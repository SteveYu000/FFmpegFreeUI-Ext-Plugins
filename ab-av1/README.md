# FFmpegFreeUI Ext AB-AV1 插件

本插件通过 FFmpegFreeUI Ext Plugin API，把 `ab-av1 crf-search` 直接接入原生“参数面板 → 质量”和原生编码队列。编码器不再限定为 SVT-AV1：插件会使用 3FUI 当前预设中实际选择的 FFmpeg 编码器。

选择“使用 VMAF 分数（ab-av1）”后，目标 VMAF、CRF 范围、采样数量、单段时长、彻底搜索和 VMAF 模型会随 FFmpegFreeUI v6 预设保存，并作为普通参数行显示在原生“参数总览”文本框中。原生“命令行模板”会在 FFmpeg 命令前显示对应的 `ab-av1 crf-search` 命令。任务开始时，插件先在 `ext.task.before-prepare` 阶段搜索 CRF；搜索成功后只修改该任务的预设快照，再由 3FUI 生成并执行正式 FFmpeg 命令。

## 原生集成方式

- `host.Ui.RegisterChoice`：向原生质量控制方式下拉框添加稳定选项，不直接修改控件的 `Items`。
- `IExtPluginUiContext.StateJson`：参数随 3FUI v6 预设保存和恢复，不使用插件自己的预设文件。
- `IExtPluginParameterPanelCatalog` + 原生控件锚点：以 `OrderedTransform` 资源租约装饰 `MTB_参数总览`，把目标 VMAF、最小/最大 CRF、采样、彻底搜索和模型直接追加为原生带行号文本；不创建额外总览面板，也不输出启用/未启用状态。
- `host.Commands.RegisterStepProvider`：仅在原生命令模板预览中贡献 `ab-av1 crf-search` 步骤。编码器、编码预设、像素格式、解码输入参数和自定义编码参数均来自当前预设；未选择编码器时不生成 `--encoder`，也不回退或补全为 `libsvtav1`。实际任务仍由准备阶段处理器执行一次搜索，避免重复运行。
- `ext.task.before-prepare`：在任务准备阶段执行可取消的 ab-av1 搜索，并将 CRF 写回当前任务快照。
- `ReportProgress` / `ReportResult`：搜索过程、最终 CRF、VMAF、预测视频流大小和预测编码时长进入原生任务日志。
- 原生编码队列：添加、开始、停止、重置、并发调度、输出路径、音频/字幕/附件及最终封装全部由 3FUI 负责；插件不提供第二套任务列表。

## 界面与渲染

- 参数区使用与当前 3FUI 相同的 LakeUI `3.23.0` 控件：`ModernTextBox`、`ModernComboBox`、`ModernButton`、`ModernCheckBox` 和 `HtmlColorLabel`。
- 插件通过 Ext API 的稳定锚点读取原生质量输入框和控制方式下拉框，继承字体、颜色、圆角、下拉层、SSAA 与动画帧率等公开样式；没有访问宿主私有字段。
- LakeUI 控件的 `BackgroundSource` 指向原生质量页，因此透明背景、个性化背景和宿主统一配置的 D2D/D3D 画刷、文字、位图及脏区缓存由同一套渲染路径处理。
- 单行输入框、下拉框和按钮沿用宿主的 `32 px` 设计高度，只由外层面板执行一次 DPI 缩放；字段列严格限制在宿主可用宽度内，避免 LakeUI 动态控件按首选尺寸再次撑高或横向越界。
- 搜索字段标题使用原生的灰色、底部对齐样式；“彻底搜索”位于同一个 `42 px` 控件行，其下边缘与左侧输入框对齐；模型行采用严格的 `10 px + 32 px` 控件高度，避免下拉框和按钮被裁切。
- 插件参数区移除了独占高度的校验状态空行，采用 `114 px` 紧凑设计高度；不修改质量页滚动状态，也不再改变原生进阶/滤镜输入框的最小高度。ab-av1 就绪状态显示在原生“质量值”输入框右侧。
- 创建和预设恢复期间暂停布局；模型列表使用批量更新；参数刷新按 120 ms 合并，避免每次按键都让 3FUI 重算整页参数。
- 根参数区启用 WinForms 双缓冲，且只在状态真正变化时改控件属性，减少重复布局和无效重绘。

