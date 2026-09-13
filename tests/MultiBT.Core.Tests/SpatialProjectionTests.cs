using MultiBT.Core.Sync;
using Xunit;

namespace MultiBT.Core.Tests;

/// <summary>
/// The position picker's projection: directions onto the view, and a click back into a direction.
/// </summary>
/// <remarks>
/// The inverse is the part that matters. A forward projection that disagrees with the inverse still draws a
/// plausible picture -- every marker lands somewhere sensible-looking -- and the only symptom is that the
/// device ends up somewhere other than where the user clicked. So the round trip is tested directly, and the
/// occlusion rule is tested separately because it decides which of those pixels a click is allowed to mean.
/// </remarks>
public sealed class SpatialProjectionTests
{
    private const double Size = 260.0;
    private const double Tolerance = 1e-6;

    [Fact]
    public void ADirectionDrawnInTheViewIsTheDirectionAClickThereReturns()
    {
        var camera = SpatialViewCamera.Default;

        // Straight ahead, straight up, front-right, front-right-and-up: the cases a flat plan cannot all
        // express, and the ones the picker exists for.
        DevicePosition[] directions =
        [
            new(0.0, 1.0, 0.0),
            new(0.0, 0.0, 1.0),
            Normalised(0.6, 0.8, 0.0),
            Normalised(0.5, 0.6, 0.62),
            Normalised(0.0, 0.7071067811865476, 0.7071067811865476),
        ];

        foreach (DevicePosition direction in directions)
        {
            (double x, double y, bool visible) = SpatialProjection.Project(direction, camera, Size);

            Assert.True(visible, $"the test direction {direction} should be on the visible side");

            DevicePosition back = SpatialProjection.Unproject(x, y, camera, Size);

            AssertClose(direction, back, $"round trip for {direction}");
        }
    }

    [Fact]
    public void TheCentreOfTheViewIsTheDirectionTheCameraLooksAlong()
    {
        foreach (double distance in new[] { 2.0, 4.0, 9.0 })
        {
            var camera = SpatialViewCamera.Default with { Distance = distance };

            DevicePosition centre = SpatialProjection.Unproject(Size / 2.0, Size / 2.0, camera, Size);
            DevicePosition expected = Normalised(
                Math.Sin(Degrees(camera.AzimuthDegrees)) * Math.Cos(Degrees(camera.ElevationDegrees)),
                Math.Cos(Degrees(camera.AzimuthDegrees)) * Math.Cos(Degrees(camera.ElevationDegrees)),
                Math.Sin(Degrees(camera.ElevationDegrees)));

            AssertClose(expected, centre, $"centre of the view at distance {distance}");
        }
    }

    [Fact]
    public void StraightUpIsDrawnAboveStraightAhead()
    {
        var camera = SpatialViewCamera.Default;

        double upY = SpatialProjection.Project(new DevicePosition(0.0, 0.0, 1.0), camera, Size).Y;
        double aheadY = SpatialProjection.Project(new DevicePosition(0.0, 1.0, 0.0), camera, Size).Y;

        Assert.True(upY < aheadY, $"up ({upY:0.#}) should be above ahead ({aheadY:0.#})");
        Assert.True(aheadY < Size, "ahead should be on screen, not past the bottom edge");
    }

    [Fact]
    public void TheListenersRightIsDrawnOnTheRightOfTheView()
    {
        var camera = SpatialViewCamera.Default;

        double rightX = SpatialProjection.Project(new DevicePosition(1.0, 0.0, 0.0), camera, Size).X;
        double leftX = SpatialProjection.Project(new DevicePosition(-1.0, 0.0, 0.0), camera, Size).X;

        Assert.True(rightX > Size / 2.0, $"right ({rightX:0.#}) should be right of centre");
        Assert.True(leftX < Size / 2.0, $"left ({leftX:0.#}) should be left of centre");
    }

    [Fact]
    public void ADirectionTheNearSurfaceHidesIsReportedHidden()
    {
        var camera = SpatialViewCamera.Default;

        // The camera sits in front of and above the listener, so behind them is the far side.
        Assert.False(SpatialProjection.Project(new DevicePosition(0.0, -1.0, 0.0), camera, Size).Visible);
        Assert.False(SpatialProjection.Project(new DevicePosition(0.0, 0.0, -1.0), camera, Size).Visible);

        // And the ones it faces are visible.
        Assert.True(SpatialProjection.Project(new DevicePosition(0.0, 1.0, 0.0), camera, Size).Visible);
        Assert.True(SpatialProjection.Project(new DevicePosition(0.0, 0.0, 1.0), camera, Size).Visible);
    }

