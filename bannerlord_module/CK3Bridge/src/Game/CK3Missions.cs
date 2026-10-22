using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CK3xBL.Protocol;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.ModuleManager;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.CustomBattle;
using TaleWorlds.MountAndBlade.MissionSpawnHandlers;
using TaleWorlds.MountAndBlade.Missions.Handlers;
using TaleWorlds.MountAndBlade.Missions.MissionLogics;
using TaleWorlds.MountAndBlade.Source.Missions;
using TaleWorlds.MountAndBlade.Source.Missions.Handlers.Logic;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.ObjectSystem;

namespace CK3Bridge
{
    /// <summary>Normalised opener inputs (see RequestRules / RequestExtras for the rules).</summary>
    internal sealed class MissionSetup
    {
        public string Scene;
        public string Season;
        public int PresetHour;
        public string SceneLevels;
        public int BattleSizeIndex;
        public RequestExtras Extras;
        public bool Arena;                // a noble single combat in an arena (PROTOCOL.md §27)
        public bool Hideout;              // a skirmish at a band's camp, a SandBox hideout scene (PROTOCOL.md §37)
        public SiegeInfo Siege;           // an assault on a besieged holding (PROTOCOL.md §39); null = no siege
        public NavalInfo Naval;           // a battle at sea or a landing (PROTOCOL.md §44, CK3Bridge.Naval); null = on land
        public int Tourney;               // a tournament bout (PROTOCOL.md §29, §36): 1 single combat, 2 wrestling, 3 grand melee, 4 joust, 5 archery
        public int TourneyPar;            // §36 archery: targets to hit
        public string ArcheryHitFormat;   // §36 archery: the hit message in the request's language
    }

    /// <summary>
    /// Opens the battle with our own field opener: the 26 behaviours of BannerlordMissions.OpenCustomBattleMission
    /// (Bannerlord v1.4.8) with four changes: mission name "CK3Battle"; both sides' general names
    /// for the captains logic (stock passes only the player side's); that logic is <see cref="CK3CaptainsAssignmentLogic"/>
    /// (heroes[].captainOf); and an atmosphere that may use a weather preset. Sieges stay in CK3 (PROTOCOL.md decision), so no
    /// siege mission is ever opened.
    /// CK3 logics (tracker, state decider, face guard) are attached in OnBeforeMissionBehaviorInitialize.
    /// </summary>
    internal static class CK3Missions
    {
        public const string OwnFieldMissionName = "CK3Battle";
        public const string StockFieldMissionName = "CustomBattle";

        /// <summary>
        /// THE switch: true opens field battles as "CK3Battle", whose views come from <see cref="CK3BattleViews"/>
        /// (stock views plus the CK3 presenter). If our view creator is somehow not registered, "CustomBattle"
        /// is used so the battle still gets the stock UI (a mission without views crashes in BattleObserverMissionLogic).
        /// </summary>
        private static readonly bool UseOwnFieldMissionName = true;

        public static string FieldMissionName =>
            UseOwnFieldMissionName && IsViewCreatorRegistered(OwnFieldMissionName) ? OwnFieldMissionName : StockFieldMissionName;

        public static Mission Open(MissionSetup setup, BattleArmies armies)
        {
            Game.Current.PlayerTroop = armies.PlayerCharacter;   // the agent built for this character gets the Player controller
            if (setup.Arena || setup.Hideout) return OpenArena(setup, armies);
            if (setup.Siege != null) return RuntimeSettings.WithBattleSize(setup.BattleSizeIndex, () => OpenSiege(setup, armies));
            if (setup.Naval != null && NavalOpener != null) return RuntimeSettings.WithBattleSize(setup.BattleSizeIndex, () => NavalOpener(setup, armies));
            var mission = RuntimeSettings.WithBattleSize(setup.BattleSizeIndex, () => OpenField(setup, armies));
            // A field battle may ask for more than Bannerlord's largest option (up to RequestRules.MaxFieldBattleSize).
            int asked = setup.Extras?.BattleSize ?? 0;
            if (asked > RequestRules.FieldBattleSizes[RequestRules.FieldBattleSizes.Length - 1])
                RuntimeSettings.RaiseFieldBattleSize(mission, RequestRules.FieldTroopsFor(asked, MountedShare(armies)));
            return mission;
        }

