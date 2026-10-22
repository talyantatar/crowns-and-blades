// A throw-away world for one test: fake CK3 and Bannerlord installs, a fake Documents folder, AppData, Desktop and a registry
// stand-in, all under %TEMP%\ck3xbl setup test\<name with spaces and non-ASCII letters>. Nothing here is a real game.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using CK3xBL.Setup.Util;

namespace CK3xBL.Setup.Tests;

internal sealed class RunResult
{
    public int ExitCode;
    public string Log = "";
    public string Output = "";

    public override string ToString() => $"exit {ExitCode}\n--- log ---\n{Log}\n--- output ---\n{Output}";
}

internal sealed class Sandbox : IDisposable
{
    /// <summary>Spaces, Latin-1 accents and Cyrillic in every path (written as escapes so this file stays ASCII).</summary>
    public const string OddName = "\u00DCn\u00EFc\u00F8d\u00E9 \u0422\u0435\u0441\u0442";
    public const string OriginalDlcLoad = "{\"enabled_mods\":[\"mod/ugc_1.mod\"],\"disabled_dlcs\":[]}";

    public static string Base => Path.Combine(Path.GetTempPath(), "ck3xbl setup test");

    public readonly string Root;
    public readonly string LogDir;
    public readonly bool Keep;

    public Sandbox(string name, bool keep)
    {
        Keep = keep;
        Root = Path.Combine(Base, $"{OddName} {name} {Guid.NewGuid().ToString("N").Substring(0, 6)}");
        LogDir = Path.Combine(Base, "logs", Path.GetFileName(Root));
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDir);

        // Fake Crusader Kings III (Steam layout, version file as the launcher writes it)
        Touch(Path.Combine(Ck3Dir, "binaries", "ck3.exe"));
        Write(Path.Combine(Ck3Dir, "binaries", "steam_appid.txt"), "1158310");
        Write(Path.Combine(Ck3Dir, "launcher", "launcher-settings.json"), "{\"rawVersion\":\"1.20.0.4\",\"distPlatform\":\"steam\"}");
        // Fake Bannerlord
        Touch(Path.Combine(BlDir, "bin", "Win64_Shipping_Client", "Bannerlord.exe"));
        Write(Path.Combine(BlDir, "bin", "Win64_Shipping_Client", "Version.xml"),
            "<Version>\n  <Singleplayer Value=\"v1.4.8\" />\n</Version>\n");
        Write(Path.Combine(BlDir, "Modules", "Native", "SubModule.xml"), "<Module><Id value=\"Native\"/></Module>\n");
        // Fake Documents with a player's mod list and one other mod
        Write(DlcLoad, OriginalDlcLoad);
        Write(Path.Combine(Ck3UserDir, "mod", "ugc_1.mod"), "name=\"Some other mod\"\npath=\"C:/elsewhere/ugc_1\"\n");
    }

    public string Ck3Dir => Path.Combine(Root, "Games", "Crusader Kings III");
    public string BlDir => Path.Combine(Root, "Games", "Mount & Blade II Bannerlord");
    public string ModulesDir => Path.Combine(BlDir, "Modules");
    public string ModuleDir => Path.Combine(ModulesDir, "CK3Bridge");
    public string Ck3UserDir => Path.Combine(Root, "Documents", "Paradox Interactive", "Crusader Kings III");
    public string ModDir => Path.Combine(Ck3UserDir, "mod", "ck3xbl");
    public string Descriptor => Path.Combine(Ck3UserDir, "mod", "ck3xbl.mod");
    public string DlcLoad => Path.Combine(Ck3UserDir, "dlc_load.json");
    public string DlcLoadBackup => DlcLoad + ".ck3xbl_backup";
    public string InstallDir => Path.Combine(Root, "LocalAppData", "Programs", "CK3xBL");
    public string SharedDir => Path.Combine(Root, "LocalAppData", "ck3xbl");
    public string StartMenuShortcut => Path.Combine(Root, "RoamingAppData", "Microsoft", "Windows", "Start Menu", "Programs", "Crowns & Blades.lnk");
    public string DesktopShortcut => Path.Combine(Root, "Desktop", "Crowns & Blades.lnk");
    public string Manifest => Path.Combine(InstallDir, "install_manifest.json");
    public string RegistryPrefix => SetupContext.SandboxRegistryPrefix(Root);

    public static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[0]);
    }

    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, FileOps.Utf8NoBom);
    }

    /// <summary>Runs an exe (the packed setup or the installed uninstaller) against this sandbox and waits for its result.</summary>
    public RunResult Run(string exe, params string[] args)
    {
        var id = Guid.NewGuid().ToString("N").Substring(0, 8);
        var log = Path.Combine(LogDir, $"setup-{id}.log");
        var result = Path.Combine(LogDir, $"result-{id}.json");
        var all = new List<string> { "/silent", $"/target-root={Root}", $"/log={log}", $"/result={result}" };
        all.AddRange(args);
        var psi = new ProcessStartInfo(exe, Options.Quote(all))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = LogDir,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(180_000)) throw new TimeoutException($"{exe} did not finish");
        p.WaitForExit();

        // The installed uninstaller hands over to a copy of itself in the sandbox's Temp: wait for that copy's result file.
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (!File.Exists(result) && DateTime.UtcNow < deadline) System.Threading.Thread.Sleep(100);
        int code = p.ExitCode;
        if (File.Exists(result) && Json.Parse(File.ReadAllText(result)) is JsonObject o && o.GetLong("exitCode") is { } c) code = (int)c;
        return new RunResult
        {
            ExitCode = code,
            Log = File.Exists(log) ? ReadShared(log) : "",
            Output = stdout.Result + stderr.Result,
        };
    }

    private static string ReadShared(string path) => Encoding.UTF8.GetString(FileOps.ReadShared(path));

    /// <summary>Every file under the sandbox with its hash: for "nothing changed" checks.</summary>
    public SortedDictionary<string, string> Snapshot()
    {
        var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(Root, "*", SearchOption.AllDirectories)) map[FileOps.Relative(Root, f)] = FileOps.Sha256File(f);
        foreach (var d in Directory.GetDirectories(Root, "*", SearchOption.AllDirectories)) map[FileOps.Relative(Root, d) + "\\"] = "dir";
        return map;
    }

    public bool RegistryKeyExists(string subKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPrefix + "\\" + subKey);
        return key is not null;
    }

    public object? RegistryValue(string subKey, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPrefix + "\\" + subKey);
        return key?.GetValue(name);
    }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(RegistryPrefix, throwOnMissingSubKey: false); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
        if (Keep) return;
        try
        {
            FileOps.DeleteTree(Root);
            FileOps.DeleteTree(LogDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"  (could not clean up {Root}: {e.Message})");
        }
    }

    /// <summary>Removes the parent registry key once every sandbox key is gone.</summary>
    public static void CleanRegistryParent()
    {
        try
        {
            using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\CK3xBL-Sandbox"))
                if (parent is null || parent.SubKeyCount > 0 || parent.ValueCount > 0) return;
            Registry.CurrentUser.DeleteSubKey(@"Software\CK3xBL-Sandbox", throwOnMissingSubKey: false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
    }

    public static IEnumerable<string> Lines(string text) => text.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0);
}
