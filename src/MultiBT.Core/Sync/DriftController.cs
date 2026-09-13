using MultiBT.Core.Audio;

namespace MultiBT.Core.Sync;

/// <summary>
/// The clock-drift controller: one instance per output channel, steers the drift ring
/// buffer's trough toward a fixed target by nudging a resampler ratio.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Independent audio clocks diverge. At 50 ppm (the Bluetooth Core
/// Specification's active-clock limit) two devices drift 15 ms in 5 minutes, 90 ms in 30
/// minutes and 180 ms in an hour; at 100 ppm it is 30/180/360 ms. Since 20–40 ms between two
/// correlated sources is already clearly audible, an uncompensated mirror is correct for
/// about five minutes and wrong forever after. See docs/SPEC.md §6.1.
/// </para>
/// <para>
/// <b>Why P-only, with no integral term.</b> The plant is an integrator: with fill f, control
/// c and unknown drift d, <c>df/dt = c − d</c>. Choosing <c>c = K·e</c> gives
/// <c>dE/dt = K·E − d</c>, so the error converges to <c>E_ss = d/K</c> — a proportional
/// controller on an integrating plant already has zero steady-state RATE error. With
/// K = 0.5/s and d = 100 ppm, E_ss = 0.2 ms: 15x below the audible threshold, and constant,
/// so it never accumulates. Adding an integral term would make the loop a double integrator
/// and manufacture a limit cycle. See docs/PITFALLS.md B7.
/// </para>
/// <para>
/// <b>Why the trough and not the mean.</b> A wireless endpoint can deliver audio in ~60 ms
/// bursts, so the backlog saw-tooths. Steering the mean drags the trough below one render
/// pull and starves the buffer into audible crackle; steering the window minimum keeps the
/// low point safe whatever the burst size. See docs/PITFALLS.md B8.
/// </para>
/// <para>
/// This class is deliberately free of any audio or COM dependency so the control law can be
/// unit-tested against a simulated drifting plant (see DriftControllerTests).
/// </para>
/// </remarks>
public sealed class DriftController
{
    private readonly double _targetBacklogSeconds;
    private readonly double _maxBacklogSeconds;
    private double _windowMinSeconds = double.PositiveInfinity;
    private double _smoothedTroughSeconds;
    private double _correction;
    private bool _primed;
    private bool _warmedUp;
    private double _warmUpUntilSeconds;

    /// <param name="engineLatencyMs">
    /// The requested engine latency for this channel. Must be the SAME for every channel:
    /// if per-channel targets differ, the difference is injected as a static misalignment.
    /// See docs/PITFALLS.md B11.
    /// </param>
    public DriftController(double engineLatencyMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(engineLatencyMs);

        _targetBacklogSeconds = (engineLatencyMs + EngineTunables.TargetBacklogMarginMs) / 1000.0;
        _maxBacklogSeconds = (engineLatencyMs + EngineTunables.ResyncMarginMs) / 1000.0;

        // Warm-up is armed from t=0; a channel that resyncs re-arms it from that moment so the
        // freshly cleared buffer is given the same settling time as a fresh start.
        _warmUpUntilSeconds = EngineTunables.WarmUpSeconds;
    }

    /// <summary>Current dimensionless correction ratio, e.g. 42e-6 for +42 ppm.</summary>
    public double Correction => _correction;

    /// <summary>Current correction in parts per million, for diagnostics and the UI.</summary>
    public double CorrectionPpm => _correction * 1e6;

    /// <summary>The EMA-smoothed trough the controller is acting on, in seconds.</summary>
    public double SmoothedTroughSeconds => _smoothedTroughSeconds;

    /// <summary>The trough target <c>f* = engineLatencyMs + TargetBacklogMarginMs</c>, in seconds.</summary>
    public double TargetBacklogSeconds => _targetBacklogSeconds;

    /// <summary>Backlog above which the channel should resync instead of correcting smoothly, in seconds.</summary>
    public double MaxBacklogSeconds => _maxBacklogSeconds;

    /// <summary>True once the warm-up period has elapsed and corrections are being applied.</summary>
    public bool IsWarmedUp => _warmedUp;

