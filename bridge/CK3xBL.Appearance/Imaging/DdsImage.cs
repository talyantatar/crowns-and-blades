// Minimal DDS decoder for the textures this component reads from the user's CK3 install at runtime:
// uncompressed 24/32-bit (portrait palettes, some colored emblems), DXT1/BC1 and DXT5/BC3 (patterns, most emblems).
// Mip levels can be decoded individually so the CoA renderer can sample a level close to the on-screen size.
using System.Buffers.Binary;

namespace CK3xBL.Appearance.Imaging;

/// <summary>8-bit RGBA pixels, row 0 at the top.</summary>
internal sealed class RgbaImage
{
    public RgbaImage(int w, int h) { Width = w; Height = h; Pixels = new byte[w * h * 4]; }
    public readonly int Width, Height;
    public readonly byte[] Pixels;

    /// <summary>Bilinear sample (wrap or clamp addressing); u, v in texture space (0..1). Writes straight RGBA 0..1.</summary>
    public void SampleBilinear(double u, double v, Span<float> rgba, bool wrap = true)
    {
        double x = u * Width - 0.5, y = v * Height - 0.5;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        float fx = (float)(x - x0), fy = (float)(y - y0);
        int x1 = x0 + 1, y1 = y0 + 1;
        if (wrap) { x0 = Wrap(x0, Width); x1 = Wrap(x1, Width); y0 = Wrap(y0, Height); y1 = Wrap(y1, Height); }
        else
        {
            x0 = Math.Clamp(x0, 0, Width - 1); x1 = Math.Clamp(x1, 0, Width - 1);
            y0 = Math.Clamp(y0, 0, Height - 1); y1 = Math.Clamp(y1, 0, Height - 1);
        }
        int i00 = (y0 * Width + x0) * 4, i10 = (y0 * Width + x1) * 4, i01 = (y1 * Width + x0) * 4, i11 = (y1 * Width + x1) * 4;
        var p = Pixels;
        for (int c = 0; c < 4; c++)
        {
            float top = p[i00 + c] + (p[i10 + c] - p[i00 + c]) * fx;
            float bot = p[i01 + c] + (p[i11 + c] - p[i01 + c]) * fx;
            rgba[c] = (top + (bot - top) * fy) / 255f;
        }
    }

    private static int Wrap(int i, int n) { i %= n; return i < 0 ? i + n : i; }
}

internal sealed class DdsImage
{
    private enum Format { Bgra32, Rgb24Masked, Dxt1, Dxt5, Unsupported }

    private readonly byte[] _data;
    private readonly int _dataOffset;
    private readonly Format _format;
    private readonly uint _rMask, _gMask, _bMask, _aMask;
    private readonly int _bitCount;

    public int Width { get; }
    public int Height { get; }
    public int MipCount { get; }
    public string FormatName => _format.ToString();

    private DdsImage(byte[] data, int dataOffset, int w, int h, int mips, Format f, int bitCount, uint r, uint g, uint b, uint a)
    {
        _data = data; _dataOffset = dataOffset; Width = w; Height = h; MipCount = Math.Max(1, mips); _format = f;
        _bitCount = bitCount; _rMask = r; _gMask = g; _bMask = b; _aMask = a;
    }

