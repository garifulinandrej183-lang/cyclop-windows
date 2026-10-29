namespace Cyclop.Core.Media;

/// <summary>Sanitizes GSMTC timeline values before they are shown or used for seeking.</summary>
public readonly record struct SpotifyTimeline(TimeSpan Position, TimeSpan Duration, TimeSpan SeekStart, TimeSpan SeekEnd, bool CanSeek)
{
    public static SpotifyTimeline Create(
        TimeSpan position,
        TimeSpan endTime,
        TimeSpan minSeekTime,
        TimeSpan maxSeekTime,
        bool playbackPositionEnabled)
    {
        var duration = NonNegative(endTime);
        var start = Clamp(minSeekTime, TimeSpan.Zero, duration);
        var end = Clamp(maxSeekTime, start, duration);
        var safePosition = Clamp(position, start, duration);
        var canSeek = playbackPositionEnabled && duration > TimeSpan.Zero && end > start;

        if (!canSeek)
        {
            // A non-seekable session can still report its current position.
            safePosition = Clamp(position, TimeSpan.Zero, duration);
            start = TimeSpan.Zero;
            end = duration;
        }

        return new SpotifyTimeline(safePosition, duration, start, end, canSeek);
    }

    public TimeSpan ClampSeekTarget(TimeSpan target) => Clamp(target, SeekStart, SeekEnd);

    private static TimeSpan NonNegative(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}
