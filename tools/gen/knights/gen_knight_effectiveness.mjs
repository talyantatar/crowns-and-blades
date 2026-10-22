#!/usr/bin/env node
// gen_knight_effectiveness.mjs - every source of CK3 knight effectiveness (and knight limit) in the user's install.
//
// CK3 has no trigger or script value for a ruler's knight effectiveness (the script_docs dumps have none), so the
// value is rebuilt from its sources. This tool reads ALL of game/common (read-only), finds every modifier block that
// grants `knight_effectiveness_mult`, `knight_effectiveness_per_<skill>` or `knight_limit`, resolves its values
// (`@constants`, `@[ inline math ]`, constant script values), classifies who must hold it, and writes one record per
// grant with the exact CK3 trigger that tests it at battle time from the knights' liege (the army owner).
// Every trigger it writes is checked against the 1.20 script docs (key exists, scope supported, link input/output
// types, database keys exist); the run fails when one does not check out.
//
// Output: bridge/data/ck3_knight_effectiveness_sources.json (UTF-8, no BOM, deterministic).
//
// Usage: node tools/gen/knights/gen_knight_effectiveness.mjs [--check] [--out <file>] [--prowess [<file>]]
//                                                            [--emit-ck3 <file>] [--quiet]
//   --check        write nothing; exit 1 when the output file would change
//   --prowess      also write every `prowess` / `prowess_no_portrait` grant (default bridge/data/ck3_prowess_sources.json;
//                  informational: CK3's `prowess` value is already final)
//   --emit-ck3     also write a DRAFT CK3 scripted effect (liege scope) that saves knight effectiveness in permille
//                  (ck3xbl_a_ke); lint it with node tools/gen/knights/lint_knight_draft.mjs
// The CK3 install is read from CK3_GAME_DIR (default: found in the Steam libraries, tools/gen/lib/game_dirs.cjs); the
// script_docs dumps from CK3XBL_SCRIPT_DOCS, default the CK3 logs folder.
// Full run: node tools/gen/knights/gen_knight_effectiveness.mjs --prowess --emit-ck3 tools/gen/knights/draft/ck3xbl_knight_eff_effects.txt

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { gameDir, listFiles, parseText, readText, scriptDocsDir, walk } from '../lib/pdx.mjs';
import { loadScriptDocs } from '../lib/scopecheck.mjs';
import { knightEffEffectLines } from './knight_eff_emit.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(HERE, '..', '..', '..');
const GAME = gameDir();
const DOCS = scriptDocsDir();
const DEFAULT_OUT = path.join(REPO, 'bridge', 'data', 'ck3_knight_effectiveness_sources.json');
const DEFAULT_PROWESS_OUT = path.join(REPO, 'bridge', 'data', 'ck3_prowess_sources.json');

// ---------------------------------------------------------------------------------------------------------------
// Arguments
// ---------------------------------------------------------------------------------------------------------------

const argv = process.argv.slice(2);
function argValue(flag, fallback) {
  const i = argv.indexOf(flag);
  if (i < 0) return null;
  const v = argv[i + 1];
  return v && !v.startsWith('--') ? v : fallback;
}
const OPT = {
  check: argv.includes('--check'),
  quiet: argv.includes('--quiet'),
  out: argValue('--out', DEFAULT_OUT) || DEFAULT_OUT,
  prowessOut: argv.includes('--prowess') ? argValue('--prowess', DEFAULT_PROWESS_OUT) : null,
  emitCk3: argv.includes('--emit-ck3') ? argValue('--emit-ck3', null) : null,
};
const log = (...a) => { if (!OPT.quiet) console.log(...a); };

// ---------------------------------------------------------------------------------------------------------------
// Reading game files (read-only)
// ---------------------------------------------------------------------------------------------------------------

const g = (...parts) => path.join(GAME, ...parts);
const relGame = (file) => path.relative(GAME, file).split(path.sep).join('/');
const fileCache = new Map();

/** Parses one script file; `@[ a + b ]` inline math is folded into one token first (same line, so lines stay). */
function load(file) {
  if (fileCache.has(file)) return fileCache.get(file);
  let text = readText(file);
  text = text.replace(/@\[([^\]\n]*)\]/g, (m, inner) => `@[${inner.replace(/\s+/g, '')}]`);
  const errors = [];
  const tree = parseText(text, errors);
  const consts = new Map();
  for (const n of tree) if (n.key && n.key.startsWith('@') && n.value !== undefined) consts.set(n.key.slice(1), n.value);
  const entry = { file, rel: relGame(file), text, tree, consts, errors };
  fileCache.set(file, entry);
  return entry;
}

const scriptFiles = (sub, recursive = true) => listFiles(g(...sub.split('/')), ['.txt'], recursive);

// ---------------------------------------------------------------------------------------------------------------
// Values: numbers, file-local @constants, @[inline math], constant script values
// ---------------------------------------------------------------------------------------------------------------

const round6 = (v) => Math.round(v * 1e6) / 1e6;

function evalExpr(expr, consts, depth) {
  const toks = expr.match(/\d+(?:\.\d+)?|[A-Za-z_][A-Za-z0-9_]*|[-+*/()]/g) || [];
  let i = 0;
  const primary = () => {
    const t = toks[i++];
    if (t === '(') { const v = sum(); i++; return v; }
    if (t === '-') return -primary();
    if (t !== undefined && /^\d/.test(t)) return Number(t);
    return constValue(t, consts, depth + 1);
  };
  const prod = () => {
    let v = primary();
    while (toks[i] === '*' || toks[i] === '/') { const op = toks[i++]; const r = primary(); v = op === '*' ? v * r : v / r; }
    return v;
  };
  const sum = () => {
    let v = prod();
    while (toks[i] === '+' || toks[i] === '-') { const op = toks[i++]; const r = prod(); v = op === '+' ? v + r : v - r; }
    return v;
  };
  return sum();
}

function constValue(name, consts, depth) {
  if (depth > 32 || !consts.has(name)) return NaN;
  return scalar(consts.get(name), consts, depth);
}

function scalar(raw, consts, depth = 0) {
  if (typeof raw !== 'string') return NaN;
  if (/^-?\d+(\.\d+)?$/.test(raw)) return Number(raw);
  if (raw.startsWith('@[') && raw.endsWith(']')) return evalExpr(raw.slice(2, -1), consts, depth);
  if (raw.startsWith('@')) return constValue(raw.slice(1), consts, depth);
  return NaN;
}

/** Constant script values (`name = <number|@const|@[..]>` or `name = { value = <...> }`) of common/script_values. */
const SCRIPT_VALUES = new Map();
for (const f of scriptFiles('common/script_values')) {
  const e = load(f);
  for (const n of e.tree) {
    if (!n.key || n.key.startsWith('@')) continue;
    let raw = n.value;
    if (raw === undefined && n.block && n.block.length === 1 && n.block[0].key === 'value') raw = n.block[0].value;
    const v = scalar(raw, e.consts);
    if (Number.isFinite(v)) SCRIPT_VALUES.set(n.key, { value: round6(v), source: `${e.rel}:${n.line}` });
  }
}

/** Every script value name (constant or not). */
const ALL_SCRIPT_VALUES = new Set();
for (const f of scriptFiles('common/script_values')) for (const n of load(f).tree) if (n.key && !n.key.startsWith('@')) ALL_SCRIPT_VALUES.add(n.key);

/** Resolves a modifier value: {value, expr} (expr = the raw text when it is not a plain number). */
function resolveValue(raw, entry) {
  if (raw === undefined) return { value: null, expr: '{block}' };
  if (/^-?\d+(\.\d+)?$/.test(raw)) return { value: Number(raw), expr: null };
  if (raw.startsWith('@')) {
    const v = scalar(raw, entry.consts);
    return { value: Number.isFinite(v) ? round6(v) : null, expr: raw };
  }
  const sv = SCRIPT_VALUES.get(raw);
  return { value: sv ? sv.value : null, expr: raw, exprSource: sv ? sv.source : null };
}

// ---------------------------------------------------------------------------------------------------------------
// Database keys (for trigger value checks)
// ---------------------------------------------------------------------------------------------------------------

function topKeys(sub, recursive = true) {
  const s = new Set();
  for (const f of scriptFiles(sub, recursive)) for (const n of load(f).tree) if (n.key && n.block && !n.key.startsWith('@')) s.add(n.key);
  return s;
}
function nestedKeys(sub, re) {
  const s = new Set();
  for (const f of scriptFiles(sub)) walk(load(f).tree, (n) => { if (n.key && n.block && re.test(n.key)) s.add(n.key); });
  return s;
}
function listedIn(sub, blockKey) {
  const s = new Set();
  for (const f of scriptFiles(sub)) {
    walk(load(f).tree, (n) => {
      if (n.key === blockKey && n.block) for (const c of n.block) { if (c.key === null && c.value) s.add(c.value); else if (c.key) s.add(c.key); }
    });
  }
  return s;
}
function secondLevelKeys(sub) {
  const s = new Set();
  for (const f of scriptFiles(sub)) for (const n of load(f).tree) if (n.block) for (const c of n.block) if (c.key && c.block) s.add(c.key);
  return s;
}

