param([switch]$Offline)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$acquire = Join-Path $projectRoot 'release/acquire-fff-native.ps1'
$lock = Get-Content -LiteralPath (Join-Path $projectRoot 'release/fff-native.lock.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$testPrefix = [IO.Path]::GetFullPath((Join-Path $projectRoot '.test-tools')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$testRoot = [IO.Path]::GetFullPath((Join-Path $testPrefix ('fff-native-dependencies-' + [Guid]::NewGuid().ToString('N'))))
if (-not $testRoot.StartsWith($testPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '测试路径超出项目范围' }
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$script:checks = 0
function Assert-Dependency([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message }; $script:checks++ }
try {
    $fresh = Join-Path $testRoot '首次获取'
    if ($Offline) {
        [IO.Directory]::CreateDirectory($fresh) | Out-Null
        $cachedArchive = Join-Path $projectRoot ('Frontend/obj/third-party/fff-native/' + $lock.version + '/' + $lock.archive.name)
        Copy-Item -LiteralPath $cachedArchive -Destination (Join-Path $fresh $lock.archive.name)
    }
    & $acquire -CacheDirectory $fresh
    $archive = Join-Path $fresh $lock.archive.name
    Assert-Dependency ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -eq $lock.archive.sha256) '官方下载包校验值不符'
    foreach ($file in $lock.files) {
        Assert-Dependency ((Get-FileHash -LiteralPath (Join-Path $fresh ('native/' + $file.name)) -Algorithm SHA256).Hash -eq $file.sha256) ('提取文件校验值不符：' + $file.name)
    }

    # 只保留已校验的 DLL，确认离线缓存无需重新获取播放器包。
    $backup = Join-Path $testRoot $lock.archive.name
    Copy-Item -LiteralPath $archive -Destination $backup
    Remove-Item -LiteralPath $archive
    & $acquire -CacheDirectory $fresh
    Assert-Dependency (-not (Test-Path -LiteralPath $archive)) '有效原生库缓存仍触发下载'

    # 损坏一个已提取文件，应从正确的缓存包恢复。
    Copy-Item -LiteralPath $backup -Destination $archive
    $damaged = Join-Path $fresh 'native/z.dll'
    [IO.File]::WriteAllText($damaged, '损坏的缓存', [Text.UTF8Encoding]::new($false))
    & $acquire -CacheDirectory $fresh
    $expected = $lock.files | Where-Object { $_.name -eq 'z.dll' }
    Assert-Dependency ((Get-FileHash -LiteralPath $damaged -Algorithm SHA256).Hash -eq $expected.sha256) '未恢复损坏的原生库缓存'

    $badCache = Join-Path $testRoot '损坏的下载包'
    [IO.Directory]::CreateDirectory($badCache) | Out-Null
    [IO.File]::WriteAllText((Join-Path $badCache $lock.archive.name), '损坏的下载包')
    $rejected = $false
    try { & $acquire -CacheDirectory $badCache } catch { $rejected = $_.Exception.Message.Contains('构建缓存校验失败') }
    Assert-Dependency $rejected '损坏的官方下载包未被拒绝'
    Assert-Dependency (@(Get-ChildItem -LiteralPath (Join-Path $badCache 'native') -File).Count -eq 0) '损坏的下载包仍释放了 DLL'

    # 仅在此测试作用域模拟被篡改的 HTTP 响应。
    $badDownload = Join-Path $testRoot '被篡改的下载'
    $downloadRejected = & {
        function Invoke-WebRequest { param($Uri, $OutFile) [IO.File]::WriteAllText($OutFile, '被篡改的响应') }
        try { & $acquire -CacheDirectory $badDownload; return $false }
        catch { return $_.Exception.Message.Contains('SHA-256 校验失败') }
    }
    Assert-Dependency $downloadRejected '被篡改的下载响应未被拒绝'
    Assert-Dependency (-not (Test-Path -LiteralPath (Join-Path $badDownload $lock.archive.name))) '校验失败的下载进入正式缓存'
    Assert-Dependency (-not (Test-Path -LiteralPath (Join-Path $badDownload ($lock.archive.name + '.download')))) '校验失败后留下临时下载文件'
    Write-Output ('PASS ' + $script:checks + ' 项预览依赖获取回归：首次获取、离线复用、损坏修复和篡改拒绝')
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith($testPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '拒绝清理项目外路径' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
