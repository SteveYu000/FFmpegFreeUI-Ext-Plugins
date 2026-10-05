# SPDX-License-Identifier: AGPL-3.0-only
# RVE 集成模块，修改于 2026-10-05；许可与源码范围见 LICENSING.md。
"""由 Ext 插件提供的 RVE 启动包装器；参数仍直接传给真实 RVE 后端。"""
from __future__ import annotations
import argparse
import ctypes
import os
from pathlib import Path
import runpy
import sys

# Windows 作业随 Python 根进程退出关闭，取消时不会遗留解码或编码子进程。
_job_handle = None
def install_process_job():
    global _job_handle
    if os.name != "nt":
        return
    from ctypes import wintypes
    class Basic(ctypes.Structure):
        _fields_ = [("PerProcessUserTimeLimit", ctypes.c_int64), ("PerJobUserTimeLimit", ctypes.c_int64),
                    ("LimitFlags", wintypes.DWORD), ("MinimumWorkingSetSize", ctypes.c_size_t),
                    ("MaximumWorkingSetSize", ctypes.c_size_t), ("ActiveProcessLimit", wintypes.DWORD),
                    ("Affinity", ctypes.c_size_t), ("PriorityClass", wintypes.DWORD), ("SchedulingClass", wintypes.DWORD)]
    class Io(ctypes.Structure):
        _fields_ = [(name, ctypes.c_uint64) for name in ("ReadOperationCount","WriteOperationCount","OtherOperationCount","ReadTransferCount","WriteTransferCount","OtherTransferCount")]
    class Extended(ctypes.Structure):
        _fields_ = [("BasicLimitInformation", Basic), ("IoInfo", Io), ("ProcessMemoryLimit", ctypes.c_size_t),
                    ("JobMemoryLimit", ctypes.c_size_t), ("PeakProcessMemoryUsed", ctypes.c_size_t), ("PeakJobMemoryUsed", ctypes.c_size_t)]
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateJobObjectW.restype = wintypes.HANDLE
    kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
    kernel.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
    kernel.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
    kernel.GetCurrentProcess.restype = wintypes.HANDLE
    handle = kernel.CreateJobObjectW(None, None)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    info = Extended()
    info.BasicLimitInformation.LimitFlags = 0x2000
    if not kernel.SetInformationJobObject(handle, 9, ctypes.byref(info), ctypes.sizeof(info)):
        raise ctypes.WinError(ctypes.get_last_error())
    if not kernel.AssignProcessToJobObject(handle, kernel.GetCurrentProcess()):
        raise ctypes.WinError(ctypes.get_last_error())
    _job_handle = handle

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--backend-dir", required=True)
    parser.add_argument("--work-dir", required=True)
    parser.add_argument("--target-script", required=True)
    parser.add_argument("--upscale-precision", default="auto")
    parser.add_argument("--interp-precision", default="auto")
    parser.add_argument("--process-order", default="upscale-first")
    parser.add_argument("--input-multiple", default="1")
    parser.add_argument("--ffprobe-path", required=True)
    parser.add_argument("backend_args", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    backend = Path(args.backend_dir).resolve()
    work = Path(args.work_dir).resolve()
    cache = work.parent.parent / "cache"
    temporary = work / "tmp"
    temporary.mkdir(parents=True, exist_ok=True)
    for key in ("TEMP", "TMP"):
        os.environ[key] = str(temporary)
    os.environ["PYTHONUTF8"] = "1"
    os.environ["PYTHONIOENCODING"] = "utf-8"
    os.environ["VIDEOENHANCER_BACKEND_DIR"] = str(backend)
    os.environ["VIDEOENHANCER_WORK_DIR"] = str(work)
    os.environ["VIDEOENHANCER_FFPROBE_PATH"] = args.ffprobe_path
    os.environ["VIDEOENHANCER_UPSCALE_PRECISION"] = args.upscale_precision
    os.environ["VIDEOENHANCER_INTERP_PRECISION"] = args.interp_precision
    os.environ["VIDEOENHANCER_PROCESS_ORDER"] = args.process_order
    os.environ["VIDEOENHANCER_UPSCALE_INPUT_MULTIPLE"] = args.input_multiple
    os.environ["VIDEOENHANCER_ONNX_INPUT_MULTIPLE"] = args.input_multiple
    for key, suffix in (("XDG_CACHE_HOME",""),("HF_HOME","huggingface"),("TORCH_HOME","torch"),("CUDA_CACHE_PATH","cuda"),("PYTHONPYCACHEPREFIX","pycache"),("NUMBA_CACHE_DIR","numba"),("MPLCONFIGDIR","matplotlib")):
        os.environ[key] = str(cache / suffix)
    if str(backend) not in sys.path:
        sys.path.insert(0, str(backend))
    install_process_job()
    forwarded = args.backend_args
    if forwarded and forwarded[0] == "--":
        forwarded = forwarded[1:]
    sys.argv = [args.target_script, *forwarded]
    runpy.run_path(args.target_script, run_name="__main__")

if __name__ == "__main__":
    main()
