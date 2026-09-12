using NAudio.Wave;

namespace MultiBT.Core.Audio;

/// <summary>
/// Measures the signal level flowing through a channel, immediately BEFORE the per-device gain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> "The second device makes no sound" splits into two very different
/// failures that look identical from the outside, and nothing else in the engine can tell them
/// apart:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>The channel is producing silence.</b> Capture picked up nothing, or the ring is empty, or a
/// conversion is broken. The samples arriving at the player are all zeros.
/// </description></item>
/// <item><description>
/// <b>The channel is producing audio but the device is not playing it.</b> The samples are fine;
/// the endpoint is muted, at zero volume, in a bad exclusive-mode state, or simply not rendering.
/// </description></item>
/// </list>
/// <para>
/// Without a probe the two are indistinguishable — and the earlier version of this engine shipped
/// exactly that blind spot: the ring was empty, every read was zero-filled, and the only counter
/// that looked relevant (<c>StarvedReads</c>) stayed near zero because most reads began with a
/// fraction of a millisecond of real data before the zero-fill took over.
/// </para>
/// <para>
/// <b>Why BEFORE the gain.</b> The per-device gain is deliberately allowed to be 0 — a muted device
/// is a normal state, and it is also how the self-test runs silently. Measuring after the gain would
/// report "silence" for every intentionally muted channel and hide the very thing being diagnosed.
/// </para>
/// <para>
/// Cost is one pass over an already-hot buffer, with no allocation.
/// </para>
/// </remarks>
public sealed class SignalProbe : ISampleProvider
{
    /// <summary>Amplitude at or below which a block is considered silent (about -120 dBFS).</summary>
    private const float SilenceFloor = 1e-6f;

    private readonly ISampleProvider _source;
    private long _blocks;
    private long _silentBlocks;
    private long _peakMicro;
    private long _leftPeakMicro;
    private long _rightPeakMicro;
    private long _sumSquaresMicro;
    private long _samples;

    public SignalProbe(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        WaveFormat = source.WaveFormat;
    }

    /// <inheritdoc />
    public WaveFormat WaveFormat { get; }

    /// <summary>Largest absolute sample seen since the last <see cref="ResetPeak"/>, 0..1.</summary>
    public double Peak => Interlocked.Read(ref _peakMicro) / 1e6;

    /// <summary>RMS level since construction, 0..1.</summary>
    public double Rms
    {
        get
        {
            long samples = Interlocked.Read(ref _samples);
            return samples <= 0 ? 0.0 : Math.Sqrt(Interlocked.Read(ref _sumSquaresMicro) / 1e6 / samples);
        }
    }

    /// <summary>Blocks observed since construction.</summary>
    public long Blocks => Interlocked.Read(ref _blocks);

    /// <summary>Blocks whose peak was below <see cref="SilenceFloor"/>.</summary>
    public long SilentBlocks => Interlocked.Read(ref _silentBlocks);

    /// <summary>
    /// Fraction of blocks that were entirely silent, 0..1.
    /// </summary>
    /// <remarks>
    /// A value near 1.0 with a source that is definitely playing is the signature of a channel that
    /// is being fed nothing but zero-fill.
    /// </remarks>
    public double SilentBlockFraction
    {
        get
        {
            long blocks = Interlocked.Read(ref _blocks);
            return blocks <= 0 ? 0.0 : (double)Interlocked.Read(ref _silentBlocks) / blocks;
        }
    }

    /// <summary>True when the channel has produced at least one non-silent block.</summary>
    public bool HasSignal => Interlocked.Read(ref _blocks) > Interlocked.Read(ref _silentBlocks);

    /// <summary>Clears the running peak so the caller can read "peak since last check".</summary>
    public void ResetPeak()
    {
        Interlocked.Exchange(ref _peakMicro, 0);
        Interlocked.Exchange(ref _leftPeakMicro, 0);
        Interlocked.Exchange(ref _rightPeakMicro, 0);
    }

    /// <summary>Peak of the LEFT channel, or null when the stream is not stereo.</summary>
    /// <remarks>
    /// Null rather than the aggregate peak: a caller comparing left against right must not be handed two
    /// identical numbers on a mono stream and conclude the panning works.
    /// </remarks>
    public double? LeftPeak => WaveFormat.Channels == 2
        ? Interlocked.Read(ref _leftPeakMicro) / 1e6
        : null;

    /// <summary>Peak of the RIGHT channel, or null when the stream is not stereo.</summary>
    public double? RightPeak => WaveFormat.Channels == 2
        ? Interlocked.Read(ref _rightPeakMicro) / 1e6
        : null;

    /// <inheritdoc />
    public int Read(Span<float> buffer)
    {
        int read = _source.Read(buffer);

        if (read <= 0)
        {
            return read;
        }

        float peak = 0f;
        double sumSquares = 0.0;

        // Per-channel peaks, for stereo only. This is what makes device POSITIONING verifiable: the aggregate
        // peak above cannot tell a correctly panned speaker from one panned the wrong way round, because both
        // produce the same total level. Null on a non-stereo stream, so a caller cannot mistake "not measured"
        // for "silent".
        bool stereo = WaveFormat.Channels == 2;
        float leftPeak = 0f;
        float rightPeak = 0f;

        for (int i = 0; i < read; i++)
        {
            float sample = buffer[i];
            float magnitude = Math.Abs(sample);

            if (magnitude > peak)
            {
                peak = magnitude;
            }

            if (stereo)
            {
                if ((i & 1) == 0)
                {
                    if (magnitude > leftPeak)
                    {
                        leftPeak = magnitude;
                    }
                }
                else if (magnitude > rightPeak)
                {
                    rightPeak = magnitude;
                }
            }

            sumSquares += (double)sample * sample;
        }

        if (stereo)
        {
            RaisePeak(ref _leftPeakMicro, leftPeak);
            RaisePeak(ref _rightPeakMicro, rightPeak);
        }

        Interlocked.Increment(ref _blocks);

        if (peak <= SilenceFloor)
        {
            Interlocked.Increment(ref _silentBlocks);
        }

        long scaledPeak = (long)(peak * 1e6);
        RaisePeak(ref _peakMicro, peak);

        Interlocked.Add(ref _sumSquaresMicro, (long)(sumSquares * 1e6));
        Interlocked.Add(ref _samples, read);

        return read;
    }

    /// <summary>Raises a stored peak only if the new one is higher. Lock-free: the audio thread must not wait.</summary>
    private static void RaisePeak(ref long field, float peak)
    {
        long scaled = (long)(peak * 1e6);
        long current;

        while ((current = Interlocked.Read(ref field)) < scaled)
        {
            if (Interlocked.CompareExchange(ref field, scaled, current) == current)
            {
                break;
            }
        }
    }
}
