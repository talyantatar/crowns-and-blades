// The bridge/data tables this component reads at runtime (all tunables live there).
//   bl_skins_human.json        Bannerlord colour gradients and hair/beard style tags (per gender, adult)
//   ck3_gene_to_bl_face.json   CK3 morph gene -> Bannerlord deform key (+ direction)
//   bl_ruler_kits.json         ruler/lord battle rosters, heraldic shields, banner items
//   bl_catalog.json            item tiers/cultures (lower-tier armour for barons and landless knights), culture banner colours
//   bl_banner_colors.json      the 229-colour banner palette
//   ck3_named_colors_to_bl.json CK3 named CoA colours -> palette ids
//   ck3_emblem_to_bl_icon.json CK3 emblem keywords -> banner icon ids (created by this component)
//   mapping.json               CK3 culture/heritage/unit_gfx -> Bannerlord culture
//   ck3_hair_to_bl.json        optional override of the built-in hair/beard table (Tables/ck3_hair_to_bl.json)
using System.Reflection;
using System.Text.Json;
using CK3xBL.Appearance.Genes;
using CK3xBL.Core.Config;

namespace CK3xBL.Appearance.Data;

internal sealed class BlSkin
{
    public readonly List<Rgb> SkinGradient = new();
    public readonly List<Rgb> HairGradient = new();
    public readonly List<Rgb> EyeGradient = new();
    public readonly List<(int Index, string Tag)> Hairs = new();
    public readonly List<(int Index, string Tag)> Beards = new();

    public int? HairIndex(string tag) => Find(Hairs, tag);
    public int? BeardIndex(string tag) => Find(Beards, tag);

    private static int? Find(List<(int Index, string Tag)> list, string tag)
    {
        foreach (var (i, t) in list)
            if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }
}

internal sealed record MorphMapEntry(string Ck3Gene, string BlKey, int Dir);

internal sealed record KitItem(string Slot, string Id, int Tier, bool Heraldic);

internal sealed class KitRoster
{
    public required string Id;
    public string Culture = "";
    public readonly List<string> Flags = new();
    public readonly List<List<KitItem>> Sets = new();
}

internal sealed record CatalogArmor(string Id, string Culture, string Type, int Tier, int UsedByTroops);

internal sealed record ShieldItem(string Id, string? Culture, int Tier);

internal sealed record BannerItem(string Id, string Culture);

/// <summary>
/// Team colours of a Bannerlord culture banner: the module builds it with new Banner(culture.Banner, culture.Color,
/// culture.Color2), i.e. Color is the background (primary) and Color2 every icon layer.
/// </summary>
internal sealed record CultureBanner(Rgb Primary, Rgb Icon);

internal sealed class BridgeData
{
    public string Directory { get; }
    private readonly Action<string> _log;

    private BridgeData(string dir, Action<string> log) { Directory = dir; _log = log; }

    public static BridgeData? Open(string? configuredDir, Action<string> log)
    {
        string? dir = FindDataDirectory(configuredDir);
        if (dir == null)
        {
            log($"bridge/data not found (configured '{configuredDir}', base '{AppContext.BaseDirectory}')");
            return null;
        }
        return new BridgeData(dir, log);
    }

