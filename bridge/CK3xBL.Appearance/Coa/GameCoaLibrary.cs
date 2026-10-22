// Predefined coats of arms from the install (common/coat_of_arms/coat_of_arms/*.txt, playset-merged): landed titles
// by key (01_landed_titles.txt: 1,881 titles), dynasties by history id (90_dynasties.txt) and the templates they
// reference with parent = "...". Used when the save cannot provide a title's CoA (binary saves in normal mode)
// (fallback). Generated/edited CoAs exist only in saves.
using CK3xBL.Appearance.Game;
using CK3xBL.Appearance.Pdx;

namespace CK3xBL.Appearance.Coa;

internal sealed class GameCoaLibrary
{
    private readonly Dictionary<string, PdxNode> _defs = new(StringComparer.Ordinal);
    private readonly NamedColors _colors;

    private GameCoaLibrary(NamedColors colors) { _colors = colors; }

    public int Count => _defs.Count;

    public static GameCoaLibrary Load(GameFiles? files, NamedColors colors)
    {
        var lib = new GameCoaLibrary(colors);
        if (files == null) return lib;
        foreach (var file in files.ListFiles(Path.Combine("common", "coat_of_arms", "coat_of_arms")))
        {
            var root = PdxTextParser.Parse(GameFiles.ReadShared(file));
            foreach (var it in root.Items)
                if (!it.Key.StartsWith('@') && it.Value.Block != null)
                    lib._defs[it.Key] = it.Value.Block;
        }
        return lib;
    }

    public PdxNode? Node(string key) => _defs.TryGetValue(key, out var n) ? n : null;

    /// <summary>Resolves a key (title key such as "k_france", dynasty history id, template name) to a CoA.</summary>
    public CoaModel? Get(string key, string source) =>
        _defs.TryGetValue(key, out var node) ? CoaParser.Parse(node, source, _colors, Node) : null;
}
