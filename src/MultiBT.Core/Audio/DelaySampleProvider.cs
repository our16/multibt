using NAudio.Wave;

namespace MultiBT.Core.Audio;

/// <summary>
/// Integer-sample compensation delay line — the SECOND buffer, deliberately separate from the
/// drift ring buffer.
/// </summary>
/// <remarks>
/// <para>
/// <b>This separation is the single most important structural decision in the engine.</b> If
/// compensation were implemented as a read-pointer offset into the drift ring, then
/// <c>fill</c> and <c>delay</c> would be the same variable: the drift controller would
/// faithfully hold the fill at its 5 ms target while silently eating the entire compensation.
/// Two buffers, two purposes, exactly one of them controlled.
/// See docs/PITFALLS.md B1.
/// </para>
/// <para>
/// <b>Runs at the device mix rate</b>, i.e. downstream of <see cref="AdaptiveResampler"/>, so
/// <c>delayFrames = round(ms / 1000 * Fs)</c> is exact and the drift controller cannot perturb
/// it. Quantisation is ≤20.8 µs at 48 kHz — about 40x finer than the tightest audible
/// alignment — so integer samples are sufficient and no fractional interpolation is needed.
/// </para>
/// <para>
/// The ring is zero-initialised, so the first <c>delayFrames</c> frames read out as silence
/// automatically. That gives pre-fill for free with no priming state machine.
/// </para>
/// </remarks>
public sealed class DelaySampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly int _capacityFrames;
    private readonly int _maxUsableFrames;
    private readonly float[] _ring;
    private readonly int _glideThresholdFrames;
    private int _writeFrame;
    private int _delayFrames;
    private int _targetDelayFrames;

    /// <param name="source">Upstream provider, already at the device mix rate.</param>
    /// <param name="maxDelayMs">Largest compensation this line must support.</param>
    public DelaySampleProvider(ISampleProvider source, int maxDelayMs = EngineTunables.MaxDelayMs)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDelayMs);

        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = source.WaveFormat;

        // Capacity covers the full delay range plus slack, so the read pointer can never
        // collide with the write pointer. The slack is a safety margin, NOT usable delay:
        // reporting it as part of the maximum would advertise a range the clamp refuses.
        _capacityFrames = (int)Math.Ceiling(maxDelayMs / 1000.0 * WaveFormat.SampleRate)
                          + EngineTunables.DelayRingSlackFrames;

        _maxUsableFrames = _capacityFrames - EngineTunables.DelayRingSlackFrames;

        _ring = new float[_capacityFrames * _channels];

        _glideThresholdFrames = (int)Math.Round(
            EngineTunables.DelayGlideThresholdMs / 1000.0 * WaveFormat.SampleRate,
            MidpointRounding.AwayFromZero);
    }

    /// <inheritdoc />
    public WaveFormat WaveFormat { get; }

    /// <summary>Delay currently being applied, in frames.</summary>
    public int AppliedDelayFrames => _delayFrames;

    /// <summary>Delay currently being applied, in ms. This is what the device actually gets.</summary>
    public double AppliedDelayMs => _delayFrames / (double)WaveFormat.SampleRate * 1000.0;

    /// <summary>The delay the line is slewing toward, in ms.</summary>
    public double TargetDelayMs => _targetDelayFrames / (double)WaveFormat.SampleRate * 1000.0;

    /// <summary>Largest delay this line supports, in ms. Excludes the internal collision slack.</summary>
    public double MaxDelayMs => _maxUsableFrames / (double)WaveFormat.SampleRate * 1000.0;

    /// <summary>
    /// Requests a new delay in frames and reports whether the change was too large to slew.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the caller must mute, apply, and fade back in rather than gliding.
    /// </returns>
    /// <remarks>
    /// A change larger than <see cref="EngineTunables.DelayGlideThresholdMs"/> must NOT be
    /// slewed: at the 0.5 % pitch budget, 170 ms would take 34 seconds of audibly sliding
    /// audio. A brief gap is strictly better than half a minute of detuned playback.
    /// See docs/PITFALLS.md B9.
    /// </remarks>
    public bool SetDelayFrames(int targetFrames, out bool requiresMuteResync)
    {
        targetFrames = Math.Clamp(targetFrames, 0, _maxUsableFrames);

        int delta = Math.Abs(targetFrames - _delayFrames);

        if (delta <= _glideThresholdFrames)
        {
            _targetDelayFrames = targetFrames;
            requiresMuteResync = false;
            return true;
        }

        _delayFrames = targetFrames;
        _targetDelayFrames = targetFrames;
        requiresMuteResync = true;
        return true;
    }

    /// <summary>Convenience overload taking milliseconds.</summary>
    public bool SetDelayMs(double delayMs, out bool requiresMuteResync)
    {
        int frames = (int)Math.Round(delayMs / 1000.0 * WaveFormat.SampleRate, MidpointRounding.AwayFromZero);
        return SetDelayFrames(frames, out requiresMuteResync);
    }

    /// <summary>
    /// Whether changing to the given delay would require a mute/resync, WITHOUT applying it.
    /// </summary>
    /// <remarks>
    /// This exists so the caller can fade out BEFORE the change lands. Applying first and
    /// discovering afterwards is too late: <see cref="SetDelayFrames"/> steps the read pointer
    /// immediately for large changes, and that step is the click.
    /// </remarks>
    public bool WouldRequireMuteResync(double delayMs)
    {
        int frames = (int)Math.Round(delayMs / 1000.0 * WaveFormat.SampleRate, MidpointRounding.AwayFromZero);
        frames = Math.Clamp(frames, 0, _maxUsableFrames);
        return Math.Abs(frames - _delayFrames) > _glideThresholdFrames;
    }

    /// <inheritdoc />
    public int Read(Span<float> buffer)
    {
        int framesRequested = buffer.Length / _channels;
        if (framesRequested == 0)
        {
            return 0;
        }

        int framesRead = _source.Read(buffer) / _channels;
        if (framesRead == 0)
        {
            return 0;
        }

        AdvanceGlide(framesRead);

        // Push the incoming frames into the ring.
        for (int frame = 0; frame < framesRead; frame++)
        {
            int sourceBase = frame * _channels;
            int targetBase = ((_writeFrame + frame) % _capacityFrames) * _channels;

            for (int channel = 0; channel < _channels; channel++)
            {
                _ring[targetBase + channel] = buffer[sourceBase + channel];
            }
        }

        // Pull the delayed frames back out, in place.
        for (int frame = 0; frame < framesRead; frame++)
        {
            int ringFrame = ((_writeFrame + frame - _delayFrames) % _capacityFrames + _capacityFrames) % _capacityFrames;
            int sourceBase = ringFrame * _channels;
            int targetBase = frame * _channels;

            for (int channel = 0; channel < _channels; channel++)
            {
                buffer[targetBase + channel] = _ring[sourceBase + channel];
            }
        }

        _writeFrame = (_writeFrame + framesRead) % _capacityFrames;
        return framesRead * _channels;
    }

    /// <summary>
    /// Moves the applied delay toward the target by at most the amount that keeps the implied
    /// pitch shift inside the glide budget.
    /// </summary>
    private void AdvanceGlide(int framesRead)
    {
        int delta = _targetDelayFrames - _delayFrames;
        if (delta == 0)
        {
            return;
        }

        // Shifting the read pointer by k frames over N frames is a k/N rate change.
        int maxStep = Math.Max(1, (int)(framesRead * EngineTunables.DelayGlideMaxPitchShift));

        if (Math.Abs(delta) <= maxStep)
        {
            _delayFrames = _targetDelayFrames;
            return;
        }

        _delayFrames += Math.Sign(delta) * maxStep;
    }
}
