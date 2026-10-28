// What a commander or knight gives the troops of the formation he leads (PROTOCOL §49, the user's scheme of 2026-10-28), like a
// Bannerlord captain's perks: his CK3 skills, traits and accolade each add something of their own, so no two captains are alike.
//   Skills    prowess: melee of the formation; stewardship: its health; diplomacy: steadier as comrades fall; intrigue: straighter shots
//   Traits    blademaster: one-handed; hunter: bows; tourney horseman: charge and horse speed; strategist, unyielding defender: morale;
//             organizer: foot speed; reckless: harder hits, more taken; cautious leader: less taken; unyielding defender: sturdier
//             shields; aggressive attacker: charge; flexible leader: flank and rear hits
//   Accolade  by the men-at-arms type it leads, 5..15 % over ranks 1..6 (heavy cavalry's charge 8..25 %)
// Every effect is capped at +25 % (or -25 % for what is taken), and several captains of one formation give the strongest of each
// effect, not their product (the module's BuffMath.Strongest).
using CK3xBL.Core.Model;
using CK3xBL.Protocol;

namespace CK3xBL.Mapping.Buffs;

public static class CaptainAura
{
    public const double Cap = 0.25;
    public const double ProwessMeleePerPoint = 0.002, ProwessMeleeMax = 0.06;
    public const double StewardshipHealthPerPoint = 0.005, StewardshipHealthMax = 0.08;
    public const double DiplomacyMoraleLossPerPoint = 0.02, DiplomacyMoraleLossMax = 0.25;
    public const double IntrigueAimPerPoint = 0.005, IntrigueAimMax = 0.10;
    public const float FallMoraleLoss = 5f;                  // a captain without an accolade still shakes his men when he falls

    /// <summary>An accolade's strength at rank 1..6: 5 % at rank 1, 15 % at rank 6.</summary>
    public static double AccoladeStrength(int rank) => 0.05 + 0.02 * (Math.Clamp(rank, 1, 6) - 1);