    /// <summary>
    /// Feeds one fill observation. Call this at render rate — cheap, it is only a min().
    /// </summary>
    /// <param name="fillSeconds">
    /// Current ring fill in seconds (writer frames minus reader frames, divided by the sample
    /// rate). Note this is the DRIFT buffer only; the compensation delay line is a separate
    /// buffer and must never be folded into this number. See docs/PITFALLS.md B1.
    /// </param>
    public void Observe(double fillSeconds)
    {
        if (fillSeconds < _windowMinSeconds)
        {
            _windowMinSeconds = fillSeconds;
        }
    }

    /// <summary>
    /// Advances the warm-up clock. Until this returns true the controller holds its
    /// correction at zero, because a just-started render client's fill is meaningless.
    /// </summary>
    /// <param name="elapsedSeconds">Seconds since the channel started.</param>
    public bool ObserveWarmUp(double elapsedSeconds)
    {
        if (!_warmedUp && elapsedSeconds >= _warmUpUntilSeconds)
        {
            _warmedUp = true;
        }

        return _warmedUp;
    }

    /// <summary>
    /// Re-arms the warm-up from the given clock position.
    /// </summary>
    /// <remarks>
    /// Called after a buffer resync. Without this, <see cref="Reset"/> would clear the warm-up
    /// flag while the deadline stayed in the past, so the very next tick would immediately
    /// resume correcting against a buffer that had just been emptied — which is exactly the
    /// state warm-up exists to avoid.
    /// </remarks>
    public void RestartWarmUp(double nowSeconds)
    {
        _warmUpUntilSeconds = nowSeconds + EngineTunables.WarmUpSeconds;
        _warmedUp = false;
    }

    /// <summary>
    /// Whether the controller corrects at all.
    /// </summary>
    /// <remarks>
    /// On everywhere except the self-test, which turns it off to measure the chain's RAW rate balance: with no
    /// correction applied, a ring's fill drifts at the true rate error, and that number is what tells a real clock
    /// difference apart from a bug in our own rate handling. A correction pinned at its ceiling cannot make that
    /// distinction, because a proportional controller saturates for any error above a fraction of a millisecond.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Runs one control tick and returns the new correction ratio to apply to the resampler.
    /// Call this at a FIXED <see cref="EngineTunables.ControlTickHz"/> — never from the render
    /// callback, which reintroduces per-callback ratio jitter.
    /// </summary>
    /// <returns>The dimensionless correction to pass to the per-channel resampler.</returns>
    public double Tick()
    {
        if (!Enabled)
        {
            return 0.0;
        }

        double trough = _windowMinSeconds;
        _windowMinSeconds = double.PositiveInfinity;   // reset the window

        if (double.IsPositiveInfinity(trough))
        {
            return _correction;   // no observations this window; hold
        }

        if (!_primed)
        {
            // Seed the EMA with the first real observation rather than ramping up from 0,
            // which would otherwise look like a large error on a fully drained buffer.
            _smoothedTroughSeconds = trough;
            _primed = true;
        }
        else
        {
            _smoothedTroughSeconds += EngineTunables.TroughSmoothingAlpha * (trough - _smoothedTroughSeconds);
        }

        if (!_warmedUp)
        {
            return _correction;
        }

        double error = _smoothedTroughSeconds - _targetBacklogSeconds;

        double correction = Math.Clamp(
            EngineTunables.PGain * error,
            -EngineTunables.MaxCorrection,
            EngineTunables.MaxCorrection);

        // Rate-limit so the correction moves smoothly: a fast-moving correction is frequency
        // modulation, and FM is detectable well below the static pitch JND. See docs/PITFALLS.md B10.
        correction = Math.Clamp(
            correction,
            _correction - EngineTunables.MaxCorrectionRatePerTick,
            _correction + EngineTunables.MaxCorrectionRatePerTick);

        _correction = correction;
        return correction;
    }

    /// <summary>
    /// Whether the backlog has grown past the resync margin, meaning the channel should
    /// stop correcting smoothly and clear its buffer instead.
    /// </summary>
    public bool ShouldResync(double fillSeconds) => fillSeconds > _maxBacklogSeconds;

    /// <summary>Resets controller state. Call after a buffer clear or a channel restart.</summary>
    public void Reset()
    {
        _windowMinSeconds = double.PositiveInfinity;
        _smoothedTroughSeconds = 0.0;
        _correction = 0.0;
        _primed = false;
        _warmedUp = false;
    }
}
