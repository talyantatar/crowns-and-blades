// Small text helpers for names and presentation strings shown in Bannerlord and on the transition cards.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CK3xBL.Mapping.Planning;

public static partial class Text
{
    private static readonly TextInfo Title = CultureInfo.InvariantCulture.TextInfo;

    /// <summary>Bannerlord names go through TextObject markup: no braces, no dots (PROTOCOL §3.2); whitespace collapsed.</summary>
    public static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var sb = new StringBuilder(name.Length);
        foreach (char ch in name)
        {
            if (ch is '{' or '}' or '.' || char.IsControl(ch)) sb.Append(' ');
            else sb.Append(ch);
        }
        return Spaces().Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>"c_orangallu" -> "Orangallu", "k_west_francia" -> "West Francia".</summary>
    public static string TitleKeyToName(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";
        string k = key.Trim();
        if (k.Length > 2 && k[1] == '_' && "bcdkeh".Contains(k[0])) k = k[2..];
        return Title.ToTitleCase(k.Replace('_', ' '));
    }

    /// <summary>
    /// A CK3 army name as an object of a sentence: "1st Raiders of Hepung" -> "the Raiders of Hepung",
    /// "Bhupati Phoro's Host" -> "Bhupati Phoro's Host".
    /// </summary>
    public static string ArmyAsObject(string? armyName)
    {
        string n = SanitizeName(armyName);
        if (n.Length == 0) return "the enemy host";
        n = Ordinal().Replace(n, "");
        if (n.Contains("'s ", StringComparison.Ordinal) || n.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) return n;
        return "the " + n;
    }

    public static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Season for display; Bannerlord's "fall" reads as CK3's "Autumn".</summary>
    public static string SeasonName(string season) => season switch
    {
        "spring" => "Spring",
        "summer" => "Summer",
        "fall" => "Autumn",
        "winter" => "Winter",
        _ => Capitalize(season),
    };

    public static string TerrainName(string terrain) => terrain switch
    {
        "terraced_hills" => "Terraced hills",
        "desert_mountains" => "Desert mountains",
        "coastal_sea" or "sea" => "Coast",
        _ => Capitalize(terrain.Replace('_', ' ')),
    };

    public static string Number(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^\d+(st|nd|rd|th)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex Ordinal();
}
