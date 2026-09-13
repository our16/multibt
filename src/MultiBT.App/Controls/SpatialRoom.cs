using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MultiBT.Core.Sync;

namespace MultiBT.App.Controls;

/// <summary>
/// The room a device's direction is measured in: a floor, a sofa, and someone sitting on it.
/// </summary>
/// <remarks>
/// <para>
/// Solid faces rather than an outline, because the room exists to answer "which way am I facing" at a glance,
/// and an outline of a sofa only answers it once the viewer has worked out which end is the backrest.
/// </para>
/// <para>
/// The origin is the listener's EARS, not their seat: a direction is "where a speaker is relative to my head",
/// and the marker line is drawn from there. Everything else is placed around that: the body below and behind,
/// the floor a metre down, the sofa behind the body. The whole room is then scaled down, because a sofa at its
/// real size reaches well outside the unit sphere the directions live on, and drawing the bubble through the
/// furniture looks like a mistake rather than a diagram.
/// </para>
/// </remarks>
internal static class SpatialRoom
{
    /// <summary>
    /// How much the room is shrunk to sit inside the direction sphere.
    /// </summary>
    /// <remarks>
    /// The scene below is written in metres around the listener's ears, where the floor is a metre down and the
    /// sofa is two metres wide. Just under three quarters of that fits inside the unit sphere, which is as large
    /// as the furniture can be drawn before it starts running off the edge of the view: the sphere is what the
    /// camera is framed around, so anything past it is cropped rather than merely outside the bubble.
    /// </remarks>
    private const double Scale = 0.72;

    /// <summary>Half the width of the sofa, in the scene's own metres.</summary>
    private const double HalfWidth = 1.0;

