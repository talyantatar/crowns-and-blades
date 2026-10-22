#!/usr/bin/env node
// Writes ck3_battle_game.log: what CK3's logs/game.log holds for two player battles with the ck3xbl mod, record for record in
// the order the mod's scripts emit them (docs/PROTOCOL.md §1 as refined by §5 and §10). The end-to-end self test
// (IntegrationTests.cs) feeds it through the bridge pipeline and checks it against the mod, so regenerate it after changing
// the mod's records: node bridge/CK3xBL.SelfTest/fixtures/make_ck3_battle_game_log.mjs
// It also writes ck3_battle_clock_game.log: three steered battles of the battle clock (PROTOCOL §14)
// in the same format, from the mod's ST|* literals and dump keys (see the story before that part), and
// ck3_stand_in_game.log: one snapshot whose player is absent and emitted as C|PLAYER (PROTOCOL §15, ck3xbl_c_absent).
//
// Realism, taken from a real CK3 game.log (CK3 1.20.0.3, release mode):
//  * markers: "[hh:mm:ss][I][jomini_effect_impl.cpp:450]: file: <mod file> line: <n> (<context>): CK3XBL|..." where <mod file>
//    and <n> locate the info_log statement in ck3_mod/ck3xbl (looked up here, so a marker that is not a literal of the mod
//    fails right away); contexts as the engine prints them: on_action name, "<event>:option", "<scripted gui>:effect";
//  * info_log_scopes dumps: "[..][I][effectimpl.cpp:642]: <current scope>", "Root: <root>", a blank line, "Saved event
//    targets:", "key: value" lines (engine-saved foreign targets first, values with two decimals), optionally a blank line,
//    "Saved list targets:" and list members without a key; "weak (Type - id)" wrapped in \x15 control bytes;
//  * saved scopes persist for the whole effect (and travel along the event chain), so every dump repeats the keys of the
//    records before it, with their latest values.
//
// Story: battle 3 at Orangallu (871.2.14), the player Gunda Kakatiya defends with his host and an ally's army against Bhupati
// Phoro (with a hired regiment and an onager); the player fights it in Bannerlord (CHOICE|FIELD), the first keypad payload is
// rejected (IN|BAD) and the resend accepted (IN|OK), the result is applied (RES|...) and CK3 ends the combat (END|...).
// Battle 4 at Medak: the player is only the war leader, lets CK3 decide (CHOICE|AUTO), his host joins later (J) and CK3 ends it.
// Knights facts (PROTOCOL §13.2): Bhupati besieges Orangallu in hostile land with
// knight effectiveness 150 %; both side commanders and their faith hostility; Prola is an acclaimed archer knight; Mallaya wears a
// famed sword and illustrious mail with a nasal helmet; Rudra is brave with forest_fighter at level 3; Kesava has the_wake at 4.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { encode } from '../../../tools/gen/keypad_payload.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(HERE, '..', '..', '..');
const MOD = path.join(REPO, 'ck3_mod', 'ck3xbl');
const OUT = path.join(HERE, 'ck3_battle_game.log');
const CLOCK_OUT = path.join(HERE, 'ck3_battle_clock_game.log');

// ------------------------------------------------------------------------------------------------ the mod's info_log literals

/** record text after "CK3XBL|" -> [{ file, line }] for every `info_log = "CK3XBL|..."` statement of the mod */
function indexLiterals() {
  const index = new Map();
  const walk = (dir) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, e.name);
      if (e.isDirectory()) walk(full);
      else if (e.name.endsWith('.txt')) {
        const rel = path.relative(MOD, full).split(path.sep).join('/');
        fs.readFileSync(full, 'utf8').split(/\r?\n/).forEach((text, i) => {
          for (const m of text.matchAll(/\binfo_log = "CK3XBL\|([^"]*)"/g)) {
            if (!index.has(m[1])) index.set(m[1], []);
            index.get(m[1]).push({ file: rel, line: i + 1 });
          }
        });
      }
    }
  };
  walk(MOD);
  return index;
}

const LITERALS = indexLiterals();

/** Every name the mod saves (save_scope_as, save_scope_value_as and their temporary forms): the only keys a dump can carry. */
function savedNames() {
  const names = new Set();
  const walk = (dir) => {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, e.name);
      if (e.isDirectory()) walk(full);
      else if (e.name.endsWith('.txt')) {
        const text = fs.readFileSync(full, 'utf8');
        for (const m of text.matchAll(/\bsave_(?:temporary_)?scope_as = (\w+)|\bsave_(?:temporary_)?scope_value_as = \{\s*name = (\w+)/g)) names.add(m[1] || m[2]);
      }
    }
  };
  walk(MOD);
  return names;
}

const SAVED = savedNames();

/** The info_log statement of `rec`; `preferFile` picks a file ("<part of the path>" or "<part>#<n>" for its n-th statement). */
function locate(rec, preferFile) {
  const places = LITERALS.get(rec);
  if (!places) throw new Error(`'CK3XBL|${rec}' is not an info_log literal of the mod`);
  if (!preferFile) return places[0];
  const [part, nth] = preferFile.split('#');
  const inFile = places.filter((p) => p.file.includes(part));
  const at = inFile[nth ? Number(nth) - 1 : 0];
  if (preferFile.includes('#') && !at) throw new Error(`'CK3XBL|${rec}' has no statement ${preferFile}`);
  return at || places[0];
}

// ------------------------------------------------------------------------------------------------ scope prints

const X = '\x15';
const weak = (type, id) => `${X}weak (${type} - ${id})${X}!`;
const character = (name, titleKey, id, historicalId) =>
  `${name} of ${titleKey} (${historicalId ? `Internal ID: ${id} - Historical ID ${historicalId}` : `Internal ID ${id}`}) ${weak('Character', id)}`;
const title = (name, id, key) => `${name} (Internal ID: ${id} - Internal Key: ${key}) ${weak('Landed_title', id)}`;
const house = (name, id) => `${name} (Internal ID: ${id} - Internal Key: ) ${weak('Dynasty_house', id)}`;
const province = (name, id) => `${name} (Internal ID: ${id}) ${weak('Province', id)}`;
const armyPrint = (name, id) => `${name} ${weak('Army', id)}`;
const regimentPrint = (maa, name, id) => `'TOOLTIP:MEN_AT_ARMS_TYPE,${maa} L; ${name}!!' (Internal ID: '${id}' - Type: 'personal' ) ${weak('Regiment', id)}`;
const combatPrint = (name, id) => `${name} ${weak('Combat', id)}`;
const sidePrint = (name, id) => `${name} ${weak('Combat_side', id)}`;
const warPrint = (name, id) => `${name} ${weak('War', id)}`;

// ------------------------------------------------------------------------------------------------ the people and places

