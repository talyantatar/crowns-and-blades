// Battles at sea and landings with War Sails (PROTOCOL §44). CK3 has no ships, so the bridge fits each side's men into a fleet of its
// culture's War Sails hulls: heavy, medium and light ships in turn until the crews hold the side's agents, at most MaxShips. A landing
// (a CK3 battle the attackers fight right off their ships) takes only hulls that can sail shallow water, as the DLC's raid does.
// Scenes: a coastal sea for a battle at sea, a raided village of the defenders' culture for a landing.
using CK3xBL.Protocol;

namespace CK3xBL.Mapping.Planning;

public static class NavalPlanner
{
    /// <summary>Ships a side deploys at sea (NavalDLCShipDeploymentModel.MaxShipDeploymentLimit = 8).</summary>
    public const int MaxShips = 8;
    /// <summary>Ships that land in a raid. NavalRaidMissionController keeps four landing paths, but the fourth ship already indexes
    /// past them (bench 2026-10-20: 4 ships crashed OnDeploymentFinished with an IndexOutOfRangeException, 3 landed and fought;
    /// live b298 crashed with 8).</summary>
    public const int RaidMaxShips = 3;
    /// <summary>Of the loser's men who fled a battle at sea, the share that drowned (counted as killed).</summary>
    public const double DrownedShareOfFled = 0.5;
    /// <summary>Of the men War Sails reports killed (it reports no wounded), the share counted dead: a land battle's share (bench
    /// 2026-10-20: 90 killed, 172 wounded).</summary>
    public const double KilledShareOfDowned = 0.35;

    /// <summary>War Sails hulls (NavalDLC ship_hulls.xml): crew capacity and whether they sail shallow water (a landing needs it).</summary>
    public static readonly IReadOnlyDictionary<string, (int Crew, bool Shallow)> Hulls = new Dictionary<string, (int, bool)>(StringComparer.Ordinal)
    {
        ["nord_medium_ship"] = (106, true), ["northern_medium_ship"] = (54, true), ["northern_light_ship"] = (32, true),
        ["sturgia_heavy_ship"] = (96, true), ["battanian_medium_ship"] = (50, true), ["battanian_light_ship"] = (36, true),
        ["vlandia_heavy_ship"] = (108, false), ["western_medium_ship"] = (60, false), ["western_light_ship"] = (32, true),
        ["empire_heavy_ship"] = (120, false), ["empire_medium_ship"] = (60, false), ["central_light_ship"] = (30, true),
        ["aserai_heavy_ship"] = (112, false), ["eastern_medium_ship"] = (60, false), ["eastern_heavy_ship"] = (94, false),
        ["khuzait_heavy_ship"] = (100, false),
    };

