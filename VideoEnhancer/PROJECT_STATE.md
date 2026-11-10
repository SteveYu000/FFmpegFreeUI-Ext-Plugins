# VideoEnhancer 当前状态

更新日期：2026-11-11。交接快照，以源码、Git 和实际验证结果为准。

## 当前发布状态

用户要求“更新版本为0.4并发布”，按现有三段版本、更新器识别和资产命名统一为 0.4.0。正式发布已完成：[VideoEnhancer Ext 0.4.0](https://github.com/SteveYu000/FFmpegFreeUI-Ext-Plugins/releases/tag/VideoEnhancer/v0.4.0)。

- 目标目录：FFmpegFreeUI-Ext-Plugins/VideoEnhancer；远程 SteveYu000/FFmpegFreeUI-Ext-Plugins，分支 main。
- 发布代码提交 c106ddbecad9c53234f2526dfedaa2af2f45382e，消息“更新 VideoEnhancer 至 0.4.0 并统一页面标题与按钮布局”；前一批移植提交为 3312b14，两者均已推送。
- 已推送带注释标签 VideoEnhancer/v0.4.0，指向上述发布代码提交；发布后仅更新本状态文件，不移动标签或重建已发布产物。当前分支与工作区以 Git 查询为准。
- 前后端、教程、README、安装说明和许可范围标题均为 0.4.0。最低和编译 LakeUI 保持 5.117.0，Ext SDK 2.5.0，目标 .NET 10。
- GitHub Release ID 440573820，正式版、非草稿，四个资产均已上传。没有修改其他插件或仓库全局 latest。
- 实际发行 DLL 的 PluginUpdater.FetchLatestManifestAsync 已从公开发布接口读取并识别 0.4.0，返回正确的安装包名称、字节数与 SHA-256。

## 发布内容与边界

已移植上游 v1.3.15 至 v1.3.17 及 main 83c5f12 的 Ext 相关内容：ONNX 到 TensorRT 自动/手动转换、模型尺寸约束、RGB48 / uint16 主帧、gbrp16le 中间视频、FP32 默认与显式半精度、TensorRT 不稳定模型精度限制和缓存隔离、中文路径、独立后端所有权、后端下载去重及 HDR 保存回滚。

本次 UI：两个标题复用“模型资源库”的 13 号组件；模型管理刷新/移除按钮位于标题右侧，尺寸与资源库一致；AI 增强页底线和无用创建函数移除。没有新增兼容层或采用 LakeUI 5.201 专有 API。

最终编码、音频、字幕、附件、章节、元数据和后处理仍由宿主预设控制；不恢复 CLI、独立图片/右键模块或 EXE 安装器。安装和对应源码 ZIP 分开发布，各附 SHA-256。

## 0.4.0 产物与验证

- 发布代码提交上执行 locked restore、Release 构建成功，0 警告、0 错误。
- C# 回归 513 个断言通过，包含当前 Ext 宿主真实命令与插件组合；许可与双包分发 247 个断言通过；原生依赖离线检查 18 项通过。各进程已正常结束、退出码 0。
- 安装 ZIP 与对应源码 ZIP 均通过 7za 完整性校验；安装包未夹带源码。管理文件不进入两包或程序集。
- DLL 为 0.4.0.0，信息版本 0.4.0+c106ddbecad9c53234f2526dfedaa2af2f45382e，引用 LakeUI 5.117.0.0；SHA-256 为 fdc1ee504b92e97966f9ea361f0dcf5164300a3a8dec7b20d3d86282d4974cde。
- DLL 内嵌与当前源码快照一致：171 个文件，身份 d391094e690e0341d10fdc91233744003b897e82ec8bdf93ff1d5cee4c469362。
- 安装包 VideoEnhancer-0.4.0-win-x64.zip：10,682,927 字节，SHA-256 a67051ab20987246330769ded505b2ba0b14d936f96f154a10493b8fff0efbf9。
- 源码包 VideoEnhancer-0.4.0-source.zip：98,526,536 字节，SHA-256 519146da0b460e553428437c9eee2f421f6f701d6375344fa237a77b56386d97。
- 已读取正式远程 Release，四个资产的服务器大小和 SHA-256 均与本地一致；两份远程 .sha256 文件已下载并逐字节核验。远程标签指向正确发布代码。

同一批 RVE 移植代码此前通过 Python 43 项及帧模块 7 项测试，含 RTX 5060 小模型真实 TensorRT 构建/推理；本轮版本打包没有重复全套 GPU 测试，未验收全部大模型和长视频。没有真实窗口截图复测：工具列表不含 Computer Use 所需 node_repl，逻辑布局回归不能当作视觉验收。

本轮日志、发布返回值、更新器结果和完整资产元数据位于忽略目录 .test-tools/release-0.4.0/，发行文件位于 dist/。

本机线索：宿主 6.3.0.1 / SDK 2.5.0 / LakeUI 5.117，输出目录为 E:/DesktopPlus/Works/Code/FFmpegFreeUI-API-Extended-Edition/FFmpegFreeUI/bin/Release/net10.0-windows10.0.26100.0。FFmpeg/FFprobe 位于 C:/Apps/ffmpeg/，通过 VIDEOENHANCER_TEST_FFMPEG / VIDEOENHANCER_TEST_FFPROBE 指定，宿主目录使用 VIDEOENHANCER_TEST_HOST。GPU 验证使用隔离 RVE 副本，不覆盖用户安装。

## 尚未解决的问题

### ONNX 图片超分显存不足

已证实：RealHatGAN-JP-Illustration-2x-fix1，输入 1500×1061，FP32，UpscaleTileSize=0 整帧推理时，ONNX Runtime 请求单个 9,905,897,472 字节（约 9.23 GiB）缓冲区失败；用户显卡为 RTX 5060、8 GB。报错发生在模型推理，FFmpeg 写命令的红字本身不是失败依据。

已建议分块 256，仍不足则 128。固定 FP32 ONNX 不会因勾选半精度自动变成 FP16。本轮没有实现自动 OOM 降级，也未复测该大模型成功；RGB48 与小模型验证不能把此问题标记为已修复。

### Rendering 后日志可见但无法复制

用户反馈：“复制当前视图”缺少 BACKEND: Rendering 之后日志，选中文字 Ctrl+C 也无法复制。截图显示后续 ONNX 异常。问题未定位，本轮未改宿主或插件复制实现。

此前检查：宿主复制读取控件 .Text；LakeUI 5.112 的 getter 与 AppendLine 使用同一行集合，尚无缓存未同步证据。ANSI、控制字符和粘贴目标影响均未证实；只读剪贴板检查未捕获目标日志。此前 Computer Use native pipe 不可用，未完成真实窗口复制测试，等待记事本对照结果。本次没有做日志复制的真实窗口复测。

## 后续工作

本轮发布没有待完成的产品改动。上述显存和日志复制问题仍需另行验证；不将其视为本次发布已修复内容。后续版本工作按用户新要求开展，不自动扩大范围。
