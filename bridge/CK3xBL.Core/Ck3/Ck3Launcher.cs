// Starts CK3 directly (binaries/ck3.exe, steam_appid.txt next to it lets Steam attach the session) with the launch options of the
// chosen mode (PROTOCOL §0). Release mode must stay consistent for an achievement campaign: never mix debug and release launches
// on one save. The Xbox app edition cannot be started this way (it is refused with a message).
using System.Diagnostics;
using CK3xBL.Core.Config;
using CK3xBL.Core.Diagnostics;

namespace CK3xBL.Core.Ck3;

public static class Ck3Launcher
{
    public static string Arguments(BridgeMode mode, bool continueLastSave)
    {
        var args = new List<string>();
        if (mode == BridgeMode.Dev) args.AddRange(new[] { "-debug_mode", "-suppress_error_log" });
        args.Add("-gdpr-compliant");
        if (continueLastSave) args.Add("-continuelastsave");
        return string.Join(" ", args);
    }

    /// <summary>Launches CK3 unless it is already running. Returns a user-facing problem description, or null on success.</summary>
    public static string? Launch(string gameDir, BridgeMode mode, bool continueLastSave, IJournal journal) =>
        Launch(gameDir, mode, continueLastSave, journal, out _);

    /// <summary>As the overload above; <paramref name="needsElevation"/> is true when ck3.exe may only run as administrator (the
    /// bridge can then restart itself as administrator, so that both run at the same level).</summary>
    public static string? Launch(string gameDir, BridgeMode mode, bool continueLastSave, IJournal journal, out bool needsElevation)
    {
        needsElevation = false;
        if (Ck3Locator.IsRunning()) return "Crusader Kings III is already running.";
        var binaries = Path.Combine(gameDir, "binaries");
        var exe = Path.Combine(binaries, "ck3.exe");
        if (!File.Exists(exe)) return $"ck3.exe not found in {binaries}. Use 'Set game folders...' in the tray menu.";
        if (GameInstall.Ck3Edition(gameDir) == GameEdition.Xbox)
            return "This edition of Crusader Kings III cannot be started from here. Start it from the Xbox app.";

        var psi = new ProcessStartInfo(exe, Arguments(mode, continueLastSave)) { WorkingDirectory = binaries, UseShellExecute = false };
        try
        {
            using var p = Process.Start(psi);
            journal.Info("ck3", $"launched ({mode}): {psi.Arguments}{(IsSteamRunning() ? "" : " - Steam is not running, achievements need it")}");
            return null;
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == ErrorElevationRequired)
        {
            // ck3.exe is marked "Run as administrator" (its Compatibility tab, or a game folder only administrators may run from):
            // a player's report, 2026-10-26. A bridge without administrator rights cannot type into a CK3 that has them (Windows
            // forbids it), so starting CK3 elevated from here would lose every result: the bridge has to run as administrator too.
            needsElevation = true;
            journal.Error("ck3", "launch failed: ck3.exe requires administrator rights", e);
            return "Crusader Kings III is set to run as administrator. Either run Crowns & Blades as administrator too, or right-click " +
                   $"{exe}, Properties, Compatibility, and untick 'Run this program as an administrator' (also under 'Change " +
                   "settings for all users').";
        }
        catch (Exception e)
        {
            journal.Error("ck3", "launch failed", e);
            return $"Could not start Crusader Kings III: {e.Message}";
        }
    }

    private const int ErrorElevationRequired = 740;

    public static bool IsSteamRunning()
    {
        var ps = Process.GetProcessesByName("steam");
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }
}