        /// <summary>The share of the requested troops that ride (a horse, camel or elephant is an agent of its own).</summary>
        private static double MountedShare(BattleArmies armies)
        {
            int all = 0, mounted = 0;
            foreach (var side in new[] { armies.Attacker, armies.Defender })
            {
                foreach (var t in side?.Request?.Troops ?? new List<TroopRequest>())
                {
                    int n = Math.Max(0, t.Count);
                    all += n;
                    bool rides = !string.IsNullOrEmpty(t.Mount)
                        || (MBObjectManager.Instance.GetObject<BasicCharacterObject>(t.Id ?? "")?.IsMounted ?? false);
                    if (rides) mounted += n;
                }
            }
            return all > 0 ? mounted / (double)all : 0;
        }

        public const string ArenaMissionName = "CK3Arena";

        /// <summary>
        /// A noble single combat in an arena (PROTOCOL.md §27): the behaviours of BannerlordMissions.OpenCustomBattleLordsHallMission
        /// (Bannerlord v1.4.8) with <see cref="CK3ArenaDuelController"/> as the spawner, no team AI (the duelists fight as in a
        /// tournament), and the CK3 suppliers (heroes' own arms). The views are the lords hall's plus the CK3 presenter
        /// (<see cref="CK3BattleViews"/>).
        /// </summary>
        private static Mission OpenArena(MissionSetup setup, BattleArmies armies)
        {
            CustomBattleCombatant playerParty = armies.PlayerSide.Combatant;
            CustomBattleCombatant enemyParty = armies.EnemySide.Combatant;
            BattleSideEnum playerSide = playerParty.Side;
            CustomBattleCombatant attackerParty = armies.PlayerIsAttacker ? playerParty : enemyParty;
            CustomBattleCombatant defenderParty = armies.PlayerIsAttacker ? enemyParty : playerParty;

            var troopSuppliers = new IMissionTroopSupplier[2];
            troopSuppliers[(int)playerParty.Side] = CK3HeraldrySupplier.Wrap(
                new CustomBattleTroopSupplier(playerParty, isPlayerSide: true, isPlayerGeneral: true, isSallyOut: false), armies.PlayerSide, "player side");
            troopSuppliers[(int)enemyParty.Side] = CK3HeraldrySupplier.Wrap(
                new CustomBattleTroopSupplier(enemyParty, isPlayerSide: false, isPlayerGeneral: false, isSallyOut: false), armies.EnemySide, "enemy side");

            var record = new MissionInitializerRecord(setup.Scene)
            {
                DoNotUseLoadingScreen = false,
                PlayingInCampaignMode = false,
                AtmosphereOnCampaign = Atmosphere(setup),
                SceneLevels = setup.SceneLevels,
                DecalAtlasGroup = 2,
            };
            BridgeIo.Log($"opening {(setup.Hideout ? "hideout" : "arena")} mission '{ArenaMissionName}' on {setup.Scene} ({setup.Season}, {setup.PresetHour}h)");
            return MissionState.OpenNew(ArenaMissionName, record, _ =>
            {
                // The spawner: a band's camp (PROTOCOL.md §37), the archery contest or a bout (§36), a single combat (§27).
                MissionLogic spawner = setup.Hideout
                    ? new CK3HideoutController(troopSuppliers, playerSide, new List<BasicCharacterObject>(armies.HeroByCharacter.Keys))
                    : setup.Tourney == 5
                        ? (MissionLogic)new CK3ArenaArcheryController(troopSuppliers, playerSide, setup.TourneyPar, setup.ArcheryHitFormat)
                        : new CK3ArenaDuelController(troopSuppliers, playerSide, bareHands: setup.Tourney == 2, mounted: setup.Tourney == 4);
                var behaviors = new List<MissionBehavior>
                {
                    new MissionOptionsComponent(),
                    new BattleEndLogic(),
                    new MissionCombatantsLogic(null, playerParty, defenderParty, attackerParty, Mission.MissionTeamAITypeEnum.NoTeamAI, false),
                    new AgentHumanAILogic(),
                    spawner,
                };
                if (!setup.Hideout) behaviors.Add(new CK3ArenaAudience(ArenaCulture(setup.Scene)));
                behaviors.AddRange(new MissionBehavior[]
                {
                    new BattleObserverMissionLogic(),
                    new CustomBattleAgentLogic(),
                    new AgentVictoryLogic(),
                    new EquipmentControllerLeaveLogic(),
                    new MissionHardBorderPlacer(),
                    new MissionBoundaryPlacer(),
                    new MissionBoundaryCrossingHandler(),
                    new BattleMissionAgentInteractionLogic(),
                    new HighlightsController(),
                    new BattleHighlightsController(),
                });
                return behaviors.ToArray();
            });
        }

