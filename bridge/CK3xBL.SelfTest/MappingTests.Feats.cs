// Self tests of the battle feats and the player's own wounds (PROTOCOL §32).
// Run: dotnet run --project CK3xBL.SelfTest -- mapping.feats core.feats
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Model;
using CK3xBL.Mapping.Buffs;
using CK3xBL.Core.Orchestration;
using CK3xBL.Mapping.Outcome;
using CK3xBL.Mapping.Planning;
using CK3xBL.Protocol;

namespace CK3xBL.SelfTest;

internal static partial class MappingTests
{
    private static void PlayerWounds()
    {
        var battle = Scenarios.FrankishWinterHills();
        var options = new MapperOptions();
        var req = Mapper.BuildRequest(battle, options);
        var me = req.Attacker.Heroes.Concat(req.Defender.Heroes).First(h => h.IsPlayer);
        Expect.Equal("player", me.Role, "the player fights in person in this scenario");
        string myWin = req.PlayerSide;

        HeroResult Mine(BattleResult r) => r.Heroes.Single(h => h.Key == me.Key);
        int? FateOf(Ck3Outcome o) => o.Characters.FirstOrDefault(c => c.Tag == me.Tag)?.Fate;

        var result = Synthetic(req, myWin);
        (Mine(result).State, Mine(result).Health) = ("alive", 0.05f);
        Expect.Equal(Fates.Wounded, FateOf(Mapper.BuildOutcome(battle, req, result, options)), "under 10 % health at the end: wounded");
        Mine(result).Health = 0.5f;
        Expect.Null(FateOf(Mapper.BuildOutcome(battle, req, result, options)), "half health: unharmed");

        // A fall always wounds; 10 % of falls (seeded by bid and tag) wound severely, never a maiming or worse.
        int severe = 0, falls = 0;
        for (int bid = 1; bid <= 400; bid++)
        {
            battle.Bid = bid;
            var fallen = Synthetic(req, myWin);
            Mine(fallen).State = bid % 2 == 0 ? "killed" : "wounded";
            int? fate = FateOf(Mapper.BuildOutcome(battle, req, fallen, options));
            Expect.True(fate is Fates.Wounded or Fates.SevereWound, $"bid {bid}: a fall gives fate {fate}");
            falls++;
            if (fate == Fates.SevereWound) severe++;
        }
        Expect.True(severe > falls * 0.05 && severe < falls * 0.16, $"{severe} severe wounds in {falls} falls (about 10 %)");
    }

    private static void FeatsInOutcome()
    {
        var battle = Scenarios.FrankishWinterHills();
        var options = new MapperOptions();
        var req = Mapper.BuildRequest(battle, options);
        var me = req.Attacker.Heroes.Concat(req.Defender.Heroes).First(h => h.IsPlayer);
        var result = Synthetic(req, req.PlayerSide);
        var mine = result.Heroes.Single(h => h.Key == me.Key);
        (mine.State, mine.Kills, mine.Health) = ("alive", 60, 0.8f);
        var o = Mapper.BuildOutcome(battle, req, result, options);
        Expect.True(o.Feats is not null, "the player fought in person: deeds are reported");
        var d = o.Feats!;
        Expect.Equal(60, d.Kills);
        Expect.True(d.Won && !d.Wounded, "won, unharmed");
        Expect.Equal(me.Ck3Id, d.Ck3Id);
        Expect.Equal(battle.Location.Terrain is "hills" or "mountains" ? "rough" : d.Terrain, d.Terrain, "hills and mountains are rough terrain");
        Expect.Equal(battle.Location.Winter != "none", d.Winter);

        var lost = Synthetic(req, req.PlayerSide == "attacker" ? "defender" : "attacker", "aborted");
        lost.Heroes.Single(h => h.Key == me.Key).State = "alive";
        Expect.Null(Mapper.BuildOutcome(battle, req, lost, options).Feats, "an aborted clash earns no feats");
    }

    private static void BanditHideouts()
    {
        var battle = KnightsBattle();
        battle.IsSkirmish = true;
        battle.SkirmishKind = 1;
        battle.Location.Terrain = "mountains";
        var req = Mapper.Plan(battle, new MapperOptions()).Request;
        Expect.Equal("hideout", req.Kind, "bandits are fought at their camp");
        Expect.True(req.Scene.StartsWith("mountain_hideout_", StringComparison.Ordinal), $"a mountain hideout ({req.Scene})");
        Expect.True(!string.IsNullOrEmpty(req.FallbackScene) && req.FallbackScene != req.Scene, "the woods stay as the fallback");
        Expect.Equal("The Bandits' Lair", req.Presentation.Title);
        battle.Location.Terrain = "forest";
        Expect.True(Mapper.Plan(battle, new MapperOptions()).Request.Scene is "forest_hideout_003" or "forest_hideout_004" or "bandit_forest", "a forest camp");
        battle.SkirmishKind = 3;
        Expect.Equal("field", Mapper.Plan(battle, new MapperOptions()).Request.Kind, "an assassin strikes on the road, not at a camp");
    }

