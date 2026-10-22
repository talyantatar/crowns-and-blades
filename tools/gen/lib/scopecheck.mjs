// scopecheck.mjs - scope and context checks for CK3 script, used by lint_ck3_mod.mjs.
//
// The name checks of the lint only prove that a key exists somewhere in the script docs. This pass follows the
// script the way the engine runs it and checks, for every key:
//  - context: effects only in effect blocks, triggers only in trigger blocks (limit, trigger, AND/OR/NOT, ...),
//    script-value operations only in value blocks, scripted effects/triggers likewise;
//  - scope: the current scope type is among the key's "Supported Scopes" (effects.log / triggers.log) or the link's
//    "Input Scopes" (event_targets.log); links and iterators change the scope type ("Output Scopes",
//    "Supported Targets");
//  - targets: "<type> target" trigger arguments, portrait characters and the scope parameters of known block effects
//    have the right type; `scope:`, `var:` and `global_var:` names are set somewhere.
// Types of saved scopes, variables and global lists are inferred from every save_scope_as / set_variable /
// add_to_global_variable_list in the mod (flow-insensitive, iterated to a fixed point). Scripted effects and
// triggers are checked with the scope and the parameters of every call site; unreached ones once without a scope.
// Unknown types never produce errors, so the pass only reports what it can prove.

import { readText } from './pdx.mjs';

// ---------------------------------------------------------------------------------------------------------------
// Script docs
// ---------------------------------------------------------------------------------------------------------------

const list = (text) => new Set(text.split(',').map((s) => s.trim()).filter(Boolean));

/** Parses one of the `script_docs` logs into entries {name, scopes, targets, input, output, data, traits}. */
function parseDocLog(file) {
  const entries = [];
  for (const chunk of readText(file).split(/^-{10,}\s*$/m)) {
    const lines = chunk.split(/\r?\n/).filter((l) => l.trim());
    const head = lines.length ? /^([A-Za-z_][A-Za-z0-9_]*) - /.exec(lines[0]) : null;
    if (!head) continue;
    const e = { name: head[1], scopes: null, targets: null, input: null, output: null, data: false, traits: null };
    for (const l of lines) {
      let m;
      if ((m = /^Supported Scopes: (.*)$/.exec(l))) e.scopes = list(m[1]);
      else if ((m = /^Supported Targets: (.*)$/.exec(l))) e.targets = [...list(m[1])][0] || null;
      else if ((m = /^Input Scopes: (.*)$/.exec(l))) e.input = list(m[1]);
      else if ((m = /^Output Scopes: (.*)$/.exec(l))) e.output = [...list(m[1])][0] || null;
      else if (/^Requires Data: yes/.test(l)) e.data = true;
      else if ((m = /^Traits: (.*)$/.exec(l))) e.traits = m[1].trim();
    }
    entries.push(e);
  }
  return entries;
}

// Where the engine differs from the script docs (seen in game): link -> its real output scope. holy_site_controller: the docs
// say landed_title, CK3 1.20.0.3 returns the county holder (error.log 2026-10-04: "Event target link 'holder' did not get a
// matching scope type. Expected 'landed_title', but got 'character'" after holy_site_controller).
const LINK_OUTPUT_FIXES = { holy_site_controller: 'character' };

export function loadScriptDocs(docsDir) {
  const byName = (entries) => new Map(entries.map((e) => [e.name, e]));
  const targets = parseDocLog(`${docsDir}/event_targets.log`);
  for (const e of targets) if (LINK_OUTPUT_FIXES[e.name]) e.output = LINK_OUTPUT_FIXES[e.name];
  return {
    effects: byName(parseDocLog(`${docsDir}/effects.log`)),
    triggers: byName(parseDocLog(`${docsDir}/triggers.log`)),
    links: byName(targets.filter((e) => !e.data)),
    dataLinks: byName(targets.filter((e) => e.data)),
  };
}

