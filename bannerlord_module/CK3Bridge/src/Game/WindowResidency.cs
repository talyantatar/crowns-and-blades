using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TaleWorlds.Engine.Options;

namespace CK3Bridge
{
    /// <summary>
    /// The Bannerlord window seen from inside its own process:
    ///  - self-cloak with DWMWA_CLOAK (cross-process cloak is E_ACCESSDENIED, in-process works; verified experiment);
    ///  - a 30 fps native frame limiter while cloaked, because hiding never throttles a flip-model presenter.
    ///    NativeOptions.SetConfig + ApplyConfigChanges only; never NativeOptions.SaveConfig. The engine rewrites
    ///    engine_config.txt at exit, so the user's limiter is put back before every exit path;
    ///  - Windows 11 power throttling opt-out while a battle loads or plays;
    ///  - handing the foreground to CK3 (legal because Bannerlord is the foreground process at that moment).
    /// All members run on the main thread.
    /// </summary>
    internal static class WindowResidency
    {
        private const int DwmwaCloak = 13;
        private const int ProcessPowerThrottling = 4;
        private const uint PowerThrottlingCurrentVersion = 1;
        private const uint PowerThrottlingExecutionSpeed = 0x1;
        private const uint PowerThrottlingIgnoreTimerResolution = 0x4;
        private const int AsfwAny = -1;
        public const int BackgroundFrameLimit = 30;

        private static readonly uint OwnProcessId = (uint)Process.GetCurrentProcess().Id;
        private static IntPtr _hwnd;
        private static float _savedFrameLimiter = -1f;

        public static bool IsCloaked { get; private set; }

        public static bool IsForeground
        {
            get
            {
                IntPtr hwnd = Hwnd;
                return hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;
            }
        }

        /// <summary>
        /// The foreground window belongs to this process (the game window or one of its dialogs). Used before a battle is
        /// shown, so it is never revealed behind another application.
        /// </summary>
        public static bool IsOwnProcessForeground
        {
            get
            {
                IntPtr foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero) return false;
                GetWindowThreadProcessId(foreground, out uint pid);
                return pid == OwnProcessId;
            }
        }

        /// <summary>The engine's render window: class "TWApplication" owned by this process.</summary>
        public static IntPtr Hwnd
        {
            get
            {
                if (_hwnd != IntPtr.Zero && IsWindow(_hwnd)) return _hwnd;
                _hwnd = IntPtr.Zero;
                EnumWindows((h, _) =>
                {
                    GetWindowThreadProcessId(h, out uint pid);
                    if (pid != OwnProcessId) return true;
                    var name = new StringBuilder(64);
                    GetClassNameW(h, name, name.Capacity);
                    if (name.ToString() != "TWApplication") return true;
                    _hwnd = h;
                    return false;
                }, IntPtr.Zero);
                return _hwnd;
            }
        }

        /// <summary>
        /// Stops DWM from swapping in a "Not Responding" ghost window (a different HWND) when a long load blocks the
        /// message pump for more than 5 s; the bridge's window tracking would lose us.
        /// </summary>
        public static void DisableGhosting() => DisableProcessWindowsGhosting();

        /// <summary>"bg": cloak and cap the frame rate.</summary>
        public static void EnterBackground()
        {
            IntPtr hwnd = Hwnd;
            if (hwnd != IntPtr.Zero)
            {
                int on = 1;
                int hr = DwmSetWindowAttribute(hwnd, DwmwaCloak, ref on, sizeof(int));
                IsCloaked = hr == 0;
                if (hr != 0) BridgeIo.Log($"cloak failed hr=0x{hr:X8}");
            }
            if (_savedFrameLimiter < 0f)
            {
                _savedFrameLimiter = NativeOptions.GetConfig(NativeOptions.NativeOptionsType.FrameLimiter);
                NativeOptions.SetConfig(NativeOptions.NativeOptionsType.FrameLimiter, BackgroundFrameLimit);
                NativeOptions.ApplyConfigChanges(false);
            }
        }

        /// <summary>"fg": uncloak and give the user's frame limiter back.</summary>
        public static void EnterForeground()
        {
            IntPtr hwnd = Hwnd;
            if (hwnd != IntPtr.Zero)
            {
                int off = 0;
                int hr = DwmSetWindowAttribute(hwnd, DwmwaCloak, ref off, sizeof(int));
                if (hr == 0) IsCloaked = false;
                else BridgeIo.Log($"uncloak failed hr=0x{hr:X8}");
            }
            RestoreFrameLimiter(applyNow: true);
        }

        /// <summary>
        /// Puts the user's frame limiter back in the native config. <paramref name="applyNow"/> is false on the exit
        /// path, where only the in-memory value matters (it is what the engine writes to engine_config.txt).
        /// </summary>
        public static void RestoreFrameLimiter(bool applyNow)
        {
            if (_savedFrameLimiter < 0f) return;
            NativeOptions.SetConfig(NativeOptions.NativeOptionsType.FrameLimiter, _savedFrameLimiter);
            if (applyNow) NativeOptions.ApplyConfigChanges(false);
            _savedFrameLimiter = -1f;
        }

        /// <summary>
        /// Opts the process out of EcoQoS and timer-resolution throttling while a battle loads or runs behind CK3, and
        /// gives control back to Windows when idle.
        /// </summary>
        public static void SetHighQoS(bool high)
        {
            var state = new ProcessPowerThrottlingState
            {
                Version = PowerThrottlingCurrentVersion,
                ControlMask = high ? PowerThrottlingExecutionSpeed | PowerThrottlingIgnoreTimerResolution : 0u,
                StateMask = 0u,
            };
            if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf(typeof(ProcessPowerThrottlingState))))
                BridgeIo.Log($"SetProcessInformation(power throttling, high={high}) failed: {Marshal.GetLastWin32Error()}");
        }

        /// <summary>
        /// "handoff_to": Bannerlord is the foreground process at the end of a battle, so SetForegroundWindow is allowed.
        /// The target process is allowed to take the foreground too; if the call still
        /// fails, any process may (the bridge's activation ladder then succeeds on its first rung).
        /// </summary>
        public static bool HandoffTo(IntPtr target)
        {
            if (!IsWindow(target)) return false;
            GetWindowThreadProcessId(target, out uint targetPid);
            AllowSetForegroundWindow((int)targetPid);
            ClipCursor(IntPtr.Zero);   // Bannerlord clips the cursor in battle; never hand over a clipped cursor
            if (SetForegroundWindow(target)) return true;
            AllowSetForegroundWindow(AsfwAny);
            return false;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessPowerThrottlingState
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClipCursor(IntPtr rect);

        [DllImport("user32.dll")]
        private static extern void DisableProcessWindowsGhosting();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref ProcessPowerThrottlingState information, int size);
    }
}
