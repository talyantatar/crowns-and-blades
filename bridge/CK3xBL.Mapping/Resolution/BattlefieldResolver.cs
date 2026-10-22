// Port of tools/data/battlefield_resolver.js resolveBattlefield:
// CK3 location facts -> Bannerlord scene (field or village), season, hour, weather and atmosphere. Deterministic:
// every decision hashes "<provinceId>|<Y.M.D>|<battle seq>|<decision>", so the same battle always gets the same field and sky.
//
// Sieges stay in CK3 (PROTOCOL §0): every battle is a Bannerlord field battle. A relief battle at a besieged castle or town gets
// the field scene of the place (the reference's "field" mode); one at a besieged tribal holding gets a village scene of the
// place's architecture (the reference's "relief" mode for villages). The reference's castle/town siege scenes are never used.
// In forest, taiga or jungle the castle/town relief battle is fought on the cleared land around the holding: the farmlands pool
// of its climate, with no chance-based coast or river lead (SceneVegetation.PoolTerrain).
//
// Differences from the reference, all for inputs the reference never sees:
//   * the CK3-logged terrain key wins over the static index (modded maps); unknown keys fall back to plains;
//   * a province missing from the static index/climate tables gets a synthetic record from the logged coastal/riverside flags
//     and the mapping.json climate region, instead of throwing;
//   * crossings need the 11 MB neighbour index, which is not shipped, so the caller passes Crossing.None unless it knows better;
//   * scene ids are checked against bl_scenes.json as well as terrain_scenes.json scene_info, and must ship in a module the bridge
//     always launches (MappingTables.LaunchedSceneModules): most village scenes live in SandBox, which the normal launch omits.
using System.Globalization;
using CK3xBL.Mapping.Data;

namespace CK3xBL.Mapping.Resolution;

public enum Crossing { None, River, MajorRiver, Strait }

public sealed class BattlefieldInput
{
    public long ProvinceId { get; init; }
    public int Year { get; init; } = 1066;
    public int Month { get; init; } = 9;
    public int Day { get; init; } = 15;
    public int BattleSeq { get; init; }
    /// <summary>CK3 terrain key as logged (L|terrain); null = take it from the province index.</summary>
    public string? Terrain { get; init; }
    /// <summary>CK3 winter modifier level: none | mild | normal | harsh; null = not logged (date + winter bias decide).</summary>
    public string? Winter { get; init; }
    /// <summary>CK3 holding type key of the province (castle_holding, ...), or null/none.</summary>
    public string? Holding { get; init; }
    /// <summary>building_gfx of the place's culture (settlement architecture).</summary>
    public string? BuildingGfx { get; init; }
    public string? Heritage { get; init; }
    /// <summary>An army is besieging the holding here: the battle is a relief battle before the settlement.</summary>
    public bool SiegeHere { get; init; }
    /// <summary>Logged L|coastal / L|riverside, used only when the province is missing from the static index.</summary>
    public bool Coastal { get; init; }
    public bool Riverside { get; init; }
    /// <summary>mapping.json climate region key (L|region), used only when the province is missing from the climate table.</summary>
    public string? ClimateRegion { get; init; }
    public Crossing Crossing { get; init; } = Crossing.None;
    /// <summary>Agents in the battle (both sides, heroes included); big battles avoid small scenes.</summary>
    public int Agents { get; init; }
}

public sealed class BattlefieldChoice
{
    /// <summary>field | village. Both open as Bannerlord field battles; a village scene is a custom-battle village map.</summary>
    public required string Kind { get; init; }
    public required string Scene { get; init; }
    public required IReadOnlyList<string> Candidates { get; init; }
    public required string ListSource { get; init; }
    /// <summary>The holding here (castle, town or tribal village) is besieged: a relief battle. Only wording depends on it.</summary>
    public bool Relief { get; init; }

