// Uninstall: removes exactly what install_manifest.json lists (files only while unchanged since install), undoes the
// dlc_load.json edit, puts back what setup had moved aside, and keeps the player's settings, logs and saves unless asked.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CK3xBL.Setup.Util;

namespace CK3xBL.Setup.Engine;

internal sealed class UninstallEngine
{
    private readonly SetupContext _c;
    private readonly IInteraction _ui;
    private readonly SetupLog _log;
    private readonly Changes _changes;
    private readonly bool _deleteUserData;
    private InstallManifest _m = new();
    /// <summary>Entries that could not be removed this time (kept in the manifest so uninstall can be run again).</summary>
    private readonly List<InstalledFile> _remaining = new();
    private DlcLoadRecord? _remainingDlcLoad;
    private readonly List<string> _remainingShortcuts = new();
    /// <summary>Copies setup had moved aside that were not put back this time. Their backups are never deleted, and they stay
    /// in the reduced manifest whenever uninstall is incomplete.</summary>
    private readonly List<MovedAside> _unrestored = new();
    /// <summary>A copy could not be put back for a reason another run can fix (an error, administrator rights): keep the uninstaller.</summary>
    private bool _restoreRetry;
    /// <summary>A step threw: what it left is unknown, so the uninstaller, its record and the Add/Remove Programs entry stay.</summary>
    private bool _stepFailed;

    public readonly List<StepResult> Results = new();
    public event Action<int, string>? Progress;

    public UninstallEngine(SetupContext ctx, IInteraction ui, SetupLog log, bool dryRun, bool deleteUserData)
    {
        _c = ctx;
        _ui = ui;
        _log = log;
        _deleteUserData = deleteUserData;
        _changes = new Changes(dryRun, log);
    }

    public int Run()
    {
        _log.Info($"{Product.Name} uninstall {Product.SetupVersion}" + (_changes.DryRun ? " (dry run: nothing is changed)" : "") +
                  (_c.Sandbox ? $" in the sandbox {_c.SandboxRoot}" : ""));
        var manifest = InstallManifest.TryLoad(_c.ManifestPath, out var problem);
        if (manifest is null)
        {
            if (problem is not null) _log.Warn(problem);
            bool registered;
            using (var key = _c.OpenUserKey(Product.UninstallKey, writable: false)) registered = key is not null;
            if (registered)
            {
                _changes.Do($"remove the stale entry {_c.RegistryDisplay(Product.UninstallKey)}", () => Shell.UnregisterUninstall(_c));
                Add("Add/Remove Programs", StepStatus.Warning, $"no install record in {_c.InstallDir}; removed the stale Add/Remove Programs entry");
                return ExitCodes.Partial;
            }
            Add("Uninstall", StepStatus.Failed, $"{Product.Name} is not installed in {_c.InstallDir}");
            return ExitCodes.Failed;
        }
        _m = manifest;
        _log.Info($"removing version {_m.Version} from {_m.InstallDir}");

        if (!EnsureBridgeStopped()) return ExitCodes.Blocked;

        // A step that throws keeps the records of what it was removing, so uninstall can be run again for the rest.
        Step(10, "Bannerlord module", RemoveModule, () => KeepExisting(Targets.BlModule));
        Step(30, "CK3 mod", RemoveCk3Mod, () => KeepExisting(Targets.Ck3Mod, Targets.Ck3Descriptor));
        Step(45, "CK3 mod list", RestoreModList, () => _remainingDlcLoad ??= _m.DlcLoad);
        Step(55, "Moved-aside files", RestoreMovedAside, KeepUnrestored);
        Step(65, "Shortcuts", RemoveShortcuts, () => _remainingShortcuts.AddRange(_m.Shortcuts.Where(File.Exists).Except(_remainingShortcuts).ToList()));
        Step(70, "Start with Windows", RemoveAutostart);
        Step(80, "Program files", RemoveApp);
        Step(90, "Settings and logs", RemoveUserData);
        Step(95, "Add/Remove Programs", Unregister);
        Progress?.Invoke(100, "Done");
        return StepResults.ExitCode(Results);
    }

