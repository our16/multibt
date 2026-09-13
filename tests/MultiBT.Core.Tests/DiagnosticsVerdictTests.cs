using MultiBT.Core.Audio;
using MultiBT.Core.Sync;
using Xunit;

namespace MultiBT.Core.Tests;

/// <summary>
/// Which layer the diagnostics point at, and — as importantly — when they point at nothing.
/// </summary>
/// <remarks>
/// The last case is the one worth guarding: a classifier that always finds a culprit is worse than none, because
/// it sends the reader to fix something that is not broken. Several tests here assert that a healthy set comes
/// back as delivered rather than as a plausible-sounding fault.
/// </remarks>
public sealed class DiagnosticsVerdictTests
{
    [Fact]
    public void AHealthySetPointsAtNothing()
    {
        Assert.Equal(
            FaultLayer.Delivered,
            DiagnosticsVerdict.Classify([Channel(), Channel("second")]));
    }

    [Fact]
    public void NoChannelsIsNotAFault()
    {
        Assert.Equal(FaultLayer.None, DiagnosticsVerdict.Classify([]));
    }

    [Fact]
    public void OneStarvingChannelIsThatDevicesOwnProblem()
    {
        Assert.Equal(
            FaultLayer.SingleDevice,
            DiagnosticsVerdict.Classify([Channel(fillMinMs: 10.0), Channel("second")]));
    }

    [Fact]
    public void TwoStarvingChannelsPointAtTheSharedSource()
    {
        // The capture source is the only thing upstream of every channel, so a shortfall that appears in more
        // than one channel at once cannot be a device's own read rate.
        Assert.Equal(
            FaultLayer.SharedSource,
            DiagnosticsVerdict.Classify([Channel(fillMinMs: 10.0), Channel("second", fillMinMs: 8.0)]));
    }

    [Fact]
    public void AnEndpointRunningAboveItsOwnSteadyStateIsReportedAsTheEndpoint()
    {
        Assert.Equal(
            FaultLayer.Endpoint,
            DiagnosticsVerdict.Classify([Channel(currentLatencyMs: 100 + DiagnosticsVerdict.EndpointLatencyAlarmMs + 1)]));
    }

    [Fact]
    public void TheEndpointIsBlamedBeforeTheBuffersAboveIt()
    {
        // Both symptoms present: a device that is not draining makes every conclusion drawn from the buffers
        // above it meaningless, so it has to win.
        Assert.Equal(
            FaultLayer.Endpoint,
            DiagnosticsVerdict.Classify(
            [
                Channel(fillMinMs: 5.0, currentLatencyMs: 200.0),
                Channel("second", fillMinMs: 5.0),
            ]));
    }

    [Fact]
    public void MostlyZeroFilledReadsAreTheEndpointToo()
    {
        Assert.Equal(
            FaultLayer.Endpoint,
            DiagnosticsVerdict.Classify([Channel(worstSilenceFraction: 0.9)]));
    }

    [Fact]
    public void SilenceHandedOutWithAnEmptyRingIsStarvationRatherThanAnEndpointFault()
    {
        // The same silence fraction means something different when the ring is empty: there was nothing to hand
        // out, which is the starvation path, and reporting it as an endpoint fault would send the reader to the
        // wrong layer.
        Assert.Equal(
            FaultLayer.SingleDevice,
            DiagnosticsVerdict.Classify([Channel(fillMinMs: 0.5, worstSilenceFraction: 0.9)]));
    }

    [Fact]
    public void APinnedCorrectionIsAClockMismatch()
    {
        Assert.Equal(
            FaultLayer.ClockMismatch,
            DiagnosticsVerdict.Classify([Channel(correctionPpm: EngineTunables.MaxCorrection * 1e6)]));
    }

    [Fact]
    public void StarvationOutranksAClockMismatch()
    {
        Assert.Equal(
            FaultLayer.SingleDevice,
            DiagnosticsVerdict.Classify(
            [
                Channel(fillMinMs: 1.0, correctionPpm: EngineTunables.MaxCorrection * 1e6),
            ]));
    }

    [Fact]
    public void StarvationIsMeasuredAgainstTheTargetRatherThanAFixedDepth()
    {
        // A low target means a low fill is normal. An absolute threshold would call this starving and send the
        // reader after a device that is behaving exactly as configured.
        ChannelDiagnostics lowTarget = Channel(fillMinMs: 8.0, targetMs: 20.0);

        Assert.False(DiagnosticsVerdict.IsStarving(lowTarget));
        Assert.Equal(FaultLayer.Delivered, DiagnosticsVerdict.Classify([lowTarget]));

        // The same depth against the usual target IS starving.
        Assert.True(DiagnosticsVerdict.IsStarving(Channel(fillMinMs: 8.0)));
    }

    private static ChannelDiagnostics Channel(
        string key = "device",
        double fillMinMs = 100.0,
        double targetMs = 105.0,
        double correctionPpm = 0.0,
        double averageLatencyMs = 100.0,
        double currentLatencyMs = 100.0,
        double worstSilenceFraction = 0.0) =>
        new(
            DeviceKey: key,
            FillMinMs: fillMinMs,
            FillMaxMs: fillMinMs,
            FillMeanMs: fillMinMs,
            TargetBacklogMs: targetMs,
            CorrectionPpm: correctionPpm,
            StarvedReads: 0,
            OverflowDrops: 0,
            ResyncCount: 0,
            AppliedDelayMs: 0.0,
            AverageLatencyMs: averageLatencyMs,
            CurrentLatencyMs: currentLatencyMs,
            EngineLatencyMs: 100,
            WorstSilenceFraction: worstSilenceFraction);
}
