// Renders a CK3 coat of arms to a PNG for the transition title cards (PROTOCOL §7.3), reproducing the jomini shaders:
//   pattern  out = fallback; lerp(out, c1, tex.r); lerp(out, c2, tex.g); lerp(out, c3, tex.b)   (coat_of_arms_pattern.fxh:64-70)
//   emblem   rgb = c1; lerp(rgb, c2, tex.g); lerp(rgb, c3, tex.r); rgb = Overlay(tex.b, rgb); alpha = tex.a,
//            optionally clipped to pattern channels (mask)                                  (coat_of_arms_textured_emblem.fxh:122-142)
//   instance quad [-1,1]^2 rotated by the instance angle, scaled by |scale|, centred at position; negative scale mirrors
//            (vertex shader of the same file). Instances draw in file order, deeper ones (higher depth) first.
// The result is clipped to CK3's own title shield shape (gfx/interface/coat_of_arms/title_mask.dds) when available.
// Textures are read from the user's install at runtime; nothing is copied into the repo.
using CK3xBL.Appearance.Imaging;

namespace CK3xBL.Appearance.Coa;

/// <summary>Maps canvas coordinates (0..1, y down) to an emblem instance's texture coordinates and back.</summary>
internal readonly struct EmblemTransform
{
    private readonly double _ox, _oy, _hw, _hh, _cos, _sin;
    private readonly bool _mirrorX, _mirrorY;

    public EmblemTransform(CoaInstance i)
    {
        _ox = i.X; _oy = i.Y;
        _hw = Math.Abs(i.ScaleX) / 2; _hh = Math.Abs(i.ScaleY) / 2;
        double a = i.Rotation * Math.PI / 180;
        _cos = Math.Cos(a); _sin = Math.Sin(a);
        _mirrorX = i.ScaleX < 0; _mirrorY = i.ScaleY < 0;
    }

    public bool Degenerate => _hw < 1e-6 || _hh < 1e-6;

    /// <summary>Canvas point -> texture uv; false when the point is outside the instance quad.</summary>
    public bool ToUv(double x, double y, out double u, out double v)
    {
        double rx = (x - _ox) / _hw, ry = -(y - _oy) / _hh;
        double cx = _cos * rx + _sin * ry, cy = -_sin * rx + _cos * ry;
        u = (cx + 1) / 2; v = (1 - cy) / 2;
        if (_mirrorX) u = 1 - u;
        if (_mirrorY) v = 1 - v;
        return cx is >= -1 and <= 1 && cy is >= -1 and <= 1;
    }

    /// <summary>Axis-aligned bounds of the rotated quad on the canvas.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) Bounds()
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (cx, cy) in new[] { (-1.0, -1.0), (1.0, -1.0), (-1.0, 1.0), (1.0, 1.0) })
        {
            double rx = _cos * cx - _sin * cy, ry = _sin * cx + _cos * cy;
            double x = _ox + _hw * rx, y = _oy - _hh * ry;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        return (minX, minY, maxX, maxY);
    }
}

internal sealed class CoaRenderer
{
    private static readonly CoaColor Grey = new() { R = 128, G = 128, B = 128 };
    private readonly CoaTextures _tex;

    public CoaRenderer(CoaTextures textures) { _tex = textures; }

    /// <summary>Renders to &lt;dir&gt;/&lt;content hash&gt;.png (reused when it already exists) and returns the path.</summary>
    public string RenderToFile(CoaModel coa, string dir, int size = 256, bool shieldMask = true)
    {
        string path = Path.Combine(dir, $"{coa.ContentHash()}_{size}{(shieldMask ? "s" : "q")}.png");
        if (File.Exists(path)) return path;
        PngWriter.Save(Render(coa, size, shieldMask), path);
        return path;
    }

    public RgbaImage Render(CoaModel coa, int size, bool shieldMask)
    {
        var canvas = new float[size * size * 4];
        Draw(coa, canvas, size, 0);
        var img = new RgbaImage(size, size);
        var mask = shieldMask ? _tex.TitleMask() : null;
        Span<float> m = stackalloc float[4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int i = (y * size + x) * 4;
            float a = canvas[i + 3];
            if (mask != null)
            {
                mask.SampleBilinear((x + 0.5) / size, (y + 0.5) / size, m, wrap: false);
                a *= m[3];
            }
            img.Pixels[i] = ToByte(canvas[i]);
            img.Pixels[i + 1] = ToByte(canvas[i + 1]);
            img.Pixels[i + 2] = ToByte(canvas[i + 2]);
            img.Pixels[i + 3] = ToByte(a);
        }
        return img;
    }

