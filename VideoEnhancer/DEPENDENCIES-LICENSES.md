# VideoEnhancer 0.1.0 依赖与许可证

发行的插件 DLL 采用 AGPL-3.0-only；根目录 MIT 声明继续用于独立源码。文件范围和再分发要求见 [LICENSING.md](LICENSING.md)。第三方组件保持原授权。LakeUI 按维护者的赞助者许可使用，由宿主提供，本文件不审查其开源许可。

## 插件与 RVE 集成

`Backend/BackendServices.cs` 中包含 RVE 源码适配及补丁；`Backend/embedded-tools/*.py` 直接导入或启动 RVE。两部分明确标为 AGPL-3.0-only。其余独立前后端源码、脚本、测试与文档保留 MIT。合并后的 DLL 按 AGPL 分发，并随 ZIP 附上完整插件对应源码、逐文件 SHA-256 清单、许可和构建材料。

RVE 原始许可与参考源码固定于提交 `8541c46ca14e6f84fc86c5b9d3b75373e25732e5`，见 [上游源码](https://github.com/TNTwise/REAL-Video-Enhancer/tree/8541c46ca14e6f84fc86c5b9d3b75373e25732e5)。归档包含上游各架构目录自己的许可，不能将权重或所有架构都统一标为 AGPL。参考归档不是按需 Python 包的精确版本证明。

## 托管与构建依赖

版本由前后端 `packages.lock.json` 固定；许可依据实际 NuGet 包。SDK、LakeUI 和其传递组件由宿主提供，不合并、不随 ZIP 复制；项目不引用 FFmpegFreeUI.dll。

| 依赖 | 版本 | 使用方式 | 许可 |
| --- | --- | --- | --- |
| FFmpegFreeUI.Ext.PluginSdk | 2.5.0 | 宿主提供 | MIT |
| ILRepack.Lib.MSBuild.Task | 2.0.48 | 仅构建 | MIT |
| Vortice.Direct2D1 / Direct3D11 / DirectComposition / DirectX / DXGI | 3.8.3 | LakeUI 传递依赖、宿主提供 | MIT |
| Vortice.Mathematics | 2.1.0 | 宿主提供 | MIT |
| SharpGen.Runtime / COM | 2.4.2-beta | 宿主提供 | MIT |

来源：[Ext SDK](https://github.com/SteveYu000/FFmpegFreeUI-API-Extended-Edition)、[ILRepack](https://github.com/ravibpatel/ILRepack.Lib.MSBuild.Task)、[Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)、[Vortice.Mathematics](https://github.com/amerkoleci/Vortice.Mathematics)、[SharpGenTools](https://github.com/SharpGenTools/SharpGenTools)。

## 随包独立工具

| 组件 | 版本 | 许可 | ZIP 中的材料 |
| --- | --- | --- | --- |
| aria2-next | 2.8.3 | GPL-2.0-or-later | COPYING、AUTHORS、依赖声明、SOURCE.txt、同版完整源码 |
| 7-Zip Extra x64 7za.exe | 26.03 | LGPL-2.1-or-later，部分 BSD | License.txt、SOURCE.txt、同版完整源码；这些材料及程序也内嵌于 DLL |

7za 负责 ZIP、7z、TAR、GZ、XZ、ZST 的安装及模型导入解压，压缩 TAR 会继续解包；不支持 RAR。二者通过独立进程执行。原始版权、精确二进制和源码校验信息见 `licenses/aria2-next` 与 `licenses/7zip`；这些许可证不被插件许可取代。

## 原生预览库

构建下载官方 [FFF.Player 2026.8.18](https://github.com/Lake1059/FFF_Project/releases/tag/2026.8.18)，只读取其中十个原生 DLL，不运行播放器、不将播放器随包分发。官方下载包、每个 DLL 的大小和 SHA-256 固定在 `release/fff-native.lock.json`。ZIP 目录为 `videoenhancer/bin/fff-native-11`，均为可替换的独立 DLL。

FFF_Project 标签提交为 `72d3406738413262d2a99b8fc2a678dfdbf31364`，实际二进制接口为 API 11。配套库的版本已通过 DLL 版本导出确认，与该源码中 vcpkg baseline `e03dc9b29710050cd1018bc5674688108658d327` 一致。

| 组件与文件 | 已确认版本 | 选择的许可 | 随包原文 |
| --- | --- | --- | --- |
| FFF.Native.dll | 2026.8.18 / API 11 | MIT | FFF.Native/LICENSE.txt |
| libass / ass-9.dll | 0.17.4 | ISC | libass/COPYING |
| Brotli / brotlicommon.dll、brotlidec.dll | 1.2.0 | MIT | Brotli/LICENSE |
| bzip2 / bz2.dll | 1.0.8 | bzip2-1.0.6 | bzip2/LICENSE |
| FreeType / freetype.dll | 2.14.3 | FTL（上游还提供 GPL 分支） | FreeType/LICENSE.TXT、FTL.TXT、GPLv2.TXT |
| FriBidi / fribidi-0.dll | 1.0.16 | LGPL-2.1-or-later | FriBidi/COPYING、AUTHORS |
| HarfBuzz / harfbuzz.dll | 14.2.0 | MIT-Modern-Variant | HarfBuzz/COPYING |
| libpng / libpng16.dll | 1.6.58 | libpng-2.0 | libpng/LICENSE |
| zlib / z.dll | 1.3.2 | Zlib | zlib/LICENSE |

本软件部分功能基于 FreeType Team 的工作，按 FTL 使用 FreeType；项目网址为 https://freetype.org/。版权、免责声明和条款随原始 LICENSE.TXT 与 FTL.TXT 保留。FFF_Project 原许可证版权行含占位文本，原样保留，未擅自补写。

完整版本源码、vcpkg 端口补丁和构建脚本归档随 ZIP 的 `licenses/sources` 提供。SHA-256、SHA-512 与下载地址固定在 `third-party-sources.lock.json`。libass、Brotli、bzip2、FriBidi、HarfBuzz、libpng、zlib 的源码归档 SHA-512 与固定端口一致；FreeType 使用其官方 GitHub 镜像的同版标签源码，归档哈希与 GitLab 的打包格式不同。

FriBidi 的 LGPL 正文、作者声明、1.0.16 完整源码及端口补丁已附上；用户可构建并替换接口兼容的 DLL。构建与替换步骤见 `licenses/FFF.Native/BUILDING.md`。上游未提供该官方二进制的完整编译日志或编译器精确版本；现有材料不声称已复现逐字节相同的二进制。

FFF.Native 的 MIT 不覆盖配套库和另行提供的 FFmpeg Shared 构建。本插件 ZIP 不包含 avcodec、avformat 等 FFmpeg DLL；实际 Shared 构建应依其配置核对许可。[FFmpeg 官方许可说明](https://ffmpeg.org/legal.html)

## 按需安装的环境与模型

以下环境不在初始 ZIP 中，由下载页按所选源安装。源码、许可及再分发范围应与每个实际归档关联；本插件的许可不能授予这些组件的额外权利。

| 组件 | 许可边界 |
| --- | --- |
| RVE / Python 便携环境 | RVE 主项目 AGPLv3；Python PSF；架构、Python 包和静态依赖各自授权。保留实际包中的原始声明，并关联源码、版本和本插件修改 |
| RTXHDR-RTXVSR sidecar | Zennmn / maxzrb 应用代码 MIT；NVIDIA RTX Video SDK DLL 专有，不能用 MIT 替代其许可 |
| FFmpeg / FFprobe / 动态库 | 构建配置决定 LGPL 或 GPL；启用 nonfree 的构建不可再分发 |
| NumPy / NCNN / PyTorch | 主项目通常 BSD，实际包与其第三方部分分别核对 |
| ONNX / ONNX Runtime | 主项目 MIT，实际包的第三方部分分别核对 |
| TensorRT | 开源源码与 NVIDIA 二进制运行时条款不同，不能用仓库的 Apache-2.0 替代二进制许可 |
| 模型权重与扩展架构 | 每个模型单独确认作者、版本、许可及用途限制；可能包含非商业条款 |

RTX 边界见 [THIRD_PARTY.md](https://github.com/Zennmn/RTXHDR-RTXVSR/blob/main/THIRD_PARTY.md)、[DISTRIBUTION_TERMS.txt](https://github.com/Zennmn/RTXHDR-RTXVSR/blob/main/DISTRIBUTION_TERMS.txt) 和 [NVIDIA SDK 许可](https://developer.nvidia.com/downloads/nvidia-rtx-sdks-license-23jan2023pdf)。目前不将未锁定的便携环境和全部模型宣称为已完成逐版本许可审查。
