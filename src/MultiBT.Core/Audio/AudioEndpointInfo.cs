namespace MultiBT.Core.Audio;

/// <summary>
/// Immutable snapshot of one WASAPI render endpoint.
/// </summary>
/// <remarks>
/// This is a plain DTO on purpose: <c>MMDevice</c> MUST NOT be cached or held long-term.
/// A cached COM object dies on sleep/resume even though the endpoint still enumerates,
/// so the engine re-resolves a fresh <c>MMDevice</c> on every activation
/// (see docs/PITFALLS.md C5).
/// </remarks>
/// <param name="EndpointId">
/// <c>MMDevice.ID</c> — the WASAPI endpoint id string. <b>OPAQUE</b>: the format is
/// documented by Microsoft as undefined and MUST NOT be parsed. It is stable across a
/// reboot but CHANGES on driver upgrade/reinstall, so it cannot be the only identity
/// key (see docs/PITFALLS.md C4).
/// </param>
/// <param name="InstanceId">
/// <c>PKEY_Device_InstanceId</c> — the PnP device instance id. This is what the
/// transport classifier keys on, and the second-level identity fallback.
/// </param>
/// <param name="FriendlyName">Display name, e.g. "JBL Charge 5".</param>
/// <param name="DeviceFriendlyName">Endpoint name, e.g. "Speakers (JBL Charge 5)".</param>
/// <param name="Transport">Classified transport.</param>
/// <param name="IsActive"><c>true</c> when the endpoint state is Active.</param>
public sealed record AudioEndpointInfo(
    string EndpointId,
    string InstanceId,
    string FriendlyName,
    string DeviceFriendlyName,
    Transport Transport,
    bool IsActive)
{
    /// <summary>
    /// True when this endpoint is a paired-but-disconnected Bluetooth device.
    /// Such an endpoint enumerates only under <c>DeviceState.All</c>
    /// (never under <c>DeviceState.Active</c>) — a classic source of "my speaker vanished"
    /// bugs. See docs/SPEC.md §4.1.
    /// </summary>
    public bool IsPairedButDisconnected => Transport == Transport.Bluetooth && !IsActive;

    public override string ToString() => $"{FriendlyName} [{Transport}{(IsActive ? "" : ", inactive")}]";
}
