# VideoEnhancer 当前状态

更新日期：2026-11-10。交接快照，以源码、Git 和实际执行结果为准，不作逐次工作日志。

## 项目基线

| 项目 | 当前事实 |
| --- | --- |
| 代码位置 | FFmpegFreeUI-Ext-Plugins/VideoEnhancer；实际路径为 E:/DesktopPlus/Works/Code/FFmpegFreeUI-Ext-Plugins/VideoEnhancer |
| Git | SteveYu000/FFmpegFreeUI-Ext-Plugins，main，HEAD 1c1e877；已执行 pull --ff-only，与 origin/main 同步 |
| 工作区 | 58 个路径未提交，全部位于 VideoEnhancer；暂存区为空。开始时已有本状态文件和 tests/test_tensorrt_onnx_paths.py 两项改动，均保留 |
| 产品 | 0.3.0 / videoenhancer.ext.3fui.dll；本轮未改版本、提交、推送或发布 |
| 上游 | 最新正式版 [v1.3.17](https://github.com/maxzrb/VideoEnhancer/releases/tag/v1.3.17)；本轮核对并移植 main 至 83c5f1235c2bfbf43fe0f1fa4c01f03712d48e1b，收尾实时复核未变化 |
| 构建目标 | net10.0-windows10.0.17763.0；Ext SDK 2.5.0；LakeUI 编译和最低依赖固定为 5.117.0，支持兼容 5.x |
| 许可与分发 | 组合 DLL 为 AGPL-3.0-only，独立自有源码保留 MIT；安装 ZIP 和对应源码 ZIP 分开，管理文档不进入程序集或 ZIP |

本插件最近已提交的生产改动仍是 8682a3e；之后的 e9b77d5 / 1c1e877 属于 ab-av1，不能据其标题修改本插件版本。已发布的 VideoEnhancer/v0.3.0 资产没有替换，仓库 latest 的 ab-av1 版本也不是本插件版本。

## 已完成的本轮移植

用户要求移植上游与 Ext 相关更新，并希望保持 LakeUI 5.117。本轮覆盖 v1.3.16、v1.3.17 和发布后主线的后端下载去重；取消两处仅为 5.201 专有透明渲染优化服务的调用，不增加多版本兼容层，全部 AI 改动保留：

- 引入 RVE 2.4.1-dev17 配套的 16 个 RGB48 核心文件，内嵌于 DLL。SDR/HDR 主帧统一为 uint16；普通源、跨后端中间视频、分段切片与拼接采用 gbrp16le。Ext 的 rve-frame-backend.py 同步改为 16bit，没有增加独立图片或图片 CLI 模块。
- 同步 ONNX、PyTorch/TensorRT、NCNN 浮点超分、转场旁路、时序窗口读写等修订。保留 Ext 的中文 Engine 文件流加载修复；16 个核心文件与上游 AST 一致，差异仅为该额外修复及许可/来源注释。
- 默认 FP32，半精度开关显式传 float16。TensorRT 的 GRL、DAT2、AniToon-RPLKSRL 强制 FP32；转换关闭 TF32，新缓存指纹隔离旧策略，旧 Engine 不删除。RIFE 预构建按任务实际补帧精度执行。固定精度 ONNX / Engine 的内部精度仍由模型决定。
- NCNN RIFE 只有 8bit 字节接口，补帧菜单改为 CUDA / TensorRT，默认 CUDA；遗留的选择器空值回退也已更正。显式启用 NCNN 补帧的预设由插件返回可读诊断，不静默切换模型。
- RGB48 同步只接受配套基线；本插件管理标记为 .videoenhancer-rgb48-owner=ext，按哈希同步并在同进程串行化。独立高位深包按 rgb48-v1 和必要入口验证，保留其 Python 文件，顺序和分段也使用其自带入口。未知版本/协议或同步失败阻止推理；纯 RTX / FFmpeg 任务不因无关 RVE 同步失败而阻塞。
- 分段最近邻使用 zscale point 保留低位，要求 FFmpeg 包含 zscale。半精度控件增加 LakeUI 原生信息图标和说明，5.117 已支持这些属性；类型化预览图像源、像素读取、图像生命周期和现有透明布局也保留。两个纯布局容器不调用 5.201 才有的 TransparentRenderMode；不声称保留了该新渲染优化。
- 模型下载只显示一条基于渠道状态的后端更新项，隐藏后端历史归档；没有归档条目也能显示安装入口。参数总览继续只显示启用功能和用户调整项，完整真实参数保留在命令模板；默认 FP32 不增加总览说明。
- 更新使用教程、README、安装说明、许可范围、RVE 来源材料及 ZIP 的 LakeUI 最低版本元数据。

没有移植上游 CLI、独立图片页面、EXE 安装器或最终 SVT 编码兜底。宿主继续控制最终编码、音频、字幕、附件、章节、元数据和后处理；入队预设快照和其他插件组合方式保持原设计。v1.3.14 / v1.3.15 既有预览、ONNX TensorRT、HDR 保存回滚及 PR #16 中文路径解析修复保留。

## 本轮验证与产物

全部下述结果为 2026-11-10 本机执行：

- locked restore / Release build：0 警告、0 错误，退出码 0。
- 完整 C#：按实际 LakeUI 5.117 重新执行，517 项断言通过，包含真实 Ext 宿主命令、其他插件外挂字幕、RTX HTTP/管道、选帧、起始偏移与裁剪、参数异常、单 DLL 和清理，退出码 0。比先前 5.201 的 518 项少一项已取消渲染 API 对应断言。
- 此前同日的 Python test_*.py：43/43 通过，GPU 开关开启，无跳过，退出码 0。本次只调整 LakeUI 基线和界面调用，没有改后端或重跑 GPU。包括真实 TensorRT 构建/推理、FP16/FP32、中文路径、动态/固定 profile、分块补边、图内低倍率输出、旧不稳定 FP16 Engine 拒绝。
- FrameBackendTests.py：7/7 通过。RGB48 的 CPU/CUDA FP32 全 65,536 码值往返精确；普通 ONNX、全模型分段及 ONNX+最近邻混合分段实际执行 Ext 生成的计划，4,096 级样本逐码一致。时序窗口读取与写入也逐码一致。
- 独立后端所有权使用 C# 文件哈希夹具验证；实际处理链使用 Ext 管理的隔离 RVE 副本，不改用户后端安装。未验收完整独立包的全部推理组合、全部大模型、长视频、FlashVSR / BasicVSR++ 实模型或真实窗口视觉效果；RGB48 不代表未验证的 HDR 色彩路径已开放。
- 安装/源码双包：247 项许可与分发断言通过，独立 pwsh 进程退出码 0；安装包没有 Python 源码、工程文件或源码归档，源码快照与 DLL 对应，错误源码配旧 DLL 被拒绝。本轮只在忽略的验证目录生成包，没有上传或替换 dist 中已有发布 ZIP。
- git diff --check 通过；新增 16 个核心资源已在合并 DLL 内确认。没有 FFmpegFreeUI / SharpCompress 程序集引用，AGENTS / PROJECT_STATE 未进入源码快照。

DLL：dist/videoenhancer.ext.3fui.dll，0.3.0.0，SHA-256：
9237674359a3727caae2c579dcca610ad82b2ed6cb0f771263a4f00fa8a27ca2

内嵌和当前源码快照一致，171 个文件，SHA-256：
1acefe6bd366391ac96e077a188392c9e812dcaac5873a8809428538cae5d581

当前 5.117 日志与验证包位于 .test-tools/lakeui-5117/：full.log、restore.log、restore-locked.log、build.log、distribution.log、zip-build.log、packages/。此前同日的 Python/GPU 和帧模块日志在 .test-tools/upstream-sync-20261110/python-all.log、frame.log。验证安装 ZIP 哈希为 d3f34a6ba96caf66b6612ab9aad18534e16da8984a9a0460545a6705df3f39dd；验证源码 ZIP 为 8a3506ad795bb8d4221063dc5c26804b8092a1ebbb64e06ce93edb61ff85ffc0。未声明对应源码 ZIP 已执行完整重建；分发回归验证的是源码身份、清单、许可和拒绝不匹配行为。

上游参考 Git 在 .test-tools/upstream-review-20261103.git/，upstream-main=83c5f12；本轮固定导出为 .test-tools/upstream-20261110/，旧 v1.3.15 导出不是最新主线。

## 本地条件与测试入口

实际宿主输出目录：
E:/DesktopPlus/Works/Code/FFmpegFreeUI-API-Extended-Edition/FFmpegFreeUI/bin/Release/net10.0-windows10.0.26100.0

宿主为 6.3.0.1 / SDK 2.5.0 / LakeUI 5.117；本轮测试进程也使用 5.117，不要求宿主升级，没有替换安装文件。实际 DLL 的 LakeUI 引用为 5.117.0.0，PE 元数据确认没有 TransparentRenderMode 成员引用，安装元数据最低版本为 5.117.0。已确认 InfoIcon 和类型化预览接口在该版本存在；没有据此声称完成真实窗口视觉验收。

.NET SDK：11.0.100-rc.1.26425.128，产品目标仍为 .NET 10。真实测试 Python：
C:/Apps/FFmpegFreeUI API Extended/Plugin/videoenhancer/python/python/python.exe
其 Torch 2.9.0+cu130、ONNX 1.22.0、ONNX Runtime 1.29.0、TensorRT 10.12.0.36。GPU 为 RTX 5060 Laptop，8151 MiB，驱动 621.14。Python 日志有上游既有的 TF32 API 弃用、只读缓冲区及可选量化组件提示，测试均退出 0。

FFmpeg / FFprobe 使用 C:/Apps/ffmpeg/，已确认包含 zscale。相关环境变量为 VIDEOENHANCER_TEST_FFMPEG、VIDEOENHANCER_TEST_FFPROBE、VIDEOENHANCER_TEST_HOST。GPU 测试设置 VIDEOENHANCER_TEST_TRT=1，VIDEOENHANCER_TEST_RVE_BACKEND 指向隔离副本 .test-tools/upstream-sync-20261110/runtime/python/backend。不要把候选核心直接覆盖用户安装后再测试。

常用入口：
- dotnet restore Frontend/VideoEnhancerPlugin.vbproj --locked-mode
- dotnet build Frontend/VideoEnhancerPlugin.vbproj -c Release --no-restore
- dotnet run --project tests/VideoEnhancer.Tests.csproj -c Release
- python -B -m unittest discover -s tests -p "test_*.py" -v
- python -B tests/FrameBackendTests.py -v
- pwsh -NoProfile -File tests/DistributionLicenseTests.ps1 -Package <验证安装包> -SourcePackage <验证源码包>

## 尚未解决的问题

### ONNX 图片超分显存不足

已证实：RealHatGAN-JP-Illustration-2x-fix1，输入 1500×1061，FP32，UpscaleTileSize=0 整帧推理时，ONNX Runtime 请求单个 9,905,897,472 字节（约 9.23 GiB）缓冲区失败；用户显卡为 RTX 5060、8 GB。报错发生在模型推理，FFmpeg 写命令的红字本身不是失败依据。

已建议分块 256，仍不足则 128。固定 FP32 ONNX 不会因勾选半精度自动变成 FP16。本轮没有实现自动 OOM 降级，也未复测该大模型成功；RGB48 与小模型验证不能把此问题标记为已修复。

### Rendering 后日志可见但无法复制

用户反馈：“复制当前视图”缺少 BACKEND: Rendering 之后日志，选中文字 Ctrl+C 也无法复制。截图显示后续 ONNX 异常。问题未定位，本轮未改宿主或插件复制实现。

此前检查：宿主复制读取控件 .Text；LakeUI 5.112 的 getter 与 AppendLine 使用同一行集合，尚无缓存未同步证据。ANSI、控制字符和粘贴目标影响均未证实；只读剪贴板检查未捕获目标日志。此前 Computer Use native pipe 不可用，未完成真实窗口复制测试，等待记事本对照结果。本次没有做日志复制的真实窗口复测。

## 后续接手

本轮移植与验证已完成，全部 58 项改动未提交，版本保持 0.3.0；没有自动提交、推送或发布授权。用户若要求提交，使用中文消息并只暂存本插件明确范围。若要求发布，重新核对当前源码快照、宿主最低 LakeUI、版本和两包来源，再操作远程 Release；本地验证包不是已发布资产。
