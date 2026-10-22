// P/Invoke surface for the transition UX (user32, gdi32, kernel32, dwmapi). x64 and x86 safe (nint everywhere).
// Only what this component uses is declared here; constants carry their Win32 names so they can be looked up.
using System.Runtime.InteropServices;
using System.Text;

namespace CK3xBL.Ux.Interop;

internal static unsafe class Native
{
    // ------------------------------------------------------------------ structs
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
    }

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public POINT pt; public uint lPrivate; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public int cbSize; public uint style; public nint lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public nint hInstance; public nint hIcon; public nint hCursor; public nint hbrBackground;
        public char* lpszMenuName; public char* lpszClassName; public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    // SendInput (sizes must match the native union exactly; MOUSEINPUT is the largest member).
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION U; }
    [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)] public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public nint dwExtraInfo; }

    // ------------------------------------------------------------------ constants
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;

    public const long WS_POPUP = 0x80000000L, WS_VISIBLE = 0x10000000L, WS_CAPTION = 0x00C00000L, WS_THICKFRAME = 0x00040000L,
        WS_SYSMENU = 0x00080000L, WS_MINIMIZEBOX = 0x00020000L, WS_MAXIMIZEBOX = 0x00010000L, WS_BORDER = 0x00800000L,
        WS_DLGFRAME = 0x00400000L;

    public const long WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000,
        WS_EX_NOACTIVATE = 0x08000000, WS_EX_WINDOWEDGE = 0x100, WS_EX_CLIENTEDGE = 0x200, WS_EX_DLGMODALFRAME = 0x1,
        WS_EX_STATICEDGE = 0x20000;

    public static readonly nint HWND_TOP = 0, HWND_BOTTOM = 1, HWND_TOPMOST = -1, HWND_NOTOPMOST = -2;

    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20,
        SWP_SHOWWINDOW = 0x40, SWP_NOOWNERZORDER = 0x200;

    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;

    public const uint MONITOR_DEFAULTTONEAREST = 2, MONITOR_DEFAULTTOPRIMARY = 1;
    public const uint MONITORINFOF_PRIMARY = 1;

    public const uint WM_DESTROY = 0x0002, WM_TIMER = 0x0113, WM_QUIT = 0x0012, WM_NCHITTEST = 0x0084, WM_MOUSEACTIVATE = 0x0021,
        WM_APP = 0x8000, WM_INPUTLANGCHANGEREQUEST = 0x0050, WM_MOUSEMOVE = 0x0200, WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    public const nint HTTRANSPARENT = -1, MA_NOACTIVATE = 3;

    public const uint PM_NOREMOVE = 0x0, PM_REMOVE = 0x1;
    public const uint QS_ALLINPUT = 0x04FF, MWMO_INPUTAVAILABLE = 0x0004;
    public const uint WAIT_OBJECT_0 = 0, WAIT_FAILED = 0xFFFFFFFF, INFINITE = 0xFFFFFFFF;

    public const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;
    public const uint ULW_ALPHA = 2;
    public const int BI_RGB = 0, DIB_RGB_COLORS = 0;

    public const uint PW_CLIENTONLY = 0x1, PW_RENDERFULLCONTENT = 0x2;

    public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3, DWMWA_EXCLUDED_FROM_PEEK = 12, DWMWA_CLOAKED = 14;

    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x2, KEYEVENTF_SCANCODE = 0x8;
    public const ushort VK_MENU = 0x12;
    /// <summary>Unassigned virtual key used as a "menu mask": tapping it while Alt is held stops a bare Alt release from
    /// opening the target's window menu (the same trick AutoHotkey uses by default).</summary>
    public const ushort VK_MASK_UNASSIGNED = 0xE8;

    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public const uint LLKHF_INJECTED = 0x10, LLKHF_UP = 0x80, LLMHF_INJECTED = 0x1;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;

    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003;

    // ------------------------------------------------------------------ user32: windows and messages
    [DllImport("user32.dll", SetLastError = true)] public static extern ushort RegisterClassExW(in WNDCLASSEXW wc);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool PeekMessageW(out MSG msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool TranslateMessage(in MSG msg);
    [DllImport("user32.dll")] public static extern nint DispatchMessageW(in MSG msg);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool PostThreadMessageW(uint threadId, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint milliseconds, uint wakeMask, uint flags);
    [DllImport("user32.dll", SetLastError = true)] public static extern nuint SetTimer(nint hwnd, nuint id, uint elapse, nint timerProc);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool KillTimer(nint hwnd, nuint id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateLayeredWindow(nint hwnd, nint hdcDst, POINT* pptDst, SIZE* psize, nint hdcSrc, POINT* pptSrc,
        uint crKey, BLENDFUNCTION* pblend, uint flags);

    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] public static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] public static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsHungAppWindow(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetClientRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool ClientToScreen(nint hwnd, ref POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(nint hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(nint hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern nint GetWindow(nint hwnd, uint cmd);
    public const uint GW_OWNER = 4;
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    // ------------------------------------------------------------------ user32: monitors and DPI
    [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] public static extern nint MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern nint SetThreadDpiAwarenessContext(nint context);

    // ------------------------------------------------------------------ user32: foreground and input
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool altTab);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT* inputs, int size);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] public static extern nint GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll")] public static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool ClipCursor(nint rect);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookExW(int idHook, delegate* unmanaged<int, nint, nint, nint> proc, nint module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module,
        delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool UnhookWinEvent(nint hook);

    // ------------------------------------------------------------------ user32/gdi32: drawing
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] public static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern nint CreateDIBSection(nint hdc, in BITMAPINFOHEADER bmi, uint usage, out nint bits, nint section, uint offset);

    // ------------------------------------------------------------------ kernel32 / dwmapi
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] public static extern nint GetModuleHandleW(nint name);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWaitableTimer(nint timer, in long dueTime, int period, nint completion, nint arg, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool CancelWaitableTimer(nint timer);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool CloseHandle(nint handle);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(nint hwnd, int attr, in int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);

    // ------------------------------------------------------------------ helpers
    public static long Style(nint h) => (long)GetWindowLongPtrW(h, GWL_STYLE);
    public static long ExStyle(nint h) => (long)GetWindowLongPtrW(h, GWL_EXSTYLE);
    public static bool IsTopmost(nint h) => (ExStyle(h) & WS_EX_TOPMOST) != 0;

    public static string ClassName(nint h) { var sb = new StringBuilder(256); GetClassNameW(h, sb, sb.Capacity); return sb.ToString(); }
    public static string WindowTitle(nint h) { var sb = new StringBuilder(512); GetWindowTextW(h, sb, sb.Capacity); return sb.ToString(); }
    public static int ProcessIdOf(nint h) { GetWindowThreadProcessId(h, out var pid); return (int)pid; }

    /// <summary>The process's name, or null when it has exited (also between opening it and reading the name) or cannot be read.</summary>
    public static string? TryProcessName(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    /// <summary>DWM cloak state (0 = not cloaked), or -1 when it cannot be read.</summary>
    public static int Cloaked(nint h) => DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int v, sizeof(int)) == 0 ? v : -1;

    public static MONITORINFO MonitorInfo(nint monitor)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(monitor, ref mi);
        return mi;
    }

    /// <summary>Makes sure the calling thread owns a message queue (AttachThreadInput and PostThreadMessage need one).</summary>
    public static void EnsureMessageQueue() => PeekMessageW(out _, 0, 0, 0, PM_NOREMOVE);
}
