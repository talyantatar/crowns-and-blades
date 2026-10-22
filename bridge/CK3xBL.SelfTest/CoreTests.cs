// Self tests for CK3xBL.Core: log tailing and parsing, snapshot building, events, keypad payload, run file, deliveries, config,
// protocol JSON and the battle orchestrator (with fakes). Headless; touches only temp folders.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Bannerlord;
using CK3xBL.Core.Ck3;
using CK3xBL.Core.Config;
using CK3xBL.Core.Delivery;
using CK3xBL.Core.Diagnostics;
using CK3xBL.Core.Json;
using CK3xBL.Core.Logs;
using CK3xBL.Core.Model;
using CK3xBL.Core.Native;
using CK3xBL.Core.Orchestration;
using CK3xBL.Protocol;
using static CK3xBL.SelfTest.CoreFixtures;

namespace CK3xBL.SelfTest;

internal static class CoreTests
{
    public static void Register(TestRegistry r)
    {
        r.Add("core.scope_print.formats", ScopePrintFormats);
        r.Add("core.parser.records_and_dumps", ParserRecordsAndDumps);
        r.Add("core.parser.flush_and_orphans", ParserFlushAndOrphans);
        r.Add("core.snapshot.full_battle", SnapshotFullBattle);
        r.Add("core.snapshot.stale_scopes_rejected", SnapshotStaleScopes);
        r.Add("core.snapshot.legacy_grammar", SnapshotLegacyGrammar);
        r.Add("core.snapshot.bid_mismatch_and_missing_begin", SnapshotBidMismatch);
        r.Add("core.events.lifecycle_records", EventsLifecycle);
        r.Add("core.events.session_key_renewal", EventsSessionKey);
        r.Add("core.tailer.append_partial_utf8", TailerAppendPartialUtf8);
        r.Add("core.tailer.restart_detection", TailerRestart);
        r.Add("core.tailer.start_at_end", TailerStartAtEnd);
        r.Add("core.monitor.end_to_end", MonitorEndToEnd);
        r.Add("core.keypad.encode_round_trip", KeypadRoundTrip);
        r.Add("core.keypad.checksum_and_limits", KeypadChecksumAndLimits);
        r.Add("core.keypad.maa_index_table", KeypadMaaIndex);
        r.Add("core.runfile.render", RunFileRender);
        r.Add("core.delivery.keypad_sends_verified_chords", KeypadDeliverySends);
        r.Add("core.delivery.keypad_retry_after_bad", KeypadDeliveryRetry);
        r.Add("core.delivery.keypad_focus_loss_aborts", KeypadDeliveryFocusLoss);
        r.Add("core.delivery.keypad_probe_before_typing", KeypadDeliveryProbe);
        r.Add("core.delivery.keypad_restores_layout", KeypadDeliveryLayout);
        r.Add("core.delivery.keypad_keys_held_is_retryable", KeypadDeliveryKeysHeld);
        r.Add("core.delivery.runfile_types_and_resets", RunFileDeliveryTypes);
        r.Add("core.delivery.router_never_console_in_normal", DeliveryRouterModes);
        r.Add("core.config.defaults_and_invalid", ConfigDefaultsAndInvalid);
        r.Add("core.json.protocol_conventions", JsonConventions);
        r.Add("core.bannerlord.files", BannerlordFiles);
        r.Add("core.bannerlord.commands_never_overwritten", BannerlordCommandsNeverOverwritten);
        r.Add("core.bannerlord.transport_reset_and_withdraw", BannerlordTransportResetAndWithdraw);
        r.Add("core.bannerlord.adopts_only_bridge_processes", BannerlordAdoptsOnlyBridgeProcesses);
        r.Add("core.ck3.launch_args_and_pause", Ck3LaunchArgsAndPause);
        r.Add("core.ck3.pause_attach_never_replays_history", PauseAttachNeverReplaysHistory);
        r.Add("core.tailer.at_end_after_a_sliced_backlog", TailerAtEnd);
        r.Add("core.snapshot.player_absent_key", SnapshotPlayerAbsent);
        r.Add("core.bannerlord.stale_result_of_another_run_is_ignored", BannerlordStaleResultIgnored);
        r.Add("core.orchestrator.happy_path", OrchestratorHappyPath);
        r.Add("core.orchestrator.idle_after_result_returns", OrchestratorIdleAfterResult);
        r.Add("core.orchestrator.final_result_without_status", OrchestratorFinalResultWithoutStatus);
        r.Add("core.orchestrator.auto_cancels_preload", OrchestratorAutoCancels);
        r.Add("core.orchestrator.waits_for_focus", OrchestratorWaitsForFocus);
        r.Add("core.orchestrator.abort_hotkey_while_fighting", OrchestratorAbortWhileFighting);
        r.Add("core.orchestrator.bannerlord_lost_recovers", OrchestratorBannerlordLost);
        r.Add("core.orchestrator.queued_battle_answered_first", OrchestratorQueue);
        r.Add("core.orchestrator.stale_battle_abandoned_before_field", OrchestratorStaleAbandonBeforeField);
        r.Add("core.orchestrator.ck3_restart_drops_battle", OrchestratorCk3Restart);
        r.Add("core.orchestrator.unresponsive_keeps_preload", OrchestratorUnresponsiveKeepsPreload);
        r.Add("core.orchestrator.never_requests_a_held_battle_again", OrchestratorHeldBattleNotRequestedAgain);
        r.Add("core.orchestrator.stray_session_cancelled_once", OrchestratorStraySessionCancelled);
        r.Add("core.orchestrator.cloaks_bannerlord_again", OrchestratorCloaksAgain);
        r.Add("core.orchestrator.reenters_when_still_held", OrchestratorReentersWhenHeld);
        r.Add("core.orchestrator.delivery_waits_for_released_keys", OrchestratorDeliveryWaitsForKeys);
        r.Add("core.orchestrator.delivery_waits_for_keypad", OrchestratorDeliveryWaitsForKeypad);
        r.Add("core.orchestrator.reloaded_save_while_fighting_recovers", OrchestratorReloadWhileFighting);
        r.Add("core.orchestrator.foreign_bannerlord_fails_cleanly", OrchestratorForeignBannerlord);
        r.Add("core.orchestrator.field_choice_restores_archived_snapshot", OrchestratorRestoresArchivedSnapshot);

        // Knights & commanders pass (PROTOCOL §13).
        r.Add("core.knights.snapshot_facts", KnightsSnapshotFacts);
        r.Add("core.knights.neutral_defaults_without_facts", KnightsNeutralDefaults);
        r.Add("core.knights.malformed_lines_ignored", KnightsMalformedLines);
        r.Add("core.knights.join_carries_facts", KnightsJoinCarriesFacts);
        r.Add("core.knights.award_encoder_vector", KnightsAwardEncoderVector);
        r.Add("core.knights.unharmed_characters_not_written", KnightsUnharmedNotWritten);
        r.Add("core.knights.award_key_cap", KnightsAwardKeyCap);
        r.Add("core.knights.keypad_delivery_caps_awards_and_times", KnightsKeypadDeliveryCap);
        r.Add("core.knights.run_file_carries_every_award", KnightsRunFileCarriesAwards);
        r.Add("core.knights.config_defaults", KnightsConfigDefaults);
        r.Add("core.knights.reveal_dissolves_into_the_battle", KnightsRevealDissolves);
        r.Add("core.knights.title_in_engine_when_card_not_shown", KnightsTitleInEngine);
        r.Add("core.knights.result_card_names_the_ck3_option", KnightsResultCardText);
        r.Add("core.knights.award_records_journaled", KnightsAwardJournaled);

        // Battle clock (PROTOCOL §14).
        r.Add("core.decoder.steering_records", DecoderSteeringRecords);
        r.Add("core.keypad.v4_encoder", KeypadV4Encoder);
        r.Add("core.keypad.v4_old_mod_fallback", KeypadV4OldModFallback);
        r.Add("core.runfile.v4_old_mod_fallback", RunFileV4OldModFallback);
        r.Add("core.config.battle_clock", ConfigBattleClock);
        r.Add("core.config.bridge_root_override", ConfigBridgeRootOverride);
        r.Add("core.archive.append_and_load", ArchiveAppendAndLoad);
        r.Add("core.orchestrator.steering_detach_and_follow", SteeringDetachAndFollow);
        r.Add("core.orchestrator.steering_closed_before_begin", SteeringClosedBeforeBegin);
        r.Add("core.orchestrator.steering_child_planned_from_parent", SteeringChildPlannedFromParent);
        r.Add("core.orchestrator.steering_end_drops_offered_child", SteeringEndDropsOfferedChild);
        r.Add("core.orchestrator.steering_child_auto_keeps_parent", SteeringChildAutoKeepsParent);
        r.Add("core.orchestrator.steering_parent_after_restart", SteeringParentAfterRestart);
        r.Add("core.orchestrator.steering_reinforcement_clash_off", SteeringReinforcementClashOff);
        r.Add("core.orchestrator.steering_v3_fallback_not_detached", SteeringV3FallbackNotDetached);
        r.Add("core.orchestrator.steering_dev_mode_begin_before_res_end", SteeringDevModeOrder);
        r.Add("core.orchestrator.steering_done_before_detach", SteeringDoneBeforeDetach);
        r.Add("core.orchestrator.steering_ck3_restart_clears", SteeringCk3RestartClears);
        r.Add("core.orchestrator.steering_follows_the_clock_fixture", SteeringFollowsTheClockFixture);
        r.Add("core.orchestrator.steering_watchdog", SteeringWatchdog);
    }

    // ===================================================================================================== scope prints & parser

    private static void ScopePrintFormats()
    {
        var c = Expect.NotNull(ScopePrint.Parse(Gunda));
        Expect.Equal("Character", c.Type);
        Expect.Equal(10811L, c.Id);
        Expect.Equal("Gunda Kakatiya", c.Name);
        Expect.Equal("c_orangallu", c.TitleKey);
        Expect.Equal("189226", c.HistoricalId);

        // CK3 in Russian: a no-break space and an em dash before the id (live log 2026-10-17); the names are localized.
        var ru = Expect.NotNull(ScopePrint.Parse("Tughtekin Айюбид of d_sanaa (Internal ID: 54591 - Historical ID 32916) weak (Character — 54591)!"));
        Expect.Equal(("Character", 54591L, "Tughtekin Айюбид", "d_sanaa"), (ru.Type, ru.Id, ru.Name, ru.TitleKey));
        Expect.Equal(("Province", 6215L), (ScopePrint.Parse("Мадин-ан-Накира (Internal ID: 6215) weak (Province — 6215)!")!.Type, ScopePrint.Parse("Мадин-ан-Накира (Internal ID: 6215) weak (Province — 6215)!")!.Id));

        var low = Expect.NotNull(ScopePrint.Parse(Lowborn));
        Expect.Equal(23538L, low.Id);
        Expect.Equal("Nachana Lowborn", low.Name);
        Expect.Null(low.TitleKey);
        Expect.Null(low.HistoricalId);

        Expect.True(Expect.NotNull(ScopePrint.Parse(NoCharacter)).IsNull);

        var reg = Expect.NotNull(ScopePrint.Parse(Regiment("armored_footmen", "Armored Footmen", 16706, "title")));
        Expect.Equal("Regiment", reg.Type);
        Expect.Equal("armored_footmen", reg.MaaKey);
        Expect.Equal("title", reg.RegimentType);
        Expect.Equal("Armored Footmen", reg.Name);

        var title = Expect.NotNull(ScopePrint.Parse(OrangalluCounty));
        Expect.Equal("c_orangallu", title.Key);
        Expect.Equal("Orangallu", title.Name);

        var house = Expect.NotNull(ScopePrint.Parse(KakatiyaHouse));
        Expect.Equal("Kakatiya", house.Name);
        Expect.Null(house.Key);

        var army = Expect.NotNull(ScopePrint.Parse($"1st Raiders of Hepung {Weak("Army", 16777235)}"));
        Expect.Equal(("Army", 16777235L, "1st Raiders of Hepung"), (army.Type, army.Id, army.Name));

        var combat = Expect.NotNull(ScopePrint.Parse(CombatPrint));
        Expect.Equal("Battle of Orangallu", combat.Name);
        Expect.Null(ScopePrint.Parse("709.00"));
    }

    private static void ParserRecordsAndDumps()
    {
        var records = Ck3RecordParser.ParseAll(BattleLog());
        var types = records.Select(x => x.Type).ToList();
        Expect.Equal("B", types[0]);
        Expect.Equal("SOMETHING_NEW", types[^1], "unknown records are emitted (and ignored downstream)");
        Expect.Equal(13, types.Count(t => t == "L"));
        Expect.Equal(1, types.Count(t => t == "LV"));

        var begin = records[0];
        Expect.Equal("BEGIN", begin.Arg(0));
        Expect.Null(begin.IntArg(1), "markers carry no numbers (PROTOCOL §5.1)");
        Expect.Equal(17, begin.Dump?.GetInt("ck3xbl_bid"));
        Expect.Equal("ck3xbl_snapshot_effect", begin.Context);
        Expect.Equal(new TimeSpan(19, 2, 11), begin.Time);
        var dump = Expect.NotNull(begin.Dump);
        Expect.False(dump.Has("governor"), "foreign engine-saved scopes are dropped");
        Expect.Equal(870, dump.GetInt("ck3xbl_year"));
        Expect.Equal(8199L, dump.GetScope("ck3xbl_prov")?.Id);
        Expect.False(dump.Current.Contains('\u0015'), "control bytes stripped");

        // The S|def dump arrives after an unrelated engine line; it must still attach.
        var sDef = records.First(x => x.Type == "S" && x.Arg(0) == "def");
        Expect.Equal(1700, sDef.Dump?.GetInt("ck3xbl_s_size"));

        // L/CX/AX/RX never carry a dump, even when one follows later.
        Expect.True(records.Where(x => x.Type is "L" or "CX" or "AX" or "RX").All(x => x.Dump is null));
        // The foreign dump between records is not attached to the following A record.
        var secondArmy = records.Where(x => x.Type == "A").ElementAt(1);
        Expect.Equal(6, secondArmy.Dump?.GetInt("ck3xbl_a_tag"));
    }

    private static void ParserFlushAndOrphans()
    {
        var got = new List<Ck3Record>();
        var p = new Ck3RecordParser(got.Add);
        p.Feed(Marker("IN|OK", "ck3xbl_key_commit:effect"));
        foreach (var l in Dump17($"Gunda {Weak("Character", 10811)}", ("ck3xbl_in_count", "61.00"), ("ck3xbl_in_sum", "1234.00")))
            p.Feed(l);
        Expect.Equal(0, got.Count, "the dump is open until the next timestamped line or a flush");
        p.Flush();
        Expect.Equal(1, got.Count);
        Expect.Equal(61, got[0].Dump?.GetInt("ck3xbl_in_count"));

        // A marker whose dump never comes is still emitted (on the next marker).
        got.Clear();
        p.Feed(Marker("CHOICE|FIELD|17", "ck3xbl_battle.0001:option"));
        p.Feed(Marker("CHOICE|AUTO|18", "ck3xbl_battle.0001:option"));
        p.Flush();
        Expect.SequenceEqual(new[] { "CHOICE|FIELD|17", "CHOICE|AUTO|18" }, got.Select(x => x.Raw));
        Expect.True(got.All(x => x.Dump is null));

        // Run files have no "(context)" part; continuation lines before any header are ignored.
        got.Clear();
        p.Reset();
        p.Feed("Saved event targets:");
        p.Feed("[19:03:00][I][jomini_effect_impl.cpp:450]: file: run/ck3xbl_apply.txt line: 1: CK3XBL|NOOP");
        p.Flush();
        Expect.Equal(1, got.Count);
        Expect.Equal("NOOP", got[0].Type);
        Expect.Null(got[0].Context);
    }

    private static BattleSnapshot BuildFixtureSnapshot(List<string>? warnings = null)
    {
        var events = new List<Ck3Event>();
        var decoder = new Ck3EventDecoder(events.Add, w => warnings?.Add(w), sessionKey: "test");
        foreach (var r in Ck3RecordParser.ParseAll(BattleLog())) decoder.Accept(r);
        var done = events.OfType<SnapshotCompleted>().ToList();
        Expect.Equal(1, done.Count, "exactly one snapshot");
        return done[0].Snapshot;
    }

    private static void SnapshotFullBattle()
    {
        var s = BuildFixtureSnapshot();
        Expect.True(s.Complete);
        Expect.Equal(17, s.Bid);
        Expect.Equal("b17_test", s.RequestId);
        Expect.Equal((870, 6, 14), (s.Year, s.Month, s.Day));
        Expect.Equal(134217751L, s.CombatId);
        Expect.Equal("Battle of Orangallu", s.CombatName);
        Expect.Equal("Hepung's Raid on Orangallu", s.WarName);
        Expect.Equal(10811L, s.PlayerCk3Id);

        var loc = s.Location;
        Expect.Equal((8199L, "Orangallu", "c_orangallu", "b_orangallu"), (loc.ProvinceId, loc.ProvinceName, loc.CountyKey, loc.BaronyKey));
        Expect.Equal(("hills", "mild", "castle_holding"), (loc.Terrain, loc.Winter, loc.Holding));
        Expect.Equal((false, true, false), (loc.Coastal, loc.Riverside, loc.Siege));
        Expect.Equal((2, 14, 8200L), (loc.FortLevel, loc.Development, loc.AttackerOriginProvinceId ?? 0));
        Expect.Equal("india", loc.Facts["region"]);
        Expect.Equal("whatever", loc.Facts["future_field"], "unknown L fields are kept");
        Expect.Equal(16, loc.Facts.Count);

        Expect.Equal(1925, s.Attacker.Size);
        Expect.False(s.Attacker.PlayerIn);
        Expect.True(s.Defender.PlayerIn);
        Expect.True(s.PlayerSide == s.Defender);
        Expect.Equal(20001L, s.Attacker.Primary?.Ck3Id);

        // Armies in emission order, regiments attached by a_tag.
        Expect.SequenceEqual(new[] { 5, 6 }, s.Attacker.Armies.Select(a => a.Tag));
        var a1 = s.Attacker.Armies[0];
        Expect.Equal((16777235L, "1st Raiders of Hepung", 1225, 900), (a1.ArmyId, a1.Name, a1.Size, a1.Levies));
        Expect.Equal(20002L, a1.Commander?.Ck3Id);
        Expect.Equal(20001L, a1.Owner?.Ck3Id);
        Expect.Equal(("kirati", "heritage_burman", "indian_unit_gfx", "culture_era_early_medieval"),
            (a1.OwnerCulture.Culture, a1.OwnerCulture.Heritage, a1.OwnerCulture.UnitGfx, a1.OwnerCulture.Era));
        Expect.True(a1.OwnerNomadic, "AX|nomadic|yes");
        Expect.Equal(1, a1.Regiments.Count);
        Expect.Equal(("mangudai", 300, 300, false), (a1.Regiments[0].MaaKey, a1.Regiments[0].Current, a1.Regiments[0].Max, a1.Regiments[0].Hired));

        var a2 = s.Attacker.Armies[1];
        Expect.Null(a2.Commander, "a_has_cmd = 0: the a_cmd left over from army 5 must be ignored");
        Expect.False(a2.OwnerNomadic);
        var hired = a2.Regiments.Single();
        Expect.Equal(("armored_footmen", true, "title", "bengali", "heritage_indo_aryan"),
            (hired.MaaKey, hired.Hired, hired.RegimentType, hired.HiredCulture?.Culture, hired.HiredCulture?.Heritage));

        var d = s.Defender.Armies.Single();
        Expect.Equal(7, d.Tag);
        Expect.SequenceEqual(new[] { "armored_footmen", "war_elephant" }, d.Regiments.Select(x => x.MaaKey));
        Expect.Equal(10811L, d.Commander?.Ck3Id);
        Expect.Equal("telugu", d.OwnerCulture.Culture);

        // Characters per side, in emission order (§5.2: each character once, first role wins).
        Expect.SequenceEqual(new[] { 20002L, 20001L }, s.Attacker.Characters.Select(c => c.Ref.Ck3Id));
        Expect.SequenceEqual(new[] { 10811L, 23538L }, s.Defender.Characters.Select(c => c.Ref.Ck3Id));
        var raider = s.Attacker.Characters[0];
        Expect.Equal(("Bodo Hepung", "Hepung", "reaver"), (raider.Ref.Name, raider.HouseName, raider.CommanderTraits.Single()));
        Expect.Null(raider.PrimaryTitleKey);
        var primary = s.Attacker.Characters[1];
        Expect.True(primary.IsPrimary && primary.Role == "PRIMARY");
        Expect.Equal("d_hepung", primary.PrimaryTitleKey);

        var gunda = s.Defender.Characters[0];
        Expect.Equal("CMD", gunda.Role);
        Expect.True(gunda.IsCommander && gunda.IsPlayer && !gunda.IsKnight);
        Expect.Equal((1, 71, 11, 15, -12, 2, 7), (gunda.Tag, gunda.Age, gunda.Prowess, gunda.Martial, gunda.Weight, gunda.Tier, gunda.ArmyTag));
        Expect.Equal(("telugu", "heritage_dravidian", "indian_unit_gfx", "indian_clothing_gfx", "culture_era_early_medieval"),
            (gunda.Culture.Culture, gunda.Culture.Heritage, gunda.Culture.UnitGfx, gunda.Culture.ClothingGfx, gunda.Culture.Era));
        Expect.Equal("indian", gunda.Ethnicity);
        Expect.SequenceEqual(new[] { "scarred", "physique_good_2" }, gunda.Traits);
        Expect.SequenceEqual(new[] { "unyielding_defender", "forder" }, gunda.CommanderTraits, "CX|ctrait kept apart from appearance traits");
        Expect.Equal("c_orangallu", gunda.PrimaryTitleKey);
        Expect.Equal("Kakatiya", gunda.HouseName);

        var knight = s.Defender.Characters[1];
        Expect.True(knight.IsKnight && knight.Female);
        Expect.Equal((2, 17, "unknown"), (knight.Tag, knight.Prowess, knight.Culture.Culture));
    }

    private static void SnapshotLegacyGrammar()
    {
        var events = new List<Ck3Event>();
        var decoder = new Ck3EventDecoder(events.Add, sessionKey: "t");
        foreach (var r in Ck3RecordParser.ParseAll(LegacyBattleLog())) decoder.Accept(r);
        var s = events.OfType<SnapshotCompleted>().Single().Snapshot;
        Expect.Equal(5, s.Bid);
        Expect.Equal((3, 21, 8300L), (s.Location.FortLevel, s.Location.Development, s.Location.AttackerOriginProvinceId ?? 0), "L|fort/dev/origin");
        Expect.Equal(10811L, s.Defender.Primary?.Ck3Id, "no sentinel: the scope is read");
        var gunda = s.Defender.Characters[0];
        Expect.True(gunda.IsCommander && gunda.IsPrimary && gunda.IsPlayer, "a repeated character merges its roles");
        Expect.SequenceEqual(new[] { "scarred" }, gunda.Traits, "trait lines of a repeated record are not duplicated");
        Expect.Equal(("c_orangallu", "Kakatiya"), (gunda.PrimaryTitleKey, gunda.HouseName));
        var knight = s.Defender.Characters.Single(c => c.Ref.Ck3Id == 23538);
        Expect.Null(knight.PrimaryTitleKey, "no sentinel, landless: the leftover c_title is not his");
        Expect.Null(knight.HouseName, "no sentinel: the leftover house is not in his printed name");
        Expect.Equal(2, s.Defender.Characters.Count);
    }

    private static void SnapshotStaleScopes()
    {
        var knight = BuildFixtureSnapshot().Defender.Characters.Single(c => c.Ref.Ck3Id == 23538);
        Expect.Null(knight.PrimaryTitleKey, "landless lowborn: the c_title scope is the player's, left over");
        Expect.Null(knight.HouseName, "lowborn: the c_house scope is the player's, left over");

        // The sentinels decide (PROTOCOL §5.4), even against the printed-name heuristics; s_has_primary = 0 drops a leftover primary.
        var log = new List<string> { Marker("B|BEGIN") };
        log.AddRange(Dump(CombatPrint, ("ck3xbl_bid", "3.00")));
        log.Add(Marker("S|def"));
        log.AddRange(Dump(CombatPrint, ("ck3xbl_bid", "3.00"), ("ck3xbl_s_size", "10.00"), ("ck3xbl_s_has_primary", "0.00"), ("ck3xbl_s_primary", Gunda)));
        log.Add(Marker("C|KNIGHT|def"));
        log.AddRange(Dump(Lowborn, ("ck3xbl_bid", "3.00"), ("ck3xbl_c_tag", "9.00"), ("ck3xbl_c_has_title", "1.00"),
            ("ck3xbl_c_title", OrangalluCounty), ("ck3xbl_c_has_house", "1.00"), ("ck3xbl_c_house", KakatiyaHouse)));
        log.Add(Marker("B|END"));
        log.AddRange(Dump(CombatPrint, ("ck3xbl_bid", "3.00")));
        var events = new List<Ck3Event>();
        var decoder = new Ck3EventDecoder(events.Add, sessionKey: "t");
        foreach (var rec in Ck3RecordParser.ParseAll(log)) decoder.Accept(rec);
        var snapshot = events.OfType<SnapshotCompleted>().Single().Snapshot;
        var c = snapshot.Defender.Characters.Single();
        Expect.Equal("c_orangallu", c.PrimaryTitleKey);
        Expect.Equal("Kakatiya", c.HouseName);
        Expect.Null(snapshot.Defender.Primary);
    }

