using System.Text.Json;

namespace Cyclop.Core.Settings;

public sealed class AppSettings
{
    public bool CaptureClipboard { get; set; }
    public bool ClipboardPaused { get; set; }
    public bool AutoSaveImages { get; set; }
    public bool StartAtLogin { get; set; }
    public bool WatchScreenshots { get; set; }
    public string ScreenshotFolder { get; set; } = "";
    public int HistoryLimit { get; set; } = 100;
    public int HistoryDays { get; set; } = 14;
    public int ScreenshotLimit { get; set; } = 100;
    public string MonitorDevice { get; set; } = "";
    public uint HotkeyModifiers { get; set; } = 0x0002 | 0x0001; // Ctrl+Alt
    public uint HotkeyKey { get; set; } = 0x20; // Space
    public bool ReducedMotion { get; set; }
    public static string DataRoot => Environment.GetEnvironmentVariable("CYCLOP_DATA_ROOT") is { Length: > 0 } root
        ? Path.GetFullPath(root) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CyclopWindows");
    public static AppSettings Load(string path)
    {
        try { return Validate(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(string path)
    {
        Validate(this);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
    private static AppSettings Validate(AppSettings value)
    {
        value.HistoryLimit = Math.Clamp(value.HistoryLimit, 10, 500);
        value.HistoryDays = Math.Clamp(value.HistoryDays, 1, 90);
        value.ScreenshotLimit = Math.Clamp(value.ScreenshotLimit, 10, 1000);
        return value;
    }
}
