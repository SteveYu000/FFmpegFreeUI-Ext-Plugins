# VideoEnhancer 当前状态

更新日期：2026-11-10。交接快照，以源码、Git 和实际验证结果为准。

## 当前发布工作

用户要求“更新版本为0.4并发布”，按现有三段版本、更新器识别和资产命名统一为 0.4.0。本轮已获得提交、推送和正式 GitHub Release 发布授权，无须再次请求确认。

- 目标目录：FFmpegFreeUI-Ext-Plugins/VideoEnhancer；远程 SteveYu000/FFmpegFreeUI-Ext-Plugins，分支 main。
- 上一批提交 3312b14 已完成 RGB48 / TensorRT 移植并保持 LakeUI 5.117；此次把尚未提交的标题、按钮和底线修改纳入 0.4.0。
- 前后端项目、教程、README、安装说明和许可范围标题统一 0.4.0。最低 LakeUI 仍为 5.117.0，Ext SDK 2.5.0，目标 .NET 10。
- 即将提交版本和 UI 修改，再从该代码提交构建、验证安装/对应源码两包，推送 main 和 VideoEnhancer/v0.4.0 标签，创建正式发布。
- 当前尚未构建或上传 0.4.0，不把旧 dist 文件或测试 ZIP 当作新版本。发布完成后本文件需用实际提交、源码身份、资产及远程结果更新。

## 发布内容与边界

已移植上游 v1.3.15 至 v1.3.17 及 main 83c5f12 的 Ext 相关内容：ONNX 到 TensorRT 自动/手动转换、模型尺寸约束、RGB48 / uint16 主帧、gbrp16le 中间视频、FP32 默认与显式半精度、TensorRT 不稳定模型精度限制和缓存隔离、中文路径、独立后端所有权、后端下载去重及 HDR 保存回滚。

本次 UI：两个标题复用“模型资源库”的 13 号组件；模型管理刷新/移除按钮位于标题右侧，尺寸与资源库一致；AI 增强页底线和无用创建函数移除。没有新增兼容层或采用 LakeUI 5.201 专有 API。

最终编码、音频、字幕、附件、章节、元数据和后处理仍由宿主预设控制；不恢复 CLI、独立图片/右键模块或 EXE 安装器。安装和源码 ZIP 分开发布，各附 SHA-256；不改其他插件的版本或旧 Release。

## 既有验证与本轮待办

此前同日：0.3.0 UI 完整 C# 回归 513 项通过、构建 0 警告 0 错误；RVE 相关 Python 43 项加帧模块 7 项均通过，含 RTX 5060 小模型真实 TensorRT 构建/推理；早期双包检查 247 项通过。它们不是 0.4.0 新产物验证结果。

0.4.0 需执行 locked restore / Release build、现有 C# 回归、双包许可/源码一致性及 ZIP 完整性校验，再核验发布标签和四个远程资产。没有真实窗口截图复测：本轮工具列表不含 Computer Use 所需 node_repl；不能把逻辑布局检查当作视觉验收。

本机线索：宿主 6.3.0.1 / SDK 2.5.0 / LakeUI 5.117，输出目录为 E:/DesktopPlus/Works/Code/FFmpegFreeUI-API-Extended-Edition/FFmpegFreeUI/bin/Release/net10.0-windows10.0.26100.0。FFmpeg/FFprobe 位于 C:/Apps/ffmpeg/，通过 VIDEOENHANCER_TEST_FFMPEG / VIDEOENHANCER_TEST_FFPROBE 指定，宿主目录使用 VIDEOENHANCER_TEST_HOST。GPU 验证使用隔离 RVE 副本，不覆盖用户安装。

## 尚未解决的问题

### ONNX 图片超分显存不足

已证实：RealHatGAN-JP-Illustration-2x-fix1，输入 1500×1061，FP32，UpscaleTileSize=0 整帧推理时，ONNX Runtime 请求单个 9,905,897,472 字节（约 9.23 GiB）缓冲区失败；用户显卡为 RTX 5060、8 GB。报错发生在模型推理，FFmpeg 写命令的红字本身不是失败依据。

已建议分块 256，仍不足则 128。固定 FP32 ONNX 不会因勾选半精度自动变成 FP16。本轮没有实现自动 OOM 降级，也未复测该大模型成功；RGB48 与小模型验证不能把此问题标记为已修复。

### Rendering 后日志可见但无法复制

用户反馈：“复制当前视图”缺少 BACKEND: Rendering 之后日志，选中文字 Ctrl+C 也无法复制。截图显示后续 ONNX 异常。问题未定位，本轮未改宿主或插件复制实现。

此前检查：宿主复制读取控件 .Text；LakeUI 5.112 的 getter 与 AppendLine 使用同一行集合，尚无缓存未同步证据。ANSI、控制字符和粘贴目标影响均未证实；只读剪贴板检查未捕获目标日志。此前 Computer Use native pipe 不可用，未完成真实窗口复制测试，等待记事本对照结果。本次没有做日志复制的真实窗口复测。

## 下一步

完成本轮已经授权的 0.4.0 提交、构建验证、推送和正式发布，读取远程结果后再标记完成。版本号不取仓库内 ab-av1 的版本，发行主体为 videoenhancer.ext.3fui.dll。
