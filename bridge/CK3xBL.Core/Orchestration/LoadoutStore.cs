// The Arsenal's loadouts (PROTOCOL §51): what each player character chose to carry in Bannerlord, by CK3 character id, kept in
// loadouts.json in the shared folder. The mapper dresses the player in his loadout wherever the items are still his (KnightBuffPlanner
// checks each against his gear, stash and kit); the Arsenal's result replaces it.
using System.Text.Json;

namespace CK3xBL.Core.Orchestration;

public sealed class LoadoutStore
{
    public const string FileName = "loadouts.json";
    private readonly string _path;
    private readonly object _gate = new();

    public LoadoutStore(string sharedDir) => _path = Path.Combine(sharedDir, FileName);

    /// <summary>Every saved loadout (character id -> slot -> item id); empty when there is none or the file cannot be read.</summary>
    public Dictionary<long, Dictionary<string, string>> Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return new();
                var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(_path)) ?? new();
                var all = new Dictionary<long, Dictionary<string, string>>();
                foreach (var (id, slots) in raw)
                    if (long.TryParse(id, out long ck3Id) && slots is not null) all[ck3Id] = new Dictionary<string, string>(slots, StringComparer.Ordinal);
                return all;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                return new();
            }
        }
    }

    /// <summary>Keeps <paramref name="loadout"/> as the character's loadout (atomically, beside the others).</summary>
    public void Save(long ck3Id, IReadOnlyDictionary<string, string> loadout)
    {
        lock (_gate)
        {
            var all = Load();
            all[ck3Id] = new Dictionary<string, string>(loadout, StringComparer.Ordinal);
            var raw = all.ToDictionary(p => p.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), p => p.Value);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
