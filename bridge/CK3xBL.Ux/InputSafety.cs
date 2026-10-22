using System.Diagnostics;
using System.Runtime.InteropServices;
using static CK3xBL.Ux.Interop.Native;

namespace CK3xBL.Ux;

/// <summary>The current foreground window and its process (a snapshot).</summary>
public sealed record ForegroundInfo(IntPtr Hwnd, int ProcessId, string ProcessName, string WindowClass, string Title)
{
    public bool IsNone => Hwnd == 0;
}

/// <summary>
/// Read-only input checks for result delivery: inject into a game only after
/// a player click, with no key or button held, the game verifiably in the foreground, and a quiet moment of input.
/// </summary>
public static class InputSafety
{
    private static readonly int[] Modifiers = [0x10, 0x11, 0x12, 0x5B, 0x5C]; // Shift, Ctrl, Alt, LWin, RWin

    /// <summary>Virtual keys currently held down: keyboard keys and mouse buttons (VK 0x01-0xFE).</summary>
    public static IReadOnlyList<int> HeldKeys()
    {
        var held = new List<int>();
        for (int vk = 0x01; vk <= 0xFE; vk++)
        {
            // Generic Shift/Ctrl/Alt (0x10-0x12) duplicate their left/right variants; VK_PACKET (0xE7) is synthetic.
            if (vk is 0x10 or 0x11 or 0x12 or 0xE7) continue;
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) held.Add(vk);
        }
        return held;
    }

    /// <summary>True when any key or mouse button is down.</summary>
    public static bool AnyKeyOrButtonDown() => HeldKeys().Count > 0;

    /// <summary>True when Shift, Ctrl, Alt or a Windows key is down (these corrupt injected chords and text).</summary>
    public static bool AnyModifierDown() => Modifiers.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    /// <summary>
    /// Time since the last input in this session (GetLastInputInfo). Coarse: it includes injected input, our own too. For
    /// physical input only, use <see cref="InputGuard.PhysicalIdle"/>.
    /// </summary>
    public static TimeSpan LastInputIdle()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    /// <summary>The foreground window, its process and class.</summary>
    public static ForegroundInfo GetForeground()
    {
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0) return new ForegroundInfo(0, 0, "", "", "");
        int pid = ProcessIdOf(hwnd);
        return new ForegroundInfo(hwnd, pid, TryProcessName(pid) ?? "", ClassName(hwnd), WindowTitle(hwnd));
    }

    /// <summary>True when the foreground window is <paramref name="hwnd"/> itself.</summary>
    public static bool IsForeground(IntPtr hwnd) => hwnd != 0 && GetForegroundWindow() == hwnd;

    /// <summary>True when the foreground window belongs to <paramref name="processId"/>.</summary>
    public static bool IsForegroundProcess(int processId)
    {
        nint fg = GetForegroundWindow();
        return fg != 0 && ProcessIdOf(fg) == processId;
    }

    /// <summary>
    /// Waits until no key or button is held and input has been quiet for <paramref name="quiet"/> (physical input when a
    /// <paramref name="guard"/> is given, any input otherwise). Returns false on timeout.
    /// </summary>
    public static async Task<bool> WaitForQuietAsync(TimeSpan quiet, TimeSpan timeout, InputGuard? guard, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            TimeSpan idle = guard?.PhysicalIdle ?? LastInputIdle();
            if (!AnyKeyOrButtonDown() && idle >= quiet) return true;
            if (sw.Elapsed >= timeout) return false;
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The keyboard layout (HKL) active for the window's thread. Record it before a chord burst.</summary>
    public static IntPtr GetKeyboardLayoutOf(IntPtr hwnd) =>
        hwnd == 0 ? 0 : GetKeyboardLayout(GetWindowThreadProcessId(hwnd, out _));

    /// <summary>
    /// Asks the window to switch back to <paramref name="layout"/> (WM_INPUTLANGCHANGEREQUEST) if it changed. Holding
    /// Ctrl+Shift+Alt can trip the Windows input-language hotkeys (seen in testing: an unexpected switch to Russian), so the
    /// delivery restores the layout after each burst. Returns true when no change was needed or the request was posted.
    /// </summary>
    public static bool RestoreKeyboardLayout(IntPtr hwnd, IntPtr layout)
    {
        if (hwnd == 0 || layout == 0) return false;
        if (GetKeyboardLayoutOf(hwnd) == layout) return true;
        UxLog.Write($"input: keyboard layout of 0x{hwnd:X} changed; requesting 0x{layout:X} back");
        return PostMessageW(hwnd, WM_INPUTLANGCHANGEREQUEST, 0, layout);
    }
}

