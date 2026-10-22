// The strength balance (PROTOCOL §17): a straight clash in Bannerlord should end as CK3 would decide it, so the side that Bannerlord
// would make weaker than CK3 does gets damage and health, and the player's command is what tips it.
//   CK3         S = (sum men x damage) x (sum men x toughness) over the side's levies and men-at-arms (base stats of the catalog)
//   Bannerlord  S = (sum agents x worth)^2, Lanchester's square law: worth = 10 x 2^((tier - 1) / k) of the group's troops
//               (k = tiers per doubling, calibrated in play) x sqrt(the group's own CK3 damage x health factors)
//   Factor      f = (CK3 attacker / defender) / (Bannerlord attacker / defender); f > 1 strengthens the attacker by f, else the
//               defender by 1 / f, at most StrengthMaxFactor: damage dealt x sqrt(f), damage taken / sqrt(f).
// Heroes and knight retinues are left out on both sides (their own buffs carry them). Levy worth 10 at tier 1 matches CK3's levy
// (damage 10, toughness 10), so with k = 2 (the tier rule's own scale) the two estimates agree and f stays near 1.
using CK3xBL.Core.Abstractions;
using CK3xBL.Mapping.Data;
using CK3xBL.Protocol;

namespace CK3xBL.Mapping.Planning;

public static class StrengthBalance
{
    /// <summary>CK3 strength of a side: (sum men x damage) x (sum men x toughness).</summary>
    public static double Ck3Strength(MappingTables t, SidePlan side)
    {
        double damage = 0, toughness = 0;
        foreach (var g in side.Groups)
        {
            var (d, tough) = g.Key.Kind == GroupKind.Levy
                ? (t.Ck3.Levy.Damage, t.Ck3.Levy.Toughness)
                : t.Ck3.MaaTypes.GetValueOrDefault(g.Key.MaaKey!) is { } m ? (m.Damage, m.Toughness) : (t.Ck3.Levy.Damage, t.Ck3.Levy.Toughness);
            damage += g.Soldiers * d;
            toughness += g.Soldiers * tough;
        }
        return damage * toughness;
    }

    /// <summary>What one agent of a group is worth, in CK3 levy units (a tier 1 troop = 10).</summary>
    public static double AgentWorth(MappingTables t, PlannedGroup g, double tiersPerDoubling)
    {
        double weight = 0, tier = 0;
        foreach (var tw in g.Troops)
        {
            int troopTier = t.Troops.GetValueOrDefault(tw.Troop)?.Tier ?? 0;
            if (troopTier <= 0) continue;
            weight += tw.Weight;
            tier += tw.Weight * troopTier;
        }
        double mean = weight > 0 ? tier / weight : 1;
        return 10 * Math.Pow(2, (mean - 1) / tiersPerDoubling) * Math.Sqrt(g.StatDamage * g.StatHealth);
    }

    /// <summary>Estimated Bannerlord strength of a side: (sum agents x worth)^2.</summary>
    public static double BannerlordStrength(MappingTables t, SidePlan side, double tiersPerDoubling)
    {
        double sum = side.Groups.Sum(g => g.Agents * AgentWorth(t, g, tiersPerDoubling));
        return sum * sum;
    }

    /// <summary>Computes the balance of a planned battle and applies it to the request's side buffs.</summary>
    public static BalanceInfo Apply(MappingTables t, SidePlan attacker, SidePlan defender, BattleRequest request, MapperOptions options)
    {
        double k = options.StrengthTiersPerDoubling > 0 ? options.StrengthTiersPerDoubling : 3;
        var info = new BalanceInfo
        {
            Ck3Attacker = Ck3Strength(t, attacker), Ck3Defender = Ck3Strength(t, defender),
            BannerlordAttacker = BannerlordStrength(t, attacker, k), BannerlordDefender = BannerlordStrength(t, defender, k),
            TiersPerDoubling = k,
        };
        double f = Factor(info.Ck3Attacker, info.Ck3Defender, info.BannerlordAttacker, info.BannerlordDefender);
        if (!options.StrengthBalance || !double.IsFinite(f) || Math.Abs(f - 1) < 0.02) return info;

        bool boostAttacker = f > 1;
        double strength = boostAttacker ? f : 1 / f;
        double max = Math.Clamp(options.StrengthMaxFactor, 1, 4);
        info.Capped = strength > max;
        strength = Math.Min(strength, max);
        info.Factor = Math.Round(strength, 3);
        info.Boosted = boostAttacker ? "attacker" : "defender";

        var side = boostAttacker ? request.Attacker : request.Defender;
        var b = side.Buffs ?? new SideBuffs();
        double root = Math.Sqrt(strength);
        // The commander-trait buffs stay; the product keeps inside the module's clamps (0.25..4 per value, 3 per hit).
        b.DamageDealtMult = (float)Math.Clamp(b.DamageDealtMult * root, 0.25, 3);
        b.DamageTakenMult = (float)Math.Clamp(b.DamageTakenMult / root, 0.25, 4);
        side.Buffs = b;
        return info;
    }

    /// <summary>f = (CK3 attacker / defender) / (Bannerlord attacker / defender); 1 when a side has nothing.</summary>
    public static double Factor(double ck3Att, double ck3Def, double blAtt, double blDef)
    {
        if (ck3Att <= 0 || ck3Def <= 0 || blAtt <= 0 || blDef <= 0) return 1;
        return ck3Att / ck3Def / (blAtt / blDef);
    }
}