    private static void SnapshotBidMismatch()
    {
        var warnings = new List<string>();
        var events = new List<Ck3Event>();
        var decoder = new Ck3EventDecoder(events.Add, warnings.Add, sessionKey: "t");
        // Records of a battle whose BEGIN we never saw (bridge started mid-snapshot) are ignored.
        decoder.Accept(new Ck3Record { Type = "A", Args = new[] { "att", "4" } });
        decoder.Accept(new Ck3Record { Type = "B", Args = new[] { "END", "4" } });
        Expect.Equal(0, events.Count);
        // END with a different bid discards the open battle.
        decoder.Accept(new Ck3Record { Type = "B", Args = new[] { "BEGIN", "5" } });
        decoder.Accept(new Ck3Record { Type = "B", Args = new[] { "END", "6" } });
        Expect.Equal(0, events.Count);
        Expect.True(warnings.Count >= 2);
        // A snapshot without dumps still completes (mapper decides what to do with an empty side).
        decoder.Accept(new Ck3Record { Type = "B", Args = new[] { "BEGIN", "7" } });
        decoder.Accept(new Ck3Record { Type = "B", Args = new[] { "END", "7" } });
        Expect.Equal(7, events.OfType<SnapshotCompleted>().Single().Snapshot.Bid);
    }

    private static void EventsLifecycle()
    {
        // §5 forms: no numbers in markers, the bid comes from each dump.
        var log = new List<string> { Marker("CHOICE|FIELD", "ck3xbl_battle.0001:option") };
        log.AddRange(Dump17(Gunda));
        log.Add(Marker("CHOICE|AUTO", "ck3xbl_battle.0001:option"));
        log.AddRange(Dump(Gunda, ("ck3xbl_bid", "19.00")));
        log.Add(Marker("CHOICE|ABANDON", "ck3xbl_battle.0002:option"));
        log.AddRange(Dump(Gunda, ("ck3xbl_bid", "20.00")));
        log.Add(Marker("CHOICE|FIELD_AS_COMMANDER|18", "ck3xbl_battle.0001:option"));   // early sketch form, without a dump
        log.Add(Marker("IN|BAD", "ck3xbl_key_commit:effect"));
        log.AddRange(Dump17(Gunda, ("ck3xbl_in_count", "60.00"), ("ck3xbl_in_sum", "999.00")));
        log.Add(Marker("IN|OK", "ck3xbl_key_commit:effect"));
        log.AddRange(Dump17(Gunda, ("ck3xbl_in_count", "61.00"), ("ck3xbl_in_sum", "1234.00")));
        log.Add(Marker("RES|A", "ck3xbl_apply_effect"));
        log.AddRange(Dump17(Gunda, ("ck3xbl_a_tag", "5.00"), ("ck3xbl_after", "455.00")));
        log.Add(Marker("RES|END", "ck3xbl_apply_effect"));
        log.AddRange(Dump17(Gunda));
        log.Add(Marker("END|winner", "ck3xbl_on_combat_end_winner"));
        log.AddRange(Dump17(CombatPrint, ("ck3xbl_end_wipe", "0.00"), ("ck3xbl_end_has_primary", "1.00"), ("ck3xbl_end_primary", Gunda)));
        log.Add(Marker("END|loser", "ck3xbl_on_combat_end_loser"));
        log.AddRange(Dump17(CombatPrint, ("ck3xbl_end_wipe", "1.00"), ("ck3xbl_end_has_primary", "0.00"), ("ck3xbl_end_primary", Gunda)));

        // A join (§5.6): J, then the army's AX lines, its regiments and its commander, all without an open snapshot.
        log.Add(Marker("J|att", "ck3xbl_on_join"));
        log.AddRange(Dump17($"3rd Raiders {Weak("Army", 16777300)}", ("ck3xbl_a_tag", "9.00"), ("ck3xbl_a_size", "300.00"),
            ("ck3xbl_a_levies", "250.00"), ("ck3xbl_a_has_cmd", "1.00"), ("ck3xbl_a_owner", Hepung), ("ck3xbl_a_cmd", RaidKnight)));
        log.Add(Marker("AX|culture|kirati"));
        log.Add(Marker("R"));
        log.AddRange(Dump17(Regiment("light_horsemen", "Light Horsemen", 16800), ("ck3xbl_r_cur", "50.00"), ("ck3xbl_r_max", "100.00"),
            ("ck3xbl_r_hired", "1.00"), ("ck3xbl_a_tag", "9.00")));
        log.Add(Marker("RX|culture|bengali"));
        log.Add(Marker("C|CMD|att"));
        log.AddRange(Dump17(RaidKnight, ("ck3xbl_c_tag", "3.00"), ("ck3xbl_c_army", "9.00")));
        log.Add(Marker("CX|ctrait|reaver"));
        log.Add(Marker("CHOICE|WHATEVER"));
        log.AddRange(Dump17(Gunda));

        var events = new List<Ck3Event>();
        var decoder = new Ck3EventDecoder(events.Add, sessionKey: "t");
        foreach (var r in Ck3RecordParser.ParseAll(log)) decoder.Accept(r);

        var choices = events.OfType<ChoiceMade>().ToList();
        Expect.SequenceEqual(new[] { (17, Ck3Choice.Field), (19, Ck3Choice.Auto), (20, Ck3Choice.Abandon), (18, Ck3Choice.Field) },
            choices.Select(c => (c.Bid, c.Choice)));
        var acks = events.OfType<InputAck>().ToList();
        Expect.Equal(2, acks.Count);
        Expect.Equal((17, false, 60), (acks[0].Bid, acks[0].Ok, acks[0].Count));
        Expect.Equal((true, 61, 1234L), (acks[1].Ok, acks[1].Count, acks[1].Sum));
        var traces = events.OfType<ResultTrace>().ToList();
        Expect.SequenceEqual(new[] { (17, "A"), (17, "END") }, traces.Select(t => (t.Bid, t.Stage)));
        Expect.Equal("455.00", traces[0].Values["ck3xbl_after"]);
        var ends = events.OfType<CombatEnded>().ToList();
        Expect.Equal((17, true, false, 10811L), (ends[0].Bid, ends[0].WinnerSide, ends[0].Wipe, ends[0].Primary?.Ck3Id ?? 0));
        Expect.Equal((false, true), (ends[1].WinnerSide, ends[1].Wipe));
        Expect.Null(ends[1].Primary, "end_has_primary = 0: the leftover primary is ignored");

        var join = events.OfType<ArmyJoined>().Single();
        Expect.Equal((17, SideKey.Attacker, 9, 16777300L, 20002L), (join.Bid, join.Side, join.Army.Tag, join.Army.ArmyId, join.Army.Commander?.Ck3Id ?? 0));
        Expect.Equal("kirati", join.Army.OwnerCulture.Culture);
        var reg = join.Army.Regiments.Single();
        Expect.Equal(("light_horsemen", 50, "bengali"), (reg.MaaKey, reg.Current, reg.HiredCulture?.Culture));
        var commander = join.Characters.Single();
        Expect.Equal((3, true, "reaver"), (commander.Tag, commander.IsCommander, commander.CommanderTraits.Single()));
        int joinIndex = events.FindIndex(e => e is ArmyJoined);
        Expect.True(events.Take(joinIndex).OfType<ChoiceMade>().Count() == 4, "the join is complete before the next unrelated record");
    }

    private static void EventsSessionKey()
    {
        var events = new List<Ck3Event>();
        var decoder = new Ck3EventDecoder(events.Add, sessionKey: "aaaa");
        decoder.Accept(new Ck3Record { Type = "B", Args = new[] { "BEGIN", "5" } });
        decoder.Accept(new Ck3Record { Type = "B", Args = new[] { "END", "5" } });
        Expect.Equal("aaaa", decoder.SessionKey);
        decoder.Accept(new Ck3Record { Type = "B", Args = new[] { "BEGIN", "3" } });   // older save loaded
        Expect.True(decoder.SessionKey != "aaaa");
        string afterReload = decoder.SessionKey;
        decoder.LogRestarted();
        Expect.True(decoder.SessionKey != afterReload);
        Expect.True(events.Last() is LogRestarted);
        Expect.Equal(4, decoder.SessionKey.Length);
    }

    // ===================================================================================================== tailer

    private static void TailerAppendPartialUtf8()
    {
        using var dir = new TempDir();
        var path = dir.File("game.log");
        File.WriteAllText(path, "");
        var f = new LogFollower(path, startAtEnd: false);
        Expect.Equal(0, f.Poll().Lines.Count);

        AppendBytes(path, Encoding.UTF8.GetBytes("[19:00:00][I][x.cpp:1]: first\r\n[19:00:01][I][x.cpp:1]: Lu"));
        var r = f.Poll();
        Expect.SequenceEqual(new[] { "[19:00:00][I][x.cpp:1]: first" }, r.Lines, "CRLF stripped, partial line held back");

        // "ó" (C3 B3) split across two writes.
        var rest = Encoding.UTF8.GetBytes("ó Talāra\n");
        AppendBytes(path, rest[..1]);
        Expect.Equal(0, f.Poll().Lines.Count);
        AppendBytes(path, rest[1..]);
        Expect.SequenceEqual(new[] { "[19:00:01][I][x.cpp:1]: Luó Talāra" }, f.Poll().Lines);
    }

    private static void TailerRestart()
    {
        using var dir = new TempDir();
        var path = dir.File("game.log");
        File.WriteAllText(path, "[18:00:00][I][a.cpp:1]: old session line one\n[18:00:00][I][a.cpp:1]: old session line two\n");
        var f = new LogFollower(path, startAtEnd: false);
        Expect.Equal(2, f.Poll().Lines.Count);

        // CK3 relaunched: the file is recreated and shorter.
        File.WriteAllText(path, "[19:00:00][I][b.cpp:1]: new\n");
        var r = f.Poll();
        Expect.True(r.Restarted);
        Expect.SequenceEqual(new[] { "[19:00:00][I][b.cpp:1]: new" }, r.Lines);

        // Recreated and already LONGER than our offset between two polls: detected by the changed head.
        File.WriteAllText(path, "[20:00:00][I][c.cpp:1]: " + new string('x', 200) + "\n[20:00:00][I][c.cpp:1]: second\n");
        r = f.Poll();
        Expect.True(r.Restarted);
        Expect.Equal(2, r.Lines.Count);
        Expect.False(f.Poll().Restarted);
    }

    private static void TailerStartAtEnd()
    {
        using var dir = new TempDir();
        var path = dir.File("game.log");
        File.WriteAllText(path, "[18:00:00][I][a.cpp:1]: stale\n[18:00:00][I][a.cpp:1]: half a li");
        var f = new LogFollower(path, startAtEnd: true);
        Expect.Equal(0, f.Poll().Lines.Count);
        AppendBytes(path, Encoding.UTF8.GetBytes("ne\n[19:00:00][I][a.cpp:1]: fresh\n"));
        Expect.SequenceEqual(new[] { "[19:00:00][I][a.cpp:1]: fresh" }, f.Poll().Lines, "the rest of the half line is dropped");

        // Missing file: nothing, no exception; appears later -> read from its start (it is a new CK3 session).
        var missing = new LogFollower(dir.File("debug.log"), startAtEnd: false);
        Expect.Equal(0, missing.Poll().Lines.Count);
    }

    private static async Task MonitorEndToEnd()
    {
        using var dir = new TempDir();
        var path = dir.File("game.log");
        File.WriteAllText(path, "[18:59:00][I][jominiapplication.cpp:261]: Changing game speed to: 2\n");
        var journal = new MemoryJournal();
        using var monitor = new Ck3LogMonitor(path, journal, new LogTailerOptions { PollInterval = TimeSpan.FromMilliseconds(20) });
        var snapshot = monitor.Expect<SnapshotCompleted>(_ => true);
        var choice = monitor.Expect<ChoiceMade>(c => c.Bid == 17);
        monitor.Start();
        await Task.Delay(60);

        // Written in two bursts, the second ending in an open dump: it completes when the file goes idle.
        var lines = BattleLog();
        int half = lines.Count / 2;
        File.AppendAllLines(path, lines.Take(half));
        await Task.Delay(50);
        File.AppendAllLines(path, lines.Skip(half));
        var s = await snapshot.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Expect.Equal(17, Expect.NotNull(s).Snapshot.Bid);
        Expect.Equal(monitor.SessionKey, s.Snapshot.SessionKey);

        File.AppendAllLines(path, new[] { Marker("CHOICE|FIELD|17", "ck3xbl_battle.0001:option") });
        Expect.Equal(Ck3Choice.Field, Expect.NotNull(await choice.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)).Choice);

