// Realistic CK3 log text for the core tests, in the exact formats of PROTOCOL.md §1 as refined by §5 (the mod as implemented) and
// live tests: info_log markers without numbers, info_log_scopes dumps carrying ck3xbl_bid, \x15-wrapped "weak (...)" fragments,
// foreign engine-saved scopes inside dumps, leftover (persisting) ck3xbl_ scopes guarded by sentinels, unrelated engine lines.
namespace CK3xBL.SelfTest;

internal static class CoreFixtures
{
    private const string X = "\u0015";

    public static string Weak(string type, long id) => $"{X}weak ({type} - {id}){X}!";

    public static string Marker(string record, string context = "ck3xbl_snapshot_effect", int line = 40) =>
        $"[19:02:11][I][jomini_effect_impl.cpp:450]: file: common/scripted_effects/ck3xbl_gen_side_effects.txt line: {line} ({context}): CK3XBL|{record}";

    /// <summary>An info_log_scopes dump: current scope line, Root, blank, "Saved event targets:", key: value lines.</summary>
    public static IEnumerable<string> Dump(string current, params (string Key, string Value)[] values)
    {
        yield return $"[19:02:11][I][effectimpl.cpp:642]: {current}";
        yield return $"Root: {current}";
        yield return "";
        yield return "Saved event targets:";
        // Engine-saved scopes of the surrounding context show up too; the parser must ignore them.
        yield return $"governor: Shuzhen_6DD1_73CD Zhào of c_nanxi (Internal ID: 12972 - Historical ID han_3410) {Weak("Character", 12972)}";
        foreach (var (k, v) in values) yield return $"{k}: {v}";
        yield return "";
    }

    /// <summary>A dump of battle 17: ck3xbl_bid is saved first and persists in every later dump (PROTOCOL §5.1).</summary>
    public static IEnumerable<string> Dump17(string current, params (string Key, string Value)[] values) =>
        Dump(current, new[] { ("ck3xbl_bid", "17.00") }.Concat(values).ToArray());

    public const string Combat = "Battle of Orangallu";
    public static string CombatPrint => $"{Combat} {Weak("Combat", 134217751)}";
    public static string Gunda => $"Gunda Kakatiya of c_orangallu (Internal ID: 10811 - Historical ID 189226) {Weak("Character", 10811)}";
    public static string Hepung => $"Rakta Hepung of d_hepung (Internal ID: 20001 - Historical ID 300555) {Weak("Character", 20001)}";
    public static string RaidKnight => $"Bodo Hepung of  (Internal ID: 20002 - Historical ID 300556) {Weak("Character", 20002)}";
    public static string Lowborn => $"Nachana Lowborn of  (Internal ID 23538) {Weak("Character", 23538)}";
    public static string NoCharacter => $"(no character) {Weak("Character", 4294967295)}";
    public static string OrangalluCounty => $"Orangallu (Internal ID: 10110 - Internal Key: c_orangallu) {Weak("Landed_title", 10110)}";
    public static string KakatiyaHouse => $"Kakatiya (Internal ID: 5296 - Internal Key: ) {Weak("Dynasty_house", 5296)}";
    public static string HepungHouse => $"Hepung (Internal ID: 6001 - Internal Key: ) {Weak("Dynasty_house", 6001)}";

    public static string Regiment(string maa, string name, long id, string type = "personal") => type == "title"
        ? $"'TOOLTIP:MEN_AT_ARMS_TYPE,{maa} L; {name}!!' (Internal ID: '{id}' - Type: 'title' - Owner ID: '777' - Controller ID: '777' ) {Weak("Regiment", id)}"
        : $"'TOOLTIP:MEN_AT_ARMS_TYPE,{maa} L; {name}!!' (Internal ID: '{id}' - Type: '{type}' ) {Weak("Regiment", id)}";

