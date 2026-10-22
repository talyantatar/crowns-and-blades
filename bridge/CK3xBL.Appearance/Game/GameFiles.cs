// Read-only view of the CK3 install plus the enabled mods of the active playset (Documents/dlc_load.json).
// CK3 merges a folder by file name: a mod file with the same relative path replaces the vanilla one, files are
// processed in name order, and a mod's replace_path drops the vanilla folder. Gene order, ethnicities and coats of
// arms therefore follow the playset the game runs with. Caveat: a save made under a different playset (e.g. AGOT
// saves while only ck3xbl is enabled) is decoded against the current playset; the meta portrait still works because
// it names its genes, while stored dna= strings are rejected when their length does not match.
using System.Text.Json;
using CK3xBL.Appearance.Pdx;

namespace CK3xBL.Appearance.Game;

internal sealed class GameFiles
{
    private sealed record Source(string Root, string Name, HashSet<string> ReplacePaths);

    private readonly List<Source> _sources;   // vanilla first, then mods in load order (later wins)

    public string GameDir { get; }
    public IReadOnlyList<string> ModNames => _sources.Skip(1).Select(s => s.Name).ToList();

    private GameFiles(string gameDir, List<Source> sources) { GameDir = gameDir; _sources = sources; }

    /// <summary>Finds the "game" folder from either the install root or the game folder itself.</summary>
    public static string? ResolveGameDir(string? ck3GameDir)
    {
        if (string.IsNullOrWhiteSpace(ck3GameDir)) return null;
        foreach (var cand in new[] { Path.Combine(ck3GameDir, "game"), ck3GameDir })
            if (Directory.Exists(Path.Combine(cand, "common", "genes"))) return Path.GetFullPath(cand);
        return null;
    }

    public static GameFiles? Locate(string? ck3GameDir, string? ck3DocumentsDir, Action<string> log)
    {
        var game = ResolveGameDir(ck3GameDir);
        if (game == null)
        {
            log($"CK3 game folder not found under '{ck3GameDir}'");
            return null;
        }
        var sources = new List<Source> { new(game, "vanilla", new HashSet<string>()) };
        if (!string.IsNullOrWhiteSpace(ck3DocumentsDir)) sources.AddRange(ReadPlayset(ck3DocumentsDir!, log));
        return new GameFiles(game, sources);
    }

    private static IEnumerable<Source> ReadPlayset(string docs, Action<string> log)
    {
        var list = new List<Source>();
        string dlcLoad = Path.Combine(docs, "dlc_load.json");
        if (!File.Exists(dlcLoad)) return list;
        try
        {
            using var doc = JsonDocument.Parse(ReadShared(dlcLoad));
            if (!doc.RootElement.TryGetProperty("enabled_mods", out var mods) || mods.ValueKind != JsonValueKind.Array) return list;
            foreach (var m in mods.EnumerateArray())
            {
                string? rel = m.GetString();
                if (string.IsNullOrEmpty(rel)) continue;
                string descriptor = Path.IsPathRooted(rel) ? rel : Path.Combine(docs, rel);
                if (!File.Exists(descriptor)) continue;
                var node = PdxTextParser.Parse(ReadShared(descriptor));
                string? path = node.Str("path") ?? node.Str("archive");
                if (string.IsNullOrEmpty(path)) continue;
                if (!Path.IsPathRooted(path)) path = Path.Combine(docs, path);
                if (!Directory.Exists(path)) continue;
                var replace = new HashSet<string>(node.All("replace_path").Select(v => Norm(v.Text ?? "")),
                                                  StringComparer.OrdinalIgnoreCase);
                list.Add(new Source(Path.GetFullPath(path), node.Str("name") ?? Path.GetFileName(path), replace));
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            log($"playset not read ({e.Message}); using vanilla files only");
        }
        return list;
    }

    /// <summary>All files of a folder after playset merging, ordered by file name (CK3's processing order).</summary>
    public IReadOnlyList<string> ListFiles(string relDir, string pattern = "*.txt")
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string norm = Norm(relDir);
        foreach (var src in _sources)
        {
            if (src.ReplacePaths.Contains(norm)) byName.Clear();
            string dir = Path.Combine(src.Root, relDir);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, pattern)) byName[Path.GetFileName(f)] = f;
        }
        return byName.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Value).ToList();
    }

    /// <summary>The effective copy of one file (last mod that has it, else vanilla), or null.</summary>
    public string? FindFile(string relPath)
    {
        for (int i = _sources.Count - 1; i >= 0; i--)
        {
            string p = Path.Combine(_sources[i].Root, relPath);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    private static string Norm(string p) => p.Replace('\\', '/').Trim('/');
}
