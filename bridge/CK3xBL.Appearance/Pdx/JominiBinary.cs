// Reader for the jomini binary format used by non-debug CK3 saves (header types 01/03): a stream of little-endian u16 tokens.
// Field names are 16-bit ids from a table that ships only inside the game, so keys come out as "#<hex id>" unless they
// are written as strings (gene names, numeric ids). Callers identify fields by position or structure (see MetaReader,
// CoaTokenMap), never by a hard-coded name table.
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace CK3xBL.Appearance.Pdx;

internal enum BinTok : byte { End, Equals, Open, Close, Scalar, Key }

/// <summary>Forward-only tokenizer over a jomini binary buffer.</summary>
internal ref struct JominiBinaryReader
{
    public const ushort EqualsId = 0x0001, OpenId = 0x0003, CloseId = 0x0004;
    public const ushort I32 = 0x000c, F32 = 0x000d, Bool = 0x000e, Str = 0x000f, U32 = 0x0014, UStr = 0x0017;
    public const ushort F64 = 0x0167, Rgb = 0x0243, U64 = 0x029c, I64 = 0x0317;

    private readonly ReadOnlySpan<byte> _b;
    public int Position;

    public JominiBinaryReader(ReadOnlySpan<byte> buffer) { _b = buffer; Position = 0; }

    public bool AtEnd => Position + 2 > _b.Length;

    public ushort PeekId() => AtEnd ? (ushort)0 : BinaryPrimitives.ReadUInt16LittleEndian(_b.Slice(Position));

    /// <summary>Reads one token. For scalars, 'id' is the type id and the payload is skipped; use the Last* fields.</summary>
    public ushort Read()
    {
        ushort id = BinaryPrimitives.ReadUInt16LittleEndian(_b.Slice(Position));
        Position += 2;
        PayloadStart = Position;
        switch (id)
        {
            case I32: case U32: case F32: Position += 4; break;
            case Bool: Position += 1; break;
            case F64: case U64: case I64: Position += 8; break;
            case Str: case UStr:
                int n = BinaryPrimitives.ReadUInt16LittleEndian(_b.Slice(Position));
                PayloadStart = Position + 2;
                Position += 2 + n;
                break;
        }
        if (Position > _b.Length) throw new EndOfStreamException("truncated jomini binary token");
        return id;
    }

    public int PayloadStart;

    public static bool IsScalar(ushort id) =>
        id is I32 or U32 or F32 or Bool or Str or UStr or F64 or U64 or I64;

    public static bool IsString(ushort id) => id is Str or UStr;

    public readonly ReadOnlySpan<byte> StringBytes(ushort id)
    {
        int n = BinaryPrimitives.ReadUInt16LittleEndian(_b.Slice(PayloadStart - 2));
        return _b.Slice(PayloadStart, n);
    }

    public readonly long Int(ushort id) => id switch
    {
        I32 => BinaryPrimitives.ReadInt32LittleEndian(_b.Slice(PayloadStart)),
        U32 => BinaryPrimitives.ReadUInt32LittleEndian(_b.Slice(PayloadStart)),
        U64 => (long)BinaryPrimitives.ReadUInt64LittleEndian(_b.Slice(PayloadStart)),
        I64 => BinaryPrimitives.ReadInt64LittleEndian(_b.Slice(PayloadStart)),
        Bool => _b[PayloadStart],
        F32 => (long)BinaryPrimitives.ReadSingleLittleEndian(_b.Slice(PayloadStart)),
        _ => 0,
    };

    public readonly double Float(ushort id) => id switch
    {
        F32 => BinaryPrimitives.ReadSingleLittleEndian(_b.Slice(PayloadStart)),
        // CK3 writes 64-bit "fixed" numbers as integers scaled by 100000.
        F64 => BinaryPrimitives.ReadInt64LittleEndian(_b.Slice(PayloadStart)) / 100000.0,
        _ => Int(id),
    };

    public readonly string Text(ushort id) => id switch
    {
        Str or UStr => Encoding.UTF8.GetString(StringBytes(id)),
        Bool => _b[PayloadStart] != 0 ? "yes" : "no",
        F32 or F64 => FloatText(Float(id)),
        I32 or U32 or U64 or I64 => Int(id).ToString(CultureInfo.InvariantCulture),
        _ => TokenName(id),
    };

    // Floats always carry a decimal point so callers can tell them from integers after conversion to text.
    private static string FloatText(double d)
    {
        string t = d.ToString("R", CultureInfo.InvariantCulture);
        return t.Contains('.') || t.Contains('E') || t.Contains('N') || t.Contains('I') ? t : t + ".0";
    }

    public static string TokenName(ushort id) => "#" + id.ToString("x", CultureInfo.InvariantCulture);
}

