[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9_.-]+$')]
    [string] $Repository = 'Stasik1960/FFXIV-Plug',
    [ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$')]
    [string] $ReleaseTag = 'v0.4.0',
    [ValidatePattern('^[A-Za-z0-9._/-]+$')]
    [string] $BinaryRef = 'main',
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginDirectory = Join-Path $projectRoot 'dist/plugin'
if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory, $projectRoot)
}

$requiredFiles = @(
    'EmoteTogether.dll',
    'EmoteTogether.json',
    'EmoteTogether.deps.json',
    'EmoteTogether.Client.dll',
    'EmoteTogether.Protocol.dll',
    'Assets/tiger-mouth.jpg'
)
foreach ($name in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $pluginDirectory $name) -PathType Leaf)) {
        throw "Missing dist/plugin/$name. Run scripts/build.ps1 first."
    }
}

$manifestPath = Join-Path $pluginDirectory 'EmoteTogether.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
if ($manifest.InternalName -ne 'EmoteTogether' -or [int]$manifest.DalamudApiLevel -ne 15) {
    throw 'Expected the EmoteTogether manifest targeting Dalamud API 15.'
}
$assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $pluginDirectory 'EmoteTogether.dll')).Version.ToString()
if ($assemblyVersion -ne $manifest.AssemblyVersion) {
    throw "DLL version $assemblyVersion does not match manifest version $($manifest.AssemblyVersion)."
}
$tagVersionText = $ReleaseTag.Substring(1)
if ($tagVersionText.Split('.').Count -eq 3) { $tagVersionText += '.0' }
if ([version]$tagVersionText -ne [version]$assemblyVersion) {
    throw "Release tag $ReleaseTag does not match assembly version $assemblyVersion."
}

# Keep versioned installer binaries in Git so both the index and ZIP are publicly
# available from raw.githubusercontent.com without a release-upload API.
$parsedVersion = [version]$assemblyVersion
$packageVersion = if ($parsedVersion.Revision -eq 0) { $parsedVersion.ToString(3) } else { $parsedVersion.ToString(4) }
$binaryPath = "plugins/EmoteTogether/$packageVersion/EmoteTogether.zip"
$packageDirectory = Join-Path $projectRoot "plugins/EmoteTogether/$packageVersion"
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
$zipPath = Join-Path $packageDirectory 'EmoteTogether.zip'
Add-Type -AssemblyName System.IO.Compression
$file = [System.IO.FileStream]::new($zipPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
try {
    $zip = [System.IO.Compression.ZipArchive]::new($file, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($name in $requiredFiles) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip,
                (Join-Path $pluginDirectory $name),
                $name,
                [System.IO.Compression.CompressionLevel]::Optimal
            ) | Out-Null
        }
    }
    finally { $zip.Dispose() }
}
finally { $file.Dispose() }

# Verify the exact root-level installer layout and that every entry matches dist/plugin.
$verification = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    if ($verification.Entries.Count -ne $requiredFiles.Count) { throw 'Unexpected archive entry count.' }
    foreach ($name in $requiredFiles) {
        $entry = $verification.GetEntry($name)
        if ($null -eq $entry -or $entry.FullName -ne $name) { throw "Missing root entry $name." }
        $sourceHash = (Get-FileHash -LiteralPath (Join-Path $pluginDirectory $name) -Algorithm SHA256).Hash
        $stream = $entry.Open()
        try { $entryHash = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($sourceHash -ne $entryHash) { throw "Archive entry differs from dist/plugin/$name." }
    }
}
finally { $verification.Dispose() }

$packageHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
$checksumPath = $zipPath + '.sha256'
[System.IO.File]::WriteAllText($checksumPath, $packageHash.ToLowerInvariant() + '  EmoteTogether.zip' + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    if ([System.IO.Path]::GetFullPath($OutputDirectory) -ne [System.IO.Path]::GetFullPath($packageDirectory)) {
        Copy-Item -LiteralPath $zipPath, $checksumPath -Destination $OutputDirectory -Force
    }
}

$releaseUrl = "https://raw.githubusercontent.com/$Repository/$BinaryRef/$binaryPath"
$entry = $manifest.Clone()
$entry.RepoUrl = "https://github.com/$Repository"
$entry.DownloadLinkInstall = $releaseUrl
$entry.DownloadLinkUpdate = $releaseUrl
$entry.DownloadLinkTesting = $releaseUrl
$entry.IsHide = $false
$entry.IsTestingExclusive = $false
$entry.LastUpdate = [System.DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$entry.Changelog = 'Приглашение цели одним нажатием. Сворачивание до заголовка и перемещаемая мини-панель с тигром и быстрым запуском. Служебные сообщения скрываются до обработки Aetherphone; постоянная синхронизация часов убрана. Таймаут участника 30 секунд, автоматическая смена пропавшего лидера. Автор: Ringoto. Всем участникам требуется версия 0.4.0 (протокол ET4).'
$indexPath = Join-Path $projectRoot 'repo.json'
$indexJson = ConvertTo-Json -InputObject @($entry) -Depth 20
[System.IO.File]::WriteAllText($indexPath, $indexJson + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

$indexCheck = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json -NoEnumerate
if ($indexCheck -isnot [array] -or $indexCheck.Count -ne 1 -or $indexCheck[0].DownloadLinkInstall -ne $releaseUrl) {
    throw 'Generated repo.json is not a valid one-entry repository array.'
}

[pscustomobject]@{
    Package = $zipPath
    RepositoryIndex = $indexPath
    AssemblyVersion = $assemblyVersion
    Files = $requiredFiles.Count
    Bytes = (Get-Item -LiteralPath $zipPath).Length
    SHA256 = $packageHash
    Checksum = $checksumPath
    RepositoryPath = $binaryPath
    DownloadLink = $releaseUrl
} | ConvertTo-Json
