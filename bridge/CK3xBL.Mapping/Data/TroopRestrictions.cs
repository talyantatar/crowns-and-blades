// mapping.json "restricted_troops": bandit,
// minor-faction, quest and civilian troop lines that look wrong outside their own setting (forest outlaws among Norman levies,
// wolf-skin berserkers among the 1066 English). Each rule is an id pattern ('*' matches any run of characters) with explicit allow
// entries; a matching troop may be resolved only where one entry matches the army's profile, MaA key, shared ladder, use and era.
//
// The data is kept clean statically, so the resolver needs no runtime filter: Validate walks every troop list the resolver can reach
// (in the resolver's order, mirrored by Reachable and cross-checked against TroopResolver in SelfCheck) for every profile and
// era, and reports each restricted troop it finds outside its allow entries. MappingTables.Load runs it and refuses data that fails,
// so every troop choice of a loaded table set obeys the rules.
using System.Globalization;
using CK3xBL.Mapping.Resolution;

namespace CK3xBL.Mapping.Data;

/// <summary>Which resolution produced a troop list: the army's levy, a men-at-arms regiment, or knights and their retinues.</summary>
public enum TroopUse { Levy, Maa, Knights }

/// <summary>Where a troop is resolved: the army's profile and culture era, and for men-at-arms the CK3 key (null for a modded key)
/// and the shared ladder it reads (null for culture and profile ladders).</summary>
public sealed record TroopContext(TroopUse Use, string Profile, string? Era, string? MaaKey = null, string? Ladder = null);

public sealed class RestrictedTroopsJson
{
    public string? Note { get; set; }
    public List<RestrictedTroopRule> Rules { get; set; } = new();
}

public sealed class RestrictedTroopRule
{
    /// <summary>Troop id pattern; '*' matches any run of characters (brotherhood_of_woods_*, *brigand*).</summary>
    public string Pattern { get; set; } = "";
    public string? What { get; set; }
    /// <summary>Where a matching troop may appear; empty = nowhere.</summary>
    public List<TroopAllowance> Allow { get; set; } = new();
}

/// <summary>One allow entry: every constraint it sets must match (lists are any-of); eras are inclusive.</summary>
public sealed class TroopAllowance : IEraGated
{
    public List<string>? Profiles { get; set; }
    /// <summary>CK3 MaA keys.</summary>
    public List<string>? Maa { get; set; }
    /// <summary>mapping.json shared_ladders keys.</summary>
    public List<string>? Ladders { get; set; }
    /// <summary>levy | maa | knights.</summary>
    public List<string>? Uses { get; set; }
    public string? MinEra { get; set; }
    public string? MaxEra { get; set; }
    public string? Why { get; set; }
}

public sealed class TroopRestrictions
{
    private const int MaxReported = 40;

    private readonly RestrictedTroopsJson _json;

    /// <param name="section">mapping.json restricted_troops; null (no section) allows every troop everywhere.</param>
    public TroopRestrictions(RestrictedTroopsJson? section) => _json = section ?? new RestrictedTroopsJson();

    public IReadOnlyList<RestrictedTroopRule> Rules => _json.Rules;

    /// <summary>The first rule whose pattern matches the troop, or null for an unrestricted troop.</summary>
    public RestrictedTroopRule? RuleFor(string troopId) => _json.Rules.FirstOrDefault(r => Matches(r.Pattern, troopId));

    /// <summary>True when the troop is unrestricted, or one allow entry of its rule matches the context.</summary>
    public bool IsAllowed(string troopId, TroopContext context)
    {
        var rule = RuleFor(troopId);
        return rule is null || rule.Allow.Any(a => Admits(a, context));
    }

    /// <summary>Glob match with '*' only, ordinal.</summary>
    public static bool Matches(string pattern, string id)
    {
        string[] parts = pattern.Split('*');
        if (parts.Length == 1) return string.Equals(pattern, id, StringComparison.Ordinal);
        if (!id.StartsWith(parts[0], StringComparison.Ordinal) || !id.EndsWith(parts[^1], StringComparison.Ordinal)) return false;
        int pos = parts[0].Length, end = id.Length - parts[^1].Length;
        if (pos > end) return false;
        for (int i = 1; i < parts.Length - 1; i++)
        {
            if (parts[i].Length == 0) continue;
            int at = id.IndexOf(parts[i], pos, end - pos, StringComparison.Ordinal);
            if (at < 0) return false;
            pos = at + parts[i].Length;
        }
        return true;
    }

