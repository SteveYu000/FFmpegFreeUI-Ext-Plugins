"""验证视频帧模块的边缘分块、字节协议和分段加载；不依赖 GPU 或已安装模型。"""

from __future__ import annotations

import importlib.util
import io
import sys
import types
import unittest
from pathlib import Path
from unittest.mock import patch

import numpy as np

TOOLS = Path(__file__).resolve().parents[1] / "Backend" / "embedded-tools"


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"无法加载测试模块：{path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


frames = load_module("frame_backend_test", TOOLS / "rve-frame-backend.py")
segments = load_module("segmented_backend_test", TOOLS / "rve-segmented-backend.py")


def nearest(image, scale):
    return np.repeat(np.repeat(image, scale, axis=0), scale, axis=1)


class FrameBackendTests(unittest.TestCase):
    def test_tile_edges_and_single_pixel(self):
        for height, width in [(1, 1), (1, 5), (7, 1), (5, 7), (8, 8)]:
            image = np.arange(height * width * 3, dtype=np.uint8).reshape(height, width, 3)
            for scale in [1, 2, 4]:
                for pad in [0, 1, 3]:
                    with self.subTest(size=(height, width), scale=scale, pad=pad):
                        result = frames.tiled_rgb(lambda block: nearest(block, scale), image, scale, 4, pad)
                        np.testing.assert_array_equal(result, nearest(image, scale))

    def test_onnx_tile_wrapper(self):
        image = np.arange(5 * 7 * 3, dtype=np.uint8).reshape(5, 7, 3)
        model = types.SimpleNamespace(scale=2, _run=lambda block: nearest(block, 2))
        np.testing.assert_array_equal(frames.onnx_tiled(model, image, 4, 1), nearest(image, 2))

    def test_ncnn_frame_protocol_and_model_loading(self):
        created = []

        class FakeFrame:
            def __init__(self, backend, width, height, *args):
                self.backend, self.width, self.height = backend, width, height
                self.payload = b""

            def set_frame_bytes(self, payload):
                self.payload = payload
                return self

            def get_frame_bytes(self):
                return self.payload

        class FakeNCNN:
            def __init__(self, **options):
                created.append(options)
                self.scale = options["scale"]

            def __call__(self, frame):
                image = np.frombuffer(frame.payload, np.uint8).reshape(frame.height, frame.width, 3)
                return FakeFrame("ncnn", frame.width * self.scale, frame.height * self.scale).set_frame_bytes(
                    nearest(image, self.scale).tobytes()
                )

        ncnn_module, frame_module = types.ModuleType("src.ncnn.UpscaleNCNN"), types.ModuleType("src.utils.Frame")
        ncnn_module.UpscaleNCNN, frame_module.Frame = FakeNCNN, FakeFrame
        with patch.dict(sys.modules, {"src.ncnn.UpscaleNCNN": ncnn_module, "src.utils.Frame": frame_module}):
            image = np.arange(3 * 5 * 3, dtype=np.uint8).reshape(3, 5, 3)
            upscaler = frames.FrameUpscaler("ncnn", Path("fixture_x2.param"), 5, 3, tile_size=4)
            self.assertEqual(created[0]["modelPath"], "fixture_x2")
            self.assertEqual((created[0]["width"], created[0]["height"], created[0]["tilesize"]), (5, 3, 4))
            self.assertEqual(upscaler.process_bytes(image.tobytes()), nearest(image, 2).tobytes())
            np.testing.assert_array_equal(upscaler(image), nearest(image, 2))
            # 用户导入模型可以没有倍率名称，使用分段请求中已确认的原生倍率。
            imported = frames.FrameUpscaler("ncnn", Path("custom.param"), 5, 3, native_scale=2)
            self.assertEqual(imported.process_bytes(image.tobytes()), nearest(image, 2).tobytes())

    def test_segment_loader_uses_frame_module(self):
        upscaler_type = segments.load_frame_backend(TOOLS)
        self.assertEqual(upscaler_type.__name__, "FrameUpscaler")
        self.assertEqual(upscaler_type.__module__, "videoenhancer_frame_backend")

    def test_segment_coverage_and_output_geometry(self):
        first = dict(start=1, end=3, backend="ncnn", model="fixture_x2", scale=2, outputWidth=10, outputHeight=6)
        second = dict(first, start=4, end=6)
        self.assertEqual(segments.validate_segments([first, second], 6), (10, 6))
        for changed in [dict(second, start=5), dict(second, outputWidth=12), dict(second, end=5)]:
            with self.assertRaises(ValueError):
                segments.validate_segments([first, changed], 6)

    def test_exact_frame_read_detects_truncation(self):
        self.assertEqual(segments.read_exact(io.BytesIO(b"frame"), 5), b"frame")
        self.assertEqual(segments.read_exact(io.BytesIO(b"fram"), 5), b"fram")

    def test_unsupported_frame_backend_is_explicit(self):
        with self.assertRaisesRegex(ValueError, "不支持的帧推理后端"):
            frames.FrameUpscaler("unknown", Path("fixture_x2.param"), 5, 3)


if __name__ == "__main__":
    unittest.main()
