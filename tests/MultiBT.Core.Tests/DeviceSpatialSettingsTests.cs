using System.Text.Json;
using MultiBT.Core.Config;
using MultiBT.Core.Sync;
using Xunit;

namespace MultiBT.Core.Tests;

/// <summary>
/// Per-device position: the persisted form, and that "not configured" is the same as the origin.
/// </summary>
/// <remarks>
/// The round trip matters more than it looks. A position that silently fails to persist would look like the
/// spatial mix losing its settings on every restart, and the failure would be attributed to the mixer rather
/// than to storage.
/// </remarks>
public sealed class DeviceSpatialSettingsTests
{
    [Fact]
    public void AnUntouchedDeviceIsNotConfigured()
    {
        var spatial = new DeviceSpatialSettings();

        Assert.False(spatial.IsConfigured);
        Assert.True(spatial.ToPosition().IsOrigin);
    }

    [Fact]
    public void AnyAxisIsEnoughToCountAsConfigured()
    {
        Assert.True(new DeviceSpatialSettings { Right = 2 }.IsConfigured);
        Assert.True(new DeviceSpatialSettings { Front = -1.5 }.IsConfigured);
        Assert.True(new DeviceSpatialSettings { Up = 1 }.IsConfigured);
    }

    [Fact]
    public void SetWritesAllThreeAxes()
    {
        var spatial = new DeviceSpatialSettings();
        spatial.Set(1.5, -2, 0.5);

        Assert.Equal(1.5, spatial.Right);
        Assert.Equal(-2, spatial.Front);
        Assert.Equal(0.5, spatial.Up);
    }

    [Fact]
    public void PositionSurvivesARoundTripThroughJson()
    {
        var profile = new DeviceProfile
        {
            Key = "speaker-right",
            DisplayName = "Right",
            Spatial = new DeviceSpatialSettings { Right = 2.5, Front = 1.0, Up = -0.5 },
        };

        string json = JsonSerializer.Serialize(profile, ProfileStore.SerializerOptions);
        DeviceProfile? restored = JsonSerializer.Deserialize<DeviceProfile>(json, ProfileStore.SerializerOptions);

        Assert.NotNull(restored);
        Assert.Equal(2.5, restored!.Spatial.Right);
        Assert.Equal(1.0, restored.Spatial.Front);
        Assert.Equal(-0.5, restored.Spatial.Up);
        Assert.True(restored.Spatial.IsConfigured);
    }

    [Fact]
    public void AProfileWrittenBeforePositionsExistedLoadsAsUnconfigured()
    {
        // Additive schema change: an existing settings file has no "Spatial" block, and it must come back as
        // an unplaced device rather than as a deserialisation failure or a null.
        const string legacy = """
        { "key": "old", "displayName": "Old", "audio": { "enabled": true } }
        """;

        DeviceProfile? restored = JsonSerializer.Deserialize<DeviceProfile>(legacy, ProfileStore.SerializerOptions);

        Assert.NotNull(restored);
        Assert.NotNull(restored!.Spatial);
        Assert.False(restored.Spatial.IsConfigured);
    }
}
