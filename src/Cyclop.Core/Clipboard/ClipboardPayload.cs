namespace Cyclop.Core.Clipboard;

public enum ClipboardPayloadKind { Text = 1, Files = 2, Png = 3 }

/// <summary>Transient clipboard value. Callers must not log or persist it outside the encrypted history.</summary>
public sealed record ClipboardPayload
{
    private readonly byte[]? _png;
    private ClipboardPayload(ClipboardPayloadKind kind, string? text, IReadOnlyList<string>? files, byte[]? png)
    { Kind = kind; Text = text; Files = files; _png = png?.ToArray(); }

    public ClipboardPayloadKind Kind { get; }
    public string? Text { get; }
    public IReadOnlyList<string>? Files { get; }
    public byte[]? Png => _png?.ToArray();
    public static ClipboardPayload FromText(string text) => new(ClipboardPayloadKind.Text, text ?? throw new ArgumentNullException(nameof(text)), null, null);
    public static ClipboardPayload FromFiles(IEnumerable<string> paths) => new(ClipboardPayloadKind.Files, null, Array.AsReadOnly(paths?.ToArray() ?? throw new ArgumentNullException(nameof(paths))), null);
    public static ClipboardPayload FromPng(byte[] png) => new(ClipboardPayloadKind.Png, null, null, png ?? throw new ArgumentNullException(nameof(png)));
}
