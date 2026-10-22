using System;
using System.Collections.Generic;
using CK3xBL.Protocol;

namespace CK3Bridge
{
    /// <summary>
    /// The formations a hero may be asked to captain (heroes[].captainOf, PROTOCOL.md §13.4 item 2). The values equal
    /// TaleWorlds.Core.FormationClass (Infantry 0, Ranged 1, Cavalry 2, HorseArcher 3; pinned by the self-check), so the game
    /// code casts; kept here so the headless self-check can test the parse without game assemblies.
    /// </summary>
    public enum CaptainFormation
    {
        Infantry = 0,
        Ranged = 1,
        Cavalry = 2,
        HorseArcher = 3,
    }

    /// <summary>
    /// What one agent carries into a hit or a spawn: its side's buffs, and its hero's or group's when it has them. A null
    /// member is neutral. The default value is an unbuffed agent.
    /// </summary>
    public readonly struct AgentBuffs
    {
        public AgentBuffs(SideBuffs side, float heroDamageMultiplier, HeroBuffs hero, GroupBuffs group, AuraSpec aura = null)
        {
            Side = side;
            HeroDamageMultiplier = heroDamageMultiplier;
            Hero = hero;
            Group = group;
            Aura = aura;
        }

        /// <summary>The combined aura of the acclaimed knights leading this troop's formation (PROTOCOL.md §42); null = none.</summary>
        public AuraSpec Aura { get; }

        public SideBuffs Side { get; }

        /// <summary>heroes[].damageMultiplier of a hero agent; 0 (the default value) and 1 both mean "no hero factor".</summary>
        public float HeroDamageMultiplier { get; }

        public HeroBuffs Hero { get; }
        public GroupBuffs Group { get; }
    }

    /// <summary>
    /// Pure arithmetic of the CK3 combat buffs (PROTOCOL.md §13.4): the module's safety clamps, the
    /// per-hit damage product and the spawn-time values. Game types stay out (src/Game/CK3Buffs.cs applies the results), so the
    /// headless self-check tests every rule here.
    /// </summary>
    public static class BuffMath
    {
        /// <summary>Clamp of every *Mult the request carries (PROTOCOL.md §13.4 item 7; the bridge clamps tighter).</summary>
        public const float MinMult = 0.25f;
        public const float MaxMult = 4f;
        public const float MaxMoraleAdd = 50f;
        public const float MinStartHealthFraction = 0.05f;
        public const float MaxKnockdownResistanceAdd = 0.5f;
        public const float MinHeroDamageMultiplier = 0.25f;
        public const float MaxHeroDamageMultiplier = 3f;

        /// <summary>Clamp of the whole per-hit product (PROTOCOL.md §13.4 item 4).</summary>
        public const float MinHitMultiplier = 0.25f;
        public const float MaxHitMultiplier = 3f;

        /// <summary>Clamped to [lo, hi]; NaN and infinities (which Newtonsoft reads from "NaN"/"Infinity" or overflowing numbers) become <paramref name="neutral"/>.</summary>
        public static float Safe(float value, float lo, float hi, float neutral)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return neutral;
            return value < lo ? lo : value > hi ? hi : value;
        }

        public static float Mult(float value) => Safe(value, MinMult, MaxMult, 1f);
        public static float MoraleAdd(float value) => Safe(value, -MaxMoraleAdd, MaxMoraleAdd, 0f);
        public static float HeroDamageMultiplier(float value) => Safe(value, MinHeroDamageMultiplier, MaxHeroDamageMultiplier, 1f);

        /// <summary>A clamped copy (the request object is left as it was read); null stays null.</summary>
        public static SideBuffs Sanitize(SideBuffs buffs)
        {
            if (buffs == null) return null;
            return new SideBuffs
            {
                DamageDealtMult = Mult(buffs.DamageDealtMult),
                DamageTakenMult = Mult(buffs.DamageTakenMult),
                InitialMoraleAdd = MoraleAdd(buffs.InitialMoraleAdd),
                ChargeDamageMult = Mult(buffs.ChargeDamageMult),
                TroopDeathChanceMult = Mult(buffs.TroopDeathChanceMult),
            };
        }

        public static GroupBuffs Sanitize(GroupBuffs buffs)
        {
            if (buffs == null) return null;
            return new GroupBuffs
            {
                DamageDealtMult = Mult(buffs.DamageDealtMult),
                DamageTakenMult = Mult(buffs.DamageTakenMult),
                HealthMult = Mult(buffs.HealthMult),
                InitialMoraleAdd = MoraleAdd(buffs.InitialMoraleAdd),
            };
        }

