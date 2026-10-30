[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9_.-]+$')][string] $Repository = 'Stasik1960/FFXIV-Plug',
    [ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$')][string] $ReleaseTag = 'v0.2.1',
    [ValidatePattern('^[A-Za-z0-9._/-]+$')][string] $BinaryRef = 'main',
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'package-plugin.ps1') -InternalName 'PartyArt' -PluginDirectory 'dist/PartyArt' `
    -RequiredFiles @('PartyArt.dll', 'PartyArt.json', 'PartyArt.deps.json', 'PartyArt.Core.dll', 'Assets/wolf.jpg') `
    -Repository $Repository -ReleaseTag $ReleaseTag -BinaryRef $BinaryRef -OutputDirectory $OutputDirectory `
    -Changelog 'События подземелий и заданий теперь срабатывают только в PvE. Для PvP добавлены отдельные вход на карту, начало и завершение матча. Определение режима по данным территории устраняет смешение событий во время загрузки. Существующие тексты и назначения PvE сохраняются; события PvP назначаются отдельно.'
