using System.Diagnostics;
using MultiBT.Core.Sync;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MultiBT.Core.Audio;

/// <summary>
/// One output device: its own ring buffer, resampler, delay line, gain and player.
/// </summary>
/// <remarks>
/// <para>
/// <b>Chain order is load-bearing and must not be rearranged:</b>
/// </para>
/// <code>
/// ring buffer          (drift buffer — the ONLY thing the controller steers)
///   -> RingDiagnostics (counts the failures NAudio hides)
///   -> channel match   (1->2 or 2->1)
///   -> AdaptiveResampler (fixed conversion AND the drift nudge; ratio is the control input)
///   -> DelaySampleProvider (compensation — a SEPARATE buffer, at the device mix rate)
///   -> VolumeSampleProvider (per-device gain)
///   -> MeteringSampleProvider (UI only)
///   -> SampleToWaveProvider -> WasapiPlayer
/// </code>
/// <para>
/// Resampling precedes delay so the delay line runs at the device mix rate, which makes
/// <c>delayFrames = round(ms/1000*Fs)</c> exact and keeps the drift controller from perturbing
/// it. See docs/PITFALLS.md B1 and docs/SPEC.md §6.2.
/// </para>
/// <para>
/// <b>Why one resampler and not two.</b> A separate fixed-rate converter would normally be
/// <c>WdlResamplingSampleProvider</c> — but on NAudio 3.1.0 that type loses samples and
/// eventually returns 0 permanently when its source under-feeds, which is exactly the
/// <c>BufferedWaveProvider</c>-backed pattern used here (NAudio #1412, fixed only in an
/// unreleased version). Permanently silent audio is a far worse outcome than any marginal
/// difference in conversion quality, so both the fixed conversion and the drift nudge are done
/// by <see cref="AdaptiveResampler"/>, which uses the same WDL filter configuration NAudio's
/// own provider uses (<c>interp, filtercnt: 2, sinc: false</c>). Conversion quality is
/// therefore unchanged from what NAudio ships; only the defect is avoided.
/// See docs/PITFALLS.md A2.
/// </para>
/// <para>
/// <b>One channel = one provider instance = one player.</b> A
/// <c>BufferedWaveProvider</c> is safe for one writer and one reader; it is NOT safe for
/// multiple concurrent readers. Fan-out means one buffer per channel, never one shared buffer.
/// </para>
/// </remarks>
public sealed class OutputChannel : IAsyncDisposable
{
    private readonly BufferedWaveProvider _ring;

    /// <summary>
    /// Per-channel gain for device positioning, or null when this device is not stereo.
    /// </summary>
    /// <remarks>
    /// Null for a multichannel device rather than a throw: the provider refuses non-stereo by design, and a
    /// chain that throws would stop the device opening at all. The cost is that positioning does not pan such
    /// a device, which is honest — it has more channels than a left/right pair, and placing it is a different
    /// problem from placing a speaker.
    /// </remarks>
    private StereoSpatialGainProvider? _spatial;

    /// <summary>Peaks of what is actually played, i.e. after the gain stages. Null when not stereo.</summary>
    private readonly SignalProbe? _outputProbe;
    private readonly RingDiagnostics _ringDiagnostics;
    private readonly AdaptiveResampler _resampler;
    private readonly DelaySampleProvider _delay;

    /// <summary>Delay asked for by the user or by the latency compensation, in ms.</summary>
    private double _userDelayMs;

    /// <summary>Extra delay implied by this device's position, in ms.</summary>
    private double _spatialDelayMs;

    /// <summary>Upper bound for the combined delay, as the delay line was built with.</summary>
    private readonly int _maxDelayMs;

    /// <summary>
    /// What the delay line is actually set to: the user's delay PLUS the distance delay.
    /// </summary>
    /// <remarks>
    /// Kept as two separate numbers and summed here, rather than letting the position overwrite the delay.
    /// The delay line is shared by the slider, by the latency compensation and now by geometry, and the one
    /// thing that must never happen is a position change silently discarding a delay the user set by hand.
    /// </remarks>
    private double TotalDelayMs => Math.Clamp(_userDelayMs + _spatialDelayMs, 0.0, _maxDelayMs);
    private readonly SignalProbe _signalProbe;
    private readonly VolumeSampleProvider _volume;
    private readonly MeteringSampleProvider _meter;
    private readonly DriftController _drift;
    private WasapiPlayer _player;
    private readonly Stopwatch _uptime = new();

    private long _resyncCount;

