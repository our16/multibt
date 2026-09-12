using NAudio.Wave;

namespace MultiBT.Core.Audio;

/// <summary>
/// Counts the two failure modes NAudio hides by default.
/// </summary>
/// <remarks>
/// <para>
/// Both of these are invisible without instrumentation, which is why diagnostics are a Tier 1
/// requirement rather than a nice-to-have. See docs/PITFALLS.md A8.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Starvation.</b> With <c>ReadFully = true</c> (the default, and required here), a
/// starving read is zero-filled and STILL reports a full read. Starvation is therefore
/// indistinguishable from genuine silence unless you observe the fill across the read.
/// </description></item>
/// <item><description>
/// <b>Overflow drops.</b> With <c>DiscardOnBufferOverflow = true</c>, <c>AddSamples</c> writes
/// what fits and SILENTLY DROPS THE NEWEST samples — no exception, no counter. This engine
/// therefore keeps it <c>false</c> and resyncs explicitly on a bounded backlog instead.
/// </description></item>
/// </list>
/// <para>
/// This provider sits immediately downstream of the ring buffer so it sees every read the
/// chain makes, and samples the ring's fill on both sides of it.
/// </para>
/// </remarks>
public sealed class RingDiagnostics : ISampleProvider
{
    private readonly BufferedWaveProvider _ring;
    private readonly ISampleProvider _inner;
    private long _starvedReads;
    private long _partialStarvedReads;
    private long _totalReads;
    private long _worstShortfallMs;
    private long _worstRequestedMs;
    private int _windowReads;
    private long _overflowDrops;
    private double _fillMinSeconds = double.PositiveInfinity;
    private double _fillMaxSeconds;
    private double _fillSumSeconds;

