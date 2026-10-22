// The bridge's view of the Bannerlord process and its CK3Bridge module (PROTOCOL §3, §8.3).
using CK3xBL.Protocol;

namespace CK3xBL.Core.Bannerlord;

public static class BannerlordStates
{
    public const string Booting = "booting", Idle = "idle", Loading = "loading", Held = "held", Visible = "visible",
        Fighting = "fighting", Result = "result", FadingOut = "fading_out", Black = "black", Ended = "ended",
        Cancelled = "cancelled", Aborted = "aborted", Error = "error";

    /// <summary>The module holds a battle session in these states (a request was taken and not yet ended).</summary>
    public static bool HoldsSession(string? status) => status is Loading or Held or Visible or Fighting or Result;
}

public static class BannerlordCommands
{
    public const string Go = "go", Cancel = "cancel", Abort = "abort", ReturnNow = "return_now", Quit = "quit",
        Foreground = "fg", Background = "bg";

    /// <summary>The bridge could not show its title card: the module shows its own for the held battle (PROTOCOL §13.6).</summary>
    public const string TitleInEngine = "title_in_engine";

    public static string HandoffTo(nint hwnd) => $"handoff_to {(long)hwnd}";

    /// <summary>'cancel' naming the battle it is meant for (PROTOCOL §8.3): modules that ignore the extra word cancel whatever they
    /// hold; a module that reads it can leave another battle's pending request alone.</summary>
    public static string CancelBattle(string battleId) => $"{Cancel} {battleId}";

    public static bool IsCancel(string command) => command == Cancel || command.StartsWith(Cancel + " ", StringComparison.Ordinal);
}

public enum BannerlordLoss
{
    /// <summary>The process is gone (crash, closed by the player or the bridge, killed). Whatever it held or had not taken yet is gone
    /// with it; request.json and command.txt were removed.</summary>
    Exited,
    /// <summary>The process runs but its heartbeat is stale (or never came). It may still hold its battle and come back;
    /// StatusChanged is raised again when it does.</summary>
    Unresponsive,
}

public interface IBannerlordLink
{
    /// <summary>Last status read from bannerlord_status.json for the current process (null before the first heartbeat).</summary>
    BannerlordStatus? Status { get; }
    bool IsRunning { get; }
    int ProcessId { get; }
    /// <summary>The game window (status hwnd, else the TWApplication window of the process), or 0.</summary>
    nint MainWindow { get; }
    /// <summary>Foreground window just before the last launch (to hand focus back if the booting game took it).</summary>
    nint ForegroundBeforeLaunch { get; }
    /// <summary>Why Bannerlord cannot be started, in words for the player (e.g. the player's own Bannerlord is open), or null.</summary>
    string? Problem { get; }

    /// <summary>Raised (on a background thread) when status or battleId changes, and again when a lost heartbeat recovers.</summary>
    event Action<BannerlordStatus>? StatusChanged;
    /// <summary>Raised when the process exited, did not boot, or its heartbeat went stale (once per loss).</summary>
    event Action<BannerlordLoss, string>? Lost;
    /// <summary>Raised (on a background thread) when result.json was rewritten with a parseable result.</summary>
    event Action<BattleResult>? ResultUpdated;

    /// <summary>Adopts a running Bannerlord that runs CK3Bridge, or launches one (returns once started, not when idle). Never touches
    /// a Bannerlord started without CK3Bridge (see <see cref="Problem"/>).</summary>
    Task StartAsync(CancellationToken ct);
    /// <summary>Deletes any stale result.json, then writes request.json atomically (the module preloads it).</summary>
    Task WriteRequestAsync(BattleRequest request, CancellationToken ct);
    /// <summary>Deletes request.json if it still holds <paramref name="battleId"/> (the module has not taken it).</summary>
    Task WithdrawRequestAsync(string battleId, CancellationToken ct);
    /// <summary>Writes one command to command.txt and returns once the module took it, so commands and requests stay in order and a
    /// pending command is never overwritten (PROTOCOL §3.3, §8.3). False when dropped: Bannerlord not running, exited or closing.</summary>
    Task<bool> SendCommandAsync(string command, CancellationToken ct);
    /// <summary>result.json if it belongs to <paramref name="battleId"/>, else null.</summary>
    BattleResult? ReadResult(string battleId);
    void DeleteResult();
    /// <summary>Graceful quit: "quit" command, then CloseMainWindow; kills only a window hung for longer than the configured limit.</summary>
    Task StopAsync(CancellationToken ct);
}
