using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CK3xBL.Ux.Audio;

/// <summary>
/// Ramps a process's audio sessions down and back up while remembering the user's own mixer level (CK3 sits at 0.20 on this
/// PC, so "restore" must never mean 1.0).
///
/// Levels are given as a fraction of the user's level: 0 = silent, 1 = the user's level; nothing is ever made louder than
/// the user had it. The remembered levels are persisted to <c>%LOCALAPPDATA%\ck3xbl\audio.json</c> before any change,
/// because Windows stores per-app volume and gives it to the app's next instance: a bridge that died mid-battle would
/// otherwise leave CK3 silent for good. <see cref="RecoverFromPreviousRun"/> repairs that at the next start.
///
/// Pending levels: a level that cannot be put back now (the process exited while lowered, e.g. Bannerlord muted between
/// battles and then quit, or it has no audio session at the moment) becomes a pending entry, keyed by process name and
/// persisted. It is applied as soon as a process of that name has an audio session again: a watch checks every 2 s while
/// anything is remembered or pending, and <see cref="CheckSoon"/> checks at once when a game process is reported.
///
/// A level is only put back if it is still one our ramps could have left behind (at or below the highest level we last
/// set, plus a small tolerance); if the user has turned the game up in the mixer meanwhile, their choice wins.
/// Thread-safe. COM work runs on thread-pool (MTA) threads.
/// </summary>
public sealed class ProcessVolumeController : IDisposable
{
    private const float Tolerance = 0.02f;
    private const int StepMs = 15;
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(2);

    private sealed class SessionMemory
    {
        public float User { get; set; }
        public float LastSet { get; set; }
    }

    private sealed class Entry
    {
        public string ProcessName { get; set; } = "";
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        /// <summary>Level for sessions not seen when the levels were remembered (sessions appear lazily).</summary>
        public float UserLevel { get; set; }
        /// <summary>The highest level any of our ramps could have left on any session.</summary>
        public float LastSetLevel { get; set; }
        public Dictionary<string, SessionMemory> Sessions { get; set; } = new();
        public DateTime SavedUtc { get; set; }
    }

