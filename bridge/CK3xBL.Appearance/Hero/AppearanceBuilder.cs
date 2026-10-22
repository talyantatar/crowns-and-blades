// CK3 genes + live facts -> Bannerlord AppearanceSpec (PROTOCOL §7.1):
//   weight  0.5 + current_weight/200            build  clamp(0.35 + prowess/40 + 0.08*physique, 0, 1)
//   height  gene_height v/255 (dwarf 0.05, giant 0.95)
//   skin/hairColor/eyeColor  palette RGB (hair greyed by age) -> nearest Bannerlord gradient point -> idx/(n-1)
//   hair/beard  resolved portrait accessory (meta) or a seeded pick from the culture family x hair type (derived)
//   morphs  CK3 morph genes -> signed -1..1 by Bannerlord deform-key id (bone (v-127.5)/127.5, gene_bs_* +-v/255, x dir)
// The live record wins for age, weight, prowess and traits; the save wins for genes. CK3 body genes (body type,
// body shape, bust) are never mapped: Bannerlord takes weight and build from the live stats instead.
using System.Globalization;
using CK3xBL.Appearance.Data;
using CK3xBL.Appearance.Genes;
using CK3xBL.Protocol;

namespace CK3xBL.Appearance.Hero;

internal sealed class LookInput
{
    public long Ck3Id;
    public bool Female;
    public int ApparentAge;                    // years, portrait age
    public int Weight;                         // CK3 current_weight, -100..100
    public int Prowess;
    public IReadOnlyCollection<string> Traits = Array.Empty<string>();
    public string? ClothingGfx;
    public required GeneSet Genes;
    public bool IsPortrait;                    // genes carry the resolved special genes (hairstyles, beards, balding)
    public string Source = "stock";
}

internal sealed class AppearanceBuilder
{
    private static readonly HashSet<string> BodyGenes = new(StringComparer.Ordinal)
        { "gene_bs_body_type", "gene_bs_body_shape", "gene_bs_bust" };

    private readonly GeneDb? _genes;
    private readonly PortraitPalettes? _palettes;
    private readonly BlSkin[] _skins;
    private readonly List<MorphMapEntry> _morphs;
    private readonly HairStyleTable _hair;

    public AppearanceBuilder(GeneDb? genes, PortraitPalettes? palettes, BlSkin[] skins, List<MorphMapEntry> morphs, HairStyleTable hair)
    {
        _genes = genes; _palettes = palettes; _skins = skins; _morphs = morphs; _hair = hair;
    }

