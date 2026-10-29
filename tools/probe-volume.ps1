[CmdletBinding()]
param(
    [switch] $ReadOnly,
    [ValidateRange(0, 60)]
    [int] $WaitSeconds = 3
)

$ErrorActionPreference = 'Stop'
if (-not [OperatingSystem]::IsWindows()) {
    throw 'The Core Audio volume probe requires Windows.'
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnet = Join-Path $env:LOCALAPPDATA 'CyclopBuild\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

$project = Join-Path $PSScriptRoot 'VolumeSmoke\VolumeSmoke.csproj'
$probeArguments = if ($ReadOnly) { @('--read-only', '--wait', [string]$WaitSeconds) } else { @('--contracts-only') }
& $dotnet run --project $project --configuration Release -- @probeArguments
if ($LASTEXITCODE -ne 0) {
    throw "Volume probe check failed with exit code $LASTEXITCODE."
}
