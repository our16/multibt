using System.Text.Json;
using MultiBT.Core.Config;
using Xunit;

namespace MultiBT.Core.Tests;

/// <summary>
/// The record that lets a crash be undone.
/// </summary>
/// <remarks>
/// The whole recovery mechanism is "write this to disk before the routing happens, and put the routing back if it
/// is still there next time". That makes the round trip through JSON the mechanism itself, not a detail of it: a
/// record that does not survive being written is a machine left rendering into a cable nobody can hear.
/// </remarks>
public sealed class RoutedDefaultRecoveryTests
{
    [Fact]
    public void ARoutingRecordSurvivesARoundTripThroughJson()
    {
        var settings = new MultiBtSettings();
        settings.Engine.DefaultOutputBeforeMirror = "{0.0.0.00000000}.{previous-speaker}";
        settings.Engine.RoutedCableEndpointId = "{0.0.0.00000000}.{cable}";

        string json = JsonSerializer.Serialize(settings, ProfileStore.SerializerOptions);
        MultiBtSettings? restored = JsonSerializer.Deserialize<MultiBtSettings>(json, ProfileStore.SerializerOptions);

        Assert.NotNull(restored);
        Assert.Equal("{0.0.0.00000000}.{previous-speaker}", restored!.Engine.DefaultOutputBeforeMirror);
        Assert.Equal("{0.0.0.00000000}.{cable}", restored.Engine.RoutedCableEndpointId);
    }

    [Fact]
    public void ASettingsFileWrittenBeforeTheRecordExistedHasNoRecord()
    {
        // The negative control, and it matters more than the positive one: a legacy file must NOT look like a
        // crash. If it did, upgrading would restore a default output that this app never changed.
        const string legacy = """
        { "schemaVersion": 1, "engine": { "engineLatencyMs": 100, "sourceDeviceId": "{src}" } }
        """;

        MultiBtSettings? restored = JsonSerializer.Deserialize<MultiBtSettings>(legacy, ProfileStore.SerializerOptions);

        Assert.NotNull(restored);
        Assert.Null(restored!.Engine.DefaultOutputBeforeMirror);
        Assert.Null(restored.Engine.RoutedCableEndpointId);
    }

    [Fact]
    public void AConsumedRecordIsIndistinguishableFromOneThatNeverExisted()
    {
        // Clearing both fields in the same write is what makes the recovery idempotent: the next launch has
        // nothing to act on, so a failed or interrupted restore cannot be retried forever.
        var settings = new MultiBtSettings();
        settings.Engine.DefaultOutputBeforeMirror = "{previous}";
        settings.Engine.RoutedCableEndpointId = "{cable}";

        settings.Engine.DefaultOutputBeforeMirror = null;
        settings.Engine.RoutedCableEndpointId = null;

        string json = JsonSerializer.Serialize(settings, ProfileStore.SerializerOptions);
        MultiBtSettings? restored = JsonSerializer.Deserialize<MultiBtSettings>(json, ProfileStore.SerializerOptions);

        Assert.NotNull(restored);
        Assert.Null(restored!.Engine.DefaultOutputBeforeMirror);
        Assert.Null(restored.Engine.RoutedCableEndpointId);
    }
}
