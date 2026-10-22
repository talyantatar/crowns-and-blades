// Self tests of the costumes (PROTOCOL §21): look keys per group, the looks in the request, era and role rules, the switch,
// and the armory modules in Bannerlord's launch list.
// Run: dotnet run --project CK3xBL.SelfTest -- mapping.costumes core.bannerlord.armory
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Bannerlord;
using CK3xBL.Mapping.Data;
using CK3xBL.Mapping.Planning;

namespace CK3xBL.SelfTest;

internal static partial class MappingTests
{
    private static void CostumesInRequest()
    {
        var tables = MappingTables.Get(null);
        Expect.True(tables.Costumes is not null, "ck3xbl_costumes.json is loaded");
        var req = Mapper.BuildRequest(KnightsBattle(), new MapperOptions());
        var troops = req.Attacker.Troops.Concat(req.Defender.Troops).ToList();
        Expect.True(troops.Count > 0, "the battle has troops");
        Expect.True(req.Looks is { Count: > 0 }, "the request carries looks");
        foreach (var t in troops)
        {
            Expect.True(t.Look is not null && req.Looks!.ContainsKey(t.Look), $"{t.Group} ({t.Id}) has a look of the request");
            Expect.True(t.Look!.StartsWith("western_feudal|2|", StringComparison.Ordinal), $"{t.Group}: French in the high medieval era, got {t.Look}");
        }
        Expect.True(troops.Any(t => t.Group!.StartsWith("levy", StringComparison.Ordinal) && t.Look!.EndsWith("|levy", StringComparison.Ordinal)), "levies dress as levies");
        Expect.True(troops.Any(t => t.Group!.Contains("bowmen") && t.Look!.EndsWith("|rng", StringComparison.Ordinal)), "bowmen dress as archers");
        foreach (var (key, look) in req.Looks!)
        {
            Expect.True(look.Slots.ContainsKey("Head") && look.Slots.ContainsKey("Body"), $"{key} dresses head and body");
            foreach (var (slot, list) in look.Slots)
                foreach (var item in list)
                {
                    var c = tables.CostumeItems[item.Id];
                    Expect.Equal(slot, c.Slot, $"{item.Id} sits in its own slot");
                    Expect.True(c.EraMin <= 2, $"{item.Id} is not of a later era than high medieval");
                    Expect.True(c.Culture is "vlandia" or "neutral" or "neutral_culture", $"{item.Id} ({c.Culture}) is western or neutral");
                }
        }

        var heroes = req.Attacker.Heroes.Concat(req.Defender.Heroes).ToList();
        Expect.True(heroes.Count > 0 && heroes.All(h => h.Look is not null && req.Looks!.ContainsKey(h.Look)), "every hero has a look of the request");
        Expect.True(heroes.Where(h => h.Formation is "Cavalry" or "HorseArcher").All(h => h.Look!.EndsWith("|cav", StringComparison.Ordinal)), "mounted heroes dress as riders");

        var off = Mapper.BuildRequest(KnightsBattle(), new MapperOptions { Costumes = false });
        Expect.True(off.Attacker.Heroes.Concat(off.Defender.Heroes).All(h => h.Look is null), "costumes off: no hero names a look");
        Expect.Null(off.Looks, "costumes off: no looks");
        Expect.True(off.Attacker.Troops.Concat(off.Defender.Troops).All(t => t.Look is null), "costumes off: no troop names a look");
    }