        // CK3 restart: the file is recreated.
        var restarted = monitor.Expect<LogRestarted>(_ => true);
        File.WriteAllText(path, "[20:00:00][I][jomini_game_setup.cpp:351]: Log system initialized.\n");
        Expect.NotNull(await restarted.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    // ===================================================================================================== keypad payload

    /// <summary>The CK3 mod's keypad semantics (PROTOCOL §2.2), to replay key sequences.</summary>
    private sealed class KeypadSimulator
    {
        public readonly int[] R = new int[KeypadLayout.RegisterCount];
        public long Acc;
        public int Idx;
        public bool? Committed;

        public void Press(KeypadKey key)
        {
            switch (key)
            {
                case >= KeypadKey.D0 and <= KeypadKey.D9: Acc = Acc * 10 + (int)key; break;
                case KeypadKey.Store: R[Idx] = (int)Acc; Idx++; Acc = 0; break;
                case KeypadKey.Seek: Idx = (int)Acc; Acc = 0; break;
                case KeypadKey.Reset: Array.Clear(R); Idx = 0; Acc = 0; Committed = null; break;
                case KeypadKey.Commit: Committed = R[KeypadLayout.Checksum] == KeypadPayload.ComputeChecksum(R, R[KeypadLayout.Version]); break;
            }
            Expect.True(Acc < 1_000_000, "accumulator stays below 10^6");
        }
    }

    private static Ck3Outcome SampleOutcome() => new()
    {
        Bid = 17,
        ProvinceId = 8199,
        OutcomeCode = 2,
        MyLost = 312,
        EnemyLost = 1204,
        MyKills = 7,
        PrestigeBonus = 50,
        PlayerWon = true,
        Armies =
        {
            new ArmyLoss { ArmyTag = 5, LevyLossPermille = 640, Maa = { new MaaLoss { MaaKey = "mangudai", MaaIndex = 0, SurvivorSharePermille = 410 } } },
            new ArmyLoss { ArmyTag = 6, LevyLossPermille = 1000, Maa = { new MaaLoss { MaaKey = "armored_footmen", MaaIndex = 48, SurvivorSharePermille = 0 } } },
            new ArmyLoss
            {
                ArmyTag = 7, LevyLossPermille = 180,
                Maa = { new MaaLoss { MaaKey = "armored_footmen", MaaIndex = 48, SurvivorSharePermille = 905 },
                        new MaaLoss { MaaKey = "war_elephant", MaaIndex = 59, SurvivorSharePermille = 1000 } },
            },
        },
        Characters =
        {
            new CharacterFate { Tag = 1, Ck3Id = 10811, Fate = 0 },
            new CharacterFate { Tag = 2, Ck3Id = 23538, Fate = 1 },
            new CharacterFate { Tag = 3, Ck3Id = 20002, Fate = 4, KillerTag = 2 },
        },
    };

    private static MaaIndexTable SampleMaaTable() =>
        new(new[] { "huscarl", "mangudai", "x" }.Concat(Enumerable.Range(0, 60).Select(i => "filler" + i)));

    private static void KeypadRoundTrip()
    {
        var payload = KeypadPayload.Encode(SampleOutcome(), SampleMaaTable());
        var r = payload.Registers;
        Expect.Equal((3, 17, 2, 8199), (r[0], r[1], r[2], r[3]));
        Expect.Equal((312, 1204, 7, 50), (r[4], r[5], r[6], r[7]));
        Expect.Equal((5, 640, 2, 410), (r[10], r[11], r[12], r[13]), "mangudai resolved through the index table (index 2)");
        Expect.Equal((6, 1000, 48, 0), (r[24], r[25], r[26], r[27]));
        Expect.Equal((7, 180, 48, 905, 59, 1000), (r[38], r[39], r[40], r[41], r[42], r[43]));
        Expect.Equal((2, 1, 0), (r[130], r[131], r[132]), "the unharmed character 1 has no slot (PROTOCOL §13.5)");
        Expect.Equal((3, 4, 2), (r[133], r[134], r[135]));

        var keys = payload.ToKeySequence();
        Expect.Equal(KeypadKey.Reset, keys[0]);
        Expect.Equal(KeypadKey.Commit, keys[^1]);
        var sim = new KeypadSimulator();
        foreach (var k in keys) sim.Press(k);
        Expect.SequenceEqual(payload.Registers, sim.R, "replaying the keys reproduces every register");
        Expect.Equal(true, sim.Committed);

        // Byte-for-byte the sequence of the mod's reference encoder for the same registers:
        //   node tools/gen/keypad_payload.mjs 1=17 2=2 3=8199 4=312 5=1204 6=7 7=50 10=5 11=640 12=2 13=410 24=6 25=1000 26=48 38=7
        //        39=180 40=48 41=905 42=59 43=1000 130=2 131=1 133=3 134=4 135=2      (payload version 3, checksum 90)
        const string reference = "F3 3 F1 1 7 F1 2 F1 8 1 9 9 F1 3 1 2 F1 1 2 0 4 F1 7 F1 5 0 F1 1 0 F5 5 F1 6 4 0 F1 2 F1 4 1 0 F1 2 4 F5 6 F1 " +
            "1 0 0 0 F1 4 8 F1 3 8 F5 7 F1 1 8 0 F1 4 8 F1 9 0 5 F1 5 9 F1 1 0 0 0 F1 1 3 0 F5 2 F1 1 F1 1 3 3 F5 3 F1 4 F1 2 F1 " +
            "1 9 9 F5 9 0 F1 F2";
        Expect.Equal(90, r[KeypadLayout.Checksum]);
        Expect.Equal(reference, string.Join(" ", keys.Select(KeyName)));
    }

    private static string KeyName(KeypadKey k) => k switch
    {
        KeypadKey.Store => "F1",
        KeypadKey.Commit => "F2",
        KeypadKey.Reset => "F3",
        KeypadKey.Seek => "F5",
        _ => ((int)k).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static void KeypadChecksumAndLimits()
    {
        var regs = new int[200];
        regs[0] = 2; regs[1] = 9973; regs[198] = 5;
        Expect.Equal((2 + 9973 + 5) % 9973, KeypadPayload.ComputeChecksum(regs, KeypadLayout.LegacyPayloadVersion), "version 2: plain sum");
        regs[0] = 3;
        Expect.Equal(3 * 1 + 0 * 2 + 5 * (198 % 16 + 1), KeypadPayload.ComputeChecksum(regs), "version 3: weighted by position");
        // PROTOCOL §10.3 example: node tools/gen/keypad_payload.mjs 1=3 2=2 3=8199 4=300 5=900 6=7 10=12 11=500 12=48 13=400 130=5 131=4 132=2
        var example = new int[200];
        foreach (var (i, v) in new[] { (0, 3), (1, 3), (2, 2), (3, 8199), (4, 300), (5, 900), (6, 7), (10, 12), (11, 500), (12, 48), (13, 400),
                     (130, 5), (131, 4), (132, 2) })
            example[i] = v;
        Expect.Equal(2292, KeypadPayload.ComputeChecksum(example));
        // A repeated store key (F1) after r10 stores 0 in r11 and shifts the rest of the run (r11..r13 -> r12..r14): the plain sum
        // misses it, the weighted checksum does not.
        var shifted = (int[])example.Clone();
        (shifted[11], shifted[12], shifted[13], shifted[14]) = (0, example[11], example[12], example[13]);
        Expect.Equal(KeypadPayload.ComputeChecksum(example, KeypadLayout.LegacyPayloadVersion),
            KeypadPayload.ComputeChecksum(shifted, KeypadLayout.LegacyPayloadVersion), "same plain sum");
        Expect.True(KeypadPayload.ComputeChecksum(shifted) != KeypadPayload.ComputeChecksum(example), "a shifted run changes the version 3 checksum");

        var warnings = new List<string>();
        var big = new Ck3Outcome { Bid = 1, OutcomeCode = 1, MyLost = 5_000_000, EnemyLost = -3 };
        for (int i = 0; i < 25; i++) big.Characters.Add(new CharacterFate { Tag = 100 + i, Fate = i % 5 == 0 ? 4 : 0 });
        for (int i = 0; i < 10; i++) big.Armies.Add(new ArmyLoss { ArmyTag = i + 1, LevyLossPermille = 1500 });
        var p = KeypadPayload.Encode(big, null, warnings);
        Expect.Equal(999_999, p.Registers[KeypadLayout.MyLost]);
        Expect.Equal(0, p.Registers[KeypadLayout.EnemyLost]);
        Expect.Equal(1000, p.Registers[KeypadLayout.ArmyBase + 1], "levy loss clamped to 1000");
        Expect.True(warnings.Count >= 3);
        // 25 characters, five of them killed: only those five are written (unharmed ones never get a slot).
        var fates = Enumerable.Range(0, 20).Select(j => (Tag: p.Registers[130 + 3 * j], Fate: p.Registers[131 + 3 * j])).ToList();
        Expect.Equal(5, fates.Count(f => f.Fate == 4));
        var sim = new KeypadSimulator();
        foreach (var k in p.ToKeySequence()) sim.Press(k);
        Expect.SequenceEqual(p.Registers, sim.R);
    }

    private static void KeypadMaaIndex()
    {
        using var dir = new TempDir();
        var path = dir.File("ck3_maa_index.json");
        File.WriteAllText(path, "{ \"note\": \"x\", \"keys\": [ \"huscarl\", \"landsknecht\", \"huscarl\" ] }");
        var t = MaaIndexTable.Load(path);
        Expect.Equal((1, 2, 0), (t.IndexOf("huscarl"), t.IndexOf("landsknecht"), t.IndexOf("nope")));

        // The real table shipped in bridge/data, if present: append-only keys, index 1-based.
        var real = Path.Combine(BridgePaths.Detect().DataDir, "ck3_maa_index.json");
        if (File.Exists(real))
        {
            var table = MaaIndexTable.Load(real);
            Expect.Equal(1, table.IndexOf("huscarl"));
            Expect.True(table.IndexOf("armored_footmen") > 0);
        }

        var warnings = new List<string>();
        var o = new Ck3Outcome { Bid = 1, Armies = { new ArmyLoss { ArmyTag = 1, Maa = { new MaaLoss { MaaKey = "unknown_maa" } } } } };
        var p = KeypadPayload.Encode(o, t, warnings);
        Expect.Equal(0, p.Registers[KeypadLayout.ArmyBase + 2]);
        Expect.True(warnings.Any(w => w.Contains("unknown_maa")));
    }

    private static void RunFileRender()
    {
        var payload = KeypadPayload.Encode(SampleOutcome(), SampleMaaTable());
        var text = RunFileRenderer.Render(payload);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Expect.Equal("hidden_effect = {", lines[0]);
        Expect.Equal(27, lines.Count(l => l.StartsWith("\tset_variable = { name = ck3xbl_r", StringComparison.Ordinal)), "non-zero registers only");
        Expect.True(lines.Contains("\tset_variable = { name = ck3xbl_r1 value = 17 }"));
        Expect.True(lines.Contains("\tset_variable = { name = ck3xbl_r199 value = 90 }"));
        Expect.Equal("\tck3xbl_keypad_reset_effect = yes", lines[1], "the run file resets the keypad first (PROTOCOL §10.4)");
        int commit = Array.IndexOf(lines, "\tck3xbl_commit_effect = yes");
        int apply = Array.IndexOf(lines, "\tck3xbl_apply_effect = yes");
        Expect.True(commit > 0 && apply == commit + 1, "commit, then apply");
        Expect.Equal("}", lines[^1]);
        // Same body as the mod's reference encoder (tools/gen/keypad_payload.mjs prints it without the final newline).
        Expect.True(text.StartsWith("hidden_effect = {\n\tck3xbl_keypad_reset_effect = yes\n\tset_variable = { name = ck3xbl_r0 value = 3 }\n" +
            "\tset_variable = { name = ck3xbl_r1 value = 17 }\n", StringComparison.Ordinal) &&
            text.EndsWith("\tset_variable = { name = ck3xbl_r199 value = 90 }\n\tck3xbl_commit_effect = yes\n\tck3xbl_apply_effect = yes\n}\n",
            StringComparison.Ordinal));

        using var dir = new TempDir();
        var file = dir.File("run/ck3xbl_apply.txt");
        AtomicFile.WriteAllText(file, text, withBom: true);
        var bytes = File.ReadAllBytes(file);
        Expect.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "UTF-8 with BOM (CK3 script files)");
        Expect.False(File.Exists(file + ".tmp"));
    }

    // ===================================================================================================== deliveries

    private sealed class FakeInjector : IKeyInjector
    {
        private readonly object _gate = new();
        public readonly List<KeyStroke> Sent = new();
        public nint Foreground = 0x100;
        public nint Layout = 0x04090409;
        public readonly List<nint> LayoutRequests = new();
        public Action<IReadOnlyList<KeyStroke>>? OnSend;

        public nint ForegroundWindow => Foreground;
        public volatile bool Held;
        public bool AnyModifierOrButtonHeld() => Held;
        public nint GetKeyboardLayout(uint threadId) => Layout;
        public void RequestKeyboardLayout(nint hwnd, nint hkl) => LayoutRequests.Add(hkl);

        public bool Send(IReadOnlyList<KeyStroke> strokes)
        {
            lock (_gate) Sent.AddRange(strokes);
            OnSend?.Invoke(strokes);
            return true;
        }

        public List<KeyStroke> Snapshot() { lock (_gate) return Sent.ToList(); }
    }

    private sealed class FixedCk3(Ck3Window? window) : ICk3Locator
    {
        public Ck3Window? Window = window;
        public Ck3Window? Find() => Window;
    }

    private static readonly Ck3Window TestCk3 = new(0x100, 1111, 7);

    private static readonly HashSet<ushort> AllowedKeypadScans = new(
        new ushort[] { ScanCodes.LeftCtrl, ScanCodes.LeftShift, ScanCodes.LeftAlt, ScanCodes.F1, ScanCodes.F2, ScanCodes.F3, ScanCodes.F5 }
            .Concat(Enumerable.Range(0, 10).Select(d => ScanCodes.Digit(d))));

    /// <summary>Decodes injected strokes back into keypad keys, checking that every key arrives with Ctrl+Shift+Alt held.</summary>
    private static List<KeypadKey> DecodeChords(IEnumerable<KeyStroke> strokes, out bool modifiersLeftDown)
    {
        var down = new HashSet<ushort>();
        var keys = new List<KeypadKey>();
        var byScan = Enum.GetValues<KeypadKey>().ToDictionary(ScanCodes.ForKeypad);
        foreach (var s in strokes)
        {
            Expect.Equal(KeyStrokeKind.Scan, s.Kind, "keypad uses scan codes only");
            Expect.True(AllowedKeypadScans.Contains(s.Code), $"scan 0x{s.Code:X2} is not a digit/F1/F2/F3/F5/modifier (never letters, F4, F10)");
            if (s.Up) { down.Remove(s.Code); continue; }
            down.Add(s.Code);
            if (byScan.TryGetValue(s.Code, out var key))
            {
                Expect.True(ScanCodes.KeypadModifiers.All(down.Contains), $"{key} pressed without Ctrl+Shift+Alt held");
                keys.Add(key);
            }
        }
        modifiersLeftDown = down.Count > 0;
        return keys;
    }

    private static KeypadOptions FastKeypad(bool holdPerChunk = true, bool probe = false) => new()
    {
        KeyDelay = TimeSpan.Zero, ChunkSize = 12, HoldModifiersPerChunk = holdPerChunk, AckTimeout = TimeSpan.FromSeconds(2), Probe = probe,
    };

    /// <summary>The probe (F3, F2) before the payload: typed only once the keypad answers; an unanswered probe types nothing more.</summary>
    private static async Task KeypadDeliveryProbe()
    {
        var injector = new FakeInjector();
        var hub = new Ck3EventHub();
        AttachMod(injector, hub, 17);
        var delivery = new KeypadDelivery(new FixedCk3(TestCk3), injector, null, hub, SampleMaaTable(), FastKeypad(probe: true), new MemoryJournal());
        Expect.Equal(DeliveryStatus.Delivered, await delivery.DeliverAsync(SampleOutcome(), CancellationToken.None));
        var keys = DecodeChords(injector.Snapshot(), out _);
        Expect.SequenceEqual(new[] { KeypadKey.Reset, KeypadKey.Commit }.Concat(KeypadPayload.Encode(SampleOutcome(), SampleMaaTable()).ToKeySequence()), keys,
            "probe, then the payload");

        // The battle window is closed: nothing answers, so only the two probes are sent.
        var closed = new FakeInjector();
        var opts = FastKeypad(probe: true);
        opts.AckTimeout = TimeSpan.FromMilliseconds(100);
        var blind = new KeypadDelivery(new FixedCk3(TestCk3), closed, null, new Ck3EventHub(), null, opts, new MemoryJournal());
        Expect.Equal(DeliveryStatus.KeypadClosed, await blind.DeliverAsync(SampleOutcome(), CancellationToken.None));
        Expect.SequenceEqual(new[] { KeypadKey.Reset, KeypadKey.Commit, KeypadKey.Reset, KeypadKey.Commit }, DecodeChords(closed.Snapshot(), out bool stuck));
        Expect.False(stuck);
    }

    /// <summary>Plays the CK3 mod: replays chords into a simulator and answers commits on the event hub.</summary>
    private static void AttachMod(FakeInjector injector, Ck3EventHub hub, int bid, Func<int, bool>? acceptCommit = null)
    {
        int commits = 0;
        AttachModAnswering(injector, hub, sim => new InputAck(bid, sim.Committed == true && (acceptCommit?.Invoke(++commits) ?? true), sim.Idx,
            sim.R.Take(KeypadLayout.Checksum).Sum(v => (long)v)));
    }

    private static async Task KeypadDeliverySends()
    {
        foreach (bool holdPerChunk in new[] { true, false })
        {
            var injector = new FakeInjector();
            var hub = new Ck3EventHub();
            AttachMod(injector, hub, 17);
            var delivery = new KeypadDelivery(new FixedCk3(TestCk3), injector, null, hub, SampleMaaTable(), FastKeypad(holdPerChunk), new MemoryJournal());
            Expect.Equal(DeliveryStatus.Delivered, await delivery.DeliverAsync(SampleOutcome(), CancellationToken.None), $"delivered (holdPerChunk {holdPerChunk})");

            var keys = DecodeChords(injector.Snapshot(), out bool stuck);
            Expect.False(stuck, "every key and modifier released");
            Expect.SequenceEqual(KeypadPayload.Encode(SampleOutcome(), SampleMaaTable()).ToKeySequence(), keys);
            Expect.Equal(0, injector.LayoutRequests.Count);
        }
    }

    private static async Task KeypadDeliveryRetry()
    {
        var injector = new FakeInjector();
        var hub = new Ck3EventHub();
        AttachMod(injector, hub, 17, acceptCommit: n => n >= 2);      // first commit -> IN|BAD
        var delivery = new KeypadDelivery(new FixedCk3(TestCk3), injector, null, hub, SampleMaaTable(), FastKeypad(), new MemoryJournal());
        Expect.Equal(DeliveryStatus.Delivered, await delivery.DeliverAsync(SampleOutcome(), CancellationToken.None));
        var keys = DecodeChords(injector.Snapshot(), out _);
        Expect.Equal(2, keys.Count(k => k == KeypadKey.Reset), "resent from a reset");
        Expect.Equal(2, keys.Count(k => k == KeypadKey.Commit));

        // No acknowledgement at all: three attempts (each slower), then failure.
        var silent = new FakeInjector();
        var opts = FastKeypad();
        opts.AckTimeout = TimeSpan.FromMilliseconds(100);
        var failing = new KeypadDelivery(new FixedCk3(TestCk3), silent, null, new Ck3EventHub(), null, opts, new MemoryJournal());
        Expect.Equal(DeliveryStatus.Failed, await failing.DeliverAsync(SampleOutcome(), CancellationToken.None));
        Expect.Equal(3, DecodeChords(silent.Snapshot(), out _).Count(k => k == KeypadKey.Commit));

        // CK3 not running: nothing is sent.
        var none = new FakeInjector();
        var noCk3 = new KeypadDelivery(new FixedCk3(null), none, null, new Ck3EventHub(), null, opts, new MemoryJournal());
        Expect.Equal(DeliveryStatus.Failed, await noCk3.DeliverAsync(SampleOutcome(), CancellationToken.None));
        Expect.Equal(0, none.Snapshot().Count);
    }

    private static async Task KeypadDeliveryFocusLoss()
    {
        var injector = new FakeInjector();
        int batches = 0, pressesAfterChange = 0;
        bool changed = false;
        injector.OnSend = strokes =>
        {
            if (changed) pressesAfterChange += strokes.Count(s => !s.Up);
            if (++batches == 25) { injector.Foreground = 0x999; changed = true; }   // the player alt-tabs mid-burst
        };
        var delivery = new KeypadDelivery(new FixedCk3(TestCk3), injector, null, new Ck3EventHub(), null, FastKeypad(), new MemoryJournal());
        Expect.Equal(DeliveryStatus.Interrupted, await delivery.DeliverAsync(SampleOutcome(), CancellationToken.None), "retryable: nothing applied");
        Expect.Equal(0, pressesAfterChange, "nothing is pressed into the other window");
        DecodeChords(injector.Snapshot(), out bool stuck);
        Expect.False(stuck, "the key in flight and the modifiers are released");
    }

    private static async Task KeypadDeliveryKeysHeld()
    {
        var injector = new FakeInjector { Held = true };   // the player holds a modifier or mouse button
        var options = FastKeypad();
        options.HeldKeysTimeout = TimeSpan.FromMilliseconds(50);
        var delivery = new KeypadDelivery(new FixedCk3(TestCk3), injector, null, new Ck3EventHub(), null, options, new MemoryJournal());
        Expect.Equal(DeliveryStatus.Interrupted, await delivery.DeliverAsync(SampleOutcome(), CancellationToken.None), "retryable");
        Expect.Equal(0, injector.Snapshot().Count, "nothing typed");
    }

    private static async Task KeypadDeliveryLayout()
    {
        var injector = new FakeInjector();
        var hub = new Ck3EventHub();
        AttachMod(injector, hub, 17);
        var inner = injector.OnSend!;
        injector.OnSend = s => { inner(s); injector.Layout = 0x04190419; };   // Windows switched CK3 to Russian mid-burst
        var delivery = new KeypadDelivery(new FixedCk3(TestCk3), injector, null, hub, SampleMaaTable(), FastKeypad(), new MemoryJournal());
        Expect.Equal(DeliveryStatus.Delivered, await delivery.DeliverAsync(SampleOutcome(), CancellationToken.None));
        Expect.True(injector.LayoutRequests.Count > 0 && injector.LayoutRequests.All(h => h == 0x04090409), "US layout requested back");
    }

    private static async Task RunFileDeliveryTypes()
    {
        using var dir = new TempDir();
        var injector = new FakeInjector();
        var hub = new Ck3EventHub();
        injector.OnSend = strokes =>
        {
            // CK3 runs the file when Enter is pressed: the mod logs RES|END.
            if (strokes.Any(s => s is { Kind: KeyStrokeKind.Scan, Code: ScanCodes.Enter, Up: false }))
                Task.Run(() => hub.Publish(new ResultTrace(17, "END", new Dictionary<string, string>())));
        };
        var options = new RunFileOptions { Ck3UserDir = dir.Path, KeyDelay = TimeSpan.Zero, ConsoleOpenDelay = TimeSpan.Zero };
        var delivery = new RunFileDelivery(new FixedCk3(TestCk3), injector, null, hub, null, options, new MemoryJournal());
        string seenDuringTyping = "";
        var inner = injector.OnSend;
        injector.OnSend = s =>
        {
            if (seenDuringTyping.Length == 0 && File.Exists(delivery.RunFilePath)) seenDuringTyping = File.ReadAllText(delivery.RunFilePath);
            inner(s);
        };
        Expect.Equal(DeliveryStatus.Delivered, await delivery.DeliverAsync(SampleOutcome(), CancellationToken.None));

        var strokes = injector.Snapshot();
        string typed = new(strokes.Where(s => s is { Kind: KeyStrokeKind.Unicode, Up: false }).Select(s => (char)s.Code).ToArray());
        Expect.Equal("run ck3xbl_apply.txt", typed);
        var scans = strokes.Where(s => s is { Kind: KeyStrokeKind.Scan, Up: false }).Select(s => s.Code).ToList();
        var bs = ScanCodes.Backspace;
        Expect.SequenceEqual(new[] { ScanCodes.Grave, bs, bs, bs, ScanCodes.Enter, ScanCodes.Grave }, scans,
            "open console, clear the line (the ё a Russian layout types with the console key), Enter, close console");
        Expect.True(seenDuringTyping.Contains("ck3xbl_apply_effect = yes"), "the payload was on disk while typing");
        var after = File.ReadAllBytes(delivery.RunFilePath);
        Expect.True(after[0] == 0xEF && Encoding.UTF8.GetString(after[3..]) == RunFileRenderer.NoOp, "reset to the no-op afterwards (with BOM)");

        // Focus elsewhere: the console is never opened.
        var away = new FakeInjector { Foreground = 0x999 };
        var refused = new RunFileDelivery(new FixedCk3(TestCk3), away, null, new Ck3EventHub(), null, options, new MemoryJournal());
        Expect.Equal(DeliveryStatus.Interrupted, await refused.DeliverAsync(SampleOutcome(), CancellationToken.None), "console never opened: retryable");
        Expect.Equal(0, away.Snapshot().Count);
    }

    private sealed class RecordingDelivery(string name, List<string> log) : ICk3Delivery
    {
        public Task<DeliveryStatus> DeliverAsync(Ck3Outcome outcome, CancellationToken ct)
        {
            log.Add(name);
            return Task.FromResult(DeliveryStatus.Delivered);
        }
    }

    private static async Task DeliveryRouterModes()
    {
        var used = new List<string>();
        var keypad = new RecordingDelivery("keypad", used);
        var run = new RecordingDelivery("runfile", used);
        var mode = BridgeMode.Normal;
        foreach (var channel in new[] { "auto", "runfile", "keypad" })
            await new DeliveryRouter(() => mode, channel, keypad, run, new MemoryJournal()).DeliverAsync(new Ck3Outcome(), CancellationToken.None);
        Expect.SequenceEqual(new[] { "keypad", "keypad", "keypad" }, used, "normal mode never uses the console");
        used.Clear();
        mode = BridgeMode.Dev;
        foreach (var channel in new[] { "auto", "runfile", "keypad" })
            await new DeliveryRouter(() => mode, channel, keypad, run, new MemoryJournal()).DeliverAsync(new Ck3Outcome(), CancellationToken.None);
        Expect.SequenceEqual(new[] { "runfile", "runfile", "keypad" }, used);
    }

    // ===================================================================================================== config, json, files

    private static void ConfigDefaultsAndInvalid()
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        var created = BridgeConfigStore.LoadOrCreate(path, () => new BridgeConfig { Ck3 = { GameDir = "X:/ck3" } }, out var problem);
        Expect.Null(problem);
        Expect.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Expect.True(text.Contains("\"mode\": \"normal\"") && text.Contains("\"gameDir\": \"X:/ck3\"") && text.Contains("\"keyDelayMs\": 12"),
            "camelCase keys, enum as text, every default written");
        Expect.Equal("X:/ck3", created.Ck3.GameDir);

        File.WriteAllText(path, "{ // edited by hand\n \"mode\": \"dev\", \"delivery\": { \"keyDelayMs\": 20, }, \"unknownKey\": 1 }");
        var edited = BridgeConfigStore.LoadOrCreate(path, () => new BridgeConfig(), out problem);
        Expect.Null(problem);
        Expect.Equal(BridgeMode.Dev, edited.Mode);
        Expect.Equal(20, edited.Delivery.KeyDelayMs);
        Expect.Equal(12, edited.Delivery.ChunkSize, "missing keys keep their defaults");

        File.WriteAllText(path, "{ not json");
        var broken = BridgeConfigStore.LoadOrCreate(path, () => new BridgeConfig(), out problem);
        Expect.NotNull(problem);
        Expect.Equal(BridgeMode.Normal, broken.Mode);
        Expect.Equal("{ not json", File.ReadAllText(path), "a broken file is never overwritten");

        var mapper = edited.ToMapperOptions("D:/data");
        Expect.Equal(("D:/data", 10, "order_of_battle"), (mapper.DataDirectory, mapper.ReturnCountdownSeconds, mapper.Deployment));
        Expect.Equal(@"C:\Steam", GameLocator.UnescapeVdf(@"C:\\Steam"));
    }

    private static void JsonConventions()
    {
        var request = new BattleRequest
        {
            Id = "b17_test",
            Attacker = new SideRequest
            {
                Name = "Raiders",
                Troops = { new TroopRequest { Id = "aserai_recruit", Count = 30, Group = "maa:armored_footmen#16706" } },
                Heroes = { new HeroSpec { Key = "h0", Skills = { ["OneHanded"] = 116 }, Appearance = new AppearanceSpec { Morphs = { ["face_width"] = -0.12f } } } },
            },
        };
        var json = ProtocolJson.Serialize(request);
        Expect.True(json.Contains("\"protocol\":2") && json.Contains("\"playerSide\":\"attacker\"") && json.Contains("\"timeOfDay\":12"), json);
        Expect.True(json.Contains("\"OneHanded\":116") && json.Contains("\"face_width\":-0.12"), "dictionary keys verbatim");
        Expect.True(json.Contains("\"group\":\"maa:armored_footmen#16706\""));
        var back = Expect.NotNull(ProtocolJson.Deserialize<BattleRequest>(json));
        Expect.Equal(30, back.Attacker.Troops[0].Count);

        // The module writes with Newtonsoft (camelCase); unknown fields are ignored.
        var status = Expect.NotNull(ProtocolJson.Deserialize<BannerlordStatus>(
            "{\"status\":\"held\",\"battleId\":\"b17_test\",\"t\":1790000000000,\"pid\":4242,\"hwnd\":329874,\"error\":null,\"extra\":true}"));
        Expect.Equal(("held", "b17_test", 4242, 329874L), (status.Status, status.BattleId, status.Pid, status.Hwnd));

        var snapshotJson = ProtocolJson.Serialize(BuildFixtureSnapshot(), indented: true);
        Expect.False(snapshotJson.Contains("\"sides\""), "computed properties are not archived: " +
            string.Join(" ", snapshotJson.Split('\n').Where(l => l.StartsWith("  \"", StringComparison.Ordinal))));
        Expect.True(snapshotJson.Contains("\"facts\""));
    }

    private static async Task BannerlordFiles()
    {
        using var dir = new TempDir();
        using var link = new BannerlordLink(new BannerlordLinkOptions { SharedDir = dir.Path, GameDir = dir.File("nope") }, new MemoryJournal());
        File.WriteAllText(link.ResultPath, "{\"id\":\"old\",\"status\":\"result\"}");
        await link.WriteRequestAsync(new BattleRequest { Id = "b1_t" }, CancellationToken.None);
        Expect.False(File.Exists(link.ResultPath), "stale result removed before a new request");
        var bytes = File.ReadAllBytes(link.RequestPath);
        Expect.True(bytes[0] == (byte)'{', "UTF-8 without BOM");
        Expect.Equal("b1_t", ProtocolJson.TryRead<BattleRequest>(link.RequestPath)?.Id);

        File.WriteAllText(link.ResultPath, "{\"protocol\":2,\"id\":\"b1_t\",\"status\":\"result\",\"winner\":\"defender\",\"attacker\":{\"killed\":3}}");
        Expect.Equal("defender", link.ReadResult("b1_t")?.Winner);
        Expect.Null(link.ReadResult("b2_t"), "a result for another battle is ignored");
        File.WriteAllText(link.ResultPath, "{\"protocol\":2,\"id\":\"b1_");
        Expect.Null(link.ReadResult("b1_t"), "a half-written file is tolerated");

        await link.SendCommandAsync(BannerlordCommands.Go, CancellationToken.None);
        Expect.False(File.Exists(link.CommandPath), "commands are dropped while Bannerlord is not running");
        Expect.Equal("handoff_to 1234", BannerlordCommands.HandoffTo(1234));
    }

    private static async Task BannerlordCommandsNeverOverwritten()
    {
        using var dir = new TempDir();
        using var link = new BannerlordLink(new BannerlordLinkOptions { SharedDir = dir.Path, GameDir = dir.File("nope") }, new MemoryJournal());
        link.AttachForTests(Process.GetCurrentProcess());
        // 'fg' is still waiting in command.txt: the module's main thread is stalled by a load step.
        File.WriteAllText(link.CommandPath, "fg");
        var taken = new List<string>();
        var module = Task.Run(async () =>
        {
            await Task.Delay(400);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (taken.Count < 2 && DateTime.UtcNow < deadline)
            {
                // Like BridgeIo.TryTake: read the whole file, then delete it.
                try
                {
                    var text = File.ReadAllText(link.CommandPath);
                    File.Delete(link.CommandPath);
                    taken.Add(text);
                }
                catch (IOException) { }
                await Task.Delay(20);
            }
        });
        Expect.True(await link.SendCommandAsync(BannerlordCommands.Go, CancellationToken.None), "'go' taken");
        await module;
        Expect.SequenceEqual(new[] { "fg", "go" }, taken, "the pending command is never replaced");
    }

    private static async Task BannerlordTransportResetAndWithdraw()
    {
        using var dir = new TempDir();
        using var link = new BannerlordLink(new BannerlordLinkOptions { SharedDir = dir.Path, GameDir = dir.File("nope") }, new MemoryJournal());
        var leftovers = new[] { link.RequestPath, link.CommandPath, link.RequestPath + ".tmp", link.CommandPath + ".tmp" };
        foreach (var f in leftovers) File.WriteAllText(f, "x");
        File.WriteAllText(link.StatusPath, "{}");
        link.ResetTransport();
        Expect.False(leftovers.Any(File.Exists), "an earlier run's request and commands are gone");
        Expect.True(File.Exists(link.StatusPath), "the module's own files are left alone");

        await link.WriteRequestAsync(new BattleRequest { Id = "b2_t" }, CancellationToken.None);
        await link.WithdrawRequestAsync("b1_t", CancellationToken.None);
        Expect.True(File.Exists(link.RequestPath), "another battle's request is kept");
        await link.WithdrawRequestAsync("b2_t", CancellationToken.None);
        Expect.False(File.Exists(link.RequestPath));
        Expect.Equal("cancel b2_t", BannerlordCommands.CancelBattle("b2_t"));
        Expect.True(BannerlordCommands.IsCancel("cancel") && BannerlordCommands.IsCancel("cancel b2_t") && !BannerlordCommands.IsCancel("cancelled"));
    }

    private static void BannerlordAdoptsOnlyBridgeProcesses()
    {
        using var dir = new TempDir();
        using var link = new BannerlordLink(new BannerlordLinkOptions { SharedDir = dir.Path, GameDir = dir.File("nope") }, new MemoryJournal());
        var now = DateTime.UtcNow;
        long t = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        var idle = new BannerlordStatus { Status = BannerlordStates.Idle, Pid = 4242, T = t };
        Expect.True(link.ProvesBridge(4242, idle, now), "a fresh heartbeat names it");
        Expect.False(link.ProvesBridge(5151, idle, now), "the heartbeat is another process's: the player's own game");
        Expect.False(link.ProvesBridge(4242, idle, now.AddSeconds(30)), "stale heartbeat");
        Expect.True(link.ProvesBridge(4242, new BannerlordStatus { Status = BannerlordStates.Loading, Pid = 4242, T = t }, now.AddSeconds(30)),
            "a load step may keep it silent longer");
        // CK3Bridge in the module list proves nothing: a player may tick the (then passive) module for their own game.
        Expect.False(link.ProvesBridge(5151, null, now), "no heartbeat, whatever the command line says");

        var own = Win32.CommandLineOf(Environment.ProcessId);
        Expect.True(own?.Contains("CK3xBL.SelfTest", StringComparison.OrdinalIgnoreCase) == true, $"own command line read: {own ?? "null"}");
        Expect.Null(Win32.CommandLineOf(int.MaxValue - 1), "no such process");
    }

    private static void Ck3LaunchArgsAndPause()
    {
        Expect.Equal("-gdpr-compliant", Ck3Launcher.Arguments(BridgeMode.Normal, false));
        Expect.Equal("-debug_mode -suppress_error_log -gdpr-compliant -continuelastsave", Ck3Launcher.Arguments(BridgeMode.Dev, true));

        using var dir = new TempDir();
        using var pause = new PauseStateReader(dir.File("dedicated_server.log"));
        var seen = new List<bool>();
        pause.Changed += seen.Add;
        Expect.Null(pause.IsPaused);
        pause.Apply("[18:06:56][I][jominiapplication.cpp:261]: Pause Toggled! Paused: false, PlayerID: 0");
        pause.Apply("[18:06:58][I][jominiapplication.cpp:261]: Pause Toggled! Paused: true, PlayerID: 0");
        pause.Apply("[18:06:58][I][jominiapplication.cpp:261]: Changing game speed to: 2");
        Expect.Equal(true, pause.IsPaused);
        Expect.SequenceEqual(new[] { false, true }, seen);
    }

    // Live-test fix (PROTOCOL §16): the bridge used to replay an earlier CK3 session's dedicated_server.log as
    // hundreds of "[ck3] paused/running" lines at start-up. The existing content is read silently; one Attached line says the state.
    private static void PauseAttachNeverReplaysHistory()
    {
        const string On = "[18:06:58][I][jominiapplication.cpp:261]: Pause Toggled! Paused: true, PlayerID: 0";
        const string Off = "[18:06:59][I][jominiapplication.cpp:261]: Pause Toggled! Paused: false, PlayerID: 0";
        using var dir = new TempDir();
        var path = dir.File("dedicated_server.log");
        File.WriteAllText(path, "[18:00:00][I][pdx_bootstrap.cpp:1]: session one\n" + string.Concat(Enumerable.Repeat(Off + "\n" + On + "\n", 300)));

        bool running = true;
        using var pause = new PauseStateReader(path, () => running);
        var seen = new List<bool>();
        var attached = new List<(bool? Paused, string Why)>();
        int restarts = 0;
        pause.Changed += seen.Add;
        pause.Attached += (p, why) => attached.Add((p, why));
        pause.Restarted += () => restarts++;
        pause.Poll();
        Expect.Equal(0, seen.Count, "600 old toggles are not replayed");
        Expect.Equal(1, attached.Count);
        Expect.Equal(true, attached[0].Paused, "a running CK3's file: its last toggle is the state");
        Expect.True(pause.IsAttached && pause.IsPaused == true);

        File.AppendAllText(path, Off + "\n");
        pause.Poll();
        Expect.SequenceEqual(new[] { false }, seen, "after the attach every toggle is live");

        // CK3 relaunched: the file is recreated; the new session's toggles are live, the attach is not repeated.
        File.WriteAllText(path, "[20:00:00][I][pdx_bootstrap.cpp:1]: session two\n" + On + "\n");
        pause.Poll();
        Expect.Equal(1, restarts);
        Expect.SequenceEqual(new[] { false, true }, seen);
        Expect.Equal(1, attached.Count);

        // A file a closed CK3 left behind is history: the state stays unknown, still nothing replayed.
        running = false;
        using var stale = new PauseStateReader(path, () => running);
        var staleSeen = new List<bool>();
        string? why = null;
        bool? staleState = false;
        stale.Changed += staleSeen.Add;
        stale.Attached += (p, w) => { staleState = p; why = w; };
        stale.Poll();
        Expect.True(why?.Contains("closed", StringComparison.Ordinal) == true, why ?? "no attach");
        Expect.Null(staleState);
        Expect.Null(stale.IsPaused);
        Expect.Equal(0, staleSeen.Count);

        // No file yet: attached at once (unknown); the file CK3 then creates is the new session, live from its first line.
        var missing = dir.File("dedicated_server_2.log");
        using var fresh = new PauseStateReader(missing);
        var freshSeen = new List<bool>();
        int freshAttached = 0;
        fresh.Changed += freshSeen.Add;
        fresh.Attached += (_, _) => freshAttached++;
        fresh.Poll();
        Expect.Equal(1, freshAttached);
        Expect.Null(fresh.IsPaused);
        File.WriteAllText(missing, "[21:00:00][I][pdx_bootstrap.cpp:1]: session three\n" + Off + "\n");
        fresh.Poll();
        Expect.SequenceEqual(new[] { false }, freshSeen);
        Expect.Equal(1, freshAttached);
    }