    public required string Terrain { get; init; }
    /// <summary>Terrain whose scene pool the field scene came from: <see cref="Terrain"/>, or farmlands for a relief battle at a
    /// castle or town in wooded terrain (SceneVegetation.PoolTerrain).</summary>
    public required string PoolTerrain { get; init; }
    public required string Climate { get; init; }
    public string? Region { get; init; }
    public string? County { get; init; }
    public bool Coastal { get; init; }
    public required string TerrainTypeName { get; init; }
    public int TerrainType { get; init; }

    public required string Season { get; init; }
    public int SeasonId { get; init; }
    public bool WinterActive { get; init; }
    /// <summary>Start hour for an own opener (quarter hours inside the season's daylight window).</summary>
    public double Hour { get; init; }
    /// <summary>Nearest stock custom-battle hour (6/12/15/18/22): what the stock opener can show.</summary>
    public int LegacyTimeOfDay { get; init; }
    public required string Weather { get; init; }
    public required string InterpolatedAtmosphere { get; init; }
    public required string AtmospherePreset { get; init; }
    public bool PresetProven { get; init; }
    public double Rain { get; init; }
    public double Snow { get; init; }
    public double FogDensity { get; init; }
    public IReadOnlyList<double>? FogColor { get; init; }
    public double DrynessFactor { get; init; }
    public required IReadOnlyList<string> Why { get; init; }
}

public sealed class BattlefieldResolver
{
    private readonly MappingTables _t;
    private TerrainScenesJson Ts => _t.Terrain;

    public BattlefieldResolver(MappingTables tables) => _t = tables;

    private sealed class Ctx
    {
        public required ProvinceFacts P;
        public required string Terrain;
        public required string Pool;
        public required string Bucket;
        public required string? Winter;
        public required double SnowPct;
        public required int Month, Day;
        public required string Seed;
        public required BattlefieldInput Input;
        public double Hash(string decision) => JsCompat.Hash01(Seed + "|" + decision);
    }

    public BattlefieldChoice Resolve(BattlefieldInput input)
    {
        var p = ProvinceFor(input);
        _t.Climates.TryGetValue((int)input.ProvinceId, out var cl);
        string bucket = cl?.Bucket ?? FallbackBucket(input.ClimateRegion, p.Terrain);
        string terrain = input.Terrain is { Length: > 0 } t && Ts.Terrain.ContainsKey(t) ? t : Ts.Terrain.ContainsKey(p.Terrain) ? p.Terrain : "plains";
        string settlement = Ts.Siege.HoldingKind.GetValueOrDefault(input.Holding ?? "none") ?? "field";
        bool relief = input.SiegeHere && settlement != "field";
        string pool = SceneVegetation.PoolTerrain(terrain, settlement, input.SiegeHere);
        var ctx = new Ctx
        {
            P = p, Terrain = terrain, Pool = pool, Bucket = bucket, Winter = string.IsNullOrEmpty(input.Winter) ? null : input.Winter,
            SnowPct = p.SnowPct, Month = Math.Clamp(input.Month, 1, 12), Day = Math.Clamp(input.Day, 1, 31),
            Seed = $"{input.ProvinceId}|{input.Year}.{input.Month}.{input.Day}|{input.BattleSeq}", Input = input,
        };

        var (season, winterActive, seasonWhy) = ResolveSeason(ctx);
        double hour = ResolveHour(ctx, season);
        string weather = ResolveWeather(ctx, season, hour);
        var atmo = Ts.Weather.Atmosphere.GetValueOrDefault(weather) ?? new AtmosphereRow();

        var village = relief && settlement == "village" ? ResolveVillage(ctx) : null;
        var field = village is null ? ResolveField(ctx, season) : null;

        string blTerrain = season == "winter" ? "Snow" : Ts.Terrain.GetValueOrDefault(pool)?.BlTerrainType ?? "Plain";
        int legacyHour = NearestLegacyHour(hour);
        var preset = PresetFor(hour, weather);
        var why = new List<string> { seasonWhy };
        if (relief) why.Add($"relief battle at a besieged {settlement} (field battle: sieges stay in CK3)");
        if (pool != terrain) why.Add($"{terrain} around a besieged {settlement}: the {pool} scene pool (cleared land around the holding)");
        why.AddRange(field?.Why ?? new List<string> { $"village scene, {village!.Architecture} architecture" });

        return new BattlefieldChoice
        {
            Kind = village is not null ? "village" : "field",
            Scene = village?.Scene ?? field!.Scene,
            Candidates = village?.Candidates ?? field!.Candidates,
            ListSource = village is not null ? "village" : field!.Source,
            Relief = relief,
            Terrain = terrain,
            PoolTerrain = pool,
            Climate = bucket,
            Region = cl?.Region,
            County = cl?.County,
            Coastal = p.IsCoastal,
            TerrainTypeName = blTerrain,
            TerrainType = Ts.BlTerrainTypeIds.GetValueOrDefault(blTerrain, 1),
            Season = season,
            SeasonId = Ts.Season.BlIds.GetValueOrDefault(season, 1),
            WinterActive = winterActive,
            Hour = hour,
            LegacyTimeOfDay = legacyHour,
            Weather = weather,
            InterpolatedAtmosphere = atmo.Interpolated,
            AtmospherePreset = preset.Name,
            PresetProven = preset.Proven,
            Rain = atmo.Rain,
            Snow = atmo.Snow,
            FogDensity = atmo.Fog,
            FogColor = atmo.FogColor,
            DrynessFactor = atmo.Dryness ?? (bucket == "arid" ? 0.8 : 0.3),
            Why = why,
        };
    }