const DB = {
  traits: topKeys('common/traits'),
  perks: topKeys('common/lifestyle_perks'),
  dynastyPerks: topKeys('common/dynasty_perks'),
  traditions: topKeys('common/culture/traditions'),
  pillars: topKeys('common/culture/pillars'),
  innovations: topKeys('common/culture/innovations'),
  tenets: topKeys('common/religion/tenet_types'),
  doctrines: nestedKeys('common/religion/doctrine_types', /^(doctrine|tenet)_/),
  buildings: topKeys('common/buildings'),
  domicileBuildings: topKeys('common/domiciles/buildings'),
  domicileTypes: topKeys('common/domiciles/types'),
  modifiers: topKeys('common/modifiers'),
  courtPositions: topKeys('common/court_positions/types'),
  courtTasks: topKeys('common/court_positions/tasks'),
  courtTypes: topKeys('common/court_types'),
  councilTasks: topKeys('common/council_tasks'),
  laws: new Set([...topKeys('common/laws'), ...secondLevelKeys('common/laws')]),
  governments: topKeys('common/governments'),
  govFlags: listedIn('common/governments', 'flags'),
  holySites: topKeys('common/religion/holy_site_types'),
  accoladeTypes: topKeys('common/accolade_types'),
  confederationTypes: topKeys('common/confederation_types'),
  gameRuleOptions: secondLevelKeys('common/game_rules'),
  culturalParameters: new Set([
    ...listedIn('common/culture/traditions', 'parameters'), ...listedIn('common/culture/pillars', 'parameters'),
    ...listedIn('common/culture/innovations', 'parameters'), ...listedIn('common/culture/cultures', 'parameters'),
  ]),
};

// ---------------------------------------------------------------------------------------------------------------
// Script-doc check of the triggers / script values this tool writes
// ---------------------------------------------------------------------------------------------------------------

const SD = loadScriptDocs(DOCS);
const SAVED_SCOPES = {
  ck3xbl_kl: 'character', // the liege (army owner) - saved by the caller
  holder: 'character', // = liege, for building is_enabled triggers (engine: barony holder)
  county: 'landed_title', // saved per province inside building triggers
  councillor_liege: 'character', // council task scale values (marshal_overtime_boost_scale)
  ck3xbl_kf: 'faith',
};
const CONTROL = new Set(['AND', 'OR', 'NOT', 'NOR', 'NAND', 'trigger_if', 'trigger_else_if', 'trigger_else']);
const VALUE_OPS = new Set(['value', 'add', 'subtract', 'multiply', 'divide', 'modulo', 'min', 'max', 'floor', 'ceiling', 'round']);
const VALUE_DB = {
  has_trait: DB.traits, has_perk: DB.perks, has_dynasty_perk: DB.dynastyPerks, has_cultural_tradition: DB.traditions,
  has_cultural_pillar: DB.pillars, has_innovation: DB.innovations, rite_has_tenet: DB.tenets, has_personal_tenet: DB.tenets,
  has_doctrine: new Set([...DB.doctrines, ...DB.tenets]), has_building: DB.buildings, has_building_or_higher: DB.buildings,
  has_domicile_building: DB.domicileBuildings, has_domicile_building_or_higher: DB.domicileBuildings,
  is_domicile_type: DB.domicileTypes, has_character_modifier: DB.modifiers, has_artifact_modifier: DB.modifiers,
  has_court_position: DB.courtPositions, employs_court_position: DB.courtPositions, has_court_type: DB.courtTypes,
  has_realm_law: DB.laws, has_government: DB.governments, government_has_flag: DB.govFlags,
  is_holy_site_type: DB.holySites, controls_holy_site: DB.holySites, is_performing_council_task: DB.councilTasks,
  has_game_rule: DB.gameRuleOptions, has_cultural_parameter: DB.culturalParameters,
};
const PARAM_BLOCKS = {
  has_trait_xp: { trait: DB.traits, track: null, value: 'num' },
  aptitude: { court_position: DB.courtPositions, value: 'num' },
  accolade_attribute_level: { type: DB.accoladeTypes, level: 'num' },
};
const ITERATOR_PARAMS = { type: DB.courtPositions, is_eminent: null, count: null, percent: null };
const DATA_LINK_DB = { holy_site: DB.holySites, confederation_type: DB.confederationTypes };

function scopeOk(entryScopes, scope) {
  if (!entryScopes || entryScopes.has('none') || entryScopes.has('any')) return true;
  return scope === null || entryScopes.has(scope);
}

/** Vanilla scripted triggers (name -> body text), used inside building is_enabled triggers. */
const SCRIPTED_TRIGGERS = new Map();
for (const f of scriptFiles('common/scripted_triggers')) {
  const e = load(f);
  for (const n of e.tree) if (n.key && n.block) SCRIPTED_TRIGGERS.set(n.key, { entry: e, node: n });
}
const DESC_TRIGGER_PARAMS = new Set(['text', 'subject', 'object', 'value']);

/** Type of a scope path (`scope:x`, `holy_site:key`, `a.b.c`, a link, or a value trigger at the end). */
function pathType(p, scope, errs, prevScope = null) {
  let cur = scope;
  const parts = p.split('.');
  for (let i = 0; i < parts.length; i++) {
    const part = parts[i];
    if (part === 'prev' && i === 0) { cur = prevScope; continue; }
    const colon = part.indexOf(':');
    if (colon > 0) {
      const prefix = part.slice(0, colon);
      const key = part.slice(colon + 1);
      if (prefix === 'scope') {
        if (!(key in SAVED_SCOPES)) errs.push(`unknown saved scope '${part}'`);
        cur = SAVED_SCOPES[key] || null;
        continue;
      }
      const dl = SD.dataLinks.get(prefix);
      if (!dl) { errs.push(`unknown data link '${prefix}:'`); return null; }
      if (DATA_LINK_DB[prefix] && !DATA_LINK_DB[prefix].has(key)) errs.push(`'${part}': no such ${prefix}`);
      cur = dl.output;
      continue;
    }
    if (['this', 'root', 'prev'].includes(part)) continue;
    const link = SD.links.get(part);
    if (link) {
      if (link.input && cur && !link.input.has(cur)) errs.push(`link '${part}' does not take a ${cur} scope`);
      cur = link.output;
      continue;
    }
    const trig = SD.triggers.get(part);
    if (trig && i === parts.length - 1 && trig.traits && trig.traits.includes('<')) {
      if (!scopeOk(trig.scopes, cur)) errs.push(`'${part}' is not supported in a ${cur} scope`);
      return 'value';
    }
    errs.push(`unknown link or value '${part}' in '${p}'`);
    return null;
  }
  return cur;
}

function checkValueRef(raw, scope, errs) {
  if (raw === undefined || /^-?\d+(\.\d+)?$/.test(raw)) return;
  if (SCRIPT_VALUES.has(raw)) return;
  if (raw.includes(':') || raw.includes('.')) { pathType(raw, scope, errs); return; }
  const trig = SD.triggers.get(raw);
  if (trig && trig.traits && trig.traits.includes('<')) { if (!scopeOk(trig.scopes, scope)) errs.push(`'${raw}' not in ${scope} scope`); return; }
  if (ALL_SCRIPT_VALUES.has(raw)) return; // non-constant script value
  errs.push(`unknown value '${raw}'`);
}

/**
 * Checks script nodes against the script docs. ctx: 'trigger' or 'value'. `prev` is the scope type before the block
 * the nodes belong to (what `prev` means there).
 */
