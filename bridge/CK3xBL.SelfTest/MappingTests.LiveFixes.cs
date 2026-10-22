// Self tests of the fixes after the live full-pipeline test of build 2 (live test of 2026-10-04; PROTOCOL §15):
//   item 1  the player stands in for a host without a commander (his own C record, ck3xbl_c_absent) and is never harmed by the outcome;
//   item 2  the reinforcement-clash subtitle never reads "X joins the Host of X", and the clash-2 result line shows this clash's losses
//           with the whole battle's totals.
// The battle is the live b2_ea00/b3_ea00 pair, hand-built: Duke Robert's commander-less remnant at Apulia against Atuy Sipet's host.
// Run: dotnet run --project CK3xBL.SelfTest -- mapping.livefix
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Model;
using CK3xBL.Mapping.Outcome;
using CK3xBL.Mapping.Planning;
using CK3xBL.Protocol;

namespace CK3xBL.SelfTest;

internal static partial class MappingTests
{
    private static void RegisterLiveFixes(TestRegistry r)
    {
        r.Add("mapping.livefix.stand_in_player_when_no_army_has_a_commander", LiveStandInPlayer);
        r.Add("mapping.livefix.stand_in_player_is_not_the_ck3_commander_of_the_knights_pass", LiveStandInKnightsPass);
        r.Add("mapping.livefix.absent_player_is_never_harmed", LiveAbsentPlayerUnharmed);
        r.Add("mapping.livefix.reinforcement_subtitle_wording", LiveJoinsWording);
        r.Add("mapping.livefix.clash_two_of_the_live_battle", LiveClashTwo);
    }

    private const long Robert = 31254, Atuy = 30961;
    private const int RobertTag = 2;

    /// <summary>b2_ea00: the enemy's 1,336 survivors attack Robert's leftover army of 250 that has no commander. Robert is logged on his
    /// side as <paramref name="playerRole"/> (the live mod: PRIMARY with c_is_player; PROTOCOL §15: PLAYER) and is not with the army.</summary>
    private static BattleSnapshot CommanderlessHost(string playerRole = "PRIMARY", bool? absent = true)
    {
        var robert = Scenarios.Ref(Robert, "Robert de Hauteville", "d_apulia");
        var atuy = Scenarios.Ref(Atuy, "Atuy Sipet", "c_chupka");
        var norman = Scenarios.Cul("norman", "heritage_frankish", "western_unit_gfx", Scenarios.Early);
        var turkish = Scenarios.Cul("turkish", "heritage_turkic", "mongol_unit_gfx", Scenarios.Early);
        var me = Scenarios.Person(robert, RobertTag, playerRole, norman, 14, 23, 51, 3, 0, player: true);
        me.Absent = absent;
        var def = Scenarios.Side(SideKey.Defender, false, robert,
            new[] { Scenarios.Army(4, "Norman Highwaymen", robert, null, norman, 125, ("bowmen", 75, 75), ("armored_footmen", 50, 50)) },
            new[] { me });
        var att = Scenarios.Side(SideKey.Attacker, false, atuy,
            new[] { Scenarios.Army(3, "Kotankorkur Atuy's Host", atuy, atuy, turkish, 836, ("horse_archers", 300, 300), ("light_horsemen", 200, 200)) },
            new[] { Scenarios.Person(atuy, 1, "CMD+PRIMARY", turkish, 10, 12, 52, 2, 3) });
        return Scenarios.Battle(2, "Battle of Apulia", (1066, 10, 2), 2619, "Apulia", "c_apulia", "plains", "none", "castle_holding", 3, false, false,
            "Debug Artifact War", Robert, att, def, ("region", "world_europe_west"));
    }