// ---------------------------------------------------------------------------------------------------------------
// Tables of the engine's block syntax (only what the mod uses; anything else is reported so the table grows with it)
// ---------------------------------------------------------------------------------------------------------------

// Parameter kinds: name (an identifier), value (number, value reference or script-value block), bool,
// scope / scope:<type> (an event target), any (a scope or a value), effects (the remaining keys are effects).
const VARIABLE_SET = { name: 'name', value: 'any', days: 'value', months: 'value', years: 'value' };
const VARIABLE_CHANGE = { name: 'name', add: 'value', subtract: 'value', multiply: 'value', divide: 'value', min: 'value', max: 'value' };
const VARIABLE_LIST = { name: 'name', target: 'scope' };
const MESSAGE = {
  type: 'name', title: 'name', desc: 'name', tooltip: 'name', left_icon: 'scope', right_icon: 'scope', goto: 'scope', '*': 'effects',
};

const BLOCK_EFFECTS = {
  save_scope_value_as: { name: 'name', value: 'any' },
  save_temporary_scope_value_as: { name: 'name', value: 'any' },
  set_variable: VARIABLE_SET,
  set_global_variable: VARIABLE_SET,
  set_local_variable: VARIABLE_SET,
  change_variable: VARIABLE_CHANGE,
  change_global_variable: VARIABLE_CHANGE,
  change_local_variable: VARIABLE_CHANGE,
  add_to_variable_list: VARIABLE_LIST,
  add_to_global_variable_list: VARIABLE_LIST,
  add_to_local_variable_list: VARIABLE_LIST,
  remove_list_variable: VARIABLE_LIST,
  remove_list_global_variable: VARIABLE_LIST,
  remove_list_local_variable: VARIABLE_LIST,
  send_interface_toast: MESSAGE,
  send_interface_message: MESSAGE,
  battle_event: { key: 'name', left_portrait: 'scope:character', right_portrait: 'scope:character', type: 'name', target_right: 'bool' },
  imprison: { target: 'scope:character', type: 'name', reason: 'name' },
  death: { death_reason: 'name', killer: 'scope:character' },
  trigger_event: { id: 'name', on_action: 'name', saved_event_id: 'any', days: 'value', months: 'value', years: 'value' },
  add_character_modifier: { modifier: 'name', days: 'value', weeks: 'value', months: 'value', years: 'value' },
  add_province_modifier: { modifier: 'name', days: 'value', weeks: 'value', months: 'value', years: 'value' },
  add_county_modifier: { modifier: 'name', days: 'value', weeks: 'value', months: 'value', years: 'value' },
  random: { chance: 'value', '*': 'effects' },
  // Event options (vanilla events everywhere): a stress change per trait, 	rait = <script value>.
  stress_impact: { base: 'value', '*': 'value' },
  // Duel rewards (PROTOCOL 18.4); effects.log set_owner, add_trait_xp.
  set_owner: { target: 'scope:character', history: 'name' },
  add_trait_xp: { trait: 'name', track: 'name', value: 'value' },
  // Battle gear (PROTOCOL 16); vanilla common/scripted_effects/00_ep1_artifact_creation_effects.txt.
  create_artifact: {
    name: 'name', description: 'name', type: 'name', visuals: 'name', modifier: 'name', quality: 'value', wealth: 'value',
    save_scope_as: 'name', creator: 'scope:character', template: 'name',
  },
};
// Effects whose block is a script value (`effect = { value = ... multiply = ... }`); add_glory: vanilla
// common/scripted_effects/00_accolades_scripted_effects.txt:371.
const VALUE_BLOCK_EFFECTS = new Set([
  'deplete_army_by_percentage', 'change_maa_troops_count', 'add_prestige', 'add_piety', 'add_gold', 'add_dread', 'add_glory',
]);

