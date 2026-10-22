// Single-pass scanner over a text gamestate (debug-mode saves are ~95 MB; budget ~1 s).
// Records sit at column 0 regardless of nesting (the CoA id 18837 also appears in another section), so the scanner
// tokenises the whole file, tracks brace depth outside quotes and knows its section from the key path:
//   living.<id> / dead_unprunable.<id>                 first_name birth female ethnicity culture dynasty_house dna,
//                                                      family_data.child (-> parents), family_data.real_father,
//                                                      landed_data.domain (first = primary title)
//   landed_titles.landed_titles.<id>                   key coat_of_arms_id
//   dynasties.dynasty_house.<id> / dynasties.dynasties.<id>   dynasty coat_of_arms_id name / coat_of_arms_id key
//   culture_manager.cultures.<id>                      culture_template name heritage clothing_gfx unit_gfx coa_gfx ethnicities
//   coat_of_arms.coat_of_arms_manager_database.<id>    byte span only (parsed on demand from a retained copy)
//   traits_lookup                                      trait names by index
// Nothing is allocated for sections that are not listed above except brace tracking.
using System.Text;
using CK3xBL.Appearance.Coa;
using CK3xBL.Appearance.Pdx;

namespace CK3xBL.Appearance.Save;

internal sealed class TextGamestateScanner
{
    private enum Role : byte
    {
        Other, Root, Living, Character, CharFamily, CharChildren, CharLanded, CharDomain,
        TitlesOuter, Titles, Title, DynastiesOuter, Houses, House, Dynasties, Dynasty,
        CultureManager, Cultures, Culture, CultureClothing, CultureUnit, CultureCoaGfx, CultureEthnicities,
        CoaOuter, CoaDb, CoaRecord, TraitsLookup,
    }

    private struct Frame
    {
        public Role Role;
        public long Id;
        public int Start;
    }

    private readonly byte[] _d;
    private int _i;
    private Frame[] _stack = new Frame[128];
    private int _depth;
    private readonly SaveIndex _ix = new();
    private CharacterRecord? _char;
    private TitleRecord? _title;
    private HouseRecord? _house;
    private DynastyRecord? _dynasty;
    private CultureRecord? _culture;
    private readonly List<string> _traits = new();
    private readonly Dictionary<long, (int, int)> _coaSpans = new();
    private int _coaDbStart = -1, _coaDbEnd = -1;

    private TextGamestateScanner(byte[] data) { _d = data; }

    public static SaveIndex Scan(byte[] gamestate, NamedColors colors)
    {
        var s = new TextGamestateScanner(gamestate);
        s.Run();
        s._ix.TraitsLookup = s._traits.ToArray();
        if (s._coaDbStart >= 0 && s._coaDbEnd > s._coaDbStart)
        {
            // Keep only the CoA database (a few MB) instead of the whole gamestate.
            var bytes = gamestate.AsSpan(s._coaDbStart, s._coaDbEnd - s._coaDbStart).ToArray();
            var spans = new Dictionary<long, (int, int)>(s._coaSpans.Count);
            foreach (var (id, (start, len)) in s._coaSpans) spans[id] = (start - s._coaDbStart, len);
            s._ix.Coas = new CoaStore(bytes, spans, ParseRecord, colors);
        }
        return s._ix;
    }

    /// <summary>Parses one "id={...}" record and returns its block.</summary>
    private static PdxNode? ParseRecord(ReadOnlyMemory<byte> record)
    {
        var root = PdxTextParser.Parse(record.Span);
        return root.Items.Count > 0 ? root.Items[0].Value.Block : null;
    }

