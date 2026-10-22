using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CK3Bridge
{
    /// <summary>
    /// The spawner of a noble single combat in an arena (PROTOCOL.md §27). Arena scenes have no battle spawn paths, so this logic
    /// takes the place of the battle spawn logic, as LordsHallFightMissionController does for lords' halls: on the first tick that has
    /// both teams it spawns every troop of both suppliers on foot at the two arena spawn points (scene tag sp_arena) furthest apart,
    /// facing each other, then starts the fight (mission mode Battle, deployment finished). It is the mission's
    /// IMissionAgentSpawnLogic, so BattleEndLogic ends the duel when a side has no one standing.
    /// </summary>
    internal sealed class CK3ArenaDuelController : MissionLogic, IMissionAgentSpawnLogic
    {
        private static readonly string[] SpawnTags = { "sp_arena", "sp_arena_respawn" };

        private readonly IMissionTroopSupplier[] _suppliers;
        private readonly BattleSideEnum _playerSide;
        private readonly bool _bareHands;
        private readonly bool _mounted;
        private bool _spawned;

        /// <param name="bareHands">A wrestling bout (PROTOCOL.md §29): every weapon is taken from the fighters as they spawn.</param>
        /// <param name="mounted">A joust (PROTOCOL.md §36): the fighters spawn on their tournament horses, blunt lance in hand.</param>
        public CK3ArenaDuelController(IMissionTroopSupplier[] suppliers, BattleSideEnum playerSide, bool bareHands = false, bool mounted = false)
        {
            _suppliers = suppliers;
            _playerSide = playerSide;
            _bareHands = bareHands;
            _mounted = mounted;
        }

        public BattleSideEnum PlayerSide => _playerSide;

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            if (_spawned || Mission.AttackerTeam == null || Mission.DefenderTeam == null) return;
            _spawned = true;
            try
            {
                Spawn();
            }
            catch (Exception e)
            {
                BridgeIo.Log("arena: spawning the duelists failed: " + e);
            }
        }

        private void Spawn()
        {
            (Vec3 a, Vec3 d) = SpawnPoints();
            int count = 0;
            foreach (BattleSideEnum side in new[] { BattleSideEnum.Attacker, BattleSideEnum.Defender })
            {
                IMissionTroopSupplier supplier = _suppliers[(int)side];
                if (supplier == null) continue;
                Vec3 at = side == BattleSideEnum.Attacker ? a : d;
                Vec3 facing = side == BattleSideEnum.Attacker ? d : a;
                Vec2 direction = (facing - at).AsVec2;
                direction = direction.Length > 0.01f ? direction.Normalized() : Vec2.Forward;
                Vec2 sideways = direction.LeftVec();
                List<IAgentOriginBase> troops = supplier.SupplyTroops(64).ToList();
                for (int i = 0; i < troops.Count; i++)
                {
                    // A second fighter of a side (rare) stands a step beside the first.
                    Vec3 position = at + (sideways * (1.5f * i)).ToVec3();
                    Agent agent = Mission.SpawnTroop(troops[i], side == _playerSide, hasFormation: true, spawnWithHorse: _mounted,
                        isReinforcement: false, formationTroopCount: troops.Count, formationTroopIndex: i, isAlarmed: true,
                        wieldInitialWeapons: true, initialPosition: position, initialDirection: direction,
                        specialActionSetSuffix: null, bannerItem: null, formationIndex: _mounted ? FormationClass.Cavalry : FormationClass.Infantry, useTroopClassForSpawn: false);
                    count++;
                    if (agent != null && _bareHands) Unarm(agent);
                    if (agent != null && agent.Character == Game.Current.PlayerTroop && Mission.MainAgent == null)
                    {
                        agent.Controller = AgentControllerType.Player;
                        Mission.MainAgent = agent;
                    }
                }
            }
            Mission.SetMissionMode(MissionMode.Battle, true);
            if (!Mission.IsDeploymentFinished) Mission.OnDeploymentFinished();
            Charge();
            BridgeIo.Log($"arena: {count} duelist(s) spawned {a.Distance(d):0.0} m apart");
        }

        /// <summary>
        /// Without team AI a formation holds where it spawned, so the AI duelist would wait for the player: every formation
        /// of both teams is ordered to charge (the player's own agent is not moved by it), and every AI fighter picks its target itself.
        /// </summary>
        private void Charge()
        {
            foreach (Team team in new[] { Mission.AttackerTeam, Mission.DefenderTeam })
            {
                if (team == null) continue;
                foreach (Formation formation in team.FormationsIncludingEmpty)
                    if (formation.CountOfUnits > 0) formation.SetMovementOrder(MovementOrder.MovementOrderCharge);
                foreach (Agent agent in team.ActiveAgents)
                {
                    if (agent.IsPlayerControlled || !agent.IsHuman) continue;
                    agent.SetWatchState(Agent.WatchState.Alarmed);
                    agent.SetAutomaticTargetSelection(true);
                }
            }
        }

        /// <summary>Takes every weapon (and the banner slot) from a wrestler: fists only.</summary>
        private static void Unarm(Agent agent)
        {
            for (EquipmentIndex i = EquipmentIndex.WeaponItemBeginSlot; i < EquipmentIndex.NumAllWeaponSlots; i++)
                if (!agent.Equipment[i].IsEmpty) agent.RemoveEquippedWeapon(i);
        }

        /// <summary>The two tagged arena spawn points furthest apart; else two points around the scene's first spawn or the origin.</summary>
        private (Vec3, Vec3) SpawnPoints()
        {
            var points = new List<Vec3>();
            foreach (string tag in SpawnTags)
            {
                foreach (GameEntity e in Mission.Scene.FindEntitiesWithTag(tag)) points.Add(e.GlobalPosition);
                if (points.Count >= 2) break;
            }
            if (points.Count < 2)
            {
                Vec3 center = points.Count == 1 ? points[0] : Vec3.Zero;
                BridgeIo.Log($"arena: {points.Count} spawn point(s) found; the duelists start 12 m apart around {center}");
                return (center + new Vec3(-6f, 0f, 0f), center + new Vec3(6f, 0f, 0f));
            }
            Vec3 bestA = points[0], bestB = points[1];
            float best = -1f;
            for (int i = 0; i < points.Count; i++)
                for (int j = i + 1; j < points.Count; j++)
                {
                    float dist = points[i].DistanceSquared(points[j]);
                    if (dist > best)
                    {
                        best = dist;
                        bestA = points[i];
                        bestB = points[j];
                    }
                }
            return (bestA, bestB);
        }

        // ---- IMissionAgentSpawnLogic: one wave, nothing to reinforce ------------------------------------------------------

        public void StartSpawner(BattleSideEnum side)
        {
        }

        public void StopSpawner(BattleSideEnum side)
        {
        }

        public bool IsSideSpawnEnabled(BattleSideEnum side) => !_spawned;

        /// <summary>After the spawn, a side is depleted when nobody of its team is still standing.</summary>
        public bool IsSideDepleted(BattleSideEnum side)
        {
            if (!_spawned) return false;
            Team team = side == BattleSideEnum.Attacker ? Mission.AttackerTeam : Mission.DefenderTeam;
            return team == null || team.ActiveAgents.All(a => !a.IsHuman || !a.IsActive());
        }

        public float GetReinforcementInterval(BattleSideEnum side) => 0f;

        public IEnumerable<IAgentOriginBase> GetAllTroopsForSide(BattleSideEnum side) =>
            _suppliers[(int)side]?.GetAllTroops() ?? Enumerable.Empty<IAgentOriginBase>();

        public bool GetSpawnHorses(BattleSideEnum side) => _mounted;

        public int GetNumberOfPlayerControllableTroops() => 0;
    }
}