        public const string SiegeMissionName = "CK3Siege";

        /// <summary>PROTOCOL.md §44: opens a battle at sea or a landing; set by CK3Bridge.Naval when War Sails is loaded, else null (naval
        /// requests are then fought in the field).</summary>
        internal static Func<MissionSetup, BattleArmies, Mission> NavalOpener;

        /// <summary>
        /// An assault on a besieged holding (PROTOCOL.md §39): the behaviours of BannerlordMissions.OpenSiegeMissionWithDeployment
        /// (Bannerlord v1.4.8, read from its IL) with the CK3 suppliers (spawn order, heroes' own arms) and the CK3 captains logic,
        /// under the mission name "CK3Siege", whose views are the stock custom siege views plus the CK3 presenter.
        /// </summary>
        private static Mission OpenSiege(MissionSetup setup, BattleArmies armies)
        {
            SiegeInfo siege = setup.Siege ?? new SiegeInfo();
            CustomBattleCombatant playerParty = armies.PlayerSide.Combatant;
            CustomBattleCombatant enemyParty = armies.EnemySide.Combatant;
            BattleSideEnum playerSide = playerParty.Side;
            bool isPlayerAttacker = armies.PlayerIsAttacker;
            bool isPlayerGeneral = armies.PlayerIsGeneral;
            bool isPlayerSergeant = !isPlayerGeneral;
            bool sallyOut = siege.SallyOut, relief = siege.ReliefForceAttack;
            CustomBattleCombatant attackerParty = isPlayerAttacker ? playerParty : enemyParty;
            CustomBattleCombatant defenderParty = isPlayerAttacker ? enemyParty : playerParty;

            var troopSuppliers = new IMissionTroopSupplier[2];
            var playerSupplier = new CustomBattleTroopSupplier(playerParty, isPlayerSide: true, isPlayerGeneral, isSallyOut: sallyOut);
            var enemySupplier = new CustomBattleTroopSupplier(enemyParty, isPlayerSide: false, isPlayerGeneral: false, isSallyOut: sallyOut);
            CK3SpawnOrder.Apply(playerSupplier, armies.PlayerSide, "player side");
            CK3SpawnOrder.Apply(enemySupplier, armies.EnemySide, "enemy side");
            troopSuppliers[(int)playerParty.Side] = CK3HeraldrySupplier.Wrap(playerSupplier, armies.PlayerSide, "player side");
            troopSuppliers[(int)enemyParty.Side] = CK3HeraldrySupplier.Wrap(enemySupplier, armies.EnemySide, "enemy side");

            float[] walls = siege.WallHitpointPercentages != null && siege.WallHitpointPercentages.Length > 0
                ? siege.WallHitpointPercentages : new[] { 1f, 1f };
            List<MissionSiegeWeapon> attackerWeapons = SiegeWeapons(siege.AttackerMachines);
            List<MissionSiegeWeapon> defenderWeapons = SiegeWeapons(siege.DefenderMachines);
            int level = Math.Max(1, Math.Min(3, siege.SceneUpgradeLevel));
            var record = new MissionInitializerRecord(setup.Scene)
            {
                DoNotUseLoadingScreen = false,
                PlayingInCampaignMode = false,
                AtmosphereOnCampaign = Atmosphere(setup),
                SceneLevels = $"level_{level} siege",
                DecalAtlasGroup = 2,
            };
            BridgeIo.Log($"opening siege mission '{SiegeMissionName}' on {setup.Scene} (level {level}, walls {string.Join("/", walls)}, " +
                         $"attackers' engines {string.Join(", ", siege.AttackerMachines ?? new List<string>())}, defenders' {string.Join(", ", siege.DefenderMachines ?? new List<string>())}" +
                         $"{(sallyOut ? ", sally out" : "")}{(relief ? ", relief force attack" : "")}, progress {siege.Progress:0.00})");
            Mission mission = MissionState.OpenNew(SiegeMissionName, record, _ =>
            {
                var behaviors = new List<MissionBehavior>
                {
                    new BattleSpawnLogic(sallyOut ? "sally_out_set" : relief ? "relief_force_attack_set" : "battle_set"),
                    new MissionOptionsComponent(),
                    new BattleEndLogic(),
                    new BattleReinforcementsSpawnController(),
                    new MissionCombatantsLogic(null, playerParty, defenderParty, attackerParty,
                        sallyOut ? Mission.MissionTeamAITypeEnum.SallyOut : Mission.MissionTeamAITypeEnum.Siege, isPlayerSergeant),
                    new SiegeMissionPreparationHandler(sallyOut, relief, walls, siege.HasSiegeTower),
                    new DefaultBattleMissionAgentSpawnLogic(troopSuppliers, playerSide, sallyOut ? Mission.BattleSizeType.SallyOut : Mission.BattleSizeType.Siege),
                    new BattlePowerCalculationLogic(),
                };
                if (sallyOut || relief) behaviors.Add(new CustomSallyOutMissionController(defenderParty, attackerParty));
                else if (siege.AttackerWaveRatio > 0) behaviors.Add(new CK3SiegeSpawnHandler(defenderParty, attackerParty, siege.AttackerWaveRatio));
                else behaviors.Add(new CustomSiegeMissionSpawnHandler(defenderParty, attackerParty, false));
                behaviors.AddRange(new MissionBehavior[]
                {
                    new BattleObserverMissionLogic(),
                    new CustomBattleAgentLogic(),
                    new BannerBearerLogic(),
                    new AgentHumanAILogic(),
                });
                if (!sallyOut) behaviors.Add(new AmmoSupplyLogic(new List<BattleSideEnum> { BattleSideEnum.Defender }));
                behaviors.AddRange(new MissionBehavior[]
                {
                    new AgentVictoryLogic(),
                    new AssignPlayerRoleInTeamMissionController(isPlayerGeneral, isPlayerSergeant, isPlayerInArmy: false,
                        isPlayerSergeant ? new List<string> { armies.PlayerCharacter.StringId } : new List<string>()),
                    new CK3CaptainsAssignmentLogic(armies.Attacker.General.GetName(), armies.Defender.General.GetName()),
                    new MissionAgentPanicHandler(),
                    new MissionBoundaryPlacer(),
                    new MissionBoundaryCrossingHandler(10f),
                    new AgentMoraleInteractionLogic(),
                    new HighlightsController(),
                    new BattleHighlightsController(),
                    new EquipmentControllerLeaveLogic(),
                    new MissionSiegeEnginesLogic(sallyOut ? new List<MissionSiegeWeapon>() : defenderWeapons, attackerWeapons),
                    new SiegeDeploymentHandler(isPlayerAttacker),
                    new SiegeDeploymentMissionController(isPlayerAttacker),
                    new MissionObjectiveLogic(),
                });
                return behaviors.ToArray();
            });
            mission.SetPlayerCanTakeControlOfAnotherAgentWhenDead();
            return mission;
        }

