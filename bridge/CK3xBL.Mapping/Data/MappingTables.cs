// All static tables the mapper needs, loaded once per data directory and shared (immutable after load, thread-safe to read).
using System.Collections.Concurrent;
using System.Text.Json;
using CK3xBL.Core.Config;

namespace CK3xBL.Mapping.Data;

public sealed class MappingTables
{
    /// <summary>
    /// Modules whose scenes the mapper may send: the ones in every Bannerlord launch the bridge makes (PROTOCOL §3.1, also part
    /// of the fallback list). CK3Bridge ships no scenes. A scene of SandBox or StoryMode is not loaded in the normal launch, and the
    /// module would replace it with its fallback field scene.
    /// </summary>
    public static readonly IReadOnlySet<string> LaunchedSceneModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Native", "SandBoxCore", "CustomBattle",
    };

    private static readonly ConcurrentDictionary<string, Lazy<MappingTables>> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public required string Directory { get; init; }
    public required MappingJson Mapping { get; init; }
    public required TerrainScenesJson Terrain { get; init; }
    public required Ck3CatalogJson Ck3 { get; init; }
    public required IReadOnlyDictionary<string, BlTroop> Troops { get; init; }
    public required IReadOnlyDictionary<string, RulerKit> Kits { get; init; }
    /// <summary>Scene ids of bl_scenes.json that live in one of <see cref="LaunchedSceneModules"/>.</summary>
    public required IReadOnlySet<string> LoadableSceneIds { get; init; }
    public required IReadOnlyDictionary<int, ProvinceFacts> Provinces { get; init; }
    public required IReadOnlyDictionary<int, ProvinceClimate> Climates { get; init; }
    /// <summary>MaA key -> 1-based index of bridge/data/ck3_maa_index.json (PROTOCOL §2.2).</summary>
    public required IReadOnlyDictionary<string, int> MaaIndex { get; init; }
    /// <summary>Every item of bl_catalog.json by id (tier, classes and armour of kit and artifact items).</summary>
    public required IReadOnlyDictionary<string, BlItem> Items { get; init; }
    /// <summary>Combat traits, accolade types and artifact items of the knights pass (PROTOCOL §13.8).</summary>
    public required KnightsTables Knights { get; init; }
    /// <summary>mapping.json restricted_troops. Load refuses data that resolves a restricted troop outside
    /// its allow entries, so the resolver needs no runtime filter.</summary>
    public required TroopRestrictions Restrictions { get; init; }
    /// <summary>The battle gear of ck3xbl_equipment.json by Bannerlord item id (PROTOCOL §16); empty when the file is missing.</summary>
    public IReadOnlyDictionary<string, GearItem> Gear { get; init; } = new Dictionary<string, GearItem>();
    /// <summary>The costume tables of ck3xbl_costumes.json (PROTOCOL §21); null when the file is missing.</summary>
    public CostumesJson? Costumes { get; init; }

    private IReadOnlyDictionary<string, CostumeItem>? _costumeItems;
    /// <summary>The costume items by id (empty without the costume file).</summary>
    public IReadOnlyDictionary<string, CostumeItem> CostumeItems => _costumeItems ??=
        (Costumes?.Items ?? new List<CostumeItem>()).GroupBy(i => i.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>
    /// Returns the tables for <paramref name="configuredDirectory"/> (or the discovered bridge/data), loading them once. A failed
    /// load (a file being regenerated, a sharing violation) is not cached: the next battle tries again.
    /// </summary>
    public static MappingTables Get(string? configuredDirectory)
    {
        string dir = DataLocator.Resolve(configuredDirectory);
        var lazy = Cache.GetOrAdd(dir, d => new Lazy<MappingTables>(() => Load(d)));
        try
        {
            return lazy.Value;
        }
        catch
        {
            Cache.TryRemove(new KeyValuePair<string, Lazy<MappingTables>>(dir, lazy));
            throw;
        }
    }

    public const string GearFile = "ck3xbl_equipment.json";
    public const string CostumesFile = "ck3xbl_costumes.json";

    public static MappingTables Load(string directory)
    {
        T Read<T>(string file) where T : class
        {
            string path = Path.Combine(directory, file);
            return JsonSerializer.Deserialize<T>(DataFile.ReadAllBytes(path), Json) ?? throw new InvalidDataException($"{path} is empty");
        }

        var maaKeys = Read<MaaIndexJson>("ck3_maa_index.json").Keys;
        var maaIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < maaKeys.Count; i++) maaIndex.TryAdd(maaKeys[i], i + 1);
        var blCatalog = Read<BlCatalogJson>("bl_catalog.json");
        var mapping = Read<MappingJson>("mapping.json");
        var restrictions = new TroopRestrictions(mapping.RestrictedTroops);
        var problems = restrictions.Validate(mapping, blCatalog.Troops);
        if (problems.Count > 0)
            throw new InvalidDataException($"{Path.Combine(directory, "mapping.json")} breaks its restricted_troops rules:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");

        return new MappingTables
        {
            Directory = directory,
            Mapping = mapping,
            Terrain = Read<TerrainScenesJson>("terrain_scenes.json"),
            Ck3 = Read<Ck3CatalogJson>("ck3_catalog.json"),
            Troops = blCatalog.Troops,
            Kits = Read<RulerKitsJson>("bl_ruler_kits.json").RulerAndNobleRosters,
            LoadableSceneIds = Read<List<BlSceneEntry>>("bl_scenes.json")
                .Where(s => s.Module is not null && LaunchedSceneModules.Contains(s.Module))
                .Select(s => s.Id).ToHashSet(StringComparer.Ordinal),
            Provinces = ProvinceTableReader.ReadTerrainIndex(Path.Combine(directory, "ck3_province_terrain_index.json")),
            Climates = ProvinceTableReader.ReadClimate(Path.Combine(directory, "ck3_province_climate.json")),
            MaaIndex = maaIndex,
            Items = blCatalog.Items.All.GroupBy(i => i.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal),
            Knights = KnightsTables.Load(directory, Json),
            Restrictions = restrictions,
            Gear = File.Exists(Path.Combine(directory, GearFile))
                ? Read<GearJson>(GearFile).Items.GroupBy(i => i.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)
                : new Dictionary<string, GearItem>(),
            Costumes = File.Exists(Path.Combine(directory, CostumesFile)) ? Read<CostumesJson>(CostumesFile) : null,
        };
    }

    /// <summary>True when the troop exists in bl_catalog.json and is loaded by the CustomGame game type.</summary>
    public bool IsSpawnableTroop(string id) => Troops.TryGetValue(id, out var t) && t.LoadedInCustomgame;
}