        public static HeroBuffs Sanitize(HeroBuffs buffs)
        {
            if (buffs == null) return null;
            return new HeroBuffs
            {
                DamageDealtMeleeMult = Mult(buffs.DamageDealtMeleeMult),
                DamageDealtRangedMult = Mult(buffs.DamageDealtRangedMult),
                DamageDealtVsHeroMult = Mult(buffs.DamageDealtVsHeroMult),
                DamageTakenMult = Mult(buffs.DamageTakenMult),
                ChargeDamageMult = Mult(buffs.ChargeDamageMult),
                InitialMoraleAdd = MoraleAdd(buffs.InitialMoraleAdd),
                NoPanic = buffs.NoPanic,
                StartHealthFraction = Safe(buffs.StartHealthFraction, MinStartHealthFraction, 1f, 1f),
                SpeedMult = Mult(buffs.SpeedMult),
                SwingSpeedMult = Mult(buffs.SwingSpeedMult),
                RangedInaccuracyMult = Mult(buffs.RangedInaccuracyMult),
                KnockdownResistanceAdd = Safe(buffs.KnockdownResistanceAdd, -MaxKnockdownResistanceAdd, MaxKnockdownResistanceAdd, 0f),
                AiLevel = Safe(buffs.AiLevel, 0f, 1f, 0f),
                Cleave = buffs.Cleave,
            };
        }

        /// <summary>An AI ability or chance (0..1) raised towards 1 by <paramref name="level"/> (PROTOCOL.md §28).</summary>
        public static float AiRaise(float value, float level) => Math.Min(1f, value + (1f - Math.Max(0f, value)) * level);

        /// <summary>An AI error, delay or randomness lowered by up to 80 % at <paramref name="level"/> 1.</summary>
        public static float AiLower(float value, float level) => value * (1f - 0.8f * level);

        /// <summary>
        /// The groupBuffs of one side, clamped and keyed verbatim (PROTOCOL.md §13.4 item 3). A key that no troop of the side
        /// carries (<see cref="RequestRules.GroupKey"/>) is passed to <paramref name="unknownKey"/> and left out; a null
        /// dictionary or entry counts as empty.
        /// </summary>
        public static Dictionary<string, GroupBuffs> GroupBuffsOf(SideRequest side, Action<string> unknownKey)
        {
            var result = new Dictionary<string, GroupBuffs>(StringComparer.Ordinal);
            if (side?.GroupBuffs == null || side.GroupBuffs.Count == 0) return result;
            var requested = new HashSet<string>(StringComparer.Ordinal);
            if (side.Troops != null)
            {
                foreach (TroopRequest troop in side.Troops)
                {
                    string key = troop == null ? null : RequestRules.GroupKey(troop);
                    if (key != null) requested.Add(key);
                }
            }
            foreach (KeyValuePair<string, GroupBuffs> kv in side.GroupBuffs)
            {
                if (kv.Key == null || kv.Value == null) continue;
                if (requested.Contains(kv.Key)) result[kv.Key] = Sanitize(kv.Value);
                else unknownKey?.Invoke(kv.Key);
            }
            return result;
        }

        /// <summary>
        /// The factor on one hit (PROTOCOL.md §13.4 item 4): attacker side dealt (× charge on a charge), hero damageMultiplier,
        /// hero melee or ranged dealt (× vs-hero on a melee hit against a hero, × hero charge on a charge), group dealt; victim
        /// side, hero and group taken; the product clamped to 0.25..3. <paramref name="charge"/> = a horse charge or a melee hit
        /// by a mounted agent. The caller skips falls, friendly fire and hits on mounts.
        /// </summary>
        public static float HitMultiplier(in AgentBuffs attacker, in AgentBuffs victim, bool missile, bool charge, bool victimIsHero)
        {
            float mult = 1f;
            if (attacker.Side != null)
            {
                mult *= attacker.Side.DamageDealtMult;
                if (charge) mult *= attacker.Side.ChargeDamageMult;
            }
            if (attacker.HeroDamageMultiplier > 0f) mult *= attacker.HeroDamageMultiplier;
            if (attacker.Hero != null)
            {
                mult *= missile ? attacker.Hero.DamageDealtRangedMult : attacker.Hero.DamageDealtMeleeMult;
                if (!missile && victimIsHero) mult *= attacker.Hero.DamageDealtVsHeroMult;
                if (charge) mult *= attacker.Hero.ChargeDamageMult;
            }
            if (attacker.Group != null) mult *= attacker.Group.DamageDealtMult;
            if (attacker.Aura != null)
            {
                mult *= missile ? attacker.Aura.RangedDamageMult : attacker.Aura.MeleeDamageMult;
                if (charge) mult *= attacker.Aura.ChargeDamageMult;
            }
            if (victim.Side != null) mult *= victim.Side.DamageTakenMult;
            if (victim.Hero != null) mult *= victim.Hero.DamageTakenMult;
            if (victim.Group != null) mult *= victim.Group.DamageTakenMult;
            if (victim.Aura != null) mult *= victim.Aura.DamageTakenMult;
            return Safe(mult, MinHitMultiplier, MaxHitMultiplier, 1f);
        }

