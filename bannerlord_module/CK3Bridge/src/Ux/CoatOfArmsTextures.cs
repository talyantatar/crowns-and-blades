using System;
using System.Collections;
using System.IO;
using System.Reflection;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI;
using TaleWorlds.TwoDimension;

namespace CK3Bridge
{
    /// <summary>
    /// The two CK3 coats of arms of the current battle (request presentation.attackerCoaPng / defenderCoaPng, PNGs the
    /// bridge renders from the user's CK3 install, PROTOCOL.md §7.3), as Gauntlet textures for the loading card and the
    /// in-engine title card. Player's side left, enemy right, like the CK3 event window.
    ///
    /// Loading uses the stock path for runtime images: TaleWorlds.Engine.Texture.CreateFromMemory(bytes) wrapped in
    /// EngineTexture (AvatarThumbnailCache.OnCreateTexture for compressed avatar images; OnlineImageTextureProvider for
    /// downloaded PNGs). Widgets reach the textures through two TextureProviders, which TextureProviderFactory finds by
    /// class name (it scans every loaded assembly in WidgetInfo.Refresh, run by GauntletUISubModule.OnSubModuleLoad after
    /// all module DLLs are loaded, Module.LoadSubModules). Main thread only.
    ///
    /// Engine textures are not released explicitly: Texture.Release dereferences RenderTargetComponent, which a plain
    /// texture does not have, and the stock OnlineImageTextureProvider never releases either; dropping the last reference
    /// lets the engine free it.
    /// </summary>
    internal static class CoatOfArmsTextures
    {
        private const long MaxFileBytes = 4L * 1024 * 1024;
        private const int MaxPixels = 2048;

        public static Texture Player { get; private set; }
        public static Texture Enemy { get; private set; }
        public static bool Any => Player != null || Enemy != null;

        public static void Load(string playerPng, string enemyPng)
        {
            Clear();
            if (string.IsNullOrWhiteSpace(playerPng) && string.IsNullOrWhiteSpace(enemyPng)) return;
            if (!ProvidersRegistered())
            {
                BridgeIo.Log("coat of arms textures skipped: the CK3 texture providers are not registered with Gauntlet");
                return;
            }
            Player = TryLoad(playerPng, "player");
            Enemy = TryLoad(enemyPng, "enemy");
        }

        public static void Clear()
        {
            Player = null;
            Enemy = null;
        }

        private static Texture TryLoad(string path, string label)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                if (!Path.IsPathRooted(path) || !File.Exists(path))
                {
                    BridgeIo.Log($"{label} coat of arms: '{path}' not found");
                    return null;
                }
                if (new FileInfo(path).Length > MaxFileBytes)
                {
                    BridgeIo.Log($"{label} coat of arms: '{path}' is larger than {MaxFileBytes} bytes");
                    return null;
                }
                byte[] bytes = File.ReadAllBytes(path);
                if (!PngHeader.TryRead(bytes, out int width, out int height) || width > MaxPixels || height > MaxPixels)
                {
                    BridgeIo.Log($"{label} coat of arms: '{path}' is not a PNG of at most {MaxPixels}x{MaxPixels}");
                    return null;
                }
                TaleWorlds.Engine.Texture engineTexture = TaleWorlds.Engine.Texture.CreateFromMemory(bytes);
                if (engineTexture == null)
                {
                    BridgeIo.Log($"{label} coat of arms: the engine did not create a texture from '{path}'");
                    return null;
                }
                engineTexture.Name = "ck3xbl_coa_" + label;
                BridgeIo.Log($"{label} coat of arms loaded ({width}x{height}) from {path}");
                return new Texture(new EngineTexture(engineTexture));
            }
            catch (Exception e)
            {
                BridgeIo.Log($"{label} coat of arms: loading '{path}' failed: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// TextureProviderFactory.CreateInstance asserts (and returns null every frame) for an unknown provider name, so
        /// the textures are only offered once both providers are known. The private registry is read defensively: if
        /// it cannot be read, the registration order above is trusted.
        /// </summary>
        private static bool ProvidersRegistered()
        {
            if (HasProviders()) return true;
            TextureProviderFactory.RefreshProviderTypes();
            return HasProviders();
        }

        private static bool HasProviders()
        {
            try
            {
                FieldInfo field = typeof(TextureProviderFactory).GetField("_textureProvidertypes", BindingFlags.Static | BindingFlags.NonPublic);
                if (!(field?.GetValue(null) is IDictionary types)) return true;
                return types.Contains(nameof(CK3PlayerCoaTextureProvider)) && types.Contains(nameof(CK3EnemyCoaTextureProvider));
            }
            catch (Exception e)
            {
                BridgeIo.Log("texture provider lookup failed: " + e.Message);
                return true;
            }
        }
    }

    /// <summary>Gauntlet texture provider of the player's coat of arms (prefab TextureProviderName="@PlayerCoaProvider").</summary>
    public sealed class CK3PlayerCoaTextureProvider : TextureProvider
    {
        protected override Texture OnGetTextureForRender(TwoDimensionContext twoDimensionContext, string name) => CoatOfArmsTextures.Player;
    }

    /// <summary>Gauntlet texture provider of the enemy's coat of arms (prefab TextureProviderName="@EnemyCoaProvider").</summary>
    public sealed class CK3EnemyCoaTextureProvider : TextureProvider
    {
        protected override Texture OnGetTextureForRender(TwoDimensionContext twoDimensionContext, string name) => CoatOfArmsTextures.Enemy;
    }
}
