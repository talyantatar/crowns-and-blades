// Typed views of the catalog files the mapper reads (only the fields it needs):
//   ck3_catalog.json                              - MaA damage/pursuit/screen/toughness, levy stats, culture heritage/building gfx,
//                                                  terrain defender advantage
//   bl_catalog.json                               - Bannerlord troops (formation, mount, CustomGame availability) and items (tier,
//                                                  classes, armour)
//   bl_ruler_kits.json                            - ruler/lord equipment rosters loaded in CustomGame
//   ck3_maa_index.json (PROTOCOL §2.2)            - 1-based MaA type index for the keypad payload
//   bl_scenes.json                                - every Bannerlord scene id and the module that ships it
namespace CK3xBL.Mapping.Data;

public sealed class Ck3CatalogJson
{
    public Dictionary<string, Ck3Maa> MaaTypes { get; set; } = new();
    public Ck3Levy Levy { get; set; } = new();
    public Dictionary<string, Ck3Culture> Cultures { get; set; } = new();
    public List<Ck3Terrain> Terrains { get; set; } = new();
}

public sealed class Ck3Maa
{
    public string BaseType { get; set; } = "";
    public double Damage { get; set; }
    public double Toughness { get; set; }
    public double Pursuit { get; set; }
    public double Screen { get; set; }
}

public sealed class Ck3Levy
{
    public double Damage { get; set; } = 10;
    public double Toughness { get; set; } = 10;
    public double Pursuit { get; set; }
    public double Screen { get; set; }
}

public sealed class Ck3Terrain
{
    public string Key { get; set; } = "";
    /// <summary>Advantage the combat defender gets on this terrain (common/terrain_types).</summary>
    public double DefenderAdvantage { get; set; }
}

public sealed class Ck3Culture
{
    public string? Heritage { get; set; }
    public List<string> BuildingGfx { get; set; } = new();
}

public sealed class BlCatalogJson
{
    public Dictionary<string, BlTroop> Troops { get; set; } = new();
    public BlCatalogItems Items { get; set; } = new();
}

/// <summary>The item lists of bl_catalog.json (the "notable" summary is not an item list and is not read).</summary>
public sealed class BlCatalogItems
{
    public List<BlItem> Horses { get; set; } = new();
    public List<BlItem> Camels { get; set; } = new();
    public List<BlItem> OtherAnimals { get; set; } = new();
    public List<BlItem> Harness { get; set; } = new();
    public List<BlItem> Weapons { get; set; } = new();
    public List<BlItem> Armors { get; set; } = new();
    public List<BlItem> Shields { get; set; } = new();
    public List<BlItem> Ammo { get; set; } = new();
    public List<BlItem> Banners { get; set; } = new();

    public IEnumerable<BlItem> All => Horses.Concat(Camels).Concat(OtherAnimals).Concat(Harness).Concat(Weapons).Concat(Armors)
        .Concat(Shields).Concat(Ammo).Concat(Banners);
}

public sealed class BlItem
{
    public string Id { get; set; } = "";
    /// <summary>Bannerlord ItemTypeEnum name (OneHandedWeapon, Polearm, Bow, Arrows, Shield, BodyArmor, ...).</summary>
    public string? Type { get; set; }
    /// <summary>Item tier; null for items Bannerlord gives no tier (non-crafted melee weapons, banners).</summary>
    public int? Tier { get; set; }
    /// <summary>Weapon classes of every usage (OneHandedSword, Dagger, Mace, ...).</summary>
    public List<string> Classes { get; set; } = new();
    public BlArmor? Armor { get; set; }
}

public sealed class BlArmor
{
    public int Head { get; set; }
    public int Body { get; set; }
}

public sealed class BlTroop
{
    public string Formation { get; set; } = "Infantry";
    public string? FormationFromGear { get; set; }
    public bool Mounted { get; set; }
    public bool LoadedInCustomgame { get; set; }
    /// <summary>Troop tier 1..6 (0 = not given): the strength balance's estimate of what one agent is worth.</summary>
    public int Tier { get; set; }
}

public sealed class RulerKitsJson
{
    public Dictionary<string, RulerKit> RulerAndNobleRosters { get; set; } = new();
}

public sealed class RulerKit
{
    public List<RulerKitSet> Sets { get; set; } = new();

    /// <summary>Heroes always use the first set at spawn.</summary>
    public bool HasHorse => Sets.Count > 0 && Sets[0].Items.Any(i => i.Slot == "Horse" && !string.IsNullOrEmpty(i.Id));
}

public sealed class RulerKitSet
{
    public List<RulerKitItem> Items { get; set; } = new();
}

public sealed class RulerKitItem
{
    public string Slot { get; set; } = "";
    public string Id { get; set; } = "";
}

public sealed class MaaIndexJson
{
    public List<string> Keys { get; set; } = new();
}

public sealed class BlSceneEntry
{
    public string Id { get; set; } = "";
    /// <summary>The Bannerlord module whose SceneObj folder holds the scene (Native, SandBoxCore, SandBox, StoryMode).</summary>
    public string? Module { get; set; }
}
