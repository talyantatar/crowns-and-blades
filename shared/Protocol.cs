// Crowns & Blades protocol v2 DTOs. See docs/PROTOCOL.md §3 (and the additive fields of §13).
// Compiled into BOTH the bridge (net10.0-windows, System.Text.Json, camelCase policy, IncludeFields = true)
// and the Bannerlord module (net472, Newtonsoft.Json with CamelCasePropertyNamesContractResolver).
// Rules: plain public fields only, no attributes, no LINQ, no C# features newer than C# 7.3 (the module builds with
// LangVersion latest, but keep it simple), no dependencies outside the base class library.
// Additive fields (PROTOCOL §13) carry defaults that reproduce the behaviour before they existed: a reader that gets a
// request without them (an older bridge, a hand-dropped request) behaves exactly as before. ProtocolInfo.Version stays 2.
using System.Collections.Generic;

namespace CK3xBL.Protocol
{
    public static class ProtocolInfo
    {
        public const int Version = 2;
        public const string FolderName = "ck3xbl";          // under %LOCALAPPDATA%
        public const string RequestFile = "request.json";
        public const string ResultFile = "result.json";
        public const string StatusFile = "bannerlord_status.json";
        public const string CommandFile = "command.txt";
    }

    public class BattleRequest
    {
        public int Protocol = ProtocolInfo.Version;
        public string Id;                                // "b17_ab12"
        public string Kind = "field";                    // field | arena (a single combat, PROTOCOL §27) | hideout (§37) | siege (§39, §40)
        public string FallbackScene;                     // PROTOCOL §27: field scene for an arena the game cannot load
        public int Tourney;                              // PROTOCOL §29: 0 no tournament, 1 single combat, 2 wrestling (bare hands),
                                                         // 3 grand melee, 4 joust (mounted, blunt lances), 5 archery (§36)
        public int TourneyPar;                           // §36 archery: the targets to hit to beat the best other archer
        public string Scene = "battle_terrain_029";
        public string Season = "summer";                 // summer | fall | winter | spring
        public float TimeOfDay = 12f;
        public string PlayerSide = "attacker";           // attacker | defender
        public bool PlayerIsGeneral = true;
        public Presentation Presentation = new Presentation();
        public ScaleInfo Scale = new ScaleInfo();
        public SideRequest Attacker;
        public SideRequest Defender;
        public SiegeInfo Siege;                          // only for kind = siege
        public NavalInfo Naval;                          // only for kind = naval (PROTOCOL §44)
        public BalanceInfo Balance;                      // bridge-side record of the strength balance (PROTOCOL §17); the module ignores it
        // PROTOCOL §21. Costumes by TroopRequest.Look key; null = stock troop armour (as before).
        public Dictionary<string, LookSpec> Looks;
    }

    /// <summary>PROTOCOL §21: the items that dress one CK3 culture, era and role, by slot (Head, Body, Leg, Gloves, Cape, HorseHarness).</summary>
    public class LookSpec
    {
        public Dictionary<string, List<LookItem>> Slots = new Dictionary<string, List<LookItem>>();
    }

    public class LookItem
    {
        public string Id;                                // Bannerlord item id; ids the game did not load are skipped
        public float Weight = 1f;                        // relative chance among items of about the same armour
    }

    /// <summary>PROTOCOL §17: how the bridge evened the sides out (recorded in request.json for calibration).</summary>
    public class BalanceInfo
    {
        public double Ck3Attacker;                       // CK3 strength: (sum men x damage) x (sum men x toughness)
        public double Ck3Defender;
        public double BannerlordAttacker;                // estimate: (sum agents x worth of one agent)^2
        public double BannerlordDefender;
        public double Factor = 1;                        // strength factor given to Boosted (damage and health each x sqrt)
        public string Boosted;                           // attacker | defender | null (no change)
        public bool Capped;                              // the factor hit the configured maximum
        public double TiersPerDoubling;                  // the calibration constant used
    }

