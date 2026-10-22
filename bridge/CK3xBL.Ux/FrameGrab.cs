using System.Diagnostics;
using CK3xBL.Ux.Interop;
using static CK3xBL.Ux.Interop.Native;

namespace CK3xBL.Ux;

/// <summary>A captured client-area frame and where it sits on screen.</summary>
internal sealed class CapturedFrame(DibSurface surface, ScreenRect screenRect, long elapsedMs) : IDisposable
{
    public DibSurface Surface { get; } = surface;
    public ScreenRect ScreenRect { get; } = screenRect;
    public long ElapsedMs { get; } = elapsedMs;
    public void Dispose() => Surface.Dispose();
}

/// <summary>
/// Freeze-frame capture for the transition overlay. PrintWindow(PW_CLIENTONLY | PW_RENDERFULLCONTENT) asks DWM for the
/// composed content, so it works on DX11 flip-model games where BitBlt returns black (measured: CK3
/// 1920x1080 in 10-12 ms warm, ~134 ms cold). Call once per transition, never per frame.
/// </summary>
internal static class FrameGrab
{
    /// <summary>Captures the client area, or returns null when the window is gone, minimized, cloaked, or the capture is
    /// empty (all-black frames are treated as failures so the overlay never cuts to black on a failed grab).</summary>
    public static CapturedFrame? Capture(nint hwnd)
    {
        using var _ = DpiScope.PerMonitorV2();
        if (hwnd == 0 || !IsWindow(hwnd) || IsIconic(hwnd) || Cloaked(hwnd) > 0) return null;
        if (!GetClientRect(hwnd, out var rc) || rc.Width <= 0 || rc.Height <= 0) return null;
        var origin = new POINT();
        ClientToScreen(hwnd, ref origin);

        var sw = Stopwatch.StartNew();
        var surface = DibSurface.Create(rc.Width, rc.Height);
        bool ok;
        nint dc = CreateCompatibleDC(0);
        try
        {
            nint old = SelectObject(dc, surface.Handle);
            ok = PrintWindow(hwnd, dc, PW_CLIENTONLY | PW_RENDERFULLCONTENT);
            SelectObject(dc, old);
        }
        finally { DeleteDC(dc); }

        if (!ok || IsBlank(surface))
        {
            UxLog.Write($"freeze frame: capture of 0x{hwnd:X} {(ok ? "was blank" : "failed")} after {sw.ElapsedMilliseconds} ms");
            surface.Dispose();
            return null;
        }
        return new CapturedFrame(surface, new ScreenRect(origin.X, origin.Y, rc.Width, rc.Height), sw.ElapsedMilliseconds);
    }

    /// <summary>True when a sparse sample of the colour channels is entirely zero.</summary>
    private static unsafe bool IsBlank(DibSurface s)
    {
        byte* bits = (byte*)s.Bits;
        int stepX = Math.Max(1, s.Width / 48), stepY = Math.Max(1, s.Height / 27);
        for (int y = stepY / 2; y < s.Height; y += stepY)
        {
            uint* row = (uint*)(bits + (long)y * s.Stride);
            for (int x = stepX / 2; x < s.Width; x += stepX)
                if ((row[x] & 0x00FFFFFFu) != 0) return false;
        }
        return true;
    }
}
