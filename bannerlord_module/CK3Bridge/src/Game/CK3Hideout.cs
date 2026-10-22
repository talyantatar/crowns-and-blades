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
    /// A skirmish with bandits or deserters fought at their camp (PROTOCOL.md §37): one of SandBox's hideout scenes. Hideouts have no
    /// battle spawn paths, so this logic spawns everyone, as the arena duel does: the player's party at the attackers' spot (tags
    /// attacker_infantry / attacker_archer), the band spread over the camp's posts (npc_wait, sp_guard_with_spear) round the
    /// defenders' spot, its leader at the boss fight spot (hideout_boss_fight). The band idles at its fires until the party comes
    /// within <see cref="AlarmDistance"/> or <see cref="AlarmSeconds"/> have passed; then both sides charge. It is the mission's
    /// IMissionAgentSpawnLogic, so BattleEndLogic ends the fight when a side has no one standing.
    /// </summary>
    internal sealed class CK3HideoutController : MissionLogic, IMissionAgentSpawnLogic
    {
        public const float AlarmDistance = 35f, AlarmSeconds = 25f;

        private readonly IMissionTroopSupplier[] _suppliers;
        private readonly BattleSideEnum _playerSide;
        private readonly ICollection<BasicCharacterObject> _heroes;
        private bool _spawned;
        private bool _alarmed;
        private float _start;

        /// <param name="heroes">The CK3 heroes: the band's leader waits at the boss spot (without one, the band's first man).</param>
        public CK3HideoutController(IMissionTroopSupplier[] suppliers, BattleSideEnum playerSide, ICollection<BasicCharacterObject> heroes)
        {
            _suppliers = suppliers;
            _playerSide = playerSide;
            _heroes = heroes ?? new List<BasicCharacterObject>();
        }

        public BattleSideEnum PlayerSide => _playerSide;

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            try
            {
                if (!_spawned)
                {
                    if (Mission.AttackerTeam == null || Mission.DefenderTeam == null) return;
                    _spawned = true;
                    Spawn();
                }
                else if (!_alarmed && (Mission.CurrentTime - _start > AlarmSeconds || PartyIsClose())) Alarm();
            }
            catch (Exception e)
            {
                BridgeIo.Log("hideout: " + e);
                _spawned = true;
                if (!_alarmed) Alarm();
            }
        }

        private Vec3 First(Vec3 fallback, params string[] tags)
        {
            foreach (string tag in tags)
            {
                GameEntity e = Mission.Scene.FindEntityWithTag(tag);
                if (e != null) return e.GlobalPosition;
            }
            return fallback;
        }

        private void Spawn()
        {
            Vec3 party = First(Vec3.Zero, "attacker_infantry", "attacker_ranged", "attacker_archer");
            Vec3 partyArchers = First(party, "attacker_archer", "attacker_ranged");
            Vec3 camp = First(party + new Vec3(30f, 0f, 0f), "defender_infantry", "defender_ranged", "defender_archer");
            Vec3 boss = First(camp, "hideout_boss_fight");
            var posts = new List<Vec3>();
            foreach (string tag in new[] { "sp_guard_with_spear", "npc_wait" })
                foreach (GameEntity e in Mission.Scene.FindEntitiesWithTag(tag))
                    if (e.GlobalPosition.Distance(camp) < 60f) posts.Add(e.GlobalPosition);
            posts = posts.Distinct().OrderBy(p => p.Distance(camp)).ToList();

            int partyCount = 0, bandCount = 0;
            foreach (BattleSideEnum side in new[] { BattleSideEnum.Attacker, BattleSideEnum.Defender })
            {
                IMissionTroopSupplier supplier = _suppliers[(int)side];
                if (supplier == null) continue;
                List<IAgentOriginBase> troops = supplier.SupplyTroops(128).ToList();
                bool mine = side == _playerSide;
                Vec3 towards = mine ? camp : party;
                for (int i = 0; i < troops.Count; i++)
                {
                    IAgentOriginBase origin = troops[i];
                    bool ranged = origin.Troop?.DefaultFormationClass is FormationClass.Ranged or FormationClass.HorseArcher;
                    Vec3 at;
                    if (mine) at = (ranged ? partyArchers : party) + Ring(i, 1.4f);
                    else if (IsLeader(origin, i, troops)) at = boss;
                    else at = posts.Count > 0 ? posts[i % posts.Count] + Ring(i / Math.Max(1, posts.Count), 1.2f) : camp + Ring(i, 1.6f);
                    Vec2 direction = (towards - at).AsVec2;
                    direction = direction.Length > 0.01f ? direction.Normalized() : Vec2.Forward;
                    Agent agent = Mission.SpawnTroop(origin, mine, hasFormation: true, spawnWithHorse: false, isReinforcement: false,
                        formationTroopCount: troops.Count, formationTroopIndex: i, isAlarmed: mine, wieldInitialWeapons: true,
                        initialPosition: at, initialDirection: direction, specialActionSetSuffix: null, bannerItem: null,
                        formationIndex: ranged ? FormationClass.Ranged : FormationClass.Infantry, useTroopClassForSpawn: false);
                    if (agent == null) continue;
                    if (mine) partyCount++; else bandCount++;
                    if (agent.Character == Game.Current.PlayerTroop && Mission.MainAgent == null)
                    {
                        agent.Controller = AgentControllerType.Player;
                        Mission.MainAgent = agent;
                    }
                }
            }
            // Until the alarm everyone holds: the band at its fires, the party behind the player.
            foreach (Team team in new[] { Mission.AttackerTeam, Mission.DefenderTeam })
                foreach (Formation formation in team.FormationsIncludingEmpty)
                    if (formation.CountOfUnits > 0) formation.SetMovementOrder(MovementOrder.MovementOrderStop);
            Mission.SetMissionMode(MissionMode.Battle, true);
            if (!Mission.IsDeploymentFinished) Mission.OnDeploymentFinished();
            _start = Mission.CurrentTime;
            BridgeIo.Log($"hideout: party {partyCount}, band {bandCount} over {posts.Count} camp post(s), {party.Distance(camp):0} m apart");
        }

        private bool IsLeader(IAgentOriginBase origin, int index, List<IAgentOriginBase> band) =>
            band.Any(o => o.Troop != null && _heroes.Contains(o.Troop)) ? origin.Troop != null && _heroes.Contains(origin.Troop) : index == 0;

        private static Vec3 Ring(int i, float step)
        {
            if (i == 0) return Vec3.Zero;
            float angle = i * 2.399963f;   // golden angle: no two neighbours on top of each other
            float r = step * (float)Math.Sqrt(i);
            return new Vec3((float)Math.Cos(angle) * r, (float)Math.Sin(angle) * r, 0f);
        }

        private bool PartyIsClose()
        {
            Team party = _playerSide == BattleSideEnum.Attacker ? Mission.AttackerTeam : Mission.DefenderTeam;
            Team band = _playerSide == BattleSideEnum.Attacker ? Mission.DefenderTeam : Mission.AttackerTeam;
            if (party == null || band == null) return true;
            foreach (Agent p in party.ActiveAgents)
                foreach (Agent b in band.ActiveAgents)
                    if (p.Position.DistanceSquared(b.Position) < AlarmDistance * AlarmDistance) return true;
            return false;
        }

        /// <summary>The band has seen the party: every formation of both sides charges, every AI fighter picks its own target.</summary>
        private void Alarm()
        {
            _alarmed = true;
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
            BridgeIo.Log($"hideout: the band is alarmed after {Mission.CurrentTime - _start:0} s");
        }

        // ---- IMissionAgentSpawnLogic: one wave, nothing to reinforce ------------------------------------------------------

        public void StartSpawner(BattleSideEnum side)
        {
        }

        public void StopSpawner(BattleSideEnum side)
        {
        }

        public bool IsSideSpawnEnabled(BattleSideEnum side) => !_spawned;

        public bool IsSideDepleted(BattleSideEnum side)
        {
            if (!_spawned) return false;
            Team team = side == BattleSideEnum.Attacker ? Mission.AttackerTeam : Mission.DefenderTeam;
            return team == null || team.ActiveAgents.All(a => !a.IsHuman || !a.IsActive());
        }

        public float GetReinforcementInterval(BattleSideEnum side) => 0f;

        public IEnumerable<IAgentOriginBase> GetAllTroopsForSide(BattleSideEnum side) =>
            _suppliers[(int)side]?.GetAllTroops() ?? Enumerable.Empty<IAgentOriginBase>();

        public bool GetSpawnHorses(BattleSideEnum side) => false;

        public int GetNumberOfPlayerControllableTroops() => 0;
    }
}