    /// <summary>Builds the spec; 'notes' receives a one-line human summary for the log.</summary>
    public AppearanceSpec Build(LookInput c, out string notes)
    {
        var skin = _skins[c.Female ? 1 : 0];
        var spec = new AppearanceSpec { Source = c.Source };
        var traits = c.Traits;
        var n = new List<string>();

        spec.Weight = Round(Math.Clamp(0.5 + c.Weight / 200.0, 0, 1));
        int physique = Level(traits, "physique_good_") - Level(traits, "physique_bad_")
                       + (Has(traits, "strong") ? 1 : 0) - (Has(traits, "weak") || Has(traits, "spindly") ? 1 : 0)
                       + (Has(traits, "giant") ? 2 : 0);
        spec.Build = Round(Math.Clamp(0.35 + c.Prowess / 40.0 + 0.08 * physique, 0, 1));

        double height = c.Genes.Morph("gene_height") is MorphGene h ? h.Value / 255.0 : 0.5;
        if (c.Genes.Morph("gene_height")?.Template == "dwarf_height" || Has(traits, "dwarf")) height = 0.05;
        if (c.Genes.Morph("gene_height")?.Template == "giant_height" || Has(traits, "giant")) height = 0.95;
        if (Has(traits, "spindly")) height = Math.Min(1, height + 0.1);
        spec.Height = Round(height);

        bool albino = Has(traits, "albino");
        if (c.Genes.Color("skin_color") is ColorGene sc && _palettes?.Skin(sc.X, sc.Y) is Rgb skinRgb)
        {
            if (albino) skinRgb = new Rgb(255, 255, 255);
            spec.Skin = Offset(skin.SkinGradient, skinRgb, out _);
            n.Add("skin " + skinRgb.Hex);
        }
        if (c.Genes.Color("hair_color") is ColorGene hc && _palettes?.Hair(hc.X, hc.Y) is Rgb hairRgb)
        {
            if (albino) hairRgb = new Rgb(247, 236, 214);
            double grey = Greying(c.ApparentAge);
            var (hh, ss, vv) = hairRgb.ToHsv();
            var greyed = Rgb.FromHsv(hh, Math.Max(0, ss * (1 - grey)), Math.Min(1, vv + 0.3 * grey));
            spec.HairColor = Offset(skin.HairGradient, greyed, out _);
            n.Add($"hair {hairRgb.Hex}->{greyed.Hex} grey {grey:0.00}");
        }
        if (c.Genes.Color("eye_color") is ColorGene ec && _palettes?.Eye(ec.X, ec.Y) is Rgb eyeRgb)
        {
            spec.EyeColor = Offset(skin.EyeGradient, eyeRgb, out _);
            n.Add("eye " + eyeRgb.Hex);
        }

        string gender = c.ApparentAge < 18 ? (c.Female ? "girl" : "boy") : (c.Female ? "female" : "male");
        var rnd = new Mulberry32(unchecked((uint)c.Ck3Id ^ 0x48414952u));   // "HAIR": stable per character
        spec.Hair = BuildHair(c, gender, skin, rnd, n);
        spec.Beard = BuildBeard(c, gender, skin, rnd, n);

        foreach (var m in _morphs)
        {
            if (BodyGenes.Contains(m.Ck3Gene) || c.Genes.Morph(m.Ck3Gene) is not MorphGene g) continue;
            double s = m.Ck3Gene.StartsWith("gene_bs_", StringComparison.Ordinal)
                ? (g.Template.EndsWith("_neg", StringComparison.Ordinal) ? -1 : 1) * g.Value / 255.0
                : (g.Value - 127.5) / 127.5;
            spec.Morphs[m.BlKey] = (float)Math.Round(Math.Clamp(s * m.Dir, -1, 1), 3);
        }
        n.Add($"{spec.Morphs.Count} morphs");
        notes = string.Join(", ", n);
        return spec;
    }

    private StyleSpec BuildHair(LookInput c, string gender, BlSkin skin, Mulberry32 rnd, List<string> n)
    {
        string? accessory = null, template = null;
        bool bald = false;
        if (c.IsPortrait && c.Genes.Morph("hairstyles") is MorphGene hs)
        {
            template = hs.Template;
            accessory = _genes?.PickAccessory("hairstyles", hs.Template, hs.Value, gender);
            bald = hs.Template.StartsWith("no_", StringComparison.Ordinal) || accessory is "bald" or "empty";
        }
        if (accessory == null && !bald)
        {
            string hairType = HairType(c.Genes);
            string family = _hair.Family(c.ClothingGfx);
            template = FirstTemplate("hairstyles", $"{family}_hairstyles_{hairType}", $"{family}_hairstyles_straight", "western_hairstyles_straight");
            accessory = template == null ? null : WeightedPick("hairstyles", template, gender, rnd);
        }

        string stage = BaldingStage(c);
        string[] tags;
        if (bald) tags = _hair.HairTags("bald", null, c.Female);
        else
        {
            tags = _hair.HairTags(accessory ?? "", _genes?.AccessoryComment(accessory), c.Female);
            if (!c.Female && _hair.BaldingTags(stage) is { Length: > 0 } balding) tags = balding;
        }
        var style = new StyleSpec { Bald = bald || tags.Contains("Bald") };
        style.Tags.AddRange(tags);
        style.Index = FirstIndex(tags, skin.HairIndex) ?? (style.Bald ? 0 : null);
        n.Add($"hair {(c.IsPortrait ? "portrait" : "derived")} {template}/{accessory ?? (bald ? "bald" : "-")} {stage} -> {string.Join("|", tags)}#{style.Index}");
        return style;
    }

