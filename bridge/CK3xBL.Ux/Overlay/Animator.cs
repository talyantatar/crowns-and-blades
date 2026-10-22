namespace CK3xBL.Ux.Overlay;

/// <summary>Easing curves on t in [0, 1].</summary>
internal static class Easing
{
    public static double Linear(double t) => t;
    /// <summary>Gentle start and end: the default for fades of the whole screen.</summary>
    public static double InOutSine(double t) => -(Math.Cos(Math.PI * t) - 1) / 2;
    /// <summary>Fast start, soft landing: text appearing over an already darkening screen.</summary>
    public static double OutCubic(double t) => 1 - Math.Pow(1 - t, 3);
}

/// <summary>
/// Alpha animations of layered windows, ticked by <see cref="OverlayThread"/>'s 60 fps clock. Time-based (a late frame never
/// stretches a fade). Single-threaded: use only on the overlay thread.
/// </summary>
internal sealed class Animator(Func<double> now, Action requestFrame)
{
    private sealed class Fade
    {
        public required LayeredWindow Window;
        public required byte To;
        public required double StartMs, DurationMs;
        public required Func<double, double> Ease;
        public Action<bool>? Done;
        public bool Started;
        public byte From;
    }

    private readonly List<Fade> _fades = new();

    /// <summary>Frames ticked since creation (diagnostics and tests).</summary>
    public long FrameCount { get; private set; }

    public bool IsAnimating => _fades.Count > 0;

    /// <summary>
    /// Fades <paramref name="window"/> from its current alpha (read when the delay has elapsed) to <paramref name="to"/>.
    /// A running fade on the same window is replaced; its <c>done</c> callback receives false. <paramref name="done"/>
    /// receives true when this fade completes.
    /// </summary>
    public void FadeTo(LayeredWindow window, byte to, double durationMs, double delayMs, Func<double, double> ease, Action<bool>? done = null)
    {
        Stop(window);
        _fades.Add(new Fade
        {
            Window = window, To = to, StartMs = now() + Math.Max(0, delayMs), DurationMs = Math.Max(0, durationMs), Ease = ease, Done = done,
        });
        requestFrame();
    }

    /// <summary>Stops every fade of <paramref name="window"/> where it is (callbacks receive false).</summary>
    public void Stop(LayeredWindow window)
    {
        List<Fade>? stopped = null;
        for (int i = _fades.Count - 1; i >= 0; i--)
        {
            if (_fades[i].Window != window) continue;
            (stopped ??= new()).Add(_fades[i]);
            _fades.RemoveAt(i);
        }
        if (stopped != null) foreach (var f in stopped) Invoke(f, false);
    }

    /// <summary>Advances every fade to the current time. Returns true while fades remain (another frame is needed).</summary>
    public bool Tick()
    {
        FrameCount++;
        double t = now();
        List<Fade>? finished = null;
        foreach (var f in _fades)
        {
            if (t < f.StartMs) continue;
            if (!f.Started) { f.Started = true; f.From = f.Window.Alpha; }
            double k = f.DurationMs <= 0 ? 1 : Math.Clamp((t - f.StartMs) / f.DurationMs, 0, 1);
            double eased = f.Ease(k);
            f.Window.SetAlpha((byte)Math.Round(f.From + (f.To - f.From) * eased));
            if (k >= 1) (finished ??= new()).Add(f);
        }
        if (finished != null)
        {
            foreach (var f in finished) _fades.Remove(f);
            // Callbacks run after the list is consistent: they may start new fades.
            foreach (var f in finished) Invoke(f, true);
        }
        return _fades.Count > 0;
    }

    private static void Invoke(Fade f, bool completed)
    {
        try { f.Done?.Invoke(completed); }
        catch (Exception e) { UxLog.Write($"overlay fade callback failed: {e}"); }
    }
}
