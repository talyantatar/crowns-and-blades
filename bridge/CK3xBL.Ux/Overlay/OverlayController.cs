using System.Runtime.InteropServices;
using CK3xBL.Ux.Interop;
using CK3xBL.Ux.Rendering;
using static CK3xBL.Ux.Interop.Native;

namespace CK3xBL.Ux.Overlay;

/// <summary>A rendered card ready to be presented: opaque background + transparent foreground, both monitor-sized.</summary>
internal sealed class CardSurfaces(DibSurface background, DibSurface foreground, ScreenRect bounds, CardLayout layout) : IDisposable
{
    public DibSurface Background { get; } = background;
    public DibSurface Foreground { get; } = foreground;
    public ScreenRect Bounds { get; } = bounds;
    public CardLayout Layout { get; } = layout;
    public void Dispose() { Background.Dispose(); Foreground.Dispose(); }
}

/// <summary>Fade timings of a card (milliseconds).</summary>
internal sealed record CardTiming(double BackgroundMs, double ForegroundDelayMs, double ForegroundMs)
{
    /// <summary>Darkening from a freeze frame into the card: the title arrives once the screen is already dimming.</summary>
    public static readonly CardTiming FromFreezeFrame = new(650, 280, 720);
    /// <summary>No freeze frame (e.g. Bannerlord is already black, or the capture failed): a plain fade in.</summary>
    public static readonly CardTiming FadeIn = new(450, 120, 600);
}

/// <summary>
/// Presents and removes transition cards. Lives on the <see cref="OverlayThread"/>; every public method must be called there.
///
/// A <see cref="Stage"/> is the set of windows for one card, bottom to top: an optional freeze frame (the game's last frame,
/// pixel-identical to the screen, so showing it is an invisible cut), the opaque card background, the transparent card
/// foreground and an optional hint line. A newer card is stacked above the old one, which is destroyed once the new one is
/// opaque. Requests carry a sequence number; anything older than the latest request is dropped, so a hide that overtakes a
/// slow show can never leave a card stranded on screen.
///
/// Safety valves (a covered screen must never trap the player): the card fades away by itself when the foreground moves to an
/// application that is not one of ours, and when it has been fully shown for longer than <see cref="MaxHold"/>. Nothing is
/// presented while such an application is in the foreground (the hook only reports changes), and a request a valve removed
/// stays dismissed, so its still-rendering card can never appear a moment later.
/// </summary>
internal sealed class OverlayController : IDisposable
{
    private sealed class Stage(long seq)
    {
        public readonly long Seq = seq;
        public LayeredWindow? Freeze, Back, Front, Hint;
        public TaskCompletionSource<bool>? Shown;
        public double ShownAtMs = -1;

        public IEnumerable<LayeredWindow> Windows()
        {
            if (Freeze != null) yield return Freeze;
            if (Back != null) yield return Back;
            if (Front != null) yield return Front;
            if (Hint != null) yield return Hint;
        }
    }

    [ThreadStatic] private static OverlayController? t_foregroundTarget;

    private readonly OverlayThread _ui;
    private readonly Func<long> _latestSeq;
    private readonly Func<int, bool> _isTrustedProcess;
    private readonly Func<nint> _foregroundWindow;
    private long _dismissedSeq;                        // requests up to this one were removed by a safety valve
    private Stage? _current;
    private readonly List<Stage> _retiring = new();   // covered by _current; destroyed when _current is fully shown
    private readonly List<Stage> _hiding = new();     // fading out; destroyed when the fade ends
    private TaskCompletionSource<bool>? _hideDone;
    private nint _foregroundHook;
    private readonly int _ownPid = Environment.ProcessId;