    private static readonly Brush SofaTop = Frozen(new SolidColorBrush(Color.FromRgb(0xCE, 0xD7, 0xE3)));
    private static readonly Brush SofaFront = Frozen(new SolidColorBrush(Color.FromRgb(0xB2, 0xC0, 0xD2)));
    private static readonly Brush SofaSide = Frozen(new SolidColorBrush(Color.FromRgb(0x97, 0xA8, 0xBE)));
    private static readonly Pen SofaPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x7E, 0x8E, 0xA5))), 0.7));

    private static readonly Brush ClothTop = Frozen(new SolidColorBrush(Color.FromRgb(0x8B, 0x9C, 0xB3)));
    private static readonly Brush ClothFront = Frozen(new SolidColorBrush(Color.FromRgb(0x69, 0x7A, 0x93)));
    private static readonly Brush ClothSide = Frozen(new SolidColorBrush(Color.FromRgb(0x53, 0x62, 0x78)));
    private static readonly Pen ClothPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x44, 0x51, 0x64))), 0.7));

    private static readonly Brush SkinFill = Frozen(new SolidColorBrush(Color.FromRgb(0xF3, 0xE4, 0xD3)));
    private static readonly Pen SkinPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xB2, 0x8A, 0x6B))), 0.7));
    private static readonly Brush HairFill = Frozen(new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x68)));

    private static readonly Brush FloorFill = Frozen(new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)));
    private static readonly Pen FloorPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xD8, 0xE1, 0xEC))), 1.0));
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x5B, 0x6B, 0x84)));

    private static readonly Typeface LabelTypeface = new("Segoe UI");

    /// <summary>A rectangular block, given its two opposite corners in (right, front, up).</summary>
    private readonly record struct Block(double X0, double Y0, double Z0, double X1, double Y1, double Z1);

    /// <summary>One visible face, ready to be filled, with how far away it is.</summary>
    private readonly record struct Face(double Depth, Point[] Corners, Brush Fill, Pen Pen);

    /// <summary>
    /// Draws the floor, the sofa, the person on it, and the labels for ahead and behind.
    /// </summary>
    /// <param name="dc">Where to draw.</param>
    /// <param name="camera">The view, already framed for the device being placed.</param>
    /// <param name="size">Viewport side length in pixels.</param>
    /// <param name="frontText">Label for the direction the person faces.</param>
    /// <param name="backText">Label for the direction behind them.</param>
    /// <param name="listenerText">Label for the listener themselves.</param>
    public static void Draw(
        DrawingContext dc,
        SpatialViewCamera camera,
        double size,
        string frontText,
        string backText,
        string listenerText)
    {
        var faces = new List<Face>();

        // The sofa: a seat, a backrest and two arms, so that it reads as a sofa rather than as a box.
        Add(faces, camera, size, new Block(-HalfWidth, -0.52, -1.00, HalfWidth, 0.18, -0.60), SofaTop, SofaFront, SofaSide, SofaPen);
        Add(faces, camera, size, new Block(-HalfWidth, -0.74, -0.60, HalfWidth, -0.52, 0.12), SofaTop, SofaFront, SofaSide, SofaPen);
        Add(faces, camera, size, new Block(-HalfWidth, -0.74, -1.00, -0.86, 0.18, -0.28), SofaTop, SofaFront, SofaSide, SofaPen);
        Add(faces, camera, size, new Block(0.86, -0.74, -1.00, HalfWidth, 0.18, -0.28), SofaTop, SofaFront, SofaSide, SofaPen);

        // Cushions, sitting a little proud of the seat so the sofa has a surface rather than a lid.
        Add(faces, camera, size, new Block(-0.84, -0.50, -0.58, -0.03, 0.14, -0.48), SofaTop, SofaFront, SofaSide, SofaPen);
        Add(faces, camera, size, new Block(0.03, -0.50, -0.58, 0.84, 0.14, -0.48), SofaTop, SofaFront, SofaSide, SofaPen);

        // The person, seated: thighs forward, shins down, torso upright, head at the origin of the directions.
        Add(faces, camera, size, new Block(-0.24, -0.18, -0.62, -0.04, 0.52, -0.48), ClothTop, ClothFront, ClothSide, ClothPen);
        Add(faces, camera, size, new Block(0.04, -0.18, -0.62, 0.24, 0.52, -0.48), ClothTop, ClothFront, ClothSide, ClothPen);
        Add(faces, camera, size, new Block(-0.24, 0.44, -1.00, -0.04, 0.60, -0.56), ClothTop, ClothFront, ClothSide, ClothPen);
        Add(faces, camera, size, new Block(0.04, 0.44, -1.00, 0.24, 0.60, -0.56), ClothTop, ClothFront, ClothSide, ClothPen);
        Add(faces, camera, size, new Block(-0.24, -0.16, -0.52, 0.24, 0.06, 0.02), ClothTop, ClothFront, ClothSide, ClothPen);

        // Arms, resting along the sides and forward onto the knees.
        Add(faces, camera, size, new Block(-0.38, -0.12, -0.48, -0.25, 0.30, 0.00), ClothTop, ClothFront, ClothSide, ClothPen);
        Add(faces, camera, size, new Block(0.25, -0.12, -0.48, 0.38, 0.30, 0.00), ClothTop, ClothFront, ClothSide, ClothPen);

        // Neck.
        Add(faces, camera, size, new Block(-0.08, -0.10, -0.02, 0.08, 0.02, 0.06), ClothTop, ClothFront, ClothSide, ClothPen);

        faces.Sort((left, right) => right.Depth.CompareTo(left.Depth));

        DrawFloor(dc, camera, size);

        foreach (Face face in faces)
        {
            dc.DrawGeometry(face.Fill, face.Pen, Polygon(face.Corners));
        }

        DrawHead(dc, camera, size);

        // Placed clear of the furniture rather than at the midpoint of what they name: a label behind the sofa
        // has to be somewhere the sofa is not, or it is drawn on the backrest and read as part of it.
        DrawLabel(dc, camera, size, listenerText, new DevicePosition(0.52, -0.05, 0.16));
        DrawLabel(dc, camera, size, frontText, new DevicePosition(0.0, 1.15, -0.95));
        DrawLabel(dc, camera, size, backText, new DevicePosition(1.42, -0.95, -0.9));
    }

    /// <summary>The head, drawn as a disc because a sphere projects to one and nothing else would read as a head.</summary>
    private static void DrawHead(DrawingContext dc, SpatialViewCamera camera, double size)
    {
        const double Radius = 0.17;

        // A little above the shoulders, which puts its centre on the origin: the ear height a direction is
        // measured from.
        DevicePosition centre = new(0.0, -0.05, 0.17);

        (double x, double y, _) = Project(centre, camera, size);
        (double edgeX, double ignoredTop, _) = Project(centre with { Right = centre.Right + Radius }, camera, size);
        (double ignoredSide, double edgeY, _) = Project(centre with { Up = centre.Up + Radius }, camera, size);

        double radiusX = Math.Max(Math.Abs(edgeX - x), 2.0);
        double radiusY = Math.Max(Math.Abs(edgeY - y), 2.0);

        dc.DrawEllipse(HairFill, SkinPen, new Point(x, y), radiusX, radiusY);

        // Face, in the direction the person is looking: a smaller disc offset towards the front.
        (double fx, double fy, _) = Project(centre with { Front = centre.Front + (Radius * 0.45) }, camera, size);

        dc.DrawEllipse(SkinFill, null, new Point(fx, fy), radiusX * 0.72, radiusY * 0.72);
    }

    private static void DrawFloor(DrawingContext dc, SpatialViewCamera camera, double size)
    {
        const int Samples = 64;
        var rim = new Point[Samples];

        for (int i = 0; i < Samples; i++)
        {
            double angle = 2.0 * Math.PI * i / Samples;

            // The floor is a metre below the listener's ears, out to just past the arms of the sofa.
            (double x, double y, _) = Project(
                new DevicePosition(Math.Sin(angle) * 1.15, Math.Cos(angle) * 1.15, -1.0),
                camera,
                size);

            rim[i] = new Point(x, y);
        }

        dc.DrawGeometry(FloorFill, FloorPen, Polygon(rim));
    }

    private static void DrawLabel(
        DrawingContext dc,
        SpatialViewCamera camera,
        double size,
        string text,
        DevicePosition at)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        (double x, double y, _) = Project(at, camera, size);

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            12.0,
            LabelBrush,
            VisualStudioPixelsPerDip);

        dc.DrawText(formatted, new Point(x - (formatted.Width / 2.0), y - (formatted.Height / 2.0)));
    }

    /// <summary>
    /// The device scale factor WPF is rendering at, for text.
    /// </summary>
    /// <remarks>
    /// A constant 1.0 here, which makes the text slightly small on a high-DPI screen rather than blurry: the
    /// alternative is a dependency on a live visual, and this class draws into an offscreen render as well.
    /// </remarks>
    private const double VisualStudioPixelsPerDip = 1.0;

    /// <summary>Adds every face of a block that the camera can see.</summary>
    private static void Add(
        List<Face> faces,
        SpatialViewCamera camera,
        double size,
        Block block,
        Brush top,
        Brush front,
        Brush side,
        Pen pen)
    {
        DevicePosition eye = SpatialProjection.CameraPosition(camera);

        (DevicePosition Corner, DevicePosition Normal)[] named =
        [
            (new DevicePosition(0.0, 0.0, block.Z1), new DevicePosition(0.0, 0.0, 1.0)),
            (new DevicePosition(0.0, 0.0, block.Z0), new DevicePosition(0.0, 0.0, -1.0)),
            (new DevicePosition(0.0, block.Y1, 0.0), new DevicePosition(0.0, 1.0, 0.0)),
            (new DevicePosition(0.0, block.Y0, 0.0), new DevicePosition(0.0, -1.0, 0.0)),
            (new DevicePosition(block.X1, 0.0, 0.0), new DevicePosition(1.0, 0.0, 0.0)),
            (new DevicePosition(block.X0, 0.0, 0.0), new DevicePosition(-1.0, 0.0, 0.0)),
        ];

        for (int i = 0; i < named.Length; i++)
        {
            Brush fill = i switch
            {
                0 => top,
                1 => side,
                2 => front,
                _ => side,
            };

            Point[] corners = i switch
            {
                0 => [Corner(block.X0, block.Y0, block.Z1), Corner(block.X1, block.Y0, block.Z1), Corner(block.X1, block.Y1, block.Z1), Corner(block.X0, block.Y1, block.Z1)],
                1 => [Corner(block.X0, block.Y0, block.Z0), Corner(block.X1, block.Y0, block.Z0), Corner(block.X1, block.Y1, block.Z0), Corner(block.X0, block.Y1, block.Z0)],
                2 => [Corner(block.X0, block.Y1, block.Z0), Corner(block.X1, block.Y1, block.Z0), Corner(block.X1, block.Y1, block.Z1), Corner(block.X0, block.Y1, block.Z1)],
                3 => [Corner(block.X0, block.Y0, block.Z0), Corner(block.X1, block.Y0, block.Z0), Corner(block.X1, block.Y0, block.Z1), Corner(block.X0, block.Y0, block.Z1)],
                4 => [Corner(block.X1, block.Y0, block.Z0), Corner(block.X1, block.Y1, block.Z0), Corner(block.X1, block.Y1, block.Z1), Corner(block.X1, block.Y0, block.Z1)],
                _ => [Corner(block.X0, block.Y0, block.Z0), Corner(block.X0, block.Y1, block.Z0), Corner(block.X0, block.Y1, block.Z1), Corner(block.X0, block.Y0, block.Z1)],
            };

            DevicePosition normal = named[i].Normal;
            DevicePosition centre = new(
                (block.X0 + block.X1) / 2.0,
                (block.Y0 + block.Y1) / 2.0,
                (block.Z0 + block.Z1) / 2.0);

            // Back-face culling: a face whose outward normal points away from the eye cannot be seen.
            double towardsEye =
                ((eye.Right - centre.Right) * normal.Right)
                + ((eye.Front - centre.Front) * normal.Front)
                + ((eye.Up - centre.Up) * normal.Up);

            if (towardsEye <= 0.0)
            {
                continue;
            }

            double depth = Math.Sqrt(
                ((eye.Right - centre.Right) * (eye.Right - centre.Right))
                + ((eye.Front - centre.Front) * (eye.Front - centre.Front))
                + ((eye.Up - centre.Up) * (eye.Up - centre.Up)));

            faces.Add(new Face(depth, corners, fill, pen));
        }

        Point Corner(double x, double y, double z)
        {
            (double px, double py, _) = Project(new DevicePosition(x, y, z), camera, size);

            return new Point(px, py);
        }
    }

    /// <summary>Projects a point of the scene, after shrinking the room to fit the direction sphere.</summary>
    private static (double X, double Y, bool Visible) Project(
        DevicePosition point,
        SpatialViewCamera camera,
        double size) =>
        SpatialProjection.ProjectPoint(
            new DevicePosition(point.Right * Scale, point.Front * Scale, point.Up * Scale),
            camera,
            size);

    private static StreamGeometry Polygon(Point[] points)
    {
        var geometry = new StreamGeometry();

        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: true, isClosed: true);

            for (int i = 1; i < points.Length; i++)
            {
                context.LineTo(points[i], isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();

        return geometry;
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();

        return freezable;
    }
}
