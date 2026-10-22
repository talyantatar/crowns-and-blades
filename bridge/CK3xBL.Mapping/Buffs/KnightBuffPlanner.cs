// The knights pass of the mapper (PROTOCOL §13): what CK3 knights and commanders carry into Bannerlord, and what it
// changes in the CK3 write-back.
//   Side      side commander's traits -> side buffs (advantage, volatility, charge, troop death chance) and the general's
//             Tactics; the Marauder accolade adds advantage to an attacker in hostile territory
//   Groups    active accolades and the army owner's troop traits -> damage/health/morale of CK3 troop groups;
//             knight effectiveness and the knight's traits -> his retinue
//   Heroes    captaincy on the AI side, artifacts, knight effectiveness, personal trait buffs, skill adds
//             and death chance, then the per-value and combined caps
//   Write-back per-side fatal share and retreat losses, per-group pursuit and screen; fate factors
// Built only for a snapshot of a mod that logs the knights facts (BattleSnapshot.KnightFactsLogged); without them the
// mapper builds today's request member for member. Every float written to the request passes Safe (finite, clamped).
using CK3xBL.Core.Model;
using CK3xBL.Mapping.Data;
using CK3xBL.Mapping.Outcome;
using CK3xBL.Mapping.Planning;
using CK3xBL.Protocol;

namespace CK3xBL.Mapping.Buffs;

/// <summary>What the knights pass changes in the casualty conversion of one CK3 troop group.</summary>
public sealed record GroupCasualtyMods(double PursuitAdd, double PursuitMult, double ScreenAdd, double ScreenMult)
{
    /// <summary>pursuit' = max(0, pursuit + add) x max(0, 1 + mult).</summary>
    public double Pursuit(double pursuit) => Math.Max(0, pursuit + PursuitAdd) * Math.Max(0, 1 + PursuitMult);

    public double Screen(double screen) => Math.Max(0, screen + ScreenAdd) * Math.Max(0, 1 + ScreenMult);
}

/// <summary>Factors on one side's CK3 casualty conversion (commander traits).</summary>
public sealed record SideCasualtyMods(double FatalShareMult, double RetreatLossMult);

public sealed class KnightBuffPlanner
{
    // Calibration constants (INFERRED; calibrate in play).
    public const double KnightEffectivenessShare = 0.5;     // K_KE: share of knight effectiveness that becomes health x damage
    public const double KnightEffectivenessMin = 0.5, KnightEffectivenessMax = 3.0;
    public const double GroupShare = 0.5;                   // k: CK3 damage/toughness multiplier -> Bannerlord dealt/health
    public const int LinkedMaaMoralePerRank = 2, ArmyWideMoralePerRank = 1;
    // Raised with the prowess soft caps (2026-10-20): a prowess 100 hero reaches x5 health and x2.75 damage before his other effects.
    public const double HeroDurabilityCap = 6.0, HeroDealtCap = 3.0;
    public const double AcclaimedHarmFactor = 0.8;          // an active acclaimed knight's wounded/maimed/killed weights
    public const double AcclaimedKnightCaptureFactor = 0.1, AcclaimedCommanderCaptureFactor = 0.167;
    public const double DeathChanceBase = 0.3, DeathChanceMax = 0.9;
    public const double MartialArmyBonusPerPoint = 0.01, MartialArmyBonusMax = 0.30;    // commander's martial: +1 % damage and health per point, at most +30 %
    public const double MartialMoralePerPoint = 0.5;                                    // ... and +0.5 initial morale per point
    public const int AgeSlowFrom = 50;                                                  // heroes older than this lose speed
    public const double AgeSlowPerYear = 0.005, AgeSpeedFloor = 0.85;

    private static readonly string[] FormationOrder = { "Infantry", "Ranged", "Cavalry", "HorseArcher" };
    private static readonly HashSet<string> WeaponSkills = new(StringComparer.Ordinal)
        { "OneHanded", "TwoHanded", "Polearm", "Bow", "Crossbow", "Throwing", "Riding", "Athletics" };

    private sealed record ActiveAccolade(CharacterSnapshot Character, int Rank, IReadOnlyList<(string Type, int Level)> Levels);

    private sealed class SideState
    {
        public required SideSnapshot Snapshot { get; init; }
        public required TraitContext Context { get; init; }
        public CharacterSnapshot? Commander { get; init; }
        public required List<ActiveAccolade> Accolades { get; init; }
        public SideEvaluation Evaluation { get; set; } = new();
        public SideBuffs? Buffs { get; set; }
    }

    private sealed class GroupMods
    {
        public double Damage, Toughness, Morale, PursuitAdd, PursuitMult, ScreenAdd, ScreenMult;
    }

    private readonly MappingTables _t;
    private readonly BattleSnapshot _battle;
    private readonly CombatTraitEvaluator _evaluator;
    private readonly Dictionary<SideKey, SideState> _sides = new();
    private readonly Dictionary<CharacterSnapshot, TraitEvaluation> _heroEvaluations = new(ReferenceEqualityComparer.Instance);

