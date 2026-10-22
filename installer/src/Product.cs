// Names, folders and versions shared by setup, uninstall, the elevated helper and the tests.
using System;
using System.Globalization;
using System.Reflection;

namespace CK3xBL.Setup;

internal static class Product
{
    public const string Name = "Crowns & Blades";
    public const string Publisher = "TaLyAn";
    public const string RepoUrl = "https://github.com/graff0n/ck3-bannerlord-bridge";
    public const string IssuesUrl = RepoUrl + "/issues";
    public const string AntivirusHelpUrl = RepoUrl + "/blob/main/docs/TROUBLESHOOTING.md#windows-or-the-antivirus-warns-about-the-exe";

    public const string BridgeExe = "CK3xBL.exe";
    public const string BridgeProcessName = "CK3xBL";
    public const string UninstallerExe = "CK3xBL-Uninstall.exe";
    public const string ManifestFile = "install_manifest.json";
    public const string ShortcutFile = Name + ".lnk";

    /// <summary>%LOCALAPPDATA%\Programs\CK3xBL (per user, no administrator rights).</summary>
    public const string InstallFolder = "CK3xBL";
    /// <summary>%LOCALAPPDATA%\ck3xbl: the bridge's settings, logs and battle archive (shared with the Bannerlord module).</summary>
    public const string SharedFolder = "ck3xbl";

    public const string ModFolder = "ck3xbl";
    public const string ModDescriptor = "ck3xbl.mod";
    public const string ModuleFolder = "CK3Bridge";

    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CK3xBL";
    /// <summary>The bridge's "Start with Windows" entry: HKCU\...\Run, value CK3xBL = "&lt;CK3xBL.exe&gt;".</summary>
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValue = "CK3xBL";
    /// <summary>The folder under the shared folder that holds the player's own copies setup moved out of its way.</summary>
    public const string BackupFolder = "setup-backups";

    /// <summary>The bridge's single-instance mutex and its graceful-quit event (both per session).</summary>
    public const string BridgeMutex = @"Local\CK3xBL.Bridge.SingleInstance";
    public const string BridgeQuitEvent = @"Local\CK3xBL.Bridge.Quit";

    public const string Disclaimer =
        "Crowns & Blades is a free fan project. It is not affiliated with, endorsed by or sponsored by Paradox Interactive " +
        "or TaleWorlds Entertainment. Crusader Kings III is a trademark of Paradox Interactive AB. Mount & Blade II: Bannerlord " +
        "is a trademark of TaleWorlds Entertainment. You need your own copies of both games. No game files are included.";

    /// <summary>This setup's own version, e.g. "0.9.0-beta".</summary>
    public static string SetupVersion
    {
        get
        {
            var attr = (AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute(
                typeof(Product).Assembly, typeof(AssemblyInformationalVersionAttribute));
            return attr?.InformationalVersion ?? typeof(Product).Assembly.GetName().Version.ToString(3);
        }
    }
}

/// <summary>"major.minor.patch[-pre]" ordering, enough for our own release names (0.9.0-beta &lt; 0.9.0 &lt; 0.9.1-beta).</summary>
internal readonly struct ReleaseVersion : IComparable<ReleaseVersion>
{
    public readonly int Major, Minor, Patch;
    public readonly string Pre;

    private ReleaseVersion(int major, int minor, int patch, string pre)
    {
        Major = major; Minor = minor; Patch = patch; Pre = pre;
    }

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text!.Trim().TrimStart('v', 'V');
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s.Substring(0, plus);
        string pre = "";
        int dash = s.IndexOf('-');
        if (dash >= 0) { pre = s.Substring(dash + 1); s = s.Substring(0, dash); }
        var parts = s.Split('.');
        if (parts.Length < 1 || parts.Length > 4) return false;
        var n = new int[3];
        for (int i = 0; i < Math.Min(parts.Length, 3); i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return false;
        version = new ReleaseVersion(n[0], n[1], n[2], pre);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (Pre.Length == 0 && other.Pre.Length == 0) return 0;
        if (Pre.Length == 0) return 1;          // a release is newer than its pre-releases
        if (other.Pre.Length == 0) return -1;
        return string.Compare(Pre, other.Pre, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Compares two version strings; unparsable ones compare equal (no downgrade warning on garbage).</summary>
    public static int Compare(string? a, string? b) =>
        TryParse(a, out var va) && TryParse(b, out var vb) ? va.CompareTo(vb) : 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Pre.Length > 0 ? "-" + Pre : "");
}
