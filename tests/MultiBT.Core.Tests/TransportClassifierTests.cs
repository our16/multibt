using MultiBT.Core.Audio;
using MultiBT.Core.Devices;
using Xunit;

namespace MultiBT.Core.Tests;

public sealed class TransportClassifierTests
{
    // A real-shaped Bluetooth endpoint instance id, as it appears in setupapi logs.
    private const string BluetoothInstanceId =
        @"BTHENUM\{0000111e-0000-1000-8000-00805f9b34fb}_LOCALMFG&0002\7&267977e0&3&5CC6E98362C4_C00000000";

    [Fact]
    public void ClassifiesBluetoothEnumerator() =>
        Assert.Equal(Transport.Bluetooth, TransportClassifier.Classify(BluetoothInstanceId));

    [Fact]
    public void ClassifiesBluetoothHandsFreeEnumerator() =>
        Assert.Equal(
            Transport.Bluetooth,
            TransportClassifier.Classify(@"BTHHFENUM\BthHFENUM\7&1f2e3d4c&0&5CC6E98362C4_C00000000"));

    [Fact]
    public void ClassificationIsCaseInsensitive()
    {
        // Case varies in practice: one real log line has a lowercase GUID and an uppercase MAC.
        Assert.Equal(Transport.Bluetooth, TransportClassifier.Classify(@"bthenum\{0000110b-0000-1000-8000-00805f9b34fb}\x"));
        Assert.Equal(Transport.Usb, TransportClassifier.Classify(@"usb\VID_1234&PID_5678"));
    }

    [Fact]
    public void ClassifiesUsb() =>
        Assert.Equal(Transport.Usb, TransportClassifier.Classify(@"USB\VID_046D&PID_0A38&MI_00\6&1b2c3d4e&0&0000"));

    [Fact]
    public void ClassifiesHdmiFromTheEndpointName()
    {
        // HDMI audio normally presents as an HDAUDIO function of the GPU's audio controller, so
        // the instance id alone is not decisive — the name is the reliable signal.
        Assert.Equal(
            Transport.Hdmi,
            TransportClassifier.Classify(@"HDAUDIO\FUNC_01&VEN_10DE&DEV_0099", "NVIDIA High Definition Audio (HDMI)"));
    }

    [Fact]
    public void IdBasedRulesWinOverNameBasedOnes()
    {
        // A Bluetooth device whose name happens to mention a display must not be reclassified.
        Assert.Equal(
            Transport.Bluetooth,
            TransportClassifier.Classify(BluetoothInstanceId, "JBL Bar HDMI"));
    }

    [Fact]
    public void UnclassifiableInputFallsBackToOtherWithoutThrowing()
    {
        // Falling back safely matters more than guessing: Other widens the plausibility window
        // rather than rejecting good measurements.
        Assert.Equal(Transport.Other, TransportClassifier.Classify(null));
        Assert.Equal(Transport.Other, TransportClassifier.Classify(string.Empty));
        Assert.Equal(Transport.Other, TransportClassifier.Classify("   "));
        Assert.Equal(Transport.Other, TransportClassifier.Classify(@"SWD\MMDEVAPI\{0.0.0.00000000}.{abc}"));
    }

    [Fact]
    public void UnknownTransportGetsTheWidestPlausibilityWindow()
    {
        (double min, double max) = TransportClassifier.PlausibleDelayRange(Transport.Other);

        Assert.Equal(0.0, min);
        Assert.Equal(EngineTunables.MaxDelayMs, max);
    }

    [Theory]
    [InlineData(Transport.Bluetooth, 185.0, true)]
    [InlineData(Transport.Bluetooth, 79.0, false)]
    [InlineData(Transport.Bluetooth, 451.0, false)]
    [InlineData(Transport.Usb, 12.0, true)]
    [InlineData(Transport.Usb, 200.0, false)]
    [InlineData(Transport.Hdmi, 100.0, true)]
    [InlineData(Transport.Hdmi, 130.0, false)]
    public void PlausibilityWindowsRejectImpossibleMeasurements(Transport transport, double measuredMs, bool expected)
    {
        // A ~0 ms reading on a device that should be late almost always means something else
        // was audible, so the window is a real gate rather than cosmetic validation.
        Assert.Equal(expected, TransportClassifier.IsPlausibleDelay(transport, measuredMs));
    }

    [Fact]
    public void OnlyBluetoothInvalidatesMeasurementOnReconnect()
    {
        // A Bluetooth re-pair can silently renegotiate the codec, which moves latency by tens
        // of milliseconds with no user-visible signal.
        Assert.True(TransportClassifier.RequiresRemeasureOnReconnect(Transport.Bluetooth));
        Assert.False(TransportClassifier.RequiresRemeasureOnReconnect(Transport.Usb));
        Assert.False(TransportClassifier.RequiresRemeasureOnReconnect(Transport.Hdmi));
        Assert.False(TransportClassifier.RequiresRemeasureOnReconnect(Transport.Other));
    }
}
