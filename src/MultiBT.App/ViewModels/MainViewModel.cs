using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using MultiBT.App.Localization;
using MultiBT.App.Services;
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

    /// <summary>Coalesces preference changes into one settings write per burst.</summary>
    private readonly DispatcherTimer _settingsSaveTimer;

    /// <summary>Coalesces delay-slider drags into one recompute-and-apply per burst.</summary>
    private readonly DispatcherTimer _delayWriteTimer;

    /// <summary>Devices whose delay changed and still need applying.</summary>
    private readonly HashSet<string> _pendingDelayWrites = new(StringComparer.Ordinal);

    /// <summary>Snapshot history behind Ctrl+Z / Ctrl+Y.</summary>
    private readonly SettingsHistory _history = new();

    /// <summary>Endpoint volumes waiting to be written, keyed by endpoint id.</summary>
    private readonly Dictionary<string, double> _pendingVolumeWrites = new(StringComparer.OrdinalIgnoreCase);


    /// <summary>
    /// Endpoints resolved for the running engine. Kept alive deliberately: an
    /// <c>MMDevice</c> passed to a player must outlive that player, and a cached one must not
    /// be reused after sleep/resume — so these are created per start and released per stop.
    /// </summary>
    private readonly Dictionary<string, MMDevice> _liveChannelDevices = new(StringComparer.Ordinal);

    private string? _defaultRenderEndpointId;
    private MultiBtSettings _settings;
    private AudioEngine? _engine;

    /// <summary>
    /// A capture backend built for this start attempt but not yet handed to the engine.
    /// </summary>
    /// <remarks>
    /// A backend owns the endpoint inside it, and ownership passes to <see cref="AudioEngine"/> only when
    /// <c>Start</c> is actually reached. Channel construction sits between building the backend and starting
    /// the engine, and it can throw — a device that vanished, a format the endpoint refuses. The engine
    /// cannot dispose a backend it was never given, so without this field such a failure leaked the
    /// capture endpoint for the life of the process.
    /// </remarks>
    private IAudioInputBackend? _pendingBackend;

    /// <summary>
    /// Endpoint id the engine is currently capturing, or null when stopped.
    /// </summary>
    /// <remarks>
    /// Kept as the single answer to "is this device the capture source?" so no code path has to
    /// re-derive it. Getting that wrong is not cosmetic: a device that is BOTH the source and an
    /// output captures its own loopback and plays it back to itself, which is an audio feedback loop.
    /// </remarks>
    private string? _captureSourceEndpointId;
    private DeviceViewModel? _primaryDevice;
    private string _statusText = string.Empty;
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

        // Apply the remembered language BEFORE anything builds a user-visible string.
        Localizer.Instance.SetLanguage(_settings.Ui.Language);

        _diagnosticsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _diagnosticsTimer.Tick += (_, _) => RefreshDiagnostics();

        _volumeWriteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _volumeWriteTimer.Tick += (_, _) => FlushEndpointVolumeWrites();

        _settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _settingsSaveTimer.Tick += (_, _) => FlushSettingsSave();

        _delayWriteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _delayWriteTimer.Tick += (_, _) => FlushDelayWrites();

        // Indexer-bound XAML updates itself on a language change, but strings composed in code and
        // enum-valued pickers do not, so give them a chance to rebuild.
        Localizer.Instance.LanguageChanged += (_, _) => OnLanguageChanged();

        // Seed the history with the state we loaded, so the first Ctrl+Z returns here.
        _history.Seed(SerializeSettings());

        UndoCommand = new RelayCommand(Undo, () => _history.CanUndo);
        RedoCommand = new RelayCommand(Redo, () => _history.CanRedo);

        RefreshDevices();
    }

    /// <summary>Steps the settings back one snapshot. Bound to Ctrl+Z.</summary>
    public ICommand UndoCommand { get; }

    /// <summary>Steps the settings forward one snapshot. Bound to Ctrl+Y.</summary>
    public ICommand RedoCommand { get; }

    /// <summary>Whether an undo is available.</summary>
    public bool CanUndo => _history.CanUndo;

    /// <summary>Whether a redo is available.</summary>
    public bool CanRedo => _history.CanRedo;

    /// <summary>Serialises the settings exactly as they would be persisted.</summary>
    private string SerializeSettings() =>
        System.Text.Json.JsonSerializer.Serialize(_settings, ProfileStore.SerializerOptions);

    private void Undo()
    {
        if (!_history.TryUndo(out string? snapshot) || snapshot is null)
        {
            return;
        }

        _ = ApplySnapshotAsync(snapshot, Localizer.Instance["Status.Undone"]);
    }

    private void Redo()
    {
        if (!_history.TryRedo(out string? snapshot) || snapshot is null)
        {
            return;
        }

        _ = ApplySnapshotAsync(snapshot, Localizer.Instance["Status.Redone"]);
    }

    /// <summary>
    /// Restores a snapshot and pushes it into the running mirror.
    /// </summary>
    /// <remarks>
    /// Rebuilt through the normal paths rather than by restarting the engine: enumeration re-reads the
    /// device records, and the volume/delay flushes push the restored values into the live channels. A
    /// restart would work too, but it would fade every device in again for two seconds, which is not
    /// what "quickly step back" should feel like.
    /// </remarks>
    private async Task ApplySnapshotAsync(string snapshot, string statusTemplate)
    {
        try
        {
            MultiBtSettings? restored = System.Text.Json.JsonSerializer.Deserialize<MultiBtSettings>(
                snapshot,
                ProfileStore.SerializerOptions);

            if (restored is null)
            {
                return;
            }

            _settings = restored;

            // Rebuilds the rows from the restored records and re-binds the primary.
            RefreshDevices();

            // Enabled state lives on the device record, and RefreshDevices sets it on the FIELD rather
            // than through the property, so the channels are reconciled explicitly here.
            foreach (DeviceViewModel device in Devices)
            {
                bool shouldBeEnabled = device.Profile.Audio.Enabled != false;

                if (device.IsEnabled != shouldBeEnabled)
                {
                    device.IsEnabled = shouldBeEnabled;
                    await OnDeviceToggledCoreAsync(device).ConfigureAwait(true);
                }

                if (device.Profile.Audio.DesiredVolume is double volume)
                {
                    device.EndpointVolume = volume;
                }
            }

            FlushEndpointVolumeWrites();
            FlushDelayWrites();

            // These are read straight off the settings, so the bound pickers need telling.
            OnPropertyChanged(nameof(EngineLatencyMs));
            OnPropertyChanged(nameof(Mode));
            OnPropertyChanged(nameof(Language));
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));

            // Persist the restored state so disk matches what is on screen.
            _store.Save(_settings);

            StatusText = statusTemplate;
        }
        catch (Exception ex)
        {
            StatusText = Localizer.Instance.Format("Status.UndoFailed", ex.Message);
        }
    }

    /// <summary>Current UI language.</summary>
    public UiLanguage Language => _settings.Ui.Language;

    /// <summary>Every render endpoint, offered as a possible input.</summary>
    public IReadOnlyList<AudioEndpointInfo> CaptureSinkCandidates => VirtualCableDetector.OrderForSinkPicker(
        _devices.EnumerateRenderEndpoints(includeInactive: true));

    /// <summary>The configured capture sink, or null when none is set.</summary>
    public string? CaptureSinkEndpointId => _settings.Engine.CaptureSinkDeviceId;

    /// <summary>
    /// Whether the input note applies, so the UI can show or hide it.
    /// </summary>
    public bool HasInputNote => InputNote is not null;

    /// <summary>
    /// A note about the current input, when it is a real device rather than a virtual cable.
    /// </summary>
    /// <remarks>
    /// Not a warning and not a defect report: capturing a real device is a supported choice. It is stated
    /// because of one consequence the user cannot deduce — a captured real device keeps playing its own
    /// audio, straight from Windows, so it is the one device whose delay and volume MultiBT does not
    /// control. Without saying so, a dead delay slider on that device looks like a bug.
    /// </remarks>
    public string? InputNote
    {
        get
        {
            AudioEndpointInfo? endpoint = ResolveEffectiveInput();

            if (endpoint is null
                || VirtualCableDetector.IsVirtualCableRenderEndpoint(endpoint.FriendlyName, endpoint.DeviceFriendlyName))
            {
                // A cable is inaudible and exists only to be captured, so every device stays controllable.
                return null;
            }

            return Localizer.Instance.Format("Input.RealDeviceNote", endpoint.FriendlyName);
        }
    }

    /// <summary>
    /// The input the mirror will actually use, described without opening anything.
    /// </summary>
    /// <remarks>
    /// Mirrors the priority of <see cref="ResolveSourceDevice"/> for the two cases that matter for display:
    /// an explicit choice, or — with the input on "auto" — the Windows default output. Resolving this the
    /// same way matters, because "auto" is the default state and it very often lands on a real speaker,
    /// which is precisely when the note needs to appear.
    /// </remarks>
    private AudioEndpointInfo? ResolveEffectiveInput()
    {
        string? id = _settings.Engine.CaptureSinkDeviceId;

        if (string.IsNullOrEmpty(id))
        {
            if (!_devices.TryGetDefaultRenderDevice(out MMDevice? current) || current is null)
            {
                return null;
            }

            id = current.ID;
            current.Dispose();
        }

        return _devices
            .EnumerateRenderEndpoints(includeInactive: true)
            .FirstOrDefault(e => string.Equals(e.EndpointId, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Sets the input to a chosen endpoint, or back to "auto" when given null.
    /// </summary>
    /// <remarks>
    /// Choosing a virtual cable also has to point the Windows default output at that same cable, because a
    /// cable only carries audio that Windows renders into it. That is the ONE place the app touches the
    /// default output, and it happens only because the user picked a cable. Any other endpoint is left
    /// alone: capturing a real device works with the default wherever it already is.
    /// </remarks>
    public void SetCaptureSink(string? endpointId)
    {
        _settings.Engine.CaptureSinkDeviceId = string.IsNullOrWhiteSpace(endpointId) ? null : endpointId;

        if (_settings.Engine.CaptureSinkDeviceId is not string chosen)
        {
            StatusText = Localizer.Instance["Status.SinkCleared"];
        }
        else if (!IsVirtualCable(chosen))
        {
            // A real device: nothing to switch. The user's own default stays theirs.
            StatusText = Localizer.Instance["Sink.Applied"];
        }
        else if (DefaultEndpointSwitcher.TrySetDefault(chosen, out string? error))
        {
            _defaultRenderEndpointId = chosen;
            StatusText = Localizer.Instance["Sink.AppliedCable"];
        }
        else
        {
            StatusText = Localizer.Instance.Format("Status.SinkSwitchFailed", error);
        }

        QueueSettingsSave();
        OnPropertyChanged(nameof(CaptureSinkEndpointId));
        OnPropertyChanged(nameof(HasInputNote));
        OnPropertyChanged(nameof(InputNote));

        if (_engine is not null)
        {
            _ = RestartForNewSourceAsync();
        }
    }

    /// <summary>Whether a configured endpoint is a virtual cable, by endpoint id.</summary>
    private bool IsVirtualCable(string endpointId)
    {
        AudioEndpointInfo? endpoint = _devices
            .EnumerateRenderEndpoints(includeInactive: true)
            .FirstOrDefault(e => string.Equals(e.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase));

        return endpoint is not null
            && VirtualCableDetector.IsVirtualCableRenderEndpoint(endpoint.FriendlyName, endpoint.DeviceFriendlyName);
    }

    /// <summary>Switches the UI language and remembers it.</summary>
    public void SetLanguage(UiLanguage language)
    {
        if (_settings.Ui.Language == language)
        {
            return;
        }

        _settings.Ui.Language = language;
        Localizer.Instance.SetLanguage(language);

        QueueSettingsSave();
        OnPropertyChanged(nameof(Language));
    }

    /// <summary>Rebuilds every string composed in code rather than bound through the indexer.</summary>
    private void OnLanguageChanged()
    {
        foreach (DeviceViewModel device in Devices)
        {
            device.RaiseLocalisedText();
        }

        OnPropertyChanged(nameof(SystemLatencySummary));
        OnPropertyChanged(nameof(CaptureSourceWarning));
        OnPropertyChanged(nameof(HasCaptureSourceWarning));
        StatusText = Localizer.Instance["Status.Idle"];
    }

    /// <summary>Queues a debounced settings write. A slider drag is not a reason to rewrite the file.</summary>
    private void QueueSettingsSave()
    {
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    /// <summary>
    /// Commits the current settings: records a history snapshot and writes the file.
    /// </summary>
    /// <remarks>
    /// Auto-save and undo share this one commit point, so anything that persists is also undoable and
    /// there is no second place to remember to update. Every settings mutation must route here.
    /// </remarks>
    private void FlushSettingsSave()
    {
        _settingsSaveTimer.Stop();

        try
        {
            string snapshot = SerializeSettings();

            // Commit records nothing when the state is unchanged, so an idempotent save does not create
            // a history entry that would make Ctrl+Z look broken.
            if (_history.Commit(snapshot))
            {
                OnPropertyChanged(nameof(CanUndo));
                OnPropertyChanged(nameof(CanRedo));
            }

            _store.Save(_settings);
        }
        catch (Exception)
        {
            // A settings write failure must never disturb audio.
        }
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
            QueueSettingsSave();

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
            QueueSettingsSave();
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
    public string SystemLatencySummary => Localizer.Instance.Format(
        _exceedsLipSync ? "SystemLatency.Warning" : "SystemLatency.Ok",
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{_systemLatencyMs:0}"));

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

            // Auto-save when a device is ticked on or off.
            device.EnabledChangedForPersistence += (_, _) => QueueSettingsSave();

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

                // Manual delay changes must reach the running chain AND be remembered.
                if (e.PropertyName == nameof(DeviceViewModel.ManualOffsetMs))
                {
                    QueueDelayWrite(device);
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
                ?? Devices.FirstOrDefault(d => d.Profile.IsPrimary)
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

        StatusText = Localizer.Instance.Format(
            "Status.Found",
            Devices.Count,
            Devices.Count(d => d.Endpoint.Transport == Transport.Bluetooth));
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
        ? Localizer.Instance.Format("Capture.NotDefault", _primaryDevice!.DisplayName)
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
        StatusText = Localizer.Instance["Status.SettingsSaved"];
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

        StatusText = Localizer.Instance.Format("Status.Profile", profile.Name);
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

        StatusText = Localizer.Instance[paused ? "Status.Paused" : "Status.Resumed"];
    }

    /// <summary>Toggles a device from outside the UI (the tray menu).</summary>
    public Task ToggleDeviceAsync(DeviceViewModel device) => ToggleDeviceSafelyAsync(device);

    /// <summary>
    /// Whether a device is the endpoint currently being captured.
    /// </summary>
    /// <remarks>
    /// The capture source must never also be an output. Loopback captures what the endpoint is
    /// rendering, so writing to it closes the loop and produces runaway echo — the failure this helper
    /// exists to make impossible from every code path.
    /// </remarks>
    private bool IsCaptureSource(DeviceViewModel device)
    {
        if (string.IsNullOrEmpty(_captureSourceEndpointId))
        {
            return false;
        }

        return string.Equals(
            device.Endpoint.EndpointId,
            _captureSourceEndpointId,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rebuilds the mirror against a newly chosen capture source.</summary>
    private async Task RestartForNewSourceAsync()
    {
        try
        {
            // Stop first: it clears the capture-source id, so Start() re-evaluates the whole output
            // set from scratch and the skip logic sees the new source.
            await StopAsync().ConfigureAwait(true);
            Start();
        }
        catch (Exception ex)
        {
            StatusText = Localizer.Instance.Format("Status.CouldNotStart", ex.Message);
        }
    }



    /// <summary>
    /// Chooses the input backend for a resolved endpoint.
    /// </summary>
    /// <remarks>
    /// A virtual cable gets the cable backend, which validates that the endpoint really is one; everything
    /// else is plain loopback. No brand is named here either — the decision is "is this endpoint a cable?",
    /// answered by <see cref="VirtualCableDetector"/>, so replacing or removing the cable changes nothing
    /// else in the application.
    /// </remarks>
    private static IAudioInputBackend BuildInputBackend(MMDevice source)
    {
        ArgumentNullException.ThrowIfNull(source);

        bool isCable = VirtualCableDetector.IsVirtualCableRenderEndpoint(
            source.FriendlyName,
            source.DeviceFriendlyName);

        return isCable
            ? new VirtualCableBackend(source)
            : new NativeLoopbackBackend(source);
    }

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
            StatusText = Localizer.Instance.Format("Status.CouldNotToggle", device.DisplayName, ex.Message);
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

        // Remember the choice, and make sure only one device claims it.
        foreach (DeviceViewModel candidate in Devices)
        {
            candidate.Profile.IsPrimary = ReferenceEquals(candidate, device);
        }

        QueueSettingsSave();

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
                    StatusText = Localizer.Instance.Format("Status.PrimarySetAndSwitched", device.DisplayName);
                }
                else
                {
                    StatusText = Localizer.Instance.Format("Status.PrimarySwitchFailed", device.DisplayName, error);
                }
            }
            else
            {
                StatusText = Localizer.Instance.Format("Status.PrimarySet", device.DisplayName);
            }

            // Apply it to a RUNNING mirror.
            //
            // Capture is bound at start-up, so without this the switch only took effect after a manual
            // stop/start — which read as "setting the primary does nothing". Restarting also puts the
            // old source back into the output set and drops the new one out of it, so the source is
            // never also an output.
            if (_engine is not null)
            {
                StatusText = Localizer.Instance.Format("Status.RestartingForSource", device.DisplayName);
                _ = RestartForNewSourceAsync();
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

        // Apply remembered per-device volume. Deliberately here rather than during enumeration:
        // enumeration SHOWS the device's real volume, while starting the mirror is the moment the
        // user's stored preference should take effect.
        foreach (DeviceViewModel remembered in Devices.Where(d => d.Profile.Audio.DesiredVolume is not null))
        {
            remembered.EndpointVolume = remembered.Profile.Audio.DesiredVolume!.Value;
        }

        FlushEndpointVolumeWrites();

        List<DeviceViewModel> selected = Devices.Where(d => d.IsEnabled).ToList();
        if (selected.Count == 0)
        {
            StatusText = Localizer.Instance["Status.SelectDevice"];
            return;
        }

        if (!ResolveSourceDevice(out MMDevice? source) || source is null)
        {
            StatusText = Localizer.Instance["Status.NoSource"];
            return;
        }


        // Which backend to use is decided HERE and nowhere else. This is the only place in the
        // application that knows a virtual cable might be involved; the engine learns nothing about it.
        IAudioInputBackend backend;

        try
        {
            backend = BuildInputBackend(source);
        }
        catch (Exception ex)
        {
            // A refused backend (e.g. asked to treat a real speaker as a cable) has already released the
            // device it was given, so there is nothing to clean up beyond reporting.
            StatusText = Localizer.Instance.Format("Status.CouldNotStart", ex.Message);
            return;
        }

        // Ownership of the backend — and of the endpoint inside it — is held here until the engine is
        // actually started, because the channel construction below can throw.
        _pendingBackend = backend;
        _captureSourceEndpointId = source.ID;

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
                // Never open an output to the endpoint we are capturing: that is a feedback loop.
                if (IsCaptureSource(device))
                {
                    device.Status = Localizer.Instance["Status.DeviceSkippedSource"];
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
                StatusText = Localizer.Instance["Status.NoDeviceOpened"];
                ReleaseLiveDevices();
                return;
            }

            engine.Start(backend);

            // The engine owns the backend from here on; it disposes it in DisposeAsync.
            _pendingBackend = null;
            _engine = engine;
            IsRunning = true;
            _diagnosticsTimer.Start();

            StatusText = Localizer.Instance.Format("Status.Mirroring", engine.Channels.Count, _settings.Engine.EngineLatencyMs);
        }
        catch (Exception ex)
        {
            StatusText = Localizer.Instance.Format("Status.CouldNotStart", ex.Message);
            _ = engine.DisposeAsync();
            ReleaseLiveDevices();
            DisposePendingBackend();
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
            StatusText = Localizer.Instance["Status.Stopped"];
        }

        ReleaseLiveDevices();
        DisposePendingBackend();
        _captureSourceEndpointId = null;
        DiagnosticsSummary = Localizer.Instance["Diagnostics.None"];
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
        if (double.IsNaN(device.EndpointVolume))
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

            // Remembered as a device preference. Written here rather than on every slider tick, so a
            // drag produces one settings write per burst instead of one per pixel.
            DeviceViewModel? owner = Devices.FirstOrDefault(d => MatchesEndpoint(d, endpointId));
            if (owner is not null)
            {
                owner.Profile.Audio.DesiredVolume = volume;
                QueueSettingsSave();
            }

            // Refresh ONLY the mute flag.
            //
            // This deliberately does NOT read the achieved volume back any more. Windows quantises
            // endpoint volume, so echoing the achieved value into the view model moved the slider thumb
            // away from where the user was pointing; during a drag the two fought each other and the
            // change appeared to take effect only on mouse release. The slider now shows the user's
            // intent, which is also what the next write will send.
            DeviceViewModel? muted = Devices.FirstOrDefault(d => MatchesEndpoint(d, endpointId));
            if (muted is not null
                && EndpointVolumeReader.TryRead(_devices, endpointId, out _, out bool isMuted))
            {
                muted.EndpointMuted = isMuted;
            }
        }

        _pendingVolumeWrites.Clear();
    }

    /// <summary>Queues a debounced apply of a device's manual delay.</summary>
    private void QueueDelayWrite(DeviceViewModel device)
    {
        _pendingDelayWrites.Add(device.Key);
        _delayWriteTimer.Stop();
        _delayWriteTimer.Start();
    }

    /// <summary>
    /// Recomputes compensations and pushes the resulting delay into the running channels.
    /// </summary>
    /// <remarks>
    /// Changing one device's manual trim can change the whole alignment, so every channel is
    /// re-evaluated rather than just the one that moved.
    /// </remarks>
    private void FlushDelayWrites()
    {
        _delayWriteTimer.Stop();

        if (_pendingDelayWrites.Count == 0)
        {
            return;
        }

        _pendingDelayWrites.Clear();

        RecomputeCompensations();
        RaiseAllDevicesChanged();
        QueueSettingsSave();

        if (_engine is null)
        {
            return;
        }

        foreach (DeviceViewModel device in Devices)
        {
            OutputChannel? channel = _engine.Channels
                .FirstOrDefault(c => string.Equals(c.DeviceKey, device.Key, StringComparison.Ordinal));

            if (channel is not null)
            {
                // ApplyUserDelayAsync, NOT ApplyDelayAsync: the latter glides small changes at about
                // 5 ms per second, which is correct for drift correction and useless for a slider.
                _ = channel.ApplyUserDelayAsync(
                    device.EffectiveDelayMs,
                    TimeSpan.FromMilliseconds(EngineTunables.FadeMs));
            }
        }
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
            StatusText = Localizer.Instance["Status.AutoMatchNone"];
            return;
        }

        double reference = involved.Min(d => d.EndpointVolume);

        foreach (DeviceViewModel device in involved)
        {
            device.EndpointVolume = reference;
        }

        // Write immediately rather than waiting for the debounce: this is a deliberate one-shot action.
        FlushEndpointVolumeWrites();

        StatusText = Localizer.Instance.Format(
            "Status.AutoMatchDone",
            involved.Count,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{reference * 100:0}"));
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
        // Guarded on the engine actually running. This used to test a ViewModel-held source device;
        // after the backend refactor that field no longer exists, and testing a stale flag here made
        // the method return early — ticking a device on while mirroring did nothing at all.
        if (_engine is null || !_engine.IsRunning)
        {
            return;
        }

        // Check if channel already exists
        if (_engine.Channels.Any(c => c.DeviceKey == device.Key))
        {
            return;
        }

        // THE FEEDBACK GUARD.
        //
        // This method used to create an output channel for any device the user ticked, including the
        // one being captured. A loopback capture of a device that is also an output feeds the device's
        // own playback back into itself, and the result is a runaway echo. Start() had this check;
        // this path did not, so ticking the capture-source device on produced exactly that.
        if (IsCaptureSource(device))
        {
            device.Status = Localizer.Instance["Status.DeviceSkippedSource"];
            StatusText = Localizer.Instance.Format("Status.SourceCannotBeOutput", device.DisplayName);
            return;
        }

        if (!_devices.TryResolveDevice(device.Endpoint.EndpointId, out MMDevice? resolved) || resolved is null)
        {
            device.Status = "unavailable";
            return;
        }

        _liveChannelDevices[device.Endpoint.EndpointId] = resolved;

        // Take the format from the RUNNING engine rather than re-probing the source device. The engine's
        // format is exactly what the existing chains were built at, and it avoids this class holding a
        // second reference to a device the backend already owns.
        if (_engine.CaptureFormat is not WaveFormat captureFormat)
        {
            return;
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

        StatusText = Localizer.Instance.Format("Status.Added", device.DisplayName, _engine.Channels.Count);
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

        StatusText = Localizer.Instance.Format("Status.Removed", _engine.Channels.Count);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _devices.Dispose();
    }

    private bool ResolveSourceDevice(out MMDevice? source)
    {
        // Priority 0: the input the user chose — any endpoint, cable or real device.
        string? chosenId = _settings.Engine.CaptureSinkDeviceId;

        if (!string.IsNullOrEmpty(chosenId)
            && _devices.TryResolveDevice(chosenId, out MMDevice? chosen)
            && chosen is not null)
        {
            source = chosen;
            return true;
        }

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
        DiagnosticsSummary = string.Join("   |   ", all.Select(SummariseChannel));

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

    /// <summary>
    /// Releases a backend that was built but never handed to the engine.
    /// </summary>
    /// <remarks>
    /// A no-op on the normal path, because a successful start clears the field. It exists for the runs
    /// that never reach <c>Start</c>: a channel that could not be built, or a stop before start.
    /// </remarks>
    private void DisposePendingBackend()
    {
        IAudioInputBackend? pending = _pendingBackend;
        _pendingBackend = null;

        if (pending is null)
        {
            return;
        }

        try
        {
            pending.Dispose();
        }
        catch (Exception)
        {
            // Releasing the capture endpoint must not become a start/stop failure of its own.
        }
    }

    /// <summary>
    /// Formats one channel's diagnostics in the current language.
    /// </summary>
    /// <remarks>
    /// Lives here rather than in Core so Core stays language-neutral: a diagnostics record is data,
    /// and only the presentation layer should know about languages.
    /// </remarks>
    private static string SummariseChannel(ChannelDiagnostics d)
    {
        Localizer loc = Localizer.Instance;
        var invariant = System.Globalization.CultureInfo.InvariantCulture;

        string fill = d.HasWindowData
            ? loc.Format(
                "Diagnostics.Fill",
                string.Create(invariant, $"{d.FillMinMs:0.#}"),
                string.Create(invariant, $"{d.FillMaxMs:0.#}"))
            : loc["Diagnostics.NoReads"];

        string format = d.RequiresRateConversion
            ? $"{d.CaptureSampleRate}→{d.DeviceSampleRate} Hz"
            : $"{d.DeviceSampleRate} Hz";

        if (d.RequiresChannelConversion)
        {
            format += $" {d.CaptureChannels}ch→{d.DeviceChannels}ch";
        }

        string signal = d.SignalPeak > 0.0
            ? loc.Format("Diagnostics.Signal", string.Create(invariant, $"{(d.SignalDbFs ?? 0):0}"))
            : loc["Diagnostics.SignalSilent"];

        var flags = new System.Text.StringBuilder();

        if (d.EndpointMuted)
        {
            flags.Append(", ").Append(loc["Diagnostics.EndpointMuted"]);
        }
        else if (!double.IsNaN(d.EndpointVolumeScalar) && d.EndpointVolumeScalar <= 0.001)
        {
            flags.Append(", ").Append(loc["Diagnostics.EndpointVolumeZero"]);
        }
        else if (!double.IsNaN(d.EndpointVolumeScalar) && d.EndpointVolumeScalar < 0.05)
        {
            flags.Append(", ").Append(loc.Format(
                "Diagnostics.EndpointVolume",
                string.Create(invariant, $"{d.EndpointVolumeScalar * 100:0}")));
        }

        if (d.PartialStarvedReads > 0 && d.WorstSilenceFraction > 0.01)
        {
            flags.Append(", ").Append(loc.Format(
                "Diagnostics.SilenceFraction",
                string.Create(invariant, $"{d.WorstSilenceFraction * 100:0}")));
        }

        if (d.StarvedInWindow)
        {
            flags.Append(", ").Append(loc["Diagnostics.Starved"]);
        }

        if (d.CorrectionSaturated)
        {
            flags.Append(", ").Append(loc.Format(
                "Diagnostics.CorrectionAtLimit",
                string.Create(invariant, $"{d.CorrectionPpm:+0;-0;0}")));
        }

        if (d.ResyncCount > 0)
        {
            flags.Append(", ").Append(loc.Format("Diagnostics.Resync", d.ResyncCount));
        }

        return $"{d.DeviceKey}: {d.CorrectionPpm:+0;-0;0} ppm, {fill}, {format}, {signal}{flags}";
    }

    private void RaiseAllDevicesChanged()
    {
        foreach (DeviceViewModel device in Devices)
        {
            device.RaiseLatencyChanged();
        }
    }
}

