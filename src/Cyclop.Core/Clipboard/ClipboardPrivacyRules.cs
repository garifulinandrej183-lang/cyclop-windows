namespace Cyclop.Core.Clipboard;

/// <summary>Pure privacy gate for registered clipboard exclusion markers.</summary>
public static class ClipboardPrivacyRules
{
    public const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";

    public static bool ShouldExclude(IEnumerable<string> registeredFormats, Func<string, uint?> readPolicyDword)
    {
        ArgumentNullException.ThrowIfNull(registeredFormats); ArgumentNullException.ThrowIfNull(readPolicyDword);
        if (registeredFormats.Any(format => format.Equals(ExcludeFormat, StringComparison.OrdinalIgnoreCase))) return true;
        var canIncludeMarker = registeredFormats.Any(format => format.Equals("CanIncludeInClipboardHistory", StringComparison.OrdinalIgnoreCase));
        if (canIncludeMarker && readPolicyDword("CanIncludeInClipboardHistory") != 1) return true;
        return false;
    }
}
