// A coat of arms in CK3 terms: pattern + up to 3 colours + emblems with instances.
// Built from save text, binary meta (calibrated tokens) or game files; consumed by the banner builder and the
// PNG renderer, and stored in the appearance cache (so it is a plain serialisable DTO).
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CK3xBL.Appearance.Genes;

namespace CK3xBL.Appearance.Coa;

internal sealed class CoaColor
{
    public string? Name { get; set; }     // named colour ("red"), null for literal rgb/hsv
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }

    [JsonIgnore]
    internal Rgb Rgb => new(R, G, B);
    internal static CoaColor From(string? name, Rgb c) => new() { Name = name, R = c.R, G = c.G, B = c.B };
    public override string ToString() => Name ?? $"rgb({R},{G},{B})";
}

internal sealed class CoaInstance
{
    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.5;
    public double ScaleX { get; set; } = 1;
    public double ScaleY { get; set; } = 1;
    public double Rotation { get; set; }     // degrees
    public double Depth { get; set; }
}

internal sealed class CoaEmblem
{
    public bool Textured { get; set; }                       // textured_emblem (drawn as is) vs colored_emblem
    public string Texture { get; set; } = "";                // file name without ".dds"
    public CoaColor?[] Colors { get; set; } = new CoaColor?[3];
    public List<CoaInstance> Instances { get; set; } = new();
    public int[]? Mask { get; set; }                         // pattern channels (1..3) the emblem is clipped to
}

/// <summary>Quartering: another coat of arms drawn into sub-rectangles (offset = top-left, scale = size, 0..1).</summary>
internal sealed class CoaSub
{
    public CoaModel Coa { get; set; } = new();
    public List<CoaInstance> Instances { get; set; } = new();
}

internal sealed class CoaModel
{
    public string Pattern { get; set; } = "pattern_solid";   // without ".dds"
    public CoaColor?[] Colors { get; set; } = new CoaColor?[3];
    public List<CoaEmblem> Emblems { get; set; } = new();
    public List<CoaSub> Subs { get; set; } = new();
    /// <summary>Where it came from: "meta:primary_title", "title:c_x", "house:123", "game:k_france", ...</summary>
    public string Source { get; set; } = "";

    [JsonIgnore]
    public bool IsEmpty => Emblems.Count == 0 && Subs.Count == 0 && Colors.All(c => c == null);

    /// <summary>Stable content hash (source excluded), used for PNG file names.</summary>
    public string ContentHash()
    {
        var sb = new StringBuilder();
        Append(sb, this);
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static void Append(StringBuilder sb, CoaModel c)
    {
        sb.Append(c.Pattern).Append('|');
        foreach (var col in c.Colors) sb.Append(col == null ? "-" : $"{col.R},{col.G},{col.B}").Append('|');
        foreach (var e in c.Emblems)
        {
            sb.Append(e.Textured ? 'T' : 'C').Append(e.Texture).Append('|');
            foreach (var col in e.Colors) sb.Append(col == null ? "-" : $"{col.R},{col.G},{col.B}").Append('|');
            if (e.Mask != null) sb.Append("m").Append(string.Join(",", e.Mask)).Append('|');
            foreach (var i in e.Instances)
                sb.Append(FormattableString.Invariant($"{i.X:0.###},{i.Y:0.###},{i.ScaleX:0.###},{i.ScaleY:0.###},{i.Rotation:0.#},{i.Depth:0.##}|"));
        }
        foreach (var s in c.Subs)
        {
            sb.Append("sub{");
            Append(sb, s.Coa);
            foreach (var i in s.Instances) sb.Append(FormattableString.Invariant($"{i.X:0.###},{i.Y:0.###},{i.ScaleX:0.###},{i.ScaleY:0.###}|"));
            sb.Append('}');
        }
    }
}
