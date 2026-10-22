// CK3 coat of arms -> Bannerlord banner code (PROTOCOL §7.2):
//   layer = mesh.color.color2.sizeX.sizeY.posX.posY.stroke.mirror.rotationDegrees (integers only, invariant culture)
//   layer 0  background 'plain_background' over the 1528 canvas in CK3 color1 (team colour 1)
//   divisions  the pattern's color2/color3 areas and every ordinary/frame emblem, rasterised from the user's own
//              textures and approximated by up to N rectangles (icon 'rectangle_icon'); coarser grids are tried before
//              a shape is dropped. The shield maps to the 512 px editable area centred at 764; rectangles that touch the
//              shield edge are extended to the canvas edge so the field covers the whole flag.
//   charges  keyword table -> icon id, CK3 position/scale/mirror/rotation; first icon colour = team colour 2.
// Icons are single-fill in Bannerlord (colorId2 is the stroke colour), so two-tone CK3 charges keep their color1.
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CK3xBL.Appearance.Data;
using CK3xBL.Appearance.Genes;
using CK3xBL.Appearance.Imaging;

namespace CK3xBL.Appearance.Coa;

internal sealed class BannerLayer
{
    public int Mesh, Color, Color2, SizeX, SizeY, PosX, PosY, Stroke, Mirror, Rotation;

    public IEnumerable<int> Values()
    {
        yield return Mesh; yield return Color; yield return Color2; yield return SizeX; yield return SizeY;
        yield return PosX; yield return PosY; yield return Stroke; yield return Mirror; yield return Rotation;
    }
}

internal sealed class BannerResult
{
    public readonly List<BannerLayer> Layers = new();
    public string Source = "";
    public string Notes = "";

    public int PrimaryColor => Layers.Count > 0 ? Layers[0].Color : 0;
    public int SecondaryColor => Layers.Count > 1 ? Layers[1].Color : PrimaryColor;

    public string Code => string.Join(".", Layers.SelectMany(l => l.Values()).Select(v => v.ToString(CultureInfo.InvariantCulture)));
}

internal sealed class BannerBuilder
{
    private const int Canvas = 1528, Center = 764, Area = 512;
    private const int Samples = 32;                      // rasterisation resolution over the shield
    /// <summary>Redmean distance below which two team colours look alike (PROTOCOL §7.2; the module's RequestRules.ClashDistance).</summary>
    public const double ClashDistance = 60;
    /// <summary>A banner code holds at most 33 layers: the background and 32 icons (the module rejects longer codes).</summary>
    public const int MaxLayers = 33;
    /// <summary>The module flies this culture's banner when it does not know a side's culture id.</summary>
    private const string ModuleFallbackCulture = "vlandia";

    private sealed record ChargeRule(string[] Words, string[] Substrings, string Category, int Icon);

    private readonly List<(int Id, Rgb Rgb)> _palette;
    private readonly Dictionary<int, Rgb> _paletteById;
    private readonly Dictionary<string, int> _named;
    private readonly Dictionary<string, CultureBanner?> _cultureBanners;
    private readonly CoaTextures _tex;
    private readonly List<ChargeRule> _charges = new();
    private readonly Regex _nonCharge;
    private readonly int _background = 11, _rectangle = 510, _maxDivisions = 10, _maxIcons = MaxLayers - 1;
    private readonly int _fallbackIcon = 409;
    private readonly double _maxDivisionError = 0.25, _minDivisionArea = 0.04;