    public class Presentation
    {
        public bool Preload = true;
        public string Title;
        public string Subtitle;
        public string Detail;
        public string Objective;
        public int ReturnCountdownSeconds = 10;          // -1 = wait for the player
        public string Deployment = "order_of_battle";    // order_of_battle | auto
        public string Language = "en";                   // PROTOCOL §26: en | ru, for the module's own texts (footer, defaults)
        public string AttackerCoaPng;                    // optional absolute path to a rendered CK3 coat of arms (title cards)
        public string DefenderCoaPng;

        // PROTOCOL §13.4. true (default; also for requests without the field, e.g. hand-dropped ones): the module shows its own
        // in-engine title card as before. false: the bridge shows the title card itself; the module's loading card and reveal
        // show only the black backdrop (no title text, no coats of arms), which fades out over 1.2 s after the reveal while the
        // bridge's card dissolves into the battle. The bridge always sends false; when its own card could not be shown it sends
        // the module command "title_in_engine", after which the module treats the held battle as true.
        public bool InEngineTitle = true;
    }

    public class ScaleInfo
    {
        public float MenPerAgent = 1f;
        public int BattleSize = 500;                     // agents on the field at once (BL battle size setting to apply)
    }

    public class SiegeInfo
    {
        public int SceneUpgradeLevel = 1;                // 1..3
        public float[] WallHitpointPercentages;          // e.g. { 1, 1 }; 0 = a breach
        public bool HasSiegeTower;
        public List<string> AttackerMachines = new List<string>();   // BL siege engine ids (DefaultSiegeEngineTypes property names)
        public List<string> DefenderMachines = new List<string>();
        public float Progress;                           // §39: the bridge's estimate of the CK3 siege's progress, 0..1
        // §46: the assault comes in waves: at most this many attackers per defender on the field at once (the rest reinforce as the
        // first waves fall); 0 = Bannerlord's own split of the battle size.
        public float AttackerWaveRatio;
        // Bannerlord's sally out and relief force attack; the bridge sends neither (§40: the custom battle siege scenes carry no
        // sally_out_set or relief_force_attack_set spawn points).
        public bool SallyOut;
        public bool ReliefForceAttack;
        // Bridge-side records (the module ignores them), §40: the player holds the walls (else he is outside them); the request's
        // sides were swapped to put him there, and the bridge swaps the result back; the agents each troop group lost breaking
        // into the holding, counted as killed in the outcome.
        public bool PlayerDefends;
        public bool SidesSwapped;
        public Dictionary<string, int> BreakIn;
        public float BreakInShare;
    }

    /// <summary>PROTOCOL §44: a battle at sea (War Sails). Ships are ShipHull ids of the NavalDLC; their crews come from the side's
    /// troops and heroes.</summary>
    public class NavalInfo
    {
        public bool Raid;                                // a landing: the attackers come ashore from their ships on a raid scene
        public List<string> AttackerShips = new List<string>();
        public List<string> DefenderShips = new List<string>();   // unused for a landing (the defenders hold the shore)
        public float WindStrength = 0.5f;                // 0..1
        public string WindDirection = "Random";          // TowardsDefender | TowardsAttacker | Side | Random
        public string Terrain = "CoastalSea";            // OpenSea | CoastalSea | River
    }

    public class SideRequest
    {
        public string Name;
        public string Culture = "vlandia";
        public BannerSpec Banner;
        public string General;                           // a HeroSpec.Key, or a stock BL character id
        public List<HeroSpec> Heroes = new List<HeroSpec>();
        public List<TroopRequest> Troops = new List<TroopRequest>();

        // PROTOCOL §13.4. Side-wide buffs (commander advantage, traits); null = none (as before).
        public SideBuffs Buffs;
        // PROTOCOL §13.4. Buffs per troop group, keyed by TroopRequest.Group exactly as sent (keys verbatim, like groups in
        // results): knight effectiveness of a knight's retinue, accolade and army-owner bonuses of CK3 regiments. A group
        // without an entry is unbuffed (as before).
        public Dictionary<string, GroupBuffs> GroupBuffs = new Dictionary<string, GroupBuffs>();
    }

