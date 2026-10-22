using CK3xBL.Ux.Interop;
using static CK3xBL.Ux.Interop.Native;

namespace CK3xBL.Ux;

/// <summary>
/// Puts the game windows on one stage: CK3 runs "windowed" at the monitor's
/// resolution and the bridge strips its caption so it becomes a borderless window covering the monitor, like Bannerlord's
/// "Borderless Fullscreen". Original styles and rects are remembered and restored on exit.
/// Thread-safe; all rects are physical pixels.
/// </summary>
public sealed class WindowStager
{
    private const long FrameStyles = WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
    private const long FrameExStyles = WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME | WS_EX_STATICEDGE;

    private sealed record Saved(int ProcessId, long Style, long ExStyle, Native.RECT Rect);

    private readonly Dictionary<nint, Saved> _saved = new();
    private readonly object _gate = new();

    /// <summary>The full rect of the monitor the window is on (the stage rect).</summary>
    public static ScreenRect MonitorRectOf(IntPtr hwnd) => GameWindows.MonitorRectOf(hwnd);

    /// <summary>True when the window has no caption/frame and covers <paramref name="target"/> exactly.</summary>
    public static bool IsBorderlessAt(IntPtr hwnd, ScreenRect target)
    {
        using var _ = DpiScope.PerMonitorV2();
        if (!IsWindow(hwnd)) return false;
        GetWindowRect(hwnd, out var r);
        return (Style(hwnd) & FrameStyles) == 0 && ScreenRect.From(r) == target;
    }

    /// <summary>
    /// Strips the window frame and fits the window to <paramref name="target"/> without activating it. Remembers the original
    /// style and rect the first time. Returns false when the window is gone or the change did not stick.
    /// </summary>
    public bool MakeBorderless(IntPtr hwnd, ScreenRect target)
    {
        using var _ = DpiScope.PerMonitorV2();
        if (!IsWindow(hwnd) || target.IsEmpty) return false;
        long style = Style(hwnd), ex = ExStyle(hwnd);
        GetWindowRect(hwnd, out var rect);
        lock (_gate) _saved.TryAdd(hwnd, new Saved(ProcessIdOf(hwnd), style, ex, rect));

        long newStyle = style & ~FrameStyles, newEx = ex & ~FrameExStyles;
        if (newStyle != style) SetWindowLongPtrW(hwnd, GWL_STYLE, (nint)newStyle);
        if (newEx != ex) SetWindowLongPtrW(hwnd, GWL_EXSTYLE, (nint)newEx);
        SetWindowPos(hwnd, 0, target.Left, target.Top, target.Width, target.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_NOOWNERZORDER);
        bool ok = IsBorderlessAt(hwnd, target);
        UxLog.Write($"stager: borderless {(ok ? "applied" : "did NOT stick")} on {GameWindows.Describe(hwnd)}");
        return ok;
    }

    /// <summary>
    /// Re-applies <see cref="MakeBorderless"/> if the game restored its frame or moved (SDL can re-apply its style, e.g. after a
    /// resolution change in CK3's options). Cheap to call before every swap. Returns true when the window is staged.
    /// </summary>
    public bool EnsureBorderless(IntPtr hwnd, ScreenRect target) =>
        IsBorderlessAt(hwnd, target) || MakeBorderless(hwnd, target);

    /// <summary>
    /// The swap, with the overlay opaque: raise <paramref name="show"/> in the normal band, activate it with the ladder, release
    /// any cursor clip (Bannerlord clips the cursor in battle), and push <paramref name="hide"/> to the bottom.
    /// Returns true when <paramref name="show"/> ended up in the foreground.
    /// </summary>
    public static bool Swap(IntPtr show, IntPtr hide, Func<int, bool>? isTrustedProcess = null)
    {
        if (IsWindow(show)) SetWindowPos(show, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        bool foreground = Foreground.Activate(show, isTrustedProcess);
        ReleaseCursorClip();
        if (hide != 0 && IsWindow(hide)) SetWindowPos(hide, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        return foreground;
    }

    /// <summary>Releases any cursor confinement (never leave the cursor clipped to a window we just hid).</summary>
    public static void ReleaseCursorClip() => ClipCursor(0);

    /// <summary>Restores the original style and rect of one window (if it was staged and still belongs to the same process).</summary>
    public void Restore(IntPtr hwnd)
    {
        Saved? saved;
        lock (_gate)
        {
            if (!_saved.Remove(hwnd, out saved)) return;
        }
        RestoreCore(hwnd, saved);
    }

    /// <summary>Restores every staged window (bridge exit and crash handlers). Idempotent.</summary>
    public void RestoreAll()
    {
        List<KeyValuePair<nint, Saved>> all;
        lock (_gate)
        {
            all = _saved.ToList();
            _saved.Clear();
        }
        foreach (var (hwnd, saved) in all) RestoreCore(hwnd, saved);
    }

    private static void RestoreCore(nint hwnd, Saved saved)
    {
        using var _ = DpiScope.PerMonitorV2();
        // A recycled handle could belong to another process by now: only touch the window we changed.
        if (!IsWindow(hwnd) || ProcessIdOf(hwnd) != saved.ProcessId) return;
        SetWindowLongPtrW(hwnd, GWL_STYLE, (nint)saved.Style);
        // The topmost bit is z-order state owned by SetWindowPos; keep whatever it is now rather than writing it back.
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, (nint)((saved.ExStyle & ~WS_EX_TOPMOST) | (ExStyle(hwnd) & WS_EX_TOPMOST)));
        SetWindowPos(hwnd, 0, saved.Rect.Left, saved.Rect.Top, saved.Rect.Width, saved.Rect.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_NOOWNERZORDER);
        UxLog.Write($"stager: restored {GameWindows.Describe(hwnd)}");
    }
}
