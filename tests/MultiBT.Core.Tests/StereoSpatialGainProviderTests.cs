using MultiBT.Core.Audio;
using NAudio.Wave;
using Xunit;

namespace MultiBT.Core.Tests;

/// <summary>
/// The per-channel gain stage that device positioning needs.
/// </summary>
/// <remarks>
/// A source that produces a known, distinct value per channel, so a mistake in the interleaving shows up as
/// the wrong channel being scaled rather than as something subtler.
/// </remarks>
internal sealed class ChannelTaggedSource : ISampleProvider
{
    public ChannelTaggedSource(int channels = 2, int sampleRate = 48000)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            // Left frames read 0.4, right frames read 0.8: swapped scaling is immediately visible.
            buffer[i] = i % 2 == 0 ? 0.4f : 0.8f;
        }

        return buffer.Length;
    }
}

public sealed class StereoSpatialGainProviderTests
{
    private const double Tolerance = 0.0001;

    private static float[] ReadFrames(ISampleProvider provider, int frames)
    {
        var buffer = new float[frames * provider.WaveFormat.Channels];
        int read = provider.Read(buffer);

        Assert.Equal(buffer.Length, read);
        return buffer;
    }

    [Fact]
    public void PassesTheSignalThroughUnchangedByDefault()
    {
        // Unity by default: a device with no position must be untouched, and this stage sits in every chain.
        var provider = new StereoSpatialGainProvider(new ChannelTaggedSource());

        float[] output = ReadFrames(provider, 4);

        Assert.Equal(0.4, output[0], Tolerance);
        Assert.Equal(0.8, output[1], Tolerance);
    }

    [Fact]
    public void ScalesEachChannelByItsOwnGain()
    {
        var provider = new StereoSpatialGainProvider(new ChannelTaggedSource());
        provider.SetGains(1.0, 0.5);

        float[] output = ReadFrames(provider, 4);

        // Left keeps its level, right is halved -- and the channels are not swapped.
        Assert.Equal(0.4, output[0], Tolerance);
        Assert.Equal(0.4, output[2], Tolerance);
        Assert.Equal(0.4, output[1], Tolerance);
        Assert.Equal(0.4, output[3], Tolerance);
    }

    [Fact]
    public void CanCloseOneChannelEntirely()
    {
        // Hard right: the left channel is closed, which is what a speaker directly to the right needs.
        var provider = new StereoSpatialGainProvider(new ChannelTaggedSource());
        provider.SetGains(0.0, 1.0);

        float[] output = ReadFrames(provider, 4);

        Assert.Equal(0.0, output[0], Tolerance);
        Assert.Equal(0.8, output[1], Tolerance);
        Assert.Equal(0.0, output[2], Tolerance);
        Assert.Equal(0.8, output[3], Tolerance);
    }

    [Fact]
    public void GainsAboveUnityAreClamped()
    {
        // These gains come from a position calculation; above unity would amplify a full-scale signal into
        // clipping, so the stage refuses rather than trusting the caller.
        var provider = new StereoSpatialGainProvider(new ChannelTaggedSource());
        provider.SetGains(4.0, 4.0);

        Assert.Equal(1.0, provider.LeftGain, Tolerance);
        Assert.Equal(1.0, provider.RightGain, Tolerance);
    }

    [Fact]
    public void NegativeGainsAreClampedToSilence()
    {
        var provider = new StereoSpatialGainProvider(new ChannelTaggedSource());
        provider.SetGains(-1.0, 1.0);

        float[] output = ReadFrames(provider, 2);

        Assert.Equal(0.0, output[0], Tolerance);
        Assert.Equal(0.8, output[1], Tolerance);
    }

    [Fact]
    public void AChangeTakesEffectOnTheNextBuffer()
    {
        var source = new ChannelTaggedSource();
        var provider = new StereoSpatialGainProvider(source);

        _ = ReadFrames(provider, 2);
        provider.SetGains(0.5, 0.5);
        float[] output = ReadFrames(provider, 2);

        Assert.Equal(0.2, output[0], Tolerance);
        Assert.Equal(0.4, output[1], Tolerance);
    }

    [Fact]
    public void AMonoSourceIsRejectedRatherThanGuessedAt()
    {
        // Panning mono would mean inventing a second channel. The engine has an explicit channel-matching
        // stage for that, and this stage must not quietly take over the decision.
        var mono = new ChannelTaggedSource(channels: 1);

        ArgumentException error = Assert.Throws<ArgumentException>(() => new StereoSpatialGainProvider(mono));
        Assert.Contains("stereo", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheFormatIsPassedThroughUntouched()
    {
        // The chain is built at the device's own mix format; this stage must not alter it.
        var source = new ChannelTaggedSource(sampleRate: 44100);
        var provider = new StereoSpatialGainProvider(source);

        Assert.Equal(44100, provider.WaveFormat.SampleRate);
        Assert.Equal(2, provider.WaveFormat.Channels);
    }
}