    /// <summary>Longest a fully shown card may stay up before it removes itself.</summary>
    public TimeSpan MaxHold { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Raised (on the overlay thread) when a safety valve removed the card; the argument says why.</summary>
    public event Action<string>? AutoHidden;

    /// <param name="ui">The overlay thread this controller lives on.</param>
    /// <param name="latestSeq">The newest request number; anything older is dropped.</param>
    /// <param name="isTrustedProcess">Processes whose windows may hold the foreground under a card (the games).</param>
    /// <param name="foregroundWindow">The current foreground window (GetForegroundWindow; tests substitute it).</param>
    public OverlayController(OverlayThread ui, Func<long> latestSeq, Func<int, bool> isTrustedProcess, Func<nint> foregroundWindow)
    {
        _ui = ui;
        _latestSeq = latestSeq;
        _isTrustedProcess = isTrustedProcess;
        _foregroundWindow = foregroundWindow;
        _ui.Housekeeping += CheckMaxHold;
    }

    /// <summary>True while any card window exists (visible or fading).</summary>
    public bool HasStage => _current != null || _retiring.Count > 0 || _hiding.Count > 0;

    /// <summary>Number of overlay windows alive (diagnostics and tests).</summary>
    public int WindowCount => _hiding.Concat(_retiring).Append(_current).OfType<Stage>().Sum(s => s.Windows().Count());

    /// <summary>Alpha of every overlay window alive (diagnostics and tests).</summary>
    public IReadOnlyList<byte> WindowAlphas =>
        _hiding.Concat(_retiring).Append(_current).OfType<Stage>().SelectMany(s => s.Windows()).Select(w => w.Alpha).ToList();

    /// <summary>The monitor rect of the visible card, if any.</summary>
    public ScreenRect? CurrentBounds => _current?.Back?.Bounds;

    /// <summary>
    /// Shows the freeze frame at full opacity, exactly over the captured client area. False when the request is stale or was
    /// dismissed, when the player is in another application, or when the frame could not be uploaded.
    /// </summary>
    public bool ShowFreeze(long seq, CapturedFrame frame)
    {
        if (!MayPresent(seq)) return false;
        var stage = BeginStage(seq);
        var window = LayeredWindow.Create("freeze", frame.ScreenRect, perPixelAlpha: false);
        if (!window.SetContent(frame.Surface, 255)) { window.Dispose(); return false; }
        window.ShowTopmost();
        stage.Freeze = window;
        EnsureForegroundWatch();
        return true;
    }

    /// <summary>
    /// Presents a card above everything shown so far. Completes with true when the card is fully opaque, false when it was
    /// superseded, dismissed or declined (see <see cref="MayPresent"/>) or removed before that. Takes ownership of
    /// <paramref name="card"/>.
    /// </summary>
    public Task<bool> Present(long seq, CardSurfaces card, CardTiming timing)
    {
        using (card)
        {
            if (!MayPresent(seq)) return Task.FromResult(false);
            var stage = _current?.Seq == seq ? _current : BeginStage(seq);

            stage.Back = LayeredWindow.Create("card", card.Bounds, perPixelAlpha: false);
            stage.Front = LayeredWindow.Create("text", card.Bounds, perPixelAlpha: true);
            bool ok = stage.Back.SetContent(card.Background, 0) & stage.Front.SetContent(card.Foreground, 0);
            stage.Back.ShowTopmost();
            stage.Front.ShowTopmost();
            var shown = stage.Shown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!ok)
            {
                UxLog.Write("overlay: card upload failed; removing it");
                HideAll(150);
                return shown.Task;
            }

            int pending = 2;
            bool interrupted = false;
            void OnLayerDone(bool completed)
            {
                interrupted |= !completed;
                if (--pending > 0) return;
                if (interrupted || _current != stage) { stage.Shown?.TrySetResult(false); return; }
                // The card is opaque: what lies beneath is now invisible and can go.
                stage.Freeze?.Dispose();
                stage.Freeze = null;
                foreach (var old in _retiring) DestroyStage(old);
                _retiring.Clear();
                stage.ShownAtMs = _ui.NowMs;
                stage.Shown?.TrySetResult(true);
            }
            _ui.Animator.FadeTo(stage.Back, 255, timing.BackgroundMs, 0, Easing.InOutSine, OnLayerDone);
            _ui.Animator.FadeTo(stage.Front, 255, timing.ForegroundMs, timing.ForegroundDelayMs, Easing.OutCubic, OnLayerDone);
            EnsureForegroundWatch();
            return shown.Task;
        }
    }

