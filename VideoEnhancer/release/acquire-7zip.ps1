param([Parameter(Mandatory = $true)][string]$CacheDirectory)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$cache = [IO.Path]::GetFullPath($CacheDirectory)
[IO.Directory]::CreateDirectory($cache) | Out-Null

# 固定版本和校验值，构建时获取；最终用户不需要联网安装解压组件。
function Get-VerifiedFile([string]$Name, [string]$Hash) {
    $path = Join-Path $cache $Name
    if (-not (Test-Path -LiteralPath $path)) {
        Invoke-WebRequest -Uri "https://github.com/ip7z/7zip/releases/download/26.03/$Name" -OutFile $path
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Hash) {
        throw "7-Zip 构建依赖校验失败：$path"
    }
    return $path
}
$extra = Get-VerifiedFile '7z2603-extra.7z' '191894E6ACB3647FFB69CE630479FF318523B2E2B9890AA7F05C1127C2E59B8F'
$bootstrap = Get-VerifiedFile '7zr.exe' 'AD4C82FADCBDF93C03B4FC440F300509C7D60C5C2F4D183E35D9D70D6957037D'
$source = Get-VerifiedFile '7z2603-src.tar.xz' '9CBDE5099C6DEB73691B0579063DA5827522CCBBCBA3F0020FD04E8C8C16C0D4'
$exe = Join-Path $cache 'extra/x64/7za.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    & $bootstrap x $extra "-o$(Join-Path $cache 'extra')" -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '无法释放 7-Zip 独立组件' }
}
if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne 'EDBEE35370E14030E4C785CF88200F42DC651C1EB4217C1E3963C38A12F099B0') {
    throw '7za.exe 校验失败'
}