    private sealed class StoreFile
    {
        public int Version { get; set; } = 1;
        public List<Entry> Entries { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private readonly string _storePath;
    private readonly object _gate = new();
    private readonly Dictionary<int, Entry> _active = new();          // remembered levels of running processes, by pid
    private readonly List<Entry> _pending = new();                    // to put back once that game has a session again (one per name)
    private readonly Dictionary<int, CancellationTokenSource> _ramps = new();
    private readonly object _checkGate = new();                       // one pending check at a time
    private readonly System.Threading.Timer _watch;
    private bool _watchRunning, _disposed;

    /// <summary>%LOCALAPPDATA%\ck3xbl\audio.json</summary>
    public static string DefaultStorePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ck3xbl", "audio.json");

    public ProcessVolumeController(string storePath)
    {
        _storePath = storePath;
        _watch = new System.Threading.Timer(_ => CheckPendingIfIdle(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>True while the user's level of <paramref name="processId"/> is remembered (i.e. we may have lowered it).</summary>
    public bool IsRemembered(int processId) { lock (_gate) return _active.ContainsKey(processId); }

    /// <summary>Number of pending entries (diagnostics and tests).</summary>
    internal int PendingCount { get { lock (_gate) return _pending.Count; } }

    /// <summary>
    /// Ramps every session of the process to <paramref name="fraction"/> × the user's level over <paramref name="duration"/>
    /// (square-law curve, see <see cref="PerceptualLerp"/>). The first call for a process remembers the user's level. A newer ramp of the same process
    /// supersedes this one (which then returns quietly); cancelling <paramref name="ct"/> stops where it is and throws.
    /// </summary>
    public Task RampAsync(int processId, float fraction, TimeSpan duration, CancellationToken ct) =>
        Task.Run(() => RunRampAsync(processId, Math.Clamp(fraction, 0f, 1f), duration, restore: false, ct), CancellationToken.None);

    /// <summary>
    /// Ramps back to the user's level and forgets it. A no-op when nothing was remembered for the process. If the process has
    /// exited or has no audio session right now, the level becomes pending (see the class remarks).
    /// </summary>
    public Task RestoreAsync(int processId, TimeSpan duration, CancellationToken ct) =>
        Task.Run(() => RunRampAsync(processId, 1f, duration, restore: true, ct), CancellationToken.None);

    /// <summary>
    /// Restores every remembered level at once, without ramps (exit and crash handlers). Levels of processes that have exited
    /// or have no session stay pending in the store for the next start. Never throws.
    /// </summary>
    public void RestoreAllNow()
    {
        List<Entry> entries;
        lock (_gate)
        {
            // Ramp steps check their token under the same gate: none of them can set a level after this.
            foreach (var cts in _ramps.Values) cts.Cancel();
            entries = _active.Values.ToList();
        }
        foreach (var entry in entries)
        {
            try
            {
                if (!IsRunning(entry)) MoveToPending(entry.ProcessId, "it has exited");
                else if (ApplyRestore(entry.ProcessId, entry, sameRun: true)) Forget(entry.ProcessId);
                else MoveToPending(entry.ProcessId, "it has no audio session");
            }
            catch (Exception e) { UxLog.Write($"audio: restoring pid {entry.ProcessId} failed: {e.Message}"); }
        }
    }

    /// <summary>
    /// At bridge start: puts back levels a previous bridge run left lowered (it crashed or was killed mid-battle, or quit while
    /// a game was muted). Entries whose game is not running (or has no session yet) stay pending. Returns the number of
    /// entries applied. Never throws.
    /// </summary>
    public int RecoverFromPreviousRun()
    {
        StoreFile? file;
        try
        {
            if (!File.Exists(_storePath)) return 0;
            file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_storePath), Json);
        }
        catch (Exception e)
        {
            UxLog.Write($"audio: cannot read '{_storePath}': {e.Message}");
            return 0;
        }
        var entries = file?.Entries ?? [];
        lock (_gate)
        {
            foreach (var entry in entries) AddPendingLocked(entry);
            CommitLocked();
        }
        int repaired;
        lock (_checkGate) repaired = CheckPendingCore();
        if (entries.Count > 0)
            UxLog.Write($"audio: recovery from a previous run repaired {repaired} process(es), {PendingCount} pending");
        return repaired;
    }

    /// <summary>
    /// Checks the pending levels now (on a thread-pool thread) instead of at the next watch tick, e.g. because a game process
    /// was just started. Cheap when nothing is remembered.
    /// </summary>
    public void CheckSoon()
    {
        lock (_gate)
        {
            if (_disposed || (_pending.Count == 0 && _active.Count == 0)) return;
        }
        ThreadPool.QueueUserWorkItem(_ => CheckPendingIfIdle());
    }

    /// <summary>Applies the pending levels now and returns how many were applied (tests; the watch does this by itself).</summary>
    internal int CheckPendingNow()
    {
        lock (_checkGate) return CheckPendingCore();
    }

    /// <summary>Stops the watch. Remembered and pending levels stay in the store; call <see cref="RestoreAllNow"/> first.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _watchRunning = false;
        }
        _watch.Dispose();
    }

    /// <summary>
    /// Interpolates the (linear amplitude) session volume on a square-law curve. Loudness grows roughly with amplitude^0.6,
    /// so a linear amplitude ramp sounds as if it only drops at the very end; the square law spreads the change evenly
    /// enough without the abrupt start of a pure dB ramp.
    /// </summary>
    internal static float PerceptualLerp(float from, float to, double k)
    {
        double a = Math.Sqrt(Math.Max(0, from)), b = Math.Sqrt(Math.Max(0, to));
        double v = a + (b - a) * Math.Clamp(k, 0, 1);
        return (float)(v * v);
    }

    /// <summary>Self-test seam: remembers a level for a process without touching any audio session.</summary>
    internal void RememberForTest(int pid, string processName, long startUtcTicks, float userLevel, float lastSetLevel)
    {
        lock (_gate)
        {
            _active[pid] = new Entry
            {
                ProcessName = processName, ProcessId = pid, ProcessStartUtcTicks = startUtcTicks,
                UserLevel = userLevel, LastSetLevel = lastSetLevel, SavedUtc = DateTime.UtcNow,
            };
            CommitLocked();
        }
    }

    // ------------------------------------------------------------------------------------------------ internals

