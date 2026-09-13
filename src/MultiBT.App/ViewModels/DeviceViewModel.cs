using System.Globalization;
using System.Text;
using MultiBT.App.Localization;
using MultiBT.Core.Audio;
using MultiBT.Core.Config;
using MultiBT.Core.Devices;
using MultiBT.Core.Sync;

namespace MultiBT.App.ViewModels;

/// <summary>
/// One row in the device list: a live endpoint joined to its stored profile record.
/// </summary>
/// <remarks>
/// The three latency numbers are surfaced separately on purpose — measured, compensated, and
/// the user's trim — because collapsing them into one "delay" field is what makes a UI lie about
/// which value it is actually applying. See docs/SPEC.md §9.1.
/// </remarks>
public sealed class DeviceViewModel : ObservableObject
{
    private bool _isEnabled;
    private double _manualOffsetMs;
    private LatencyModel.CompensationBasis _compensationBasis;
    private string? _status;
    private bool _isPrimary;
    private double _endpointVolume = double.NaN;
    private bool _endpointMuted;

    public DeviceViewModel(
        AudioEndpointInfo endpoint,
        DeviceProfile? profile,
        DeviceIdentityMatcher.MatchKind match)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        Endpoint = endpoint;
        MatchKind = match;

        if (profile is null)
        {
            // First sighting: create a record and adopt the endpoint's identities.
            Profile = new DeviceProfile
            {
                Key = BuildKey(endpoint),
                DisplayName = endpoint.FriendlyName,
                Transport = endpoint.Transport,
                Identities = new DeviceIdentities
                {
                    EndpointId = endpoint.EndpointId,
                    InstanceId = endpoint.InstanceId,
                    FriendlyName = endpoint.FriendlyName,
                },
            };
        }
        else
        {
            Profile = profile;

            // A name-only match means the endpoint identity changed. The measurement was taken
            // against a different identity, so it must be marked stale rather than silently
            // reused. See docs/SPEC.md §5.3.
            if (!DeviceIdentityMatcher.PreservesMeasurement(match))
            {
                Profile.Latency.InvalidateMeasurement($"endpoint identity changed (matched by {match})");
            }
        }

