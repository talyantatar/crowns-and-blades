// Title-card and Bannerlord UI strings (PROTOCOL §3.2 presentation): CK3 wording ("Battle of X"),
// leaders and season, and the battlefield in one line ("Hills near Orangallu"). A relief battle is fought on a field before the
// holding (PROTOCOL §0), so the siege shows only in the words: the ground actually used ("Fields before Angers" for an open
// scene pool, else "Hills before X", PROTOCOL §13.7), and an objective by the player's siege role (§13.7). The bridge's own
// title card dissolves into the battle, so the module shows none (inEngineTitle = false, PROTOCOL §13.6). A reinforcement clash
// keeps the earlier clash's ground and staging under the title "Reinforcements arrive!" (PROTOCOL §14.6).
// Every sentence comes from Words in the language CK3 runs in (PROTOCOL §26); English when no words are given.
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Model;
using CK3xBL.Core.Texts;
using CK3xBL.Mapping.Resolution;
using CK3xBL.Protocol;

namespace CK3xBL.Mapping.Planning;

public static class PresentationWriter
{
    public const string ReinforcementsTitle = "Reinforcements arrive!";

    public static string PlaceName(BattleSnapshot battle, Words? w = null)
    {
        w ??= Words.English;
        string p = Text.SanitizeName(battle.Location.ProvinceName);
        if (p.Length > 0) return p;
        string combat = Text.SanitizeName(battle.CombatName);
        const string prefix = "Battle of ";
        return combat.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? combat[prefix.Length..] : combat.Length > 0 ? combat : w.DefaultPlace;
    }

    private static string CountyName(BattleSnapshot battle, BattlefieldChoice field, Words w)
    {
        string n = Text.SanitizeName(battle.Location.CountyName);
        if (n.Length == 0) n = Text.TitleKeyToName(battle.Location.CountyKey ?? field.County);
        return n.Length > 0 ? n : PlaceName(battle, w);
    }

    /// <summary>The CK3 combat name, with an article when it is CK3's English "Battle of ...": "Battle of Orangallu" -> "The Battle of
    /// Orangallu". A name in another language (CK3 in Russian: "Битва в провинции Маньяпура") stays as CK3 wrote it.</summary>
    public static string Title(BattleSnapshot battle, Words? w = null)
    {
        w ??= Words.English;
        string combat = Text.SanitizeName(battle.CombatName);
        if (combat.Length == 0) return w.BattleOf(PlaceName(battle, w));
        return combat.StartsWith("Battle ", StringComparison.OrdinalIgnoreCase) ? "The " + combat : combat;
    }

    public static string Detail(BattleSnapshot battle, BattlefieldChoice field, Words? w = null)
    {
        w ??= Words.English;
        if (field.Kind == "village") return w.Village(CountyName(battle, field, w));
        if (!field.Relief) return w.Near(Ground(field, w), CountyName(battle, field, w));
        bool fields = field.ListSource == "terrain" && SceneVegetation.IsOpenPool(field.PoolTerrain);
        return w.Before(fields ? w.Fields : Ground(field, w), PlaceName(battle, w));
    }

    /// <summary>The ground of the field scene: the situational list it led with, else the terrain of its scene pool.</summary>
    private static string Ground(BattlefieldChoice field, Words w) => field.ListSource switch
    {
        "coastal_near" or "coastal_far" or "strait->coastal" => w.Coast,
        "riverside" or "river_crossing" => w.Riverbank,
        "major_river_crossing" => w.RiverCrossing,
        "lakeside_near" => w.Lakeshore,
        _ => w.Terrain(field.PoolTerrain),
    };

    /// <summary>The largest army on a side, as the object of a sentence ("the Raiders of Hepung").</summary>
    public static string ArmyObject(SideSnapshot side, Words? w = null)
    {
        w ??= Words.English;
        string name = Text.SanitizeName(side.Armies.OrderByDescending(a => a.Size).ThenBy(a => a.Tag).FirstOrDefault()?.Name);
        return ArmyNameObject(name, w);
    }