    public BannerBuilder(List<(int Id, Rgb Rgb)> palette, Dictionary<string, int> named, Dictionary<string, CultureBanner?> cultureBanners,
                         JsonDocument? table, CoaTextures tex)
    {
        _palette = palette;
        _paletteById = palette.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Rgb);
        _named = named;
        _cultureBanners = cultureBanners;
        _tex = tex;
        // Same defaults as bridge/data/ck3_emblem_to_bl_icon.json, used when the table is missing.
        string nonCharge = "^ce_(ordinary|per_|party|quarter|gyron|paly|pales|bendy|bends|barry|bars|barru|chequy|checky|checkers|block|border|bend|pale|fess|chevron|pile|stripes|circle$|circle_|empty|mena_(pale|fess|bend)|.*frame|.*pattern|.*stripes|.*_dual$|lozengy|lozenge|bordure|chief|saltire_|cross_.*_border|india_doted|vair|ermine|fretty|masoned|waves|wavy|diagonal|dancetty|embattled|indented|engrailed|label|(circle|crescent)_mask)";
        if (table != null)
        {
            var r = table.RootElement;
            _background = Int(r, "plain_background", _background);
            _rectangle = Int(r, "rectangle_icon", _rectangle);
            _maxDivisions = Math.Clamp(Int(r, "max_division_layers", _maxDivisions), 0, MaxLayers - 1);
            _maxIcons = Math.Clamp(Int(r, "max_icon_layers", _maxIcons), 1, MaxLayers - 1);
            _maxDivisionError = Dbl(r, "division_max_error", _maxDivisionError);
            _minDivisionArea = Dbl(r, "division_min_area", _minDivisionArea);
            if (r.TryGetProperty("non_charge_regex", out var nc) && nc.GetString() is string s) nonCharge = s;
            if (r.TryGetProperty("fallback_charge", out var fb)) _fallbackIcon = Int(fb, "icon", _fallbackIcon);
            if (r.TryGetProperty("charges", out var arr))
                foreach (var c in arr.EnumerateArray())
                {
                    var kws = c.GetProperty("keywords").EnumerateArray().Select(k => k.GetString()!.ToLowerInvariant()).ToArray();
                    _charges.Add(new ChargeRule(kws.Where(k => !k.Contains('_')).ToArray(), kws.Where(k => k.Contains('_')).ToArray(),
                        c.TryGetProperty("category", out var cat) ? cat.GetString() ?? "" : "", Int(c, "icon", _fallbackIcon)));
                }
        }
        _nonCharge = new Regex(nonCharge, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public bool IsCharge(string texture) => !_nonCharge.IsMatch(texture);

    public BannerResult Build(CoaModel coa)
    {
        var result = new BannerResult { Source = coa.Source };
        var notes = new List<string>();
        int bg = ColorId(coa.Colors[0]) ?? ColorId("white") ?? 110;
        result.Layers.Add(new BannerLayer { Mesh = _background, Color = bg, Color2 = bg, SizeX = Canvas, SizeY = Canvas, PosX = Center, PosY = Center });

        // ---- field divisions: pattern channels, then ordinaries/frames in file order
        int divisionBudget = _maxDivisions;
        // Palette colour currently showing at each sample of the shield, so a piece is drawn only where it changes the
        // colour (a white stripe on a white field is skipped, a counterchanged piece over another division is kept).
        var field = Enumerable.Repeat(bg, Samples * Samples).ToArray();
        void Division(float[] coverage, CoaColor? color, string label)
        {
            if (ColorId(color) is not int cid) return;
            var visible = new float[coverage.Length];
            for (int i = 0; i < visible.Length; i++) visible[i] = field[i] == cid ? 0 : coverage[i];
            // Divisions share the icon layers with the charges: never more than the code can hold.
            int room = Math.Min(divisionBudget, _maxIcons + 1 - result.Layers.Count);
            int added = AddDivision(result, visible, cid, room, field, out string why);
            divisionBudget -= added;
            if (why.Length > 0) notes.Add($"{label}: {why}");
        }
        var pat = coa.Pattern.Equals("pattern_solid", StringComparison.OrdinalIgnoreCase) ? null : _tex.Pattern(coa.Pattern, Samples);
        if (pat != null)
        {
            var (g, b) = PatternCoverage(pat);
            Division(g, coa.Colors[1], coa.Pattern + " color2");
            Division(b, coa.Colors[2], coa.Pattern + " color3");
        }
        var charges = new List<(CoaEmblem E, CoaInstance I, int N)>();
        foreach (var e in coa.Emblems)
        {
            if (!e.Textured && !IsCharge(e.Texture))
            {
                // Ordinaries/frames/fields: each of the emblem's colour areas becomes rectangles (or is dropped).
                if (EmblemCoverage(e, pat) is float[][] cov)
                    for (int c = 0; c < 3; c++)
                        Division(cov[c], e.Colors[c] ?? (c == 0 ? null : e.Colors[0]), $"{e.Texture} color{c + 1}");
                continue;
            }
            foreach (var inst in e.Instances) charges.Add((e, inst, charges.Count));
        }

        // ---- charges (deeper instances first, as in CK3)
        charges.Sort((a, b) => a.I.Depth != b.I.Depth ? b.I.Depth.CompareTo(a.I.Depth) : a.N.CompareTo(b.N));
        foreach (var (e, inst, _) in charges)
        {
            if (result.Layers.Count > _maxIcons) break;   // background + _maxIcons icons (<= MaxLayers)
            int icon = IconFor(e.Texture, out string how);
            int color = ChargeColor(e, bg);
            bool mirror = inst.ScaleX < 0;
            int rot = (int)Math.Round(inst.Rotation);
            if (inst.ScaleY < 0) { mirror = !mirror; rot += 180; } // a vertical flip = horizontal flip + half turn
            result.Layers.Add(new BannerLayer
            {
                Mesh = icon, Color = color, Color2 = color,
                SizeX = Math.Max(1, (int)Math.Round(Math.Abs(inst.ScaleX) * Area)),
                SizeY = Math.Max(1, (int)Math.Round(Math.Abs(inst.ScaleY) * Area)),
                PosX = (int)Math.Round(Center + (inst.X - 0.5) * Area),
                PosY = (int)Math.Round(Center + (inst.Y - 0.5) * Area),
                Mirror = mirror ? 1 : 0,
                Rotation = ((rot % 360) + 360) % 360,
            });
            notes.Add($"{e.Texture}->{icon}({how})");
        }
        if (result.Layers.Count == 1 && coa.Emblems.Count > 0)
        {
            // Nothing could be drawn but the CoA has emblems: keep a centred mark so team colour 2 is the CK3 color2.
            int c2 = ColorId(coa.Colors[1]) is int x && x != bg ? x : Contrast(bg);
            result.Layers.Add(new BannerLayer { Mesh = _fallbackIcon, Color = c2, Color2 = c2, SizeX = 400, SizeY = 400, PosX = Center, PosY = Center });
            notes.Add($"fallback icon {_fallbackIcon}");
        }
        result.Notes = string.Join("; ", notes);
        return result;
    }

    // ------------------------------------------------------------------ colours

    public int? ColorId(CoaColor? c)
    {
        if (c == null) return null;
        if (c.Name != null && _named.TryGetValue(c.Name, out int id)) return id;
        return Nearest(c.Rgb);
    }

    private int? ColorId(string name) => _named.TryGetValue(name, out int id) ? id : null;

    private int Nearest(Rgb c)
    {
        int best = _palette.Count > 0 ? _palette[0].Id : 0;
        double bestD = double.MaxValue;
        foreach (var (id, rgb) in _palette)
        {
            double d = Rgb.Distance(rgb, c);
            if (d < bestD) { bestD = d; best = id; }
        }
        return best;
    }

    public Rgb RgbOf(int id) => _paletteById.TryGetValue(id, out var c) ? c : new Rgb(128, 128, 128);

    /// <summary>White or black palette colour, whichever stands out against 'background'.</summary>
    private int Contrast(int background)
    {
        var c = RgbOf(background);
        double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        return lum > 128 ? ColorId("black") ?? 149 : ColorId("white") ?? 110;
    }

    private int ChargeColor(CoaEmblem e, int bg)
    {
        foreach (var c in e.Colors)
            if (ColorId(c) is int id && Rgb.Distance(RgbOf(id), RgbOf(bg)) >= 40) return id;
        return Contrast(bg);
    }

    // ------------------------------------------------------------------ icons

    private int IconFor(string texture, out string how)
    {
        string name = texture.ToLowerInvariant();
        string stem = name.StartsWith("ce_", StringComparison.Ordinal) ? name[3..] : name;
        var words = stem.Split('_', StringSplitOptions.RemoveEmptyEntries);
        foreach (var rule in _charges)
        {
            if (rule.Substrings.Any(s => stem.Contains(s, StringComparison.Ordinal)) || rule.Words.Any(w => words.Any(x => WordMatch(x, w))))
            {
                how = rule.Category;
                return rule.Icon;
            }
        }
        how = "fallback";
        return _fallbackIcon;
    }

    private static bool WordMatch(string word, string keyword) =>
        word == keyword || word == keyword + "s" || word == keyword + "es";

    // ------------------------------------------------------------------ divisions

    private static (float[] G, float[] B) PatternCoverage(RgbaImage pat)
    {
        var g = new float[Samples * Samples];
        var b = new float[Samples * Samples];
        Span<float> t = stackalloc float[4];
        for (int y = 0; y < Samples; y++)
        for (int x = 0; x < Samples; x++)
        {
            pat.SampleBilinear((x + 0.5) / Samples, (y + 0.5) / Samples, t);
            // The lerp chain ends with color3 (blue), so blue wins over green.
            b[y * Samples + x] = t[2];
            g[y * Samples + x] = t[1] * (1 - t[2]);
        }
        return (g, b);
    }

    /// <summary>
    /// Coverage of each of the emblem's three colour areas (alpha x the shader's colour weights, clipped by the pattern mask
    /// as in the renderer), union of instances.
    /// </summary>
    private float[][]? EmblemCoverage(CoaEmblem e, RgbaImage? pattern)
    {
        var tex = _tex.Emblem(e.Texture, e.Textured, Samples * 2);
        if (tex == null) return null;
        var cov = new[] { new float[Samples * Samples], new float[Samples * Samples], new float[Samples * Samples] };
        Span<float> t = stackalloc float[4];
        Span<float> p = stackalloc float[4];
        bool m1 = false, m2 = false, m3 = false;
        if (e.Mask != null)
            foreach (int ch in e.Mask) { m1 |= ch == 1; m2 |= ch == 2; m3 |= ch == 3; }
        bool masked = (m1 || m2 || m3) && pattern != null;
        foreach (var inst in e.Instances)
        {
            var xf = new EmblemTransform(inst);
            if (xf.Degenerate) continue;
            for (int y = 0; y < Samples; y++)
            for (int x = 0; x < Samples; x++)
            {
                if (!xf.ToUv((x + 0.5) / Samples, (y + 0.5) / Samples, out double u, out double v)) continue;
                tex.SampleBilinear(u, v, t);
                int i = y * Samples + x;
                // rgb = c1; lerp(c2, g); lerp(c3, r)  ->  weights of c1, c2, c3
                float w3 = t[0], w2 = t[1] * (1 - t[0]), w1 = (1 - t[1]) * (1 - t[0]);
                if (e.Textured) { w1 = 1; w2 = w3 = 0; }
                float a = t[3];
                if (masked)
                {
                    pattern!.SampleBilinear((x + 0.5) / Samples, (y + 0.5) / Samples, p);
                    float pr = Math.Clamp(p[0] - p[1] - p[2], 0, 1), pg = Math.Clamp(p[1] - p[2], 0, 1), pb = p[2];
                    a *= Math.Clamp((m1 ? pr : 0) + (m2 ? pg : 0) + (m3 ? pb : 0), 0, 1);
                }
                cov[0][i] = Math.Max(cov[0][i], a * w1);
                cov[1][i] = Math.Max(cov[1][i], a * w2);
                cov[2][i] = Math.Max(cov[2][i], a * w3);
            }
        }
        return cov;
    }

    /// <summary>
    /// Approximates a coverage map by at most 'budget' rectangles, trying 32, 16, 8 then 4 cells per side and accepting the
    /// first fit whose error (|rectangles - coverage| / coverage) is small enough. Returns the number of layers added.
    /// </summary>
    private int AddDivision(BannerResult result, float[] coverage, int color, int budget, int[] field, out string why)
    {
        double area = coverage.Sum() / coverage.Length;
        if (area < _minDivisionArea) { why = area > 0.001 ? $"ignored (area {area:0.00})" : ""; return 0; }
        if (budget <= 0) { why = "dropped (no layers left)"; return 0; }
        foreach (int n in new[] { Samples, 16, 8, 4 })
        {
            var rects = FitRectangles(Downsample(coverage, n), n, budget);
            if (rects == null) continue;
            double error = FitError(coverage, rects, n);
            if (error > _maxDivisionError) continue;
            int k = Samples / n;
            foreach (var (x0, y0, x1, y1) in rects)
            {
                for (int y = y0 * k; y < y1 * k; y++)
                    for (int x = x0 * k; x < x1 * k; x++) field[y * Samples + x] = color;
                double l = x0 == 0 ? 0 : Center + ((double)x0 / n - 0.5) * Area;
                double r = x1 == n ? Canvas : Center + ((double)x1 / n - 0.5) * Area;
                double t = y0 == 0 ? 0 : Center + ((double)y0 / n - 0.5) * Area;
                double b = y1 == n ? Canvas : Center + ((double)y1 / n - 0.5) * Area;
                result.Layers.Add(new BannerLayer
                {
                    Mesh = _rectangle, Color = color, Color2 = color,
                    SizeX = (int)Math.Round(r - l), SizeY = (int)Math.Round(b - t),
                    PosX = (int)Math.Round((l + r) / 2), PosY = (int)Math.Round((t + b) / 2),
                });
            }
            why = $"{rects.Count} rect @{n} err {error:0.00}";
            return rects.Count;
        }
        why = "dropped (no close rectangle fit)";
        return 0;
    }

    private static double FitError(float[] coverage, List<(int X0, int Y0, int X1, int Y1)> rects, int n)
    {
        int k = Samples / n;
        double diff = 0, total = 0;
        for (int y = 0; y < Samples; y++)
        for (int x = 0; x < Samples; x++)
        {
            int cx = x / k, cy = y / k;
            bool inside = rects.Any(r => cx >= r.X0 && cx < r.X1 && cy >= r.Y0 && cy < r.Y1);
            float c = coverage[y * Samples + x];
            diff += Math.Abs((inside ? 1 : 0) - c);
            total += c;
        }
        return total > 0 ? diff / total : 1;
    }

    private static bool[] Downsample(float[] cov, int n)
    {
        int k = Samples / n;
        var cells = new bool[n * n];
        for (int cy = 0; cy < n; cy++)
        for (int cx = 0; cx < n; cx++)
        {
            float sum = 0;
            for (int y = 0; y < k; y++)
            for (int x = 0; x < k; x++)
                sum += cov[(cy * k + y) * Samples + cx * k + x];
            cells[cy * n + cx] = sum / (k * k) >= 0.5f;
        }
        return cells;
    }

    /// <summary>Greedy row-major cover by maximal rectangles; null when more than 'max' are needed.</summary>
    private static List<(int X0, int Y0, int X1, int Y1)>? FitRectangles(bool[] cells, int n, int max)
    {
        var left = (bool[])cells.Clone();
        var rects = new List<(int, int, int, int)>();
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            if (!left[y * n + x]) continue;
            int x1 = x;
            while (x1 < n && left[y * n + x1]) x1++;
            int y1 = y + 1;
            while (y1 < n && Enumerable.Range(x, x1 - x).All(i => cells[y1 * n + i])) y1++;
            for (int yy = y; yy < y1; yy++)
                for (int xx = x; xx < x1; xx++) left[yy * n + xx] = false;
            rects.Add((x, y, x1, y1));
            if (rects.Count > max) return null;
        }
        return rects;
    }

