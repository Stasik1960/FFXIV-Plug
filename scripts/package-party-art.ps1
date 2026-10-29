[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9_.-]+$')][string] $Repository = 'Stasik1960/FFXIV-Plug',
    [ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$')][string] $ReleaseTag = 'v0.1.0',
    [ValidatePattern('^[A-Za-z0-9._/-]+$')][string] $BinaryRef = 'main',
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'package-plugin.ps1') -InternalName 'PartyArt' -PluginDirectory 'dist/PartyArt' `
    -RequiredFiles @('PartyArt.dll', 'PartyArt.json', 'PartyArt.deps.json', 'PartyArt.Core.dll') `
    -Repository $Repository -ReleaseTag $ReleaseTag -BinaryRef $BinaryRef -OutputDirectory $OutputDirectory `
    -Changelog 'Первый выпуск: рисунок из 45 строк по кнопке или /partyart send в чат группы /p. Предпросмотр, интервал 1–5 секунд, остановка кнопкой, закрытием окна или /partyart cancel. При выходе из группы, смене локации и отключении плагина очередь отменяется.'
