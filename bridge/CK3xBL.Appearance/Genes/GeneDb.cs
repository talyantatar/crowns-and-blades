// Gene database read at runtime from the CK3 install (common/genes/*.txt, playset-merged).
// The stored dna= string holds the inheritable genes only: color_genes, then morph_genes, then the top-level
// accessory_genes, each in file order (files sorted by name). special_genes (hairstyles, beards, clothes, ...) are
// resolved by the portrait system and appear only in portrait blocks.
using System.Text.RegularExpressions;
using CK3xBL.Appearance.Game;
using CK3xBL.Appearance.Pdx;

namespace CK3xBL.Appearance.Genes;

internal enum GeneKind { Color, Morph, Accessory, SpecialMorph, SpecialAccessory }

internal sealed class GeneTemplate
{
    public required string Name;
    public int Index;
    /// <summary>Accessory genes only: male/female/boy/girl -> weighted accessory list or alias ("boy = male").</summary>
    public readonly Dictionary<string, AccessoryChoice> Genders = new(StringComparer.Ordinal);
}

internal sealed class AccessoryChoice
{
    public string? Alias;
    public readonly List<(double Weight, string Name)> Items = new();
}

internal sealed class GeneDef
{
    public required string Name;
    public GeneKind Kind;
    public readonly Dictionary<int, GeneTemplate> ByIndex = new();
    public readonly Dictionary<string, GeneTemplate> ByName = new(StringComparer.Ordinal);

    public string TemplateName(int index) => ByIndex.TryGetValue(index, out var t) ? t.Name : "?" + index;
}

internal sealed partial class GeneDb
{
    public readonly List<GeneDef> Inheritable = new();
    public readonly Dictionary<string, GeneDef> ByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _comments = new(StringComparer.Ordinal);

    /// <summary>NPortrait.GENE_DATABASE_VERSION this build was checked against (00_defines.txt:1442).</summary>
    public const int ExpectedPortraitsVersion = 5;

    public static GeneDb Load(GameFiles files)
    {
        var db = new GeneDb();
        foreach (var file in files.ListFiles("common/genes"))
        {
            string text = GameFiles.ReadShared(file);
            db.ReadComments(text);
            var root = PdxTextParser.Parse(text);
            foreach (var it in root.Items)
            {
                var block = it.Value.Block;
                if (block == null) continue;
                switch (it.Key)
                {
                    case "color_genes": db.AddGenes(block, GeneKind.Color); break;
                    case "morph_genes": db.AddGenes(block, GeneKind.Morph); break;
                    case "accessory_genes": db.AddGenes(block, GeneKind.Accessory); break;
                    case "special_genes":
                        foreach (var sub in block.Items)
                        {
                            if (sub.Value.Block == null) continue;
                            db.AddGenes(sub.Value.Block, sub.Key == "accessory_genes" ? GeneKind.SpecialAccessory : GeneKind.SpecialMorph);
                        }
                        break;
                }
            }
        }
        return db;
    }

    private void AddGenes(PdxNode block, GeneKind kind)
    {
        foreach (var g in block.Items)
        {
            if (g.Value.Block == null) continue;
            var def = new GeneDef { Name = g.Key, Kind = kind };
            bool accessory = kind is GeneKind.Accessory or GeneKind.SpecialAccessory;
            foreach (var t in g.Value.Block.Items)
            {
                var tb = t.Value.Block;
                if (tb == null || tb.Long("index") is not long index) continue;
                var tmpl = new GeneTemplate { Name = t.Key, Index = (int)index };
                if (accessory)
                {
                    foreach (var gender in new[] { "male", "female", "boy", "girl" })
                    {
                        var gv = tb.Get(gender);
                        if (gv == null) continue;
                        var choice = new AccessoryChoice();
                        if (gv.Block == null) choice.Alias = gv.Text;
                        else
                            foreach (var w in gv.Block.Items)
                                if (double.TryParse(w.Key, System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out var weight) && w.Value.Text != null)
                                    choice.Items.Add((weight, w.Value.Text));
                        tmpl.Genders[gender] = choice;
                    }
                }
                def.ByIndex[tmpl.Index] = tmpl;
                def.ByName[tmpl.Name] = tmpl;
            }
            ByName[def.Name] = def;
            if (kind is GeneKind.Color or GeneKind.Morph or GeneKind.Accessory) Inheritable.Add(def);
        }
    }

    [GeneratedRegex(@"=\s*([A-Za-z0-9_]+)\s*#\s*([^\r\n]+)")]
    private static partial Regex CommentRegex();

    private void ReadComments(string text)
    {
        foreach (Match m in CommentRegex().Matches(text))
            _comments.TryAdd(m.Groups[1].Value, m.Groups[2].Value.Trim());
    }

    /// <summary>The human comment next to an accessory ("male_hair_indian_01 # Short Pompadour"), if any.</summary>
    public string? AccessoryComment(string? accessory) =>
        accessory != null && _comments.TryGetValue(accessory, out var c) ? c : null;

    /// <summary>
    /// Resolves an accessory gene (hairstyles, beards, ...) to the accessory the portrait shows. CK3 picks from the
    /// template's weighted list for the gender; the pick reproduces the verified cases at value/256 * total weight.
    /// </summary>
    public string? PickAccessory(string gene, string template, int value, string gender)
    {
        var items = AccessoryList(gene, template, gender);
        if (items == null) return null;
        double total = items.Sum(i => i.Weight);
        double target = value / 256.0 * total, acc = 0;
        foreach (var (w, name) in items)
        {
            acc += w;
            if (target < acc) return name;
        }
        return items[^1].Name;
    }

    /// <summary>The weighted accessory list of a template for a gender ("boy = male" aliases resolved), or null.</summary>
    public IReadOnlyList<(double Weight, string Name)>? AccessoryList(string gene, string template, string gender)
    {
        if (!ByName.TryGetValue(gene, out var def) || !def.ByName.TryGetValue(template, out var t)) return null;
        if (!t.Genders.TryGetValue(gender, out var choice)) return null;
        for (int guard = 0; choice.Alias != null && guard < 4; guard++)
        {
            if (!t.Genders.TryGetValue(choice.Alias, out var next)) return null;
            choice = next;
        }
        return choice.Items.Count == 0 ? null : choice.Items;
    }

    public bool HasTemplate(string gene, string template) =>
        ByName.TryGetValue(gene, out var def) && def.ByName.ContainsKey(template);
}
