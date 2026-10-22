// CK3xBL.Mapping entry point: CK3 battle snapshot -> Bannerlord request (PROTOCOL §3.2), Bannerlord result -> CK3 outcome
// (PROTOCOL §2.1). Ports tools/data/mapping_resolver.js and tools/data/battlefield_resolver.js (PROTOCOL §6).
// Stateless apart from the shared, read-only data tables.
// The knights pass (PROTOCOL §13, KnightBuffPlanner) runs only for snapshots of a mod that logs the knights facts
// (BattleSnapshot.KnightFactsLogged): an older mod gets today's request and outcome member for member (PROTOCOL §13.1).
// Battle clock (PROTOCOL §14): outcomes are keypad payload 4 (kill targets, absolute levy
// survivors) unless MapperOptions.BattleClock says otherwise, and a reinforcement clash is planned from the live snapshot capped at
// the earlier clashes' end state (ContinuationSnapshot), on the earlier clash's field, with cumulative targets.
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Model;
using CK3xBL.Core.Texts;
using CK3xBL.Mapping.Buffs;
using CK3xBL.Mapping.Continuation;
using CK3xBL.Mapping.Data;
using CK3xBL.Mapping.Outcome;
using CK3xBL.Mapping.Planning;
using CK3xBL.Mapping.Resolution;
using CK3xBL.Protocol;

namespace CK3xBL.Mapping;

/// <summary>Everything decided for one battle; <see cref="Request"/> is what goes to Bannerlord, the rest explains it.</summary>
public sealed class BattlePlan
{
    public required BattleRequest Request { get; init; }
    public required BattlefieldChoice Battlefield { get; init; }
    public required LocationFacts Location { get; init; }
    public required SidePlan Attacker { get; init; }
    public required SidePlan Defender { get; init; }
    public required HeroAssignment Heroes { get; init; }
}

public sealed class BattleMapper : IBattleMapper
{
    public BattleRequest BuildRequest(BattleSnapshot battle, MapperOptions options) => Plan(battle, options).Request;

    public Ck3Outcome BuildOutcome(BattleSnapshot battle, BattleRequest request, BattleResult result, MapperOptions options) =>
        BuildOutcome(battle, request, result, options, null);

    /// <summary>The outcome of a clash; <paramref name="battle"/> is the snapshot the request was planned from (the capped one of a
    /// continuation), and <paramref name="parent"/> the clash it continues (targets and r4..r6 cumulative), or null.</summary>
    public Ck3Outcome BuildOutcome(BattleSnapshot battle, BattleRequest request, BattleResult result, MapperOptions options, ParentLink? parent)
    {
        (request, result) = SiegeSides.AsCk3(request, result);
        var tables = MappingTables.Get(options.DataDirectory);
        var facts = EffectiveFacts(battle, tables);
        KnightBuffPlanner? knights = null;
        if (battle.KnightFactsLogged)
        {
            CharacterSnapshot? General(SideKey key)
            {
                var side = key == SideKey.Attacker ? request.Attacker : request.Defender;
                var spec = side?.Heroes.FirstOrDefault(h => h.Key == side.General);
                var c = spec is null ? null : battle.Side(key).Characters.FirstOrDefault(x => x.Ref.Ck3Id == spec.Ck3Id);
                // A stand-in player (PROTOCOL §15) leads in Bannerlord only: not the side's CK3 commander (HeroAssignment.Ck3Commander).
                return c is not null && HeroPlanner.IsPresent(c) ? c : null;
            }
            knights = new KnightBuffPlanner(tables, battle, facts, General(SideKey.Attacker), General(SideKey.Defender));
        }
        return new OutcomeBuilder(tables, battle, request, result, options, facts, knights, parent).Build();
    }

