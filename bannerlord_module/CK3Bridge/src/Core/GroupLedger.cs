using System.Collections.Generic;

namespace CK3Bridge
{
    /// <summary>
    /// Tags spawned agents with the CK3 group they were requested for, without replacing the stock
    /// CustomBattleTroopSupplier (spawn priority, heroes first, UnitSpawnPrioritization all stay stock).
    ///
    /// Several TroopRequests may share one Bannerlord troop (e.g. two armies' levies of one culture, or a regiment split
    /// over two tiers). Agents of one troop are identical, so any assignment is valid; each agent built for (side, troop)
    /// goes to the reservation that is least filled relative to its size (ties: request order). Spawns, and so exposure
    /// to the fighting, are spread proportionally: with a battle-size limit or reinforcements, two equal armies sharing
    /// a troop lose men in proportion instead of the first one taking every casualty while the second stays unspawned.
    /// </summary>
    public sealed class GroupLedger<TKey>
    {
        private sealed class Reservation
        {
            public string Group;
            public int Requested;
            public int Taken;
            public int Rank;
        }

        private readonly Dictionary<TKey, List<Reservation>> _reservations = new Dictionary<TKey, List<Reservation>>();

        /// <summary>
        /// Reserves <paramref name="count"/> agents of <paramref name="key"/> for <paramref name="group"/>. Reservations of a lower
        /// <paramref name="rank"/> are filled first (the spawn order sends professionals, rank 0, before levies, rank 1: PROTOCOL.md §3.6).
        /// </summary>
        public void Add(TKey key, string group, int count, int rank = 0)
        {
            if (count <= 0) return;
            if (!_reservations.TryGetValue(key, out List<Reservation> list)) _reservations[key] = list = new List<Reservation>();
            list.Add(new Reservation { Group = group, Requested = count, Rank = rank });
        }

        /// <summary>
        /// Takes the group of the next agent of <paramref name="key"/>. Returns false when no reservation is left
        /// (an agent the request did not ask for).
        /// </summary>
        public bool TryTake(TKey key, out string group)
        {
            group = null;
            if (!_reservations.TryGetValue(key, out List<Reservation> list)) return false;
            Reservation best = null;
            foreach (Reservation r in list)
            {
                if (r.Taken >= r.Requested) continue;
                // Lowest rank first; within a rank r.Taken / r.Requested < best.Taken / best.Requested, compared without division;
                // strict, so ties keep request order.
                if (best == null || r.Rank < best.Rank
                    || (r.Rank == best.Rank && (long)r.Taken * best.Requested < (long)best.Taken * r.Requested)) best = r;
            }
            if (best == null) return false;
            best.Taken++;
            group = best.Group;
            return true;
        }
    }
}
