# VideoEnhancer 当前状态

更新日期：2026-11-12。交接快照，以源码、Git 和实际验证结果为准。

## 当前任务

用户要求“更新 LakeUI 版本为最新版，并应用之前取消的渲染优化”，随后要求将 ab-av1 和 VideoEnhancer 分别提交。实现与验证已完成；本状态文件与 VideoEnhancer 改动同批提交。当前仅授权本地提交，不推送或更新线上版本、标签、资产。

- 实际目标为 FFmpegFreeUI-Ext-Plugins/VideoEnhancer，远程 SteveYu000/FFmpegFreeUI-Ext-Plugins；主分支 main，本轮开始时 HEAD 与 origin/main 均为 29fb23d。已 fetch 核对无远程新增提交。
- ab-av1 改动按用户要求单独提交为 50fbbe0“支持 ab-av1 单视频流预设并校验输入流数量”；527 项现有断言通过。VideoEnhancer 使用下一条独立提交，不混入 ab-av1 文件。
- 官方 NuGet 实时版本列表最新稳定版为 LakeUI 5.301.0，包仅提供 net11.0-windows；前后端及测试因此统一到 net11.0-windows10.0.17763.0。SDK 2.5.0 与插件版本 0.4.0 保持原值。
- 前端精确锁定 LakeUI 5.301.0，测试默认匹配；两份 packages.lock.json 已重新生成并通过 locked restore。Vortice 等传递依赖未变，LakeUI 仍由宿主提供，赞助者许可边界不变。
- 恢复 Frontend/LakeLayoutPanel.vb 中 ModernGridPanel、ModernHorizontalPanel 两处 TransparentRenderMode = SkipWhenFullyTransparent。它们只承担布局；严格全透明时跳过自身 GPU 表面，子控件保留原有背景源。现有滚动事务、透明背景和 DPI 布局保持原设计。
- 同步运行时最低版本检查、源码安装检查、ZIP 的 lakeUi 元数据、使用教程、README 与中文安装说明到 LakeUI 5.301 / .NET 11；Python 测试所用 C# 夹具路径同步到新目标框架。

## 本轮验证

- locked restore 成功；Release 构建成功，0 错误、3 条 .NET 11 新增 WFO2000 警告：BackendServiceJob.Output / Error 的新 StreamReader 返回值及测试 TestHost.ApiVersion 的新 Version 返回值。未改变这些现有属性行为，也未全局屏蔽警告。
- C# 完整回归 513 个断言通过、退出码 0，包含 UI/DPI 布局、单 DLL 入口、原生预览、错误处理、媒体链、RTX 协议、真实 Ext 宿主插件发现与其他插件组合。
- 隔离目录中的安装/源码双包检查 247 个断言通过、退出码 0；安装元数据的 lakeUi = 5.301.0，源码身份与新 DLL 一致。安装包不合并或携带 LakeUI。
- 两个 Python 夹具的语法及新 C# 可执行文件路径检查通过；本轮没有改 AI 算法或重跑 GPU 推理。
- 新 DLL 源码快照：171 个文件，0433b2ac2c628425263b90f9fadc94e80561745304d753817e20866a07549ed5。本轮只需状态文件更新，管理文件不会改变源码身份。
- 日志和本地测试双包在忽略目录 .test-tools/lakeui-5301/。这些是未发布的验证产物；不要将它们当作线上 0.4.0 资产或上传覆盖已有版本。
- 当前没有 Computer Use / node_repl 工具入口；没有真实窗口截图、连续滚动或 LakeUI 独立窗口复测。布局与宿主加载回归不能当作视觉验收，也未量化 GPU 或滚动性能提升。

本机实际验证宿主：6.5.0.1、LakeUI 5.301.0.0、.NET 11，目录 E:/DesktopPlus/Works/Code/FFmpegFreeUI-API-Extended-Edition/FFmpegFreeUI/bin/Release/net11.0-windows。通过 VIDEOENHANCER_TEST_HOST 指定，FFmpeg/FFprobe 用 VIDEOENHANCER_TEST_FFMPEG / VIDEOENHANCER_TEST_FFPROBE 指向 C:/Apps/ffmpeg/。本轮没有改动宿主源码或安装目录。

## 已发布版本

线上仍为 [VideoEnhancer Ext 0.4.0](https://github.com/SteveYu000/FFmpegFreeUI-Ext-Plugins/releases/tag/VideoEnhancer/v0.4.0)，发行代码 c106ddbecad9c53234f2526dfedaa2af2f45382e；标签 VideoEnhancer/v0.4.0 未移动。该发行基于 .NET 10 / LakeUI 5.117，与本轮工作区不同。

上次发布的安装包、独立源码包及各自 SHA-256 已核验，证据在 .test-tools/release-0.4.0/。dist 中的旧发布 ZIP 未覆盖；dist/videoenhancer.ext.3fui.dll 已被本轮本地构建更新，不能与旧发布源码 ZIP 混用。

当前代码已含上游 v1.3.15–v1.3.17 及主线 83c5f12 的 Ext 相关移植：RGB48、ONNX/TensorRT 转换和精度限制、缓存隔离、中文路径、后端脚本所有权及下载去重等。最终编码、音频、字幕、章节、元数据与后处理仍由宿主预设控制。

## 尚未解决的问题

### ONNX 图片超分显存不足

已证实：RealHatGAN-JP-Illustration-2x-fix1，输入 1500×1061，FP32，UpscaleTileSize=0 整帧推理时，ONNX Runtime 请求单个 9,905,897,472 字节（约 9.23 GiB）缓冲区失败；用户显卡为 RTX 5060、8 GB。报错发生在模型推理，FFmpeg 写命令的红字本身不是失败依据。

已建议分块 256，仍不足则 128。固定 FP32 ONNX 不会因勾选半精度自动变成 FP16。本轮没有实现自动 OOM 降级，也未复测该大模型成功；RGB48 与小模型验证不能把此问题标记为已修复。

### Rendering 后日志可见但无法复制

用户反馈：“复制当前视图”缺少 BACKEND: Rendering 之后日志，选中文字 Ctrl+C 也无法复制。截图显示后续 ONNX 异常。问题未定位，本轮未改宿主或插件复制实现。

此前检查：宿主复制读取控件 .Text；LakeUI 5.112 的 getter 与 AppendLine 使用同一行集合，尚无缓存未同步证据。ANSI、控制字符和粘贴目标影响均未证实；只读剪贴板检查未捕获目标日志。此前 Computer Use native pipe 不可用，未完成真实窗口复制测试，等待记事本对照结果。本次没有做日志复制的真实窗口复测。

## 后续工作

本轮实现、验证及分别提交工作已完成；提交详情和当前工作区以 Git 查询为准。后续推送或发布按用户要求进行。真实视觉效果及上述两项既有问题尚未验证完成，不自动扩大为本轮修复范围。
