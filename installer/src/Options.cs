// Command line. Switches are case-insensitive and may be written /name, /name=value, /name:value, --name or --name=value.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CK3xBL.Setup;

internal sealed class Options
{
    // Modes
    public bool Silent;
    public bool Uninstall;
    public bool DryRun;
    public bool Help;

    // Sandbox and target overrides (any of them switches game auto-detection off)
    public string? TargetRoot;
    public string? InstallDir;
    public string? Ck3Dir;
    public string? Ck3UserDir;
    public string? BlDir;

    public string? PayloadPath;
    public string? LogPath;
    public string? ResultPath;

    // Install choices (GUI defaults are the same; the wizard lets the player change them)
    public bool NoCk3Mod;
    public bool NoDlcLoad;
    public bool NoBlModule;
    public bool NoStartMenu;
    public bool DesktopShortcut;
    public bool StartBridge;
    public bool ReplaceExisting;
    public bool RemoveModuleLink;
    public bool AllowDowngrade;

    // Uninstall choice
    public bool DeleteUserData;

    // Internal: elevated helper and the uninstaller's copy of itself in %TEMP%
    public string? ElevatedRequest;
    public bool FromTemp;
    public int WaitPid;

    public bool HasTargetOverride =>
        TargetRoot is not null || InstallDir is not null || Ck3Dir is not null || Ck3UserDir is not null || BlDir is not null;

    public const string Usage =
        "CK3xBL-Setup.exe [options]\n" +
        "  (no options)          the setup wizard\n" +
        "  /uninstall            remove Crowns & Blades\n" +
        "  /silent               no windows; exit code 0 = done, 1 = done with warnings or skipped steps, 2 = failed,\n" +
        "                        3 = blocked (Crowns & Blades or Bannerlord running), 4 = bad arguments, 5 = cancelled\n" +
        "  /dry-run              print the plan, change nothing\n" +
        "  /target-root=<dir>    sandbox: every destination (install folder, Documents, AppData, games, registry) under <dir>\n" +
        "  /install-dir=<dir> /ck3-dir=<dir> /ck3-user-dir=<dir> /bl-dir=<dir>   single overrides (no auto-detection)\n" +
        "  /payload=<file>       use this payload (.zip or a packed setup .exe) instead of the one inside this exe\n" +
        "  /log=<file>           log file (default: %LOCALAPPDATA%\\ck3xbl\\setup.log)\n" +
        "  /result=<file>        write {\"exitCode\":n} there when done (for scripts)\n" +
        "  /no-ck3-mod /no-dlc-load /no-bl-module /no-start-menu /desktop-shortcut /start-bridge\n" +
        "  /replace-existing     replace a ck3xbl mod or CK3Bridge module this setup did not install (moved to a backup)\n" +
        "  /remove-module-link   Modules\\CK3Bridge is a link (development checkout): remove the link, never its target\n" +
        "  /allow-downgrade      install over a newer version\n" +
        "  /delete-user-data     with /uninstall: also delete %LOCALAPPDATA%\\ck3xbl (settings, logs, battle archive)\n";

    public static Options Parse(string[] args)
    {
        var o = new Options();
        foreach (var raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string a = raw.Trim();
            if (a.StartsWith("--", StringComparison.Ordinal)) a = a.Substring(2);
            else if (a.StartsWith("/", StringComparison.Ordinal) || a.StartsWith("-", StringComparison.Ordinal)) a = a.Substring(1);
            else throw new ArgumentException($"unexpected argument '{raw}'");

            string name = a, value = "";
            int sep = a.IndexOfAny(new[] { '=', ':' });
            if (sep > 0) { name = a.Substring(0, sep); value = a.Substring(sep + 1).Trim('"'); }
            name = name.ToLowerInvariant();

            switch (name)
            {
                case "silent": case "quiet": case "s": case "q": o.Silent = true; break;
                case "uninstall": case "u": o.Uninstall = true; break;
                case "dry-run": case "dryrun": o.DryRun = true; break;
                case "help": case "?": case "h": o.Help = true; break;
                case "target-root": o.TargetRoot = Need(name, value); break;
                case "install-dir": o.InstallDir = Need(name, value); break;
                case "ck3-dir": o.Ck3Dir = Need(name, value); break;
                case "ck3-user-dir": o.Ck3UserDir = Need(name, value); break;
                case "bl-dir": o.BlDir = Need(name, value); break;
                case "payload": o.PayloadPath = Need(name, value); break;
                case "log": o.LogPath = Need(name, value); break;
                case "result": o.ResultPath = Need(name, value); break;
                case "no-ck3-mod": o.NoCk3Mod = true; break;
                case "no-dlc-load": o.NoDlcLoad = true; break;
                case "no-bl-module": o.NoBlModule = true; break;
                case "no-start-menu": o.NoStartMenu = true; break;
                case "desktop-shortcut": o.DesktopShortcut = true; break;
                case "start-bridge": o.StartBridge = true; break;
                case "replace-existing": o.ReplaceExisting = true; break;
                case "remove-module-link": o.RemoveModuleLink = true; break;
                case "allow-downgrade": o.AllowDowngrade = true; break;
                case "delete-user-data": o.DeleteUserData = true; break;
                case "elevated-request": o.ElevatedRequest = Need(name, value); break;
                case "from-temp": o.FromTemp = true; break;
                case "wait-pid":
                    if (!int.TryParse(Need(name, value), out o.WaitPid)) throw new ArgumentException("/wait-pid needs a number");
                    break;
                default: throw new ArgumentException($"unknown option '{raw}'");
            }
        }
        return o;
    }

    private static string Need(string name, string value) =>
        value.Length > 0 ? value : throw new ArgumentException($"/{name} needs a value (/{name}=...)");

    /// <summary>The arguments that carry the targets and choices to another copy of setup (elevated helper, uninstall relaunch).</summary>
    public List<string> TargetArguments()
    {
        var list = new List<string>();
        void Add(string name, string? value) { if (value is not null) list.Add($"/{name}={value}"); }
        Add("target-root", TargetRoot);
        Add("install-dir", InstallDir);
        Add("ck3-dir", Ck3Dir);
        Add("ck3-user-dir", Ck3UserDir);
        Add("bl-dir", BlDir);
        Add("log", LogPath);
        Add("result", ResultPath);
        if (Silent) list.Add("/silent");
        if (DryRun) list.Add("/dry-run");
        if (DeleteUserData) list.Add("/delete-user-data");
        return list;
    }

    /// <summary>Quotes arguments for CreateProcess (the MSVCRT rules .NET Framework's parser follows).</summary>
    public static string Quote(IEnumerable<string> args) => string.Join(" ", args.Select(QuoteOne));

    private static string QuoteOne(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); backslashes = 0; continue; }
            sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }
}
