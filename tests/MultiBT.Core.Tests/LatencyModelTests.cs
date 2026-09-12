using MultiBT.Core.Audio;
using MultiBT.Core.Sync;
using Xunit;

namespace MultiBT.Core.Tests;

public sealed class LatencyModelTests
{
    [Fact]
    public void EffectiveDelaySumsCompensationAndSignedOffset()
    {
        var settings = new DeviceLatencySettings { CompensationMs = 125.0, ManualOffsetMs = -15.0 };

        // 125 - 15 = 110, matching the worked example in docs/SPEC.md §5.1.
        Assert.Equal(110.0, LatencyModel.ComputeEffectiveDelayMs(settings));
    }

    [Fact]
    public void NegativeOffsetCannotDriveTotalBelowZero()
    {
        // With no compensation there is nowhere to move, which is exactly why the UI must
        // show the RESOLVED value rather than the raw trim.
        var settings = new DeviceLatencySettings { CompensationMs = 0.0, ManualOffsetMs = -50.0 };

        Assert.Equal(0.0, LatencyModel.ComputeEffectiveDelayMs(settings));
    }

    [Fact]
    public void EffectiveDelayIsClampedToMaximum()
    {
        var settings = new DeviceLatencySettings { CompensationMs = 5000.0, ManualOffsetMs = 500.0 };

        Assert.Equal(EngineTunables.MaxDelayMs, LatencyModel.ComputeEffectiveDelayMs(settings));
    }

    [Theory]
    [InlineData(0.0, 48000, 0)]
    [InlineData(110.0, 48000, 5280)]
    [InlineData(125.0, 48000, 6000)]
    [InlineData(10.0, 44100, 441)]
    public void EffectiveDelayConvertsToWholeSamples(double delayMs, int sampleRate, int expectedSamples)
    {
        var settings = new DeviceLatencySettings { CompensationMs = delayMs };

        Assert.Equal(expectedSamples, LatencyModel.ComputeEffectiveDelaySamples(settings, sampleRate));
    }

    [Fact]
    public void AlignAllDelaysEverythingToTheSlowestDevice()
    {
        CompensationTarget[] targets =
        [
            new("jbl", 185.0, Transport.Bluetooth),
            new("sony", 240.0, Transport.Bluetooth),
            new("projector", 310.0, Transport.Hdmi),
        ];

        IReadOnlyDictionary<string, double> compensations =
            LatencyModel.ComputeCompensations(targets, SyncMode.AlignAll);

        // Target = max(measured) = 310 ms.
        Assert.Equal(125.0, compensations["jbl"]);
        Assert.Equal(70.0, compensations["sony"]);
        Assert.Equal(0.0, compensations["projector"]);
    }

    [Fact]
    public void WiredOnlyLeavesBluetoothAtItsNaturalLatency()
    {
        CompensationTarget[] targets =
        [
            new("jbl", 185.0, Transport.Bluetooth),
            new("usb-dac", 40.0, Transport.Usb),
            new("projector", 90.0, Transport.Hdmi),
        ];

        IReadOnlyDictionary<string, double> compensations =
            LatencyModel.ComputeCompensations(targets, SyncMode.WiredOnly);

        // Bluetooth is NOT dragged into the alignment: forcing it to 0 compensation keeps the
        // system latency in the wired group's range instead of the Bluetooth range.
        Assert.Equal(0.0, compensations["jbl"]);
        Assert.Equal(50.0, compensations["usb-dac"]);
        Assert.Equal(0.0, compensations["projector"]);
    }

    [Fact]
    public void SystemLatencyIsTheSlowestAlignedDevice()
    {
        CompensationTarget[] targets =
        [
            new("jbl", 185.0, Transport.Bluetooth),
            new("sony", 240.0, Transport.Bluetooth),
            new("projector", 310.0, Transport.Hdmi),
        ];

        IReadOnlyDictionary<string, double> compensations =
            LatencyModel.ComputeCompensations(targets, SyncMode.AlignAll);

        Assert.Equal(
            310.0,
            LatencyModel.ComputeSystemLatencyMs(targets, compensations, SyncMode.AlignAll));
    }

    [Fact]
    public void WiredOnlyKeepsSystemLatencyOutOfTheLipSyncFailureRange()
    {
        CompensationTarget[] targets =
        [
            new("jbl", 185.0, Transport.Bluetooth),
            new("usb-dac", 40.0, Transport.Usb),
            new("projector", 90.0, Transport.Hdmi),
        ];

        IReadOnlyDictionary<string, double> compensations =
            LatencyModel.ComputeCompensations(targets, SyncMode.WiredOnly);

        double systemLatency = LatencyModel.ComputeSystemLatencyMs(targets, compensations, SyncMode.WiredOnly);

        // This is the whole reason SyncMode exists: aligning everything to a Bluetooth device
        // would push the system past the ITU-R BT.1359-1 threshold and break lip-sync.
        Assert.Equal(90.0, systemLatency);
        Assert.False(LatencyModel.ExceedsLipSyncThreshold(systemLatency));

        double alignAllLatency = LatencyModel.ComputeSystemLatencyMs(
            targets,
            LatencyModel.ComputeCompensations(targets, SyncMode.AlignAll),
            SyncMode.AlignAll);

        Assert.True(LatencyModel.ExceedsLipSyncThreshold(alignAllLatency));
    }

    [Fact]
    public void EmptyTargetSetProducesNoCompensations()
    {
        IReadOnlyDictionary<string, double> compensations =
            LatencyModel.ComputeCompensations([], SyncMode.AlignAll);

        Assert.Empty(compensations);
    }

    [Fact]
    public void BluetoothOnlySetUnderWiredOnlyAlignsNothing()
    {
        // Degenerate case: with no wired device there is nothing to align against, so
        // everything keeps its natural latency rather than being forced to an arbitrary value.
        CompensationTarget[] targets =
        [
            new("jbl", 185.0, Transport.Bluetooth),
            new("sony", 240.0, Transport.Bluetooth),
        ];

        IReadOnlyDictionary<string, double> compensations =
            LatencyModel.ComputeCompensations(targets, SyncMode.WiredOnly);

        Assert.Equal(0.0, compensations["jbl"]);
        Assert.Equal(0.0, compensations["sony"]);
    }
}
