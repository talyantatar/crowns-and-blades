#!/usr/bin/env node
// Builds bridge/data/ck3_accolade_types.json from the user's CK3 install (read-only) and the bridge's own data tables.
//
//   node tools/gen/knights/gen_ck3_accolade_types.mjs            # write the JSON and the proposed CK3 snippet
//   node tools/gen/knights/gen_ck3_accolade_types.mjs --check    # write nothing; exit 1 if either output would change
//
// Inputs (CK3, read-only; CK3_GAME_DIR overrides the install path):
//   common/accolade_types/*.txt, common/accolade_names/*.txt, common/accolade_icons/*.txt,
//   common/men_at_arms_types/01_accolade_maa_types.txt, common/scripted_triggers/04_ep2_accolade_triggers.txt,
//   common/script_values/*.txt, common/defines/00_defines.txt (NAccolade),
//   common/combat_phase_events/00_knight_phase_events.txt, events/war_events/combat_events.txt,
//   common/on_action/combat_on_actions.txt, localization/english/**.yml
// Inputs (project): bridge/data/mapping.json, bridge/data/bl_troops_customgame.json, bridge/data/ck3_catalog.json
// Outputs: bridge/data/ck3_accolade_types.json, tools/gen/knights/ck3_accolade_emit_snippet.txt (a proposal for the
// mod generator; not part of the mod). Deterministic: no timestamps; keys in a fixed order.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { gameDir, parseText, stripBom, walk } from '../lib/pdx.mjs';
import { readScript, toPlain, child, resolveConst, readLocalization, round6 } from './lib_ck3script.mjs';
import { accoladeEmitEffectLines } from './accolade_emit.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../../..');
const GAME = gameDir().replace(/\\/g, '/');
const OUT_JSON = path.join(ROOT, 'bridge/data/ck3_accolade_types.json');
const OUT_SNIPPET = path.join(HERE, 'ck3_accolade_emit_snippet.txt');
const CHECK = process.argv.includes('--check');

const rel = (f) => path.relative(GAME, f).replace(/\\/g, '/');
const readJson = (f) => JSON.parse(stripBom(fs.readFileSync(f, 'utf8')));
const r2 = (x) => Math.round(x * 100) / 100;
const r3 = (x) => Math.round(x * 1000) / 1000;

// ---------------------------------------------------------------------------------------------------------------
// Script values (only the plain forms: number, reference, { value add subtract multiply divide min max }).

function loadScriptValues() {
  const dir = path.join(GAME, 'common/script_values');
  const nodes = new Map();
  for (const f of fs.readdirSync(dir).filter((n) => n.endsWith('.txt')).sort()) {
    const file = path.join(dir, f);
    for (const n of parseText(fs.readFileSync(file, 'utf8'))) {
      if (n.key && !n.key.startsWith('@') && !nodes.has(n.key)) nodes.set(n.key, { node: n, file: `common/script_values/${f}` });
    }
  }
  const cache = new Map();
  function value(name, depth = 0) {
    if (typeof name === 'number') return name;
    if (/^-?\d+(\.\d+)?$/.test(name)) return Number(name);
    if (cache.has(name)) return cache.get(name);
    const e = nodes.get(name);
    if (!e || depth > 20) return null;
    let v = null;
    if (e.node.value !== undefined) v = value(e.node.value, depth + 1);
    else if (e.node.block) {
      const ops = e.node.block;
      if (ops.every((o) => ['value', 'add', 'subtract', 'multiply', 'divide', 'min', 'max', 'desc'].includes(o.key) && o.value !== undefined)) {
        v = 0;
        for (const o of ops) {
          if (o.key === 'desc') continue;
          const x = value(o.value, depth + 1);
          if (x === null) { v = null; break; }
          if (o.key === 'value') v = x;
          else if (o.key === 'add') v += x;
          else if (o.key === 'subtract') v -= x;
          else if (o.key === 'multiply') v *= x;
          else if (o.key === 'divide') v /= x;
          else if (o.key === 'min') v = Math.max(v, x);
          else if (o.key === 'max') v = Math.min(v, x);
        }
      }
    }
    if (v !== null) v = round6(v);
    cache.set(name, v);
    return v;
  }
  return { value, has: (n) => nodes.has(n), source: (n) => (nodes.get(n) ? `${nodes.get(n).file}:${nodes.get(n).node.line}` : null) };
}

// ---------------------------------------------------------------------------------------------------------------
// Inputs

const SV = loadScriptValues();
const catalog = readJson(path.join(ROOT, 'bridge/data/ck3_catalog.json'));
const mapping = readJson(path.join(ROOT, 'bridge/data/mapping.json'));
const blTroops = readJson(path.join(ROOT, 'bridge/data/bl_troops_customgame.json'));
const BASE_TYPES = Object.keys(catalog.meta.counts.maa_by_base_type).sort((a, b) => b.length - a.length || a.localeCompare(b));

function ck3Version() {
  try {
    const s = readJson(path.join(GAME, '../launcher/launcher-settings.json'));
    return s.rawVersion || s.version || null;
  } catch {
    return null;
  }
}

function findLine(file, re, from = 0) {
  const lines = stripBom(fs.readFileSync(file, 'utf8')).split(/\r?\n/);
  for (let i = from; i < lines.length; i++) if (re.test(lines[i])) return i + 1;
  return null;
}

// Bannerlord FormationClass (TaleWorlds.Core.dll 1.4.8): Infantry 0, Ranged 1, Cavalry 2, HorseArcher 3,
// Skirmisher 4, HeavyInfantry 5, LightCavalry 6, HeavyCavalry 7, General 8, Bodyguard 9. Troops only use 0..3 (their
// default_group); bl_troops_customgame.json "group" is the lower-case form.
const FORMATION_CLASS = { Infantry: 0, Ranged: 1, Cavalry: 2, HorseArcher: 3, Skirmisher: 4, HeavyInfantry: 5, LightCavalry: 6, HeavyCavalry: 7, General: 8, Bodyguard: 9 };
const GROUP_TO_CLASS = { infantry: 'Infantry', ranged: 'Ranged', cavalry: 'Cavalry', horsearcher: 'HorseArcher' };

// ---------------------------------------------------------------------------------------------------------------
// Bannerlord formation of each CK3 MaA base type: weighted BL default_group of every troop the mapper can field for it
// (mapping.json maa_base + the role's ladder or shared ladder, all cultures).

