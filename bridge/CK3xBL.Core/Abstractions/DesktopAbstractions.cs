// Seams between the orchestrator and the Windows desktop / the tray UI, so the battle state machine runs headless in tests.
using CK3xBL.Core.Delivery;
using CK3xBL.Core.Native;

namespace CK3xBL.Core.Abstractions;

public interface IDesktop
{
    nint ForegroundWindow { get; }
    int ProcessIdOf(nint hwnd);
    bool IsWindow(nint hwnd);
    /// <summary>Waits until no modifier/mouse button is held and physical input has been idle for <paramref name="quietFor"/>.
    /// Returns false when that did not happen within <paramref name="timeout"/>.</summary>
    Task<bool> WaitForQuietInputAsync(TimeSpan quietFor, TimeSpan timeout, CancellationToken ct);
    /// <summary>Bannerlord clips the cursor during battle; never leave it clipped after switching away.</summary>
    void ReleaseCursorClip();
}

public enum NoticeKind { Info, Warning, Error }

/// <summary>Short, non-activating notices for the player (tray balloons). Never steals focus.</summary>
public interface INotifier
{
    void Notify(string title, string text, NoticeKind kind = NoticeKind.Info);
}

public sealed class Win32Desktop(IKeyInjector injector, IInputGuardSource? guards) : IDesktop
{
    public nint ForegroundWindow => Win32.GetForegroundWindow();

    public int ProcessIdOf(nint hwnd) => Win32.ProcessIdOf(hwnd);

    public bool IsWindow(nint hwnd) => hwnd != 0 && Win32.IsWindow(hwnd);

    public async Task<bool> WaitForQuietInputAsync(TimeSpan quietFor, TimeSpan timeout, CancellationToken ct)
    {
        // The hooks only exist while leased, so physical idle time counts from the start of this wait at the earliest.
        using var guard = quietFor > TimeSpan.Zero ? guards?.Acquire() : null;
        var deadline = DateTime.UtcNow + timeout;
        while (injector.AnyModifierOrButtonHeld() || (guard is not null && guard.PhysicalIdleMs < quietFor.TotalMilliseconds))
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
        return true;
    }

    public void ReleaseCursorClip() => Win32.ClipCursor(0);
}
