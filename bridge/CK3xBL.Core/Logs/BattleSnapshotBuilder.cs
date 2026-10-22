// Builds BattleSnapshots from the B|BEGIN ... B|END record sequence (PROTOCOL §1.2 as refined by §5), and joined armies from a J
// record and the lines that follow it (§5.6).
//
// Rules that matter:
//  * Markers carry no numbers (§5.1): the bid is the ck3xbl_bid key of the record's dump. A bid in the marker (the original §1.2
//    form) is still accepted.
//  * Saved scopes persist for the whole effect, so each dump also holds keys of EARLIER records. Each record type reads only its own
//    keys, and optional scopes are read only when their sentinel is 1 (§5.4): s_has_primary, a_has_cmd, c_has_title, c_has_house.
//    Without a sentinel (older mod builds) the title key printed in the character's own name and a house name contained in the
//    printed name are trusted; a stale leftover scope is rejected.
//  * L/CX/AX/RX carry no dump and no bid: they belong to the open snapshot (or the pending join) and attach to the most recent
//    C / A / R record.
//  * R records name their army by ck3xbl_a_tag; when that is missing they belong to the most recent A (or J) record.
//  * Characters are deduplicated by CK3 id per side (the mod lists each character once, older builds could repeat them).
//  * Knights facts (§13.2) are read as logged and never defaulted here: an absent fact stays null/empty, and
//    BattleSnapshot.KnightFactsLogged tells the mapper whether the mod logs them at all.
using CK3xBL.Core.Model;
using Invariant = System.Globalization.CultureInfo;
using NumberStyles = System.Globalization.NumberStyles;

namespace CK3xBL.Core.Logs;

/// <summary>An army that joined a combat after its start (J), with its regiments and commander lines.</summary>
public sealed class JoinedArmy(int bid, SideKey side, ArmySnapshot army, int steerRole)
{
    public readonly int Bid = bid;
    public readonly SideKey Side = side;
    public readonly ArmySnapshot Army = army;
    /// <summary>ck3xbl_j_steer (PROTOCOL §14.5): how the join meets a steered combat; 0 = not steered.</summary>
    public readonly int SteerRole = steerRole;
    public readonly List<CharacterSnapshot> Characters = new();
}

public sealed class BattleSnapshotBuilder
{
    private readonly Action<string> _warn;
    private BattleSnapshot? _current;
    private JoinedArmy? _join;
    private ArmySnapshot? _lastArmy;
    private RegimentSnapshot? _lastRegiment;
    private CharacterSnapshot? _lastCharacter;

    public BattleSnapshotBuilder(Action<string>? warn = null) => _warn = warn ?? (_ => { });

    /// <summary>The bid of the snapshot being built, if any.</summary>
    public int? OpenBid => _current?.Bid;

    public bool HasPendingJoin => _join is not null;

    public void Reset()
    {
        _current = null;
        _join = null;
        ResetCursors();
    }

    /// <summary>Records that make up a snapshot (or the lines following a J).</summary>
    public static bool IsSnapshotRecord(string type) =>
        type is "B" or "L" or "LV" or "S" or "A" or "R" or "C" or "CX" or "AX" or "RX";

    /// <summary>Records that may follow a J and belong to it (§5.6).</summary>
    public static bool IsJoinFollowUp(string type) => type is "AX" or "R" or "RX" or "C" or "CX";

    /// <summary>Feeds one snapshot record. Returns the snapshot when B|END completes it.</summary>
    public BattleSnapshot? Accept(Ck3Record r, string sessionKey)
    {
        switch (r.Type)
        {
            case "B" when r.Arg(0) == "BEGIN": Begin(r, sessionKey); return null;
            case "B" when r.Arg(0) == "END": return End(r);
            case "L": Location(r); return null;
            case "LV": LocationValues(r); return null;
            case "S": Side(r); return null;
            case "A": Army(r); return null;
            case "R": Regiment(r); return null;
            case "C": Character(r); return null;
            case "CX": CharacterFact(r); return null;
            case "AX": ArmyFact(r); return null;
            case "RX":
                if (_lastRegiment is not null) ApplyCulture(_lastRegiment.HiredCulture ??= new CultureInfo(), r);
                return null;
            default: return null;
        }
    }

