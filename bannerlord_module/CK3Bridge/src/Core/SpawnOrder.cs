using System;
using System.Collections.Generic;

namespace CK3Bridge
{
    /// <summary>
    /// Spawn order of a side (PROTOCOL.md §3.6): heroes first, then the professionals (men-at-arms and knight retinues) of every
    /// group interleaved in proportion, the levies last, again interleaved. Bannerlord fills the field from the top of this order
    /// and sends the rest as reinforcements, so levies only take the field when too few professionals are left to fill it.
    /// Pure arithmetic: CK3SpawnOrder puts the priorities into the stock CustomBattleTroopSupplier queue (highest first).
    /// </summary>
    public static class SpawnOrder
    {
        public const float HeroPriority = 100f;
        private const float ProfessionalBase = 2f, LevyBase = 1f;

        public static bool IsLevy(string group) => group != null && group.StartsWith("levy#", StringComparison.Ordinal);

        /// <summary>Priority of the <paramref name="index"/>-th agent (0-based) of a group of <paramref name="size"/>: within
        /// (base, base + 1), falling evenly through the group, so every group's agents are spread over the whole tier.</summary>
        public static float Priority(bool levy, int index, int size) =>
            (levy ? LevyBase : ProfessionalBase) + 1f - (index + 0.5f) / Math.Max(1, size);

        /// <summary>
        /// The priority of each agent of the troop entries (character key, group, count), in entry order; <paramref name="available"/>
        /// caps how many agents of each character the supplier holds (what it holds beyond the entries is the caller's: heroes).
        /// </summary>
        public static List<KeyValuePair<TKey, float>> Plan<TKey>(IList<KeyValuePair<TKey, KeyValuePair<string, int>>> entries, IDictionary<TKey, int> available)
        {
            var left = new Dictionary<TKey, int>(available);
            var takes = new int[entries.Count];
            var size = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < entries.Count; i++)
            {
                TKey key = entries[i].Key;
                string group = entries[i].Value.Key ?? "";
                int have = left.TryGetValue(key, out int n) ? n : 0;
                takes[i] = Math.Max(0, Math.Min(entries[i].Value.Value, have));
                left[key] = have - takes[i];
                size[group] = (size.TryGetValue(group, out int s) ? s : 0) + takes[i];
            }
            var next = new Dictionary<string, int>(StringComparer.Ordinal);
            var plan = new List<KeyValuePair<TKey, float>>();
            for (int i = 0; i < entries.Count; i++)
            {
                string group = entries[i].Value.Key ?? "";
                bool levy = IsLevy(group);
                for (int k = 0; k < takes[i]; k++)
                {
                    int j = next.TryGetValue(group, out int x) ? x : 0;
                    next[group] = j + 1;
                    plan.Add(new KeyValuePair<TKey, float>(entries[i].Key, Priority(levy, j, size[group])));
                }
            }
            return plan;
        }
    }
}
