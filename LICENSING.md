# FFmpegFreeUI-Ext-Plugins 许可证说明

本仓库收录多个 FFmpegFreeUI API Extended Edition 插件，采用按插件、源文件和第三方组件分别授权的方式。复用源码或分发安装包时，应确认所使用内容的具体授权范围。

## 授权范围

| 内容 | 许可证及适用范围 | 许可正文与详细说明 |
| --- | --- | --- |
| 根目录自有公共文档 `README.md`、`LICENSING.md` | MIT；仅适用于这两个文档 | [MIT 正文及维护者版权声明](./ab-av1/LICENSE) |
| `ab-av1/` 的自有插件源码及插件 DLL | MIT | [插件许可证](./ab-av1/LICENSE)、[插件说明](./ab-av1/README.md#许可证) |
| ab-av1 上游程序 | 上游 MIT；保留 Alex Butler 的版权与原始声明 | [AB-AV1-LICENSE.txt](./ab-av1/AB-AV1-LICENSE.txt) |
| VideoEnhancer 的独立自有源码、脚本和文档 | MIT；具体文件范围按该插件的许可范围说明确认 | [MIT 正文](./VideoEnhancer/LICENSE)、[许可范围](./VideoEnhancer/LICENSING.md) |
| `VideoEnhancer/Backend/BackendServices.cs`、`VideoEnhancer/Backend/embedded-tools/*.py` | AGPL-3.0-only；包含 RVE 集成、适配与补丁 | [AGPL 正文](./VideoEnhancer/LICENSES/AGPL-3.0-only.txt)、[许可范围](./VideoEnhancer/LICENSING.md) |
| VideoEnhancer 合并发布的 `videoenhancer.ext.3fui.dll` | AGPL-3.0-only；组合产物包含上述 AGPL 部分 | [许可范围与对应源码要求](./VideoEnhancer/LICENSING.md) |
| 第三方源码、程序、动态库、运行环境和模型 | 各自的原始许可证或专有条款 | 组件原始声明及各插件的依赖说明 |

VideoEnhancer 的 MIT 源码授权与其组合 DLL 的 AGPL 授权适用范围不同。使用时应结合 [LICENSING.md](./VideoEnhancer/LICENSING.md) 确认文件范围，并保留相应的版权声明。

独立作品在同一仓库或安装包中并列分发，按各自的许可证处理；复制、编译合并或链接代码时，还须核对组合方式及许可证兼容性。依据见 [GNU 关于并列分发与组合程序的说明](https://www.gnu.org/licenses/gpl-faq.en.html#MereAggregation)。

## 第三方组件与宿主

第三方许可须与实际分发的组件、版本和构建对应。随包分发、按需下载、宿主提供及仅用于构建的组件，应在插件的依赖说明中分别列明，并提供对应的原始声明及适用的源码材料。

VideoEnhancer 的 RVE、aria2-next、7za、FFF.Native 及配套动态库、RTX 组件和模型的具体边界，见 [依赖与许可证](./VideoEnhancer/DEPENDENCIES-LICENSES.md) 和 [第三方声明](./VideoEnhancer/Backend/THIRD-PARTY-NOTICES.txt)。FFmpeg 应按实际构建配置核对授权，模型权重及 NVIDIA 二进制组件也须按各自条款处理。

FFmpegFreeUI 宿主及其 SDK 的授权独立处理。LakeUI 按维护者的赞助者许可使用，具体范围以相应授权约定为准；本仓库的 MIT 或 AGPL 声明不转授该赞助者许可。

## 复用与再分发

- 复用或分发 MIT 内容时，保留对应的版权和许可声明，包括适用的免责条款。具体权利和条件见 [MIT 正文](https://opensource.org/license/mit)。
- 分发 VideoEnhancer 组合 DLL 时，保留 AGPL 正文及相关声明，并依该许可证提供与二进制对应的完整源码、构建和安装材料。具体履行方式见插件的 [对应源码与再分发说明](./VideoEnhancer/LICENSING.md#对应源码与再分发)。
- 将 AGPL 程序或组合产物的修改版用于网络交互时，依 AGPL 第 13 条向远程交互用户醒目提供免费获取对应源码的入口。具体条件见 [AGPL 正文](./VideoEnhancer/LICENSES/AGPL-3.0-only.txt)。
- 打包第三方组件时，按其条款保留原始声明，并满足相应的源码提供、可替换动态库或其他再分发要求。插件许可证不能代替第三方许可证。

## 贡献与维护要求

1. 提交者应具有提交相应内容并按适用许可证授权的权利。新增原创内容沿用其所属文件范围已明确适用的许可证；引入不同授权时，在提交中说明来源、授权范围及原因。
2. 新增插件目录应提供自己的 `LICENSE`。自有源码、集成代码、第三方组件或发行产物采用不同许可时，应另附 `LICENSING.md` 明确范围，并更新本说明的授权表和根目录 README。
3. 引入或修改第三方代码时，保留作者、版权及原始许可证，记录可核对的来源和版本，按适用条款标明修改；源文件可使用 SPDX 标识，但标识必须与实际授权一致。
4. 新增或升级依赖时，同步核对依赖声明、锁定清单、随包许可和对应源码。按需下载的组件也应说明其授权来源，避免把插件的许可直接套用到模型或运行环境。
5. 发布前核对源码文件声明、插件许可范围说明、项目包许可元数据及发行材料。发现不一致时，先明确并修正授权范围，再发布；未明确授权范围的内容应先补充说明。

本说明用于整理仓库的许可范围和维护要求。具体使用权利与义务以相应许可证正文、有效版权声明及权利人的授权约定为准。
