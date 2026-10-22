// End-to-end check of the contract between the CK3 mod, the bridge and the Bannerlord module (docs/PROTOCOL.md), with neither game
// running:
//   fixtures/ck3_battle_game.log (generated from the mod's own info_log statements by fixtures/make_ck3_battle_game_log.mjs)
//   -> Ck3RecordParser -> Ck3EventDecoder -> BattleSnapshot -> BattleMapper.BuildRequest -> AppearanceProvider.Enrich (the user's
//   newest CK3 save when there is one) -> request.json exactly as BannerlordLink writes it -> read back the way the module reads it
//   (Bannerlord's own Newtonsoft.Json with the module's settings, loaded from the install when present, and a strict reader of the
//   module's contract) -> a result.json written the way the module writes it -> BattleMapper.BuildOutcome -> KeypadPayload and
//   RunFileRenderer -> replayed through the CK3 mod's generated keypad, commit and apply scripts (parsed from ck3_mod/ck3xbl).
// Read-only on the user's CK3 saves and both game installs; files are written to a temp folder only.
using CultureInfo = System.Globalization.CultureInfo;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CK3xBL.Appearance;
using CK3xBL.Appearance.Save;
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Ck3;
using CK3xBL.Core.Config;
using CK3xBL.Core.Delivery;
using CK3xBL.Core.Json;
using CK3xBL.Core.Logs;
using CK3xBL.Core.Model;
using CK3xBL.Mapping;
using CK3xBL.Mapping.Planning;
using CK3xBL.Protocol;

namespace CK3xBL.SelfTest;

internal static class IntegrationTests
{
    private const string SessionKey = "e2e0";
    private const long PlayerId = 10811;                 // Gunda Kakatiya, the player of the fixture (and of the debug campaign)

    public static void Register(TestRegistry r)
    {
        r.Add("integration.fixture.records_are_mod_statements", FixtureRecordsAreModStatements);
        r.Add("integration.bridge.reads_only_values_the_mod_saves", BridgeReadsOnlyValuesTheModSaves);
        r.Add("integration.e2e.log_to_events", LogToEvents);
        r.Add("integration.e2e.knights_facts_from_the_mod", KnightsFactsFromTheMod);
        r.Add("integration.e2e.battle_clock_from_the_mod", BattleClockFromTheMod);
        r.Add("integration.e2e.reinforcement_clash_to_the_mod", ReinforcementClashToTheMod);
        r.Add("integration.e2e.stand_in_from_the_mod", StandInFromTheMod);
        r.Add("integration.e2e.snapshot_to_request", SnapshotToRequest);
        r.Add("integration.e2e.request_json_as_the_module_reads_it", RequestJsonAsTheModuleReadsIt);
        r.Add("integration.e2e.result_json_to_outcome", ResultJsonToOutcome);
        r.Add("integration.e2e.register_map_matches_the_apply_effect", RegisterMapMatchesTheApplyEffect);
        r.Add("integration.e2e.keypad_keys_commit_in_the_mod", KeypadKeysCommitInTheMod);
        r.Add("integration.e2e.run_file_commits_in_the_mod", RunFileCommitsInTheMod);
        r.Add("integration.e2e.payload_v4_keys_reach_the_steering_apply", PayloadV4KeysReachTheSteeringApply);
        r.Add("integration.mod.dust_settles_option_is_the_loc_text", DustSettlesOptionIsTheLocText);
        r.Add("integration.mod.payload_v4_registers_match_registers_mjs", PayloadV4RegistersMatchRegistersMjs);
        r.Add("integration.live.decode_game_log", DecodeLiveGameLog);
    }

    // ================================================================================================================ shared state

    private static readonly Lazy<Paths> P = new(Paths.Find);
    private static readonly Lazy<ModScripts> Mod = new(() => ModScripts.Load(P.Value.Mod));
    private static readonly Lazy<Decoded> Log = new(Decode);
    private static readonly Lazy<(List<Ck3Event> Events, List<string> Warnings)> ClockLog = new(() => DecodeFile(P.Value.ClockFixture));
    private static readonly Lazy<Prepared> Prep = new(Prepare);
    private static readonly Lazy<Returned> Ret = new(Return);

    private sealed class Paths
    {
        public required string Bridge, Data, Mod, Fixture, ClockFixture, StandInFixture, Temp, Ck3UserDir;
        public string? Ck3Game, BannerlordGame;

        public static Paths Find()
        {
            var bridgePaths = BridgePaths.Detect();
            string bridge = bridgePaths.BridgeRoot;
            string repo = Directory.GetParent(bridge)!.FullName;
            string mod = Path.Combine(repo, "ck3_mod", "ck3xbl");
            Expect.True(File.Exists(Path.Combine(mod, "descriptor.mod")), $"CK3 mod not found at {mod}");
            var config = new BridgeConfig();
            GameLocator.Resolve(config);
            string temp = Path.Combine(Path.GetTempPath(), "ck3xbl_selftest_integration");
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            Directory.CreateDirectory(temp);
            return new Paths
            {
                Bridge = bridge, Data = bridgePaths.DataDir, Mod = mod, Temp = temp, Ck3UserDir = config.Ck3.UserDir,
                Fixture = Path.Combine(bridge, "CK3xBL.SelfTest", "fixtures", "ck3_battle_game.log"),
                ClockFixture = Path.Combine(bridge, "CK3xBL.SelfTest", "fixtures", "ck3_battle_clock_game.log"),
                StandInFixture = Path.Combine(bridge, "CK3xBL.SelfTest", "fixtures", "ck3_stand_in_game.log"),
                Ck3Game = GameLocator.IsCk3Dir(config.Ck3.GameDir) ? config.Ck3.GameDir : null,
                BannerlordGame = GameLocator.IsBannerlordDir(config.Bannerlord.GameDir) ? config.Bannerlord.GameDir : null,
            };
        }
    }

    // ================================================================================================================ the fixture

    /// <summary>Both fixtures are what the mod writes: every marker is an info_log literal of the mod (in the file the marker names), and
    /// every ck3xbl_ key of a dump is a name the mod saves.</summary>
    private static void FixtureRecordsAreModStatements()
    {
        CheckFixture(P.Value.Fixture, new[] { "B", "L", "LV", "S", "A", "R", "C", "CX", "AX", "RX", "CHOICE", "J", "IN", "RES", "END" });
        CheckFixture(P.Value.ClockFixture, new[] { "B", "S", "A", "C", "CHOICE", "J", "IN", "RES", "ST", "END" });
        CheckFixture(P.Value.StandInFixture, new[] { "B", "L", "S", "A", "R", "C", "CX", "CHOICE" });
    }

