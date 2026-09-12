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

    // ---------------------------------------------------------------- the estimated path
    //
    // Nothing measures latency on this machine — acoustic measurement is not implemented end to end — so in
    // practice every device arrives with HasMeasurement == false. Returning zero compensation for all of
    // them, which is what treating "unmeasured" as "excluded" amounts to, made AlignAll a mode that did
    // nothing at all while the UI presented it as aligning delays. These tests pin the replacement: a
    // stated per-transport estimate, labelled as an estimate.

    [Fact]
    public void UnmeasuredDevicesAlignOnATransportEstimate()
    {
        CompensationTarget[] targets =
        [
            new("jbl", 0.0, Transport.Bluetooth, HasMeasurement: false),
            new("usb-dac", 0.0, Transport.Usb, HasMeasurement: false),
        ];

        IReadOnlyDictionary<string, LatencyModel.DeviceCompensation> plan =
            LatencyModel.ComputeCompensationPlan(targets, SyncMode.AlignAll);

        // Bluetooth is assumed slow (200 ms), a wired endpoint fast (10 ms), so the wired one is delayed by
        // the difference. Real numbers, and the basis says where they came from.
        Assert.Equal(0.0, plan["jbl"].CompensationMs);
        Assert.Equal(190.0, plan["usb-dac"].CompensationMs);
        Assert.Equal(LatencyModel.CompensationBasis.Estimated, plan["jbl"].Basis);
        Assert.Equal(200.0, plan["jbl"].AssumedLatencyMs);
        Assert.Equal(10.0, plan["usb-dac"].AssumedLatencyMs);
    }

    [Fact]
    public void AMeasurementBeatsTheEstimateForTheSameDevice()
    {
        CompensationTarget[] targets =
        [
            new("jbl", 120.0, Transport.Bluetooth),
            new("usb-dac", 0.0, Transport.Usb, HasMeasurement: false),
        ];

        IReadOnlyDictionary<string, LatencyModel.DeviceCompensation> plan =
            LatencyModel.ComputeCompensationPlan(targets, SyncMode.AlignAll);

        // The measured 120 wins over the 200 estimate, and it is the measured value that anchors the set,
        // so the wired device is delayed by 120 - 10 rather than 200 - 10.
        Assert.Equal(LatencyModel.CompensationBasis.Measured, plan["jbl"].Basis);
        Assert.Equal(120.0, plan["jbl"].AssumedLatencyMs);
        Assert.Equal(110.0, plan["usb-dac"].CompensationMs);
    }

    [Fact]
    public void WiredOnlyStillLeavesBluetoothAloneWithoutMeasurements()
    {
        CompensationTarget[] targets =
        [
            new("jbl", 0.0, Transport.Bluetooth, HasMeasurement: false),
            new("usb-dac", 0.0, Transport.Usb, HasMeasurement: false),
        ];

        IReadOnlyDictionary<string, LatencyModel.DeviceCompensation> plan =
            LatencyModel.ComputeCompensationPlan(targets, SyncMode.WiredOnly);

        Assert.Equal(0.0, plan["jbl"].CompensationMs);
        Assert.Equal(LatencyModel.CompensationBasis.None, plan["jbl"].Basis);

        // Only one wired device is present, so nothing in the aligned group can be slow relative to it.
        Assert.Equal(0.0, plan["usb-dac"].CompensationMs);
    }

    [Fact]
    public void SystemLatencyUsesTheEstimateWhenNothingIsMeasured()
    {
        CompensationTarget[] targets =
        [
            new("jbl", 0.0, Transport.Bluetooth, HasMeasurement: false),
            new("usb-dac", 0.0, Transport.Usb, HasMeasurement: false),
        ];

        IReadOnlyDictionary<string, double> compensations =
            LatencyModel.ComputeCompensations(targets, SyncMode.AlignAll);

        // Aligning everything to a Bluetooth speaker really does put the system at ~200 ms, which breaks
        // lip-sync — and saying so is the entire purpose of this figure. Reporting 0 because nothing was
        // formally measured would hide the very problem it exists to warn about.
        double systemLatency = LatencyModel.ComputeSystemLatencyMs(targets, compensations, SyncMode.AlignAll);

        Assert.Equal(200.0, systemLatency);
        Assert.True(LatencyModel.ExceedsLipSyncThreshold(systemLatency));
    }
}
