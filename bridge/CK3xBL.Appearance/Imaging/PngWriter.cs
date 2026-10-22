// Minimal PNG encoder (8-bit RGBA, no interlace) on top of System.IO.Compression.ZLibStream.
using System.Buffers.Binary;
using System.IO.Compression;

namespace CK3xBL.Appearance.Imaging;

internal static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(RgbaImage img)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, img.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], img.Height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // colour type RGBA
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(ms, "IHDR"u8, ihdr);

        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                int stride = img.Width * 4;
                Span<byte> filterNone = [0];
                for (int y = 0; y < img.Height; y++)
                {
                    z.Write(filterNone);
                    z.Write(img.Pixels, y * stride, stride);
                }
            }
            WriteChunk(ms, "IDAT"u8, raw.GetBuffer().AsSpan(0, (int)raw.Length));
        }
        WriteChunk(ms, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    /// <summary>Writes atomically (temp file + rename) so readers never see a half-written PNG.</summary>
    public static void Save(RgbaImage img, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, Encode(img));
        File.Move(tmp, path, overwrite: true);
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        s.Write(type);
        s.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
