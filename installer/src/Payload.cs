// The payload: a zip appended to CK3xBL-Setup.exe (see PAYLOAD_LAYOUT.md), with manifest.json listing every file's size and
// SHA-256. Nothing is installed until every entry has been checked against the manifest.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CK3xBL.Setup.Util;

namespace CK3xBL.Setup;

internal sealed class PayloadFile
{
    public string Path = "";     // zip path with '/', e.g. "app/data/mapping.json"
    public long Size;
    public string Sha256 = "";

    /// <summary>"app", "ck3mod" or "blmodule".</summary>
    public string Area => Path.Substring(0, Path.IndexOf('/'));
}

internal sealed class PayloadManifest
{
    public const int CurrentFormat = 1;
    public const string FileName = "manifest.json";
    public static readonly string[] Areas = { "app", "ck3mod", "blmodule" };
    public const string AppPrefix = "app/";
    public const string ModPrefix = "ck3mod/" + Product.ModFolder + "/";
    public const string ModulePrefix = "blmodule/" + Product.ModuleFolder + "/";

    public int Format = CurrentFormat;
    public string ProductId = Product.InstallFolder;
    public string Version = "";
    public string? SupportsCk3;
    public string? SupportsBannerlord;
    public List<PayloadFile> Files = new();

    public static PayloadManifest Parse(string json)
    {
        if (Json.Parse(json) is not JsonObject o) throw new InvalidDataException("manifest.json is not a JSON object");
        var m = new PayloadManifest
        {
            Format = (int)(o.GetLong("format") ?? 0),
            ProductId = o.GetString("product") ?? "",
            Version = o.GetString("version") ?? "",
        };
        if (m.Format != CurrentFormat) throw new InvalidDataException($"payload format {m.Format} is not supported by this setup (expects {CurrentFormat})");
        if (m.ProductId != Product.InstallFolder) throw new InvalidDataException($"payload is for '{m.ProductId}', not {Product.InstallFolder}");
        if (!ReleaseVersion.TryParse(m.Version, out _)) throw new InvalidDataException($"payload version '{m.Version}' is not valid");
        if (o.GetObject("supports") is { } s)
        {
            m.SupportsCk3 = s.GetString("ck3");
            m.SupportsBannerlord = s.GetString("bannerlord");
        }
        foreach (var item in o.GetArray("files") ?? new List<object?>())
        {
            if (item is not JsonObject f) throw new InvalidDataException("manifest.json: a files[] entry is not an object");
            var file = new PayloadFile
            {
                Path = f.GetString("path") ?? "",
                Size = f.GetLong("size") ?? -1,
                Sha256 = (f.GetString("sha256") ?? "").ToLowerInvariant(),
            };
            CheckPath(file.Path);
            if (file.Size < 0 || file.Sha256.Length != 64) throw new InvalidDataException($"manifest.json: bad size or sha256 for {file.Path}");
            m.Files.Add(file);
        }
        var dup = m.Files.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new InvalidDataException($"manifest.json lists {dup.Key} twice");
        return m;
    }

    public string ToJson()
    {
        var o = new JsonObject
        {
            ["format"] = new JsonNumber(Format),
            ["product"] = ProductId,
            ["version"] = Version,
        };
        if (SupportsCk3 is not null || SupportsBannerlord is not null)
        {
            var s = new JsonObject();
            if (SupportsCk3 is not null) s["ck3"] = SupportsCk3;
            if (SupportsBannerlord is not null) s["bannerlord"] = SupportsBannerlord;
            o["supports"] = s;
        }
        o["files"] = Files.Select(f => (object?)new JsonObject
        {
            ["path"] = f.Path,
            ["size"] = new JsonNumber(f.Size),
            ["sha256"] = f.Sha256,
        }).ToList();
        return Json.Write(o, indented: true) + "\n";
    }

    /// <summary>A payload path must be relative, use '/', stay inside its area and name a known area.</summary>
    public static void CheckPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.IndexOf('\\') >= 0 || path.StartsWith("/", StringComparison.Ordinal) ||
            path.IndexOf(':') >= 0 || path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
            throw new InvalidDataException($"payload path '{path}' is not a safe relative path");
        var parts = path.Split('/');
        if (parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.Trim() != p || p.EndsWith(".", StringComparison.Ordinal)))
            throw new InvalidDataException($"payload path '{path}' is not a safe relative path");
        if (parts.Length < 2 || !Areas.Contains(parts[0])) throw new InvalidDataException($"payload path '{path}' is outside app/, ck3mod/ and blmodule/");
        if (parts[0] == "ck3mod" && !path.StartsWith(ModPrefix, StringComparison.Ordinal))
            throw new InvalidDataException($"payload path '{path}' must be under {ModPrefix}");
        if (parts[0] == "blmodule" && !path.StartsWith(ModulePrefix, StringComparison.Ordinal))
            throw new InvalidDataException($"payload path '{path}' must be under {ModulePrefix}");
    }
}

