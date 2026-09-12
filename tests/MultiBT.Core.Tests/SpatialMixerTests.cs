using MultiBT.Core.Audio;
using MultiBT.Core.Sync;
using Xunit;

namespace MultiBT.Core.Tests;

/// <summary>
/// Device positioning: turning a configured position into a stereo gain and a distance delay.
/// </summary>
/// <remarks>
/// Two properties matter more than the exact numbers, and are tested first: a device with NO position must
/// come back completely unchanged, and turning positions on must never make a device quieter than it was.
/// Everything else is tuning; those two are correctness.
/// </remarks>
public sealed class SpatialMixerTests
{
    private const double Tolerance = 0.001;

    [Fact]
    public void NoPositionsProducesNoPlacements()
    {
        Assert.Empty(SpatialMixer.ComputePlacements(new Dictionary<string, DevicePosition>()));
    }

    [Fact]
    public void ADeviceAtTheOriginIsLeftExactlyAsItWas()
    {
        // The origin is "at the listener", which is what an unconfigured device behaves as. It must not gain
        // attenuation, panning or delay, or enabling the feature would disturb a set-up that configured
        // nothing.
        var positions = new Dictionary<string, DevicePosition>
        {
            ["front"] = new(0, 0, 0),
            ["right"] = new(3, 0, 0),
        };

        IReadOnlyDictionary<string, SpatialPlacement> placements = SpatialMixer.ComputePlacements(positions);

        Assert.Equal(1.0, placements["front"].LeftGain, Tolerance);
        Assert.Equal(1.0, placements["front"].RightGain, Tolerance);
        Assert.Equal(0.0, placements["front"].DelayMs, Tolerance);
    }

    [Fact]
    public void TheNearestDeviceIsNeverAttenuated()
    {
        // A set with a single positioned device must play at full level: distance attenuation is relative to
        // the closest speaker, so with one speaker there is nothing to be relative to.
        var positions = new Dictionary<string, DevicePosition> { ["only"] = new(5, 0, 0) };

        IReadOnlyDictionary<string, SpatialPlacement> placements = SpatialMixer.ComputePlacements(positions);

        Assert.Equal(1.0, placements["only"].LeftGain + placements["only"].RightGain, Tolerance);
    }

    [Fact]
    public void ADeviceOnTheRightPlaysMostlyTheRightChannel()
    {
        var positions = new Dictionary<string, DevicePosition>
        {
            ["front"] = new(0, 2, 0),
            ["right"] = new(3, 0, 0),
        };

        IReadOnlyDictionary<string, SpatialPlacement> placements = SpatialMixer.ComputePlacements(positions);

        // Directly ahead stays centred.
        Assert.Equal(1.0, placements["front"].LeftGain, Tolerance);
        Assert.Equal(1.0, placements["front"].RightGain, Tolerance);

        // Hard right: the left channel is closed. The right channel is the pan gain (normalised to 1) times
        // the distance gain, and this device is 3 m out against a nearest of 2 m, so 2/3 of full.
        Assert.Equal(0.0, placements["right"].LeftGain, Tolerance);
        Assert.Equal(2.0 / 3.0, placements["right"].RightGain, Tolerance);
    }

    [Fact]
    public void ADeviceOnTheLeftIsTheMirrorImage()
    {
        var positions = new Dictionary<string, DevicePosition>
        {
            ["front"] = new(0, 2, 0),
            ["left"] = new(-3, 0, 0),
        };

        IReadOnlyDictionary<string, SpatialPlacement> placements = SpatialMixer.ComputePlacements(positions);

        Assert.Equal(2.0 / 3.0, placements["left"].LeftGain, Tolerance);
        Assert.Equal(0.0, placements["left"].RightGain, Tolerance);
    }

