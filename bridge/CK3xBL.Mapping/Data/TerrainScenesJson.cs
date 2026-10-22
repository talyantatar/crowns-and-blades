// Typed view of bridge/data/terrain_scenes.json (tools/data/build_terrain_scenes.js).
// The reference resolver (battlefield_resolver.js) iterates several JS objects in key order, so those are read into
// OrderedWeights / SceneListSet with JavaScript property order instead of a Dictionary.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CK3xBL.Mapping.Data;

public sealed class TerrainScenesJson
{
    public ClimateTable Climate { get; set; } = new();
    public Dictionary<string, TerrainEntry> Terrain { get; set; } = new();
    public Dictionary<string, int> BlTerrainTypeIds { get; set; } = new();
    public Dictionary<string, SceneListSet> Situational { get; set; } = new();
    public SceneRules Rules { get; set; } = new();
    public SiegeTable Siege { get; set; } = new();
    public VillageTable Village { get; set; } = new();
    public SeasonTable Season { get; set; } = new();
    public WeatherTable Weather { get; set; } = new();
    public TimeOfDayTable TimeOfDay { get; set; } = new();
    public Dictionary<string, SceneInfo> SceneInfo { get; set; } = new();
}

public sealed class ClimateTable
{
    public Dictionary<string, List<string>> Fallback { get; set; } = new();
    public Dictionary<string, string> MappingJsonAlias { get; set; } = new();
}

public sealed class TerrainEntry
{
    public string BlTerrainType { get; set; } = "Plain";
    public SceneListSet? Scenes { get; set; }
}

public sealed class SceneRules
{
    public int CoastalNearPx { get; set; } = 3;
    public double CoastalFarChance { get; set; }
    public double RiversideChance { get; set; }
    public int PickTopN { get; set; } = 3;
    public List<double> PickWeights { get; set; } = new();
    public int LargeBattleAgents { get; set; } = 600;
    [JsonPropertyName("large_battle_min_scene_m")]
    public double LargeBattleMinSceneM { get; set; } = 1000;
    public List<string> ExcludeUnlisted { get; set; } = new();
    public string FallbackScene { get; set; } = "battle_terrain_029";
    [JsonPropertyName("relief7_terciles")]
    public Dictionary<string, List<double>> Relief7Terciles { get; set; } = new();
}

/// <summary>The "siege" block: settlement kind per CK3 holding and settlement architecture. Its castle/town siege scene lists are
/// not read: sieges stay in CK3 and every Bannerlord battle is a field battle (PROTOCOL §0).</summary>
public sealed class SiegeTable
{
    /// <summary>CK3 holding key -> castle | town | village | field.</summary>
    public Dictionary<string, string> HoldingKind { get; set; } = new();
    public ArchitectureTable Architecture { get; set; } = new();
}

public sealed class ArchitectureTable
{
    /// <summary>building_gfx -> BL culture, or -> { castle, town, village } -> BL culture.</summary>
    public Dictionary<string, JsonElement> BuildingGfx { get; set; } = new();
    public Dictionary<string, string> HeritageOverrideForWestern { get; set; } = new();
    public string Default { get; set; } = "vlandia";
}

public sealed class VillageTable
{
    /// <summary>architecture (BL culture) -> custom-battle village scene ids.</summary>
    public Dictionary<string, List<string>> Scenes { get; set; } = new();
}

public sealed class SeasonTable
{
    public Dictionary<string, int> BlIds { get; set; } = new();
    /// <summary>bucket -> [ ["MM-DD", season], ... ] ascending.</summary>
    public Dictionary<string, List<List<string>>> Base { get; set; } = new();
    public List<string> MildWinterBuckets { get; set; } = new();
    public List<WinterWindow> WinterWindowByBias { get; set; } = new();
    public double PermanentSnowPct { get; set; } = 30;
}

