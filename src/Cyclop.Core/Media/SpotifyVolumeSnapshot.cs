namespace Cyclop.Core.Media;

/// <summary>Current per-application audio-session volume exposed by Windows Core Audio.</summary>
public sealed record SpotifyVolumeSnapshot(bool IsAvailable, double Volume, bool IsMuted)
{
    public static SpotifyVolumeSnapshot Unavailable { get; } = new(false, 0, false);
}
