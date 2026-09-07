# FFmpegFreeUI Ext AB-AV1 插件

这是个使用AB-AV1和FFmpeg作为后端，高度集成进3FUI编码面板的插件。
插件使用了AB-AV1的功能，计算目标VMAF或者xpsnr分数所需的最小crf值。

## 使用方法
打开“参数面板 → 质量”，即可在“控制方式”中看到“使用目标分数（ab-av1）”。
## 原生集成方式

- `host.Ui.RegisterChoice`：向第一个原生质量控制方式下拉框添加稳定选项；第二、第三栏通过 `ParametersVideoQualityFields` 有序资源租约切换为指标和目标分数，离开插件模式或卸载时完整恢复原生条目与样式。
- `IExtPluginUiContext.StateJson`：参数随 3FUI v6 预设保存和恢复，不使用插件自己的预设文件。
- `host.PresetOverview.RegisterRowProvider`：使用 Ext API 2.5 的正式预设总览提供器，从宿主当前正在展示的完整预设快照读取插件私有状态。参数面板、预设管理、任务参数查看和 Agent 读取预设均由宿主统一追加同一组 AB-AV1 行；插件不再监听或改写原生文本框，也不输出启用/未启用状态。
- `host.Commands.RegisterStepProvider`：仅在原生命令模板预览中贡献 `ab-av1 crf-search` 步骤。编码器、编码预设、像素格式、解码输入参数和自定义编码参数均来自当前预设；未选择编码器时不生成 `--encoder`，也不回退或补全为 `libsvtav1`。实际任务仍由准备阶段处理器执行一次搜索，避免重复运行。
- `ext.preset.after-capture`：把仅用于显示的 VMAF/XPSNR 和目标分数从原生 FFmpeg 质量字段中清除；真实值保存在插件私有状态，防止原生命令模板误生成 `-VMAF`/`-XPSNR` 参数。
- `ext.task.before-prepare`：在任务准备阶段执行可取消的 ab-av1 搜索，并将 CRF 写回当前任务快照。
- `ReportProgress` / `ReportResult`：搜索过程、最终 CRF、所选 VMAF/XPSNR 分数、预测视频流大小和预测编码时长进入原生任务日志。
- 原生编码队列：添加、开始、停止、重置、并发调度、输出路径、音频/字幕/附件及最终封装全部由 3FUI 负责；插件不提供第二套任务列表。

## 安装要求

- FFmpegFreeUI API Extended Edition，Ext Plugin API `2.5.0` 或更高版本。
- Windows 10/11 和 .NET 10 Desktop Runtime。
- `ab-av1.exe`。
- 可供 ab-av1 调用、包含所选编码器及所用 VMAF/XPSNR 质量滤镜的 FFmpeg。

安装目录：

```text
FFmpegFreeUI.exe
FFmpegFreeUI.Ext.PluginHost.dll
FFmpegFreeUI.Ext.PluginSdk.dll
Plugin\
├─ FFmpegFreeUI.Ext.AbAv1.3fui.dll
└─ ab-av1.exe
```

插件启动的子进程会依次从插件目录、FFmpegFreeUI 根目录和系统 `PATH` 查找 FFmpeg。不要把另一份 `FFmpegFreeUI.Ext.PluginSdk.dll` 放进 `Plugin` 目录。

如何安装插件：打开插件管理页，拖入插件或者打开插件目录放入。

## 使用

1. 在原生参数面板选择希望使用的 FFmpeg 视频编码器，设置编码预设、像素格式、编码器参数及最终输出的音频、字幕、附件和容器。
2. 在“质量 → 控制方式”选择“使用目标分数（ab-av1）”。
3. 在第二栏选择 VMAF 或 XPSNR，在第三栏填写目标分数，再设置最小/最大 CRF、可选采样数量、单段时长和彻底搜索；VMAF 模式还可设置模型，XPSNR 模式会隐藏这一行。
4. 像普通任务一样保存预设、添加文件并加入原生编码队列。
5. 可在“参数总览”或“预设管理”的选中预设预览中核对 AB-AV1 参数，并在右侧“命令行模板”核对完整搜索命令。
6. 启动队列。任务日志会先显示 CRF 搜索进度，成功后自动进入正式编码。

“VMAF 模型”支持：

- 留空：完全采用 ab-av1 的自动模型逻辑；
- 本地模型：点击“本地 JSON”，或输入 JSON 文件完整路径。

## 参数映射