    private static string UseKey(TroopUse use) => use.ToString().ToLowerInvariant();

    private static bool Admits(TroopAllowance a, TroopContext c) =>
        (a.Uses is null || a.Uses.Contains(UseKey(c.Use)))
        && (a.Profiles is null || a.Profiles.Contains(c.Profile))
        && (a.Maa is null || (c.MaaKey is not null && a.Maa.Contains(c.MaaKey)))
        && (a.Ladders is null || (c.Ladder is not null && a.Ladders.Contains(c.Ladder)))
        && TroopResolver.EraOk(a, c.Era);

    /// <summary>
    /// Problems that make the mapping data unusable: malformed rules (unknown profile, MaA, ladder, use or era; an entry without a
    /// profile, MaA or ladder constraint), patterns that match no catalog troop or overlap on one, and every restricted troop the
    /// resolver can reach outside its allow entries. Empty = valid. <paramref name="troops"/> may be null to skip the catalog checks.
    /// </summary>
    public IReadOnlyList<string> Validate(MappingJson mapping, IReadOnlyDictionary<string, BlTroop>? troops)
    {
        var problems = new List<string>();
        var uses = Enum.GetValues<TroopUse>().Select(UseKey).ToHashSet(StringComparer.Ordinal);
        var patterns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in _json.Rules)
        {
            string p = rule.Pattern;
            if (p.Length == 0 || p.Trim('*').Length == 0 || p.Any(ch => ch != '*' && ch != '_' && !char.IsAsciiLetterLower(ch) && !char.IsAsciiDigit(ch)))
                problems.Add($"restricted_troops: invalid pattern '{p}'");
            if (!patterns.Add(p)) problems.Add($"restricted_troops: duplicate pattern '{p}'");
            foreach (var a in rule.Allow)
            {
                string where = $"restricted_troops '{p}' allow entry";
                if (a.Profiles is null && a.Maa is null && a.Ladders is null) problems.Add($"{where} sets no profile, MaA or ladder (it would allow everywhere)");
                foreach (var x in a.Profiles ?? []) if (!mapping.Profiles.ContainsKey(x)) problems.Add($"{where}: unknown profile '{x}'");
                foreach (var x in a.Maa ?? []) if (!mapping.MaaTier.ContainsKey(x)) problems.Add($"{where}: unknown MaA '{x}'");
                foreach (var x in a.Ladders ?? []) if (!mapping.SharedLadders.ContainsKey(x)) problems.Add($"{where}: unknown shared ladder '{x}'");
                foreach (var x in a.Uses ?? []) if (!uses.Contains(x)) problems.Add($"{where}: unknown use '{x}'");
                foreach (var e in new[] { a.MinEra, a.MaxEra })
                    if (e is not null && !TroopResolver.IsKnownEra(e)) problems.Add($"{where}: unknown era '{e}'");
                if (TroopResolver.IsKnownEra(a.MinEra) && TroopResolver.IsKnownEra(a.MaxEra) && !TroopResolver.EraOk(a, a.MinEra))
                    problems.Add($"{where}: min_era after max_era");
            }
        }

