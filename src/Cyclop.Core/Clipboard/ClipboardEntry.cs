namespace Cyclop.Core.Clipboard;

public sealed record ClipboardEntry(Guid Id, DateTimeOffset CreatedUtc, ClipboardPayload Payload, string Preview, bool IsPinned);

public sealed class ClipboardCaptureOptions
{
    /// <summary>Recording is opt-in. Default is disabled.</summary>
    public bool ExplicitlyEnabled { get; set; }
    public bool IsPaused { get; set; }
    public bool SaveImagesToShelf { get; set; }
    public int MaxItems { get; set; } = 100;
    public int MaxPinnedItems { get; set; } = 20;
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(30);
    public int MaxPayloadBytes { get; set; } = 16 * 1024 * 1024;
    public long MaxTotalPayloadBytes { get; set; } = 64L * 1024 * 1024;
}
