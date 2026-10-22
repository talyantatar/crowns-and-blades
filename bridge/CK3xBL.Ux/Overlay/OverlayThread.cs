using System.Collections.Concurrent;
using System.Diagnostics;
using static CK3xBL.Ux.Interop.Native;

namespace CK3xBL.Ux.Overlay;

/// <summary>
/// The overlay's own UI thread: STA, Per-Monitor-V2 DPI context, a Win32 message loop, a work queue for calls from other
/// threads, a 60 fps animation clock (high-resolution waitable timer, so frames are not quantised to the 15.6 ms system
/// tick) and a 1 s housekeeping tick. Every overlay window is created and touched only on this thread.
/// </summary>
internal sealed class OverlayThread : IDisposable
{
    public const double FrameIntervalMs = 1000.0 / 60.0;
    private const uint WM_WORK = WM_APP + 1;

    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private uint _nativeThreadId;
    private nint _frameTimer;
    private nuint _housekeepingTimer;   // thread timer: Windows assigns the id (the requested one is ignored when hwnd is null)
    private bool _frameArmed;
    private double _nextFrameMs;
    private Exception? _startupError;
    private volatile bool _stopped;

    /// <summary>Animations driven by this thread's frame clock (use only on this thread).</summary>
    public Animator Animator { get; }

    /// <summary>Raised on this thread about once per second.</summary>
    public event Action? Housekeeping;

    /// <summary>Milliseconds since the thread started (monotonic); the animation time base.</summary>
    public double NowMs => _clock.Elapsed.TotalMilliseconds;

    public bool IsAlive => !_stopped && _thread.IsAlive;

    public OverlayThread()
    {
        Animator = new Animator(() => NowMs, RequestFrame);
        _thread = new Thread(Run) { IsBackground = true, Name = "ck3xbl-overlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException("The overlay thread did not start within 10 s.");
        if (_startupError != null) throw new InvalidOperationException("The overlay thread failed to start.", _startupError);
    }

    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    /// <summary>Runs <paramref name="action"/> on the overlay thread (fire and forget; exceptions are logged).</summary>
    public void Post(Action action)
    {
        if (_stopped) return;
        _work.Enqueue(action);
        PostThreadMessageW(_nativeThreadId, WM_WORK, 0, 0);
    }

    /// <summary>Runs <paramref name="func"/> on the overlay thread and returns its result.</summary>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_stopped) { tcs.SetException(new ObjectDisposedException(nameof(OverlayThread))); return tcs.Task; }
        Post(() =>
        {
            try { tcs.TrySetResult(func()); }
            catch (Exception e) { tcs.TrySetException(e); }
        });
        return tcs.Task;
    }

    public Task InvokeAsync(Action action) => InvokeAsync(() => { action(); return true; });

    private unsafe void Run()
    {
        try
        {
            // Permanent for this thread: windows created here are PMv2, rects are physical pixels.
            SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            _nativeThreadId = GetCurrentThreadId();
            EnsureMessageQueue();
            _frameTimer = CreateWaitableTimerExW(0, 0, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            if (_frameTimer == 0) _frameTimer = CreateWaitableTimerExW(0, 0, 0, TIMER_ALL_ACCESS);   // pre-1803 Windows
            if (_frameTimer == 0) throw new System.ComponentModel.Win32Exception();
            _housekeepingTimer = SetTimer(0, 0, 1000, 0);
        }
        catch (Exception e)
        {
            _startupError = e;
            _stopped = true;
            _ready.Set();
            return;
        }
        _ready.Set();

        try
        {
            nint timer = _frameTimer;
            while (true)
            {
                uint wait = MsgWaitForMultipleObjectsEx(1, &timer, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                if (wait == WAIT_OBJECT_0) OnFrame();
                else if (wait == WAIT_FAILED) Thread.Sleep(1);   // should not happen; never spin hot

                while (PeekMessageW(out var msg, 0, 0, 0, PM_REMOVE))
                {
                    if (msg.message == WM_QUIT) return;
                    if (msg.hwnd == 0 && msg.message == WM_WORK) { DrainWork(); continue; }
                    if (msg.hwnd == 0 && msg.message == WM_TIMER && (nuint)msg.wParam == _housekeepingTimer) { RaiseHousekeeping(); continue; }
                    TranslateMessage(msg);
                    DispatchMessageW(msg);
                }
                // A posted WM_WORK can be dropped if the queue overflows; draining on every wake makes that harmless.
                DrainWork();
            }
        }
        catch (Exception e)
        {
            UxLog.Write($"overlay thread crashed: {e}");
        }
        finally
        {
            _stopped = true;
            if (_housekeepingTimer != 0) KillTimer(0, _housekeepingTimer);
            if (_frameTimer != 0) { CloseHandle(_frameTimer); _frameTimer = 0; }
            // Work queued after the loop ended still runs, so callers waiting on InvokeAsync are released (cleanup paths).
            DrainWork();
        }
    }

    private void DrainWork()
    {
        while (_work.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception e) { UxLog.Write($"overlay work item failed: {e}"); }
        }
    }

    private void RaiseHousekeeping()
    {
        try { Housekeeping?.Invoke(); }
        catch (Exception e) { UxLog.Write($"overlay housekeeping failed: {e}"); }
    }

    /// <summary>Arms the frame timer if it is idle (called by the animator when a fade starts).</summary>
    private void RequestFrame()
    {
        if (_frameArmed || _frameTimer == 0) return;
        _nextFrameMs = NowMs + FrameIntervalMs;
        ArmTimer(FrameIntervalMs);
    }

    private void OnFrame()
    {
        _frameArmed = false;
        bool more;
        try { more = Animator.Tick(); }
        catch (Exception e) { UxLog.Write($"overlay animation tick failed: {e}"); more = false; }
        if (!more) return;
        // Keep a steady cadence: schedule against the ideal timeline and skip frames we missed rather than bunching up.
        double now = NowMs;
        _nextFrameMs += FrameIntervalMs;
        if (_nextFrameMs <= now) _nextFrameMs = now + FrameIntervalMs;
        ArmTimer(_nextFrameMs - now);
    }

    private void ArmTimer(double delayMs)
    {
        long due = -(long)Math.Max(1, delayMs * 10_000);   // relative, in 100 ns units
        _frameArmed = SetWaitableTimer(_frameTimer, due, 0, 0, 0, false);
    }

    public void Dispose()
    {
        if (_stopped && !_thread.IsAlive) return;
        PostThreadMessageW(_nativeThreadId, WM_QUIT, 0, 0);
        if (!_thread.Join(TimeSpan.FromSeconds(3))) UxLog.Write("overlay thread did not stop within 3 s");
        _ready.Dispose();
    }
}