    /// <summary>Battle 17 in the order of PROTOCOL §5.2: two attacking armies (the second has no commander, with the first army's
    /// a_cmd still in its dump), one defending army led by the player; regiments incl. a hired one; a lowborn knight whose dump
    /// carries the player's c_title/c_house as leftovers (sentinels 0); noise lines and a foreign dump in between.</summary>
    public static List<string> BattleLog()
    {
        var log = new List<string>
        {
            "[19:02:10][W][pdx_mesh_streamer.cpp:249]: Requesting mesh tgp_great_building_mandala_capital_01_mesh",
            Marker("B|BEGIN", line: 12),
        };
        log.AddRange(Dump17(CombatPrint,
            ("ck3xbl_year", "870.00"), ("ck3xbl_month", "6.00"), ("ck3xbl_day", "14.00"),
            ("ck3xbl_combat", CombatPrint),
            ("ck3xbl_prov", $"Orangallu (Internal ID: 8199) {Weak("Province", 8199)}"),
            ("ck3xbl_county", OrangalluCounty),
            ("ck3xbl_barony", $"Orangallu (Internal ID: 10111 - Internal Key: b_orangallu) {Weak("Landed_title", 10111)}"),
            ("ck3xbl_war", $"Hepung's Raid on Orangallu {Weak("War", 4242)}"),
            ("ck3xbl_player", Gunda)));
        foreach (var l in new[] { "terrain|hills", "winter|mild", "holding|castle_holding", "coastal|no", "riverside|yes", "siege|no",
                     "raid|yes", "occupied|no", "region|india", "building_gfx|indian_building_gfx", "loc_heritage|heritage_dravidian",
                     "loc_culture|telugu", "future_field|whatever" })
            log.Add(Marker("L|" + l, "ck3xbl_gen_emit_location_effect"));
        log.Add(Marker("LV", "ck3xbl_gen_emit_location_effect"));
        log.AddRange(Dump17(CombatPrint, ("ck3xbl_l_fort", "2.00"), ("ck3xbl_l_dev", "14.00"), ("ck3xbl_l_has_origin", "1.00"),
            ("ck3xbl_l_origin", $"Pakhal (Internal ID: 8200) {Weak("Province", 8200)}")));

        // ---- attacker side
        log.Add(Marker("S|att"));
        log.AddRange(Dump17(CombatPrint, ("ck3xbl_s_size", "1925.00"), ("ck3xbl_s_has_primary", "1.00"), ("ck3xbl_s_primary", Hepung),
            ("ck3xbl_s_player_in", "0.00")));
        log.Add(Marker("A|att"));
        log.AddRange(Dump17($"1st Raiders of Hepung {Weak("Army", 16777235)}",
            ("ck3xbl_a_tag", "5.00"), ("ck3xbl_a_size", "1225.00"), ("ck3xbl_a_levies", "900.00"), ("ck3xbl_a_has_cmd", "1.00"),
            ("ck3xbl_a_owner", Hepung), ("ck3xbl_a_cmd", RaidKnight)));
        log.Add(Marker("AX|culture|kirati"));
        log.Add(Marker("AX|heritage|heritage_burman"));
        log.Add(Marker("AX|unit_gfx|indian_unit_gfx"));
        log.Add(Marker("AX|era|culture_era_early_medieval"));
        log.Add(Marker("AX|nomadic|yes"));
        log.Add(Marker("R"));
        log.AddRange(Dump17(Regiment("mangudai", "Mangudai", 16791645),
            ("ck3xbl_r_cur", "300.00"), ("ck3xbl_r_max", "300.00"), ("ck3xbl_r_hired", "0.00"), ("ck3xbl_a_tag", "5.00")));
        // A foreign dump (vanilla debug output) between records: must not be attached to anything.
        log.Add("[19:02:11][I][effectimpl.cpp:642]: empty");
        log.Add("Root: empty");
        log.Add("");
        log.Add("Saved event targets:");
        log.Add($"liege: Sildis Shor of d_shoria (Internal ID: 13650 - Historical ID 303027) {Weak("Character", 13650)}");
        log.Add("");

        // Army 2: no commander, but the dump still holds the previous army's a_cmd (saved scopes persist).
        log.Add(Marker("A|att"));
        log.AddRange(Dump17($"2nd Raiders of Hepung {Weak("Army", 16777236)}",
            ("ck3xbl_a_tag", "6.00"), ("ck3xbl_a_size", "700.00"), ("ck3xbl_a_levies", "500.00"), ("ck3xbl_a_has_cmd", "0.00"),
            ("ck3xbl_a_owner", Hepung), ("ck3xbl_a_cmd", RaidKnight), ("ck3xbl_r_cur", "300.00")));
        log.Add(Marker("AX|culture|kirati"));
        log.Add(Marker("AX|nomadic|no"));
        log.Add(Marker("R"));
        log.AddRange(Dump17(Regiment("armored_footmen", "Armored Footmen", 16706, "title"),
            ("ck3xbl_r_cur", "200.00"), ("ck3xbl_r_max", "200.00"), ("ck3xbl_r_hired", "1.00"), ("ck3xbl_a_tag", "6.00")));
        log.Add(Marker("RX|culture|bengali"));
        log.Add(Marker("RX|heritage|heritage_indo_aryan"));

        log.Add(Marker("C|CMD|att"));
        log.AddRange(Dump17(RaidKnight, CharacterValues(tag: 3, age: 34, female: false, prowess: 14, martial: 9, isPlayer: false, army: 5,
            ("ck3xbl_c_has_title", "0.00"), ("ck3xbl_c_has_house", "1.00"), ("ck3xbl_c_house", HepungHouse))));
        log.Add(Marker("CX|culture|kirati"));
        log.Add(Marker("CX|heritage|heritage_burman"));
        log.Add(Marker("CX|ctrait|reaver"));
        log.Add(Marker("C|PRIMARY|att"));
        log.AddRange(Dump17(Hepung, CharacterValues(tag: 4, age: 51, female: false, prowess: 8, martial: 12, isPlayer: false, army: 0,
            ("ck3xbl_c_has_title", "1.00"), ("ck3xbl_c_title", $"Hepung (Internal ID: 3001 - Internal Key: d_hepung) {Weak("Landed_title", 3001)}"),
            ("ck3xbl_c_has_house", "1.00"), ("ck3xbl_c_house", HepungHouse))));

        // ---- defender side
        log.Add(Marker("S|def"));
        // An unrelated engine line between a marker and its dump (other threads write to the same log).
        log.Add("[19:02:11][W][rivers_ambience_node.cpp:20]: CRiversAmbienceNode::Process(): No sound alias named 'river_node' configured!");
        log.AddRange(Dump17(CombatPrint, ("ck3xbl_s_size", "1700.00"), ("ck3xbl_s_has_primary", "1.00"), ("ck3xbl_s_primary", Gunda),
            ("ck3xbl_s_player_in", "1.00"), ("ck3xbl_a_tag", "6.00")));
        log.Add(Marker("A|def"));
        log.AddRange(Dump17($"Host of Gunda Kakatiya {Weak("Army", 60)}",
            ("ck3xbl_a_tag", "7.00"), ("ck3xbl_a_size", "1700.00"), ("ck3xbl_a_levies", "1275.00"), ("ck3xbl_a_has_cmd", "1.00"),
            ("ck3xbl_a_owner", Gunda), ("ck3xbl_a_cmd", Gunda)));
        log.Add(Marker("AX|culture|telugu"));
        log.Add(Marker("AX|nomadic|no"));
        log.Add(Marker("R"));
        log.AddRange(Dump17(Regiment("armored_footmen", "Armored Footmen", 16707),
            ("ck3xbl_r_cur", "200.00"), ("ck3xbl_r_max", "200.00"), ("ck3xbl_r_hired", "0.00"), ("ck3xbl_a_tag", "7.00")));
        log.Add(Marker("R"));
        log.AddRange(Dump17(Regiment("war_elephant", "War Elephants", 16708),
            ("ck3xbl_r_cur", "25.00"), ("ck3xbl_r_max", "25.00"), ("ck3xbl_r_hired", "0.00"), ("ck3xbl_a_tag", "7.00")));

        log.Add(Marker("C|CMD|def"));
        log.AddRange(Dump17(Gunda, CharacterValues(tag: 1, age: 71, female: false, prowess: 11, martial: 15, isPlayer: true, army: 7,
            ("ck3xbl_c_has_title", "1.00"), ("ck3xbl_c_title", OrangalluCounty), ("ck3xbl_c_has_house", "1.00"), ("ck3xbl_c_house", KakatiyaHouse))));
        log.Add(Marker("CX|culture|telugu"));
        log.Add(Marker("CX|heritage|heritage_dravidian"));
        log.Add(Marker("CX|unit_gfx|indian_unit_gfx"));
        log.Add(Marker("CX|clothing_gfx|indian_clothing_gfx"));
        log.Add(Marker("CX|ethnicity|indian"));
        log.Add(Marker("CX|era|culture_era_early_medieval"));
        log.Add(Marker("CX|trait|scarred"));
        log.Add(Marker("CX|trait|physique_good_2"));
        log.Add(Marker("CX|ctrait|unyielding_defender"));
        log.Add(Marker("CX|ctrait|forder"));
        // A lowborn knight: the c_title / c_house scopes in his dump are leftovers from the player's record (sentinels 0).
        log.Add(Marker("C|KNIGHT|def"));
        log.AddRange(Dump17(Lowborn, CharacterValues(tag: 2, age: 26, female: true, prowess: 17, martial: 4, isPlayer: false, army: 7,
            ("ck3xbl_c_has_title", "0.00"), ("ck3xbl_c_title", OrangalluCounty), ("ck3xbl_c_has_house", "0.00"), ("ck3xbl_c_house", KakatiyaHouse))));
        log.Add(Marker("CX|culture|unknown"));

        log.Add(Marker("B|END", line: 99));
        log.AddRange(Dump17(CombatPrint));
        log.Add("[19:02:12][I][jomini_effect_impl.cpp:450]: file: events/ck3xbl_battle_events.txt line: 30 (ck3xbl_battle.0001:immediate): CK3XBL|SOMETHING_NEW|1|2");
        return log;
    }

