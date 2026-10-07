# VideoEnhancer 0.2.0 许可范围

发行的 `videoenhancer.ext.3fui.dll` 采用 **AGPL-3.0-only**。它包含 RVE 集成和源文件补丁；将这些代码与 MIT 部分编译、合并为一个 DLL 时，按 AGPL 第三版分发该组合。

根目录 `LICENSE` 和 `LICENSES/MIT.txt` 保留原 MIT 版权及许可原文，适用于下表中的独立源码。它们不表示整个 DLL、ZIP、AI 运行环境或模型均采用 MIT。AGPL 完整正文位于 `LICENSES/AGPL-3.0-only.txt`。

| 源码或组件 | 授权范围 |
| --- | --- |
| `Backend/BackendServices.cs` | AGPL-3.0-only；包含 RVE 源文件适配及补丁 |
| `Backend/embedded-tools/*.py` | AGPL-3.0-only；RVE 启动、帧管线、分段、模型检查与 TensorRT 准备模块 |
| `Frontend/**`、其他 `Backend/*.cs`、自有 JSON、测试、构建与安装脚本、文档 | MIT；保留原有授权，可单独复用 |
| `Backend/third-party/**`、第三方源码归档及二进制 | 按其原许可证，不能用本项目的 MIT 或 AGPL 代替 |
| 按需下载的 RVE、RTX、Python 环境、FFmpeg 和模型 | 按实际版本及各自条款，详见 `DEPENDENCIES-LICENSES.md` |

MIT 部分保留原授权；发行的组合仍须满足 AGPL 条款。源码版权声明不被替换；第三方作者声明随其原始源码和许可文件保留。RVE 集成文件用 SPDX 标明第三版，未替上游推定“或更新版本”的额外授权。

## 对应源码与再分发

正式发布生成两个独立 ZIP：`VideoEnhancer-<版本>-win-x64.zip` 安装包和 `VideoEnhancer-<版本>-source.zip` 对应源码包，各附 `.sha256` 校验文件。安装包保留许可证、原始声明，以及 `videoenhancer/licenses/VideoEnhancer/SOURCE.txt` 中的源码包名称、同一 Release 下载地址与 SHA-256；安装元数据记录相同信息。源码包的根目录提供本次构建使用的完整插件源码、`SOURCE-SNAPSHOT.json` 逐文件清单和构建说明，`source-manifest.json` 记录配套安装包名称及 DLL 哈希。

插件源码包含前后端、运行用 Python 模块、项目与锁定文件、构建和安装脚本、测试及许可材料；构建缓存、开发机目录和二进制输出不属于该源码快照。源码清单也内嵌在插件 DLL 中，打包时核对源码与 DLL，禁止将新源码配给旧 DLL。

第三方版本源码和 vcpkg 构建配方位于独立源码包的 `third-party-sources`，其中 `aria2-next` 与 `7zip` 子目录提供各自的完整源码，其余归档直接放在该目录下；下载地址和校验值见源码包的 `release/third-party-sources.lock.json` 及各组件来源说明。RVE 参考源码用于说明集成与补丁的来源，不能充当未锁定的按需运行环境的版本证明。第三方原生构建的证据与限制见 FFF.Native 的 `SOURCE.txt` 和 `BUILDING.md`。

网络发布和镜像时，应在同一 Release 免费提供两个包及校验文件，并保持源码下载地址有效。AGPL 第 6(d) 条允许对应源码单独下载，接收者无需同时下载源码；只保留仓库主页或不匹配的最新源码不符合本项目的发布流程。

再分发本插件 DLL 时，应同时保留 AGPL 正文、版权和许可声明，并按 AGPL 提供与二进制对应的源码及构建、安装材料。修改集成或补丁时应保留原声明并标注修改。若将修改版用于通过网络与用户交互的服务，还应履行 AGPL 第 13 条对应源码义务。

LakeUI 由宿主提供，按维护者的赞助者许可使用。

依据：[RVE 原始许可](https://github.com/TNTwise/REAL-Video-Enhancer/blob/8541c46ca14e6f84fc86c5b9d3b75373e25732e5/LICENSE)、[AGPL 第三版](https://www.gnu.org/licenses/agpl-3.0.html)、[GNU 对组合与并列分发的说明](https://www.gnu.org/licenses/gpl-faq.html#MereAggregation)。
