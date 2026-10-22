// Numeric helpers that reproduce the reference JavaScript resolvers bit for bit, so a battle resolves to the same scene,
// sky and troop mix in the bridge as in tools/data/mapping_resolver.js and tools/data/battlefield_resolver.js.
using CK3xBL.Mapping.Data;

namespace CK3xBL.Mapping.Resolution;

public static class JsCompat
{
    /// <summary>JavaScript Math.round: halves round towards +infinity (C# Math.Round would round half to even).</summary>
    public static double Round(double x) => Math.Floor(x + 0.5);

    public static int RoundToInt(double x) => (int)Round(x);

    /// <summary>mapping_resolver.js r2: round to 2 decimals.</summary>
    public static double R2(double x) => Round(x * 100) / 100;

    /// <summary>battlefield_resolver.js hash01: FNV-1a over UTF-16 code units plus a murmur-style finaliser, mapped to [0, 1).</summary>
    public static double Hash01(string s)
    {
        uint h = 0x811c9dc5;
        foreach (char c in s)
        {
            h ^= c;
            h *= 0x01000193;
        }
        h ^= h >> 13;
        h *= 0x5bd1e995;
        h ^= h >> 15;
        return h / 4294967296.0;
    }

    /// <summary>battlefield_resolver.js pickWeighted: walks the weights in order and returns the key whose cumulative share passes u.</summary>
    public static string PickWeighted(IReadOnlyList<KeyValuePair<string, double>> weights, double u)
    {
        if (weights.Count == 0) throw new ArgumentException("no weights to pick from", nameof(weights));
        double total = 0;
        foreach (var kv in weights) total += kv.Value;
        double acc = 0;
        foreach (var kv in weights)
        {
            acc += kv.Value / total;
            if (u < acc) return kv.Key;
        }
        return weights[^1].Key;
    }

    public static string PickWeighted(OrderedWeights weights, double u) => PickWeighted(weights.Items, u);
}
