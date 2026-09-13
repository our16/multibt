namespace MultiBT.Core.Sync;

/// <summary>
/// Where the position picker looks at the listener from.
/// </summary>
/// <param name="AzimuthDegrees">
/// Turn around the vertical axis. 0 looks at the listener from straight in front, and increasing values
/// turn to the listener's right.
/// </param>
/// <param name="ElevationDegrees">Height above the horizon: 0 is level with the listener, 90 is overhead.</param>
/// <param name="Distance">How far the camera sits from the listener, measured in sphere radii.</param>
/// <remarks>
/// Mutable state of the VIEW, not of any device, which is why it is not part of a device's settings: two
/// devices share one camera, and turning it to place one device must not move the other.
/// </remarks>
public readonly record struct SpatialViewCamera(double AzimuthDegrees, double ElevationDegrees, double Distance)
{
    /// <summary>In front of the listener and above, so the floor and the horizon are both visible.</summary>
    /// <remarks>
    /// Far enough out that the directions a click can reach form a wide cap: the reachable set is everything
    /// within acos(1/distance) of the view direction, which is 75 degrees here and narrows quickly as the
    /// camera comes closer. The rest is reached by turning the view, or exactly, by the preset directions.
    /// </remarks>
    public static SpatialViewCamera Default { get; } = new(0.0, 25.0, 4.0);

    /// <summary>
    /// The steepest and shallowest tilt the camera may take.
    /// </summary>
    /// <remarks>
    /// Not a taste limit: at exactly +-90 degrees the view direction is parallel to the world's up axis, the
    /// screen-up vector has no component left to build from, and the projection collapses to a single line.
    /// </remarks>
    public const double MaxElevationDegrees = 85.0;

    /// <summary>The same camera with its tilt kept inside the usable range.</summary>
    public SpatialViewCamera Clamped() => this with
    {
        ElevationDegrees = Math.Clamp(ElevationDegrees, -MaxElevationDegrees, MaxElevationDegrees),
        Distance = Math.Max(1.35, Distance),
    };
}

/// <summary>
/// Projects directions onto the picker's view, and turns a click back into a direction.
/// </summary>
/// <remarks>
/// <para>
/// What the picker draws is the set of DIRECTIONS a device may have: the surface of a unit sphere with the
/// listener at its centre. A click is therefore not a point in the room but the direction of the sphere's
/// NEAR surface under the cursor, which is what clicking a visible point means to the person doing it. The
/// far side is reachable by turning the camera, exactly as in any other 3D view.
/// </para>
/// <para>
/// Perspective rather than orthographic. A parallel projection flattens the sphere into a disc, and the
/// middle of the disc stops reading as "towards the viewer" -- which is the one thing the view exists to
/// communicate, and the reason a flat top-down plane cannot express a speaker that is above and to a side.
/// </para>
/// <para>
/// All of the trigonometry lives here, in Core, so that it can be tested without a window. An inverted
/// inverse is invisible in review and shows up only as "the markers are in the wrong place", which is the
/// worst possible way to find it.
/// </para>
/// <para>
/// Axes are the mixer's: +X is the listener's right, +Y is in front of them, +Z is above them.
/// </para>
/// </remarks>
public static class SpatialProjection
{
    /// <summary>Fraction of the viewport the sphere's silhouette fills, leaving a margin for its labels.</summary>
    private const double FillFraction = 0.86;

    /// <summary>
    /// Projects a direction to a viewport point.
    /// </summary>
    /// <param name="direction">A device direction; only its direction matters, not its length.</param>
    /// <param name="camera">Where the view is looking from.</param>
    /// <param name="size">Viewport side length in pixels; the viewport is square.</param>
    /// <returns>
    /// Screen coordinates with y increasing DOWNWARD as WPF counts it, and whether this direction is on the
    /// surface the camera can see. A direction on the far side still projects, but onto the near surface's
    /// pixels, so a caller must not draw it as if it were in front.
    /// </returns>
    public static (double X, double Y, bool Visible) Project(
        DevicePosition direction,
        SpatialViewCamera camera,
        double size)
    {
        if (size <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "The viewport must have a positive size.");
        }

        (double eyeX, double eyeY, double eyeZ) = Eye(camera);
        (double fx, double fy, double fz, double rx, double ry, double rz, double ux, double uy, double uz) =
            Basis(camera);

        (double nx, double ny, double nz) = Normalise(direction.Right, direction.Front, direction.Up);

        // Camera-space coordinates: x along the screen's right, y up the screen, z into the screen.
        double vx = nx - eyeX;
        double vy = ny - eyeY;
        double vz = nz - eyeZ;

        double depth = (vx * fx) + (vy * fy) + (vz * fz);
        double right = (vx * rx) + (vy * ry) + (vz * rz);
        double up = (vx * ux) + (vy * uy) + (vz * uz);

        // Behind the camera: still reported, so a caller can decide to hide it, but never divided by.
        if (depth <= 1e-6)
        {
            return (size / 2.0, size / 2.0, false);
        }

        double focal = Focal(camera, size);

