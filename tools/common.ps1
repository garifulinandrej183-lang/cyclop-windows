$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path $PSScriptRoot -Parent
function Get-DotNet {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $localSdk = Join-Path $env:LOCALAPPDATA 'CyclopBuild\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localSdk) { return $localSdk }
    throw 'Install the .NET SDK version from global.json (10.0.403), or place it in %LOCALAPPDATA%\CyclopBuild\dotnet.'
}
function Assert-LastExit([string]$stage) {
    if ($LASTEXITCODE -ne 0) { throw "$stage failed (exit $LASTEXITCODE)." }
}
