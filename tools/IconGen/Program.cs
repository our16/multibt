// MultiBT icon generator.
//
// Draws the application icon as VECTOR art at several sizes and writes a multi-size .ico, plus PNG
// previews for review. Rendering each size from geometry (instead of downscaling one bitmap) is what
// keeps the small sizes crisp, and it lets the design adapt: at 16 px a two-arc mark turns to mush,
// so the small variants deliberately use a simpler, bolder glyph.
//
// Usage:
//   dotnet run --project tools/IconGen            # writes the ico + previews
//
// Outputs:
//   src/MultiBT.App/Assets/multibt.ico            # multi-size, DIB entries + PNG for 256
//   assets/icon/preview-<size>.png                # for visual review
//
// Design: a rounded "app tile" in the UI accent colour, carrying a white glyph of one signal
// glyph with radiating arcs. Minimal, high contrast on both light and dark taskbars, and legible at
// 16 px.

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MultiBT.IconGen;

internal static class Program
{
    /// <summary>Sizes embedded in the .ico. 16/20/24/32 cover taskbar and DPI scaling; 256 is the shell.</summary>
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    /// <summary>Brand gradient: indigo to cyan, top-left to bottom-right.</summary>
    /// <summary>The application's accent colour, so the icon and the window agree.</summary>
    /// <remarks>
    /// Flat rather than a gradient on purpose: at 16 px a colour gradient across a 16-pixel tile is noise,
    /// not depth. Depth comes from the single top highlight below, which is only drawn where it can be seen.
    /// </remarks>
    private static readonly Color Accent = Color.FromRgb(0x25, 0x63, 0xEB);

    [STAThread]
    private static int Main(string[] args)
    {
        string repositoryRoot = FindRepositoryRoot();

        string icoPath = Path.Combine(repositoryRoot, "src", "MultiBT.App", "Assets", "multibt.ico");
        string previewDirectory = Path.Combine(repositoryRoot, "assets", "icon");

        Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);
        Directory.CreateDirectory(previewDirectory);

        var images = new List<(int Size, byte[] Png, byte[] Bgra)>();

        foreach (int size in Sizes)
        {
            byte[] bgra = RenderBgra(size);
            byte[] png = RenderPng(size);

            images.Add((size, png, bgra));

            // Previews at the sizes a human can actually judge.
            if (size is 16 or 32 or 64 or 256)
            {
                File.WriteAllBytes(Path.Combine(previewDirectory, $"preview-{size}.png"), png);
            }
        }

        WriteIco(icoPath, images);

        Console.WriteLine($"icon  : {icoPath} ({new FileInfo(icoPath).Length} bytes, {Sizes.Length} sizes)");
        Console.WriteLine($"sizes : {string.Join(", ", Sizes)}");
        Console.WriteLine($"assets: {previewDirectory}");

        // Structural checks. This tool cannot judge aesthetics, but it CAN prove the icon is not
        // blank, not fully transparent, actually carries the glyph at the sizes that matter, and
        // round-trips as a valid multi-size .ico.
        Console.WriteLine();
        VerifyIco(icoPath, Sizes.Length);

        Console.WriteLine();
        Console.WriteLine("ASCII preview (glyph = #, tile = ., transparent = space)");
        foreach (int size in new[] { 16, 32, 64 })
        {
            Console.WriteLine();
            Console.WriteLine($"--- {size} x {size} ---");
            PrintAscii(RenderBgra2(size), size);
        }

