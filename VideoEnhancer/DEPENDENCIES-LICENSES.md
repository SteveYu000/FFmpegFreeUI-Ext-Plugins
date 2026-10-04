# VideoEnhancer 0.1.0 依赖与许可证

本文件说明源码和 ZIP 中的依赖边界。根目录 `LICENSE` 保留继承项目的 MIT 声明，第三方代码、工具、运行组件和模型分别按原许可证授权。LakeUI 按项目维护者的赞助者许可使用，本次不审查其开源许可，也不据此选择本项目许可证。

## 自有代码的许可证建议

自有、可独立使用的 C# / VB 代码可以继续采用 MIT；已确认的 SDK、SharpCompress 和构建工具没有要求将这些代码改为 GPL。继承的 MIT 版权与许可声明必须保留。

RVE 集成需要另行界定：本项目的 Python 包装器导入 RVE 模块，`BackendServices.cs` 还包含修改 RVE 源文件的补丁文本。RVE 及其派生部分应遵守上游 AGPLv3，不能因为通过 Python 子进程执行就将其全部认定为 MIT。[RVE 许可证](https://github.com/TNTwise/REAL-Video-Enhancer/blob/v2-main/LICENSE)

这是基于当前代码结构的许可边界判断，尚未逐段确定补丁的版权来源。若希望对自有代码采用一个覆盖 RVE 派生部分的统一开源许可证，AGPLv3 是应考虑的候选；最终选择仍需确认代码来源及原生组件授权。本次没有修改根许可证，也没有把 ZIP 整体标为 MIT。独立进程和通信协议可以帮助划分作品边界，但不能替代对实际代码与交互方式的判断。[GNU 说明](https://www.gnu.org/licenses/gpl-faq.en.html#MereAggregation)

## 托管项目与构建依赖

版本来自 `Frontend/packages.lock.json`、`Backend/packages.lock.json`；许可来自对应 NuGet 包的 nuspec 和许可文件。

| 依赖 | 版本 | 使用与发布方式 | 许可 |
| --- | --- | --- | --- |
| FFmpegFreeUI.Ext.PluginSdk | 2.5.0 | 前后端编译引用，运行时由 Ext 宿主提供；不合并、不随 ZIP 复制 | MIT |
| SharpCompress | 0.50.3 | 合并进入插件 DLL，提供压缩包处理 | MIT |
| ILRepack.Lib.MSBuild.Task | 2.0.48 | 构建时合并程序集，不作为插件运行组件发布 | MIT |
| Vortice.Direct2D1 / Direct3D11 / DirectComposition / DirectX / DXGI | 3.8.3 | LakeUI 的传递依赖，由宿主提供，不合并、不随 ZIP 复制 | MIT |
| Vortice.Mathematics | 2.1.0 | 同上 | MIT |
| SharpGen.Runtime / SharpGen.Runtime.COM | 2.4.2-beta | 同上 | MIT |

项目来源：[Ext SDK](https://github.com/SteveYu000/FFmpegFreeUI-API-Extended-Edition)、[SharpCompress](https://github.com/adamhathcock/sharpcompress)、[ILRepack 构建任务](https://github.com/ravibpatel/ILRepack.Lib.MSBuild.Task)、[Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)、[Vortice.Mathematics](https://github.com/amerkoleci/Vortice.Mathematics)、[SharpGenTools](https://github.com/SharpGenTools/SharpGenTools)。

项目只引用仓库内的 Frontend / Backend；未引用 FFmpegFreeUI.dll，也没有指向开发机其他项目的构建引用。

## 随 ZIP 分发的独立工具

| 组件 | 版本 | 许可 | 随包材料 |
| --- | --- | --- | --- |
| aria2-next | 2.8.3 | GPL-2.0-or-later | COPYING、AUTHORS、依赖声明、二进制和源码校验信息、同版完整源码 |
| 7-Zip Extra 的 x64 7za.exe | 26.03 | LGPL-2.1-or-later，部分代码 BSD | 上游 License.txt、SOURCE.txt、同版完整源码；二进制和材料也内嵌于 DLL |

aria2-next 通过独立进程执行下载，7za.exe 通过独立进程解压。它们的许可与源代码义务分别履行；并列打包本身不会将自有代码自动改为相同许可。具体构建来源和 SHA-256 见 `Backend/third-party/aria2-next/SOURCE.txt`、`Backend/third-party/7zip/SOURCE.txt`。

## 按需安装的 AI 运行组件

这些组件由模型下载页安装，不包含在初始插件 ZIP 的运行环境中。下载目录名称或日期不能证明二进制对应哪个源码提交。

| 组件 | 上游许可与边界 | 发布或安装时应提供的材料 |
| --- | --- | --- |
| REAL Video Enhancer / RVE | 主项目 AGPLv3；模型架构目录可能保留各自许可 | 与实际 Python 包对应的源码、修改后的文件、许可与版权声明 |
| RTXHDR-RTXVSR sidecar（Zennmn 上游 / maxzrb 分支） | 应用代码 MIT；第三方组件各自授权 | 对应分支、提交、构建说明和第三方声明 |
| NVIDIA RTX Video SDK DLL | NVIDIA 专有许可，不能以 sidecar 的 MIT 代替 | 实际 SDK 版本的许可及获准再分发范围 |
| FFmpeg / FFprobe / FFmpeg 动态库 | 默认 LGPL-2.1-or-later；启用 GPL 组件后的构建适用 GPL；nonfree 构建不可再分发 | 对应构建配置、源码与许可；不能只凭文件名判断 |
| Python / NumPy | PSF / BSD-3-Clause，其他依赖各自授权 | 与便携环境实际版本一致的许可材料 |
| NCNN / PyTorch / ONNX / ONNX Runtime | 通常分别为 BSD-3-Clause / BSD-3-Clause / MIT / MIT | 实际安装版本和其中第三方组件的声明 |
| TensorRT | 开源源码与 NVIDIA 二进制运行时的许可有区别 | 不能用开源仓库的 Apache-2.0 替代 NVIDIA 二进制许可 |
| 模型权重、扩展架构 | 按每个模型的来源授权，可能有非商业限制 | 原作者、来源、版本、许可与用途限制 |

RVE 主项目参考源码：[v2-main](https://github.com/TNTwise/REAL-Video-Enhancer/tree/v2-main)。审查时参考的提交为 `8541c46ca14e6f84fc86c5b9d3b75373e25732e5`，该提交不表示当前下载归档必然由它构建。

RTX 的 MIT 许可不覆盖 nvngx_vsr.dll、nvngx_truehdr.dll 和配套 FFmpeg 库。第三方边界见上游 [THIRD_PARTY.md](https://github.com/Zennmn/RTXHDR-RTXVSR/blob/main/THIRD_PARTY.md)、[DISTRIBUTION_TERMS.txt](https://github.com/Zennmn/RTXHDR-RTXVSR/blob/main/DISTRIBUTION_TERMS.txt)，以及 [NVIDIA RTX SDK 许可](https://developer.nvidia.com/downloads/nvidia-rtx-sdks-license-23jan2023pdf)。FFmpeg 构建许可判断见 [FFmpeg 官方说明](https://ffmpeg.org/legal.html)。

本次没有将所有便携 Python 依赖或全部下载模型认定为已完成逐版本审查；下载源仍需要把许可证和精确版本与归档关联。

## 内嵌原生预览组件：来源尚未核实

`Frontend/EmbeddedFffNativePayload.vb` 内嵌以下二进制，预览时释放到插件数据目录。仓库没有提供 FFF.Native 源码、构建记录或对应许可证，也没有给配套库提供精确来源记录。这里列出的上游许可仅用于定位，不能证明内嵌二进制的构建授权。

| 文件 | 上游通常适用的许可 | 当前 SHA-256 |
| --- | --- | --- |
| FFF.Native.dll | 未知，需要作者、源码和授权来源 | `1b196833b06141ad227a37e78673dbb51077754959255dfc91e2340056f990d0` |
| ass-9.dll | libass：ISC | `c2571f763f2e768cb19d7b5c26746c55b8a70ed74615fc484a14a1123c084d2b` |
| brotlicommon.dll | Brotli：MIT | `115071a35873940b559f98ebadec848f4364cb65444ef56f0312e403649ea52f` |
| brotlidec.dll | Brotli：MIT | `6ebb5aa59b70e5e0ae8d4aec3fca4c97eea700d50c5ddf6c7b7dca22a56daf4a` |
| bz2.dll | bzip2 / libbzip2 许可（BSD 风格） | `6429c92fbd2fbef5500456c560f59079bf4c9c5526791d177a6f75fdefe68d96` |
| freetype.dll | FreeType：FTL 或 GPL；文件版本为 2.14.3 | `3e3bf35bd53063eab5b13696c1f50a51b16a88b420e397de71a95ad5d13255ee` |
| fribidi-0.dll | FriBidi：LGPL-2.1-or-later | `7c3aac2c402247c94afb17d3953ce4ec3d229eb3eaf53904b6850e6ce880cd22` |
| harfbuzz.dll | HarfBuzz：MIT 风格，子目录可能有其他许可 | `ecafbf886d77a8ddb3746d3ddeff86a995540eef7bde2481c01c5b1d6fe948f6` |
| libpng16.dll | libpng 许可 | `a8702e4f5bc266c727e5cfe5daf701aa79009eadea6fee0c1c20fe449a052f62` |
| z.dll | zlib：Zlib；文件版本为 1.3.2 | `8f964ddce3a8a421265b0701a7a6211adb19a8fd362e44b6bc9bdf662b7484d5` |

上游声明：[libass](https://github.com/libass/libass/blob/master/COPYING)、[Brotli](https://github.com/google/brotli/blob/master/LICENSE)、[bzip2](https://sourceware.org/bzip2/manual/manual.html)、[FreeType FTL](https://github.com/freetype/freetype/blob/master/docs/FTL.TXT)、[FriBidi](https://github.com/fribidi/fribidi/blob/master/COPYING)、[HarfBuzz](https://github.com/harfbuzz/harfbuzz/blob/main/COPYING)、[libpng](https://github.com/pnggroup/libpng/blob/libpng16/LICENSE)、[zlib](https://github.com/madler/zlib/blob/develop/LICENSE)。

其中 FriBidi 的 LGPL 义务需要针对实际构建补充对应源码和许可证；当前释放逻辑保留已有非空 DLL，允许替换。FFF.Native 是否静态包含其他库、是否动态加载特定 FFmpeg 构建，也需要源码和构建材料确认。来源未补齐前，不能把整个 ZIP 描述为许可材料齐全。
