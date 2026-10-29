using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Cyclop.Core.Media;

namespace Cyclop.Windows.Media;

/// <summary>Controls Spotify.exe audio sessions through Windows Core Audio only.</summary>
public sealed class SpotifyVolumeService : IDisposable
{
    private static readonly Guid MmDeviceEnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid MmDeviceEnumeratorInterfaceId = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid AudioSessionManager2InterfaceId = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid AudioSessionControl2InterfaceId = new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");
    private static readonly Guid SimpleAudioVolumeInterfaceId = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");

    private const uint ClsctxInprocServer = 1;
    private const int ERender = 0;
    private const uint DeviceStateActive = 0x1;
    private const int AudioSessionActive = 1;
    private const int AudioSessionInactive = 0;
    private const int AudioSessionExpired = 2;
    private const int CoInitMultithreaded = 0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SynchronizationContext? _notificationContext = SynchronizationContext.Current;
    private readonly Dictionary<string, EndpointState> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SpotifyAudioSession> _sessions = new(StringComparer.Ordinal);
    private readonly CoreAudioDeviceNotificationSink _deviceNotifications;
    private IMMDeviceEnumerator? _deviceEnumerator;
    private nint _mtaCookie;
    private SpotifyVolumeSnapshot _snapshot = SpotifyVolumeSnapshot.Unavailable;
    private int _initialized;
    private int _disposed;
    private int _refreshRequested;
    private int _refreshWorkerRunning;

    public SpotifyVolumeService() => _deviceNotifications = new CoreAudioDeviceNotificationSink(this);

    public event EventHandler<SpotifyVolumeSnapshot>? Changed;

    public SpotifyVolumeSnapshot Snapshot => Volatile.Read(ref _snapshot);

