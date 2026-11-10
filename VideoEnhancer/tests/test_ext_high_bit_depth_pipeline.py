"""执行 Ext 实际生成的 FFmpeg/RVE 计划，核验 RGB48 普通与分段输出。"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
BACKEND = os.environ.get("VIDEOENHANCER_TEST_RVE_BACKEND")
FFMPEG = os.environ.get("VIDEOENHANCER_TEST_FFMPEG")
FFPROBE = os.environ.get("VIDEOENHANCER_TEST_FFPROBE")
FIXTURE = ROOT / "tests/bin/Release/net10.0-windows10.0.17763.0/VideoEnhancer.Tests.exe"


@unittest.skipUnless(BACKEND and FFMPEG and FFPROBE and FIXTURE.is_file(),
                     "需要 RVE 参考目录、FFmpeg/FFprobe 及已构建的 C# 测试程序")
class ExtHighBitDepthPipelineTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import numpy as np
        from test_onnx_tensorrt import make_model
        cls.np = np
        cls.temp = tempfile.TemporaryDirectory(prefix="VE-RGB48-", dir=ROOT / ".test-tools")
        cls.addClassCleanup(cls.temp.cleanup)
        cls.runtime = Path(cls.temp.name)
        cls.backend = cls.runtime / "python/backend"
        shutil.copytree(BACKEND, cls.backend,
                        ignore=shutil.ignore_patterns("__pycache__", "*.pyc", "*.log", "*.engine", "*.pth"))
        # 仅在测试副本声明 Ext 所有权，让实际 DLL 同步本次移植的核心。
        (cls.backend / ".videoenhancer-rgb48-owner").write_text("ext\n", encoding="utf-8")
        subprocess.run([str(FIXTURE), "--prepare-rgb48-backend", str(cls.runtime)], check=True,
                       capture_output=True, text=True, encoding="utf-8")
        cls.model = cls.runtime / "models/ONNX/fixture-2x.onnx"
        cls.model.parent.mkdir(parents=True)
        make_model(cls.model, shape=(1, 3, "height", "width"))
        source = np.rint(np.linspace(0, 65535, 4096)).astype(np.uint16).reshape(64, 64, 1).repeat(3, axis=2)
        cls.source = np.stack((source, np.flip(source, 0).copy()))
        raw = cls.runtime / "samples.rgb"
        raw.write_bytes(cls.source.astype("<u2").tobytes())
        cls.input = cls.runtime / "input.mkv"
        cls.run_command([FFMPEG, "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo",
                         "-pix_fmt", "rgb48le", "-s:v", "64x64", "-r", "25", "-i", str(raw),
                         "-c:v", "ffv1", "-level", "3", "-pix_fmt", "gbrp16le", str(cls.input)])

    @staticmethod
    def run_command(command):
        result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace")
        if result.returncode:
            raise AssertionError(f"命令失败 {result.returncode}: {command}\n{result.stdout}\n{result.stderr}")
        return result.stdout

    def execute(self, name, settings):
        work = self.runtime / name
        work.mkdir()
        preset = work / "settings.json"
        preset.write_text(json.dumps(settings), encoding="utf-8")
        plan = json.loads(self.run_command([str(FIXTURE), "--export-rgb48-plan", str(self.runtime),
                                           str(preset), str(self.input), str(work), FFMPEG, FFPROBE]))
        for step in plan:
            process = step["ProcessFileName"]
            # 隔离副本只拷贝 RVE 源码，推理仍用当前已安装依赖的解释器。
            if process.lower().endswith("python.exe"):
                process = sys.executable
            self.run_command([process, *step["Arguments"]])
        output = work / "enhanced-0.mkv"
        info = json.loads(self.run_command([FFPROBE, "-v", "error", "-show_streams", "-of", "json", str(output)]))
        self.assertEqual(len(info["streams"]), 1)
        stream = info["streams"][0]
        self.assertEqual((stream["width"], stream["height"], stream["pix_fmt"]), (128, 128, "gbrp16le"))
        decoded = subprocess.run([FFMPEG, "-v", "error", "-i", str(output), "-map", "0:v:0", "-f", "rawvideo",
                                  "-pix_fmt", "rgb48le", "pipe:1"], check=True, capture_output=True).stdout
        actual = self.np.frombuffer(decoded, dtype="<u2").reshape(2, 128, 128, 3)
        self.np.testing.assert_array_equal(actual, self.source.repeat(2, axis=1).repeat(2, axis=2))
        self.assertEqual(self.np.unique(actual[0, :, :, 0]).size, 4096)
        return plan

    def settings(self, segments=None):
        if segments is None:
            return dict(UpscaleEnabled=True, Backend="onnx", Model=str(self.model),
                        UpscaleHalfPrecision=False, UpscaleTileSize=16)
        return dict(SegmentedEnabled=True, UpscaleHalfPrecision=False, UpscaleTileSize=16,
                    SegmentedVideos=[dict(Path=str(self.input), Enabled=True, SourceWidth=64, SourceHeight=64,
                                          FrameCount=2, DurationSeconds=2/25, Segments=segments)])

    def test_regular_onnx_plan_preserves_4096_levels(self):
        plan = self.execute("regular", self.settings())
        self.assertTrue(any("--target-script" in step["Arguments"] for step in plan))
        self.assertTrue(all("--hdr" not in step["Arguments"] for step in plan))

    def test_segmented_onnx_plan_preserves_4096_levels(self):
        self.execute("model-segments", self.settings([
            dict(Start=i, End=i, Backend="onnx", Model=str(self.model)) for i in (1, 2)]))

    def test_mixed_onnx_nearest_segments_preserve_4096_levels(self):
        probe = self.run_command([FFMPEG, "-hide_banner", "-h", "filter=zscale"])
        if "zscale" not in probe or "Unknown filter" in probe or "not available" in probe:
            self.skipTest("本机 FFmpeg 未包含 zscale")
        self.execute("mixed-segments", self.settings([
            dict(Start=1, End=1, Backend="onnx", Model=str(self.model)),
            dict(Start=2, End=2, Backend="ffmpeg", Model="neighbor", TargetWidth=128, TargetHeight=128)]))


if __name__ == "__main__":
    unittest.main()
