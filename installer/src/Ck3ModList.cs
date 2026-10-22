// dlc_load.json: the mod list CK3 reads when it starts. Setup adds one entry, "mod/ck3xbl.mod", and keeps everything else
// exactly as it was. Note: the Paradox Launcher rewrites this file from the selected playset every time it is used to play.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CK3xBL.Setup.Util;

namespace CK3xBL.Setup;

internal enum ModListOutcome
{
    Added,
    Created,
    AlreadyPresent,
    Unreadable,
    Restored,
    Deleted,
    EntryRemoved,
    NotPresent,
}

internal static class Ck3ModList
{
    public const string FileName = "dlc_load.json";
    public const string Entry = "mod/" + Product.ModDescriptor;
    public const string BackupSuffix = ".ck3xbl_backup";

    public static string PathIn(string ck3UserDir) => Path.Combine(ck3UserDir, FileName);

    /// <summary>"mod/ck3xbl.mod" in any case or slash style, or an absolute path to our descriptor.</summary>
    public static bool IsOurEntry(object? entry, string ck3UserDir)
    {
        if (entry is not string s || s.Trim().Length == 0) return false;
        var norm = s.Trim().Replace('\\', '/');
        if (norm.Equals(Entry, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return Path.IsPathRooted(s) && FileOps.PathEquals(s, Path.Combine(ck3UserDir, "mod", Product.ModDescriptor));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { return false; }
    }

    public static bool IsEnabled(string ck3UserDir)
    {
        var path = PathIn(ck3UserDir);
        try
        {
            if (!File.Exists(path)) return false;
            var text = FileOps.ReadUtf8(FileOps.ReadShared(path), out _);
            return Json.Parse(text) is JsonObject o && o.GetArray("enabled_mods") is { } mods && mods.Any(m => IsOurEntry(m, ck3UserDir));
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Describes what <see cref="Enable"/> would do, without writing.</summary>
    public static string PlanEnable(string ck3UserDir)
    {
        var path = PathIn(ck3UserDir);
        if (!File.Exists(path)) return $"create {path} with {Entry}";
        if (IsEnabled(ck3UserDir)) return $"{path} already lists {Entry}; unchanged";
        return $"add {Entry} to {path} (backup {path}{BackupSuffix})";
    }

    /// <summary>Adds our entry. <paramref name="previous"/> is the record of an earlier install (its backup is ours to refresh).</summary>
    public static ModListOutcome Enable(string ck3UserDir, DlcLoadRecord? previous, out DlcLoadRecord? record, out string message)
    {
        var path = PathIn(ck3UserDir);
        var backup = path + BackupSuffix;
        record = null;

        if (!File.Exists(path))
        {
            var fresh = new JsonObject
            {
                ["enabled_mods"] = new List<object?> { Entry },
                ["disabled_dlcs"] = new List<object?>(),
            };
            var bytes = FileOps.EncodeUtf8(Json.Write(fresh, indented: false), bom: false);
            FileOps.WriteAtomic(path, bytes);
            record = new DlcLoadRecord { Path = path, Added = true, CreatedFile = true, WrittenSha256 = FileOps.Sha256(bytes) };
            message = $"created {path} with {Entry}";
            return ModListOutcome.Created;
        }

        var original = FileOps.ReadShared(path);
        if (!TryEdit(original, ck3UserDir, add: true, out var edited, out bool present, out var problem))
        {
            message = $"{path} is not valid JSON ({problem}); it was left unchanged";
            record = previous;
            return ModListOutcome.Unreadable;
        }
        if (present)
        {
            // Already listed: by an earlier install of ours (keep that record, it still knows how to undo) or by the player.
            record = previous is { Added: true } ? previous : null;
            message = $"{path} already lists {Entry}";
            return ModListOutcome.AlreadyPresent;
        }

        // Back up the file as it is now, unless a backup exists that is not ours (then it is left exactly as it is).
        bool backupIsOurs = previous?.Backup is not null && FileOps.PathEquals(previous.Backup, backup);
        string? recordBackup = null;
        if (!File.Exists(backup) || backupIsOurs)
        {
            FileOps.WriteAtomic(backup, original);
            recordBackup = backup;
        }
        FileOps.WriteAtomic(path, edited!);
        record = new DlcLoadRecord { Path = path, Added = true, CreatedFile = false, Backup = recordBackup, WrittenSha256 = FileOps.Sha256(edited!) };
        message = $"added {Entry} to {path}" + (recordBackup is not null ? $" (backup {Path.GetFileName(backup)})" : " (an older backup exists and was kept)");
        return ModListOutcome.Added;
    }

    /// <summary>Undoes <see cref="Enable"/>: unchanged since setup wrote it, the file goes back to exactly what it was; changed
    /// since (the launcher or the player edited it), only our entry is removed.</summary>
    public static ModListOutcome Disable(DlcLoadRecord record, string ck3UserDir, out string message)
    {
        var path = record.Path.Length > 0 ? record.Path : PathIn(ck3UserDir);
        if (!record.Added)
        {
            message = $"{path}: the entry was not added by setup; left unchanged";
            return ModListOutcome.NotPresent;
        }
        if (!File.Exists(path))
        {
            message = $"{path} no longer exists";
            return ModListOutcome.NotPresent;
        }

        var current = FileOps.ReadShared(path);
        bool unchanged = FileOps.Sha256(current) == record.WrittenSha256;
        if (unchanged && record.CreatedFile)
        {
            FileOps.DeleteFile(path);
            message = $"deleted {path} (setup had created it)";
            return ModListOutcome.Deleted;
        }
        if (unchanged && record.Backup is not null && File.Exists(record.Backup))
        {
            FileOps.WriteAtomic(path, File.ReadAllBytes(record.Backup));
            FileOps.DeleteFile(record.Backup);
            message = $"restored {path} from its backup";
            return ModListOutcome.Restored;
        }

        if (!TryEdit(current, ck3UserDir, add: false, out var edited, out bool present, out var problem))
        {
            message = $"{path} is not valid JSON ({problem}); it was left unchanged";
            return ModListOutcome.Unreadable;
        }
        if (!present)
        {
            message = $"{path} no longer lists {Entry}";
            return ModListOutcome.NotPresent;
        }
        FileOps.WriteAtomic(path, edited!);
        message = $"removed {Entry} from {path}" +
                  (record.Backup is not null && File.Exists(record.Backup) ? $"; the old backup {Path.GetFileName(record.Backup)} was kept" : "");
        return ModListOutcome.EntryRemoved;
    }

    /// <summary>Adds or removes our entry in the JSON text, changing nothing else. <paramref name="present"/>: the entry was
    /// there before. Keeps a BOM if the file had one.</summary>
    internal static bool TryEdit(byte[] original, string ck3UserDir, bool add, out byte[]? edited, out bool present, out string? problem)
    {
        edited = null;
        present = false;
        problem = null;
        var text = FileOps.ReadUtf8(original, out bool bom);
        object? root;
        try { root = Json.Parse(text); }
        catch (FormatException e) { problem = e.Message; return false; }
        if (root is not JsonObject o) { problem = "the top level is not an object"; return false; }

        var mods = o["enabled_mods"];     // missing or null: an empty list
        if (mods is not null && mods is not List<object?>) { problem = "enabled_mods is not a list"; return false; }

        var list = (List<object?>?)mods ?? new List<object?>();
        present = list.Any(m => IsOurEntry(m, ck3UserDir));
        if (add)
        {
            if (present) return true;
            list.Add(Entry);
            o["enabled_mods"] = list;
        }
        else
        {
            if (!present) return true;
            list.RemoveAll(m => IsOurEntry(m, ck3UserDir));
        }
        edited = FileOps.EncodeUtf8(Json.Write(o, indented: false), bom);
        return true;
    }
}