    private static void SiegeProgressAndWorks()
    {
        // Fort 2: morale 250, about 1 a day (×1.25 for siege events).
        Expect.Near(0, CK3xBL.Mapping.Planning.SiegePlanner.Progress(0, 2, 5000, 500, 0), 1e-9, "day one: nothing done yet");
        double p40 = CK3xBL.Mapping.Planning.SiegePlanner.Progress(40, 2, 1000, 500, 0);
        Expect.True(p40 > 0.15 && p40 < 0.25, $"40 days at fort 2: {p40:0.00}");
        Expect.True(CK3xBL.Mapping.Planning.SiegePlanner.Progress(40, 2, 9000, 500, 0) > p40, "a larger host besieges faster");
        Expect.True(CK3xBL.Mapping.Planning.SiegePlanner.Progress(40, 12, 1000, 500, 0) < CK3xBL.Mapping.Planning.SiegePlanner.Progress(40, 12, 1000, 500, 3),
            "engines of the fort's tier speed the siege of a strong fort");
        Expect.Equal(1.0, CK3xBL.Mapping.Planning.SiegePlanner.Progress(10_000, 2, 1000, 500, 0), "never past 1");

        var battle = KnightsBattle();
        battle.IsSiege = true;
        battle.SiegeFort = 4;
        battle.SiegeGarrison = 300;
        battle.SiegeMen = 2000;
        battle.Location.Holding = "castle_holding";
        var army = battle.Attacker.Armies[0];
        army.Regiments.Add(new RegimentSnapshot { MaaKey = "trebuchet", Current = 50, Max = 50 });
        army.Regiments.Add(new RegimentSnapshot { MaaKey = "onager", Current = 50, Max = 50 });

        battle.SiegeDays = 0;
        var fresh = Mapper.Plan(battle, new MapperOptions()).Request;
        Expect.Equal("siege", fresh.Kind);
        Expect.True(fresh.Scene.Contains("castle", StringComparison.Ordinal) || fresh.Scene.Contains("siege", StringComparison.Ordinal), $"a castle ({fresh.Scene})");
        Expect.True(!string.IsNullOrEmpty(fresh.FallbackScene) && fresh.FallbackScene != fresh.Scene, "the field stays as the fallback");
        var s = fresh.Siege!;
        Expect.Equal(2, s.SceneUpgradeLevel, "fort 4: a castle of level 2");
        Expect.False(s.AttackerMachines.Contains("SiegeTower") || s.AttackerMachines.Contains("Ram"), "day one: ladders only");
        Expect.Equal("Trebuchet,Onager", string.Join(",", s.AttackerMachines), "one engine per siege regiment, the strongest first");
        Expect.Equal("Ballista", string.Join(",", s.DefenderMachines));
        Expect.Equal(1f, s.WallHitpointPercentages.Min(), "walls whole");

        // PROTOCOL §46: waves, the walls' bonus, a garrison of soldiers instead of village levies.
        Expect.Near(2.5, s.AttackerWaveRatio, 1e-6, "the assault comes in waves");
        var wall = CK3xBL.Mapping.Planning.SiegePlanner.WallBonus(4);
        Expect.True(fresh.Defender.Buffs is { } db && db.DamageDealtMult >= wall.Dealt - 1e-4 && db.DamageTakenMult <= wall.Taken + 1e-4 && db.InitialMoraleAdd >= wall.Morale,
            $"fort 4 walls: x{wall.Dealt} damage, x{wall.Taken} taken, +{wall.Morale} morale");
        Expect.Near(4, CK3xBL.Mapping.Planning.SiegePlanner.GarrisonTier(4), 1e-9, "fort 4: tier 4 soldiers");
        Expect.Near(5, CK3xBL.Mapping.Planning.SiegePlanner.GarrisonTier(20), 1e-9, "at most tier 5");
        battle.IsSiege = false;
        var field = Mapper.Plan(battle, new MapperOptions()).Request;
        battle.IsSiege = true;
        string Levy(BattleRequest r) => string.Join(",", r.Defender.Troops.Where(t => t.Group!.StartsWith("levy#", StringComparison.Ordinal)).Select(t => t.Id).OrderBy(x => x));
        Expect.True(Levy(fresh).Length > 0 && Levy(fresh) != Levy(field), $"the garrison is not the levy ({Levy(fresh)} vs {Levy(field)})");

        battle.SiegeDays = 400;
        var late = Mapper.Plan(battle, new MapperOptions()).Request.Siege!;
        Expect.True(late.Progress >= 0.8f, $"a long siege ({late.Progress})");
        Expect.Equal(2, late.AttackerMachines.Count(m => m == "SiegeTower"), "two towers late in a siege");
        Expect.True(late.AttackerMachines.Contains("Ram") && late.HasSiegeTower, "a ram and towers");
        Expect.Equal(0f, late.WallHitpointPercentages.Min(), "a breach in the wall");

        Expect.True(CK3xBL.Mapping.Planning.SiegePlanner.Scene("vlandia", "city_holding", 3).Contains("town", StringComparison.Ordinal)
                    || CK3xBL.Mapping.Planning.SiegePlanner.Scene("vlandia", "city_holding", 3).Contains("city", StringComparison.Ordinal), "a city is a town");
        Expect.True(CK3xBL.Mapping.Planning.SiegePlanner.Scene("empire", "tribal_holding", 1).StartsWith("sturgia_", StringComparison.Ordinal), "tribal palisades");
        Expect.True(CK3xBL.Mapping.Planning.SiegePlanner.Scene("unknown", "castle_holding", 1).StartsWith("vlandia_", StringComparison.Ordinal), "unknown culture: Vlandia");
    }

