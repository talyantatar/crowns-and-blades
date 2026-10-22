// CK3's pause state from logs/dedicated_server.log: "[18:06:58][I][jominiapplication.cpp:261]: Pause Toggled! Paused: true, PlayerID: 0"
// (live tests). Diagnostic only: the bridge never presses SPACE; CK3 pauses itself through the auto_pause message filter
// (PROTOCOL §1.4).
//  * Attach (PROTOCOL §15, live test): what the file already holds is read silently, never replayed as
//    Changed events. Its last toggle is CK3's current state only while CK3 runs (CK3 recreates its logs at every launch, so the file of
//    a running CK3 is that session's); a file a closed CK3 left behind is history and the state stays unknown. Attached fires once,
//    with that state, when the follower first reaches the end of the file (at once when the file does not exist).
//  * After the attach every toggle is live and raises Changed. A recreated file (CK3 relaunched) resets the state to unknown and
//    raises Restarted; the new session's toggles are then live too.
using System.Text.RegularExpressions;
using CK3xBL.Core.Logs;

namespace CK3xBL.Core.Ck3;

public sealed class PauseStateReader : IDisposable
{
    private static readonly Regex PauseLine = new(@"Pause Toggled! Paused: (?<p>true|false)", RegexOptions.Compiled);
    private readonly LogFollower _follower;
    private readonly Func<bool>? _ck3Running;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _pollGate = new();
    private Task? _loop;
    private bool _attached;
    private volatile int _state = -1;     // -1 unknown, 0 running, 1 paused

    /// <param name="logPath">CK3's logs/dedicated_server.log.</param>
    /// <param name="ck3Running">Whether CK3 runs now, asked once at the attach; null = assume it does (the file's last toggle counts).</param>
    /// <param name="pollInterval">Default 250 ms.</param>
    public PauseStateReader(string logPath, Func<bool>? ck3Running = null, TimeSpan? pollInterval = null)
    {
        _follower = new LogFollower(logPath, startAtEnd: false);
        _ck3Running = ck3Running;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>True/false from the last "Pause Toggled!" line of the current CK3 session; null before any, while no CK3 runs at the
    /// attach, or right after a CK3 restart.</summary>
    public bool? IsPaused => _state switch { 0 => false, 1 => true, _ => null };

    /// <summary>True once the file's existing content has been read (see <see cref="Attached"/>).</summary>
    public bool IsAttached => _attached;

    /// <summary>A live toggle (after the attach).</summary>
    public event Action<bool>? Changed;

    /// <summary>Once, when the existing content has been read: the current state (null = unknown) and why, for one journal line.</summary>
    public event Action<bool?, string>? Attached;

    /// <summary>The file was recreated (CK3 relaunched): the state is unknown until the new session's first toggle.</summary>
    public event Action? Restarted;

    public void Start() => _loop ??= Task.Factory.StartNew(Run, TaskCreationOptions.LongRunning).Unwrap();

    private async Task Run()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { Poll(); }
            catch (Exception) { /* diagnostic only: a bad read must not stop the follower; the next poll retries */ }
            try { await Task.Delay(_pollInterval, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One read of the file (the background loop calls it; tests call it directly).</summary>
    internal void Poll()
    {
        lock (_pollGate)
        {
            var r = _follower.Poll();
            if (r.Restarted)
            {
                _state = -1;
                if (_attached) Restarted?.Invoke();
            }
            foreach (var line in r.Lines) Apply(line, notify: _attached);
            if (_attached || !_follower.AtEnd) return;

            _attached = true;
            string why;
            if (!File.Exists(_follower.Path)) why = "no dedicated_server.log yet";
            else if (_state != -1 && _ck3Running?.Invoke() == false)
            {
                _state = -1;
                why = "dedicated_server.log is from a closed CK3 session";
            }
            else why = _state == -1 ? "no pause toggle in this CK3 session yet" : "last toggle of the current CK3 session";
            Attached?.Invoke(IsPaused, why);
        }
    }

    /// <summary>Applies one log line; <paramref name="notify"/> = raise <see cref="Changed"/> when the state changes.</summary>
    internal void Apply(string line, bool notify = true)
    {
        var m = PauseLine.Match(line);
        if (!m.Success) return;
        bool paused = m.Groups["p"].Value == "true";
        int next = paused ? 1 : 0;
        if (_state == next) return;
        _state = next;
        if (notify) Changed?.Invoke(paused);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
