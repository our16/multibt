using System.Collections.Concurrent;
using MultiBT.Core.Audio;
using NAudio.CoreAudioApi;

namespace MultiBT.Core.Devices;

/// <summary>Kind of endpoint change observed.</summary>
public enum DeviceChangeKind
{
    Added = 0,
    Removed = 1,
    StateChanged = 2,
    DefaultChanged = 3,
    PropertyChanged = 4,
}

/// <summary>A device change, already marshalled off the notification callback thread.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="DeviceId">
/// The affected endpoint id. <b>OPAQUE</b> — compare it, or pass it to
/// <see cref="DeviceManager.TryResolveDevice"/>. Never parse it.
/// </param>
/// <param name="NewState">New endpoint state for <see cref="DeviceChangeKind.StateChanged"/>.</param>
public sealed record DeviceChangeEvent(DeviceChangeKind Kind, string DeviceId, DeviceState NewState);

/// <summary>
/// Enumerates render endpoints, classifies them, and reports changes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Notifications are an event API in NAudio 3.x.</b> <c>IMMNotificationClient</c> and
/// <c>RegisterEndpointNotificationCallback</c> are <c>internal</c> now; the supported surface is
/// <c>MMDeviceNotificationClient</c> obtained from <c>MMDeviceEnumerator.CreateNotificationClient</c>.
/// See docs/PITFALLS.md A11.
/// </para>
/// <para>
/// <b>Callbacks are marshalled to a queue and never do audio work inline.</b> With
/// <c>useSynchronizationContext: false</c> the handlers run on an audio/COM thread, and NAudio
/// warns explicitly that they must be non-blocking and must not call back into the audio stack
/// (for example disposing a player) or they risk deadlock. This class therefore only enqueues;
/// the engine drains the queue from its own control tick.
/// </para>
/// </remarks>
public sealed class DeviceManager : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient _notifications;
    private readonly ConcurrentQueue<DeviceChangeEvent> _pending = new();
    private bool _disposed;

    public DeviceManager()
    {
        _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);

        _notifications.DeviceAdded += (_, e) => Enqueue(DeviceChangeKind.Added, e.DeviceId, DeviceState.Active);
        _notifications.DeviceRemoved += (_, e) => Enqueue(DeviceChangeKind.Removed, e.DeviceId, DeviceState.NotPresent);
        _notifications.DeviceStateChanged += (_, e) => Enqueue(DeviceChangeKind.StateChanged, e.DeviceId, e.NewState);
        _notifications.DefaultDeviceChanged += (_, e) => Enqueue(DeviceChangeKind.DefaultChanged, e.DeviceId, DeviceState.Active);

        // PropertyValueChanged fires on EVERY volume change — high frequency. Record nothing
        // expensive here; the engine ignores this kind unless it is actively waiting.
        _notifications.PropertyValueChanged += (_, e) => Enqueue(DeviceChangeKind.PropertyChanged, e.DeviceId, DeviceState.Active);
    }

    /// <summary>
    /// Raised when changes are drained by <see cref="DrainPendingChanges"/>. Always on the
    /// caller's thread, never on the COM callback thread.
    /// </summary>
    public event EventHandler<DeviceChangeEvent>? DeviceChanged;

    /// <summary>
    /// Enumerates render endpoints.
    /// </summary>
    /// <param name="includeInactive">
    /// When true (the default) uses <c>DeviceState.All</c>. This is REQUIRED to see a
    /// paired-but-disconnected Bluetooth endpoint, which Windows reports as
    /// <c>Unplugged</c> rather than <c>Active</c> — enumerating only
    /// <c>DeviceState.Active</c> is the classic cause of "my speaker vanished from the list".
    /// See docs/SPEC.md §4.1.
    /// </param>
    /// <remarks>
    /// The underlying collection is a snapshot whose indexer materialises a NEW
    /// <c>MMDevice</c> wrapper on every access, so each one is disposed immediately here and
    /// nothing from this call is retained. Re-enumerate after any device change.
    /// </remarks>
    public IReadOnlyList<AudioEndpointInfo> EnumerateRenderEndpoints(bool includeInactive = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        DeviceState mask = includeInactive ? DeviceState.All : DeviceState.Active;
        var result = new List<AudioEndpointInfo>();

        using MMDeviceCollection collection = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, mask);

        for (int i = 0; i < collection.Count; i++)
        {
            using MMDevice device = collection[i];
            result.Add(Describe(device));
        }

        return result;
    }

    /// <summary>Resolves a FRESH <c>MMDevice</c> for an endpoint id.</summary>
    /// <remarks>
    /// Always call this at activation time. A cached <c>MMDevice</c> dies on sleep/resume even
    /// though the endpoint still enumerates. See docs/PITFALLS.md C5.
    /// </remarks>
    public bool TryResolveDevice(string endpointId, out MMDevice? device)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(endpointId);

        try
        {
            device = _enumerator.GetDevice(endpointId);
            return device is not null;
        }
        catch (Exception)
        {
            // The endpoint may have been removed between the notification and this call.
            device = null;
            return false;
        }
    }

    /// <summary>Resolves the current default render endpoint, or returns false when there is none.</summary>
    public bool TryGetDefaultRenderDevice(out MMDevice? device)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out device))
        {
            return true;
        }

        device = null;
        return false;
    }

    /// <summary>
    /// Drains queued changes and raises <see cref="DeviceChanged"/> for each.
    /// </summary>
    /// <remarks>
    /// Call this from the engine's control tick or from the UI dispatcher — never from a
    /// notification callback.
    /// </remarks>
    public int DrainPendingChanges()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int drained = 0;
        while (_pending.TryDequeue(out DeviceChangeEvent? change))
        {
            drained++;
            DeviceChanged?.Invoke(this, change);
        }

        return drained;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifications.Dispose();   // unsubscribes; the client also keeps the enumerator alive
        _enumerator.Dispose();
    }

    private static AudioEndpointInfo Describe(MMDevice device)
    {
        // Identity comes from EndpointIdentityReader, NOT from PKEY_Device_InstanceId directly:
        // that property is absent from the endpoint property store on Windows 11 24H2, so a
        // direct read silently yields empty and every device classifies as Other.
        // See docs/PITFALLS.md D3.
        EndpointIdentity identity = EndpointIdentityReader.Read(device);

        Transport transport = TransportClassifier.Classify(identity.InstanceId, device.FriendlyName);

        return new AudioEndpointInfo(
            device.ID,
            identity.InstanceId,
            device.FriendlyName,
            device.DeviceFriendlyName,
            transport,
            device.State == DeviceState.Active);
    }

    private void Enqueue(DeviceChangeKind kind, string deviceId, DeviceState state) =>
        _pending.Enqueue(new DeviceChangeEvent(kind, deviceId, state));
}