    // ------------------------------------------------------------------ side clash

    /// <summary>The culture banner the module flies for a side without a CK3 banner; null when its colours are unknown.</summary>
    public CultureBanner? CultureBannerOf(string? culture)
    {
        if (!string.IsNullOrEmpty(culture) && _cultureBanners.TryGetValue(culture, out var known)) return known;
        return _cultureBanners.GetValueOrDefault(ModuleFallbackCulture);
    }

    /// <summary>
    /// When both sides' primary colours look alike, swap the enemy's two team colours (as the stock code does for
    /// same-culture battles); if they still clash, recolour the enemy field with the palette colour farthest from ours.
    /// Colours are swapped per layer, so every division and charge keeps its own colour.
    /// </summary>
    public string? ResolveClash(BannerResult mine, BannerResult enemy) =>
        mine.Layers.Count == 0 ? null : ResolveClash(RgbOf(mine.PrimaryColor), RgbOf(mine.SecondaryColor), enemy);

    /// <summary>The same rule against our side's team colours (e.g. a culture banner's).</summary>
    public string? ResolveClash(Rgb myPrimary, Rgb mySecondary, BannerResult enemy)
    {
        if (enemy.Layers.Count == 0) return null;
        if (Rgb.Distance(myPrimary, RgbOf(enemy.PrimaryColor)) >= ClashDistance) return null;
        int p = enemy.PrimaryColor, s = enemy.SecondaryColor;
        if (s != p && Rgb.Distance(myPrimary, RgbOf(s)) >= ClashDistance)
        {
            foreach (var l in enemy.Layers)
            {
                l.Color = l.Color == p ? s : l.Color == s ? p : l.Color;
                l.Color2 = l.Color2 == p ? s : l.Color2 == s ? p : l.Color2;
            }
            return $"swapped enemy colours {p}<->{s}";
        }
        var candidates = new[] { "red", "blue", "yellow", "green", "black", "white", "purple", "orange" }
            .Select(ColorId).Where(c => c is int).Select(c => c!.Value).Distinct().ToList();
        if (candidates.Count == 0) return null;
        int best = candidates.OrderByDescending(c => Math.Min(Rgb.Distance(RgbOf(c), myPrimary), Rgb.Distance(RgbOf(c), mySecondary))).First();
        foreach (var l in enemy.Layers)
        {
            if (l.Color == p) l.Color = best;
            if (l.Color2 == p) l.Color2 = best;
        }
        return $"recoloured enemy field {p}->{best}";
    }

    private static double Dbl(JsonElement e, string prop, double fallback) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    private static int Int(JsonElement e, string prop, int fallback) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
}
