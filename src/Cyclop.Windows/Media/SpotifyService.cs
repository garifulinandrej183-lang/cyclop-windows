using System.Collections.Immutable;
using System.IO;
using System.Threading;
using Cyclop.Core.Media;
using global::Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Cyclop.Windows.Media;

/// <summary>Tracks and controls only Spotify's desktop GSMTC session.</summary>
public sealed class SpotifyService : IDisposable
{
    private const int MaxArtworkBytes = 8 * 1024 * 1024;

    private readonly SynchronizationContext? _notificationContext = SynchronizationContext.Current;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs>? _sessionsChangedHandler;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs>? _currentSessionChangedHandler;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs>? _mediaChangedHandler;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs>? _playbackChangedHandler;
    private TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs>? _timelineChangedHandler;
    private CancellationTokenSource? _sessionLifetime;
    private SpotifySnapshot _snapshot = SpotifySnapshot.Empty;
    private long _generation;
    private int _disposed;

    public event EventHandler<SpotifySnapshot>? Changed;

    public SpotifySnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Connects to GSMTC and begins observing the Spotify session, if present.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manager = _manager;
            if (manager is null)
            {
                manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync()
                    .AsTask(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

                _manager = manager;
                _sessionsChangedHandler = (_, _) => _ = RebindAsync();
                _currentSessionChangedHandler = (_, _) => _ = RebindAsync();
                manager.SessionsChanged += _sessionsChangedHandler;
                manager.CurrentSessionChanged += _currentSessionChangedHandler;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        await RebindAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> PlayPauseAsync(CancellationToken cancellationToken = default)
    {
        var session = _session;
        if (session is null) return false;
        try
        {
            var playback = session.GetPlaybackInfo();
            var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            return await TryCommandAsync(
                session,
                () => playing ? playback.Controls.IsPauseEnabled : playback.Controls.IsPlayEnabled,
                playing ? s => s.TryPauseAsync() : s => s.TryPlayAsync(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public Task<bool> PreviousAsync(CancellationToken cancellationToken = default)
    {
        var session = _session;
        return session is null
            ? Task.FromResult(false)
            : TryCommandAsync(session, () => session.GetPlaybackInfo().Controls.IsPreviousEnabled,
                s => s.TrySkipPreviousAsync(), cancellationToken);
    }

    public Task<bool> NextAsync(CancellationToken cancellationToken = default)
    {
        var session = _session;
        return session is null
            ? Task.FromResult(false)
            : TryCommandAsync(session, () => session.GetPlaybackInfo().Controls.IsNextEnabled,
                s => s.TrySkipNextAsync(), cancellationToken);
    }

    public async Task<bool> SeekAsync(TimeSpan target, CancellationToken cancellationToken = default)
    {
        var session = _session;
        var snapshot = Snapshot;
        if (session is null || !snapshot.CanSeek)
        {
            return false;
        }

        var safeTarget = target < snapshot.SeekStart ? snapshot.SeekStart : target > snapshot.SeekEnd ? snapshot.SeekEnd : target;
        long ticks;
        try
        {
            ticks = safeTarget.Ticks;
        }
        catch (OverflowException)
        {
            return false;
        }

        return await TryCommandAsync(session, () => session.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled,
            s => s.TryChangePlaybackPositionAsync(ticks), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> TryCommandAsync(
        GlobalSystemMediaTransportControlsSession session,
        Func<bool> isCapabilityEnabled,
        Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> command,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(session, _session) || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isCapabilityEnabled()) return false;
            return await command(session).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // GSMTC can reject a command after its capability was observed if
            // the player closes or changes tracks in between.
            return false;
        }
    }

    private async Task RebindAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manager = _manager;
            if (manager is null || Volatile.Read(ref _disposed) != 0) return;

            var sessions = manager.GetSessions();
            var current = manager.GetCurrentSession();
            var selected = current is not null && SpotifySessionIdentity.IsSpotify(current.SourceAppUserModelId)
                ? current
                : sessions
                    .Where(s => SpotifySessionIdentity.IsSpotify(s.SourceAppUserModelId))
                    .OrderByDescending(IsPlaying)
                    .FirstOrDefault();

            if (ReferenceEquals(selected, _session))
            {
                if (selected is not null)
                {
                    _ = RefreshAsync(selected, Volatile.Read(ref _generation));
                }
                else if (Snapshot.HasSession)
                {
                    Publish(SpotifySnapshot.Empty);
                }
                return;
            }

            DetachSession();
            if (selected is null)
            {
                Publish(SpotifySnapshot.Empty);
                return;
            }

            _session = selected;
            var generation = Interlocked.Increment(ref _generation);
            _sessionLifetime = new CancellationTokenSource();
            var lifetimeToken = _sessionLifetime.Token;
            _mediaChangedHandler = (_, _) => _ = RefreshAsync(selected, generation, lifetimeToken);
            _playbackChangedHandler = (_, _) => _ = RefreshAsync(selected, generation, lifetimeToken);
            _timelineChangedHandler = (_, _) => _ = RefreshAsync(selected, generation, lifetimeToken);
            selected.MediaPropertiesChanged += _mediaChangedHandler;
            selected.PlaybackInfoChanged += _playbackChangedHandler;
            selected.TimelinePropertiesChanged += _timelineChangedHandler;
            Publish(SpotifySnapshot.Empty with { SourceAppUserModelId = selected.SourceAppUserModelId ?? string.Empty });
            _ = RefreshAsync(selected, generation, lifetimeToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // GSMTC may be unavailable on older or restricted Windows installs.
            if (_session is null) Publish(SpotifySnapshot.Empty);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task RefreshAsync(
        GlobalSystemMediaTransportControlsSession session,
        long generation,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsCurrent(session, generation, cancellationToken)) return;

                var properties = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken).ConfigureAwait(false);
                if (!IsCurrent(session, generation, cancellationToken)) return;

                var playback = session.GetPlaybackInfo();
                var controls = playback.Controls;
                var timelineInfo = session.GetTimelineProperties();
                var timeline = SpotifyTimeline.Create(
                    timelineInfo.Position,
                    timelineInfo.EndTime,
                    timelineInfo.MinSeekTime,
                    timelineInfo.MaxSeekTime,
                    controls.IsPlaybackPositionEnabled);
                var artwork = await ReadArtworkAsync(properties.Thumbnail, cancellationToken).ConfigureAwait(false);
                if (!IsCurrent(session, generation, cancellationToken)) return;

                var snapshot = new SpotifySnapshot(
                    session.SourceAppUserModelId ?? string.Empty,
                    properties.Title ?? string.Empty,
                    properties.Artist ?? string.Empty,
                    artwork,
                    timeline.Position,
                    timeline.Duration,
                    timeline.SeekStart,
                    timeline.SeekEnd,
                    playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    controls.IsPlayEnabled,
                    controls.IsPauseEnabled,
                    controls.IsPreviousEnabled,
                    controls.IsNextEnabled,
                    timeline.CanSeek);
                Publish(snapshot);
            }
            finally
            {
                _refreshGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // A session can disappear while metadata or its thumbnail is read.
            // A later SessionsChanged event will bind the next valid session.
        }
    }

    private static async Task<ImmutableArray<byte>> ReadArtworkAsync(
        IRandomAccessStreamReference? thumbnail,
        CancellationToken cancellationToken)
    {
        if (thumbnail is null) return ImmutableArray<byte>.Empty;

        try
        {
            using var stream = await thumbnail.OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(false);
            var size = stream.Size;
            if (size is 0 or > MaxArtworkBytes) return ImmutableArray<byte>.Empty;

            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)size).AsTask(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = new byte[(int)size];
            reader.ReadBytes(bytes);
            return ImmutableArray.CreateRange(bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Empty, unsupported or transiently inaccessible artwork is valid.
            return ImmutableArray<byte>.Empty;
        }
    }

    private bool IsCurrent(
        GlobalSystemMediaTransportControlsSession session,
        long generation,
        CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && Volatile.Read(ref _disposed) == 0
        && generation == Volatile.Read(ref _generation)
        && ReferenceEquals(session, _session);

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            return false;
        }
    }

    private void Publish(SpotifySnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
        var generation = Volatile.Read(ref _generation);
        void RaiseChanged()
        {
            if (Volatile.Read(ref _disposed) == 0 && generation == Volatile.Read(ref _generation))
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

    private void DetachSession()
    {
        Interlocked.Increment(ref _generation);
        _sessionLifetime?.Cancel();
        _sessionLifetime?.Dispose();
        _sessionLifetime = null;

        var oldSession = _session;
        _session = null;
        if (oldSession is not null)
        {
            if (_mediaChangedHandler is not null) oldSession.MediaPropertiesChanged -= _mediaChangedHandler;
            if (_playbackChangedHandler is not null) oldSession.PlaybackInfoChanged -= _playbackChangedHandler;
            if (_timelineChangedHandler is not null) oldSession.TimelinePropertiesChanged -= _timelineChangedHandler;
        }

        _mediaChangedHandler = null;
        _playbackChangedHandler = null;
        _timelineChangedHandler = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Increment(ref _generation);
        _sessionLifetime?.Cancel();
        _sessionLifetime?.Dispose();
        _sessionLifetime = null;
        DetachSession();

        if (_manager is { } manager)
        {
            if (_sessionsChangedHandler is not null) manager.SessionsChanged -= _sessionsChangedHandler;
            if (_currentSessionChangedHandler is not null) manager.CurrentSessionChanged -= _currentSessionChangedHandler;
        }

        _manager = null;
        _sessionsChangedHandler = null;
        _currentSessionChangedHandler = null;
        Publish(SpotifySnapshot.Empty);
        Changed = null;
    }
}