    /// <summary>Starts collecting a J record's army and its follow-up lines. Returns false when the record has no army dump.</summary>
    public bool BeginJoin(Ck3Record r)
    {
        if (ParseSide(r.Arg(0)) is not { } side || ParseArmy(r, _warn) is not { } army) return false;
        ResetCursors();
        _join = new JoinedArmy(BidOf(r, 1) ?? 0, side, army, r.Dump?.GetInt("ck3xbl_j_steer") ?? 0);
        _lastArmy = army;
        return true;
    }

    /// <summary>The join being collected, which is finished as soon as a record arrives that does not belong to it.</summary>
    public JoinedArmy? TakeJoin()
    {
        var join = _join;
        _join = null;
        if (join is not null) ResetCursors();
        return join;
    }

    // ------------------------------------------------------------------------------------------------------------------ records

    private void Begin(Ck3Record r, string sessionKey)
    {
        int? bid = BidOf(r, 1);
        if (bid is null) { _warn($"B|BEGIN without a bid: {r.Raw}"); return; }
        if (_current is not null) _warn($"battle {_current.Bid} was never closed by B|END; discarding it for {bid}");
        Reset();

        var s = new BattleSnapshot { Bid = bid.Value, SessionKey = sessionKey };
        if (r.Dump is { } d)
        {
            s.Year = d.GetInt("ck3xbl_year") ?? 0;
            s.Month = d.GetInt("ck3xbl_month") ?? 0;
            s.Day = d.GetInt("ck3xbl_day") ?? 0;
            if (d.GetScope("ck3xbl_combat") is { } combat) { s.CombatId = combat.Id; s.CombatName = combat.Name; }
            if (d.GetScope("ck3xbl_prov") is { } prov) { s.Location.ProvinceId = prov.Id; s.Location.ProvinceName = prov.Name; }
            if (d.GetScope("ck3xbl_county") is { } county) { s.Location.CountyKey = county.Key; s.Location.CountyName = county.Name; }
            if (d.GetScope("ck3xbl_barony") is { } barony) s.Location.BaronyKey = barony.Key;
            if (d.GetScope("ck3xbl_war") is { } war) s.WarName = war.Name;
            if (d.GetScope("ck3xbl_player") is { } player) s.PlayerCk3Id = player.Id;
            // A reinforcement clash (PROTOCOL §14.6) names the steering bid it continues; absent or 0 = a first clash.
            s.ParentBid = Math.Max(0, d.GetInt("ck3xbl_parent_bid") ?? 0);
            s.Clash = Math.Max(1, d.GetInt("ck3xbl_clash") ?? 1);
            // A single combat (PROTOCOL §18): two characters, no armies.
            s.IsDuel = d.GetInt("ck3xbl_kind") == 2;
            s.DuelFatality = Math.Clamp(d.GetInt("ck3xbl_d_fatality") ?? 0, 0, 4);
            s.DuelNoble = s.IsDuel && d.GetInt("ck3xbl_d_noble") is { } noble ? noble != 0 : null;
            s.DuelTourney = s.IsDuel ? Math.Clamp(d.GetInt("ck3xbl_d_tourney") ?? 0, 0, 5) : 0;
            // A skirmish on the road (PROTOCOL §20): a small battle of the travellers against a band.
            s.IsSkirmish = d.GetInt("ck3xbl_kind") == 3;
            s.SkirmishKind = s.IsSkirmish ? Math.Clamp(d.GetInt("ck3xbl_sk_kind") ?? 1, 1, 5) : 0;
            // An assault on a besieged holding (PROTOCOL §39).
            s.IsSiege = d.GetInt("ck3xbl_kind") == 4;
            s.IsSeaBattle = d.GetInt("ck3xbl_kind") == 5;
            // A relief (PROTOCOL §40): a field battle against the besiegers of the player's (or a vassal's) holding.
            s.Relief = !s.IsSiege && d.GetInt("ck3xbl_relief") == 1;
            if (s.IsSiege || s.Relief)
            {
                s.SiegeFort = Math.Clamp(d.GetInt("ck3xbl_sg_fort") ?? 1, 0, 30);
                s.SiegeGarrison = Math.Clamp(d.GetInt("ck3xbl_sg_garrison") ?? 0, 0, 20000);
                s.SiegeDays = Math.Clamp(d.GetInt("ck3xbl_sg_days") ?? 0, 0, 3650);
                s.SiegeMen = Math.Max(0, d.GetInt("ck3xbl_sg_men") ?? 0);
            }
        }
        else _warn($"B|BEGIN {bid} has no dump; date, site and player are unknown");
        _current = s;
    }

