// Keyboard injection primitives. Everything except typed text goes by SCAN CODE (KEYEVENTF_SCANCODE): the physical key position is
// the same in every keyboard layout (US, German, Russian, ...), so digits, F-keys, Enter and the console key never depend on the
// layout. Text uses KEYEVENTF_UNICODE (VK_PACKET), which bypasses the layout entirely.
using System.Runtime.InteropServices;
using CK3xBL.Core.Native;

namespace CK3xBL.Core.Delivery;

public enum KeyStrokeKind { Scan, Unicode }

public readonly record struct KeyStroke(KeyStrokeKind Kind, ushort Code, bool Up)
{
    public static KeyStroke Down(ushort scan) => new(KeyStrokeKind.Scan, scan, false);
    public static KeyStroke Release(ushort scan) => new(KeyStrokeKind.Scan, scan, true);
    public static KeyStroke CharDown(char c) => new(KeyStrokeKind.Unicode, c, false);
    public static KeyStroke CharUp(char c) => new(KeyStrokeKind.Unicode, c, true);
}

public static class ScanCodes
{
    public const ushort LeftCtrl = 0x1D, LeftShift = 0x2A, LeftAlt = 0x38;
    public const ushort F1 = 0x3B, F2 = 0x3C, F3 = 0x3D, F5 = 0x3F;
    public const ushort Enter = 0x1C, Backspace = 0x0E;
    /// <summary>The key below Esc (` on US, ё on RU): CK3's console toggle (VK_OEM_3 on US layouts; scan code 0x29 on every layout).</summary>
    public const ushort Grave = 0x29;

    /// <summary>Number-row digit scan codes: 1..9 = 0x02..0x0A, 0 = 0x0B.</summary>
    public static ushort Digit(int d) => d == 0 ? (ushort)0x0B : (ushort)(0x01 + d);

    public static ushort ForKeypad(KeypadKey key) => key switch
    {
        >= KeypadKey.D0 and <= KeypadKey.D9 => Digit((int)key),
        KeypadKey.Store => F1,
        KeypadKey.Commit => F2,
        KeypadKey.Reset => F3,
        KeypadKey.Seek => F5,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    /// <summary>The keypad modifiers in press order (released in reverse).</summary>
    public static readonly ushort[] KeypadModifiers = { LeftCtrl, LeftShift, LeftAlt };
}

/// <summary>The OS side of injection (SendInput and friends), behind an interface so delivery logic is testable.</summary>
public interface IKeyInjector
{
    nint ForegroundWindow { get; }
    /// <summary>Sends the strokes as one SendInput batch (no physical input can interleave). False if not all were inserted.</summary>
    bool Send(IReadOnlyList<KeyStroke> strokes);
    /// <summary>True while Shift/Ctrl/Alt/Win or a mouse button is physically or logically down.</summary>
    bool AnyModifierOrButtonHeld();
    nint GetKeyboardLayout(uint threadId);
    /// <summary>Asks the window's thread to switch to the given layout (WM_INPUTLANGCHANGEREQUEST).</summary>
    void RequestKeyboardLayout(nint hwnd, nint hkl);
}

public sealed class Win32KeyInjector : IKeyInjector
{
    private static readonly int InputSize = Marshal.SizeOf<Win32.INPUT>();
    private static readonly int[] HeldCheckVks = { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x01, 0x02, 0x04 }; // Shift Ctrl Alt LWin RWin LMB RMB MMB

    public nint ForegroundWindow => Win32.GetForegroundWindow();

    public bool Send(IReadOnlyList<KeyStroke> strokes)
    {
        if (strokes.Count == 0) return true;
        var inputs = new Win32.INPUT[strokes.Count];
        for (int i = 0; i < strokes.Count; i++)
        {
            var s = strokes[i];
            uint flags = s.Kind == KeyStrokeKind.Unicode ? Win32.KEYEVENTF_UNICODE : Win32.KEYEVENTF_SCANCODE;
            if (s.Up) flags |= Win32.KEYEVENTF_KEYUP;
            inputs[i] = new Win32.INPUT
            {
                type = Win32.INPUT_KEYBOARD,
                U = new Win32.INPUTUNION { ki = new Win32.KEYBDINPUT { wVk = 0, wScan = s.Code, dwFlags = flags } },
            };
        }
        return Win32.SendInput((uint)inputs.Length, inputs, InputSize) == inputs.Length;
    }

    public bool AnyModifierOrButtonHeld() => HeldCheckVks.Any(vk => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0);

    public nint GetKeyboardLayout(uint threadId) => Win32.GetKeyboardLayout(threadId);

    public void RequestKeyboardLayout(nint hwnd, nint hkl) => Win32.PostMessageW(hwnd, Win32.WM_INPUTLANGCHANGEREQUEST, 0, hkl);
}

/// <summary>A lease on physical-input awareness. The low-level hooks behind it exist only while at least one lease is held (an
/// always-on low-level hook would add latency to every input event of the session).</summary>
public interface IInputGuard : IDisposable
{
    /// <summary>Milliseconds since the last PHYSICAL (non-injected) key or mouse event, or since the lease began.</summary>
    long PhysicalIdleMs { get; }
    /// <summary>While true, physical key and button presses are swallowed (releases and mouse movement always pass).</summary>
    bool Blocking { get; set; }
}

public interface IInputGuardSource
{
    IInputGuard Acquire();
}
