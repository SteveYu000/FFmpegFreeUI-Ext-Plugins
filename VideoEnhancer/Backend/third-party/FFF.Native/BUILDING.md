# 原生库源码、构建与替换

同版本独立源码包中的 `third-party-sources` 提供 FFF_Project API 11 标签源码、八个配套库的版本源码和完整 vcpkg baseline；源码包下载地址及哈希见安装包的 `licenses/VideoEnhancer/SOURCE.txt`。`../third-party-sources.lock.json` 列出版本、官方下载地址和校验值。每个库的版权与条款均保留在源码归档内，各自的许可目录也提供原始正文。

## 构建接口兼容的 FriBidi

1. 解压 `vcpkg-e03dc9b-source.tar.gz`。该提交的 `ports/fribidi` 包含 1.0.16 的端口、Meson 构建参数及 `meson-crosscompile.patch`；`scripts` 和 `triplets/x64-windows.cmake` 也在同一源码归档中。
2. 安装 Windows C/C++ 编译工具、Windows SDK 和 PowerShell，进入解压后的 vcpkg 目录，执行 `./bootstrap-vcpkg.bat`。
3. 执行 `./vcpkg.exe install fribidi:x64-windows --classic`，使用该 checkout 的动态库 triplet。将源码包中的 `third-party-sources/fribidi-1.0.16-source.tar.gz` 放入 vcpkg 的 `downloads`，按 `ports/fribidi/portfile.cmake` 中的文件名命名，可以复用同版源码；不提供缓存时由 vcpkg 下载相同 SHA-512 的源文件。
4. 对 FriBidi 自定义修改，应从其源码归档和该端口的补丁出发，保留原声明；在自己的 vcpkg overlay 中应用修改后再编译。
5. 关闭宿主，备份原 `Plugin/videoenhancer/bin/fff-native-11/fribidi-0.dll`，将 Release 输出中接口兼容的 `fribidi-0.dll` 放到该位置，重启宿主检查字幕预览。

插件运行时直接加载这些 DLL，不对用户替换后的原生文件强制检查发行哈希。源码下载和发行哈希检查只发生在插件构建、打包过程中。本项目不限制修改库或为调试这些修改而进行的逆向工程；具体权利以 LGPL 原文为准。

## 其他配套库与 FFF.Native

各版本源码及对应 vcpkg 端口、补丁、通用构建脚本均已附上。FreeType 按 FTL 使用，其源归档来自官方 GitHub 镜像的 `VER-2-14-3` 标签，vcpkg 端口的 GitLab 压缩包哈希与镜像压缩包不同；使用本包源码时可建立 overlay 端口并按锁定清单更新归档路径和哈希。

FFF_Project 根 `vcpkg.json` 固定同一个 baseline 和 libass。其 C++ 工程使用 x64 Windows、MSVC `v145` 工具集及 Windows SDK；`FFF.Native/FFF.Native.vcxproj` 定义了包括头文件、导入库和 delay-load 的链接配置。libass 的安装位置为 `third_party/vcpkg_installed/x64-windows`，FFmpeg 开发文件位置为 `third_party/ffmpeg/include`、`third_party/ffmpeg/lib/x64`。构建时应使用 ABI 兼容的 FFmpeg 开发文件，并核对该 FFmpeg 构建的许可。完整工程、安装和编译相关脚本保留在 FFF_Project 源码归档中。

官方二进制的所有编译器版本、额外参数和完整编译日志未公开。上述材料提供已验证版本的源码及构建配方，不保证得到逐字节相同的官方 DLL。发布自己修改过的库时，应同时提供相应源码、补丁和实际构建记录。