    private static void LiveStandInPlayer()
    {
        foreach (var role in new[] { "PRIMARY", "PLAYER" })
        {
            var battle = CommanderlessHost(role);
            var req = Mapper.BuildRequest(battle, new MapperOptions());
            var h0 = req.Defender.Heroes.Single(h => h.Key == "h0");
            Expect.True(h0.IsPlayer && h0.Role == "player" && h0.Ck3Id == Robert && h0.Tag == RobertTag, $"{role}: Robert is the avatar");
            Expect.Equal("defender", req.PlayerSide);
            Expect.True(req.PlayerIsGeneral, $"{role}: he leads");
            Expect.Equal("h0", req.Defender.General);
            Expect.Equal(1, req.Defender.Heroes.Count(h => h.IsPlayer));
            // Formation, kit and skills from his CK3 facts, as for any hero (prowess 14, martial 23, duke).
            Expect.True(h0.Formation.Length > 0 && h0.Equipment.Roster.Length > 0, h0.Equipment.Roster);
            Expect.Equal(Math.Clamp(60 + 8 * 14, 60, 330), h0.Skills["OneHanded"]);
            Expect.Equal(230, h0.Skills["Tactics"]);
            Expect.Equal("Robert de Hauteville", h0.Name);
            Expect.True(req.Presentation.Subtitle.StartsWith("Robert de Hauteville against Atuy Sipet · ", StringComparison.Ordinal), req.Presentation.Subtitle);
        }

        // A knight rides with the commander-less host: Robert still stands in and leads; the knight is a hero under him.
        var withKnight = CommanderlessHost();
        withKnight.Defender.Characters.Add(Scenarios.Person(Scenarios.Ref(31300, "Serlo"), 7, "KNIGHT", withKnight.Defender.Characters[0].Culture, 15, 6, 30, 0, 4));
        var kreq = Mapper.BuildRequest(withKnight, new MapperOptions());
        Expect.Equal(Robert, kreq.Defender.Heroes.Single(h => h.IsPlayer).Ck3Id);
        Expect.Equal("h0", kreq.Defender.General);
        Expect.True(kreq.Defender.Heroes.Any(h => h.Ck3Id == 31300 && h.Role == "knight" && !h.IsPlayer));

        // An army commander of his side still stands in first (today's rule); Robert stays home.
        var led = CommanderlessHost("PLAYER");
        var tancred = Scenarios.Ref(31400, "Tancred");
        led.Defender.Armies[0].Commander = tancred;
        led.Defender.Characters.Add(Scenarios.Person(tancred, 8, "CMD", led.Defender.Characters[0].Culture, 11, 12, 40, 1, 4));
        var lreq = Mapper.BuildRequest(led, new MapperOptions());
        var standIn = lreq.Defender.Heroes.Single(h => h.IsPlayer);
        Expect.True(standIn.Ck3Id == 31400 && standIn.Role == "commander");
        Expect.False(lreq.Defender.Heroes.Any(h => h.Ck3Id == Robert), "with a commander to stand in, the absent player is not on the field");

        // No record of the player at all (older mod): today's behaviour, a stock general and no hero on his side.
        var old = CommanderlessHost();
        old.Defender.Characters.Clear();
        var oreq = Mapper.BuildRequest(old, new MapperOptions());
        Expect.Equal(0, oreq.Defender.Heroes.Count);
        Expect.True(oreq.PlayerIsGeneral && oreq.Defender.General.StartsWith("commander_", StringComparison.Ordinal), oreq.Defender.General);
    }

    // Robert leads in Bannerlord only: his commander traits must not shape the troops through the knights pass (CK3's side has no
    // commander). aggressive_attacker L1 would raise the enemy's troop death chance x1.2 (MappingTests.Knights KnightsCasualties).
    private static void LiveStandInKnightsPass()
    {
        static BattleSnapshot Aggressive(bool commands)
        {
            var b = CommanderlessHost();
            b.KnightFactsLogged = true;
            var me = b.Defender.Characters[0];
            me.CommanderTraits.Add("aggressive_attacker");
            me.TraitLevels["aggressive_attacker"] = 1;
            if (commands)
            {
                me.IsCommander = true;
                me.ArmyTag = 4;
                b.Defender.Armies[0].Commander = me.Ref;
            }
            return b;
        }
        double DeathMult(BattleSnapshot b) => Mapper.BuildRequest(b, new MapperOptions()).Attacker.Buffs?.TroopDeathChanceMult ?? 1;
        Expect.Near(1.2, DeathMult(Aggressive(commands: true)), 1e-6, "control: commanding in CK3, his trait counts");
        Expect.Near(1.0, DeathMult(Aggressive(commands: false)), 1e-6, "standing in: no CK3 commander");
        var standIn = Aggressive(commands: false);
        var req = Mapper.BuildRequest(standIn, new MapperOptions());
        Expect.True(req.Defender.Heroes.Single(h => h.IsPlayer).Ck3Id == Robert && req.Defender.General == "h0");
    }

