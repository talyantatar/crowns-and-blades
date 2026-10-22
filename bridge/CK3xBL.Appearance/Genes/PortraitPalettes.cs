// CK3 colour genes are UVs into gfx/portraits/{skin,hair,eye}_palette.dds (uncompressed BGRA, row 0 at the top).
// The pixel is at (x/255*(w-1), y/255*(h-1)). In CK3's shader the palette colour
// multiplies the diffuse texture, so the RGB is a tint used to rank against Bannerlord's gradients, not a final colour.
using CK3xBL.Appearance.Game;
using CK3xBL.Appearance.Imaging;

namespace CK3xBL.Appearance.Genes;

internal readonly record struct Rgb(byte R, byte G, byte B)
{
    public string Hex => $"#{R:x2}{G:x2}{B:x2}";

    public static Rgb FromDoubles(double r, double g, double b) =>
        new(ToByte(r), ToByte(g), ToByte(b));

    private static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round(v * 255, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>"Redmean" perceptual distance (0..~765), as used for the banner palette tables.</summary>
    public static double Distance(Rgb a, Rgb b)
    {
        double rm = (a.R + b.R) / 2.0;
        int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt((2 + rm / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rm) / 256) * db * db);
    }

    public (double H, double S, double V) ToHsv()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = (g - b) / d % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
            if (h < 0) h += 1;
        }
        return (h, max > 0 ? d / max : 0, max);
    }

    public static Rgb FromHsv(double h, double s, double v)
    {
        h = (h % 1 + 1) % 1;
        int i = (int)Math.Floor(h * 6);
        double f = h * 6 - i, p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        var (r, g, b) = (i % 6) switch
        {
            0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q),
        };
        return FromDoubles(r, g, b);
    }
}

internal sealed class PortraitPalettes
{
    private readonly RgbaImage? _skin, _hair, _eye;

    private PortraitPalettes(RgbaImage? skin, RgbaImage? hair, RgbaImage? eye) { _skin = skin; _hair = hair; _eye = eye; }

    public bool Complete => _skin != null && _hair != null && _eye != null;

    public static PortraitPalettes Load(GameFiles files, Action<string> log)
    {
        RgbaImage? Read(string name)
        {
            string? path = files.FindFile(Path.Combine("gfx", "portraits", name));
            if (path == null) { log($"palette {name} not found"); return null; }
            try { return DdsImage.Load(path).Decode(0); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                log($"palette {name} unreadable: {e.Message}");
                return null;
            }
        }
        return new PortraitPalettes(Read("skin_palette.dds"), Read("hair_palette.dds"), Read("eye_palette.dds"));
    }

    public Rgb? Skin(int x, int y) => Sample(_skin, x, y);
    public Rgb? Hair(int x, int y) => Sample(_hair, x, y);
    public Rgb? Eye(int x, int y) => Sample(_eye, x, y);

    private static Rgb? Sample(RgbaImage? img, int gx, int gy)
    {
        if (img == null) return null;
        int x = Math.Min(img.Width - 1, (int)Math.Round(gx / 255.0 * (img.Width - 1), MidpointRounding.AwayFromZero));
        int y = Math.Min(img.Height - 1, (int)Math.Round(gy / 255.0 * (img.Height - 1), MidpointRounding.AwayFromZero));
        int o = (y * img.Width + x) * 4;
        return new Rgb(img.Pixels[o], img.Pixels[o + 1], img.Pixels[o + 2]);
    }
}
