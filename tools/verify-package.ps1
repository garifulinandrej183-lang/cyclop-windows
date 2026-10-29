#requires -Version 7.0
param([Parameter(Mandatory)][string]$ZipPath)
$ErrorActionPreference = 'Stop'
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ZipPath))
try {
    $entries = @{}
    foreach ($entry in $archive.Entries) { if (-not $entry.FullName.EndsWith('/')) { $entries[$entry.FullName.Replace('\','/')] = $entry } }
    $manifests = @($entries.Keys | Where-Object { $_.EndsWith('/SHA256SUMS.bundle.txt') })
    if ($manifests.Count -ne 1) { throw 'Expected one bundle manifest.' }
    $manifestName = $manifests[0]
    $prefix = $manifestName.Substring(0, $manifestName.Length - 'SHA256SUMS.bundle.txt'.Length)
    $reader = [IO.StreamReader]::new($entries[$manifestName].Open())
    try { $lines = $reader.ReadToEnd() -split '\r?\n' } finally { $reader.Dispose() }
    $checked = 0
    foreach ($line in $lines) {
        if ($line.Length -eq 0) { continue }
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw 'Invalid bundle manifest.' }
        $expected = $Matches[1]; $name = $prefix + $Matches[2]
        if (-not $entries.ContainsKey($name)) { throw "ZIP entry missing: $name" }
        $stream = $entries[$name].Open()
        try { $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
        if ($actual -ne $expected) { throw "ZIP integrity mismatch: $name" }
        $checked++
    }
    if ($entries.Count -ne $checked + 1) { throw 'Unexpected unlisted files in portable ZIP.' }
    if ($entries.Keys.Where({ $_ -match '(?i)(^|/)cudnn[^/]*\.dll$|(^|/)(AGENTS\.md|\.git)(/|$)' }).Count -gt 0) { throw 'Unexpected build-only or GPU files in package.' }
    Write-Host "PASS: portable ZIP contains $checked files matching SHA-256 manifest."
} finally { $archive.Dispose() }