    private static void CheckFixture(string fixture, string[] requiredTypes)
    {
        var mod = Mod.Value;
        var marker = new Regex(@"^\[\d\d:\d\d:\d\d\]\[I\]\[jomini_effect_impl\.cpp:\d+\]: file: (?<file>\S+) line: \d+ \((?<ctx>[^)]*)\): CK3XBL\|(?<rec>.*)$");
        var dumpKey = new Regex(@"^(?<key>ck3xbl_[A-Za-z0-9_]+): ");
        int records = 0, keys = 0;
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(fixture, Encoding.UTF8))
        {
            if (line.Contains("CK3XBL|", StringComparison.Ordinal))
            {
                var m = marker.Match(line);
                Expect.True(m.Success, $"marker line not in the engine's format: {line}");
                string rec = m.Groups["rec"].Value;
                Expect.True(mod.Literals.TryGetValue(rec, out var files), $"'CK3XBL|{rec}' is not an info_log statement of the mod");
                Expect.True(files!.Contains(m.Groups["file"].Value), $"'CK3XBL|{rec}' is not in {m.Groups["file"].Value}");
                types.Add(rec.Split('|')[0]);
                records++;
            }
            else if (dumpKey.Match(line) is { Success: true } k)
            {
                Expect.True(mod.SavedNames.Contains(k.Groups["key"].Value), $"dump key {k.Groups["key"].Value} is never saved by the mod");
                keys++;
            }
        }
        foreach (var t in requiredTypes)
            Expect.True(types.Contains(t), $"{Path.GetFileName(fixture)} has no {t} record");
        Console.WriteLine($"      {Path.GetFileName(fixture)}: {records} records ({types.Count} types) and {keys} dump values, all from the mod");
    }

    /// <summary>Every ck3xbl_ value or scope the bridge's log decoder reads is one the mod saves (a renamed key would silently read as
    /// missing).</summary>
    private static void BridgeReadsOnlyValuesTheModSaves()
    {
        var mod = Mod.Value;
        var literal = new Regex("\"(?<key>ck3xbl_[a-z0-9_]+)\"");
        var read = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(P.Value.Bridge, "CK3xBL.Core", "Logs"), "*.cs"))
            foreach (var line in File.ReadLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)))   // code, not comments
                foreach (Match m in literal.Matches(line))
                    read.Add(m.Groups["key"].Value);
        Expect.True(read.Count >= 40, $"only {read.Count} keys found in the decoder sources");
        var missing = read.Where(k => !mod.SavedNames.Contains(k)).ToList();
        Expect.True(missing.Count == 0, $"the bridge reads keys the mod never saves: {string.Join(", ", missing)}");
        Console.WriteLine($"      {read.Count} keys read by the bridge, all saved by the mod");
    }

    // ================================================================================================================ log -> events

    private sealed class Decoded
    {
        public required List<Ck3Event> Events;
        public required List<string> Warnings;
        public required BattleSnapshot Battle3, Battle4;
    }

    private static Decoded Decode()
    {
        var (events, warnings) = DecodeFile(P.Value.Fixture);
        var snapshots = events.OfType<SnapshotCompleted>().Select(s => s.Snapshot).ToList();
        Expect.Equal(2, snapshots.Count, "snapshots in the fixture");
        return new Decoded { Events = events, Warnings = warnings, Battle3 = snapshots[0], Battle4 = snapshots[1] };
    }

    /// <summary>A fixture through the bridge's parser and decoder, line by line (the tailer hands over lines in whatever chunks the file
    /// grows by; the parser must not care).</summary>
    private static (List<Ck3Event> Events, List<string> Warnings) DecodeFile(string fixture)
    {
        var events = new List<Ck3Event>();
        var warnings = new List<string>();
        var decoder = new Ck3EventDecoder(events.Add, warnings.Add, SessionKey);
        var parser = new Ck3RecordParser(decoder.Accept);
        foreach (var line in File.ReadLines(fixture, Encoding.UTF8)) parser.Feed(line);
        parser.Flush();
        decoder.Flush();
        return (events, warnings);
    }

    private static string Describe(Ck3Event e) => e switch
    {
        SnapshotCompleted s => $"snapshot {s.Snapshot.Bid}",
        ChoiceMade c => $"choice {c.Bid} {c.Choice}",
        InputAck a => $"in {a.Bid} {(a.Ok ? "ok" : "bad")}",
        ResultTrace t => $"res {t.Bid} {t.Stage}",
        CombatEnded c => $"end {c.Bid} {(c.WinnerSide ? "winner" : "loser")}",
        ArmyJoined j => $"join {j.Bid} {j.Side} army {j.Army.Tag}",
        SteeringTrace t => $"st {t.Bid} {t.Stage}",
        _ => e.GetType().Name,
    };

    private static void LogToEvents()
    {
        var d = Log.Value;
        Expect.True(d.Warnings.Count == 0, "decoder warnings: " + string.Join(" | ", d.Warnings));

        Expect.SequenceEqual(new[]
        {
            "snapshot 3", "choice 3 Field", "in 3 bad", "in 3 ok",
            "res 3 BEGIN", "res 3 LIVE", "res 3 A", "res 3 A", "res 3 A", "res 3 END", "end 3 winner", "end 3 loser",
            "snapshot 4", "choice 4 Auto", "join 4 Defender army 12", "end 4 winner", "end 4 loser",
        }, d.Events.Select(Describe));

        var b = d.Battle3;
        Expect.Equal("b3_" + SessionKey, b.RequestId);
        Expect.True(b.Complete);
        Expect.Equal((134217801L, "Battle of Orangallu"), (b.CombatId, b.CombatName));
        Expect.Equal((871, 2, 14), (b.Year, b.Month, b.Day));
        Expect.Equal(PlayerId, b.PlayerCk3Id);
        Expect.Equal("Bhupati Phoro's Conquest of Warangal", b.WarName);
        var loc = b.Location;
        Expect.Equal((1256L, "Orangallu", "c_orangallu", "b_warangal"), (loc.ProvinceId, loc.ProvinceName, loc.CountyKey, loc.BaronyKey));
        Expect.Equal(("hills", "none", "castle_holding"), (loc.Terrain, loc.Winter, loc.Holding));
        Expect.Equal((false, false, true, 3, 12, (long?)1255), (loc.Coastal, loc.Riverside, loc.Siege, loc.FortLevel, loc.Development, loc.AttackerOriginProvinceId));
        Expect.Equal("world_india", loc.Facts["region"]);
        Expect.Equal("indian_building_gfx", loc.Facts["building_gfx"]);

        var att = b.Attacker;
        Expect.Equal((1925, false, 10935L), (att.Size, att.PlayerIn, att.Primary!.Ck3Id));
        var host = att.Armies.Single();
        Expect.Equal((7, 16777240L, 1925, 1346), (host.Tag, host.ArmyId, host.Size, host.Levies));
        Expect.Equal(("kannada", "heritage_dravidian", "culture_era_early_medieval", false),
            (host.OwnerCulture.Culture, host.OwnerCulture.Heritage, host.OwnerCulture.Era, host.OwnerNomadic));
        Expect.SequenceEqual(new[] { "armored_footmen", "light_horsemen", "paiks", "onager" }, host.Regiments.Select(r => r.MaaKey));
        var paiks = host.Regiments.Single(r => r.MaaKey == "paiks");
        Expect.True(paiks.Hired && paiks.HiredCulture?.Culture == "telugu" && paiks.HiredCulture.UnitGfx == "indian_unit_gfx", "hired regiment look");
        Expect.Equal((175, 200), (host.Regiments[1].Current, host.Regiments[1].Max));
        Expect.SequenceEqual(new[] { 5, 6, 7 }, att.Characters.Select(c => c.Tag));
        var bhupati = att.Characters[0];
        Expect.True(bhupati.IsCommander && !bhupati.IsPrimary && bhupati.ArmyTag == 7 && bhupati.PrimaryTitleKey == "d_vengi" && bhupati.HouseName == "Phoro");
        Expect.SequenceEqual(new[] { "aggressive_attacker" }, bhupati.CommanderTraits);
        Expect.SequenceEqual(new[] { "strong" }, bhupati.Traits);
        var kesava = att.Characters[1];
        Expect.True(kesava.IsKnight && kesava.Ref.HistoricalId is null && kesava.PrimaryTitleKey is null && kesava.HouseName is null,
            "a lowborn knight takes no leftover title or house from the record before him");

        var def = b.Defender;
        Expect.Equal((2300, true, PlayerId), (def.Size, def.PlayerIn, def.Primary!.Ck3Id));
        Expect.True(ReferenceEquals(def, b.PlayerSide), "the player's side is the defender");
        Expect.SequenceEqual(new[] { 8, 9 }, def.Armies.Select(a => a.Tag));
        Expect.Equal(PlayerId, def.Armies[0].Commander?.Ck3Id);
        Expect.Null(def.Armies[1].Commander, "the ally's army has no commander (the leftover a_cmd is guarded by its sentinel)");
        var gunda = def.Characters[0];
        Expect.True(gunda.IsCommander && gunda.IsPlayer && gunda.Tag == 1 && gunda.ArmyTag == 8 && gunda.Age == 71 && !gunda.Female);
        Expect.Equal(("c_orangallu", "Kakatiya", "south_indian"), (gunda.PrimaryTitleKey, gunda.HouseName, gunda.Ethnicity));
        Expect.Equal(("telugu", "indian_clothing_gfx"), (gunda.Culture.Culture, gunda.Culture.ClothingGfx));
        Expect.SequenceEqual(new[] { "unyielding_defender" }, gunda.CommanderTraits);
        Expect.SequenceEqual(new[] { 1, 2, 8 }, def.Characters.Select(c => c.Tag));
        Expect.True(def.Characters[1].PrimaryTitleKey is null && def.Characters[1].HouseName == "Kakatiya", "landless dynast");

        // Battle 4: the player is only the defenders' war leader (C|PRIMARY); his host joins later (J) under the knight Rudra.
        var b4 = d.Battle4;
        Expect.Equal(4, b4.Bid);
        var leader = b4.Defender.Characters.Single(c => c.Ref.Ck3Id == PlayerId);
        Expect.True(leader.IsPrimary && leader.IsPlayer && !leader.IsCommander && leader.ArmyTag == 0);
        var join = d.Events.OfType<ArmyJoined>().Single();
        Expect.Equal((4, SideKey.Defender, 12, 3), (join.Bid, join.Side, join.Army.Tag, join.Army.Regiments.Count));
        Expect.Equal(23540L, join.Characters.Single().Ref.Ck3Id);
        var ok = d.Events.OfType<InputAck>().Last();
        Expect.Equal((true, 34, 2687L), (ok.Ok, ok.Count, ok.Sum));
        var armiesApplied = d.Events.OfType<ResultTrace>().Where(t => t.Stage == "A").Select(t => t.Values["ck3xbl_a_tag"]).ToList();
        Expect.SequenceEqual(new[] { "7.00", "8.00", "9.00" }, armiesApplied);
        var ended = d.Events.OfType<CombatEnded>().First();
        Expect.True(ended.WinnerSide && ended.Primary?.Ck3Id == PlayerId && !ended.Wipe);
    }

    /// <summary>The knights facts of PROTOCOL §13.2 as the mod logs them: the S keys of both sides,
    /// knight effectiveness, the siege role and territory of the armies, an acclaimed knight, artifacts and leveled traits.</summary>
    private static void KnightsFactsFromTheMod()
    {
        var d = Log.Value;
        var b = d.Battle3;
        Expect.True(b.KnightFactsLogged, "the mod logs the knights facts (ck3xbl_s_has_cmd)");
        Expect.SequenceEqual(new int?[] { 1, 2 }, b.Sides.Select(s => s.FaithHostilityOut).OrderBy(h => h), "faith hostility of both sides");
        Expect.True(b.Sides.All(s => s.Commander is null || s.Characters.Any(c => c.Ref.Ck3Id == s.Commander.Ck3Id && c.IsCommander)),
            "a side commander is one of the side's C|CMD characters");

        var armies = b.Sides.SelectMany(s => s.Armies).ToList();
        Expect.Equal(1, armies.Count(a => a.KnightEffectivenessPermille == 1500), "one army logs knight effectiveness 1500");
        Expect.True(b.Attacker.Armies.Any(a => a is { Besieging: true, Territory: "hostile" }), "the attacker besieges in hostile land");
        Expect.True(b.Defender.Armies.Any(a => a.Territory == "own") && b.Defender.Armies.All(a => a.Territory != "hostile"),
            "the defender fights in its own land; no defender army is in hostile land");

        var characters = d.Events.OfType<SnapshotCompleted>().SelectMany(s => s.Snapshot.Sides).SelectMany(s => s.Characters)
            .Concat(d.Events.OfType<ArmyJoined>().SelectMany(j => j.Characters)).ToList();
        var acclaimed = b.Sides.SelectMany(s => s.Characters).Where(c => c.Accolade is { Active: true, Rank: 3 }).ToList();
        Expect.True(acclaimed.Count == 1 && acclaimed[0].Accolade!.Levels.GetValueOrDefault("archer_attribute") == 2,
            "one acclaimed knight in battle 3: active, rank 3, archer attribute at level 2");
        Expect.True(characters.Any(c => c.Artifacts.Any(a => a is { SlotType: "primary_armament", Rarity: "famed" }) &&
                                        c.Artifacts.Any(a => a is { SlotType: "armor", Rarity: "illustrious", HelmetFeature: "armor_helmet_nasal" })),
            "a famed weapon and illustrious armour with its helmet shape");
        Expect.True(characters.Any(c => c.CombatTraits.Contains("brave") && c.TraitLevels.GetValueOrDefault("forest_fighter") == 3),
            "brave and forest_fighter at level 3");
        Expect.True(characters.Any(c => c.TraitLevels.GetValueOrDefault("the_wake") == 4), "the_wake at level 4");
    }

    /// <summary>The battle clock as the mod logs it (fixtures/ck3_battle_clock_game.log, PROTOCOL §14.5):
    /// battle 5 is steered to the Bannerlord winner with a winner-side join; battle 6 is suspended by a loser-side join that opens the
    /// reinforcement clash 7 (child snapshot with its parent link), which is steered and decided; battle 8 comes from a run file in
    /// dev-mode order (the steering starts in the same run as the commit) and is handed back. Every record decodes without warnings,
    /// in the mod's order, with the values the bridge follows.</summary>
    private static void BattleClockFromTheMod()
    {
        var (events, warnings) = ClockLog.Value;
        Expect.True(warnings.Count == 0, "decoder warnings: " + string.Join(" | ", warnings));

        static IEnumerable<string> Times(int n, string what) => Enumerable.Repeat(what, n);
        string[] Apply(int bid) => new[] { $"in {bid} ok", $"res {bid} BEGIN", $"res {bid} LIVE", $"st {bid} BEGIN", $"res {bid} END" };
        var expected = new List<string> { "snapshot 5", "choice 5 Field" };
        expected.AddRange(Apply(5));
        expected.AddRange(Times(4, "st 5 P"));
        expected.Add("join 5 Defender army 15");
        expected.AddRange(Times(4, "st 5 P"));
        expected.AddRange(new[] { "st 5 WIN", "end 5 winner", "end 5 loser", "st 5 FIX", "st 5 FIX", "st 5 DONE" });
        expected.AddRange(new[] { "snapshot 6", "choice 6 Field" });
        expected.AddRange(Apply(6));
        expected.AddRange(Times(2, "st 6 P"));
        expected.AddRange(new[] { "join 6 Attacker army 18", "snapshot 7", "st 6 SUSPEND", "choice 7 Field" });
        expected.AddRange(Apply(7));
        expected.AddRange(Times(5, "st 7 P"));
        expected.AddRange(new[] { "st 7 WIN", "end 7 winner", "end 7 loser", "st 7 FIX", "st 7 FIX", "st 7 FIX", "st 7 DONE" });
        expected.AddRange(new[] { "snapshot 8", "choice 8 Field" });
        expected.AddRange(Apply(8));
        expected.AddRange(Times(9, "st 8 P"));
        expected.AddRange(new[] { "st 8 HANDBACK", "st 8 FIX", "st 8 FIX", "st 8 DONE", "end 8 winner", "end 8 loser" });
        Expect.SequenceEqual(expected, events.Select(Describe), "the battle clock's records in the mod's order");

        // Every IN dump carries the highest payload version the mod accepts (PROTOCOL §14.3).
        Expect.True(events.OfType<InputAck>().All(a => a.Ok && a.MaxVersion == KeypadLayout.SteerPayloadVersion), "IN|OK with ck3xbl_in_maxver 4");
        var st = events.OfType<SteeringTrace>().ToList();
        SteeringTrace One(int bid, string stage) => st.Single(t => t.Bid == bid && t.Stage == stage);
        IEnumerable<string> FixedTags(int bid) => st.Where(t => t.Bid == bid && t.Stage == "FIX").Select(t => t.Values["ck3xbl_a_tag"]);

        // Battle 5: decide mode, the defender wins; the ally joins the winners and is steered as a joiner (j_steer 1).
        var begin5 = One(5, "BEGIN");
        Expect.Equal(((int?)1, (int?)2, (int?)380, (int?)1020, (int?)12, (int?)0),
            (begin5.Mode, begin5.Winner, begin5.AttackerTarget, begin5.DefenderTarget, begin5.EndDay, begin5.ParentBid), "ST|BEGIN of battle 5");
        var joins = events.OfType<ArmyJoined>().ToList();
        Expect.Equal((5, SideKey.Defender, 15, 1), (joins[0].Bid, joins[0].Side, joins[0].Army.Tag, joins[0].SteerRole), "winner-side join");
        Expect.True(st.Where(t => t.Bid == 5 && t.Stage == "P").All(t => t.AttackerTarget == 380 && t.DefenderTarget == 1020 && t.Reason is null && t.Mode is null),
            "pulses carry the targets and no stage-bound keys");
        var win5 = One(5, "WIN");
        Expect.Equal(((int?)2, false, (int?)10), (win5.Winner, win5.Capped, win5.CombatDay), "ST|WIN of battle 5");
        Expect.True(win5.DefenderKilled >= 0.95 * 1020, $"the winner counted {win5.DefenderKilled} of its target 1020");
        Expect.True(events.OfType<CombatEnded>().Where(c => c.Bid == 5).All(c => c.SteeringBid == 5), "END names the steering bid");
        Expect.SequenceEqual(new[] { "13.00", "14.00" }, FixedTags(5), "one ST|FIX per army of the battle");
        Expect.Equal((int?)1, One(5, "DONE").Reason, "decided");

        // Battles 6 and 7: the loser's reinforcement (j_steer 2) suspends battle 6 and opens clash 2 of the same CK3 combat.
        Expect.Equal((6, SideKey.Attacker, 18, 2), (joins[1].Bid, joins[1].Side, joins[1].Army.Tag, joins[1].SteerRole), "loser-side join");
        var snapshots = events.OfType<SnapshotCompleted>().Select(s => s.Snapshot).ToDictionary(s => s.Bid);
        var (parent, child) = (snapshots[6], snapshots[7]);
        Expect.Equal((6, 2, parent.CombatId), (child.ParentBid, child.Clash, child.CombatId), "the child snapshot links its parent");
        Expect.Equal((0, 1), (parent.ParentBid, parent.Clash), "the parent is a first clash");
        Expect.SequenceEqual(new[] { 17, 19 }, child.Attacker.Armies.Select(a => a.Tag), "the parent's army keeps its tag; the reinforcement is tagged");
        Expect.SequenceEqual(new[] { 16 }, child.Defender.Armies.Select(a => a.Tag));
        var suspend = One(6, "SUSPEND");
        Expect.Equal(((int?)7, (int?)1), (suspend.ChildBid, suspend.Reason), "ST|SUSPEND names the child");
        var begin7 = One(7, "BEGIN");
        Expect.Equal(((int?)6, (int?)1, (int?)410, (int?)900), (begin7.ParentBid, begin7.Mode, begin7.AttackerTarget, begin7.DefenderTarget),
            "the child's ST|BEGIN continues the parent with cumulative targets");
        Expect.True(events.OfType<CombatEnded>().Where(c => c.Bid == 7).All(c => c.SteeringBid == 7), "END of clash 2");
        Expect.SequenceEqual(new[] { "16.00", "17.00", "19.00" }, FixedTags(7));
        Expect.Equal((int?)1, One(7, "DONE").Reason);

        // Battle 8: run file in dev mode. Commit, apply and ST|BEGIN come in one run (the bridge sees ST|BEGIN before RES|END
        // completes the delivery); hand-back mode reaches its targets and CK3 ends the combat itself later, unsteered.
        var run = events.SkipWhile(e => e is not InputAck { Bid: 8 }).Take(5).ToList();
        Expect.True(run.All(e => e.LogTime == run[0].LogTime), "IN, RES and ST|BEGIN of battle 8 in one effect run");
        var runContexts = File.ReadLines(P.Value.ClockFixture, Encoding.UTF8)
            .Where(l => l.EndsWith("CK3XBL|IN|OK", StringComparison.Ordinal) || l.Contains("): CK3XBL|RES|", StringComparison.Ordinal) ||
                        l.EndsWith("CK3XBL|ST|BEGIN", StringComparison.Ordinal))
            .Select(l => Regex.Match(l, @" \((?<ctx>[^)]*)\): CK3XBL\|").Groups["ctx"].Value).TakeLast(5);
        Expect.SequenceEqual(Enumerable.Repeat("run_file", 5), runContexts, "the run file's context");
        Expect.Equal(((int?)2, (int?)0), (One(8, "BEGIN").Mode, One(8, "BEGIN").Winner), "hand-back mode, no winner");
        var handback = One(8, "HANDBACK");
        Expect.Equal(((int?)2, false), (handback.Reason, handback.Capped), "targets reached");
        Expect.Equal((int?)2, One(8, "DONE").Reason, "handed back");
        Expect.True(events.OfType<CombatEnded>().Where(c => c.Bid == 8).All(c => c.SteeringBid == 0), "the fix removed the steering bid");
        Console.WriteLine($"      {events.Count} events: {st.Count} ST records over battles 5-8, {joins.Count} joins, clash 2 of battle {child.ParentBid}");
    }

    /// <summary>The reinforcement clash end to end: the parent battle 6 as the bridge planned and resolved it (payload 4) becomes the
    /// ParentLink of the child snapshot 7 of the fixture; the mapper plans "Reinforcements arrive!" on the parent's field with the
    /// reinforcement and the parent's armies capped at their end state, and its outcome (clash 2, cumulative targets and summary) goes
    /// through the keypad into the mod's commit for the child's bid.</summary>
    private static void ReinforcementClashToTheMod()
    {
        var snapshots = ClockLog.Value.Events.OfType<SnapshotCompleted>().Select(s => s.Snapshot).ToDictionary(s => s.Bid);
        var options = new BridgeConfig().ToMapperOptions(P.Value.Data);
        var mapper = new BattleMapper();

        var first = snapshots[6];
        var firstRequest = mapper.BuildRequest(first, options);
        var firstOutcome = mapper.BuildOutcome(first, firstRequest, PlainResult(firstRequest, "defender"), options, null);
        Expect.Equal((KeypadLayout.SteerPayloadVersion, 12, 0, 1), (firstOutcome.PayloadVersion, firstOutcome.PaceDays, firstOutcome.ParentBid, firstOutcome.Clash),
            "the first clash is payload 4 at the default pace day");
        var parent = new ParentLink
        {
            Planned = first, Request = firstRequest, Outcome = firstOutcome,
            FallenTags = firstOutcome.Characters.Where(c => c.Fate >= 3).Select(c => c.Tag).ToHashSet(),
        };

        var live = snapshots[7];
        var plan = mapper.BuildContinuation(live, parent, options);
        var request = plan.Request;
        Expect.Equal(("b7_" + SessionKey, PresentationWriter.ReinforcementsTitle), (request.Id, request.Presentation.Title));
        Expect.Equal((firstRequest.Scene, firstRequest.PlayerSide, firstRequest.Season), (request.Scene, request.PlayerSide, request.Season),
            "fought on the parent's field");
        Expect.Equal((6, 2), (plan.Capped.ParentBid, plan.Capped.Clash));
        Expect.True(request.Attacker.Troops.Any(t => t.Group!.EndsWith("#19", StringComparison.Ordinal)) ||
                    request.Attacker.Troops.Any(t => t.Group!.Contains("#19#", StringComparison.Ordinal)), "the reinforcement fights");
        int liveLevies = live.Attacker.Armies.Single(a => a.Tag == 17).Levies, cappedLevies = plan.Capped.Attacker.Armies.Single(a => a.Tag == 17).Levies;
        Expect.True(cappedLevies <= liveLevies, $"the parent's host is capped at its end state ({cappedLevies} of {liveLevies} live levies)");

        var outcome = mapper.BuildOutcome(plan.Capped, request, PlainResult(request, "defender"), options, parent);
        Expect.Equal((7, 6, 2, KeypadLayout.SteerPayloadVersion, 2), (outcome.Bid, outcome.ParentBid, outcome.Clash, outcome.PayloadVersion, outcome.OutcomeCode));
        Expect.True(outcome.AttackerKillTarget > firstOutcome.AttackerKillTarget && outcome.DefenderKillTarget > firstOutcome.DefenderKillTarget,
            $"cumulative kill targets: {firstOutcome.AttackerKillTarget}/{firstOutcome.DefenderKillTarget} -> {outcome.AttackerKillTarget}/{outcome.DefenderKillTarget}");
        Expect.True(outcome.MyLost > firstOutcome.MyLost && outcome.EnemyLost > firstOutcome.EnemyLost, "cumulative r4..r6");
        Expect.True(outcome.Armies.Any(a => a.ArmyTag == 19), "the reinforcement's end state is in the payload");

        var payload = KeypadPayload.Encode(outcome, MaaIndexTable.Load(Path.Combine(P.Value.Data, "ck3_maa_index.json")));
        var pad = Mod.Value.NewKeypad(pendingBid: 7);
        foreach (var k in payload.ToKeySequence()) pad.Press(k);
        Expect.True(pad.LastCommitOk, $"IN|OK for the child; the mod computed checksum {pad.LastSum}, the payload carries {payload.Registers[KeypadLayout.Checksum]}");
        Expect.Equal((7L, 4L), (pad.R[KeypadLayout.Bid], pad.R[KeypadLayout.Version]));
        var stale = Mod.Value.NewKeypad(pendingBid: 6);
        foreach (var k in payload.ToKeySequence()) stale.Press(k);
        Expect.False(stale.LastCommitOk, "the child's payload is refused while the parent's bid is pending");
        Console.WriteLine($"      clash 2 of battle 6: targets {outcome.AttackerKillTarget}/{outcome.DefenderKillTarget}, {outcome.Armies.Count} army slots, " +
                          $"{payload.ToKeySequence().Count} keys");
    }

    /// <summary>The stand-in player of PROTOCOL §15.1 as the mod logs it (fixtures/ck3_stand_in_game.log, battle 9 at Kollipaka):
    /// Gunda is the C|PLAYER|def record with ck3xbl_c_absent 1 and his host has no commander. The parser reads him absent, the mapper
    /// puts him on the field as the defenders' general h0, and the outcome never harms him nor names him as killer, even in hardcore.</summary>
    private static void StandInFromTheMod()
    {
        var (events, warnings) = DecodeFile(P.Value.StandInFixture);
        Expect.True(warnings.Count == 0, "decoder warnings: " + string.Join(" | ", warnings));
        Expect.SequenceEqual(new[] { "snapshot 9", "choice 9 Field" }, events.Select(Describe));
        var battle = events.OfType<SnapshotCompleted>().Single().Snapshot;
        Expect.True(battle.Complete && battle.Defender.Commander is null && battle.Defender.Armies.All(a => a.Commander is null),
            "the defenders have no commander");
        var gunda = battle.Defender.Characters.Single(c => c.Ref.Ck3Id == PlayerId);
        Expect.True(gunda.IsPlayer && gunda.Absent == true && !gunda.IsCommander && gunda.ArmyTag == 0, "Gunda is the absent C|PLAYER record");
        var beta = battle.Defender.Characters.Single(c => c.IsPrimary);
        Expect.True(!beta.IsPlayer && beta.Absent is null, "ck3xbl_c_absent is read on the player's record only");

        var options = new BridgeConfig().ToMapperOptions(P.Value.Data);
        options.PlayerDeathIsReal = true;                      // hardcore: still no harm to a player who was not there
        var mapper = new BattleMapper();
        var request = mapper.BuildRequest(battle, options);
        var h0 = request.Defender.Heroes.Single(h => h.IsPlayer);
        Expect.True(h0.Key == "h0" && h0.Role == "player" && h0.Ck3Id == PlayerId && h0.Tag == gunda.Tag, "Gunda stands in as h0");
        Expect.Equal(("defender", true, "h0"), (request.PlayerSide, request.PlayerIsGeneral, request.Defender.General));
        var enemyGeneral = request.Attacker.Heroes.Single(h => h.Key == request.Attacker.General);
        Expect.True(enemyGeneral.Tag > 0, "Bhupati leads the attackers as a hero");

        // Lost: Bhupati cuts Gunda down. No fate for the player.
        var lost = PlainResult(request, "attacker");
        var fallen = lost.Heroes.Single(r => r.Key == "h0");
        (fallen.State, fallen.KilledBy, fallen.Kills) = ("killed", enemyGeneral.Key, 4);
        var lostOutcome = mapper.BuildOutcome(battle, request, lost, options);
        Expect.False(lostOutcome.Characters.Any(c => c.Tag == gunda.Tag || c.Ck3Id == PlayerId), "the absent player is never harmed");
        Expect.Equal(4, lostOutcome.MyKills);

        // Won: Gunda strikes Bhupati down. Bhupati's fate applies, with no killer (neither the striker nor the general h0 is in CK3's combat).
        var won = PlainResult(request, "defender");
        var bhupati = won.Heroes.Single(r => r.Key == enemyGeneral.Key);
        (bhupati.State, bhupati.KilledBy) = ("killed", "h0");
        var wonOutcome = mapper.BuildOutcome(battle, request, won, options);
        var fate = wonOutcome.Characters.Single(c => c.Tag == enemyGeneral.Tag);
        Expect.Equal(0, fate.KillerTag, "an absent player is never named as killer");
        Expect.False(wonOutcome.Characters.Any(c => c.Tag == gunda.Tag));
        Console.WriteLine($"      battle 9: Gunda (tag {gunda.Tag}) stands in as h0; enemy general tag {enemyGeneral.Tag} gets fate {fate.Fate} with no killer");
    }

    /// <summary>A Bannerlord result without hero events: the winner's groups lose 15 % killed and 4 % wounded, the loser's 40 % killed,
    /// 8 % wounded and 20 % routed; every hero stands.</summary>
    private static BattleResult PlainResult(BattleRequest req, string winner)
    {
        SideResult Side(SideRequest s, bool won)
        {
            var side = new SideResult();
            foreach (var g in s.Troops.GroupBy(t => t.Group!))
            {
                int n = g.Sum(t => t.Count);
                var gr = new GroupResult
                {
                    Requested = n, Spawned = n, Killed = (int)(n * (won ? 0.15 : 0.40)), Wounded = (int)(n * (won ? 0.04 : 0.08)),
                    Routed = won ? 0 : (int)(n * 0.20),
                };
                side.Groups[g.Key] = gr;
                side.Requested += n; side.Spawned += n; side.Killed += gr.Killed; side.Wounded += gr.Wounded; side.Routed += gr.Routed;
            }
            return side;
        }
        var result = new BattleResult
        {
            Id = req.Id, Status = "finished", Winner = winner, DurationSeconds = 300f,
            Attacker = Side(req.Attacker, winner == "attacker"), Defender = Side(req.Defender, winner == "defender"),
        };
        foreach (var h in req.Attacker.Heroes.Concat(req.Defender.Heroes))
            result.Heroes.Add(new HeroResult { Key = h.Key, Ck3Id = h.Ck3Id, Tag = h.Tag, State = "alive" });
        return result;
    }

    // ================================================================================================================ snapshot -> request

    private sealed class Prepared
    {
        public required MapperOptions Options;
        public required BattleRequest Request;
        public required string Json;
        public required string? SaveUsed;
        public required bool SaveIsThePlayers;
    }

    /// <summary>What the orchestrator's PrepareAsync does: BuildRequest, preload on, Enrich, then BannerlordLink writes request.json.</summary>
    private static Prepared Prepare()
    {
        var paths = P.Value;
        var battle = Log.Value.Battle3;
        var options = new BridgeConfig().ToMapperOptions(paths.Data);

        // Appearance from the user's newest save (read-only). The fixture's player is the debug campaign's player; when that save is
        // present, the battle is moved to its date and age so the evidence rule of PROTOCOL §7.0 can accept it.
        using var provider = new AppearanceProvider { DataDirectory = paths.Data };
        provider.Start("", paths.Ck3Game ?? "", Path.Combine(paths.Temp, "cache"));
        string? save = null;
        if (Directory.Exists(paths.Ck3UserDir))
        {
            using var watcher = new SaveWatcher(paths.Ck3UserDir, _ => true, _ => { });
            save = watcher.ResolveTarget();
        }
        var state = save is not null ? provider.LoadSaveNow(save) : null;
        bool savePlayer = state?.Meta.Player?.Id == PlayerId && state.Meta.Date is not null;
        if (savePlayer)
        {
            var date = state!.Meta.Date!.Value;
            (battle.Year, battle.Month, battle.Day) = (date.Year, date.Month, date.Day);
            var me = battle.Defender.Characters.Single(c => c.Ref.Ck3Id == PlayerId);
            me.Age = state.Index?.Character(PlayerId)?.Birth is string birth && CK3Date.Parse(birth) is CK3Date born
                ? (int)Math.Floor(date.AsYears - born.AsYears)
                : (int)Math.Round(state.Meta.Player!.Age * 100);
        }

        var request = new BattleMapper().BuildRequest(battle, options);
        request.Presentation.Preload = true;
        provider.Enrich(battle, request);

        string file = Path.Combine(paths.Temp, ProtocolInfo.RequestFile);
        ProtocolJson.WriteAtomic(file, request);
        var bytes = File.ReadAllBytes(file);
        Expect.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "request.json has no BOM (PROTOCOL header)");
        string json = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        return new Prepared { Options = options, Request = request, Json = json, SaveUsed = save, SaveIsThePlayers = savePlayer };
    }

    private static void SnapshotToRequest()
    {
        var d = Log.Value;
        var p = Prep.Value;
        var req = p.Request;
        var battle = d.Battle3;
        Expect.Equal((ProtocolInfo.Version, "b3_" + SessionKey, "field"), (req.Protocol, req.Id, req.Kind));
        Expect.Null(req.Siege, "sieges stay in CK3");
        Expect.Equal(("defender", true, true), (req.PlayerSide, req.PlayerIsGeneral, req.Presentation.Preload));
        Expect.True(req.Season is "spring" or "summer" or "fall" or "winter", req.Season);
        Expect.True(new[] { 6f, 12f, 15f, 18f, 22f }.Contains(req.TimeOfDay), $"time of day {req.TimeOfDay}");
        Expect.True(req.Presentation.Title.Contains("Orangallu", StringComparison.Ordinal), req.Presentation.Title);
        Expect.True(req.Scale.BattleSize > 0 && req.Scale.MenPerAgent > 0);

        // Heroes are the battle's tagged characters; the player plays himself.
        var tags = battle.Sides.SelectMany(s => s.Characters).ToDictionary(c => c.Tag, c => c.Ref.Ck3Id);
        var heroes = req.Attacker.Heroes.Concat(req.Defender.Heroes).ToList();
        foreach (var h in heroes)
            Expect.True(tags.TryGetValue(h.Tag, out var id) && id == h.Ck3Id, $"hero {h.Key}: tag {h.Tag} / CK3 id {h.Ck3Id} not in the snapshot");
        var player = req.Defender.Heroes.Single(h => h.IsPlayer);
        Expect.Equal(("player", PlayerId, 1), (player.Role, player.Ck3Id, player.Tag));
        Expect.Equal(player.Key, req.Defender.General);
        Expect.Equal(10935L, req.Attacker.Heroes.Single(h => h.Key == req.Attacker.General).Ck3Id);
        Expect.Equal(heroes.Count, heroes.Select(h => h.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "unique hero names");
        foreach (var h in heroes)
        {
            Expect.False(h.Name.IndexOfAny(new[] { '{', '}', '.' }) >= 0, $"hero name '{h.Name}'");
            var a = Expect.NotNull(h.Appearance, $"{h.Key}: appearance");
            Expect.True(new[] { a.Weight, a.Build, a.Height, a.Skin, a.HairColor, a.EyeColor }.All(v => v is >= 0 and <= 1), $"{h.Key}: look values");
            Expect.NotNull(h.Equipment?.Roster, $"{h.Key}: equipment roster");
        }
        if (p.SaveIsThePlayers)
        {
            Expect.Equal("meta_portrait", player.Appearance.Source, "the player's own CK3 face from the save");
            Expect.Equal("meta:primary_title", req.Defender.Banner?.Source, "the player's coat of arms");
            Expect.True(File.Exists(req.Presentation.DefenderCoaPng), "the player's coat of arms PNG");
        }
        else Expect.True(player.Appearance.Source != "meta_portrait", "no save of this campaign: no portrait from another campaign");
        Console.WriteLine($"      save {(p.SaveUsed is null ? "none" : Path.GetFileName(p.SaveUsed))}{(p.SaveIsThePlayers ? " (the fixture's player)" : "")}; " +
                          $"player look {player.Appearance.Source}; banners {req.Attacker.Banner?.Source ?? "culture"} / {req.Defender.Banner?.Source ?? "culture"}");

        // Troop groups name armies, regiments and knights of the snapshot (they come back as result.json group keys).
        foreach (var (sideReq, side) in new[] { (req.Attacker, battle.Attacker), (req.Defender, battle.Defender) })
        {
            Expect.True(sideReq.Troops.Count > 0 && sideReq.Troops.All(t => t.Count > 0 && !string.IsNullOrEmpty(t.Id)), "troops");
            foreach (var t in sideReq.Troops)
            {
                Expect.True(GroupKey.TryParse(t.Group, out var g), $"group '{t.Group}'");
                switch (g.Kind)
                {
                    case GroupKind.Levy:
                        Expect.True(side.Armies.Any(a => a.Tag == g.ArmyTag && a.Levies > 0), $"{t.Group}: no such army with levies");
                        break;
                    case GroupKind.Maa:
                        var reg = side.Armies.SingleOrDefault(a => a.Tag == g.ArmyTag)?.Regiments.SingleOrDefault(r => r.RegimentId == g.RegimentId);
                        Expect.True(reg is not null && reg.MaaKey == g.MaaKey, $"{t.Group}: no such regiment");
                        break;
                    case GroupKind.Retinue:
                        Expect.True(side.Characters.Any(c => c.Tag == g.CharTag && c.IsKnight), $"{t.Group}: no such knight");
                        break;
                }
            }
        }
        Expect.False(req.Attacker.Troops.Any(t => t.Group!.StartsWith("maa:onager#", StringComparison.Ordinal)), "siege weapons stay off the field");

        // Battle 4 is preloaded too (the bridge acts on every B|END): the player is not on the field, a stand-in leads.
        var req4 = new BattleMapper().BuildRequest(d.Battle4, p.Options);
        Expect.Equal("b4_" + SessionKey, req4.Id);
        Expect.False(req4.Attacker.Heroes.Concat(req4.Defender.Heroes).Any(h => h.Role == "player"), "the war leader at home does not fight");
    }

    // ================================================================================================================ request.json at the module

    private static void RequestJsonAsTheModuleReadsIt()
    {
        var p = Prep.Value;
        using var doc = JsonDocument.Parse(p.Json);
        var root = doc.RootElement;

        var problems = ModuleContract.Check(root, typeof(BattleRequest), p.Request, "$");
        Expect.True(problems.Count == 0, "request.json against the module's contract:\n" + string.Join("\n", problems.Take(20)));

        // RequestIntake.Parse / RequestExtras.From: an explicit preload = true holds the battle until 'go'; battleSize as a number.
        Expect.Equal(JsonValueKind.True, root.GetProperty("presentation").GetProperty("preload").ValueKind, "presentation.preload is the JSON literal true");
        Expect.Equal(JsonValueKind.Number, root.GetProperty("scale").GetProperty("battleSize").ValueKind);
        Expect.Equal(ProtocolInfo.Version, root.GetProperty("protocol").GetInt32());

        var newtonsoft = Newtonsoft.TryLoad(P.Value.BannerlordGame, out var why);
        if (newtonsoft is null)
        {
            Console.WriteLine($"      Bannerlord's Newtonsoft.Json not available ({why}); strict contract reader only");
            return;
        }
        var parsed = newtonsoft.ReadLikeTheModule(p.Json, typeof(BattleRequest));
        var diffs = new List<string>();
        DeepCompare(p.Request, parsed, typeof(BattleRequest), "$", diffs);
        Expect.True(diffs.Count == 0, "the module's Newtonsoft reads a different request:\n" + string.Join("\n", diffs.Take(20)));
        Console.WriteLine($"      {p.Json.Length} B; read by Newtonsoft {newtonsoft.Version} with the module's settings: identical");
    }

    // ================================================================================================================ result.json -> outcome

    private sealed class Returned
    {
        public required Ck3Outcome Outcome;
        public required KeypadPayload Payload;
    }

    private static Returned Return()
    {
        var p = Prep.Value;
        var result = FakeBannerlordResult(p.Request);
        // Written the way the module writes it (BridgeIo.WriteJsonAtomic: Newtonsoft, module settings, UTF-8 without BOM).
        var newtonsoft = Newtonsoft.TryLoad(P.Value.BannerlordGame, out _);
        string json = newtonsoft?.Serialize(result) ?? ModuleContract.SerializeLikeTheModule(result);
        string file = Path.Combine(P.Value.Temp, ProtocolInfo.ResultFile);
        File.WriteAllText(file, json, new UTF8Encoding(false));
        var read = Expect.NotNull(ProtocolJson.TryRead<BattleResult>(file), "the bridge reads result.json");
        var diffs = new List<string>();
        DeepCompare(result, read, typeof(BattleResult), "$", diffs);
        Expect.True(diffs.Count == 0, "the bridge reads a different result:\n" + string.Join("\n", diffs.Take(20)));

        var outcome = new BattleMapper().BuildOutcome(Log.Value.Battle3, p.Request, read, p.Options);
        var warnings = new List<string>();
        var payload = KeypadPayload.Encode(outcome, MaaIndexTable.Load(Path.Combine(P.Value.Data, "ck3_maa_index.json")), warnings);
        Expect.True(warnings.Count == 0, "payload warnings: " + string.Join(" | ", warnings));
        return new Returned { Outcome = outcome, Payload = payload };
    }

    /// <summary>A plausible Bannerlord battle: the defenders (the player's side) win. Attacker groups lose 40 % killed, 8 % wounded and
    /// 20 % routed, defender groups 15 % killed and 4 % wounded. The player kills 6 and fells the enemy general; one enemy knight is
    /// knocked out by the player's knight, the other flees; the player's lowborn knight is killed by the enemy knight.</summary>
    private static BattleResult FakeBannerlordResult(BattleRequest req)
    {
        SideResult Side(SideRequest s, bool won)
        {
            var side = new SideResult();
            foreach (var g in s.Troops.GroupBy(t => t.Group!))
            {
                int n = g.Sum(t => t.Count);
                var gr = new GroupResult
                {
                    Requested = n, Spawned = n, Killed = (int)(n * (won ? 0.15 : 0.40)), Wounded = (int)(n * (won ? 0.04 : 0.08)),
                    Routed = won ? 0 : (int)(n * 0.20),
                };
                side.Groups[g.Key] = gr;
                side.Requested += n; side.Spawned += n; side.Killed += gr.Killed; side.Wounded += gr.Wounded; side.Routed += gr.Routed;
            }
            side.Requested += s.Heroes.Count;
            side.Spawned += s.Heroes.Count;
            return side;
        }
        var result = new BattleResult
        {
            Id = req.Id, Status = "finished", Winner = "defender", DurationSeconds = 412.5f,
            Attacker = Side(req.Attacker, won: false), Defender = Side(req.Defender, won: true),
        };
        HeroSpec By(long ck3Id) => req.Attacker.Heroes.Concat(req.Defender.Heroes).Single(h => h.Ck3Id == ck3Id);
        var player = req.Defender.Heroes.Single(h => h.IsPlayer);
        var states = new Dictionary<long, (string State, int Kills, string? KilledBy)>
        {
            [player.Ck3Id] = ("alive", 6, null),
            [10845] = ("alive", 3, null),                           // Prola, the player's knight
            [23540] = ("killed", 1, By(23611).Key),                 // Rudra, slain by Kesava
            [10935] = ("killed", 2, player.Key),                    // Bhupati, the enemy general, felled by the player
            [23611] = ("wounded", 1, By(10845).Key),                // Kesava, knocked out by Prola
            [16210] = ("routed", 0, null),                          // Mallaya flees
        };
        foreach (var h in req.Attacker.Heroes.Concat(req.Defender.Heroes))
        {
            var (state, kills, killedBy) = states.TryGetValue(h.Ck3Id, out var s) ? s : ("alive", 0, null);
            result.Heroes.Add(new HeroResult { Key = h.Key, Ck3Id = h.Ck3Id, Tag = h.Tag, State = state, Kills = kills, KilledBy = killedBy });
            var side = req.Attacker.Heroes.Contains(h) ? result.Attacker : result.Defender;
            if (state == "killed") side.Killed++;
            else if (state == "wounded") side.Wounded++;
            else if (state == "routed") side.Routed++;
        }
        return result;
    }

    private static void ResultJsonToOutcome()
    {
        var o = Ret.Value.Outcome;
        var battle = Log.Value.Battle3;
        Expect.Equal((3, 1256L, 2, true), (o.Bid, o.ProvinceId, o.OutcomeCode, o.PlayerWon));
        Expect.True(o.EnemyLost > o.MyLost && o.MyLost > 0, $"losses: mine {o.MyLost}, enemy {o.EnemyLost}");
        Expect.Equal(6, o.MyKills);

        var armies = battle.Sides.SelectMany(s => s.Armies).ToDictionary(a => a.Tag);
        Expect.True(o.Armies.Count > 0 && o.Armies.All(a => armies.ContainsKey(a.ArmyTag)), "armies are tagged armies of the battle");
        foreach (var a in o.Armies)
        {
            Expect.True(a.LevyLossPermille is >= 0 and <= 1000, $"army {a.ArmyTag}: levy loss {a.LevyLossPermille}");
            foreach (var m in a.Maa)
            {
                Expect.True(armies[a.ArmyTag].Regiments.Any(r => r.MaaKey == m.MaaKey), $"army {a.ArmyTag}: no {m.MaaKey} regiment");
                Expect.True(m.SurvivorSharePermille is >= 0 and <= 1000, $"army {a.ArmyTag} {m.MaaKey}: share {m.SurvivorSharePermille}");
            }
        }
        Expect.True(o.Armies.Single(a => a.ArmyTag == 7).LevyLossPermille > o.Armies.Single(a => a.ArmyTag == 8).LevyLossPermille, "the loser loses more");

        var sideOf = battle.Sides.SelectMany(s => s.Characters.Select(c => (c.Tag, s.Key))).ToDictionary(x => x.Tag, x => x.Key);
        var fates = o.Characters.ToDictionary(c => c.Tag);
        Expect.True(o.Characters.All(c => sideOf.ContainsKey(c.Tag) && c.Fate is >= 1 and <= 4), "fates of tagged characters only, unharmed ones left out");
        Expect.False(fates.ContainsKey(1), "the player's own hero stood: no fate");
        Expect.True(fates.TryGetValue(5, out var bhupati) && bhupati.Fate is 1 or 2 or 3, "the felled enemy commander is wounded, maimed or captured");
        Expect.True(fates.ContainsKey(6) && fates.ContainsKey(8), "both fallen knights get a CK3 fate");
        Expect.False(fates.ContainsKey(7), "a fleeing knight is unharmed");
        foreach (var c in o.Characters.Where(c => c.KillerTag > 0))
            Expect.True(sideOf.TryGetValue(c.KillerTag, out var ks) && ks != sideOf[c.Tag], $"character {c.Tag}: killer {c.KillerTag} is an enemy");
        Console.WriteLine($"      outcome {o.OutcomeCode}: armies {string.Join(", ", o.Armies.Select(a => $"{a.ArmyTag}:{a.LevyLossPermille}‰+{a.Maa.Count} MaA"))}; " +
                          $"fates {string.Join(", ", o.Characters.Select(c => $"{c.Tag}:{c.Fate}"))}");
    }

    // ================================================================================================================ payload -> the mod

    private static void RegisterMapMatchesTheApplyEffect()
    {
        var apply = Mod.Value.ApplyEffect;
        Expect.Equal(KeypadLayout.ArmySlots, apply.ArmySlots.Count, "army slots");
        for (int i = 0; i < apply.ArmySlots.Count; i++)
        {
            int b = KeypadLayout.ArmyBase + KeypadLayout.ArmyStride * i;
            var slot = apply.ArmySlots[i];
            Expect.Equal((b, b + 1), (slot["TAG"], slot["LEVY"]), $"army slot {i}");
            for (int k = 0; k < KeypadLayout.MaaPerArmy; k++)
                Expect.Equal((b + 2 + 2 * k, b + 3 + 2 * k), (slot[$"T{k + 1}"], slot[$"S{k + 1}"]), $"army slot {i} MaA pair {k}");
        }
        Expect.Equal(KeypadLayout.CharacterSlots, apply.CharacterSlots.Count, "character slots");
        for (int j = 0; j < apply.CharacterSlots.Count; j++)
        {
            int b = KeypadLayout.CharacterBase + KeypadLayout.CharacterStride * j;
            Expect.Equal((b, b + 1, b + 2), (apply.CharacterSlots[j]["TAG"], apply.CharacterSlots[j]["FATE"], apply.CharacterSlots[j]["KILLER"]), $"character slot {j}");
        }
        // Award slots (PROTOCOL §13.5): two blocks of four in registers payload 3 left free.
        Expect.Equal(KeypadLayout.AwardSlots, apply.AwardSlots.Count, "award slots");
        for (int k = 0; k < apply.AwardSlots.Count; k++)
            Expect.Equal((KeypadLayout.AwardSlot(k), KeypadLayout.AwardSlot(k) + 1), (apply.AwardSlots[k]["TAG"], apply.AwardSlots[k]["AWARD"]), $"award slot {k}");
        Expect.Equal((KeypadLayout.Bid, KeypadLayout.Outcome), (apply.BidRegister, apply.OutcomeRegister));
        Expect.Equal((KeypadLayout.MyLost, KeypadLayout.EnemyLost, KeypadLayout.MyKills, KeypadLayout.PrestigeBonus),
            (apply.SummaryRegisters["ck3xbl_res_my_lost"], apply.SummaryRegisters["ck3xbl_res_enemy_lost"], apply.SummaryRegisters["ck3xbl_res_my_kills"],
             apply.PrestigeRegister));

        // The MaA index of the bridge's table is the mod's chain, both ways.
        var table = MaaIndexTable.Load(Path.Combine(P.Value.Data, "ck3_maa_index.json"));
        Expect.Equal(table.Count, apply.MaaByIndex.Count, "MaA types in the chain");
        foreach (var (index, key) in apply.MaaByIndex) Expect.Equal(index, table.IndexOf(key), $"MaA '{key}'");

        // This battle's payload: every slot names an army of the battle and MaA types that army has.
        var r = Ret.Value.Payload.Registers;
        var battle = Log.Value.Battle3;
        int version = Ret.Value.Outcome.PayloadVersion == KeypadLayout.SteerPayloadVersion ? KeypadLayout.SteerPayloadVersion : KeypadLayout.PayloadVersion;
        Expect.Equal((version, 3, 2, 1256), (r[0], r[1], r[2], r[3]));
        var armies = battle.Sides.SelectMany(s => s.Armies).ToDictionary(a => a.Tag);
        foreach (var slot in apply.ArmySlots.Where(s => r[s["TAG"]] > 0))
        {
            Expect.True(armies.TryGetValue(r[slot["TAG"]], out var army), $"register r{slot["TAG"]} = {r[slot["TAG"]]}: not an army of the battle");
            for (int k = 1; k <= KeypadLayout.MaaPerArmy; k++)
                if (r[slot[$"T{k}"]] is int type and > 0)
                    Expect.True(army!.Regiments.Any(x => x.MaaKey == apply.MaaByIndex[type]), $"army {army.Tag}: MaA index {type} ({apply.MaaByIndex[type]})");
        }
        var tags = battle.Sides.SelectMany(s => s.Characters).Select(c => c.Tag).ToHashSet();
        foreach (var slot in apply.CharacterSlots.Where(s => r[s["TAG"]] > 0))
            Expect.True(tags.Contains(r[slot["TAG"]]) && r[slot["FATE"]] is >= 1 and <= 4, $"character slot r{slot["TAG"]}: a harmed character of the battle");
        foreach (var slot in apply.AwardSlots.Where(s => r[s["TAG"]] > 0))
            Expect.True(tags.Contains(r[slot["TAG"]]) && r[slot["AWARD"]] is > 0 and <= 40_203, $"award slot r{slot["TAG"]}: a character of the battle");
    }

    private static void KeypadKeysCommitInTheMod()
    {
        var mod = Mod.Value;
        var payload = Ret.Value.Payload;

        // Each key the bridge presses (by scan code, with Ctrl+Shift+Alt) is a vanilla shortcut on one of the 0002 keypad buttons,
        // whose scripted GUI runs the keypad effect of the same meaning.
        var keyName = new Dictionary<ushort, string> { [ScanCodes.F1] = "f1", [ScanCodes.F2] = "f2", [ScanCodes.F3] = "f3", [ScanCodes.F5] = "f5" };
        for (int digit = 0; digit <= 9; digit++) keyName[ScanCodes.Digit(digit)] = digit.ToString(CultureInfo.InvariantCulture);
        Expect.SequenceEqual(new ushort[] { 0x1D, 0x2A, 0x38 }, ScanCodes.KeypadModifiers, "left Ctrl, Shift, Alt");
        string? vanilla = P.Value.Ck3Game is { } game ? Path.Combine(game, "game", "gui", "shortcuts.shortcuts") : null;
        string? vanillaText = vanilla is not null && File.Exists(vanilla) ? File.ReadAllText(vanilla) : null;
        var actions = new Dictionary<KeypadKey, string>();
        foreach (var key in Enum.GetValues<KeypadKey>())
        {
            string name = keyName[ScanCodes.ForKeypad(key)];
            string shortcut = "_ctrl_shift_alt_" + name;
            if (vanillaText is not null)
                Expect.True(Regex.IsMatch(vanillaText, $@"(?m)^\s*{shortcut} = ""ctrl\+shift\+alt\+{name}"""), $"vanilla defines {shortcut}");
            Expect.True(mod.ButtonGui.TryGetValue(shortcut, out var gui), $"the keypad has a button for {shortcut}");
            Expect.True(mod.ScriptedGuiEffects.TryGetValue(gui!, out var effect), $"scripted GUI {gui}");
            string expected = key switch
            {
                >= KeypadKey.D0 and <= KeypadKey.D9 => $"ck3xbl_keypad_digit_effect = {{ D = {(int)key} }}",
                KeypadKey.Store => "ck3xbl_keypad_store_effect = yes",
                KeypadKey.Commit => "ck3xbl_commit_effect = yes",
                KeypadKey.Reset => "ck3xbl_keypad_reset_effect = yes",
                _ => "ck3xbl_keypad_seek_effect = yes",
            };
            Expect.Equal(expected, effect, $"{key} ({shortcut}, {gui})");
            actions[key] = effect!;
        }

        // The bridge's key sequence, replayed through the mod's own generated keypad script.
        var keys = payload.ToKeySequence();
        var pad = mod.NewKeypad(pendingBid: 3);
        pad.Leftovers();                                       // an earlier attempt's registers: the leading F3 clears them
        foreach (var k in keys) pad.Press(k);
        Expect.SequenceEqual(payload.Registers.Select(v => (long)v), pad.R, "registers after the keys");
        Expect.True(pad.LastCommitOk, $"IN|OK expected; the mod computed checksum {pad.LastSum}, the payload carries {payload.Registers[KeypadLayout.Checksum]}");
        Expect.Equal(1, pad.Ready);
        Expect.Equal((long)payload.Registers[KeypadLayout.Checksum], pad.LastSum, "the mod's ck3xbl_in_sum");

        // A lost or a repeated store key is caught by the mod (IN|BAD), so the bridge resends.
        int store = StoreKeyIndexFor(keys, KeypadLayout.ArmyBase);
        foreach (var (what, broken) in new[]
                 {
                     ("lost F1", keys.Where((_, i) => i != store).ToList()),
                     ("repeated F1", keys.Take(store + 1).Append(KeypadKey.Store).Concat(keys.Skip(store + 1)).ToList()),
                 })
        {
            var bad = mod.NewKeypad(pendingBid: 3);
            foreach (var k in broken) bad.Press(k);
            Expect.False(bad.LastCommitOk, $"{what} must be rejected");
            Expect.Equal(0, bad.Ready, what);
        }
        // A payload for another battle is rejected too.
        var other = mod.NewKeypad(pendingBid: 4);
        foreach (var k in keys) other.Press(k);
        Expect.False(other.LastCommitOk, "payload of battle 3 while battle 4 is pending");
        Console.WriteLine($"      {keys.Count} keys, {payload.NonZero().Count()} registers, checksum {payload.Registers[KeypadLayout.Checksum]} (payload v{payload.Registers[0]})");
    }

    /// <summary>The index of the store key (F1) that writes register <paramref name="register"/> in a key sequence.</summary>
    private static int StoreKeyIndexFor(IReadOnlyList<KeypadKey> keys, int register)
    {
        long acc = 0, idx = 0;
        for (int i = 0; i < keys.Count; i++)
        {
            switch (keys[i])
            {
                case >= KeypadKey.D0 and <= KeypadKey.D9: acc = acc * 10 + (int)keys[i]; break;
                case KeypadKey.Seek: idx = acc; acc = 0; break;
                case KeypadKey.Reset: idx = 0; acc = 0; break;
                case KeypadKey.Store:
                    if (idx == register) return i;
                    idx++; acc = 0;
                    break;
            }
        }
        throw new ExpectationException($"no store key writes r{register}");
    }

    private static void RunFileCommitsInTheMod()
    {
        var mod = Mod.Value;
        var payload = Ret.Value.Payload;
        string text = RunFileRenderer.Render(payload);
        string file = Path.Combine(P.Value.Temp, "run", RunFileRenderer.FileName);
        AtomicFile.WriteAllText(file, text, withBom: true);
        var bytes = File.ReadAllBytes(file);
        Expect.True(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "UTF-8 with BOM (CK3 script files)");

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
        Expect.Equal("hidden_effect = {", lines[0]);
        Expect.Equal("}", lines[^1]);
        Expect.Equal("ck3xbl_keypad_reset_effect = yes", lines[1], "the run file resets the keypad first (PROTOCOL §10.4)");
        foreach (var effect in RunFileRenderer.CalledEffects)
            Expect.True(mod.ScriptedEffects.Contains(effect), $"{effect} is a scripted effect of the mod");

        // Run the body the way CK3 would, through the mod's own keypad script.
        var pad = mod.NewKeypad(pendingBid: 3);
        pad.Leftovers();
        var setVariable = new Regex(@"^set_variable = \{ name = ck3xbl_r(?<n>\d+) value = (?<v>\d+) \}$");
        var callEffect = new Regex(@"^(?<e>[a-z0-9_]+) = yes$");
        bool applied = false;
        foreach (var line in lines.Skip(1).Take(lines.Count - 2))
        {
            if (setVariable.Match(line) is { Success: true } sv)
            {
                int n = int.Parse(sv.Groups["n"].Value, CultureInfo.InvariantCulture);
                Expect.True(n is >= 0 and < KeypadLayout.RegisterCount, $"register r{n}");
                pad.R[n] = long.Parse(sv.Groups["v"].Value, CultureInfo.InvariantCulture);
                continue;
            }
            var call = callEffect.Match(line);
            Expect.True(call.Success, $"unexpected run-file statement: {line}");
            string effect = call.Groups["e"].Value;
            Expect.True(mod.ScriptedEffects.Contains(effect), $"{effect} is not a scripted effect of the mod");
            switch (effect)
            {
                case "ck3xbl_keypad_reset_effect": pad.Reset(); break;
                case "ck3xbl_commit_effect": pad.Commit(); break;
                case "ck3xbl_apply_effect": applied = pad.Ready == 1; break;
                default: throw new ExpectationException($"the run file calls {effect}, which the test does not model");
            }
        }
        Expect.SequenceEqual(payload.Registers.Select(v => (long)v), pad.R, "registers after the run file");
        Expect.True(pad.LastCommitOk && applied, "the run file commits (IN|OK) and the apply effect sees ck3xbl_ready = 1");
    }

    /// <summary>The bridge's result card and notice name the option of event 0002 exactly as the mod's English localization does.</summary>
    /// <summary>Payload 4 from the bridge to the steering (PROTOCOL §14.2, §14.4): the battle's version 4 outcome, encoded and replayed
    /// through the mod's keypad, leaves in the mod's registers exactly what the steering apply reads, where it reads it: the kill
    /// targets and the pace day in ck3xbl_apply_effect, each army's levy survivors and MaA pairs in the slots that
    /// ck3xbl_st_store_slots_effect stores on the armies, the awards and fates in the slots of the apply. Army slots are applied at the
    /// click only for payloads 2 and 3.</summary>
    private static void PayloadV4KeysReachTheSteeringApply()
    {
        var outcome = Ret.Value.Outcome;
        var payload = Ret.Value.Payload;
        Expect.Equal(KeypadLayout.SteerPayloadVersion, outcome.PayloadVersion, "the bridge's default outcome is payload 4");
        var pad = Mod.Value.NewKeypad(pendingBid: 3);
        foreach (var k in payload.ToKeySequence()) pad.Press(k);
        Expect.True(pad.LastCommitOk, "IN|OK");
        long R(int register) => pad.R[register];

        string dir = Path.Combine(P.Value.Mod, "common", "scripted_effects");
        string apply = ModScripts.Block(File.ReadAllText(Path.Combine(dir, "ck3xbl_gen_apply_effects.txt")), "ck3xbl_apply_effect");
        int Reads(string scopeValue)
        {
            var m = Regex.Match(apply, $@"name = {scopeValue} value = var:ck3xbl_r(?<r>\d+) \}}");
            Expect.True(m.Success, $"ck3xbl_apply_effect does not save {scopeValue} from a register");
            return int.Parse(m.Groups["r"].Value, CultureInfo.InvariantCulture);
        }
        Expect.Equal((KeypadLayout.AttackerKillTarget, KeypadLayout.DefenderKillTarget, KeypadLayout.SteerHint),
            (Reads("ck3xbl_st_att_target"), Reads("ck3xbl_st_def_target"), Reads("ck3xbl_st_hint")), "the steering registers of the apply");
        Expect.Equal(((long)outcome.AttackerKillTarget, (long)outcome.DefenderKillTarget, 12L),
            (R(KeypadLayout.AttackerKillTarget), R(KeypadLayout.DefenderKillTarget), R(KeypadLayout.SteerHint)), "kill targets and pace day in the mod");
        Expect.True(Regex.IsMatch(apply, @"limit = \{ NOT = \{ var:ck3xbl_r0 = 4 \} \}\s*ck3xbl_apply_army_slot_effect"),
            "army slots are applied at the click only for payloads 2 and 3");

        string slots = ModScripts.Block(File.ReadAllText(Path.Combine(dir, "ck3xbl_gen_steer_slot_effects.txt")), "ck3xbl_st_store_slots_effect");
        var stored = Regex.Matches(slots, @"ck3xbl_st_store_slot_effect = \{(?<args>[^}]*)\}")
            .Select(m => Regex.Matches(m.Groups["args"].Value, @"(?<p>\w+) = ck3xbl_r(?<r>\d+)")
                .ToDictionary(a => a.Groups["p"].Value, a => int.Parse(a.Groups["r"].Value, CultureInfo.InvariantCulture)))
            .ToList();
        Expect.Equal(KeypadLayout.ArmySlots, stored.Count, "stored army slots");
        Expect.SequenceEqual(Mod.Value.ApplyEffect.ArmySlots.Select(s => string.Join(",", s.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))),
            stored.Select(s => string.Join(",", s.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))), "the same register map as the v3 army slots");
        Expect.True(outcome.Armies.Count is > 0 and <= KeypadLayout.ArmySlots, $"{outcome.Armies.Count} armies");
        for (int i = 0; i < outcome.Armies.Count; i++)
        {
            var army = outcome.Armies[i];
            var slot = stored[i];
            Expect.Equal(((long)army.ArmyTag, (long)army.LevySurvivors), (R(slot["TAG"]), R(slot["LEVY"])), $"slot {i}: tag and levy survivors");
            for (int k = 0; k < KeypadLayout.MaaPerArmy; k++)
            {
                var pair = k < army.Maa.Count ? army.Maa[k] : null;
                Expect.Equal(((long)(pair?.MaaIndex ?? 0), (long)(pair?.SurvivorSharePermille ?? 0)), (R(slot[$"T{k + 1}"]), R(slot[$"S{k + 1}"])),
                    $"army {army.ArmyTag} MaA pair {k}");
                if (pair is not null) Expect.Equal(pair.MaaKey, Mod.Value.ApplyEffect.MaaByIndex[pair.MaaIndex], $"army {army.ArmyTag}: the mod's MaA chain");
            }
        }
        var harmed = outcome.Characters.Where(c => c.Fate > 0).ToList();
        for (int j = 0; j < harmed.Count; j++)
        {
            var slot = Mod.Value.ApplyEffect.CharacterSlots[j];
            Expect.Equal(((long)harmed[j].Tag, (long)harmed[j].Fate), (R(slot["TAG"]), R(slot["FATE"])), $"character slot {j}");
        }
        for (int k = 0; k < Math.Min(outcome.Awards.Count, KeypadLayout.AwardSlots); k++)
            Expect.Equal((long)outcome.Awards[k].Tag, R(Mod.Value.ApplyEffect.AwardSlots[k]["TAG"]), $"award slot {k}");
        Console.WriteLine($"      payload 4: targets {outcome.AttackerKillTarget}/{outcome.DefenderKillTarget}, pace {R(KeypadLayout.SteerHint)}, " +
                          $"{outcome.Armies.Count} stored army slots, {harmed.Count} fates, {outcome.Awards.Count} awards");
    }

    private static void DustSettlesOptionIsTheLocText()
    {
        string loc = Path.Combine(P.Value.Mod, "localization", "english", "ck3xbl_l_english.yml");
        var line = Regex.Match(File.ReadAllText(loc, Encoding.UTF8), @"(?m)^\s*ck3xbl_battle\.0002\.a:\d*\s+""(?<text>[^""]*)""");
        Expect.True(line.Success, $"ck3xbl_battle.0002.a not found in {loc}");
        Expect.Equal(line.Groups["text"].Value, Ck3Texts.DustSettlesOption);
    }

    /// <summary>The payload 4 registers of the bridge (PROTOCOL §14.2) are the ones of the mod's register map, tools/gen/lib/registers.mjs
    /// (REG.steerPayloadVersion, REG.maxPayloadVersion, REG.killTarget { attacker, defender }, REG.steerHint), which the generated commit
    /// and apply use.</summary>
    private static void PayloadV4RegistersMatchRegistersMjs()
    {
        string file = Path.Combine(Directory.GetParent(P.Value.Bridge)!.FullName, "tools", "gen", "lib", "registers.mjs");
        string text = File.ReadAllText(file);
        int Number(string pattern, string what)
        {
            var m = Regex.Match(text, pattern);
            Expect.True(m.Success, $"{what} not found in {file}");
            return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        Expect.Equal(KeypadLayout.SteerPayloadVersion, Number(@"\bsteerPayloadVersion:\s*(\d+)", "steerPayloadVersion"));
        Expect.Equal(KeypadLayout.SteerPayloadVersion, Number(@"\bmaxPayloadVersion:\s*(\d+)", "maxPayloadVersion"));
        Expect.Equal(KeypadLayout.AttackerKillTarget, Number(@"\bkillTarget:\s*\{\s*attacker:\s*(\d+)", "killTarget.attacker"));
        Expect.Equal(KeypadLayout.DefenderKillTarget, Number(@"\bkillTarget:\s*\{[^}]*\bdefender:\s*(\d+)", "killTarget.defender"));
        Expect.Equal(KeypadLayout.SteerHint, Number(@"\bsteerHint:\s*(\d+)", "steerHint"));
    }

    // ================================================================================================================ a real game.log (opt-in)

    /// <summary>Diagnostic for the in-game tests: decodes a real CK3 log with the bridge's own parser and
    /// prints every event, each snapshot's armies and characters, the request the mapper would build, and the size of the scope
    /// dumps. Opt-in: set CK3XBL_GAME_LOG to the path of logs/game.log. Read-only (shared read, CK3 may be running).</summary>
    private static void DecodeLiveGameLog()
    {
        string? path = Environment.GetEnvironmentVariable("CK3XBL_GAME_LOG");
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.WriteLine("      set CK3XBL_GAME_LOG to a CK3 logs/game.log to decode it: skipped");
            return;
        }
        var lines = new List<string>();
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(fs, Encoding.UTF8))
            while (reader.ReadLine() is { } line) lines.Add(line);

        // Size of the info_log_scopes dumps: continuation lines after each "[..][I][effectimpl.cpp:..]" header.
        int dumps = 0, dumpLines = 0, largest = 0, current = -1;
        foreach (var line in lines)
        {
            if (line.StartsWith('['))
            {
                if (current >= 0) { dumpLines += current; largest = Math.Max(largest, current); }
                current = line.Contains("][effectimpl.cpp:", StringComparison.Ordinal) ? 0 : -1;
                if (current == 0) dumps++;
            }
            else if (current >= 0) current++;
        }
        if (current >= 0) { dumpLines += current; largest = Math.Max(largest, current); }

        var events = new List<Ck3Event>();
        var warnings = new List<string>();
        var decoder = new Ck3EventDecoder(events.Add, warnings.Add, "live");
        var parser = new Ck3RecordParser(decoder.Accept);
        parser.Feed(lines);
        parser.Flush();
        decoder.Flush();
        Console.WriteLine($"      {path}: {lines.Count} lines, {lines.Count(l => l.Contains("CK3XBL|", StringComparison.Ordinal))} CK3XBL records, " +
                          $"{dumps} dumps ({dumpLines} continuation lines, largest {largest})");
        var options = new BridgeConfig().ToMapperOptions(P.Value.Data);
        foreach (var e in events)
        {
            Console.WriteLine($"      [{e.LogTime}] {Describe(e)}");
            if (e is not SnapshotCompleted { Snapshot: var s }) continue;
            Console.WriteLine($"        {s.CombatName} at {s.Location.ProvinceName} ({s.Location.ProvinceId}), {s.Year}.{s.Month}.{s.Day}, " +
                              $"player {s.PlayerCk3Id}, facts {string.Join(" ", s.Location.Facts.Select(f => f.Key + "=" + f.Value))}");
            foreach (var side in s.Sides)
            {
                Console.WriteLine($"        {side.Key}: {side.Size} men, player in {side.PlayerIn}, primary {side.Primary?.Name}");
                foreach (var a in side.Armies)
                    Console.WriteLine($"          army {a.Tag} '{a.Name}' {a.Size} ({a.Levies} levies) cmd {a.Commander?.Name ?? "-"}, {a.OwnerCulture.Culture}: " +
                                      string.Join(", ", a.Regiments.Select(r => $"{r.MaaKey} {r.Current}/{r.Max}{(r.Hired ? " hired" : "")}")));
                foreach (var c in side.Characters)
                    Console.WriteLine($"          {c.Role} tag {c.Tag} '{c.Ref.Name}' ({c.Ref.Ck3Id}) age {c.Age} prowess {c.Prowess} army {c.ArmyTag} " +
                                      $"{c.Culture.Culture}/{c.Ethnicity} traits [{string.Join(",", c.Traits.Concat(c.CommanderTraits))}]");
                Expect.True(side.Armies.All(a => a.Tag > 0) && side.Characters.All(c => c.Tag > 0), $"battle {s.Bid} {side.Key}: untagged army or character");
            }
            var request = new BattleMapper().BuildRequest(s, options);
            Console.WriteLine($"        request {request.Id}: scene {request.Scene}, {request.Season} {request.TimeOfDay}h, player {request.PlayerSide}, " +
                              $"{request.Attacker.Troops.Sum(t => t.Count)} vs {request.Defender.Troops.Sum(t => t.Count)} agents, " +
                              $"{request.Attacker.Heroes.Count + request.Defender.Heroes.Count} heroes");
            if (request.Balance is { } bal && bal.Ck3Defender > 0 && bal.BannerlordDefender > 0)
                Console.WriteLine(FormattableString.Invariant(
                    $"          balance: CK3 {bal.Ck3Attacker / bal.Ck3Defender:0.###}, Bannerlord {bal.BannerlordAttacker / bal.BannerlordDefender:0.###} -> {bal.Boosted ?? "no change"} x{bal.Factor:0.###}{(bal.Capped ? " (capped)" : "")}"));
        }
        foreach (var w in warnings) Console.WriteLine("      WARNING " + w);
        Expect.True(warnings.Count == 0, $"{warnings.Count} decoder warnings (listed above)");
    }

    // ================================================================================================================ the CK3 mod's scripts

    /// <summary>Facts read from the mod's script files (generated and hand-written), with no game needed.</summary>
    private sealed class ModScripts
    {
        public required Dictionary<string, HashSet<string>> Literals;      // "B|BEGIN" -> files (relative to the mod) holding it
        public required HashSet<string> SavedNames;                         // save_scope_as / save_scope_value_as / temporary names
        public required HashSet<string> ScriptedEffects;
        public required Dictionary<string, string> ScriptedGuiEffects;      // scripted GUI -> its effect statement
        public required Dictionary<string, string> ButtonGui;               // keypad shortcut -> scripted GUI
        public required ApplyMap ApplyEffect;
        public required KeypadScript Keypad;

        public KeypadState NewKeypad(int pendingBid) => new(Keypad, pendingBid);

        public static ModScripts Load(string dir)
        {
            var files = Directory.EnumerateFiles(dir, "*.txt", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(dir, "*.gui", SearchOption.AllDirectories))
                .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), f => File.ReadAllText(f), StringComparer.Ordinal);

            var literals = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var saved = new HashSet<string>(StringComparer.Ordinal);
            var effects = new HashSet<string>(StringComparer.Ordinal);
            var infoLog = new Regex("\\binfo_log = \"CK3XBL\\|(?<rec>[^\"]*)\"");
            var save = new Regex(@"\bsave_(?:temporary_)?scope_as = (?<n>\w+)|\bsave_(?:temporary_)?scope_value_as = \{\s*name = (?<n>\w+)");
            var topLevel = new Regex(@"(?m)^(?<k>[A-Za-z0-9_.]+) = \{");
            foreach (var (rel, text) in files)
            {
                foreach (Match m in infoLog.Matches(text))
                {
                    if (!literals.TryGetValue(m.Groups["rec"].Value, out var set)) literals[m.Groups["rec"].Value] = set = new(StringComparer.Ordinal);
                    set.Add(rel);
                }
                foreach (Match m in save.Matches(text)) saved.Add(m.Groups["n"].Value);
                if (rel.StartsWith("common/scripted_effects/", StringComparison.Ordinal))
                    foreach (Match m in topLevel.Matches(text)) effects.Add(m.Groups["k"].Value);
            }

            var guis = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(files["common/scripted_guis/ck3xbl_gen_keypad_sguis.txt"],
                         @"(?m)^(?<g>\w+) = \{ scope = character effect = \{ (?<e>.*) \} \}\s*$"))
                guis[m.Groups["g"].Value] = m.Groups["e"].Value;
            var buttons = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(files["gui/event_window_widgets/ck3xbl_keypad.gui"],
                         @"shortcut = ""(?<s>\w+)"" onclick = ""\[GetScriptedGui\('(?<g>\w+)'\)"))
                buttons[m.Groups["s"].Value] = m.Groups["g"].Value;

            return new ModScripts
            {
                Literals = literals, SavedNames = saved, ScriptedEffects = effects, ScriptedGuiEffects = guis, ButtonGui = buttons,
                ApplyEffect = ApplyMap.Parse(files["common/scripted_effects/ck3xbl_gen_apply_effects.txt"]),
                Keypad = KeypadScript.Parse(files["common/scripted_effects/ck3xbl_gen_keypad_effects.txt"]),
            };
        }

        /// <summary>The text of a top-level block <c>name = { ... }</c> (generated files carry no braces in strings or comments).</summary>
        public static string Block(string text, string name)
        {
            var m = Regex.Match(text, $@"(?m)^{Regex.Escape(name)} = \{{");
            Expect.True(m.Success, $"block {name} not found");
            int depth = 0;
            for (int i = m.Index + m.Length - 1; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return text[m.Index..(i + 1)];
            }
            throw new ExpectationException($"block {name} is not closed");
        }
    }

    /// <summary>The register map ck3xbl_apply_effect reads (ck3xbl_gen_apply_effects.txt).</summary>
    private sealed class ApplyMap
    {
        public required List<Dictionary<string, int>> ArmySlots, CharacterSlots, AwardSlots;   // parameter (TAG, LEVY, T1..S6 / TAG, FATE, KILLER /
                                                                                              // TAG, AWARD) -> register
        public required Dictionary<int, string> MaaByIndex;
        public required int BidRegister, OutcomeRegister, PrestigeRegister;
        public required Dictionary<string, int> SummaryRegisters;                   // player variable -> register

        public static ApplyMap Parse(string text)
        {
            var apply = ModScripts.Block(text, "ck3xbl_apply_effect");
            List<Dictionary<string, int>> Slots(string effect) => Regex.Matches(apply, $@"{effect} = \{{(?<args>[^}}]*)\}}")
                .Select(m => Regex.Matches(m.Groups["args"].Value, @"(?<p>\w+) = ck3xbl_r(?<r>\d+)")
                    .ToDictionary(a => a.Groups["p"].Value, a => int.Parse(a.Groups["r"].Value, CultureInfo.InvariantCulture)))
                .ToList();
            int Register(string pattern) => int.Parse(Regex.Match(text, pattern).Groups["r"].Value, CultureInfo.InvariantCulture);
            var summary = ModScripts.Block(text, "ck3xbl_apply_summary_effect");
            return new ApplyMap
            {
                ArmySlots = Slots("ck3xbl_apply_army_slot_effect"),
                CharacterSlots = Slots("ck3xbl_apply_char_slot_effect"),
                AwardSlots = Slots("ck3xbl_apply_award_slot_effect"),
                MaaByIndex = Regex.Matches(ModScripts.Block(text, "ck3xbl_apply_maa_pair_effect"),
                        @"root\.var:\$TYPE\$ = (?<i>\d+) \} every_army_maa_regiment = \{ limit = \{ is_maa_type = (?<k>\w+) \}")
                    .ToDictionary(m => int.Parse(m.Groups["i"].Value, CultureInfo.InvariantCulture), m => m.Groups["k"].Value),
                BidRegister = Register(@"name = ck3xbl_bid value = var:ck3xbl_r(?<r>\d+)"),
                OutcomeRegister = Register(@"name = ck3xbl_res_outcome value = var:ck3xbl_r(?<r>\d+)"),
                PrestigeRegister = int.Parse(Regex.Match(summary, @"name = ck3xbl_res_prestige value = \{ value = var:ck3xbl_r(?<r>\d+) modulo = 226 \}").Groups["r"].Value, CultureInfo.InvariantCulture),
                // Plain registers, and r6 read as kills modulo 1000 with the second feat mask above (PROTOCOL §45).
                SummaryRegisters = Regex.Matches(summary, @"name = (?<v>ck3xbl_res_\w+) value = (\{ value = )?var:ck3xbl_r(?<r>\d+)")
                    .ToDictionary(m => m.Groups["v"].Value, m => int.Parse(m.Groups["r"].Value, CultureInfo.InvariantCulture)),
            };
        }
    }

    /// <summary>The semantics of the generated keypad effects (ck3xbl_gen_keypad_effects.txt), read from the script itself.</summary>
    private sealed class KeypadScript
    {
        public required long DigitOverflowAt;                                  // a digit with acc >= this flags overflow
        public required Dictionary<long, int> StoreTargets;                     // idx -> register written by F1
        public required HashSet<int> ResetRegisters;                            // registers F3 sets to 0
        public required int LegacyVersion;                                      // r0 value whose checksum is the plain sum
        public required HashSet<long> AcceptedVersions;
        public required List<(int Reg, long Mod)> PlainTerms;
        public required List<(int Reg, long Mod, long Weight, long OuterMod)> WeightedTerms;
        public required int ChecksumRegister, BidRegister;

        public static KeypadScript Parse(string text)
        {
            long Num(Group g) => long.Parse(g.Value, CultureInfo.InvariantCulture);
            var digit = ModScripts.Block(text, "ck3xbl_keypad_digit_effect");
            var store = ModScripts.Block(text, "ck3xbl_keypad_store_effect");
            var reset = ModScripts.Block(text, "ck3xbl_keypad_reset_effect");
            var commit = ModScripts.Block(text, "ck3xbl_commit_effect");
            var verify = Regex.Match(commit, @"limit = \{\s*OR = \{ (?<versions>(?:var:ck3xbl_r0 = \d+ ?)+)\}\s*var:ck3xbl_r(?<sum>\d+) = scope:ck3xbl_in_sum.*?var:ck3xbl_r(?<bid>\d+) = global_var:ck3xbl_pending_bid",
                RegexOptions.Singleline);
            Expect.True(verify.Success, "the commit's verification block");
            return new KeypadScript
            {
                DigitOverflowAt = Num(Regex.Match(digit, @"var:ck3xbl_acc >= (?<n>\d+)").Groups["n"]),
                StoreTargets = Regex.Matches(store, @"var:ck3xbl_idx = (?<i>\d+) \} set_variable = \{ name = ck3xbl_r(?<r>\d+) value = var:ck3xbl_acc \}")
                    .ToDictionary(m => Num(m.Groups["i"]), m => (int)Num(m.Groups["r"])),
                ResetRegisters = Regex.Matches(reset, @"set_variable = \{ name = ck3xbl_r(?<r>\d+) value = 0 \}").Select(m => (int)Num(m.Groups["r"])).ToHashSet(),
                LegacyVersion = (int)Num(Regex.Match(commit, @"limit = \{ var:ck3xbl_r0 = (?<v>\d+) \}").Groups["v"]),
                AcceptedVersions = Regex.Matches(verify.Groups["versions"].Value, @"= (?<v>\d+)").Select(m => Num(m.Groups["v"])).ToHashSet(),
                PlainTerms = Regex.Matches(commit, @"(?m)^\s*add = var:ck3xbl_r(?<r>\d+) modulo = (?<m>\d+)\s*$")
                    .Select(m => ((int)Num(m.Groups["r"]), Num(m.Groups["m"]))).ToList(),
                WeightedTerms = Regex.Matches(commit, @"add = \{ value = var:ck3xbl_r(?<r>\d+) modulo = (?<m>\d+) multiply = (?<w>\d+) \} modulo = (?<o>\d+)")
                    .Select(m => ((int)Num(m.Groups["r"]), Num(m.Groups["m"]), Num(m.Groups["w"]), Num(m.Groups["o"]))).ToList(),
                ChecksumRegister = (int)Num(verify.Groups["sum"]),
                BidRegister = (int)Num(verify.Groups["bid"]),
            };
        }
    }

    /// <summary>Player variables of the keypad while the mod runs it.</summary>
    private sealed class KeypadState(KeypadScript script, int pendingBid)
    {
        public readonly long[] R = new long[KeypadLayout.RegisterCount];
        public long Acc, Idx;
        public bool Overflow;
        public int Ready;
        public bool LastCommitOk;
        public long LastSum = -1;

        /// <summary>Registers left by an earlier, interrupted attempt.</summary>
        public void Leftovers()
        {
            R[50] = 777; R[KeypadLayout.Checksum] = 1; Idx = 42; Acc = 9;
        }

        public void Press(KeypadKey key)
        {
            switch (key)
            {
                case >= KeypadKey.D0 and <= KeypadKey.D9:
                    if (Acc >= script.DigitOverflowAt) { Overflow = true; Acc = (int)key; }
                    else Acc = Acc * 10 + (int)key;
                    break;
                case KeypadKey.Store:
                    if (script.StoreTargets.TryGetValue(Idx, out int reg)) R[reg] = Acc;
                    Idx++;
                    Acc = 0;
                    break;
                case KeypadKey.Seek: Idx = Acc; Acc = 0; break;
                case KeypadKey.Reset: Reset(); break;
                case KeypadKey.Commit: Commit(); break;
            }
        }

        public void Reset()
        {
            foreach (int reg in script.ResetRegisters) R[reg] = 0;
            Idx = 0; Acc = 0; Overflow = false;
            if (Ready == 1) Ready = 0;
        }

        public void Commit()
        {
            long sum = 0;
            if (R[0] == script.LegacyVersion)
                foreach (var (reg, mod) in script.PlainTerms) sum = (sum + R[reg]) % mod;
            else
                foreach (var (reg, mod, weight, outer) in script.WeightedTerms) sum = (sum + R[reg] % mod * weight) % outer;
            LastSum = sum;
            LastCommitOk = script.AcceptedVersions.Contains(R[0]) && R[script.ChecksumRegister] == sum && !Overflow &&
                           pendingBid > 0 && R[script.BidRegister] == pendingBid;
            if (LastCommitOk) Ready = 1;
        }
    }

    // ================================================================================================================ the module's JSON

    /// <summary>The module's JSON contract (bannerlord_module/CK3Bridge/src/Core/ProtocolJson.cs): Newtonsoft's DefaultContractResolver
    /// with CamelCaseNamingStrategy(processDictionaryKeys: false, overrideSpecifiedNames: true) over the public fields of
    /// shared/Protocol.cs. Stricter than Newtonsoft, which also accepts names in another case and ignores unknown members: here every
    /// member must carry exactly the module's name and a JSON type its field accepts, every field must be present, dictionary keys
    /// must be the sender's keys verbatim, and every value must equal the sender's.</summary>
    private static class ModuleContract
    {
        public static List<string> Check(JsonElement e, Type type, object? expected, string path)
        {
            var problems = new List<string>();
            Walk(e, type, expected, path, problems);
            return problems;
        }

        private static void Walk(JsonElement e, Type type, object? expected, string path, List<string> problems)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            if (e.ValueKind == JsonValueKind.Null)
            {
                if (expected is not null) problems.Add($"{path}: null, expected {expected}");
                if (type.IsValueType && underlying is null) problems.Add($"{path}: null for a {type.Name}");
                return;
            }
            type = underlying ?? type;
            if (type == typeof(string))
            {
                if (e.ValueKind != JsonValueKind.String) problems.Add($"{path}: {e.ValueKind}, the module expects a string");
                else if (!Equals(e.GetString(), expected)) problems.Add($"{path}: '{e.GetString()}' != '{expected}'");
            }
            else if (type == typeof(bool))
            {
                if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) problems.Add($"{path}: {e.ValueKind}, the module expects a boolean");
                else if (!Equals(e.GetBoolean(), expected)) problems.Add($"{path}: {e.GetBoolean()} != {expected}");
            }
            else if (type == typeof(int) || type == typeof(long))
            {
                if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt64(out long v)) problems.Add($"{path}: {e.GetRawText()} is not an integer");
                else if (v != Convert.ToInt64(expected, CultureInfo.InvariantCulture)) problems.Add($"{path}: {v} != {expected}");
            }
            else if (type == typeof(float) || type == typeof(double))
            {
                if (e.ValueKind != JsonValueKind.Number) problems.Add($"{path}: {e.ValueKind}, the module expects a number");
                else if (type == typeof(float) ? (float)e.GetDouble() != (float)expected! : e.GetDouble() != (double)expected!)
                    problems.Add($"{path}: {e.GetRawText()} != {expected}");
            }
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var valueType = type.GetGenericArguments()[1];
                var dict = (System.Collections.IDictionary)expected!;
                if (e.ValueKind != JsonValueKind.Object) { problems.Add($"{path}: {e.ValueKind}, the module expects an object"); return; }
                var keys = e.EnumerateObject().Select(p => p.Name).ToList();
                var sent = dict.Keys.Cast<string>().ToList();
                if (!keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(sent.OrderBy(k => k, StringComparer.Ordinal), StringComparer.Ordinal))
                    problems.Add($"{path}: keys [{string.Join(",", keys)}] are not the sender's [{string.Join(",", sent)}] verbatim");
                foreach (var p in e.EnumerateObject())
                    if (dict.Contains(p.Name)) Walk(p.Value, valueType, dict[p.Name], $"{path}.{p.Name}", problems);
            }
            else if (type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)))
            {
                var itemType = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
                var list = ((System.Collections.IEnumerable)expected!).Cast<object?>().ToList();
                if (e.ValueKind != JsonValueKind.Array) { problems.Add($"{path}: {e.ValueKind}, the module expects an array"); return; }
                var items = e.EnumerateArray().ToList();
                if (items.Count != list.Count) { problems.Add($"{path}: {items.Count} items, sent {list.Count}"); return; }
                for (int i = 0; i < items.Count; i++) Walk(items[i], itemType, list[i], $"{path}[{i}]", problems);
            }
            else if (type.IsClass)
            {
                if (e.ValueKind != JsonValueKind.Object) { problems.Add($"{path}: {e.ValueKind}, the module expects an object"); return; }
                if (expected is null) { problems.Add($"{path}: an object where the sender had null"); return; }
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance).ToDictionary(f => NewtonsoftCamelCase(f.Name), StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject())
                {
                    if (!fields.TryGetValue(p.Name, out var field)) { problems.Add($"{path}.{p.Name}: not a member of {type.Name} (the module would drop it)"); continue; }
                    seen.Add(p.Name);
                    Walk(p.Value, field.FieldType, field.GetValue(expected), $"{path}.{p.Name}", problems);
                }
                foreach (var name in fields.Keys.Where(n => !seen.Contains(n))) problems.Add($"{path}.{name}: missing");
            }
            else problems.Add($"{path}: {type.Name} is not a protocol type");
        }

        /// <summary>Newtonsoft.Json's StringUtils.ToCamelCase, which CamelCaseNamingStrategy applies to member names.</summary>
        public static string NewtonsoftCamelCase(string s)
        {
            if (string.IsNullOrEmpty(s) || !char.IsUpper(s[0])) return s;
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (i == 1 && !char.IsUpper(chars[i])) break;
                bool hasNext = i + 1 < chars.Length;
                if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
                {
                    if (char.IsSeparator(chars[i + 1])) chars[i] = char.ToLowerInvariant(chars[i]);
                    break;
                }
                chars[i] = char.ToLowerInvariant(chars[i]);
            }
            return new string(chars);
        }

        /// <summary>Without Bannerlord's Newtonsoft: the module's output shape (camelCase members, verbatim dictionary keys, indented).</summary>
        public static string SerializeLikeTheModule(object value) => JsonSerializer.Serialize(value, value.GetType(), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DictionaryKeyPolicy = null, IncludeFields = true, WriteIndented = true,
        });
    }

    /// <summary>Bannerlord's own Newtonsoft.Json (bin/Win64_Shipping_Client, read at runtime, never copied), driven by reflection with
    /// the module's settings and the exact read path of RequestIntake.Parse (JObject.Load with DateParseHandling.None, then
    /// ToObject with a serializer of those settings).</summary>
    private sealed class Newtonsoft
    {
        private static readonly object Gate = new();
        private static Newtonsoft? _loaded;
        private static string? _failure;

        private readonly Assembly _asm;
        private readonly object _settings, _serializer;

        private Newtonsoft(Assembly asm)
        {
            _asm = asm;
            Type T(string name) => asm.GetType(name, throwOnError: true)!;
            object Enum(string type, string value) => System.Enum.Parse(T(type), value);
            var settingsType = T("Newtonsoft.Json.JsonSerializerSettings");
            _settings = Activator.CreateInstance(settingsType)!;
            var resolver = Activator.CreateInstance(T("Newtonsoft.Json.Serialization.DefaultContractResolver"))!;
            var strategy = Activator.CreateInstance(T("Newtonsoft.Json.Serialization.CamelCaseNamingStrategy"), false, true)!;
            resolver.GetType().GetProperty("NamingStrategy")!.SetValue(resolver, strategy);
            void Set(string property, object? value) => settingsType.GetProperty(property)!.SetValue(_settings, value);
            Set("ContractResolver", resolver);
            Set("Formatting", Enum("Newtonsoft.Json.Formatting", "Indented"));
            Set("MissingMemberHandling", Enum("Newtonsoft.Json.MissingMemberHandling", "Ignore"));
            Set("NullValueHandling", Enum("Newtonsoft.Json.NullValueHandling", "Include"));
            Set("DateParseHandling", Enum("Newtonsoft.Json.DateParseHandling", "None"));
            Set("FloatParseHandling", Enum("Newtonsoft.Json.FloatParseHandling", "Double"));
            Set("Culture", CultureInfo.InvariantCulture);
            _serializer = T("Newtonsoft.Json.JsonSerializer").GetMethod("Create", new[] { settingsType })!.Invoke(null, new[] { _settings })!;
        }

        public string Version => _asm.GetName().Version?.ToString() ?? "?";

        public static Newtonsoft? TryLoad(string? bannerlordDir, out string why)
        {
            lock (Gate)
            {
                if (_loaded is null && _failure is null)
                {
                    string? dll = bannerlordDir is null ? null : Path.Combine(bannerlordDir, "bin", "Win64_Shipping_Client", "Newtonsoft.Json.dll");
                    try
                    {
                        if (dll is null || !File.Exists(dll)) _failure = "Bannerlord install not found";
                        else _loaded = new Newtonsoft(Assembly.LoadFrom(dll));
                    }
                    catch (Exception e) { _failure = $"{e.GetType().Name}: {e.Message}"; }
                }
                why = _failure ?? "";
                return _loaded;
            }
        }

        public object ReadLikeTheModule(string json, Type type)
        {
            Type T(string name) => _asm.GetType(name, throwOnError: true)!;
            var reader = Activator.CreateInstance(T("Newtonsoft.Json.JsonTextReader"), new StringReader(json))!;
            reader.GetType().GetProperty("DateParseHandling")!.SetValue(reader, System.Enum.Parse(T("Newtonsoft.Json.DateParseHandling"), "None"));
            var root = T("Newtonsoft.Json.Linq.JObject").GetMethod("Load", new[] { T("Newtonsoft.Json.JsonReader") })!.Invoke(null, new[] { reader })!;
            return T("Newtonsoft.Json.Linq.JToken").GetMethod("ToObject", new[] { typeof(Type), T("Newtonsoft.Json.JsonSerializer") })!
                .Invoke(root, new object[] { type, _serializer })!;
        }

        public string Serialize(object value) =>
            (string)_asm.GetType("Newtonsoft.Json.JsonConvert", throwOnError: true)!
                .GetMethod("SerializeObject", new[] { typeof(object), _settings.GetType() })!.Invoke(null, new[] { value, _settings })!;
    }

    /// <summary>Field-by-field comparison of two protocol objects (public fields, lists, dictionaries).</summary>
    private static void DeepCompare(object? a, object? b, Type type, string path, List<string> diffs)
    {
        if (a is null || b is null)
        {
            if (a is not null || b is not null) diffs.Add($"{path}: {a ?? "null"} vs {b ?? "null"}");
            return;
        }
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal))
        {
            if (!Equals(a, b)) diffs.Add($"{path}: {a} vs {b}");
        }
        else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var da = (System.Collections.IDictionary)a;
            var db = (System.Collections.IDictionary)b;
            var valueType = type.GetGenericArguments()[1];
            foreach (var key in da.Keys.Cast<object>().Union(db.Keys.Cast<object>()))
                DeepCompare(da.Contains(key) ? da[key] : null, db.Contains(key) ? db[key] : null, valueType, $"{path}[{key}]", diffs);
        }
        else if (type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)))
        {
            var itemType = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
            var la = ((System.Collections.IEnumerable)a).Cast<object?>().ToList();
            var lb = ((System.Collections.IEnumerable)b).Cast<object?>().ToList();
            if (la.Count != lb.Count) { diffs.Add($"{path}: {la.Count} vs {lb.Count} items"); return; }
            for (int i = 0; i < la.Count; i++) DeepCompare(la[i], lb[i], itemType, $"{path}[{i}]", diffs);
        }
        else
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                DeepCompare(f.GetValue(a), f.GetValue(b), f.FieldType, $"{path}.{f.Name}", diffs);
    }

}
