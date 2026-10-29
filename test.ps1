param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [switch]$PythonTests)
. "$PSScriptRoot\tools\common.ps1"
Push-Location $PSScriptRoot
try {
    $sdk = Get-DotNet
    & $sdk restore Cyclop.sln --locked-mode
    Assert-LastExit 'Restore'
    & $sdk test tests/Cyclop.Tests/Cyclop.Tests.csproj -c $Configuration --no-restore --logger 'trx;LogFileName=cyclop.trx' --results-directory artifacts/test-results
    Assert-LastExit 'Tests'
    & $sdk run --project tools/VolumeSmoke/VolumeSmoke.csproj -c $Configuration -p:RestoreLockedMode=true -- --contracts-only
    Assert-LastExit 'Core Audio ABI contracts'
    if ($PythonTests) {
        $workerPython = Join-Path $PSScriptRoot 'worker\.venv\Scripts\python.exe'
        if (-not (Test-Path -LiteralPath $workerPython)) { throw 'Build the worker first with tools/build-worker.ps1.' }
        & $workerPython -m unittest discover -s worker/tests -v
        Assert-LastExit 'Worker tests'
    }
} finally { Pop-Location }
