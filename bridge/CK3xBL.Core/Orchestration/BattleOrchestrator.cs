// The battle state machine (PROTOCOL §1.3 / §3.3 / §8.3).
//
//   Idle ─snapshot─▶ Preparing ─request written (preload)─▶ Offered ─CHOICE FIELD─▶ (WaitingToEnter) ─▶ Entering ─visible─▶ Fighting
//     ▲                                                        │ AUTO / ABANDON: cancel                                   │ result
//     │                                                        ▼                                                          ▼
//     └──────────── Delivering ◀── (AwaitingReturnFocus) ◀── Returning ◀─────────── fading_out / black ──────────── ResultShown
//
// Design rules:
//  * One battle is active at a time; further snapshots queue. If the player answers a queued battle first, it is staged instead.
//  * All state lives on one mailbox loop (Handle never awaits). Slow work runs as ACTIVITIES (Task.Run) that post their result
//    back; a stale or cancelled activity's result is ignored. Every Bannerlord command/request goes through one ordered chain, and
//    each command waits until the module took it, so e.g. a 'cancel' always reaches the module before the next battle's request.
//  * Bannerlord's session is reconciled with ours: a battle the module holds that the bridge does not own is cancelled (once), a
//    request is written again only after the process that held it exited, and the window is cloaked again whenever the player is
//    taken back to CK3.
//  * Never steal focus from another app: entering waits until CK3 is the foreground window; the return hands focus back through
//    the module (handoff_to) and only falls back to the activation ladder when the foreground window is one of ours.
//  * Abort hotkey meaning depends on the phase: cancel (before the battle is visible), abort (fighting), return_now (scoreboard),
//    stop injecting (return).
//  * Bannerlord lost (crash, exit, stale heartbeat) while presenting: hide overlay, focus CK3, restore audio, tell the player.
//  * Battle clock (PROTOCOL §14): a battle delivered as payload version 4 is detached into _steering,
//    so the Bannerlord slot is free at once, while CK3 fights the result out over a few days. The bridge only follows a steered
//    battle (ST records, END); it never acts on it. A reinforcement clash (a snapshot naming a parent) is planned from the parent's
//    end state and goes to the front of the queue, because CK3 is paused on it.
using System.Threading.Channels;
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Bannerlord;
using CK3xBL.Core.Ck3;
using CK3xBL.Core.Delivery;
using CK3xBL.Core.Diagnostics;
using CK3xBL.Core.Logs;
using CK3xBL.Core.Model;
using CK3xBL.Core.Native;
using CK3xBL.Core.Texts;
using CK3xBL.Protocol;

namespace CK3xBL.Core.Orchestration;

public enum BattlePhase
{
    Idle,
    /// <summary>Building the request (mapping, appearance) and handing it to Bannerlord for preloading.</summary>
    Preparing,
    /// <summary>Request preloading in Bannerlord; waiting for the player's choice in CK3 event 0001.</summary>
    Offered,
    /// <summary>Player chose to fight, but CK3 is not the foreground window or Bannerlord is not ready yet.</summary>
    WaitingToEnter,
    /// <summary>Title card up, CK3 muted, Bannerlord brought forward and told 'go'; waiting for 'visible'.</summary>
    Entering,
    Fighting,
    /// <summary>Bannerlord scoreboard / countdown.</summary>
    ResultShown,
    /// <summary>Result card up, outcome built, focus being handed back to CK3.</summary>
    Returning,
    /// <summary>The outcome is ready but the player is in another app; delivered when CK3 is in front again.</summary>
    AwaitingReturnFocus,
    /// <summary>Sending the outcome into CK3 (keypad or run file) and waiting for the acknowledgement.</summary>
    Delivering,
}

/// <summary>Where a battle CK3 fights out after the Bannerlord fight stands (PROTOCOL §14.5 ST records).</summary>
public enum SteeringState
{
    /// <summary>Delivered; the player has not clicked "The dust settles..." yet (no ST|BEGIN).</summary>
    Awaiting,
    Steering,
    /// <summary>Reinforcements reached the Bannerlord loser: a second clash is offered, CK3 fights natively meanwhile.</summary>
    Suspended,
    /// <summary>The end was issued (ST|WIN / ST|HANDBACK).</summary>
    Ending,
    /// <summary>CK3 ended the combat; the armies get their end state the next day.</summary>
    Fixing,
    Closed,
}

/// <summary>Progress of a steered battle for the tray (raised on the orchestrator loop). EnemyFallen / EnemyTarget are counted for the
/// player's side: enemy dead CK3 counted so far and the Bannerlord number it steers to; null until CK3 has logged them.</summary>
public sealed record SteeringProgress(int Bid, string Battle, SteeringState State, int? CombatDay, int? EnemyFallen, int? EnemyTarget);

public sealed class OrchestratorOptions
{
    public TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    public TimeSpan OfferTimeout = TimeSpan.FromMinutes(10);
    public TimeSpan EnterTimeout = TimeSpan.FromSeconds(120);
    public TimeSpan AbortTimeout = TimeSpan.FromSeconds(20);
    /// <summary>Before darkening: wait (at most this long) until no key or mouse button is held.</summary>
    public TimeSpan HeldKeysTimeout = TimeSpan.FromSeconds(1.5);
    /// <summary>Before injecting: physical input idle this long.</summary>
    public TimeSpan QuietInput = TimeSpan.FromMilliseconds(300);
    public TimeSpan QuietInputTimeout = TimeSpan.FromSeconds(3);
    /// <summary>How long delivery keeps waiting for the player to stop using keyboard and mouse. After that it types anyway when no
    /// modifier or mouse button is held, and gives up only if one still is.</summary>
    public TimeSpan InputBusyPatience = TimeSpan.FromSeconds(60);
    public TimeSpan FgSettle = TimeSpan.FromMilliseconds(150);
    /// <summary>Entering waits at most this long for Bannerlord to load a battle requested just before (PROTOCOL §40); under EnterTimeout.</summary>
    public TimeSpan LoadWaitTimeout = TimeSpan.FromSeconds(90);
    /// <summary>PROTOCOL §24: give CK3's RAM to Bannerlord once the battle has started (CK3 is paused behind it meanwhile).</summary>
    public bool TrimCk3Memory = true;
    /// <summary>How long after 'go' the trim runs: Bannerlord has the foreground and CK3 has stopped drawing its last frames.</summary>
    public TimeSpan TrimCk3Delay = TimeSpan.FromSeconds(3);
    /// <summary>CK3's memory is trimmed only when less RAM than this (MB) is free as the battle starts.</summary>
    public long TrimCk3BelowFreeRamMb = 6000;
    /// <summary>Before typing a result into a CK3 whose memory was trimmed: wait (at most this long) until its working set stops
    /// growing, i.e. it has paged its memory back in.</summary>
    public TimeSpan Ck3WarmUpTimeout = TimeSpan.FromSeconds(20);
    /// <summary>PROTOCOL §31: fewer men on the field at once when the free memory would not hold the configured battle size.</summary>
    public bool MemoryGuard;
    /// <summary>Commit (MB) kept free for Windows and the other programs when the battle size is fitted to the free memory.</summary>
    public int MemoryReserveMb = 2000;
    public Func<long?> FreeCommitMb = ProcessMemory.FreeCommitMb;
    /// <summary>PROTOCOL §32: the feat ledger (feats.json in the shared folder); empty = no battle feats.</summary>
    public string FeatLedgerPath = "";
    public TimeSpan HandoffWait = TimeSpan.FromSeconds(1.5);
    /// <summary>How long the return waits for the final result.json (finished/aborted) before using the provisional one.</summary>
    public TimeSpan ResultWait = TimeSpan.FromSeconds(3);
    public TimeSpan Ck3FadeOut = TimeSpan.FromMilliseconds(600);
    public TimeSpan Ck3FadeIn = TimeSpan.FromMilliseconds(500);
    public TimeSpan BannerlordFadeIn = TimeSpan.FromMilliseconds(800);
    public TimeSpan BannerlordFadeOut = TimeSpan.FromMilliseconds(600);
    /// <summary>With the module title off (presentation.inEngineTitle = false): the bridge card stays fully up this long once the
    /// battle is visible, then dissolves over RevealDissolve into it (PROTOCOL §13.6).</summary>
    public TimeSpan RevealHold = TimeSpan.FromMilliseconds(400);
    public TimeSpan RevealDissolve = TimeSpan.FromMilliseconds(900);
    public int MaxDeliveryAttempts = 3;
    /// <summary>With the battle window closed (the keypad probe went unanswered), the probe is sent again this often while CK3 is in
    /// front, at most KeypadTimedProbes times; after that only when the player comes back to CK3 from another window. Each probe
    /// that misses presses F3 and F2 into CK3's interface (military and realm windows), so it must stay rare.</summary>
    public TimeSpan KeypadReprobe = TimeSpan.FromSeconds(20);
    public int KeypadTimedProbes = 3;
    /// <summary>'fg' + 'go' sent again at most this often when Bannerlord still holds the battle after entering.</summary>
    public int MaxReentries = 2;
    /// <summary>Battle clock: a journal warning when a steered battle logs no ST|P for this long while CK3 runs unpaused.</summary>
    public TimeSpan SteeringStallWarning = TimeSpan.FromSeconds(30);
    /// <summary>Battle clock: a journal warning when a delivered battle still has no ST|BEGIN after this long (the player never clicked).</summary>
    public TimeSpan SteeringAwaitWarning = TimeSpan.FromMinutes(30);
    /// <summary>Battle clock: a steered battle still open after this long is forgotten.</summary>
    public TimeSpan SteeringMaxAge = TimeSpan.FromHours(24);
    /// <summary>Battle clock: ST|P is journaled for the first pulse and then every this many pulses (each one goes to pulse.jsonl).</summary>
    public int SteeringPulseJournalEvery = 5;
}

public sealed class OrchestratorServices
{
    public required IBattleMapper Mapper;
    public required IAppearanceProvider Appearance;
    public required ITransitionUx Ux;
    public required ICk3Delivery Delivery;
    public required IBannerlordLink Bannerlord;
    public required ICk3Locator Ck3;
    public required IDesktop Desktop;
    public required INotifier Notifier;
    public required IBattleArchive Archive;
    public required IJournal Journal;
    /// <summary>Read per battle, so config changes apply to the next battle. Returns a fresh instance per call (the orchestrator sets
    /// BattleClock.MaxPayloadVersion on it).</summary>
    public required Func<MapperOptions> MapperOptions;
    /// <summary>Optional: whether CK3 is paused (null = unknown), for the stall warning of a steered battle.</summary>
    public Func<bool?>? Ck3Paused;
    /// <summary>Optional: tells the presentation layer which processes are the games (their windows may hold the foreground
    /// while a card is up; any other foreground app makes the overlay step aside).</summary>
    public Action<int>? GameProcessSeen;
    /// <summary>Optional: one-line hint under the current card (null clears), e.g. when Bannerlord could not be activated.</summary>
    public Func<string?, CancellationToken, Task>? ShowHint;
}

public sealed class BattleOrchestrator : IAsyncDisposable
{
    private const string Area = "battle";

    private readonly OrchestratorServices _s;
    private readonly OrchestratorOptions _o;
    private readonly Channel<Input> _inbox = Channel.CreateUnbounded<Input>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _life = new();
    private readonly List<Battle> _queue = new();
    private readonly HashSet<int> _unstageable = new();
    /// <summary>Battle ids a 'cancel' was sent for (mailbox only): their sessions are being ended already, so a status naming one
    /// must not trigger a second 'cancel' (one without a session would take the next battle's pending request).</summary>
    private readonly HashSet<string> _cancelsSent = new();
    private readonly object _blGate = new(), _uxGate = new(), _verdictGate = new();
    private int _verdicts, _verdictsAgreeing;   // Bannerlord winners checked against CK3's favourite this session (Ck3Verdict)
    private Task _blChain = Task.CompletedTask;
    private Task _recovery = Task.CompletedTask;
    private Task? _loop, _ticker;
    private Battle? _active;
    /// <summary>Delivered version 4 battles CK3 is fighting out, by steering bid (mailbox only).</summary>
    private readonly Dictionary<int, SteeringBattle> _steering = new();
    /// <summary>Steering bids journaled once as unknown (ST records of a battle this bridge run does not follow).</summary>
    private readonly HashSet<int> _unknownSteering = new();
    private DateTime _lastTick = DateTime.UtcNow;
    private volatile BattlePhase _phase = BattlePhase.Idle;
    private int _activitySeq;
    /// <summary>Process that last reported its first idle (0 = none since the last exit).</summary>
    private int _readyPid;
    // Presentation bookkeeping: what must be undone (touched by activities, hence the lock).
    private bool _overlayShown;
    private int _ck3MutedPid, _bannerlordMutedPid;
    /// <summary>CK3's working set was trimmed for the current battle: it is warmed up before the result is typed.</summary>
    private volatile bool _ck3Trimmed;

