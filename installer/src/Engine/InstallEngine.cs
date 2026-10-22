// Install, upgrade and repair. Steps run in order and independently: a failed step is reported and the others still run.
// Everything written is recorded in install_manifest.json, so the next upgrade and the uninstaller touch exactly that.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CK3xBL.Setup.Util;

namespace CK3xBL.Setup.Engine;

internal sealed class InstallChoices
{
    public bool InstallCk3Mod = true;
    public bool EnableInDlcLoad = true;
    public bool InstallBlModule = true;
    public bool StartMenuShortcut = true;
    public bool DesktopShortcut;
    public bool ReplaceExisting;
    public bool RemoveModuleLink;
    public bool AllowDowngrade;
    public bool DryRun;

    public static InstallChoices From(Options o) => new()
    {
        InstallCk3Mod = !o.NoCk3Mod,
        EnableInDlcLoad = !o.NoDlcLoad,
        InstallBlModule = !o.NoBlModule,
        StartMenuShortcut = !o.NoStartMenu,
        DesktopShortcut = o.DesktopShortcut,
        ReplaceExisting = o.ReplaceExisting,
        RemoveModuleLink = o.RemoveModuleLink,
        AllowDowngrade = o.AllowDowngrade,
        DryRun = o.DryRun,
    };
}

internal sealed class InstallEngine
{
    private readonly SetupContext _c;
    private readonly Payload _p;
    private readonly InstallChoices _o;
    private readonly IInteraction _ui;
    private readonly SetupLog _log;
    private readonly Changes _changes;
    private readonly string? _payloadPath;
    private readonly string _stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    private InstallManifest? _previous;
    private InstallManifest _m = new();
    private bool _modInstalled;

    public readonly List<StepResult> Results = new();

    /// <summary>Progress for the wizard: (percent, step name).</summary>
    public event Action<int, string>? Progress;

    /// <param name="payloadPath">The /payload file, when the payload is not inside the running exe (passed to the elevated helper).</param>
    public InstallEngine(SetupContext ctx, Payload payload, InstallChoices choices, IInteraction ui, SetupLog log, string? payloadPath)
    {
        _c = ctx;
        _p = payload;
        _o = choices;
        _ui = ui;
        _log = log;
        _payloadPath = payloadPath;
        _changes = new Changes(choices.DryRun, log);
    }

    public InstallManifest Manifest => _m;

    public int Run()
    {
        var version = _p.Manifest.Version;
        _log.Info($"{Product.Name} {version} setup {Product.SetupVersion}" + (_o.DryRun ? " (dry run: nothing is changed)" : "") +
                  (_c.Sandbox ? $" in the sandbox {_c.SandboxRoot}" : ""));
        _log.Info($"payload {_p.Origin}");
        _log.Info($"install folder {_c.InstallDir}");
        _log.Info($"CK3 {_c.Ck3Dir ?? "(not found)"} {GameDetector.Ck3Version(_c.Ck3Dir)}; CK3 user folder {_c.Ck3UserDir}");
        _log.Info($"Bannerlord {_c.BlDir ?? "(not found)"} {GameDetector.BannerlordVersion(_c.BlDir)}");

        Progress?.Invoke(2, "Checking the package");
        try { _p.VerifyAll(); }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            Add("Package", StepStatus.Failed, e.Message + ". Download the setup again.");
            return ExitCodes.Failed;
        }

        _previous = InstallManifest.TryLoad(_c.ManifestPath, out var problem);
        if (problem is not null) _log.Warn(problem + "; treating this as a fresh install");
        if (_previous is not null)
        {
            int cmp = ReleaseVersion.Compare(version, _previous.Version);
            _log.Info(cmp > 0 ? $"upgrading from {_previous.Version}" : cmp == 0 ? $"repairing {_previous.Version}" : $"{_previous.Version} is installed");
            if (cmp < 0)
            {
                int choice = _ui.Ask("Newer version installed",
                    $"{Product.Name} {_previous.Version} is installed, which is newer than this setup ({version}).\n\nInstall the older version anyway?",
                    new[] { "Install anyway", "Cancel" }, _o.AllowDowngrade ? 0 : 1);
                if (choice != 0)
                {
                    Add("Version", StepStatus.Failed, $"{_previous.Version} is newer than {version}; nothing was changed (use /allow-downgrade)");
                    return ExitCodes.Failed;
                }
            }
        }

