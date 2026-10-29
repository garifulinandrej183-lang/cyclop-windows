using System.Windows;
using Cyclop.Core.Media;
using Cyclop.Windows.Media;

namespace Cyclop.App;

public sealed partial class MainWindow
{
    private SpotifyVolumeService? _spotifyVolume;
    private SpotifyVolumeSnapshot _volumeSnapshot = SpotifyVolumeSnapshot.Unavailable;
    private CancellationTokenSource? _volumeDebounce;
    private long _volumeGeneration;
    private bool _volumeSync, _volumeDragging, _volumeWritePending, _volumeWriterActive;
    private double _requestedVolume;
    private readonly SpotifyMuteState _muteState = new();
    private bool _muteChanging;
    private const string VolumeFailure = "Не удалось изменить громкость Spotify. Проверьте аудиосессию в микшере Windows.";

    private async Task InitializeVolumeAsync()
    {
        _spotifyVolume = new SpotifyVolumeService();
        _spotifyVolume.Changed += (_, snapshot) => Dispatcher.BeginInvoke(() => ShowVolume(snapshot));
        try { await _spotifyVolume.InitializeAsync(_lifetime.Token); ShowVolume(_spotifyVolume.Snapshot); }
        catch (OperationCanceledException) { }
        catch (Exception) { ShowVolume(SpotifyVolumeSnapshot.Unavailable); }
    }

    private void ShowVolume(SpotifyVolumeSnapshot snapshot)
    {
        if (_disposed) return;
        _volumeSnapshot = snapshot; _muteState.Observe(snapshot); _vm.CanChangeVolume = snapshot.IsAvailable && !_muteChanging;
        _vm.VolumeHint = !snapshot.IsAvailable ? "Включите музыку в Spotify, чтобы Windows создала аудиосессию. Общая громкость Windows не меняется."
            : snapshot.IsMuted ? "Spotify приглушён. Нажмите динамик или переместите ползунок выше нуля, чтобы включить звук."
            : "Громкость только Spotify · 0–100%";
        RefreshMuteButton();
        if (!snapshot.IsAvailable)
        {
            _volumeDebounce?.Cancel(); _volumeWritePending = false; _volumeGeneration++;
            _vm.VolumeLabel = "—";
        }
        else if (_volumeDragging || _volumeWritePending) return;
        _volumeSync = true;
        try
        {
            _volume.Value = snapshot.IsAvailable && !snapshot.IsMuted ? Math.Clamp(snapshot.Volume * 100, 0, 100) : 0;
            if (snapshot.IsAvailable) _vm.VolumeLabel = $"{Math.Round(_volume.Value):0}%";
        }
        finally { _volumeSync = false; }
    }

    private async void QueueVolumeChange()
    {
        if (_volumeSync || _disposed || _spotifyVolume is null || !_vm.CanChangeVolume) return;
        _vm.VolumeLabel = $"{Math.Round(_volume.Value):0}%";
        _requestedVolume = _volume.Value / 100; _volumeGeneration++; _volumeWritePending = true;
        RefreshMuteButton();
        if (_volumeWriterActive) return;
        _volumeWriterActive = true;
        _volumeDebounce?.Dispose(); _volumeDebounce = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _volumeDebounce.Token;
        try
        {
            // Send the latest value at most once per interval, including during a continuous drag.
            while (!_disposed && _vm.CanChangeVolume)
            {
                await Task.Delay(60, token);
                var generation = _volumeGeneration; var scalar = _requestedVolume;
                var success = await _spotifyVolume.SetVolumeAsync(scalar, token);
                if (_disposed) return;
                if (generation != _volumeGeneration) continue;
                if (!success) SetStatus(VolumeFailure); else if (_vm.Status == VolumeFailure) SetStatus("");
                break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_disposed) SetStatus(VolumeFailure); }
        finally
        {
            _volumeWriterActive = false;
            if (!_disposed)
            {
                if (token.IsCancellationRequested && !_lifetime.IsCancellationRequested && _vm.CanChangeVolume && _volumeWritePending)
                {
                    QueueVolumeChange();
                }
                else
                {
                    _volumeWritePending = false;
                    ShowVolume(_spotifyVolume.Snapshot);
                }
            }
        }
    }

    private void EndVolumeDrag()
    {
        if (!_volumeDragging) return;
        _volumeDragging = false;
        if (!_volumeWritePending) ShowVolume(_volumeSnapshot);
        else RefreshMuteButton();
        if (!IsMouseOver) ScheduleLeave();
    }

    private void RefreshMuteButton()
    {
        var action = _muteState.GetToggle(_volumeSnapshot);
        _vm.SoundMuted = _volumeSnapshot.IsAvailable && (_volumeSnapshot.IsMuted || _volumeSnapshot.Volume <= 0);
        var busy = _muteChanging || _volumeWritePending || _volumeWriterActive || _volumeDragging;
        _vm.CanToggleMute = action is not null && !busy;
        _vm.MuteLabel = _vm.SoundMuted ? "Включить звук Spotify" : "Отключить звук Spotify";
        _vm.MuteHint = !_volumeSnapshot.IsAvailable ? _vm.VolumeHint : busy ? "Применяется громкость Spotify…"
            : action is null ? "Поднимите громкость ползунком: прежний уровень неизвестен."
            : _vm.SoundMuted ? $"Включить звук Spotify · {Math.Round((action.RestoreVolume ?? _volumeSnapshot.Volume) * 100):0}%"
            : "Отключить звук Spotify, сохранив громкость";
    }

    private async Task ToggleSpotifyMuteAsync()
    {
        if (_disposed || _spotifyVolume is null || !_vm.CanToggleMute) return;
        var action = _muteState.GetToggle(_volumeSnapshot); if (action is null) return;
        _muteChanging = true; ShowVolume(_volumeSnapshot);
        try
        {
            var success = action.RestoreVolume is double restore
                ? await _spotifyVolume.SetVolumeAsync(restore, _lifetime.Token)
                : await _spotifyVolume.SetMutedAsync(action.IsMuted, _lifetime.Token);
            if (!_disposed && !success) SetStatus(VolumeFailure);
            else if (!_disposed && _vm.Status == VolumeFailure) SetStatus("");
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_disposed) SetStatus(VolumeFailure); }
        finally { _muteChanging = false; if (!_disposed) ShowVolume(_spotifyVolume.Snapshot); }
    }

    private async Task ReconnectVolumeAsync()
    {
        if (_spotifyVolume is null) return;
        try { await _spotifyVolume.InitializeAsync(_lifetime.Token); ShowVolume(_spotifyVolume.Snapshot); }
        catch (OperationCanceledException) { }
        catch (Exception) { ShowVolume(SpotifyVolumeSnapshot.Unavailable); }
    }
}
