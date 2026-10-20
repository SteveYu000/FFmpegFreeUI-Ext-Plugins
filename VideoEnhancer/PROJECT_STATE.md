# VideoEnhancer 当前状态

更新日期：2026-10-21。交接快照，先读 [AGENTS.md](AGENTS.md)；实际状态以源码、Git 和可复核执行结果为准。这里只保留当前有效事实，不作逐次工作日志。

## 项目基线

| 项目 | 当前事实 |
| --- | --- |
| 代码位置 | `FFmpegFreeUI-Ext-Plugins/VideoEnhancer/`，不是同名上游独立仓库 |
| 远程仓库 | [SteveYu000/FFmpegFreeUI-Ext-Plugins](https://github.com/SteveYu000/FFmpegFreeUI-Ext-Plugins) |
| 分支 / HEAD | `main` / `9e001d2bf05476365898e5343aec38930028e8d6`；本次 `git pull --ff-only` 已确认无远程更新 |
| 产品版本 / 二进制 | `0.2.0` / `videoenhancer.ext.3fui.dll` |
| 已发布版本 | [VideoEnhancer/v0.2.0](https://github.com/SteveYu000/FFmpegFreeUI-Ext-Plugins/releases/tag/VideoEnhancer%2Fv0.2.0)，对应上述 HEAD；下述未提交修改尚未发布 |
| 上游移植基线 | maxzrb/VideoEnhancer `v1.3.13`，提交 `472d62fcb484557acc33a0df17ddbb01c77f0b2e`；后续同步需重新检查远程 |
| 构建目标 | `net10.0-windows10.0.17763.0`；Ext SDK `2.5.0`；LakeUI 构建锁定 `5.112.0`，支持后续兼容 5.x |
| 许可 | 发行 DLL 为 AGPL-3.0-only，独立自有源码保留 MIT；例外和第三方范围见 [LICENSING.md](LICENSING.md) |

代码导航：[Frontend](Frontend) 为 VB/LakeUI 界面，[Backend](Backend) 为 C# 服务和处理链；自有托管程序集最终合并。Python/FFmpeg/RTX 保留外部进程协议。处理链及入口见 [README.md](README.md)，用户教程见 [ExtTutorial.md](Frontend/Resources/ExtTutorial.md)，构建和依赖锁文件在 [release](release)。

当前入口：参数面板 → 画面帧下方的“视频参数 | AI增强”；主导航“视频超分”的竖排菜单为“首页、实时预览、模型管理、模型下载、模型转换、模型导入、使用教程”。模型管理已从首页移到实时预览下方，分段编辑在 AI 增强页。安装和源码分别打 ZIP；无插件 CLI、独立图片页面或右键超分模块。

## 当前工作区

本次需求：建立本插件的工作约定和单一状态文件。新增 `AGENTS.md` 与本文件；不修改产品代码、版本或 Release，不替用户提交上一轮改动。

上一轮参数总览精简已完成本地实现，尚未提交：

- [ExtVideoPipeline.Presentation.cs](Backend/ExtVideoPipeline.Presentation.cs) 新增展示逻辑；[ExtVideoPipeline.cs](Backend/ExtVideoPipeline.cs) 接入。总览只呈现启用功能及用户参数，去除“仅视频 FFV1 / MKV”等处理链说明和内部 JSON；分段方案显示可读范围、模型和尺寸。
- 命令行模板仍保留完整后端参数。RTX 预览分别列出服务启动命令、HTTP 请求及运行时占位值；复用真实请求构建，说明行不会进入实际执行参数。
- [README.md](README.md)、[ExtTutorial.md](Frontend/Resources/ExtTutorial.md) 已同步说明。
- 回归修改位于 `tests/ActualHostTests.cs`、`tests/ParameterErrorTests.cs`、`tests/Program.cs`、`tests/VideoEnhancer.Tests.csproj`。新增测试用 LakeUI 版本属性，不改变产品最低版本。

本次开始时有 7 个已跟踪修改文件及 1 个未跟踪的 Presentation 文件；检查期间还看到 `VideoEnhancer-安装说明.txt` 出现修改，本次没有修改该文件，也未判断其修改来源。加上本次两份文档，最终检查共 11 个未提交路径。不得清理或覆盖这些改动；之后提交时重新核对实际 diff 和用户指定范围。

## 最近验证与产物

以下为上一轮已观察结果，本次纯文档任务没有重跑产品测试：

- 插件 Release 构建成功；C# 回归 **423 项断言通过**，包含真实宿主命令生成与其他插件组合、模拟 RTX HTTP、实际 FFmpeg 样本流程。
- 真实宿主为 6.2.38、配套 LakeUI 5.115.0。测试覆盖命令生成，不代表实际窗口显示或 GPU 模型运行已验证。
- 测试临时选用 LakeUI 5.115.0，前端声明仍锁定 5.112.0，产生预期 NU1608 警告；不能报告为整个测试构建“零警告”。
- 本地 DLL 为 `dist/videoenhancer.ext.3fui.dll`，程序集版本 0.2.0.0；SHA-256：`2495eacc01597ec59bf81701d666a0e8ea4362bdeabd883956aed80ccb32b7e4`。
- 本次只读核实 DLL 内嵌源码快照为 `34889a4269b93a6c84ca34ff5094f76aea1ddc2b7d41629edcce3685d9b3c4be`；当前源码快照为 `044700a0231d11f3ed25169e8046ceb86f6c6fba1ad06c3928a4b7687fea66c5`。两份清单的差异是 `README.md`、`VideoEnhancer-安装说明.txt`，本次均未修改。发布前必须重新构建，不能直接用当前 DLL 执行 `-SkipBuild` 打包。
- 没有重新生成两份 ZIP；现有 0.2.0 ZIP 及 GitHub Release 仍是已发布基线，不能称为包含本地修改的新包。

本次文档检查：UTF-8、17 个相对链接及 `git diff --check` 通过；8 个原有改动文件的 SHA-256 与开始时一致。已核对源码快照和安装包复制清单，两份管理文档均不在其中；不需改动构建脚本。本次未运行产品构建、GPU 或窗口测试。

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

先核对 Git 和实际源码，保留上述未提交修改。用户要求提交时按中文消息提交对应范围；用户要求发布时重新构建、验证源码身份并生成两包，不能直接上传旧 ZIP。显存和复制问题分别等待针对性复测，保持未解决标记。
