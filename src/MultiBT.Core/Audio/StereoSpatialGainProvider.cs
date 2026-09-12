using NAudio.Wave;

namespace MultiBT.Core.Audio;

/// <summary>
/// Applies a separate gain to the left and right channels of a stereo stream.
/// </summary>
/// <remarks>
/// <para>
/// The chain had one gain for the whole signal, which is enough to mute or to attenuate but cannot express
/// "this speaker sits to your right", because placing a device needs the two channels scaled differently.
/// This is that missing stage.
/// </para>
/// <para>
/// <b>Stereo only, and it says so.</b> A mono source is rejected rather than guessed at: panning a single
/// channel would mean inventing the other one, and the engine already inserts an explicit channel-matching
/// stage (<c>OutputChannel.MatchChannels</c>) before this point precisely so that every provider after it can
/// assume a channel count. Silently accepting mono here would move that decision somewhere less visible.
/// </para>
/// <para>
/// Gains are written from the UI thread while <see cref="Read"/> runs on the audio thread, so they are
/// volatile and applied per buffer. There is no lock: a gain changing mid-buffer is audible as nothing at
/// all, while a lock on the audio thread is a source of dropouts.
/// </para>
/// </remarks>
public sealed class StereoSpatialGainProvider : ISampleProvider
{
    private readonly ISampleProvider _source;

    // Volatile: written by the UI thread, read by the audio thread.
    private volatile float _left = 1f;
    private volatile float _right = 1f;

    /// <param name="source">A stereo source. The engine matches channels before this stage.</param>
    /// <exception cref="ArgumentException">The source is not stereo.</exception>
    public StereoSpatialGainProvider(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.WaveFormat.Channels != 2)
        {
            throw new ArgumentException(
                $"Spatial gain needs a stereo source; got {source.WaveFormat.Channels} channel(s). "
                + "Insert a channel-matching stage before this one instead of expecting this to guess.",
                nameof(source));
        }

        _source = source;
    }

    /// <inheritdoc />
    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>The gain currently applied to the left channel.</summary>
    public float LeftGain => _left;

    /// <summary>The gain currently applied to the right channel.</summary>
    public float RightGain => _right;

    /// <summary>
    /// Sets both gains, clamped to 0..1.
    /// </summary>
    /// <remarks>
    /// Clamped rather than trusted: these come from a position calculation, and a gain above unity would
    /// amplify a signal that is already at full scale into clipping.
    /// </remarks>
    public void SetGains(double left, double right)
    {
        _left = (float)Math.Clamp(left, 0.0, 1.0);
        _right = (float)Math.Clamp(right, 0.0, 1.0);
    }

    /// <inheritdoc />
    public int Read(Span<float> buffer)
    {
        int read = _source.Read(buffer);

        // Interleaved stereo: even indices are left, odd are right.
        for (int i = 0; i + 1 < read; i += 2)
        {
            buffer[i] *= _left;
            buffer[i + 1] *= _right;
        }

        return read;
    }
}