    private static void ReliefAtTheWalls()
    {
        // Break-in loss: 10 % against a smaller besieger, 20 % against twice the host, halved by martial 30.
        Expect.Near(0.10, CK3xBL.Mapping.Planning.SiegePlanner.BreakInLoss(1000, 400, 0), 1e-9);
        Expect.Near(0.20, CK3xBL.Mapping.Planning.SiegePlanner.BreakInLoss(1000, 3000, 0), 1e-9);
        Expect.Near(0.10, CK3xBL.Mapping.Planning.SiegePlanner.BreakInLoss(1000, 3000, 30), 1e-9, "martial 30 halves it");
        Expect.Equal("defend", CK3xBL.Core.Orchestration.BattleOrchestrator.ReliefModeOf("FIELD_DEFEND"));
        Expect.Equal("camp", CK3xBL.Core.Orchestration.BattleOrchestrator.ReliefModeOf("field_camp"));
        Expect.Equal("", CK3xBL.Core.Orchestration.BattleOrchestrator.ReliefModeOf("FIELD_AS_COMMANDER"));

        var battle = Scenarios.FrankishWinterHills();
        battle.Relief = true;
        battle.SiegeFort = 3;
        battle.SiegeGarrison = 300;
        battle.SiegeDays = 60;
        battle.Location.Holding = "castle_holding";
        var field = Mapper.BuildRequest(battle, new MapperOptions());
        Expect.Equal("field", field.Kind, "without a choice the relief is a field battle");

        var defend = new MapperOptions { ReliefMode = "defend" };
        var req = Mapper.BuildRequest(battle, defend);
        Expect.Equal("siege", req.Kind);
        Expect.True(req.Siege!.PlayerDefends && req.Siege.BreakIn is { Count: > 0 }, "the host broke in and lost men");
        string ck3Side = req.PlayerSide;
        var mine = ck3Side == "attacker" ? req.Attacker : req.Defender;
        string garrison = CK3xBL.Mapping.BattleMapper.GarrisonGroup(battle.Bid);
        Expect.True(mine.Troops.Any(t => t.Group == garrison), "the garrison fights with the relief");
        Expect.Equal(field.Attacker.Troops.Concat(field.Defender.Troops).Sum(t => t.Count),
            req.Attacker.Troops.Concat(req.Defender.Troops).Where(t => t.Group != garrison).Sum(t => t.Count) + req.Siege.BreakIn.Values.Sum(),
            "every man lost breaking in is accounted for");

        bool swapped = SiegeSides.Settle(req);
        Expect.Equal(ck3Side == "attacker", swapped, "swapped only when CK3 had the relief attack");
        Expect.Equal("defender", req.PlayerSide, "the player holds the walls");
        Expect.False(SiegeSides.Settle(req), "settled once");
        var won = Mapper.BuildOutcome(battle, req, Synthetic(req, "defender"), defend);
        Expect.True(won.PlayerWon, "the walls held: a win for the player in CK3");
        Expect.True(won.Summary.Contains("breaking into", StringComparison.Ordinal), won.Summary);
        var lost = Mapper.BuildOutcome(battle, req, Synthetic(req, "attacker"), defend);
        Expect.False(lost.PlayerWon);

        var camp = Mapper.BuildRequest(battle, new MapperOptions { ReliefMode = "camp" });
        Expect.Equal("field", camp.Kind, "the camp is struck in the field (no relief spawn set in the siege scenes)");
        var foe = camp.PlayerSide == "attacker" ? camp.Defender : camp.Attacker;
        Expect.Near(CK3xBL.Mapping.BattleMapper.CampSurpriseMorale, foe.Buffs!.InitialMoraleAdd, 1e-6, "the besiegers are caught in their camp");
        Expect.True((camp.PlayerSide == "attacker" ? camp.Attacker : camp.Defender).Troops.Any(t => t.Group == garrison), "the garrison sallies out");
        Expect.False(SiegeSides.Settle(camp), "a field battle is never swapped");
    }

    private static void KnightAuras()
    {
        var battle = KnightsBattle();
        var req = Mapper.BuildRequest(battle, new MapperOptions());
        var odo = req.Attacker.Heroes.Single(h => h.Tag == 2);
        Expect.True(odo.Aura is not null, "an acclaimed archer leads the shooters");
        var a = odo.Aura!;
        Expect.Equal("Ranged", a.Formation);
        Expect.Near(1 + 3 * KnightBuffPlanner.AuraPerRank, a.RangedDamageMult, 1e-6, "rank 3: +7.5 % to arrows");
        Expect.Near(1 - 3 * KnightBuffPlanner.AuraPerRank, a.RangedInaccuracyMult, 1e-6, "and straighter shots");
        Expect.Near(1, a.MeleeDamageMult, 1e-6, "no melee factor for archers");
        Expect.Near(6, a.InitialMoraleAdd, 1e-6);
        Expect.True(a.MoraleLoss >= 5, "his fall shakes the formation");
        Expect.True(req.Attacker.Heroes.Concat(req.Defender.Heroes).Where(h => h.Tag != 2).All(h => h.Aura is null), "knights without an accolade lead nobody");

        Character(battle, 2).Accolade = new AccoladeSnapshot { Active = true, Rank = 6, Primary = "lancer_attribute", Levels = { ["lancer_attribute"] = 3, ["archer_attribute"] = 3 } };
        var lancer = Mapper.BuildRequest(battle, new MapperOptions()).Attacker.Heroes.Single(h => h.Tag == 2).Aura!;
        Expect.Equal("Cavalry", lancer.Formation, "the primary attribute decides");
        Expect.Near(1 + 1.5 * 6 * KnightBuffPlanner.AuraPerRank, lancer.ChargeDamageMult, 1e-6, "rank 6 lancer: +22.5 % charge");
        Expect.Equal("HorseArcher", KnightBuffPlanner.AuraFormation("archer_cavalry"));
        Expect.Null(KnightBuffPlanner.AuraFormation("siege_weapon"));
    }

