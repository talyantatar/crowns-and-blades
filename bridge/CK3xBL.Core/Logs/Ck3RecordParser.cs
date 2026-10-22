// Turns CK3 log lines into Ck3Records (PROTOCOL §1.1).
//
// The log is a sequence of ENTRIES: a timestamped line "[HH:MM:SS][L][source]: message" plus any following lines without a
// timestamp (continuations). A record is an entry whose message contains "CK3XBL|". Record types that carry values are followed by
// an info_log_scopes DUMP entry: the message is the current scope print and the continuations are "Root: ...", a blank line,
// "Saved event targets:" and "key: value" lines. An entry is only complete when the next timestamped line arrives, so the last
// entry of a burst is completed by Flush() (the tailer calls it when the file goes idle).
//
// Tolerance: a dump is attached to the pending record even if a few unrelated engine lines got in between (other threads write to
// the same log); a dump without any ck3xbl_ key belongs to someone else and is skipped; a record whose dump never comes is
// emitted without one; unknown record types are emitted as-is and ignored downstream.
using System.Globalization;
using System.Text.RegularExpressions;

namespace CK3xBL.Core.Logs;

public sealed class Ck3RecordParser
{
    /// <summary>Record types that are followed by a dump (PROTOCOL §1.2-1.3, §5.3, §14.5). Everything else (L, CX, AX, RX, unknown) is
    /// complete on its marker.</summary>
    private static readonly HashSet<string> TypesWithDump = new(StringComparer.Ordinal)
        { "B", "LV", "S", "A", "R", "C", "CHOICE", "J", "IN", "RES", "END", "ST" };

    /// <summary>How many unrelated entries may separate a marker from its dump before we give up waiting.</summary>
    private const int MaxEntriesBetweenMarkerAndDump = 8;

    private static readonly Regex Header = new(
        @"^\[(?<h>\d\d):(?<m>\d\d):(?<s>\d\d)\]\[(?<lvl>[DIWE])\]\[(?<src>[^\]]+)\]: (?<msg>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Marker = new(@"CK3XBL\|(?<rec>[^\r\n]*)$", RegexOptions.Compiled);
    private static readonly Regex MarkerContext = new(@"\((?<ctx>[^()]*)\): CK3XBL\|", RegexOptions.Compiled);
    private static readonly Regex KeyValue = new(@"^(?<key>[A-Za-z0-9_]+): ?(?<val>.*)$", RegexOptions.Compiled);

    private readonly Action<Ck3Record> _sink;
    private Entry? _current;
    private Ck3Record? _pending;          // marker waiting for its dump
    private int _pendingBudget;

    public Ck3RecordParser(Action<Ck3Record> sink) => _sink = sink;

    public void Feed(string line)
    {
        line = LogText.StripControl(line);
        var m = Header.Match(line);
        if (m.Success)
        {
            if (_current is not null) Process(_current);
            _current = new Entry(
                new TimeSpan(int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture),
                             int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture),
                             int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture)),
                m.Groups["msg"].Value);
        }
        else
        {
            _current?.Continuations.Add(line);    // lines before the first header (mid-file start) are dropped
        }
    }

    public void Feed(IEnumerable<string> lines)
    {
        foreach (var l in lines) Feed(l);
    }

    /// <summary>Completes the current entry and emits a pending record even if its dump has not arrived.</summary>
    public void Flush()
    {
        if (_current is not null)
        {
            Process(_current);
            _current = null;
        }
        EmitPending();
    }

    /// <summary>Drops all partial state (the log file was recreated).</summary>
    public void Reset()
    {
        _current = null;
        _pending = null;
        _pendingBudget = 0;
    }

    /// <summary>Convenience for tests and offline tools: parses a complete log text.</summary>
    public static List<Ck3Record> ParseAll(IEnumerable<string> lines)
    {
        var list = new List<Ck3Record>();
        var p = new Ck3RecordParser(list.Add);
        p.Feed(lines);
        p.Flush();
        return list;
    }

    private void Process(Entry entry)
    {
        var marker = Marker.Match(entry.Message);
        if (marker.Success)
        {
            EmitPending();
            var record = ParseMarker(marker.Groups["rec"].Value.TrimEnd(), entry);
            if (TypesWithDump.Contains(record.Type))
            {
                _pending = record;
                _pendingBudget = MaxEntriesBetweenMarkerAndDump;
            }
            else _sink(record);
            return;
        }

        if (_pending is null) return;
        if (TryParseDump(entry) is { } dump)
        {
            _pending.Dump = dump;
            EmitPending();
            return;
        }
        if (--_pendingBudget <= 0) EmitPending();
    }

    private void EmitPending()
    {
        if (_pending is null) return;
        var r = _pending;
        _pending = null;
        _sink(r);
    }

    private static Ck3Record ParseMarker(string rec, Entry entry)
    {
        var parts = rec.Split('|');
        var ctx = MarkerContext.Match(entry.Message);
        return new Ck3Record
        {
            Type = parts[0].Trim(),
            Args = parts.Skip(1).Select(a => a.Trim()).ToArray(),
            Raw = rec,
            Time = entry.Time,
            Context = ctx.Success ? ctx.Groups["ctx"].Value : null,
        };
    }

    private static ScopeDump? TryParseDump(Entry entry)
    {
        // A dump's first continuation line is "Root: ...".
        int rootIndex = entry.Continuations.FindIndex(l => l.Length > 0);
        if (rootIndex < 0 || !entry.Continuations[rootIndex].StartsWith("Root:", StringComparison.Ordinal)) return null;

        var dump = new ScopeDump
        {
            Current = entry.Message.Trim(),
            Root = entry.Continuations[rootIndex]["Root:".Length..].Trim(),
        };
        for (int i = rootIndex + 1; i < entry.Continuations.Count; i++)
        {
            var kv = KeyValue.Match(entry.Continuations[i]);
            if (!kv.Success) continue;
            var key = kv.Groups["key"].Value;
            if (key.StartsWith("ck3xbl_", StringComparison.Ordinal)) dump.Values[key] = kv.Groups["val"].Value.Trim();
        }
        return dump.Values.Count > 0 ? dump : null;
    }

    private sealed class Entry(TimeSpan time, string message)
    {
        public readonly TimeSpan Time = time;
        public readonly string Message = message;
        public readonly List<string> Continuations = new();
    }
}
