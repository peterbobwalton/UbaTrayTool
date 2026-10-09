using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using UbaTrayTool.Trace;

namespace UbaTrayTool;

/// <summary>
/// Tray icon: a dark tile with three timeline lanes (UbaVisualizer's look), a progress strip
/// along the bottom while a build runs, and a badge for the result.
/// </summary>
internal static class IconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static readonly Color Blue = Color.FromArgb(64, 156, 255);
    public static readonly Color Green = Color.FromArgb(40, 190, 90);
    public static readonly Color Red = Color.FromArgb(230, 60, 50);
    public static readonly Color Amber = Color.FromArgb(240, 180, 30);

    public static Icon CreateIcon(int size, BuildState state, double? fraction)
    {
        using Bitmap bmp = Render(size, state, fraction);
        IntPtr handle = bmp.GetHicon();
        try
        {
            using Icon temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>All coordinates are authored on a 32x32 grid and scaled.</summary>
    public static Bitmap Render(int size, BuildState state, double? fraction)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        g.ScaleTransform(size / 32f, size / 32f);

        bool active = state == BuildState.Running;

        // Tile
        var tile = new RectangleF(1f, 1f, 30f, 30f);
        using (GraphicsPath tilePath = RoundedRect(tile, 5f))
        using (var tileBrush = new LinearGradientBrush(tile, Color.FromArgb(70, 76, 90), Color.FromArgb(28, 31, 38), 70f))
        using (var tilePen = new Pen(Color.FromArgb(200, 205, 215), 1.2f))
        {
            g.FillPath(tileBrush, tilePath);
            g.DrawPath(tilePen, tilePath);
        }

        // Timeline lanes: offset bars like processes on cores.
        Color[] lane = active
            ? [Color.FromArgb(90, 200, 255), Color.FromArgb(120, 230, 140), Color.FromArgb(255, 200, 80)]
            : state == BuildState.Idle
                ? [Color.FromArgb(150, 155, 165), Color.FromArgb(125, 130, 140), Color.FromArgb(105, 110, 120)]
                : [Color.FromArgb(150, 205, 240), Color.FromArgb(150, 220, 160), Color.FromArgb(230, 205, 140)];
        (float x, float w)[][] bars =
        [
            [(5f, 9f), (16f, 11f)],
            [(5f, 14f), (21f, 6f)],
            [(5f, 5f), (12f, 10f)],
        ];
        for (int row = 0; row < 3; row++)
        {
            float y = 6f + row * 6.5f;
            using var brush = new SolidBrush(lane[row]);
            foreach ((float x, float w) in bars[row])
            {
                using GraphicsPath p = RoundedRect(new RectangleF(x, y, w, 4.2f), 1.2f);
                g.FillPath(brush, p);
            }
        }

        // Progress strip while running.
        if (active)
        {
            var track = new RectangleF(4f, 25.5f, 24f, 3.5f);
            using (GraphicsPath trackPath = RoundedRect(track, 1.5f))
            using (var trackBrush = new SolidBrush(Color.FromArgb(70, 74, 86)))
                g.FillPath(trackBrush, trackPath);
            float f = (float)Math.Clamp(fraction ?? 0.08, 0.08, 1);
            using GraphicsPath fill = RoundedRect(new RectangleF(track.X, track.Y, track.Width * f, track.Height), 1.5f);
            using var fillBrush = new SolidBrush(Blue);
            g.FillPath(fillBrush, fill);
        }

        // Result badge, bottom right.
        Color? badge = state switch
        {
            BuildState.Succeeded => Green,
            BuildState.Failed => Red,
            BuildState.Stopped => Amber,
            BuildState.Unreadable => Amber,
            _ => null,
        };
        if (badge is { } colour)
        {
            var circle = new RectangleF(16f, 16f, 15.5f, 15.5f);
            using (var brush = new SolidBrush(colour))
            using (var pen = new Pen(Color.White, 1.6f))
            {
                g.FillEllipse(brush, circle);
                g.DrawEllipse(pen, circle);
            }
            using var mark = new Pen(Color.White, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            if (state == BuildState.Succeeded)
            {
                g.DrawLines(mark, [new PointF(19.8f, 24.2f), new PointF(22.6f, 27f), new PointF(27.6f, 21f)]);
            }
            else
            {
                g.DrawLine(mark, 23.75f, 19.8f, 23.75f, 24.6f);
                using var dot = new SolidBrush(Color.White);
                g.FillEllipse(dot, 22.4f, 26.3f, 2.7f, 2.7f);
            }
        }

        return bmp;
    }

    /// <summary>Writes a multi-resolution .ico (PNG-compressed entries).</summary>
    public static void SaveIco(string path)
    {
        int[] sizes = [16, 20, 24, 32, 48, 64, 256];
        var images = new List<byte[]>();
        foreach (int size in sizes)
        {
            using Bitmap bmp = Render(size, BuildState.Running, 0.6);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            images.Add(ms.ToArray());
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }
        foreach (byte[] image in images)
            w.Write(image);
    }

    internal static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
