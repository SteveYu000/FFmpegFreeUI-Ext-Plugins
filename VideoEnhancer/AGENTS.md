# VideoEnhancer 工作约定

约定版本：1.0；更新日期：2026-10-21。适用于本仓库 `VideoEnhancer/` 及其子目录。遵守当前用户要求和运行环境的权限约束；用户的新要求可调整这里的既有约定。

新会话优先从本目录启动；若从仓库根目录启动，显式读取本文件及状态文件，确保子目录约定被加载。

## 开始工作

1. 先确认 Git 根目录、远程地址和实际目标目录，读取适用的 `AGENTS.md`、[PROJECT_STATE.md](PROJECT_STATE.md) 及相关源码。此处是 Ext 插件；同名的上游独立仓库只作参考，不能因终端工作目录不同而改错仓库。上游仓库是指[VideoEnhancer](https://github.com/maxzrb/VideoEnhancer/)。
2. 修改前检查分支、HEAD、暂存区、未提交及未跟踪文件，检查远程更新。使用 `git pull --ff-only` 同步；有本地改动时先核对差异，只在不会覆盖或重组现有工作的情况下快进。不能自动 stash、rebase、reset、清理或恢复用户文件。
3. 先简述目标、关键约束和已有改动。复杂任务给出简短分步安排；方向明确就继续实施，只有影响范围、实质冲突、缺失条件或不可逆选择需要用户决定。
4. `PROJECT_STATE.md` 是交接快照，需与当前源码、Git 和实际执行结果核对。里面的待办不会自动扩大本次授权范围。

## 协作习惯

- 使用简体中文，先说结论，解释实际变化、原因和证据。执行期间简短报告有意义的发现，不逐条播报工具调用。
- “帮我”“改成”“可以先……”按行动要求处理；“是什么”“为什么”“如何设计”先调查并回答，不自动实施无关重构。“继续”延续已有任务和已确认的选择。
- 已有授权持续有效；常规、可逆的实现选择自行判断。遇到阻碍说明具体来源和影响，继续能独立完成的部分，避免重复确认。
- UI、滚动残影等视觉问题先用 Computer Use 录屏或连续截图取证，再结合代码推测原因；说明是否也在 LakeUI 独立场景复现。静态代码检查、控件尺寸或模拟测试不能当作真实界面验证。
- 收到 Esc 中止信号后，先请求用户允许恢复控制；得到明确答复前停止窗口输入。工具不可用或批准超时要按实际错误报告，不能声称已完成控制或验证。
- 区分“已证实”“推测”“未验证”。最终交付简述改动、验证及限制，并说明 Git 状态；不用每完成一步就要求确认或提醒提交。

## 产品与处理链边界

- 前端 VB、后端 C# 可以分目录，发行自有托管代码合并为 `videoenhancer.ext.3fui.dll`。界面使用类型化 DLL 服务，不恢复插件 CLI 或为合并前组件保留参数输入入口。RVE/Python、RTX、FFmpeg 等外部程序仍使用其真实协议。
- 插件处理用户选中的视频流，将增强视频交回 Ext 现有处理链。保持原始输入为输入 0；最终编码、音频、字幕、附件、章节、元数据和后处理由宿主预设控制。不要接管整条宿主队列或全局替换 FFmpeg。
- 每个任务读取入队时的预设快照，不能读取后来改变的当前 UI 状态。用户参数须可保存进预设；解析和校验异常由插件转成可读提示，不冒泡为宿主未处理异常。
- 各功能开关直接控制；AI 全部关闭时参数总览不显示本插件。参数总览只呈现已启用功能和用户调整的参数项，不加入处理链说明、内部 JSON 或隐藏选项。
- 命令行模板显示完整、真实的后端参数；RTX 分别呈现服务启动命令和 HTTP JSON 请求，不能将请求伪装成 CLI 参数。运行日志显示实际执行值，不把预览说明混入执行参数。
- 保持现有 Ext SDK 接口、任务顺序和其他插件的组合能力。默认只修改本插件；宿主、SDK、上游仓库和其他插件不属于默认修改范围。新项目不新增旧宿主兼容层或旧组件迁移逻辑。
- 页面入口以 [README.md](README.md) 为准。采用宿主原生 LakeUI 的字体、半透明背景和竖排二级导航；模型列表复用 AI 增强页的名称解析与分类。不要恢复独立图片、右键超分或四宫格模块。
- 移植上游更新先核对版本和提交差异，保留 Ext 的处理链、入口和发布方式；不整树覆盖。

## 文件、依赖与许可

- 文件读写使用 UTF-8，保持已有 BOM 和换行风格；代码注释使用中文。PowerShell 读取中文前设为 UTF-8 输出，并用 `Get-Content -Encoding UTF8`；机械修改用 Python/Node.js，查找优先 `rg`。
- 项目引用和脚本使用相对路径、NuGet 或环境变量。不能引入开发机其他目录的固定引用，也不引用 `FFmpegFreeUI.dll`。本机路径只作为状态文件中的本地环境说明。
- 解压统一使用锁定版 7za，不重新引入 SharpCompress 或 RAR 支持。下载的原生依赖按锁文件校验哈希，缓存放在构建目录；不要将 DLL 直接提交进源码树。
- 许可边界以 [LICENSING.md](LICENSING.md)、[DEPENDENCIES-LICENSES.md](DEPENDENCIES-LICENSES.md) 和 `LICENSES/` 为准。新增或升级依赖需核实对应版本许可并补齐声明、源码义务。LakeUI 按用户已确认的赞助者许可处理，不写入许可凭据。

## 验证与交付

以下命令从 `VideoEnhancer/` 执行，更多入口见 README：

```powershell
dotnet restore Frontend/VideoEnhancerPlugin.vbproj --locked-mode
dotnet build Frontend/VideoEnhancerPlugin.vbproj -c Release --no-restore --nologo
dotnet run --project tests/VideoEnhancer.Tests.csproj -c Release
git diff --check
```

- 按改动选择验证：处理链、参数或协议改动运行相关语义回归；组合行为需检查真实宿主命令。Python 改动运行对应 Python 测试；安装、解压和发布逻辑改动运行分发及依赖测试。测试环境变量和适配条件见状态文件。
- 使用真实宿主测试时，`VIDEOENHANCER_TEST_HOST` 指向主程序输出目录。其 LakeUI 版本通过 `VideoEnhancerTestLakeUiVersion` 传入，并在 restore/build/run 中保持一致；不因此修改产品最低依赖。
- 等待验证进程真正结束，记录退出码、跳过项和警告。通过后不无故重复或扩展测试；纯文档改动检查内容、链接和 diff，不专门增加镜像式测试或运行全套 GPU 测试。
- 只有用户要求提交、推送、PR 或发布时，才执行相应动作；同一任务已有明确授权时直接完成。提交消息用中文，只暂存本次明确范围的文件。“提交后修改”先提交前一批，再开始后一批；不要擅自 amend 或改写历史。
- 发布使用 [release/Build-Zip.ps1](release/Build-Zip.ps1)，生成安装 ZIP、对应源码 ZIP 及各自 SHA-256，安装包附中文说明。保留源码构建安装；不恢复 EXE 安装器或分发安装包功能。
- 打包前确认 DLL 内嵌源码快照与当前源码一致，不能用旧 DLL 配新源码。版本、标签、提交、两包内容和源码获取信息须对应；发布后读取远程结果并验证资产。未经要求不改版本、不替换既有 Release。
- `AGENTS.md` 和 `PROJECT_STATE.md` 是仓库协作材料，不写入项目资源、程序集、运行界面或发行 ZIP，也不为管理记录改动产品构建配置。

## 状态维护

实质任务结束、目标或验证发生重要变化、交接工具/设备前，更新 [PROJECT_STATE.md](PROJECT_STATE.md) 的当前快照：在做什么、哪些已完成但未提交、验证证据、未解决问题和下一步。删除过时内容，避免重复源码和 Git 可直接查询的信息。

只维护这一份状态文件；不另建 STATUS、工作进度、HANDOFF、TODO、DECISIONS 等平行记录，不逐次追加工具流水账。不要记录密钥、许可凭据、用户媒体完整路径或大段原始日志。新会话重新核实本地环境，不将旧设备路径当作通用配置。

## 官方依据

2026-10-21 查阅：[OpenAI AGENTS.md 指南](https://learn.chatgpt.com/docs/agent-configuration/agents-md)、[Codex 工作流实践](https://developers.openai.com/cookbook/examples/codex/iterating-development-workflows-with-codex)。本文件采用按目录放置长期约定、提供必要上下文和验证入口的做法；`PROJECT_STATE.md` 的名称、维护方式及以上产品边界是本项目约定，非 Codex 强制文件结构。