    private static void LiveAbsentPlayerUnharmed()
    {
        var hardcore = new MapperOptions { PlayerDeathIsReal = true };
        foreach (bool? absent in new bool?[] { true, null })
        {
            for (int bid = 1; bid <= 150; bid++)
            {
                var battle = CommanderlessHost(absent is null ? "PRIMARY" : "PLAYER", absent);
                battle.Bid = bid;
                battle.KnightFactsLogged = bid % 2 == 0;
                var req = Mapper.BuildRequest(battle, hardcore);
                var result = Synthetic(req, "attacker");     // the avatar falls after 5 kills
                var o = Mapper.BuildOutcome(battle, req, result, hardcore);
                Expect.False(o.Characters.Any(c => c.Tag == RobertTag || c.Ck3Id == Robert), $"absent={absent} bid {bid}: never harmed");
                Expect.False(o.Awards.Any(a => a.Tag == RobertTag), "no awards for a battle he was not at");
                Expect.Equal(0, o.PrestigeBonus);
                Expect.Equal(5, o.MyKills);
                Expect.True(o.Summary.Contains("You struck down 5 foes yourself.", StringComparison.Ordinal), o.Summary);
                // He wins and strikes down the enemy general: that fate applies, his own fall still does not.
                var won = Mapper.BuildOutcome(battle, req, Synthetic(req, "defender"), hardcore);
                Expect.True(won.Characters.Any(c => c.Tag == 1) && !won.Characters.Any(c => c.Tag == RobertTag), "the enemy general he struck down gets a fate");
                // ... but CK3 never names the absent player as killer: not as striker, not as his side's general (h0).
                Expect.Equal(0, won.Characters.Single(c => c.Tag == 1).KillerTag);
            }
        }
        // Control: present (ck3xbl_c_absent = 0), his blow is credited to him.
        {
            var battle = CommanderlessHost("PLAYER", absent: false);
            var req = Mapper.BuildRequest(battle, hardcore);
            var won = Mapper.BuildOutcome(battle, req, Synthetic(req, "defender"), hardcore);
            Expect.Equal(RobertTag, won.Characters.Single(c => c.Tag == 1).KillerTag);
        }

        // The mod says he is in the combat (ck3xbl_c_absent = 0): the CK3 player's own rules, wounds yes, death only in hardcore.
        int harmed = 0, killed = 0;
        for (int bid = 1; bid <= 150; bid++)
        {
            var battle = CommanderlessHost("PLAYER", absent: false);
            battle.Bid = bid;
            var req = Mapper.BuildRequest(battle, new MapperOptions());
            var fate = Mapper.BuildOutcome(battle, req, Synthetic(req, "attacker"), new MapperOptions()).Characters.FirstOrDefault(c => c.Tag == RobertTag);
            if (fate is not null) harmed++;
            if (fate?.Fate == Fates.Killed) killed++;
        }
        Expect.True(harmed > 0, "present: his fall has CK3 consequences");
        Expect.Equal(0, killed);
    }

    private static void LiveJoinsWording()
    {
        var robert = Scenarios.Ref(Robert, "Robert de Hauteville", "d_apulia");
        var roger = Scenarios.Ref(31500, "Roger de Hauteville", "c_calabria");
        var tancred = Scenarios.Ref(31400, "Tancred");
        var norman = Scenarios.Cul("norman", "heritage_frankish", "western_unit_gfx", Scenarios.Early);
        var fought = new HashSet<int> { 4 };
        SideSnapshot Host(ArmySnapshot joiner, CharacterRef? remnantCommander = null) => Scenarios.Side(SideKey.Defender, true, robert,
            new[] { Scenarios.Army(4, "Norman Highwaymen", robert, remnantCommander, norman, 3), joiner }, Array.Empty<CharacterSnapshot>());

        // The live case: Robert's new host joins the host named after him.
        var own = Scenarios.Army(6, "Duke Robert's Host", robert, null, norman, 900);
        Expect.Equal("Fresh troops join the Host of Robert de Hauteville", PresentationWriter.Joins(Host(own), own, fought));
        // Led by Robert himself, owned by his brother: still his own host.
        var ledByRobert = Scenarios.Army(6, "Host of Calabria", roger, robert, norman, 900);
        Expect.Equal("Fresh troops join the Host of Robert de Hauteville", PresentationWriter.Joins(Host(ledByRobert), ledByRobert, fought));
        // The owner already commands an army of the host.
        var rogers = Scenarios.Army(6, "Host of Calabria", roger, null, norman, 900);
        Expect.Equal("Fresh troops join the Host of Robert de Hauteville", PresentationWriter.Joins(Host(rogers, roger), rogers, fought));
        // A vassal's army with its own commander, and without one.
        var commanded = Scenarios.Army(6, "Host of Calabria", roger, tancred, norman, 900);
        Expect.Equal("Tancred brings fresh troops to the Host of Robert de Hauteville", PresentationWriter.Joins(Host(commanded), commanded, fought));
        Expect.Equal("Roger de Hauteville joins the Host of Robert de Hauteville", PresentationWriter.Joins(Host(rogers), rogers, fought));
        // Nobody named at all: the army itself.
        var nameless = Scenarios.Army(6, "Raiders of Dublin", new CharacterRef(), null, norman, 900);
        Expect.Equal("The Raiders of Dublin joins the Host of Robert de Hauteville", PresentationWriter.Joins(Host(nameless), nameless, fought));
    }