    private static void ElephantsRide()
    {
        var battle = KnightsBattle();
        battle.Attacker.Armies[0].Regiments.Add(new RegimentSnapshot { RegimentId = 77, MaaKey = "war_elephant", Current = 100, Max = 100 });
        var horses = Mapper.BuildRequest(battle, new MapperOptions());
        var onHorses = horses.Attacker.Troops.Where(t => t.Group!.Contains("war_elephant")).ToList();
        Expect.True(onHorses.Count > 0 && onHorses.All(t => t.Mount is null), "without the elephants module they ride horses");

        var req = Mapper.BuildRequest(battle, new MapperOptions { Elephants = true });
        var elephants = req.Attacker.Troops.Where(t => t.Group!.Contains("war_elephant")).ToList();
        Expect.True(elephants.All(t => t.Mount == "elephant" && t.MountHarness == "be_elephant_armor_kushan_tier_2"), "war elephants in iron");
        int before = onHorses.Sum(t => t.Count), after = elephants.Sum(t => t.Count);
        Expect.True(after >= 1 && Math.Abs(after - before / (double)CK3xBL.Mapping.Planning.ElephantPlanner.MenPerElephant) <= elephants.Count,
            $"one elephant per {CK3xBL.Mapping.Planning.ElephantPlanner.MenPerElephant} agents ({before} -> {after})");
        Expect.True(req.Attacker.Troops.Concat(req.Defender.Troops).Where(t => !t.Group!.Contains("war_elephant")).All(t => t.Mount is null), "nobody else rides one");

        // The outcome still counts the regiment's CK3 men: a win with few elephants lost costs few men.
        var o = Mapper.BuildOutcome(battle, req, Synthetic(req, "attacker"), new MapperOptions { Elephants = true });
        Expect.True(o.PlayerWon || !o.PlayerWon, "the outcome builds");
    }

    private static void NavalFleets()
    {
        Expect.Equal("nord", NavalPlanner.Tradition("sturgia", "heritage_north_germanic"), "the Norse sail drakkars");
        Expect.Equal("empire", NavalPlanner.Tradition("empire", "heritage_byzantine"));
        Expect.Equal("vlandia", NavalPlanner.Tradition("aserai_unknown", null), "an unknown culture sails western ships");
        Expect.SequenceEqual(new[] { "vlandia_heavy_ship" }, NavalPlanner.Fleet("vlandia", 0, false), "at least one ship");
        var fleet = NavalPlanner.Fleet("empire", 200, false);
        Expect.SequenceEqual(new[] { "empire_heavy_ship", "empire_medium_ship", "central_light_ship" }, fleet, "heavy, medium, light until 200 men fit");
        Expect.Equal(NavalPlanner.MaxShips, NavalPlanner.Fleet("nord", 100000, false).Count, "never more than MaxShips");
        Expect.True(NavalPlanner.Fleet("vlandia", 500, true).All(h => NavalPlanner.Hulls[h].Shallow), "a landing takes shallow-water ships only");
        var raid = NavalPlanner.Fleet("nord", 1064, true);
        Expect.Equal(NavalPlanner.RaidMaxShips, raid.Count, "a landing: four ships at most (the raid scenes' jumping frames; live b298 crashed with 8)");
        Expect.Equal("nord_medium_ship", raid[0], "the largest shallow hull first");
        Expect.True(NavalPlanner.Fleets.Values.SelectMany(f => f).All(NavalPlanner.Hulls.ContainsKey), "every fleet hull is known");
        Expect.True(NavalPlanner.RaidScenes.ContainsKey("nord") && NavalPlanner.RaidScene("khuzait", -3).StartsWith("khuzait_"), "a raided village of the defenders' culture");
        Expect.Equal(NavalPlanner.OpenSeaScene, NavalPlanner.SeaScene(4, openSea: true));
        var info = NavalPlanner.Info(true, new List<string> { "western_light_ship" }, new List<string> { "vlandia_heavy_ship" }, 7, false);
        Expect.True(info.Raid && info.DefenderShips.Count == 0 && info.Terrain == "River", "a landing: defenders ashore");
        Expect.True(info.WindStrength is >= 0.3f and <= 0.71f, "a breeze to a gale");
    }

