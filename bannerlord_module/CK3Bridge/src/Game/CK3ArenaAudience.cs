using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace CK3Bridge
{
    /// <summary>
    /// The crowd of an arena (PROTOCOL.md §27). Arena scenes carry placeholder spectators (entities tagged "audience", unanimated
    /// crowd meshes that stand in T-pose) that SandBox's MissionAudienceHandler hides and replaces with townsfolk of the campaign
    /// settlement; a custom game has no settlement, so that handler cannot run. This one hides every placeholder and seats a share
    /// of them with townsfolk of the arena's culture (townsman_/townswoman_&lt;culture&gt;), playing the arena spectator animation. When
    /// the animation does not take or no townsfolk exist, the stands stay empty: never a T-pose.
    /// </summary>
    internal sealed class CK3ArenaAudience : MissionLogic
    {
        private const string AudienceTag = "audience";
        private const float Density = 0.45f;
        private readonly string _culture;
        private bool _done;

        public CK3ArenaAudience(string culture)
        {
            _culture = culture;
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            if (_done) return;
            _done = true;
            var seats = new List<GameEntity>();
            try
            {
                foreach (GameEntity e in Mission.Scene.FindEntitiesWithTag(AudienceTag)) seats.Add(e);
                foreach (GameEntity e in seats) e.SetVisibilityExcludeParents(false);
            }
            catch (Exception e)
            {
                BridgeIo.Log("arena audience: hiding the placeholders failed: " + e.Message);
                return;
            }
            int seated = 0;
            try
            {
                seated = Seat(seats);
            }
            catch (Exception e)
            {
                BridgeIo.Log("arena audience: seating failed, stands left empty: " + e);
            }
            BridgeIo.Log($"arena audience: {seats.Count} placeholder(s) hidden, {seated} spectator(s) seated");
        }

        private int Seat(List<GameEntity> seats)
        {
            var folk = new List<BasicCharacterObject>();
            foreach (string id in new[] { "townsman_" + _culture, "townswoman_" + _culture })
                if (MBObjectManager.Instance.GetObject<BasicCharacterObject>(id) is BasicCharacterObject c) folk.Add(c);
            if (folk.Count == 0) return 0;
            ActionIndexCache spectating = ActionIndexCache.act_arena_spectator;
            var spawned = new List<Agent>();
            foreach (GameEntity seat in seats)
            {
                if (MBRandom.RandomFloat > Density) continue;
                MatrixFrame frame = seat.GetGlobalFrame();
                Vec3 position = frame.origin;
                Vec2 direction = frame.rotation.f.AsVec2.Normalized();
                BasicCharacterObject who = folk[MBRandom.RandomInt(folk.Count)];
                // An origin, as SandBox's audience gives its townsfolk: mission logics read agent.Origin in OnAgentBuild (live
                // 2026-10-18: without one the seating failed with a NullReferenceException and the stands stayed empty).
                var data = new AgentBuildData(who)
                    .TroopOrigin(new BasicBattleAgentOrigin(who))
                    .Team(Team.Invalid)
                    .InitialPosition(in position)
                    .InitialDirection(in direction)
                    .CivilianEquipment(true)
                    .NoHorses(true)
                    .CanSpawnOutsideOfMissionBoundary(true);
                Agent agent = Mission.SpawnAgent(data);
                if (agent == null) continue;
                agent.Controller = AgentControllerType.None;
                agent.ToggleInvulnerable();
                bool playing = agent.SetActionChannel(0, in spectating, true, (AnimFlags)0, 0f, MBRandom.RandomFloatRanged(0.75f, 1f),
                    -0.2f, 0.4f, MBRandom.RandomFloat, false, -0.2f, 0, true);
                spawned.Add(agent);
                if (!playing && spawned.Count == 1)
                {
                    // The animation is not in this agent's action set: an unanimated crowd would be the T-pose again.
                    foreach (Agent a in spawned) a.FadeOut(true, true);
                    BridgeIo.Log("arena audience: the spectator animation did not play; stands left empty");
                    return 0;
                }
            }
            return spawned.Count;
        }
    }
}
