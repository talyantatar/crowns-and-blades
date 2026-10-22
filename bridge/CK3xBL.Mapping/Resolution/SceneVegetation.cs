// Vegetation of the Bannerlord field scenes: how wooded
// the playable area is and with what, so a temperate forest or a "Fields before X" relief battle does not open in a dense spruce
// forest. terrain_scenes.json scene_info carries the tag of every field scene; the tags are derived here from the scene files
// (read-only) and the pools are curated to the rules below, which SelfCheck verifies.
//
// Derivation [I, judged on in-game screenshots]. Trees are the flora kinds flagged "tree" in Native/ModuleData/flora_kinds.xml
// (shrubs, far billboards, dead, dry, broken, cut, young and sprout kinds left out); a kind is a conifer when its summer variations
// are spruce or pine meshes, else broadleaf. Inside the playable area (the hull of the border_soft / border_hard / walk_area_vertex
// entities of scene.xscene, else the whole terrain) two amounts are summed per kind:
//   painted: the summer mesh density of each terrain layer x the share of the area's 200 m terrain nodes whose summer layer mask
//            uses that layer, with "*_field" kinds (scattered trees) at half weight;
//   placed:  the trees of flora.bin standing in the area, per km^2 / PlacedTreesPerPaintedUnit.
// The painted coverage inside a node is in terrain.bin (compressed, not read), so a painted layer counts as fully used where its
// node uses it. Scenes without files fall back to their culture: Sturgian/Battanian woods conifer_dense, other woods mixed, other
// scenes open.
//
// Pool rules: (2) the temperate ("any") and mediterranean forest pools hold no conifer_dense scene; (3) the temperate
// plains and farmlands pools hold only open / deciduous_open scenes; (4) a relief battle at a castle or town-kind holding in
// forest, taiga or jungle is fought on the farmlands pool of its climate (cleared land around the holding); (5) its detail reads
// "Fields before X" only for an open pool (PROTOCOL §13.7).
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CK3xBL.Mapping.Data;

namespace CK3xBL.Mapping.Resolution;

public static class SceneVegetation
{
    public const string ConiferDense = "conifer_dense";
    public const string Mixed = "mixed";
    public const string DeciduousOpen = "deciduous_open";
    public const string Open = "open";

    public static readonly IReadOnlyList<string> Tags = [ConiferDense, Mixed, DeciduousOpen, Open];

    /// <summary>flora.bin trees per km^2 that weigh as much as one unit of painted layer density [I].</summary>
    public const double PlacedTreesPerPaintedUnit = 100;
    /// <summary>Weight of a painted "*_field" kind (scattered trees) against a forest kind [I].</summary>
    public const double FieldKindWeight = 0.5;
    /// <summary>Tree amount below which a scene is open [I].</summary>
    public const double OpenBelow = 12;
    /// <summary>Conifer share from which a wooded scene is conifer_dense; below <see cref="BroadleafFrom"/> it is broadleaf [I].</summary>
    public const double ConiferFrom = 0.6;
    public const double BroadleafFrom = 0.4;
    /// <summary>Broadleaf amount from which the wood is closed (mixed) rather than deciduous_open [I].</summary>
    public const double ClosedWoodFrom = 30;

    /// <summary>Terrain pools whose scenes are open ground: their relief battles read "Fields before X".</summary>
    public static readonly IReadOnlySet<string> OpenPools = new HashSet<string>(StringComparer.Ordinal) { "farmlands", "plains", "steppe", "drylands" };

    /// <summary>Wooded terrains whose relief battles move to the farmlands pool (rule 4).</summary>
    public static readonly IReadOnlySet<string> ClearedForRelief = new HashSet<string>(StringComparer.Ordinal) { "forest", "taiga", "jungle" };

    private static readonly string[] SkippedTreeKinds = ["shrub", "far_", "dead", "dry_tree", "broken", "cutted", "young", "sprout", "visual_test"];

    public enum TreeKind { Conifer, Broadleaf }

    /// <summary>Tree amount of a playable area: painted layer density plus placed trees, in painted-density units.</summary>
    public sealed record TreeCover(double Conifer, double Broadleaf)
    {
        public double Total => Conifer + Broadleaf;
        public double ConiferShare => Total > 0 ? Conifer / Total : 0;
    }

    // ---- derivation ------------------------------------------------------------------------------------------------------

