// CK3 equipped artifacts -> HeroSpec.equipment.overrides (PROTOCOL §13.4). Port of
// tools/gen/knights/resolve_artifact_overrides.mjs (resolveArtifactOverrides, self-test over every kit x category x rarity x
// mounted/foot) with one change of the design: heroes of tier <= 1 always wear an armour or helmet artifact, because Appearance
// would otherwise dress barons and landless knights in a tier 4/5 culture armour instead of the lord kit the reference compares
// against (EquipmentPlanner downgrade). Artifact overrides are mapper overrides, so Appearance keeps them (fixed slots).
using CK3xBL.Core.Model;
using CK3xBL.Mapping.Data;

namespace CK3xBL.Mapping.Planning;

public sealed class ArtifactOutfitter
{
    private static readonly string[] ItemSlots = { "Item0", "Item1", "Item2", "Item3" };
    private static readonly Dictionary<string, string> CategoryGroup = new(StringComparer.Ordinal)
    {
        ["sword"] = "sword", ["axe"] = "axe", ["mace"] = "mace", ["spear"] = "polearm", ["dagger"] = "dagger",
        ["bow"] = "bow", ["longbow"] = "bow", ["composite_bow"] = "bow", ["crossbow"] = "crossbow",
    };
    private static readonly HashSet<string> Melee = new(StringComparer.Ordinal) { "sword", "axe", "mace" };

    private readonly ArtifactItemsJson _map;
    private readonly IReadOnlyDictionary<string, BlItem> _items;

    public ArtifactOutfitter(MappingTables tables)
    {
        _map = tables.Knights.Artifacts;
        _items = tables.Items;
    }

    /// <summary>Weapon-slot group of a Bannerlord item: sword/axe/mace/polearm/dagger/bow/crossbow/thrown/shield/arrows/bolts/banner/other.</summary>
    public static string GroupOf(BlItem? item)
    {
        if (item is null) return "other";
        switch (item.Type)
        {
            case "Shield": return "shield";
            case "Arrows": return "arrows";
            case "Bolts": return "bolts";
            case "Bow": return "bow";
            case "Crossbow": return "crossbow";
            case "Thrown": return "thrown";
            case "Polearm": return "polearm";
            case "Banner": return "banner";
        }
        var c = item.Classes;
        if (c.Contains("Dagger")) return "dagger";
        if (c.Contains("OneHandedSword") || c.Contains("TwoHandedSword")) return "sword";
        if (c.Contains("OneHandedAxe") || c.Contains("TwoHandedAxe")) return "axe";
        if (c.Contains("Mace") || c.Contains("TwoHandedMace")) return "mace";
        return "other";
    }