    private StyleSpec BuildBeard(LookInput c, string gender, BlSkin skin, Mulberry32 rnd, List<string> n)
    {
        var none = new StyleSpec { Index = 0 };
        // CK3 removes the beard only for beardless_eunuch (gfx/portraits/portrait_modifiers/99_special.txt
        // special_beardless_eunuch); eunuch_1 (castrated as an adult) keeps it. Takes effect once the CK3 mod lists
        // beardless_eunuch among its CX|trait lines (requested in PROTOCOL §7.1).
        bool beardless = c.Traits.Contains("beardless_eunuch");
        if (c.Female || c.ApparentAge < _hair.BeardMinAge || beardless)
        {
            none.Tags.Add("Cleanshaven");
            n.Add("beard none");
            return none;
        }
        string? accessory = null, template = null;
        if (c.IsPortrait && c.Genes.Morph("beards") is MorphGene bg)
        {
            template = bg.Template;
            accessory = _genes?.PickAccessory("beards", bg.Template, bg.Value, gender);
            if (bg.Template.StartsWith("no_", StringComparison.Ordinal) || accessory is "empty") accessory = "no_beard";
        }
        else if (rnd.Next() < _hair.BeardChance)
        {
            string family = _hair.BeardFamily(_hair.Family(c.ClothingGfx));
            string type = HairType(c.Genes) is "curly" or "afro" ? "curly" : "straight";
            template = FirstTemplate("beards", $"{family}_beards_{type}", $"{family}_beards_afro", $"{family}_beards_straight", "western_beards_straight");
            accessory = template == null ? null : WeightedPick("beards", template, gender, rnd);
        }
        else accessory = "no_beard";

        var tags = _hair.BeardTags(accessory ?? "no_beard", _genes?.AccessoryComment(accessory));
        var style = new StyleSpec();
        style.Tags.AddRange(tags);
        style.Index = FirstIndex(tags, skin.BeardIndex) ?? 0;
        n.Add($"beard {template}/{accessory} -> {string.Join("|", tags)}#{style.Index}");
        return style;
    }

    /// <summary>Portrait: gene_balding_hair_effect. Derived: gene_baldness strength vs age ("0.3 starts balding at 30").</summary>
    private static string BaldingStage(LookInput c)
    {
        if (c.Female) return "no_baldness";
        if (c.Genes.Morph("gene_balding_hair_effect") is MorphGene be) return be.Template;
        if (c.Genes.Morph("gene_baldness") is MorphGene b && b.Template == "male_pattern_baldness")
        {
            double start = b.Value / 255.0, age = c.ApparentAge / 100.0;
            if (age >= start + 0.15) return "baldness_stage_2";
            if (age >= start) return "baldness_stage_1";
        }
        return "no_baldness";
    }

    private static string HairType(GeneSet g)
    {
        string t = g.Morph("gene_hair_type")?.Template ?? "hair_straight";
        t = t.StartsWith("hair_", StringComparison.Ordinal) ? t[5..] : t;
        return t.StartsWith("straight", StringComparison.Ordinal) ? "straight" : t;
    }

    private string? FirstTemplate(string gene, params string[] candidates) =>
        candidates.FirstOrDefault(t => _genes?.HasTemplate(gene, t) == true);

    private string? WeightedPick(string gene, string template, string gender, Mulberry32 rnd)
    {
        var list = _genes?.AccessoryList(gene, template, gender);
        if (list == null) return null;
        double r = rnd.Next() * list.Sum(i => i.Weight);
        foreach (var (w, name) in list)
        {
            if (r < w) return name;
            r -= w;
        }
        return list[^1].Name;
    }

    private static int? FirstIndex(string[] tags, Func<string, int?> lookup)
    {
        foreach (var t in tags)
            if (lookup(t) is int i) return i;
        return null;
    }

    /// <summary>gene_age hair_hsv_shift_curve: no grey up to portrait age 35, fully grey at 70 (01_genes_morph.txt:9748).</summary>
    public static double Greying(int age) => Math.Clamp((age / 100.0 - 0.35) / 0.35, 0, 1);

    /// <summary>Gradient offset of the nearest point: idx/(n-1).</summary>
    public static float Offset(List<Rgb> gradient, Rgb c, out int index)
    {
        index = 0;
        if (gradient.Count < 2) return 0.5f;
        double best = double.MaxValue;
        for (int i = 0; i < gradient.Count; i++)
        {
            double d = Rgb.Distance(gradient[i], c);
            if (d < best) { best = d; index = i; }
        }
        return Round(index / (double)(gradient.Count - 1));
    }

    private static bool Has(IReadOnlyCollection<string> traits, string t) => traits.Contains(t);

    private static int Level(IReadOnlyCollection<string> traits, string prefix)
    {
        foreach (var t in traits)
            if (t.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(t.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int l))
                return l;
        return 0;
    }

    private static float Round(double v) => (float)Math.Round(v, 3);
}
