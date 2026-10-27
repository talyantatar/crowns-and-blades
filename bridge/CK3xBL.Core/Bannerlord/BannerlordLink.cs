// Owns the Bannerlord process and the file transport to the CK3Bridge module (PROTOCOL §3, §8.3).
//  * Launch (§3.1): bin/Win64_Shipping_Client/Bannerlord.exe /singleplayer _MODULES_*...*_MODULES_ with that folder as working dir;
//    once with the fallback module list if the first list never reaches a heartbeat (or the module reports an error while booting).
//  * Activation: the module stays passive unless the bridge started its Bannerlord: the launch sets CK3XBL_HOST (the bridge's pid)
//    in the game's environment and leaves a short-lived launch lease (bannerlord_launch.json) for a game a store launcher restarts.
//    A player who ticks CK3 Bridge in the Bannerlord launcher gets their own game, untouched.
//  * Adopt: a Bannerlord that is already running is taken over only when it proves it runs an active CK3Bridge: a fresh heartbeat
//    naming its pid (a passive module never writes one). Any other Bannerlord is the player's own game: the bridge never quits,
//    closes or kills it, does not start a second copy next to it, and reports the problem (Problem) until that game exits.
//  * Module check: no launch while <Bannerlord>/Modules/CK3Bridge is missing (Problem says so; tried again on the next start).
//    When neither module list boots, BootFailed tells the player once and nothing is relaunched until the next start.
//  * Safe-mode question after an unclean exit: answered No automatically while booting, only in bridge processes.
//  * Health: process exit => Lost(Exited); heartbeat older than HeartbeatTimeout (longer while booting/loading) => Lost(Unresponsive),
//    and StatusChanged is raised again when the heartbeat recovers. Bannerlord is killed only when its window has been hung for
//    longer than HungKill; otherwise the bridge waits or closes it gracefully.
//  * Files: request.json / command.txt are written atomically. The module takes command.txt whole (read, then delete), so a command
//    is never written over one it has not taken yet (SendCommandAsync). What a process had not taken dies with it.
using System.Diagnostics;
using CK3xBL.Core.Config;
using CK3xBL.Core.Diagnostics;
using CK3xBL.Core.Json;
using CK3xBL.Core.Native;
using CK3xBL.Protocol;

namespace CK3xBL.Core.Bannerlord;

public sealed class BannerlordLinkOptions
{
    public string GameDir = "";
    public string SharedDir = "";
    public List<string> Modules = new() { "Native", "SandBoxCore", "CustomBattle", "CK3Bridge" };
    public List<string> FallbackModules = new() { "Native", "SandBoxCore", "CustomBattle", "SandBox", "StoryMode", "CK3Bridge" };
    public bool KeepRunning = true;
    /// <summary>Add the armory modules found in Modules to both lists (PROTOCOL §21, <see cref="ArmoryModuleList"/>).</summary>
    public bool ArmoryModules = true;
    /// <summary>Load SandBox for the arenas of noble single combats (PROTOCOL §27, <see cref="BannerlordLink.ArenaModule"/>).</summary>
    public bool ArenaModule = true;
    /// <summary>With War Sails installed, load NavalDLC and CK3Bridge.Naval for battles at sea and landings (PROTOCOL §44).</summary>
    public bool NavalModules = true;
    /// <summary>The bridge's own folder, where the CK3Bridge.Naval module is shipped (<see cref="NavalModule.ShippedDir"/>).</summary>
    public string AppDir = AppContext.BaseDirectory;
    /// <summary>Free the RAM of a Bannerlord waiting hidden for the next battle (PROTOCOL §28).</summary>
    public bool TrimWhenIdle = true;
    public TimeSpan IdleTrimDelay = TimeSpan.FromSeconds(20);
    public TimeSpan IdleTrimInterval = TimeSpan.FromMinutes(5);
    /// <summary>Restart an idle Bannerlord whose committed memory has grown above this (MB; 0 = never): Bannerlord keeps the memory
    /// of the battles it has fought, and with CK3 beside it Windows runs out of commit for the next big battle (PROTOCOL §31).</summary>
    public long RecycleAboveMb = 8000;
    public TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    public TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(5);
    public TimeSpan LoadingHeartbeatTimeout = TimeSpan.FromSeconds(60);
    public TimeSpan BootTimeout = TimeSpan.FromSeconds(180);
    public TimeSpan HungKill = TimeSpan.FromSeconds(30);
    /// <summary>A booted Bannerlord silent this much longer than its heartbeat limit is killed (and relaunched), even with a window
    /// that still answers: its game thread is stuck in native code.</summary>
    public TimeSpan SilentKill = TimeSpan.FromSeconds(60);
    public TimeSpan RelaunchDelay = TimeSpan.FromSeconds(3);
    public TimeSpan QuitTimeout = TimeSpan.FromSeconds(10);
    public TimeSpan CloseTimeout = TimeSpan.FromSeconds(20);

    public string BinDir => Path.Combine(GameDir, "bin", "Win64_Shipping_Client");
    public string ExePath => Path.Combine(BinDir, "Bannerlord.exe");
}

public sealed class BannerlordLink : IBannerlordLink, IDisposable
{
    private const string GameProcessName = "Bannerlord";
    private const string WindowClass = "TWApplication";
    internal const string BridgeModule = "CK3Bridge";
    private const string ForeignGameProblem =
        "Bannerlord is already running, but not for Crowns & Blades (your own game?). Save and close it; the bridge then starts its own Bannerlord.";
    private const string ModuleMissingProblem =
        "The CK3Bridge module is not installed in Bannerlord (Modules\\CK3Bridge). Run the Crowns & Blades installer again.";

