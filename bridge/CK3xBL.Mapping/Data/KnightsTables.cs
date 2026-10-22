// Typed views of the knights data files (PROTOCOL §13.8). Only the members the mapper reads are
// modelled; System.Text.Json ignores the rest.
//   ck3_combat_traits.json        traits with their Bannerlord effects and CK3 fate factors, conversion constants, combine rules
//                                 (generator tools/gen/knights/gen_combat_traits.mjs; camelCase keys, CK3 fate keys snake_case)
//   ck3_accolade_types.json       per accolade attribute: captain rule, per-level group effects, fate factors, Marauder advantage
//                                 (generator gen_ck3_accolade_types.mjs; camelCase under "bannerlord", CK3 modifier keys snake_case)
//   ck3_artifact_to_bl_items.json artifact type -> Bannerlord items per culture, rarity and mounted/foot (gen_artifact_tables.mjs)
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CK3xBL.Mapping.Data;

public sealed class KnightsTables
{
    /// <summary>
    /// Which side's <c>ck3xbl_s_hostility_out</c> scales a commander's faith-hostility advantage: "out" =
    /// the holder's own faith's view of the enemy's faith; "in" = the enemy faith's view of the holder's. INFERRED; still to be confirmed in game.
    /// </summary>
    public const string HostilityDirection = "out";

    private static readonly JsonSerializerOptions CamelJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public required CombatTraitsJson CombatTraits { get; init; }
    public required AccoladeTypesJson Accolades { get; init; }
    public required ArtifactItemsJson Artifacts { get; init; }

    public static KnightsTables Load(string directory, JsonSerializerOptions snakeJson)
    {
        T Read<T>(string file, JsonSerializerOptions options) where T : class
        {
            string path = Path.Combine(directory, file);
            return JsonSerializer.Deserialize<T>(DataFile.ReadAllBytes(path), options) ?? throw new InvalidDataException($"{path} is empty");
        }

        return new KnightsTables
        {
            CombatTraits = Read<CombatTraitsJson>("ck3_combat_traits.json", CamelJson),
            Accolades = Read<AccoladeTypesJson>("ck3_accolade_types.json", CamelJson),
            Artifacts = Read<ArtifactItemsJson>("ck3_artifact_to_bl_items.json", snakeJson),
        };
    }
}

// ---- ck3_combat_traits.json ---------------------------------------------------------------------------------------------

public sealed class CombatTraitsJson
{
    public TraitConversion Conversion { get; set; } = new();
    /// <summary>Effect name ("side.advantage", "hero.noPanic", ...) -> how values of one character combine.</summary>
    public Dictionary<string, BlEffectInfo> BlEffects { get; set; } = new();
    public Dictionary<string, CombatTrait> Traits { get; set; } = new();
}

public sealed class TraitConversion
{
    public AdvantageConversion Advantage { get; set; } = new();
    public CombatRollConversion CombatRoll { get; set; } = new();
    public FaithHostilityConversion FaithHostility { get; set; } = new();
    public OwnerTroopsConversion OwnerTroops { get; set; } = new();
}

public sealed class AdvantageConversion
{
    public double PerPoint { get; set; }
    public double MaxDamageBonus { get; set; }
    public double MoralePerPoint { get; set; }
    public double MaxMoraleBonus { get; set; }
}

public sealed class CombatRollConversion
{
    public double PerSpreadPoint { get; set; }
    public double MaxVolatility { get; set; }
}

public sealed class FaithHostilityConversion
{
    /// <summary>Faith hostility level ("0".."3") -> share of a faith-hostility advantage that applies.</summary>
    public Dictionary<string, double> Scale { get; set; } = new();
}

public sealed class OwnerTroopsConversion
{
    /// <summary>The table's group.damageDealtMult m stands for a CK3 damage_mult of (m - 1) / DamageShare.</summary>
    public double DamageShare { get; set; } = 0.5;
    /// <summary>The table's group.damageTakenMult m stands for a CK3 toughness_mult of (1/m - 1) / ToughnessShare.</summary>
    public double ToughnessShare { get; set; } = 0.5;
}

public sealed class BlEffectInfo
{
    /// <summary>add | mult | or.</summary>
    public string Combine { get; set; } = "add";
}

public sealed class CombatTrait
{
    public TraitEffects Bl { get; set; } = new();
    public TraitFate? Fate { get; set; }
}

public sealed class TraitEffects
{
    /// <summary>Effects at level 0.</summary>
    public List<TraitEffect> Base { get; set; } = new();
    /// <summary>XP tracks; steps are cumulative (a level-n character has the base plus steps 1..n).</summary>
    public List<TraitTrack>? Tracks { get; set; }
}

public sealed class TraitTrack
{
    public string Name { get; set; } = "";
    public List<TraitStep> Steps { get; set; } = new();
}

public sealed class TraitStep
{
    public List<TraitEffect> Effects { get; set; } = new();
}

public sealed class TraitEffect
{
    public string Effect { get; set; } = "";
    public double Value { get; set; }
    public EffectCondition? When { get; set; }
}

