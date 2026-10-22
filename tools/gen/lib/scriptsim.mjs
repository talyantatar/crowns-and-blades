// scriptsim.mjs - a small interpreter for the subset of CK3 script the battle clock uses (docs/PROTOCOL.md §14),
// so test_ck3_mod.mjs can run the mod's own scripted effects, triggers and script values against a toy world
// instead of re-implementing them. It knows characters, armies with men-at-arms regiments, combats with two sides and the
// province they are in; saved scopes (temporary ones alike), variables with expiry, global variables and lists; and the
// effects, triggers, links and value operations of the steering script. Anything else throws, so a script change that leaves
// this subset is noticed instead of being skipped silently.
//
// Semantics follow CK3 where the battle clock depends on them: in a script value `min = x` is a floor and `max = x` a cap,
// applied in order; operations inside an iterator of a value block act on the outer value; a saved scope lives until the end of
// the effect run (one ScriptSim.call) unless cleared; `days = n` on a variable expires it n days later (ScriptSim.advance).

import fs from 'node:fs';
import path from 'node:path';
import { parseText, stripBom } from './pdx.mjs';

const ENGINE_VALUES = {
  army: {
    army_size: (a) => a.levies + a.regiments.reduce((n, r) => n + r.cur, 0),
  },
  regiment: {
    maa_current_troops_count: (r) => r.cur,
    maa_max_troops_count: (r) => r.max,
  },
  combat_side: {
    num_enemies_killed: (s) => s.killed,
    side_soldiers: (s) => s.soldiers,
  },
};

const LINKS = {
  combat_attacker: (c) => c.att,
  combat_defender: (c) => c.def,
  combat: (s) => s.combat,
  enemy_side: (s) => s.enemy,
  location: (x) => x.location,
  army_owner: (a) => a.owner,
  army_commander: (a) => a.commander,
  involved_combat_side: (a) => a.side,
  // The engine's side lists: empty unless a test fills `commanders` (side_commander was unset at on_combat_start in game).
  side_commander: (s) => s.commanders?.[0] ?? null,
};

const isScope = (v) => v !== null && typeof v === 'object' && 'kind' in v;

/** A character: variables, character modifiers, realm laws and traits for the headroom values, and its armies. */
export function character(name, { laws = [], traits = [], modifiers = [] } = {}) {
  return { kind: 'character', name, vars: new Map(), modifiers: new Set(modifiers), laws: new Set(laws), traits: new Set(traits), armies: [] };
}

/** An army of `owner` with levies and MaA regiments [{ type, cur, max }]; `side` is set while it fights. */
export function army(name, { owner, commander = null, levies = 0, regiments = [] }) {
  const a = { kind: 'army', name, vars: new Map(), owner, commander, levies, side: null, location: null,
    regiments: regiments.map((r) => ({ kind: 'regiment', vars: new Map(), ...r })) };
  owner.armies.push(a);
  return a;
}

/** A combat in a new province; both sides start with no armies. */
export function combat(name) {
  const location = { kind: 'province', name, vars: new Map(), armies: [] };
  const c = { kind: 'combat', name, vars: new Map(), location };
  const side = (attacker) => ({ kind: 'combat_side', vars: new Map(), combat: c, attacker, killed: 0, soldiers: 0, flags: {} });
  c.att = side(true);
  c.def = side(false);
  c.att.enemy = c.def;
  c.def.enemy = c.att;
  return c;
}

/** Puts `a` into the combat on `side` (and into its province). */
export function joinCombat(a, side) {
  a.side = side;
  a.location = side.combat.location;
  if (!a.location.armies.includes(a)) a.location.armies.push(a);
}

export class ScriptSim {
  /**
   * @param {string} modDir   the mod folder (common/scripted_effects, scripted_triggers, script_values are loaded)
   * @param {object} options  `overrides`: relative path -> file text (in memory); `stubs`: effect name -> (sim, ctx, params)
   *                          run instead of the scripted effect of that name
   */
  constructor(modDir, { overrides = {}, stubs = {} } = {}) {
    this.effects = new Map();
    this.triggers = new Map();
    this.values = new Map();
    const load = (dir, into) => {
      const full = path.join(modDir, ...dir.split('/'));
      const names = new Set(fs.existsSync(full) ? fs.readdirSync(full).filter((f) => f.endsWith('.txt')) : []);
      for (const rel of Object.keys(overrides)) if (rel.startsWith(`${dir}/`)) names.add(rel.slice(dir.length + 1));
      for (const name of names) {
        const rel = `${dir}/${name}`;
        const text = rel in overrides ? overrides[rel] : fs.readFileSync(path.join(full, name), 'utf8');
        for (const n of parseText(stripBom(text))) into.set(n.key, n);
      }
    };
    load('common/scripted_effects', this.effects);
    load('common/scripted_triggers', this.triggers);
    load('common/script_values', this.values);
    this.stubs = stubs;
    this.globals = new Map();
    this.lists = new Map();
    this.expiry = []; // { holder: Map, name, day }
    this.day = 0;
    this.log = []; // { record, saved: Map copy }
    this.events = []; // { id, days, root, saved }
    this.toasts = [];
    this.player = null;
  }

