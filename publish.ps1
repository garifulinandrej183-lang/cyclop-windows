param([switch]$SkipWorkerBuild, [string]$IsccPath = '')
. "$PSScriptRoot\tools\common.ps1"
Push-Location $PSScriptRoot
try {
    & "$PSScriptRoot\test.ps1"
    if (-not $SkipWorkerBuild) { & "$PSScriptRoot\tools\build-worker.ps1" }
    $workerPath = Join-Path $PSScriptRoot 'artifacts\worker\cyclop-translator'
    if (-not (Test-Path -LiteralPath "$workerPath\cyclop-translator.exe")) { throw 'Frozen translator worker missing.' }
    $sdk = Get-DotNet
    $stage = Join-Path $PSScriptRoot 'artifacts\publish\Cyclop-Windows-0.1.0-win-x64'
    # .NET output is never edited; publish to a fresh staging directory every time.
    if (Test-Path -LiteralPath $stage) { $stage = "$stage-$(Get-Date -Format yyyyMMddHHmmss)" }
    & $sdk publish src/Cyclop.App/Cyclop.App.csproj -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:PublishTrimmed=false -p:PublishAot=false -p:DebugType=None -p:DebugSymbols=false -p:CopyOutputSymbolsToPublishDirectory=false -o $stage
    Assert-LastExit 'Publish'
    Copy-Item -LiteralPath $workerPath -Destination (Join-Path $stage 'worker') -Recurse
    if (Get-ChildItem -LiteralPath $stage -File -Recurse -Filter '*.pdb') { throw 'Debug symbols must not be included in release packages.' }
    Copy-Item -LiteralPath README.md, README.ru.md -Destination $stage
    $publicDocs = Join-Path $stage 'docs'
    New-Item -ItemType Directory -Path $publicDocs -Force | Out-Null
    foreach ($document in @('BUILDING.md','CLIPBOARD.md','MANUAL-TESTS.md','PORTING.md','SPOTIFY.md','TRANSLATION.md','panel.png','translate.png')) {
        Copy-Item -LiteralPath (Join-Path 'docs' $document) -Destination $publicDocs
    }
    Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\','/')
        $digest = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$digest  $relative"
    } | Set-Content -Encoding utf8 (Join-Path $stage 'SHA256SUMS.bundle.txt')
    $package = Join-Path $PSScriptRoot 'artifacts\Cyclop-Windows-0.1.0-win-x64.zip'
    Compress-Archive -LiteralPath $stage -DestinationPath $package -Force
    & "$PSScriptRoot\tools\verify-package.ps1" -ZipPath $package
    if (-not $IsccPath) {
        $candidates = @("$env:LOCALAPPDATA\CyclopBuild\InnoSetup\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 7\ISCC.exe")
        $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
    if (-not $IsccPath) { throw "Portable ZIP created at $package; Inno Setup compiler missing. Install Inno Setup 7.1.0 or pass -IsccPath." }
    & $IsccPath /Qp "/DSourceDir=$stage" "/DArtifactDir=$PSScriptRoot\artifacts" installer/Cyclop.iss
    Assert-LastExit 'Installer'
    Get-FileHash -Algorithm SHA256 -LiteralPath $package, "$PSScriptRoot\artifacts\Cyclop-Windows-0.1.0-Setup.exe" | ForEach-Object { "$($_.Hash.ToLower())  $(Split-Path $_.Path -Leaf)" } | Set-Content -Encoding utf8 "$PSScriptRoot\artifacts\SHA256SUMS.txt"
    Write-Host "Portable: $package"
    Write-Host "Installer: $PSScriptRoot\artifacts\Cyclop-Windows-0.1.0-Setup.exe (unsigned)"
} finally { Pop-Location }