    /// <summary>Evaluates both sides. The generals (the characters of the sides' general heroes) stand in as side commanders when
    /// the S record names none.</summary>
    public KnightBuffPlanner(MappingTables tables, BattleSnapshot battle, LocationFacts facts, CharacterSnapshot? attackerGeneral, CharacterSnapshot? defenderGeneral)
    {
        _t = tables;
        _battle = battle;
        _evaluator = new CombatTraitEvaluator(tables.Knights.CombatTraits);
        foreach (var side in battle.Sides)
        {
            var enemy = battle.Side(Other(side.Key));
            int hostility = KnightsTables.HostilityDirection == "in" ? enemy.FaithHostilityOut ?? 0 : side.FaithHostilityOut ?? 0;
            var commander = side.Commander is { } cmd ? side.Characters.FirstOrDefault(c => c.Ref.Ck3Id == cmd.Ck3Id) : null;
            _sides[side.Key] = new SideState
            {
                Snapshot = side,
                Context = new TraitContext
                {
                    Terrain = facts.Terrain ?? "plains", Winter = facts.Winter ?? "none", Coastal = facts.Coastal, Role = RoleOf(side.Key),
                    EnemyFaithHostility = Math.Clamp(hostility, 0, 3),
                    // "Hostile Territory Attacker": only the combat attacker, judged by its largest army.
                    HostileTerritory = side.Key == SideKey.Attacker && LargestArmy(side)?.Territory == "hostile",
                },
                Commander = commander ?? (side.Key == SideKey.Attacker ? attackerGeneral : defenderGeneral),
                Accolades = side.Characters.Where(c => c.Accolade is { Active: true } && c.ArmyTag != 0).Select(Active).ToList(),
            };
        }

        var att = _sides[SideKey.Attacker];
        var def = _sides[SideKey.Defender];
        double terrainAdvantage = tables.Ck3.Terrains.FirstOrDefault(x => x.Key == att.Context.Terrain)?.DefenderAdvantage ?? 0;
        var (a, d) = _evaluator.EvaluateBattle(
            new SideInput { Commander = att.Commander is { } ac ? Holder(ac) : null, Context = att.Context, ExtraAdvantage = MarauderAdvantage(att) },
            new SideInput { Commander = def.Commander is { } dc ? Holder(dc) : null, Context = def.Context, ExtraAdvantage = MarauderAdvantage(def) },
            att.Context.Winter, terrainAdvantage);
        att.Evaluation = a;
        def.Evaluation = d;
        foreach (var s in _sides.Values) s.Buffs = CapSideBuffs(s.Evaluation);
    }

    private static SideKey Other(SideKey k) => k == SideKey.Attacker ? SideKey.Defender : SideKey.Attacker;

    private static string RoleOf(SideKey k) => k == SideKey.Attacker ? "attacker" : "defender";

    private static ArmySnapshot? LargestArmy(SideSnapshot side) => side.Armies.OrderByDescending(a => a.Size).ThenBy(a => a.Tag).FirstOrDefault();

    /// <summary>A finite value clamped to its range, else the member's neutral value (NaN survives Math.Clamp, and the request JSON
    /// refuses NaN and infinities).</summary>
    public static double Safe(double v, double lo, double hi, double neutral) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : neutral;

    /// <summary>Knight effectiveness of an army owner: <c>ck3xbl_a_ke</c> in permille, clamped; not logged = 100 %.</summary>
    public static double KnightEffectiveness(int? permille) =>
        permille is { } p ? Math.Clamp(p / 1000.0, KnightEffectivenessMin, KnightEffectivenessMax) : 1;

    /// <summary>The health x damage factor a knight and his retinue get from knight effectiveness: 1 + K_KE x (KE - 1).</summary>
    public static double KnightEffectivenessFactor(double knightEffectiveness) => 1 + KnightEffectivenessShare * (knightEffectiveness - 1);

    // ---- characters --------------------------------------------------------------------------------------------------

    /// <summary>The evaluator's view of a character: appearance, combat and commander traits together.</summary>
    private static TraitHolder Holder(CharacterSnapshot c) => new()
    {
        Traits = c.Traits.Concat(c.CombatTraits).Concat(c.CommanderTraits).Distinct(StringComparer.Ordinal).ToList(),
        Levels = c.TraitLevels, CultureParameters = c.CultureParams, FaithParameters = c.FaithParams, Martial = c.Martial,
    };

    private TraitEvaluation HeroEvaluation(SideKey side, CharacterSnapshot c)
    {
        if (!_heroEvaluations.TryGetValue(c, out var ev)) _heroEvaluations[c] = ev = _evaluator.EvaluateCharacter(Holder(c), _sides[side].Context);
        return ev;
    }

