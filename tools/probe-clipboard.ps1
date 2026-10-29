param(
    [switch]$LiveClipboard,
    [switch]$ShelfOnly,
    [switch]$DataOnly
)

$ErrorActionPreference = 'Stop'
if (@($LiveClipboard, $ShelfOnly, $DataOnly).Where({ $_ }).Count -gt 1) { throw 'Choose only one probe mode.' }
$probeMode = if ($LiveClipboard) { '--live-clipboard' } elseif ($ShelfOnly) { '--shelf-only' } elseif ($DataOnly) { '--data-only' } else { '--safe' }
. "$PSScriptRoot\common.ps1"
$dotnetExe = Get-DotNet
$project = Join-Path $PSScriptRoot 'ClipboardSmoke\ClipboardSmoke.csproj'
& $dotnetExe run --project $project --configuration Release -p:RestoreLockedMode=true -- $probeMode
exit $LASTEXITCODE
