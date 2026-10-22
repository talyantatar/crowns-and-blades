// A small JSON reader/writer that keeps key order and number text exactly, so dlc_load.json and other files we edit change
// only where we change them. Values: JsonObject, List<object?>, string, JsonNumber, bool, null.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CK3xBL.Setup.Util;

internal sealed class JsonNumber
{
    public readonly string Raw;
    public JsonNumber(string raw) { Raw = raw; }
    public JsonNumber(long value) { Raw = value.ToString(CultureInfo.InvariantCulture); }

    public long? AsLong() => long.TryParse(Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    public override string ToString() => Raw;
}

internal sealed class JsonObject : IEnumerable<KeyValuePair<string, object?>>
{
    private readonly List<KeyValuePair<string, object?>> _items = new();

    public int Count => _items.Count;

    /// <summary>Get: the value or null. Set: replaces in place (keeps the key's position) or appends.</summary>
    public object? this[string key]
    {
        get => TryGet(key, out var v) ? v : null;
        set
        {
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].Key == key) { _items[i] = new KeyValuePair<string, object?>(key, value); return; }
            _items.Add(new KeyValuePair<string, object?>(key, value));
        }
    }

    public bool Contains(string key) => TryGet(key, out _);

    public bool TryGet(string key, out object? value)
    {
        foreach (var kv in _items)
            if (kv.Key == key) { value = kv.Value; return true; }
        value = null;
        return false;
    }

    public bool Remove(string key) => _items.RemoveAll(kv => kv.Key == key) > 0;

    public string? GetString(string key) => this[key] as string;
    public long? GetLong(string key) => (this[key] as JsonNumber)?.AsLong();
    public bool GetBool(string key) => this[key] is bool b && b;
    public JsonObject? GetObject(string key) => this[key] as JsonObject;
    public List<object?>? GetArray(string key) => this[key] as List<object?>;

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class Json
{
    private const int MaxDepth = 256;

    /// <summary>Parses JSON text (a leading BOM is ignored). Throws FormatException on any syntax error.</summary>
    public static object? Parse(string text)
    {
        int i = 0;
        if (text.Length > 0 && text[0] == '﻿') i = 1;
        var value = ParseValue(text, ref i, 0);
        SkipWhitespace(text, ref i);
        if (i != text.Length) throw Error(text, i, "unexpected text after the value");
        return value;
    }

    public static bool TryParse(string text, out object? value)
    {
        try { value = Parse(text); return true; }
        catch (FormatException) { value = null; return false; }
    }

    public static string Write(object? value, bool indented)
    {
        var sb = new StringBuilder();
        WriteValue(sb, value, indented, 0);
        return sb.ToString();
    }

    private static object? ParseValue(string s, ref int i, int depth)
    {
        if (depth > MaxDepth) throw Error(s, i, "nesting too deep");
        SkipWhitespace(s, ref i);
        if (i >= s.Length) throw Error(s, i, "unexpected end");
        char c = s[i];
        switch (c)
        {
            case '{': return ParseObject(s, ref i, depth);
            case '[': return ParseArray(s, ref i, depth);
            case '"': return ParseString(s, ref i);
            case 't': Expect(s, ref i, "true"); return true;
            case 'f': Expect(s, ref i, "false"); return false;
            case 'n': Expect(s, ref i, "null"); return null;
            default:
                if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(s, ref i);
                throw Error(s, i, $"unexpected character '{c}'");
        }
    }

    private static JsonObject ParseObject(string s, ref int i, int depth)
    {
        var obj = new JsonObject();
        i++; // {
        SkipWhitespace(s, ref i);
        if (i < s.Length && s[i] == '}') { i++; return obj; }
        while (true)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length || s[i] != '"') throw Error(s, i, "expected a key");
            var key = ParseString(s, ref i);
            SkipWhitespace(s, ref i);
            if (i >= s.Length || s[i] != ':') throw Error(s, i, "expected ':'");
            i++;
            obj[key] = ParseValue(s, ref i, depth + 1);
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw Error(s, i, "unexpected end in an object");
            if (s[i] == ',') { i++; continue; }
            if (s[i] == '}') { i++; return obj; }
            throw Error(s, i, "expected ',' or '}'");
        }
    }

    private static List<object?> ParseArray(string s, ref int i, int depth)
    {
        var list = new List<object?>();
        i++; // [
        SkipWhitespace(s, ref i);
        if (i < s.Length && s[i] == ']') { i++; return list; }
        while (true)
        {
            list.Add(ParseValue(s, ref i, depth + 1));
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw Error(s, i, "unexpected end in an array");
            if (s[i] == ',') { i++; continue; }
            if (s[i] == ']') { i++; return list; }
            throw Error(s, i, "expected ',' or ']'");
        }
    }

    private static string ParseString(string s, ref int i)
    {
        i++; // opening quote
        var sb = new StringBuilder();
        while (true)
        {
            if (i >= s.Length) throw Error(s, i, "unterminated string");
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c < 0x20) throw Error(s, i - 1, "control character in a string");
            if (c != '\\') { sb.Append(c); continue; }
            if (i >= s.Length) throw Error(s, i, "unterminated escape");
            char e = s[i++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (i + 4 > s.Length ||
                        !int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                        throw Error(s, i, "bad \\u escape");
                    sb.Append((char)code);
                    i += 4;
                    break;
                default: throw Error(s, i - 1, $"bad escape '\\{e}'");
            }
        }
    }

    private static JsonNumber ParseNumber(string s, ref int i)
    {
        int start = i;
        if (s[i] == '-') i++;
        if (i >= s.Length || !char.IsDigit(s[i])) throw Error(s, i, "bad number");
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (i < s.Length && s[i] == '.')
        {
            i++;
            if (i >= s.Length || !char.IsDigit(s[i])) throw Error(s, i, "bad number");
            while (i < s.Length && char.IsDigit(s[i])) i++;
        }
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
            if (i >= s.Length || !char.IsDigit(s[i])) throw Error(s, i, "bad number");
            while (i < s.Length && char.IsDigit(s[i])) i++;
        }
        return new JsonNumber(s.Substring(start, i - start));
    }

    private static void Expect(string s, ref int i, string word)
    {
        if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw Error(s, i, $"expected '{word}'");
        i += word.Length;
    }

    private static void SkipWhitespace(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
    }

    private static FormatException Error(string s, int i, string what) =>
        new($"invalid JSON at offset {Math.Min(i, s.Length)}: {what}");

    private static void WriteValue(StringBuilder sb, object? v, bool indented, int level)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case string str: WriteString(sb, str); break;
            case JsonNumber n: sb.Append(n.Raw); break;
            case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
            case long n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
            case JsonObject o:
                if (o.Count == 0) { sb.Append("{}"); break; }
                sb.Append('{');
                bool first = true;
                foreach (var kv in o)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, indented, level + 1);
                    WriteString(sb, kv.Key);
                    sb.Append(indented ? ": " : ":");
                    WriteValue(sb, kv.Value, indented, level + 1);
                }
                NewLine(sb, indented, level);
                sb.Append('}');
                break;
            case IList list:
                if (list.Count == 0) { sb.Append("[]"); break; }
                sb.Append('[');
                for (int k = 0; k < list.Count; k++)
                {
                    if (k > 0) sb.Append(',');
                    NewLine(sb, indented, level + 1);
                    WriteValue(sb, list[k], indented, level + 1);
                }
                NewLine(sb, indented, level);
                sb.Append(']');
                break;
            default:
                throw new ArgumentException($"cannot write a {v.GetType().Name} as JSON");
        }
    }

    private static void NewLine(StringBuilder sb, bool indented, int level)
    {
        if (!indented) return;
        sb.Append('\n');
        sb.Append(' ', level * 2);
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