    private void Run()
    {
        var d = _d;
        int n = d.Length;
        _stack[0] = new Frame { Role = Role.Root, Id = -1, Start = 0 };
        _depth = 0;
        while (true)
        {
            SkipWs();
            if (_i >= n) break;
            byte c = d[_i];
            if (c == '{') { _i++; Open(Role.Other, -1, _i - 1); continue; } // anonymous block in a list
            if (c == '}') { _i++; Close(); continue; }
            if (IsOp(c)) { _i++; continue; }                                  // stray operator

            int tokenStart = _i;
            ReadToken(out int ks, out int ke, out bool kq);
            SkipWs();
            if (_i < n && IsOp(d[_i]))
            {
                _i++;
                if (_i < n && d[_i] == '=') _i++;
                SkipWs();
                if (_i < n && d[_i] == '{')
                {
                    _i++;
                    OpenKeyed(ks, ke, tokenStart);
                    continue;
                }
                if (_i >= n) break;
                if (d[_i] == '}') continue; // "key = }" (malformed): the brace closes the enclosing block
                ReadToken(out int vs, out int ve, out bool vq);
                int save = _i;
                SkipWs();
                if (!vq && _i < n && d[_i] == '{' && IsColorFunction(vs, ve))
                {
                    _i++;
                    Open(Role.Other, -1, tokenStart); // key = rgb { ... }
                    continue;
                }
                _i = save;
                OnScalar(ks, ke, vs, ve);
            }
            else
            {
                OnBare(ks, ke, kq);
            }
        }
    }

    // ------------------------------------------------------------------ tokenising

    private void SkipWs()
    {
        var d = _d;
        int n = d.Length;
        while (_i < n)
        {
            byte c = d[_i];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r') { _i++; continue; }
            if (c == '#') { while (_i < n && d[_i] != '\n') _i++; continue; }
            if (c == 0xEF && _i + 2 < n && d[_i + 1] == 0xBB && d[_i + 2] == 0xBF) { _i += 3; continue; } // BOM
            break;
        }
    }

    private static bool IsOp(byte c) => c == '=' || c == '<' || c == '>' || c == '!' || c == '?';

