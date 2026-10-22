// Port of mapping_resolver.js casualtiesToCK3: Bannerlord group results -> CK3 fatal losses.
// CK3 semantics: only fatal casualties are permanent; routed men return to the army after the battle. The reference uses CK3's
// constants (main-phase conversion 0.3, pursuit base 0.05); here they are parameters so MapperOptions can tune them:
//   conversion (winner / loser) = MapperOptions.KilledToCk3DeadShareWinner / ...Loser   (replaces BASE_RATIO_CASUALTIES_CONVERSION)
//   pursuit base                = MapperOptions.RoutedToCk3DeadShareLoser               (replaces BASE_TOUGHNESS_TO_PURSUIT)
// Terrain hard_casualty_modifier / retreat_losses and the harsh-winter x1.2 apply exactly as in the reference. The knights pass
// (PROTOCOL §13) adds the side commanders' casualty traits as factors on each side's fatal share and on the loser's
// pursuit losses, and the accolade / army-owner pursuit and screen changes through the groups' Pursuit and Screen.
// Bannerlord "killed" and "wounded" (unconscious) agents are both casualties: the module's own killed-vs-unconscious roll
// (its IAgentStateDecider, troopDeathChance) only changes what the player sees, never what CK3 loses, so the conversion is
// always the configured one and never flips with the random split.
using CK3xBL.Mapping.Data;

namespace CK3xBL.Mapping.Resolution;

/// <summary>One CK3 group (levy pool or MaA regiment) with what Bannerlord did to its agents.</summary>
public sealed class CasualtyGroup
{
    public required string Id { get; init; }
    /// <summary>CK3 men the group stands for.</summary>
    public required int Soldiers { get; init; }
    /// <summary>Agents requested for the group (unspawned agents count as survivors).</summary>
    public required int Agents { get; init; }
    public int Killed { get; init; }
    public int Unconscious { get; init; }
    public int Fled { get; init; }
    /// <summary>CK3 per-soldier toughness (levy 10).</summary>
    public double Toughness { get; init; } = 10;
    public double Pursuit { get; init; }
    public double Screen { get; init; }
}

public sealed record GroupCasualties(string Id, int Casualties, int Fatal, int Routed);

public sealed record CasualtyOutcome(double PursuitFraction, IReadOnlyList<GroupCasualties> Winner, IReadOnlyList<GroupCasualties> Loser);

public sealed record CasualtySettings
{
    /// <summary>The loser had no agent standing or fled at the end: every casualty is fatal (early-phase defeat rule).</summary>
    public bool Annihilated { get; init; }
    public string? Terrain { get; init; }
    public bool HarshWinter { get; init; }
    public bool LoserIsDefender { get; init; }
    public double WinnerConversion { get; init; } = 0.3;
    public double LoserConversion { get; init; } = 0.3;
    public double PursuitBase { get; init; } = 0.05;
    /// <summary>Factors on each side's fatal share (own hard_casualty_modifier x enemy enemy_hard_casualty_modifier traits).</summary>
    public double WinnerFatalShareMult { get; init; } = 1;
    public double LoserFatalShareMult { get; init; } = 1;
    /// <summary>Factor on the loser's pursuit losses (its commander's retreat_losses traits).</summary>
    public double LoserRetreatLossMult { get; init; } = 1;
}

public static class CasualtyModel
{
    public static CasualtyOutcome Convert(MappingJson m, IReadOnlyList<CasualtyGroup> winnerGroups, IReadOnlyList<CasualtyGroup> loserGroups, CasualtySettings o)
    {
        var tm = o.Terrain is not null ? m.Casualties.FatalRatioModifiers.Terrain.GetValueOrDefault(o.Terrain) : null;
        Dictionary<string, double> SideMods(bool isDefender) => (isDefender ? tm?.Defender : tm?.Attacker) ?? new Dictionary<string, double>();
        double Ratio(bool isDefender, double share) =>
            share * (1 + SideMods(isDefender).GetValueOrDefault("hard_casualty_modifier")) * (o.HarshWinter ? 1.2 : 1);

        double conv = Ratio(!o.LoserIsDefender, o.WinnerConversion) * o.WinnerFatalShareMult;
        (int Cas, int Fatal) Main(CasualtyGroup g)
        {
            int cas = g.Agents > 0 ? JsCompat.RoundToInt((g.Killed + g.Unconscious) / (double)g.Agents * g.Soldiers) : 0;
            return (cas, JsCompat.RoundToInt(cas * conv));
        }

        var win = winnerGroups.Select(g => { var r = Main(g); return new GroupCasualties(g.Id, r.Cas, r.Fatal, r.Cas - r.Fatal); }).ToList();
        static int Alive(CasualtyGroup g) =>
            Math.Max(0, g.Soldiers - (g.Agents > 0 ? JsCompat.RoundToInt((g.Killed + g.Unconscious + g.Fled) / (double)g.Agents * g.Soldiers) : 0));
        double pursuit = winnerGroups.Sum(g => Alive(g) * g.Pursuit);
        double screen = loserGroups.Sum(g => Alive(g) * g.Screen);

        conv = Ratio(o.LoserIsDefender, o.LoserConversion) * o.LoserFatalShareMult;
        var pre = loserGroups.Select(g =>
        {
            var r = Main(g);
            int routed = r.Cas - r.Fatal + (g.Agents > 0 ? JsCompat.RoundToInt(g.Fled / (double)g.Agents * g.Soldiers) : 0);
            return (G: g, R: r, Routed: routed);
        }).ToList();
        double toughness = pre.Sum(x => x.Routed * (x.G.Toughness > 0 ? x.G.Toughness : 10));
        double retreat = SideMods(o.LoserIsDefender).GetValueOrDefault("retreat_losses");
        double q = o.Annihilated ? 1 : Math.Min(1, Math.Max(0.01, (o.PursuitBase + 0.5 * Math.Max(0, pursuit - screen) / Math.Max(1, toughness)) * (1 + retreat) * o.LoserRetreatLossMult));
        var lose = pre.Select(x => new GroupCasualties(x.G.Id, x.R.Cas, Math.Min(x.G.Soldiers, x.R.Fatal + JsCompat.RoundToInt(x.Routed * q)), x.Routed)).ToList();
        return new CasualtyOutcome(JsCompat.R2(q), win, lose);
    }
}
