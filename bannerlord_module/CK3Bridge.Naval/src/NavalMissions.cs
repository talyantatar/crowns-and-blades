using System;
using System.Collections.Generic;
using System.Linq;
using CK3xBL.Protocol;
using NavalDLC;
using NavalDLC.CustomBattle;
using NavalDLC.Missions;
using NavalDLC.Missions.Deployment;
using NavalDLC.Missions.Handlers;
using NavalDLC.Missions.MissionLogics;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.CustomBattle;
using TaleWorlds.MountAndBlade.MissionSpawnHandlers;
using TaleWorlds.MountAndBlade.Source.Missions;
using TaleWorlds.MountAndBlade.Source.Missions.Handlers.Logic;
using TaleWorlds.ObjectSystem;

namespace CK3Bridge.Naval
{
    /// <summary>
    /// The CK3 naval missions (docs/PROTOCOL.md §44): the behaviours of CustomNavalMissions.OpenNavalBattleForCustomMission and
    /// OpenNavalRaidBattleForCustomMission (War Sails v1.2.8 for Bannerlord v1.4.8, read from their IL) with the CK3 troop suppliers
    /// (spawn order, heroes' own arms), under the mission names "CK3Naval" and "CK3NavalRaid", whose views are the DLC's plus the CK3
    /// presenter (CK3NavalViews).
    /// </summary>
    internal static class NavalMissions
    {
        public const string BattleMissionName = "CK3Naval";
        public const string RaidMissionName = "CK3NavalRaid";
        /// <summary>Ships that can land in a raid: the fourth already indexes past the controller's four landing paths (bench 2026-10-20).</summary>
        public const int RaidMaxShips = 3;

        public static Mission Open(MissionSetup setup, BattleArmies armies)
        {
            NavalInfo naval = setup.Naval ?? new NavalInfo();
            return naval.Raid ? OpenRaid(setup, armies, naval) : OpenBattle(setup, armies, naval);
        }

        private static MBList<IShipOrigin> Ships(List<string> hulls, bool playerShips, string label)
        {
            var list = new MBList<IShipOrigin>();
            foreach (string id in hulls ?? new List<string>())
            {
                ShipHull hull = MBObjectManager.Instance.GetObject<ShipHull>(id ?? "");
                if (hull == null) { BridgeIo.Log($"{label}: unknown ship hull '{id}' skipped"); continue; }
                list.Add(new CustomBattleShip(hull, playerShips));
            }
            return list;
        }

        private static IMissionTroopSupplier[] Suppliers(BattleArmies armies, bool isPlayerGeneral)
        {
            var suppliers = new IMissionTroopSupplier[2];
            CustomBattleCombatant playerParty = armies.PlayerSide.Combatant, enemyParty = armies.EnemySide.Combatant;
            var playerSupplier = new CustomBattleTroopSupplier(playerParty, isPlayerSide: true, isPlayerGeneral, isSallyOut: false);
            var enemySupplier = new CustomBattleTroopSupplier(enemyParty, isPlayerSide: false, isPlayerGeneral: false, isSallyOut: false);
            CK3SpawnOrder.Apply(playerSupplier, armies.PlayerSide, "player side");
            CK3SpawnOrder.Apply(enemySupplier, armies.EnemySide, "enemy side");
            suppliers[(int)playerParty.Side] = CK3HeraldrySupplier.Wrap(playerSupplier, armies.PlayerSide, "player side");
            suppliers[(int)enemyParty.Side] = CK3HeraldrySupplier.Wrap(enemySupplier, armies.EnemySide, "enemy side");
            return suppliers;
        }

        private static TerrainType TerrainOf(NavalInfo naval) => naval.Terrain switch
        {
            "OpenSea" => TerrainType.OpenSea,
            "River" => TerrainType.River,
            _ => TerrainType.CoastalSea,
        };

        private static NavalCustomBattleWindConfig.Direction WindOf(NavalInfo naval) =>
            Enum.TryParse(naval.WindDirection ?? "", true, out NavalCustomBattleWindConfig.Direction d) ? d : NavalCustomBattleWindConfig.Direction.Random;

        private static MissionInitializerRecord Record(MissionSetup setup, NavalInfo naval, TerrainType terrain, string sceneLevels)
        {
            float wind = Math.Max(0f, Math.Min(1f, naval.WindStrength));
            AtmosphereInfo atmosphere = CustomNavalMissions.CreateAtmosphereInfoForMission(setup.Season, setup.PresetHour, wind, new Vec2(0f, 1f), terrain);
            atmosphere.NauticalInfo.UsesNavalSimulatedWater = 1;
            return new MissionInitializerRecord(setup.Scene)
            {
                TerrainType = (int)terrain,
                NeedsRandomTerrain = false,
                PlayingInCampaignMode = true,
                AtmosphereOnCampaign = atmosphere,
                SceneHasMapPatch = false,
                DecalAtlasGroup = 2,
                SceneLevels = sceneLevels,
            };
        }

