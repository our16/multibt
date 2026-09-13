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
    /// The Windows default output before the mirror routed it into a cable, or null when it did not.
    /// </summary>
    /// <remarks>
    /// Held only while mirroring, so stopping can hand the machine back exactly as it was found.
    /// </remarks>
    private string? _defaultOutputBeforeMirror;

    /// <summary>The cable this app pointed the Windows default output at, or null when it did not.</summary>
    private string? _routedCableEndpointId;

    /// <summary>
    /// True while an input change is rebuilding the mirror, so the stop half does not undo the routing
    /// only for the start half to redo it a moment later.
    /// </summary>
    private bool _restartingForSource;

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

    /// <summary>The notice last announced, so an unchanged one is not announced again.</summary>
    private string? _lastNotice;

    /// <summary>Group-fader offset since the current drag began. Zero at rest.</summary>
    private double _lastNudge;
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

    /// <summary>Render endpoints offered as inputs: the usable ones, plus the current choice.</summary>
    /// <remarks>
    /// A typical machine reports 21 render endpoints and only 4 that can carry audio; the rest are phantom
    /// entries from unplugged HDMI outputs and duplicate driver copies of the same jack. Listing all of them
    /// is not helpful — 17 impossible choices bury the ones that work.
    ///
    /// The selected endpoint is kept even when it is currently inactive, so the box can still show what is
    /// chosen: dropping it would silently reset the selection to "auto".
    /// </remarks>
    public IReadOnlyList<AudioEndpointInfo> CaptureSinkCandidates
    {
        get
        {
            string? selectedId = _settings.Engine.CaptureSinkDeviceId;

            return VirtualCableDetector.OrderForSinkPicker(
                _devices.EnumerateRenderEndpoints(includeInactive: true)
                    .Where(e => e.IsActive
                        || string.Equals(e.EndpointId, selectedId, StringComparison.OrdinalIgnoreCase)));
        }
    }

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

            if (endpoint is null)
            {
                return null;
            }

            if (VirtualCableDetector.IsVirtualCableRenderEndpoint(endpoint.FriendlyName, endpoint.DeviceFriendlyName))
            {
                // A cable is inaudible and exists only to be captured, so every device stays controllable —
                // but only while Windows is actually rendering into it. Windows moves the default output on
                // its own (a Bluetooth speaker reconnecting is enough), and a cable that nothing feeds is
                // silence on every output with the app still reporting success, so say so plainly.
                return string.Equals(CurrentDefaultRenderEndpointId(), endpoint.EndpointId, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : Localizer.Instance.Format("Input.CableNotRoutedNote", endpoint.FriendlyName);
            }

            // A real device is captured from its own loopback, which only carries audio while Windows is
            // rendering into THAT device. Choosing one that is not the default therefore captures silence
            // on every output, while the real default keeps playing natively — the same shape of silent
            // failure as a starved cable, and just as impossible to guess from the outside.
            if (!string.Equals(CurrentDefaultRenderEndpointId(), endpoint.EndpointId, StringComparison.OrdinalIgnoreCase))
            {
                return Localizer.Instance.Format("Input.RealDeviceNotDefaultNote", endpoint.FriendlyName);
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
    /// Choosing does NOT touch the Windows default output. It used to, and that was wrong: selecting a
    /// cable immediately sent all system audio into a device nothing was yet consuming, so the machine went
    /// silent the moment the dropdown changed. The routing now happens in <c>StartAsync</c> and is undone on
    /// stop, which ties it to the mirror actually running.
    /// </remarks>
    public void SetCaptureSink(string? endpointId)
    {
        _settings.Engine.CaptureSinkDeviceId = string.IsNullOrWhiteSpace(endpointId) ? null : endpointId;

        StatusText = _settings.Engine.CaptureSinkDeviceId is null
            ? Localizer.Instance["Sink.Cleared"]
            : Localizer.Instance["Sink.Applied"];

        QueueSettingsSave();
        OnPropertyChanged(nameof(CaptureSinkEndpointId));
        OnPropertyChanged(nameof(HasInputNote));
        OnPropertyChanged(nameof(InputNote));
        RaiseNotice();

        if (_engine is not null)
        {
            _ = RestartForNewSourceAsync();
        }
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
        RaiseNotice();
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
            OnPropertyChanged(nameof(NoticeText));
            OnPropertyChanged(nameof(HasNotice));            }
        }
    }

    public bool HasSettingsWarning => !string.IsNullOrEmpty(_settingsWarning);

    /// <summary>
    /// The single most important thing to tell the user, or null when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// Deliberately ONE message chosen by priority. The UI renders it in a fixed-height line, so showing it,
    /// hiding it, or swapping it for another cannot move anything else on the page. Several messages shown
    /// at once would need more height than one line and would reintroduce the layout jumping.
    /// </remarks>
    public string? NoticeText
    {
        get
        {
            if (HasSettingsWarning)
            {
                return SettingsWarning;
            }

            if (HasCaptureSourceWarning)
            {
                return CaptureSourceWarning;
            }

            return InputNote;
        }
    }

    /// <summary>Whether <see cref="NoticeText"/> has something to say.</summary>
    public bool HasNotice => NoticeText is not null;

    /// <summary>
    /// Announces the notice, but only when it actually differs from what was last announced.
    /// </summary>
    /// <remarks>
    /// The notice depends on state that changes outside this class — whether Windows currently renders into
    /// the chosen input, which moves when the mirror starts or stops and can move on its own when a device
    /// reconnects. Without re-checking it, the banner kept claiming the mirror would be silent long after
    /// the routing had been fixed, which sends the user hunting for a problem that is not there.
    /// </remarks>
    private void RaiseNotice()
    {
        string? current = NoticeText;

        if (string.Equals(current, _lastNotice, StringComparison.Ordinal))
        {
            return;
        }

        _lastNotice = current;
        RaiseNotice();
    }

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

    /// <summary>A cheap fingerprint of the current endpoint set, used to notice device changes.</summary>
    /// <remarks>
    /// Watching the set itself rather than draining DeviceManager's notification queue: that queue is raised
    /// through DrainPendingChanges, which the ENGINE drains, so a second consumer would race it and whichever
    /// ran first would starve the other. Comparing a signature interferes with nothing and cannot miss a
    /// change.
    /// </remarks>
    private string DescribeEndpointSet() =>
        string.Join(
            '|',
            _devices.EnumerateRenderEndpoints(includeInactive: true)
                .Select(e => $"{e.EndpointId}:{(e.IsActive ? 1 : 0)}"));

    /// <summary>The endpoint fingerprint as of the last refresh.</summary>
    private string? _lastEndpointSignature;

    /// <summary>Diagnostics ticks since the last device-set check (one tick is 500 ms).</summary>
    private int _deviceWatchTicks;

    /// <summary>
    /// Rebuilds the device list when the set of endpoints has changed underneath us.
    /// </summary>
    /// <remarks>
    /// Without this a Bluetooth speaker that reconnects does not appear until the user presses refresh: the
    /// ViewModel simply never looked again.
    /// </remarks>
    private void RefreshDevicesIfChanged()
    {
        string signature = DescribeEndpointSet();

        if (string.Equals(signature, _lastEndpointSignature, StringComparison.Ordinal))
        {
            return;
        }

        RefreshDevices();
    }

    /// <summary>Re-enumerates endpoints and joins them to stored profiles.</summary>
    public void RefreshDevices()
    {
        _lastEndpointSignature = DescribeEndpointSet();

        // Device notifications are the realistic trigger for Windows moving the default output (a Bluetooth
        // speaker reconnecting does it routinely), so this is where the notice gets re-checked against
        // reality rather than left stating stale advice.
        RaiseNotice();

        // A device that has just come back and is still ticked needs its channel again, or it stays silent
        // until the user toggles it by hand.
        if (_engine is not null)
        {
            _ = ReconcileChannelsAsync();
        }

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

                // A position change re-places EVERY device, not just this one, because the distance delay is
                // measured against the nearest device: moving one can change what another should be given.
                if (e.PropertyName == nameof(DeviceViewModel.SpatialPlacementChanged))
                {
                    ApplySpatialPlacements();
                    QueueSettingsSave();
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
        // same gain sound different. That difference is the measurable half of a loudness mismatch; the
        // other half is speaker sensitivity, which no API exposes, so it stays a manual trim.
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
    /// <summary>
    /// The endpoint the mirror will actually capture from, without touching any hardware.
    /// </summary>
    /// <remarks>
    /// Mirrors ResolveSourceDevice's priority, which it has to: the warning below is about whether the
    /// captured endpoint receives audio, and comparing the PRIMARY device was wrong whenever the user had
    /// explicitly chosen a different input.
    /// </remarks>
    private string? ResolvedSourceEndpointId =>
        !string.IsNullOrEmpty(_settings.Engine.CaptureSinkDeviceId)
            ? _settings.Engine.CaptureSinkDeviceId
            : _primaryDevice?.Endpoint.EndpointId
              ?? _settings.Engine.SourceDeviceId;

    /// <summary>
    /// Whether the endpoint being captured is NOT the one Windows renders into.
    /// </summary>
    /// <remarks>
    /// This is the single condition that explains "the mirror runs and nothing is audible": loopback carries
    /// what the source endpoint is rendering, so a source that is not the default output captures silence.
    /// </remarks>
    public bool CaptureSourceIsNotDefault =>
        !string.IsNullOrEmpty(ResolvedSourceEndpointId)
        && !string.IsNullOrEmpty(_defaultRenderEndpointId)
        && !string.Equals(ResolvedSourceEndpointId, _defaultRenderEndpointId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Actionable warning for the UI, or null when the capture source is correct.</summary>
    /// <remarks>
    /// Names the RESOLVED source, and tolerates not being able to resolve it. The primary device used to be
    /// dereferenced here with the null-forgiving operator, which was safe only while the condition above
    /// guaranteed it was non-null. It does not any more — an explicit input choice outranks the primary —
    /// and during construction the primary is always null because RefreshDevices binds it later, so the
    /// old code threw NullReferenceException before the window was ever shown.
    /// </remarks>
    public string? CaptureSourceWarning =>
        CaptureSourceIsNotDefault && ResolvedSourceEndpointName is string name
            ? Localizer.Instance.Format("Capture.NotDefault", name)
            : null;

    /// <summary>
    /// Display name of the endpoint the mirror will capture, or null when it cannot be resolved.
    /// </summary>
    private string? ResolvedSourceEndpointName
    {
        get
        {
            string? id = ResolvedSourceEndpointId;

            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            return _devices
                .EnumerateRenderEndpoints(includeInactive: true)
                .FirstOrDefault(e => string.Equals(e.EndpointId, id, StringComparison.OrdinalIgnoreCase))
                ?.FriendlyName;
        }
    }

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
        RaiseNotice();
    }

    /// <summary>
    /// Recomputes every device's automatic compensation from the current measurements and mode.
    /// </summary>
    public void RecomputeCompensations()
    {
        // HasMeasurement is passed EXPLICITLY.
        //
        // It used to be omitted, and it defaults to true — so every device was reported as measured at
        // whatever value it stored, which is 0 in practice because nothing measures latency here. A set
        // that is entirely "measured at 0 ms" has a reference of 0 and yields 0 compensation for everyone:
        // the mode appeared to work and did nothing. The flag is what distinguishes a real number from an
        // assumption, and it decides whether a transport estimate is used instead.
        CompensationTarget[] targets = Devices
            .Select(d => new CompensationTarget(
                d.Key,
                d.Profile.Latency.MeasuredDelayRelRefMs,
                d.Endpoint.Transport,
                d.Profile.Latency.HasMeasurement))
            .ToArray();

        IReadOnlyDictionary<string, LatencyModel.DeviceCompensation> plan =
            LatencyModel.ComputeCompensationPlan(targets, Mode);

        foreach (DeviceViewModel device in Devices)
        {
            if (plan.TryGetValue(device.Key, out LatencyModel.DeviceCompensation? entry))
            {
                device.Profile.Latency.CompensationMs = entry.CompensationMs;
                device.CompensationBasis = entry.Basis;
            }
        }

        IReadOnlyDictionary<string, double> compensations = plan
            .ToDictionary(pair => pair.Key, pair => pair.Value.CompensationMs, StringComparer.Ordinal);

        SystemLatencyMs = LatencyModel.ComputeSystemLatencyMs(targets, compensations, Mode);
        ExceedsLipSync = LatencyModel.ExceedsLipSyncThreshold(SystemLatencyMs);
        OnPropertyChanged(nameof(SystemLatencySummary));
    }

    /// <summary>
    /// Group fader position, as an offset from where the drag started.
    /// </summary>
    /// <remarks>
    /// This is a MOMENTARY control, not a stored level: each movement pushes every ticked device's volume by
    /// the same amount, and it returns to zero when the drag ends because the devices themselves now hold the
    /// result. It has no range of its own -- the travel available is whatever the devices allow, and it is
    /// asymmetric when they sit at different levels.
    /// </remarks>
    public double MasterNudge
    {
        get => _lastNudge;
        set
        {
            double increment = value - _lastNudge;

            if (Math.Abs(increment) < 0.0001)
            {
                return;
            }

            double applied = NudgeAllVolumes(increment);
            _lastNudge += applied;

            // Notified even when the value was clamped, so the thumb is pushed back to the rail it has hit
            // instead of drifting away from the levels it is driving.
            OnPropertyChanged(nameof(MasterNudge));
            OnPropertyChanged(nameof(MasterNudgeLabel));
        }
    }

    /// <summary>The offset currently being applied, e.g. "+12%".</summary>
    public string MasterNudgeLabel => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{_lastNudge * 100:+0;-0;0}%");

    /// <summary>Ends a drag: the devices hold the result, so the fader returns to its rest position.</summary>
    public void EndMasterNudge()
    {
        _lastNudge = 0.0;
        OnPropertyChanged(nameof(MasterNudge));
        OnPropertyChanged(nameof(MasterNudgeLabel));
    }

    /// <summary>
    /// Pushes every ticked device's volume by the same amount, stopping at the first device to hit a rail.
    /// </summary>
    /// <remarks>
    /// The limit is the whole point: pushing past it would either clip a device at 100 % or silently park one
    /// at 0 %, and both destroy the balance the group fader exists to preserve. Devices whose volume cannot
    /// be read are excluded from the limit rather than treated as 0, which would otherwise pin the whole group
    /// in place.
    /// </remarks>
    /// <returns>The offset actually applied, which is the requested one unless a device hit a rail.</returns>
    private double NudgeAllVolumes(double delta)
    {
        List<DeviceViewModel> involved = Devices
            .Where(d => d.IsEnabled && !double.IsNaN(d.EndpointVolume))
            .ToList();

        if (involved.Count == 0)
        {
            return 0.0;
        }

        double headroom = involved.Min(d => 1.0 - d.EndpointVolume);
        double floor = involved.Min(d => d.EndpointVolume);
        double applied = Math.Clamp(delta, -floor, headroom);

        if (Math.Abs(applied) < 0.0001)
        {
            return 0.0;
        }

        foreach (DeviceViewModel device in involved)
        {
            device.EndpointVolume = Math.Clamp(device.EndpointVolume + applied, 0.0, 1.0);
            QueueEndpointVolumeWrite(device);
        }

        return applied;
    }

    /// <summary>
    /// Pushes every device's configured position to its channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Positions come from the saved settings, so an unplaced device yields unity gain and zero delay and this
    /// method changes nothing about it. That is what makes the feature safe to leave switched on: a set-up that
    /// has never touched it behaves exactly as it did before.
    /// </para>
    /// <para>
    /// Only ENABLED devices take part, matching the rest of the app: a ticked-out device is not in the mirror,
    /// so it must not influence the alignment of the ones that are. In particular it must not drag the
    /// reference distance, which the distance delay is measured against.
    /// </para>
    /// </remarks>
    public void ApplySpatialPlacements()
    {
        var positions = new Dictionary<string, MultiBT.Core.Sync.DevicePosition>(StringComparer.Ordinal);

        foreach (DeviceViewModel device in Devices.Where(d => d.IsEnabled && d.Profile.Spatial.IsConfigured))
        {
            // The distance only takes part when the device says so. With it off, every device sits on the same
            // 1 m circle, so the delay is zero and the gain is unity for all of them, and a direction changes
            // nothing but the stereo image -- which is exactly what the app did before a position could be
            // clicked at all.
            positions[device.Key] = device.Profile.Spatial.UseDistance
                ? device.Profile.Spatial.ToPosition()
                : device.SpatialDirection.AtDistance(MultiBT.Core.Sync.SpatialMixer.DirectionRadiusMetres);
        }

        IReadOnlyDictionary<string, MultiBT.Core.Sync.SpatialPlacement> placements =
            MultiBT.Core.Sync.SpatialMixer.ComputePlacements(positions);

        // Hand every device what it is being given, whether or not it has a channel right now: the picker states
        // these numbers, and a stopped mirror showing the wrong ones would be worse than showing none.
        foreach (DeviceViewModel device in Devices)
        {
            device.SetAppliedSpatialPlacement(
                placements.TryGetValue(device.Key, out MultiBT.Core.Sync.SpatialPlacement placed)
                    ? placed
                    : MultiBT.Core.Sync.SpatialMixer.Unity);
        }

        if (_engine is null)
        {
            return;
        }

        foreach (OutputChannel channel in _engine.Channels)
        {
            if (placements.TryGetValue(channel.DeviceKey, out MultiBT.Core.Sync.SpatialPlacement placement))
            {
                channel.SetSpatialGains(placement.LeftGain, placement.RightGain);
                channel.SetSpatialDelayMs(placement.DelayMs);
            }
            else
            {
                // Not placed: back to unity, so removing a position takes effect immediately.
                channel.SetSpatialGains(1.0, 1.0);
                channel.SetSpatialDelayMs(0.0);
            }
        }
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
                // the user's Windows volume settings, so resuming restores exactly what was there. The
                // master volume rides on the same multiplier, so resuming keeps the level the user set.
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
            //
            // Flagged as a restart so Stop() leaves the Windows routing alone: without this the default
            // output would flip back and forth on every input change.
            _restartingForSource = true;

            try
            {
                await StopAsync().ConfigureAwait(true);
            }
            finally
            {
                _restartingForSource = false;
            }

            Start();
        }
        catch (Exception ex)
        {
            StatusText = Localizer.Instance.Format("Status.CouldNotStart", ex.Message);
        }
    }



    /// <summary>
    /// Points the Windows default output at the resolved source, when that source is a virtual cable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cable only carries audio that Windows renders into it, so choosing one as the input means the
    /// default output has to move there, or every output stays silent while the app reports success.
    /// </para>
    /// <para>
    /// Only a cable needs this. A real endpoint is already whatever the user chose as default, and moving
    /// it would be an unrequested change to their system.
    /// </para>
    /// <para>
    /// The previous default is remembered so <see cref="RestoreDefaultOutput"/> can put it back when the
    /// mirror stops, which is what stops a stopped mirror from leaving the machine routed into an
    /// inaudible cable.
    /// </para>
    /// </remarks>
    /// <returns>A message describing a failure, or null when routing is correct.</returns>
    private string? EnsureWindowsRendersIntoSource(MMDevice source)
    {
        if (!VirtualCableDetector.IsVirtualCableRenderEndpoint(source.FriendlyName, source.DeviceFriendlyName))
        {
            return null;
        }

        // Asked of Windows rather than of a cached id: Windows moves the default output on its own — a
        // Bluetooth speaker reconnecting does it routinely — so a cached answer is how the app ends up
        // capturing a cable that nothing is feeding.
        string? current = CurrentDefaultRenderEndpointId();

        if (string.Equals(current, source.ID, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!DefaultEndpointSwitcher.TrySetDefault(source.ID, out string? error))
        {
            return Localizer.Instance.Format("Sink.SwitchFailed", error);
        }

        _defaultOutputBeforeMirror = current;
        _routedCableEndpointId = source.ID;
        _defaultRenderEndpointId = source.ID;
        return null;
    }

    /// <summary>
    /// Puts the Windows default output back where it was before the mirror routed it into a cable.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative: it restores only when the default is still the cable this app set. If the
    /// user has since chosen a different default, that choice stands — undoing it would be the app deciding
    /// it knows better about the machine's audio routing.
    /// </remarks>
    private void RestoreDefaultOutput()
    {
        string? previous = _defaultOutputBeforeMirror;
        string? cable = _routedCableEndpointId;

        _defaultOutputBeforeMirror = null;
        _routedCableEndpointId = null;

        if (previous is null || cable is null)
        {
            return;
        }

        if (!string.Equals(CurrentDefaultRenderEndpointId(), cable, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (DefaultEndpointSwitcher.TrySetDefault(previous, out _))
        {
            _defaultRenderEndpointId = previous;
        }
    }

    /// <summary>
    /// The render endpoint Windows currently uses as default, or null when it cannot be read.
    /// </summary>
    private string? CurrentDefaultRenderEndpointId()
    {
        if (!_devices.TryGetDefaultRenderDevice(out MMDevice? current) || current is null)
        {
            return null;
        }

        string id = current.ID;
        current.Dispose();
        return id;
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
            device.Status = Localizer.Instance["Status.Failed"];
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


        // Route Windows into the cable, but only NOW — at start — and undo it at stop.
        //
        // A cable carries only what Windows renders into it, so choosing one as the input is meaningless
        // unless the default output is pointed at it. Doing that at SELECTION time instead is what made
        // the machine go silent: the dropdown changed the system's routing while nothing was consuming
        // the cable yet, so every application's audio went somewhere inaudible. Tying the switch to the
        // mirror's lifetime makes "Windows renders into the cable" true exactly while MultiBT is running.
        string? routingFailure = EnsureWindowsRendersIntoSource(source);

        // The switch above changes whether the notice is still true.
        RaiseNotice();

        if (routingFailure is not null)
        {
            // Refuse rather than run: the mirror would be silent by construction, and reporting success
            // while no audio can arrive is the one outcome worth blocking.
            StatusText = routingFailure;
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

        foreach (DeviceViewModel device in selected)
        {
            // Skip the capture source device - using it as output causes feedback/echo
            // Never open an output to the endpoint we are capturing: that is a feedback loop.
            if (IsCaptureSource(device))
            {
                device.Status = Localizer.Instance["Status.DeviceSkippedSource"];
                continue;
            }

            // EACH DEVICE FAILS ON ITS OWN.
            //
            // This used to sit inside one try around the whole loop, so a single endpoint that refused to
            // open aborted the entire start: every other device stayed silent too, and the only sound left
            // was whatever Windows happened to render natively. One bad endpoint is a normal occurrence —
            // an unplugged speaker, a virtual cable that rejects raw mode — and it must cost its own
            // channel, not the whole mirror.
            try
            {
                // Fresh resolution per activation: a cached MMDevice dies on sleep/resume even
                // though the endpoint still enumerates.
                if (!_devices.TryResolveDevice(device.Endpoint.EndpointId, out MMDevice? resolved) || resolved is null)
                {
                    device.Status = Localizer.Instance["Status.DeviceUnavailable"];
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
                device.Status = RunningStatus(channel.AppliedDelayMs, correctionPpm: null, resynced: !glided);
            }
            catch (Exception ex)
            {
                // The device keeps its own failure, in the row where the user is looking.
                device.Status = $"{Localizer.Instance["Status.DeviceOpenFailed"]}: {ex.Message}";
            }
        }

        try
        {
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

            // Channels are constructed at unity gain, so the stored master volume has to be applied now or
            // it would only take effect the next time the slider moves.
            ApplyGainsToEngine();

            // Same reasoning for positions: a channel starts centred and undelayed, so the saved placement has
            // to be pushed to it or the device would only be placed the next time someone edited it.
            ApplySpatialPlacements();

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

        // Hand the machine's audio routing back unless this stop is only a step of restarting: leaving the
        // system pointed at an inaudible cable after the user stopped mirroring is silence with no cause
        // the user could possibly guess.
        if (!_restartingForSource)
        {
            RestoreDefaultOutput();

            // Restoring the default can change whether the notice still applies.
            RaiseNotice();
        }

        OnPropertyChanged(nameof(HasInputNote));
        OnPropertyChanged(nameof(InputNote));
        RaiseNotice();
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
            bool written = EndpointVolumeReader.TryWrite(_devices, endpointId, volumeScalar: volume);

            // Remembered as a device preference. Written here rather than on every slider tick, so a
            // drag produces one settings write per burst instead of one per pixel.
            DeviceViewModel? owner = Devices.FirstOrDefault(d => MatchesEndpoint(d, endpointId));
            if (owner is not null)
            {
                owner.Profile.Audio.DesiredVolume = volume;
                QueueSettingsSave();
            }

            // A write that did not land is reported rather than swallowed.
            //
            // The slider shows what the user asked for, never what the endpoint achieved, so without this a
            // device that refuses the write looks like a slider that does nothing -- with no reason given,
            // which is exactly the confusion the per-device volume display exists to remove.
            if (!written)
            {
                StatusText = Localizer.Instance.Format(
                    "Status.VolumeFailed",
                    owner?.DisplayName ?? endpointId);
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
            device.Status = Localizer.Instance["Status.DeviceUnavailable"];
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

        device.Status = RunningStatus(channel.AppliedDelayMs, correctionPpm: null, resynced: !glided);

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
            device.Status = Localizer.Instance["Status.DeviceStopped"];
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

    /// <summary>
    /// Re-attaches enabled devices that have no channel.
    /// </summary>
    private async Task ReconcileChannelsAsync()
    {
        if (_engine is null)
        {
            return;
        }

        foreach (DeviceViewModel device in Devices.Where(d => d.IsEnabled).ToList())
        {
            if (_engine.Channels.Any(c => c.DeviceKey == device.Key))
            {
                continue;
            }

            await AddChannelToEngineAsync(device).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The "running" line for a device, composed from the localisation table.
    /// </summary>
    /// <remarks>
    /// Parts joined with " · " rather than one template per case: the delay, the drift correction and the
    /// resync marker are independent facts, and a template per combination would drift apart as they change.
    /// The ppm figure keeps its own unit, which is a unit and not a word to translate.
    /// </remarks>
    private static string RunningStatus(double delayMs, double? correctionPpm, bool resynced)
    {
        Localizer loc = Localizer.Instance;
        var parts = new List<string> { loc["Status.Running"] };

        if (correctionPpm is double ppm)
        {
            parts.Add(ppm.ToString("+0;-0;0", System.Globalization.CultureInfo.InvariantCulture) + " ppm");
        }

        parts.Add(loc.Format(
            "Status.Delay",
            delayMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));

        if (resynced)
        {
            parts.Add(loc["Status.Resynced"]);
        }

        return string.Join(" · ", parts);
    }

    private void RefreshDiagnostics()
    {
        // Watch for devices appearing and disappearing. Every 4th tick, so the endpoint enumeration does not
        // run at the full 500 ms rate.
        if (++_deviceWatchTicks >= 4)
        {
            _deviceWatchTicks = 0;
            RefreshDevicesIfChanged();
        }

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
                device.Status = RunningStatus(diagnostics.AppliedDelayMs, diagnostics.CorrectionPpm, resynced: false);
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

