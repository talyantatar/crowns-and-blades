// Builds CK3xBL-Setup-<version>.exe: the bare setup exe + payload.zip (manifest.json first) + a 32-byte trailer.
// Used by the release script through the tool in installer/tests ("pack"). See PAYLOAD_LAYOUT.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CK3xBL.Setup.Util;

namespace CK3xBL.Setup;

internal sealed class PackRequest
{
    public string StubExe = "";
    public string PayloadDir = "";
    public string Version = "";
    public string OutputExe = "";
    public string? SupportsCk3 = "1.20.*";
    public string? SupportsBannerlord = "v1.4.*";
    /// <summary>Extra leak patterns (regex, case-insensitive) that must not occur in any payload file, text or binary.</summary>
    public List<string> Forbidden = new();
}

internal static class Packer
{
    /// <summary>Files the payload must have.</summary>
    public static readonly string[] Required =
    {
        "app/" + Product.BridgeExe,
        "app/data/ck3_maa_index.json",
        PayloadManifest.ModPrefix + "descriptor.mod",
        PayloadManifest.ModulePrefix + "SubModule.xml",
        PayloadManifest.ModulePrefix + "bin/Win64_Shipping_Client/CK3Bridge.dll",
    };

    /// <summary>Built-in leak checks that hold for every machine: no Windows user folders and no debug symbols.</summary>
    private static readonly Regex UserFolder = new(@"[A-Za-z]:[\\/]+Users[\\/]+[^\\/\s""<>|*?]+[\\/]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static PayloadManifest Pack(PackRequest req, Action<string> log)
    {
        if (!ReleaseVersion.TryParse(req.Version, out _)) throw new ArgumentException($"'{req.Version}' is not a version (expected e.g. 0.9.0-beta)");
        var root = FileOps.Full(req.PayloadDir);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);

        var manifest = new PayloadManifest
        {
            Version = req.Version,
            SupportsCk3 = req.SupportsCk3,
            SupportsBannerlord = req.SupportsBannerlord,
        };
        var problems = new List<string>();
        var forbidden = req.Forbidden.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)).ToList();
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => FileOps.Relative(root, f).Replace('\\', '/'))
            .Where(p => p != PayloadManifest.FileName)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        foreach (var rel in files)
        {
            var full = Path.Combine(root, rel.Replace('/', '\\'));
            if (FileOps.IsReparsePoint(full)) { problems.Add($"{rel}: is a link"); continue; }
            try { PayloadManifest.CheckPath(rel); }
            catch (InvalidDataException e) { problems.Add(e.Message); continue; }
            if (rel.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) problems.Add($"{rel}: debug symbols must not ship");
            if (rel.Equals("app/config.json", StringComparison.OrdinalIgnoreCase)) problems.Add($"{rel}: a developer's bridge settings must not ship");
            var bytes = File.ReadAllBytes(full);
            foreach (var hit in LeakHits(bytes, IsText(rel), forbidden)) problems.Add($"{rel}: {hit}");
            manifest.Files.Add(new PayloadFile { Path = rel, Size = bytes.LongLength, Sha256 = FileOps.Sha256(bytes) });
        }
        foreach (var required in Required)
            if (!manifest.Files.Any(f => f.Path == required)) problems.Add($"missing {required}");
        if (problems.Count > 0) throw new InvalidDataException("the payload is not ready:\n  " + string.Join("\n  ", problems));

        var stub = Payload.ReadStub(req.StubExe);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(req.OutputExe))!);
        var tmp = req.OutputExe + ".tmp";
        var zipTmp = req.OutputExe + ".zip.tmp";
        try
        {
            // The zip is built on its own (its offsets are relative to its start), then appended to the stub.
            using (var zipFile = new FileStream(zipTmp, FileMode.Create, FileAccess.ReadWrite))
            using (var zip = new ZipArchive(zipFile, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                AddEntry(zip, PayloadManifest.FileName, Encoding.UTF8.GetBytes(manifest.ToJson()));
                foreach (var f in manifest.Files)
                    AddEntry(zip, f.Path, File.ReadAllBytes(Path.Combine(root, f.Path.Replace('/', '\\'))));
            }
            using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                output.Write(stub, 0, stub.Length);
                long zipStart = output.Position;
                using (var zipFile = new FileStream(zipTmp, FileMode.Open, FileAccess.Read)) zipFile.CopyTo(output);
                long zipLength = output.Position - zipStart;
                var trailer = new byte[Payload.TrailerSize];
                BitConverter.GetBytes(zipStart).CopyTo(trailer, 0);
                BitConverter.GetBytes(zipLength).CopyTo(trailer, 8);
                BitConverter.GetBytes(1).CopyTo(trailer, 16);
                Payload.Magic.CopyTo(trailer, 24);
                output.Write(trailer, 0, trailer.Length);
            }
            if (File.Exists(req.OutputExe)) FileOps.DeleteFile(req.OutputExe);
            File.Move(tmp, req.OutputExe);
        }
        finally
        {
            FileOps.TryDelete(zipTmp);
            FileOps.TryDelete(tmp);
        }

        using (var check = Payload.OpenAppended(req.OutputExe) ?? throw new InvalidDataException("the packed exe has no payload"))
            check.VerifyAll();
        log($"packed {manifest.Files.Count} files, version {manifest.Version}, into {req.OutputExe} ({new FileInfo(req.OutputExe).Length / 1024} KB)");
        return manifest;
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] data)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);   // reproducible output
        using var s = entry.Open();
        s.Write(data, 0, data.Length);
    }

    private static readonly string[] TextExtensions = { ".json", ".txt", ".yml", ".xml", ".mod", ".md", ".gui", ".csv", ".config" };

    internal static bool IsText(string path) => TextExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Leak findings in one file. Text is read as UTF-8; a binary as single-byte text and as UTF-16 (the two ways
    /// paths and strings sit inside .exe and .dll files).</summary>
    internal static IEnumerable<string> LeakHits(byte[] bytes, bool isText, List<Regex> forbidden)
    {
        var views = isText
            ? new[] { FileOps.ReadUtf8(bytes, out _) }
            : new[]
            {
                Encoding.GetEncoding(28591).GetString(bytes),
                Encoding.Unicode.GetString(bytes),
                bytes.Length > 1 ? Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1) : "",   // UTF-16 at odd offsets
            };
        var hits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in views)
        {
            var m = UserFolder.Match(text);
            if (m.Success) hits.Add($"contains a Windows user folder path ({m.Value})");
            foreach (var re in forbidden)
            {
                var f = re.Match(text);
                if (f.Success) hits.Add($"matches the forbidden pattern {re} ({Shorten(f.Value)})");
            }
        }
        return hits;
    }

    private static string Shorten(string s) => s.Length <= 60 ? s : s.Substring(0, 60) + "...";
}