const C = {
  gunda: { print: character('Gunda Kakatiya', 'c_orangallu', 10811, '189226'), tag: 1, age: 71, prowess: 9, martial: 14, weight: 18, tier: 2,
    title: title('Orangallu', 10110, 'c_orangallu'), house: house('Kakatiya', 5301) },
  prola: { print: character('Prola Kakatiya', '', 10845, '189229'), tag: 2, age: 34, prowess: 18, martial: 10, weight: 6, tier: 0,
    house: house('Kakatiya', 5301),
    knights: ['acc_active|yes', 'acc_rank|3', 'acc_primary|archer_attribute', 'acc_type|valiant_attribute|1', 'acc_type|archer_attribute|2'] },
  bhupati: { print: character('Bhupati Phoro', 'd_vengi', 10935, '189301'), tag: 5, age: 38, prowess: 12, martial: 11, weight: -4, tier: 3,
    title: title('Vengi', 3612, 'd_vengi'), house: house('Phoro', 5340),
    knights: ['ktrait|education_martial_4'], owner: ['otrait|education_martial_4'] },
  kesava: { print: character('Kesava Lowborn', '', 23611), tag: 6, age: 29, prowess: 16, martial: 6, weight: 0, tier: 0,
    knights: ['ktrait|the_wake', 'txp|the_wake|4'] },
  mallaya: { print: character('Mallaya Phoro', '', 16210, '189302'), tag: 7, age: 24, prowess: 13, martial: 7, weight: 10, tier: 0,
    house: house('Phoro', 5340),
    knights: ['artifact|primary_armament|sword|famed', 'artifact|armor|armor_mail|illustrious', 'artifact_helmet|armor_helmet_nasal'] },
  rudra: { print: character('Rudra Lowborn', '', 23540), tag: 8, age: 41, prowess: 15, martial: 9, weight: 24, tier: 0,
    knights: ['ktrait|brave', 'txp|forest_fighter|3'] },
  beta: { print: character('Beta Kakatiya', 'c_mahur', 10846, '189230'), tag: 9, age: 66, prowess: 7, martial: 12, weight: 5, tier: 2,
    title: title('Mahur', 10130, 'c_mahur'), house: house('Kakatiya', 5301) },
};

const CULTURE = {
  telugu: ['culture|telugu', 'heritage|heritage_dravidian', 'unit_gfx|indian_unit_gfx', 'clothing_gfx|indian_clothing_gfx', 'era|culture_era_early_medieval'],
  kannada: ['culture|kannada', 'heritage|heritage_dravidian', 'unit_gfx|indian_unit_gfx', 'clothing_gfx|indian_clothing_gfx', 'era|culture_era_early_medieval'],
};
// AX lines carry no clothing_gfx (PROTOCOL §1.2, §5.5); RX lines only culture, heritage and unit_gfx.
const armyCulture = (c) => CULTURE[c].filter((l) => !l.startsWith('clothing_gfx'));
const hiredCulture = (c) => CULTURE[c].filter((l) => /^(culture|heritage|unit_gfx)\|/.test(l));

// ------------------------------------------------------------------------------------------------ log writer

const lines = [];
let clock = 0;
const at = (hh, mm, ss) => { clock = hh * 3600 + mm * 60 + ss; };
const pad = (n) => String(n).padStart(2, '0');
const stamp = () => `[${pad(Math.floor(clock / 3600))}:${pad(Math.floor(clock / 60) % 60)}:${pad(clock % 60)}]`;
const engine = (level, source, text) => lines.push(`${stamp()}[${level}][${source}]: ${text}`);
const value = (v) => (typeof v === 'number' ? v.toFixed(2) : v);

/** One effect execution: saved scopes persist until it ends (and are handed along an event chain). */
class Run {
  constructor(root, context, { saved = [], foreign = [], lists = [] } = {}) {
    this.root = root;
    this.context = context;
    this.saved = new Map(saved);
    this.foreign = foreign;
    this.lists = lists;
  }

  save(key, v) {
    if (key.startsWith('ck3xbl_') && !SAVED.has(key)) throw new Error(`${key} is not a name the mod saves`);
    this.saved.set(key, value(v));
    return this;
  }

  /** clear_saved_scope: the key leaves the saved targets. */
  clear(key) {
    this.saved.delete(key);
    return this;
  }

  marker(rec, preferFile) {
    const where = locate(rec, preferFile);
    lines.push(`${stamp()}[I][jomini_effect_impl.cpp:450]: file: ${where.file} line: ${where.line} (${this.context}): CK3XBL|${rec}`);
  }

  record(rec, current, preferFile) {
    this.marker(rec, preferFile);
    lines.push(`${stamp()}[I][effectimpl.cpp:642]: ${current}`, `Root: ${this.root}`, '', 'Saved event targets:');
    for (const [k, v] of this.foreign) lines.push(`${k}: ${v}`);
    for (const [k, v] of this.saved) lines.push(`${k}: ${v}`);
    if (this.lists.length) {
      lines.push('', 'Saved list targets:');
      for (const [name, members] of this.lists) lines.push(`${name}: `, ...members);
    }
  }

  facts(prefix, list) {
    for (const f of list) this.marker(`${prefix}|${f}`);
  }
}

// ------------------------------------------------------------------------------------------------ the snapshot (ck3xbl_snapshot_effect)

function characterRecord(run, role, sideTag, c, armyTag, isPlayer, culture, extra = [], preferFile = 'ck3xbl_gen_side_effects') {
  run.save('ck3xbl_c_tag', c.tag).save('ck3xbl_c_age', c.age).save('ck3xbl_c_female', 0).save('ck3xbl_c_prowess', c.prowess)
    .save('ck3xbl_c_martial', c.martial).save('ck3xbl_c_weight', c.weight).save('ck3xbl_c_tier', c.tier)
    .save('ck3xbl_c_is_player', isPlayer ? 1 : 0).save('ck3xbl_c_army', armyTag);
  // PROTOCOL §15: 1 when the character is not physically in the battle (no tagged army of it here; the fixture's players with an
  // army at the site always command it).
  run.save('ck3xbl_c_absent', armyTag ? 0 : 1);
  run.save('ck3xbl_c_has_title', c.title ? 1 : 0);
  if (c.title) run.save('ck3xbl_c_title', c.title);
  run.save('ck3xbl_c_has_house', c.house ? 1 : 0);
  if (c.house) run.save('ck3xbl_c_house', c.house);
  run.record(`C|${role}|${sideTag}`, c.print, preferFile);
  run.facts('CX', CULTURE[culture]);
  run.facts('CX', ['ethnicity|south_indian', ...extra]);
  // combat traits and levels, then the accolade, then the artifacts (ck3xbl_emit_cx_effect)
  run.facts('CX', c.knights || []);
}

function armyRecords(run, rec, a) {
  run.save('ck3xbl_a_tag', a.tag).save('ck3xbl_a_size', a.size)
    .save('ck3xbl_a_levies', a.size - a.regiments.reduce((n, r) => n + r.cur, 0))
    .save('ck3xbl_a_owner', a.owner.print).save('ck3xbl_a_has_cmd', a.commander ? 1 : 0);
  if (a.commander) run.save('ck3xbl_a_cmd', a.commander.print);
  // The owner's knight effectiveness rides on this dump only: the record clears it right after (clear_saved_scope).
  run.save('ck3xbl_a_ke', a.ke);
  run.record(rec, armyPrint(a.name, a.id), rec.startsWith('J') ? 'ck3xbl_snapshot_effects' : 'ck3xbl_gen_side_effects');
  run.clear('ck3xbl_a_ke');
  run.facts('AX', [`besieging|${a.besieging ? 'yes' : 'no'}`, `territory|${a.territory}`]);
  run.facts('AX', armyCulture(a.culture));
  run.marker(`AX|nomadic|no`);
  run.facts('AX', a.owner.owner || []);
  for (const r of a.regiments) {
    run.save('ck3xbl_r_cur', r.cur).save('ck3xbl_r_max', r.max).save('ck3xbl_r_hired', r.hired ? 1 : 0);
    run.record('R', regimentPrint(r.maa, r.name, r.id));
    if (r.hired) run.facts('RX', hiredCulture(r.hired));
  }
}