function checkNodes(nodes, scope, ctx, errs, prev = null) {
  for (const n of nodes) {
    const k = n.key;
    if (k === null) { errs.push(`bare item '${n.value ?? '{...}'}'`); continue; }
    if (CONTROL.has(k)) { if (n.block) checkNodes(n.block, scope, 'trigger', errs, prev); continue; }
    if (k === 'limit') { checkNodes(n.block || [], scope, 'trigger', errs, prev); continue; }
    if (['if', 'else_if', 'else'].includes(k) && ctx === 'value') { checkNodes(n.block || [], scope, 'value', errs, prev); continue; }
    if (ctx === 'value' && VALUE_OPS.has(k)) {
      if (n.block) checkNodes(n.block, scope, 'value', errs, prev); else checkValueRef(n.value, scope, errs);
      continue;
    }
    if (k === 'save_temporary_scope_as') continue;
    if (k === 'custom_description' || k === 'custom_tooltip') {
      checkNodes((n.block || []).filter((c) => !DESC_TRIGGER_PARAMS.has(c.key)), scope, 'trigger', errs, prev);
      continue;
    }
    const iter = /^(any|every)_(.+)$/.exec(k);
    if (iter && n.block) {
      const t = SD.triggers.get(`any_${iter[2]}`);
      if (!t) { errs.push(`unknown iterator '${k}'`); continue; }
      if (iter[1] === 'every' && ctx !== 'value') errs.push(`'${k}' outside a value block`);
      if (iter[1] === 'any' && ctx !== 'trigger') errs.push(`'${k}' outside a trigger block`);
      if (!scopeOk(t.scopes, scope)) errs.push(`'${k}' is not supported in a ${scope} scope`);
      const inner = n.block.filter((c) => {
        if (c.key in ITERATOR_PARAMS) {
          const set = ITERATOR_PARAMS[c.key];
          if (set && !set.has(c.value)) errs.push(`${k}: unknown ${c.key} '${c.value}'`);
          return false;
        }
        return true;
      });
      checkNodes(inner, t.targets || null, iter[1] === 'every' ? 'value' : 'trigger', errs, scope);
      continue;
    }
    if (k.includes(':') || k.includes('.') || k === 'prev') {
      const s = pathType(k, scope, errs, prev);
      if (n.block) checkNodes(n.block, s, ctx, errs, scope);
      else if (n.value) pathType(n.value, scope, errs, prev);
      continue;
    }
    const link = SD.links.get(k);
    const trig = SD.triggers.get(k);
    if (n.block && link && !(trig && PARAM_BLOCKS[k])) {
      if (link.input && scope && !link.input.has(scope)) errs.push(`link '${k}' does not take a ${scope} scope`);
      checkNodes(n.block, link.output, ctx, errs, scope);
      continue;
    }
    if (trig) {
      if (ctx !== 'trigger') errs.push(`trigger '${k}' outside a trigger block`);
      if (!scopeOk(trig.scopes, scope)) errs.push(`'${k}' is not supported in a ${scope} scope`);
      if (n.block) {
        const params = PARAM_BLOCKS[k];
        if (!params) { errs.push(`no parameter table for block trigger '${k}'`); continue; }
        for (const c of n.block) {
          if (!(c.key in params)) errs.push(`${k}: unknown parameter '${c.key}'`);
          else if (params[c.key] instanceof Set && !params[c.key].has(c.value)) errs.push(`${k}: unknown ${c.key} '${c.value}'`);
        }
      } else if (VALUE_DB[k] && !VALUE_DB[k].has(n.value)) {
        errs.push(`${k} = ${n.value}: no such database entry`);
      } else if (n.value && (n.value.includes(':') || n.value.includes('.') || n.value === 'prev')) {
        pathType(n.value, scope, errs, prev);
      }
      continue;
    }
    if (link && !n.block) { // scope comparison: `holder = scope:x`, `confederation_type = confederation_type:y`
      if (link.input && scope && !link.input.has(scope)) errs.push(`link '${k}' does not take a ${scope} scope`);
      const vt = pathType(n.value, scope, errs, prev);
      if (vt && link.output && vt !== link.output) errs.push(`'${k} = ${n.value}' compares ${link.output} with ${vt}`);
      continue;
    }
    const st = SCRIPTED_TRIGGERS.get(k);
    if (st) { // vanilla scripted trigger (building is_enabled): `= yes/no` or parameters
      if (ctx !== 'trigger') errs.push(`scripted trigger '${k}' outside a trigger block`);
      if (n.block) for (const c of n.block) if (c.value && (c.value.includes(':') || c.value.includes('.'))) pathType(c.value, scope, errs, prev);
      continue;
    }
    errs.push(`unknown key '${k}'`);
  }
}

/** Returns a list of problems of a script text evaluated in `scope` (character = the liege). */
function checkScript(text, ctx, scope = 'character') {
  const errs = [];
  const perr = [];
  const tree = parseText(text, perr);
  for (const e of perr) errs.push(`parse: ${e.message}`);
  checkNodes(tree, scope, ctx, errs, null);
  return errs;
}

// ---------------------------------------------------------------------------------------------------------------
// Grants: modifier blocks with knight keys
// ---------------------------------------------------------------------------------------------------------------

const KE_MULT = 'knight_effectiveness_mult';
const KE_PER = /^knight_effectiveness_per_([a-z]+)$/;
const SKILLS = new Set(['martial', 'stewardship', 'diplomacy', 'intrigue', 'learning', 'prowess', 'dread', 'tyranny']);
const PROWESS_KEYS = new Set(['prowess', 'prowess_no_portrait']);
const IS_KE = (k) => k === KE_MULT || KE_PER.test(k);
const WARNINGS = [];

// Folders whose knight keys are definitions, not grants (handled separately or not at all).
const NOT_GRANTS = ['common/modifier_definition_formats/', 'common/game_concepts/', 'common/script_values/', 'common/defines/',
  'common/artifacts/blueprints/'];

// Block keys that hold character modifiers (only used to accept prowess-only blocks; knight keys appear nowhere else).
const MODIFIER_CONTAINERS = new Set([
  'character_modifier', 'government_character_modifier', 'faith_character_modifier', 'faith_character_modifier_scaled',
  'county_holder_character_modifier', 'character_culture_modifier', 'characer_dynasty_modifier', 'character_dynasty_modifier',
  'personal_tenet_modifier', 'owner_modifier', 'liege_modifier', 'knight_modifier', 'council_owner_modifier',
  'any_member_house_member', 'any_member_house_head', 'leading_house_head', 'leading_house_member', 'aligned_house_head',
  'aligned_house_member', 'divergent_house_head', 'divergent_house_member', 'base_employer_modifier', 'employee_modifier',
  'culture_modifier', 'faith_modifier', 'domicile_temperament_high_modifier', 'domicile_temperament_low_modifier',
]);

function isModifierBlock(rel, n, parents) {
  if (MODIFIER_CONTAINERS.has(n.key)) return true;
  if (rel.startsWith('common/modifiers/') && parents.length === 0) return true;
  if (rel.startsWith('common/traits/') && (parents.length === 0 || parents.some((p) => p.key === 'track' || p.key === 'tracks'))) return true;
  const parent = parents[parents.length - 1];
  if (parent && (parent.key === 'scaling_employer_modifiers' || parent.key === 'scaling_employer_court_modifiers')) return true;
  if (n.key === 'modifier' && (rel.startsWith('common/court_positions/types/') || rel.startsWith('common/laws/'))) return true;
  return false;
}

/**
 * Every block with knight keys (knight_effectiveness_*, knight_limit) is a grant; with `wantProwess`, modifier blocks
 * that only grant prowess are grants too. Prowess values are recorded on every grant either way, so the knight table
 * does not depend on the flag.
 */
function collectGrants(wantProwess) {
  const grants = [];
  const files = [...scriptFiles('common'), ...scriptFiles('events')];
  for (const f of files) {
    const e = load(f);
    if (NOT_GRANTS.some((p) => e.rel.startsWith(p))) continue;
    if (!/knight_effectiveness|knight_limit|prowess/.test(e.text)) continue;
    walk(e.tree, (n, parents) => {
      if (!n.block) return;
      const knightKeys = n.block.filter((c) => c.key && (c.key.startsWith('knight_effectiveness') || c.key === 'knight_limit'));
      const prowessKeys = n.block.filter((c) => c.key && PROWESS_KEYS.has(c.key));
      const isGrant = knightKeys.length > 0 || (wantProwess && prowessKeys.length > 0 && isModifierBlock(e.rel, n, parents));
      if (!isGrant) return;
      const fields = { mult: null, perSkill: [], knightLimit: null, prowess: null, raw: [] };
      for (const c of [...knightKeys, ...prowessKeys].sort((a, b) => a.line - b.line)) {
        if (c.key.startsWith('knight_effectiveness') && !(c.key === KE_MULT || (KE_PER.test(c.key) && SKILLS.has(KE_PER.exec(c.key)[1])))) {
          WARNINGS.push(`${e.rel}:${c.line}: unknown knight modifier '${c.key}' (new skill?) - not counted`);
          continue;
        }
        const v = resolveValue(c.value, e);
        if (v.value === null && !PROWESS_KEYS.has(c.key)) WARNINGS.push(`${e.rel}:${c.line}: value of ${c.key} '${c.value}' not resolved`);
        fields.raw.push({ key: c.key, ...v, line: c.line });
        if (c.key === KE_MULT) fields.mult = round6((fields.mult || 0) + (v.value || 0));
        else if (KE_PER.test(c.key)) fields.perSkill.push({ skill: KE_PER.exec(c.key)[1], value: v.value });
        else if (c.key === 'knight_limit') fields.knightLimit = round6((fields.knightLimit || 0) + (v.value || 0));
        else fields.prowess = round6((fields.prowess || 0) + (v.value || 0));
      }
      if (fields.raw.length) grants.push({ entry: e, node: n, parents, fields });
    });
  }
  return grants;
}

// ---------------------------------------------------------------------------------------------------------------
// Static modifiers: how each one is applied (character / artifact / game rule / engine)
// ---------------------------------------------------------------------------------------------------------------

function scriptedEffectBodies() {
  const map = new Map();
  for (const f of scriptFiles('common/scripted_effects')) {
    const e = load(f);
    for (const n of e.tree) if (n.key && n.block) map.set(n.key, { entry: e, node: n });
  }
  return map;
}

