// The CK3 mod list the game reads at start: <CK3 user dir>\dlc_load.json {"enabled_mods":["mod/x.mod",...],"disabled_dlcs":[...]}.
// The Paradox Launcher rewrites this file from the selected playset every time it is used to play, so an entry added here lasts only
// until then; the launcher's own database (launcher-v2.sqlite) is never read or written. Changes touch enabled_mods only, keep every
// other key and entry as they are, back the original up once (dlc_load.json.ck3xbl_backup, never overwritten) and are written
// atomically, compact and UTF-8 without BOM, as the launcher writes them. Shared by the bridge and the installer.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CK3xBL.Core.Json;

namespace CK3xBL.Core.Ck3;

public enum ModListChange
{
    /// <summary>The file was changed.</summary>
    Changed,
    /// <summary>Nothing to do: the entry was already there (Enable) or already absent (Disable).</summary>
    Unchanged,
    /// <summary>CK3 is running; it reads the list only at start, and the launcher may be about to rewrite it.</summary>
    Ck3Running,
    /// <summary>dlc_load.json is not valid JSON (or not the expected shape); it is never overwritten.</summary>
    Unreadable,
    /// <summary>The file could not be read or written.</summary>
    Failed,
}

public static class Ck3ModList
{
    public const string FileName = "dlc_load.json";
    public const string BackupSuffix = ".ck3xbl_backup";
    /// <summary>Our entry, relative to the CK3 user folder (as the launcher writes entries for mods in Documents\...\mod).</summary>
    public const string Entry = "mod/ck3xbl.mod";
    private const string EnabledKey = "enabled_mods";

    /// <summary>Compact, and non-ASCII paths kept as they are (as the launcher writes them).</summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string PathIn(string ck3UserDir) => Path.Combine(ck3UserDir, FileName);

    /// <summary>Whether the list holds our entry (relative or absolute, any slash direction or case). False when the file is missing
    /// or unreadable.</summary>
    public static bool IsEnabled(string ck3UserDir) =>
        TryRead(ck3UserDir, out var root, out _) == null && Enabled(root!).Any(e => IsOurEntry(ck3UserDir, e));

    /// <summary>Appends our entry to enabled_mods (creating the file when there is none).</summary>
    public static ModListChange Enable(string ck3UserDir, Func<bool>? ck3Running = null)
    {
        if ((ck3Running ?? Ck3Locator.IsRunning)()) return ModListChange.Ck3Running;
        string path = PathIn(ck3UserDir);
        JsonObject root;
        if (!File.Exists(path)) root = new JsonObject { [EnabledKey] = new JsonArray(), ["disabled_dlcs"] = new JsonArray() };
        else if (TryRead(ck3UserDir, out var read, out bool unreadable) is { } failure) return unreadable ? ModListChange.Unreadable : failure;
        else root = read!;

        if (root[EnabledKey] is null) root[EnabledKey] = new JsonArray();
        if (root[EnabledKey] is not JsonArray list) return ModListChange.Unreadable;
        if (list.Any(e => e?.GetValueKind() == JsonValueKind.String && IsOurEntry(ck3UserDir, e.GetValue<string>()))) return ModListChange.Unchanged;
        list.Add(Entry);
        return Write(path, root);
    }

    /// <summary>Removes every entry naming our mod from enabled_mods; nothing else changes.</summary>
    public static ModListChange Disable(string ck3UserDir, Func<bool>? ck3Running = null)
    {
        if ((ck3Running ?? Ck3Locator.IsRunning)()) return ModListChange.Ck3Running;
        string path = PathIn(ck3UserDir);
        if (!File.Exists(path)) return ModListChange.Unchanged;
        if (TryRead(ck3UserDir, out var root, out bool unreadable) is { } failure) return unreadable ? ModListChange.Unreadable : failure;
        if (root![EnabledKey] is not JsonArray list) return root[EnabledKey] is null ? ModListChange.Unchanged : ModListChange.Unreadable;
        int removed = 0;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i]?.GetValueKind() != JsonValueKind.String || !IsOurEntry(ck3UserDir, list[i]!.GetValue<string>())) continue;
            list.RemoveAt(i);
            removed++;
        }
        return removed == 0 ? ModListChange.Unchanged : Write(path, root);
    }

    /// <summary>Our entry, written relative ("mod/ck3xbl.mod") or as an absolute path to the same descriptor.</summary>
    public static bool IsOurEntry(string ck3UserDir, string entry)
    {
        string normalized = entry.Replace('\\', '/').Trim();
        if (string.Equals(normalized, Entry, StringComparison.OrdinalIgnoreCase)) return true;
        if (!Path.IsPathRooted(normalized)) return false;
        try
        {
            string ours = Path.GetFullPath(Path.Combine(ck3UserDir, "mod", "ck3xbl.mod"));
            return string.Equals(Path.GetFullPath(normalized), ours, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static IEnumerable<string> Enabled(JsonObject root) =>
        root[EnabledKey] is JsonArray list
            ? list.Where(e => e?.GetValueKind() == JsonValueKind.String).Select(e => e!.GetValue<string>())
            : [];

    /// <summary>Reads the file (shared with a launcher writing it). Returns null on success; otherwise the failure, with
    /// <paramref name="unreadable"/> set when the content (not the disk) is the problem.</summary>
    private static ModListChange? TryRead(string ck3UserDir, out JsonObject? root, out bool unreadable)
    {
        root = null;
        unreadable = false;
        string text;
        try
        {
            using var fs = new FileStream(PathIn(ck3UserDir), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ModListChange.Failed; }
        try
        {
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject;
        }
        catch (JsonException) { }
        if (root is not null) return null;
        unreadable = true;
        return ModListChange.Unreadable;
    }

    private static ModListChange Write(string path, JsonObject root)
    {
        try
        {
            string backup = path + BackupSuffix;
            if (File.Exists(path) && !File.Exists(backup)) File.Copy(path, backup);   // the true original, kept once
            AtomicFile.WriteAllText(path, root.ToJsonString(WriteOptions), withBom: false);
            return ModListChange.Changed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ModListChange.Failed; }
    }
}
