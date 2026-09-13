namespace MultiBT.Core.Audio;

/// <summary>
/// Every tuning constant for the mirror/sync engine, in one place.
/// </summary>
/// <remarks>
/// <para>
/// These values are not guesses. The trough-based control law and the constants marked
/// "AudioHQ-validated" were derived empirically by the AudioHQ project (MIT) whose
/// changelog documents the failure modes that produced each one — in particular that a
/// wireless endpoint can deliver audio in ~60 ms bursts, so the controller must steer the
/// <b>trough</b> (window minimum) rather than the mean. See docs/SPEC.md §6.3.
/// </para>
/// <para>
/// Do not scatter magic numbers elsewhere. If a value here needs changing, change it here
/// and update docs/SPEC.md §6.3 in the same commit.
/// </para>
/// </remarks>
public static class EngineTunables
{
    // ---------------------------------------------------------------- buffering

    /// <summary>Capacity of each per-output drift ring buffer, in seconds. Ring only — bounded by construction.</summary>
    public const double BufferSeconds = 2.0;

    /// <summary>
    /// Target trough depth ABOVE the requested engine latency.
    /// <c>f* = engineLatencyMs + TargetBacklogMarginMs</c>. AudioHQ-validated (0.2.4):
    /// targeting the trough at <c>latency + 5ms</c> locks exactly on target with only tiny
    /// drift correction, while a lower target underran between WASAPI pulls and crackled.
    /// </summary>
    public const double TargetBacklogMarginMs = 5.0;

    /// <summary>
    /// Backlog above which the channel gives up on smooth correction and resyncs:
    /// <c>maxBacklog = engineLatencyMs + ResyncMarginMs</c> — log, then re-prime.
    /// AudioHQ-validated (0.2.1/0.2.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is also the line the PRE-FILL must stay below, and it is deliberately the looser of the two
    /// numbers: the fill lands above <c>f*</c> on purpose (see <see cref="PrefillSlackMs"/>) and a channel
    /// that lands within a millisecond of this line spends its life crossing it.
    /// </para>
    /// <para>
    /// Raised from 25 to 80 ms after a controlled comparison on real hardware. Two Bluetooth outputs held their
    /// fill flat (0 to 0.1 ms of swing) with one resync each: the warm-up settle and nothing after. Adding a third
    /// device with a weak link -- measured by the user, and matching its own report that it alone is fine --
    /// pushed ALL THREE channels into a 10 ms swing with 9 to 11 resyncs, the other two included. A Bluetooth link
    /// cannot change how full our ring is except by making its endpoint read more slowly, so what the numbers show
    /// is the transport starving the endpoints, our ring backing up, and the trim below dropping audio to bring it
    /// back. Every trim is a gap, and that is the choppiness being reported.
    /// </para>
    /// <para>
    /// A wider margin does NOT reduce how much audio has to be dropped -- the surplus is set by the transport, not
    /// by this number -- it changes how often: from a small gap every few seconds to a larger one far less often.
    /// That is usually the better trade, and the latency it costs is invisible next to the ~200 ms a Bluetooth
    /// output already adds. It is one constant, so it is also the knob to turn back if it sounds worse.
    /// </para>
    /// </remarks>
    public const double ResyncMarginMs = 80.0;

    /// <summary>
    /// Latency presets offered to the user, in ms.
    /// Bluetooth endpoints should default to <see cref="DefaultBluetoothLatencyMs"/>:
    /// a Bluetooth endpoint quietly ignores a low requested latency and reports a much
    /// larger buffer, so asking for 15 ms just starves the buffer.
    /// </summary>
    public static readonly int[] LatencyPresetsMs = [15, 30, 60, 100];

    /// <summary>Default requested engine latency, in ms (the "Safe" preset).</summary>
    public const int DefaultLatencyMs = 100;

    /// <summary>Requested engine latency used for Bluetooth endpoints, in ms.</summary>
    public const int DefaultBluetoothLatencyMs = 100;

    /// <summary>Seconds after a render client starts before its fill is trusted. Do not apply compensation decisions before this.</summary>
    public const double WarmUpSeconds = 3.0;

