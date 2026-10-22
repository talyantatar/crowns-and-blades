using System;
using System.IO;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.CustomBattle;

namespace CK3Bridge
{
    /// <summary>
    /// Module entry point. Thin: every hook forwards to <see cref="BridgeController"/> and never lets an exception escape
    /// into the engine. Hook order (Bannerlord v1.4.8 Module / Mission):
    /// OnSubModuleLoad -> OnBeforeInitialModuleScreenSetAsRoot (before the logo check) -> OnApplicationTick (every
    /// frame) -> OnGameStart (each custom game start, before its mission models are built) -> OnBeforeMissionBehaviorInitialize
    /// (Mission.AfterStart) -> OnGameEnd (before BannerlordConfig.Save at exit) -> OnSubModuleUnloaded (after the Save).
    /// Every hook does nothing unless the Crowns & Blades bridge started this game (<see cref="HostGate"/>): a player who ticks
    /// the module in the Bannerlord launcher plays their own game as if it were not there.
    /// </summary>
    public sealed class CK3BridgeSubModule : MBSubModuleBase
    {
        private readonly BridgeController _controller = new BridgeController();

        /// <summary>Set by CK3Bridge.Naval in InitializeGameStarter (before OnGameStart) when it registered the naval models with the CK3
        /// effects: this module then leaves the stat and damage models to it.</summary>
        internal static bool NavalModelsRegistered;
        private string _lastTickError;
        private bool _active;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            Guard("load", () =>
            {
                HostDecision host = HostGate.Evaluate();
                _active = host.Active;
                LogVersions();
                if (!_active)
                {
                    BridgeIo.Log($"CK3Bridge loaded without the bridge ({host.Reason}): inactive, the game runs untouched");
                    return;
                }
                ExceptionTrace.Install();
                BridgeIo.Log("CK3Bridge active: " + host.Reason);
                _controller.OnLoad();
            });
        }

        /// <summary>
        /// Skips the TaleWorlds logo video: Module.SetInitialModuleScreenAsRoot checks the private
        /// _splashScreenPlayed right after calling this hook. There is no command-line flag.
        /// </summary>
        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();
            if (!_active) return;
            Guard("splash skip", () =>
            {
                FieldInfo field = typeof(TaleWorlds.MountAndBlade.Module).GetField("_splashScreenPlayed", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null) field.SetValue(TaleWorlds.MountAndBlade.Module.CurrentModule, true);
                else BridgeIo.Log("Module._splashScreenPlayed not found; the intro video is skipped when it starts");
            });
            // Native's GauntletUI hook (earlier in module order) has set up the UI resources and global layers; installing
            // the loading card now means Gauntlet never installs its default one (it only fills an empty slot).
            _controller.InstallLoadingCard();
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);
            if (!_active) return;
            try
            {
                _controller.Tick();
                _lastTickError = null;
            }
            catch (Exception e)
            {
                // Log a repeating failure once, not every frame.
                if (e.Message != _lastTickError) BridgeIo.Log("tick failed: " + e);
                _lastTickError = e.Message;
            }
        }

        /// <summary>
        /// Registers the CK3 combat-buff models (PROTOCOL.md §13.4). CustomGame.OnInitialize adds the stock models, then
        /// MBGameManager.OnGameStart calls every submodule here before it builds MissionGameModels, and
        /// GameModelsManager.GetGameModel scans from the end, so these subclasses replace the stock ones. The stock models are
        /// created anew on every custom game start (also after Exit to Main Menu), hence every time. Outside a CK3 battle they
        /// return the stock values.
        /// </summary>
        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);
            if (!_active) return;
            Guard("model registration", () =>
            {
                if (!(game.GameType is CustomGame)) return;
                // With War Sails, CK3Bridge.Naval registers the same effects on the naval stat and damage models (PROTOCOL.md §44).
                if (!NavalModelsRegistered)
                {
                    gameStarterObject.AddModel(new CK3AgentStatCalculateModel());
                    gameStarterObject.AddModel(new CK3AgentApplyDamageModel());
                }
                gameStarterObject.AddModel(new CK3BattleMoraleModel());
                BridgeIo.Log("CK3 combat models registered" + (NavalModelsRegistered ? " (stats and damage on the naval models)" : ""));
            });
        }

        public override void OnBeforeMissionBehaviorInitialize(Mission mission)
        {
            base.OnBeforeMissionBehaviorInitialize(mission);
            if (!_active) return;
            Guard("mission attach", () => _controller.OnBeforeMissionBehaviorInitialize(mission));
        }

        public override void OnGameEnd(Game game)
        {
            if (_active) Guard("game end", _controller.OnGameEnd);
            base.OnGameEnd(game);
        }

        protected override void OnSubModuleUnloaded()
        {
            if (_active) Guard("unload", _controller.OnUnloaded);
            base.OnSubModuleUnloaded();
        }

        /// <summary>Logs this module's version and the game's, with a warning when the game is not the version it was built for (the
        /// bridge decides what to tell the player; reflection hooks fail soft with their own log lines).</summary>
        private static void LogVersions()
        {
            Assembly own = typeof(CK3BridgeSubModule).Assembly;
            string module = own.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? own.GetName().Version.ToString();
            string bin = Path.GetDirectoryName(typeof(MBSubModuleBase).Assembly.Location) ?? "";
            string game = GameVersionInfo.ReadGameVersion(Path.Combine(bin, "Version.xml"));
            BridgeIo.Log($"CK3Bridge {module.Split('+')[0]} in Bannerlord {game ?? "(version unknown)"}");
            if (!GameVersionInfo.IsTarget(game))
                BridgeIo.Log($"warning: this module was built for Bannerlord {GameVersionInfo.Target}; {game} is untested");
        }

        private static void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                BridgeIo.Log($"{what} failed: {e}");
            }
        }
    }
}
