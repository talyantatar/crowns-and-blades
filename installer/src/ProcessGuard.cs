// Which of our programs and the games are running, matched by their executable's folder (a sandbox never matches a real
// game). Setup never kills anything: it asks the bridge to quit through its quit event, and asks the player to close games.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Threading;
using CK3xBL.Setup.Util;

namespace CK3xBL.Setup;

internal static class ProcessGuard
{
    /// <summary>Running processes whose executable lies inside <paramref name="dir"/> (optionally only one process name).</summary>
    public static List<Process> RunningFrom(string? dir, string? processName = null)
    {
        var result = new List<Process>();
        if (string.IsNullOrWhiteSpace(dir)) return result;
        var processes = processName is null ? Process.GetProcesses() : Process.GetProcessesByName(processName);
        foreach (var p in processes)
        {
            bool keep = false;
            try
            {
                if (p.Id != Process.GetCurrentProcess().Id && Native.ProcessImagePath(p.Id) is { } image && FileOps.IsUnder(image, dir!))
                    keep = true;
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException or PathTooLongException) { }
            if (keep) result.Add(p);
            else p.Dispose();
        }
        return result;
    }

    /// <summary>Crowns & Blades (CK3xBL.exe) running from the install folder.</summary>
    public static List<Process> Bridge(string installDir) => RunningFrom(installDir, Product.BridgeProcessName);

    public static bool BannerlordRunning(string? blDir)
    {
        var running = RunningFrom(blDir);
        try { return running.Count > 0; }
        finally { running.ForEach(p => p.Dispose()); }
    }

    public static bool Ck3Running(SetupContext ctx)
    {
        // Without a known CK3 folder (real install only) any ck3.exe counts: CK3 reads dlc_load.json only when it starts.
        var running = ctx.Ck3Dir is not null ? RunningFrom(ctx.Ck3Dir, "ck3")
            : ctx.Sandbox ? new List<Process>() : Process.GetProcessesByName("ck3").ToList();
        try { return running.Count > 0; }
        finally { running.ForEach(p => p.Dispose()); }
    }

    /// <summary>Some copy of the bridge (any folder) holds its single-instance mutex.</summary>
    public static bool AnyBridgeInstance()
    {
        try
        {
            using var m = Mutex.OpenExisting(Product.BridgeMutex, MutexRights.Synchronize);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>Asks the running bridge to quit (it then closes its own Bannerlord and restores CK3's window and volumes) and
    /// waits for the processes to exit. False when the bridge has no quit event (older than 0.9) or did not exit in time.</summary>
    public static bool RequestBridgeQuit(List<Process> bridges, TimeSpan timeout, SetupLog log)
    {
        if (bridges.Count == 0) return true;
        try
        {
            using var quit = EventWaitHandle.OpenExisting(Product.BridgeQuitEvent, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize);
            quit.Set();
            log.Info("asked Crowns & Blades to quit");
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            log.Warn("this Crowns & Blades version cannot be asked to quit; quit it from its tray icon");
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            log.Warn("not allowed to signal Crowns & Blades's quit event");
            return false;
        }
        var deadline = DateTime.UtcNow + timeout;
        foreach (var p in bridges)
        {
            var left = deadline - DateTime.UtcNow;
            try
            {
                if (left <= TimeSpan.Zero || !p.WaitForExit((int)left.TotalMilliseconds)) return false;
            }
            catch (InvalidOperationException) { }
        }
        return true;
    }

    /// <summary>Waits for a process id to exit (the uninstaller's copy in %TEMP% waits for the one that started it).</summary>
    public static void WaitForPid(int pid, TimeSpan timeout)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException) { }          // already gone
        catch (InvalidOperationException) { }
    }
}