    private static void CostumesRules()
    {
        var tables = MappingTables.Get(null);
        var costumes = tables.Costumes!;
        var items = tables.CostumeItems;
        foreach (var profile in tables.Mapping.Profiles.Keys)
            Expect.True(costumes.Looks.ContainsKey(profile) && costumes.Looks[profile].Count > 50, $"profile {profile} has a look");

        var early = CostumePlanner.Build(costumes, items, CostumePlanner.Key("western_feudal", "culture_era_early_medieval", CostumePlanner.Infantry))!;
        var late = CostumePlanner.Build(costumes, items, CostumePlanner.Key("western_feudal", "culture_era_late_medieval", CostumePlanner.Infantry))!;
        int Count(Protocol.LookSpec s) => s.Slots.Values.Sum(l => l.Count);
        Expect.True(Count(late) > Count(early), "the late era adds plate and visors");
        Expect.True(early.Slots.Values.SelectMany(l => l).All(i => items[i.Id].EraMin <= 1), "no late item in the early era");

        var levy = CostumePlanner.Build(costumes, items, CostumePlanner.Key("western_feudal", "culture_era_early_medieval", CostumePlanner.Levy))!;
        var tagged = levy.Slots.Values.SelectMany(l => l).FirstOrDefault(i => items[i.Id].Tags.Contains(CostumePlanner.Levy));
        Expect.True(tagged is not null, "the western look has levy cloth");
        var same = early.Slots.Values.SelectMany(l => l).First(i => i.Id == tagged!.Id);
        Expect.Near(same.Weight * CostumePlanner.RoleFactor, tagged!.Weight, 0.011, "a levy item counts double for levies");

        Expect.Null(CostumePlanner.Build(costumes, items, "no_such_profile|1|inf"), "an unknown profile has no look");
        Expect.False(late.Slots.ContainsKey(CostumePlanner.HorseHarnessSlot), "horses keep their stock harness");

        Expect.Equal(1.0, HeroPlanner.ProwessSpeed(0), "no prowess: stock speed");
        Expect.Near(1.04, HeroPlanner.ProwessSpeed(10), 1e-9, "prowess 10: +4 %");
        Expect.Equal(1.10, HeroPlanner.ProwessSpeed(40), "at most +10 %");
        Expect.Equal(1.0, HeroPlanner.ProwessSpeed(-3), "negative prowess does not slow");
        Expect.Equal("norse|0|cav", CostumePlanner.Key("norse", "culture_era_tribal", CostumePlanner.RoleOf("heavy_cavalry")), "key");
        Expect.Equal("norse|1|rng", CostumePlanner.Key("norse", null, CostumePlanner.RoleOf("archers")), "an unlogged era is early medieval");
        Expect.Equal(CostumePlanner.Infantry, CostumePlanner.RoleOf("pikemen"), "pikemen are infantry");
    }

    private static void Ck3VerdictCheck()
    {
        var even = new Protocol.BalanceInfo { Ck3Attacker = 100, Ck3Defender = 95 };
        var strong = new Protocol.BalanceInfo { Ck3Attacker = 100, Ck3Defender = 40 };
        Expect.Null(Core.Orchestration.BattleOrchestrator.Ck3Verdict(null, "attacker"), "no balance record (single combat): no check");
        Expect.Null(Core.Orchestration.BattleOrchestrator.Ck3Verdict(even, "defender")!.Value.Agrees, "a toss-up counts for neither");
        var same = Core.Orchestration.BattleOrchestrator.Ck3Verdict(strong, "attacker")!.Value;
        Expect.True(same.Agrees == true && same.Text.Contains("2.5:1") && same.Text.Contains("same"), same.Text);
        var differs = Core.Orchestration.BattleOrchestrator.Ck3Verdict(strong, "defender")!.Value;
        Expect.True(differs.Agrees == false && differs.Text.Contains("DIFFERENT"), differs.Text);
        var weak = Core.Orchestration.BattleOrchestrator.Ck3Verdict(new Protocol.BalanceInfo { Ck3Attacker = 30, Ck3Defender = 90 }, "defender")!.Value;
        Expect.True(weak.Agrees == true && weak.Text.Contains("favours the defender (3:1)"), weak.Text);
        Expect.Null(Core.Orchestration.BattleOrchestrator.Ck3Verdict(strong, "none")!.Value.Agrees, "no winner: not counted");
    }

    private static void TitleArticle()
    {
        var b = KnightsBattle();
        b.CombatName = "Битва в провинции Маньяпура";
        Expect.Equal("Битва в провинции Маньяпура", PresentationWriter.Title(b), "a Russian name gets no English article");
        b.CombatName = "Battle of Orangallu";
        Expect.Equal("The Battle of Orangallu", PresentationWriter.Title(b));
        b.CombatName = "The Siege Lines of Acre";
        Expect.Equal("The Siege Lines of Acre", PresentationWriter.Title(b), "a name that has its article keeps it");
    }