// The comparison operator of a `value` parameter (`value >= 2`) is accepted like any value parameter.
const BLOCK_TRIGGERS = {
  is_target_in_variable_list: VARIABLE_LIST,
  is_target_in_global_variable_list: VARIABLE_LIST,
  is_target_in_local_variable_list: VARIABLE_LIST,
  // triggers.log has_trait_xp: track only for traits with several tracks.
  has_trait_xp: { trait: 'name', track: 'name', value: 'value' },
  // triggers.log accolade_attribute_level; vanilla common/men_at_arms_types/01_accolade_maa_types.txt:28.
  accolade_attribute_level: { type: 'name', level: 'value' },
  // triggers.log aptitude; vanilla common/activities/pulse_actions/hunt_pulse_actions.txt:1004.
  aptitude: { court_position: 'name', value: 'value' },
  // triggers.log faith_hostility_level (faith scope towards the target faith).
  faith_hostility_level: { target: 'scope:faith', value: 'value' },
};

/** True for a block effect whose every key is a parameter with a script value (stress_impact: one per trait). */
export function blockAcceptsAnyValue(key) {
  return (BLOCK_EFFECTS[key] || BLOCK_TRIGGERS[key])?.['*'] === 'value';
}

/** Parameter names of a block effect or trigger the pass has a table for (the lint's name check accepts them there), or null. */
export function blockParameterNames(key) {
  const spec = BLOCK_EFFECTS[key] || BLOCK_TRIGGERS[key];
  return spec ? new Set(Object.keys(spec).filter((k) => k !== '*')) : null;
}

const VALUE_OPS = new Set(['value', 'add', 'subtract', 'multiply', 'divide', 'modulo', 'min', 'max']);
const VALUE_FLAGS = new Set(['round', 'floor', 'ceiling', 'abs']);
const LOGIC = new Set(['AND', 'OR', 'NOT', 'NOR', 'NAND']);
const ITERATOR_SPECIAL = {
  limit: 'triggers', alternative_limit: 'triggers', order_by: 'value', position: 'value', max: 'value', min: 'value',
  check_range_bounds: 'bool', count: 'skip', percent: 'value', weight: 'skip', variable: 'name', list: 'name',
  type: 'name', include_hired: 'bool',
  phase: 'name',   // guest subset iterators of an activity (any_/every_/ordered_guest_subset = { name = .. phase = .. })
};
// Prefixes of references to data: `culture:norse` is a culture scope.
const DATA_PREFIX_TYPES = {
  culture: 'culture', faith: 'faith', religion: 'religion', title: 'landed_title', character: 'character', province: 'province',
  dynasty: 'dynasty', house: 'dynasty_house', flag: 'flag',
};
const NOT_SCOPES = new Set(['value', 'boolean', 'flag']);

const isNumber = (s) => /^-?\d+(\.\d+)?$/.test(s);
const isRefKey = (k) => k.includes(':') || k.includes('.') || k === 'this' || k === 'root' || k === 'prev';
const isListIterator = (k) => /^(every|random|ordered|any)_in_(global_|local_)?list$/.test(k);

/** Deep copy of a parse tree with `$PARAM$` replaced in keys and values. */
function substitute(nodes, params) {
  const sub = (s) => (typeof s === 'string' ? s.replace(/\$([A-Za-z0-9_]+)\$/g, (m, p) => (p in params ? params[p] : m)) : s);
  return nodes.map((n) => ({ ...n, key: sub(n.key), value: sub(n.value), block: n.block ? substitute(n.block, params) : undefined }));
}

// ---------------------------------------------------------------------------------------------------------------
// Checker
// ---------------------------------------------------------------------------------------------------------------

export class ScopeChecker {
  /**
   * @param docs        loadScriptDocs() result
   * @param defs        { effects, triggers }: Map name -> { node, file } of the mod's scripted effects / triggers
   * @param vanilla     { effects, triggers, scriptValues }: Sets of vanilla names (bodies are not checked)
   * @param onError     (file, line, message) => void
   */
  constructor({ docs, defs, vanilla, onError }) {
    this.docs = docs;
    this.defs = defs;
    this.vanilla = vanilla;
    this.onError = onError;
    this.entries = [];
    this.seeds = new Map();
  }

