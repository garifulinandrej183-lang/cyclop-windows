using Cyclop.Core.Media;
using Xunit;

namespace Cyclop.Tests;

public sealed class VolumeTests
{
    [Fact]
    public void NormalMuteRoundTripPreservesNativeScalar()
    {
        var state = new SpotifyMuteState();
        var audible = new SpotifyVolumeSnapshot(true, .63, false);
        state.Observe(audible);
        Assert.Equal(new SpotifyMuteAction(true), state.GetToggle(audible));
        var muted = audible with { IsMuted = true }; state.Observe(muted);
        Assert.Equal(new SpotifyMuteAction(false), state.GetToggle(muted));
    }

    [Fact]
    public void ZeroSliderRestoresLastObservedLevelRatherThanMaximum()
    {
        var state = new SpotifyMuteState();
        state.Observe(new(true, .28, false));
        var silent = new SpotifyVolumeSnapshot(true, 0, false); state.Observe(silent);
        Assert.Equal(new SpotifyMuteAction(false, .28), state.GetToggle(silent));
    }

    [Fact]
    public void InitiallySilentSessionDoesNotGuessAnAudibleLevel()
    {
        var state = new SpotifyMuteState();
        Assert.Null(state.GetToggle(new(true, 0, false)));
        Assert.Null(state.GetToggle(new(true, 0, true)));
        Assert.Null(state.GetToggle(SpotifyVolumeSnapshot.Unavailable));
        // A muted session can expose its retained native level even at first launch.
        Assert.Equal(new SpotifyMuteAction(false), state.GetToggle(new(true, .42, true)));
    }

    [Fact]
    public void ExternalVolumeChangesReplaceRememberedLevelAndInvalidReadingsDoNot()
    {
        var state = new SpotifyMuteState();
        state.Observe(new(true, .2, false)); state.Observe(new(true, .77, false));
        state.Observe(new(true, double.NaN, false)); state.Observe(new(true, 2, false));
        Assert.Equal(new SpotifyMuteAction(false, .77), state.GetToggle(new(true, 0, false)));
        Assert.Null(state.GetToggle(new(true, double.NaN, false)));
    }
}