function snapshot(b, base = []) {
  const run = new Run(b.combat, b.context, { saved: base });
  run.save('ck3xbl_bid', b.bid).save('ck3xbl_parent_bid', b.parentBid ?? 0).save('ck3xbl_clash', b.clash ?? 1).save('ck3xbl_combat', b.combat).save('ck3xbl_year', b.date[0]).save('ck3xbl_month', b.date[1])
    .save('ck3xbl_day', b.date[2]).save('ck3xbl_prov', b.province).save('ck3xbl_county', b.county).save('ck3xbl_barony', b.barony)
    .save('ck3xbl_war', b.war).save('ck3xbl_player', C.gunda.print)
    .save('ck3xbl_att_side', b.att.side).save('ck3xbl_att_primary', b.att.primary.print)
    .save('ck3xbl_def_side', b.def.side).save('ck3xbl_def_primary', b.def.primary.print)
    .save('ck3xbl_player_present', b.playerPresent).save('ck3xbl_my_side_code', 2)
    .save('ck3xbl_my_side', b.def.side).save('ck3xbl_enemy_side', b.att.side)
    .save('ck3xbl_my_men', b.def.size).save('ck3xbl_enemy_men', b.att.size)
    .save('ck3xbl_enemy_leader', b.att.armies[0].commander.print).save('ck3xbl_enemy_army_owner', b.att.armies[0].owner.print);
  run.record('B|BEGIN', b.combat);
  run.facts('L', b.facts);
  run.save('ck3xbl_l_fort', b.fort).save('ck3xbl_l_dev', b.dev).save('ck3xbl_l_has_origin', 1).save('ck3xbl_l_origin', b.origin);
  run.record('LV', b.province);
  for (const [tag, code, s] of [['att', 1, b.att], ['def', 2, b.def]]) {
    run.save('ck3xbl_side', s.side).save('ck3xbl_side_code', code).save('ck3xbl_s_size', s.size).save('ck3xbl_s_has_primary', 1)
      .save('ck3xbl_s_primary', s.primary.print).save('ck3xbl_s_player_in', s.playerIn)
      .save('ck3xbl_s_has_cmd', 0).save('ck3xbl_s_hostility_out', 0);
    // A side without any commander keeps has_cmd 0 and the previous side's ck3xbl_s_cmd (sentinel, §5.4).
    if (s.commander) run.save('ck3xbl_s_cmd', s.commander.print).save('ck3xbl_s_has_cmd', 1).save('ck3xbl_s_hostility_out', s.hostilityOut);
    run.record(`S|${tag}`, s.side);
    for (const a of s.armies) armyRecords(run, `A|${tag}`, a);
    for (const [role, c, armyTag, isPlayer, culture, extra] of s.characters) characterRecord(run, role, tag, c, armyTag, isPlayer, culture, extra);
  }
  run.record('B|END', b.combat);
  return run;
}

// ------------------------------------------------------------------------------------------------ battle 3: Orangallu

const b3 = {
  bid: 3, date: [871, 2, 14], context: 'ck3xbl_on_combat_start', playerPresent: 1,
  combat: combatPrint('Battle of Orangallu', 134217801),
  province: province('Orangallu', 1256), county: title('Orangallu', 10110, 'c_orangallu'), barony: title('Orangallu', 10111, 'b_warangal'),
  war: warPrint("Bhupati Phoro's Conquest of Warangal", 37),
  // Bhupati's host besieges Orangallu (AX|besieging|yes), so the place has a siege.
  facts: ['terrain|hills', 'winter|none', 'holding|castle_holding', 'coastal|no', 'riverside|no', 'siege|yes', 'raid|no', 'occupied|no',
    'region|world_india', 'building_gfx|indian_building_gfx', 'loc_culture|telugu', 'loc_heritage|heritage_dravidian'],
  fort: 3, dev: 12, origin: province('Ellur', 1255),
  att: {
    side: sidePrint('Attacker', 33554501), size: 1925, primary: C.bhupati, playerIn: 0, commander: C.bhupati, hostilityOut: 2,
    armies: [{
      name: "Bhupati Phoro's Host", id: 16777240, tag: 7, size: 1925, owner: C.bhupati, commander: C.bhupati, culture: 'kannada',
      ke: 1500, besieging: true, territory: 'hostile',
      regiments: [
        { maa: 'armored_footmen', name: 'Armored Footmen', id: 16791700, cur: 200, max: 200 },
        { maa: 'light_horsemen', name: 'Light Horsemen', id: 16791701, cur: 175, max: 200 },
        { maa: 'paiks', name: 'Paiks', id: 16791702, cur: 200, max: 200, hired: 'telugu' },
        { maa: 'onager', name: 'Onagers', id: 16791703, cur: 4, max: 4 },
      ],
    }],
    characters: [
      ['CMD', C.bhupati, 7, false, 'kannada', ['trait|strong', 'ctrait|aggressive_attacker']],
      ['KNIGHT', C.kesava, 7, false, 'kannada', []],
      ['KNIGHT', C.mallaya, 7, false, 'kannada', ['trait|one_eyed']],
    ],
  },
  def: {
    side: sidePrint('Defender', 33554502), size: 2300, primary: C.gunda, playerIn: 1, commander: C.gunda, hostilityOut: 1,
    armies: [
      { name: 'Army of Warangal', id: 16777242, tag: 8, size: 1700, owner: C.gunda, commander: C.gunda, culture: 'telugu',
        ke: 1150, besieging: false, territory: 'own',
        regiments: [
          { maa: 'war_elephant', name: 'War Elephants', id: 16791710, cur: 25, max: 25 },
          { maa: 'armored_footmen', name: 'Armored Footmen', id: 16791711, cur: 200, max: 200 },
          { maa: 'bowmen', name: 'Bowmen', id: 16791712, cur: 200, max: 200 },
        ] },
      // An ally's levies without a commander: the dump still holds the previous army's ck3xbl_a_cmd (sentinel 0). The barony's
      // controller is neither the ally nor below him, nor at war with him: territory other.
      { name: 'Army of Mahur', id: 16777244, tag: 9, size: 600, owner: C.beta, commander: null, culture: 'telugu', regiments: [],
        ke: 1000, besieging: false, territory: 'other' },
    ],
    characters: [
      ['CMD', C.gunda, 8, true, 'telugu', ['trait|scarred', 'ctrait|unyielding_defender']],
      ['KNIGHT', C.prola, 8, false, 'telugu', []],
      ['KNIGHT', C.rudra, 8, false, 'telugu', ['trait|wounded_1', 'ctrait|forest_fighter']],
    ],
  },
};