    /// <summary>PROTOCOL §13.4: applies to every agent of one side (heroes and troops). Neutral values = no effect.</summary>
    public class SideBuffs
    {
        public float DamageDealtMult = 1f;               // every hit dealt by an agent of this side (mount charges credited to the rider)
        public float DamageTakenMult = 1f;               // every hit taken by a human agent of this side
        public float InitialMoraleAdd;                   // added to every agent's initial morale (Bannerlord clamps to 15..100)
        public float ChargeDamageMult = 1f;              // extra factor on horse charges and mounted melee hits
        public float TroopDeathChanceMult = 1f;          // factor on the killed-vs-unconscious roll of this side's downed troops
    }

    /// <summary>PROTOCOL §13.4: applies to every agent spawned for one troop group. Neutral values = no effect.</summary>
    public class GroupBuffs
    {
        public float DamageDealtMult = 1f;
        public float DamageTakenMult = 1f;               // honoured by the module; reserved (the bridge's knights pass leaves it 1)
        public float HealthMult = 1f;                    // spawn health pool (HealthLimit and Health) of each agent of the group
        public float InitialMoraleAdd;                   // added once when the agent is built
    }

    public class BannerSpec
    {
        public string Code;                              // Bannerlord banner code string
        public string Source;                            // e.g. "title:c_orangallu" (informational)
    }

    public class TroopRequest
    {
        public string Id;                                // BL troop (NPCCharacter) id
        public int Count;
        public string Group;                             // opaque tag echoed back in results, e.g. "levy#16777235", "maa:armored_footmen#16777235"
        public string Look;                              // PROTOCOL §21: key of BattleRequest.Looks; null = stock armour
        public string Mount;                             // PROTOCOL §43: the horse item every man of the group rides (an elephant); null = the troop's own
        public string MountHarness;                      // its harness; null = none
    }

    public class HeroSpec
    {
        public string Key;                               // "h0"
        public long Ck3Id;                               // CK3 internal character id
        public int Tag;                                  // ck3xbl_tag
        public string Role;                              // player | commander | knight | primary
        public bool IsPlayer;
        public string Name;
        public bool Female;
        public int Age = 30;
        public int Level = 20;
        public string Formation = "Infantry";            // Infantry | Ranged | Cavalry | HorseArcher
        public bool Captain;
        public AppearanceSpec Appearance;
        public EquipmentSpec Equipment;
        public Dictionary<string, int> Skills = new Dictionary<string, int>();
        public float HealthMultiplier = 1f;              // whole health pool: prowess x knight effectiveness (PROTOCOL §13.3)

        // PROTOCOL §13.4 additions. Defaults = no effect.
        public float DamageMultiplier = 1f;              // every hit this hero deals (knight effectiveness; mount charges included)
        public string CaptainOf;                         // FormationClass name (Infantry|Ranged|Cavalry|HorseArcher) this hero should
                                                         // captain; null = stock captain assignment. Acts only on a team that does
                                                         // not deploy with Order of Battle (in practice the AI side; the bridge sets
                                                         // it only there)
        public float? DeathChance;                       // killed-vs-unconscious chance when downed (cosmetic in Bannerlord; CK3 fates
                                                         // are decided by the bridge); null = the module default. Replaces the raw
                                                         // heroes[].deathChance read of module stage 1 (same JSON key).
        public HeroBuffs Buffs;                          // personal combat-trait effects; null = none
        public BannerSpec Banner;                        // PROTOCOL §25: the hero's own arms (title, else house); null = the side's
        public string Look;                              // PROTOCOL §21: key of BattleRequest.Looks for the armour slots that
                                                         // Equipment.Overrides leaves to the kit; null = the kit's own armour
        public AuraSpec Aura;                            // PROTOCOL §42: an acclaimed knight leads a formation; null = none
    }

    /// <summary>
    /// PROTOCOL §42: what an acclaimed knight gives the troops (not heroes) of his side in one formation while he is on the field,
    /// like a Bannerlord captain. Neutral values = no effect. When he falls, the formation's troops lose MoraleLoss.
    /// </summary>
    public class AuraSpec
    {
        public string Formation;                         // Infantry | Ranged | Cavalry | HorseArcher
        public float MeleeDamageMult = 1f;
        public float RangedDamageMult = 1f;
        public float ChargeDamageMult = 1f;              // horse charges and mounted melee hits
        public float DamageTakenMult = 1f;
        public float RangedInaccuracyMult = 1f;          // below 1 = straighter shots
        public float InitialMoraleAdd;                   // troops that take the field while he stands
        public float MoraleLoss;                         // taken from the formation's troops when he falls
    }

