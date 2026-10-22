// A character's genes in CK3 terms.
//   colour genes (hair_color, skin_color, eye_color): x, y, recessive x, recessive y (0..255, palette UV)
//   morph/accessory genes: template, value, recessive template, recessive value (value 0..255)
// The first (dominant) pair is what the portrait shows; the recessive pair only matters for inheritance.
using System.Globalization;
using System.Text;
using CK3xBL.Appearance.Pdx;

namespace CK3xBL.Appearance.Genes;

internal readonly record struct ColorGene(int X, int Y, int RecX, int RecY);

internal readonly record struct MorphGene(string Template, int Value, string RecTemplate, int RecValue);

internal sealed class GeneSet
{
    public readonly Dictionary<string, ColorGene> Colors = new(StringComparer.Ordinal);
    public readonly Dictionary<string, MorphGene> Morphs = new(StringComparer.Ordinal);

    public int Count => Colors.Count + Morphs.Count;

    public MorphGene? Morph(string gene) => Morphs.TryGetValue(gene, out var g) ? g : null;
    public ColorGene? Color(string gene) => Colors.TryGetValue(gene, out var g) ? g : null;

    /// <summary>From a portrait block: <c>genes = { hair_color = { 42 249 42 249 } gene_x = { "tmpl" 144 "tmpl" 144 } ... }</c>.</summary>
    public static GeneSet FromPortraitBlock(PdxNode genes)
    {
        var set = new GeneSet();
        foreach (var it in genes.Items)
        {
            var b = it.Value.Block;
            if (b == null || b.List.Count < 2) continue;
            var l = b.List;
            if (l[0].AsLong() is long x && l[1].AsLong() is long y)
            {
                long rx = l.Count > 2 ? l[2].AsLong() ?? x : x, ry = l.Count > 3 ? l[3].AsLong() ?? y : y;
                set.Colors[it.Key] = new ColorGene(Byte(x), Byte(y), Byte(rx), Byte(ry));
            }
            else if (l[0].Text is string t && l[1].AsLong() is long v)
            {
                string rt = l.Count > 2 && l[2].Text != null ? l[2].Text! : t;
                long rv = l.Count > 3 ? l[3].AsLong() ?? v : v;
                set.Morphs[it.Key] = new MorphGene(t, Byte(v), rt, Byte(rv));
            }
        }
        return set;
    }

    /// <summary>Decodes a stored <c>dna="..."</c> string (4 bytes per inheritable gene, in gene-database order).</summary>
    public static GeneSet? FromDna(string base64, GeneDb db, out string? error)
    {
        error = null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { error = "dna is not base64"; return null; }
        var order = db.Inheritable;
        if (bytes.Length != order.Count * 4)
        {
            // Another playset or game version: the gene order is unknown, so nothing can be decoded reliably.
            error = $"dna has {bytes.Length} bytes, gene database expects {order.Count * 4} (different playset or game version)";
            return null;
        }
        var set = new GeneSet();
        for (int i = 0; i < order.Count; i++)
        {
            var g = order[i];
            int o = i * 4;
            if (g.Kind == GeneKind.Color)
                set.Colors[g.Name] = new ColorGene(bytes[o], bytes[o + 1], bytes[o + 2], bytes[o + 3]);
            else
                set.Morphs[g.Name] = new MorphGene(g.TemplateName(bytes[o]), bytes[o + 1], g.TemplateName(bytes[o + 2]), bytes[o + 3]);
        }
        return set;
    }

    // ---- compact serialisation for the appearance cache: "name=a,b,c,d;..." (templates may not contain , ; =) ----

    public string Serialize()
    {
        var sb = new StringBuilder(Count * 32);
        foreach (var (k, c) in Colors)
            sb.Append(k).Append('=').Append(c.X).Append(',').Append(c.Y).Append(',').Append(c.RecX).Append(',').Append(c.RecY).Append(';');
        foreach (var (k, m) in Morphs)
            sb.Append(k).Append("=~").Append(m.Template).Append(',').Append(m.Value).Append(',').Append(m.RecTemplate).Append(',').Append(m.RecValue).Append(';');
        return sb.ToString();
    }

    public static GeneSet Deserialize(string? s)
    {
        var set = new GeneSet();
        if (string.IsNullOrEmpty(s)) return set;
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string name = part[..eq];
            string body = part[(eq + 1)..];
            bool morph = body.StartsWith('~');
            var f = (morph ? body[1..] : body).Split(',');
            if (f.Length != 4) continue;
            if (morph)
            {
                if (int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) &&
                    int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rv))
                    set.Morphs[name] = new MorphGene(f[0], v, f[2], rv);
            }
            else if (int.TryParse(f[0], out int x) && int.TryParse(f[1], out int y) &&
                     int.TryParse(f[2], out int rx) && int.TryParse(f[3], out int ry))
            {
                set.Colors[name] = new ColorGene(x, y, rx, ry);
            }
        }
        return set;
    }

    private static int Byte(long v) => (int)Math.Clamp(v, 0, 255);
}