        /// <summary>Initial morale before Bannerlord's own 15..100 clamp: + side, hero and aura initialMoraleAdd.</summary>
        public static float InitialMorale(float morale, in AgentBuffs agent) =>
            morale + (agent.Side?.InitialMoraleAdd ?? 0f) + (agent.Hero?.InitialMoraleAdd ?? 0f) + (agent.Aura?.InitialMoraleAdd ?? 0f);

        /// <summary>A clamped copy of a knight's aura (PROTOCOL.md §42); null, or a formation that is none of the four, stays null.</summary>
        public static AuraSpec Sanitize(AuraSpec aura)
        {
            if (aura == null || !TryParseCaptainOf(aura.Formation, out CaptainFormation formation)) return null;
            return new AuraSpec
            {
                Formation = formation.ToString(),
                MeleeDamageMult = Mult(aura.MeleeDamageMult),
                RangedDamageMult = Mult(aura.RangedDamageMult),
                ChargeDamageMult = Mult(aura.ChargeDamageMult),
                DamageTakenMult = Mult(aura.DamageTakenMult),
                RangedInaccuracyMult = Mult(aura.RangedInaccuracyMult),
                InitialMoraleAdd = MoraleAdd(aura.InitialMoraleAdd),
                MoraleLoss = Safe(aura.MoraleLoss, 0f, MaxMoraleAdd, 0f),
            };
        }

        /// <summary>
        /// Several knights leading one formation: the strongest of each effect counts (CK3 lets only the strongest accolade
        /// effect of a kind apply), not their product. Null when there is none.
        /// </summary>
        public static AuraSpec Strongest(IEnumerable<AuraSpec> auras)
        {
            AuraSpec best = null;
            foreach (AuraSpec a in auras)
            {
                if (a == null) continue;
                if (best == null)
                {
                    best = new AuraSpec
                    {
                        Formation = a.Formation, MeleeDamageMult = a.MeleeDamageMult, RangedDamageMult = a.RangedDamageMult,
                        ChargeDamageMult = a.ChargeDamageMult, DamageTakenMult = a.DamageTakenMult, RangedInaccuracyMult = a.RangedInaccuracyMult,
                        InitialMoraleAdd = a.InitialMoraleAdd, MoraleLoss = a.MoraleLoss,
                    };
                    continue;
                }
                best.MeleeDamageMult = Math.Max(best.MeleeDamageMult, a.MeleeDamageMult);
                best.RangedDamageMult = Math.Max(best.RangedDamageMult, a.RangedDamageMult);
                best.ChargeDamageMult = Math.Max(best.ChargeDamageMult, a.ChargeDamageMult);
                best.DamageTakenMult = Math.Min(best.DamageTakenMult, a.DamageTakenMult);
                best.RangedInaccuracyMult = Math.Min(best.RangedInaccuracyMult, a.RangedInaccuracyMult);
                best.InitialMoraleAdd = Math.Max(best.InitialMoraleAdd, a.InitialMoraleAdd);
                best.MoraleLoss = Math.Max(best.MoraleLoss, a.MoraleLoss);
            }
            return best;
        }

        /// <summary>A downed troop's killed-vs-unconscious chance × the side's troopDeathChanceMult, clamped to 0..1.</summary>
        public static float TroopDeathChance(float chance, SideBuffs side) =>
            Safe(chance * (side?.TroopDeathChanceMult ?? 1f), 0f, 1f, chance);

        /// <summary>A knock-down, knock-back or dismount resistance plus the hero's knockdownResistanceAdd, kept a share (0..1).</summary>
        public static float Resistance(float value, float add) => Safe(value + add, 0f, 1f, value);

        /// <summary>
        /// A hero's starting health when his buffs say he starts wounded (startHealthFraction &lt; 1): that share of his final
        /// pool (after healthMultiplier), at least 1. False when he starts at full health.
        /// </summary>
        public static bool TryStartHealth(float healthLimit, HeroBuffs hero, out float health)
        {
            health = healthLimit;
            if (hero == null || !(hero.StartHealthFraction < 1f)) return false;
            health = Math.Max(1f, healthLimit * hero.StartHealthFraction);
            return true;
        }

        /// <summary>
        /// heroes[].captainOf: one of Infantry, Ranged, Cavalry, HorseArcher, case-insensitive (surrounding blanks ignored).
        /// Anything else (null, empty, numbers, General, Bodyguard, ...) is no captaincy.
        /// </summary>
        public static bool TryParseCaptainOf(string value, out CaptainFormation formation)
        {
            formation = CaptainFormation.Infantry;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string name = value.Trim();
            foreach (CaptainFormation candidate in (CaptainFormation[])Enum.GetValues(typeof(CaptainFormation)))
            {
                if (!string.Equals(candidate.ToString(), name, StringComparison.OrdinalIgnoreCase)) continue;
                formation = candidate;
                return true;
            }
            return false;
        }
    }
}
