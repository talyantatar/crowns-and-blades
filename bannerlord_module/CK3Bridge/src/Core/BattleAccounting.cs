using System;
using System.Collections.Generic;
using CK3xBL.Protocol;

namespace CK3Bridge
{
    public enum BattleSide
    {
        Attacker,
        Defender,
    }

    public enum AgentFate
    {
        Killed,
        Wounded,
        Routed,
    }

    /// <summary>
    /// Counts requested/spawned/killed/wounded/routed per side and per CK3 group, and tracks every CK3 hero, then
    /// produces result.json snapshots (PROTOCOL.md §3.5). Game-agnostic: <see cref="BattleTracker"/> feeds it agent
    /// events, the self-check feeds it synthetic ones.
    /// Side totals cover every human agent of the side (heroes and a stock general included); groups cover troops only;
    /// heroes are reported individually. A hero's kills count enemies only: friendly fire and a
    /// horse trampling his own men earn him nothing, because the bridge turns kills into CK3 prestige, glory and prowess.
    /// </summary>
    public sealed class BattleAccounting
    {
        private readonly string _battleId;
        private readonly SideResult _attacker = new SideResult();
        private readonly SideResult _defender = new SideResult();
        private readonly List<HeroResult> _heroes = new List<HeroResult>();
        private readonly Dictionary<string, HeroResult> _heroByKey = new Dictionary<string, HeroResult>(StringComparer.Ordinal);
        private readonly Dictionary<string, BattleSide> _heroSide = new Dictionary<string, BattleSide>(StringComparer.Ordinal);

        public BattleAccounting(string battleId)
        {
            _battleId = battleId;
        }

        public SideResult Side(BattleSide side) => side == BattleSide.Attacker ? _attacker : _defender;

        /// <summary>Registers requested troops of one CK3 group (unspawned men count as survivors).</summary>
        public void ExpectTroops(BattleSide side, string group, int count)
        {
            if (count <= 0) return;
            SideResult s = Side(side);
            s.Requested += count;
            Group(s, group).Requested += count;
        }

        /// <summary>Registers one agent that belongs to no group (a stock general character).</summary>
        public void ExpectUngrouped(BattleSide side) => Side(side).Requested++;

        public void ExpectHero(BattleSide side, HeroSpec hero)
        {
            if (hero?.Key == null || _heroByKey.ContainsKey(hero.Key)) return;
            Side(side).Requested++;
            var result = new HeroResult { Key = hero.Key, Ck3Id = hero.Ck3Id, Tag = hero.Tag, State = "unspawned" };
            _heroes.Add(result);
            _heroByKey[hero.Key] = result;
            _heroSide[hero.Key] = side;
        }

        public bool IsHero(string heroKey) => heroKey != null && _heroByKey.ContainsKey(heroKey);

        /// <summary>An agent was built. <c>group</c> is its CK3 group, null for heroes and ungrouped agents.</summary>
        public void OnSpawned(BattleSide side, string group, string heroKey)
        {
            SideResult s = Side(side);
            s.Spawned++;
            if (group != null) Group(s, group).Spawned++;
            if (heroKey != null && _heroByKey.TryGetValue(heroKey, out HeroResult hero)) hero.State = "alive";
        }

        /// <summary>
        /// An agent of <paramref name="side"/> left the battle. <c>killerHeroKey</c> is the hero who downed it, or null; he
        /// is credited only when he fights for the other side. A hero downed by a friend still names him in killedBy.
        /// </summary>
        public void OnRemoved(BattleSide side, string group, string heroKey, AgentFate fate, string killerHeroKey)
        {
            SideResult s = Side(side);
            GroupResult g = group != null ? Group(s, group) : null;
            switch (fate)
            {
                case AgentFate.Killed:
                    s.Killed++;
                    if (g != null) g.Killed++;
                    break;
                case AgentFate.Wounded:
                    s.Wounded++;
                    if (g != null) g.Wounded++;
                    break;
                case AgentFate.Routed:
                    s.Routed++;
                    if (g != null) g.Routed++;
                    break;
            }

            if (heroKey != null && _heroByKey.TryGetValue(heroKey, out HeroResult hero))
            {
                hero.State = fate == AgentFate.Killed ? "killed" : fate == AgentFate.Wounded ? "wounded" : "routed";
                if (fate != AgentFate.Routed) hero.KilledBy = killerHeroKey;
            }

            if (fate != AgentFate.Routed && killerHeroKey != null && _heroByKey.TryGetValue(killerHeroKey, out HeroResult killer)
                && _heroSide[killerHeroKey] != side)
                killer.Kills++;
        }

        /// <summary>Health left (0..1 of the full pool) of a hero still on the field (PROTOCOL.md §32).</summary>
        public void SetHeroHealth(string heroKey, float fraction)
        {
            if (heroKey != null && _heroByKey.TryGetValue(heroKey, out HeroResult hero) && hero.State == "alive")
                hero.Health = (float)Math.Round(Math.Max(0f, Math.Min(1f, fraction)), 3);
        }

        /// <summary>A deep copy of the current counts, so later events never mutate a result that was already written.</summary>
        public BattleResult Snapshot(string status, string winner, bool playerRetreated, float durationSeconds, string error)
        {
            var result = new BattleResult
            {
                Id = _battleId,
                Status = status,
                Winner = winner,
                PlayerRetreated = playerRetreated,
                DurationSeconds = (float)Math.Round(Math.Max(0f, durationSeconds), 1),
                Attacker = Copy(_attacker),
                Defender = Copy(_defender),
                Error = error,
            };
            foreach (HeroResult h in _heroes)
                result.Heroes.Add(new HeroResult { Key = h.Key, Ck3Id = h.Ck3Id, Tag = h.Tag, State = h.State, Kills = h.Kills, KilledBy = h.KilledBy, Health = h.Health });
            return result;
        }

        /// <summary>
        /// Winner side from MissionResult.BattleState (TaleWorlds.Core enum name). DefenderPullBack (siege) counts for
        /// the attacker. "None" falls back to the player's victory/defeat flags, then to "none".
        /// </summary>
        public static string WinnerFrom(string battleState, bool playerVictory, bool playerDefeated, bool playerIsAttacker)
        {
            switch (battleState)
            {
                case "AttackerVictory":
                case "DefenderPullBack":
                    return "attacker";
                case "DefenderVictory":
                    return "defender";
            }
            if (playerVictory) return playerIsAttacker ? "attacker" : "defender";
            if (playerDefeated) return playerIsAttacker ? "defender" : "attacker";
            return "none";
        }

        private static GroupResult Group(SideResult side, string group)
        {
            if (!side.Groups.TryGetValue(group, out GroupResult g)) side.Groups[group] = g = new GroupResult();
            return g;
        }

        private static SideResult Copy(SideResult s)
        {
            var copy = new SideResult { Requested = s.Requested, Spawned = s.Spawned, Killed = s.Killed, Wounded = s.Wounded, Routed = s.Routed };
            foreach (KeyValuePair<string, GroupResult> kv in s.Groups)
            {
                GroupResult g = kv.Value;
                copy.Groups[kv.Key] = new GroupResult { Requested = g.Requested, Spawned = g.Spawned, Killed = g.Killed, Wounded = g.Wounded, Routed = g.Routed };
            }
            return copy;
        }
    }
}