        private static Mission OpenBattle(MissionSetup setup, BattleArmies armies, NavalInfo naval)
        {
            CustomBattleCombatant playerParty = armies.PlayerSide.Combatant, enemyParty = armies.EnemySide.Combatant;
            BattleSideEnum playerSide = playerParty.Side;
            bool isPlayerAttacker = armies.PlayerIsAttacker, isPlayerGeneral = armies.PlayerIsGeneral, isPlayerSergeant = !isPlayerGeneral;
            MBList<IShipOrigin> playerShips = Ships(isPlayerAttacker ? naval.AttackerShips : naval.DefenderShips, true, "player side");
            MBList<IShipOrigin> enemyShips = Ships(isPlayerAttacker ? naval.DefenderShips : naval.AttackerShips, false, "enemy side");
            IMissionTroopSupplier[] suppliers = Suppliers(armies, isPlayerGeneral);
            TerrainType terrain = TerrainOf(naval);
            NavalCustomBattleWindConfig.Direction wind = WindOf(naval);
            MissionInitializerRecord record = Record(setup, naval, terrain, setup.SceneLevels ?? "");

            ShipDeploymentModelHolder models = ShipDeploymentModelHolder.Current;
            int playerCap = models.MaxTroops(playerShips, true), enemyCap = models.MaxTroops(enemyShips, false);
            int[] maxPerTeam = { playerCap, 0, enemyCap };
            int deployablePlayerShips = Math.Min(playerShips.Count, NavalShipDeploymentLimit.Max().NetDeploymentLimit);
            CustomBattleCombatant attackerParty = isPlayerAttacker ? playerParty : enemyParty;
            CustomBattleCombatant defenderParty = isPlayerAttacker ? enemyParty : playerParty;
            BridgeIo.Log($"opening naval battle '{BattleMissionName}' on {setup.Scene}: {playerShips.Count} player ship(s) for {playerCap} men, " +
                         $"{enemyShips.Count} enemy ship(s) for {enemyCap}, {terrain}, wind {naval.WindStrength:0.##} {wind}");

            Mission mission = NavalMissionState.OpenNew(BattleMissionName, record, m => new MissionBehavior[]
            {
                new NavalShipsLogic(),
                new NavalFloatsamLogic(),
                new NavalAgentsLogic(),
                new DefaultNavalMissionLogic(playerShips, null, enemyShips, NavalShipDeploymentLimit.Max(), NavalShipDeploymentLimit.Invalid(), NavalShipDeploymentLimit.Max()),
                new NavalTrajectoryPlanningLogic(),
                new DefaultNavalMissionAgentSpawnLogic(suppliers, playerSide, deployablePlayerShips, maxPerTeam),
                new NavalMissionDeploymentPlanningLogic(m),
                new BattlePowerCalculationLogic(),
                new CustomBattleAgentLogic(),
                new WaveParametersComputerLogic(),
                new MissionOptionsComponent(),
                new NavalAgentMoraleInteractionLogic(),
                new NavalBattleEndLogic(),
                new NavalBoundaryForceFieldLogic(),
                new NavalMissionCombatantsLogic(new List<IBattleCombatant> { playerParty, enemyParty }, playerParty, defenderParty, attackerParty,
                    Mission.MissionTeamAITypeEnum.NavalBattle, isPlayerSergeant),
                new BattleObserverMissionLogic(),
                new AgentHumanAILogic(),
                new AgentVictoryLogic(),
                new ShipCollisionOutcomeLogic(m),
                new ShipRetreatLogic(),
                new BattleMissionAgentInteractionLogic(),
                new NavalAssignPlayerRoleInTeamMissionController(isPlayerGeneral, isPlayerSergeant, false, null),
                new EquipmentControllerLeaveLogic(),
                new MissionHardBorderPlacer(),
                new MissionBoundaryPlacer(),
                new MissionBoundaryCrossingHandler(30f),
                new HighlightsController(),
                new BattleHighlightsController(),
                new NavalDeploymentMissionController(isPlayerAttacker),
                new NavalDeploymentHandler(isPlayerAttacker),
                new NavalCustomBattleWindAndWaveLogic(wind, terrain),
            }, true, true);
            mission.SetPlayerCanTakeControlOfAnotherAgentWhenDead();
            return mission;
        }

