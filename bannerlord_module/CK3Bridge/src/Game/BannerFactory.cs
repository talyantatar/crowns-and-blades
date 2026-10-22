using System.Collections.Generic;
using CK3xBL.Protocol;
using TaleWorlds.Core;

namespace CK3Bridge
{
    /// <summary>
    /// Side banners. Team colours are the banner's layer-0 colour and first icon colour
    /// (CustomBattleCombatant.PrimaryColorPair -> MissionCombatantsLogic -> every UseTeamColor garment), and heraldic
    /// shields and banner bearers draw the banner itself.
    /// </summary>
    internal static class BannerFactory
    {
        /// <summary>The CK3-derived banner when its code is fully valid, else the culture banner.</summary>
        public static Banner ForSide(SideRequest side, BasicCultureObject culture, string sideLabel)
        {
            string code = side.Banner?.Code;
            if (!string.IsNullOrWhiteSpace(code))
            {
                if (IsUsable(code, out string error)) return new Banner(code.Trim());
                BridgeIo.Log($"{sideLabel}: banner from {side.Banner.Source ?? "?"} rejected ({error}); using the {culture.StringId} banner");
            }
            return new Banner(culture.Banner, culture.Color, culture.Color2);
        }

        /// <summary>A hero's own arms (PROTOCOL.md §25), or null (the hero shows the side's banner) when none was sent or the code is unusable.</summary>
        public static Banner ForHero(HeroSpec spec)
        {
            string code = spec?.Banner?.Code;
            if (string.IsNullOrWhiteSpace(code)) return null;
            if (IsUsable(code, out string error)) return new Banner(code.Trim());
            BridgeIo.Log($"hero {spec.Key}: arms from {spec.Banner.Source ?? "?"} rejected ({error}); side banner kept");
            return null;
        }

        /// <summary>Syntax (BannerCode) plus live ids: backgrounds, icons and palette colours must exist.</summary>
        public static bool IsUsable(string code, out string error)
        {
            if (!BannerCode.TryParse(code, out List<BannerLayerIds> layers, out error)) return false;
            BannerManager manager = BannerManager.Instance;
            for (int i = 0; i < layers.Count; i++)
            {
                BannerLayerIds layer = layers[i];
                bool meshOk = i == 0
                    ? manager.GetBackgroundMeshName(layer.Mesh) != null
                    : manager.GetIconDataFromIconId(layer.Mesh).MaterialName != null;
                if (!meshOk)
                {
                    error = $"layer {i}: unknown {(i == 0 ? "background" : "icon")} id {layer.Mesh}";
                    return false;
                }
                // Colours must be palette ids: GetColor returns a placeholder for others and ChangePrimaryColor
                // silently ignores non-palette colours (BannerManager).
                if (!manager.ReadOnlyColorPalette.ContainsKey(layer.Color) || !manager.ReadOnlyColorPalette.ContainsKey(layer.Color2))
                {
                    error = $"layer {i}: colour ids {layer.Color}/{layer.Color2} are not palette ids";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// When both sides would wear (nearly) the same primary colour, the enemy's pair is swapped, as the custom battle
        /// menu does for same-culture battles (CustomBattleHelper); the same rule applies to CK3 colours.
        /// </summary>
        public static void ResolveClash(Banner playerBanner, Banner enemyBanner)
        {
            uint playerPrimary = playerBanner.GetPrimaryColor();
            uint enemyPrimary = enemyBanner.GetPrimaryColor();
            uint enemyIcon = enemyBanner.GetFirstIconColor();
            if (RequestRules.ColorDistance(playerPrimary, enemyPrimary) >= RequestRules.ClashDistance) return;
            if (enemyBanner.GetBannerDataListCount() < 2)
            {
                BridgeIo.Log("team colours clash and the enemy banner has no icon colour to swap in");
                return;
            }
            enemyBanner.ChangePrimaryColor(enemyIcon);
            enemyBanner.ChangeIconColors(enemyPrimary);
            BridgeIo.Log("team colours clash: enemy banner colours swapped");
        }
    }
}
