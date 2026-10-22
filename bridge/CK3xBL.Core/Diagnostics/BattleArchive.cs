// Per-battle archive: %LOCALAPPDATA%\ck3xbl\battles\<id>\{snapshot,request,result,outcome}.json, for debugging and support.
// It also brings back the parent of a reinforcement clash after a bridge restart (PROTOCOL §14.6), and keeps the trace of a steered
// battle (steering.json, pulse.jsonl; PROTOCOL §14.7).
using CK3xBL.Core.Json;

namespace CK3xBL.Core.Diagnostics;

public interface IBattleArchive
{
    /// <summary>Stores <paramref name="value"/> as battles/&lt;battleId&gt;/&lt;name&gt;.json. Never throws.</summary>
    void Save(string battleId, string name, object value);

    /// <summary>Reads &lt;name&gt;.json of the newest battle folder whose id starts with <paramref name="idPrefix"/> (e.g. "b17_" for
    /// battle 17 of any session); null when there is none or it cannot be read. Never throws.</summary>
    T? TryLoad<T>(string idPrefix, string name) where T : class => null;

    /// <summary>Appends <paramref name="value"/> as one JSON line to battles/&lt;battleId&gt;/&lt;name&gt;.jsonl. Never throws.</summary>
    void Append(string battleId, string name, object value) { }
}

public sealed class BattleArchive(string root, IJournal journal) : IBattleArchive
{
    public void Save(string battleId, string name, object value)
    {
        try
        {
            ProtocolJson.WriteAtomic(Path.Combine(root, SafeName(battleId), name + ".json"), value, indented: true);
        }
        catch (Exception e)
        {
            journal.Warn("archive", $"could not archive {name} for {battleId}: {e.Message}");
        }
    }

    public T? TryLoad<T>(string idPrefix, string name) where T : class
    {
        try
        {
            if (!Directory.Exists(root)) return null;
            string prefix = SafeName(idPrefix);
            string? newest = Directory.EnumerateDirectories(root)
                .Where(dir => Path.GetFileName(dir).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(dir => Path.Combine(dir, name + ".json"))
                .Where(File.Exists)
                .MaxBy(File.GetLastWriteTimeUtc);
            return newest is null ? null : ProtocolJson.TryRead<T>(newest);
        }
        catch (Exception e)
        {
            journal.Warn("archive", $"could not read {name} of {idPrefix}*: {e.Message}");
            return null;
        }
    }

    public void Append(string battleId, string name, object value)
    {
        try
        {
            string dir = Path.Combine(root, SafeName(battleId));
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, name + ".jsonl"), ProtocolJson.Serialize(value) + "\n");
        }
        catch (Exception e)
        {
            journal.Warn("archive", $"could not append to {name} for {battleId}: {e.Message}");
        }
    }

    /// <summary>Default number of battle folders kept by <see cref="Prune"/>.</summary>
    public const int DefaultKeep = 50;

    /// <summary>Deletes all but the <paramref name="keep"/> newest battle folders (by last write time). Run at start, before any
    /// battle of this run is archived. Returns the number removed. Never throws.</summary>
    public int Prune(int keep = DefaultKeep)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(root)) return 0;
            var old = new DirectoryInfo(root).EnumerateDirectories()
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Skip(Math.Max(0, keep))
                .ToList();
            foreach (var dir in old)
            {
                try
                {
                    dir.Delete(recursive: true);
                    removed++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    journal.Warn("archive", $"could not remove old battle folder {dir.Name}: {e.Message}");
                }
            }
            if (removed > 0) journal.Info("archive", $"removed {removed} old battle folder(s), keeping the newest {keep}");
        }
        catch (Exception e)
        {
            journal.Warn("archive", $"could not prune {root}: {e.Message}");
        }
        return removed;
    }

    private static string SafeName(string id) =>
        string.Concat(id.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}