    /// <summary>Shows (or replaces) a one-line hint strip on the current card. Takes ownership of <paramref name="strip"/>.</summary>
    public void ShowHint(DibSurface strip, ScreenRect bounds)
    {
        using (strip)
        {
            if (_current?.Back == null) return;
            if (_current.Hint != null) { _ui.Animator.Stop(_current.Hint); _current.Hint.Dispose(); }
            var hint = LayeredWindow.Create("hint", bounds, perPixelAlpha: true);
            if (!hint.SetContent(strip, 0)) { hint.Dispose(); return; }
            hint.ShowTopmost();
            _current.Hint = hint;
            _ui.Animator.FadeTo(hint, 255, 350, 0, Easing.InOutSine);
        }
    }

    /// <summary>Removes the hint strip, if any.</summary>
    public void HideHint()
    {
        var hint = _current?.Hint;
        if (hint == null) return;
        _current!.Hint = null;
        _ui.Animator.FadeTo(hint, 0, 250, 0, Easing.InOutSine, _ => hint.Dispose());
    }

    /// <summary>
    /// Fades every card out over <paramref name="durationMs"/> and destroys the windows. Completes when they are gone (or
    /// when a newer card takes over the screen first).
    /// </summary>
    public Task HideAll(double durationMs)
    {
        if (_current != null) { _hiding.Add(_current); _current = null; }
        _hiding.AddRange(_retiring);
        _retiring.Clear();
        _hideDone?.TrySetResult(true);
        if (_hiding.Count == 0) { RemoveForegroundWatch(); return Task.CompletedTask; }

        var done = _hideDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stages = _hiding.ToList();
        foreach (var stage in stages) stage.Shown?.TrySetResult(false);
        int pending = stages.Sum(s => s.Windows().Count());
        if (pending == 0)
        {
            // Nothing on screen yet (e.g. a freeze capture that never showed): just forget the stages.
            foreach (var stage in stages) { _hiding.Remove(stage); DestroyStage(stage); }
            if (!HasStage) RemoveForegroundWatch();
            done.TrySetResult(true);
            return done.Task;
        }
        void OnFaded(bool completed)
        {
            if (--pending > 0 || !completed) return;
            foreach (var stage in stages)
            {
                if (!_hiding.Remove(stage)) continue;     // taken over by a newer card meanwhile
                DestroyStage(stage);
            }
            if (!HasStage) RemoveForegroundWatch();
            done.TrySetResult(true);
        }
        foreach (var stage in stages)
            foreach (var w in stage.Windows().ToList())
            {
                // Text leaves slightly ahead of the background so no letters linger on a vanished card.
                double ms = w == stage.Back || w == stage.Freeze ? durationMs : durationMs * 0.85;
                _ui.Animator.FadeTo(w, 0, ms, 0, Easing.InOutSine, OnFaded);
            }
        return done.Task;
    }

    /// <summary>Called when the caller cancelled the show <paramref name="seq"/>: if it is still on screen, remove it quickly.</summary>
    public void CancelShow(long seq)
    {
        if (_current?.Seq == seq && _current.ShownAtMs < 0) HideAll(150);
    }

    /// <summary>Destroys every window at once (shutdown).</summary>
    public void DestroyAll()
    {
        foreach (var stage in _hiding.Concat(_retiring).Append(_current).OfType<Stage>()) DestroyStage(stage);
        _hiding.Clear(); _retiring.Clear(); _current = null;
        _hideDone?.TrySetResult(true);
        RemoveForegroundWatch();
    }

    public void Dispose()
    {
        _ui.Housekeeping -= CheckMaxHold;
        DestroyAll();
    }

    // ------------------------------------------------------------------------------------------------ internals

    /// <summary>Makes a new current stage; the old current (and anything still fading out) waits beneath it.</summary>
    private Stage BeginStage(long seq)
    {
        if (_current != null) _retiring.Add(_current);
        // Stages that were fading out freeze at their current alpha and are removed once the new card is opaque, so the
        // screen never "breathes" between two cards.
        foreach (var stage in _hiding)
        {
            foreach (var w in stage.Windows()) _ui.Animator.Stop(w);
            _retiring.Add(stage);
        }
        _hiding.Clear();
        _hideDone?.TrySetResult(true);
        foreach (var stage in _retiring) stage.Shown?.TrySetResult(false);
        _current = new Stage(seq);
        return _current;
    }

