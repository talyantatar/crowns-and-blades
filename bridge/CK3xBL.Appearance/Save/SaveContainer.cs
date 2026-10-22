// CK3 save container.
//   bytes 0..23: "SAV" + 2-hex version + 2-hex type + 8-hex checksum + 8-hex metadata length + "\n"
//   type 00: text meta, then the text gamestate          type 01: binary meta, then the binary gamestate
//   type 02: text meta, then a zip whose "gamestate" entry is text (it repeats meta_data)
//   type 03: binary meta, then a zip whose "gamestate" entry is binary
// The zip starts at 24 + metaLen and its offsets are relative to that point, so it is opened through a sub-stream;
// ZipArchive over the whole file throws. Files are opened with FileShare.ReadWrite | Delete because CK3 rotates
// autosaves by rename while we read.
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace CK3xBL.Appearance.Save;

internal sealed class SaveHeader
{
    public string Version = "";
    public string Type = "";
    public long MetaLength;
    public long FileLength;

    public bool Zipped => Type is "02" or "03";
    /// <summary>Meta/gamestate encoding; refined by content sniffing in SaveContainer.</summary>
    public bool BinaryMeta;
    public long ZipOffset => 24 + MetaLength;

    public override string ToString() => $"SAV v{Version} type {Type} meta {MetaLength} B, file {FileLength} B";
}

internal static class SaveContainer
{
    public const int HeaderLength = 24;

    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

    /// <summary>Parses the 24-byte header. Returns null when the file is not a (complete) CK3 save.</summary>
    public static SaveHeader? TryReadHeader(Stream s)
    {
        if (s.Length < HeaderLength) return null;
        Span<byte> h = stackalloc byte[HeaderLength];
        s.Position = 0;
        if (s.ReadAtLeast(h, HeaderLength, throwOnEndOfStream: false) < HeaderLength) return null;
        if (h[0] != 'S' || h[1] != 'A' || h[2] != 'V') return null;
        string ascii = Encoding.ASCII.GetString(h);
        if (!long.TryParse(ascii.AsSpan(15, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long metaLen)) return null;
        var hdr = new SaveHeader
        {
            Version = ascii.Substring(3, 2),
            Type = ascii.Substring(5, 2),
            MetaLength = metaLen,
            FileLength = s.Length,
            BinaryMeta = ascii.Substring(5, 2) is "01" or "03",
        };
        if (metaLen <= 0 || HeaderLength + metaLen > s.Length) return null;
        return hdr;
    }

    public static byte[] ReadMeta(Stream s, SaveHeader h)
    {
        var meta = new byte[h.MetaLength];
        s.Position = HeaderLength;
        s.ReadExactly(meta);
        // Sniff: text meta starts with "meta_data" (some writers may disagree with the type code).
        h.BinaryMeta = !(meta.Length > 9 && meta.AsSpan(0, 9).SequenceEqual("meta_data"u8));
        return meta;
    }

    /// <summary>True when a zipped save is complete (its end-of-central-directory record is present).</summary>
    public static bool ZipLooksComplete(Stream s, SaveHeader h)
    {
        if (!h.Zipped) return true;
        long len = s.Length;
        int tail = (int)Math.Min(len - h.ZipOffset, 65_557);
        if (tail < 22) return false;
        var buf = new byte[tail];
        s.Position = len - tail;
        s.ReadExactly(buf);
        for (int i = tail - 22; i >= 0; i--)
            if (buf[i] == 0x50 && buf[i + 1] == 0x4b && buf[i + 2] == 5 && buf[i + 3] == 6) return true;
        return false;
    }

    /// <summary>Reads the whole gamestate (text or binary) into memory. 'binary' reports its encoding.</summary>
    public static byte[] ReadGamestate(Stream s, SaveHeader h, out bool binary)
    {
        byte[] data;
        if (h.Zipped)
        {
            using var sub = new SubStream(s, h.ZipOffset, s.Length - h.ZipOffset);
            using var zip = new ZipArchive(sub, ZipArchiveMode.Read, leaveOpen: true);
            var entry = zip.GetEntry("gamestate") ?? zip.Entries.FirstOrDefault()
                        ?? throw new InvalidDataException("save zip has no entries");
            if (entry.Length > int.MaxValue - 64) throw new InvalidDataException("gamestate too large");
            data = new byte[entry.Length];
            using var es = entry.Open();
            es.ReadExactly(data);
        }
        else
        {
            long start = HeaderLength + h.MetaLength;
            long len = s.Length - start;
            if (len > int.MaxValue - 64) throw new InvalidDataException("gamestate too large");
            data = new byte[len];
            s.Position = start;
            s.ReadExactly(data);
        }
        binary = !LooksLikeText(data);
        return data;
    }

    private static bool LooksLikeText(byte[] d)
    {
        int n = Math.Min(d.Length, 256);
        int printable = 0;
        for (int i = 0; i < n; i++)
        {
            byte c = d[i];
            if (c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c < 0x7f) || c >= 0x80) printable++;
        }
        return n > 0 && printable == n && d[0] != 0;
    }
}

/// <summary>Read-only, seekable window [offset, offset+length) over another stream (which stays open).</summary>
internal sealed class SubStream : Stream
{
    private readonly Stream _inner;
    private readonly long _offset;
    private readonly long _length;
    private long _pos;

    public SubStream(Stream inner, long offset, long length)
    {
        _inner = inner; _offset = offset; _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _pos; set => _pos = Math.Clamp(value, 0, _length); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long remaining = _length - _pos;
        if (remaining <= 0) return 0;
        if (buffer.Length > remaining) buffer = buffer[..(int)remaining];
        _inner.Position = _offset + _pos;
        int n = _inner.Read(buffer);
        _pos += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            _ => _length + offset,
        };
        return _pos;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
