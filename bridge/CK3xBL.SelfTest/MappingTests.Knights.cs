// Self tests of the knights pass and the presentation flags of CK3xBL.Mapping (PROTOCOL §13): the
// worked example, the old-mod gate, knight effectiveness, accolades, captaincy, the eight worked battles of
// tools/gen/knights/test_combat_traits.mjs, hero buffs, caps and non-finite inputs, artifacts (reference cases computed with
// tools/gen/knights/resolve_artifact_overrides.mjs), fates, awards, casualty factors and the presentation flags.
// Run: dotnet run --project CK3xBL.SelfTest -- mapping.knights mapping.presentation
using System.Text.Json;
using CK3xBL.Appearance;
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Delivery;
using CK3xBL.Mapping;
using CK3xBL.Core.Json;
using CK3xBL.Core.Model;
using CK3xBL.Mapping.Buffs;
using CK3xBL.Mapping.Data;
using CK3xBL.Mapping.Outcome;
using CK3xBL.Mapping.Planning;
using CK3xBL.Mapping.Resolution;
using CK3xBL.Protocol;

namespace CK3xBL.SelfTest;

internal static partial class MappingTests
{
    private static void RegisterKnights(TestRegistry r)
    {
        r.Add("mapping.knights.worked_example", KnightsWorkedExample);
        r.Add("mapping.knights.gate_old_mod_is_todays_request", KnightsGate);
        r.Add("mapping.knights.knight_effectiveness", KnightsEffectiveness);
        r.Add("mapping.knights.stat_balance", KnightsStatBalance);
        r.Add("mapping.maa_stats_scale_damage_and_health", MaaStatScaling);
        r.Add("mapping.gear.table_is_consistent", GearTable);
        r.Add("mapping.gear.outfitter_rules", GearOutfitterRules);
        r.Add("mapping.gear.hero_carries_exactly_his_gear", GearOnHero);
        r.Add("mapping.balance.strength_matches_ck3", BalanceMatchesCk3);
        r.Add("mapping.levy.development_moves_the_era", LevyByDevelopment);
        r.Add("mapping.skirmish.fifty_at_most", SkirmishPlan);
        r.Add("mapping.costumes.every_group_dressed_by_culture_era_role", CostumesInRequest);
        r.Add("mapping.costumes.era_and_role_rules", CostumesRules);
        r.Add("core.bannerlord.armory_modules_in_the_launch_list", ArmoryLaunchList);
        r.Add("core.balance.bannerlord_winner_checked_against_ck3", Ck3VerdictCheck);
        r.Add("core.memory.trim_working_set", TrimOwnWorkingSet);
        r.Add("mapping.feats.player_wounds_from_his_own_fight", PlayerWounds);
        r.Add("mapping.feats.deeds_in_the_outcome", FeatsInOutcome);
        r.Add("core.feats.ledger_counts_and_r7", FeatLedgerCounts);
        r.Add("mapping.knights.cleave_past_prowess_30_and_ai_level", CleaveAndAiLevel);
        r.Add("mapping.outcome.fugitive_heroes_are_pursued", FugitivesArePursued);
        r.Add("mapping.costumes.unique_regiments_wear_their_own_look", UniqueRegimentsLook);
        r.Add("mapping.skirmish.bandits_at_their_hideout", BanditHideouts);
        r.Add("mapping.siege.progress_decides_the_siege_works", SiegeProgressAndWorks);
        r.Add("mapping.siege.relief_at_the_walls_or_the_camp", ReliefAtTheWalls);
        r.Add("mapping.knights.accolade_knight_leads_his_formation", KnightAuras);
        r.Add("mapping.elephants.elephant_regiments_ride_elephants", ElephantsRide);
        r.Add("core.bannerlord.elephant_module_and_armory_prep", ElephantModuleAndArmoryPrep);
        r.Add("mapping.naval.fleets_of_their_tradition", NavalFleets);
        r.Add("mapping.naval.sea_battle_landing_and_drowning", NavalBattles);
        r.Add("core.bannerlord.naval_module_and_launch_list", NavalModuleAndLaunchList);
        r.Add("mapping.presentation.russian_when_ck3_is_russian", RussianTexts);
        r.Add("mapping.presentation.article_only_for_english_battle_names", TitleArticle);
        r.Add("mapping.duel.two_duelists_alone", DuelRequest);
        r.Add("mapping.duel.outcome_fixes_the_winner", DuelOutcome);
        r.Add("mapping.knights.accolades_stack_trim_and_inactive", KnightsAccolades);
        r.Add("mapping.knights.captain_only_on_the_ai_side", KnightsCaptaincy);
        r.Add("mapping.knights.hostile_territory_only_for_the_attacker", KnightsHostileTerritory);
        r.Add("mapping.knights.hostility_direction", KnightsHostilityDirection);
        r.Add("mapping.knights.golden.combat_traits_worked_battles", KnightsGoldenTraitBattles);
        r.Add("mapping.knights.hero_buffs_skills_and_tactics", KnightsHeroBuffs);
        r.Add("mapping.knights.combined_hero_caps", KnightsCombinedCaps);
        r.Add("mapping.knights.non_finite_inputs_stay_neutral", KnightsNonFinite);
        r.Add("mapping.knights.artifacts.reference_cases_survive_enrich", KnightsArtifactCases);
        r.Add("mapping.knights.artifacts.every_kit_category_rarity", KnightsArtifactSweep);
        r.Add("mapping.knights.artifacts.landless_knight_wears_his_mail", KnightsArtifactLandless);
        r.Add("mapping.knights.fate_factors", KnightsFates);
        r.Add("mapping.knights.awards", KnightsAwards);
        r.Add("mapping.knights.awards_in_the_outcome", KnightsAwardsInOutcome);
        r.Add("mapping.knights.casualty_factors", KnightsCasualties);
        r.Add("mapping.presentation.in_engine_title_off", PresentationInEngineTitle);
        r.Add("mapping.presentation.objective_by_siege_role", PresentationSiegeObjective);
    }

    // ---- the knights battle ------------------------------------------------------------------------------------------

    /// <summary>
    /// Worked example: the AI attacks; its knight Odo (prowess 15, brave) is acclaimed (active, rank 3, Archer level 2,
    /// primary) and rides in army 12, whose owner has 150 % knight effectiveness and which fields bowmen and levies. The player
    /// defends with his own host and a knight. A mod with the knights pass logged it (KnightFactsLogged).
    /// </summary>
    private static BattleSnapshot KnightsBattle()
    {
        var guy = Scenarios.Ref(301, "Guy de Lusignan", "c_lusignan");
        var odo = Scenarios.Ref(302, "Odo");
        var me = Scenarios.Ref(401, "Henry", "d_normandy");
        var hugh = Scenarios.Ref(402, "Hugh");
        var french = Scenarios.Cul("french", "heritage_frankish", "western_unit_gfx", Scenarios.High);
        var norman = Scenarios.Cul("norman", "heritage_frankish", "western_unit_gfx", Scenarios.High);

        var host12 = Scenarios.Army(12, "Host of Lusignan", guy, guy, french, 800, ("bowmen", 200, 200), ("armored_footmen", 200, 200));
        host12.KnightEffectivenessPermille = 1500;
        var guyC = Scenarios.Person(guy, 1, "CMD+PRIMARY", french, 10, 10, 40, 2, 12);
        guyC.HouseName = "Lusignan";
        var odoC = Scenarios.Person(odo, 2, "KNIGHT", french, 15, 6, 30, 0, 12);
        odoC.CombatTraits.Add("brave");
        odoC.HouseName = "Mauleon";
        odoC.Accolade = new AccoladeSnapshot { Active = true, Rank = 3, Primary = "archer_attribute", Levels = { ["archer_attribute"] = 2 } };
        var att = Scenarios.Side(SideKey.Attacker, false, guy, new[] { host12 }, new[] { guyC, odoC });
        att.Commander = guy;
        att.FaithHostilityOut = 0;

        var host20 = Scenarios.Army(20, "Host of Normandy", me, me, norman, 900, ("bowmen", 100, 100), ("armored_footmen", 200, 200), ("armored_horsemen", 100, 100));
        var meC = Scenarios.Person(me, 10, "CMD+PRIMARY", norman, 12, 8, 35, 3, 20, player: true);
        meC.HouseName = "Normandy";
        var hughC = Scenarios.Person(hugh, 11, "KNIGHT", norman, 12, 5, 28, 0, 20);
        hughC.HouseName = "Montfort";
        var def = Scenarios.Side(SideKey.Defender, true, me, new[] { host20 }, new[] { meC, hughC });
        def.Commander = me;
        def.FaithHostilityOut = 0;

        var battle = Scenarios.Battle(20, "Battle of Saumur", (1150, 6, 1), 2262, "Saumur", "c_anjou", "plains", "none", "castle_holding", 2, false, false,
            "Lusignan Revolt", 401, att, def, ("region", "world_europe_west"));
        battle.KnightFactsLogged = true;
        return battle;
    }

    private static CharacterSnapshot Character(BattleSnapshot battle, int tag) => battle.Sides.SelectMany(s => s.Characters).Single(c => c.Tag == tag);

    private static string MaaGroup(ArmySnapshot army, string maa) => GroupKey.Maa(maa, army.Tag, army.Regiments.First(x => x.MaaKey == maa).RegimentId).ToString();

    private static KnightBuffPlanner PlannerFor(BattleSnapshot battle)
    {
        var plan = Mapper.Plan(battle, new MapperOptions());
        return new KnightBuffPlanner(Tables, battle, plan.Location, plan.Heroes.AttackerGeneral?.Character, plan.Heroes.DefenderGeneral?.Character);
    }

    private static HeroSpec HeroOf(BattleRequest req, int tag) => req.Attacker.Heroes.Concat(req.Defender.Heroes).Single(h => h.Tag == tag);

    // ---- worked example, old-mod gate, knight effectiveness, accolades ------------------------------------------------

    private static void KnightsWorkedExample()
    {
        var battle = KnightsBattle();
        var req = Mapper.BuildRequest(battle, new MapperOptions { StrengthBalance = false });   // the knights layer alone
        Expect.Equal("defender", req.PlayerSide);
        var odo = HeroOf(req, 2);
        Expect.Near(2.0, odo.HealthMultiplier, 1e-6, "1.6 (prowess 15) x 1.25 (150 % knight effectiveness)");
        Expect.Near(1.25, odo.DamageMultiplier, 1e-6);
        Expect.Equal("Ranged", odo.CaptainOf);
        Expect.Equal("Ranged", odo.Formation);
        Expect.Equal("", odo.Equipment.Overrides["Horse"]);
        Expect.Equal("", odo.Equipment.Overrides["HorseHarness"]);
        var buffs = Expect.NotNull(odo.Buffs);
        Expect.Near(25, buffs.InitialMoraleAdd, 1e-6);
        Expect.True(buffs.NoPanic);
        Expect.Near(0.6, odo.DeathChance!.Value, 1e-6);

        var retinue = req.Attacker.GroupBuffs["knight-retinue#2"];
        Expect.Near(1.25, retinue.DamageDealtMult, 1e-6);
        Expect.Near(1.25, retinue.HealthMult, 1e-6);
        Expect.Near(10, retinue.InitialMoraleAdd, 1e-6, "brave retinue");
        var bowmen = req.Attacker.GroupBuffs[MaaGroup(battle.Attacker.Armies[0], "bowmen")];
        Expect.Near(1.2, bowmen.DamageDealtMult, 1e-6, "1 + 0.5 x 0.4");
        Expect.Near(1.1, bowmen.HealthMult, 1e-6, "1 + 0.5 x 0.2");
        Expect.Near(6, bowmen.InitialMoraleAdd, 1e-6, "2 x rank 3");
        Expect.False(req.Attacker.GroupBuffs.ContainsKey("levy#12"), "Archer buffs only archers");
        Expect.False(req.Attacker.GroupBuffs.ContainsKey(MaaGroup(battle.Attacker.Armies[0], "armored_footmen")));
        foreach (var g in req.Attacker.GroupBuffs.Values.Concat(req.Defender.GroupBuffs.Values))
            Expect.Equal(1f, g.DamageTakenMult, "GroupBuffs.DamageTakenMult is reserved");
        // The commanders give the side buffs: martial 10 against 8 puts the attackers 2 points ahead.
        var side = Expect.NotNull(req.Attacker.Buffs);
        Expect.Near(1.05, side.DamageDealtMult, 1e-6);
        Expect.Near(2, side.InitialMoraleAdd, 1e-6);
        Expect.Null(req.Defender.Buffs);
        // Commanders get no knight effectiveness.
        Expect.Near(1 + 10 / 25.0, HeroOf(req, 1).HealthMultiplier, 1e-6);
        Expect.Near(1, HeroOf(req, 1).DamageMultiplier, 1e-6);
        Expect.True(ProtocolJson.Serialize(req).Contains("\"captainOf\":\"Ranged\"", StringComparison.Ordinal));
        Expect.Equal(JsonSerializer.Serialize(req, Json), JsonSerializer.Serialize(Mapper.BuildRequest(KnightsBattle(), new MapperOptions { StrengthBalance = false }), Json), "deterministic");
    }

