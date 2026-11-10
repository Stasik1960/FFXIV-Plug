[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9_.-]+$')][string] $Repository = 'Stasik1960/FFXIV-Plug',
    [ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$')][string] $ReleaseTag = 'v0.4.1',
    [ValidatePattern('^[A-Za-z0-9._/-]+$')][string] $BinaryRef = 'main',
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'package-plugin.ps1') -InternalName 'EmoteTogether' -PluginDirectory 'dist/plugin' `
    -RequiredFiles @('EmoteTogether.dll', 'EmoteTogether.json', 'EmoteTogether.deps.json', 'EmoteTogether.Client.dll', 'EmoteTogether.Protocol.dll', 'Assets/tiger-mouth.jpg') `
    -Repository $Repository -ReleaseTag $ReleaseTag -BinaryRef $BinaryRef -OutputDirectory $OutputDirectory `
    -Changelog 'Пересборка для FFXIV 7.58. Сборка и автоматические проверки прошли с Dalamud API 15 Beta Testing. Протокол ET4 совместим с версией 0.4.0.'
