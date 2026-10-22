// File helpers: hashing, atomic writes, path comparisons, and the link (reparse point) rules. Setup never writes or deletes
// through a junction or symbolic link: a developer's Modules\CK3Bridge is a junction into their checkout.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CK3xBL.Setup.Util;

internal static class FileOps
{
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string Sha256(byte[] data)
    {
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(data));
    }

    public static string Sha256File(string path)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16);
        return Hex(sha.ComputeHash(fs));
    }

    /// <summary>The file's SHA-256, or null when it is missing or unreadable.</summary>
    public static string? TrySha256File(string path)
    {
        try { return File.Exists(path) ? Sha256File(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>Writes through a temp file in the same folder, then swaps it in, so a crash never leaves half a file.</summary>
    public static void WriteAtomic(string path, byte[] data)
    {
        WriteAtomic(path, s => s.Write(data, 0, data.Length));
    }

    public static void WriteAtomic(string path, Action<Stream> write)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".ck3xbl_tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                write(fs);
                fs.Flush(true);
            }
            ReplaceWith(tmp, path);
        }
        finally
        {
            if (File.Exists(tmp)) TryDelete(tmp);
        }
    }

    /// <summary>Moves <paramref name="source"/> over <paramref name="target"/> (same folder).</summary>
    public static void ReplaceWith(string source, string target)
    {
        if (File.Exists(target))
        {
            var attrs = File.GetAttributes(target);
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(target, attrs & ~FileAttributes.ReadOnly);
            File.Replace(source, target, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(source, target);
        }
    }

    public static void DeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    public static bool TryDelete(string path)
    {
        try { DeleteFile(path); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>The absolute path without a trailing separator (a drive root keeps its "C:\").</summary>
    public static string Full(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
    }

    public static bool PathEquals(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);

    private static string WithSeparator(string full) => full.EndsWith("\\", StringComparison.Ordinal) ? full : full + "\\";

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or inside it.</summary>
    public static bool IsUnder(string path, string root)
    {
        var p = Full(path);
        var r = Full(root);
        return p.Equals(r, StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith(WithSeparator(r), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Path of <paramref name="path"/> relative to <paramref name="root"/> (which must contain it), with '\'.</summary>
    public static string Relative(string root, string path)
    {
        var r = Full(root);
        var p = Full(path);
        if (!IsUnder(p, r)) throw new ArgumentException($"{path} is not under {root}");
        return p.Length == r.Length ? "" : p.Substring(WithSeparator(r).Length);
    }

    public static bool IsReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>The first link on the way from <paramref name="boundary"/> (exclusive) down to <paramref name="path"/> (inclusive),
    /// or null. Folders above the boundary may be links on purpose (a moved Documents or Steam library) and are not checked.</summary>
    public static string? LinkOnPath(string boundary, string path)
    {
        var b = Full(boundary);
        var p = Full(path);
        if (!IsUnder(p, b)) return IsReparsePoint(p) ? p : null;
        var current = b;
        foreach (var part in Relative(b, p).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current)) return null;
            if (IsReparsePoint(current)) return current;
        }
        return null;
    }

    /// <summary>The first link anywhere inside an existing folder (the folder itself included), or null.</summary>
    public static string? LinkInside(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        if (IsReparsePoint(dir)) return dir;
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(d))
            {
                if (IsReparsePoint(entry)) return entry;
                if (Directory.Exists(entry)) stack.Push(entry);
            }
        }
        return null;
    }

    /// <summary>Removes a junction or symbolic link itself. Its target is never touched. Throws when it is not a link.</summary>
    public static void RemoveLink(string path)
    {
        if (!IsReparsePoint(path)) throw new IOException($"{path} is not a link");
        bool ok = Directory.Exists(path) ? Native.RemoveDirectoryW(path) : Native.DeleteFileW(path);
        if (!ok) throw new IOException($"could not remove the link {path} (Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
    }

    /// <summary>Deletes a folder tree without following links: a link inside is removed as a link, its target is kept.</summary>
    public static void DeleteTree(string dir)
    {
        if (IsReparsePoint(dir)) { RemoveLink(dir); return; }
        if (!Directory.Exists(dir)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            if (IsReparsePoint(entry)) RemoveLink(entry);
            else if (Directory.Exists(entry)) DeleteTree(entry);
            else DeleteFile(entry);
        }
        Directory.Delete(dir, recursive: false);
    }

    /// <summary>Deletes <paramref name="dir"/> and then its parents while they are empty, stopping at <paramref name="boundary"/>
    /// (never deleted). Links are never deleted here.</summary>
    public static void DeleteEmptyDirsUpward(string dir, string boundary)
    {
        var d = Full(dir);
        var b = Full(boundary);
        while (IsUnder(d, b) && !d.Equals(b, StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(d) || IsReparsePoint(d)) return;
            using (var e = Directory.EnumerateFileSystemEntries(d).GetEnumerator())
                if (e.MoveNext()) return;
            try { Directory.Delete(d, recursive: false); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            d = Path.GetDirectoryName(d)!;
        }
    }

    /// <summary>Copies a folder tree (no links inside: checked by the caller).</summary>
    public static void CopyTree(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.GetDirectories(source))
            CopyTree(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }

    /// <summary>Moves a file or folder. Across volumes a folder is copied to a temporary sibling of <paramref name="dest"/>
    /// first and renamed into place, so a failed copy never leaves half a folder at <paramref name="dest"/>; the source is
    /// deleted only after that.</summary>
    public static void Move(string source, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (File.Exists(source))
        {
            if (File.Exists(dest)) DeleteFile(dest);
            File.Move(source, dest);
            return;
        }
        if (string.Equals(Path.GetPathRoot(Full(source)), Path.GetPathRoot(Full(dest)), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(source, dest);
            return;
        }
        var tmp = dest + ".ck3xbl_tmp";
        if (Directory.Exists(tmp)) DeleteTree(tmp);
        try
        {
            CopyTree(source, tmp);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { DeleteTree(tmp); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw;
        }
        Directory.Move(tmp, dest);
        DeleteTree(source);
    }

    /// <summary>The folder a delete may climb up to for <paramref name="dir"/>: its parent, or the folder itself for a root.</summary>
    public static string ParentOrSelf(string dir) => Path.GetDirectoryName(Full(dir)) ?? Full(dir);

    /// <summary>A drive or share root ("D:\", "\\server\share"), which is never used as an install folder.</summary>
    public static bool IsRoot(string path)
    {
        try
        {
            var full = Full(path);
            return string.Equals(full.TrimEnd('\\'), (Path.GetPathRoot(full) ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>Whether the current user can create a file in <paramref name="dir"/> (created when missing, under its parent).</summary>
    public static bool CanWrite(string dir)
    {
        var probeDir = dir;
        while (!Directory.Exists(probeDir))
        {
            var parent = Path.GetDirectoryName(probeDir);
            if (parent is null) return false;
            probeDir = parent;
        }
        var probe = Path.Combine(probeDir, ".ck3xbl_write_probe_" + Guid.NewGuid().ToString("N"));
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
        finally { if (File.Exists(probe)) TryDelete(probe); }
    }

    /// <summary>A file's text without its UTF-8 BOM; <paramref name="hadBom"/> says whether it had one.</summary>
    public static string ReadUtf8(byte[] bytes, out bool hadBom)
    {
        hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return hadBom ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : Encoding.UTF8.GetString(bytes);
    }

    public static byte[] EncodeUtf8(string text, bool bom)
    {
        var body = Utf8NoBom.GetBytes(text);
        if (!bom) return body;
        var result = new byte[body.Length + 3];
        result[0] = 0xEF; result[1] = 0xBB; result[2] = 0xBF;
        Buffer.BlockCopy(body, 0, result, 3, body.Length);
        return result;
    }

    /// <summary>Reads a file another process may hold open.</summary>
    public static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }
}
