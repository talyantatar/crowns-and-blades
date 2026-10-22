// The language the player runs CK3 in (PROTOCOL §26): pdx_settings.txt in the CK3 user folder holds
//     "language"={ version=0 value="l_russian" }
// The bridge writes its cards and Bannerlord texts in that language when it has them (English and Russian), else in English.
using System.Text.RegularExpressions;

namespace CK3xBL.Core.Ck3;

public static partial class Ck3Language
{
    public const string English = "en", Russian = "ru";

    /// <summary>"ru" for a CK3 set to Russian, else "en" (also when the file is missing or unreadable).</summary>
    public static string Read(string? ck3UserDir)
    {
        if (string.IsNullOrWhiteSpace(ck3UserDir)) return English;
        try
        {
            string path = Path.Combine(ck3UserDir, "pdx_settings.txt");
            return File.Exists(path) ? FromSettings(File.ReadAllText(path)) : English;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return English;
        }
    }

    /// <summary>The bridge language of a pdx_settings.txt text.</summary>
    public static string FromSettings(string settings)
    {
        var m = LanguageValue().Match(settings ?? "");
        return m.Success && m.Groups[1].Value == "l_russian" ? Russian : English;
    }

    /// <summary>The configured choice ("auto", "en", "ru"): auto follows CK3.</summary>
    public static string Resolve(string? configured, string? ck3UserDir) => configured?.Trim().ToLowerInvariant() switch
    {
        Russian => Russian,
        English => English,
        _ => Read(ck3UserDir),
    };

    [GeneratedRegex("\"language\"\\s*=\\s*\\{[^}]*?value\\s*=\\s*\"(l_\\w+)\"", RegexOptions.Singleline)]
    private static partial Regex LanguageValue();
}
