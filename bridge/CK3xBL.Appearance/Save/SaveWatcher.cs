// Notices fresh CK3 saves without touching the game:
//   continue_game.json changes -> its "title" names save games/<title>.ck3, which is complete before the json is written.
//   Fallback: Documents/last_save.ck3 (written ~40 ms after the json), then the newest *.ck3 in save games/.
// A candidate is used only when its size and mtime are unchanged for 250 ms and its header (and, for zipped saves,
// the zip directory) parses. FileSystemWatcher events are backed by a slow poll because they can be missed.
// A save is marked as handled only once the reader accepted it; a failed read is retried (1 s later, then with the
// poll) up to MaxReadAttempts times per file version, so a transient sharing violation does not leave the bridge on an
// old save until CK3 writes the next one.
using System.Text.Json;

namespace CK3xBL.Appearance.Save;

internal sealed class SaveWatcher : IDisposable
{
    private static readonly TimeSpan StableFor = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private const int MaxStabilityChecks = 120;     // 30 s for a slow write before waiting for the next event
    private const int MaxReadAttempts = 3;          // per file version; then wait for the next save

    private readonly string _docs;
    private readonly Func<string, bool> _onSaveReady;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private Task? _worker;
    private (string Path, long Length, DateTime Write) _last;     // handled: read, or given up on
    private (string Path, long Length, DateTime Write) _failed;   // last version whose read failed
    private int _failures;

    /// <param name="onSaveReady">Reads the save; returns false when it could not be read (the watcher retries).</param>
    public SaveWatcher(string ck3DocumentsDir, Func<string, bool> onSaveReady, Action<string> log)
    {
        _docs = ck3DocumentsDir; _onSaveReady = onSaveReady; _log = log;
    }

    public string SaveGamesDir => Path.Combine(_docs, "save games");

    public void Start()
    {
        TryWatch(_docs, "continue_game.json");
        TryWatch(_docs, "last_save.ck3");
        TryWatch(SaveGamesDir, "*.ck3");
        _worker = Task.Run(() => RunAsync(_cts.Token));
        Kick(); // initial read of whatever save is newest
    }

    private void TryWatch(string dir, string filter)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            var w = new FileSystemWatcher(dir, filter) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            w.Changed += (_, _) => Kick();
            w.Created += (_, _) => Kick();
            w.Renamed += (_, _) => Kick();
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
        catch (Exception e) when (e is ArgumentException or IOException or PlatformNotSupportedException)
        {
            _log($"cannot watch {dir}\\{filter}: {e.Message} (polling only)");
        }
    }

    public void Kick()
    {
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(Poll, ct).ConfigureAwait(false);
                await Task.Delay(Debounce, ct).ConfigureAwait(false);
                while (_signal.CurrentCount > 0) _signal.Wait(0, ct); // coalesce bursts
                await CheckAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                _log($"save watcher: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        string? path = ResolveTarget();
        if (path == null) return;
        var info = new FileInfo(path);
        if (!info.Exists || (_last.Path == path && _last.Length == info.Length && _last.Write == info.LastWriteTimeUtc)) return;

        for (int i = 0; i < MaxStabilityChecks; i++)
        {
            info.Refresh();
            long len = info.Length;
            DateTime write = info.LastWriteTimeUtc;
            await Task.Delay(StableFor, ct).ConfigureAwait(false);
            info.Refresh();
            if (info.Exists && info.Length == len && info.LastWriteTimeUtc == write && HeaderParses(path))
            {
                Read((path, len, write), ct);
                return;
            }
        }
        _log($"save {Path.GetFileName(path)} did not settle; waiting for the next change");
    }

    private void Read((string Path, long Length, DateTime Write) version, CancellationToken ct)
    {
        bool ok;
        try { ok = _onSaveReady(version.Path); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log($"save {Path.GetFileName(version.Path)}: reader failed: {e.GetType().Name}: {e.Message}");
            ok = false;
        }
        if (ok)
        {
            _last = version;
            _failures = 0;
            return;
        }
        _failures = _failed == version ? _failures + 1 : 1;
        _failed = version;
        if (_failures >= MaxReadAttempts)
        {
            _last = version;
            _log($"save {Path.GetFileName(version.Path)} unreadable after {_failures} attempts; waiting for the next save");
            return;
        }
        _log($"save {Path.GetFileName(version.Path)} not read (attempt {_failures}/{MaxReadAttempts}); retrying");
        _ = Task.Delay(RetryDelay, ct).ContinueWith(_ => Kick(), CancellationToken.None,
                                                    TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    /// <summary>The save CK3 just wrote: continue_game.json's title, else last_save.ck3, else the newest save.</summary>
    public string? ResolveTarget()
    {
        var candidates = new List<FileInfo>();
        string json = Path.Combine(_docs, "continue_game.json");
        if (File.Exists(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(Game.GameFiles.ReadShared(json));
                if (doc.RootElement.TryGetProperty("title", out var t) && t.GetString() is string title && title.Length > 0)
                {
                    var titled = new FileInfo(Path.Combine(SaveGamesDir, title + ".ck3"));
                    if (titled.Exists) candidates.Add(titled);
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                // half-written json: the next event retries
            }
        }
        var last = new FileInfo(Path.Combine(_docs, "last_save.ck3"));
        if (last.Exists) candidates.Add(last);
        if (candidates.Count == 0 && Directory.Exists(SaveGamesDir))
        {
            var newest = new DirectoryInfo(SaveGamesDir).EnumerateFiles("*.ck3").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest != null) candidates.Add(newest);
        }
        // The titled save is complete first; last_save.ck3 is preferred only when it is clearly newer.
        return candidates.OrderByDescending(f => f.LastWriteTimeUtc - (f.Name == "last_save.ck3" ? TimeSpan.FromSeconds(2) : TimeSpan.Zero))
                         .FirstOrDefault()?.FullName;
    }

    private static bool HeaderParses(string path)
    {
        try
        {
            using var fs = SaveContainer.OpenShared(path);
            var h = SaveContainer.TryReadHeader(fs);
            return h != null && SaveContainer.ZipLooksComplete(fs, h);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        try { _worker?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _cts.Dispose();
    }
}
