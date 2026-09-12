using System.Text.Json;
using MultiBT.Core.Audio;
using MultiBT.Core.Sync;

namespace MultiBT.Core.Config;

/// <summary>UI language. Persisted with the other settings.</summary>
public enum UiLanguage
{
    /// <summary>Chinese. The default.</summary>
    Chinese = 0,

    English = 1,
}

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

    /// <summary>
    /// The virtual cable Windows should render into, or <c>null</c> when none is configured.
    /// </summary>
    /// <remarks>
    /// With a cable selected, every real speaker becomes one of our OUTPUTS, so all of them gain delay
    /// and volume control. Without it, whichever device Windows renders to plays natively and can never
    /// be delayed — the limitation that made the primary device uncontrollable.
    /// </remarks>
    public string? CaptureSinkDeviceId { get; set; }

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

/// <summary>A device's persistent record — its remembered preferences.</summary>
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

    /// <summary>Whether the user last chose this device as the primary capture source.</summary>
    public bool IsPrimary { get; set; }

    /// <summary>
    /// Sample rate observed for this endpoint the last time it was opened.
    /// </summary>
    /// <remarks>
    /// Remembered so that a CHANGE can be detected: a device whose mix format moved (a Bluetooth
    /// speaker renegotiating its codec, a driver update) invalidates any stored latency measurement,
    /// because that measurement was taken under the previous format. See docs/SPEC.md §5.3.
    /// </remarks>
    public int? LastObservedSampleRate { get; set; }

    /// <summary>Channel count observed the last time this endpoint was opened.</summary>
    public int? LastObservedChannels { get; set; }
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

    /// <summary>
    /// The device's output volume we last set, 0..1, or <c>null</c> when never set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Remembered so the user does not have to re-do per-device volume matching every session. It is
    /// applied when the mirror STARTS, not when devices are enumerated — enumeration shows the
    /// device's real current volume, so a change made in Windows is never silently overwritten just
    /// because MultiBT was launched.
    /// </para>
    /// <para>
    /// <c>null</c> is distinct from 0: 0 is a legitimate (silent) setting the user may have chosen.
    /// </para>
    /// </remarks>
    public double? DesiredVolume { get; set; }

    /// <summary>
    /// Whether this device was enabled the last time the user had it on.
    /// </summary>
    /// <remarks>
    /// Nullable on purpose: null means no explicit choice was ever made, which preserves the
    /// long-standing default that a device with a stored profile starts enabled. A plain false
    /// default would silently switch every existing user's devices off on the first run of this build.
    /// </remarks>
    public bool? Enabled { get; set; }

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

    /// <summary>UI language. Defaults to Chinese.</summary>
    public UiLanguage Language { get; set; } = UiLanguage.Chinese;
}
