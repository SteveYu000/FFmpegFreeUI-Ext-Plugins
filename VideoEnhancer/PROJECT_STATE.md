# VideoEnhancer 当前状态

更新日期：2026-10-21。交接快照，先读 [AGENTS.md](AGENTS.md)；实际状态以源码、Git 和可复核执行结果为准。这里只保留当前有效事实，不作逐次工作日志。

## 项目基线

| 项目 | 当前事实 |
| --- | --- |
| 代码位置 | `FFmpegFreeUI-Ext-Plugins/VideoEnhancer/`，不是同名上游独立仓库 |
| 远程仓库 | [SteveYu000/FFmpegFreeUI-Ext-Plugins](https://github.com/SteveYu000/FFmpegFreeUI-Ext-Plugins) |
| Git | `main`；本轮实现与工作约定已提交为 `557ed1e`，提交前已安全同步；当前 HEAD 以 Git 查询为准，尚未推送 |
| 产品版本 / 二进制 | `0.2.0` / `videoenhancer.ext.3fui.dll` |
| 已发布版本 | [VideoEnhancer/v0.2.0](https://github.com/SteveYu000/FFmpegFreeUI-Ext-Plugins/releases/tag/VideoEnhancer%2Fv0.2.0)，源码为 `9e001d2bf05476365898e5343aec38930028e8d6`；本轮本地修改尚未发布 |
| 上游移植基线 | maxzrb/VideoEnhancer `v1.3.13`，提交 `472d62fcb484557acc33a0df17ddbb01c77f0b2e`；后续同步需重新检查远程 |
| 构建目标 | `net10.0-windows10.0.17763.0`；Ext SDK `2.5.0`；LakeUI 构建锁定 `5.112.0`，支持后续兼容 5.x |
| 许可 | 发行 DLL 为 AGPL-3.0-only，独立自有源码保留 MIT；例外和第三方范围见 [LICENSING.md](LICENSING.md) |

代码导航：[Frontend](Frontend) 为 VB/LakeUI 界面，[Backend](Backend) 为 C# 服务和处理链；自有托管程序集最终合并。Python/FFmpeg/RTX 保留外部进程协议。处理链及入口见 [README.md](README.md)，用户教程见 [ExtTutorial.md](Frontend/Resources/ExtTutorial.md)，构建和依赖锁文件在 [release](release)。

当前入口：参数面板 → 画面帧下方的“视频参数 | AI增强”；主导航“视频超分”的竖排菜单为“首页、实时预览、模型管理、模型下载、模型转换、模型导入、使用教程”。模型管理已从首页移到实时预览下方，分段编辑在 AI 增强页。安装和源码分别打 ZIP；无插件 CLI、独立图片页面或右键超分模块。

## 当前工作

用户本轮要求：先提交现有改动，再查看上游最新版更新及其与 Ext 项目的关系。已完成本地提交 `557ed1e`（精简视频增强参数展示并建立项目工作约定），共 11 个路径，包含参数展示、对应测试、教程/安装说明及工作约定；未推送或发布。

已提交的参数展示行为：总览只呈现启用功能和用户参数，去除中间视频说明和内部 JSON；完整 RVE/FFmpeg 参数、RTX 服务启动和 HTTP 请求结构留在命令模板及任务详情。展示逻辑位于 `Backend/ExtVideoPipeline.Presentation.cs`；预览说明不进入真实执行参数。测试可通过 `VideoEnhancerTestLakeUiVersion` 匹配实际宿主，不改变产品最低依赖。

随后通过 GitHub 实时 API 和实际 Git 对象比较移植基线与最新主线，完成下节审查。本轮未实施上游移植，产品版本仍是 0.2.0。当前业务修改均已提交，审查结果在本文件维护；进入下一轮前重新检查 Git 状态。

## 最近验证与产物

以下为此前已观察的产品验证结果；本轮提交与差异审查没有重跑产品测试：

- 插件 Release 构建成功；C# 回归 **423 项断言通过**，包含真实宿主命令生成与其他插件组合、模拟 RTX HTTP、实际 FFmpeg 样本流程。
- 真实宿主为 6.2.38、配套 LakeUI 5.115.0。测试覆盖命令生成，不代表实际窗口显示或 GPU 模型运行已验证。
- 测试临时选用 LakeUI 5.115.0，前端声明仍锁定 5.112.0，产生预期 NU1608 警告；不能报告为整个测试构建“零警告”。
- 本地 DLL 为 `dist/videoenhancer.ext.3fui.dll`，程序集版本 0.2.0.0；SHA-256：`2495eacc01597ec59bf81701d666a0e8ea4362bdeabd883956aed80ccb32b7e4`。
- 本次只读核实 DLL 内嵌源码快照为 `34889a4269b93a6c84ca34ff5094f76aea1ddc2b7d41629edcce3685d9b3c4be`；当前源码快照为 `044700a0231d11f3ed25169e8046ceb86f6c6fba1ad06c3928a4b7687fea66c5`。两份清单的差异是 `README.md`、`VideoEnhancer-安装说明.txt`，本次均未修改。发布前必须重新构建，不能直接用当前 DLL 执行 `-SkipBuild` 打包。
- 没有重新生成两份 ZIP；现有 0.2.0 ZIP 及 GitHub Release 仍是已发布基线，不能称为包含本地修改的新包。

本轮记录检查：UTF-8、相对链接及 `git diff --check` 通过。`AGENTS.md`、`PROJECT_STATE.md` 仍不在发行源码快照和安装包复制清单中。参考仓库只获取上游 Git 对象，原分支及工作文件保持不变。本轮未运行产品构建、GPU 或窗口测试。

## 上游 v1.3.14 审查（2026-10-21，未移植）

最新正式版为 [v1.3.14](https://github.com/maxzrb/VideoEnhancer/releases/tag/v1.3.14)，2026-10-20 发布，标签指向 `6fe854505e869bba76c7b10efde7703f0dd7eeb9`。实时主线为 `a4f4bf0ce2587aac0b35aecb765fd1e6536a035a`，比标签多一条发布验证文档提交。GitHub 网页缓存曾仍显示 1.3.13，本轮以实时 API、标签对象和 Git 差异交叉核实。

比较基线为 Ext 实际移植的 `472d62f`；截至最新主线共 10 个新增提交、34 个变动路径，主要是预览、原版图片页/CLI/安装器及测试文档。实际移植基线仍为 1.3.13，不能因为已审查而改写成已同步 1.3.14。

| 上游变化 | Ext 相关性与现状 |
| --- | --- |
| ffprobe 异步探测、2500ms 超时和取消、同时排空输出管道 | 高，建议优先移植。`Frontend/PreviewEngine.vb` 的 OnTick 仍同步调用 ProbeInputFps/CheckKeyframeProbe；ReadToEnd 在 WaitForExit 前，等待时间不能限制前面的阻塞读取 |
| 帧率探测失败后可恢复，仅缓存正帧率 | 高。Ext 仍将 0 写入输入帧率缓存，临时不可读后可能不再重试 |
| 有界窗口读取关键帧 packet，支持零时间帧，成功显示后才消费关键帧 | 高。Ext 仍用 show_frames 全量扫描，SeekSeconds 判断为大于 0，探测时即推进已用关键帧 |
| 刷新调度、慢速任务换帧，切任务/模式/隐藏时取消并丢弃旧图 | 高。Ext 仍为 500ms 定时器、每轮改写进度游标，后台回图仅判断 running，存在对应旧逻辑 |
| 分步编码等待视频阶段，读取当前步骤输出，进度回退时重置 | 高，需适配 Ext。当前只读 task.输出文件；AI 前置步骤写中间视频，最终文件可能尚不存在。应结合现有步骤/事件与中间路径做只读适配，不引入 CLI 中转识别或旧宿主兼容分支 |
| 图片输出根目录/末尾反斜杠时改用 ArgumentList，避免丢失后端模型参数 | 原修复入口不适用。Ext 已删除独立图片页→CLI 链，前端直接传设置；外部进程参数另由 WindowsArguments 处理末尾反斜杠。用户此前 ONNX 任务已成功加载模型，不是这个参数丢失问题 |
| 图片失败显示主因、完整错误详情/日志及高 DPI 提示布局 | 可借鉴并适配首页任务详情、宿主日志，不恢复图片页面。与现有日志复制异常不是同一项修复 |
| 安装器/自更新接受 FFmpegFreeUI.exe 与 FFmpegFreeUI.x64.exe | 无需直接移植。Ext 仅发 ZIP，Install.ps1 按 SDK/PluginHost 检查宿主，不依赖这两个 EXE 名字 |
| 原生 FFmpeg 工具命令透传、停用后只清理本插件替代路径 | 无需直接移植。上游为全局替代进程/CLI 补救；Ext 正常入口不写全局替代路径，原生命令由宿主执行。HostSettings 中仍有未使用的属性定义，不等于启用了替代机制 |

主要证据：[预览修复 f29d011](https://github.com/maxzrb/VideoEnhancer/commit/f29d0112b92a8572ddc3df8e61ec60123890ccfd)、[探测恢复/输出路径补修 dbf7e24](https://github.com/maxzrb/VideoEnhancer/commit/dbf7e24345096156387cdbdaa777f9fd8913532f)、[图片参数与错误详情 99ed3d9](https://github.com/maxzrb/VideoEnhancer/commit/99ed3d9e75bbd7bf74693af31e7a3a1b845aee32)、[宿主名/工具透传 d7441ae](https://github.com/maxzrb/VideoEnhancer/commit/d7441aefcf7efbca7838a646f59ccb075be69b62)。

建议后续将整组预览修复作为一次移植：异步/取消/超时及缓存恢复、刷新和关键帧语义、按 Ext 步骤绑定输出，并改造上游预览回归夹具。错误详情展示可另行改进。本轮为源码静态审查，未复现视觉症状，未运行上游测试，不声称 UI 或 GPU 修复已验证。

本次差异没有改动 RVE/RTX 推理算法、模型和自动显存降级策略，也没有修改日志复制实现。上游明确说明启用时 VMAF 内置模型自动枚举仍待完善，不能称所有评测问题均已解决。以下两项原问题继续保持未解决。

## 尚未解决的问题

### ONNX 图片超分显存不足

已证实：RealHatGAN-JP-Illustration-2x-fix1，输入 1500×1061，FP32，`UpscaleTileSize=0` 整帧推理时，ONNX Runtime 请求单个 9,905,897,472 字节（约 9.23 GiB）缓冲区失败；用户显卡为 RTX 5060、8 GB。报错发生在模型推理，FFmpeg 写命令的红字本身不是失败依据。

已建议分块 256，仍不足则 128。固定 FP32 ONNX 模型不会因勾选半精度自动变成 FP16。尚未实现自动 OOM 降级，也未记录真实 GPU 分块复测成功，不能标记为已修复。

### Rendering 后日志可见但无法复制

用户反馈：“复制当前视图”缺少 `BACKEND: Rendering` 之后日志，选中文字 Ctrl+C 也无法复制。截图已显示后续 ONNX 异常。问题未定位，未改宿主或插件复制实现。

已有检查：宿主复制读取控件 `.Text`；检查的 LakeUI 5.112 实现中，getter 与 AppendLine 使用同一行集合，尚无“缓存未同步”的证据。ANSI 色码、控制字符或粘贴目标的影响均未证实。一次只读剪贴板检查未捕获目标日志，不能据此判断截断原因。

当时 Computer Use 返回 native pipe 不可用（os error 2），重置重试仍失败，未完成实际窗口复制测试。曾请求用户粘贴到记事本比较，尚无结果。用户恢复该问题后，优先获取完整粘贴结果和真实控件操作证据，再决定修复位置；不是本次文档任务的自动待办。

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

用户本轮只要求审查上游更新，尚未要求实际移植。下一次明确要求移植时，以上节预览修复为首批范围，保留 Ext 处理链与现有入口。显存和日志复制问题继续分别复测，不因升级上游版本而自动关闭。发布前重建并校验源码身份，不能直接上传旧 ZIP；当前仅本地提交，未推送或发布。