    private void Draw(CoaModel coa, float[] canvas, int size, int depth)
    {
        var c1 = coa.Colors[0] ?? Grey;
        var c2 = coa.Colors[1] ?? c1;
        var c3 = coa.Colors[2] ?? c1;
        var pattern = _tex.Pattern(coa.Pattern, size) ?? _tex.Pattern("pattern_solid", size);
        Span<float> t = stackalloc float[4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float r = c1.R / 255f, g = c1.G / 255f, b = c1.B / 255f;   // fallback = color1
            if (pattern != null)
            {
                pattern.SampleBilinear((x + 0.5) / size, (y + 0.5) / size, t);
                Lerp(ref r, ref g, ref b, c1, t[0]);
                Lerp(ref r, ref g, ref b, c2, t[1]);
                Lerp(ref r, ref g, ref b, c3, t[2]);
            }
            int i = (y * size + x) * 4;
            canvas[i] = r; canvas[i + 1] = g; canvas[i + 2] = b; canvas[i + 3] = 1;
        }

        if (depth < 3)
            foreach (var sub in coa.Subs)
                foreach (var inst in sub.Instances)
                    DrawSub(sub.Coa, inst, canvas, size, depth);

        var order = new List<(CoaEmblem E, CoaInstance I, int N)>();
        foreach (var e in coa.Emblems)
            foreach (var inst in e.Instances)
                order.Add((e, inst, order.Count));
        order.Sort((a, b) => a.I.Depth != b.I.Depth ? b.I.Depth.CompareTo(a.I.Depth) : a.N.CompareTo(b.N));
        foreach (var (e, inst, _) in order) DrawEmblem(e, inst, coa, pattern, canvas, size);
    }

    private void DrawSub(CoaModel sub, CoaInstance inst, float[] canvas, int size, int depth)
    {
        int w = (int)Math.Round(Math.Abs(inst.ScaleX) * size), h = (int)Math.Round(Math.Abs(inst.ScaleY) * size);
        if (w < 1 || h < 1) return;
        int subSize = Math.Max(w, h);
        var subCanvas = new float[subSize * subSize * 4];
        Draw(sub, subCanvas, subSize, depth + 1);
        int x0 = (int)Math.Round(inst.X * size), y0 = (int)Math.Round(inst.Y * size);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int cx = x0 + x, cy = y0 + y;
            if (cx < 0 || cy < 0 || cx >= size || cy >= size) continue;
            int sx = x * subSize / w, sy = y * subSize / h;
            int si = (sy * subSize + sx) * 4, di = (cy * size + cx) * 4;
            canvas[di] = subCanvas[si]; canvas[di + 1] = subCanvas[si + 1]; canvas[di + 2] = subCanvas[si + 2];
        }
    }

    private void DrawEmblem(CoaEmblem e, CoaInstance inst, CoaModel coa, RgbaImage? pattern, float[] canvas, int size)
    {
        var xf = new EmblemTransform(inst);
        if (xf.Degenerate) return;
        var tex = _tex.Emblem(e.Texture, e.Textured, Math.Max(Math.Abs(inst.ScaleX), Math.Abs(inst.ScaleY)) * size);
        if (tex == null) return;
        var c1 = e.Colors[0] ?? coa.Colors[1] ?? coa.Colors[0] ?? Grey;
        var c2 = e.Colors[1] ?? c1;
        var c3 = e.Colors[2] ?? c1;
        bool m1 = false, m2 = false, m3 = false;
        if (e.Mask != null)
            foreach (int ch in e.Mask) { m1 |= ch == 1; m2 |= ch == 2; m3 |= ch == 3; }
        bool masked = e.Mask != null && (m1 || m2 || m3) && pattern != null;

        var (minX, minY, maxX, maxY) = xf.Bounds();
        int px0 = Math.Max(0, (int)Math.Floor(minX * size)), px1 = Math.Min(size - 1, (int)Math.Ceiling(maxX * size));
        int py0 = Math.Max(0, (int)Math.Floor(minY * size)), py1 = Math.Min(size - 1, (int)Math.Ceiling(maxY * size));
        Span<float> t = stackalloc float[4];
        Span<float> p = stackalloc float[4];
        for (int y = py0; y <= py1; y++)
        for (int x = px0; x <= px1; x++)
        {
            double cx = (x + 0.5) / size, cy = (y + 0.5) / size;
            if (!xf.ToUv(cx, cy, out double u, out double v)) continue;
            tex.SampleBilinear(u, v, t);
            float alpha = t[3];
            if (alpha <= 0.002f) continue;
            if (masked)
            {
                pattern!.SampleBilinear(cx, cy, p);
                float pr = Math.Clamp(p[0] - p[1] - p[2], 0, 1), pg = Math.Clamp(p[1] - p[2], 0, 1), pb = p[2];
                alpha *= Math.Clamp((m1 ? pr : 0) + (m2 ? pg : 0) + (m3 ? pb : 0), 0, 1);
                if (alpha <= 0.002f) continue;
            }
            float r, g, b;
            if (e.Textured) { r = t[0]; g = t[1]; b = t[2]; }
            else
            {
                r = c1.R / 255f; g = c1.G / 255f; b = c1.B / 255f;
                Lerp(ref r, ref g, ref b, c2, t[1]);
                Lerp(ref r, ref g, ref b, c3, t[0]);
                r = Overlay(t[2], r); g = Overlay(t[2], g); b = Overlay(t[2], b);
            }
            int i = (y * size + x) * 4;
            canvas[i] += (r - canvas[i]) * alpha;
            canvas[i + 1] += (g - canvas[i + 1]) * alpha;
            canvas[i + 2] += (b - canvas[i + 2]) * alpha;
        }
    }

    /// <summary>Overlay blend with the texture's blue channel as base (cw/utility.fxh GetOverlay flips base and blend).</summary>
    private static float Overlay(float bse, float blend) =>
        bse < 0.5f ? 2 * bse * blend : 1 - 2 * (1 - bse) * (1 - blend);

    private static void Lerp(ref float r, ref float g, ref float b, CoaColor c, float t)
    {
        r += (c.R / 255f - r) * t;
        g += (c.G / 255f - g) * t;
        b += (c.B / 255f - b) * t;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
}
