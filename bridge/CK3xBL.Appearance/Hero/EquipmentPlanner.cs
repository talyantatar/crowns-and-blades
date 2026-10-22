// Battle kit by rank, culture and sex (bridge/data/bl_ruler_kits.json from sandboxcore_equipment_sets.xml):
//   king/emperor (tier >= 4)  <cult>_king_template_bat_m|f
//   duke (3)                  <cult>_bat_template_heavy|flamboyant (lady for women)
//   count (2)                 <cult>_bat_template_medium (lady)
//   baron (1) / landless (0)  medium kit with lower-tier head and body armour from bl_catalog.json
// When the mapper already chose a roster (it also decides mounted vs foot and may empty the horse slots, PROTOCOL §6.2),
// that roster and its overrides are kept and only augmented. Heroes always wear the roster's first set, so variety
// between lords comes from the armour of another set (head, body, cape, gloves, legs) emitted as overrides; weapons and
// horses stay those of the first set. Every hero keeps a heraldic (banner-drawing) shield; captains carry a banner item.
using CK3xBL.Appearance.Data;
using CK3xBL.Protocol;

namespace CK3xBL.Appearance.Hero;

internal sealed class EquipmentPlanner
{
    private static readonly Dictionary<string, string> LordPrefix = new(StringComparer.Ordinal)
        { ["vlandia"] = "vla", ["sturgia"] = "stu", ["battania"] = "bat", ["khuzait"] = "khu", ["aserai"] = "ase", ["empire"] = "emp" };

    private static readonly string[] WeaponSlots = { "Item0", "Item1", "Item2", "Item3" };
    private static readonly string[] ArmourSlots = { "Head", "Body", "Cape", "Gloves", "Leg" };

    private readonly Dictionary<string, KitRoster> _rosters;
    private readonly List<ShieldItem> _shields;
    private readonly List<BannerItem> _banners;
    private readonly List<CatalogArmor> _armors;
    private readonly HashSet<string> _heraldicIds;

    public EquipmentPlanner(Dictionary<string, KitRoster> rosters, List<ShieldItem> shields, List<BannerItem> banners, List<CatalogArmor> armors)
    {
        _rosters = rosters; _shields = shields; _banners = banners; _armors = armors;
        _heraldicIds = new HashSet<string>(shields.Select(s => s.Id), StringComparer.Ordinal);
    }

    public bool HasData => _rosters.Count > 0;

