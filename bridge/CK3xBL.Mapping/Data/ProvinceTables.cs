// Static per-province facts derived from the CK3 install (derived numbers only, no game assets):
//   ck3_province_terrain_index.json (tools/data/ck3_map_index.js)        - terrain, winter bias, coast/river/water, relief, ground
//   ck3_province_climate.json       (tools/data/ck3_province_climate.js) - graphical region, climate bucket, county
// Both files are column tables: meta.cols names the columns, rows hold the values (index columns refer to meta lists).
using System.Text.Json;

namespace CK3xBL.Mapping.Data;

public sealed class ProvinceFacts
{
    public int Id { get; init; }
    public string Terrain { get; init; } = "plains";
    public double WinterBias { get; init; }
    /// <summary>1 coastal | 2 lakeside | 4 major riverside | 8 impassable.</summary>
    public int Flags { get; init; }
    /// <summary>max-min heightmap raw over 7x7 map px at the combat locator; null when unknown.</summary>
    public int? Relief7Raw { get; init; }
    /// <summary>Distance to a river in map px (-1 = none within 6 px).</summary>
    public int RiverDistPx { get; init; } = -1;
    /// <summary>Distance to sea/lake water in map px (-1 = none within 8 px).</summary>
    public int WaterDistPx { get; init; } = -1;
    public string? WaterKind { get; init; }
    public double SnowPct { get; init; }

    public bool IsCoastal => (Flags & 1) != 0;
    public bool IsMajorRiverside => (Flags & 4) != 0;
}

public sealed class ProvinceClimate
{
    public string? Region { get; init; }
    public string Bucket { get; init; } = "temperate";
    public string? County { get; init; }
}

internal static class ProvinceTableReader
{
    public static Dictionary<int, ProvinceFacts> ReadTerrainIndex(string path)
    {
        using var doc = JsonDocument.Parse(DataFile.ReadAllBytes(path));
        var meta = doc.RootElement.GetProperty("meta");
        // "flags(1 coastal|2 ...)" -> "flags": the reference resolver strips the parenthesised notes the same way.
        var cols = meta.GetProperty("cols").EnumerateArray().Select(c => StripNote(c.GetString() ?? "")).ToList();
        var terrains = meta.GetProperty("terrains").EnumerateArray().Select(t => t.GetString() ?? "plains").ToList();
        var waterKinds = meta.GetProperty("waterKinds").EnumerateArray().Select(t => t.GetString()).ToList();
        int Col(string name) => cols.IndexOf(name);
        int cId = Col("id"), cTerrain = Col("terrain"), cBias = Col("winterBias"), cFlags = Col("flags"), cRelief = Col("relief7Raw"),
            cRiver = Col("riverDistPx"), cWater = Col("waterDistPx"), cWaterKind = Col("waterKind"), cSnow = Col("snow%");
        if (cId < 0 || cTerrain < 0) throw new InvalidDataException($"{path}: missing id/terrain columns");

        var result = new Dictionary<int, ProvinceFacts>();
        foreach (var row in doc.RootElement.GetProperty("rows").EnumerateArray())
        {
            var v = row.EnumerateArray().ToList();
            double Num(int c, double fallback = 0) => c >= 0 && c < v.Count && v[c].ValueKind == JsonValueKind.Number ? v[c].GetDouble() : fallback;
            int terrainIdx = (int)Num(cTerrain, -1);
            int waterIdx = (int)Num(cWaterKind, -1);
            var p = new ProvinceFacts
            {
                Id = (int)Num(cId),
                Terrain = terrainIdx >= 0 && terrainIdx < terrains.Count ? terrains[terrainIdx] : "plains",
                WinterBias = Num(cBias),
                Flags = (int)Num(cFlags),
                Relief7Raw = cRelief >= 0 ? (int)Num(cRelief) : null,
                RiverDistPx = (int)Num(cRiver, -1),
                WaterDistPx = (int)Num(cWater, -1),
                WaterKind = waterIdx >= 0 && waterIdx < waterKinds.Count ? waterKinds[waterIdx] : null,
                SnowPct = Num(cSnow),
            };
            result[p.Id] = p;
        }
        return result;
    }

    public static Dictionary<int, ProvinceClimate> ReadClimate(string path)
    {
        using var doc = JsonDocument.Parse(DataFile.ReadAllBytes(path));
        var meta = doc.RootElement.GetProperty("meta");
        List<string?> List(string name) => meta.GetProperty(name).EnumerateArray().Select(e => e.GetString()).ToList();
        var regions = List("regions");
        var buckets = List("buckets");
        static string? At(List<string?> list, int i) => i >= 0 && i < list.Count ? list[i] : null;

        var result = new Dictionary<int, ProvinceClimate>();
        foreach (var row in doc.RootElement.GetProperty("rows").EnumerateArray())
        {
            var v = row.EnumerateArray().ToList();
            if (v.Count < 5) continue;
            int id = v[0].GetInt32();
            result[id] = new ProvinceClimate
            {
                Region = At(regions, v[1].GetInt32()),
                Bucket = At(buckets, v[4].GetInt32()) ?? "temperate",
                County = v.Count > 5 && v[5].ValueKind == JsonValueKind.String ? v[5].GetString() : null,
            };
        }
        return result;
    }

    private static string StripNote(string col)
    {
        int i = col.IndexOf('(');
        return i >= 0 ? col[..i] : col;
    }
}
