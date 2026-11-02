param(
    [ValidateSet('Install', 'Restore', 'Check')]
    [string]$Action = 'Install',
    [string]$GamePath
)

$ErrorActionPreference = 'Stop'
$OriginalHash = 'aa5854beba136bf04275793ad2d4654dc5eff1e166f7b9748a63ffd67b8c9fd4'
$Marker = '<script data-zh-subtitles="1" src="../zh_subtitles/runtime.js"></script>'
$Utf8 = New-Object System.Text.UTF8Encoding($false)
$Utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)
Add-Type -AssemblyName System.Web.Extensions
$Json = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$Json.MaxJsonLength = 67108864
$Json.RecursionLimit = 100

function Get-Hash([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
    } finally { $stream.Dispose(); $algorithm.Dispose() }
}

function Read-Json([string]$Path) {
    return $Json.DeserializeObject([System.IO.File]::ReadAllText($Path, $Utf8))
}

function Write-State([string]$Path, [object]$Value) {
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [System.IO.File]::WriteAllText($temporary, $Json.Serialize($Value), $Utf8)
        if ([System.IO.File]::Exists($Path)) {
            [System.IO.File]::Replace($temporary, $Path, [System.Management.Automation.Language.NullString]::Value)
        } else { [System.IO.File]::Move($temporary, $Path) }
    } finally {
        if ([System.IO.File]::Exists($temporary)) { [System.IO.File]::Delete($temporary) }
    }
}

function Assert-FreeSpace([string]$Archive, [long]$RequiredBytes) {
    $driveRoot = [System.IO.Path]::GetPathRoot($Archive)
    if ($driveRoot -match '^[a-zA-Z]:\\$') {
        $drive = New-Object System.IO.DriveInfo($driveRoot)
        if ($drive.AvailableFreeSpace -lt $RequiredBytes) {
            throw '可用空间不足。请至少预留约 2 GB 空间，用于资源备份和临时文件。'
        }
    }
}

