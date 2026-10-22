// Is everything in place for a battle? Checks both game installs, the CK3 mod (files, mod list, version) and the Bannerlord module
// (files, version), and the game versions this release was built for. Pure file reads, no UI: the tray turns warnings into one notice
// per start and About lists every line. Nothing here ever blocks the bridge.
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CK3xBL.Core.Ck3;

namespace CK3xBL.Core.Config;

public enum InstallSeverity
{
    /// <summary>Fine, or worth a journal line only.</summary>
    Ok,
    /// <summary>Battles may not work: the player gets a notice.</summary>
    Warning,
}

/// <summary>What the player can do about a warning (the tray offers the matching action).</summary>
public enum InstallFix { None, SetGameFolders, Reinstall, AddToPlayset, UpdateGame }

public sealed record InstallItem(string Check, InstallSeverity Severity, string Title, string Text, InstallFix Fix = InstallFix.None)
{
    public bool IsWarning => Severity == InstallSeverity.Warning;
}

/// <summary>The inputs of a check: the resolved game folders, plus an Xbox Bannerlord found by the (Windows-only) locator.</summary>
public sealed class InstallTargets
{
    public string Ck3Dir = "";
    public string Ck3UserDir = "";
    public string BannerlordDir = "";
    public string? XboxBannerlordDir;
}

public static class InstallCheck
{
    public const string ModDescriptorFile = "ck3xbl.mod";
    /// <summary>A file every build of the mod has: proves path= points at the mod, not at an empty or foreign folder.</summary>
    public const string ModProbeFile = "common/on_action/ck3xbl_on_actions.txt";

    public static List<InstallItem> Run(InstallTargets t, Func<string, bool>? isModEnabled = null)
    {
        var items = new List<InstallItem>();
        CheckCk3(t, items);
        CheckBannerlord(t, items);
        CheckMod(t, items, isModEnabled ?? Ck3ModList.IsEnabled);
        CheckModule(t, items);
        return items;
    }

    private static void CheckCk3(InstallTargets t, List<InstallItem> items)
    {
        if (!GameInstall.IsCk3Dir(t.Ck3Dir))
        {
            items.Add(new("ck3.found", InstallSeverity.Warning, "Crusader Kings III not found",
                "Use 'Set game folders...' in the Crowns & Blades tray menu to choose its install folder.", InstallFix.SetGameFolders));
            return;
        }
        string? version = GameInstall.Ck3Version(t.Ck3Dir);
        var edition = GameInstall.Ck3Edition(t.Ck3Dir);
        items.Add(new("ck3.found", InstallSeverity.Ok, $"Crusader Kings III {version ?? "(version unknown)"} ({Describe(edition)})", t.Ck3Dir));
        if (!GameInstall.IsTargetCk3(version))
            items.Add(new("ck3.version", InstallSeverity.Warning, $"Crusader Kings III {version} is untested",
                $"This release was built for CK3 {BridgeVersion.Ck3Target}. Battles may fail; check for a Crowns & Blades update.", InstallFix.UpdateGame));
        if (edition == GameEdition.Xbox)
            items.Add(new("ck3.edition", InstallSeverity.Warning, "Crusader Kings III from the Xbox app is not supported",
                "Start CK3 from its own launcher; the tray cannot start this edition."));
    }

    private static void CheckBannerlord(InstallTargets t, List<InstallItem> items)
    {
        if (!GameInstall.IsBannerlordDir(t.BannerlordDir))
        {
            bool xbox = GameInstall.IsXboxBannerlordDir(t.BannerlordDir) || t.XboxBannerlordDir is not null;
            items.Add(xbox
                ? new("bl.found", InstallSeverity.Warning, "Bannerlord from the Xbox app is not supported",
                    "The Xbox / Game Pass edition of Bannerlord cannot load the CK3 bridge module in this version.")
                : new("bl.found", InstallSeverity.Warning, "Bannerlord not found",
                    "Use 'Set game folders...' in the Crowns & Blades tray menu to choose its install folder.", InstallFix.SetGameFolders));
            return;
        }
        string? version = GameInstall.BannerlordVersion(t.BannerlordDir);
        var edition = GameInstall.BannerlordEdition(t.BannerlordDir);
        items.Add(new("bl.found", InstallSeverity.Ok, $"Bannerlord {version ?? "(version unknown)"} ({Describe(edition)})", t.BannerlordDir));
        if (!GameInstall.IsTargetBannerlord(version))
            items.Add(new("bl.version", InstallSeverity.Warning, $"Bannerlord {version} is untested",
                $"This release was built for Bannerlord {BridgeVersion.BannerlordTarget}. Battles may fail; check for a Crowns & Blades update.",
                InstallFix.UpdateGame));
        if (edition is GameEdition.Gog or GameEdition.Epic)
            items.Add(new("bl.edition", InstallSeverity.Ok, $"Bannerlord from {Describe(edition)} is untested",
                "Only the Steam edition was tested with this release."));
    }