    /// <summary>The tree flora kinds of Native/ModuleData/flora_kinds.xml, as conifer or broadleaf.</summary>
    public static IReadOnlyDictionary<string, TreeKind> ReadTreeKinds(string floraKindsXmlPath)
    {
        var kinds = new Dictionary<string, TreeKind>(StringComparer.Ordinal);
        foreach (var kind in XDocument.Load(floraKindsXmlPath).Root!.Elements("flora_kind"))
        {
            string? name = (string?)kind.Attribute("name");
            bool tree = kind.Element("flags")?.Elements("flag").Any(f => (string?)f.Attribute("name") == "tree" && (string?)f.Attribute("value") == "true") ?? false;
            if (name is null || !tree) continue;
            var summer = kind.Elements("seasonal_kind").FirstOrDefault(s => (string?)s.Attribute("season") == "summer");
            var variations = (summer ?? kind).Descendants("flora_variation").Select(v => (string?)v.Attribute("name") ?? "").ToList();
            string all = name + " " + string.Join(" ", variations);
            if (SkippedTreeKinds.Any(s => all.Contains(s, StringComparison.Ordinal))) continue;
            kinds[name] = variations.Any(v => v.Contains("spruce", StringComparison.Ordinal) || v.Contains("pine", StringComparison.Ordinal))
                ? TreeKind.Conifer : TreeKind.Broadleaf;
        }
        return kinds;
    }

    /// <summary>Tree amount inside the playable area of a scene (scene.xscene text, flora.bin bytes or null).</summary>
    public static TreeCover Measure(string xsceneXml, byte[]? floraBin, IReadOnlyDictionary<string, TreeKind> kinds)
    {
        var scene = XDocument.Parse(xsceneXml).Root!;
        var terrain = scene.Element("terrain");
        int nx = Int(terrain?.Attribute("node_dimension_x")), ny = Int(terrain?.Attribute("node_dimension_y"));
        double nodeSize = Num(terrain?.Attribute("node_size"));
        double width = nx * nodeSize, height = ny * nodeSize;

        var border = new List<(double X, double Y)>();
        foreach (var e in scene.Element("entities")?.Elements("game_entity") ?? [])
        {
            string? prefab = (string?)e.Attribute("prefab");
            bool vertex = prefab is "border_soft" or "border_hard"
                || (e.Element("tags")?.Elements("tag").Any(t => ((string?)t.Attribute("name"))?.StartsWith("walk_area_vertex", StringComparison.Ordinal) ?? false) ?? false);
            if (vertex && Position(e) is { } p) border.Add(p);
        }
        var hull = border.Count >= 3 ? Hull(border) : [];
        double areaKm2 = hull.Count >= 3 ? Area(hull) / 1e6 : 0;
        if (areaKm2 < 0.01)
        {
            hull = [(0, 0), (width, 0), (width, height), (0, height)];
            areaKm2 = width * height / 1e6;
        }

        var nodes = terrain?.Element("nodes")?.Elements("node")
            .Select(n => (X: (Int(n.Attribute("idx")) + 0.5) * nodeSize, Y: (Int(n.Attribute("idy")) + 0.5) * nodeSize, Mask: Long(n.Attribute("layer_is_used_mask_summer"))))
            .ToList() ?? [];
        var inArea = nodes.Where(n => Inside(hull, n.X, n.Y)).ToList();
        if (inArea.Count == 0) inArea = nodes;

        double conifer = 0, broadleaf = 0;
        var layers = terrain?.Element("layers")?.Elements("layer").ToList() ?? [];
        for (int i = 0; i < layers.Count && i < 63; i++)
        {
            var meshes = layers[i].Element("summer")?.Descendants("mesh");
            if (meshes is null || inArea.Count == 0) continue;
            double share = inArea.Count(n => (n.Mask & (1L << i)) != 0) / (double)inArea.Count;
            foreach (var mesh in meshes)
            {
                string? kind = (string?)mesh.Attribute("name");
                if (kind is null || !kinds.TryGetValue(kind, out var tk)) continue;
                double amount = Num(mesh.Attribute("density")) * share * (kind.Contains("_field", StringComparison.Ordinal) ? FieldKindWeight : 1);
                if (tk == TreeKind.Conifer) conifer += amount; else broadleaf += amount;
            }
        }

        foreach (var (kind, x, y) in PlacedFlora(floraBin))
        {
            if (!kinds.TryGetValue(kind, out var tk) || !Inside(hull, x, y)) continue;
            double amount = 1 / areaKm2 / PlacedTreesPerPaintedUnit;
            if (tk == TreeKind.Conifer) conifer += amount; else broadleaf += amount;
        }
        return new TreeCover(Math.Round(conifer, 2), Math.Round(broadleaf, 2));
    }