at(21, 13, 40);
engine('I', 'savegameitem.cpp:734', 'Waited 12 ms for 1 main menu portraits, ready: true');
engine('W', 'pdx_mesh_streamer.cpp:249', 'Requesting mesh tgp_great_building_mandala_capital_01_mesh');
at(21, 14, 5);
const snap3 = snapshot(b3);
// The toast (filter ck3xbl_battle, auto_pause) and event 0001 follow in the same tick; neither writes to game.log.

// CHOICE|FIELD from event 0001: the event chain carries the snapshot's saved scopes. Option effects also show engine-saved
// targets and lists from the surrounding context (as in the real game.log); none starts with ck3xbl_.
at(21, 14, 19);
const foreign = [
  ['governor', character('Shuzhen_6DD1_73CD Zhào', 'c_nanxi', 12972, 'han_3410')],
  ['pool_province', province('Ijaw', 8115)],
  ['steps', '0.10'],
  ['throne', 'yes'],
  ['buddhism_relic_name', 'Flag bone (6463)'],
];
const lists = [['elders', [character('Qikun Yáng', 'd_yongchang', 14142, 'bai_yang_3_2'), 'Petros Lowborn of  (Internal ID 18194) weak (Character - 18194)!']]];
new Run(C.gunda.print, 'ck3xbl_battle.0001:option', { saved: snap3.saved, foreign, lists }).record('CHOICE|FIELD', C.gunda.print);

// The battle is fought in Bannerlord; CK3 stays paused. The first keypad payload loses a key (IN|BAD), the resend is accepted.
at(21, 20, 39);
new Run(C.gunda.print, 'ck3xbl_key_commit:effect').save('ck3xbl_bid', 3).save('ck3xbl_in_sum', 6114).save('ck3xbl_in_count', 33)
  .save('ck3xbl_in_maxver', 4).record('IN|BAD', C.gunda.print);
at(21, 20, 41);
new Run(C.gunda.print, 'ck3xbl_key_commit:effect').save('ck3xbl_bid', 3).save('ck3xbl_in_sum', 2687).save('ck3xbl_in_count', 34)
  .save('ck3xbl_in_maxver', 4).record('IN|OK', C.gunda.print);

// "The dust settles..." in event 0002 runs ck3xbl_apply_effect (RES trace). force_win ends the combat in the same tick.
at(21, 20, 47);
const res = new Run(C.gunda.print, 'ck3xbl_battle.0002:option', { saved: snap3.saved });
res.save('ck3xbl_bid', 3).save('ck3xbl_res_outcome', 2);
res.record('RES|BEGIN', C.gunda.print);
res.save('ck3xbl_res_combat', b3.combat).save('ck3xbl_res_has_prov', 1).save('ck3xbl_res_prov', b3.province)
  .save('ck3xbl_res_has_primaries', 1).save('ck3xbl_res_att_primary', C.bhupati.print).save('ck3xbl_res_def_primary', C.gunda.print)
  .save('ck3xbl_res_my_side', 2).save('ck3xbl_res_has_captor', 1).save('ck3xbl_res_winner_code', 2).save('ck3xbl_res_default_vside', 1)
  .save('ck3xbl_captor', C.gunda.print).save('ck3xbl_res_loser_primary', C.bhupati.print);
res.record('RES|LIVE', C.gunda.print);
for (const [a, after] of [[b3.att.armies[0], 1012], [b3.def.armies[0], 1431], [b3.def.armies[1], 486]]) {
  res.save('ck3xbl_a_tag', a.tag).save('ck3xbl_after', after);
  res.record('RES|A', armyPrint(a.name, a.id));
}
res.save('ck3xbl_bid', 3);
res.record('RES|END', C.gunda.print);
for (const [kind, side, primary] of [['winner', b3.def.side, C.gunda], ['loser', b3.att.side, C.bhupati]]) {
  new Run(side, `ck3xbl_on_combat_end_${kind}`).save('ck3xbl_bid', 3).save('ck3xbl_end_st_bid', 0).save('ck3xbl_end_wipe', 0).save('ck3xbl_end_has_primary', 1)
    .save('ck3xbl_end_primary', primary.print).record(`END|${kind}`, side);
}

// ------------------------------------------------------------------------------------------------ battle 4: Medak (CK3 decides)

const b4 = {
  bid: 4, date: [871, 3, 2], context: 'ck3xbl_on_combat_start', playerPresent: 0,
  combat: combatPrint('Battle of Medak', 134217830),
  province: province('Medak', 1257), county: title('Orangallu', 10110, 'c_orangallu'), barony: title('Medak', 10112, 'b_medak'),
  war: warPrint("Bhupati Phoro's Conquest of Warangal", 37),
  facts: ['terrain|plains', 'winter|none', 'holding|castle_holding', 'coastal|no', 'riverside|yes', 'siege|no', 'raid|no', 'occupied|no',
    'region|world_india', 'building_gfx|indian_building_gfx', 'loc_culture|telugu', 'loc_heritage|heritage_dravidian'],
  fort: 1, dev: 12, origin: province('Orangallu', 1256),
  att: {
    side: sidePrint('Attacker', 33554530), size: 500, primary: C.bhupati, playerIn: 0, commander: C.mallaya, hostilityOut: 2,
    armies: [{ name: 'Raiders of Vengi', id: 16777250, tag: 10, size: 500, owner: C.bhupati, commander: C.mallaya, culture: 'kannada',
      ke: 1500, besieging: false, territory: 'hostile',
      regiments: [{ maa: 'light_horsemen', name: 'Light Horsemen', id: 16791720, cur: 120, max: 200 }] }],
    characters: [
      ['CMD', C.mallaya, 10, false, 'kannada', ['trait|one_eyed']],
      ['PRIMARY', C.bhupati, 0, false, 'kannada', ['trait|strong', 'ctrait|aggressive_attacker']],
    ],
  },
  def: {
    // The player is the defenders' war leader but not on the field: C|PRIMARY, s_player_in 1, ck3xbl_player_present 0.
    side: sidePrint('Defender', 33554531), size: 600, primary: C.gunda, playerIn: 1, commander: C.prola, hostilityOut: 1,
    armies: [{ name: 'Army of Mahur', id: 16777244, tag: 11, size: 600, owner: C.beta, commander: C.prola, culture: 'telugu', regiments: [],
      ke: 1000, besieging: false, territory: 'other' }],
    characters: [
      ['CMD', C.prola, 11, false, 'telugu', []],
      ['PRIMARY', C.gunda, 0, true, 'telugu', ['trait|scarred', 'ctrait|unyielding_defender']],
    ],
  },
};

at(21, 31, 12);
const snap4 = snapshot(b4);
at(21, 31, 20);
new Run(C.gunda.print, 'ck3xbl_battle.0001:option', { saved: snap4.saved }).record('CHOICE|AUTO', C.gunda.print);

