// Startup work asked for on the command line: Melty's Play (enable the mod, start CK3) and Melty's release test.
//
// Melty installs the release the way players get it: the CK3 mod folder into Documents\...\mod\ck3xbl, CK3Bridge into Bannerlord's
// Modules, the bridge into its own folder. It writes no ck3xbl.mod, so the bridge writes it (EnsureCk3ModDescriptor).
//
// The test (recipe test.args = --melty-test <slug>): while %LOCALAPPDATA%\Melty\test\<slug>.json exists ({testId, run, runs, seed,
// checks}), the bridge appends one "pass <name>" or "fail <name>: why" line per check to the file named by checks:
//   bridge-ready          data and both games found
//   ck3-mod-enabled       ck3xbl.mod points at the mod and dlc_load.json enables it
//   bannerlord-module     Modules\CK3Bridge\SubModule.xml is there
//   ck3-mod-loaded        CK3 started and its debug.log shows the mod's keypad widget loaded
//   bannerlord-battle     a bundled battle (data\melty_test_snapshot.json) reaches the fight in Bannerlord
// The battle is then aborted (it goes back to CK3 as unfought). Melty closes the games when the run ends.
using System.Text;
using System.Text.Json;
using CK3xBL.Core.Ck3;
using CK3xBL.Core.Config;
using CK3xBL.Core.Diagnostics;
using CK3xBL.Core.Logs;
using CK3xBL.Core.Orchestration;
using CK3xBL.Protocol;

namespace CK3xBL;

internal sealed class StartupActions
{
    public bool LaunchCk3;
    public string? MeltyTestSlug;
}

internal static class Ck3ModSetup
{
    /// <summary>Writes mod\ck3xbl.mod from mod\ck3xbl\descriptor.mod with an absolute path= line (as the installer does) when it is
    /// missing or points elsewhere. Returns what was done, or null when nothing was needed or possible.</summary>
    public static string? EnsureCk3ModDescriptor(string ck3UserDir)
    {
        if (string.IsNullOrWhiteSpace(ck3UserDir)) return null;
        string modDir = Path.Combine(ck3UserDir, "mod", "ck3xbl");
        string inner = Path.Combine(modDir, "descriptor.mod");
        if (!File.Exists(inner) || !File.Exists(Path.Combine(modDir, InstallCheck.ModProbeFile))) return null;
        var current = InstallCheck.ReadModDescriptor(ck3UserDir).Dir;
        if (current is not null && File.Exists(Path.Combine(current, InstallCheck.ModProbeFile))) return null;
        var lines = File.ReadAllLines(inner, Encoding.UTF8).Where(l => !l.TrimStart().StartsWith("path=", StringComparison.OrdinalIgnoreCase));
        string text = string.Join("\n", lines) + $"\npath=\"{Path.GetFullPath(modDir).Replace('\\', '/')}\"\n";
        string outer = Path.Combine(ck3UserDir, "mod", InstallCheck.ModDescriptorFile);
        File.WriteAllText(outer, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return $"wrote {outer}";
    }
}

internal sealed class MeltyTest
{
    private const int TestBid = 990001;
    private static readonly TimeSpan Ck3LoadTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BattleTimeout = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan FightFor = TimeSpan.FromSeconds(45);

    private readonly BridgeHost _host;
    private readonly IJournal _journal;
    private readonly string _checksFile;

    private MeltyTest(BridgeHost host, IJournal journal, string checksFile)
    {
        _host = host;
        _journal = journal;
        _checksFile = checksFile;
    }

