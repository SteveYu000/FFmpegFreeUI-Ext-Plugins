"""验证 ONNX 候选约束；可选在真实 CUDA/TensorRT 上构建并加载 Engine。"""
import contextlib
import importlib.util
import io
import os
import shutil
import subprocess
import sys
import tempfile
import types
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
HAS_ONNX = all(importlib.util.find_spec(name) for name in ("onnx", "onnxruntime", "numpy"))


def load_script(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def make_model(path, shape=(1, 3, 8, 8), scale=2, outputs=1, multiple=1, half=False):
    import numpy as np
    import onnx
    from onnx import helper, numpy_helper
    sizes = [value * scale if index >= 2 and isinstance(value, int) else value
             for index, value in enumerate(shape)]
    names = ["output"] if outputs == 1 else ["output", "second_output"]
    nodes = [helper.make_node("Resize", ["input", "", "scales"], [names[0]],
                              mode="nearest", coordinate_transformation_mode="asymmetric", nearest_mode="floor")]
    if outputs > 1:
        nodes.append(helper.make_node("Identity", [names[0]], [names[1]]))
    tensor_type = onnx.TensorProto.FLOAT16 if half else onnx.TensorProto.FLOAT
    graph = helper.make_graph(
        nodes, "test_upscale",
        [helper.make_tensor_value_info("input", tensor_type, shape)],
        [helper.make_tensor_value_info(name, tensor_type, sizes) for name in names],
        [numpy_helper.from_array(np.array([1, 1, scale, scale], dtype=np.float32), "scales")],
    )
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 18)])
    model.ir_version = 9
    model.metadata_props.add(key="input_multiple", value=str(multiple))
    onnx.checker.check_model(model)
    onnx.save(model, path)
    return model