| FFmpegFreeUI 设置 | ab-av1 参数 |
|---|---|
| 指标 VMAF + 目标分数 | `--min-vmaf value` |
| 指标 XPSNR + 目标分数 | `--min-xpsnr value`；允许负数，不传 VMAF 模型 |
| 非空的编码器名称 | 原样写入 `--encoder`；为空则整项省略 |
| 常规编码预设 | `--preset`；libaom/libvpx/rav1e 的具体 FFmpeg 参数由 ab-av1 映射 |
| AMF/Vulkan 的 3FUI 编码预设 | `--enc quality=value` |
| 像素格式 | 原样写入 `--pix-format` |
| profile、tune/usage、GPU、线程 | 每项一个 `--enc key=value` |
| 硬件解码、解码格式、解码线程和设备 | 每项一个 `--enc-input key=value` |
| `svtav1-params` 中的 `keyint` | `--keyint`，保留帧数或 `8s` 这类时长值 |
| `svtav1-params` 中的 `scd` | `--scd true/false`；SVT 的 `1/0` 只做等价布尔转换 |
| `svtav1-params` 中的 `preset` | `--preset`；若与结构化预设同时存在，按 3FUI 参数顺序使用后者 |
| `svtav1-params` 中的其他参数 | 按原顺序拆成多个 `--svt key=value`，名称和值不修正 |
| 进阶参数中的 `-preset` / `-pix_fmt` / `-vf` / `-filter:v` / 视频编码器别名 | 分别转为 `--preset` / `--pix-format` / `--vfilter` / `--encoder`，只输出最后有效值 |
| 其他编码器的进阶/自定义视频参数 | 每项一个 `--enc key=value`，不做编码器白名单修正 |
| 自定义视频滤镜 | `--vfilter` |

搜索命令不重复传入 ab-av1 自己掌控的 `crf`、`input-depth`、输入/覆盖标志和音频编码选项，否则 ab-av1 会在开始搜索前直接拒绝命令。这只影响搜索子进程：插件不会删除或改写 3FUI 保存的原预设，用户手写的冲突或错误值仍会原样进入最终 FFmpeg 命令并由用户负责。

搜索成功后，当前任务快照会进入单值质量模式并写入结果。质量参数名与 ab-av1 一致：NVENC 使用 `cq`，QSV 使用 `global_quality`，VAAPI 使用 `q`，Vulkan/rav1e/vvenc 使用 `qp`，`hevc_videotoolbox` 使用 `q:v`，其余编码器使用 `crf`。

## 构建

项目不依赖相邻的 FFmpegFreeUI 源码目录。Ext Plugin SDK `2.5.0` 和 LakeUI `3.23.0` 均通过 NuGet 固定版本获取，解析后的完整依赖版本记录在 `packages.lock.json` 中。SDK 包同时提供构建期合同和部署 targets；插件产物不会携带 SDK 或 LakeUI 的私有副本，运行时统一使用 3FUI 自带版本。

执行：

```powershell
dotnet restore .\FFmpegFreeUI.Ext.AbAv1.vbproj --locked-mode
dotnet build .\FFmpegFreeUI.Ext.AbAv1.vbproj -c Release --no-restore
```

插件产物位于：

```text
dist\FFmpegFreeUI.Ext.AbAv1.3fui.dll
```

运行轻量契约测试：

```powershell
dotnet restore .\tests\FFmpegFreeUI.Ext.AbAv1.Tests.vbproj --locked-mode
dotnet run --project .\tests\FFmpegFreeUI.Ext.AbAv1.Tests.vbproj -c Release --no-restore
```

需要主动升级 SDK、LakeUI 或其传递依赖时，先在项目文件中修改精确版本，再执行不带 `--locked-mode` 的 `dotnet restore`，检查并提交更新后的两个 `packages.lock.json`。

## 当前限制

- ab-av1 0.11.7 的 `--pix-format` 当前只接受其帮助中列出的格式；插件会原样传值，不会把 `p010le` 等值擅自改成别的格式。
- 3FUI 内置缩放、裁剪、帧率转换、插帧、降噪、锐化、字幕烧录、色彩转换、帧服务器、剪辑区间等尚未等价映射给 ab-av1；检测到这些设置时任务会在搜索前报错，避免采样处理链与最终编码不一致。
- 队列“停止”会通过取消令牌终止 ab-av1 及其 FFmpeg 子进程；Ext API 当前没有把原生“暂停/恢复”事件开放给准备阶段处理器，因此搜索阶段不响应暂停，进入正式编码后恢复原生暂停行为。

## 许可证

插件代码使用 MIT，详见 [LICENSE](./LICENSE)；随发布目录提供的 ab-av1 使用其上游 MIT 许可证，详见 [AB-AV1-LICENSE.txt](./AB-AV1-LICENSE.txt)。使用的LakeUI使用赞助许可证，许可证编号`SLA-260316-STWHFX2O`。