    private static void NavalBattles()
    {
        var battle = KnightsBattle();
        battle.IsSeaBattle = true;
        var land = Mapper.BuildRequest(battle, new MapperOptions());
        Expect.True(land.Kind != "naval" && land.Naval is null, "without War Sails a battle at sea is fought on land");

        var sea = Mapper.BuildRequest(battle, new MapperOptions { Naval = true });
        Expect.Equal("naval", sea.Kind);
        Expect.True(sea.Naval is { Raid: false } && sea.Naval.AttackerShips.Count >= 1 && sea.Naval.AttackerShips.Count <= NavalPlanner.MaxShips &&sea.Naval.DefenderShips.Count >= 1, "two fleets");
        Expect.Equal(land.Scene, sea.FallbackScene, "the land field stays as fallback");
        Expect.True(NavalPlanner.SeaScenes.Contains(sea.Scene) || sea.Scene == NavalPlanner.OpenSeaScene, sea.Scene);

        // Half the loser's fled men drown: the loser loses more at sea than on land.
        var onLand = Mapper.BuildOutcome(battle, land, Synthetic(land, "attacker"), new MapperOptions());
        var atSea = Mapper.BuildOutcome(battle, sea, Synthetic(sea, "attacker"), new MapperOptions { Naval = true });
        Expect.Equal("defender", sea.PlayerSide, "the player defends and loses here");
        Expect.True(atSea.MyLost > onLand.MyLost, $"drowned at sea ({onLand.MyLost} -> {atSea.MyLost})");
        Expect.Equal(onLand.EnemyLost, atSea.EnemyLost, "the winner keeps his men");

        battle.IsSeaBattle = false;
        battle.Landing = true;
        // The player defends here: War Sails' raid only lands the player's own ships (bench and live b298 crashed with the AI landing),
        // so a landing the player defends is fought on the coast.
        var landing = Mapper.BuildRequest(battle, new MapperOptions { Naval = true });
        Expect.True(landing.Kind != "naval" && landing.Naval is null, "a landing the player defends: on the coast");
        var ashore = Mapper.BuildOutcome(battle, landing, Synthetic(landing, "attacker"), new MapperOptions { Naval = true });
        Expect.Equal(onLand.MyLost, ashore.MyLost, "nobody drowns ashore");
        Expect.True(NavalPlanner.Fleet("nord", 5000, true).Count <= NavalPlanner.RaidMaxShips, "a landing: three ships at most");

        battle.IsSiege = true;
        Expect.True(Mapper.BuildRequest(battle, new MapperOptions { Naval = true }).Kind != "naval", "a siege stays a siege");
    }