@unittest.skipUnless(HAS_ONNX, "需要 ONNX / ONNX Runtime / NumPy")
class OnnxCandidateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.inspector = load_script("test_onnx_inspector", ROOT / "Backend/embedded-tools/inspect_upscale_models.py")

    def test_single_rgb_model_keeps_both_backends(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "模型.onnx"
            make_model(path, multiple=16)
            result = self.inspector.inspect_model(str(path))
            self.assertEqual("", result["error"])
            self.assertEqual(["onnx", "tensorrt"], result["backends"])
            self.assertEqual(16, result["input_multiple"])

    def test_fp16_model_is_a_tensor_rt_candidate(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "half.onnx"
            make_model(path, half=True)
            result = self.inspector.inspect_model(str(path))
            self.assertEqual("", result["error"])
            self.assertEqual(["onnx", "tensorrt"], result["backends"])

    def test_multiple_outputs_are_not_tensor_rt_candidates(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "multiple.onnx"
            make_model(path, outputs=2)
            result = self.inspector.inspect_model(str(path))
            self.assertEqual("", result["error"])
            self.assertEqual(["onnx"], result["backends"])

    def test_batch_or_non_rgb_models_are_not_tensor_rt_candidates(self):
        for shape in ((2, 3, 8, 8), (1, 1, 8, 8)):
            with self.subTest(shape=shape), tempfile.TemporaryDirectory() as folder:
                path = Path(folder) / "unsupported.onnx"
                make_model(path, shape=shape)
                result = self.inspector.inspect_model(str(path))
                self.assertNotIn("tensorrt", result["backends"])

    def test_dynamic_model_reports_integer_scale(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "dynamic.onnx"
            make_model(path, shape=(1, 3, "height", "width"))
            result = self.inspector.inspect_model(str(path))
            self.assertEqual("", result["error"])
            self.assertEqual(2, result["scale"])
            self.assertIn("tensorrt", result["backends"])


@unittest.skipUnless(os.environ.get("VIDEOENHANCER_TEST_TRT") == "1",
                     "设置 VIDEOENHANCER_TEST_TRT=1 才运行真实 GPU 编译")
class OnnxTensorRtGpuTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import torch
        if not HAS_ONNX or not torch.cuda.is_available():
            raise unittest.SkipTest("需要 ONNX 和 CUDA GPU")
        cls.torch = torch
        backend = os.environ.get("VIDEOENHANCER_TEST_RVE_BACKEND")
        if backend:
            sys.path.insert(0, backend)
        cls.converter = load_script("test_onnx_converter", ROOT / "Backend/embedded-tools/convert_tensorrt.py")
        # 在隔离副本应用 DLL 的实际补丁，保留用户安装目录不变。
        if not backend:
            raise unittest.SkipTest("需要 VIDEOENHANCER_TEST_RVE_BACKEND")
        cls.fixture = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.fixture.cleanup)
        patched = Path(cls.fixture.name) / "python/backend/src/pytorch/UpscaleTorch.py"
        patched.parent.mkdir(parents=True)
        shutil.copyfile(Path(backend) / "src/pytorch/UpscaleTorch.py", patched)
        executable = ROOT / "tests/bin/Release/net11.0-windows10.0.17763.0/VideoEnhancer.Tests.exe"
        subprocess.run([str(executable), "--patch-trt-loader", cls.fixture.name], check=True)
        module = load_script("src.pytorch.ExtPatchedUpscaleTorch", patched)
        cls.rve_engine = module._PrebuiltTensorRTEngine

    def convert(self, folder, shape, scale=2, options=(), half=False):
        path = Path(folder) / "测试 模型.onnx"
        make_model(path, shape=shape, scale=scale, half=half)
        with patch.object(sys, "argv", ["convert_tensorrt.py", str(path), "--output-dir", folder,
                                       "--width", "35", "--height", "17", "--precision", "fp32",
                                       "--input-multiple", "16", *options]):
            with contextlib.redirect_stdout(io.StringIO()) as log:
                self.converter.main()
        self.assertIn("VIDEOENHANCER_TRT_PROGRESS|TensorRT Engine|100", log.getvalue())
        return next(Path(folder).glob("*.engine"))

    def check_engine(self, path, shape, scale, dtype=None):
        # 使用 RVE 已安装的加载器检查 TorchScript 封装和元数据，而非直接加载原生 plan。
        engine = self.rve_engine(str(path), self.torch.device("cuda:0"), self.torch.float32)
        with path.open("rb") as stream:
            model = self.torch.jit.load(stream, map_location="cuda").eval()
        value = self.torch.rand(shape, device="cuda", dtype=dtype or self.torch.float32)
        with self.torch.no_grad():
            output = model(value)
        self.assertEqual(tuple(output.shape), (1, 3, shape[2] * scale, shape[3] * scale))
        self.assertEqual(engine.get_scale(), scale)
        expected = self.torch.nn.functional.interpolate(value, scale_factor=scale, mode="nearest")
        self.torch.testing.assert_close(output, expected, atol=1e-5, rtol=1e-5)

    def test_dynamic_profile_rounds_to_input_multiple(self):
        with tempfile.TemporaryDirectory() as folder:
            path = self.convert(folder, (1, 3, "height", "width"))
            self.check_engine(path, (1, 3, 32, 48), 2)

    def test_fp16_model_preserves_input_precision(self):
        with tempfile.TemporaryDirectory() as folder:
            path = self.convert(folder, (1, 3, "height", "width"), half=True)
            self.check_engine(path, (1, 3, 32, 48), 2, self.torch.float16)
            # 旧 FP16 Engine 即使文件仍存在，也不能供已知不稳定模型继续使用。
            legacy = path.with_name("AniSD-DAT2-fp16-2x.engine")
            shutil.copyfile(path, legacy)
            with self.assertRaisesRegex(ValueError, "FP32"):
                self.rve_engine(str(legacy), self.torch.device("cuda:0"), self.torch.float32)

    def test_fixed_profile_is_kept(self):
        with tempfile.TemporaryDirectory() as folder:
            path = self.convert(folder, (1, 3, 24, 32))
            self.check_engine(path, (1, 3, 24, 32), 2)

    def test_tiled_profile_aligns_padding(self):
        with tempfile.TemporaryDirectory() as folder:
            path = self.convert(folder, (1, 3, "height", "width"),
                                options=("--width", "640", "--height", "480", "--tile-size", "16"))
            self.check_engine(path, (1, 3, 48, 48), 2)

    def test_lower_scale_stays_in_engine_graph(self):
        with tempfile.TemporaryDirectory() as folder:
            path = self.convert(folder, (1, 3, 16, 16), scale=4, options=("--output-scale", "2"))
            engine = self.rve_engine(str(path), self.torch.device("cuda:0"), self.torch.float32)
            value = self.torch.rand((1, 3, 16, 16), device="cuda")
            with path.open("rb") as stream:
                model = self.torch.jit.load(stream, map_location="cuda").eval()
            output = model(value)
            expected = self.torch.nn.functional.interpolate(
                self.torch.nn.functional.interpolate(value, scale_factor=4, mode="nearest"),
                size=(32, 32), mode="bicubic", align_corners=False)
            self.assertEqual(engine.get_scale(), 2)
            self.torch.testing.assert_close(output, expected, atol=2e-5, rtol=2e-5)

    def test_external_weights_are_rejected_before_build(self):
        import onnx
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / "external.onnx"
            model = make_model(source)
            onnx.save_model(model, source, save_as_external_data=True,
                            all_tensors_to_one_file=True, location="weights.bin", size_threshold=0)
            args = types.SimpleNamespace(input_multiple=1)
            with self.assertRaisesRegex(ValueError, "self-contained"):
                self.converter.convert_onnx(args, str(source), self.torch.device("cuda:0"))

    def test_multiple_outputs_fail_without_backend_fallback(self):
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / "multiple.onnx"
            make_model(source, outputs=2)
            with self.assertRaisesRegex(ValueError, "one input and one output"):
                self.converter.convert_onnx(types.SimpleNamespace(input_multiple=1),
                                            str(source), self.torch.device("cuda:0"))


if __name__ == "__main__":
    unittest.main()
