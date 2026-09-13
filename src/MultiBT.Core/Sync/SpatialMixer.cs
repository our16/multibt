using MultiBT.Core.Audio;

namespace MultiBT.Core.Sync;

/// <summary>
/// Where a device sits relative to the listener, in metres.
/// </summary>
/// <param name="Right">Positive is to the listener's RIGHT, negative to the left.</param>
/// <param name="Front">Positive is in FRONT of the listener, negative behind.</param>
/// <param name="Up">Positive is ABOVE the listener, negative below.</param>
/// <remarks>
/// Three axes rather than a single azimuth, because a room is not flat and "the projector is on the
/// ceiling" is a position, not a direction. All-zero means "at the listener", which is deliberately a
/// legal position: it is what a device with no configured position behaves as, and it must not change
/// anything about that device's audio.
/// </remarks>
public readonly record struct DevicePosition(double Right, double Front, double Up)
{
    /// <summary>Straight-line distance from the listener, in metres.</summary>
    public double Distance => Math.Sqrt((Right * Right) + (Front * Front) + (Up * Up));

    /// <summary>Whether this position carries no information, i.e. it is the listener's own point.</summary>
    public bool IsOrigin => Distance < 0.0001;
}

/// <summary>
/// What one device should be given so that it sits at its configured position.
/// </summary>
/// <param name="LeftGain">Gain applied to the left channel, 0..1.</param>
/// <param name="RightGain">Gain applied to the right channel, 0..1.</param>
/// <param name="DelayMs">Delay to add so that every device's sound arrives together, in ms.</param>
public readonly record struct SpatialPlacement(double LeftGain, double RightGain, double DelayMs);

/// <summary>
/// Turns device positions into a per-device stereo gain and a distance delay.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the delay is for.</b> Two speakers at different distances physically deliver sound at different
/// times, so a set that is aligned on paper smears in a real room. The delay here cancels that: each device
/// is delayed by <c>(farthest - own) / c</c>, so everything arrives at the listener simultaneously. It is
/// the same idea as <see cref="LatencyModel"/>'s alignment, derived from geometry rather than measurement.
/// </para>
/// <para>
/// <b>What creates the spatial effect is the GAIN, not the delay.</b> A device on the right plays mostly the
/// right channel of the stereo image, one on the left mostly the left, one above stays centred but is
/// attenuated. So the effect is honest about what it can do: it spreads an ordinary stereo image across
/// physically separated speakers. It cannot place a sound that is not in the source material.
/// </para>
/// <para>
/// <b>Nothing here touches a device with no position.</b> A device at the origin, and every device when the
/// feature is off, comes back as unity gain and zero delay, so enabling positions cannot disturb a set-up
/// that has not configured any.
/// </para>
/// </remarks>
public static class SpatialMixer
{
    /// <summary>Speed of sound in air, m/s. Used only for the distance delay.</summary>
    public const double SpeedOfSoundMetresPerSecond = 343.0;

    /// <summary>
    /// Quietest a device may be made by its distance, as a fraction of the nearest one.
    /// </summary>
    /// <remarks>
    /// A floor, because a correctly-placed far speaker that has been made inaudible is a bug report rather
    /// than an effect: distance attenuation is a hint, not a reason to silence a device the user paid for.
    /// </remarks>
    public const double MinimumDistanceGain = 0.35;

    /// <summary>Unity placement: full on both channels, no added delay.</summary>
    public static SpatialPlacement Unity { get; } = new(1.0, 1.0, 0.0);

    /// <summary>How many directions the picker offers: the four cardinal points and the four diagonals.</summary>
    public const int DirectionCount = 8;

    /// <summary>The radius every offered direction sits on, in metres.</summary>
    public const double DirectionRadiusMetres = 1.0;

    /// <summary>
    /// Converts a direction index into a position: 0 is straight ahead, and each step turns 45 degrees
    /// clockwise, so 2 is the listener's right and 6 is their left.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every direction sits on the SAME circle, and that is what keeps a direction change down to a change of
    /// pan alone. Distance delay is <c>(farthest - own) / c</c> and distance attenuation is measured against
    /// the nearest device, so if every device is equally far, both come out at zero and unity for all of them
    /// whatever the direction. Picking a direction therefore cannot quietly re-time the set or make a device
    /// quieter.
    /// </para>
    /// <para>
    /// No elevation: a pair of speakers cannot reproduce height, so offering "above" would only push a
    /// device's sound sideways. The saved coordinates still carry an elevation for anyone who sets one, which
    /// is why this returns a whole position rather than a flat pair.
    /// </para>
    /// </remarks>
    /// <param name="index">Direction index; out of range values wrap, so -1 is the same as 7.</param>
    public static DevicePosition DirectionPosition(int index) => DirectionPosition(index, 0.0);

    /// <summary>
    /// The same direction, tilted up or down, still on the same 1 m circle.
    /// </summary>
    /// <remarks>
    /// Elevation is the thing a flat plan cannot express, and the reason the picker is a sphere rather than a
    /// plane: a speaker in the corner of a ceiling is above AND to a side, and one directly overhead has no
    /// horizontal direction at all. Keeping the radius fixed means an elevation changes the direction and
    /// nothing else -- the pan, the distance delay and the attenuation are all still decided by the directions
    /// of the devices relative to each other, never by how high one of them sits.
    /// </remarks>
    /// <param name="index">Horizontal direction index; out of range values wrap.</param>
    /// <param name="elevationDegrees">0 on the horizon, +90 straight overhead, -90 straight below.</param>
    public static DevicePosition DirectionPosition(int index, double elevationDegrees)
    {
        double radians = WrapDirection(index) * Math.PI / 4.0;
        double elevation = elevationDegrees * Math.PI / 180.0;
        double horizontal = Math.Cos(elevation) * DirectionRadiusMetres;

        return new DevicePosition(
            Snap(Math.Sin(radians) * horizontal),
            Snap(Math.Cos(radians) * horizontal),
            Snap(Math.Sin(elevation) * DirectionRadiusMetres));
    }