    private static string ArmyNameObject(string? armyName, Words w)
    {
        if (w.Language == Words.English.Language) return Text.ArmyAsObject(armyName);
        string n = Text.SanitizeName(armyName);
        return n.Length == 0 ? w.EnemyHost : w.ArmyObject(n);
    }

    public static string SideName(SideSnapshot side, Words? w = null)
    {
        w ??= Words.English;
        string leader = Text.SanitizeName(side.Primary?.Name);
        if (leader.Length == 0)
            leader = Text.SanitizeName(side.Armies.OrderByDescending(a => a.Size).Select(a => a.Owner?.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)));
        if (leader.Length > 0) return w.HostOf(leader);
        string army = Text.SanitizeName(side.Armies.OrderByDescending(a => a.Size).FirstOrDefault()?.Name);
        return army.Length > 0 ? army : side.Key == SideKey.Attacker ? w.AttackingHost : w.DefendingHost;
    }

    public static Presentation Build(BattleSnapshot battle, BattlefieldChoice field, HeroAssignment heroes, string playerSideName, MapperOptions options)
    {
        var w = Words.For(options.Language);
        var enemySide = battle.Side(heroes.PlayerSide == SideKey.Attacker ? SideKey.Defender : SideKey.Attacker);
        string own = heroes.Avatar?.Spec.Name ?? heroes.General(heroes.PlayerSide)?.Spec.Name ?? playerSideName;
        string enemy = heroes.General(enemySide.Key)?.Spec.Name ?? ArmyObject(enemySide, w);
        string when = w.When(w.Season(field.Season), battle.Year);
        string objective = SiegeObjective(battle, field, heroes.PlayerSide, w) ?? w.Rout(ArmyObject(enemySide, w));
        return new Presentation
        {
            Preload = true,
            Title = Title(battle, w),
            Subtitle = $"{w.Against(own, enemy)} · {when}",
            Detail = Detail(battle, field, w),
            Objective = Text.Capitalize(objective),
            ReturnCountdownSeconds = options.ReturnCountdownSeconds,
            Deployment = options.Deployment,
            InEngineTitle = false,
            Language = w.Language,
        };
    }

    /// <summary>
    /// The presentation of a reinforcement clash (PROTOCOL §14.6): "Reinforcements arrive!", who joined which host, and the earlier
    /// clash's ground, staging and coats of arms. The objective is the freshly planned one, since the enemy host may now be the
    /// newcomer. The newcomer is the largest army the earlier clash did not have, preferring the earlier loser's side (its
    /// reinforcement opened the clash).
    /// </summary>
    /// <param name="capped">The clash's snapshot as planned (ContinuationSnapshot.Cap).</param>
    /// <param name="parent">The earlier clash.</param>
    /// <param name="planned">What <see cref="Build"/> wrote for this clash.</param>
    public static Presentation Continuation(BattleSnapshot capped, ParentLink parent, Presentation planned)
    {
        var w = Words.For(planned.Language);
        var earlier = parent.Request.Presentation ?? new Presentation();
        var fought = parent.Planned.Sides.SelectMany(s => s.Armies).Select(a => a.Tag).ToHashSet();
        SideKey? loser = parent.Outcome.OutcomeCode switch { 1 => SideKey.Defender, 2 => SideKey.Attacker, _ => null };
        var joiner = capped.Sides
            .SelectMany(s => s.Armies.Where(a => !fought.Contains(a.Tag)).Select(a => (Side: s, Army: a)))
            .OrderByDescending(x => x.Side.Key == loser).ThenByDescending(x => x.Army.Size).ThenBy(x => x.Army.Tag)
            .FirstOrDefault();
        string when = w.When(w.Season(parent.Request.Season), capped.Year);
        return new Presentation
        {
            Preload = planned.Preload,
            Title = w.Reinforcements,
            Subtitle = joiner.Army is null ? planned.Subtitle : $"{Joins(joiner.Side, joiner.Army, fought, w)} · {when}",
            Detail = earlier.Detail,
            Objective = planned.Objective,
            ReturnCountdownSeconds = planned.ReturnCountdownSeconds,
            Deployment = earlier.Deployment,
            AttackerCoaPng = earlier.AttackerCoaPng,
            DefenderCoaPng = earlier.DefenderCoaPng,
            InEngineTitle = earlier.InEngineTitle,
            Language = planned.Language,
        };
    }

    /// <summary>
    /// Who joined which host, in words (live test: never "X joins the Host of X"):
    /// "Fresh troops join the Host of X" when the newcomer's owner or commander is the host's leader (the name in
    /// <see cref="SideName"/>) or commands one of the armies already there; "&lt;commander&gt; brings fresh troops to the Host of X" when
    /// the new army has a commander; else "&lt;owner&gt; joins the Host of X" (or the army itself, "The Raiders of Hepung joins ...").
    /// </summary>
    /// <param name="side">The side the newcomer joined.</param>
    /// <param name="joiner">The new army.</param>
    /// <param name="fought">Tags of the armies of the earlier clash.</param>
    public static string Joins(SideSnapshot side, ArmySnapshot joiner, IReadOnlySet<int> fought, Words? w = null)
    {
        w ??= Words.English;
        string host = SideName(side, w);
        var leaders = new HashSet<long>();
        if (HostLeader(side) is { Ck3Id: not 0 } leader) leaders.Add(leader.Ck3Id);
        foreach (var other in side.Armies.Where(a => a.Tag != joiner.Tag && fought.Contains(a.Tag)))
            if (other.Commander is { Ck3Id: not 0 } cmd) leaders.Add(cmd.Ck3Id);
        bool Leads(CharacterRef? r) => r is { Ck3Id: not 0 } && leaders.Contains(r.Ck3Id);
        if (Leads(joiner.Owner) || Leads(joiner.Commander)) return w.FreshTroopsJoin(host);
        string commander = Text.SanitizeName(joiner.Commander?.Name);
        if (commander.Length > 0) return w.BringsFreshTroops(commander, host);
        string owner = Text.SanitizeName(joiner.Owner?.Name);
        if (owner.Length > 0) return w.Joins(owner, host);
        string army = Text.SanitizeName(joiner.Name);
        return army.Length > 0 ? w.Joins(Text.Capitalize(ArmyNameObject(joiner.Name, w)), host) : w.FreshTroopsJoin(host);
    }

    /// <summary>The character a side's host is named after (<see cref="SideName"/>): its primary participant, else the owner of its
    /// largest army that has one.</summary>
    private static CharacterRef? HostLeader(SideSnapshot side) =>
        Text.SanitizeName(side.Primary?.Name).Length > 0 ? side.Primary
        : side.Armies.OrderByDescending(a => a.Size).Select(a => a.Owner).FirstOrDefault(o => !string.IsNullOrWhiteSpace(o?.Name));

    /// <summary>
    /// The objective at a besieged holding by the player's siege role (PROTOCOL §13.7): a side is the besieger when one of its armies
    /// logged AX|besieging|yes. Null when the holding is not besieged or no army logged the role (an older mod): then the plain rout.
    /// </summary>
    private static string? SiegeObjective(BattleSnapshot battle, BattlefieldChoice field, SideKey playerSide, Words w)
    {
        if (!field.Relief) return null;
        static bool Besieger(SideSnapshot side) => side.Armies.Any(a => a.Besieging == true);
        string place = PlaceName(battle, w);
        if (Besieger(battle.Side(playerSide))) return w.HoldSiegeLines(place);
        if (Besieger(battle.Side(playerSide == SideKey.Attacker ? SideKey.Defender : SideKey.Attacker))) return w.BreakSiege(place);
        return null;
    }
}
