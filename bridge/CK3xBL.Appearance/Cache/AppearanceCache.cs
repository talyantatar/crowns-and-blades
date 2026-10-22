// %LOCALAPPDATA%/ck3xbl/appearance_cache.json: what the last good save said about every character
// the bridge has met in battle (plus the player and heir), keyed by internal id, and the coats of arms of their titles
// and houses. A stale or missing save therefore never blocks a battle, and a binary (normal-mode) save can still use
// DNA and CoAs read earlier from a text save. Written atomically (temp + rename); a corrupt file is ignored.
using System.Text.Json;
using System.Text.Json.Serialization;
using CK3xBL.Appearance.Coa;

namespace CK3xBL.Appearance.Cache;

/// <summary>Everything known about one CK3 character from saves (never from the live log).</summary>
internal sealed class CharacterFacts
{
    public long Id { get; set; }
    public string? FirstName { get; set; }
    public string? Birth { get; set; }
    public bool? Female { get; set; }
    public string? Ethnicity { get; set; }
    public string? CultureKey { get; set; }
    public string? Heritage { get; set; }
    public string? ClothingGfx { get; set; }
    public List<string> CultureEthnicities { get; set; } = new();
    public long HouseId { get; set; } = -1;
    public string? PrimaryTitleKey { get; set; }

    /// <summary>meta_portrait | save_dna | null. Genes are serialised with GeneSet.Serialize.</summary>
    public string? GeneSource { get; set; }
    public string? Genes { get; set; }
    public double PortraitAge { get; set; }            // meta age (apparent age / 100) when GeneSource = meta_portrait
    public string? PortraitDate { get; set; }          // meta_date of the save the portrait came from
    public List<string> ParentGenes { get; set; } = new();

    public CoaModel? TitleCoa { get; set; }
    public CoaModel? HouseCoa { get; set; }
    public string? SeenInSave { get; set; }            // save file name the facts came from
}

internal sealed class CacheFile
{
    /// <summary>
    /// 2: sex is null when unknown. Version-1 files could hold a wrong "female: false" learned from binary saves, so they
    /// are discarded.
    /// </summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public string? PlaythroughId { get; set; }
    public string? SavePath { get; set; }
    public string? SaveDate { get; set; }
    public string? GameVersion { get; set; }
    public long PlayerId { get; set; } = -1;
    public long HeirId { get; set; } = -1;
    public List<long> TrackedIds { get; set; } = new();
    public Dictionary<string, CharacterFacts> Characters { get; set; } = new();
    public Dictionary<string, CoaModel> TitleCoas { get; set; } = new();
    public Dictionary<string, CoaModel> HouseCoas { get; set; } = new();
}

internal sealed class AppearanceCache
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly string _path;
    private readonly object _lock = new();
    private CacheFile _data = new();

    /// <summary>Upper bound on tracked characters, so the file stays small over a long campaign.</summary>
    private const int MaxTracked = 2000;

    public AppearanceCache(string path) { _path = path; }

    /// <summary>A cache that is never loaded or written (battles that must not use or change the real one).</summary>
    public static AppearanceCache InMemory() => new("");

    public string Path => _path;

    public void Load(Action<string> log)
    {
        lock (_lock)
        {
            try
            {
                if (_path.Length == 0 || !File.Exists(_path)) return;
                var d = JsonSerializer.Deserialize<CacheFile>(File.ReadAllBytes(_path), Json);
                if (d is { Version: CacheFile.CurrentVersion }) _data = d;
                else if (d != null) log($"appearance cache of format version {d.Version} ignored (current {CacheFile.CurrentVersion})");
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                log($"appearance cache ignored ({e.Message})");
                _data = new CacheFile();
            }
        }
    }

    public void Save(Action<string> log)
    {
        if (_path.Length == 0) return;
        byte[] bytes;
        lock (_lock) bytes = JsonSerializer.SerializeToUtf8Bytes(_data, Json);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"appearance cache not written: {e.Message}");
        }
    }

    public CharacterFacts? Character(long id)
    {
        lock (_lock) return _data.Characters.TryGetValue(Key(id), out var c) ? c : null;
    }

    public CoaModel? TitleCoa(string? key)
    {
        if (key == null) return null;
        lock (_lock) return _data.TitleCoas.TryGetValue(key, out var c) ? c : null;
    }

    public CoaModel? HouseCoa(long houseId)
    {
        lock (_lock) return _data.HouseCoas.TryGetValue(Key(houseId), out var c) ? c : null;
    }

    public IReadOnlyList<long> TrackedIds()
    {
        lock (_lock) return _data.TrackedIds.ToList();
    }

    /// <summary>Remembers ids seen in a battle (most recent last). Returns true when something new was added.</summary>
    public bool Track(IEnumerable<long> ids)
    {
        lock (_lock)
        {
            bool changed = false;
            foreach (long id in ids)
            {
                if (id <= 0) continue;
                int at = _data.TrackedIds.IndexOf(id);
                if (at == _data.TrackedIds.Count - 1 && at >= 0) continue;
                if (at >= 0) _data.TrackedIds.RemoveAt(at);
                _data.TrackedIds.Add(id);
                changed = true;
            }
            while (_data.TrackedIds.Count > MaxTracked)
            {
                _data.Characters.Remove(Key(_data.TrackedIds[0]));
                _data.TrackedIds.RemoveAt(0);
            }
            return changed;
        }
    }

    public void Put(CharacterFacts facts)
    {
        lock (_lock)
        {
            _data.Characters[Key(facts.Id)] = facts;
            if (facts.PrimaryTitleKey != null && facts.TitleCoa != null) _data.TitleCoas[facts.PrimaryTitleKey] = facts.TitleCoa;
            if (facts.HouseId >= 0 && facts.HouseCoa != null) _data.HouseCoas[Key(facts.HouseId)] = facts.HouseCoa;
        }
    }

    public void PutTitleCoa(string key, CoaModel coa) { lock (_lock) _data.TitleCoas[key] = coa; }

    /// <summary>Player and heir of the save the cache was last rebuilt from (-1 when none).</summary>
    public (long Player, long Heir) SavePlayers
    {
        get { lock (_lock) return (_data.PlayerId, _data.HeirId); }
    }

    /// <summary>Was the cache last rebuilt from exactly this save (path, date and, when both are known, playthrough)?</summary>
    public bool Describes(string path, string? playthroughId, string? date)
    {
        lock (_lock)
            return _data.SavePath == path && _data.SaveDate == date &&
                   (playthroughId == null || _data.PlaythroughId == null || playthroughId == _data.PlaythroughId);
    }

    /// <summary>
    /// Records the save the facts come from. Character ids are reused between campaigns, so a save of another playthrough
    /// clears everything learned from the previous one. When either playthrough id is unknown, a save whose player is
    /// neither the previous player nor the previous heir counts as another playthrough.
    /// </summary>
    public bool SetSave(string path, string? playthroughId, string? date, string? version, long player, long heir)
    {
        lock (_lock)
        {
            bool reset = playthroughId != null && _data.PlaythroughId != null
                ? playthroughId != _data.PlaythroughId
                : _data.PlayerId > 0 && player != _data.PlayerId && player != _data.HeirId;
            if (reset) _data = new CacheFile();
            _data.PlaythroughId = playthroughId ?? _data.PlaythroughId;
            _data.SavePath = path; _data.SaveDate = date; _data.GameVersion = version;
            _data.PlayerId = player; _data.HeirId = heir;
            return reset;
        }
    }

    private static string Key(long id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