        if (FileOps.IsReparsePoint(_c.InstallDir))
        {
            Add("Program files", StepStatus.Failed, $"{_c.InstallDir} is a link; nothing was written through it. Choose another install folder.");
            return ExitCodes.Failed;
        }

        if (!EnsureBridgeStopped()) return ExitCodes.Blocked;

        _m = new InstallManifest
        {
            Version = version,
            InstalledUtc = DateTime.UtcNow,
            InstallDir = _c.InstallDir,
            SharedDir = _c.SharedDir,
            Ck3Dir = GameDetector.IsCk3Dir(_c.Ck3Dir) ? _c.Ck3Dir : null,
            Ck3UserDir = _c.Ck3UserDir,
            Ck3UserDirCustom = _c.Ck3UserDirCustom,
            BlDir = GameDetector.IsBannerlordDir(_c.BlDir) ? _c.BlDir : null,
        };
        if (_previous is not null)
        {
            // Copies setup moved aside earlier stay recorded until uninstall puts them back (records of moves that never happened are dropped).
            _m.MovedAside.AddRange(_previous.MovedAside.Where(a => File.Exists(a.Backup) || Directory.Exists(a.Backup)));
            _m.RemovedLinks.AddRange(_previous.RemovedLinks);
        }

        // A step that fails part-way keeps the earlier install's records for its targets, so nothing on disk is forgotten.
        Step(10, "Program files", InstallApp, Targets.App, Targets.Uninstaller);
        Step(40, "CK3 mod", InstallCk3Mod, Targets.Ck3Mod, Targets.Ck3Descriptor);
        Step(55, "CK3 mod list", EnableModList);
        Step(65, "Bannerlord module", InstallModule, Targets.BlModule);
        Step(80, "Shortcuts", InstallShortcuts);
        Step(85, "Add/Remove Programs", RegisterUninstall);
        Step(90, "Settings", SeedConfig);
        Step(95, "Install record", WriteManifest);
        Step(98, "Program check", VerifyBridge);
        Progress?.Invoke(100, "Done");

