using MultiBT.Core.Audio;
using MultiBT.Core.Config;
using MultiBT.Core.Devices;
using Xunit;

namespace MultiBT.Core.Tests;

public sealed class DeviceIdentityMatcherTests
{
    private static AudioEndpointInfo Endpoint(
        string endpointId = "{0.0.0.00000000}.{a1b2c3}",
        string instanceId = @"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}\7&1&001122334455_C00000000",
        string friendlyName = "JBL Charge 5") =>
        new(endpointId, instanceId, friendlyName, $"Speakers ({friendlyName})", Transport.Bluetooth, true);

    [Fact]
    public void MatchesOnEndpointIdFirst()
    {
        var identities = new DeviceIdentities
        {
            EndpointId = "{0.0.0.00000000}.{a1b2c3}",
            InstanceId = "something-else",
            FriendlyName = "something-else",
        };

        Assert.Equal(DeviceIdentityMatcher.MatchKind.EndpointId, DeviceIdentityMatcher.Match(identities, Endpoint()));
    }

    [Fact]
    public void FallsBackToInstanceIdWhenEndpointIdChanged()
    {
        // Exactly the driver-reinstall case: the endpoint id changed, the PnP instance id did not.
        var identities = new DeviceIdentities
        {
            EndpointId = "{0.0.0.00000000}.{OLD-ENDPOINT}",
            InstanceId = @"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}\7&1&001122334455_C00000000",
        };

        Assert.Equal(DeviceIdentityMatcher.MatchKind.InstanceId, DeviceIdentityMatcher.Match(identities, Endpoint()));
    }

    [Fact]
    public void FallsBackToFriendlyNameAsALastResort()
    {
        var identities = new DeviceIdentities
        {
            EndpointId = "{0.0.0.00000000}.{OLD}",
            InstanceId = @"USB\OLD\PATH",
            FriendlyName = "JBL Charge 5",
        };

        Assert.Equal(DeviceIdentityMatcher.MatchKind.FriendlyName, DeviceIdentityMatcher.Match(identities, Endpoint()));
    }

    [Fact]
    public void UnrelatedDeviceDoesNotMatch()
    {
        var identities = new DeviceIdentities
        {
            EndpointId = "{0.0.0.00000000}.{OTHER}",
            InstanceId = @"USB\VID_0000&PID_0000",
            FriendlyName = "Some Other Speaker",
        };

        Assert.Equal(DeviceIdentityMatcher.MatchKind.None, DeviceIdentityMatcher.Match(identities, Endpoint()));
    }

    [Fact]
    public void MissingOrNullIdentitiesNeverMatch()
    {
        Assert.Equal(DeviceIdentityMatcher.MatchKind.None, DeviceIdentityMatcher.Match(null, Endpoint()));
        Assert.Equal(DeviceIdentityMatcher.MatchKind.None, DeviceIdentityMatcher.Match(new DeviceIdentities(), Endpoint()));
        Assert.Equal(DeviceIdentityMatcher.MatchKind.None, DeviceIdentityMatcher.Match(new DeviceIdentities(), null));
    }

    [Fact]
    public void OnlyStrongMatchesPreserveAMeasurement()
    {
        // A name-only match means the endpoint identity changed; whatever was measured against
        // the previous endpoint may no longer hold, so the caller must invalidate it.
        Assert.True(DeviceIdentityMatcher.PreservesMeasurement(DeviceIdentityMatcher.MatchKind.EndpointId));
        Assert.True(DeviceIdentityMatcher.PreservesMeasurement(DeviceIdentityMatcher.MatchKind.InstanceId));
        Assert.False(DeviceIdentityMatcher.PreservesMeasurement(DeviceIdentityMatcher.MatchKind.FriendlyName));
        Assert.False(DeviceIdentityMatcher.PreservesMeasurement(DeviceIdentityMatcher.MatchKind.None));
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        // Case genuinely varies between sources: one real setupapi log line has a lowercase
        // GUID and an uppercase MAC.
        var identities = new DeviceIdentities { InstanceId = @"bthenum\{0000110B-0000-1000-8000-00805F9B34FB}\7&1&001122334455_C00000000" };

        Assert.Equal(DeviceIdentityMatcher.MatchKind.InstanceId, DeviceIdentityMatcher.Match(identities, Endpoint()));
    }
}