    /// <summary>
    /// Plans a reinforcement clash (PROTOCOL §14.6): the live snapshot capped at the earlier clashes' end state, fought on the
    /// earlier clash's field (scene, season, kind) at the next stock hour, with the earlier player side, coats of arms, staging and
    /// detail under the title "Reinforcements arrive!". The outcome of the clash is built from <see cref="ContinuationPlan.Capped"/>.
    /// </summary>
    public ContinuationPlan BuildContinuation(BattleSnapshot live, ParentLink parent, MapperOptions options)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var capped = ContinuationSnapshot.Cap(live, parent);
        var request = Plan(capped, options).Request;
        var earlier = parent.Request;
        request.Kind = earlier.Kind;
        request.Scene = earlier.Scene;
        request.Season = earlier.Season;
        request.TimeOfDay = NextStockHour(earlier.TimeOfDay);
        request.PlayerSide = earlier.PlayerSide;
        request.Presentation = PresentationWriter.Continuation(capped, parent, request.Presentation);
        return new ContinuationPlan { Request = request, Capped = capped };
    }

    private static readonly int[] DaytimeStockHours = { 6, 12, 15, 18 };

    /// <summary>The stock custom-battle hour after an earlier clash's: the day moves on but never into the night (6 -> 12 -> 15 -> 18;
    /// 18 and a night battle at 22 stay).</summary>
    private static float NextStockHour(float hour) => DaytimeStockHours.Where(h => h > hour).Select(h => (float)h).DefaultIfEmpty(hour).First();

    /// <summary>Resolves the whole battle (troops, heroes, battlefield, presentation). Deterministic for a given snapshot.</summary>
    public BattlePlan Plan(BattleSnapshot battle, MapperOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Plan(battle, options, MappingTables.Get(options.DataDirectory));
    }

    /// <summary>The same against given tables (MapperOptions.DataDirectory is not consulted).</summary>
    public BattlePlan Plan(BattleSnapshot battle, MapperOptions options, MappingTables tables)
    {
        ArgumentNullException.ThrowIfNull(battle);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tables);
        var troops = new TroopResolver(tables.Mapping);
        var forcePlanner = new ForcePlanner(tables, troops);
        var facts = EffectiveFacts(battle, tables);
        // A band's leader is as hard as the year's bands (PROTOCOL §28); idempotent, so replanning the snapshot changes nothing.
        if (battle.IsSkirmish)
            foreach (var side in battle.Sides.Where(s => !s.PlayerIn))
                foreach (var c in side.Characters)
                    c.Prowess = Math.Max(c.Prowess, ForcePlanner.SkirmishLeaderProwess(battle.Year));

        var (attacker, defender) = forcePlanner.Plan(battle, facts, options);
        var heroes = new HeroPlanner(tables, troops, forcePlanner).Plan(battle, attacker, defender);
        var knights = battle.KnightFactsLogged
            ? new KnightBuffPlanner(tables, battle, facts, heroes.Ck3Commander(SideKey.Attacker), heroes.Ck3Commander(SideKey.Defender))
            : null;
        knights?.ShapeHeroes(heroes, attacker, defender, forcePlanner);
        // Costumes (PROTOCOL §21): a hero's kit armour takes the look of their own culture and era; CK3 gear and artifacts stay.
        foreach (var h in heroes.Heroes)
        {
            var culture = forcePlanner.CharacterCulture(battle, h.Side == SideKey.Attacker ? attacker : defender, h.Character);
            h.Spec.Look = CostumePlanner.Key(culture.Profile, culture.Era, CostumePlanner.RoleOfFormation(h.Spec.Formation));
        }

        int agents = attacker.TroopAgents + defender.TroopAgents + heroes.Heroes.Count;
        // A single combat with a lowborn foe and a skirmish on the road are fought in the woods (PROTOCOL §27): the field is chosen
        // as for a wooded province of the same climate, away from any holding.
        bool woods = Woods(battle);
        var field = new BattlefieldResolver(tables).Resolve(new BattlefieldInput
        {
            ProvinceId = battle.Location.ProvinceId,
            Year = battle.Year, Month = battle.Month, Day = battle.Day, BattleSeq = battle.Bid,
            Terrain = woods ? WoodsTerrain(facts.Terrain) : facts.Terrain, Winter = facts.Winter,
            Holding = woods ? null : facts.Holding, BuildingGfx = woods ? null : facts.BuildingGfx ?? ReliefSideBuildingGfx(tables, attacker),
            Heritage = facts.Heritage, SiegeHere = !woods && facts.SiegeHere, Coastal = !woods && facts.Coastal, Riverside = !woods && facts.Riverside,
            ClimateRegion = facts.Region, Agents = agents,
        });

        int soldiers = attacker.Soldiers + defender.Soldiers;
        int groupAgents = attacker.GroupAgents + defender.GroupAgents;
        var playerPlan = heroes.PlayerSide == SideKey.Attacker ? attacker : defender;
        var words = Words.For(options.Language);
        var request = new BattleRequest
        {
            Protocol = ProtocolInfo.Version,
            Id = battle.RequestId,
            Kind = "field",            // always: sieges stay in CK3 and Siege stays null (PROTOCOL §0)
            Scene = field.Scene,
            Season = field.Season,
            TimeOfDay = field.LegacyTimeOfDay,
            PlayerSide = heroes.PlayerSide == SideKey.Attacker ? "attacker" : "defender",
            PlayerIsGeneral = heroes.PlayerIsGeneral,
            Presentation = PresentationWriter.Build(battle, field, heroes, PresentationWriter.SideName(playerPlan.Snapshot, words), options),
            Scale = new ScaleInfo { MenPerAgent = (float)JsCompat.R2(soldiers / (double)Math.Max(1, groupAgents)), BattleSize = options.BattleSize },
            Attacker = SideRequestFor(tables, troops, attacker, heroes, knights, words),
            Defender = SideRequestFor(tables, troops, defender, heroes, knights, words),
        };
        if (battle.IsDuel) ShapeDuel(request, heroes, battle, words);
        else if (!battle.IsSiege && !Relieving(battle, options)) request.Balance = StrengthBalance.Apply(tables, attacker, defender, request, options);
        if (battle.IsDuel && (battle.DuelNoble == true || battle.DuelTourney > 0)) PutInArena(request);
        if (battle.IsSkirmish) ShapeSkirmish(request, heroes, battle, words);
        if (battle.IsSiege) ShapeSiege(request, battle, facts, words);
        if (Relieving(battle, options)) ShapeRelief(request, battle, facts, heroes, options.ReliefMode == ReliefDefend, words);
        else if (AtSea(battle, options)) ShapeNaval(request, battle, words);
        if (options.Elephants) ElephantPlanner.Mount(request, tables);
        CostumePlanner.Dress(request, tables, options.Costumes);
        return new BattlePlan
        {
            Request = request, Battlefield = field, Location = facts, Attacker = attacker, Defender = defender, Heroes = heroes,
        };
    }

    // ---- venues (PROTOCOL §27) -----------------------------------------------------------------------------------------

    /// <summary>The six Bannerlord arenas (SandBox), one per culture.</summary>
    public static readonly IReadOnlySet<string> ArenaCultures = new HashSet<string>(StringComparer.Ordinal)
        { "aserai", "battania", "empire", "khuzait", "sturgia", "vlandia" };

    /// <summary>Fought in the woods: a single combat with a lowborn foe, and every skirmish on the road.</summary>
    public static bool Woods(BattleSnapshot battle) => battle.IsSkirmish || (battle.IsDuel && battle.DuelNoble == false && battle.DuelTourney == 0);

    /// <summary>The wooded CK3 terrain: taiga and jungle stay, everything else becomes forest (season and climate still pick
    /// snow, autumn or summer woods).</summary>
    public static string WoodsTerrain(string? terrain) => terrain is "taiga" or "jungle" or "forest" ? terrain : "forest";

    /// <summary>
    /// A noble single combat is fought in the arena of the challenged side's culture (kind "arena", PROTOCOL §27). The field scene
    /// stays as fallbackScene: the module opens it instead when the arena cannot be loaded (SandBox missing from the launch).
    /// </summary>
    private static void PutInArena(BattleRequest request)
    {
        string culture = ArenaCultures.Contains(request.Defender?.Culture ?? "") ? request.Defender!.Culture
            : ArenaCultures.Contains(request.Attacker?.Culture ?? "") ? request.Attacker!.Culture : "vlandia";
        // Only three arenas carry the archery set (SandBox tournament_archery_set, PROTOCOL §36).
        if (request.Tourney == Archery && culture is not ("aserai" or "battania" or "empire")) culture = "empire";
        request.FallbackScene = request.Scene;
        request.Kind = "arena";
        request.Scene = $"arena_{culture}_a";
    }

    /// <summary>Bannerlord's death chance of a downed duelist by CK3's fatality rule (PROTOCOL §18): never for "no" and "practice",
    /// always for "always", even odds where CK3 may let the loser die ("possible", and "default", which CK3 decides by government).</summary>
    public static float DuelDeathChance(int fatality) => fatality switch { 2 => 1f, 1 or 3 => 0.5f, _ => 0f };

    /// <summary>A single combat (PROTOCOL §18): the two duelists alone, the death chance of CK3's rule, its own titles.</summary>
    private static void ShapeDuel(BattleRequest request, HeroAssignment heroes, BattleSnapshot battle, Words w)
    {
        float death = DuelDeathChance(battle.DuelFatality);
        foreach (var h in heroes.Heroes) h.Spec.DeathChance = death;
        foreach (var (side, key) in new[] { (request.Attacker, SideKey.Attacker), (request.Defender, SideKey.Defender) })
            if (heroes.Heroes.FirstOrDefault(h => h.Side == key) is { } duelist) side.General = duelist.Spec.Key;
        var p = request.Presentation ??= new Presentation();
        string me = heroes.Avatar?.Spec.Name ?? w.You;
        string foe = heroes.Heroes.FirstOrDefault(h => h.Side != heroes.PlayerSide)?.Spec.Name ?? w.YourFoe;
        p.Title = battle.DuelTourney > 0 ? w.Tourney(battle.DuelTourney) : w.SingleCombat;
        p.Subtitle = w.Against(me, foe);
        p.Objective = battle.DuelTourney == 3 ? w.WinTheMelee : w.DefeatFoe(foe);
        p.Deployment = "auto";
        request.Tourney = battle.DuelTourney;
        if (battle.DuelTourney != Archery) ApplyProwessGap(heroes);
        if (battle.DuelTourney == WrestlingBout) Unarm(heroes);
        if (battle.DuelTourney == Joust) Arm(heroes, JoustKit, "Cavalry", TournamentHorse(request.Defender?.Culture ?? request.Attacker?.Culture));
        if (battle.DuelTourney == Archery)
        {
            Arm(heroes, ArcheryKit, "Ranged", horse: null);
            var rival = battle.Sides.Where(s => s.Key != heroes.PlayerSide).SelectMany(s => s.Characters).FirstOrDefault();
            request.TourneyPar = ArcheryPar(rival?.Prowess ?? 8);
            p.Objective = w.HitTargets(request.TourneyPar, ArcheryTargets);
        }
    }

    /// <summary>In a single combat or a tournament bout the duelist with more CK3 prowess hits 1 % harder and takes 1 % less per point
    /// of the gap, at most 50 % (2026-10-20: on top of the soft-capped prowess factors, so a champion outclasses a novice).</summary>
    public const double GapPerPoint = 0.01, GapMax = 0.5;

    public static void ApplyProwessGap(HeroAssignment heroes)
    {
        var a = heroes.Heroes.FirstOrDefault(h => h.Side == SideKey.Attacker);
        var d = heroes.Heroes.FirstOrDefault(h => h.Side == SideKey.Defender);
        if (a is null || d is null) return;
        int gap = a.Character.Prowess - d.Character.Prowess;
        if (gap == 0) return;
        var stronger = gap > 0 ? a : d;
        double k = Math.Min(GapMax, GapPerPoint * Math.Abs(gap));
        var b = stronger.Spec.Buffs ??= new HeroBuffs();
        b.DamageDealtMeleeMult = (float)(b.DamageDealtMeleeMult * (1 + k));
        b.DamageDealtRangedMult = (float)(b.DamageDealtRangedMult * (1 + k));
        b.DamageTakenMult = (float)(b.DamageTakenMult * (1 - k));
    }

    public const int WrestlingBout = 2, Joust = 4, Archery = 5;

    /// <summary>The pots of an arena's archery set (SandBox prefab tournament_archery_set, PROTOCOL §36).</summary>
    public const int ArcheryTargets = 13;

    /// <summary>The targets to hit in the archery contest: the best other archer's prowess sets the mark, 4 (prowess 0) to 12 (24 and more).</summary>
    public static int ArcheryPar(int rivalProwess) => Math.Clamp(4 + (int)Math.Round(Math.Max(0, rivalProwess) / 3.0), 4, 12);

    /// <summary>A joust (PROTOCOL §36): a blunt lance, the jousting shield and a tournament horse; nothing else in hand.</summary>
    public static readonly IReadOnlyDictionary<string, string> JoustKit = new Dictionary<string, string>
        { ["Item0"] = "empire_lance_1_t3_blunt", ["Item1"] = "jousting_shield", ["Item2"] = "", ["Item3"] = "" };

    /// <summary>The archery contest (PROTOCOL §36): a hunting bow and two quivers of tournament arrows, on foot.</summary>
    public static readonly IReadOnlyDictionary<string, string> ArcheryKit = new Dictionary<string, string>
        { ["Item0"] = "hunting_bow", ["Item1"] = "tournament_arrows", ["Item2"] = "tournament_arrows", ["Item3"] = "", ["Horse"] = "", ["HorseHarness"] = "" };

    /// <summary>The tournament horse of an arena culture (SandBoxCore horses_and_others.xml).</summary>
    public static string TournamentHorse(string? culture) =>
        (culture is "aserai" or "battania" or "empire" or "khuzait" or "sturgia" or "vlandia" ? culture : "vlandia") + "_horse_tournament";

    private static void Arm(HeroAssignment heroes, IReadOnlyDictionary<string, string> kit, string formation, string? horse)
    {
        foreach (var h in heroes.Heroes)
        {
            var eq = h.Spec.Equipment ??= new EquipmentSpec();
            foreach (var (slot, item) in kit) eq.Overrides[slot] = item;
            if (horse is not null) eq.Overrides["Horse"] = horse;
            h.Spec.Formation = formation;
        }
    }

    /// <summary>What a wrestler leaves in the tent (PROTOCOL §29): every weapon and every piece of armour, body and legs too (he
    /// wrestles in his undergarments, barefoot), and the horse. Armour made a bout of fists last minutes (live 2026-10-18).</summary>
    public static readonly IReadOnlyList<string> WrestlerEmptySlots =
        new[] { "Item0", "Item1", "Item2", "Item3", "Head", "Body", "Leg", "Gloves", "Cape", "Horse", "HorseHarness" };

    /// <summary>A wrestling bout goes quickly: each wrestler's health pool x0.35 and his blows x1.5, so the bout takes about a quarter
    /// of the time and the stronger (prowess) wrestler keeps his edge, as both factors apply to both alike.</summary>
    public const float WrestlerHealthFactor = 0.35f, WrestlerBlowFactor = 1.5f;

    private static void Unarm(HeroAssignment heroes)
    {
        foreach (var h in heroes.Heroes)
        {
            var eq = h.Spec.Equipment ??= new EquipmentSpec();
            foreach (string slot in WrestlerEmptySlots) eq.Overrides[slot] = "";
            h.Spec.Formation = "Infantry";
            h.Spec.Look = null;   // no costume either: nothing to dress
            h.Spec.HealthMultiplier = Math.Max(0.25f, h.Spec.HealthMultiplier * WrestlerHealthFactor);
            var buffs = h.Spec.Buffs ??= new HeroBuffs();
            buffs.DamageDealtMeleeMult = Math.Min(4f, buffs.DamageDealtMeleeMult * WrestlerBlowFactor);
        }
    }

    /// <summary>An assault on a besieged holding (PROTOCOL §39): kind siege on a castle or town of the defenders' culture, with the
    /// siege works the estimated progress of the CK3 siege gives; the field stays as the fallback scene.</summary>
    private static void ShapeSiege(BattleRequest request, BattleSnapshot battle, LocationFacts facts, Words w)
    {
        request.FallbackScene = request.Scene;
        request.Kind = "siege";
        request.Scene = SiegePlanner.Scene(request.Defender?.Culture, facts.Holding ?? battle.Location.Holding, battle.Bid);
        request.Siege = SiegePlanner.Build(battle, battle.Attacker);
        // PROTOCOL §46: the walls give the defenders damage, protection and morale by fort level.
        int fort = battle.SiegeFort > 0 ? battle.SiegeFort : battle.Location.FortLevel;
        var (dealt, taken, morale) = SiegePlanner.WallBonus(fort);
        if (request.Defender is { } d)
        {
            var b = d.Buffs ??= new SideBuffs();
            b.DamageDealtMult *= dealt;
            b.DamageTakenMult *= taken;
            b.InitialMoraleAdd += morale;
        }
        var p = request.Presentation ??= new Presentation();
        string place = PresentationWriter.PlaceName(battle, w);
        p.Title = w.Assault(place);
        p.Objective = w.TakeTheWalls;
    }

    /// <summary>A battle fought on ships (PROTOCOL §44): two hosts met at sea, or a landing, with War Sails loaded.</summary>
    public static bool AtSea(BattleSnapshot battle, MapperOptions options) =>
        options.Naval && !battle.IsDuel && !battle.IsSkirmish && !battle.IsSiege && (battle.IsSeaBattle || battle.Landing);

    /// <summary>
    /// PROTOCOL §44: kind "naval". At sea both sides sail fleets of their naval tradition (NavalPlanner) on a coastal sea; in a
    /// landing the attackers come ashore from shallow-water ships at a village of the defenders' culture. The field stays as
    /// fallbackScene for a game without War Sails.
    /// </summary>
    private static void ShapeNaval(BattleRequest request, BattleSnapshot battle, Words w)
    {
        bool raid = !battle.IsSeaBattle;
        // War Sails' raid lands the player's own ships: with the player ashore and the AI landing, its controller indexes its landing
        // frames by ship and crashes (bench and live b298, 2026-10-20). Defending against a landing is fought on the coast instead.
        if (raid && request.PlayerSide != "attacker")
        {
            request.Presentation ??= new Presentation();
            request.Presentation.Title = w.Landing(PresentationWriter.PlaceName(battle, w));
            return;
        }
        int Agents(SideRequest s) => (s?.Troops?.Sum(t => t.Count) ?? 0) + (s?.Heroes?.Count ?? 0);
        string TraditionOf(SideRequest s, SideSnapshot side) =>
            NavalPlanner.Tradition(s?.Culture, side.Armies.OrderByDescending(a => a.Size).FirstOrDefault()?.OwnerCulture.Heritage);
        string att = TraditionOf(request.Attacker, battle.Attacker), def = TraditionOf(request.Defender, battle.Defender);
        var attackerShips = NavalPlanner.Fleet(att, Agents(request.Attacker), shallowOnly: raid);
        var defenderShips = NavalPlanner.Fleet(def, Agents(request.Defender), shallowOnly: false);
        bool openSea = battle.IsSeaBattle && battle.Location.Coastal == false;
        request.FallbackScene = request.Scene;
        request.Kind = "naval";
        request.Scene = raid ? NavalPlanner.RaidScene(def, battle.Bid) : NavalPlanner.SeaScene(battle.Bid, openSea);
        request.Naval = NavalPlanner.Info(raid, attackerShips, defenderShips, battle.Bid, openSea);
        var p = request.Presentation ??= new Presentation();
        string place = PresentationWriter.PlaceName(battle, w);
        if (raid) p.Title = w.Landing(place);
        else p.Title = w.SeaBattle(place);
    }

    public const string ReliefDefend = "defend", ReliefCamp = "camp";

    /// <summary>A relief battle the player chose to fight at the walls (PROTOCOL §40).</summary>
    public static bool Relieving(BattleSnapshot battle, MapperOptions options) =>
        battle.Relief && !battle.IsSiege && !battle.IsDuel && !battle.IsSkirmish && options.ReliefMode is ReliefDefend or ReliefCamp;

    /// <summary>The garrison's troop group in a defence of the walls (no CK3 army: the outcome leaves it out).</summary>
    public static string GarrisonGroup(int bid) => $"garrison#{900002 + 2 * bid}";

    /// <summary>
    /// A relief (PROTOCOL §40) on a castle or town of the holder's culture. Defend: the player's host breaks into the holding,
    /// losing a share of its men on the way (SiegePlanner.BreakInLoss), and holds the walls with the garrison against the besiegers,
    /// who bring the works their siege has built. Camp: the host falls on the besiegers' camp outside the walls (Bannerlord's relief
    /// force attack). The player's side is put inside or outside the walls once the looks are settled (SiegeSides.Settle).
    /// </summary>
    private static void ShapeRelief(BattleRequest request, BattleSnapshot battle, LocationFacts facts, HeroAssignment heroes, bool defend, Words w)
    {
        var mineKey = heroes.PlayerSide;
        var foeKey = mineKey == SideKey.Attacker ? SideKey.Defender : SideKey.Attacker;
        var mine = (mineKey == SideKey.Attacker ? request.Attacker : request.Defender)!;
        var foe = (mineKey == SideKey.Attacker ? request.Defender : request.Attacker)!;
        var p = request.Presentation ??= new Presentation();
        string place = PresentationWriter.PlaceName(battle, w);
        if (!defend)
        {
            // The custom battle siege scenes carry no relief force spawn set (sp_relief_force_attack_set is an editor prefab only),
            // so the strike is fought in the field before the walls: the besiegers are caught in their camp (morale) and the
            // garrison sallies out to join the relief.
            var camp = foe.Buffs ??= new SideBuffs();
            camp.InitialMoraleAdd += CampSurpriseMorale;
            AddGarrison(request, mine, battle);
            p.Title = w.StrikeTheCamp(place);
            p.Objective = w.BreakTheSiege;
            return;
        }
        request.FallbackScene = request.Scene;
        request.Kind = "siege";
        request.Scene = SiegePlanner.Scene(mine.Culture, facts.Holding ?? battle.Location.Holding, battle.Bid);
        var siege = SiegePlanner.Build(battle, battle.Side(foeKey));
        siege.PlayerDefends = true;
        var me = battle.Side(mineKey);
        int martial = heroes.Ck3Commander(mineKey)?.Martial
                      ?? me.Characters.FirstOrDefault(c => c.Ref.Ck3Id == battle.PlayerCk3Id)?.Martial ?? 0;
        double loss = SiegePlanner.BreakInLoss(me.Size, battle.Side(foeKey).Size, martial);
        siege.BreakIn = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in mine.Troops)
        {
            int cut = Math.Min(t.Count - 1, JsCompat.RoundToInt(t.Count * loss));
            if (cut <= 0) continue;
            t.Count -= cut;
            siege.BreakIn[t.Group] = siege.BreakIn.GetValueOrDefault(t.Group) + cut;
        }
        siege.BreakInShare = (float)Math.Round(loss, 3);
        AddGarrison(request, mine, battle);
        p.Title = w.HoldTheWalls(place);
        p.Objective = w.DefendTheWalls;
        request.Siege = siege;
    }

    /// <summary>Initial morale of besiegers caught in their camp (PROTOCOL §40).</summary>
    public const float CampSurpriseMorale = -20f;

    /// <summary>The holding's garrison fights with the relief (PROTOCOL §40): men of the relief's levies, one group of its own.</summary>
    private static void AddGarrison(BattleRequest request, SideRequest mine, BattleSnapshot battle)
    {
        var like = mine.Troops.FirstOrDefault(t => t.Group?.StartsWith("levy", StringComparison.Ordinal) == true) ?? mine.Troops.FirstOrDefault();
        if (battle.SiegeGarrison <= 0 || like is null) return;
        int agents = Math.Max(1, JsCompat.RoundToInt(battle.SiegeGarrison / Math.Max(1.0, request.Scale.MenPerAgent)));
        mine.Troops.Add(new TroopRequest { Id = like.Id, Count = agents, Group = GarrisonGroup(battle.Bid), Look = like.Look });
    }

    /// <summary>A skirmish on the road (PROTOCOL §20): its own titles, deployed at once (a handful of men).</summary>
    private static void ShapeSkirmish(BattleRequest request, HeroAssignment heroes, BattleSnapshot battle, Words w)
    {
        var p = request.Presentation ??= new Presentation();
        string foe = heroes.Heroes.FirstOrDefault(h => h.Side != heroes.PlayerSide)?.Spec.Name ?? w.TheBand;
        (p.Title, p.Objective) = w.Skirmish(battle.SkirmishKind, foe);
        p.Deployment = "auto";
        if (HideoutScene(battle) is { } camp)
        {
            // PROTOCOL §37: bandits and deserters are sought out at their camp; the woods stay as the fallback scene.
            request.FallbackScene = request.Scene;
            request.Kind = "hideout";
            request.Scene = camp;
            (p.Title, p.Objective) = w.Hideout(battle.SkirmishKind, foe);
        }
    }

    /// <summary>SandBox's hideout scenes by CK3 terrain (PROTOCOL §37).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> HideoutScenes = new Dictionary<string, string[]>
    {
        ["forest"] = new[] { "forest_hideout_003", "forest_hideout_004", "bandit_forest" },
        ["mountain"] = new[] { "mountain_hideout_002", "mountain_hideout_004" },
        ["desert"] = new[] { "desert_hideout_002", "desert_hideout_004" },
        ["steppe"] = new[] { "hideout_steppe_001", "hideout_steppe_002" },
        ["coast"] = new[] { "sea_bandit_a", "sea_bandit_b", "sea_bandit_c" },
    };

    /// <summary>The camp of a band of bandits (kind 1) or deserters (kind 2): the hideout of the CK3 terrain, picked by bid; null for
    /// every other skirmish (an assassin, soldiers, zealots stay on the road).</summary>
    public static string? HideoutScene(BattleSnapshot battle)
    {
        if (!battle.IsSkirmish || battle.SkirmishKind is 3 or 4 or 5) return null;
        string terrain = (battle.Location.Terrain ?? "").ToLowerInvariant();
        string group = terrain switch
        {
            "forest" or "taiga" or "jungle" or "wetlands" => "forest",
            "mountains" or "hills" or "desert_mountains" => "mountain",
            "desert" or "drylands" or "oasis" => "desert",
            _ when battle.Location.Coastal => "coast",
            _ => "steppe",
        };
        var scenes = HideoutScenes[group];
        return scenes[Math.Abs(battle.Bid) % scenes.Length];
    }

    /// <summary>The logged facts with the terrain the battle is actually fought on (logged key, else the static index, else plains).</summary>
    internal static LocationFacts EffectiveFacts(BattleSnapshot battle, MappingTables tables)
    {
        var facts = LocationFacts.From(battle, tables);
        string? logged = facts.Terrain;
        string terrain = logged is not null && tables.Terrain.Terrain.ContainsKey(logged) ? logged
            : tables.Provinces.TryGetValue((int)battle.Location.ProvinceId, out var p) && tables.Terrain.Terrain.ContainsKey(p.Terrain) ? p.Terrain
            : "plains";
        return facts with { Terrain = terrain };
    }

    // Without a logged building_gfx the village's look comes from the relief army's culture: in a CK3 relief battle the army
    // that marches in (the combat attacker) comes to lift the siege of its own holding (INFERRED; the mod logs L|building_gfx).
    private static string? ReliefSideBuildingGfx(MappingTables tables, SidePlan attacker)
    {
        var culture = attacker.Snapshot.Armies.OrderByDescending(a => a.Size).Select(a => LocationFacts.Clean(a.OwnerCulture.Culture)).FirstOrDefault(c => c is not null);
        return culture is not null ? tables.Ck3.Cultures.GetValueOrDefault(culture)?.BuildingGfx.FirstOrDefault() : null;
    }

    private static SideRequest SideRequestFor(MappingTables tables, TroopResolver troops, SidePlan side, HeroAssignment heroes, KnightBuffPlanner? knights, Words words)
    {
        string culture = side.DominantCulture(tables.Mapping.DefaultCulture);
        var general = heroes.General(side.Key);
        var troopRequests = ForcePlanner.Troops(side);
        return new SideRequest
        {
            Name = PresentationWriter.SideName(side.Snapshot, words),
            Culture = culture,
            General = general?.Spec.Key ?? troops.PickCommander(culture, female: false, mounted: true, index: side.Key == SideKey.Attacker ? 0 : 1),
            Heroes = heroes.Heroes.Where(h => h.Side == side.Key).Select(h => h.Spec).ToList(),
            Troops = troopRequests,
            Buffs = knights?.SideBuffsFor(side.Key),
            GroupBuffs = WithMaaStats(side, knights?.GroupBuffsFor(side.Key, troopRequests.Select(t => t.Group!)) ?? new Dictionary<string, GroupBuffs>()),
        };
    }

    /// <summary>Multiplies each MaA group's CK3 damage and toughness factors (PlannedGroup.StatDamage/StatHealth) into its buffs,
    /// for every mod: they replace the extra agents the tier overflow used to give. The product keeps the 0.5..2 group clamp.</summary>
    private static Dictionary<string, GroupBuffs> WithMaaStats(SidePlan side, Dictionary<string, GroupBuffs> buffs)
    {
        foreach (var g in side.Groups.Where(g => g.Agents > 0 && (g.StatDamage != 1 || g.StatHealth != 1)))
        {
            var b = buffs.TryGetValue(g.Id, out var had) ? had : buffs[g.Id] = new GroupBuffs();
            b.DamageDealtMult = (float)KnightBuffPlanner.Safe(b.DamageDealtMult * g.StatDamage, 0.5, 2.0, 1);
            b.HealthMult = (float)KnightBuffPlanner.Safe(b.HealthMult * g.StatHealth, 0.5, 2.0, 1);
        }
        return buffs;
    }
}
