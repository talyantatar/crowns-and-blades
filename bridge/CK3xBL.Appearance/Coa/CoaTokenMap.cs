// Field names of coats of arms in binary saves are 16-bit tokens without a public table. This map starts from the
// ids observed in CK3 1.20 (autosave_exit.ck3: #53c pattern, #539/#53a/#53b color1..3, #53f colored_emblem,
// #53e textured_emblem, #19f texture, #542 instance, #4c position, #5d scale, #156 rotation, #543 depth, #53d mask)
// and re-derives each id from the structure of the CoA blocks it is given (pattern = the key holding "pattern_*.dds",
// colours in written order, position before scale, ...), so a patch that renumbers tokens is followed automatically
// as soon as enough samples exist. Renamed blocks are then parsed by the ordinary text CoaParser.
using CK3xBL.Appearance.Pdx;

namespace CK3xBL.Appearance.Coa;

internal sealed class CoaTokenMap
{
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal)
    {
        ["#53c"] = "pattern", ["#539"] = "color1", ["#53a"] = "color2", ["#53b"] = "color3",
        ["#53f"] = "colored_emblem", ["#53e"] = "textured_emblem", ["#19f"] = "texture",
        ["#542"] = "instance", ["#4c"] = "position", ["#5d"] = "scale", ["#156"] = "rotation",
        ["#543"] = "depth", ["#53d"] = "mask",
    };

    public string? NameOf(string token) => _names.TryGetValue(token, out var n) ? n : null;

    /// <summary>True for a block that is a coat of arms (has a "pattern_*.dds" string at its top level).</summary>
    public static bool LooksLikeCoa(PdxNode b) => b.Items.Any(i => IsPattern(i.Value.Text));

    private static bool IsPattern(string? t) =>
        t != null && t.StartsWith("pattern", StringComparison.OrdinalIgnoreCase) && t.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);

    private static bool IsDds(string? t) => t != null && t.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);

    /// <summary>Updates the map from sample CoA blocks; ids with too little evidence keep their previous value.</summary>
    public void Calibrate(IEnumerable<PdxNode> coaBlocks, NamedColors colors)
    {
        var votes = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        void Vote(string token, string name)
        {
            if (!votes.TryGetValue(name, out var v)) votes[name] = v = new Dictionary<string, int>(StringComparer.Ordinal);
            v[token] = v.GetValueOrDefault(token) + 1;
        }
        bool IsColor(PdxValue v) =>
            v.Text != null ? colors.ResolveName(v.Text) != null
                           : v.Block is { } b && (b.Function == "rgb" || (b.Items.Count == 0 && b.List.Count == 3 && b.Numbers().Any(n => n > 3)));

        foreach (var coa in coaBlocks)
        {
            int colorIndex = 0;
            foreach (var it in coa.Items)
            {
                if (IsPattern(it.Value.Text)) { Vote(it.Key, "pattern"); continue; }
                if (it.Value.Block is PdxNode eb && eb.Items.Any(x => IsDds(x.Value.Text)))
                {
                    var tex = eb.Items.First(x => IsDds(x.Value.Text));
                    Vote(tex.Key, "texture");
                    bool colored = tex.Value.Text!.StartsWith("ce_", StringComparison.OrdinalIgnoreCase);
                    Vote(it.Key, colored ? "colored_emblem" : "textured_emblem");
                    CalibrateEmblem(eb, Vote, IsColor);
                    continue;
                }
                if (colorIndex < 3 && IsColor(it.Value)) Vote(it.Key, "color" + ++colorIndex);
            }
        }

        foreach (var (name, tokens) in votes)
        {
            var best = tokens.OrderByDescending(kv => kv.Value).First();
            int total = tokens.Values.Sum();
            if (best.Value * 2 <= total) continue; // no clear majority: keep the previous id
            foreach (var stale in _names.Where(kv => kv.Value == name && kv.Key != best.Key).Select(kv => kv.Key).ToList())
                _names.Remove(stale);
            _names[best.Key] = name;
        }
    }

    private static void CalibrateEmblem(PdxNode emblem, Action<string, string> vote, Func<PdxValue, bool> isColor)
    {
        int colorIndex = 0;
        foreach (var it in emblem.Items)
        {
            if (IsDds(it.Value.Text)) continue;
            var b = it.Value.Block;
            if (b != null && b.Items.Any(x => x.Value.Block is { } xb && xb.List.Count == 2 && xb.Numbers().Count == 2))
            {
                vote(it.Key, "instance");
                var pairs = b.Items.Where(x => x.Value.Block is { } xb && xb.List.Count == 2 && xb.Numbers().Count == 2).ToList();
                if (pairs.Count >= 2) { vote(pairs[0].Key, "position"); vote(pairs[1].Key, "scale"); }
                continue;
            }
            if (b != null && b.Function == null && b.Items.Count == 0 && b.List.Count is >= 1 and <= 3 &&
                b.Numbers().All(n => n is >= 0 and <= 3) && b.Numbers().Any(n => n > 0))
            {
                vote(it.Key, "mask");
                continue;
            }
            if (colorIndex < 3 && isColor(it.Value)) vote(it.Key, "color" + ++colorIndex);
        }
    }

    /// <summary>Copy of a binary CoA block with its token keys renamed to text names (unknown tokens dropped).</summary>
    public PdxNode Rename(PdxNode b)
    {
        var r = new PdxNode { Function = b.Function };
        foreach (var it in b.Items)
        {
            string key = it.Key.StartsWith('#') ? NameOf(it.Key) ?? it.Key : it.Key;
            var v = it.Value.Block != null ? new PdxValue(Rename(it.Value.Block)) : it.Value;
            r.Items.Add(new PdxItem(key, it.Op, v));
        }
        foreach (var v in b.List) r.List.Add(v.Block != null ? new PdxValue(Rename(v.Block)) : v);
        // A binary mask is positional ({ 1 0 0 } = channel 1, { 0 2 0 } = channel 2): keep only the set channels.
        var mask = r.Get("mask")?.Block;
        if (mask != null && mask.List.Count == 3)
        {
            var kept = mask.List.Where(v => v.AsLong() is > 0).ToList();
            mask.List.Clear();
            mask.List.AddRange(kept);
        }
        return r;
    }
}
