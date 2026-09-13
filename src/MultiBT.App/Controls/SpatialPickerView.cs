using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MultiBT.Core.Sync;

namespace MultiBT.App.Controls;

/// <summary>
/// The position picker: a view of the sphere of directions a device may be placed at.
/// </summary>
/// <remarks>
/// <para>
/// A custom element rather than a tree of shapes, because the drawing is generated from the camera every time
/// it moves: a ring of directions has to be projected point by point, and a XAML shape per sample would mean
/// rebuilding children on every mouse move. All of the geometry lives in <see cref="SpatialProjection"/>; this
/// class decides colours and turns mouse input into a direction, nothing more.
/// </para>
/// <para>
/// Clicking means the direction of the sphere's near surface, which is what a click on a visible point means.
/// Directions the near surface hides are drawn HOLLOW so that "I cannot reach that from here" is visible
/// rather than discovered, and the preset buttons beside the view reach every direction without turning it.
/// </para>
/// </remarks>
public sealed class SpatialPickerView : FrameworkElement
{
    /// <summary>How far the view turns per pixel dragged.</summary>
    private const double DegreesPerPixel = 0.4;

    /// <summary>
    /// How close and how far the camera may be pulled.
    /// </summary>
    /// <remarks>
    /// The near limit is not taste: the reachable directions form a cap of acos(1 / distance), so a camera
    /// pulled closer than this leaves too little of the sphere clickable to be useful.
    /// </remarks>
    private const double MinimumCameraDistance = 1.7;

    /// <inheritdoc cref="MinimumCameraDistance" />
    private const double MaximumCameraDistance = 9.0;