    /// <summary>
    /// Whether the fill has been settled to the target once, at the end of warm-up.
    /// </summary>
    /// <remarks>
    /// Once only, deliberately. Re-settling on every tick would drop audio continuously and would turn a stable
    /// surplus into an audible stutter; the point is to correct where the first pull left the ring, not to keep
    /// correcting.
    /// </remarks>
    private bool _settledAfterWarmUp;

    /// <summary>
    /// Whether the drift correction runs. Off only in the self-test, to measure the chain's raw rate balance.
    /// </summary>
    public bool DriftCorrectionEnabled
    {
        get => _drift.Enabled;
        set => _drift.Enabled = value;
    }
    private int _engineLatencyMs;
    private double _lastFillMinMs;
    private double _lastFillMaxMs;
    private double _lastFillMeanMs;
    private readonly int _captureSampleRate;
    private readonly int _captureChannels;
    private readonly int _chainChannels;
    private readonly double _endpointVolumeScalar;
    private readonly bool _endpointMuted;
    private double _targetGain;
    private bool _startupRampActive;
    private volatile bool _rampAborted;
    private EventHandler<float>? _levelChanged;
    private readonly byte[] _trimScratch = new byte[64 * 1024];
    private Exception? _fault;
    private bool _disposed;

    public OutputChannel(
        string deviceKey,
        MMDevice device,
        WaveFormat captureFormat,
        int engineLatencyMs,
        double gain,
        int maxDelayMs = EngineTunables.MaxDelayMs)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceKey);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(captureFormat);

        DeviceKey = deviceKey;
        _engineLatencyMs = engineLatencyMs;
        _drift = new DriftController(engineLatencyMs);

        // Resolve the device mix format from our OWN client. The player also negotiates it
        // internally on Init, but we need it up front to build the chain at the right rate.
        using (AudioClient probe = device.CreateAudioClient())
        {
            DeviceMixFormat = probe.MixFormat;
        }

        // Read the ENDPOINT's own volume and mute state.
        //
        // This is deliberately separate from the chain gain, and it is the single most common reason
        // a channel that is provably carrying audio is still inaudible: the engine can be feeding
        // perfect samples into a player whose endpoint is muted or at 0 %.
        try
        {
            AudioEndpointVolume endpointVolume = device.AudioEndpointVolume;

            try
            {
                _endpointVolumeScalar = endpointVolume.MasterVolumeLevelScalar;

                if (endpointVolume.Mute)
                {
                    // Unmute it, and say so.
                    //
                    // A muted endpoint is THE most common reason a channel that is provably carrying
                    // correct audio is still inaudible — measured on the machine this was developed
                    // on: the Bluetooth speaker's endpoint was muted, our samples arrived at
                    // -2 dBFS, and Windows discarded every one of them. From the outside that is
                    // indistinguishable from a broken engine, which is exactly why it cost so long
                    // to find.
                    //
                    // Unmuting is the right call rather than merely warning: the user explicitly
                    // enabled this device for output, so a muted endpoint contradicts a stated
                    // intent. The VOLUME is left alone — that is a level choice, not a contradiction.
                    endpointVolume.Mute = false;
                    EndpointUnmutedOnStart = true;
                }

                _endpointMuted = endpointVolume.Mute;
            }
            finally
            {
                // Release immediately: holding an endpoint-volume COM reference across a
                // sleep/resume is exactly the kind of stale reference this engine avoids.
                endpointVolume.Dispose();
            }
        }
        catch (Exception)
        {
            _endpointVolumeScalar = double.NaN;   // unavailable on this endpoint
        }

        // Recorded purely for diagnostics. "Why is this one device silent?" is almost always
        // answered by the formats, and neither number is visible anywhere else in the app.
        _captureSampleRate = captureFormat.SampleRate;
        _captureChannels = captureFormat.Channels;

        _ring = new BufferedWaveProvider(captureFormat, TimeSpan.FromSeconds(EngineTunables.BufferSeconds))
        {
            // MUST be true. A 0-byte read is treated as end-of-stream by the playback path and
            // stops this channel permanently; loopback delivers nothing at all during silence.
            ReadFully = true,

            // Deliberately false. When true, AddSamples silently drops the NEWEST samples with
            // no exception and no counter. We resync explicitly on a bounded backlog instead.
            DiscardOnBufferOverflow = false,
        };

        _ringDiagnostics = new RingDiagnostics(_ring, _ring.ToSampleProvider());

        // PRE-FILL the drift ring to target depth PLUS the player's own buffer.
        //
        // Two separate facts make this necessary, and missing either one leaves the channel silent:
        //
        // 1. The controller cannot create a buffer. The engine starts the player with an EMPTY ring;
        //    capture then feeds at exactly real time while playback consumes at the device clock, so
        //    the fill stays wherever it started — at zero. The controller's authority is +-200 ppm
        //    (0.02 %), which can NUDGE an established buffer but would need ~500 s of saturated
        //    correction to CREATE 105 ms of one. Left alone it simply pins at the limit.
        //
        // 2. WASAPI fills its ENTIRE buffer on the player's first pull. With the endpoint granting
        //    ~engineLatencyMs (measured: requested 100 ms -> granted 100 ms), that first pull takes
        //    engineLatencyMs worth of input straight out of the ring. Pre-filling only to f* would
        //    therefore hand the whole target to the player and leave ~5 ms behind — and because
        //    every later read drains whatever is available (ReadFully zero-fills the remainder), the
        //    ring can never recover its depth on its own.
        //
        // Measured before this fix (tools/MirrorSelfTest): "fill 0.5-0.5 ms, -200 ppm, correction
        // saturated, 50% of a read was silence" on every channel — an effectively silent output with
        // no exception anywhere.
        double prefillMs = (engineLatencyMs + EngineTunables.TargetBacklogMarginMs)
                           + engineLatencyMs
                           + EngineTunables.PrefillSlackMs;

        int prefillBytes = (int)(prefillMs / 1000.0 * captureFormat.AverageBytesPerSecond);

        // Whole frames only: a partial frame would desynchronise the interleaved channels.
        prefillBytes -= prefillBytes % captureFormat.BlockAlign;

        // Never exceed the ring's own capacity.
        prefillBytes = Math.Min(prefillBytes, _ring.BufferLength - captureFormat.BlockAlign);

        if (prefillBytes > 0)
        {
            _ring.AddSamples(new byte[prefillBytes]);
        }

        ISampleProvider samples = MatchChannels(_ringDiagnostics, DeviceMixFormat);

        // NO separate fixed-rate converter here — AdaptiveResampler performs the FULL conversion.
        //
        // It is constructed with the source rate as its input and the device mix rate as its
        // output, so it handles 44100<->48000 just as well as the ±200 ppm drift nudge layered on
        // top of it. Its filter configuration (interp, filtercnt: 2, sinc: false) is the SAME one
        // NAudio's own WdlResamplingSampleProvider uses, so conversion quality is unchanged.
        //
        // A WdlResamplingSampleProvider used to sit here, and it broke Bluetooth outputs
        // specifically. On NAudio 3.1.0 that type loses samples and EVENTUALLY RETURNS 0
        // PERMANENTLY when its source under-feeds (#1412, fixed only in an unreleased version).
        // Its source here is a BufferedWaveProvider-backed chain — exactly the pattern that
        // triggers it — and because the converter was inserted only when the rates DIFFERED, the
        // failure hit precisely the devices whose mix format is 44.1 kHz while the captured
        // device runs at 48 kHz, i.e. many Bluetooth speakers, and nothing else. A permanently
        // zero read is permanent silence on that channel.
        // See docs/PITFALLS.md A2 and PROGRESS.md "问题 2".
        _resampler = new AdaptiveResampler(samples, DeviceMixFormat.SampleRate);

        _chainChannels = _resampler.WaveFormat.Channels;

        _delay = new DelaySampleProvider(_resampler, maxDelayMs);
        _maxDelayMs = maxDelayMs;

        // Probes the signal BEFORE the per-device gain. Placed after the delay line so it sees
        // exactly what the player will receive, but before the gain so an intentionally muted
        // channel still reports whether audio is flowing.
        _signalProbe = new SignalProbe(_delay);

        _volume = new VolumeSampleProvider(_signalProbe) { Volume = 0f };

        // Recorded so the start-up ramp knows where to land. The chain stays at 0 until Start().
        _targetGain = Math.Clamp(gain, 0.0, 1.0);

        // Spatial gain sits HERE: after the volume stage and before the meter.
        //
        //   after the probe  -> the probe above still measures the signal BEFORE any gain, so an
        //                       intentionally silent channel still reports whether audio is flowing;
        //   before the meter -> the level the UI shows is what is actually played.
        //
        // Only for stereo. Skipped otherwise, see the field remarks.
        ISampleProvider beforeMeter = _volume;

        if (_chainChannels == 2)
        {
            _spatial = new StereoSpatialGainProvider(_volume);
            beforeMeter = _spatial;

            // A SECOND probe, downstream of the gain stages.
            //
            // The first probe deliberately sits BEFORE them, so that an intentionally silenced channel still
            // reports whether audio is flowing. That is precisely why it cannot show what the spatial gain
            // did, and measuring the pan needs a tap after it.
            _outputProbe = new SignalProbe(beforeMeter);
            beforeMeter = _outputProbe;
        }

        _meter = new MeteringSampleProvider(beforeMeter);

        // Fan the meter out to our own event so subscribers cannot be leaked by an empty
        // remove accessor, and so the meter's own event signature stays internal.
        _meter.StreamVolume += (_, e) =>
            _levelChanged?.Invoke(this, e.MaxSampleValues.Length > 0 ? e.MaxSampleValues[0] : 0f);

        _player = InitializePlayer(device, engineLatencyMs, new SampleToWaveProvider(_meter), out string? note);
        InitializationNote = note;

        AverageLatencyMs = _player is IWaveLatency latency ? latency.AverageLatency.TotalMilliseconds : 0.0;
    }

    /// <summary>Stable device key from the profile.</summary>
    public string DeviceKey { get; }

    /// <summary>The endpoint's negotiated mix format.</summary>
    public WaveFormat DeviceMixFormat { get; }

    /// <summary>Whether this device can be placed in space, i.e. it is a stereo output.</summary>
    public bool SupportsSpatialGain => _spatial is not null;

    /// <summary>
    /// Applies the per-channel gains for this device's position.
    /// </summary>
    /// <remarks>
    /// A no-op on a device that is not stereo, so callers can hand every channel the same placement without
    /// having to ask first. That is deliberate: a caller that has to remember which devices can be panned
    /// will eventually forget.
    /// </remarks>
    public void SetSpatialGains(double left, double right) => _spatial?.SetGains(left, right);

    /// <summary>Peak of the left channel, or null when this device is not stereo.</summary>
    /// <remarks>
    /// Exposed so a test can tell a correctly panned device from one panned the wrong way round. The
    /// aggregate level cannot: both directions produce the same total.
    /// </remarks>
    public double? LeftPeak => _outputProbe?.LeftPeak;

    /// <summary>Peak of the right channel, or null when this device is not stereo.</summary>
    public double? RightPeak => _outputProbe?.RightPeak;

    /// <summary>Non-null when the player could not be built exactly as requested (e.g. raw mode unavailable).</summary>
    public string? InitializationNote { get; }

    /// <summary>
    /// True when this device's Windows endpoint was muted and the engine unmuted it on start.
    /// </summary>
    /// <remarks>
    /// Surfaced so the UI can tell the user that a device was muted, rather than silently "fixing"
    /// something they may have muted deliberately.
    /// </remarks>
    public bool EndpointUnmutedOnStart { get; }

    /// <summary>Latency the endpoint actually granted, in ms — not what we asked for.</summary>
    public int EngineLatencyGrantedMs => _player.LatencyMilliseconds;

    /// <summary>Device-reported steady-state pipeline latency, in ms.</summary>
    public double AverageLatencyMs { get; private set; }

    /// <summary>Fault recorded by the fan-out path, if any. One sick channel must not unwind the capture callback.</summary>
    public Exception? Fault => _fault;

    /// <summary>Raised when this channel should be torn down and rebuilt (device invalidated, playback failed).</summary>
    public event EventHandler<Exception?>? Faulted;

    /// <summary>
    /// Pushes captured bytes into this channel's ring. Called from the capture thread for every
    /// channel in turn, so it must stay allocation-free and must not block.
    /// </summary>
    public void AddSamples(ReadOnlySpan<byte> buffer)
    {
        if (_fault is not null)
        {
            return;
        }

        _ring.AddSamples(buffer);
    }

    /// <summary>Records a fan-out fault without letting it escape into the capture callback.</summary>
    public void ReportFault(Exception exception)
    {
        _fault ??= exception;
        Faulted?.Invoke(this, exception);
    }

    /// <summary>Starts playback on this channel, ramping in from silence.</summary>
    /// <remarks>
    /// The channel begins at 0 gain and ramps to its configured level so a mirror never starts with a
    /// sudden burst. That matters most when several devices start together, and for Bluetooth
    /// speakers that are already turned up.
    /// </remarks>
    public void Start()
    {
        _uptime.Restart();

        _rampAborted = false;
        _startupRampActive = true;
        _volume.Volume = 0f;

        _player.Play();

        _ = RampUpToTargetAsync();
    }

    /// <summary>Ramps the chain gain from silence to the configured level.</summary>
    private async Task RampUpToTargetAsync()
    {
        // ~25 ms per step: smooth to the ear, few enough that scheduling overhead does not dominate.
        int steps = Math.Clamp((int)(EngineTunables.StartupRampMs / 25.0), 4, 200);
        TimeSpan stepDelay = TimeSpan.FromMilliseconds(EngineTunables.StartupRampMs) / steps;

        try
        {
            for (int step = 1; step <= steps; step++)
            {
                if (_rampAborted)
                {
                    // Teardown took over the gain; stop fighting it.
                    return;
                }

                _volume.Volume = (float)(_targetGain * step / steps);
                await Task.Delay(stepDelay).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // A failed ramp must never leave the channel silent or take the engine down.
        }
        finally
        {
            if (!_rampAborted)
            {
                _startupRampActive = false;
                _volume.Volume = (float)_targetGain;
            }
        }
    }

    /// <summary>
    /// Runs one drift control step. Called by the engine at
    /// <see cref="EngineTunables.ControlTickHz"/> — never from a render callback.
    /// </summary>
    public void UpdateDrift()
    {
        double elapsed = _uptime.Elapsed.TotalSeconds;
        double fillSeconds = _ring.BufferedDuration.TotalSeconds;

        // Sample the window statistics FIRST, before any early return.
        //
        // Diagnostics must be live during warm-up too: "the channel is warming up" and "the
        // channel is producing nothing" are exactly the cases an operator needs to tell apart,
        // and returning early here would leave the panel blank for the first seconds.
        _lastFillMinMs = _ringDiagnostics.FillMinSeconds * 1000.0;
        _lastFillMaxMs = _ringDiagnostics.FillMaxSeconds * 1000.0;
        _lastFillMeanMs = _ringDiagnostics.FillMeanSeconds * 1000.0;
        _ringDiagnostics.ResetWindowStatistics();

        if (!_drift.ObserveWarmUp(elapsed))
        {
            // The fill of a just-started client is meaningless; make no compensation decisions.
            return;
        }

        if (!_settledAfterWarmUp)
        {
            _settledAfterWarmUp = true;

            // SETTLE ONCE, to the target, at the end of warm-up.
            //
            // The ring is pre-filled to survive the player's first WASAPI pull, and that pull is approximately one
            // engine latency long but not exactly, so where the fill LANDS is not knowable in advance. Measured on
            // real hardware: one Bluetooth device landed at 124.5 ms against a 105 ms target while the others landed
            // between 95 and 105.
            //
            // Nothing else brings a surplus down. The controller's authority is +-400 ppm, so removing 20 ms of
            // excess would take the better part of a minute of saturation -- and it cannot remove it at all, because
            // a proportional controller settles at an error proportional to the mismatch rather than at zero. The
            // trim below only fires above the resync line at 135 ms, which 124.5 does not reach. So each device
            // keeps whatever surplus its first pull happened to leave, and the devices end up tens of milliseconds
            // apart from EACH OTHER, which is the one thing this whole mechanism exists to prevent.
            fillSeconds = TrimToTarget(fillSeconds);
        }

        if (_drift.ShouldResync(fillSeconds))
        {
            // TRIM THE EXCESS down to the target — do NOT empty the buffer.
            //
            // This used to call ClearBuffer(), and that was a silent-channel bug: ClearBuffer leaves
            // the ring EMPTY, and nothing can refill it. Capture only replaces what playback
            // consumes (both run at real time), and the controller's authority is +-200 ppm, so it
            // would need minutes to rebuild 105 ms. One resync therefore muted the channel
            // PERMANENTLY, and because the trigger raced against the player's first pull, it
            // happened intermittently — exactly the reported "sometimes it just doesn't make sound".
            //
            // Dropping only the surplus keeps the channel alive and, as a bonus, converges to the
            // target immediately instead of walking there at 0.02 %.
            fillSeconds = TrimToTarget(fillSeconds);

            // Deliberately NO _drift.Reset() / warm-up restart here: the ring was not emptied, so
            // the controller's state is still valid. Observe() below simply sees the trimmed fill.
        }

        _drift.Observe(fillSeconds);
        _resampler.SetCorrection(_drift.Tick());
    }

    /// <summary>
    /// Drops whatever the ring holds beyond the target depth, and reports the fill afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One place for the only two callers, because they are the same action: drop buffered audio until the target
    /// is reached. The difference is only in WHEN — once at the end of warm-up, and again whenever the backlog runs
    /// away past the resync line.
    /// </para>
    /// <para>
    /// Counted as a resync either way: dropping audio the device was about to play is a discontinuity, and a
    /// counter that hid half of them would be worse than no counter.
    /// </para>
    /// </remarks>
    /// <param name="fillSeconds">The current fill, in seconds.</param>
    /// <returns>The fill after trimming, in seconds.</returns>
    private double TrimToTarget(double fillSeconds)
    {
        double excessSeconds = fillSeconds - _drift.TargetBacklogSeconds;

        if (excessSeconds <= 0)
        {
            return fillSeconds;
        }

        int droppedBytes = TrimExcess(excessSeconds);

        Interlocked.Increment(ref _resyncCount);

        return fillSeconds - (droppedBytes / (double)_ring.WaveFormat.AverageBytesPerSecond);
    }

    /// <summary>
    /// Discards the oldest buffered audio, down to at most <paramref name="excessSeconds"/> worth.
    /// </summary>
    /// <returns>The number of bytes actually dropped.</returns>
    /// <remarks>
    /// Dropping audio is a real discontinuity, which is why it is confined to the "backlog has run
    /// away" case and bounded by the surplus. It is still strictly better than the alternative it
    /// replaces, because emptying the buffer silences the channel outright.
    /// </remarks>
    private int TrimExcess(double excessSeconds)
    {
        WaveFormat format = _ring.WaveFormat;
        int bytesPerSecond = format.AverageBytesPerSecond;

        int target = (int)(excessSeconds * bytesPerSecond);
        target -= target % format.BlockAlign;

        int dropped = 0;

        while (dropped < target)
        {
            int chunk = Math.Min(_trimScratch.Length, target - dropped);
            chunk -= chunk % format.BlockAlign;

            if (chunk <= 0)
            {
                break;
            }

            // NAudio 3.x exposes Read(Span<byte>) rather than the old (byte[], int, int).
            int read = _ring.Read(_trimScratch.AsSpan(0, chunk));
            if (read <= 0)
            {
                break;
            }

            dropped += read;
        }

        return dropped;
    }

    /// <summary>Reads this device's endpoint volume/mute state, and unmutes it if it was muted.</summary>
    /// <remarks>
    /// Exposed so a caller can re-check after start-up (a user can mute a device at any time) and so
    /// the UI can warn instead of leaving the user staring at a silent speaker.
    /// </remarks>
    public (double VolumeScalar, bool Muted) ReadEndpointState(MMDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        try
        {
            AudioEndpointVolume endpointVolume = device.AudioEndpointVolume;

            try
            {
                return (endpointVolume.MasterVolumeLevelScalar, endpointVolume.Mute);
            }
            finally
            {
                endpointVolume.Dispose();
            }
        }
        catch (Exception)
        {
            return (double.NaN, false);
        }
    }

    /// <summary>Sets per-device gain. Uses the chain, NOT the endpoint volume.</summary>
    /// <remarks>
    /// While the start-up ramp is running the value is recorded as the new TARGET and applied when the
    /// ramp reaches it, so dragging a slider during the first seconds does not cause a jump.
    /// <para>
    /// Writing <c>AudioEndpointVolume.MasterVolumeLevelScalar</c> here instead would change the volume
    /// for every application on that endpoint — see docs/PITFALLS.md A6.
    /// </para>
    /// </remarks>
    public void SetGain(double gain)
    {
        _targetGain = Math.Clamp(gain, 0.0, 1.0);

        if (!_startupRampActive)
        {
            _volume.Volume = (float)_targetGain;
        }
    }

    /// <summary>
    /// The gain currently applied in the chain, 0..1.
    /// </summary>
    /// <remarks>
    /// Read back from the <c>VolumeSampleProvider</c> that is actually in the signal path, so it can
    /// be asserted in tests. The volume slider previously updated only the view model and never
    /// reached the audio — which is indistinguishable from "the slider does nothing", so there has
    /// to be a way to prove the value landed.
    /// </remarks>
    public double CurrentGain => _volume.Volume;

    /// <summary>The gain this channel is ramping toward or holding, 0..1.</summary>
    public double TargetGain => _targetGain;

    /// <summary>
    /// Applies a compensation delay.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the change was small enough to glide. When <c>false</c> the caller must
    /// mute around the change: a large step (a new measurement adding ~170 ms, or a Bluetooth
    /// device dropping out) must not be slewed, because at the 0.5 % pitch budget 170 ms would
    /// take 34 seconds of audibly sliding audio. See docs/PITFALLS.md B9.
    /// </returns>
    /// <remarks>
    /// Use this only BEFORE playback starts, where a step is inaudible. Once the channel is
    /// running, use <see cref="ApplyDelayAsync"/>, which fades around large changes.
    /// </remarks>
    public bool ApplyDelayMs(double delayMs)
    {
        _userDelayMs = delayMs;
        _delay.SetDelayMs(TotalDelayMs, out bool requiresMuteResync);
        return !requiresMuteResync;
    }

    /// <summary>
    /// Sets the extra delay implied by this device's position, on top of whatever the user asked for.
    /// </summary>
    /// <remarks>
    /// Applied immediately and without a fade. A position change is a deliberate configuration act, it is
    /// small in practice (a metre is 2.9 ms), and routing it through the glide path would make the control
    /// appear dead for tens of seconds — the exact fault that made manual delay changes look broken before.
    /// </remarks>
    public void SetSpatialDelayMs(double delayMs)
    {
        double clamped = Math.Max(0.0, delayMs);

        if (Math.Abs(clamped - _spatialDelayMs) < 0.01)
        {
            return;
        }

        _spatialDelayMs = clamped;
        _delay.SetDelayMs(TotalDelayMs, out _);
    }

    /// <summary>The distance delay currently added for this device's position, in ms.</summary>
    public double SpatialDelayMs => _spatialDelayMs;

    /// <summary>
    /// Applies a delay the USER asked for: always lands immediately, behind a short fade.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ApplyDelayAsync"/>, which may glide. A glided change is inaudible but
    /// SLOW by design (about 5 ms per second), so a slider routed through it appears to do nothing.
    /// This path fades out, jumps, and fades back — 20 ms of fade is far less noticeable than a delay
    /// control that takes half a minute to respond.
    /// </remarks>
    public async Task ApplyUserDelayAsync(double delayMs, TimeSpan fade)
    {
        _userDelayMs = delayMs;
        // Stop any in-flight start-up ramp so it cannot fight this fade over the gain.
        _rampAborted = true;
        _startupRampActive = false;

        float original = _volume.Volume;

        await FadeToAsync(0f, fade).ConfigureAwait(false);
        _delay.SetDelayMs(TotalDelayMs, out _, immediate: true);
        await FadeToAsync(original, fade).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a compensation delay on a RUNNING channel, fading around large changes.
    /// </summary>
    /// <remarks>
    /// The fade decision must be made BEFORE the change lands: <c>SetDelayMs</c> steps the read
    /// pointer immediately for a large delta, and that step IS the click — discovering the need to
    /// mute afterwards would be too late. Hence the pre-check.
    /// </remarks>
    public async Task ApplyDelayAsync(double delayMs, TimeSpan fade)
    {
        _userDelayMs = delayMs;
        if (!_delay.WouldRequireMuteResync(TotalDelayMs))
        {
            _delay.SetDelayMs(TotalDelayMs, out _);
            return;
        }

        float original = _volume.Volume;

        await FadeToAsync(0f, fade).ConfigureAwait(false);
        _delay.SetDelayMs(TotalDelayMs, out _);
        await FadeToAsync(original, fade).ConfigureAwait(false);
    }

    /// <summary>The delay actually being applied, in ms — this is what the device gets.</summary>
    public double AppliedDelayMs => _delay.AppliedDelayMs;

    /// <summary>Snapshot of this channel's diagnostics.</summary>
    public ChannelDiagnostics GetDiagnostics() => new(
        DeviceKey,
        _lastFillMinMs,
        _lastFillMaxMs,
        _lastFillMeanMs,
        _drift.TargetBacklogSeconds * 1000.0,
        _resampler.CorrectionPpm,
        _ringDiagnostics.StarvedReads,
        _ringDiagnostics.OverflowDrops,
        Interlocked.Read(ref _resyncCount),
        _delay.AppliedDelayMs,
        AverageLatencyMs,
        _player is IWaveLatency latency ? latency.CurrentLatency.TotalMilliseconds : 0.0,
        EngineLatencyGrantedMs,
        _ringDiagnostics.PartialStarvedReads,
        _ringDiagnostics.WorstSilenceFraction,
        _signalProbe.Peak,
        _signalProbe.Rms,
        _signalProbe.SilentBlockFraction,
        _endpointVolumeScalar,
        _endpointMuted,
        _captureSampleRate,
        DeviceMixFormat.SampleRate,
        _captureChannels,
        _chainChannels);

    /// <summary>Fades the chain gain to zero so stopping does not produce a click.</summary>
    public Task FadeOutAsync(TimeSpan fade)
    {
        // Stop any in-flight start-up ramp first, so the two do not fight over the gain.
        _rampAborted = true;
        _startupRampActive = false;
        return FadeToAsync(0f, fade);
    }

    /// <summary>Ramps the chain gain to a target over the given duration.</summary>
    private async Task FadeToAsync(float target, TimeSpan fade)
    {
        // ~25 ms per step: enough resolution to be smooth, few enough that a very short fade is not
        // dominated by Task.Delay scheduling overhead.
        int steps = Math.Clamp((int)(fade.TotalMilliseconds / 25.0), 4, 200);
        TimeSpan stepDelay = fade / steps;
        float start = _volume.Volume;

        for (int step = 1; step <= steps; step++)
        {
            _volume.Volume = start + ((target - start) * step / steps);
            await Task.Delay(stepDelay).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Level updates for the UI meter, as peak linear amplitude per block.
    /// </summary>
    /// <remarks>
    /// Raised on the PLAYBACK thread. The UI must marshal to the dispatcher; do not bind
    /// directly, and do not do work in the handler.
    /// </remarks>
    public event EventHandler<float>? LevelChanged
    {
        add => _levelChanged += value;
        remove => _levelChanged -= value;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Prefer DisposeAsync so the calling (UI) thread is not blocked while the playback
        // thread is joined.
        await _player.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Builds and initialises the player, falling back once if raw mode is unavailable.
    /// </summary>
    /// <remarks>
    /// Raw mode bypasses the endpoint's "audio enhancements" APO — worth having, because those
    /// effects can downmix stereo content toward mono. It needs <c>IAudioClient2</c> and throws
    /// on <c>Init</c> (not on <c>Build</c>) when unsupported, so the fallback has to wrap Init.
    /// </remarks>
    private static WasapiPlayer InitializePlayer(
        MMDevice device,
        int engineLatencyMs,
        IWaveProvider source,
        out string? note)
    {
        note = null;

        // Built into a local first, so the raw-mode attempt can be DISPOSED if Init rejects it.
        // Without this the failed attempt leaks its AudioClient on the endpoint — and the raw-mode
        // path is precisely the one Bluetooth endpoints take, because they frequently lack the
        // IAudioClient2 support raw mode needs. A leaked client on the endpoint is exactly the
        // kind of thing that makes a device look connected but stay silent.
        WasapiPlayer? attempt = null;

        try
        {
            attempt = new WasapiPlayerBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithEventSync()
                .WithLatency(engineLatencyMs)
                .WithRawMode()
                .WithCategory(AudioStreamCategory.Media)
                .WithMmcssThreadPriority("Pro Audio")
                .Build();

            attempt.Init(source);
            return attempt;
        }
        catch (Exception ex)
        {
            // Raw mode unsupported on this endpoint. Retry once without it rather than failing
            // the channel — but tell the caller, so the difference is visible in diagnostics.
            //
            // This catches Exception, not InvalidOperationException. It used to name the latter, and that
            // made the fallback dead code for the most common case: endpoints refuse raw mode by failing a
            // COM call, and NAudio surfaces that as CoreAudioException, which derives from COMException and
            // therefore never matched. VB-CABLE's render endpoint rejects raw mode this way, so the retry
            // that exists for exactly this situation never ran and the channel simply failed to open.
            //
            // Catching broadly is safe here because the retry is cheap and the second attempt is the
            // authoritative one: if the endpoint is genuinely broken, the non-raw attempt fails too and that
            // error is what propagates.
            note = $"Raw mode unavailable ({ex.Message}); continuing without it.";

            if (attempt is not null)
            {
                try
                {
                    attempt.Dispose();
                }
                catch (Exception)
                {
                    // Already dead; nothing to release.
                }
            }
        }

        WasapiPlayer fallback = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(engineLatencyMs)
            .WithCategory(AudioStreamCategory.Media)
            .WithMmcssThreadPriority("Pro Audio")
            .Build();

        fallback.Init(source);
        return fallback;
    }

    /// <summary>Matches the channel count between the capture format and the device mix format.</summary>
    private static ISampleProvider MatchChannels(ISampleProvider source, WaveFormat deviceMixFormat)
    {
        if (source.WaveFormat.Channels == 1 && deviceMixFormat.Channels >= 2)
        {
            return new MonoToStereoSampleProvider(source);
        }

        if (source.WaveFormat.Channels == 2 && deviceMixFormat.Channels == 1)
        {
            return new StereoToMonoSampleProvider(source);
        }

        // Multi-channel device formats (5.1/7.1) are intentionally NOT remapped in v0.1:
        // the capture is stereo and the engine's downmix is left to the shared-mode mixer.
        // Downgrading that to a silent guess would be worse than saying so.
        return source;
    }
}
