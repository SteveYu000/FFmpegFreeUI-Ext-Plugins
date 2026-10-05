param([Parameter(Mandatory = $true)][string]$CacheDirectory)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'fff-native.lock.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$cache = [IO.Path]::GetFullPath($CacheDirectory)
$native = Join-Path $cache 'native'
[IO.Directory]::CreateDirectory($native) | Out-Null

function Test-NativeCache {
    foreach ($file in $lock.files) {
        $target = Join-Path $native $file.name
        if (-not (Test-Path -LiteralPath $target) -or (Get-Item -LiteralPath $target).Length -ne $file.size) { return $false }
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256) { return $false }
    }
    return $true
}
if (Test-NativeCache) { return }

# 上游将原生库包含在官方播放器单文件包中；仅读取该包，构建时不运行播放器。
$archive = Join-Path $cache $lock.archive.name
if (-not (Test-Path -LiteralPath $archive)) {
    $partial = $archive + '.download'
    try {
        Invoke-WebRequest -Uri $lock.archive.url -OutFile $partial
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $lock.archive.sha256) { throw 'FFF.Player 官方下载包 SHA-256 校验失败' }
        Move-Item -LiteralPath $partial -Destination $archive
    } finally {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    }
}
if ((Get-Item -LiteralPath $archive).Length -ne $lock.archive.size -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $lock.archive.sha256) {
    throw 'FFF.Player 构建缓存校验失败，请删除损坏的下载包后重试'
}

# 读取 .NET 单文件格式 6.0 的清单，只提取锁定清单中的原生 DLL。
$data = [IO.File]::ReadAllBytes($archive)
$signature = [Convert]::FromHexString('8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE')
$marker = [Text.Encoding]::Latin1.GetString($data).IndexOf([Text.Encoding]::Latin1.GetString($signature), [StringComparison]::Ordinal)
if ($marker -lt 8) { throw 'FFF.Player 下载包缺少 .NET 单文件标记' }
$headerOffset = [BitConverter]::ToInt64($data, $marker - 8)
if ($headerOffset -lt 0 -or $headerOffset -ge $data.LongLength) { throw 'FFF.Player 下载包清单位置无效' }
$stream = [IO.MemoryStream]::new($data, $false)
$reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::UTF8, $false)
$entries = @{}
try {
    $stream.Position = $headerOffset
    $major = $reader.ReadUInt32()
    $minor = $reader.ReadUInt32()
    $count = $reader.ReadInt32()
    if ($major -ne 6 -or $minor -ne 0 -or $count -lt $lock.files.Count -or $count -gt 10000) { throw 'FFF.Player 单文件格式与锁定发行版不符' }
    $null = $reader.ReadString()
    # 依赖清单、运行配置的位置及大小，以及清单标志，共五个 64 位字段。
    $stream.Position += 40
    for ($index = 0; $index -lt $count; $index++) {
        $offset = $reader.ReadInt64()
        $size = $reader.ReadInt64()
        $compressedSize = $reader.ReadInt64()
        $type = $reader.ReadByte()
        $name = $reader.ReadString()
        if ($lock.files.name -notcontains $name) { continue }
        if ($entries.ContainsKey($name) -or $type -ne 2 -or $compressedSize -ne 0 -or $size -le 0 -or $size -gt [int]::MaxValue -or $offset -lt 0 -or $offset + $size -gt $headerOffset) {
            throw "FFF.Player 原生文件条目无效：$name"
        }
        $entries[$name] = @{ Offset = $offset; Size = $size }
    }
} finally { $reader.Dispose() }

foreach ($file in $lock.files) {
    if ($file.name -notmatch '^[A-Za-z0-9._-]+\.dll$' -or -not $entries.ContainsKey($file.name)) { throw "FFF.Player 缺少锁定的原生文件：$($file.name)" }
    $entry = $entries[$file.name]
    if ($entry.Size -ne $file.size) { throw "FFF.Player 原生文件大小不符：$($file.name)" }
    $bytes = [byte[]]::new([int]$entry.Size)
    [Buffer]::BlockCopy($data, [int]$entry.Offset, $bytes, 0, $bytes.Length)
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -ne $file.sha256) { throw "FFF.Player 原生文件校验失败：$($file.name)" }
    $target = Join-Path $native $file.name
    $partial = $target + '.extract'
    try {
        [IO.File]::WriteAllBytes($partial, $bytes)
        Move-Item -LiteralPath $partial -Destination $target -Force
    } finally {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
    }
}
if (-not (Test-NativeCache)) { throw 'FFF.Native 原生库缓存校验失败' }