    /// <summary>The vegetation tag of a measured scene.</summary>
    public static string Classify(TreeCover cover)
    {
        if (cover.Total < OpenBelow) return Open;
        if (cover.ConiferShare >= ConiferFrom) return ConiferDense;
        if (cover.ConiferShare >= BroadleafFrom) return Mixed;
        return cover.Total >= ClosedWoodFrom ? Mixed : DeciduousOpen;
    }

    /// <summary>Tag of a scene whose files cannot be read: Sturgian and Battanian woods are conifer forests, other woods mixed,
    /// anything not in a forest, taiga or jungle pool open.</summary>
    public static string CultureFallback(string? culture, bool wooded) =>
        !wooded ? Open : culture is "sturgia" or "battania" ? ConiferDense : Mixed;

    /// <summary>The SceneObj folder of a scene: the modules every bridge launch loads first, then the others in name order.</summary>
    public static string? SceneFolder(string modulesDir, string sceneId)
    {
        if (!Directory.Exists(modulesDir)) return null;
        var modules = MappingTables.LaunchedSceneModules.OrderBy(m => m, StringComparer.Ordinal)
            .Concat(Directory.GetDirectories(modulesDir).Select(Path.GetFileName).OfType<string>()
                .Where(m => !MappingTables.LaunchedSceneModules.Contains(m)).OrderBy(m => m, StringComparer.Ordinal));
        return modules.Select(m => Path.Combine(modulesDir, m, "SceneObj", sceneId))
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "scene.xscene")));
    }

    /// <summary>Vegetation tag of a scene from its files under <paramref name="modulesDir"/> (read-only), else the culture fallback.</summary>
    public static string Derive(string modulesDir, string sceneId, string? culture, bool wooded, IReadOnlyDictionary<string, TreeKind> kinds)
    {
        string? folder = SceneFolder(modulesDir, sceneId);
        if (folder is null) return CultureFallback(culture, wooded);
        string flora = Path.Combine(folder, "flora.bin");
        return Classify(Measure(Encoding.UTF8.GetString(DataFile.ReadAllBytes(Path.Combine(folder, "scene.xscene"))).TrimStart('﻿'),
            File.Exists(flora) ? DataFile.ReadAllBytes(flora) : null, kinds));
    }

    // flora.bin "FLR2": int32 payload size, int32 count, then per instance int32 name length, name, int32, 20 floats (a 4x4
    // frame whose fourth row is the position, then 4 scale/colour values).
    private static IEnumerable<(string Kind, double X, double Y)> PlacedFlora(byte[]? bin)
    {
        if (bin is null || bin.Length < 12 || Encoding.ASCII.GetString(bin, 0, 4) != "FLR2") yield break;
        int count = BinaryPrimitives.ReadInt32LittleEndian(bin.AsSpan(8)), at = 12;
        for (int i = 0; i < count && at + 4 <= bin.Length; i++)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(bin.AsSpan(at));
            if (len < 0 || len > 256 || at + 4 + len + 4 + 80 > bin.Length) yield break;
            string kind = Encoding.ASCII.GetString(bin, at + 4, len);
            int floats = at + 4 + len + 4;
            yield return (kind, BinaryPrimitives.ReadSingleLittleEndian(bin.AsSpan(floats + 48)), BinaryPrimitives.ReadSingleLittleEndian(bin.AsSpan(floats + 52)));
            at = floats + 80;
        }
    }

    private static (double X, double Y)? Position(XElement entity)
    {
        var parts = ((string?)entity.Element("transform")?.Attribute("position"))?.Split(',');
        return parts is { Length: >= 2 }
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ? (x, y) : null;
    }

    private static int Int(XAttribute? a) => int.TryParse((string?)a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static long Long(XAttribute? a) => long.TryParse((string?)a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static double Num(XAttribute? a) => double.TryParse((string?)a, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // Convex hull (monotone chain), counter-clockwise.
    private static List<(double X, double Y)> Hull(List<(double X, double Y)> points)
    {
        var p = points.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
        if (p.Count < 3) return p;
        static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var lower = new List<(double X, double Y)>();
        foreach (var q in p) { while (lower.Count >= 2 && Cross(lower[^2], lower[^1], q) <= 0) lower.RemoveAt(lower.Count - 1); lower.Add(q); }
        var upper = new List<(double X, double Y)>();
        for (int i = p.Count - 1; i >= 0; i--) { var q = p[i]; while (upper.Count >= 2 && Cross(upper[^2], upper[^1], q) <= 0) upper.RemoveAt(upper.Count - 1); upper.Add(q); }
        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private static bool Inside(List<(double X, double Y)> hull, double x, double y)
    {
        for (int i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];
            if ((b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X) < 0) return false;
        }
        return hull.Count >= 3;
    }

    private static double Area(List<(double X, double Y)> hull)
    {
        double s = 0;
        for (int i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];
            s += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(s) / 2;
    }

    // ---- pool rules ------------------------------------------------------------------------------------------------------

    /// <summary>Rule (4): the terrain whose scene pool a battle uses. A relief battle (siege here) at a castle or town-kind holding
    /// in forest, taiga or jungle is fought on the farmlands around the holding; every other battle uses its own terrain.</summary>
    /// <param name="terrain">CK3 terrain of the battle.</param>
    /// <param name="settlementKind">terrain_scenes.json siege.holding_kind of the holding: castle | town | village | field.</param>
    /// <param name="siegeHere">An army is besieging the holding (L|siege = yes).</param>
    public static string PoolTerrain(string terrain, string settlementKind, bool siegeHere) =>
        siegeHere && settlementKind is "castle" or "town" && ClearedForRelief.Contains(terrain) ? "farmlands" : terrain;

    /// <summary>Rule (5): a relief battle on this pool is "Fields before X" (open ground), else "&lt;terrain&gt; before X".</summary>
    public static bool IsOpenPool(string poolTerrain) => OpenPools.Contains(poolTerrain);

    /// <summary>Rules (2) and (3): may a scene with this tag stand in the pool of <paramref name="poolTerrain"/> for this climate
    /// bucket ("any" is the list the temperate bucket uses)?</summary>
    public static bool FitsPool(string poolTerrain, string bucket, string vegetation) => poolTerrain switch
    {
        "forest" when bucket is "any" or "temperate" or "mediterranean" => vegetation != ConiferDense,
        "plains" or "farmlands" when bucket is "any" or "temperate" => vegetation is Open or DeciduousOpen,
        _ => true,
    };

    /// <summary>scene id -> vegetation tag of terrain_scenes.json scene_info (scenes without a tag are left out).</summary>
    public static IReadOnlyDictionary<string, string> ReadTags(string terrainScenesJsonPath)
    {
        using var doc = JsonDocument.Parse(DataFile.ReadAllBytes(terrainScenesJsonPath));
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("scene_info", out var info))
            foreach (var scene in info.EnumerateObject())
                if (scene.Value.ValueKind == JsonValueKind.Object && scene.Value.TryGetProperty("vegetation", out var v) && v.GetString() is { } tag)
                    tags[scene.Name] = tag;
        return tags;
    }

    /// <summary>
    /// Headless self-check for the mapping tests: every scene of the forest, taiga, plains and farmlands pools carries a known tag,
    /// the pools obey rules (2) and (3) and keep enough scenes to pick from, rules (4) and (5) behave, the South Seaxe relief battle
    /// (province 1506, forest, castle, besieged) lands on an all-open temperate farmlands pool, and, when
    /// <paramref name="modulesDir"/> (the Bannerlord Modules folder) is readable, every stored tag equals a fresh derivation.
    /// Returns the failures; empty = pass.
    /// </summary>
    public static IReadOnlyList<string> SelfCheck(MappingTables tables, string? modulesDir)
    {
        var failures = new List<string>();
        void Check(bool ok, string what) { if (!ok) failures.Add(what); }
        var tags = ReadTags(Path.Combine(tables.Directory, "terrain_scenes.json"));
        var ts = tables.Terrain;
        int pick = Math.Max(1, ts.Rules.PickTopN);

        foreach (var terrain in new[] { "forest", "taiga", "plains", "farmlands" })
        {
            var buckets = ts.Terrain.GetValueOrDefault(terrain)?.Scenes?.Buckets;
            if (buckets is null) { failures.Add($"terrain_scenes.json has no {terrain} pool"); continue; }
            foreach (var (bucket, scenes) in buckets)
            {
                foreach (var id in scenes)
                {
                    if (!tags.TryGetValue(id, out var tag) || !Tags.Contains(tag)) { failures.Add($"{terrain}.{bucket}: {id} has no vegetation tag"); continue; }
                    Check(FitsPool(terrain, bucket, tag), $"{terrain}.{bucket}: {id} ({tag}) breaks the pool rule");
                }
            }
        }
        foreach (var (terrain, bucket) in new[] { ("forest", "any"), ("forest", "mediterranean"), ("plains", "any"), ("farmlands", "any") })
            Check((ts.Terrain.GetValueOrDefault(terrain)?.Scenes?.Buckets.GetValueOrDefault(bucket)?.Count ?? 0) >= pick,
                $"{terrain}.{bucket} keeps fewer than {pick} scenes");
        foreach (var tag in tags) Check(Tags.Contains(tag.Value), $"scene_info.{tag.Key}: unknown vegetation '{tag.Value}'");

        Check(PoolTerrain("forest", "castle", true) == "farmlands" && PoolTerrain("taiga", "town", true) == "farmlands" && PoolTerrain("jungle", "castle", true) == "farmlands",
            "relief battles in forest, taiga and jungle use the farmlands pool");
        Check(PoolTerrain("forest", "castle", false) == "forest" && PoolTerrain("forest", "village", true) == "forest" && PoolTerrain("hills", "castle", true) == "hills",
            "only castle/town relief battles in wooded terrain change pool");
        Check(IsOpenPool("farmlands") && IsOpenPool("steppe") && !IsOpenPool("forest") && !IsOpenPool("hills"), "open pools are farmlands, plains, steppe and drylands");
        Check(Classify(new TreeCover(10, 2.5)) == ConiferDense && Classify(new TreeCover(9.8, 0)) == Open && Classify(new TreeCover(0, 25)) == DeciduousOpen
            && Classify(new TreeCover(0, 34)) == Mixed && Classify(new TreeCover(12, 14)) == Mixed, "classification thresholds");
        Check(CultureFallback("battania", true) == ConiferDense && CultureFallback("empire", true) == Mixed && CultureFallback("sturgia", false) == Open,
            "culture fallback");

        // South Seaxe (Hastings test b1_7018): forest, castle, besieged, temperate -> farmlands pool of open scenes, "Fields before".
        string seaxeTerrain = tables.Provinces.TryGetValue(1506, out var seaxe) ? seaxe.Terrain : "";
        string seaxeBucket = tables.Climates.TryGetValue(1506, out var climate) ? climate.Bucket : "";
        string seaxePool = PoolTerrain(seaxeTerrain, ts.Siege.HoldingKind.GetValueOrDefault("castle_holding") ?? "", true);
        Check(seaxeTerrain == "forest" && seaxeBucket == "temperate" && seaxePool == "farmlands" && IsOpenPool(seaxePool),
            $"South Seaxe: terrain {seaxeTerrain}, bucket {seaxeBucket}, pool {seaxePool}");
        var seaxeScenes = ts.Terrain.GetValueOrDefault(seaxePool)?.Scenes?.Buckets.GetValueOrDefault("any") ?? [];
        Check(seaxeScenes.Count > 0 && seaxeScenes.All(id => tags.GetValueOrDefault(id) is Open or DeciduousOpen), "South Seaxe: the temperate farmlands pool is all open ground");

        if (modulesDir is not null && File.Exists(Path.Combine(modulesDir, "Native", "ModuleData", "flora_kinds.xml")))
        {
            var kinds = ReadTreeKinds(Path.Combine(modulesDir, "Native", "ModuleData", "flora_kinds.xml"));
            var wooded = new[] { "forest", "taiga", "jungle" }
                .SelectMany(t => ts.Terrain.GetValueOrDefault(t)?.Scenes?.Buckets.Values ?? Enumerable.Empty<List<string>>())
                .SelectMany(l => l).ToHashSet(StringComparer.Ordinal);
            foreach (var (id, tag) in tags)
            {
                if (SceneFolder(modulesDir, id) is null) continue;
                string derived = Derive(modulesDir, id, null, wooded.Contains(id), kinds);
                Check(derived == tag, $"scene_info.{id}: stored vegetation {tag}, the scene files give {derived}");
            }
        }
        return failures;
    }
}
