# VideoEnhancer 当前状态

更新日期：2026-10-21。交接快照，先读 [AGENTS.md](AGENTS.md)；实际状态以源码、Git 和可复核执行结果为准。这里只保留当前有效事实，不作逐次工作日志。

## 项目基线

| 项目 | 当前事实 |
| --- | --- |
| 代码位置 | `FFmpegFreeUI-Ext-Plugins/VideoEnhancer/`，不是同名上游独立仓库 |
| 远程仓库 | [SteveYu000/FFmpegFreeUI-Ext-Plugins](https://github.com/SteveYu000/FFmpegFreeUI-Ext-Plugins) |
| Git | main，HEAD 为 64bf297，领先 origin/main 两项提交；本轮 git pull --ff-only 确认远程无更新；预览移植和 0.3.0 版本修改尚未提交 |
| 产品版本 / 二进制 | `0.3.0` / `videoenhancer.ext.3fui.dll` |
| 已发布版本 | [VideoEnhancer/v0.2.0](https://github.com/SteveYu000/FFmpegFreeUI-Ext-Plugins/releases/tag/VideoEnhancer%2Fv0.2.0)，源码为 `9e001d2bf05476365898e5343aec38930028e8d6`；本轮本地修改尚未发布 |
| 上游同步范围 | v1.3.13 / 472d62f 为完整基线；本轮追加 v1.3.14 / 6fe8545 的前四组预览修复，其余更新未移植 |
| 构建目标 | `net10.0-windows10.0.17763.0`；Ext SDK `2.5.0`；LakeUI 构建锁定 `5.112.0`，支持后续兼容 5.x |
| 许可 | 发行 DLL 为 AGPL-3.0-only，独立自有源码保留 MIT；例外和第三方范围见 [LICENSING.md](LICENSING.md) |

代码导航：[Frontend](Frontend) 为 VB/LakeUI 界面，[Backend](Backend) 为 C# 服务和处理链；自有托管程序集最终合并。Python/FFmpeg/RTX 保留外部进程协议。处理链及入口见 [README.md](README.md)，用户教程见 [ExtTutorial.md](Frontend/Resources/ExtTutorial.md)，构建和依赖锁文件在 [release](release)。

当前入口：参数面板 → 画面帧下方的“视频参数 | AI增强”；主导航“视频超分”的竖排菜单为“首页、实时预览、模型管理、模型下载、模型转换、模型导入、使用教程”。模型管理已从首页移到实时预览下方，分段编辑在 AI 增强页。安装和源码分别打 ZIP；无插件 CLI、独立图片页面或右键超分模块。

## 当前工作

用户已授权的前四组 v1.3.14 实时预览修复已完成移植并通过回归。随后按用户要求将产品版本统一更新为 **0.3.0**，发布日志直接在对话中交付。此前修改与审查分别在 `557ed1e`、`64bf297`；预览移植和本次版本修改尚未提交、推送或发布。

待提交修改包括：`Frontend/PreviewEngine.vb`、新增 `Frontend/PreviewEngine.ExtSources.vb`、`Frontend/HostRuntime.vb`；后端 `EnhancementPlanning.cs`、`EnhancementTaskRegistry.cs`、`ExtVideoPipeline.cs`；对应预览/计划/真实宿主回归和 README。此次另同步前后端项目版本、Ext 教程、安装说明与两份许可说明的版本标题；后端版本由合并程序集元数据自动读取。当前共 18 个未提交路径，具体以 Git 状态为准。

异步探测、取消与关键帧调度来自上游已审查的 v1.3.14；Ext 部分按当前步骤 ID 绑定本计划生成的视频输出和帧率，RVE 使用本阶段遥测，RTX/原生 FFmpeg 使用当前步骤进度，音频及探测阶段等待视频。补帧和跨后端两阶段按实际输出帧率换算时间，切步骤及终态清理旧遥测和路径。

未修改宿主、SDK 接口、预设结构和最终编码链。原版图片页/CLI/安装器等其余更新不在本轮范围内。没有进行部署或生成新 ZIP。

## 最近验证与产物

- **0.3.0 Release 构建通过，0 警告、0 错误**：执行 `dotnet restore Frontend/VideoEnhancerPlugin.vbproj --locked-mode --nologo` 和 `dotnet build Frontend/VideoEnhancerPlugin.vbproj -c Release --no-restore --nologo -v:quiet`；保持 LakeUI 5.112.0 / Ext SDK 2.5.0 / .NET 10 目标。
- **此前预览移植的完整 C# 回归 476 项断言通过，退出码 0**。此次仅修改版本与配套说明，未重复运行全套回归。其中预览专项 32 项覆盖真实子进程超时退出、UI 消息循环心跳、防止探测重入、零时间关键帧及失败重试、四档刷新、切任务/模式/隐藏/停止/音频步骤取消旧画面、RVE/RTX 进度隔离、实际 FFmpeg 不同画面及 FFprobe 帧率/关键帧。
- 新增回归先在旧实现上复现“音频阶段误启动视频抽帧”，移植后通过。跨后端先超分/先补帧、同后端组合及 RTX 继承补帧帧率均有语义检查。
- 真实宿主 6.2.38 / LakeUI 5.115.0 的当前步骤对象、音频等待、临时视频输出、RVE 映射和现有插件组合通过只读加载验证；不修改宿主安装目录。RTX HTTP/帧管道继续使用测试夹具，不代表真实 GPU 推理验证。
- 测试匹配宿主时使用 `VideoEnhancerTestLakeUiVersion=5.115.0`，有预期 NU1608 警告（前端精确依赖 5.112.0）。产品独立 Release 构建无此警告。
- 已重新生成 `dist/videoenhancer.ext.3fui.dll`；实测程序集 / 文件版本均为 **0.3.0.0**，后端服务版本为 **0.3.0**。DLL SHA-256：`6c48db67242451f7d841ba842ee220f3417ba6e4ed08ac70d7281ea611a8a230`。
- 当前源码快照及 DLL 内嵌快照一致，版本均为 0.3.0：`480c9997cdefd4a9f52b91df3c7039e8853783fd1148ad0f02c053c8fa338d42`，共 147 个源码文件。此前已确认合并 DLL 包含预览元数据类型，不引用 FFmpegFreeUI 主程序集或独立 VideoEnhancer.Backend 程序集。此次再次确认管理文档未进入源码发行快照。
- 没有生成新安装/源码 ZIP；现有 0.2.0 ZIP 及 GitHub Release 仍是旧发行，不能当作本轮结果。

本轮 Computer Use 已按当前技能初始化；`list_apps` 返回 native pipe 不可用（os error 2）。重置、重新初始化后重试仍失败，未操作实际窗口，**未完成真实界面视觉复测**。自动化消息循环和媒体测试不能替代此项。

测试日志位于本机忽略目录 `.test-tools/preview-port/`（full-tests.log、full-build.log、release-build.log；本次版本构建为 version-030-restore.log、version-030-build.log）。Git diff、UTF-8、相对链接及管理文档发行排除检查通过；参考仓库分支和工作文件未改动。

## 上游 v1.3.14 移植范围

上游正式版 [v1.3.14](https://github.com/maxzrb/VideoEnhancer/releases/tag/v1.3.14) 于 2026-10-20 发布，标签提交 `6fe854505e869bba76c7b10efde7703f0dd7eeb9`。已审查主线 `a4f4bf0ce2587aac0b35aecb765fd1e6536a035a` 比标签多一条发布验证文档提交。以实际移植基线 `472d62f` 比较，10 个新增提交、34 个变动路径；网页缓存曾仍显示 1.3.13，本轮依据实时 API 与实际 Git 对象核实。

| 本轮已移植项 | Ext 实现 |
| --- | --- |
| 异步探测与恢复 | stdout/stderr 同时异步读取，2.5 秒超时覆盖读取和等待，取消时结束子进程；仅缓存正帧率，超时后同一输入可重新探测 |
| 关键帧读取与重试 | 按有界时间窗口读取 packet 标记；支持 PTS=0；成功显示后才消费关键帧，不可解码时保留同一关键帧重试 |
| 刷新节奏与生命周期 | 100ms 调度保持 0.5/1/2/3 秒选择；按上次抽帧累计进度；切任务/模式、隐藏、停止、换阶段或进度回退时取消旧工作，不回写旧图 |
| 分步输出适配 | 当前宿主步骤只读适配；自有 FFmpeg/RVE/RTX/分段视频使用计划登记的路径和输出帧率；等待音频、探测和无视频输出步骤，未知进程参数不当作视频路径 |

来源：[预览主修复 f29d011](https://github.com/maxzrb/VideoEnhancer/commit/f29d0112b92a8572ddc3df8e61ec60123890ccfd)、[探测恢复/输出路径补修 dbf7e24](https://github.com/maxzrb/VideoEnhancer/commit/dbf7e24345096156387cdbdaa777f9fd8913532f)。使用当前 Ext 数据结构适配，没有引入上游 CLI 中转识别、旧宿主兼容或全局替代 FFmpeg。

本轮没有移植图片参数/错误详情界面、EXE 安装器或原生 FFmpeg CLI 透传；Ext 已移除相关原版入口。上游 VMAF 内置模型自动枚举尚未完善。本轮也没有修改 RVE/RTX 推理算法、显存策略或日志复制实现，下列两项问题继续保持未解决。

## 尚未解决的问题

### ONNX 图片超分显存不足

已证实：RealHatGAN-JP-Illustration-2x-fix1，输入 1500×1061，FP32，`UpscaleTileSize=0` 整帧推理时，ONNX Runtime 请求单个 9,905,897,472 字节（约 9.23 GiB）缓冲区失败；用户显卡为 RTX 5060、8 GB。报错发生在模型推理，FFmpeg 写命令的红字本身不是失败依据。

已建议分块 256，仍不足则 128。固定 FP32 ONNX 模型不会因勾选半精度自动变成 FP16。尚未实现自动 OOM 降级，也未记录真实 GPU 分块复测成功，不能标记为已修复。

### Rendering 后日志可见但无法复制

用户反馈：“复制当前视图”缺少 `BACKEND: Rendering` 之后日志，选中文字 Ctrl+C 也无法复制。截图已显示后续 ONNX 异常。问题未定位，未改宿主或插件复制实现。

已有检查：宿主复制读取控件 `.Text`；检查的 LakeUI 5.112 实现中，getter 与 AppendLine 使用同一行集合，尚无“缓存未同步”的证据。ANSI 色码、控制字符或粘贴目标的影响均未证实。一次只读剪贴板检查未捕获目标日志，不能据此判断截断原因。

当时 Computer Use 返回 native pipe 不可用（os error 2），重置重试仍失败，未完成实际窗口复制测试。曾请求用户粘贴到记事本比较，尚无结果。用户恢复该问题后，优先获取完整粘贴结果和真实控件操作证据，再决定修复位置；不属于本轮预览移植范围。

## 本地执行条件

以下路径仅为当前机器线索，换环境必须重查；不可写成项目固定依赖：

- Ext 插件目录：`E:/DesktopPlus/Works/Code/FFmpegFreeUI-Ext-Plugins/VideoEnhancer`。
- 同名上游参考目录：`E:/DesktopPlus/Works/Code/VideoEnhancer`。其 HandShake 记录不作为本插件的运行状态来源。
- 宿主参考仓库：`E:/DesktopPlus/Works/Code/FFmpegFreeUI-API-Extended-Edition`；仅在本任务明确涉及宿主时按授权范围修改。
- 上轮真实宿主测试目录是该宿主仓库下 `FFmpegFreeUI/bin/Release/net10.0-windows10.0.26100.0`，不要传 PluginHost 输出目录。
- FFmpeg / FFprobe 可通过 `VIDEOENHANCER_TEST_FFMPEG`、`VIDEOENHANCER_TEST_FFPROBE` 指定；上轮使用本机 `C:/Apps/ffmpeg/`。真实宿主目录用 `VIDEOENHANCER_TEST_HOST` 指定。
- 当前机器上轮选中 .NET SDK `11.0.100-rc.1.26425.128`，产品目标仍是 .NET 10；NETSDK1057 为预览 SDK 提示。不要把本机 SDK 选择变为项目升级要求。

真实宿主配套 LakeUI 5.115.0 时，从插件目录执行：

```powershell
dotnet restore tests/VideoEnhancer.Tests.csproj --locked-mode --nologo -p:VideoEnhancerTestLakeUiVersion=5.115.0
dotnet build tests/VideoEnhancer.Tests.csproj -c Release --no-restore --nologo -p:VideoEnhancerTestLakeUiVersion=5.115.0
dotnet run --project tests/VideoEnhancer.Tests.csproj -c Release --no-build -p:VideoEnhancerTestLakeUiVersion=5.115.0
```

未设置该属性时测试默认 5.112.0。执行前按实际宿主重新匹配，并设置所需环境变量。Python/GPU 测试须另查依赖和设备，不继承其他环境的可用结论。

分发测试用独立进程 `pwsh -NoProfile -File tests/DistributionLicenseTests.ps1`，以进程退出码判断，避免其预期失败样本留下的 `$LASTEXITCODE` 干扰外层判断。依赖校验支持 `./tests/NativeDependencyTests.ps1 -Offline`。

## 后续接手

预览移植和 0.3.0 版本修改已完成，工作区有未提交修改；尚未生成 0.3.0 ZIP 或更新 GitHub Release。用户要求提交时使用中文消息并只暂存明确范围；发布前确认源码身份后生成两包，不上传旧 ZIP。待 Computer Use 恢复后可补做真实窗口预览检查；ONNX 显存不足和宿主日志复制问题继续单独处理。
