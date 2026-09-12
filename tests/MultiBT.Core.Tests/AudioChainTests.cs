using MultiBT.Core.Audio;
using NAudio.Wave;
using Xunit;

namespace MultiBT.Core.Tests;

/// <summary>Test doubles for the sample-provider chain.</summary>
internal sealed class RampSource : ISampleProvider
{
    private int _next;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);

    public int Read(Span<float> buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = _next++ % 10000;
        }

        return buffer.Length;
    }
}

internal sealed class ConstantSource : ISampleProvider
{
    private readonly float _value;

    public ConstantSource(float value, int channels = 1, int sampleRate = 48000)
    {
        _value = value;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        buffer.Fill(_value);
        return buffer.Length;
    }
}

/// <summary>
/// A source that returns fewer frames than requested — the under-feeding pattern that triggers
/// NAudio 3.1.0's <c>WdlResamplingSampleProvider</c> defect (#1412).
/// </summary>
internal sealed class StingySource : ISampleProvider
{
    private readonly float _value;
    private readonly int _maxFrames;

    public StingySource(float value, int maxFrames)
    {
        _value = value;
        _maxFrames = maxFrames;
    }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);

    public int Read(Span<float> buffer)
    {
        int frames = Math.Min(buffer.Length, _maxFrames);
        buffer.Slice(0, frames).Fill(_value);
        return frames;
    }
}

public sealed class DelaySampleProviderTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void ZeroDelayIsPassThrough()
    {
        var provider = new DelaySampleProvider(new RampSource());
        var buffer = new float[100];

        int read = provider.Read(buffer);

        Assert.Equal(100, read);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(i, buffer[i]);
        }
    }

    [Fact]
    public void LargeChangeAppliesImmediatelyAndAsksForResync()
    {
        var provider = new DelaySampleProvider(new RampSource());

        // 30 ms = 1440 frames, above the 20 ms glide threshold.
        Assert.True(provider.SetDelayMs(30.0, out bool requiresMuteResync));

        // A large step must NOT be slewed: at the 0.5 % pitch budget, 170 ms would take ~34 s
        // of audibly sliding audio, so the caller is told to mute and re-apply instead.
        Assert.True(requiresMuteResync);
        Assert.Equal(1440, provider.AppliedDelayFrames);
    }

    [Fact]
    public void SmallChangeGlidesInsteadOfJumping()
    {
        var provider = new DelaySampleProvider(new RampSource());

        // 5 ms = 240 frames, below the threshold.
        Assert.True(provider.SetDelayMs(5.0, out bool requiresMuteResync));
        Assert.False(requiresMuteResync);

        Assert.Equal(0, provider.AppliedDelayFrames);

        var buffer = new float[480];
        provider.Read(buffer);

        // 480 frames at a 0.5 % budget allows a 2-frame step, so after one block the applied
        // delay must still be nowhere near the 240-frame target.
        Assert.InRange(provider.AppliedDelayFrames, 1, 3);
    }

    [Fact]
    public void DelayEmitsSilenceThenShiftedSignal()
    {
        var provider = new DelaySampleProvider(new RampSource());
        provider.SetDelayMs(30.0, out _);   // 1440 frames, applied immediately

        const int total = 2400;
        var buffer = new float[total];
        int read = provider.Read(buffer);

        Assert.Equal(total, read);

        // Zero-initialised ring: everything before the delay has elapsed reads as silence,
        // which is what gives pre-fill for free with no priming state machine.
        for (int f = 0; f < 1440; f++)
        {
            Assert.Equal(0f, buffer[f]);
        }

        // After that, output frame f is input frame (f - 1440).
        Assert.Equal(0f, buffer[1440]);
        Assert.Equal(1f, buffer[1441]);
        Assert.Equal(560f, buffer[2000]);
    }

    [Fact]
    public void MaxDelayReportsTheUsableRangeNotTheRawRingCapacity()
    {
        var provider = new DelaySampleProvider(new RampSource(), maxDelayMs: 100);

        // At 48 kHz, 100 ms is exactly 4800 frames. The internal collision slack must NOT be
        // advertised as usable delay, or the reported maximum would disagree with the clamp
        // that SetDelayFrames actually applies.
        Assert.InRange(provider.MaxDelayMs, 100.0, 100.1);
    }
}

public sealed class AdaptiveResamplerTests
{
    [Fact]
    public void OutputFormatMatchesRequestedRate()
    {
        var resampler = new AdaptiveResampler(new ConstantSource(1.0f), 48000);

        Assert.Equal(48000, resampler.WaveFormat.SampleRate);
        Assert.Equal(1, resampler.WaveFormat.Channels);
    }

    [Fact]
    public void NeverReturnsAShortRead_EvenWhenTheSourceUnderFeeds()
    {
        // The source hands back at most 64 frames per call. A short or zero read from a live
        // pipeline is treated as end-of-stream by the playback path and stops the channel for
        // good, so the resampler must always satisfy the request.
        var resampler = new AdaptiveResampler(new StingySource(1.0f, maxFrames: 64), 48000);

        var prime = new float[64];
        resampler.Read(prime);   // warm the filter state

        var buffer = new float[128];
        int read = resampler.Read(buffer);

        Assert.Equal(128, read);

        // And the data must be real signal, not the zero-fill fallback: if the fill loop had
        // exhausted its iteration guard we would see a buffer of zeros here.
        int nonZero = buffer.Count(v => Math.Abs(v) > 0.5f);
        Assert.True(nonZero > 100, $"expected real signal, but only {nonZero}/128 samples were non-zero");
    }

