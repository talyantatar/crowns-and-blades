// Sieges in Bannerlord (PROTOCOL §39). CK3 lets no script read a siege's progress, so it is estimated here with CK3's own siege
// rule (common/defines NSiege): the defenders' morale is 100 + 75 per fort level and falls about 1 a day, 0.01 more for every 200
// besiegers beyond the garrison, slower by 0.7 for every siege tier the besiegers' engines lack against the fort. The estimate
// decides what the besiegers bring to the assault: ladders always, then a siege tower, a ram, a second tower, a breach in the wall.
// Every siege regiment of the army is one Bannerlord engine.
using CK3xBL.Core.Model;
using CK3xBL.Protocol;

namespace CK3xBL.Mapping.Planning;

public static class SiegePlanner
{
    public const double BaseMorale = 100, MoralePerFortLevel = 75, DailyLoss = 1, MinDailyLoss = 0.5, LossPerStack = 0.01, StackSize = 200;
    public const double MissingTierFactor = 0.7;
    /// <summary>CK3's random siege events (breach, starvation, disease, desertion) speed a siege up on top of the daily loss.</summary>
    public const double EventsFactor = 1.25;
    private static readonly int[] FortThresholds = { 4, 6, 11, 16, 31 };

    /// <summary>Progress at which each siege work joins the assault: a tower, the ram, a second tower, a breach.</summary>
    public const double TowerAt = 0.2, RamAt = 0.4, SecondTowerAt = 0.6, BreachAt = 0.8;

    /// <summary>Most ranged engines the besiegers deploy (the stock custom battle's slots).</summary>
    public const int MaxRangedEngines = 4;

    /// <summary>CK3 siege men-at-arms -> the Bannerlord engine one regiment brings, and its CK3 siege tier.</summary>
    public static readonly IReadOnlyDictionary<string, (string Engine, int Tier)> SiegeMaa = new Dictionary<string, (string, int)>(StringComparer.Ordinal)
    {
        ["onager"] = ("Onager", 1),
        ["mangonel"] = ("Catapult", 2),
        ["trebuchet"] = ("Trebuchet", 3),
        ["bombard"] = ("FireTrebuchet", 4),
        ["cannon"] = ("FireTrebuchet", 5),
        ["ballista"] = ("Ballista", 1),
        ["ballista_elephant"] = ("Ballista", 1),
        ["rocket_carts"] = ("FireOnager", 3),
    };

    /// <summary>Siege towers a CK3 regiment brings by itself (the Chinese siege tower and cloud ladder men-at-arms).</summary>
    public static readonly IReadOnlySet<string> TowerMaa = new HashSet<string>(StringComparer.Ordinal) { "siege_tower", "cloud_ladder" };

    /// <summary>The estimated progress of the CK3 siege, 0..1.</summary>
    public static double Progress(int days, int fortLevel, int men, int garrison, int siegeTier)
    {
        double morale = BaseMorale + MoralePerFortLevel * Math.Max(0, fortLevel);
        double stacks = Math.Floor(Math.Max(0, men - garrison) / StackSize);
        double daily = Math.Max(MinDailyLoss, DailyLoss + LossPerStack * stacks);
        int needed = FortThresholds.Count(t => fortLevel >= t);
        double speed = Math.Pow(MissingTierFactor, Math.Max(0, needed - siegeTier));
        return Math.Clamp(Math.Max(0, days) * daily * speed * EventsFactor / morale, 0, 1);
    }

    /// <summary>Breaking into a besieged holding (PROTOCOL §40) costs 10 % of the host against a besieging army half its size or less,
    /// up to 20 % against one twice its size or more; the commander's martial cuts that, by half at 30.</summary>
    public const double BreakInMin = 0.10, BreakInMax = 0.20, BreakInMartialHalf = 30;