    /// <summary>Battle 21 with the knights facts of PROTOCOL §13.2: S keys on both sides (the defenders have no commander, with the
    /// attackers' s_cmd still in their dump), knight effectiveness on two armies (one negative, as computed by the mod; the third army's
    /// value was cleared after its dump), every AX/CX record (incl. a duplicate trait, a lower txp level after a higher one, a tracked
    /// trait, the 4-step the_wake, an accolade and three artifacts) and a character without any of them.</summary>
    public static List<string> KnightsBattleLog()
    {
        var log = new List<string> { Marker("B|BEGIN") };
        log.AddRange(Dump21(CombatPrint, ("ck3xbl_year", "870.00"), ("ck3xbl_player", Gunda)));

        log.Add(Marker("S|att"));
        log.AddRange(Dump21(CombatPrint, ("ck3xbl_s_size", "1925.00"), ("ck3xbl_s_has_primary", "1.00"), ("ck3xbl_s_primary", Hepung),
            ("ck3xbl_s_player_in", "0.00"), ("ck3xbl_s_has_cmd", "1.00"), ("ck3xbl_s_cmd", RaidKnight), ("ck3xbl_s_hostility_out", "2.00")));
        log.Add(Marker("A|att"));
        log.AddRange(Dump21($"1st Raiders of Hepung {Weak("Army", 16777235)}", ("ck3xbl_a_tag", "5.00"), ("ck3xbl_a_size", "1225.00"),
            ("ck3xbl_a_levies", "900.00"), ("ck3xbl_a_has_cmd", "1.00"), ("ck3xbl_a_owner", Hepung), ("ck3xbl_a_cmd", RaidKnight),
            ("ck3xbl_a_ke", "1500.00")));
        foreach (var l in new[] { "culture|kirati", "besieging|yes", "territory|hostile", "otrait|greatest_of_khans", "otrait|greatest_of_khans",
                     "ocparam|formation_fighting_strategist", "ofparam|tenet_mountain_worship_trait_bonuses", "nomadic|no" })
            log.Add(Marker("AX|" + l));
        log.Add(Marker("A|att"));
        log.AddRange(Dump21($"2nd Raiders of Hepung {Weak("Army", 16777236)}", ("ck3xbl_a_tag", "6.00"), ("ck3xbl_a_size", "700.00"),
            ("ck3xbl_a_levies", "700.00"), ("ck3xbl_a_has_cmd", "0.00"), ("ck3xbl_a_owner", Hepung), ("ck3xbl_a_ke", "-200.00")));
        log.Add(Marker("AX|besieging|no"));
        log.Add(Marker("AX|territory|other"));

        log.Add(Marker("C|CMD|att"));
        log.AddRange(Dump21(RaidKnight, CharacterValues(tag: 3, age: 34, female: false, prowess: 14, martial: 9, isPlayer: false, army: 5)));
        foreach (var l in new[] { "culture|kirati", "ctrait|reaver", "ktrait|brave", "ktrait|brave", "txp|forest_fighter|3", "txp|forest_fighter|2",
                     "txp|tourney_participant:foot|2", "cparam|forest_trait_bonuses", "fparam|tenet_mountain_worship_trait_bonuses",
                     "acc_active|yes", "acc_rank|3", "acc_primary|archer_attribute", "acc_type|archer_attribute|2", "acc_type|stalwart_attribute|1",
                     "artifact|primary_armament|sword|famed", "artifact|armor|mail|illustrious", "artifact_helmet|armor_helmet_nasal",
                     "artifact|helmet|unknown|common" })
            log.Add(Marker("CX|" + l));
        log.Add(Marker("C|KNIGHT|att"));
        log.AddRange(Dump21(Lowborn, CharacterValues(tag: 4, age: 26, female: true, prowess: 17, martial: 4, isPlayer: false, army: 6)));
        log.Add(Marker("CX|txp|the_wake|4"));
        log.Add(Marker("CX|acc_active|no"));

        log.Add(Marker("S|def"));
        log.AddRange(Dump21(CombatPrint, ("ck3xbl_s_size", "1700.00"), ("ck3xbl_s_has_primary", "1.00"), ("ck3xbl_s_primary", Gunda),
            ("ck3xbl_s_player_in", "1.00"), ("ck3xbl_s_has_cmd", "0.00"), ("ck3xbl_s_cmd", RaidKnight), ("ck3xbl_s_hostility_out", "0.00")));
        log.Add(Marker("A|def"));
        log.AddRange(Dump21($"Host of Gunda Kakatiya {Weak("Army", 60)}", ("ck3xbl_a_tag", "7.00"), ("ck3xbl_a_size", "1700.00"),
            ("ck3xbl_a_levies", "1275.00"), ("ck3xbl_a_has_cmd", "1.00"), ("ck3xbl_a_owner", Gunda), ("ck3xbl_a_cmd", Gunda)));
        log.Add(Marker("AX|territory|own"));
        log.Add(Marker("C|CMD|def"));
        log.AddRange(Dump21(Gunda, CharacterValues(tag: 1, age: 71, female: false, prowess: 11, martial: 15, isPlayer: true, army: 7)));
        log.Add(Marker("CX|ctrait|unyielding_defender"));

        log.Add(Marker("B|END", line: 99));
        log.AddRange(Dump21(CombatPrint));

        // An army joins later (§5.6): its knights facts come with it.
        log.Add(Marker("J|def", "ck3xbl_on_join"));
        log.AddRange(Dump21($"Levy of Orangallu {Weak("Army", 61)}", ("ck3xbl_a_tag", "8.00"), ("ck3xbl_a_size", "400.00"),
            ("ck3xbl_a_levies", "400.00"), ("ck3xbl_a_has_cmd", "0.00"), ("ck3xbl_a_owner", Gunda), ("ck3xbl_a_ke", "1150.00")));
        log.Add(Marker("AX|besieging|no"));
        log.Add(Marker("AX|territory|own"));
        log.Add(Marker("AX|otrait|excommunicated"));
        log.Add(Marker("C|KNIGHT|def"));
        log.AddRange(Dump21(Lowborn, CharacterValues(tag: 9, age: 26, female: true, prowess: 17, martial: 4, isPlayer: false, army: 8)));
        log.Add(Marker("CX|ktrait|berserker"));
        log.Add(Marker("CX|artifact|primary_armament|unknown|masterwork"));
        log.Add(Marker("CX|gear|ck3xbl_main|vlandia_sword_3_t4"));
        log.Add(Marker("CX|gear|ck3xbl_horse|t2_vlandia_horse"));
        log.Add(Marker("CHOICE|AUTO", "ck3xbl_battle.0001:option"));
        log.AddRange(Dump21(Gunda));
        return log;
    }

