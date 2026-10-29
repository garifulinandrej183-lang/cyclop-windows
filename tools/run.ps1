#requires -Version 7.0
param([switch]$Collapsed)
. "$PSScriptRoot\common.ps1"
$sdk = Get-DotNet
$application = Join-Path $RepoRoot 'src\Cyclop.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\Cyclop.exe'
if (-not (Test-Path -LiteralPath $application)) { throw 'Run build.ps1 first.' }
$launch = [Diagnostics.ProcessStartInfo]::new()
$launch.FileName = $application
$launch.UseShellExecute = $false
$launch.CreateNoWindow = $true
$launch.Environment['DOTNET_ROOT_X64'] = Split-Path $sdk -Parent
if (-not $Collapsed) { $launch.ArgumentList.Add('--open') }
$started = [Diagnostics.Process]::Start($launch)
Write-Host "Cyclop process launched (PID $($started.Id))."
