[CmdletBinding()]
param(
    [ValidateRange(1, 120)]
    [int] $WaitSeconds = 8
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnet = Join-Path $env:LOCALAPPDATA 'CyclopBuild\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

$probeDirectory = Join-Path ([IO.Path]::GetTempPath()) ('CyclopSpotifyProbe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $probeDirectory | Out-Null
try {
    $projectReference = [System.Security.SecurityElement]::Escape((Join-Path $repoRoot 'src\Cyclop.Windows\Cyclop.Windows.csproj'))
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <UseWPF>true</UseWPF>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="$projectReference" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $probeDirectory 'SpotifyProbe.csproj') -Encoding utf8

    @"
using Cyclop.Windows.Media;

using var service = new SpotifyService();
var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
service.Changed += (_, snapshot) =>
{
    if (snapshot.HasSession && (snapshot.Title.Length > 0 || snapshot.Artist.Length > 0 || snapshot.Duration > TimeSpan.Zero || snapshot.ArtworkBytes.Length > 0))
    {
        changed.TrySetResult();
    }
};
await service.InitializeAsync();
await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds($WaitSeconds)));

var snapshot = service.Snapshot;
Console.WriteLine(snapshot.HasSession
    ? "Spotify session found."
    : "No Spotify desktop GSMTC session is currently available.");
Console.WriteLine($"SourceAppUserModelId: {snapshot.SourceAppUserModelId}");
Console.WriteLine($"Title: {snapshot.Title}");
Console.WriteLine($"Artist: {snapshot.Artist}");
Console.WriteLine($"Playing: {snapshot.IsPlaying}");
Console.WriteLine($"Position: {snapshot.Position} / {snapshot.Duration}");
Console.WriteLine($"Capabilities: play={snapshot.CanPlay}, pause={snapshot.CanPause}, previous={snapshot.CanSkipPrevious}, next={snapshot.CanSkipNext}, seek={snapshot.CanSeek}");
Console.WriteLine($"Artwork bytes: {snapshot.ArtworkBytes.Length}");
"@ | Set-Content -LiteralPath (Join-Path $probeDirectory 'Program.cs') -Encoding utf8

    & $dotnet run --project (Join-Path $probeDirectory 'SpotifyProbe.csproj') --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "Spotify probe failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item -LiteralPath $probeDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
