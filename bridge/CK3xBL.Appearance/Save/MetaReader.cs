// Save metadata (first ~23 KB): the player's and heir's resolved portraits (117 genes incl. hairstyle/beard picks and
// character overrides), the primary-title and house coats of arms, game version and date. Readable for text and binary
// saves without a token table:
//   text:   meta_data = { version="1.20.0.3" portraits_version=5 meta_date=871.1.1 meta_coat_of_arms={..}
//                         meta_house_coat_of_arms={..} meta_main_portrait={ type=male id=10811 random_seed=.. age=0.71 genes={..} } meta_heir_portrait={..} }
//   binary: the same fields as tokens; portraits are found as blocks holding a gene block ("hair_color" key) and read
//           positionally (type string, id, seed, age float); CoAs are blocks with a "pattern_*.dds" value, the house
//           one after the "house_frame_*" string.
using System.Globalization;
using CK3xBL.Appearance.Coa;
using CK3xBL.Appearance.Genes;
using CK3xBL.Appearance.Pdx;

namespace CK3xBL.Appearance.Save;

internal sealed class MetaPortrait
{
    public long Id;
    public string Type = "male";              // male | female | boy | girl
    public double Age;                         // apparent age / 100
    public required GeneSet Genes;
    public bool Female => Type is "female" or "girl";
}

internal sealed class SaveMeta
{
    public string? GameVersion;
    public int? PortraitsVersion;
    public CK3Date? Date;
    public string? PlayerName;
    public string? HouseName;
    public MetaPortrait? Player;
    public MetaPortrait? Heir;
    public PdxNode? TitleCoaNode;              // text-named (binary blocks are renamed through CoaTokenMap)
    public PdxNode? HouseCoaNode;
    public PdxNode? TitleCoaRaw;               // binary saves: the token-keyed blocks, renamed again after calibration
    public PdxNode? HouseCoaRaw;
    public List<PdxNode> RawBinaryCoas = new(); // binary CoA blocks, for token calibration

    /// <summary>Binary saves: (re)derives the text-named CoA blocks with the current token map.</summary>
    public void RenameCoas(CoaTokenMap tokens)
    {
        if (TitleCoaRaw != null) TitleCoaNode = tokens.Rename(TitleCoaRaw);
        if (HouseCoaRaw != null) HouseCoaNode = tokens.Rename(HouseCoaRaw);
    }
}

/// <summary>CK3 date (year.month.day).</summary>
internal readonly record struct CK3Date(int Year, int Month, int Day)
{
    private static readonly int[] MonthDays = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };

    public double AsYears => Year + (Month - 1) / 12.0 + (Day - 1) / 365.0;

    public static CK3Date? Parse(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var p = s.Split('.');
        if (p.Length < 3 || !int.TryParse(p[0], out int y) || !int.TryParse(p[1], out int m) || !int.TryParse(p[2], out int d)) return null;
        return new CK3Date(y, m, d);
    }

    /// <summary>Binary saves store dates as hours since -5000.1.1 in 365-day years.</summary>
    public static CK3Date FromBinary(long hours)
    {
        long days = hours / 24;
        int year = (int)(days / 365) - 5000;
        int doy = (int)(days % 365);
        int month = 0;
        while (month < 11 && doy >= MonthDays[month]) doy -= MonthDays[month++];
        return new CK3Date(year, month + 1, doy + 1);
    }

    public override string ToString() => $"{Year}.{Month}.{Day}";
}

internal static class MetaReader
{
    public static SaveMeta Read(byte[] meta, bool binary, CoaTokenMap tokens, NamedColors colors)
    {
        return binary ? ReadBinary(meta, tokens, colors) : ReadText(meta);
    }

    private static SaveMeta ReadText(byte[] meta)
    {
        var root = PdxTextParser.Parse(meta);
        var md = root.Block("meta_data") ?? root;
        var m = new SaveMeta
        {
            GameVersion = md.Str("version"),
            PortraitsVersion = (int?)md.Long("portraits_version"),
            Date = CK3Date.Parse(md.Str("meta_date")),
            PlayerName = md.Str("meta_player_name"),
            HouseName = md.Str("meta_house_name"),
            TitleCoaNode = md.Block("meta_coat_of_arms"),
            HouseCoaNode = md.Block("meta_house_coat_of_arms"),
        };
        m.Player = TextPortrait(md.Block("meta_main_portrait"));
        m.Heir = TextPortrait(md.Block("meta_heir_portrait"));
        return m;
    }

