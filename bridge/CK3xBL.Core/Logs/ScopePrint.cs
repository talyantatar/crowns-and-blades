// Parses CK3 scope prints as they appear in info_log_scopes dumps (PROTOCOL §1.1). Examples (control bytes already stripped):
//   Gunda Kakatiya of c_orangallu (Internal ID: 10811 - Historical ID 189226) weak (Character - 10811)!
//   KO_nC_ek Lowborn of  (Internal ID 23538) weak (Character - 23538)!            <- lowborn: no title, no colon after "ID"
//   (no character) weak (Character - 4294967295)!                                   <- empty scope
//   1st Raiders of Hepung weak (Army - 16777235)!
//   'TOOLTIP:MEN_AT_ARMS_TYPE,armored_footmen L; Armored Footmen!!' (Internal ID: '16706' - Type: 'personal' ) weak (Regiment - 16706)!
//   Sanda (Internal ID: 8199) weak (Province - 8199)!
//   Sanda (Internal ID: 10110 - Internal Key: c_sanda) weak (Landed_title - 10110)!
//   Battle of Sanda weak (Combat - 134217751)!
using System.Globalization;
using System.Text.RegularExpressions;

namespace CK3xBL.Core.Logs;

public sealed class ScopePrint
{
    /// <summary>Printed id of an empty scope (uint32 -1).</summary>
    public const long NullId = 4294967295;

    public string Type = "";                   // Character | Army | Regiment | Province | Landed_title | Combat | Combat_side | ...
    public long Id;
    public string Name = "";                   // display part before the id block
    public string? Key;                        // "Internal Key: c_sanda" (titles, houses)
    public string? HistoricalId;               // characters with a history entry
    public string? TitleKey;                   // characters: "<name> of <title key>" (empty for the landless)
    public string? MaaKey;                     // regiments
    public string? RegimentType;               // regiments: personal | title | unknown
    public string Raw = "";

    public bool IsNull => Id == NullId;

    // The separator follows the game language: " - " in English, a no-break space and an em dash in Russian
    // ("(Character — 54591)!"), so any dash between any spaces.
    private static readonly Regex TypeId = new(
        @"\((?<type>[A-Za-z_]+)\s+[-‒–—―]\s+(?<id>\d+)\)!?\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InternalKey = new(@"Internal Key: (?<key>[A-Za-z0-9_\-]*)\)", RegexOptions.Compiled);
    private static readonly Regex HistoricalIdRx = new(@"Historical ID (?<hid>[A-Za-z0-9_]+)", RegexOptions.Compiled);
    private static readonly Regex MaaRx = new(@"MEN_AT_ARMS_TYPE,(?<maa>[A-Za-z0-9_]+)", RegexOptions.Compiled);
    private static readonly Regex RegimentTypeRx = new(@"Type: '(?<t>[A-Za-z_]+)'", RegexOptions.Compiled);
    private static readonly Regex CharacterNameRx = new(@"^(?<name>.*?) of (?<title>\S*)\s*$", RegexOptions.Compiled);

    /// <summary>Returns null when the text has no "(Type - id)" block.</summary>
    public static ScopePrint? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = LogText.StripControl(text).Trim();
        var m = TypeId.Match(text);
        if (!m.Success) return null;

        var p = new ScopePrint
        {
            Raw = text,
            Type = m.Groups["type"].Value,
            Id = long.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture),
        };

        // Everything before " weak (" (or before the id block if "weak" is missing) is the descriptive part.
        string head = text[..m.Index].TrimEnd();
        if (head.EndsWith(" weak", StringComparison.Ordinal)) head = head[..^5].TrimEnd();
        else if (head == "weak") head = "";

        if (InternalKey.Match(head) is { Success: true } k && k.Groups["key"].Value.Length > 0) p.Key = k.Groups["key"].Value;
        if (HistoricalIdRx.Match(head) is { Success: true } h) p.HistoricalId = h.Groups["hid"].Value;

        switch (p.Type)
        {
            case "Regiment":
                if (MaaRx.Match(head) is { Success: true } maa) p.MaaKey = maa.Groups["maa"].Value;
                if (RegimentTypeRx.Match(head) is { Success: true } rt) p.RegimentType = rt.Groups["t"].Value;
                int quoteEnd = head.IndexOf("!!'", StringComparison.Ordinal);
                int semi = head.IndexOf(" L; ", StringComparison.Ordinal);
                p.Name = semi >= 0 && quoteEnd > semi ? head[(semi + 4)..quoteEnd] : p.MaaKey ?? "";
                break;
            case "Character":
                string beforeId = CutAtInternalId(head);
                if (CharacterNameRx.Match(beforeId) is { Success: true } cn)
                {
                    p.Name = cn.Groups["name"].Value.Trim();
                    var title = cn.Groups["title"].Value;
                    p.TitleKey = title.Length > 0 ? title : null;
                }
                else p.Name = beforeId.Trim();
                if (p.Name == "(no character)") p.Name = "";
                break;
            default:
                p.Name = CutAtInternalId(head).Trim();
                break;
        }
        return p;
    }

    private static string CutAtInternalId(string head)
    {
        int i = head.IndexOf(" (Internal ID", StringComparison.Ordinal);
        if (i < 0) i = head.IndexOf("(Internal ID", StringComparison.Ordinal);
        return i >= 0 ? head[..i] : head;
    }

    public override string ToString() => $"{Type}:{Id} {Name}";
}

internal static class LogText
{
    /// <summary>CK3 wraps tooltip fragments in \x15 / \x16 (PROTOCOL §1.1).</summary>
    public static string StripControl(string s)
    {
        if (s.IndexOf('\x15') < 0 && s.IndexOf('\x16') < 0) return s;
        return s.Replace("\x15", "").Replace("\x16", "");
    }
}
