// CK3xBL-Setup.exe: installs, upgrades and uninstalls Crowns & Blades. Run with /? for the command line.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using CK3xBL.Setup.Engine;
using CK3xBL.Setup.Ui;
using CK3xBL.Setup.Util;

namespace CK3xBL.Setup;

internal static class Program
{
    /// <summary>Set when this process handed the uninstall over to its copy in %TEMP% (which then writes /result).</summary>
    private static bool _handedOff;

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show($"Something went wrong: {e.Exception}\n\nPlease report it at {Product.IssuesUrl}",
                $"{Product.Name} Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);

        Options o;
        try { o = Options.Parse(args); }
        catch (ArgumentException e)
        {
            bool silent = Array.Exists(args, a => a.TrimStart('/', '-').StartsWith("silent", StringComparison.OrdinalIgnoreCase));
            Tell(silent, $"{e.Message}\n\n{Options.Usage}", error: true);
            return ExitCodes.BadArguments;
        }
        if (o.Help)
        {
            Tell(o.Silent, Options.Usage, error: false);
            return ExitCodes.Ok;
        }
        if (o.ElevatedRequest is not null)
        {
            try { return Elevation.RunHelper(o.ElevatedRequest); }
            catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException) { return ExitCodes.Failed; }
        }

        int code;
        try
        {
            code = CheckAccount(o) ?? (o.Uninstall ? RunUninstall(o) : RunInstall(o));
        }
        catch (Exception e)
        {
            Tell(o.Silent, $"Setup failed: {e.Message}", error: true);
            code = ExitCodes.Failed;
        }
        if (!_handedOff) WriteResult(o.ResultPath, code);
        return code;
    }

    /// <summary>Setup works for the Windows user signed in to this session. Run with another account's administrator password
    /// it would install into that account's profile; run with "Run as administrator" it would start the bridge (and so
    /// Bannerlord) elevated. Null: go on; otherwise the exit code.</summary>
    private static int? CheckAccount(Options o)
    {
        if (o.DryRun || o.FromTemp || o.TargetRoot is not null) return null;
        if (!Native.RunsAsSessionUser())
        {
            string account;
            using (var id = System.Security.Principal.WindowsIdentity.GetCurrent()) account = id.Name;
            Tell(o.Silent, $"{Product.Name} {(o.Uninstall ? "is removed" : "installs")} for your own Windows user only, but this setup " +
                           $"runs as another account ({account}), probably because an administrator's password was typed in.\n\n" +
                           "Close this and run the setup again normally, not with 'Run as administrator'.", error: true);
            return ExitCodes.BadArguments;
        }
        // Same account, elevated: harmless for files and registry (the same profile), but the bridge must not start elevated.
        if (o.Uninstall || o.Silent || !Native.IsElevated()) return null;
        int choice = ChoiceDialog.Show(null, $"{Product.Name} Setup",
            $"{Product.Name} installs for your Windows user only and needs no administrator rights. Close this and run the setup " +
            "normally, not with 'Run as administrator'.\n\nIf you continue, setup does not start Crowns & Blades at the end; " +
            "start it from the Start menu.", new[] { "Close", "Continue anyway" }, 0);
        return choice == 1 ? null : ExitCodes.Cancelled;
    }

    private static int RunInstall(Options o)
    {
        var ctx = SetupContext.Create(o);
        Payload? payload;
        try
        {
            payload = o.PayloadPath is not null ? Payload.Open(o.PayloadPath) : Payload.OpenAppended(ctx.SelfExe);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Tell(o.Silent, $"This setup file is damaged ({e.Message}). Download it again from {Product.RepoUrl}/releases.", error: true);
            return ExitCodes.Failed;
        }

        if (payload is null)
        {
            // A bare setup exe: the uninstaller copy in the install folder (started without /uninstall), or a developer build.
            var selfDir = Path.GetDirectoryName(ctx.SelfExe)!;
            if (File.Exists(Path.Combine(selfDir, Product.ManifestFile)) || File.Exists(ctx.ManifestPath))
            {
                o.Uninstall = true;
                return RunUninstall(o);
            }
            Tell(o.Silent, $"This copy of setup has no installation package inside it. Download CK3xBL-Setup from {Product.RepoUrl}/releases.",
                error: true);
            return ExitCodes.Failed;
        }

        using (payload)
        using (var log = new SetupLog(console: o.Silent || o.DryRun))
        {
            if (o.Silent || o.DryRun) SetupLog.PrepareConsole();
            var logPath = o.LogPath ?? (o.DryRun ? null : ctx.DefaultLogPath);
            if (logPath is not null) log.OpenFile(logPath);

            var previous = InstallManifest.TryLoad(ctx.ManifestPath, out _);
            ApplyPrevious(ctx, previous, o, log);

            if (o.Silent)
            {
                var engine = new InstallEngine(ctx, payload, InstallChoices.From(o), new SilentInteraction(log), log, o.PayloadPath);
                int code = engine.Run();
                if (o.StartBridge && code is ExitCodes.Ok or ExitCodes.Partial && !o.DryRun) StartBridge(ctx, log, gui: false);
                return code;
            }

            using var form = new WizardForm(ctx, payload, o, log, previous);
            Application.Run(form);
            if (form.StartBridgeRequested) StartBridge(ctx, log, gui: true);
            return form.ExitCode;
        }
    }

    /// <summary>An upgrade keeps the folders the player chose last time, unless the command line names others. The CK3 user
    /// folder follows the Documents folder when Windows or OneDrive moved it since (Known Folder Move), unless the player
    /// had chosen that folder by hand.</summary>
    private static void ApplyPrevious(SetupContext ctx, InstallManifest? previous, Options o, SetupLog log)
    {
        if (previous is null) return;
        if (o.Ck3UserDir is null && previous.Ck3UserDir.Length > 0)
        {
            var recorded = previous.Ck3UserDir;
            var current = ctx.DefaultCk3UserDir;
            if (FileOps.PathEquals(recorded, current))
            {
                ctx.Ck3UserDir = current;
            }
            else if (Directory.Exists(recorded) && (previous.Ck3UserDirCustom || !HasCk3Data(current)))
            {
                ctx.Ck3UserDir = recorded;
                ctx.Ck3UserDirCustom = previous.Ck3UserDirCustom;
            }
            else
            {
                ctx.Ck3UserDirMovedFrom = recorded;
                log.Info(Directory.Exists(recorded)
                    ? $"the Documents folder moved: {recorded} -> {current}"
                    : $"the CK3 user folder of the last install, {recorded}, no longer exists; using {current}");
            }
        }
        if (o.Ck3Dir is null && GameDetector.IsCk3Dir(previous.Ck3Dir)) ctx.Ck3Dir = previous.Ck3Dir;
        if (o.BlDir is null && GameDetector.IsBannerlordDir(previous.BlDir)) ctx.BlDir = previous.BlDir;
    }

    /// <summary>CK3 has used this user folder: its settings, its mod list or its mod folder are there.</summary>
    private static bool HasCk3Data(string dir) =>
        File.Exists(Path.Combine(dir, "pdx_settings.txt")) || File.Exists(Path.Combine(dir, Ck3ModList.FileName)) ||
        Directory.Exists(Path.Combine(dir, "mod"));

    private static int RunUninstall(Options o)
    {
        var ctx = SetupContext.Create(o);
        var selfDir = Path.GetDirectoryName(ctx.SelfExe)!;
        if (o.InstallDir is null && !o.FromTemp && File.Exists(Path.Combine(selfDir, Product.ManifestFile))) ctx.InstallDir = FileOps.Full(selfDir);

        // A running exe cannot delete itself: the copy in the install folder hands over to a copy of itself in %TEMP%.
        if (!o.FromTemp && !o.DryRun && FileOps.IsUnder(ctx.SelfExe, ctx.InstallDir))
        {
            Directory.CreateDirectory(ctx.TempDir);
            var tempExe = Path.Combine(ctx.TempDir, $"CK3xBL-Uninstall-{Guid.NewGuid():N}.exe");
            File.Copy(ctx.SelfExe, tempExe);
            var args = new List<string> { "/uninstall", "/from-temp", $"/wait-pid={Process.GetCurrentProcess().Id}" };
            args.AddRange(o.TargetArguments());
            if (o.InstallDir is null) args.Add($"/install-dir={ctx.InstallDir}");
            Process.Start(new ProcessStartInfo(tempExe, Options.Quote(args)) { UseShellExecute = false, WorkingDirectory = ctx.TempDir })?.Dispose();
            _handedOff = true;
            return ExitCodes.Ok;
        }
        if (o.WaitPid > 0) ProcessGuard.WaitForPid(o.WaitPid, TimeSpan.FromSeconds(30));

        int code;
        using (var log = new SetupLog(console: o.Silent || o.DryRun))
        {
            if (o.Silent || o.DryRun) SetupLog.PrepareConsole();
            var logPath = o.LogPath ?? (o.DryRun ? null : Path.Combine(ctx.TempDir, "CK3xBL-Uninstall.log"));
            if (logPath is not null) log.OpenFile(logPath);

            if (o.Silent)
            {
                code = new UninstallEngine(ctx, new SilentInteraction(log), log, o.DryRun, o.DeleteUserData).Run();
            }
            else
            {
                var manifest = InstallManifest.TryLoad(ctx.ManifestPath, out _);
                if (manifest is null)
                {
                    MessageBox.Show($"{Product.Name} is not installed in\n{ctx.InstallDir}", $"Uninstall {Product.Name}",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    code = ExitCodes.Failed;
                }
                else
                {
                    using var form = new UninstallForm(ctx, o, log, manifest);
                    Application.Run(form);
                    code = form.ExitCode;
                }
            }
        }
        if (o.FromTemp) DeleteSelfLater(ctx.SelfExe);
        return code;
    }

    private static void StartBridge(SetupContext ctx, SetupLog log, bool gui)
    {
        if (ctx.Sandbox) return;
        if (ctx.Elevated)
        {
            log.Info("setup runs as administrator, so it does not start Crowns & Blades; start it from the Start menu");
            return;
        }
        var exe = Path.Combine(ctx.InstallDir, Product.BridgeExe);
        if (!File.Exists(exe))
        {
            var text = $"{exe} is missing, so Crowns & Blades was not started. Your antivirus may have removed it; see {Product.AntivirusHelpUrl}";
            log.Warn(text);
            if (gui) MessageBox.Show(text, $"{Product.Name} Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (ProcessGuard.AnyBridgeInstance())
        {
            log.Info("Crowns & Blades is already running; not starting another copy");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = ctx.InstallDir, UseShellExecute = true })?.Dispose();
            log.Info($"started {exe}");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            log.Warn($"could not start {exe}: {e.Message}");
        }
    }

    /// <summary>The uninstaller's %TEMP% copy removes itself a moment after it exits.</summary>
    private static void DeleteSelfLater(string exe)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c ping -n 3 127.0.0.1 >nul & del /f /q \"{exe}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) { }
    }

    private static void WriteResult(string? path, int code)
    {
        if (path is null) return;
        try { FileOps.WriteAtomic(path, FileOps.Utf8NoBom.GetBytes($"{{\"exitCode\":{code}}}\n")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    private static void Tell(bool silent, string text, bool error)
    {
        if (silent && SetupLog.PrepareConsole())
        {
            try
            {
                if (error) Console.Error.WriteLine(text);
                else Console.Out.WriteLine(text);
            }
            catch (IOException) { }
            return;
        }
        if (silent) return;
        MessageBox.Show(text, $"{Product.Name} Setup", MessageBoxButtons.OK, error ? MessageBoxIcon.Error : MessageBoxIcon.Information);
    }
}
