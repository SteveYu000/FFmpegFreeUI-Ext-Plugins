# VideoEnhancer 当前状态

更新日期：2026-11-03。交接快照；实际状态以源码、Git 和可复核执行结果为准。这里只保留当前有效事实，不作逐次工作日志。

## 项目基线

| 项目 | 当前事实 |
| --- | --- |
| 代码位置 | `FFmpegFreeUI-Ext-Plugins/VideoEnhancer/` |
| 远程仓库 / Git | [SteveYu000/FFmpegFreeUI-Ext-Plugins](https://github.com/SteveYu000/FFmpegFreeUI-Ext-Plugins)，main，HEAD `9d1a5bb`，与 origin/main 同步；本轮 17 个修改/新增路径尚未提交 |
| 产品版本 / 二进制 | `0.3.0` / `videoenhancer.ext.3fui.dll`；本轮未修改版本 |
| 已发布版本 | [VideoEnhancer/v0.3.0](https://github.com/SteveYu000/FFmpegFreeUI-Ext-Plugins/releases/tag/VideoEnhancer%2Fv0.3.0)，源码提交 `9d1a5bb33bb1f07b41b09e5a54fb5a9470b3579e`；本轮修改尚未发布 |
| 上游同步范围 | v1.3.13 / `472d62f` 完整基线；v1.3.14 / `6fe8545` 的四组预览修复；本轮追加 v1.3.15 / `21436b7` 中适用 Ext 的更新 |
| 构建目标 | `net10.0-windows10.0.17763.0`；Ext SDK `2.5.0`；产品 LakeUI 锁定 `5.112.0`，支持兼容 5.x |
| 许可 | 发行 DLL 为 AGPL-3.0-only，独立自有源码保留 MIT；范围见 [LICENSING.md](LICENSING.md) |

代码导航：[Frontend](Frontend) 为 VB/LakeUI 界面，[Backend](Backend) 为 C# 服务和处理链，自有托管代码最终合并为一个 DLL。Python/FFmpeg/RTX 保留外部进程协议。处理链和入口见 [README.md](README.md)，用户教程见 [ExtTutorial.md](Frontend/Resources/ExtTutorial.md)。

当前入口：参数面板 → 画面帧下方的“视频参数 | AI增强”；主导航“视频超分”的竖排菜单为“首页、实时预览、模型管理、模型下载、模型转换、模型导入、使用教程”。分段编辑在 AI 增强页。安装和源码分别打 ZIP；无插件 CLI、独立图片页面、四宫格或右键超分模块。

## 当前工作

用户要求查看上游最新版本并移植本项目相关更新。已核实最新正式版为 v1.3.15（2026-11-02），完成适用部分的移植及本轮验证。开始时工作区干净，`git pull --ff-only` 确认 main 已同步，并取得已有 0.3.0 发布标签。此前状态文件关于“0.3.0 尚未提交/发布”的描述已经过时，本轮以实际 Git 和 GitHub API 为准。

本轮完成：

- TensorRT 超分选择器和模型转换页接受 ONNX；保留 ONNX Runtime。同步 28 项内置 ONNX 模型的候选后端、检查器和 ONNX 预检缓存版本。
- ONNX 转换支持动态尺寸、固定 profile、分块补边对齐、图内低倍率输出，并封装为现有 RVE 可加载的 TorchScript Engine。限定自包含、单输入、单输出 RGB NCHW 浮点图；不兼容时明确失败，不静默切换后端。
- 内置和用户模型清单向界面及实际转换进程传递 InputMultiple；缓存指纹包含约束。Ext 计划在源模型换成 Engine 后仍保留原模型输入倍数。
- 配置新增 TrySave，成功后只通知一次；HDR 保存失败时恢复开关、配置及宿主预设。保留 Ext 入队预设快照，不引入上游旧队列钩子。
- 真实 GPU 验证发现并修复两处 Windows 中文路径问题：ONNX 从内存解析；RVE Engine 通过 Python 文件流加载。RVE 补丁在已有输入倍数补丁的提前返回之前应用。
- 补齐 README、Ext 教程及新的 C#/Python 语义回归。

改动集中在 BackendServices、EnhancementPlanning、模型能力清单/预检、转换脚本及界面、PluginConfig、HDR 开关处理和两份新测试文件。未修改宿主或 SDK、最终编码链、依赖锁版本、项目版本和 GitHub Release；未生成新 ZIP，Ext 代码尚未提交或推送。

用户随后要求将中文 ONNX 路径问题提交上游。已在隔离检出 `.test-tools/upstream-pr-onnx-unicode/` 创建分支 `fix/onnx-tensorrt-unicode-paths`，提交 `0c41250`（中文消息），推送到 SteveYu000/VideoEnhancer 并创建 [上游 PR #16](https://github.com/maxzrb/VideoEnhancer/pull/16)。已回读确认目标 main、状态 OPEN、非草稿、无合并冲突；仅含 ONNX 内存解析修复与新测试两个文件。3 个无 GPU 用例通过（旧实现有 2 个失败），真实 GPU 及已有图内缩放共 6 个用例通过。该 PR 聚焦 ONNX 输入路径，使用 ASCII Engine 输出路径，不包含另一个 RVE Engine 中文加载补丁或 Ext 其他改动。上游检出工作区干净；Ext 原有 17 个未提交路径继续保留。

## 上游 v1.3.15 适用性

上游 [v1.3.15 发布说明](https://github.com/maxzrb/VideoEnhancer/releases/tag/v1.3.15) 经实时 GitHub API 和 Git 对象核实；标签源码提交为 `21436b7c079dfb17d2bc18458f3f7148f8a5654f`。已核对 main `0034eea514474f5ae008e8c68c3e36ff86c5bd04`，比标签仅多一项发布验证文档提交。

| 上游更新 | Ext 处理 |
| --- | --- |
| ONNX → TensorRT、手动转换尺寸倍数、模型候选和缓存 | 已移植，并适配类型化 DLL 服务与 Ext 任务计划 |
| HDR 开关写盘失败回滚 | 已移植，另覆盖宿主预设通知失败后的恢复 |
| 入队读取当前面板而非旧磁盘配置 | Ext 已按每个任务的宿主预设快照读取，保留当前实现 |
| 缺省 SVT AV1 / y412 编码，以及显式数字视频编码器保护 | 不移植；最终编码属于宿主，AI 步骤继续输出无损视频；回归确认没有注入 libsvtav1 |
| VMAF 通过真实 FFmpeg 恢复模型发现 | 上游用于全局替代进程中转；Ext 不安装该中转，保留宿主自己的质量评测，不添加反射轮询补丁 |
| EXE 安装器及上游版本号 | 不移植；Ext 继续 ZIP 分发，本轮版本仍为 0.3.0 |

此前 v1.3.14 已移植异步探测/超时恢复、关键帧有界读取与失败重试、预览刷新及取消生命周期、Ext 当前步骤的视频输出/帧率适配。相关代码已包含在提交 `9d1a5bb`，本轮完整回归继续通过。

## 最近验证与产物

- **最终产品 Release 构建通过，0 警告、0 错误**：locked restore 后以产品 LakeUI 5.112.0 构建，维持 Ext SDK 2.5.0 / .NET 10 目标。
- **完整 C# 回归 496 项断言通过，退出码 0**，包含本次 20 项专项断言、原有预设/参数展示/预览/原生组件/媒体链回归，以及 RTX HTTP、帧管道和取消夹具。
- **真实宿主 6.2.43.1 / LakeUI 5.117.0** 的插件发现、命令生成和插件组合验证通过。测试显式匹配 LakeUI 5.117.0，出现预期 NU1608（产品前端精确锁定 5.112.0）；产品独立 Release 无此警告。
- **Python 常规回归 26 个用例：19 通过，7 个 GPU 编译用例按开关跳过，退出码 0**。包括原有 PTH 图内缩放、GPU 张量设备和 RVE 组合处理；错误注入用例中的预期 traceback 不是测试失败。
- **独立 ONNX / TensorRT 专项 12/12 通过，退出码 0**：真实 ONNX Runtime 候选识别及 RTX 5060 上的小模型编译、RVE 加载和数值验证；覆盖 FP16/FP32、动态/固定 profile、分块补边、4x 网络图内输出 2x、中文模型/Engine 路径、外置权重及多输出拒绝。测试使用隔离 RVE 源码副本，未改用户安装目录。未测试大型 HAT 模型的显存占用或原始报错图片。
- TensorRT 输出缺少可选 modelopt / TensorRT-LLM 的提示；本轮非量化单图模型验证通过，没有安装这些可选组件。
- Computer Use 技能要求 node_repl，但本会话工具列表未提供该执行工具；**未进行真实窗口视觉复测**。HDR 组件故障测试关闭模态消息展示，验证的是保存和回滚逻辑。
- 最终 DLL：`dist/videoenhancer.ext.3fui.dll`，版本 0.3.0.0，SHA-256 `796f42db1cef8ce90aa64fbee9d2d8f02282fe91f8e3e0bb0198efbd59c6d806`。
- DLL 内嵌源码与当前源码身份一致：`4b7863ac83b0fdba5063a28e277b3180f8a4b0605e8d4cca208f79be27344374`，149 个文件；没有宿主主程序集、SharpCompress 或独立 Backend 程序集引用，管理文档未收录到发行源码快照。

本轮日志位于忽略目录 `.test-tools/upstream-port-1315/`：full-tests.log、python-tests.log、onnx-tensorrt-gpu.log、release-build.log。上游参考源码及 Git 对象在 `.test-tools/upstream-1.3.15/` 和 `.test-tools/upstream-review-20261103.git/`。早期失败用于定位中文路径问题；最终日志及退出码以上述通过结果为准。

## 尚未解决的问题

### ONNX 图片超分显存不足

已证实：RealHatGAN-JP-Illustration-2x-fix1，输入 1500×1061，FP32，`UpscaleTileSize=0` 整帧推理时，ONNX Runtime 请求单个 9,905,897,472 字节（约 9.23 GiB）缓冲区失败；用户显卡为 RTX 5060、8 GB。报错发生在模型推理，FFmpeg 写命令的红字本身不是失败依据。

已建议分块 256，仍不足则 128。固定 FP32 ONNX 模型不会因勾选半精度自动变成 FP16。尚未实现自动 OOM 降级，也未记录真实 GPU 分块复测成功，不能标记为已修复。

### Rendering 后日志可见但无法复制

用户反馈：“复制当前视图”缺少 `BACKEND: Rendering` 之后日志，选中文字 Ctrl+C 也无法复制。截图已显示后续 ONNX 异常。问题未定位，未改宿主或插件复制实现。

已有检查：宿主复制读取控件 `.Text`；检查的 LakeUI 5.112 实现中，getter 与 AppendLine 使用同一行集合，尚无“缓存未同步”的证据。ANSI 色码、控制字符或粘贴目标的影响均未证实。一次只读剪贴板检查未捕获目标日志，不能据此判断截断原因。

当时 Computer Use 返回 native pipe 不可用（os error 2），重置重试仍失败，未完成实际窗口复制测试。曾请求用户粘贴到记事本比较，尚无结果。用户恢复该问题后，优先获取完整粘贴结果和真实控件操作证据，再决定修复位置；不属于本轮上游同步范围。

## 本地执行条件

以下仅为当前机器说明，换环境须重查，不作为项目固定依赖：

- 本插件：`E:/DesktopPlus/Works/Code/FFmpegFreeUI-Ext-Plugins/VideoEnhancer`。会话最初提供的同名 `E:/DesktopPlus/Works/Code/VideoEnhancer` 参考目录已不存在，不要把旧目录记录当作当前源码位置。
- 宿主只读验证目录：`E:/DesktopPlus/Works/Code/FFmpegFreeUI-API-Extended-Edition/FFmpegFreeUI/bin/Release/net10.0-windows10.0.26100.0`；本轮实际为 6.2.43.1 / LakeUI 5.117.0。
- FFmpeg / FFprobe 本轮使用 `C:/Apps/ffmpeg/`，通过 VIDEOENHANCER_TEST_FFMPEG / VIDEOENHANCER_TEST_FFPROBE 指定。
- .NET SDK 本轮选择 11.0.100-rc.1.26425.128，产品目标仍是 .NET 10。NETSDK1057 为预览 SDK 提示，不应把本机 SDK 选择变成项目升级要求。
- GPU：RTX 5060 Laptop GPU，8151 MiB，驱动 617.42。
- 真实 GPU Python：`C:/Apps/FFmpegFreeUI API Extended/Plugin/videoenhancer/python/python/python.exe`；已具备 torch、ONNX、ONNX Runtime、TensorRT、Torch-TensorRT。对应已安装 RVE backend 只读作为测试副本来源。
- 基础 Codex Python 不含上述推理包；`.test-tools/scale-python/` 仅有 CPU Torch 等部分依赖。不要对这些解释器混用依赖结论。

常规构建及回归入口：

```powershell
dotnet restore Frontend/VideoEnhancerPlugin.vbproj --locked-mode
dotnet build Frontend/VideoEnhancerPlugin.vbproj -c Release --no-restore --nologo
dotnet restore tests/VideoEnhancer.Tests.csproj --locked-mode -p:VideoEnhancerTestLakeUiVersion=5.117.0
dotnet build tests/VideoEnhancer.Tests.csproj -c Release --no-restore -p:VideoEnhancerTestLakeUiVersion=5.117.0
dotnet run --project tests/VideoEnhancer.Tests.csproj -c Release --no-build -p:VideoEnhancerTestLakeUiVersion=5.117.0
python -B -m unittest discover -s tests -p "test_*.py"
```

真实宿主测试还需 VIDEOENHANCER_TEST_HOST 指向上述主程序输出目录；应按当前实际宿主重新匹配 LakeUI，不改变产品最低依赖。专项 C# 可在 run 后附 `-- --upstream-1315-tests`。独立 GPU 专项：在已构建测试程序且推理依赖齐全的解释器中，设置 VIDEOENHANCER_TEST_TRT=1、VIDEOENHANCER_TEST_RVE_BACKEND 为现有 RVE backend 目录，再运行 `python -B tests/test_onnx_tensorrt.py -v`。TEMP/TMP、Torch/CUDA 缓存均应指向隔离测试目录。

本轮未修改安装、压缩或下载依赖，未重复运行独立分发测试。对应入口保留 `pwsh -NoProfile -File tests/DistributionLicenseTests.ps1` 及 `./tests/NativeDependencyTests.ps1 -Offline`。

## 后续接手

本轮上游相关更新、验证和使用说明已完成，工作区有 17 个未提交路径；没有新发布包、提交或推送。用户要求提交时用中文消息；若要求发布，先按对应版本核验源码身份，再生成安装/源码两包，不上传旧 ZIP。实际窗口视觉复测仍需可用的 Computer Use 执行工具。ONNX 大模型显存不足和宿主日志复制问题仍保持未解决，不能因小模型 TensorRT 测试通过而标记修复。
