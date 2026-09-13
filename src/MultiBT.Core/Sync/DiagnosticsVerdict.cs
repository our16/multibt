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
/// every radio symptom reaches this code only as its consequences -- a device that reports a rising latency, or a
/// buffer that is not being drained. When those are absent and the user still hears choppiness, the honest answer
/// is <see cref="FaultLayer.Delivered"/> rather than a guess.
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
}
