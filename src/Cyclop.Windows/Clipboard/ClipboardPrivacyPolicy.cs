using System.Runtime.InteropServices;
using Cyclop.Core.Clipboard;

namespace Cyclop.Windows.Clipboard;

/// <summary>Checks opt-out clipboard formats before requesting any payload data.</summary>
public static class ClipboardPrivacyPolicy
{
    public const string ExclusionFormat = "ExcludeClipboardContentFromMonitorProcessing";
    public const string InternalFormat = "Cyclop.Windows.InternalClipboardWrite";

    public static bool IsExcluded()
    {
        if (!OpenClipboard(0)) throw new ExternalException("Windows clipboard is busy.", Marshal.GetLastWin32Error());
        try
        {
            var formats = EnumerateFormats();
            if (formats.Contains(InternalFormat, StringComparer.OrdinalIgnoreCase)) return true;
            var canInclude = RegisterClipboardFormat("CanIncludeInClipboardHistory");
            uint? value = formats.Contains("CanIncludeInClipboardHistory", StringComparer.OrdinalIgnoreCase) ? ReadDwordFormat(canInclude) : null;
            return ClipboardPrivacyRules.ShouldExclude(formats, name => name == "CanIncludeInClipboardHistory" ? value : null);
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Pure predicate for privacy behavior tests.</summary>
    public static bool ShouldExclude(IEnumerable<string> clipboardFormats, Func<string, uint?> readDword)
    {
        return ClipboardPrivacyRules.ShouldExclude(clipboardFormats, readDword);
    }

    private static uint? ReadDwordFormat(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == 0) return null;
        var size = GlobalSize(handle);
        if (size < sizeof(uint)) return null;
        var pointer = GlobalLock(handle);
        if (pointer == 0) return null;
        try { return unchecked((uint)Marshal.ReadInt32(pointer)); }
        finally { GlobalUnlock(handle); }
    }

    private static string[] EnumerateFormats()
    {
        var result = new List<string>(); uint format = 0;
        while ((format = EnumClipboardFormats(format)) != 0)
        {
            var name = new System.Text.StringBuilder(256);
            var length = GetClipboardFormatName(format, name, name.Capacity);
            if (length > 0) result.Add(name.ToString());
        }
        return result.ToArray();
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint RegisterClipboardFormat(string format);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetClipboardFormatName(uint format, System.Text.StringBuilder name, int maxCount);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nuint GlobalSize(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(nint handle);
}
