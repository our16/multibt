namespace MultiBT.Core.Interop;

/// <summary>
/// Property identifiers for KSPROPSETID_BtAudio.
/// </summary>
/// <remarks>
/// See docs/PITFALLS.md C1. These are the oneshot properties that Bluetooth audio devices
/// must expose per WHQL requirement Device.Audio.Bluetooth.AtleastOneProfileSupport.
/// </remarks>
public enum KsBtAudioProperty : uint
{
    /// <summary>Connect the device (oneshot).</summary>
    OneshotReconnect = 0x00000000,

    /// <summary>Disconnect the device (oneshot).</summary>
    OneshotDisconnect = 0x00000001,
}
