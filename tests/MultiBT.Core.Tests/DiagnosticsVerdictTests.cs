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

    [Fact]
    public void ARingHeldAboveItsTargetIsReportedAsBacklog()
    {
        // 125 against a 105 target is comfortably past the 15 ms alarm; landing exactly on the threshold does not
        // count, which is deliberate for a threshold that exists to ignore normal wander.
        Assert.Equal(
            FaultLayer.Backlog,
            DiagnosticsVerdict.Classify([Channel(fillMinMs: 125.0, fillMaxMs: 125.0, fillMeanMs: 125.0)]));
    }

    [Fact]
    public void ARingBeingTrimmedAndRefilledIsBacklogEvenWhenItsAverageLooksRight()
    {
        // Measured on real hardware: 94.9 to 114.9 ms in one window against a 105 ms target. The average sits on
        // the target and hides the fault completely; what gives it away is that the ring filled past the resync
        // line and was cut back, which drops audio.
        Assert.Equal(
            FaultLayer.Backlog,
            DiagnosticsVerdict.Classify([Channel(fillMinMs: 94.9, fillMaxMs: 114.9, fillMeanMs: 105.0)]));
    }

    [Fact]
    public void AHealthyRingIsNotBackedUp()
    {
        // The other real measurement: 96.4 to 96.5 ms. Half a millisecond of wander must not be reported as
        // anything at all, or the panel cries wolf on a working set-up.
        ChannelDiagnostics healthy = Channel(fillMinMs: 96.4, fillMaxMs: 96.5, fillMeanMs: 96.45);

        Assert.False(DiagnosticsVerdict.IsBackedUp(healthy));
        Assert.Equal(FaultLayer.Delivered, DiagnosticsVerdict.Classify([healthy]));
    }

    [Fact]
    public void BacklogOutranksAPinnedCorrection()
    {
        // A pinned proportional correction only means the error passed 0.8 ms, so it is the weakest evidence here.
        // The first version of this classifier reported exactly that and sent the reader after the wrong thing.
        Assert.Equal(
            FaultLayer.Backlog,
            DiagnosticsVerdict.Classify(
            [
                Channel(fillMinMs: 140.0, fillMaxMs: 140.0, fillMeanMs: 140.0, correctionPpm: EngineTunables.MaxCorrection * 1e6),
            ]));
    }

    private static ChannelDiagnostics Channel(
        string key = "device",
        double fillMinMs = 100.0,
        double targetMs = 105.0,
        double correctionPpm = 0.0,
        double averageLatencyMs = 100.0,
        double currentLatencyMs = 100.0,
        double worstSilenceFraction = 0.0,
        double? fillMaxMs = null,
        double? fillMeanMs = null) =>
        new(
            DeviceKey: key,
            FillMinMs: fillMinMs,
            FillMaxMs: fillMaxMs ?? fillMinMs,
            FillMeanMs: fillMeanMs ?? fillMinMs,
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
