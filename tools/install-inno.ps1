$ErrorActionPreference = 'Stop'
$version = '7.1.0'
$expected = '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'
$buildRoot = Join-Path $env:LOCALAPPDATA 'CyclopBuild'
$target = Join-Path $buildRoot 'InnoSetup'
if (Test-Path -LiteralPath (Join-Path $target 'ISCC.exe')) { Write-Host "Inno Setup compiler already present: $target"; return }
New-Item -ItemType Directory -Force -Path $buildRoot | Out-Null
$download = Join-Path $buildRoot "innosetup-$version-x64.exe"
Invoke-WebRequest "https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-$version-x64.exe" -OutFile $download
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Inno Setup archive checksum mismatch.' }
if ((Get-AuthenticodeSignature -FilePath $download).Status -ne 'Valid') { throw 'Inno Setup installer signature is not valid.' }
$arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/CURRENTUSER',('/DIR="' + $target + '"'))
$installed = Start-Process -FilePath $download -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if ($installed.ExitCode -ne 0 -or -not (Test-Path -LiteralPath "$target\ISCC.exe")) { throw 'Inno Setup installation failed.' }
Write-Host "Inno Setup $version installed for the current user at $target."