    /// <summary>Strips every fact only a mod with the knights pass logs.</summary>
    private static BattleSnapshot WithoutKnightFacts(BattleSnapshot battle)
    {
        battle.KnightFactsLogged = false;
        foreach (var side in battle.Sides)
        {
            side.Commander = null;
            side.FaithHostilityOut = null;
            foreach (var a in side.Armies)
            {
                a.KnightEffectivenessPermille = null;
                a.Besieging = null;
                a.Territory = null;
                a.OwnerTraits.Clear();
                a.OwnerCultureParams.Clear();
                a.OwnerFaithParams.Clear();
            }
            foreach (var c in side.Characters)
            {
                c.CombatTraits.Clear();
                c.TraitLevels.Clear();
                c.CultureParams.Clear();
                c.FaithParams.Clear();
                c.Accolade = null;
                c.Artifacts.Clear();
            }
        }
        return battle;
    }

    private static BattleSnapshot GateBattle()
    {
        // Facts today's mod logs as well (martial, CX|ctrait, CX|trait wounded_3, terrain, winter) and the new ones.
        var battle = KnightsBattle();
        battle.Location.Terrain = "forest";
        battle.Location.Facts["terrain"] = "forest";
        battle.Location.Winter = "normal";
        battle.Location.Facts["winter"] = "normal";
        Character(battle, 1).CommanderTraits.Add("forest_fighter");
        Character(battle, 1).TraitLevels["forest_fighter"] = 3;
        Character(battle, 11).Traits.Add("wounded_3");
        Character(battle, 11).Artifacts.Add(new ArtifactSnapshot { SlotType = "primary_armament", Type = "sword", Rarity = "illustrious" });
        battle.Attacker.Armies[0].Territory = "hostile";
        return battle;
    }

    private static void KnightsGate()
    {
        var options = new MapperOptions { StrengthBalance = false };   // the gate is about the knights layer; the balance is §17
        var old = GateBattle();
        old.KnightFactsLogged = false;
        var req = Mapper.BuildRequest(old, options);
        foreach (var side in new[] { req.Attacker, req.Defender })
        {
            Expect.Null(side.Buffs);
            Expect.Equal(0, side.GroupBuffs.Count);
            foreach (var h in side.Heroes)
            {
                var c = Character(old, h.Tag);
                Expect.Equal(1f, h.DamageMultiplier);
                Expect.Null(h.CaptainOf);
                Expect.Null(h.DeathChance);
                Expect.Null(h.Buffs);
                Expect.Near(Math.Clamp(1 + c.Prowess / 25.0, 1, 3), h.HealthMultiplier, 1e-6, "healthMultiplier = P");
                Expect.Equal(Math.Clamp(10 * c.Martial, 0, 300), h.Skills["Tactics"]);
                Expect.True(h.Equipment.Overrides.Keys.All(k => k is "Horse" or "HorseHarness"), $"{h.Name}: no artifact overrides");
            }
        }
        Expect.False(req.Presentation.InEngineTitle, "the polish applies to every mod");
        // Member for member the request of the same battle without any knights fact.
        var stripped = Mapper.BuildRequest(WithoutKnightFacts(GateBattle()), options);
        Expect.Equal(JsonSerializer.Serialize(stripped, Json), JsonSerializer.Serialize(req, Json));
        var result = Synthetic(req, "attacker");
        result.Heroes.Single(h => h.Tag == 11).State = "killed";
        var o = Mapper.BuildOutcome(old, req, result, options);
        Expect.Equal(0, o.Awards.Count);
        Expect.Equal(JsonSerializer.Serialize(Mapper.BuildOutcome(WithoutKnightFacts(GateBattle()), stripped, result, options), Json), JsonSerializer.Serialize(o, Json));

        // The flag is the switch: the same battle from a mod with the pass gets buffs.
        var knights = Mapper.BuildRequest(GateBattle(), options);
        Expect.NotNull(knights.Attacker.Buffs);
        Expect.True(knights.Attacker.GroupBuffs.Count > 0);
        Expect.True(HeroOf(knights, 1).Skills["Tactics"] > 100, "forest_fighter L3 adds Tactics to the general");
    }

    /// <summary>Prowess damage, character health, commander martial and age (the balance that runs once the CK3 mod logs health).</summary>
    private static void KnightsStatBalance()
    {
        Expect.Near(1, HeroPlanner.ProwessDamage(0), 1e-9);
        Expect.Near(1.25, HeroPlanner.ProwessDamage(10), 1e-9);
        Expect.Near(2, HeroPlanner.ProwessDamage(40), 1e-9, "x2 at 40");
        Expect.Near(2.5, HeroPlanner.ProwessDamage(80), 1e-9, "then half as fast");
        Expect.Near(2.75, HeroPlanner.ProwessDamage(150), 1e-9, "capped at x2.75 (prowess 100)");
        Expect.Near(3, HeroPlanner.ProwessHealth(50), 1e-9);
        Expect.Near(5, HeroPlanner.ProwessHealth(120), 1e-9, "health capped at x5 (prowess 100)");
        Expect.Equal(300, HeroPlanner.WeaponSkill(30));
        Expect.Equal(330, HeroPlanner.WeaponSkill(34));
        Expect.Equal(384, HeroPlanner.WeaponSkill(88), "a champion keeps rising past the old 330");
        Expect.Equal(400, HeroPlanner.WeaponSkill(200));
        Expect.Near(1, HeroPlanner.ProwessDamage(-5), 1e-9, "negative prowess does not lower damage");
        Expect.Near(1, HeroPlanner.HealthFactor(null), 1e-9, "health not logged = neutral");
        Expect.Near(1, HeroPlanner.HealthFactor(5), 1e-9);
        Expect.Near(1.18, HeroPlanner.HealthFactor(8), 1e-9);
        Expect.Near(0.7, HeroPlanner.HealthFactor(-10), 1e-9, "floor");
        Expect.Near(1.5, HeroPlanner.HealthFactor(50), 1e-9, "ceiling");

        // An older mod (no health logged) keeps the earlier numbers; a newer one gets the balance.
        var older = Mapper.BuildRequest(KnightsBattle(), new MapperOptions());
        var battle = KnightsBattle();
        foreach (var c in battle.Sides.SelectMany(s => s.Characters)) c.Health = 8;
        var req = Mapper.BuildRequest(battle, new MapperOptions());
        var before = HeroOf(older, 1);
        var cmd = HeroOf(req, 1);
        Expect.Near(before.HealthMultiplier * 1.18, cmd.HealthMultiplier, 1e-5, "commander health pool x health factor");
        Expect.Near(before.DamageMultiplier * 1.25, cmd.DamageMultiplier, 1e-5, "commander damage x prowess 10 factor");
    }

    /// <summary>
    /// A MaA regiment's CK3 damage and toughness: the tier still picks the troop, the overflow no longer adds agents, and the
    /// overflow and the damage/toughness shape become the group's damage and health (ForcePlanner.MaaStatFactors).
    /// </summary>
    private static void MaaStatScaling()
    {
        static void Pair((double D, double H) want, (double Damage, double Health) got, string what)
        {
            Expect.Near(want.D, got.Damage, 1e-4, what + " damage");
            Expect.Near(want.H, got.Health, 1e-4, what + " health");
        }
        Pair((1, 1), ForcePlanner.MaaStatFactors(1, null), "unknown stats");
        Pair((1, 1), ForcePlanner.MaaStatFactors(1, (1, 1)), "the reference MaA");
        Pair((1.3099, 1.5338), ForcePlanner.MaaStatFactors(1.83, (94 / 36.0, 60 / 18.0)), "kheshig: overflow 1.83, more toughness than damage");
        Pair((0.75, 0.75), ForcePlanner.MaaStatFactors(0.5, null), "below the bottom rung");
        Pair((1.6, 1), ForcePlanner.MaaStatFactors(2, (4, 1)), "clamped at 1.6");
        Pair((1, 1), ForcePlanner.MaaStatFactors(double.NaN, null), "non-finite overflow is neutral");

        var battle = KnightsBattle();
        battle.KnightFactsLogged = false;                    // no knights pass: the group buffs are the stat factors alone
        var host = battle.Attacker.Armies[0];
        host.Regiments.Add(new RegimentSnapshot { RegimentId = 16997, MaaKey = "kheshig", Current = 200, Max = 200 });
        var plan = Mapper.Plan(battle, new MapperOptions());
        var groups = plan.Attacker.Groups;
        var kheshig = groups.Single(g => g.Key.MaaKey == "kheshig");
        var footmen = groups.Single(g => g.Key.MaaKey == "armored_footmen");
        Expect.True(kheshig.PowerOverflow > 1, $"kheshig overflow {kheshig.PowerOverflow}");
        Expect.True(Math.Abs(kheshig.Agents - footmen.Agents) <= 1, $"same men, same agents: {kheshig.Agents} vs {footmen.Agents}");
        Pair((kheshig.StatDamage, kheshig.StatHealth), ForcePlanner.MaaStatFactors(kheshig.PowerOverflow, (94 / 36.0, 60 / 18.0)), "planned kheshig");
        var buff = plan.Request.Attacker.GroupBuffs[kheshig.Id];
        Expect.Near(kheshig.StatDamage, buff.DamageDealtMult, 1e-5, "kheshig damageDealtMult");
        Expect.Near(kheshig.StatHealth, buff.HealthMult, 1e-5, "kheshig healthMult");
        Expect.False(plan.Request.Attacker.GroupBuffs.ContainsKey(footmen.Id), "the reference MaA stays neutral");

        // With the knights pass the stat factors multiply into the group's other buffs.
        battle.KnightFactsLogged = true;
        var withKnights = Mapper.BuildRequest(battle, new MapperOptions()).Attacker.GroupBuffs[kheshig.Id];
        Expect.True(withKnights.HealthMult >= buff.HealthMult - 1e-5, "knights buffs come on top");
    }

    // ---- battle gear (PROTOCOL §16) -----------------------------------------------------------------------------------

