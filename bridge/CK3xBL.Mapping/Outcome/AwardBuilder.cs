// What each hero earned in Bannerlord, written back to CK3 through the award slots (PROTOCOL §13.5).
// Only for outcomes 1..3, both sides, heroes with a tag that took the field. "Downs" are the enemy heroes this hero struck down
// (their killedBy), each with the CK3 fate the outcome gives it; troop kills are his kills minus heroes he downed.
//   Prestige  per down 150 x max(1, victim tier) if the victim's CK3 fate is death, else 75 x max(1, victim tier), halved for a
//             lowborn victim (no house), as vanilla knight_prestige_gain_on_kill / _on_wound_inverse; + min(100, 2 x troop kills);
//             at most 1000, rounded to 25. Never for the CK3 player in person (he already gets the battle's prestige, r7).
//   Glory     acclaimed knights only: per down +25 (killed) or +10; + min(25, round(2 sqrt(troop kills))); +25 when his own fate is
//             death (his successor keeps it); 0..100, rounded to 5. Battle-outcome glory is vanilla and never sent.
//   Prowess   rolls of knight_increase_prowess_chance_effect: one per down, one more at 10 and at 30 troop kills, at most 3.
// Amounts are INFERRED calibration constants, modelled on the vanilla analogues.
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Model;
using CK3xBL.Core.Texts;
using CK3xBL.Mapping.Resolution;
using CK3xBL.Protocol;

namespace CK3xBL.Mapping.Outcome;

/// <summary>One hero of the request with his Bannerlord result and CK3 character (null when the snapshot does not list him).</summary>
public sealed record AwardHero(SideKey Side, HeroSpec Spec, HeroResult Result, CharacterSnapshot? Character, bool PlayerSide);

public static class AwardBuilder
{
    public const int PrestigePerKilledVictim = 150, PrestigePerHarmedVictim = 75, PrestigePerTroopKill = 2, TroopPrestigeCap = 100;
    public const int MaxPrestige = 1000, PrestigeStep = 25;
    public const int GloryPerKilledVictim = 25, GloryPerHarmedVictim = 10, TroopGloryCap = 25, GloryForOwnDeath = 25;
    public const int MaxGlory = 100, GloryStep = 5;
    public const int MaxProwessRolls = 3;

    /// <summary>The awards, player side first, then by 10 x glory + prestige + 50 x rolls, then tag; empty awards left out.</summary>
    public static List<CharacterAward> Build(IReadOnlyList<AwardHero> heroes, IReadOnlyList<CharacterFate> fates)
    {
        int FateOf(int tag) => fates.FirstOrDefault(f => f.Tag == tag)?.Fate ?? Fates.Unharmed;
        var awards = new List<CharacterAward>();
        foreach (var h in heroes)
        {
            if (h.Spec.Tag <= 0 || string.IsNullOrEmpty(h.Spec.Key) || (h.Result.State ?? "").ToLowerInvariant() == "unspawned") continue;
            var downed = heroes.Where(v => !ReferenceEquals(v, h) && OutcomeBuilder.IsDowned(v.Result) && v.Result.KilledBy == h.Spec.Key).ToList();
            var downs = downed.Where(v => v.Side != h.Side).ToList();
            int friendlyDowns = downed.Count - downs.Count;
            int troopKills = Math.Max(0, h.Result.Kills - downs.Count - friendlyDowns);

            double prestige = 0;
            int glory = 0;
            foreach (var v in downs)
            {
                bool killed = v.Spec.Tag > 0 && FateOf(v.Spec.Tag) == Fates.Killed;
                double gain = (killed ? PrestigePerKilledVictim : PrestigePerHarmedVictim) * Math.Max(1, v.Character?.Tier ?? 0);
                // A victim without a house is lowborn (ck3xbl_c_has_house = 0); a landless noble keeps the full value.
                if (v.Character is { HouseName: null }) gain /= 2;
                prestige += gain;
                glory += killed ? GloryPerKilledVictim : GloryPerHarmedVictim;
            }
            prestige += Math.Min(TroopPrestigeCap, PrestigePerTroopKill * troopKills);
            bool ck3Player = h.Spec.IsPlayer && h.Spec.Role == "player";
            int prestigeAward = ck3Player ? 0 : JsCompat.RoundToInt(Math.Min(MaxPrestige, prestige) / PrestigeStep) * PrestigeStep;

            int gloryAward = 0;
            if (h.Character?.Accolade is not null)
            {
                glory += Math.Min(TroopGloryCap, JsCompat.RoundToInt(2 * Math.Sqrt(troopKills)));
                if (FateOf(h.Spec.Tag) == Fates.Killed) glory += GloryForOwnDeath;
                gloryAward = JsCompat.RoundToInt(Math.Min(MaxGlory, glory) / (double)GloryStep) * GloryStep;
            }

            int rolls = Math.Min(MaxProwessRolls, downs.Count + (troopKills >= 10 ? 1 : 0) + (troopKills >= 30 ? 1 : 0));
            if (prestigeAward == 0 && gloryAward == 0 && rolls == 0) continue;
            awards.Add(new CharacterAward
            {
                Tag = h.Spec.Tag, Ck3Id = h.Spec.Ck3Id, Prestige = prestigeAward, Glory = gloryAward, ProwessRolls = rolls,
                Name = h.Spec.Name ?? "", PlayerSide = h.PlayerSide,
            });
        }
        return awards
            .OrderByDescending(a => a.PlayerSide)
            .ThenByDescending(a => 10 * a.Glory + a.Prestige + 50 * a.ProwessRolls)
            .ThenBy(a => a.Tag)
            .ToList();
    }

    /// <summary>The summary's renown line for up to two player-side awards ("Renown: Sir Odo (+325 prestige, +30 glory)."), or null.</summary>
    public static string? RenownLine(IEnumerable<CharacterAward> awards, Words? w = null)
    {
        w ??= Words.English;
        var parts = awards.Where(a => a.PlayerSide).Take(2).Select(a =>
        {
            var gains = new List<string>();
            if (a.Prestige > 0) gains.Add(w.Prestige(a.Prestige));
            if (a.Glory > 0) gains.Add(w.Glory(a.Glory));
            if (a.ProwessRolls > 0) gains.Add(w.ProwessChances(a.ProwessRolls));
            return $"{a.Name} ({string.Join(", ", gains)})";
        }).ToList();
        return parts.Count == 0 ? null : w.Renown(string.Join("; ", parts));
    }
}
