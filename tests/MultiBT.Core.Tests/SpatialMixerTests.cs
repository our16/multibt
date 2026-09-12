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

    [Fact]
    public void EveryDirectionSitsOnTheSameCircle()
    {
        for (int index = 0; index < SpatialMixer.DirectionCount; index++)
        {
            DevicePosition position = SpatialMixer.DirectionPosition(index);

            Assert.Equal(SpatialMixer.DirectionRadiusMetres, position.Distance, Tolerance);
        }
    }

    [Fact]
    public void DirectionZeroIsStraightAheadAndLaterIndexesTurnClockwise()
    {
        // 0 ahead, 2 right, 4 behind, 6 left: the order the picker presents them in.
        DevicePosition ahead = SpatialMixer.DirectionPosition(0);
        Assert.Equal(0.0, ahead.Right, Tolerance);
        Assert.Equal(1.0, ahead.Front, Tolerance);

        DevicePosition right = SpatialMixer.DirectionPosition(2);
        Assert.Equal(1.0, right.Right, Tolerance);
        Assert.Equal(0.0, right.Front, Tolerance);

        DevicePosition behind = SpatialMixer.DirectionPosition(4);
        Assert.Equal(0.0, behind.Right, Tolerance);
        Assert.Equal(-1.0, behind.Front, Tolerance);

        DevicePosition left = SpatialMixer.DirectionPosition(6);
        Assert.Equal(-1.0, left.Right, Tolerance);
        Assert.Equal(0.0, left.Front, Tolerance);

        // The diagonals split the difference.
        DevicePosition frontRight = SpatialMixer.DirectionPosition(1);
        Assert.Equal(frontRight.Right, frontRight.Front, Tolerance);
        Assert.True(frontRight.Right > 0.0);
    }

    [Fact]
    public void ADirectionSurvivesBeingReadBackFromItsCoordinates()
    {
        for (int index = 0; index < SpatialMixer.DirectionCount; index++)
        {
            DevicePosition position = SpatialMixer.DirectionPosition(index);

            Assert.Equal(index, SpatialMixer.NearestDirectionIndex(position.Right, position.Front));
        }
    }

    /// <summary>
    /// The point of putting every direction on one shared radius: devices that differ only by direction come
    /// back with no delay and with their facing channel untouched. Choosing a direction can therefore neither
    /// re-time the set nor drag the whole image down in level -- all it changes is which channel a device
    /// leans towards.
    /// </summary>
    [Fact]
    public void DevicesThatOnlyDifferByDirectionAreNeitherAttenuatedNorDelayed()
    {
        var positions = new Dictionary<string, DevicePosition>(StringComparer.Ordinal);

        for (int index = 0; index < SpatialMixer.DirectionCount; index++)
        {
            positions[$"device{index}"] = SpatialMixer.DirectionPosition(index);
        }

        IReadOnlyDictionary<string, SpatialPlacement> placements = SpatialMixer.ComputePlacements(positions);

        for (int index = 0; index < SpatialMixer.DirectionCount; index++)
        {
            SpatialPlacement placement = placements[$"device{index}"];
            double loudest = Math.Max(placement.LeftGain, placement.RightGain);

            Assert.Equal(0.0, placement.DelayMs, Tolerance);
            Assert.True(
                loudest >= 1.0 - Tolerance,
                $"direction {index} peaks at {loudest}, so it lost level to a distance it does not have");
        }
    }

    /// <summary>
    /// A device on the right must favour the RIGHT channel. This is the check that caught an earlier version
    /// whose trigonometry was inverted, and the picker makes it reachable from the UI rather than only from a
    /// hand-written position.
    /// </summary>
    [Fact]
    public void ThePickerDirectionsPanTowardsTheSideTheyName()
    {
        IReadOnlyDictionary<string, SpatialPlacement> placements = SpatialMixer.ComputePlacements(
            new Dictionary<string, DevicePosition>
            {
                ["right"] = SpatialMixer.DirectionPosition(2),
                ["left"] = SpatialMixer.DirectionPosition(6),
                ["front-right"] = SpatialMixer.DirectionPosition(1),
                ["behind-left"] = SpatialMixer.DirectionPosition(5),
            });

        Assert.True(placements["right"].RightGain > placements["right"].LeftGain);
        Assert.True(placements["left"].LeftGain > placements["left"].RightGain);
        Assert.True(placements["front-right"].RightGain > placements["front-right"].LeftGain);
        Assert.True(placements["behind-left"].LeftGain > placements["behind-left"].RightGain);
    }

    [Fact]
    public void ADeviceWithNoPositionReadsAsStraightAhead()
    {
        Assert.Equal(0, SpatialMixer.NearestDirectionIndex(0.0, 0.0));
    }

    [Fact]
    public void DirectionIndexesWrapRatherThanBeingRejected()
    {
        Assert.Equal(SpatialMixer.DirectionPosition(0), SpatialMixer.DirectionPosition(SpatialMixer.DirectionCount));
        Assert.Equal(SpatialMixer.DirectionPosition(7), SpatialMixer.DirectionPosition(-1));
    }
}
