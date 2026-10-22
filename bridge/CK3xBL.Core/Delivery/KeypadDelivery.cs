// Normal-mode IN channel (PROTOCOL §2.2): types the register payload into CK3's event 0002 keypad as Ctrl+Shift+Alt chords and
// waits for the mod's IN|OK. Achievement-safe: no console, only vanilla modder hotkeys.
//
// Safety rules:
//  * CK3 must be the foreground window before every chunk AND every key; if focus moves, stop (never retarget) and release keys.
//  * Never send letters, F4 or F10. Digits and F-keys go by scan code, so the keyboard layout does not matter.
//  * Holding Ctrl+Shift+Alt overlaps Windows' input-language hotkeys: CK3's thread layout is recorded before and restored after.
//  * Physical keys are swallowed while a chunk is being sent (short windows), and injection starts only when no modifier or mouse
//    button is held.
//  * The payload is typed only after a probe (F3, F2) brought back an IN record: without the battle window's keypad the chords
//    reach CK3's own hotkeys and open windows and press menu items.
//  * No IN|OK within the timeout (or IN|BAD): reset (F3) and send everything once more, slower each time (a CK3 that is still paging
//    its memory back in after a battle misses short key presses: live 2026-10-19, b288 lost 1 and 3 of 23 registers).
//  * A mod from before the battle clock refuses payload version 4 (PROTOCOL §14.3): the outcome goes again as version 3, and the
//    session stays on version 3.
using System.Diagnostics;
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Ck3;
using CK3xBL.Core.Diagnostics;
using CK3xBL.Core.Logs;

namespace CK3xBL.Core.Delivery;

public sealed class KeypadOptions
{
    public TimeSpan KeyDelay = TimeSpan.FromMilliseconds(12);
    public int ChunkSize = 12;
    public bool HoldModifiersPerChunk = true;
    public TimeSpan AckTimeout = TimeSpan.FromSeconds(4);
    public TimeSpan HeldKeysTimeout = TimeSpan.FromSeconds(1.5);
    public bool BlockPhysicalInput = true;
    public int Attempts = 3;
    /// <summary>Key delay factor of each attempt after the first (2nd ×2, 3rd ×4, ...): slower keys, but not so slow that the input
    /// hooks stay on for minutes (the orchestrator first lets a trimmed CK3 page its memory back in).</summary>
    public int SlowdownPerAttempt = 2;
    /// <summary>Most keys the award slots may add to the sequence (PROTOCOL §13.5); awards are dropped from the last one beyond it.
    /// 0 = no awards over the keypad.</summary>
    public int AwardKeyBudget = 90;
    /// <summary>Before the payload, F3 + F2 (reset, commit) must bring back an IN record: proof that the battle window's keypad is
    /// open. Without it every chord falls through to CK3's own hotkeys (F1-F5 open the character, realm, military and court
    /// windows, digits set the speed and press menu items: live 2026-10-20, b339).</summary>
    public bool Probe = true;
    /// <summary>Probes before giving up (the second one slower, for a CK3 still paging its memory back in).</summary>
    public int ProbeAttempts = 2;
}

public enum KeySendResult { Completed, FocusLost, InjectionFailed, KeysHeld }

public sealed class KeypadDelivery : ICk3Delivery
{
    private readonly ICk3Locator _ck3;
    private readonly IKeyInjector _injector;
    private readonly IInputGuardSource? _guards;
    private readonly ICk3EventSource _events;
    private readonly MaaIndexTable? _maaIndex;
    private readonly KeypadOptions _options;
    private readonly IJournal _journal;
    /// <summary>Stopwatch timestamp of the first key of the last SendAsync (deliveries run one at a time).</summary>
    private long _firstKeyAt;
    private volatile int _maxPayloadVersion = KeypadLayout.SteerPayloadVersion;
    /// <summary>The key delay of the attempt being sent.</summary>
    private TimeSpan _delay;

    public KeypadDelivery(ICk3Locator ck3, IKeyInjector injector, IInputGuardSource? guards, ICk3EventSource events,
        MaaIndexTable? maaIndex, KeypadOptions options, IJournal journal)
    {
        _ck3 = ck3;
        _injector = injector;
        _guards = guards;
        _events = events;
        _maaIndex = maaIndex;
        _options = options;
        _journal = journal;
    }

    /// <summary>4, or 3 for the rest of the bridge session once a mod from before the battle clock refused a version 4 payload
    /// (PROTOCOL §14.3).</summary>
    public int MaxPayloadVersion => _maxPayloadVersion;

