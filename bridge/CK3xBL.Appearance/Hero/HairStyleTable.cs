// CK3 hairstyle/beard accessories -> Bannerlord style tags, driven by Tables/ck3_hair_to_bl.json (overridable from
// bridge/data). Matching uses the accessory name plus its comment in the gene files ("Short Pompadour").
using System.Text.Json;

namespace CK3xBL.Appearance.Hero;

internal sealed class HairStyleTable
{
    private sealed record Rule(string[] Keywords, string[] Exclude, string[] Male, string[] Female);

    private readonly Dictionary<string, string> _families = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _beardFamilies = new(StringComparer.Ordinal);
    private readonly List<Rule> _hair = new();
    private readonly List<Rule> _beard = new();
    private readonly Dictionary<string, string[]> _balding = new(StringComparer.Ordinal);
    private string[] _hairDefaultMale = { "Short" }, _hairDefaultFemale = { "TiedInBack" }, _beardDefault = { "LightShortBeard" };
    private string _defaultFamily = "western";

    public double BeardChance { get; private set; } = 0.7;
    public int BeardMinAge { get; private set; } = 18;

    public static HairStyleTable Load(JsonDocument? doc)
    {
        var t = new HairStyleTable();
        if (doc == null) return t;
        var root = doc.RootElement;
        if (root.TryGetProperty("hairstyle_families", out var fam))
            foreach (var kv in fam.EnumerateObject())
                if (kv.Value.ValueKind == JsonValueKind.String)
                {
                    if (kv.Name == "default") t._defaultFamily = kv.Value.GetString()!;
                    else if (kv.Name != "comment") t._families[kv.Name] = kv.Value.GetString()!;
                }
        if (root.TryGetProperty("beard_families", out var bf))
            foreach (var kv in bf.EnumerateObject())
                if (kv.Value.ValueKind == JsonValueKind.String && kv.Name != "comment") t._beardFamilies[kv.Name] = kv.Value.GetString()!;
        ReadRules(root, "hair_rules", t._hair);
        ReadRules(root, "beard_rules", t._beard);
        if (root.TryGetProperty("hair_default", out var hd))
        {
            t._hairDefaultMale = Strings(hd, "male") is { Length: > 0 } m ? m : t._hairDefaultMale;
            t._hairDefaultFemale = Strings(hd, "female") is { Length: > 0 } f ? f : t._hairDefaultFemale;
        }
        if (root.TryGetProperty("beard_default", out var bd) && Strings(bd, "male") is { Length: > 0 } b) t._beardDefault = b;
        if (root.TryGetProperty("balding", out var bal))
            foreach (var kv in bal.EnumerateObject())
                if (kv.Value.ValueKind == JsonValueKind.Object) t._balding[kv.Name] = Strings(kv.Value, "male");
        if (root.TryGetProperty("beard_chance", out var bc))
        {
            if (bc.TryGetProperty("beard", out var p) && p.ValueKind == JsonValueKind.Number) t.BeardChance = p.GetDouble();
            if (bc.TryGetProperty("min_age", out var a) && a.ValueKind == JsonValueKind.Number) t.BeardMinAge = a.GetInt32();
        }
        return t;
    }

    private static void ReadRules(JsonElement root, string name, List<Rule> into)
    {
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var r in arr.EnumerateArray())
            into.Add(new Rule(Strings(r, "keywords"), Strings(r, "exclude"), Strings(r, "male"), Strings(r, "female")));
    }

    private static string[] Strings(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : Array.Empty<string>();

    /// <summary>Hairstyle template family for a culture's clothing gfx ("indian_clothing_gfx" -> "indian").</summary>
    public string Family(string? clothingGfx) =>
        clothingGfx != null && _families.TryGetValue(clothingGfx, out var f) ? f : _defaultFamily;

    public string BeardFamily(string family) => _beardFamilies.TryGetValue(family, out var f) ? f : "western";

    public string[] HairTags(string accessory, string? comment, bool female) =>
        Match(_hair, accessory, comment, female) ?? (female ? _hairDefaultFemale : _hairDefaultMale);

    public string[] BeardTags(string accessory, string? comment) =>
        Match(_beard, accessory, comment, female: false) ?? _beardDefault;

    /// <summary>Tags that replace the hairstyle for a balding stage ("baldness_stage_2"), empty = keep the hairstyle.</summary>
    public string[] BaldingTags(string stage) => _balding.TryGetValue(stage, out var t) ? t : Array.Empty<string>();

    private static string[]? Match(List<Rule> rules, string accessory, string? comment, bool female)
    {
        string text = (accessory + " " + (comment ?? "")).ToLowerInvariant();
        string note = (comment ?? "").ToLowerInvariant();
        foreach (var r in rules)
        {
            if (!r.Keywords.Any(k => text.Contains(k, StringComparison.Ordinal))) continue;
            if (r.Exclude.Any(x => note.Contains(x, StringComparison.Ordinal))) continue;
            var tags = female ? r.Female : r.Male;
            if (tags.Length > 0) return tags;
        }
        return null;
    }
}
