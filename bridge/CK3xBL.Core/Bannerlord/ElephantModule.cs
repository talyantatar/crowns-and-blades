// War elephants (PROTOCOL §43). Bannerlord has none; Burning Empires ships them in its own module, BurningEmpires_Elephants, which
// depends on the whole Burning Empires conversion (its cultures, troops, and XSLTs that remove the stock custom battle scenes the
// bridge fights on). So the bridge builds, on the player's machine and from the player's own copy, a small module that holds
// only the elephant: its asset package (hard-linked, else copied), sounds, prefab, skeleton actions and monster, and its horse and
// harness items without the Burning Empires cultures. Nothing of Burning Empires is shipped with the bridge.
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace CK3xBL.Core.Bannerlord;

public static class ElephantModule
{
    /// <summary>The elephants are on. Live 2026-10-19 the first build crashed Bannerlord at the first elephant's spawn: without
    /// project.mbproj the module's action sets were never loaded (fixed in generation 2, verified on the bench with every armour pack).</summary>
    public const bool Complete = true;

    public const string SourceModule = "BurningEmpires_Elephants";
    public const string ModuleId = "CK3Bridge.Elephants";
    /// <summary>Bumped when the built module's layout changes: a module of another generation is rebuilt.</summary>
    public const int Generation = 2;
    private const string StampFile = "ck3xbl_built_from.txt";

    /// <summary>The horse item and the harnesses the bridge puts on elephant regiments (BurningEmpires_Elephants item ids).</summary>
    public const string ElephantItem = "elephant";
    public static readonly IReadOnlyList<string> Harnesses = new[]
    {
        "be_elephant_armor_sassanid_tier_1", "be_elephant_armor_kushan_tier_1", "be_elephant_armor_sassanid_tier_2",
        "be_elephant_armor_kushan_tier_2", "be_elephant_armor_tier_3",
    };

    private static readonly string[] ModuleDataFiles =
    {
        // project.mbproj lists which of these the engine loads (action sets, action types, monster usages, sounds): without it the
        // elephant had no action set and its first spawn hung Bannerlord (bench 2026-10-19).
        "project.mbproj", "action_sets.xml", "action_types.xml", "be_elephant_monsters.xml", "module_sounds.xml", "monster_usage_sets.xml",
        "monster_usage_sets.xslt",
    };

    public static string SourceDir(string gameDir) => Path.Combine(gameDir, "Modules", SourceModule);
    public static string ModuleDir(string gameDir) => Path.Combine(gameDir, "Modules", ModuleId);

    /// <summary>The built module is there (it stays usable when its source was removed: the asset package is a hard link or a copy).</summary>
    public static bool IsBuilt(string gameDir) => File.Exists(Path.Combine(ModuleDir(gameDir), "SubModule.xml"));

    /// <summary>Builds or refreshes the module when Burning Empires' elephants are installed. Returns whether it is usable, with a
    /// line for the journal. Never throws.</summary>
    public static bool Ensure(string gameDir, out string report)
    {
        string src = SourceDir(gameDir), dst = ModuleDir(gameDir);
        try
        {
            if (!File.Exists(Path.Combine(src, "SubModule.xml")))
            {
                report = IsBuilt(gameDir) ? "elephants: kept the module built before (Burning Empires' elephants are gone)" : "";
                return IsBuilt(gameDir);
            }
            string stamp = Stamp(src);
            string stampPath = Path.Combine(dst, StampFile);
            if (IsBuilt(gameDir) && File.Exists(stampPath) && File.ReadAllText(stampPath).Trim() == stamp)
            {
                report = "";
                return true;
            }
            Build(src, dst);
            File.WriteAllText(stampPath, stamp);
            report = $"elephants: built {ModuleId} from {SourceModule}";
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            report = $"elephants: {ModuleId} could not be built ({e.Message}); elephant regiments ride horses";
            return IsBuilt(gameDir);
        }
    }

