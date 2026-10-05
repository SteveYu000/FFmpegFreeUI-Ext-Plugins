param([string]$OutputDirectory = '', [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'dist' }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory, $projectRoot)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
if (-not $SkipBuild) {
    & dotnet restore (Join-Path $projectRoot 'Frontend/VideoEnhancerPlugin.vbproj') --locked-mode
    if ($LASTEXITCODE -ne 0) { throw '依赖还原失败' }
    & dotnet build (Join-Path $projectRoot 'Frontend/VideoEnhancerPlugin.vbproj') -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw '插件构建失败' }
}
# 从 DLL 读取构建时的源码身份，拒绝发布与它不对应的新源码。
$sourceSnapshot = & (Join-Path $PSScriptRoot 'Source-Snapshot.ps1')
$pluginPath = Join-Path $projectRoot 'dist/videoenhancer.ext.3fui.dll'
# 与宿主一样统计顶层 Entry，包括内部类型；元数据校验不要求加载宿主依赖。
$pluginStream = [IO.File]::OpenRead($pluginPath)
$pluginPe = [Reflection.PortableExecutable.PEReader]::new($pluginStream)
try {
    $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pluginPe)
    $assemblyName = $metadata.GetString($metadata.GetAssemblyDefinition().Name)
    if ($assemblyName -cne 'videoenhancer.ext.3fui') { throw "插件程序集名称不正确：$assemblyName" }
    $entries = @(
        foreach ($handle in $metadata.TypeDefinitions) {
            $definition = $metadata.GetTypeDefinition($handle)
            if ($metadata.GetString($definition.Name) -ceq 'Entry' -and $definition.GetDeclaringType().IsNil) {
                $metadata.GetString($definition.Namespace) + '.Entry'
            }
        }
    )
    if ($entries.Count -ne 1 -or $entries[0] -cne 'videoenhancer.Entry') {
        throw "插件必须仅有 videoenhancer.Entry 一个顶层入口；实际为：$($entries -join '、')"
    }
} finally { $pluginPe.Dispose(); $pluginStream.Dispose() }
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($pluginPath))
$resource = $assembly.GetManifestResourceStream('VideoEnhancer.SourceSnapshot')
if (-not $resource) { throw '插件缺少对应源码清单，请重新构建后打包' }
$reader = [IO.StreamReader]::new($resource, [Text.Encoding]::UTF8)
try { $compiledSnapshot = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
if ($compiledSnapshot.sha256 -ne $sourceSnapshot.sha256 -or $compiledSnapshot.version -ne $sourceSnapshot.version) {
    throw '源码与插件 DLL 不匹配，请重新构建，不要使用 -SkipBuild'
}
$licenseSourceCache = Join-Path $projectRoot 'Backend/obj/third-party/license-sources'
& (Join-Path $PSScriptRoot 'acquire-license-sources.ps1') -CacheDirectory $licenseSourceCache
$licenseSourceLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'third-party-sources.lock.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$ariaCache = Join-Path $projectRoot 'Backend/obj/third-party/aria2-next/2.8.3'
& (Join-Path $PSScriptRoot 'acquire-aria2.ps1') -CacheDirectory $ariaCache
$sevenCache = Join-Path $projectRoot 'Backend/obj/third-party/7zip/26.03'
& (Join-Path $PSScriptRoot 'acquire-7zip.ps1') -CacheDirectory $sevenCache
$sevenZip = Join-Path $sevenCache 'extra/x64/7za.exe'
function Add-Zip([string]$ArchivePath, [string]$WorkingDirectory, [string[]]$InputPaths) {
    # 显式文件清单和禁用通配符保证对应源码包只包含快照列出的原文。
    $zipArguments = @('a', '-tzip', $ArchivePath, '-mx=5', '-mmt=on', '-mcu=on', '-scsUTF-8', '-sccUTF-8', '-spd', '-y', '-bd') + $InputPaths
    Push-Location -LiteralPath $WorkingDirectory
    try {
        & $sevenZip @zipArguments | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7za ZIP 打包失败（退出码 $LASTEXITCODE）：$ArchivePath" }
    } finally { Pop-Location }
}
$nativeCache = Join-Path $projectRoot 'Frontend/obj/third-party/fff-native/2026.8.18'
& (Join-Path $PSScriptRoot 'acquire-fff-native.ps1') -CacheDirectory $nativeCache
$nativeLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'fff-native.lock.json') -Raw -Encoding UTF8 | ConvertFrom-Json
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'Frontend/VideoEnhancerPlugin.vbproj') -Raw -Encoding UTF8
$version = [string]$project.Project.PropertyGroup.Version
$packageName = "VideoEnhancer-$version-win-x64.zip"
$sourcePackageName = "VideoEnhancer-$version-source.zip"
$releaseTag = [Uri]::EscapeDataString("VideoEnhancer/v$version")
$releaseRoot = 'https://github.com/SteveYu000/FFmpegFreeUI-Ext-Plugins/releases'
$sourceUrl = "$releaseRoot/download/$releaseTag/$sourcePackageName"
$stage = Join-Path $outputRoot ('.zip-stage-' + [Guid]::NewGuid().ToString('N'))
$installationStage = Join-Path $stage 'installation'
$sourceMaterials = Join-Path $stage 'source-materials'
[IO.Directory]::CreateDirectory($installationStage) | Out-Null
[IO.Directory]::CreateDirectory($sourceMaterials) | Out-Null
try {
    Copy-Item -LiteralPath $pluginPath -Destination $installationStage
    Copy-Item -LiteralPath (Join-Path $projectRoot 'VideoEnhancer-安装说明.txt') -Destination $installationStage
    $binarySha256 = (Get-FileHash -LiteralPath (Join-Path $installationStage 'videoenhancer.ext.3fui.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    $data = Join-Path $installationStage 'videoenhancer'
    # 安装包只携带运行文件与声明；源码材料在独立暂存目录中组装。
    $nativeSource = Join-Path $nativeCache 'native'
    $nativeDestination = Join-Path $data 'bin/fff-native-11'
    [IO.Directory]::CreateDirectory($nativeDestination) | Out-Null
    foreach ($file in $nativeLock.files) { Copy-Item -LiteralPath (Join-Path $nativeSource $file.name) -Destination $nativeDestination }
    $ariaDestination = Join-Path $data 'bin/aria2-next'
    [IO.Directory]::CreateDirectory($ariaDestination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $ariaCache 'aria2-next.exe') -Destination $ariaDestination
    $sevenDestination = Join-Path $data 'bin/7zip'
    [IO.Directory]::CreateDirectory($sevenDestination) | Out-Null
    Copy-Item -LiteralPath $sevenZip -Destination $sevenDestination
    $licenses = Join-Path $data 'licenses'
    foreach ($component in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Backend/third-party') -Directory) {
        $destination = Join-Path $licenses $component.Name
        [IO.Directory]::CreateDirectory($destination) | Out-Null
        Get-ChildItem -LiteralPath $component.FullName -File | Copy-Item -Destination $destination
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'third-party-sources.lock.json') -Destination $licenses
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fff-native.lock.json') -Destination (Join-Path $licenses 'FFF.Native')
    Copy-Item -LiteralPath (Join-Path $sevenCache 'extra/License.txt') -Destination (Join-Path $licenses '7zip')
    $ownLicenseDirectory = Join-Path $licenses 'VideoEnhancer'
    [IO.Directory]::CreateDirectory($ownLicenseDirectory) | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'LICENSES') -File | Copy-Item -Destination $ownLicenseDirectory
    $snapshotJson = $sourceSnapshot | ConvertTo-Json -Depth 6
    foreach ($destination in @($ownLicenseDirectory, $sourceMaterials)) {
        [IO.File]::WriteAllText((Join-Path $destination 'SOURCE-SNAPSHOT.json'), $snapshotJson, [Text.UTF8Encoding]::new($false))
    }
    $thirdPartySources = Join-Path $sourceMaterials 'third-party-sources'
    [IO.Directory]::CreateDirectory($thirdPartySources) | Out-Null
    foreach ($artifact in $licenseSourceLock.artifacts) { Copy-Item -LiteralPath (Join-Path $licenseSourceCache $artifact.file) -Destination $thirdPartySources }
    foreach ($component in @('aria2-next', '7zip')) { [IO.Directory]::CreateDirectory((Join-Path $thirdPartySources $component)) | Out-Null }
    Copy-Item -LiteralPath (Join-Path $ariaCache 'aria2-next-v2.8.3-source.tar.gz') -Destination (Join-Path $thirdPartySources 'aria2-next')
    Copy-Item -LiteralPath (Join-Path $sevenCache '7z2603-src.tar.xz') -Destination (Join-Path $thirdPartySources '7zip')
    $sourceManifest = [ordered]@{ schemaVersion = 1; id = 'videoenhancer'; version = $version; license = 'AGPL-3.0-only'; installationArchive = $packageName; binarySha256 = $binarySha256; sourceSnapshot = $sourceSnapshot.sha256 }
    [IO.File]::WriteAllText((Join-Path $sourceMaterials 'source-manifest.json'), ($sourceManifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $sourceGuide = "VideoEnhancer $version 对应源码" + [Environment]::NewLine +
        "配套安装包：$packageName" + [Environment]::NewLine +
        "源码清单 SHA-256：$($sourceSnapshot.sha256)" + [Environment]::NewLine +
        "插件 DLL SHA-256：$binarySha256" + [Environment]::NewLine +
        "插件源码在本包根目录，第三方版本源码与构建配方在 third-party-sources。" + [Environment]::NewLine +
        "许可原文保留在 LICENSES 与 Backend/third-party；版本及校验值见 release 中的锁定清单。" + [Environment]::NewLine +
        "解压后按 README.md 使用 .NET 10 SDK 与 PowerShell 7 执行 ./release/Build-Zip.ps1 或 ./Install.ps1。" + [Environment]::NewLine +
        "本包与安装包必须在同一 Release 免费提供；发布与镜像时同时保留两包及各自的 .sha256 校验文件。" + [Environment]::NewLine
    [IO.File]::WriteAllText((Join-Path $sourceMaterials 'SOURCE.txt'), $sourceGuide, [Text.UTF8Encoding]::new($false))
    $temporarySourcePackage = Join-Path $stage $sourcePackageName
    $sourceList = Join-Path $stage 'source-files.txt'
    [IO.File]::WriteAllLines($sourceList, [string[]]$sourceSnapshot.files.path, [Text.UTF8Encoding]::new($false))
    Add-Zip $temporarySourcePackage $projectRoot @("@$sourceList")
    Add-Zip $temporarySourcePackage $sourceMaterials @('.')
    $sourceArchiveSha256 = (Get-FileHash -LiteralPath $temporarySourcePackage -Algorithm SHA256).Hash.ToLowerInvariant()
    $sourceNotice = "VideoEnhancer $version 对应源码下载" + [Environment]::NewLine +
        "源码包：$sourcePackageName（与安装包在同一 Release 单独提供，使用插件无需解压源码包）" + [Environment]::NewLine +
        "下载地址：$sourceUrl" + [Environment]::NewLine +
        "源码包 SHA-256：$sourceArchiveSha256" + [Environment]::NewLine +
        "源码清单 SHA-256：$($sourceSnapshot.sha256)" + [Environment]::NewLine +
        "插件 DLL SHA-256：$binarySha256" + [Environment]::NewLine +
        "源码包包含完整插件源码、构建与安装脚本、许可原文和所分发第三方组件的对应源码。" + [Environment]::NewLine +
        "发布与镜像时应同时提供两包，并保持上述源码下载地址免费、有效。" + [Environment]::NewLine
    [IO.File]::WriteAllText((Join-Path $ownLicenseDirectory 'SOURCE.txt'), $sourceNotice, [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Backend/THIRD-PARTY-NOTICES.txt') -Destination $data
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $data 'LICENSE.txt')
    foreach ($name in @('README.md', 'DEPENDENCIES-LICENSES.md', 'LICENSING.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $data }
    $manifest = [ordered]@{ id = 'videoenhancer'; version = $version; extApi = '2.5.0'; lakeUi = '5.112.0'; entry = 'videoenhancer.ext.3fui.dll'; runtime = 'videoenhancer'; distribution = 'zip'; license = 'AGPL-3.0-only'; sourceSnapshot = $sourceSnapshot.sha256; sourceArchive = $sourcePackageName; sourceUrl = $sourceUrl; sourceSha256 = $sourceArchiveSha256 }
    [IO.File]::WriteAllText((Join-Path $installationStage 'videoenhancer.manifest.json'), ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $temporaryPackage = Join-Path $stage $packageName
    Add-Zip $temporaryPackage $installationStage @('.')
    $latestSnapshot = & (Join-Path $PSScriptRoot 'Source-Snapshot.ps1')
    if ($latestSnapshot.sha256 -ne $sourceSnapshot.sha256) { throw '打包过程中源码发生变化，请重新构建' }
    # 两个包均生成成功且快照仍一致，才替换最终发布文件。
    foreach ($temporary in @($temporarySourcePackage, $temporaryPackage)) {
        $package = Join-Path $outputRoot ([IO.Path]::GetFileName($temporary))
        $hash = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::Move($temporary, $package, $true)
        [IO.File]::WriteAllText($package + '.sha256', "$hash  $([IO.Path]::GetFileName($package))`n", [Text.UTF8Encoding]::new($false))
        Write-Output $package
        Write-Output "SHA-256: $hash"
    }
} finally {
    # 仅删除本次创建的 ZIP 暂存目录，先验证绝对路径与输出根目录的关系。
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $resolvedRoot = [IO.Path]::GetFullPath($outputRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedStage.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'ZIP 暂存路径超出输出目录' }
    if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
