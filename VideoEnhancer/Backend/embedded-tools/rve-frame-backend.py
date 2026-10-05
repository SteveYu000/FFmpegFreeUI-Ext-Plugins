# SPDX-License-Identifier: AGPL-3.0-only
# RVE 集成模块，修改于 2026-10-05；许可与源码范围见 LICENSING.md。
"""分段视频使用的单帧超分模块；不提供图片批处理或命令行入口。"""

from __future__ import annotations

import os
import re
import sys
from pathlib import Path

import numpy as np

BACKEND_ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(BACKEND_ROOT))


def model_scale(path: Path) -> int:
    if path.is_dir() and all((path / name).is_file() for name in (
        "diffusion_pytorch_model_streaming_dmd.safetensors", "LQ_proj_in.ckpt", "TCDecoder.ckpt", "Wan2.1_VAE.pth")):
        return 4
    if path.is_dir() and (path / "config.py").is_file() and (path / "chkpts.pth").is_file():
        return 1
    if path.is_file() and "basicvsr" in path.name.lower() and path.suffix.lower() == ".pth":
        return 4
    name = path.stem.lower()
    match = re.search(r"(?:^|[_-])(\d+)x(?:[_-]|$)|(?:^|[_-])x(\d+)(?:[_-]|$)", name)
    if match:
        return int(match.group(1) or match.group(2))
    if path.is_dir():
        param = next(path.glob("*.param"), None)
        if param:
            text = param.read_text(encoding="utf-8", errors="ignore")
            match = re.search(r"\bPixelShuffle\b[^\r\n]*\b0=(\d+)", text)
            if match:
                return int(match.group(1))
    raise ValueError(f"无法从模型名称识别倍率，请在名称中包含 x2/x3/x4：{path.name}")


def reflect_pad(image: np.ndarray, tile: int, pad: int) -> tuple[np.ndarray, int, int]:
    height, width = image.shape[:2]
    extra_h = (tile - height % tile) % tile
    extra_w = (tile - width % tile) % tile
    mode = "reflect" if height > 1 and width > 1 else "edge"
    return np.pad(image, ((pad, extra_h + pad), (pad, extra_w + pad), (0, 0)), mode=mode), extra_h, extra_w


def tiled_rgb(run_block, image: np.ndarray, scale: int, tile: int, pad: int) -> np.ndarray:
    padded, extra_h, extra_w = reflect_pad(image, tile, pad)
    height, width = padded.shape[:2]
    output = np.zeros((height * scale, width * scale, 3), dtype=np.uint8)
    tiles_x = (width - 2 * pad + tile - 1) // tile
    tiles_y = (height - 2 * pad + tile - 1) // tile
    for y in range(tiles_y):
        for x in range(tiles_x):
            x0, y0 = x * tile + pad, y * tile + pad
            x1, y1 = min(x0 + tile, width - pad), min(y0 + tile, height - pad)
            px0, py0, px1, py1 = x0 - pad, y0 - pad, x1 + pad, y1 + pad
            block = padded[py0:py1, px0:px1]
            block_out = run_block(block)
            loss_x = block.shape[1] * scale - block_out.shape[1]
            loss_y = block.shape[0] * scale - block_out.shape[0]
            crop_x = (x0 - px0) * scale - max(0, loss_x // 2)
            crop_y = (y0 - py0) * scale - max(0, loss_y // 2)
            output[y0*scale:y1*scale, x0*scale:x1*scale] = block_out[
                crop_y:crop_y+(y1-y0)*scale, crop_x:crop_x+(x1-x0)*scale
            ]
    top = left = pad * scale
    return output[top:output.shape[0]-(extra_h+pad)*scale,
                  left:output.shape[1]-(extra_w+pad)*scale]


def onnx_tiled(upscaler, image: np.ndarray, tile: int = 256, pad: int = 8) -> np.ndarray:
    return tiled_rgb(upscaler._run, image, int(upscaler.scale), tile, pad)


class FrameUpscaler:
    def __init__(
        self,
        backend: str,
        model: Path,
        width: int,
        height: int,
        tile_size: int = 0,
        native_scale: int = 0,
    ):
        self.backend, self.model_path = backend, model
        self.width, self.height = width, height
        self.tile_size = max(0, int(tile_size))
        try:
            self.scale = native_scale or model_scale(model)
        except ValueError:
            if backend != "cuda":
                raise
            self.scale = 0
        self.model = self._create()
        if backend in ("cuda", "tensorrt"):
            self.scale = int(self.model.getScale())

    def _create(self):
        if self.backend == "onnx":
            from src.onnx.UpscaleONNX import UpscaleONNX
            return UpscaleONNX(str(self.model_path), device="default", width=self.width,
                               height=self.height, scale=self.scale, tilesize=256, tile_pad=8)
        if self.backend in ("cuda", "tensorrt"):
            import torch
            from src.pytorch.UpscaleTorch import UpscalePytorch
            model = UpscalePytorch(str(self.model_path), device="cuda",
                                   precision=os.environ.get("VIDEOENHANCER_UPSCALE_PRECISION", "auto"),
                                   width=self.width, height=self.height, tilesize=256, tile_pad=10,
                                   backend="pytorch" if self.backend == "cuda" else "tensorrt")
            actual_dtype = model.dtype
            if self.backend == "cuda":
                try:
                    actual_dtype = next(model.upscale_model_wrapper.get_model().parameters()).dtype
                    model.dtype = actual_dtype
                except (AttributeError, StopIteration):
                    pass
            self.frame_precision = "float32" if actual_dtype == torch.float32 else "float16"
            return model
        if self.backend == "ncnn":
            model = next(self.model_path.glob("*.param")).with_suffix("") if self.model_path.is_dir() else self.model_path.with_suffix("")
            from src.ncnn.UpscaleNCNN import UpscaleNCNN
            return UpscaleNCNN(
                modelPath=str(model),
                num_threads=1,
                scale=self.scale,
                gpuid=0,
                width=self.width,
                height=self.height,
                tilesize=self.tile_size,
            )
        raise ValueError(f"不支持的帧推理后端：{self.backend}")

    def __call__(self, rgb: np.ndarray) -> np.ndarray:
        if self.backend == "onnx":
            return onnx_tiled(self.model, rgb)
        if self.backend == "ncnn":
            return np.frombuffer(self.process_bytes(rgb.tobytes()), dtype=np.uint8).reshape(
                self.height * self.scale, self.width * self.scale, 3
            )
        from src.utils.Frame import Frame
        internal = "pytorch" if self.backend == "cuda" else self.backend
        frame = Frame(internal, self.width, self.height, "cuda", 0, False, self.frame_precision).set_frame_bytes(rgb.tobytes())
        result = self.model(frame)
        return np.frombuffer(result.get_frame_bytes(), dtype=np.uint8).reshape(
            self.height * self.scale, self.width * self.scale, 3)

    def process_bytes(self, payload: bytes) -> bytes:
        if self.backend != "ncnn":
            raise ValueError("process_bytes 仅用于分段视频的 RVE NCNN 优化路径")
        from src.utils.Frame import Frame
        frame = Frame(
            "ncnn", self.width, self.height, "cuda", 0, False, "float16"
        ).set_frame_bytes(payload)
        return self.model(frame).get_frame_bytes()