    private BattleSnapshot? End(Ck3Record r)
    {
        int? bid = BidOf(r, 1);
        if (_current is null) { _warn($"B|END {bid} without an open battle (bridge started mid-snapshot?)"); return null; }
        if (bid is not null && bid != _current.Bid)
        {
            _warn($"B|END {bid} does not match open battle {_current.Bid}; discarding both");
            Reset();
            return null;
        }
        var done = _current;
        done.Complete = true;
        Reset();
        foreach (var side in done.Sides)
            if (side.Armies.Count == 0 && !done.IsDuel) _warn($"battle {done.Bid}: {side.Key} side has no armies");
        return done;
    }

    private void Location(Ck3Record r)
    {
        if (_current is null || r.Args.Length < 2) return;
        SetLocationFact(_current.Location, r.Args[0], string.Join("|", r.Args.Skip(1)));
    }

    /// <summary>LV (§5.3): fort, development and attacker origin as numbers, stored like the L fields they replace.</summary>
    private void LocationValues(Ck3Record r)
    {
        if (OpenFor(r, -1) is not { } s || r.Dump is not { } d) return;
        if (d.GetInt("ck3xbl_l_fort") is { } fort) SetLocationFact(s.Location, "fort", Text(fort));
        if (d.GetInt("ck3xbl_l_dev") is { } dev) SetLocationFact(s.Location, "dev", Text(dev));
        long origin = d.GetBool("ck3xbl_l_has_origin") ? d.GetScope("ck3xbl_l_origin")?.Id ?? 0 : 0;
        SetLocationFact(s.Location, "origin", Text(origin));
        // PROTOCOL §44: the attackers came straight off the sea (their origin is a sea province).
        s.Landing = origin > 0 && d.GetInt("ck3xbl_l_landing") == 1;
    }

    private static void SetLocationFact(LocationInfo loc, string field, string value)
    {
        loc.Facts[field] = value;
        switch (field)
        {
            case "terrain": loc.Terrain = value; break;
            case "winter": loc.Winter = value; break;
            case "holding": loc.Holding = value; break;
            case "coastal": loc.Coastal = IsYes(value); break;
            case "riverside": loc.Riverside = IsYes(value); break;
            case "siege": loc.Siege = IsYes(value); break;
            case "fort": loc.FortLevel = (int)ParseNumber(value); break;
            case "dev": loc.Development = (int)ParseNumber(value); break;
            case "origin":
                long origin = ParseNumber(value);
                loc.AttackerOriginProvinceId = origin > 0 ? origin : null;
                break;
        }
    }

    private void Side(Ck3Record r)
    {
        if (OpenFor(r, 1) is not { } s || ParseSide(r.Arg(0)) is not { } key || r.Dump is not { } d) return;
        var side = s.Side(key);
        side.Size = d.GetInt("ck3xbl_s_size") ?? side.Size;
        side.PlayerIn = d.GetBool("ck3xbl_s_player_in");
        if (Sentinel(d, "ck3xbl_s_has_primary") && d.GetScope("ck3xbl_s_primary") is { } primary) side.Primary = Ref(primary);
        // The knights pass (PROTOCOL §13.2): the sentinel's presence marks a mod that logs it; the commander scope persists into later
        // dumps, so it is read only when this side's sentinel is 1.
        if (d.Has("ck3xbl_s_has_cmd"))
        {
            s.KnightFactsLogged = true;
            side.FaithHostilityOut = d.GetInt("ck3xbl_s_hostility_out");
            if (d.GetBool("ck3xbl_s_has_cmd") && d.GetScope("ck3xbl_s_cmd") is { } cmd) side.Commander = Ref(cmd);
        }
    }

    private void Army(Ck3Record r)
    {
        if (OpenFor(r, 1) is not { } s || ParseSide(r.Arg(0)) is not { } key) return;
        var army = ParseArmy(r, _warn);
        if (army is null) return;
        var side = s.Side(key);
        var existing = army.Tag != 0 ? side.Armies.Find(a => a.Tag == army.Tag) : null;
        _lastRegiment = null;
        if (existing is not null) { _lastArmy = existing; return; }
        side.Armies.Add(army);
        _lastArmy = army;
    }

