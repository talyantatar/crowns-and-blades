// CK3 battle gear -> HeroSpec.equipment.overrides (PROTOCOL §16.3). Every gear artifact is one Bannerlord item, so the hero carries
// exactly it; empty CK3 slots keep the kit of his culture and rank. Applied after the vanilla artifact overrides, which it wins over.
//   Weapons   the gear's weapons (three CK3 slots, the main weapon first), then a bow's or crossbow's ammunition (Bannerlord's fourth
//             slot is kept for it, 2026-10-28), then the kit's weapons of a class group none of them has, four at most.
//   Armour    Head, Body, Gloves, Leg, Cape: the item replaces the kit's.
//   Horse     Horse and its harness from ck3xbl_equipment.json; the hero rides, even when his culture's nobles fight on foot.
//   Quality   every gear item gets its group's modifier by CK3 rarity (Qualities): common and masterwork Fine, famed Masterwork,
//             illustrious Legendary, so an artifact beats the kit's item of its tier.
using CK3xBL.Core.Model;
using CK3xBL.Mapping.Data;

namespace CK3xBL.Mapping.Planning;

public sealed class GearOutfitter
{
    private static readonly string[] WeaponSlots = { "Item0", "Item1", "Item2", "Item3" };
    private static readonly Dictionary<string, string> ArmourSlot = new(StringComparer.Ordinal)
    {
        ["ck3xbl_head"] = "Head", ["ck3xbl_body"] = "Body", ["ck3xbl_hands"] = "Gloves", ["ck3xbl_legs"] = "Leg", ["ck3xbl_cape"] = "Cape",
    };

    /// <summary>CK3's rarity thresholds on quality + wealth (both 10 + 90 q): famed from 130, illustrious from 180.</summary>
    public const double FamedFrom = 0.6112, IllustriousFrom = 0.8889;

    private readonly IReadOnlyDictionary<string, BlItem> _items;
    private readonly IReadOnlyDictionary<string, GearItem> _gear;

    public GearOutfitter(MappingTables tables)
    {
        _items = tables.Items;
        _gear = tables.Gear;
    }

    /// <summary>What the gear sets: slot overrides ("" = leave the slot empty), the item modifiers of the gear's slots, and whether
    /// the hero now rides.</summary>
    public sealed record Outfit(Dictionary<string, string> Overrides, bool Mounted)
    {
        public Dictionary<string, string> Qualities { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>The Bannerlord modifier quality of a gear item by its CK3 rarity.</summary>
    public static string QualityOf(double quality) =>
        quality >= IllustriousFrom ? "Legendary" : quality >= FamedFrom ? "Masterwork" : "Fine";

    /// <param name="kitItems">The current first set (slot -> item id): the kit with the vanilla artifact overrides applied.</param>
    public Outfit Resolve(IReadOnlyDictionary<string, string> kitItems, IEnumerable<GearSnapshot> gear, List<string>? log = null)
    {
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        bool mounted = false;
        var weapons = new List<string>();
        var gearIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in gear.OrderBy(g => g.Slot == "ck3xbl_main" ? 0 : 1))
        {
            if (!_items.ContainsKey(g.ItemId)) { log?.Add($"skip {g.ItemId}: not a Bannerlord item of the catalog"); continue; }
            gearIds.Add(g.ItemId);
            if (g.Slot is "ck3xbl_main" or "ck3xbl_gear" or "ck3xbl_weapon") weapons.Add(g.ItemId);
            else if (ArmourSlot.TryGetValue(g.Slot, out var slot)) { overrides[slot] = g.ItemId; log?.Add($"{slot} = {g.ItemId} (gear)"); }
            else if (g.Slot == "ck3xbl_horse")
            {
                overrides["Horse"] = g.ItemId;
                overrides["HorseHarness"] = _gear.GetValueOrDefault(g.ItemId)?.Harness ?? "";
                mounted = true;
                log?.Add($"Horse = {g.ItemId}, HorseHarness = {overrides["HorseHarness"]} (gear)");
            }
            else log?.Add($"skip {g.ItemId}: unknown gear slot {g.Slot}");
        }
        if (weapons.Count > 0)
        {
            // Three gear weapons at most (a save from before 2026-10-28 may still hold a fourth): the ammunition needs the last slot.
            var final = weapons.Take(WeaponSlots.Length - 1).ToList();
            if (weapons.Count > final.Count) log?.Add($"drop {string.Join(", ", weapons.Skip(final.Count))}: three gear weapons at most");
            var groups = final.Select(Group).ToHashSet(StringComparer.Ordinal);
            foreach (var (launcher, ammoGroup, ammoType) in new[] { ("bow", "arrows", "Arrows"), ("crossbow", "bolts", "Bolts") })
            {
                if (!groups.Contains(launcher) || groups.Contains(ammoGroup) || final.Count >= WeaponSlots.Length) continue;
                int tier = final.Where(i => Group(i) == launcher).Select(Tier).DefaultIfEmpty(1).Max();
                string? ammo = BestAmmo(ammoType, tier, kitItems);
                if (ammo is null) { log?.Add($"no {ammoGroup} for the {launcher}"); continue; }
                final.Add(ammo);
                groups.Add(ammoGroup);
                log?.Add($"{ammoGroup} {ammo} for the {launcher} (tier <= {tier})");
            }
            // The kit's other weapons fill what is left, one per class group the gear does not bring; never loose ammunition.
            foreach (var s in WeaponSlots)
            {
                if (final.Count >= WeaponSlots.Length) break;
                if (!kitItems.TryGetValue(s, out var id) || id.Length == 0 || final.Contains(id)) continue;
                string grp = Group(id);
                if (grp is "arrows" or "bolts" || groups.Contains(grp)) continue;
                final.Add(id);
                groups.Add(grp);
            }
            for (int i = 0; i < WeaponSlots.Length; i++) overrides[WeaponSlots[i]] = i < final.Count ? final[i] : "";
            log?.Add($"weapons: {string.Join(", ", final)}");
        }
        var outfit = new Outfit(overrides, mounted);
        foreach (var (slot, id) in overrides)
            if (id.Length > 0 && gearIds.Contains(id) && slot != "HorseHarness")
                outfit.Qualities[slot] = QualityOf(_gear.GetValueOrDefault(id)?.Quality ?? 0);
        if (outfit.Qualities.Count > 0) log?.Add($"qualities: {string.Join(", ", outfit.Qualities.Select(q => $"{q.Key} {q.Value}"))}");
        return outfit;
    }

    private string Group(string id) => ArtifactOutfitter.GroupOf(_items.GetValueOrDefault(id));

    private int Tier(string id) => _items.TryGetValue(id, out var it) ? it.Tier ?? 0 : 0;

    /// <summary>The kit's own ammunition of that type if it has some, else the catalog's best of a tier not above the launcher's.</summary>
    public string? BestAmmo(string type, int tier, IReadOnlyDictionary<string, string> kitItems)
    {
        foreach (var s in WeaponSlots)
            if (kitItems.TryGetValue(s, out var id) && _items.GetValueOrDefault(id)?.Type == type) return id;
        return _items.Values.Where(i => i.Type == type && (i.Tier ?? 0) <= tier)
            .OrderByDescending(i => i.Tier ?? 0).ThenBy(i => i.Id, StringComparer.Ordinal).Select(i => i.Id).FirstOrDefault()
            ?? _items.Values.Where(i => i.Type == type).OrderBy(i => i.Tier ?? 0).ThenBy(i => i.Id, StringComparer.Ordinal).Select(i => i.Id).FirstOrDefault();
    }
}