    [Fact]
    public void ConstantInputConvergesToConstantOutput()
    {
        // Drift correction runs at ratio ~1.0, so the resampler must be transparent in the
        // steady state. A DC source makes that checkable exactly.
        var resampler = new AdaptiveResampler(new ConstantSource(0.5f), 48000);

        var buffer = new float[1024];
        resampler.Read(buffer);
        resampler.Read(buffer);

        Assert.All(buffer, v => Assert.InRange(v, 0.45f, 0.55f));
    }

    [Fact]
    public void CorrectionIsClampedToTheHardCeiling()
    {
        var resampler = new AdaptiveResampler(new ConstantSource(1.0f), 48000);

        resampler.SetCorrection(10.0);

        Assert.Equal(EngineTunables.MaxCorrectionHardCeiling, resampler.Correction, precision: 12);
    }

    [Fact]
    public void CorrectionIsReportedInPpm()
    {
        var resampler = new AdaptiveResampler(new ConstantSource(1.0f), 48000);

        resampler.SetCorrection(42e-6);

        Assert.Equal(42.0, resampler.CorrectionPpm, precision: 6);
    }

    [Fact]
    public void SetCorrectionIsIdempotent()
    {
        var resampler = new AdaptiveResampler(new ConstantSource(1.0f), 48000);

        resampler.SetCorrection(100e-6);
        double first = resampler.Correction;
        resampler.SetCorrection(100e-6);

        Assert.Equal(first, resampler.Correction);
    }

    [Fact]
    public void ConvertsFrom44100To48000WithoutEverGoingSilent()
    {
        // Regression test for the reported "other Bluetooth devices produce no sound" bug.
        //
        // A 44.1 kHz source feeding a 48 kHz device is THE case that used to be routed through
        // WdlResamplingSampleProvider, which on NAudio 3.1.0 loses samples and eventually returns
        // 0 permanently when its source under-feeds (#1412). Because that converter was only
        // inserted when the rates DIFFERED, the failure hit exactly the devices whose mix format
        // is 44.1 kHz — many Bluetooth speakers — while wired 48 kHz devices kept working.
        //
        // Reading MANY blocks and requiring signal in each one is what makes this a regression
        // test: the defect's signature is that the first blocks are fine and a later block is
        // silent forever.
        var source = new ConstantSource(0.5f, channels: 1, sampleRate: 44100);
        var resampler = new AdaptiveResampler(source, 48000);

        Assert.Equal(48000, resampler.WaveFormat.SampleRate);

        var buffer = new float[480];
        for (int block = 0; block < 60; block++)
        {
            int read = resampler.Read(buffer);

            Assert.Equal(480, read);
            Assert.True(
                buffer.Any(v => Math.Abs(v) > 0.1f),
                $"block {block} was silent — the resampler stopped producing audio");
        }
    }

    [Fact]
    public void ConvertsFrom48000To44100WithoutEverGoingSilent()
    {
        // The opposite direction, for a device whose mix format is LOWER than the capture.
        var source = new ConstantSource(0.5f, channels: 1, sampleRate: 48000);
        var resampler = new AdaptiveResampler(source, 44100);

        Assert.Equal(44100, resampler.WaveFormat.SampleRate);

        var buffer = new float[441];
        for (int block = 0; block < 60; block++)
        {
            int read = resampler.Read(buffer);

            Assert.Equal(441, read);
            Assert.True(
                buffer.Any(v => Math.Abs(v) > 0.1f),
                $"block {block} was silent — the resampler stopped producing audio");
        }
    }

    [Fact]
    public void StereoRateConversionPreservesBothChannels()
    {
        // Bluetooth speakers are typically stereo, and a bug that dropped a channel would be
        // heard as "quieter" rather than "silent" — worth pinning down explicitly.
        var source = new ConstantSource(0.5f, channels: 2, sampleRate: 44100);
        var resampler = new AdaptiveResampler(source, 48000);

        Assert.Equal(2, resampler.WaveFormat.Channels);

        var buffer = new float[480 * 2];
        resampler.Read(buffer);
        resampler.Read(buffer);

        for (int frame = 0; frame < 480; frame++)
        {
            Assert.True(Math.Abs(buffer[frame * 2]) > 0.1f, $"left channel frame {frame} silent");
            Assert.True(Math.Abs(buffer[(frame * 2) + 1]) > 0.1f, $"right channel frame {frame} silent");
        }
    }

    [Fact]
    public void RateConversionStillCorrectsDrift()
    {
        // The drift nudge must keep working ON TOP of a real rate conversion: the correction is a
        // ratio applied to the input rate, so it composes with 44100->48000 rather than replacing it.
        var source = new ConstantSource(0.5f, channels: 1, sampleRate: 44100);
        var resampler = new AdaptiveResampler(source, 48000);

        var buffer = new float[480];
        resampler.Read(buffer);

        resampler.SetCorrection(150e-6);

        Assert.Equal(150.0, resampler.CorrectionPpm, precision: 6);

        for (int block = 0; block < 20; block++)
        {
            int read = resampler.Read(buffer);
            Assert.Equal(480, read);
            Assert.True(buffer.Any(v => Math.Abs(v) > 0.1f), $"block {block} silent after a correction");
        }
    }
}
