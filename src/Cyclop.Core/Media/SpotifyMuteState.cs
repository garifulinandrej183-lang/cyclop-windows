namespace Cyclop.Core.Media;

public sealed record SpotifyMuteAction(bool IsMuted, double? RestoreVolume = null);

/// <summary>Remembers real nonzero levels; never invents a level for an initially silent session.</summary>
public sealed class SpotifyMuteState
{
    private double? _lastNonzero;

    public void Observe(SpotifyVolumeSnapshot snapshot)
    {
        if (IsValid(snapshot) && snapshot.Volume > 0) _lastNonzero = snapshot.Volume;
    }

    public SpotifyMuteAction? GetToggle(SpotifyVolumeSnapshot snapshot)
    {
        if (!IsValid(snapshot)) return null;
        if (snapshot.Volume > 0) return new(!snapshot.IsMuted);
        return _lastNonzero is > 0 ? new(false, _lastNonzero) : null;
    }

    private static bool IsValid(SpotifyVolumeSnapshot snapshot) => snapshot.IsAvailable &&
        double.IsFinite(snapshot.Volume) && snapshot.Volume is >= 0 and <= 1;
}
