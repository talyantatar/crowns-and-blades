using System;
using System.Collections.Generic;
using CK3xBL.Protocol;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace CK3Bridge
{
    /// <summary>
    /// Mission side of a CK3 battle: tags every human agent with its side, CK3 group and hero key (and gives a grouped
    /// agent its group's buffs, <see cref="CK3BuffRegistry"/>), counts what happens to it, decides killed vs wounded, and
    /// reports load / result / end to the <see cref="BattleSession"/>.
    ///
    /// Killed vs wounded: custom games register DefaultAgentDecideKilledOrUnconsciousModel, which returns death
    /// probability 1 (every casualty is Killed, verified in a live test). Mission.GetAgentState asks the first
    /// IAgentStateDecider behaviour before rolling, so this class decides with CK3 odds
    /// (RequestExtras: troops 0.3 = CK3 main-phase fatal share, times the side's troopDeathChanceMult; heroes per key).
    ///
    /// Hero kills: a blow is credited to the hero who dealt it. A horse charge is dealt by the mount, which is never tagged
    /// (only humans are), so it counts for the mount's rider, as TaleWorlds credits it (MissionScoreboardComponent,
    /// AgentMoraleInteractionLogic; PROTOCOL.md §13.4 item 4).
    ///
    /// Snapshot timing: OnEndMission runs from Mission.EndMissionInternal before agents are removed and cleared,
    /// so agents still standing then are survivors.
    ///
    /// Every override is guarded: these are engine callbacks and an exception must never reach the mission loop.
    /// </summary>
    internal sealed class BattleTracker : MissionLogic, IAgentStateDecider
    {
        private sealed class AgentRecord
        {
            public BattleSide Side;
            public string Group;
            public string HeroKey;
            public bool Removed;
        }

        private readonly BattleSession _session;
        private readonly BattleArmies _armies;
        private readonly BattleAccounting _accounting;
        private readonly CK3Costumes _costumes;
        private readonly Dictionary<Agent, AgentRecord> _records = new Dictionary<Agent, AgentRecord>();
        private readonly HashSet<string> _unrequestedLogged = new HashSet<string>();
        private MissionResult _missionResult;
        private bool _resultReady;
        private bool _retreated;
        private bool _finished;
        private float _fightStart = -1f;
        private float _resultTime = -1f;

        /// <summary>
        /// Troops waiting for their costume (PROTOCOL.md §21). Dressing inside OnAgentBuild rebuilt each agent's visuals in the middle of
        /// Mission.SpawnAgent (the engine still checks the visuals' resources and initialises the stats after that callback); with
        /// 800-1000 agents spawned at once for the order of battle Bannerlord crashed there, in 3 of 3 big battles (live 2026-10-18;
        /// the same battle without costumes ran). So troops are dressed outside the build, once deployment is finished,
        /// <see cref="DressPerTick"/> per tick. Heroes, a handful, are still dressed in the build.
        /// </summary>
        private readonly Queue<(Agent Agent, BattleSide Side, string Group)> _toDress = new Queue<(Agent, BattleSide, string)>();
        public const int DressPerTick = 40;

        public BattleTracker(BattleSession session)
        {
            _session = session;
            _armies = session.Armies;
            _accounting = session.Armies.Accounting;
            _costumes = new CK3Costumes(session.Request);
        }

        public override void AfterStart()
        {
            base.AfterStart();
            Guard(nameof(AfterStart), _session.OnMissionLoaded);
            Guard("team banners", LogTeamBanners);
        }

        /// <summary>Diagnostics (live 2026-10-18: a red shield with a white eagle showed as plain red): the banner each team really
        /// carries, with the texture of every icon layer.</summary>
        private void LogTeamBanners()
        {
            foreach (Team team in Mission.Teams)
            {
                if (team?.Banner == null || (team.Side != BattleSideEnum.Attacker && team.Side != BattleSideEnum.Defender)) continue;
                string code = team.Banner.Serialize();
                var icons = new List<string>();
                string[] parts = code.Split('.');
                for (int i = 10; i + 9 < parts.Length; i += 10)
                    if (int.TryParse(parts[i], out int mesh))
                    {
                        BannerIconData data = BannerManager.Instance.GetIconDataFromIconId(mesh);
                        icons.Add($"{mesh}={data.MaterialName ?? "?"}#{data.TextureIndex}");
                    }
                BridgeIo.Log($"team {team.Side}{(team.IsPlayerTeam ? " (player)" : "")} banner {code}; icons {(icons.Count > 0 ? string.Join(", ", icons) : "none")}");
            }
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            if (_toDress.Count == 0 || _finished || !Mission.IsDeploymentFinished) return;
            Guard("costumes", DressSome);
        }

        /// <summary>Dresses up to <see cref="DressPerTick"/> waiting troops. Re-equipping recomputes the health pool, so the pool the
        /// group buffs gave and the health left are put back.</summary>
        private void DressSome()
        {
            for (int n = 0; n < DressPerTick && _toDress.Count > 0; n++)
            {
                var (agent, side, group) = _toDress.Dequeue();
                if (agent == null || !agent.IsActive() || !_records.TryGetValue(agent, out AgentRecord record) || record.Removed) continue;
                float baseLimit = agent.BaseHealthLimit, limit = agent.HealthLimit, health = agent.Health;
                if (!_costumes.Dress(agent, side, group)) continue;
                agent.BaseHealthLimit = baseLimit;
                agent.HealthLimit = limit;
                agent.Health = Math.Min(health, limit);
            }
        }

        public override void OnDeploymentFinished()
        {
            base.OnDeploymentFinished();
            _fightStart = Mission.CurrentTime;
        }

        public override void OnAgentBuild(Agent agent, Banner banner)
        {
            base.OnAgentBuild(agent, banner);
            Guard(nameof(OnAgentBuild), () => Register(agent));
        }

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            base.OnAgentRemoved(affectedAgent, affectorAgent, agentState, blow);
            Guard(nameof(OnAgentRemoved), () => Remove(affectedAgent, affectorAgent, agentState));
            Guard("aura", () => CK3BuffRegistry.KnightLeft(affectedAgent, Mission));
        }

        /// <summary>Consulted for downing blows only. Mounts (and any failure) fall back to the stock roll (AgentState.None).</summary>
        public AgentState GetAgentState(Agent affectedAgent, float deathProbability, out bool usedSurgery)
        {
            usedSurgery = false;
            try
            {
                if (!affectedAgent.IsHuman) return AgentState.None;
                RequestExtras extras = _session.Extras;
                float chance = _records.TryGetValue(affectedAgent, out AgentRecord record) && record.HeroKey != null
                    ? extras.DeathChanceForHero(record.HeroKey)
                    : BuffMath.TroopDeathChance(extras.TroopDeathChance, CK3BuffRegistry.Side(affectedAgent));
                return MBRandom.RandomFloat < chance ? AgentState.Killed : AgentState.Unconscious;
            }
            catch (Exception e)
            {
                BridgeIo.Log("GetAgentState failed: " + e.Message);
                return AgentState.None;
            }
        }

        public override void OnMissionResultReady(MissionResult missionResult)
        {
            base.OnMissionResultReady(missionResult);
            Guard(nameof(OnMissionResultReady), () =>
            {
                if (_finished) return;
                _missionResult = missionResult;
                _resultReady = true;
                _resultTime = Mission.CurrentTime;
                BridgeIo.Log($"mission result: {missionResult?.BattleState} playerVictory={missionResult?.PlayerVictory} enemyRetreated={missionResult?.EnemyRetreated}");
                _session.OnResultReady(Snapshot("result"));
            });
        }

        /// <summary>Tab -> Leave -> "retreat?" confirmed (BasicMissionHandler -> Mission.RetreatMission).</summary>
        public override void OnRetreatMission()
        {
            base.OnRetreatMission();
            _retreated = true;
        }

        public override void OnSurrenderMission()
        {
            base.OnSurrenderMission();
            _retreated = true;
        }

        protected override void OnEndMission()
        {
            base.OnEndMission();
            Guard(nameof(OnEndMission), () =>
            {
                if (_finished) return;
                if (_costumes.Dressed > 0) BridgeIo.Log($"costumes: {_costumes.Dressed} agents dressed");
                BattleResult final = Snapshot(_session.EndStatus(_resultReady || _retreated));
                _finished = true;
                _session.OnMissionEnded(final);
            });
        }

        private void Register(Agent agent)
        {
            if (_finished || !agent.IsHuman || agent.Character == null) return;
            if (!TrySide(agent, out BattleSide side, out BattleSideEnum engineSide)) return;

            var record = new AgentRecord { Side = side };
            if (_armies.HeroByCharacter.TryGetValue(agent.Character, out CreatedHero hero))
            {
                record.HeroKey = hero.Spec.Key;
                _costumes.DressHero(agent, hero.Spec);   // before CK3FaceGuard (added after this logic) checks the body under the new helmet
            }
            else if (_armies.Ledger.TryTake((engineSide, agent.Character), out string group))
            {
                record.Group = group;
                if (_costumes.HasLook(side, group)) _toDress.Enqueue((agent, side, group));   // dressed after the build (see _toDress)
                GroupBuffs buffs = CK3BuffRegistry.TagAgentGroup(agent, engineSide, group);
                if (buffs != null) ApplyGroupBuffs(agent, buffs);
            }
            else if (_unrequestedLogged.Add(agent.Character.StringId))
            {
                BridgeIo.Log($"agent of '{agent.Character.StringId}' on {side} was not requested; counted in side totals only");
            }
            _records[agent] = record;
            _accounting.OnSpawned(side, record.Group, record.HeroKey);
        }

        /// <summary>
        /// The spawn-time part of a group's buffs (PROTOCOL.md §13.4 item 5): a larger or smaller health pool, starting full,
        /// and extra initial morale (Bannerlord clamped it to 15..100 at initialisation only). Damage factors act per hit.
        /// </summary>
        private static void ApplyGroupBuffs(Agent agent, GroupBuffs buffs)
        {
            if (buffs.HealthMult != 1f)
            {
                agent.BaseHealthLimit *= buffs.HealthMult;
                agent.HealthLimit *= buffs.HealthMult;
                agent.Health = agent.HealthLimit;
            }
            if (buffs.InitialMoraleAdd != 0f) agent.ChangeMorale(buffs.InitialMoraleAdd);
        }

        private void Remove(Agent affected, Agent affector, AgentState state)
        {
            if (_finished || !_records.TryGetValue(affected, out AgentRecord record) || record.Removed) return;
            AgentFate fate;
            switch (state)
            {
                case AgentState.Killed: fate = AgentFate.Killed; break;
                case AgentState.Unconscious: fate = AgentFate.Wounded; break;
                case AgentState.Routed: fate = AgentFate.Routed; break;
                default: return;   // Deleted/None/Active: not a battle outcome
            }
            record.Removed = true;
            Agent killerAgent = affector != null && affector.IsMount ? affector.RiderAgent : affector;
            string killer = killerAgent != null && _records.TryGetValue(killerAgent, out AgentRecord by) ? by.HeroKey : null;
            _accounting.OnRemoved(record.Side, record.Group, record.HeroKey, fate, killer);
        }

        private BattleResult Snapshot(string status)
        {
            bool playerIsAttacker = _armies.PlayerIsAttacker;
            string winner = "none";
            if (status != "cancelled" && status != "aborted")
            {
                if (_missionResult != null)
                    winner = BattleAccounting.WinnerFrom(_missionResult.BattleState.ToString(), _missionResult.PlayerVictory,
                        _missionResult.PlayerDefeated, playerIsAttacker);
                else if (_retreated)
                    winner = playerIsAttacker ? "defender" : "attacker";
            }
            // Mission time excludes pauses (the hold, the escape menu): deployment end -> result.
            foreach (KeyValuePair<Agent, AgentRecord> kv in _records)
                if (kv.Value.HeroKey != null && !kv.Value.Removed && kv.Key.IsActive() && kv.Key.HealthLimit > 0f)
                    _accounting.SetHeroHealth(kv.Value.HeroKey, kv.Key.Health / kv.Key.HealthLimit);
            float end = _resultTime >= 0f ? _resultTime : Mission.CurrentTime;
            float start = _fightStart >= 0f ? _fightStart : 0f;
            return _accounting.Snapshot(status, winner, _retreated && !_resultReady, end - start, null);
        }

        private static bool TrySide(Agent agent, out BattleSide side, out BattleSideEnum engineSide)
        {
            engineSide = agent.Team?.Side ?? agent.Origin?.BattleCombatant?.Side ?? BattleSideEnum.None;
            side = engineSide == BattleSideEnum.Attacker ? BattleSide.Attacker : BattleSide.Defender;
            return engineSide == BattleSideEnum.Attacker || engineSide == BattleSideEnum.Defender;
        }

        private static void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                BridgeIo.Log($"BattleTracker.{what} failed: {e}");
            }
        }
    }
}