    public static double BreakInLoss(int myMen, int besiegers, int martial)
    {
        double ratio = besiegers / (double)Math.Max(1, myMen);
        double loss = BreakInMin + (BreakInMax - BreakInMin) * Math.Clamp((ratio - 0.5) / 1.5, 0, 1);
        return loss * (1 - 0.5 * Math.Clamp(martial, 0, BreakInMartialHalf) / BreakInMartialHalf);
    }

    // ---- a harder assault (PROTOCOL §46) -------------------------------------------------------------------------------

    /// <summary>The garrison are the holding's soldiers, not village levies: heavy foot and archers of a tier that rises with the fort
    /// (fort 1: 2.5, fort 3: 3.5, fort 6 and more: 5), GarrisonRangedShare of them archers.</summary>
    public const double GarrisonTierBase = 2, GarrisonTierPerFort = 0.5, GarrisonTierMax = 5, GarrisonRangedShare = 0.45;

    public static double GarrisonTier(int fortLevel) => Math.Clamp(GarrisonTierBase + GarrisonTierPerFort * Math.Max(1, fortLevel), 2.5, GarrisonTierMax);

    /// <summary>The walls: every fort level gives the defenders +4 % damage (at most +40 %), 3 % less damage taken (at most 30 % less)
    /// and +4 morale (at most +30).</summary>
    public const double WallDamagePerFort = 0.04, WallDamageMax = 0.4, WallGuardPerFort = 0.03, WallGuardMax = 0.3, WallMoralePerFort = 4, WallMoraleMax = 30;

    public static (float Dealt, float Taken, float Morale) WallBonus(int fortLevel)
    {
        int f = Math.Max(1, fortLevel);
        return ((float)(1 + Math.Min(WallDamageMax, WallDamagePerFort * f)), (float)(1 - Math.Min(WallGuardMax, WallGuardPerFort * f)),
            (float)Math.Min(WallMoraleMax, WallMoralePerFort * f));
    }

    /// <summary>At most this many attackers per defender on the field at once; the rest come in waves as the first ones fall.</summary>
    public const float AttackerWaveRatio = 2.5f;

    /// <summary>Bannerlord's castle level by CK3 fort level: 1-2 -> 1, 3-5 -> 2, 6 and more -> 3.</summary>
    public static int SceneLevel(int fortLevel) => fortLevel >= 6 ? 3 : fortLevel >= 3 ? 2 : 1;

    /// <summary>The defenders' engines on the walls by castle level: none, a ballista, a ballista and a mangonel.</summary>
    public static List<string> DefenderEngines(int level) => level switch
    {
        >= 3 => new List<string> { "Ballista", "Catapult" },
        2 => new List<string> { "Ballista" },
        _ => new List<string>(),
    };

    /// <summary>The siege of an assault: works by progress, one engine per siege regiment of the besiegers' armies.</summary>
    public static SiegeInfo Build(BattleSnapshot battle, SideSnapshot besiegers)
    {
        var regiments = besiegers.Armies.SelectMany(a => a.Regiments).Where(r => r.Current > 0 && !string.IsNullOrEmpty(r.MaaKey)).ToList();
        int tier = regiments.Select(r => SiegeMaa.TryGetValue(r.MaaKey, out var m) ? m.Tier : 0).DefaultIfEmpty(0).Max();
        int fort = battle.SiegeFort > 0 ? battle.SiegeFort : battle.Location.FortLevel;
        double progress = Progress(battle.SiegeDays, fort, battle.SiegeMen > 0 ? battle.SiegeMen : besiegers.Size, battle.SiegeGarrison, tier);
        int level = SceneLevel(fort);

        var engines = new List<string>();
        int towers = progress >= SecondTowerAt ? 2 : progress >= TowerAt ? 1 : 0;
        towers += regiments.Count(r => TowerMaa.Contains(r.MaaKey));
        for (int i = 0; i < Math.Min(2, towers); i++) engines.Add("SiegeTower");
        if (progress >= RamAt) engines.Add("Ram");
        engines.AddRange(regiments.Where(r => SiegeMaa.ContainsKey(r.MaaKey)).OrderByDescending(r => SiegeMaa[r.MaaKey].Tier)
            .Select(r => SiegeMaa[r.MaaKey].Engine).Take(MaxRangedEngines));

        return new SiegeInfo
        {
            SceneUpgradeLevel = level,
            WallHitpointPercentages = progress >= BreachAt ? new[] { 0f, 1f } : new[] { 1f, 1f },
            HasSiegeTower = engines.Contains("SiegeTower"),
            AttackerMachines = engines,
            DefenderMachines = DefenderEngines(level),
            Progress = (float)Math.Round(progress, 3),
            AttackerWaveRatio = AttackerWaveRatio,
        };
    }