    /// <summary>
    /// Extra pre-fill beyond <c>target + engineLatencyMs</c>, in ms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The player's first WASAPI pull is approximately one engine-latency's worth of input but not
    /// exactly, and the difference is not knowable up front. Pre-filling to land slightly ABOVE the
    /// target makes the controller converge downward, which is the safe direction: too much buffer
    /// is jitter margin, whereas too little saturates the correction negatively and leaves the ring
    /// with no margin at all.
    /// </para>
    /// <para>
    /// "Slightly" is bounded by the resync line, which is what this value got wrong: landing above
    /// <c>f*</c> is the intent, but the landing point is
    /// <c>engineLatencyMs + TargetBacklogMarginMs + PrefillSlackMs</c> after the first pull and the
    /// resync line is <c>engineLatencyMs + ResyncMarginMs</c>, so the slack must stay under
    /// <c>ResyncMarginMs - TargetBacklogMarginMs</c> MINUS a guard. At 25 ms of slack the landing
    /// point sat 5 ms ABOVE the line, which put every channel over it at birth.
    /// </para>
    /// </remarks>
    public const double PrefillSlackMs = 12.0;

    /// <summary>
    /// How far below the resync line the pre-fill must land, in ms.
    /// </summary>
    /// <remarks>
    /// A guard rather than a tight bound, because the landing point after the player's first pull is only
    /// approximately predictable and a channel that lands on the line stutters. Asserted by a test so that
    /// changing any of the three margins cannot quietly re-create the defect.
    /// </remarks>
    public const double MinimumPrefillGuardMs = 15.0;

    // ---------------------------------------------------------------- drift control

    /// <summary>
    /// How much correction authority the CONTROLLER may use, ±400 ppm = ±0.04 %.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 400 ppm is 0.7 cents — far below the 5–10 cent static pitch JND — and it is the RATE limit that keeps the
    /// correction from turning into audible frequency modulation; the ceiling is not what does that. See
    /// docs/PITFALLS.md B10.
    /// </para>
    /// <para>
    /// This was 200 ppm, on the reasoning that ±100 ppm is the worst case for a pair of consumer devices and that
    /// 200 gives 2x headroom over it. Measured with tools/MirrorSelfTest on real hardware, that assumption does
    /// not hold for Bluetooth endpoints: two of four channels sat pinned at ±200 ppm for an entire run, which
    /// means the real mismatch was at least that large and the controller had no authority left to hold the
    /// trough at <c>f*</c>. A saturated controller is not merely a lost correction: the fill then settles wherever
    /// the mismatch leaves it (measured: 115 ms against a 105 ms target), so devices end up tens of milliseconds
    /// apart from EACH OTHER — which is the one thing this whole mechanism exists to prevent.
    /// </para>
    /// <para>
    /// Kept apart from <see cref="MaxCorrectionHardCeiling"/> rather than merged with it: that one is the
    /// resampler's own safety clamp, and a control bound equal to a safety bound means the safety bound never has
    /// anything to catch.
    /// </para>
    /// </remarks>
    public const double MaxCorrection = 400e-6;

    /// <summary>
    /// The resampler's own hard clamp on the ratio it will apply, ±500 ppm.
    /// </summary>
    /// <remarks>
    /// A backstop rather than a control bound: it exists so that a bug in the controller cannot reach the
    /// resampler as an extreme ratio. AudioHQ ships ±0.5 % (= 8.63 cents) and calls it inaudible; there is no
    /// reason to go near that and this stays an order of magnitude below it.
    /// </remarks>
    public const double MaxCorrectionHardCeiling = 500e-6;

    /// <summary>
    /// Proportional gain, per second, applied to the trough error.
    /// The plant is an integrator, so P alone already yields zero steady-state RATE error:
    /// <c>E_ss = d/K</c>. With K=0.5/s and d=100 ppm, E_ss = 0.2 ms — 15x below the audible
    /// threshold for two correlated sources, and constant, so it never accumulates.
    /// <b>Never add an integral term</b>: that makes the loop a double integrator and
    /// manufactures a limit cycle. See docs/PITFALLS.md B7.
    /// </summary>
    public const double PGain = 0.5;

    /// <summary>Dead time L is ~30–100 ms (up to ~60 ms on bursty wireless). Stability needs K·L &lt; π/2, i.e. K &lt; 15.7/s here — a 31x margin.</summary>
    public const double PGainSafeLimitPerSecond = 15.7;

    /// <summary>
    /// EMA weight per control tick on the trough (~1 s settling at 5 Hz).
    /// Smooths the control variable so the residual wobble has almost no energy in the 1–10 Hz
    /// band where FM detection is most sensitive. AudioHQ-validated (0.3).
    /// </summary>
    public const double TroughSmoothingAlpha = 0.3;

    /// <summary>
    /// Control loop rate. Recomputed at a fixed rate — NEVER per render callback, which
    /// reintroduces per-callback ratio jitter. AudioHQ-validated (targetRate/5).
    /// </summary>
    public const double ControlTickHz = 5.0;

