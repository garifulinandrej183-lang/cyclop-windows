param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
. "$PSScriptRoot\tools\common.ps1"
Push-Location $PSScriptRoot
try {
    $sdk = Get-DotNet
    & $sdk restore Cyclop.sln --locked-mode
    Assert-LastExit 'Restore'
    & $sdk build Cyclop.sln -c $Configuration --no-restore
    Assert-LastExit 'Build'
} finally { Pop-Location }