    public async Task<DeliveryStatus> DeliverAsync(Ck3Outcome outcome, CancellationToken ct)
    {
        if (outcome.PayloadVersion > _maxPayloadVersion)
        {
            _journal.Info("keypad", $"battle {outcome.Bid}: CK3's mod takes payload version {_maxPayloadVersion} only; sending that");
            outcome.PayloadVersion = _maxPayloadVersion;
        }
        var (payload, keys) = Encode(outcome);

        if (_options.Probe)
        {
            var probe = await ProbeAsync(ct).ConfigureAwait(false);
            if (probe != DeliveryStatus.Delivered) return probe;
        }

        int attempt = 1;
        while (attempt <= _options.Attempts)
        {
            if (_ck3.Find() is not { } ck3) { _journal.Warn("keypad", "CK3 is not running"); return DeliveryStatus.Failed; }
            using var ack = _events.Expect<InputAck>(a => a.Bid == outcome.Bid);
            _delay = attempt == 1 ? _options.KeyDelay : _options.KeyDelay * (Math.Max(1, _options.SlowdownPerAttempt) * (attempt - 1));
            if (attempt > 1) _journal.Info("keypad", $"attempt {attempt}: {_delay.TotalMilliseconds:0} ms per key");
            var sent = await SendAsync(keys, ck3, ct).ConfigureAwait(false);
            if (sent != KeySendResult.Completed)
            {
                _journal.Warn("keypad", $"attempt {attempt}: stopped ({sent})");
                // Nothing is applied without the final F2 commit, and the next attempt starts with a reset (F3).
                return sent == KeySendResult.InjectionFailed ? DeliveryStatus.Failed : DeliveryStatus.Interrupted;
            }
            var result = await ack.WaitAsync(_options.AckTimeout, ct).ConfigureAwait(false);
            if (result is { Ok: true })
            {
                // Keys and time from the first key to IN|OK, to calibrate the cost per key from real battles.
                var elapsed = Stopwatch.GetElapsedTime(_firstKeyAt);
                _journal.Info("keypad", $"battle {outcome.Bid}: {keys.Count} keys in {elapsed.TotalSeconds:0.0} s, IN|OK (count {result.Count}, sum {result.Sum})");
                return DeliveryStatus.Delivered;
            }
            if (result is not null && payload.RefusedByOldMod(result))
            {
                // The keys arrived intact but the mod predates the battle clock: the same outcome once more as version 3, which this
                // session keeps from now on. The re-send is not an attempt of its own (the version 3 payload gets the usual retries).
                _journal.Warn("keypad", $"battle {outcome.Bid}: CK3's mod does not accept payload version 4 (IN|BAD without ck3xbl_in_maxver, " +
                                        "checksum intact); sending it again as version 3, the result is applied at once (no battle clock until the bridge restarts)");
                _maxPayloadVersion = KeypadLayout.PayloadVersion;
                outcome.PayloadVersion = KeypadLayout.PayloadVersion;
                (payload, keys) = Encode(outcome);
                continue;
            }
            _journal.Warn("keypad", result is null
                ? $"attempt {attempt}: no IN record within {_options.AckTimeout.TotalSeconds:0.#} s"
                : $"attempt {attempt}: IN|BAD (mod checksum {result.Sum}, ours {payload.Registers[KeypadLayout.Checksum]}, " +
                  $"count {result.Count}, ours {payload.NonZero().Count(x => x.Index < KeypadLayout.Checksum)})");
            attempt++;
        }
        return DeliveryStatus.Failed;
    }

    private static readonly KeypadKey[] ProbeKeys = { KeypadKey.Reset, KeypadKey.Commit };

    /// <summary>Reset + commit: the keypad answers with an IN record (IN|BAD for bid 0), CK3's main interface with nothing. Delivered
    /// = the keypad is open.</summary>
    private async Task<DeliveryStatus> ProbeAsync(CancellationToken ct)
    {
        for (int probe = 1; probe <= Math.Max(1, _options.ProbeAttempts); probe++)
        {
            if (_ck3.Find() is not { } ck3) { _journal.Warn("keypad", "CK3 is not running"); return DeliveryStatus.Failed; }
            using var ack = _events.Expect<InputAck>(_ => true);
            _delay = probe == 1 ? _options.KeyDelay : _options.KeyDelay * Math.Max(1, _options.SlowdownPerAttempt);
            var sent = await SendAsync(ProbeKeys, ck3, ct).ConfigureAwait(false);
            if (sent != KeySendResult.Completed)
            {
                _journal.Warn("keypad", $"probe {probe}: stopped ({sent})");
                return sent == KeySendResult.InjectionFailed ? DeliveryStatus.Failed : DeliveryStatus.Interrupted;
            }
            if (await ack.WaitAsync(_options.AckTimeout, ct).ConfigureAwait(false) is not null) return DeliveryStatus.Delivered;
            _journal.Warn("keypad", $"probe {probe}: the keypad did not answer within {_options.AckTimeout.TotalSeconds:0.#} s");
        }
        _journal.Warn("keypad", "the battle window's keypad is not open (or covered); the result is not typed into CK3's interface");
        return DeliveryStatus.KeypadClosed;
    }

