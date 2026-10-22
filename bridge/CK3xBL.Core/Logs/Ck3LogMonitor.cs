// The OUT-channel pipeline: tailer -> record parser -> event decoder -> subscribers (+ awaitable expectations).
using CK3xBL.Core.Diagnostics;

namespace CK3xBL.Core.Logs;

/// <summary>Lets a caller wait for a CK3 event that will be caused by something it is about to do (keypad acks, run-file acks).
/// Register the expectation BEFORE acting, so an event that arrives quickly is never missed.</summary>
public interface ICk3EventSource
{
    Ck3Expectation<T> Expect<T>(Func<T, bool> match) where T : Ck3Event;
}

internal interface IPendingExpectation
{
    void Offer(Ck3Event e);
}

public sealed class Ck3Expectation<T> : IPendingExpectation, IDisposable where T : Ck3Event
{
    private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<T, bool> _match;
    private readonly Action<Ck3Expectation<T>> _unregister;

    internal Ck3Expectation(Func<T, bool> match, Action<Ck3Expectation<T>> unregister)
    {
        _match = match;
        _unregister = unregister;
    }

    void IPendingExpectation.Offer(Ck3Event e)
    {
        if (e is T t && _match(t)) _tcs.TrySetResult(t);
    }

    /// <summary>The matching event, or null on timeout. Throws OperationCanceledException when ct is cancelled.</summary>
    public async Task<T?> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try { return await _tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    public void Dispose() => _unregister(this);
}

/// <summary>Dispatches events to subscribers and pending expectations. Thread-safe.</summary>
public class Ck3EventHub : ICk3EventSource
{
    private readonly object _gate = new();
    private readonly List<IPendingExpectation> _expectations = new();

    public event Action<Ck3Event>? EventReceived;

    public Ck3Expectation<T> Expect<T>(Func<T, bool> match) where T : Ck3Event
    {
        var e = new Ck3Expectation<T>(match, x => { lock (_gate) _expectations.Remove(x); });
        lock (_gate) _expectations.Add(e);
        return e;
    }

    public void Publish(Ck3Event e)
    {
        List<IPendingExpectation> pending;
        lock (_gate) pending = _expectations.ToList();
        foreach (var x in pending) x.Offer(e);
        EventReceived?.Invoke(e);
    }
}

/// <summary>Follows CK3's game.log (or debug.log) and publishes decoded events.</summary>
public sealed class Ck3LogMonitor : Ck3EventHub, IDisposable
{
    private readonly LogTailer _tailer;
    private readonly Ck3RecordParser _parser;
    private readonly Ck3EventDecoder _decoder;
    private readonly IJournal _journal;
    private readonly object _feedGate = new();

    public Ck3LogMonitor(string logPath, IJournal journal, LogTailerOptions? options = null)
    {
        _journal = journal;
        _decoder = new Ck3EventDecoder(Publish, w => journal.Warn("ck3log", w));
        _parser = new Ck3RecordParser(r =>
        {
            journal.Debug("ck3log", r.ToString());
            _decoder.Accept(r);
        });
        _tailer = new LogTailer(logPath, options);
        _tailer.LinesRead += lines => { lock (_feedGate) _parser.Feed(lines); };
        _tailer.WentIdle += () =>
        {
            lock (_feedGate)
            {
                _parser.Flush();
                _decoder.Flush();
            }
        };
        _tailer.Restarted += () =>
        {
            lock (_feedGate)
            {
                journal.Info("ck3log", $"{Path.GetFileName(logPath)} was recreated (CK3 restarted)");
                _parser.Reset();
                _decoder.LogRestarted();
            }
        };
        _tailer.Faulted += e => journal.Error("ck3log", "event handler failed", e);
    }

    public string LogPath => _tailer.Path;
    public string SessionKey => _decoder.SessionKey;

    public void Start()
    {
        _journal.Info("ck3log", $"following {_tailer.Path} (session {_decoder.SessionKey})");
        _tailer.Start();
    }

    public void Dispose() => _tailer.Dispose();
}
