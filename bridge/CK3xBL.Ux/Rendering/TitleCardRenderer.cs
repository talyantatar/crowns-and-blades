using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using CK3xBL.Core.Abstractions;

namespace CK3xBL.Ux.Rendering;

/// <summary>Which card is drawn: the battle title card, or the result card after a won or lost battle.</summary>
public enum CardKind { Title, Victory, Defeat }

/// <summary>Where the renderer put things (pixels in the target bitmap). Used by tests and for logging.</summary>
public sealed class CardLayout
{
    public float Scale { get; init; }
    public RectangleF TitleBounds { get; init; }
    public RectangleF TextBounds { get; init; }
    public RectangleF LeftCoaBounds { get; init; }
    public RectangleF RightCoaBounds { get; init; }
    public float TitleEmPixels { get; init; }
    public int TitleLineCount { get; init; }
}

/// <summary>
/// Draws the CK3-styled transition cards procedurally (no copied art): a dark parchment background with a vignette, an
/// optional faint blurred freeze frame of the game, a gold Fondamento title with soft shadow and glow, ornamental gold
/// rules, cream subtitle and detail lines, and optional coats of arms with soft shadows.
///
/// The card is drawn as two layers so the overlay can animate them independently: an opaque background and a transparent
/// foreground (text, ornaments, CoAs). All sizes scale with the target (reference design: 2560x1440).
/// The shared font objects are not safe for concurrent use, so text rendering is serialized; the background uses only
/// private objects and may render concurrently with text.
/// </summary>
public static class TitleCardRenderer
{
    /// <summary>Vertical position of the title's optical centre, as a fraction of the card height.</summary>
    public const float TitleCenterFraction = 0.40f;

    /// <summary>Serializes all text rendering; <see cref="CardFonts.Dispose"/> takes it too, so fonts never vanish mid-render.</summary>
    internal static readonly object TextGate = new();

    // Palette. Gold and cream follow the CK3 / Bannerlord loading-title colours (#D9B76B, #F4E1C4).
    private static readonly Color[] GoldRamp = [Color.FromArgb(252, 236, 190), Color.FromArgb(232, 200, 128), Color.FromArgb(217, 183, 107), Color.FromArgb(160, 116, 52)];
    private static readonly Color[] DefeatRamp = [Color.FromArgb(246, 214, 196), Color.FromArgb(214, 150, 122), Color.FromArgb(190, 110, 86), Color.FromArgb(124, 56, 40)];
    private static readonly float[] RampStops = [0f, 0.38f, 0.62f, 1f];
    private static readonly Color Cream = Color.FromArgb(244, 225, 196);
    private static readonly Color Muted = Color.FromArgb(205, 186, 150);
    private static readonly Color OrnamentGold = Color.FromArgb(205, 168, 96);
    private static readonly Color OrnamentRed = Color.FromArgb(196, 120, 92);

    private readonly record struct BackgroundStyle(float BaseR, float BaseG, float BaseB, float GlowR, float GlowG, float GlowB, float UnderlayMix);

    private static BackgroundStyle StyleFor(CardKind kind) => kind switch
    {
        CardKind.Title => new(62, 47, 32, 58, 38, 15, 0.17f),
        CardKind.Victory => new(46, 36, 25, 54, 36, 13, 0.17f),
        _ => new(46, 31, 26, 60, 24, 14, 0.17f),
    };

    /// <summary>Victory or defeat, from the result card's title ("Defeat at X", "Rout at X", "Retreat from X" read as defeat).</summary>
    public static CardKind KindForResult(string? title)
    {
        string t = (title ?? "").TrimStart();
        if (t.StartsWith("The ", StringComparison.OrdinalIgnoreCase)) t = t[4..];
        foreach (var word in new[] { "Defeat", "Rout", "Retreat", "Disaster" })
            if (t.StartsWith(word, StringComparison.OrdinalIgnoreCase)) return CardKind.Defeat;
        return CardKind.Victory;
    }

    // =====================================================================================================================
    // Background
    // =====================================================================================================================

    /// <summary>
    /// Fills <paramref name="target"/> (32bpp, any size) with the opaque card background. <paramref name="underlay"/> (any
    /// size, any 32bpp format) is the freeze frame; it is blurred, desaturated and mixed in faintly so the darkened game is
    /// still sensed behind the card.
    /// </summary>
    public static void RenderBackground(Bitmap target, CardKind kind, Bitmap? underlay) =>
        RenderBackgroundCore(target, StyleFor(kind), underlay);