internal static class DataFile
{
    /// <summary>Reads a whole data file without blocking (or being blocked by) a generator that rewrites it at the same time.</summary>
    public static byte[] ReadAllBytes(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream(fs.CanSeek ? (int)Math.Min(fs.Length, int.MaxValue) : 0);
        fs.CopyTo(ms);
        return ms.ToArray();
    }
}

/// <summary>Finds bridge/data: the configured directory, else CK3XBL_BRIDGE_ROOT/data, else next to the executable or in any parent
/// directory.</summary>
public static class DataLocator
{
    private const string Marker = "mapping.json";

    public static string Resolve(string? configuredDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            string full = Path.GetFullPath(configuredDirectory);
            if (File.Exists(Path.Combine(full, Marker))) return full;
            throw new DirectoryNotFoundException($"Mapping data directory '{full}' does not contain {Marker}.");
        }

        if (BridgePaths.RootFromEnvironment() is { } root && File.Exists(Path.Combine(root, "data", Marker)))
            return Path.Combine(root, "data");
        var searched = new List<string>();
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                foreach (var candidate in new[] { Path.Combine(dir.FullName, "data"), Path.Combine(dir.FullName, "bridge", "data") })
                {
                    if (File.Exists(Path.Combine(candidate, Marker))) return Path.GetFullPath(candidate);
                    searched.Add(candidate);
                }
            }
        }
        throw new DirectoryNotFoundException(
            $"Could not find the bridge data directory ({Marker}). Set MapperOptions.DataDirectory. Searched: {string.Join("; ", searched.Distinct())}");
    }
}