internal sealed class Payload : IDisposable
{
    /// <summary>Trailer at the end of a packed setup exe: int64 zip offset, int64 zip length, int32 format, int32 reserved, magic.</summary>
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("CK3XBLPL");
    public const int TrailerSize = 8 + 8 + 4 + 4 + 8;

    private readonly Stream _stream;
    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.Ordinal);

    public PayloadManifest Manifest { get; }
    /// <summary>Where it came from (for the log).</summary>
    public string Origin { get; }

    private Payload(Stream stream, string origin)
    {
        _stream = stream;
        Origin = origin;
        try
        {
            _zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            foreach (var e in _zip.Entries)
            {
                if (e.FullName.EndsWith("/", StringComparison.Ordinal) || e.FullName.EndsWith("\\", StringComparison.Ordinal)) continue;
                var name = e.FullName.Replace('\\', '/');   // some zip tools write '\'
                if (_entries.ContainsKey(name)) throw new InvalidDataException($"the payload holds {name} twice");
                _entries[name] = e;
            }
            if (!_entries.TryGetValue(PayloadManifest.FileName, out var manifestEntry))
                throw new InvalidDataException("the payload has no manifest.json");
            using var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8);
            try { Manifest = PayloadManifest.Parse(reader.ReadToEnd()); }
            catch (FormatException e) { throw new InvalidDataException($"the payload's manifest.json is damaged ({e.Message})", e); }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>The payload appended to a packed setup exe, or null when the exe has none (a bare stub).</summary>
    public static Payload? OpenAppended(string exePath)
    {
        var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            if (!TryReadTrailer(fs, out long offset, out long length)) { fs.Dispose(); return null; }
            return new Payload(new SubStream(fs, offset, length), exePath);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>A payload given with /payload: a .zip, or a packed setup exe.</summary>
    public static Payload Open(string path)
    {
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return OpenAppended(path) ?? throw new InvalidDataException($"{path} carries no payload");
        return new Payload(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), path);
    }

    /// <summary>Length of the exe without its payload (the stub), or the whole length when there is none.</summary>
    public static long StubLength(string exePath)
    {
        using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return TryReadTrailer(fs, out long offset, out _) ? offset : fs.Length;
    }

    /// <summary>The bare setup exe (no payload, no signature): used as the uninstaller copy and by the pack tool.</summary>
    public static byte[] ReadStub(string exePath)
    {
        long length = StubLength(exePath);
        var bytes = new byte[length];
        using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            int read = 0;
            while (read < length)
            {
                int n = fs.Read(bytes, read, (int)Math.Min(1 << 20, length - read));
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
        }
        PeFile.ClearSecurityDirectory(bytes);
        return bytes;
    }

    private static bool TryReadTrailer(FileStream fs, out long offset, out long length)
    {
        offset = length = 0;
        // A signature (Authenticode) added after packing sits behind the payload, 8-byte aligned with zero padding.
        long end = PeFile.SecurityDirectoryStart(fs) ?? fs.Length;
        var tail = new byte[Math.Min(end, TrailerSize + 8)];
        fs.Position = end - tail.Length;
        ReadExactly(fs, tail);
        int last = tail.Length;
        while (last > 0 && tail[last - 1] == 0 && tail.Length - last < 8) last--;
        if (last < TrailerSize) return false;
        int start = last - TrailerSize;
        for (int i = 0; i < Magic.Length; i++)
            if (tail[start + 24 + i] != Magic[i]) return false;
        offset = BitConverter.ToInt64(tail, start);
        length = BitConverter.ToInt64(tail, start + 8);
        int format = BitConverter.ToInt32(tail, start + 16);
        long trailerPos = end - tail.Length + start;
        if (format != 1 || offset <= 0 || length <= 0 || offset + length != trailerPos)
            throw new InvalidDataException("the setup's payload trailer is damaged; download the setup again");
        return true;
    }

    private static void ReadExactly(Stream s, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer, read, buffer.Length - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
    }

    public IEnumerable<PayloadFile> FilesWithPrefix(string prefix) =>
        Manifest.Files.Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal));

    public bool Has(string path) => Manifest.Files.Any(f => f.Path == path);

    /// <summary>Checks every listed file (present, size, SHA-256) and that nothing unlisted is in the zip.</summary>
    public void VerifyAll()
    {
        foreach (var f in Manifest.Files)
        {
            if (!_entries.TryGetValue(f.Path, out var e)) throw new InvalidDataException($"the payload is missing {f.Path}");
            if (e.Length != f.Size) throw new InvalidDataException($"payload file {f.Path} has the wrong size");
            using var s = e.Open();
            using var sha = SHA256.Create();
            if (FileOps.Hex(sha.ComputeHash(s)) != f.Sha256) throw new InvalidDataException($"payload file {f.Path} is damaged (SHA-256 mismatch)");
        }
        var listed = new HashSet<string>(Manifest.Files.Select(f => f.Path), StringComparer.Ordinal) { PayloadManifest.FileName };
        var extra = _entries.Keys.FirstOrDefault(k => !listed.Contains(k));
        if (extra is not null) throw new InvalidDataException($"the payload holds {extra}, which manifest.json does not list");
    }

    public byte[] ReadAll(PayloadFile f)
    {
        using var s = _entries[f.Path].Open();
        var ms = new MemoryStream((int)Math.Min(f.Size, int.MaxValue));
        s.CopyTo(ms);
        var bytes = ms.ToArray();
        if (bytes.LongLength != f.Size || FileOps.Sha256(bytes) != f.Sha256) throw new InvalidDataException($"payload file {f.Path} is damaged");
        return bytes;
    }

    /// <summary>Writes one payload file to <paramref name="dest"/> atomically, checking its hash on the way.</summary>
    public void Extract(PayloadFile f, string dest)
    {
        FileOps.WriteAtomic(dest, output =>
        {
            using var input = _entries[f.Path].Open();
            using var sha = SHA256.Create();
            var buffer = new byte[1 << 16];
            long total = 0;
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.TransformBlock(buffer, 0, n, null, 0);
                output.Write(buffer, 0, n);
                total += n;
            }
            sha.TransformFinalBlock(buffer, 0, 0);
            if (total != f.Size || FileOps.Hex(sha.Hash) != f.Sha256) throw new InvalidDataException($"payload file {f.Path} is damaged");
        });
    }

    public void Dispose()
    {
        _zip.Dispose();
        _stream.Dispose();
    }
}

