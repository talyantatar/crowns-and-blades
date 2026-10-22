// Per-group Bannerlord results for one side. PROTOCOL §3.5 keys result groups by TroopRequest.group; older modules keyed them by
// troop id, and a module that tracks nothing still reports side totals. All three are accepted, best first:
//   1. group keys present          -> exact per-group numbers (a missing group had no casualties);
//   2. troop ids present           -> each troop's numbers split over the groups that requested it, by requested count;
//   3. only side totals            -> split over all groups by requested count (fallback).
using CK3xBL.Protocol;

namespace CK3xBL.Mapping.Outcome;

public sealed record GroupStats(int Requested, int Spawned, int Killed, int Wounded, int Routed)
{
    public int Downed => Killed + Wounded;
}

public static class GroupResults
{
    public static Dictionary<string, GroupStats> Resolve(SideRequest request, SideResult? result)
    {
        var requested = request.Troops.Where(t => !string.IsNullOrEmpty(t.Group))
            .GroupBy(t => t.Group!).ToDictionary(g => g.Key, g => g.Sum(t => Math.Max(0, t.Count)), StringComparer.Ordinal);
        var stats = new Dictionary<string, GroupStats>(StringComparer.Ordinal);
        var groups = result?.Groups ?? new Dictionary<string, GroupResult>();

        if (result is null)
        {
            foreach (var (g, n) in requested) stats[g] = new GroupStats(n, 0, 0, 0, 0);
            return stats;
        }

        if (requested.Keys.Any(groups.ContainsKey))
        {
            foreach (var (g, n) in requested)
                stats[g] = groups.TryGetValue(g, out var r) ? FromShares(n, new double[] { r.Spawned, r.Killed, r.Wounded, r.Routed }) : new GroupStats(n, n, 0, 0, 0);
            return stats;
        }

        var troopIds = request.Troops.Select(t => t.Id).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        if (troopIds.Count > 0 && troopIds.Any(groups.ContainsKey))
        {
            var acc = requested.Keys.ToDictionary(g => g, _ => new double[4], StringComparer.Ordinal);
            foreach (var byTroop in request.Troops.Where(t => !string.IsNullOrEmpty(t.Group)).GroupBy(t => t.Id))
            {
                if (!groups.TryGetValue(byTroop.Key, out var r)) continue;
                double total = byTroop.Sum(t => Math.Max(0, t.Count));
                if (total <= 0) continue;
                foreach (var t in byTroop)
                {
                    double share = Math.Max(0, t.Count) / total;
                    var a = acc[t.Group!];
                    a[0] += r.Spawned * share; a[1] += r.Killed * share; a[2] += r.Wounded * share; a[3] += r.Routed * share;
                }
            }
            foreach (var (g, n) in requested) stats[g] = FromShares(n, acc[g]);
            return stats;
        }

        double all = requested.Values.Sum();
        foreach (var (g, n) in requested)
        {
            double share = all > 0 ? n / all : 0;
            stats[g] = FromShares(n, new[] { result.Spawned * share, result.Killed * share, result.Wounded * share, result.Routed * share });
        }
        return stats;
    }

    private static GroupStats FromShares(int requested, double[] a)
    {
        int Clamp(double v) => Math.Clamp((int)Math.Round(v, MidpointRounding.AwayFromZero), 0, requested);
        int killed = Clamp(a[1]), wounded = Math.Min(Clamp(a[2]), requested - killed);
        int routed = Math.Min(Clamp(a[3]), requested - killed - wounded);
        return new GroupStats(requested, Clamp(a[0]), killed, wounded, routed);
    }
}
