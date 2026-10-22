// The Bannerlord module step with administrator rights, for a Modules folder the player cannot write (some GOG / Epic /
// Program Files installs). Setup re-launches itself elevated with /elevated-request=<file>; that copy does only this step.
// The request file lies in the player's %TEMP%, so the helper trusts none of its paths: every file it deletes must be inside
// ModulesCK3Bridge, every backup inside the player's setup-backups folder, and it reports only next to the request.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CK3xBL.Setup.Util;

namespace CK3xBL.Setup.Engine;

internal static class Elevation
{
    public const string OpInstall = "install-module";
    public const string OpUninstall = "uninstall-module";
    public const string OpRestore = "restore-module";

    /// <summary>Runs the module step elevated. Null when the player declined the prompt or the helper failed to report.</summary>
    public static ModuleResult? Run(SetupContext ctx, string op, ModuleRequest req, string? payloadPath, SetupLog log)
    {
        var id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(ctx.TempDir);
        var requestFile = Path.Combine(ctx.TempDir, $"ck3xbl-elevated-{id}.json");
        var resultFile = Path.Combine(ctx.TempDir, $"ck3xbl-elevated-{id}.result.json");
        var o = new JsonObject
        {
            ["op"] = op,
            ["blDir"] = req.BlDir,
            ["removeLink"] = req.RemoveLink,
            ["moveAsideTo"] = req.MoveAsideTo,
            ["restoreFrom"] = req.RestoreFrom,
            ["backupRoot"] = req.BackupRoot ?? ctx.BackupRoot,
            ["payload"] = payloadPath ?? ctx.SelfExe,
            ["resultFile"] = resultFile,
            ["logFile"] = log.FilePath,
            ["previousFiles"] = FilesToJson(req.PreviousFiles),
        };
        File.WriteAllText(requestFile, Json.Write(o, indented: true), FileOps.Utf8NoBom);
        try
        {
            var psi = new ProcessStartInfo(ctx.SelfExe, Options.Quote(new[] { "/elevated-request=" + requestFile }))
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(ctx.SelfExe)!,
            };
            log.Info(op == OpRestore ? "asking for administrator rights to put the earlier Bannerlord module back"
            : "asking for administrator rights for the Bannerlord module");
            using (var p = Process.Start(psi))
            {
                if (p is null) return null;
                p.WaitForExit();
            }
            if (!File.Exists(resultFile))
            {
                log.Warn("the elevated helper did not report back");
                return null;
            }
            return ResultFromJson(File.ReadAllText(resultFile));
        }
        catch (Win32Exception e) when (e.NativeErrorCode == Native.ErrorCancelled)
        {
            log.Warn("the administrator prompt was declined");
            return null;
        }
        catch (Exception e) when (e is Win32Exception or IOException or FormatException or InvalidDataException)
        {
            log.Warn($"the elevated module step failed: {e.Message}");
            return null;
        }
        finally
        {
            FileOps.TryDelete(requestFile);
            FileOps.TryDelete(resultFile);
        }
    }

    /// <summary>The elevated copy: reads the request, does the step, writes the result. Returns the process exit code.</summary>
    public static int RunHelper(string requestFile)
    {
        var requestDir = Path.GetDirectoryName(FileOps.Full(requestFile))!;
        if (Json.Parse(File.ReadAllText(requestFile)) is not JsonObject o) return ExitCodes.BadArguments;
        var resultFile = o.GetString("resultFile");
        if (!IsIn(resultFile, requestDir)) return ExitCodes.BadArguments;

        var op = o.GetString("op");
        var backupRoot = o.GetString("backupRoot");
        var req = new ModuleRequest
        {
            BlDir = o.GetString("blDir") ?? "",
            RemoveLink = o.GetBool("removeLink"),
            MoveAsideTo = o.GetString("moveAsideTo"),
            RestoreFrom = o.GetString("restoreFrom"),
            BackupRoot = backupRoot,
            PreviousFiles = FilesFromJson(o.GetArray("previousFiles")),
        };
        using var log = new SetupLog(console: false);
        // The log is appended to only in the request's folder (%TEMP%) or the player's ck3xbl folder; anywhere else it is not written.
        var sharedDir = IsBackupRoot(backupRoot) ? Path.GetDirectoryName(FileOps.Full(backupRoot!)) : null;
        if (o.GetString("logFile") is { Length: > 0 } logFile && (IsIn(logFile, requestDir) || IsIn(logFile, sharedDir))) log.OpenFile(logFile);

        ModuleResult result;
        bool refused = false;
        try
        {
            if (op is not (OpInstall or OpUninstall or OpRestore)) throw new RequestRefused($"unknown operation '{op}'");
            if (!GameDetector.IsBannerlordDir(req.BlDir)) throw new RequestRefused($"{req.BlDir} is not a Bannerlord folder");
            if (req.PreviousFiles.FirstOrDefault(f => !FileOps.IsUnder(f.Path, req.ModuleDir)) is { } outside)
                throw new RequestRefused($"{outside.Path} is not inside {req.ModuleDir}");
            foreach (var backup in new[] { req.MoveAsideTo, req.RestoreFrom })
                if (backup is not null && !(IsBackupRoot(backupRoot) && IsIn(backup, backupRoot) && !FileOps.PathEquals(backup, backupRoot)))
                    throw new RequestRefused($"{backup} is not inside the setup-backups folder");
            if (op == OpRestore && req.RestoreFrom is null) throw new RequestRefused("nothing to put back");

            var changes = new Changes(dryRun: false, log);
            log.Info($"elevated helper: {op} in {req.ModuleDir}");
            if (op == OpUninstall)
            {
                result = ModuleInstaller.Uninstall(req, changes, log);
            }
            else if (op == OpRestore)
            {
                result = ModuleInstaller.Restore(req, changes, log);
            }
            else
            {
                using var payload = Payload.Open(o.GetString("payload") ?? "");
                payload.VerifyAll();
                result = ModuleInstaller.Install(payload, req, changes, log);
            }
        }
        catch (RequestRefused e)
        {
            log.Error($"elevated helper: request refused: {e.Message}");
            result = new ModuleResult { Ok = false, Message = $"request refused: {e.Message}" };
            refused = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            log.Error($"elevated helper: {e.Message}");
            result = new ModuleResult { Ok = false, Message = e.Message };
        }
        File.WriteAllText(resultFile!, ResultToJson(result), FileOps.Utf8NoBom);
        return refused ? ExitCodes.BadArguments : result.Ok ? ExitCodes.Ok : ExitCodes.Failed;
    }

    private sealed class RequestRefused : Exception
    {
        public RequestRefused(string message) : base(message) { }
    }

    /// <summary><paramref name="path"/> is a rooted path inside <paramref name="dir"/> (not the folder itself).</summary>
    private static bool IsIn(string? path, string? dir)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dir)) return false;
        try { return Path.IsPathRooted(path) && FileOps.IsUnder(path!, dir!) && !FileOps.PathEquals(path, dir); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>A rooted ...\ck3xbl\setup-backups folder (the shape of <see cref="SetupContext.BackupRoot"/>).</summary>
    private static bool IsBackupRoot(string? root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        try
        {
            if (!Path.IsPathRooted(root)) return false;
            var full = FileOps.Full(root!);
            return string.Equals(Path.GetFileName(full), Product.BackupFolder, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Path.GetFileName(Path.GetDirectoryName(full)), Product.SharedFolder, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static List<object?> FilesToJson(IEnumerable<InstalledFile> files) =>
        files.Select(f => (object?)new JsonObject
        {
            ["path"] = f.Path,
            ["sha256"] = f.Sha256,
            ["size"] = new JsonNumber(f.Size),
            ["target"] = f.Target,
        }).ToList();

    private static List<InstalledFile> FilesFromJson(List<object?>? list) =>
        (list ?? new List<object?>()).OfType<JsonObject>().Select(f => new InstalledFile
        {
            Path = f.GetString("path") ?? "",
            Sha256 = f.GetString("sha256") ?? "",
            Size = f.GetLong("size") ?? 0,
            Target = f.GetString("target") ?? Targets.BlModule,
        }).Where(f => f.Path.Length > 0).ToList();

    private static string ResultToJson(ModuleResult r) => Json.Write(new JsonObject
    {
        ["ok"] = r.Ok,
        ["message"] = r.Message,
        ["files"] = FilesToJson(r.Files),
        ["remaining"] = FilesToJson(r.Remaining),
        ["movedAside"] = r.MovedAside.Select(a => (object?)new JsonObject { ["original"] = a.Original, ["backup"] = a.Backup }).ToList(),
        ["removedLinks"] = r.RemovedLinks.Cast<object?>().ToList(),
    }, indented: true);

    private static ModuleResult ResultFromJson(string json)
    {
        if (Json.Parse(json) is not JsonObject o) throw new InvalidDataException("bad helper result");
        var r = new ModuleResult
        {
            Ok = o.GetBool("ok"),
            Message = o.GetString("message") ?? "",
            Files = FilesFromJson(o.GetArray("files")),
            Remaining = FilesFromJson(o.GetArray("remaining")),
        };
        foreach (var a in (o.GetArray("movedAside") ?? new List<object?>()).OfType<JsonObject>())
            r.MovedAside.Add(new MovedAside { Original = a.GetString("original") ?? "", Backup = a.GetString("backup") ?? "" });
        r.RemovedLinks.AddRange((o.GetArray("removedLinks") ?? new List<object?>()).OfType<string>());
        return r;
    }
}
