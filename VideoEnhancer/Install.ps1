param([string]$HostDirectory = $env:FFMPEGFREEUI_HOME, [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
if ([string]::IsNullOrWhiteSpace($HostDirectory)) { throw '请使用 -HostDirectory 或 FFMPEGFREEUI_HOME 指定 Ext 宿主目录' }
$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$hostRoot = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($HostDirectory), $projectRoot)
$sdk = Join-Path $hostRoot 'FFmpegFreeUI.Ext.PluginSdk.dll'
if (-not (Test-Path -LiteralPath $sdk) -or -not (Test-Path -LiteralPath (Join-Path $hostRoot 'FFmpegFreeUI.Ext.PluginHost.dll'))) { throw '目标目录缺少 Ext SDK 或 PluginHost' }
# SDK 的程序集版本固定为 2.0 以保持绑定稳定；发布版本通过文件版本核验。
$sdkVersion = [Version][Diagnostics.FileVersionInfo]::GetVersionInfo($sdk).FileVersion
$pluginHostVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $hostRoot 'FFmpegFreeUI.Ext.PluginHost.dll')).Version
if ($sdkVersion -lt [Version]'2.5.0' -or $pluginHostVersion -lt [Version]'2.5.0') { throw '需要 Ext API 2.5 或更新版本' }
$lakeUi = Join-Path $hostRoot 'LakeUI.dll'
if (-not (Test-Path -LiteralPath $lakeUi)) { throw '目标目录缺少 LakeUI（需要 5.112 或更新的 5.x）' }
$lakeVersion = [Reflection.AssemblyName]::GetAssemblyName($lakeUi).Version
if ($lakeVersion.Major -ne 5 -or $lakeVersion -lt [Version]'5.112.0') { throw "需要 LakeUI 5.112 或更新的 5.x；当前为 $lakeVersion" }
$activeHost = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and [IO.Path]::GetDirectoryName($_.Path) -eq $hostRoot -and $_.ProcessName -like 'FFmpegFreeUI*' }
if ($activeHost) { throw '请先关闭目标宿主，再安装插件' }
& (Join-Path $projectRoot 'release/Build-Zip.ps1') -SkipBuild:$SkipBuild
$packageExit = $LASTEXITCODE
if ($packageExit -and $packageExit -ne 0) { throw '构建安装文件失败' }
$pluginDirectory = Join-Path $hostRoot 'Plugin'
[IO.Directory]::CreateDirectory($pluginDirectory) | Out-Null
# 从本次 ZIP 安装，只覆盖自身文件，不清除现有模型、配置和其他插件。
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'Frontend/VideoEnhancerPlugin.vbproj') -Raw -Encoding UTF8
$version = [string]$project.Project.PropertyGroup.Version
$package = Join-Path $projectRoot "dist/VideoEnhancer-$version-win-x64.zip"
$sevenZip = Join-Path $projectRoot 'Backend/obj/third-party/7zip/26.03/extra/x64/7za.exe'
& $sevenZip x $package "-o$pluginDirectory" -y -aoa -mmt=on -sccUTF-8 -bd
if ($LASTEXITCODE -ne 0) { throw "7za 安装解压失败（退出码 $LASTEXITCODE）" }
# 新 DLL 解压成功后移除本插件更名前的文件，避免宿主同时扫描两个入口程序集。
$previousPlugin = Join-Path $pluginDirectory 'videoenhancer.3fui.dll'
if (Test-Path -LiteralPath $previousPlugin) { Remove-Item -LiteralPath $previousPlugin }
Write-Output "已安装：$(Join-Path $pluginDirectory 'videoenhancer.ext.3fui.dll')"