  /** Engine-provided saved scopes (`scope:army` in on_actions), name -> type. */
  seedSaved(name, type) {
    this.seeds.set(name, type);
  }

  /** A top-level script body: ctx 'effect' | 'trigger', root = scope type of root (or null). */
  addEntry(nodes, ctx, root, file) {
    this.entries.push({ nodes, ctx, root, file });
  }

  /** A single reference outside script bodies (e.g. a portrait's `character = scope:x`) that must be of type `expected`. */
  addTarget(expr, expected, root, file, line) {
    this.entries.push({ target: { expr, expected, line }, root, file });
  }

  run() {
    this.types = { saved: new Map(), vars: new Map(), globals: new Map(), lists: new Map() };
    for (const [name, type] of this.seeds) this.record('saved', name, type);
    // Iterate until the inferred types are stable, then report in one final pass.
    let size = -1;
    for (let pass = 0; pass < 10; pass++) {
      this.walkAll(false);
      const now = Object.values(this.types).reduce((n, m) => n + [...m.values()].reduce((a, s) => a + s.size, 0), 0);
      if (now === size) break;
      size = now;
    }
    this.reported = new Set();
    this.walkAll(true);
  }

  walkAll(final) {
    this.final = final;
    this.visited = new Set();
    this.reached = new Set();
    for (const e of this.entries) {
      const env = { root: e.root, file: e.file };
      if (e.target) this.target(e.target.expr, e.target.expected, e.root, env, e.target.line);
      else this.walk(e.nodes, e.ctx, e.root, env);
    }
    for (const [kind, ctx] of [['effects', 'effect'], ['triggers', 'trigger']]) {
      for (const [name, def] of this.defs[kind]) {
        if (!this.reached.has(name)) this.walk(def.node.block || [], ctx, null, { root: null, file: def.file });
      }
    }
  }

  error(env, line, message) {
    if (!this.final) return;
    const key = `${env.file}:${line}:${message}`;
    if (this.reported.has(key)) return;
    this.reported.add(key);
    this.onError(env.file, line, message);
  }

  // --- type inference --------------------------------------------------------------------------------------

  record(map, name, type) {
    if (!name || name.includes('$') || !type) return;
    const m = this.types[map];
    if (!m.has(name)) m.set(name, new Set());
    m.get(name).add(type);
  }

  /** Inferred type of a saved scope / variable / list, or null. Reports names that are never set. */
  typeOf(map, name, label, env, line) {
    const set = this.types[map].get(name);
    if (!set) {
      this.error(env, line, `${label}:${name} is never set in the mod`);
      return null;
    }
    return set.size === 1 ? [...set][0] : null;
  }

  /**
   * Type of a reference like `scope:x`, `var:x`, `root.var:y`, `location`, `culture:norse`, `army_size`, a number or
   * yes/no, evaluated in scope type `cur`. Returns {known, type}: known = false for names it cannot resolve.
   */
  resolve(expr, cur, env, line) {
    if (typeof expr !== 'string') return { known: false, type: null };
    if (isNumber(expr)) return { known: true, type: 'value' };
    if (expr === 'yes' || expr === 'no') return { known: true, type: 'boolean' };
    if (expr.includes('$')) return { known: true, type: null };
    const segs = expr.split('.');
    let t = cur;
    for (let i = 0; i < segs.length; i++) {
      const seg = segs[i];
      const m = /^([a-z_]+):(.+)$/.exec(seg);
      if (m) {
        const [, prefix, name] = m;
        if (prefix === 'scope') t = this.typeOf('saved', name, 'scope', env, line);
        else if (prefix === 'var') t = this.typeOf('vars', name, 'var', env, line);
        else if (prefix === 'global_var') t = this.typeOf('globals', name, 'global_var', env, line);
        else if (prefix === 'local_var') t = null;
        else if (DATA_PREFIX_TYPES[prefix]) t = DATA_PREFIX_TYPES[prefix];
        else if (this.docs.dataLinks.has(prefix)) t = this.docs.dataLinks.get(prefix).output;
        else return { known: false, type: null };
        continue;
      }
      if (seg === 'this') continue;
      if (seg === 'root') { t = env.root; continue; }
      if (seg === 'prev' || seg === 'from') { t = null; continue; }
      const link = this.docs.links.get(seg);
      if (link) {
        if (link.input && t && !link.input.has(t)) this.error(env, line, `link '${seg}' needs ${[...link.input].join('/')} scope, not ${t}`);
        t = link.output;
        continue;
      }
      const trigger = this.docs.triggers.get(seg);
      if (trigger || this.vanilla.scriptValues.has(seg)) {
        if (trigger) this.checkSupported(trigger, seg, t, env, line);
        if (i !== segs.length - 1) this.error(env, line, `'${seg}' is a value and cannot be followed by '.${segs[i + 1]}'`);
        return { known: true, type: 'value' };
      }
      return { known: false, type: null };
    }
    return { known: true, type: t };
  }

