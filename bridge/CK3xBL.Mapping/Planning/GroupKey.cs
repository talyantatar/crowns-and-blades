// Troop group keys (TroopRequest.group, echoed back per group in result.json, PROTOCOL §3.2/§3.5):
//   levy#<armyTag>                           the levies of one army
//   maa:<maaKey>#<armyTag>#<regimentId>     one men-at-arms regiment
//   knight-retinue#<charTag>                 the retinue agents of one knight (not CK3 soldiers; never written back)
using System.Globalization;

namespace CK3xBL.Mapping.Planning;

public enum GroupKind { Levy, Maa, Retinue }

public readonly record struct GroupKey(GroupKind Kind, int ArmyTag, string? MaaKey, long RegimentId, int CharTag)
{
    public static GroupKey Levy(int armyTag) => new(GroupKind.Levy, armyTag, null, 0, 0);
    public static GroupKey Maa(string maaKey, int armyTag, long regimentId) => new(GroupKind.Maa, armyTag, maaKey, regimentId, 0);
    public static GroupKey Retinue(int charTag) => new(GroupKind.Retinue, 0, null, 0, charTag);

    public override string ToString() => Kind switch
    {
        GroupKind.Levy => $"levy#{ArmyTag.ToString(CultureInfo.InvariantCulture)}",
        GroupKind.Maa => $"maa:{MaaKey}#{ArmyTag.ToString(CultureInfo.InvariantCulture)}#{RegimentId.ToString(CultureInfo.InvariantCulture)}",
        _ => $"knight-retinue#{CharTag.ToString(CultureInfo.InvariantCulture)}",
    };

    public static bool TryParse(string? s, out GroupKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(s)) return false;
        var parts = s.Split('#');
        static bool Int(string x, out int v) => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        static bool Long(string x, out long v) => long.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        if (parts.Length == 2 && parts[0] == "levy" && Int(parts[1], out var army)) { key = Levy(army); return true; }
        if (parts.Length == 2 && parts[0] == "knight-retinue" && Int(parts[1], out var ch)) { key = Retinue(ch); return true; }
        if (parts.Length == 3 && parts[0].StartsWith("maa:", StringComparison.Ordinal) && parts[0].Length > 4 &&
            Int(parts[1], out var a) && Long(parts[2], out var r))
        {
            key = Maa(parts[0][4..], a, r);
            return true;
        }
        return false;
    }
}