        /// <summary>Bannerlord siege engines by DefaultSiegeEngineTypes property name (SiegeTower, Ram, Onager, Catapult, Trebuchet,
        /// Ballista, FireOnager, FireTrebuchet, ...); unknown names are logged and left out.</summary>
        private static List<MissionSiegeWeapon> SiegeWeapons(List<string> names)
        {
            var list = new List<MissionSiegeWeapon>();
            if (names == null) return list;
            foreach (string name in names)
            {
                PropertyInfo p = typeof(DefaultSiegeEngineTypes).GetProperty(name ?? "", BindingFlags.Public | BindingFlags.Static);
                if (p?.GetValue(null) is SiegeEngineType type) list.Add(MissionSiegeWeapon.CreateDefaultWeapon(type));
                else BridgeIo.Log($"siege engine '{name}' unknown; left out");
            }
            return list;
        }

        /// <summary>A siege map of the custom battle list (CustomGame.CustomBattleScenes) that exists.</summary>
        public static bool IsSiegeScene(string scene)
        {
            if (string.IsNullOrWhiteSpace(scene) || !Utilities.TryGetFullFilePathOfScene(scene, out _)) return false;
            CustomGame game = CustomGame.Current;
            if (game == null) return false;
            foreach (CustomBattleSceneData data in game.CustomBattleScenes)
                if (data.SceneID == scene) return data.IsSiegeMap;
            return false;
        }

        /// <summary>The siege mission has its views (CK3BattleViews registered "CK3Siege").</summary>
        public static bool IsSiegeMissionReady() => IsViewCreatorRegistered(SiegeMissionName);

