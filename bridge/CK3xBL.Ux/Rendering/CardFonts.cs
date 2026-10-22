using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using CK3xBL.Core.Config;

namespace CK3xBL.Ux.Rendering;

/// <summary>
/// The typeface for title cards. CK3 ships Fondamento (SIL OFL) as TrueType files under game/fonts; they are read at runtime
/// from the user's install into memory and loaded with <see cref="PrivateFontCollection.AddMemoryFont"/> (nothing is copied
/// to disk, and no handle stays open on the install: AddFontFile would keep the files locked against writers, e.g. a Steam
/// update, for as long as the bridge runs). GDI+ cannot load CFF-flavoured .otf files, so only files GDI+ accepts are used.
/// Falls back to the same family installed on the system, then to a classic serif. Loading never throws.
/// </summary>
public sealed class CardFonts : IDisposable
{
    private static readonly string[] FallbackSerifs = ["Palatino Linotype", "Book Antiqua", "Georgia", "Cambria", "Times New Roman"];

    private readonly PrivateFontCollection? _collection;
    private readonly List<nint> _fontMemory;   // GDI+ reads memory fonts in place: freed only after the collection is disposed
    private bool _disposed;

    /// <summary>The family used for every line of the card.</summary>
    public FontFamily Family { get; }
    /// <summary>True when the family has a real italic face (otherwise the subtitle stays upright rather than slanted).</summary>
    public bool HasItalic { get; }
    /// <summary>Where the family came from: the font file(s) loaded, "system:&lt;name&gt;" or "fallback:&lt;name&gt;".</summary>
    public string Source { get; }
    /// <summary>True when the requested family was found (in the CK3 install or installed on the system).</summary>
    public bool IsRequestedFamily { get; }

    private CardFonts(FontFamily family, PrivateFontCollection? collection, List<nint>? fontMemory, string source, bool isRequested)
    {
        Family = family;
        _collection = collection;
        _fontMemory = fontMemory ?? [];
        Source = source;
        IsRequestedFamily = isRequested;
        HasItalic = family.IsStyleAvailable(FontStyle.Italic);
    }

    /// <summary>
    /// Loads <paramref name="familyName"/> from the CK3 install (searching game/fonts recursively), else falls back. Never
    /// throws: any failure (an unreadable folder, a broken file, a GDI+ error) moves on to the next source.
    /// </summary>
    public static CardFonts Load(string familyName, string? ck3GameDir)
    {
        try
        {
            string? fontsDir = Ck3Install.FindFontsDirectory(ck3GameDir);
            if (fontsDir != null && TryLoadFromDirectory(familyName, fontsDir) is { } fromInstall) return fromInstall;
        }
        catch (Exception e) { UxLog.Write($"font: loading '{familyName}' from the CK3 install failed: {e.Message}"); }

        try
        {
            var installed = InstalledFamilyNames();
            if (installed.Contains(familyName))
                return new CardFonts(new FontFamily(familyName), null, null, "system:" + familyName, true);
            foreach (var name in FallbackSerifs)
                if (installed.Contains(name))
                    return new CardFonts(new FontFamily(name), null, null, "fallback:" + name, false);
        }
        catch (Exception e) { UxLog.Write($"font: reading the installed fonts failed: {e.Message}"); }
        return new CardFonts(new FontFamily(GenericFontFamilies.Serif), null, null, "fallback:generic-serif", false);
    }

