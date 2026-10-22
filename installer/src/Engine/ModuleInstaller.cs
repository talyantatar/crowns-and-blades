// Writes and removes the Bannerlord module (<BL>\Modules\CK3Bridge). Decisions (remove a development link, move a foreign folder
// aside) are made before this runs, by the wizard or the command line, so the elevated helper can run it without asking.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CK3xBL.Setup.Util;

namespace CK3xBL.Setup.Engine;

internal sealed class ModuleRequest
{
    public string BlDir = "";
    public bool RemoveLink;
    /// <summary>Move an existing (foreign) CK3Bridge folder here first; null: none. Always under <see cref="BackupRoot"/>.</summary>
    public string? MoveAsideTo;
    /// <summary>Restore: the player's own CK3Bridge folder that an install moved aside, put back into <see cref="ModuleDir"/>.</summary>
    public string? RestoreFrom;
    /// <summary>%LOCALAPPDATA%\ck3xbl\setup-backups of the player who runs setup: the only place backups are moved to or from.</summary>
    public string? BackupRoot;
    /// <summary>Module files an earlier install wrote (removed when the new payload no longer has them and they are unchanged).</summary>
    public List<InstalledFile> PreviousFiles = new();

    public string ModulesDir => Path.Combine(BlDir, "Modules");
    public string ModuleDir => Path.Combine(ModulesDir, Product.ModuleFolder);
}

internal sealed class ModuleResult
{
    public bool Ok;
    public string Message = "";
    public List<InstalledFile> Files = new();
    public List<MovedAside> MovedAside = new();
    public List<string> RemovedLinks = new();
    /// <summary>Uninstall: files that could not be removed (kept in the manifest for another try).</summary>
    public List<InstalledFile> Remaining = new();
}

