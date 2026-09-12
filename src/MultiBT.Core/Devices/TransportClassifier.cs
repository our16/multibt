using MultiBT.Core.Audio;

namespace MultiBT.Core.Devices;

/// <summary>
/// Classifies an endpoint's transport from its PnP instance id and display strings.
/// </summary>
/// <remarks>
/// <para>
/// The transport matters in three places: it selects the latency plausibility window used
/// to reject bogus acoustic measurements (docs/SPEC.md §5.4.3), it drives the UI icon, and
/// it decides whether a reconnect must invalidate a stored measurement.
/// </para>
/// <para>
/// <b>UNVERIFIED RULE — log real values before trusting this.</b> The <c>BTHENUM\</c> /
/// <c>BTHHFENUM\</c> enumerator prefixes are convention, not something backed by an
/// authoritative document, and HDMI audio is genuinely hard to detect from the instance id
/// alone (it usually presents as an <c>HDAUDIO\</c> function of the GPU's audio controller,
/// so the distinguishing signal is the endpoint NAME, not the id). This classifier therefore
/// falls back to <see cref="Transport.Other"/> rather than guessing, and
/// <see cref="PlausibleDelayRange"/> widens to the full range for <c>Other</c> so a
/// misclassification degrades into "we cannot auto-reject" instead of "we reject good data".
/// See docs/PITFALLS.md D4.
/// </para>
/// </remarks>
public static class TransportClassifier
{
    private const string BluetoothEnumerator = "BTHENUM\\";
    private const string BluetoothHandsFreeEnumerator = "BTHHFENUM\\";
    private const string UsbEnumerator = "USB\\";

    // HDMI/DisplayPort audio presents as an HDAUDIO function; these name fragments are the
    // reliable signal. Verified as *not* present in Bluetooth endpoint names.
    private static readonly string[] HdmiNameFragments =
    [
        "HDMI",
        "DisplayPort",
        "Display Audio",
        "Digital Display",
    ];

    /// <summary>
    /// Classifies an endpoint.
    /// </summary>
    /// <param name="instanceId">
    /// <c>PKEY_Device_InstanceId</c>, e.g.
    /// <c>BTHENUM\{0000110b-...}\7&amp;2f9a1c3&amp;0&amp;001122334455_C00000000</c>.
    /// </param>
    /// <param name="friendlyName">Endpoint display name, e.g. "JBL Charge 5".</param>
    /// <param name="deviceDescription">Optional <c>PKEY_Device_DeviceDesc</c>.</param>
    public static Transport Classify(
        string? instanceId,
        string? friendlyName = null,
        string? deviceDescription = null)
    {
        // Normalise FIRST. Real values arrive as "{1}.HDAUDIO\..." or
        // "\\?\bthenum#...#..." — with an ordinal prefix and '#' separators — so raw prefix
        // matching misses them entirely. See EndpointIdentityReader.
        string id = EndpointIdentityReader.Normalize(instanceId);

        if (id.Length > 0)
        {
            if (StartsWith(id, BluetoothEnumerator) || StartsWith(id, BluetoothHandsFreeEnumerator))
            {
                return Transport.Bluetooth;
            }

            if (StartsWith(id, UsbEnumerator))
            {
                return Transport.Usb;
            }
        }

        // Name-based detection. Checked after the id-based rules so that a Bluetooth or USB
        // device whose name happens to mention a display cannot be misreported.
        foreach (string? text in new[] { friendlyName, deviceDescription })
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            foreach (string fragment in HdmiNameFragments)
            {
                if (text.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    return Transport.Hdmi;
                }
            }
        }

        return Transport.Other;
    }

    /// <summary>
    /// The latency window, in ms, inside which an acoustic measurement of this transport is
    /// considered plausible. Outside it the measurement is rejected: a 0 ms reading on a
    /// device that should be late almost always means something else was audible.
    /// </summary>
    public static (double MinMs, double MaxMs) PlausibleDelayRange(Transport transport) => transport switch
    {
        Transport.Usb => (0.0, 80.0),
        Transport.Hdmi => (0.0, 120.0),
        Transport.Bluetooth => (80.0, 450.0),
        // Widened deliberately: an unknown transport must not cause good data to be rejected.
        _ => (0.0, EngineTunables.MaxDelayMs),
    };

    /// <summary>Returns whether a measured delay is plausible for the given transport.</summary>
    public static bool IsPlausibleDelay(Transport transport, double measuredMs)
    {
        (double min, double max) = PlausibleDelayRange(transport);
        return measuredMs >= min && measuredMs <= max;
    }

    /// <summary>
    /// Checks if the given instance id and friendly name indicate a Bluetooth device.
    /// </summary>
    public static bool IsBluetooth(string? instanceId, string? friendlyName) =>
        Classify(instanceId, friendlyName) == Transport.Bluetooth;

    /// <summary>
    /// Whether a reconnect must invalidate a stored measurement for this transport.
    /// </summary>
    /// <remarks>
    /// True for Bluetooth, and this is not theoretical: a re-pair forces fresh codec
    /// negotiation and the link can silently fall back to baseline SBC, which differs from
    /// AAC by roughly 60 ms with no user-visible signal. See docs/PITFALLS.md C3.
    /// </remarks>
    public static bool RequiresRemeasureOnReconnect(Transport transport) =>
        transport == Transport.Bluetooth;

    private static bool StartsWith(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
