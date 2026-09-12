using MultiBT.Core.Audio;
using MultiBT.Core.Devices;
using Xunit;

namespace MultiBT.Core.Tests;

/// <summary>
/// Virtual-cable detection, which decides where Windows sends system audio.
/// </summary>
/// <remarks>
/// A false POSITIVE here is the dangerous direction: it would divert system audio into a device the user
/// cannot hear, producing total silence that looks like broken audio code. So the tests below check the
/// negative cases as carefully as the positive ones.
/// </remarks>
public sealed class VirtualCableDetectorTests
{
    private static AudioEndpointInfo Endpoint(string name, bool active = true, string transport = "") =>
        new(
            "{0.0.0.00000000}.{id}",
            @"SWD\MMDEVAPI\{0.0.0.00000000}.{id}",
            name,
            transport.Length == 0 ? name : transport,
            Transport.Other,
            active);

    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)")]
    [InlineData("CABLE Input")]
    [InlineData("VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)")]
    [InlineData("VoiceMeeter Aux Input (VB-Audio VoiceMeeter AUX VAIO)")]
    [InlineData("VoiceMeeter VAIO3 Input (VB-Audio VoiceMeeter VAIO3)")]
    [InlineData("Line 1 (Virtual Audio Cable)")]
    [InlineData("cable input (vb-audio virtual cable)")]   // case-insensitive
    public void RecognisesKnownVirtualCables(string name) =>
        Assert.True(VirtualCableDetector.IsVirtualCableRenderEndpoint(name));

    [Theory]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)")]   // the RECORDING side, not a sink
    [InlineData("扬声器 (Realtek High Definition Audio)")]
    [InlineData("扬声器 (智能音箱 Pro-3420)")]
    [InlineData("耳机 (MI Portable Speaker)")]
    [InlineData("数字音频(HDMI) (High Definition Audio Device)")]
    [InlineData("NVIDIA Output (NVIDIA High Definition Audio)")]
    [InlineData("Realtek Digital Output (Realtek High Definition Audio)")]
    [InlineData("")]
    [InlineData(null)]
    public void DoesNotMistakeRealDevicesForCables(string? name)
    {
        // A wrong positive would send all system audio somewhere inaudible.
        Assert.False(VirtualCableDetector.IsVirtualCableRenderEndpoint(name));
    }

    [Fact]
    public void CaptureSideIsNotUsableAsASink()
    {
        Assert.True(VirtualCableDetector.IsVirtualCableCaptureEndpoint("CABLE Output (VB-Audio Virtual Cable)"));
        Assert.False(VirtualCableDetector.IsVirtualCableRenderEndpoint("CABLE Output (VB-Audio Virtual Cable)"));
        Assert.False(VirtualCableDetector.IsUsableCaptureSink(Endpoint("CABLE Output (VB-Audio Virtual Cable)")));
    }

    [Fact]
    public void OnlyActiveCablesAreUsableAsASink()
    {
        Assert.True(VirtualCableDetector.IsUsableCaptureSink(Endpoint("CABLE Input (VB-Audio Virtual Cable)")));

        // Disabled or unplugged would capture nothing, and the failure would look like a broken mirror
        // rather than a sink that does not exist.
        Assert.False(VirtualCableDetector.IsUsableCaptureSink(Endpoint("CABLE Input (VB-Audio Virtual Cable)", active: false)));
    }

    [Fact]
    public void DeviceDescriptionIsAlsoConsulted()
    {
        Assert.True(VirtualCableDetector.IsVirtualCableRenderEndpoint("CABLE Input", "VB-Audio Virtual Cable"));
    }

    [Fact]
    public void SinkPickerPutsUsableCablesFirst()
    {
        AudioEndpointInfo[] endpoints =
        [
            Endpoint("扬声器 (Realtek High Definition Audio)"),
            Endpoint("耳机 (MI Portable Speaker)"),
            Endpoint("CABLE Input (VB-Audio Virtual Cable)"),
            Endpoint("VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)", active: false),
            Endpoint("数字音频(HDMI) (High Definition Audio Device)"),
        ];

        IReadOnlyList<AudioEndpointInfo> ordered = VirtualCableDetector.OrderForSinkPicker(endpoints);

        Assert.Equal(5, ordered.Count);

        // The rule, strongest first: a USABLE cable, then active endpoints, then the rest by name. The
        // inactive VoiceMeeter is recognised but not currently usable, so it does not outrank an active
        // real device — offering an unusable sink ahead of a working one would be misleading.
        Assert.StartsWith("CABLE Input", ordered[0].FriendlyName);
        Assert.True(ordered[1].IsActive);
        Assert.Contains("VoiceMeeter", ordered[4].FriendlyName);
    }

    [Fact]
    public void FindsTheBestSinkOrReportsNoneInstalled()
    {
        AudioEndpointInfo cable = Endpoint("CABLE Input (VB-Audio Virtual Cable)");

        Assert.NotNull(VirtualCableDetector.FindBestCaptureSink(
            [Endpoint("扬声器 (Realtek High Definition Audio)"), cable]));

        // No cable installed: must report "none" rather than fall back to a real speaker, which would
        // silently send system audio to a device the user hears twice.
        Assert.Null(VirtualCableDetector.FindBestCaptureSink(
            [Endpoint("扬声器 (Realtek High Definition Audio)"), Endpoint("耳机 (MI Portable Speaker)")]));
    }
}