    /// <summary>
    /// Rounds a coordinate that is zero to within floating-point noise to exactly zero.
    /// </summary>
    /// <remarks>
    /// <c>sin(pi)</c> comes out as 1.2e-16 rather than 0. Snapping keeps a saved position the number it looks
    /// like instead of a rounding artefact, and makes the direction to position to direction round trip exact.
    /// </remarks>
    private static double Snap(double value) => Math.Abs(value) < 1e-9 ? 0.0 : value;

    /// <summary>
    /// The direction index nearest to a horizontal position.
    /// </summary>
    /// <remarks>
    /// Lets a picker with only eight answers display a position that was saved as a pair of coordinates, so
    /// an existing set-up reads as the direction it is closest to instead of being discarded. A device nobody
    /// has placed reads as straight ahead, which is the one direction that changes nothing about its audio.
    /// </remarks>
    public static int NearestDirectionIndex(double right, double front)
    {
        double degrees = Math.Atan2(right, front) * 180.0 / Math.PI;

        return WrapDirection((int)Math.Round(degrees / 45.0, MidpointRounding.AwayFromZero));
    }

    /// <summary>Folds any index into <c>0..DirectionCount-1</c>.</summary>
    private static int WrapDirection(int index)
    {
        int count = DirectionCount;
        return ((index % count) + count) % count;
    }

    /// <summary>
    /// Computes every device's placement from its position.
    /// </summary>
    /// <param name="positions">Position per device key. A key with no entry is treated as at the origin.</param>
    /// <param name="maxDelayMs">Upper bound for the distance delay, matching the delay line's own limit.</param>
    public static IReadOnlyDictionary<string, SpatialPlacement> ComputePlacements(
        IReadOnlyDictionary<string, DevicePosition> positions,
        int maxDelayMs = EngineTunables.MaxDelayMs)
    {
        ArgumentNullException.ThrowIfNull(positions);

        var result = new Dictionary<string, SpatialPlacement>(StringComparer.Ordinal);

        if (positions.Count == 0)
        {
            return result;
        }

        // Only positioned devices take part. The reference distance is the NEAREST one, so the closest
        // speaker is never attenuated and never delayed; everything else is expressed relative to it.
        double nearest = double.MaxValue;
        double farthest = 0.0;

        foreach (DevicePosition position in positions.Values)
        {
            if (position.IsOrigin)
            {
                continue;
            }

            nearest = Math.Min(nearest, position.Distance);
            farthest = Math.Max(farthest, position.Distance);
        }

        foreach ((string key, DevicePosition position) in positions)
        {
            if (position.IsOrigin || nearest == double.MaxValue)
            {
                // No position configured: leave this device exactly as it was.
                result[key] = Unity;
                continue;
            }

            double distance = position.Distance;
            double distanceGain = Math.Clamp(nearest / distance, MinimumDistanceGain, 1.0);

            // Panning from the horizontal axes only. Elevation cannot be reproduced by a pair of speakers,
            // so it attenuates and delays but never pans -- pretending otherwise would send a ceiling
            // speaker's sound sideways, which is worse than not panning it at all.
            (double left, double right) = Pan(position.Right, position.Front);

            double delayMs = Math.Clamp((farthest - distance) / SpeedOfSoundMetresPerSecond * 1000.0, 0.0, maxDelayMs);

            result[key] = new SpatialPlacement(
                left * distanceGain,
                right * distanceGain,
                delayMs);
        }

        return result;
    }

    /// <summary>
    /// Splits a horizontal direction between the two channels.
    /// </summary>
    /// <remarks>
    /// The louder channel is normalised to 1, so panning moves the image without making anything quieter: at
    /// dead centre both channels are at unity and a device sounds exactly as it does with positions off. A
    /// constant-power law would drop the centre to 0.707 and read as "turning this feature on halved my
    /// volume", which is a support question rather than an effect.
    /// </remarks>
    private static (double Left, double Right) Pan(double right, double front)
    {
        // The law, in one sentence: the channel FACING the speaker is left untouched, and the channel on the
        // opposite side fades out as the speaker moves further to the side.
        //
        //   dead ahead  -> (1, 1)   centred, and exactly as loud as with positioning off
        //   hard right  -> (0, 1)   the left channel is closed
        //   27 deg right-> (2/3, 1) favouring the right without closing the left
        //
        // An earlier trig version of this used sin/cos and was inverted: at 27 degrees to the right it made
        // the LEFT channel the louder one. The failure only appeared off-axis, because at dead ahead and at
        // hard right the two formulations happen to agree.
        double lateral = Math.Abs(right);
        double forward = Math.Abs(front);

        if (lateral < 0.0001)
        {
            // Dead ahead or dead behind: nothing to pan towards.
            return (1.0, 1.0);
        }

        if (forward + lateral < 0.0001)
        {
            // Directly above or below: no horizontal direction either.
            return (1.0, 1.0);
        }

        // 0 straight ahead, 1 straight out to the side.
        double side = lateral / (lateral + forward);
        double away = 1.0 - side;

        return right < 0 ? (1.0, away) : (away, 1.0);
    }
}