    /// <summary>Per-tick slew limit on the correction: 50 ppm/tick at 5 Hz = 250 ppm/s.</summary>
    public const double MaxCorrectionRatePerTick = 50e-6;

    /// <summary>Guard against an infinite fill loop in the resampler if a source misbehaves.</summary>
    public const int ResamplerMaxIterations = 8;

    // ---------------------------------------------------------------- per-device delay

    /// <summary>
    /// Delay changes at or below this size are slewed; larger ones trigger a mute/resync.
    /// 170 ms slewed at the 0.5 % pitch limit would take 34 SECONDS of audibly sliding
    /// audio, so a brief gap is strictly better. See docs/PITFALLS.md B9.
    /// </summary>
    public const double DelayGlideThresholdMs = 20.0;

    /// <summary>Implied pitch shift budget while slewing a delay change (0.5 % = 8.6 cents).</summary>
    public const double DelayGlideMaxPitchShift = 0.005;

    /// <summary>Fade applied around a large delay change or at engine stop, in ms. Prevents a click at the splice.</summary>
    public const double FadeMs = 10.0;

    /// <summary>
    /// Ramp-in time when a channel STARTS, in ms.
    /// </summary>
    /// <remarks>
    /// A mirror that begins at its full configured level produces a sudden burst — startling with one
    /// device and genuinely unpleasant with several at once, especially Bluetooth speakers that are
    /// already loud. Starting silent and ramping up costs two seconds and removes that entirely.
    /// </remarks>
    public const double StartupRampMs = 2000.0;

    /// <summary>Maximum per-device compensation, in ms. Covers the Bluetooth-vs-wired worst case (double-headphones ships 0–2000 ms).</summary>
    public const int MaxDelayMs = 2000;

    /// <summary>
    /// Reserved sample slots added to the delay ring beyond <see cref="MaxDelayMs"/>
    /// so the ring never collides with the read pointer.
    /// </summary>
    public const int DelayRingSlackFrames = 8192;

    // ---------------------------------------------------------------- acoustic calibration

    /// <summary>Calibration sweep sample rate, Hz.</summary>
    public const int CalibrationSampleRate = 48000;

    /// <summary>
    /// Sweep lower band, Hz. NOT 20 Hz — no Bluetooth speaker reproduces it, and low
    /// frequencies are dominated by room modes with long decay, which biases a delay estimate.
    /// </summary>
    public const double CalibrationSweepF1 = 200.0;

    /// <summary>Sweep upper band, Hz. NOT 20 kHz — the speaker is rolling off there and it is heavily beamed.</summary>
    public const double CalibrationSweepF2 = 8000.0;

    /// <summary>Sweep duration, ms. Long enough for SNR, short enough not to average over a moving target (limiter engagement).</summary>
    public const double CalibrationSweepMs = 250.0;

    /// <summary>Sweep level. Kept clear of any AGC / limiter, which would distort the envelope and bias the leading edge.</summary>
    public const double CalibrationSweepAmplitude = 0.25;   // ≈ -12 dBFS

    /// <summary>Raised-cosine fade at both ends of the sweep, to avoid starting on a discontinuity.</summary>
    public const double CalibrationFadeMs = 10.0;

    /// <summary>Silence before the sweep.</summary>
    public const double CalibrationLeadSilenceMs = 200.0;

    /// <summary>Silence after the sweep, giving the quality metrics a clean noise-floor region.</summary>
    public const double CalibrationTailSilenceMs = 750.0;

    /// <summary>Minimum peak-to-noise ratio (dB) for an accepted measurement. Catches "nothing played" and "only room noise".</summary>
    public const double CalibrationMinPsrDb = 12.0;

    /// <summary>Required dominance of the main peak over the next local maximum outside ±5 ms, as a linear factor (4x = 12 dB).</summary>
    public const double CalibrationMinPeakDominance = 4.0;

    /// <summary>Repeat measurements per device before statistics.</summary>
    public const int CalibrationRepeats = 7;

    /// <summary>Accepted measurements required for a usable result. Fewer than this → report failure, never silently fall back to 0.</summary>
    public const int CalibrationMinAccepted = 3;

    /// <summary>MAD multiplier for outlier rejection (3 × 1.4826 × MAD ≈ 3σ for a normal distribution).</summary>
    public const double CalibrationMadSigma = 3.0;

    /// <summary>Spread (P90 − P10) above which a measurement is marked low-confidence and refused for auto-apply, in ms.</summary>
    public const double CalibrationMaxSpreadMs = 8.0;
}
