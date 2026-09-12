using MultiBT.Core.Audio;
using NAudio.Wave;
using Xunit;

namespace MultiBT.Core.Tests;

/// <summary>
/// The per-channel peak measurement, which is what makes device positioning verifiable.
/// </summary>
/// <remarks>
/// This is a verification tool, so it needs verifying itself: if LeftPeak and RightPeak were swapped, or
/// both reported the aggregate, tests built on top of it would "confirm" panning that does not work. That
/// matters here because the aggregate level cannot tell a device panned right from one panned left -- both
/// give the same total.
/// </remarks>
public sealed class SignalProbeChannelTests
{
    private const double Tolerance = 0.0001;

    private sealed class StereoSource(float left, float right, int channels = 2) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, channels);

        public int Read(Span<float> buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = i % 2 == 0 ? left : right;
            }

            return buffer.Length;
        }
    }

    private static SignalProbe Read(SignalProbe probe, int frames)
    {
        var buffer = new float[frames * probe.WaveFormat.Channels];
        _ = probe.Read(buffer);
        return probe;
    }

    [Fact]
    public void LeftAndRightAreMeasuredSeparately()
    {
        var probe = Read(new SignalProbe(new StereoSource(0.4f, 0.8f)), 100);

        Assert.Equal(0.4, probe.LeftPeak!.Value, Tolerance);
        Assert.Equal(0.8, probe.RightPeak!.Value, Tolerance);
    }

    [Fact]
    public void AOneSidedSignalIsReportedOnTheCorrectSide()
    {
        // A device panned hard right must show nothing on the left. This is the assertion that would catch
        // the channels being swapped.
        var probe = Read(new SignalProbe(new StereoSource(0f, 0.8f)), 100);

        Assert.Equal(0.0, probe.LeftPeak!.Value, Tolerance);
        Assert.Equal(0.8, probe.RightPeak!.Value, Tolerance);

        var mirrored = Read(new SignalProbe(new StereoSource(0.8f, 0f)), 100);

        Assert.Equal(0.8, mirrored.LeftPeak!.Value, Tolerance);
        Assert.Equal(0.0, mirrored.RightPeak!.Value, Tolerance);
    }

    [Fact]
    public void TheAggregatePeakIsStillTheLargerOfTheTwo()
    {
        var probe = Read(new SignalProbe(new StereoSource(0.3f, 0.9f)), 100);

        Assert.Equal(0.9, probe.Peak, Tolerance);
    }

    [Fact]
    public void ResetClearsBothChannels()
    {
        SignalProbe probe = Read(new SignalProbe(new StereoSource(0.4f, 0.8f)), 100);
        probe.ResetPeak();

        Assert.Equal(0.0, probe.LeftPeak!.Value, Tolerance);
        Assert.Equal(0.0, probe.RightPeak!.Value, Tolerance);
        Assert.Equal(0.0, probe.Peak, Tolerance);
    }

    [Fact]
    public void AMonoStreamReportsNullRatherThanTwoEqualNumbers()
    {
        // Null, not the aggregate: a caller comparing left against right must not be handed two identical
        // numbers on a mono stream and conclude the panning works.
        var probe = Read(new SignalProbe(new StereoSource(0.5f, 0.5f, channels: 1)), 100);

        Assert.Null(probe.LeftPeak);
        Assert.Null(probe.RightPeak);
        Assert.Equal(0.5, probe.Peak, Tolerance);
    }
}
