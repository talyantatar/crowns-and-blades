// Where things are: the bridge's own folders, the shared exchange folder, and both game installs (auto-detection).
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CK3xBL.Protocol;
using Microsoft.Win32;

namespace CK3xBL.Core.Config;

public sealed class BridgePaths
{
    /// <summary>The folder holding data/: bridge/ in a source checkout, the install folder of an installed copy.</summary>
    public string BridgeRoot = "";
    public string ConfigFile = "";
    public string DataDir = "";
    /// <summary>%LOCALAPPDATA%\ck3xbl: request/result/status/command files, bridge.log, battles/ archive, caches.</summary>
    public string SharedDir = "";
    /// <summary>The folder of the running executable.</summary>
    public string InstallDir = "";
    /// <summary>Running from a source checkout (bridge/CK3xBL.slnx next to data/): config.json stays in bridge/.</summary>
    public bool IsDevTree;

    public string JournalFile => Path.Combine(SharedDir, "bridge.log");
    public string ArchiveDir => Path.Combine(SharedDir, "battles");
    public string CacheDir => Path.Combine(SharedDir, "cache");
    /// <summary>The table every install must have; without it the bridge cannot send results to CK3.</summary>
    public string DataMarker => Path.Combine(DataDir, DataMarkerFile);
    public bool HasData => File.Exists(DataMarker);

    /// <summary>Environment variable naming bridge/ for a binary built outside the repository (dotnet build --artifacts-path).</summary>
    public const string RootVariable = "CK3XBL_BRIDGE_ROOT";
    public const string DataMarkerFile = "ck3_maa_index.json";
    public const string ConfigFileName = "config.json";
    /// <summary>Marks a source checkout's bridge/ folder.</summary>
    public const string DevTreeMarker = "CK3xBL.slnx";

    /// <summary>Finds the bridge's folders. The root (the folder holding data/): an explicit --config path's folder, then
    /// CK3XBL_BRIDGE_ROOT, then the nearest folder holding data/ck3_maa_index.json walking up from the executable (bin/Release/... in a
    /// checkout, the install folder itself when installed), else the executable's folder.
    /// config.json: --config wins; a checkout (or a root that already has a config.json) keeps it in the root; an installed copy keeps
    /// it in %LOCALAPPDATA%\ck3xbl, so upgrades and uninstalls never touch the player's settings.</summary>
    public static BridgePaths Detect(string? explicitConfig = null) =>
        Detect(explicitConfig, Environment.GetEnvironmentVariable(RootVariable), AppContext.BaseDirectory, SharedFolder());

    /// <summary><see cref="Detect(string?)"/> with every input given (tests).</summary>
    public static BridgePaths Detect(string? explicitConfig, string? rootVariable, string exeDir, string sharedDir)
    {
        string install = Path.GetFullPath(exeDir);
        string root = explicitConfig is not null
            ? Path.GetDirectoryName(Path.GetFullPath(explicitConfig))!
            : RootFromVariable(rootVariable) ?? FindBridgeRoot(install) ?? install;
        root = Path.TrimEndingDirectorySeparator(root);
        bool dev = File.Exists(Path.Combine(root, DevTreeMarker));
        string config = explicitConfig is not null ? Path.GetFullPath(explicitConfig)
            : dev || File.Exists(Path.Combine(root, ConfigFileName)) ? Path.Combine(root, ConfigFileName)
            : Path.Combine(sharedDir, ConfigFileName);
        return new BridgePaths
        {
            BridgeRoot = root,
            ConfigFile = config,
            DataDir = Path.Combine(root, "data"),
            SharedDir = sharedDir,
            InstallDir = Path.TrimEndingDirectorySeparator(install),
            IsDevTree = dev,
        };
    }

    public static string SharedFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProtocolInfo.FolderName);

    /// <summary>bridge/ as CK3XBL_BRIDGE_ROOT names it, when that folder holds data/ck3_maa_index.json; else null.</summary>
    public static string? RootFromEnvironment() => RootFromVariable(Environment.GetEnvironmentVariable(RootVariable));

    private static string? RootFromVariable(string? value) =>
        value is { Length: > 0 } && IsBridgeRoot(value) ? Path.GetFullPath(value) : null;

    private static bool IsBridgeRoot(string dir) => File.Exists(Path.Combine(dir, "data", DataMarkerFile));

    private static string? FindBridgeRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (IsBridgeRoot(dir.FullName)) return dir.FullName;
        return null;
    }
}