    private static void CheckMod(InstallTargets t, List<InstallItem> items, Func<string, bool> isModEnabled)
    {
        if (string.IsNullOrWhiteSpace(t.Ck3UserDir)) return;
        var mod = ReadModDescriptor(t.Ck3UserDir);
        if (mod.Dir is null || !File.Exists(Path.Combine(mod.Dir, ModProbeFile)))
        {
            items.Add(new("mod.files", InstallSeverity.Warning, "The CK3 mod is missing",
                $"{Path.Combine(t.Ck3UserDir, "mod", ModDescriptorFile)} or the folder it points to is missing. Run the Crowns & Blades installer again.",
                InstallFix.Reinstall));
            return;
        }
        items.Add(new("mod.files", InstallSeverity.Ok, $"CK3 mod {mod.Version ?? "(version unknown)"} installed", mod.Dir));
        if (mod.Version is { } v && v != BridgeVersion.Mod)
            items.Add(new("mod.version", InstallSeverity.Warning, "The CK3 mod does not match the bridge",
                $"The CK3 mod is version {v}, the bridge {BridgeVersion.Display}. Run the Crowns & Blades installer again.", InstallFix.Reinstall));
        if (!isModEnabled(t.Ck3UserDir))
            items.Add(new("mod.enabled", InstallSeverity.Warning, "The CK3 mod is not in your current CK3 mod list",
                "Add 'Crowns & Blades Battles' to your playset in the Paradox Launcher, or start CK3 from the Crowns & Blades tray menu.",
                InstallFix.AddToPlayset));
        else items.Add(new("mod.enabled", InstallSeverity.Ok, "CK3 mod enabled in dlc_load.json", Ck3ModList.PathIn(t.Ck3UserDir)));
    }

    private static void CheckModule(InstallTargets t, List<InstallItem> items)
    {
        if (!GameInstall.IsBannerlordDir(t.BannerlordDir)) return;
        string dir = GameInstall.BannerlordModuleDir(t.BannerlordDir);
        if (!GameInstall.HasBannerlordModule(t.BannerlordDir))
        {
            items.Add(new("module.files", InstallSeverity.Warning, "The Bannerlord module is missing",
                $"{dir} is missing or incomplete. Run the Crowns & Blades installer again.", InstallFix.Reinstall));
            return;
        }
        string? version = ModuleVersion(dir);
        items.Add(new("module.files", InstallSeverity.Ok, $"Bannerlord module {version ?? "(version unknown)"} installed", dir));
        if (version is not null && !string.Equals(version, BridgeVersion.Module, StringComparison.OrdinalIgnoreCase))
            items.Add(new("module.version", InstallSeverity.Warning, "The Bannerlord module does not match the bridge",
                $"The Bannerlord module is {version}, the bridge expects {BridgeVersion.Module}. Run the Crowns & Blades installer again.",
                InstallFix.Reinstall));
        if (IsLink(dir))
            items.Add(new("module.link", InstallSeverity.Ok, "Bannerlord module is a link (development setup)", dir));
    }

    // ------------------------------------------------------------------------------------------------------------ readers

    public readonly record struct ModDescriptor(string? Dir, string? Version);

    /// <summary>Reads &lt;user&gt;/mod/ck3xbl.mod: its path= (absolute, or relative to the user folder) and the version of the mod it
    /// points to (that folder's descriptor.mod, else the outer descriptor).</summary>
    public static ModDescriptor ReadModDescriptor(string ck3UserDir)
    {
        string outer = Path.Combine(ck3UserDir, "mod", ModDescriptorFile);
        string? text = TryReadText(outer);
        if (text is null) return default;
        string? path = DescriptorValue(text, "path");
        string? dir = null;
        if (path is not null)
        {
            try { dir = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ck3UserDir, path)); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        string? inner = dir is null ? null : TryReadText(Path.Combine(dir, "descriptor.mod"));
        return new ModDescriptor(dir, (inner is null ? null : DescriptorValue(inner, "version")) ?? DescriptorValue(text, "version"));
    }

    /// <summary>key="value" of a Paradox descriptor.</summary>
    public static string? DescriptorValue(string text, string key) =>
        Regex.Match(text, $@"(?m)^\s*{Regex.Escape(key)}\s*=\s*""(?<v>[^""]*)""") is { Success: true } m ? m.Groups["v"].Value : null;

    /// <summary>Version/@value of a module's SubModule.xml ("v0.9.0"); null if unreadable.</summary>
    public static string? ModuleVersion(string moduleDir)
    {
        try
        {
            string file = Path.Combine(moduleDir, "SubModule.xml");
            return File.Exists(file) ? XDocument.Load(file).Root?.Element("Version")?.Attribute("value")?.Value : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException) { return null; }
    }

    public static bool IsLink(string dir)
    {
        try { return Directory.Exists(dir) && new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public static string Describe(GameEdition edition) => edition switch
    {
        GameEdition.Steam => "Steam",
        GameEdition.Gog => "GOG",
        GameEdition.Epic => "Epic Games",
        GameEdition.Xbox => "Xbox app",
        _ => "store unknown",
    };

    private static string? TryReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