    /// <summary>ck3xbl_equipment.json: every item is a catalog item of the right type, horses bring a harness, the prowess caps hold.</summary>
    private static void GearTable()
    {
        var gear = Tables.Gear;
        Expect.True(gear.Count > 200, $"{gear.Count} gear items");
        var caps = new Dictionary<string, int>
        {
            ["ck3xbl_main"] = 20, ["ck3xbl_gear"] = 6, ["ck3xbl_head"] = 7, ["ck3xbl_body"] = 8, ["ck3xbl_hands"] = 3, ["ck3xbl_legs"] = 3,
            ["ck3xbl_cape"] = 2, ["ck3xbl_horse"] = 2,
        };
        foreach (var g in gear.Values)
        {
            Expect.True(Tables.Items.TryGetValue(g.Id, out var item), $"{g.Id} is in bl_catalog.json");
            Expect.True(caps.TryGetValue(g.Slot, out int cap) && g.Prowess >= 0 && g.Prowess <= cap, $"{g.Id}: prowess {g.Prowess} in {g.Slot}");
            if (g.Slot == "ck3xbl_horse")
            {
                Expect.Equal("Horse", item!.Type, g.Id);
                Expect.True(g.Harness is not null && Tables.Items.GetValueOrDefault(g.Harness)?.Type == "HorseHarness", $"{g.Id}: harness {g.Harness}");
            }
            if (g.Slot == "ck3xbl_body") Expect.Equal("BodyArmor", item!.Type, g.Id);
        }
        Expect.Equal(20, gear.Values.Where(g => g.Slot == "ck3xbl_main").Max(g => g.Prowess), "the best main weapon gives +20");
        Expect.True(gear.Values.Select(g => g.Name).Distinct().Count() == gear.Count, "every item has its own name");
    }

    private static void GearOutfitterRules()
    {
        var o = new GearOutfitter(Tables);
        var kit = new Dictionary<string, string>
        {
            ["Item0"] = "vlandia_sword_3_t4", ["Item1"] = "tall_heater_shield", ["Item2"] = "vlandia_lance_2_t4", ["Item3"] = "",
            ["Head"] = "padded_cap", ["Horse"] = "", ["HorseHarness"] = "",
        };
        // A bow as the main weapon: the kit's sword, shield and lance follow; the bow needs arrows, so the lance makes room.
        var bow = o.Resolve(kit, new[] { new GearSnapshot { Slot = "ck3xbl_main", ItemId = "lowland_yew_bow" } });
        Expect.Equal("lowland_yew_bow", bow.Overrides["Item0"]);
        Expect.Equal("vlandia_sword_3_t4", bow.Overrides["Item1"]);
        Expect.Equal("tall_heater_shield", bow.Overrides["Item2"]);
        Expect.Equal("Arrows", Tables.Items[bow.Overrides["Item3"]].Type, "arrows for the bow");
        Expect.True((Tables.Items[bow.Overrides["Item3"]].Tier ?? 0) <= 4, "arrows not above the bow's tier");
        Expect.False(bow.Mounted);
        Expect.False(bow.Overrides.ContainsKey("Head"), "armour slots without gear keep the kit");

        // A sword in the main slot replaces the kit's sword (same class group); armour and a horse with its harness.
        var full = o.Resolve(kit, new[]
        {
            new GearSnapshot { Slot = "ck3xbl_gear", ItemId = "pavise_shield" },
            new GearSnapshot { Slot = "ck3xbl_main", ItemId = "vlandia_noble_sword_1_t5" },
            new GearSnapshot { Slot = "ck3xbl_head", ItemId = "visored_helmet_over_mail_coif" },
            new GearSnapshot { Slot = "ck3xbl_horse", ItemId = "t3_vlandia_horse" },
        });
        Expect.Equal("vlandia_noble_sword_1_t5", full.Overrides["Item0"], "the main weapon first");
        Expect.Equal("pavise_shield", full.Overrides["Item1"]);
        Expect.Equal("vlandia_lance_2_t4", full.Overrides["Item2"], "the kit's lance: a class group the gear lacks");
        Expect.Equal("", full.Overrides["Item3"], "the kit's shield and sword are replaced, the slot stays empty");
        Expect.Equal("visored_helmet_over_mail_coif", full.Overrides["Head"]);
        Expect.Equal("t3_vlandia_horse", full.Overrides["Horse"]);
        Expect.Equal(Tables.Gear["t3_vlandia_horse"].Harness, full.Overrides["HorseHarness"]);
        Expect.True(full.Mounted, "a horse in the gear: the hero rides");

        var unknown = o.Resolve(kit, new[] { new GearSnapshot { Slot = "ck3xbl_main", ItemId = "no_such_item" } });
        Expect.Equal(0, unknown.Overrides.Count, "an item the catalog does not know changes nothing");
    }

    private static void GearOnHero()
    {
        var battle = KnightsBattle();
        var odo = Character(battle, 2);
        odo.Gear.Add(new GearSnapshot { Slot = "ck3xbl_main", ItemId = "lowland_yew_bow" });
        odo.Gear.Add(new GearSnapshot { Slot = "ck3xbl_gear", ItemId = "vlandia_lance_3_t5" });
        odo.Gear.Add(new GearSnapshot { Slot = "ck3xbl_body", ItemId = "coat_of_plates_over_mail" });
        odo.Gear.Add(new GearSnapshot { Slot = "ck3xbl_horse", ItemId = "noble_horse_western" });
        var h = HeroOf(Mapper.BuildRequest(battle, new MapperOptions()), 2);
        var ov = h.Equipment.Overrides;
        Expect.Equal("lowland_yew_bow", ov["Item0"]);
        Expect.Equal("vlandia_lance_3_t5", ov["Item1"]);
        Expect.True(Enumerable.Range(0, 4).Any(i => ov.TryGetValue($"Item{i}", out var id) && Tables.Items.GetValueOrDefault(id)?.Type == "Arrows"), "arrows");
        Expect.Equal("coat_of_plates_over_mail", ov["Body"]);
        Expect.Equal("noble_horse_western", ov["Horse"]);
        Expect.True(h.Formation is "Cavalry" or "HorseArcher", $"formation {h.Formation}");

        // The same knight without gear keeps his kit.
        var plain = HeroOf(Mapper.BuildRequest(KnightsBattle(), new MapperOptions()), 2);
        Expect.False(plain.Equipment.Overrides.GetValueOrDefault("Item0") == "lowland_yew_bow");
    }

    /// <summary>
    /// The strength balance (PROTOCOL §17): elite men-at-arms against five times their number of levies. CK3 calls it close;
    /// Bannerlord's square law, with elite troops worth less than in CK3, would crush the elite side, so it gets the difference.
    /// </summary>
    private static void BalanceMatchesCk3()
    {
        Expect.Near(2, StrengthBalance.Factor(4, 1, 2, 1), 1e-12);
        Expect.Near(1, StrengthBalance.Factor(0, 1, 2, 1), 1e-12, "a side with nothing: no factor");

        var battle = KnightsBattle();
        var att = battle.Attacker.Armies[0];
        att.Levies = 0;
        att.Regiments.Clear();
        att.Regiments.Add(new RegimentSnapshot { RegimentId = 17001, MaaKey = "armored_horsemen", Current = 200, Max = 200 });
        var def = battle.Defender.Armies[0];
        def.Levies = 1000;
        def.Regiments.Clear();

        var plan = Mapper.Plan(battle, new MapperOptions());
        var bal = Expect.NotNull(plan.Request.Balance);
        double ck3 = bal.Ck3Attacker / bal.Ck3Defender, bl = bal.BannerlordAttacker / bal.BannerlordDefender;
        Expect.True(ck3 > 1, $"CK3: 200 heavy horse beat 1000 levies ({ck3:0.##})");
        Expect.True(bl < ck3, $"Bannerlord's estimate is lower ({bl:0.##})");
        Expect.Equal("attacker", bal.Boosted);
        double f = Math.Min(4, ck3 / bl);
        Expect.Near(f, bal.Factor, 1e-3);
        var buffs = Expect.NotNull(plan.Request.Attacker.Buffs);
        Expect.True(buffs.DamageDealtMult > 1 && buffs.DamageTakenMult < 1, "damage up, damage taken down");

        // Switched off, or with k = 2 (the tier rule's own scale), nothing changes for a side whose troops match CK3.
        Expect.Null(Mapper.Plan(battle, new MapperOptions { StrengthBalance = false }).Request.Balance!.Boosted);
    }

    /// <summary>Levies by development (PROTOCOL §19): the owner's capital development moves the levy mix along the eras.</summary>
    private static void LevyByDevelopment()
    {
        Expect.Near(0, TroopResolver.LevyEraShift(null), 1e-12, "not logged: the era's own mix");
        Expect.Near(0, TroopResolver.LevyEraShift(20), 1e-12);
        Expect.Near(-1, TroopResolver.LevyEraShift(0), 1e-12, "a bare county: one era back");
        Expect.Near(1.5, TroopResolver.LevyEraShift(90), 1e-12, "capped");

        var res = new TroopResolver(Tables.Mapping);
        const string high = "culture_era_high_medieval", late = "culture_era_late_medieval", early = "culture_era_early_medieval";
        string Mix(LevyResolution l) => string.Join(",", l.Troops.Select(t => $"{t.Troop}:{t.Weight}"));
        Expect.Equal(Mix(res.ResolveLevy("western_feudal", high, false)), Mix(res.ResolveLevy("western_feudal", high, false, 20)), "average land: unchanged");
        Expect.Equal(Mix(res.ResolveLevy("western_feudal", late, false)), Mix(res.ResolveLevy("western_feudal", high, false, 40)), "rich: a whole era on");
        Expect.Equal(Mix(res.ResolveLevy("western_feudal", early, false)), Mix(res.ResolveLevy("western_feudal", high, false, 0)), "poor: a whole era back");
        Expect.Equal(Mix(res.ResolveLevy("western_feudal", late, false)), Mix(res.ResolveLevy("western_feudal", late, false, 90)), "past the last era: the last era");
        var half = res.ResolveLevy("western_feudal", high, false, 30);
        Expect.Near(1, half.Troops.Sum(t => t.Weight), 0.05, "a blend still sums to 1 (weights rounded to 0.01)");
        Expect.True(half.Troops.Any(t => t.Troop == "vlandian_militia_veteran_spearman"), "half way to the late mix: some veterans");

        // Through the planner: the same army with a rich capital fields better levies.
        var battle = KnightsBattle();
        battle.Attacker.Armies[0].Development = 45;
        var plan = Mapper.Plan(battle, new MapperOptions());
        var levyGroup = plan.Attacker.Groups.First(g => g.Key.Kind == GroupKind.Levy);
        Expect.Equal(Mix(res.ResolveLevy("western_feudal", high, false, 45)), string.Join(",", levyGroup.Troops.Select(t => $"{t.Troop}:{t.Weight}")));
    }