    /// <summary>The custom battle siege scenes by Bannerlord culture: castles and towns (custom_battle_scenes.xml, v1.4.8).</summary>
    public static readonly IReadOnlyDictionary<string, (string[] Castles, string[] Towns)> Scenes = new Dictionary<string, (string[], string[])>(StringComparer.Ordinal)
    {
        ["vlandia"] = (new[] { "vlandia_castle_005a", "vlandia_castle_a", "vlandia_castle_b", "vlandia_castle_c", "vlandia_castle_d" },
                       new[] { "vlandia_town_a", "vlandia_town_c", "vlandia_town_d", "vlandia_town_e", "vlandia_town_f", "vlandia_town_g", "vlandia_town_h", "vlandia_city_a" }),
        ["empire"] = (new[] { "empire_siege_001", "empire_castle_004", "empire_castle_b", "empire_castle_c", "empire_castle_f", "empire_castle_g" },
                      new[] { "empire_town_a", "empire_town_b", "empire_town_c", "empire_town_d", "empire_town_e", "empire_town_f", "empire_town_g", "empire_town_h",
                              "empire_town_i", "empire_town_k", "empire_town_l", "empire_town_m", "empire_town_n", "empire_town_o", "empire_town_p", "empire_town_q",
                              "empire_town_r", "empire_town_j_siege" }),
        ["sturgia"] = (new[] { "sturgia_castle_003", "sturgia_castle_siege_001", "sturgia_castle_c" },
                       new[] { "sturgia_town_a", "sturgia_town_b", "sturgia_town_c", "sturgia_town_d", "sturgia_town_e", "sturgia_town_f", "sturgia_town_g" }),
        ["aserai"] = (new[] { "aserai_castle_002", "aserai_castle_a", "aserai_castle_c", "aserai_castle_d" },
                      new[] { "aserai_town_a", "aserai_town_b", "aserai_town_c", "aserai_town_d", "aserai_town_e", "aserai_town_f", "aserai_town_g", "aserai_town_h" }),
        ["battania"] = (new[] { "battania_castle_a", "battania_castle_b" },
                        new[] { "battania_town_a", "battania_town_b", "battania_town_c", "battania_town_d", "battania_town_e" }),
        ["khuzait"] = (new[] { "khuzait_castle_siege_001", "khuzait_castle_002" },
                       new[] { "khuzait_city_a", "khuzait_city_b", "khuzait_town_004", "khuzait_town_e", "khuzait_town_f", "khuzait_town_g" }),
    };

    /// <summary>The siege scene of a holding: a castle for a castle (and a tribal holding: Sturgia's palisades), a town for a city
    /// or a temple, of the defenders' Bannerlord culture; picked by bid.</summary>
    public static string Scene(string? culture, string? holding, int bid)
    {
        bool tribal = holding is "tribal_holding" or "nomad_holding" or "herder_holding";
        string c = tribal ? "sturgia" : culture is not null && Scenes.ContainsKey(culture) ? culture : "vlandia";
        var (castles, towns) = Scenes[c];
        var pool = holding is "city_holding" or "church_holding" ? towns : castles;
        return pool[Math.Abs(bid) % pool.Length];
    }
}
