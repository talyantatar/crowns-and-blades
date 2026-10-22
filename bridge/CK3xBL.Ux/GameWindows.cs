using System.Diagnostics;
using System.Runtime.InteropServices;
using CK3xBL.Ux.Interop;
using static CK3xBL.Ux.Interop.Native;

namespace CK3xBL.Ux;

/// <summary>Read-only helpers to locate and describe the games' render windows. All rects are physical pixels.</summary>
public static class GameWindows
{
    /// <summary>CK3's main window class (SDL2). Its helper windows (IME, MSCTFIME UI) share the process, so match the class.</summary>
    public const string Ck3WindowClass = "SDL_app";
    /// <summary>Bannerlord's main window class.</summary>
    public const string BannerlordWindowClass = "TWApplication";

    /// <summary>
    /// The process's main window: top-level, unowned, of the given class (if any), largest area; visible windows win over
    /// hidden ones. Process.MainWindowHandle is not used because it flips to splash/IME/crash windows. Returns 0 if none.
    /// </summary>
    public static IntPtr FindMainWindow(int pid, string? className = null)
    {
        var state = new FindState(pid, className);
        var handle = GCHandle.Alloc(state);
        try
        {
            unsafe { EnumWindows(&FindCallback, GCHandle.ToIntPtr(handle)); }
        }
        finally { handle.Free(); }
        return state.BestVisible != 0 ? state.BestVisible : state.BestHidden;
    }

    /// <summary>First main window of any running process with this name (without ".exe"). Returns 0 if none.</summary>
    public static IntPtr FindByProcessName(string processName, string? className = null)
    {
        foreach (var p in Process.GetProcessesByName(processName))
        {
            using (p)
            {
                var h = FindMainWindow(p.Id, className);
                if (h != 0) return h;
            }
        }
        return 0;
    }

    /// <summary>The full rect (not the work area) of the monitor the window is on, or the nearest one.</summary>
    public static ScreenRect MonitorRectOf(IntPtr hwnd)
    {
        using var _ = DpiScope.PerMonitorV2();
        nint monitor = hwnd != 0 && IsWindow(hwnd)
            ? MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
            : MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
        return ScreenRect.From(MonitorInfo(monitor).rcMonitor);
    }

    /// <summary>The primary monitor's full rect.</summary>
    public static ScreenRect PrimaryMonitorRect() => MonitorRectOf(0);

    /// <summary>The window's client area in screen coordinates (empty if the window is gone).</summary>
    public static ScreenRect ClientRectOnScreen(IntPtr hwnd)
    {
        using var _ = DpiScope.PerMonitorV2();
        if (!IsWindow(hwnd) || !GetClientRect(hwnd, out var rc)) return default;
        var origin = new POINT();
        ClientToScreen(hwnd, ref origin);
        return new ScreenRect(origin.X, origin.Y, rc.Width, rc.Height);
    }

    public static int ProcessIdOf(IntPtr hwnd) => hwnd == 0 ? 0 : Native.ProcessIdOf(hwnd);

    public static bool IsAlive(IntPtr hwnd) => hwnd != 0 && IsWindow(hwnd);

    /// <summary>One-line description for logs: class, pid, style bits that matter here, rect, topmost/cloak state.</summary>
    public static string Describe(IntPtr hwnd)
    {
        if (!IsAlive(hwnd)) return $"hwnd=0x{hwnd:X} (gone)";
        using var _ = DpiScope.PerMonitorV2();
        GetWindowRect(hwnd, out var r);
        long style = Style(hwnd), ex = ExStyle(hwnd);
        return $"hwnd=0x{hwnd:X} class='{ClassName(hwnd)}' pid={Native.ProcessIdOf(hwnd)} rect={r} " +
               $"style=0x{style:X8} ex=0x{ex:X8} caption={(style & WS_CAPTION) == WS_CAPTION} topmost={(ex & WS_EX_TOPMOST) != 0} " +
               $"visible={IsWindowVisible(hwnd)} iconic={IsIconic(hwnd)} cloaked={Cloaked(hwnd)}";
    }

    private sealed class FindState(int pid, string? className)
    {
        public readonly int Pid = pid;
        public readonly string? ClassName = className;
        public nint BestVisible, BestHidden;
        public long BestVisibleArea = -1, BestHiddenArea = -1;
    }

    [UnmanagedCallersOnly]
    private static int FindCallback(nint hwnd, nint lParam)
    {
        try
        {
            var state = (FindState)GCHandle.FromIntPtr(lParam).Target!;
            if (Native.ProcessIdOf(hwnd) != state.Pid) return 1;
            if (GetWindow(hwnd, GW_OWNER) != 0) return 1;
            if (state.ClassName != null && !string.Equals(ClassName(hwnd), state.ClassName, StringComparison.Ordinal)) return 1;
            GetWindowRect(hwnd, out var r);
            long area = (long)r.Width * r.Height;
            if (IsWindowVisible(hwnd))
            {
                if (area > state.BestVisibleArea) { state.BestVisibleArea = area; state.BestVisible = hwnd; }
            }
            else if (area > state.BestHiddenArea) { state.BestHiddenArea = area; state.BestHidden = hwnd; }
        }
        catch
        {
            // Never let an exception cross the native callback boundary.
        }
        return 1;
    }
}
