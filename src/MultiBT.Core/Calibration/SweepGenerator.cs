using MultiBT.Core.Audio;

namespace MultiBT.Core.Calibration;

/// <summary>
/// Reference signal used for acoustic latency measurement.
/// </summary>
/// <remarks>
/// <para>
/// An <b>exponential (logarithmic) sine sweep</b>, per Farina's swept-sine technique.
/// It beats the alternatives for this specific job:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>vs. linear chirp</b>: a linear chirp spends equal time per Hz, so its energy per octave
/// rises with frequency — most of it lands in 8–20 kHz, exactly where a Bluetooth speaker is
/// rolling off and beaming. A log sweep puts equal energy per octave, matching how speakers
/// and rooms divide the spectrum.
/// </description></item>
/// <item><description>
/// <b>vs. MLS</b>: MLS needs exact periodicity to keep its ideal autocorrelation, which is
/// fragile when playback and capture clocks differ; it is highly sensitive to time-variance
/// and nonlinearity, both of which fold back as spurious peaks spread across ALL delays; and
/// its crest factor is worse for the same peak level. That is the worst possible profile for a
/// moving, lossy, compressing Bluetooth path.
/// </description></item>
/// <item><description>
/// <b>vs. a click/impulse</b>: best raw time localisation in theory, but far too little energy
/// for real-room SNR, no real speaker reproduces a Dirac (the radiated impulse is band-limited
/// and its onset smeared by driver group delay), and it can trip a Bluetooth speaker's
/// protection limiter. This is the classic clap test — fine for a human to judge by ear, not a
/// precision instrument.
/// </description></item>
/// </list>
/// <para>
/// The band is 200 Hz–8 kHz rather than 20 Hz–20 kHz: no Bluetooth speaker reproduces the
/// extremes, and low frequencies are dominated by long-decaying room modes, which biases a
/// delay estimate. The 200 Hz–8 kHz band also gives a correlation-peak width of
/// 1/7800 ≈ 128 µs — two orders of magnitude finer than the ~1 ms we need.
/// See docs/SPEC.md §5.4.1.
/// </para>
/// </remarks>
public static class SweepGenerator
{
    /// <summary>
    /// Generates the complete calibration buffer: lead silence, then the faded sweep, then
    /// tail silence. The buffer is what actually gets queued into the device's normal chain.
    /// </summary>
    /// <remarks>
    /// The lead and tail silence are not padding for convenience. The lead silence stops the
    /// sweep from starting on a discontinuity (a click), and the tail silence gives the
    /// quality metrics a clean region in which to estimate the noise floor.
    /// </remarks>
    public static float[] GenerateCalibrationBuffer(
        int sampleRate = EngineTunables.CalibrationSampleRate,
        double f1 = EngineTunables.CalibrationSweepF1,
        double f2 = EngineTunables.CalibrationSweepF2,
        double sweepMs = EngineTunables.CalibrationSweepMs,
        double amplitude = EngineTunables.CalibrationSweepAmplitude,
        double fadeMs = EngineTunables.CalibrationFadeMs,
        double leadSilenceMs = EngineTunables.CalibrationLeadSilenceMs,
        double tailSilenceMs = EngineTunables.CalibrationTailSilenceMs)
    {
        float[] sweep = GenerateSweepOnly(sampleRate, f1, f2, sweepMs, amplitude, fadeMs);

        int lead = MsToSamples(leadSilenceMs, sampleRate);
        int tail = MsToSamples(tailSilenceMs, sampleRate);

        var buffer = new float[lead + sweep.Length + tail];
        Array.Copy(sweep, 0, buffer, lead, sweep.Length);
        return buffer;
    }

    /// <summary>
    /// Generates just the sweep, faded — this is the exact reference the matcher correlates
    /// against, so it must be bit-identical to the samples that were sent to the device.
    /// </summary>
    public static float[] GenerateSweepOnly(
        int sampleRate = EngineTunables.CalibrationSampleRate,
        double f1 = EngineTunables.CalibrationSweepF1,
        double f2 = EngineTunables.CalibrationSweepF2,
        double sweepMs = EngineTunables.CalibrationSweepMs,
        double amplitude = EngineTunables.CalibrationSweepAmplitude,
        double fadeMs = EngineTunables.CalibrationFadeMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(f1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(f2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sweepMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(amplitude, 1.0);

        if (f2 <= f1)
        {
            throw new ArgumentException("f2 must be greater than f1 for a rising log sweep.", nameof(f2));
        }

        int length = MsToSamples(sweepMs, sampleRate);
        var sweep = new float[length];

        double durationSeconds = sweepMs / 1000.0;
        double ratio = Math.Log(f2 / f1);

        // Exponential sweep:
        //   f(t)    = f1 * (f2/f1)^(t/T)
        //   phase(t)= 2*pi*f1*T/ln(f2/f1) * ( exp((t/T)*ln(f2/f1)) - 1 )
        double phaseScale = 2.0 * Math.PI * f1 * durationSeconds / ratio;

        for (int n = 0; n < length; n++)
        {
            double t = (double)n / sampleRate;
            double phase = phaseScale * (Math.Exp(t / durationSeconds * ratio) - 1.0);
            sweep[n] = (float)(amplitude * Math.Sin(phase));
        }

        ApplyRaisedCosineFades(sweep, MsToSamples(fadeMs, sampleRate));
        return sweep;
    }

    /// <summary>
    /// Returns the instantaneous frequency of the sweep at a normalised position, for tests
    /// and diagnostics.
    /// </summary>
    public static double InstantaneousFrequency(double f1, double f2, double normalisedPosition)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(f1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(f2);
        return f1 * Math.Pow(f2 / f1, normalisedPosition);
    }

    private static void ApplyRaisedCosineFades(float[] buffer, int fadeSamples)
    {
        if (fadeSamples <= 0)
        {
            return;
        }

        // Never let the two fades overlap on a short sweep.
        int fade = Math.Min(fadeSamples, buffer.Length / 2);
        if (fade <= 0)
        {
            return;
        }

        for (int n = 0; n < fade; n++)
        {
            // Raised cosine (Hann half-window), 0 -> 1.
            double w = 0.5 * (1.0 - Math.Cos(Math.PI * n / fade));
            buffer[n] *= (float)w;
            buffer[buffer.Length - 1 - n] *= (float)w;
        }
    }

    private static int MsToSamples(double ms, int sampleRate) =>
        (int)Math.Round(ms / 1000.0 * sampleRate, MidpointRounding.AwayFromZero);
}