    /// <summary>Set in the game's environment at launch (value: the bridge's pid); the module stays passive without it (or a lease).</summary>
    public const string HostVariable = "CK3XBL_HOST";
    /// <summary>Launch lease in the shared folder: {"pid": bridge pid, "t": unix ms}. The module accepts a fresh one when its
    /// environment lacks <see cref="HostVariable"/> (a store launcher restarted the game). Removed at the first heartbeat.</summary>
    public const string LaunchLeaseFile = "bannerlord_launch.json";

    private readonly BannerlordLinkOptions _o;
    private readonly IJournal _journal;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly object _startLock = new();   // one start decision (adopt / launch / wait for a foreign game) at a time

    private Process? _process;
    private int _generation;                      // bumped per launched or adopted process; a command belongs to one process
    private int _foreignPid;                      // a Bannerlord without CK3Bridge: nothing is launched until it exits
    private string? _problem;
    private bool _moduleMissing;                  // Modules/CK3Bridge was missing at the last launch attempt
    private volatile bool _bootFailureReported;   // BootFailed raised; reset by the next heartbeat
    private DateTime _launchedAt;
    private volatile bool _usingFallback;
    private volatile bool _launchedWithArmory;    // this process loads the armory modules (PROTOCOL §21)
    /// <summary>The running game loads the war elephants (PROTOCOL §43): elephant regiments ride them.</summary>
    public bool LoadsElephants { get; private set; }
    /// <summary>The running game loads War Sails and CK3Bridge.Naval (PROTOCOL §44): landings and battles at sea are fought on ships.</summary>
    public bool LoadsNaval { get; private set; }
    private volatile bool _bootedOnce;            // this process sent at least one heartbeat
    private volatile bool _lostReported;
    private volatile bool _stopping;
    private DateTime? _hungSince;
    private DateTime? _relaunchAt;
    private BannerlordStatus? _status;
    private string? _lastStatusKey;
    private DateTime _resultStamp;
    private readonly HashSet<string> _requestIds = new(StringComparer.Ordinal);   // battles requested by this bridge run (under _gate)
    private Task? _watch;

    public BannerlordLink(BannerlordLinkOptions options, IJournal journal)
    {
        _o = options;
        _journal = journal;
        Directory.CreateDirectory(_o.SharedDir);
    }

    public string RequestPath => Path.Combine(_o.SharedDir, ProtocolInfo.RequestFile);
    public string ResultPath => Path.Combine(_o.SharedDir, ProtocolInfo.ResultFile);
    public string StatusPath => Path.Combine(_o.SharedDir, ProtocolInfo.StatusFile);
    public string CommandPath => Path.Combine(_o.SharedDir, ProtocolInfo.CommandFile);
    public string LaunchLeasePath => Path.Combine(_o.SharedDir, LaunchLeaseFile);

    public BannerlordStatus? Status { get { lock (_gate) return _status; } }
    public bool IsRunning { get { lock (_gate) return _process is { HasExited: false }; } }
    public int ProcessId { get { lock (_gate) return _process is { HasExited: false } p ? p.Id : 0; } }
    public string? Problem { get { lock (_gate) return _problem ?? (_moduleMissing ? ModuleMissingProblem : null); } }
    /// <summary>The last launch attempt found no CK3Bridge module in Bannerlord's Modules folder.</summary>
    public bool ModuleMissing { get { lock (_gate) return _moduleMissing; } }
    public nint ForegroundBeforeLaunch { get; private set; }

    public nint MainWindow
    {
        get
        {
            int pid = ProcessId;
            if (pid == 0) return 0;
            var hwnd = (nint)(Status?.Hwnd ?? 0);
            if (hwnd != 0 && Win32.IsWindow(hwnd) && Win32.ProcessIdOf(hwnd) == pid) return hwnd;
            var windows = Win32.TopLevelWindows(pid, WindowClass);
            return windows.Count > 0 ? windows[0] : 0;
        }
    }

    public event Action<BannerlordStatus>? StatusChanged;
    public event Action<BannerlordLoss, string>? Lost;
    public event Action<BattleResult>? ResultUpdated;
    /// <summary>Raised when <see cref="Problem"/> changes (null = resolved).</summary>
    public event Action<string?>? ProblemChanged;
    /// <summary>Raised once when Bannerlord could not boot the module with either module list (text for the player).</summary>
    public event Action<string>? BootFailed;

    public Task StartAsync(CancellationToken ct)
    {
        _stopping = false;
        EnsureStarted();
        _watch ??= Task.Run(WatchLoop);
        return Task.CompletedTask;
    }

    /// <summary>Bridge start, before anything else: removes request.json, command.txt and half-written copies left by an earlier run,
    /// which the module would otherwise take as new work, and a launch lease that would activate a game this run did not start.</summary>
    public void ResetTransport()
    {
        foreach (var path in new[] { RequestPath, CommandPath, RequestPath + ".tmp", CommandPath + ".tmp", LaunchLeasePath })
            if (File.Exists(path) && TryDelete(path)) _journal.Info("bannerlord", $"removed {Path.GetFileName(path)} left by an earlier run");
    }

    // ------------------------------------------------------------------------------------------------------------- process

