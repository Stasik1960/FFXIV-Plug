[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9_.-]+$')][string] $Repository = 'Stasik1960/FFXIV-Plug',
    [ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$')][string] $ReleaseTag = 'v0.4.0',
    [ValidatePattern('^[A-Za-z0-9._/-]+$')][string] $BinaryRef = 'main',
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'package-plugin.ps1') -InternalName 'EmoteTogether' -PluginDirectory 'dist/plugin' `
    -RequiredFiles @('EmoteTogether.dll', 'EmoteTogether.json', 'EmoteTogether.deps.json', 'EmoteTogether.Client.dll', 'EmoteTogether.Protocol.dll', 'Assets/tiger-mouth.jpg') `
    -Repository $Repository -ReleaseTag $ReleaseTag -BinaryRef $BinaryRef -OutputDirectory $OutputDirectory `
    -Changelog 'Приглашение цели одним нажатием. Сворачивание до заголовка и перемещаемая мини-панель с тигром и быстрым запуском. Служебные сообщения скрываются до обработки Aetherphone; постоянная синхронизация часов убрана. Таймаут участника 30 секунд, автоматическая смена пропавшего лидера. Автор: Ringoto. Всем участникам требуется версия 0.4.0 (протокол ET4).'
