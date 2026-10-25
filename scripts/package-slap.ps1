[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9_.-]+$')][string] $Repository = 'Stasik1960/FFXIV-Plug',
    [ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$')][string] $ReleaseTag = 'v0.1.1',
    [ValidatePattern('^[A-Za-z0-9._/-]+$')][string] $BinaryRef = 'main',
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'package-plugin.ps1') -InternalName 'SlapYourFriend' -PluginDirectory 'dist/SlapYourFriend' `
    -RequiredFiles @('SlapYourFriend.dll', 'SlapYourFriend.json', 'SlapYourFriend.deps.json', 'SlapYourFriend.Core.dll', 'Assets/friendly-slap.jpg') `
    -Repository $Repository -ReleaseTag $ReleaseTag -BinaryRef $BinaryRef -OutputDirectory $OutputDirectory `
    -Changelog 'Исправлено бесконечное ожидание текущей эмоции: пощёчина прерывает зацикленные эмоции. Стандартный текст /slap снова виден в чате. Запуск только после остановки на полсекунды; бег не расходует таймер. Уточнено определение домашнего мира при вводе имени.'
