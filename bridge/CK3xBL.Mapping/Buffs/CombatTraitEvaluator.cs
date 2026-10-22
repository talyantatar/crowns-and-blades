// Port of tools/gen/knights/combat_traits_eval.mjs (evaluateCharacter, evaluateBattle): which
// effects of bridge/data/ck3_combat_traits.json hold for a character in a battle, and the side buffs the two side commanders
// give. The bridge adds the Marauder accolade advantage to the side advantage; everything
// else reproduces the reference, which test_combat_traits.mjs pins with eight worked battles (MappingTests reproduces them).
using CK3xBL.Mapping.Data;

namespace CK3xBL.Mapping.Buffs;

/// <summary>A character as the evaluator sees him: held traits, their levels (CX|txp) and his culture/faith parameters.</summary>
public sealed class TraitHolder
{
    public IReadOnlyList<string> Traits { get; init; } = Array.Empty<string>();
    /// <summary>"&lt;trait&gt;" or "&lt;trait&gt;:&lt;track&gt;" -> track steps reached; absent = level 0 (base effects only).</summary>
    public IReadOnlyDictionary<string, int> Levels { get; init; } = new Dictionary<string, int>();
    public IReadOnlyCollection<string> CultureParameters { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> FaithParameters { get; init; } = Array.Empty<string>();
    public int Martial { get; init; }
}

/// <summary>The battle as one side sees it (the `when` conditions of the table).</summary>
public sealed record TraitContext
{
    public string Terrain { get; init; } = "plains";
    /// <summary>none | mild | normal | harsh.</summary>
    public string Winter { get; init; } = "none";
    public bool Coastal { get; init; }
    /// <summary>attacker | defender.</summary>
    public string Role { get; init; } = "attacker";
    /// <summary>Faith hostility level 0..3 that scales faith-hostility advantages.</summary>
    public int EnemyFaithHostility { get; init; }
    public bool SameFaith { get; init; }
    public bool HostileTerritory { get; init; }
    public bool OwnTerritory { get; init; }
    public bool OwnArmy { get; init; }
    public bool SameHeritageCounty { get; init; }
    public bool IndependentPrimary { get; init; }
}

/// <summary>Effect qualifiers: effects with different qualifiers are combined separately.</summary>
public sealed record EffectQualifiers(string? Weapon, string? Vs, string? Skill, string? Troops, string? MaaBaseType, string? MaaType, string? Per)
{
    public static readonly EffectQualifiers None = new(null, null, null, null, null, null, null);

    public static EffectQualifiers Of(EffectCondition? w) =>
        w is null ? None : new(w.Weapon, w.Vs, w.Skill, w.Troops, w.MaaBaseType, w.MaaType, w.Per);

    public bool IsNone => this == None;
}

/// <summary>A combined effect that acts only on some weapons, skills or troops (or is a group/liege effect).</summary>
public sealed class QualifiedEffect
{
    public required string Effect { get; init; }
    public required EffectQualifiers When { get; init; }
    public double Value { get; set; }
}

/// <summary>The effects of one character that hold in a context, combined per effect name (and per qualifier set).</summary>
public sealed class TraitEvaluation
{
    /// <summary>scope ("side", "outcome", "hero", "retinue") -> effect name -> combined value (booleans as 0/1).</summary>
    public Dictionary<string, Dictionary<string, double>> Scopes { get; } = new(StringComparer.Ordinal);
    public List<QualifiedEffect> Qualified { get; } = new();

    public double? Get(string scope, string name) =>
        Scopes.TryGetValue(scope, out var bucket) && bucket.TryGetValue(name, out var v) ? v : null;

