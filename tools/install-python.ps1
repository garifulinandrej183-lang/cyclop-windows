#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$buildRoot = Join-Path $env:LOCALAPPDATA 'CyclopBuild'
$target = Join-Path $buildRoot 'Python31214'
$pythonExe = Join-Path $target 'python\python.exe'
if (-not (Test-Path -LiteralPath $pythonExe)) {
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    $archive = Join-Path $buildRoot 'cpython-3.12.14-20260825-windows-x64.tar.gz'
    Invoke-WebRequest 'https://github.com/astral-sh/python-build-standalone/releases/download/20260825/cpython-3.12.14%2B20260825-x86_64-pc-windows-msvc-install_only_stripped.tar.gz' -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne '8e6aad12ef6fc9685e67ce66253f8f72d6e8fa02cb7187e5850bd4db5ecd9e2a') { throw 'Build Python checksum mismatch.' }
    & tar -xzf $archive -C $target
    if ($LASTEXITCODE -ne 0) { throw 'Build Python extraction failed.' }
}
$version = & $pythonExe -c 'import sys; print(sys.version.split()[0])'
if ($version -ne '3.12.14') { throw 'Expected Python 3.12.14.' }
$env:CYCLOP_PYTHON = $pythonExe
if ($env:GITHUB_ENV) { "CYCLOP_PYTHON=$pythonExe" | Add-Content -LiteralPath $env:GITHUB_ENV -Encoding utf8 }
Write-Host "Pinned build Python: $pythonExe ($version). It is only needed on the build machine."