    /// <summary>PROTOCOL §13.4: personal effects of one hero (CK3 combat traits). Neutral values = no effect.</summary>
    public class HeroBuffs
    {
        public float DamageDealtMeleeMult = 1f;
        public float DamageDealtRangedMult = 1f;
        public float DamageDealtVsHeroMult = 1f;         // extra factor on melee hits against hero agents
        public float DamageTakenMult = 1f;
        public float ChargeDamageMult = 1f;
        public float InitialMoraleAdd;
        public bool NoPanic;                             // never flees because of low morale
        public float StartHealthFraction = 1f;           // starts with this share of the (multiplied) health pool, e.g. 0.5 for wounded_3
        public float SpeedMult = 1f;
        public float SwingSpeedMult = 1f;
        public float RangedInaccuracyMult = 1f;
        public float KnockdownResistanceAdd;
        public float AiLevel;                            // PROTOCOL §28: 0..1, how well an AI-controlled hero blocks, parries and
                                                         // attacks (from prowess); 0 = Bannerlord's own AI; never for the player's agent
        public bool Cleave;                              // PROTOCOL §33: swings cut through a man into the next one (prowess over 30)
    }

    public class AppearanceSpec
    {
        public string Source = "stock";                  // meta_portrait | save_dna | parent_inherited | ethnicity_seeded | stock
        public float Weight = 0.5f;
        public float Build = 0.5f;
        public float Height = 0.5f;
        public float Skin = 0.5f;
        public float HairColor = 0.5f;
        public float EyeColor = 0.5f;
        public StyleSpec Hair = new StyleSpec();
        public StyleSpec Beard = new StyleSpec();
        public Dictionary<string, float> Morphs = new Dictionary<string, float>();   // BL deform key id -> -1..1
        public string BodyProperties;                    // optional raw "<BodyProperties .../>" override
    }

    public class StyleSpec
    {
        public int? Index;
        public List<string> Tags = new List<string>();
        public bool Bald;
    }

    public class EquipmentSpec
    {
        public string Roster;                            // BL EquipmentRoster / equipment set id
        public Dictionary<string, string> Overrides = new Dictionary<string, string>();  // slot (Item0..Item4, Head, Body, Leg, Gloves, Cape, Horse, HorseHarness) -> item id
    }

    public class BattleResult
    {
        public int Protocol = ProtocolInfo.Version;
        public string Id;
        public string Status;                            // started | result | finished | aborted | cancelled | error
        public string Winner;                            // attacker | defender | none
        public bool PlayerRetreated;
        public float DurationSeconds;
        public SideResult Attacker = new SideResult();
        public SideResult Defender = new SideResult();
        public List<HeroResult> Heroes = new List<HeroResult>();
        public string Error;
    }

    public class SideResult
    {
        public int Requested;
        public int Spawned;
        public int Killed;
        public int Wounded;
        public int Routed;
        public Dictionary<string, GroupResult> Groups = new Dictionary<string, GroupResult>();
    }

    public class GroupResult
    {
        public int Requested;
        public int Spawned;
        public int Killed;
        public int Wounded;
        public int Routed;
    }

    public class HeroResult
    {
        public string Key;
        public long Ck3Id;
        public int Tag;
        public string State;                             // alive | killed | wounded | routed | unspawned
        public int Kills;
        public string KilledBy;                          // hero key of the killer, or null
        public float? Health;                            // a hero still on the field: health left, 0..1 of his full pool (PROTOCOL §32)
    }

    public class BannerlordStatus
    {
        public string Status;                            // booting|idle|loading|held|visible|fighting|result|fading_out|black|ended|cancelled|aborted|error
        public string BattleId;
        public long T;                                   // unix ms
        public int Pid;
        public long Hwnd;
        public string Error;
    }
}
