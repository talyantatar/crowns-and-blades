using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade.CustomBattle;
using TaleWorlds.MountAndBlade.View.Screens;
using TaleWorlds.ScreenSystem;

namespace CK3Bridge
{
    /// <summary>
    /// CustomGameManager with our idle state instead of the custom battle menu. DoLoadingForGameManager (CustomGame,
    /// models, troops, scenes) stays stock; only OnLoadFinished differs from CustomGameManager.OnLoadFinished,
    /// which would push CustomBattleState with its menu UI and menu music.
    /// </summary>
    public sealed class CK3GameManager : CustomGameManager
    {
        public override void OnLoadFinished()
        {
            IsLoaded = true;   // what MBGameManager.OnLoadFinished does
            Game.Current.GameStateManager.CleanAndPushState(Game.Current.GameStateManager.CreateState<CK3IdleState>());
        }
    }

    /// <summary>
    /// Between battles Bannerlord sits here: no menu, no music (MBMusicManager.Update leaves menu mode when the active
    /// state is not IsMusicMenuState), a black screen. Battles are opened from this state,
    /// so MissionState pops straight back to it (MissionState.OnTick).
    /// </summary>
    public sealed class CK3IdleState : GameState
    {
        public override bool IsMenuState => true;

        public override bool IsMusicMenuState => false;
    }

    /// <summary>View model of the idle screen. Stage 1 shows plain black; stage 2 adds presentation fields here.</summary>
    public sealed class CK3IdleVM : ViewModel
    {
    }

    /// <summary>
    /// Black full-screen layer (GUI/Prefabs/CK3IdleScreen.xml). Like CustomBattleScreen (OnActivate/OnFrameTick) it
    /// drops the global loading window on its first frames, which otherwise stays up after GameLoadingState or after a
    /// mission that was cancelled while its loading card was held.
    /// </summary>
    [GameStateScreen(typeof(CK3IdleState))]
    public sealed class CK3IdleScreen : ScreenBase, IGameStateListener
    {
        private GauntletLayer _layer;
        private CK3IdleVM _dataSource;
        private int _framesUntilLoadingWindowOff;

        /// <summary>GameStateScreenManager creates screens with Activator.CreateInstance(type, state).</summary>
        public CK3IdleScreen(CK3IdleState state)
        {
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();
            _dataSource = new CK3IdleVM();
            _layer = new GauntletLayer("CK3Idle", 1, shouldClear: true);
            _layer.LoadMovie("CK3IdleScreen", _dataSource);
            AddLayer(_layer);
        }

        protected override void OnActivate()
        {
            base.OnActivate();
            _framesUntilLoadingWindowOff = 2;
        }

        protected override void OnFrameTick(float dt)
        {
            base.OnFrameTick(dt);
            if (_framesUntilLoadingWindowOff < 0) return;
            if (_framesUntilLoadingWindowOff == 0 && LoadingWindow.IsLoadingWindowActive) LoadingWindow.DisableGlobalLoadingWindow();
            _framesUntilLoadingWindowOff--;
        }

        protected override void OnFinalize()
        {
            if (_layer != null) RemoveLayer(_layer);
            _layer = null;
            _dataSource?.OnFinalize();
            _dataSource = null;
            base.OnFinalize();
        }

        void IGameStateListener.OnActivate()
        {
        }

        void IGameStateListener.OnDeactivate()
        {
        }

        void IGameStateListener.OnInitialize()
        {
        }

        void IGameStateListener.OnFinalize()
        {
        }
    }
}