    /// <summary>Parses an A or J record's dump into an army.</summary>
    internal static ArmySnapshot? ParseArmy(Ck3Record r, Action<string> warn)
    {
        if (r.Dump is not { } d) { warn($"{r.Raw}: army record without dump"); return null; }
        var army = new ArmySnapshot
        {
            Tag = d.GetInt("ck3xbl_a_tag") ?? 0,
            Size = d.GetInt("ck3xbl_a_size") ?? 0,
            Levies = d.GetInt("ck3xbl_a_levies") ?? 0,
            // Raw, as logged (it may be <= 0): the mapper clamps it, and null stays "not logged".
            KnightEffectivenessPermille = d.GetInt("ck3xbl_a_ke"),
            Development = d.GetInt("ck3xbl_a_dev") is >= 0 and var dev ? dev : null,
        };
        if (d.CurrentScope is { Type: "Army" } print) { army.ArmyId = print.Id; army.Name = print.Name; }
        if (d.GetScope("ck3xbl_a_owner") is { } owner) army.Owner = Ref(owner);
        if (d.GetBool("ck3xbl_a_has_cmd") && d.GetScope("ck3xbl_a_cmd") is { } cmd) army.Commander = Ref(cmd);
        if (army.Tag == 0) warn($"{r.Raw}: army {army.ArmyId} has no ck3xbl_a_tag; results cannot be applied to it");
        return army;
    }

    private void Regiment(Ck3Record r)
    {
        if (r.Dump is not { } d) return;
        var snapshot = OpenFor(r, 0);
        if (snapshot is null && _join is null) return;

        var reg = new RegimentSnapshot
        {
            Current = d.GetInt("ck3xbl_r_cur") ?? 0,
            Max = d.GetInt("ck3xbl_r_max") ?? 0,
            Hired = d.GetBool("ck3xbl_r_hired"),
        };
        if (d.CurrentScope is { Type: "Regiment" } print)
        {
            reg.RegimentId = print.Id;
            reg.MaaKey = print.MaaKey ?? "";
            reg.RegimentType = print.RegimentType ?? "unknown";
        }
        if (reg.MaaKey.Length == 0) _warn($"{r.Raw}: regiment {reg.RegimentId} without a MaA type");

        int tag = d.GetInt("ck3xbl_a_tag") ?? 0;
        var army = snapshot is null
            ? _join!.Army
            : (tag != 0 ? snapshot.Sides.SelectMany(x => x.Armies).FirstOrDefault(a => a.Tag == tag) : null) ?? _lastArmy;
        if (army is null) { _warn($"{r.Raw}: regiment {reg.RegimentId} has no army to belong to"); return; }
        if (reg.RegimentId != 0 && army.Regiments.Any(x => x.RegimentId == reg.RegimentId)) return;
        army.Regiments.Add(reg);
        _lastRegiment = reg;
    }

    private void Character(Ck3Record r)
    {
        var snapshot = OpenFor(r, 2);
        if ((snapshot is null && _join is null) || ParseSide(r.Arg(1)) is not { } key) return;
        if (r.Dump is not { } d || d.CurrentScope is not { Type: "Character" } print)
        {
            _warn($"{r.Raw}: character record without a character dump");
            _lastCharacter = null;
            return;
        }
        string role = r.Arg(0).ToUpperInvariant();
        var list = snapshot is not null ? snapshot.Side(key).Characters : _join!.Characters;
        var c = list.Find(x => x.Ref.Ck3Id == print.Id);
        if (c is null)
        {
            c = new CharacterSnapshot
            {
                Ref = Ref(print),
                Role = role,
                Tag = d.GetInt("ck3xbl_c_tag") ?? 0,
                Age = d.GetInt("ck3xbl_c_age") ?? 0,
                Female = d.GetBool("ck3xbl_c_female"),
                Prowess = d.GetInt("ck3xbl_c_prowess") ?? 0,
                Martial = d.GetInt("ck3xbl_c_martial") ?? 0,
                Health = d.GetDouble("ck3xbl_c_health"),
                Weight = d.GetInt("ck3xbl_c_weight") ?? 0,
                Tier = d.GetInt("ck3xbl_c_tier") ?? 0,
                ArmyTag = d.GetInt("ck3xbl_c_army") ?? 0,
                PrimaryTitleKey = FreshTitleKey(print, d),
                HouseName = FreshHouseName(print, d),
            };
            list.Add(c);
            if (c.Tag == 0) _warn($"{r.Raw}: character {print.Id} has no ck3xbl_c_tag; its fate cannot be applied");
        }
        else if (c.ArmyTag == 0) c.ArmyTag = d.GetInt("ck3xbl_c_army") ?? 0;

        switch (role)
        {
            case "CMD": c.IsCommander = true; break;
            case "KNIGHT": c.IsKnight = true; break;
            case "PRIMARY": c.IsPrimary = true; break;
            case "PLAYER": c.IsPlayer = true; break;
        }
        if (d.GetBool("ck3xbl_c_is_player")) c.IsPlayer = true;
        // PROTOCOL §15: whether the player is physically in the combat. Kept for the player's record only: a dump can carry saved
        // values of earlier records of the same chain (live test).
        if (c.IsPlayer && d.Has("ck3xbl_c_absent")) c.Absent = d.GetBool("ck3xbl_c_absent");
        _lastCharacter = c;
    }

