# RVE RGB48 核心（Ext 集成）

来源：[maxzrb/VideoEnhancer](https://github.com/maxzrb/VideoEnhancer/tree/83c5f1235c2bfbf43fe0f1fa4c01f03712d48e1b/cli/third-party/RVE/high-bit-depth)，移植日期：2026-11-10，配套 RVE 基线为 2.4.1-dev17。16 个 Python 文件沿用 RVE 的 AGPL-3.0 许可，原始正文见上级 LICENSE，来源见上级 SOURCE.txt。上游修订基于 RVE-high-bit-depth-20261108.zip，SHA-256 为 e72eb55982e764865986121f3438aae0e26cde89e34a11d3e4a4e02a4c18fdd2。

本目录源码内嵌到插件 DLL；配套入口位于 Backend/embedded-tools。SDR/HDR 主帧统一为 RGB48 / uint16；预览、转场检测旁路的 8bit 数据不写回主帧。保留 Ext 的 Engine 中文路径文件流加载修复。FP32 为默认，半精度开关显式选择 float16；TensorRT 的 GRL、DAT2、AniToon-RPLKSRL 使用 FP32，并通过缓存指纹隔离旧精度/TF32 策略的 Engine。旧缓存不删除。

RveHighBitDepthSupport 仅为匹配基线且归本插件管理的后端按哈希同步，所有权标记 .videoenhancer-rgb48-owner 的值为 ext。独立包已提供 PixelFormat.py 时验证 rgb48-v1 协议及配套入口，保留其 Python 文件，顺序/分段入口也使用独立包文件。更新失败会阻止推理，避免 8bit 与 16bit 协议混用。独立包覆盖安装可通过 external 标记声明脚本所有权。

Ext 的分段模块使用 rve-frame-backend.py，不增加独立图片页面或图片 CLI。中间文件与模型/自定义分段统一为 gbrp16le；最近邻使用 zscale point 保留低位，要求 FFmpeg 含 zscale。最终编码、音频、字幕、章节和元数据仍由宿主预设决定。NCNN RIFE 的字节接口仅 8bit，明确拒绝并提示改选 CUDA 或 TensorRT。RGB48 不代表所有 HDR 色彩路径均已验证，也不能提高固定 FP16 模型的内部有效精度。

测试入口为 tests/test_high_bit_depth_regressions.py、tests/test_ext_high_bit_depth_pipeline.py 及 C# 的 --upstream-1317-tests；运行环境和验证结果以 PROJECT_STATE.md 为准。本目录源码只随对应源码 ZIP 提供；安装包中不复制这些 Python 源文件。
