param([string]$Package = '', [string]$SourcePackage = '')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'Frontend/VideoEnhancerPlugin.vbproj') -Raw -Encoding UTF8
$version = [string]$project.Project.PropertyGroup.Version
if (-not $Package) { $Package = Join-Path $projectRoot "dist/VideoEnhancer-$version-win-x64.zip" }
$packagePath = [IO.Path]::GetFullPath($Package)
$script:checks = 0
function Assert([bool]$Success, [string]$Message) {
    if (-not $Success) { throw $Message }
    $script:checks++
}
function Read-EntryBytes($Archive, [string]$Name) {
    $entry = $Archive.GetEntry($Name)
    if (-not $entry) { throw "ZIP 缺少：$Name" }
    $stream = $entry.Open()
    $buffer = [IO.MemoryStream]::new()
    try { $stream.CopyTo($buffer); return ,$buffer.ToArray() } finally { $buffer.Dispose(); $stream.Dispose() }
}
function Hash([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }
$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
$sourceZip = $null
# 源码解压夹具放在系统临时目录，避免项目目录监视持续占用新建的源码文件夹。
$fixtureBase = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'VideoEnhancer-distribution-tests'))
$fixture = Join-Path $fixtureBase ([Guid]::NewGuid().ToString('N'))
try {
    $manifest = [Text.Encoding]::UTF8.GetString((Read-EntryBytes $archive 'videoenhancer.manifest.json')) | ConvertFrom-Json
    Assert ($manifest.version -ceq $version) '安装元数据与当前项目发布版本一致'
    Assert ($manifest.license -eq 'AGPL-3.0-only') '发行 DLL 的许可元数据正确'
    Assert ($manifest.entry -eq 'videoenhancer.ext.3fui.dll') '元数据使用 Ext 插件二进制名称'
    Assert ($null -ne $archive.GetEntry($manifest.entry)) '安装包包含声明的插件 DLL'
    Assert ($null -eq $archive.GetEntry('videoenhancer.3fui.dll')) '安装包不包含更名前的插件 DLL'
    Assert (@($archive.Entries | Where-Object { $_.FullName -match 'SharpCompress' }).Count -eq 0) '安装包没有 SharpCompress 二进制或许可残留'
    Assert (@($archive.Entries | Where-Object { $_.FullName -match '\.(zip|7z|tar|gz|xz|zst|cs|vb|py|csproj|vbproj|slnx)$' }).Count -eq 0) '安装包不含源码、工程文件或源码归档'
    Assert ($manifest.sourceArchive -ceq "VideoEnhancer-$version-source.zip") '安装元数据指向同版本独立源码包'
    Assert ($null -eq $archive.GetEntry($manifest.sourceArchive)) '源码包没有嵌套进安装包'
    $sourceUrl = [Uri]$manifest.sourceUrl
    Assert ($sourceUrl.Scheme -eq 'https' -and $sourceUrl.Host -eq 'github.com' -and
        [Uri]::UnescapeDataString($sourceUrl.AbsolutePath) -ceq "/SteveYu000/FFmpegFreeUI-Ext-Plugins/releases/download/VideoEnhancer/v$version/$($manifest.sourceArchive)") '源码下载地址对应同一插件 Release'
    if (-not $SourcePackage) { $SourcePackage = Join-Path ([IO.Path]::GetDirectoryName($packagePath)) $manifest.sourceArchive }
    $sourcePackagePath = [IO.Path]::GetFullPath($SourcePackage)
    $sourceHash = (Get-FileHash -LiteralPath $sourcePackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert ($sourceHash -eq $manifest.sourceSha256) '独立源码包与安装元数据的 SHA-256 一致'
    $sourceZip = [IO.Compression.ZipFile]::OpenRead($sourcePackagePath)
    foreach ($path in @($packagePath, $sourcePackagePath)) {
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert ((Get-Content -LiteralPath ($path + '.sha256') -Raw -Encoding UTF8).Trim() -ceq "$hash  $([IO.Path]::GetFileName($path))") '两个发行包各有准确的校验文件'
    }
    Assert ((Hash (Read-EntryBytes $archive 'videoenhancer/bin/7zip/7za.exe')) -eq 'edbee35370e14030e4c785cf88200f42dc651c1eb4217c1e3963c38a12f099b0') '安装包随附锁定版本的 7za'
    $guide = Read-EntryBytes $archive 'VideoEnhancer-安装说明.txt'
    Assert ((Hash $guide) -eq (Get-FileHash -LiteralPath (Join-Path $projectRoot 'VideoEnhancer-安装说明.txt')).Hash.ToLowerInvariant()) '安装包根目录附带原文安装说明'
    Assert ([Text.Encoding]::UTF8.GetString($guide).Contains('Plugin/')) '安装说明包含实际 Plugin 布局'
    foreach ($name in @('LICENSING.md', 'DEPENDENCIES-LICENSES.md', 'THIRD-PARTY-NOTICES.txt')) {
        Assert ($null -ne $archive.GetEntry("videoenhancer/$name")) "随包范围与声明：$name"
    }
    Assert ((Hash (Read-EntryBytes $archive 'videoenhancer/LICENSE.txt')) -eq (Hash ([IO.File]::ReadAllBytes((Join-Path $projectRoot 'LICENSE'))))) '根 MIT 声明保持原文'
    Assert ((Hash (Read-EntryBytes $archive 'videoenhancer/licenses/VideoEnhancer/AGPL-3.0-only.txt')) -eq (Hash (Read-EntryBytes $archive 'videoenhancer/licenses/RVE/LICENSE'))) 'AGPL 原文与上游一致'
    foreach ($component in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Backend/third-party') -Directory) {
        foreach ($file in Get-ChildItem -LiteralPath $component.FullName -File) {
            Assert ((Hash (Read-EntryBytes $archive "videoenhancer/licenses/$($component.Name)/$($file.Name)")) -eq (Hash ([IO.File]::ReadAllBytes($file.FullName)))) "保留原始第三方声明：$($component.Name)/$($file.Name)"
        }
    }
    $lock = [Text.Encoding]::UTF8.GetString((Read-EntryBytes $archive 'videoenhancer/licenses/third-party-sources.lock.json')) | ConvertFrom-Json
    Assert ($lock.artifacts.Count -eq 11) '安装包保留固定版本第三方源码清单'
    foreach ($item in $lock.artifacts) {
        $bytes = Read-EntryBytes $sourceZip ("third-party-sources/" + $item.file)
        Assert ($bytes.Length -eq $item.size -and (Hash $bytes) -eq $item.sha256) "独立源码包归档大小与哈希：$($item.component)"
    }
    Assert ((Hash (Read-EntryBytes $sourceZip 'third-party-sources/aria2-next/aria2-next-v2.8.3-source.tar.gz')) -eq '420e31256b5e29de6ab9b295423ed29431494527b4fdc9afb3946dff4a73eaf7') '独立源码包包含同版完整 aria2-next 源码'
    Assert ((Hash (Read-EntryBytes $sourceZip 'third-party-sources/7zip/7z2603-src.tar.xz')) -eq '9cbde5099c6deb73691b0579063da5827522ccbbcba3f0020fd04e8c8c16c0d4') '独立源码包包含同版完整 7-Zip 源码'
    $snapshotBytes = Read-EntryBytes $archive 'videoenhancer/licenses/VideoEnhancer/SOURCE-SNAPSHOT.json'
    $snapshot = [Text.Encoding]::UTF8.GetString($snapshotBytes) | ConvertFrom-Json
    Assert ($snapshot.sha256 -eq $manifest.sourceSnapshot) '源码清单与安装元数据一致'
    Assert ((Hash $snapshotBytes) -eq (Hash (Read-EntryBytes $sourceZip 'SOURCE-SNAPSHOT.json'))) '两个包携带相同的对应源码清单'
    Assert (@($snapshot.files | Where-Object { $_.path -match '(^|/)(bin|obj|__pycache__)/|\.(dll|exe|user|suo|log)$' }).Count -eq 0) '插件源码不含构建缓存、二进制或开发机记录'
    Assert (@($sourceZip.Entries | Where-Object { $_.FullName -match '(^|/)(bin|obj|__pycache__)/|\.(dll|exe|user|suo|log)$' }).Count -eq 0) '独立源码包没有二进制输出或构建缓存'
    $binary = Read-EntryBytes $archive $manifest.entry
    $sourceManifest = [Text.Encoding]::UTF8.GetString((Read-EntryBytes $sourceZip 'source-manifest.json')) | ConvertFrom-Json
    Assert ($sourceManifest.version -eq $manifest.version -and $sourceManifest.sourceSnapshot -eq $manifest.sourceSnapshot) '源码包与安装包的版本及源码身份一致'
    Assert ($sourceManifest.installationArchive -ceq [IO.Path]::GetFileName($packagePath) -and $sourceManifest.binarySha256 -eq (Hash $binary)) '源码包明确对应实际分发的插件 DLL'
    $notice = [Text.Encoding]::UTF8.GetString((Read-EntryBytes $archive 'videoenhancer/licenses/VideoEnhancer/SOURCE.txt'))
    Assert ($notice.Contains($manifest.sourceArchive) -and $notice.Contains($manifest.sourceUrl) -and $notice.Contains($manifest.sourceSha256)) '安装包保留明确且可校验的源码获取说明'
    $assembly = [Reflection.Assembly]::Load($binary)
    Assert ($assembly.GetName().Version.ToString(3) -ceq $version) '发行 DLL 的程序集版本与当前发布版本一致'
    $resource = $assembly.GetManifestResourceStream('VideoEnhancer.SourceSnapshot')
    $reader = [IO.StreamReader]::new($resource, [Text.Encoding]::UTF8)
    try { $compiled = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    Assert ($compiled.sha256 -eq $snapshot.sha256) '插件 DLL 内嵌相同的对应源码身份'
    Assert (@($assembly.GetReferencedAssemblies() | Where-Object { $_.Name -eq 'SharpCompress' }).Count -eq 0) '发行 DLL 不引用 SharpCompress'
    Assert (@($assembly.GetManifestResourceNames() | Where-Object { $_ -match 'SharpCompress' }).Count -eq 0) '发行 DLL 没有 SharpCompress 资源'
    Assert (-not $assembly.GetManifestResourceNames().Contains('VideoEnhancer.Embedded.SevenZip.7z2603-src.tar.xz')) '发行 DLL 不再内嵌第三方源码归档'
    foreach ($resourceName in @('VideoEnhancer.Embedded.AGPL-3.0-only.txt', 'VideoEnhancer.Embedded.LICENSING.md')) {
        Assert ($assembly.GetManifestResourceNames().Contains($resourceName)) "DLL 包含许可材料：$resourceName"
    }
    foreach ($file in $snapshot.files) {
        Assert ((Hash (Read-EntryBytes $sourceZip $file.path)) -eq $file.sha256) "插件对应源码原文：$($file.path)"
    }
    [IO.Compression.ZipFileExtensions]::ExtractToDirectory($sourceZip, $fixture)
    # 在子进程复核清单，使目录遍历句柄随进程结束释放后再清理夹具。
    $verifiedSnapshot = Join-Path $fixture 'verified-snapshot.json'
    & pwsh -NoProfile -File (Join-Path $fixture 'release/Source-Snapshot.ps1') -OutputFile $verifiedSnapshot | Out-Null
    Assert ($LASTEXITCODE -eq 0) '源码包中的快照脚本可独立执行'
    $fixtureSnapshot = Get-Content -LiteralPath $verifiedSnapshot -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert ($fixtureSnapshot.sha256 -eq $snapshot.sha256) '源码包可直接解压复核完整插件源码'
    [IO.Directory]::CreateDirectory((Join-Path $fixture 'dist')) | Out-Null
    [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry($manifest.entry), (Join-Path $fixture "dist/$($manifest.entry)"))
    # 只修改隔离夹具，验证新源码配旧 DLL 会在生成任一发布包前被拒绝。
    [IO.File]::AppendAllText((Join-Path $fixture 'README.md'), [Environment]::NewLine + 'stale source fixture', [Text.UTF8Encoding]::new($false))
    $rejection = (& pwsh -NoProfile -File (Join-Path $fixture 'release/Build-Zip.ps1') -SkipBuild 2>&1 | Out-String)
    Assert ($LASTEXITCODE -ne 0 -and $rejection.Contains('源码与插件 DLL 不匹配')) '拒绝发行与 DLL 不对应的源码'
    Assert (-not (Test-Path -LiteralPath (Join-Path $fixture "dist/VideoEnhancer-$version-win-x64.zip"))) '拒绝时不会生成错误安装包'
    Assert (-not (Test-Path -LiteralPath (Join-Path $fixture "dist/VideoEnhancer-$version-source.zip"))) '拒绝时不会生成错误源码包'
    Write-Output "PASS $script:checks 个许可与双包分发断言"
} finally {
    if ($sourceZip) { $sourceZip.Dispose() }
    $archive.Dispose()
    $resolved = [IO.Path]::GetFullPath($fixture)
    $allowed = $fixtureBase.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw '测试清理路径超出范围' }
    # 新建源码目录可能仍被 Windows 扫描器短暂占用；保持路径检查，并有限重试清理。
    for ($attempt = 0; (Test-Path -LiteralPath $resolved) -and $attempt -lt 4; $attempt++) {
        try { Remove-Item -LiteralPath $resolved -Recurse -Force }
        catch {
            if ($attempt -eq 3) { throw }
            [GC]::Collect()
            [GC]::WaitForPendingFinalizers()
            Start-Sleep -Milliseconds (250 * ($attempt + 1))
        }
    }
}
