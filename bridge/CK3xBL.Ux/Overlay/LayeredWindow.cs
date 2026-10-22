using System.Runtime.InteropServices;
using CK3xBL.Ux.Interop;
using static CK3xBL.Ux.Interop.Native;

namespace CK3xBL.Ux.Overlay;

/// <summary>
/// One overlay surface: a topmost, layered, click-through, never-activating popup driven by UpdateLayeredWindow.
///
/// Why raw Win32 and not a WinForms Form: WinForms implements <c>Opacity</c> with SetLayeredWindowAttributes, and when
/// WS_EX_LAYERED is forced through CreateParams it never calls it for <c>Opacity = 1.0</c> (the verified bug), leaving the
/// layered window invisible. Mixing the two APIs is also illegal: once SetLayeredWindowAttributes has been called,
/// UpdateLayeredWindow fails. Here every update, content and alpha alike, goes through UpdateLayeredWindow, and the window is
/// never shown before its first UpdateLayeredWindow call.
///
/// Content is uploaded once (the system keeps its own copy, so the source DIB can be freed right after); fades change only the
/// constant alpha (a blend-only UpdateLayeredWindow call, no pixel upload), so a 60 fps full-screen fade costs almost nothing.
/// Must be created, used and destroyed on the overlay thread.
/// </summary>
internal sealed unsafe class LayeredWindow : IDisposable
{
    private const string WindowClass = "CK3xBL.TransitionOverlay";
    private static readonly object RegistrationGate = new();
    private static bool s_registered;

    public const long OverlayExStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;

    /// <summary>Self-test seam: when false, windows are created, filled and faded but never shown on screen.</summary>
    internal static volatile bool ShowOnScreen = true;

    public nint Hwnd { get; private set; }
    public string Name { get; }
    public ScreenRect Bounds { get; }
    /// <summary>True: content alpha is per pixel (premultiplied BGRA). False: content is opaque, alpha is constant only.</summary>
    public bool PerPixelAlpha { get; }
    public byte Alpha { get; private set; }
    public bool HasContent { get; private set; }

    private LayeredWindow(nint hwnd, string name, ScreenRect bounds, bool perPixelAlpha)
    {
        Hwnd = hwnd; Name = name; Bounds = bounds; PerPixelAlpha = perPixelAlpha;
    }

    /// <summary>Creates the (hidden) window covering <paramref name="bounds"/> in physical screen pixels.</summary>
    public static LayeredWindow Create(string name, ScreenRect bounds, bool perPixelAlpha)
    {
        EnsureClassRegistered();
        nint hwnd = CreateWindowExW((uint)OverlayExStyle, WindowClass, "CK3xBL " + name, unchecked((uint)WS_POPUP),
            bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0, 0, GetModuleHandleW(0), 0);
        if (hwnd == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"CreateWindowEx({name}) failed");
        // We animate alpha ourselves; DWM's own show/hide animation would fight it. Also keep it out of Aero Peek.
        DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, 1, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_EXCLUDED_FROM_PEEK, 1, sizeof(int));
        return new LayeredWindow(hwnd, name, bounds, perPixelAlpha);
    }

    /// <summary>
    /// Uploads <paramref name="surface"/> (placed at the window's top-left; it must match the window size) with the given
    /// constant alpha. The surface can be disposed afterwards.
    /// </summary>
    public bool SetContent(DibSurface surface, byte alpha)
    {
        if (Hwnd == 0) return false;
        if (surface.Width != Bounds.Width || surface.Height != Bounds.Height)
            throw new ArgumentException($"surface {surface.Width}x{surface.Height} does not match window {Bounds}");
        nint dc = CreateCompatibleDC(0);
        if (dc == 0) return false;
        try
        {
            nint old = SelectObject(dc, surface.Handle);
            var dst = new POINT { X = Bounds.Left, Y = Bounds.Top };
            var size = new SIZE { cx = Bounds.Width, cy = Bounds.Height };
            var src = new POINT();
            var blend = Blend(alpha);
            bool ok = UpdateLayeredWindow(Hwnd, 0, &dst, &size, dc, &src, 0, &blend, ULW_ALPHA);
            SelectObject(dc, old);
            if (!ok) { UxLog.Write($"overlay {Name}: UpdateLayeredWindow(content) failed, error {Marshal.GetLastWin32Error()}"); return false; }
            Alpha = alpha;
            HasContent = true;
            return true;
        }
        finally { DeleteDC(dc); }
    }

    /// <summary>Changes only the constant alpha (no pixel upload).</summary>
    public void SetAlpha(byte alpha)
    {
        if (Hwnd == 0 || !HasContent || alpha == Alpha) return;
        var blend = Blend(alpha);
        if (UpdateLayeredWindow(Hwnd, 0, null, null, 0, null, 0, &blend, ULW_ALPHA)) Alpha = alpha;
    }

    /// <summary>Shows the window at the top of the topmost band without activating it (and without taking focus).</summary>
    public void ShowTopmost()
    {
        if (Hwnd == 0 || !ShowOnScreen) return;
        SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER);
    }

    public bool IsVisible => Hwnd != 0 && IsWindowVisible(Hwnd);

    public void Dispose()
    {
        if (Hwnd == 0) return;
        // Hide first so the topmost window is gone from composition even if destruction is deferred.
        ShowWindow(Hwnd, SW_HIDE);
        DestroyWindow(Hwnd);
        Hwnd = 0;
    }

    private BLENDFUNCTION Blend(byte alpha) => new()
    {
        BlendOp = AC_SRC_OVER,
        SourceConstantAlpha = alpha,
        // Opaque layers ignore the (undefined) alpha bytes of a PrintWindow capture; text layers use premultiplied alpha.
        AlphaFormat = PerPixelAlpha ? AC_SRC_ALPHA : (byte)0,
    };

    private static void EnsureClassRegistered()
    {
        lock (RegistrationGate)
        {
            if (s_registered) return;
            fixed (char* name = WindowClass)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = sizeof(WNDCLASSEXW),
                    lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
                    hInstance = GetModuleHandleW(0),
                    lpszClassName = name,
                };
                if (RegisterClassExW(wc) == 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    const int ERROR_CLASS_ALREADY_EXISTS = 1410;
                    if (error != ERROR_CLASS_ALREADY_EXISTS) throw new System.ComponentModel.Win32Exception(error, "RegisterClassEx failed");
                }
            }
            s_registered = true;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        // Belt and braces on top of WS_EX_TRANSPARENT | WS_EX_NOACTIVATE: never hit-testable, never activated by a click.
        if (msg == WM_NCHITTEST) return HTTRANSPARENT;
        if (msg == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
