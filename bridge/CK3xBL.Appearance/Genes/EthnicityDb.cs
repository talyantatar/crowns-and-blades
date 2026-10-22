// Stand-in genes for characters without stored DNA (PROTOCOL §7.0).
// CK3 regenerates such faces internally with an unknown seed, so this is NOT the in-game face: it is a stable,
// ethnicity-consistent stand-in seeded by the character's internal id (same face every battle). When a parent has
// stored DNA, the stand-in inherits from it the way CK3 does (one allele per parent, recessive 25 %).
using System.Globalization;
using CK3xBL.Appearance.Game;
using CK3xBL.Appearance.Pdx;

namespace CK3xBL.Appearance.Genes;

internal sealed class EthnicityDb
{
    private readonly Dictionary<string, PdxNode> _ethnicities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OrderedDictionary<string, PdxNode>> _resolved = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>NPortrait.INHERIT_RECESSIVE_GENE_CHANCE (00_defines.txt:390).</summary>
    private const double RecessiveChance = 0.25;

    public const string Fallback = "ethnicity_template";

    public int Count => _ethnicities.Count;
    public bool Has(string? key) => key != null && _ethnicities.ContainsKey(key);

    public static EthnicityDb Load(GameFiles files)
    {
        var db = new EthnicityDb();
        foreach (var file in files.ListFiles("common/ethnicities"))
        {
            var root = PdxTextParser.Parse(GameFiles.ReadShared(file));
            foreach (var it in root.Items)
                if (!it.Key.StartsWith('@') && it.Value.Block != null)
                    db._ethnicities[it.Key] = it.Value.Block;
        }
        return db;
    }

    /// <summary>Gene blocks of an ethnicity with its template chain applied (child entries override, keeping order).</summary>
    private OrderedDictionary<string, PdxNode> Resolve(string name)
    {
        lock (_lock)
        {
            if (_resolved.TryGetValue(name, out var cached)) return cached;
            var genes = new OrderedDictionary<string, PdxNode>(StringComparer.Ordinal);
            var chain = new List<PdxNode>();
            string? cur = name;
            for (int depth = 0; cur != null && depth < 8 && _ethnicities.TryGetValue(cur, out var block); depth++)
            {
                chain.Add(block);
                cur = block.Str("template");
            }
            for (int i = chain.Count - 1; i >= 0; i--)
                foreach (var it in chain[i].Items)
                    if (it.Value.Block != null && it.Key is not ("template" or "using"))
                        genes[it.Key] = it.Value.Block;
            _resolved[name] = genes;
            return genes;
        }
    }

    /// <summary>Generates the inheritable genes for an ethnicity, seeded by the internal id.</summary>
    public GeneSet Generate(string? ethnicity, uint seed, IReadOnlyCollection<string> traits)
    {
        string key = ethnicity != null && _ethnicities.ContainsKey(ethnicity) ? ethnicity : Fallback;
        var rnd = new Mulberry32(seed);
        var set = new GeneSet();
        foreach (var (gene, block) in Resolve(key))
        {
            var options = new List<(double W, PdxNode V)>();
            foreach (var it in block.Items)
            {
                if (it.Value.Block == null || !double.TryParse(it.Key, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) || w <= 0)
                    continue;
                var tr = it.Value.Block.Block("traits");
                if (tr != null && !tr.Words().Any(t => traits.Contains(t) || traits.Contains(t.Replace("beauty_", "beauty_good_"))))
                    continue;
                options.Add((w, it.Value.Block));
            }
            if (options.Count == 0) continue;
            double r = rnd.Next() * options.Sum(o => o.W);
            var pick = options[^1].V;
            foreach (var o in options)
            {
                if (r < o.W) { pick = o.V; break; }
                r -= o.W;
            }
            string? template = pick.Str("name");
            if (template == null)
            {
                var rect = pick.Numbers();
                if (rect.Count < 4) continue;
                int x = ToByte(rect[0] + (rect[2] - rect[0]) * rnd.Next());
                int y = ToByte(rect[1] + (rect[3] - rect[1]) * rnd.Next());
                set.Colors[gene] = new ColorGene(x, y, x, y);
            }
            else
            {
                var range = pick.Block("range")?.Numbers();
                double a = range is { Count: >= 2 } ? range[0] : 0.5, b = range is { Count: >= 2 } ? range[1] : 0.5;
                int v = ToByte(a + (b - a) * rnd.Next());
                set.Morphs[gene] = new MorphGene(template, v, template, v);
            }
        }
        return set;
    }

    /// <summary>
    /// Child genes from one or two parents' genes; a missing parent is replaced by the child's own ethnicity stand-in.
    /// Each gene takes one allele from each side (the recessive one with 25 % chance) and shows one of them;
    /// skin colour blends between the parents as in 00_genes_color.txt (blend_range 0.55..0.65).
    /// </summary>
    public static GeneSet Inherit(GeneSet standIn, GeneSet? father, GeneSet? mother, uint seed)
    {
        var rnd = new Mulberry32(seed ^ 0x9E3779B9u);
        var a = father ?? standIn;
        var b = mother ?? standIn;
        var child = new GeneSet();
        foreach (var (name, own) in standIn.Colors)
        {
            var ga = a.Color(name) ?? own;
            var gb = b.Color(name) ?? own;
            (int ax, int ay) = rnd.Next() < RecessiveChance ? (ga.RecX, ga.RecY) : (ga.X, ga.Y);
            (int bx, int by) = rnd.Next() < RecessiveChance ? (gb.RecX, gb.RecY) : (gb.X, gb.Y);
            if (name == "skin_color")
            {
                double t = 0.55 + 0.1 * rnd.Next();
                int x = (int)Math.Round(ax + (bx - ax) * t), y = (int)Math.Round(ay + (by - ay) * t);
                child.Colors[name] = new ColorGene(x, y, ax, ay);
            }
            else if (rnd.Next() < 0.5) child.Colors[name] = new ColorGene(ax, ay, bx, by);
            else child.Colors[name] = new ColorGene(bx, by, ax, ay);
        }
        foreach (var (name, own) in standIn.Morphs)
        {
            var ga = a.Morph(name) ?? own;
            var gb = b.Morph(name) ?? own;
            var (at, av) = rnd.Next() < RecessiveChance ? (ga.RecTemplate, ga.RecValue) : (ga.Template, ga.Value);
            var (bt, bv) = rnd.Next() < RecessiveChance ? (gb.RecTemplate, gb.RecValue) : (gb.Template, gb.Value);
            child.Morphs[name] = rnd.Next() < 0.5 ? new MorphGene(at, av, bt, bv) : new MorphGene(bt, bv, at, av);
        }
        return child;
    }

    private static int ToByte(double v) => Math.Clamp((int)Math.Round(v * 255, MidpointRounding.AwayFromZero), 0, 255);
}

/// <summary>mulberry32 PRNG (same sequence as the original Node prototype).</summary>
internal sealed class Mulberry32
{
    private uint _a;
    public Mulberry32(uint seed) { _a = seed; }

    public double Next()
    {
        unchecked
        {
            _a += 0x6D2B79F5u;
            uint t = (_a ^ (_a >> 15)) * (1u | _a);
            t = (t + ((t ^ (t >> 7)) * (61u | t))) ^ t;
            return (t ^ (t >> 14)) / 4294967296.0;
        }
    }
}