    internal Action<string>? DiagnosticSink { get; set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Volatile.Write(ref _initialized, 1);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sets the scalar on every currently enumerated Spotify.exe render session.</summary>
    public async Task<bool> SetVolumeAsync(double scalar0To1, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!double.IsFinite(scalar0To1)) return false;

        var target = Math.Clamp(scalar0To1, 0d, 1d);
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            return await Task.Run(() =>
            {
                using var apartment = CoreAudioApartment.Enter();
                RefreshCoreAudio(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                SpotifyAudioSession[] sessions;
                lock (_stateLock) sessions = _sessions.Values.ToArray();
                if (sessions.Length == 0) return false;

                var successCount = 0;
                foreach (var session in sessions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        // Set the requested level before unmuting, avoiding a burst at the old level.
                        Marshal.ThrowExceptionForHR(session.Volume.SetMasterVolume((float)target, IntPtr.Zero));
                        if (target > 0d) Marshal.ThrowExceptionForHR(session.Volume.SetMute(0, IntPtr.Zero));
                        successCount++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // A session can disappear despite the refresh gate if the system disconnects it.
                    }
                }

                Publish(ReadSnapshot());
                return successCount == sessions.Length;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Changes mute state for all currently enumerated Spotify.exe render sessions without changing their volume scalar.</summary>
    public async Task<bool> SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            return await Task.Run(() =>
            {
                using var apartment = CoreAudioApartment.Enter();
                RefreshCoreAudio(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                SpotifyAudioSession[] sessions;
                lock (_stateLock) sessions = _sessions.Values.ToArray();
                if (sessions.Length == 0) return false;

                var successCount = 0;
                foreach (var session in sessions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        Marshal.ThrowExceptionForHR(session.Volume.SetMute(muted ? 1 : 0, IntPtr.Zero));
                        successCount++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // A session can disappear despite the refresh gate if the system disconnects it.
                    }
                }

                Publish(ReadSnapshot());
                return successCount == sessions.Length;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => RefreshCoreAudio(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void RefreshCoreAudio(CancellationToken cancellationToken)
    {
        using var apartment = CoreAudioApartment.Enter();
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0) return;

        try
        {
            EnsureDeviceEnumerator();
            var spotifyProcessIds = GetSpotifyProcessIds();
            DiagnosticSink?.Invoke($"Spotify.exe processes: {spotifyProcessIds.Count}.");
            EnumerateActiveRenderEndpoints(spotifyProcessIds, cancellationToken);
            Publish(ReadSnapshot());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticSink?.Invoke($"Core Audio endpoint enumeration failed: {ex.GetType().Name} (0x{ex.HResult:X8}).");
            Publish(SpotifyVolumeSnapshot.Unavailable);
        }
    }

    private void EnsureDeviceEnumerator()
    {
        if (_deviceEnumerator is not null) return;
        // Keep the MTA alive across idle ThreadPool workers for the lifetime of the notifications.
        if (_mtaCookie == 0) Marshal.ThrowExceptionForHR(CoIncrementMTAUsage(out _mtaCookie));
        var classId = MmDeviceEnumeratorClassId;
        var interfaceId = MmDeviceEnumeratorInterfaceId;
        var result = CoCreateInstance(ref classId, IntPtr.Zero, ClsctxInprocServer, ref interfaceId, out var enumerator);
        Marshal.ThrowExceptionForHR(result);
        DiagnosticSink?.Invoke("IMMDeviceEnumerator COM activation succeeded.");

        _deviceEnumerator = enumerator;
        Marshal.ThrowExceptionForHR(_deviceEnumerator.RegisterEndpointNotificationCallback(_deviceNotifications));
        DiagnosticSink?.Invoke("Endpoint notification callback registered.");
    }

    private void EnumerateActiveRenderEndpoints(HashSet<uint> spotifyProcessIds, CancellationToken cancellationToken)
    {
        var enumerator = _deviceEnumerator!;
        Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(ERender, DeviceStateActive, out var collection));
        var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
            DiagnosticSink?.Invoke($"Active render endpoints: {count}.");
            for (uint index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IMMDevice? device = null;
                try
                {
                    Marshal.ThrowExceptionForHR(collection.Item(index, out device));
                    Marshal.ThrowExceptionForHR(device.GetId(out var endpointId));
                    activeIds.Add(endpointId);
                    var endpoint = GetOrCreateEndpoint(endpointId, device);
                    device = null; // The endpoint retains this COM reference.
                    RefreshEndpointSessions(endpoint, spotifyProcessIds, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Continue enumerating other active render endpoints.
                    DiagnosticSink?.Invoke("Could not inspect an active render endpoint.");
                }
                finally
                {
                    ReleaseComObject(device);
                }
            }
        }
        finally
        {
            ReleaseComObject(collection);
        }

        EndpointState[] removed;
        SpotifyAudioSession[] removedSessions;
        lock (_stateLock)
        {
            var ids = _endpoints.Keys.Where(id => !activeIds.Contains(id)).ToArray();
            removed = ids.Select(id => _endpoints[id]).ToArray();
            foreach (var endpoint in removed) _endpoints.Remove(endpoint.Id);
            var removedKeys = new List<string>();
            foreach (var endpoint in removed)
            {
                removedKeys.AddRange(_sessions.Where(pair => pair.Value.EndpointId.Equals(endpoint.Id, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key));
            }
            removedSessions = removedKeys.Select(RemoveSessionLocked).Where(session => session is not null).Cast<SpotifyAudioSession>().ToArray();
        }

        foreach (var session in removedSessions) TryUnregisterEvents(session);
        foreach (var endpoint in removed) endpoint.Dispose();
    }

    private EndpointState GetOrCreateEndpoint(string endpointId, IMMDevice device)
    {
        EndpointState? existing;
        lock (_stateLock)
        {
            _endpoints.TryGetValue(endpointId, out existing);
        }

        if (existing is not null)
        {
            ReleaseComObject(device);
            return existing;
        }

        var interfaceId = AudioSessionManager2InterfaceId;
        Marshal.ThrowExceptionForHR(device.Activate(ref interfaceId, ClsctxInprocServer, IntPtr.Zero, out var manager));
        var created = new EndpointState(this, endpointId, device, manager);
        lock (_stateLock)
        {
            if (!_endpoints.TryGetValue(endpointId, out existing)) _endpoints.Add(endpointId, created);
        }

        if (existing is not null)
        {
            created.Dispose();
            return existing;
        }

        try
        {
            Marshal.ThrowExceptionForHR(manager.RegisterSessionNotification(created.SessionNotifications));
            created.SessionNotificationRegistered = true;
        }
        catch (Exception)
        {
            // Existing sessions can still be enumerated; future sessions are
            // rediscovered on endpoint/device activity.
        }

        return created;
    }

    private void RefreshEndpointSessions(EndpointState endpoint, HashSet<uint> spotifyProcessIds, CancellationToken cancellationToken)
    {
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        IAudioSessionEnumerator? sessionEnumerator = null;
        try
        {
            Marshal.ThrowExceptionForHR(endpoint.Manager.GetSessionEnumerator(out sessionEnumerator));
            Marshal.ThrowExceptionForHR(sessionEnumerator.GetCount(out var count));
            var spotifySessions = 0;
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IAudioSessionControl? control = null;
                try
                {
                    Marshal.ThrowExceptionForHR(sessionEnumerator.GetSession(index, out control));
                    if (control is not null && ObserveSession(endpoint, control, spotifyProcessIds, seenKeys)) spotifySessions++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // A single stale session must not hide other sessions.
                    DiagnosticSink?.Invoke("Could not inspect an audio session.");
                }
            }
            DiagnosticSink?.Invoke($"Sessions on one render endpoint: {count}; Spotify matches: {spotifySessions}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Pending OnSessionCreated references below may still be usable.
            DiagnosticSink?.Invoke("Could not enumerate sessions on a render endpoint.");
        }
        finally
        {
            ReleaseComObject(sessionEnumerator);
        }

        while (endpoint.PendingSessions.TryDequeue(out var pending))
        {
            try
            {
                ObserveSession(endpoint, pending, spotifyProcessIds, seenKeys);
            }
            catch (Exception)
            {
            }
        }

        SpotifyAudioSession[] endpointSessions;
        lock (_stateLock)
        {
            endpointSessions = _sessions.Values
                .Where(session => session.EndpointId.Equals(endpoint.Id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        var sessionStates = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var session in endpointSessions)
        {
            if (seenKeys.Contains(session.Key)) continue;
            try
            {
                Marshal.ThrowExceptionForHR(session.Control.GetState(out var state));
                sessionStates.Add(session.Key, state == AudioSessionExpired);
            }
            catch (Exception)
            {
                sessionStates.Add(session.Key, true);
            }
        }

        List<SpotifyAudioSession> removedSessions = [];
        lock (_stateLock)
        {
            foreach (var session in endpointSessions)
            {
                if (!_sessions.TryGetValue(session.Key, out var current) || !ReferenceEquals(current, session)) continue;
                if (seenKeys.Contains(session.Key))
                {
                    session.MissedEnumerations = 0;
                    continue;
                }

                session.MissedEnumerations++;
                if ((sessionStates[session.Key] || session.MissedEnumerations >= 3) && RemoveSessionLocked(session.Key) is { } removed)
                {
                    removedSessions.Add(removed);
                }
            }
        }

        foreach (var session in removedSessions) TryUnregisterEvents(session);
    }

    private bool ObserveSession(
        EndpointState endpoint,
        IAudioSessionControl control,
        HashSet<uint> spotifyProcessIds,
        HashSet<string> seenKeys)
    {
        var control2 = (IAudioSessionControl2)control;
        Marshal.ThrowExceptionForHR(control2.GetProcessId(out var processId));
        if (!spotifyProcessIds.Contains(processId))
        {
            return false;
        }
        Marshal.ThrowExceptionForHR(control.GetState(out var sessionState));
        if (sessionState == AudioSessionExpired) return false;

        Marshal.ThrowExceptionForHR(control2.GetSessionInstanceIdentifier(out var instanceId));
        var key = endpoint.Id + "|" + instanceId;
        seenKeys.Add(key);

        lock (_stateLock)
        {
            if (_sessions.TryGetValue(key, out var existing))
            {
                existing.MissedEnumerations = 0;
                return true;
            }
        }

        var volume = (ISimpleAudioVolume)control;
        var events = new SpotifyAudioSessionEvents(this);
        var entry = new SpotifyAudioSession(key, endpoint.Id, processId, control, control2, volume, events);
        try
        {
            Marshal.ThrowExceptionForHR(control.RegisterAudioSessionNotification(events));
            entry.EventsRegistered = true;
        }
        catch (Exception)
        {
            // Snapshot and SetMasterVolume still work for this session.
        }

        var duplicate = false;
        lock (_stateLock)
        {
            if (_sessions.TryGetValue(key, out var existing))
            {
                existing.MissedEnumerations = 0;
                duplicate = true;
            }
            else _sessions.Add(key, entry);
        }

        if (duplicate && entry.EventsRegistered) TryUnregisterEvents(entry);
        return true;
    }

    private SpotifyVolumeSnapshot ReadSnapshot()
    {
        SpotifyAudioSession[] sessions;
        lock (_stateLock) sessions = _sessions.Values.ToArray();
        DiagnosticSink?.Invoke($"Tracked Spotify audio sessions: {sessions.Length}.");
        if (sessions.Length == 0) return SpotifyVolumeSnapshot.Unavailable;

        var readings = new List<(SpotifyAudioSession Session, int State, float Volume, int Mute)>();
        foreach (var session in sessions)
        {
            try
            {
                Marshal.ThrowExceptionForHR(session.Control.GetState(out var state));
                if (state == AudioSessionExpired) continue;
                Marshal.ThrowExceptionForHR(session.Volume.GetMasterVolume(out var volume));
                Marshal.ThrowExceptionForHR(session.Volume.GetMute(out var mute));
                readings.Add((session, state, Math.Clamp(volume, 0f, 1f), mute));
            }
            catch (Exception ex)
            {
                // A session can expire between the snapshot and these reads.
                DiagnosticSink?.Invoke($"Could not read Spotify session volume: {ex.GetType().Name} (0x{ex.HResult:X8}).");
            }
        }

        var activeFirst = readings.OrderBy(reading => reading.State == AudioSessionActive ? 0 : 1)
            .ThenBy(reading => reading.Session.EndpointId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(reading => reading.Session.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        return activeFirst.Session is null
            ? SpotifyVolumeSnapshot.Unavailable
            : new SpotifyVolumeSnapshot(true, activeFirst.Volume, activeFirst.Mute != 0);
    }

    private void Publish(SpotifyVolumeSnapshot snapshot)
    {
        var previous = Interlocked.Exchange(ref _snapshot, snapshot);
        if (previous == snapshot || Volatile.Read(ref _disposed) != 0) return;

        void RaiseChanged()
        {
            if (Volatile.Read(ref _disposed) == 0 && ReferenceEquals(Snapshot, snapshot))
            {
                Changed?.Invoke(this, snapshot);
            }
        }

        if (_notificationContext is null || ReferenceEquals(SynchronizationContext.Current, _notificationContext))
        {
            RaiseChanged();
        }
        else
        {
            _notificationContext.Post(static state => ((Action)state!).Invoke(), (Action)RaiseChanged);
        }
    }

    private void QueueRefresh()
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _initialized) == 0) return;
        Interlocked.Exchange(ref _refreshRequested, 1);
        if (Interlocked.CompareExchange(ref _refreshWorkerRunning, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (Interlocked.Exchange(ref _refreshRequested, 0) != 0 && Volatile.Read(ref _disposed) == 0)
                {
                    try { await RefreshAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { }
                }
            }
            finally
            {
                Volatile.Write(ref _refreshWorkerRunning, 0);
                if (Volatile.Read(ref _refreshRequested) != 0) QueueRefresh();
            }
        });
    }

    private void QueueSessionCreated(string endpointId, IAudioSessionControl control)
    {
        lock (_stateLock)
        {
            if (_endpoints.TryGetValue(endpointId, out var endpoint)) endpoint.PendingSessions.Enqueue(control);
            else return;
        }

        QueueRefresh();
    }

    private static HashSet<uint> GetSpotifyProcessIds()
    {
        var result = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName("Spotify"))
        {
            using (process)
            {
                try
                {
                    result.Add(checked((uint)process.Id));
                }
                catch (Exception)
                {
                    // The process may exit while the snapshot is being taken.
                }
            }
        }

        return result;
    }

    private SpotifyAudioSession? RemoveSessionLocked(string key)
    {
        if (!_sessions.Remove(key, out var session)) return null;
        return session;
    }

    private static void TryUnregisterEvents(SpotifyAudioSession session)
    {
        try { session.Control.UnregisterAudioSessionNotification(session.Events); }
        catch (Exception) { }
        session.EventsRegistered = false;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.ReleaseComObject(value); }
        catch (InvalidComObjectException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _initialized, 0);
        try
        {
            Task.Run(() =>
            {
                using var apartment = CoreAudioApartment.Enter();
                _refreshGate.Wait();
                try
                {
                    SpotifyAudioSession[] sessions;
                    EndpointState[] endpoints;
                    lock (_stateLock)
                    {
                        sessions = _sessions.Keys.ToArray().Select(RemoveSessionLocked).Where(session => session is not null).Cast<SpotifyAudioSession>().ToArray();
                        endpoints = _endpoints.Values.ToArray();
                        _endpoints.Clear();
                    }

                    foreach (var session in sessions) TryUnregisterEvents(session);
                    foreach (var endpoint in endpoints) endpoint.Dispose();

                    if (_deviceEnumerator is not null)
                    {
                        try { _deviceEnumerator.UnregisterEndpointNotificationCallback(_deviceNotifications); }
                        catch (Exception) { }
                        ReleaseComObject(_deviceEnumerator);
                        _deviceEnumerator = null;
                    }
                }
                finally
                {
                    var cookie = Interlocked.Exchange(ref _mtaCookie, 0);
                    if (cookie != 0) CoDecrementMTAUsage(cookie);
                    _refreshGate.Release();
                }
            }).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Shutdown must remain best-effort if Windows Audio is unavailable.
        }

        Publish(SpotifyVolumeSnapshot.Unavailable);
        Changed = null;
    }

    private sealed class EndpointState : IDisposable
    {
        private readonly SpotifyVolumeService _owner;
        public string Id { get; }
        public IMMDevice Device { get; }
        public IAudioSessionManager2 Manager { get; }
        public SpotifyAudioSessionNotification SessionNotifications { get; }
        public ConcurrentQueue<IAudioSessionControl> PendingSessions { get; } = new();
        public bool SessionNotificationRegistered { get; set; }

        public EndpointState(SpotifyVolumeService owner, string id, IMMDevice device, IAudioSessionManager2 manager)
        {
            _owner = owner;
            Id = id;
            Device = device;
            Manager = manager;
            SessionNotifications = new SpotifyAudioSessionNotification(owner, id);
        }

        public void Dispose()
        {
            if (SessionNotificationRegistered)
            {
                try { Manager.UnregisterSessionNotification(SessionNotifications); }
                catch (Exception) { }
                SessionNotificationRegistered = false;
            }

            while (PendingSessions.TryDequeue(out _)) { }
            ReleaseComObject(Manager);
            ReleaseComObject(Device);
        }
    }

    private sealed class SpotifyAudioSession
    {
        public string Key { get; }
        public string EndpointId { get; }
        public uint ProcessId { get; }
        public IAudioSessionControl Control { get; }
        public IAudioSessionControl2 Control2 { get; }
        public ISimpleAudioVolume Volume { get; }
        public SpotifyAudioSessionEvents Events { get; }
        public bool EventsRegistered { get; set; }
        public int MissedEnumerations { get; set; }

        public SpotifyAudioSession(string key, string endpointId, uint processId, IAudioSessionControl control, IAudioSessionControl2 control2, ISimpleAudioVolume volume, SpotifyAudioSessionEvents events)
        {
            Key = key;
            EndpointId = endpointId;
            ProcessId = processId;
            Control = control;
            Control2 = control2;
            Volume = volume;
            Events = events;
        }
    }

    private sealed class CoreAudioApartment : IDisposable
    {
        private readonly bool _uninitialize;
        private CoreAudioApartment(bool uninitialize) => _uninitialize = uninitialize;

        public static CoreAudioApartment Enter()
        {
            var result = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
            if (result < 0 && result != RpcEChangedMode) Marshal.ThrowExceptionForHR(result);
            return new CoreAudioApartment(result >= 0);
        }

        public void Dispose()
        {
            if (_uninitialize) CoUninitialize();
        }
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoInitializeEx(IntPtr reserved, int coInit);
    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoIncrementMTAUsage(out nint cookie);
    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoDecrementMTAUsage(nint cookie);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, uint context, ref Guid interfaceId, out IMMDeviceEnumerator instance);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback([MarshalAs(UnmanagedType.Interface)] IMMNotificationClient callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback([MarshalAs(UnmanagedType.Interface)] IMMNotificationClient callback);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, uint context, IntPtr activationParams, out IAudioSessionManager2 interfacePointer);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint streamFlags, out IAudioSessionControl sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint streamFlags, out ISimpleAudioVolume simpleAudioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
        [PreserveSig] int RegisterSessionNotification([MarshalAs(UnmanagedType.Interface)] IAudioSessionNotification callback);
        [PreserveSig] int UnregisterSessionNotification([MarshalAs(UnmanagedType.Interface)] IAudioSessionNotification callback);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr callback);
        [PreserveSig] int UnregisterDuckNotification(IntPtr callback);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl sessionControl);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, IntPtr eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingId);
        [PreserveSig] int SetGroupingParam(ref Guid groupingId, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification([MarshalAs(UnmanagedType.Interface)] IAudioSessionEvents callback);
        [PreserveSig] int UnregisterAudioSessionNotification([MarshalAs(UnmanagedType.Interface)] IAudioSessionEvents callback);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, IntPtr eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingId);
        [PreserveSig] int SetGroupingParam(ref Guid groupingId, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification([MarshalAs(UnmanagedType.Interface)] IAudioSessionEvents callback);
        [PreserveSig] int UnregisterAudioSessionNotification([MarshalAs(UnmanagedType.Interface)] IAudioSessionEvents callback);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionId);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionInstanceId);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(int optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, IntPtr eventContext);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(int isMuted, IntPtr eventContext);
        [PreserveSig] int GetMute(out int isMuted);
    }

    [ComVisible(true), Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionNotification
    {
        [PreserveSig] int OnSessionCreated([MarshalAs(UnmanagedType.Interface)] IAudioSessionControl sessionControl);
    }

    [ComVisible(true), Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionEvents
    {
        [PreserveSig] int OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)] string newDisplayName, IntPtr eventContext);
        [PreserveSig] int OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)] string newIconPath, IntPtr eventContext);
        [PreserveSig] int OnSimpleVolumeChanged(float newVolume, int newMute, IntPtr eventContext);
        [PreserveSig] int OnChannelVolumeChanged(uint channelCount, IntPtr newChannelVolumeArray, uint changedChannel, IntPtr eventContext);
        [PreserveSig] int OnGroupingParamChanged(ref Guid newGroupingParam, IntPtr eventContext);
        [PreserveSig] int OnStateChanged(int newState);
        [PreserveSig] int OnSessionDisconnected(int disconnectReason);
    }

    [ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class SpotifyAudioSessionNotification : IAudioSessionNotification
    {
        private readonly SpotifyVolumeService _owner;
        private readonly string _endpointId;
        public SpotifyAudioSessionNotification(SpotifyVolumeService owner, string endpointId) { _owner = owner; _endpointId = endpointId; }
        public int OnSessionCreated(IAudioSessionControl sessionControl) { _owner.QueueSessionCreated(_endpointId, sessionControl); return 0; }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class SpotifyAudioSessionEvents : IAudioSessionEvents
    {
        private readonly SpotifyVolumeService _owner;
        public SpotifyAudioSessionEvents(SpotifyVolumeService owner) => _owner = owner;
        public int OnDisplayNameChanged(string newDisplayName, IntPtr eventContext) { return 0; }
        public int OnIconPathChanged(string newIconPath, IntPtr eventContext) { return 0; }
        public int OnSimpleVolumeChanged(float newVolume, int newMute, IntPtr eventContext) { _owner.QueueRefresh(); return 0; }
        public int OnChannelVolumeChanged(uint channelCount, IntPtr newChannelVolumeArray, uint changedChannel, IntPtr eventContext) { return 0; }
        public int OnGroupingParamChanged(ref Guid newGroupingParam, IntPtr eventContext) { return 0; }
        public int OnStateChanged(int newState) { _owner.QueueRefresh(); return 0; }
        public int OnSessionDisconnected(int disconnectReason) { _owner.QueueRefresh(); return 0; }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class CoreAudioDeviceNotificationSink : IMMNotificationClient
    {
        private readonly SpotifyVolumeService _owner;
        public CoreAudioDeviceNotificationSink(SpotifyVolumeService owner) => _owner = owner;
        public int OnDeviceStateChanged(string deviceId, uint newState) { _owner.QueueRefresh(); return 0; }
        public int OnDeviceAdded(string deviceId) { _owner.QueueRefresh(); return 0; }
        public int OnDeviceRemoved(string deviceId) { _owner.QueueRefresh(); return 0; }
        public int OnDefaultDeviceChanged(int flow, int role, string defaultDeviceId) { _owner.QueueRefresh(); return 0; }
        public int OnPropertyValueChanged(string deviceId, PropertyKey key) { _owner.QueueRefresh(); return 0; }
    }
}
