// What the armory modules need before Bannerlord loads them for the bridge (PROTOCOL §21, §43):
//  - a Shaders folder an item pack ships was built for another game version and crashes Bannerlord v1.4.8 at start (live
//    2026-10-17, Open Source Armory): it is renamed Shaders.ck3xbl_off, and the game builds its own shaders;
//  - Terra Armarium declares its items for the campaign only, so the custom battle the bridge fights in would not load them: its
//    SubModule.xml gets CustomGame next to CampaignStoryMode (the original kept as SubModule.xml.ck3xbl_bak).
// Each change is made once and logged; a failure leaves the module as it was.
using System.Text;
using System.Text.RegularExpressions;

namespace CK3xBL.Core.Bannerlord;

public static class ArmoryCompat
{
    public const string DisabledShaders = "Shaders.ck3xbl_off";
    public const string Backup = ".ck3xbl_bak";

    /// <summary>Prepares the installed <paramref name="modules"/>; returns one journal line per change or failure.</summary>
    public static List<string> Prepare(string gameDir, IEnumerable<string> modules)
    {
        var lines = new List<string>();
        foreach (var module in modules)
        {
            var dir = Path.Combine(gameDir, "Modules", module);
            if (!Directory.Exists(dir)) continue;
            try
            {
                var shaders = Path.Combine(dir, "Shaders");
                if (Directory.Exists(shaders) && !Directory.Exists(Path.Combine(dir, DisabledShaders)))
                {
                    Directory.Move(shaders, Path.Combine(dir, DisabledShaders));
                    lines.Add($"{module}: its Shaders folder (built for another game version) renamed {DisabledShaders}");
                }
                var sub = Path.Combine(dir, "SubModule.xml");
                if (module.StartsWith("BL.Terra_Armarium", StringComparison.Ordinal) && File.Exists(sub))
                {
                    var text = File.ReadAllText(sub);
                    var fixedText = WithCustomGame(text);
                    if (fixedText != text)
                    {
                        if (!File.Exists(sub + Backup)) File.Copy(sub, sub + Backup);
                        File.WriteAllText(sub, fixedText, new UTF8Encoding(false));
                        lines.Add($"{module}: items loaded in custom battles too (SubModule.xml, original kept as SubModule.xml{Backup})");
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                lines.Add($"{module}: not prepared ({e.Message})");
            }
        }
        return lines;
    }

    /// <summary>A SubModule.xml whose campaign-only XML nodes also load in CustomGame (unchanged when every node already does).</summary>
    public static string WithCustomGame(string subModuleXml) =>
        Regex.Replace(subModuleXml, @"<IncludedGameTypes>(?:(?!</IncludedGameTypes>)[\s\S])*</IncludedGameTypes>", m =>
            m.Value.Contains("\"CustomGame\"", StringComparison.Ordinal) || !m.Value.Contains("\"CampaignStoryMode\"", StringComparison.Ordinal)
                ? m.Value
                : Regex.Replace(m.Value, @"(<GameType\s+value=""CampaignStoryMode""\s*/>)", "$1<GameType value=\"CustomGame\"/>"));
}
