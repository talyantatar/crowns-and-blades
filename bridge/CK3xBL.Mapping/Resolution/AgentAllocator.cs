// Port of mapping_resolver.js allocateAgents, plus two guards the reference leaves out:
//   * MinAgentsPerSide: a side whose share would fall below the floor gets its own scale (the casualty write-back uses each
//     group's own agents/soldiers, so a per-side scale never distorts CK3 losses; it only makes the small side relatively stronger);
//   * a hard cap: per-regiment minimums and agent multipliers (elephants x2) can push the total over the budget, so levy agents
//     of the over-represented side are trimmed until the budget holds.
namespace CK3xBL.Mapping.Resolution;

public sealed class AllocationGroup
{
    public required string Id { get; init; }
    public required bool IsLevy { get; init; }
    public required int Soldiers { get; init; }
    public double AgentMultiplier { get; init; } = 1;
    public int Agents { get; set; }
}

public sealed class AllocationSide
{
    public List<AllocationGroup> Groups { get; } = new();
    /// <summary>Soldiers per agent factor actually used for this side.</summary>
    public double Scale { get; set; }
    /// <summary>True when the side was raised to MinAgentsPerSide (its agent ratio then no longer mirrors CK3).</summary>
    public bool Floored { get; set; }
    public int Soldiers => Groups.Sum(g => g.Soldiers);
    public int Agents => Groups.Sum(g => g.Agents);
}

public static class AgentAllocator
{
    /// <summary>Common scale for both sides: min(1, budget / all soldiers).</summary>
    public static double BaseScale(int budget, int soldiers) => Math.Min(1, Math.Max(0, budget) / (double)Math.Max(1, soldiers));

    /// <param name="sides">Exactly two sides (attacker, defender).</param>
    /// <param name="maxTotal">Agent budget for both sides (MapperOptions.MaxAgentsTotal).</param>
    /// <param name="reserved">Agents already spent outside these groups (knight retinues).</param>
    /// <param name="minAgentsPerSide">Floor per side (capped by that side's soldiers).</param>
    public static void Allocate(IReadOnlyList<AllocationSide> sides, int maxTotal, int reserved, int minAgentsPerSide)
    {
        if (sides.Count != 2) throw new ArgumentException("exactly two sides expected", nameof(sides));
        int budget = Math.Max(0, maxTotal - reserved);
        int[] soldiers = { sides[0].Soldiers, sides[1].Soldiers };
        double s = BaseScale(budget, soldiers[0] + soldiers[1]);

        int[] floor = soldiers.Select(n => Math.Min(Math.Max(0, minAgentsPerSide), n)).ToArray();
        double[] scale = { s, s };
        bool[] floored = { false, false };
        for (int i = 0; i < 2; i++)
        {
            if (soldiers[i] > 0 && soldiers[i] * s < floor[i])
            {
                floored[i] = true;
                scale[i] = Math.Min(1, floor[i] / (double)soldiers[i]);
            }
        }
        if (floored[0] != floored[1])
        {
            int f = floored[0] ? 0 : 1, o = 1 - f;
            scale[o] = BaseScale(budget - floor[f], soldiers[o]);
            if (soldiers[o] > 0 && soldiers[o] * scale[o] < floor[o])
            {
                floored[o] = true;
                scale[o] = Math.Min(1, floor[o] / (double)soldiers[o]);
            }
        }

        for (int i = 0; i < 2; i++)
        {
            sides[i].Scale = scale[i];
            sides[i].Floored = floored[i];
            AllocateSide(sides[i].Groups, scale[i]);
        }

        if (!floored[0] && !floored[1]) RatioGuard(sides, soldiers);
        CapGuard(sides, budget, floor);
    }

    // mapping_resolver.js: per-group quota soldiers*s*multiplier, floor, at least 1 agent per non-empty regiment (levies may
    // drop to 0), then largest remainders until the side reaches round(sum of quotas).
    private static void AllocateSide(List<AllocationGroup> groups, double s)
    {
        var quotas = groups.Select(g => g.Soldiers * s * (g.AgentMultiplier > 0 ? g.AgentMultiplier : 1)).ToList();
        var rem = new double[groups.Count];
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            double exact = quotas[i];
            g.Agents = g.Soldiers > 0 ? Math.Max(g.IsLevy ? 0 : 1, (int)Math.Floor(exact)) : 0;
            rem[i] = exact - Math.Floor(exact);
        }
        int target = JsCompat.RoundToInt(quotas.Sum());
        int cur = groups.Sum(g => g.Agents);
        foreach (int i in Enumerable.Range(0, groups.Count).OrderByDescending(i => rem[i]))
        {
            if (cur >= target) break;
            if (groups[i].Soldiers > 0) { groups[i].Agents++; cur++; }
        }
    }

    // mapping_resolver.js ratio guard: per-regiment minimums can over-represent a small side; trim the first levy pool of the
    // over-represented side until the agent ratio is within 5% of the CK3 soldier ratio.
    private static void RatioGuard(IReadOnlyList<AllocationSide> sides, int[] soldiers)
    {
        if (soldiers[0] <= 0 || soldiers[1] <= 0) return;
        double want = soldiers[0] / (double)soldiers[1];
        for (int guard = 0; guard < 10000; guard++)
        {
            double have = sides[0].Agents / (double)Math.Max(1, sides[1].Agents);
            if (Math.Abs(have - want) / want <= 0.05) break;
            int big = have > want ? 0 : 1;
            var levy = sides[big].Groups.FirstOrDefault(g => g.IsLevy && g.Agents > 0);
            if (levy is null) break;
            levy.Agents--;
        }
    }

    private static void CapGuard(IReadOnlyList<AllocationSide> sides, int budget, int[] floor)
    {
        while (sides[0].Agents + sides[1].Agents > budget)
        {
            // Trim the side that is more generously represented (agents per soldier), never below its floor.
            var order = Enumerable.Range(0, 2)
                .Where(i => sides[i].Agents > floor[i])
                .OrderByDescending(i => sides[i].Agents / (double)Math.Max(1, sides[i].Soldiers))
                .ToList();
            bool trimmed = false;
            foreach (int i in order)
            {
                var victim = sides[i].Groups.Where(g => g.IsLevy && g.Agents > 0).OrderByDescending(g => g.Agents).FirstOrDefault()
                             ?? sides[i].Groups.Where(g => !g.IsLevy && g.Agents > 1).OrderByDescending(g => g.Agents).FirstOrDefault();
                if (victim is null) continue;
                victim.Agents--;
                trimmed = true;
                break;
            }
            if (!trimmed) return;
        }
    }
}
