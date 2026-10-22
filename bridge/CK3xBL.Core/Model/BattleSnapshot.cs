// The bridge's in-memory picture of one CK3 battle, built by the log parser from the records in docs/PROTOCOL.md §1.2.
// Owned by CK3xBL.Core. Mapping/Appearance/Ux consume it read-only.
using System.Collections.Generic;

namespace CK3xBL.Core.Model;

public enum SideKey { Attacker, Defender }

public sealed class BattleSnapshot
{
    public int Bid;                                   // ck3xbl_bid (global counter in the CK3 save)
    public string SessionKey = "";                    // bridge-generated, makes request ids unique across CK3 restarts
    public string RequestId => $"b{Bid}_{SessionKey}";
    public long CombatId;                             // (Combat - N) print id
    public string CombatName = "";                    // "Battle of Orangallu"
    public int Year, Month, Day;
    public LocationInfo Location = new();
    public long PlayerCk3Id;                          // internal id of the player character
    public string? WarName;
    public SideSnapshot Attacker = new() { Key = SideKey.Attacker };
    public SideSnapshot Defender = new() { Key = SideKey.Defender };
    public bool Complete;                             // B|END seen
    public int ParentBid;                             // B|BEGIN ck3xbl_parent_bid: the steering bid a reinforcement clash continues
                                                      // (PROTOCOL §14.6); 0 = first clash
    public int Clash = 1;                             // B|BEGIN ck3xbl_clash: 1 = first clash, 2 = first reinforcement clash, ...
    public bool IsDuel;                               // B|BEGIN ck3xbl_kind = 2: a CK3 single combat (PROTOCOL §18), one character per side
    public int DuelFatality;                          // B|BEGIN ck3xbl_d_fatality: 0 no, 1 possible, 2 always, 3 default, 4 practice
    public bool? DuelNoble;                           // B|BEGIN ck3xbl_d_noble: the foe is of noble birth (an arena, PROTOCOL §27);
                                                      // null = an older mod (no venue: the field as before)
    public int DuelTourney;                           // B|BEGIN ck3xbl_d_tourney: a tournament bout (PROTOCOL §29): 1 single combat,
                                                      // 2 wrestling, 3 grand melee, 4 joust, 5 archery (§36); 0 = no tournament
    public bool IsSkirmish;                           // B|BEGIN ck3xbl_kind = 3: a travel danger fought out (PROTOCOL §20), 50 fighters at most
    public int SkirmishKind;                          // B|BEGIN ck3xbl_sk_kind: 1 bandits, 2 deserters, 3 assassin, 4 soldiers, 5 zealots
    public bool IsSiege;                              // B|BEGIN ck3xbl_kind = 4: the player's army storms a besieged holding (PROTOCOL §39)
    public bool Landing;                              // LV ck3xbl_l_landing = 1: the attackers came straight off the sea (§44)
    public bool IsSeaBattle;                          // B|BEGIN ck3xbl_kind = 5: two hosts met at sea (§44), fought on ships
    public bool Relief;                               // B|BEGIN ck3xbl_relief = 1: the enemy besieges a holding of the player or a vassal here (§40)
    public int SiegeFort;                             // ck3xbl_sg_fort: the province's fort level
    public int SiegeGarrison;                         // ck3xbl_sg_garrison: its largest garrison
    public int SiegeDays;                             // ck3xbl_sg_days: days the army has stood there (the siege's length, about)
    public int SiegeMen;                              // ck3xbl_sg_men: the besieging army's men
    public bool KnightFactsLogged;                    // true when any S dump of this battle has the key ck3xbl_s_has_cmd (a mod
                                                      // with the knights pass, PROTOCOL §13); false = old mod: the mapper skips
                                                      // the whole pass

    public SideSnapshot Side(SideKey k) => k == SideKey.Attacker ? Attacker : Defender;
    public IEnumerable<SideSnapshot> Sides { get { yield return Attacker; yield return Defender; } }
    /// <summary>The side the player is on (s_player_in = 1), or null when the player is only a top liege of a participant.</summary>
    public SideSnapshot? PlayerSide => Attacker.PlayerIn ? Attacker : Defender.PlayerIn ? Defender : null;
}

public sealed class LocationInfo
{
    public long ProvinceId;
    public string ProvinceName = "";
    public string? CountyKey;                         // c_sanda
    public string? CountyName;
    public string? BaronyKey;
    public string Terrain = "plains";                 // CK3 terrain key (L|terrain)
    public string Winter = "none";                    // none | mild | normal | harsh
    public string Holding = "none";                   // castle_holding | city_holding | church_holding | tribal_holding | none ...
    public bool Coastal, Riverside, Siege;
    public int FortLevel, Development;
    public long? AttackerOriginProvinceId;            // optional, for river crossings
    /// <summary>Every <c>L|field|value</c> record verbatim (PROTOCOL §1.2), including fields without a typed property above
    /// (region, building_gfx, loc_culture, raid, occupied, ...). Keys are the field names as emitted.</summary>
    public Dictionary<string, string> Facts = new(StringComparer.Ordinal);
}

public sealed class SideSnapshot
{
    public SideKey Key;
    public int Size;                                  // side_army_size
    public bool PlayerIn;
    public CharacterRef? Primary;                     // side_primary_participant
    public CharacterRef? Commander;                   // S dump ck3xbl_s_cmd (side_commander); null = not logged / none
    public int? FaithHostilityOut;                    // S dump ck3xbl_s_hostility_out 0..3: this side's commander's faith towards
                                                      // the enemy commander's faith; null = not logged
    public List<ArmySnapshot> Armies = new();
    public List<CharacterSnapshot> Characters = new();   // commanders, knights, primary, player (deduplicated by Ck3Id)
}

