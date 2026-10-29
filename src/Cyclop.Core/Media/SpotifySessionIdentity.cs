namespace Cyclop.Core.Media;

/// <summary>Strict allow-list for Spotify's supported Windows desktop packages.</summary>
public static class SpotifySessionIdentity
{
    public const string StorePackageFamilyName = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0";
    public const string StoreAumid = StorePackageFamilyName + "!Spotify";

    private static readonly HashSet<string> DesktopIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Spotify.exe",
        "Spotify",
    };

    public static bool IsSpotify(string? sourceAppUserModelId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
        {
            return false;
        }

        var id = sourceAppUserModelId.Trim();
        if (DesktopIds.Contains(id) || string.Equals(id, StoreAumid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A packaged app's AUMID starts with its package family name. Keep the
        // package check exact and constrain the application id to Spotify.
        return id.StartsWith(StorePackageFamilyName + "!", StringComparison.OrdinalIgnoreCase)
            && string.Equals(id[(StorePackageFamilyName.Length + 1)..], "Spotify", StringComparison.OrdinalIgnoreCase);
    }
}