        // Remembered. null means no explicit choice was made yet, so a device with a stored profile
        // keeps starting enabled rather than silently switching off for existing users.
        _isEnabled = profile is not null && Profile.Audio.Enabled != false;
        _manualOffsetMs = Profile.Latency.ManualOffsetMs;
    }

    /// <summary>The live endpoint this row represents.</summary>
    public AudioEndpointInfo Endpoint { get; }

    /// <summary>The stored profile record backing this row.</summary>
    public DeviceProfile Profile { get; }

    /// <summary>How the stored record was matched to the live endpoint.</summary>
    public DeviceIdentityMatcher.MatchKind MatchKind { get; }

    public string Key => Profile.Key;

    public string DisplayName => string.IsNullOrEmpty(Profile.DisplayName)
        ? Endpoint.FriendlyName
        : Profile.DisplayName;

    /// <summary>
    /// Why a device looks present but produces nothing, or null when there is nothing to explain.
    /// </summary>
    /// <remarks>
    /// Kept separate from the transport type that used to sit here. The type is a category the user can see
    /// for themselves and no longer belongs in the output list; a paired-but-disconnected Bluetooth device
    /// is the opposite — it is the explanation for silence, and losing it would turn an obvious cause back
    /// into a mystery.
    /// </remarks>
    public string? ConnectionHint => Endpoint.IsPairedButDisconnected
        ? Localizer.Instance["Transport.PairedNotConnected"]
        : null;

    /// <summary>Whether <see cref="ConnectionHint"/> has something to say.</summary>
    public bool HasConnectionHint => ConnectionHint is not null;

    /// <summary>Whether this device participates in the mirror.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                // Remembered as a device preference so the user does not re-tick devices every session.
                Profile.Audio.Enabled = value;
                IsEnabledChanged?.Invoke(this, EventArgs.Empty);
                EnabledChangedForPersistence?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Raised when IsEnabled changes, for the engine to react immediately.</summary>
    public event EventHandler? IsEnabledChanged;

    /// <summary>Raised when a preference changed that should be auto-saved.</summary>
    public event EventHandler? EnabledChangedForPersistence;

    /// <summary>Whether this device is the primary capture source.</summary>
    public bool IsPrimary
    {
        get => _isPrimary;
        set
        {
            if (SetProperty(ref _isPrimary, value))
            {
                IsPrimaryChanged?.Invoke(this, EventArgs.Empty);

                // The UI shows "主设备" on the primary row and "设为主设备" on the others, so the
                // inverse has to notify too or both buttons end up in the wrong state.
                OnPropertyChanged(nameof(IsNotPrimary));
            }
        }
    }

    /// <summary>Inverse of <see cref="IsPrimary"/>, for binding the "set as primary" button.</summary>
    public bool IsNotPrimary => !_isPrimary;

    /// <summary>Raised when IsPrimary changes.</summary>
    public event EventHandler? IsPrimaryChanged;

    /// <summary>
    /// This device's own Windows endpoint volume, 0..1, or NaN when it cannot be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the volume the UI drives.</b> It is the device's real Windows volume, so 100 % means
    /// the device is at its full capability — which a chain-gain multiplier on top of it could never
    /// express: with the endpoint at 61 %, a multiplier of 1.0 still tops out at 61 %.
    /// </para>
    /// <para>
    /// It is also initialised FROM the device, so the slider shows reality instead of an invented
    /// number.
    /// </para>
    /// </remarks>
    public double EndpointVolume
    {
        get => _endpointVolume;
        set
        {
            if (SetProperty(ref _endpointVolume, value))
            {
                OnPropertyChanged(nameof(EndpointVolumeLabel));
                OnPropertyChanged(nameof(EndpointVolumePercent));
                OnPropertyChanged(nameof(HasEndpointVolume));
            }
        }
    }

    /// <summary>Whether an endpoint volume could be read for this device.</summary>
    public bool HasEndpointVolume => !double.IsNaN(_endpointVolume);

    /// <summary>Endpoint volume as a percentage, or a dash when unknown.</summary>
    public string EndpointVolumePercent => double.IsNaN(_endpointVolume)
        ? "—"
        : string.Create(CultureInfo.InvariantCulture, $"{_endpointVolume * 100:0}%");

    /// <summary>Localised volume label including the percentage.</summary>
    public string EndpointVolumeLabel => double.IsNaN(_endpointVolume)
        ? Localizer.Instance["Volume.Unknown"]
        : Localizer.Instance.Format("Volume.Display", string.Create(CultureInfo.InvariantCulture, $"{_endpointVolume * 100:0}"));

    /// <summary>
    /// A warning about this device's volume, or <c>null</c> when there is nothing to report.
    /// </summary>
    /// <remarks>
    /// Only problems are surfaced in the device row: the slider beside it already shows the value, so
    /// repeating "device volume 61 %" as text would be noise. A mute or an unreadable control, on the
    /// other hand, is exactly what the user needs told — a muted endpoint makes a provably-correct
    /// channel completely inaudible.
    /// </remarks>
    public string? VolumeWarning => double.IsNaN(_endpointVolume)
        ? Localizer.Instance["Volume.Unknown"]
        : _endpointMuted
            ? Localizer.Instance["Volume.MutedWarning"]
            : null;

    /// <summary>Whether <see cref="VolumeWarning"/> has something to show.</summary>
    public bool HasVolumeProblem => VolumeWarning is not null;

    /// <summary>Whether this device's endpoint is muted in Windows.</summary>
    public bool EndpointMuted
    {
        get => _endpointMuted;
        set
        {
            if (SetProperty(ref _endpointMuted, value))
            {
                OnPropertyChanged(nameof(EndpointVolumeLabel));
            }
        }
    }

    /// <summary>Signed user trim in ms. Positive makes this device later.</summary>
    public double ManualOffsetMs
    {
        get => _manualOffsetMs;
        set
        {
            if (SetProperty(ref _manualOffsetMs, value))
            {
                // Remembered per device: the value lives in the device profile, which is persisted with
                // the rest of the settings.
                Profile.Latency.ManualOffsetMs = value;

                OnPropertyChanged(nameof(LatencySummary));
                OnPropertyChanged(nameof(EffectiveDelayMs));
                OnPropertyChanged(nameof(DelayLabel));
            }
        }
    }

    /// <summary>
    /// The delay actually being applied, in ms.
    /// </summary>
    /// <remarks>
    /// Always display this resolved value rather than the raw trim: when
    /// <c>CompensationMs</c> is 0 a negative trim has nowhere to move, so the raw number would
    /// misrepresent what the device receives.
    /// </remarks>
    public double EffectiveDelayMs => LatencyModel.ComputeEffectiveDelayMs(Profile.Latency);

    /// <summary>
    /// What the automatic part of this device's delay is based on: a measurement, an estimate, or nothing.
    /// </summary>
    /// <remarks>
    /// Shown so the number is attributable. A delay that appears with no explanation is indistinguishable
    /// from a bug, and the two possible sources — a measurement and a per-transport estimate — carry very
    /// different confidence.
    /// </remarks>
    public LatencyModel.CompensationBasis CompensationBasis
    {
        get => _compensationBasis;
        set
        {
            if (SetProperty(ref _compensationBasis, value))
            {
                OnPropertyChanged(nameof(LatencySummary));
            }
        }
    }

    /// <summary>How far one press of the delay step buttons moves the value, in ms.</summary>
    public const double DelayStepMs = 5.0;

    /// <summary>Adds one step to the manual delay, stopping at the configured maximum.</summary>
    public void IncreaseDelay() => ManualOffsetMs = Math.Clamp(ManualOffsetMs + DelayStepMs, 0.0, 2000.0);

    /// <summary>Subtracts one step from the manual delay, stopping at zero.</summary>
    public void DecreaseDelay() => ManualOffsetMs = Math.Clamp(ManualOffsetMs - DelayStepMs, 0.0, 2000.0);

    /// <summary>
    /// This device's direction: 0 is straight ahead, and each step is another 45 degrees to the right.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An index rather than three coordinate boxes. "Where is this speaker" has eight sensible answers, and
    /// three number fields in a 58 px row ask the question badly. The coordinates are still what gets saved:
    /// the index is derived from them, so a position written by an older version of the app lands on the
    /// nearest direction instead of being thrown away.
    /// </para>
    /// <para>
    /// Straight ahead is the default, and for a device nobody has placed it is also the honest answer: an
    /// unplaced device has no direction to report, and straight ahead is the direction that does nothing.
    /// </para>
    /// </remarks>
    public int SpatialDirectionIndex
    {
        get => SpatialMixer.NearestDirectionIndex(Profile.Spatial.Right, Profile.Spatial.Front);
        set
        {
            DevicePosition position = SpatialMixer.DirectionPosition(value);

            SetSpatial(position.Right, position.Front, position.Up);
        }
    }

    /// <summary>
    /// Writes all three axes at once.
    /// </summary>
    /// <remarks>
    /// One method for three properties because they are one fact: the mixer needs a position, not three
    /// independent numbers, and notifying them separately could let a listener observe a half-applied
    /// position -- the reference distance is shared, so a partial update is a real intermediate state.
    /// </remarks>
    private void SetSpatial(double right, double front, double up)
    {
        if (Math.Abs(Profile.Spatial.Right - right) < 0.0001
            && Math.Abs(Profile.Spatial.Front - front) < 0.0001
            && Math.Abs(Profile.Spatial.Up - up) < 0.0001)
        {
            return;
        }

        Profile.Spatial.Set(right, front, up);

        // One notification for one fact. The picker re-reads the direction from the coordinates, and
        // MainViewModel re-places EVERY device on this change, because the reference distance is shared.
        OnPropertyChanged(nameof(SpatialDirectionIndex));
    }

    /// <summary>Manual delay as shown in the UI, e.g. "125 ms".</summary>
    public string DelayLabel => string.Create(
        CultureInfo.InvariantCulture,
        $"{_manualOffsetMs:0} ms");

    /// <summary>Read-only summary line for the UI, localised.</summary>
    public string LatencySummary
    {
        get
        {
            Localizer loc = Localizer.Instance;
            DeviceLatencySettings latency = Profile.Latency;

            string trim = loc.Format("Latency.Trim", $"{latency.ManualOffsetMs:+0;-0;0}");
            string effective = loc.Format("Latency.Effective", $"{EffectiveDelayMs:0}");

            // No measurement: state what the automatic part was based on instead, so the effective number
            // is never unattributed.
            if (!latency.HasMeasurement)
            {
                string automatic = _compensationBasis == LatencyModel.CompensationBasis.Estimated
                    ? loc.Format("Latency.Estimated", $"{latency.CompensationMs:+0;-0;0}")
                    : loc["Latency.NoCompensation"];

                return $"{automatic} · {trim} → {effective}";
            }

            string stale = latency.MeasurementIsStale ? " · " + loc["Latency.Stale"] : string.Empty;

            string quality = latency.MeasurementQuality switch
            {
                MeasurementQuality.High => $"{latency.MeasurementSpreadMs:0.0} ms",
                MeasurementQuality.Low => $"{latency.MeasurementSpreadMs:0.0} ms ({loc["Latency.LowConfidence"]})",
                _ => loc["Status.Failed"],
            };

            return $"{loc.Format("Latency.Measured", $"{latency.MeasuredDelayRelRefMs:0}")} ±{quality} · "
                   + $"{loc.Format("Latency.Compensation", $"{latency.CompensationMs:+0;-0;0}")} · "
                   + $"{trim} → {effective}{stale}";
        }
    }

    /// <summary>Runtime status shown next to the device.</summary>
    /// <remarks>
    /// Null means nothing has happened to this device yet, and that is displayed as the localised idle
    /// text rather than a stored English word. Idle is the one state with no event to recompute it, so a
    /// stored copy kept the language it was created in: a Chinese window showed "Idle" on every row.
    /// </remarks>
    public string Status
    {
        get => _status ?? Localizer.Instance["Status.Idle"];
        set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// Re-raises every localised and computed property.
    /// </summary>
    /// <remarks>
    /// Needed after a language switch: indexer-bound XAML updates itself, but strings composed in C#
    /// (transport label, latency summary, status) have to be rebuilt and announced.
    /// </remarks>
    public void RaiseLocalisedText()
    {
        OnPropertyChanged(nameof(ConnectionHint));
        OnPropertyChanged(nameof(LatencySummary));
        OnPropertyChanged(nameof(EndpointVolumeLabel));
        OnPropertyChanged(nameof(EndpointVolumePercent));

        // A device that is idle has no runtime text of its own, so re-reading Status is what turns its
        // placeholder into the new language.
        OnPropertyChanged(nameof(Status));
    }

    /// <summary>Refreshes every computed property after the underlying settings change.</summary>
    public void RaiseLatencyChanged()
    {
        OnPropertyChanged(nameof(LatencySummary));
        OnPropertyChanged(nameof(EffectiveDelayMs));
    }

    /// <summary>
    /// Builds a stable profile key from the endpoint.
    /// </summary>
    /// <remarks>
    /// Derived from the friendly name plus a short hash of the instance id, so it is readable in
    /// the settings file yet still distinguishes two identical-looking devices. It is NOT the
    /// endpoint id — that value is opaque and changes on driver reinstall.
    /// </remarks>
    private static string BuildKey(AudioEndpointInfo endpoint)
    {
        var builder = new StringBuilder();
        foreach (char c in endpoint.FriendlyName.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        string slug = builder.ToString().Trim('-');
        if (slug.Length == 0)
        {
            slug = "device";
        }

        int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(endpoint.InstanceId);
        return $"{slug}-{hash & 0xFFFF:x4}";
    }
}
