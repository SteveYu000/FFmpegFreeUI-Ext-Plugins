# VideoEnhancer Ext

面向 FFmpegFreeUI API Extended Edition 的视频 AI 增强插件，前端和后端合并发布为 `videoenhancer.3fui.dll`。

需要 Ext API 2.5、LakeUI 5.110 或更新的 5.x，以及 .NET 10 Desktop Runtime。插件不引用 FFmpegFreeUI 主程序集，不提供旧宿主兼容入口。

## 安装

关闭宿主，将 `VideoEnhancer-0.1.0-win-x64.zip` 解压到宿主的 `Plugin` 目录。ZIP 包含插件 DLL、aria2-next、许可及对应源码；Python / RVE、RTX 运行组件和模型在“视频超分 → 模型下载”中按需安装。

从源码安装：

```powershell
$env:FFMPEGFREEUI_HOME = "相对或绝对宿主目录"
./Install.ps1
```

也可以使用 `./Install.ps1 -HostDirectory ../FFmpegFreeUI`。相对目录以本插件源码目录为基准。源码安装通过 ZIP 安装本插件的文件，保留现有模型、用户配置和其他插件。

```powershell
dotnet build VideoEnhancer.slnx -c Release
./release/Build-Zip.ps1
dotnet run --project tests/VideoEnhancer.Tests.csproj -c Release
```

可将 `VIDEOENHANCER_TEST_HOST` 设为 Ext 宿主编译输出目录，额外验证真实宿主命令生成及插件组合。测试只读加载宿主程序集，不修改宿主。

媒体回归需要 FFmpeg 与 FFprobe 位于 PATH，可分别设置 `VIDEOENHANCER_TEST_FFMPEG`、`VIDEOENHANCER_TEST_FFPROBE`。

## 入口与预设

- 参数面板 → 画面帧下方 → **视频参数 | AI增强**：超分、补帧、HDR 和分段开关直接控制功能，参数随 v6 预设保存和恢复。
- 主导航参数面板下方 → **视频超分**：竖排“首页、实时预览、模型下载、模型转换、模型导入、使用教程”。
- 首页显示本插件任务的生命周期状态、当前用户参数、插件 ZIP 更新信息和已安装模型；双击任务可查看其参数和后端命令。
- 分段方案通过 AI 参数页中的“编辑分段方案”配置，按输入文件保存进预设。没有配置分段方案的文件沿用普通 AI 开关；普通开关都关闭时交给宿主直接处理。没有独立图片页面或右键超分模块。

用户的最终 FFmpeg 编码、音频、字幕、容器及自定义参数由宿主预设控制。参数总览显示 AI 设置、分段方案与 RTX 真实请求结构；命令预览显示 RVE / 中间 FFmpeg 的实际命令结构。运行时生成的端口、会话、尺寸及完整 RTX JSON 写入任务日志和结构化结果。

## 处理链

1. 从任务的预设快照读取本插件私有数据；不读取之后被用户改动的当前面板设置。
2. 探测原始视频，在每个任务独立的工作目录准备模型和无损视频源。多个明确选择的 `0:v:序号` 分别处理。
3. RVE 通过便携 Python 直接执行；同后端的组合使用帧级包装器，跨后端使用视频流中间文件。分段的模型段使用 RVE，FFmpeg / Anime4K 段生成后统一无损拼接。
4. RTX 在 DLL 中启动 sidecar，以 HTTP JSON 提交任务，通过命名管道接收原始帧，再由宿主队列中的 FFmpeg 步骤写入 FFV1。
5. 每个选中视频流输出仅含视频的 FFV1 / MKV 中间文件。SDR 模型路径使用 RGB 8-bit，HDR 路径使用 RGB 16-bit；RTX 保留 10-bit 输出。
6. 保持原始输入为原生命令的输入 0，并追加增强视频。按最终命令实际的输入编号绑定所选视频；音频、字幕、附件、章节及原始元数据继续来自原始输入或用户指定的其他输入。
7. 宿主继续应用最终滤镜、编码、裁剪及封装，然后执行现有后处理。两遍编码复用同一增强视频。
8. 成功、失败或取消均清理本任务的中间文件；最终文件由宿主管理。暂停和停止使用宿主队列控制。

## 能力边界

- RVE 原始帧协议要求恒定帧率；检测到明显的变帧率时会在启动前报错。
- 分段模式目前不能与补帧、RTX HDR 同时开启，不能静默忽略这些设置。
- RTX VSR 与补帧按原后端规则固定先补帧、再 RTX；HDR 输入不能重复进行 RTX HDR 映射。
- 带首帧或容器起始偏移的输入不能与 `-copyts` 同时启用；`-isync`、绝对 `-seek_timestamp` 和非 1 的 `-itsscale` 输入时间轴也会被明确拒绝。默认时间戳归零、普通输入裁剪和音视频首帧偏移会同步到增强视频。
- 完全自写命令应使用明确的 `-map 0:v:序号` 或 `[0:v:序号]` 滤镜输入。无法确认来源时会报错。
- 前置步骤、原生滤镜、额外输入及后处理插件可按 Ext 现有顺序组合。其他插件若在 `TaskBeforePrepare` 就分析视频（如提前搜索质量参数），仍分析原始输入；现有接口不提供向这类插件交换增强视频的统一契约。

## 目录和依赖

`Frontend` 为 VB / LakeUI 界面，`Backend` 为 C# 服务与处理链，构建后合并为一个 DLL。Python、FFmpeg、RTX SDK 和 aria2-next 仍为各自的运行组件。

默认数据目录为 DLL 同目录下的 `videoenhancer`。可用 `VIDEOENHANCER_ROOT` 指定数据目录（支持环境变量展开、相对 DLL 目录的路径），用 `VIDEOENHANCER_FFMPEG` 指定 FFmpeg。项目引用和构建脚本采用相对路径、NuGet 或环境变量，不依赖开发机其他仓库目录。

发行包只生成 ZIP 及 SHA-256 校验文件，不生成 EXE 安装包、自更新安装器或分发安装包页面。