// CK3 runs on: the player's host (re-tagged, battle 3 released its tags) joins the defenders, led by the knight Rudra.
at(21, 31, 52);
const join = new Run(b4.def.side, 'ck3xbl_on_combat_join');
// The join is classified for the battle clock before its J record (ck3xbl_j_steer 0: battle 4 is not steered).
join.save('ck3xbl_j_steer', 0).save('ck3xbl_bid', 4).save('ck3xbl_player', C.gunda.print).save('ck3xbl_side_code', 2);
armyRecords(join, 'J|def', {
  name: 'Army of Warangal', id: 16777242, tag: 12, size: 1180, owner: C.gunda, commander: C.rudra, culture: 'telugu',
  ke: 1150, besieging: false, territory: 'own',
  regiments: [
    { maa: 'war_elephant', name: 'War Elephants', id: 16791710, cur: 20, max: 25 },
    { maa: 'armored_footmen', name: 'Armored Footmen', id: 16791711, cur: 140, max: 200 },
    { maa: 'bowmen', name: 'Bowmen', id: 16791712, cur: 180, max: 200 },
  ],
});
characterRecord(join, 'CMD', 'def', C.rudra, 12, false, 'telugu', ['trait|wounded_1', 'ctrait|forest_fighter'], 'ck3xbl_snapshot_effects');

at(21, 33, 5);
for (const [kind, side, primary] of [['winner', b4.def.side, C.gunda], ['loser', b4.att.side, C.bhupati]]) {
  new Run(side, `ck3xbl_on_combat_end_${kind}`).save('ck3xbl_bid', 4).save('ck3xbl_end_st_bid', 0).save('ck3xbl_end_wipe', 0).save('ck3xbl_end_has_primary', 1)
    .save('ck3xbl_end_primary', primary.print).record(`END|${kind}`, side);
}

function writeLog(file) {
  fs.writeFileSync(file, lines.join('\n') + '\n', 'utf8');
  console.log(`wrote ${path.relative(REPO, file)}: ${lines.length} lines, ${lines.filter((l) => l.includes('CK3XBL|')).length} records`);
  lines.length = 0;
}

writeLog(OUT);

// ================================================================================================ ck3_battle_clock_game.log
//
// The battle clock (PROTOCOL §14), keypad payload version 4. Each battle: snapshot, FIELD, IN|OK,
// the apply (RES|BEGIN, RES|LIVE, ST|BEGIN, RES|END), daily ST|P pulses, the end, and one day after the end the fix (ST|FIX per
// army with an end state, ST|DONE).
//  Battle 5, Warangal: decided (outcome 2). The ally Beta's army joins the winners on day 7 (J|def, ck3xbl_j_steer 1, no end state).
//  Battle 6, Medak: decided; on day 5 Mallaya's fresh army joins the Bannerlord losers (J|att, ck3xbl_j_steer 2). Steering is
//    suspended and the same combat gets a new snapshot, battle 7 (ck3xbl_parent_bid 6, ck3xbl_clash 2, ST|SUSPEND), which the
//    player takes to the field; its payload carries cumulative targets and its ST|BEGIN names battle 6 as its parent. The
//    steered armies keep their tags (16, 17); the joiner, tagged 18 by its J record, is tagged again (19) by the new snapshot.
//  Battle 8, Ellur: dev mode (one run file: IN|OK, RES|BEGIN, RES|LIVE, ST|BEGIN, RES|END in one run), outcome 3: steered to both
//    targets, then handed back (ST|HANDBACK); the fix runs mid-combat (ST|DONE reason 2), and CK3's own END follows later
//    without a steering bid.
// The context of a console run file is not observed yet; the fixture names it "run_file".

/** Saved values that travel from one effect run into the event it triggers (combat and side scopes are dropped there). */
const carried = (run, drop = ['ck3xbl_combat', 'ck3xbl_res_combat', 'ck3xbl_att_side', 'ck3xbl_def_side', 'ck3xbl_my_side',
  'ck3xbl_enemy_side', 'ck3xbl_side', 'ck3xbl_war']) => [...run.saved].filter(([k]) => !drop.includes(k));

/** The keypad payload of a v4 result: its IN|OK values come from the reference encoder (tools/gen/keypad_payload.mjs). */
function commitV4(bid, values, context = 'ck3xbl_key_commit:effect', run = null) {
  const enc = encode({ 0: 4, 1: bid, ...values });
  const count = enc.registers.slice(0, 199).filter((v) => v > 0).length;
  const r = run || new Run(C.gunda.print, context);
  r.save('ck3xbl_bid', bid).save('ck3xbl_in_sum', enc.checksum).save('ck3xbl_in_count', count).save('ck3xbl_in_maxver', 4)
    .record('IN|OK', C.gunda.print);
  return enc;
}

/** The apply of a v4 payload up to RES|END: RES|BEGIN, RES|LIVE, the steering start (mark armies, store slots, ST|BEGIN). */
function applyV4(res, b, { outcome, targets, endDay, cday, parent = 0, mode = 1, enemyLeader }) {
  res.save('ck3xbl_bid', b.bid).save('ck3xbl_res_outcome', outcome);
  res.record('RES|BEGIN', C.gunda.print);
  res.save('ck3xbl_res_combat', b.combat).save('ck3xbl_res_has_prov', 1).save('ck3xbl_res_prov', b.province)
    .save('ck3xbl_res_has_primaries', 1).save('ck3xbl_res_att_primary', C.bhupati.print).save('ck3xbl_res_def_primary', C.gunda.print)
    .save('ck3xbl_res_my_side', 2).save('ck3xbl_res_has_captor', outcome < 3 ? 1 : 0).save('ck3xbl_res_winner_code', outcome < 3 ? outcome : 0)
    .save('ck3xbl_res_default_vside', 1);
  res.record('RES|LIVE', C.gunda.print);
  res.save('ck3xbl_st_att_target', targets[0]).save('ck3xbl_st_def_target', targets[1]).save('ck3xbl_st_hint', 12)
    .save('ck3xbl_st_cur_bid', b.bid).save('ck3xbl_st_old_bid', parent).save('ck3xbl_st_has_enemy_leader', 1)
    .save('ck3xbl_st_enemy_leader', enemyLeader.print)
    .save('ck3xbl_st_mode', mode).save('ck3xbl_st_winner', mode === 1 ? outcome : 0).save('ck3xbl_st_end_day', endDay)
    .save('ck3xbl_st_cday', cday).save('ck3xbl_st_parent', parent);
  res.record('ST|BEGIN', C.gunda.print, 'ck3xbl_steer_effects');
  res.save('ck3xbl_chain', b.chain);
  res.save('ck3xbl_bid', b.bid);
  res.record('RES|END', C.gunda.print);
}

/** A pulse event (ck3xbl_clock.0001): it first drops every saved scope it inherited but its chain id (PROTOCOL §14.5). */
const pulseRun = (chain) => new Run(C.gunda.print, 'ck3xbl_clock.0001:immediate', { saved: [['ck3xbl_chain', chain]] });

