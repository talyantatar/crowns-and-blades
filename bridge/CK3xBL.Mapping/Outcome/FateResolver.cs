// What happens in CK3 to a hero who went down in Bannerlord. The module's IAgentStateDecider splits downed heroes into "killed"
// and "wounded" (unconscious) at random (heroDeathChance, default 0.3), and custom battles without it kill every downed agent
// (seen in a live test). Neither split is CK3's: both states mean "fell in battle", and CK3's own odds decide the rest. Weights are
// CK3 1.20's combat phase events (common/combat_phase_events/00_knight_phase_events.txt and 00_commander_phase_events.txt),
// given that something happened:
//   knight:    wounded 100 x f40, maimed 40 x f30, killed 30 x f30 (x4 when already wounded_3)
//   commander: wounded  25 x f40, maimed 10 x f30, killed  5 x f30, and only if already wounded or martial and prowess <= 5
//   f40 = max(0.1, (40 - prowess) / 40), f30 = max(0.1, (30 - prowess) / 30); a side 5x stronger than the enemy suffers no
//   maiming or deaths ("vastly outnumbering" rule).
// On top: a downed commander of the losing side is captured with p = 0.35 when the battle is
// part of a war. With the knights pass (PROTOCOL §13) the weights are multiplied by FateFactors instead of the
// hard-coded wounded_3 rule: every held trait's CK3 phase-event factors (wounded_3 among them), the knight's own active accolade,
// enemy Blademaster / Crossbow Captain and own House Paragon accolades; the factors also scale the capture chance. The roll is
// conditional on the hero having fallen, so a factor that scales all three weights alike (brave x2) leaves the odds unchanged.
// The CK3 player's own Bannerlord fall is never a CK3 death or capture unless MapperOptions.PlayerDeathIsReal: it becomes a wound
// or a maiming by the same weights. A stand-in avatar (the commander the user plays when the ruler stays home) is
// an ordinary CK3 character and gets the ordinary odds. Rolls are seeded by battle id and character tag, so a re-applied result
// gives the same fates.
using CK3xBL.Core.Model;
using CK3xBL.Mapping.Resolution;

namespace CK3xBL.Mapping.Outcome;

public static class Fates
{
    public const int Unharmed = 0, Wounded = 1, Maimed = 2, Captured = 3, Killed = 4;
    /// <summary>PROTOCOL §32: two wound levels at once (the CK3 player's fall in Bannerlord, 10 %).</summary>
    public const int SevereWound = 5;
    /// <summary>The order of gravity (the keypad's character slots keep the gravest): a severe wound ranks over a wound, under a maiming.</summary>
    public static double Gravity(int fate) => fate == SevereWound ? 1.5 : fate;
}

public sealed class FateContext
{
    public required int Bid { get; init; }
    public required CharacterSnapshot Character { get; init; }
    /// <summary>The hero rolls with the commander table (army commanders and the side general).</summary>
    public required bool AsCommander { get; init; }
    /// <summary>The hero is the CK3 player character himself (HeroSpec role "player"), not a stand-in.</summary>
    public required bool IsCk3Player { get; init; }
    public required bool PlayerDeathIsReal { get; init; }
    public required bool OnLosingSide { get; init; }
    public required bool AtWar { get; init; }
    /// <summary>CK3 soldiers of the hero's side and of the enemy side.</summary>
    public required int OwnStrength { get; init; }
    public required int EnemyStrength { get; init; }
    /// <summary>Knights pass factors on the CK3 weights and the capture chance; null = today's rule (old mod, PROTOCOL §13.1).</summary>
    public FateFactors? Factors { get; init; }
}

/// <summary>Multipliers on the CK3 fate weights of one downed hero, and on his capture chance.</summary>
public sealed record FateFactors(double Wounded, double Maimed, double Killed, double Capture);

public static class FateResolver
{
    public const double CaptureChance = 0.35;

    /// <summary>The CK3 fate of a hero Bannerlord reported "killed" or "wounded".</summary>
    public static int Downed(FateContext ctx)
    {
        var c = ctx.Character;
        double p = c.Prowess;
        double f40 = Math.Max(0.1, (40 - p) / 40), f30 = Math.Max(0.1, (30 - p) / 30);
        bool alreadyWounded = c.Traits.Any(t => t.StartsWith("wounded", StringComparison.Ordinal));
        double w, m, k;
        if (ctx.AsCommander)
        {
            w = 25 * f40; m = 10 * f30; k = 5 * f30;
            if (!alreadyWounded && !(c.Martial <= 5 && c.Prowess <= 5)) k = 0;
        }
        else
        {
            w = 100 * f40; m = 40 * f30; k = 30 * f30;
            if (ctx.Factors is null && c.Traits.Contains("wounded_3")) k *= 4;
        }
        if (ctx.Factors is { } f)
        {
            w *= f.Wounded;
            m *= f.Maimed;
            k *= f.Killed;
        }
        if (ctx.EnemyStrength > 0 && ctx.OwnStrength > 5 * ctx.EnemyStrength) { m = 0; k = 0; }
        bool protectedPlayer = ctx.IsCk3Player && !ctx.PlayerDeathIsReal;
        if (protectedPlayer) k = 0;

        double u = JsCompat.Hash01($"{ctx.Bid}|{c.Tag}|fate") * (w + m + k);
        int fate = u < k ? Fates.Killed : u < k + m ? Fates.Maimed : Fates.Wounded;
        if (fate != Fates.Killed && ctx.AsCommander && ctx.OnLosingSide && ctx.AtWar && !protectedPlayer &&
            JsCompat.Hash01($"{ctx.Bid}|{c.Tag}|capture") < CaptureChance * (ctx.Factors?.Capture ?? 1))
            fate = Fates.Captured;
        return fate;
    }
}