public sealed class WinterWindow
{
    public double Min { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    /// <summary>1 (always) or the string "winterBias" (probability = the province's winter_severity_bias).</summary>
    public JsonElement Chance { get; set; }
}

public sealed class WeatherTable
{
    /// <summary>weather group -> season -> weighted kinds.</summary>
    public Dictionary<string, Dictionary<string, OrderedWeights>> Table { get; set; } = new();
    public Dictionary<string, string> GroupOfBucket { get; set; } = new();
    public MonsoonRule Monsoon { get; set; } = new();
    [JsonPropertyName("ck3_winter_override")]
    public Dictionary<string, OrderedWeights> Ck3WinterOverride { get; set; } = new();
    public List<string> DesertTerrains { get; set; } = new();
    public List<double> FogHours { get; set; } = new() { 5, 10 };
    public Dictionary<string, AtmosphereRow> Atmosphere { get; set; } = new();
}

public sealed class MonsoonRule
{
    public List<string> Buckets { get; set; } = new();
    public List<int> Months { get; set; } = new();
    public OrderedWeights Override { get; set; } = new();
}

public sealed class AtmosphereRow
{
    public string Interpolated { get; set; } = "semicloudy_field_battle";
    public double Rain { get; set; }
    public double Snow { get; set; }
    public double Fog { get; set; } = 1;
    public List<double>? FogColor { get; set; }
    public double? Dryness { get; set; }
}

public sealed class TimeOfDayTable
{
    public OrderedWeights LegacyHours { get; set; } = new();
    public Dictionary<string, List<double>> Daylight { get; set; } = new();
    public List<PresetRow> PresetsByHour { get; set; } = new();
}

public sealed class PresetRow
{
    public double From { get; set; }
    public double To { get; set; }
    public string? Clear { get; set; }
    public string? Rain { get; set; }
    public string? Fog { get; set; }
    public string? Overcast { get; set; }
    public bool CustomBattle { get; set; }
}

public sealed class SceneInfo
{
    [JsonPropertyName("size_m")]
    public List<double>? SizeM { get; set; }
    public string? Relief { get; set; }
    public bool SnowLayer { get; set; }
    public bool Missing { get; set; }
}

/// <summary>
/// A JSON object of name -> weight kept in JavaScript property order (integer-like keys ascending first, then the rest in
/// document order), so weighted picks iterate exactly like the reference resolver.
/// </summary>
[JsonConverter(typeof(OrderedWeightsConverter))]
public sealed class OrderedWeights
{
    private readonly List<KeyValuePair<string, double>> _items = new();

    public IReadOnlyList<KeyValuePair<string, double>> Items => _items;
    public int Count => _items.Count;

    public double Get(string key)
    {
        foreach (var kv in _items) if (kv.Key == key) return kv.Value;
        return 0;
    }

    /// <summary>Sets a value; a new key goes where a JS object would put it.</summary>
    public void Set(string key, double value)
    {
        int i = _items.FindIndex(kv => kv.Key == key);
        if (i >= 0) { _items[i] = new(key, value); return; }
        _items.Add(new(key, value));
        ApplyJsKeyOrder();
    }

    public void Remove(string key) => _items.RemoveAll(kv => kv.Key == key);

    public OrderedWeights Clone()
    {
        var c = new OrderedWeights();
        c._items.AddRange(_items);
        return c;
    }

    internal static OrderedWeights FromDocumentOrder(IEnumerable<KeyValuePair<string, double>> items)
    {
        var w = new OrderedWeights();
        w._items.AddRange(items);
        w.ApplyJsKeyOrder();
        return w;
    }

    // Integer-like keys ascending first, then every other key in insertion order (OrderBy is stable).
    private void ApplyJsKeyOrder()
    {
        var ordered = _items.OrderBy(kv => ArrayIndex(kv.Key) is null ? 1 : 0).ThenBy(kv => ArrayIndex(kv.Key) ?? 0).ToList();
        _items.Clear();
        _items.AddRange(ordered);
    }

    private static uint? ArrayIndex(string key) =>
        uint.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
        && n != uint.MaxValue && n.ToString(System.Globalization.CultureInfo.InvariantCulture) == key ? n : null;
}

internal sealed class OrderedWeightsConverter : JsonConverter<OrderedWeights>
{
    public override OrderedWeights Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var items = doc.RootElement.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.Number)
            .Select(p => new KeyValuePair<string, double>(p.Name, p.Value.GetDouble()))
            .ToList();
        return OrderedWeights.FromDocumentOrder(items);
    }

    public override void Write(Utf8JsonWriter writer, OrderedWeights value, JsonSerializerOptions options) =>
        throw new NotSupportedException("terrain_scenes.json is read-only for the bridge");
}

/// <summary>
/// A scene list keyed by climate bucket (<c>any</c>, <c>boreal</c>, ...), optionally with <c>by_terrain</c> lists that apply
/// only in <c>by_terrain_buckets</c> (terrain_scenes.json "terrain.*.scenes" and "situational.*").
/// </summary>
[JsonConverter(typeof(SceneListSetConverter))]
public sealed class SceneListSet
{
    public Dictionary<string, List<string>> Buckets { get; } = new();
    public Dictionary<string, List<string>>? ByTerrain { get; set; }
    public List<string>? ByTerrainBuckets { get; set; }
}

internal sealed class SceneListSetConverter : JsonConverter<SceneListSet>
{
    public override SceneListSet Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var set = new SceneListSet();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            switch (p.Name)
            {
                case "by_terrain":
                    set.ByTerrain = p.Value.Deserialize<Dictionary<string, List<string>>>(options);
                    break;
                case "by_terrain_buckets":
                    set.ByTerrainBuckets = p.Value.Deserialize<List<string>>(options);
                    break;
                default:
                    if (p.Value.ValueKind == JsonValueKind.Array)
                        set.Buckets[p.Name] = p.Value.Deserialize<List<string>>(options) ?? new();
                    break;
            }
        }
        return set;
    }

    public override void Write(Utf8JsonWriter writer, SceneListSet value, JsonSerializerOptions options) =>
        throw new NotSupportedException("terrain_scenes.json is read-only for the bridge");
}