        if (troops is not null)
        {
            foreach (var rule in _json.Rules)
                if (!troops.Keys.Any(id => Matches(rule.Pattern, id))) problems.Add($"restricted_troops '{rule.Pattern}' matches no troop of bl_catalog.json");
            foreach (var id in troops.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var hits = _json.Rules.Where(r => Matches(r.Pattern, id)).Select(r => r.Pattern).ToList();
                if (hits.Count > 1) problems.Add($"troop '{id}' matches several restricted_troops patterns: {string.Join(", ", hits)}");
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var violations = new List<string>();
        foreach (var list in Reachable(mapping))
        {
            foreach (var t in list.Troops)
            {
                if (IsAllowed(t.Troop, list.Context)) continue;
                var c = list.Context;
                string v = $"{list.Location}: restricted troop '{t.Troop}' reached by profile {c.Profile}, {c.Era}" + (c.MaaKey is null ? "" : $", MaA {c.MaaKey}");
                if (seen.Add(v)) violations.Add(v);
            }
        }
        problems.AddRange(violations.Take(MaxReported));
        if (violations.Count > MaxReported) problems.Add($"... and {violations.Count - MaxReported} more restricted troop placements");
        return problems;
    }

    /// <summary>A troop list the resolver can return in a context; for men-at-arms the whole ladder (every rung a terrain or winter
    /// tier shift can reach).</summary>
    internal sealed record ReachedList(TroopContext Context, string Location, IReadOnlyList<TroopWeight> Troops, IReadOnlyList<Rung>? Ladder, string? BaseTypeHint);

    /// <summary>
    /// Every troop list the resolver reaches, for every profile and era of mapping.json: levies (the nomadic variant for Khuzait
    /// profiles too), knights, and the ladder of every non-excluded MaA key plus one modded key per base type. Mirrors
    /// TroopResolver.ResolveLevy/ResolveKnights/ResolveMaa (ladder_ref, then forced culture, then the era-gated profile override,
    /// then the profile culture); SelfCheck proves the mirror against the resolver.
    /// </summary>
    internal static IEnumerable<ReachedList> Reachable(MappingJson m)
    {
        var eras = m.LevyByEra.Keys.ToList();
        var maaKeys = m.MaaTier.Where(kv => !kv.Value.Excluded).Select(kv => ((string?)kv.Key, kv.Value.BaseType))
            .Concat(m.BaseRoles.Where(kv => !kv.Value.Exclude).Select(kv => ((string?)null, kv.Key)))
            .ToList();
        foreach (var (profileKey, prof) in m.Profiles)
        {
            foreach (var era in eras)
            {
                var levyByVariant = m.LevyByEra[era];
                string variant = !string.IsNullOrEmpty(prof.LevyVariant) ? prof.LevyVariant : prof.BlCulture;
                var variants = prof.BlCulture == "khuzait" ? new[] { variant, "steppe_nomadic" } : new[] { variant };
                foreach (var v in variants)
                {
                    string used = levyByVariant.ContainsKey(v) ? v : prof.BlCulture;
                    if (levyByVariant.TryGetValue(used, out var levy))
                        yield return new ReachedList(new TroopContext(TroopUse.Levy, profileKey, era), $"levy_by_era.{era}.{used}", levy, null, null);
                }

                var pk = TroopResolver.PickEra(prof.Knights, era);
                if (pk is not null) yield return new ReachedList(new TroopContext(TroopUse.Knights, profileKey, era), $"profiles.{profileKey}.knights", pk.Troops, null, null);
                else if (m.Knights.TryGetValue(prof.BlCulture, out var knights))
                    yield return new ReachedList(new TroopContext(TroopUse.Knights, profileKey, era), $"knights.{prof.BlCulture}", knights, null, null);

                foreach (var (key, baseType) in maaKeys)
                {
                    var found = MaaLadder(m, key, baseType, profileKey, prof, era);
                    if (found is null) continue;
                    var (ladder, location, sharedRef) = found.Value;
                    var troops = ladder.SelectMany(r => r.Troops).ToList();
                    yield return new ReachedList(new TroopContext(TroopUse.Maa, profileKey, era, key, sharedRef), location, troops, ladder, key is null ? baseType : null);
                }
            }
        }
    }

    private static (List<Rung> Ladder, string Location, string? SharedRef)? MaaLadder(MappingJson m, string? key, string baseType, string profileKey, Profile prof, string era)
    {
        var info = key is null ? null : m.MaaTier.GetValueOrDefault(key);
        if (!m.BaseRoles.TryGetValue(baseType, out var bas) || bas.Exclude || (info?.Excluded ?? false)) return null;
        var ov = key is null ? null : m.MaaOverrides.GetValueOrDefault(key);
        string? role = !string.IsNullOrEmpty(ov?.LadderRole) ? ov!.LadderRole : bas.Role;
        string? reference = !string.IsNullOrEmpty(ov?.LadderRef) ? ov!.LadderRef : string.IsNullOrEmpty(ov?.LadderRole) ? bas.LadderRef : null;
        if (!string.IsNullOrEmpty(reference))
            return m.SharedLadders.TryGetValue(reference, out var shared) && shared.Count > 0 ? (shared, $"shared_ladders.{reference}", reference) : null;

        string? culture = !string.IsNullOrEmpty(ov?.ForceCulture) ? ov!.ForceCulture : !string.IsNullOrEmpty(bas.ForceCulture) ? bas.ForceCulture : null;
        if (culture is null)
        {
            var po = role is not null && prof.RoleOverrides is not null && prof.RoleOverrides.TryGetValue(role, out var entries) ? TroopResolver.PickEra(entries, era) : null;
            if (po?.Ladder is { Count: > 0 }) return (po.Ladder, $"profiles.{profileKey}.role_overrides.{role}", null);
            culture = !string.IsNullOrEmpty(po?.UseCulture) ? po!.UseCulture : prof.BlCulture;
        }
        return role is not null && m.MaaLadders.TryGetValue(role, out var byCulture) && byCulture.TryGetValue(culture!, out var ladder) && ladder.Count > 0
            ? (ladder, $"maa_ladders.{role}.{culture}", null)
            : null;
    }

    /// <summary>
    /// Headless self-check for the mapping tests: the data validates, Reachable picks exactly the ladder TroopResolver interpolates,
    /// the pure functions behave (pattern matching, era gates, unknown troops), and the pins hold (the
    /// reviewed cultures keep their profiles, no restricted troop in their levy/MaA/knight lists outside its allow entries,
    /// berserkers only for Norse up to the Early Medieval era). Returns the failures; empty = pass.
    /// </summary>
    public static IReadOnlyList<string> SelfCheck(MappingTables tables)
    {
        var failures = new List<string>();
        var m = tables.Mapping;
        var restrictions = tables.Restrictions;
        if (restrictions.Rules.Count == 0) failures.Add("mapping.json has no restricted_troops rules");
        failures.AddRange(restrictions.Validate(m, tables.Troops));

        // Reachable mirrors the resolver: interpolating the mirrored ladder at the resolver's tier gives the resolver's mix.
        var resolver = new TroopResolver(m);
        foreach (var list in Reachable(m).Where(l => l.Context.Use == TroopUse.Maa))
        {
            var c = list.Context;
            var r = resolver.ResolveMaa(c.MaaKey ?? "ck3xbl_selfcheck_modded_maa", c.Profile, c.Era, null, null, list.BaseTypeHint);
            string id = $"{c.MaaKey ?? list.BaseTypeHint}/{c.Profile}/{c.Era}";
            if (r.Excluded) { failures.Add($"{id}: resolver excludes a regiment Reachable resolves to {list.Location}"); continue; }
            string mine = Mix(TroopResolver.Interpolate(list.Ladder!, r.Tier)), theirs = Mix(r.Troops);
            if (mine != theirs) failures.Add($"{id}: Reachable ladder {list.Location} gives {mine}, the resolver {theirs}");
        }

        // Pure functions.
        void Check(bool ok, string what) { if (!ok) failures.Add(what); }
        Check(Matches("*brigand*", "sturgian_hardened_brigand") && Matches("*_fighter", "champion_fighter") && Matches("sword_sister*", "sword_sisters_sister_t3"),
            "wildcard patterns must match inside, at the end and at the start");
        Check(!Matches("caravan_*", "veteran_caravan_guard_aserai") && !Matches("*_fighter", "fighter") && !Matches("jawwal_*", "jawwal"),
            "patterns must not match outside their fixed parts");
        const string Early = TroopResolver.EarlyMedieval, High = "culture_era_high_medieval";
        Check(restrictions.IsAllowed("sturgian_ulfhednar", new TroopContext(TroopUse.Knights, "norse", Early)), "ulfhednar allowed for norse knights, early medieval");
        Check(restrictions.IsAllowed("sturgian_berzerker", new TroopContext(TroopUse.Maa, "norse", null)), "an unlogged era counts as early medieval (TroopResolver.EraOk)");
        Check(!restrictions.IsAllowed("sturgian_berzerker", new TroopContext(TroopUse.Maa, "norse", High)), "berserkers end with the early medieval era");
        Check(!restrictions.IsAllowed("sturgian_berzerker", new TroopContext(TroopUse.Maa, "east_slavic", "culture_era_tribal")), "berserkers never for east_slavic");
        Check(!restrictions.IsAllowed("sturgian_ulfhednar", new TroopContext(TroopUse.Maa, "western_feudal", Early, "huscarl", "huscarl")), "berserkers never for western_feudal");
        Check(restrictions.IsAllowed("jawwal_tier_2", new TroopContext(TroopUse.Maa, "western_feudal", High, "camel_rider", "camel")), "camel riders allowed through the camel ladder");
        Check(!restrictions.IsAllowed("jawwal_tier_1", new TroopContext(TroopUse.Levy, "arab", Early)), "jawwal foot not allowed in levies");
        Check(restrictions.IsAllowed("vlandian_sergeant", new TroopContext(TroopUse.Maa, "steppe", Early)), "an unrestricted troop is allowed everywhere");

        // Pins: the reviewed cultures keep their profiles (CK3's "Oghuz" is the culture key turkish).
        var expected = new Dictionary<string, string>
        {
            ["english"] = "western_feudal", ["anglo_saxon"] = "western_feudal", ["saxon"] = "western_feudal", ["norman"] = "western_feudal",
            ["french"] = "western_feudal", ["frankish"] = "western_feudal", ["german"] = "western_feudal", ["italian"] = "western_feudal",
            ["lombard"] = "western_feudal", ["norse"] = "norse", ["danish"] = "norse", ["swedish"] = "norse", ["norwegian"] = "norse",
            ["castilian"] = "iberian_christian", ["greek"] = "byzantine", ["russian"] = "east_slavic", ["bedouin"] = "arab",
            ["levantine"] = "arab", ["egyptian"] = "arab", ["turkish"] = "steppe", ["pecheneg"] = "steppe",
        };
        foreach (var (culture, profile) in expected)
            Check(resolver.ResolveProfile(culture, null, null).Profile == profile, $"culture {culture} should resolve to profile {profile}");

        string[] pinnedMaa = ["armored_footmen", "pikemen_unit", "bowmen", "crossbowmen", "light_horsemen", "armored_horsemen", "light_footmen", "huscarl", "varangian_veterans"];
        foreach (var profile in expected.Values.Distinct())
        {
            foreach (var era in m.LevyByEra.Keys)
            {
                var troops = resolver.ResolveLevy(profile, era, false).Troops.Select(t => (TroopUse.Levy, (string?)null, t.Troop))
                    .Concat(resolver.ResolveKnights(profile, era).Select(t => (TroopUse.Knights, (string?)null, t.Troop)))
                    .Concat(pinnedMaa.SelectMany(k => resolver.ResolveMaa(k, profile, era, null, null, null).Troops.Select(t => (TroopUse.Maa, (string?)k, t.Troop))));
                foreach (var (use, key, troop) in troops)
                {
                    string? ladder = key is null ? null : m.MaaOverrides.GetValueOrDefault(key)?.LadderRef;
                    Check(restrictions.IsAllowed(troop, new TroopContext(use, profile, era, key, ladder)),
                        $"{profile}/{era}/{key ?? use.ToString()}: restricted troop {troop}");
                    bool berserker = troop is "sturgian_berzerker" or "sturgian_ulfhednar";
                    Check(!berserker || (profile == "norse" && era is "culture_era_tribal" or TroopResolver.EarlyMedieval),
                        $"{profile}/{era}/{key ?? use.ToString()}: berserker {troop} outside Norse up to the early medieval era");
                }
            }
        }
        return failures;
    }

    private static string Mix(IEnumerable<TroopWeight> troops) =>
        string.Join(",", troops.Select(t => t.Troop + ":" + t.Weight.ToString(CultureInfo.InvariantCulture)));
}
