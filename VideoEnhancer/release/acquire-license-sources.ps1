param([Parameter(Mandatory = $true)][string]$CacheDirectory)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$cache = [IO.Path]::GetFullPath($CacheDirectory)
[IO.Directory]::CreateDirectory($cache) | Out-Null
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'third-party-sources.lock.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($artifact in $lock.artifacts) {
    if ([IO.Path]::GetFileName($artifact.file) -ne $artifact.file) { throw '源码清单包含非法文件名' }
    $destination = Join-Path $cache $artifact.file
    if (-not (Test-Path -LiteralPath $destination)) {
        $partial = $destination + '.download'
        try {
            Invoke-WebRequest -Uri $artifact.url -OutFile $partial -TimeoutSec 180 -MaximumRetryCount 2 -RetryIntervalSec 2
            if ((Get-Item -LiteralPath $partial).Length -ne $artifact.size -or
                (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $artifact.sha256) {
                throw "第三方源码下载校验失败：$($artifact.file)"
            }
            Move-Item -LiteralPath $partial -Destination $destination
        } finally {
            if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial }
        }
    }
    if ((Get-Item -LiteralPath $destination).Length -ne $artifact.size -or
        (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $artifact.sha256) {
        throw "第三方源码缓存校验失败：$($artifact.file)"
    }
}
Write-Output "第三方对应源码已校验：$($lock.artifacts.Count) 个归档"
