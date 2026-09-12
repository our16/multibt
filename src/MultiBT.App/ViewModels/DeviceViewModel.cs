using System.Globalization;
using System.Text;
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
    private string _status = "Idle";
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

        _isEnabled = profile is not null;
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

    /// <summary>Transport label including the paired-but-disconnected distinction.</summary>
    public string TransportLabel => Endpoint.IsPairedButDisconnected
        ? $"{Endpoint.Transport} · paired, not connected"
        : IsPrimary
            ? $"{Endpoint.Transport} · 捕获源"
            : Endpoint.Transport.ToString();

    /// <summary>Whether this device participates in the mirror.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                IsEnabledChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Raised when IsEnabled changes, for the engine to react immediately.</summary>
    public event EventHandler? IsEnabledChanged;

    /// <summary>Whether this device is the primary capture source.</summary>
    public bool IsPrimary
    {
        get => _isPrimary;
        set
        {
            if (SetProperty(ref _isPrimary, value))
            {
                IsPrimaryChanged?.Invoke(this, EventArgs.Empty);
                OnPropertyChanged(nameof(TransportLabel));

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

    /// <summary>Labelled endpoint volume, including a mute warning when applicable.</summary>
    public string EndpointVolumeLabel
    {
        get
        {
            if (double.IsNaN(_endpointVolume))
            {
                return "设备音量 未知（无法读取）";
            }

            string suffix = _endpointMuted ? "（Windows 已静音）" : string.Empty;
            return string.Create(CultureInfo.InvariantCulture, $"设备音量 {_endpointVolume * 100:0}%{suffix}");
        }
    }

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
                Profile.Latency.ManualOffsetMs = value;
                OnPropertyChanged(nameof(LatencySummary));
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

    /// <summary>Read-only summary line for the UI.</summary>
    public string LatencySummary
    {
        get
        {
            DeviceLatencySettings latency = Profile.Latency;

            if (!latency.HasMeasurement)
            {
                return $"not measured · trim {latency.ManualOffsetMs:+0;-0;0} ms → {EffectiveDelayMs:0} ms";
            }

            string stale = latency.MeasurementIsStale ? " · ⚠ measurement stale" : string.Empty;
            string quality = latency.MeasurementQuality switch
            {
                MeasurementQuality.High => $"±{latency.MeasurementSpreadMs:0.0} ms",
                MeasurementQuality.Low => $"±{latency.MeasurementSpreadMs:0.0} ms (low confidence)",
                _ => "failed",
            };

            return $"measured {latency.MeasuredDelayRelRefMs:0} ms {quality} · "
                   + $"compensation {latency.CompensationMs:+0;-0;0} ms · "
                   + $"trim {latency.ManualOffsetMs:+0;-0;0} ms → {EffectiveDelayMs:0} ms{stale}";
        }
    }

    /// <summary>Runtime status shown next to the device.</summary>
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
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