    /// <param name="ring">The drift ring buffer being observed.</param>
    /// <param name="inner">The sample-provider view of that same ring.</param>
    public RingDiagnostics(BufferedWaveProvider ring, ISampleProvider inner)
    {
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc />
    public WaveFormat WaveFormat => _inner.WaveFormat;

    /// <summary>Reads that began with an empty ring — i.e. silence that was not real silence.</summary>
    public long StarvedReads => Interlocked.Read(ref _starvedReads);

    /// <summary>
    /// Reads where the ring held LESS data than the read asked for, so the remainder was zero-filled.
    /// </summary>
    /// <remarks>
    /// <b>This is the counter that actually detects a silent channel.</b> Counting only fully-empty
    /// reads is not enough, and that is a trap: with <c>ReadFully = true</c> a player that asks for
    /// 100 ms in one pull, when the ring holds 0.5 ms, gets 0.5 ms of real audio followed by 99.5 %
    /// zeros — returned as a full, successful read. The result is an effectively SILENT output with
    /// no exception, no short read, and a "starved" counter that stays near zero.
    /// </remarks>
    public long PartialStarvedReads => Interlocked.Read(ref _partialStarvedReads);

    /// <summary>Largest observed shortfall between what a read asked for and what the ring held, in ms.</summary>
    public double WorstShortfallMs => Interlocked.Read(ref _worstShortfallMs);

    /// <summary>
    /// Fraction of the requested audio that had to be zero-filled in the worst single read, 0..1.
    /// </summary>
    /// <remarks>
    /// A value approaching 1.0 means the channel is playing essentially all silence: the read asked
    /// for far more than the ring held and the WAVE provider filled the rest with zeros.
    /// </remarks>
    public double WorstSilenceFraction
    {
        get
        {
            long requested = Interlocked.Read(ref _worstRequestedMs);
            return requested <= 0 ? 0.0 : (double)Interlocked.Read(ref _worstShortfallMs) / requested;
        }
    }

    /// <summary>
    /// Total reads observed since construction. Cumulative — it must NOT reset per control
    /// window, or a channel that stopped reading entirely would look identical to a healthy one.
    /// </summary>
    public long Reads => Interlocked.Read(ref _totalReads);

    /// <summary>Samples dropped because the ring overflowed. Zero while overflow dropping is disabled.</summary>
    public long OverflowDrops => Interlocked.Read(ref _overflowDrops);

    /// <summary>
    /// Minimum fill observed in the current window, in seconds, or <see cref="double.NaN"/> when
    /// no read occurred in the window.
    /// </summary>
    /// <remarks>
    /// NaN rather than 0 is important: reporting 0 for "no data" would make an idle window look
    /// identical to a fully starved one and raise false starvation alarms.
    /// </remarks>
    public double FillMinSeconds => _windowReads == 0 ? double.NaN : _fillMinSeconds;

    /// <summary>Maximum fill observed in the current window, in seconds, or NaN when idle.</summary>
    public double FillMaxSeconds => _windowReads == 0 ? double.NaN : _fillMaxSeconds;

    /// <summary>Mean fill observed in the current window, in seconds, or NaN when idle.</summary>
    public double FillMeanSeconds => _windowReads == 0 ? double.NaN : _fillSumSeconds / _windowReads;

    /// <inheritdoc />
    public int Read(Span<float> buffer)
    {
        double before = _ring.BufferedDuration.TotalSeconds;

        int read = _inner.Read(buffer);

        if (before <= 0.0 && read > 0)
        {
            Interlocked.Increment(ref _starvedReads);
        }

        // Detect PARTIAL starvation, which is what actually makes a channel silent.
        //
        // The read asks for `buffer.Length` float samples; the ring can only supply
        // `before` seconds' worth. Anything beyond that is zero-filled by ReadFully and reported as
        // a successful, full read — so this comparison is the only place the shortfall is visible.
        int bytesPerSecond = _ring.WaveFormat.AverageBytesPerSecond;
        if (bytesPerSecond > 0 && buffer.Length > 0)
        {
            double requestedMs = buffer.Length * 4.0 / bytesPerSecond * 1000.0;
            double availableMs = before * 1000.0;
            double shortfallMs = requestedMs - availableMs;

            if (shortfallMs > 0.5)
            {
                Interlocked.Increment(ref _partialStarvedReads);

                if (Interlocked.Read(ref _worstShortfallMs) < (long)shortfallMs)
                {
                    Interlocked.Exchange(ref _worstShortfallMs, (long)shortfallMs);
                    Interlocked.Exchange(ref _worstRequestedMs, (long)requestedMs);
                }
            }
        }

        // Track fill statistics for the UI and for the drift controller's trough.
        double after = _ring.BufferedDuration.TotalSeconds;
        if (after < _fillMinSeconds)
        {
            _fillMinSeconds = after;
        }

        if (after > _fillMaxSeconds)
        {
            _fillMaxSeconds = after;
        }

        _fillSumSeconds += after;
        _windowReads++;
        Interlocked.Increment(ref _totalReads);

        return read;
    }

    /// <summary>Notices samples the ring refused, if overflow dropping were ever enabled.</summary>
    public void NoteOverflowDrop(long count) => Interlocked.Add(ref _overflowDrops, count);

    /// <summary>Clears the per-window statistics. Call at the control tick after sampling them.</summary>
    public void ResetWindowStatistics()
    {
        _fillMinSeconds = double.PositiveInfinity;
        _fillMaxSeconds = 0.0;
        _fillSumSeconds = 0.0;
        _windowReads = 0;
    }
}

/// <summary>Point-in-time diagnostics for one output channel, for the diagnostics panel.</summary>
/// <param name="DeviceKey">Channel identity.</param>
/// <param name="FillMinMs">Minimum ring fill in the window, ms. NaN means no read occurred, which is NOT starvation.</param>
/// <param name="FillMaxMs">Maximum ring fill in the window, ms. NaN means no read occurred.</param>
/// <param name="FillMeanMs">Mean ring fill in the window, ms. NaN means no read occurred.</param>
/// <param name="TargetBacklogMs">The controlled target <c>f*</c>, ms.</param>
/// <param name="CorrectionPpm">Resampler correction currently applied, ppm.</param>
/// <param name="StarvedReads">Cumulative starved reads.</param>
/// <param name="OverflowDrops">Cumulative dropped samples.</param>
/// <param name="ResyncCount">Cumulative buffer resyncs.</param>
/// <param name="AppliedDelayMs">Compensation delay actually being applied, ms.</param>
/// <param name="AverageLatencyMs">Device-reported steady-state pipeline latency, ms.</param>
/// <param name="CurrentLatencyMs">Device-reported live pipeline latency, ms.</param>
/// <param name="EngineLatencyMs">Latency actually granted by the endpoint after Init, ms.</param>
/// <param name="PartialStarvedReads">Reads that had to be partially zero-filled. The real silence detector.</param>
/// <param name="WorstSilenceFraction">Worst fraction of a read that was zero-filled, 0..1.</param>
/// <param name="SignalPeak">Peak sample level reaching the player, 0..1, measured BEFORE the per-device gain.</param>
/// <param name="SignalRms">RMS level reaching the player, 0..1.</param>
/// <param name="SignalSilentFraction">Fraction of output blocks that were entirely silent, 0..1.</param>
/// <param name="EndpointVolumeScalar">Device's own Windows volume, 0..1, or NaN when unavailable. NOT our chain gain.</param>
/// <param name="EndpointMuted">Whether the Windows endpoint itself is muted.</param>
/// <param name="CaptureSampleRate">Sample rate of the captured stream (the source device's mix format).</param>
/// <param name="DeviceSampleRate">Sample rate of THIS device's mix format.</param>
/// <param name="CaptureChannels">Channel count of the captured stream.</param>
/// <param name="DeviceChannels">Channel count of this device's mix format.</param>
public sealed record ChannelDiagnostics(
    string DeviceKey,
    double FillMinMs,
    double FillMaxMs,
    double FillMeanMs,
    double TargetBacklogMs,
    double CorrectionPpm,
    long StarvedReads,
    long OverflowDrops,
    long ResyncCount,
    double AppliedDelayMs,
    double AverageLatencyMs,
    double CurrentLatencyMs,
    int EngineLatencyMs,
    long PartialStarvedReads = 0,
    double WorstSilenceFraction = 0.0,
    double SignalPeak = 0.0,
    double SignalRms = 0.0,
    double SignalSilentFraction = 0.0,
    double EndpointVolumeScalar = double.NaN,
    bool EndpointMuted = false,
    int CaptureSampleRate = 0,
    int DeviceSampleRate = 0,
    int CaptureChannels = 0,
    int DeviceChannels = 0)
{
    /// <summary>Whether any read was observed in the reported window.</summary>
    public bool HasWindowData => !double.IsNaN(FillMinMs);

    /// <summary>
    /// True when the trough collapsed to (or below) zero — i.e. the channel actually starved.
    /// </summary>
    /// <remarks>
    /// Requires window data: an idle window (no reads) reports NaN, and treating that as 0 would
    /// raise a starvation alarm for a channel that simply was not being pulled.
    /// </remarks>
    public bool StarvedInWindow => HasWindowData && FillMinMs <= 0.0;

    /// <summary>True when the correction is pinned at the limit, meaning it is not converging.</summary>
    public bool CorrectionSaturated => Math.Abs(CorrectionPpm) >= EngineTunables.MaxCorrection * 1e6 * 0.99;

    /// <summary>Whether this channel needs a sample-rate conversion between capture and device.</summary>
    /// <remarks>
    /// Worth surfacing because a rate mismatch is the difference between "this device works" and
    /// "this device is silent" when the conversion is wrong — and with many Bluetooth speakers
    /// running at 44.1 kHz while the captured device runs at 48 kHz, that split falls exactly along
    /// the wired/Bluetooth line and looks like a device fault.
    /// </remarks>
    public bool RequiresRateConversion =>
        CaptureSampleRate != 0 && DeviceSampleRate != 0 && CaptureSampleRate != DeviceSampleRate;

    /// <summary>Whether the channel/channel-count differs between capture and device.</summary>
    public bool RequiresChannelConversion =>
        CaptureChannels != 0 && DeviceChannels != 0 && CaptureChannels != DeviceChannels;

    /// <summary>
    /// Whether audio is actually reaching the player.
    /// </summary>
    /// <remarks>
    /// The decisive distinction: <c>false</c> here means the engine is feeding silence (a capture or
    /// conversion problem), while <c>true</c> with no audible output means the DEVICE is not playing
    /// what it is being given (muted endpoint, zero volume, exclusive-mode conflict) — a completely
    /// different fix.
    /// </remarks>
    public bool HasSignal => SignalPeak > 0.0;

    /// <summary>Signal level in dBFS, or null when there is no signal.</summary>
    public double? SignalDbFs => SignalPeak > 0.0 ? 20.0 * Math.Log10(SignalPeak) : null;

    /// <summary>
    /// True when audio is reaching the player but Windows will not make it audible.
    /// </summary>
    /// <remarks>
    /// This is the decisive split for "the device makes no sound". Our samples are provably fine
    /// (<see cref="HasSignal"/>), so the fix is at the endpoint — unmute it or raise its volume —
    /// not in the engine.
    /// </remarks>
    public bool BlockedAtEndpoint =>
        HasSignal && (EndpointMuted || (!double.IsNaN(EndpointVolumeScalar) && EndpointVolumeScalar <= 0.001));

    /// <summary>Compact one-line summary for the diagnostics panel.</summary>
    public string Summarize()
    {
        string fill = HasWindowData
            ? $"fill {FillMinMs:0.#}–{FillMaxMs:0.#} ms"
            : "fill — (no reads)";

        string format = RequiresRateConversion
            ? $"{CaptureSampleRate}→{DeviceSampleRate} Hz"
            : $"{DeviceSampleRate} Hz";

        if (RequiresChannelConversion)
        {
            format += $" {CaptureChannels}ch→{DeviceChannels}ch";
        }

        string signal = SignalPeak > 0.0
            ? $"signal {SignalDbFs:0} dBFS"
            : "signal SILENT";

        string flags = string.Empty;

        if (EndpointMuted)
        {
            // Decisive: our samples are fine, Windows is throwing them away.
            flags += ", ⚠ ENDPOINT MUTED";
        }
        else if (!double.IsNaN(EndpointVolumeScalar) && EndpointVolumeScalar <= 0.001)
        {
            flags += ", ⚠ endpoint volume 0%";
        }
        else if (!double.IsNaN(EndpointVolumeScalar) && EndpointVolumeScalar < 0.05)
        {
            flags += $", endpoint volume {EndpointVolumeScalar * 100:0}%";
        }

        if (PartialStarvedReads > 0 && WorstSilenceFraction > 0.01)
        {
            // The headline feed failure: the channel is playing mostly zeros.
            flags += $", ⚠ {WorstSilenceFraction * 100:0}% of a read was silence";
        }

        if (StarvedInWindow)
        {
            flags += ", ⚠ starved";
        }

        if (CorrectionSaturated)
        {
            // Informational rather than alarming: the controller clamps at ±200 ppm by design, so
            // it reports saturation for a few minutes after start-up while it walks a slightly
            // over-filled ring back down to target. That is the intended, safe convergence
            // direction. It only matters if the fill is ALSO not stable.
            flags += $", correction at limit ({CorrectionPpm:+0;-0;0} ppm)";
        }

        if (ResyncCount > 0)
        {
            flags += $", resync {ResyncCount}";
        }

        return $"{DeviceKey}: {CorrectionPpm:+0;-0;0} ppm, {fill}, {format}, {signal}{flags}";
    }
}
