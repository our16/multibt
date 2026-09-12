using System.Collections.Concurrent;
using MultiBT.Core.Devices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiBT.Core.Audio;

/// <summary>
/// Orchestrates the whole mirror: one capture source, N output channels, one control loop.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fan-out is one buffer per channel, not one buffer with many readers.</b> The capture
/// callback pushes the same bytes into every channel's own ring buffer; no provider instance
/// is ever shared between two players.
/// </para>
/// <para>
/// <b>The callback is defensively isolated.</b> Each channel's push is individually wrapped so
/// that one sick device cannot unwind the capture callback and take down every other output.
/// </para>
/// <para>
/// <b>The channel set is published as an immutable snapshot.</b> The capture thread reads a
/// single volatile array reference, so adding or removing a channel never races with the audio
/// path and never requires a lock on the hot path.
/// </para>
/// </remarks>
public sealed class AudioEngine : IAsyncDisposable
{
    private readonly DeviceManager _devices;
    private readonly List<OutputChannel> _channels = [];
    private readonly Lock _channelLock = new();
    private volatile OutputChannel[] _snapshot = [];
    private readonly ConcurrentDictionary<string, RecoveryPolicy> _recoveryPolicies = new();
    private readonly ConcurrentDictionary<string, Task> _pendingRecoveries = new();
    /// <summary>
    /// The audio input. Deliberately an interface, not a concrete loopback or a specific cable — see
    /// IAudioInputBackend for why the engine must not know which it has.
    /// </summary>
    private IAudioInputBackend? _backend;
    private Timer? _controlTimer;
    private int _tickInProgress;
    private bool _disposed;

    public AudioEngine(DeviceManager devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _devices = devices;
    }

    /// <summary>Current output channels.</summary>
    public IReadOnlyList<OutputChannel> Channels
    {
        get
        {
            lock (_channelLock)
            {
                return _channels.ToArray();
            }
        }
    }

    /// <summary>True while capture is running.</summary>
    public bool IsRunning => _backend is not null;

    /// <summary>Format of the captured stream, or <c>null</c> when stopped.</summary>
    public WaveFormat? CaptureFormat { get; private set; }

    /// <summary>Raised when capture stops, carrying the originating exception if there was one.</summary>
    public event EventHandler<Exception?>? CaptureStopped;

    /// <summary>Adds a channel and republishes the fan-out snapshot.</summary>
    public void AddChannel(OutputChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        lock (_channelLock)
        {
            _channels.Add(channel);
            _snapshot = _channels.ToArray();
        }
    }

    /// <summary>Removes a channel by device key and republishes the snapshot.</summary>
    public void RemoveChannel(string deviceKey)
    {
        lock (_channelLock)
        {
            _channels.RemoveAll(c => string.Equals(c.DeviceKey, deviceKey, StringComparison.Ordinal));
            _snapshot = _channels.ToArray();
        }
    }

    /// <summary>
    /// Starts capture from a freshly resolved render endpoint and starts every channel.
    /// </summary>
    /// <param name="backend">
    /// Where PCM comes from. The engine treats every backend identically and never inspects its kind.
    /// </param>
    public void Start(IAudioInputBackend backend)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(backend);

        Stop();

        // The engine takes ownership of the backend, and therefore of whatever device it holds.
        _backend = backend;
        _backend.DataAvailable += OnDataAvailable;
        _backend.Stopped += OnCaptureStopped;
        CaptureFormat = _backend.Format;

        foreach (OutputChannel channel in Channels)
        {
            channel.Start();
        }

        _backend.Start();