    /// <summary>
    /// An active accolade with its levels. When the levels add up to more than the rank (a rank was lost), secondary attributes
    /// lose levels first, the lowest first, until they fit (INFERRED).
    /// </summary>
    private static ActiveAccolade Active(CharacterSnapshot c)
    {
        var acc = c.Accolade!;
        var levels = acc.Levels.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => Math.Min(3, kv.Value), StringComparer.Ordinal);
        int rank = Math.Max(0, acc.Rank);
        while (levels.Values.Sum() > rank)
        {
            var drop = levels.Where(kv => kv.Value > 0)
                .OrderBy(kv => kv.Key == acc.Primary).ThenBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
            levels[drop]--;
        }
        return new ActiveAccolade(c, rank, levels.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)).ToList());
    }

    private AccoladeLevel? LevelOf(string type, int level) =>
        _t.Knights.Accolades.Types.GetValueOrDefault(type)?.Bannerlord.PerLevel.GetValueOrDefault(level.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private AccoladeCaptain? CaptainOf(string type) => _t.Knights.Accolades.Types.GetValueOrDefault(type)?.Bannerlord.Captain;

    /// <summary>Marauder: the highest uncontrolled-province advantage of a knight whose army attacks in hostile territory.</summary>
    private double MarauderAdvantage(SideState s)
    {
        if (s.Snapshot.Key != SideKey.Attacker) return 0;
        double best = 0;
        foreach (var acc in s.Accolades)
        {
            if (ArmyOf(s.Snapshot, acc.Character)?.Territory != "hostile") continue;
            foreach (var (type, level) in acc.Levels) best = Math.Max(best, LevelOf(type, level)?.AdvantageIfUncontrolled ?? 0);
        }
        return best;
    }

    private static ArmySnapshot? ArmyOf(SideSnapshot side, CharacterSnapshot c) =>
        c.ArmyTag != 0 ? side.Armies.FirstOrDefault(a => a.Tag == c.ArmyTag) : null;

    /// <summary>The knight-effectiveness factor of a knight: from the owner of the army he fights in.</summary>
    private double KnightFactor(SideKey side, CharacterSnapshot c) =>
        KnightEffectivenessFactor(KnightEffectiveness(ArmyOf(_sides[side].Snapshot, c)?.KnightEffectivenessPermille));

    // ---- side --------------------------------------------------------------------------------------------------------

    private static SideBuffs? CapSideBuffs(SideEvaluation e)
    {
        var b = new SideBuffs
        {
            DamageDealtMult = (float)Safe(e.DamageDealtMult, 0.7, 1.5, 1),
            DamageTakenMult = (float)Safe(e.DamageTakenMult, 0.7, 1.3, 1),
            InitialMoraleAdd = (float)Safe(e.InitialMoraleAdd, -20, 20, 0),
            ChargeDamageMult = (float)Safe(e.ChargeDamageMult, 0.8, 1.3, 1),
            TroopDeathChanceMult = (float)Safe(e.TroopDeathChanceMult, 0.25, 2, 1),
        };
        bool neutral = b.DamageDealtMult == 1 && b.DamageTakenMult == 1 && b.InitialMoraleAdd == 0 && b.ChargeDamageMult == 1 && b.TroopDeathChanceMult == 1;
        return neutral ? null : b;
    }

    /// <summary>The side's buffs (null when all neutral).</summary>
    public SideBuffs? SideBuffsFor(SideKey side) => _sides[side].Buffs;

    /// <summary>The side commander's Tactics bonus for the side's general hero.</summary>
    public double TacticsAdd(SideKey side) => _sides[side].Evaluation.TacticsAdd;

    /// <summary>The side evaluation behind the buffs (advantage and uncapped values), for logs and tests.</summary>
    public SideEvaluation EvaluationFor(SideKey side) => _sides[side].Evaluation;

    public SideCasualtyMods SideCasualtiesFor(SideKey side)
    {
        var e = _sides[side].Evaluation;
        return new SideCasualtyMods(Safe(e.FatalShareMult, 0, 10, 1), Safe(e.RetreatLossMult, 0, 10, 1));
    }

    // ---- groups ------------------------------------------------------------------------------------------------------

    private string? BaseTypeOf(GroupKey key) => key.Kind == GroupKind.Maa ? _t.Ck3.MaaTypes.GetValueOrDefault(key.MaaKey!)?.BaseType : null;

    private static bool AccoladeCovers(AccoladeGroupEffect g, GroupKey key, string? baseType) => g.Scope switch
    {
        "maa_base_type" => key.Kind == GroupKind.Maa && baseType is not null && baseType == g.BaseType,
        "army" => key.Kind is GroupKind.Levy or GroupKind.Maa,
        _ => false,
    };

    /// <summary>Whether an army-owner effect's troop qualifier covers a group (ck3_combat_traits.json conversion.ownerTroops).</summary>
    private static bool OwnerEffectCovers(EffectQualifiers w, GroupKey key, string? baseType)
    {
        string troops = w.Troops ?? (w.MaaBaseType is not null || w.MaaType is not null ? "maa" : "army");
        return troops switch
        {
            "army" => key.Kind is GroupKind.Levy or GroupKind.Maa,
            "levy" => key.Kind == GroupKind.Levy,
            "maa" => key.Kind == GroupKind.Maa && (w.MaaBaseType is null or "all" || w.MaaBaseType == baseType) && (w.MaaType is null || w.MaaType == key.MaaKey),
            _ => false,
        };
    }

    /// <summary>The raw CK3 multipliers and morale every source gives one levy or MaA group, summed.</summary>
    private GroupMods ModsFor(SideKey side, GroupKey key)
    {
        var mods = new GroupMods();
        if (key.Kind == GroupKind.Retinue) return mods;      // retinues are not CK3 soldiers: never buffed by accolades or owners
        var s = _sides[side];
        string? baseType = BaseTypeOf(key);

        foreach (var acc in s.Accolades.Where(a => a.Character.ArmyTag == key.ArmyTag))
        {
            foreach (var (type, level) in acc.Levels)
            {
                if (LevelOf(type, level) is not { } lv) continue;
                bool covered = false;
                foreach (var g in lv.Groups.Where(g => AccoladeCovers(g, key, baseType)))
                {
                    covered = true;
                    mods.Damage += g.Ck3Value("damage_mult");
                    mods.Toughness += g.Ck3Value("toughness_mult");
                    mods.PursuitMult += g.Ck3Value("pursuit_mult");
                    mods.ScreenMult += g.Ck3Value("screen_mult");
                }
                if (!covered) continue;
                // Rank presence: the acclaimed knight's renown steadies the troops his attribute leads (INFERRED).
                mods.Morale += CaptainOf(type)?.Rule switch
                {
                    "linked_maa" => LinkedMaaMoralePerRank * acc.Rank,
                    "own_army_wide" => ArmyWideMoralePerRank * acc.Rank,
                    _ => 0,
                };
            }
        }

        // The army owner's troop traits: CK3 computes a regiment's stats with its owner's modifiers.
        if (s.Snapshot.Armies.FirstOrDefault(a => a.Tag == key.ArmyTag) is { } army)
        {
            var conv = _t.Knights.CombatTraits.Conversion.OwnerTroops;
            var owner = army.Owner is { } o ? s.Snapshot.Characters.FirstOrDefault(c => c.Ref.Ck3Id == o.Ck3Id) : null;
            var holder = new TraitHolder
            {
                Traits = army.OwnerTraits, Levels = owner?.TraitLevels ?? new Dictionary<string, int>(),
                CultureParameters = army.OwnerCultureParams, FaithParameters = army.OwnerFaithParams,
            };
            foreach (var q in _evaluator.EvaluateCharacter(holder, s.Context).Qualified.Where(q => OwnerEffectCovers(q.When, key, baseType)))
            {
                switch (q.Effect)
                {
                    case "group.damageDealtMult": mods.Damage += (q.Value - 1) / conv.DamageShare; break;
                    case "group.damageTakenMult" when q.Value > 0: mods.Toughness += (1 / q.Value - 1) / conv.ToughnessShare; break;
                    case "group.ck3DamageAdd" when BaseStat(key, toughness: false) is > 0 and var dmg: mods.Damage += q.Value / dmg; break;
                    case "group.ck3ToughnessAdd" when BaseStat(key, toughness: true) is > 0 and var tough: mods.Toughness += q.Value / tough; break;
                    case "outcome.pursuitAdd": mods.PursuitAdd += q.Value; break;
                    case "outcome.pursuitMult": mods.PursuitMult += q.Value; break;
                    case "outcome.screenAdd": mods.ScreenAdd += q.Value; break;
                    case "outcome.screenMult": mods.ScreenMult += q.Value; break;
                }
            }
        }
        return mods;
    }

    /// <summary>The CK3 base damage or toughness of a group's soldiers (ck3_catalog; levies 10).</summary>
    private double BaseStat(GroupKey key, bool toughness)
    {
        if (key.Kind == GroupKind.Levy) return toughness ? _t.Ck3.Levy.Toughness : _t.Ck3.Levy.Damage;
        var m = _t.Ck3.MaaTypes.GetValueOrDefault(key.MaaKey!);
        return m is null ? 0 : toughness ? m.Toughness : m.Damage;
    }

    /// <summary>
    /// The buffs of the side's troop groups (keys = TroopRequest.group): levies and MaA from accolades and the army owner, knight
    /// retinues from knight effectiveness and the knight's traits. Neutral groups are left out; damageTakenMult is reserved (1).
    /// </summary>
    public Dictionary<string, GroupBuffs> GroupBuffsFor(SideKey side, IEnumerable<string> groupIds)
    {
        var buffs = new Dictionary<string, GroupBuffs>(StringComparer.Ordinal);
        foreach (var id in groupIds.Distinct(StringComparer.Ordinal))
        {
            if (!GroupKey.TryParse(id, out var key)) continue;
            double dealt, health, morale;
            if (key.Kind == GroupKind.Retinue)
            {
                if (_sides[side].Snapshot.Characters.FirstOrDefault(c => c.Tag == key.CharTag) is not { } knight) continue;
                dealt = health = KnightFactor(side, knight);
                morale = HeroEvaluation(side, knight).Get("retinue", "initialMoraleAdd") ?? 0;
            }
            else
            {
                var mods = ModsFor(side, key);
                dealt = 1 + GroupShare * mods.Damage;
                health = 1 + GroupShare * mods.Toughness;
                morale = mods.Morale;
            }
            if (_sides[side].Commander is { } general && general.Martial > 0 && HeroPlanner.StatBalance(general))
            {
                // The side commander's martial skill steadies his whole army.
                double martial = 1 + Math.Min(MartialArmyBonusMax, MartialArmyBonusPerPoint * general.Martial);
                dealt *= martial;
                health *= martial;
                morale += MartialMoralePerPoint * general.Martial;
            }
            var b = new GroupBuffs
            {
                DamageDealtMult = (float)Safe(dealt, 0.5, 2.0, 1),
                HealthMult = (float)Safe(health, 0.5, 2.0, 1),
                InitialMoraleAdd = (float)Safe(morale, -20, 30, 0),
            };
            if (b.DamageDealtMult != 1 || b.HealthMult != 1 || b.InitialMoraleAdd != 0) buffs[id] = b;
        }
        return buffs;
    }

    /// <summary>Pursuit and screen changes of one CK3 troop group for the casualty conversion.</summary>
    public GroupCasualtyMods GroupCasualtiesFor(SideKey side, GroupKey key)
    {
        var m = ModsFor(side, key);
        static double Finite(double v) => double.IsFinite(v) ? v : 0;
        return new GroupCasualtyMods(Finite(m.PursuitAdd), Finite(m.PursuitMult), Finite(m.ScreenAdd), Finite(m.ScreenMult));
    }

    // ---- heroes ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Applies the hero part of the pass to the planned heroes, in the order of the design: captaincy and dismount (AI side), artifact
    /// overrides, knight effectiveness, personal trait buffs and skills, the general's Tactics, then the caps.
    /// </summary>
    public void ShapeHeroes(HeroAssignment heroes, SidePlan attacker, SidePlan defender, ForcePlanner forces)
    {
        var outfitter = new ArtifactOutfitter(_t);
        var troops = new Dictionary<SideKey, List<TroopRequest>>
        {
            [SideKey.Attacker] = ForcePlanner.Troops(attacker), [SideKey.Defender] = ForcePlanner.Troops(defender),
        };
        foreach (var h in heroes.Heroes)
        {
            var c = h.Character;
            var spec = h.Spec;
            bool general = ReferenceEquals(heroes.General(h.Side), h);
            // The player's side deploys with Order of Battle, where the player picks the captains.
            if (h.Side != heroes.PlayerSide && !spec.IsPlayer && !general) AssignCaptaincy(h, troops[h.Side]);
            if (c.Artifacts.Count > 0 || c.Gear.Count > 0) Outfit(h, outfitter, h.Side == SideKey.Attacker ? attacker : defender, forces);
            spec.Aura = AuraFor(h);
            if (c.IsKnight && !c.IsCommander)
            {
                double keF = KnightFactor(h.Side, c);
                spec.HealthMultiplier = (float)Safe(spec.HealthMultiplier * keF, 0.5, HeroDurabilityCap, 1);
                spec.DamageMultiplier = (float)Safe(spec.DamageMultiplier * keF, 0.5, HeroDealtCap, 1);   // prowess damage x knight effectiveness
            }
            ApplyTraits(spec, HeroEvaluation(h.Side, c));
            if (HeroPlanner.StatBalance(c))
            {
                ApplyProwessSpeed(spec, c.Prowess);
                ApplyAge(spec, c.Age);
            }
            // Heroes Bannerlord's AI plays fight as well as their prowess says (PROTOCOL §28); the player's own hero is the player.
            if (!spec.IsPlayer) (spec.Buffs ??= new HeroBuffs()).AiLevel = (float)HeroPlanner.AiLevel(c.Prowess);
            // PROTOCOL §33: past prowess 30 a swing cuts through a man into the next one, the player's as well as an AI hero's.
            if (c.Prowess > HeroPlanner.CleaveProwess) (spec.Buffs ??= new HeroBuffs()).Cleave = true;
            if (general && spec.Skills.TryGetValue("Tactics", out int tactics))
                spec.Skills["Tactics"] = (int)Math.Round(Safe(tactics + TacticsAdd(h.Side), 0, 300, tactics));
            CapHero(spec, _sides[h.Side].Buffs);
        }
    }

    /// <summary>Strength of a knight's aura per accolade rank (PROTOCOL §42): 2.5 % a rank, 15 % at rank 6, as Bannerlord's captain
    /// perks; morale +2 a rank while he stands, −2 a rank (at least 5) when he falls.</summary>
    public const double AuraPerRank = 0.025;
    public const int AuraMoralePerRank = 2;

    /// <summary>The formation a men-at-arms base type fights in, in Bannerlord.</summary>
    public static string? AuraFormation(string baseType) => baseType switch
    {
        "archers" or "skirmishers" or "gunpowder" => "Ranged",
        "archer_cavalry" => "HorseArcher",
        "pikemen" or "heavy_infantry" => "Infantry",
        "light_cavalry" or "heavy_cavalry" or "camel_cavalry" or "elephant_cavalry" => "Cavalry",
        _ => null,
    };

    /// <summary>
    /// PROTOCOL §42: an acclaimed knight whose (primary, else strongest) attribute leads a men-at-arms type leads every troop of
    /// his side in that type's formation, like a Bannerlord captain, for as long as he is on the field: shooters shoot harder and
    /// straighter, foot hits harder and holds, horse hits and charges harder.
    /// </summary>
    private AuraSpec? AuraFor(PlannedHero h)
    {
        var acc = _sides[h.Side].Accolades.FirstOrDefault(a => ReferenceEquals(a.Character, h.Character));
        if (acc is null || acc.Rank <= 0) return null;
        var linked = acc.Levels.Where(x => x.Level >= 1 && CaptainOf(x.Type) is { Rule: "linked_maa", LinkedBaseType: not null })
            .OrderByDescending(x => x.Type == h.Character.Accolade!.Primary).ThenByDescending(x => x.Level).ThenBy(x => x.Type, StringComparer.Ordinal)
            .Select(x => CaptainOf(x.Type)!.LinkedBaseType!).ToList();
        string? baseType = linked.FirstOrDefault(b => AuraFormation(b) is not null);
        if (baseType is null) return null;
        float s = (float)(AuraPerRank * Math.Clamp(acc.Rank, 1, 6));
        var aura = new AuraSpec
        {
            Formation = AuraFormation(baseType),
            InitialMoraleAdd = AuraMoralePerRank * acc.Rank,
            MoraleLoss = Math.Max(5, AuraMoralePerRank * acc.Rank),
        };
        switch (baseType)
        {
            case "archers" or "gunpowder" or "archer_cavalry":
                aura.RangedDamageMult = 1 + s;
                aura.RangedInaccuracyMult = 1 - s;
                break;
            case "skirmishers":
                aura.RangedDamageMult = 1 + s;
                aura.MeleeDamageMult = 1 + s / 2;
                break;
            case "pikemen" or "heavy_infantry":
                aura.MeleeDamageMult = 1 + s;
                aura.DamageTakenMult = 1 - s / 2;
                break;
            case "heavy_cavalry":
                aura.MeleeDamageMult = 1 + s;
                aura.ChargeDamageMult = 1 + 1.5f * s;
                break;
            default:                                  // light, camel and elephant cavalry
                aura.MeleeDamageMult = 1 + s;
                aura.ChargeDamageMult = 1 + s;
                break;
        }
        return aura;
    }

    /// <summary>
    /// An acclaimed knight whose (primary, else first) men-at-arms attribute leads a troop type of his army captains the Bannerlord
    /// formation that holds most agents of those groups: on foot for Infantry/Ranged, mounted for Cavalry/HorseArcher when
    /// his kit has a horse.
    /// </summary>
    private void AssignCaptaincy(PlannedHero h, List<TroopRequest> sideTroops)
    {
        var acc = _sides[h.Side].Accolades.FirstOrDefault(a => ReferenceEquals(a.Character, h.Character));
        if (acc is null) return;
        bool Linked(string type) => CaptainOf(type) is { Rule: "linked_maa", LinkedBaseType: not null };
        var held = acc.Levels.Where(x => x.Level >= 1).Select(x => x.Type).ToList();
        string? type = held.FirstOrDefault(t => t == h.Character.Accolade!.Primary && Linked(t)) ?? held.FirstOrDefault(Linked);
        if (type is null) return;
        string baseType = CaptainOf(type)!.LinkedBaseType!;

        var agents = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tr in sideTroops)
        {
            if (!GroupKey.TryParse(tr.Group, out var key) || key.Kind != GroupKind.Maa || key.ArmyTag != h.Character.ArmyTag || BaseTypeOf(key) != baseType) continue;
            if (_t.Troops.GetValueOrDefault(tr.Id)?.Formation is { } f) agents[f] = agents.GetValueOrDefault(f) + tr.Count;
        }
        string? formation = FormationOrder.Where(f => agents.GetValueOrDefault(f) > 0).OrderByDescending(f => agents[f]).FirstOrDefault();
        if (formation is null) return;      // none of his linked groups in his army

        var spec = h.Spec;
        var overrides = spec.Equipment.Overrides;
        if (formation is "Infantry" or "Ranged")
        {
            overrides["Horse"] = "";
            overrides["HorseHarness"] = "";
        }
        else if (_t.Kits.TryGetValue(spec.Equipment.Roster, out var kit) && kit.HasHorse)
        {
            // A foot culture's noble (horse emptied by the kit rule) rides with the cavalry he captains.
            if (overrides.GetValueOrDefault("Horse") == "") overrides.Remove("Horse");
            if (overrides.GetValueOrDefault("HorseHarness") == "") overrides.Remove("HorseHarness");
        }
        else return;
        spec.Formation = formation;
        spec.CaptainOf = formation;
    }

    private void Outfit(PlannedHero h, ArtifactOutfitter outfitter, SidePlan plan, ForcePlanner forces)
    {
        var spec = h.Spec;
        if (!_t.Kits.TryGetValue(spec.Equipment.Roster, out var kit)) return;
        bool mounted = spec.Formation is "Cavalry" or "HorseArcher" && spec.Equipment.Overrides.GetValueOrDefault("Horse") != "";
        string culture = forces.CharacterCulture(_battle, plan, h.Character).BlCulture;
        if (h.Character.Artifacts.Count > 0)
            foreach (var (slot, id) in outfitter.Resolve(ArtifactOutfitter.KitItems(kit), culture, mounted, h.Character.Tier, h.Character.Artifacts))
                spec.Equipment.Overrides[slot] = id;
        if (h.Character.Gear.Count == 0) return;

        // Battle gear (PROTOCOL §16.3) on top: the kit as dressed so far, then exactly the CK3 items.
        var current = ArtifactOutfitter.KitItems(kit);
        foreach (var (slot, id) in spec.Equipment.Overrides) current[slot] = id;
        var outfit = new GearOutfitter(_t).Resolve(current, h.Character.Gear);
        foreach (var (slot, id) in outfit.Overrides) spec.Equipment.Overrides[slot] = id;
        if (outfit.Mounted && spec.Formation is not ("Cavalry" or "HorseArcher"))
        {
            // Any weapon slot takes any weapon: a horse archer carries a bow or crossbow and no melee main weapon beside it.
            var mains = h.Character.Gear.Where(g => g.Slot == "ck3xbl_main").Select(g => _t.Items.GetValueOrDefault(g.ItemId)?.Type).ToList();
            spec.Formation = mains.Count > 0 && mains.All(t => t is "Bow" or "Crossbow") ? "HorseArcher" : "Cavalry";
        }
    }

    /// <summary>Personal trait effects: hero buffs, skill adds and the presentation death chance.</summary>
    private static void ApplyTraits(HeroSpec spec, TraitEvaluation ev)
    {
        var hero = ev.Scope("hero");
        double Get(string name, double neutral) => hero.TryGetValue(name, out var v) ? v : neutral;
        double all = Get("damageDealtMult", 1), melee = all, ranged = all, vsHero = 1;
        foreach (var q in ev.Qualified)
        {
            if (q.Effect == "hero.damageDealtMult")
            {
                if (q.When.Vs == "hero") vsHero *= q.Value;
                else if (q.When.Weapon == "ranged") ranged *= q.Value;
                else if (q.When.Weapon == "melee") melee *= q.Value;
                else { melee *= q.Value; ranged *= q.Value; }
            }
            else if (q.Effect == "hero.skillAdd" && q.When.Skill is { } skill && spec.Skills.TryGetValue(skill, out int current))
            {
                // Weapon skills go to the prowess soft cap (400); a trait never lowers a skill prowess already put above the cap.
                int cap = Math.Max(current, WeaponSkills.Contains(skill) ? HeroPlanner.SkillMax : 300);
                spec.Skills[skill] = (int)Math.Round(Safe(current + q.Value, 0, cap, current));
            }
        }

        var b = new HeroBuffs
        {
            DamageDealtMeleeMult = (float)Safe(melee, 0.6, 1.6, 1),
            DamageDealtRangedMult = (float)Safe(ranged, 0.6, 1.6, 1),
            DamageDealtVsHeroMult = (float)Safe(vsHero, 0.6, 1.6, 1),
            DamageTakenMult = (float)Safe(Get("damageTakenMult", 1), 0.6, 1.6, 1),
            ChargeDamageMult = (float)Safe(Get("chargeDamageMult", 1), 0.6, 1.6, 1),
            InitialMoraleAdd = (float)Safe(Get("initialMoraleAdd", 0), -50, 50, 0),
            NoPanic = Get("noPanic", 0) != 0,
            StartHealthFraction = (float)Safe(Get("startHealthFraction", 1), 0.1, 1, 1),
            SpeedMult = (float)Safe(Get("speedMult", 1), 0.6, 1.2, 1),
            SwingSpeedMult = (float)Safe(Get("swingSpeedMult", 1), 0.8, 1.2, 1),
            RangedInaccuracyMult = (float)Safe(Get("rangedInaccuracyMult", 1), 0.5, 4, 1),
            KnockdownResistanceAdd = (float)Safe(Get("knockdownResistanceAdd", 0), -0.5, 0.5, 0),
        };
        spec.Buffs = IsNeutral(b) ? null : b;

        // CK3's knight_killed factor shows as the killed-vs-unconscious chance in Bannerlord (cosmetic; CK3 fates are the bridge's).
        double deathMult = Get("deathChanceMult", 1);
        if (deathMult != 1 && double.IsFinite(deathMult)) spec.DeathChance = (float)Math.Clamp(DeathChanceBase * deathMult, 0, DeathChanceMax);
    }

    /// <summary>Prowess makes a hero quicker on foot: +0.4 % speed per point, at most +10 % (HeroPlanner.ProwessSpeed).</summary>
    private static void ApplyProwessSpeed(HeroSpec spec, int prowess)
    {
        double factor = HeroPlanner.ProwessSpeed(prowess);
        if (factor == 1) return;
        var b = spec.Buffs ??= new HeroBuffs();
        b.SpeedMult = (float)Safe(b.SpeedMult * factor, 0.6, 1.2, 1);
    }

    /// <summary>Older heroes are slower on foot: -0.5 % speed per year over 50, at most -15 %.</summary>
    private static void ApplyAge(HeroSpec spec, int age)
    {
        if (age <= AgeSlowFrom) return;
        double factor = Math.Max(AgeSpeedFloor, 1 - AgeSlowPerYear * (age - AgeSlowFrom));
        var b = spec.Buffs ??= new HeroBuffs();
        b.SpeedMult = (float)Safe(b.SpeedMult * factor, 0.6, 1.2, 1);
    }

    private static bool IsNeutral(HeroBuffs b) =>
        b.DamageDealtMeleeMult == 1 && b.DamageDealtRangedMult == 1 && b.DamageDealtVsHeroMult == 1 && b.DamageTakenMult == 1 &&
        b.ChargeDamageMult == 1 && b.InitialMoraleAdd == 0 && !b.NoPanic && b.StartHealthFraction == 1 && b.SpeedMult == 1 &&
        b.SwingSpeedMult == 1 && b.RangedInaccuracyMult == 1 && b.KnockdownResistanceAdd == 0 && b.AiLevel == 0 && !b.Cleave;

    /// <summary>
    /// Combined hero caps, after the per-value clamps: durability healthMultiplier / (hero x side damage taken) at most 4
    /// (the hero's own damage taken is raised; healthMultiplier carries prowess and knight effectiveness), and the dealt product
    /// damageMultiplier x max(melee, ranged) x vs-hero x charge at most 2 / (side dealt x side charge), reducing the factors above 1
    /// towards 1 in the order vs-hero, charge, melee and ranged together, damageMultiplier last.
    /// </summary>
    public static void CapHero(HeroSpec spec, SideBuffs? side)
    {
        double sideTaken = side?.DamageTakenMult ?? 1, sideDealt = side?.DamageDealtMult ?? 1, sideCharge = side?.ChargeDamageMult ?? 1;
        var b = spec.Buffs ?? new HeroBuffs();
        if (spec.HealthMultiplier / (b.DamageTakenMult * sideTaken) > HeroDurabilityCap)
            b.DamageTakenMult = (float)(spec.HealthMultiplier / (HeroDurabilityCap * sideTaken));

        double limit = HeroDealtCap / (sideDealt * sideCharge);
        double vs = b.DamageDealtVsHeroMult, charge = b.ChargeDamageMult, melee = b.DamageDealtMeleeMult, ranged = b.DamageDealtRangedMult;
        double damage = spec.DamageMultiplier;
        double Excess() => damage * Math.Max(melee, ranged) * vs * charge / limit;
        static double Toward1(double f, double excess) => f > 1 && excess > 1 ? Math.Max(1, f / excess) : f;
        if (Excess() > 1)
        {
            vs = Toward1(vs, Excess());
            charge = Toward1(charge, Excess());
            double cut = Math.Min(Excess(), Math.Max(melee, ranged));
            if (cut > 1)
            {
                melee = melee > 1 ? Math.Max(1, melee / cut) : melee;
                ranged = ranged > 1 ? Math.Max(1, ranged / cut) : ranged;
            }
            damage = Toward1(damage, Excess());
            (b.DamageDealtVsHeroMult, b.ChargeDamageMult, b.DamageDealtMeleeMult, b.DamageDealtRangedMult) = ((float)vs, (float)charge, (float)melee, (float)ranged);
            spec.DamageMultiplier = (float)damage;
        }
        spec.Buffs = IsNeutral(b) ? null : b;
    }

    // ---- fates -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Factors on a downed hero's CK3 fate weights: each held trait's phase-event factors (knight or commander
    /// table); an active acclaimed knight is harmed and captured less; enemy Blademaster / Crossbow Captain raise a knight's
    /// death weight (the highest level on the enemy side); House Paragon of his own side shields knights of his liege's house.
    /// </summary>
    public FateFactors FateFactorsFor(SideKey side, CharacterSnapshot c, bool asCommander)
    {
        double w = 1, m = 1, k = 1, capture = 1;
        foreach (var trait in Holder(c).Traits)
        {
            if (_t.Knights.CombatTraits.Traits.GetValueOrDefault(trait)?.Fate is not { } f) continue;
            w *= (asCommander ? f.CommanderWounded : f.KnightWounded) ?? 1;
            m *= (asCommander ? f.CommanderMaimed : f.KnightMaimed) ?? 1;
            k *= (asCommander ? f.CommanderKilled : f.KnightKilled) ?? 1;
        }
        var s = _sides[side];
        if (s.Accolades.Any(a => ReferenceEquals(a.Character, c)))
        {
            w *= AcclaimedHarmFactor; m *= AcclaimedHarmFactor; k *= AcclaimedHarmFactor;
            capture *= asCommander ? AcclaimedCommanderCaptureFactor : AcclaimedKnightCaptureFactor;
        }
        if (!asCommander)
        {
            k *= AccoladeFateFactor(_sides[Other(side)], "enemy_knight_death_weight", strongest: Math.Max);
            double house = AccoladeFateFactor(s, "own_house_knight_harm_weight", strongest: Math.Min);
            if (house != 1 && c.HouseName is { } name && OwnerHouse(s.Snapshot, c) == name) { w *= house; m *= house; k *= house; }
        }
        return new FateFactors(Safe(w, 0, 1e3, 1), Safe(m, 0, 1e3, 1), Safe(k, 0, 1e3, 1), Safe(capture, 0, 1, 1));
    }

    /// <summary>The strongest factor of one accolade fate effect among a side's active accolades (CK3 applies only the highest
    /// of low/medium/high); 1 when none.</summary>
    private double AccoladeFateFactor(SideState s, string effect, Func<double, double, double> strongest)
    {
        double? best = null;
        foreach (var acc in s.Accolades)
            foreach (var (type, level) in acc.Levels)
                if (LevelOf(type, level)?.FateFactors?.GetValueOrDefault(effect) is { } f and > 0)
                    best = best is { } b ? strongest(b, f) : f;
        return best ?? 1;
    }

    /// <summary>The house of the owner of the army the knight fights in (null when unknown).</summary>
    private static string? OwnerHouse(SideSnapshot side, CharacterSnapshot c) =>
        ArmyOf(side, c)?.Owner is { } owner ? side.Characters.FirstOrDefault(x => x.Ref.Ck3Id == owner.Ck3Id)?.HouseName : null;
}
