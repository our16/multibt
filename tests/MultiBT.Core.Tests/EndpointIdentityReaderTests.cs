using MultiBT.Core.Audio;
using MultiBT.Core.Devices;

namespace MultiBT.Core.Tests;

/// <summary>
/// Tests built from values observed on a real machine (Windows 11 24H2, build 26100) by
/// tools/SmokeCheck. These are not invented strings — they are copied out of the raw property
/// store dump, because the whole point is that the documented approach
/// (<c>PKEY_Device_InstanceId</c>) does not work and the real one has two quirks.
/// </summary>
public sealed class EndpointIdentityReaderTests
{
    [Fact]
    public void StripsOrdinalPrefixAndNormalisesSeparators()
    {
        // Observed verbatim in the property store under {b3f8fa53-...} pid=2.
        const string raw =
            @"{1}.HDAUDIO\FUNC_01&VEN_10EC&DEV_0897&SUBSYS_10EC129E&REV_1004\4&29ACFEC2&0&0001";

        Assert.Equal(
            @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897&SUBSYS_10EC129E&REV_1004\4&29ACFEC2&0&0001",
            EndpointIdentityReader.Normalize(raw));
    }

    [Fact]
    public void ConvertsDevicePathSeparatorsAndStripsExtendedPrefix()
    {
        // Observed verbatim under {b3f8fa53-...} pid=11. Note the '#', the '\\?\' prefix and the
        // lowercase enumerator — all three would defeat naive prefix matching.
        const string raw =
            @"{2}.\\?\hdaudio#func_01&ven_10ec&dev_0897&subsys_10ec129e&rev_1004#4&29acfec2&0&0001#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\rearlineoutwave3";

        string normalized = EndpointIdentityReader.Normalize(raw);

        Assert.StartsWith(@"hdaudio\func_01&ven_10ec", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain('#', normalized);
        Assert.DoesNotContain(@"\\?\", normalized);
    }

    [Fact]
    public void NormalisedBluetoothInterfacePathBecomesDetectable()
    {
        // A Bluetooth endpoint's interface path would carry the same shape as the HDAUDIO one
        // above. Before normalisation the '#' separators made this undetectable.
        const string raw =
            @"{2}.\\?\bthenum#{{0000110b-0000-1000-8000-00805f9b34fb}}_localmfg&0002#7&267977e0&3&5cc6e98362c4_c00000000";

        string normalized = EndpointIdentityReader.Normalize(raw);

        Assert.Equal(
            Transport.Bluetooth,
            TransportClassifier.Classify(normalized));
    }

    [Fact]
    public void MmDevApiWrapperIsNotTreatedAsHardware()
    {
        // Every endpoint carries one of these; it describes the MMDEVAPI software device and says
        // nothing about the transport, so it must never win over a hardware path.
        const string raw =
            @"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{26c6a230-1f7b-46fe-b1a2-3ab8271e5bde}#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

        string normalized = EndpointIdentityReader.Normalize(raw);

        Assert.False(EndpointIdentityReader.IsHardwarePath(normalized));
        Assert.StartsWith(@"SWD\MMDEVAPI\", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void HardwarePathBeatsTheMmDevApiWrapper()
    {
        Assert.True(EndpointIdentityReader.IsHardwarePath(@"HDAUDIO\FUNC_01&VEN_10EC"));
        Assert.True(EndpointIdentityReader.IsHardwarePath(@"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}\x"));
        Assert.False(EndpointIdentityReader.IsHardwarePath(string.Empty));
        Assert.False(EndpointIdentityReader.IsHardwarePath(null));
    }

    [Fact]
    public void EmptyInputNormalisesToEmpty()
    {
        // PKEY_Device_InstanceId really is absent on the test machine, so empty must be a normal,
        // expected input rather than an error.
        Assert.Equal(string.Empty, EndpointIdentityReader.Normalize(null));
        Assert.Equal(string.Empty, EndpointIdentityReader.Normalize(string.Empty));
        Assert.Equal(string.Empty, EndpointIdentityReader.Normalize("   "));
    }
}
