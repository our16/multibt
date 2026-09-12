using System.Text.Json;
using MultiBT.Core.Audio;
using MultiBT.Core.Sync;

namespace MultiBT.Core.Config;

/// <summary>Root settings document. Persisted to <c>%APPDATA%\MultiBT\profiles.json</c>.</summary>
public sealed class MultiBtSettings
{
    /// <summary>Schema version, so a future format change can be migrated rather than guessed at.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public const int CurrentSchemaVersion = 1;

    public string? ActiveProfileId { get; set; }

    public EngineSettings Engine { get; set; } = new();

    /// <summary>Device records, shared across profiles.</summary>
    public List<DeviceProfile> Devices { get; set; } = [];

    public List<ProfileDefinition> Profiles { get; set; } = [];

    public UiSettings Ui { get; set; } = new();
}

/// <summary>Engine-wide settings.</summary>
public sealed class EngineSettings
{
    /// <summary>
    /// Requested engine latency in ms — this is the base for the drift trough target
    /// <c>f* = engineLatencyMs + 5ms</c>.
    /// </summary>
    /// <remarks>
    /// <b>This must be the same for every channel.</b> The controller holds <c>f*</c> equal
    /// across devices so that <c>f_i + b_i</c> is constant; per-channel targets inject a
    /// static misalignment equal to the difference. A per-device override is allowed but the
    /// UI must flag that it requires re-measurement.
    /// </remarks>
    public int EngineLatencyMs { get; set; } = EngineTunables.DefaultLatencyMs;

    /// <summary>Render endpoint to mirror, or <c>null</c> to follow the Windows default render device.</summary>
    public string? SourceDeviceId { get; set; }

    public bool StartWithWindows { get; set; }

    public bool AutoResumeLastProfile { get; set; } = true;
}

/// <summary>
/// The three-level identity fallback for a device.
/// </summary>
/// <remarks>
/// A single key is not enough. <c>MMDevice.ID</c> is documented by Microsoft as OPAQUE and
/// its lifetime is tied to the device *installation*, so it CHANGES on driver upgrade or
/// reinstall (it survives a reboot and a USB replug). Windows can also recreate an endpoint
/// under a new id. Matching therefore tries: endpoint id → instance id → friendly name.
/// See docs/PITFALLS.md C4.
/// </remarks>
public sealed class DeviceIdentities
{
    /// <summary>WASAPI endpoint id. Opaque — compared, never parsed.</summary>
    public string? EndpointId { get; set; }

    /// <summary>PnP instance id, e.g. <c>BTHENUM\{...}\7&amp;2f9a1c3&amp;0&amp;001122334455_C00000000</c>.</summary>
    public string? InstanceId { get; set; }

    /// <summary>Last-resort match, used when Windows recreates the endpoint under a new id.</summary>
    public string? FriendlyName { get; set; }
}

/// <summary>A device's persistent record.</summary>
public sealed class DeviceProfile
{
    /// <summary>Stable key used by profiles and by the engine. Never an endpoint id.</summary>
    public string Key { get; set; } = string.Empty;

    public DeviceIdentities Identities { get; set; } = new();

    /// <summary>User-facing label, e.g. "客厅 JBL".</summary>
    public string DisplayName { get; set; } = string.Empty;

    public Transport Transport { get; set; } = Transport.Other;

    public DeviceLatencySettings Latency { get; set; } = new();

    public DeviceAudioSettings Audio { get; set; } = new();
}

/// <summary>Per-device audio settings.</summary>
public sealed class DeviceAudioSettings
{
    /// <summary>
    /// Chain gain, held at 1.0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The authoritative per-device volume is now the device's own Windows <b>endpoint volume</b>, which
    /// the UI drives directly. This field is kept only because it is part of the persisted schema.
    /// </para>
    /// <para>
    /// It is deliberately fixed rather than exposed: as a multiplier on top of the endpoint volume it
    /// silently capped the achievable loudness. With the endpoint at 61 %, a chain gain of 1.0 still
    /// only reaches 61 % of what the device can do, so "100 %" on a slider did not mean 100 % of
    /// anything — which is exactly the confusion this replaced.
    /// </para>
    /// </remarks>
    public double Gain { get; set; } = 1.0;

    public bool Muted { get; set; }

    /// <summary>Per-device cap on total compensation, in ms.</summary>
    public int MaxDelayMs { get; set; } = EngineTunables.MaxDelayMs;
}

/// <summary>A named scene.</summary>
public sealed class ProfileDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Icon { get; set; }

    /// <summary>What "synchronised" means for this profile. See <see cref="SyncMode"/>.</summary>
    public SyncMode Mode { get; set; } = SyncMode.AlignAll;

    public List<OutputDefinition> Outputs { get; set; } = [];
}

/// <summary>One device's participation in a profile.</summary>
public sealed class OutputDefinition
{
    public string DeviceKey { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Per-profile gain override. Falls back to the device record when <c>null</c>.</summary>
    public double? Gain { get; set; }

    /// <summary>Signed user trim, in ms. Positive = make this device later.</summary>
    public double ManualOffsetMs { get; set; }
}

public sealed class UiSettings
{
    public bool StartMinimizedToTray { get; set; } = true;

    public bool ShowLevelMeters { get; set; } = true;
}
