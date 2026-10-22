// Follows a CK3 log file that another process keeps appending to (PROTOCOL §1.1).
//  * The file is opened per poll with FileShare.ReadWrite | Delete and closed again: CK3 recreates its logs at every launch, and a
//    handle held open across polls would leave the old file "pending delete" and could block CK3 from recreating it.
//  * Restart detection: the file shrank below our offset, or its first bytes changed (a relaunched CK3 rewrote it and already grew
//    past our offset between two polls). Either way the offset resets to 0 and Restarted fires before any new line.
//  * Lines are split on '\n' at the BYTE level and decoded as UTF-8 only when complete, so a multi-byte character or a line that is
//    still being written is never cut; the incomplete tail waits for the next poll.
using System.Text;

namespace CK3xBL.Core.Logs;

public sealed class LogTailerOptions
{
    public TimeSpan PollInterval = TimeSpan.FromMilliseconds(75);
    /// <summary>Skip what is already in the file at start (stale records from before the bridge ran).</summary>
    public bool StartAtEnd = true;
    /// <summary>Upper bound for one read, so a huge backlog is processed in slices.</summary>
    public int MaxReadBytes = 4 << 20;
}

/// <summary>Synchronous, single-threaded follower: call <see cref="Poll"/> repeatedly (or use <see cref="LogTailer"/>).</summary>
public sealed class LogFollower
{
    private const int SignatureLength = 64;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private readonly string _path;
    private readonly int _maxReadBytes;
    private bool _startAtEnd;
    private long _offset;
    private byte[] _signature = Array.Empty<byte>();
    private readonly MemoryStream _partial = new();
    private bool _skipToNextLine;                     // started at EOF inside a line: drop that line's remainder

    public LogFollower(string path, bool startAtEnd, int maxReadBytes = 4 << 20)
    {
        _path = path;
        _startAtEnd = startAtEnd;
        _maxReadBytes = maxReadBytes;
    }

    public string Path => _path;

    /// <summary>After a <see cref="Poll"/>: everything in the file has been read (also true when the file does not exist). False while
    /// a backlog larger than one read is still being sliced, or after a transient open failure.</summary>
    public bool AtEnd { get; private set; }

    /// <summary>Reads everything appended since the last poll. Returns complete lines (without line terminators) and whether the
    /// file was found to have been restarted (in which case the returned lines are from the new file only).</summary>
    public PollResult Poll()
    {
        FileStream fs;
        try
        {
            fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16,
                FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { AtEnd = true; return PollResult.Empty; }
        catch (DirectoryNotFoundException) { AtEnd = true; return PollResult.Empty; }
        catch (IOException) { AtEnd = false; return PollResult.Empty; }           // transient: being recreated right now
        catch (UnauthorizedAccessException) { AtEnd = false; return PollResult.Empty; }

        using (fs)
        {
            long length = fs.Length;
            bool restarted = false;

            if (_startAtEnd)
            {
                // First successful open: skip existing content but remember its head, so a later CK3 relaunch is still detected.
                _startAtEnd = false;
                _offset = length;
                _signature = ReadHead(fs, Math.Min(SignatureLength, length));
                // If the file currently ends mid-line, the rest of that line is garbage for us; skip it at the next newline.
                _skipToNextLine = length > 0 && !EndsWithNewline(fs, length);
                AtEnd = true;
                return PollResult.Empty;
            }

            if (length < _offset || !SignatureMatches(fs, length))
            {
                restarted = true;
                _offset = 0;
                _signature = Array.Empty<byte>();
                _partial.SetLength(0);
                _skipToNextLine = false;
            }

            if (_signature.Length < SignatureLength && length > _signature.Length)
                _signature = ReadHead(fs, Math.Min(SignatureLength, length));

            if (length == _offset)
            {
                AtEnd = true;
                return restarted ? new PollResult(Array.Empty<string>(), true) : PollResult.Empty;
            }

            long toRead = Math.Min(length - _offset, _maxReadBytes);
            var buffer = new byte[toRead];
            fs.Seek(_offset, SeekOrigin.Begin);
            int read = ReadFully(fs, buffer);
            _offset += read;
            AtEnd = _offset >= length;
            return new PollResult(SplitLines(buffer.AsSpan(0, read)), restarted);
        }
    }

    private List<string> SplitLines(ReadOnlySpan<byte> data)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != (byte)'\n') continue;
            var segment = data[start..i];
            byte[] lineBytes;
            if (_partial.Length > 0)
            {
                _partial.Write(segment);
                lineBytes = _partial.ToArray();
                _partial.SetLength(0);
            }
            else lineBytes = segment.ToArray();
            start = i + 1;

            if (_skipToNextLine) { _skipToNextLine = false; continue; }
            int len = lineBytes.Length;
            if (len > 0 && lineBytes[len - 1] == (byte)'\r') len--;
            lines.Add(Utf8.GetString(lineBytes, 0, len));
        }
        if (start < data.Length) _partial.Write(data[start..]);
        return lines;
    }

    private bool SignatureMatches(FileStream fs, long length)
    {
        if (_signature.Length == 0) return true;
        if (length < _signature.Length) return false;
        var head = ReadHead(fs, _signature.Length);
        return head.AsSpan().SequenceEqual(_signature);
    }

    private static byte[] ReadHead(FileStream fs, long count)
    {
        var head = new byte[count];
        fs.Seek(0, SeekOrigin.Begin);
        int n = ReadFully(fs, head);
        return n == head.Length ? head : head[..n];
    }

    private static bool EndsWithNewline(FileStream fs, long length)
    {
        fs.Seek(length - 1, SeekOrigin.Begin);
        return fs.ReadByte() == '\n';
    }

    private static int ReadFully(Stream s, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = s.Read(buffer, total, buffer.Length - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    public readonly record struct PollResult(IReadOnlyList<string> Lines, bool Restarted)
    {
        public static readonly PollResult Empty = new(Array.Empty<string>(), false);
    }
}

/// <summary>Background loop around <see cref="LogFollower"/>. Callbacks run on the tailer's own thread, in file order.</summary>
public sealed class LogTailer : IDisposable
{
    private readonly LogFollower _follower;
    private readonly LogTailerOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Complete lines from one poll, in order.</summary>
    public event Action<IReadOnlyList<string>>? LinesRead;
    /// <summary>The first poll after data that found nothing new (callers use it to flush records whose dump has ended).</summary>
    public event Action? WentIdle;
    /// <summary>The file was recreated (CK3 relaunched). Fires before the new file's first lines.</summary>
    public event Action? Restarted;
    public event Action<Exception>? Faulted;

    public LogTailer(string path, LogTailerOptions? options = null)
    {
        _options = options ?? new LogTailerOptions();
        _follower = new LogFollower(path, _options.StartAtEnd, _options.MaxReadBytes);
    }

    public string Path => _follower.Path;

    public void Start() => _loop ??= Task.Factory.StartNew(Run, TaskCreationOptions.LongRunning).Unwrap();

    private async Task Run()
    {
        bool hadData = false;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = _follower.Poll();
                if (result.Restarted) Restarted?.Invoke();
                if (result.Lines.Count > 0)
                {
                    hadData = true;
                    LinesRead?.Invoke(result.Lines);
                }
                else if (hadData)
                {
                    hadData = false;
                    WentIdle?.Invoke();
                }
            }
            catch (Exception e)
            {
                // A consumer bug must not kill the tailer; report it and keep following.
                Faulted?.Invoke(e);
            }
            try { await Task.Delay(_options.PollInterval, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