  checkSupported(doc, name, cur, env, line) {
    if (!doc.scopes || doc.scopes.has('none') || !cur) return;
    if (!doc.scopes.has(cur)) this.error(env, line, `'${name}' is not valid in ${cur} scope (supported: ${[...doc.scopes].join(', ')})`);
  }

  /** A key that switches scope: the new type must be a scope, not a value. */
  scopeOf(expr, cur, env, line) {
    const r = this.resolve(expr, cur, env, line);
    if (r.type && NOT_SCOPES.has(r.type)) this.error(env, line, `'${expr}' is a ${r.type}, not a scope`);
    return r.type && !NOT_SCOPES.has(r.type) ? r.type : null;
  }

  /** A target argument: a reference whose type must be `expected` (when both are known). Returns its type. */
  target(expr, expected, cur, env, line) {
    const r = this.resolve(expr, cur, env, line);
    if (expected && r.type && r.type !== expected) this.error(env, line, `'${expr}' is ${r.type}, expected ${expected}`);
    return r.type;
  }

  /** A script-value operand (number, value reference, value trigger) or an inline script-value block. */
  operand(node, cur, env) {
    if (node.block) {
      this.walk(node.block, 'value', cur, env);
      return 'value';
    }
    const v = node.value;
    if (node.valueType === 'string') {
      this.error(env, node.line, `'${node.key}' expects a value, not the string "${v}"`);
      return null;
    }
    const r = this.resolve(v, cur, env, node.line);
    if (!r.known) this.error(env, node.line, `'${v}' is not a number, value trigger, script value or value reference`);
    else if (r.type && r.type !== 'value' && r.type !== 'boolean') this.error(env, node.line, `'${v}' is a ${r.type} scope, not a value`);
    return r.type;
  }

  /** Type of a `value =` argument that may be a scope or a value (set_variable, save_scope_value_as). */
  anyValue(node, cur, env) {
    if (node.block) {
      this.walk(node.block, 'value', cur, env);
      return 'value';
    }
    if (node.value && node.value.startsWith('flag:')) return 'flag';
    return this.resolve(node.value, cur, env, node.line).type;
  }

  // --- walking ---------------------------------------------------------------------------------------------

  walk(nodes, ctx, cur, env) {
    for (const n of nodes) {
      if (n.key === null) continue;
      if (ctx === 'effect') this.effect(n, cur, env);
      else if (ctx === 'trigger') this.trigger(n, cur, env);
      else this.value(n, cur, env);
    }
  }

  /** Calls a scripted effect/trigger of the mod: its body is checked in the caller's scope, with the call's parameters. */
  call(name, kind, node, cur, env) {
    const def = this.defs[kind].get(name);
    const params = {};
    for (const c of node.block || []) if (c.key && c.value !== undefined) params[c.key] = c.value;
    const key = `${kind}|${name}|${cur}|${env.root}|${JSON.stringify(params)}`;
    this.reached.add(name);
    if (this.visited.has(key)) return;
    this.visited.add(key);
    const body = node.block ? substitute(def.node.block || [], params) : def.node.block || [];
    this.walk(body, kind === 'effects' ? 'effect' : 'trigger', cur, { root: env.root, file: def.file });
  }