    private static string Stamp(string src)
    {
        var parts = new List<string> { "gen" + Generation };
        foreach (var f in new[] { "SubModule.xml" }.Concat(ModuleDataFiles.Select(n => Path.Combine("ModuleData", n)))
                     .Append(Path.Combine("ModuleData", "be_elephants_generated", "items", "be_elephant_items.xml")))
        {
            var path = Path.Combine(src, f);
            parts.Add(File.Exists(path) ? $"{f}@{File.GetLastWriteTimeUtc(path).Ticks}" : $"{f}@-");
        }
        foreach (var tpac in Directory.Exists(Path.Combine(src, "AssetPackages")) ? Directory.GetFiles(Path.Combine(src, "AssetPackages")) : Array.Empty<string>())
            parts.Add($"{Path.GetFileName(tpac)}@{new FileInfo(tpac).Length}");
        return string.Join(";", parts);
    }

    private static void Build(string src, string dst)
    {
        var itemsSrc = Path.Combine(src, "ModuleData", "be_elephants_generated", "items", "be_elephant_items.xml");
        if (!File.Exists(itemsSrc)) throw new InvalidDataException("be_elephant_items.xml is missing");
        if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
        Directory.CreateDirectory(Path.Combine(dst, "ModuleData", "items"));

        foreach (var dir in new[] { "AssetPackages", "ModuleSounds", "Prefabs" })
        {
            var from = Path.Combine(src, dir);
            if (!Directory.Exists(from)) continue;
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(dst, dir, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                // The 200 MB asset package is linked when the volume allows it, copied otherwise.
                if (!(dir == "AssetPackages" && CreateHardLink(to, file, IntPtr.Zero))) File.Copy(file, to, overwrite: true);
            }
        }
        foreach (var name in ModuleDataFiles)
        {
            var from = Path.Combine(src, "ModuleData", name);
            if (File.Exists(from)) File.Copy(from, Path.Combine(dst, "ModuleData", name), overwrite: true);
        }
        File.WriteAllText(Path.Combine(dst, "ModuleData", "items", "ck3xbl_elephant_items.xml"), WithoutCultures(File.ReadAllText(itemsSrc)), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dst, "SubModule.xml"), SubModuleXml, new UTF8Encoding(false));
    }

    /// <summary>The items without their Burning Empires cultures (kushan, sassanid), which do not exist in the bridge's game: an
    /// unknown culture reference would create an empty culture object.</summary>
    public static string WithoutCultures(string itemsXml) => Regex.Replace(itemsXml, @"\s+culture=""[^""]*""", "");

    private const string SubModuleXml = """
<?xml version="1.0" encoding="UTF-8"?>
<!-- Built by Crowns & Blades (CK3xBL) from the player's own BurningEmpires_Elephants (Burning Empires Team; elephant model and
     animations by Haterade and Artem). Not to be shared: rebuilt from Burning Empires on every machine. -->
<Module>
  <Id value="CK3Bridge.Elephants" />
  <Name value="Crowns &amp; Blades - War Elephants (from Burning Empires)" />
  <Version value="v1.0.0" />
  <DefaultModule value="false" />
  <ModuleCategory value="Singleplayer" />
  <ModuleType value="Community" />
  <DependedModules>
    <DependedModule Id="Native" />
    <DependedModule Id="SandBoxCore" />
    <DependedModule Id="Sandbox" />
    <DependedModule Id="StoryMode" />
    <DependedModule Id="CustomBattle" />
  </DependedModules>
  <SubModules />
  <Xmls>
    <XmlNode>
      <XmlName id="Monsters" path="be_elephant_monsters" />
      <IncludedGameTypes><GameType value="Campaign"/><GameType value="CampaignStoryMode"/><GameType value="CustomGame"/></IncludedGameTypes>
    </XmlNode>
    <XmlNode>
      <XmlName id="Items" path="items" />
      <IncludedGameTypes><GameType value="Campaign"/><GameType value="CampaignStoryMode"/><GameType value="CustomGame"/></IncludedGameTypes>
    </XmlNode>
  </Xmls>
</Module>
""";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