    private void DestroyStage(Stage stage)
    {
        foreach (var w in stage.Windows().ToList())
        {
            _ui.Animator.Stop(w);
            w.Dispose();
        }
        stage.Freeze = stage.Back = stage.Front = stage.Hint = null;
        stage.Shown?.TrySetResult(false);
    }

    /// <summary>
    /// False for a request that is no longer the latest or that a safety valve dismissed. Also false while the player is in
    /// another application (the foreground hook only reports changes, so a card requested while they are already there would
    /// otherwise cover their app): the request is then dismissed, anything still up is removed and <see cref="AutoHidden"/> fires.
    /// </summary>
    private bool MayPresent(long seq)
    {
        if (seq != _latestSeq() || seq <= _dismissedSeq) return false;
        if (DescribeForeignForeground() is not { } foreign) return true;
        AutoHide($"the player is in another application ({foreign}); card {seq} is not shown", 150, _latestSeq());
        return false;
    }

    private void CheckMaxHold()
    {
        if (_current is { ShownAtMs: >= 0 } stage && _ui.NowMs - stage.ShownAtMs > MaxHold.TotalMilliseconds)
            AutoHide($"card was held longer than {MaxHold.TotalSeconds:F0} s", 400, stage.Seq);
    }

    /// <summary>
    /// Safety valve: requests up to <paramref name="dismissUpTo"/> are dismissed for good (one still rendering is dropped when it
    /// arrives), whatever is visible fades out over <paramref name="ms"/>, and <see cref="AutoHidden"/> fires.
    /// </summary>
    private void AutoHide(string reason, double ms, long dismissUpTo)
    {
        _dismissedSeq = Math.Max(_dismissedSeq, dismissUpTo);
        UxLog.Write($"overlay: safety valve: {reason}");
        if (_current != null || _retiring.Count > 0) HideAll(ms);
        try { AutoHidden?.Invoke(reason); }
        catch (Exception e) { UxLog.Write($"overlay AutoHidden handler failed: {e}"); }
    }

    /// <summary>
    /// "name, class 'X'" when the foreground window belongs to an application that is neither the bridge, a game nor a shell
    /// surface; null otherwise (including no foreground window at all).
    /// </summary>
    private string? DescribeForeignForeground()
    {
        nint hwnd = _foregroundWindow();
        if (hwnd == 0) return null;
        int pid = ProcessIdOf(hwnd);
        if (pid == 0 || pid == _ownPid || Foreground.IsShellWindow(hwnd)) return null;
        bool trusted;
        try { trusted = _isTrustedProcess(pid); }
        catch (Exception e)
        {
            UxLog.Write($"overlay: trust check for pid {pid} failed ({e.Message}); treating it as foreign");
            trusted = false;
        }
        return trusted ? null : $"{TryProcessName(pid) ?? "pid " + pid}, class '{ClassName(hwnd)}'";
    }

    private unsafe void EnsureForegroundWatch()
    {
        if (_foregroundHook != 0) return;
        t_foregroundTarget = this;
        // Out-of-context hook: callbacks arrive through this thread's message loop; no DLL is injected anywhere.
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, &OnForegroundEvent, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (_foregroundHook == 0) UxLog.Write("overlay: SetWinEventHook(EVENT_SYSTEM_FOREGROUND) failed; the foreground safety valve is off");
    }

    private void RemoveForegroundWatch()
    {
        if (_foregroundHook == 0) return;
        UnhookWinEvent(_foregroundHook);
        _foregroundHook = 0;
    }

    [UnmanagedCallersOnly]
    private static void OnForegroundEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try { t_foregroundTarget?.OnForegroundChanged(); }
        catch (Exception e) { UxLog.Write($"overlay foreground handler failed: {e}"); }
    }

    /// <summary>
    /// The foreground changed: if it is now another application, the card leaves and every request so far is dismissed. The
    /// current foreground is re-read rather than taken from the (out-of-context, so slightly late) event.
    /// </summary>
    private void OnForegroundChanged()
    {
        if (_current == null && _retiring.Count == 0) return;   // nothing up, or only fading out already
        if (DescribeForeignForeground() is { } foreign)
            AutoHide($"the foreground moved to another application ({foreign})", 150, _latestSeq());
    }
}
