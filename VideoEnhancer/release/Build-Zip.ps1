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
& (Join-Path $PSScriptRoot 'acquire-7zip.ps1') -CacheDirectory (Join-Path $projectRoot 'Backend/obj/third-party/7zip/26.03')
$nativeCache = Join-Path $projectRoot 'Frontend/obj/third-party/fff-native/2026.8.18'
& (Join-Path $PSScriptRoot 'acquire-fff-native.ps1') -CacheDirectory $nativeCache
$nativeLock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'fff-native.lock.json') -Raw -Encoding UTF8 | ConvertFrom-Json
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'Frontend/VideoEnhancerPlugin.vbproj') -Raw -Encoding UTF8
$version = [string]$project.Project.PropertyGroup.Version
$stage = Join-Path $outputRoot ('.zip-stage-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null
try {
    Copy-Item -LiteralPath $pluginPath -Destination $stage
    Copy-Item -LiteralPath (Join-Path $projectRoot 'VideoEnhancer-安装说明.txt') -Destination $stage
    $data = Join-Path $stage 'videoenhancer'
    # 只将锁定清单中的预览 DLL 随 ZIP 分发，官方下载包留在构建缓存。
    $nativeSource = Join-Path $nativeCache 'native'
    $nativeDestination = Join-Path $data 'bin/fff-native-11'
    [IO.Directory]::CreateDirectory($nativeDestination) | Out-Null
    foreach ($file in $nativeLock.files) { Copy-Item -LiteralPath (Join-Path $nativeSource $file.name) -Destination $nativeDestination }
    $ariaDestination = Join-Path $data 'bin/aria2-next'
    [IO.Directory]::CreateDirectory($ariaDestination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $ariaCache 'aria2-next.exe') -Destination $ariaDestination
    $licenses = Join-Path $data 'licenses'
    foreach ($component in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Backend/third-party') -Directory) {
        $destination = Join-Path $licenses $component.Name
        [IO.Directory]::CreateDirectory($destination) | Out-Null
        Get-ChildItem -LiteralPath $component.FullName -File | Copy-Item -Destination $destination
    }
    Copy-Item -LiteralPath (Join-Path $ariaCache 'aria2-next-v2.8.3-source.tar.gz') -Destination (Join-Path $licenses 'aria2-next')
    $thirdPartySources = Join-Path $licenses 'sources'
    [IO.Directory]::CreateDirectory($thirdPartySources) | Out-Null
    foreach ($artifact in $licenseSourceLock.artifacts) { Copy-Item -LiteralPath (Join-Path $licenseSourceCache $artifact.file) -Destination $thirdPartySources }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'third-party-sources.lock.json') -Destination $licenses
    $ownLicenseDirectory = Join-Path $licenses 'VideoEnhancer'
    [IO.Directory]::CreateDirectory($ownLicenseDirectory) | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'LICENSES') -File | Copy-Item -Destination $ownLicenseDirectory
    $snapshotJson = $sourceSnapshot | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText((Join-Path $ownLicenseDirectory 'SOURCE-SNAPSHOT.json'), $snapshotJson, [Text.UTF8Encoding]::new($false))
    $sourceArchivePath = Join-Path $ownLicenseDirectory "VideoEnhancer-$version-source.zip"
    $sourceArchive = [IO.Compression.ZipFile]::Open($sourceArchivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $sourceSnapshot.files) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($sourceArchive, (Join-Path $projectRoot $file.path), $file.path, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($sourceArchive, (Join-Path $ownLicenseDirectory 'SOURCE-SNAPSHOT.json'), 'SOURCE-SNAPSHOT.json') | Out-Null
    } finally { $sourceArchive.Dispose() }
    $latestSnapshot = & (Join-Path $PSScriptRoot 'Source-Snapshot.ps1')
    if ($latestSnapshot.sha256 -ne $sourceSnapshot.sha256) { throw '打包过程中源码发生变化，请重新构建' }
    $sourceArchiveSha256 = (Get-FileHash -LiteralPath $sourceArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $sourceNotice = "VideoEnhancer $version 对应源码" + [Environment]::NewLine +
        "源码清单 SHA-256：$($sourceSnapshot.sha256)" + [Environment]::NewLine +
        "插件 DLL SHA-256：$((Get-FileHash -LiteralPath $pluginPath -Algorithm SHA256).Hash.ToLowerInvariant())" + [Environment]::NewLine +
        "源码归档 SHA-256：$sourceArchiveSha256" + [Environment]::NewLine +
        "解压源码后，按 README.md 使用 .NET 10 SDK 与 PowerShell 7 执行 ./release/Build-Zip.ps1。" + [Environment]::NewLine +
        "构建时校验下载依赖；许可范围见 LICENSING.md，版权与条款随源码保留。" + [Environment]::NewLine
    [IO.File]::WriteAllText((Join-Path $ownLicenseDirectory 'SOURCE.txt'), $sourceNotice, [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fff-native.lock.json') -Destination (Join-Path $licenses 'FFF.Native')
    $sevenCache = Join-Path $projectRoot 'Backend/obj/third-party/7zip/26.03'
    Copy-Item -LiteralPath (Join-Path $sevenCache 'extra/License.txt') -Destination (Join-Path $licenses '7zip')
    Copy-Item -LiteralPath (Join-Path $sevenCache '7z2603-src.tar.xz') -Destination (Join-Path $licenses '7zip')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Backend/third-party/7zip/SOURCE.txt') -Destination (Join-Path $licenses '7zip')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Backend/THIRD-PARTY-NOTICES.txt') -Destination $data
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $data 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $data 'README.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'DEPENDENCIES-LICENSES.md') -Destination $data
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSING.md') -Destination $data
    $manifest = [ordered]@{ id = 'videoenhancer'; version = $version; extApi = '2.5.0'; lakeUi = '5.112.0'; entry = 'videoenhancer.ext.3fui.dll'; runtime = 'videoenhancer'; distribution = 'zip'; license = 'AGPL-3.0-only'; sourceSnapshot = $sourceSnapshot.sha256; sourceArchive = "videoenhancer/licenses/VideoEnhancer/VideoEnhancer-$version-source.zip"; sourceSha256 = $sourceArchiveSha256 }
    [IO.File]::WriteAllText((Join-Path $stage 'videoenhancer.manifest.json'), ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $package = Join-Path $outputRoot "VideoEnhancer-$version-win-x64.zip"
    if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package }
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $package, [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($package + '.sha256', "$hash  $([IO.Path]::GetFileName($package))`n", [Text.UTF8Encoding]::new($false))
    Write-Output $package
    Write-Output "SHA-256: $hash"
} finally {
    # 仅删除本次创建的 ZIP 暂存目录，先验证绝对路径与输出根目录的关系。
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $resolvedRoot = [IO.Path]::GetFullPath($outputRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedStage.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'ZIP 暂存路径超出输出目录' }
    if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