    /// <summary>Fleets by naval tradition: heavy, medium, light.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Fleets = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["nord"] = new[] { "nord_medium_ship", "northern_medium_ship", "northern_light_ship" },
        ["sturgia"] = new[] { "sturgia_heavy_ship", "northern_medium_ship", "northern_light_ship" },
        ["battania"] = new[] { "battanian_medium_ship", "battanian_medium_ship", "battanian_light_ship" },
        ["vlandia"] = new[] { "vlandia_heavy_ship", "western_medium_ship", "western_light_ship" },
        ["empire"] = new[] { "empire_heavy_ship", "empire_medium_ship", "central_light_ship" },
        ["aserai"] = new[] { "aserai_heavy_ship", "eastern_medium_ship", "central_light_ship" },
        ["khuzait"] = new[] { "khuzait_heavy_ship", "eastern_heavy_ship", "eastern_medium_ship" },
    };

    /// <summary>The naval tradition of a side: the Norse (CK3 North Germanic heritage) sail drakkars, everyone else their Bannerlord
    /// culture's ships.</summary>
    public static string Tradition(string? blCulture, string? heritage) =>
        heritage == "heritage_north_germanic" ? "nord" : blCulture is not null && Fleets.ContainsKey(blCulture) ? blCulture : "vlandia";

    /// <summary>The ships that carry <paramref name="agents"/> men: the tradition's hulls in turn (for a landing shallow ones only,
    /// the largest first), until their crews hold everyone or <see cref="MaxShips"/> sail (<see cref="RaidMaxShips"/> for a landing).
    /// At least one ship.</summary>
    public static List<string> Fleet(string tradition, int agents, bool shallowOnly)
    {
        var hulls = Fleets.GetValueOrDefault(tradition) ?? Fleets["vlandia"];
        var usable = shallowOnly ? hulls.Where(h => Hulls[h].Shallow).OrderByDescending(h => Hulls[h].Crew).ToArray() : hulls;
        if (usable.Length == 0) usable = new[] { "western_light_ship" };
        int max = shallowOnly ? RaidMaxShips : MaxShips;
        var fleet = new List<string>();
        int crew = 0;
        for (int i = 0; fleet.Count < max && (fleet.Count == 0 || crew < agents); i++)
        {
            string hull = usable[i % usable.Length];
            fleet.Add(hull);
            crew += Hulls[hull].Crew;
        }
        return fleet;
    }

    /// <summary>Coastal seas of the War Sails custom battle (naval_custom_battle_scenes.xml).</summary>
    public static readonly string[] SeaScenes =
    {
        "battle_terrain_coastal_01", "battle_terrain_coastal_02", "battle_terrain_coastal_03", "battle_terrain_coastal_04",
        "battle_terrain_coastal_07", "battle_terrain_coastal_011", "battle_terrain_coastal_013", "battle_terrain_coastal_caspian_sea_02",
        "battle_terrain_coastal_021", "battle_terrain_coastal_022", "battle_terrain_coastal_south_europe_02", "coastal_terrain_north_of_the_north_sea_01",
    };

    public const string OpenSeaScene = "battle_terrain_opensea_northern";

    /// <summary>Raided villages by Bannerlord culture (naval_custom_battle_scenes.xml, is_naval_raid_map).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> RaidScenes = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["empire"] = new[] { "empire_village_e_navalraid", "empire_village_r_navalraid", "empire_village_x_navalraid", "empire_village_003_navalraid" },
        ["aserai"] = new[] { "aserai_village_j_raid", "aserai_village_m_navalraid", "aserai_village_k_navalraid" },
        ["vlandia"] = new[] { "vlandia_village_b_navalraid", "vlandia_village_m_navalraid" },
        ["sturgia"] = new[] { "sturgia_village_k_navalraid", "sturgia_village_g_navalraid_v2" },
        ["khuzait"] = new[] { "khuzait_village_h_navalraid", "khuzait_village_k_navalraid" },
        ["battania"] = new[] { "battania_village_d_raid" },
        ["nord"] = new[] { "nord_village_f_raid", "nord_village_e_navalraid" },
    };

    public static string RaidScene(string tradition, int bid)
    {
        var pool = RaidScenes.GetValueOrDefault(tradition) ?? RaidScenes["vlandia"];
        return pool[Math.Abs(bid) % pool.Length];
    }

    /// <summary>A battle at sea: a coastal sea picked by bid; the open sea when <paramref name="openSea"/>.</summary>
    public static string SeaScene(int bid, bool openSea) => openSea ? OpenSeaScene : SeaScenes[Math.Abs(bid) % SeaScenes.Length];

    /// <summary>The wind: strength by bid between a breeze and a gale, direction random.</summary>
    public static NavalInfo Info(bool raid, List<string> attackerShips, List<string> defenderShips, int bid, bool openSea) => new()
    {
        Raid = raid,
        AttackerShips = attackerShips,
        DefenderShips = raid ? new List<string>() : defenderShips,
        WindStrength = 0.3f + 0.1f * (Math.Abs(bid) % 5),
        WindDirection = "Random",
        Terrain = raid ? "River" : openSea ? "OpenSea" : "CoastalSea",
    };
}
