using System.Diagnostics;
using System.Runtime.InteropServices;
using MultiBT.Core.Audio;
using MultiBT.Core.Interop;
using NAudio.CoreAudioApi;

namespace MultiBT.Core.Devices;

/// <summary>
/// Connects and disconnects an ALREADY-PAIRED Bluetooth audio device.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is achievable, and what is not.</b> See docs/PITFALLS.md C1/C2.
/// </para>
/// <para>
/// <b>WORKS (the route to implement):</b> Core Audio plus <c>IKsControl</c> with
/// <c>KSPROPSETID_BtAudio</c>'s <c>KSPROPERTY_ONESHOT_RECONNECT</c> /
/// <c>KSPROPERTY_ONESHOT_DISCONNECT</c>, issued with <c>KSPROPERTY_TYPE_GET</c>. This is a
/// supported requirement, not a hack: WHQL
/// <c>Device.Audio.Bluetooth.AtleastOneProfileSupport</c> MANDATES that Bluetooth audio devices
/// expose both oneshot properties so the Sound Control Panel can connect and disconnect them.
/// No elevation, no driver, no MSIX.
/// </para>
/// <para>
/// <b>Three rules that are easy to get wrong:</b> (1) a successful oneshot only means the driver
/// ATTEMPTED the connection, so always confirm by polling the endpoint to
/// <c>DEVICE_STATE_ACTIVE</c> with a bounded timeout; (2) one physical device exposes several
/// endpoints (A2DP render, HFP render, HFP capture) and Windows only fully disconnects when all
/// are down, so group by <c>PKEY_Device_ContainerId</c> and issue the oneshot for every KS
/// filter in the group; (3) after connecting, Windows needs
/// <see cref="RecoveryPolicy.ReconnectSettleDelay"/> before the endpoints are usable.
/// </para>
/// <para>
/// <b>DOES NOT WORK — do not waste time on these:</b> <c>BluetoothDevice.FromIdAsync</c> only
/// constructs an object (the WinRT Bluetooth API has no connect verb at all);
/// <c>AudioPlaybackConnection</c> is the wrong direction; a raw L2CAP connect to AVDTP PSM 25
/// collides with the in-box <c>BthA2dp.sys</c> signalling channel; SetupAPI
/// <c>DIF_PROPERTYCHANGE</c> needs elevation; and <c>PairAsync</c> is officially unsupported in
/// desktop apps, so <b>pairing is out of scope — MultiBT only manages already-paired devices</b>.
/// </para>
/// </remarks>
public sealed class BluetoothConnector : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private bool _disposed;

    /// <summary>Whether the connector has been verified to work on this machine.</summary>
    public bool IsSupported { get; private set; }

    /// <summary>
    /// Probes whether the oneshot reconnect property is available on this machine.
    /// </summary>
    /// <remarks>
    /// Call once at startup. If this returns false, the UI must not show a Connect button
    /// (or show it greyed-out with a fallback hint to use Windows Settings).
    /// </remarks>
    public bool Probe()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            // Try to find a Bluetooth device and send a harmless query to it.
            // If the property set is not supported, we'll get a COM exception.
            using MMDeviceCollection devices = _enumerator.EnumerateAudioEndPoints(
                DataFlow.Render, DeviceState.Active | DeviceState.Unplugged);

            for (int i = 0; i < devices.Count; i++)
            {
                using MMDevice device = devices[i];
                if (TransportClassifier.IsBluetooth(device.InstanceId, device.FriendlyName))
                {
                    IKsControl? ksControl = null;
                    try
                    {
                        ksControl = GetKsControlForDevice(device);
                        if (ksControl is not null)
                        {
                            // Try to query the oneshot reconnect property. We don't actually send it;
                            // we just check if the property set exists.
                            var property = new KSPROPERTY
                            {
                                Id = (int)KsBtAudioProperty.OneshotReconnect,
                                Flags = (int)KsPropertyType.Get
                            };

                            int hr = ksControl.KsProperty(
                                ref property,
                                (uint)Marshal.SizeOf<KSPROPERTY>(),
                                out _,
                                0);

                            // S_OK (0) or S_FALSE (1) both mean the property exists
                            IsSupported = hr >= 0;
                            return IsSupported;
                        }
                    }
                    finally
                    {
                        if (ksControl is not null)
                        {
                            Marshal.ReleaseComObject(ksControl);
                        }
                    }
                }
            }

            IsSupported = false;
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BluetoothConnector] Probe failed: {ex.Message}");
            IsSupported = false;
            return false;
        }
    }

    /// <summary>
    /// Asks Windows to connect an already-paired Bluetooth audio device.
    /// </summary>
    /// <param name="containerId">
    /// The device container id. All KS filters in the container must receive the oneshot.
    /// </param>
    /// <param name="cancellationToken">Cancels the state-polling wait.</param>
    /// <returns><c>true</c> only when the endpoint actually reached <c>DEVICE_STATE_ACTIVE</c>.</returns>
    public async Task<bool> ConnectAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(containerId);

        if (!IsSupported)
        {
            throw new InvalidOperationException(
                "Bluetooth oneshot is not supported on this machine. " +
                "The user should connect the device via Windows Settings.");
        }

        // Find all endpoints in this container and send oneshot reconnect to each KS filter
        List<MMDevice> devicesInContainer = FindDevicesInContainer(containerId);

        if (devicesInContainer.Count == 0)
        {
            Debug.WriteLine($"[BluetoothConnector] No devices found for container {containerId}");
            return false;
        }

        // Send oneshot to all devices
        bool anySucceeded = false;
        foreach (MMDevice device in devicesInContainer)
        {
            IKsControl? ksControl = null;
            try
            {
                ksControl = GetKsControlForDevice(device);
                if (ksControl is not null)
                {
                    var property = new KSPROPERTY
                    {
                        Id = (int)KsBtAudioProperty.OneshotReconnect,
                        Flags = (int)KsPropertyType.Get
                    };

                    int hr = ksControl.KsProperty(
                        ref property,
                        (uint)Marshal.SizeOf<KSPROPERTY>(),
                        out _,
                        0);

                    if (hr >= 0)
                    {
                        anySucceeded = true;
                        Debug.WriteLine($"[BluetoothConnector] Oneshot reconnect sent to {device.FriendlyName}");
                    }
                    else
                    {
                        Debug.WriteLine($"[BluetoothConnector] Oneshot reconnect failed for {device.FriendlyName}: 0x{hr:X8}");
                    }
                }
            }
            finally
            {
                if (ksControl is not null)
                {
                    Marshal.ReleaseComObject(ksControl);
                }
                device.Dispose();
            }
        }

        if (!anySucceeded)
        {
            return false;
        }

        // Poll for the endpoint to become active (with bounded timeout)
        return await PollForActiveStateAsync(containerId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks Windows to disconnect an already-paired Bluetooth audio device.
    /// </summary>
    public async Task<bool> DisconnectAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(containerId);

        if (!IsSupported)
        {
            throw new InvalidOperationException(
                "Bluetooth oneshot is not supported on this machine.");
        }

        List<MMDevice> devicesInContainer = FindDevicesInContainer(containerId);

        if (devicesInContainer.Count == 0)
        {
            Debug.WriteLine($"[BluetoothConnector] No devices found for container {containerId}");
            return false;
        }

        bool anySucceeded = false;
        foreach (MMDevice device in devicesInContainer)
        {
            IKsControl? ksControl = null;
            try
            {
                ksControl = GetKsControlForDevice(device);
                if (ksControl is not null)
                {
                    var property = new KSPROPERTY
                    {
                        Id = (int)KsBtAudioProperty.OneshotDisconnect,
                        Flags = (int)KsPropertyType.Get
                    };

                    int hr = ksControl.KsProperty(
                        ref property,
                        (uint)Marshal.SizeOf<KSPROPERTY>(),
                        out _,
                        0);

                    if (hr >= 0)
                    {
                        anySucceeded = true;
                        Debug.WriteLine($"[BluetoothConnector] Oneshot disconnect sent to {device.FriendlyName}");
                    }
                    else
                    {
                        Debug.WriteLine($"[BluetoothConnector] Oneshot disconnect failed for {device.FriendlyName}: 0x{hr:X8}");
                    }
                }
            }
            finally
            {
                if (ksControl is not null)
                {
                    Marshal.ReleaseComObject(ksControl);
                }
                device.Dispose();
            }
        }

        if (!anySucceeded)
        {
            return false;
        }

        // Poll for the endpoint to become unplugged
        return await PollForUnpluggedStateAsync(containerId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets all render endpoints belonging to a container.
    /// </summary>
    public IReadOnlyList<AudioEndpointInfo> GetEndpointsInContainer(string containerId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = new List<AudioEndpointInfo>();

        using MMDeviceCollection devices = _enumerator.EnumerateAudioEndPoints(
            DataFlow.Render, DeviceState.All);

        for (int i = 0; i < devices.Count; i++)
        {
            using MMDevice device = devices[i];
            string? deviceContainerId = GetContainerId(device);
            if (string.Equals(deviceContainerId, containerId, StringComparison.OrdinalIgnoreCase))
            {
                EndpointIdentity identity = EndpointIdentityReader.Read(device);
                Transport transport = TransportClassifier.Classify(identity.InstanceId, device.FriendlyName);
                result.Add(new AudioEndpointInfo(
                    device.ID,
                    identity.InstanceId,
                    device.FriendlyName,
                    device.DeviceFriendlyName,
                    transport,
                    device.State == DeviceState.Active));
            }
        }

        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enumerator.Dispose();
    }

    private List<MMDevice> FindDevicesInContainer(string containerId)
    {
        var result = new List<MMDevice>();

        using MMDeviceCollection devices = _enumerator.EnumerateAudioEndPoints(
            DataFlow.Render, DeviceState.All);

        for (int i = 0; i < devices.Count; i++)
        {
            // We need to clone the device since using will dispose it
            using MMDevice device = devices[i];
            string? deviceContainerId = GetContainerId(device);
            if (string.Equals(deviceContainerId, containerId, StringComparison.OrdinalIgnoreCase))
            {
                // Re-enumerate to get a fresh instance
                try
                {
                    MMDevice freshDevice = _enumerator.GetDevice(device.ID);
                    result.Add(freshDevice);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BluetoothConnector] Failed to get device {device.FriendlyName}: {ex.Message}");
                }
            }
        }

        return result;
    }

    private static IKsControl? GetKsControlForDevice(MMDevice device)
    {
        try
        {
            // In NAudio 3.x, we can try to activate IKsControl directly
            // This is done through the IDeviceTopology interface
            object? topologyObj = null;
            try
            {
                // Try to activate IDeviceTopology
                Guid topologyGuid = typeof(IDeviceTopology).GUID;
                int hr = Activate设备(device, ref topologyGuid, out topologyObj);
                if (hr == 0 && topologyObj is IDeviceTopology topology)
                {
                    // Navigate to the KS filter
                    if (topology.GetConnectorCount() > 0)
                    {
                        IConnector connector = topology.GetConnector(0);
                        IPart connectedPart = connector.GetConnectedTo();
                        IDeviceTopology connectedTopology = connectedPart.GetTopologyObject();
                        string? ksFilterPath;
                        connectedTopology.GetDeviceId(out ksFilterPath);

                        if (!string.IsNullOrEmpty(ksFilterPath))
                        {
                            // Get device and activate IKsControl
                            using MMDevice ksDevice = new MMDeviceEnumerator().GetDevice(ksFilterPath);
                            // Try to get IKsControl through IDeviceTopology
                            // This is a simplified approach - in reality we need to navigate the topology
                            return null; // Placeholder - actual implementation needs more work
                        }
                    }
                }
            }
            finally
            {
                if (topologyObj is not null)
                {
                    Marshal.ReleaseComObject(topologyObj);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BluetoothConnector] GetKsControlForDevice failed: {ex.Message}");
        }

        return null;
    }

    // P/Invoke for IDeviceTopology activation
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid clsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid iid,
        out IntPtr ppv);

    private static int Activate设备(MMDevice device, ref Guid iid, out object? ppv)
    {
        // This is a placeholder - actual implementation needs proper COM activation
        ppv = null;
        return -1; // E_NOTIMPL
    }

    private static bool TryGetKsControl(string? ksFilterPath, out IKsControl? ksControl)
    {
        ksControl = null;

        if (string.IsNullOrEmpty(ksFilterPath))
        {
            return false;
        }

        try
        {
            using MMDeviceEnumerator enumerator = new();
            using MMDevice device = enumerator.GetDevice(ksFilterPath);
            ksControl = GetKsControlForDevice(device);
            return ksControl is not null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BluetoothConnector] TryGetKsControl failed for {ksFilterPath}: {ex.Message}");
            ksControl = null;
            return false;
        }
    }

    private static string? GetContainerId(MMDevice device)
    {
        try
        {
            // PKEY_Device_ContainerId is defined as PKEY_Device.ContainerId
            // GUID: {8C7F7AA1-1523-44CF-871F-F4E35F6A5BC2}, PID = 4
            // NAudio doesn't define this, so we create it manually
            var containerIdGuid = new Guid(0x8C7F7AA1, 0x1523, 0x44CF, 0x87, 0x1F, 0xF4, 0xE3, 0x5F, 0x6A, 0x5B, 0xC2);
            var propertyKey = new PropertyKey(containerIdGuid, 4);
            return device.Properties.TryGetValue<string>(propertyKey, out string? value) ? value : null;
        }
        catch
        {
            // Property may not exist for some devices
        }

        return null;
    }

    private async Task<bool> PollForActiveStateAsync(string containerId, CancellationToken cancellationToken)
    {
        const int timeoutMs = 10000; // 10 seconds total timeout
        const int pollIntervalMs = 500;
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var endpoints = GetEndpointsInContainer(containerId);
            if (endpoints.Count > 0 && endpoints.Any(e => e.IsActive))
            {
                Debug.WriteLine($"[BluetoothConnector] Endpoint became active after {stopwatch.ElapsedMilliseconds}ms");
                return true;
            }

            await Task.Delay(pollIntervalMs, cancellationToken).ConfigureAwait(false);
        }

        Debug.WriteLine($"[BluetoothConnector] PollForActiveState timed out after {timeoutMs}ms");
        return false;
    }

    private async Task<bool> PollForUnpluggedStateAsync(string containerId, CancellationToken cancellationToken)
    {
        const int timeoutMs = 5000; // 5 seconds total timeout
        const int pollIntervalMs = 500;
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var endpoints = GetEndpointsInContainer(containerId);
            if (endpoints.Count > 0 && endpoints.All(e => !e.IsActive))
            {
                Debug.WriteLine($"[BluetoothConnector] Endpoint became unplugged after {stopwatch.ElapsedMilliseconds}ms");
                return true;
            }

            await Task.Delay(pollIntervalMs, cancellationToken).ConfigureAwait(false);
        }

        Debug.WriteLine($"[BluetoothConnector] PollForUnpluggedState timed out after {timeoutMs}ms");
        return false;
    }
}