/// <summary>A read-only window onto part of a stream (the zip inside the setup exe).</summary>
internal sealed class SubStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start, _length;
    private long _position;

    public SubStream(Stream inner, long start, long length)
    {
        _inner = inner;
        _start = start;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long left = _length - _position;
        if (left <= 0) return 0;
        if (count > left) count = (int)left;
        _inner.Position = _start + _position;
        int n = _inner.Read(buffer, offset, count);
        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Just enough PE parsing to find (and clear) the Authenticode certificate table.</summary>
internal static class PeFile
{
    private static int SecurityDirectoryOffset(byte[] header)
    {
        if (header.Length < 0x40 || header[0] != 'M' || header[1] != 'Z') return -1;
        int pe = BitConverter.ToInt32(header, 0x3C);
        if (pe <= 0 || pe + 24 + 2 > header.Length) return -1;
        if (header[pe] != 'P' || header[pe + 1] != 'E' || header[pe + 2] != 0 || header[pe + 3] != 0) return -1;
        int optional = pe + 24;
        ushort magic = BitConverter.ToUInt16(header, optional);
        int dataDirs = magic switch { 0x10B => optional + 96, 0x20B => optional + 112, _ => -1 };
        if (dataDirs < 0) return -1;
        int entry = dataDirs + 4 * 8;   // IMAGE_DIRECTORY_ENTRY_SECURITY = 4
        return entry + 8 <= header.Length ? entry : -1;
    }

    /// <summary>File offset where the certificate table starts, when the exe is signed and the table is at its end.</summary>
    public static long? SecurityDirectoryStart(FileStream fs)
    {
        var header = new byte[Math.Min(4096, fs.Length)];
        fs.Position = 0;
        int read = fs.Read(header, 0, header.Length);
        if (read < header.Length) return null;
        int entry = SecurityDirectoryOffset(header);
        if (entry < 0) return null;
        uint offset = BitConverter.ToUInt32(header, entry);
        uint size = BitConverter.ToUInt32(header, entry + 4);
        if (offset == 0 || size == 0 || offset + (long)size != fs.Length) return null;
        return offset;
    }

    /// <summary>Clears the certificate table entry of an in-memory PE (a cut-down copy must not claim a signature it lacks).</summary>
    public static void ClearSecurityDirectory(byte[] image)
    {
        int entry = SecurityDirectoryOffset(image);
        if (entry < 0) return;
        for (int i = 0; i < 8; i++) image[entry + i] = 0;
    }
}
