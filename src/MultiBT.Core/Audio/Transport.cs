namespace MultiBT.Core.Audio;

/// <summary>
/// Transport of an audio endpoint, used to pick the latency plausibility window
/// (see <see cref="Devices.TransportClassifier.PlausibleDelayRange"/>), the UI icon,
/// and whether a Bluetooth reconnect should invalidate a stored measurement.
/// </summary>
public enum Transport
{
    /// <summary>Classification failed or the transport is unknown. Always a safe fallback.</summary>
    Other = 0,

    /// <summary>Bluetooth A2DP / HFP audio endpoint (instance id under BTHENUM / BTHHFENUM).</summary>
    Bluetooth = 1,

    /// <summary>USB audio endpoint.</summary>
    Usb = 2,

    /// <summary>HDMI / DisplayPort audio endpoint.</summary>
    Hdmi = 3,
}