    private void Step(int percent, string name, Func<StepResult> step, Action? onFailure = null)
    {
        Progress?.Invoke(percent, name);
        StepResult r;
        try { r = step(); }
        catch (Exception e) when (IsStepFailure(e))
        {
            r = new StepResult { Status = StepStatus.Failed, Message = e.Message };
            _stepFailed = true;
            try { onFailure?.Invoke(); }
            catch (Exception e2) when (IsStepFailure(e2)) { _log.Warn($"{name}: {e2.Message}"); }
        }
        r.Step = name;
        Add(r);
    }

    private static bool IsStepFailure(Exception e) =>
        e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException
            or System.Security.SecurityException;

    /// <summary>Keeps the records of these targets whose files are still there (after a step stopped part-way).</summary>
    private void KeepExisting(params string[] targets)
    {
        var have = new HashSet<string>(_remaining.Select(f => FileOps.Full(f.Path)), StringComparer.OrdinalIgnoreCase);
        foreach (var f in _m.Files.Where(f => targets.Contains(f.Target) && File.Exists(f.Path)))
            if (have.Add(FileOps.Full(f.Path))) _remaining.Add(f);
    }

    private void KeepUnrestored()
    {
        _restoreRetry = true;
        foreach (var a in _m.MovedAside.Where(a => File.Exists(a.Backup) || Directory.Exists(a.Backup)))
            if (!_unrestored.Contains(a)) _unrestored.Add(a);
    }

    private void Add(string step, StepStatus status, string message) => Add(new StepResult { Step = step, Status = status, Message = message });

    private void Add(StepResult r)
    {
        Results.Add(r);
        var level = r.Status switch { StepStatus.Failed => LogLevel.Error, StepStatus.Warning or StepStatus.Skipped => LogLevel.Warn, _ => LogLevel.Info };
        _log.Write(level, $"{r.Step}: {r.Message}");
    }

    private static StepResult Result(StepStatus status, string message) => new() { Status = status, Message = message };

    private bool EnsureBridgeStopped()
    {
        while (true)
        {
            var running = ProcessGuard.Bridge(_m.InstallDir);
            try
            {
                if (running.Count == 0) return true;
                if (_changes.DryRun)
                {
                    _log.Plan("ask the running Crowns & Blades to quit");
                    return true;
                }
                if (_c.Sandbox)
                {
                    // The quit event is per session, not per folder: a sandbox must never signal the player's real bridge.
                    Add("Crowns & Blades", StepStatus.Failed, "a CK3xBL.exe is running from the sandbox install folder");
                    return false;
                }
                int choice = _ui.Ask("Crowns & Blades is running",
                    "Crowns & Blades is running. Close it now? It closes its own Bannerlord window and puts CK3's window back first.",
                    new[] { "Close it", "Cancel" }, 1);
                if (choice != 0)
                {
                    Add("Crowns & Blades", StepStatus.Failed, "it is running; quit it from its tray icon and uninstall again");
                    return false;
                }
                if (ProcessGuard.RequestBridgeQuit(running, TimeSpan.FromSeconds(30), _log)) continue;
                if (_ui.Ask("Crowns & Blades is still running",
                        "Right-click the Crowns & Blades icon in the notification area and choose Quit, then press Retry.",
                        new[] { "Retry", "Cancel" }, 1) != 0)
                {
                    Add("Crowns & Blades", StepStatus.Failed, "it is still running; quit it from its tray icon and uninstall again");
                    return false;
                }
            }
            finally { running.ForEach(p => p.Dispose()); }
        }
    }