    /// <summary>
    /// A skirmish on the road (PROTOCOL §20): a band of 28 under its leader falls on the player, two knights and 10 guards. All of it
    /// fits in 50 fighters, no side is raised to the battle floor, and the outcome is payload 3 without army slots.
    /// </summary>
    private static void SkirmishPlan()
    {
        var me = Scenarios.Ref(601, "Henry", "d_normandy");
        var foe = Scenarios.Ref(602, "Black Wulf");
        var k1 = Scenarios.Ref(603, "Hugh");
        var k2 = Scenarios.Ref(604, "Odo");
        var norman = Scenarios.Cul("norman", "heritage_frankish", "western_unit_gfx", Scenarios.High);
        var band = Scenarios.Army(900161, "Band", foe, null, norman, 28);
        var guard = Scenarios.Army(900162, "Guards", me, null, norman, 10);
        var att = Scenarios.Side(SideKey.Attacker, false, foe, new[] { band }, new[] { Scenarios.Person(foe, 40, "KNIGHT", norman, 8, 3, 35, 0, 0) });
        var def = Scenarios.Side(SideKey.Defender, true, me, new[] { guard }, new[]
        {
            Scenarios.Person(me, 41, "KNIGHT", norman, 12, 8, 30, 3, 0, player: true),
            Scenarios.Person(k1, 42, "KNIGHT", norman, 14, 5, 28, 0, 0), Scenarios.Person(k2, 43, "KNIGHT", norman, 10, 5, 25, 0, 0),
        });
        var battle = Scenarios.Battle(80, "Bandits", (1150, 6, 1), 2262, "Saumur", "c_anjou", "forest", "none", "castle_holding", 0, false, false,
            null, 601, att, def, ("region", "world_europe_west"));
        battle.KnightFactsLogged = true;
        battle.IsSkirmish = true;
        battle.SkirmishKind = 1;

        var plan = Mapper.Plan(battle, new MapperOptions());
        var req = plan.Request;
        int troops = req.Attacker.Troops.Sum(t => t.Count) + req.Defender.Troops.Sum(t => t.Count);
        int heroes = req.Attacker.Heroes.Count + req.Defender.Heroes.Count;
        Expect.Equal(4, heroes, "the leader, the player and his two knights");
        Expect.True(troops + heroes <= ForcePlanner.SkirmishMaxFighters, $"{troops} troops + {heroes} heroes");
        int bandSize = ForcePlanner.SkirmishBandSize(battle, 28);
        Expect.True(bandSize >= 6 + 10 && bandSize <= 18 + 10, $"a random band of 6..18, +10 for the road's danger: {bandSize}");
        Expect.Equal(bandSize, ForcePlanner.SkirmishBandSize(battle, 28), "the same band every time the battle is planned");
        Expect.Equal(bandSize + 10, troops, "every man of the band and the guards (scale 1), no retinues");
        var bandGroup = plan.Attacker.Groups.Single();
        Expect.Equal("band", bandGroup.LadderSource, "the band is armed by its year, not as village levies");
        Expect.Near(3.93, bandGroup.Tier, 0.01, "1150: tier 1.5 + 283 / 116.5");
        Expect.Near(1.5, ForcePlanner.SkirmishBandTier(867, 1), 1e-9, "867: rabble");
        Expect.Near(3.5, ForcePlanner.SkirmishBandTier(1100, 1), 0.01, "1100: solid tier 3-4 foot");
        Expect.Near(4.5, ForcePlanner.SkirmishBandTier(1300, 1), 1e-9, "at most 4.5");
        Expect.Near(4.2, ForcePlanner.SkirmishBandTier(1100, 4), 0.01, "soldiers a step above");
        Expect.Equal(14, ForcePlanner.SkirmishLeaderProwess(1100), "a leader of 1100 has prowess 14 at least");
        Expect.True(req.Attacker.Heroes.Single().Skills.Count > 0 && plan.Heroes.Heroes.First(h => h.Side == SideKey.Attacker).Character.Prowess >= 15,
            "the band's leader of 1150 is raised to the year's prowess");
        Expect.Equal((2, 5), ForcePlanner.SkirmishBandRange(3), "an assassin brings 2..5 cutthroats");
        Expect.Equal("The Bandits' Lair", req.Presentation.Title, "bandits are sought out at their camp (PROTOCOL §37)");
        Expect.True(plan.Battlefield.PoolTerrain is "forest" or "taiga" or "jungle", $"skirmishes are fought in the woods (pool {plan.Battlefield.PoolTerrain})");
        Expect.True(plan.Battlefield.Kind != "village", "never in the castle's village");

        // A big band is cut down to the cap, still without the 40-per-side floor.
        band.Levies = 200;
        band.Size = 200;
        var big = Mapper.Plan(battle, new MapperOptions()).Request;
        int bigTroops = big.Attacker.Troops.Sum(t => t.Count) + big.Defender.Troops.Sum(t => t.Count);
        Expect.True(bigTroops + heroes <= ForcePlanner.SkirmishMaxFighters, $"capped: {bigTroops} + {heroes}");
        band.Levies = 28;
        band.Size = 28;

        var result = new BattleResult { Id = req.Id, Status = "finished", Winner = "defender" };
        var outcome = Mapper.BuildOutcome(battle, req, result, new MapperOptions());
        Expect.Equal(KeypadLayout.PayloadVersion, outcome.PayloadVersion, "no battle clock for a skirmish");
        Expect.Equal(0, outcome.Armies.Count, "the bands are no CK3 armies");
        Expect.True(outcome.PlayerWon);
        Expect.True(outcome.Summary.StartsWith("The road is clear", StringComparison.Ordinal), outcome.Summary);
    }

    /// <summary>A single combat (PROTOCOL §18): the player defends against a challenger; one character per side, no armies.</summary>
    private static BattleSnapshot DuelBattle(int fatality)
    {
        var me = Scenarios.Ref(501, "Henry", "d_normandy");
        var foe = Scenarios.Ref(502, "Hugo");
        var norman = Scenarios.Cul("norman", "heritage_frankish", "western_unit_gfx", Scenarios.High);
        var meC = Scenarios.Person(me, 30, "KNIGHT", norman, 14, 8, 30, 3, 0, player: true);
        var foeC = Scenarios.Person(foe, 31, "KNIGHT", norman, 18, 5, 28, 1, 0);
        var att = Scenarios.Side(SideKey.Attacker, false, foe, Array.Empty<ArmySnapshot>(), new[] { foeC });
        var def = Scenarios.Side(SideKey.Defender, true, me, Array.Empty<ArmySnapshot>(), new[] { meC });
        var battle = Scenarios.Battle(40, "Single Combat", (1150, 6, 1), 2262, "Saumur", "c_anjou", "plains", "none", "castle_holding", 2, false, false,
            null, 501, att, def, ("region", "world_europe_west"));
        battle.KnightFactsLogged = true;
        battle.IsDuel = true;
        battle.DuelFatality = fatality;
        return battle;
    }

    private static void DuelRequest()
    {
        var plan = Mapper.Plan(DuelBattle(fatality: 1), new MapperOptions());
        var req = plan.Request;
        Expect.Equal("defender", req.PlayerSide);
        foreach (var side in new[] { req.Attacker, req.Defender })
        {
            Expect.Equal(0, side.Troops.Count, "no troops, no retinues");
            Expect.Equal(1, side.Heroes.Count, "one duelist per side");
            Expect.Equal(side.Heroes[0].Key, side.General, "the duelist is the side's general (no stock general joins)");
            Expect.Near(0.5, side.Heroes[0].DeathChance ?? -1, 1e-6, "'possible': even odds of a kill");
        }
        Expect.True(req.Defender.Heroes[0].IsPlayer, "the player fights as himself");
        Expect.Equal("Single Combat", req.Presentation.Title);
        Expect.Equal("auto", req.Presentation.Deployment);
        Expect.Null(req.Balance, "no strength balance for two men");
        Expect.Near(0, Mapper.Plan(DuelBattle(fatality: 4), new MapperOptions()).Request.Attacker.Heroes[0].DeathChance ?? -1, 1e-6, "practice: never a kill");
        Expect.Near(1, BattleMapper.DuelDeathChance(2), 1e-6, "'always': every downed duelist dies");

        // The prowess gap (2026-10-20, live b322: 88 against 29 fought as near equals): the stronger duelist hits harder and takes less.
        var gapBattle = DuelBattle(fatality: 1);
        var duelists = gapBattle.Sides.SelectMany(s => s.Characters).ToList();
        duelists[0].Prowess = 88;
        duelists[1].Prowess = 29;
        var gapReq = Mapper.Plan(gapBattle, new MapperOptions()).Request;
        var champion = gapReq.Attacker.Heroes.Concat(gapReq.Defender.Heroes).Single(h => h.Ck3Id == duelists[0].Ref.Ck3Id);
        var novice = gapReq.Attacker.Heroes.Concat(gapReq.Defender.Heroes).Single(h => h.Ck3Id == duelists[1].Ref.Ck3Id);
        Expect.True(champion.Buffs is { } cb && cb.DamageDealtMeleeMult >= 1.5f - 1e-4 && cb.DamageTakenMult <= 0.5f + 1e-4,
            "a gap of 59: +50 % dealt, -50 % taken (the cap)");
        Expect.True(novice.Buffs is null || (novice.Buffs.DamageDealtMeleeMult <= 1f + 1e-4 && novice.Buffs.DamageTakenMult >= 1f - 1e-4), "the weaker gets nothing");
        Expect.True(champion.Skills["OneHanded"] > novice.Skills["OneHanded"] + 50, "the champion's skill keeps rising past 330");

        // Venues (PROTOCOL §27). An older mod (no ck3xbl_d_noble): the field as before.
        Expect.Equal("field", req.Kind, "older mod: a field");
        var noble = DuelBattle(fatality: 1);
        noble.DuelNoble = true;
        var arena = Mapper.Plan(noble, new MapperOptions()).Request;
        Expect.Equal("arena", arena.Kind, "a noble foe: the arena");
        Expect.Equal($"arena_{arena.Defender.Culture}_a", arena.Scene, "the arena of the challenged side's culture");
        Expect.Equal(req.Scene, arena.FallbackScene, "the field it would have had stays as the fallback");
        var lowborn = DuelBattle(fatality: 1);
        lowborn.DuelNoble = false;
        var woods = Mapper.Plan(lowborn, new MapperOptions());
        Expect.Equal("field", woods.Request.Kind, "a lowborn foe: a field in the woods");
        Expect.Equal("forest", woods.Battlefield.PoolTerrain, $"wooded field (pool {woods.Battlefield.PoolTerrain}, scene {woods.Request.Scene})");
        // Tournament bouts (PROTOCOL §29): always the arena, whoever the foe; nobody dies; wrestlers fight bare-handed.
        var bout = DuelBattle(fatality: 4);
        bout.DuelTourney = 1;
        bout.DuelNoble = false;
        var boutReq = Mapper.Plan(bout, new MapperOptions()).Request;
        Expect.Equal("arena", boutReq.Kind, "a tournament bout is fought in the arena, even against a lowborn foe");
        Expect.Equal(1, boutReq.Tourney);
        Expect.Equal("Tournament: Single Combat", boutReq.Presentation.Title);
        Expect.True(boutReq.Attacker.Heroes.Concat(boutReq.Defender.Heroes).All(h => h.DeathChance == 0), "practice: nobody dies");
        var wrestling = DuelBattle(fatality: 4);
        wrestling.DuelTourney = 2;
        var wrestReq = Mapper.Plan(wrestling, new MapperOptions()).Request;
        Expect.Equal(2, wrestReq.Tourney);
        Expect.Equal("Tournament: Wrestling", wrestReq.Presentation.Title);
        foreach (var h in wrestReq.Attacker.Heroes.Concat(wrestReq.Defender.Heroes))
        {
            Expect.True(BattleMapper.WrestlerEmptySlots.All(s => h.Equipment.Overrides.TryGetValue(s, out var id) && id == ""), $"{h.Key} wrestles bare-handed");
            Expect.True(h.Equipment.Overrides["Body"] == "" && h.Equipment.Overrides["Leg"] == "", $"{h.Key} wrestles without any armour");
            Expect.True(h.Buffs?.DamageDealtMeleeMult >= BattleMapper.WrestlerBlowFactor, $"{h.Key}'s blows count more");
            Expect.Null(h.Look, "no costume for a wrestler");
        }
        var boutHero = Mapper.Plan(bout, new MapperOptions()).Request.Attacker.Heroes[0];
        var wrestler = wrestReq.Attacker.Heroes.Single(h => h.Key == boutHero.Key);
        Expect.Near(Math.Max(0.25, boutHero.HealthMultiplier * BattleMapper.WrestlerHealthFactor), wrestler.HealthMultiplier, 1e-4, "a wrestler's health pool is cut");
        var melee = DuelBattle(fatality: 4);
        melee.DuelTourney = 3;
        Expect.Equal("Tournament: Grand Melee", Mapper.Plan(melee, new MapperOptions()).Request.Presentation.Title);
        Expect.Equal("Турнир: борьба", Mapper.Plan(wrestling, new MapperOptions { Language = "ru" }).Request.Presentation.Title);
        // Joust and archery (PROTOCOL §36).
        var joust = DuelBattle(fatality: 4);
        joust.DuelTourney = 4;
        var joustReq = Mapper.Plan(joust, new MapperOptions()).Request;
        Expect.Equal("Tournament: Joust", joustReq.Presentation.Title);
        foreach (var h in joustReq.Attacker.Heroes.Concat(joustReq.Defender.Heroes))
        {
            Expect.Equal("empire_lance_1_t3_blunt", h.Equipment.Overrides["Item0"], $"{h.Key} rides with a blunt lance");
            Expect.True(h.Equipment.Overrides["Horse"].EndsWith("_horse_tournament", StringComparison.Ordinal), $"{h.Key} on a tournament horse");
            Expect.Equal("Cavalry", h.Formation);
        }
        var archery = DuelBattle(fatality: 4);
        archery.DuelTourney = 5;
        var archReq = Mapper.Plan(archery, new MapperOptions()).Request;
        Expect.Equal("Tournament: Archery", archReq.Presentation.Title);
        Expect.True(archReq.Scene is "arena_aserai_a" or "arena_battania_a" or "arena_empire_a", $"an arena with the archery set ({archReq.Scene})");
        Expect.True(archReq.TourneyPar is >= 4 and <= 12, $"mark {archReq.TourneyPar}");
        Expect.True(archReq.Attacker.Heroes.Concat(archReq.Defender.Heroes).All(h => h.Equipment.Overrides["Item0"] == "hunting_bow"), "bows for everyone");
        Expect.Equal(4, BattleMapper.ArcheryPar(0));
        Expect.Equal(12, BattleMapper.ArcheryPar(40));
        Expect.Equal("taiga", BattleMapper.WoodsTerrain("taiga"), "taiga stays");
        Expect.Equal("forest", BattleMapper.WoodsTerrain("plains"), "plains turn to forest");
    }

