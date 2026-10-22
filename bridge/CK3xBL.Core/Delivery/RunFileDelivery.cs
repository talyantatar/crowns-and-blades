// Dev-mode IN channel (PROTOCOL §2.3): writes run/ck3xbl_apply.txt, opens CK3's console with the key
// below Esc (scan 0x29), clears the line (on a Russian layout that key also types "ё"), types "run ck3xbl_apply.txt" as Unicode text, presses Enter, waits for RES|END, closes the console and
// overwrites the file with a no-op. Console use disables achievements, so this channel is never used in normal mode. A mod from
// before the battle clock refuses payload version 4 (PROTOCOL §14.3): the file is run once more as version 3, and the session stays
// on version 3.
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Ck3;
using CK3xBL.Core.Diagnostics;
using CK3xBL.Core.Json;
using CK3xBL.Core.Logs;

namespace CK3xBL.Core.Delivery;

public sealed class RunFileOptions
{
    /// <summary>CK3 user folder (the run/ subfolder is created if missing).</summary>
    public string Ck3UserDir = "";
    public TimeSpan KeyDelay = TimeSpan.FromMilliseconds(12);
    /// <summary>Time for the console widget to open and take text focus after the toggle key.</summary>
    public TimeSpan ConsoleOpenDelay = TimeSpan.FromMilliseconds(150);
    public TimeSpan AckTimeout = TimeSpan.FromSeconds(4);
    public TimeSpan HeldKeysTimeout = TimeSpan.FromSeconds(1.5);
    public bool BlockPhysicalInput = true;
    public int TextChunk = 16;
    /// <summary>Backspaces after the console opens: on a Russian (or any non-US) layout the console key also types its letter (ё)
    /// into the input line, and "ёrun ..." is an unknown command. Harmless on an empty line.</summary>
    public int ClearBackspaces = 3;
}

public sealed class RunFileDelivery : ICk3Delivery
{
    private readonly ICk3Locator _ck3;
    private readonly IKeyInjector _injector;
    private readonly IInputGuardSource? _guards;
    private readonly ICk3EventSource _events;
    private readonly MaaIndexTable? _maaIndex;
    private readonly RunFileOptions _options;
    private readonly IJournal _journal;
    private volatile int _maxPayloadVersion = KeypadLayout.SteerPayloadVersion;

    public RunFileDelivery(ICk3Locator ck3, IKeyInjector injector, IInputGuardSource? guards, ICk3EventSource events,
        MaaIndexTable? maaIndex, RunFileOptions options, IJournal journal)
    {
        _ck3 = ck3;
        _injector = injector;
        _guards = guards;
        _events = events;
        _maaIndex = maaIndex;
        _options = options;
        _journal = journal;
    }

    public string RunFilePath => Path.Combine(_options.Ck3UserDir, "run", RunFileRenderer.FileName);

    /// <summary>4, or 3 for the rest of the bridge session once a mod from before the battle clock refused a version 4 payload
    /// (PROTOCOL §14.3).</summary>
    public int MaxPayloadVersion => _maxPayloadVersion;