  list(name) {
    if (!this.lists.has(name)) this.lists.set(name, []);
    return this.lists.get(name);
  }

  /** Moves the calendar to `day`: every variable set with `days` that has run out is removed. */
  advance(day) {
    this.day = day;
    this.expiry = this.expiry.filter((e) => {
      if (e.day > day) return true;
      if (e.holder.get(e.name)?.since === e.since) e.holder.delete(e.name);
      return false;
    });
  }

  /** Runs the scripted effect `name` (an effect run of its own: fresh saved scopes unless `saved` is given). */
  call(name, { root, scope = root, params = {}, saved = new Map() } = {}) {
    const ctx = { root, this: scope, saved, params };
    this.runEffect(name, params, ctx);
    return ctx;
  }

  /** Evaluates the script value `name` with this = `scope`. */
  evalValue(name, scope, { root = scope, saved = new Map(), params = {} } = {}) {
    return this.value(this.values.get(name), { root, this: scope, saved, params });
  }

  /** Evaluates the scripted trigger `name` with this = `scope`. */
  evalTrigger(name, scope, { root = scope, saved = new Map(), params = {} } = {}) {
    return this.trigger(this.triggers.get(name).block, { root, this: scope, saved, params });
  }

  // --- references and values ------------------------------------------------------------------------------------

  sub(s, params) {
    return typeof s === 'string' ? s.replace(/\$([A-Za-z0-9_]+)\$/g, (m, p) => (p in params ? params[p] : m)) : s;
  }

  readVar(holder, name, expr) {
    const v = holder.get(name);
    if (v === undefined) throw new Error(`${expr} is not set`);
    return v.value;
  }

  setVar(holder, name, value, days) {
    const entry = { value, since: Symbol(name) };
    holder.set(name, entry);
    if (days !== undefined) this.expiry.push({ holder, name, day: this.day + days, since: entry.since });
  }

  /** One segment of a chain applied to `cur` (a scope, or null at the start). */
  step(seg, cur, ctx, expr) {
    let m;
    if (cur === null) {
      if (seg === 'root') return ctx.root;
      if (seg === 'this') return ctx.this;
      if ((m = /^scope:(.+)$/.exec(seg))) {
        if (!ctx.saved.has(m[1])) throw new Error(`${expr}: scope:${m[1]} is not set`);
        return ctx.saved.get(m[1]);
      }
      if ((m = /^global_var:(.+)$/.exec(seg))) return this.readVar(this.globals, m[1], expr);
      cur = ctx.this;
    }
    if ((m = /^var:(.+)$/.exec(seg))) return this.readVar(cur.vars, m[1], expr);
    if (LINKS[seg]) {
      const next = LINKS[seg](cur);
      if (next === undefined) throw new Error(`${expr}: link ${seg} on ${cur.kind}`);
      return next;
    }
    if (ENGINE_VALUES[cur.kind]?.[seg]) return ENGINE_VALUES[cur.kind][seg](cur);
    if (this.values.has(seg)) return this.value(this.values.get(seg), { ...ctx, this: cur });
    throw new Error(`${expr}: unknown ${seg} in ${cur.kind} scope`);
  }

  ref(expr0, ctx) {
    const expr = this.sub(expr0, ctx.params);
    if (/^-?\d+(\.\d+)?$/.test(expr)) return Number(expr);
    let cur = null;
    for (const seg of expr.split('.')) cur = this.step(seg, cur, ctx, expr);
    return cur;
  }

  /** The target of a scope block: null for an unset saved scope or link when the block is optional (?=). */
  target(n, ctx) {
    if (n.op === '?=') {
      const m = /^scope:([^.]+)$/.exec(n.key);
      if (m && !ctx.saved.has(m[1])) return null;
    }
    const t = this.ref(n.key, ctx);
    return t === undefined ? null : t;
  }

