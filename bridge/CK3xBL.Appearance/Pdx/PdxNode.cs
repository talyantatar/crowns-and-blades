// In-memory form of Paradox script (jomini text, or binary converted to the same shape).
// A block keeps its key/value items in file order (duplicate keys are legal and common: `instance = {}` twice,
// `3 = male_hair_x` repeated in accessory lists) plus its bare list values (`{ 1 2 3 }`, anonymous `{ }` blocks).
using System.Globalization;

namespace CK3xBL.Appearance.Pdx;

internal sealed class PdxNode
{
    public readonly List<PdxItem> Items = new();
    public readonly List<PdxValue> List = new();

    /// <summary>"rgb", "hsv", "hsv360", "hex" when the block was written as <c>rgb { ... }</c>; otherwise null.</summary>
    public string? Function;

    public PdxValue? Get(string key)
    {
        foreach (var it in Items)
            if (it.Key == key) return it.Value;
        return null;
    }

    public IEnumerable<PdxValue> All(string key)
    {
        foreach (var it in Items)
            if (it.Key == key) yield return it.Value;
    }

    public PdxNode? Block(string key) => Get(key)?.Block;
    public string? Str(string key) => Get(key)?.Text;

    public double? Num(string key) => Get(key)?.AsDouble();

    public long? Long(string key) => Get(key)?.AsLong();

    /// <summary>Numeric list values (<c>{ 0.5 0.35 }</c>); non-numeric entries are skipped.</summary>
    public List<double> Numbers()
    {
        var r = new List<double>(List.Count);
        foreach (var v in List)
            if (v.AsDouble() is double d) r.Add(d);
        return r;
    }

    public List<string> Words()
    {
        var r = new List<string>(List.Count);
        foreach (var v in List)
            if (v.Text != null) r.Add(v.Text);
        return r;
    }
}

internal readonly struct PdxItem
{
    public PdxItem(string key, string op, PdxValue value) { Key = key; Op = op; Value = value; }
    public readonly string Key;
    public readonly string Op;
    public readonly PdxValue Value;
}

/// <summary>Either a scalar (Text) or a block (Block).</summary>
internal sealed class PdxValue
{
    public PdxValue(string text) => Text = text;
    public PdxValue(PdxNode block) => Block = block;

    public readonly string? Text;
    public readonly PdxNode? Block;

    public double? AsDouble() =>
        Text != null && double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    public long? AsLong()
    {
        if (Text == null) return null;
        if (long.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        return double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)d : null;
    }

    public override string ToString() => Text ?? "{...}";
}