    /// <summary>
    /// All font files under <paramref name="fontsDir"/> whose file name contains the family name (spaces ignored). Folders that
    /// cannot be read are skipped.
    /// </summary>
    public static IReadOnlyList<string> FindCandidateFiles(string familyName, string fontsDir)
    {
        string key = Normalize(familyName);
        if (key.Length == 0 || !Directory.Exists(fontsDir)) return [];
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        try
        {
            return Directory.EnumerateFiles(fontsDir, "*.*", options)
                .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
                .Where(f => Normalize(Path.GetFileNameWithoutExtension(f)).Contains(key, StringComparison.Ordinal))
                // TrueType first: GDI+ renders .ttf reliably; .otf only works when it carries TrueType outlines.
                .OrderBy(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            UxLog.Write($"font: cannot search '{fontsDir}': {e.Message}");
            return [];
        }
    }

    private static CardFonts? TryLoadFromDirectory(string familyName, string fontsDir)
    {
        var files = FindCandidateFiles(familyName, fontsDir);
        if (files.Count == 0) return null;
        var collection = new PrivateFontCollection();
        var memory = new List<nint>();
        var loaded = new List<string>();
        FontFamily? family = null;
        CardFonts? result = null;
        try
        {
            foreach (var file in files)
                if (TryAddMemoryFont(collection, file, memory)) loaded.Add(file);
            // Several files of one family (Regular + Italic) merge into a single FontFamily with both styles. Every element of
            // Families is a separate GDI+ clone: keep one, dispose the rest.
            var families = collection.Families;
            family = families.FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase))
                     ?? families.FirstOrDefault(f => f.IsStyleAvailable(FontStyle.Regular));
            foreach (var other in families)
                if (!ReferenceEquals(other, family)) other.Dispose();
            if (family != null && family.IsStyleAvailable(FontStyle.Regular))
                result = new CardFonts(family, collection, memory, string.Join(";", loaded), true);
            return result;
        }
        finally
        {
            if (result == null)
            {
                family?.Dispose();
                collection.Dispose();
                foreach (var block in memory) Marshal.FreeCoTaskMem(block);
            }
        }
    }

    /// <summary>Reads one font file into unmanaged memory and adds it. On success the block is appended to <paramref name="memory"/>.</summary>
    private static bool TryAddMemoryFont(PrivateFontCollection collection, string file, List<nint> memory)
    {
        nint block = 0;
        try
        {
            byte[] bytes = File.ReadAllBytes(file);
            block = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, block, bytes.Length);
            collection.AddMemoryFont(block, bytes.Length);
            memory.Add(block);
            return true;
        }
        catch (Exception e)
        {
            if (block != 0) Marshal.FreeCoTaskMem(block);
            UxLog.Write($"font: cannot load '{file}': {e.Message}");
            return false;
        }
    }

    private static HashSet<string> InstalledFamilyNames()
    {
        using var installed = new InstalledFontCollection();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in installed.Families)
        {
            names.Add(family.Name);
            family.Dispose();
        }
        return names;
    }

    private static string Normalize(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>
    /// Releases the family (and the font memory). Takes the renderer's text lock, so it waits for a card or hint being drawn
    /// with these fonts; drawing with them afterwards fails with an exception instead of reading freed memory.
    /// </summary>
    public void Dispose()
    {
        lock (TitleCardRenderer.TextGate)
        {
            if (_disposed) return;
            _disposed = true;
            Family.Dispose();
            _collection?.Dispose();
            foreach (var block in _fontMemory) Marshal.FreeCoTaskMem(block);
            _fontMemory.Clear();
        }
    }
}

/// <summary>Locates the Crusader Kings III install: the configured path (GameLocator.Resolve has already filled it in at start), else
/// the same detection the bridge uses (GameLocator).</summary>
public static class Ck3Install
{
    /// <summary>
    /// The CK3 <c>game/fonts</c> directory. <paramref name="configured"/> may be the install root or its <c>game</c> folder.
    /// Returns null when CK3 cannot be found.
    /// </summary>
    public static string? FindFontsDirectory(string? configured)
    {
        foreach (var root in CandidateRoots(configured))
        {
            foreach (var fonts in new[] { Path.Combine(root, "game", "fonts"), Path.Combine(root, "fonts") })
                if (Directory.Exists(fonts)) return fonts;
        }
        return null;
    }

    private static IEnumerable<string> CandidateRoots(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
        string? detected = null;
        try { detected = GameLocator.FindCk3(); }
        catch (Exception e) { UxLog.Write($"font: CK3 detection failed: {e.Message}"); }
        if (detected is not null) yield return detected;
    }
}