    private void CharacterFact(Ck3Record r)
    {
        if (_lastCharacter is not { } c || r.Args.Length < 2) return;
        string value = string.Join("|", r.Args.Skip(1));
        switch (r.Args[0])
        {
            case "ethnicity": c.Ethnicity = value; break;
            case "trait": AddOnce(c.Traits, value); break;
            case "ctrait": AddOnce(c.CommanderTraits, value); break;
            case "ktrait": AddOnce(c.CombatTraits, value); break;
            case "cparam": AddOnce(c.CultureParams, value); break;
            case "fparam": AddOnce(c.FaithParams, value); break;
            case "txp": TraitLevel(c, r); break;
            case "acc_active": (c.Accolade ??= new AccoladeSnapshot()).Active = IsYes(value); break;
            case "acc_rank":
                if (int.TryParse(value, NumberStyles.Integer, Invariant.InvariantCulture, out int rank)) (c.Accolade ??= new AccoladeSnapshot()).Rank = rank;
                else Malformed(r);
                break;
            case "acc_primary": (c.Accolade ??= new AccoladeSnapshot()).Primary = value; break;
            case "acc_type":
                if (r.Args.Length == 3 && int.TryParse(r.Args[2], NumberStyles.Integer, Invariant.InvariantCulture, out int level))
                    (c.Accolade ??= new AccoladeSnapshot()).Levels[r.Args[1]] = level;
                else Malformed(r);
                break;
            case "artifact":
                if (r.Args.Length == 4) c.Artifacts.Add(new ArtifactSnapshot { SlotType = r.Args[1], Type = r.Args[2], Rarity = r.Args[3] });
                else Malformed(r);
                break;
            case "gear":
                if (r.Args.Length == 3 && r.Args[1].StartsWith("ck3xbl_", StringComparison.Ordinal) && r.Args[2].Length > 0)
                    c.Gear.Add(new GearSnapshot { Slot = r.Args[1], ItemId = r.Args[2] });
                else Malformed(r);
                break;
            case "artifact_helmet":
                // Follows the armor line it describes; a helmet shape without one has nothing to attach to.
                if (c.Artifacts.FindLast(a => a.SlotType == "armor") is { } armor) armor.HelmetFeature = value;
                else _warn($"{r.Raw}: helmet shape without an armor artifact before it; ignored");
                break;
            default: ApplyCulture(c.Culture, r); break;
        }
    }

    /// <summary>CX|txp|&lt;trait&gt;[:&lt;track&gt;]|&lt;level&gt;: the highest level logged for a trait (track) wins.</summary>
    private void TraitLevel(CharacterSnapshot c, Ck3Record r)
    {
        if (r.Args.Length != 3 || r.Args[1].Length == 0 ||
            !int.TryParse(r.Args[2], NumberStyles.Integer, Invariant.InvariantCulture, out int level) || level < 1)
        {
            Malformed(r);
            return;
        }
        string key = r.Args[1];
        c.TraitLevels[key] = c.TraitLevels.TryGetValue(key, out int existing) ? Math.Max(existing, level) : level;
    }

    private void ArmyFact(Ck3Record r)
    {
        if (_lastArmy is not { } army || r.Args.Length < 2) return;
        string value = r.Args[1];
        switch (r.Args[0])
        {
            case "nomadic": army.OwnerNomadic = IsYes(value); break;
            case "besieging": army.Besieging = IsYes(value); break;
            case "territory":
                if (value is "own" or "hostile" or "other") army.Territory = value;
                else Malformed(r);
                break;
            case "otrait": AddOnce(army.OwnerTraits, value); break;
            case "ocparam": AddOnce(army.OwnerCultureParams, value); break;
            case "ofparam": AddOnce(army.OwnerFaithParams, value); break;
            default: ApplyCulture(army.OwnerCulture, r); break;
        }
    }