    /// <summary>The test of <paramref name="slug"/>, when Melty's test file for it is there; else null (a normal start).</summary>
    public static MeltyTest? Find(string? slug, BridgeHost host, IJournal journal)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Melty", "test", slug + ".json");
        if (!File.Exists(file)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("checks", out var checks) && checks.GetString() is { Length: > 0 } path)
            {
                journal.Info("melty", $"release test {(doc.RootElement.TryGetProperty("testId", out var id) ? id.ToString() : "?")}: checks go to {path}");
                return new MeltyTest(host, journal, path);
            }
            journal.Warn("melty", $"{file} names no checks file; no test");
        }
        catch (Exception e) { journal.Warn("melty", $"{file} unreadable: {e.Message}"); }
        return null;
    }

    public async Task RunAsync()
    {
        try
        {
            var cfg = _host.Config;
            bool ready = _host.Paths.HasData && GameLocator.IsCk3Dir(cfg.Ck3.GameDir) && GameLocator.IsBannerlordDir(cfg.Bannerlord.GameDir);
            Report("bridge-ready", ready, ready ? null : $"data {_host.Paths.HasData}, CK3 '{cfg.Ck3.GameDir}', Bannerlord '{cfg.Bannerlord.GameDir}'");

            bool enabled = _host.Ck3ModInstalled && _host.Ck3ModEnabled;
            Report("ck3-mod-enabled", enabled, enabled ? null : "ck3xbl.mod or its dlc_load.json entry is missing");

            bool module = File.Exists(Path.Combine(cfg.Bannerlord.GameDir, "Modules", "CK3Bridge", "SubModule.xml"));
            Report("bannerlord-module", module, module ? null : "Modules\\CK3Bridge\\SubModule.xml is missing");

            var loaded = await WaitForCk3ModAsync();
            Report("ck3-mod-loaded", loaded is null, loaded);

            var battle = await RunBattleAsync();
            Report("bannerlord-battle", battle is null, battle);
        }
        catch (Exception e)
        {
            _journal.Error("melty", "release test failed", e);
            Report("bannerlord-battle", false, e.Message);
        }
    }

    /// <summary>Null when CK3 runs and has loaded the mod's keypad widget since the test started, else why not.</summary>
    private async Task<string?> WaitForCk3ModAsync()
    {
        var start = DateTime.Now;
        if (!_host.Ck3Running && _host.LaunchCk3(_host.Mode) is { } problem) return $"CK3 did not start: {problem}";
        string log = Path.Combine(_host.Config.Ck3.UserDir, "logs", "debug.log");
        var deadline = DateTime.UtcNow + Ck3LoadTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(2000);
            if (!File.Exists(log) || File.GetLastWriteTime(log) < start.AddSeconds(-5)) continue;
            if (ReadShared(log).Contains("ck3xbl_keypad.gui\" is complete", StringComparison.Ordinal)) return null;
        }
        return _host.Ck3Running ? $"CK3 runs, but {log} never showed the mod's GUI loaded" : "CK3 is not running";
    }

    /// <summary>Null when the bundled battle reached the fight; else why not. The battle is aborted afterwards.</summary>
    private async Task<string?> RunBattleAsync()
    {
        string sample = Path.Combine(_host.Paths.DataDir, "melty_test_snapshot.json");
        if (!File.Exists(sample)) return $"{sample} is missing";
        var snapshot = JsonDocument.Parse(File.ReadAllText(sample)).RootElement;
        string original = File.ReadAllText(sample);
        string json = original.Replace($"\"bid\": {snapshot.GetProperty("bid").GetInt32()},", $"\"bid\": {TestBid},");
        if (json == original) return $"{sample} has no \"bid\" line to replace";
        string dir = Path.Combine(_host.Paths.ArchiveDir, $"b{TestBid}_melty");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "snapshot.json"), json);

        var orchestrator = _host.Orchestrator;
        if (orchestrator.Phase != BattlePhase.Idle) return "the bridge is busy with another battle";
        orchestrator.OnCk3Event(new ChoiceMade(TestBid, Ck3Choice.Field, "FIELD"));
        _journal.Info("melty", $"test battle b{TestBid} staged");
        if (!await orchestrator.WaitForPhaseAsync(BattlePhase.Fighting, BattleTimeout))
            return $"the battle did not reach the fight in {BattleTimeout.TotalMinutes:0} min (phase {orchestrator.Phase}, Bannerlord " +
                   $"{_host.Bannerlord.Status?.Status ?? "not running"}{(_host.Bannerlord.Problem is { } p ? ", " + p : "")})";
        await Task.Delay(FightFor);
        bool alive = _host.Bannerlord.IsRunning && orchestrator.Phase == BattlePhase.Fighting;
        _host.AbortHotkey();
        return alive ? null : $"the fight ended early (phase {orchestrator.Phase}, Bannerlord running {_host.Bannerlord.IsRunning})";
    }

    private void Report(string check, bool pass, string? why)
    {
        string line = pass ? $"pass {check}" : $"fail {check}: {(why ?? "failed").Replace('\n', ' ')}";
        _journal.Write(pass ? JournalLevel.Info : JournalLevel.Warn, "melty", line);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_checksFile))!);
            File.AppendAllText(_checksFile, line + "\n", new UTF8Encoding(false));
        }
        catch (Exception e) { _journal.Warn("melty", $"could not write {_checksFile}: {e.Message}"); }
    }

    private static string ReadShared(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }
}