/** One daily pulse of a steered combat (ck3xbl_clock.0001): the controller values of both sides, then ST|P. */
function pulse(run, stBid, p) {
  run.save('ck3xbl_st_any', 1).save('ck3xbl_st_cur_bid', stBid).save('ck3xbl_bid', stBid).save('ck3xbl_st_cday', p.cday)
    .save('ck3xbl_st_att_has_cmd', 1).save('ck3xbl_st_def_has_cmd', 1);
  for (const i of [0, 1]) run.save('ck3xbl_st_obs', p.obs[i]).save('ck3xbl_st_rem', p.targets[i] - p.killed[i]).save('ck3xbl_st_want', p.want[i]);
  run.save('ck3xbl_st_cday_marks', p.cday).save('ck3xbl_st_days', p.cday)
    .save('ck3xbl_st_att_killed', p.killed[0]).save('ck3xbl_st_att_target', p.targets[0]).save('ck3xbl_st_att_men', p.men[0])
    .save('ck3xbl_st_att_gain', p.gain[0] * 1000)
    .save('ck3xbl_st_def_killed', p.killed[1]).save('ck3xbl_st_def_target', p.targets[1]).save('ck3xbl_st_def_men', p.men[1])
    .save('ck3xbl_st_def_gain', p.gain[1] * 1000)
    .save('ck3xbl_st_susp', p.susp || 0);
  run.record('ST|P', C.gunda.print);
}

/** The daily pulses of `days` ([cday, attKilled, defKilled, attMen, defMen, attGain, defGain]) in one chain. Each pulse starts
 * with only the chain id, whatever the apply or the previous pulse saved. */
function pulses(stBid, chain, targets, days, hh, mm) {
  let prev = [0, 0];
  let run = null;
  days.forEach(([cday, ka, kd, ma, md, ga, gd], i) => {
    at(hh, mm + i, 0);
    run = pulseRun(chain);
    pulse(run, stBid, { cday, killed: [ka, kd], obs: [ka - prev[0], kd - prev[1]], targets, men: [ma, md], gain: [ga, gd],
      want: [Math.max(0, targets[0] - ka) / Math.max(1, 12 - Math.max(cday + 1, 5)), Math.max(0, targets[1] - kd) / Math.max(1, 12 - Math.max(cday + 1, 5))] });
    prev = [ka, kd];
  });
  return run;
}

/** The end call in the pulse that found the winner's target reached: ST|WIN. */
function win(run, stBid, { winner, cday, killed, targets }) {
  run.save('ck3xbl_bid', stBid).save('ck3xbl_st_cday', cday)
    .save('ck3xbl_st_att_killed', killed[0]).save('ck3xbl_st_att_short', Math.max(0, targets[0] - killed[0]))
    .save('ck3xbl_st_def_killed', killed[1]).save('ck3xbl_st_def_short', Math.max(0, targets[1] - killed[1]))
    .save('ck3xbl_st_winner', winner).save('ck3xbl_st_capped', 0).save('ck3xbl_st_retry', 0);
  run.record('ST|WIN', C.gunda.print);
}

/** CK3 ends the combat: the END pair, each naming the combat's latest snapshot bid and the steering bid. */
function endPair(b, stBid, winnerSide, loserSide) {
  for (const [kind, side, primary] of [['winner', winnerSide, C.gunda], ['loser', loserSide, C.bhupati]]) {
    new Run(side, `ck3xbl_on_combat_end_${kind}`).save('ck3xbl_bid', b.bid).save('ck3xbl_end_st_bid', stBid).save('ck3xbl_end_wipe', 0)
      .save('ck3xbl_end_has_primary', 1).save('ck3xbl_end_primary', primary.print)
      .record(`END|${kind}`, side);
  }
}

/** The fix (ck3xbl_clock.0002, or right after the hand-back's pulse): ST|FIX per army [tag, after, excess, default], ST|DONE. */
function fix(stBid, reason, armies, b, context = 'ck3xbl_clock.0002:immediate') {
  const run = new Run(C.gunda.print, context, { saved: [['ck3xbl_st_cur_bid', stBid]] });
  run.save('ck3xbl_bid', stBid).save('ck3xbl_st_reason', reason);
  for (const [tag, after, excess, isDefault, name, id] of armies) {
    run.save('ck3xbl_st_levies_now', after).save('ck3xbl_st_levy_target', after).save('ck3xbl_st_default', isDefault)
      .save('ck3xbl_st_levy_excess', excess).save('ck3xbl_st_unindexed', 0).save('ck3xbl_a_tag', tag).save('ck3xbl_after', after);
    run.record('ST|FIX', armyPrint(name, id));
  }
  run.save('ck3xbl_st_has_summary', 1).save('ck3xbl_sum_outcome', 2).save('ck3xbl_sum_tries', 0).save('ck3xbl_prov', b.province)
    .save('ck3xbl_enemy_leader', C.bhupati.print).save('ck3xbl_summary_player', C.gunda.print);
  run.record('ST|DONE', C.gunda.print, 'ck3xbl_steer_effects#2');
  return run;
}

const fieldBattle = (bid, date, name, combatId, provName, provId, attArmy, defArmy, extra = {}) => ({
  bid, date, context: 'ck3xbl_on_combat_start', playerPresent: 1, chain: extra.chain || 1,
  combat: combatPrint(name, combatId), province: province(provName, provId),
  county: title('Orangallu', 10110, 'c_orangallu'), barony: title(provName, 10120 + bid, `b_${provName.toLowerCase()}`),
  war: warPrint("Bhupati Phoro's Conquest of Warangal", 37),
  facts: ['terrain|plains', 'winter|none', 'holding|castle_holding', 'coastal|no', 'riverside|no', 'siege|no', 'raid|no', 'occupied|no',
    'region|world_india', 'building_gfx|indian_building_gfx', 'loc_culture|telugu', 'loc_heritage|heritage_dravidian'],
  fort: 1, dev: 10, origin: province('Ellur', 1255),
  att: { side: sidePrint('Attacker', combatId - 100663200), size: attArmy.size, primary: C.bhupati, playerIn: 0, commander: attArmy.commander, hostilityOut: 2,
    armies: [attArmy], characters: [['CMD', attArmy.commander, attArmy.tag, false, 'kannada', ['trait|strong', 'ctrait|aggressive_attacker']]] },
  def: { side: sidePrint('Defender', combatId - 100663199), size: defArmy.size, primary: C.gunda, playerIn: 1, commander: C.gunda, hostilityOut: 1,
    armies: [defArmy], characters: [['CMD', C.gunda, defArmy.tag, true, 'telugu', ['trait|scarred', 'ctrait|unyielding_defender']]] },
  ...extra,
});
const host = (tag, id, size, extra = {}) => ({ name: "Bhupati Phoro's Host", id, tag, size, owner: C.bhupati, commander: C.bhupati,
  culture: 'kannada', ke: 1500, besieging: false, territory: 'hostile',
  regiments: [{ maa: 'armored_footmen', name: 'Armored Footmen', id: id + 15000, cur: 200, max: 200 }], ...extra });
const warangal = (tag, size) => ({ name: 'Army of Warangal', id: 16777242, tag, size, owner: C.gunda, commander: C.gunda, culture: 'telugu',
  ke: 1150, besieging: false, territory: 'own',
  regiments: [{ maa: 'bowmen', name: 'Bowmen', id: 16791712, cur: 200, max: 200 }] });

// ------------------------------------------------------------------------------------------------ battle 5: Warangal (decided)