    private (KeypadPayload Payload, IReadOnlyList<KeypadKey> Keys) Encode(Ck3Outcome outcome)
    {
        var warnings = new List<string>();
        var payload = KeypadPayload.Encode(outcome, _maaIndex, warnings, Math.Max(0, _options.AwardKeyBudget));
        foreach (var w in warnings) _journal.Warn("keypad", w);
        var keys = payload.ToKeySequence();
        _journal.Info("keypad", $"battle {outcome.Bid}: payload v{payload.Version}, {keys.Count} keys, checksum {payload.Registers[KeypadLayout.Checksum]}");
        return (payload, keys);
    }

    /// <summary>Sends the keys in chunks under the safety rules. Public for diagnostics tools and tests.</summary>
    public async Task<KeySendResult> SendAsync(IReadOnlyList<KeypadKey> keys, Ck3Window ck3, CancellationToken ct)
    {
        if (_delay <= TimeSpan.Zero) _delay = _options.KeyDelay;
        if (!await WaitForReleasedKeysAsync(ct).ConfigureAwait(false)) return KeySendResult.KeysHeld;

        using var guard = _options.BlockPhysicalInput ? _guards?.Acquire() : null;
        nint layoutBefore = _injector.GetKeyboardLayout(ck3.ThreadId);
        _firstKeyAt = Stopwatch.GetTimestamp();
        try
        {
            for (int start = 0; start < keys.Count; start += _options.ChunkSize)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = keys.Skip(start).Take(_options.ChunkSize).ToList();
                var r = await SendChunkAsync(chunk, ck3, guard).ConfigureAwait(false);
                if (r != KeySendResult.Completed) return r;
                await Task.Delay(_delay, CancellationToken.None).ConfigureAwait(false);
            }
            return KeySendResult.Completed;
        }
        finally
        {
            nint layoutAfter = _injector.GetKeyboardLayout(ck3.ThreadId);
            if (layoutAfter != layoutBefore && layoutBefore != 0)
            {
                _journal.Warn("keypad", $"CK3 keyboard layout changed 0x{layoutBefore:X} -> 0x{layoutAfter:X}; restoring");
                _injector.RequestKeyboardLayout(ck3.Hwnd, layoutBefore);
            }
        }
    }

    // A chunk never leaves a key down: every press is followed by its release, and held modifiers are released in finally even when
    // focus moved (a stuck Ctrl+Shift+Alt would be far worse than a stray key-up in another window). Delays inside a chunk are not
    // cancellable for the same reason; cancellation is honoured between chunks.
    private async Task<KeySendResult> SendChunkAsync(List<KeypadKey> chunk, Ck3Window ck3, IInputGuard? guard)
    {
        if (_injector.ForegroundWindow != ck3.Hwnd) return KeySendResult.FocusLost;
        bool modifiersDown = false;
        if (guard is not null) guard.Blocking = true;
        try
        {
            foreach (var key in chunk)
            {
                if (_injector.ForegroundWindow != ck3.Hwnd) return KeySendResult.FocusLost;
                if (!modifiersDown)
                {
                    if (!_injector.Send(ScanCodes.KeypadModifiers.Select(KeyStroke.Down).ToList())) return KeySendResult.InjectionFailed;
                    modifiersDown = true;
                    await Delay().ConfigureAwait(false);
                }
                ushort scan = ScanCodes.ForKeypad(key);
                if (!_injector.Send(new[] { KeyStroke.Down(scan) })) return KeySendResult.InjectionFailed;
                await Delay().ConfigureAwait(false);
                _injector.Send(new[] { KeyStroke.Release(scan) });
                await Delay().ConfigureAwait(false);
                if (!_options.HoldModifiersPerChunk)
                {
                    ReleaseModifiers();
                    modifiersDown = false;
                    await Delay().ConfigureAwait(false);
                }
            }
            return KeySendResult.Completed;
        }
        finally
        {
            if (modifiersDown) ReleaseModifiers();
            if (guard is not null) guard.Blocking = false;
        }
    }

    private void ReleaseModifiers() =>
        _injector.Send(ScanCodes.KeypadModifiers.Reverse().Select(KeyStroke.Release).ToList());

    private Task Delay() => Task.Delay(_delay, CancellationToken.None);

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