    /// <summary>Reads a quoted or bare token; [s, e) excludes the quotes.</summary>
    private void ReadToken(out int s, out int e, out bool quoted)
    {
        var d = _d;
        int n = d.Length;
        if (d[_i] == '"')
        {
            quoted = true;
            s = ++_i;
            while (_i < n && d[_i] != '"')
            {
                if (d[_i] == '\\' && _i + 1 < n) _i++;
                _i++;
            }
            e = _i;
            if (_i < n) _i++;
            return;
        }
        quoted = false;
        s = _i;
        while (_i < n)
        {
            byte c = d[_i];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '{' || c == '}' || c == '=' || c == '"' || c == '#' ||
                c == '<' || c == '>' || c == '!' || (c == '?' && _i + 1 < n && d[_i + 1] == '='))
                break;
            _i++;
        }
        e = _i;
        if (e == s) _i++; // stray character: always make progress
    }

    private bool Eq(int s, int e, ReadOnlySpan<byte> lit) => _d.AsSpan(s, e - s).SequenceEqual(lit);

    private bool IsColorFunction(int s, int e) =>
        Eq(s, e, "rgb"u8) || Eq(s, e, "hsv"u8) || Eq(s, e, "hsv360"u8) || Eq(s, e, "hex"u8) || Eq(s, e, "LIST"u8);

    private long Number(int s, int e)
    {
        if (e <= s || e - s > 18) return -1;
        long v = 0;
        for (int k = s; k < e; k++)
        {
            byte c = _d[k];
            if (c < '0' || c > '9') return -1;
            v = v * 10 + (c - '0');
        }
        return v;
    }

    private string Text(int s, int e) => Encoding.UTF8.GetString(_d, s, e - s);

    // ------------------------------------------------------------------ structure

    private void Open(Role role, long id, int start)
    {
        if (++_depth >= _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
        _stack[_depth] = new Frame { Role = role, Id = id, Start = start };
    }

    private void Close()
    {
        if (_depth == 0) return; // unbalanced '}' at top level
        var f = _stack[_depth--];
        switch (f.Role)
        {
            case Role.CoaRecord: _coaSpans[f.Id] = (f.Start, _i - f.Start); break;
            case Role.CoaDb: _coaDbStart = f.Start; _coaDbEnd = _i; break;
            case Role.Character: _char = null; break;
            case Role.Title: _title = null; break;
            case Role.House: _house = null; break;
            case Role.Dynasty: _dynasty = null; break;
            case Role.Culture: _culture = null; break;
        }
    }

    private void OpenKeyed(int ks, int ke, int start)
    {
        Role parent = _stack[_depth].Role;
        Role role = Role.Other;
        long id = -1;
        switch (parent)
        {
            case Role.Root:
                if (Eq(ks, ke, "living"u8) || Eq(ks, ke, "dead_unprunable"u8)) role = Role.Living;
                else if (Eq(ks, ke, "landed_titles"u8)) role = Role.TitlesOuter;
                else if (Eq(ks, ke, "dynasties"u8)) role = Role.DynastiesOuter;
                else if (Eq(ks, ke, "culture_manager"u8)) role = Role.CultureManager;
                else if (Eq(ks, ke, "coat_of_arms"u8)) role = Role.CoaOuter;
                else if (Eq(ks, ke, "traits_lookup"u8)) role = Role.TraitsLookup;
                break;
            case Role.Living:
                if ((id = Number(ks, ke)) >= 0)
                {
                    role = Role.Character;
                    _char = new CharacterRecord { Id = id, Female = false };   // text saves write female=yes for women only
                    _ix.Characters[id] = _char;
                }
                break;
            case Role.Character:
                if (Eq(ks, ke, "family_data"u8)) role = Role.CharFamily;
                else if (Eq(ks, ke, "landed_data"u8)) role = Role.CharLanded;
                break;
            case Role.CharFamily:
                if (Eq(ks, ke, "child"u8)) role = Role.CharChildren;
                break;
            case Role.CharLanded:
                if (Eq(ks, ke, "domain"u8)) role = Role.CharDomain;
                break;
            case Role.TitlesOuter:
                if (Eq(ks, ke, "landed_titles"u8)) role = Role.Titles;
                break;
            case Role.Titles:
                if ((id = Number(ks, ke)) >= 0)
                {
                    role = Role.Title;
                    _title = new TitleRecord { Id = id };
                    _ix.Titles[id] = _title;
                }
                break;
            case Role.DynastiesOuter:
                if (Eq(ks, ke, "dynasty_house"u8)) role = Role.Houses;
                else if (Eq(ks, ke, "dynasties"u8)) role = Role.Dynasties;
                break;
            case Role.Houses:
                if ((id = Number(ks, ke)) >= 0)
                {
                    role = Role.House;
                    _house = new HouseRecord { Id = id };
                    _ix.Houses[id] = _house;
                }
                break;
            case Role.Dynasties:
                if ((id = Number(ks, ke)) >= 0)
                {
                    role = Role.Dynasty;
                    _dynasty = new DynastyRecord { Id = id };
                    _ix.Dynasties[id] = _dynasty;
                }
                break;
            case Role.CultureManager:
                if (Eq(ks, ke, "cultures"u8)) role = Role.Cultures;
                break;
            case Role.Cultures:
                if ((id = Number(ks, ke)) >= 0)
                {
                    role = Role.Culture;
                    _culture = new CultureRecord { Id = id };
                    _ix.Cultures[id] = _culture;
                }
                break;
            case Role.Culture:
                if (Eq(ks, ke, "clothing_gfx"u8)) role = Role.CultureClothing;
                else if (Eq(ks, ke, "unit_gfx"u8)) role = Role.CultureUnit;
                else if (Eq(ks, ke, "coa_gfx"u8)) role = Role.CultureCoaGfx;
                else if (Eq(ks, ke, "ethnicities"u8)) role = Role.CultureEthnicities;
                break;
            case Role.CoaOuter:
                if (Eq(ks, ke, "coat_of_arms_manager_database"u8)) role = Role.CoaDb;
                break;
            case Role.CoaDb:
                if ((id = Number(ks, ke)) >= 0) role = Role.CoaRecord;
                break;
        }
        Open(role, id, start);
    }

    private void OnScalar(int ks, int ke, int vs, int ve)
    {
        switch (_stack[_depth].Role)
        {
            case Role.Root:
                if (Eq(ks, ke, "date"u8)) _ix.Date = CK3Date.Parse(Text(vs, ve));
                else if (Eq(ks, ke, "playthrough_id"u8)) _ix.PlaythroughId = Text(vs, ve);
                break;
            case Role.Character when _char != null:
                if (Eq(ks, ke, "first_name"u8)) _char.FirstName = Text(vs, ve);
                else if (Eq(ks, ke, "birth"u8)) _char.Birth = Text(vs, ve);
                else if (Eq(ks, ke, "female"u8)) _char.Female = Eq(vs, ve, "yes"u8);
                else if (Eq(ks, ke, "ethnicity"u8)) _char.Ethnicity = Text(vs, ve);
                else if (Eq(ks, ke, "culture"u8)) _char.Culture = Number(vs, ve);
                else if (Eq(ks, ke, "dynasty_house"u8)) _char.House = Number(vs, ve);
                else if (Eq(ks, ke, "dna"u8)) _char.Dna = Text(vs, ve);
                break;
            case Role.CharFamily when _char != null:
                if (Eq(ks, ke, "real_father"u8) && Number(vs, ve) is long rf and >= 0) _ix.AddParent(_char.Id, rf);
                break;
            case Role.Title when _title != null:
                if (Eq(ks, ke, "key"u8)) { _title.Key = Text(vs, ve); _ix.TitleIdByKey[_title.Key] = _title.Id; }
                else if (Eq(ks, ke, "coat_of_arms_id"u8)) _title.CoaId = Number(vs, ve);
                break;
            case Role.House when _house != null:
                if (Eq(ks, ke, "dynasty"u8)) _house.Dynasty = Number(vs, ve);
                else if (Eq(ks, ke, "coat_of_arms_id"u8)) _house.CoaId = Number(vs, ve);
                else if (Eq(ks, ke, "name"u8)) _house.Name = Text(vs, ve);
                break;
            case Role.Dynasty when _dynasty != null:
                if (Eq(ks, ke, "coat_of_arms_id"u8)) _dynasty.CoaId = Number(vs, ve);
                else if (Eq(ks, ke, "key"u8)) _dynasty.Key = Text(vs, ve);
                break;
            case Role.Culture when _culture != null:
                if (Eq(ks, ke, "culture_template"u8)) _culture.Key = Text(vs, ve);
                else if (Eq(ks, ke, "name"u8)) _culture.Key ??= Text(vs, ve);
                else if (Eq(ks, ke, "heritage"u8)) _culture.Heritage = Text(vs, ve);
                break;
            case Role.CultureEthnicities when _culture != null:
                _culture.Ethnicities.Add(Text(vs, ve));
                break;
        }
    }

    private void OnBare(int s, int e, bool quoted)
    {
        switch (_stack[_depth].Role)
        {
            case Role.CharChildren when _char != null:
                if (Number(s, e) is long child and >= 0) _ix.AddParent(child, _char.Id);
                break;
            case Role.CharDomain when _char != null:
                if (_char.PrimaryTitle < 0) _char.PrimaryTitle = Number(s, e);
                break;
            case Role.TraitsLookup:
                _traits.Add(Text(s, e));
                break;
            case Role.CultureClothing when _culture != null:
                _culture.ClothingGfx ??= Text(s, e);
                break;
            case Role.CultureUnit when _culture != null:
                _culture.UnitGfx ??= Text(s, e);
                break;
            case Role.CultureCoaGfx when _culture != null:
                _culture.CoaGfx ??= Text(s, e);
                break;
        }
    }
}
