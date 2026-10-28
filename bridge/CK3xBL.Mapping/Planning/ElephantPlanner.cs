// War elephants (PROTOCOL §43). With the elephants module loaded (MapperOptions.Elephants), every CK3 regiment whose men-at-arms base
// type is elephant cavalry (war elephants, the elephantiers accolade, the ballista elephants) rides elephants: the module seats the
// group's troop on the elephant item with a harness. An elephant carries the weight of several horsemen (500 hit points, a 400
// charge), so the group sends one elephant per MenPerElephant agents it would have fielded; the outcome converts its agents to the
// regiment's CK3 men as for any group.
using CK3xBL.Core.Bannerlord;
using CK3xBL.Mapping.Data;
using CK3xBL.Protocol;

namespace CK3xBL.Mapping.Planning;

public static class ElephantPlanner
{
    public const string ElephantBaseType = "elephant_cavalry";
    public const int MenPerElephant = 4;

    public static bool IsElephantMaa(MappingTables t, string? maaKey) =>
        maaKey is not null && (t.Ck3.MaaTypes.GetValueOrDefault(maaKey)?.BaseType ?? t.Mapping.MaaTier.GetValueOrDefault(maaKey)?.BaseType) == ElephantBaseType;

    /// <summary>The harness of a regiment: royal armour for the acclaimed elephantiers, iron for war elephants, leather otherwise.</summary>
    public static string HarnessOf(string maaKey) => maaKey switch
    {
        "accolade_maa_elephantiers" => "be_elephant_armor_tier_3",
        "war_elephant" => "be_elephant_armor_kushan_tier_2",
        _ => "be_elephant_armor_sassanid_tier_1",
    };

    /// <summary>
    /// Who rides a war elephant (the elephantiers too): the culture's best heavy lancer, shield and lance (the user's choice,
    /// 2026-10-28; they rode as whatever the cavalry ladder gave).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Lancers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["vlandia"] = "vlandian_banner_knight", ["empire"] = "imperial_elite_cataphract", ["sturgia"] = "druzhinnik_champion",
        ["aserai"] = "aserai_vanguard_faris", ["khuzait"] = "khuzait_heavy_lancer", ["battania"] = "vlandian_banner_knight",
    };

    /// <summary>Who rides a ballista elephant: the culture's best mounted archer.</summary>
    public static readonly IReadOnlyDictionary<string, string> Archers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["empire"] = "bucellarii", ["khuzait"] = "khuzait_khans_guard", ["aserai"] = "aserai_mameluke_heavy_cavalry",
    };
    public const string DefaultArcher = "khuzait_khans_guard";

    /// <summary>
    /// Seats the elephant regiments of both sides on elephants; returns how many groups it changed. Not in an assault: elephants
    /// climb no ladders and fit no tower, so their crews storm the walls on foot, every man of them (2026-10-28: 886 elephant men
    /// went to a siege as 158 elephants that could not reach the walls).
    /// </summary>
    public static int Mount(BattleRequest request, MappingTables t)
    {
        if (request.Kind == "siege") return 0;
        int groups = 0;
        foreach (var side in new[] { request.Attacker, request.Defender })
        {
            if (side?.Troops is null) continue;
            string culture = side.Culture ?? "vlandia";
            foreach (var troop in side.Troops)
            {
                if (!GroupKey.TryParse(troop.Group, out var key) || key.Kind != GroupKind.Maa || !IsElephantMaa(t, key.MaaKey)) continue;
                string rider = key.MaaKey == "ballista_elephant" ? Archers.GetValueOrDefault(culture, DefaultArcher) : Lancers.GetValueOrDefault(culture, Lancers["vlandia"]);
                if (t.Troops.ContainsKey(rider)) troop.Id = rider;
                troop.Mount = ElephantModule.ElephantItem;
                troop.MountHarness = HarnessOf(key.MaaKey!);
                troop.Count = Math.Max(1, (int)Math.Round(troop.Count / (double)MenPerElephant, MidpointRounding.AwayFromZero));
                groups++;
            }
        }
        return groups;
    }
}
