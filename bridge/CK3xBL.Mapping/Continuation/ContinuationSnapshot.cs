// Second and later clashes of a steered CK3 combat (PROTOCOL §14.6): the live snapshot of the reinforcement
// clash, capped at the end state of the earlier clashes. CK3 inflicts the Bannerlord dead over several days, so when the
// reinforcement arrives the armies of the earlier clash may still hold men Bannerlord already killed. Each of them fights the new
// clash with at most its survivors in CK3 terms: levies = earlier levies - levy dead, each regiment = earlier current - its dead,
// from the earlier outcome's GroupLosses. Men knocked down but not killed fight again, exactly as the write-back counts them.
// The result is min(CK3 now, survivors) per group. Characters captured or killed in an earlier clash are left out (their fates are
// applied at the steering start, so CK3 normally no longer lists them; this is defense in depth). Armies the earlier clash did not
// have (the reinforcement, winner-side joiners) are taken as logged. Clash 3 nests the same way: its parent is clash 2, whose
// planned snapshot was already capped by clash 1.
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Json;
using CK3xBL.Core.Model;
using CK3xBL.Mapping.Outcome;
using CK3xBL.Mapping.Planning;

namespace CK3xBL.Mapping.Continuation;

public static class ContinuationSnapshot
{
    /// <summary>A copy of <paramref name="live"/> capped at the end state of <paramref name="parent"/>; <paramref name="live"/> is
    /// not changed.</summary>
    public static BattleSnapshot Cap(BattleSnapshot live, ParentLink parent)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(parent);
        // The archive's round trip (BattleArchive writes snapshots with ProtocolJson): a deep copy that keeps every logged field.
        var capped = ProtocolJson.Deserialize<BattleSnapshot>(ProtocolJson.Serialize(live))
                     ?? throw new InvalidOperationException($"battle {live.Bid}: the snapshot could not be copied");

        var dead = parent.Outcome.GroupLosses.GroupBy(g => g.GroupKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(x => Math.Max(0, x.Dead)), StringComparer.Ordinal);
        int Dead(GroupKey key) => dead.GetValueOrDefault(key.ToString());
        var earlier = parent.Planned.Sides.SelectMany(s => s.Armies).Where(a => a.Tag > 0)
            .GroupBy(a => a.Tag).ToDictionary(g => g.Key, g => g.First());
        var fallen = new HashSet<int>(parent.FallenTags);
        fallen.UnionWith(parent.Outcome.Characters.Where(c => c.Fate is Fates.Captured or Fates.Killed).Select(c => c.Tag));

        foreach (var side in capped.Sides)
        {
            int cut = 0;
            foreach (var army in side.Armies)
            {
                if (army.Tag <= 0 || !earlier.TryGetValue(army.Tag, out var before)) continue;
                army.Levies = Math.Min(army.Levies, Math.Max(0, before.Levies - Dead(GroupKey.Levy(army.Tag))));
                foreach (var reg in army.Regiments)
                {
                    var was = before.Regiments.FirstOrDefault(r => r.RegimentId == reg.RegimentId && r.MaaKey == reg.MaaKey);
                    if (was is not null)
                        reg.Current = Math.Min(reg.Current, Math.Max(0, was.Current - Dead(GroupKey.Maa(reg.MaaKey, army.Tag, reg.RegimentId))));
                }
                int size = Math.Max(0, army.Levies) + army.Regiments.Sum(r => Math.Max(0, r.Current));
                cut += army.Size - size;
                army.Size = size;
            }
            side.Size = Math.Max(0, side.Size - cut);
            side.Characters.RemoveAll(c => fallen.Contains(c.Tag));
        }
        return capped;
    }
}