function baseTypeFormations() {
  const groupOf = new Map(blTroops.map((t) => [t.id, t.group]));
  const out = {};
  for (const base of Object.keys(mapping.base_roles)) {
    const role = mapping.base_roles[base];
    if (role.exclude) { out[base] = { formation: null, note: role.note || 'excluded from field battles' }; continue; }
    const counts = {};
    const add = (troop, w) => {
      const g = groupOf.get(troop);
      if (!g) return;
      const cls = GROUP_TO_CLASS[g];
      counts[cls] = (counts[cls] || 0) + (Number(w) || 1);
    };
    for (const list of Object.values(mapping.maa_base?.[base] || {})) for (const t of list) add(t.troop, t.weight);
    const ladder = role.ladder_ref ? mapping.shared_ladders?.[role.ladder_ref] : mapping.maa_ladders?.[role.role];
    const ladders = Array.isArray(ladder) ? [ladder] : Object.values(ladder || {});
    for (const rungs of ladders) for (const rung of rungs) for (const t of rung.troops || []) add(t.troop, t.weight);
    const total = Object.values(counts).reduce((a, b) => a + b, 0);
    const share = Object.fromEntries(Object.entries(counts).sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0])).map(([k, v]) => [k, r2(v / total)]));
    const formation = Object.keys(share)[0] || null;
    out[base] = {
      formation,
      formationClassId: formation ? FORMATION_CLASS[formation] : null,
      share,
      ladder: role.ladder_ref ? `shared_ladders.${role.ladder_ref}` : `maa_ladders.${role.role}`,
      ...(role.note ? { note: role.note } : {}),
    };
  }
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// Modifier classification (what a key does in a CK3 field battle).

function classifyModifier(key) {
  for (const b of BASE_TYPES) {
    if (!key.startsWith(`${b}_`)) continue;
    const rest = key.slice(b.length + 1);
    let m;
    if ((m = /^(damage|toughness|pursuit|screen)_(mult|add)$/.exec(rest))) return { kind: 'maa_stat', base: b, stat: m[1], op: m[2] };
    if (/^siege_value_(add|mult)$/.test(rest)) return { kind: 'siege', base: b };
    if (/^(max_size_add|maintenance_mult|recruitment_cost_mult)$/.test(rest)) return { kind: 'economy', base: b };
  }
  let m;
  if ((m = /^army_(damage|toughness|pursuit|screen)_(mult|add)$/.exec(key))) return { kind: 'army_stat', stat: m[1], op: m[2] };
  if (key === 'counter_efficiency') return { kind: 'counter_efficiency' };
  if (key === 'uncontrolled_province_advantage') return { kind: 'advantage_uncontrolled' };
  if (/advantage$/.test(key)) return { kind: 'advantage_other' };
  if (['movement_speed', 'winter_movement_speed', 'hostile_county_attrition', 'siege_phase_time', 'siege_morale_loss', 'supply_limit_mult'].includes(key)) return { kind: 'strategic' };
  if (['martial', 'diplomacy', 'stewardship', 'intrigue', 'learning', 'prowess', 'martial_per_stress_level'].includes(key)) return { kind: 'skill' };
  if (/^knight_effectiveness/.test(key)) return { kind: 'knight_effectiveness' };
  if (key === 'knight_limit') return { kind: 'knight_count' };
  return { kind: 'other' };
}

// What each class of modifier means for the bridge / module (shared text, referenced by kind).
const MODIFIER_KINDS = {
  maa_stat: 'knight_army_modifier on one MaA base type of the army the acclaimed knight is in: damage/toughness -> Bannerlord buff of that army\'s MaA groups of the base type (tier or agent multipliers); pursuit/screen -> the bridge\'s casualty conversion (write-back side).',
  army_stat: 'knight_army_modifier on every regiment of the knight\'s army (levies and MaA): damage/toughness -> Bannerlord buff of all CK3 troop groups of that army.',
  counter_efficiency: 'CK3 counter strength of the army\'s MaA: optional module damage bonus of that army\'s MaA agents against agents of base types their CK3 MaA counter (ck3_catalog maa_types[].counters). No tier equivalent.',
  advantage_uncontrolled: 'Advantage while in a province the army owner does not control: feeds the commander-advantage mechanic of the mapper for the knight\'s side (needs the proposed AX|uncontrolled line).',
  siege: 'Siege value: no effect (sieges stay in CK3, PROTOCOL section 0).',
  strategic: 'Map movement, attrition or siege timing: no effect on a field battle.',
  economy: 'Liege MaA economy (regiment cap, maintenance): no battle effect; the extra regiments already show in the army snapshot.',
  skill: 'Liege skill modifier: already contained in the logged CK3 skill values (C record ck3xbl_c_martial etc.).',
  knight_effectiveness: 'Liege modifier on all of his knights: belongs in the knight-effectiveness reconstruction (bridge/data/ck3_knight_effectiveness_sources.json, backlog item 4).',
  knight_count: 'More knights for the liege: already visible as more C|KNIGHT records.',
  other: 'No field-battle effect.',
};

// ---------------------------------------------------------------------------------------------------------------
// Accolade parameters with a battle meaning (verified where vanilla reads them).

function parameterTable() {
  const kp = 'common/combat_phase_events/00_knight_phase_events.txt';
  const kpFile = path.join(GAME, kp);
  const coa = 'common/on_action/combat_on_actions.txt';
  const coaFile = path.join(GAME, coa);
  const f = (name) => SV.value(name);
  const at = (file, short, re) => {
    const l = findLine(file, re);
    return l ? `${short}:${l}` : short;
  };
  return {
    accolade_increase_hostile_knight_death_low: { battle: 'fate', effect: 'enemy_knight_death_weight', factor: f('accolade_hostile_knight_death_in_battle_actual_low_value'), source: at(kpFile, kp, /has_accolade_parameter = accolade_increase_hostile_knight_death_low/), note: 'Chance factor of event knight_killed for every knight of the OTHER side while this side has a knight or commander with the parameter; only the highest of low/medium/high applies.' },
    accolade_increase_hostile_knight_death_medium: { battle: 'fate', effect: 'enemy_knight_death_weight', factor: f('accolade_hostile_knight_death_in_battle_actual_medium_value'), source: at(kpFile, kp, /has_accolade_parameter = accolade_increase_hostile_knight_death_medium/), note: 'see _low' },
    accolade_increase_hostile_knight_death_high: { battle: 'fate', effect: 'enemy_knight_death_weight', factor: f('accolade_hostile_knight_death_in_battle_actual_high_value'), source: at(kpFile, kp, /has_accolade_parameter = accolade_increase_hostile_knight_death_high/), note: 'see _low' },
    accolade_defends_family_low: { battle: 'fate', effect: 'own_house_knight_harm_weight', factor: f('accolade_defends_family_actual_low_value'), source: at(kpFile, kp, /has_accolade_parameter = accolade_defends_family_low/), note: 'Chance factor of knight_wounded, knight_maimed and knight_killed for knights of the same side whose house is the liege\'s house; only the highest of low/medium/high applies.' },
    accolade_defends_family_medium: { battle: 'fate', effect: 'own_house_knight_harm_weight', factor: f('accolade_defends_family_actual_medium_value'), source: at(kpFile, kp, /has_accolade_parameter = accolade_defends_family_medium/), note: 'see _low' },
    accolade_defends_family_high: { battle: 'fate', effect: 'own_house_knight_harm_weight', factor: f('accolade_defends_family_actual_high_value'), source: at(kpFile, kp, /has_accolade_parameter = accolade_defends_family_high/), note: 'see _low' },
    acclaimed_knight_prestige_from_victory: { battle: 'automatic', effect: 'army_owner_prestige_on_win', amount: f('acclaimed_knight_prestige_from_victory_value'), source: at(coaFile, coa, /has_accolade_parameter = acclaimed_knight_prestige_from_victory$/), note: 'Vanilla on_combat_end_winner; runs by itself after the mod\'s force_win. Nothing to do.' },
    acclaimed_knight_prestige_from_victory_high: { battle: 'automatic', effect: 'army_owner_prestige_on_win', amount: f('acclaimed_knight_prestige_from_victory_value_high'), source: at(coaFile, coa, /has_accolade_parameter = acclaimed_knight_prestige_from_victory_high/), note: 'see above' },
    acclaimed_knight_piety_from_battle: { battle: 'none', note: 'Read at war end (religious war casus belli, alliance interactions), not per battle.' },
    acclaimed_knight_piety_from_battle_high: { battle: 'none', note: 'see above' },
    accolade_organize_army_bonus: { battle: 'none', note: 'Marshal council task (common/council_tasks/00_marshal_tasks.txt).' },
  };
}

