// One CK3XBL record from the log (PROTOCOL §1.1): the marker "CK3XBL|<TYPE>|<args...>" plus its optional scope dump.
using System.Globalization;

namespace CK3xBL.Core.Logs;

public sealed class Ck3Record
{
    /// <summary>Record type: B, L, S, A, R, C, CX, AX, RX, CHOICE, J, IN, RES, END, or anything else (ignored downstream).</summary>
    public string Type = "";
    /// <summary>The '|'-separated fields after the type.</summary>
    public string[] Args = Array.Empty<string>();
    /// <summary>The text after "CK3XBL|", e.g. "B|BEGIN|17".</summary>
    public string Raw = "";
    /// <summary>Wall-clock time of day from the [HH:MM:SS] stamp (no date in CK3 logs).</summary>
    public TimeSpan Time;
    /// <summary>The script context in parentheses ("ck3xbl_on_combat_start"), absent for run files.</summary>
    public string? Context;
    public ScopeDump? Dump;

    public string Arg(int i) => i < Args.Length ? Args[i] : "";

    /// <summary>Parses argument i as a non-negative integer; returns null when missing or malformed.</summary>
    public int? IntArg(int i) => int.TryParse(Arg(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public override string ToString() => Dump is null ? Raw : $"{Raw} (+{Dump.Values.Count} values)";
}

/// <summary>An info_log_scopes dump: current scope print, root print and the saved ck3xbl_* targets/values.</summary>
public sealed class ScopeDump
{
    public string Current = "";
    public string Root = "";
    /// <summary>Only keys starting with "ck3xbl_" (foreign engine-saved scopes are dropped, PROTOCOL §1.1).</summary>
    public Dictionary<string, string> Values = new(StringComparer.Ordinal);

    public bool Has(string key) => Values.ContainsKey(key);

    public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;

    /// <summary>Values print with two decimals ("709.00"); rounds to the nearest integer.</summary>
    public int? GetInt(string key) => GetDouble(key) is { } d ? (int)Math.Round(d, MidpointRounding.AwayFromZero) : null;

    public long? GetLong(string key) => GetDouble(key) is { } d ? (long)Math.Round(d, MidpointRounding.AwayFromZero) : null;

    public double? GetDouble(string key)
    {
        var s = Get(key);
        if (s is null) return null;
        return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public bool GetBool(string key) => GetInt(key) is { } v && v != 0;

    /// <summary>The parsed scope saved under key, or null when absent, unparseable or an empty scope.</summary>
    public ScopePrint? GetScope(string key)
    {
        var p = ScopePrint.Parse(Get(key));
        return p is null || p.IsNull ? null : p;
    }

    public ScopePrint? CurrentScope
    {
        get
        {
            var p = ScopePrint.Parse(Current);
            return p is null || p.IsNull ? null : p;
        }
    }
}
