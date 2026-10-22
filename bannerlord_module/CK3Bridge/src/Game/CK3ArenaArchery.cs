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
    /// The archery contest of a CK3 tournament (PROTOCOL.md §36). Three arenas (aserai, battania, empire) carry SandBox's hidden
    /// archery set (entity tag tournament_archery: pots with a DestructableComponent and archers' stands tagged sp_arena); SandBox's own
    /// archery controller needs a campaign, so this one runs the contest: the set is shown, the player stands at its stand with a bow
    /// and two quivers, and has <see cref="TimeLimit"/> seconds to hit <c>par</c> different pots (the bridge sets the mark from the
    /// best other archer's prowess). The rival stands beside him and does not shoot. The loser leaves the lists (fades out, which no
    /// fate counts), so BattleEndLogic ends the mission with the right winner, as in a duel. Like the duel it is the mission's
    /// IMissionAgentSpawnLogic.
    /// </summary>
    internal sealed class CK3ArenaArcheryController : MissionLogic, IMissionAgentSpawnLogic
    {
        public const string SetTag = "tournament_archery";
        public const float TimeLimit = 120f;
        private const float SetRadius = 60f;
        private const float OutOfArrowsGrace = 4f;

        private readonly IMissionTroopSupplier[] _suppliers;
        private readonly BattleSideEnum _playerSide;
        private readonly int _par;
        private readonly string _hitFormat;
        private readonly HashSet<DestructableComponent> _hit = new HashSet<DestructableComponent>();
        private readonly List<DestructableComponent> _targets = new List<DestructableComponent>();
        private bool _spawned;
        private bool _decided;
        private float _start = -1f;
        private float _outOfArrowsAt = -1f;
        private Agent _player;
        private Agent _rival;

        /// <param name="hitFormat">The hit message, {0} hits so far, {1} the mark (the request's language).</param>
        public CK3ArenaArcheryController(IMissionTroopSupplier[] suppliers, BattleSideEnum playerSide, int par, string hitFormat)
        {
            _suppliers = suppliers;
            _playerSide = playerSide;
            _par = Math.Max(1, par);
            _hitFormat = string.IsNullOrEmpty(hitFormat) ? "Hits: {0} of {1}" : hitFormat;
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
                    Setup();
                    return;
                }
                if (!_decided) Judge();
            }
            catch (Exception e)
            {
                BridgeIo.Log("archery: " + e);
                _spawned = true;
            }
        }

        private void Setup()
        {
            GameEntity set = Mission.Scene.FindEntityWithTag(SetTag);
            Vec3 center = set != null ? set.GlobalPosition : Vec3.Zero;
            if (set != null) ShowAll(set);
            foreach (DestructableComponent d in Mission.MissionObjects.OfType<DestructableComponent>())
            {
                if (set != null && d.GameEntity.GlobalPosition.Distance(center) > SetRadius) continue;
                d.OnHitTaken += OnTargetHit;
                _targets.Add(d);
            }
            var stands = Mission.Scene.FindEntitiesWithTag("sp_arena").Select(e => e.GlobalPosition)
                .Where(p => set == null || p.Distance(center) < SetRadius).OrderBy(p => p.Distance(center)).ToList();
            Vec3 me = stands.Count > 0 ? stands[0] : center;
            Vec3 rival = stands.Count > 1 ? stands[1] : me + new Vec3(2f, 0f, 0f);
            Vec2 facing = set != null ? set.GetGlobalFrame().rotation.f.AsVec2 : Vec2.Forward;
            facing = facing.Length > 0.01f ? facing.Normalized() : Vec2.Forward;

            foreach (BattleSideEnum side in new[] { BattleSideEnum.Attacker, BattleSideEnum.Defender })
            {
                IAgentOriginBase origin = _suppliers[(int)side]?.SupplyTroops(1).FirstOrDefault();
                if (origin == null) continue;
                bool mine = side == _playerSide;
                Agent agent = Mission.SpawnTroop(origin, mine, hasFormation: true, spawnWithHorse: false, isReinforcement: false,
                    formationTroopCount: 1, formationTroopIndex: 0, isAlarmed: false, wieldInitialWeapons: true,
                    initialPosition: mine ? me : rival, initialDirection: facing, specialActionSetSuffix: null, bannerItem: null,
                    formationIndex: FormationClass.Ranged, useTroopClassForSpawn: false);
                if (agent == null) continue;
                if (mine)
                {
                    _player = agent;
                    if (agent.Character == Game.Current.PlayerTroop && Mission.MainAgent == null)
                    {
                        agent.Controller = AgentControllerType.Player;
                        Mission.MainAgent = agent;
                    }
                }
                else
                {
                    // The mark to beat watches: no AI, no harm.
                    _rival = agent;
                    agent.Controller = AgentControllerType.None;
                    agent.ToggleInvulnerable();
                }
            }
            Mission.SetMissionMode(MissionMode.Battle, true);
            if (!Mission.IsDeploymentFinished) Mission.OnDeploymentFinished();
            _start = Mission.CurrentTime;
            BridgeIo.Log($"archery: {_targets.Count} target(s), mark {_par}, {TimeLimit:0} s{(set == null ? " (no archery set in this arena)" : "")}");
            InformationManager.DisplayMessage(new InformationMessage(string.Format(_hitFormat, 0, _par)));
        }

        private static void ShowAll(GameEntity entity)
        {
            entity.SetVisibilityExcludeParents(true);
            foreach (GameEntity child in entity.GetChildren()) ShowAll(child);
        }

        private void OnTargetHit(DestructableComponent target, Agent attacker, in MissionWeapon weapon, ScriptComponentBehavior attackerScript, int damage)
        {
            if (_decided || attacker == null || attacker != _player || !_hit.Add(target)) return;
            InformationManager.DisplayMessage(new InformationMessage(string.Format(_hitFormat, _hit.Count, _par)));
        }

        /// <summary>The mark reached wins at once; time up, or no arrow left (after a grace for those in the air), loses.</summary>
        private void Judge()
        {
            if (_player == null || !_player.IsActive()) return;
            if (_hit.Count >= _par)
            {
                Decide(playerWon: true, why: $"{_hit.Count} of {_par} hit");
                return;
            }
            float now = Mission.CurrentTime;
            if (_start >= 0f && now - _start > TimeLimit)
            {
                Decide(playerWon: false, why: $"time up with {_hit.Count} of {_par}");
                return;
            }
            if (Arrows(_player) > 0) { _outOfArrowsAt = -1f; return; }
            if (_outOfArrowsAt < 0f) _outOfArrowsAt = now;
            else if (now - _outOfArrowsAt > OutOfArrowsGrace) Decide(playerWon: false, why: $"no arrows left with {_hit.Count} of {_par}");
        }

        private static int Arrows(Agent agent)
        {
            int n = 0;
            for (EquipmentIndex i = EquipmentIndex.WeaponItemBeginSlot; i < EquipmentIndex.NumAllWeaponSlots; i++)
            {
                MissionWeapon w = agent.Equipment[i];
                if (!w.IsEmpty && w.CurrentUsageItem != null && w.CurrentUsageItem.IsConsumable) n += w.Amount;
            }
            return n;
        }

        private void Decide(bool playerWon, string why)
        {
            _decided = true;
            BridgeIo.Log($"archery: the player {(playerWon ? "wins" : "loses")} ({why})");
            Agent loser = playerWon ? _rival : _player;
            if (loser != null && loser.IsActive()) loser.FadeOut(true, true);
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