    private static void TrimOwnWorkingSet()
    {
        // The self test trims itself: a live process of the same user, the case of CK3.
        var junk = new byte[64 * 1024 * 1024];
        for (int i = 0; i < junk.Length; i += 4096) junk[i] = 1;
        var (before, after, error) = Core.Native.ProcessMemory.Trim(Environment.ProcessId);
        GC.KeepAlive(junk);
        Expect.Null(error, "EmptyWorkingSet on an own-user process works");
        Expect.True(before > 0 && after < before, $"working set {before} MB -> {after} MB");
        Expect.Equal("no process", Core.Native.ProcessMemory.Trim(0).Error, "no pid: nothing tried");

        // PROTOCOL §31: the battle size the free commit holds.
        Expect.True(Core.Native.ProcessMemory.FreeCommitMb() > 0, "free commit readable");
        long Growth(params string[] files) => Core.Native.ProcessMemory.PageFileGrowthMb(32768, files, _ => 11264, _ => 300_000);
        Expect.Equal(3 * 32768 - 11264, Growth(@"e:\pagefile.sys 0 0"), "system managed: up to 3 x RAM");
        Expect.Equal(16000 - 11264, Core.Native.ProcessMemory.PageFileGrowthMb(32768, new[] { @"c:\pagefile.sys 2000 16000" }, _ => 11264, _ => 300_000), "sized: up to its maximum");
        Expect.Equal(0, Growth(@"?:\pagefile.sys"), "automatic on all drives: unknown, nothing counted");
        Expect.Equal(0, Core.Native.ProcessMemory.PageFileGrowthMb(32768, new[] { @"e:\pagefile.sys 0 0" }, _ => 0, _ => 1000), "a full drive leaves no room");
        Expect.Equal(1000, Core.Orchestration.MemoryGuard.Fit(1000, 20000, 2000), "plenty of memory: as configured");
        Expect.Equal(500, Core.Orchestration.MemoryGuard.Fit(1000, 7500, 2000), "3500 MB for agents: 500");
        Expect.Equal(200, Core.Orchestration.MemoryGuard.Fit(1000, 1000, 2000), "never below the smallest size");
        Expect.Equal(300, Core.Orchestration.MemoryGuard.Fit(300, 20000, 2000), "never above the setting");
        Expect.Equal(1000, Core.Orchestration.MemoryGuard.Fit(1000, null, 2000), "unknown memory: as configured");
    }

