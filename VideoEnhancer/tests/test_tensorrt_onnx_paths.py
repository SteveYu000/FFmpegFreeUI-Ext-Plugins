"""验证 ONNX 中文路径的内存解析、错误提示及外置权重拒绝，无需 GPU 依赖。"""
import ast
import contextlib
import io
import sys
import types
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

CONVERTER = Path(__file__).resolve().parents[1] / "Backend/embedded-tools/convert_tensorrt.py"


class StopAfterParse(Exception):
    pass


class OnnxPathParsingTests(unittest.TestCase):
    def setUp(self):
        # 只执行解析入口，常规回归不依赖 CUDA、PyTorch 或 TensorRT。
        tree = ast.parse(CONVERTER.read_text(encoding="utf-8-sig"))
        function = next(node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "convert_onnx")
        namespace = {}
        exec(compile(ast.Module(body=[function], type_ignores=[]), str(CONVERTER), "exec"), namespace)
        self.convert = namespace["convert_onnx"]
        self.model = MagicMock()
        self.model.graph.initializer = []
        self.model.metadata_props = []
        self.payload = b"\x08\x09\x00onnx-payload"
        self.model.SerializeToString.return_value = self.payload
        self.onnx = types.ModuleType("onnx")
        self.onnx.load = MagicMock(return_value=self.model)
        self.onnx.TensorProto = types.SimpleNamespace(EXTERNAL=1)
        self.parser = MagicMock()
        self.parser.parse_from_file.side_effect = AssertionError("不得把中文路径交给原生文件接口")
        self.network = MagicMock()
        self.network.get_input.side_effect = StopAfterParse
        builder = MagicMock()
        builder.create_network.return_value = self.network
        trt = types.ModuleType("tensorrt")
        trt.Logger = MagicMock()
        trt.Logger.WARNING = 0
        trt.Builder = MagicMock(return_value=builder)
        trt.NetworkDefinitionCreationFlag = types.SimpleNamespace(EXPLICIT_BATCH=0)
        trt.OnnxParser = MagicMock(return_value=self.parser)
        runtime = types.ModuleType("torch_tensorrt.dynamo.runtime")
        runtime.TorchTensorRTModule = MagicMock()
        self.modules = patch.dict(sys.modules, {
            "onnx": self.onnx, "tensorrt": trt,
            "torch_tensorrt": types.ModuleType("torch_tensorrt"),
            "torch_tensorrt.dynamo": types.ModuleType("torch_tensorrt.dynamo"),
            "torch_tensorrt.dynamo.runtime": runtime,
        })
        self.modules.start()
        self.addCleanup(self.modules.stop)
        self.args = types.SimpleNamespace(input_multiple=1)

    def test_unicode_path_is_loaded_by_onnx_and_parsed_as_bytes(self):
        source = str(Path("中文 目录") / "超分 模型.onnx")
        self.parser.parse.return_value = True
        self.network.num_inputs = self.network.num_outputs = 1
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(StopAfterParse):
            self.convert(self.args, source, None)
        self.onnx.load.assert_called_once_with(source, load_external_data=False)
        self.parser.parse.assert_called_once_with(self.payload)
        self.parser.parse_from_file.assert_not_called()

    def test_parser_errors_are_still_reported(self):
        self.parser.parse.return_value = False
        self.parser.num_errors = 1
        self.parser.get_error.return_value = "unsupported test operator"
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaisesRegex(
                ValueError, "TensorRT ONNX parse failed: unsupported test operator"):
            self.convert(self.args, "模型.onnx", None)

    def test_external_weights_are_rejected_before_serializing_or_parsing(self):
        self.model.graph.initializer = [types.SimpleNamespace(data_location=1)]
        with self.assertRaisesRegex(ValueError, "self-contained ONNX"):
            self.convert(self.args, "外置权重.onnx", None)
        self.model.SerializeToString.assert_not_called()
        self.parser.parse.assert_not_called()


if __name__ == "__main__":
    unittest.main()
