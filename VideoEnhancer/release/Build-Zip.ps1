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
$ariaCache = Join-Path $projectRoot 'Backend/obj/third-party/aria2-next/2.8.3'
& (Join-Path $PSScriptRoot 'acquire-aria2.ps1') -CacheDirectory $ariaCache
& (Join-Path $PSScriptRoot 'acquire-7zip.ps1') -CacheDirectory (Join-Path $projectRoot 'Backend/obj/third-party/7zip/26.03')
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'Frontend/VideoEnhancerPlugin.vbproj') -Raw -Encoding UTF8
$version = [string]$project.Project.PropertyGroup.Version
$stage = Join-Path $outputRoot ('.zip-stage-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null
try {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'dist/videoenhancer.3fui.dll') -Destination $stage
    $data = Join-Path $stage 'videoenhancer'
    $ariaDestination = Join-Path $data 'bin/aria2-next'
    [IO.Directory]::CreateDirectory($ariaDestination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $ariaCache 'aria2-next.exe') -Destination $ariaDestination
    $licenses = Join-Path $data 'licenses'
    foreach ($component in @('aria2-next', 'SharpCompress', '7zip')) { [IO.Directory]::CreateDirectory((Join-Path $licenses $component)) | Out-Null }
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Backend/third-party/aria2-next') -File | Copy-Item -Destination (Join-Path $licenses 'aria2-next')
    Copy-Item -LiteralPath (Join-Path $ariaCache 'aria2-next-v2.8.3-source.tar.gz') -Destination (Join-Path $licenses 'aria2-next')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Backend/third-party/SharpCompress/LICENSE.txt') -Destination (Join-Path $licenses 'SharpCompress')
    $sevenCache = Join-Path $projectRoot 'Backend/obj/third-party/7zip/26.03'
    Copy-Item -LiteralPath (Join-Path $sevenCache 'extra/License.txt') -Destination (Join-Path $licenses '7zip')
    Copy-Item -LiteralPath (Join-Path $sevenCache '7z2603-src.tar.xz') -Destination (Join-Path $licenses '7zip')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Backend/third-party/7zip/SOURCE.txt') -Destination (Join-Path $licenses '7zip')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Backend/THIRD-PARTY-NOTICES.txt') -Destination $data
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $data 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $data 'README.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'DEPENDENCIES-LICENSES.md') -Destination $data
    $manifest = [ordered]@{ id = 'videoenhancer'; version = $version; extApi = '2.5.0'; lakeUi = '5.112.0'; entry = 'videoenhancer.3fui.dll'; runtime = 'videoenhancer'; distribution = 'zip' }
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
