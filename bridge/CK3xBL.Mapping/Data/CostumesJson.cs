// bridge/data/ck3xbl_costumes.json (PROTOCOL §21), written by tools/gen/costumes/build_costumes.mjs.
namespace CK3xBL.Mapping.Data;

public sealed class CostumesJson
{
    public List<CostumeItem> Items { get; set; } = new();
    /// <summary>CK3 culture profile -> item id -> weight.</summary>
    public Dictionary<string, Dictionary<string, double>> Looks { get; set; } = new();
}

public sealed class CostumeItem
{
    public string Id { get; set; } = "";
    public string Slot { get; set; } = "";
    public string Culture { get; set; } = "";
    public int Armor { get; set; }
    /// <summary>Earliest CK3 era: 0 tribal, 1 early, 2 high, 3 late medieval.</summary>
    public int EraMin { get; set; }
    public List<string> Tags { get; set; } = new();
    public string Source { get; set; } = "";
}
