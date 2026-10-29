using System.Collections.Immutable;

namespace Cyclop.Core.Media;

/// <summary>A point-in-time view of the supported Spotify desktop media session.</summary>
public sealed record SpotifySnapshot(
    string SourceAppUserModelId,
    string Title,
    string Artist,
    ImmutableArray<byte> ArtworkBytes,
    TimeSpan Position,
    TimeSpan Duration,
    TimeSpan SeekStart,
    TimeSpan SeekEnd,
    bool IsPlaying,
    bool CanPlay,
    bool CanPause,
    bool CanSkipPrevious,
    bool CanSkipNext,
    bool CanSeek)
{
    public static SpotifySnapshot Empty { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        ImmutableArray<byte>.Empty,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        false,
        false,
        false,
        false,
        false,
        false);

    public bool HasSession => SourceAppUserModelId.Length != 0;
}