function textOf(entry, node) {
  // the source text of a node (from its line to its closing brace line)
  const lines = entry.text.split('\n');
  let depth = 0;
  const out = [];
  for (let i = node.line - 1; i < lines.length; i++) {
    const l = lines[i].replace(/#.*$/, '');
    out.push(lines[i]);
    for (const ch of l) { if (ch === '{') depth++; else if (ch === '}') depth--; }
    if (depth <= 0 && i >= node.line - 1 && /[{}]/.test(l)) break;
    if (depth <= 0 && !/[{}]/.test(l)) break;
  }
  return out.join('\n');
}

function staticModifierUsage(names) {
  const usage = new Map([...names].map((n) => [n, []]));
  if (!names.size) return usage;
  const effects = scriptedEffectBodies();
  const re = new RegExp(`\\b(${[...names].join('|')})\\b`);
  const files = [...scriptFiles('common'), ...scriptFiles('events'), ...scriptFiles('history')];
  for (const f of files) {
    if (f.includes(`${path.sep}modifiers${path.sep}`) && relGame(f).startsWith('common/modifiers/')) continue;
    const raw = readText(f);
    if (!re.test(raw)) continue;
    const e = load(f);
    walk(e.tree, (n, parents) => {
      if (n.value === undefined) return;
      const m = /^(?:(player|ai|all):)?([A-Za-z0-9_]+)$/.exec(n.value);
      if (!m || !names.has(m[2])) return;
      const name = m[2];
      const keys = parents.map((p) => p.key);
      let how = 'unknown';
      const ctx = { file: `${e.rel}:${n.line}`, how };
      if (['add_character_modifier', 'remove_character_modifier', 'has_character_modifier', 'has_character_modifier_duration_remaining'].includes(n.key)
        || (n.key === 'modifier' && keys.includes('add_character_modifier'))) how = 'character';
      else if (['add_artifact_modifier', 'has_artifact_modifier', 'remove_artifact_modifier'].includes(n.key)
        || (n.key === 'modifier' && (keys.includes('create_artifact') || keys.some((k) => k && /artifact/.test(k))))) how = 'artifact';
      else if (n.key === 'apply_modifier' && e.rel.startsWith('common/game_rules/')) {
        how = 'game_rule';
        ctx.rule = parents[0]?.key;
        ctx.option = parents[parents.length - 1]?.key;
        ctx.who = m[1] || 'all';
      } else if (/^[A-Z][A-Z0-9_]*$/.test(n.key) && parents.length) {
        const eff = effects.get(parents[parents.length - 1].key);
        if (eff) {
          const body = textOf(eff.entry, eff.node);
          if (new RegExp(`add_artifact_modifier\\s*=\\s*\\$${n.key}\\$`).test(body)) how = 'artifact';
          else if (new RegExp(`\\$${n.key}\\$`).test(body) && /add_character_modifier/.test(body)) how = 'character';
          ctx.via = parents[parents.length - 1].key;
        }
      } else if (['localization_key', 'desc'].includes(n.key)) {
        return; // a loc key with the same name, not an application
      }
      ctx.how = how;
      if (how === 'artifact') {
        // rarity limit and random_list weight around an artifact pool entry
        for (let i = parents.length - 1; i >= 0; i--) {
          const p = parents[i];
          const lim = p.block?.find((c) => c.key === 'limit' && c.block);
          const rar = lim?.block.find((c) => c.key === 'rarity');
          if (rar && !ctx.rarity) ctx.rarity = rar.value;
          if (/^\d+$/.test(p.key || '') && parents[i - 1]?.key === 'random_list' && ctx.weight === undefined) ctx.weight = Number(p.key);
        }
        ctx.effect = parents[0]?.key;
      }
      usage.get(name).push(ctx);
    });
  }
  return usage;
}

// ---------------------------------------------------------------------------------------------------------------
// Classification: kind, key, who holds it, the trigger (liege scope), stacking count, scale
// ---------------------------------------------------------------------------------------------------------------

const APTITUDE_LEVELS = ['terrible', 'poor', 'average', 'good', 'excellent'];
const indent = (s) => s.replace(/\s+/g, ' ').trim();

/** Common condition sub-blocks of modifier blocks: parameter / doctrine / flag / dynasty perk. */
function blockConditions(n) {
  const conds = [];
  const texts = [];
  const get = (key) => n.block.find((c) => c.key === key);
  const param = get('parameter');
  if (param) { conds.push(`culture ?= { has_cultural_parameter = ${param.value} }`); texts.push(`holder's culture has parameter ${param.value}`); }
  const doctrine = get('doctrine');
  if (doctrine) { conds.push(`faith = { has_doctrine = ${doctrine.value} }`); texts.push(`holder's faith has doctrine ${doctrine.value}`); }
  const flag = get('flag');
  if (flag) {
    const inv = get('invert_check')?.value === 'yes';
    conds.push(inv ? `NOT = { government_has_flag = ${flag.value} }` : `government_has_flag = ${flag.value}`);
    texts.push(`holder's government ${inv ? 'lacks' : 'has'} flag ${flag.value}`);
  }
  const dperk = get('county_holder_dynasty_perk');
  if (dperk) { conds.push(`dynasty ?= { has_dynasty_perk = ${dperk.value} }`); texts.push(`county holder's dynasty has perk ${dperk.value} (approximated by the liege's dynasty)`); }
  const scale = get('scale');
  return { conds, texts, scale };
}

function scaleText(entry, scaleNode) {
  if (!scaleNode) return null;
  if (scaleNode.value !== undefined) return scaleNode.value;
  const body = textOf(entry, scaleNode).replace(/^\s*scale\s*=\s*\{/, '').replace(/\}\s*$/, '');
  return indent(body.replace(/#[^\n]*/g, ''));
}

function holySiteInfo(entry, name) {
  const top = entry.tree.find((t) => t.key === name);
  const county = top?.block?.find((c) => c.key === 'county')?.value || null;
  return { county, dynamic: !county };
}

/** Serializes parsed nodes back to one line of script. */
function serialize(nodes) {
  return nodes.map((n) => {
    const v = (x, t) => (t === 'string' ? `"${x}"` : x);
    if (n.key === null) return n.block ? `{ ${serialize(n.block)} }` : v(n.value, n.valueType);
    const k = n.keyType === 'string' ? `"${n.key}"` : n.key;
    return n.block ? `${k} ${n.op} { ${serialize(n.block)} }`.replace('{  }', '{ }') : `${k} ${n.op} ${v(n.value, n.valueType)}`;
  }).join(' ');
}

/**
 * A building's is_enabled trigger as one line, with custom_description/custom_tooltip wrappers replaced by AND
 * (same logic; their texts are tooltip loc keys only).
 */
function buildingEnabled(entry, top) {
  const en = top.block.find((c) => c.key === 'is_enabled' && c.block);
  if (!en || !en.block.length) return null;
  const unwrap = (nodes) => nodes.map((n) => {
    if (!n.block) return n;
    if (n.key === 'custom_description' || n.key === 'custom_tooltip') {
      return { ...n, key: 'AND', keyType: 'word', block: unwrap(n.block.filter((c) => !DESC_TRIGGER_PARAMS.has(c.key))) };
    }
    return { ...n, block: unwrap(n.block) };
  });
  const nodes = unwrap(en.block);
  // a lone top-level AND is redundant inside the province iterator
  const flat = nodes.flatMap((n) => (n.key === 'AND' && n.block ? n.block : [n]));
  return serialize(flat);
}

/**
 * Classifies one grant into record stubs. A stub: {kind, subkind, key, block, scope_owner, trigger, count, scale,
 * conditional, condition_text, testable, approximation, exclusive_group, notes}.
 */
function classify(grant, usage) {
  const { entry, node, parents } = grant;
  const rel = entry.rel;
  const top = parents[0] || node;
  const key = top.key;
  const bk = node.key; // the modifier block's own key
  const stub = {
    kind: 'other', subkind: null, key, block: bk, scope_owner: 'liege', trigger: null, count: null, scale: null,
    conditional: false, condition_text: null, testable: 'exact', approximation: null, exclusive_group: null, notes: [],
  };
  const conds = blockConditions(node);
  const addConds = (base) => (conds.conds.length ? `${base} ${conds.conds.join(' ')}` : base);
  if (conds.conds.length) { stub.conditional = true; stub.condition_text = conds.texts.join('; '); }
  const within = (k) => parents.some((p) => p.key === k);
  const out = [stub];

  if (rel.startsWith('common/modifiers/')) {
    const uses = usage.get(key) || [];
    const hows = [...new Set(uses.map((u) => u.how))];
    stub.notes.push(`applied by: ${uses.length ? uses.map((u) => `${u.how}@${u.file}${u.via ? ` via ${u.via}` : ''}`).slice(0, 8).join(', ') : 'nothing in script (engine)'}`);
    const make = (how) => {
      const s = { ...stub, notes: [...stub.notes] };
      if (how === 'character' || how === 'unknown') {
        Object.assign(s, { kind: 'character_modifier', subkind: 'static_character_modifier', trigger: `has_character_modifier = ${key}` });
        if (how === 'unknown') { s.testable = 'inferred'; s.notes.push('application context not recognised; assumed a character modifier'); }
      } else if (how === 'artifact') {
        const benefit = `has_artifact_modifier = ${key} scope:ck3xbl_kl = { can_benefit_from_artifact = prev }`;
        Object.assign(s, {
          kind: 'artifact_template', subkind: 'artifact_modifier', scope_owner: 'liege_equipped_artifact',
          trigger: `any_equipped_character_artifact = { ${benefit} }`,
          count: `every_equipped_character_artifact = { limit = { ${benefit} } add = 1 }`,
          testable: 'inferred',
        });
        s.notes.push('equipped inventory artifacts; court-slot artifacts are assumed to count as equipped (is_equipped); the fallback modifier of a template is not a knight source');
        s.artifact_pool = uses.filter((u) => u.how === 'artifact').map((u) => ({ source_file: u.file, effect: u.effect || null, via: u.via || null, rarity: u.rarity || null, weight: u.weight ?? null }));
      } else if (how === 'game_rule') {
        const u = uses.find((x) => x.how === 'game_rule');
        const who = u.who === 'player' ? ' is_ai = no' : u.who === 'ai' ? ' is_ai = yes' : '';
        Object.assign(s, { kind: 'other', subkind: 'game_rule', trigger: `has_game_rule = ${u.option}${who}`, conditional: true, condition_text: `game rule ${u.rule} = ${u.option}, applied to ${u.who}` });
      }
      return s;
    };
    if (!uses.length) {
      Object.assign(stub, { kind: 'other', subkind: 'engine_modifier', trigger: null, testable: 'no', approximation: 'applied by the engine (no script reference); not testable by key' });
      return out;
    }
    const kinds = hows.filter((h) => h !== 'unknown');
    return (kinds.length ? kinds : ['unknown']).map(make);
  }

  if (rel.startsWith('common/traits/')) {
    stub.kind = 'trait';
    let base = `has_trait = ${key}`;
    const trackIdx = parents.findIndex((p) => p.key === 'track' || p.key === 'tracks');
    if (trackIdx >= 0) {
      const xp = node.key; // XP threshold
      const trackName = parents[trackIdx].key === 'tracks' ? parents[trackIdx + 1]?.key : null;
      base = `has_trait_xp = { trait = ${key}${trackName ? ` track = ${trackName}` : ''} value >= ${xp} }`;
      stub.subkind = 'trait_xp_track';
      stub.block = `track${trackName ? `:${trackName}` : ''}:${xp}`;
      stub.conditional = true;
      stub.condition_text = [`trait XP${trackName ? ` (track ${trackName})` : ''} >= ${xp}`, stub.condition_text].filter(Boolean).join('; ');
      stub.testable = 'inferred';
      stub.notes.push('track thresholds assumed cumulative (every reached threshold applies)');
    }
    stub.trigger = addConds(base);
    return out;
  }

  if (rel.startsWith('common/lifestyle_perks/')) {
    stub.kind = 'perk';
    stub.subkind = bk === 'government_character_modifier' ? 'perk_government_conditional' : 'perk';
    if (bk === 'government_character_modifier') {
      const flag = node.block.find((c) => c.key === 'flag')?.value;
      const inv = node.block.find((c) => c.key === 'invert_check')?.value === 'yes';
      stub.block = `government:${inv ? 'not:' : ''}${flag}`;
    }
    stub.trigger = addConds(`has_perk = ${key}`);
    return out;
  }

  if (rel.startsWith('common/dynasty_perks/')) {
    Object.assign(stub, { kind: 'dynasty_perk', scope_owner: 'liege_dynasty', trigger: addConds(`dynasty ?= { has_dynasty_perk = ${key} }`) });
    return out;
  }

  if (rel.startsWith('common/culture/traditions/') || rel.startsWith('common/culture/pillars/')) {
    const pillar = rel.startsWith('common/culture/pillars/');
    Object.assign(stub, {
      kind: pillar ? 'other' : 'tradition', subkind: pillar ? 'pillar' : 'tradition', scope_owner: 'liege_culture',
      trigger: addConds(`culture ?= { ${pillar ? 'has_cultural_pillar' : 'has_cultural_tradition'} = ${key} }`),
    });
    if (bk !== 'character_modifier' && bk !== 'faith_character_modifier') stub.notes.push(`block '${bk}' is not a character modifier`);
    return out;
  }

  if (rel.startsWith('common/culture/innovations/')) {
    Object.assign(stub, { kind: 'innovation', scope_owner: 'liege_culture', trigger: addConds(`culture ?= { has_innovation = ${key} }`) });
    return out;
  }

  if (rel.startsWith('common/religion/tenet_types/') || rel.startsWith('common/religion/doctrine_types/')) {
    stub.kind = 'tenet';
    const isTenet = rel.startsWith('common/religion/tenet_types/');
    const dkey = isTenet ? key : parents[parents.length - 1]?.key || key;
    stub.key = dkey;
    if (bk === 'personal_tenet_modifier') {
      Object.assign(stub, { subkind: 'personal_tenet', trigger: addConds(`has_personal_tenet = ${dkey}`) });
    } else if (isTenet) {
      Object.assign(stub, { subkind: 'rite_tenet', scope_owner: 'liege_rite', trigger: addConds(`rite ?= { rite_has_tenet = ${dkey} }`) });
      stub.testable = 'inferred';
      stub.notes.push('1.20 tenets are core tenets of the character\'s rite (rite_types info); faith = { has_tenet } would test the faith instead');
    } else {
      Object.assign(stub, { subkind: 'doctrine', scope_owner: 'liege_faith', trigger: addConds(`faith = { has_doctrine = ${dkey} }`) });
    }
    return out;
  }

  if (rel.startsWith('common/religion/holy_site_types/')) {
    // The county holder is read as holy_site_county.holder. holy_site_controller is not used: the docs give it a landed_title
    // output, but CK3 1.20 returns a character (in game, error.log: "Event target link 'holder' did not get a matching scope
    // type. Expected 'landed_title', but got 'character'").
    stub.kind = 'holy_site';
    const hs = holySiteInfo(entry, key);
    if (bk === 'county_holder_character_modifier') {
      stub.subkind = 'holy_site_county_holder';
      stub.scope_owner = 'liege_holds_holy_site_county';
      stub.trigger = hs.dynamic
        ? `faith = { any_holy_site = { is_holy_site_type = ${key} holy_site_county = { holder ?= scope:ck3xbl_kl } } }`
        : `faith = { is_faith_holy_site = holy_site:${key} } holy_site:${key} = { holy_site_county = { holder ?= scope:ck3xbl_kl } }`;
      stub.conditional = true;
      stub.condition_text = `liege holds the county of holy site ${key}${hs.county ? ` (${hs.county})` : ' (dynamic site)'} and it is a holy site of his faith (regular or eminent)`;
      stub.testable = 'inferred';
    } else if (bk === 'faith_character_modifier' || bk === 'faith_character_modifier_scaled') {
      stub.subkind = bk === 'faith_character_modifier' ? 'holy_site_faith' : 'holy_site_faith_scaled';
      stub.scope_owner = 'liege_faith';
      stub.trigger = hs.dynamic
        ? `faith = { save_temporary_scope_as = ck3xbl_kf any_eminent_holy_site = { is_holy_site_type = ${key} holy_site_county = { holder ?= { faith = scope:ck3xbl_kf } } } }`
        : `faith = { is_faith_eminent_holy_site = holy_site:${key} controls_holy_site = ${key} }`;
      stub.conditional = true;
      stub.condition_text = `holy site ${key} is an EMINENT holy site of the liege's faith and is held by that faith`;
      stub.testable = 'inferred';
      if (bk === 'faith_character_modifier_scaled') {
        const sc = scaleText(entry, conds.scale);
        stub.scale = {
          script_value: sc,
          liege_scope_expr: hs.dynamic ? null : `holy_site:${key}.total_active_artifact_rarity_sum`,
          meaning: 'sum of rarity levels (common 1 .. illustrious 4) of holy relics enshrined at the site',
        };
        stub.condition_text += `; value x ${sc}`;
        if (hs.dynamic) { stub.testable = 'approx'; stub.approximation = 'dynamic site: relic rarity sum not addressable by key from the liege; use 0 (or the eminent-site count)'; }
      }
    } else {
      stub.notes.push(`holy site block '${bk}' is not a character modifier`);
      stub.testable = 'no';
    }
    return out;
  }

  if (rel.startsWith('common/buildings/')) {
    stub.kind = 'building';
    stub.subkind = bk === 'character_modifier' ? 'holding_building' : `holding_building_${bk}`;
    stub.scope_owner = 'liege_held_holding';
    const enabled = buildingEnabled(entry, top);
    let inner = `has_building = ${key}`;
    if (enabled) {
      inner += `${/scope:county\b/.test(enabled) ? ' county = { save_temporary_scope_as = county }' : ''} ${enabled}`;
      stub.conditional = true;
      stub.condition_text = [stub.condition_text, `building is_enabled: ${enabled}`].filter(Boolean).join('; ');
      stub.notes.push(`is_enabled is evaluated in the province with scope:holder = the liege (save_scope_as = holder before use)${/scope:county\b/.test(enabled) ? ' and scope:county = its county' : ''}`);
    }
    stub.notes.push('owner of the holding = barony holder; every holding with the building adds its modifier once (stacks per holding) [inferred]');
    if (!['character_modifier', 'character_culture_modifier', 'characer_dynasty_modifier', 'character_dynasty_modifier'].includes(bk)) {
      stub.notes.push(`block '${bk}' is not a character modifier`);
      stub.testable = 'no';
    }
    // culture parameter / dynasty perk conditions are on the holder (= liege): outside the province iterator
    const outer = conds.conds.join(' ');
    stub.trigger = `any_directly_owned_province = { ${inner} }${outer ? ` ${outer}` : ''}`;
    stub.count = `every_directly_owned_province = { limit = { ${inner} } add = 1 }`;
    stub.count_condition = outer || null;
    return out;
  }

  if (rel.startsWith('common/domiciles/buildings/')) {
    Object.assign(stub, {
      kind: 'building', subkind: 'domicile_building', scope_owner: 'liege_domicile',
      trigger: addConds(`domicile ?= { has_domicile_building_or_higher = ${key} }`),
    });
    stub.notes.push('domicile buildings inherit the character modifiers of their previous levels (domicile buildings info): every reached level adds its own value');
    return out;
  }

  if (rel.startsWith('common/domiciles/types/')) {
    const temper = /^domicile_temperament_(high|low)_modifier$/.exec(bk);
    Object.assign(stub, { kind: 'other', subkind: temper ? `domicile_temperament_${temper[1]}` : `domicile_type_${bk}`, scope_owner: 'liege_domicile' });
    const base = `domicile ?= { is_domicile_type = ${key} }${temper ? ` has_domicile_temperament_${temper[1]} = yes` : ''}`;
    stub.trigger = base;
    if (conds.scale) {
      const sc = scaleText(entry, conds.scale);
      stub.scale = { script_value: sc, liege_scope_expr: `{ ${sc} }`, meaning: 'scale evaluated in the domicile owner\'s scope (perk gates inside)' };
      stub.conditional = true;
      stub.condition_text = `camp temperament ${temper ? temper[1] : '?'}; value x scale { ${sc} }`;
    }
    return out;
  }

  if (rel.startsWith('common/court_positions/types/')) {
    stub.kind = 'court_position';
    if (within('scaling_employer_modifiers')) {
      const levelName = node.key;
      const defined = (parents.find((p) => p.key === 'scaling_employer_modifiers').block || []).map((c) => c.key);
      const idx = (name) => (APTITUDE_LEVELS.includes(name) ? APTITUDE_LEVELS.indexOf(name) + 1 : Number((/(\d+)$/.exec(name) || [])[1]));
      const lv = idx(levelName);
      const levels = defined.map(idx).filter(Number.isFinite).sort((a, b) => a - b);
      const nextLv = levels.find((l) => l > lv);
      const apt = nextLv === undefined
        ? `aptitude = { court_position = ${key} value >= ${lv} }`
        : nextLv === lv + 1
          ? `aptitude = { court_position = ${key} value = ${lv} }`
          : `aptitude = { court_position = ${key} value >= ${lv} } aptitude = { court_position = ${key} value < ${nextLv} }`;
      Object.assign(stub, {
        subkind: 'court_position_employer', scope_owner: 'liege_court_position_employee', block: `scaling_employer_modifiers:${levelName}`,
        trigger: addConds(`any_court_position_holder = { type = ${key} ${apt} }`),
        count: `every_court_position_holder = { type = ${key} limit = { ${apt} } add = 1 }`,
        conditional: true, condition_text: `liege employs a ${key} with aptitude level ${lv} (${levelName})`,
        exclusive_group: `court_position:${key}:aptitude`,
      });
      stub.notes.push('aptitude levels terrible..excellent = 1..5 (aptitude_level_breakpoints, court positions info)');
    } else if (bk === 'modifier') {
      Object.assign(stub, { subkind: 'court_position_holder', scope_owner: 'liege_as_court_position_holder', trigger: addConds(`has_court_position = ${key}`) });
      stub.notes.push('applies when the liege himself holds this position at another ruler\'s court');
    } else if (bk === 'base_employer_modifier') {
      Object.assign(stub, { subkind: 'court_position_employer_base', scope_owner: 'liege_court_position_employee', trigger: addConds(`employs_court_position = ${key}`) });
    } else if (bk === 'culture_modifier' || bk === 'rite_modifier') {
      Object.assign(stub, { subkind: `court_position_${bk}`, trigger: addConds(`OR = { has_court_position = ${key} employs_court_position = ${key} }`) });
    } else {
      Object.assign(stub, { subkind: `court_position_${bk}`, scope_owner: 'courtiers', testable: 'no', approximation: 'goes to courtiers, not to the liege' });
    }
    return out;
  }

  if (rel.startsWith('common/court_positions/tasks/')) {
    const types = top.block.find((c) => c.key === 'court_position_types' || c.key === 'position_types');
    const pos = types?.block?.find((c) => c.key === null)?.value || '?';
    const levelName = node.key;
    const lv = APTITUDE_LEVELS.indexOf(levelName) + 1;
    Object.assign(stub, {
      kind: 'court_position', subkind: 'court_position_task', scope_owner: 'liege_court_position_employee', block: `scaling_employer_modifiers:${levelName}`,
      trigger: null, count: null, conditional: true,
      condition_text: `liege's ${pos} performs task ${key} with aptitude level ${lv} (${levelName})`,
      testable: 'no', exclusive_group: `court_position_task:${key}`,
      approximation: `no trigger reads a court position's active task (1.20 script docs); upper bound: any_court_position_holder = { type = ${pos} aptitude = { court_position = ${pos} value ${lv === 5 ? '>=' : '='} ${lv} } }. Default: not counted`,
    });
    stub.position = pos;
    return out;
  }

  if (rel.startsWith('common/court_types/')) {
    const lp = parents.find((p) => p.key === 'level_perk');
    const level = lp?.block?.find((c) => c.key === 'court_grandeur')?.value;
    // has_court_type errors for a character without a royal court ("Character must have a royal court"), so the royal court is
    // tested first, as vanilla does (common/scripted_triggers/00_artifact_triggers.txt:2210-2214).
    Object.assign(stub, {
      kind: 'other', subkind: 'court_type', scope_owner: 'liege_court', block: `level_perk:${level}`,
      trigger: addConds(`has_royal_court = yes has_court_type = ${key} court_grandeur_current_level >= ${level}`),
      conditional: true, condition_text: `court type ${key} and court grandeur level >= ${level} (level perks are cumulative)`,
    });
    return out;
  }

  if (rel.startsWith('common/council_tasks/')) {
    const sc = scaleText(entry, conds.scale);
    const name = node.block.find((c) => c.key === 'name')?.value;
    Object.assign(stub, {
      kind: 'other', subkind: 'council_task', scope_owner: 'liege_councillor', block: name || bk,
      trigger: `any_councillor = { is_performing_council_task = ${key} }`,
      conditional: Boolean(sc), condition_text: sc ? `a councillor performs ${key}; value x scale { ${sc} } evaluated in the councillor's scope (needs scope:councillor_liege = the liege)` : `a councillor performs ${key}`,
    });
    if (sc) stub.scale = { script_value: sc, scope: 'councillor', requires: ['scope:councillor_liege = the liege'], meaning: 'overtime boost (monthly growth, variables on the liege or the councillor)' };
    return out;
  }

  if (rel.startsWith('common/confederation_types/')) {
    const lvl = parents.find((p) => p.key === 'cohesion_level');
    const thr = Number(lvl?.block?.find((c) => c.key === 'cohesion_threshold')?.value);
    const all = (top.block || []).filter((c) => c.key === 'cohesion_level').map((c) => Number(c.block.find((x) => x.key === 'cohesion_threshold')?.value)).sort((a, b) => a - b);
    const next = all.find((t) => t > thr);
    const range = `cohesion >= ${thr}${next !== undefined ? ` cohesion < ${next}` : ''}`;
    const member = bk;
    const who = {
      any_member_house_member: '',
      any_member_house_head: ' house.house_head ?= scope:ck3xbl_kl',
    }[member];
    Object.assign(stub, {
      kind: 'other', subkind: 'house_bloc', scope_owner: 'liege_house_bloc', block: `cohesion_level:${thr}:${member}`,
      trigger: `house ?= { house_confederation ?= { confederation_type = confederation_type:${key} has_cohesion = yes ${range} } }${who || ''}`,
      conditional: true, condition_text: `liege's house is in a ${key} bloc with cohesion ${thr}..${next ?? 'max'} (only the highest level applies), recipient class ${member}`,
      exclusive_group: `house_bloc:${key}`,
    });
    if (who === undefined) { stub.testable = 'approx'; stub.approximation = `recipient class ${member} not modelled; tested as any member`; }
    return out;
  }

  if (rel.startsWith('common/accolade_types/')) {
    const ranksIdx = parents.findIndex((p) => p.key === 'ranks');
    const rank = parents[ranksIdx + 1]?.key;
    const allRanks = (parents[ranksIdx]?.block || []).map((c) => Number(c.key)).filter(Number.isFinite).sort((a, b) => a - b);
    const isTop = Number(rank) === allRanks[allRanks.length - 1];
    const lvl = `accolade_attribute_level = { type = ${key} level ${isTop ? '>=' : '='} ${rank} }`;
    Object.assign(stub, { kind: 'other', subkind: `accolade_${bk}`, block: `rank:${rank}:${bk}`, conditional: true, exclusive_group: `accolade:${key}` });
    if (bk === 'liege_modifier') {
      Object.assign(stub, {
        scope_owner: 'liege_active_accolade',
        trigger: `any_active_accolade = { ${lvl} }`,
        count: `every_active_accolade = { limit = { ${lvl} } add = 1 }`,
        condition_text: `an ACTIVE accolade of the liege's acclaimed knights has attribute ${key} at rank ${rank}`,
        testable: 'inferred',
      });
      stub.notes.push('liege_modifier: "character modifier applied to the liege that has this accolade activated" (accolade type info); ranks assumed to replace each other (values grow per rank)');
    } else if (bk === 'knight_modifier') {
      Object.assign(stub, { scope_owner: 'knight', trigger: `accolade ?= { ${lvl} }`, condition_text: `the knight himself is the acclaimed knight of an accolade with ${key} rank ${rank}`, testable: 'inferred' });
      stub.notes.push('knight_modifier goes to the acclaimed knight personally - not a source of his liege\'s knight effectiveness');
    } else {
      Object.assign(stub, { scope_owner: 'army', testable: 'no', approximation: `${bk} is an army modifier` });
    }
    return out;
  }

  if (rel.startsWith('common/governments/')) {
    Object.assign(stub, { kind: 'other', subkind: 'government', trigger: addConds(`has_government = ${key}`) });
    return out;
  }

  if (rel.startsWith('common/laws/')) {
    const law = bk === 'modifier' ? parents[parents.length - 1]?.key || key : key;
    Object.assign(stub, { kind: 'law', key: law, trigger: addConds(`has_realm_law = ${law}`) });
    if (bk !== 'modifier') stub.notes.push(`law block '${bk}'`);
    return out;
  }

  // Anything else: listed so a patch that adds a new kind of source shows up.
  Object.assign(stub, {
    kind: 'other', subkind: `unclassified:${rel.split('/').slice(0, -1).join('/')}`, testable: 'no',
    approximation: 'new kind of source: classify it in gen_knight_effectiveness.mjs',
  });
  WARNINGS.push(`${rel}:${node.line}: unclassified knight grant (${parents.map((p) => p.key).join(' > ')} > ${bk})`);
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// Records
// ---------------------------------------------------------------------------------------------------------------

const KIND_ORDER = ['character_modifier', 'trait', 'perk', 'tradition', 'innovation', 'tenet', 'holy_site', 'building', 'law',
  'dynasty_perk', 'court_position', 'artifact_template', 'other'];

/** Line of the first relevant modifier key of a grant (knight keys, or prowess keys for the prowess table). */
function firstLine(gr, fieldName) {
  const raw = gr.fields.raw.filter((r) => (fieldName === 'prowess' ? PROWESS_KEYS.has(r.key) : !PROWESS_KEYS.has(r.key)));
  return (raw[0] || gr.fields.raw[0]).line;
}

function buildRecords(grants, usage, fieldName) {
  const records = [];
  for (const gr of grants) {
    const has = fieldName === 'prowess' ? gr.fields.prowess !== null : (gr.fields.mult !== null || gr.fields.perSkill.length || gr.fields.knightLimit !== null);
    if (!has) continue;
    for (const s of classify(gr, usage)) {
      const rec = {
        id: null,
        kind: s.kind,
        subkind: s.subkind,
        key: s.key,
        block: s.block,
        scope_owner: s.scope_owner,
        mult: fieldName === 'prowess' ? null : gr.fields.mult,
        per_skill: fieldName === 'prowess' ? [] : gr.fields.perSkill,
        knight_limit: fieldName === 'prowess' ? null : gr.fields.knightLimit,
        prowess: gr.fields.prowess,
        affects_knight_effectiveness: gr.fields.mult !== null || gr.fields.perSkill.length > 0,
        conditional: s.conditional,
        condition_text: s.condition_text,
        trigger: s.trigger,
        count: s.count,
        count_condition: s.count_condition ?? null,
        scale: s.scale,
        exclusive_group: s.exclusive_group,
        testable: s.testable,
        approximation: s.approximation,
        source_file: `${gr.entry.rel}:${firstLine(gr, fieldName)}`,
        block_line: gr.node.line,
        values: gr.fields.raw.filter((r) => r.expr).map((r) => ({ modifier: r.key, expr: r.expr, value: r.value, ...(r.exprSource ? { defined_at: r.exprSource } : {}) })),
        notes: s.notes,
      };
      if (s.artifact_pool) rec.artifact_pool = s.artifact_pool;
      if (s.position) rec.court_position = s.position;
      if (!rec.values.length) delete rec.values;
      if (!rec.notes.length) delete rec.notes;
      records.push(rec);
    }
  }
  // ids and stable order
  const seen = new Map();
  for (const r of records) {
    let id = r.block === r.key || r.block === 'character_modifier' ? `${r.kind}:${r.key}` : `${r.kind}:${r.key}:${r.block}`;
    const n = (seen.get(id) || 0) + 1;
    seen.set(id, n);
    if (n > 1) id += `#${n}`;
    r.id = id;
  }
  const lineOf = (r) => Number(r.source_file.split(':').pop());
  const fileOf = (r) => r.source_file.slice(0, r.source_file.lastIndexOf(':'));
  records.sort((a, b) => KIND_ORDER.indexOf(a.kind) - KIND_ORDER.indexOf(b.kind) || fileOf(a).localeCompare(fileOf(b)) || lineOf(a) - lineOf(b) || a.id.localeCompare(b.id));
  return records;
}

/**
 * The checker must reject known mistakes and accept known-good vanilla forms; otherwise "all triggers check out"
 * would mean nothing. Returns failures.
 */
function selfTestChecker() {
  const bad = [
    ['has_trait = no_such_trait_xyz', 'trigger'], // database key
    ['has_building = smiths_01', 'trigger'], // province trigger in character scope
    ['culture = { has_perk = chivalric_dominance_perk }', 'trigger'], // character trigger in culture scope
    ['faith = { has_cultural_tradition = tradition_only_the_strong }', 'trigger'], // culture trigger in faith scope
    ['any_directly_owned_province = { has_trait = brave }', 'trigger'], // character trigger in province scope
    ['holy_site:no_such_site_xyz = { is_holy_site_type = santiago }', 'trigger'], // data link key
    ['holy_site:santiago = { holy_site_county = scope:ck3xbl_kl }', 'trigger'], // landed_title compared with character
    ['holy_site:santiago = { holy_site_county = { has_court_type = court_warlike } }', 'trigger'], // character trigger in title scope
    ['holy_site:santiago = { holy_site_controller = { holder = scope:ck3xbl_kl } }', 'trigger'], // the controller is a character (in game)
    ['every_directly_owned_province = { limit = { has_building = smiths_01 } add = 1 }', 'trigger'], // value iterator in trigger
    ['frobnicate_knights = yes', 'trigger'], // unknown key
    ['aptitude = { court_position = no_such_position value = 2 }', 'trigger'], // parameter value
    ['dynasty ?= { has_dynasty_perk = warfare_legacy_1 } domicile = { has_trait = brave }', 'trigger'],
  ];
  const good = [
    ['culture ?= { has_cultural_tradition = tradition_only_the_strong }', 'trigger'],
    ['domicile ?= { has_domicile_building_or_higher = camp_fire_01 }', 'trigger'],
    ['any_court_position_holder = { type = master_of_horse_court_position aptitude = { court_position = master_of_horse_court_position value >= 3 } }', 'trigger'],
    ['every_directly_owned_province = { limit = { has_building = smiths_01 } add = 1 }', 'value'],
    ['faith = { is_faith_eminent_holy_site = holy_site:santiago controls_holy_site = santiago }', 'trigger'],
    ['holy_site:santiago = { holy_site_county = { holder ?= scope:ck3xbl_kl } }', 'trigger'],
  ];
  const failures = [];
  for (const [t, ctx] of bad) if (!checkScript(t, ctx).length) failures.push(`checker accepted a wrong trigger: ${t}`);
  for (const [t, ctx] of good) { const e = checkScript(t, ctx); if (e.length) failures.push(`checker rejected a vanilla form: ${t} (${e.join('; ')})`); }
  return failures;
}

function verify(records) {
  const problems = [...selfTestChecker()];
  for (const r of records) {
    for (const [field, ctx] of [['trigger', 'trigger'], ['count', 'value'], ['count_condition', 'trigger']]) {
      if (!r[field]) continue;
      for (const e of checkScript(r[field], ctx)) problems.push(`${r.id} ${field}: ${e}`);
    }
    if (r.scale?.liege_scope_expr && r.scale.liege_scope_expr.startsWith('holy_site:')) {
      for (const e of checkScript(`value = ${r.scale.liege_scope_expr}`, 'value')) problems.push(`${r.id} scale: ${e}`);
    }
    if (r.scale?.script_value && r.subkind !== 'holy_site_faith_scaled') {
      const scope = r.scale.scope === 'councillor' ? 'character' : 'character';
      for (const e of checkScript(r.scale.script_value, 'value', scope)) problems.push(`${r.id} scale: ${e}`);
    }
    if ((r.testable === 'exact' || r.testable === 'inferred') && !r.trigger) problems.push(`${r.id}: testable without a trigger`);
    if (r.affects_knight_effectiveness && r.mult === null && !r.per_skill.length) problems.push(`${r.id}: no value`);
    for (const p of r.per_skill) if (p.value === null) problems.push(`${r.id}: unresolved per-skill value`);
  }
  return problems;
}

// ---------------------------------------------------------------------------------------------------------------
// Reforge blueprints: knight modifiers that are REMOVED when an artifact is reforged
// ---------------------------------------------------------------------------------------------------------------

function reforgeRemovals() {
  const out = [];
  for (const f of scriptFiles('common/artifacts/blueprints')) {
    const e = load(f);
    for (const n of e.tree) {
      const dis = n.block?.find((c) => c.key === 'disallowed_modifiers');
      const hits = (dis?.block || []).filter((c) => c.key === null && (IS_KE(c.value) || c.value === 'knight_limit')).map((c) => c.value);
      if (hits.length) {
        const get = (k) => n.block.find((c) => c.key === k)?.value;
        out.push({ blueprint: n.key, in_type: get('in_type'), out_type: get('out_type'), removes: hits, source_file: `${e.rel}:${n.line}` });
      }
    }
  }
  return out;
}

// ---------------------------------------------------------------------------------------------------------------
// Summary
// ---------------------------------------------------------------------------------------------------------------

function summarize(records) {
  const ke = records.filter((r) => r.affects_knight_effectiveness);
  const count = (arr, f) => arr.reduce((m, r) => { const k = f(r); m[k] = (m[k] || 0) + 1; return m; }, {});
  const sortObj = (o) => Object.fromEntries(Object.entries(o).sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0])));
  const files = new Set(ke.map((r) => r.source_file.slice(0, r.source_file.lastIndexOf(':'))));
  return {
    records: records.length,
    knight_effectiveness_records: ke.length,
    knight_effectiveness_files: files.size,
    knight_effectiveness_mult_records: ke.filter((r) => r.mult !== null).length,
    knight_effectiveness_per_skill_records: ke.filter((r) => r.per_skill.length).length,
    per_skill_by_skill: sortObj(ke.reduce((m, r) => { for (const p of r.per_skill) m[p.skill] = (m[p.skill] || 0) + 1; return m; }, {})),
    knight_limit_records: records.filter((r) => r.knight_limit !== null).length,
    knight_effectiveness_by_kind: sortObj(count(ke, (r) => r.kind)),
    knight_effectiveness_by_subkind: sortObj(count(ke, (r) => r.subkind || r.kind)),
    knight_effectiveness_by_testable: sortObj(count(ke, (r) => r.testable)),
    knight_effectiveness_conditional: ke.filter((r) => r.conditional).length,
    knight_effectiveness_not_testable: ke.filter((r) => r.testable === 'no' || r.testable === 'approx').map((r) => r.id),
    knight_effectiveness_scaled: ke.filter((r) => r.scale).map((r) => r.id),
    knight_effectiveness_stacking: ke.filter((r) => r.count).length,
    knight_effectiveness_on_knight_himself: ke.filter((r) => r.scope_owner === 'knight').map((r) => r.id),
  };
}

// ---------------------------------------------------------------------------------------------------------------
// Optional DRAFT CK3 scripted effect (liege scope, permille; the effect body is rendered by knight_eff_emit.mjs)
// ---------------------------------------------------------------------------------------------------------------

function emitCk3(records, hash) {
  const L = [];
  L.push('# DRAFT generated by tools/gen/knights/gen_knight_effectiveness.mjs --emit-ck3 - not part of the mod build.');
  L.push(`# Source table: bridge/data/ck3_knight_effectiveness_sources.json (content hash ${hash}).`);
  L.push('# this = the liege (army owner of the knights). Saves value ck3xbl_a_ke = knight effectiveness in permille');
  L.push('# (1000 = 100 %, CK3 base) and the scopes the source triggers need (ck3xbl_kl, holder, councillor_liege = the liege).');
  L.push('# Not counted: sources with testable = no (court position tasks, engine modifiers), dynamic holy-site relic scaling.');
  L.push(...knightEffEffectLines(records));
  return `﻿${L.join('\n')}\n`;
}

// ---------------------------------------------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------------------------------------------

function gameVersion() {
  try {
    return JSON.parse(readText(path.join(path.dirname(GAME), 'launcher', 'launcher-settings.json'))).rawVersion;
  } catch { return null; }
}

function writeIfChanged(file, content, label) {
  const old = fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : null;
  if (old === content) { log(`${label}: unchanged ${file}`); return false; }
  if (OPT.check) { console.error(`${label}: would change ${file}`); return true; }
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const tmp = `${file}.tmp`;
  fs.writeFileSync(tmp, content, 'utf8');
  fs.renameSync(tmp, file);
  log(`${label}: wrote ${file}`);
  return true;
}

function main() {
  const grants = collectGrants(Boolean(OPT.prowessOut));
  const staticNames = new Set(grants.filter((gr) => gr.entry.rel.startsWith('common/modifiers/')).map((gr) => (gr.parents[0] || gr.node).key));
  const usage = staticModifierUsage(staticNames);
  const records = buildRecords(grants, usage, 'knight');
  const problems = verify(records);
  for (const e of fileCache.values()) {
    if (e.errors.length && /knight_effectiveness|knight_limit/.test(e.text) && !NOT_GRANTS.some((p) => e.rel.startsWith(p))) {
      WARNINGS.push(`${e.rel}: parse problems ${JSON.stringify(e.errors.slice(0, 2))}`);
    }
  }

  const doc = {
    meta: {
      description: 'Every source of CK3 knight effectiveness (knight_effectiveness_mult, knight_effectiveness_per_<skill>) and knight_limit in game/common, with the CK3 trigger that tests it from the knights\' liege.',
      generator: 'tools/gen/knights/gen_knight_effectiveness.mjs',
      game: 'Crusader Kings III',
      game_version: gameVersion(),
      receiver: 'liege: the ruler whose knights they are = the owner of the army the knight fights in (army_owner of knight_army). Knight effectiveness modifiers on the knight himself do nothing for him (game concept knight_effectiveness, accolade liege_modifier vs knight_modifier).',
      formula: 'knight_effectiveness = 1 + sum(mult x instances x scale) + sum(per_skill.value x liege skill x instances). CK3: at 100% knights fight normally, at 200% as if their prowess were doubled (game_concept_knight_effectiveness_desc).',
      trigger_context: 'Every trigger/count is written for the liege as the current scope (this). Some use saved scopes the caller sets first: scope:ck3xbl_kl = the liege (artifacts, holy sites), scope:holder = the liege (building is_enabled), scope:councillor_liege = the liege (council task scale, evaluated in the councillor scope).',
      fields: {
        kind: KIND_ORDER.join('|'),
        subkind: 'finer class (static_character_modifier, artifact_modifier, holding_building, domicile_building, court_position_employer, holy_site_faith_scaled, council_task, accolade_liege_modifier, ...)',
        scope_owner: 'who must hold the source: liege | liege_culture | liege_rite | liege_faith | liege_dynasty | liege_held_holding | liege_domicile | liege_court | liege_court_position_employee | liege_as_court_position_holder | liege_councillor | liege_active_accolade | liege_house_bloc | liege_equipped_artifact | liege_holds_holy_site_county | knight | army | courtiers',
        mult: 'knight_effectiveness_mult (0.1 = +10 %), null if the block has none',
        per_skill: '[{skill, value}]: knight_effectiveness_per_<skill>, value per point of the liege\'s skill (dread, tyranny, martial, stewardship, diplomacy, intrigue, learning, prowess)',
        knight_limit: 'knight_limit of the same block (informational: CK3 has max_number_of_knights)',
        conditional: 'true when the grant depends on a scripted condition beyond holding the source (culture parameter, government flag, aptitude, grandeur, cohesion, scale, building is_enabled, holy site control/eminence)',
        trigger: 'CK3 trigger in liege scope; null = not testable',
        count: 'script-value body (liege scope) giving the number of instances for stacking sources (several holdings, artifacts, employees, accolades)',
        count_condition: 'extra trigger on the liege for a counted source',
        scale: 'scaled grants: value x the scale script value',
        exclusive_group: 'records of one group are mutually exclusive by construction (levels)',
        testable: 'exact | inferred (trigger follows the docs but an engine detail is unverified) | approx | no',
      },
    },
    summary: summarize(records),
    sources: records,
    reforge_removes: reforgeRemovals(),
    warnings: WARNINGS,
  };
  const body = JSON.stringify(doc, null, 1);
  const hash = crypto.createHash('sha256').update(body).digest('hex').slice(0, 16);
  doc.meta.content_hash = hash;
  const json = `${JSON.stringify(doc, null, 1)}\n`;

  log(`knight grants: ${records.length} records (${doc.summary.knight_effectiveness_records} knight effectiveness in ${doc.summary.knight_effectiveness_files} files, ${doc.summary.knight_limit_records} knight_limit)`);
  log(`by kind: ${JSON.stringify(doc.summary.knight_effectiveness_by_kind)}`);
  log(`by testable: ${JSON.stringify(doc.summary.knight_effectiveness_by_testable)}`);
  for (const w of WARNINGS) log(`warning: ${w}`);
  if (problems.length) {
    for (const p of problems) console.error(`TRIGGER CHECK: ${p}`);
    console.error(`${problems.length} trigger problem(s) - nothing written`);
    process.exit(1);
  }
  log('trigger check: every trigger, count and scale checked against the script_docs dumps (keys, scopes, links, database keys)');

  let changed = writeIfChanged(OPT.out, json, 'sources');
  if (OPT.prowessOut) {
    const prowessGrants = grants.filter((gr) => gr.fields.prowess !== null);
    const pRecords = buildRecords(prowessGrants, staticModifierUsage(new Set(prowessGrants.filter((gr) => gr.entry.rel.startsWith('common/modifiers/')).map((gr) => (gr.parents[0] || gr.node).key))), 'prowess');
    const pdoc = {
      meta: {
        description: 'Every grant of prowess / prowess_no_portrait in game/common (informational: CK3\'s prowess value, logged as ck3xbl_c_prowess, is already final).',
        generator: 'tools/gen/knights/gen_knight_effectiveness.mjs --prowess', game_version: gameVersion(),
        trigger_context: 'triggers are written for the holder of the prowess (the knight himself) as current scope; read scope:ck3xbl_kl / scope:holder as that holder',
      },
      summary: { records: pRecords.length, by_kind: summarize(pRecords).knight_effectiveness_by_kind },
      sources: pRecords,
    };
    pdoc.summary.by_kind = pRecords.reduce((m, r) => { m[r.kind] = (m[r.kind] || 0) + 1; return m; }, {});
    changed = writeIfChanged(OPT.prowessOut, `${JSON.stringify(pdoc, null, 1)}\n`, 'prowess') || changed;
  }
  if (OPT.emitCk3) changed = writeIfChanged(path.resolve(OPT.emitCk3), emitCk3(records, hash), 'ck3 draft') || changed;
  if (OPT.check && changed) process.exit(1);
}

main();