/// <summary>
/// Low-level keyboard and mouse hooks on a dedicated thread: tracks the last PHYSICAL (non-injected) input and can swallow
/// physical presses while the bridge injects into a game, so the player cannot interleave keys or click focus away.
///
/// Hardening: only key-downs, button-downs and wheel turns are swallowed; releases always pass, so a key held before the
/// block can never get stuck down. Blocking always switches itself off after <see cref="MaxBlock"/> (callbacks must stay far
/// below LowLevelHooksTimeout, and the player must never feel locked out). Install it only around transitions: an
/// always-on low-level hook adds latency to every input event of the whole session.
///
/// Degrades conservatively: if a hook failed to install, or Windows silently removed it (it does that to a hook whose
/// callback overran LowLevelHooksTimeout, e.g. during a long GC pause), <see cref="PhysicalIdle"/> falls back to
/// <see cref="InputSafety.LastInputIdle"/>, which also counts injected input, so a quiet-input wait never passes while the
/// player is typing. A removed hook is detected when GetLastInputInfo reports input the hooks have not seen for longer than
/// any live hook could take to see it.
/// </summary>
public sealed class InputGuard : IDisposable
{
    /// <summary>LowLevelHooksTimeout is capped at 1 s (Windows 10 1709+); input unseen for longer means the hooks are gone.</summary>
    private const int HookLostAfterMs = 1500;
    /// <summary>GetTickCount granularity: a hook callback stamped in the same tick as the input it reports.</summary>
    private const int TickSlackMs = 20;

    [ThreadStatic] private static InputGuard? t_instance;

    private readonly Thread? _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private nint _keyboardHook, _mouseHook;
    private long _lastPhysicalTick = Environment.TickCount64;
    private int _lastHookEventTick = Environment.TickCount;   // any event, injected or not; same 32-bit clock as GetLastInputInfo
    private readonly object _healthGate = new();
    private int? _unseenInputTick;                            // newest input GetLastInputInfo reported that the hooks had not seen
    private volatile bool _hooksLost;
    private readonly object _blockGate = new();
    private readonly List<InputBlock> _blocks = new();
    private long _blockUntilTick;                             // latest deadline of the live blocks (read by the hook callbacks)
    private int _swallowed;

    /// <summary>Safety cap for one <see cref="BlockPhysicalInput"/> call.</summary>
    public static readonly TimeSpan MaxBlock = TimeSpan.FromSeconds(4);

    public InputGuard() : this(installHooks: true) { }

