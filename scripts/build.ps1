$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    dotnet build EmoteTogether.slnx -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet run --project tests/EmoteTogether.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    $pluginOutput = Join-Path $projectRoot 'src/EmoteTogether.Plugin/bin/Release'
    $pluginDist = Join-Path $projectRoot 'dist/plugin'
    New-Item -ItemType Directory -Path $pluginDist -Force | Out-Null
    foreach ($name in @('EmoteTogether.dll', 'EmoteTogether.json', 'EmoteTogether.deps.json', 'EmoteTogether.Client.dll', 'EmoteTogether.Protocol.dll')) {
        Copy-Item -LiteralPath (Join-Path $pluginOutput $name) -Destination $pluginDist -Force
    }
    Write-Host 'Ready: dist/plugin. No separate server or runtime installation is required.'
}
finally { Pop-Location }