public sealed class ArmySnapshot
{
    public long ArmyId;                               // (Army - N)
    public string Name = "";
    public int Tag;                                   // ck3xbl_aid
    public int Size;                                  // army_size (levies + MaA)
    public int Levies;
    public CharacterRef? Owner;
    public CharacterRef? Commander;
    public CultureInfo OwnerCulture = new();          // AX lines
    public bool OwnerNomadic;                         // AX|nomadic|yes: the owner's government is nomadic (PROTOCOL §5.5)
    public int? KnightEffectivenessPermille;          // A/J dump ck3xbl_a_ke as logged (may be <= 0 or > 3000); null = not logged
    public int? Development;                          // A/J dump ck3xbl_a_dev: the owner's capital county development; null = not logged / none
    public bool? Besieging;                           // AX|besieging
    public string? Territory;                         // AX|territory: "own" | "hostile" | "other"; null = not logged
    public List<string> OwnerTraits = new();          // AX|otrait
    public List<string> OwnerCultureParams = new();   // AX|ocparam
    public List<string> OwnerFaithParams = new();     // AX|ofparam
    public List<RegimentSnapshot> Regiments = new();
}

public sealed class RegimentSnapshot
{
    public long RegimentId;
    public string MaaKey = "";                        // CK3 MaA type key
    public int Current, Max;
    public bool Hired;
    public string RegimentType = "personal";          // personal | title | unknown
    public CultureInfo? HiredCulture;                 // RX lines (mercenary look)
}

public sealed class CharacterRef
{
    public long Ck3Id;
    public string? HistoricalId;
    public string Name = "";                          // as printed (first name + dynasty)
    public string? TitleKey;                          // "of c_orangallu" part
}

public sealed class CharacterSnapshot
{
    public CharacterRef Ref = new();
    public string Role = "KNIGHT";                    // CMD | KNIGHT | PRIMARY | PLAYER (a character can hold several; first wins, flags below)
    public bool IsCommander, IsKnight, IsPrimary, IsPlayer;
    public bool? Absent;                              // C dump ck3xbl_c_absent (PROTOCOL §16): true = the player is not physically in
                                                      // the combat, false = he is; null = not logged (older mod)
    public int Tag;                                   // ck3xbl_tag
    public int Age;
    public bool Female;
    public int Prowess, Martial;
    public double? Health;                            // ck3xbl_c_health (CK3 character health, base 5); null = not logged (older mod: the stat balance below stays off)
    public int Weight;                                // current_weight -100..100
    public int Tier;                                  // highest_held_title_tier 0..6
    public int ArmyTag;                               // army the character leads/fights in (0 = unknown)
    public string? PrimaryTitleKey;
    public string? HouseName;
    public CultureInfo Culture = new();               // CX lines
    public string? Ethnicity;
    public List<string> Traits = new();               // appearance-relevant traits (CX|trait)
    public List<string> CommanderTraits = new();      // commander traits held (CX|ctrait, PROTOCOL §5.5)
    public List<string> CombatTraits = new();         // CX|ktrait
    public Dictionary<string, int> TraitLevels = new(StringComparer.Ordinal);   // CX|txp: "<trait>" or "<trait>:<track>" -> 1..4
    public List<string> CultureParams = new();        // CX|cparam
    public List<string> FaithParams = new();          // CX|fparam
    public AccoladeSnapshot? Accolade;                // CX|acc_* (null = not acclaimed / not logged)
    public List<ArtifactSnapshot> Artifacts = new();  // CX|artifact (+ CX|artifact_helmet)
    public List<GearSnapshot> Gear = new();           // CX|gear, in the order CK3 lists the equipped artifacts
}

/// <summary>An equipped battle gear artifact: a Bannerlord item (PROTOCOL §16.2 CX|gear).</summary>
public sealed class GearSnapshot
{
    public string Slot = "";                          // ck3xbl_main | ck3xbl_gear | ck3xbl_head | ck3xbl_body | ... | ck3xbl_horse
    public string ItemId = "";                        // Bannerlord item id
}

/// <summary>The accolade of an acclaimed knight (PROTOCOL §13.2 CX|acc_*).</summary>
public sealed class AccoladeSnapshot
{
    public bool Active;                               // CX|acc_active
    public int Rank;                                  // CX|acc_rank 0..6
    public string? Primary;                           // CX|acc_primary (accolade_type key)
    public Dictionary<string, int> Levels = new(StringComparer.Ordinal);    // CX|acc_type key -> 0..3
}

/// <summary>An equipped weapon, armour or helmet artifact (PROTOCOL §13.2 CX|artifact).</summary>
public sealed class ArtifactSnapshot
{
    public string SlotType = "";                      // primary_armament | armor | helmet
    public string Type = "";                          // CK3 artifact_type key or "unknown"
    public string Rarity = "";                        // common | masterwork | famed | illustrious
    public string? HelmetFeature;                     // CX|artifact_helmet after an armor line
}

public sealed class CultureInfo
{
    public string? Culture;                           // culture key or "unknown"
    public string? Heritage;
    public string? UnitGfx;
    public string? ClothingGfx;
    public string? Era;
}
