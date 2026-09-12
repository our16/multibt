namespace MultiBT.Core.Sync;

/// <summary>How confident we are in a stored acoustic measurement.</summary>
public enum MeasurementQuality
{
    /// <summary>No usable measurement. Deliberately a distinct state — never silently treated as 0 ms.</summary>
    Failed = 0,

    /// <summary>Measured, but the spread was wide. Usable as a starting point; not auto-applied.</summary>
    Low = 1,

    /// <summary>Measured and consistent. May be auto-applied.</summary>
    High = 2,
}

/// <summary>
/// Per-device latency state — the three-field model, kept deliberately separate because it
/// distinguishes facts we learned, facts we decided, and facts the user decided.
/// </summary>
/// <remarks>
/// <para>
/// <b>The measured value is RELATIVE, not absolute.</b> A microphone's own path (ADC +
/// USB/driver buffering) adds an unknown constant to every measurement. That constant
/// cancels in the differences between devices but not in any absolute value, so an absolute
/// per-device latency is simply not measurable. The property is named
/// <see cref="MeasuredDelayRelRefMs"/> to make that impossible to forget. Never render the
/// largest of these as "your system latency" — it is wrong by tens of milliseconds.
/// See docs/SPEC.md §5.1.1.
/// </para>
/// </remarks>
public sealed class DeviceLatencySettings
{
    /// <summary>
    /// Measured delay relative to the reference device, in ms. Read-only to the user;
    /// this is an observation, not a control.
    /// </summary>
    public double MeasuredDelayRelRefMs { get; set; }

    /// <summary>P90 − P10 of the accepted repeats, in ms. Shown in the UI as "±N ms" and used as the confidence gate.</summary>
    public double MeasurementSpreadMs { get; set; }

    /// <summary>Confidence in the measurement.</summary>
    public MeasurementQuality MeasurementQuality { get; set; } = MeasurementQuality.Failed;

    /// <summary>When the measurement was taken, or <c>null</c> if never.</summary>
    public DateTimeOffset? MeasurementUtc { get; set; }

    /// <summary>
    /// True when the measurement can no longer be trusted. See <see cref="InvalidateMeasurement"/>.
    /// </summary>
    public bool MeasurementIsStale { get; set; }

    /// <summary>Human-readable reason the measurement went stale, for the UI.</summary>
    public string? MeasurementStaleReason { get; set; }

    /// <summary>
    /// Software-controlled delay for this device, in ms. Computed by
    /// <see cref="LatencyModel.ComputeCompensations"/>; not user-editable.
    /// </summary>
    public double CompensationMs { get; set; }

    /// <summary>
    /// User trim, in ms. <b>Signed.</b> Positive means "make this device later".
    /// </summary>
    public double ManualOffsetMs { get; set; }

    /// <summary>Whether a usable measurement exists at all.</summary>
    public bool HasMeasurement => MeasurementUtc is not null && MeasurementQuality != MeasurementQuality.Failed;

    /// <summary>
    /// Marks the measurement stale while KEEPING the value usable.
    /// </summary>
    /// <remarks>
    /// Invalidation is mandatory on: endpoint id change or re-registration, device
    /// disconnect/reconnect (a Bluetooth re-pair can silently renegotiate the codec),
    /// mix-format or sample-rate change, and any change to the global engine latency or to
    /// this device's latency override. Skipping this is the "it was fine yesterday" bug class.
    /// See docs/SPEC.md §5.3.
    /// </remarks>
    public void InvalidateMeasurement(string reason)
    {
        if (MeasurementUtc is null)
        {
            return;
        }

        MeasurementIsStale = true;
        MeasurementStaleReason = reason;
    }
}
