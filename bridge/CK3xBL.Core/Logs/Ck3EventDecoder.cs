// Records -> events. Snapshot records go through BattleSnapshotBuilder; lifecycle records (CHOICE, IN, RES, END, ST) become events
// directly; a J record and the AX/R/RX/C/CX lines after it become one ArmyJoined event (PROTOCOL §5.6), emitted when a record that
// does not belong to the join arrives or the log goes idle (Flush). Unknown record types are ignored.
//
// The bid of every record with a dump is its ck3xbl_bid value (§5.1); a number in the marker (original §1 form) is accepted too.
//
// Session key: request ids are "b<bid>_<session>". CK3's bid counter lives in the save, so it restarts when CK3 relaunches (log
// recreated) and goes backwards when an older save is loaded. In both cases the session key is renewed, which keeps every request
// id (and its archive folder) unique.
using System.Security.Cryptography;

namespace CK3xBL.Core.Logs;

public sealed class Ck3EventDecoder
{
    private readonly Action<Ck3Event> _sink;
    private readonly Action<string> _warn;
    private readonly BattleSnapshotBuilder _builder;
    private int _highestBid;
    private TimeSpan _joinTime;

    public Ck3EventDecoder(Action<Ck3Event> sink, Action<string>? warn = null, string? sessionKey = null)
    {
        _sink = sink;
        _warn = warn ?? (_ => { });
        _builder = new BattleSnapshotBuilder(_warn);
        SessionKey = sessionKey ?? NewSessionKey();
    }

    public string SessionKey { get; private set; }

    public static string NewSessionKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant();

    public void LogRestarted()
    {
        _builder.Reset();
        _highestBid = 0;
        SessionKey = NewSessionKey();
        _sink(new LogRestarted());
    }

    /// <summary>The log went idle: nothing more belongs to a pending join.</summary>
    public void Flush() => EmitJoin();

    public void Accept(Ck3Record r)
    {
        if (_builder.HasPendingJoin && !(BattleSnapshotBuilder.IsJoinFollowUp(r.Type) && _builder.OpenBid is null)) EmitJoin();

        if (r.Type == "B" && r.Arg(0) == "BEGIN" && BattleSnapshotBuilder.BidOf(r, 1) is { } bid)
        {
            if (bid <= _highestBid)
            {
                _warn($"battle id went back from {_highestBid} to {bid} (older save loaded?); new session key");
                SessionKey = NewSessionKey();
            }
            _highestBid = bid;
        }

        if (r.Type == "J")
        {
            if (_builder.BeginJoin(r)) _joinTime = r.Time;
            return;
        }

        if (BattleSnapshotBuilder.IsSnapshotRecord(r.Type))
        {
            if (_builder.Accept(r, SessionKey) is { } snapshot)
                _sink(new SnapshotCompleted(snapshot) { LogTime = r.Time });
            return;
        }

        switch (r.Type)
        {
            case "CHOICE": Choice(r); break;
            case "IN": Input(r); break;
            case "RES": Result(r); break;
            case "END": CombatEnd(r); break;
            case "ST": Steering(r); break;
        }
    }

    private void EmitJoin()
    {
        if (_builder.TakeJoin() is { } join)
            _sink(new ArmyJoined(join.Bid, join.Side, join.Army, join.Characters) { LogTime = _joinTime, SteerRole = join.SteerRole });
    }

    private void Choice(Ck3Record r)
    {
        string kind = r.Arg(0).ToUpperInvariant();
        Ck3Choice? choice = kind switch
        {
            _ when kind.StartsWith("FIELD", StringComparison.Ordinal) => Ck3Choice.Field,
            _ when kind.StartsWith("AUTO", StringComparison.Ordinal) => Ck3Choice.Auto,
            "ABANDON" => Ck3Choice.Abandon,
            _ => null,
        };
        int? bid = BattleSnapshotBuilder.BidOf(r, 1);
        if (choice is null || bid is null) { _warn($"unrecognised choice record: {r.Raw}"); return; }
        _sink(new ChoiceMade(bid.Value, choice.Value, kind) { LogTime = r.Time });
    }

    private void Input(Ck3Record r)
    {
        string kind = r.Arg(0).ToUpperInvariant();
        if (kind is not ("OK" or "BAD")) return;
        int? bid = BattleSnapshotBuilder.BidOf(r, 1);
        if (bid is null) { _warn($"IN record without a bid: {r.Raw}"); return; }
        _sink(new InputAck(bid.Value, kind == "OK", r.Dump?.GetInt("ck3xbl_in_count") ?? 0, r.Dump?.GetLong("ck3xbl_in_sum") ?? 0)
            { LogTime = r.Time, MaxVersion = r.Dump?.GetInt("ck3xbl_in_maxver") ?? 0 });
    }

    private void Result(Ck3Record r)
    {
        int? bid = BattleSnapshotBuilder.BidOf(r, 1);
        if (bid is null) { _warn($"RES record without a bid: {r.Raw}"); return; }
        var values = r.Dump?.Values ?? new Dictionary<string, string>();
        _sink(new ResultTrace(bid.Value, r.Arg(0).ToUpperInvariant(), values) { LogTime = r.Time });
    }

    private void Steering(Ck3Record r)
    {
        int? bid = BattleSnapshotBuilder.BidOf(r, 1);
        if (bid is null) { _warn($"ST record without a bid: {r.Raw}"); return; }
        var values = r.Dump?.Values ?? new Dictionary<string, string>();
        _sink(new SteeringTrace(bid.Value, r.Arg(0).ToUpperInvariant(), values) { LogTime = r.Time });
    }

    private void CombatEnd(Ck3Record r)
    {
        string kind = r.Arg(0).ToLowerInvariant();
        if (kind is not ("winner" or "loser")) return;
        var d = r.Dump;
        var primary = d is not null && BattleSnapshotBuilder.Sentinel(d, "ck3xbl_end_has_primary") ? d.GetScope("ck3xbl_end_primary") : null;
        _sink(new CombatEnded(BattleSnapshotBuilder.BidOf(r, 1) ?? 0, kind == "winner", d?.GetBool("ck3xbl_end_wipe") ?? false,
            primary is null ? null : BattleSnapshotBuilder.Ref(primary)) { LogTime = r.Time, SteeringBid = d?.GetInt("ck3xbl_end_st_bid") ?? 0 });
    }
}
