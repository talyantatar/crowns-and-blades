// Single-pass scanner over a binary gamestate (non-debug "normal mode" saves, header type 03). Field names are tokens
// without a public table, so sections and fields are recognised by structure, re-derived for every save:
//   CoA database  the container whose integer-keyed records hold a "pattern_*.dds" string
//   titles        the container whose records hold a title-key string (k_france, c_orangallu, ...); the CoA-id field is
//                 the integer field that is present in most titles, points into the CoA database and is distinct per title
//   stored DNA    any record with a string that is base64 of 416 bytes (104 genes x 4); the record key is the character id
// Houses, dynasties, cultures and family links are not derived from binary saves (knights then use ethnicity
// stand-ins); the meta (player/heir portraits and CoAs) is always read separately.
using System.Text;
using System.Text.RegularExpressions;
using CK3xBL.Appearance.Coa;
using CK3xBL.Appearance.Pdx;
using J = CK3xBL.Appearance.Pdx.JominiBinaryReader;

namespace CK3xBL.Appearance.Save;

internal sealed partial class BinaryGamestateScanner
{
    private const int DnaBytes = 416;

    private sealed class Record
    {
        public long Id;
        public int Container;
        public int Start, End;
        public bool HasPattern;
        public string? TitleKey;
        public string? Dna;
        public List<(ushort Token, long Value)>? Ints;   // collected only for title-like records
    }

    private sealed class Container
    {
        public required string Path;
        public int Start = -1, End = -1;
        public int Patterns, TitleKeys;
    }

    private struct Frame
    {
        public string? Name;        // token name ("#2799") or numeric key
        public int Container;       // index in _containers when this frame is a container of records, else -1
        public Record? Record;      // set when this frame is a record
        public int Start;
    }

    [GeneratedRegex("^[ekdcbhx]_[A-Za-z0-9_]+$")]
    private static partial Regex TitleKeyRegex();

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex UuidRegex();

    private readonly byte[] _d;
    private readonly List<Container> _containers = new();
    private readonly Dictionary<string, int> _containerByPath = new(StringComparer.Ordinal);
    private readonly List<Record> _records = new();
    private Frame[] _stack = new Frame[128];
    private int _depth;
    private string? _playthroughId;

    private BinaryGamestateScanner(byte[] data) { _d = data; }

    public static SaveIndex Scan(byte[] gamestate, NamedColors colors, CoaTokenMap tokens, Action<string> log)
    {
        var s = new BinaryGamestateScanner(gamestate);
        s.Run();
        return s.BuildIndex(colors, tokens, log);
    }

    private void Run()
    {
        var r = new J(_d);
        _stack[0] = new Frame { Container = -1 };
        _depth = 0;
        try
        {
            while (!r.AtEnd)
            {
                int tokenStart = r.Position;
                ushort id = r.Read();
                if (id == J.CloseId) { Close(r.Position); continue; }
                if (id == J.OpenId) { Push(new Frame { Container = -1, Start = tokenStart }); continue; }
                if (id == J.EqualsId) continue;

                if (!r.AtEnd && r.PeekId() == J.EqualsId)
                {
                    // key = ...
                    long keyInt = id is J.I32 or J.U32 ? r.Int(id) : -1;
                    string? keyName = J.IsScalar(id) ? null : J.TokenName(id);
                    r.Read(); // '='
                    if (r.AtEnd) break;
                    if (r.PeekId() == J.OpenId)
                    {
                        r.Read();
                        OpenKeyed(keyName, keyInt, tokenStart);
                        continue;
                    }
                    ushort vid = r.Read();
                    if (!J.IsScalar(vid) && !r.AtEnd && r.PeekId() == J.OpenId)
                    {
                        r.Read(); // token-prefixed block such as rgb { }
                        Push(new Frame { Container = -1, Start = tokenStart });
                        continue;
                    }
                    OnScalar(id, vid, ref r);
                }
            }
        }
        catch (EndOfStreamException) { /* truncated gamestate: keep what was read */ }
        catch (ArgumentOutOfRangeException) { }
    }