    private static readonly Brush SphereFill = Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0xF8, 0xFA, 0xFC)));
    private static readonly Brush FloorFill = Frozen(new SolidColorBrush(Color.FromRgb(0xEF, 0xF6, 0xFF)));
    private static readonly Brush SofaFill = Frozen(new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)));
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)));
    private static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)));
    private static readonly Brush ListenerFill = Frozen(new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)));
    private static readonly Brush VisibleDot = Frozen(new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)));
    private static readonly Brush HollowFill = Frozen(new SolidColorBrush(Colors.White));
    private static readonly Brush Halo = Frozen(new SolidColorBrush(Colors.White));

    private static readonly Pen OutlinePen = Frozen(new Pen(
        Frozen(new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1))), 1.0));

    private static readonly Pen RingPen = Frozen(new Pen(
        Frozen(new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0))), 1.0));

    private static readonly Pen AxisPen = Frozen(new Pen(
        Frozen(new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0))), 1.0)
    {
        DashStyle = DashStyles.Dot,
    });

    private static readonly Pen DistancePen = Frozen(new Pen(
        Frozen(new SolidColorBrush(Color.FromRgb(0x93, 0xC5, 0xFD))), 1.0)
    {
        DashStyle = DashStyles.Dash,
    });

    private static readonly Pen DotPen = Frozen(new Pen(
        Frozen(new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))), 1.0));

    private static readonly Pen AccentPen = Frozen(new Pen(Accent, 1.5));

    private SpatialViewCamera _camera = SpatialViewCamera.Default;
    private bool _placing;
    private bool _orbiting;
    private Point _last;

    /// <summary>Raised while the user places the device, once per move, so it is heard as it is dragged.</summary>
    public event EventHandler<DevicePosition>? DirectionPicked;

    /// <summary>Initializes the picker with the cursor its two gestures need.</summary>
    public SpatialPickerView()
    {
        Cursor = Cursors.Cross;
        Focusable = true;

        // A popup's content is loaded again each time it opens, which is exactly where "look at this device"
        // belongs: opening the picker for a device behind the listener must not show a sphere whose marker
        // appears to be missing, because the obvious conclusion is that the position was lost.
        Loaded += (_, _) => LookAt(Direction);
    }

    /// <summary>The direction to show, as a unit vector.</summary>
    public static readonly DependencyProperty DirectionProperty = DependencyProperty.Register(
        nameof(Direction),
        typeof(DevicePosition),
        typeof(SpatialPickerView),
        new FrameworkPropertyMetadata(default(DevicePosition), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How far away the device is, shown as a ring once the distance is switched on.</summary>
    public static readonly DependencyProperty DistanceProperty = DependencyProperty.Register(
        nameof(Distance),
        typeof(double),
        typeof(SpatialPickerView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Whether that distance is drawn as the marker's radius.</summary>
    public static readonly DependencyProperty UseDistanceProperty = DependencyProperty.Register(
        nameof(UseDistance),
        typeof(bool),
        typeof(SpatialPickerView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// The label drawn on the floor in front of the listener.
    /// </summary>
    /// <remarks>
    /// A sphere of directions has no orientation of its own: without a word for "ahead" and something in the
    /// room to stand behind the listener, there is no way to tell a front-right speaker from a front-left one
    /// at a glance, which is the only question this view exists to answer.
    /// </remarks>
    public static readonly DependencyProperty FrontTextProperty = DependencyProperty.Register(
        nameof(FrontText),
        typeof(string),
        typeof(SpatialPickerView),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The label drawn at the back of the floor, where the sofa stands.</summary>
    public static readonly DependencyProperty BackTextProperty = DependencyProperty.Register(
        nameof(BackText),
        typeof(string),
        typeof(SpatialPickerView),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The label for the listener, marked on the floor at the origin.</summary>
    public static readonly DependencyProperty ListenerTextProperty = DependencyProperty.Register(
        nameof(ListenerText),
        typeof(string),
        typeof(SpatialPickerView),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <inheritdoc cref="FrontTextProperty" />
    public string FrontText
    {
        get => (string)GetValue(FrontTextProperty);
        set => SetValue(FrontTextProperty, value);
    }

    /// <inheritdoc cref="BackTextProperty" />
    public string BackText
    {
        get => (string)GetValue(BackTextProperty);
        set => SetValue(BackTextProperty, value);
    }

    /// <inheritdoc cref="ListenerTextProperty" />
    public string ListenerText
    {
        get => (string)GetValue(ListenerTextProperty);
        set => SetValue(ListenerTextProperty, value);
    }

    /// <inheritdoc cref="DirectionProperty" />
    public DevicePosition Direction
    {
        get => (DevicePosition)GetValue(DirectionProperty);
        set => SetValue(DirectionProperty, value);
    }

    /// <inheritdoc cref="DistanceProperty" />
    public double Distance
    {
        get => (double)GetValue(DistanceProperty);
        set => SetValue(DistanceProperty, value);
    }

    /// <inheritdoc cref="UseDistanceProperty" />
    public bool UseDistance
    {
        get => (bool)GetValue(UseDistanceProperty);
        set => SetValue(UseDistanceProperty, value);
    }

    /// <summary>
    /// Points the view at a direction, so that the marker is on screen when the picker opens.
    /// </summary>
    /// <remarks>
    /// Without this, opening the picker for a device placed behind the listener shows a sphere with the device
    /// apparently missing -- and the obvious conclusion is that the position was lost.
    /// </remarks>
    public void LookAt(DevicePosition direction)
    {
        if (direction.IsOrigin)
        {
            return;
        }

        double unitUp = Math.Clamp(direction.Up / direction.Distance, -1.0, 1.0);

        _camera = new SpatialViewCamera(
            Math.Atan2(direction.Right, direction.Front) * 180.0 / Math.PI,
            Math.Asin(unitUp) * 180.0 / Math.PI,
            _camera.Distance).Clamped();

        InvalidateVisual();
    }

    /// <summary>Puts the view back where it opens: in front of the listener and above.</summary>
    public void ResetCamera()
    {
        _camera = SpatialViewCamera.Default;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        (double size, double offsetX, double offsetY) = Viewport();

        if (size < 40.0)
        {
            return;
        }

        drawingContext.PushTransform(new TranslateTransform(offsetX, offsetY));

        DrawFloor(drawingContext);
        drawingContext.DrawGeometry(SphereFill, OutlinePen, Close(SpatialProjection.Silhouette(View)));
        DrawFurniture(drawingContext);

        // A wireframe globe, hidden edges included: the fill is nearly white, so a dashed-away back half would
        // cost more code than it communicates.
        DrawRing(drawingContext, 0.0, RingPen);
        DrawRing(drawingContext, 45.0, RingPen);
        DrawRing(drawingContext, -45.0, RingPen);

        DrawAxis(drawingContext, size);
        DrawPresetDots(drawingContext, size);
        DrawOrientation(drawingContext, size);
        DrawMarker(drawingContext, size);

        drawingContext.Pop();
    }

    /// <summary>
    /// The floor the listener stands on, as a disc through the origin.
    /// </summary>
    /// <remarks>
    /// Without a horizontal plane the rings are a bubble in space, and "the speaker is behind me" cannot be
    /// read off it. The disc is schematic -- its radius is the framed radius, not a room measurement.
    /// </remarks>
    private void DrawFloor(DrawingContext dc)
    {
        DevicePosition[] rim = SpatialProjection.ElevationRing(0.0, 64);
        var ring = new DevicePosition[rim.Length];

        for (int i = 0; i < rim.Length; i++)
        {
            ring[i] = rim[i].AtDistance(View.FrameRadius);
        }

        dc.DrawGeometry(FloorFill, OutlinePen, Close(ring));
    }

    /// <summary>
    /// The room the directions are relative to: a sofa behind the listener.
    /// </summary>
    /// <remarks>
    /// Drawn inside the sphere's footprint rather than to scale, because the sphere is a unit of DIRECTIONS and
    /// a sofa at its true size would sit outside the frame at every camera distance. What matters is which way
    /// it is on, not how big it is.
    /// </remarks>
    private void DrawFurniture(DrawingContext dc)
    {
        // Seat: a quad on the floor behind the listener, plus a backrest rising from its far edge.
        DevicePosition[] seat =
        [
            new(-0.62, -0.66, 0.0),
            new(0.62, -0.66, 0.0),
            new(0.62, -0.98, 0.0),
            new(-0.62, -0.98, 0.0),
        ];

        dc.DrawGeometry(SofaFill, DotPen, Close(seat));

        DevicePosition[] back =
        [
            new(-0.62, -0.98, 0.0),
            new(0.62, -0.98, 0.0),
            new(0.62, -0.98, 0.34),
            new(-0.62, -0.98, 0.34),
        ];

        dc.DrawGeometry(SofaFill, DotPen, Close(back));
    }

    /// <summary>Labels the two directions that cannot be guessed: ahead and behind.</summary>
    private void DrawOrientation(DrawingContext dc, double size)
    {
        (double frontX, double frontY, _) = SpatialProjection.ProjectPoint(new DevicePosition(0.0, 0.86, 0.0), View, size);
        DrawLabel(dc, FrontText, frontX, frontY);

        (double backX, double backY, _) = SpatialProjection.ProjectPoint(new DevicePosition(0.0, -1.12, 0.0), View, size);
        DrawLabel(dc, BackText, backX, backY);
    }

    private void DrawLabel(DrawingContext dc, string text, double x, double y)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11.0,
            LabelBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(formatted, new Point(x - (formatted.Width / 2.0), y - (formatted.Height / 2.0)));
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnMouseLeftButtonDown(e);

        // Shift turns placing into turning the view, and so does grabbing the empty space around the sphere:
        // there is nothing to place out there, so dragging it can only mean "show me the other side".
        _orbiting = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift || !IsOnTheSphere(e.GetPosition(this));
        _placing = !_orbiting;
        _last = e.GetPosition(this);

        CaptureMouse();

        if (_placing)
        {
            Pick(_last);
        }

        Cursor = _orbiting ? Cursors.SizeAll : Cursors.Cross;
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnMouseRightButtonDown(e);

        _orbiting = true;
        _placing = false;
        _last = e.GetPosition(this);

        CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnMouseUp(e);

        _orbiting = false;
        _placing = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Cross;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnMouseMove(e);

        Point point = e.GetPosition(this);

        if (_orbiting)
        {
            double dx = point.X - _last.X;
            double dy = point.Y - _last.Y;

            // Dragging turns the room rather than the camera: to the right brings the listener's left into
            // view, and downward tips the view towards overhead. Both are one sign away from the opposite
            // feel, so if it reads backwards this is the line to flip.
            _camera = (_camera with
            {
                AzimuthDegrees = _camera.AzimuthDegrees - (dx * DegreesPerPixel),
                ElevationDegrees = _camera.ElevationDegrees + (dy * DegreesPerPixel),
            }).Clamped();

            InvalidateVisual();
        }
        else if (_placing)
        {
            Pick(point);
        }

        _last = point;
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnMouseWheel(e);

        double steps = e.Delta / 120.0;

        _camera = _camera with
        {
            Distance = Math.Clamp(_camera.Distance - (steps * 0.4), MinimumCameraDistance, MaximumCameraDistance),
        };

        InvalidateVisual();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        if (!_orbiting && !_placing)
        {
            Cursor = Cursors.Cross;
        }
    }

    /// <summary>
    /// The camera as the drawing uses it: the same pose, framed around whatever has to be on screen.
    /// </summary>
    /// <remarks>
    /// Framing is separate from the pose because it depends on the DEVICE, not on where the user is looking
    /// from. Left at the unit sphere, a device switched to three metres would have its marker drawn off the
    /// edge of the view -- which reads as "the position was lost" rather than "this view is framed too closely".
    /// </remarks>
    private SpatialViewCamera View => _camera with { FrameRadius = Frame };

    /// <summary>
    /// How far out the view is framed, in sphere radii, when the distance counts.
    /// </summary>
    /// <remarks>
    /// Compressed rather than equal to the distance, because framing a five metre device at five radii would
    /// shrink the direction sphere to a fifth of the view and leave nothing worth clicking. A third of the
    /// excess keeps the sphere at least 40 percent of the frame at the far end, and the ring is always drawn at
    /// the frame's edge so it is never the part that falls off. The number itself is stated by the slider and
    /// the readout underneath, so the ring does not have to be a ruler.
    /// </remarks>
    private double Frame
    {
        get
        {
            if (!UseDistance)
            {
                return SpatialMixer.DirectionRadiusMetres;
            }

            double beyond =
                Math.Clamp(Distance, SpatialMixer.DirectionRadiusMetres, 5.0) - SpatialMixer.DirectionRadiusMetres;

            return SpatialMixer.DirectionRadiusMetres + (0.35 * beyond);
        }
    }

    /// <summary>The square viewport inside the element, and where it starts.</summary>
    private (double Size, double OffsetX, double OffsetY) Viewport()
    {
        double size = Math.Max(1.0, Math.Min(ActualWidth, ActualHeight));

        return (size, (ActualWidth - size) / 2.0, (ActualHeight - size) / 2.0);
    }

    /// <summary>
    /// Whether a point in the element is over the sphere rather than the empty space around it.
    /// </summary>
    /// <remarks>
    /// Measured from the projected outline instead of from the ray test, because the outline is already drawn
    /// and the two must agree exactly: a click one pixel inside the drawn edge must place rather than turn.
    /// </remarks>
    private bool IsOnTheSphere(Point point)
    {
        (double size, double offsetX, double offsetY) = Viewport();

        if (size < 40.0)
        {
            return false;
        }

        double centreX = offsetX + (size / 2.0);
        double centreY = offsetY + (size / 2.0);
        double radius = 0.0;

        foreach (DevicePosition direction in SpatialProjection.Silhouette(View, 32))
        {
            (double x, double y, _) = SpatialProjection.Project(direction, View, size);

            double dx = offsetX + x - centreX;
            double dy = offsetY + y - centreY;

            radius = Math.Max(radius, Math.Sqrt((dx * dx) + (dy * dy)));
        }

        double fromCentreX = point.X - centreX;
        double fromCentreY = point.Y - centreY;

        return Math.Sqrt((fromCentreX * fromCentreX) + (fromCentreY * fromCentreY)) <= radius;
    }

    /// <summary>Turns a click in the element into the direction it points at.</summary>
    private void Pick(Point point)
    {
        (double size, double offsetX, double offsetY) = Viewport();

        DevicePosition direction = SpatialProjection.Unproject(
            point.X - offsetX,
            point.Y - offsetY,
            View,
            size);

        DirectionPicked?.Invoke(this, direction);
    }

    private void DrawRing(DrawingContext dc, double elevationDegrees, Pen pen)
    {
        dc.DrawGeometry(null, pen, Close(SpatialProjection.ElevationRing(elevationDegrees)));
    }

    private void DrawAxis(DrawingContext dc, double size)
    {
        (double topX, double topY, _) = SpatialProjection.Project(new DevicePosition(0.0, 0.0, 1.0), View, size);
        (double bottomX, double bottomY, _) = SpatialProjection.Project(new DevicePosition(0.0, 0.0, -1.0), View, size);

        dc.DrawLine(AxisPen, new Point(topX, topY), new Point(bottomX, bottomY));
    }

    private void DrawPresetDots(DrawingContext dc, double size)
    {
        // The eight directions a plan can name, plus straight up and straight down. The tilted rings are left
        // as lines: their sixteen dots would be more clutter than information, and the preset buttons below
        // the view reach them exactly.
        foreach (DevicePosition direction in SpatialProjection.ElevationRing(0.0, SpatialMixer.DirectionCount))
        {
            DrawDot(dc, direction.AtDistance(SpatialMixer.DirectionRadiusMetres), size, 3.0);
        }

        DrawDot(dc, new DevicePosition(0.0, 0.0, 1.0), size, 3.0);
        DrawDot(dc, new DevicePosition(0.0, 0.0, -1.0), size, 3.0);
    }

    private void DrawDot(DrawingContext dc, DevicePosition point, double size, double radius)
    {
        (double x, double y, bool visible) = SpatialProjection.ProjectPoint(point, View, size);

        // Hollow when the near surface hides it, so an unreachable direction looks unreachable.
        dc.DrawEllipse(
            visible ? VisibleDot : HollowFill,
            visible ? null : DotPen,
            new Point(x, y),
            radius,
            radius);
    }

    private void DrawMarker(DrawingContext dc, double size)
    {
        DevicePosition direction = Direction;

        if (direction.IsOrigin)
        {
            direction = new DevicePosition(0.0, SpatialMixer.DirectionRadiusMetres, 0.0);
        }

        double radius = UseDistance ? Frame : SpatialMixer.DirectionRadiusMetres;
        DevicePosition marker = direction.AtDistance(radius);

        if (UseDistance)
        {
            // The ring the device sits on, so "two metres away" is a shape and not only a number.
            DevicePosition[] horizon = SpatialProjection.ElevationRing(0.0, 64);
            var ring = new DevicePosition[horizon.Length];

            for (int i = 0; i < horizon.Length; i++)
            {
                ring[i] = horizon[i].AtDistance(radius);
            }

            dc.DrawGeometry(null, DistancePen, Close(ring));
        }

        (double x, double y, bool behind) = SpatialProjection.ProjectPoint(marker, View, size);

        // A device nearer than the reference sphere is INSIDE the drawn globe, so the globe's near surface
        // "hides" it by construction. Drawing it hollow would label a position the user just set as
        // unreachable, so only a marker genuinely behind the sphere is hollowed out.
        bool solid = !behind || marker.Distance <= SpatialMixer.DirectionRadiusMetres;

        (double cx, double cy, _) = SpatialProjection.ProjectPoint(default, View, size);

        var centre = new Point(cx, cy);
        var markerPoint = new Point(x, y);

        dc.DrawLine(AccentPen, centre, markerPoint);

        // The listener is the origin everything else is measured from, so it gets its own mark: a dark dot in
        // a white ring, which also keeps the centre readable under the marker's own line.
        dc.DrawEllipse(Halo, null, centre, 6.0, 6.0);
        dc.DrawEllipse(ListenerFill, null, centre, 3.5, 3.5);

        // Naming the dot is what turns it from a mark into a viewpoint.
        DrawLabel(dc, ListenerText, centre.X, centre.Y + 14.0);

        double markerRadius = solid ? 6.0 : 5.0;

        dc.DrawEllipse(Halo, null, markerPoint, markerRadius + 1.5, markerRadius + 1.5);
        dc.DrawEllipse(solid ? Accent : HollowFill, solid ? null : AccentPen, markerPoint, markerRadius, markerRadius);
    }

    /// <summary>Builds a closed shape through a ring of directions.</summary>
    private StreamGeometry Close(DevicePosition[] ring)
    {
        (double size, _, _) = Viewport();
        var geometry = new StreamGeometry();

        using (StreamGeometryContext context = geometry.Open())
        {
            for (int i = 0; i < ring.Length; i++)
            {
                (double x, double y, _) = SpatialProjection.Project(ring[i], View, size);

                if (i == 0)
                {
                    context.BeginFigure(new Point(x, y), isFilled: true, isClosed: true);
                }
                else
                {
                    context.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: false);
                }
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