    /// <summary>
    /// The overrides the hero's artifacts add to his kit. <paramref name="kitItems"/> is the kit's first set (slot -> item id, what
    /// Bannerlord gives a hero); <paramref name="mounted"/> = the hero rides; <paramref name="heroTier"/> = CK3 title tier (tier
    /// &lt;= 1 always wears armour and helmet artifacts). <paramref name="log"/> receives one line per decision.
    /// </summary>
    public Dictionary<string, string> Resolve(IReadOnlyDictionary<string, string> kitItems, string blCulture, bool mounted, int heroTier,
        IEnumerable<ArtifactSnapshot> artifacts, List<string>? log = null)
    {
        var eq = new Dictionary<string, string>(kitItems, StringComparer.Ordinal);
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        void Set(string slot, string id, string why) { eq[slot] = id; overrides[slot] = id; log?.Add($"{slot} = {id} ({why})"); }
        int Tier(string? id) => id is not null && _items.TryGetValue(id, out var it) ? it.Tier ?? -1 : -1;
        string? At(string slot) => eq.TryGetValue(slot, out var id) && id.Length > 0 ? id : null;
        string Grp(string slot) => At(slot) is { } id ? GroupOf(_items.GetValueOrDefault(id)) : "empty";
        List<string> SlotsOf(Func<string, bool> pred) => ItemSlots.Where(pred).ToList();
        string culture = _map.Cultures.Contains(blCulture) ? blCulture : _map.CultureFallback.GetValueOrDefault(blCulture) ?? _map.CultureFallback["*"];
        string mode = mounted ? "mounted" : "foot";

        // Armour, then the crown (so the crown wins Head), then weapons.
        static int Order(ArtifactSnapshot a) => a.SlotType == "armor" ? 0 : a.SlotType == "helmet" ? 1 : 2;
        foreach (var art in artifacts.OrderBy(Order))
        {
            if (!_map.TypeToCategory.TryGetValue(art.Type, out var category)) { log?.Add($"skip {art.Type}: no battle category"); continue; }
            var entry = _map.Items.GetValueOrDefault(category)?.GetValueOrDefault(culture)?.GetValueOrDefault(art.Rarity)?.GetValueOrDefault(mode);
            if (entry is null) { log?.Add($"skip {art.Type}/{art.Rarity}: no entry"); continue; }

            if (!string.IsNullOrEmpty(entry.Body) || !string.IsNullOrEmpty(entry.Head))
            {
                string? featureHead = art.HelmetFeature is { } f
                    ? _map.ArmorHelmetByFeature.GetValueOrDefault(f)?.GetValueOrDefault(culture)?.GetValueOrDefault(art.Rarity) : null;
                string? head = !string.IsNullOrEmpty(featureHead) ? featureHead : entry.Head;
                foreach (var (slot, id, isHead) in new[] { ("Body", entry.Body, false), ("Head", head, true) })
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    if (heroTier <= 1) { Set(slot, id, $"{category} {art.Rarity}: tier {heroTier} wears his artifact"); continue; }
                    // Never downgrade the KIT item; a crown (applied after the armour) replaces an armour helmet anyway.
                    int ours = ArmourOf(id, isHead) ?? 0;
                    int kit = kitItems.TryGetValue(slot, out var kitId) && kitId.Length > 0 ? ArmourOf(kitId, isHead) ?? 0 : -1;
                    if (ours >= kit) Set(slot, id, $"{category} {art.Rarity}: {(isHead ? "head" : "body")} {ours} >= kit {kit}");
                    else log?.Add($"keep {slot} {At(slot)} (kit {(isHead ? "head" : "body")} {kit} > {ours})");
                }
                continue;
            }

            if (!CategoryGroup.TryGetValue(category, out var group) || entry.Weapon is not { Length: > 0 } weapon)
            {
                log?.Add($"skip {category}: no weapon");
                continue;
            }
            var sameGroup = SlotsOf(s => Grp(s) == group);
            var empty = SlotsOf(s => Grp(s) == "empty");
            var thrown = SlotsOf(s => Grp(s) == "thrown");
            var polearms = SlotsOf(s => Grp(s) == "polearm");
            var shields = SlotsOf(s => Grp(s) == "shield");
            var melee = SlotsOf(s => Melee.Contains(Grp(s)));

            if (_map.Categories.GetValueOrDefault(category)?.Kind == "ranged")
            {
                string ammoType = _map.Categories[category].AmmoType == "Bolts" ? "bolts" : "arrows";
                var kitAmmo = SlotsOf(s => Grp(s) == ammoType);
                // Free for the launcher and its ammo: empty slots, thrown weapons, then polearms (a second polearm first). The
                // bow/crossbow IS the character's primary armament in CK3, so it may displace the kit's lance; never a shield or
                // the main melee weapon. The other launcher kind (a kit bow for a crossbow artifact and vice versa) and its ammo
                // are free as well.
                string otherGroup = group == "bow" ? "crossbow" : "bow";
                string otherAmmoType = ammoType == "arrows" ? "bolts" : "arrows";
                var otherLauncher = SlotsOf(s => Grp(s) == otherGroup);
                var otherAmmo = SlotsOf(s => Grp(s) == otherAmmoType);
                var free = new Queue<string>(empty.Concat(thrown).Concat(polearms.Skip(1)).Concat(polearms.Take(1)));
                string? launcherSlot = sameGroup.FirstOrDefault() ?? otherLauncher.FirstOrDefault();
                string? ammoSlot = kitAmmo.Count > 0 ? null : otherAmmo.FirstOrDefault();
                int needSlots = (launcherSlot is null ? 1 : 0) + (kitAmmo.Count > 0 || ammoSlot is not null ? 0 : 1);
                if (free.Count < needSlots) { log?.Add($"skip {category}: needs {needSlots} free weapon slot(s)"); continue; }
                launcherSlot ??= free.Dequeue();
                string? current = At(launcherSlot);
                if (current is not null && GroupOf(_items.GetValueOrDefault(current)) == group && Tier(current) > Tier(weapon))
                    log?.Add($"keep {launcherSlot} {current} (tier {Tier(current)} > {Tier(weapon)})");
                else if (current == weapon) log?.Add($"keep {launcherSlot} {weapon} (already in the kit)");
                else Set(launcherSlot, weapon, $"{category} {art.Rarity}");
                string ammo = entry.Ammo ?? "";
                if (kitAmmo.Count > 0)
                {
                    foreach (var s in kitAmmo)
                        if (Tier(ammo) > Tier(At(s))) Set(s, ammo, "better ammo");
                }
                else Set(ammoSlot ?? free.Dequeue(), ammo, $"{ammoType} for the {category}");
                foreach (var s in otherAmmo)
                    if (s != ammoSlot && At(s) is { } id && GroupOf(_items.GetValueOrDefault(id)) == otherAmmoType)
                        Set(s, ammo, $"{ammoType} instead of unusable {otherAmmoType}");
                continue;
            }

            string? target = null, why = "";
            if (sameGroup.Count > 0) { target = sameGroup[0]; why = "same class group"; }
            else if (category == "dagger") { target = empty.FirstOrDefault(); why = "empty slot"; }
            else if (Melee.Contains(group))
            {
                target = melee.FirstOrDefault() ?? empty.FirstOrDefault() ?? thrown.FirstOrDefault() ?? polearms.FirstOrDefault();
                why = "main melee weapon / free slot";
            }
            else if (group == "polearm")
            {
                target = empty.FirstOrDefault() ?? thrown.FirstOrDefault() ?? (shields.Count > 1 ? shields[1] : null) ?? melee.FirstOrDefault();
                why = "free slot / main melee weapon";
            }
            if (target is null) { log?.Add($"skip {category}: no slot"); continue; }
            if (sameGroup.Count > 0 && Tier(At(target)) > Tier(weapon)) { log?.Add($"keep {target} {At(target)} (tier {Tier(At(target))} > {Tier(weapon)})"); continue; }
            if (At(target) == weapon) { log?.Add($"keep {target} {weapon} (already in the kit)"); continue; }
            Set(target, weapon, $"{category} {art.Rarity}, {why}");
        }
        return overrides;
    }

    private int? ArmourOf(string id, bool head) => _items.GetValueOrDefault(id)?.Armor is { } a ? head ? a.Head : a.Body : null;

    /// <summary>The kit's first set (what Bannerlord gives a hero at spawn), first item per slot.</summary>
    public static Dictionary<string, string> KitItems(RulerKit kit)
    {
        var items = new Dictionary<string, string>(StringComparer.Ordinal);
        if (kit.Sets.Count > 0)
            foreach (var it in kit.Sets[0].Items) items.TryAdd(it.Slot, it.Id);
        return items;
    }
}
