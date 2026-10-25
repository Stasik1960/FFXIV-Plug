[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9_.-]+$')][string] $Repository = 'Stasik1960/FFXIV-Plug',
    [ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$')][string] $ReleaseTag = 'v0.1.0',
    [ValidatePattern('^[A-Za-z0-9._/-]+$')][string] $BinaryRef = 'main',
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'package-plugin.ps1') -InternalName 'SlapYourFriend' -PluginDirectory 'dist/SlapYourFriend' `
    -RequiredFiles @('SlapYourFriend.dll', 'SlapYourFriend.json', 'SlapYourFriend.deps.json', 'SlapYourFriend.Core.dll', 'Assets/friendly-slap.jpg') `
    -Repository $Repository -ReleaseTag $ReleaseTag -BinaryRef $BinaryRef -OutputDirectory $OutputDirectory `
    -Changelog 'Первый выпуск Slap Your Friend. Друзья, выбранная цель или ввод имени; /slap при сближении. Индивидуальный повтор каждые 1–60 минут, по умолчанию 1 минута. Настраиваемая дистанция и минималистичный интерфейс с отключаемой картинкой. Автор: Ringoto.'