    private static void DuelOutcome()
    {
        var battle = DuelBattle(fatality: 1);
        var req = Mapper.Plan(battle, new MapperOptions()).Request;
        string foeKey = req.Attacker.Heroes[0].Key, myKey = req.Defender.Heroes[0].Key;
        BattleResult Result(string winner, string foeState) => new()
        {
            Id = req.Id, Status = "finished", Winner = winner,
            Heroes = { new HeroResult { Key = foeKey, State = foeState }, new HeroResult { Key = myKey, State = "alive" } },
        };

        var won = Mapper.BuildOutcome(battle, req, Result("defender", "killed"), new MapperOptions());
        Expect.True(won.IsDuel);
        Expect.Equal(2, won.OutcomeCode, "the defender (the player) won");
        Expect.True(won.DuelLoserKilled, "the challenger fell dead");
        Expect.True(won.PlayerWon);
        Expect.Equal(KeypadLayout.PayloadVersion, won.PayloadVersion, "payload 3: no battle clock for a duel");
        Expect.Equal(0, won.Armies.Count + won.Characters.Count + won.Awards.Count, "no losses, fates or awards: CK3's single combat applies them");
        var regs = KeypadPayload.Encode(won, null).Registers;
        Expect.Equal(2, regs[KeypadLayout.Outcome]);
        Expect.Equal(1, regs[KeypadLayout.DuelLoserKilled]);

        var downed = Mapper.BuildOutcome(battle, req, Result("defender", "wounded"), new MapperOptions());
        Expect.False(downed.DuelLoserKilled, "only knocked down");
        Expect.Equal(0, KeypadPayload.Encode(downed, null).Registers[KeypadLayout.DuelLoserKilled]);
    }

    private static void KnightsEffectiveness()
    {
        Expect.Equal(1.0, KnightBuffPlanner.KnightEffectiveness(null));
        Expect.Equal(0.5, KnightBuffPlanner.KnightEffectiveness(0));
        Expect.Equal(0.5, KnightBuffPlanner.KnightEffectiveness(-200));
        Expect.Equal(1.5, KnightBuffPlanner.KnightEffectiveness(1500));
        Expect.Equal(3.0, KnightBuffPlanner.KnightEffectiveness(int.MaxValue));
        // At 300 % the damage factor 2.0 stays under the dealt cap of 3 / 1.05 (his side deals 1.05); the retinue keeps 2.0.
        foreach (var (permille, health, damage, retinue) in new (int?, double, double, double)[]
                 { (null, 1.6, 1, 1), (0, 1.2, 0.75, 0.75), (-200, 1.2, 0.75, 0.75), (1500, 2.0, 1.25, 1.25), (5000, 3.2, 2.0, 2.0) })
        {
            var battle = KnightsBattle();
            battle.Attacker.Armies[0].KnightEffectivenessPermille = permille;
            var req = Mapper.BuildRequest(battle, new MapperOptions());
            var odo = HeroOf(req, 2);
            Expect.Near(health, odo.HealthMultiplier, 1e-5, $"health at a_ke {permille}");
            Expect.Near(damage, odo.DamageMultiplier, 1e-5, $"damage at a_ke {permille}");
            Expect.Near(retinue, req.Attacker.GroupBuffs["knight-retinue#2"].HealthMult, 1e-5, $"retinue at a_ke {permille}");
            Expect.Near(1.4, HeroOf(req, 1).HealthMultiplier, 1e-5, "the commander keeps P");
        }
        // The retinue's size stays prowess-only (D9).
        int RetinueAgents(int permille)
        {
            var battle = KnightsBattle();
            battle.Attacker.Armies[0].KnightEffectivenessPermille = permille;
            return Mapper.Plan(battle, new MapperOptions()).Attacker.Retinues.Single(g => g.Key.CharTag == 2).Agents;
        }
        Expect.Equal(RetinueAgents(1000), RetinueAgents(3000));
    }

    private static void KnightsAccolades()
    {
        // A second acclaimed archer (level 1, rank 1) in the same army: the CK3 multipliers add before the conversion.
        var battle = KnightsBattle();
        var clement = Scenarios.Person(Scenarios.Ref(303, "Clement"), 3, "KNIGHT", battle.Attacker.Characters[0].Culture, 8, 4, 24, 0, 12);
        clement.Accolade = new AccoladeSnapshot { Active = true, Rank = 1, Primary = "archer_attribute", Levels = { ["archer_attribute"] = 1 } };
        battle.Attacker.Characters.Add(clement);
        string bowGroup = MaaGroup(battle.Attacker.Armies[0], "bowmen");
        var b = Mapper.BuildRequest(battle, new MapperOptions()).Attacker.GroupBuffs[bowGroup];
        Expect.Near(1 + 0.5 * (0.4 + 0.1), b.DamageDealtMult, 1e-6);
        Expect.Near(1 + 0.5 * (0.2 + 0.1), b.HealthMult, 1e-6);
        Expect.Near(2 * 3 + 2 * 1, b.InitialMoraleAdd, 1e-6);

        // An inactive accolade does nothing (no buff, no captaincy).
        battle = KnightsBattle();
        Character(battle, 2).Accolade!.Active = false;
        var req = Mapper.BuildRequest(battle, new MapperOptions());
        Expect.False(req.Attacker.GroupBuffs.ContainsKey(bowGroup));
        Expect.Null(HeroOf(req, 2).CaptainOf);

        // Levels beyond the rank: secondary attributes lose levels first, then the primary.
        battle = KnightsBattle();
        var acc = Character(battle, 2).Accolade!;
        acc.Rank = 1;
        acc.Levels["valiant_attribute"] = 1;
        req = Mapper.BuildRequest(battle, new MapperOptions());
        b = req.Attacker.GroupBuffs[bowGroup];
        Expect.Near(1.05, b.DamageDealtMult, 1e-6, "Archer level 1");
        Expect.Near(2, b.InitialMoraleAdd, 1e-6);
        Expect.False(req.Attacker.GroupBuffs.ContainsKey("levy#12"), "Valiant dropped");

        // An acclaimed knight without an army tag buffs nothing.
        battle = KnightsBattle();
        Character(battle, 2).ArmyTag = 0;
        Expect.False(Mapper.BuildRequest(battle, new MapperOptions()).Attacker.GroupBuffs.ContainsKey(bowGroup));
    }

    private static void KnightsCaptaincy()
    {
        var options = new MapperOptions();
        // The same acclaimed archer on the player's side: Order of Battle is the player's, so no captaincy and no re-forming.
        var battle = KnightsBattle();
        var hugh = Character(battle, 11);
        hugh.Accolade = new AccoladeSnapshot { Active = true, Rank = 3, Primary = "archer_attribute", Levels = { ["archer_attribute"] = 2 } };
        var plain = KnightsBattle();
        Character(plain, 2).Accolade = null;
        var reference = HeroOf(Mapper.BuildRequest(plain, options), 11);
        string odoFormation = HeroOf(Mapper.BuildRequest(plain, options), 2).Formation;
        var spec = HeroOf(Mapper.BuildRequest(battle, options), 11);
        Expect.Null(spec.CaptainOf);
        Expect.Equal(reference.Formation, spec.Formation);
        Expect.Equal(reference.Equipment.Overrides.GetValueOrDefault("Horse"), spec.Equipment.Overrides.GetValueOrDefault("Horse"));

        // Never the side's general.
        battle = KnightsBattle();
        Character(battle, 1).Accolade = new AccoladeSnapshot { Active = true, Rank = 3, Primary = "archer_attribute", Levels = { ["archer_attribute"] = 2 } };
        Expect.Null(HeroOf(Mapper.BuildRequest(battle, options), 1).CaptainOf);

        // A Lancer captains the cavalry and keeps his horse; an Archer whose army has no archers keeps his own formation.
        battle = KnightsBattle();
        battle.Attacker.Armies[0].Regiments.Add(new RegimentSnapshot { RegimentId = 16999, MaaKey = "armored_horsemen", Current = 100, Max = 100 });
        battle.Attacker.Armies[0].Size += 100;
        Character(battle, 2).Accolade = new AccoladeSnapshot { Active = true, Rank = 2, Primary = "lancer_attribute", Levels = { ["lancer_attribute"] = 2 } };
        spec = HeroOf(Mapper.BuildRequest(battle, options), 2);
        Expect.Equal("Cavalry", spec.CaptainOf);
        Expect.Equal("Cavalry", spec.Formation);
        Expect.False(spec.Equipment.Overrides.ContainsKey("Horse"), "he rides");

        battle = KnightsBattle();
        battle.Attacker.Armies[0].Regiments.RemoveAll(x => x.MaaKey == "bowmen");
        spec = HeroOf(Mapper.BuildRequest(battle, options), 2);
        Expect.Null(spec.CaptainOf);
        Expect.Equal(odoFormation, spec.Formation, "the culture's knight formation");
    }

    private static void KnightsHostileTerritory()
    {
        double Advantage(Action<BattleSnapshot> setup, SideKey side)
        {
            var battle = KnightsBattle();
            setup(battle);
            return PlannerFor(battle).EvaluationFor(side).Advantage;
        }
        void Marauder(BattleSnapshot b, int tag) =>
            Character(b, tag).Accolade = new AccoladeSnapshot { Active = true, Rank = 3, Primary = "marauder_attribute", Levels = { ["marauder_attribute"] = 3 } };

        // Marauder level 3 (+5) only for the combat attacker whose army stands in hostile territory.
        Expect.Near(10 + 5, Advantage(b => { Marauder(b, 2); b.Attacker.Armies[0].Territory = "hostile"; }, SideKey.Attacker), 1e-9);
        Expect.Near(10, Advantage(b => { Marauder(b, 2); b.Attacker.Armies[0].Territory = "own"; }, SideKey.Attacker), 1e-9);
        Expect.Near(10, Advantage(b => { Marauder(b, 2); b.Attacker.Armies[0].Territory = "other"; }, SideKey.Attacker), 1e-9);
        Expect.Near(8, Advantage(b => { Marauder(b, 11); b.Defender.Armies[0].Territory = "hostile"; }, SideKey.Defender), 1e-9);

        // the_wake level 4: +5 for an attacker in hostile territory only (the other steps hold anywhere).
        void Wake(BattleSnapshot b, int tag) { Character(b, tag).CombatTraits.Add("the_wake"); Character(b, tag).TraitLevels["the_wake"] = 4; }
        double hostile = Advantage(b => { Wake(b, 1); b.Attacker.Armies[0].Territory = "hostile"; }, SideKey.Attacker);
        double own = Advantage(b => { Wake(b, 1); b.Attacker.Armies[0].Territory = "own"; }, SideKey.Attacker);
        Expect.Near(5, hostile - own, 1e-9);
        double defHostile = Advantage(b => { Wake(b, 10); b.Defender.Armies[0].Territory = "hostile"; }, SideKey.Defender);
        double defOwn = Advantage(b => { Wake(b, 10); b.Defender.Armies[0].Territory = "own"; }, SideKey.Defender);
        Expect.Near(0, defHostile - defOwn, 1e-9);
    }

