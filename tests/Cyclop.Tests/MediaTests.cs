using Cyclop.Core.Media;
using Xunit;

namespace Cyclop.Tests;

public sealed class MediaTests
{
    [Theory]
    [InlineData("Spotify.exe")]
    [InlineData("spotify")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify")]
    [InlineData("spotifyab.spotifymusic_zpdnekdrzrea0!spotify")]
    public void RecognizesOnlyKnownSpotifyDesktopAndStoreIds(string sourceId)
    {
        Assert.True(SpotifySessionIdentity.IsSpotify(sourceId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Spotify Web Helper")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!OtherApp")]
    [InlineData("com.squirrel.spotify.spotify")]
    [InlineData("Chrome.exe")]
    [InlineData("msedge.exe")]
    public void DoesNotTreatBrowsersOrUnknownIdsAsSpotify(string? sourceId)
    {
        Assert.False(SpotifySessionIdentity.IsSpotify(sourceId));
    }

    [Fact]
    public void TimelineClampsPositionAndSeekBoundsToDuration()
    {
        var timeline = SpotifyTimeline.Create(
            TimeSpan.FromSeconds(90),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(80),
            playbackPositionEnabled: true);

        Assert.Equal(TimeSpan.FromSeconds(60), timeline.Duration);
        Assert.Equal(TimeSpan.FromSeconds(60), timeline.Position);
        Assert.Equal(TimeSpan.FromSeconds(10), timeline.SeekStart);
        Assert.Equal(TimeSpan.FromSeconds(60), timeline.SeekEnd);
        Assert.True(timeline.CanSeek);
        Assert.Equal(TimeSpan.FromSeconds(10), timeline.ClampSeekTarget(TimeSpan.FromSeconds(-5)));
        Assert.Equal(TimeSpan.FromSeconds(60), timeline.ClampSeekTarget(TimeSpan.FromSeconds(90)));
    }

    [Fact]
    public void DisabledOrEmptySeekCapabilityProducesSafeNonSeekableTimeline()
    {
        var noCapability = SpotifyTimeline.Create(
            TimeSpan.FromSeconds(12),
            TimeSpan.FromSeconds(60),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(60),
            playbackPositionEnabled: false);
        var emptyRange = SpotifyTimeline.Create(
            TimeSpan.FromSeconds(12),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30),
            playbackPositionEnabled: true);

        Assert.False(noCapability.CanSeek);
        Assert.Equal(TimeSpan.FromSeconds(12), noCapability.Position);
        Assert.False(emptyRange.CanSeek);
        Assert.Equal(TimeSpan.FromSeconds(12), emptyRange.Position);
    }
}
