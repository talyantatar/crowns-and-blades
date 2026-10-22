// The Crowns & Blades badge (a gold crown over two crossed swords on a dark red rounded square), drawn in code: the same design as
// the bridge's tray icon (TrayContext.DrawIcon). assets/CK3xBL.ico is generated from this by "CK3xBL.Setup.Tests.exe icon".
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace CK3xBL.Setup.Ui;

internal static class AppIcon
{
    public static readonly Color Field = Color.FromArgb(90, 20, 20);
    public static readonly Color Gold = Color.FromArgb(232, 184, 48);
    public static readonly Color GoldEdge = Color.FromArgb(122, 90, 16);
    public static readonly Color Steel = Color.FromArgb(217, 217, 217);
    public static readonly Color Grip = Color.FromArgb(138, 106, 42);

    /// <summary>The badge at any size (designed on a 32 px grid).</summary>
    public static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        float k = size / 32f;
        using (var field = new SolidBrush(Field))
        using (var square = RoundedSquare(0.5f * k, 31f * k, 7f * k))
            g.FillPath(field, square);
        using (var blade = new Pen(Steel, Math.Max(1.4f, 2.2f * k)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(blade, 7.5f * k, 27.5f * k, 24.5f * k, 10.5f * k);
            g.DrawLine(blade, 24.5f * k, 27.5f * k, 7.5f * k, 10.5f * k);
        }
        using (var grip = new Pen(Grip, Math.Max(1.2f, 1.8f * k)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(grip, 9.8f * k, 22.6f * k, 12.3f * k, 25.1f * k);
            g.DrawLine(grip, 22.2f * k, 22.6f * k, 19.7f * k, 25.1f * k);
        }
        var crown = new[]
        {
            new PointF(8.1f * k, 12.8f * k), new PointF(8.1f * k, 5.9f * k), new PointF(12.3f * k, 9.4f * k), new PointF(16f * k, 4.4f * k),
            new PointF(19.7f * k, 9.4f * k), new PointF(23.9f * k, 5.9f * k), new PointF(23.9f * k, 12.8f * k),
        };
        using (var gold = new SolidBrush(Gold)) g.FillPolygon(gold, crown);
        if (size >= 24)
            using (var edge = new Pen(GoldEdge, 0.5f * k) { LineJoin = LineJoin.Round })
                g.DrawPolygon(edge, crown);
        return bmp;
    }

    private static GraphicsPath RoundedSquare(float at, float side, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(at, at, d, d, 180, 90);
        path.AddArc(at + side - d, at, d, d, 270, 90);
        path.AddArc(at + side - d, at + side - d, d, d, 0, 90);
        path.AddArc(at, at + side - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>A multi-size .ico (PNG-compressed entries, Windows Vista and later).</summary>
    public static byte[] BuildIco(IEnumerable<int> sizes)
    {
        var images = new List<(int size, byte[] png)>();
        foreach (var s in sizes)
        {
            using var bmp = Draw(s);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            images.Add((s, ms.ToArray()));
        }
        using var output = new MemoryStream();
        using var w = new BinaryWriter(output);
        w.Write((ushort)0);              // reserved
        w.Write((ushort)1);              // type: icon
        w.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);            // palette
            w.Write((byte)0);            // reserved
            w.Write((ushort)1);          // planes
            w.Write((ushort)32);         // bits per pixel
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
        w.Flush();
        return output.ToArray();
    }

    /// <summary>The window icon: the exe's own icon, else the drawn one.</summary>
    public static Icon WindowIcon()
    {
        try
        {
            var exe = System.Windows.Forms.Application.ExecutablePath;
            var icon = Icon.ExtractAssociatedIcon(exe);
            if (icon is not null) return icon;
        }
        catch (Exception e) when (e is ArgumentException or IOException) { }
        using var bmp = Draw(32);
        return Icon.FromHandle(bmp.GetHicon());
    }
}
