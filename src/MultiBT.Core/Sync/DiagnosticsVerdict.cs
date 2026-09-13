using MultiBT.Core.Audio;

namespace MultiBT.Core.Sync;

/// <summary>
/// Which layer is responsible for a fault, as far as the numbers can tell.
/// </summary>
/// <remarks>
/// Ordered by how far down the audio path the evidence points, which is also the order the classification
/// checks them in: the endpoint is below us, the shared capture is at the top, and "this device alone" sits
/// between them.
/// </remarks>
public enum FaultLayer
{
    /// <summary>Nothing in the numbers looks wrong.</summary>
    None,

    /// <summary>
    /// The device's OWN reported latency is running above its steady state, so it is not draining at a steady
    /// rate. Below everything this program controls: the Bluetooth link, the driver, or the device.
    /// </summary>
    Endpoint,

    /// <summary>
    /// Several channels are starving in the same window, so the shortfall is shared -- the capture source, since
    /// that is the only thing upstream of every channel.
    /// </summary>
    SharedSource,

    /// <summary>One channel starving while the others are fine: that device's own read/consume rate.</summary>
    SingleDevice,

    /// <summary>
    /// The ring is holding more than the target and keeps being cut back, so audio is being dropped to keep up.
    /// This is the audible one: every cut is a discontinuity, and devices sitting at different depths arrive at
    /// different times.
    /// </summary>
    Backlog,

    /// <summary>
    /// The drift correction is pinned at its ceiling, so the device's clock differs by more than the authority
    /// deliberately granted to the controller.
    /// </summary>
    ClockMismatch,

    /// <summary>Every number is healthy, so whatever is audible is happening after the data leaves this process.</summary>
    Delivered,
}

/// <summary>
/// Reads a set of channel diagnostics and says which layer the evidence points at.
/// </summary>
/// <remarks>
/// <para>
/// A pure function of the numbers, deliberately: the whole point of it is that the answer does not depend on who
/// is reading it or on what they believe. It is also the only part of the diagnostics that can be tested without
/// a sound card.
/// </para>
/// <para>
/// What it canNOT see, and must not pretend to: the radio. There is no public API for Bluetooth link quality, so
/// every radio symptom reaches this code only as its consequences. When those are absent and the user still hears
/// choppiness, the honest answer is <see cref="FaultLayer.Delivered"/> rather than a guess.
/// </para>
/// <para>
/// The backlog rule exists because the first version of this did not have one, and reported a pinned correction as
/// a clock mismatch on a machine whose real problem was rings holding 115 to 145 ms against a 105 ms target and
/// being trimmed over and over. A pinned proportional correction only means the error exceeds 0.8 ms -- which is
/// true whenever the fill is off target at all -- so it is the WEAKEST evidence here, not the strongest.
/// </para>
/// </remarks>
public static class DiagnosticsVerdict
{
    /// <summary>
    /// How far above its own steady state a device's latency must run before it counts as not draining steadily.
    /// </summary>
    /// <remarks>
    /// Well above the millisecond-scale wander a healthy endpoint reports, and well below the tens of milliseconds
    /// a stalled Bluetooth link shows.
    /// </remarks>
    public const double EndpointLatencyAlarmMs = 20.0;

    /// <summary>How empty the ring must get, as a fraction of the target, before a channel counts as starving.</summary>
    /// <remarks>
    /// A fraction rather than an absolute depth, because the target itself is a user setting: what matters is how
    /// much of the intended margin is left, not a fixed number of milliseconds.
    /// </remarks>
    public const double StarvingFillFraction = 0.25;

    /// <summary>How much of a read may be zero-filled before the channel counts as handing out silence.</summary>
    public const double SilenceFractionAlarm = 0.5;

    /// <summary>How close to the ceiling the correction must be to count as pinned.</summary>
    public const double PinnedCorrectionFraction = 0.98;