        return (
            (size / 2.0) + (focal * right / depth),
            (size / 2.0) - (focal * up / depth),
            IsFacingCamera(nx, ny, nz, eyeX, eyeY, eyeZ));
    }

    /// <summary>
    /// Whether a direction is on the part of the sphere the camera can see.
    /// </summary>
    /// <remarks>
    /// NOT <c>dot(point, eye) &gt; 0</c>, which is the test for "on the near hemisphere" and lets through
    /// everything the near surface hides. The camera is OUTSIDE the sphere, so a direction is only visible
    /// when the eye is on the outward side of its surface: <c>dot(point, eye) &gt; 1</c>. Anything else is
    /// drawn where a nearer direction already is, which is what makes clicking a sphere show the near
    /// surface rather than the far one.
    /// </remarks>
    private static bool IsFacingCamera(double x, double y, double z, double eyeX, double eyeY, double eyeZ) =>
        ((x * eyeX) + (y * eyeY) + (z * eyeZ)) > 1.0;

    /// <summary>
    /// Turns a click into the direction it points at.
    /// </summary>
    /// <remarks>
    /// The ray from the camera through the pixel meets the near surface of the unit sphere, and that point is
    /// the direction. A click that misses the sphere -- slightly outside its silhouette -- is folded onto the
    /// rim rather than ignored: the rim IS the horizon, so a click that just misses the edge still means a
    /// direction, and discarding it reads as a dead area around the sphere.
    /// </remarks>
    /// <param name="x">Click x in viewport pixels.</param>
    /// <param name="y">Click y in viewport pixels, y increasing downward.</param>
    /// <param name="camera">Where the view is looking from.</param>
    /// <param name="size">Viewport side length in pixels.</param>
    public static DevicePosition Unproject(double x, double y, SpatialViewCamera camera, double size)
    {
        if (size <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "The viewport must have a positive size.");
        }

        (double eyeX, double eyeY, double eyeZ) = Eye(camera);
        (double fx, double fy, double fz, double rx, double ry, double rz, double ux, double uy, double uz) =
            Basis(camera);

        double focal = Focal(camera, size);

        // Camera-space direction of the ray: right and up across the pixel's offset, then into the screen.
        double a = x - (size / 2.0);
        double b = (size / 2.0) - y;

        (double dx, double dy, double dz) = Normalise(
            (rx * a) + (ux * b) + (fx * focal),
            (ry * a) + (uy * b) + (fy * focal),
            (rz * a) + (uz * b) + (fz * focal));

        // |eye + t*dir| = 1 with |dir| = 1.
        double o = (eyeX * dx) + (eyeY * dy) + (eyeZ * dz);
        double c = (eyeX * eyeX) + (eyeY * eyeY) + (eyeZ * eyeZ) - 1.0;
        double discriminant = (o * o) - c;

        // Near surface when the ray crosses the sphere, closest approach when it does not.
        double t = discriminant >= 0.0 ? -o - Math.Sqrt(discriminant) : -o;

        // A negative t would put the point behind the camera, which can only happen for a degenerate camera.
        t = Math.Max(t, 1e-6);

        (double px, double py, double pz) = Normalise(
            eyeX + (t * dx),
            eyeY + (t * dy),
            eyeZ + (t * dz));

        return new DevicePosition(px, py, pz);
    }

    /// <summary>The camera's position, in sphere radii.</summary>
    private static (double X, double Y, double Z) Eye(SpatialViewCamera camera)
    {
        SpatialViewCamera safe = camera.Clamped();

        double azimuth = Degrees(safe.AzimuthDegrees);
        double elevation = Degrees(safe.ElevationDegrees);
        double horizontal = Math.Cos(elevation) * safe.Distance;

        // Azimuth 0 is in FRONT of the listener, which is +Y: a picker that opens behind them would show the
        // directions they are least likely to be placing.
        return (
            Math.Sin(azimuth) * horizontal,
            Math.Cos(azimuth) * horizontal,
            Math.Sin(elevation) * safe.Distance);
    }

    /// <summary>
    /// The camera basis: forward into the screen, right across it, up the screen.
    /// </summary>
    /// <remarks>
    /// The world's up axis is not the screen's up axis whenever the camera is tilted, and at the tilt this
    /// view uses they differ by 25 degrees -- enough that skipping the correction puts every marker in the
    /// wrong place while still looking plausible.
    /// </remarks>
    private static (double Fx, double Fy, double Fz, double Rx, double Ry, double Rz, double Ux, double Uy, double Uz)
        Basis(SpatialViewCamera camera)
    {
        (double ex, double ey, double ez) = Eye(camera);

        // Forward points from the camera at the listener, who is the origin.
        (double fx, double fy, double fz) = Normalise(-ex, -ey, -ez);

        // Up is the world's up, with any part along the view direction removed (Gram-Schmidt).
        double alongForward = fz;
        double uxRaw = -alongForward * fx;
        double uyRaw = -alongForward * fy;
        double uzRaw = 1.0 - (alongForward * fz);

        (double ux, double uy, double uz) = Normalise(uxRaw, uyRaw, uzRaw);

        // Right completes the triad, so that (right, up, forward) is right-handed.
        (double rx, double ry, double rz) = (
            (uy * fz) - (uz * fy),
            (uz * fx) - (ux * fz),
            (ux * fy) - (uy * fx));

        return (fx, fy, fz, rx, ry, rz, ux, uy, uz);
    }

    /// <summary>
    /// Pixels per unit at the sphere's centre, chosen so the silhouette fills the viewport.
    /// </summary>
    /// <remarks>
    /// The silhouette of a unit sphere seen from distance d is a circle of radius 1/sqrt(d*d - 1) in the same
    /// units, so scaling by its inverse makes the sphere the same apparent size whatever the distance is and
    /// keeps the click targets from shrinking when the camera is pulled back.
    /// </remarks>
    private static double Focal(SpatialViewCamera camera, double size)
    {
        double distance = camera.Clamped().Distance;

        return (size * FillFraction / 2.0) * Math.Sqrt((distance * distance) - 1.0);
    }

    private static (double X, double Y, double Z) Normalise(double x, double y, double z)
    {
        double length = Math.Sqrt((x * x) + (y * y) + (z * z));

        if (length < 1e-12)
        {
            return (0.0, 0.0, 1.0);
        }

        return (x / length, y / length, z / length);
    }

    private static double Degrees(double value) => value * Math.PI / 180.0;
}