    /// <summary>Self-test seam: <paramref name="installHooks"/> false gives a guard without hooks (it never sees or blocks input).</summary>
    internal InputGuard(bool installHooks)
    {
        if (!installHooks) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "ck3xbl-input-guard" };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("input guard thread did not start");
        if (_keyboardHook == 0 || _mouseHook == 0)
            UxLog.Write("input: installing the low-level hooks failed; physical idle time falls back to GetLastInputInfo and nothing can be blocked");
    }

    /// <summary>True when both hooks are installed and still receive input.</summary>
    public bool IsActive => HooksAlive();

    /// <summary>
    /// Time since the last physical keyboard or mouse event (moves included). Without working hooks: time since the last input
    /// of any kind (<see cref="InputSafety.LastInputIdle"/>; conservative, as injected input counts too).
    /// </summary>
    public TimeSpan PhysicalIdle => HooksAlive()
        ? TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastPhysicalTick))
        : InputSafety.LastInputIdle();

    /// <summary>Physical presses swallowed since the guard was created.</summary>
    public int SwallowedCount => Volatile.Read(ref _swallowed);

    /// <summary>True while physical presses are being swallowed (some block is live and within its cap).</summary>
    public bool IsBlocking => Environment.TickCount64 < Interlocked.Read(ref _blockUntilTick);

    /// <summary>
    /// Swallows physical presses until the returned block is disposed, or <paramref name="duration"/> (at most
    /// <see cref="MaxBlock"/>) has passed. Blocks may overlap: presses are swallowed while any of them is in force. Injected
    /// input (SendInput) passes; mouse movement passes so the cursor never freezes.
    /// </summary>
    public InputBlock BlockPhysicalInput(TimeSpan? duration = null)
    {
        var span = duration is { } d && d < MaxBlock ? d : MaxBlock;
        var block = new InputBlock(this, Environment.TickCount64 + (long)span.TotalMilliseconds);
        lock (_blockGate)
        {
            _blocks.Add(block);
            UpdateBlockDeadlineLocked();
        }
        if (!HooksAlive()) UxLog.Write("input: BlockPhysicalInput without working hooks; physical input is NOT blocked");
        return block;
    }

    /// <summary>One <see cref="BlockPhysicalInput"/> request. Dispose it to end it early.</summary>
    public sealed class InputBlock : IDisposable
    {
        private readonly InputGuard _guard;
        private int _disposed;

        internal InputBlock(InputGuard guard, long deadlineTick)
        {
            _guard = guard;
            DeadlineTick = deadlineTick;
        }

        internal long DeadlineTick { get; }

        /// <summary>True until the block is disposed or reaches its cap.</summary>
        public bool IsActive => Volatile.Read(ref _disposed) == 0 && Environment.TickCount64 < DeadlineTick;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _guard.EndBlock(this);
        }
    }

    private void EndBlock(InputBlock block)
    {
        lock (_blockGate)
        {
            _blocks.Remove(block);
            UpdateBlockDeadlineLocked();
        }
    }

    private void UpdateBlockDeadlineLocked() =>
        Interlocked.Exchange(ref _blockUntilTick, _blocks.Count == 0 ? 0 : _blocks.Max(b => b.DeadlineTick));

    /// <summary>
    /// False when a hook is missing or Windows removed it silently: the newest input GetLastInputInfo reports has gone unseen by
    /// the hooks for longer than <see cref="HookLostAfterMs"/>. A live hook sees every event within LowLevelHooksTimeout, so a
    /// short mismatch (the callback is still running) is not a loss. Once lost, the hooks are never trusted again.
    /// </summary>
    private bool HooksAlive()
    {
        if (_keyboardHook == 0 || _mouseHook == 0 || _hooksLost) return false;
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return true;   // cannot tell; keep trusting the hooks
        int lastInput = unchecked((int)info.dwTime), seen = Volatile.Read(ref _lastHookEventTick), now = Environment.TickCount;
        lock (_healthGate)
        {
            if (unchecked(lastInput - seen) <= TickSlackMs)
            {
                _unseenInputTick = null;                  // the hooks have seen the newest input
                return true;
            }
            // Track the oldest input still unseen; once the hooks catch up with it, track the newest one instead.
            if (_unseenInputTick is not { } unseen || unchecked(seen - unseen) >= -TickSlackMs) _unseenInputTick = unseen = lastInput;
            if (unchecked(now - unseen) <= HookLostAfterMs) return true;
            _hooksLost = true;
        }
        UxLog.Write("input: the low-level hooks stopped receiving input (Windows removes hooks that overrun LowLevelHooksTimeout); " +
                    "falling back to GetLastInputInfo, and physical input can no longer be blocked");
        return false;
    }

    private unsafe void Run()
    {
        t_instance = this;
        _threadId = GetCurrentThreadId();
        EnsureMessageQueue();
        nint module = GetModuleHandleW(0);
        _keyboardHook = SetWindowsHookExW(WH_KEYBOARD_LL, &KeyboardProc, module, 0);
        _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, &MouseProc, module, 0);
        _ready.Set();
        try
        {
            while (GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                TranslateMessage(msg);
                DispatchMessageW(msg);
            }
        }
        finally
        {
            if (_keyboardHook != 0) UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != 0) UnhookWindowsHookEx(_mouseHook);
            _keyboardHook = _mouseHook = 0;
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe nint KeyboardProc(int code, nint wParam, nint lParam)
    {
        var self = t_instance;
        if (code >= 0 && self != null)
        {
            Volatile.Write(ref self._lastHookEventTick, Environment.TickCount);
            var k = (KBDLLHOOKSTRUCT*)lParam;
            if ((k->flags & LLKHF_INJECTED) == 0)
            {
                Interlocked.Exchange(ref self._lastPhysicalTick, Environment.TickCount64);
                bool isDown = (k->flags & LLKHF_UP) == 0;
                if (isDown && self.IsBlocking) { Interlocked.Increment(ref self._swallowed); return 1; }
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    private static unsafe nint MouseProc(int code, nint wParam, nint lParam)
    {
        var self = t_instance;
        if (code >= 0 && self != null)
        {
            Volatile.Write(ref self._lastHookEventTick, Environment.TickCount);
            var m = (MSLLHOOKSTRUCT*)lParam;
            if ((m->flags & LLMHF_INJECTED) == 0)
            {
                Interlocked.Exchange(ref self._lastPhysicalTick, Environment.TickCount64);
                // Downs and wheel are swallowed; moves and button releases always pass.
                const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207, WM_MOUSEWHEEL = 0x20A,
                    WM_XBUTTONDOWN = 0x20B, WM_MOUSEHWHEEL = 0x20E;
                bool press = wParam is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_MOUSEWHEEL or WM_XBUTTONDOWN or WM_MOUSEHWHEEL;
                if (press && self.IsBlocking) { Interlocked.Increment(ref self._swallowed); return 1; }
            }
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    public void Dispose()
    {
        lock (_blockGate)
        {
            _blocks.Clear();
            UpdateBlockDeadlineLocked();
        }
        if (_threadId != 0) PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        _thread?.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