    private async Task RunRampAsync(int pid, float fraction, TimeSpan duration, bool restore, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CancellationTokenSource mine;
        lock (_gate)
        {
            if (_ramps.Remove(pid, out var previous)) previous.Cancel();
            mine = _ramps[pid] = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }
        var sessions = AudioSessions.OpenForProcess(pid);
        try
        {
            Entry? entry;
            lock (_gate) _active.TryGetValue(pid, out entry);
            if (restore && entry == null) return;
            if (restore && !IsRunning(entry!))
            {
                MoveToPending(pid, "it has exited");     // the pid may already belong to another process: never touch that one
                return;
            }
            if (sessions.Count == 0)
            {
                if (restore) MoveToPending(pid, "it has no audio session");
                else UxLog.Write($"audio: pid {pid} has no audio session yet; nothing to ramp");
                return;
            }
            entry ??= Remember(pid, sessions);

            // Plan every session: from its current level to fraction x the user's level.
            var plan = new List<(ProcessSession session, float from, float to)>();
            lock (_gate)
            {
                foreach (var s in sessions)
                {
                    float current = s.GetLevel() ?? 0f;
                    var memory = MemoryFor(entry, s.InstanceId, current);
                    // On restore, a session the user turned up meanwhile keeps the user's new level.
                    if (restore && current > memory.LastSet + Tolerance) continue;
                    float to = memory.User * fraction;
                    plan.Add((s, current, to));
                    memory.LastSet = Math.Max(current, to);        // crash mid-ramp: anything up to here is ours
                }
                entry.LastSetLevel = Math.Max(entry.LastSetLevel, plan.Count > 0 ? plan.Max(p => Math.Max(p.from, p.to)) : 0f);
                CommitLocked();
            }

            var clock = Stopwatch.StartNew();
            double total = Math.Max(0, duration.TotalMilliseconds);
            while (clock.Elapsed.TotalMilliseconds < total)
            {
                double k = clock.Elapsed.TotalMilliseconds / total;
                ApplyStep(plan, -(Math.Cos(Math.PI * k) - 1) / 2, mine.Token);
                await Task.Delay(StepMs, mine.Token).ConfigureAwait(false);
            }
            ApplyStep(plan, 1, mine.Token);

            if (restore) Forget(pid);
            else
            {
                lock (_gate)
                {
                    foreach (var (session, _, to) in plan) MemoryFor(entry, session.InstanceId, to).LastSet = to;
                    CommitLocked();
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Superseded by a newer ramp of the same process (that one takes it from here), or stopped by RestoreAllNow.
        }
        finally
        {
            foreach (var s in sessions) s.Dispose();
            lock (_gate)
            {
                if (_ramps.TryGetValue(pid, out var current) && current == mine) _ramps.Remove(pid);
            }
            mine.Dispose();
        }
    }

    /// <summary>
    /// Sets one step of the ramp (<paramref name="eased"/> 1 = exactly the targets). Runs under the gate and checks the token
    /// there, so a ramp that was superseded or stopped can never set a level after its successor or RestoreAllNow did.
    /// </summary>
    private void ApplyStep(List<(ProcessSession session, float from, float to)> plan, double eased, CancellationToken token)
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            foreach (var (session, from, to) in plan) session.SetLevel(eased >= 1 ? to : PerceptualLerp(from, to, eased));
        }
    }

    /// <summary>
    /// Remembers the user's levels for a process seen for the first time, adopting the pending entry of an earlier instance
    /// of the same game (one that exited while lowered, in this run or a previous one).
    /// </summary>
    private Entry Remember(int pid, List<ProcessSession> sessions)
    {
        string name = AudioSessions.ProcessNameOf(pid);
        long start = 0;
        try { using var p = Process.GetProcessById(pid); start = StartTicks(p); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }
        SweepExited();   // an earlier instance that exited while lowered becomes pending here, so it is adopted below

        lock (_gate)
        {
            if (_active.TryGetValue(pid, out var existing)) return existing;
            var earlier = _pending.FirstOrDefault(e => string.Equals(e.ProcessName, name, StringComparison.OrdinalIgnoreCase));
            var entry = new Entry { ProcessName = name, ProcessId = pid, ProcessStartUtcTicks = start, SavedUtc = DateTime.UtcNow };
            float loudest = 0f;
            foreach (var s in sessions)
            {
                float current = s.GetLevel() ?? 0f;
                // The game was restarted while lowered: Windows gave it the level we had lowered it to.
                float user = earlier != null && current <= earlier.LastSetLevel + Tolerance
                    ? Math.Max(current, earlier.UserLevel)
                    : current;
                entry.Sessions[s.InstanceId] = new SessionMemory { User = user, LastSet = current };
                if (s.State == AudioSessionState.Active || loudest == 0f) loudest = Math.Max(loudest, user);
            }
            entry.UserLevel = loudest;
            entry.LastSetLevel = entry.Sessions.Values.Select(m => m.LastSet).DefaultIfEmpty(0f).Max();
            if (earlier != null) _pending.Remove(earlier);
            _active[pid] = entry;
            UxLog.Write($"audio: remembered {name} (pid {pid}) user level {entry.UserLevel:F2} over {sessions.Count} session(s)" +
                        (earlier != null ? " (adopted a pending level)" : ""));
            return entry;
        }
    }