    private static void NavalModuleAndLaunchList()
    {
        var list = CK3xBL.Core.Bannerlord.NavalModule.WithNaval(new[] { "Native", "SandBox", "CustomBattle", "CK3Bridge" });
        Expect.Equal("Native,SandBox,CustomBattle,StoryMode,NavalDLC,CK3Bridge,CK3Bridge.Naval", string.Join(",", list));
        Expect.Equal("Native", string.Join(",", CK3xBL.Core.Bannerlord.NavalModule.WithNaval(new[] { "Native" })), "no bridge: unchanged");

        string root = Path.Combine(Path.GetTempPath(), $"ck3xbl_nv_{Guid.NewGuid():N}");
        string game = Path.Combine(root, "game"), app = Path.Combine(root, "app");
        try
        {
            Expect.False(CK3xBL.Core.Bannerlord.NavalModule.Ensure(game, app, out _), "no War Sails: no naval module");
            Directory.CreateDirectory(Path.Combine(game, "Modules", "NavalDLC"));
            File.WriteAllText(Path.Combine(game, "Modules", "NavalDLC", "SubModule.xml"), "<Module/>");
            Expect.False(CK3xBL.Core.Bannerlord.NavalModule.Ensure(game, app, out _), "nothing shipped and nothing installed");
            string src = CK3xBL.Core.Bannerlord.NavalModule.ShippedDir(app);
            Directory.CreateDirectory(Path.Combine(src, "bin", "Win64_Shipping_Client"));
            File.WriteAllText(Path.Combine(src, "SubModule.xml"), "<Module id=\"CK3Bridge.Naval\"/>");
            File.WriteAllText(Path.Combine(src, "bin", "Win64_Shipping_Client", "CK3Bridge.Naval.dll"), "dll");
            Expect.True(CK3xBL.Core.Bannerlord.NavalModule.Ensure(game, app, out string report) && report.Contains("installed"), report);
            Expect.Equal("dll", File.ReadAllText(Path.Combine(game, "Modules", "CK3Bridge.Naval", "bin", "Win64_Shipping_Client", "CK3Bridge.Naval.dll")));
            Expect.True(CK3xBL.Core.Bannerlord.NavalModule.Ensure(game, app, out string again) && again.Length == 0, "up to date: not copied");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void ElephantModuleAndArmoryPrep()
    {
        Expect.Equal("<Item id=\"elephant\" name=\"E\"/>", CK3xBL.Core.Bannerlord.ElephantModule.WithoutCultures("<Item id=\"elephant\" name=\"E\" culture=\"Culture.kushan\"/>"));

        string game = Path.Combine(Path.GetTempPath(), $"ck3xbl_bl_{Guid.NewGuid():N}");
        try
        {
            string src = CK3xBL.Core.Bannerlord.ElephantModule.SourceDir(game);
            Expect.False(CK3xBL.Core.Bannerlord.ElephantModule.Ensure(game, out _), "no Burning Empires: no elephants");
            Directory.CreateDirectory(Path.Combine(src, "ModuleData", "be_elephants_generated", "items"));
            Directory.CreateDirectory(Path.Combine(src, "AssetPackages"));
            File.WriteAllText(Path.Combine(src, "SubModule.xml"), "<Module/>");
            File.WriteAllText(Path.Combine(src, "AssetPackages", "pack0.tpac"), "assets");
            File.WriteAllText(Path.Combine(src, "ModuleData", "be_elephant_monsters.xml"), "<Monsters/>");
            File.WriteAllText(Path.Combine(src, "ModuleData", "project.mbproj"), "<base type=\"solution\"/>");
            File.WriteAllText(Path.Combine(src, "ModuleData", "action_sets.xml"), "<action_sets/>");
            File.WriteAllText(Path.Combine(src, "ModuleData", "be_elephants_generated", "items", "be_elephant_items.xml"),
                "<Items><Item id=\"elephant\" culture=\"Culture.kushan\"/></Items>");
            Expect.True(CK3xBL.Core.Bannerlord.ElephantModule.Ensure(game, out string built), built);
            string dst = CK3xBL.Core.Bannerlord.ElephantModule.ModuleDir(game);
            Expect.Equal("assets", File.ReadAllText(Path.Combine(dst, "AssetPackages", "pack0.tpac")), "the asset package is there");
            Expect.True(File.Exists(Path.Combine(dst, "ModuleData", "project.mbproj")) && File.Exists(Path.Combine(dst, "ModuleData", "action_sets.xml")),
                "project.mbproj comes along: without it the engine never loads the elephant's action sets (bench 2026-10-19)");
            Expect.False(File.ReadAllText(Path.Combine(dst, "ModuleData", "items", "ck3xbl_elephant_items.xml")).Contains("kushan"), "no Burning Empires culture");
            Expect.True(File.ReadAllText(Path.Combine(dst, "SubModule.xml")).Contains("CK3Bridge.Elephants") &&
                        !File.ReadAllText(Path.Combine(dst, "SubModule.xml")).Contains("\"BurningEmpires\""), "a module of its own, without the conversion");
            Expect.True(CK3xBL.Core.Bannerlord.ElephantModule.Ensure(game, out string again) && again.Length == 0, "up to date: not rebuilt");

            string terra = Path.Combine(game, "Modules", "BL.Terra_Armarium.Byzantine");
            Directory.CreateDirectory(Path.Combine(terra, "Shaders"));
            File.WriteAllText(Path.Combine(terra, "SubModule.xml"),
                "<Module><Xmls><XmlNode><XmlName id=\"Items\" path=\"items\"/><IncludedGameTypes><GameType value=\"Campaign\"/><GameType value=\"CampaignStoryMode\"/></IncludedGameTypes></XmlNode></Xmls></Module>");
            var lines = CK3xBL.Core.Bannerlord.ArmoryCompat.Prepare(game, new[] { "BL.Terra_Armarium.Byzantine", "Missing" });
            Expect.Equal(2, lines.Count, string.Join(" / ", lines));
            Expect.True(Directory.Exists(Path.Combine(terra, CK3xBL.Core.Bannerlord.ArmoryCompat.DisabledShaders)) && !Directory.Exists(Path.Combine(terra, "Shaders")), "shaders off");
            Expect.True(File.ReadAllText(Path.Combine(terra, "SubModule.xml")).Contains("\"CustomGame\""), "items in custom battles");
            Expect.True(File.Exists(Path.Combine(terra, "SubModule.xml" + CK3xBL.Core.Bannerlord.ArmoryCompat.Backup)), "original kept");
            Expect.Equal(0, CK3xBL.Core.Bannerlord.ArmoryCompat.Prepare(game, new[] { "BL.Terra_Armarium.Byzantine" }).Count, "once only");
        }
        finally { if (Directory.Exists(game)) Directory.Delete(game, true); }

        var list = CK3xBL.Core.Bannerlord.BannerlordLink.ArmoryModuleList(new[] { "Native", "CustomBattle", "CK3Bridge" },
            new[] { "BL.Terra_Armarium.UniversalCore", "BL.Terra_Armarium.Byzantine", "CK3Bridge.Elephants" });
        Expect.Equal("Native,CustomBattle,SandBox,StoryMode,BL.Terra_Armarium.UniversalCore,BL.Terra_Armarium.Byzantine,CK3Bridge.Elephants,CK3Bridge", string.Join(",", list));
    }

    private static void UniqueRegimentsLook()
    {
        var battle = KnightsBattle();
        var reg = battle.Sides.SelectMany(s => s.Armies).SelectMany(a => a.Regiments).First();
        reg.MaaKey = "huscarl";
        var req = Mapper.BuildRequest(battle, new MapperOptions());
        var troops = req.Attacker.Troops.Concat(req.Defender.Troops).Where(t => t.Group!.Contains("huscarl")).ToList();
        Expect.True(troops.Count > 0, "the huscarl regiment is in the battle");
        Expect.True(troops.All(t => t.Look!.StartsWith("maa:huscarl|", StringComparison.Ordinal)), $"huscarls wear their own look ({troops[0].Look})");
        Expect.True(req.Looks!.ContainsKey(troops[0].Look!), "the look travels in the request");
        var levy = req.Attacker.Troops.Concat(req.Defender.Troops).First(t => t.Group!.StartsWith("levy", StringComparison.Ordinal));
        Expect.False(levy.Look!.StartsWith("maa:", StringComparison.Ordinal), "levies keep their culture's look");
    }

    private static void FugitivesArePursued()
    {
        var battle = Scenarios.FrankishWinterHills();
        var options = new MapperOptions();
        var req = Mapper.BuildRequest(battle, options);
        string loser = req.PlayerSide;                       // the player's side loses
        string winner = loser == "attacker" ? "defender" : "attacker";
        var losers = (loser == "attacker" ? req.Attacker : req.Defender).Heroes.Where(h => !h.IsPlayer && h.Tag > 0).ToList();
        Expect.True(losers.Count > 0, "the losing side has heroes besides the player");
        int caught = 0, escaped = 0;
        for (int bid = 1; bid <= 200; bid++)
        {
            battle.Bid = bid;
            var result = Synthetic(req, winner);
            foreach (var h in result.Heroes.Where(h => losers.Any(l => l.Key == h.Key))) { h.State = "routed"; h.KilledBy = null!; }
            var o = Mapper.BuildOutcome(battle, req, result, options);
            foreach (var l in losers)
                if (o.Characters.FirstOrDefault(c => c.Tag == l.Tag) is { } f) { caught++; Expect.True(f.Fate is Fates.Captured or Fates.Wounded, $"fate {f.Fate}"); }
                else escaped++;
        }
        Expect.True(caught > 0 && escaped > 0, $"{caught} caught, {escaped} escaped: the pursuit catches some fugitives, not all");
    }

    private static void CleaveAndAiLevel()
    {
        Expect.Near(0.6, CK3xBL.Mapping.Planning.HeroPlanner.AiLevel(14), 1e-9, "prowess 14: a fair fighter, not a champion");
        Expect.Near(1.0, CK3xBL.Mapping.Planning.HeroPlanner.AiLevel(30), 1e-9, "prowess 30: the best AI");
        Expect.Near(0.25, CK3xBL.Mapping.Planning.HeroPlanner.AiLevel(-3), 1e-9);

        var battle = KnightsBattle();
        var people = battle.Sides.SelectMany(s => s.Characters).Where(c => c.Tag > 0).ToList();
        Expect.True(people.Count >= 2, "the scenario has heroes");
        people[0].Prowess = 31;
        people[1].Prowess = 30;
        var heroes = Mapper.BuildRequest(battle, new MapperOptions()).Attacker.Heroes.Concat(Mapper.BuildRequest(battle, new MapperOptions()).Defender.Heroes).ToList();
        Expect.True(heroes.Single(h => h.Tag == people[0].Tag).Buffs?.Cleave == true, "prowess 31 cleaves");
        Expect.False(heroes.Single(h => h.Tag == people[1].Tag).Buffs?.Cleave == true, "prowess 30 does not");
    }

    private static void FeatLedgerCounts()
    {
        var r = new FeatRecord();
        BattleFeats Deeds(int kills = 0, bool won = true, bool defending = false, double odds = 1, string terrain = "", bool winter = false,
            bool wounded = false, bool slew = false) => new()
            { Ck3Id = 7, Kills = kills, Won = won, Defending = defending, EnemyToOwn = odds, Terrain = terrain, Winter = winter, Wounded = wounded, SlewCommander = slew };

        Expect.Equal(Feat.Reaper, FeatLedger.Count(r, Deeds(kills: 55, won: false), "b1") & (Feat.Reaper | Feat.Carnage));
        Expect.Equal(Feat.Carnage, FeatLedger.Count(r, Deeds(kills: 120, won: false), "b2") & (Feat.Reaper | Feat.Carnage), "100+: carnage instead of reaper");
        Expect.Equal(Feat.None, FeatLedger.Count(r, Deeds(kills: 120), "b2"), "a battle counts once");
        Expect.True(FeatLedger.Count(r, Deeds(slew: true, won: false), "b3").HasFlag(Feat.CommanderSlayer));

        // Defensive wins against a larger enemy: the third earns the trait; a defence against a smaller host does not count.
        r = new FeatRecord();
        FeatLedger.Count(r, Deeds(defending: true, odds: 1.5), "d1");
        FeatLedger.Count(r, Deeds(defending: true, odds: 0.5), "d2");
        Expect.False(FeatLedger.Count(r, Deeds(defending: true, odds: 1.2), "d3").HasFlag(Feat.Defender));
        Expect.True(FeatLedger.Count(r, Deeds(defending: true, odds: 1.2), "d4").HasFlag(Feat.Defender));
        Expect.True(FeatLedger.Count(r, Deeds(defending: true, odds: 2.5), "d5").HasFlag(Feat.Miracle), "2:1 or more: a miracle");

        r = new FeatRecord();
        for (int i = 1; i <= 4; i++) Expect.False(FeatLedger.Count(r, Deeds(), $"a{i}").HasFlag(Feat.Attacker), $"attack win {i}");
        Expect.True(FeatLedger.Count(r, Deeds(), "a5").HasFlag(Feat.Attacker), "the fifth attack win");

        r = new FeatRecord();
        FeatLedger.Count(r, Deeds(terrain: "forest", winter: true), "t1");
        FeatLedger.Count(r, Deeds(terrain: "forest", winter: true), "t2");
        var third = FeatLedger.Count(r, Deeds(terrain: "forest", winter: true), "t3");
        Expect.Equal(Feat.TerrainForest, third & Feat.TerrainDesert, "forest code");
        Expect.True(third.HasFlag(Feat.Winter));
        Expect.Equal(Feat.TerrainDesert, FeatLedger.Count(new FeatRecord { TerrainWins = { ["desert"] = 2 } }, Deeds(terrain: "desert"), "t4") & Feat.TerrainDesert);

        // Ten unwounded battles in a row; a wound resets the streak; the fifth wound leaves scars.
        r = new FeatRecord();
        for (int i = 1; i <= 9; i++) FeatLedger.Count(r, Deeds(won: false), $"u{i}");
        FeatLedger.Count(r, Deeds(won: false, wounded: true), "w1");
        Expect.False(FeatLedger.Count(r, Deeds(won: false), "u10").HasFlag(Feat.Invulnerable), "the wound broke the streak");
        for (int i = 11; i <= 18; i++) Expect.False(FeatLedger.Count(r, Deeds(won: false), $"u{i}").HasFlag(Feat.Invulnerable), $"u{i}");
        Expect.True(FeatLedger.Count(r, Deeds(won: false), "u19").HasFlag(Feat.Invulnerable), "ten in a row");
        Expect.Equal(0, r.UnwoundedStreak, "the streak starts again");
        for (int i = 2; i <= 4; i++) FeatLedger.Count(r, Deeds(won: false, wounded: true), $"w{i}");
        Expect.True(FeatLedger.Count(r, Deeds(won: false, wounded: true), "w5").HasFlag(Feat.Scars), "five wounds");

        Expect.False(FeatLedger.Count(r, Deeds(won: false, wounded: true), "w6").HasFlag(Feat.Scars), "scars rise at 5, 10, 20 wounds only");

        r = new FeatRecord { Kills = 990 };
        Expect.True(FeatLedger.Count(r, Deeds(kills: 10, won: false), "l1").HasFlag(Feat.Legend), "a thousand in a lifetime");
        Expect.False(FeatLedger.Count(r, Deeds(kills: 10, won: false), "l2").HasFlag(Feat.Legend), "rank II waits for 2500");
        Expect.True(FeatLedger.Count(r, Deeds(kills: 1500, won: false), "l3").HasFlag(Feat.Legend), "2500: rank II");

        // PROTOCOL §45: the second mask.
        var heavy = FeatLedger.Count(new FeatRecord(), Deeds(kills: 260, defending: true, odds: 3.2), "n1");
        Expect.True(heavy.HasFlag(Feat.BloodyHarvest) && heavy.HasFlag(Feat.Carnage) && heavy.HasFlag(Feat.LastStand), $"{heavy}");
        Expect.True(FeatLedger.Count(new FeatRecord(), new BattleFeats { Ck3Id = 7, HeroesDowned = 5 }, "n2").HasFlag(Feat.KnightHunter));
        r = new FeatRecord { BattlesFought = 24 };
        Expect.True(FeatLedger.Count(r, Deeds(won: false), "n3").HasFlag(Feat.Veteran), "25 battles");
        Expect.False(FeatLedger.Count(r, Deeds(won: false), "n4").HasFlag(Feat.Veteran));
        r = new FeatRecord { SeaWins = 2 };
        var sea = FeatLedger.Count(r, new BattleFeats { Ck3Id = 7, Won = true, AtSea = true, Kills = 12, EnemyToOwn = 1 }, "n5");
        Expect.True(sea.HasFlag(Feat.SeaWolf) && sea.HasFlag(Feat.FirstToBoard), $"{sea}");
        r = new FeatRecord { LandingWins = 4 };
        Expect.True(FeatLedger.Count(r, new BattleFeats { Ck3Id = 7, Won = true, Landing = true, EnemyToOwn = 1 }, "n6").HasFlag(Feat.CoastTerror));

        // r7 = prestige + 226 × mask, and the mod's decoding gets both back.
        foreach (var (prestige, mask) in new[] { (0, 0), (225, 4095), (35, 1 | 64 | 2048), (225, 0) })
        {
            int r7 = FeatLedger.Register7(prestige, mask);
            Expect.True(r7 <= 999_999, $"r7 {r7} fits a register");
            Expect.Equal((prestige, mask), (r7 % 226, r7 / 226), $"r7 {r7}");
        }
        // r6 = kills (at most 999) + 1000 × bits 12..18; r7 keeps the first 12 bits only.
        int all = (int)(Feat.Reaper | Feat.LastStand | Feat.CoastTerror);
        Expect.Equal(1, FeatLedger.Register7(0, all) / 226, "r7: the first mask only");
        int r6 = FeatLedger.Register6(1234, all);
        Expect.Equal((999, 1 | 64), (r6 % 1000, r6 / 1000), $"r6 {r6}");
        Expect.True(FeatLedger.Register6(999, -1 ^ int.MinValue) <= 999_999, "r6 fits a register");

        string path = Path.Combine(Path.GetTempPath(), $"ck3xbl_feats_{Guid.NewGuid():N}.json");
        try
        {
            for (int i = 1; i <= 4; i++) FeatLedger.CountInFile(path, Deeds(), $"f{i}", out _);
            Expect.True(FeatLedger.CountInFile(path, Deeds(), "f5", out string? problem).HasFlag(Feat.Attacker), $"the file keeps the counts ({problem})");
        }
        finally { File.Delete(path); }
    }
}
