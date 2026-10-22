// What the bridge learns from CK3's log, one level above raw records (PROTOCOL §1.2-1.3).
using CK3xBL.Core.Model;

namespace CK3xBL.Core.Logs;

public abstract record Ck3Event
{
    /// <summary>Wall-clock time of day from the log stamp.</summary>
    public TimeSpan LogTime { get; init; }
}

/// <summary>B|BEGIN ... B|END for a player-involved battle; the snapshot is complete and immutable from here on.</summary>
public sealed record SnapshotCompleted(BattleSnapshot Snapshot) : Ck3Event;

public enum Ck3Choice
{
    /// <summary>Fight it in Bannerlord (CHOICE|FIELD, also FIELD_AS_COMMANDER).</summary>
    Field,
    /// <summary>CK3 autoresolve (CHOICE|AUTO, AUTO_*).</summary>
    Auto,
    /// <summary>The player gave up waiting in event 0002 (CHOICE|ABANDON); CK3 resolves natively.</summary>
    Abandon,
}

public sealed record ChoiceMade(int Bid, Ck3Choice Choice, string Kind) : Ck3Event;

/// <summary>IN|OK / IN|BAD: the keypad (or run-file) payload was committed; Ok = checksum and bid verified by the mod.</summary>
public sealed record InputAck(int Bid, bool Ok, int Count, long Sum) : Ck3Event
{
    /// <summary>ck3xbl_in_maxver: the highest payload version the mod accepts (4 with the battle clock, PROTOCOL §14.3); 0 = not
    /// logged (a mod from before the battle clock).</summary>
    public int MaxVersion { get; init; }
}

/// <summary>RES|BEGIN, RES|LIVE, RES|GONE, RES|A, RES|END: trace of the apply effect. Stage END means the result is applied.</summary>
public sealed record ResultTrace(int Bid, string Stage, IReadOnlyDictionary<string, string> Values) : Ck3Event;

/// <summary>END|winner / END|loser from on_combat_end_* for a tagged combat (Bid 0 = not one of ours).</summary>
public sealed record CombatEnded(int Bid, bool WinnerSide, bool Wipe, CharacterRef? Primary) : Ck3Event
{
    /// <summary>ck3xbl_end_st_bid: the steering bid of a steered combat (PROTOCOL §14.5), which may differ from <see cref="Bid"/>
    /// (the latest snapshot's bid, e.g. an unanswered reinforcement clash); 0 = not steered.</summary>
    public int SteeringBid { get; init; }
}

/// <summary>J|att|def: an army joined a combat after it started, with its regiments and commander (PROTOCOL §5.6). Informational.</summary>
public sealed record ArmyJoined(int Bid, SideKey Side, ArmySnapshot Army, IReadOnlyList<CharacterSnapshot> Characters) : Ck3Event
{
    /// <summary>ck3xbl_j_steer (PROTOCOL §14.5): 0 not steered, 1 joined the Bannerlord winner's side, 2 joined the loser's side and a
    /// second clash is offered, 3 joined the loser's side without an offer, 4 the combat is being handed back.</summary>
    public int SteerRole { get; init; }
}

/// <summary>ST|BEGIN, ST|P, ST|SUSPEND, ST|RESUME, ST|WIN, ST|HANDBACK, ST|FIX, ST|DONE: trace of a steered CK3 combat (PROTOCOL
/// §14.5). Bid = the steering bid; Values = the record's ck3xbl_* dump keys. The typed members read the keys of §14.5 and are null
/// when this record does not carry the key or the key is bound to another stage.</summary>
public sealed record SteeringTrace(int Bid, string Stage, IReadOnlyDictionary<string, string> Values) : Ck3Event
{
    // The mod saves the ST dump values with save_scope_value_as. Each pulse starts with only scope:ck3xbl_chain, but records of
    // one pulse (or of one apply chain) share its saved values, so a dump may still carry a key an earlier record of the same
    // pulse set (an ST|WIN carries that pulse's ST|P keys). The stage-bound keys
    // are therefore read only on the stages that set them fresh (PROTOCOL §14.5); on any other stage they are null / false.
    /// <summary>ck3xbl_st_parent (BEGIN): the steering bid this clash continues; 0 = the first clash.</summary>
    public int? ParentBid => On("BEGIN") ? Int("ck3xbl_st_parent") : null;
    /// <summary>ck3xbl_st_child (SUSPEND): the bid of the reinforcement clash just offered.</summary>
    public int? ChildBid => On("SUSPEND") ? Int("ck3xbl_st_child") : null;
    /// <summary>ck3xbl_st_mode (BEGIN, RESUME): 1 decide, 2 hand back.</summary>
    public int? Mode => On("BEGIN", "RESUME") ? Int("ck3xbl_st_mode") : null;
    /// <summary>ck3xbl_st_reason (SUSPEND, RESUME, HANDBACK, DONE); its meaning depends on the stage.</summary>
    public int? Reason => On("SUSPEND", "RESUME", "HANDBACK", "DONE") ? Int("ck3xbl_st_reason") : null;
    /// <summary>ck3xbl_st_winner (BEGIN, WIN): 1 attacker, 2 defender, 0 none.</summary>
    public int? Winner => On("BEGIN", "WIN") ? Int("ck3xbl_st_winner") : null;
    /// <summary>ck3xbl_st_cday: the combat day.</summary>
    public int? CombatDay => Int("ck3xbl_st_cday");
    /// <summary>ck3xbl_st_end_day (BEGIN): the combat day the steering ends by.</summary>
    public int? EndDay => On("BEGIN") ? Int("ck3xbl_st_end_day") : null;
    /// <summary>ck3xbl_st_att_killed / ck3xbl_st_def_killed: enemy dead CK3 counted for the attacker / defender side so far.</summary>
    public int? AttackerKilled => Int("ck3xbl_st_att_killed");
    public int? DefenderKilled => Int("ck3xbl_st_def_killed");
    /// <summary>ck3xbl_st_att_target / ck3xbl_st_def_target: the kill targets of the payload (r8 / r9).</summary>
    public int? AttackerTarget => Int("ck3xbl_st_att_target");
    public int? DefenderTarget => Int("ck3xbl_st_def_target");
    /// <summary>ck3xbl_st_capped (WIN, HANDBACK): the winner's gain sat at its maximum, CK3 could not kill fast enough.</summary>
    public bool Capped => On("WIN", "HANDBACK") && Int("ck3xbl_st_capped") is > 0;

    private bool On(params string[] stages) => Array.IndexOf(stages, Stage) >= 0;

    private int? Int(string key) =>
        Values.TryGetValue(key, out var raw) &&
        double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? (int)Math.Round(v, MidpointRounding.AwayFromZero)
            : null;
}

/// <summary>The log file was recreated: CK3 restarted, every in-flight battle is gone.</summary>
public sealed record LogRestarted : Ck3Event;
