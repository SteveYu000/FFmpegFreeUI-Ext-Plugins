param([string]$Package = '')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Package) { $Package = Join-Path $projectRoot 'dist/VideoEnhancer-0.1.0-win-x64.zip' }
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
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Package))
$fixture = Join-Path $projectRoot ('.test-tools/license-distribution-' + [Guid]::NewGuid().ToString('N'))
try {
    $manifest = [Text.Encoding]::UTF8.GetString((Read-EntryBytes $archive 'videoenhancer.manifest.json')) | ConvertFrom-Json
    Assert ($manifest.license -eq 'AGPL-3.0-only') '发行 DLL 的许可元数据正确'
    $guide = Read-EntryBytes $archive 'VideoEnhancer-安装说明.txt'
    Assert ((Hash $guide) -eq (Get-FileHash -LiteralPath (Join-Path $projectRoot 'VideoEnhancer-安装说明.txt')).Hash.ToLowerInvariant()) 'ZIP 根目录附带原文安装说明'
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
    Assert ($lock.artifacts.Count -eq 11) '附带固定版本第三方源码清单'
    foreach ($item in $lock.artifacts) {
        $bytes = Read-EntryBytes $archive ("videoenhancer/licenses/sources/" + $item.file)
        Assert ($bytes.Length -eq $item.size -and (Hash $bytes) -eq $item.sha256) "对应源码大小与哈希：$($item.component)"
    }
    $snapshot = [Text.Encoding]::UTF8.GetString((Read-EntryBytes $archive 'videoenhancer/licenses/VideoEnhancer/SOURCE-SNAPSHOT.json')) | ConvertFrom-Json
    Assert ($snapshot.sha256 -eq $manifest.sourceSnapshot) '清单与发行元数据一致'
    Assert (@($snapshot.files | Where-Object { $_.path -match '(^|/)(bin|obj|__pycache__)/|\.(dll|exe|user|suo|log)$' }).Count -eq 0) '插件源码不含构建缓存、二进制或开发机记录'
    $assembly = [Reflection.Assembly]::Load((Read-EntryBytes $archive 'videoenhancer.3fui.dll'))
    $resource = $assembly.GetManifestResourceStream('VideoEnhancer.SourceSnapshot')
    $reader = [IO.StreamReader]::new($resource, [Text.Encoding]::UTF8)
    try { $compiled = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    Assert ($compiled.sha256 -eq $snapshot.sha256) '插件 DLL 内嵌相同的对应源码身份'
    foreach ($resourceName in @('VideoEnhancer.Embedded.AGPL-3.0-only.txt', 'VideoEnhancer.Embedded.LICENSING.md')) {
        Assert ($assembly.GetManifestResourceNames().Contains($resourceName)) "DLL 包含许可材料：$resourceName"
    }
    $sourceBytes = Read-EntryBytes $archive $manifest.sourceArchive
    Assert ((Hash $sourceBytes) -eq $manifest.sourceSha256) '完整插件源码归档哈希正确'
    $sourceStream = [IO.MemoryStream]::new($sourceBytes, $false)
    $sourceZip = [IO.Compression.ZipArchive]::new($sourceStream, [IO.Compression.ZipArchiveMode]::Read)
    try {
        foreach ($file in $snapshot.files) {
            Assert ((Hash (Read-EntryBytes $sourceZip $file.path)) -eq $file.sha256) "插件对应源码原文：$($file.path)"
        }
        [IO.Compression.ZipFileExtensions]::ExtractToDirectory($sourceZip, $fixture)
    } finally { $sourceZip.Dispose(); $sourceStream.Dispose() }
    $fixtureSnapshot = & (Join-Path $fixture 'release/Source-Snapshot.ps1')
    Assert ($fixtureSnapshot.sha256 -eq $snapshot.sha256) '解压后的源码快照可以复核'
    [IO.Directory]::CreateDirectory((Join-Path $fixture 'dist')) | Out-Null
    [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry('videoenhancer.3fui.dll'), (Join-Path $fixture 'dist/videoenhancer.3fui.dll'))
    # 只修改隔离夹具，验证新源码配旧 DLL 会被打包流程拒绝。
    [IO.File]::AppendAllText((Join-Path $fixture 'README.md'), [Environment]::NewLine + 'stale source fixture', [Text.UTF8Encoding]::new($false))
    $rejection = (& pwsh -NoProfile -File (Join-Path $fixture 'release/Build-Zip.ps1') -SkipBuild 2>&1 | Out-String)
    Assert ($LASTEXITCODE -ne 0 -and $rejection.Contains('源码与插件 DLL 不匹配')) '拒绝发行与 DLL 不对应的源码'
    Assert (-not (Test-Path -LiteralPath (Join-Path $fixture 'dist/VideoEnhancer-0.1.0-win-x64.zip'))) '拒绝时不会生成错误发行包'
    Write-Output "PASS $script:checks 个许可与 ZIP 断言"
} finally {
    $archive.Dispose()
    $resolved = [IO.Path]::GetFullPath($fixture)
    $allowed = [IO.Path]::GetFullPath((Join-Path $projectRoot '.test-tools')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
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