    private static unsafe void RenderBackgroundCore(Bitmap target, BackgroundStyle st, Bitmap? underlay)
    {
        int w = target.Width, h = target.Height;
        float s = ScaleOf(w, h);

        // Parchment blotches: fractal noise on a 4 px grid, largest feature ~ a fifth of the screen height.
        const int step = 4;
        int gw = w / step + 2, gh = h / step + 2;
        float[] parchment = Procedural.FractalGrid(gw, gh, step, h / 5f, 6, seed: 1066);
        // Paper fibres: horizontally stretched noise (cheap trick: sample a second grid with a squashed x axis).
        float[] fibres = Procedural.FractalGrid(gw, gh, step, 22f * s + 6f, 3, seed: 1453);

        float[]? ulR = null, ulG = null, ulB = null;
        const int ulW = 192, ulH = 108;
        if (underlay != null) (ulR, ulG, ulB) = PrepareUnderlay(underlay, ulW, ulH);

        float cx = w * 0.5f, cy = h * 0.44f, ty = h * TitleCenterFraction;
        float invVx = 1f / (w * 0.60f), invVy = 1f / (h * 0.66f);
        float invGx = 1f / (w * 0.34f), invGy = 1f / (h * 0.12f);

        var data = target.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, target.PixelFormat);
        try
        {
            nint scan0 = data.Scan0;
            int stride = data.Stride;
            Parallel.For(0, h, y =>
            {
                byte* row = (byte*)scan0 + (long)y * stride;
                float ey = (y - cy) * invVy, gy2 = (y - ty) * invGy;
                gy2 *= gy2;
                for (int x = 0; x < w; x++)
                {
                    float ex = (x - cx) * invVx;
                    float d = MathF.Sqrt(ex * ex + ey * ey);
                    float vig = 1f - Procedural.SmoothStep(0.12f, 1.22f, d);
                    float light = 0.05f + 0.95f * MathF.Pow(vig, 1.45f);

                    float n1 = Procedural.SampleGrid(parchment, gw, gh, x / (float)step, y / (float)step);
                    float n2 = Procedural.SampleGrid(fibres, gw, gh, x / (float)step * 0.18f, y / (float)step);
                    // Texture contrast fades towards the edges so the vignette stays clean.
                    float tex = 0.76f + (0.38f * n1 + 0.16f * (n2 - 0.5f)) * (0.45f + 0.55f * vig);
                    float gx = (x - cx) * invGx;
                    float glow = MathF.Exp(-(gx * gx + gy2)) * 0.55f;

                    float r = st.BaseR * light * tex + st.GlowR * glow;
                    float g = st.BaseG * light * tex + st.GlowG * glow;
                    float b = st.BaseB * light * tex + st.GlowB * glow;

                    if (ulR != null)
                    {
                        float ux = x * (ulW - 1f) / Math.Max(1, w - 1), uy = y * (ulH - 1f) / Math.Max(1, h - 1);
                        float ur = Procedural.SampleGrid(ulR, ulW, ulH, ux, uy);
                        float ug = Procedural.SampleGrid(ulG!, ulW, ulH, ux, uy);
                        float ub = Procedural.SampleGrid(ulB!, ulW, ulH, ux, uy);
                        float mix = st.UnderlayMix * (0.35f + 0.65f * vig);
                        r += (ur - r) * mix; g += (ug - g) * mix; b += (ub - b) * mix;
                    }

                    // Film grain doubles as dither: dark gradients band visibly on 8-bit panels without it.
                    float grain = (Procedural.Hash01(x, y, 7) - 0.5f) * 3.2f;
                    row[x * 4 + 0] = ToByte(b + grain);
                    row[x * 4 + 1] = ToByte(g + grain);
                    row[x * 4 + 2] = ToByte(r + grain);
                    row[x * 4 + 3] = 255;
                }
            });
        }
        finally { target.UnlockBits(data); }
    }

    /// <summary>Downsamples, blurs, desaturates and darkens the freeze frame into small float planes for bilinear sampling.</summary>
    private static unsafe (float[] r, float[] g, float[] b) PrepareUnderlay(Bitmap source, int w, int h)
    {
        using var small = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(source, new Rectangle(0, 0, w, h));
        }
        Procedural.BoxBlur(small, 2, 3);
        var r = new float[w * h]; var gr = new float[w * h]; var b = new float[w * h];
        var data = small.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
            {
                byte* row = (byte*)data.Scan0 + (long)y * data.Stride;
                for (int x = 0; x < w; x++)
                {
                    float pb = row[x * 4], pg = row[x * 4 + 1], pr = row[x * 4 + 2];
                    float lum = 0.299f * pr + 0.587f * pg + 0.114f * pb;
                    // 45% saturation, warm sepia tint, half brightness: a memory of the map, not the map itself.
                    int i = y * w + x;
                    r[i] = (lum + (pr - lum) * 0.45f) * 0.52f;
                    gr[i] = (lum + (pg - lum) * 0.45f) * 0.47f;
                    b[i] = (lum + (pb - lum) * 0.45f) * 0.40f;
                }
            }
        }
        finally { small.UnlockBits(data); }
        return (r, gr, b);
    }

    // =====================================================================================================================
    // Foreground
    // =====================================================================================================================

    /// <summary>
    /// Draws the card's text, ornaments and coats of arms onto <paramref name="target"/>, which must be
    /// <see cref="PixelFormat.Format32bppPArgb"/>; it is cleared to transparent first.
    /// </summary>
    public static CardLayout RenderForeground(Bitmap target, TitleCard card, CardKind kind, CardFonts fonts)
    {
        lock (TextGate) return RenderForegroundCore(target, card, kind, fonts);
    }

    private sealed class TextBlock(GraphicsPath path, RectangleF bounds)
    {
        public GraphicsPath Path { get; } = path;
        public RectangleF Bounds { get; } = bounds;
    }

    private static CardLayout RenderForegroundCore(Bitmap target, TitleCard card, CardKind kind, CardFonts fonts)
    {
        int w = target.Width, h = target.Height;
        float s = ScaleOf(w, h), cx = w / 2f;
        var family = fonts.Family;
        var italic = fonts.HasItalic ? FontStyle.Italic : FontStyle.Regular;

        using var leftCoa = LoadCoa(card.LeftCoaPng);
        using var rightCoa = LoadCoa(card.RightCoaPng);
        bool anyCoa = leftCoa != null || rightCoa != null;
        float coaBox = 214f * s, coaGap = 70f * s, margin = w * 0.05f;

        // ---- title: shrink to fit, then wrap onto two lines if it still does not fit
        float maxTitleW = anyCoa ? Math.Max(w * 0.46f, w - 2 * (margin + coaBox + coaGap)) : w * 0.76f;
        string title = string.IsNullOrWhiteSpace(card.Title) ? " " : card.Title.Trim();
        float titleEm = 118f * s;
        float singleWidth = MeasureWidth(title, family, FontStyle.Regular, titleEm);
        var titleLines = new List<string> { title };
        if (singleWidth > maxTitleW)
        {
            titleEm = Math.Max(78f * s, titleEm * maxTitleW / singleWidth);
            if (MeasureWidth(title, family, FontStyle.Regular, titleEm) > maxTitleW && SplitInTwo(title) is { } two)
            {
                titleLines = [two.first, two.second];
                float widest = Math.Max(MeasureWidth(two.first, family, FontStyle.Regular, 118f * s), MeasureWidth(two.second, family, FontStyle.Regular, 118f * s));
                titleEm = Math.Clamp(118f * s * maxTitleW / widest, 64f * s, 108f * s);
            }
        }
        float titleCap = CapHeight(family, FontStyle.Regular, titleEm);
        float titleAdvance = titleEm * 1.06f;
        float firstBaseline = h * TitleCenterFraction + titleCap / 2f - (titleLines.Count - 1) * titleAdvance / 2f;

        var title1 = new List<TextBlock>();
        for (int i = 0; i < titleLines.Count; i++)
            title1.Add(BuildLine(titleLines[i], family, FontStyle.Regular, titleEm, cx, firstBaseline + i * titleAdvance));
        var titleBounds = Union(title1.Select(t => t.Bounds));
        float lastBaseline = firstBaseline + (titleLines.Count - 1) * titleAdvance;

        // ---- ornaments
        float topOrnamentY = firstBaseline - titleCap - 0.50f * titleEm;
        float ruleY = lastBaseline + 0.50f * titleEm;
        float ruleHalf = Math.Clamp(titleBounds.Width / 2f + 70f * s, 250f * s, w * 0.30f);

        // ---- subtitle and detail lines
        var below = new List<TextBlock>();
        float maxLineW = anyCoa ? maxTitleW + coaBox : w * 0.86f;
        float cursor = ruleY + 40f * s;                       // cap top of the next line
        float subtitleBottom = ruleY;
        foreach (var line in SplitLines(card.Subtitle, 2))
        {
            float em = FitEm(line, family, italic, 46f * s, 34f * s, maxLineW);
            float cap = CapHeight(family, italic, em);
            var block = BuildLine(line, family, italic, em, cx, cursor + cap);
            below.Add(block);
            subtitleBottom = cursor + cap;
            cursor += cap + 0.62f * em;
        }
        int subtitleCount = below.Count;
        if (subtitleCount > 0) cursor += 10f * s;
        foreach (var line in SplitLines(card.Detail, 5))
        {
            float em = FitEm(line, family, FontStyle.Regular, 34f * s, 26f * s, maxLineW);
            float cap = CapHeight(family, FontStyle.Regular, em);
            below.Add(BuildLine(line, family, FontStyle.Regular, em, cx, cursor + cap));
            cursor += cap + 0.78f * em;
        }

        // ---- coats of arms flank the title block, centred between the top ornament and the subtitle
        float coaCenterY = (topOrnamentY + (subtitleCount > 0 ? subtitleBottom : ruleY)) / 2f;
        float contentHalf = Math.Max(Math.Max(titleBounds.Width / 2f, ruleHalf),
            below.Take(subtitleCount).Select(b => b.Bounds.Width / 2f).DefaultIfEmpty(0f).Max());
        RectangleF leftRect = leftCoa != null ? PlaceCoa(leftCoa, coaBox, cx - contentHalf - coaGap, coaCenterY, rightAligned: true, margin, w) : RectangleF.Empty;
        RectangleF rightRect = rightCoa != null ? PlaceCoa(rightCoa, coaBox, cx + contentHalf + coaGap, coaCenterY, rightAligned: false, margin, w) : RectangleF.Empty;

        bool defeat = kind == CardKind.Defeat;
        Color ornament = defeat ? OrnamentRed : OrnamentGold;

        using var g = Graphics.FromImage(target);
        g.Clear(Color.Transparent);
        ConfigureQuality(g);

        // 1. Warm glow behind the title (quarter resolution, heavily blurred).
        var glowColor = defeat ? Color.FromArgb(70, 200, 110, 80) : Color.FromArgb(78, 214, 160, 80);
        DrawBlurredLayer(g, target.Size, 4, (int)(26f * s), glowColor, PointF.Empty, lg =>
        {
            foreach (var t in title1) lg.FillPath(Brushes.White, t.Path);
        });

        // 2. Soft drop shadow under everything (half resolution).
        DrawBlurredLayer(g, target.Size, 2, (int)(7f * s), Color.FromArgb(225, 0, 0, 0), new PointF(0, 3.5f * s), lg =>
        {
            foreach (var t in title1.Concat(below)) lg.FillPath(Brushes.White, t.Path);
            DrawRule(lg, cx, ruleY, ruleHalf, s, Color.White, major: true);
            DrawRule(lg, cx, topOrnamentY, 96f * s, s, Color.White, major: false);
        });
        foreach (var (img, rect) in new[] { (leftCoa, leftRect), (rightCoa, rightRect) })
        {
            if (img == null) continue;
            DrawBlurredLayer(g, target.Size, 2, (int)(12f * s), Color.FromArgb(210, 0, 0, 0), new PointF(0, 9f * s), lg => DrawSilhouette(lg, img, rect));
        }

        // 3. Coats of arms.
        foreach (var (img, rect) in new[] { (leftCoa, leftRect), (rightCoa, rightRect) })
            if (img != null) g.DrawImage(img, rect);

        // 4. Ornamental rules.
        DrawRule(g, cx, ruleY, ruleHalf, s, ornament, major: true);
        DrawRule(g, cx, topOrnamentY, 96f * s, s, ornament, major: false);

        // 5. Title: thin dark outline for crispness, then a vertical gold (or ember for defeat) gradient.
        var ramp = defeat ? DefeatRamp : GoldRamp;
        var gradRect = RectangleF.Inflate(titleBounds, 0, 2);
        using (var outline = new Pen(Color.FromArgb(150, 28, 16, 6), Math.Max(1.2f, 2.4f * s)) { LineJoin = LineJoin.Round })
        using (var fill = new LinearGradientBrush(gradRect, ramp[0], ramp[^1], LinearGradientMode.Vertical)
               { InterpolationColors = new ColorBlend { Colors = ramp, Positions = RampStops } })
        {
            foreach (var t in title1) g.DrawPath(outline, t.Path);
            foreach (var t in title1) g.FillPath(fill, t.Path);
        }

        // 6. Subtitle (cream) and detail lines (muted).
        using (var creamBrush = new SolidBrush(Color.FromArgb(245, Cream)))
        using (var mutedBrush = new SolidBrush(Color.FromArgb(225, Muted)))
        {
            for (int i = 0; i < below.Count; i++) g.FillPath(i < subtitleCount ? creamBrush : mutedBrush, below[i].Path);
        }

        var layout = new CardLayout
        {
            Scale = s,
            TitleBounds = titleBounds,
            TextBounds = Union(title1.Concat(below).Select(b => b.Bounds)),
            LeftCoaBounds = leftRect,
            RightCoaBounds = rightRect,
            TitleEmPixels = titleEm,
            TitleLineCount = titleLines.Count,
        };
        foreach (var t in title1.Concat(below)) t.Path.Dispose();
        return layout;
    }

    /// <summary>
    /// Draws a one-line hint (e.g. "Click to continue to the battle") centred on a transparent
    /// <see cref="PixelFormat.Format32bppPArgb"/> strip. <paramref name="scale"/> is the card scale.
    /// </summary>
    public static void RenderHint(Bitmap target, string text, CardFonts fonts, float scale)
    {
        lock (TextGate)
        {
            using var g = Graphics.FromImage(target);
            g.Clear(Color.Transparent);
            ConfigureQuality(g);
            float em = FitEm(text, fonts.Family, FontStyle.Regular, 32f * scale, 22f * scale, target.Width * 0.9f);
            float cap = CapHeight(fonts.Family, FontStyle.Regular, em);
            var block = BuildLine(text, fonts.Family, FontStyle.Regular, em, target.Width / 2f, target.Height / 2f + cap / 2f);
            DrawBlurredLayer(g, target.Size, 2, (int)(6f * scale), Color.FromArgb(220, 0, 0, 0), new PointF(0, 3f * scale), lg => lg.FillPath(Brushes.White, block.Path));
            using var brush = new SolidBrush(Color.FromArgb(235, Cream));
            g.FillPath(brush, block.Path);
            block.Path.Dispose();
        }
    }

    /// <summary>Background and foreground composed into one opaque image (self tests, previews).</summary>
    public static Bitmap RenderComposite(TitleCard card, CardKind kind, int width, int height, CardFonts fonts, Bitmap? underlay, out CardLayout layout)
    {
        var result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        RenderBackground(result, kind, underlay);
        using var fg = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        layout = RenderForeground(fg, card, kind, fonts);
        using var g = Graphics.FromImage(result);
        g.CompositingMode = CompositingMode.SourceOver;
        g.DrawImageUnscaled(fg, 0, 0);
        return result;
    }

    // =====================================================================================================================
    // Helpers
    // =====================================================================================================================

    /// <summary>Card scale: 1 at 2560x1440; the smaller axis wins so ultrawide and portrait monitors stay balanced.</summary>
    public static float ScaleOf(int width, int height) => Math.Min(width / 2560f, height / 1440f);

    private static void ConfigureQuality(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    }

    private static StringFormat Typographic()
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.FormatFlags |= StringFormatFlags.NoWrap;
        return f;
    }

    /// <summary>A text line as a path: horizontally centred on its ink at <paramref name="centerX"/>, baseline at <paramref name="baselineY"/>.</summary>
    private static TextBlock BuildLine(string text, FontFamily family, FontStyle style, float emPx, float centerX, float baselineY)
    {
        var path = new GraphicsPath();
        using (var fmt = Typographic()) path.AddString(text, family, (int)style, emPx, PointF.Empty, fmt);
        float ascent = emPx * family.GetCellAscent(style) / family.GetEmHeight(style);
        var ink = path.GetBounds();
        using (var m = new Matrix())
        {
            m.Translate(centerX - (ink.Left + ink.Width / 2f), baselineY - ascent);
            path.Transform(m);
        }
        return new TextBlock(path, path.GetBounds());
    }

    private static float MeasureWidth(string text, FontFamily family, FontStyle style, float emPx)
    {
        using var path = new GraphicsPath();
        using var fmt = Typographic();
        path.AddString(text, family, (int)style, emPx, PointF.Empty, fmt);
        return path.GetBounds().Width;
    }

    /// <summary>Height of a capital H: the optical size used to centre lines vertically regardless of descenders.</summary>
    private static float CapHeight(FontFamily family, FontStyle style, float emPx)
    {
        using var path = new GraphicsPath();
        using var fmt = Typographic();
        path.AddString("H", family, (int)style, emPx, PointF.Empty, fmt);
        float hgt = path.GetBounds().Height;
        return hgt > 0 ? hgt : emPx * 0.7f;
    }

    private static float FitEm(string text, FontFamily family, FontStyle style, float preferred, float minimum, float maxWidth)
    {
        float width = MeasureWidth(text, family, style, preferred);
        return width <= maxWidth || width <= 0 ? preferred : Math.Max(minimum, preferred * maxWidth / width);
    }

    /// <summary>Splits at the space closest to the middle; null when there is no space.</summary>
    private static (string first, string second)? SplitInTwo(string text)
    {
        int best = -1;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == ' ' && (best < 0 || Math.Abs(i - text.Length / 2) < Math.Abs(best - text.Length / 2))) best = i;
        return best <= 0 ? null : (text[..best].Trim(), text[(best + 1)..].Trim());
    }

    private static IEnumerable<string> SplitLines(string? text, int max) =>
        (text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(max);

    private static RectangleF Union(IEnumerable<RectangleF> rects)
    {
        RectangleF? acc = null;
        foreach (var r in rects)
            if (r.Width > 0 && r.Height > 0) acc = acc is { } a ? RectangleF.Union(a, r) : r;
        return acc ?? RectangleF.Empty;
    }

    /// <summary>
    /// The ornamental rule: a small diamond with a dot, flanked by gold lines that fade out towards their ends, two
    /// small beads, and (for the major rule) a fine second line underneath.
    /// </summary>
    private static void DrawRule(Graphics g, float cx, float y, float half, float s, Color color, bool major)
    {
        float r = (major ? 8f : 5.5f) * s;
        float thick = Math.Max(1f, (major ? 1.7f : 1.3f) * s);
        using (var pen = new Pen(color, Math.Max(1f, 1.5f * s)) { LineJoin = LineJoin.Miter })
        {
            var diamond = new[] { new PointF(cx, y - r), new PointF(cx + r, y), new PointF(cx, y + r), new PointF(cx - r, y) };
            g.DrawPolygon(pen, diamond);
        }
        float ir = r * 0.42f;
        using (var solid = new SolidBrush(color))
        {
            g.FillPolygon(solid, [new PointF(cx, y - ir), new PointF(cx + ir, y), new PointF(cx, y + ir), new PointF(cx - ir, y)]);
            float beadR = Math.Max(1.2f, (major ? 2.1f : 1.6f) * s), beadX = r + 13f * s;
            g.FillEllipse(solid, cx - beadX - beadR, y - beadR, beadR * 2, beadR * 2);
            g.FillEllipse(solid, cx + beadX - beadR, y - beadR, beadR * 2, beadR * 2);
        }
        float start = r + 24f * s;
        FadingLine(g, cx + start, cx + half, y, thick, color, 235);
        FadingLine(g, cx - start, cx - half, y, thick, color, 235);
        if (major)
        {
            float y2 = y + 6f * s, thin = Math.Max(1f, 0.9f * s);
            FadingLine(g, cx + start, cx + half * 0.62f, y2, thin, color, 130);
            FadingLine(g, cx - start, cx - half * 0.62f, y2, thin, color, 130);
        }
    }

    /// <summary>A horizontal line from x0 (full alpha) to x1 (transparent).</summary>
    private static void FadingLine(Graphics g, float x0, float x1, float y, float thickness, Color color, int alpha)
    {
        if (Math.Abs(x1 - x0) < 1f) return;
        var rect = new RectangleF(Math.Min(x0, x1), y - thickness / 2f, Math.Abs(x1 - x0), thickness);
        var c0 = Color.FromArgb(alpha, color);
        var c1 = Color.FromArgb(0, color);
        // Gradient rect is inflated by a pixel so the ends do not wrap around (GDI+ tiles linear gradients).
        var gradRect = RectangleF.Inflate(rect, 1, 1);
        using var brush = x1 > x0
            ? new LinearGradientBrush(gradRect, c0, c1, LinearGradientMode.Horizontal)
            : new LinearGradientBrush(gradRect, c1, c0, LinearGradientMode.Horizontal);
        brush.WrapMode = WrapMode.TileFlipX;
        g.FillRectangle(brush, rect);
    }

    /// <summary>
    /// Renders shapes into a reduced-resolution mask, blurs it and composites it in <paramref name="color"/> at the given
    /// offset. Used for drop shadows and glows; the reduction keeps a large blur cheap at 1440p.
    /// </summary>
    private static void DrawBlurredLayer(Graphics g, Size targetSize, int reduction, int blurRadiusPx, Color color, PointF offset, Action<Graphics> drawShapes)
    {
        int lw = Math.Max(1, targetSize.Width / reduction), lh = Math.Max(1, targetSize.Height / reduction);
        using var layer = new Bitmap(lw, lh, PixelFormat.Format32bppPArgb);
        using (var lg = Graphics.FromImage(layer))
        {
            lg.Clear(Color.Transparent);
            lg.SmoothingMode = SmoothingMode.AntiAlias;
            lg.InterpolationMode = InterpolationMode.HighQualityBilinear;
            lg.ScaleTransform(1f / reduction, 1f / reduction);
            drawShapes(lg);
        }
        Procedural.BoxBlur(layer, Math.Max(1, blurRadiusPx / reduction / 2), 3);

        // Recolour: the mask is white with alpha; the colour matrix maps it to the requested colour and alpha.
        var matrix = new ColorMatrix(
        [
            [0, 0, 0, 0, 0],
            [0, 0, 0, 0, 0],
            [0, 0, 0, 0, 0],
            [0, 0, 0, color.A / 255f, 0],
            [color.R / 255f, color.G / 255f, color.B / 255f, 0, 1],
        ]);
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(matrix);
        var previous = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        var dest = new Rectangle((int)MathF.Round(offset.X), (int)MathF.Round(offset.Y), lw * reduction, lh * reduction);
        g.DrawImage(layer, dest, 0, 0, lw, lh, GraphicsUnit.Pixel, attributes);
        g.InterpolationMode = previous;
    }

    /// <summary>Draws an image's alpha as a white silhouette (feeds <see cref="DrawBlurredLayer"/>).</summary>
    private static void DrawSilhouette(Graphics g, Bitmap image, RectangleF rect)
    {
        var white = new ColorMatrix(
        [
            [0, 0, 0, 0, 0],
            [0, 0, 0, 0, 0],
            [0, 0, 0, 0, 0],
            [0, 0, 0, 1, 0],
            [1, 1, 1, 0, 1],
        ]);
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(white);
        var dest = new[] { rect.Location, new PointF(rect.Right, rect.Top), new PointF(rect.Left, rect.Bottom) };
        g.DrawImage(image, dest, new RectangleF(0, 0, image.Width, image.Height), GraphicsUnit.Pixel, attributes);
    }

    /// <summary>Fits the CoA into a square box, keeping its aspect, anchored at <paramref name="anchorX"/> and clamped on screen.</summary>
    private static RectangleF PlaceCoa(Bitmap coa, float box, float anchorX, float centerY, bool rightAligned, float margin, int width)
    {
        float aspect = coa.Width / (float)Math.Max(1, coa.Height);
        float cw = aspect >= 1 ? box : box * aspect, ch = aspect >= 1 ? box / aspect : box;
        float x = rightAligned ? anchorX - cw : anchorX;
        x = Math.Clamp(x, margin, width - margin - cw);
        return new RectangleF(x, centerY - ch / 2f, cw, ch);
    }

    /// <summary>Loads a CoA PNG fully into memory (no file lock is kept). Null when missing or unreadable.</summary>
    private static Bitmap? LoadCoa(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            using var image = Image.FromStream(stream);
            var copy = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(copy)) g.DrawImage(image, 0, 0, image.Width, image.Height);
            return copy;
        }
        catch (Exception e)
        {
            UxLog.Write($"card: cannot load coat of arms '{path}': {e.Message}");
            return null;
        }
    }

    private static byte ToByte(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);
}
