// Tolerant parser for Paradox (jomini) text script: game files (genes, ethnicities, named colours, coats of arms),
// save metadata and single records cut out of a save. Never throws on malformed input; it parses what it can.
//   key = value | key = { ... } | key = rgb { r g b } | bare list values | anonymous { } blocks | # comments
//   operators = < > <= >= != ?= == | file variables @name = 0.5 (resolved when used as a value; @[ ] expressions are kept as text)
using System.Text;

namespace CK3xBL.Appearance.Pdx;

internal static class PdxTextParser
{
    private static readonly HashSet<string> ColorFunctions = new(StringComparer.OrdinalIgnoreCase)
        { "rgb", "hsv", "hsv360", "hex", "hsvRGB", "LIST" };

    public static PdxNode Parse(string text)
    {
        var p = new Parser(text);
        return p.ParseRoot();
    }

    public static PdxNode Parse(ReadOnlySpan<byte> utf8) => Parse(Encoding.UTF8.GetString(utf8));

    public static PdxNode ParseFile(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;
        private readonly Dictionary<string, string> _vars = new(StringComparer.Ordinal);

        public Parser(string s) { _s = s; }

        public PdxNode ParseRoot()
        {
            var root = new PdxNode();
            ParseBody(root, topLevel: true);
            return root;
        }

        private enum Tok { End, Open, Close, Op, Word }

        // Reads the next token. For Word, 'text' is the word (quotes removed); 'quoted' tells whether it was quoted.
        private Tok Next(out string text, out bool quoted)
        {
            text = ""; quoted = false;
            var s = _s;
            while (_i < s.Length)
            {
                char c = s[_i];
                if (c == '﻿' || char.IsWhiteSpace(c)) { _i++; continue; }
                if (c == '#') { while (_i < s.Length && s[_i] != '\n') _i++; continue; }
                break;
            }
            if (_i >= s.Length) return Tok.End;
            char ch = s[_i];
            if (ch == '{') { _i++; return Tok.Open; }
            if (ch == '}') { _i++; return Tok.Close; }
            if (ch == '=' || ch == '<' || ch == '>' || ch == '!' || ch == '?')
            {
                int start = _i++;
                if (_i < s.Length && s[_i] == '=') _i++;
                text = s.Substring(start, _i - start);
                return Tok.Op;
            }
            if (ch == '"')
            {
                quoted = true;
                _i++;
                var sb = new StringBuilder();
                while (_i < s.Length && s[_i] != '"')
                {
                    if (s[_i] == '\\' && _i + 1 < s.Length) { sb.Append(s[_i + 1]); _i += 2; continue; }
                    sb.Append(s[_i++]);
                }
                _i++; // closing quote (or end of input)
                text = sb.ToString();
                return Tok.Word;
            }
            int b = _i;
            while (_i < s.Length)
            {
                char c = s[_i];
                if (char.IsWhiteSpace(c) || c == '{' || c == '}' || c == '=' || c == '<' || c == '>' || c == '!' ||
                    c == '"' || c == '#' || (c == '?' && _i + 1 < s.Length && s[_i + 1] == '='))
                    break;
                _i++;
            }
            if (_i == b) { _i++; return Next(out text, out quoted); } // stray character: skip it
            text = s.Substring(b, _i - b);
            return Tok.Word;
        }

        private Tok Peek()
        {
            int save = _i;
            var t = Next(out _, out _);
            _i = save;
            return t;
        }

        private void ParseBody(PdxNode node, bool topLevel)
        {
            while (true)
            {
                var t = Next(out var text, out var quoted);
                switch (t)
                {
                    case Tok.End:
                        return;
                    case Tok.Close:
                        if (topLevel) continue; // unbalanced '}' in a file: ignore it
                        return;
                    case Tok.Open:
                    {
                        var anon = new PdxNode();
                        ParseBody(anon, false);
                        node.List.Add(new PdxValue(anon));
                        continue;
                    }
                    case Tok.Op:
                        continue; // operator without a key: ignore
                    case Tok.Word:
                        if (Peek() == Tok.Op)
                        {
                            Next(out var op, out _);
                            var value = ParseValue();
                            if (value == null) return;
                            if (text.StartsWith('@') && value.Text != null) _vars[text] = value.Text;
                            node.Items.Add(new PdxItem(text, op, value));
                        }
                        else
                        {
                            node.List.Add(new PdxValue(quoted ? text : Resolve(text)));
                        }
                        continue;
                }
            }
        }

        private PdxValue? ParseValue()
        {
            int before = _i;
            var t = Next(out var text, out var quoted);
            switch (t)
            {
                case Tok.Close:
                    _i = before; // "key = }": leave the brace to the enclosing block
                    return new PdxValue("");
                case Tok.Open:
                {
                    var block = new PdxNode();
                    ParseBody(block, false);
                    return new PdxValue(block);
                }
                case Tok.Word:
                    if (!quoted && ColorFunctions.Contains(text) && Peek() == Tok.Open)
                    {
                        Next(out _, out _);
                        var block = new PdxNode { Function = text.ToLowerInvariant() };
                        ParseBody(block, false);
                        return new PdxValue(block);
                    }
                    return new PdxValue(quoted ? text : Resolve(text));
                case Tok.End:
                    return null;
                default:
                    return new PdxValue(""); // "key = =": keep going
            }
        }

        private string Resolve(string word) =>
            word.Length > 1 && word[0] == '@' && word[1] != '[' && _vars.TryGetValue(word, out var v) ? v : word;
    }
}
