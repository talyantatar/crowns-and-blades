using System;
using NavalDLC.ComponentInterfaces;
using NavalDLC.GameComponents;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.CustomBattle;
using TaleWorlds.ObjectSystem;

namespace CK3Bridge.Naval
{
    /// <summary>
    /// War Sails in the bridge's custom game (docs/PROTOCOL.md §44). The DLC's own custom battle is a game type of its own
    /// (NavalCustomGame) with ship models and ship data; the bridge's game is the stock CustomGame, so this module gives it what a
    /// naval mission needs:
    ///  - the naval game models (ship parameters, physics, deployment, the naval custom battle initialization), and the CK3 combat
    ///    effects on the naval agent-stat and damage models, in InitializeGameStarter, which runs before NavalDLC's OnGameStart reads
    ///    the starter's models into NavalDLCManager.GameModels (CK3Bridge then leaves its own stat and damage models out);
    ///  - the ship types (as NavalCustomGame.OnRegisterTypes) and their XML (as NavalDLCSubModule.RegisterSubModuleObjects) once the
    ///    custom game is initialized;
    ///  - the CK3Naval missions, through CK3Missions.NavalOpener.
    /// Inactive (like CK3Bridge) unless the bridge started the game.
    /// </summary>
    public sealed class NavalSubModule : MBSubModuleBase
    {
        private bool _active;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            Guard("load", () =>
            {
                _active = HostGate.Evaluate().Active;
                if (!_active) return;
                CK3Missions.NavalOpener = NavalMissions.Open;
                BridgeIo.Log("CK3Bridge.Naval active: battles at sea and landings");
            });
        }

        protected override void InitializeGameStarter(Game game, IGameStarter starterObject)
        {
            base.InitializeGameStarter(game, starterObject);
            if (!_active || !(game.GameType is CustomGame)) return;
            Guard("naval models", () =>
            {
                starterObject.AddModel(new NavalDLCCampaignShipParametersModel());
                starterObject.AddModel(new NavalDLCShipPhysicsParametersModel());
                starterObject.AddModel(new NavalDLCClanShipOwnershipModel());
                starterObject.AddModel(new NavalDLCShipDistributionModel());
                starterObject.AddModel(new NavalDLCShipDeploymentModel());
                starterObject.AddModel(new NavalCustomBattleMissionShipParametersModel());
                starterObject.AddModel(new CK3NavalAgentStatCalculateModel());
                starterObject.AddModel(new CK3NavalAgentApplyDamageModel());
                starterObject.AddModel(new CK3NavalBattleInitializationModel());
                CK3BridgeSubModule.NavalModelsRegistered = true;
                BridgeIo.Log("naval models registered (CK3 effects on the naval stat and damage models)");
            });
        }

        public override void OnGameInitializationFinished(Game game)
        {
            base.OnGameInitializationFinished(game);
            if (!_active || !(game.GameType is CustomGame)) return;
            Guard("ship data", () =>
            {
                MBObjectManager om = MBObjectManager.Instance;
                om.RegisterType<ShipUpgradePiece>("ShipUpgradePiece", "ShipUpgradePieces", 60u, true, false);
                om.RegisterType<ShipSlot>("ShipSlot", "ShipSlots", 59u, true, false);
                om.RegisterType<ShipHull>("ShipHull", "ShipHulls", 58u, true, false);
                om.RegisterType<ShipPhysicsReference>("ShipPhysicsReference", "ShipPhysicsReferences", 64u, true, false);
                om.RegisterType<MissionShipObject>("MissionShip", "MissionShips", 57u, true, false);
                foreach (string xml in new[] { "ShipUpgradePieces", "ShipSlots", "ShipHulls", "ShipPhysicsReferences", "MissionShips" })
                    MBObjectManagerExtensions.LoadXML(om, xml, false);
                BridgeIo.Log($"ship data loaded: {om.GetObjectTypeList<ShipHull>().Count} hulls");
            });
        }

        private static void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                BridgeIo.Log($"CK3Bridge.Naval {what} failed: {e}");
            }
        }
    }
}