  /** Key/value parameters of a block effect or trigger, by a parameter table. */
  params(node, spec, cur, env) {
    const values = {};
    for (const c of node.block) {
      if (!c.key) continue;
      const kind = spec[c.key] || (spec['*'] === 'effects' || spec['*'] === 'value' ? spec['*'] : null);
      if (!kind) {
        this.error(env, c.line, `unexpected '${c.key}' in ${node.key}`);
        continue;
      }
      if (kind === 'effects') this.effect(c, cur, env);
      else if (kind === 'value') this.operand(c, cur, env);
      else if (kind === 'any') values[c.key] = this.anyValue(c, cur, env);
      else if (kind.startsWith('scope')) values[c.key] = this.target(c.value, kind.split(':')[1] || null, cur, env, c.line);
      else values[c.key] = c.value;
    }
    return values;
  }

  /** every_/random_/ordered_/any_ iterators: special keys, then the body in the target scope. */
  iterator(node, ctx, doc, cur, env) {
    if (!node.block) {
      this.error(env, node.line, `iterator '${node.key}' needs a block`);
      return;
    }
    let target = doc.targets || null;
    if (isListIterator(node.key)) {
      const name = (node.block.find((c) => c.key === 'variable' || c.key === 'list') || {}).value;
      const global = node.key.includes('_global_');
      target = global && name ? this.typeOf('lists', name, 'global list', env, node.line) : null;
    }
    for (const c of node.block) {
      if (!c.key) continue;
      const special = ITERATOR_SPECIAL[c.key];
      if (special === 'triggers') this.walk(c.block || [], 'trigger', target, env);
      else if (special === 'value') this.operand(c, target, env);
      else if (special) continue;
      else if (ctx === 'effect') this.effect(c, target, env);
      else if (ctx === 'trigger') this.trigger(c, target, env);
      else this.value(c, target, env);
    }
  }

  isIterator(name, doc) {
    return /^(every|random|ordered|any)_/.test(name) && (Boolean(doc.targets) || isListIterator(name));
  }