    /// <summary>
    /// How far above the target the AVERAGE fill may sit before the channel counts as backed up.
    /// </summary>
    /// <remarks>
    /// A healthy channel holds the target to within about a millisecond, so this is an order of magnitude above
    /// normal wander and well below the tens of milliseconds a genuinely backed-up ring shows.
    /// </remarks>
    public const double BacklogMeanAlarmMs = 15.0;

    /// <summary>
    /// How much the fill may swing within one window before the channel counts as backed up.
    /// </summary>
    /// <remarks>
    /// This is the signature of the trim-and-refill cycle: the ring fills past the resync line, gets cut back to
    /// the target, and fills again. A healthy channel's fill does not move by whole milliseconds in a window, so a
    /// swing of this size means audio is being dropped repeatedly -- which is heard as choppiness.
    /// </remarks>
    public const double BacklogSwingAlarmMs = 8.0;

    /// <summary>
    /// Classifies one set of channels.
    /// </summary>
    /// <param name="channels">Every channel's diagnostics for the same window.</param>
    /// <returns>The layer the evidence points at.</returns>
    /// <remarks>
    /// One layer, not a list: a caller that has to rank several answers ends up trusting none of them. The
    /// per-channel detail is still in the diagnostics for anyone who wants it.
    /// </remarks>
    public static FaultLayer Classify(IReadOnlyList<ChannelDiagnostics> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);

        if (channels.Count == 0)
        {
            return FaultLayer.None;
        }

        // The endpoint is below everything we control, so it is checked first: if a device is not draining, no
        // conclusion drawn from the buffers above it means anything.
        foreach (ChannelDiagnostics channel in channels)
        {
            if (channel.CurrentLatencyMs - channel.AverageLatencyMs > EndpointLatencyAlarmMs)
            {
                return FaultLayer.Endpoint;
            }

            if (channel.WorstSilenceFraction >= SilenceFractionAlarm && !IsStarving(channel))
            {
                // Zero-filling reads while the ring was still holding data: the device asked for more than it was
                // taking, so the shortfall is on its side. The same silence fraction with an EMPTY ring means the
                // opposite -- there was nothing to hand out -- and belongs to the starvation paths below instead.
                return FaultLayer.Endpoint;
            }
        }

        int starving = 0;

        foreach (ChannelDiagnostics channel in channels)
        {
            if (IsStarving(channel))
            {
                starving++;
            }
        }

        if (starving >= 2)
        {
            return FaultLayer.SharedSource;
        }

        if (starving == 1)
        {
            return FaultLayer.SingleDevice;
        }

        // Above the starvation checks because a ring being cut back is a fault in THIS program's own chain, and
        // that is the more actionable answer of the two.
        foreach (ChannelDiagnostics channel in channels)
        {
            if (IsBackedUp(channel))
            {
                return FaultLayer.Backlog;
            }
        }

        foreach (ChannelDiagnostics channel in channels)
        {
            if (Math.Abs(channel.CorrectionPpm) >= EngineTunables.MaxCorrection * 1e6 * PinnedCorrectionFraction)
            {
                return FaultLayer.ClockMismatch;
            }
        }

        return FaultLayer.Delivered;
    }

    /// <summary>Whether this channel's ring is drawn down close to empty within the window.</summary>
    public static bool IsStarving(ChannelDiagnostics channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return channel.FillMinMs < channel.TargetBacklogMs * StarvingFillFraction;
    }

    /// <summary>
    /// Whether this channel is holding more than its target, steadily or as a fill-and-trim cycle.
    /// </summary>
    /// <remarks>
    /// Either symptom counts on its own. A ring that sits above the target has added latency the controller cannot
    /// remove, since its authority is hundredths of a percent; a ring whose fill swings by milliseconds is being
    /// trimmed, and every trim drops audio.
    /// </remarks>
    public static bool IsBackedUp(ChannelDiagnostics channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (channel.FillMeanMs > channel.TargetBacklogMs + BacklogMeanAlarmMs)
        {
            return true;
        }

        return channel.FillMaxMs - channel.FillMinMs > BacklogSwingAlarmMs;
    }
}