    private static MetaPortrait? TextPortrait(PdxNode? p)
    {
        if (p?.Block("genes") is not PdxNode genes || p.Long("id") is not long id) return null;
        return new MetaPortrait
        {
            Id = id,
            Type = p.Str("type") ?? "male",
            Age = p.Num("age") ?? 0,
            Genes = GeneSet.FromPortraitBlock(genes),
        };
    }

    private static SaveMeta ReadBinary(byte[] meta, CoaTokenMap tokens, NamedColors colors)
    {
        var root = JominiBinary.Parse(meta);
        // The meta is one top-level block (meta_data); fall back to the root if it is written flat.
        var md = root.Items.Count == 1 && root.Items[0].Value.Block is PdxNode only ? only : root;
        var m = new SaveMeta();

        bool afterHouseFrame = false;
        string? lastString = null;
        bool versionSeen = false;
        var portraits = new List<MetaPortrait>();
        foreach (var it in md.Items)
        {
            var v = it.Value;
            if (v.Text != null)
            {
                string t = v.Text;
                if (!versionSeen && LooksLikeVersion(t)) { m.GameVersion = t; versionSeen = true; continue; }
                if (versionSeen && m.PortraitsVersion == null && long.TryParse(t, out long pv) && pv is > 0 and < 100)
                    m.PortraitsVersion = (int)pv;
                else if (m.Date == null && long.TryParse(t, out long hours) && hours is > 40_000_000 and < 200_000_000)
                    m.Date = CK3Date.FromBinary(hours);
                if (t.StartsWith("house_frame", StringComparison.Ordinal)) { afterHouseFrame = true; m.HouseName ??= lastString; }
                else if (m.PlayerName == null && versionSeen && !long.TryParse(t, out _) && !t.Contains('.')) m.PlayerName = t;
                if (!long.TryParse(t, out _) && !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    lastString = t;
                continue;
            }
            if (v.Block is not PdxNode b) continue;
            if (CoaTokenMap.LooksLikeCoa(b))
            {
                m.RawBinaryCoas.Add(b);
                if (afterHouseFrame) m.HouseCoaRaw ??= b;
                else m.TitleCoaRaw ??= b;
                continue;
            }
            if (BinaryPortrait(b) is MetaPortrait p) portraits.Add(p);
        }
        tokens.Calibrate(m.RawBinaryCoas, colors);
        m.RenameCoas(tokens);
        if (portraits.Count > 0) m.Player = portraits[0];
        if (portraits.Count > 1) m.Heir = portraits[1];
        return m;
    }

    private static bool LooksLikeVersion(string t) =>
        t.Length >= 5 && char.IsDigit(t[0]) && t.Count(c => c == '.') >= 2 && t.All(c => char.IsDigit(c) || c == '.');

    /// <summary>A portrait block: holds a gene block whose keys are gene names; type/id/seed/age are positional.</summary>
    private static MetaPortrait? BinaryPortrait(PdxNode b)
    {
        PdxNode? genes = null;
        string? type = null;
        var ints = new List<long>();
        double? age = null;
        foreach (var it in b.Items)
        {
            var v = it.Value;
            if (v.Block != null)
            {
                if (genes == null && v.Block.Get("hair_color") != null) genes = v.Block;
                continue;
            }
            string t = v.Text ?? "";
            if (type == null && t is "male" or "female" or "boy" or "girl") type = t;
            else if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) ints.Add(l);
            else if (age == null && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) age = d;
        }
        if (genes == null || ints.Count == 0) return null;
        return new MetaPortrait
        {
            Id = ints[0],
            Type = type ?? "male",
            Age = age ?? 0,
            Genes = GeneSet.FromPortraitBlock(genes),
        };
    }
}
