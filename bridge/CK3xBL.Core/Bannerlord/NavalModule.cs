// War Sails (PROTOCOL §44). The bridge ships the CK3Bridge.Naval module in its own folder (naval/CK3Bridge.Naval next to the app) and
// puts it into Bannerlord's Modules only when the DLC is installed: a module that depends on NavalDLC must not sit there for players
// without it, and the installer manages the CK3Bridge module alone. Copied again whenever the shipped files differ.
namespace CK3xBL.Core.Bannerlord;

public static class NavalModule
{
    public const string DlcModule = "NavalDLC";
    public const string ModuleId = "CK3Bridge.Naval";
    private static readonly string[] Files = { "SubModule.xml", Path.Combine("bin", "Win64_Shipping_Client", "CK3Bridge.Naval.dll") };

    /// <summary>Where the bridge keeps the module: naval/CK3Bridge.Naval in the app folder.</summary>
    public static string ShippedDir(string appDir) => Path.Combine(appDir, "naval", ModuleId);

    public static bool DlcInstalled(string gameDir) => File.Exists(Path.Combine(gameDir, "Modules", DlcModule, "SubModule.xml"));

    /// <summary>Installs or refreshes the module when War Sails is installed; returns whether Bannerlord can load it, with a journal
    /// line when something was done (or failed). Never throws.</summary>
    public static bool Ensure(string gameDir, string appDir, out string report)
    {
        report = "";
        if (!DlcInstalled(gameDir)) return false;
        string src = ShippedDir(appDir), dst = Path.Combine(gameDir, "Modules", ModuleId);
        try
        {
            if (!Files.All(f => File.Exists(Path.Combine(src, f))))
                return File.Exists(Path.Combine(dst, Files[0]));   // a dev run without the shipped copy: whatever is installed
            bool changed = false;
            foreach (var f in Files)
            {
                string from = Path.Combine(src, f), to = Path.Combine(dst, f);
                if (File.Exists(to) && SameBytes(from, to)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to, overwrite: true);
                changed = true;
            }
            if (changed) report = $"naval: installed {ModuleId} into Bannerlord's Modules (War Sails found)";
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report = $"naval: {ModuleId} not installed ({e.Message}); battles at sea are fought on land";
            return File.Exists(Path.Combine(dst, Files[0]));
        }
    }

    private static bool SameBytes(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    /// <summary>The launch list with War Sails: NavalDLC (and the SandBox and StoryMode it needs) before CK3Bridge, CK3Bridge.Naval after
    /// it (it builds on CK3Bridge).</summary>
    public static List<string> WithNaval(IReadOnlyList<string> modules)
    {
        var list = modules.ToList();
        bool Has(string m) => list.Any(x => string.Equals(x, m, StringComparison.OrdinalIgnoreCase));
        int bridge = list.FindIndex(x => string.Equals(x, BannerlordLink.BridgeModule, StringComparison.OrdinalIgnoreCase));
        if (bridge < 0) return list;
        foreach (var dep in new[] { "SandBox", "StoryMode", DlcModule })
            if (!Has(dep)) list.Insert(bridge++, dep);
        if (!Has(ModuleId)) list.Insert(bridge + 1, ModuleId);
        return list;
    }
}