    [Fact]
    public void APartlyOffAxisDeviceIsBetweenTheTwo()
    {
        // 45 degrees to the right: neither channel is closed, and the sum of the pair is unchanged from a
        // centred device, so panning redistributes rather than reduces.
        var positions = new Dictionary<string, DevicePosition>
        {
            ["front"] = new(0, 2, 0),
            ["diagonal"] = new(1, 2, 0),
        };

        IReadOnlyDictionary<string, SpatialPlacement> placements = SpatialMixer.ComputePlacements(positions);

        SpatialPlacement diagonal = placements["diagonal"];

        // Off to the right but not hard right: neither channel is closed, and the right is favoured. 45
        // degrees is deliberately unity on both (see the pan remarks), so this case avoids it.
        Assert.True(diagonal.RightGain > diagonal.LeftGain);
        Assert.True(diagonal.LeftGain > 0.0);
    }

    [Fact]
    public void ADirectlyOverheadDeviceStaysCentred()
    {
        // Elevation cannot be panned by a pair of speakers, so a ceiling speaker must not be sent sideways.
        var positions = new Dictionary<string, DevicePosition>
        {
            ["front"] = new(0, 2, 0),
            ["ceiling"] = new(0, 0, 2),
        };

        IReadOnlyDictionary<string, SpatialPlacement> placements = SpatialMixer.ComputePlacements(positions);

        Assert.Equal(placements["ceiling"].LeftGain, placements["ceiling"].RightGain, Tolerance);
    }

    [Fact]
    public void TheFarthestDeviceGetsNoDelayAndTheNearestWaits()
    {
        // Sound has to arrive together, so the NEAR device is the one delayed: the far device is already
        // physically late. 1 m is about 2.92 ms at 343 m/s.
        var positions = new Dictionary<string, DevicePosition>
        {
            ["near"] = new(0, 1, 0),
            ["far"] = new(0, 3, 0),
        };

        IReadOnlyDictionary<string, SpatialPlacement> placements = SpatialMixer.ComputePlacements(positions);

        Assert.Equal(0.0, placements["far"].DelayMs, Tolerance);
        Assert.Equal(2.0 / SpatialMixer.SpeedOfSoundMetresPerSecond * 1000.0, placements["near"].DelayMs, Tolerance);
    }

    [Fact]
    public void TheDelayNeverExceedsTheDelayLineLimit()
    {
        var positions = new Dictionary<string, DevicePosition>
        {
            ["near"] = new(0, 0.1, 0),
            ["very-far"] = new(0, 500, 0),
        };

        IReadOnlyDictionary<string, SpatialPlacement> placements = SpatialMixer.ComputePlacements(positions);

        Assert.True(placements["near"].DelayMs <= EngineTunables.MaxDelayMs);
        Assert.True(placements["near"].DelayMs > 0.0);
    }

    [Fact]
    public void DistanceAttenuationHasAFloor()
    {
        // A far device is quieter, but not silenced: making a correctly-placed speaker inaudible is a bug
        // report rather than an effect.
        var positions = new Dictionary<string, DevicePosition>
        {
            ["near"] = new(0, 1, 0),
            ["far"] = new(0, 100, 0),
        };

        IReadOnlyDictionary<string, SpatialPlacement> placements = SpatialMixer.ComputePlacements(positions);

        double farTotal = placements["far"].LeftGain + placements["far"].RightGain;
        double nearTotal = placements["near"].LeftGain + placements["near"].RightGain;

        Assert.True(farTotal < nearTotal);
        Assert.True(farTotal >= SpatialMixer.MinimumDistanceGain * 2.0 - Tolerance);
    }

    [Fact]
    public void UpAndDownDoNotPanButDoAffectDistance()
    {
        var positions = new Dictionary<string, DevicePosition>
        {
            ["floor"] = new(0, 0, -2),
            ["ceiling"] = new(0, 0, 2),
        };

        IReadOnlyDictionary<string, SpatialPlacement> placements = SpatialMixer.ComputePlacements(positions);

        // Equal distance above and below: identical treatment, and both centred.
        Assert.Equal(placements["floor"].LeftGain, placements["ceiling"].LeftGain, Tolerance);
        Assert.Equal(1.0, placements["ceiling"].LeftGain, Tolerance);
        Assert.Equal(1.0, placements["ceiling"].RightGain, Tolerance);
    }
}
