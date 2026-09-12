using MultiBT.Core.Audio;
using MultiBT.Core.Config;
using MultiBT.Core.Sync;
using Xunit;

namespace MultiBT.Core.Tests;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public ProfileStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "multibt-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "profiles.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; a leftover temp directory must not fail the suite.
        }
    }

    [Fact]
    public void RoundTripsSettings()
    {
        var store = new ProfileStore(_path);

        var settings = new MultiBtSettings
        {
            ActiveProfileId = "movie",
            Engine = new EngineSettings { EngineLatencyMs = 100, AutoResumeLastProfile = false },
            Devices =
            [
                new DeviceProfile
                {
                    Key = "jbl-charge5",
                    DisplayName = "客厅 JBL",
                    Transport = Transport.Bluetooth,
                    Identities = new DeviceIdentities
                    {
                        EndpointId = "{0.0.0.00000000}.{a1b2}",
                        InstanceId = @"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}\7&1&001122334455_C00000000",
                        FriendlyName = "JBL Charge 5",
                    },
                    Latency = new DeviceLatencySettings
                    {
                        MeasuredDelayRelRefMs = 185.0,
                        MeasurementSpreadMs = 4.2,
                        MeasurementQuality = MeasurementQuality.High,
                        MeasurementUtc = DateTimeOffset.UtcNow,
                        CompensationMs = 125.0,
                        ManualOffsetMs = -15.0,
                    },
                    Audio = new DeviceAudioSettings { Gain = 0.8 },
                },
            ],
            Profiles =
            [
                new ProfileDefinition
                {
                    Id = "movie",
                    Name = "电影",
                    Icon = "🎬",
                    Mode = SyncMode.WiredOnly,
                    Outputs = [new OutputDefinition { DeviceKey = "jbl-charge5", Enabled = true, ManualOffsetMs = -5 }],
                },
            ],
        };

        store.Save(settings);
        MultiBtSettings loaded = store.Load(out string? warning);

        Assert.Null(warning);
        Assert.Equal("movie", loaded.ActiveProfileId);
        Assert.Equal(100, loaded.Engine.EngineLatencyMs);
        Assert.False(loaded.Engine.AutoResumeLastProfile);

        DeviceProfile device = Assert.Single(loaded.Devices);
        Assert.Equal("jbl-charge5", device.Key);
        Assert.Equal(Transport.Bluetooth, device.Transport);
        Assert.Equal(185.0, device.Latency.MeasuredDelayRelRefMs);
        Assert.Equal(-15.0, device.Latency.ManualOffsetMs);
        Assert.Equal(MeasurementQuality.High, device.Latency.MeasurementQuality);

        ProfileDefinition profile = Assert.Single(loaded.Profiles);
        Assert.Equal(SyncMode.WiredOnly, profile.Mode);
        Assert.Equal("🎬", profile.Icon);
    }

    [Fact]
    public void MissingFileYieldsDefaultsWithoutAWarning()
    {
        var store = new ProfileStore(_path);

        MultiBtSettings settings = store.Load(out string? warning);

        Assert.Null(warning);
        Assert.Single(settings.Profiles);
        Assert.Equal(MultiBtSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Fact]
    public void CorruptFileIsQuarantinedNotDiscarded()
    {
        File.WriteAllText(_path, "{ this is not valid json ");

        var store = new ProfileStore(_path);
        MultiBtSettings settings = store.Load(out string? warning);

        // The user must be TOLD, not silently reset — appearing to have amnesia is the failure
        // mode this behaviour exists to prevent.
        Assert.NotNull(warning);
        Assert.Contains("preserved", warning, StringComparison.OrdinalIgnoreCase);

        // And the unreadable file must still exist under a different name, because it may hold
        // measured latencies the user cannot easily recreate.
        string[] quarantined = Directory.GetFiles(_directory, "profiles.corrupt-*.json");
        Assert.Single(quarantined);
        Assert.Contains("not valid json", File.ReadAllText(quarantined[0]));

        Assert.Single(settings.Profiles);   // defaults
    }

    [Fact]
    public void NewerSchemaLoadsDataButWarns()
    {
        File.WriteAllText(
            _path,
            """
            { "schemaVersion": 99, "activeProfileId": "future", "profiles": [], "devices": [] }
            """);

        var store = new ProfileStore(_path);
        MultiBtSettings settings = store.Load(out string? warning);

        // Forward compatibility: use what we can understand, but say so.
        Assert.NotNull(warning);
        Assert.Equal("future", settings.ActiveProfileId);
    }

    [Fact]
    public void SaveLeavesNoTemporaryFileBehind()
    {
        var store = new ProfileStore(_path);

        store.Save(ProfileStore.CreateDefault());

        // The write is a temp file plus an atomic rename, so a crash mid-save can never leave a
        // truncated settings file in place.
        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void SaveCreatesMissingDirectory()
    {
        string nested = Path.Combine(_directory, "a", "b", "profiles.json");
        var store = new ProfileStore(nested);

        store.Save(ProfileStore.CreateDefault());

        Assert.True(File.Exists(nested));
    }
}
