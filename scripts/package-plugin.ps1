[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z][A-Za-z0-9]+$')][string] $InternalName,
    [Parameter(Mandatory)][string] $PluginDirectory,
    [Parameter(Mandatory)][string[]] $RequiredFiles,
    [Parameter(Mandatory)][ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$')][string] $ReleaseTag,
    [Parameter(Mandatory)][string] $Changelog,
    [ValidatePattern('^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9_.-]+$')][string] $Repository = 'Stasik1960/FFXIV-Plug',
    [ValidatePattern('^[A-Za-z0-9._/-]+$')][string] $BinaryRef = 'main',
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$PluginDirectory = [IO.Path]::GetFullPath($PluginDirectory, $projectRoot)
foreach ($name in $RequiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $PluginDirectory $name) -PathType Leaf)) {
        throw "Missing $PluginDirectory/$name. Run scripts/build.ps1 first."
    }
}
$manifest = Get-Content -LiteralPath (Join-Path $PluginDirectory "$InternalName.json") -Raw | ConvertFrom-Json -AsHashtable
if ($manifest.InternalName -ne $InternalName -or [int]$manifest.DalamudApiLevel -ne 15) {
    throw "Expected $InternalName manifest targeting Dalamud API 15."
}
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $PluginDirectory "$InternalName.dll")).Version.ToString()
if ($assemblyVersion -ne $manifest.AssemblyVersion) {
    throw "DLL version $assemblyVersion does not match manifest version $($manifest.AssemblyVersion)."
}
$tagVersionText = $ReleaseTag.Substring(1)
if ($tagVersionText.Split('.').Count -eq 3) { $tagVersionText += '.0' }
if ([version]$tagVersionText -ne [version]$assemblyVersion) {
    throw "Release tag $ReleaseTag does not match assembly version $assemblyVersion."
}

$parsedVersion = [version]$assemblyVersion
$packageVersion = if ($parsedVersion.Revision -eq 0) { $parsedVersion.ToString(3) } else { $parsedVersion.ToString(4) }
$binaryPath = "plugins/$InternalName/$packageVersion/$InternalName.zip"
$packageDirectory = Join-Path $projectRoot "plugins/$InternalName/$packageVersion"
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
$zipPath = Join-Path $packageDirectory "$InternalName.zip"
Add-Type -AssemblyName System.IO.Compression
$file = [IO.FileStream]::new($zipPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $zip = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($name in $RequiredFiles) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $PluginDirectory $name), $name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $zip.Dispose() }
}
finally { $file.Dispose() }

$verification = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    if ($verification.Entries.Count -ne $RequiredFiles.Count) { throw 'Unexpected archive entry count.' }
    foreach ($name in $RequiredFiles) {
        $entry = $verification.GetEntry($name)
        if ($null -eq $entry -or $entry.FullName -ne $name) { throw "Missing archive entry $name." }
        $sourceHash = (Get-FileHash -LiteralPath (Join-Path $PluginDirectory $name) -Algorithm SHA256).Hash
        $stream = $entry.Open()
        try { $entryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($sourceHash -ne $entryHash) { throw "Archive entry differs from $PluginDirectory/$name." }
    }
}
finally { $verification.Dispose() }

$packageHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
$checksumPath = "$zipPath.sha256"
[IO.File]::WriteAllText($checksumPath, $packageHash.ToLowerInvariant() + "  $InternalName.zip" + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory, $projectRoot)
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    if ($OutputDirectory -ne [IO.Path]::GetFullPath($packageDirectory)) {
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
$entry.LastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$entry.Changelog = $Changelog
$indexPath = Join-Path $projectRoot 'repo.json'
$previousEntries = @()
if (Test-Path -LiteralPath $indexPath) {
    $previousEntries = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json -AsHashtable -NoEnumerate
    if ($previousEntries -isnot [array] -or @($previousEntries | Where-Object { -not $_.InternalName }).Count -gt 0) {
        throw 'Existing repo.json must be an array of named plugin entries.'
    }
    if (@($previousEntries | Group-Object InternalName | Where-Object Count -gt 1).Count -gt 0) {
        throw 'Existing repo.json contains duplicate plugin names.'
    }
}
$replaced = $false
$entries = @(foreach ($previous in $previousEntries) {
    if ($previous.InternalName -eq $InternalName) { $entry; $replaced = $true }
    else { $previous }
})
if (-not $replaced) { $entries += $entry }
$indexJson = ConvertTo-Json -InputObject $entries -Depth 20
$indexCheck = ConvertFrom-Json -InputObject $indexJson -NoEnumerate
$matching = @($indexCheck | Where-Object InternalName -eq $InternalName)
if ($indexCheck -isnot [array] -or $matching.Count -ne 1 -or $matching[0].DownloadLinkInstall -ne $releaseUrl) {
    throw 'Generated repository index is invalid.'
}
[IO.File]::WriteAllText($indexPath, $indexJson + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    InternalName = $InternalName
    Package = $zipPath
    RepositoryIndex = $indexPath
    AssemblyVersion = $assemblyVersion
    Files = $RequiredFiles.Count
    PluginsInCatalog = $entries.Count
    Bytes = (Get-Item -LiteralPath $zipPath).Length
    SHA256 = $packageHash
    Checksum = $checksumPath
    RepositoryPath = $binaryPath
    DownloadLink = $releaseUrl
} | ConvertTo-Json