    [Fact]
    public void ThePickerDirectionsAreEitherVisibleOrHiddenConsistently()
    {
        var camera = SpatialViewCamera.Default;
        (double ex, double ey, double ez) = Eye(camera);
        int visibleCount = 0;

        for (int index = 0; index < SpatialMixer.DirectionCount; index++)
        {
            DevicePosition direction = SpatialMixer.DirectionPosition(index);
            (double x, double y, bool visible) = SpatialProjection.Project(direction, camera, Size);
            DevicePosition back = SpatialProjection.Unproject(x, y, camera, Size);

            // Round trips exactly when it can be seen, and when it cannot, the pixel belongs to a direction
            // NEARER the camera -- which is the whole reason a click means the near surface.
            if (visible)
            {
                visibleCount++;
                AssertClose(direction, back, $"{index} is visible, so it must round trip");
            }
            else
            {
                double here = (direction.Right * ex) + (direction.Front * ey) + (direction.Up * ez);
                double there = (back.Right * ex) + (back.Front * ey) + (back.Up * ez);

                Assert.True(there > here, $"{index} is hidden, so its pixel must mean something nearer");
            }
        }

        // Two of the eight are behind the camera plane... but not zero of them either: a view that shows the
        // directions it should is the point, so both halves are asserted.
        Assert.True(visibleCount > 0 && visibleCount < SpatialMixer.DirectionCount);
    }

    [Fact]
    public void EveryClickGivesAUnitDirectionEvenOutsideTheSphere()
    {
        var camera = SpatialViewCamera.Default;

        for (double x = -40.0; x <= Size + 40.0; x += 20.0)
        {
            for (double y = -40.0; y <= Size + 40.0; y += 20.0)
            {
                DevicePosition direction = SpatialProjection.Unproject(x, y, camera, Size);
                double length = Math.Sqrt(
                    (direction.Right * direction.Right)
                    + (direction.Front * direction.Front)
                    + (direction.Up * direction.Up));

                // A click that misses the sphere folds onto the rim rather than being thrown away, so this
                // must hold for clicks well outside it too.
                Assert.Equal(1.0, length, Tolerance);
            }
        }
    }

    [Fact]
    public void EveryDirectionProjectionLandsInsideTheViewport()
    {
        var camera = SpatialViewCamera.Default;

        for (int index = 0; index < SpatialMixer.DirectionCount; index++)
        {
            foreach (double elevation in new[] { -45.0, 0.0, 45.0, 90.0 })
            {
                DevicePosition direction = SpatialMixer.DirectionPosition(index, elevation);
                (double x, double y, _) = SpatialProjection.Project(direction, camera, Size);

                Assert.InRange(x, 0.0, Size);
                Assert.InRange(y, 0.0, Size);
            }
        }
    }

    [Fact]
    public void TurningTheCameraToTheSideBringsThatSideIntoView()
    {
        var facingForward = SpatialViewCamera.Default;
        var facingRight = facingForward with { AzimuthDegrees = 90.0 };

        DevicePosition hardRight = SpatialMixer.DirectionPosition(2);
        DevicePosition straightAhead = SpatialMixer.DirectionPosition(0);

        Assert.False(SpatialProjection.Project(hardRight, facingForward, Size).Visible);
        Assert.True(SpatialProjection.Project(hardRight, facingRight, Size).Visible);

        Assert.True(SpatialProjection.Project(straightAhead, facingForward, Size).Visible);
        Assert.False(SpatialProjection.Project(straightAhead, facingRight, Size).Visible);
    }

    [Fact]
    public void AZeroSizedViewportIsRejected()
    {
        var camera = SpatialViewCamera.Default;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => SpatialProjection.Project(new DevicePosition(0.0, 1.0, 0.0), camera, 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SpatialProjection.Unproject(0.0, 0.0, camera, 0.0));
    }

    [Fact]
    public void TheTiltIsClampedSoTheViewNeverCollapses()
    {
        // At exactly 90 degrees the screen-up vector has nothing left to be built from.
        var overhead = SpatialViewCamera.Default with { ElevationDegrees = 90.0 };

        DevicePosition towards = SpatialProjection.Unproject(Size / 2.0, Size / 2.0, overhead, Size);
        double length = Math.Sqrt(
            (towards.Right * towards.Right) + (towards.Front * towards.Front) + (towards.Up * towards.Up));

        Assert.Equal(1.0, length, Tolerance);
        Assert.True(towards.Up > 0.0, "an overhead camera looks down at the listener, so it sees from above");
    }

