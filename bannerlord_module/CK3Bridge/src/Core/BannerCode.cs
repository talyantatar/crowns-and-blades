using System.Collections.Generic;
using System.Globalization;

namespace CK3Bridge
{
    /// <summary>One layer of a Bannerlord banner code (mesh.color.color2.sizeX.sizeY.posX.posY.stroke.mirror.rotation).</summary>
    public struct BannerLayerIds
    {
        public int Mesh;
        public int Color;
        public int Color2;
    }

    /// <summary>
    /// Strict syntactic check of a banner code. The engine silently drops trailing tokens and clears the whole banner
    /// on one non-integer token (Banner.TryGetBannerDataFromCode), so the module
    /// rejects anything that is not exactly groups of 10 integers, 1 background + up to 32 icons.
    /// Palette and mesh ids are checked against the live BannerManager by <see cref="BannerFactory"/>.
    /// </summary>
    public static class BannerCode
    {
        public const int TokensPerLayer = 10;
        public const int MaxLayers = 33;

        public static bool TryParse(string code, out List<BannerLayerIds> layers, out string error)
        {
            layers = new List<BannerLayerIds>();
            error = null;
            if (string.IsNullOrWhiteSpace(code))
            {
                error = "empty banner code";
                return false;
            }
            string[] tokens = code.Trim().Split('.');
            if (tokens.Length % TokensPerLayer != 0)
            {
                error = $"banner code has {tokens.Length} tokens, not a multiple of {TokensPerLayer}";
                return false;
            }
            int layerCount = tokens.Length / TokensPerLayer;
            if (layerCount > MaxLayers)
            {
                error = $"banner code has {layerCount} layers (max {MaxLayers})";
                return false;
            }
            var values = new int[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                if (!int.TryParse(tokens[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out values[i]))
                {
                    error = $"banner code token {i} ('{tokens[i]}') is not an integer";
                    return false;
                }
            }
            for (int l = 0; l < layerCount; l++)
            {
                int o = l * TokensPerLayer;
                layers.Add(new BannerLayerIds { Mesh = values[o], Color = values[o + 1], Color2 = values[o + 2] });
            }
            return true;
        }
    }
}