function New-PatchedArchive([string]$Source, [string]$Destination) {
    $inputStream = [System.IO.File]::OpenRead($Source)
    $reader = New-Object System.IO.BinaryReader($inputStream)
    $outputStream = $null
    $writer = $null
    try {
        $magic = $reader.ReadUInt32()
        $headerSize = $reader.ReadUInt32()
        $pickleSize = $reader.ReadUInt32()
        $jsonSize = $reader.ReadUInt32()
        if ($magic -ne 4 -or $jsonSize -gt 16777216 -or ($headerSize + 8) -ge $inputStream.Length) {
            throw '资源包的 ASAR 文件头无效，已停止安装。'
        }
        $headerBytes = $reader.ReadBytes([int]$jsonSize)
        if ($headerBytes.Length -ne $jsonSize) { throw '资源包文件头不完整。' }
        $header = $Json.DeserializeObject($Utf8Strict.GetString($headerBytes))
        $entry = $header['files']['index.html']
        $dataStart = 8L + [long]$headerSize
        [void]$inputStream.Seek($dataStart + [long]$entry['offset'], [System.IO.SeekOrigin]::Begin)
        $indexBytes = $reader.ReadBytes([int]$entry['size'])
        if ($indexBytes.Length -ne $entry['size']) { throw 'index.html 不完整。' }
        $indexText = $Utf8Strict.GetString($indexBytes)
        if ($indexText.Contains('data-zh-subtitles')) { throw '源资源包已包含字幕入口，已停止重复写入。' }
        $closing = $indexText.IndexOf('</body>', [StringComparison]::Ordinal)
        if ($closing -lt 0) { throw '未找到游戏页面加载位置。' }
        $payload = $Utf8.GetBytes($indexText.Insert($closing, $Marker + "`n"))
        $entry['size'] = [int]$payload.Length
        $entry['offset'] = [string]($inputStream.Length - $dataStart)
        # The supported archive has no per-file integrity table. Do not invent one.
        if ($entry.ContainsKey('integrity')) { throw '此资源包的完整性格式与支持版本不一致。' }
        $newHeader = $Utf8.GetBytes($Json.Serialize($header))
        $padding = (4 - ($newHeader.Length % 4)) % 4
        $newSize = 8 + $newHeader.Length + $padding
        $outputStream = [System.IO.File]::Open($Destination, [System.IO.FileMode]::CreateNew)
        $writer = New-Object System.IO.BinaryWriter($outputStream)
        $writer.Write([uint32]4)
        $writer.Write([uint32]$newSize)
        $writer.Write([uint32]($newSize - 4))
        $writer.Write([uint32]$newHeader.Length)
        $writer.Write([byte[]]$newHeader)
        if ($padding) { $writer.Write([byte[]](New-Object byte[] $padding)) }
        [void]$inputStream.Seek($dataStart, [System.IO.SeekOrigin]::Begin)
        $inputStream.CopyTo($outputStream, 8388608)
        $writer.Write([byte[]]$payload)
        $writer.Flush()
    } finally {
        if ($writer) { $writer.Dispose() } elseif ($outputStream) { $outputStream.Dispose() }
        $reader.Dispose()
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($GamePath)) {
        $GamePath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    } else { $GamePath = [System.IO.Path]::GetFullPath($GamePath) }
    $archive = Join-Path $GamePath 'resources\app.asar'
    $executable = Join-Path $GamePath 'Aesops_Fables.exe'
    $backup = Join-Path $GamePath 'resources\zh_subtitles_backup\app.asar.original'
    $statePath = Join-Path $PSScriptRoot 'install-state.json'
    if (-not [System.IO.File]::Exists($archive) -or -not [System.IO.File]::Exists($executable)) {
        throw '没有找到游戏。请把补丁中的 resources 文件夹和安装脚本复制到 Aesops_Fables.exe 所在目录。'
    }
    foreach ($process in @(Get-Process -Name 'Aesops_Fables' -ErrorAction SilentlyContinue)) {
        $processPath = $null
        try { $processPath = $process.Path } catch {}
        if (-not $processPath -or $processPath -ieq $executable) {
            throw '游戏正在运行，请先完全关闭游戏，再运行本脚本。'
        }
    }
    Write-Host '正在检查游戏版本，资源包较大，请稍候……'
    $actual = Get-Hash $archive
    $state = $null
    if ([System.IO.File]::Exists($statePath)) { $state = Read-Json $statePath }
    $isOurs = $state -and $state.ContainsKey('original_sha256') -and $state['original_sha256'] -eq $OriginalHash -and $state.ContainsKey('patched_sha256') -and $state['patched_sha256'] -eq $actual

    if ($Action -eq 'Restore') {
        if ($actual -eq $OriginalHash) { Write-Host '当前已经是原版资源，无需恢复。' -ForegroundColor Green; exit 0 }
        if (-not $isOurs) { throw '当前资源与本补丁的安装记录不符，可能又安装了其他修改。为保留后续改动，已停止覆盖。' }
        if (-not [System.IO.File]::Exists($backup) -or (Get-Hash $backup) -ne $OriginalHash) { throw '原版备份缺失或校验失败，无法自动恢复。' }
        Assert-FreeSpace $archive ((Get-Item -LiteralPath $archive).Length + 16777216L)
        $temporary = $archive + '.zh-restore-' + [Guid]::NewGuid().ToString('N') + '.tmp'
        try {
            Write-Host '正在恢复原版资源……'
            [System.IO.File]::Copy($backup, $temporary, $false)
            if ((Get-Hash $temporary) -ne $OriginalHash) { throw '恢复文件校验失败，原资源未被替换。' }
            [System.IO.File]::Replace($temporary, $archive, [System.Management.Automation.Language.NullString]::Value)
        } finally { if ([System.IO.File]::Exists($temporary)) { [System.IO.File]::Delete($temporary) } }
        Write-Host '已恢复原版资源。存档和原有文字汉化保留。' -ForegroundColor Green
        exit 0
    }

    if ($actual -ne $OriginalHash -and -not $isOurs) { throw '游戏版本不匹配，或资源包已被其他补丁改动。本版仅支持已校验的 Ver.product-1.23 资源包；本次未修改游戏。' }
    if ($Action -eq 'Check') { Write-Host '版本检查通过。' -ForegroundColor Green; exit 0 }
    foreach ($name in @('runtime.js', 'config.json', 'subtitles.zh-CN.json')) {
        if (-not [System.IO.File]::Exists((Join-Path $PSScriptRoot $name))) { throw "补丁文件不完整，缺少：$name。请重新解压完整补丁。" }
    }
    $configuration = Read-Json (Join-Path $PSScriptRoot 'config.json')
    $subtitles = Read-Json (Join-Path $PSScriptRoot 'subtitles.zh-CN.json')
    if (-not $subtitles.ContainsKey('media')) { throw '字幕数据格式不正确，请重新解压补丁。' }
    if ($isOurs) {
        if (-not [System.IO.File]::Exists($backup) -or (Get-Hash $backup) -ne $OriginalHash) { throw '检测到已安装字幕，但原版备份缺失或损坏，请保留当前文件并检查备份。' }
        Write-Host '字幕已经安装，现有字幕文件可直接使用。' -ForegroundColor Green
        exit 0
    }
    Assert-FreeSpace $archive (2L * (Get-Item -LiteralPath $archive).Length + 16777216L)
    [void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($backup))
    if (-not [System.IO.File]::Exists($backup)) {
        Write-Host '正在备份原版资源（约 823 MiB）……'
        $backupTemporary = $backup + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        try {
            [System.IO.File]::Copy($archive, $backupTemporary, $false)
            if ((Get-Hash $backupTemporary) -ne $OriginalHash) { throw '备份校验失败，已停止安装。' }
            [System.IO.File]::Move($backupTemporary, $backup)
        } finally { if ([System.IO.File]::Exists($backupTemporary)) { [System.IO.File]::Delete($backupTemporary) } }
    }
    if ((Get-Hash $backup) -ne $OriginalHash) { throw '已有备份与支持版本不符，已停止安装；没有覆盖该备份。' }
    $temporary = $archive + '.zh-install-' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        Write-Host '正在写入字幕加载入口……'
        New-PatchedArchive $backup $temporary
        $patchedHash = Get-Hash $temporary
        Write-State $statePath @{ original_sha256 = $OriginalHash; patched_sha256 = $patchedHash; subtitle_version = '1.0' }
        [System.IO.File]::Replace($temporary, $archive, [System.Management.Automation.Language.NullString]::Value)
    } finally { if ([System.IO.File]::Exists($temporary)) { [System.IO.File]::Delete($temporary) } }
    Write-Host '中文字幕安装完成！按原来的方式启动游戏即可。' -ForegroundColor Green
} catch {
    Write-Host ('操作未完成：' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
