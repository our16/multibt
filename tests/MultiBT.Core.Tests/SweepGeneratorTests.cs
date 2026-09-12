using MultiBT.Core.Audio;
using MultiBT.Core.Calibration;
using Xunit;

namespace MultiBT.Core.Tests;

public sealed class SweepGeneratorTests
{
    [Fact]
    public void CalibrationBufferHasLeadSweepAndTail()
    {
        float[] buffer = SweepGenerator.GenerateCalibrationBuffer();

        int lead = MsToSamples(EngineTunables.CalibrationLeadSilenceMs);
        int sweep = MsToSamples(EngineTunables.CalibrationSweepMs);
        int tail = MsToSamples(EngineTunables.CalibrationTailSilenceMs);

        Assert.Equal(lead + sweep + tail, buffer.Length);

        // The lead silence stops the sweep starting on a discontinuity, and the tail silence
        // gives the quality metrics a clean region to estimate the noise floor from.
        Assert.All(buffer.Take(lead), v => Assert.Equal(0f, v));
        Assert.All(buffer.Skip(lead + sweep), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void SweepNeverClips()
    {
        float[] sweep = SweepGenerator.GenerateSweepOnly();

        Assert.All(sweep, v => Assert.InRange(Math.Abs(v), 0f, (float)EngineTunables.CalibrationSweepAmplitude));
    }

    [Fact]
    public void SweepIsFadedAtBothEnds()
    {
        float[] sweep = SweepGenerator.GenerateSweepOnly();

        // A raised-cosine fade means the first and last samples are exactly zero, so the signal
        // cannot start or end on a step (which would be an audible click).
        Assert.Equal(0f, sweep[0]);
        Assert.Equal(0f, sweep[^1]);
    }

    [Fact]
    public void FrequencyRisesAcrossTheSweep()
    {
        // The log sweep must actually sweep upward. Counting sign changes in a late window
        // against an early one verifies the aural intent rather than just the formula.
        float[] sweep = SweepGenerator.GenerateSweepOnly();

        int quarter = sweep.Length / 4;
        int earlyCrossings = CountZeroCrossings(sweep.AsSpan(quarter, quarter));
        int lateCrossings = CountZeroCrossings(sweep.AsSpan(3 * quarter, quarter));

        Assert.True(
            lateCrossings > earlyCrossings * 2,
            $"expected the late window to oscillate much faster: early={earlyCrossings}, late={lateCrossings}");
    }

    [Fact]
    public void InstantaneousFrequencySpansTheConfiguredBand()
    {
        Assert.Equal(EngineTunables.CalibrationSweepF1, SweepGenerator.InstantaneousFrequency(200, 8000, 0.0), precision: 6);
        Assert.Equal(EngineTunables.CalibrationSweepF2, SweepGenerator.InstantaneousFrequency(200, 8000, 1.0), precision: 6);

        // Monotonic increase.
        double previous = 0;
        for (int i = 0; i <= 10; i++)
        {
            double f = SweepGenerator.InstantaneousFrequency(200, 8000, i / 10.0);
            Assert.True(f > previous);
            previous = f;
        }
    }

    [Fact]
    public void DescendingBandIsRejected()
    {
        Assert.Throws<ArgumentException>(() => SweepGenerator.GenerateSweepOnly(f1: 8000, f2: 200));
    }

    [Fact]
    public void SweepBandExcludesTheExtremes()
    {
        // 200 Hz–8 kHz, not 20 Hz–20 kHz: no Bluetooth speaker reproduces the extremes, and low
        // frequencies are dominated by long-decaying room modes that bias a delay estimate.
        Assert.Equal(200.0, EngineTunables.CalibrationSweepF1);
        Assert.Equal(8000.0, EngineTunables.CalibrationSweepF2);
    }

    private static int CountZeroCrossings(ReadOnlySpan<float> span)
    {
        int crossings = 0;
        for (int i = 1; i < span.Length; i++)
        {
            if ((span[i - 1] < 0 && span[i] >= 0) || (span[i - 1] > 0 && span[i] <= 0))
            {
                crossings++;
            }
        }

        return crossings;
    }

    private static int MsToSamples(double ms) =>
        (int)Math.Round(ms / 1000.0 * EngineTunables.CalibrationSampleRate, MidpointRounding.AwayFromZero);
}