    private void Malformed(Ck3Record r) => _warn($"{r.Raw}: malformed {r.Type}|{r.Arg(0)} line; ignored");

    private static void ApplyCulture(CultureInfo culture, Ck3Record r)
    {
        if (r.Args.Length < 2) return;
        string value = r.Args[1];
        switch (r.Args[0])
        {
            case "culture": culture.Culture = value; break;
            case "heritage": culture.Heritage = value; break;
            case "unit_gfx": culture.UnitGfx = value; break;
            case "clothing_gfx": culture.ClothingGfx = value; break;
            case "era": culture.Era = value; break;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------ helpers

    private void ResetCursors()
    {
        _lastArmy = null;
        _lastRegiment = null;
        _lastCharacter = null;
    }

    /// <summary>The record's bid: ck3xbl_bid in its dump (§5.1), else a number in marker argument <paramref name="markerArg"/>.</summary>
    internal static int? BidOf(Ck3Record r, int markerArg) =>
        r.Dump?.GetInt("ck3xbl_bid") ?? (markerArg >= 0 ? r.IntArg(markerArg) : null);

    /// <summary>The open snapshot if the record's bid (when it has one) matches it.</summary>
    private BattleSnapshot? OpenFor(Ck3Record r, int markerArg)
    {
        if (_current is null) return null;
        int? bid = BidOf(r, markerArg);
        if (bid is not null && bid != _current.Bid)
        {
            _warn($"{r.Raw}: bid {bid} does not match open battle {_current.Bid}; ignored");
            return null;
        }
        return _current;
    }

    internal static SideKey? ParseSide(string s) => s.ToLowerInvariant() switch
    {
        "att" or "attacker" => SideKey.Attacker,
        "def" or "defender" => SideKey.Defender,
        _ => null,
    };

    internal static CharacterRef Ref(ScopePrint p) => new()
    {
        Ck3Id = p.Id,
        HistoricalId = p.HistoricalId,
        Name = p.Name,
        TitleKey = p.TitleKey,
    };

    /// <summary>True when the optional scope guarded by the sentinel may be read: sentinel = 1, or no sentinel at all.</summary>
    internal static bool Sentinel(ScopeDump d, string sentinel) => !d.Has(sentinel) || d.GetBool(sentinel);

    /// <summary>The primary title key: the one printed in the character's own name ("X of c_foo") is always fresh; otherwise the
    /// saved c_title scope, but only when its sentinel says it belongs to this character.</summary>
    private static string? FreshTitleKey(ScopePrint print, ScopeDump d)
    {
        if (print.TitleKey is not null) return print.TitleKey;
        return d.Has("ck3xbl_c_has_title") && d.GetBool("ck3xbl_c_has_title") ? d.GetScope("ck3xbl_c_title")?.Key : null;
    }

    /// <summary>The house name: decided by the ck3xbl_c_has_house sentinel when present; without it, trusted only when the printed
    /// character name contains it (CK3 prints "First House"), which rejects a stale house left over from the previous character.</summary>
    private static string? FreshHouseName(ScopePrint print, ScopeDump d)
    {
        var house = d.GetScope("ck3xbl_c_house");
        if (house is null || house.Name.Length == 0) return null;
        if (d.Has("ck3xbl_c_has_house")) return d.GetBool("ck3xbl_c_has_house") ? house.Name : null;
        return print.Name.Contains(house.Name, StringComparison.OrdinalIgnoreCase) ? house.Name : null;
    }

    private static void AddOnce(List<string> list, string value)
    {
        if (!list.Contains(value)) list.Add(value);
    }

    private static bool IsYes(string v) => v.Equals("yes", StringComparison.OrdinalIgnoreCase) || v == "1";

    private static long ParseNumber(string v) =>
        double.TryParse(v, NumberStyles.Float, Invariant.InvariantCulture, out var d) ? (long)Math.Round(d, MidpointRounding.AwayFromZero) : 0;

    private static string Text(long v) => v.ToString(Invariant.InvariantCulture);
}