        private static Mission OpenField(MissionSetup setup, BattleArmies armies)
        {
            CustomBattleCombatant playerParty = armies.PlayerSide.Combatant;
            CustomBattleCombatant enemyParty = armies.EnemySide.Combatant;
            BattleSideEnum playerSide = playerParty.Side;
            bool isPlayerAttacker = armies.PlayerIsAttacker;
            bool isPlayerGeneral = armies.PlayerIsGeneral;
            bool isPlayerSergeant = !isPlayerGeneral;
            BasicCharacterObject playerCharacter = armies.PlayerCharacter;
            CustomBattleCombatant attackerParty = isPlayerAttacker ? playerParty : enemyParty;
            CustomBattleCombatant defenderParty = isPlayerAttacker ? enemyParty : playerParty;

            var troopSuppliers = new IMissionTroopSupplier[2];
            var playerSupplier = new CustomBattleTroopSupplier(playerParty, isPlayerSide: true, isPlayerGeneral, isSallyOut: false);
            var enemySupplier = new CustomBattleTroopSupplier(enemyParty, isPlayerSide: false, isPlayerGeneral: false, isSallyOut: false);
            // Professionals first, levies last (PROTOCOL.md §3.6).
            CK3SpawnOrder.Apply(playerSupplier, armies.PlayerSide, "player side");
            CK3SpawnOrder.Apply(enemySupplier, armies.EnemySide, "enemy side");
            // Heroes' own arms (PROTOCOL.md §25).
            troopSuppliers[(int)playerParty.Side] = CK3HeraldrySupplier.Wrap(playerSupplier, armies.PlayerSide, "player side");
            troopSuppliers[(int)enemyParty.Side] = CK3HeraldrySupplier.Wrap(enemySupplier, armies.EnemySide, "enemy side");

            var record = new MissionInitializerRecord(setup.Scene)
            {
                DoNotUseLoadingScreen = false,
                PlayingInCampaignMode = false,
                AtmosphereOnCampaign = Atmosphere(setup),
                SceneLevels = setup.SceneLevels,
                DecalAtlasGroup = 2,
            };

            string missionName = FieldMissionName;
            BridgeIo.Log($"opening field mission '{missionName}' on {setup.Scene} ({setup.Season}, {setup.PresetHour}h, battle size index {setup.BattleSizeIndex})");
            Mission mission = MissionState.OpenNew(missionName, record, _ => new MissionBehavior[]
            {
                new DefaultBattleMissionAgentSpawnLogic(troopSuppliers, playerSide, Mission.BattleSizeType.Battle),
                new BattlePowerCalculationLogic(),
                new CustomBattleAgentLogic(),
                new BannerBearerLogic(),
                new CustomBattleMissionSpawnHandler(defenderParty, attackerParty),
                new MissionOptionsComponent(),
                new BattleEndLogic(),
                new BattleReinforcementsSpawnController(),
                new MissionCombatantsLogic(null, playerParty, defenderParty, attackerParty, Mission.MissionTeamAITypeEnum.FieldBattle, isPlayerSergeant),
                new BattleObserverMissionLogic(),
                new AgentHumanAILogic(),
                new AgentVictoryLogic(),
                new MissionAgentPanicHandler(),
                new BattleMissionAgentInteractionLogic(),
                new AgentMoraleInteractionLogic(),
                new AssignPlayerRoleInTeamMissionController(isPlayerGeneral, isPlayerSergeant, isPlayerInArmy: false,
                    isPlayerSergeant ? new List<string> { playerCharacter.StringId } : new List<string>()),
                new CK3CaptainsAssignmentLogic(armies.Attacker.General.GetName(), armies.Defender.General.GetName()),
                new EquipmentControllerLeaveLogic(),
                new MissionHardBorderPlacer(),
                new MissionBoundaryPlacer(),
                new MissionBoundaryCrossingHandler(),
                new HighlightsController(),
                new BattleHighlightsController(),
                new BattleDeploymentMissionController(isPlayerAttacker),
                new BattleDeploymentHandler(isPlayerAttacker),
                new MissionObjectiveLogic(),
            });
            mission.SetPlayerCanTakeControlOfAnotherAgentWhenDead();
            return mission;
        }