  effect(n, cur, env) {
    const k = n.key;
    if (k === 'if' || k === 'else_if' || k === 'else') {
      for (const c of n.block || []) {
        if (c.key === 'limit') {
          if (k === 'else') this.error(env, c.line, 'else has no limit');
          this.walk(c.block || [], 'trigger', cur, env);
        } else if (c.key) this.effect(c, cur, env);
      }
      return;
    }
    if (k === 'hidden_effect' || k === 'show_as_tooltip') return this.walk(n.block || [], 'effect', cur, env);
    if (k === 'custom_tooltip' || k === 'custom_description') return this.tooltip(n, 'effect', cur, env);
    if (k === 'random_list') {
      // Weight blocks (battle gear rolls, PROTOCOL 16): each is an effect body with an optional trigger / modifier.
      for (const c of n.block || []) {
        for (const e of c.block || []) {
          if (e.key === 'trigger') this.walk(e.block || [], 'trigger', cur, env);
          else if (e.key === 'modifier') continue;
          else if (e.key) this.effect(e, cur, env);
        }
      }
      return;
    }
    if (k === 'switch') return;
    if (k === 'limit') return this.error(env, n.line, 'limit is only valid in if/else_if and iterators');
    if (VALUE_OPS.has(k)) return this.error(env, n.line, `'${k}' is a script-value operation, not an effect`);
    if (isRefKey(k)) {
      if (!n.block) return this.error(env, n.line, `comparison '${k} ${n.op} ${n.value}' in an effect block`);
      return this.walk(n.block, 'effect', this.scopeOf(k, cur, env, n.line), env);
    }
    if (this.defs.effects.has(k)) return this.call(k, 'effects', n, cur, env);
    if (this.vanilla.effects.has(k)) return;
    if (this.defs.triggers.has(k) || this.vanilla.triggers.has(k)) return this.error(env, n.line, `scripted trigger '${k}' used in an effect block`);
    const link = this.docs.links.get(k);
    const doc = this.docs.effects.get(k);
    const blockEffect = doc && (this.isIterator(k, doc) || BLOCK_EFFECTS[k] || VALUE_BLOCK_EFFECTS.has(k));
    if (link && (n.block ? !blockEffect : !doc)) {
      if (!n.block) return this.error(env, n.line, `link '${k}' needs a block in an effect`);
      return this.walk(n.block, 'effect', this.scopeOf(k, cur, env, n.line), env);
    }
    if (!doc) {
      if (this.docs.triggers.has(k)) this.error(env, n.line, `trigger '${k}' used in an effect block`);
      return; // unknown names are reported by the name check
    }
    this.checkSupported(doc, k, cur, env, n.line);
    if (this.isIterator(k, doc)) return this.iterator(n, 'effect', doc, cur, env);
    if (k === 'save_scope_as' || k === 'save_temporary_scope_as') return this.record('saved', n.value, cur);
    if (VALUE_BLOCK_EFFECTS.has(k)) return void this.operand(n, cur, env);
    if (n.block) {
      const spec = BLOCK_EFFECTS[k];
      if (!spec) return this.error(env, n.line, `the lint has no parameter table for effect '${k}' (add it to BLOCK_EFFECTS)`);
      const p = this.params(n, spec, cur, env);
      const name = (n.block.find((c) => c.key === 'name') || {}).value;
      if (k === 'create_artifact') this.record('saved', (n.block.find((c) => c.key === 'save_scope_as') || {}).value, 'artifact');
      else if (k === 'save_scope_value_as' || k === 'save_temporary_scope_value_as') this.record('saved', name, p.value);
      else if (k === 'set_variable') this.record('vars', name, p.value);
      else if (k === 'set_global_variable') this.record('globals', name, p.value);
      else if (k === 'add_to_global_variable_list') this.record('lists', name, p.target);
      return;
    }
    // `effect = value`: validate references (e.g. a mistyped scope: name).
    if (n.valueType === 'word' && /^(scope|var|global_var):|^(root|this)\./.test(n.value)) this.resolve(n.value, cur, env, n.line);
  }