// ---------------------------------------------------------------------------------------------------------------
// Fit requirements (squire_perfect_fit / _acceptable_fit scripted triggers), summarised.

function fitTriggers() {
  const file = path.join(GAME, 'common/scripted_triggers/04_ep2_accolade_triggers.txt');
  const tree = parseText(fs.readFileSync(file, 'utf8'));
  const out = {};
  for (const n of tree) {
    if (!n.block || !/_attribute(_acceptable)?_trigger$/.test(n.key)) continue;
    const s = { source: `${rel(file)}:${n.line}`, traits: [], not_traits: [], tourney_tracks: [], skills: [], culture: [], other: [] };
    walk(n.block, (x, parents) => {
      const negated = parents.some((p) => p.key === 'NOT' || p.key === 'NOR');
      if (x.key === 'has_trait' && x.value) (negated ? s.not_traits : s.traits).push(x.value);
      else if (x.key === 'has_trait_xp' && x.block) {
        const t = x.block.find((y) => y.key === 'trait')?.value;
        const track = x.block.find((y) => y.key === 'track')?.value;
        const v = x.block.find((y) => y.key === 'value');
        s.tourney_tracks.push(`${t}${track ? `.${track}` : ''} ${v?.op || '>='} ${v?.value}`);
      } else if (['diplomacy', 'martial', 'stewardship', 'intrigue', 'learning', 'prowess'].includes(x.key) && x.value) {
        const v = SV.value(x.value);
        s.skills.push(`${x.key} ${x.op} ${v ?? x.value}`);
      } else if (['has_innovation', 'has_cultural_tradition', 'has_cultural_parameter'].includes(x.key)) s.culture.push(`${x.key}=${x.value}`);
      else if (x.key && /^var:\w+_unlock$/.test(x.key)) s.other.push(`${x.key} (unlocked by an event)`);
      else if (['government_has_flag', 'has_dynasty_perk'].includes(x.key)) s.other.push(`${negated ? 'NOT ' : ''}${x.key}=${x.value}`);
    });
    for (const k of ['traits', 'not_traits', 'tourney_tracks', 'skills', 'culture', 'other']) s[k] = [...new Set(s[k])];
    out[n.key] = s;
  }
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// Accolade MaA (unlocked by attribute level 2+).

function accoladeMaa() {
  const file = path.join(GAME, 'common/men_at_arms_types/01_accolade_maa_types.txt');
  const { tree, consts } = readScript(file);
  const out = {};
  for (const n of tree) {
    if (!n.block || n.key.startsWith('@')) continue;
    let reqType = null;
    let reqLevel = null;
    walk(child(n.block, 'can_recruit')?.block || [], (x) => {
      if (x.key === 'accolade_attribute_level' && x.block) {
        reqType = x.block.find((y) => y.key === 'type')?.value;
        const lv = x.block.find((y) => y.key === 'level');
        reqLevel = lv ? `${lv.op} ${lv.value}` : null;
      }
    });
    const get = (k) => {
      const c = child(n.block, k);
      return c ? (c.block ? toPlain(c.block, consts) : resolveConst(c.value, consts)) : null;
    };
    out[n.key] = {
      source: `${rel(file)}:${n.line}`,
      base_type: get('type'),
      requires: { accolade_type: reqType, level: reqLevel },
      max_regiments: get('max_regiments'),
      damage: get('damage'), toughness: get('toughness'), pursuit: get('pursuit'), screen: get('screen'),
      terrain_bonus: get('terrain_bonus') || {}, winter_bonus: get('winter_bonus') || {}, counters: get('counters') || {},
      stack: get('stack'),
      in_maa_index: null, // filled below
    };
  }
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// Category vocabulary (comments of 00_accolade_categories.txt).

function categoryGroups() {
  const file = path.join(GAME, 'common/accolade_types/00_accolade_categories.txt');
  const out = {};
  for (const line of stripBom(fs.readFileSync(file, 'utf8')).split(/\r?\n/)) {
    const m = /^#\s*([A-Za-z][A-Za-z ?]*?):\s*(".*)$/.exec(line);
    if (!m) continue;
    out[m[1].replace(/\?/g, '').trim().replace(/\s+/g, '_')] = [...m[2].matchAll(/"([^"]+)"/g)].map((x) => x[1]);
  }
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// Glory: defines, values, and every vanilla glory change that a battle can cause.

function gloryFacts() {
  const defFile = path.join(GAME, 'common/defines/00_defines.txt');
  const defs = parseText(fs.readFileSync(defFile, 'utf8'));
  const na = toPlain(child(defs, 'NAccolade').block, {});
  const values = {};
  for (const k of ['sub_minimal', 'minimal', 'minor', 'medium', 'major', 'massive', 'monumental']) {
    values[`${k}_glory_value`] = SV.value(`${k}_glory_value`);
    values[`${k}_glory_gain`] = SV.value(`${k}_glory_gain`);
    if (k !== 'sub_minimal') values[`${k}_glory_loss`] = SV.value(`${k}_glory_loss`);
  }
  // Battle-time glory changes in vanilla script.
  const sources = [];
  const kp = path.join(GAME, 'common/combat_phase_events/00_knight_phase_events.txt');
  const MEANING = {
    'knight_berserker_attack|scope:enemy_knight': 'a berserker knight (root) attacks an enemy knight; the ATTACKED knight\'s accolade gains (as written in 1.20)',
    'knight_become_berserker|root': 'root turns berserker in battle; root\'s own accolade gains',
    'knight_shieldmaiden_attack|scope:enemy_knight': 'a shieldmaiden (root) attacks an enemy knight; the ATTACKED knight\'s accolade gains (as written in 1.20)',
    'knight_wounded|scope:enemy_knight': 'root is wounded by an enemy knight; the wounding knight\'s accolade gains',
    'knight_maimed|scope:enemy_knight': 'root is maimed by an enemy knight; the maiming knight\'s accolade gains',
    'knight_killed|scope:enemy_knight': 'root is killed by an enemy knight; the killing knight\'s accolade gains (if acclaimed)',
    'knight_killed|root': 'root is killed; root\'s own accolade gains (after death = { }), so the successor inherits it',
  };
  for (const ev of parseText(fs.readFileSync(kp, 'utf8'))) {
    if (!ev.block) continue;
    walk(ev.block, (n, parents) => {
      if (n.key !== 'add_glory') return;
      const recipient = parents.some((p) => p.key === 'scope:enemy_knight') ? 'scope:enemy_knight' : 'root';
      sources.push({ source: `${rel(kp)}:${n.line}`, event: ev.key, recipient, amount: n.value, value: SV.value(n.value), meaning: MEANING[`${ev.key}|${recipient}`] || null });
    });
  }
  const ce = path.join(GAME, 'events/war_events/combat_events.txt');
  for (const ev of parseText(fs.readFileSync(ce, 'utf8'))) {
    if (!ev.block) continue;
    walk(ev.block, (n) => {
      if (n.key !== 'add_glory') return;
      const meaning = ev.key === 'combat_event.2101' ? 'battle won: every acclaimed side knight and side commander of the winning side (fired by on_combat_end_winner)'
        : ev.key === 'combat_event.2111' ? 'battle lost: every acclaimed side knight and side commander of the losing side (fired by on_combat_end_loser)' : null;
      sources.push({ source: `${rel(ce)}:${n.line}`, event: ev.key, recipient: 'every acclaimed side_knight / side_commander', amount: n.value, value: SV.value(n.value), meaning });
    });
  }
  return {
    rank_thresholds: na.ACCOLADE_GLORY_LEVELS,
    rank_thresholds_source: `${rel(defFile)}:${findLine(defFile, /ACCOLADE_GLORY_LEVELS/)} (NAccolade.ACCOLADE_GLORY_LEVELS; rank n needs thresholds[n-1] glory)`,
    starting_glory: na.STARTING_GLORY,
    max_attributes: na.ACCOLADE_MAX_TYPES,
    levels_per_attribute: 3,
    ranks: Array.isArray(na.ACCOLADE_GLORY_LEVELS) ? na.ACCOLADE_GLORY_LEVELS.length : null,
    succession_glory_loss: { small: na.SUCCESSION_GLORY_LOSS_SMALL, large: na.SUCCESSION_GLORY_LOSS_LARGE, script_value: 'accolade_succession_glory_loss', script_value_source: SV.source('accolade_succession_glory_loss'), honourable_deaths_skip_it: 'common/on_action/accolade_on_actions.txt on_accolade_new_acclaimed_knight: no loss when the old knight died with death_reason death_battle (the mod\'s battle deaths use it) and similar' },
    values,
    values_source: SV.source('minimal_glory_value'),
    battle_sources: sources,
  };
}

// ---------------------------------------------------------------------------------------------------------------
// accolade_names: which name templates can be picked for which category / type (cosmetic).

function accoladeNames() {
  const dir = path.join(GAME, 'common/accolade_names');
  const out = [];
  for (const f of fs.readdirSync(dir).filter((n) => n.endsWith('.txt')).sort()) {
    const file = path.join(dir, f);
    for (const n of parseText(fs.readFileSync(file, 'utf8'))) {
      if (!n.block) continue;
      const cats = new Set();
      const types = new Set();
      walk(n.block, (x) => {
        if (x.key === 'type_has_accolade_category' && x.value) cats.add(x.value);
        if (x.key === 'this' && /^accolade_type:/.test(x.value || '')) types.add(x.value.slice('accolade_type:'.length));
        if (x.key === 'has_accolade_type' && x.value) types.add(x.value);
      });
      out.push({ key: n.key, source: `${rel(file)}:${n.line}`, loc_key: child(n.block, 'key')?.value || null, num_options: Number(child(n.block, 'num_options')?.value) || null, categories: [...cats].sort(), types: [...types].sort() });
    }
  }
  return out;
}

function accoladeIcons() {
  const dir = path.join(GAME, 'common/accolade_icons');
  const out = [];
  for (const f of fs.readdirSync(dir).filter((n) => n.endsWith('.txt')).sort()) {
    for (const n of parseText(fs.readFileSync(path.join(dir, f), 'utf8'))) {
      if (n.block) out.push({ key: n.key, texture: child(n.block, 'texture')?.value || null, source: `common/accolade_icons/${f}:${n.line}` });
    }
  }
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// Bannerlord effect of one attribute level.

const K_AGENT = 0.5; // agent multipliers: 1 + K * CK3 mult (INFERRED, calibrate; the tier path needs no constant)

function tierDelta(dm, tm) {
  return r2(Math.log2((1 + dm) * (1 + tm)));
}

function blLevelEffect(levelBlock, typeKey) {
  const kam = levelBlock.knight_army_modifier || {};
  const byBase = {};
  const army = { damage: 0, toughness: 0, pursuit: 0, screen: 0 };
  let counterEff = 0;
  let advUncontrolled = 0;
  const ignored = [];
  for (const [k, v] of Object.entries(kam)) {
    const c = classifyModifier(k);
    if (c.kind === 'maa_stat') {
      byBase[c.base] = byBase[c.base] || { damage: 0, toughness: 0, pursuit: 0, screen: 0 };
      if (c.op === 'mult') byBase[c.base][c.stat] += v;
      else ignored.push(`${k} (flat add)`);
    } else if (c.kind === 'army_stat') army[c.stat] += c.op === 'mult' ? v : 0;
    else if (c.kind === 'counter_efficiency') counterEff += v;
    else if (c.kind === 'advantage_uncontrolled') advUncontrolled += v;
    else ignored.push(`${k} (${c.kind})`);
  }
  const groups = [];
  for (const [base, s] of Object.entries(byBase).sort()) {
    groups.push({
      scope: 'maa_base_type',
      baseType: base,
      tierDelta: tierDelta(s.damage, s.toughness),
      damageMult: r3(1 + K_AGENT * s.damage),
      healthMult: r3(1 + K_AGENT * s.toughness),
      pursuitMult: r3(1 + s.pursuit),
      screenMult: r3(1 + s.screen),
      ck3: { damage_mult: s.damage, toughness_mult: s.toughness, pursuit_mult: s.pursuit, screen_mult: s.screen },
    });
  }
  if (army.damage || army.toughness || army.pursuit || army.screen) {
    groups.push({
      scope: 'army',
      baseType: null,
      tierDelta: tierDelta(army.damage, army.toughness),
      damageMult: r3(1 + K_AGENT * army.damage),
      healthMult: r3(1 + K_AGENT * army.toughness),
      pursuitMult: r3(1 + army.pursuit),
      screenMult: r3(1 + army.screen),
      ck3: { army_damage_mult: army.damage, army_toughness_mult: army.toughness, army_pursuit_mult: army.pursuit, army_screen_mult: army.screen },
    });
  }
  const fate = {};
  for (const p of levelBlock.accolade_parameters || []) {
    const t = PARAMS[p];
    if (t && t.battle === 'fate') fate[t.effect] = t.factor;
  }
  // Liege modifiers that reach the battle through other tables (all of the liege's knights, not only this one).
  const liegeKe = {};
  for (const [k, v] of Object.entries(levelBlock.liege_modifier || {})) if (classifyModifier(k).kind === 'knight_effectiveness') liegeKe[k] = v;
  const out = { groups };
  if (counterEff) out.counterDamageMult = r3(1 + K_AGENT * counterEff);
  if (advUncontrolled) out.advantageIfUncontrolled = advUncontrolled;
  if (Object.keys(fate).length) out.fateFactors = fate;
  if (Object.keys(liegeKe).length) out.liegeKnightEffectiveness = liegeKe;
  if (ignored.length) out.noFieldEffect = ignored;
  if (!groups.length && !counterEff && !advUncontrolled && !Object.keys(fate).length && !Object.keys(liegeKe).length) out.none = true;
  void typeKey;
  return out;
}

const PARAMS = parameterTable();

// ---------------------------------------------------------------------------------------------------------------
// Accolade types.

function accoladeTypes(formations, maaUnlocks, fits) {
  const dir = path.join(GAME, 'common/accolade_types');
  const files = fs.readdirSync(dir).filter((n) => n.endsWith('.txt')).sort();
  const types = {};
  const allModifierKeys = new Map();
  for (const f of files) {
    const file = path.join(dir, f);
    const { tree, consts } = readScript(file);
    for (const n of tree) {
      if (!n.block || n.key.startsWith('@') || !child(n.block, 'ranks')) continue;
      const get = (k) => child(n.block, k)?.value ?? null;
      const categories = toPlain(child(n.block, 'accolade_categories')?.block || [], consts);
      const perfect = (child(n.block, 'squire_perfect_fit')?.block || []).map((x) => x.key).filter(Boolean);
      const acceptable = (child(n.block, 'squire_acceptable_fit')?.block || []).map((x) => x.key).filter(Boolean);
      const levels = {};
      for (const r of child(n.block, 'ranks').block) {
        if (!r.block) continue;
        const raw = toPlain(r.block, consts);
        const lvl = {};
        for (const k of ['liege_modifier', 'knight_modifier', 'knight_army_modifier']) {
          if (!raw[k]) continue;
          lvl[k] = {};
          for (const [mk, mv] of Object.entries(raw[k])) {
            const num = typeof mv === 'number' ? mv : SV.value(mv);
            lvl[k][mk] = num ?? mv;
            const c = classifyModifier(mk);
            const e = allModifierKeys.get(mk) || { kind: c.kind, blocks: new Set() };
            e.blocks.add(k);
            allModifierKeys.set(mk, e);
          }
        }
        for (const k of ['men_at_arms', 'accolade_parameters']) if (raw[k]) lvl[k] = [].concat(raw[k]);
        for (const k of ['terrain_bonus', 'winter_bonus', 'holding_bonus']) if (raw[k]) lvl[k] = raw[k];
        lvl.source = `${rel(file)}:${r.line}`;
        levels[r.key] = lvl;
      }
      // Linked base types: battle stats, MaA economy and unlocked MaA.
      const linked = new Set();
      const battleLinked = new Set();
      for (const l of Object.values(levels)) {
        for (const mk of Object.keys(l.knight_army_modifier || {})) {
          const c = classifyModifier(mk);
          if (c.base) linked.add(c.base);
          if (c.kind === 'maa_stat' && c.base) battleLinked.add(c.base);
        }
        for (const mk of Object.keys(l.liege_modifier || {})) {
          const c = classifyModifier(mk);
          if (c.base) linked.add(c.base);
        }
        for (const maa of l.men_at_arms || []) if (maaUnlocks[maa]?.base_type) linked.add(maaUnlocks[maa].base_type);
      }
      const unlockedMaa = [...new Set(Object.values(levels).flatMap((l) => l.men_at_arms || []))];
      const tierCat = ['eminent', 'skilled', 'common', 'men_at_arms'].find((c) => categories.includes(c)) || null;
      const setCat = ['men_at_arms', 'skill', 'personality', 'rare_traits', 'relationship'].find((c) => categories.includes(c)) || null;
      const skills = ['prowess', 'martial', 'diplomacy', 'stewardship', 'intrigue', 'learning'].filter((s) => categories.includes(s));

      // Bannerlord.
      const linkedList = [...linked].sort();
      const battleList = [...battleLinked].sort();
      const primaryBase = battleList[0] || linkedList[0] || null;
      const formationInfo = primaryBase ? formations[primaryBase] : null;
      const perLevel = {};
      for (const [lk, l] of Object.entries(levels)) perLevel[lk] = blLevelEffect(l, n.key);
      const armyWide = Object.values(perLevel).some((p) => p.groups.some((g) => g.scope === 'army') || p.counterDamageMult || p.advantageIfUncontrolled);
      let captain;
      if (formationInfo?.formation) {
        captain = {
          formation: formationInfo.formation,
          formationClassId: formationInfo.formationClassId,
          rule: 'linked_maa',
          linkedBaseType: primaryBase,
          note: `Captain the Bannerlord formation that holds most agents of this battle's groups maa:<key>#<knight's army tag>#* whose CK3 base type is ${primaryBase}; when the army has none, the default here; when the army has no such MaA at all, the knight's own formation (HeroSpec.formation).`,
        };
      } else if (primaryBase && formations[primaryBase] && !formations[primaryBase].formation) {
        captain = { formation: null, formationClassId: null, rule: 'own', linkedBaseType: primaryBase, note: `${primaryBase} never fights in a field battle: the knight keeps his own formation (HeroSpec.formation).` };
      } else {
        captain = { formation: null, formationClassId: null, rule: armyWide ? 'own_army_wide' : 'own', linkedBaseType: null, note: armyWide ? 'Army-wide effect: the knight keeps his own formation (HeroSpec.formation); the buff covers every CK3 troop group of his army.' : 'No troop-type link: the knight keeps his own formation (HeroSpec.formation).' };
      }
      types[n.key] = {
        source: `${rel(file)}:${n.line}`,
        name_loc: n.key,
        adjective_loc: get('adjective'),
        noun_loc: get('noun'),
        icon: get('icon'),
        portrait_pose: get('portrait_pose'),
        tier_field: get('tier'),
        tier: tierCat,
        set: setCat,
        categories,
        skills,
        squire_perfect_fit: perfect,
        squire_acceptable_fit: acceptable,
        fit: perfect.map((t) => fits[t]).filter(Boolean)[0] || null,
        linked_base_types: linkedList,
        battle_base_types: battleList,
        unlocked_maa: unlockedMaa,
        levels,
        bannerlord: { captain, perLevel },
      };
    }
  }
  return { types, allModifierKeys };
}

// ---------------------------------------------------------------------------------------------------------------
// The record proposal for the CK3 mod (static script: the type keys come from this table, nothing prints numbers).

function emitProposal(typeKeys) {
  return {
    status: 'PROPOSAL - not emitted by the mod yet; owner: CK3 mod generator (tools/gen/gen_ck3_mod.mjs). Parsers already ignore unknown CX/AX fields (BattleSnapshotBuilder.CharacterFact/ArmyFact), so adding the lines cannot break the bridge.',
    where: 'After the CX lines of every C record (any role: an acclaimed knight may also be an army commander), in that character\'s scope; AX|uncontrolled after the AX lines of every A/J army.',
    records: {
      'CX|acc_active|yes|no': 'is_acclaimed = yes and the accolade is one of its owner\'s active accolades (accolade_owner.any_active_accolade). Inactive accolades give no bonuses.',
      'CX|acc_rank|<0..6>': 'accolade_rank (triggers.log:11436), static chain >= 6 .. >= 1, else 0.',
      'CX|acc_primary|<type>': 'initial_type (event_targets.log:1524) = { this = accolade_type:<type> } (vanilla compare form, common/accolade_names/00_accolade_names.txt:1243).',
      'CX|acc_type|<type>|<0..3>': 'one line per has_accolade_type (triggers.log:11455) with the level from accolade_attribute_level = { type = <type> level >= n } (triggers.log:11414).',
      'AX|uncontrolled|yes|no': 'army scope: location.barony_controller (event_targets.log:1536) is not the army_owner. Only needed for marauder_attribute (uncontrolled_province_advantage).',
    },
    no_glory_value: 'The accolade\'s current glory has no trigger or script value in triggers.log/event_targets.log. The rank (with glory.rank_thresholds) bounds it; var:lifetime_glory on the accolade is vanilla bookkeeping of the net change under the current knight (reset at succession), not the glory.',
    type_keys: typeKeys,
  };
}

function snippetText(typeKeys) {
  const L = [];
  L.push('# PROPOSAL generated by tools/gen/knights/gen_ck3_accolade_types.mjs - NOT part of the mod. Adopt it in');
  L.push('# tools/gen/gen_ck3_mod.mjs (ck3xbl_gen_emit_effects.txt) and run the lint.');
  L.push('# Script names: script_docs (is_acclaimed triggers.log:4970, accolade event_targets.log:372,');
  L.push('# accolade_owner/acclaimed_knight/initial_type event_targets.log:1482-1526, any_active_accolade triggers.log:1250,');
  L.push('# accolade_rank 11436, has_accolade_type 11455, accolade_attribute_level 11414, add_glory effects.log:15972).');
  L.push('');
  L.push('# Character scope (the character of a C record), right after its CX lines.');
  L.push(...accoladeEmitEffectLines(typeKeys));
  L.push('');
  L.push('# Army scope (A and J records), after the AX lines. Only marauder_attribute reads it.');
  L.push('ck3xbl_emit_army_uncontrolled_effect = {');
  L.push('\tarmy_owner = { save_temporary_scope_as = ck3xbl_ax_owner }');
  L.push('\tif = {');
  L.push('\t\tlimit = { location = { barony_controller ?= scope:ck3xbl_ax_owner } }');
  L.push('\t\tinfo_log = "CK3XBL|AX|uncontrolled|no"');
  L.push('\t}');
  L.push('\telse = { info_log = "CK3XBL|AX|uncontrolled|yes" }');
  L.push('}');
  L.push('');
  L.push('# Write-back (apply effect, root = the player, registers as in PROTOCOL 2.2). Proposed glory block: 8 pairs');
  L.push('# (character tag, 1000 + glory) at r122..r129 and r190..r197. Call it BEFORE the character slots, so a knight who');
  L.push('# dies still adds his glory to his own accolade before it passes to the successor. Vanilla on_combat_end_winner /');
  L.push('# _loser (combat_event.2101 +25 / .2111 -10) still run after force_win: the bridge never sends those amounts.');
  L.push('ck3xbl_apply_glory_slot_effect = {');
  L.push('\tif = {');
  L.push('\t\tlimit = {');
  L.push('\t\t\tvar:$TAG$ > 0');
  L.push('\t\t\tvar:$GLORY$ > 0');
  L.push('\t\t\tNOT = { var:$GLORY$ = 1000 }');
  L.push('\t\t}');
  L.push('\t\tevery_in_global_list = {');
  L.push('\t\t\tvariable = ck3xbl_chars');
  L.push('\t\t\tlimit = {');
  L.push('\t\t\t\thas_variable = ck3xbl_tag');
  L.push('\t\t\t\tvar:ck3xbl_tag = root.var:$TAG$');
  L.push('\t\t\t\tis_acclaimed = yes');
  L.push('\t\t\t}');
  L.push('\t\t\taccolade = {');
  L.push('\t\t\t\tadd_glory = {');
  L.push('\t\t\t\t\tvalue = root.var:$GLORY$');
  L.push('\t\t\t\t\tsubtract = 1000');
  L.push('\t\t\t\t}');
  L.push('\t\t\t}');
  L.push('\t\t}');
  L.push('\t}');
  L.push('}');
  L.push('# ck3xbl_apply_glory_slot_effect = { TAG = ck3xbl_r122 GLORY = ck3xbl_r123 }  ... r128/r129, r190/r191 ... r196/r197');
  return L.join('\n') + '\n';
}

// ---------------------------------------------------------------------------------------------------------------

function main() {
  const formations = baseTypeFormations();
  const maaUnlocks = accoladeMaa();
  const maaIndex = readJson(path.join(ROOT, 'bridge/data/ck3_maa_index.json')).keys || [];
  for (const [k, v] of Object.entries(maaUnlocks)) {
    const i = maaIndex.indexOf(k);
    v.in_maa_index = i >= 0 ? i + 1 : null;
  }
  const fits = fitTriggers();
  const { types, allModifierKeys } = accoladeTypes(formations, maaUnlocks, fits);
  const typeKeys = Object.keys(types);

  // Localization (display names) for types, adjectives, nouns and MaA; `$key$` references are resolved.
  const loc = readLocalization(GAME, null);
  const text = (key, depth = 0) => {
    const t = key ? loc[key]?.text : undefined;
    if (t === undefined) return null;
    return depth > 5 ? t : t.replace(/\$([A-Za-z0-9_.\-]+)\$/g, (m, k) => text(k, depth + 1) ?? m);
  };
  for (const [k, t] of Object.entries(types)) {
    t.name = text(k);
    t.adjective = text(t.adjective_loc);
    t.noun = text(t.noun_loc);
  }
  for (const [k, v] of Object.entries(maaUnlocks)) v.name = text(k);

  // Unknown modifier keys get reported so a new patch cannot slip through silently. Only keys on the knight or his army
  // can matter in a battle; unknown liege keys are owner-side (opinion, lifestyle, schemes...).
  const unclassified = [...allModifierKeys].filter(([, e]) => e.kind === 'other' && (e.blocks.has('knight_army_modifier') || e.blocks.has('knight_modifier'))).map(([k]) => k).sort();
  const liegeNonBattle = [...allModifierKeys].filter(([, e]) => e.kind === 'other' && !e.blocks.has('knight_army_modifier') && !e.blocks.has('knight_modifier')).map(([k]) => k).sort();

  const glory = gloryFacts();
  const names = accoladeNames();
  const nameCount = {};
  for (const k of typeKeys) {
    const cats = new Set(types[k].categories);
    nameCount[k] = names.filter((n) => n.types.includes(k) || (n.types.length === 0 && n.categories.some((c) => cats.has(c)))).length;
    types[k].name_templates = nameCount[k];
  }

  // Order the fields of each type for readability.
  const orderedTypes = {};
  for (const k of typeKeys) {
    const t = types[k];
    orderedTypes[k] = {
      name: t.name, adjective: t.adjective, noun: t.noun, source: t.source,
      tier: t.tier, tier_field: t.tier_field, set: t.set, categories: t.categories, skills: t.skills,
      linked_base_types: t.linked_base_types, battle_base_types: t.battle_base_types, unlocked_maa: t.unlocked_maa,
      squire_perfect_fit: t.squire_perfect_fit, squire_acceptable_fit: t.squire_acceptable_fit, fit: t.fit,
      icon: t.icon, portrait_pose: t.portrait_pose, adjective_loc: t.adjective_loc, noun_loc: t.noun_loc, name_templates: t.name_templates,
      levels: t.levels,
      bannerlord: t.bannerlord,
    };
  }

  const doc = {
    meta: {
      what: 'CK3 accolade attributes (Tours & Tournaments) with their per-level effects, what to read at battle time, the Bannerlord mapping of each attribute level, and the glory write-back.',
      generator: 'tools/gen/knights/gen_ck3_accolade_types.mjs',
      ck3_version: ck3Version(),
      game_dir: '<CK3 install>/game',
      sources: ['common/accolade_types/*.txt', 'common/accolade_names/*.txt', 'common/accolade_icons/*.txt', 'common/men_at_arms_types/01_accolade_maa_types.txt', 'common/scripted_triggers/04_ep2_accolade_triggers.txt', 'common/script_values/*.txt', 'common/defines/00_defines.txt (NAccolade)', 'common/combat_phase_events/00_knight_phase_events.txt', 'events/war_events/combat_events.txt', 'common/on_action/combat_on_actions.txt', 'localization/english/**.yml', 'bridge/data/mapping.json', 'bridge/data/bl_troops_customgame.json', 'bridge/data/ck3_catalog.json', 'bridge/data/ck3_maa_index.json'],
      counts: { types: typeKeys.length, by_tier: countBy(typeKeys.map((k) => types[k].tier)), accolade_maa: Object.keys(maaUnlocks).length, name_templates: names.length, icons: accoladeIcons().length },
      unclassified_army_modifiers: unclassified,
      liege_modifiers_without_battle_effect: liegeNonBattle,
    },
    model: {
      attributes: 'An accolade has up to 3 attributes (NAccolade.ACCOLADE_MAX_TYPES) of 3 levels each. Each accolade rank (1..6, glory thresholds below) lets the owner add an attribute or raise one by a level, so the levels of all attributes sum to at most the rank; losing a rank disables one level (game concept accolade_rank). The first attribute is the primary one (initial_type; it only names the accolade); primary and secondary attributes work the same.',
      level_blocks: 'ranks = { 1 = {...} 2 = {...} 3 = {...} } of an attribute are its levels 1..3, not glory values (the .info file\'s glory-keyed example is outdated). INFERRED: only the highest unlocked level\'s modifiers apply (not cumulative): several attributes repeat the same value at every level (horse_archer screen 0.25 at levels 1-3, elephantry damage 0.2 at levels 2-3), which only makes sense if each level replaces the previous one. Parameters are checked with has_accolade_parameter over all unlocked levels, and vanilla picks the highest of low/medium/high itself.',
      knight_army_modifier: 'applies to the army the acclaimed knight is in (accolade_type.info). Liege modifiers apply to the owner. No 1.20 attribute uses knight_modifier, terrain_bonus, winter_bonus or holding_bonus (kept in the schema for patches).',
      active: 'Only active accolades (owner.any_active_accolade, max_active_accolades) give bonuses.',
      max_one_maa_attribute: 'An accolade may hold only one men_at_arms attribute (single_maa_attribute_allowance in the fit triggers).',
      acclaimed_fate: {
        combat_phase: 'knight_none chance x1.25 for acclaimed knights (00_knight_phase_events.txt:17-19): fewer combat-phase events of any kind; as the victim of berserker/shieldmaiden attacks they are picked x0.75.',
        post_battle: 'combat_event.1001 (on_combat_end_winner, runs after force_win): acclaimed commanders get +500 on the 90 "no effect" weight vs 10 capture (events/war_events/combat_events.txt:663-664); acclaimed knights capture x0.1 (:853-854) and death x0.5 (:944-945).',
        bridge_rule: 'For heroes the BL battle downed, the bridge rolls the CK3 fate itself (FateResolver): multiply the wounded/maimed/killed weights of an acclaimed knight by 0.8 against the unchanged "nothing" outcome when the fate model has one, else killed and maimed weights by 0.8 (1/1.25), and capture probability by 0.1 (knights) or 10/600 / (10/100) = 0.17 (commanders).',
      },
    },
    read_at_battle: {
      per_character: [
        { what: 'acclaimed?', script: 'is_acclaimed = yes', doc: 'triggers.log:4970', scope: 'character' },
        { what: 'the accolade', script: 'accolade', doc: 'event_targets.log:372', scope: 'character -> accolade' },
        { what: 'active?', script: 'accolade_owner ?= { any_active_accolade = { acclaimed_knight ?= scope:<knight> } }', doc: 'event_targets.log:1488, triggers.log:1250, event_targets.log:1482', scope: 'accolade' },
        { what: 'rank 0..6', script: 'accolade_rank >= n (also usable as a value: save_scope_value_as = { name = x value = accolade_rank }, vanilla 00_accolade_interactions.txt:88-89)', doc: 'triggers.log:11436', scope: 'accolade' },
        { what: 'attribute held', script: 'has_accolade_type = <type>', doc: 'triggers.log:11455', scope: 'accolade' },
        { what: 'attribute level 0..3', script: 'accolade_attribute_level = { type = <type> level >= n }', doc: 'triggers.log:11414', scope: 'accolade' },
        { what: 'primary attribute', script: 'initial_type = { this = accolade_type:<type> }', doc: 'event_targets.log:1524 (+ accolade_type global link event_targets.log:5)', scope: 'accolade' },
        { what: 'category / parameter', script: 'has_accolade_category = <flag> / has_accolade_parameter = <flag>', doc: 'triggers.log:11443 / 11449', scope: 'accolade' },
        { what: 'glory', script: 'none (no trigger, link or script value); rank + rank_thresholds bound it', doc: 'add_glory effects.log:15972 is write-only', scope: 'accolade' },
      ],
      record_proposal: emitProposal(typeKeys),
    },
    bannerlord: {
      formation_classes: FORMATION_CLASS,
      formation_classes_source: 'TaleWorlds.Core.FormationClass (Bannerlord 1.4.8). Troop default groups: infantry/ranged/cavalry/horsearcher (bl_troops_customgame.json "group").',
      base_type_formation: formations,
      modes: {
        tier: 'DEFAULT, bridge only (works with the current module): add tierDelta to the resolved tier of every matching group before TroopResolver picks rungs (same rule as the MaA tier rule: tier = ref + log2((damage/ref)(toughness/ref)), i.e. delta = log2((1+damage_mult)(1+toughness_mult))). Clamp the result to the ladder like any tier; above the top rung the agent multiplier (2^(over/2), cap 2) applies as for every MaA.',
        agent: 'Module stat model (when available): damageMult on the outgoing damage of agents of matching groups (CK3ApplyDamageModel : CustomAgentApplyDamageModel, override ApplyGeneralDamageModifiers, AttackInformation.AttackerAgent -> group), healthMult on their HealthLimit/Health at spawn (as heroes\' healthMultiplier). K = 0.5 of the CK3 multiplier (INFERRED, calibrate). Never both modes for the same group.',
        writeback: 'pursuitMult / screenMult multiply the ck3_catalog pursuit / screen of the matching MaA in the bridge\'s casualty conversion (CasualtyModel), independent of the mode.',
      },
      matching_groups: 'A level effect with scope maa_base_type covers the request groups maa:<maaKey>#<armyTag>#<regimentId> whose ck3_catalog maa_types[maaKey].base_type is the baseType and whose armyTag is the acclaimed knight\'s ck3xbl_c_army (knights) or his army (commanders). scope army covers levy#<armyTag> and every maa:*#<armyTag>#*. A knight with c_army = 0 buffs nothing.',
      captain: 'The acclaimed knight captains the formation of his linked groups (bannerlord.captain per type). Stock GeneralsAndCaptainsAssignmentLogic (TaleWorlds.MountAndBlade.dll) assigns heroes by Character.GetPower() to the largest formation whose mounted-ness matches the hero (PickBestRegularFormationToLead, protected virtual) and sets formation.Captain. Proposal for the module: optional HeroSpec field captainOf (FormationClass name); a subclass overriding PickBestRegularFormationToLead returns that formation first. Without module support the bridge may set HeroSpec.formation to the class (default_group) and, for a foot formation, Horse/HorseHarness = "" so the stock logic can match him.',
      rank_presence: 'INFERRED design (no CK3 counterpart): every agent of the formation an ACTIVE acclaimed knight captains gets initial morale +2 x accolade rank (rank 1..6 -> +2..+12; Bannerlord initial morale is 35 + random 0..29 + additions, clamped 15..100, CommonAIComponent.InitializeMorale), applied with Agent.ChangeMorale (AgentComponentExtensions) once the captain is assigned (OnDeploymentFinished) and to reinforcements of that formation as they spawn.',
      morale_per_rank: 2,
      k_agent: K_AGENT,
    },
    glory,
    write_back: {
      effect: 'add_glory = <value or script value block> in accolade scope (effects.log:15972; block form as in common/scripted_effects/00_accolades_scripted_effects.txt:371). Guard like vanilla: is_acclaimed = yes, then accolade = { add_glory = ... } (accolade_minimal_glory_gain_with_checks_effect, :1648).',
      automatic: 'Do NOT send battle-outcome glory: vanilla on_combat_end_winner/loser fire combat_event.2101 (+25 minor_glory_gain to every acclaimed knight and commander of the winner) and combat_event.2111 (-10 minimal_glory_loss for the loser) after the mod\'s force_win (PROTOCOL 10.5). Outcome 3 (abort) has no winner, so no outcome glory.',
      per_knight_rule: {
        hero_downs: 'for every enemy hero whose result killedBy is this acclaimed knight: CK3 fate killed -> +25 (minor_glory_gain, as knight_killed :1265), wounded / maimed / captured -> +10 (minimal_glory_gain, as knight_wounded :741 and knight_maimed :952)',
        troop_kills: 'min(25, round(2 * sqrt(kills - hero downs))) (INFERRED: vanilla has no per-soldier glory; 4 kills -> 4, 30 -> 11, 100 -> 20; never more than one minor gain)',
        own_death: '+25 when his own CK3 fate is killed (knight_killed :1344 adds minor_glory_gain to the dying knight\'s accolade); the successor keeps it and death_battle avoids the succession loss',
        cap: 100,
        floor: 0,
        both_sides: true,
      },
      registers: 'PROPOSAL (owner: CK3 mod, tools/gen/lib/registers.mjs): 8 glory pairs (character tag, 1000 + glory) in the free registers r122..r129 and r190..r197; r198 stays free. The checksum already covers r0..r198. Apply them before the character slots (snippet tools/gen/knights/ck3_accolade_emit_snippet.txt).',
      register_slots: [[122, 123], [124, 125], [126, 127], [128, 129], [190, 191], [192, 193], [194, 195], [196, 197]],
      offset: 1000,
      related: 'Prestige for kills (knight_prestige_gain_on_kill) and knight_increase_prowess_chance_effect are the other write-back items of backlog item 6; acclaimed_knight_prestige_from_victory is automatic (parameters table).',
    },
    modifier_kinds: MODIFIER_KINDS,
    parameters: PARAMS,
    categories: categoryGroups(),
    accolade_maa: maaUnlocks,
    fit_triggers: fits,
    types: orderedTypes,
    names,
    icons: accoladeIcons(),
  };

  // Drift guards (a CK3 patch that changes the shape should be noticed, not silently mapped).
  const warnings = [];
  for (const k of typeKeys) {
    const lv = Object.keys(types[k].levels).sort().join(',');
    if (lv !== '1,2,3') warnings.push(`${k}: levels ${lv} (expected 1,2,3)`);
    if (types[k].battle_base_types.length > 1) warnings.push(`${k}: several battle base types ${types[k].battle_base_types.join(',')} (captain uses the first)`);
    for (const [l, b] of Object.entries(types[k].levels)) {
      if (b.knight_modifier) warnings.push(`${k} level ${l}: knight_modifier present (not mapped yet)`);
      for (const x of ['terrain_bonus', 'winter_bonus', 'holding_bonus']) if (b[x]) warnings.push(`${k} level ${l}: ${x} present (not mapped yet)`);
      for (const p of b.accolade_parameters || []) if (!(p in PARAMS) && /death|battle|army|combat|knight_death|defend/.test(p)) warnings.push(`${k} level ${l}: parameter ${p} looks battle-related but is not in the parameter table`);
    }
  }
  if (unclassified.length) warnings.push(`unclassified knight/army modifiers: ${unclassified.join(', ')}`);
  if (!Array.isArray(glory.rank_thresholds) || glory.rank_thresholds.length !== 6) warnings.push('NAccolade.ACCOLADE_GLORY_LEVELS is not 6 values');
  for (const w of warnings) console.log(`WARNING: ${w}`);

  const json = JSON.stringify(doc, null, 1) + '\n';
  const snippet = snippetText(typeKeys);
  const changed = [];
  for (const [file, text] of [[OUT_JSON, json], [OUT_SNIPPET, snippet]]) {
    const old = fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : null;
    if (old === text) continue;
    changed.push(path.relative(ROOT, file));
    if (!CHECK) {
      fs.writeFileSync(`${file}.tmp`, text, 'utf8');
      fs.renameSync(`${file}.tmp`, file);
    }
  }
  console.log(`${typeKeys.length} accolade types, ${Object.keys(maaUnlocks).length} accolade MaA, ${names.length} name templates; unclassified knight/army modifiers: ${unclassified.length ? unclassified.join(', ') : 'none'}`);
  if (CHECK) {
    if (changed.length) {
      console.log(`would change: ${changed.join(', ')}`);
      process.exit(1);
    }
    console.log('up to date');
  } else console.log(changed.length ? `wrote ${changed.join(', ')}` : 'no changes');
}

function countBy(list) {
  const out = {};
  for (const x of list) out[x ?? 'none'] = (out[x ?? 'none'] || 0) + 1;
  return out;
}

main();
