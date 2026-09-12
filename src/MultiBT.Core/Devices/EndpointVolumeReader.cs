using NAudio.CoreAudioApi;

namespace MultiBT.Core.Devices;

/// <summary>
/// Reads and writes a Windows endpoint's own volume and mute state.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately separate from the per-device chain gain. Three different factors decide how
/// loud a mirrored device actually is, and confusing them is why "the volumes don't match" is hard
/// to reason about:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>The captured signal already carries the SOURCE endpoint's volume.</b> WASAPI loopback captures
/// the stream after the source's endpoint volume is applied, so turning the system volume up or down
/// automatically changes every mirrored output together. Nothing needs to be implemented for that —
/// it is free, and it is why "follow the main device's volume" needs no extra mode.
/// </description></item>
/// <item><description>
/// <b>Each output endpoint has its own volume.</b> A Bluetooth speaker at 100 % and a DAC at 61 %
/// will be audibly different at the same chain gain. That difference is <i>known and measurable</i>,
/// so it is read out, shown per device, and adjusted per device.
/// </description></item>
/// <item><description>
/// <b>Each device's speaker/amp sensitivity is unknown.</b> No API exposes it. This is the part that
/// can only be fixed by ear, which is why the per-device slider is the final authority: a reading
/// explains a mismatch, it cannot settle one.
/// </description></item>
/// </list>
/// </remarks>
public static class EndpointVolumeReader
{
    /// <summary>Reads an endpoint's volume (0..1) and mute state.</summary>
    /// <returns><c>false</c> when the endpoint cannot be resolved or exposes no volume control.</returns>
    public static bool TryRead(
        DeviceManager devices,
        string endpointId,
        out double volumeScalar,
        out bool muted)
    {
        volumeScalar = double.NaN;
        muted = false;

        if (devices is null || string.IsNullOrWhiteSpace(endpointId))
        {
            return false;
        }

        if (!devices.TryResolveDevice(endpointId, out MMDevice? device) || device is null)
        {
            return false;
        }

        using (device)
        {
            try
            {
                AudioEndpointVolume volume = device.AudioEndpointVolume;

                try
                {
                    volumeScalar = volume.MasterVolumeLevelScalar;
                    muted = volume.Mute;
                    return true;
                }
                finally
                {
                    volume.Dispose();
                }
            }
            catch (Exception)
            {
                // A capture-only or virtual endpoint may expose no volume control at all.
                return false;
            }
        }
    }

    /// <summary>Sets an endpoint's volume and/or unmutes it.</summary>
    /// <returns><c>false</c> when the endpoint cannot be resolved or written to.</returns>
    public static bool TryWrite(
        DeviceManager devices,
        string endpointId,
        double? volumeScalar = null,
        bool? muted = null)
    {
        if (devices is null || string.IsNullOrWhiteSpace(endpointId))
        {
            return false;
        }

        if (!devices.TryResolveDevice(endpointId, out MMDevice? device) || device is null)
        {
            return false;
        }

        using (device)
        {
            try
            {
                AudioEndpointVolume volume = device.AudioEndpointVolume;

                try
                {
                    if (volumeScalar is double scalar)
                    {
                        volume.MasterVolumeLevelScalar = (float)Math.Clamp(scalar, 0.0, 1.0);
                    }

                    if (muted is bool isMuted)
                    {
                        volume.Mute = isMuted;
                    }

                    return true;
                }
                finally
                {
                    volume.Dispose();
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
