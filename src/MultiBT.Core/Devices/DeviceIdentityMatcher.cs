using MultiBT.Core.Audio;
using MultiBT.Core.Config;

namespace MultiBT.Core.Devices;

/// <summary>
/// Decides whether an enumerated endpoint is the device a stored profile refers to.
/// </summary>
/// <remarks>
/// <para>
/// A single identity key is not sufficient, and the reason is documented Microsoft behaviour:
/// <c>MMDevice.ID</c> is an OPAQUE string whose lifetime is tied to the device *installation*,
/// so it survives a reboot but CHANGES on a driver upgrade or reinstall. Windows can also
/// recreate an endpoint under a fresh id. On top of that, a Bluetooth re-pair can change which
/// endpoint appears at all.
/// </para>
/// <para>
/// So matching degrades through three levels, and the level that matched matters: a match by
/// friendly name alone is a weak signal the caller should treat as re-adopted rather than
/// confirmed (and it should invalidate any stored measurement, since a differently-identified
/// endpoint is not necessarily the same physical device).
/// </para>
/// <para>See docs/PITFALLS.md C4 and docs/SPEC.md §4.2.</para>
/// </remarks>
public static class DeviceIdentityMatcher
{
    /// <summary>How a stored device record was matched to a live endpoint.</summary>
    public enum MatchKind
    {
        /// <summary>No match.</summary>
        None = 0,

        /// <summary>Matched on the WASAPI endpoint id. Strongest, but breaks on driver reinstall.</summary>
        EndpointId = 1,

        /// <summary>Matched on the PnP instance id. Survives an endpoint id change.</summary>
        InstanceId = 2,

        /// <summary>Matched on the friendly name only. Weak — treat as re-adoption, not confirmation.</summary>
        FriendlyName = 3,
    }

    /// <summary>
    /// Attempts to match a stored identity record against a live endpoint, strongest key first.
    /// </summary>
    /// <param name="identities">The stored identities.</param>
    /// <param name="endpoint">The live endpoint.</param>
    /// <returns>Which key matched, or <see cref="MatchKind.None"/>.</returns>
    public static MatchKind Match(DeviceIdentities? identities, AudioEndpointInfo? endpoint)
    {
        if (identities is null || endpoint is null)
        {
            return MatchKind.None;
        }

        if (!string.IsNullOrEmpty(identities.EndpointId)
            && string.Equals(identities.EndpointId, endpoint.EndpointId, StringComparison.OrdinalIgnoreCase))
        {
            return MatchKind.EndpointId;
        }

        if (!string.IsNullOrEmpty(identities.InstanceId)
            && string.Equals(identities.InstanceId, endpoint.InstanceId, StringComparison.OrdinalIgnoreCase))
        {
            return MatchKind.InstanceId;
        }

        if (!string.IsNullOrEmpty(identities.FriendlyName)
            && string.Equals(identities.FriendlyName, endpoint.FriendlyName, StringComparison.OrdinalIgnoreCase))
        {
            return MatchKind.FriendlyName;
        }

        return MatchKind.None;
    }

    /// <summary>
    /// Whether a match is strong enough to keep trusting the stored measurement.
    /// </summary>
    /// <remarks>
    /// A weak (name-only) match means the endpoint identity changed, so anything measured
    /// against the old endpoint may no longer hold.
    /// </remarks>
    public static bool PreservesMeasurement(MatchKind kind) =>
        kind is MatchKind.EndpointId or MatchKind.InstanceId;
}
