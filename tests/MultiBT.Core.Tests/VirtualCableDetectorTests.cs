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
            Id(name),
            @"SWD\MMDEVAPI\" + Id(name),
            name,
            transport.Length == 0 ? name : transport,
            Transport.Other,
            active);

    /// <summary>
    /// A stable, unique endpoint id per name.
    /// </summary>
    /// <remarks>
    /// Uniqueness is load-bearing, not cosmetic. These tests resolve a sink BY ID, so a helper that handed
    /// every endpoint the same id would let a test pass while the code picked an entirely different device —
    /// the assertions would be checking nothing.
    /// </remarks>
    private static string Id(string name)
    {
        byte[] hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name));
        return "{0.0.0.00000000}.{" + new Guid(hash).ToString().ToUpperInvariant() + "}";
    }

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
    public void SinkPickerDoesNotPrivilegeACable()
    {
        AudioEndpointInfo[] endpoints =
        [
            Endpoint("扬声器 (Realtek High Definition Audio)"),
            Endpoint("CABLE Input (VB-Audio Virtual Cable)"),
            Endpoint("VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)", active: false),
        ];

        IReadOnlyList<AudioEndpointInfo> ordered = VirtualCableDetector.OrderForSinkPicker(endpoints);

        Assert.Equal(3, ordered.Count);

        // Active first, then by name, and nothing more. A cable is one legitimate input among others, so
        // ranking it first would present it as the intended answer — the coupling this ordering rejects.
        Assert.All(ordered.Take(2), e => Assert.True(e.IsActive));
        Assert.False(ordered[2].IsActive);
        Assert.Equal(
            ordered.Take(2).Select(e => e.FriendlyName).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase),
            ordered.Take(2).Select(e => e.FriendlyName));
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

    [Theory]
    // A real device on this machine whose name contains "Virtual Audio Device". It is an HDMI audio
    // endpoint, and treating it as a sink would divert all system audio into a silent output.
    [InlineData("NVIDIA Virtual Audio Device (Wave Extensible) (WDM)")]
    [InlineData("Virtual Audio Device")]
    [InlineData("NVIDIA Virtual Audio Device")]
    public void DoesNotTreatVirtualAudioDeviceAsACable(string name)
    {
        Assert.False(VirtualCableDetector.IsVirtualCableRenderEndpoint(name));
        Assert.False(VirtualCableDetector.IsUsableCaptureSink(Endpoint(name)));
    }

}