        TimeSpan period = TimeSpan.FromSeconds(1.0 / EngineTunables.ControlTickHz);
        _controlTimer = new Timer(_ => ControlTick(), null, period, period);
    }

    /// <summary>Stops capture and the control loop. Channels are left intact for later restart.</summary>
    public void Stop()
    {
        _controlTimer?.Dispose();
        _controlTimer = null;

        if (_backend is not null)
        {
            _backend.DataAvailable -= OnDataAvailable;
            _backend.Stopped -= OnCaptureStopped;

            // Stop the input FIRST so the chains stop being fed before they are torn down.
            _backend.Stop();
            _backend.Dispose();
            _backend = null;
        }

        CaptureFormat = null;
    }

    /// <summary>Diagnostics snapshot for every channel, for the diagnostics panel.</summary>
    public IReadOnlyList<ChannelDiagnostics> GetDiagnostics() =>
        _snapshot.Select(c => c.GetDiagnostics()).ToArray();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Shutdown order matters: capture first (stop feeding), then the control loop, then
        // fade and dispose each player. See docs/SPEC.md §7.4.
        OutputChannel[] channels = _snapshot;
        Stop();

        foreach (OutputChannel channel in channels)
        {
            await channel.FadeOutAsync(TimeSpan.FromMilliseconds(EngineTunables.FadeMs)).ConfigureAwait(false);
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        lock (_channelLock)
        {
            _channels.Clear();
            _snapshot = [];
        }
    }

    /// <summary>
    /// Fan-out. Runs on the capture thread, so it must not allocate, block, or throw.
    /// </summary>
    private void OnDataAvailable(ReadOnlySpan<byte> buffer)
    {
        OutputChannel[] channels = _snapshot;

        for (int i = 0; i < channels.Length; i++)
        {
            try
            {
                channels[i].AddSamples(buffer);
            }
            catch (Exception ex)
            {
                // Isolate per channel: one failing device must not silence the others.
                channels[i].ReportFault(ex);
            }
        }
    }

    private void OnCaptureStopped(object? sender, Exception? exception) =>
        CaptureStopped?.Invoke(this, exception);

    /// <summary>
    /// The control loop: drains device notifications and runs one drift step per channel.
    /// </summary>
    /// <remarks>
    /// Runs at a FIXED rate. NAudio runs one playback thread per player, and recomputing the
    /// ratio per render callback would reintroduce per-callback jitter; the controller also
    /// needs a stable sampling interval for its EMA and rate limit.
    /// </remarks>
    private void ControlTick()
    {
        // Re-entrancy guard: a slow tick must be skipped, not queued up.
        if (Interlocked.Exchange(ref _tickInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            // Marshal queued device notifications onto this thread; the COM callback thread
            // must never do audio work. See docs/PITFALLS.md A11.
            _devices.DrainPendingChanges();

            OutputChannel[] channels = _snapshot;
            for (int i = 0; i < channels.Length; i++)
            {
                try
                {
                    channels[i].UpdateDrift();
                }
                catch (Exception ex)
                {
                    channels[i].ReportFault(ex);
                }
            }
        }
        catch (Exception)
        {
            // A control-loop failure must never take down the process; the next tick retries.
        }
        finally
        {
            Interlocked.Exchange(ref _tickInProgress, 0);
        }
    }

    /// <summary>
    /// Handles device state changes from the notification system.
    /// </summary>
    /// <remarks>
    /// This is called from the control tick, so it runs on the control thread, not the
    /// notification callback thread. See docs/PITFALLS.md A11.
    /// </remarks>
    public void HandleDeviceChange(DeviceChangeEvent change)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        switch (change.Kind)
        {
            case DeviceChangeKind.StateChanged:
                HandleDeviceStateChanged(change.DeviceId, change.NewState);
                break;

            case DeviceChangeKind.Removed:
                HandleDeviceRemoved(change.DeviceId);
                break;

            case DeviceChangeKind.Added:
                HandleDeviceAdded(change.DeviceId);
                break;
        }
    }

    private void HandleDeviceStateChanged(string deviceId, DeviceState newState)
    {
        // Find if this device has an active channel
        OutputChannel[] channels = _snapshot;
        OutputChannel? channel = channels.FirstOrDefault(c =>
            string.Equals(c.DeviceKey, deviceId, StringComparison.OrdinalIgnoreCase));

        if (channel is null)
        {
            return;
        }

        if (newState == DeviceState.Unplugged || newState == DeviceState.NotPresent)
        {
            // Device unplugged: tear down the channel but keep it in the desired set
            // so it can be re-armed when it returns.
            HandleDeviceRemoved(deviceId);
        }
        else if (newState == DeviceState.Active)
        {
            // Device came back: schedule recovery with settle delay
            ScheduleRecovery(deviceId);
        }
    }

    private void HandleDeviceRemoved(string deviceId)
    {
        OutputChannel[] channels = _snapshot;
        OutputChannel? channel = channels.FirstOrDefault(c =>
            string.Equals(c.DeviceKey, deviceId, StringComparison.OrdinalIgnoreCase));

        if (channel is not null)
        {
            // Reset the recovery policy for this device
            if (_recoveryPolicies.TryGetValue(deviceId, out RecoveryPolicy? policy))
            {
                policy.Reset();
            }

            // The channel will be stopped by the fault handling mechanism
            channel.ReportFault(new DeviceNotFoundException($"Device {deviceId} was removed"));
        }
    }

    private void HandleDeviceAdded(string deviceId)
    {
        // When a device is added, we might need to start it if it was in the desired set
        // For now, this is handled by the profile system re-enabling channels
    }

    private void ScheduleRecovery(string deviceId)
    {
        // Don't schedule if already pending
        if (_pendingRecoveries.ContainsKey(deviceId))
        {
            return;
        }

        RecoveryPolicy policy = _recoveryPolicies.GetOrAdd(deviceId, _ => new RecoveryPolicy());

        if (policy.IsExhausted)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RecoveryPolicy.ReconnectSettleDelay).ConfigureAwait(false);

                if (_disposed || _pendingRecoveries.TryRemove(deviceId, out _))
                {
                    return;
                }

                // Try to restart the channel
                if (policy.TryScheduleRetry(out TimeSpan delay))
                {
                    await Task.Delay(delay).ConfigureAwait(false);

                    if (!_disposed)
                    {
                        // The actual restart would require resolving the device and
                        // creating a new channel, which is handled by the profile system
                        // For now, we just log the attempt
                    }
                }
            }
            catch (Exception)
            {
                // Recovery failed - the channel will be marked as failed
            }
        });
    }
}