    private static IEnumerable<string> Dump21(string current, params (string Key, string Value)[] values) =>
        Dump(current, new[] { ("ck3xbl_bid", "21.00") }.Concat(values).ToArray());

    /// <summary>The original §1.2 grammar (bids in the markers, fort/dev/origin as L lines, no sentinels, a character repeated in
    /// two roles), as emitted by early probe builds. The parser must keep accepting it.</summary>
    public static List<string> LegacyBattleLog()
    {
        var log = new List<string> { Marker("B|BEGIN|5") };
        log.AddRange(Dump(CombatPrint, ("ck3xbl_bid", "5.00"), ("ck3xbl_player", Gunda)));
        log.Add(Marker("L|fort|3"));
        log.Add(Marker("L|dev|21"));
        log.Add(Marker("L|origin|8300"));
        log.Add(Marker("S|def|5"));
        log.AddRange(Dump(CombatPrint, ("ck3xbl_bid", "5.00"), ("ck3xbl_s_size", "900.00"), ("ck3xbl_s_primary", Gunda), ("ck3xbl_s_player_in", "1.00")));
        log.Add(Marker("A|def|5"));
        log.AddRange(Dump($"Host of Gunda Kakatiya {Weak("Army", 60)}", ("ck3xbl_bid", "5.00"), ("ck3xbl_a_tag", "2.00"),
            ("ck3xbl_a_size", "900.00"), ("ck3xbl_a_levies", "900.00"), ("ck3xbl_a_has_cmd", "1.00"), ("ck3xbl_a_owner", Gunda), ("ck3xbl_a_cmd", Gunda)));
        log.Add(Marker("C|CMD|def|5"));
        log.AddRange(Dump(Gunda, CharacterValues(tag: 1, age: 71, female: false, prowess: 11, martial: 15, isPlayer: true, army: 2,
            ("ck3xbl_c_title", OrangalluCounty), ("ck3xbl_c_house", KakatiyaHouse))));
        log.Add(Marker("CX|trait|scarred"));
        log.Add(Marker("C|PRIMARY|def|5"));
        log.AddRange(Dump(Gunda, CharacterValues(tag: 1, age: 71, female: false, prowess: 11, martial: 15, isPlayer: true, army: 2,
            ("ck3xbl_c_title", OrangalluCounty), ("ck3xbl_c_house", KakatiyaHouse))));
        log.Add(Marker("CX|trait|scarred"));
        // No sentinels: the leftover c_title / c_house of the player must be rejected for the landless lowborn knight.
        log.Add(Marker("C|KNIGHT|def|5"));
        log.AddRange(Dump(Lowborn, CharacterValues(tag: 2, age: 26, female: true, prowess: 17, martial: 4, isPlayer: false, army: 2,
            ("ck3xbl_c_title", OrangalluCounty), ("ck3xbl_c_house", KakatiyaHouse))));
        log.Add(Marker("B|END|5"));
        log.AddRange(Dump(CombatPrint, ("ck3xbl_bid", "5.00")));
        return log;
    }

    private static (string, string)[] CharacterValues(int tag, int age, bool female, int prowess, int martial, bool isPlayer, int army,
        params (string, string)[] extra)
    {
        var list = new List<(string, string)>
        {
            ("ck3xbl_c_tag", $"{tag}.00"), ("ck3xbl_c_age", $"{age}.00"), ("ck3xbl_c_female", female ? "1.00" : "0.00"),
            ("ck3xbl_c_prowess", $"{prowess}.00"), ("ck3xbl_c_martial", $"{martial}.00"), ("ck3xbl_c_weight", "-12.00"),
            ("ck3xbl_c_tier", "2.00"), ("ck3xbl_c_is_player", isPlayer ? "1.00" : "0.00"), ("ck3xbl_c_army", $"{army}.00"),
        };
        list.AddRange(extra);
        return list.ToArray();
    }
}
