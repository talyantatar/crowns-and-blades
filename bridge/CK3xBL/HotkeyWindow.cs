// The global abort hotkey Ctrl+Alt+Shift+End: a message-only window that owns the registration.
using System.Runtime.InteropServices;

namespace CK3xBL;

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    private const uint VK_END = 0x23;
    private const int HotkeyId = 0xC3B1;
    private static readonly nint HWND_MESSAGE = -3;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    public event Action? Pressed;

    /// <summary>False when another application already owns the combination.</summary>
    public bool Registered { get; }

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
        Registered = RegisterHotKey(Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, VK_END);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam == HotkeyId) Pressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Registered) UnregisterHotKey(Handle, HotkeyId);
        DestroyHandle();
    }
}
