using System;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.GauntletUI.GamepadNavigation;
using TaleWorlds.MountAndBlade.GauntletUI;
using TaleWorlds.ScreenSystem;

namespace CK3Bridge
{
    /// <summary>
    /// Replaces Bannerlord's loading screen (GauntletDefaultLoadingWindowManager: random art, logo) with the CK3 card:
    /// plain black outside a battle (boot, return to idle) and the battle's title card while a battle loads or is held.
    /// Mirrors the stock manager (GauntletDefaultLoadingWindowManager): layer
    /// order 115003 above every other layer except the gamepad cursor and tooltips, focus layer with a hidden mouse while
    /// enabled, and the engine's 720 s loading-stuck watchdog. The card text follows <see cref="BattleSession"/> every
    /// late tick, so it is right whenever the engine enables the window (MissionState.OnInitialize, MissionScreen.OnFinalize,
    /// GameLoadingScreen).
    /// </summary>
    public sealed class CK3LoadingWindowManager : GlobalLayer, ILoadingWindowManager
    {
        /// <summary>Another mod replacing the manager every frame must not turn this into a reload loop.</summary>
        private const int MaxInstalls = 8;

        private static int _installs;

        private GauntletLayer _layer;
        private GauntletMovieIdentifier _movie;
        private CK3CardVM _dataSource;
        private bool _enabled;

        /// <summary>
        /// Installs the manager unless it is already the active one. LoadingWindow.InitializeWith destroys the previous
        /// manager and enables the new one if the window is up; GauntletUISubModule only
        /// installs its default while the slot is empty (GauntletUISubModule.OnApplicationTick). Called from
        /// OnBeforeInitialModuleScreenSetAsRoot (before Gauntlet's first tick) and every application tick (cheap type test).
        /// </summary>
        public static void EnsureInstalled()
        {
            if (LoadingWindow.LoadingWindowManager is CK3LoadingWindowManager || _installs >= MaxInstalls) return;
            _installs++;
            string replaced = LoadingWindow.LoadingWindowManager?.GetType().Name ?? "none";
            try
            {
                LoadingWindow.InitializeWith<CK3LoadingWindowManager>();
            }
            catch (Exception e)
            {
                // InitializeWith has already destroyed the previous manager and kept our empty one, which would leave the
                // game without any loading screen: the stock manager is put back and no retry follows.
                _installs = MaxInstalls;
                BridgeIo.Log("CK3 loading card failed, restoring the stock loading screen: " + e);
                LoadingWindow.InitializeWith<GauntletDefaultLoadingWindowManager>();
                return;
            }
            BridgeIo.Log(_installs < MaxInstalls
                ? $"CK3 loading card installed (replacing {replaced})"
                : $"CK3 loading card installed (replacing {replaced}); it was replaced {MaxInstalls} times, no further re-installs");
        }

        void ILoadingWindowManager.Initialize()
        {
            var dataSource = new CK3CardVM();
            var layer = new GauntletLayer("CK3LoadingWindow", 115003, false);
            // LoadMovie throws if the prefab is missing; nothing is registered before it succeeds, so a failed install
            // leaves a manager whose Enable/Disable/Destroy are no-ops (see EnsureInstalled).
            _movie = layer.LoadMovie("CK3LoadingWindow", dataSource);
            _dataSource = dataSource;
            _layer = layer;
            Layer = layer;
            ScreenManager.AddGlobalLayer(this, isFocusable: false);
        }

        void ILoadingWindowManager.Destroy()
        {
            if (_layer == null) return;
            _dataSource.OnFinalize();
            _layer.ReleaseMovie(_movie);
            ScreenManager.RemoveGlobalLayer(this);
            _layer = null;
            _dataSource = null;
        }

        void ILoadingWindowManager.EnableLoadingWindow()
        {
            if (_layer == null) return;
            _enabled = true;
            RefreshCard();
            _dataSource.IsActive = true;
            Layer.IsFocusLayer = true;
            ScreenManager.TrySetFocus(Layer);
            Layer.InputRestrictions.SetInputRestrictions(isMouseVisible: false);
            Utilities.StartLoadingStuckCheckState(720f);
        }

        void ILoadingWindowManager.DisableLoadingWindow()
        {
            if (_layer == null) return;
            _enabled = false;
            _dataSource.IsActive = false;
            Layer.IsFocusLayer = false;
            ScreenManager.TryLoseFocus(Layer);
            Layer.InputRestrictions.ResetInputRestrictions();
            GauntletGamepadNavigationManager.Instance?.SetAllDirty();
            Utilities.EndLoadingStuckCheckState();
        }

        void ILoadingWindowManager.SetCurrentModeIsMultiplayer(bool isMultiplayer)
        {
            // Single player only; the card has no multiplayer variant.
        }

        protected override void OnLateTick(float dt)
        {
            base.OnLateTick(dt);
            if (!_enabled) return;
            try
            {
                RefreshCard();
            }
            catch (Exception e)
            {
                BridgeIo.Log("loading card refresh failed: " + e.Message);
                _enabled = false;   // keep showing the last card rather than failing every frame
            }
        }

        /// <summary>
        /// The battle's card while it loads or is held, black otherwise (also while a cancel is pending, and when the bridge
        /// shows the title itself, BattleSession.InEngineTitle). Followed every late tick, so a "title_in_engine" that arrives
        /// while the battle is held brings the title onto the card at once.
        /// </summary>
        private void RefreshCard()
        {
            BattleSession session = BattleSession.Current;
            bool showBattle = session != null && session.Phase <= BattlePhase.Held && !session.IsStopping;
            _dataSource.SetCard(showBattle ? session.TitleCard : null);
        }
    }
}
