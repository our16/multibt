using MultiBT.Core.Audio;
using MultiBT.Core.Sync;
using Xunit;

namespace MultiBT.Core.Tests;

/// <summary>
/// Closed-loop verification of the drift control law.
/// </summary>
/// <remarks>
/// <para>
/// These tests treat <see cref="DriftController"/> as the controller and a trivial integrator
/// as the plant, which is exactly the model docs/SPEC.md §6.3 uses:
/// </para>
/// <code>
/// d(fill)/dt = -(c + d)
/// </code>
/// <para>
/// where <c>fill</c> is the ring fill in seconds, <c>c</c> the correction ratio we apply, and
/// <c>d</c> the device's fractional clock error (positive = device is fast). Derivation: the
/// resampler consumes <c>(1+c)</c> input frames per output frame, the device pulls output at
/// <c>(1+d)</c>, so input consumption is <c>~1+d+c</c> against a writer rate of 1.
/// </para>
/// <para>
/// The point of simulating rather than only unit-testing individual steps is that the central
/// design claim — <i>"P-only is sufficient because the plant is an integrator, giving zero
/// steady-state rate error and E_ss = d/K"</i> — is a statement about the closed loop. If the
/// sign convention or the gain were wrong, a step test could still pass while the loop diverged.
/// </para>
/// </remarks>
public sealed class DriftControllerTests
{
    private const double TickSeconds = 1.0 / EngineTunables.ControlTickHz;
    private const int LatencyMs = 100;

    [Fact]
    public void HoldsFillNearTarget_WhenDeviceClockRunsFast()
    {
        const double drift = 100e-6;   // 100 ppm fast
        (DriftController controller, double fill, double correction) = Simulate(drift, seconds: 120.0);

        // The design predicts a CONSTANT offset of E_ss = d/K = 100e-6 / 0.5 = 0.2 ms.
        // What matters is that it is small, bounded, and does not grow with time.
        double offsetMs = Math.Abs(fill - controller.TargetBacklogSeconds) * 1000.0;
        Assert.InRange(offsetMs, 0.0, 1.0);

        // A fast device is corrected by consuming LESS input per output frame, so the
        // converged correction is negative and equal in magnitude to the drift.
        Assert.InRange(correction, -drift - 3e-5, -drift + 3e-5);
    }

    [Fact]
    public void HoldsFillNearTarget_WhenDeviceClockRunsSlow()
    {
        const double drift = -150e-6;   // 150 ppm slow
        (DriftController controller, double fill, double correction) = Simulate(drift, seconds: 120.0);

        double offsetMs = Math.Abs(fill - controller.TargetBacklogSeconds) * 1000.0;
        Assert.InRange(offsetMs, 0.0, 1.5);
        Assert.InRange(correction, -drift - 3e-5, -drift + 3e-5);
    }

    [Fact]
    public void OffsetDoesNotAccumulate_OverLongRun()
    {
        const double drift = 100e-6;

        (DriftController c1, double fill1, _) = Simulate(drift, seconds: 60.0);
        (DriftController c2, double fill2, _) = Simulate(drift, seconds: 600.0);

        double offset1 = Math.Abs(fill1 - c1.TargetBacklogSeconds) * 1000.0;
        double offset2 = Math.Abs(fill2 - c2.TargetBacklogSeconds) * 1000.0;

        // Ten times the duration must NOT mean ten times the error. Without compensation a
        // 100 ppm device would be 360 ms out after an hour; with it, the error is constant.
        Assert.InRange(offset2, 0.0, 1.0);
        Assert.InRange(Math.Abs(offset2 - offset1), 0.0, 0.2);
    }

    [Fact]
    public void AppliesNoCorrectionBeforeWarmUp()
    {
        var controller = new DriftController(LatencyMs);

        controller.Observe(0.0);   // a fully drained buffer, which would otherwise look like a huge error
        Assert.Equal(0.0, controller.Tick());
        Assert.False(controller.IsWarmedUp);

        Assert.True(controller.ObserveWarmUp(EngineTunables.WarmUpSeconds));
        Assert.True(controller.IsWarmedUp);
    }

    [Fact]
    public void NeverExceedsMaxCorrection()
    {
        // A wild error, sustained, must still not push the ratio past the bound.
        var controller = new DriftController(LatencyMs);
        double elapsed = 0.0;

        for (int i = 0; i < 400; i++)
        {
            elapsed += TickSeconds;
            controller.ObserveWarmUp(elapsed);
            controller.Observe(0.0);          // ring permanently empty -> maximal negative error
            controller.Tick();
            Assert.InRange(Math.Abs(controller.Correction), 0.0, EngineTunables.MaxCorrection + 1e-12);
        }
    }

    [Fact]
    public void RateLimitsEveryCorrectionChange()
    {
        // A fast-moving correction is frequency modulation, and FM is audible well below the
        // static pitch JND. Every step must respect the slew limit.
        var controller = new DriftController(LatencyMs);
        double elapsed = 0.0;

        for (int i = 0; i < 25; i++)
        {
            elapsed += TickSeconds;
            controller.ObserveWarmUp(elapsed);
            controller.Observe(0.0);
            controller.Tick();
        }

        double previous = controller.Correction;

        for (int i = 0; i < 100; i++)
        {
            elapsed += TickSeconds;
            controller.ObserveWarmUp(elapsed);
            controller.Observe(0.0);
            double current = controller.Tick();

            Assert.InRange(
                Math.Abs(current - previous),
                0.0,
                EngineTunables.MaxCorrectionRatePerTick + 1e-12);

            previous = current;
        }
    }

    [Fact]
    public void TargetIsLatencyPlusMargin()
    {
        var controller = new DriftController(LatencyMs);

        Assert.Equal(
            (LatencyMs + EngineTunables.TargetBacklogMarginMs) / 1000.0,
            controller.TargetBacklogSeconds,
            precision: 9);
    }

    [Fact]
    public void RequestsResyncOnlyAboveTheMargin()
    {
        var controller = new DriftController(LatencyMs);

        double justBelow = (LatencyMs + EngineTunables.ResyncMarginMs - 1.0) / 1000.0;
        double justAbove = (LatencyMs + EngineTunables.ResyncMarginMs + 1.0) / 1000.0;

        Assert.False(controller.ShouldResync(justBelow));
        Assert.True(controller.ShouldResync(justAbove));
    }

    /// <summary>
    /// Runs the closed loop for a simulated duration and returns the end state.
    /// </summary>
    private static (DriftController Controller, double Fill, double Correction) Simulate(double drift, double seconds)
    {
        var controller = new DriftController(LatencyMs);

        double fill = controller.TargetBacklogSeconds;
        double elapsed = 0.0;
        double correction = 0.0;
        int ticks = (int)Math.Round(seconds / TickSeconds);

        for (int i = 0; i < ticks; i++)
        {
            elapsed += TickSeconds;
            controller.ObserveWarmUp(elapsed);
            controller.Observe(fill);
            correction = controller.Tick();

            // Plant: d(fill)/dt = -(c + d).
            fill -= (correction + drift) * TickSeconds;
        }

        return (controller, fill, correction);
    }
}
