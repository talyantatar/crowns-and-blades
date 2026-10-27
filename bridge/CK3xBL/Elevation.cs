// Running as administrator, only when the player asks for it: a CK3 marked "Run as administrator" can only be started and typed into
// by a bridge running at the same level (Windows' integrity levels forbid a lower program from sending keys to a higher one).
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace CK3xBL;

internal static class Elevation
{
    /// <summary>Marks the restarted copy: it waits for the old one to quit instead of saying "already running".</summary>
    public const string RestartedFlag = "--restarted";

    private const int ErrorCancelled = 1223;

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Starts this program again as administrator with the same arguments (Windows shows its prompt). True when the new copy
    /// started; the caller then quits. False, with the reason, when the player declined or it failed.</summary>
    public static bool RestartAsAdministrator(IReadOnlyList<string> args, out string? why)
    {
        why = null;
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        foreach (var a in args.Where(a => !a.Equals(RestartedFlag, StringComparison.OrdinalIgnoreCase))) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(RestartedFlag);
        try
        {
            using var p = Process.Start(psi);
            return p is not null;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            why = "Administrator rights were not granted; Crowns & Blades keeps running as before.";
            return false;
        }
        catch (Win32Exception e)
        {
            why = $"Could not restart as administrator: {e.Message}";
            return false;
        }
    }
}
