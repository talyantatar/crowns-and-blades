namespace CK3xBL.Ux.Interop;

/// <summary>
/// Switches the calling thread to Per-Monitor-V2 DPI awareness for the lifetime of the scope, so every rect read or written
/// inside it is in physical pixels (matching CK3, which is PM aware, and Bannerlord at 100% scaling) no matter how the
/// host process was started. Windows created inside the scope are PMv2 windows.
/// </summary>
internal readonly struct DpiScope : IDisposable
{
    private readonly nint _previous;

    private DpiScope(nint previous) => _previous = previous;

    public static DpiScope PerMonitorV2() => new(Native.SetThreadDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2));

    public void Dispose()
    {
        // SetThreadDpiAwarenessContext returns 0 when it failed (pre-1607 Windows); then there is nothing to restore.
        if (_previous != 0) Native.SetThreadDpiAwarenessContext(_previous);
    }
}
