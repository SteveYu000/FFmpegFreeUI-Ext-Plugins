param([string]$OutputFile = '')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# 源码快照排除构建输出和缓存，只记录可移植的相对路径与内容校验值。
function Get-SourceFiles([string]$Directory) {
    foreach ($entry in Get-ChildItem -LiteralPath $Directory) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "源码目录不允许链接：$($entry.Name)" }
        if ($entry.PSIsContainer) {
            if ($entry.Name -notin @('bin', 'obj', '__pycache__', '.vs')) { Get-SourceFiles $entry.FullName }
        } elseif ($entry.Name -notmatch '\.(user|suo|log)$') { $entry.FullName }
    }
}
[string[]]$paths = @(
    foreach ($name in @('.gitignore', 'LICENSE', 'README.md', 'LICENSING.md', 'DEPENDENCIES-LICENSES.md', 'VideoEnhancer-安装说明.txt', 'VideoEnhancer.slnx', 'Install.ps1')) {
        $file = Join-Path $projectRoot $name
        if (-not (Test-Path -LiteralPath $file)) { throw "缺少源码材料：$name" }
        $file
    }
    foreach ($name in @('Backend', 'Frontend', 'tests', 'release', 'LICENSES')) { Get-SourceFiles (Join-Path $projectRoot $name) }
)
$paths = @($paths | ForEach-Object { [IO.Path]::GetRelativePath($projectRoot, $_).Replace('\', '/') })
[Array]::Sort($paths, [StringComparer]::Ordinal)
$files = @(
    foreach ($relative in $paths) {
        $file = Join-Path $projectRoot $relative
        [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
)
# 使用显式 LF，使不同 PowerShell 平台生成相同的源码身份。
$identity = ($files | ForEach-Object { $_.path + '|' + $_.sha256 }) -join ([string][char]10)
$sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))).ToLowerInvariant()
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'Frontend/VideoEnhancerPlugin.vbproj') -Raw -Encoding UTF8
$snapshot = [ordered]@{ schemaVersion = 1; version = [string]$project.Project.PropertyGroup.Version; sha256 = $sha256; files = $files }
if ($OutputFile) {
    $destination = [IO.Path]::GetFullPath($OutputFile, $projectRoot)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    $json = $snapshot | ConvertTo-Json -Depth 6
    if (-not (Test-Path -LiteralPath $destination) -or [IO.File]::ReadAllText($destination) -cne $json) {
        [IO.File]::WriteAllText($destination, $json, [Text.UTF8Encoding]::new($false))
    }
    Write-Output "源码快照：$sha256"
} else { $snapshot }
