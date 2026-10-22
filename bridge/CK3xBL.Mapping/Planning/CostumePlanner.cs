// Costumes (PROTOCOL §21): every troop group wears the armour of its CK3 culture, era and role. The bridge picks the items
// (ck3xbl_costumes.json: a profile's items, minus those of a later era, role items preferred); the module swaps each agent's
// stock armour for one of them with about the same protection, so the look changes and the fighting does not.
using CK3xBL.Mapping.Data;
using CK3xBL.Mapping.Resolution;
using CK3xBL.Protocol;

namespace CK3xBL.Mapping.Planning;

public static class CostumePlanner
{
    public const string Levy = "levy", Infantry = "inf", Ranged = "rng", Cavalry = "cav";

    /// <summary>Weight factor of an item tagged for the group's role (levy cloth, riders' mail, archers' hoods).</summary>
    public const double RoleFactor = 2;

    /// <summary>Not dressed: swapping a built horse's harness left the horse invisible in play (the table keeps the items).</summary>
    public const string HorseHarnessSlot = "HorseHarness";

    private static readonly HashSet<string> CavalryBases = new(StringComparer.Ordinal)
    {
        "light_cavalry", "heavy_cavalry", "archer_cavalry", "camel_cavalry", "elephant_cavalry",
    };

    private static readonly HashSet<string> RangedBases = new(StringComparer.Ordinal) { "archers", "crossbowmen" };

    /// <summary>The costume role of a hero by Bannerlord formation.</summary>
    public static string RoleOfFormation(string? formation) =>
        formation is "Cavalry" or "HorseArcher" ? Cavalry : formation == "Ranged" ? Ranged : Infantry;

    /// <summary>The costume role of a MaA base type.</summary>
    public static string RoleOf(string? baseType) =>
        baseType is null ? Infantry : CavalryBases.Contains(baseType) ? Cavalry : RangedBases.Contains(baseType) ? Ranged : Infantry;

    /// <summary>PROTOCOL §35: the look a MaA regiment wears: its own ("maa:&lt;key&gt;", the CK3 men-at-arms with a culture of their own:
    /// huscarls, cataphracts, kheshig, ...) when the table has one, else its culture profile's.</summary>
    public static string LookOf(CostumesJson? costumes, string? maaKey, string profile) =>
        maaKey is not null && costumes?.Looks.ContainsKey("maa:" + maaKey) == true ? "maa:" + maaKey : profile;

    /// <summary>The look key of a group: profile|era ordinal|role.</summary>
    public static string Key(string profile, string? era, string role) => $"{profile}|{TroopResolver.EraOrdinal(era)}|{role}";

    /// <summary>The items of a look key, by slot, heaviest weight first; null when the profile has no look.</summary>
    public static LookSpec? Build(CostumesJson costumes, IReadOnlyDictionary<string, CostumeItem> items, string key)
    {
        var parts = key.Split('|');
        if (parts.Length != 3 || !int.TryParse(parts[1], out int era) || !costumes.Looks.TryGetValue(parts[0], out var look)) return null;
        string role = parts[2];
        var spec = new LookSpec();
        foreach (var (id, weight) in look)
        {
            if (weight <= 0 || !items.TryGetValue(id, out var item) || item.EraMin > era || item.Slot == HorseHarnessSlot) continue;
            double w = item.Tags.Contains(role) ? weight * RoleFactor : weight;
            if (!spec.Slots.TryGetValue(item.Slot, out var list)) spec.Slots[item.Slot] = list = new List<LookItem>();
            list.Add(new LookItem { Id = id, Weight = (float)Math.Round(w, 2) });
        }
        foreach (var list in spec.Slots.Values)
            list.Sort((a, b) => b.Weight != a.Weight ? b.Weight.CompareTo(a.Weight) : string.CompareOrdinal(a.Id, b.Id));
        return spec.Slots.Count > 0 ? spec : null;
    }

    /// <summary>
    /// Fills <see cref="BattleRequest.Looks"/> with the looks the troops name, or clears every look when costumes are off or the
    /// table is missing.
    /// </summary>
    public static void Dress(BattleRequest request, MappingTables tables, bool enabled)
    {
        var sides = new[] { request.Attacker, request.Defender }.Where(s => s is not null).ToList();
        var troops = sides.Where(s => s.Troops is not null).SelectMany(s => s.Troops).ToList();
        var heroes = sides.Where(s => s.Heroes is not null).SelectMany(s => s.Heroes).Where(h => h is not null).ToList();
        var costumes = tables.Costumes;
        request.Looks = null;
        if (!enabled || costumes is null)
        {
            foreach (var t in troops) t.Look = null;
            foreach (var h in heroes) h.Look = null;
            return;
        }
        var items = tables.CostumeItems;
        var looks = new Dictionary<string, LookSpec>(StringComparer.Ordinal);
        string? Known(string? key)
        {
            if (key is null) return null;
            if (!looks.ContainsKey(key) && Build(costumes, items, key) is { } spec) looks[key] = spec;
            return looks.ContainsKey(key) ? key : null;
        }
        foreach (var t in troops) t.Look = Known(t.Look);
        foreach (var h in heroes) h.Look = Known(h.Look);
        if (looks.Count > 0) request.Looks = looks;
    }
}