## 安装要求

- FFmpegFreeUI API Extended Edition，Ext Plugin API `2.3.0` 或更高版本；建议使用与本项目对应的 `2.4.x` 宿主。
- Windows 10/11 和 .NET 10 Desktop Runtime。
- 宿主自带的 LakeUI `3.23.0`；不要向 `Plugin` 目录复制另一份 LakeUI 或 Vortice DLL。
- `ab-av1.exe`。
- 可供 ab-av1 调用、包含 `libvmaf` 和所选编码器的 FFmpeg。

安装目录：

```text
FFmpegFreeUI.exe
FFmpegFreeUI.Ext.PluginHost.dll
FFmpegFreeUI.Ext.PluginSdk.dll
Plugin\
├─ FFmpegFreeUI.Ext.AbAv1.3fui.dll
├─ ab-av1.exe
└─ AB-AV1-LICENSE.txt
```

插件启动的子进程会依次从插件目录、FFmpegFreeUI 根目录和系统 `PATH` 查找 FFmpeg。不要把另一份 `FFmpegFreeUI.Ext.PluginSdk.dll` 放进 `Plugin` 目录。

完全退出并重新启动 FFmpegFreeUI 后，打开“参数面板 → 质量”，即可在“控制方式”中看到“使用 VMAF 分数（ab-av1）”。
当前预设的 AB-AV1 参数会直接显示在“参数面板 → 参数总览”的原生带行号框内，不显示“已启用/未启用”字样；启用 VMAF 控制方式时，右侧原生“命令行模板”还会在 FFmpeg 命令前显示可复制的 ab-av1 搜索命令。即使暂时切回原生质量模式，已经保存的插件参数仍会保留。

命令行模板属于预览：插件只展示当前预设中确实存在并能映射到 ab-av1 的参数。编码器为空就省略 `--encoder`；编码器名、像素格式或附加参数即使拼写错误也保持原值，由 ab-av1/FFmpeg 在运行时诊断。真正加入队列后仍会检查无法等价采样的画面处理链，预览不会用虚构的“基础模板”替代用户设置。

## 使用

1. 在原生参数面板选择希望使用的 FFmpeg 视频编码器，设置编码预设、像素格式、编码器参数及最终输出的音频、字幕、附件和容器。
2. 在“质量 → 控制方式”选择“使用 VMAF 分数（ab-av1）”。
3. 设置目标 VMAF、最小/最大 CRF、可选采样数量、单段时长、彻底搜索和 VMAF 模型。
4. 像普通任务一样保存预设、添加文件并加入原生编码队列。
5. 可在“参数总览”核对 AB-AV1 参数，并在右侧“命令行模板”核对完整搜索命令。
6. 启动队列。任务日志会先显示 CRF 搜索进度，成功后自动进入正式编码。

“VMAF 模型”支持：

- 留空：完全采用 ab-av1 的自动模型逻辑；
- 内置模型：输入名称，或点击“扫描模型”；
- 本地模型：点击“本地 JSON”，或输入 JSON 文件完整路径。

## 参数映射

