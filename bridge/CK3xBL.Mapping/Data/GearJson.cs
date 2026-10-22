// bridge/data/ck3xbl_equipment.json: the battle gear table the CK3 mod is generated from (tools/gen/equipment/equipment.mjs,
// PROTOCOL §16). A CX|gear record names the Bannerlord item; this table adds what the hero needs with it (a horse's harness).
namespace CK3xBL.Mapping.Data;

public sealed class GearJson
{
    public List<GearItem> Items { get; set; } = new();
}

public sealed class GearItem
{
    public int Idx { get; set; }
    /// <summary>The Bannerlord item id (the artifact IS this item).</summary>
    public string Id { get; set; } = "";
    /// <summary>sword, greatsword, axe, ..., shield, spear, lance, thrown, dagger, helmet, armour, gloves, boots, cloak, horse, camel.</summary>
    public string Kind { get; set; } = "";
    /// <summary>The CK3 slot flag: ck3xbl_main, ck3xbl_gear, ck3xbl_head, ck3xbl_body, ck3xbl_hands, ck3xbl_legs, ck3xbl_cape, ck3xbl_horse.</summary>
    public string Slot { get; set; } = "";
    public string Culture { get; set; } = "";
    public int Tier { get; set; }
    public int Prowess { get; set; }
    /// <summary>The harness a horse artifact brings (null: none in the catalog).</summary>
    public string? Harness { get; set; }
    public string Name { get; set; } = "";
}
