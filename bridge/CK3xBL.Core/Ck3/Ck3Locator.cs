// Finds the running CK3: process "ck3", top-level window class "SDL_app".
using System.Diagnostics;
using CK3xBL.Core.Native;

namespace CK3xBL.Core.Ck3;

public readonly record struct Ck3Window(nint Hwnd, int ProcessId, uint ThreadId);

public interface ICk3Locator
{
    /// <summary>The CK3 main window, or null when CK3 is not running (or has no window yet).</summary>
    Ck3Window? Find();
}

public sealed class Ck3Locator : ICk3Locator
{
    public const string ProcessName = "ck3";
    public const string WindowClass = "SDL_app";

    private readonly object _gate = new();
    private Ck3Window? _cached;

    public Ck3Window? Find()
    {
        lock (_gate)
        {
            // The window handle is stable for the life of the process; re-validate instead of enumerating every time.
            if (_cached is { } c && Win32.IsWindow(c.Hwnd) && Win32.ProcessIdOf(c.Hwnd) == c.ProcessId) return c;
            _cached = null;
            foreach (var p in Process.GetProcessesByName(ProcessName))
            {
                using (p)
                {
                    var windows = Win32.TopLevelWindows(p.Id, WindowClass);
                    if (windows.Count == 0) continue;
                    var hwnd = windows[0];
                    _cached = new Ck3Window(hwnd, p.Id, Win32.ThreadIdOf(hwnd));
                    return _cached;
                }
            }
            return null;
        }
    }

    public static bool IsRunning()
    {
        var ps = Process.GetProcessesByName(ProcessName);
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }
}