    public IReadOnlyDictionary<string, double> Scope(string scope) =>
        Scopes.TryGetValue(scope, out var bucket) ? bucket : new Dictionary<string, double>();
}

/// <summary>One side's result of <see cref="CombatTraitEvaluator.EvaluateBattle"/>, before the bridge's caps.</summary>
public sealed class SideEvaluation
{
    public double Advantage { get; init; }
    public double DamageDealtMult { get; init; } = 1;
    public double DamageTakenMult { get; init; } = 1;
    public double InitialMoraleAdd { get; init; }
    public double ChargeDamageMult { get; init; } = 1;
    public double TroopDeathChanceMult { get; init; } = 1;
    public double TacticsAdd { get; init; }
    /// <summary>Factor on the side's CK3 fatal share (own hard_casualty_modifier x enemy enemy_hard_casualty_modifier).</summary>
    public double FatalShareMult { get; init; } = 1;
    /// <summary>Factor on the side's pursuit (retreat) losses.</summary>
    public double RetreatLossMult { get; init; } = 1;
}

/// <summary>One side of a battle for <see cref="CombatTraitEvaluator.EvaluateBattle"/>.</summary>
public sealed class SideInput
{
    public TraitHolder? Commander { get; init; }
    public required TraitContext Context { get; init; }
    /// <summary>Advantage from outside the commander's traits (the Marauder accolade).</summary>
    public double ExtraAdvantage { get; init; }
}

public sealed class CombatTraitEvaluator
{
    private readonly CombatTraitsJson _t;

    public CombatTraitEvaluator(CombatTraitsJson table) => _t = table;

    /// <summary>evaluateCharacter: every effect of the character's traits (base plus reached track steps) whose condition holds.</summary>
    public TraitEvaluation EvaluateCharacter(TraitHolder ch, TraitContext ctx)
    {
        var output = new TraitEvaluation();
        foreach (var key in ch.Traits)
        {
            if (!_t.Traits.TryGetValue(key, out var rec)) continue;
            var effects = new List<TraitEffect>(rec.Bl.Base);
            var tracks = rec.Bl.Tracks ?? new List<TraitTrack>();
            foreach (var tr in tracks)
            {
                string levelKey = tr.Steps.Count > 0 && tracks.Count > 1 ? $"{key}:{tr.Name}" : key;
                int level = ch.Levels.GetValueOrDefault(levelKey);
                for (int i = 0; i < Math.Min(level, tr.Steps.Count); i++) effects.AddRange(tr.Steps[i].Effects);
            }
            foreach (var e in effects)
            {
                double scale = Holds(e.When, ctx, ch);
                if (scale == 0) continue;
                Add(output, e, scale);
            }
        }
        return output;
    }

