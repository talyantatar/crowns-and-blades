// Pattern/emblem textures read from the user's CK3 install on demand (gfx/coat_of_arms/{patterns,colored_emblems,
// textured_emblems}/*.dds; mixed DXT1/DXT5/BGRA at 128-512 px).
// The bridge runs for a whole session, so memory stays bounded: a file's raw bytes are dropped as soon as the mip level
// that was asked for is decoded, and decoded levels live in a least-recently-used cache with a byte budget (a miss
// re-reads one file from disk, a few milliseconds). Only each texture's location and mip chain (a few bytes) and the
// title mask are kept for the whole session.
using System.Collections.Concurrent;
using CK3xBL.Appearance.Game;
using CK3xBL.Appearance.Imaging;

namespace CK3xBL.Appearance.Coa;

internal sealed class CoaTextures
{
    /// <summary>Default budget for decoded mip levels (a 512 px level is 1 MB, a 64 px one 16 KB).</summary>
    public const long DefaultBudgetBytes = 64L << 20;

    private sealed record TextureInfo(string Path, int Width, int Height, int MipCount);

    private readonly GameFiles? _files;
    // Where each texture asked for lives and its mip chain; null = not in the install (or unreadable).
    private readonly ConcurrentDictionary<string, TextureInfo?> _info = new(StringComparer.OrdinalIgnoreCase);
    private readonly LevelCache _levels;
    private readonly Lazy<RgbaImage?> _titleMask;

    public CoaTextures(GameFiles? files, long budgetBytes = DefaultBudgetBytes)
    {
        _files = files;
        _levels = new LevelCache(budgetBytes);
        _titleMask = new Lazy<RgbaImage?>(() => Decode(Path.Combine("gfx", "interface", "coat_of_arms", "title_mask.dds"), double.MaxValue, keep: false));
    }

    public bool Available => _files != null;

    /// <summary>Bytes of decoded pixels currently cached (diagnostics, self-test).</summary>
    public long CachedBytes => _levels.Bytes;

    /// <summary>A pattern texture at the mip level closest to (not below) targetPixels, or null when missing.</summary>
    public RgbaImage? Pattern(string name, double targetPixels) =>
        Decode(Path.Combine("gfx", "coat_of_arms", "patterns", name + ".dds"), targetPixels, keep: true);

    public RgbaImage? Emblem(string name, bool textured, double targetPixels) =>
        Decode(Path.Combine("gfx", "coat_of_arms", textured ? "textured_emblems" : "colored_emblems", name + ".dds"), targetPixels, keep: true)
        ?? Decode(Path.Combine("gfx", "coat_of_arms", textured ? "colored_emblems" : "textured_emblems", name + ".dds"), targetPixels, keep: true);

    /// <summary>The shield-shaped mask CK3 uses for title coats of arms (gfx/interface/coat_of_arms/title_mask.dds); kept for the session.</summary>
    public RgbaImage? TitleMask() => _titleMask.Value;

    /// <summary>The mip level of 'rel' for targetPixels; cached in the LRU when 'keep'.</summary>
    private RgbaImage? Decode(string rel, double targetPixels, bool keep)
    {
        if (_files == null) return null;
        DdsImage? image = null;
        if (!_info.TryGetValue(rel, out var info))
        {
            image = LoadFromInstall(rel, out string? path);
            info = image == null ? null : new TextureInfo(path!, image.Width, image.Height, image.MipCount);
            _info[rel] = info;
        }
        if (info == null) return null;
        int level = DdsImage.LevelFor(info.Width, info.Height, info.MipCount, Math.Max(1, targetPixels));
        if (keep && _levels.Get((info.Path, level)) is RgbaImage cached) return cached;
        image ??= LoadFile(info.Path);
        if (image == null) return null;
        RgbaImage decoded;
        try { decoded = image.Decode(level); }
        catch (InvalidDataException) { return null; }
        if (keep) _levels.Add((info.Path, level), decoded);
        return decoded;
    }

    private DdsImage? LoadFromInstall(string rel, out string? path)
    {
        path = _files!.FindFile(rel);
        return path == null ? null : LoadFile(path);
    }

    private static DdsImage? LoadFile(string path)
    {
        try { return DdsImage.Load(path); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Decoded mip levels; the least recently used are dropped once their pixels exceed the budget.</summary>
    private sealed class LevelCache(long budgetBytes)
    {
        private readonly object _lock = new();
        private readonly Dictionary<(string, int), LinkedListNode<((string, int) Key, RgbaImage Image)>> _map = new();
        private readonly LinkedList<((string, int) Key, RgbaImage Image)> _order = new();   // most recent first
        private long _bytes;

        public long Bytes { get { lock (_lock) return _bytes; } }

        public RgbaImage? Get((string, int) key)
        {
            lock (_lock)
            {
                if (!_map.TryGetValue(key, out var node)) return null;
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Image;
            }
        }

        public void Add((string, int) key, RgbaImage image)
        {
            lock (_lock)
            {
                if (_map.ContainsKey(key)) return;   // decoded twice by racing callers: keep the first
                _map[key] = _order.AddFirst((key, image));
                _bytes += image.Pixels.Length;
                // The newest level always stays, even when it alone exceeds the budget.
                while (_bytes > budgetBytes && _order.Count > 1)
                {
                    var last = _order.Last!;
                    _order.RemoveLast();
                    _map.Remove(last.Value.Key);
                    _bytes -= last.Value.Image.Pixels.Length;
                }
            }
        }
    }
}