    [Fact]
    public void TheSilhouetteIsWhereTheLineOfSightIsTangent()
    {
        var camera = SpatialViewCamera.Default;
        DevicePosition[] outline = SpatialProjection.Silhouette(camera);

        Assert.Equal(64, outline.Length);

        foreach (DevicePosition point in outline)
        {
            double length = Math.Sqrt(
                (point.Right * point.Right) + (point.Front * point.Front) + (point.Up * point.Up));

            // On the sphere...
            Assert.Equal(1.0, length, Tolerance);

            // ...and exactly on the boundary of what the camera can see, which is what makes it the outline
            // rather than the horizon. On the boundary itself the answer is a coin flip, so what is asserted is
            // the boundary condition: the line of sight grazes the surface here instead of crossing it.
            (double ex, double ey, double ez) = Eye(camera);
            double towardsEye = (point.Right * ex) + (point.Front * ey) + (point.Up * ez);
            double squared =
                (point.Right * point.Right) + (point.Front * point.Front) + (point.Up * point.Up);

            Assert.Equal(squared, towardsEye, 1e-9);

            // Just inside the boundary the camera does see it: a direction nudged towards the view axis, which
            // stays ON the sphere. Scaling it inwards instead would put it inside the ball, where it is hidden
            // for the obvious reason.
            Assert.True(SpatialProjection.IsVisible(
                Normalised(
                    point.Right + (0.02 * ex),
                    point.Front + (0.02 * ey),
                    point.Up + (0.02 * ez)),
                camera));
        }
    }

    [Fact]
    public void AnElevationRingNeverLeavesItsOwnHeight()
    {
        foreach (double elevation in new[] { -45.0, 0.0, 45.0 })
        {
            double expected = Math.Sin(Degrees(elevation));

            foreach (DevicePosition point in SpatialProjection.ElevationRing(elevation, 24))
            {
                Assert.Equal(expected, point.Up, Tolerance);
                Assert.Equal(1.0, point.Distance, Tolerance);
            }
        }

        // The poles have no horizontal direction left at all.
        foreach (DevicePosition point in SpatialProjection.ElevationRing(90.0, 8))
        {
            Assert.Equal(0.0, point.Right, Tolerance);
            Assert.Equal(0.0, point.Front, Tolerance);
            Assert.Equal(1.0, point.Up, Tolerance);
        }
    }

    [Fact]
    public void TheHorizonRingStartsAheadAndTurnsTowardsTheListenersRight()
    {
        DevicePosition[] horizon = SpatialProjection.ElevationRing(0.0, 8);

        // Sample 0 is straight ahead, sample 2 a quarter of the way round is hard right.
        Assert.Equal(0.0, horizon[0].Right, Tolerance);
        Assert.Equal(1.0, horizon[0].Front, Tolerance);
        Assert.Equal(1.0, horizon[2].Right, Tolerance);
        Assert.Equal(0.0, horizon[2].Front, Tolerance);
    }

    [Fact]
    public void ADistanceRingIsOnlyOnScreenWhenTheViewIsFramedAroundIt()
    {
        DevicePosition atThree = SpatialMixer.DirectionPosition(1).AtDistance(3.0);

        // Framed around the unit sphere -- the default -- a device three radii away is drawn past the edge,
        // which is why the picker widens the frame as soon as a distance is switched on.
        (double closeX, double closeY, _) = SpatialProjection.ProjectPoint(atThree, SpatialViewCamera.Default, Size);
        Assert.False(closeX > 0 && closeX < Size && closeY > 0 && closeY < Size);

        var framedOut = SpatialViewCamera.Default with { FrameRadius = 3.0 };
        (double x, double y, bool visible) = SpatialProjection.ProjectPoint(atThree, framedOut, Size);

        Assert.Equal(3.0, atThree.Distance, Tolerance);
        Assert.True(visible);
        Assert.InRange(x, 0.0, Size);
        Assert.InRange(y, 0.0, Size);

        // The same direction on the sphere is drawn somewhere else, so a device with a meaningful distance
        // cannot be mistaken for one that has none.
        (double sx, double sy, _) = SpatialProjection.Project(SpatialMixer.DirectionPosition(1), framedOut, Size);
        Assert.True(Math.Abs(x - sx) > 0.5 || Math.Abs(y - sy) > 0.5);
    }

    /// <summary>The camera's own position, as the tests compute it independently.</summary>
    private static (double X, double Y, double Z) Eye(SpatialViewCamera camera)
    {
        double horizontal = Math.Cos(Degrees(camera.ElevationDegrees)) * camera.Distance;

        return (
            Math.Sin(Degrees(camera.AzimuthDegrees)) * horizontal,
            Math.Cos(Degrees(camera.AzimuthDegrees)) * horizontal,
            Math.Sin(Degrees(camera.ElevationDegrees)) * camera.Distance);
    }

    private static DevicePosition Normalised(double right, double front, double up)
    {
        double length = Math.Sqrt((right * right) + (front * front) + (up * up));

        return new DevicePosition(right / length, front / length, up / length);
    }

    private static void AssertClose(DevicePosition expected, DevicePosition actual, string because)
    {
        Assert.True(
            Math.Abs(expected.Right - actual.Right) < 1e-5
            && Math.Abs(expected.Front - actual.Front) < 1e-5
            && Math.Abs(expected.Up - actual.Up) < 1e-5,
            $"{because}: expected ({expected.Right:0.#####}, {expected.Front:0.#####}, {expected.Up:0.#####}) "
            + $"but got ({actual.Right:0.#####}, {actual.Front:0.#####}, {actual.Up:0.#####})");
    }

    private static double Degrees(double value) => value * Math.PI / 180.0;
}