        if (Results.Any(r => r.Step == "Install record" && r.Status == StepStatus.Failed)) return ExitCodes.Failed;
        return StepResults.ExitCode(Results);
    }

    private void Step(int percent, string name, Func<StepResult> step, params string[] targets)
    {
        Progress?.Invoke(percent, name);
        StepResult r;
        try { r = step(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException or System.Runtime.InteropServices.COMException)
        {
            r = new StepResult { Status = StepStatus.Failed, Message = e.Message };
            CarryOver(targets);
        }
        r.Step = name;
        Add(r);
    }

    private void Add(string step, StepStatus status, string message) => Add(new StepResult { Step = step, Status = status, Message = message });

    private void Add(StepResult r)
    {
        Results.Add(r);
        var level = r.Status switch { StepStatus.Failed => LogLevel.Error, StepStatus.Warning or StepStatus.Skipped => LogLevel.Warn, _ => LogLevel.Info };
        _log.Write(level, $"{r.Step}: {r.Message}");
    }

    private static StepResult Result(StepStatus status, string message) => new() { Status = status, Message = message };

    private IEnumerable<InstalledFile> Previous(params string[] targets) =>
        _previous?.Files.Where(f => targets.Contains(f.Target)) ?? Enumerable.Empty<InstalledFile>();

    /// <summary>Keeps the earlier install's records for these targets that this run has not replaced (a step not selected,
    /// skipped or stopped part-way), so upgrades and uninstall still know those files.</summary>
    private void CarryOver(params string[] targets)
    {
        var have = new HashSet<string>(_m.Files.Select(f => FileOps.Full(f.Path)), StringComparer.OrdinalIgnoreCase);
        foreach (var f in Previous(targets))
            if (have.Add(FileOps.Full(f.Path))) _m.Files.Add(f);
    }

    private void RecordMovedAside(MovedAside a)
    {
        if (!_m.MovedAside.Any(x => FileOps.PathEquals(x.Original, a.Original) && FileOps.PathEquals(x.Backup, a.Backup))) _m.MovedAside.Add(a);
    }

    // ---------------------------------------------------------------------------------------------------------------------

    /// <summary>A running CK3xBL.exe from the install folder locks its files: ask it to quit (never kill it).</summary>
    private bool EnsureBridgeStopped()
    {
        while (true)
        {
            var running = ProcessGuard.Bridge(_c.InstallDir);
            try
            {
                if (running.Count == 0) return true;
                if (_o.DryRun)
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
                    "Crowns & Blades is running and must be closed before it can be updated.\n\n" +
                    "Close it now? It closes its own Bannerlord window and puts CK3's window back first.",
                    new[] { "Close it", "Cancel" }, 1);
                if (choice != 0)
                {
                    Add("Crowns & Blades", StepStatus.Failed, "it is running; quit it from its tray icon and run setup again");
                    return false;
                }
                if (ProcessGuard.RequestBridgeQuit(running, TimeSpan.FromSeconds(30), _log)) continue;
                int retry = _ui.Ask("Crowns & Blades is still running",
                    "Right-click the Crowns & Blades icon in the notification area and choose Quit, then press Retry.",
                    new[] { "Retry", "Cancel" }, 1);
                if (retry != 0)
                {
                    Add("Crowns & Blades", StepStatus.Failed, "it is still running; quit it from its tray icon and run setup again");
                    return false;
                }
            }
            finally { running.ForEach(p => p.Dispose()); }
        }
    }

    private StepResult InstallApp()
    {
        var dir = _c.InstallDir;
        if (FileOps.LinkInside(dir) is { } link)
        {
            CarryOver(Targets.App, Targets.Uninstaller);
            return Result(StepStatus.Skipped, $"{link} is a link; nothing was written through it");
        }
        var ours = new HashSet<string>(Previous(Targets.App, Targets.Uninstaller).Select(f => FileOps.Full(f.Path)), StringComparer.OrdinalIgnoreCase);
        var newPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int written = 0, movedAside = 0;

        // A file setup is about to replace that an earlier install of ours did not write belongs to someone else: it is
        // moved to setup-backups (and put back by uninstall), never overwritten.
        void Place(string dest, string sha256, Action write)
        {
            newPaths.Add(FileOps.Full(dest));
            var current = FileOps.TrySha256File(dest);
            if (current == sha256) return;
            if (File.Exists(dest) && !ours.Contains(FileOps.Full(dest)))
            {
                MoveAside(dest, Path.Combine(_c.BackupRoot, _stamp, "app", FileOps.Relative(dir, dest)));
                movedAside++;
            }
            _changes.Do($"write {dest}", write);
            written++;
        }

        // The uninstaller first (this setup without its payload, a few hundred KB): whatever fails later, it is on disk.
        var uninstaller = Path.Combine(dir, Product.UninstallerExe);
        var stub = Payload.ReadStub(_c.SelfExe);
        var stubHash = FileOps.Sha256(stub);
        Place(uninstaller, stubHash, () => FileOps.WriteAtomic(uninstaller, stub));
        _m.Files.Add(new InstalledFile { Path = uninstaller, Sha256 = stubHash, Size = stub.Length, Target = Targets.Uninstaller });

        foreach (var f in _p.FilesWithPrefix(PayloadManifest.AppPrefix))
        {
            var dest = Path.Combine(dir, f.Path.Substring(PayloadManifest.AppPrefix.Length).Replace('/', '\\'));
            Place(dest, f.Sha256, () => _p.Extract(f, dest));
            _m.Files.Add(new InstalledFile { Path = dest, Sha256 = f.Sha256, Size = f.Size, Target = Targets.App });
        }

        var previousDir = _previous is { InstallDir.Length: > 0 } ? _previous.InstallDir : dir;
        ModuleInstaller.RemoveStale(Previous(Targets.App, Targets.Uninstaller), newPaths, previousDir, _changes, _log);
        return Result(movedAside == 0 ? StepStatus.Done : StepStatus.Warning,
            $"{dir} ({written} files written, {newPaths.Count - written} already up to date)" +
            (movedAside > 0 ? $"; {movedAside} files there that setup had not installed were moved to {Path.Combine(_c.BackupRoot, _stamp, "app")}" : ""));
    }

    private StepResult InstallCk3Mod()
    {
        if (!_o.InstallCk3Mod)
        {
            CarryOver(Targets.Ck3Mod, Targets.Ck3Descriptor);
            _modInstalled = File.Exists(_c.ModDescriptorPath);
            return Result(StepStatus.NotSelected, "not selected");
        }
        var modDir = _c.ModDir;
        var descriptor = _c.ModDescriptorPath;

        var link = FileOps.LinkInside(modDir) ?? (FileOps.IsReparsePoint(descriptor) ? descriptor : null);
        if (link is not null)
            return Result(StepStatus.Skipped, $"{link} is a link (a development checkout?); nothing was written through it");

        bool ownedDir = Previous(Targets.Ck3Mod).Any();
        bool ownedDescriptor = Previous(Targets.Ck3Descriptor).Any();
        bool foreignDir = !ownedDir && Directory.Exists(modDir) && Directory.EnumerateFileSystemEntries(modDir).Any();
        var pointsTo = File.Exists(descriptor) ? DescriptorPath(descriptor) : null;
        bool foreignDescriptor = !ownedDescriptor && File.Exists(descriptor) && !SameModPath(pointsTo, modDir);

        if (foreignDir || foreignDescriptor)
        {
            var what = new List<string>();
            if (foreignDescriptor) what.Add($"{descriptor} points to {pointsTo ?? "an unknown folder"}");
            if (foreignDir) what.Add($"{modDir} already exists");
            var backup = Path.Combine(_c.BackupRoot, _stamp);
            int choice = _ui.Ask("CK3 mod already present",
                "A Crowns & Blades mod that this setup did not install was found:\n  " + string.Join("\n  ", what) +
                $"\n\nReplace it? The existing files are moved to\n  {backup}\nand put back if you uninstall.",
                new[] { "Replace", "Skip" }, _o.ReplaceExisting ? 0 : 1);
            if (choice != 0)
            {
                _modInstalled = File.Exists(descriptor);
                return Result(StepStatus.Skipped, "an existing CK3 mod was kept (" + string.Join("; ", what) + ")");
            }
            if (File.Exists(descriptor)) MoveAside(descriptor, Path.Combine(backup, Product.ModDescriptor));
            if (Directory.Exists(modDir)) MoveAside(modDir, Path.Combine(backup, Product.ModFolder));
        }

        var newPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int written = 0;
        foreach (var f in _p.FilesWithPrefix(PayloadManifest.ModPrefix))
        {
            var dest = Path.Combine(modDir, f.Path.Substring(PayloadManifest.ModPrefix.Length).Replace('/', '\\'));
            newPaths.Add(FileOps.Full(dest));
            if (FileOps.TrySha256File(dest) != f.Sha256)
            {
                _changes.Do($"write {dest}", () => _p.Extract(f, dest));
                written++;
            }
            _m.Files.Add(new InstalledFile { Path = dest, Sha256 = f.Sha256, Size = f.Size, Target = Targets.Ck3Mod });
        }
        // Also removes the earlier install's files at an earlier place (the Documents folder moved, or another folder was chosen).
        ModuleInstaller.RemoveStale(Previous(Targets.Ck3Mod), newPaths, "", _changes, _log);
        ModuleInstaller.RemoveStale(Previous(Targets.Ck3Descriptor), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FileOps.Full(descriptor) },
            "", _changes, _log);

        // mod\ck3xbl.mod: the mod's descriptor plus an absolute path= (forward slashes), UTF-8 without BOM, as the launcher writes it.
        var bytes = FileOps.EncodeUtf8(BuildDescriptor(), bom: false);
        var hash = FileOps.Sha256(bytes);
        if (FileOps.TrySha256File(descriptor) != hash) _changes.Do($"write {descriptor}", () => FileOps.WriteAtomic(descriptor, bytes));
        _m.Files.Add(new InstalledFile { Path = descriptor, Sha256 = hash, Size = bytes.Length, Target = Targets.Ck3Descriptor });
        _modInstalled = true;
        return Result(StepStatus.Done, $"{modDir} ({written} files written) and {Product.ModDescriptor}");
    }

    /// <summary>Recorded before the move: a move that fails half-way still leaves a record of where the copy went (uninstall
    /// skips records whose backup does not exist).</summary>
    private void MoveAside(string original, string backup)
    {
        RecordMovedAside(new MovedAside { Original = original, Backup = backup });
        _changes.Do($"move {original} to {backup}", () => FileOps.Move(original, backup));
    }

    internal string BuildDescriptor()
    {
        var source = _p.Manifest.Files.FirstOrDefault(f => f.Path == PayloadManifest.ModPrefix + "descriptor.mod")
                     ?? throw new InvalidDataException("the payload has no descriptor.mod");
        var text = FileOps.ReadUtf8(_p.ReadAll(source), out _).Replace("\r\n", "\n");
        var lines = text.Split('\n').Where(l => !Regex.IsMatch(l, @"^\s*path\s*=")).ToList();
        while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        lines.Add($"path=\"{FileOps.Full(_c.ModDir).Replace('\\', '/')}\"");
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>The path= of a .mod descriptor, resolved against the CK3 user folder when relative.</summary>
    internal string? DescriptorPath(string descriptorFile)
    {
        try
        {
            var m = Regex.Match(File.ReadAllText(descriptorFile), "^\\s*path\\s*=\\s*\"(?<p>[^\"]*)\"", RegexOptions.Multiline);
            if (!m.Success) return null;
            var p = m.Groups["p"].Value;
            return Path.IsPathRooted(p) ? p : Path.Combine(_c.Ck3UserDir, p.Replace('/', '\\'));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static bool SameModPath(string? a, string b)
    {
        try { return a is not null && FileOps.PathEquals(a, b); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { return false; }
    }

    private StepResult EnableModList()
    {
        if (!_o.EnableInDlcLoad)
        {
            _m.DlcLoad = _previous?.DlcLoad;
            return Result(StepStatus.NotSelected, "not selected");
        }
        if (!_modInstalled && !_o.DryRun)
        {
            _m.DlcLoad = _previous?.DlcLoad;
            return Result(StepStatus.Skipped, "the CK3 mod is not installed");
        }
        if (ProcessGuard.Ck3Running(_c))
        {
            _m.DlcLoad = _previous?.DlcLoad;
            return Result(StepStatus.Warning,
                "Crusader Kings III is running, so its mod list was not changed. Start CK3 from the Crowns & Blades tray icon, " +
                "or add the mod to your playset in the Paradox Launcher.");
        }
        // The earlier record stays until the edit succeeds: a failure here must not lose the way to undo an earlier edit.
        _m.DlcLoad = _previous?.DlcLoad;
        if (_o.DryRun)
        {
            if (_previous?.DlcLoad is { Added: true } p && !FileOps.PathEquals(p.Path, Ck3ModList.PathIn(_c.Ck3UserDir)) && File.Exists(p.Path))
                _log.Plan($"take {Ck3ModList.Entry} out of {p.Path} again (the CK3 user folder changed)");
            _log.Plan(Ck3ModList.PlanEnable(_c.Ck3UserDir));
            return Result(StepStatus.Done, "planned");
        }
        var outcome = Ck3ModList.Enable(_c.Ck3UserDir, PreviousModList(), out var record, out var message);
        _m.DlcLoad = record;
        return outcome == ModListOutcome.Unreadable
            ? Result(StepStatus.Warning, message + ". Add the mod to your playset in the Paradox Launcher.")
            : Result(StepStatus.Done, message);
    }

    /// <summary>The earlier mod-list record, for this run's dlc_load.json. When the CK3 user folder changed: if the old
    /// dlc_load.json is gone (Windows moved the Documents folder, file and backup included), the record moves along with
    /// it; if it is still there (another folder was chosen), our entry is taken out of that old list first.</summary>
    private DlcLoadRecord? PreviousModList()
    {
        var previous = _previous?.DlcLoad;
        var path = Ck3ModList.PathIn(_c.Ck3UserDir);
        if (previous is null || FileOps.PathEquals(previous.Path, path)) return previous;
        if (File.Exists(previous.Path))
        {
            if (previous.Added)
            {
                Ck3ModList.Disable(previous, FileOps.ParentOrSelf(previous.Path), out var undone);
                _log.Info($"the CK3 user folder changed: {undone}");
            }
            return null;
        }
        return new DlcLoadRecord
        {
            Path = path,
            Added = previous.Added,
            CreatedFile = previous.CreatedFile,
            Backup = previous.Backup is null ? null : path + Ck3ModList.BackupSuffix,
            WrittenSha256 = previous.WrittenSha256,
        };
    }

    private StepResult InstallModule()
    {
        if (!_o.InstallBlModule)
        {
            CarryOver(Targets.BlModule);
            return Result(StepStatus.NotSelected, "not selected");
        }
        if (!GameDetector.IsBannerlordDir(_c.BlDir))
        {
            CarryOver(Targets.BlModule);
            return Result(StepStatus.Skipped, "Mount & Blade II: Bannerlord was not found; choose its folder to install the module");
        }

        var req = new ModuleRequest { BlDir = _c.BlDir!, BackupRoot = _c.BackupRoot };
        var target = req.ModuleDir;
        var elsewhere = new List<InstalledFile>();
        foreach (var f in Previous(Targets.BlModule))
            (FileOps.IsUnder(f.Path, target) ? req.PreviousFiles : elsewhere).Add(f);
        // The earlier install's module in another Bannerlord folder: removed like stale files (never by the elevated helper).
        if (elsewhere.Count > 0) ModuleInstaller.RemoveStale(elsewhere, new HashSet<string>(), "", _changes, _log);

        while (!_o.DryRun && ProcessGuard.BannerlordRunning(_c.BlDir))
        {
            int choice = _ui.Ask("Bannerlord is running", "Mount & Blade II: Bannerlord is running and uses the module's files.\n\n" +
                                                          "Close Bannerlord, then press Retry.", new[] { "Retry", "Skip" }, 1);
            if (choice != 0)
            {
                CarryOver(Targets.BlModule);
                return Result(StepStatus.Failed, "Bannerlord is running; close it and run setup again");
            }
        }

        if (FileOps.IsReparsePoint(target))
        {
            var linkTarget = Native.FinalPath(target) ?? "another folder";
            int choice = _ui.Ask("Development link found",
                $"{target}\nis a link to\n{linkTarget}\n(a development checkout). Setup never writes through a link.\n\n" +
                "Remove the link and install the release module? Only the link is removed; the folder it points to is not touched.",
                new[] { "Remove the link and install", "Skip" }, _o.RemoveModuleLink ? 0 : 1);
            if (choice != 0)
            {
                CarryOver(Targets.BlModule);
                return Result(StepStatus.Skipped, $"{target} is a link to {linkTarget}; left unchanged");
            }
            req.RemoveLink = true;
        }
        else if (FileOps.LinkInside(target) is { } inner)
        {
            CarryOver(Targets.BlModule);
            return Result(StepStatus.Skipped, $"{inner} is a link; nothing was written through it");
        }
        else if (req.PreviousFiles.Count == 0 && Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            var backup = Path.Combine(_c.BackupRoot, _stamp, Product.ModuleFolder);
            int choice = _ui.Ask("Bannerlord module already present",
                $"{target} exists but was not installed by this setup.\n\nReplace it? The existing folder is moved to\n  {backup}\n" +
                "and put back if you uninstall.", new[] { "Replace", "Skip" }, _o.ReplaceExisting ? 0 : 1);
            if (choice != 0) return Result(StepStatus.Skipped, $"an existing {target} was kept");
            req.MoveAsideTo = backup;
            // Recorded before the move, which may run in the elevated helper: if that stops early, the record is still here.
            RecordMovedAside(new MovedAside { Original = target, Backup = backup });
        }

        ModuleResult result;
        if (_o.DryRun || FileOps.CanWrite(req.ModulesDir))
        {
            result = ModuleInstaller.Install(_p, req, _changes, _log);
        }
        else
        {
            result = InstallModuleElevated(req) ?? ManualModule(req);
        }

        Merge(result);
        if (!result.Ok)
        {
            CarryOver(Targets.BlModule);
            return Result(StepStatus.Failed, result.Message);
        }
        return Result(StepStatus.Done, result.Message);
    }

    /// <summary>Records what a module run wrote, moved or unlinked, also when it stopped part-way.</summary>
    private void Merge(ModuleResult result)
    {
        var have = new HashSet<string>(_m.Files.Select(f => FileOps.Full(f.Path)), StringComparer.OrdinalIgnoreCase);
        foreach (var f in result.Files)
            if (have.Add(FileOps.Full(f.Path))) _m.Files.Add(f);
        foreach (var a in result.MovedAside) RecordMovedAside(a);
        foreach (var l in result.RemovedLinks)
            if (!_m.RemovedLinks.Contains(l)) _m.RemovedLinks.Add(l);
    }

    private ModuleResult? InstallModuleElevated(ModuleRequest req)
    {
        if (_ui.IsSilent || _c.Sandbox) return null;
        int choice = _ui.Ask("Administrator rights needed",
            $"Bannerlord's Modules folder\n  {req.ModulesDir}\ncannot be written without administrator rights.\n\n" +
            "Continue? Windows will ask for permission, for the module only.", new[] { "Continue", "Skip" }, 1);
        if (choice != 0) return null;
        var result = Elevation.Run(_c, Elevation.OpInstall, req, _payloadPath, _log);
        if (result is null || result.Ok) return result;
        // The helper stopped part-way: keep the record of what it did write or move, then fall back to the manual copy.
        _log.Warn($"the elevated module step failed: {result.Message}");
        Merge(result);
        return null;
    }

    /// <summary>No rights for Modules: put the module next to the program, and say where to copy it.</summary>
    private ModuleResult ManualModule(ModuleRequest req)
    {
        var manual = Path.Combine(_c.InstallDir, "bannerlord-module", Product.ModuleFolder);
        foreach (var f in _p.FilesWithPrefix(PayloadManifest.ModulePrefix))
        {
            var dest = Path.Combine(manual, f.Path.Substring(PayloadManifest.ModulePrefix.Length).Replace('/', '\\'));
            _changes.Do($"write {dest}", () => _p.Extract(f, dest));
            _m.Files.Add(new InstalledFile { Path = dest, Sha256 = f.Sha256, Size = f.Size, Target = Targets.App });
        }
        return new ModuleResult
        {
            Ok = false,
            Message = $"Bannerlord's Modules folder ({req.ModulesDir}) needs administrator rights. Copy the folder\n  {manual}\n" +
                      $"into\n  {req.ModulesDir}\n(or run setup again and accept the administrator prompt).",
        };
    }

    private StepResult InstallShortcuts()
    {
        var bridge = Path.Combine(_c.InstallDir, Product.BridgeExe);
        var wanted = new List<string>();
        if (_o.StartMenuShortcut) wanted.Add(Path.Combine(_c.StartMenuPrograms, Product.ShortcutFile));
        if (_o.DesktopShortcut) wanted.Add(Path.Combine(_c.Desktop, Product.ShortcutFile));
        foreach (var lnk in wanted)
        {
            _m.Shortcuts.Add(lnk);   // recorded first: uninstall removes it if it exists, also after a failure here
            _changes.Do($"create the shortcut {lnk}", () =>
                Shell.CreateShortcut(lnk, bridge, _c.InstallDir, "Fight your Crusader Kings III battles in Mount & Blade II: Bannerlord"));
        }
        foreach (var old in _previous?.Shortcuts ?? new List<string>())
            if (!wanted.Any(w => FileOps.PathEquals(w, old)) && File.Exists(old))
                _changes.Do($"delete the shortcut {old}", () => FileOps.DeleteFile(old));
        return wanted.Count == 0 ? Result(StepStatus.NotSelected, "none") : Result(StepStatus.Done, string.Join(", ", wanted));
    }

    private StepResult RegisterUninstall()
    {
        var uninstaller = Path.Combine(_c.InstallDir, Product.UninstallerExe);
        if (!_o.DryRun && !File.Exists(uninstaller))
            return Result(StepStatus.Failed, $"{uninstaller} was not written, so no Add/Remove Programs entry was made. Run setup again.");
        long kb = _m.Files.Where(f => f.Target is Targets.App or Targets.Uninstaller).Sum(f => f.Size) / 1024;
        var display = _c.RegistryDisplay(Product.UninstallKey);
        _changes.Do($"register the uninstaller under {display}", () => Shell.RegisterUninstall(_c, _m.Version, kb));
        _m.RegistryKeys.Add(display);
        return Result(StepStatus.Done, display);
    }

    /// <summary>First install: write the chosen game folders to the bridge's settings. An existing file is kept, except that
    /// the CK3 user folder setup itself wrote there follows the Documents folder when Windows moved it.</summary>
    private StepResult SeedConfig()
    {
        var config = Path.Combine(_c.SharedDir, "config.json");
        if (File.Exists(config)) return FollowMovedUserDir(config);
        var ck3 = new JsonObject();
        if (_m.Ck3Dir is not null) ck3["gameDir"] = _m.Ck3Dir;
        ck3["userDir"] = _c.Ck3UserDir;
        var o = new JsonObject { ["ck3"] = ck3 };
        if (_m.BlDir is not null) o["bannerlord"] = new JsonObject { ["gameDir"] = _m.BlDir };
        var bytes = FileOps.EncodeUtf8(Json.Write(o, indented: true) + "\n", bom: false);
        _changes.Do($"write {config}", () => FileOps.WriteAtomic(config, bytes));
        return Result(StepStatus.Done, $"wrote {config}");
    }

    private StepResult FollowMovedUserDir(string config)
    {
        var old = _c.Ck3UserDirMovedFrom;
        if (old is null) return Result(StepStatus.Done, $"kept the existing {config}");
        var bytes = FileOps.ReadShared(config);
        var text = FileOps.ReadUtf8(bytes, out bool bom);
        if (!Json.TryParse(text, out var root) || root is not JsonObject o || o.GetObject("ck3") is not { } ck3 ||
            ck3.GetString("userDir") is not { } userDir || !SameModPath(userDir, old))
            return Result(StepStatus.Done, $"kept the existing {config}");
        ck3["userDir"] = _c.Ck3UserDir;
        var updated = FileOps.EncodeUtf8(Json.Write(o, indented: true) + "\n", bom);
        _changes.Do($"update ck3.userDir in {config}: {old} -> {_c.Ck3UserDir}", () => FileOps.WriteAtomic(config, updated));
        return Result(StepStatus.Done, $"{config}: the CK3 user folder now is {_c.Ck3UserDir} (the Documents folder moved)");
    }

    /// <summary>Antivirus programs sometimes quarantine the unsigned bridge right after it is written: say so instead of
    /// reporting a working install.</summary>
    private StepResult VerifyBridge()
    {
        if (_o.DryRun) return Result(StepStatus.NotSelected, "dry run");
        var exe = Path.Combine(_c.InstallDir, Product.BridgeExe);
        var entry = _m.Files.FirstOrDefault(f => f.Target == Targets.App && FileOps.PathEquals(f.Path, exe));
        if (entry is null) return Result(StepStatus.NotSelected, $"{Product.BridgeExe} is not part of this install");
        if (FileOps.TrySha256File(exe) == entry.Sha256) return Result(StepStatus.Done, $"{Product.BridgeExe} is in place");
        return Result(StepStatus.Warning, File.Exists(exe)
            ? $"{exe} was changed right after install, probably by your antivirus. See {Product.AntivirusHelpUrl}"
            : $"{exe} was removed right after install, probably by your antivirus. See {Product.AntivirusHelpUrl}");
    }

    private StepResult WriteManifest()
    {
        _changes.Do($"write {_c.ManifestPath}", () => _m.Save(_c.ManifestPath));
        return Result(StepStatus.Done, _c.ManifestPath);
    }
}