    private static void KnightsHostilityDirection()
    {
        Expect.Equal("out", KnightsTables.HostilityDirection);
        double Advantage(int ownOut, int enemyOut)
        {
            var battle = KnightsBattle();
            Character(battle, 1).CommanderTraits.Add("holy_warrior");
            battle.Attacker.FaithHostilityOut = ownOut;
            battle.Defender.FaithHostilityOut = enemyOut;
            return PlannerFor(battle).EvaluationFor(SideKey.Attacker).Advantage;
        }
        // "out": the holy warrior's own faith's view of the enemy's faith scales his advantage (hostile = full +4).
        Expect.Near(10 + 4, Advantage(2, 0), 1e-9);
        Expect.Near(10 + 2, Advantage(1, 3), 1e-9);
        Expect.Near(10, Advantage(0, 2), 1e-9);
    }

    // ---- combat traits, hero buffs and caps ---------------------------------------------------------------------------

    private static TraitHolder Holder(int martial, params string[] traits) => new() { Traits = traits, Martial = martial };

    private static TraitHolder Leveled(int martial, Dictionary<string, int> levels, params string[] traits) => new() { Traits = traits, Levels = levels, Martial = martial };

    /// <summary>The eight worked battles of tools/gen/knights/test_combat_traits.mjs.</summary>
    private static void KnightsGoldenTraitBattles()
    {
        var ev = new CombatTraitEvaluator(Tables.Knights.CombatTraits);
        (SideEvaluation A, SideEvaluation D) Battle(string terrain, string winter, double terrainAdvantage, TraitHolder? att, TraitHolder? def) =>
            ev.EvaluateBattle(new SideInput { Commander = att, Context = new TraitContext { Terrain = terrain, Winter = winter, Role = "attacker" } },
                              new SideInput { Commander = def, Context = new TraitContext { Terrain = terrain, Winter = winter, Role = "defender" } },
                              winter, terrainAdvantage);

        // 1. Forest: forest_fighter L3 (martial 8) vs a plain defender (martial 6): 19 vs 9.
        var (a, d) = Battle("forest", "none", 3, Leveled(8, new() { ["forest_fighter"] = 3 }, "forest_fighter"), Holder(6));
        Expect.Near(19, a.Advantage, 1e-9);
        Expect.Near(9, d.Advantage, 1e-9);
        Expect.Near(1.25, a.DamageDealtMult, 1e-4);
        Expect.Near(10, a.InitialMoraleAdd, 1e-4);
        Expect.Near(1, d.DamageDealtMult, 1e-4);
        Expect.Near(20, a.TacticsAdd, 1e-4);

        // 2. Mountains: flexible_leader cuts the defender's terrain advantage 12 by 20 %.
        (a, d) = Battle("mountains", "none", 12, Holder(5, "flexible_leader"), Holder(5));
        Expect.Near(14.6, d.Advantage, 1e-4);
        Expect.Near(1 + 0.025 * 9.6, d.DamageDealtMult, 1e-4);
        Expect.Near(15, a.TacticsAdd, 1e-4);

        // 3. reckless L3 in the open: roll shift +4, spread +16 -> volatility cap 1.15.
        (a, d) = Battle("plains", "none", 0, Leveled(5, new() { ["reckless"] = 3 }, "reckless"), Holder(5));
        Expect.Near(12, a.Advantage, 1e-4);
        Expect.Near(1.15, a.DamageTakenMult, 1e-4);
        Expect.Near((1 + 0.025 * 7) * 1.15, a.DamageDealtMult, 1e-4);
        Expect.Near(1.05, a.ChargeDamageMult, 1e-4);
        Expect.Near(5, a.TacticsAdd, 1e-4);

        // 4. Casualties: aggressive_attacker L1 vs unyielding_defender + organizer.
        (a, d) = Battle("plains", "none", 0, Leveled(5, new() { ["aggressive_attacker"] = 1 }, "aggressive_attacker"), Holder(5, "unyielding_defender", "organizer"));
        Expect.Near(0.9 * 1.2, d.FatalShareMult, 1e-4);
        Expect.Near(0.9, d.RetreatLossMult, 1e-4);
        Expect.Near(1.2, d.TroopDeathChanceMult, 1e-4);

        // 5. Culture parameters gate personality terrain bonuses.
        double BraveIn(params string[] cultureParameters) =>
            ev.EvaluateCharacter(new TraitHolder { Traits = new[] { "brave" }, CultureParameters = cultureParameters },
                                 new TraitContext { Terrain = "mountains", Role = "defender" }).Get("side", "advantage") ?? 0;
        Expect.Near(0, BraveIn(), 1e-9);
        Expect.Near(1.5, BraveIn("mountain_trait_bonuses"), 1e-9);

        // 6. Harsh winter: winter_soldier L2 beats the defender's +5.
        (a, d) = Battle("plains", "harsh", 0, Leveled(4, new() { ["winter_soldier"] = 2 }, "winter_soldier"), Holder(4));
        Expect.Near(14, a.Advantage, 1e-9);
        Expect.Near(9, d.Advantage, 1e-9);

        // 7. Heroes: brave + wounded_3; berserker + giant; blademaster L2.
        var ctx = new TraitContext { Terrain = "plains", Role = "attacker" };
        var h1 = ev.EvaluateCharacter(Holder(0, "brave", "wounded_3"), ctx);
        Expect.Near(10, h1.Get("hero", "initialMoraleAdd")!.Value, 1e-9);
        Expect.Equal(1.0, h1.Get("hero", "noPanic"));
        Expect.Near(0.5, h1.Get("hero", "startHealthFraction")!.Value, 1e-9);
        Expect.Near(8, h1.Get("hero", "deathChanceMult")!.Value, 1e-9);
        Expect.Near(10, h1.Get("retinue", "initialMoraleAdd")!.Value, 1e-9);
        var h2 = ev.EvaluateCharacter(Holder(0, "berserker", "giant"), ctx);
        Expect.Near(1.2 * 1.05, h2.Qualified.Single(q => q.Effect == "hero.damageDealtMult" && q.When == new EffectQualifiers("melee", null, null, null, null, null, null)).Value, 1e-9);
        Expect.Near(0.25, h2.Get("hero", "knockdownResistanceAdd")!.Value, 1e-9);
        var h3 = ev.EvaluateCharacter(Leveled(0, new() { ["lifestyle_blademaster"] = 2 }, "lifestyle_blademaster"), ctx);
        Expect.Near(Math.Pow(1.02, 3), h3.Get("hero", "swingSpeedMult")!.Value, 1e-9);

        // 8. Army owner: greatest_of_khans heavy cavalry deals x1.5.
        var khan = ev.EvaluateCharacter(Holder(0, "greatest_of_khans"), new TraitContext { Terrain = "steppe", Role = "attacker" });
        Expect.Near(1.5, khan.Qualified.Single(q => q.Effect == "group.damageDealtMult" && q.When.MaaBaseType == "heavy_cavalry").Value, 1e-9);
    }

    private static void KnightsHeroBuffs()
    {
        var battle = KnightsBattle();
        Character(battle, 11).Traits.Add("wounded_3");
        Character(battle, 11).CombatTraits.Add("brave");
        Character(battle, 10).CombatTraits.Add("berserker");
        Character(battle, 10).CombatTraits.Add("tourney_participant");
        Character(battle, 10).TraitLevels["tourney_participant:bow"] = 2;
        Character(battle, 1).CommanderTraits.Add("reckless");
        var req = Mapper.BuildRequest(battle, new MapperOptions());

        var hugh = HeroOf(req, 11);
        var hb = Expect.NotNull(hugh.Buffs);
        Expect.Near(10, hb.InitialMoraleAdd, 1e-6);
        Expect.True(hb.NoPanic);
        Expect.Near(0.5, hb.StartHealthFraction, 1e-6);
        Expect.Near(0.9, hugh.DeathChance!.Value, 1e-6, "0.3 x 8, capped at 0.9");

        var me = HeroOf(req, 10);
        var mb = Expect.NotNull(me.Buffs);
        Expect.Near(1.2, mb.DamageDealtMeleeMult, 1e-6);
        Expect.Near(1.25, mb.DamageDealtVsHeroMult, 1e-6);
        Expect.Near(1, mb.DamageDealtRangedMult, 1e-6);
        int weapon = Math.Clamp(60 + 8 * 12, 60, 330);
        Expect.Equal(weapon + 10 + 10, me.Skills["Bow"], "tourney bow track level 2");
        Expect.Equal(weapon + 10 + 10, me.Skills["Crossbow"]);
        Expect.Equal(weapon, me.Skills["OneHanded"]);

        // The general gets his side commander's Tactics (reckless L0: -15 + 5).
        Expect.Equal(100 - 10, HeroOf(req, 1).Skills["Tactics"]);
        Expect.Equal(80, me.Skills["Tactics"], "no commander traits on the defending side");
    }

    private static void KnightsCombinedCaps()
    {
        // Durability: health 6 on a side that takes 0.7 -> the hero's own damage taken rises to 6 / (6 x 0.7).
        var spec = new HeroSpec { HealthMultiplier = 6 };
        KnightBuffPlanner.CapHero(spec, new SideBuffs { DamageTakenMult = 0.7f });
        Expect.Near(1 / 0.7, Expect.NotNull(spec.Buffs).DamageTakenMult, 1e-5);
        Expect.Near(6, spec.HealthMultiplier, 1e-6, "healthMultiplier is kept");

        // Dealt: 3 x 1.6 x 1.6 x 1.3 on a side dealing 1.5 must come under 3 / 1.5; reduced vs-hero, charge, melee, then damage.
        spec = new HeroSpec
        {
            DamageMultiplier = 3,
            Buffs = new HeroBuffs { DamageDealtMeleeMult = 1.6f, DamageDealtVsHeroMult = 1.6f, ChargeDamageMult = 1.3f },
        };
        KnightBuffPlanner.CapHero(spec, new SideBuffs { DamageDealtMult = 1.5f });
        var b = spec.Buffs;
        double product = spec.DamageMultiplier * Math.Max(b?.DamageDealtMeleeMult ?? 1, b?.DamageDealtRangedMult ?? 1) *
                         (b?.DamageDealtVsHeroMult ?? 1) * (b?.ChargeDamageMult ?? 1) * 1.5;
        Expect.True(product <= 3.0 + 1e-5, $"dealt product {product}");
        Expect.Null(b, "vs-hero, charge and melee all back at 1");
        Expect.Near(3 / 1.5, spec.DamageMultiplier, 1e-5, "damageMultiplier last");

        // The user's example passes unchanged: health 3, damage 2, neutral side.
        spec = new HeroSpec { HealthMultiplier = 3, DamageMultiplier = 2 };
        KnightBuffPlanner.CapHero(spec, null);
        Expect.Near(3, spec.HealthMultiplier, 1e-6);
        Expect.Near(2, spec.DamageMultiplier, 1e-6);
        Expect.Null(spec.Buffs);
    }

