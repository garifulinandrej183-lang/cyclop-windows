using Cyclop.Windows.Media;

ValidateComVtableOrder();
Console.WriteLine("PASS COM ABI contracts (no native activation).");
var writeIndex = Array.IndexOf(args, "--set-volume");
if (!args.Contains("--read-only") && writeIndex < 0) return;
var waitIndex = Array.IndexOf(args, "--wait");
var waitSeconds = waitIndex >= 0 && waitIndex + 1 < args.Length && int.TryParse(args[waitIndex + 1], out var wait) ? Math.Clamp(wait, 0, 60) : 2;
using var service = new SpotifyVolumeService();
var diagnostics = 0;
service.DiagnosticSink = message =>
{
    if (Interlocked.Increment(ref diagnostics) <= 30) Console.Error.WriteLine($"[diagnostic] {message}");
};
service.Changed += (_, snapshot) => Print(snapshot, "changed");
await service.InitializeAsync();
Print(service.Snapshot, "initial");
if (writeIndex >= 0)
{
    if (writeIndex + 1 >= args.Length || !double.TryParse(args[writeIndex + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var target) || target < 0 || target > 1 || !double.IsFinite(target))
        throw new ArgumentException("--set-volume requires an explicit scalar between 0 and 1.");
    if (!await service.SetVolumeAsync(target)) throw new InvalidOperationException("Spotify session volume write failed.");
    var actual = service.Snapshot;
    if (!actual.IsAvailable || Math.Abs(actual.Volume - target) > .005 || target > 0 && actual.IsMuted)
        throw new InvalidOperationException("Volume readback does not match the requested value.");
    Print(actual, "write-confirmed");
}
if (waitSeconds > 0) await Task.Delay(TimeSpan.FromSeconds(waitSeconds));

static void Print(Cyclop.Core.Media.SpotifyVolumeSnapshot snapshot, string state)
{
    Console.WriteLine($"[{state}] available={snapshot.IsAvailable} volume={(snapshot.IsAvailable ? $"{snapshot.Volume:P0}" : "—")} muted={snapshot.IsMuted}");
}

static void ValidateComVtableOrder()
{
    var serviceType = typeof(SpotifyVolumeService);
    (string Name, string[] Methods)[] contracts =
    [
        ("IMMDeviceEnumerator", ["EnumAudioEndpoints", "GetDefaultAudioEndpoint", "GetDevice", "RegisterEndpointNotificationCallback", "UnregisterEndpointNotificationCallback"]),
        ("IMMDeviceCollection", ["GetCount", "Item"]),
        ("IMMDevice", ["Activate", "OpenPropertyStore", "GetId", "GetState"]),
        ("IAudioSessionManager2", ["GetAudioSessionControl", "GetSimpleAudioVolume", "GetSessionEnumerator", "RegisterSessionNotification", "UnregisterSessionNotification", "RegisterDuckNotification", "UnregisterDuckNotification"]),
        ("IAudioSessionEnumerator", ["GetCount", "GetSession"]),
        ("IAudioSessionControl", ["GetState", "GetDisplayName", "SetDisplayName", "GetIconPath", "SetIconPath", "GetGroupingParam", "SetGroupingParam", "RegisterAudioSessionNotification", "UnregisterAudioSessionNotification"]),
        ("IAudioSessionControl2", ["GetState", "GetDisplayName", "SetDisplayName", "GetIconPath", "SetIconPath", "GetGroupingParam", "SetGroupingParam", "RegisterAudioSessionNotification", "UnregisterAudioSessionNotification", "GetSessionIdentifier", "GetSessionInstanceIdentifier", "GetProcessId", "IsSystemSoundsSession", "SetDuckingPreference"]),
        ("ISimpleAudioVolume", ["SetMasterVolume", "GetMasterVolume", "SetMute", "GetMute"]),
        ("IAudioSessionNotification", ["OnSessionCreated"]),
        ("IAudioSessionEvents", ["OnDisplayNameChanged", "OnIconPathChanged", "OnSimpleVolumeChanged", "OnChannelVolumeChanged", "OnGroupingParamChanged", "OnStateChanged", "OnSessionDisconnected"]),
        ("IMMNotificationClient", ["OnDeviceStateChanged", "OnDeviceAdded", "OnDeviceRemoved", "OnDefaultDeviceChanged", "OnPropertyValueChanged"])
    ];

    foreach (var (name, expected) in contracts)
    {
        var contract = serviceType.GetNestedType(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            ?? throw new InvalidOperationException($"Missing COM interface {name}.");
        var actual = contract.GetMethods()
            .OrderBy(method => method.MetadataToken)
            .Select(method => method.Name)
            .ToArray();
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidOperationException($"COM vtable order mismatch for {name}: {string.Join(", ", actual)}.");
        }
    }

    var sessionControl2 = serviceType.GetNestedType("IAudioSessionControl2", System.Reflection.BindingFlags.NonPublic)!;
    var isSystemSoundsSession = sessionControl2.GetMethod("IsSystemSoundsSession")!;
    if (isSystemSoundsSession.ReturnType != typeof(int) || isSystemSoundsSession.GetParameters().Length != 0)
    {
        throw new InvalidOperationException("IAudioSessionControl2.IsSystemSoundsSession must be HRESULT-only with no out parameter.");
    }

    var notificationClient = serviceType.GetNestedType("IMMNotificationClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!;
    var propertyChanged = notificationClient.GetMethod("OnPropertyValueChanged")!;
    if (propertyChanged.GetParameters()[1].ParameterType.IsByRef)
    {
        throw new InvalidOperationException("IMMNotificationClient.OnPropertyValueChanged passes PROPERTYKEY by value.");
    }
}