        /// <summary>
        /// Stock atmosphere (season id + preset by hour, BannerlordMissions.CreateAtmosphereInfoForMission). An
        /// optional weather preset from the request replaces the preset name when the file exists under
        /// Native/Atmospheres; rain, snow and fog densities are applied when given ([I], test BL-ATM).
        /// </summary>
        internal static AtmosphereInfo Atmosphere(MissionSetup setup)
        {
            AtmosphereInfo info = BannerlordMissions.CreateAtmosphereInfoForMission(setup.Season, setup.PresetHour);
            RequestExtras extras = setup.Extras;
            if (extras.AtmospherePreset != null)
            {
                if (AtmospherePresetExists(extras.AtmospherePreset)) info.AtmosphereName = extras.AtmospherePreset;
                else BridgeIo.Log($"atmosphere preset '{extras.AtmospherePreset}' not found, keeping {info.AtmosphereName}");
            }
            if (extras.Rain.HasValue) info.RainInfo.Density = MBMath.ClampFloat(extras.Rain.Value, 0f, 1f);
            if (extras.Snow.HasValue) info.SnowInfo.Density = MBMath.ClampFloat(extras.Snow.Value, 0f, 1f);
            if (extras.FogDensity.HasValue) info.FogInfo.Density = MBMath.ClampFloat(extras.FogDensity.Value, 0f, 10f);
            return info;
        }

        private static bool AtmospherePresetExists(string name)
        {
            if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return false;
            return File.Exists(System.IO.Path.Combine(ModuleHelper.GetModuleFullPath("Native"), "Atmospheres", name + ".xml"));
        }

        /// <summary>
        /// A scene the field opener can use: it exists (TaleWorlds.Engine Utilities.TryGetFullFilePathOfScene) and
        /// is not a siege or lords-hall map of the custom battle list (CustomGame.CustomBattleScenes), which need other
        /// openers. Village maps are fine; their forced scene level is returned so they load like custom battle's
        /// "Village" mode does (CustomBattleHelper.StartGame -> SceneLevel = forcedSceneLevel).
        /// </summary>
        public static bool IsFieldScene(string scene, out string forcedSceneLevel, out string reason)
        {
            forcedSceneLevel = null;
            reason = null;
            if (string.IsNullOrWhiteSpace(scene) || !Utilities.TryGetFullFilePathOfScene(scene, out _))
            {
                reason = "not found";
                return false;
            }
            CustomGame game = CustomGame.Current;
            if (game == null) return true;
            foreach (CustomBattleSceneData data in game.CustomBattleScenes)
            {
                if (data.SceneID != scene) continue;
                if (data.IsSiegeMap || data.IsLordsHallMap)
                {
                    reason = data.IsSiegeMap ? "a siege map" : "a lords hall map";
                    return false;
                }
                forcedSceneLevel = string.IsNullOrEmpty(data.ForcedSceneLevel) ? null : data.ForcedSceneLevel;
                break;
            }
            return true;
        }

        /// <summary>The culture of an arena scene id ("arena_vlandia_a" -> "vlandia").</summary>
        private static string ArenaCulture(string scene)
        {
            string[] parts = (scene ?? "").Split('_');
            return parts.Length >= 2 ? parts[1] : "vlandia";
        }

        /// <summary>An arena scene the game has loaded (the SandBox module, PROTOCOL.md §27).</summary>
        public static bool IsArenaScene(string scene) =>
            !string.IsNullOrWhiteSpace(scene) && scene.StartsWith("arena_", StringComparison.Ordinal) && Utilities.TryGetFullFilePathOfScene(scene, out _);

        /// <summary>A SandBox hideout scene the game has loaded (PROTOCOL.md §37).</summary>
        public static bool IsHideoutScene(string scene) =>
            !string.IsNullOrWhiteSpace(scene) && (scene.Contains("hideout") || scene.StartsWith("sea_bandit_", StringComparison.Ordinal))
            && Utilities.TryGetFullFilePathOfScene(scene, out _);

        /// <summary>The arena mission has its views (CK3BattleViews registered "CK3Arena"); without them it would crash.</summary>
        public static bool IsArenaMissionReady() => IsViewCreatorRegistered(ArenaMissionName);

        /// <summary>
        /// Reads ViewCreatorManager's private registry (filled by CollectTypes from every
        /// assembly that references the View assembly). Defensive only: any reflection failure means "not registered".
        /// </summary>
        private static bool IsViewCreatorRegistered(string missionName)
        {
            try
            {
                FieldInfo field = typeof(ViewCreatorManager).GetField("_viewCreators", BindingFlags.Static | BindingFlags.NonPublic);
                return field?.GetValue(null) is IDictionary creators && creators.Contains(missionName);
            }
            catch (Exception e)
            {
                BridgeIo.Log("view creator lookup failed: " + e.Message);
                return false;
            }
        }
    }
}