    /// <summary>The memory of one session; sessions that appeared after the levels were remembered get the process default.</summary>
    private static SessionMemory MemoryFor(Entry entry, string instanceId, float current)
    {
        if (!entry.Sessions.TryGetValue(instanceId, out var memory))
            entry.Sessions[instanceId] = memory = new SessionMemory { User = entry.UserLevel, LastSet = Math.Max(current, entry.LastSetLevel) };
        return memory;
    }

    /// <summary>Sets sessions back to the remembered user level where they are still at a level we set. True if any session exists.</summary>
    private static bool ApplyRestore(int pid, Entry entry, bool sameRun)
    {
        var sessions = AudioSessions.OpenForProcess(pid);
        try
        {
            int changed = 0;
            foreach (var s in sessions)
            {
                float current = s.GetLevel() ?? 0f;
                SessionMemory? memory = null;
                if (sameRun) entry.Sessions.TryGetValue(s.InstanceId, out memory);
                float user = memory?.User ?? entry.UserLevel;
                float lastSet = memory?.LastSet ?? entry.LastSetLevel;
                if (current <= lastSet + Tolerance && current < user && s.SetLevel(user)) changed++;
            }
            if (changed > 0)
                UxLog.Write($"audio: put {entry.ProcessName} (pid {pid}) back to {entry.UserLevel:F2} on {changed} session(s)");
            return sessions.Count > 0;
        }
        finally { foreach (var s in sessions) s.Dispose(); }
    }

    /// <summary>
    /// One pass of the watch: remembered processes that have exited become pending, then every pending entry is applied to the
    /// running processes of its name. Returns how many pending entries were dealt with. Never throws.
    /// </summary>
    private int CheckPendingCore()
    {
        int applied = 0;
        try
        {
            SweepExited();
            List<Entry> pending;
            lock (_gate) pending = _pending.ToList();
            foreach (var entry in pending)
            {
                try
                {
                    if (!ApplyPendingEntry(entry)) continue;
                    applied++;
                    lock (_gate)
                    {
                        _pending.Remove(entry);
                        CommitLocked();
                    }
                }
                catch (Exception e) { UxLog.Write($"audio: applying the pending level of {entry.ProcessName} failed: {e.Message}"); }
            }
        }
        catch (Exception e) { UxLog.Write($"audio: pending check failed: {e.Message}"); }
        return applied;
    }

    /// <summary>The watch tick: skipped when a check is already running.</summary>
    private void CheckPendingIfIdle()
    {
        if (!Monitor.TryEnter(_checkGate)) return;
        try { CheckPendingCore(); }
        finally { Monitor.Exit(_checkGate); }
    }

    /// <summary>
    /// Applies a pending entry to every running process of its name. True when it has been dealt with: some process had an
    /// audio session to apply it to, or it was folded into the memory of a process we are managing right now.
    /// </summary>
    private bool ApplyPendingEntry(Entry entry)
    {
        bool handled = false;
        foreach (var (pid, start) in RunningProcesses(entry.ProcessName))
        {
            lock (_gate)
            {
                if (_active.TryGetValue(pid, out var managed))
                {
                    AdoptLocked(managed, entry);
                    handled = true;
                    continue;
                }
                if (_ramps.ContainsKey(pid)) continue;   // a ramp is starting: it adopts the entry when it remembers the process
            }
            bool sameRun = pid == entry.ProcessId && start == entry.ProcessStartUtcTicks;
            handled |= ApplyRestore(pid, entry, sameRun);
        }
        return handled;
    }

    /// <summary>
    /// Folds a pending entry into the memory of a process we are already managing, the way <see cref="Remember"/> adopts one:
    /// a remembered level that our earlier lowering explains (at or below the pending last-set level) becomes the user's level.
    /// </summary>
    private static void AdoptLocked(Entry managed, Entry pending)
    {
        foreach (var memory in managed.Sessions.Values)
            if (memory.User <= pending.LastSetLevel + Tolerance) memory.User = Math.Max(memory.User, pending.UserLevel);
        if (managed.UserLevel <= pending.LastSetLevel + Tolerance) managed.UserLevel = Math.Max(managed.UserLevel, pending.UserLevel);
        UxLog.Write($"audio: folded the pending level of {pending.ProcessName} into pid {managed.ProcessId} (user level {managed.UserLevel:F2})");
    }

