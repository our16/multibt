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
    /// What a device's assumed latency is based on.
    /// </summary>
    public enum CompensationBasis
    {
        /// <summary>The device takes no part in alignment and gets no compensation.</summary>
        None = 0,

        /// <summary>
        /// No measurement exists, so a typical latency for the device's transport was assumed.
        /// </summary>
        Estimated = 1,

        /// <summary>A measurement exists and was used.</summary>
        Measured = 2,
    }

    /// <summary>One device's compensation, and what it was derived from.</summary>
    /// <param name="CompensationMs">Delay to add, in ms.</param>
    /// <param name="Basis">Whether that came from a measurement, an estimate, or nothing.</param>
    /// <param name="AssumedLatencyMs">The latency the device was assumed to have.</param>
    public sealed record DeviceCompensation(double CompensationMs, CompensationBasis Basis, double AssumedLatencyMs);

    /// <summary>
    /// A typical end-to-end latency for a transport, used when nothing has been measured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are ESTIMATES, not measurements, and every caller that shows them must say so. They exist
    /// because the alternative is worse: with no measurement at all — which is the normal state, since
    /// acoustic measurement is not implemented end to end — alignment would add exactly zero to every
    /// device while presenting itself as a working mode. That is how a user ends up staring at a delay
    /// field wondering where the number came from, or why two speakers are audibly out of step.
    /// </para>
    /// <para>
    /// The values are chosen to be right about the one thing that matters: Bluetooth is an order of
    /// magnitude slower than everything wired, so aligning a wired speaker to a Bluetooth one is the case
    /// that needs a real number. Being roughly right there is far better than being exactly zero, because
    /// the residual error is what the manual offset is for.
    /// </para>
    /// </remarks>
    public static double EstimateLatencyMs(Transport transport) => transport switch
    {
        // A2DP buffering is typically 150-400 ms.
        Transport.Bluetooth => 200.0,

        // HDMI sinks re-clock the stream; a frame or so.
        Transport.Hdmi => 20.0,

        // A wired endpoint's own buffer.
        Transport.Usb => 10.0,

        // Wired analogue, or a virtual device: negligible, but not nothing.
        _ => 10.0,
    };

    /// <summary>
    /// The latency assumed for one device: its measurement when there is one, otherwise an estimate.
    /// </summary>
    public static (double LatencyMs, CompensationBasis Basis) AssumedLatency(CompensationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return target.HasMeasurement
            ? (target.MeasuredDelayRelRefMs, CompensationBasis.Measured)
            : (EstimateLatencyMs(target.Transport), CompensationBasis.Estimated);
    }

    /// <summary>
    /// Computes the automatic compensation for every device, with its basis.
    /// </summary>
    /// <remarks>
    /// In <see cref="SyncMode.AlignAll"/> every participating device is delayed so that all of them land at
    /// the slowest assumed latency in the set. In <see cref="SyncMode.WiredOnly"/> Bluetooth devices are
    /// excluded and keep their natural latency, so only the wired group is aligned internally.
    /// </remarks>
    public static IReadOnlyDictionary<string, DeviceCompensation> ComputeCompensationPlan(
        IReadOnlyCollection<CompensationTarget> targets,
        SyncMode mode,
        int maxDelayMs = EngineTunables.MaxDelayMs)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var result = new Dictionary<string, DeviceCompensation>(StringComparer.Ordinal);
        if (targets.Count == 0)
        {
            return result;
        }

        // Participants: everything, except Bluetooth under WiredOnly.
        List<CompensationTarget> aligned = targets
            .Where(t => mode != SyncMode.WiredOnly || t.Transport != Transport.Bluetooth)
            .ToList();

        if (aligned.Count == 0)
        {
            // Nothing to align against: every device keeps its natural latency.
            foreach (CompensationTarget target in targets)
            {
                result[target.DeviceKey] = new DeviceCompensation(0.0, CompensationBasis.None, 0.0);
            }

            return result;
        }

        var assumed = targets.ToDictionary(
            t => t.DeviceKey,
            t => AssumedLatency(t),
            StringComparer.Ordinal);

        double referenceMs = aligned.Max(t => assumed[t.DeviceKey].LatencyMs);
        var alignedKeys = aligned.Select(t => t.DeviceKey).ToHashSet(StringComparer.Ordinal);

        foreach (CompensationTarget target in targets)
        {
            (double latency, CompensationBasis basis) = assumed[target.DeviceKey];

            if (!alignedKeys.Contains(target.DeviceKey))
            {
                result[target.DeviceKey] = new DeviceCompensation(0.0, CompensationBasis.None, latency);
                continue;
            }

            double compensation = Math.Clamp(referenceMs - latency, 0.0, maxDelayMs);
            result[target.DeviceKey] = new DeviceCompensation(compensation, basis, latency);
        }

        return result;
    }

    /// <summary>
    /// Computes the automatic compensation for every device.
    /// </summary>
    public static IReadOnlyDictionary<string, double> ComputeCompensations(
        IReadOnlyCollection<CompensationTarget> targets,
        SyncMode mode,
        int maxDelayMs = EngineTunables.MaxDelayMs) =>
        ComputeCompensationPlan(targets, mode, maxDelayMs)
            .ToDictionary(pair => pair.Key, pair => pair.Value.CompensationMs, StringComparer.Ordinal);

    /// <summary>
    /// The system's end-to-end latency implied by a set of compensations, in ms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is <c>max(assumed latency + compensation)</c> over the participating devices — i.e. the latency
    /// of the device everything else is aligned to, and the number that decides whether video is watchable.
    /// </para>
    /// <para>
    /// "Assumed" rather than "measured", because an estimate is exactly what the figure is based on when
    /// nothing has been measured. Reporting 0 in that case would claim there is no latency problem at all:
    /// aligning to a Bluetooth speaker really does mean ~200 ms, which is what makes lip-sync fail, and the
    /// whole point of this number is to warn about that.
    /// </para>
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
            if (mode == SyncMode.WiredOnly && target.Transport == Transport.Bluetooth)
            {
                continue;
            }

            (double latency, _) = AssumedLatency(target);
            double compensation = compensations.TryGetValue(target.DeviceKey, out double c) ? c : 0.0;
            double total = latency + compensation;

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