    private static void KnightsNonFinite()
    {
        Expect.Equal(1.0, KnightBuffPlanner.Safe(double.NaN, 0.5, 2, 1));
        Expect.Equal(1.0, KnightBuffPlanner.Safe(double.PositiveInfinity, 0.5, 2, 1));
        Expect.Equal(0.0, KnightBuffPlanner.Safe(double.NegativeInfinity, -20, 30, 0));
        Expect.Equal(0.5, KnightBuffPlanner.KnightEffectiveness(int.MinValue));

        // NaN in an accolade input and in the advantage conversion, infinity in an accolade input: neutral members, valid JSON.
        var t = MappingTables.Load(DataLocator.Resolve(null));
        var archer = t.Knights.Accolades.Types["archer_attribute"].Bannerlord.PerLevel["2"].Groups[0].Ck3;
        archer["damage_mult"] = double.NaN;
        archer["toughness_mult"] = double.PositiveInfinity;
        t.Knights.CombatTraits.Conversion.Advantage.PerPoint = double.NaN;
        var battle = KnightsBattle();
        var req = Mapper.Plan(battle, new MapperOptions(), t).Request;
        var bow = req.Attacker.GroupBuffs[MaaGroup(battle.Attacker.Armies[0], "bowmen")];
        Expect.Equal(1f, bow.DamageDealtMult);
        Expect.Equal(1f, bow.HealthMult);
        Expect.Near(6, bow.InitialMoraleAdd, 1e-6, "the finite part stays");
        Expect.Equal(1f, Expect.NotNull(req.Attacker.Buffs).DamageDealtMult);
        string json = ProtocolJson.Serialize(req);
        Expect.False(json.Contains("NaN", StringComparison.Ordinal) || json.Contains("Infinity", StringComparison.Ordinal));
    }

    // ---- artifacts ----------------------------------------------------------------------------------------------------

    private static ArtifactSnapshot Art(string slot, string type, string rarity, string? helmet = null) =>
        new() { SlotType = slot, Type = type, Rarity = rarity, HelmetFeature = helmet };

    /// <summary>Cases computed with resolve_artifact_overrides.mjs (heroes of tier >= 2 compare against the kit).</summary>
    private static readonly (string Kit, string Culture, bool Mounted, ArtifactSnapshot[] Artifacts, string Expected)[] ArtifactCases =
    {
        ("vla_king_template_bat_m", "vlandia", true,
            new[] { Art("primary_armament", "sword", "famed"), Art("armor", "armor_mail", "illustrious", "armor_helmet_great_helmet"), Art("helmet", "helmet", "illustrious") },
            "Body=sturgian_fortified_armor,Head=western_crowned_plated_helmet,Item2=vlandia_noble_sword_1_t5"),     // the crown wins Head
        ("stu_bat_template_medium", "sturgia", false, new[] { Art("primary_armament", "axe", "illustrious") }, ""),   // already in the kit
        ("khu_bat_template_medium", "khuzait", true, new[] { Art("primary_armament", "composite", "famed"), Art("armor", "armor_lamellar", "common") },
            "Item3=heavy_steppe_arrows"),                                                                          // never downgrade
        ("emp_bat_template_medium", "empire", true, new[] { Art("primary_armament", "spear", "masterwork") }, "Item2=empire_lance_3_t5"),
        ("vla_bat_template_heavy", "vlandia", true, new[] { Art("primary_armament", "dagger", "common"), Art("primary_armament", "crossbow", "famed") },
            "Item1=bolt_c,Item3=crossbow_g"),                                                                       // dagger skipped
        ("bat_bat_template_medium", "battania", false, new[] { Art("primary_armament", "longbow", "illustrious"), Art("armor", "armor_mail", "common") },
            "Item3=piercing_arrows"),
    };

    private static string Overrides(IReadOnlyDictionary<string, string> o) =>
        string.Join(",", o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

    private static readonly Lazy<AppearanceEngine> KitEngine = new(() => AppearanceEngine.Load(null, null, Tables.Directory, _ => { }));

    private static void KnightsArtifactCases()
    {
        Expect.True(KitEngine.Value.Equipment.HasData, "bl_ruler_kits.json loaded by Appearance");
        var outfitter = new ArtifactOutfitter(Tables);
        foreach (var (kit, culture, mounted, artifacts, expected) in ArtifactCases)
        {
            var overrides = outfitter.Resolve(ArtifactOutfitter.KitItems(Tables.Kits[kit]), culture, mounted, heroTier: 2, artifacts);
            Expect.Equal(expected, Overrides(overrides), kit);
            // Appearance keeps every mapper override (fixed slots): the artifact slots come out of Enrich unchanged.
            var spec = new EquipmentSpec { Roster = kit };
            foreach (var (slot, id) in overrides) spec.Overrides[slot] = id;
            var planned = Expect.NotNull(KitEngine.Value.Equipment.Plan(spec, culture, 2, false, true, 77, null, out _));
            foreach (var (slot, id) in overrides) Expect.Equal(id, planned.Overrides.GetValueOrDefault(slot), $"{kit} {slot} after Enrich");
        }
    }

    /// <summary>The reference's self-test: every battle kit x category x rarity x mounted/foot, no unknown item, no replaced shield,
    /// no launcher without ammunition; only daggers find no slot.</summary>
    private static void KnightsArtifactSweep()
    {
        var t = Tables;
        var outfitter = new ArtifactOutfitter(t);
        var typesByCategory = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (type, category) in t.Knights.Artifacts.TypeToCategory) typesByCategory.TryAdd(category, type);
        static string SlotOf(string type) => type.StartsWith("armor_", StringComparison.Ordinal) ? "armor" : type.StartsWith("helmet", StringComparison.Ordinal) ? "helmet" : "primary_armament";
        int cases = 0;
        foreach (var (rosterId, roster) in t.Kits.Where(k => !k.Key.Contains("_civ_", StringComparison.Ordinal)))
        {
            var items = ArtifactOutfitter.KitItems(roster);
            string culture = rosterId.Split('_')[0] switch
            {
                "vla" => "vlandia", "stu" => "sturgia", "bat" => "battania", "ase" => "aserai", "khu" => "khuzait", _ => "empire",
            };
            foreach (var (category, type) in typesByCategory)
            foreach (var rarity in new[] { "common", "masterwork", "famed", "illustrious" })
            foreach (bool mounted in new[] { true, false })
            {
                cases++;
                var log = new List<string>();
                var overrides = outfitter.Resolve(items, culture, mounted, heroTier: 2, new[] { Art(SlotOf(type), type, rarity) }, log);
                foreach (var skip in log.Where(l => l.StartsWith("skip", StringComparison.Ordinal)))
                    Expect.True(skip.StartsWith("skip dagger", StringComparison.Ordinal), $"{rosterId} {type} {rarity}: {skip}");
                foreach (var (slot, id) in overrides)
                {
                    Expect.True(t.Items.ContainsKey(id), $"{rosterId} {type} {rarity}: unknown {id}");
                    Expect.False(items.TryGetValue(slot, out var old) && ArtifactOutfitter.GroupOf(t.Items.GetValueOrDefault(old)) == "shield", $"{rosterId}: replaced the shield in {slot}");
                }
                var after = new Dictionary<string, string>(items);
                foreach (var (slot, id) in overrides) after[slot] = id;
                var groups = new[] { "Item0", "Item1", "Item2", "Item3" }.Select(s => after.TryGetValue(s, out var id) ? ArtifactOutfitter.GroupOf(t.Items.GetValueOrDefault(id)) : "empty").ToList();
                Expect.True(!groups.Contains("bow") || groups.Contains("arrows"), $"{rosterId} {type} {rarity} {mounted}: bow without arrows");
                Expect.True(!groups.Contains("crossbow") || groups.Contains("bolts"), $"{rosterId} {type} {rarity} {mounted}: crossbow without bolts");
            }
        }
        Expect.True(cases > 5000, $"only {cases} cases");
    }

    private static void KnightsArtifactLandless()
    {
        // A landless Vlandian knight (tier 0) with a common mail: Body = the hauberk the table picks (body 27), although the lord kit
        // he is built on wears more; Appearance's baron/landless downgrade leaves the fixed slots alone.
        var battle = KnightsBattle();
        Character(battle, 11).Artifacts.Add(Art("armor", "armor_mail", "common"));
        var hugh = HeroOf(Mapper.BuildRequest(battle, new MapperOptions()), 11);
        Expect.Equal("hauberk", hugh.Equipment.Overrides["Body"]);
        Expect.Equal("kettle_hat_over_padded_coif", hugh.Equipment.Overrides["Head"]);
        var planned = Expect.NotNull(KitEngine.Value.Equipment.Plan(hugh.Equipment, "vlandia", 0, false, true, hugh.Ck3Id, null, out _));
        Expect.Equal("hauberk", planned.Overrides["Body"]);
        Expect.Equal("kettle_hat_over_padded_coif", planned.Overrides["Head"]);
        // The same knight at tier 2 keeps the stronger kit armour (never downgrade).
        Character(battle, 11).Tier = 2;
        var count = HeroOf(Mapper.BuildRequest(battle, new MapperOptions()), 11);
        Expect.False(count.Equipment.Overrides.ContainsKey("Body"));
    }

    // ---- fates, awards, casualties -------------------------------------------------------------------------------------

    private static void KnightsFates()
    {
        var battle = KnightsBattle();
        var knights = PlannerFor(battle);
        // Odo: brave x2 on every weight, and as an active acclaimed knight x0.8 and capture x0.1.
        var odo = knights.FateFactorsFor(SideKey.Attacker, Character(battle, 2), asCommander: false);
        Expect.Near(1.6, odo.Wounded, 1e-9);
        Expect.Near(1.6, odo.Maimed, 1e-9);
        Expect.Near(1.6, odo.Killed, 1e-9);
        Expect.Near(0.1, odo.Capture, 1e-9);
        Expect.Equal(new FateFactors(1, 1, 1, 1), knights.FateFactorsFor(SideKey.Defender, Character(battle, 11), asCommander: false));

        // wounded_3 multiplies the knight's death weight by 4 once (the table's factor replaces the hard-coded rule).
        battle = KnightsBattle();
        Character(battle, 11).Traits.Add("wounded_3");
        var wounded = PlannerFor(battle).FateFactorsFor(SideKey.Defender, Character(battle, 11), asCommander: false);
        Expect.Equal(new FateFactors(1, 1, 4, 1), wounded);
        for (int tag = 1; tag <= 2000; tag++)
        {
            var c = new CharacterSnapshot { Tag = tag, Prowess = 10, Traits = { "wounded_3" } };
            FateContext Ctx(FateFactors? f) => new()
            {
                Bid = 3, Character = c, AsCommander = false, IsCk3Player = false, PlayerDeathIsReal = false, OnLosingSide = true, AtWar = true,
                OwnStrength = 1000, EnemyStrength = 1000, Factors = f,
            };
            Expect.Equal(FateResolver.Downed(Ctx(null)), FateResolver.Downed(Ctx(wounded)), $"tag {tag}");
        }

        // An enemy Blademaster level 2 raises the death weight of this side's knights x1.5 (not the commanders').
        battle = KnightsBattle();
        Character(battle, 11).Accolade = new AccoladeSnapshot { Active = true, Rank = 2, Primary = "blademaster_attribute", Levels = { ["blademaster_attribute"] = 2 } };
        knights = PlannerFor(battle);
        var vsBlademaster = knights.FateFactorsFor(SideKey.Attacker, Character(battle, 2), asCommander: false);
        Expect.Near(1.6 * 1.5, vsBlademaster.Killed, 1e-9);
        Expect.Near(1.6, vsBlademaster.Wounded, 1e-9);
        Expect.Near(1, knights.FateFactorsFor(SideKey.Attacker, Character(battle, 1), asCommander: true).Killed, 1e-9);

        // House Paragon level 3 on his side shields knights of the army owner's house (x0.25), and only them.
        battle = KnightsBattle();
        Character(battle, 2).Accolade = new AccoladeSnapshot { Active = true, Rank = 3, Primary = "house_knight_attribute", Levels = { ["house_knight_attribute"] = 3 } };
        var cousin = Scenarios.Person(Scenarios.Ref(304, "Hugues"), 4, "KNIGHT", Character(battle, 1).Culture, 9, 4, 22, 0, 12);
        cousin.HouseName = "Lusignan";
        battle.Attacker.Characters.Add(cousin);
        knights = PlannerFor(battle);
        Expect.Equal(new FateFactors(0.25, 0.25, 0.25, 1), knights.FateFactorsFor(SideKey.Attacker, cousin, asCommander: false));
        Expect.Near(1.6, knights.FateFactorsFor(SideKey.Attacker, Character(battle, 2), asCommander: false).Killed, 1e-9, "Odo is a Mauleon");

        // An acclaimed commander is captured far less often.
        battle = KnightsBattle();
        Character(battle, 1).Accolade = new AccoladeSnapshot { Active = true, Rank = 1, Primary = "valiant_attribute", Levels = { ["valiant_attribute"] = 1 } };
        Expect.Near(0.167, PlannerFor(battle).FateFactorsFor(SideKey.Attacker, Character(battle, 1), asCommander: true).Capture, 1e-9);
    }