| FFmpegFreeUI 设置 | ab-av1 参数 |
|---|---|
| 非空的编码器名称 | 原样写入 `--encoder`；为空则整项省略 |
| 常规编码预设 | `--preset`；libaom/libvpx/rav1e 的具体 FFmpeg 参数由 ab-av1 映射 |
| AMF/Vulkan 的 3FUI 编码预设 | `--enc quality=value` |
| 像素格式 | 原样写入 `--pix-format` |
| profile、tune/usage、GPU、线程 | 每项一个 `--enc key=value` |
| 硬件解码、解码格式、解码线程和设备 | 每项一个 `--enc-input key=value` |
| `libsvtav1`/`svt-av1` 的 `svtav1-params` | 按原顺序拆成多个 `--svt key=value`，不改值 |
| 其他编码器的进阶/自定义视频参数 | 每项一个 `--enc key=value`，不做白名单修正 |
| 自定义视频滤镜 | `--vfilter` |

搜索成功后，当前任务快照会进入单值质量模式并写入结果。质量参数名与 ab-av1 一致：NVENC 使用 `cq`，QSV 使用 `global_quality`，VAAPI 使用 `q`，Vulkan/rav1e/vvenc 使用 `qp`，`hevc_videotoolbox` 使用 `q:v`，其余编码器使用 `crf`。插件不会改写用户手写的进阶参数；其中若另有 `crf/cq/qp/preset` 等冲突项，仍原样保留并由用户负责。

## 构建

当前项目按下面的相邻源码目录引用 Ext SDK：

```text
Code\
├─ FFmpegFreeUI-API-Extended-Edition\
└─ FFmpegFreeUI-Ext-Plugins\
   └─ ab-av1\
```

执行：

```powershell
dotnet restore .\FFmpegFreeUI.Ext.AbAv1.vbproj
dotnet build .\FFmpegFreeUI.Ext.AbAv1.vbproj -c Release --no-restore
```

插件产物位于：

```text
dist\FFmpegFreeUI.Ext.AbAv1.3fui.dll
```

运行轻量契约测试：

```powershell
dotnet restore .\tests\FFmpegFreeUI.Ext.AbAv1.Tests.vbproj
dotnet run --project .\tests\FFmpegFreeUI.Ext.AbAv1.Tests.vbproj -c Release --no-restore
```

发布目录中附带的 `ab-av1.exe` 版本为 `0.11.7`，SHA-256 为
`A3A9534D2ADECABC4861ACF632D0F64741F56E2050D52032A5307BEBB4FBDCC9`。真实冒烟测试除 SVT-AV1 外还覆盖了 `libx264` 与 `libx265`：同一段 3 秒视频、目标 VMAF 80 分别搜索到 CRF 23.9（VMAF 80.148）和 CRF 21.1（VMAF 80.364），并验证了 `--enc` 与编码器自有的小数搜索精度。

## 当前限制

- 只支持 FFmpegFreeUI v6 JSON 预设。编码器名称不设插件白名单，但是否可用、CRF 范围和参数组合仍取决于当前 ab-av1 与 FFmpeg 构建。
- ab-av1 0.11.7 的 `--pix-format` 当前只接受其帮助中列出的格式；插件会原样传值，不会把 `p010le` 等值擅自改成别的格式。
- 3FUI 内置缩放、裁剪、帧率转换、插帧、降噪、锐化、字幕烧录、色彩转换、帧服务器、剪辑区间等尚未等价映射给 ab-av1；检测到这些设置时任务会在搜索前报错，避免采样处理链与最终编码不一致。
- 自定义视频滤镜会通过 `--vfilter` 传入；改变尺寸、时间轴或帧率的复杂滤镜仍需人工核对。
- ab-av1 的预测大小只代表视频流，最终文件还包含音频、字幕和附件。
- 队列“停止”会通过取消令牌终止 ab-av1 及其 FFmpeg 子进程；Ext API 当前没有把原生“暂停/恢复”事件开放给准备阶段处理器，因此搜索阶段不响应暂停，进入正式编码后恢复原生暂停行为。

## 许可证

插件代码使用 MIT，详见 [LICENSE](./LICENSE)；随发布目录提供的 ab-av1 使用其上游 MIT 许可证，详见 [AB-AV1-LICENSE.txt](./AB-AV1-LICENSE.txt)。
