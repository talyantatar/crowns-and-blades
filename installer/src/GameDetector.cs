// Finds Crusader Kings III and Mount & Blade II: Bannerlord: Steam (registry, libraryfolders.vdf, app manifests, uninstall
// entries), then GOG and Epic. Every candidate is validated by the executable it must contain, so no store id is trusted blindly.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using CK3xBL.Setup.Util;

namespace CK3xBL.Setup;

internal static class GameDetector
{
    public const string Ck3AppId = "1158310";
    public const string BannerlordAppId = "261550";

    public static bool IsCk3Dir(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "binaries", "ck3.exe"));

    public static bool IsBannerlordDir(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "bin", "Win64_Shipping_Client", "Bannerlord.exe"));

    public static string? FindCk3() => Find(Ck3AppId, IsCk3Dir);

    public static string? FindBannerlord() => Find(BannerlordAppId, IsBannerlordDir);

    private static string? Find(string appId, Func<string, bool> valid)
    {
        foreach (var candidate in Candidates(appId))
        {
            try
            {
                if (valid(candidate)) return FileOps.Full(candidate);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { }
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string appId)
    {
        foreach (var library in SteamLibraries())
        {
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            string? installDir = null;
            try { if (File.Exists(manifest)) installDir = ParseInstallDir(File.ReadAllText(manifest)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (installDir is not null) yield return Path.Combine(library, "steamapps", "common", installDir);
        }
        foreach (var dir in UninstallLocations($"Steam App {appId}")) yield return dir;
        foreach (var dir in GogGameDirs()) yield return dir;
        foreach (var dir in EpicGameDirs()) yield return dir;
    }

    /// <summary>The Steam folder and every library listed in its libraryfolders.vdf.</summary>
    public static List<string> SteamLibraries()
    {
        var result = new List<string>();
        var steam = SteamRoot();
        if (steam is null) return result;
        result.Add(steam);
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf))
                foreach (var path in ParseLibraryFolders(File.ReadAllText(vdf)))
                    if (!result.Any(r => FileOps.PathEquals(r, path))) result.Add(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return result;
    }

    internal static List<string> ParseLibraryFolders(string vdf)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(vdf, "\"path\"\\s+\"(?<p>[^\"]+)\""))
        {
            try { list.Add(FileOps.Full(UnescapeVdf(m.Groups["p"].Value))); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException) { }
        }
        return list;
    }

    internal static string? ParseInstallDir(string acf)
    {
        var m = Regex.Match(acf, "\"installdir\"\\s+\"(?<dir>[^\"]+)\"");
        return m.Success ? UnescapeVdf(m.Groups["dir"].Value) : null;
    }

    private static string UnescapeVdf(string s) => s.Replace(@"\\", @"\");

    private static string? SteamRoot()
    {
        var candidates = new[]
        {
            ReadValue(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath"),
            ReadValue(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"),
            ReadValue(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"),
        };
        foreach (var path in candidates)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                if (Directory.Exists(path)) return FileOps.Full(path!);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException) { }
        }
        return null;
    }

    private static IEnumerable<string> UninstallLocations(string keyName)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            var dir = ReadValue(RegistryHive.LocalMachine, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + keyName,
                "InstallLocation");
            if (!string.IsNullOrWhiteSpace(dir)) yield return dir!;
        }
    }

    /// <summary>GOG Galaxy: HKLM\SOFTWARE\[WOW6432Node\]GOG.com\Games\&lt;id&gt;, value "path".</summary>
    private static IEnumerable<string> GogGameDirs()
    {
        var result = new List<string>();
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var games = hive.OpenSubKey(@"SOFTWARE\GOG.com\Games");
                if (games is null) continue;
                foreach (var id in games.GetSubKeyNames())
                {
                    using var game = games.OpenSubKey(id);
                    if (game?.GetValue("path") is string path && path.Length > 0) result.Add(path);
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return result;
    }

    /// <summary>Epic Games Launcher: the *.item manifests (JSON) under its data folder, field "InstallLocation".</summary>
    private static IEnumerable<string> EpicGameDirs()
    {
        var result = new List<string>();
        var dataDir = ReadValue(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Epic Games\EpicGamesLauncher", "AppDataPath")
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic",
                          "EpicGamesLauncher", "Data");
        try
        {
            var manifests = Path.Combine(dataDir, "Manifests");
            if (!Directory.Exists(manifests)) return result;
            foreach (var item in Directory.GetFiles(manifests, "*.item"))
            {
                try
                {
                    if (Json.Parse(File.ReadAllText(item)) is JsonObject o && o.GetString("InstallLocation") is { Length: > 0 } dir)
                        result.Add(dir);
                }
                catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return result;
    }

    private static string? ReadValue(RegistryHive hive, RegistryView view, string subKey, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(name) as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>"1.20.0.4" from launcher\launcher-settings.json (rawVersion), else null.</summary>
    public static string? Ck3Version(string? ck3Dir)
    {
        if (string.IsNullOrWhiteSpace(ck3Dir)) return null;
        try
        {
            var file = Path.Combine(ck3Dir, "launcher", "launcher-settings.json");
            if (!File.Exists(file)) return null;
            return (Json.Parse(File.ReadAllText(file)) as JsonObject)?.GetString("rawVersion");
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>"v1.4.8" from bin\Win64_Shipping_Client\Version.xml (Singleplayer Value), else null.</summary>
    public static string? BannerlordVersion(string? blDir)
    {
        if (string.IsNullOrWhiteSpace(blDir)) return null;
        try
        {
            var file = Path.Combine(blDir, "bin", "Win64_Shipping_Client", "Version.xml");
            if (!File.Exists(file)) return null;
            var m = Regex.Match(File.ReadAllText(file), "<Singleplayer\\s+Value\\s*=\\s*\"(?<v>[^\"]+)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups["v"].Value : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>Where the game came from, for the "untested" notes: Steam, GOG, Epic or unknown.</summary>
    public static string StoreOf(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "unknown";
        try
        {
            if (File.Exists(Path.Combine(dir, "steam_appid.txt")) || File.Exists(Path.Combine(dir, "binaries", "steam_appid.txt")) ||
                File.Exists(Path.Combine(dir, "bin", "Win64_Shipping_Client", "steam_appid.txt")) ||
                FileOps.Full(dir!).IndexOf(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Steam";
            if (Directory.GetFiles(dir, "goggame-*.info").Length > 0) return "GOG";
            if (Directory.Exists(Path.Combine(dir, ".egstore"))) return "Epic";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return "unknown";
    }

    /// <summary>True when <paramref name="version"/> fits a pattern such as "1.20.*" or "v1.4.*" (no pattern: always true).</summary>
    public static bool VersionMatches(string? version, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(version)) return true;
        var v = version!.Trim().TrimStart('v', 'V');
        var p = pattern!.Trim().TrimStart('v', 'V');
        if (p.EndsWith("*", StringComparison.Ordinal))
        {
            var prefix = p.Substring(0, p.Length - 1);
            return v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                   v.Equals(prefix.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
        }
        return v.Equals(p, StringComparison.OrdinalIgnoreCase);
    }
}
