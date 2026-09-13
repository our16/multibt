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

    /// <summary>What the engine is applying to this device, for the picker's readout. Unity until pushed.</summary>
    private SpatialPlacement _appliedPlacement = SpatialMixer.Unity;

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

    /// <summary>The closest a device may be placed, in metres.</summary>
    public const double MinimumSpatialDistanceMetres = 0.5;

    /// <summary>The farthest a device may be placed, in metres.</summary>
    public const double MaximumSpatialDistanceMetres = 5.0;

    /// <summary>
    /// The key for each direction name, in the order the table stores them.
    /// </summary>
    /// <remarks>
    /// Written out rather than composed from an index at the point of use. A built-up key
    /// ("Spatial.Dir." + i) compiles and reads fine, and hides the keys from every search for what the app
    /// actually uses -- including this project's own dead-key audit and its localisation gate.
    /// </remarks>
    private static readonly string[] DirectionKeys =
    [
        "Spatial.Dir.0",
        "Spatial.Dir.1",
        "Spatial.Dir.2",
        "Spatial.Dir.3",
        "Spatial.Dir.4",
        "Spatial.Dir.5",
        "Spatial.Dir.6",
        "Spatial.Dir.7",
    ];

    /// <summary>
    /// This device's direction as a unit vector, however far away it has been placed.
    /// </summary>
    /// <remarks>
    /// A device nobody has placed reads as straight ahead, which is the direction that does nothing: one metre
    /// dead ahead has nothing to pan towards and nothing to be delayed against, so "nobody placed this" and
    /// "placed dead ahead" sound identical rather than being two code paths.
    /// </remarks>
    public DevicePosition SpatialDirection
    {
        get
        {
            DevicePosition position = Profile.Spatial.ToPosition();

            return position.IsOrigin
                ? new DevicePosition(0.0, SpatialMixer.DirectionRadiusMetres, 0.0)
                : position.AtDistance(SpatialMixer.DirectionRadiusMetres);
        }
    }

    /// <summary>
    /// How far away this device is, in metres.
    /// </summary>
    /// <remarks>
    /// A separate setting from the direction because it is a separate question, and -- with
    /// <see cref="SpatialUseDistance"/> off -- a separate promise: a direction always moves the stereo image,
    /// while the distance only matters once the user has said that it should.
    /// </remarks>
    public double SpatialDistance
    {
        get
        {
            DevicePosition position = Profile.Spatial.ToPosition();

            return position.IsOrigin ? SpatialMixer.DirectionRadiusMetres : position.Distance;
        }

        set
        {
            double distance = Math.Round(
                Math.Clamp(value, MinimumSpatialDistanceMetres, MaximumSpatialDistanceMetres),
                1);

            DevicePosition direction = SpatialDirection;

            SetSpatial(direction.AtDistance(distance));
        }
    }

    /// <summary>Whether the distance takes part in the mix, or only the direction does.</summary>
    public bool SpatialUseDistance
    {
        get => Profile.Spatial.UseDistance;
        set
        {
            if (Profile.Spatial.UseDistance == value)
            {
                return;
            }

            Profile.Spatial.UseDistance = value;

            OnPropertyChanged(nameof(SpatialUseDistance));
            OnPropertyChanged(nameof(SpatialLabel));
            OnPropertyChanged(nameof(SpatialDistanceReadout));

            // Every device is re-placed, not just this one: the reference distance is shared, so switching
            // one device's distance on or off can change what every other device should be given.
            RaiseSpatialPlacementChanged();
        }
    }

    /// <summary>
    /// Places the device at a direction, keeping how far away it is.
    /// </summary>
    /// <remarks>
    /// Takes a unit vector rather than a point in space, so that clicking the same direction while the camera
    /// happens to be nearer or farther away cannot move the speaker.
    /// </remarks>
    public void SetSpatialDirection(DevicePosition direction)
    {
        if (direction.IsOrigin)
        {
            return;
        }

        SetSpatial(direction.AtDistance(SpatialDistance));
    }

    /// <summary>
    /// The direction, and -- when it counts -- the distance, as one short line.
    /// </summary>
    /// <remarks>
    /// Named rather than numeric. "右前上方" answers "where did I put this speaker" at a glance, which three
    /// coordinates do not, and the elevation is folded into the name rather than shown as a second number.
    /// The distance appears only when it is switched on: showing a number that changes nothing invites the
    /// question of why it changes nothing.
    /// </remarks>
    public string SpatialLabel
    {
        get
        {
            Localizer loc = Localizer.Instance;
            DevicePosition direction = SpatialDirection;
            double elevation = Math.Asin(Math.Clamp(direction.Up, -1.0, 1.0)) * 180.0 / Math.PI;
            string label;

            if (elevation > 60.0)
            {
                label = loc["Spatial.Zenith"];
            }
            else if (elevation < -60.0)
            {
                label = loc["Spatial.Nadir"];
            }
            else
            {
                int index = SpatialMixer.NearestDirectionIndex(direction.Right, direction.Front);
                string suffix = elevation > 20.0
                    ? loc["Spatial.Suffix.Above"]
                    : elevation < -20.0 ? loc["Spatial.Suffix.Below"] : loc["Spatial.Suffix.Level"];

                label = loc[DirectionKeys[index]] + suffix;
            }

            return Profile.Spatial.UseDistance
                ? label + " · " + string.Create(CultureInfo.InvariantCulture, $"{SpatialDistance:0.#} m")
                : label;
        }
    }

    /// <summary>How far above or below the horizon the device currently sits, in degrees.</summary>
    public double CurrentElevationDegrees =>
        Math.Asin(Math.Clamp(SpatialDirection.Up, -1.0, 1.0)) * 180.0 / Math.PI;

    /// <summary>
    /// Applies one of the eight horizontal directions, keeping the height the device already has.
    /// </summary>
    /// <remarks>
    /// The horizontal direction and the height are two questions rather than forty buttons: a horizontal preset
    /// keeps the elevation and a height preset keeps the horizontal direction, so any of the forty exact
    /// positions is two clicks away and none of them needs a name of its own.
    /// </remarks>
    public void ApplyDirectionPreset(int index) =>
        SetSpatialDirection(SpatialMixer.DirectionPosition(index, CurrentElevationDegrees));

    /// <summary>Applies a height, keeping the horizontal direction the device already has.</summary>
    public void ApplyElevationPreset(double elevationDegrees) =>
        SetSpatialDirection(SpatialMixer.DirectionPosition(
            SpatialMixer.NearestDirectionIndex(SpatialDirection.Right, SpatialDirection.Front),
            elevationDegrees));

    /// <summary>
    /// Records what the engine is actually applying to this device, so the picker can state it.
    /// </summary>
    /// <remarks>
    /// Pushed in by the view model rather than worked out here, because both numbers are RELATIVE: the distance
    /// delay is measured against the farthest device and the attenuation against the nearest, so one device on
    /// its own cannot compute either of them.
    /// </remarks>
    public void SetAppliedSpatialPlacement(SpatialPlacement placement)
    {
        if (_appliedPlacement == placement)
        {
            return;
        }

        _appliedPlacement = placement;

        OnPropertyChanged(nameof(SpatialChannelReadout));
        OnPropertyChanged(nameof(SpatialDistanceReadout));
    }

    /// <summary>The stereo image this device is being given, as the picker states it.</summary>
    public string SpatialChannelReadout => Localizer.Instance.Format(
        "Spatial.Readout.Channels",
        Number(_appliedPlacement.LeftGain, "0.00"),
        Number(_appliedPlacement.RightGain, "0.00"));

    /// <summary>
    /// What the distance is doing to this device, or a note that it is doing nothing.
    /// </summary>
    /// <remarks>
    /// The attenuation is taken from the LOUDER channel, which is the one the pan leaves at unity, so what is
    /// reported is the distance attenuation alone and never the panning twice over.
    /// </remarks>
    public string SpatialDistanceReadout
    {
        get
        {
            Localizer loc = Localizer.Instance;

            if (!SpatialUseDistance)
            {
                return loc["Spatial.Readout.Passive"];
            }

            double loudest = Math.Max(_appliedPlacement.LeftGain, _appliedPlacement.RightGain);
            double decibels = 20.0 * Math.Log10(Math.Clamp(loudest, 1e-6, 1.0));

            return loc.Format("Spatial.Readout.Delay", Number(_appliedPlacement.DelayMs, "0.#"))
                + " · "
                + loc.Format("Spatial.Readout.Attenuation", Number(decibels, "0.0"));
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
    private void SetSpatial(DevicePosition position)
    {
        if (Math.Abs(Profile.Spatial.Right - position.Right) < 0.0001
            && Math.Abs(Profile.Spatial.Front - position.Front) < 0.0001
            && Math.Abs(Profile.Spatial.Up - position.Up) < 0.0001)
        {
            return;
        }

        Profile.Spatial.Set(position.Right, position.Front, position.Up);

        OnPropertyChanged(nameof(SpatialDirection));
        OnPropertyChanged(nameof(SpatialDistance));
        OnPropertyChanged(nameof(SpatialLabel));

        RaiseSpatialPlacementChanged();
    }

    /// <summary>Tells listeners that a position moved, so that every device is re-placed.</summary>
    private void RaiseSpatialPlacementChanged() => OnPropertyChanged(nameof(SpatialPlacementChanged));

    /// <summary>Formats a readout number invariantly, so the decimal separator never depends on the machine.</summary>
    private static string Number(double value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>
    /// A change token for the placement, raised whenever this device's position or distance switch moves.
    /// </summary>
    /// <remarks>
    /// A property with no value, because the listener's job is to re-place EVERY device and not to read
    /// anything from this one. Naming it after the change rather than reusing one of the real properties is
    /// what lets the distance switch join in without the listener having to keep an arbitrary pair of names
    /// in step.
    /// </remarks>
    public bool SpatialPlacementChanged => false;

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
        // placeholder into the new language. The position label is composed from the table too.
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(SpatialLabel));
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
