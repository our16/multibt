using System.Collections.ObjectModel;
using System.Windows.Threading;
using MultiBT.Core.Audio;
using MultiBT.Core.Config;
using MultiBT.Core.Devices;
using MultiBT.Core.Sync;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiBT.App.ViewModels;

/// <summary>
/// Drives the main window: enumerates endpoints, joins them to stored profiles, and owns the
/// audio engine's lifetime.
/// </summary>
/// <remarks>
/// <para>
/// <b>Verified in this repository:</b> settings load/save, endpoint enumeration and transport
/// classification, profile joining, and the latency summary. These are covered by unit tests
/// under tests/MultiBT.Core.Tests.
/// </para>
/// <para>
/// <b>NOT verified on real hardware:</b> the actual mirror (start/stop). The plumbing is wired
/// to <see cref="AudioEngine"/>, but no audio has been played through it in this repository, so
/// treat <see cref="Start"/> as the M2/M3 starting point rather than a finished feature.
/// </para>
/// </remarks>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DeviceManager _devices = new();
    private readonly ProfileStore _store = new();
    private readonly DispatcherTimer _diagnosticsTimer;

    /// <summary>Coalesces slider drags into one endpoint-volume write per burst.</summary>
    private readonly DispatcherTimer _volumeWriteTimer;

    /// <summary>Endpoint volumes waiting to be written, keyed by endpoint id.</summary>
    private readonly Dictionary<string, double> _pendingVolumeWrites = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set while mirroring a read value back, so it does not queue another write.</summary>
    private bool _suppressVolumeWrites;

    /// <summary>
    /// Endpoints resolved for the running engine. Kept alive deliberately: an
    /// <c>MMDevice</c> passed to a player must outlive that player, and a cached one must not
    /// be reused after sleep/resume — so these are created per start and released per stop.
    /// </summary>
    private readonly Dictionary<string, MMDevice> _liveChannelDevices = new(StringComparer.Ordinal);

    private string? _defaultRenderEndpointId;
    private MultiBtSettings _settings;
    private AudioEngine? _engine;
    private MMDevice? _sourceDevice;
    private DeviceViewModel? _primaryDevice;
    private string _statusText = "Idle";
    private string? _settingsWarning;
    private bool _isRunning;
    private bool _isPaused;
    private double _systemLatencyMs;
    private bool _exceedsLipSync;
    private string _diagnosticsSummary = "—";

    public MainViewModel()
    {
        _settings = _store.Load(out string? warning);
        _settingsWarning = warning;

        _diagnosticsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _diagnosticsTimer.Tick += (_, _) => RefreshDiagnostics();

        _volumeWriteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _volumeWriteTimer.Tick += (_, _) => FlushEndpointVolumeWrites();

        RefreshDevices();
    }

    /// <summary>Live endpoints joined to their stored profiles.</summary>
    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    /// <summary>Engine-wide requested latency, the base of the drift trough target.</summary>
    public int EngineLatencyMs
    {
        get => _settings.Engine.EngineLatencyMs;
        set
        {
            if (_settings.Engine.EngineLatencyMs == value)
            {
                return;
            }

            _settings.Engine.EngineLatencyMs = value;

            // Changing the global latency invalidates every measurement: compensation was
            // computed against the previous engine latency. See docs/SPEC.md §5.3.
            foreach (DeviceProfile device in _settings.Devices)
            {
                device.Latency.InvalidateMeasurement("global engine latency changed");
            }

            OnPropertyChanged();
            RecomputeCompensations();
            RaiseAllDevicesChanged();
        }
    }

    /// <summary>Which "synchronised" means for this session.</summary>
    public SyncMode Mode
    {
        get => _settings.Profiles.FirstOrDefault(p => p.Id == _settings.ActiveProfileId)?.Mode ?? SyncMode.AlignAll;
        set
        {
            ProfileDefinition? profile = _settings.Profiles.FirstOrDefault(p => p.Id == _settings.ActiveProfileId);
            if (profile is null || profile.Mode == value)
            {
                return;
            }

            profile.Mode = value;
            OnPropertyChanged();
            RecomputeCompensations();
            RaiseAllDevicesChanged();
        }
    }

    /// <summary>System end-to-end latency implied by the current compensations, in ms.</summary>
    public double SystemLatencyMs
    {
        get => _systemLatencyMs;
        private set => SetProperty(ref _systemLatencyMs, value);
    }

    /// <summary>
    /// True when the system latency exceeds the ITU-R BT.1359-1 audio-lag detectability
    /// threshold, meaning video will look out of sync.
    /// </summary>
    public bool ExceedsLipSync
    {
        get => _exceedsLipSync;
        private set => SetProperty(ref _exceedsLipSync, value);
    }

    /// <summary>Human-readable system latency warning for the UI.</summary>
    public string SystemLatencySummary => _exceedsLipSync
        ? $"⚠ system latency {_systemLatencyMs:0} ms — exceeds the ~125 ms lip-sync threshold, video will look out of sync"
        : $"system latency {_systemLatencyMs:0} ms";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Non-null when the settings file was unreadable or written by a newer build.</summary>
    public string? SettingsWarning
    {
        get => _settingsWarning;
        private set
        {
            if (SetProperty(ref _settingsWarning, value))
            {
                OnPropertyChanged(nameof(HasSettingsWarning));
            }
        }
    }

    public bool HasSettingsWarning => !string.IsNullOrEmpty(_settingsWarning);

    public string DiagnosticsSummary
    {
        get => _diagnosticsSummary;
        private set => SetProperty(ref _diagnosticsSummary, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsNotRunning));
            }
        }
    }

    public bool IsNotRunning => !_isRunning;

    /// <summary>Re-enumerates endpoints and joins them to stored profiles.</summary>
    public void RefreshDevices()
    {
        // Preserve the primary across the rebuild.
        //
        // RefreshDevices REPLACES every DeviceViewModel, so the previous primary instance becomes
        // stale and its IsPrimary flag is lost with it — which is why the marker used to vanish on
        // refresh. Remember the ENDPOINT ID (not the view model) and re-bind it below, and fall back
        // to the persisted setting so the choice also survives a restart.
        string? previousPrimaryEndpointId = _primaryDevice?.Endpoint.EndpointId
                                            ?? _settings.Engine.SourceDeviceId;

        Devices.Clear();
        _primaryDevice = null;

        IReadOnlyList<AudioEndpointInfo> endpoints = _devices.EnumerateRenderEndpoints();
        System.Diagnostics.Debug.WriteLine($"[MultiBT] Enumerated {endpoints.Count} endpoints");

        foreach (AudioEndpointInfo endpoint in endpoints)
        {
            // Only show Bluetooth audio render endpoints
            if (endpoint.Transport != Transport.Bluetooth)
            {
                continue;
            }

            System.Diagnostics.Debug.WriteLine($"[MultiBT] Device: {endpoint.FriendlyName} - {endpoint.Transport} - Active: {endpoint.IsActive}");
            DeviceProfile? profile = null;
            DeviceIdentityMatcher.MatchKind match = DeviceIdentityMatcher.MatchKind.None;

            foreach (DeviceProfile candidate in _settings.Devices)
            {
                DeviceIdentityMatcher.MatchKind kind = DeviceIdentityMatcher.Match(candidate.Identities, endpoint);
                if (kind != DeviceIdentityMatcher.MatchKind.None)
                {
                    profile = candidate;
                    match = kind;
                    break;
                }
            }

            Devices.Add(new DeviceViewModel(endpoint, profile, match));
        }

        // Subscribe to enable/disable changes for real-time engine updates
        foreach (DeviceViewModel device in Devices)
        {
            // Route through a guarded async wrapper: an `async void` event handler that throws
            // takes the whole process down, and this one reconfigures live audio devices.
            device.IsEnabledChanged += (_, _) => _ = ToggleDeviceSafelyAsync(device);

            // Push VOLUME changes to the device's own Windows endpoint volume.
            //
            // This subscription is why the volume sliders work at all: earlier the slider updated only
            // the view model and the audio never changed, which is indistinguishable from "the slider
            // does nothing". Writes are debounced because a drag raises one change per pixel.
            device.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DeviceViewModel.EndpointVolume))
                {
                    QueueEndpointVolumeWrite(device);
                }
            };
        }

        // Adopt anything newly seen so it is persisted and gets a stable key.
        foreach (DeviceViewModel device in Devices.Where(d => !_settings.Devices.Contains(d.Profile)))
        {
            _settings.Devices.Add(device.Profile);
        }

        // Resolve the Windows default render device BEFORE choosing a default primary, because the
        // default primary IS that device.
        RefreshCaptureSourceState();

        // Offer a sensible primary (capture source) by DEFAULT rather than leaving every radio button
        // unchecked, and RE-BIND an existing choice so refreshing does not lose it.
        //
        // Priority: the previously-chosen endpoint, then the persisted setting, then the Windows
        // default render device — which is the only correct default, because the mirror captures what
        // the SOURCE endpoint is RENDERING and that is the endpoint Windows plays to.
        if (_primaryDevice is null)
        {
            DeviceViewModel? preferred =
                Devices.FirstOrDefault(d => MatchesEndpoint(d, previousPrimaryEndpointId))
                ?? Devices.FirstOrDefault(d => MatchesEndpoint(d, _defaultRenderEndpointId))
                ?? Devices.FirstOrDefault(d => d.Endpoint.IsActive);

            if (preferred is not null)
            {
                SetPrimaryDevice(preferred);
            }
        }

        // Read each device's own Windows endpoint volume so the UI can explain WHY two devices at the
        // same gain sound different, and so AutoMatchLevels has the data it needs.
        RefreshEndpointVolumes();

        RecomputeCompensations();
        RaiseAllDevicesChanged();

        StatusText = $"Found {Devices.Count} render endpoint(s), "
                     + $"{Devices.Count(d => d.Endpoint.Transport == Transport.Bluetooth)} Bluetooth.";
    }

    /// <summary>
    /// True when the chosen capture source is NOT the Windows default render device.
    /// </summary>
    /// <remarks>
    /// This is the most likely reason for "other devices make no sound", and it produces NO error
    /// anywhere: the mirror captures what the SOURCE endpoint is rendering, so if the source is not
    /// the endpoint Windows is actually playing to, there is nothing to capture and every output
    /// stays silent while looking perfectly healthy.
    /// </remarks>
    public bool CaptureSourceIsNotDefault =>
        _primaryDevice is not null
        && !string.IsNullOrEmpty(_defaultRenderEndpointId)
        && !string.Equals(_primaryDevice.Endpoint.EndpointId, _defaultRenderEndpointId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Actionable warning for the UI, or null when the capture source is correct.</summary>
    public string? CaptureSourceWarning => CaptureSourceIsNotDefault
        ? $"⚠ 主设备「{_primaryDevice!.DisplayName}」不是 Windows 默认输出设备。"
          + "MultiBT 捕获的是「主设备正在播放的声音」，而系统声音只会送到 Windows 默认设备——"
          + "所以其他设备会完全没有声音。请把「主设备」改为 Windows 默认输出设备，"
          + "或在 Windows 设置里把这个设备设为默认输出。"
        : null;

    public bool HasCaptureSourceWarning => CaptureSourceWarning is not null;

    /// <summary>Re-reads the Windows default render device and refreshes the capture-source warning.</summary>
    private void RefreshCaptureSourceState()
    {
        if (_devices.TryGetDefaultRenderDevice(out MMDevice? def) && def is not null)
        {
            _defaultRenderEndpointId = def.ID;
            def.Dispose();
        }

        OnPropertyChanged(nameof(CaptureSourceIsNotDefault));
        OnPropertyChanged(nameof(CaptureSourceWarning));
        OnPropertyChanged(nameof(HasCaptureSourceWarning));
    }

    /// <summary>
    /// Recomputes every device's automatic compensation from the current measurements and mode.
    /// </summary>
    public void RecomputeCompensations()
    {
        CompensationTarget[] targets = Devices
            .Select(d => new CompensationTarget(
                d.Key,
                d.Profile.Latency.MeasuredDelayRelRefMs,
                d.Endpoint.Transport))
            .ToArray();

        IReadOnlyDictionary<string, double> compensations = LatencyModel.ComputeCompensations(targets, Mode);

        foreach (DeviceViewModel device in Devices)
        {
            if (compensations.TryGetValue(device.Key, out double compensation))
            {
                device.Profile.Latency.CompensationMs = compensation;
            }
        }

        SystemLatencyMs = LatencyModel.ComputeSystemLatencyMs(targets, compensations, Mode);
        ExceedsLipSync = LatencyModel.ExceedsLipSyncThreshold(SystemLatencyMs);
        OnPropertyChanged(nameof(SystemLatencySummary));
    }

    /// <summary>Persists settings atomically.</summary>
    public void SaveSettings()
    {
        _store.Save(_settings);
        StatusText = "Settings saved.";
    }

    // ------------------------------------------------------------------ profiles

    /// <summary>Named scenes from the settings file.</summary>
    public IReadOnlyList<ProfileDefinition> Profiles => _settings.Profiles;

    /// <summary>Currently active profile id.</summary>
    public string? ActiveProfileId => _settings.ActiveProfileId;

    /// <summary>
    /// Activates a profile: applies its output set, gains and manual trims, then persists the
    /// choice.
    /// </summary>
    /// <remarks>
    /// Device enable/disable changes go through the normal toggle path so a running engine is
    /// updated in place rather than torn down and rebuilt.
    /// <para>
    /// A profile with an EMPTY output list means "no change", not "switch everything off". An
    /// empty list is the default state of a freshly created profile, and acting on it would
    /// silently mute every device the user had configured.
    /// </para>
    /// </remarks>
    public async Task ActivateProfileAsync(string profileId)
    {
        ProfileDefinition? profile = _settings.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null)
        {
            return;
        }

        _settings.ActiveProfileId = profileId;
        OnPropertyChanged(nameof(ActiveProfileId));
        OnPropertyChanged(nameof(Mode));

        if (profile.Outputs.Count > 0)
        {
            foreach (DeviceViewModel device in Devices)
            {
                OutputDefinition? output = profile.Outputs
                    .FirstOrDefault(o => string.Equals(o.DeviceKey, device.Key, StringComparison.Ordinal));

                if (output is null)
                {
                    continue;
                }

                // NOTE: OutputDefinition.Gain is deliberately NOT applied any more. Per-device loudness
                // is the device's own Windows volume, which is a machine setting rather than a scene
                // setting — restoring a scene should not silently change the user's system volumes.
                // The field stays in the schema so older settings files still load.

                device.ManualOffsetMs = output.ManualOffsetMs;

                bool changed = device.IsEnabled != output.Enabled;
                device.IsEnabled = output.Enabled;

                if (changed)
                {
                    await OnDeviceToggledCoreAsync(device).ConfigureAwait(true);
                }
            }
        }

        RecomputeCompensations();
        RaiseAllDevicesChanged();
        _store.Save(_settings);

        StatusText = $"Profile: {profile.Name}";
    }

    /// <summary>Saves the current device enable/gain/trim state into the active profile.</summary>
    public void SaveCurrentStateToActiveProfile()
    {
        ProfileDefinition? profile = _settings.Profiles.FirstOrDefault(p => p.Id == _settings.ActiveProfileId);
        if (profile is null)
        {
            return;
        }

        profile.Outputs = Devices.Select(d => new OutputDefinition
        {
            DeviceKey = d.Key,
            Enabled = d.IsEnabled,

            // No Gain: per-device loudness is the device's Windows volume, which is a machine setting.
            // Persisting it per scene would mean restoring a scene silently rewrites the user's system
            // volumes, which is not what "load a scene" should do.
            Gain = null,
            ManualOffsetMs = d.ManualOffsetMs,
        }).ToList();

        _store.Save(_settings);
    }

    // ------------------------------------------------------------------ pause

    /// <summary>True while multi-device output is muted by the tray pause.</summary>
    public bool IsPaused
    {
        get => _isPaused;
        private set => SetProperty(ref _isPaused, value);
    }

    /// <summary>
    /// Pauses or resumes multi-device output.
    /// </summary>
    /// <remarks>
    /// Implemented as a chain-gain mute, deliberately NOT as stopping the engine. Stopping tears
    /// down every player and every compensation delay line, so resuming would have to rebuild the
    /// whole mirror and re-establish synchronisation — a pause switch that costs a rebuild is not a
    /// pause switch. Muting the channels leaves capture, the drift controller and the delay lines
    /// running, so resume is instant and still in sync.
    /// </remarks>
    public void SetPaused(bool paused)
    {
        IsPaused = paused;

        if (_engine is not null)
        {
            foreach (OutputChannel channel in _engine.Channels)
            {
                // Chain gain, not the device volume: pausing mutes the whole mirror without touching
                // the user's Windows volume settings, so resuming restores exactly what was there.
                channel.SetGain(paused ? 0f : 1f);
            }
        }

        StatusText = paused ? "已暂停多设备输出。" : "已恢复输出。";
    }

    /// <summary>Toggles a device from outside the UI (the tray menu).</summary>
    public Task ToggleDeviceAsync(DeviceViewModel device) => ToggleDeviceSafelyAsync(device);

    /// <summary>Guarded wrapper so a device failure surfaces as status text, never as a crash.</summary>
    private async Task ToggleDeviceSafelyAsync(DeviceViewModel device)
    {
        try
        {
            await OnDeviceToggledCoreAsync(device).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            device.Status = "failed";
            StatusText = $"无法切换 {device.DisplayName}：{ex.Message}";
        }
    }

    /// <summary>
    /// Sets a device as the primary capture source.
    /// The primary device is used for loopback capture and is excluded from output.
    /// </summary>
    public void SetPrimaryDevice(DeviceViewModel? device)
    {
        // Unset previous primary
        if (_primaryDevice is not null)
        {
            _primaryDevice.IsPrimary = false;
        }

        _primaryDevice = device;

        if (device is not null)
        {
            device.IsPrimary = true;

            // Persist so the choice survives a refresh AND a restart.
            _settings.Engine.SourceDeviceId = device.Endpoint.EndpointId;

            // Keep Windows in step with the choice.
            //
            // The mirror captures what the SOURCE endpoint is RENDERING, and Windows only plays
            // system audio to the DEFAULT endpoint. So a primary that is not the Windows default
            // captures silence — the failure that looks exactly like broken audio code. Switching
            // both together is what makes "choose a primary" mean what the user expects.
            if (!string.Equals(device.Endpoint.EndpointId, _defaultRenderEndpointId, StringComparison.OrdinalIgnoreCase))
            {
                if (DefaultEndpointSwitcher.TrySetDefault(device.Endpoint.EndpointId, out string? error))
                {
                    _defaultRenderEndpointId = device.Endpoint.EndpointId;
                    StatusText = $"主设备已设为「{device.DisplayName}」，并已同步切换 Windows 默认输出设备。";
                }
                else
                {
                    StatusText = $"主设备已设为「{device.DisplayName}」，但切换 Windows 默认输出设备失败：{error}";
                }
            }
            else
            {
                StatusText = $"主设备已设置为: {device.DisplayName}";
            }
        }

        // Re-evaluate the capture-source warning: picking a device that Windows is not playing to is
        // the difference between "other devices sound" and "nothing but the default sounds".
        RefreshCaptureSourceState();
        OnPropertyChanged(nameof(PrimaryDisplayName));
    }

    /// <summary>Display name of the current capture source, for the UI.</summary>
    public string PrimaryDisplayName => _primaryDevice?.DisplayName ?? "（未选择）";

    /// <summary>Matches a device row to an endpoint id, tolerating a null id.</summary>
    private static bool MatchesEndpoint(DeviceViewModel device, string? endpointId) =>
        !string.IsNullOrEmpty(endpointId)
        && string.Equals(device.Endpoint.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Starts the mirror to every enabled device.
    /// </summary>
    /// <remarks>
    /// NOT hardware-verified in this repository — see the class remarks.
    /// </remarks>
    public void Start()
    {
        if (_engine is not null)
        {
            return;
        }

        List<DeviceViewModel> selected = Devices.Where(d => d.IsEnabled).ToList();
        if (selected.Count == 0)
        {
            StatusText = "Select at least one output device first.";
            return;
        }

        if (!ResolveSourceDevice(out MMDevice? source) || source is null)
        {
            StatusText = "No render device available to mirror.";
            return;
        }

        _sourceDevice = source;

        // Loopback capture delivers the source endpoint's mix format, so that is the format the
        // chains must be built at. Reading it from our own client here avoids a chicken-and-egg
        // with the recorder, which only exposes its format after construction.
        WaveFormat captureFormat;
        using (AudioClient probe = source.CreateAudioClient())
        {
            captureFormat = probe.MixFormat;
        }

        var engine = new AudioEngine(_devices);

        try
        {
            foreach (DeviceViewModel device in selected)
            {
                // Skip the capture source device - using it as output causes feedback/echo
                if (string.Equals(device.Endpoint.EndpointId, source.ID, StringComparison.OrdinalIgnoreCase))
                {
                    device.Status = "skipped (capture source)";
                    continue;
                }

                // Fresh resolution per activation: a cached MMDevice dies on sleep/resume even
                // though the endpoint still enumerates.
                if (!_devices.TryResolveDevice(device.Endpoint.EndpointId, out MMDevice? resolved) || resolved is null)
                {
                    device.Status = "unavailable";
                    continue;
                }

                _liveChannelDevices[device.Endpoint.EndpointId] = resolved;

                var channel = new OutputChannel(
                    device.Key,
                    resolved,
                    captureFormat,
                    _settings.Engine.EngineLatencyMs,
                    // Chain gain fixed at 1.0: per-device loudness is the device's Windows volume, so the
                    // chain must not scale it again.
                    1.0);

                bool glided = channel.ApplyDelayMs(device.EffectiveDelayMs);

                engine.AddChannel(channel);
                device.Status = glided
                    ? $"running · delay {channel.AppliedDelayMs:0} ms"
                    : $"running · delay {channel.AppliedDelayMs:0} ms (resynced)";
            }

            if (engine.Channels.Count == 0)
            {
                StatusText = "No device could be opened.";
                ReleaseLiveDevices();
                return;
            }

            engine.Start(source);
            _engine = engine;
            IsRunning = true;
            _diagnosticsTimer.Start();

            StatusText = $"Mirroring to {engine.Channels.Count} device(s) "
                         + $"(requested latency {_settings.Engine.EngineLatencyMs} ms).";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not start: {ex.Message}";
            _ = engine.DisposeAsync();
            ReleaseLiveDevices();
            DisposeSourceDevice();
        }
    }

    /// <summary>Stops the mirror, fading out first so the stop does not click.</summary>
    public async Task StopAsync()
    {
        _diagnosticsTimer.Stop();

        // Persist the current gains/trims so a volume tweak survives a restart. Done here rather than
        // on every slider tick, which would rewrite the settings file continuously.
        try
        {
            SaveCurrentStateToActiveProfile();
        }
        catch (Exception)
        {
            // Never let a settings write failure block stopping audio.
        }

        if (_engine is not null)
        {
            AudioEngine engine = _engine;
            _engine = null;

            await engine.DisposeAsync().ConfigureAwait(true);
            IsRunning = false;
            StatusText = "Stopped.";
        }

        ReleaseLiveDevices();
        DisposeSourceDevice();
        DiagnosticsSummary = "—";
    }

    /// <summary>Pushes chain-gain changes into the running channels.</summary>
    /// <remarks>
    /// The chain gain is held at 1.0 so per-device loudness is exactly the device's Windows volume —
    /// 100 % on the slider means 100 % of the device. It is only used as a whole-mirror mute for
    /// <see cref="SetPaused"/>.
    /// </remarks>
    public void ApplyGainsToEngine()
    {
        if (_engine is null)
        {
            return;
        }

        float chainGain = IsPaused ? 0f : 1f;

        foreach (OutputChannel channel in _engine.Channels)
        {
            channel.SetGain(chainGain);
        }
    }

    /// <summary>Queues a debounced write of a device's endpoint volume.</summary>
    /// <remarks>
    /// A slider drag raises a change per pixel, and each write is a COM call that changes system state.
    /// Coalescing to one write per ~150 ms keeps the drag responsive without hammering the endpoint.
    /// </remarks>
    private void QueueEndpointVolumeWrite(DeviceViewModel device)
    {
        if (_suppressVolumeWrites || double.IsNaN(device.EndpointVolume))
        {
            return;
        }

        _pendingVolumeWrites[device.Endpoint.EndpointId] = device.EndpointVolume;
        _volumeWriteTimer.Stop();
        _volumeWriteTimer.Start();
    }

    private void FlushEndpointVolumeWrites()
    {
        _volumeWriteTimer.Stop();

        foreach ((string endpointId, double volume) in _pendingVolumeWrites)
        {
            EndpointVolumeReader.TryWrite(_devices, endpointId, volumeScalar: volume);

            // Mirror the achieved value back, in case the endpoint quantised it.
            if (EndpointVolumeReader.TryRead(_devices, endpointId, out double actual, out bool muted))
            {
                DeviceViewModel? device = Devices.FirstOrDefault(d => MatchesEndpoint(d, endpointId));
                if (device is not null)
                {
                    // Assign the field directly through the property only when it really differs, so a
                    // re-entrant write does not queue itself forever.
                    if (Math.Abs(device.EndpointVolume - actual) > 1e-6)
                    {
                        _suppressVolumeWrites = true;
                        device.EndpointVolume = actual;
                        _suppressVolumeWrites = false;
                    }

                    device.EndpointMuted = muted;
                }
            }
        }

        _pendingVolumeWrites.Clear();
    }

    /// <summary>
    /// Sets every enabled device's output volume so their LOUDNESS matches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrored loudness is approximately <c>endpointVolume x speakerSensitivity</c>. Only the first
    /// factor is measurable, so this equalises it: every enabled device is set to the LOWEST volume
    /// among them, and the user then raises any device that still sounds quiet (or trims by ear,
    /// because speaker sensitivity is not exposed by any API).
    /// </para>
    /// <para>
    /// It only ever lowers. Raising every device to the loudest one's volume would make the whole set
    /// as loud as the loudest speaker, which is the opposite of matching them.
    /// </para>
    /// </remarks>
    public void AutoMatchLevels()
    {
        List<DeviceViewModel> involved = Devices
            .Where(d => d.IsEnabled && !double.IsNaN(d.EndpointVolume) && d.EndpointVolume > 0.001)
            .ToList();

        if (involved.Count == 0)
        {
            StatusText = "无法自动对齐：没有读到任何设备的端点音量。请手动调整滑杆。";
            return;
        }

        double reference = involved.Min(d => d.EndpointVolume);

        foreach (DeviceViewModel device in involved)
        {
            device.EndpointVolume = reference;
        }

        // Write immediately rather than waiting for the debounce: this is a deliberate one-shot action.
        FlushEndpointVolumeWrites();

        StatusText = $"已把 {involved.Count} 个设备的音量统一到 {reference * 100:0}%"
                     + "（取其中最小的一个）。音箱本身的灵敏度差异仍需手动微调滑杆。";
    }

    /// <summary>
    /// Mirrors every device's Windows endpoint volume and mute state into the view models.
    /// </summary>
    /// <remarks>
    /// This is what makes "the volumes don't match" explainable instead of mysterious: the user can
    /// see that one speaker is at 100 % and another at 61 %, which is exactly the part that CAN be
    /// equalised — as opposed to speaker sensitivity, which cannot.
    /// </remarks>
    public void RefreshEndpointVolumes()
    {
        foreach (DeviceViewModel device in Devices)
        {
            if (EndpointVolumeReader.TryRead(_devices, device.Endpoint.EndpointId, out double volume, out bool muted))
            {
                device.EndpointVolume = volume;
                device.EndpointMuted = muted;
            }
            else
            {
                device.EndpointVolume = double.NaN;
                device.EndpointMuted = false;
            }
        }
    }

    /// <summary>Raises a device's own Windows endpoint volume to 100 %.</summary>
    public void MaximizeEndpointVolume(DeviceViewModel device)
    {
        ArgumentNullException.ThrowIfNull(device);

        // The slider already reaches 100 %, but a single click is convenient when a device is
        // noticeably quieter than the others.
        device.EndpointVolume = 1.0;
        FlushEndpointVolumeWrites();
        StatusText = $"已将「{device.DisplayName}」的设备音量设为 100%。";
    }

    /// <summary>
    /// Handles device enable/disable changes while the engine is running.
    /// Adds or removes channels in real-time without restarting the entire mirror.
    /// </summary>
    public async Task OnDeviceToggledCoreAsync(DeviceViewModel device)
    {
        if (_engine is null)
        {
            return;
        }

        if (device.IsEnabled)
        {
            // Device was enabled: add a channel
            await AddChannelToEngineAsync(device).ConfigureAwait(true);
        }
        else
        {
            // Device was disabled: remove the channel
            await RemoveChannelFromEngineAsync(device.Key).ConfigureAwait(true);
        }
    }

    private async Task AddChannelToEngineAsync(DeviceViewModel device)
    {
        if (_engine is null || _sourceDevice is null)
        {
            return;
        }

        // Check if channel already exists
        if (_engine.Channels.Any(c => c.DeviceKey == device.Key))
        {
            return;
        }

        if (!_devices.TryResolveDevice(device.Endpoint.EndpointId, out MMDevice? resolved) || resolved is null)
        {
            device.Status = "unavailable";
            return;
        }

        _liveChannelDevices[device.Endpoint.EndpointId] = resolved;

        WaveFormat captureFormat;
        using (AudioClient probe = _sourceDevice.CreateAudioClient())
        {
            captureFormat = probe.MixFormat;
        }

        var channel = new OutputChannel(
            device.Key,
            resolved,
            captureFormat,
            _settings.Engine.EngineLatencyMs,
            // Chain gain fixed at 1.0; see the Start() path.
            1.0);

        bool glided = channel.ApplyDelayMs(device.EffectiveDelayMs);

        _engine.AddChannel(channel);
        channel.Start();

        device.Status = glided
            ? $"running · delay {channel.AppliedDelayMs:0} ms"
            : $"running · delay {channel.AppliedDelayMs:0} ms (resynced)";

        StatusText = $"Added {device.DisplayName}. Running on {_engine.Channels.Count} device(s).";
    }

    private async Task RemoveChannelFromEngineAsync(string deviceKey)
    {
        if (_engine is null)
        {
            return;
        }

        OutputChannel? channel = _engine.Channels.FirstOrDefault(c => c.DeviceKey == deviceKey);
        if (channel is null)
        {
            return;
        }

        _engine.RemoveChannel(deviceKey);

        DeviceViewModel? device = Devices.FirstOrDefault(d => d.Key == deviceKey);
        if (device is not null)
        {
            device.Status = "stopped";
        }

        // Dispose the channel and release the device. The device reference must be released AFTER
        // the player is gone: the player holds the MMDevice internally, so disposing it first
        // would pull the COM object out from under a running stream.
        await channel.FadeOutAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(true);
        await channel.DisposeAsync().ConfigureAwait(true);

        // BUGFIX: _liveChannelDevices is keyed by ENDPOINT ID (see Start and
        // AddChannelToEngineAsync), not by device key. Looking it up by deviceKey silently missed
        // every single time, so the MMDevice was never released here and stayed in the dictionary
        // holding the endpoint open until Stop.
        if (device is not null
            && _liveChannelDevices.TryGetValue(device.Endpoint.EndpointId, out MMDevice? mmDevice))
        {
            mmDevice.Dispose();
            _liveChannelDevices.Remove(device.Endpoint.EndpointId);
        }

        StatusText = $"Removed device. Running on {_engine.Channels.Count} device(s).";
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _devices.Dispose();
    }

    private bool ResolveSourceDevice(out MMDevice? source)
    {
        // Priority 1: User-selected primary device
        if (_primaryDevice is not null
            && _devices.TryResolveDevice(_primaryDevice.Endpoint.EndpointId, out MMDevice? primary)
            && primary is not null)
        {
            source = primary;
            return true;
        }

        // Priority 2: Configured source device
        string? configuredId = _settings.Engine.SourceDeviceId;

        if (!string.IsNullOrEmpty(configuredId)
            && _devices.TryResolveDevice(configuredId, out MMDevice? configured)
            && configured is not null)
        {
            source = configured;
            return true;
        }

        // Priority 3: Windows default render device
        return _devices.TryGetDefaultRenderDevice(out source) && source is not null;
    }

    private void RefreshDiagnostics()
    {
        if (_engine is null)
        {
            return;
        }

        IReadOnlyList<ChannelDiagnostics> all = _engine.GetDiagnostics();
        if (all.Count == 0)
        {
            return;
        }

        // The two numbers that actually diagnose a problem: the correction being applied, the
        // trough depth (which NAudio would otherwise hide completely), and the capture→device
        // format pair — the last being what distinguishes "this device is silent because it is
        // disconnected" from "silent because the rate conversion is broken".
        DiagnosticsSummary = string.Join("   |   ", all.Select(d => d.Summarize()));

        foreach (ChannelDiagnostics diagnostics in all)
        {
            DeviceViewModel? device = Devices.FirstOrDefault(d => d.Key == diagnostics.DeviceKey);
            if (device is not null)
            {
                device.Status = $"running · {diagnostics.CorrectionPpm:+0;-0;0} ppm · delay {diagnostics.AppliedDelayMs:0} ms";
            }
        }
    }

    private void ReleaseLiveDevices()
    {
        foreach (MMDevice device in _liveChannelDevices.Values)
        {
            device.Dispose();
        }

        _liveChannelDevices.Clear();
    }

    private void DisposeSourceDevice()
    {
        _sourceDevice?.Dispose();
        _sourceDevice = null;
    }

    private void RaiseAllDevicesChanged()
    {
        foreach (DeviceViewModel device in Devices)
        {
            device.RaiseLatencyChanged();
        }
    }
}
