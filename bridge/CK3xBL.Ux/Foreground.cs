using System.Diagnostics;
using System.Runtime.InteropServices;
using static CK3xBL.Ux.Interop.Native;

namespace CK3xBL.Ux;

/// <summary>
/// Brings a game window to the foreground from a background process.
///
/// The ladder, cheapest and most legitimate first; every rung is confirmed with GetForegroundWindow for up to 150 ms:
///   1. plain SetForegroundWindow (works when we own the last input, were granted AllowSetForegroundWindow, ...);
///   2. Alt-tap: Alt held while calling SetForegroundWindow (the foreground lock yields to a process that sends input);
///   3. AttachThreadInput to the foreground thread, BringWindowToTop + SetForegroundWindow, detach at once;
///   4. SwitchToThisWindow.
/// Rungs 2-4 are used only when the current foreground window is "ours" (the bridge, a registered game process, the target's
/// own process), nobody's (no foreground window) or the bare desktop/taskbar: a player who alt-tabbed to another application
/// is never yanked back, and input is never injected into a foreign application. Shell surfaces the player is actively using
/// (Start/Search, Task View, the Alt-Tab switcher) are not neutral either, and neither is a held modifier key (an injected
/// Alt-up would release the player's own Alt and commit an Alt-Tab). Windows are never left TOPMOST (a real bug seen in
/// testing): the target's topmost bit is checked afterwards and cleared if the ladder set it.
/// </summary>
public static class Foreground
{
    private const int RungWaitMs = 150;
    /// <summary>How long the ladder waits for the player to let go of a modifier key before giving up on rungs 2-4.</summary>
    private const int ModifierReleaseWaitMs = 250;