    /// <summary>Adopts a running Bannerlord that runs CK3Bridge, or launches one, unless the player's own Bannerlord is running.</summary>
    private void EnsureStarted()
    {
        lock (_startLock)
        {
            if (IsRunning) return;
            var listed = Process.GetProcessesByName(GameProcessName);
            // Never the process that just exited: while Windows tears it down it is still listed, and its last heartbeat is fresh
            // (live 2026-10-18: a memory restart adopted the dying game, took its exit for an armory crash and dropped the armory).
            // It is waited out (the next tick looks again), neither adopted nor taken for the player's own game.
            var running = listed.Where(p => p.Id != _lastExitedPid && !Exited(p)).ToArray();
            bool dying = running.Length < listed.Length;
            Process? adopted = null;
            try
            {
                var now = DateTime.UtcNow;
                var status = ProtocolJson.TryRead<BannerlordStatus>(StatusPath);
                adopted = running.FirstOrDefault(p => ProvesBridge(p.Id, status, now));
                if (adopted is not null) Adopt(adopted);
                else if (running.Length > 0) SetForeign(running[0].Id);
            }
            finally
            {
                foreach (var p in listed) if (!ReferenceEquals(p, adopted)) p.Dispose();
            }
            if (running.Length > 0 || dying) return;
            SetForeign(0);
            Launch(_usingFallback);
        }
    }

    /// <summary>A Bannerlord process belongs to the bridge when a fresh heartbeat names it: only an active CK3Bridge writes one, and it
    /// is active only in a game the bridge started. CK3Bridge in the command line proves nothing (the player may tick the module).</summary>
    internal bool ProvesBridge(int pid, BannerlordStatus? status, DateTime now) =>
        status is not null && status.Pid == pid && now - HeartbeatTime(status) <= HeartbeatLimit(status);