    /// <summary>Deletes listed files that are unchanged since install; changed ones are the player's now and are kept. Only
    /// files in Crowns & Blades's own folders are deleted (<see cref="RecordRules"/>): a record never makes uninstall delete
    /// anything elsewhere.</summary>
    private (int removed, int kept) DeleteFiles(IEnumerable<InstalledFile> files)
    {
        int removed = 0, kept = 0;
        var dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // folder -> where its cleanup stops
        foreach (var f in files)
        {
            if (!File.Exists(f.Path)) continue;
            if (!RecordRules.Place(f, _m.InstallDir, out var boundary))
            {
                _log.Warn($"{f.Path} is not in a Crowns & Blades folder; not deleted");
                kept++;
                continue;
            }
            if (FileOps.LinkOnPath(boundary, f.Path) is { } link)
            {
                _log.Warn($"{f.Path}: {link} is a link now; nothing is deleted through it");
                kept++;
                continue;
            }
            if (FileOps.TrySha256File(f.Path) != f.Sha256)
            {
                _log.Warn($"{f.Path} was changed after install; kept");
                kept++;
                continue;
            }
            try
            {
                _changes.Do($"delete {f.Path}", () => FileOps.DeleteFile(f.Path));
                removed++;
                dirs[Path.GetDirectoryName(f.Path)!] = boundary;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"could not delete {f.Path}: {e.Message}");
                _remaining.Add(f);
            }
        }
        if (!_changes.DryRun)
            foreach (var d in dirs.OrderByDescending(d => d.Key.Length)) FileOps.DeleteEmptyDirsUpward(d.Key, d.Value);
        return (removed, kept);
    }

    private StepResult RemoveModule()
    {
        var files = _m.FilesOf(Targets.BlModule).ToList();
        if (files.Count == 0 || _m.BlDir is null) return Result(StepStatus.NotSelected, "not installed");
        var req = new ModuleRequest { BlDir = _m.BlDir, BackupRoot = BackupRoot };
        var elsewhere = new List<InstalledFile>();
        foreach (var f in files) (FileOps.IsUnder(f.Path, req.ModuleDir) ? req.PreviousFiles : elsewhere).Add(f);
        files = req.PreviousFiles;
        // Module files recorded in another Bannerlord folder (an older place): removed here, never by the elevated helper.
        if (elsewhere.Count > 0) DeleteFiles(elsewhere);
        if (files.Count == 0) return Result(StepStatus.Done, $"removed the module files outside {req.ModuleDir}");

        if (FileOps.LinkOnPath(req.ModulesDir, req.ModuleDir) is { } link)
            return Result(StepStatus.Done, $"{link} is a link now (a development checkout?); left unchanged");

        while (!_changes.DryRun && ProcessGuard.BannerlordRunning(_m.BlDir))
        {
            if (_ui.Ask("Bannerlord is running", "Mount & Blade II: Bannerlord is running and uses the module's files.\n\nClose Bannerlord, then press Retry.",
                    new[] { "Retry", "Skip" }, 1) != 0)
            {
                _remaining.AddRange(files);
                return Result(StepStatus.Failed, "Bannerlord is running; the module was kept. Close Bannerlord and uninstall again.");
            }
        }

        ModuleResult result;
        if (_changes.DryRun || FileOps.CanWrite(req.ModuleDir))
        {
            result = ModuleInstaller.Uninstall(req, _changes, _log);
        }
        else if (!_ui.IsSilent && !_c.Sandbox &&
                 _ui.Ask("Administrator rights needed", $"Removing the module from\n  {req.ModuleDir}\nneeds administrator rights. Continue?",
                     new[] { "Continue", "Skip" }, 1) == 0 &&
                 Elevation.Run(_c, Elevation.OpUninstall, req, null, _log) is { } elevated)
        {
            result = elevated;
        }
        else
        {
            _remaining.AddRange(files);
            return Result(StepStatus.Failed, $"{req.ModuleDir} needs administrator rights; delete that folder by hand");
        }
        _remaining.AddRange(result.Remaining);
        return Result(result.Ok ? StepStatus.Done : StepStatus.Failed, result.Message);
    }

    private StepResult RemoveCk3Mod()
    {
        var files = _m.FilesOf(Targets.Ck3Mod).ToList();
        var descriptors = _m.FilesOf(Targets.Ck3Descriptor).ToList();
        if (files.Count == 0 && descriptors.Count == 0) return Result(StepStatus.NotSelected, "not installed");
        var (removed, kept) = DeleteFiles(files);

        // ck3xbl.mod: also removed when the launcher rewrote it but it still points at our (now removed) folder.
        foreach (var d in descriptors)
        {
            if (!File.Exists(d.Path)) continue;
            if (!RecordRules.Place(d, _m.InstallDir, out var descriptorRoot))
            {
                _log.Warn($"{d.Path} is not {Product.ModDescriptor} in a CK3 mod folder; not deleted");
                kept++;
                continue;
            }
            if (FileOps.TrySha256File(d.Path) == d.Sha256 || PointsTo(d.Path, FileOps.ParentOrSelf(descriptorRoot), Path.Combine(descriptorRoot, Product.ModFolder)))
            {
                try
                {
                    _changes.Do($"delete {d.Path}", () => FileOps.DeleteFile(d.Path));
                    removed++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _log.Warn($"could not delete {d.Path}: {e.Message}");
                    _remaining.Add(d);
                }
            }
            else
            {
                _log.Warn($"{d.Path} was changed after install and points elsewhere; kept");
                kept++;
            }
        }
        var failed = _remaining.Count(f => f.Target is Targets.Ck3Mod or Targets.Ck3Descriptor);
        return Result(kept == 0 && failed == 0 ? StepStatus.Done : StepStatus.Warning,
            $"removed {removed} files" + (kept > 0 ? $", kept {kept} changed ones" : "") + (failed > 0 ? $", {failed} could not be removed yet" : ""));
    }

    private static bool PointsTo(string descriptor, string ck3UserDir, string modDir)
    {
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(descriptor), "^\\s*path\\s*=\\s*\"(?<p>[^\"]*)\"",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            if (!m.Success) return false;
            var p = m.Groups["p"].Value;
            var full = Path.IsPathRooted(p) ? p : Path.Combine(ck3UserDir, p.Replace('/', '\\'));
            return FileOps.PathEquals(full, modDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private StepResult RestoreModList()
    {
        var record = _m.DlcLoad;
        if (record is null || !record.Added) return Result(StepStatus.NotSelected, "setup had not changed the CK3 mod list");
        if (!_changes.DryRun && ProcessGuard.Ck3Running(_c))
        {
            _remainingDlcLoad = record;
            return Result(StepStatus.Warning, "Crusader Kings III is running, so its mod list was not changed. " +
                                              "The Paradox Launcher drops the missing mod from it by itself.");
        }
        if (_changes.DryRun)
        {
            _log.Plan($"undo the change to {record.Path} (restore its backup if unchanged since install, else remove only {Ck3ModList.Entry})");
            return Result(StepStatus.Done, "planned");
        }
        var outcome = Ck3ModList.Disable(record, _m.Ck3UserDir, out var message);
        return Result(outcome == ModListOutcome.Unreadable ? StepStatus.Warning : StepStatus.Done, message);
    }

    private string BackupRoot => Path.Combine(_m.SharedDir, Product.BackupFolder);

    /// <summary>Puts back what setup moved out of its way (the player's own mod, module or descriptor), each where it was,
    /// when that place is free again. A copy that cannot go back stays in setup-backups, and the player is told where. Files
    /// moved out of the install folder go back in <see cref="RemoveApp"/>, once setup's own files there are gone.</summary>
    private StepResult RestoreMovedAside()
    {
        var entries = _m.MovedAside.Where(a => !FileOps.IsUnder(a.Original, _m.InstallDir)).ToList();
        if (entries.Count == 0) return Result(StepStatus.NotSelected, "nothing was moved aside");
        int restored = RestoreEntries(entries);
        var left = _unrestored.Where(entries.Contains).ToList();
        if (left.Count == 0) return Result(StepStatus.Done, $"put back {restored}");
        return Result(StepStatus.Warning, $"put back {restored}; your earlier copies of " +
                                          string.Join(", ", left.Select(a => $"{a.Original} (in {a.Backup})")) +
                                          " were not put back. Move them back by hand" + (_restoreRetry ? ", or run the uninstaller again" : "") + ".");
    }

    /// <summary>Restores these entries; the ones that cannot go back are added to <see cref="_unrestored"/> (and set
    /// <see cref="_restoreRetry"/> when another run could fix it). Returns how many went back.</summary>
    private int RestoreEntries(IEnumerable<MovedAside> entries)
    {
        int restored = 0;
        foreach (var a in entries)
        {
            if (!File.Exists(a.Backup) && !Directory.Exists(a.Backup)) continue;   // put back earlier, or never moved
            if (File.Exists(a.Original) || Directory.Exists(a.Original) || FileOps.IsReparsePoint(a.Original))
            {
                // Files the player changed (kept by uninstall) or a new copy are there now: not ours to remove.
                _log.Warn($"{a.Original} is in use again (files you changed, or a new copy); your earlier copy stays in {a.Backup}");
                _unrestored.Add(a);
                continue;
            }
            try
            {
                if (RestoreNeedsRights(a, out var req))
                {
                    if (!RestoreElevated(a, req))
                    {
                        _unrestored.Add(a);
                        _restoreRetry = true;
                        continue;
                    }
                }
                else
                {
                    _changes.Do($"put back {a.Original} from {a.Backup}", () => FileOps.Move(a.Backup, a.Original));
                }
                if (!_changes.DryRun) FileOps.DeleteEmptyDirsUpward(FileOps.ParentOrSelf(a.Backup), _m.SharedDir);
                restored++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"could not put back {a.Original} from {a.Backup}: {e.Message}");
                _unrestored.Add(a);
                _restoreRetry = true;
            }
        }
        return restored;
    }

    /// <summary>The player's earlier CK3Bridge folder goes back into a Modules folder that this account cannot write.</summary>
    private bool RestoreNeedsRights(MovedAside a, out ModuleRequest req)
    {
        req = new ModuleRequest { BlDir = _m.BlDir ?? "", BackupRoot = BackupRoot, RestoreFrom = a.Backup };
        return !_changes.DryRun && _m.BlDir is not null && FileOps.PathEquals(a.Original, req.ModuleDir) && Directory.Exists(a.Backup) &&
               !FileOps.CanWrite(req.ModulesDir);
    }

    private bool RestoreElevated(MovedAside a, ModuleRequest req)
    {
        if (_ui.IsSilent || _c.Sandbox)
        {
            _log.Warn($"putting {a.Original} back needs administrator rights; run the uninstaller again without /silent, or move {a.Backup} back by hand");
            return false;
        }
        if (_ui.Ask("Administrator rights needed",
                $"Putting your earlier CK3Bridge folder back into\n  {req.ModulesDir}\nneeds administrator rights. Continue?",
                new[] { "Continue", "Skip" }, 1) != 0)
            return false;
        var result = Elevation.Run(_c, Elevation.OpRestore, req, null, _log);
        if (result is { Ok: true } && !Directory.Exists(a.Backup)) return true;
        _log.Warn($"{a.Original} was not put back: {result?.Message ?? "the administrator step did not finish"}");
        return false;
    }

    private StepResult RemoveShortcuts()
    {
        int n = 0;
        foreach (var lnk in _m.Shortcuts.Where(File.Exists))
        {
            try
            {
                _changes.Do($"delete the shortcut {lnk}", () => FileOps.DeleteFile(lnk));
                n++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"could not delete {lnk}: {e.Message}");
                _remainingShortcuts.Add(lnk);
            }
        }
        return Result(_remainingShortcuts.Count == 0 ? StepStatus.Done : StepStatus.Warning,
            $"removed {n}" + (_remainingShortcuts.Count > 0 ? $"; {_remainingShortcuts.Count} could not be removed yet" : ""));
    }

    /// <summary>Runs after the program files are gone: a failure here is reported, but never keeps the uninstaller (the
    /// player's settings are not part of the install record).</summary>
    private StepResult RemoveUserData()
    {
        if (!_deleteUserData) return Result(StepStatus.NotSelected, $"kept {_m.SharedDir} (settings, logs, battle archive)");
        if (ProcessGuard.BannerlordRunning(_m.BlDir) && !_changes.DryRun)
            return Result(StepStatus.Warning, $"Bannerlord is running; {_m.SharedDir} was kept");
        try { return DeleteUserData(); }
        catch (Exception e) when (IsStepFailure(e))
        {
            return Result(StepStatus.Warning, $"{_m.SharedDir} was not deleted completely: {e.Message}");
        }
    }

    private StepResult DeleteUserData()
    {

        // setup-backups holds the player's own earlier copies that could not be put back: never deleted with the settings.
        bool keepBackups = _unrestored.Count > 0 || HasEntries(BackupRoot);
        if (Directory.Exists(_m.SharedDir))
        {
            if (!keepBackups)
            {
                _changes.Do($"delete {_m.SharedDir}", () => FileOps.DeleteTree(_m.SharedDir));
            }
            else
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(_m.SharedDir).ToList())
                {
                    if (FileOps.PathEquals(entry, BackupRoot)) continue;
                    _changes.Do($"delete {entry}", () =>
                    {
                        if (Directory.Exists(entry) || FileOps.IsReparsePoint(entry)) FileOps.DeleteTree(entry);
                        else FileOps.DeleteFile(entry);
                    });
                }
            }
        }
        // Files the bridge itself creates elsewhere: the dev-mode run file, and a config.json next to an older bridge exe
        // (only in a folder of our own: an install folder shared with other programs keeps its config.json).
        var extras = new List<string> { Path.Combine(_m.Ck3UserDir, "run", "ck3xbl_apply.txt") };
        if (string.Equals(Path.GetFileName(FileOps.Full(_m.InstallDir)), Product.InstallFolder, StringComparison.OrdinalIgnoreCase))
            extras.Add(Path.Combine(_m.InstallDir, "config.json"));
        foreach (var extra in extras)
            if (File.Exists(extra)) _changes.Do($"delete {extra}", () => FileOps.DeleteFile(extra));
        if (!_changes.DryRun && !Incomplete) FileOps.DeleteEmptyDirsUpward(_m.InstallDir, FileOps.ParentOrSelf(_m.InstallDir));

        if (!keepBackups) return Result(StepStatus.Done, $"deleted {_m.SharedDir}");
        var what = _unrestored.Count > 0 ? "your earlier copy of " + string.Join(", ", _unrestored.Select(a => a.Original)) : "earlier copies of your files";
        return Result(StepStatus.Warning, $"deleted the settings, logs and battle archive; kept {BackupRoot}: it holds {what}");
    }

    private static bool HasEntries(string dir)
    {
        try { return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    private StepResult RemoveApp()
    {
        var files = _m.Files.Where(f => f.Target is Targets.App or Targets.Uninstaller).ToList();
        var uninstallers = files.Where(f => f.Target == Targets.Uninstaller).ToList();
        var parent = FileOps.ParentOrSelf(_m.InstallDir);
        var (removed, kept) = DeleteFiles(files.Except(uninstallers));

        // Other programs' files that setup moved out of its way go back now that ours are gone.
        var aside = _m.MovedAside.Where(a => FileOps.IsUnder(a.Original, _m.InstallDir) &&
                                             !uninstallers.Any(u => FileOps.PathEquals(u.Path, a.Original))).ToList();
        int restored = RestoreEntries(aside);
        var notes = (kept > 0 ? $"; kept {kept} changed files" : "") + (restored > 0 ? $"; put back {restored} files of yours" : "") +
                    (_unrestored.Any(aside.Contains) ? $"; some files of yours stay in {BackupRoot}" : "");

        if (Incomplete)
        {
            // Keep the uninstaller, the record and the entry in Add/Remove Programs, so the rest can be removed later.
            var rest = new InstallManifest
            {
                Version = _m.Version,
                InstalledUtc = _m.InstalledUtc,
                InstallDir = _m.InstallDir,
                SharedDir = _m.SharedDir,
                Ck3Dir = _m.Ck3Dir,
                Ck3UserDir = _m.Ck3UserDir,
                Ck3UserDirCustom = _m.Ck3UserDirCustom,
                BlDir = _m.BlDir,
                DlcLoad = _remainingDlcLoad,
                RegistryKeys = _m.RegistryKeys,
            };
            rest.Files.AddRange(_remaining);
            rest.Files.AddRange(uninstallers);
            rest.MovedAside.AddRange(_unrestored);
            rest.Shortcuts.AddRange(_remainingShortcuts);
            _changes.Do($"write {_c.ManifestPath} (what is left)", () => rest.Save(_c.ManifestPath));
            return Result(StepStatus.Warning, $"removed {removed} program files; the uninstaller stays until the rest can be removed" + notes);
        }

        var (removedUninstaller, _) = DeleteFiles(uninstallers);
        if (File.Exists(_c.ManifestPath)) _changes.Do($"delete {_c.ManifestPath}", () => FileOps.DeleteFile(_c.ManifestPath));
        if (!_changes.DryRun) FileOps.DeleteEmptyDirsUpward(_m.InstallDir, parent);
        return Result(kept == 0 && !_unrestored.Any(aside.Contains) ? StepStatus.Done : StepStatus.Warning,
            $"removed {removed + removedUninstaller} files from {_m.InstallDir}" + notes);
    }

    /// <summary>The bridge's "Start with Windows" entry, when it starts the CK3xBL.exe of this install (one that starts a
    /// copy elsewhere, such as a source checkout, is left alone).</summary>
    private StepResult RemoveAutostart()
    {
        var display = _c.RegistryDisplay(Product.RunKey) + "\\" + Product.RunValue;
        using var key = _c.OpenUserKey(Product.RunKey, writable: !_changes.DryRun);
        if (key?.GetValue(Product.RunValue) is not string command || command.Trim().Length == 0)
            return Result(StepStatus.NotSelected, "not turned on");
        var exe = CommandExe(command);
        bool ours;
        try { ours = exe is not null && Path.IsPathRooted(exe) && FileOps.IsUnder(exe, _m.InstallDir); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { ours = false; }
        if (!ours) return Result(StepStatus.Done, $"{display} starts {command}, not this install; left unchanged");
        _changes.Do($"remove {display}", () => key.DeleteValue(Product.RunValue, throwOnMissingValue: false));
        return Result(StepStatus.Done, $"removed {display}");
    }

    /// <summary>The program of a Run command line: the quoted first part, or everything up to ".exe".</summary>
    internal static string? CommandExe(string command)
    {
        var c = command.Trim();
        if (c.StartsWith("\"", StringComparison.Ordinal))
        {
            int close = c.IndexOf('"', 1);
            return close > 1 ? c.Substring(1, close - 1) : null;
        }
        int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? c.Substring(0, exe + 4) : c;
    }

    private bool Incomplete => _remaining.Count > 0 || _remainingDlcLoad is not null || _remainingShortcuts.Count > 0 || _restoreRetry || _stepFailed;

    private StepResult Unregister()
    {
        if (Incomplete) return Result(StepStatus.Warning, "kept, so uninstall can be run again");
        _changes.Do($"remove {_c.RegistryDisplay(Product.UninstallKey)}", () => Shell.UnregisterUninstall(_c));
        return Result(StepStatus.Done, "removed");
    }
}