    /// <summary>Remembered processes that have exited become pending entries (their next instance inherits the lowered level).</summary>
    private void SweepExited()
    {
        List<Entry> active;
        lock (_gate) active = _active.Values.ToList();
        foreach (var entry in active)
            if (!IsRunning(entry)) MoveToPending(entry.ProcessId, "it has exited");
    }

    /// <summary>Turns the active entry of <paramref name="pid"/> into a pending one and persists it.</summary>
    private void MoveToPending(int pid, string why)
    {
        lock (_gate)
        {
            if (!_active.Remove(pid, out var entry)) return;
            // The next sessions inherit at most the highest level a session was left at; that bounds what is "ours" to restore.
            if (entry.Sessions.Count > 0) entry.LastSetLevel = entry.Sessions.Values.Max(m => m.LastSet);
            AddPendingLocked(entry);
            CommitLocked();
            UxLog.Write($"audio: cannot put {entry.ProcessName} (pid {pid}) back now ({why}); its level {entry.UserLevel:F2} is pending");
        }
    }

    /// <summary>Adds a pending entry, merging it with one of the same game (the higher user level and last-set level win).</summary>
    private void AddPendingLocked(Entry entry)
    {
        var same = _pending.FirstOrDefault(p => string.Equals(p.ProcessName, entry.ProcessName, StringComparison.OrdinalIgnoreCase));
        if (same != null)
        {
            entry.UserLevel = Math.Max(entry.UserLevel, same.UserLevel);
            entry.LastSetLevel = Math.Max(entry.LastSetLevel, same.LastSetLevel);
            _pending.Remove(same);
        }
        entry.SavedUtc = DateTime.UtcNow;
        _pending.Add(entry);
    }

    private void Forget(int pid)
    {
        lock (_gate)
        {
            if (_active.Remove(pid)) CommitLocked();
        }
    }

    /// <summary>True while the remembered process itself still runs (not a later process that reuses its pid).</summary>
    private static bool IsRunning(Entry entry)
    {
        try
        {
            using var p = Process.GetProcessById(entry.ProcessId);
            // HasExited also catches a process that has exited while someone still holds a handle to it.
            return !p.HasExited && (entry.ProcessStartUtcTicks == 0 || StartTicks(p) == entry.ProcessStartUtcTicks);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; }   // cannot be inspected: assume it still runs (the restore path copes)
    }

    /// <summary>Pid and start time of every running process with this name.</summary>
    private static List<(int pid, long start)> RunningProcesses(string name)
    {
        var result = new List<(int, long)>();
        Process[] found;
        try { found = Process.GetProcessesByName(name); }
        catch (InvalidOperationException) { return result; }
        foreach (var p in found)
        {
            using (p)
            {
                try { if (!p.HasExited) result.Add((p.Id, StartTicks(p))); }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
            }
        }
        return result;
    }

    private static long StartTicks(Process p)
    {
        try { return p.StartTime.ToUniversalTime().Ticks; }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { return 0; }
    }

    /// <summary>
    /// Writes active + pending entries atomically (temp file + rename), deleting the file when there is nothing to keep, and
    /// runs the watch exactly while there is something to watch.
    /// </summary>
    private void CommitLocked()
    {
        bool watch = !_disposed && (_active.Count > 0 || _pending.Count > 0);
        if (watch != _watchRunning)
        {
            _watchRunning = watch;
            var period = watch ? WatchInterval : Timeout.InfiniteTimeSpan;
            _watch.Change(period, period);
        }
        try
        {
            var file = new StoreFile { Entries = _active.Values.Concat(_pending).ToList() };
            if (file.Entries.Count == 0)
            {
                if (File.Exists(_storePath)) File.Delete(_storePath);
                return;
            }
            foreach (var e in _active.Values) e.SavedUtc = DateTime.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            string tmp = _storePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json), new UTF8Encoding(false));
            File.Move(tmp, _storePath, overwrite: true);
        }
        catch (Exception e) { UxLog.Write($"audio: cannot write '{_storePath}': {e.Message}"); }
    }
}