    /// <summary>
    /// The aura of <paramref name="c"/> over <paramref name="formation"/>; <paramref name="accoladeBaseType"/> is the men-at-arms base
    /// type his accolade leads (null without one). Null when nothing of his would change the troops.
    /// </summary>
    public static AuraSpec? For(CharacterSnapshot c, string formation, string? accoladeBaseType, int accoladeRank)
    {
        bool mounted = formation is "Cavalry" or "HorseArcher";
        var a = new Acc();

        // ---- skills
        a.Melee += Math.Min(ProwessMeleeMax, ProwessMeleePerPoint * Math.Max(0, c.Prowess));
        a.Health += Math.Min(StewardshipHealthMax, StewardshipHealthPerPoint * Math.Max(0, c.Stewardship));
        a.MoraleLoss -= Math.Min(DiplomacyMoraleLossMax, DiplomacyMoraleLossPerPoint * Math.Max(0, c.Diplomacy));
        a.Aim -= Math.Min(IntrigueAimMax, IntrigueAimPerPoint * Math.Max(0, c.Intrigue));

        // ---- traits
        int blade = Level(c, "lifestyle_blademaster");
        if (blade > 0) a.OneHanded += 0.02 * Math.Min(blade, 3);
        int hunter = Level(c, "lifestyle_hunter");
        if (hunter > 0) a.Bow += 0.03 * Math.Min(hunter, 3);
        if (Level(c, "tourney_participant:horse") > 0)
        {
            a.Charge += 0.05;
            if (mounted) a.Speed += 0.03;
        }
        if (Has(c, "strategist")) a.Morale += 5;
        if (Has(c, "organizer") && !mounted) a.Speed += 0.04;
        if (Has(c, "reckless"))
        {
            a.Melee += 0.08;
            a.Ranged += 0.08;
            a.Taken += 0.05;
        }
        if (Has(c, "cautious_leader")) a.Taken -= 0.06;
        if (Has(c, "unyielding_defender"))
        {
            a.Shield -= 0.10;
            a.Morale += 5;
        }
        if (Has(c, "aggressive_attacker")) a.Charge += 0.10;
        if (Has(c, "flexible_leader")) a.Flank += 0.08;

        // ---- accolade
        if (accoladeBaseType is not null && accoladeRank > 0)
        {
            double s = AccoladeStrength(accoladeRank);
            switch (accoladeBaseType)
            {
                case "heavy_infantry":
                    a.OneHanded += s;
                    a.Shield -= s;
                    break;
                case "pikemen":
                    a.Polearm += s;
                    a.VsCavalry += s;
                    break;
                case "archers":
                    a.Bow += s;
                    a.Aim -= s;
                    break;
                case "skirmishers":
                    a.Throwing += s;
                    a.Speed += 0.03 + 0.01 * (Math.Clamp(accoladeRank, 1, 6) - 1);
                    break;
                case "light_cavalry" or "camel_cavalry" or "elephant_cavalry":
                    a.Melee += s;
                    a.Speed += 0.03 + 0.01 * (Math.Clamp(accoladeRank, 1, 6) - 1);
                    break;
                case "heavy_cavalry":
                    a.Charge += 0.08 + 0.034 * (Math.Clamp(accoladeRank, 1, 6) - 1);
                    a.Melee += s;
                    break;
                case "archer_cavalry":
                    a.Ranged += s;
                    a.Aim -= s;
                    break;
                case "gunpowder":
                    a.Crossbow += s;
                    break;
            }
            a.Morale += KnightBuffPlanner.AuraMoralePerRank * accoladeRank;
        }

        var aura = new AuraSpec
        {
            Formation = formation,
            MeleeDamageMult = F(a.Melee),
            RangedDamageMult = F(a.Ranged),
            ChargeDamageMult = F(a.Charge),
            DamageTakenMult = F(a.Taken),
            RangedInaccuracyMult = F(a.Aim),
            InitialMoraleAdd = (float)Math.Clamp(a.Morale, 0, 30),
            OneHandedMult = F(a.OneHanded),
            TwoHandedMult = F(a.TwoHanded),
            PolearmMult = F(a.Polearm),
            BowMult = F(a.Bow),
            CrossbowMult = F(a.Crossbow),
            ThrowingMult = F(a.Throwing),
            VsCavalryMult = F(a.VsCavalry),
            FlankDamageMult = F(a.Flank),
            ShieldDamageTakenMult = F(a.Shield),
            SpeedMult = F(a.Speed),
            HealthMult = F(a.Health),
            MoraleLossMult = F(a.MoraleLoss),
        };
        if (IsNeutral(aura)) return null;
        aura.MoraleLoss = accoladeRank > 0 ? Math.Max(FallMoraleLoss, KnightBuffPlanner.AuraMoralePerRank * accoladeRank) : FallMoraleLoss;
        return aura;
    }

    /// <summary>A trait's level: "trait" or "trait:track" from the logged XP levels, else 1 for a held trait, else 0.</summary>
    public static int Level(CharacterSnapshot c, string key)
    {
        if (c.TraitLevels.TryGetValue(key, out int level)) return level;
        return key.Contains(':') ? 0 : Has(c, key) ? 1 : 0;
    }

    private static bool Has(CharacterSnapshot c, string trait) =>
        c.CommanderTraits.Contains(trait) || c.CombatTraits.Contains(trait) || c.Traits.Contains(trait) || c.TraitLevels.ContainsKey(trait);

    /// <summary>An effect as a factor: a gain at most +25 %, a reduction at most -25 %.</summary>
    private static float F(double add) => (float)(1 + Math.Clamp(add, -Cap, Cap));

    private static bool IsNeutral(AuraSpec a) =>
        a.MeleeDamageMult == 1 && a.RangedDamageMult == 1 && a.ChargeDamageMult == 1 && a.DamageTakenMult == 1 && a.RangedInaccuracyMult == 1 &&
        a.InitialMoraleAdd == 0 && a.OneHandedMult == 1 && a.TwoHandedMult == 1 && a.PolearmMult == 1 && a.BowMult == 1 && a.CrossbowMult == 1 &&
        a.ThrowingMult == 1 && a.VsCavalryMult == 1 && a.FlankDamageMult == 1 && a.ShieldDamageTakenMult == 1 && a.SpeedMult == 1 &&
        a.HealthMult == 1 && a.MoraleLossMult == 1;

    private sealed class Acc
    {
        public double Melee, Ranged, Charge, Taken, Aim, Morale, OneHanded, TwoHanded, Polearm, Bow, Crossbow, Throwing, VsCavalry, Flank,
            Shield, Speed, Health, MoraleLoss;
    }
}