    private void Push(Frame f)
    {
        if (++_depth >= _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
        _stack[_depth] = f;
    }

    private void OpenKeyed(string? keyName, long keyInt, int start)
    {
        var frame = new Frame { Name = keyName ?? (keyInt >= 0 ? keyInt.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""), Container = -1, Start = start };
        var parent = _stack[_depth];
        if (keyInt >= 0 && _depth is 1 or 2 && parent.Record == null && !string.IsNullOrEmpty(parent.Name) && parent.Name[0] == '#')
        {
            // An integer-keyed block directly inside a named section (depth 1 or 2): a record of that section.
            int ci = ContainerFor(_depth);
            var rec = new Record { Id = keyInt, Container = ci, Start = start };
            _records.Add(rec);
            frame.Record = rec;
        }
        Push(frame);
    }

    /// <summary>The container (section path) of the frame at 'depth'; created on first use.</summary>
    private int ContainerFor(int depth)
    {
        if (_stack[depth].Container >= 0) return _stack[depth].Container;
        var sb = new StringBuilder();
        for (int i = 1; i <= depth; i++) sb.Append('/').Append(_stack[i].Name);
        string path = sb.ToString();
        if (!_containerByPath.TryGetValue(path, out int ci))
        {
            ci = _containers.Count;
            _containers.Add(new Container { Path = path, Start = _stack[depth].Start });
            _containerByPath[path] = ci;
        }
        _stack[depth].Container = ci;
        return ci;
    }

    private void Close(int end)
    {
        if (_depth == 0) return;
        var f = _stack[_depth--];
        if (f.Record != null) f.Record.End = end;
        if (f.Container >= 0) _containers[f.Container].End = end;
    }

    private void OnScalar(ushort keyId, ushort valueId, ref J r)
    {
        if (_depth == 0 && _playthroughId == null && J.IsString(valueId) && r.StringBytes(valueId).Length == 36)
        {
            // playthrough_id: the first UUID string at the top level
            string s = Encoding.ASCII.GetString(r.StringBytes(valueId));
            if (UuidRegex().IsMatch(s)) _playthroughId = s;
            return;
        }
        var rec = _stack[_depth].Record;
        if (rec == null) return;
        if (J.IsString(valueId))
        {
            var bytes = r.StringBytes(valueId);
            if (bytes.Length > 10 && bytes.StartsWith("pattern"u8) && bytes.EndsWith(".dds"u8))
            {
                if (!rec.HasPattern) _containers[rec.Container].Patterns++;
                rec.HasPattern = true;
            }
            else if (bytes.Length is >= 540 and <= 600 && IsDna(bytes))
            {
                rec.Dna = Encoding.ASCII.GetString(bytes);
            }
            else if (rec.TitleKey == null && bytes.Length is >= 3 and <= 64 && bytes[1] == (byte)'_')
            {
                string s = Encoding.UTF8.GetString(bytes);
                if (TitleKeyRegex().IsMatch(s))
                {
                    rec.TitleKey = s;
                    rec.Ints = new List<(ushort, long)>(8);
                    _containers[rec.Container].TitleKeys++;
                }
            }
        }
        else if (rec.Ints != null && valueId is J.U32 or J.I32 && rec.Ints.Count < 48)
        {
            rec.Ints.Add((keyId, r.Int(valueId)));
        }
    }

    private static bool IsDna(ReadOnlySpan<byte> s)
    {
        Span<byte> buf = stackalloc byte[600];
        return System.Buffers.Text.Base64.DecodeFromUtf8(s, buf, out _, out int written) == System.Buffers.OperationStatus.Done
               && written == DnaBytes;
    }

    // ------------------------------------------------------------------ calibration and index

    private SaveIndex BuildIndex(NamedColors colors, CoaTokenMap tokens, Action<string> log)
    {
        var ix = new SaveIndex { PlaythroughId = _playthroughId };

        // CoA database.
        int coaCi = Best(c => c.Patterns);
        if (coaCi >= 0 && _containers[coaCi].Patterns >= 50 && _containers[coaCi].End > _containers[coaCi].Start)
        {
            var c = _containers[coaCi];
            var bytes = _d.AsSpan(c.Start, c.End - c.Start).ToArray();
            var spans = new Dictionary<long, (int, int)>();
            foreach (var rec in _records)
                if (rec.Container == coaCi && rec.HasPattern && rec.End > rec.Start)
                    spans[rec.Id] = (rec.Start - c.Start, rec.End - rec.Start);
            // Calibrate the CoA field tokens on a sample of this save's own database.
            var sample = new List<PdxNode>();
            foreach (var (start, len) in spans.Values.Take(3000))
                if (ParseRecord(bytes.AsMemory(start, len)) is PdxNode n) sample.Add(n);
            tokens.Calibrate(sample, colors);
            ix.Coas = new CoaStore(bytes, spans, m => ParseRecord(m) is PdxNode n ? tokens.Rename(n) : null, colors);
        }

        // Titles and their CoA ids.
        int titleCi = Best(c => c.TitleKeys);
        if (titleCi >= 0 && _containers[titleCi].TitleKeys >= 50)
        {
            var titles = _records.Where(r => r.Container == titleCi && r.TitleKey != null).ToList();
            ushort coaToken = CalibrateCoaIdToken(titles, ix.Coas);
            foreach (var t in titles)
            {
                var tr = new TitleRecord { Id = t.Id, Key = t.TitleKey };
                if (coaToken != 0)
                    foreach (var (tok, v) in t.Ints!)
                        if (tok == coaToken) { tr.CoaId = v; break; }
                ix.Titles[t.Id] = tr;
                ix.TitleIdByKey[t.TitleKey!] = t.Id;
            }
            log($"binary gamestate: titles in {_containers[titleCi].Path} ({titles.Count}), CoA id field {(coaToken == 0 ? "not found" : J.TokenName(coaToken))}");
        }

        // Stored DNA: prefer the container with the most DNA records (the living characters).
        var dnaByContainer = _records.Where(r => r.Dna != null).GroupBy(r => r.Container).OrderByDescending(g => g.Count());
        foreach (var g in dnaByContainer)
            foreach (var rec in g)
                if (!ix.Characters.ContainsKey(rec.Id))
                    ix.Characters[rec.Id] = new CharacterRecord { Id = rec.Id, Dna = rec.Dna };
        return ix;
    }

    private int Best(Func<Container, int> score)
    {
        int best = -1, bestScore = 0;
        for (int i = 0; i < _containers.Count; i++)
        {
            int s = score(_containers[i]);
            if (s > bestScore) { bestScore = s; best = i; }
        }
        return best;
    }

    /// <summary>The integer field present in most titles whose values point into the CoA database and differ per title.</summary>
    private static ushort CalibrateCoaIdToken(List<Record> titles, CoaStore? coas)
    {
        if (coas == null || titles.Count == 0) return 0;
        var ids = coas.Ids.ToHashSet();
        var stats = new Dictionary<ushort, (int Count, int InDb, HashSet<long> Distinct)>();
        foreach (var t in titles)
            foreach (var (tok, v) in t.Ints!.DistinctBy(x => x.Token))
            {
                if (!stats.TryGetValue(tok, out var s)) s = (0, 0, new HashSet<long>());
                s.Distinct.Add(v);
                stats[tok] = (s.Count + 1, s.InDb + (ids.Contains(v) ? 1 : 0), s.Distinct);
            }
        ushort best = 0;
        double bestScore = 0;
        foreach (var (tok, s) in stats)
        {
            if (s.Count < titles.Count / 3) continue;
            double score = (double)s.Count / titles.Count * s.InDb / s.Count * s.Distinct.Count / s.Count;
            if (score > bestScore) { bestScore = score; best = tok; }
        }
        return bestScore >= 0.5 ? best : (ushort)0;
    }

    private static PdxNode? ParseRecord(ReadOnlyMemory<byte> record)
    {
        var root = JominiBinary.Parse(record.Span);
        return root.Items.Count > 0 ? root.Items[0].Value.Block : null;
    }
}