    private static bool Exited(Process p)
    {
        try { return p.HasExited; }
        catch (InvalidOperationException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>The pid of the last Bannerlord that exited (see the adoption above).</summary>
    private volatile int _lastExitedPid;

    private void Adopt(Process p)
    {
        lock (_gate)
        {
            _process = p;
            _launchedWithArmory = false;   // an exit of a game the bridge did not start says nothing about the armory
            _generation++;
            _launchedAt = DateTime.UtcNow;
            _bootedOnce = false;
            _lostReported = false;
            _hungSince = null;
            _status = null;
            _lastStatusKey = null;
        }
        SetForeign(0);
        _journal.Info("bannerlord", $"adopted running Bannerlord pid {p.Id} (runs CK3Bridge)");
    }

    private void SetForeign(int pid)
    {
        string? problem;
        lock (_gate)
        {
            if (_foreignPid == pid) return;
            _foreignPid = pid;
            _problem = pid == 0 ? null : ForeignGameProblem;
            problem = _problem ?? (_moduleMissing ? ModuleMissingProblem : null);
        }
        if (pid != 0) _journal.Warn("bannerlord", $"pid {pid} is not running an active {BridgeModule} (no heartbeat): left alone, and no second Bannerlord is started next to it");
        else _journal.Info("bannerlord", "no Bannerlord without the bridge module is in the way any more");
        ProblemChanged?.Invoke(problem);
    }

    /// <summary>The module holding the arena scenes (PROTOCOL §27).</summary>
    public const string ArenaModule = "SandBox";

    /// <summary>The list with <paramref name="module"/> right before CK3Bridge (or at the end); unchanged when it is already there.</summary>
    public static List<string> WithModuleBeforeBridge(IReadOnlyList<string> modules, string module)
    {
        var list = modules.ToList();
        if (list.Any(x => string.Equals(x, module, StringComparison.OrdinalIgnoreCase))) return list;
        int bridge = list.FindIndex(x => string.Equals(x, BridgeModule, StringComparison.OrdinalIgnoreCase));
        list.Insert(bridge >= 0 ? bridge : list.Count, module);
        return list;
    }

    /// <summary>Item modules the costumes use when installed (PROTOCOL §21). Terra Armarium (Byzantine, Rus and Mongol, late medieval
    /// armour; its core first) and the war elephants the bridge builds from Burning Empires (§43) load the same way, in this order.</summary>
    public static readonly IReadOnlyList<string> ArmoryModules = new[]
    {
        "OpenSourceArmory", "OpenSourceSaddlery", "BL.Terra_Armarium.UniversalCore", "BL.Terra_Armarium.Byzantine", "BL.Terra_Armarium.Rus&Mongols",
        "BL.Terra_Armarium.LateMedieval", ElephantModule.ModuleId,
    };

    /// <summary>Modules the armory modules depend on (their SubModule.xml), loaded before them.</summary>
    private static readonly string[] ArmoryDependencies = { "SandBox", "StoryMode" };

    /// <summary>
    /// The launch list with the <paramref name="installed"/> armory modules: their dependencies after CustomBattle (or the last stock
    /// module), the armory modules right before CK3Bridge. The list is unchanged when none is installed.
    /// </summary>
    public static List<string> ArmoryModuleList(IReadOnlyList<string> modules, IReadOnlyList<string> installed)
    {
        var list = modules.ToList();
        if (installed.Count == 0) return list;
        bool Has(string m) => list.Any(x => string.Equals(x, m, StringComparison.OrdinalIgnoreCase));
        int bridge = list.FindIndex(x => string.Equals(x, BridgeModule, StringComparison.OrdinalIgnoreCase));
        int at = bridge >= 0 ? bridge : list.Count;
        foreach (var dep in ArmoryDependencies)
            if (!Has(dep)) list.Insert(at++, dep);
        foreach (var m in installed)
            if (!Has(m)) list.Insert(at++, m);
        return list;
    }

    /// <summary>Marker in the shared folder: the armory modules (by <see cref="ArmorySignature"/>) crashed Bannerlord before its first
    /// heartbeat, so they are left out until they change or the file is removed.</summary>
    public const string ArmoryFailureFile = "armory_failed.txt";

    private string ArmoryFailurePath => Path.Combine(_o.SharedDir, ArmoryFailureFile);

    private List<string> InstalledArmoryModules() =>
        ArmoryModules.Where(m => (ElephantModule.Complete || m != ElephantModule.ModuleId) && File.Exists(Path.Combine(_o.GameDir, "Modules", m, "SubModule.xml"))).ToList();

    /// <summary>The installed armory modules and the time their SubModule.xml was written: an update or a reinstall tries them again.</summary>
    private string ArmorySignature(IReadOnlyList<string> installed) =>
        string.Join(";", installed.Select(m =>
            m + "@" + File.GetLastWriteTimeUtc(Path.Combine(_o.GameDir, "Modules", m, "SubModule.xml")).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private string? ReadArmoryFailure()
    {
        try { return File.Exists(ArmoryFailurePath) ? File.ReadAllText(ArmoryFailurePath).Trim() : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private void WriteArmoryFailure(string signature)
    {
        try { AtomicFile.WriteAllText(ArmoryFailurePath, signature, withBom: false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _journal.Warn("bannerlord", $"{ArmoryFailureFile} not written: {e.Message}"); }
    }

    private void Launch(bool fallback)
    {
        lock (_startLock)   // StartAsync (tailer or UI thread), the watcher's relaunch and RestartAsync may race
        {
            if (IsRunning) return;
            lock (_gate) _relaunchAt = null;
            if (!File.Exists(_o.ExePath))
            {
                _journal.Error("bannerlord", $"Bannerlord.exe not found at {_o.ExePath}; set the Bannerlord folder (tray: Set game folders)");
                return;
            }
            if (!SetModuleMissing(!GameInstall.HasBannerlordModule(_o.GameDir))) return;
            var modules = fallback ? _o.FallbackModules : _o.Modules;
            // The arenas of noble single combats live in SandBox (PROTOCOL §27), which needs only Native and SandBoxCore.
            if (_o.ArenaModule && File.Exists(Path.Combine(_o.GameDir, "Modules", ArenaModule, "SubModule.xml")))
                modules = WithModuleBeforeBridge(modules, ArenaModule);
            bool withArmory = false;
            // The fallback list never takes the armory modules: a game they crash must still start without them.
            if (_o.ArmoryModules && !fallback)
            {
                if (ElephantModule.Complete)
                {
                    ElephantModule.Ensure(_o.GameDir, out string elephants);
                    if (elephants.Length > 0) _journal.Info("bannerlord", elephants);
                }
                foreach (var line in ArmoryCompat.Prepare(_o.GameDir, ArmoryModules)) _journal.Info("bannerlord", line);
                var installed = InstalledArmoryModules();
                string signature = ArmorySignature(installed);
                if (installed.Count > 0 && ReadArmoryFailure() == signature)
                    _journal.Info("bannerlord", $"armory modules left out: they crashed Bannerlord before ({ArmoryFailureFile}; remove it or change the modules to try again)");
                else if (installed.Count > 0)
                {
                    modules = ArmoryModuleList(modules, installed);
                    withArmory = true;
                    _journal.Info("bannerlord", $"armory modules: {string.Join(", ", installed)}");
                }
            }
            bool withNaval = false;
            if (_o.NavalModules && !fallback && NavalModule.Ensure(_o.GameDir, _o.AppDir, out string naval))
            {
                if (naval.Length > 0) _journal.Info("bannerlord", naval);
                modules = NavalModule.WithNaval(modules);
                withNaval = true;
            }
            var psi = new ProcessStartInfo(_o.ExePath, $"/singleplayer _MODULES_*{string.Join("*", modules)}*_MODULES_")
            {
                WorkingDirectory = _o.BinDir,
                UseShellExecute = false,
            };
            psi.Environment[HostVariable] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            WriteLaunchLease();
            ForegroundBeforeLaunch = Win32.GetForegroundWindow();
            try
            {
                var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
                lock (_gate)
                {
                    _process = p;
                    _generation++;
                    _launchedAt = DateTime.UtcNow;
                    _usingFallback = fallback;
                    _launchedWithArmory = withArmory;
                    LoadsElephants = withArmory && modules.Contains(ElephantModule.ModuleId);
                    LoadsNaval = withNaval;
                    _bootedOnce = false;
                    _lostReported = false;
                    _hungSince = null;
                    _status = null;
                    _lastStatusKey = null;
                }
                _journal.Info("bannerlord", $"launched pid {p.Id}: {psi.Arguments}");
            }
            catch (Exception e)
            {
                _journal.Error("bannerlord", "launch failed", e);
                if (File.Exists(LaunchLeasePath)) TryDelete(LaunchLeasePath);
            }
        }
    }

    /// <summary>Records whether the module is installed; returns true when a launch may go ahead. Raises ProblemChanged on a change.</summary>
    private bool SetModuleMissing(bool missing)
    {
        string? problem;
        lock (_gate)
        {
            if (_moduleMissing == missing) return !missing;
            _moduleMissing = missing;
            problem = _problem ?? (missing ? ModuleMissingProblem : null);
        }
        if (missing) _journal.Error("bannerlord", $"CK3Bridge module not found in {GameInstall.BannerlordModuleDir(_o.GameDir)}; Bannerlord is not started");
        else _journal.Info("bannerlord", "CK3Bridge module found");
        ProblemChanged?.Invoke(problem);
        return !missing;
    }

    private void WriteLaunchLease()
    {
        try
        {
            AtomicFile.WriteAllText(LaunchLeasePath,
                $"{{\"pid\":{Environment.ProcessId},\"t\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}", withBom: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _journal.Warn("bannerlord", $"could not write {LaunchLeaseFile}: {e.Message}");
        }
    }

    /// <summary>Bannerlord could not boot the module: tell the player once (until a heartbeat arrives again).</summary>
    private void ReportBootFailure(string why)
    {
        if (_bootFailureReported) return;
        _bootFailureReported = true;
        string? installed = GameInstall.BannerlordVersion(_o.GameDir);
        // A supported version is not the cause: say so instead of blaming it (players' reports 2026-10-24/26: "v1.4.8 may not be
        // supported (this release was built for v1.4.8)").
        string hint = installed is not null && installed.StartsWith(BridgeVersion.BannerlordTarget, StringComparison.OrdinalIgnoreCase)
            ? "Start Bannerlord once from its own launcher to see what stops it (an update to finish, a dialog, a broken mod in Modules); Bannerlord's own log is in the bridge log."
            : $"{(installed is not null ? $"Bannerlord {installed}" : "This Bannerlord version")} may not be supported (this release was built for {BridgeVersion.BannerlordTarget}).";
        string text = $"Bannerlord could not start the CK3 bridge module ({why}). {hint}";
        _journal.Error("bannerlord", text);
        BootFailed?.Invoke(text);
    }

    // ------------------------------------------------------------------------------------------------------------- watcher

    private async Task WatchLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception e) { _journal.Error("bannerlord", "watcher tick failed", e); }
            try { await Task.Delay(_o.PollInterval, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        Process? p;
        DateTime? relaunchAt;
        int foreign;
        lock (_gate) { p = _process; relaunchAt = _relaunchAt; foreign = _foreignPid; }

        if (p is null)
        {
            if (_stopping) return;
            if (foreign != 0 ? ForeignGameGone(foreign, now) : relaunchAt is { } at && now >= at) EnsureStarted();
            return;
        }

        if (p.HasExited)
        {
            HandleExit(p);
            return;
        }

        if (!_bootedOnce) AnswerSafeModeQuestion(p.Id);
        ReadStatus(p.Id, now);
        WatchResult();
        CheckHealth(p, now);
        TrimWhileIdle(p.Id, now);
    }

    private DateTime? _idleSince;
    private DateTime _nextIdleTrim = DateTime.MinValue;
    private int _idleTrimRunning;

    /// <summary>
    /// A Bannerlord waiting hidden for the next battle hands its RAM back to CK3 (PROTOCOL §28): once it has been idle for
    /// <see cref="BannerlordLinkOptions.IdleTrimDelay"/>, and again every <see cref="BannerlordLinkOptions.IdleTrimInterval"/> while
    /// it stays idle. Never while it holds a battle (loading, held, fighting): those pages are about to be used.
    /// </summary>
    private void TrimWhileIdle(int pid, DateTime now)
    {
        if (!_o.TrimWhenIdle) return;
        if (Status?.Status != BannerlordStates.Idle)
        {
            _idleSince = null;
            _nextIdleTrim = DateTime.MinValue;
            return;
        }
        _idleSince ??= now;
        if (now - _idleSince.Value < _o.IdleTrimDelay || now < _nextIdleTrim) return;
        _nextIdleTrim = now + _o.IdleTrimInterval;
        if (Interlocked.Exchange(ref _idleTrimRunning, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                var (before, after, error) = ProcessMemory.Trim(pid);
                if (error is null) _journal.Info("memory", $"idle Bannerlord working set {before} MB -> {after} MB, freed for CK3");
                else _journal.Warn("memory", $"idle Bannerlord not trimmed: {error}");
                long committed = ProcessMemory.CommittedMb(pid);
                if (_o.RecycleAboveMb > 0 && committed > _o.RecycleAboveMb && Status?.Status == BannerlordStates.Idle)
                    RecycleAsync(committed).GetAwaiter().GetResult();
            }
            finally
            {
                Interlocked.Exchange(ref _idleTrimRunning, 0);
            }
        });
    }

    private volatile bool _recycling;

    /// <summary>PROTOCOL §31: quits an idle Bannerlord that holds <paramref name="committedMb"/> of memory from earlier battles; the
    /// watcher starts a fresh one (hidden, idle) at once. Only between battles: a request arriving meanwhile waits for the boot.</summary>
    private async Task RecycleAsync(long committedMb)
    {
        Process? p;
        lock (_gate) p = _process;
        if (p is null || p.HasExited || _stopping) return;
        _journal.Info("memory", $"idle Bannerlord holds {committedMb} MB of memory from earlier battles; restarting it to free that for CK3 and the next battle");
        _recycling = true;
        await SendCommandAsync(BannerlordCommands.Quit, _cts.Token).ConfigureAwait(false);
        if (await WaitExitAsync(p, _o.QuitTimeout, _cts.Token).ConfigureAwait(false)) return;
        try { p.CloseMainWindow(); } catch (InvalidOperationException) { return; }
        if (!await WaitExitAsync(p, _o.CloseTimeout, _cts.Token).ConfigureAwait(false))
        {
            _recycling = false;   // it stays: never kill a game that is merely slow
            _journal.Warn("memory", "the idle Bannerlord did not quit; it keeps running");
        }
    }

    /// <summary>The player's own Bannerlord exited (or turned out to run CK3Bridge after all): time to decide again.</summary>
    private bool ForeignGameGone(int pid, DateTime now)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return true;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        return ProvesBridge(pid, ProtocolJson.TryRead<BannerlordStatus>(StatusPath), now);
    }

    private void HandleExit(Process p)
    {
        int code;
        try { code = p.ExitCode; } catch (InvalidOperationException) { code = int.MinValue; }
        try { _lastExitedPid = p.Id; } catch (InvalidOperationException) { }
        lock (_gate)
        {
            // A restart may already have replaced the process this tick looked at.
            if (!ReferenceEquals(_process, p)) { p.Dispose(); return; }
            _process = null;
            _status = null;
            _lastStatusKey = null;
        }
        p.Dispose();
        // What the dead process had not taken yet must not reach the next one (it would hold a battle nobody asks for).
        foreach (var path in new[] { RequestPath, CommandPath })
            if (File.Exists(path) && TryDelete(path)) _journal.Info("bannerlord", $"{Path.GetFileName(path)} not taken before the exit; removed");

        if (_recycling && !_stopping)
        {
            _recycling = false;
            _journal.Info("bannerlord", "exited (restarted to free memory)");
            Lost?.Invoke(BannerlordLoss.Exited, "Bannerlord was restarted to free memory");
            if (_o.KeepRunning) lock (_gate) _relaunchAt = DateTime.UtcNow;
            return;
        }
        bool requested = _stopping;
        bool neverBooted = !_bootedOnce;
        if (requested) _journal.Info("bannerlord", "exited (requested)");
        else _journal.Warn("bannerlord", $"exited unexpectedly (code {code}){(neverBooted ? " before its first heartbeat" : "")}");
        if (!requested && neverBooted) JournalGameLog(_lastExitedPid);
        Lost?.Invoke(BannerlordLoss.Exited, requested ? "Bannerlord was closed" : $"Bannerlord exited (code {code})");
        if (requested) return;
        if (neverBooted && _launchedWithArmory)
        {
            // The armory modules (an asset or shader cache built for another game version) are the likely cause: remember it for
            // these module versions and start the same list without them; the fallback list stays for a second failure.
            WriteArmoryFailure(ArmorySignature(InstalledArmoryModules()));
            _journal.Warn("bannerlord", $"Bannerlord crashed with the armory modules; relaunching without them (costumes use the vanilla items; {ArmoryFailureFile} remembers it)");
            if (_o.KeepRunning) lock (_gate) _relaunchAt = DateTime.UtcNow + _o.RelaunchDelay;
            return;
        }
        if (neverBooted && _usingFallback)
        {
            // Both module lists failed: relaunching in a loop would only repeat it. The next battle or "Start Bannerlord" tries again.
            ReportBootFailure($"it exited with code {code} before the module answered");
            return;
        }
        if (!_o.KeepRunning) return;
        if (neverBooted)
        {
            _journal.Info("bannerlord", "relaunching with the fallback module list");
            _usingFallback = true;
        }
        lock (_gate) _relaunchAt = DateTime.UtcNow + _o.RelaunchDelay;
    }

    /// <summary>Bannerlord's own log of a process that died before our module answered (rgl_log_&lt;pid&gt;.txt, its last lines and the
    /// errors log), copied into the bridge log: players' crash reports carry the bridge log only, and the cause is in Bannerlord's.</summary>
    private void JournalGameLog(int pid)
    {
        if (pid <= 0) return;
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Mount and Blade II Bannerlord", "logs");
            foreach (var (file, lines) in new[] { ($"rgl_log_errors_{pid}.txt", 40), ($"rgl_log_{pid}.txt", 60) })
            {
                string path = Path.Combine(dir, file);
                if (!File.Exists(path)) { _journal.Info("bannerlord", $"{file}: not written"); continue; }
                using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(s);
                var tail = r.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(lines).ToList();
                _journal.Warn("bannerlord", $"{file}, last {tail.Count} lines:\n    " + string.Join("\n    ", tail));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _journal.Warn("bannerlord", $"Bannerlord's log of pid {pid} unreadable: {e.Message}");
        }
    }

    private void AnswerSafeModeQuestion(int pid)
    {
        // Our process, or one started from a bridge command line (e.g. handed over by the one we launched); never the player's game.
        var pids = new List<int> { pid };
        foreach (var other in Process.GetProcessesByName(GameProcessName))
        {
            if (other.Id != pid && Win32.CommandLineOf(other.Id)?.Contains(BridgeModule, StringComparison.OrdinalIgnoreCase) == true)
                pids.Add(other.Id);
            other.Dispose();
        }
        if (SafeModeDialog.TryAnswerNo(pids) is { } what) _journal.Info("bannerlord", what);
        if (SafeModeDialog.TryAcknowledgeModChange(pids) is { } change) _journal.Info("bannerlord", change);
    }

    private void ReadStatus(int pid, DateTime now)
    {
        var s = ProtocolJson.TryRead<BannerlordStatus>(StatusPath);
        if (s is null || string.IsNullOrEmpty(s.Status)) return;
        if (s.Pid != pid && !AdoptReportedProcess(s, now)) return;   // stale file of an earlier process

        bool changed;
        lock (_gate)
        {
            _status = s;
            string key = s.Status + "|" + s.BattleId;
            changed = key != _lastStatusKey;
            _lastStatusKey = key;
        }
        if (!_bootedOnce)
        {
            _bootedOnce = true;
            _bootFailureReported = false;
            _journal.Info("bannerlord", $"first heartbeat after {(now - _launchedAt).TotalSeconds:0.0} s: {s.Status}");
            if (File.Exists(LaunchLeasePath)) TryDelete(LaunchLeasePath);   // the module has read it (or did not need it)
        }
        if (changed)
        {
            _journal.Info("bannerlord", $"status {s.Status}{(s.BattleId is null ? "" : " " + s.BattleId)}{(s.Error is null ? "" : " error: " + s.Error)}");
            if (s.Status == BannerlordStates.Error && s.BattleId is null && !_stopping)
            {
                if (!_usingFallback)
                {
                    // The module could not start with the lean module list (PROTOCOL §3.1): restart with the fallback list.
                    _journal.Warn("bannerlord", "module reported an error while idle; restarting with the fallback module list");
                    _usingFallback = true;
                    _ = RestartAsync();
                }
                else ReportBootFailure(s.Error ?? "the module reported an error");
            }
            StatusChanged?.Invoke(s);
        }
    }

    /// <summary>The heartbeat names a different, live Bannerlord process (e.g. the exe we started handed over to another process):
    /// follow that one.</summary>
    private bool AdoptReportedProcess(BannerlordStatus s, DateTime now)
    {
        if (s.Pid <= 0 || now - HeartbeatTime(s) > _o.HeartbeatTimeout) return false;
        Process reported;
        try { reported = Process.GetProcessById(s.Pid); }
        catch (ArgumentException) { return false; }
        if (!reported.ProcessName.StartsWith(GameProcessName, StringComparison.OrdinalIgnoreCase))
        {
            reported.Dispose();
            return false;
        }
        lock (_gate)
        {
            _journal.Info("bannerlord", $"following pid {s.Pid} named in the heartbeat (was {_process?.Id})");
            _process?.Dispose();
            _process = reported;
        }
        return true;
    }

    /// <summary>Reports a new or rewritten result.json. A result for a battle this bridge run never requested (a file an earlier run or
    /// a test left behind) is skipped quietly: it is no battle of this session (live test, start-up line
    /// "result.json: t1_knights finished").</summary>
    internal void WatchResult()
    {
        var stamp = File.Exists(ResultPath) ? File.GetLastWriteTimeUtc(ResultPath) : default;
        if (stamp == _resultStamp) return;
        if (ProtocolJson.TryRead<BattleResult>(ResultPath) is not { Id: not null } result) return;   // retried next tick if half-written
        _resultStamp = stamp;
        if (!IsRequestedThisRun(result.Id)) return;
        _journal.Info("bannerlord", $"result.json: {result.Id} {result.Status} (winner {result.Winner ?? "-"})");
        ResultUpdated?.Invoke(result);
    }

    private static DateTime HeartbeatTime(BannerlordStatus s) => DateTimeOffset.FromUnixTimeMilliseconds(s.T).UtcDateTime;

    private TimeSpan HeartbeatLimit(BannerlordStatus s) =>
        s.Status is BannerlordStates.Booting or BannerlordStates.Loading ? _o.LoadingHeartbeatTimeout : _o.HeartbeatTimeout;

    private void CheckHealth(Process p, DateTime now)
    {
        var s = Status;
        TimeSpan age;
        string why;
        if (!_bootedOnce)
        {
            age = now - _launchedAt;
            if (age <= _o.BootTimeout) { _hungSince = null; return; }
            why = $"no heartbeat {age.TotalSeconds:0} s after launch";
            ReportBootFailure($"no answer {age.TotalSeconds:0} s after launch");
        }
        else
        {
            age = now - HeartbeatTime(s!);
            if (age <= HeartbeatLimit(s!))
            {
                if (_lostReported)
                {
                    _lostReported = false;
                    _journal.Info("bannerlord", "heartbeat recovered");
                    StatusChanged?.Invoke(s!);   // the module may have moved on while silent: let the orchestrator look again
                }
                _hungSince = null;
                return;
            }
            why = $"heartbeat {age.TotalSeconds:0.0} s old (status {s!.Status})";
        }

        if (!_lostReported)
        {
            _lostReported = true;
            _journal.Warn("bannerlord", why);
            Lost?.Invoke(BannerlordLoss.Unresponsive, why);
        }

        // A game thread stuck in the engine's native code keeps no heartbeat while its window still answers (IsHungAppWindow false):
        // live 2026-10-20 b305 froze 40 s into a battle and every request of the next hour went unread. Long silence kills it too.
        if (_bootedOnce && age > HeartbeatLimit(s!) + _o.SilentKill)
        {
            _journal.Error("bannerlord", $"no heartbeat for {age.TotalSeconds:0} s (status {s!.Status}); killing pid {p.Id}");
            try { p.Kill(); } catch (Exception e) { _journal.Error("bannerlord", "kill failed", e); }
            _hungSince = null;
            return;
        }

        var hwnd = MainWindow;
        if (hwnd != 0 && Win32.IsHungAppWindow(hwnd))
        {
            _hungSince ??= now;
            if (now - _hungSince > _o.HungKill)
            {
                _journal.Error("bannerlord", $"window hung for {(now - _hungSince.Value).TotalSeconds:0} s; killing pid {p.Id}");
                try { p.Kill(); } catch (Exception e) { _journal.Error("bannerlord", "kill failed", e); }
                _hungSince = null;
            }
        }
        else _hungSince = null;
    }

    private async Task RestartAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stopping = false;
        EnsureStarted();
    }

    // ------------------------------------------------------------------------------------------------------------- files

    /// <summary>Whether this bridge run wrote a request with this id.</summary>
    internal bool IsRequestedThisRun(string id)
    {
        lock (_gate) return _requestIds.Contains(id);
    }

    public Task WriteRequestAsync(BattleRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(request.Id))
            lock (_gate) _requestIds.Add(request.Id);
        DeleteResult();
        ProtocolJson.WriteAtomic(RequestPath, request);
        _journal.Info("bannerlord", $"request {request.Id} written (preload {request.Presentation?.Preload})");
        return Task.CompletedTask;
    }

    public Task WithdrawRequestAsync(string battleId, CancellationToken ct)
    {
        if (ProtocolJson.TryRead<BattleRequest>(RequestPath)?.Id == battleId && TryDelete(RequestPath))
            _journal.Info("bannerlord", $"request {battleId} withdrawn before Bannerlord took it");
        return Task.CompletedTask;
    }

    /// <summary>Writes one command and waits until the module has taken it, so commands and request writes reach the module in the
    /// order they were issued. A command the module has not taken is never overwritten (it takes command.txt whole: read, then
    /// delete; replacing or appending to the file could lose a command or run one twice). There is deliberately no time limit while
    /// the process lives: the module takes the file at its next poll or right after a synchronous load step, and a main thread that
    /// stays blocked shows as a hung window, which is killed after HungKill (its exit ends the wait). Only 'quit' replaces a pending
    /// command after QuitTimeout. Returns false when the command was dropped (Bannerlord not running, exited, or being closed).</summary>
    public async Task<bool> SendCommandAsync(string command, CancellationToken ct)
    {
        int generation;
        lock (_gate)
        {
            if (_process is not { HasExited: false })
            {
                _journal.Warn("bannerlord", $"command '{command}' dropped: Bannerlord is not running");
                return false;
            }
            generation = _generation;
        }
        await _commandGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool quit = command == BannerlordCommands.Quit;
            var deadline = quit ? DateTime.UtcNow + _o.QuitTimeout : DateTime.MaxValue;
            // Normally absent: every earlier command was taken before its send returned.
            var pending = await WaitTakenAsync(generation, quit, deadline, ct).ConfigureAwait(false);
            if (pending is CommandWait.Gone or CommandWait.Stopping) return Dropped(command, pending);
            if (pending == CommandWait.TimedOut) _journal.Warn("bannerlord", "a pending command was not taken; 'quit' replaces it");

            AtomicFile.WriteAllText(CommandPath, command, withBom: false);
            _journal.Info("bannerlord", $"command: {command}");
            var taken = await WaitTakenAsync(generation, quit, deadline, ct).ConfigureAwait(false);
            return taken == CommandWait.Taken || Dropped(command, taken);
        }
        finally { _commandGate.Release(); }
    }

    private enum CommandWait { Taken, Gone, Stopping, TimedOut }

    private async Task<CommandWait> WaitTakenAsync(int generation, bool quit, DateTime deadline, CancellationToken ct)
    {
        while (File.Exists(CommandPath))
        {
            lock (_gate)
                if (_generation != generation || _process is not { HasExited: false }) return CommandWait.Gone;
            if (_stopping && !quit) return CommandWait.Stopping;
            if (DateTime.UtcNow >= deadline) return CommandWait.TimedOut;
            await Task.Delay(15, ct).ConfigureAwait(false);
        }
        return CommandWait.Taken;
    }

    private bool Dropped(string command, CommandWait why)
    {
        _journal.Warn("bannerlord", $"command '{command}' " + why switch
        {
            CommandWait.Gone => "dropped: Bannerlord exited",
            CommandWait.Stopping => "dropped: Bannerlord is being closed",
            _ => "was not taken in time",
        });
        return false;
    }

    public BattleResult? ReadResult(string battleId)
    {
        var r = ProtocolJson.TryRead<BattleResult>(ResultPath);
        return r is not null && r.Id == battleId ? r : null;
    }

    public void DeleteResult() => TryDelete(ResultPath);

    private bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _journal.Warn("bannerlord", $"could not delete {Path.GetFileName(path)}: {e.Message}");
            return false;
        }
    }

    /// <summary>Tests only: treats <paramref name="process"/> as the Bannerlord in use (no watcher; nothing is launched or stopped).</summary>
    internal void AttachForTests(Process process)
    {
        lock (_gate)
        {
            _process = process;
            _generation++;
        }
    }

    // ------------------------------------------------------------------------------------------------------------- shutdown

    public async Task StopAsync(CancellationToken ct)
    {
        _stopping = true;
        Process? p;
        lock (_gate) { p = _process; _relaunchAt = null; }
        if (p is null || p.HasExited) return;

        _journal.Info("bannerlord", "quitting");
        await SendCommandAsync(BannerlordCommands.Quit, ct).ConfigureAwait(false);
        if (await WaitExitAsync(p, _o.QuitTimeout, ct).ConfigureAwait(false)) return;

        _journal.Info("bannerlord", "no exit after 'quit'; closing the main window");
        try { p.CloseMainWindow(); } catch (InvalidOperationException) { return; }
        if (await WaitExitAsync(p, _o.CloseTimeout, ct).ConfigureAwait(false)) return;

        var hwnd = MainWindow;
        if (hwnd != 0 && Win32.IsHungAppWindow(hwnd))
        {
            _journal.Error("bannerlord", "window is hung; killing");
            try { p.Kill(); } catch (Exception e) { _journal.Error("bannerlord", "kill failed", e); }
        }
        else _journal.Warn("bannerlord", "Bannerlord is still closing; leaving it to finish on its own");
    }

    private static async Task<bool> WaitExitAsync(Process p, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return p.HasExited; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _watch?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        lock (_gate) _process?.Dispose();
        _cts.Dispose();
    }
}