    public static DdsImage Load(string path)
    {
        byte[] d;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            d = new byte[fs.Length];
            fs.ReadExactly(d);
        }
        return Parse(d) ?? throw new InvalidDataException($"unsupported DDS: {path}");
    }

    public static DdsImage? Parse(byte[] d)
    {
        if (d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S' || d[3] != ' ') return null;
        int h = (int)U32(d, 12), w = (int)U32(d, 16), mips = (int)U32(d, 28);
        uint pfFlags = U32(d, 80);
        string fourCc = System.Text.Encoding.ASCII.GetString(d, 84, 4);
        int bits = (int)U32(d, 88);
        uint r = U32(d, 92), g = U32(d, 96), b = U32(d, 100), a = U32(d, 104);
        int offset = 128;
        Format f;
        if ((pfFlags & 0x4) != 0)
        {
            if (fourCc == "DX10") { offset += 20; f = Format.Unsupported; }
            else f = fourCc switch { "DXT1" => Format.Dxt1, "DXT5" => Format.Dxt5, _ => Format.Unsupported };
        }
        else if (bits == 32) f = Format.Bgra32;
        else if (bits == 24) f = Format.Rgb24Masked;
        else f = Format.Unsupported;
        if (f == Format.Unsupported || w <= 0 || h <= 0) return null;
        return new DdsImage(d, offset, w, h, mips, f, bits, r, g, b, a);
    }

    /// <summary>Decodes one mip level (0 = full size). Levels past the stored chain fall back to the last stored one.</summary>
    public RgbaImage Decode(int level = 0)
    {
        level = Math.Clamp(level, 0, MipCount - 1);
        long offset = _dataOffset;
        for (int l = 0; l < level; l++) offset += LevelSize(Math.Max(1, Width >> l), Math.Max(1, Height >> l));
        int w = Math.Max(1, Width >> level), h = Math.Max(1, Height >> level);
        if (offset + LevelSize(w, h) > _data.Length)
        {
            if (level == 0) throw new InvalidDataException("truncated DDS");
            return Decode(level - 1);
        }
        var img = new RgbaImage(w, h);
        switch (_format)
        {
            case Format.Bgra32:
            case Format.Rgb24Masked: DecodeMasked(img, (int)offset); break;
            case Format.Dxt1: DecodeBc(img, (int)offset, alphaBlock: false); break;
            case Format.Dxt5: DecodeBc(img, (int)offset, alphaBlock: true); break;
        }
        return img;
    }

    /// <summary>Largest mip level whose size is still at least 'targetPixels' on its longer side.</summary>
    public int LevelFor(double targetPixels) => LevelFor(Width, Height, MipCount, targetPixels);

    /// <inheritdoc cref="LevelFor(double)"/>
    public static int LevelFor(int width, int height, int mipCount, double targetPixels)
    {
        int level = 0;
        while (level + 1 < mipCount && Math.Max(width >> (level + 1), height >> (level + 1)) >= targetPixels) level++;
        return level;
    }

    private long LevelSize(int w, int h) => _format switch
    {
        Format.Dxt1 => (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8,
        Format.Dxt5 => (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
        _ => (long)w * h * (_bitCount / 8),
    };

    private void DecodeMasked(RgbaImage img, int offset)
    {
        int bpp = _bitCount / 8;
        var px = img.Pixels;
        int n = img.Width * img.Height;
        for (int i = 0; i < n; i++)
        {
            int o = offset + i * bpp;
            uint v = bpp == 4 ? U32(_data, o) : (uint)(_data[o] | _data[o + 1] << 8 | _data[o + 2] << 16);
            px[i * 4] = Channel(v, _rMask);
            px[i * 4 + 1] = Channel(v, _gMask);
            px[i * 4 + 2] = Channel(v, _bMask);
            px[i * 4 + 3] = _aMask == 0 ? (byte)255 : Channel(v, _aMask);
        }
    }

    private static byte Channel(uint v, uint mask)
    {
        if (mask == 0) return 0;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        uint c = (v & mask) >> shift;
        return max == 255 ? (byte)c : (byte)(c * 255 / max);
    }

    private void DecodeBc(RgbaImage img, int offset, bool alphaBlock)
    {
        int bw = Math.Max(1, (img.Width + 3) / 4), bh = Math.Max(1, (img.Height + 3) / 4);
        int blockSize = alphaBlock ? 16 : 8;
        Span<byte> colors = stackalloc byte[16];    // 4 colours x RGBA
        Span<byte> alphas = stackalloc byte[8];
        var px = img.Pixels;
        for (int by = 0; by < bh; by++)
        for (int bx = 0; bx < bw; bx++)
        {
            int o = offset + (by * bw + bx) * blockSize;
            ulong alphaBits = 0;
            if (alphaBlock)
            {
                byte a0 = _data[o], a1 = _data[o + 1];
                alphas[0] = a0; alphas[1] = a1;
                if (a0 > a1) for (int i = 1; i < 7; i++) alphas[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
                else
                {
                    for (int i = 1; i < 5; i++) alphas[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
                    alphas[6] = 0; alphas[7] = 255;
                }
                for (int i = 0; i < 6; i++) alphaBits |= (ulong)_data[o + 2 + i] << (8 * i);
                o += 8;
            }
            ushort c0 = (ushort)(_data[o] | _data[o + 1] << 8), c1 = (ushort)(_data[o + 2] | _data[o + 3] << 8);
            Rgb565(c0, colors.Slice(0, 4));
            Rgb565(c1, colors.Slice(4, 4));
            bool fourColor = alphaBlock || c0 > c1;
            for (int c = 0; c < 3; c++)
            {
                if (fourColor)
                {
                    colors[8 + c] = (byte)((2 * colors[c] + colors[4 + c]) / 3);
                    colors[12 + c] = (byte)((colors[c] + 2 * colors[4 + c]) / 3);
                }
                else
                {
                    colors[8 + c] = (byte)((colors[c] + colors[4 + c]) / 2);
                    colors[12 + c] = 0;
                }
            }
            colors[3] = colors[7] = colors[11] = 255;
            colors[15] = fourColor ? (byte)255 : (byte)0;
            uint idx = U32(_data, o + 4);
            for (int py = 0; py < 4; py++)
            for (int pxi = 0; pxi < 4; pxi++)
            {
                int x = bx * 4 + pxi, y = by * 4 + py;
                int k = py * 4 + pxi;
                if (x >= img.Width || y >= img.Height) continue;
                int ci = (int)((idx >> (2 * k)) & 3) * 4;
                int d = (y * img.Width + x) * 4;
                px[d] = colors[ci]; px[d + 1] = colors[ci + 1]; px[d + 2] = colors[ci + 2];
                px[d + 3] = alphaBlock ? alphas[(int)((alphaBits >> (3 * k)) & 7)] : colors[ci + 3];
            }
        }
    }

    private static void Rgb565(ushort c, Span<byte> dst)
    {
        int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
        dst[0] = (byte)(r << 3 | r >> 2);
        dst[1] = (byte)(g << 2 | g >> 4);
        dst[2] = (byte)(b << 3 | b >> 2);
        dst[3] = 255;
    }

    private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
}