  /** A value: a reference, or a block whose operations run in order on one accumulator. */
  value(node, ctx) {
    if (!node.block) return this.ref(node.value, ctx);
    const acc = { v: 0 };
    this.valueOps(node.block, ctx, acc);
    return acc.v;
  }

  valueOps(nodes, ctx, acc) {
    for (let i = 0; i < nodes.length; i++) {
      const n = nodes[i];
      if (n.key === 'if') {
        const chain = [n];
        while (nodes[i + 1] && ['else_if', 'else'].includes(nodes[i + 1].key)) chain.push(nodes[++i]);
        const hit = this.pick(chain, ctx);
        if (hit) this.valueOps(hit.block.filter((x) => x.key !== 'limit'), ctx, acc);
        continue;
      }
      if (n.key === 'every_army_maa_regiment') {
        const limit = n.block.find((x) => x.key === 'limit');
        for (const r of ctx.this.regiments) {
          const inner = { ...ctx, this: r };
          if (!limit || this.trigger(limit.block, inner)) this.valueOps(n.block.filter((x) => x.key !== 'limit'), inner, acc);
        }
        continue;
      }
      if (n.key === 'round') { acc.v = Math.round(acc.v); continue; }
      if (n.key === 'floor') { acc.v = Math.floor(acc.v); continue; }
      const x = this.value(n, ctx);
      if (typeof x !== 'number' || !Number.isFinite(x)) throw new Error(`${n.key}: ${x} is not a number`);
      switch (n.key) {
        case 'value': acc.v = x; break;
        case 'add': acc.v += x; break;
        case 'subtract': acc.v -= x; break;
        case 'multiply': acc.v *= x; break;
        case 'divide': acc.v = x === 0 ? acc.v : acc.v / x; break;
        case 'modulo': acc.v %= x; break;
        case 'min': acc.v = Math.max(acc.v, x); break; // a floor
        case 'max': acc.v = Math.min(acc.v, x); break; // a cap
        default: throw new Error(`unsupported value operation ${n.key}`);
      }
    }
  }

  pick(chain, ctx) {
    return chain.find((c) => c.key === 'else' || this.trigger(c.block.find((x) => x.key === 'limit').block, ctx));
  }

  // --- triggers --------------------------------------------------------------------------------------------------

  trigger(nodes, ctx) {
    return nodes.every((n) => this.trigger1(n, ctx));
  }

  trigger1(n0, ctx) {
    const n = { ...n0, key: this.sub(n0.key, ctx.params), value: this.sub(n0.value, ctx.params) };
    const me = ctx.this;
    switch (n.key) {
      case 'NOT': return !this.trigger(n.block, ctx);
      case 'NOR': return !n.block.some((c) => this.trigger1(c, ctx));
      case 'OR': return n.block.some((c) => this.trigger1(c, ctx));
      case 'AND': return this.trigger(n.block, ctx);
      case 'custom_tooltip': return this.trigger(n.block.filter((c) => c.key !== 'text'), ctx);
      case 'has_variable': return me.vars.has(n.value);
      case 'has_global_variable': return this.globals.has(n.value);
      case 'has_character_modifier': return me.modifiers.has(n.value);
      case 'has_realm_law': return me.laws.has(n.value);
      case 'has_trait': return me.traits.has(n.value);
      case 'is_maa_type': return me.type === n.value;
      case 'is_alive': return (me.alive !== false) === (n.value === 'yes');
      case 'is_ai': return (me.ai === true) === (n.value === 'yes');
      case 'is_army_in_combat': return (me.side !== null) === (n.value === 'yes');
      case 'exists': {
        if (/^scope:/.test(n.value)) return ctx.saved.has(n.value.slice(6)) && ctx.saved.get(n.value.slice(6)) !== null;
        try { return this.ref(n.value, ctx) !== null; } catch { return false; }
      }
      case 'is_target_in_global_variable_list': {
        const target = this.ref(n.block.find((c) => c.key === 'target').value, ctx);
        return this.list(n.block.find((c) => c.key === 'name').value).includes(target);
      }
      case 'any_in_global_list': {
        const conds = n.block.filter((c) => c.key !== 'variable');
        return this.list(n.block.find((c) => c.key === 'variable').value).some((x) => this.trigger(conds, { ...ctx, this: x }));
      }
      case 'any_army_maa_regiment': return me.regiments.some((r) => this.trigger(n.block, { ...ctx, this: r }));
      case 'any_army': return me.armies.some((a) => this.trigger(n.block, { ...ctx, this: a }));
      // The engine's side list (see every_side_commander).
      case 'any_side_commander': return (me.commanders || []).some((x) => this.trigger(n.block, { ...ctx, this: x }));
      case 'is_combat_side_attacker': return me.attacker === (n.value === 'yes');
      default: break;
    }
    if (this.triggers.has(n.key)) {
      const params = n.block ? Object.fromEntries(n.block.map((c) => [c.key, this.sub(c.value, ctx.params)])) : {};
      const r = this.trigger(this.triggers.get(n.key).block, { ...ctx, params });
      return n.block || n.value === 'yes' ? r : !r;
    }
    if (n.block) {
      const target = this.target(n, ctx);
      if (target === null) {
        if (n.op === '?=') return false;
        throw new Error(`${n.key} is unset in a trigger`);
      }
      return this.trigger(n.block, { ...ctx, this: target });
    }
    if (n.key === 'this') return me === this.ref(n.value, ctx);
    const a = this.ref(n.key, ctx);
    const b = this.ref(n.value, ctx);
    if (isScope(a) || isScope(b)) {
      if (n.op !== '=') throw new Error(`${n.key} ${n.op} ${n.value}: scopes compare with = only`);
      return a === b;
    }
    switch (n.op) {
      case '=': return a === b;
      case '!=': return a !== b;
      case '<': return a < b;
      case '>': return a > b;
      case '<=': return a <= b;
      case '>=': return a >= b;
      default: throw new Error(`unsupported comparison ${n.op}`);
    }
  }

