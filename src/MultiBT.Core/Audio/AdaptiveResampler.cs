using NAudio.Dsp;
using NAudio.Wave;

namespace MultiBT.Core.Audio;

/// <summary>
/// A resampler whose ratio can be nudged at runtime, used as the drift-correction actuator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this class has to exist.</b> Neither of NAudio's shipped resamplers can be retuned
/// mid-stream: <c>WdlResamplingSampleProvider</c> keeps its <c>WdlResampler</c> in a
/// <c>private readonly</c> field, and <c>MediaFoundationResampler</c> fixes its output format
/// in <c>CreateTransform()</c>. Recreating either resets the fractional position and filter
/// state, which is an audible discontinuity. <c>NAudio.Dsp.WdlResampler</c> is public and its
/// <c>SetRates</c> only recomputes <c>m_ratio</c> — it does not touch the input buffer, the
/// fractional position or the IIR history — so a ratio change preserves phase.
/// See docs/PITFALLS.md A12.
/// </para>
/// <para>
/// <b>Why interp mode.</b> <c>SetMode(interp: true, ...)</c> gives linear interpolation plus
/// two cascaded IIR filters, which is the right trade for a ~1:1 nudge. Sinc mode's
/// <c>BuildLowPass</c> resizes and rebuilds a 2048-tap table whenever the filter position
/// changes — which in a 5 Hz control loop would be every 200 ms on the audio thread. Do the
/// fixed format conversion elsewhere and keep this instance in interp mode.
/// </para>
/// </remarks>
public sealed class AdaptiveResampler : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly WdlResampler _resampler = new();
    private readonly int _channels;
    private readonly double _inputRate;
    private readonly double _outputRate;
    private double _correction;

    /// <param name="source">Input provider. Should run at the capture rate.</param>
    /// <param name="outputSampleRate">Target rate — the device mix rate.</param>
    public AdaptiveResampler(ISampleProvider source, int outputSampleRate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputSampleRate);

        _source = source;
        _channels = source.WaveFormat.Channels;
        _inputRate = source.WaveFormat.SampleRate;
        _outputRate = outputSampleRate;

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(outputSampleRate, _channels);

        _resampler.SetMode(interp: true, filtercnt: 2, sinc: false);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(false);              // output-driven
        _resampler.SetRates(_inputRate, _outputRate);
    }

    /// <inheritdoc />
    public WaveFormat WaveFormat { get; }

    /// <summary>Current correction ratio, dimensionless.</summary>
    public double Correction => _correction;

    /// <summary>Current correction in ppm, for diagnostics.</summary>
    public double CorrectionPpm => _correction * 1e6;

    /// <summary>
    /// Applies a new correction ratio. Values are clamped to
    /// <see cref="EngineTunables.MaxCorrection"/>; the caller is responsible for rate-limiting.
    /// </summary>
    public void SetCorrection(double correction)
    {
        correction = Math.Clamp(
            correction,
            -EngineTunables.MaxCorrectionHardCeiling,
            EngineTunables.MaxCorrectionHardCeiling);

        if (Math.Abs(correction - _correction) < 1e-12)
        {
            return;
        }

        _correction = correction;

        // Positive correction means "consume input slightly faster", i.e. treat the device
        // as slightly slow so its buffer stops draining.
        _resampler.SetRates(_inputRate * (1.0 + correction), _outputRate);
    }

    /// <inheritdoc />
    public int Read(Span<float> buffer)
    {
        int framesRequested = buffer.Length / _channels;
        if (framesRequested == 0)
        {
            return 0;
        }

        int framesWritten = 0;

        // ResampleOut can satisfy FEWER frames than requested (fractional position / filter
        // priming), so loop until the output is full.
        for (int attempt = 0; attempt < EngineTunables.ResamplerMaxIterations && framesWritten < framesRequested; attempt++)
        {
            int want = framesRequested - framesWritten;

            int framesNeeded = _resampler.ResamplePrepare(want, _channels, out Span<float> inBuffer);

            // Defensive slice: feed the resampler only as much input as it offered AND as much
            // as the buffer actually holds. Feeding fewer frames than it asked for is legal —
            // ResampleOut takes the real count — whereas slicing past the end would throw.
            int inputSamples = Math.Min(framesNeeded * _channels, inBuffer.Length);

            int framesAvailable = inputSamples <= 0
                ? 0
                : _source.Read(inBuffer.Slice(0, inputSamples)) / _channels;

            int produced = _resampler.ResampleOut(
                buffer.Slice(framesWritten * _channels),
                framesAvailable,
                want,
                _channels);

            if (produced <= 0)
            {
                break;
            }

            framesWritten += produced;
        }

        if (framesWritten < framesRequested)
        {
            // NEVER return a short or zero read from a live pipeline: a 0-byte read is treated
            // as end-of-stream by the playback path and stops the channel for good. Zero-fill
            // the remainder instead. See docs/PITFALLS.md A9.
            buffer.Slice(framesWritten * _channels).Clear();
            framesWritten = framesRequested;
        }

        return framesWritten * _channels;
    }
}
