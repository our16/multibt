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
/// <param name="FrameRadius">
/// The radius the view is framed around, in sphere radii. 1 keeps the direction sphere filling the viewport;
/// a device placed further out needs a larger frame or its marker is drawn off the edge.
/// </param>
/// <param name="Zoom">Magnification on top of the framing. 1 is the default view; the wheel changes this.</param>
/// <remarks>
/// Mutable state of the VIEW, not of any device, which is why it is not part of a device's settings: two
/// devices share one camera, and turning it to place one device must not move the other.
/// </remarks>
public readonly record struct SpatialViewCamera(
    double AzimuthDegrees,
    double ElevationDegrees,
    double Distance,
    double FrameRadius = 1.0,
    double Zoom = 1.0)
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
    public SpatialViewCamera Clamped()
    {
        double frame = Math.Max(FrameRadius, 1e-3);

        return this with
        {
            ElevationDegrees = Math.Clamp(ElevationDegrees, -MaxElevationDegrees, MaxElevationDegrees),
            FrameRadius = frame,

            // The camera must stay outside what it is framing: at or inside the framed radius the silhouette has
            // no solution and the projection collapses to a point.
            Distance = Math.Max(Math.Max(1.35, Distance), frame * 1.2),
            Zoom = Math.Clamp(Zoom, 0.35, 3.0),
        };
    }
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
        double size) => ProjectPoint(direction.AtDistance(1.0), camera, size);

    /// <summary>
    /// Projects a point in space, measured in sphere radii.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Project"/> because the picker draws one thing that is not on the sphere: the
    /// ring at the device's own distance, and the marker sitting on it, when its distance is switched on.
    /// </remarks>
    /// <param name="point">A point in space; the sphere has radius 1 and the listener is the origin.</param>
    /// <param name="camera">Where the view is looking from.</param>
    /// <param name="size">Viewport side length in pixels.</param>
    public static (double X, double Y, bool Visible) ProjectPoint(
        DevicePosition point,
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

        // Camera-space coordinates: x along the screen's right, y up the screen, z into the screen.
        double vx = point.Right - eyeX;
        double vy = point.Front - eyeY;
        double vz = point.Up - eyeZ;

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
            IsVisible(point, camera));
    }

    /// <summary>
    /// Whether a point in space is in front of everything the sphere hides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The line of sight from the camera to the point must not cross the sphere first. That is a segment
    /// against sphere test, and it is not the same as the rule a direction on the surface obeys:
    /// <c>dot(point, eye) &gt; 1</c> is only correct ON the unit sphere, because it is the statement that the
    /// eye lies on the outward side of the surface there. Applied to a point three radii away it declares a
    /// speaker in plain view to be hidden, which is what the distance-ring test caught.
    /// </para>
    /// <para>
    /// A point exactly on the far surface has its first crossing strictly before it and is hidden; a point on
    /// the near surface, or on the rim where the line of sight is tangent, is the crossing itself and is not.
    /// </para>
    /// </remarks>
    public static bool IsVisible(DevicePosition point, SpatialViewCamera camera)
    {
        (double eyeX, double eyeY, double eyeZ) = Eye(camera);

        double vx = point.Right - eyeX;
        double vy = point.Front - eyeY;
        double vz = point.Up - eyeZ;

        double a = (vx * vx) + (vy * vy) + (vz * vz);

        if (a < 1e-12)
        {
            // The point IS the camera, so there is no line of sight to speak of.
            return false;
        }

        double b = 2.0 * ((eyeX * vx) + (eyeY * vy) + (eyeZ * vz));
        double c = (eyeX * eyeX) + (eyeY * eyeY) + (eyeZ * eyeZ) - 1.0;
        double discriminant = (b * b) - (4.0 * a * c);

        if (discriminant < 0.0)
        {
            // The line of sight misses the sphere altogether.
            return true;
        }

        double firstCrossing = (-b - Math.Sqrt(discriminant)) / (2.0 * a);

        return firstCrossing >= 1.0 - 1e-9;
    }

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

    /// <summary>
    /// The sphere's outline as the camera sees it, as a ring of directions.
    /// </summary>
    /// <remarks>
    /// The outline is not the horizon and not the equator: seen from outside a sphere, it is the set of points
    /// where the line of sight is tangent, which sits at <c>1 / distance</c> along the view axis. Drawing the
    /// horizon there instead is a mistake that still looks like a sphere, which is why this is computed rather
    /// than eyeballed.
    /// </remarks>
    /// <param name="camera">Where the view is looking from.</param>
    /// <param name="samples">How many points make up the ring.</param>
    public static DevicePosition[] Silhouette(SpatialViewCamera camera, int samples = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 3);

        SpatialViewCamera safe = camera.Clamped();
        (double ex, double ey, double ez) = Eye(safe);

        double length = Math.Sqrt((ex * ex) + (ey * ey) + (ez * ez));
        (double dx, double dy, double dz) = (ex / length, ey / length, ez / length);

        // Two directions across the view axis to sweep the ring along. The tilt is clamped short of vertical,
        // so (dx, dy) can never both be zero and this basis never collapses.
        (double ax, double ay, double az) = Normalise(-dy, dx, 0.0);
        (double bx, double by, double bz) = (
            (dy * az) - (dz * ay),
            (dz * ax) - (dx * az),
            (dx * ay) - (dy * ax));

        double towardsEye = 1.0 / length;
        double radius = Math.Sqrt(Math.Max(0.0, 1.0 - (towardsEye * towardsEye)));

        var ring = new DevicePosition[samples];

        for (int i = 0; i < samples; i++)
        {
            double angle = 2.0 * Math.PI * i / samples;
            double c = Math.Cos(angle);
            double s = Math.Sin(angle);

            ring[i] = Normalised(
                (towardsEye * dx) + (radius * ((c * ax) + (s * bx))),
                (towardsEye * dy) + (radius * ((c * ay) + (s * by))),
                (towardsEye * dz) + (radius * ((c * az) + (s * bz))));
        }

        return ring;
    }

    /// <summary>
    /// A ring of directions at one elevation, for drawing the sphere's latitude lines.
    /// </summary>
    /// <param name="elevationDegrees">0 is the horizon, +90 straight overhead, -90 straight below.</param>
    /// <param name="samples">How many points make up the ring.</param>
    public static DevicePosition[] ElevationRing(double elevationDegrees, int samples = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 3);

        double elevation = Degrees(elevationDegrees);
        double horizontal = Math.Cos(elevation);
        double up = Math.Sin(elevation);

        var ring = new DevicePosition[samples];

        for (int i = 0; i < samples; i++)
        {
            double azimuth = 2.0 * Math.PI * i / samples;

            ring[i] = new DevicePosition(
                Math.Sin(azimuth) * horizontal,
                Math.Cos(azimuth) * horizontal,
                up);
        }

        return ring;
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
    /// Pixels per unit at the centre of the view, chosen so the framed radius fills the viewport.
    /// </summary>
    /// <remarks>
    /// A sphere of radius r seen from distance d puts its silhouette at <c>f * r / sqrt(d*d - r*r)</c> pixels, so
    /// the focal that makes it fill the viewport is that expression inverted. Note <c>sqrt(d*d - r*r)</c> and
    /// not <c>sqrt(d*d - 1)</c>: the two only agree when the framed radius is 1, and using the latter for a
    /// device three metres away framed 46 percent too large -- which put the marker off the edge it was
    /// supposed to be inside.
    /// </remarks>
    private static double Focal(SpatialViewCamera camera, double size)
    {
        SpatialViewCamera safe = camera.Clamped();
        double framed = safe.FrameRadius;

        double separation = Math.Max((safe.Distance * safe.Distance) - (framed * framed), 1e-6);

        return (size * FillFraction / 2.0) * Math.Sqrt(separation) / framed * safe.Zoom;
    }

    /// <summary>
    /// Where the camera is, in sphere radii.
    /// </summary>
    /// <remarks>
    /// Public because drawing a solid room needs it: which faces of a box point away from the camera, and which
    /// box is in front of which, are both decided against this point and cannot be guessed from the projection.
    /// </remarks>
    public static DevicePosition CameraPosition(SpatialViewCamera camera)
    {
        (double x, double y, double z) = Eye(camera);

        return new DevicePosition(x, y, z);
    }

    /// <summary>The unit direction of a vector, as a position.</summary>
    private static DevicePosition Normalised(double x, double y, double z)
    {
        (double nx, double ny, double nz) = Normalise(x, y, z);

        return new DevicePosition(nx, ny, nz);
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