    private ProvinceFacts ProvinceFor(BattlefieldInput input)
    {
        if (_t.Provinces.TryGetValue((int)input.ProvinceId, out var p)) return p;
        return new ProvinceFacts
        {
            Id = (int)input.ProvinceId,
            Terrain = input.Terrain ?? "plains",
            Flags = (input.Coastal ? 1 : 0) | (input.Riverside ? 4 : 0),
            Relief7Raw = null,
        };
    }

    // ck3_province_climate.json covers every land province; this only runs for unknown (modded) provinces: the v0 mapping.json
    // climate (region, else terrain default) translated to terrain_scenes buckets through climate.mapping_json_alias.
    private string FallbackBucket(string? region, string terrain)
    {
        var m = _t.Mapping;
        string climate = m.ClimateRegions.FirstOrDefault(c => c.Region == region)?.Climate
                         ?? m.TerrainDefaultClimate.GetValueOrDefault(terrain) ?? m.ClimateDefault;
        return Ts.Climate.MappingJsonAlias.GetValueOrDefault(climate, "temperate");
    }

    // ---------- season ----------

    private static int MonthDay(int m, int d) => m * 100 + d;

    private static int ParseMonthDay(string s)
    {
        var parts = s.Split('-');
        return MonthDay(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    private static bool InWindow(int md, int from, int to) => from <= to ? md >= from && md <= to : md >= from || md <= to;

    private (string Season, bool WinterActive, string Why) ResolveSeason(Ctx ctx)
    {
        var s = Ts.Season;
        int md = MonthDay(ctx.Month, ctx.Day);
        double wb = ctx.P.WinterBias;
        bool winterActive = false;
        string why;
        if (ctx.SnowPct >= s.PermanentSnowPct)
        {
            winterActive = true;
            why = $"permanent snow {ctx.SnowPct}% at locator";
        }
        else if (ctx.Winter is not null)
        {
            if (ctx.Winter is "harsh" or "normal") { winterActive = true; why = $"CK3 winter_{ctx.Winter}_modifier"; }
            else if (ctx.Winter == "mild")
            {
                winterActive = s.MildWinterBuckets.Contains(ctx.Bucket) || wb >= 0.6;
                why = $"CK3 winter_mild_modifier, bucket {ctx.Bucket}, bias {wb}";
            }
            else why = "CK3 winter_none";
        }
        else
        {
            var row = s.WinterWindowByBias.FirstOrDefault(r => wb >= r.Min);
            if (row is { From: not null, To: not null } && InWindow(md, ParseMonthDay(row.From), ParseMonthDay(row.To)))
            {
                bool always = row.Chance.ValueKind == System.Text.Json.JsonValueKind.Number && row.Chance.GetDouble() == 1;
                winterActive = always || ctx.Hash("winter") < wb;
            }
            why = $"no winter logged: bias {wb} window {(row is { From: not null } ? row.From + ".." + row.To : "none")}";
        }

        string season;
        if (winterActive) season = "winter";
        else
        {
            var table = s.Base.GetValueOrDefault(ctx.Bucket) ?? s.Base["default"];
            season = table[0][1];
            foreach (var entry in table) if (md >= ParseMonthDay(entry[0])) season = entry[1];
        }
        return (season, winterActive, why);
    }

    // ---------- time of day ----------

    private double ResolveHour(Ctx ctx, string season)
    {
        var d = Ts.TimeOfDay.Daylight.GetValueOrDefault(season) ?? new List<double> { 7, 17 };
        double a = d[0], b = d[1];
        return JsCompat.Round((a + (b - a) * ctx.Hash("hour")) * 4) / 4;
    }

    private int NearestLegacyHour(double hour)
    {
        var keys = Ts.TimeOfDay.LegacyHours.Items.Select(kv => int.Parse(kv.Key, CultureInfo.InvariantCulture)).ToList();
        if (keys.Count == 0) return 12;
        int best = keys[0];
        foreach (int k in keys.Skip(1)) if (Math.Abs(k - hour) < Math.Abs(best - hour)) best = k;
        return best;
    }

    // ---------- weather ----------

    private string ResolveWeather(Ctx ctx, string season, double hour)
    {
        var w = Ts.Weather;
        string group = w.GroupOfBucket.GetValueOrDefault(ctx.Bucket) ?? "temperate";
        var row = (w.Table.GetValueOrDefault(group)?.GetValueOrDefault(season) ?? new OrderedWeights()).Clone();
        if (season == "winter" && ctx.Winter is not null && w.Ck3WinterOverride.TryGetValue(ctx.Winter, out var wov)) row = wov.Clone();
        if (season != "winter" && w.Monsoon.Buckets.Contains(ctx.Bucket) && w.Monsoon.Months.Contains(ctx.Month)) row = w.Monsoon.Override.Clone();
        if (w.DesertTerrains.Contains(ctx.Terrain))
        {
            foreach (var k in new[] { "light_rain", "heavy_rain" })
            {
                double v = row.Get(k);
                if (v != 0) { row.Set("dust", row.Get("dust") + v); row.Remove(k); }
            }
        }
        if (row.Count == 0) return "clear";
        string kind = JsCompat.PickWeighted(row, ctx.Hash("weather"));
        if (kind == "fog" && !(w.FogHours.Count >= 2 && hour >= w.FogHours[0] && hour <= w.FogHours[1])) kind = "overcast";
        if (kind is "snow" or "blizzard" && season != "winter") kind = "overcast";
        return kind;
    }

    private (string Name, bool Proven) PresetFor(double hour, string weather)
    {
        var rows = Ts.TimeOfDay.PresetsByHour;
        if (rows.Count == 0) return ("TOD_12_00_SemiCloudy", false);
        var row = rows.FirstOrDefault(r => hour >= r.From && hour < r.To) ?? rows[0];
        string? pick = weather switch
        {
            "heavy_rain" => row.Rain ?? "TOD_12_00_HeavyRain",
            "fog" => row.Fog ?? row.Overcast ?? row.Clear,
            "overcast" or "light_rain" or "snow" or "blizzard" or "dust" => row.Overcast ?? row.Clear,
            _ => row.Clear,
        };
        pick ??= "TOD_12_00_SemiCloudy";
        return (pick, row.CustomBattle && pick == row.Clear);
    }

    // ---------- field scene ----------

    private List<string> ListFor(SceneListSet? obj, string bucket, string terrain)
    {
        if (obj is null) return new List<string>();
        if (obj.ByTerrain is not null && obj.ByTerrain.TryGetValue(terrain, out var byT) &&
            (obj.ByTerrainBuckets is null || obj.ByTerrainBuckets.Contains(bucket))) return byT;
        if (obj.Buckets.TryGetValue(bucket, out var l)) return l;
        foreach (var b in Ts.Climate.Fallback.GetValueOrDefault(bucket) ?? new List<string>())
            if (obj.Buckets.TryGetValue(b, out var fl)) return fl;
        return obj.Buckets.GetValueOrDefault("any") ?? new List<string>();
    }

    private sealed record FieldPick(string Scene, List<string> Candidates, string Source, List<string> Why);

    private bool SceneUsable(string id) =>
        Ts.SceneInfo.TryGetValue(id, out var info) && !info.Missing && _t.LoadableSceneIds.Contains(id);

    private FieldPick ResolveField(Ctx ctx, string season)
    {
        var r = Ts.Rules;
        var sit = Ts.Situational;
        var p = ctx.P;
        var why = new List<string>();
        var terrainList = ListFor(Ts.Terrain.GetValueOrDefault(ctx.Pool)?.Scenes, ctx.Bucket, ctx.Pool);
        var lead = new List<string>();
        string src = "terrain";
        bool near = p.WaterDistPx >= 0 && p.WaterDistPx <= r.CoastalNearPx;
        // A relief battle on the cleared land around a holding (rule 4) stays there: only water at the holding itself leads.
        bool cleared = ctx.Pool != ctx.Terrain;
        SceneListSet? Sit(string key) => sit.GetValueOrDefault(key);
        switch (ctx.Input.Crossing)
        {
            case Crossing.MajorRiver: lead = ListFor(Sit("major_river_crossing"), ctx.Bucket, ctx.Terrain); src = "major_river_crossing"; break;
            case Crossing.River: lead = ListFor(Sit("river_crossing"), ctx.Bucket, ctx.Terrain); src = "river_crossing"; break;
            case Crossing.Strait: lead = ListFor(Sit("coastal"), ctx.Bucket, ctx.Terrain); src = "strait->coastal"; break;
            default:
                if (near && p.WaterKind == "sea") { lead = ListFor(Sit("coastal"), ctx.Bucket, ctx.Terrain); src = "coastal_near"; }
                else if (near && p.WaterKind == "lake") { lead = ListFor(Sit("lakeside"), ctx.Bucket, ctx.Terrain); src = "lakeside_near"; }
                else if (!cleared && p.IsCoastal && ctx.Hash("coast") < r.CoastalFarChance) { lead = ListFor(Sit("coastal"), ctx.Bucket, ctx.Terrain); src = "coastal_far"; }
                else if (!cleared && ((p.RiverDistPx >= 0 && p.RiverDistPx <= 2) || p.IsMajorRiverside) && ctx.Hash("river") < r.RiversideChance)
                { lead = ListFor(Sit("river_crossing"), ctx.Bucket, ctx.Terrain); src = "riverside"; }
                break;
        }
        why.Add($"list: {src} ({ctx.Pool}/{ctx.Bucket})");

        var exclude = r.ExcludeUnlisted.ToHashSet();
        var c = lead.Concat(terrainList).Distinct().Where(id => !exclude.Contains(id) && SceneUsable(id)).ToList();
        SceneInfo Info(string id) => Ts.SceneInfo[id];

        // Stable re-order inside the situational block and inside the terrain block separately (the lead stays in front).
        void ReorderBlocks(Func<string, bool> pred)
        {
            var leadBlock = c.Where(lead.Contains).ToList();
            var rest = c.Where(x => !lead.Contains(x)).ToList();
            static IEnumerable<string> F(List<string> arr, Func<string, bool> pr) => arr.Where(pr).Concat(arr.Where(x => !pr(x)));
            c = F(leadBlock, pred).Concat(F(rest, pred)).ToList();
        }

        if (r.Relief7Terciles.TryGetValue(ctx.Terrain, out var terc) && terc.Count >= 2 && p.Relief7Raw is int relief)
        {
            string? cls = relief >= terc[1] ? "relief_high" : relief <= terc[0] ? "relief_low" : null;
            if (cls is not null)
            {
                ReorderBlocks(id => Info(id).Relief == cls);
                why.Add($"relief {cls.Replace("relief_", "")} (relief7 {relief} vs terciles {terc[0]},{terc[1]})");
            }
        }
        if (season == "winter")
        {
            ReorderBlocks(id => Info(id).SnowLayer);
            why.Add("winter: snow-layer scenes first");
        }
        if (ctx.Input.Agents >= r.LargeBattleAgents)
        {
            var big = c.Where(id => (Info(id).SizeM is { Count: > 0 } sz ? sz.Min() : 0) >= r.LargeBattleMinSceneM).ToList();
            if (big.Count > 0) { c = big; why.Add($"{ctx.Input.Agents} agents: scenes >= {r.LargeBattleMinSceneM} m only"); }
        }
        if (c.Count == 0) c = new List<string> { r.FallbackScene };
        var top = c.Take(Math.Max(1, r.PickTopN)).ToList();
        var weights = top.Select((id, i) => new KeyValuePair<string, double>(id, i < r.PickWeights.Count ? r.PickWeights[i] : 0)).ToList();
        string scene = weights.Sum(w => w.Value) > 0 ? JsCompat.PickWeighted(weights, ctx.Hash("scene")) : top[0];
        return new FieldPick(scene, c.Take(6).ToList(), src, why);
    }

    // ---------- village scenes (relief battles at a besieged tribal holding) ----------

    private sealed record VillagePick(string Architecture, string Scene, List<string> Candidates);

    /// <summary>terrain_scenes.json siege.architecture: building_gfx (its village entry when split by settlement kind), then the
    /// heritage override for western gfx.</summary>
    private string VillageArchitecture(string? gfx, string? heritage)
    {
        var a = Ts.Siege.Architecture;
        string arch = a.Default;
        if (gfx is not null && a.BuildingGfx.TryGetValue(gfx, out var el))
        {
            if (el.ValueKind == System.Text.Json.JsonValueKind.String) arch = el.GetString() ?? a.Default;
            else if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
                arch = (el.TryGetProperty("village", out var v) ? v : el.TryGetProperty("castle", out var cst) ? cst : default).GetString() ?? a.Default;
        }
        if (gfx == "western_building_gfx" && heritage is not null && a.HeritageOverrideForWestern.TryGetValue(heritage, out var ho)) arch = ho;
        return arch;
    }

    /// <summary>A village scene of the place's architecture; null when that culture has no loadable village (then: field scene).</summary>
    private VillagePick? ResolveVillage(Ctx ctx)
    {
        string arch = VillageArchitecture(ctx.Input.BuildingGfx, ctx.Input.Heritage);
        var villages = (Ts.Village.Scenes.GetValueOrDefault(arch) ?? new List<string>()).Where(_t.LoadableSceneIds.Contains).ToList();
        if (villages.Count == 0) return null;
        string v = villages[(int)Math.Floor(ctx.Hash("village") * villages.Count)];
        return new VillagePick(arch, v, villages.Take(6).ToList());
    }
}
