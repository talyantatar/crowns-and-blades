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

    /// <summary>Seats the elephant regiments of both sides on elephants; returns how many groups it changed.</summary>
    public static int Mount(BattleRequest request, MappingTables t)
    {
        int groups = 0;
        foreach (var side in new[] { request.Attacker, request.Defender })
        {
            if (side?.Troops is null) continue;
            foreach (var troop in side.Troops)
            {
                if (!GroupKey.TryParse(troop.Group, out var key) || key.Kind != GroupKind.Maa || !IsElephantMaa(t, key.MaaKey)) continue;
                troop.Mount = ElephantModule.ElephantItem;
                troop.MountHarness = HarnessOf(key.MaaKey!);
                troop.Count = Math.Max(1, (int)Math.Round(troop.Count / (double)MenPerElephant, MidpointRounding.AwayFromZero));
                groups++;
            }
        }
        return groups;
    }
}