internal static class JominiBinary
{
    /// <summary>Converts a (small) binary buffer, e.g. the save metadata, to a PdxNode tree.</summary>
    public static PdxNode Parse(ReadOnlySpan<byte> buffer)
    {
        var r = new JominiBinaryReader(buffer);
        var root = new PdxNode();
        try { ParseBody(ref r, root, 0); }
        catch (EndOfStreamException) { /* truncated input: keep what was read */ }
        catch (ArgumentOutOfRangeException) { }
        return root;
    }

    private static void ParseBody(ref JominiBinaryReader r, PdxNode node, int depth)
    {
        if (depth > 200) throw new EndOfStreamException("nesting too deep");
        while (!r.AtEnd)
        {
            ushort id = r.Read();
            if (id == JominiBinaryReader.CloseId) return;
            if (id == JominiBinaryReader.EqualsId) continue;
            if (id == JominiBinaryReader.OpenId)
            {
                var anon = new PdxNode();
                ParseBody(ref r, anon, depth + 1);
                if (!r.AtEnd && r.PeekId() == JominiBinaryReader.EqualsId)
                {
                    // "{ ... } = value" does not occur in saves; skip the value to stay in sync.
                    r.Read();
                    SkipValue(ref r, depth);
                    continue;
                }
                node.List.Add(new PdxValue(anon));
                continue;
            }
            string text = r.Text(id);
            if (!r.AtEnd && r.PeekId() == JominiBinaryReader.EqualsId)
            {
                r.Read();
                var value = ParseValue(ref r, depth);
                if (value != null) node.Items.Add(new PdxItem(text, "=", value));
            }
            else if (id == JominiBinaryReader.Rgb && !r.AtEnd && r.PeekId() == JominiBinaryReader.OpenId)
            {
                r.Read();
                var block = new PdxNode { Function = "rgb" };
                ParseBody(ref r, block, depth + 1);
                node.List.Add(new PdxValue(block));
            }
            else
            {
                node.List.Add(new PdxValue(text));
            }
        }
    }

    private static PdxValue? ParseValue(ref JominiBinaryReader r, int depth)
    {
        if (r.AtEnd) return null;
        ushort id = r.Read();
        if (id == JominiBinaryReader.OpenId)
        {
            var block = new PdxNode();
            ParseBody(ref r, block, depth + 1);
            return new PdxValue(block);
        }
        if (id == JominiBinaryReader.CloseId) return null;
        if (!JominiBinaryReader.IsScalar(id) && !r.AtEnd && r.PeekId() == JominiBinaryReader.OpenId)
        {
            // rgb { r g b } and similar token-prefixed blocks
            r.Read();
            var block = new PdxNode { Function = id == JominiBinaryReader.Rgb ? "rgb" : JominiBinaryReader.TokenName(id) };
            ParseBody(ref r, block, depth + 1);
            return new PdxValue(block);
        }
        return new PdxValue(r.Text(id));
    }

    private static void SkipValue(ref JominiBinaryReader r, int depth)
    {
        if (r.AtEnd) return;
        ushort id = r.Read();
        if (id == JominiBinaryReader.OpenId) ParseBody(ref r, new PdxNode(), depth + 1);
    }
}