internal static class ModuleInstaller
{
    /// <summary>Installs the module. A failure part-way returns Ok=false with every file and move done so far, so the
    /// install record still lists them.</summary>
    public static ModuleResult Install(Payload payload, ModuleRequest req, Changes changes, SetupLog log)
    {
        var result = new ModuleResult();
        var target = req.ModuleDir;

        try
        {
            if (req.RemoveLink && FileOps.IsReparsePoint(target))
            {
                var linkTarget = Native.FinalPath(target) ?? "?";
                changes.Do($"remove the link {target} (it pointed to {linkTarget}; that folder is not touched)", () => FileOps.RemoveLink(target));
                result.RemovedLinks.Add($"{target} -> {linkTarget}");
            }
            else if (FileOps.IsReparsePoint(target))
            {
                result.Message = $"{target} is a link; nothing was written through it";
                return result;
            }

            if (req.MoveAsideTo is not null && Directory.Exists(target))
            {
                if (FileOps.LinkInside(target) is { } inner)
                {
                    result.Message = $"{inner} is a link; the existing module folder was left unchanged";
                    return result;
                }
                // Recorded before the move: a move that fails half-way still leaves a record of where the copy went.
                result.MovedAside.Add(new MovedAside { Original = target, Backup = req.MoveAsideTo });
                changes.Do($"move the existing {target} to {req.MoveAsideTo}", () => FileOps.Move(target, req.MoveAsideTo));
            }

            var files = payload.FilesWithPrefix(PayloadManifest.ModulePrefix).ToList();
            var newPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                var dest = Path.Combine(target, f.Path.Substring(PayloadManifest.ModulePrefix.Length).Replace('/', '\\'));
                newPaths.Add(FileOps.Full(dest));
                if (FileOps.TrySha256File(dest) != f.Sha256) changes.Do($"write {dest}", () => payload.Extract(f, dest));
                result.Files.Add(new InstalledFile { Path = dest, Sha256 = f.Sha256, Size = f.Size, Target = Targets.BlModule });
            }
            RemoveStale(req.PreviousFiles, newPaths, "", changes, log);

            result.Ok = true;
            result.Message = $"{files.Count} files in {target}";
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log.Warn($"the module step stopped: {e.Message}");
            result.Ok = false;
            result.Message = $"{target}: {e.Message}";
            return result;
        }
    }

    public static ModuleResult Uninstall(ModuleRequest req, Changes changes, SetupLog log)
    {
        var result = new ModuleResult { Ok = true };
        var target = req.ModuleDir;
        if (FileOps.LinkOnPath(req.ModulesDir, target) is { } link)
        {
            result.Message = $"{link} is a link now (a development checkout?); left unchanged";
            return result;
        }
        int removed = 0;
        var touched = new List<InstalledFile>();
        foreach (var f in req.PreviousFiles)
        {
            if (!FileOps.IsUnder(f.Path, target))
            {
                log.Warn($"{f.Path} is not inside {target}; not deleted");
                continue;
            }
            touched.Add(f);
            if (!File.Exists(f.Path)) continue;
            if (FileOps.LinkOnPath(req.ModulesDir, f.Path) is not null) continue;
            if (FileOps.TrySha256File(f.Path) != f.Sha256)
            {
                log.Warn($"{f.Path} was changed after setup installed it; kept");
                continue;
            }
            try
            {
                changes.Do($"delete {f.Path}", () => FileOps.DeleteFile(f.Path));
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Warn($"could not delete {f.Path}: {e.Message}");
                result.Remaining.Add(f);
                result.Ok = false;
            }
        }
        if (!changes.DryRun)
            foreach (var dir in touched.Select(f => Path.GetDirectoryName(f.Path)!).Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(d => d.Length))
                FileOps.DeleteEmptyDirsUpward(dir, req.ModulesDir);
        result.Message = result.Ok ? $"removed {removed} files from {target}" : $"{result.Remaining.Count} files in {target} could not be removed";
        return result;
    }

    /// <summary>Puts the player's own CK3Bridge folder back from <see cref="ModuleRequest.RestoreFrom"/> (uninstall), when the
    /// module's place is free again.</summary>
    public static ModuleResult Restore(ModuleRequest req, Changes changes, SetupLog log)
    {
        var target = req.ModuleDir;
        var from = req.RestoreFrom ?? throw new InvalidDataException("nothing to put back");
        if (!Directory.Exists(from)) return new ModuleResult { Ok = true, Message = $"{from} no longer exists; nothing to put back" };
        if (Directory.Exists(target) || File.Exists(target) || FileOps.IsReparsePoint(target))
            return new ModuleResult { Ok = false, Message = $"{target} is in use again; the earlier copy stays in {from}" };
        if (FileOps.LinkInside(from) is { } link)
            return new ModuleResult { Ok = false, Message = $"{link} is a link; {from} was left where it is" };
        changes.Do($"put back {target} from {from}", () => FileOps.Move(from, target));
        log.Info($"put back {target}");
        return new ModuleResult { Ok = true, Message = $"put back {target}" };
    }

    /// <summary>Deletes files an earlier install wrote that the new one does not, when unchanged since; then empty folders.
    /// Only files in Crowns & Blades's own folders count (<see cref="RecordRules"/>; <paramref name="installDir"/> is the
    /// earlier install folder): a record never makes setup delete anything elsewhere.</summary>
    public static void RemoveStale(IEnumerable<InstalledFile> previous, HashSet<string> keep, string installDir, Changes changes, SetupLog log)
    {
        foreach (var old in previous)
        {
            if (keep.Contains(FileOps.Full(old.Path)) || !File.Exists(old.Path)) continue;
            if (!RecordRules.Place(old, installDir, out var boundary))
            {
                log.Warn($"{old.Path} is not in a Crowns & Blades folder; not deleted");
                continue;
            }
            if (FileOps.LinkOnPath(boundary, old.Path) is not null) continue;
            if (FileOps.TrySha256File(old.Path) != old.Sha256)
            {
                log.Warn($"{old.Path} is no longer part of Crowns & Blades but was changed after install; kept");
                continue;
            }
            changes.Do($"delete {old.Path} (no longer part of this version)", () =>
            {
                FileOps.DeleteFile(old.Path);
                FileOps.DeleteEmptyDirsUpward(Path.GetDirectoryName(old.Path)!, boundary);
            });
        }
    }
}