        private static Mission OpenRaid(MissionSetup setup, BattleArmies armies, NavalInfo naval)
        {
            CustomBattleCombatant playerParty = armies.PlayerSide.Combatant, enemyParty = armies.EnemySide.Combatant;
            BattleSideEnum playerSide = playerParty.Side;
            bool isPlayerAttacker = armies.PlayerIsAttacker, isPlayerGeneral = armies.PlayerIsGeneral, isPlayerSergeant = !isPlayerGeneral;
            // A raid takes at most three ships: a fourth indexes past NavalRaidMissionController's landing paths and crashes the
            // deployment (bench 2026-10-20; live b298 crashed with eight).
            var landing = naval.AttackerShips ?? new List<string>();
            if (landing.Count > RaidMaxShips)
            {
                BridgeIo.Log($"landing: {landing.Count} ships asked, {RaidMaxShips} land (the raid scenes take no more)");
                landing = landing.GetRange(0, RaidMaxShips);
            }
            MBList<IShipOrigin> attackerShips = Ships(landing, isPlayerAttacker, "attacker");
            IMissionTroopSupplier[] suppliers = Suppliers(armies, isPlayerGeneral);
            TerrainType terrain = TerrainOf(naval);
            NavalCustomBattleWindConfig.Direction wind = WindOf(naval);
            MissionInitializerRecord record = Record(setup, naval, terrain, "naval_raid");

            int attackerCap = Math.Min(ShipDeploymentModelHolder.Current.MaxTroops(attackerShips, isPlayerAttacker),
                suppliers[(int)BattleSideEnum.Attacker].NumTroopsNotSupplied);
            var attackerLimit = new NavalShipDeploymentLimit(Math.Min(attackerShips.Count, NavalShipDeploymentLimit.Max().NetDeploymentLimit));
            CustomBattleCombatant attackerParty = isPlayerAttacker ? playerParty : enemyParty;
            CustomBattleCombatant defenderParty = isPlayerAttacker ? enemyParty : playerParty;
            int defenderCap = defenderParty.NumberOfHealthyMembers;
            BridgeIo.Log($"opening landing '{RaidMissionName}' on {setup.Scene}: {attackerShips.Count} ship(s) land {attackerCap} men against {defenderCap}");

            Mission mission = NavalMissionState.OpenNew(RaidMissionName, record, m => new MissionBehavior[]
            {
                new NavalShipsLogic(),
                new NavalFloatsamLogic(),
                new NavalAgentsLogic(),
                new NavalRaidMissionController(),
                new NavalRaidMissionAgentSpawnLogic(suppliers, playerSide, attackerShips, attackerLimit, attackerCap, defenderCap),
                new NavalTrajectoryPlanningLogic(),
                new NavalRaidMissionDeploymentPlanningLogic(),
                new BattlePowerCalculationLogic(),
                new CustomBattleAgentLogic(),
                new WaveParametersComputerLogic(),
                new MissionOptionsComponent(),
                new NavalAgentMoraleInteractionLogic(),
                new BattleEndLogic(),
                new NavalBoundaryForceFieldLogic(),
                new NavalMissionCombatantsLogic(new List<IBattleCombatant> { playerParty, enemyParty }, playerParty, defenderParty, attackerParty,
                    Mission.MissionTeamAITypeEnum.NavalRaid, isPlayerSergeant),
                new BattleObserverMissionLogic(),
                new AgentHumanAILogic(),
                new AgentVictoryLogic(),
                new ShipCollisionOutcomeLogic(m),
                new BattleMissionAgentInteractionLogic(),
                new NavalAssignPlayerRoleInTeamMissionController(isPlayerGeneral, isPlayerSergeant, false, null),
                new EquipmentControllerLeaveLogic(),
                new MissionHardBorderPlacer(),
                new MissionBoundaryPlacer(),
                new MissionBoundaryCrossingHandler(30f),
                new HighlightsController(),
                new BattleHighlightsController(),
                new NavalRaidDeploymentMissionController(isPlayerAttacker),
                new NavalRaidDeploymentHandler(isPlayerAttacker),
                new NavalCustomBattleWindAndWaveLogic(wind, terrain),
            }, true, true);
            mission.SetPlayerCanTakeControlOfAnotherAgentWhenDead();
            return mission;
        }
    }

    /// <summary>The DLC's ship deployment model (NavalDLCManager.Instance.GameModels.ShipDeploymentModel), with a log line and a
    /// generous fallback when the models were not registered.</summary>
    internal sealed class ShipDeploymentModelHolder
    {
        public static ShipDeploymentModelHolder Current { get; } = new ShipDeploymentModelHolder();

        public int MaxTroops(MBList<IShipOrigin> ships, bool isPlayerTeam)
        {
            var model = NavalDLCManager.Instance?.GameModels?.ShipDeploymentModel;
            if (model != null) return model.GetMaximumDeployableTroopCountForTeam(ships, isPlayerTeam);
            BridgeIo.Log("naval: no ship deployment model (naval models not registered?); crews from hull capacity");
            return ships.Sum(s => s is CustomBattleShip c ? c.TotalCrewCapacity : 0);
        }
    }
}