/// <summary>Where a game install came from. Only Steam is tested; the others are detected so the player gets a clear notice.</summary>
public enum GameEdition { Unknown, Steam, Gog, Epic, Xbox }

/// <summary>Facts read from a game install folder (no registry, no Windows API): validity, version and edition.</summary>
public static class GameInstall
{
    public static bool IsCk3Dir(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "binaries", "ck3.exe"));

    public static bool IsBannerlordDir(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "bin", "Win64_Shipping_Client", "Bannerlord.exe"));

    /// <summary>The Xbox / Game Pass layout of Bannerlord (a different binaries folder; not supported).</summary>
    public static bool IsXboxBannerlordDir(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "bin", "Gaming.Desktop.x64_Shipping_Client", "Bannerlord.exe"));

    /// <summary>"1.20.0.4" from launcher/launcher-settings.json (rawVersion), else "1.20.0" from titus_branch.txt; null if unknown.</summary>
    public static string? Ck3Version(string? ck3Dir)
    {
        if (string.IsNullOrWhiteSpace(ck3Dir)) return null;
        if (LauncherSetting(ck3Dir, "rawVersion") is { Length: > 0 } raw) return raw;
        try
        {
            string branch = Path.Combine(ck3Dir, "titus_branch.txt");
            if (File.Exists(branch) && Regex.Match(File.ReadAllText(branch), @"\d+\.\d+(\.\d+)*") is { Success: true } m) return m.Value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>launcher-settings.json distPlatform: "steam", "msstore", ...; null if unknown.</summary>
    public static string? Ck3Platform(string? ck3Dir) =>
        string.IsNullOrWhiteSpace(ck3Dir) ? null : LauncherSetting(ck3Dir, "distPlatform");

    /// <summary>"v1.4.8" from bin/Win64_Shipping_Client/Version.xml (Singleplayer/@Value); null if unknown.</summary>
    public static string? BannerlordVersion(string? blDir)
    {
        if (string.IsNullOrWhiteSpace(blDir)) return null;
        foreach (var bin in new[] { "Win64_Shipping_Client", "Gaming.Desktop.x64_Shipping_Client" })
        {
            string file = Path.Combine(blDir, "bin", bin, "Version.xml");
            if (!File.Exists(file)) continue;
            try
            {
                return XDocument.Load(file).Root?.Element("Singleplayer")?.Attribute("Value")?.Value is { Length: > 0 } v ? v : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException) { return null; }
        }
        return null;
    }

    /// <summary>The store a CK3 install belongs to, from its own files.</summary>
    public static GameEdition Ck3Edition(string? ck3Dir)
    {
        if (!IsCk3Dir(ck3Dir)) return GameEdition.Unknown;
        switch (Ck3Platform(ck3Dir)?.ToLowerInvariant())
        {
            case "steam": return GameEdition.Steam;
            case "msstore" or "xbox" or "gamepass": return GameEdition.Xbox;
        }
        return File.Exists(Path.Combine(ck3Dir!, "binaries", "steam_appid.txt")) ? GameEdition.Steam : GameEdition.Unknown;
    }

    /// <summary>The store a Bannerlord install belongs to, from its own files (Epic and GOG leave their own markers).</summary>
    public static GameEdition BannerlordEdition(string? blDir)
    {
        if (IsXboxBannerlordDir(blDir) && !IsBannerlordDir(blDir)) return GameEdition.Xbox;
        if (!IsBannerlordDir(blDir)) return GameEdition.Unknown;
        string bin = Path.Combine(blDir!, "bin", "Win64_Shipping_Client");
        try
        {
            if (Directory.EnumerateFiles(blDir!, "goggame-*.info").Any() || Directory.EnumerateFiles(bin, "goggame-*.info").Any()) return GameEdition.Gog;
            if (Directory.Exists(Path.Combine(blDir!, ".egstore"))) return GameEdition.Epic;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (File.Exists(Path.Combine(bin, "steam_appid.txt")) || File.Exists(Path.Combine(bin, "steam_api64.dll"))) return GameEdition.Steam;
        return GameEdition.Unknown;
    }

    /// <summary>Whether a CK3 version is the one this release targets ("1.20.0.4" against "1.20"). Unknown versions pass.</summary>
    public static bool IsTargetCk3(string? version) =>
        version is null || version == BridgeVersion.Ck3Target || version.StartsWith(BridgeVersion.Ck3Target + ".", StringComparison.Ordinal);

    /// <summary>Whether a Bannerlord version is in the series this release targets ("v1.4.8" against "v1.4."). Unknown versions pass.</summary>
    public static bool IsTargetBannerlord(string? version) =>
        version is null || version.StartsWith(BridgeVersion.BannerlordTargetSeries, StringComparison.OrdinalIgnoreCase);

    /// <summary>The CK3Bridge module's two essential files in &lt;Bannerlord&gt;/Modules/CK3Bridge.</summary>
    public static bool HasBannerlordModule(string? blDir) =>
        !string.IsNullOrWhiteSpace(blDir) && File.Exists(Path.Combine(BannerlordModuleDir(blDir), "SubModule.xml")) &&
        File.Exists(Path.Combine(BannerlordModuleDir(blDir), "bin", "Win64_Shipping_Client", "CK3Bridge.dll"));

    public static string BannerlordModuleDir(string blDir) => Path.Combine(blDir, "Modules", "CK3Bridge");

    private static string? LauncherSetting(string ck3Dir, string key)
    {
        string file = Path.Combine(ck3Dir, "launcher", "launcher-settings.json");
        if (!File.Exists(file)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}

/// <summary>Finds the game installs: Steam libraries (libraryfolders.vdf + appmanifest_*.acf), Steam's uninstall entries, then the
/// GOG and Epic records for Bannerlord. Every candidate is validated by its executable, so no store record is trusted blindly.
/// Xbox / Game Pass installs are only reported (<see cref="FindXboxBannerlord"/>): they cannot be launched or modded this way.</summary>
[SupportedOSPlatform("windows")]
public static class GameLocator
{
    public const string Ck3AppId = "1158310";
    public const string BannerlordAppId = "261550";
    private const string Ck3SteamFolder = "Crusader Kings III";
    private const string BannerlordSteamFolder = "Mount & Blade II Bannerlord";

    public static bool IsCk3Dir(string? dir) => GameInstall.IsCk3Dir(dir);

    public static bool IsBannerlordDir(string? dir) => GameInstall.IsBannerlordDir(dir);

    public static string DefaultCk3UserDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Paradox Interactive", "Crusader Kings III");

    public static string? FindCk3() =>
        FindApp(Ck3AppId, IsCk3Dir) ?? FromUninstallEntry(Ck3AppId, IsCk3Dir) ?? FromDefaultSteam(Ck3SteamFolder, IsCk3Dir);

    public static string? FindBannerlord() =>
        FindApp(BannerlordAppId, IsBannerlordDir) ?? FromUninstallEntry(BannerlordAppId, IsBannerlordDir) ??
        FromGog(IsBannerlordDir) ?? FromEpic(IsBannerlordDir) ?? FromDefaultSteam(BannerlordSteamFolder, IsBannerlordDir);

    /// <summary>An Xbox / Game Pass Bannerlord (&lt;drive&gt;:\XboxGames\...\Content), for a "not supported" notice only.</summary>
    public static string? FindXboxBannerlord()
    {
        foreach (var drive in SafeDrives())
        {
            string games = Path.Combine(drive, "XboxGames");
            foreach (var game in SafeDirectories(games))
            {
                string content = Path.Combine(game, "Content");
                if (GameInstall.IsXboxBannerlordDir(content)) return content;
            }
        }
        return null;
    }

    /// <summary>Resolves the configured paths: a valid configured path wins, otherwise detection fills it in.</summary>
    public static void Resolve(BridgeConfig config)
    {
        if (!IsCk3Dir(config.Ck3.GameDir)) config.Ck3.GameDir = FindCk3() ?? config.Ck3.GameDir;
        if (string.IsNullOrWhiteSpace(config.Ck3.UserDir)) config.Ck3.UserDir = DefaultCk3UserDir();
        if (!IsBannerlordDir(config.Bannerlord.GameDir)) config.Bannerlord.GameDir = FindBannerlord() ?? config.Bannerlord.GameDir;
    }

    private static string? FindApp(string appId, Func<string, bool> valid)
    {
        foreach (var library in SteamLibraries())
        {
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest)) continue;
            try
            {
                var m = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"(?<dir>[^\"]+)\"");
                if (!m.Success) continue;
                var dir = Path.Combine(library, "steamapps", "common", UnescapeVdf(m.Groups["dir"].Value));
                if (valid(dir)) return Path.GetFullPath(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>All Steam library roots listed in libraryfolders.vdf (the Steam folder itself first).</summary>
    public static List<string> SteamLibraries()
    {
        var result = new List<string>();
        var steam = SteamRoot();
        if (steam is null) return result;
        result.Add(steam);
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return result;
        try
        {
            foreach (var path in LibraryPaths(File.ReadAllText(vdf)))
                if (!result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return result;
    }

    /// <summary>The "path" entries of a libraryfolders.vdf, unescaped and made absolute.</summary>
    internal static IEnumerable<string> LibraryPaths(string vdfText)
    {
        foreach (Match m in Regex.Matches(vdfText, "\"path\"\\s+\"(?<p>[^\"]+)\""))
        {
            string path;
            try { path = Path.GetFullPath(UnescapeVdf(m.Groups["p"].Value)); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            yield return path;
        }
    }

    private static string? SteamRoot()
    {
        foreach (var (key, value) in new[]
                 {
                     (@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath"),
                     (@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                     (@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath"),
                 })
        {
            if (ReadRegistry(key, value) is { Length: > 0 } path && Directory.Exists(path)) return Path.GetFullPath(path);
        }
        return null;
    }

    /// <summary>Steam's own Add/Remove Programs entry for the game ("Steam App &lt;id&gt;", both registry views).</summary>
    private static string? FromUninstallEntry(string appId, Func<string, bool> valid)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App {appId}");
                if (key?.GetValue("InstallLocation") is string dir && valid(dir)) return Path.GetFullPath(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return null;
    }

    /// <summary>GOG Galaxy's records: HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\&lt;id&gt; "path".</summary>
    private static string? FromGog(Func<string, bool> valid)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var games = root.OpenSubKey(@"SOFTWARE\GOG.com\Games");
            if (games is null) return null;
            foreach (var id in games.GetSubKeyNames())
            {
                using var game = games.OpenSubKey(id);
                if (game?.GetValue("path") is string dir && valid(dir)) return Path.GetFullPath(dir);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return null;
    }

    /// <summary>The Epic Games Launcher's install manifests (%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item).</summary>
    private static string? FromEpic(Func<string, bool> valid)
    {
        string manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        return EpicInstallLocations(manifests).FirstOrDefault(valid);
    }

    /// <summary>InstallLocation of every Epic manifest in <paramref name="manifestDir"/> (unreadable files are skipped).</summary>
    internal static IEnumerable<string> EpicInstallLocations(string manifestDir)
    {
        IEnumerable<string> files;
        try { files = Directory.Exists(manifestDir) ? Directory.GetFiles(manifestDir, "*.item") : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var file in files)
        {
            string? location = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("InstallLocation", out var v) &&
                    v.ValueKind == JsonValueKind.String)
                    location = v.GetString();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
            if (!string.IsNullOrWhiteSpace(location)) yield return location;
        }
    }

    /// <summary>Steam's default folder on the system drive, for a Steam whose registry entries are gone.</summary>
    private static string? FromDefaultSteam(string folder, Func<string, bool> valid)
    {
        foreach (var programs in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            string root = Environment.GetFolderPath(programs);
            if (root.Length == 0) continue;
            string dir = Path.Combine(root, "Steam", "steamapps", "common", folder);
            if (valid(dir)) return dir;
        }
        return null;
    }

    private static string? ReadRegistry(string key, string value)
    {
        try { return Registry.GetValue(key, value, null) as string; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static IEnumerable<string> SafeDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var d in drives)
        {
            bool fixedReady;
            try { fixedReady = d.DriveType == DriveType.Fixed && d.IsReady; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (fixedReady) yield return d.RootDirectory.FullName;
        }
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    internal static string UnescapeVdf(string s) => s.Replace(@"\\", @"\");
}