/// <summary>When an effect holds (terrain, winter, role, parameters, context flags), and qualifiers that narrow what it acts on.</summary>
public sealed class EffectCondition
{
    public List<string>? Terrain { get; set; }
    public bool Winter { get; set; }
    public bool Coastal { get; set; }
    public string? Role { get; set; }
    public string? CultureParameter { get; set; }
    public string? FaithParameter { get; set; }
    public bool SameFaith { get; set; }
    public bool HostileTerritory { get; set; }
    public bool OwnTerritory { get; set; }
    public bool OwnArmy { get; set; }
    public bool SameHeritageCounty { get; set; }
    public bool IndependentPrimary { get; set; }
    public bool FaithHostility { get; set; }

    // Qualifiers (combat_traits_eval.mjs QUALIFIERS): effects with different qualifiers are kept apart.
    public string? Weapon { get; set; }
    public string? Vs { get; set; }
    public string? Skill { get; set; }
    public string? Troops { get; set; }
    public string? MaaBaseType { get; set; }
    public string? MaaType { get; set; }
    public string? Per { get; set; }
}

/// <summary>CK3 combat phase event factors of a trait (common/combat_phase_events); absent = 1.</summary>
public sealed class TraitFate
{
    [JsonPropertyName("knight_wounded")] public double? KnightWounded { get; set; }
    [JsonPropertyName("knight_maimed")] public double? KnightMaimed { get; set; }
    [JsonPropertyName("knight_killed")] public double? KnightKilled { get; set; }
    [JsonPropertyName("commander_wounded")] public double? CommanderWounded { get; set; }
    [JsonPropertyName("commander_maimed")] public double? CommanderMaimed { get; set; }
    [JsonPropertyName("commander_killed")] public double? CommanderKilled { get; set; }
}

// ---- ck3_accolade_types.json --------------------------------------------------------------------------------------------

public sealed class AccoladeTypesJson
{
    public Dictionary<string, AccoladeType> Types { get; set; } = new();
}

public sealed class AccoladeType
{
    public AccoladeBannerlord Bannerlord { get; set; } = new();
}

public sealed class AccoladeBannerlord
{
    public AccoladeCaptain Captain { get; set; } = new();
    /// <summary>Attribute level "1".."3" -> its effects (each level replaces the one below).</summary>
    public Dictionary<string, AccoladeLevel> PerLevel { get; set; } = new();
}

public sealed class AccoladeCaptain
{
    /// <summary>linked_maa (captains the formation of his linked MaA) | own_army_wide | own.</summary>
    public string? Rule { get; set; }
    public string? LinkedBaseType { get; set; }
}

public sealed class AccoladeLevel
{
    public List<AccoladeGroupEffect> Groups { get; set; } = new();
    /// <summary>enemy_knight_death_weight (Blademaster, Crossbow Captain) / own_house_knight_harm_weight (House Paragon).</summary>
    public Dictionary<string, double>? FateFactors { get; set; }
    /// <summary>Marauder: advantage of the knight's side while it attacks in hostile territory.</summary>
    public double AdvantageIfUncontrolled { get; set; }
}

public sealed class AccoladeGroupEffect
{
    /// <summary>maa_base_type (the knight's army's MaA of <see cref="BaseType"/>) | army (every CK3 group of his army).</summary>
    public string Scope { get; set; } = "";
    public string? BaseType { get; set; }
    /// <summary>Raw CK3 knight_army_modifier values: damage_mult, toughness_mult, pursuit_mult, screen_mult (army_* for scope army).</summary>
    public Dictionary<string, double> Ck3 { get; set; } = new();

    public double Ck3Value(string stat) => Ck3.GetValueOrDefault(stat) + Ck3.GetValueOrDefault("army_" + stat);
}

// ---- ck3_artifact_to_bl_items.json --------------------------------------------------------------------------------------

public sealed class ArtifactItemsJson
{
    public List<string> Cultures { get; set; } = new();
    /// <summary>Bannerlord culture without its own items -> the culture whose items it uses ("*" = everything else).</summary>
    public Dictionary<string, string> CultureFallback { get; set; } = new();
    /// <summary>CK3 artifact type -> battle category (sword, spear, bow, crown, armor_mail, ...).</summary>
    public Dictionary<string, string> TypeToCategory { get; set; } = new();
    public Dictionary<string, ArtifactCategory> Categories { get; set; } = new();
    /// <summary>category -> culture -> rarity -> "mounted" | "foot" -> the items.</summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, Dictionary<string, ArtifactEntry>>>> Items { get; set; } = new();
    /// <summary>armor_helmet_* feature -> culture -> rarity -> head item matching the CK3 helmet shape.</summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, string>>> ArmorHelmetByFeature { get; set; } = new();
}

public sealed class ArtifactCategory
{
    /// <summary>melee | polearm | sidearm | ranged | head | armor.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Arrows | Bolts for ranged categories.</summary>
    public string? AmmoType { get; set; }
}

public sealed class ArtifactEntry
{
    public string? Weapon { get; set; }
    public string? Ammo { get; set; }
    [JsonPropertyName("Body")] public string? Body { get; set; }
    [JsonPropertyName("Head")] public string? Head { get; set; }
}