const b5 = fieldBattle(5, [871, 4, 2], 'Battle of Warangal', 134217850, 'Warangal', 1258, host(13, 16777260, 1800), warangal(14, 1650));
at(22, 0, 0);
const snap5 = snapshot(b5);
at(22, 0, 10);
new Run(C.gunda.print, 'ck3xbl_battle.0001:option', { saved: snap5.saved }).record('CHOICE|FIELD', C.gunda.print);
at(22, 6, 30);
commitV4(5, { 2: 2, 3: 1258, 4: 380, 5: 1020, 6: 4, 7: 60, 8: 380, 9: 1020, 198: 12, 10: 13, 11: 600, 12: 48, 13: 600, 24: 14, 25: 1270, 130: 5, 131: 1, 132: 1 });
at(22, 6, 41);
const res5 = new Run(C.gunda.print, 'ck3xbl_battle.0002:option', { saved: snap5.saved });
applyV4(res5, b5, { outcome: 2, targets: [380, 1020], endDay: 12, cday: 2, enemyLeader: C.bhupati });
const t5 = [380, 1020];
pulses(5, 1, t5, [[3, 0, 0, 1800, 1650, 1, 1], [4, 0, 0, 1800, 1650, 1, 1], [5, 41, 96, 1704, 1609, 1.36, 1.85], [6, 98, 255, 1545, 1552, 1.29, 1.81]],
  22, 7);
// Day 7: the ally's army joins the winners: classified (ck3xbl_j_steer 1) before its J record, then steered as a joiner.
at(22, 12, 0);
const join5 = new Run(b5.def.side, 'ck3xbl_on_combat_join');
join5.save('ck3xbl_j_steer', 0).save('ck3xbl_bid', 5).save('ck3xbl_j_min', 155).save('ck3xbl_j_steer', 1)
  .save('ck3xbl_player', C.gunda.print).save('ck3xbl_side_code', 2);
armyRecords(join5, 'J|def', { name: 'Army of Mahur', id: 16777244, tag: 15, size: 600, owner: C.beta, commander: C.prola, culture: 'telugu',
  ke: 1000, besieging: false, territory: 'other', regiments: [] });
characterRecord(join5, 'CMD', 'def', C.prola, 15, false, 'telugu', [], 'ck3xbl_snapshot_effects');
pulses(5, 1, t5, [[7, 162, 433, 1367, 1488, 1.25, 1.77], [8, 224, 607, 1193, 1426, 1.27, 1.76], [9, 290, 782, 1018, 1360, 1.31, 1.80]],
  22, 13);
at(22, 16, 0);
const last5 = pulseRun(1);
pulse(last5, 5, { cday: 10, killed: [362, 1009], obs: [72, 227], targets: t5, men: [791, 1288], gain: [1.4, 1.9], want: [9, 5.5] });
win(last5, 5, { winner: 2, cday: 10, killed: [362, 1009], targets: t5 });
at(22, 16, 30);
endPair(b5, 5, b5.def.side, b5.att.side);
at(22, 17, 0);
fix(5, 1, [[13, 840, 0, 0, b5.att.armies[0].name, b5.att.armies[0].id], [14, 1470, 0, 0, b5.def.armies[0].name, b5.def.armies[0].id]], b5);

// ------------------------------------------------------------------------------------------------ battles 6 and 7: Medak (second clash)

const b6 = fieldBattle(6, [871, 5, 10], 'Battle of Medak', 134217860, 'Medak', 1257, host(17, 16777270, 1400), warangal(16, 1500), { chain: 1 });
at(22, 30, 0);
const snap6 = snapshot(b6);
at(22, 30, 10);
new Run(C.gunda.print, 'ck3xbl_battle.0001:option', { saved: snap6.saved }).record('CHOICE|FIELD', C.gunda.print);
at(22, 35, 0);
commitV4(6, { 2: 2, 3: 1257, 4: 150, 5: 420, 6: 2, 8: 150, 9: 420, 198: 12, 10: 17, 11: 980, 24: 16, 25: 1350 });
at(22, 35, 9);
const res6 = new Run(C.gunda.print, 'ck3xbl_battle.0002:option', { saved: snap6.saved });
applyV4(res6, b6, { outcome: 2, targets: [150, 420], endDay: 12, cday: 2, enemyLeader: C.bhupati });
pulses(6, 1, [150, 420], [[3, 0, 0, 1400, 1500, 1, 1], [4, 0, 0, 1400, 1500, 1, 1]], 22, 36);
// Day 5: Mallaya brings 900 fresh men to the Bannerlord losers: a second clash is offered.
at(22, 40, 0);
const join6 = new Run(b6.att.side, 'ck3xbl_on_combat_join');
join6.save('ck3xbl_j_steer', 0).save('ck3xbl_bid', 6).save('ck3xbl_j_min', 140).save('ck3xbl_j_steer', 2)
  .save('ck3xbl_player', C.gunda.print).save('ck3xbl_side_code', 1);
const reinforcements = { name: "Mallaya's Riders", id: 16777280, tag: 18, size: 900, owner: C.bhupati, commander: C.mallaya, culture: 'kannada',
  ke: 1500, besieging: false, territory: 'hostile', regiments: [{ maa: 'light_horsemen', name: 'Light Horsemen', id: 16791730, cur: 200, max: 200 }] };
armyRecords(join6, 'J|att', reinforcements);
characterRecord(join6, 'CMD', 'att', C.mallaya, 18, false, 'kannada', ['trait|one_eyed'], 'ck3xbl_snapshot_effects');
// Same effect run: the suspension, the new snapshot of the live combat (battle 7), then ST|SUSPEND with the parent's bid.
join6.save('ck3xbl_st_cur_bid', 6).save('ck3xbl_joiner_leader', C.mallaya.print).save('ck3xbl_joiner_side_code', 1);
const b7 = fieldBattle(7, [871, 5, 15], 'Battle of Medak', 134217860, 'Medak', 1257, host(17, 16777270, 1380), warangal(16, 1410),
  { context: 'ck3xbl_on_combat_join', parentBid: 6, clash: 2 });
b7.att.armies.push({ ...reinforcements, tag: 19 });
b7.att.size = 2280;
b7.att.characters.push(['CMD', C.mallaya, 19, false, 'kannada', ['trait|one_eyed']]);
b7.combat = b6.combat;
b7.att.side = b6.att.side;
b7.def.side = b6.def.side;
const snap7 = snapshot(b7, carried(join6, []));
snap7.save('ck3xbl_bid', 6).save('ck3xbl_st_child', 7).save('ck3xbl_st_reason', 1);
snap7.record('ST|SUSPEND', b6.combat);
snap7.save('ck3xbl_bid', 7);
at(22, 40, 20);
new Run(C.gunda.print, 'ck3xbl_battle.0001:option', { saved: snap7.saved }).record('CHOICE|FIELD', C.gunda.print);
// The child's payload: cumulative targets and summary (r4..r6, r8, r9); the parent's armies keep their tags.
at(22, 47, 0);
commitV4(7, { 2: 2, 3: 1257, 4: 410, 5: 900, 6: 5, 8: 410, 9: 900, 198: 12, 10: 17, 11: 700, 24: 16, 25: 1150, 38: 19, 39: 520, 40: 46, 41: 450 });
at(22, 47, 12);
const res7 = new Run(C.gunda.print, 'ck3xbl_battle.0002:option', { saved: snap7.saved });
applyV4(res7, b7, { outcome: 2, targets: [410, 900], endDay: 12, cday: 5, parent: 6, enemyLeader: C.mallaya });
const t7 = [410, 900];
pulses(7, 1, t7, [[6, 60, 112, 2160, 1389, 1.72, 2.1], [7, 151, 352, 1920, 1300, 1.66, 2.3], [8, 236, 606, 1666, 1210, 1.6, 2.2],
  [9, 318, 804, 1468, 1124, 1.58, 2.0]], 22, 48);