    private static void TailerAtEnd()
    {
        using var dir = new TempDir();
        var path = dir.File("dedicated_server.log");
        File.WriteAllText(path, string.Concat(Enumerable.Range(0, 20).Select(i => $"[18:00:{i:00}][I][a.cpp:1]: line {i}\n")));
        var f = new LogFollower(path, startAtEnd: false, maxReadBytes: 64);
        int lines = 0, polls = 0;
        do { lines += f.Poll().Lines.Count; polls++; } while (!f.AtEnd && polls < 100);
        Expect.True(polls > 1, "the backlog took several slices");
        Expect.Equal(20, lines);
        var none = new LogFollower(dir.File("none.log"), startAtEnd: false);
        Expect.Equal(0, none.Poll().Lines.Count);
        Expect.True(none.AtEnd, "a missing file has nothing to read");
    }

    // PROTOCOL §16: ck3xbl_c_absent on the player's C record; a leftover of the key in a later record's dump is not that character's.
    private static void SnapshotPlayerAbsent()
    {
        var log = BattleLog();
        int cmd = log.IndexOf(Marker("C|CMD|def"));
        log[cmd] = Marker("C|PLAYER|def");
        int bidLine = log.FindIndex(cmd + 1, l => l.StartsWith("ck3xbl_bid:", StringComparison.Ordinal));
        log.Insert(bidLine + 1, "ck3xbl_c_absent: 1.00");
        // The knight's dump after it carries the key too (saved values persist in the chain).
        int knight = log.IndexOf(Marker("C|KNIGHT|def"));
        int knightBid = log.FindIndex(knight + 1, l => l.StartsWith("ck3xbl_bid:", StringComparison.Ordinal));
        log.Insert(knightBid + 1, "ck3xbl_c_absent: 1.00");

        var warnings = new List<string>();
        var s = DecodeLog(log, warnings).OfType<SnapshotCompleted>().Single().Snapshot;
        var gunda = s.Defender.Characters.Single(c => c.Ref.Ck3Id == 10811);
        Expect.True(gunda.IsPlayer && !gunda.IsCommander && gunda.Role == "PLAYER");
        Expect.Equal(true, gunda.Absent);
        Expect.Null(s.Defender.Characters.Single(c => c.Ref.Ck3Id == 23538).Absent, "not the knight's key");

        log[log.FindIndex(l => l == "ck3xbl_c_absent: 1.00")] = "ck3xbl_c_absent: 0.00";
        var present = DecodeLog(log, warnings).OfType<SnapshotCompleted>().Single().Snapshot;
        Expect.Equal(false, present.Defender.Characters.Single(c => c.Ref.Ck3Id == 10811).Absent);
        Expect.Null(BuildFixtureSnapshot().Defender.Characters.Single(c => c.Ref.Ck3Id == 10811).Absent, "an older mod logs no key");
    }

    // A result.json an earlier run (or a test) left behind is no battle of this session: skipped quietly (live test).
    private static async Task BannerlordStaleResultIgnored()
    {
        using var dir = new TempDir();
        var journal = new MemoryJournal();
        using var link = new BannerlordLink(new BannerlordLinkOptions { SharedDir = dir.Path, GameDir = dir.File("nope") }, journal);
        var seen = new List<string>();
        link.ResultUpdated += r => seen.Add(r.Id!);
        File.WriteAllText(link.ResultPath, "{\"protocol\":2,\"id\":\"t1_knights\",\"status\":\"finished\",\"winner\":\"attacker\"}");
        link.WatchResult();
        Expect.Equal(0, seen.Count, "an old result is not reported");
        Expect.False(journal.Lines.Any(l => l.Contains("result.json", StringComparison.Ordinal)), "and not logged");

        await link.WriteRequestAsync(new BattleRequest { Id = "b1_t" }, CancellationToken.None);
        File.WriteAllText(link.ResultPath, "{\"protocol\":2,\"id\":\"b1_t\",\"status\":\"result\",\"winner\":\"defender\"}");
        link.WatchResult();
        Expect.SequenceEqual(new[] { "b1_t" }, seen, "a battle of this run is reported");
        Expect.True(journal.Lines.Any(l => l.Contains("result.json: b1_t result", StringComparison.Ordinal)));
        Expect.True(link.IsRequestedThisRun("b1_t") && !link.IsRequestedThisRun("t1_knights"));
    }

    // ===================================================================================================== orchestrator

    private sealed class Rig : IAsyncDisposable
    {
        public readonly FakeMapper Mapper = new();
        public readonly FakeUx Ux = new();
        public readonly FakeDesktop Desktop = new();
        public readonly FakeBannerlord Bannerlord;
        public readonly FakeDelivery Delivery = new();
        public readonly FakeNotifier Notifier = new();
        public readonly MemoryJournal Journal = new();
        public readonly FixedCk3 Ck3 = new(TestCk3);
        public readonly MemoryArchive Archive = new();
        public readonly BattleOrchestrator Orchestrator;
        /// <summary>battleClock.reinforcementClash of the mapper options.</summary>
        public volatile bool ReinforcementClash = true;
        /// <summary>What PauseStateReader says about CK3 (null = unknown).</summary>
        public bool? Ck3Paused { get; set; }
        private readonly ConcurrentQueue<SteeringProgress> _steering = new();

        public Rig(Action<OrchestratorOptions>? configure = null)
        {
            Bannerlord = new FakeBannerlord(Desktop);
            Ux.Desktop = Desktop;
            Orchestrator = new BattleOrchestrator(new OrchestratorServices
            {
                Mapper = Mapper,
                Appearance = new NullAppearance(),
                Ux = Ux,
                Delivery = Delivery,
                Bannerlord = Bannerlord,
                Ck3 = Ck3,
                Desktop = Desktop,
                Notifier = Notifier,
                Archive = Archive,
                Journal = Journal,
                MapperOptions = () => new MapperOptions { BattleClock = { ReinforcementClash = ReinforcementClash } },
                Ck3Paused = () => Ck3Paused,
            }, Options(configure));
            Orchestrator.SteeringChanged += _steering.Enqueue;
            Orchestrator.Start();
        }

        /// <summary>Every SteeringChanged of a battle, in order.</summary>
        public List<SteeringProgress> SteeringOf(int bid) => _steering.Where(p => p.Bid == bid).ToList();

        public Task Steering(int bid, SteeringState state) =>
            Until(() => SteeringOf(bid).LastOrDefault()?.State == state, $"battle {bid} steering {state} (now {SteeringOf(bid).LastOrDefault()?.State})");

        /// <summary>An ST record of the mod (values as CK3 prints them, "2116.00").</summary>
        public void St(int bid, string stage, params (string Key, int Value)[] values) =>
            Orchestrator.OnCk3Event(new SteeringTrace(bid, stage, values.ToDictionary(v => v.Key, v => v.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))));

        private static OrchestratorOptions Options(Action<OrchestratorOptions>? configure)
        {
            var options = new OrchestratorOptions
            {
                TickInterval = TimeSpan.FromMilliseconds(10),
                FgSettle = TimeSpan.Zero,
                TrimCk3Memory = false,   // the rig's CK3 is a fake window: never trim a real process (ProcessMemory has its own test)
                HandoffWait = TimeSpan.FromMilliseconds(300),
                ResultWait = TimeSpan.FromMilliseconds(300),
                QuietInput = TimeSpan.Zero,
                Ck3FadeOut = TimeSpan.Zero, Ck3FadeIn = TimeSpan.Zero, BannerlordFadeIn = TimeSpan.Zero, BannerlordFadeOut = TimeSpan.Zero,
            };
            configure?.Invoke(options);
            return options;
        }

        public void Snapshot(int bid, string session = "test", int parentBid = 0, int clash = 1) =>
            Orchestrator.OnCk3Event(new SnapshotCompleted(new BattleSnapshot { Bid = bid, SessionKey = session, CombatName = $"Battle {bid}",
                Location = { ProvinceId = 8199, ProvinceName = "Orangallu" }, ParentBid = parentBid, Clash = clash }));

        public void Choice(int bid, Ck3Choice choice) => Orchestrator.OnCk3Event(new ChoiceMade(bid, choice, choice.ToString().ToUpperInvariant()));

        public void Status(string status, string? battleId, int pid = FakeBannerlord.Pid) =>
            Orchestrator.OnBannerlordStatus(new BannerlordStatus { Status = status, BattleId = battleId, Pid = pid });

        public int Count(string command) => Bannerlord.Log.Count(c => c == command);

        public async Task Phase(BattlePhase phase)
        {
            if (!await Orchestrator.WaitForPhaseAsync(phase, TimeSpan.FromSeconds(5)))
                throw new ExpectationException($"phase {phase} not reached (now {Orchestrator.Phase});\n  {string.Join("\n  ", Journal.Lines.TakeLast(12))}");
        }

