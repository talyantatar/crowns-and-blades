// P/Invoke surface for the bridge core (x64 only: the bridge and both games are 64-bit).
// Only the calls the core needs; window choreography (overlay, audio, activation ladder) lives in CK3xBL.Ux.
using System.Runtime.InteropServices;
using System.Text;

namespace CK3xBL.Core.Native;

internal static class Win32
{
    // ------------------------------------------------------------------ SendInput
    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public INPUTUNION U;
    }

    // The union must contain MOUSEINPUT (the largest member) so that sizeof(INPUT) == 40 on x64; SendInput rejects other sizes.
    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nint dwExtraInfo;
    }

    internal const uint INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4, KEYEVENTF_SCANCODE = 0x8;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int vKey);

    // ------------------------------------------------------------------ keyboard layout
    internal const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
    [DllImport("user32.dll")] internal static extern nint GetKeyboardLayout(uint idThread);

    // ------------------------------------------------------------------ windows
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsHungAppWindow(nint hWnd);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint hWnd, out RECT lpRect);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint hWnd, uint uCmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassNameW(nint hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextW(nint hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] internal static extern int GetDlgCtrlID(nint hWnd);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ClipCursor(nint lpRect);

    internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsProc cb, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumChildWindows(nint parent, EnumWindowsProc cb, nint lParam);

    internal const uint GW_OWNER = 4;
    internal const uint WM_COMMAND = 0x0111;
    internal const int BN_CLICKED = 0;
    internal const int IDYES = 6, IDNO = 7;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    // ------------------------------------------------------------------ processes
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessCommandLineInformation = 60;   // PROCESSINFOCLASS, Windows 8.1+

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(nint process, int infoClass, nint info, int length, out int returnLength);

    /// <summary>The command line another process was started with, or null when it cannot be read (gone, or access denied).
    /// Needs only PROCESS_QUERY_LIMITED_INFORMATION, so it works for any process of the same user.</summary>
    internal static string? CommandLineOf(int pid)
    {
        nint process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return null;
        try
        {
            // The first call reports the size (STATUS_INFO_LENGTH_MISMATCH); the result is a UNICODE_STRING whose text follows it.
            NtQueryInformationProcess(process, ProcessCommandLineInformation, 0, 0, out int size);
            if (size <= 0) return null;
            nint buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, size, out _) != 0) return null;
                int bytes = (ushort)Marshal.ReadInt16(buffer);                 // UNICODE_STRING.Length
                nint text = Marshal.ReadIntPtr(buffer, IntPtr.Size);           // UNICODE_STRING.Buffer (after Length, MaximumLength, padding)
                return text == 0 ? "" : Marshal.PtrToStringUni(text, bytes / 2);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(process); }
    }

    // ------------------------------------------------------------------ helpers
    internal static int ProcessIdOf(nint hwnd)
    {
        if (hwnd == 0) return 0;
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    internal static uint ThreadIdOf(nint hwnd) => hwnd == 0 ? 0 : GetWindowThreadProcessId(hwnd, out _);

    internal static string ClassName(nint h)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    internal static string WindowText(nint h)
    {
        var sb = new StringBuilder(512);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Top-level, unowned windows of a process (optionally only visible ones), largest first.</summary>
    internal static List<nint> TopLevelWindows(int pid, string? className = null, bool visibleOnly = false)
    {
        var found = new List<(nint Hwnd, long Area)>();
        EnumWindows((h, _) =>
        {
            if (ProcessIdOf(h) != pid) return true;
            if (GetWindow(h, GW_OWNER) != 0) return true;
            if (visibleOnly && !IsWindowVisible(h)) return true;
            if (className != null && !string.Equals(ClassName(h), className, StringComparison.Ordinal)) return true;
            GetWindowRect(h, out var r);
            found.Add((h, (long)r.Width * r.Height));
            return true;
        }, 0);
        return found.OrderByDescending(f => f.Area).Select(f => f.Hwnd).ToList();
    }

    /// <summary>Every top-level window of a process, owned ones included (dialogs are usually owned).</summary>
    internal static List<nint> AllTopLevelWindows(int pid)
    {
        var list = new List<nint>();
        EnumWindows((h, _) => { if (ProcessIdOf(h) == pid) list.Add(h); return true; }, 0);
        return list;
    }

    internal static List<nint> ChildWindows(nint parent)
    {
        var list = new List<nint>();
        EnumChildWindows(parent, (h, _) => { list.Add(h); return true; }, 0);
        return list;
    }
}