    /// <summary>
    /// The hero's kit: 'existing' (the mapper's choice) augmented, or a new rank/culture/sex kit when there is none.
    /// Returns 'existing' unchanged when no kit data is available.
    /// </summary>
    public EquipmentSpec? Plan(EquipmentSpec? existing, string blCulture, int tier, bool female, bool captain, long ck3Id, string? heritage,
        out string notes)
    {
        notes = "";
        uint seed = unchecked((uint)ck3Id * 2654435761u);
        string rank = tier >= 4 ? "king" : tier == 3 ? "duke" : tier == 2 ? "count" : tier == 1 ? "baron" : "knight";
        KitRoster? roster = existing?.Roster != null ? Roster(existing.Roster) : null;
        bool fromMapper = roster != null;
        roster ??= ChooseRoster(blCulture, tier, female, heritage, seed);
        if (roster == null || roster.Sets.Count == 0) return existing;
        if (LordPrefix.ContainsKey(roster.Culture)) blCulture = roster.Culture;

        var spec = new EquipmentSpec { Roster = roster.Id };
        if (fromMapper)
            foreach (var (slot, id) in existing!.Overrides) spec.Overrides[slot] = id;
        var fixedSlots = spec.Overrides.Keys.ToHashSet(StringComparer.Ordinal);

        // Effective loadout = first set (what the hero wears) + overrides ("" = emptied slot).
        var loadout = FirstPerSlot(roster.Sets[0]);
        foreach (var (slot, id) in spec.Overrides)
        {
            if (id.Length == 0) loadout.Remove(slot);
            else loadout[slot] = new KitItem(slot, id, 0, _heraldicIds.Contains(id));
        }

        int setIndex = roster.Sets.Count > 1 && tier < 4 ? (int)(seed % (uint)roster.Sets.Count) : 0;
        if (setIndex > 0)
            foreach (var (slot, item) in FirstPerSlot(roster.Sets[setIndex]))
                if (ArmourSlots.Contains(slot) && !fixedSlots.Contains(slot))
                {
                    spec.Overrides[slot] = item.Id;
                    loadout[slot] = item;
                }

        if (tier <= 1) Downgrade(spec, loadout, fixedSlots, blCulture, tier == 1 ? 5 : 4, seed);
        EnsureHeraldicShield(spec, loadout, fixedSlots, blCulture);
        if (captain && !fixedSlots.Contains("Item4") && BannerFor(blCulture, tier) is string banner) spec.Overrides["Item4"] = banner;

        notes = $"{rank} kit {roster.Id}{(fromMapper ? " (mapper's)" : "")}, armour set {setIndex}" +
                (spec.Overrides.Count > 0 ? $": {string.Join(", ", spec.Overrides.Select(kv => kv.Key + "=" + kv.Value))}" : "");
        return spec;
    }

    private KitRoster? ChooseRoster(string blCulture, int tier, bool female, string? heritage, uint seed)
    {
        if (!LordPrefix.TryGetValue(blCulture, out string? prefix)) prefix = "emp";
        string sex = female ? "f" : "m";
        KitRoster? roster;
        if (tier >= 4)
        {
            string kingPrefix = blCulture == "empire" ? EmpireKingPrefix(heritage) : prefix;
            roster = Roster($"{kingPrefix}_king_template_bat_{sex}") ?? Roster($"n_emp_king_template_bat_{sex}");
        }
        else if (tier == 3)
            roster = female ? Roster($"{prefix}_bat_template_lady")
                            : Roster($"{prefix}_bat_template_{(seed % 2 == 0 ? "heavy" : "flamboyant")}") ?? Roster($"{prefix}_bat_template_heavy");
        else
            roster = Roster($"{prefix}_bat_template_{(female ? "lady" : "medium")}");
        return roster ?? Roster($"{prefix}_bat_template_medium") ?? Roster("emp_bat_template_medium");
    }

    private KitRoster? Roster(string id) => _rosters.TryGetValue(id, out var r) ? r : null;

    private static string EmpireKingPrefix(string? heritage) => heritage switch
    {
        "heritage_latin" or "heritage_iberian" or "heritage_frankish" => "w_emp",
        "heritage_byzantine" or "heritage_ancient_greek" => "n_emp",
        _ => "s_emp",
    };

    private static Dictionary<string, KitItem> FirstPerSlot(List<KitItem> set)
    {
        var d = new Dictionary<string, KitItem>(StringComparer.Ordinal);
        foreach (var it in set) d.TryAdd(it.Slot, it);
        return d;
    }

    /// <summary>Barons and landless knights: head and body armour of the culture at a lower tier (troop-used items first).</summary>
    private void Downgrade(EquipmentSpec spec, Dictionary<string, KitItem> loadout, HashSet<string> fixedSlots, string culture, int targetTier, uint seed)
    {
        foreach (var (slot, type) in new[] { ("Head", "HeadArmor"), ("Body", "BodyArmor") })
        {
            if (fixedSlots.Contains(slot)) continue;
            var pool = _armors.Where(a => a.Culture == culture && a.Type == type && a.Tier == targetTier && a.UsedByTroops > 0).ToList();
            if (pool.Count == 0) pool = _armors.Where(a => a.Culture == culture && a.Type == type && a.Tier == targetTier).ToList();
            if (pool.Count == 0) continue;
            pool.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            var pick = pool[(int)(seed % (uint)pool.Count)];
            spec.Overrides[slot] = pick.Id;
            loadout[slot] = new KitItem(slot, pick.Id, pick.Tier, false);
        }
    }

    /// <summary>Adds a banner-drawing shield when the kit has none (e.g. the Khuzait king kit).</summary>
    private void EnsureHeraldicShield(EquipmentSpec spec, Dictionary<string, KitItem> loadout, HashSet<string> fixedSlots, string culture)
    {
        if (WeaponSlots.Any(s => loadout.TryGetValue(s, out var it) && (it.Heraldic || _heraldicIds.Contains(it.Id)))) return;
        var candidates = _shields.Where(s => s.Culture == culture && s.Tier >= 4).ToList();
        if (candidates.Count == 0) candidates = _shields.Where(s => s.Culture == null && s.Tier >= 4).ToList();
        if (candidates.Count == 0) return;
        var shield = candidates.OrderByDescending(s => s.Tier).ThenBy(s => s.Id, StringComparer.Ordinal).First();

        string? slot = WeaponSlots.FirstOrDefault(s => !loadout.ContainsKey(s) && !fixedSlots.Contains(s));
        if (slot == null)
        {
            // Replace a duplicated weapon (e.g. a second quiver) before giving up the last slot.
            var free = WeaponSlots.Where(s => loadout.ContainsKey(s) && !fixedSlots.Contains(s)).ToList();
            slot = free.GroupBy(s => loadout[s].Id).FirstOrDefault(g => g.Count() > 1)?.Last() ?? free.LastOrDefault();
            if (slot == null) return;
        }
        spec.Overrides[slot] = shield.Id;
        loadout[slot] = new KitItem(slot, shield.Id, shield.Tier, true);
    }

    /// <summary>Culture banner item (gives the formation a banner bearer): t3 for kings, t2 dukes/counts, t1 otherwise.</summary>
    private string? BannerFor(string culture, int tier)
    {
        string suffix = tier >= 4 ? "_t3" : tier >= 2 ? "_t2" : "_t1";
        return _banners.FirstOrDefault(b => b.Culture == culture && b.Id.EndsWith(suffix, StringComparison.Ordinal))?.Id
               ?? _banners.FirstOrDefault(b => b.Culture == "neutral_culture" && b.Id.EndsWith(suffix, StringComparison.Ordinal))?.Id;
    }

    /// <summary>Skills from CK3 prowess/martial: weapons clamp(60+8p, 60, 330), Tactics clamp(10m, 0, 300).</summary>
    public static Dictionary<string, int> Skills(int prowess, int martial, int tier, string culture)
    {
        int weapon = Math.Clamp(60 + 8 * prowess, 60, 330);
        int ranged = Math.Clamp(30 + 5 * prowess, 30, 250);
        return new Dictionary<string, int>
        {
            ["OneHanded"] = weapon,
            ["TwoHanded"] = weapon,
            ["Polearm"] = weapon,
            ["Bow"] = culture is "khuzait" or "battania" ? weapon : ranged,
            ["Crossbow"] = ranged,
            ["Throwing"] = ranged,
            ["Riding"] = weapon,
            ["Athletics"] = Math.Clamp(40 + 6 * prowess, 40, 300),
            ["Tactics"] = Math.Clamp(10 * martial, 0, 300),
            ["Leadership"] = Math.Clamp(20 + 8 * martial + 10 * tier, 0, 300),
        };
    }
}