    /// <summary>b3_ea00: after clash 1 (lost), Robert's 900 fresh men reach Apulia. The whole flow of the live test: the subtitle, the
    /// stand-in avatar in clash 2, and the result line of clash 2. Then the enemy-side variant: Atuy reinforces his own host.</summary>
    private static void LiveClashTwo()
    {
        var options = new MapperOptions();
        var first = CommanderlessHost("PLAYER");
        var firstReq = Mapper.BuildRequest(first, options);
        var firstOutcome = Mapper.BuildOutcome(first, firstReq, Synthetic(firstReq, "attacker"), options);
        var parent = new ParentLink { Planned = first, Request = firstReq, Outcome = firstOutcome };

        var live = CommanderlessHost("PLAYER");
        (live.Bid, live.ParentBid, live.Clash) = (3, 2, 2);
        var robert = live.Defender.Characters[0].Ref;
        live.Defender.Armies.Add(Scenarios.Army(6, "Duke Robert's Host", robert, null, live.Defender.Armies[0].OwnerCulture, 650, ("armored_horsemen", 250, 250)));
        live.Defender.Size = live.Defender.Armies.Sum(a => a.Size);
        var plan = Mapper.BuildContinuation(live, parent, options);
        string when = $"{Text.SeasonName(firstReq.Season)} 1066";
        Expect.Equal($"Fresh troops join the Host of Robert de Hauteville · {when}", plan.Request.Presentation.Subtitle);
        Expect.Equal(PresentationWriter.ReinforcementsTitle, plan.Request.Presentation.Title);
        var h0 = plan.Request.Defender.Heroes.Single(h => h.IsPlayer);
        Expect.True(h0.Ck3Id == Robert && h0.Role == "player" && plan.Request.Defender.General == h0.Key, "Robert stands in again");

        var o = Mapper.BuildOutcome(plan.Capped, plan.Request, Synthetic(plan.Request, "attacker"), options, parent);
        Expect.Equal(2, o.Clash);
        int mineNow = o.MyLost - firstOutcome.MyLost, theirsNow = o.EnemyLost - firstOutcome.EnemyLost;
        Expect.True(mineNow > 0 && theirsNow > 0);
        string expected = $"Defeat at Apulia: {Text.Number(mineNow)} of your men fell, and {Text.Number(theirsNow)} of the enemy. You struck down 5 foes yourself. "
                          + $"Over both clashes: {Text.Number(o.MyLost)} of your men and {Text.Number(o.EnemyLost)} of the enemy.";
        Expect.Equal(expected, o.Summary.Split('\n')[0]);
        Expect.False(o.Characters.Any(c => c.Tag == RobertTag), "absent in clash 2 too");
        // The clash-3 wording.
        Expect.True(OutcomeBuilder.SummaryLine(2, new Ck3Outcome { MyLost = 30, EnemyLost = 20, Clash = 3 }, "Apulia", new Ck3Outcome { MyLost = 10, EnemyLost = 5 })
            .EndsWith("Over all 3 clashes: 30 of your men and 20 of the enemy.", StringComparison.Ordinal));
        // A first clash keeps its single line.
        Expect.False(firstOutcome.Summary.Contains("Over ", StringComparison.Ordinal), firstOutcome.Summary);

        // Enemy-side variant: only Atuy's side is reinforced, by Atuy's own new army.
        var enemyLive = CommanderlessHost("PLAYER");
        (enemyLive.Bid, enemyLive.ParentBid, enemyLive.Clash) = (3, 2, 2);
        var atuy = enemyLive.Attacker.Characters[0].Ref;
        enemyLive.Attacker.Armies.Add(Scenarios.Army(7, "Atuy's Riders", atuy, null, enemyLive.Attacker.Armies[0].OwnerCulture, 400));
        enemyLive.Attacker.Size = enemyLive.Attacker.Armies.Sum(a => a.Size);
        Expect.Equal($"Fresh troops join the Host of Atuy Sipet · {when}", Mapper.BuildContinuation(enemyLive, parent, options).Request.Presentation.Subtitle);
        // ... and by a vassal's commanded army.
        var bek = Scenarios.Ref(30970, "Bek Sipet");
        enemyLive.Attacker.Armies[^1].Owner = bek;
        enemyLive.Attacker.Armies[^1].Commander = Scenarios.Ref(30971, "Tosh");
        enemyLive.Attacker.Characters.Add(Scenarios.Person(enemyLive.Attacker.Armies[^1].Commander!, 9, "CMD", enemyLive.Attacker.Characters[0].Culture, 9, 9, 30, 0, 7));
        Expect.Equal($"Tosh brings fresh troops to the Host of Atuy Sipet · {when}", Mapper.BuildContinuation(enemyLive, parent, options).Request.Presentation.Subtitle);
    }
}