    public BattleOrchestrator(OrchestratorServices services, OrchestratorOptions? options = null)
    {
        _s = services;
        _o = options ?? new OrchestratorOptions();
    }

    public BattlePhase Phase => _phase;
    public string? ActiveBattleId => _active?.Id;
    /// <summary>Raised on the orchestrator loop thread; handlers must be quick.</summary>
    public event Action<BattlePhase, string?>? PhaseChanged;
    /// <summary>A steered battle progressed or closed (State Closed). Raised on the orchestrator loop thread; handlers must be quick.</summary>
    public event Action<SteeringProgress>? SteeringChanged;

    public void Start()
    {
        _loop ??= Task.Run(RunLoop);
        _ticker ??= Task.Run(RunTicker);
    }

    public void OnCk3Event(Ck3Event e) => Post(new EventInput(e));
    public void OnBannerlordStatus(BannerlordStatus s) => Post(new StatusInput(s));
    public void OnBannerlordLost(BannerlordLoss kind, string reason) => Post(new LostInput(kind, reason));
    public void OnBannerlordResult(BattleResult result) => Post(new ResultInput(result));
    public void OnAbortHotkey() => Post(new HotkeyInput());

    /// <summary>Polls until the phase is reached (tests, diagnostics).</summary>
    public async Task<bool> WaitForPhaseAsync(BattlePhase phase, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (_phase != phase)
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(5).ConfigureAwait(false);
        }
        return true;
    }

    private void Post(Input input) => _inbox.Writer.TryWrite(input);

    private async Task RunLoop()
    {
        try
        {
            await foreach (var input in _inbox.Reader.ReadAllAsync(_life.Token).ConfigureAwait(false))
            {
                try { Handle(input); }
                catch (Exception e) { _s.Journal.Error(Area, $"handling {input.GetType().Name} failed", e); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunTicker()
    {
        using var timer = new PeriodicTimer(_o.TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_life.Token).ConfigureAwait(false)) Post(TickInput.Instance);
        }
        catch (OperationCanceledException) { }
    }

    private void Handle(Input input)
    {
        switch (input)
        {
            case EventInput { Event: SnapshotCompleted e }: OnSnapshot(e.Snapshot); break;
            case EventInput { Event: ChoiceMade e }: OnChoice(e); break;
            case EventInput { Event: LogRestarted }: OnCk3Restarted(); break;
            case EventInput { Event: CombatEnded e }: OnCombatEnded(e); break;
            case EventInput { Event: ResultTrace { Stage: "AW" } e }: _s.Journal.Info(Area, $"CK3 RES|AW for battle {e.Bid}: {DescribeAward(e)}"); break;
            case EventInput { Event: ResultTrace e }: _s.Journal.Info(Area, $"CK3 RES|{e.Stage} for battle {e.Bid}"); break;
            case EventInput { Event: SteeringTrace e }: OnSteering(e); break;
            case EventInput { Event: ArmyJoined e }:
                _s.Journal.Info(Area, $"army {e.Army.Tag} ({e.Army.Name}) joined battle {e.Bid} as {e.Side}{JoinMeaning(e.SteerRole)}");
                break;
            case EventInput: break;
            case StatusInput s: OnStatus(s.Status); break;
            case LostInput l: OnLost(l.Kind, l.Reason); break;
            case ResultInput r: OnResult(r.Result); break;
            case HotkeyInput: OnHotkey(); break;
            case TickInput: OnTick(); break;
            case ActivityDone d: OnActivityDone(d); break;
        }
    }

    // =========================================================================================================== CK3 events

    private void OnSnapshot(BattleSnapshot snapshot)
    {
        var b = new Battle(snapshot);
        _s.Journal.Info(Area, $"{b.Id}: {snapshot.CombatName} at {snapshot.Location.ProvinceName} ({snapshot.Attacker.Size} vs {snapshot.Defender.Size}, " +
                              $"{snapshot.Attacker.Armies.Count}+{snapshot.Defender.Armies.Count} armies)");

        // A new session key means CK3 reloaded an older save: battles of the old timeline will never get a choice.
        DropWhere(x => x.SessionKey != snapshot.SessionKey, "superseded by a reloaded save");

        if (AllBattles().Any(x => x.Bid == snapshot.Bid)) { _s.Journal.Warn(Area, $"{b.Id}: duplicate snapshot ignored"); return; }
        if (snapshot.ParentBid > 0)
        {
            if (!_s.MapperOptions().BattleClock.ReinforcementClash)
            {
                // battleClock.reinforcementClash = false: not offered. FIELD gets the "cannot be fought" notice; the mod's failsafe
                // hands the parent back after three days.
                _unstageable.Add(b.Bid);
                _s.Journal.Info(Area, $"{b.Id}: reinforcement clash of battle {snapshot.ParentBid} not offered (battleClock.reinforcementClash = false)");
                return;
            }
            b.Parent = FindParent(b);
        }
        if (_active is null) Activate(b);
        else if (snapshot.ParentBid > 0)
        {
            _queue.Insert(0, b);
            _s.Journal.Info(Area, $"{b.Id}: reinforcement clash, first in line behind {_active.Id} (CK3 is paused on it)");
        }
        else
        {
            _queue.Add(b);
            _s.Journal.Info(Area, $"{b.Id}: queued behind {_active.Id}");
        }
    }

    /// <summary>The clash a reinforcement clash continues: the steered battle this run follows, else the parent's archive (the bridge
    /// restarted meanwhile). Without either, the clash is planned from the live armies, uncapped, with non-cumulative targets.
    /// Bids repeat across sessions, so the newest archived snapshot of the bid must be the same CK3 combat, and its request and
    /// outcome are read from that very folder (a battle that was offered but never fought has no outcome).</summary>
    private ParentLink? FindParent(Battle b)
    {
        int parentBid = b.Snapshot.ParentBid;
        if (_steering.TryGetValue(parentBid, out var steered))
            return new ParentLink { Planned = steered.Planned, Request = steered.Request, Outcome = steered.Outcome,
                                    FallenTags = new HashSet<int>(steered.FallenTags), Root = steered.Root };
        string prefix = $"b{parentBid}_";
        if (_s.Archive.TryLoad<BattleSnapshot>(prefix, "snapshot") is { } planned && planned.CombatId == b.Snapshot.CombatId &&
            _s.Archive.TryLoad<BattleRequest>(planned.RequestId, "request") is { } request &&
            _s.Archive.TryLoad<Ck3Outcome>(planned.RequestId, "outcome") is { } outcome)
        {
            _s.Journal.Info(Area, $"{b.Id}: continues battle {parentBid}, restored from the battle archive");
            // A parent that was itself a reinforcement clash archived the first clash's snapshot next to its own (PrepareAsync).
            var root = planned.ParentBid > 0 ? _s.Archive.TryLoad<BattleSnapshot>(planned.RequestId, "snapshot_root") : null;
            return new ParentLink { Planned = planned, Request = request, Outcome = outcome,
                                    FallenTags = Fallen(outcome, Array.Empty<int>()), Root = root };
        }
        _s.Journal.Warn(Area, $"{b.Id}: continues battle {parentBid}, which this bridge does not know; planned from the live armies " +
                              "(uncapped, kill targets of this clash only)");
        return null;
    }

    private void OnChoice(ChoiceMade c)
    {
        var b = AllBattles().FirstOrDefault(x => x.Bid == c.Bid);
        _s.Journal.Info(Area, $"battle {c.Bid}: player chose {c.Kind}");
        if (b is null && c.Choice == Ck3Choice.Field && !_unstageable.Contains(c.Bid) && RestoreFromArchive(c) is { } restored)
            b = restored;
        if (b is null)
        {
            if (c.Choice == Ck3Choice.Field)
                Notify(_unstageable.Contains(c.Bid) ? "This battle cannot be fought in Bannerlord" : "No Bannerlord battle is prepared",
                    "Choose 'Let the battle run its course' in Crusader Kings III.", NoticeKind.Warning);
            else if (_steering.TryGetValue(c.Bid, out var s) && s.State == SteeringState.Awaiting)
                Close(s, $"the player chose {c.Kind} instead of '{Ck3Texts.DustSettlesOption}'");   // after IN|OK, before the click
            return;
        }

        if (b != _active)
        {
            if (c.Choice != Ck3Choice.Field) { _queue.Remove(b); return; }
            b.Choice = Ck3Choice.Field;
            b.Mode = ReliefModeOf(c.Kind);
            if (_active is { Choice: null } current && _phase is BattlePhase.Preparing or BattlePhase.Offered)
            {
                // The player answered the queued battle first: stage it now and put the other one back in line.
                _s.Journal.Info(Area, $"{b.Id} answered first; {current.Id} goes back to the queue");
                CancelActivities(current);
                if (current.Preloaded) CancelInBannerlord(current.Id);
                current.Preloaded = false;
                _queue.Remove(b);
                _queue.Insert(0, current);
                _active = null;
                Activate(b);
            }
            return;
        }

        switch (c.Choice)
        {
            case Ck3Choice.Auto:
            case Ck3Choice.Abandon:
                Abandon(b, $"player chose {c.Kind}", notice: null);
                break;
            case Ck3Choice.Field when b.Choice is null:
                b.Choice = Ck3Choice.Field;
                string mode = ReliefModeOf(c.Kind);
                if (mode != b.Mode)
                {
                    // PROTOCOL §40: the walls instead of the field; the prepared field battle gives way to a siege request.
                    b.Mode = mode;
                    _s.Journal.Info(Area, $"{b.Id}: fought at the walls ({mode}); planned again");
                    if (b.Preloaded) CancelInBannerlord(b.Id);
                    b.Preloaded = false;
                    SetPhase(BattlePhase.Preparing);
                    StartActivity(b, "prepare", ct => PrepareAsync(b, ct));
                    break;
                }
                if (_phase == BattlePhase.Offered) BeginEnter(b);
                break;
        }
    }

    /// <summary>
    /// The player chose the field for a battle this bridge has no snapshot of: CK3 logged it before a restart of CK3 or the bridge
    /// (a save loaded with the battle's call to arms open; live b298 2026-10-20, after a crash). Its snapshot is taken from the battle
    /// archive, where every prepared battle keeps one, and the battle is staged as chosen. Null when the archive has none.
    /// </summary>
    private Battle? RestoreFromArchive(ChoiceMade c)
    {
        if (_s.Archive.TryLoad<BattleSnapshot>($"b{c.Bid}_", "snapshot") is not { } snapshot || snapshot.Bid != c.Bid) return null;
        var b = new Battle(snapshot) { Choice = Ck3Choice.Field, Mode = ReliefModeOf(c.Kind) };
        _s.Journal.Info(Area, $"{b.Id}: not logged in this session; restored from the battle archive ({snapshot.CombatName} at " +
                              $"{snapshot.Location.ProvinceName}) and staged as chosen");
        if (snapshot.ParentBid > 0) b.Parent = FindParent(b);
        if (_active is null) Activate(b);
        else _queue.Insert(0, b);
        return b;
    }

    /// <summary>The way a relief battle is fought (PROTOCOL §40): CHOICE|FIELD_DEFEND holds the walls, CHOICE|FIELD_CAMP strikes the
    /// besiegers' camp; every other choice is the field ("").</summary>
    internal static string ReliefModeOf(string kind) => kind.ToUpperInvariant() switch
    {
        "FIELD_DEFEND" => "defend",
        "FIELD_CAMP" => "camp",
        _ => "",
    };

    private void OnCombatEnded(CombatEnded e)
    {
        if (e.Bid == 0) return;
        _s.Journal.Info(Area, $"CK3 ended battle {e.Bid} ({(e.WinnerSide ? "winner" : "loser")} side{(e.Wipe ? ", wiped" : "")}" +
                              $"{(e.SteeringBid > 0 ? $", steered as {e.SteeringBid}" : "")})");
        // A steered battle is never dropped by its END: the mod sets the armies' end state the next day and closes it with ST|DONE.
        // The END of a steered combat may name a newer snapshot's bid (an unanswered reinforcement clash), hence the steering bid.
        if (_steering.TryGetValue(e.SteeringBid > 0 ? e.SteeringBid : e.Bid, out var s))
        {
            if (s.State == SteeringState.Awaiting) Close(s, "CK3 ended the battle before the result was applied");
            else if (s.State != SteeringState.Fixing)
            {
                s.State = SteeringState.Fixing;
                _s.Journal.Info(Area, $"{s.Id}: CK3 ended the battle; the armies take their end state tomorrow");
                Publish(s);
            }
        }
        // CK3 resolved a battle we were still offering (it cannot end while the player is in Bannerlord: CK3 is paused).
        var b = AllBattles().FirstOrDefault(x => x.Bid == e.Bid);
        if (b is not null && (b != _active || _phase is BattlePhase.Preparing or BattlePhase.Offered or BattlePhase.WaitingToEnter))
            Drop(b, "CK3 resolved it", cancelBannerlord: b == _active && b.Preloaded);
    }

    private void OnCk3Restarted()
    {
        _unknownSteering.Clear();
        if (_active is null && _queue.Count == 0 && _steering.Count == 0) return;
        _s.Journal.Warn(Area, "CK3 restarted; dropping all battles in flight");
        DropWhere(_ => true, "CK3 restarted");
    }

    // =========================================================================================================== battle clock

    /// <summary>PROTOCOL §14.7: a delivered version 4 battle continues in CK3. The bridge follows it in _steering and frees the
    /// Bannerlord slot for the next battle at once.</summary>
    private void Detach(Battle b)
    {
        var s = new SteeringBattle(b, DateTime.UtcNow);
        _steering[s.Bid] = s;
        _s.Journal.Info(Area, $"{b.Id}: battle continues in CK3" + (b.Snapshot.ParentBid > 0 ? $" (clash {b.Snapshot.Clash} of battle {b.Snapshot.ParentBid})" : ""));
        // ST records the apply logged before the delivery returned (run file: before its RES|END; keypad: between IN|OK and the end
        // of the reveal): ST|BEGIN, or ST|FIX and ST|DONE reason 4 when the combat was gone or too old at the click.
        if (b.EarlySteering.Count == 0) Publish(s);
        else
            foreach (var e in b.EarlySteering)
            {
                if (s.State == SteeringState.Closed) break;
                Follow(s, e);
            }
        Drop(b, "outcome delivered to CK3", cancelBannerlord: false);
    }

    private void OnSteering(SteeringTrace e)
    {
        if (_steering.TryGetValue(e.Bid, out var s))
        {
            Follow(s, e);
            return;
        }
        if (_active is { } a && a.Bid == e.Bid && _phase == BattlePhase.Delivering)
        {
            // The apply logs its first ST records before the delivery returns (dev mode: before the run file's RES|END); Detach
            // replays them in order.
            a.EarlySteering.Add(e);
            return;
        }
        if (_unknownSteering.Add(e.Bid)) _s.Journal.Info(Area, $"CK3 ST|{e.Stage} for battle {e.Bid}, which this bridge run does not follow");
    }

    private void Follow(SteeringBattle s, SteeringTrace e)
    {
        s.Record(e);
        switch (e.Stage)
        {
            case "BEGIN":
                s.State = SteeringState.Steering;
                s.LastPulseAt = DateTime.UtcNow;
                s.UnpausedSincePulse = TimeSpan.Zero;
                _s.Journal.Info(Area, $"{s.Id}: CK3 steers the battle ({(e.Mode == 2 ? "hands it back afterwards" : "then the Bannerlord winner wins")}), " +
                                      $"kill targets {s.AttackerTarget ?? 0:N0} / {s.DefenderTarget ?? 0:N0}, by combat day {e.EndDay ?? 0}");
                if (e.ParentBid is int parent && parent > 0 && parent != s.Bid && _steering.TryGetValue(parent, out var superseded))
                    Close(superseded, $"superseded by clash {s.Clash} (battle {s.Bid})");
                break;
            case "P":
                s.PulseCount++;
                s.LastPulseAt = DateTime.UtcNow;
                s.UnpausedSincePulse = TimeSpan.Zero;
                s.StallWarned = false;
                if (s.State == SteeringState.Awaiting) s.State = SteeringState.Steering;
                _s.Archive.Append(s.Id, "pulse", new SteeringRecord(e.Stage, e.LogTime, e.Values));
                if ((s.PulseCount - 1) % Math.Max(1, _o.SteeringPulseJournalEvery) == 0)
                    _s.Journal.Info(Area, $"{s.Id}: combat day {e.CombatDay ?? 0}, attackers killed {e.AttackerKilled ?? 0:N0} of {s.AttackerTarget ?? 0:N0}, " +
                                          $"defenders killed {e.DefenderKilled ?? 0:N0} of {s.DefenderTarget ?? 0:N0}");
                break;
            case "SUSPEND":
                s.State = SteeringState.Suspended;
                _s.Journal.Info(Area, $"{s.Id}: reinforcements reached the Bannerlord loser; steering paused, battle {e.ChildBid ?? 0} offered as the next clash");
                break;
            case "RESUME":
                s.State = SteeringState.Steering;
                _s.Journal.Info(Area, $"{s.Id}: steering resumes and hands the battle back to CK3 afterwards ({ResumeReason(e.Reason)})");
                break;
            case "WIN" or "HANDBACK":
                s.State = SteeringState.Ending;
                _s.Journal.Info(Area, $"{s.Id}: CK3 ST|{e.Stage} on combat day {e.CombatDay ?? 0} ({Compact(e.Values)})");
                if (e.Capped)
                    _s.Journal.Warn(Area, $"{s.Id}: CK3 could not kill fast enough: attackers killed {s.AttackerKilled ?? 0:N0} of {s.AttackerTarget ?? 0:N0}, " +
                                          $"defenders killed {s.DefenderKilled ?? 0:N0} of {s.DefenderTarget ?? 0:N0}");
                break;
            case "FIX":
                _s.Journal.Info(Area, $"{s.Id}: CK3 ST|FIX ({Compact(e.Values)})");
                break;
            case "DONE":
                Done(s, e.Reason ?? 0);
                return;   // Close published it
            default:
                _s.Journal.Info(Area, $"{s.Id}: CK3 ST|{e.Stage} ({Compact(e.Values)})");
                break;
        }
        Publish(s);
    }

    /// <summary>ST|DONE: CK3 has finished the battle and set the armies' end state.</summary>
    private void Done(SteeringBattle s, int reason)
    {
        string how = reason switch
        {
            1 => "CK3 decided it as in Bannerlord.",
            2 => "CK3 inflicted the Bannerlord losses and fights on by itself.",
            3 => "The battle ended before CK3 had inflicted every Bannerlord loss; the rest was applied directly.",
            4 => "The battle was over (or too old) before CK3 could fight it out; the losses were applied directly.",
            5 => "CK3 gave the victory to the other side.",
            6 => "CK3 did not end the battle when asked; it was handed back to CK3.",
            _ => $"Steering finished (reason {reason}).",
        };
        var (fallen, target, ours, ourTarget) = s.PlayerView();
        string numbers = fallen is { } f && target is { } t
            ? $" Enemy dead counted by CK3: {f:N0} of {t:N0}" + (ours is { } o && ourTarget is { } u ? $"; ours: {o:N0} of {u:N0}." : ".")
            : "";
        Close(s, $"ST|DONE reason {reason}", ($"{s.BattleName} is over", how + numbers, reason == 6 ? NoticeKind.Warning : NoticeKind.Info));
    }

    /// <summary>Removes a steered battle and archives its trace (steering.json).</summary>
    private void Close(SteeringBattle s, string reason, (string Title, string Text, NoticeKind Kind)? notice = null)
    {
        s.State = SteeringState.Closed;
        _steering.Remove(s.Bid);
        _s.Archive.Save(s.Id, "steering", s.ToArchive(reason, DateTime.UtcNow));
        _s.Journal.Info(Area, $"{s.Id}: steering closed ({reason})");
        Publish(s);
        if (notice is { } n) Notify(n.Title, n.Text, n.Kind);
    }

    /// <summary>Journal warnings for a battle that stopped pulsing while CK3 runs, or whose result the player never clicked in; an
    /// entry older than SteeringMaxAge is forgotten.</summary>
    private void WatchSteering(DateTime now, TimeSpan elapsed)
    {
        bool running = _s.Ck3Paused?.Invoke() == false;
        foreach (var s in _steering.Values.ToList())
        {
            if (now - s.DeliveredAt > _o.SteeringMaxAge)
            {
                Close(s, $"still open after {_o.SteeringMaxAge.TotalHours:0} h");
                continue;
            }
            switch (s.State)
            {
                case SteeringState.Awaiting when !s.AwaitWarned && now - s.DeliveredAt > _o.SteeringAwaitWarning:
                    s.AwaitWarned = true;
                    _s.Journal.Warn(Area, $"{s.Id}: delivered {(now - s.DeliveredAt).TotalMinutes:0} min ago, but '{Ck3Texts.DustSettlesOption}' was not clicked in CK3");
                    break;
                case SteeringState.Steering or SteeringState.Suspended when running:
                    s.UnpausedSincePulse += elapsed;
                    if (!s.StallWarned && s.UnpausedSincePulse > _o.SteeringStallWarning)
                    {
                        s.StallWarned = true;
                        _s.Journal.Warn(Area, $"{s.Id}: no ST|P for {s.UnpausedSincePulse.TotalSeconds:0} s while CK3 runs; its daily pulse may have stopped");
                    }
                    break;
            }
        }
    }

    private void Publish(SteeringBattle s)
    {
        var (fallen, target, _, _) = s.PlayerView();
        try { SteeringChanged?.Invoke(new SteeringProgress(s.Bid, s.BattleName, s.State, s.CombatDay, fallen, target)); }
        catch (Exception e) { _s.Journal.Warn(Area, $"SteeringChanged handler: {e.Message}"); }
    }

    /// <summary>The tags of characters captured or killed (fate 3/4) so far, carried into the next clash.</summary>
    private static HashSet<int> Fallen(Ck3Outcome outcome, IEnumerable<int> earlier) =>
        earlier.Concat(outcome.Characters.Where(c => c.Fate is 3 or 4 && c.Tag > 0).Select(c => c.Tag)).ToHashSet();

    private static string JoinMeaning(int steerRole) => steerRole switch
    {
        1 => ": the Bannerlord winner's side, it shares the victory",
        2 => ": reinforcements for the Bannerlord loser, a second clash is offered",
        3 => ": reinforcements for the Bannerlord loser, no second clash",
        4 => ": the battle is being handed back to CK3",
        _ => "",
    };

    private static string ResumeReason(int? reason) => reason switch
    {
        2 => "the player let the reinforcement clash run its course",
        3 => "the reinforcement clash was not answered in time",
        4 => "the reinforcement clash was cancelled in Bannerlord",
        _ => $"reason {reason}",
    };

    /// <summary>"st_winner=1, st_cday=9" from a dump: the ck3xbl_ prefix and the two decimals dropped.</summary>
    private static string Compact(IReadOnlyDictionary<string, string> values) => string.Join(", ", values
        .Where(kv => kv.Key != "ck3xbl_bid")
        .Select(kv => $"{(kv.Key.StartsWith("ck3xbl_", StringComparison.Ordinal) ? kv.Key[7..] : kv.Key)}={(kv.Value.EndsWith(".00", StringComparison.Ordinal) ? kv.Value[..^3] : kv.Value)}"));

    // =========================================================================================================== Bannerlord

    private void OnStatus(BannerlordStatus s)
    {
        if (s.Status == BannerlordStates.Idle && s.Pid != _readyPid)
        {
            // First idle of this process: a launch, a relaunch after a crash, or a restart from the tray.
            _readyPid = s.Pid;
            OnBannerlordReady();
        }
        var b = _active;
        if (BannerlordStates.HoldsSession(s.Status) && s.BattleId is { } held && held != b?.Id)
        {
            // Bannerlord holds a battle the bridge does not own (left by an earlier bridge run, or a request taken twice). It would
            // keep the module busy and swallow the next battle's 'go'.
            CancelStray(held, s.Status);
            return;
        }
        if (b is null) return;

        // Bannerlord came back (after an exit, or the offer timeout dropped the preload): preload again.
        if (s.Status == BannerlordStates.Idle && !b.Preloaded && !b.PreloadDropped && b.Request is not null &&
            _phase is BattlePhase.Offered or BattlePhase.WaitingToEnter)
            Preload(b);

        // Back at idle while we think the battle is on: the module went through fading_out/black between two status polls.
        if (s.Status == BannerlordStates.Idle && _phase is BattlePhase.Fighting or BattlePhase.ResultShown)
        {
            BeginReturn(b);
            return;
        }

        if (s.BattleId != b.Id) return;
        switch (s.Status)
        {
            case BannerlordStates.Held when _phase == BattlePhase.Entering && b.EnterSent && b.Reentries < _o.MaxReentries:
                // Entering finished but the battle is still held: 'fg' or the activation did not take effect (e.g. the module was
                // stalled by a load step, or its heartbeat was lost meanwhile). Ask again instead of waiting for the enter timeout.
                b.Reentries++;
                _s.Journal.Warn(Area, $"{b.Id}: still held after entering; sending 'fg' and 'go' again ({b.Reentries})");
                StartActivity(b, "reenter", ct => ReenterAsync(b, ct));
                break;
            case BannerlordStates.Visible or BannerlordStates.Fighting when _phase == BattlePhase.Entering:
                SetPhase(BattlePhase.Fighting);
                StartReveal(b);
                break;
            case BannerlordStates.Result when _phase is BattlePhase.Entering or BattlePhase.Fighting:
                if (_phase == BattlePhase.Entering) StartReveal(b);
                SetPhase(BattlePhase.ResultShown);
                b.Result = _s.Bannerlord.ReadResult(b.Id) ?? b.Result;
                break;
            case BannerlordStates.FadingOut or BannerlordStates.Black or BannerlordStates.Ended or BannerlordStates.Aborted
                when _phase is BattlePhase.Fighting or BattlePhase.ResultShown:
                BeginReturn(b);
                break;
            case BannerlordStates.Error when _phase is BattlePhase.Entering or BattlePhase.Fighting or BattlePhase.ResultShown:
                Fail(b, $"Bannerlord reported an error: {s.Error}");
                break;
            case BannerlordStates.Error when _phase is BattlePhase.Preparing or BattlePhase.Offered or BattlePhase.WaitingToEnter:
                Unstageable(b, s.Error);
                break;
            case BannerlordStates.Cancelled when _phase is BattlePhase.Entering or BattlePhase.Fighting:
                Fail(b, "Bannerlord cancelled the battle.");
                break;
        }
    }

    private void CancelStray(string battleId, string status)
    {
        if (_cancelsSent.Contains(battleId)) return;
        _s.Journal.Warn(Area, $"Bannerlord holds battle {battleId} ({status}) that the bridge does not own; cancelling it");
        CancelInBannerlord(battleId);
    }

    /// <summary>result.json changed: a final result ends the battle even if the matching status transition was missed.</summary>
    private void OnResult(BattleResult r)
    {
        var b = _active;
        if (b is null || r.Id != b.Id) return;
        if (_phase is BattlePhase.Entering or BattlePhase.Fighting or BattlePhase.ResultShown) b.Result = r;
        switch (r.Status)
        {
            case "finished" or "aborted" when _phase is BattlePhase.Fighting or BattlePhase.ResultShown:
                BeginReturn(b);
                break;
            case "aborted" or "cancelled" or "error" when _phase == BattlePhase.Entering:
            case "cancelled" or "error" when _phase is BattlePhase.Fighting or BattlePhase.ResultShown:
                Fail(b, r.Error is null ? $"Bannerlord ended the battle ({r.Status})." : $"Bannerlord reported an error: {r.Error}");
                break;
            case "error" when _phase is BattlePhase.Preparing or BattlePhase.Offered or BattlePhase.WaitingToEnter:
                Unstageable(b, r.Error);
                break;
        }
    }

    /// <summary>Bannerlord could not load the battle before the player entered it: CK3 resolves it natively.</summary>
    private void Unstageable(Battle b, string? error)
    {
        _unstageable.Add(b.Bid);
        bool chosen = b.Choice == Ck3Choice.Field;
        Drop(b, $"Bannerlord could not load it: {error}", cancelBannerlord: false);
        Notify("This battle cannot be fought in Bannerlord", (error is null ? "" : error + "\n") + (chosen
            ? "Choose 'Let the battle run its course' in Crusader Kings III."
            : "CK3 will resolve it: choose 'Let my commanders decide the day'."), NoticeKind.Warning);
    }

    private void OnBannerlordReady()
    {
        // First idle after a (re)launch: hide and throttle it, and give focus back if the booting game took it from CK3.
        _ = BannerlordCommand(BannerlordCommands.Background).ContinueWith(_ =>
        {
            if (_active is not null) return;
            var ck3 = _s.Ck3.Find();
            var fg = _s.Desktop.ForegroundWindow;
            if (ck3 is { } c && fg != c.Hwnd && _s.Bannerlord.ForegroundBeforeLaunch == c.Hwnd &&
                _s.Desktop.ProcessIdOf(fg) == _s.Bannerlord.ProcessId)
            {
                _s.Journal.Info(Area, "Bannerlord took the foreground while booting; giving it back to CK3");
                _s.Ux.BringToFront(c.Hwnd);
            }
        }, TaskScheduler.Default);
    }

    private void OnLost(BannerlordLoss kind, string reason)
    {
        bool exited = kind == BannerlordLoss.Exited;
        if (exited)
        {
            _readyPid = 0;
            _cancelsSent.Clear();   // the next process holds nothing of this one
        }
        var b = _active;
        if (b is null) return;
        switch (_phase)
        {
            case BattlePhase.Preparing or BattlePhase.Offered or BattlePhase.WaitingToEnter when exited:
                if (b.Preloaded)
                {
                    // The preload died with the process. A request written just before the exit is withdrawn here, in order behind any
                    // write still queued, so the next process gets it exactly once: when it reports idle (OnStatus).
                    b.Preloaded = false;
                    _ = Bannerlord(() => _s.Bannerlord.WithdrawRequestAsync(b.Id, _life.Token), $"withdraw {b.Id}");
                }
                _s.Journal.Warn(Area, $"{b.Id}: {reason}; will preload again when Bannerlord is back");
                break;
            case BattlePhase.Preparing or BattlePhase.Offered or BattlePhase.WaitingToEnter:
                // Only the heartbeat is late: the process (and the battle it may hold) is still there.
                _s.Journal.Warn(Area, $"{b.Id}: {reason}; keeping the preload");
                break;
            case BattlePhase.Entering or BattlePhase.Fighting or BattlePhase.ResultShown:
                Fail(b, exited ? "Bannerlord closed unexpectedly." : "Bannerlord stopped responding.", cancelBannerlord: !exited);
                break;
            // Returning and later: the outcome is (being) built from result.json; Bannerlord is no longer needed.
        }
    }

    // =========================================================================================================== hotkey & tick

    private void OnHotkey()
    {
        var b = _active;
        _s.Journal.Info(Area, $"abort hotkey in phase {_phase}");
        if (b is null) return;
        switch (_phase)
        {
            case BattlePhase.Preparing or BattlePhase.Offered or BattlePhase.WaitingToEnter:
                Abandon(b, "abort hotkey", ("Battle handed back to CK3", "Choose 'Let my commanders decide the day' (or 'Let the battle run its course')."));
                break;
            case BattlePhase.Entering:
                Abandon(b, "abort hotkey", ("Battle cancelled", "Choose 'Let the battle run its course' in Crusader Kings III."));
                break;
            case BattlePhase.Fighting when !b.AbortRequested:
                b.AbortRequested = true;
                b.AbortRequestedAt = DateTime.UtcNow;
                _ = BannerlordCommand(BannerlordCommands.Abort);
                break;
            case BattlePhase.ResultShown:
                _ = BannerlordCommand(BannerlordCommands.ReturnNow);
                break;
            case BattlePhase.Returning or BattlePhase.AwaitingReturnFocus or BattlePhase.Delivering:
                Abandon(b, "abort hotkey during the return",
                    ("Result not delivered", "Choose 'Let the battle run its course' in Crusader Kings III."), cancelBannerlord: false);
                break;
        }
    }

    private void OnTick()
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastTick;
        _lastTick = now;
        if (_steering.Count > 0) WatchSteering(now, elapsed);
        var b = _active;
        if (b is null) return;
        switch (_phase)
        {
            case BattlePhase.Offered when b.Choice is null && b.Preloaded && now - b.OfferedAt > _o.OfferTimeout:
                _s.Journal.Info(Area, $"{b.Id}: no answer for {_o.OfferTimeout.TotalMinutes:0} min; dropping the preload (reloaded on demand)");
                CancelInBannerlord(b.Id);
                b.Preloaded = false;
                b.PreloadDropped = true;
                break;
            case BattlePhase.WaitingToEnter:
                if (CanEnter(out _)) BeginEnter(b);
                else if ((!_s.Bannerlord.IsRunning || _s.Bannerlord.Status is null) && now - b.WaitingSince > _o.EnterTimeout)
                    Fail(b, _s.Bannerlord.Problem ?? "Bannerlord did not start.");
                break;
            case BattlePhase.Entering when now - b.EnteringSince > _o.EnterTimeout:
                Fail(b, "Bannerlord did not show the battle in time.");
                break;
            case BattlePhase.Fighting when b.AbortRequested && now - b.AbortRequestedAt > _o.AbortTimeout:
                Fail(b, "Bannerlord did not end the battle after the abort request.");
                break;
            case BattlePhase.AwaitingReturnFocus when _s.Ck3.Find() is not { } ck3 || _s.Desktop.ForegroundWindow != ck3.Hwnd:
                if (b.KeypadClosedAt is not null) b.LeftCk3SinceProbe = true;
                break;
            case BattlePhase.AwaitingReturnFocus when b.KeypadClosedAt is not { } closedAt || b.LeftCk3SinceProbe ||
                                                      (b.KeypadProbes <= _o.KeypadTimedProbes && now - closedAt >= _o.KeypadReprobe):
                BeginDeliver(b, showCard: true, countAttempt: false);
                break;
        }
    }

    // =========================================================================================================== transitions

    private void Activate(Battle b)
    {
        _active = b;
        SetPhase(BattlePhase.Preparing);
        StartActivity(b, "prepare", ct => PrepareAsync(b, ct));
    }

    private void Preload(Battle b)
    {
        b.Preloaded = true;
        b.PreloadDropped = false;
        bool cancelPending = _cancelsSent.Remove(b.Id);
        // Never hand the module a battle it already holds: it takes requests only when idle, so a second copy would be loaded after
        // this battle and swallow the next battle's 'go'.
        if (!cancelPending && _s.Bannerlord is { IsRunning: true, Status: { } st } bl && st.Pid == bl.ProcessId && st.BattleId == b.Id &&
            BannerlordStates.HoldsSession(st.Status))
        {
            _s.Journal.Info(Area, $"{b.Id}: Bannerlord already holds it ({st.Status}); not written again");
            return;
        }
        var request = b.Request!;
        _ = Bannerlord(() => _s.Bannerlord.WriteRequestAsync(request, _life.Token), $"preload {b.Id}");
    }

    private bool CanEnter(out string why)
    {
        if (_s.Ck3.Find() is not { } ck3 || _s.Desktop.ForegroundWindow != ck3.Hwnd)
        {
            why = "Your host awaits: return to Crusader Kings III to take the field.";
            return false;
        }
        var status = _s.Bannerlord.Status?.Status;
        if (!_s.Bannerlord.IsRunning || status is null || status == BannerlordStates.Booting)
        {
            why = _s.Bannerlord.Problem ?? "Bannerlord is still starting; the battle begins as soon as it is ready.";
            return false;
        }
        why = "";
        return true;
    }

    private void BeginEnter(Battle b)
    {
        if (!CanEnter(out var why))
        {
            if (_phase != BattlePhase.WaitingToEnter)
            {
                b.WaitingSince = DateTime.UtcNow;
                SetPhase(BattlePhase.WaitingToEnter);
                Notify("The Armies Clash", why);
            }
            return;
        }
        if (!b.Preloaded) Preload(b);
        b.EnteringSince = DateTime.UtcNow;
        SetPhase(BattlePhase.Entering);
        StartActivity(b, "enter", ct => EnterAsync(b, ct));
    }

    private void BeginReturn(Battle b)
    {
        SetPhase(BattlePhase.Returning);
        StartActivity(b, "return", ct => ReturnAsync(b, ct));
    }

    private void BeginDeliver(Battle b, bool showCard, bool countAttempt = true)
    {
        if (countAttempt) b.DeliveryAttempts++;
        // Within the patience the player's keyboard/mouse use postpones delivery; after it, typing starts once no key is held.
        bool insistOnQuiet = b.InputBusySince is not { } since || DateTime.UtcNow - since < _o.InputBusyPatience;
        SetPhase(BattlePhase.Delivering);
        StartActivity(b, "deliver", ct => DeliverAsync(b, showCard, insistOnQuiet, ct));
    }

    /// <summary>The player (or the hotkey) took the battle back to CK3.</summary>
    private void Abandon(Battle b, string reason, (string Title, string Text)? notice, bool cancelBannerlord = true)
    {
        bool presenting = IsPresenting(_phase);
        Drop(b, reason, cancelBannerlord && (b.Preloaded || presenting));
        if (presenting) StartRecovery(focusCk3: true);
        if (notice is { } n) Notify(n.Title, n.Text);
    }

    /// <summary>Something went wrong while presenting: undo everything visible and tell the player how CK3 continues.</summary>
    private void Fail(Battle b, string reason, bool cancelBannerlord = true)
    {
        _s.Journal.Warn(Area, $"{b.Id}: {reason}");
        bool presenting = IsPresenting(_phase);
        bool battleOver = _phase is BattlePhase.Returning or BattlePhase.AwaitingReturnFocus or BattlePhase.Delivering;
        Drop(b, reason, cancelBannerlord && !battleOver && (b.Preloaded || presenting));
        if (presenting) StartRecovery(focusCk3: true);
        Notify("The battle could not continue in Bannerlord", $"{reason}\nChoose 'Let the battle run its course' in Crusader Kings III.",
            NoticeKind.Warning);
    }

    /// <summary>Removes a battle. Queued battles were never sent to Bannerlord; for the active one, Bannerlord is told to cancel
    /// (when asked) and the next queued battle is activated.</summary>
    private void Drop(Battle b, string reason, bool cancelBannerlord)
    {
        CancelActivities(b);
        if (_queue.Remove(b))
        {
            _s.Journal.Info(Area, $"{b.Id}: removed from the queue ({reason})");
            return;
        }
        if (b != _active) return;
        if (cancelBannerlord) CancelInBannerlord(b.Id);
        _s.Journal.Info(Area, $"{b.Id}: finished ({reason})");
        _active = null;
        SetPhase(BattlePhase.Idle);
        if (_queue.Count > 0)
        {
            var next = _queue[0];
            _queue.RemoveAt(0);
            Activate(next);
        }
    }

    /// <summary>Drops every matching battle, steered ones included; if the active one was on screen, the presentation is undone too.</summary>
    private void DropWhere(Func<BattleSnapshot, bool> match, string reason)
    {
        foreach (var steered in _steering.Values.Where(s => match(s.Snapshot)).ToList()) Close(steered, reason);
        foreach (var queued in _queue.Where(x => match(x.Snapshot)).ToList()) Drop(queued, reason, cancelBannerlord: false);
        if (_active is not { } a || !match(a.Snapshot)) return;
        bool presenting = IsPresenting(_phase);
        Drop(a, reason, cancelBannerlord: a.Preloaded || presenting);
        if (presenting) StartRecovery(focusCk3: false);
    }

    /// <summary>Ends a battle in Bannerlord, in order with every other command: 'cancel &lt;id&gt;' while Bannerlord runs (it ends the
    /// session, or drops the request not taken yet), else the request is withdrawn so that the next process never takes it.</summary>
    private void CancelInBannerlord(string battleId)
    {
        _cancelsSent.Add(battleId);
        _ = Bannerlord(async () =>
        {
            if (_s.Bannerlord.IsRunning)
                await _s.Bannerlord.SendCommandAsync(BannerlordCommands.CancelBattle(battleId), _life.Token).ConfigureAwait(false);
            else await _s.Bannerlord.WithdrawRequestAsync(battleId, _life.Token).ConfigureAwait(false);
        }, $"cancel {battleId}");
    }

    // =========================================================================================================== activities

    private void StartActivity(Battle b, string name, Func<CancellationToken, Task<object?>> work)
    {
        int id = ++_activitySeq;
        b.ActivityId = id;
        var ct = b.Cts.Token;
        _ = Task.Run(async () =>
        {
            object? result = null;
            Exception? error = null;
            try { result = await work(ct).ConfigureAwait(false); }
            catch (Exception e) { error = e; }
            Post(new ActivityDone(b, id, name, result, error));
        });
    }

    private void OnActivityDone(ActivityDone d)
    {
        var b = d.Battle;
        if (b != _active || d.Id != b.ActivityId) return;                       // stale: battle dropped or superseded
        if (d.Error is OperationCanceledException) return;
        if (d.Error is not null)
        {
            _s.Journal.Error(Area, $"{b.Id}: {d.Name} failed", d.Error);
            if (d.Name == "prepare")
            {
                _unstageable.Add(b.Bid);
                Drop(b, "preparation failed", cancelBannerlord: false);
                Notify("This battle cannot be fought in Bannerlord", b.Choice == Ck3Choice.Field
                    ? "Choose 'Let the battle run its course' in Crusader Kings III."
                    : "CK3 will resolve it: choose 'Let my commanders decide the day'.", NoticeKind.Warning);
            }
            else Fail(b, $"Something went wrong ({d.Name}: {d.Error.Message}).");
            return;
        }

        switch (d.Name)
        {
            case "prepare":
                // The request is handed over here, on the mailbox, so it is ordered with everything else that touches it (a loss of
                // Bannerlord, a cancel) and Preloaded only ever changes on this thread.
                var prepared = (Prepared)d.Result!;
                if (prepared.Mode != b.Mode)
                {
                    StartActivity(b, "prepare", ct => PrepareAsync(b, ct));   // the player chose the walls meanwhile (PROTOCOL §40)
                    break;
                }
                b.Request = prepared.Request;
                b.Planned = prepared.Planned;
                b.Id = b.Request.Id;
                b.OfferedAt = DateTime.UtcNow;
                Preload(b);
                SetPhase(BattlePhase.Offered);
                if (b.Choice == Ck3Choice.Field) BeginEnter(b);
                break;
            case "enter" when d.Result is false:
                // CK3 lost the foreground before anything was shown: wait for the player to come back.
                SetPhase(BattlePhase.Offered);
                BeginEnter(b);
                break;
            case "enter":
                b.EnterSent = true;
                break;
            case "return":
                if (d.Result is true) BeginDeliver(b, showCard: false);
                else WaitForReturnFocus(b);
                break;
            case "deliver":
                OnDeliveryDone(b, (DeliveryResult)d.Result!);
                break;
            case "redeliver":
                BeginDeliver(b, showCard: false);
                break;
        }
    }

    /// <summary>The pause before a refused delivery is sent again (CK3 settles after paging its memory back in).</summary>
    private static readonly TimeSpan RedeliverPause = TimeSpan.FromSeconds(3);

    private void OnDeliveryDone(Battle b, DeliveryResult result)
    {
        if (result is DeliveryResult.InputBusy or DeliveryResult.KeysHeld) b.InputBusySince ??= DateTime.UtcNow;
        else b.InputBusySince = null;
        if (result != DeliveryResult.KeypadClosed && result != DeliveryResult.FocusLost) b.KeypadClosedAt = null;
        switch (result)
        {
            // As delivered: a keypad re-send to a mod from before the battle clock lowers the outcome to version 3 (applied at once).
            case DeliveryResult.Delivered when b.Outcome is { PayloadVersion: KeypadLayout.SteerPayloadVersion, OutcomeCode: >= 1 and <= 3 }:
                Detach(b);
                break;
            case DeliveryResult.Delivered:
                Drop(b, "outcome delivered to CK3", cancelBannerlord: false);
                break;
            case DeliveryResult.FocusLost:
                // The player switched away (or something took the focus): nothing was applied, and no attempt is spent; the result
                // waits for CK3 to be in front again, however often that happens (live b299 lost a won battle to two switches).
                WaitForReturnFocus(b);
                break;
            case DeliveryResult.KeypadClosed:
                // The keypad did not answer: typing on would press CK3's own hotkeys (windows, game speed, menu items). Nothing is
                // spent; the probe goes again rarely, or when the player comes back to CK3, until the battle window answers.
                b.KeypadClosedAt = DateTime.UtcNow;
                b.KeypadProbes++;
                b.LeftCk3SinceProbe = false;
                SetPhase(BattlePhase.AwaitingReturnFocus);
                StartRecovery(focusCk3: false);
                if (b.KeypadProbes == 1)
                    Notify("The dispatch waits for the battle window",
                        "Close other windows and menus in Crusader Kings III so the battle event is in front, then leave the keyboard and mouse for a few seconds.",
                        NoticeKind.Warning);
                break;
            case DeliveryResult.Failed when b.DeliveryAttempts < _o.MaxDeliveryAttempts && _s.Ck3.Find() is not null:
                // CK3 refused the keys (IN|BAD or no answer), mostly a CK3 still slow from paging its memory back in: once more after a
                // pause, never handing a fought battle back to CK3 at the first refusal (live 2026-10-19, b288).
                _s.Journal.Warn(Area, $"{b.Id}: delivery failed; trying again (attempt {b.DeliveryAttempts + 1} of {_o.MaxDeliveryAttempts})");
                StartActivity(b, "redeliver", async ct =>
                {
                    await Task.Delay(RedeliverPause, ct).ConfigureAwait(false);
                    return null;
                });
                break;
            case DeliveryResult.InputBusy:
            case DeliveryResult.KeysHeld when DateTime.UtcNow - b.InputBusySince!.Value < _o.InputBusyPatience:
                // The player is using the keyboard or mouse: nothing was typed, so this is not an attempt. The card stays up.
                BeginDeliver(b, showCard: false, countAttempt: false);
                break;
            default:
                if (result == DeliveryResult.KeysHeld) StartRecovery(focusCk3: false);   // the card was left up for the retries
                Drop(b, "delivery failed", cancelBannerlord: false);
                Notify("The result could not be entered into CK3",
                    (result == DeliveryResult.KeysHeld ? "A key or mouse button stayed held. " : "") +
                    "Choose 'Let the battle run its course' in Crusader Kings III. The battle record is in the bridge log folder.",
                    NoticeKind.Warning);
                break;
        }
    }

    private void WaitForReturnFocus(Battle b)
    {
        SetPhase(BattlePhase.AwaitingReturnFocus);
        StartRecovery(focusCk3: false);   // never trap the player behind a covered screen
        Notify("The dispatch has arrived", $"Return to Crusader Kings III and click '{Ck3Texts.DustSettlesOption}' to receive the outcome.");
    }

    /// <summary>PROTOCOL §32: the player's deeds of a decided clash go into the feat ledger; the feats they earn ride in r7.</summary>
    private void CountFeats(Battle b, Ck3Outcome outcome)
    {
        if (outcome.Feats is not { } deeds || string.IsNullOrEmpty(_o.FeatLedgerPath) || outcome.OutcomeCode is not (1 or 2)) return;
        var feats = FeatLedger.CountInFile(_o.FeatLedgerPath, deeds, b.Id, out string? problem);
        if (problem is not null) _s.Journal.Warn("feats", $"{b.Id}: feat ledger not updated: {problem}");
        outcome.FeatMask = (int)feats;
        _s.Journal.Info("feats", FormattableString.Invariant(
            $"{b.Id}: {deeds.Kills} kills, {deeds.HeroesDowned} heroes downed, won {deeds.Won}, wounded {deeds.Wounded}, enemy:own {deeds.EnemyToOwn:0.##}, terrain '{deeds.Terrain}', winter {deeds.Winter}, at sea {deeds.AtSea}, landing {deeds.Landing}, slew a commander {deeds.SlewCommander} -> {feats}"));
    }

    /// <summary>PROTOCOL §31: a battle size that the free commit would not hold crashes Bannerlord when the battle starts; such a
    /// battle is fought with fewer men on the field at once (the rest come as reinforcements).</summary>
    private void FitBattleSizeToMemory(Battle b, MapperOptions options)
    {
        if (!_o.MemoryGuard) return;
        long? free = null;
        try { free = _o.FreeCommitMb(); }
        catch (Exception e) { _s.Journal.Warn("memory", $"{b.Snapshot.RequestId}: free memory unknown: {e.Message}"); }
        int fit = MemoryGuard.Fit(options.BattleSize, free, _o.MemoryReserveMb);
        if (fit < options.BattleSize)
        {
            _s.Journal.Warn("memory", $"{b.Snapshot.RequestId}: free commit {free} MB holds no battle size {options.BattleSize}; " +
                $"{fit} men on the field at once (enlarge the page file to keep {options.BattleSize})");
            options.BattleSize = fit;
        }
        else _s.Journal.Info("memory", $"{b.Snapshot.RequestId}: free commit {free?.ToString() ?? "?"} MB, battle size {options.BattleSize}");
    }

    /// <summary>Builds the request. A reinforcement clash (PROTOCOL §14.6) is planned from the parent's end state: the mapper caps
    /// the live snapshot, and both the request and later the outcome are built from that capped snapshot.</summary>
    private Task<object?> PrepareAsync(Battle b, CancellationToken ct)
    {
        var options = _s.MapperOptions();
        FitBattleSizeToMemory(b, options);
        string mode = b.Mode;
        options.ReliefMode = mode;
        BattleRequest request;
        BattleSnapshot planned;
        if (b.Parent is { } parent)
        {
            var plan = _s.Mapper.BuildContinuation(b.Snapshot, parent, options);
            (request, planned) = (plan.Request, plan.Capped);
        }
        else (request, planned) = (_s.Mapper.BuildRequest(b.Snapshot, options), b.Snapshot);
        if (string.IsNullOrEmpty(request.Id)) request.Id = b.Snapshot.RequestId;
        request.Presentation ??= new Presentation();
        request.Presentation.Preload = true;
        try { _s.Appearance.Enrich(planned, request); }
        catch (Exception e) { _s.Journal.Warn(Area, $"{request.Id}: appearance enrichment failed, stock looks used: {e.Message}"); }
        // PROTOCOL §40: a defence of the walls or a strike on the camp is a request of its own (the field one may be held by
        // Bannerlord), with the player inside or outside the walls.
        if (mode.Length > 0)
        {
            request.Id = $"{b.Snapshot.RequestId}_{mode}";
            if (SiegeSides.Settle(request)) _s.Journal.Info(Area, $"{request.Id}: sides swapped, the player is the {request.PlayerSide} in Bannerlord");
        }
        ct.ThrowIfCancellationRequested();

        if (b.Parent is { } link)
        {
            _s.Archive.Save(request.Id, "snapshot_live", b.Snapshot);
            _s.Archive.Save(request.Id, "snapshot_root", link.Root ?? link.Planned);   // read back by FindParent after a restart
        }
        _s.Archive.Save(request.Id, "snapshot", planned);
        _s.Archive.Save(request.Id, "request", request);
        if (request.Balance is { } bal)
        {
            // One line per battle for the calibration of the strength balance (PROTOCOL §17); the result line follows at the end.
            double ck3 = bal.Ck3Defender > 0 ? bal.Ck3Attacker / bal.Ck3Defender : 0;
            double bl = bal.BannerlordDefender > 0 ? bal.BannerlordAttacker / bal.BannerlordDefender : 0;
            _s.Journal.Info("balance", FormattableString.Invariant(
                $"{request.Id}: attacker:defender CK3 {ck3:0.###}, Bannerlord {bl:0.###} (k {bal.TiersPerDoubling:0.##}) -> {(bal.Boosted is null ? "no change" : $"{bal.Boosted} x{bal.Factor:0.###}{(bal.Capped ? " (capped)" : "")}")}"));
        }
        return Task.FromResult<object?>(new Prepared(request, planned, mode));
    }

    /// <summary>Title card, CK3 muted, Bannerlord uncloaked, brought to the front and told 'go'. Returns false (having shown
    /// nothing) if CK3 lost the foreground before the switch began.</summary>
    private async Task<object?> EnterAsync(Battle b, CancellationToken ct)
    {
        await _recovery.ConfigureAwait(false);   // a previous battle's overlay must be gone first
        await _s.Desktop.WaitForQuietInputAsync(TimeSpan.Zero, _o.HeldKeysTimeout, ct).ConfigureAwait(false);
        if (_s.Ck3.Find() is not { } ck3 || _s.Desktop.ForegroundWindow != ck3.Hwnd) return false;
        RegisterGames(ck3);

        var show = _s.Ux.ShowTitleCardAsync(TitleCardFor(b, ck3.Hwnd), ct);
        lock (_uxGate) { _overlayShown = true; _ck3MutedPid = ck3.ProcessId; }
        var mute = _s.Ux.RampProcessVolumeAsync(ck3.ProcessId, 0f, _o.Ck3FadeOut, ct);
        await show.ConfigureAwait(false);

        // The module leaves the title to the bridge card (inEngineTitle = false). When the card could not be shown (e.g. the player
        // is in another application), the module must show its own title, or the battle would open without one.
        b.TitleCardPresented = _s.Ux.TitleCardPresented;
        if (b.Request?.Presentation?.InEngineTitle == false && !b.TitleCardPresented)
        {
            _s.Journal.Info(Area, $"{b.Id}: the title card was not shown; the module shows its own");
            await BannerlordCommand(BannerlordCommands.TitleInEngine).ConfigureAwait(false);
        }

        // A request written just before entering (a relief planned again for the walls, PROTOCOL §40) is still loading, or the battle
        // it replaced is still being stopped. Activated then, Bannerlord stalls in the foreground for seconds of scene loading and
        // Windows hands the focus to another window (live 2026-10-18: the overlay's safety valve went off, the player had to alt-tab).
        // The title card stays up until the module holds this battle.
        await WaitUntilHeldAsync(b, ct).ConfigureAwait(false);

        // 'fg' waits until the module took it: while a load step stalls its main thread, the title card simply stays up longer.
        await BannerlordCommand(BannerlordCommands.Foreground).ConfigureAwait(false);
        await Task.Delay(_o.FgSettle, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();   // abandoned meanwhile: never activate a window that is about to be cloaked again
        var window = _s.Bannerlord.MainWindow;
        bool inFront = window != 0 && _s.Ux.BringToFront(window);
        if (!inFront)
        {
            _s.Journal.Warn(Area, $"{b.Id}: Bannerlord could not be activated (window 0x{window:X}); it takes focus on click");
            if (_s.ShowHint is { } hint) await hint(Words.For(b.Request?.Presentation?.Language).ClickToContinue, ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        await BannerlordCommand(BannerlordCommands.Go).ConfigureAwait(false);
        await mute.ConfigureAwait(false);
        if (_o.TrimCk3Memory) TrimCk3Later(b.Id, ck3.ProcessId);
        return true;
    }

    /// <summary>Waits (at most <see cref="OrchestratorOptions.LoadWaitTimeout"/>) while Bannerlord is still loading this battle or still
    /// stopping the one before it; any other state (held, or a module that never says) lets the entry go on at once.</summary>
    private async Task WaitUntilHeldAsync(Battle b, CancellationToken ct)
    {
        var until = DateTime.UtcNow + _o.LoadWaitTimeout;
        bool waited = false;
        while (DateTime.UtcNow < until)
        {
            var st = _s.Bannerlord.Status;
            bool loadingThis = st is { Status: BannerlordStates.Loading } && st.BattleId == b.Id;
            bool stoppingOther = st is { Status: BannerlordStates.Black or BannerlordStates.Cancelled or BannerlordStates.Loading or BannerlordStates.Held }
                                 && st.BattleId is { } other && other != b.Id;
            if (!loadingThis && !stoppingOther) break;
            waited = true;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        if (waited) _s.Journal.Info(Area, $"{b.Id}: waited for Bannerlord to load it ({_s.Bannerlord.Status?.Status ?? "?"} now)");
    }

    /// <summary>Trims CK3's working set in the background, a moment after the battle started (PROTOCOL §24). Best effort: a failure
    /// is one journal line.</summary>
    private void TrimCk3Later(string battleId, int pid)
    {
        var delay = _o.TrimCk3Delay;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                // Only when RAM is short: a trimmed CK3 pages its memory back in for a long while after the battle and drops the
                // result's key presses meanwhile (live b288, b299: 10 GB trimmed to 0.6 GB, 22 of 86 registers lost).
                if (ProcessMemory.FreeRamMb() is { } freeRam && freeRam >= _o.TrimCk3BelowFreeRamMb)
                {
                    _s.Journal.Info("memory", $"{battleId}: {freeRam} MB of RAM free; CK3's memory left as it is");
                    return;
                }
                _ck3Trimmed = true;
                var (before, after, error) = ProcessMemory.Trim(pid);
                if (error is null) _s.Journal.Info("memory", $"{battleId}: CK3 working set {before} MB -> {after} MB, freed for Bannerlord; free commit {ProcessMemory.FreeCommitMb()?.ToString() ?? "?"} MB");
                else _s.Journal.Warn("memory", $"{battleId}: CK3 memory not trimmed: {error}");
            }
            catch (Exception e)
            {
                _s.Journal.Warn("memory", $"{battleId}: CK3 memory not trimmed: {e.Message}");
            }
        });
    }

    /// <summary>Bannerlord still holds the battle after entering: uncloak, activate (only if the foreground is ours) and 'go' again.
    /// Both commands are idempotent in the module.</summary>
    private async Task<object?> ReenterAsync(Battle b, CancellationToken ct)
    {
        await BannerlordCommand(BannerlordCommands.Foreground).ConfigureAwait(false);
        await Task.Delay(_o.FgSettle, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var window = _s.Bannerlord.MainWindow;
        if (window != 0 && _s.Ck3.Find() is { } ck3 && _s.Desktop.ForegroundWindow != window && IsOursOrNone(_s.Desktop.ForegroundWindow, ck3))
            _s.Ux.BringToFront(window);
        await BannerlordCommand(BannerlordCommands.Go).ConfigureAwait(false);
        return null;
    }

    /// <summary>Builds the outcome from the final result.json, shows the result card and hands the foreground back to CK3.
    /// Returns whether CK3 is in front (else the player is elsewhere and delivery waits).</summary>
    private async Task<object?> ReturnAsync(Battle b, CancellationToken ct)
    {
        // Prefer the final result (written at 'black'); the provisional 'result' one is used if the final does not come in time.
        var deadline = DateTime.UtcNow + _o.ResultWait;
        BattleResult? result;
        while ((result = _s.Bannerlord.ReadResult(b.Id)) is not { Status: "finished" or "aborted" or "cancelled" or "error" } &&
               DateTime.UtcNow < deadline)
            await Task.Delay(100, ct).ConfigureAwait(false);
        result ??= b.Result;
        if (result is null || result.Status is "started" or "cancelled" or "error")
            throw new InvalidOperationException($"no usable result.json for {b.Id} (status {result?.Status ?? "missing"})");
        b.Result = result;

        // After a version 3 fallback (an old mod) every later outcome is built as version 3 from the start, and its card says so.
        var options = _s.MapperOptions();
        options.BattleClock.MaxPayloadVersion = Math.Min(options.BattleClock.MaxPayloadVersion, _s.Delivery.MaxPayloadVersion);
        var outcome = _s.Mapper.BuildOutcome(b.Planned, b.Request!, result, options, b.Parent);
        if (outcome.Bid == 0) outcome.Bid = b.Bid;
        if (outcome.ProvinceId == 0) outcome.ProvinceId = b.Snapshot.Location.ProvinceId;
        if (outcome.ParentBid == 0) outcome.ParentBid = b.Snapshot.ParentBid;
        if (outcome.Clash == 1) outcome.Clash = b.Snapshot.Clash;
        if (outcome.PayloadVersion == KeypadLayout.SteerPayloadVersion && outcome.PaceDays == 0) outcome.PaceDays = options.BattleClock.PaceDays;
        if (result.Status == "aborted") outcome.OutcomeCode = 3;   // casualties so far, no winner: CK3 continues the combat
        CountFeats(b, outcome);
        b.Outcome = outcome;
        _s.Archive.Save(b.Id, "result", result);
        _s.Archive.Save(b.Id, "outcome", outcome);
        _s.Journal.Info(Area, $"{b.Id}: outcome {outcome.OutcomeCode} ({(outcome.PlayerWon ? "won" : "lost")}), my lost {outcome.MyLost}, " +
                              $"enemy lost {outcome.EnemyLost}, payload v{outcome.PayloadVersion}" +
                              (outcome.PayloadVersion == KeypadLayout.SteerPayloadVersion ? $", kill targets {outcome.AttackerKillTarget}/{outcome.DefenderKillTarget}" : ""));
        // The calibration counterpart of the 'balance' line written at planning (PROTOCOL §17): how the field fight really went.
        static string Lost(SideResult s) => $"{s.Killed + s.Wounded}/{s.Spawned} down, {s.Routed} routed";
        _s.Journal.Info("balance", FormattableString.Invariant(
            $"{b.Id}: result {result.Status}, winner {result.Winner ?? "none"} after {result.DurationSeconds:0} s; attacker {Lost(result.Attacker)}; defender {Lost(result.Defender)}; player retreated {result.PlayerRetreated}"));
        if (Ck3Verdict(b.Request?.Balance, result.Winner) is { } verdict)
        {
            lock (_verdictGate)
            {
                if (verdict.Agrees is { } agrees) { _verdicts++; if (agrees) _verdictsAgreeing++; }
                _s.Journal.Info("balance", $"{b.Id}: {verdict.Text}; this session Bannerlord agreed with CK3 in {_verdictsAgreeing} of {_verdicts} battles");
            }
        }

        var show = _s.Ux.ShowResultCardAsync(ResultCardFor(b, outcome), ct);
        lock (_uxGate) _overlayShown = true;
        int blPid = _s.Bannerlord.ProcessId;
        var fade = Task.CompletedTask;
        if (blPid != 0)
        {
            lock (_uxGate) _bannerlordMutedPid = blPid;
            fade = _s.Ux.RampProcessVolumeAsync(blPid, 0f, _o.BannerlordFadeOut, ct);
        }
        await show.ConfigureAwait(false);

        if (_s.Ck3.Find() is not { } ck3) throw new InvalidOperationException("Crusader Kings III is not running");
        RegisterGames(ck3);
        if (_s.Desktop.ForegroundWindow != ck3.Hwnd)
        {
            // Bannerlord is the foreground process, so it may legitimately hand the foreground to CK3 itself. A silent module must
            // not hold up the return (its command stays queued and is harmless once CK3 is in front).
            var handoff = BannerlordCommand(BannerlordCommands.HandoffTo(ck3.Hwnd));
            await Task.WhenAny(handoff, Task.Delay(_o.HandoffWait, ct)).ConfigureAwait(false);
            var until = DateTime.UtcNow + _o.HandoffWait;
            while (_s.Desktop.ForegroundWindow != ck3.Hwnd && DateTime.UtcNow < until) await Task.Delay(30, ct).ConfigureAwait(false);
            var fg = _s.Desktop.ForegroundWindow;
            if (fg != ck3.Hwnd && IsOursOrNone(fg, ck3))
            {
                _s.Journal.Info(Area, $"{b.Id}: handoff did not activate CK3; using the activation ladder");
                _s.Ux.BringToFront(ck3.Hwnd);
            }
        }
        _s.Desktop.ReleaseCursorClip();
        _ = BannerlordCommand(BannerlordCommands.Background);
        await fade.ConfigureAwait(false);
        return _s.Desktop.ForegroundWindow == ck3.Hwnd;
    }

    /// <summary>A CK3 whose memory was trimmed for the battle pages it back in once in front again, and drops short key presses
    /// meanwhile: wait until its working set has stopped growing for a moment (at most Ck3WarmUpTimeout).</summary>
    private async Task WarmUpCk3Async(Battle b, int pid, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        long last = ProcessMemory.WorkingSetMb(pid), first = last;
        int stable = 0;
        while (DateTime.UtcNow - start < _o.Ck3WarmUpTimeout && stable < 3)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            long now = ProcessMemory.WorkingSetMb(pid);
            stable = now - last < 50 ? stable + 1 : 0;
            last = now;
        }
        _ck3Trimmed = false;
        _s.Journal.Info("memory", $"{b.Id}: CK3 paged back in ({first} -> {last} MB) in {(DateTime.UtcNow - start).TotalSeconds:0.0} s before the result is typed");
    }

    private async Task<object?> DeliverAsync(Battle b, bool showCard, bool insistOnQuiet, CancellationToken ct)
    {
        var outcome = b.Outcome!;
        DeliveryResult result;
        if (_s.Ck3.Find() is not { } ck3) result = DeliveryResult.Failed;
        else
        {
            if (showCard)
            {
                var show = _s.Ux.ShowResultCardAsync(ResultCardFor(b, outcome), ct);
                lock (_uxGate) _overlayShown = true;
                await show.ConfigureAwait(false);
            }
            if (_ck3Trimmed) await WarmUpCk3Async(b, ck3.ProcessId, ct).ConfigureAwait(false);
            bool quiet = await _s.Desktop.WaitForQuietInputAsync(_o.QuietInput, _o.QuietInputTimeout, ct).ConfigureAwait(false);
            if (_s.Desktop.ForegroundWindow != ck3.Hwnd) result = DeliveryResult.FocusLost;
            else if (!quiet && insistOnQuiet) result = DeliveryResult.InputBusy;
            else result = await _s.Delivery.DeliverAsync(outcome, ct).ConfigureAwait(false) switch
            {
                DeliveryStatus.Delivered => DeliveryResult.Delivered,
                // Interrupted with CK3 still in front: a key or mouse button was held when typing should start.
                DeliveryStatus.Interrupted => _s.Desktop.ForegroundWindow == ck3.Hwnd ? DeliveryResult.KeysHeld : DeliveryResult.FocusLost,
                DeliveryStatus.KeypadClosed => DeliveryResult.KeypadClosed,
                _ => DeliveryResult.Failed,
            };
        }

        if (result is DeliveryResult.InputBusy or DeliveryResult.KeysHeld)
        {
            // Card and muted CK3 stay as they are: the next attempt follows as soon as the player lets go.
            _s.Journal.Info(Area, $"{b.Id}: delivery waits for the keyboard and mouse ({result})");
            return result;
        }
        if (TakeOverlay()) await _s.Ux.HideAsync(ct).ConfigureAwait(false);
        await RestoreCk3AudioAsync().ConfigureAwait(false);
        _s.Journal.Write(result == DeliveryResult.Delivered ? JournalLevel.Info : JournalLevel.Warn, Area, $"{b.Id}: delivery " + result switch
        {
            DeliveryResult.Delivered => "acknowledged",
            DeliveryResult.FocusLost => "interrupted (CK3 lost the foreground)",
            DeliveryResult.KeypadClosed => "not typed: the battle window is not open in CK3",
            _ => "failed",
        });
        return result;
    }

    /// <summary>Once Bannerlord shows the battle: the title card dissolves into it when the module shows no title of its own (PROTOCOL
    /// §13.6), else it is hidden; Bannerlord's sound, if an earlier return faded it out, comes back at the same time.</summary>
    private void StartReveal(Battle b) => _ = Task.Run(async () =>
    {
        try
        {
            bool dissolve = b.Request?.Presentation?.InEngineTitle == false && b.TitleCardPresented;
            var reveal = !TakeOverlay() ? Task.CompletedTask
                : dissolve ? _s.Ux.RevealAsync(_o.RevealHold, _o.RevealDissolve, _life.Token)
                : _s.Ux.HideAsync(_life.Token);
            int pid;
            lock (_uxGate) { pid = _bannerlordMutedPid; _bannerlordMutedPid = 0; }
            var audio = pid != 0 ? _s.Ux.RestoreProcessVolumeAsync(pid, _o.BannerlordFadeIn, _life.Token) : Task.CompletedTask;
            await Task.WhenAll(reveal, audio).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException) { _s.Journal.Warn(Area, $"reveal: {e.Message}"); }
    });

    /// <summary>Undo the presentation: Bannerlord cloaked again, overlay away, cursor free, CK3 in front (only if the foreground is
    /// ours), CK3 sound back.</summary>
    private void StartRecovery(bool focusCk3)
    {
        // After 'fg' only 'bg' cloaks the window and restores the background frame limit; queued behind any 'cancel'.
        if (_s.Bannerlord.IsRunning) _ = BannerlordCommand(BannerlordCommands.Background);
        var previous = _recovery;
        _recovery = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try
            {
                if (TakeOverlay()) await _s.Ux.HideAsync(_life.Token).ConfigureAwait(false);
                _s.Desktop.ReleaseCursorClip();
                if (focusCk3 && _s.Ck3.Find() is { } ck3)
                {
                    var fg = _s.Desktop.ForegroundWindow;
                    if (fg != ck3.Hwnd && IsOursOrNone(fg, ck3)) _s.Ux.BringToFront(ck3.Hwnd);
                }
                await RestoreCk3AudioAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException) { _s.Journal.Warn(Area, $"recovery: {e.Message}"); }
        });
    }

    private async Task RestoreCk3AudioAsync()
    {
        int pid;
        lock (_uxGate) { pid = _ck3MutedPid; _ck3MutedPid = 0; }
        if (pid != 0) await _s.Ux.RestoreProcessVolumeAsync(pid, _o.Ck3FadeIn, _life.Token).ConfigureAwait(false);
    }

    private bool TakeOverlay()
    {
        lock (_uxGate)
        {
            bool shown = _overlayShown;
            _overlayShown = false;
            return shown;
        }
    }

    // =========================================================================================================== helpers

    /// <summary>Runs Bannerlord file operations strictly in issue order; failures are logged, never thrown into the caller's flow.</summary>
    private Task Bannerlord(Func<Task> op, string what)
    {
        lock (_blGate)
        {
            _blChain = _blChain.ContinueWith(async _ =>
            {
                try { await op().ConfigureAwait(false); }
                catch (Exception e) { _s.Journal.Warn(Area, $"{what} failed: {e.Message}"); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            return _blChain;
        }
    }

    private void RegisterGames(Ck3Window ck3)
    {
        if (_s.GameProcessSeen is not { } seen) return;
        seen(ck3.ProcessId);
        if (_s.Bannerlord.ProcessId is var pid and > 0) seen(pid);
    }

    private Task BannerlordCommand(string command) => Bannerlord(() => _s.Bannerlord.SendCommandAsync(command, _life.Token), command);

    private bool IsOursOrNone(nint hwnd, Ck3Window ck3)
    {
        if (hwnd == 0 || !_s.Desktop.IsWindow(hwnd)) return true;
        int pid = _s.Desktop.ProcessIdOf(hwnd);
        return pid == ck3.ProcessId || pid == Environment.ProcessId || (pid != 0 && pid == _s.Bannerlord.ProcessId);
    }

    private static bool IsPresenting(BattlePhase p) =>
        p is BattlePhase.Entering or BattlePhase.Fighting or BattlePhase.ResultShown or BattlePhase.Returning
            or BattlePhase.AwaitingReturnFocus or BattlePhase.Delivering;

    private IEnumerable<Battle> AllBattles() => (_active is null ? Enumerable.Empty<Battle>() : new[] { _active }).Concat(_queue);

    private static void CancelActivities(Battle b)
    {
        b.Cts.Cancel();
        b.Cts = new CancellationTokenSource();
    }

    private void SetPhase(BattlePhase phase)
    {
        if (_phase == phase) return;
        _s.Journal.Info(Area, $"phase {_phase} -> {phase}{(_active is null ? "" : " (" + _active.Id + ")")}");
        _phase = phase;
        try { PhaseChanged?.Invoke(phase, _active?.Id); }
        catch (Exception e) { _s.Journal.Warn(Area, $"PhaseChanged handler: {e.Message}"); }
    }

    private void Notify(string title, string text, NoticeKind kind = NoticeKind.Info)
    {
        try { _s.Notifier.Notify(title, text, kind); }
        catch (Exception e) { _s.Journal.Warn(Area, $"notification failed: {e.Message}"); }
    }

    /// <summary>RES|AW (PROTOCOL §13.5): the award slot CK3 applied, decoded from A = rolls + 10 × glory/5 + 1000 × prestige/25.</summary>
    private static string DescribeAward(ResultTrace e)
    {
        static long? Number(IReadOnlyDictionary<string, string> values, string key) =>
            values.TryGetValue(key, out var raw) &&
            double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
                ? (long)Math.Round(v) : null;
        if (Number(e.Values, "ck3xbl_aw_tag") is not { } tag || Number(e.Values, "ck3xbl_aw_value") is not { } value)
            return "award slot without tag or value";
        return $"character {tag}: +{value / 1000 * 25} prestige, +{value % 1000 / 10 * 5} glory (if acclaimed), {value % 10} prowess roll(s)";
    }

    /// <summary>A side counts as CK3's favourite when its CK3 strength is this much larger; closer battles are a toss-up.</summary>
    internal const double VerdictMargin = 1.1;

    /// <summary>
    /// How CK3 would most likely have decided the battle, from the CK3 strengths of the balance record (PROTOCOL §17: (men x damage) x
    /// (men x toughness); commander advantage and terrain are not in it), against Bannerlord's winner. Agrees is null for a toss-up or a
    /// battle without a winner. Null without a balance record (single combat).
    /// </summary>
    internal static (string Text, bool? Agrees)? Ck3Verdict(BalanceInfo? balance, string? winner)
    {
        if (balance is null || balance.Ck3Attacker <= 0 || balance.Ck3Defender <= 0) return null;
        double ratio = balance.Ck3Attacker / balance.Ck3Defender;
        string? favourite = ratio >= VerdictMargin ? "attacker" : ratio <= 1 / VerdictMargin ? "defender" : null;
        string odds = FormattableString.Invariant($"{Math.Max(ratio, 1 / ratio):0.##}:1");
        bool decided = winner is "attacker" or "defender";
        if (favourite is null) return ($"CK3 check: a toss-up in CK3 ({odds}), Bannerlord winner {winner ?? "none"}", null);
        if (!decided) return ($"CK3 check: CK3 favours the {favourite} ({odds}), Bannerlord had no winner", null);
        bool agrees = winner == favourite;
        return ($"CK3 check: CK3 favours the {favourite} ({odds}), Bannerlord winner {winner} -> {(agrees ? "same" : "DIFFERENT")}", agrees);
    }

    internal static TitleCard TitleCardFor(Battle b, nint freezeFrom)
    {
        var p = b.Request?.Presentation;
        bool attacker = b.Request?.PlayerSide == "attacker";
        return new TitleCard
        {
            Title = p?.Title ?? (b.Snapshot.CombatName.Length > 0 ? b.Snapshot.CombatName : Words.For(p?.Language).BattleOf(b.Snapshot.Location.ProvinceName)),
            Subtitle = p?.Subtitle ?? "",
            Detail = p?.Detail ?? "",
            LeftCoaPng = attacker ? p?.AttackerCoaPng : p?.DefenderCoaPng,
            RightCoaPng = attacker ? p?.DefenderCoaPng : p?.AttackerCoaPng,
            FreezeFrameFrom = freezeFrom,
        };
    }

    internal static TitleCard ResultCardFor(Battle b, Ck3Outcome o)
    {
        var w = Words.For(b.Request?.Presentation?.Language);
        string place = b.Snapshot.Location.ProvinceName.Length > 0 ? b.Snapshot.Location.ProvinceName : b.Snapshot.CombatName;
        var lines = o.Summary.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var card = TitleCardFor(b, 0);
        card.Title = o.OutcomeCode == 3 ? w.CardBrokenOff(place) : o.PlayerWon ? w.CardVictory(place) : w.CardDefeat(place);
        card.Subtitle = lines.Length > 0 ? lines[0] : w.CardLosses(o.MyLost, o.EnemyLost);
        // The outcome reaches CK3 when the player clicks the option of its event 0002; the card names it as the mod does. With the
        // battle clock CK3 then fights the result out over the next days, which only happens while the game runs.
        card.Detail = string.Join("  ·  ", lines.Skip(1).Append(o.PayloadVersion == KeypadLayout.SteerPayloadVersion
            ? w.BattleContinues
            : w.ClickInCk3));
        return card;
    }

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        _inbox.Writer.TryComplete();
        foreach (var t in new[] { _loop, _ticker })
            if (t is not null) await t.ConfigureAwait(false);
        // Best effort: never leave the player behind an overlay or with a game muted.
        try
        {
            if (TakeOverlay()) await _s.Ux.HideAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception e) { _s.Journal.Warn(Area, $"shutdown cleanup (overlay): {e.Message}"); }
        int ck3Pid, bannerlordPid;
        lock (_uxGate)
        {
            (ck3Pid, bannerlordPid) = (_ck3MutedPid, _bannerlordMutedPid);
            (_ck3MutedPid, _bannerlordMutedPid) = (0, 0);
        }
        // Bannerlord stays muted between battles; its level goes back before the host quits it, or its next start would be silent.
        foreach (int pid in new[] { ck3Pid, bannerlordPid })
        {
            if (pid == 0) continue;
            try
            {
                await _s.Ux.RestoreProcessVolumeAsync(pid, TimeSpan.Zero, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception e) { _s.Journal.Warn(Area, $"shutdown cleanup (volume of process {pid}): {e.Message}"); }
        }
        _life.Dispose();
    }

    // =========================================================================================================== types

    internal sealed class Battle(BattleSnapshot snapshot)
    {
        public readonly BattleSnapshot Snapshot = snapshot;
        public string Id = snapshot.RequestId;
        public int Bid => Snapshot.Bid;
        /// <summary>The snapshot the request and the outcome are built from: for a reinforcement clash the live snapshot capped at the
        /// parent's end state (IBattleMapper.BuildContinuation), else <see cref="Snapshot"/>. Set on the mailbox after preparing.</summary>
        public BattleSnapshot Planned = snapshot;
        /// <summary>The clash this one continues (PROTOCOL §14.6); null for a first clash, or when the parent is unknown.</summary>
        public ParentLink? Parent;
        /// <summary>ST records that arrived while the battle was still being delivered (run file: before its RES|END), in log order;
        /// Detach replays them.</summary>
        public readonly List<SteeringTrace> EarlySteering = new();
        public BattleRequest? Request;
        /// <summary>The request was handed to the current Bannerlord process (or will be taken by the next one). Mailbox only.</summary>
        public bool Preloaded;
        /// <summary>The offer timed out and the preload was cancelled on purpose: reload only when the player chooses to fight.</summary>
        public bool PreloadDropped;
        public Ck3Choice? Choice;
        /// <summary>How a relief battle is fought (PROTOCOL §40): "" the field, "defend", "camp". Mailbox only.</summary>
        public string Mode = "";
        public DateTime OfferedAt, WaitingSince, EnteringSince, AbortRequestedAt;
        public bool AbortRequested;
        /// <summary>The enter activity sent 'fg' and 'go'.</summary>
        public bool EnterSent;
        /// <summary>The bridge title card was on screen when entering (ITransitionUx.TitleCardPresented); set before 'go'.</summary>
        public bool TitleCardPresented = true;
        public int Reentries;
        public BattleResult? Result;
        public Ck3Outcome? Outcome;
        public int DeliveryAttempts;
        /// <summary>Since when delivery has been waiting for the player's keyboard and mouse (null = not waiting).</summary>
        public DateTime? InputBusySince;
        /// <summary>When the keypad probe last found the battle window closed (null = not waiting for it), how often it did, and
        /// whether the player has left CK3 since.</summary>
        public DateTime? KeypadClosedAt;
        public int KeypadProbes;
        public bool LeftCk3SinceProbe;
        public int ActivityId;
        public CancellationTokenSource Cts = new();
    }

    /// <summary>A delivered version 4 battle CK3 is fighting out (PROTOCOL §14.7). Mailbox only.</summary>
    internal sealed class SteeringBattle(Battle b, DateTime deliveredAt)
    {
        public readonly int Bid = b.Bid;
        public readonly string Id = b.Id;
        public readonly BattleSnapshot Snapshot = b.Snapshot;
        public readonly BattleSnapshot Planned = b.Planned;
        public readonly BattleRequest Request = b.Request!;
        public readonly Ck3Outcome Outcome = b.Outcome!;
        /// <summary>The steering bid this clash continues (0 = first clash).</summary>
        public readonly int ParentBid = b.Snapshot.ParentBid;
        public readonly int Clash = b.Snapshot.Clash;
        /// <summary>Characters captured or killed in this or an earlier clash: left out of the next one.</summary>
        public readonly ISet<int> FallenTags = Fallen(b.Outcome!, b.Parent?.FallenTags ?? (IEnumerable<int>)Array.Empty<int>());
        /// <summary>The first clash's planned snapshot (ParentLink.Root of the next clash); null when this is the first clash.</summary>
        public readonly BattleSnapshot? Root = b.Parent is null ? null : b.Parent.Root ?? b.Parent.Planned;
        public readonly DateTime DeliveredAt = deliveredAt;
        public SteeringState State = SteeringState.Awaiting;
        public int PulseCount;
        public DateTime LastPulseAt = deliveredAt;
        /// <summary>CK3 running time without an ST|P (stall warning), and whether that warning or the "never clicked" one was given.</summary>
        public TimeSpan UnpausedSincePulse;
        public bool StallWarned, AwaitWarned;
        // The latest numbers CK3 logged (ST|BEGIN, ST|P, ST|WIN, ST|HANDBACK).
        public int? CombatDay, AttackerKilled, DefenderKilled, AttackerTarget, DefenderTarget;
        private readonly List<SteeringRecord> _records = new();

        public string BattleName => Snapshot.CombatName.Length > 0 ? Snapshot.CombatName : $"The battle at {Snapshot.Location.ProvinceName}";

        public void Record(SteeringTrace e)
        {
            _records.Add(new SteeringRecord(e.Stage, e.LogTime, e.Values));
            CombatDay = e.CombatDay ?? CombatDay;
            AttackerKilled = e.AttackerKilled ?? AttackerKilled;
            DefenderKilled = e.DefenderKilled ?? DefenderKilled;
            AttackerTarget = e.AttackerTarget ?? AttackerTarget;
            DefenderTarget = e.DefenderTarget ?? DefenderTarget;
        }

        /// <summary>The numbers from the player's side: enemy dead counted and its target, our dead counted and its target. Without a
        /// player side (the player is only a liege), the attacker's view. CK3's sides, also for a request whose sides were swapped
        /// to put the player on the walls (PROTOCOL §40).</summary>
        public (int? EnemyFallen, int? EnemyTarget, int? OurFallen, int? OurTarget) PlayerView() =>
            (Request.PlayerSide == "defender") != (Request.Siege?.SidesSwapped == true)
            ? (DefenderKilled, DefenderTarget, AttackerKilled, AttackerTarget)
            : (AttackerKilled, AttackerTarget, DefenderKilled, DefenderTarget);

        public SteeringArchive ToArchive(string closedBecause, DateTime closedAt) => new(Bid, ParentBid, Clash, State, closedBecause, DeliveredAt,
            closedAt, PulseCount, AttackerKilled, AttackerTarget, DefenderKilled, DefenderTarget, _records.ToList());
    }

    /// <summary>One ST record as archived (steering.json, pulse.jsonl).</summary>
    internal sealed record SteeringRecord(string Stage, TimeSpan LogTime, IReadOnlyDictionary<string, string> Values);

    /// <summary>steering.json: how a steered battle went, written when it closes.</summary>
    internal sealed record SteeringArchive(int Bid, int ParentBid, int Clash, SteeringState State, string ClosedBecause, DateTime DeliveredAt,
        DateTime ClosedAt, int Pulses, int? AttackerKilled, int? AttackerTarget, int? DefenderKilled, int? DefenderTarget, List<SteeringRecord> Records);

    /// <summary>The result of the prepare activity.</summary>
    private sealed record Prepared(BattleRequest Request, BattleSnapshot Planned, string Mode);

    private enum DeliveryResult { Delivered, FocusLost, InputBusy, KeysHeld, KeypadClosed, Failed }

    private abstract record Input;
    private sealed record EventInput(Ck3Event Event) : Input;
    private sealed record StatusInput(BannerlordStatus Status) : Input;
    private sealed record LostInput(BannerlordLoss Kind, string Reason) : Input;
    private sealed record ResultInput(BattleResult Result) : Input;
    private sealed record HotkeyInput : Input;
    private sealed record TickInput : Input { public static readonly TickInput Instance = new(); }
    private sealed record ActivityDone(Battle Battle, int Id, string Name, object? Result, Exception? Error) : Input;
}
