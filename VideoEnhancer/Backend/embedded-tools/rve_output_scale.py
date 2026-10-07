# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 VideoEnhancer contributors
# Ext 移植与许可导航调整：2026-10-07。
# 本脚本与 RVE 结合运行；许可与源码范围见 LICENSING.md。
"""在超分结果进入补帧或编码前落实目标尺寸。"""

import os
from functools import lru_cache


@lru_cache(maxsize=32)
def _lanczos_axis(source, target, device):
    import torch
    # 半像素坐标、四瓣 Lanczos 和边缘复制，与 CPU Lanczos4 的采样规则一致。
    position = (torch.arange(target, device=device, dtype=torch.float32) + 0.5) * (source / target) - 0.5
    indices = position.floor().long()[:, None] + torch.arange(-3, 5, device=device)[None, :]
    distance = position[:, None] - indices
    weights = torch.sinc(distance) * torch.sinc(distance / 4)
    weights = weights.masked_fill(distance.abs() >= 4, 0)
    weights = weights / weights.sum(dim=1, keepdim=True)
    return indices.clamp(0, source - 1), weights


def _resize_axis(tensor, target, axis):
    import torch
    source = tensor.shape[axis]
    if source == target:
        return tensor
    indices, weights = _lanczos_axis(source, target, str(tensor.device))
    shape = list(tensor.shape)
    shape[axis] = target
    output = torch.zeros(shape, device=tensor.device, dtype=torch.float32)
    weight_shape = [1] * tensor.ndim
    weight_shape[axis] = target
    # 逐瓣累加，避免一次展开八份完整帧占用显存。
    for tap in range(8):
        sample = tensor.index_select(axis, indices[:, tap])
        output.addcmul_(sample, weights[:, tap].reshape(weight_shape))
    return output


def resize_tensor(tensor, width, height):
    if tuple(tensor.shape[-2:]) == (height, width):
        return tensor
    # 两个可分离方向均在原设备计算，缩小后才进入回传或后续 GPU 补帧。
    output = tensor.float()
    # 先处理缩小比例更大的方向，减少第二次采样的工作量。
    axes = sorted(((width, -1), (height, -2)), key=lambda item: item[0] / tensor.shape[item[1]])
    for target, axis in axes:
        output = _resize_axis(output, target, axis)
    return output.to(dtype=tensor.dtype)


def resize_array(array, width, height):
    if tuple(array.shape[:2]) == (height, width):
        return array
    import cv2
    return cv2.resize(array, (width, height), interpolation=cv2.INTER_LANCZOS4)


def resize_frame(frame, width, height):
    if (frame.width, frame.height) == (width, height):
        return frame
    tensor = getattr(frame, "_tensor", None)
    if tensor is not None:
        frame.set_frame_tensor(resize_tensor(tensor, width, height))
    else:
        frame.set_frame_np(resize_array(frame.get_frame_np(), width, height))
    frame.width, frame.height = width, height
    return frame


def temporal_target(frames):
    scale = int(os.environ.get("VIDEOENHANCER_OUTPUT_SCALE", "0"))
    return (frames.shape[2] * scale, frames.shape[1] * scale) if scale else None
