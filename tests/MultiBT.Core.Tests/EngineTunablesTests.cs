using MultiBT.Core.Audio;
using Xunit;

namespace MultiBT.Core.Tests;

/// <summary>
/// The arithmetic relationships between the engine's tuning constants.
/// </summary>
/// <remarks>
/// These are not style checks. The three margins below are set in three different places, each with its own
/// reason, and they have to agree with each other or a channel misbehaves in a way that is invisible in code
/// and obvious only as an audible stutter. Asserting the relationship here is what stops one of them being
/// tuned on its own and quietly breaking the other two.
/// </remarks>
public sealed class EngineTunablesTests
{
    /// <summary>
    /// Where the controller wants the trough.
    /// </summary>
    private static double TargetMs => EngineTunables.DefaultLatencyMs + EngineTunables.TargetBacklogMarginMs;

    /// <summary>Where the backlog stops being corrected smoothly and gets re-primed.</summary>
    private static double ResyncLineMs => EngineTunables.DefaultLatencyMs + EngineTunables.ResyncMarginMs;

    /// <summary>
    /// Where the ring is filled to at start, once the player has taken its first WASAPI pull.
    /// </summary>
    /// <remarks>
    /// The first pull takes approximately one engine latency of input straight out of the ring, which is why
    /// the pre-fill carries an extra <see cref="EngineTunables.DefaultLatencyMs"/> on top of the target.
    /// </remarks>
    private static double LandingMs =>
        TargetMs + EngineTunables.PrefillSlackMs;

    [Fact]
    public void ThePreFilledRingLandsAboveTheTargetItIsCorrectedTowards()
    {
        // Above the target is the safe direction: too much buffer is jitter margin. Landing below it would
        // leave the controller saturating negatively with nothing to give.
        Assert.True(
            LandingMs > TargetMs,
            $"the pre-fill lands at {LandingMs:0.#} ms, at or below the {TargetMs:0.#} ms target");
    }

    [Fact]
    public void ThePreFilledRingLandsBelowTheResyncLineWithRoomToSpare()
    {
        // The defect this pins down: the pre-fill used to land 5 ms ABOVE the resync line, so every channel
        // was born over it, and a channel that settles onto the line resyncs repeatedly -- heard as one device
        // stuttering while the others are fine.
        double guard = ResyncLineMs - LandingMs;

        Assert.True(
            guard >= EngineTunables.MinimumPrefillGuardMs,
            $"the pre-fill lands at {LandingMs:0.#} ms against a {ResyncLineMs:0.#} ms resync line, leaving only "
            + $"{guard:0.#} ms of guard; {EngineTunables.MinimumPrefillGuardMs:0.#} ms is required");
    }

    [Fact]
    public void TheResyncLineIsAboveTheTargetWithEnoughBandToAbsorbJitter()
    {
        double band = ResyncLineMs - TargetMs;

        Assert.True(
            band > EngineTunables.PrefillSlackMs,
            $"the resync band is {band:0.#} ms, which is not wider than the {EngineTunables.PrefillSlackMs:0.#} ms "
            + "of pre-fill slack the landing point depends on");
    }

    [Fact]
    public void TheCorrectionCeilingIsWideEnoughThatAnOrdinaryDeviceIsNotSaturated()
    {
        // The ceiling is the controller's authority, and a device whose clock differs by more than it gets a
        // correction that is pinned rather than a trough that is held. Bluetooth endpoints measured at >= 200 ppm
        // against the old ceiling, so this asserts there is room beyond that.
        Assert.True(
            EngineTunables.MaxCorrection >= 300e-6,
            $"the correction ceiling is {EngineTunables.MaxCorrection * 1e6:0} ppm, which does not cover the >= 200 ppm "
            + "mismatch measured for Bluetooth endpoints");

        // And the ceiling must stay far below the static pitch JND, which is what makes raising it safe: 500 ppm is
        // 0.87 cents against a JND of 5 to 10 cents.
        Assert.True(EngineTunables.MaxCorrection < 800e-6);
    }
}