    /// <summary>Shell surfaces where focus is "nobody's" for the overlay's safety valve (a card may stay up under them).</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "Windows.UI.Core.CoreWindow", "TopLevelWindowForOverflowXamlIsland",
        "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "ForegroundStaging",
    };

    /// <summary>The only shell surfaces the ladder may escalate over: the bare desktop and the taskbar.</summary>
    private static readonly HashSet<string> NeutralForActivation = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
    };

    /// <summary>
    /// Runs the activation ladder. <paramref name="isTrustedProcess"/> says which processes' windows may hold the foreground
    /// while rungs 2-4 run (the games); the bridge itself and the target's process are always trusted.
    /// <paramref name="force"/> skips that check (use only on explicit user request); a held modifier key still stops rungs 2-4.
    /// Returns true when <paramref name="target"/> is the foreground window.
    /// </summary>
    public static bool Activate(IntPtr target, Func<int, bool>? isTrustedProcess = null, bool force = false)
    {
        if (target == 0 || !IsWindow(target)) { UxLog.Write($"foreground: target 0x{target:X} is not a window"); return false; }
        if (GetForegroundWindow() == target) return true;

        EnsureMessageQueue();   // AttachThreadInput needs a message queue on this thread
        bool wasTopmost = IsTopmost(target);
        try
        {
            // Restoring a minimized window is allowed without the foreground right; activation is what the ladder is for.
            if (IsIconic(target)) ShowWindow(target, SW_SHOWNOACTIVATE);

            if (TryRung("SetForegroundWindow", target, () => SetForegroundWindow(target))) return true;

            nint fg = GetForegroundWindow();
            if (!force && !MayEscalateOver(fg, target, isTrustedProcess))
            {
                UxLog.Write($"foreground: not escalating, the player is busy elsewhere ({Describe(fg)})");
                return false;
            }
            if (!WaitForModifiersReleased())
            {
                UxLog.Write("foreground: not escalating, a modifier key is held (the player is mid-shortcut)");
                return false;
            }

            if (TryRung("Alt-tap", target, () => AltTapActivate(target))) return true;
            if (TryRung("AttachThreadInput", target, () => AttachedActivate(target))) return true;
            if (TryRung("SwitchToThisWindow", target, () => { SwitchToThisWindow(target, true); return true; })) return true;

            // Visual only: raise it in the normal band (never TOPMOST) so a click on the click-through overlay lands on it.
            SetWindowPos(target, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            UxLog.Write($"foreground: every rung failed for {GameWindows.Describe(target)}; foreground is {Describe(GetForegroundWindow())}");
            return false;
        }
        finally
        {
            if (!wasTopmost && IsWindow(target) && IsTopmost(target))
            {
                SetWindowPos(target, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                UxLog.Write($"foreground: cleared a TOPMOST bit left on 0x{target:X}");
            }
        }
    }

    /// <summary>Lets <paramref name="processId"/> take the foreground itself (only effective while we hold it).</summary>
    public static bool AllowProcessToSetForeground(int processId) => AllowSetForegroundWindow((uint)processId);

    /// <summary>
    /// Taskbar, desktop, Start/Search, Task View, the Alt-Tab switcher and similar shell surfaces. They sit above topmost
    /// windows, so a card may stay up while the player uses them (the overlay's safety valve ignores them). The ladder is
    /// stricter: it escalates only over the bare desktop and the taskbar.
    /// </summary>
    public static bool IsShellWindow(IntPtr hwnd) => hwnd != 0 && ShellClasses.Contains(ClassName(hwnd));

    /// <summary>Removes the TOPMOST bit from a window (repair helper; e.g. after a crashed tool left one behind).</summary>
    public static void ClearTopmost(IntPtr hwnd)
    {
        if (hwnd != 0 && IsWindow(hwnd) && IsTopmost(hwnd))
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>True when rungs 2-4 may run over <paramref name="fg"/>: no foreground, desktop/taskbar, or one of our processes.</summary>
    internal static bool MayEscalateOver(nint fg, nint target, Func<int, bool>? isTrusted)
    {
        if (fg == 0 || NeutralForActivation.Contains(ClassName(fg))) return true;
        int pid = ProcessIdOf(fg);
        if (pid == 0) return false;                       // the window vanished meanwhile: not provably ours
        if (pid == Environment.ProcessId || pid == ProcessIdOf(target)) return true;
        try { return isTrusted?.Invoke(pid) ?? false; }
        catch (Exception e)
        {
            UxLog.Write($"foreground: trust check for pid {pid} failed ({e.Message}); treating it as foreign");
            return false;
        }
    }

    /// <summary>Waits up to <see cref="ModifierReleaseWaitMs"/> for Shift, Ctrl, Alt and Win to be up. True when they are.</summary>
    private static bool WaitForModifiersReleased()
    {
        var sw = Stopwatch.StartNew();
        while (InputSafety.AnyModifierDown())
        {
            if (sw.ElapsedMilliseconds >= ModifierReleaseWaitMs) return false;
            Thread.Sleep(10);
        }
        return true;
    }

    private static bool TryRung(string name, nint target, Func<bool> act)
    {
        bool returned;
        try { returned = act(); }
        catch (Exception e) { UxLog.Write($"foreground: rung '{name}' threw {e.Message}"); returned = false; }
        var sw = Stopwatch.StartNew();
        do
        {
            if (GetForegroundWindow() == target)
            {
                UxLog.Write($"foreground: '{name}' activated 0x{target:X} in {sw.ElapsedMilliseconds} ms");
                return true;
            }
            Thread.Sleep(5);
        } while (sw.ElapsedMilliseconds < RungWaitMs);
        UxLog.Write($"foreground: '{name}' did not take (returned {returned})");
        return false;
    }

    /// <summary>
    /// Alt down, SetForegroundWindow, a tap of an unassigned key (so the Alt release is not a bare Alt that would open the
    /// window menu), Alt up. Left Alt by scan code: right Alt is AltGr on many layouts.
    /// </summary>
    private static unsafe bool AltTapActivate(nint target)
    {
        var down = new INPUT[] { Key(VK_MENU, 0x38, false) };
        var rest = new INPUT[] { Key(VK_MASK_UNASSIGNED, 0, false), Key(VK_MASK_UNASSIGNED, 0, true), Key(VK_MENU, 0x38, true) };
        fixed (INPUT* p = down) SendInput(1, p, Marshal.SizeOf<INPUT>());
        bool result;
        try { result = SetForegroundWindow(target); }
        finally
        {
            fixed (INPUT* p = rest) SendInput((uint)rest.Length, p, Marshal.SizeOf<INPUT>());
        }
        return result;
    }

    private static bool AttachedActivate(nint target)
    {
        nint fg = GetForegroundWindow();
        if (fg != 0 && IsHungAppWindow(fg)) return false;           // attaching to a hung thread would hang us
        uint fgThread = fg == 0 ? 0 : GetWindowThreadProcessId(fg, out _);
        uint me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try
        {
            BringWindowToTop(target);
            return SetForegroundWindow(target);
        }
        finally
        {
            if (attached) AttachThreadInput(me, fgThread, false);  // detach at once: attached threads share key state
        }
    }

    private static INPUT Key(ushort vk, ushort scan, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
    };

    private static string Describe(nint hwnd)
    {
        if (hwnd == 0) return "none";
        int pid = ProcessIdOf(hwnd);
        return $"{TryProcessName(pid) ?? "?"} pid={pid} class='{ClassName(hwnd)}'";
    }
}