    /// <summary>Configured dir, CK3XBL_DATA, CK3XBL_BRIDGE_ROOT/data, &lt;exe&gt;/data, or the nearest ancestor with bridge/data or data.</summary>
    public static string? FindDataDirectory(string? configured)
    {
        static bool Ok(string? d) => !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "bl_skins_human.json"));
        if (Ok(configured)) return Path.GetFullPath(configured!);
        string? env = Environment.GetEnvironmentVariable("CK3XBL_DATA");
        if (Ok(env)) return Path.GetFullPath(env!);
        if (BridgePaths.RootFromEnvironment() is { } root && Ok(Path.Combine(root, "data"))) return Path.Combine(root, "data");
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            foreach (var cand in new[] { Path.Combine(d.FullName, "data"), Path.Combine(d.FullName, "bridge", "data") })
                if (Ok(cand)) return cand;
        }
        return null;
    }

    private JsonDocument? Json(string file)
    {
        string path = Path.Combine(Directory, file);
        if (!File.Exists(path)) { _log($"data file missing: {file}"); return null; }
        try
        {
            return JsonDocument.Parse(File.ReadAllBytes(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            _log($"data file unreadable: {file}: {e.Message}");
            return null;
        }
    }

    /// <summary>A table from bridge/data, else the copy built into this assembly (Tables/*.json).</summary>
    private JsonDocument? JsonWithBuiltIn(string file)
    {
        if (File.Exists(Path.Combine(Directory, file))) return Json(file);
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CK3xBL.Appearance.Tables." + file);
        if (s == null) return null;
        return JsonDocument.Parse(s, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
    }

    // ------------------------------------------------------------------ skins

    public BlSkin[] LoadSkins()
    {
        var skins = new[] { new BlSkin(), new BlSkin() };
        using var doc = Json("bl_skins_human.json");
        if (doc == null) return skins;
        foreach (var race in doc.RootElement.GetProperty("races").EnumerateArray())
        {
            if (race.GetProperty("id").GetString() != "human") continue;
            foreach (var skin in race.GetProperty("skins").EnumerateArray())
            {
                if (skin.GetProperty("maturity").GetString() != "adult") continue;
                int gender = skin.GetProperty("gender").GetInt32();
                if (gender is < 0 or > 1) continue;
                var s = skins[gender];
                ReadGradient(skin, "skin_color_gradient_points", s.SkinGradient);
                ReadGradient(skin, "hair_color_gradient_points", s.HairGradient);
                ReadGradient(skin, "eye_color_gradient_points", s.EyeGradient);
                ReadTags(skin, "hair_meshes", s.Hairs);
                ReadTags(skin, "beard_meshes", s.Beards);
            }
        }
        return skins;
    }

    private static void ReadGradient(JsonElement skin, string name, List<Rgb> into)
    {
        if (!skin.TryGetProperty(name, out var arr)) return;
        foreach (var p in arr.EnumerateArray())
        {
            var v = p.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            if (v.Length >= 3) into.Add(Rgb.FromDoubles(v[0], v[1], v[2]));
        }
    }

    private static void ReadTags(JsonElement skin, string name, List<(int, string)> into)
    {
        if (!skin.TryGetProperty(name, out var arr)) return;
        foreach (var m in arr.EnumerateArray())
        {
            if (!m.TryGetProperty("tags", out var tags) || tags.GetArrayLength() == 0) continue;
            into.Add((m.GetProperty("index").GetInt32(), tags[0].GetString() ?? ""));
        }
    }

    // ------------------------------------------------------------------ face morphs

    public List<MorphMapEntry> LoadMorphMap()
    {
        var list = new List<MorphMapEntry>();
        using var doc = Json("ck3_gene_to_bl_face.json");
        if (doc == null || !doc.RootElement.TryGetProperty("morphs", out var arr)) return list;
        foreach (var m in arr.EnumerateArray())
        {
            string? gene = m.GetProperty("ck3_gene").GetString(), key = m.GetProperty("bl_key").GetString();
            if (gene == null || key == null) continue;
            int dir = m.TryGetProperty("dir", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 1;
            list.Add(new MorphMapEntry(gene, key, dir == 0 ? 1 : Math.Sign(dir)));
        }
        return list;
    }

    // ------------------------------------------------------------------ equipment

    public (Dictionary<string, KitRoster> Rosters, List<ShieldItem> Shields, List<BannerItem> Banners) LoadKits()
    {
        var rosters = new Dictionary<string, KitRoster>(StringComparer.Ordinal);
        var shields = new List<ShieldItem>();
        var banners = new List<BannerItem>();
        using var doc = Json("bl_ruler_kits.json");
        if (doc == null) return (rosters, shields, banners);
        var root = doc.RootElement;
        if (root.TryGetProperty("ruler_and_noble_rosters", out var rs))
            foreach (var r in rs.EnumerateObject())
            {
                var roster = new KitRoster { Id = r.Name, Culture = Str(r.Value, "culture") ?? "" };
                if (r.Value.TryGetProperty("flags", out var flags))
                    roster.Flags.AddRange(flags.EnumerateArray().Select(f => f.GetString() ?? ""));
                foreach (var set in r.Value.GetProperty("sets").EnumerateArray())
                {
                    var items = new List<KitItem>();
                    foreach (var it in set.GetProperty("items").EnumerateArray())
                    {
                        string slot = Str(it, "slot") ?? "", id = Str(it, "id") ?? "";
                        if (slot.Length == 0 || id.Length == 0) continue;
                        int tier = it.TryGetProperty("tier", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
                        bool heraldic = it.TryGetProperty("heraldic", out var h) && h.ValueKind == JsonValueKind.True;
                        items.Add(new KitItem(slot, id, tier, heraldic));
                    }
                    roster.Sets.Add(items);
                }
                rosters[r.Name] = roster;
            }
        if (root.TryGetProperty("heraldic_shields", out var hs))
            foreach (var s in hs.EnumerateArray())
                shields.Add(new ShieldItem(Str(s, "id") ?? "", Str(s, "culture"),
                    s.TryGetProperty("tier", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0));
        if (root.TryGetProperty("banner_items", out var bi))
            foreach (var b in bi.EnumerateArray())
                banners.Add(new BannerItem(Str(b, "id") ?? "", Str(b, "culture") ?? ""));
        return (rosters, shields, banners);
    }

    /// <summary>
    /// bl_catalog.json: armour tiers/cultures, and the team colours of every Bannerlord culture banner (null for a
    /// culture the catalog lists without colours).
    /// </summary>
    public (List<CatalogArmor> Armors, Dictionary<string, CultureBanner?> CultureBanners) LoadCatalog()
    {
        var list = new List<CatalogArmor>();
        var cultures = new Dictionary<string, CultureBanner?>(StringComparer.Ordinal);
        using var doc = Json("bl_catalog.json");
        if (doc == null) return (list, cultures);
        var root = doc.RootElement;
        if (root.TryGetProperty("items", out var items) && items.TryGetProperty("armors", out var armors))
            foreach (var a in armors.EnumerateArray())
            {
                string? id = Str(a, "id"), culture = Str(a, "culture"), type = Str(a, "type");
                if (id == null || culture == null || type == null) continue;
                int tier = a.TryGetProperty("tier", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : -1;
                int used = a.TryGetProperty("used_by_troops", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : 0;
                list.Add(new CatalogArmor(id, culture, type, tier, used));
            }
        if (root.TryGetProperty("cultures", out var cs) && cs.ValueKind == JsonValueKind.Object)
            foreach (var c in cs.EnumerateObject())
                cultures[c.Name] = Argb(Str(c.Value, "color")) is Rgb primary && Argb(Str(c.Value, "color2")) is Rgb icon
                    ? new CultureBanner(primary, icon) : null;
        return (list, cultures);
    }

    /// <summary>Bannerlord ARGB hex ("0xff793191") -> RGB.</summary>
    private static Rgb? Argb(string? hex)
    {
        if (hex == null) return null;
        var digits = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex.AsSpan(2) : hex.AsSpan();
        if (!uint.TryParse(digits, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint v))
            return null;
        return new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    // ------------------------------------------------------------------ cultures

    public sealed class CultureTables
    {
        public readonly Dictionary<string, string> ByCulture = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> ByHeritage = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> ByUnitGfx = new(StringComparer.Ordinal);
        public string Default = "empire";
    }

    public CultureTables LoadCultureTables()
    {
        var t = new CultureTables();
        using var doc = Json("mapping.json");
        if (doc == null) return t;
        var root = doc.RootElement;
        void Map(string prop, Dictionary<string, string> into)
        {
            if (!root.TryGetProperty(prop, out var o) || o.ValueKind != JsonValueKind.Object) return;
            foreach (var kv in o.EnumerateObject())
                if (kv.Value.ValueKind == JsonValueKind.String) into[kv.Name] = kv.Value.GetString()!;
        }
        Map("culture_map", t.ByCulture);
        Map("heritage_map", t.ByHeritage);
        Map("unit_gfx_map", t.ByUnitGfx);
        if (Str(root, "default_culture") is string def) t.Default = def;
        return t;
    }

    // ------------------------------------------------------------------ banners

    public List<(int Id, Rgb Rgb)> LoadBannerPalette()
    {
        var list = new List<(int, Rgb)>();
        using var doc = Json("bl_banner_colors.json");
        if (doc == null) return list;
        foreach (var c in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var rgb = c.GetProperty("rgb").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            if (rgb.Length >= 3) list.Add((c.GetProperty("id").GetInt32(), new Rgb((byte)rgb[0], (byte)rgb[1], (byte)rgb[2])));
        }
        return list;
    }

    public Dictionary<string, int> LoadNamedColorMap()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var doc = Json("ck3_named_colors_to_bl.json");
        if (doc == null) return map;
        foreach (var c in doc.RootElement.GetProperty("items").EnumerateArray())
            if (Str(c, "ck3") is string name && c.TryGetProperty("bl_nearest", out var n) && n.TryGetProperty("id", out var id))
                map[name] = id.GetInt32();
        return map;
    }

    public JsonDocument? EmblemIconTable() => Json("ck3_emblem_to_bl_icon.json");

    public JsonDocument? HairTable() => JsonWithBuiltIn("ck3_hair_to_bl.json");

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
