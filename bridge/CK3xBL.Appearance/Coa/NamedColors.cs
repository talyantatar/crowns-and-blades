// CK3 coat-of-arms colours: names resolve through common/named_colors (default_colors.txt: red = hsv { 0.02 0.8 0.45 },
// brown = hsv360 { 21 74 45 }, ...); literal colours are rgb { r g b }, hsv { }, hsv360 { } or hex { }.
using System.Globalization;
using CK3xBL.Appearance.Game;
using CK3xBL.Appearance.Genes;
using CK3xBL.Appearance.Pdx;

namespace CK3xBL.Appearance.Coa;

internal sealed class NamedColors
{
    private readonly Dictionary<string, Rgb> _colors = new(StringComparer.OrdinalIgnoreCase);

    // Vanilla CoA colour names, longest first, for guessing unknown mod colours by name ("asoiaf_black" -> black).
    private static readonly string[] GuessOrder =
        { "yellow_light", "green_light", "blue_light", "red_dark", "blue_dark", "purple", "orange", "yellow", "green",
          "black", "white", "brown", "grey", "gray", "blue", "red", "gold", "silver" };

    public int Count => _colors.Count;

    public static NamedColors Load(GameFiles? files)
    {
        var nc = new NamedColors();
        if (files != null)
            foreach (var file in files.ListFiles("common/named_colors"))
            {
                var root = PdxTextParser.Parse(GameFiles.ReadShared(file));
                var block = root.Block("colors") ?? root;
                foreach (var it in block.Items)
                    if (it.Value.Block is { } b && Convert(b) is Rgb c)
                        nc._colors[it.Key] = c;
            }
        // Built-in values (default_colors.txt, 1.20) so the component degrades gracefully without an install.
        nc.Default("red", Rgb.FromHsv(0.02, 0.8, 0.45));
        nc.Default("blue", Rgb.FromHsv(0.58, 0.8, 0.4));
        nc.Default("yellow", Rgb.FromHsv(0.1, 0.75, 0.75));
        nc.Default("green", Rgb.FromHsv(0.35, 0.6, 0.30));
        nc.Default("black", Rgb.FromHsv(0.1, 0.25, 0.10));
        nc.Default("white", Rgb.FromHsv(0.08, 0.02, 0.8));
        nc.Default("purple", Rgb.FromHsv(0.9, 0.7, 0.35));
        nc.Default("orange", Rgb.FromHsv(0.064, 1, 0.6));
        nc.Default("grey", Rgb.FromHsv(0, 0, 0.5));
        nc.Default("brown", Rgb.FromHsv(21 / 360.0, 0.74, 0.45));
        nc.Default("blue_light", Rgb.FromHsv(0.58, 0.70, 0.55));
        nc.Default("green_light", Rgb.FromHsv(0.35, 0.50, 0.40));
        nc.Default("yellow_light", Rgb.FromHsv(0.1, 0.80, 1.0));
        nc.Default("red_dark", Rgb.FromHsv(0.0, 0.9, 0.30));
        nc.Default("blue_dark", Rgb.FromHsv(0.58, 0.9, 0.3));
        nc.Default("gold", Rgb.FromHsv(0.1, 0.75, 0.75));
        nc.Default("silver", Rgb.FromHsv(0.08, 0.02, 0.8));
        nc.Default("gray", Rgb.FromHsv(0, 0, 0.5));
        return nc;
    }

    private void Default(string name, Rgb c) => _colors.TryAdd(name, c);

    /// <summary>Resolves a colour value: a name, or an rgb/hsv/hsv360/hex block. Unknown names are guessed by keyword.</summary>
    public CoaColor? Resolve(PdxValue? v)
    {
        if (v == null) return null;
        if (v.Block != null) return Convert(v.Block) is Rgb c ? CoaColor.From(null, c) : null;
        return ResolveName(v.Text);
    }

    public CoaColor? ResolveName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_colors.TryGetValue(name, out var c)) return CoaColor.From(name, c);
        foreach (var g in GuessOrder)
            if (name.Contains(g, StringComparison.OrdinalIgnoreCase) && _colors.TryGetValue(g, out var gc))
                return CoaColor.From(name, gc);
        return null;
    }

    public static Rgb? Convert(PdxNode b)
    {
        var n = b.Numbers();
        switch (b.Function)
        {
            case "hsv" when n.Count >= 3: return Rgb.FromHsv(n[0], n[1], n[2]);
            case "hsv360" when n.Count >= 3: return Rgb.FromHsv(n[0] / 360, n[1] / 100, n[2] / 100);
            case "hex":
            {
                string? w = b.Words().FirstOrDefault();
                if (w == null) return null;
                w = w.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? w[2..] : w.TrimStart('#');
                if (w.Length < 6 || !uint.TryParse(w.AsSpan(0, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
                    return null;
                return new Rgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
            }
            default:
                if (n.Count < 3) return null;
                // rgb { 204 204 0 } (0..255); a few files use 0..1 floats
                bool unit = n.Take(3).All(x => x <= 1.0) && b.Words().Take(3).Any(w => w.Contains('.'));
                double k = unit ? 255 : 1;
                return new Rgb(Clamp(n[0] * k), Clamp(n[1] * k), Clamp(n[2] * k));
        }
    }

    private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
}