    private static void KnightsAwards()
    {
        AwardHero Hero(SideKey side, string key, int tag, string role, int kills, string state, string? killedBy, CharacterSnapshot? c, bool playerSide,
            bool isPlayer = false) =>
            new(side, new HeroSpec { Key = key, Tag = tag, Ck3Id = 1000 + tag, Role = role, IsPlayer = isPlayer, Name = $"Sir {key}" },
                new HeroResult { Key = key, Tag = tag, Kills = kills, State = state, KilledBy = killedBy }, c, playerSide);
        CharacterSnapshot Char(int tier, string? house, bool acclaimed = false) =>
            new() { Tier = tier, HouseName = house, Accolade = acclaimed ? new AccoladeSnapshot { Active = true, Rank = 1 } : null };

        var heroes = new List<AwardHero>
        {
            Hero(SideKey.Attacker, "p", 1, "player", 12, "alive", null, Char(3, "Capet"), true, isPlayer: true),
            Hero(SideKey.Attacker, "k1", 2, "knight", 5, "alive", null, Char(0, null, acclaimed: true), true),
            Hero(SideKey.Attacker, "k2", 3, "knight", 3, "killed", "e4", Char(0, "Vexin"), true),
            Hero(SideKey.Attacker, "a1", 4, "knight", 0, "wounded", "k1", Char(0, "Vexin"), true),
            Hero(SideKey.Defender, "e1", 11, "commander", 0, "killed", "p", Char(3, "Blois"), false),
            Hero(SideKey.Defender, "e2", 12, "knight", 0, "wounded", "k1", Char(0, null), false),
            Hero(SideKey.Defender, "e3", 13, "knight", 0, "killed", "k2", Char(0, "Perche"), false),
            Hero(SideKey.Defender, "e4", 14, "knight", 1, "killed", "p", Char(0, "Perche", acclaimed: true), false),
            Hero(SideKey.Defender, "e5", 15, "knight", 9, "unspawned", null, Char(0, "Perche"), false),
        };
        var fates = new List<CharacterFate>
        {
            new() { Tag = 3, Fate = Fates.Killed }, new() { Tag = 4, Fate = Fates.Wounded }, new() { Tag = 11, Fate = Fates.Killed },
            new() { Tag = 12, Fate = Fates.Wounded }, new() { Tag = 13, Fate = Fates.Killed }, new() { Tag = 14, Fate = Fates.Killed },
        };
        var awards = AwardBuilder.Build(heroes, fates);
        string Line(CharacterAward a) => $"{a.Tag}:{a.Prestige}/{a.Glory}/{a.ProwessRolls}";
        // k1: wounded lowborn victim 75 x 1 / 2 + 2 x (5 - 1 down - 1 ally) = 43.5 -> 50; glory 10 + round(2 sqrt 3) = 13 -> 15; 1 roll.
        // k2: killed landless noble 150 + 2 x 2 = 154 -> 150; not acclaimed; 1 roll. p: the CK3 player gets no prestige; 2 downs and
        // 10 troop kills -> 3 rolls. e4 (enemy side, acclaimed): killed k2 (150, +25 glory) and died himself (+25). e5 never fought.
        Expect.SequenceEqual(new[] { "2:50/15/1", "3:150/0/1", "1:0/0/3", "14:150/50/1" }, awards.Select(Line));
        Expect.True(awards.Take(3).All(a => a.PlayerSide) && !awards[3].PlayerSide);
        Expect.Equal("Renown: Sir k1 (+50 prestige, +15 glory, a chance at prowess); Sir k2 (+150 prestige, a chance at prowess).", AwardBuilder.RenownLine(awards));

        // Caps: five kings killed is 1000 prestige, 100 glory at most, 3 rolls.
        var champion = new List<AwardHero> { Hero(SideKey.Attacker, "c", 1, "knight", 100, "alive", null, Char(0, "Capet", acclaimed: true), true) };
        var kingFates = new List<CharacterFate>();
        for (int i = 0; i < 5; i++)
        {
            champion.Add(Hero(SideKey.Defender, $"king{i}", 20 + i, "commander", 0, "killed", "c", Char(5, "Royal"), false));
            kingFates.Add(new CharacterFate { Tag = 20 + i, Fate = Fates.Killed });
        }
        Expect.Equal("1:1000/100/3", Line(AwardBuilder.Build(champion, kingFates)[0]));
        Expect.Null(AwardBuilder.RenownLine(new[] { new CharacterAward { Tag = 9, Prestige = 25 } }), "enemy awards are not named");
    }

    private static void KnightsAwardsInOutcome()
    {
        var battle = KnightsBattle();
        var options = new MapperOptions();
        var req = Mapper.BuildRequest(battle, options);
        var result = Synthetic(req, "defender");
        var me = result.Heroes.Single(h => h.Tag == 10);
        me.State = "alive";
        me.KilledBy = null;
        me.Kills = 20;
        var hugh = result.Heroes.Single(h => h.Tag == 11);
        hugh.Kills = 12;
        var odo = result.Heroes.Single(h => h.Tag == 2);
        (odo.State, odo.KilledBy) = ("wounded", hugh.Key);
        var o = Mapper.BuildOutcome(battle, req, result, options);
        var hughAward = o.Awards.Single(a => a.Tag == 11);
        Expect.True(hughAward.PlayerSide && hughAward.Prestige > 0 && hughAward.ProwessRolls == 2, $"{hughAward.Prestige}/{hughAward.ProwessRolls}");
        Expect.True(o.Summary.Split('\n')[1].StartsWith("Renown: Hugh (+", StringComparison.Ordinal), o.Summary);
        Expect.Equal(0, Mapper.BuildOutcome(battle, req, new BattleResult { Id = req.Id, Status = "cancelled" }, options).Awards.Count);
    }

    private static void KnightsCasualties()
    {
        // aggressive_attacker L1: the enemy's fatal share x1.2 (and its troops die more often in Bannerlord); organizer: own pursuit
        // losses x0.9.
        var battle = KnightsBattle();
        Character(battle, 1).CommanderTraits.Add("aggressive_attacker");
        Character(battle, 1).TraitLevels["aggressive_attacker"] = 1;
        Character(battle, 10).CommanderTraits.Add("organizer");
        var knights = PlannerFor(battle);
        Expect.Equal(new SideCasualtyMods(1.2, 0.9), knights.SideCasualtiesFor(SideKey.Defender));
        Expect.Near(1.2, Expect.NotNull(knights.SideBuffsFor(SideKey.Defender)).TroopDeathChanceMult, 1e-6);

        var options = new MapperOptions();
        var plain = KnightsBattle();
        var req = Mapper.BuildRequest(plain, options);
        var result = Synthetic(req, "attacker");
        int baseline = Mapper.BuildOutcome(plain, req, result, options).MyLost;
        var aggressive = KnightsBattle();
        Character(aggressive, 1).CommanderTraits.Add("aggressive_attacker");
        Character(aggressive, 1).TraitLevels["aggressive_attacker"] = 1;
        Expect.True(Mapper.BuildOutcome(aggressive, Mapper.BuildRequest(aggressive, options), result, options).MyLost > baseline, "the defenders lose more men for good");

        // Outrider level 3 on the army's light cavalry: their pursuit x1.4 in the conversion.
        battle = KnightsBattle();
        battle.Attacker.Armies[0].Regiments.Add(new RegimentSnapshot { RegimentId = 16998, MaaKey = "light_horsemen", Current = 100, Max = 100 });
        Character(battle, 2).Accolade = new AccoladeSnapshot { Active = true, Rank = 3, Primary = "outrider_attribute", Levels = { ["outrider_attribute"] = 3 } };
        var mods = PlannerFor(battle).GroupCasualtiesFor(SideKey.Attacker, GroupKey.Maa("light_horsemen", 12, 16998));
        Expect.Near(14, mods.Pursuit(10), 1e-9);
        Expect.Near(10, mods.Screen(10), 1e-9);
        Expect.Near(10, PlannerFor(battle).GroupCasualtiesFor(SideKey.Attacker, GroupKey.Levy(12)).Pursuit(10), 1e-9);

        // The conversion applies the side factors to the shares.
        var groups = new[] { new CasualtyGroup { Id = "g", Soldiers = 1000, Agents = 100, Killed = 40 } };
        var plainConv = CasualtyModel.Convert(Tables.Mapping, groups, Array.Empty<CasualtyGroup>(), new CasualtySettings { WinnerConversion = 0.5 });
        var halved = CasualtyModel.Convert(Tables.Mapping, groups, Array.Empty<CasualtyGroup>(), new CasualtySettings { WinnerConversion = 0.5, WinnerFatalShareMult = 0.5 });
        Expect.Equal(200, plainConv.Winner[0].Fatal);
        Expect.Equal(100, halved.Winner[0].Fatal);
    }

    // ---- presentation flags --------------------------------------------------------------------------------------------

    private static void PresentationInEngineTitle()
    {
        foreach (var (name, build) in Scenarios.All)
            Expect.False(Mapper.BuildRequest(build(), new MapperOptions()).Presentation.InEngineTitle, name);
        Expect.False(Mapper.BuildRequest(KnightsBattle(), new MapperOptions()).Presentation.InEngineTitle);
    }

    private static void PresentationSiegeObjective()
    {
        string Objective(Action<BattleSnapshot> setup)
        {
            var battle = Scenarios.SiegeCastle();
            setup(battle);
            return Mapper.BuildRequest(battle, new MapperOptions()).Presentation.Objective;
        }
        // Angers is besieged; the player leads the attacking Angevins.
        Expect.Equal("Break the siege of Angers", Objective(b => b.Defender.Armies[0].Besieging = true));
        Expect.Equal("Hold the siege lines before Angers", Objective(b => b.Attacker.Armies[0].Besieging = true));
        Expect.Equal("Rout the Host of Brittany", Objective(_ => { }), "no AX|besieging logged (older mod)");
        Expect.Equal("Rout the Host of Brittany", Objective(b => { b.Defender.Armies[0].Besieging = false; b.Attacker.Armies[0].Besieging = false; }));
        // Without a besieged holding the role does not matter.
        Expect.Equal("Rout the Host of Brittany", Objective(b => { b.Location.Siege = false; b.Location.Facts["siege"] = "no"; b.Defender.Armies[0].Besieging = true; }));
    }
}
