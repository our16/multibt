using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiBT.Core.Config;

/// <summary>
/// Loads and atomically saves the settings document.
/// </summary>
/// <remarks>
/// <para>
/// Writes are atomic: serialise to a temp file in the same directory, then swap it in. A
/// half-written settings file would otherwise be indistinguishable from corruption on next
/// start, and this file holds data the user cannot easily recreate (measured latencies).
/// </para>
/// <para>
/// Corruption is quarantined, never silently discarded: the bad file is renamed to
/// <c>profiles.corrupt-&lt;timestamp&gt;.json</c>, defaults are returned, and a warning is
/// surfaced to the caller so the UI can tell the user rather than appearing to have amnesia.
/// "Silently reset the user's config" is the failure mode this class exists to prevent.
/// </para>
/// </remarks>
public sealed class ProfileStore
{
    private readonly string _path;

    /// <param name="path">Override for tests. Defaults to <c>%APPDATA%\MultiBT\profiles.json</c>.</param>
    public ProfileStore(string? path = null) => _path = path ?? DefaultPath;

    /// <summary>Default on-disk location.</summary>
    /// <remarks>Fully qualified <c>System.IO.Path</c> because this class has a <c>Path</c> property that shadows it.</remarks>
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MultiBT",
        "profiles.json");

    /// <summary>The path this store reads and writes.</summary>
    public string Path => _path;

    /// <summary>Serializer settings, shared so that save and load cannot drift apart.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Loads settings, falling back to defaults on a missing or corrupt file.
    /// </summary>
    /// <param name="warning">
    /// Non-null when something went wrong that the user should be told about (corruption, or
    /// an unsupported schema version).
    /// </param>
    public MultiBtSettings Load(out string? warning)
    {
        warning = null;

        if (!File.Exists(_path))
        {
            return CreateDefault();
        }

        try
        {
            string json = File.ReadAllText(_path);
            MultiBtSettings? settings = JsonSerializer.Deserialize<MultiBtSettings>(json, SerializerOptions);

            if (settings is null)
            {
                warning = Quarantine("settings file deserialised to null");
                return CreateDefault();
            }

            if (settings.SchemaVersion > MultiBtSettings.CurrentSchemaVersion)
            {
                warning = $"Settings file schema v{settings.SchemaVersion} is newer than this build "
                          + $"(v{MultiBtSettings.CurrentSchemaVersion}). Using it as-is; unknown fields were ignored.";
            }

            return settings;
        }
        catch (JsonException ex)
        {
            warning = Quarantine($"invalid JSON: {ex.Message}");
            return CreateDefault();
        }
        catch (IOException ex)
        {
            // Do NOT quarantine on a transient IO failure — the file may be fine.
            warning = $"Could not read settings ({ex.Message}). Using defaults for this session; the file was left untouched.";
            return CreateDefault();
        }
    }

    /// <summary>Saves settings atomically, creating the directory if needed.</summary>
    public void Save(MultiBtSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string? directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = _path + ".tmp";
        string json = JsonSerializer.Serialize(settings, SerializerOptions);

        File.WriteAllText(tempPath, json);

        // Atomic swap. File.Move with overwrite is a single rename on NTFS, so a crash
        // mid-save can never leave a truncated settings file behind.
        File.Move(tempPath, _path, overwrite: true);
    }

    /// <summary>Creates a fresh settings document with a usable starter profile.</summary>
    public static MultiBtSettings CreateDefault() => new()
    {
        ActiveProfileId = "default",
        Profiles =
        [
            new ProfileDefinition { Id = "default", Name = "Default", Icon = "🎬" },
        ],
    };

    /// <summary>
    /// Renames a corrupt file out of the way and returns the user-facing warning.
    /// </summary>
    private string Quarantine(string reason)
    {
        string quarantinePath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(_path) ?? ".",
            $"profiles.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");

        try
        {
            File.Move(_path, quarantinePath);
            return $"Settings file was unreadable ({reason}). It has been preserved as '{System.IO.Path.GetFileName(quarantinePath)}' and defaults restored.";
        }
        catch (IOException)
        {
            // Could not move it; still refuse to pretend the file was fine.
            return $"Settings file was unreadable ({reason}) and could not be quarantined. Defaults are in use; the original file was left in place.";
        }
    }
}
