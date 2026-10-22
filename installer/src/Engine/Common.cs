// Shared engine pieces: step results, exit codes, questions to the player, and the dry-run switch for every change.
using System;
using System.Collections.Generic;
using System.Linq;

namespace CK3xBL.Setup.Engine;

internal static class ExitCodes
{
    public const int Ok = 0;
    /// <summary>Finished, but a step was skipped, failed or needs the player's attention (see the log).</summary>
    public const int Partial = 1;
    public const int Failed = 2;
    /// <summary>Crowns & Blades (or Bannerlord) is running and was not closed.</summary>
    public const int Blocked = 3;
    public const int BadArguments = 4;
    public const int Cancelled = 5;
}

internal enum StepStatus { Done, NotSelected, Skipped, Warning, Failed }

internal sealed class StepResult
{
    public string Step = "";
    public StepStatus Status;
    public string Message = "";

    public override string ToString() => $"{Step}: {Status} - {Message}";
}

/// <summary>Questions to the player. The silent implementation answers with the choice the command line made.</summary>
internal interface IInteraction
{
    bool IsSilent { get; }

    /// <summary>Returns the index of the chosen button; <paramref name="silentChoice"/> is the answer in silent mode.</summary>
    int Ask(string title, string text, string[] buttons, int silentChoice);
}

internal sealed class SilentInteraction : IInteraction
{
    private readonly SetupLog _log;

    public SilentInteraction(SetupLog log) { _log = log; }

    public bool IsSilent => true;

    public int Ask(string title, string text, string[] buttons, int silentChoice)
    {
        _log.Write(LogLevel.Detail, $"{title}: {text.Replace("\n", " ")} -> {buttons[silentChoice]}");
        return silentChoice;
    }
}

/// <summary>Every change to disk or registry goes through <see cref="Do"/>: in a dry run it is only logged as the plan.</summary>
internal sealed class Changes
{
    public readonly bool DryRun;
    private readonly SetupLog _log;

    public Changes(bool dryRun, SetupLog log)
    {
        DryRun = dryRun;
        _log = log;
    }

    public void Do(string what, Action action)
    {
        if (DryRun)
        {
            _log.Plan(what);
            return;
        }
        action();
        _log.Write(LogLevel.Detail, what);
    }
}

internal static class StepResults
{
    public static int ExitCode(IEnumerable<StepResult> results) =>
        results.Any(r => r.Status is StepStatus.Skipped or StepStatus.Warning or StepStatus.Failed) ? ExitCodes.Partial : ExitCodes.Ok;
}