at(22, 53, 0);
const last7 = pulseRun(1);
pulse(last7, 7, { cday: 10, killed: [402, 893], obs: [84, 89], targets: t7, men: [1379, 1040], gain: [1.5, 0.8], want: [8, 7] });
win(last7, 7, { winner: 2, cday: 10, killed: [402, 893], targets: t7 });
at(22, 53, 30);
endPair(b7, 7, b6.def.side, b6.att.side);
at(22, 54, 0);
fix(7, 1, [[16, 1350, 0, 0, b7.def.armies[0].name, b7.def.armies[0].id], [17, 900, 0, 0, b7.att.armies[0].name, b7.att.armies[0].id],
  [19, 970, 0, 0, reinforcements.name, reinforcements.id]], b7);

// ------------------------------------------------------------------------------------------------ battle 8: Ellur (dev mode, hand-back)

const b8 = fieldBattle(8, [871, 6, 20], 'Battle of Ellur', 134217870, 'Ellur', 1255, host(21, 16777290, 1200), warangal(20, 1300));
at(23, 10, 0);
const snap8 = snapshot(b8);
at(23, 10, 10);
new Run(C.gunda.print, 'ck3xbl_battle.0001:option', { saved: snap8.saved }).record('CHOICE|FIELD', C.gunda.print);
// The run file commits and applies in one run, before the bridge has seen the delivery complete (RunFileDelivery waits for RES|END).
at(23, 16, 0);
const dev = new Run(C.gunda.print, 'run_file');
commitV4(8, { 2: 3, 3: 1255, 4: 210, 5: 260, 8: 260, 9: 210, 198: 12, 10: 21, 11: 940, 24: 20, 25: 1090 }, undefined, dev);
applyV4(dev, b8, { outcome: 3, targets: [260, 210], endDay: 12, cday: 1, mode: 2, enemyLeader: C.bhupati });
const t8 = [260, 210];
pulses(8, 1, t8, [[2, 0, 0, 1200, 1300, 1, 1], [3, 0, 0, 1200, 1300, 1, 1], [4, 0, 0, 1200, 1300, 1, 1],
  [5, 38, 31, 1169, 1262, 1.37, 1.35], [6, 85, 69, 1131, 1215, 1.36, 1.37], [7, 133, 108, 1092, 1167, 1.38, 1.37],
  [8, 180, 146, 1054, 1120, 1.37, 1.38], [9, 222, 182, 1018, 1078, 1.4, 1.4]], 23, 17);
at(23, 26, 0);
const last8 = pulseRun(1);
pulse(last8, 8, { cday: 10, killed: [262, 214], obs: [40, 32], targets: t8, men: [986, 1038], gain: [0.05, 0.05], want: [0, 0] });
last8.save('ck3xbl_st_cur_bid', 8).save('ck3xbl_bid', 8).save('ck3xbl_st_cday', 10).save('ck3xbl_st_att_killed', 262).save('ck3xbl_st_att_short', 0)
  .save('ck3xbl_st_def_killed', 214).save('ck3xbl_st_def_short', 0).save('ck3xbl_st_capped', 0).save('ck3xbl_st_retry', 0).save('ck3xbl_st_reason', 2);
last8.record('ST|HANDBACK', C.gunda.print);
at(23, 27, 0);
fix(8, 2, [[20, 1080, 0, 0, b8.def.armies[0].name, b8.def.armies[0].id], [21, 938, 0, 0, b8.att.armies[0].name, b8.att.armies[0].id]], b8);
// CK3 decides the battle itself days later; the fix removed the combat's steering bid, so END names none and releases the tags.
at(23, 31, 0);
endPair(b8, 0, b8.def.side, b8.att.side);

writeLog(CLOCK_OUT);

// ================================================================================================ ck3_stand_in_game.log
//
// PROTOCOL §15 (live test 2026-10-04, battle b2_ea00: the stand-in path chose a host without a commander, and the request had no
// hero on the player's side). Battle 9 at Kollipaka: Bhupati's host attacks Gunda's own "Army of Warangal", 250 men without a
// commander. Beta Kakatiya leads the defenders' war (C|PRIMARY|def, at home: ck3xbl_c_absent 1). Gunda owns the host but is not
// with it: no side lists him as commander or knight, so he is the C|PLAYER|def record (ck3xbl_c_is_player 1, ck3xbl_c_army 0,
// ck3xbl_c_absent 1), and he takes the field in the commander's place (option c of event 0001: CHOICE|FIELD).
const STAND_IN_OUT = path.join(HERE, 'ck3_stand_in_game.log');
const b9 = {
  bid: 9, date: [871, 7, 3], context: 'ck3xbl_on_combat_start', playerPresent: 0,
  combat: combatPrint('Battle of Kollipaka', 134217880),
  province: province('Kollipaka', 1259), county: title('Orangallu', 10110, 'c_orangallu'), barony: title('Kollipaka', 10129, 'b_kollipaka'),
  war: warPrint("Bhupati Phoro's Conquest of Warangal", 37),
  facts: ['terrain|plains', 'winter|none', 'holding|castle_holding', 'coastal|no', 'riverside|no', 'siege|no', 'raid|no', 'occupied|no',
    'region|world_india', 'building_gfx|indian_building_gfx', 'loc_culture|telugu', 'loc_heritage|heritage_dravidian'],
  fort: 1, dev: 9, origin: province('Ellur', 1255),
  att: {
    side: sidePrint('Attacker', 33554580), size: 1336, primary: C.bhupati, playerIn: 0, commander: C.bhupati, hostilityOut: 2,
    armies: [host(23, 16777300, 1336)],
    characters: [['CMD', C.bhupati, 23, false, 'kannada', ['trait|strong', 'ctrait|aggressive_attacker']]],
  },
  def: {
    side: sidePrint('Defender', 33554581), size: 250, primary: C.beta, playerIn: 1, commander: null, hostilityOut: 0,
    armies: [{ name: 'Army of Warangal', id: 16777242, tag: 24, size: 250, owner: C.gunda, commander: null, culture: 'telugu',
      ke: 1150, besieging: false, territory: 'own', regiments: [{ maa: 'bowmen', name: 'Bowmen', id: 16791712, cur: 125, max: 200 }] }],
    characters: [
      ['PRIMARY', C.beta, 0, false, 'telugu', []],
      ['PLAYER', C.gunda, 0, true, 'telugu', ['trait|scarred', 'ctrait|unyielding_defender']],
    ],
  },
};
at(23, 40, 0);
const snap9 = snapshot(b9);
at(23, 40, 12);
new Run(C.gunda.print, 'ck3xbl_battle.0001:option', { saved: snap9.saved }).record('CHOICE|FIELD', C.gunda.print);

writeLog(STAND_IN_OUT);
