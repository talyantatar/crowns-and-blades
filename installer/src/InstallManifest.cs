// install_manifest.json in the install folder: everything setup wrote, so upgrades and uninstall touch exactly that.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CK3xBL.Setup.Util;

namespace CK3xBL.Setup;

internal static class Targets
{
    public const string App = "app";
    public const string Uninstaller = "uninstaller";
    public const string Ck3Mod = "ck3mod";
    public const string Ck3Descriptor = "ck3descriptor";
    public const string BlModule = "blmodule";
}

/// <summary>Where a recorded file may lie for setup to delete it. A record, even a damaged or edited one, can never make setup
/// delete anything outside its own folders: the install folder, a ...\mod\ck3xbl folder (and ...\mod\ck3xbl.mod) or a
/// ...\Modules\CK3Bridge folder. Earlier places of those folders (Documents moved, another Bannerlord folder) still count.</summary>
internal static class RecordRules
{
    /// <summary>False when <paramref name="f"/> lies outside the folders of its kind. <paramref name="boundary"/>: the folder
    /// that link checks start below and empty-folder cleanup stops at (never deleted).</summary>
    public static bool Place(InstalledFile f, string installDir, out string boundary)
    {
        boundary = "";
        try
        {
            if (!System.IO.Path.IsPathRooted(f.Path)) return false;
            var path = FileOps.Full(f.Path);
            switch (f.Target)
            {
                case Targets.App:
                case Targets.Uninstaller:
                    if (installDir.Length == 0 || !FileOps.IsUnder(path, installDir) || FileOps.PathEquals(path, installDir)) return false;
                    boundary = FileOps.ParentOrSelf(installDir);
                    return true;
                case Targets.Ck3Mod:
                    return Ancestor(path, Product.ModFolder, "mod", out boundary);
                case Targets.Ck3Descriptor:
                    var dir = System.IO.Path.GetDirectoryName(path);
                    if (dir is null || !Named(path, Product.ModDescriptor) || !Named(dir, "mod")) return false;
                    boundary = dir;
                    return true;
                case Targets.BlModule:
                    return Ancestor(path, Product.ModuleFolder, "Modules", out boundary);
                default:
                    return false;
            }
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool Named(string path, string name) => string.Equals(System.IO.Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase);

    /// <summary>A folder <paramref name="name"/> inside a folder <paramref name="parentName"/> above <paramref name="path"/>;
    /// <paramref name="parent"/> is that parent.</summary>
    private static bool Ancestor(string path, string name, string parentName, out string parent)
    {
        parent = "";
        for (var d = System.IO.Path.GetDirectoryName(path); d is not null; d = System.IO.Path.GetDirectoryName(d))
        {
            var up = System.IO.Path.GetDirectoryName(d);
            if (up is not null && Named(d, name) && Named(up, parentName))
            {
                parent = up;
                return true;
            }
        }
        return false;
    }
}

internal sealed class InstalledFile
{
    public string Path = "";
    public string Sha256 = "";
    public long Size;
    public string Target = "";
}

/// <summary>A file or folder that was in our way and was moved aside (put back by uninstall when its place is free).</summary>
internal sealed class MovedAside
{
    public string Original = "";
    public string Backup = "";
}

internal sealed class DlcLoadRecord
{
    public string Path = "";
    /// <summary>True when setup added our entry (false: it was already there, nothing to undo).</summary>
    public bool Added;
    /// <summary>True when dlc_load.json did not exist and setup created it.</summary>
    public bool CreatedFile;
    /// <summary>The backup setup made of the file before adding the entry (null: none of ours).</summary>
    public string? Backup;
    /// <summary>SHA-256 of the file as setup left it: unchanged since, uninstall restores the backup; changed, it removes only our entry.</summary>
    public string WrittenSha256 = "";
}

internal sealed class InstallManifest
{
    public const int CurrentFormat = 1;

    public string Version = "";
    public DateTime InstalledUtc;
    public string InstallDir = "";
    public string SharedDir = "";
    public string? Ck3Dir;
    public string Ck3UserDir = "";
    /// <summary>True when the player chose the CK3 user folder (command line or wizard) instead of the Documents default; an
    /// upgrade then keeps it even if the Documents folder moved (OneDrive and other Known Folder moves).</summary>
    public bool Ck3UserDirCustom;
    public string? BlDir;
    public List<InstalledFile> Files = new();
    public DlcLoadRecord? DlcLoad;
    public List<MovedAside> MovedAside = new();
    public List<string> Shortcuts = new();
    /// <summary>Registry keys (paths under HKCU, sandbox prefix included) setup created.</summary>
    public List<string> RegistryKeys = new();
    /// <summary>Links setup removed on request (development junctions), for the record.</summary>
    public List<string> RemovedLinks = new();

    public IEnumerable<InstalledFile> FilesOf(string target) => Files.Where(f => f.Target == target);

    public static InstallManifest? TryLoad(string path, out string? problem)
    {
        problem = null;
        if (!File.Exists(path)) return null;
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception e) when (e is FormatException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            problem = $"{path} could not be read ({e.Message})";
            return null;
        }
    }

    public static InstallManifest Parse(string json)
    {
        if (Json.Parse(json) is not JsonObject o) throw new InvalidDataException("not a JSON object");
        if (o.GetString("product") != Product.InstallFolder) throw new InvalidDataException("not a CK3xBL install manifest");
        if ((o.GetLong("format") ?? 0) != CurrentFormat) throw new InvalidDataException("unknown install manifest format");
        var m = new InstallManifest
        {
            Version = o.GetString("version") ?? "",
            InstallDir = o.GetString("installDir") ?? "",
            SharedDir = o.GetString("sharedDir") ?? "",
            Ck3Dir = o.GetString("ck3Dir"),
            Ck3UserDir = o.GetString("ck3UserDir") ?? "",
            Ck3UserDirCustom = o.GetBool("ck3UserDirCustom"),
            BlDir = o.GetString("blDir"),
        };
        DateTime.TryParse(o.GetString("installedUtc"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out m.InstalledUtc);
        foreach (var item in o.GetArray("files") ?? new List<object?>())
            if (item is JsonObject f && f.GetString("path") is { Length: > 0 } p)
                m.Files.Add(new InstalledFile
                {
                    Path = p,
                    Sha256 = f.GetString("sha256") ?? "",
                    Size = f.GetLong("size") ?? 0,
                    Target = f.GetString("target") ?? "",
                });
        if (o.GetObject("dlcLoad") is { } d && d.GetString("path") is { Length: > 0 } dp)
            m.DlcLoad = new DlcLoadRecord
            {
                Path = dp,
                Added = d.GetBool("added"),
                CreatedFile = d.GetBool("createdFile"),
                Backup = d.GetString("backup"),
                WrittenSha256 = d.GetString("writtenSha256") ?? "",
            };
        foreach (var item in o.GetArray("movedAside") ?? new List<object?>())
            if (item is JsonObject a && a.GetString("original") is { Length: > 0 } orig && a.GetString("backup") is { Length: > 0 } bak)
                m.MovedAside.Add(new MovedAside { Original = orig, Backup = bak });
        m.Shortcuts.AddRange(Strings(o.GetArray("shortcuts")));
        m.RegistryKeys.AddRange(Strings(o.GetArray("registryKeys")));
        m.RemovedLinks.AddRange(Strings(o.GetArray("removedLinks")));
        return m;
    }

    private static IEnumerable<string> Strings(List<object?>? list) =>
        (list ?? new List<object?>()).OfType<string>().Where(s => s.Length > 0);

    public string ToJson()
    {
        var o = new JsonObject
        {
            ["product"] = Product.InstallFolder,
            ["format"] = new JsonNumber(CurrentFormat),
            ["version"] = Version,
            ["installedUtc"] = InstalledUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["installDir"] = InstallDir,
            ["sharedDir"] = SharedDir,
            ["ck3Dir"] = Ck3Dir,
            ["ck3UserDir"] = Ck3UserDir,
            ["ck3UserDirCustom"] = Ck3UserDirCustom,
            ["blDir"] = BlDir,
            ["files"] = Files.Select(f => (object?)new JsonObject
            {
                ["path"] = f.Path,
                ["sha256"] = f.Sha256,
                ["size"] = new JsonNumber(f.Size),
                ["target"] = f.Target,
            }).ToList(),
            ["dlcLoad"] = DlcLoad is null ? null : new JsonObject
            {
                ["path"] = DlcLoad.Path,
                ["added"] = DlcLoad.Added,
                ["createdFile"] = DlcLoad.CreatedFile,
                ["backup"] = DlcLoad.Backup,
                ["writtenSha256"] = DlcLoad.WrittenSha256,
            },
            ["movedAside"] = MovedAside.Select(a => (object?)new JsonObject { ["original"] = a.Original, ["backup"] = a.Backup }).ToList(),
            ["shortcuts"] = Shortcuts.Cast<object?>().ToList(),
            ["registryKeys"] = RegistryKeys.Cast<object?>().ToList(),
            ["removedLinks"] = RemovedLinks.Cast<object?>().ToList(),
        };
        return Json.Write(o, indented: true) + "\n";
    }

    public void Save(string path) => FileOps.WriteAtomic(path, FileOps.Utf8NoBom.GetBytes(ToJson()));
}
