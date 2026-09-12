using MultiBT.Core.Audio;

namespace MultiBT.Core.Sync;

/// <summary>One device's input to the compensation calculation.</summary>
/// <param name="DeviceKey">Stable profile key for the device.</param>
/// <param name="MeasuredDelayRelRefMs">Measured delay relative to the reference device, in ms.</param>
/// <param name="Transport">Transport, which decides participation in <see cref="SyncMode.WiredOnly"/>.</param>
/// <param name="HasMeasurement">
/// Whether a usable measurement exists. A device with no measurement is <b>excluded</b> from both
/// the alignment reference and the system-latency figure, and receives zero compensation.
/// Treating "unknown" as "0 ms" would hand such a device the LARGEST compensation in the set —
/// it would be delayed by the full reference amount on top of a latency nobody has measured.
/// </param>
public sealed record CompensationTarget(
    string DeviceKey,
    double MeasuredDelayRelRefMs,
    Transport Transport,
    bool HasMeasurement = true);

/// <summary>
/// The single source of truth for turning the three latency fields into a number of samples.
/// </summary>
/// <remarks>
/// <b>Both the engine and the UI must call these methods</b> rather than re-deriving the
/// arithmetic, otherwise the two silently disagree and the user sees a value that is not
/// what is being applied. See docs/SPEC.md §5.1.2.
/// </remarks>
public static class LatencyModel
{
    /// <summary>
    /// The effective delay in ms: <c>clamp(compensationMs + manualOffsetMs, 0, maxDelayMs)</c>.
    /// </summary>
    /// <remarks>
    /// The clamp matters. <c>manualOffsetMs</c> is signed, and when
    /// <c>CompensationMs == 0</c> a negative trim simply has nowhere to move — which is why
    /// the UI must display the resolved value rather than the raw trim.
    /// </remarks>
    public static double ComputeEffectiveDelayMs(DeviceLatencySettings settings, int maxDelayMs = EngineTunables.MaxDelayMs)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Math.Clamp(settings.CompensationMs + settings.ManualOffsetMs, 0.0, maxDelayMs);
    }

    /// <summary>
    /// The effective delay quantised to whole samples at the device mix rate.
    /// </summary>
    /// <remarks>
    /// Integer-sample quantisation is ≤20.8 µs at 48 kHz — about 40x finer than the tightest
    /// alignment anyone can hear — so no fractional delay line is needed.
    /// See docs/SPEC.md §5.1.2.
    /// </remarks>
    public static int ComputeEffectiveDelaySamples(
        DeviceLatencySettings settings,
        int deviceSampleRate,
        int maxDelayMs = EngineTunables.MaxDelayMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceSampleRate);
        double ms = ComputeEffectiveDelayMs(settings, maxDelayMs);
        return (int)Math.Round(ms / 1000.0 * deviceSampleRate, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Computes the automatic compensation for every device.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In <see cref="SyncMode.AlignAll"/> every participating device is delayed so that all of
    /// them land at <c>max(measured)</c> over the measured set. In
    /// <see cref="SyncMode.WiredOnly"/> only the non-Bluetooth group is aligned internally, and
    /// Bluetooth devices get zero compensation so they keep their natural latency.
    /// </para>
    /// <para>
    /// <b>Devices without a measurement get zero compensation and do not set the reference.</b>
    /// They play at their natural latency, which is the only defensible choice: their latency is
    /// unknown, so neither delaying them to the reference nor treating them as the reference is
    /// justified. The UI flags them as unmeasured so the user can run a calibration.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, double> ComputeCompensations(
        IReadOnlyCollection<CompensationTarget> targets,
        SyncMode mode,
        int maxDelayMs = EngineTunables.MaxDelayMs)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (targets.Count == 0)
        {
            return result;
        }

        // Only measured devices may take part in the alignment: an unmeasured device has no
        // known delay, so it can neither be aligned nor anchor the reference.
        List<CompensationTarget> aligned = targets
            .Where(t => t.HasMeasurement)
            .Where(t => mode != SyncMode.WiredOnly || t.Transport != Transport.Bluetooth)
            .ToList();

        if (aligned.Count == 0)
        {
            // Nothing measurable to align against: leave every device at its natural latency.
            foreach (CompensationTarget target in targets)
            {
                result[target.DeviceKey] = 0.0;
            }

            return result;
        }

        double referenceMs = aligned.Max(t => t.MeasuredDelayRelRefMs);
        var alignedKeys = aligned.Select(t => t.DeviceKey).ToHashSet(StringComparer.Ordinal);

        foreach (CompensationTarget target in targets)
        {
            double compensation = alignedKeys.Contains(target.DeviceKey)
                ? referenceMs - target.MeasuredDelayRelRefMs
                : 0.0;

            result[target.DeviceKey] = Math.Clamp(compensation, 0.0, maxDelayMs);
        }

        return result;
    }

    /// <summary>
    /// The system's end-to-end latency implied by a set of compensations, in ms.
    /// </summary>
    /// <remarks>
    /// This is <c>max(measured + compensation)</c> over the MEASURED participating devices — i.e.
    /// the latency of the device everything else is aligned to, and the number that decides
    /// whether video is watchable. Unmeasured devices are excluded because their contribution
    /// cannot be known; including them as zero would understate the figure and hide a lip-sync
    /// problem, which is the one thing this number exists to warn about.
    /// </remarks>
    public static double ComputeSystemLatencyMs(
        IReadOnlyCollection<CompensationTarget> targets,
        IReadOnlyDictionary<string, double> compensations,
        SyncMode mode)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(compensations);

        double worst = 0.0;
        bool any = false;

        foreach (CompensationTarget target in targets)
        {
            if (!target.HasMeasurement)
            {
                continue;
            }

            if (mode == SyncMode.WiredOnly && target.Transport == Transport.Bluetooth)
            {
                continue;
            }

            double compensation = compensations.TryGetValue(target.DeviceKey, out double c) ? c : 0.0;
            double total = target.MeasuredDelayRelRefMs + compensation;

            worst = any ? Math.Max(worst, total) : total;
            any = true;
        }

        return any ? worst : 0.0;
    }

    /// <summary>
    /// Whether the system latency exceeds the audio-lag detectability threshold from
    /// ITU-R BT.1359-1 (~125 ms), meaning lip-sync is broken and video will look wrong.
    /// </summary>
    public static bool ExceedsLipSyncThreshold(double systemLatencyMs) => systemLatencyMs > 125.0;
}
