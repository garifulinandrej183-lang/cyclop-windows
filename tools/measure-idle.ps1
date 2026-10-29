param([Parameter(Mandatory)][int]$ProcessId, [ValidateRange(5,120)][int]$Seconds = 30)
$ErrorActionPreference = 'Stop'
$sample = Get-Process -Id $ProcessId
$before = $sample.TotalProcessorTime.TotalSeconds
$start = [Diagnostics.Stopwatch]::StartNew()
Start-Sleep -Seconds $Seconds
$sample.Refresh()
if ($sample.HasExited) { throw 'Process exited during the measurement.' }
$elapsed = $start.Elapsed.TotalSeconds
[pscustomobject]@{
    ProcessId = $ProcessId
    ProcessName = $sample.ProcessName
    DurationSeconds = [Math]::Round($elapsed, 2)
    CpuPercentAllCores = [Math]::Round((($sample.TotalProcessorTime.TotalSeconds - $before) / $elapsed / [Environment]::ProcessorCount * 100), 3)
    WorkingSetMiB = [Math]::Round($sample.WorkingSet64 / 1MB, 2)
    PrivateMiB = [Math]::Round($sample.PrivateMemorySize64 / 1MB, 2)
    LogicalProcessors = [Environment]::ProcessorCount
} | ConvertTo-Json