        public static async Task Until(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new ExpectationException($"timed out waiting for {what}");
                await Task.Delay(5);
            }
        }

        public async ValueTask DisposeAsync() => await Orchestrator.DisposeAsync();
    }

    private sealed class FakeMapper : IBattleMapper
    {
        /// <summary>presentation.inEngineTitle of the requests (the real mapper sets false, PROTOCOL §13.6).</summary>
        public volatile bool InEngineTitle = true;
        /// <summary>Payload version of the outcomes while the options allow version 4 (the real mapper: 4 with the battle clock on).</summary>
        public volatile int PayloadVersion = KeypadLayout.PayloadVersion;
        private readonly object _gate = new();
        private readonly List<(BattleSnapshot Planned, ParentLink? Parent, MapperOptions Options)> _outcomes = new();
        private readonly List<(BattleSnapshot Live, ParentLink Parent, BattleSnapshot Capped)> _continuations = new();
        public List<(BattleSnapshot Planned, ParentLink? Parent, MapperOptions Options)> Outcomes { get { lock (_gate) return _outcomes.ToList(); } }
        public List<(BattleSnapshot Live, ParentLink Parent, BattleSnapshot Capped)> Continuations { get { lock (_gate) return _continuations.ToList(); } }

        public Ck3Outcome BuildOutcome(BattleSnapshot battle, BattleRequest request, BattleResult result, MapperOptions options, ParentLink? parent)
        {
            lock (_gate) _outcomes.Add((battle, parent, options));
            var outcome = BuildOutcome(battle, request, result, options);
            if (options.BattleClock.Enabled && options.BattleClock.MaxPayloadVersion >= KeypadLayout.SteerPayloadVersion) outcome.PayloadVersion = PayloadVersion;
            outcome.Characters.Add(new CharacterFate { Tag = 30 + battle.Bid, Fate = 3 });   // captured: left out of a later clash
            return outcome;
        }

        public ContinuationPlan BuildContinuation(BattleSnapshot live, ParentLink parent, MapperOptions options)
        {
            var capped = new BattleSnapshot { Bid = live.Bid, SessionKey = live.SessionKey, CombatName = live.CombatName, Location = live.Location,
                ParentBid = live.ParentBid, Clash = live.Clash };
            lock (_gate) _continuations.Add((live, parent, capped));
            var request = BuildRequest(capped, options);
            request.Presentation!.Title = "Reinforcements arrive!";
            return new ContinuationPlan { Request = request, Capped = capped };
        }

        public BattleRequest BuildRequest(BattleSnapshot battle, MapperOptions options) => new()
        {
            Id = battle.RequestId,
            PlayerSide = "defender",
            Presentation = new Presentation
            {
                Title = $"The {battle.CombatName}", Subtitle = "sub", AttackerCoaPng = "att.png", DefenderCoaPng = "def.png", InEngineTitle = InEngineTitle,
            },
        };

        public Ck3Outcome BuildOutcome(BattleSnapshot battle, BattleRequest request, BattleResult result, MapperOptions options) => new()
        {
            OutcomeCode = result.Winner == "attacker" ? 1 : 2,
            PlayerWon = result.Winner == "defender",
            MyLost = 10, EnemyLost = 20,
            Summary = "Our losses 10 · Enemy losses 20\nSir Nachana fell",
        };
    }

    private sealed class FakeUx : ITransitionUx
    {
        private readonly List<string> _calls = new();
        public FakeDesktop? Desktop;
        public List<string> Calls { get { lock (_calls) return _calls.ToList(); } }
        public TitleCard? LastTitle, LastResult;
        private void Log(string s) { lock (_calls) _calls.Add(s); }

        public void Initialize(UxOptions options) { }
        public Task ShowTitleCardAsync(TitleCard card, CancellationToken ct) { LastTitle = card; Log("title"); return Task.CompletedTask; }
        public Task HideAsync(CancellationToken ct) { Log("hide"); return Task.CompletedTask; }
        public Task ShowResultCardAsync(TitleCard card, CancellationToken ct) { LastResult = card; Log("result"); return Task.CompletedTask; }

        /// <summary>What the last ShowTitleCardAsync achieved (false = the overlay declined the card).</summary>
        public bool TitleCardPresented { get; set; } = true;
        /// <summary>Completes the dissolve of RevealAsync (null = at once).</summary>
        public TaskCompletionSource? RevealGate;

        public async Task RevealAsync(TimeSpan hold, TimeSpan dissolve, CancellationToken ct)
        {
            Log($"reveal:{hold.TotalMilliseconds}:{dissolve.TotalMilliseconds}");
            if (RevealGate is { } gate) await gate.Task.WaitAsync(ct);
            Log("revealed");
        }

        public bool BringToFront(nint hwnd)
        {
            Log($"front:{hwnd:X}");
            if (Desktop is not null) Desktop.Foreground = hwnd;
            return true;
        }

        public Task RampProcessVolumeAsync(int pid, float target, TimeSpan duration, CancellationToken ct) { Log($"ramp:{pid}:{target}"); return Task.CompletedTask; }
        public Task RestoreProcessVolumeAsync(int pid, TimeSpan duration, CancellationToken ct) { Log($"restore:{pid}"); return Task.CompletedTask; }
        public void Dispose() { }
    }

    private sealed class FakeDesktop : IDesktop
    {
        public volatile nint Foreground = TestCk3.Hwnd;
        public nint ForegroundWindow => Foreground;
        public bool IsWindow(nint hwnd) => hwnd != 0;
        public int ProcessIdOf(nint hwnd) => hwnd == TestCk3.Hwnd ? TestCk3.ProcessId : hwnd == FakeBannerlord.Window ? FakeBannerlord.Pid : 9999;
        public Task<bool> WaitForQuietInputAsync(TimeSpan quietFor, TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
        public void ReleaseCursorClip() { }
    }

    private sealed class FakeBannerlord(FakeDesktop desktop) : IBannerlordLink
    {
        public const int Pid = 4242;
        public static readonly nint Window = 0x500;
        private readonly List<string> _log = new();
        public BattleResult? Result;
        public List<string> Log { get { lock (_log) return _log.ToList(); } }

        public BannerlordStatus? Status { get; set; } = new() { Status = BannerlordStates.Idle, Pid = Pid };
        public volatile bool Running = true;
        public bool IsRunning => Running;
        public int ProcessId => Running ? Pid : 0;
        public nint MainWindow => Window;
        public nint ForegroundBeforeLaunch => 0;
        public string? Problem { get; set; }
        public event Action<BannerlordStatus>? StatusChanged { add { } remove { } }
        public event Action<BannerlordLoss, string>? Lost { add { } remove { } }
        public event Action<BattleResult>? ResultUpdated { add { } remove { } }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task WriteRequestAsync(BattleRequest request, CancellationToken ct)
        {
            lock (_log) _log.Add("request:" + request.Id);
            return Task.CompletedTask;
        }

        public Task WithdrawRequestAsync(string battleId, CancellationToken ct)
        {
            lock (_log) _log.Add("withdraw:" + battleId);
            return Task.CompletedTask;
        }

        public Task<bool> SendCommandAsync(string command, CancellationToken ct)
        {
            lock (_log) _log.Add(command);
            // The module hands the foreground to CK3 itself.
            if (command.StartsWith("handoff_to ", StringComparison.Ordinal)) desktop.Foreground = (nint)long.Parse(command[11..]);
            return Task.FromResult(true);
        }

        public BattleResult? ReadResult(string battleId) => Result?.Id == battleId ? Result : null;
        public void DeleteResult() { }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeDelivery : ICk3Delivery
    {
        public readonly List<Ck3Outcome> Delivered = new();
        /// <summary>Answers for the next calls (then Delivered).</summary>
        public readonly Queue<DeliveryStatus> Script = new();
        public int Calls;
        public volatile int MaxVersion = KeypadLayout.SteerPayloadVersion;
        /// <summary>Runs before a delivery is acknowledged (what CK3 logs meanwhile, or a version fallback).</summary>
        public Func<Ck3Outcome, Task>? During;

        public int MaxPayloadVersion => MaxVersion;

        public async Task<DeliveryStatus> DeliverAsync(Ck3Outcome outcome, CancellationToken ct)
        {
            lock (Delivered)
            {
                Calls++;
                if (Script.TryDequeue(out var scripted)) return scripted;
            }
            if (During is { } during) await during(outcome);
            lock (Delivered) Delivered.Add(outcome);
            return DeliveryStatus.Delivered;
        }

        public List<Ck3Outcome> Snapshot() { lock (Delivered) return Delivered.ToList(); }
    }

    private sealed class FakeNotifier : INotifier
    {
        private readonly List<string> _notes = new();
        public List<string> Notes { get { lock (_notes) return _notes.ToList(); } }
        public void Notify(string title, string text, NoticeKind kind = NoticeKind.Info) { lock (_notes) _notes.Add($"{kind}: {title}: {text}"); }
    }

    private sealed class NullAppearance : IAppearanceProvider
    {
        public void Start(string ck3DocumentsDir, string ck3GameDir, string cacheDir) { }
        public void Stop() { }
        public void Enrich(BattleSnapshot battle, BattleRequest request) { }
    }

    private sealed class MemoryArchive : IBattleArchive
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string Id, string Name), object> _saved = new();
        private readonly List<(string Id, string Name, object Value)> _appended = new();

        public void Save(string battleId, string name, object value) { lock (_gate) _saved[(battleId, name)] = value; }
        public void Append(string battleId, string name, object value) { lock (_gate) _appended.Add((battleId, name, value)); }

        public T? TryLoad<T>(string idPrefix, string name) where T : class
        {
            lock (_gate) return _saved.Where(kv => kv.Key.Id.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase) && kv.Key.Name == name)
                .Select(kv => kv.Value as T).LastOrDefault(v => v is not null);
        }

        public object? Saved(string battleId, string name) { lock (_gate) return _saved.GetValueOrDefault((battleId, name)); }
        public int Appended(string battleId, string name) { lock (_gate) return _appended.Count(a => a.Id == battleId && a.Name == name); }
    }

    // Live b298 (2026-10-20): CK3 logged the battle before a crash and a restart; the save was loaded with its call to arms open, and
    // FIELD found no prepared battle. The snapshot comes back from the battle archive and the battle is staged as chosen.
    private static async Task OrchestratorRestoresArchivedSnapshot()
    {
        await using var rig = new Rig();
        rig.Archive.Save("b42_old", "snapshot", new BattleSnapshot { Bid = 42, SessionKey = "before", CombatName = "Battle 42",
            Location = { ProvinceId = 8199, ProvinceName = "Orangallu" } });
        rig.Choice(42, Ck3Choice.Field);
        await rig.Phase(BattlePhase.Entering);
        await Rig.Until(() => rig.Bannerlord.Log.Contains("go"), "'go' for the restored battle");
        Expect.True(rig.Journal.Lines.Any(l => l.Contains("restored from the battle archive", StringComparison.Ordinal)), "the journal says so");

        await using var empty = new Rig();
        empty.Choice(43, Ck3Choice.Field);
        await Rig.Until(() => empty.Notifier.Notes.Any(n => n.Contains("No Bannerlord battle is prepared", StringComparison.Ordinal)),
            "no archive: the old notice");
    }

    private static async Task EnterBattle(Rig rig, int bid)
    {
        rig.Snapshot(bid);
        await rig.Phase(BattlePhase.Offered);
        rig.Choice(bid, Ck3Choice.Field);
        await rig.Phase(BattlePhase.Entering);
        await Rig.Until(() => rig.Bannerlord.Log.Contains("go"), "'go'");
        rig.Status(BannerlordStates.Visible, $"b{bid}_test");
        await rig.Phase(BattlePhase.Fighting);
    }

    private static async Task OrchestratorHappyPath()
    {
        await using var rig = new Rig();
        await EnterBattle(rig, 17);
        Expect.Equal("request:b17_test", rig.Bannerlord.Log[0]);
        var bl = rig.Bannerlord.Log;
        Expect.True(bl.IndexOf("fg") < bl.IndexOf("go"), "uncloak before go");
        var ux = rig.Ux.Calls;
        Expect.Equal("title", ux[0]);
        Expect.True(ux.Contains("ramp:1111:0") && ux.Contains("front:500"), string.Join(",", ux));
        Expect.Equal("The Battle 17", rig.Ux.LastTitle?.Title);
        Expect.Equal(("def.png", "att.png"), (rig.Ux.LastTitle?.LeftCoaPng, rig.Ux.LastTitle?.RightCoaPng), "player's arms on the left");
        await Rig.Until(() => rig.Ux.Calls.Contains("hide"), "overlay hidden once the battle is visible");

        rig.Bannerlord.Result = new BattleResult { Id = "b17_test", Status = "result", Winner = "defender" };
        rig.Status(BannerlordStates.Result, "b17_test");
        await rig.Phase(BattlePhase.ResultShown);
        rig.Status(BannerlordStates.FadingOut, "b17_test");
        await rig.Phase(BattlePhase.Idle);

        var outcome = rig.Delivery.Delivered.Single();
        Expect.Equal((17, 8199L, 2, true), (outcome.Bid, outcome.ProvinceId, outcome.OutcomeCode, outcome.PlayerWon));
        Expect.True(rig.Bannerlord.Log.Contains("handoff_to 256"), "module hands focus to the CK3 window");
        Expect.Equal("Victory at Orangallu", rig.Ux.LastResult?.Title);
        Expect.Equal("Our losses 10 · Enemy losses 20", rig.Ux.LastResult?.Subtitle);
        await Rig.Until(() => rig.Ux.Calls.Contains("restore:1111"), "CK3 audio restored");
        var calls = rig.Ux.Calls;
        Expect.True(calls.IndexOf("result") < calls.LastIndexOf("hide"), "result card hidden after delivery");
        Expect.False(rig.Bannerlord.Log.Any(BannerlordCommands.IsCancel));
    }

    private static async Task OrchestratorIdleAfterResult()
    {
        // The module passed fading_out and black between two status polls: the next status seen is idle (no battle id).
        await using var rig = new Rig();
        await EnterBattle(rig, 17);
        rig.Bannerlord.Result = new BattleResult { Id = "b17_test", Status = "finished", Winner = "attacker" };
        rig.Status(BannerlordStates.Result, "b17_test");
        await rig.Phase(BattlePhase.ResultShown);
        rig.Status(BannerlordStates.Idle, null);
        await rig.Phase(BattlePhase.Idle);
        Expect.Equal(1, rig.Delivery.Delivered.Single().OutcomeCode);
    }

    private static async Task OrchestratorFinalResultWithoutStatus()
    {
        // Only result.json tells: the final result alone ends the battle; an error result while entering fails it cleanly.
        await using var rig = new Rig();
        await EnterBattle(rig, 17);
        var final = new BattleResult { Id = "b17_test", Status = "finished", Winner = "defender" };
        rig.Bannerlord.Result = final;
        rig.Orchestrator.OnBannerlordResult(final);
        await rig.Phase(BattlePhase.Idle);
        Expect.Equal(2, rig.Delivery.Delivered.Single().OutcomeCode);

        rig.Snapshot(18);
        await rig.Phase(BattlePhase.Offered);
        rig.Choice(18, Ck3Choice.Field);
        await rig.Phase(BattlePhase.Entering);
        rig.Orchestrator.OnBannerlordResult(new BattleResult { Id = "b18_test", Status = "error", Error = "unknown troop" });
        await rig.Phase(BattlePhase.Idle);
        await Rig.Until(() => rig.Notifier.Notes.Any(n => n.Contains("unknown troop")), "error notice");
        Expect.Equal(1, rig.Delivery.Delivered.Count);
    }

    private static async Task OrchestratorAutoCancels()
    {
        await using var rig = new Rig();
        rig.Snapshot(17);
        await rig.Phase(BattlePhase.Offered);
        rig.Choice(17, Ck3Choice.Auto);
        await rig.Phase(BattlePhase.Idle);
        await Rig.Until(() => rig.Bannerlord.Log.Contains("cancel b17_test"), "cancel naming the battle");
        Expect.Equal(0, rig.Ux.Calls.Count, "the player never saw anything");

        // FIELD for a battle the bridge does not know: tell the player how to continue.
        rig.Choice(99, Ck3Choice.Field);
        await Rig.Until(() => rig.Notifier.Notes.Any(n => n.Contains("run its course")), "notice");
    }

    private static async Task OrchestratorWaitsForFocus()
    {
        await using var rig = new Rig();
        rig.Snapshot(17);
        await rig.Phase(BattlePhase.Offered);
        rig.Desktop.Foreground = 0x900;                      // the player clicked, then alt-tabbed to another app
        rig.Choice(17, Ck3Choice.Field);
        await rig.Phase(BattlePhase.WaitingToEnter);
        await Task.Delay(100);
        Expect.Equal(0, rig.Ux.Calls.Count, "no overlay, no focus stealing while another app is in front");
        Expect.True(rig.Notifier.Notes.Any(n => n.Contains("return to Crusader Kings III")));
        rig.Desktop.Foreground = TestCk3.Hwnd;
        await rig.Phase(BattlePhase.Entering);
        await Rig.Until(() => rig.Bannerlord.Log.Contains("go"), "'go' once CK3 is back in front");
    }

    private static async Task OrchestratorAbortWhileFighting()
    {
        await using var rig = new Rig();
        await EnterBattle(rig, 17);
        rig.Orchestrator.OnAbortHotkey();
        await Rig.Until(() => rig.Bannerlord.Log.Contains("abort"), "'abort'");
        rig.Bannerlord.Result = new BattleResult { Id = "b17_test", Status = "aborted", Winner = "none" };
        rig.Status(BannerlordStates.Aborted, "b17_test");
        await rig.Phase(BattlePhase.Idle);
        Expect.Equal(3, rig.Delivery.Delivered.Single().OutcomeCode, "abort applies casualties only");
        Expect.Equal("The fighting at Orangallu is broken off", rig.Ux.LastResult?.Title);

        // During the countdown the hotkey means return_now; before the battle is shown it cancels.
        await EnterBattle(rig, 18);
        rig.Bannerlord.Result = new BattleResult { Id = "b18_test", Status = "result", Winner = "attacker" };
        rig.Status(BannerlordStates.Result, "b18_test");
        await rig.Phase(BattlePhase.ResultShown);
        rig.Orchestrator.OnAbortHotkey();
        await Rig.Until(() => rig.Bannerlord.Log.Contains("return_now"), "'return_now'");
        rig.Status(BannerlordStates.Black, "b18_test");
        await rig.Phase(BattlePhase.Idle);
        Expect.Equal("Defeat at Orangallu", rig.Ux.LastResult?.Title);

        rig.Snapshot(19);
        await rig.Phase(BattlePhase.Offered);
        rig.Orchestrator.OnAbortHotkey();
        await rig.Phase(BattlePhase.Idle);
        await Rig.Until(() => rig.Bannerlord.Log.Count(BannerlordCommands.IsCancel) == 1 && rig.Bannerlord.Log.Contains("cancel b19_test"), "cancel of the preload");
    }

    private static async Task OrchestratorBannerlordLost()
    {
        await using var rig = new Rig();
        await EnterBattle(rig, 17);
        rig.Desktop.Foreground = 0;                           // the game window vanished with the crash
        rig.Orchestrator.OnBannerlordLost(BannerlordLoss.Exited, "Bannerlord exited (code -1)");
        await rig.Phase(BattlePhase.Idle);
        await Rig.Until(() => rig.Ux.Calls.Contains("restore:1111"), "CK3 audio restored");
        Expect.True(rig.Ux.Calls.Contains("front:100"), "CK3 brought back (the foreground was nobody's)");
        Expect.True(rig.Notifier.Notes.Any(n => n.StartsWith("Warning") && n.Contains("run its course")));
        Expect.Equal(0, rig.Delivery.Delivered.Count);

        Expect.False(rig.Bannerlord.Log.Any(BannerlordCommands.IsCancel), "nothing to cancel in a process that exited");

        // Exit while only preloading: nothing visible; the request is withdrawn (in order behind its write) and written once more
        // when the relaunched process reports idle.
        rig.Desktop.Foreground = TestCk3.Hwnd;
        rig.Snapshot(18);
        await rig.Phase(BattlePhase.Offered);
        rig.Orchestrator.OnBannerlordLost(BannerlordLoss.Exited, "Bannerlord exited (code -1)");
        rig.Status(BannerlordStates.Idle, null, pid: 5555);
        await Rig.Until(() => rig.Count("request:b18_test") == 2, "preload repeated after the restart");
        var log = rig.Bannerlord.Log;
        Expect.True(log.IndexOf("request:b18_test") < log.IndexOf("withdraw:b18_test") && log.IndexOf("withdraw:b18_test") < log.LastIndexOf("request:b18_test"),
            string.Join(",", log));
        Expect.Equal(BattlePhase.Offered, rig.Orchestrator.Phase);
        rig.Status(BannerlordStates.Idle, null, pid: 5555);
        await Task.Delay(100);
        Expect.Equal(2, rig.Count("request:b18_test"), "written once per process");
    }

    private static async Task OrchestratorQueue()
    {
        await using var rig = new Rig();
        rig.Snapshot(17);
        rig.Snapshot(18);
        await rig.Phase(BattlePhase.Offered);
        Expect.Equal("b17_test", rig.Orchestrator.ActiveBattleId);
        rig.Choice(18, Ck3Choice.Field);                      // the player answered the second event first
        await Rig.Until(() => rig.Orchestrator.ActiveBattleId == "b18_test", "b18 staged");
        await rig.Phase(BattlePhase.Entering);
        var log = rig.Bannerlord.Log;
        Expect.True(log.IndexOf("cancel b17_test") > log.IndexOf("request:b17_test") && log.IndexOf("cancel b17_test") < log.IndexOf("request:b18_test"),
            "b17's preload is cancelled before b18's request: " + string.Join(",", log));
        await Rig.Until(() => rig.Bannerlord.Log.Contains("go"), "'go'");
        rig.Status(BannerlordStates.Visible, "b18_test");
        await rig.Phase(BattlePhase.Fighting);
        rig.Bannerlord.Result = new BattleResult { Id = "b18_test", Status = "result", Winner = "defender" };
        rig.Status(BannerlordStates.Black, "b18_test");
        // b18 done -> b17 comes back from the queue and is preloaded again.
        await Rig.Until(() => rig.Bannerlord.Log.Count(c => c == "request:b17_test") == 2, "b17 preloaded again");
        await rig.Phase(BattlePhase.Offered);
        Expect.Equal("b17_test", rig.Orchestrator.ActiveBattleId);
    }

    /// <summary>PROTOCOL §10.2: when the player takes a new battle to the field while an older pending one went stale, the mod logs
    /// CHOICE|ABANDON with the stale bid and CHOICE|FIELD with the new one in the same tick. The bridge ends the stale battle like a
    /// player's abandon and stages the new one; an ABANDON of a battle it no longer knows is ignored.</summary>
    private static async Task OrchestratorStaleAbandonBeforeField()
    {
        await using var rig = new Rig();
        rig.Snapshot(17);
        await rig.Phase(BattlePhase.Offered);
        rig.Desktop.Foreground = 0x900;                      // the player chose to fight, then left for another app
        rig.Choice(17, Ck3Choice.Field);
        await rig.Phase(BattlePhase.WaitingToEnter);
        rig.Snapshot(18);                                    // days later (CK3 unpaused) another battle begins: it queues
        await Rig.Until(() => rig.Journal.Lines.Any(l => l.Contains("b18_test: queued", StringComparison.Ordinal)), "b18 queued");

        rig.Desktop.Foreground = TestCk3.Hwnd;
        rig.Choice(5, Ck3Choice.Abandon);                    // a battle the bridge dropped long ago: nothing to do
        rig.Choice(17, Ck3Choice.Abandon);                   // the mod gives up the stale battle ...
        rig.Choice(18, Ck3Choice.Field);                     // ... and takes the new one to the field
        await Rig.Until(() => rig.Orchestrator.ActiveBattleId == "b18_test", "b18 staged");
        await rig.Phase(BattlePhase.Entering);
        var log = rig.Bannerlord.Log;
        Expect.True(log.Contains("cancel b17_test") && log.IndexOf("cancel b17_test") < log.IndexOf("request:b18_test"),
            "the stale battle is cancelled before the new request: " + string.Join(",", log));
        Expect.False(log.Any(c => c.Contains("b5_", StringComparison.Ordinal)), "nothing is sent for an unknown battle");
        await Rig.Until(() => rig.Bannerlord.Log.Contains("go"), "'go' for the new battle");
    }

    private static async Task OrchestratorCk3Restart()
    {
        await using var rig = new Rig();
        rig.Snapshot(17);
        await rig.Phase(BattlePhase.Offered);
        rig.Orchestrator.OnCk3Event(new LogRestarted());
        await rig.Phase(BattlePhase.Idle);
        await Rig.Until(() => rig.Bannerlord.Log.Contains("cancel b17_test"), "cancel naming the battle");

        // A snapshot from a new session (older save loaded) supersedes battles of the old timeline.
        rig.Snapshot(5, session: "s1");
        await rig.Phase(BattlePhase.Offered);
        rig.Snapshot(3, session: "s2");
        await Rig.Until(() => rig.Orchestrator.ActiveBattleId == "b3_s2", "new timeline's battle active");
    }

    private static async Task OrchestratorUnresponsiveKeepsPreload()
    {
        // Only the heartbeat went stale (e.g. the PC slept): the process still holds the battle, so it is never requested twice.
        await using var rig = new Rig();
        rig.Snapshot(17);
        await rig.Phase(BattlePhase.Offered);
        rig.Status(BannerlordStates.Held, "b17_test");
        rig.Orchestrator.OnBannerlordLost(BannerlordLoss.Unresponsive, "heartbeat 6.0 s old (status held)");
        rig.Status(BannerlordStates.Held, "b17_test");          // the link raises the status again when the heartbeat recovers
        rig.Choice(17, Ck3Choice.Field);
        await rig.Phase(BattlePhase.Entering);
        await Rig.Until(() => rig.Bannerlord.Log.Contains("go"), "'go'");
        Expect.Equal(1, rig.Count("request:b17_test"));
        Expect.False(rig.Bannerlord.Log.Any(c => c.StartsWith("withdraw:", StringComparison.Ordinal)));
    }

    private static async Task OrchestratorHeldBattleNotRequestedAgain()
    {
        // The process the bridge followed exited (Lost: Exited), yet the Bannerlord now in use already holds the battle (e.g. a
        // launcher stub exited after handing over): the status decides, and the request is not written a second time.
        await using var rig = new Rig();
        rig.Snapshot(17);
        await rig.Phase(BattlePhase.Offered);
        rig.Orchestrator.OnBannerlordLost(BannerlordLoss.Exited, "Bannerlord exited (code 0)");
        rig.Bannerlord.Status = new BannerlordStatus { Status = BannerlordStates.Held, BattleId = "b17_test", Pid = FakeBannerlord.Pid };
        rig.Choice(17, Ck3Choice.Field);
        await rig.Phase(BattlePhase.Entering);
        await Rig.Until(() => rig.Bannerlord.Log.Contains("go"), "'go'");
        Expect.Equal(1, rig.Count("request:b17_test"));
        Expect.True(rig.Journal.Lines.Any(l => l.Contains("already holds it")));
    }

    private static async Task OrchestratorStraySessionCancelled()
    {
        await using var rig = new Rig();
        rig.Status(BannerlordStates.Held, "b5_old");            // left by an earlier bridge run
        await Rig.Until(() => rig.Count("cancel b5_old") == 1, "stray session cancelled");
        rig.Status(BannerlordStates.Held, "b5_old");            // seen again (e.g. heartbeat recovered): no second cancel
        rig.Snapshot(17);
        await rig.Phase(BattlePhase.Offered);
        rig.Status(BannerlordStates.Held, "b17_test");          // our own battle
        rig.Choice(17, Ck3Choice.Auto);
        await rig.Phase(BattlePhase.Idle);
        rig.Status(BannerlordStates.Held, "b17_test");          // its cancel is still on its way: not sent twice
        await Task.Delay(100);
        Expect.Equal(1, rig.Count("cancel b5_old"));
        Expect.Equal(1, rig.Count("cancel b17_test"));
        Expect.Equal(2, rig.Bannerlord.Log.Count(BannerlordCommands.IsCancel));
    }

    private static async Task OrchestratorCloaksAgain()
    {
        await using var rig = new Rig();
        rig.Status(BannerlordStates.Idle, null);                 // first idle of this process: cloak and throttle it
        await Rig.Until(() => rig.Count("bg") == 1, "'bg' at the first idle");
        rig.Status(BannerlordStates.Booting, null);
        rig.Status(BannerlordStates.Idle, null);                 // same process back at idle
        rig.Status(BannerlordStates.Idle, null, pid: 5555);      // restarted from the tray: a new process
        await Rig.Until(() => rig.Count("bg") == 2, "'bg' for the restarted process");

        // Abandoned while entering (after 'fg'): cancelled, then cloaked again.
        rig.Snapshot(17);
        await rig.Phase(BattlePhase.Offered);
        rig.Choice(17, Ck3Choice.Field);
        await rig.Phase(BattlePhase.Entering);
        await Rig.Until(() => rig.Bannerlord.Log.Contains("go"), "'go'");
        rig.Orchestrator.OnAbortHotkey();
        await rig.Phase(BattlePhase.Idle);
        await Rig.Until(() => rig.Bannerlord.Log.LastIndexOf("bg") > rig.Bannerlord.Log.IndexOf("cancel b17_test"), "'bg' after the cancel");
        await Rig.Until(() => rig.Ux.Calls.Contains("restore:1111"), "CK3 audio restored");
    }

    private static async Task OrchestratorReentersWhenHeld()
    {
        await using var rig = new Rig();
        rig.Snapshot(17);
        await rig.Phase(BattlePhase.Offered);
        rig.Choice(17, Ck3Choice.Field);
        await rig.Phase(BattlePhase.Entering);
        await Rig.Until(() => rig.Bannerlord.Log.Contains("go"), "'go'");
        await Task.Delay(100);                                   // the enter activity has finished
        rig.Status(BannerlordStates.Held, "b17_test");          // still held: 'fg' did not take effect
        await Rig.Until(() => rig.Count("fg") >= 2 && rig.Count("go") >= 2, "'fg' and 'go' sent again");
        var log = rig.Bannerlord.Log;
        Expect.True(log.LastIndexOf("fg") < log.LastIndexOf("go"));
        rig.Status(BannerlordStates.Visible, "b17_test");
        await rig.Phase(BattlePhase.Fighting);
    }

    private static async Task OrchestratorDeliveryWaitsForKeys()
    {
        // A held key or mouse button (CK3 still in front) postpones delivery; it uses up no attempt and does not drop the battle.
        await using (var rig = new Rig())
        {
            for (int i = 0; i < 3; i++) rig.Delivery.Script.Enqueue(DeliveryStatus.Interrupted);
            await EnterBattle(rig, 17);
            rig.Bannerlord.Result = new BattleResult { Id = "b17_test", Status = "finished", Winner = "defender" };
            rig.Status(BannerlordStates.Black, "b17_test");
            await rig.Phase(BattlePhase.Idle);
            Expect.Equal(4, rig.Delivery.Calls);
            Expect.Equal(1, rig.Delivery.Delivered.Count);
            Expect.False(rig.Notifier.Notes.Any(n => n.Contains("could not be entered")));
        }

        // Still held when the patience is over: the battle is dropped with a notice and the card is taken down.
        await using (var rig = new Rig(o => o.InputBusyPatience = TimeSpan.Zero))
        {
            rig.Delivery.Script.Enqueue(DeliveryStatus.Interrupted);
            await EnterBattle(rig, 17);
            rig.Bannerlord.Result = new BattleResult { Id = "b17_test", Status = "finished", Winner = "defender" };
            rig.Status(BannerlordStates.Black, "b17_test");
            await rig.Phase(BattlePhase.Idle);
            await Rig.Until(() => rig.Notifier.Notes.Any(n => n.Contains("stayed held")), "notice");
            await Rig.Until(() => rig.Ux.Calls.LastIndexOf("hide") > rig.Ux.Calls.LastIndexOf("result"), "result card hidden");
            await Rig.Until(() => rig.Ux.Calls.Contains("restore:1111"), "CK3 audio restored");
            Expect.Equal(0, rig.Delivery.Delivered.Count);
        }
    }

    private static async Task OrchestratorDeliveryWaitsForKeypad()
    {
        // The battle window did not answer the probe: nothing is spent, the probe goes again after the pause.
        await using (var rig = new Rig(o => o.KeypadReprobe = TimeSpan.FromMilliseconds(200)))
        {
            rig.Delivery.Script.Enqueue(DeliveryStatus.KeypadClosed);
            await EnterBattle(rig, 17);
            rig.Bannerlord.Result = new BattleResult { Id = "b17_test", Status = "finished", Winner = "defender" };
            rig.Status(BannerlordStates.Black, "b17_test");
            await rig.Phase(BattlePhase.Idle);
            Expect.Equal(2, rig.Delivery.Calls);
            Expect.Equal(1, rig.Delivery.Delivered.Count);
            Expect.True(rig.Notifier.Notes.Any(n => n.Contains("battle window")), "the player is told to bring the battle window back");
        }

        // Timed probes used up: the next one waits until the player has been away from CK3 and is back.
        await using (var rig = new Rig(o => { o.KeypadReprobe = TimeSpan.FromMilliseconds(50); o.KeypadTimedProbes = 0; }))
        {
            rig.Delivery.Script.Enqueue(DeliveryStatus.KeypadClosed);
            await EnterBattle(rig, 17);
            rig.Bannerlord.Result = new BattleResult { Id = "b17_test", Status = "finished", Winner = "defender" };
            rig.Status(BannerlordStates.Black, "b17_test");
            await rig.Phase(BattlePhase.AwaitingReturnFocus);
            await Task.Delay(400);
            Expect.Equal(1, rig.Delivery.Calls, "no probe while the player stays in CK3");
            rig.Desktop.Foreground = 0x900;
            await Task.Delay(300);
            rig.Desktop.Foreground = TestCk3.Hwnd;
            await rig.Phase(BattlePhase.Idle);
            Expect.Equal(1, rig.Delivery.Delivered.Count);
        }
    }

    private static async Task OrchestratorReloadWhileFighting()
    {
        // The player alt-tabbed to CK3 mid-battle and loaded an older save: the battle is dropped and the presentation undone.
        await using var rig = new Rig();
        await EnterBattle(rig, 17);
        rig.Snapshot(3, session: "s2");
        await Rig.Until(() => rig.Orchestrator.ActiveBattleId == "b3_s2", "new timeline's battle active");
        await Rig.Until(() => rig.Ux.Calls.Contains("restore:1111"), "CK3 audio restored");
        await Rig.Until(() => rig.Bannerlord.Log.LastIndexOf("bg") > rig.Bannerlord.Log.IndexOf("cancel b17_test") &&
                              rig.Bannerlord.Log.Contains("cancel b17_test"), "cancelled, then cloaked");
    }

    private static async Task OrchestratorForeignBannerlord()
    {
        // The player's own Bannerlord is open: nothing is launched, the player is told why, and the battle fails cleanly.
        const string problem = "Bannerlord is already running without the CK3 bridge module.";
        await using var rig = new Rig(o => o.EnterTimeout = TimeSpan.FromMilliseconds(300));
        rig.Bannerlord.Running = false;
        rig.Bannerlord.Status = null;
        rig.Bannerlord.Problem = problem;
        rig.Snapshot(17);
        await rig.Phase(BattlePhase.Offered);
        rig.Choice(17, Ck3Choice.Field);
        await rig.Phase(BattlePhase.WaitingToEnter);
        Expect.True(rig.Notifier.Notes.Any(n => n.Contains(problem)), string.Join(" | ", rig.Notifier.Notes));
        await rig.Phase(BattlePhase.Idle);
        await Rig.Until(() => rig.Bannerlord.Log.Contains("withdraw:b17_test"), "request withdrawn (nobody may take it later)");
        Expect.True(rig.Notifier.Notes.Any(n => n.StartsWith("Warning") && n.Contains("run its course")));
        Expect.Equal(0, rig.Ux.Calls.Count, "nothing was shown");
    }

    // ===================================================================================================== knights & commanders

    private static List<Ck3Event> DecodeLog(IEnumerable<string> log, List<string> warnings)
    {
        var events = new List<Ck3Event>();
        var decoder = new Ck3EventDecoder(events.Add, warnings.Add, sessionKey: "test");
        foreach (var r in Ck3RecordParser.ParseAll(log)) decoder.Accept(r);
        decoder.Flush();
        return events;
    }

    private static void KnightsSnapshotFacts()
    {
        var warnings = new List<string>();
        var s = DecodeLog(KnightsBattleLog(), warnings).OfType<SnapshotCompleted>().Single().Snapshot;
        Expect.True(warnings.Count == 0, "decoder warnings: " + string.Join(" | ", warnings));
        Expect.True(s.KnightFactsLogged, "an S dump has ck3xbl_s_has_cmd");

        Expect.Equal((20002L, (int?)2), (s.Attacker.Commander?.Ck3Id ?? 0, s.Attacker.FaithHostilityOut));
        Expect.Null(s.Defender.Commander, "s_has_cmd = 0: the attackers' s_cmd left in the dump is not the defenders' commander");
        Expect.Equal(0, s.Defender.FaithHostilityOut);

        var (a5, a6) = (s.Attacker.Armies[0], s.Attacker.Armies[1]);
        Expect.Equal((1500, true, "hostile"), (a5.KnightEffectivenessPermille, a5.Besieging, a5.Territory));
        Expect.SequenceEqual(new[] { "greatest_of_khans" }, a5.OwnerTraits, "AX|otrait, duplicates once");
        Expect.SequenceEqual(new[] { "formation_fighting_strategist" }, a5.OwnerCultureParams);
        Expect.SequenceEqual(new[] { "tenet_mountain_worship_trait_bonuses" }, a5.OwnerFaithParams);
        Expect.Equal(("kirati", false), (a5.OwnerCulture.Culture, a5.OwnerNomadic), "the older AX fields still apply");
        Expect.Equal((-200, false, "other"), (a6.KnightEffectivenessPermille, a6.Besieging, a6.Territory), "a computed value <= 0 is kept raw");
        var a7 = s.Defender.Armies.Single();
        Expect.Equal(((int?)null, (bool?)null, "own"), (a7.KnightEffectivenessPermille, a7.Besieging, a7.Territory));

        var raider = s.Attacker.Characters.Single(c => c.Tag == 3);
        Expect.SequenceEqual(new[] { "brave" }, raider.CombatTraits);
        Expect.SequenceEqual(new[] { "reaver" }, raider.CommanderTraits, "CX|ctrait unchanged");
        Expect.True(raider.Traits.Count == 0, "CX|ktrait does not feed the appearance traits");
        Expect.Equal(3, raider.TraitLevels["forest_fighter"], "the highest level logged wins");
        Expect.Equal(2, raider.TraitLevels["tourney_participant:foot"], "a tracked trait keeps its track in the key");
        Expect.Equal(2, raider.TraitLevels.Count);
        Expect.SequenceEqual(new[] { "forest_trait_bonuses" }, raider.CultureParams);
        Expect.SequenceEqual(new[] { "tenet_mountain_worship_trait_bonuses" }, raider.FaithParams);
        var acc = Expect.NotNull(raider.Accolade);
        Expect.Equal((true, 3, "archer_attribute"), (acc.Active, acc.Rank, acc.Primary));
        Expect.SequenceEqual(new[] { ("archer_attribute", 2), ("stalwart_attribute", 1) }, acc.Levels.Select(l => (l.Key, l.Value)));
        Expect.SequenceEqual(new[]
            {
                ("primary_armament", "sword", "famed", (string?)null), ("armor", "mail", "illustrious", "armor_helmet_nasal"),
                ("helmet", "unknown", "common", null),
            },
            raider.Artifacts.Select(a => (a.SlotType, a.Type, a.Rarity, a.HelmetFeature)), "the helmet shape attaches to the armor before it");

        var knight = s.Attacker.Characters.Single(c => c.Tag == 4);
        Expect.Equal(4, knight.TraitLevels["the_wake"], "a 4-step chain");
        Expect.False(Expect.NotNull(knight.Accolade).Active, "acc_active|no");

        var gunda = s.Defender.Characters.Single();
        Expect.True(gunda.CombatTraits.Count == 0 && gunda.TraitLevels.Count == 0 && gunda.Accolade is null && gunda.Artifacts.Count == 0,
            "a character without knights lines keeps the neutral values");
    }

    private static void KnightsNeutralDefaults()
    {
        // Today's mod (CoreFixtures.BattleLog has none of the §13.2 lines or keys): everything stays "not logged".
        var s = BuildFixtureSnapshot();
        Expect.False(s.KnightFactsLogged);
        foreach (var side in s.Sides)
        {
            Expect.True(side.Commander is null && side.FaithHostilityOut is null, $"{side.Key}: no commander or hostility");
            foreach (var a in side.Armies)
                Expect.True(a is { KnightEffectivenessPermille: null, Besieging: null, Territory: null, OwnerTraits.Count: 0,
                    OwnerCultureParams.Count: 0, OwnerFaithParams.Count: 0 }, $"army {a.Tag}: neutral knights facts");
            foreach (var c in side.Characters)
                Expect.True(c is { CombatTraits.Count: 0, TraitLevels.Count: 0, CultureParams.Count: 0, FaithParams.Count: 0, Accolade: null,
                    Artifacts.Count: 0 }, $"character {c.Tag}: neutral knights facts");
        }
    }

    private static void KnightsMalformedLines()
    {
        var log = new List<string> { Marker("B|BEGIN") };
        log.AddRange(Dump(CombatPrint, ("ck3xbl_bid", "3.00")));
        log.Add(Marker("A|def"));
        log.AddRange(Dump($"Host {Weak("Army", 60)}", ("ck3xbl_bid", "3.00"), ("ck3xbl_a_tag", "2.00")));
        log.Add(Marker("AX|territory|enemy"));
        log.Add(Marker("C|KNIGHT|def"));
        log.AddRange(Dump(Lowborn, ("ck3xbl_bid", "3.00"), ("ck3xbl_c_tag", "9.00")));
        foreach (var l in new[] { "artifact_helmet|armor_helmet_nasal", "txp|brave", "txp|forest_fighter|x", "txp|forest_fighter|0",
                     "acc_rank|high", "acc_type|archer_attribute", "artifact|armor|mail", "txp|forest_fighter|1" })
            log.Add(Marker("CX|" + l));
        log.Add(Marker("B|END"));
        log.AddRange(Dump(CombatPrint, ("ck3xbl_bid", "3.00")));

        var warnings = new List<string>();
        var s = DecodeLog(log, warnings).OfType<SnapshotCompleted>().Single().Snapshot;
        var lineWarnings = warnings.Where(w => w.StartsWith("CX|", StringComparison.Ordinal) || w.StartsWith("AX|", StringComparison.Ordinal)).ToList();
        Expect.Equal(8, lineWarnings.Count, "one warning per malformed line: " + string.Join(" | ", warnings));
        Expect.Null(s.Defender.Armies.Single().Territory, "an unknown territory value is not taken");
        var c = s.Defender.Characters.Single();
        Expect.True(c.Artifacts.Count == 0 && c.Accolade is null, "malformed artifact and accolade lines change nothing");
        Expect.SequenceEqual(new[] { ("forest_fighter", 1) }, c.TraitLevels.Select(t => (t.Key, t.Value)), "only the well-formed txp line");
    }

    private static void KnightsJoinCarriesFacts()
    {
        var warnings = new List<string>();
        var events = DecodeLog(KnightsBattleLog(), warnings);
        Expect.True(warnings.Count == 0, "decoder warnings: " + string.Join(" | ", warnings));
        var join = events.OfType<ArmyJoined>().Single();
        Expect.Equal((8, 1150, false, "own"), (join.Army.Tag, join.Army.KnightEffectivenessPermille, join.Army.Besieging, join.Army.Territory));
        Expect.SequenceEqual(new[] { "excommunicated" }, join.Army.OwnerTraits);
        var knight = join.Characters.Single();
        Expect.SequenceEqual(new[] { "berserker" }, knight.CombatTraits);
        Expect.Equal(("primary_armament", "unknown", "masterwork"), (knight.Artifacts.Single().SlotType, knight.Artifacts.Single().Type, knight.Artifacts.Single().Rarity));
        // Battle gear (PROTOCOL §16.2), in log order.
        Expect.Equal("ck3xbl_main:vlandia_sword_3_t4,ck3xbl_horse:t2_vlandia_horse", string.Join(",", knight.Gear.Select(g => $"{g.Slot}:{g.ItemId}")));
    }

    /// <summary>The outcome of the reference vectors of PROTOCOL §13.5 (one army, one killed character), without awards.</summary>
    private static Ck3Outcome AwardBaseOutcome() => new()
    {
        Bid = 3, ProvinceId = 8199, OutcomeCode = 2, MyLost = 300, EnemyLost = 900, MyKills = 7,
        Armies = { new ArmyLoss { ArmyTag = 12, LevyLossPermille = 500, Maa = { new MaaLoss { MaaKey = "armored_footmen", MaaIndex = 48, SurvivorSharePermille = 400 } } } },
        Characters = { new CharacterFate { Tag = 5, Fate = 4, KillerTag = 2 } },
    };

    private static void KnightsAwardEncoderVector()
    {
        // PROTOCOL §13.5 encoder vector: node tools/gen/keypad_payload.mjs 1=3 2=2 3=8199 4=300 5=900 6=7 10=12 11=500 12=48 13=400
        //   122=7 123=13062 124=9 125=40203 130=5 131=4 132=2
        var o = AwardBaseOutcome();
        o.Awards.Add(new CharacterAward { Tag = 7, Prestige = 325, Glory = 30, ProwessRolls = 2, Name = "Sir Odo", PlayerSide = true });
        o.Awards.Add(new CharacterAward { Tag = 9, Prestige = 1000, Glory = 100, ProwessRolls = 3 });
        o.Awards.Add(new CharacterAward { Tag = 11 });                                  // nothing earned: no slot
        var warnings = new List<string>();
        var p = KeypadPayload.Encode(o, null, warnings);
        Expect.True(warnings.Count == 0, string.Join(" | ", warnings));
        var r = p.Registers;
        Expect.Equal((7, 13062, 9, 40203), (r[122], r[123], r[124], r[125]), "slots 0 and 1, no gap");
        Expect.True(r.Skip(126).Take(4).All(v => v == 0) && r.Skip(190).Take(8).All(v => v == 0), "the other award slots stay 0");
        Expect.Equal(4016, r[KeypadLayout.Checksum]);
        const string reference = "F3 3 F1 3 F1 2 F1 8 1 9 9 F1 3 0 0 F1 9 0 0 F1 7 F1 1 0 F5 1 2 F1 5 0 0 F1 4 8 F1 4 0 0 F1 1 2 2 F5 7 F1 " +
            "1 3 0 6 2 F1 9 F1 4 0 2 0 3 F1 1 3 0 F5 5 F1 4 F1 2 F1 1 9 9 F5 4 0 1 6 F1 F2";
        var keys = p.ToKeySequence();
        Expect.Equal(79, keys.Count);
        Expect.Equal(reference, string.Join(" ", keys.Select(KeyName)));
        var sim = new KeypadSimulator();
        foreach (var k in keys) sim.Press(k);
        Expect.True(sim.Committed == true, "the payload commits");

        // Units are rounded, out-of-range amounts clamped (with a warning), rolls capped at 3.
        Expect.Equal(3 + 10 * 20 + 1000 * 40, KeypadPayload.AwardValue(new CharacterAward { Prestige = 5000, Glory = 400, ProwessRolls = 9 }, warnings));
        Expect.Equal(3, warnings.Count);
        Expect.Equal(1 + 10 * 3 + 1000 * 13, KeypadPayload.AwardValue(new CharacterAward { Prestige = 320, Glory = 13, ProwessRolls = 1 }), "13 units of 25, 3 of 5 (rounded)");
    }

    private static void KnightsUnharmedNotWritten()
    {
        var with = SampleOutcome();
        with.Characters.Insert(0, new CharacterFate { Tag = 40, Fate = 0 });
        with.Characters.Add(new CharacterFate { Tag = 41, Fate = 0, Kills = 12 });
        var without = SampleOutcome();
        without.Characters.RemoveAll(c => c.Fate == 0);
        var a = KeypadPayload.Encode(with, SampleMaaTable());
        var b = KeypadPayload.Encode(without, SampleMaaTable());
        Expect.SequenceEqual(b.Registers, a.Registers, "unharmed characters change no register");
        Expect.SequenceEqual(b.ToKeySequence(), a.ToKeySequence());
        Expect.Equal((2, 1, 0, 3, 4, 2), (a.Registers[130], a.Registers[131], a.Registers[132], a.Registers[133], a.Registers[134], a.Registers[135]),
            "the harmed characters fill the slots from r130");

        // More than 20 harmed characters: those with a killer are kept first.
        var many = new Ck3Outcome { Bid = 1, OutcomeCode = 1 };
        for (int i = 0; i < 24; i++) many.Characters.Add(new CharacterFate { Tag = 100 + i, Fate = 1, KillerTag = i >= 20 ? 7 : 0 });
        var warnings = new List<string>();
        var p = KeypadPayload.Encode(many, null, warnings);
        var tags = Enumerable.Range(0, KeypadLayout.CharacterSlots).Select(j => p.Registers[KeypadLayout.CharacterBase + KeypadLayout.CharacterStride * j]).ToList();
        Expect.True(new[] { 120, 121, 122, 123 }.All(tags.Contains), "the killed-by-a-character ones are kept");
        Expect.True(warnings.Any(w => w.Contains("24 harmed characters")), string.Join(" | ", warnings));
    }

    /// <summary>Eight awards of 3-digit tags and 5-digit values (each a valid A ≤ 40,203).</summary>
    private static List<CharacterAward> EightAwards() => new[]
        {
            (101, 600, 40, 1), (102, 975, 80, 1), (103, 475, 90, 2), (104, 700, 85, 0),
            (105, 750, 25, 2), (106, 725, 0, 1), (107, 600, 55, 1), (108, 650, 50, 1),
        }
        .Select(a => new CharacterAward { Tag = a.Item1, Prestige = a.Item2, Glory = a.Item3, ProwessRolls = a.Item4 }).ToList();

    private static void KnightsAwardKeyCap()
    {
        // Base: r121 and r189 unused, one harmed character at r130..r132 (so the first block's seek disappears once r129 is used).
        var bare = KeypadPayload.Encode(AwardBaseOutcome(), null);
        int baseKeys = bare.ToKeySequence().Count;
        KeypadPayload WithAwards(int n, int? budget, List<string>? warnings = null)
        {
            var o = AwardBaseOutcome();
            o.Awards.AddRange(EightAwards().Take(n));
            return KeypadPayload.Encode(o, null, warnings, budget);
        }
        var added = Enumerable.Range(1, 8).Select(n => WithAwards(n, null).ToKeySequence().Count - baseKeys).ToList();
        Expect.SequenceEqual(new[] { 14, 24, 34, 39, 54, 64, 74, 84 }, added, "keys the award block adds for 1..8 awards");

        foreach (var (budget, kept) in new[] { (90, 8), (84, 8), (83, 7), (40, 4), (39, 4), (38, 3), (14, 1), (13, 0), (0, 0) })
        {
            var warnings = new List<string>();
            var p = WithAwards(8, budget, warnings);
            var expected = WithAwards(kept, null);
            Expect.SequenceEqual(expected.Registers, p.Registers, $"budget {budget}: the first {kept} awards");
            Expect.SequenceEqual(expected.ToKeySequence(), p.ToKeySequence(), $"budget {budget}: key sequence");
            Expect.Equal(8 - kept, warnings.Count(w => w.Contains("dropped")), $"budget {budget}: one warning per dropped award");
            var outside = Enumerable.Range(0, KeypadLayout.Checksum).Where(i => i is < KeypadLayout.AwardBlockA or >= KeypadLayout.AwardBlockA + 8
                and (< KeypadLayout.AwardBlockB or >= KeypadLayout.AwardBlockB + 8));
            Expect.True(outside.All(i => p.Registers[i] == bare.Registers[i]), $"budget {budget}: army and character registers unchanged");
        }
        Expect.SequenceEqual(WithAwards(8, null).Registers, WithAwards(8, int.MaxValue).Registers, "no budget = every award");

        // Slot 8 and beyond: at most eight awards reach CK3, in priority order.
        var o9 = AwardBaseOutcome();
        o9.Awards.AddRange(EightAwards());
        o9.Awards.Add(new CharacterAward { Tag = 109, Prestige = 25 });
        var nine = new List<string>();
        Expect.SequenceEqual(WithAwards(8, null).Registers, KeypadPayload.Encode(o9, null, nine).Registers);
        Expect.True(nine.Single().Contains("character 109"), string.Join(" | ", nine));
        Expect.Equal((108, 26101), (KeypadPayload.Encode(o9, null).Registers[KeypadLayout.AwardSlot(7)], KeypadPayload.Encode(o9, null).Registers[197]));
    }

    private static async Task KnightsKeypadDeliveryCap()
    {
        var injector = new FakeInjector();
        var hub = new Ck3EventHub();
        AttachMod(injector, hub, 3);
        var options = FastKeypad();
        options.AwardKeyBudget = 40;
        var journal = new MemoryJournal();
        var delivery = new KeypadDelivery(new FixedCk3(TestCk3), injector, null, hub, null, options, journal);
        var outcome = AwardBaseOutcome();
        outcome.Awards.AddRange(EightAwards());
        Expect.Equal(DeliveryStatus.Delivered, await delivery.DeliverAsync(outcome, CancellationToken.None));

        var sent = DecodeChords(injector.Snapshot(), out _);
        var sim = new KeypadSimulator();
        foreach (var k in sent) sim.Press(k);
        Expect.Equal((101, 104, 0), (sim.R[KeypadLayout.AwardSlot(0)], sim.R[KeypadLayout.AwardSlot(3)], sim.R[KeypadLayout.AwardSlot(4)]),
            "the configured cap keeps the first four awards");
        var lines = journal.Lines;
        Expect.Equal(4, lines.Count(l => l.StartsWith("Warn [keypad] award for character", StringComparison.Ordinal) && l.Contains("dropped")));
        Expect.True(lines.Any(l => l.StartsWith("Info [keypad] battle 3: " + sent.Count + " keys in ", StringComparison.Ordinal) && l.Contains(" s, IN|OK")),
            "key count and time to IN|OK journaled: " + string.Join(" | ", lines));
    }

    private static async Task KnightsRunFileCarriesAwards()
    {
        using var dir = new TempDir();
        var injector = new FakeInjector();
        var hub = new Ck3EventHub();
        var delivery = new RunFileDelivery(new FixedCk3(TestCk3), injector, null, hub, null,
            new RunFileOptions { Ck3UserDir = dir.Path, KeyDelay = TimeSpan.Zero, ConsoleOpenDelay = TimeSpan.Zero }, new MemoryJournal());
        string body = "";
        injector.OnSend = strokes =>
        {
            if (!strokes.Any(s => s is { Kind: KeyStrokeKind.Scan, Code: ScanCodes.Enter, Up: false })) return;
            body = File.ReadAllText(delivery.RunFilePath);
            Task.Run(() => hub.Publish(new ResultTrace(3, "END", new Dictionary<string, string>())));
        };
        var outcome = AwardBaseOutcome();
        outcome.Awards.AddRange(EightAwards());
        Expect.Equal(DeliveryStatus.Delivered, await delivery.DeliverAsync(outcome, CancellationToken.None));
        var registers = Enumerable.Range(0, KeypadLayout.AwardSlots).SelectMany(k => new[] { KeypadLayout.AwardSlot(k), KeypadLayout.AwardSlot(k) + 1 });
        foreach (int reg in registers)
            Expect.True(body.Contains($"\tset_variable = {{ name = ck3xbl_r{reg} value = "), $"the run file sets r{reg} (no award cap in Dev mode)");
        Expect.True(body.Contains("\tset_variable = { name = ck3xbl_r197 value = 26101 }"));
    }

    private static void KnightsConfigDefaults()
    {
        var c = new BridgeConfig();
        Expect.Equal((90, "notice"), (c.Delivery.KeypadAwardKeyBudget, c.Ux.Ck3Windowed));
        Expect.Equal(90, new KeypadOptions().AwardKeyBudget);
        var o = new OrchestratorOptions();
        Expect.Equal((400.0, 900.0), (o.RevealHold.TotalMilliseconds, o.RevealDissolve.TotalMilliseconds));
        var read = ProtocolJson.Deserialize<BridgeConfig>("{ \"delivery\": { \"keypadAwardKeyBudget\": 0 }, \"ux\": { \"ck3Windowed\": \"borderless\" } }");
        Expect.Equal((0, "borderless"), (read!.Delivery.KeypadAwardKeyBudget, read.Ux.Ck3Windowed));
        Expect.Equal("title_in_engine", BannerlordCommands.TitleInEngine);
    }

    private static async Task KnightsRevealDissolves()
    {
        await using var rig = new Rig(o => { o.RevealHold = TimeSpan.FromMilliseconds(400); o.RevealDissolve = TimeSpan.FromMilliseconds(900); });
        // A first battle leaves Bannerlord muted (its sound is faded out on every return).
        await EnterBattle(rig, 17);
        Expect.False(rig.Ux.Calls.Any(c => c.StartsWith("reveal", StringComparison.Ordinal)), "inEngineTitle = true: the card is hidden as before");
        rig.Bannerlord.Result = new BattleResult { Id = "b17_test", Status = "finished", Winner = "defender" };
        rig.Status(BannerlordStates.Black, "b17_test");
        await rig.Phase(BattlePhase.Idle);

        // The module shows no title: the bridge card dissolves into the battle while the sound comes up.
        rig.Mapper.InEngineTitle = false;
        rig.Ux.RevealGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int before = rig.Ux.Calls.Count;
        await EnterNextBattle(rig, 18);
        await Rig.Until(() => rig.Ux.Calls.Skip(before).Contains("restore:4242"), "Bannerlord's sound restored");
        var during = rig.Ux.Calls.Skip(before).ToList();
        Expect.True(during.Contains("reveal:400:900") && !during.Contains("revealed"), "the volume restore starts before the dissolve ends: " + string.Join(",", during));
        Expect.False(during.Contains("hide"), "no plain hide");
        Expect.False(rig.Bannerlord.Log.Contains(BannerlordCommands.TitleInEngine), "the card was shown: the module keeps the backdrop only");
        rig.Ux.RevealGate.SetResult();
        await Rig.Until(() => rig.Ux.Calls.Contains("revealed"), "dissolve finished");

        // The card was not shown: nothing to dissolve, the overlay is hidden as before.
        rig.Bannerlord.Result = new BattleResult { Id = "b18_test", Status = "finished", Winner = "defender" };
        rig.Status(BannerlordStates.Black, "b18_test");
        await rig.Phase(BattlePhase.Idle);
        rig.Ux.TitleCardPresented = false;
        before = rig.Ux.Calls.Count;
        await EnterNextBattle(rig, 19);
        await Rig.Until(() => rig.Ux.Calls.Skip(before).Contains("restore:4242"), "Bannerlord's sound restored");
        await Rig.Until(() => rig.Ux.Calls.Skip(before).Contains("hide"), "the overlay hidden");
        Expect.False(rig.Ux.Calls.Skip(before).Any(c => c.StartsWith("reveal", StringComparison.Ordinal)), "no dissolve of a card that was not shown");
    }

    /// <summary>EnterBattle for a later battle of the same rig: 'visible' only after this battle's own 'go', so the enter activity
    /// (title card, title_in_engine) has finished before the reveal starts.</summary>
    private static async Task EnterNextBattle(Rig rig, int bid)
    {
        int gos = rig.Count("go");
        rig.Snapshot(bid);
        await rig.Phase(BattlePhase.Offered);
        rig.Choice(bid, Ck3Choice.Field);
        await Rig.Until(() => rig.Count("go") > gos, $"'go' for battle {bid}");
        rig.Status(BannerlordStates.Visible, $"b{bid}_test");
        await rig.Phase(BattlePhase.Fighting);
    }

    private static async Task KnightsTitleInEngine()
    {
        foreach (var (inEngine, presented, expected) in new[] { (false, false, true), (false, true, false), (true, false, false), (true, true, false) })
        {
            await using var rig = new Rig();
            rig.Mapper.InEngineTitle = inEngine;
            rig.Ux.TitleCardPresented = presented;
            await EnterBattle(rig, 17);
            var log = rig.Bannerlord.Log;
            string what = $"inEngineTitle {inEngine}, card presented {presented}";
            Expect.Equal(expected, log.Contains(BannerlordCommands.TitleInEngine), what);
            if (expected) Expect.True(log.IndexOf(BannerlordCommands.TitleInEngine) < log.IndexOf(BannerlordCommands.Foreground), $"{what}: before 'fg'");
        }
    }

    private static void KnightsResultCardText()
    {
        var battle = new BattleOrchestrator.Battle(new BattleSnapshot { Bid = 3, SessionKey = "t", Location = { ProvinceName = "Orangallu" } });
        const string click = "Click 'The dust settles...' in Crusader Kings III";
        var card = BattleOrchestrator.ResultCardFor(battle, new Ck3Outcome { OutcomeCode = 2, PlayerWon = true, Summary = "Our losses 1 · Enemy losses 9\nSir Odo fell\nRenown: Sir Ali (+325 prestige)" });
        Expect.Equal(("Victory at Orangallu", "Our losses 1 · Enemy losses 9"), (card.Title, card.Subtitle));
        Expect.Equal("Sir Odo fell  ·  Renown: Sir Ali (+325 prestige)  ·  " + click, card.Detail);
        Expect.Equal(click, BattleOrchestrator.ResultCardFor(battle, new Ck3Outcome { OutcomeCode = 1, Summary = "Our losses 9" }).Detail,
            "a one-line summary: the click line alone");
    }

    private static async Task KnightsAwardJournaled()
    {
        await using var rig = new Rig();
        rig.Orchestrator.OnCk3Event(new ResultTrace(3, "AW", new Dictionary<string, string>
            { ["ck3xbl_bid"] = "3.00", ["ck3xbl_aw_tag"] = "7.00", ["ck3xbl_aw_value"] = "13062.00" }));
        rig.Orchestrator.OnCk3Event(new ResultTrace(3, "AW", new Dictionary<string, string> { ["ck3xbl_bid"] = "3.00" }));
        await Rig.Until(() => rig.Journal.Lines.Count(l => l.Contains("RES|AW")) == 2, "both RES|AW journaled");
        var lines = rig.Journal.Lines.Where(l => l.Contains("RES|AW")).ToList();
        Expect.Equal("Info [battle] CK3 RES|AW for battle 3: character 7: +325 prestige, +30 glory (if acclaimed), 2 prowess roll(s)", lines[0]);
        Expect.True(lines[1].StartsWith("Info [battle]", StringComparison.Ordinal) && lines[1].EndsWith("award slot without tag or value", StringComparison.Ordinal));
    }

    // ===================================================================================================== battle clock

    private static void DecoderSteeringRecords()
    {
        // A reinforcement clash (B|BEGIN names its parent), the ST family, a J and an END of a steered combat, IN with and without
        // ck3xbl_in_maxver (PROTOCOL §14.3, §14.5).
        var log = new List<string> { Marker("B|BEGIN") };
        log.AddRange(Dump(CombatPrint, ("ck3xbl_bid", "21.00"), ("ck3xbl_parent_bid", "17.00"), ("ck3xbl_clash", "2.00")));
        foreach (var (side, tag, id) in new[] { ("att", "2.00", 60), ("def", "3.00", 61) })
        {
            log.Add(Marker("A|" + side));
            log.AddRange(Dump($"Host {Weak("Army", id)}", ("ck3xbl_bid", "21.00"), ("ck3xbl_a_tag", tag)));
        }
        log.Add(Marker("B|END"));
        log.AddRange(Dump(CombatPrint, ("ck3xbl_bid", "21.00")));
        log.Add(Marker("ST|BEGIN", "ck3xbl_battle.0002:option"));
        log.AddRange(Dump(Gunda, ("ck3xbl_bid", "21.00"), ("ck3xbl_st_mode", "1.00"), ("ck3xbl_st_winner", "2.00"), ("ck3xbl_st_att_target", "2116.00"),
            ("ck3xbl_st_def_target", "927.00"), ("ck3xbl_st_end_day", "10.00"), ("ck3xbl_st_cday", "4.00"), ("ck3xbl_st_parent", "17.00")));
        log.Add(Marker("ST|P", "ck3xbl_clock.0001"));
        log.AddRange(Dump(Gunda, ("ck3xbl_bid", "21.00"), ("ck3xbl_st_cday", "5.00"), ("ck3xbl_st_att_killed", "300.00"),
            ("ck3xbl_st_def_killed", "104.00"), ("ck3xbl_st_susp", "0.00"),
            // left on the pulse chain by an earlier ST|RESUME: not this stage's keys
            ("ck3xbl_st_reason", "2.00"), ("ck3xbl_st_mode", "2.00"), ("ck3xbl_st_capped", "1.00")));
        log.Add(Marker("ST|win", "ck3xbl_clock.0001"));
        log.AddRange(Dump(CombatPrint, ("ck3xbl_bid", "21.00"), ("ck3xbl_st_capped", "1.00")));
        log.Add(Marker("ST|DONE", "ck3xbl_clock.0002"));
        log.AddRange(Dump(Gunda, ("ck3xbl_bid", "21.00"), ("ck3xbl_st_reason", "1.00")));
        log.Add(Marker("J|def", "ck3xbl_on_join"));
        log.AddRange(Dump($"Allies {Weak("Army", 62)}", ("ck3xbl_bid", "21.00"), ("ck3xbl_a_tag", "4.00"), ("ck3xbl_j_steer", "2.00")));
        log.Add(Marker("J|att", "ck3xbl_on_join"));
        log.AddRange(Dump($"Raiders {Weak("Army", 63)}", ("ck3xbl_bid", "22.00"), ("ck3xbl_a_tag", "5.00")));
        log.Add(Marker("END|winner", "ck3xbl_on_combat_end_winner"));
        log.AddRange(Dump(CombatPrint, ("ck3xbl_bid", "22.00"), ("ck3xbl_end_wipe", "0.00"), ("ck3xbl_end_st_bid", "21.00")));
        log.Add(Marker("END|loser", "ck3xbl_on_combat_end_loser"));
        log.AddRange(Dump(CombatPrint, ("ck3xbl_bid", "22.00"), ("ck3xbl_end_wipe", "0.00")));
        log.Add(Marker("IN|BAD", "ck3xbl_key_commit:effect"));
        log.AddRange(Dump(Gunda, ("ck3xbl_bid", "21.00"), ("ck3xbl_in_count", "17.00"), ("ck3xbl_in_sum", "7414.00"), ("ck3xbl_in_maxver", "4.00")));
        log.Add(Marker("IN|OK", "ck3xbl_key_commit:effect"));
        log.AddRange(Dump(Gunda, ("ck3xbl_bid", "17.00"), ("ck3xbl_in_count", "17.00"), ("ck3xbl_in_sum", "2300.00")));

        var warnings = new List<string>();
        var events = DecodeLog(log, warnings);
        Expect.True(warnings.Count == 0, "decoder warnings: " + string.Join(" | ", warnings));

        var child = events.OfType<SnapshotCompleted>().Single().Snapshot;
        Expect.Equal((21, 17, 2), (child.Bid, child.ParentBid, child.Clash));
        var first = BuildFixtureSnapshot();
        Expect.Equal((0, 1), (first.ParentBid, first.Clash), "without the keys: a first clash");

        var st = events.OfType<SteeringTrace>().ToList();
        Expect.SequenceEqual(new[] { (21, "BEGIN"), (21, "P"), (21, "WIN"), (21, "DONE") }, st.Select(t => (t.Bid, t.Stage)), "bid from the dump, stage upper case");
        var begin = st[0];
        Expect.Equal(((int?)17, (int?)1, (int?)2, (int?)2116, (int?)927, (int?)10, (int?)4),
            (begin.ParentBid, begin.Mode, begin.Winner, begin.AttackerTarget, begin.DefenderTarget, begin.EndDay, begin.CombatDay));
        Expect.Null(begin.AttackerKilled, "a key the record does not carry is null");
        Expect.Equal(((int?)5, (int?)300, (int?)104, (int?)null), (st[1].CombatDay, st[1].AttackerKilled, st[1].DefenderKilled, st[1].ParentBid));
        Expect.Equal("0.00", st[1].Values["ck3xbl_st_susp"]);
        Expect.True(st[2].Capped && !st[1].Capped, "ck3xbl_st_capped");
        Expect.Equal(((int?)null, (int?)null), (st[1].Reason, st[1].Mode), "stage-bound keys carried along the pulse chain are not read on ST|P");
        Expect.Equal((int?)1, st[3].Reason);

        Expect.SequenceEqual(new[] { (21, SideKey.Defender, 4, 2), (22, SideKey.Attacker, 5, 0) },
            events.OfType<ArmyJoined>().Select(j => (j.Bid, j.Side, j.Army.Tag, j.SteerRole)), "ck3xbl_j_steer, 0 when absent");
        Expect.SequenceEqual(new[] { (22, 21), (22, 0) }, events.OfType<CombatEnded>().Select(e => (e.Bid, e.SteeringBid)),
            "ck3xbl_end_st_bid, 0 when absent");
        Expect.SequenceEqual(new[] { (false, 4), (true, 0) }, events.OfType<InputAck>().Select(a => (a.Ok, a.MaxVersion)),
            "ck3xbl_in_maxver, 0 when absent (a mod from before the battle clock)");
    }

    /// <summary>The outcome of the payload 4 reference vector: registers 0=4 1=7 2=2 3=8199 4=300 5=900 6=7 8=2116 9=927 10=12 11=1054
    /// 12=48 13=400 130=5 131=4 132=2 198=10, checksum 7414 by tools/gen/lib/registers.mjs payloadChecksum(r, 4).</summary>
    private static Ck3Outcome SteerOutcome() => new()
    {
        Bid = 7, ProvinceId = 8199, OutcomeCode = 2, MyLost = 300, EnemyLost = 900, MyKills = 7,
        PayloadVersion = KeypadLayout.SteerPayloadVersion, AttackerKillTarget = 2116, DefenderKillTarget = 927, PaceDays = 10,
        Armies =
        {
            new ArmyLoss
            {
                ArmyTag = 12, LevyLossPermille = 500, LevySurvivors = 1054,
                Maa = { new MaaLoss { MaaKey = "armored_footmen", MaaIndex = 48, SurvivorSharePermille = 400 } },
            },
        },
        Characters = { new CharacterFate { Tag = 5, Fate = 4, KillerTag = 2 } },
    };

    private static void KeypadV4Encoder()
    {
        var warnings = new List<string>();
        var p = KeypadPayload.Encode(SteerOutcome(), null, warnings);
        Expect.True(warnings.Count == 0, string.Join(" | ", warnings));
        var r = p.Registers;
        Expect.Equal((4, 7, 2, 8199, 4), (r[0], r[1], r[2], r[3], p.Version));
        Expect.Equal((2116, 927, 10), (r[KeypadLayout.AttackerKillTarget], r[KeypadLayout.DefenderKillTarget], r[KeypadLayout.SteerHint]));
        Expect.Equal((12, 1054, 48, 400), (r[10], r[11], r[12], r[13]), "r11 carries the absolute levy survivors");
        Expect.Equal((5, 4, 2), (r[130], r[131], r[132]));
        Expect.Equal(7414, r[KeypadLayout.Checksum], "the weighted checksum with r0 = 4 in the sum");
        Expect.Equal(KeypadPayload.ComputeChecksum(r, KeypadLayout.PayloadVersion), r[KeypadLayout.Checksum], "payload 4 changes no checksum rule");
        // The reference key sequence (the algorithm of tools/gen/keypad_payload.mjs over the registers above).
        const string reference = "F3 4 F1 7 F1 2 F1 8 1 9 9 F1 3 0 0 F1 9 0 0 F1 7 F1 8 F5 2 1 1 6 F1 9 2 7 F1 1 2 F1 1 0 5 4 F1 4 8 F1 4 0 0 F1 " +
            "1 3 0 F5 5 F1 4 F1 2 F1 1 9 8 F5 1 0 F1 7 4 1 4 F1 F2";
        var keys = p.ToKeySequence();
        Expect.Equal(71, keys.Count);
        Expect.Equal(reference, string.Join(" ", keys.Select(KeyName)));
        var sim = new KeypadSimulator();
        foreach (var k in keys) sim.Press(k);
        Expect.True(sim.Committed == true, "the payload commits");

        // The same outcome as version 3 (the old-mod fallback): the levy loss ‰ in r11, r8/r9/r198 stay 0 (PROTOCOL §10.3 example, bid 7).
        var v3 = SteerOutcome();
        v3.PayloadVersion = KeypadLayout.PayloadVersion;
        var q = KeypadPayload.Encode(v3, null).Registers;
        Expect.Equal((3, 500, 0, 0, 0, 2300), (q[0], q[11], q[8], q[9], q[198], q[199]));
        // An army listed only for its levy survivors is a no-op as version 3 (0 ‰, no pairs): left out of the fallback payload.
        var survivorsOnly = SteerOutcome();
        survivorsOnly.Armies.Insert(0, new ArmyLoss { ArmyTag = 14, LevySurvivors = 380 });
        var v4Slots = KeypadPayload.Encode(survivorsOnly, null).Registers;
        Expect.Equal((14, 380, 12), (v4Slots[10], v4Slots[11], v4Slots[10 + KeypadLayout.ArmyStride]), "version 4 names it");
        survivorsOnly.PayloadVersion = KeypadLayout.PayloadVersion;
        Expect.SequenceEqual(q, KeypadPayload.Encode(survivorsOnly, null).Registers, "version 3 skips it");

        // Limits: kill targets clamped to 999,999 and the pace day to 6..13 (0 = the mod's default), each with a warning.
        var big = SteerOutcome();
        (big.AttackerKillTarget, big.PaceDays) = (1_500_000, 20);
        var b = KeypadPayload.Encode(big, null, warnings).Registers;
        Expect.Equal((999_999, 13), (b[8], b[198]));
        Expect.Equal(2, warnings.Count, string.Join(" | ", warnings));
        var early = SteerOutcome();
        early.PaceDays = 3;
        Expect.Equal(6, KeypadPayload.Encode(early, null).Registers[198]);
        var unset = SteerOutcome();
        unset.PaceDays = 0;
        warnings.Clear();
        Expect.Equal(0, KeypadPayload.Encode(unset, null, warnings).Registers[198]);
        Expect.Equal(0, warnings.Count);

        // Awards: the same slots and the same award key cap as version 3 (there is no total key budget).
        var awarded = SteerOutcome();
        awarded.Awards.AddRange(EightAwards());
        var capped = KeypadPayload.Encode(awarded, null, warnings, awardKeyBudget: 40).Registers;
        Expect.Equal((101, 104, 0), (capped[KeypadLayout.AwardSlot(0)], capped[KeypadLayout.AwardSlot(3)], capped[KeypadLayout.AwardSlot(4)]));
        Expect.Equal(4, warnings.Count(w => w.Contains("dropped")));
    }

    /// <summary>Plays CK3's mod for the keypad: replays chords into a simulator and answers every commit with <paramref name="answer"/>.
    /// Returns the registers of each commit.</summary>
    private static List<int[]> AttachModAnswering(FakeInjector injector, Ck3EventHub hub, Func<KeypadSimulator, InputAck> answer)
    {
        var commits = new List<int[]>();
        var sim = new KeypadSimulator();
        var modifiers = new HashSet<ushort>();
        var byScan = Enum.GetValues<KeypadKey>().ToDictionary(ScanCodes.ForKeypad);
        injector.OnSend = strokes =>
        {
            foreach (var s in strokes)
            {
                if (s.Up) { modifiers.Remove(s.Code); continue; }
                if (ScanCodes.KeypadModifiers.Contains(s.Code)) { modifiers.Add(s.Code); continue; }
                if (modifiers.Count != 3 || !byScan.TryGetValue(s.Code, out var key)) continue;
                sim.Press(key);
                if (key != KeypadKey.Commit) continue;
                lock (commits) commits.Add((int[])sim.R.Clone());
                var ack = answer(sim);
                Task.Run(() => hub.Publish(ack));
            }
        };
        return commits;
    }

    private static async Task KeypadV4OldModFallback()
    {
        // A mod from before the battle clock: it accepts r0 = 2 or 3 only, logs no ck3xbl_in_maxver, and computes the weighted sum for
        // every r0 other than 2 (its commit's else branch), so a version 4 payload comes back IN|BAD with our own checksum.
        var injector = new FakeInjector();
        var hub = new Ck3EventHub();
        var commits = AttachModAnswering(injector, hub, sim =>
            new InputAck(7, sim.Committed == true && sim.R[0] is 2 or 3, sim.Idx, KeypadPayload.ComputeChecksum(sim.R, sim.R[0])));
        var journal = new MemoryJournal();
        var delivery = new KeypadDelivery(new FixedCk3(TestCk3), injector, null, hub, null, FastKeypad(), journal);
        Expect.Equal(4, delivery.MaxPayloadVersion);
        var outcome = SteerOutcome();
        Expect.Equal(DeliveryStatus.Delivered, await delivery.DeliverAsync(outcome, CancellationToken.None));
        Expect.Equal((3, 3), (outcome.PayloadVersion, delivery.MaxPayloadVersion), "lowered for this outcome and for the session");
        Expect.Equal(2, commits.Count, "one version 3 re-send");
        Expect.Equal((4, 1054), (commits[0][0], commits[0][11]));
        Expect.Equal((3, 500, 0, 0, 0), (commits[1][0], commits[1][11], commits[1][8], commits[1][9], commits[1][198]), "today's levy loss registers");
        Expect.True(journal.Lines.Any(l => l.StartsWith("Warn [keypad]", StringComparison.Ordinal) && l.Contains("does not accept payload version 4")),
            string.Join(" | ", journal.Lines));
        var next = SteerOutcome();
        Expect.Equal(DeliveryStatus.Delivered, await delivery.DeliverAsync(next, CancellationToken.None));
        Expect.Equal((3, 3, 3), (next.PayloadVersion, commits.Count, commits[2][0]), "later outcomes go as version 3 at once");

        // An IN|BAD of a mod with the battle clock (it logs ck3xbl_in_maxver: stale bid, nothing pending) is an ordinary failure, and so
        // is an old mod's IN|BAD for keys that did not arrive intact (its sum differs): the usual retries, never a downgrade.
        foreach (var (what, answer) in new (string, Func<KeypadSimulator, InputAck>)[]
                 {
                     ("new mod", sim => new InputAck(7, false, sim.Idx, KeypadPayload.ComputeChecksum(sim.R, sim.R[0])) { MaxVersion = 4 }),
                     ("lost key", sim => new InputAck(7, false, sim.Idx, KeypadPayload.ComputeChecksum(sim.R, sim.R[0]) + 1)),
                 })
        {
            var refusing = new FakeInjector();
            var refusingHub = new Ck3EventHub();
            var refused = AttachModAnswering(refusing, refusingHub, answer);
            var failing = new KeypadDelivery(new FixedCk3(TestCk3), refusing, null, refusingHub, null, FastKeypad(), new MemoryJournal());
            var o = SteerOutcome();
            Expect.Equal(DeliveryStatus.Failed, await failing.DeliverAsync(o, CancellationToken.None), what);
            Expect.Equal((4, 4), (o.PayloadVersion, failing.MaxPayloadVersion), what);
            Expect.True(refused.Count == 3 && refused.All(r => r[0] == 4), $"{what}: three attempts, all version 4");
        }
    }

    private static async Task RunFileV4OldModFallback()
    {
        using var dir = new TempDir();
        var injector = new FakeInjector();
        var hub = new Ck3EventHub();
        var delivery = new RunFileDelivery(new FixedCk3(TestCk3), injector, null, hub, null,
            new RunFileOptions { Ck3UserDir = dir.Path, KeyDelay = TimeSpan.Zero, ConsoleOpenDelay = TimeSpan.Zero }, new MemoryJournal());
        var bodies = new List<string>();
        injector.OnSend = strokes =>
        {
            if (!strokes.Any(s => s is { Kind: KeyStrokeKind.Scan, Code: ScanCodes.Enter, Up: false })) return;
            string body = File.ReadAllText(delivery.RunFilePath);
            bodies.Add(body);
            // The old mod: IN|BAD for r0 = 4 (in_sum = r199, no ck3xbl_in_maxver); otherwise the apply runs (RES|END).
            long sum = long.Parse(Regex.Match(body, @"ck3xbl_r199 value = (\d+) ").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Ck3Event answer = body.Contains("ck3xbl_r0 value = 4 }")
                ? new InputAck(7, false, 17, sum)
                : new ResultTrace(7, "END", new Dictionary<string, string>());
            Task.Run(() => hub.Publish(answer));
        };
        var outcome = SteerOutcome();
        Expect.Equal(DeliveryStatus.Delivered, await delivery.DeliverAsync(outcome, CancellationToken.None));
        Expect.Equal((3, 3), (outcome.PayloadVersion, delivery.MaxPayloadVersion));
        Expect.Equal(2, bodies.Count, "the file is run once more");
        Expect.True(bodies[0].Contains("\tset_variable = { name = ck3xbl_r8 value = 2116 }"), bodies[0]);
        Expect.True(bodies[1].Contains("\tset_variable = { name = ck3xbl_r0 value = 3 }") && bodies[1].Contains("\tset_variable = { name = ck3xbl_r11 value = 500 }") &&
                    !bodies[1].Contains("ck3xbl_r8 ") && !bodies[1].Contains("ck3xbl_r198 "), bodies[1]);
        var scans = injector.Snapshot().Where(s => s is { Kind: KeyStrokeKind.Scan, Up: false }).Select(s => s.Code).ToList();
        var bs = ScanCodes.Backspace;
        Expect.SequenceEqual(new[] { ScanCodes.Grave, bs, bs, bs, ScanCodes.Enter, ScanCodes.Grave, ScanCodes.Grave, bs, bs, bs, ScanCodes.Enter, ScanCodes.Grave }, scans,
            "the console is opened and closed for each run");

        // An old mod found through either channel keeps both on version 3: it is a fact about CK3, not about the channel.
        var keypad = new KeypadDelivery(new FixedCk3(TestCk3), new FakeInjector(), null, new Ck3EventHub(), null, FastKeypad(), new MemoryJournal());
        Expect.Equal(3, new DeliveryRouter(() => BridgeMode.Normal, "keypad", keypad, delivery, new MemoryJournal()).MaxPayloadVersion);
        Expect.Equal(4, new DeliveryRouter(() => BridgeMode.Normal, "keypad", keypad, keypad, new MemoryJournal()).MaxPayloadVersion);
    }

    /// <summary>CK3XBL_BRIDGE_ROOT names bridge/ for a binary built outside the repository; a folder without data/ is ignored.</summary>
    private static void ConfigBridgeRootOverride()
    {
        string? saved = Environment.GetEnvironmentVariable(BridgePaths.RootVariable);
        string dir = Path.Combine(Path.GetTempPath(), "ck3xbl_selftest_bridge_root");
        try
        {
            // The fallback an invalid env root gets is the walk up from the binary, so the expectation is taken without the variable
            // (the SelfTest runs with CK3XBL_BRIDGE_ROOT set when built with --artifacts-path outside the repository).
            Environment.SetEnvironmentVariable(BridgePaths.RootVariable, null);
            string normal = BridgePaths.Detect().BridgeRoot;
            Directory.CreateDirectory(Path.Combine(dir, "data"));
            Environment.SetEnvironmentVariable(BridgePaths.RootVariable, dir);
            Expect.Equal(normal, BridgePaths.Detect().BridgeRoot, "a folder without data/ck3_maa_index.json is not a bridge root");
            File.WriteAllText(Path.Combine(dir, "data", "ck3_maa_index.json"), "{}");
            var paths = BridgePaths.Detect();
            Expect.Equal((Path.GetFullPath(dir), Path.Combine(Path.GetFullPath(dir), "data")), (paths.BridgeRoot, paths.DataDir));
            Expect.Equal(Path.Combine(dir, "x.json"), BridgePaths.Detect(Path.Combine(dir, "x.json")).ConfigFile, "--config still wins");
        }
        finally
        {
            Environment.SetEnvironmentVariable(BridgePaths.RootVariable, saved);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static void ConfigBattleClock()
    {
        var c = new BridgeConfig();
        Expect.Equal((true, 12, true), (c.BattleClock.Enabled, c.BattleClock.PaceDays, c.BattleClock.ReinforcementClash));
        var o = c.ToMapperOptions("D:/data").BattleClock;
        Expect.Equal((true, 12, true, 4), (o.Enabled, o.PaceDays, o.ReinforcementClash, o.MaxPayloadVersion));
        Expect.True(ProtocolJson.Serialize(c, indented: true).Contains("\"battleClock\": {"), "written with every other key");
        var read = Expect.NotNull(ProtocolJson.Deserialize<BridgeConfig>("{ \"battleClock\": { \"enabled\": false, \"paceDays\": 30, \"reinforcementClash\": false } }"));
        var m = read.ToMapperOptions("").BattleClock;
        Expect.Equal((false, 13, false), (m.Enabled, m.PaceDays, m.ReinforcementClash), "the pace day is clamped to 6..13");
        read.BattleClock.PaceDays = 2;
        Expect.Equal(6, read.ToMapperOptions("").BattleClock.PaceDays);
        var orchestrator = new OrchestratorOptions();
        Expect.Equal((30.0, 30.0, 24.0, 5), (orchestrator.SteeringStallWarning.TotalSeconds, orchestrator.SteeringAwaitWarning.TotalMinutes,
            orchestrator.SteeringMaxAge.TotalHours, orchestrator.SteeringPulseJournalEvery));
    }

    private static void ArchiveAppendAndLoad()
    {
        using var dir = new TempDir();
        var journal = new MemoryJournal();
        var archive = new BattleArchive(dir.Path, journal);
        archive.Save("b17_aaaa", "outcome", new Ck3Outcome { Bid = 17, AttackerKillTarget = 2116 });
        archive.Append("b17_aaaa", "pulse", new Dictionary<string, int> { ["day"] = 5 });
        archive.Append("b17_aaaa", "pulse", new Dictionary<string, int> { ["day"] = 6 });
        Expect.SequenceEqual(new[] { "{\"day\":5}", "{\"day\":6}" }, File.ReadAllLines(Path.Combine(dir.Path, "b17_aaaa", "pulse.jsonl")), "one JSON line each");
        Expect.Equal(2116, archive.TryLoad<Ck3Outcome>("b17_", "outcome")?.AttackerKillTarget);
        Expect.Null(archive.TryLoad<Ck3Outcome>("b1_", "outcome"), "b1_ is not a prefix of b17_aaaa");
        Expect.True(journal.Lines.Count == 0, string.Join(" | ", journal.Lines));
    }

    /// <summary>Waits until battle <paramref name="bid"/> is the active one and offered.</summary>
    private static Task Offered(Rig rig, int bid) =>
        Rig.Until(() => rig.Orchestrator.ActiveBattleId == $"b{bid}_test" && rig.Orchestrator.Phase == BattlePhase.Offered, $"battle {bid} offered");

    /// <summary>Takes the offered battle <paramref name="bid"/> to the field, lets the defenders win and waits for its delivery.</summary>
    private static async Task FightAndDeliver(Rig rig, int bid)
    {
        int gos = rig.Count("go");
        rig.Choice(bid, Ck3Choice.Field);
        await Rig.Until(() => rig.Count("go") > gos, $"'go' for battle {bid}");
        rig.Status(BannerlordStates.Visible, $"b{bid}_test");
        await rig.Phase(BattlePhase.Fighting);
        rig.Bannerlord.Result = new BattleResult { Id = $"b{bid}_test", Status = "finished", Winner = "defender" };
        rig.Status(BannerlordStates.Black, $"b{bid}_test");
        await Rig.Until(() => rig.Delivery.Snapshot().Any(o => o.Bid == bid), $"battle {bid} delivered");
    }

    /// <summary>A rig whose mapper builds payload 4, with battle <paramref name="bid"/> delivered and steered (ST|BEGIN seen).</summary>
    private static async Task<Rig> SteeredRig(int bid, Action<OrchestratorOptions>? configure = null)
    {
        var rig = new Rig(configure);
        try
        {
            rig.Mapper.PayloadVersion = KeypadLayout.SteerPayloadVersion;
            rig.Snapshot(bid);
            await Offered(rig, bid);
            await FightAndDeliver(rig, bid);
            rig.St(bid, "BEGIN", ("ck3xbl_bid", bid), ("ck3xbl_st_mode", 1), ("ck3xbl_st_winner", 2), ("ck3xbl_st_att_target", 216),
                ("ck3xbl_st_def_target", 927), ("ck3xbl_st_end_day", 10), ("ck3xbl_st_cday", 4), ("ck3xbl_st_parent", 0));
            await rig.Steering(bid, SteeringState.Steering);
            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    private static async Task SteeringDetachAndFollow()
    {
        await using var rig = new Rig();
        rig.Mapper.PayloadVersion = KeypadLayout.SteerPayloadVersion;
        rig.Snapshot(17);
        await Offered(rig, 17);
        rig.Snapshot(18);                                         // queued behind 17
        await FightAndDeliver(rig, 17);
        await rig.Steering(17, SteeringState.Awaiting);
        await Offered(rig, 18);                                   // the Bannerlord slot is free at once
        var delivered = rig.Delivery.Snapshot().Single();
        Expect.Equal((4, 12), (delivered.PayloadVersion, delivered.PaceDays), "payload 4 with the configured pace day");
        Expect.Equal(4, rig.Mapper.Outcomes.Single().Options.BattleClock.MaxPayloadVersion);
        Expect.True(rig.Ux.LastResult!.Detail.EndsWith("The battle continues in Crusader Kings III. Click 'The dust settles...', then resume the game.",
            StringComparison.Ordinal), rig.Ux.LastResult.Detail);
        Expect.True(rig.Journal.Lines.Any(l => l.Contains("b17_test: battle continues in CK3")));

        rig.St(17, "BEGIN", ("ck3xbl_bid", 17), ("ck3xbl_st_mode", 1), ("ck3xbl_st_winner", 2), ("ck3xbl_st_att_target", 216),
            ("ck3xbl_st_def_target", 927), ("ck3xbl_st_end_day", 10), ("ck3xbl_st_cday", 4), ("ck3xbl_st_parent", 0));
        await rig.Steering(17, SteeringState.Steering);
        rig.St(17, "P", ("ck3xbl_st_cday", 5), ("ck3xbl_st_att_killed", 20), ("ck3xbl_st_def_killed", 300), ("ck3xbl_st_susp", 0));
        rig.St(17, "P", ("ck3xbl_st_cday", 6), ("ck3xbl_st_att_killed", 60), ("ck3xbl_st_def_killed", 615), ("ck3xbl_st_susp", 0));
        await Rig.Until(() => rig.SteeringOf(17).Last().CombatDay == 6, "day 6 published");
        var progress = rig.SteeringOf(17).Last();
        Expect.Equal(("Battle 17", (int?)615, (int?)927), (progress.Battle, progress.EnemyFallen, progress.EnemyTarget),
            "the player (defender) counts the attackers' dead against the defenders' target");
        Expect.Equal(2, rig.Archive.Appended("b17_test", "pulse"), "every pulse goes to pulse.jsonl");
        Expect.Equal(1, rig.Journal.Lines.Count(l => l.Contains("b17_test: combat day")), "the first pulse is journaled, then every 5th");

        rig.Orchestrator.OnCk3Event(new ArmyJoined(17, SideKey.Defender, new ArmySnapshot { Tag = 9, Name = "Allies" }, Array.Empty<CharacterSnapshot>())
            { SteerRole = 1 });
        rig.St(17, "WIN", ("ck3xbl_st_winner", 2), ("ck3xbl_st_cday", 9), ("ck3xbl_st_att_killed", 210), ("ck3xbl_st_def_killed", 927),
            ("ck3xbl_st_capped", 1));
        await rig.Steering(17, SteeringState.Ending);
        rig.Orchestrator.OnCk3Event(new CombatEnded(17, true, false, null) { SteeringBid = 17 });
        rig.Orchestrator.OnCk3Event(new CombatEnded(17, false, false, null) { SteeringBid = 17 });   // the loser's handler
        await rig.Steering(17, SteeringState.Fixing);
        rig.St(17, "FIX", ("ck3xbl_a_tag", 7), ("ck3xbl_after", 954));
        rig.St(17, "DONE", ("ck3xbl_st_reason", 1));
        await rig.Steering(17, SteeringState.Closed);
        Expect.Equal(1, rig.SteeringOf(17).Count(p => p.State == SteeringState.Closed), "END never closes a battle that saw ST|BEGIN; ST|DONE does");

        var lines = rig.Journal.Lines;
        Expect.True(lines.Any(l => l.Contains("joined battle 17 as Defender: the Bannerlord winner's side")), "the join is journaled");
        Expect.True(lines.Any(l => l.StartsWith("Warn [battle] b17_test: CK3 could not kill fast enough", StringComparison.Ordinal)), "capped is journaled");
        Expect.True(lines.Any(l => l.Contains("b17_test: CK3 ST|FIX (a_tag=7, after=954)")), string.Join(" | ", lines.TakeLast(6)));
        await Rig.Until(() => rig.Notifier.Notes.Any(n => n.StartsWith("Info: Battle 17 is over: CK3 decided it as in Bannerlord.", StringComparison.Ordinal)), "the end notice");
        Expect.True(rig.Notifier.Notes.Any(n => n.EndsWith("Enemy dead counted by CK3: 927 of 927; ours: 210 of 216.", StringComparison.Ordinal)),
            string.Join(" | ", rig.Notifier.Notes));
        var archived = Expect.NotNull(rig.Archive.Saved("b17_test", "steering") as BattleOrchestrator.SteeringArchive);
        Expect.Equal((6, 2, SteeringState.Closed, "ST|DONE reason 1"), (archived.Records.Count, archived.Pulses, archived.State, archived.ClosedBecause));
        Expect.Equal(("b18_test", BattlePhase.Offered), (rig.Orchestrator.ActiveBattleId, rig.Orchestrator.Phase), "the next battle untouched");
    }

    private static async Task SteeringClosedBeforeBegin()
    {
        await using var rig = new Rig();
        rig.Mapper.PayloadVersion = KeypadLayout.SteerPayloadVersion;
        rig.Snapshot(17);
        await Offered(rig, 17);
        await FightAndDeliver(rig, 17);
        await rig.Steering(17, SteeringState.Awaiting);
        rig.Orchestrator.OnCk3Event(new CombatEnded(17, true, false, null) { SteeringBid = 17 });   // CK3 ran on before the click
        await rig.Steering(17, SteeringState.Closed);
        Expect.True(rig.Journal.Lines.Any(l => l.Contains("b17_test: steering closed (CK3 ended the battle before the result was applied)")));

        rig.Snapshot(18);
        await Offered(rig, 18);
        await FightAndDeliver(rig, 18);
        await rig.Steering(18, SteeringState.Awaiting);
        rig.Choice(18, Ck3Choice.Abandon);                        // event 0002 option b after IN|OK, before "The dust settles..."
        await rig.Steering(18, SteeringState.Closed);
        Expect.False(rig.Bannerlord.Log.Contains("cancel b18_test"), "nothing to cancel in Bannerlord");

        rig.St(99, "P");
        rig.St(99, "P");
        await Rig.Until(() => rig.Journal.Lines.Any(l => l.Contains("CK3 ST|P for battle 99")), "unknown steering bid journaled");
        await Task.Delay(50);
        Expect.Equal(1, rig.Journal.Lines.Count(l => l.Contains("CK3 ST|P for battle 99")), "once");
    }

    private static async Task SteeringChildPlannedFromParent()
    {
        await using var rig = await SteeredRig(17);
        rig.Snapshot(19);                                         // another battle is offered meanwhile ...
        await Offered(rig, 19);
        rig.Snapshot(20);                                         // ... and one more queued behind it
        rig.St(17, "SUSPEND", ("ck3xbl_st_child", 18), ("ck3xbl_st_reason", 1));
        await rig.Steering(17, SteeringState.Suspended);
        rig.Snapshot(18, parentBid: 17, clash: 2);                // CK3 pauses on "Reinforcements arrive!"
        await Rig.Until(() => rig.Journal.Lines.Any(l => l.Contains("b18_test: reinforcement clash, first in line")), "the child queued first");
        rig.Choice(19, Ck3Choice.Auto);
        await Offered(rig, 18);                                   // before battle 20
        var (live, parent, capped) = rig.Mapper.Continuations.Single();
        var parentOutcome = rig.Delivery.Snapshot().Single(o => o.Bid == 17);
        Expect.True(live.Bid == 18 && ReferenceEquals(parent.Outcome, parentOutcome) && parent.FallenTags.SetEquals(new[] { 47 }),
            "planned through BuildContinuation with the parent's outcome and the characters it lost");
        Expect.True(ReferenceEquals(rig.Archive.Saved("b18_test", "snapshot"), capped) && ReferenceEquals(rig.Archive.Saved("b18_test", "snapshot_live"), live),
            "snapshot.json is the capped snapshot, snapshot_live.json the raw one");
        Expect.True(parent.Root is null && ReferenceEquals(rig.Archive.Saved("b18_test", "snapshot_root"), parent.Planned),
            "the parent is the first clash: no root, and its snapshot is archived as the root of the clashes after this one");

        await FightAndDeliver(rig, 18);
        Expect.Equal("Reinforcements arrive!", rig.Ux.LastTitle?.Title);
        var (planned, link, _) = rig.Mapper.Outcomes.Last();
        Expect.True(ReferenceEquals(planned, capped) && ReferenceEquals(link, parent), "the outcome is built from the capped snapshot, with the parent");
        var childOutcome = rig.Delivery.Snapshot().Last();
        Expect.Equal((18, 17, 2), (childOutcome.Bid, childOutcome.ParentBid, childOutcome.Clash));
        await rig.Steering(18, SteeringState.Awaiting);
        rig.St(18, "BEGIN", ("ck3xbl_st_parent", 17), ("ck3xbl_st_att_target", 400), ("ck3xbl_st_def_target", 1500));
        await rig.Steering(17, SteeringState.Closed);
        await rig.Steering(18, SteeringState.Steering);
        Expect.True(rig.Journal.Lines.Any(l => l.Contains("b17_test: steering closed (superseded by clash 2 (battle 18))")));
        await Offered(rig, 20);
    }

    private static async Task SteeringEndDropsOfferedChild()
    {
        await using var rig = await SteeredRig(17);
        rig.St(17, "SUSPEND", ("ck3xbl_st_child", 18), ("ck3xbl_st_reason", 1));
        rig.Snapshot(18, parentBid: 17, clash: 2);
        await Offered(rig, 18);
        // CK3 ran on without an answer and the combat ended: its END names the child's bid, ck3xbl_end_st_bid the parent.
        rig.Orchestrator.OnCk3Event(new CombatEnded(18, true, false, null) { SteeringBid = 17 });
        await rig.Steering(17, SteeringState.Fixing);
        await rig.Phase(BattlePhase.Idle);
        await Rig.Until(() => rig.Bannerlord.Log.Contains("cancel b18_test"), "the child's preload cancelled");
        Expect.False(rig.SteeringOf(17).Any(p => p.State == SteeringState.Closed), "the parent stays until ST|DONE");
    }

    private static async Task SteeringChildAutoKeepsParent()
    {
        await using var rig = await SteeredRig(17);
        rig.St(17, "SUSPEND", ("ck3xbl_st_child", 18), ("ck3xbl_st_reason", 1));
        rig.Snapshot(18, parentBid: 17, clash: 2);
        await Offered(rig, 18);
        rig.Choice(18, Ck3Choice.Auto);
        await rig.Phase(BattlePhase.Idle);
        await Rig.Until(() => rig.Bannerlord.Log.Contains("cancel b18_test"), "the child dropped as any battle");
        rig.St(17, "RESUME", ("ck3xbl_st_mode", 2), ("ck3xbl_st_reason", 2));
        await rig.Steering(17, SteeringState.Steering);
        rig.St(17, "HANDBACK", ("ck3xbl_st_reason", 2), ("ck3xbl_st_att_killed", 216), ("ck3xbl_st_def_killed", 927));
        await rig.Steering(17, SteeringState.Ending);
        rig.St(17, "DONE", ("ck3xbl_st_reason", 2));
        await rig.Steering(17, SteeringState.Closed);
        Expect.True(rig.Journal.Lines.Any(l => l.Contains("b17_test: steering resumes and hands the battle back to CK3 afterwards (the player let the reinforcement clash run its course)")));
        await Rig.Until(() => rig.Notifier.Notes.Any(n => n.StartsWith("Info: Battle 17 is over: CK3 inflicted the Bannerlord losses", StringComparison.Ordinal)), "the end notice");
    }

    private static async Task SteeringParentAfterRestart()
    {
        await using var rig = new Rig();
        // An earlier bridge run (another session key) delivered battle 17, itself clash 2 of battle 16; only its archive is left.
        var planned = new BattleSnapshot { Bid = 17, SessionKey = "old0", ParentBid = 16, Clash = 2 };
        var root = new BattleSnapshot { Bid = 16, SessionKey = "old0" };
        rig.Archive.Save("b17_old0", "snapshot", planned);
        rig.Archive.Save("b17_old0", "snapshot_root", root);
        rig.Archive.Save("b17_old0", "request", new BattleRequest { Id = "b17_old0" });
        rig.Archive.Save("b17_old0", "outcome", new Ck3Outcome
        {
            Bid = 17, AttackerKillTarget = 2116, Characters = { new CharacterFate { Tag = 5, Fate = 4 }, new CharacterFate { Tag = 6, Fate = 1 } },
        });
        rig.Snapshot(18, parentBid: 17, clash: 3);
        await Offered(rig, 18);
        var c = rig.Mapper.Continuations.Single();
        Expect.True(ReferenceEquals(c.Parent.Planned, planned) && c.Parent.Outcome.AttackerKillTarget == 2116 && c.Parent.FallenTags.SetEquals(new[] { 5 }),
            "the parent restored from its archive");
        Expect.True(ReferenceEquals(c.Parent.Root, root), "with the first clash's snapshot it archived (snapshot_root)");
        Expect.True(rig.Journal.Lines.Any(l => l.Contains("b18_test: continues battle 17, restored from the battle archive")));

        // Nothing known about the parent: planned from the live armies, with a warning.
        rig.Choice(18, Ck3Choice.Auto);
        await rig.Phase(BattlePhase.Idle);
        rig.Snapshot(21, parentBid: 20, clash: 2);
        await Offered(rig, 21);
        Expect.Equal(1, rig.Mapper.Continuations.Count, "no continuation without a parent");
        Expect.True(rig.Journal.Lines.Any(l => l.StartsWith("Warn [battle] b21_test: continues battle 20, which this bridge does not know", StringComparison.Ordinal)));

        // A newer battle 17 of another combat (another session) that was offered but never fought: no outcome, and not the combat
        // of the clash. Neither it nor a mix of its files with the older folder's is taken for the parent.
        rig.Choice(21, Ck3Choice.Auto);
        await rig.Phase(BattlePhase.Idle);
        rig.Archive.Save("b17_zzzz", "snapshot", new BattleSnapshot { Bid = 17, SessionKey = "zzzz", CombatId = 99 });
        rig.Archive.Save("b17_zzzz", "request", new BattleRequest { Id = "b17_zzzz" });
        rig.Snapshot(23, parentBid: 17, clash: 2);
        await Offered(rig, 23);
        Expect.Equal(1, rig.Mapper.Continuations.Count, "the other combat's archive is not a parent");
        Expect.True(rig.Journal.Lines.Any(l => l.StartsWith("Warn [battle] b23_test: continues battle 17, which this bridge does not know", StringComparison.Ordinal)));
    }

    private static async Task SteeringReinforcementClashOff()
    {
        await using var rig = new Rig { ReinforcementClash = false };
        rig.Snapshot(18, parentBid: 17, clash: 2);
        await Rig.Until(() => rig.Journal.Lines.Any(l => l.Contains("b18_test: reinforcement clash of battle 17 not offered")), "not offered");
        Expect.Equal(BattlePhase.Idle, rig.Orchestrator.Phase);
        rig.Choice(18, Ck3Choice.Field);
        await Rig.Until(() => rig.Notifier.Notes.Any(n => n.Contains("This battle cannot be fought in Bannerlord")), "notice");
        Expect.False(rig.Bannerlord.Log.Any(l => l.StartsWith("request:", StringComparison.Ordinal)), "nothing preloaded");
    }

    private static async Task SteeringV3FallbackNotDetached()
    {
        await using var rig = new Rig();
        rig.Mapper.PayloadVersion = KeypadLayout.SteerPayloadVersion;
        // What KeypadDelivery does for a mod from before the battle clock: the outcome is re-sent as version 3, for the whole session.
        rig.Delivery.During = o =>
        {
            o.PayloadVersion = KeypadLayout.PayloadVersion;
            rig.Delivery.MaxVersion = KeypadLayout.PayloadVersion;
            return Task.CompletedTask;
        };
        rig.Snapshot(17);
        await Offered(rig, 17);
        await FightAndDeliver(rig, 17);
        await rig.Phase(BattlePhase.Idle);
        Expect.Equal(0, rig.SteeringOf(17).Count, "a battle delivered as version 3 is applied at once and not followed");

        rig.Delivery.During = null;
        rig.Snapshot(18);
        await Offered(rig, 18);
        await FightAndDeliver(rig, 18);
        await rig.Phase(BattlePhase.Idle);
        Expect.Equal(3, rig.Mapper.Outcomes.Last().Options.BattleClock.MaxPayloadVersion, "later outcomes are built as version 3 from the start");
        Expect.Equal(3, rig.Delivery.Snapshot().Last().PayloadVersion);
        Expect.True(rig.Ux.LastResult!.Detail.EndsWith("Click 'The dust settles...' in Crusader Kings III", StringComparison.Ordinal), rig.Ux.LastResult.Detail);
        Expect.Equal(0, rig.SteeringOf(18).Count);
    }

    private static async Task SteeringDevModeOrder()
    {
        await using var rig = new Rig();
        rig.Mapper.PayloadVersion = KeypadLayout.SteerPayloadVersion;
        // The run file: the apply logs ST|BEGIN before its RES|END, so before the delivery returns.
        rig.Delivery.During = async o =>
        {
            rig.St(o.Bid, "BEGIN", ("ck3xbl_st_parent", o.ParentBid), ("ck3xbl_st_def_target", 927));
            await Task.Delay(100);
        };
        rig.Snapshot(17);
        await Offered(rig, 17);
        await FightAndDeliver(rig, 17);
        await rig.Steering(17, SteeringState.Steering);
        Expect.False(rig.SteeringOf(17).Any(p => p.State == SteeringState.Awaiting), "straight to Steering");
        Expect.False(rig.Journal.Lines.Any(l => l.Contains("which this bridge run does not follow")), "the early ST|BEGIN was not lost");

        // A reinforcement clash in the same order still supersedes its parent.
        rig.Snapshot(18, parentBid: 17, clash: 2);
        await Offered(rig, 18);
        await FightAndDeliver(rig, 18);
        await rig.Steering(18, SteeringState.Steering);
        await rig.Steering(17, SteeringState.Closed);
    }

    /// <summary>The combat was gone (or at day 13) at the click: the apply logs ST|FIX and ST|DONE reason 4 before RES|END, so before
    /// the battle is detached. The entry closes with them instead of waiting for a click that already happened.</summary>
    private static async Task SteeringDoneBeforeDetach()
    {
        await using var rig = new Rig(o => o.SteeringAwaitWarning = TimeSpan.FromMilliseconds(100));
        rig.Mapper.PayloadVersion = KeypadLayout.SteerPayloadVersion;
        rig.Delivery.During = async o =>
        {
            rig.St(o.Bid, "FIX", ("ck3xbl_st_cday", 14));
            rig.St(o.Bid, "DONE", ("ck3xbl_st_reason", 4));
            await Task.Delay(100);
        };
        rig.Snapshot(17);
        await Offered(rig, 17);
        await FightAndDeliver(rig, 17);
        await rig.Steering(17, SteeringState.Closed);
        Expect.True(rig.Journal.Lines.Any(l => l.Contains("b17_test: steering closed (ST|DONE reason 4)")), "closed by the early ST|DONE");
        var archived = Expect.NotNull(rig.Archive.Saved("b17_test", "steering") as BattleOrchestrator.SteeringArchive);
        Expect.SequenceEqual(new[] { "FIX", "DONE" }, archived.Records.Select(r => r.Stage), "steering.json keeps the early records");
        await Task.Delay(250);
        Expect.False(rig.Journal.Lines.Any(l => l.Contains("was not clicked") || l.Contains("which this bridge run does not follow")),
            "no 'not clicked' warning and no record lost");
    }

    /// <summary>The orchestrator follows the battle clock exactly as the mod logs it: the steering records, joins, the child snapshot
    /// and the ENDs of fixtures/ck3_battle_clock_game.log, decoded by the real parser, drive battles 5 to 8
    /// after each delivery. Battle 5: delivered v4 → detached → Steering → ST|WIN → END (steering bid 5) → ST|FIX ×2 → ST|DONE reason
    /// 1. Battle 6: a loser-side join suspends it, the child snapshot 7 (parent 6, clash 2) is planned from it, and the child's
    /// ST|BEGIN closes the parent as superseded. Battle 8: hand-back, and the later unsteered END changes nothing.</summary>
    private static async Task SteeringFollowsTheClockFixture()
    {
        string fixture = Path.Combine(BridgePaths.Detect().BridgeRoot, "CK3xBL.SelfTest", "fixtures", "ck3_battle_clock_game.log");
        var events = new List<Ck3Event>();
        var warnings = new List<string>();
        var decoder = new Ck3EventDecoder(events.Add, warnings.Add, "test");
        var parser = new Ck3RecordParser(decoder.Accept);
        foreach (var line in File.ReadLines(fixture, Encoding.UTF8)) parser.Feed(line);
        parser.Flush();
        decoder.Flush();
        Expect.True(warnings.Count == 0, "decoder warnings: " + string.Join(" | ", warnings));
        var snapshots = events.OfType<SnapshotCompleted>().ToDictionary(s => s.Snapshot.Bid);

        await using var rig = new Rig();
        rig.Mapper.PayloadVersion = KeypadLayout.SteerPayloadVersion;
        // CK3's records after battle `bid`'s RES|END up to the next choice: steering records, joins, ENDs and a child snapshot. The
        // rig plays the snapshots it offers, the choices and the deliveries itself.
        List<Ck3Event> After(int bid)
        {
            int start = events.FindIndex(e => e is ResultTrace { Stage: "END" } t && t.Bid == bid) + 1;
            return events.Skip(start).TakeWhile(e => e is not ChoiceMade && e is not SnapshotCompleted { Snapshot.ParentBid: 0 })
                .Where(e => e is SteeringTrace or ArmyJoined or CombatEnded or SnapshotCompleted).ToList();
        }
        async Task Deliver(int bid)
        {
            rig.Orchestrator.OnCk3Event(snapshots[bid]);
            await Offered(rig, bid);
            await FightAndDeliver(rig, bid);
            await rig.Steering(bid, SteeringState.Awaiting);
            Expect.Equal((KeypadLayout.SteerPayloadVersion, 12), (rig.Delivery.Snapshot().Last().PayloadVersion, rig.Delivery.Snapshot().Last().PaceDays),
                $"battle {bid}: payload 4 at the default pace day");
        }
        // ST|BEGIN is logged by the apply before its RES|END; with the keypad the bridge sees it after the delivery.
        SteeringTrace Begin(int bid) => events.OfType<SteeringTrace>().Single(t => t.Bid == bid && t.Stage == "BEGIN");
        string States(int bid) => string.Join(" ", rig.SteeringOf(bid).Select(p => p.State).Distinct());

        await Deliver(5);
        rig.Orchestrator.OnCk3Event(Begin(5));
        foreach (var e in After(5)) rig.Orchestrator.OnCk3Event(e);
        await rig.Steering(5, SteeringState.Closed);
        Expect.Equal("Awaiting Steering Ending Fixing Closed", States(5));
        Expect.True(rig.Journal.Lines.Any(l => l.Contains("b5_test: steering closed (ST|DONE reason 1)")), "decided");

        await Deliver(6);
        rig.Orchestrator.OnCk3Event(Begin(6));
        foreach (var e in After(6)) rig.Orchestrator.OnCk3Event(e);   // two pulses, the join, the child snapshot, ST|SUSPEND
        await rig.Steering(6, SteeringState.Suspended);
        await Offered(rig, 7);                                   // the reinforcement clash, planned from battle 6
        var (live, parent, _) = rig.Mapper.Continuations.Single();
        Expect.True(ReferenceEquals(live, snapshots[7].Snapshot) && parent.Planned.Bid == 6 && parent.Root is null,
            "the child snapshot is planned with battle 6 as its parent");
        await FightAndDeliver(rig, 7);
        await rig.Steering(7, SteeringState.Awaiting);
        var childOutcome = rig.Delivery.Snapshot().Last();
        Expect.Equal((7, 6, 2), (childOutcome.Bid, childOutcome.ParentBid, childOutcome.Clash));
        rig.Orchestrator.OnCk3Event(Begin(7));
        await rig.Steering(6, SteeringState.Closed);
        Expect.True(rig.Journal.Lines.Any(l => l.Contains("b6_test: steering closed (superseded by clash 2 (battle 7))")), "the parent is superseded");
        foreach (var e in After(7)) rig.Orchestrator.OnCk3Event(e);
        await rig.Steering(7, SteeringState.Closed);
        Expect.Equal("Awaiting Steering Ending Fixing Closed", States(7));
        Expect.Equal("Awaiting Steering Suspended Closed", States(6));

        await Deliver(8);
        rig.Orchestrator.OnCk3Event(Begin(8));
        foreach (var e in After(8)) rig.Orchestrator.OnCk3Event(e);
        await rig.Steering(8, SteeringState.Closed);
        Expect.Equal("Awaiting Steering Ending Closed", States(8));
        Expect.True(rig.Journal.Lines.Any(l => l.Contains("b8_test: steering closed (ST|DONE reason 2)")), "handed back");
        await rig.Phase(BattlePhase.Idle);
        Expect.False(rig.Journal.Lines.Any(l => l.StartsWith("Warn", StringComparison.Ordinal)), "no warning: " +
            string.Join(" | ", rig.Journal.Lines.Where(l => l.StartsWith("Warn", StringComparison.Ordinal))));
        Expect.False(rig.Journal.Lines.Any(l => l.Contains("does not follow", StringComparison.Ordinal)), "every ST record belongs to a followed battle");
    }

    private static async Task SteeringCk3RestartClears()
    {
        await using var rig = await SteeredRig(17);
        await rig.Phase(BattlePhase.Idle);
        rig.Orchestrator.OnCk3Event(new LogRestarted());          // only a steered battle in flight
        await rig.Steering(17, SteeringState.Closed);
        Expect.True(rig.Journal.Lines.Any(l => l.Contains("CK3 restarted; dropping all battles in flight")));

        // An older save loaded: steered battles of the old timeline are closed too.
        rig.Snapshot(18);
        await Offered(rig, 18);
        await FightAndDeliver(rig, 18);
        await rig.Steering(18, SteeringState.Awaiting);
        rig.Snapshot(3, session: "s2");
        await rig.Steering(18, SteeringState.Closed);
        Expect.True(rig.Journal.Lines.Any(l => l.Contains("b18_test: steering closed (superseded by a reloaded save)")));
    }

    private static async Task SteeringWatchdog()
    {
        await using (var rig = new Rig(o => { o.SteeringAwaitWarning = TimeSpan.FromMilliseconds(100); o.SteeringStallWarning = TimeSpan.FromMilliseconds(100); }))
        {
            rig.Mapper.PayloadVersion = KeypadLayout.SteerPayloadVersion;
            rig.Snapshot(17);
            await Offered(rig, 17);
            await FightAndDeliver(rig, 17);
            await Rig.Until(() => rig.Journal.Lines.Any(l => l.StartsWith("Warn [battle] b17_test: delivered", StringComparison.Ordinal) && l.Contains("was not clicked")),
                "the 'never clicked' warning");
            rig.Ck3Paused = true;
            rig.St(17, "BEGIN");
            await rig.Steering(17, SteeringState.Steering);
            await Task.Delay(250);
            Expect.False(rig.Journal.Lines.Any(l => l.Contains("no ST|P")), "a paused CK3 sends no pulse");
            rig.Ck3Paused = false;
            await Rig.Until(() => rig.Journal.Lines.Any(l => l.StartsWith("Warn [battle] b17_test: no ST|P for", StringComparison.Ordinal)), "the stall warning");
            await Task.Delay(150);
            Expect.Equal((1, 1), (rig.Journal.Lines.Count(l => l.Contains("was not clicked")), rig.Journal.Lines.Count(l => l.Contains("no ST|P"))), "each once");
        }
        await using (var rig = new Rig(o => o.SteeringMaxAge = TimeSpan.FromMilliseconds(200)))
        {
            rig.Mapper.PayloadVersion = KeypadLayout.SteerPayloadVersion;
            rig.Snapshot(17);
            await Offered(rig, 17);
            await FightAndDeliver(rig, 17);
            await rig.Steering(17, SteeringState.Closed);
            Expect.True(rig.Journal.Lines.Any(l => l.Contains("b17_test: steering closed (still open after")));
        }
    }

    // ===================================================================================================== helpers

    private static void AppendBytes(string path, byte[] bytes)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.Write(bytes);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ck3xbl-selftest-" + Guid.NewGuid().ToString("N")[..8]);

        public TempDir() => Directory.CreateDirectory(Path);

        public string File(string name)
        {
            var full = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            return full;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
