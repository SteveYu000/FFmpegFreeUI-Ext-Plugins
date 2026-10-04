param([Parameter(Mandatory = $true)][string]$CacheDirectory)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$cache = [IO.Path]::GetFullPath($CacheDirectory)
[IO.Directory]::CreateDirectory($cache) | Out-Null
# 与上游 1.3.10 的 aria2-next 2.8.3 版本及完整源码对应。
function Get-VerifiedArtifact([string]$Name, [string]$Url, [string]$Sha256) {
    $destination = Join-Path $cache $Name
    if (-not (Test-Path -LiteralPath $destination)) {
        $partial = $destination + '.download'
        Invoke-WebRequest -Uri $Url -OutFile $partial
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $Sha256) { throw "aria2-next 校验失败：$Name" }
        Move-Item -LiteralPath $partial -Destination $destination
    }
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $Sha256) { throw "aria2-next 缓存校验失败：$Name" }
    return $destination
}
Get-VerifiedArtifact 'aria2-next.exe' 'https://github.com/AnInsomniacy/aria2-next/releases/download/v2.8.3/aria2-next-2.8.3-windows-x86_64.exe' '08AFAF2A44811D38E7CE538DA719AB06D6925BCAAD1231EE7B92C497F58E5AAC' | Out-Null
Get-VerifiedArtifact 'aria2-next-v2.8.3-source.tar.gz' 'https://github.com/AnInsomniacy/aria2-next/archive/refs/tags/v2.8.3.tar.gz' '420E31256B5E29DE6AB9B295423ED29431494527B4FDC9AFB3946DFF4A73EAF7' | Out-Null
