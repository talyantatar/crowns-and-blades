// After an unclean exit Bannerlord asks "Game shut down unexpectedly... enable safe mode?" in a Win32 message box (seen in testing: a ~407x114
// window owned by Bannerlord, "No" at client 312,87). The bridge answers No by sending the dialog the IDNO command, which works
// without activating the dialog and does not depend on the OS language of the button captions.
using CK3xBL.Core.Native;

namespace CK3xBL.Core.Bannerlord;

public static class SafeModeDialog
{
    private const string DialogClass = "#32770";

    /// <summary>Answers No to a safe-mode question shown by any of the given processes. Returns a description of what was answered,
    /// or null. Only Yes/No message boxes that mention safe mode (or have the known size) are touched.</summary>
    public static string? TryAnswerNo(IEnumerable<int> processIds)
    {
        foreach (int pid in processIds.Distinct())
        {
            foreach (var dialog in Win32.AllTopLevelWindows(pid))
            {
                if (Win32.ClassName(dialog) != DialogClass || !Win32.IsWindowVisible(dialog)) continue;
                var children = Win32.ChildWindows(dialog);
                nint yes = 0, no = 0;
                bool mentionsSafeMode = Win32.WindowText(dialog).Contains("safe", StringComparison.OrdinalIgnoreCase);
                foreach (var child in children)
                {
                    switch (Win32.GetDlgCtrlID(child))
                    {
                        case Win32.IDYES: yes = child; break;
                        case Win32.IDNO: no = child; break;
                    }
                    if (Win32.WindowText(child).Contains("safe mode", StringComparison.OrdinalIgnoreCase)) mentionsSafeMode = true;
                }
                if (yes == 0 || no == 0) continue;
                Win32.GetWindowRect(dialog, out var rect);
                bool knownSize = Math.Abs(rect.Width - 407) <= 120 && Math.Abs(rect.Height - 114) <= 80;
                if (!mentionsSafeMode && !knownSize) continue;

                nint wParam = (Win32.BN_CLICKED << 16) | Win32.IDNO;
                if (Win32.PostMessageW(dialog, Win32.WM_COMMAND, wParam, no))
                    return $"answered No to '{Win32.WindowText(dialog)}' ({rect.Width}x{rect.Height}, pid {pid})";
            }
        }
        return null;
    }

    /// <summary>
    /// "Mod change detected: ... Deleting the runtime shader cache" (an OK-only message box Bannerlord shows when its module list
    /// differs from the last run's, e.g. when the bridge leaves the armory modules out after a crash, or adds new ones): the game
    /// waits for it before booting, and a hidden game would wait forever (bench 2026-10-19). Acknowledged with IDOK. Returns a
    /// description, or null.
    /// </summary>
    public static string? TryAcknowledgeModChange(IEnumerable<int> processIds)
    {
        foreach (int pid in processIds.Distinct())
        {
            foreach (var dialog in Win32.AllTopLevelWindows(pid))
            {
                if (Win32.ClassName(dialog) != DialogClass || !Win32.IsWindowVisible(dialog)) continue;
                bool modChange = Win32.WindowText(dialog).Contains("mod change", StringComparison.OrdinalIgnoreCase);
                // Its only button is "OK" with the control id IDCANCEL (2), not IDOK (bench 2026-10-19): any single button will do.
                nint ok = 0;
                int buttons = 0;
                foreach (var child in Win32.ChildWindows(dialog))
                {
                    if (Win32.ClassName(child) == "Button") { ok = child; buttons++; }
                    if (Win32.WindowText(child).Contains("change in your active mods", StringComparison.OrdinalIgnoreCase)) modChange = true;
                }
                if (!modChange || buttons != 1) continue;
                nint wParam = (Win32.BN_CLICKED << 16) | (Win32.GetDlgCtrlID(ok) & 0xFFFF);
                if (Win32.PostMessageW(dialog, Win32.WM_COMMAND, wParam, ok))
                    return $"acknowledged '{Win32.WindowText(dialog)}' (pid {pid})";
            }
        }
        return null;
    }
}