    private double Holds(EffectCondition? when, TraitContext ctx, TraitHolder ch)
    {
        if (when is null) return 1;
        if (when.Terrain is { } terrains && !terrains.Contains(ctx.Terrain)) return 0;
        if (when.Winter && ctx.Winter is not ("mild" or "normal" or "harsh")) return 0;
        if (when.Coastal && !ctx.Coastal) return 0;
        if (when.Role is { } role && role != ctx.Role) return 0;
        if (when.CultureParameter is { } cp && !ch.CultureParameters.Contains(cp)) return 0;
        if (when.FaithParameter is { } fp && !ch.FaithParameters.Contains(fp)) return 0;
        if ((when.SameFaith && !ctx.SameFaith) || (when.HostileTerritory && !ctx.HostileTerritory) || (when.OwnTerritory && !ctx.OwnTerritory) ||
            (when.OwnArmy && !ctx.OwnArmy) || (when.SameHeritageCounty && !ctx.SameHeritageCounty) || (when.IndependentPrimary && !ctx.IndependentPrimary))
            return 0;
        if (when.FaithHostility)
            return _t.Conversion.FaithHostility.Scale.GetValueOrDefault(ctx.EnemyFaithHostility.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return 1;
    }

    private void Add(TraitEvaluation output, TraitEffect e, double scale)
    {
        int dot = e.Effect.IndexOf('.');
        string scope = dot < 0 ? e.Effect : e.Effect[..dot];
        string name = dot < 0 ? "" : e.Effect[(dot + 1)..];
        string combine = _t.BlEffects.GetValueOrDefault(e.Effect)?.Combine ?? "add";
        var qual = EffectQualifiers.Of(e.When);
        if (!qual.IsNone || scope is "group" or "liege")
        {
            var entry = output.Qualified.FirstOrDefault(x => x.Effect == e.Effect && x.When == qual);
            if (entry is null)
            {
                entry = new QualifiedEffect { Effect = e.Effect, When = qual, Value = combine == "mult" ? 1 : 0 };
                output.Qualified.Add(entry);
            }
            entry.Value = Merge(combine, entry.Value, e.Value, scale);
            return;
        }
        if (!output.Scopes.TryGetValue(scope, out var bucket)) output.Scopes[scope] = bucket = new Dictionary<string, double>(StringComparer.Ordinal);
        bucket[name] = Merge(combine, bucket.TryGetValue(name, out var cur) ? cur : null, e.Value, scale);
    }

    private static double Merge(string combine, double? cur, double v, double scale) => combine switch
    {
        "or" => (cur ?? 0) != 0 || v != 0 ? 1 : 0,
        "mult" => (cur ?? 1) * (1 + (v - 1) * scale),
        _ => (cur ?? 0) + v * scale,
    };

    /// <summary>
    /// evaluateBattle, side part: advantage from martial, the side commanders' traits, the defender's terrain and harsh winter
    /// (plus <see cref="SideInput.ExtraAdvantage"/>); the side ahead deals more damage and starts with more morale; roll spread
    /// becomes volatility; casualty factors from both commanders.
    /// </summary>
    public (SideEvaluation Attacker, SideEvaluation Defender) EvaluateBattle(SideInput attacker, SideInput defender, string winter, double terrainDefenderAdvantage)
    {
        var conv = _t.Conversion;
        var att = attacker.Commander is { } ac ? EvaluateCharacter(ac, attacker.Context) : null;
        var def = defender.Commander is { } dc ? EvaluateCharacter(dc, defender.Context) : null;
        double advAtt = (attacker.Commander?.Martial ?? 0) + (att?.Get("side", "advantage") ?? 0) + attacker.ExtraAdvantage;
        double advDef = (defender.Commander?.Martial ?? 0) + (def?.Get("side", "advantage") ?? 0) + defender.ExtraAdvantage
                        + terrainDefenderAdvantage * Math.Max(0, 1 + (att?.Get("side", "enemyDefenderTerrainAdvantageAdd") ?? 0))
                        + (winter == "harsh" ? 5 : 0);
        double delta = advAtt - advDef;

        SideEvaluation Side(TraitEvaluation? own, TraitEvaluation? enemy, double advantage, bool ahead)
        {
            double spread = own?.Get("side", "rollSpread") ?? 0;
            double vol = Math.Clamp(1 + conv.CombatRoll.PerSpreadPoint * spread, 1 - conv.CombatRoll.MaxVolatility, 1 + conv.CombatRoll.MaxVolatility);
            double advMult = ahead ? Math.Min(1 + conv.Advantage.PerPoint * Math.Abs(delta), 1 + conv.Advantage.MaxDamageBonus) : 1;
            double morale = ahead ? Math.Min(conv.Advantage.MaxMoraleBonus, conv.Advantage.MoralePerPoint * Math.Abs(delta)) : 0;
            double enemyFatal = Math.Max(0, 1 + (enemy?.Get("outcome", "enemyFatalShareAdd") ?? 0));
            return new SideEvaluation
            {
                Advantage = advantage,
                DamageDealtMult = advMult * vol,
                DamageTakenMult = vol,
                InitialMoraleAdd = morale,
                ChargeDamageMult = own?.Get("side", "chargeDamageMult") ?? 1,
                TroopDeathChanceMult = enemyFatal,
                TacticsAdd = own?.Get("side", "tacticsAdd") ?? 0,
                FatalShareMult = Math.Max(0, 1 + (own?.Get("outcome", "ownFatalShareAdd") ?? 0)) * enemyFatal,
                RetreatLossMult = Math.Max(0, 1 + (own?.Get("outcome", "ownRetreatLossAdd") ?? 0)),
            };
        }

        return (Side(att, def, advAtt, delta > 0), Side(def, att, advDef, delta < 0));
    }
}
