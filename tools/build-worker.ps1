$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$workerRoot = Join-Path $repoRoot 'worker'
$python = Join-Path $workerRoot '.venv\Scripts\python.exe'
$artifactRoot = Join-Path $repoRoot 'artifacts\worker'

function Test-Python31214([string]$Executable, [string[]]$PrefixArguments = @()) {
    if (-not (Test-Path $Executable)) { return $false }
    $reported = & $Executable @PrefixArguments --version 2>&1
    return ($LASTEXITCODE -eq 0 -and ($reported -join ' ').Trim() -eq 'Python 3.12.14')
}

if (Test-Path $python) {
    if (-not (Test-Python31214 $python)) { throw 'worker/.venv exists, but it is not Python 3.12.14. Remove that generated venv and retry.' }
} else {
    $bootstrapPython = $null
    $bootstrapArguments = @()
    if ($env:CYCLOP_PYTHON) {
        $candidate = (Resolve-Path -LiteralPath $env:CYCLOP_PYTHON -ErrorAction Stop).Path
        if (Test-Python31214 $candidate) { $bootstrapPython = $candidate }
        else { throw 'CYCLOP_PYTHON must point to Python 3.12.14.' }
    } else {
        $currentPython = Get-Command python -ErrorAction SilentlyContinue
        if ($currentPython -and (Test-Python31214 $currentPython.Source)) {
            $bootstrapPython = $currentPython.Source
        } else {
            $launcher = Get-Command py -ErrorAction SilentlyContinue
            if ($launcher -and (Test-Python31214 $launcher.Source @('-3.12'))) {
                $bootstrapPython = $launcher.Source
                $bootstrapArguments = @('-3.12')
            }
        }
    }
    if (-not $bootstrapPython) { throw 'Python 3.12.14 is required. Set CYCLOP_PYTHON to its executable.' }
    & $bootstrapPython @bootstrapArguments -m venv (Join-Path $workerRoot '.venv')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Python31214 $python)) { throw 'Could not create the worker venv with Python 3.12.14.' }
}

& $python -m pip install --disable-pip-version-check --require-hashes -r (Join-Path $workerRoot 'requirements.lock')
if ($LASTEXITCODE -ne 0) { throw 'Installing pinned worker dependencies failed.' }

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
& $python -m PyInstaller --noconfirm --clean --onedir --name cyclop-translator `
    --distpath $artifactRoot --workpath (Join-Path $artifactRoot 'build') `
    --specpath (Join-Path $artifactRoot 'spec') --collect-all ctranslate2 `
    --collect-all sentencepiece (Join-Path $workerRoot 'worker.py')
if ($LASTEXITCODE -ne 0) { throw 'PyInstaller failed to build cyclop-translator.' }

# CTranslate2's Windows wheel carries an optional cuDNN DLL under its package
# directory. Remove every nested copy from the onedir output; CPU execution does
# not statically import it (confirmed from the PE imports and frozen smoke test).
$workerDistribution = Join-Path $artifactRoot 'cyclop-translator'
$cudaRuntimes = Get-ChildItem -LiteralPath $workerDistribution -Recurse -File -Filter 'cudnn*.dll'
foreach ($cudaRuntime in $cudaRuntimes) { Remove-Item -LiteralPath $cudaRuntime.FullName -Force }
if (Get-ChildItem -LiteralPath $workerDistribution -Recurse -File -Filter 'cudnn*.dll') {
    throw 'Optional cuDNN DLLs remain in the worker onedir output.'
}

& $python (Join-Path $workerRoot 'copy_worker_notices.py') $workerDistribution
if ($LASTEXITCODE -ne 0) { throw 'Collecting worker third-party notices failed.' }
