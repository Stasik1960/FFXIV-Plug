$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    dotnet build EmoteTogether.slnx -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet run --project tests/EmoteTogether.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    dotnet run --project tests/SlapYourFriend.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Slap Your Friend tests failed.' }
    $pluginOutput = Join-Path $projectRoot 'src/EmoteTogether.Plugin/bin/Release'
    $pluginDist = Join-Path $projectRoot 'dist/plugin'
    New-Item -ItemType Directory -Path $pluginDist -Force | Out-Null
    foreach ($name in @('EmoteTogether.dll', 'EmoteTogether.json', 'EmoteTogether.deps.json', 'EmoteTogether.Client.dll', 'EmoteTogether.Protocol.dll')) {
        Copy-Item -LiteralPath (Join-Path $pluginOutput $name) -Destination $pluginDist -Force
    }
    $assetDist = Join-Path $pluginDist 'Assets'
    New-Item -ItemType Directory -Path $assetDist -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $pluginOutput 'Assets/tiger-mouth.jpg') -Destination $assetDist -Force
    $slapOutput = Join-Path $projectRoot 'src/SlapYourFriend.Plugin/bin/Release'
    $slapDist = Join-Path $projectRoot 'dist/SlapYourFriend'
    New-Item -ItemType Directory -Path $slapDist -Force | Out-Null
    foreach ($name in @('SlapYourFriend.dll', 'SlapYourFriend.json', 'SlapYourFriend.deps.json', 'SlapYourFriend.Core.dll')) {
        Copy-Item -LiteralPath (Join-Path $slapOutput $name) -Destination $slapDist -Force
    }
    $slapAssets = Join-Path $slapDist 'Assets'
    New-Item -ItemType Directory -Path $slapAssets -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $slapOutput 'Assets/friendly-slap.jpg') -Destination $slapAssets -Force
    Write-Host 'Ready: dist/plugin and dist/SlapYourFriend. No separate server or runtime installation is required.'
}
finally { Pop-Location }