    public async Task<DeliveryStatus> DeliverAsync(Ck3Outcome outcome, CancellationToken ct)
    {
        if (outcome.PayloadVersion > _maxPayloadVersion)
        {
            _journal.Info("runfile", $"battle {outcome.Bid}: CK3's mod takes payload version {_maxPayloadVersion} only; sending that");
            outcome.PayloadVersion = _maxPayloadVersion;
        }
        if (_ck3.Find() is not { } ck3) { _journal.Warn("runfile", "CK3 is not running"); return DeliveryStatus.Failed; }
        try
        {
            while (true)
            {
                var warnings = new List<string>();
                var payload = KeypadPayload.Encode(outcome, _maaIndex, warnings);
                foreach (var w in warnings) _journal.Warn("runfile", w);
                AtomicFile.WriteAllText(RunFilePath, RunFileRenderer.Render(payload), withBom: true);

                using var applied = _events.Expect<ResultTrace>(r => r.Bid == outcome.Bid && r.Stage == "END");
                using var rejected = _events.Expect<InputAck>(a => a.Bid == outcome.Bid && !a.Ok);

                if (!await WaitForReleasedKeysAsync(ct).ConfigureAwait(false))
                {
                    _journal.Warn("runfile", "keys or mouse buttons stayed held; not typing");
                    return DeliveryStatus.Interrupted;
                }
                if (await TypeAsync(ck3, $"run {RunFileRenderer.FileName}", ct).ConfigureAwait(false) is { } stopped) return stopped;

                // Whichever comes first: RES|END (applied) or IN|BAD (the mod rejected the checksum, the bid or the version).
                var appliedTask = applied.WaitAsync(_options.AckTimeout, ct);
                var rejectedTask = rejected.WaitAsync(_options.AckTimeout, ct);
                await Task.WhenAny(appliedTask, rejectedTask).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                bool ok = appliedTask.IsCompletedSuccessfully && appliedTask.Result is not null;
                bool oldMod = !ok && rejectedTask.IsCompletedSuccessfully && rejectedTask.Result is { } bad && payload.RefusedByOldMod(bad);
                _journal.Write(ok ? JournalLevel.Info : JournalLevel.Warn, "runfile",
                    ok ? $"battle {outcome.Bid}: RES|END (payload v{payload.Version})"
                    : oldMod ? $"battle {outcome.Bid}: CK3's mod does not accept payload version 4 (IN|BAD without ck3xbl_in_maxver, checksum " +
                               "intact); running it again as version 3, the result is applied at once (no battle clock until the bridge restarts)"
                    : $"battle {outcome.Bid}: no RES|END (rejected or timed out)");

                // Close the console again; CK3 must still be in front (never type into another window).
                if (_injector.ForegroundWindow == ck3.Hwnd) await TapAsync(ScanCodes.Grave).ConfigureAwait(false);
                if (!oldMod) return ok ? DeliveryStatus.Delivered : DeliveryStatus.Failed;
                _maxPayloadVersion = KeypadLayout.PayloadVersion;
                outcome.PayloadVersion = KeypadLayout.PayloadVersion;
            }
        }
        finally
        {
            try { AtomicFile.WriteAllText(RunFilePath, RunFileRenderer.NoOp, withBom: true); }
            catch (Exception e) { _journal.Warn("runfile", $"could not reset {RunFilePath}: {e.Message}"); }
        }
    }

    /// <summary>Console toggle, text, Enter; null when Enter was pressed. Stops as soon as CK3 is not the foreground window:
    /// Interrupted when that happens before the console was opened (safe to try again later), Failed afterwards (the console may be
    /// open with part of the command in it, so typing again could send the text to the game as hotkeys).</summary>
    private async Task<DeliveryStatus?> TypeAsync(Ck3Window ck3, string command, CancellationToken ct)
    {
        using var guard = _options.BlockPhysicalInput ? _guards?.Acquire() : null;
        if (guard is not null) guard.Blocking = true;
        try
        {
            if (_injector.ForegroundWindow != ck3.Hwnd) return Stop(DeliveryStatus.Interrupted, "CK3 is not in front; console not opened");
            await TapAsync(ScanCodes.Grave).ConfigureAwait(false);
            await Task.Delay(_options.ConsoleOpenDelay, ct).ConfigureAwait(false);
            for (int i = 0; i < _options.ClearBackspaces; i++)
            {
                if (_injector.ForegroundWindow != ck3.Hwnd) return Stop(DeliveryStatus.Failed, "focus moved while clearing the console line");
                await TapAsync(ScanCodes.Backspace).ConfigureAwait(false);
            }

            for (int i = 0; i < command.Length; i += _options.TextChunk)
            {
                if (_injector.ForegroundWindow != ck3.Hwnd) return Stop(DeliveryStatus.Failed, "focus moved while typing");
                var part = command.Substring(i, Math.Min(_options.TextChunk, command.Length - i));
                var strokes = part.SelectMany(c => new[] { KeyStroke.CharDown(c), KeyStroke.CharUp(c) }).ToList();
                if (!_injector.Send(strokes)) return Stop(DeliveryStatus.Failed, "SendInput rejected the text");
                await Task.Delay(_options.KeyDelay, CancellationToken.None).ConfigureAwait(false);
            }
            if (_injector.ForegroundWindow != ck3.Hwnd) return Stop(DeliveryStatus.Failed, "focus moved before Enter");
            await TapAsync(ScanCodes.Enter).ConfigureAwait(false);
            return null;
        }
        finally
        {
            if (guard is not null) guard.Blocking = false;
        }
    }

    private async Task TapAsync(ushort scan)
    {
        _injector.Send(new[] { KeyStroke.Down(scan) });
        await Task.Delay(_options.KeyDelay, CancellationToken.None).ConfigureAwait(false);
        _injector.Send(new[] { KeyStroke.Release(scan) });
        await Task.Delay(_options.KeyDelay, CancellationToken.None).ConfigureAwait(false);
    }

    private DeliveryStatus Stop(DeliveryStatus status, string why)
    {
        _journal.Warn("runfile", why);
        return status;
    }

    private async Task<bool> WaitForReleasedKeysAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _options.HeldKeysTimeout;
        while (_injector.AnyModifierOrButtonHeld())
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
        return true;
    }
}