    private static void RussianTexts()
    {
        const string settings = "\"System\"={\n\t\"scroll_speed\"={\n\t\tversion=0\n\t\tvalue=50.000000\n\t}\n\t\"language\"={\n\t\tversion=0\n\t\tvalue=\"l_russian\"\n\t}\n}";
        Expect.Equal("ru", Core.Ck3.Ck3Language.FromSettings(settings), "CK3 in Russian");
        Expect.Equal("en", Core.Ck3.Ck3Language.FromSettings(settings.Replace("l_russian", "l_german")), "a language without words: English");
        Expect.Equal("en", Core.Ck3.Ck3Language.FromSettings(""), "no settings: English");
        Expect.Equal("ru", Core.Ck3.Ck3Language.Resolve("ru", null), "configured ru");
        Expect.Equal("en", Core.Ck3.Ck3Language.Resolve("en", null), "configured en");

        var b = KnightsBattle();
        b.CombatName = "Битва в провинции Лузиньян";
        var req = Mapper.BuildRequest(b, new MapperOptions { Language = "ru" });
        var p = req.Presentation;
        Expect.Equal("ru", p.Language, "the module is told the language");
        Expect.Equal("Битва в провинции Лузиньян", p.Title);
        Expect.True(p.Subtitle.Contains(" против ") && !p.Subtitle.Contains("against"), p.Subtitle);
        Expect.True(p.Subtitle.Contains(" г."), $"Russian season and year: {p.Subtitle}");
        Expect.True(p.Objective.StartsWith("Разгромите врага: ", StringComparison.Ordinal) || p.Objective.StartsWith("Удержите", StringComparison.Ordinal)
                    || p.Objective.StartsWith("Снимите", StringComparison.Ordinal), p.Objective);
        Expect.True(p.Detail.Contains("графство") || p.Detail.Contains("провинция") || p.Detail.StartsWith("Деревня", StringComparison.Ordinal), p.Detail);
        Expect.True(req.Attacker.Name.StartsWith("Войско", StringComparison.Ordinal) || !req.Attacker.Name.StartsWith("Host of", StringComparison.Ordinal), req.Attacker.Name);
        Expect.Equal("en", Mapper.BuildRequest(KnightsBattle(), new MapperOptions()).Presentation.Language, "English by default");

        var o = new Core.Abstractions.Ck3Outcome { MyLost = 1200, EnemyLost = 3400, MyKills = 5, PlayerWon = true, Clash = 1 };
        string line = Mapping.Outcome.OutcomeBuilder.SummaryLine(1, o, "Лузиньян", null, Core.Texts.Words.Russian);
        Expect.Equal("Победа (Лузиньян): враг потерял 3 400, мы — 1 200. Сражено вами лично: 5.", line);
        Expect.Equal("Victory at Lusignan: 3,400 of the enemy fell for 1,200 of your men. You struck down 5 foes yourself.",
            Mapping.Outcome.OutcomeBuilder.SummaryLine(1, o, "Lusignan"), "English unchanged");
        var award = new Core.Abstractions.CharacterAward { Name = "Одо", Prestige = 325, Glory = 30, ProwessRolls = 1, PlayerSide = true };
        Expect.Equal("Слава: Одо (+325 престижа, +30 славы, шанс повысить доблесть).", Mapping.Outcome.AwardBuilder.RenownLine(new[] { award }, Core.Texts.Words.Russian));
        Expect.Equal("Победа: Лузиньян", Core.Texts.Words.Russian.CardVictory("Лузиньян"));
        Expect.True(Core.Texts.Words.Russian.BattleContinues.Contains("«Пыль оседает...»"), "the option as the mod's Russian localization names it");
    }

    private static void ArmoryLaunchList()
    {
        var normal = new List<string> { "Native", "SandBoxCore", "CustomBattle", "CK3Bridge" };
        Expect.Equal(string.Join("*", normal), string.Join("*", BannerlordLink.ArmoryModuleList(normal, Array.Empty<string>())), "nothing installed: unchanged");
        Expect.Equal("Native*SandBoxCore*CustomBattle*SandBox*StoryMode*OpenSourceArmory*OpenSourceSaddlery*BL.Terra_Armarium.UniversalCore*" +
                     "BL.Terra_Armarium.Byzantine*BL.Terra_Armarium.Rus&Mongols*BL.Terra_Armarium.LateMedieval*CK3Bridge.Elephants*CK3Bridge",
            string.Join("*", BannerlordLink.ArmoryModuleList(normal, BannerlordLink.ArmoryModules)), "armory before CK3Bridge, dependencies first, Terra's core first");
        var fallback = new List<string> { "Native", "SandBoxCore", "CustomBattle", "SandBox", "StoryMode", "CK3Bridge" };
        Expect.Equal("Native*SandBoxCore*CustomBattle*SandBox*StoryMode*OpenSourceArmory*CK3Bridge",
            string.Join("*", BannerlordLink.ArmoryModuleList(fallback, new[] { "OpenSourceArmory" })), "fallback list: no duplicate dependencies");
        Expect.Equal("Native*SandBoxCore*CustomBattle*SandBox*CK3Bridge",
            string.Join("*", BannerlordLink.WithModuleBeforeBridge(normal, BannerlordLink.ArenaModule)), "the arenas' SandBox before CK3Bridge");
        Expect.Equal(string.Join("*", fallback), string.Join("*", BannerlordLink.WithModuleBeforeBridge(fallback, "SandBox")), "already there: unchanged");
    }
}
