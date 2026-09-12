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

    [Fact]
    public void ResolveSourceSinkDetectsACableWithoutBeingAsked()
    {
        AudioEndpointInfo cable = Endpoint("CABLE Input (VB-Audio Virtual Cable)");

        // No explicit choice: the cable is found on its own. This is the behaviour that makes the input
        // something the user never has to configure.
        AudioEndpointInfo? resolved = VirtualCableDetector.ResolveSourceSink(
            [Endpoint("扬声器 (Realtek High Definition Audio)"), cable],
            configuredSinkId: null);

        Assert.NotNull(resolved);
        Assert.Equal(cable.EndpointId, resolved.EndpointId);
    }

    [Fact]
    public void ResolveSourceSinkPrefersAnExplicitChoice()
    {
        AudioEndpointInfo cable = Endpoint("CABLE Input (VB-Audio Virtual Cable)");
        AudioEndpointInfo voiceMeeter = Endpoint("VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)");

        AudioEndpointInfo? resolved = VirtualCableDetector.ResolveSourceSink(
            [cable, voiceMeeter],
            configuredSinkId: voiceMeeter.EndpointId);

        Assert.NotNull(resolved);
        Assert.Equal(voiceMeeter.EndpointId, resolved.EndpointId);
    }

    [Fact]
    public void ResolveSourceSinkRecoversWhenTheConfiguredCableIsGone()
    {
        AudioEndpointInfo replacement = Endpoint("CABLE Input (VB-Audio Virtual Cable)");

        // The configured cable was uninstalled or renamed by a reinstall. Falling back to detection keeps
        // the app working instead of leaving it pointed at a device that no longer exists.
        AudioEndpointInfo? resolved = VirtualCableDetector.ResolveSourceSink(
            [replacement],
            configuredSinkId: "{0.0.0.00000000}.{uninstalled-cable}");

        Assert.NotNull(resolved);
        Assert.Equal(replacement.EndpointId, resolved.EndpointId);
    }

    [Fact]
    public void ResolveSourceSinkIgnoresAConfiguredEndpointThatBecameARealSpeaker()
    {
        AudioEndpointInfo speaker = Endpoint("扬声器 (Realtek High Definition Audio)");
        AudioEndpointInfo cable = Endpoint("CABLE Input (VB-Audio Virtual Cable)");

        // Endpoint ids outlive the device that had them, and a stale id can now belong to a real speaker.
        // Honouring it would capture a speaker while it also plays natively - heard twice, once delayed.
        AudioEndpointInfo? resolved = VirtualCableDetector.ResolveSourceSink(
            [speaker, cable],
            configuredSinkId: speaker.EndpointId);

        Assert.NotNull(resolved);
        Assert.Equal(cable.EndpointId, resolved.EndpointId);
    }

    [Fact]
    public void ResolveSourceSinkReturnsNullWhenThereIsNoCable()
    {
        // Null is not an error: it means the mirror falls back to capturing a real endpoint, and the UI
        // reports that not every device can be controlled.
        Assert.Null(VirtualCableDetector.ResolveSourceSink(
            [Endpoint("扬声器 (Realtek High Definition Audio)"), Endpoint("耳机 (MI Portable Speaker)")],
            configuredSinkId: null));
    }

    [Fact]
    public void ResolveSourceSinkIgnoresAnInactiveCable()
    {
        // Present but disabled: capturing it would yield silence, so it must not be auto-selected.
        Assert.Null(VirtualCableDetector.ResolveSourceSink(
            [Endpoint("CABLE Input (VB-Audio Virtual Cable)", active: false)],
            configuredSinkId: null));
    }

    [Fact]
    public void ResolveSourceSinkFollowsTheCableWindowsIsRenderingInto()
    {
        // Two cables installed. Only one of them is receiving anything, and that is the one Windows is
        // rendering into — anything else would be captured empty while every device reported as running.
        AudioEndpointInfo first = Endpoint("Line 1 (Virtual Audio Cable)");
        AudioEndpointInfo second = Endpoint("Line 2 (Virtual Audio Cable)");

        AudioEndpointInfo? resolved = VirtualCableDetector.ResolveSourceSink(
            [first, second],
            configuredSinkId: null,
            currentDefaultSinkId: second.EndpointId);

        Assert.NotNull(resolved);
        Assert.Equal(second.EndpointId, resolved.EndpointId);
    }

    [Fact]
    public void ResolveSourceSinkIsStableWhenNoCableIsTheDefault()
    {
        // Windows is rendering to a real speaker, so no cable carries the audio yet. The choice must still
        // be deterministic and ordered the way the names read, not dependent on enumeration order.
        AudioEndpointInfo second = Endpoint("Line 2 (Virtual Audio Cable)");
        AudioEndpointInfo first = Endpoint("Line 1 (Virtual Audio Cable)");

        AudioEndpointInfo? resolved = VirtualCableDetector.ResolveSourceSink(
            [second, first],
            configuredSinkId: null,
            currentDefaultSinkId: Endpoint("扬声器 (Realtek High Definition Audio)").EndpointId);

        Assert.NotNull(resolved);
        Assert.Equal(first.EndpointId, resolved.EndpointId);
    }

    [Fact]
    public void ResolveSourceSinkPrefersAnExplicitChoiceOverTheDefaultCable()
    {
        // The user pointed the mirror at a specific cable; the fact that another one happens to be the
        // Windows default must not override that.
        AudioEndpointInfo chosen = Endpoint("Line 1 (Virtual Audio Cable)");
        AudioEndpointInfo windowsDefault = Endpoint("Line 2 (Virtual Audio Cable)");

        AudioEndpointInfo? resolved = VirtualCableDetector.ResolveSourceSink(
            [chosen, windowsDefault],
            configuredSinkId: chosen.EndpointId,
            currentDefaultSinkId: windowsDefault.EndpointId);

        Assert.NotNull(resolved);
        Assert.Equal(chosen.EndpointId, resolved.EndpointId);
    }

    [Fact]
    public void ResolveSourceSinkSkipsADefaultCableThatIsNotUsable()
    {
        // A disabled cable is the Windows default. It would capture silence, so fall through to the one
        // that actually works rather than honouring the default blindly.
        AudioEndpointInfo disabledDefault = Endpoint("Line 1 (Virtual Audio Cable)", active: false);
        AudioEndpointInfo usable = Endpoint("Line 2 (Virtual Audio Cable)");

        AudioEndpointInfo? resolved = VirtualCableDetector.ResolveSourceSink(
            [disabledDefault, usable],
            configuredSinkId: null,
            currentDefaultSinkId: disabledDefault.EndpointId);

        Assert.NotNull(resolved);
        Assert.Equal(usable.EndpointId, resolved.EndpointId);
    }
}
