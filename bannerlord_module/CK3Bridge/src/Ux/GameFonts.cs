using System;
using System.Collections.Generic;
using System.IO;

namespace CK3Bridge
{
    /// <summary>
    /// The glyph set of Bannerlord's Galahad font, read once at runtime from the user's install
    /// (&lt;BL&gt;/GUI/GauntletUI/Fonts/Galahad/Galahad.fnt; nothing is copied). Galahad is the title font and almost a
    /// subset of FiraSansExtraCondensed (737 vs 752 glyphs), so text folded to it draws in every stock brush.
    /// Without the file, <see cref="Fold"/> is the identity.
    /// </summary>
    internal static class GameFonts
    {
        private static HashSet<int> _supported;
        private static bool _loaded;

        public static string Fold(string text)
        {
            EnsureLoaded();
            return GlyphFolding.Fold(text, _supported);
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                // The TaleWorlds assemblies live in <BL>/bin/Win64_Shipping_Client.
                string bin = Path.GetDirectoryName(typeof(TaleWorlds.Library.BasePath).Assembly.Location) ?? "";
                string path = Path.GetFullPath(Path.Combine(bin, "..", "..", "GUI", "GauntletUI", "Fonts", "Galahad", "Galahad.fnt"));
                if (!File.Exists(path))
                {
                    BridgeIo.Log($"font glyph table not found ({path}); names are drawn unfolded");
                    return;
                }
                _supported = GlyphFolding.ParseBmFontCharIds(File.ReadAllText(path));
                BridgeIo.Log($"font glyph table: {_supported.Count} glyphs from {path}");
            }
            catch (Exception e)
            {
                _supported = null;
                BridgeIo.Log("font glyph table unreadable: " + e.Message);
            }
        }
    }
}
