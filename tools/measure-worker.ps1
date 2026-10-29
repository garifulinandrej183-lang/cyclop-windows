param(
 [string]$Worker = "$PSScriptRoot\..\artifacts\worker\cyclop-translator\cyclop-translator.exe",
 [Parameter(Mandatory)][string]$Models,
 [ValidateRange(5,40)][int]$Seconds = 30
)
$ErrorActionPreference = 'Stop'
$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = [IO.Path]::GetFullPath($Worker)
$startInfo.ArgumentList.Add([IO.Path]::GetFullPath($Models))
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
$startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
$process = [Diagnostics.Process]::Start($startInfo)
try {
 foreach ($pair in @(@('en-ru','Hello, how are you?'), @('ru-en','Привет, как дела?'))) {
  $request = @{id=[guid]::NewGuid().ToString('N'); direction=$pair[0]; text=$pair[1]} | ConvertTo-Json -Compress
  $process.StandardInput.WriteLine($request); $process.StandardInput.Flush()
  $response = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(60)).GetAwaiter().GetResult() | ConvertFrom-Json
  if ($response.error -or -not $response.result) { throw 'The worker did not complete the warm-up translations.' }
 }
 $before = $process.TotalProcessorTime.TotalSeconds
 $watch = [Diagnostics.Stopwatch]::StartNew()
 Start-Sleep -Seconds $Seconds
 $process.Refresh()
 [pscustomobject]@{
  ProcessId=$process.Id; DurationSeconds=[Math]::Round($watch.Elapsed.TotalSeconds,2)
  CpuPercentAllCores=[Math]::Round((($process.TotalProcessorTime.TotalSeconds-$before)/$watch.Elapsed.TotalSeconds/[Environment]::ProcessorCount*100),3)
  WorkingSetMiB=[Math]::Round($process.WorkingSet64/1MB,2); PrivateMiB=[Math]::Round($process.PrivateMemorySize64/1MB,2)
  LogicalProcessors=[Environment]::ProcessorCount; Conditions='Both directions warmed sequentially; one model cached; stdin remains open; no new requests'
 } | ConvertTo-Json
 # A bounded wait confirms the 90-second idle shutdown without closing stdin.
 if (-not $process.WaitForExit(60000)) { if (-not $process.WaitForExit(10000)) { throw 'Worker did not exit after its idle period.' } }
 if ($process.ExitCode -ne 0) { throw "Worker idle exit code $($process.ExitCode)." }
 Write-Host 'PASS: worker exited after idle timeout with stdin still open.'
} finally {
 if (-not $process.HasExited) { $process.Kill($true) }
 $process.Dispose()
}
