// Character names as CK3 shows them (PROTOCOL §1.1): a scope print names a character by the key of the first name and the house
// in the game language ("Kin_tsuna Токудайдзи", "Kamokutaynu Худородность"). The key is looked up in CK3's own name files of
// the game language (game/localization/<language>/names), and the "Lowborn" house of the houseless is dropped, so Bannerlord
// and the cards show "Кинцуна Токудайдзи" and "Камокутайну" in a Russian CK3 and "Kin'tsuna" in an English one.
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CK3xBL.Core.Model;

namespace CK3xBL.Core.Ck3;

public sealed partial class Ck3Names
{
    private readonly string? _ck3Dir;
    private readonly Func<string> _language;
    private readonly ConcurrentDictionary<string, Table> _tables = new();

    /// <summary>The house CK3 prints for the houseless, in every language CK3 ships (core_l_*.yml LOWBORN).</summary>
    private static readonly HashSet<string> Lowborn = new(StringComparer.OrdinalIgnoreCase) { "Lowborn", "Худородность" };

    /// <param name="ck3Dir">The CK3 install (holding game/localization); null or missing keeps the printed names.</param>
    /// <param name="language">The bridge language now ("ru" or "en"), asked at every lookup.</param>
    public Ck3Names(string? ck3Dir, Func<string> language)
    {
        _ck3Dir = ck3Dir;
        _language = language;
    }

    /// <summary>Names from a fixed table (tests).</summary>
    public Ck3Names(IReadOnlyDictionary<string, string> names) : this(null, () => "test")
    {
        _tables["test"] = new Table(new Dictionary<string, string>(names, StringComparer.Ordinal));
    }

    /// <summary>"Kin_tsuna Токудайдзи" -> "Кинцуна Токудайдзи"; "Kamokutaynu Худородность" -> "Камокутайну". A first word that is no
    /// name key is kept as printed.</summary>
    public string Localize(string printed)
    {
        if (string.IsNullOrWhiteSpace(printed)) return printed;
        string name = printed.Trim();
        int space = name.IndexOf(' ');
        string first = space < 0 ? name : name[..space];
        string rest = space < 0 ? "" : name[(space + 1)..].Trim();
        if (Lowborn.Contains(rest)) rest = "";
        if (TableNow().Names.TryGetValue(first, out var shown) && shown.Length > 0) first = shown;
        return rest.Length > 0 ? $"{first} {rest}" : first;
    }

    /// <summary>Every character name of a snapshot, in place.</summary>
    public void Localize(BattleSnapshot s)
    {
        foreach (var side in new[] { s.Attacker, s.Defender })
        {
            Localize(side.Primary);
            Localize(side.Commander);
            foreach (var a in side.Armies) Localize(a);
            foreach (var c in side.Characters) Localize(c.Ref);
        }
    }

    public void Localize(ArmySnapshot a)
    {
        Localize(a.Owner);
        Localize(a.Commander);
    }

    public void Localize(CharacterRef? r)
    {
        if (r is not null) r.Name = Localize(r.Name);
    }

    private Table TableNow()
    {
        string lang = _language();
        return _tables.GetOrAdd(lang, l => Load(_ck3Dir, l));
    }

    private static Table Load(string? ck3Dir, string lang)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        string folder = lang == Ck3Language.Russian ? "russian" : "english";
        string? dir = ck3Dir is null ? null : Path.Combine(ck3Dir, "game", "localization", folder, "names");
        if (dir is null || !Directory.Exists(dir)) return new Table(names);
        foreach (var file in Directory.EnumerateFiles(dir, "*.yml"))
        {
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    var m = Entry().Match(line);
                    if (m.Success) names.TryAdd(m.Groups["key"].Value, m.Groups["value"].Value);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return new Table(names);
    }

    private sealed record Table(Dictionary<string, string> Names);

    //  Kin_tsuna: "Кинцуна" #公綱      Yoritomo:0 "Yoritomo"
    [GeneratedRegex("""^\s*(?<key>[^\s:#"]+):\d*\s*"(?<value>[^"]*)"\s*(#.*)?$""")]
    private static partial Regex Entry();
}
