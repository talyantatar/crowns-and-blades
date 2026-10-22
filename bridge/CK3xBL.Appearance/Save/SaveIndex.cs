// What one gamestate scan knows: compact facts for every character, the title/house/dynasty ->
// coat-of-arms chain, culture gfx, and an on-demand store of the CoA database. Kept in memory until the next save so
// a battle with characters the bridge has never seen can still be resolved without rescanning.
using System.Collections.Concurrent;
using CK3xBL.Appearance.Coa;
using CK3xBL.Appearance.Pdx;

namespace CK3xBL.Appearance.Save;

internal sealed class CharacterRecord
{
    public long Id;
    public string? FirstName;
    public string? Birth;
    /// <summary>Null = unknown: binary gamestates are scanned for DNA only and do not tell the sex.</summary>
    public bool? Female;
    public string? Ethnicity;
    public long Culture = -1;
    public long House = -1;
    public long PrimaryTitle = -1;
    public string? Dna;
}

internal sealed class TitleRecord
{
    public long Id;
    public string? Key;
    public long CoaId = -1;
}

internal sealed class HouseRecord
{
    public long Id;
    public long Dynasty = -1;
    public long CoaId = -1;
    public string? Name;
}

internal sealed class DynastyRecord
{
    public long Id;
    public long CoaId = -1;
    public string? Key;
}

internal sealed class CultureRecord
{
    public long Id;
    public string? Key;
    public string? Heritage;
    public string? ClothingGfx;
    public string? UnitGfx;
    public string? CoaGfx;
    public readonly List<string> Ethnicities = new();
}

internal sealed class SaveIndex
{
    public CK3Date? Date;
    /// <summary>playthrough_id: identifies the campaign (character ids are reused between campaigns).</summary>
    public string? PlaythroughId;
    public string[] TraitsLookup = Array.Empty<string>();
    public readonly Dictionary<long, CharacterRecord> Characters = new();
    public readonly Dictionary<long, List<long>> Parents = new();          // child -> parent ids
    public readonly Dictionary<long, TitleRecord> Titles = new();
    public readonly Dictionary<string, long> TitleIdByKey = new(StringComparer.Ordinal);
    public readonly Dictionary<long, HouseRecord> Houses = new();
    public readonly Dictionary<long, DynastyRecord> Dynasties = new();
    public readonly Dictionary<long, CultureRecord> Cultures = new();
    public CoaStore? Coas;

    public void AddParent(long child, long parent)
    {
        if (!Parents.TryGetValue(child, out var list)) Parents[child] = list = new List<long>(2);
        if (!list.Contains(parent)) list.Add(parent);
    }

    public CharacterRecord? Character(long id) => Characters.TryGetValue(id, out var c) ? c : null;

    /// <summary>CoA id of a title key (primary title CoA), or -1.</summary>
    public long TitleCoaId(string? key) =>
        key != null && TitleIdByKey.TryGetValue(key, out long tid) && Titles.TryGetValue(tid, out var t) ? t.CoaId : -1;

    /// <summary>CoA id of a house: its own, else its dynasty's; -1 when unknown.</summary>
    public long HouseCoaId(long houseId)
    {
        if (!Houses.TryGetValue(houseId, out var h)) return -1;
        if (h.CoaId >= 0) return h.CoaId;
        return h.Dynasty >= 0 && Dynasties.TryGetValue(h.Dynasty, out var d) ? d.CoaId : -1;
    }

    public string Summary() =>
        $"{Characters.Count} characters ({Characters.Values.Count(c => c.Dna != null)} with dna), {Titles.Count} titles, " +
        $"{Houses.Count} houses, {Dynasties.Count} dynasties, {Cultures.Count} cultures, {Coas?.Count ?? 0} coats of arms";
}

/// <summary>The CoA database kept as raw record bytes; records are parsed on first use.</summary>
internal sealed class CoaStore
{
    private readonly byte[] _bytes;
    private readonly Dictionary<long, (int Start, int Length)> _spans;
    private readonly Func<ReadOnlyMemory<byte>, PdxNode?> _parse;
    private readonly NamedColors _colors;
    private readonly ConcurrentDictionary<long, CoaModel?> _cache = new();

    public CoaStore(byte[] bytes, Dictionary<long, (int, int)> spans, Func<ReadOnlyMemory<byte>, PdxNode?> parse, NamedColors colors)
    {
        _bytes = bytes; _spans = spans; _parse = parse; _colors = colors;
    }

    public int Count => _spans.Count;
    public IEnumerable<long> Ids => _spans.Keys;

    public CoaModel? Get(long id, string source)
    {
        if (id < 0 || !_spans.TryGetValue(id, out var span)) return null;
        var model = _cache.GetOrAdd(id, _ =>
        {
            var node = _parse(_bytes.AsMemory(span.Start, span.Length));
            return node == null ? null : CoaParser.Parse(node, source, _colors);
        });
        if (model == null) return null;
        if (model.Source == source) return model;
        // Same CoA reached through another owner: return a copy labelled with this source.
        return new CoaModel { Pattern = model.Pattern, Colors = model.Colors, Emblems = model.Emblems, Subs = model.Subs, Source = source };
    }
}
