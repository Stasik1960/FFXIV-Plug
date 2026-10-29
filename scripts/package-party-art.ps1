[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9_.-]+$')][string] $Repository = 'Stasik1960/FFXIV-Plug',
    [ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$')][string] $ReleaseTag = 'v0.2.0',
    [ValidatePattern('^[A-Za-z0-9._/-]+$')][string] $BinaryRef = 'main',
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'package-plugin.ps1') -InternalName 'PartyArt' -PluginDirectory 'dist/PartyArt' `
    -RequiredFiles @('PartyArt.dll', 'PartyArt.json', 'PartyArt.deps.json', 'PartyArt.Core.dll', 'Assets/wolf.jpg') `
    -Repository $Repository -ReleaseTag $ReleaseTag -BinaryRef $BinaryRef -OutputDirectory $OutputDirectory `
    -Changelog 'Заготовки вместо готового рисунка: свои сообщения, 12 автоматических событий, каналы группы, альянса и локальная проверка. Общая очередь с интервалом от 1,5 секунды, защита от повторов, остановка при ограничениях чата. Новый интерфейс и отключаемый фон с волком. Закрытие окна сохраняет автоматику.'