        return 0;
    }

    /// <summary>Re-reads the .ico and checks its structure.</summary>
    private static void VerifyIco(string path, int expectedCount)
    {
        byte[] bytes = File.ReadAllBytes(path);

        int count = BitConverter.ToUInt16(bytes, 4);

        if (count != expectedCount)
        {
            throw new InvalidOperationException($"ICO declares {count} images, expected {expectedCount}.");
        }

        var sizes = new List<int>();

        for (int i = 0; i < count; i++)
        {
            int entry = 6 + (i * 16);

            int width = bytes[entry] == 0 ? 256 : bytes[entry];
            int height = bytes[entry + 1] == 0 ? 256 : bytes[entry + 1];
            int length = BitConverter.ToInt32(bytes, entry + 8);
            int offset = BitConverter.ToInt32(bytes, entry + 12);

            if (width != height)
            {
                throw new InvalidOperationException($"ICO entry {i} is not square ({width}x{height}).");
            }

            if (offset + length > bytes.Length)
            {
                throw new InvalidOperationException($"ICO entry {i} runs past the end of the file.");
            }

            sizes.Add(width);
        }

        Console.WriteLine($"verify: valid ICO, {count} square entries, sizes {string.Join(", ", sizes)}");
    }

    /// <summary>Prints the icon as text so its shape can be reviewed without an image viewer.</summary>
    private static void PrintAscii(byte[] bgra, int size)
    {
        int stride = size * 4;

        for (int y = 0; y < size; y++)
        {
            var line = new System.Text.StringBuilder(size);

            for (int x = 0; x < size; x++)
            {
                int i = (y * stride) + (x * 4);
                byte b = bgra[i];
                byte g = bgra[i + 1];
                byte r = bgra[i + 2];
                byte a = bgra[i + 3];

                line.Append(a < 40 ? ' '
                    : r > 200 && g > 200 && b > 200 ? '#'   // glyph
                    : '.');
            }

            Console.WriteLine(line.ToString());
        }
    }

    private static byte[] RenderBgra2(int size) => RenderBgra(size);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MultiBT.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("Could not locate the repository root (MultiBT.slnx).");
    }

    // ---------------------------------------------------------------------------------------
    // Drawing
    // ---------------------------------------------------------------------------------------

    /// <summary>Draws the icon at the given size into a drawing context.</summary>
    private static void Draw(DrawingContext dc, double size)
    {
        // ---- tile -----------------------------------------------------------------------
        // Inset slightly so the rounded corners are not clipped, and so the tile reads as a
        // deliberate shape rather than a full-bleed square.
        double inset = size * 0.045;
        double tileSize = size - (inset * 2);
        double cornerRadius = tileSize * 0.24;

        var tile = new RectangleGeometry(
            new Rect(inset, inset, tileSize, tileSize),
            cornerRadius,
            cornerRadius);

        var tileBrush = new SolidColorBrush(Accent);
        tileBrush.Freeze();

        dc.DrawGeometry(tileBrush, null, tile);

        // A subtle top highlight gives the tile depth without gradient noise at 16 px.
        if (size >= 48)
        {
            var highlight = new LinearGradientBrush(
                Color.FromArgb(48, 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255),
                new Point(0, 0),
                new Point(0, 1));

            dc.DrawGeometry(
                highlight,
                null,
                new RectangleGeometry(new Rect(inset, inset, tileSize, tileSize * 0.5), cornerRadius, cornerRadius));
        }

        // ---- glyph ----------------------------------------------------------------------
        // One signal forking into three: a stem entering from the left, splitting into three bars.
        //
        // Small sizes get a reduced form -- three bars, no stem or fork -- for the same reason the old mark
        // had a compact variant: the fork is the detail that disappears first when the glyph is only 16
        // pixels wide, and half a fork reads as a smudge rather than as a shape.
        bool compact = size < 28;

        double glyphScale = size * (compact ? 0.76 : 0.62) / 100.0;
        double glyphX = (size - (100 * glyphScale)) / 2.0;
        double glyphY = (size - (100 * glyphScale)) / 2.0;

        Point Map(double x, double y) => new(glyphX + (x * glyphScale), glyphY + (y * glyphScale));

        var white = new SolidColorBrush(Colors.White);
        white.Freeze();

        // Stroke width in DEVICE pixels, not glyph units.
        //
        // Dividing a device-pixel target by glyphScale and handing the result to Pen is a unit error: Pen
        // takes device pixels, so the stroke came out about eight times too thick at 32 px and the three bars
        // merged into one blob. The ASCII preview showed it immediately.
        // The floor is deliberately generous: at 16 px a hairline stroke antialiases away to nothing, and a
        // bar that vanishes leaves the compact mark reading as two outputs instead of three.
        double stroke = Math.Max(size < 28 ? 1.9 : 1.45, size * 0.075);

        var pen = new Pen(white, stroke)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        pen.Freeze();

        if (compact)
        {
            // Three bars: still "many outputs", with nothing that can merge at this size.
            foreach (double y in new[] { 24.0, 50.0, 76.0 })
            {
                dc.DrawGeometry(null, pen, Line(Map(17, y), Map(83, y)));
            }

            return;
        }

        // Stem, then the fork. Drawn as three separate stroked paths from one point so the join stays clean
        // and the three branches cannot accidentally cross each other.
        dc.DrawGeometry(null, pen, Line(Map(8, 50), Map(37, 50)));

        dc.DrawGeometry(null, pen, Polyline(Map, (37, 50), (58, 24), (92, 24)));
        dc.DrawGeometry(null, pen, Line(Map(37, 50), Map(92, 50)));
        dc.DrawGeometry(null, pen, Polyline(Map, (37, 50), (58, 76), (92, 76)));
    }

    /// <summary>A straight segment between two mapped points.</summary>
    private static Geometry Line(Point from, Point to)
    {
        var geometry = new StreamGeometry();

        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(from, isFilled: false, isClosed: false);
            ctx.LineTo(to, isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>A polyline through the given glyph-space points.</summary>
    private static Geometry Polyline(Func<double, double, Point> map, params (double X, double Y)[] points)
    {
        var geometry = new StreamGeometry();

        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(map(points[0].X, points[0].Y), isFilled: false, isClosed: false);

            for (int i = 1; i < points.Length; i++)
            {
                ctx.LineTo(map(points[i].X, points[i].Y), isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        return geometry;
    }
    /// <summary>Renders the icon to premultiplied BGRA pixels, top-down.</summary>
    private static byte[] RenderBgra(int size)
    {
        BitmapSource bitmap = Render(size);

        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int stride = size * 4;
        var pixels = new byte[stride * size];

        converted.CopyPixels(pixels, stride, 0);

        return pixels;
    }

    private static byte[] RenderPng(int size)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(size)));

        using var stream = new MemoryStream();
        encoder.Save(stream);

        return stream.ToArray();
    }

    private static BitmapSource Render(int size)
    {
        var visual = new DrawingVisual();

        using (DrawingContext dc = visual.RenderOpen())
        {
            Draw(dc, size);
        }

        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();

        return target;
    }

    // ---------------------------------------------------------------------------------------
    // .ico assembly
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Writes a multi-size .ico.
    /// </summary>
    /// <remarks>
    /// Entries below 256 are written as uncompressed 32-bit DIBs (BITMAPINFOHEADER + BGRA + AND mask)
    /// rather than PNG. PNG-compressed entries are only guaranteed for the 256 px image; the small
    /// sizes are the ones the shell, the taskbar and the tray actually load, and a DIB is what every
    /// Windows version reads reliably. The 256 px entry is PNG to keep the file small.
    /// </remarks>
    private static void WriteIco(string path, List<(int Size, byte[] Png, byte[] Bgra)> images)
    {
        var entries = new List<(int Size, byte[] Data)>();

        foreach ((int size, byte[] png, byte[] bgra) in images)
        {
            entries.Add((size, size >= 256 ? png : BuildDib(size, bgra)));
        }

        using FileStream stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        // ICONDIR
        writer.Write((ushort)0);              // reserved
        writer.Write((ushort)1);              // type: icon
        writer.Write((ushort)entries.Count);

        // ICONDIRENTRY table, then the image data. Offsets are absolute from the start of the file.
        int offset = 6 + (entries.Count * 16);

        foreach ((int size, byte[] data) in entries)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));   // width  (0 means 256)
            writer.Write((byte)(size >= 256 ? 0 : size));   // height
            writer.Write((byte)0);                          // palette count
            writer.Write((byte)0);                          // reserved
            writer.Write((ushort)1);                        // colour planes
            writer.Write((ushort)32);                       // bits per pixel
            writer.Write(data.Length);
            writer.Write(offset);

            offset += data.Length;
        }

        foreach ((_, byte[] data) in entries)
        {
            writer.Write(data);
        }
    }

    /// <summary>Builds a 32-bit bottom-up DIB with the alpha-derived AND mask that the format requires.</summary>
    private static byte[] BuildDib(int size, byte[] bgraTopDown)
    {
        int stride = size * 4;

        // The AND mask is 1bpp with each row padded to 4 bytes. Windows uses the alpha channel on
        // 32-bit icons, but the mask must still be present and consistent or some shell paths render
        // the transparent corners as black.
        int maskStride = ((size + 31) / 32) * 4;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // BITMAPINFOHEADER. Height is doubled: XOR image followed by the AND mask.
        writer.Write(40);
        writer.Write(size);
        writer.Write(size * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);            // BI_RGB
        writer.Write(0);            // image size (0 = derived)
        writer.Write(0);            // x pixels per metre
        writer.Write(0);            // y pixels per metre
        writer.Write(0);            // palette colours
        writer.Write(0);            // important colours

        // XOR image, bottom-up.
        for (int y = size - 1; y >= 0; y--)
        {
            writer.Write(bgraTopDown, y * stride, stride);
        }

        // AND mask, bottom-up. A set bit means "transparent" at that pixel.
        var maskRow = new byte[maskStride];

        for (int y = size - 1; y >= 0; y--)
        {
            Array.Clear(maskRow);

            for (int x = 0; x < size; x++)
            {
                byte alpha = bgraTopDown[(y * stride) + (x * 4) + 3];

                if (alpha < 128)
                {
                    maskRow[x / 8] |= (byte)(0x80 >> (x % 8));
                }
            }

            writer.Write(maskRow);
        }

        return stream.ToArray();
    }
}