  // --- effects ---------------------------------------------------------------------------------------------------

  runEffect(name, params, ctx) {
    if (this.stubs[name]) {
      this.stubs[name](this, ctx, params);
      return;
    }
    const def = this.effects.get(name);
    if (!def) throw new Error(`unknown scripted effect ${name}`);
    this.run(def.block, { ...ctx, params });
  }

  run(nodes, ctx) {
    for (let i = 0; i < nodes.length; i++) {
      const n = nodes[i];
      if (n.key === 'if') {
        const chain = [n];
        while (nodes[i + 1] && ['else_if', 'else'].includes(nodes[i + 1].key)) chain.push(nodes[++i]);
        const hit = this.pick(chain, ctx);
        if (hit) this.run(hit.block.filter((x) => x.key !== 'limit'), ctx);
        continue;
      }
      this.effect1(n, ctx);
    }
  }

  field(n, key, ctx) {
    const f = n.block.find((c) => c.key === key);
    return f ? this.sub(f.value, ctx.params) : undefined;
  }

  effect1(n0, ctx) {
    const n = { ...n0, key: this.sub(n0.key, ctx.params), value: this.sub(n0.value, ctx.params) };
    const me = ctx.this;
    const valueOf = (key) => this.value(n.block.find((c) => c.key === key), ctx);
    const each = (items, body = n.block) => {
      const limit = body.find((c) => c.key === 'limit');
      const effects = body.filter((c) => !['limit', 'variable', 'order_by'].includes(c.key));
      for (const x of [...items]) {
        const inner = { ...ctx, this: x };
        if (!limit || this.trigger(limit.block, inner)) this.run(effects, inner);
      }
    };
    switch (n.key) {
      case 'hidden_effect': this.run(n.block, ctx); return;
      case 'set_variable': {
        const days = this.field(n, 'days', ctx);
        this.setVar(me.vars, this.field(n, 'name', ctx), valueOf('value'), days === undefined ? undefined : Number(days));
        return;
      }
      case 'change_variable': {
        const name = this.field(n, 'name', ctx);
        const add = n.block.find((c) => c.key === 'add') ? valueOf('add') : -valueOf('subtract');
        me.vars.get(name).value = this.readVar(me.vars, name, name) + add;
        return;
      }
      case 'remove_variable': me.vars.delete(n.value); return;
      case 'set_global_variable': {
        const days = this.field(n, 'days', ctx);
        this.setVar(this.globals, this.field(n, 'name', ctx), valueOf('value'), days === undefined ? undefined : Number(days));
        return;
      }
      case 'change_global_variable': {
        const name = this.field(n, 'name', ctx);
        this.globals.get(name).value = this.readVar(this.globals, name, name) + valueOf('add');
        return;
      }
      case 'remove_global_variable': this.globals.delete(n.value); return;
      case 'save_scope_value_as':
      case 'save_temporary_scope_value_as': ctx.saved.set(this.field(n, 'name', ctx), valueOf('value')); return;
      case 'save_scope_as':
      case 'save_temporary_scope_as': ctx.saved.set(n.value, me); return;
      case 'clear_saved_scope': ctx.saved.delete(n.value); return;
      case 'info_log': this.log.push({ record: n.value, saved: new Map(ctx.saved), root: ctx.root }); return;
      case 'info_log_scopes': return;
      case 'every_in_global_list': each(this.list(this.field(n, 'variable', ctx))); return;
      case 'random_in_global_list': {
        const limit = n.block.find((c) => c.key === 'limit');
        const hit = this.list(this.field(n, 'variable', ctx)).find((x) => !limit || this.trigger(limit.block, { ...ctx, this: x }));
        if (hit) this.run(n.block.filter((c) => !['limit', 'variable'].includes(c.key)), { ...ctx, this: hit });
        return;
      }
      case 'every_army_in_location': each(me.armies); return;
      case 'ordered_army_in_location': {
        const limit = n.block.find((c) => c.key === 'limit');
        const order = n.block.find((c) => c.key === 'order_by');
        const fit = me.armies.filter((a) => !limit || this.trigger(limit.block, { ...ctx, this: a }));
        if (!fit.length) return;
        const best = fit.reduce((x, y) => (this.value(order, { ...ctx, this: y }) > this.value(order, { ...ctx, this: x }) ? y : x));
        this.run(n.block.filter((c) => !['limit', 'order_by'].includes(c.key)), { ...ctx, this: best });
        return;
      }
      case 'every_army_maa_regiment': each(me.regiments); return;
      // The engine's side list; empty unless the test fills it (side_commander was unset at on_combat_start in game).
      case 'every_side_commander': each(me.commanders || []); return;
      case 'random_player': {
        const limit = n.block.find((c) => c.key === 'limit');
        const inner = { ...ctx, this: this.player };
        if (this.player && (!limit || this.trigger(limit.block, inner))) this.run(n.block.filter((c) => c.key !== 'limit'), inner);
        return;
      }
      case 'add_to_global_variable_list': {
        const l = this.list(this.field(n, 'name', ctx));
        const target = this.ref(this.field(n, 'target', ctx), ctx);
        if (!l.includes(target)) l.push(target);
        return;
      }
      case 'remove_list_global_variable': {
        const l = this.list(this.field(n, 'name', ctx));
        const target = this.ref(this.field(n, 'target', ctx), ctx);
        if (l.includes(target)) l.splice(l.indexOf(target), 1);
        return;
      }
      case 'add_character_modifier': me.modifiers.add(this.field(n, 'modifier', ctx)); return;
      case 'remove_character_modifier': me.modifiers.delete(n.value); return;
      // Both take a script value block (the whole block is the value).
      case 'change_maa_troops_count': me.cur = Math.max(0, Math.min(me.max, me.cur + Math.round(this.value(n, ctx)))); return;
      case 'deplete_army_by_percentage': {
        const share = 1 - this.value(n, ctx);
        me.levies = Math.round(me.levies * share);
        for (const r of me.regiments) r.cur = Math.round(r.cur * share);
        return;
      }
      case 'set_allow_early_retreat':
      case 'set_skip_pursuit':
      case 'set_winner':
      case 'force_win': me.flags[n.key] = this.day; return;
      case 'trigger_event': {
        const id = n.block ? this.field(n, 'id', ctx) : n.value;
        const days = n.block ? Number(this.field(n, 'days', ctx) || 0) : 0;
        this.events.push({ id, days, day: this.day, root: me, saved: new Map(ctx.saved) });
        return;
      }
      case 'send_interface_toast': this.toasts.push({ type: this.field(n, 'type', ctx), to: me }); return;
      default: break;
    }
    if (this.effects.has(n.key) || this.stubs[n.key]) {
      const params = n.block ? Object.fromEntries(n.block.map((c) => [c.key, this.sub(c.value, ctx.params)])) : {};
      this.runEffect(n.key, params, ctx);
      return;
    }
    if (n.block) {
      const target = this.target(n, ctx);
      if (target === null) {
        if (n.op === '?=') return;
        throw new Error(`${n.key} is unset in an effect`);
      }
      this.run(n.block, { ...ctx, this: target });
      return;
    }
    throw new Error(`unsupported effect ${n.key}`);
  }
}