  trigger(n, cur, env) {
    const k = n.key;
    if (LOGIC.has(k)) return this.walk(n.block || [], 'trigger', cur, env);
    if (k === 'trigger_if' || k === 'trigger_else_if' || k === 'trigger_else') return this.walk(n.block || [], 'trigger', cur, env);
    if (k === 'custom_tooltip' || k === 'custom_description') return this.tooltip(n, 'trigger', cur, env);
    if (k === 'limit') return this.walk(n.block || [], 'trigger', cur, env);
    if (['if', 'else_if', 'else', 'hidden_effect'].includes(k)) return this.error(env, n.line, `'${k}' is not valid in a trigger block`);
    if (VALUE_OPS.has(k)) return this.error(env, n.line, `'${k}' is a script-value operation, not a trigger`);
    if (k === 'exists') return void this.resolve(n.value, cur, env, n.line);
    if (k === 'always') return;
    if (isRefKey(k)) {
      if (n.block) return this.walk(n.block, 'trigger', this.scopeOf(k, cur, env, n.line), env);
      const left = this.resolve(k, cur, env, n.line);
      if (n.valueType === 'word') {
        const right = this.resolve(n.value, cur, env, n.line);
        const scopes = [left.type, right.type].filter((t) => t && !NOT_SCOPES.has(t));
        const values = [left.type, right.type].filter((t) => t === 'value');
        if (scopes.length && values.length) this.error(env, n.line, `'${k} ${n.op} ${n.value}' compares a scope with a value`);
      }
      return;
    }
    if (this.defs.triggers.has(k)) return this.call(k, 'triggers', n, cur, env);
    if (this.vanilla.triggers.has(k)) return;
    if (this.defs.effects.has(k) || this.vanilla.effects.has(k)) return this.error(env, n.line, `scripted effect '${k}' used in a trigger block`);
    const link = this.docs.links.get(k);
    const doc = this.docs.triggers.get(k);
    const blockTrigger = doc && (this.isIterator(k, doc) || BLOCK_TRIGGERS[k]);
    if (link && (n.block ? !blockTrigger : !doc)) {
      if (n.block) return this.walk(n.block, 'trigger', this.scopeOf(k, cur, env, n.line), env);
      this.resolve(k, cur, env, n.line);
      if (n.valueType === 'word') this.resolve(n.value, cur, env, n.line);
      return;
    }
    if (!doc) {
      if (this.docs.effects.has(k)) this.error(env, n.line, `effect '${k}' used in a trigger block`);
      return;
    }
    this.checkSupported(doc, k, cur, env, n.line);
    if (this.isIterator(k, doc)) return this.iterator(n, 'trigger', doc, cur, env);
    if (k === 'save_temporary_scope_as') return this.record('saved', n.value, cur);
    if (n.block) {
      const spec = BLOCK_TRIGGERS[k];
      if (!spec) return this.error(env, n.line, `the lint has no parameter table for trigger '${k}' (add it to BLOCK_TRIGGERS)`);
      this.params(n, spec, cur, env);
      return;
    }
    this.triggerArgument(n, doc, cur, env);
  }

  /** The right-hand side of `trigger = value`, by the trigger's documented traits. */
  triggerArgument(n, doc, cur, env) {
    const traits = doc.traits || '';
    let m;
    if (traits.startsWith('<')) {
      if (n.valueType === 'word' && !/^\d{1,4}\.\d{1,2}\.\d{1,2}$/.test(n.value)) this.operand(n, cur, env);
    } else if (traits.startsWith('yes/no')) {
      const r = this.resolve(n.value, cur, env, n.line);
      if (r.type && r.type !== 'boolean') this.error(env, n.line, `'${n.key}' expects yes/no, not '${n.value}'`);
    } else if ((m = /^([a-z_]+) (?:target|scope)\b/.exec(traits))) {
      this.target(n.value, m[1], cur, env, n.line);
    } else if (n.valueType === 'word' && /^(scope|var|global_var):|^(root|this)\./.test(n.value)) {
      this.resolve(n.value, cur, env, n.line);
    }
  }

  tooltip(n, ctx, cur, env) {
    for (const c of n.block || []) {
      if (!c.key) continue;
      if (c.key === 'text') continue;
      if (c.key === 'subject' || c.key === 'object') this.target(c.value, null, cur, env, c.line);
      else if (ctx === 'effect') this.effect(c, cur, env);
      else this.trigger(c, cur, env);
    }
  }

  value(n, cur, env) {
    const k = n.key;
    if (VALUE_OPS.has(k)) return void this.operand(n, cur, env);
    if (VALUE_FLAGS.has(k)) return;
    if (k === 'if' || k === 'else_if' || k === 'else') {
      for (const c of n.block || []) {
        if (c.key === 'limit') this.walk(c.block || [], 'trigger', cur, env);
        else if (c.key) this.value(c, cur, env);
      }
      return;
    }
    if (k === 'desc' || k === 'format') return;
    if (isRefKey(k) && n.block) return this.walk(n.block, 'value', this.scopeOf(k, cur, env, n.line), env);
    const doc = this.docs.effects.get(k);
    if (doc && this.isIterator(k, doc)) return this.iterator(n, 'value', doc, cur, env);
    const link = this.docs.links.get(k);
    if (link && n.block) return this.walk(n.block, 'value', this.scopeOf(k, cur, env, n.line), env);
    this.error(env, n.line, `'${k}' is not a script-value operation`);
  }
}
