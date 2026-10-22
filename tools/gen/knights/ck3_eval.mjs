// ck3_eval.mjs - a small expected-value evaluator for CK3 artifact creation script (read-only analysis).
//
// It walks scripted effects the way the engine would for ONE freshly created artifact and enumerates every
// random branch with its probability, so the generator can say "an illustrious sword gets +9.6 prowess and
// +4.8 % knight effectiveness on average" straight from the install's modifier pools.
//
// Supported effect keys: if/else_if/else, random_list (weights, trigger, modifier add/factor, pick, unique),
// random (chance), add_artifact_modifier, set_artifact_feature_group, hidden_effect / hidden_effect_new_object /
// scope:newly_created_artifact blocks, create_artifact (its `modifier`), and calls of scripted effects
// (`name = yes`). Everything else is ignored and counted in `unknownEffects`.
// Supported trigger keys: rarity, rarity_level, artifact_slot_type, artifact_type, category,
// has_artifact_feature_group, has_artifact_feature, exists (scope:weapon_type/bow_type/armor_type/wealth/quality),
// scope:weapon_type / scope:bow_type / scope:armor_type = flag:x, scope:wealth / scope:quality comparisons,
// scope:owner.culture (= ctx.knightlyCulture), AND/OR/NOT/NOR/NAND, always, scripted triggers (`name = yes`).
// Unknown triggers evaluate to false and are counted in `unknownTriggers` (the "no special circumstances" baseline:
// no inspiration owner, no traditions, no buildings).

export const RARITIES = ['common', 'masterwork', 'famed', 'illustrious'];

const NON_BODY_OPTION_KEYS = new Set(['trigger', 'modifier', 'ai_value_modifier', 'opinion_modifier', 'desc', 'show_chance',
  'min', 'max', 'compare_modifier', 'weight', 'show_as_unavailable']);

function cmp(a, op, b) {
  switch (op) {
    case '=': case '==': return a === b;
    case '!=': return a !== b;
    case '<': return a < b;
    case '<=': return a <= b;
    case '>': return a > b;
    case '>=': return a >= b;
    case '?=': return a === b;
    default: return false;
  }
}

function stateKey(st) {
  const mods = [...st.mods.entries()].filter(([, n]) => n !== 0).sort(([a], [b]) => a.localeCompare(b)).map(([k, n]) => `${k}*${n}`).join(',');
  const groups = [...st.groups].sort().join(',');
  return `${mods}|${groups}`;
}

function merge(states) {
  const byKey = new Map();
  for (const st of states) {
    if (st.p <= 0) continue;
    const k = stateKey(st);
    const prev = byKey.get(k);
    if (prev) prev.p += st.p;
    else byKey.set(k, st);
  }
  return [...byKey.values()];
}

function cloneState(st, p) {
  return { p, mods: new Map(st.mods), groups: new Set(st.groups) };
}

export class ArtifactEvaluator {
  /**
   * @param {Map<string, object[]>} effects  scripted effect name -> parsed body nodes
   * @param {Map<string, object[]>} triggers scripted trigger name -> parsed body nodes
   */
  constructor(effects, triggers) {
    this.effects = effects;
    this.triggers = triggers;
    this.unknownEffects = new Map();
    this.unknownTriggers = new Map();
    this.depth = 0;
  }

  note(map, key) { map.set(key, (map.get(key) || 0) + 1); }

  // ---- triggers -------------------------------------------------------------------------------------------
  evalTriggerList(nodes, ctx, st) {
    for (const n of nodes) if (!this.evalTrigger(n, ctx, st)) return false;
    return true;
  }

  evalTrigger(n, ctx, st) {
    const k = n.key;
    const v = n.value;
    if (k === null) return true;
    switch (k) {
      case 'AND': return this.evalTriggerList(n.block || [], ctx, st);
      case 'OR': return (n.block || []).some((c) => this.evalTrigger(c, ctx, st));
      case 'NOT': return !this.evalTriggerList(n.block || [], ctx, st);
      case 'NOR': return !(n.block || []).some((c) => this.evalTrigger(c, ctx, st));
      case 'NAND': return !this.evalTriggerList(n.block || [], ctx, st);
      case 'always': return v === 'yes';
      case 'rarity': return cmp(ctx.rarity, n.op, v);
      case 'rarity_level': {
        const lvl = RARITIES.indexOf(ctx.rarity) + 1;
        const rhs = RARITIES.includes(v) ? RARITIES.indexOf(v) + 1 : Number(v);
        return cmp(lvl, n.op, rhs);
      }
      case 'artifact_slot_type': return cmp(ctx.slotType, n.op, v);
      case 'artifact_type': return cmp(ctx.type, n.op, v);
      case 'category': return cmp(ctx.category || 'inventory', n.op, v);
      case 'has_artifact_feature_group': return st.groups.has(v);
      case 'has_artifact_feature': return (ctx.features || new Set()).has(v);
      case 'exists': {
        if (v === 'scope:weapon_type') return !!ctx.weaponType;
        if (v === 'scope:bow_type') return !!ctx.bowType;
        if (v === 'scope:armor_type') return !!ctx.armorType;
        if (v === 'scope:wealth') return ctx.wealth != null;
        if (v === 'scope:quality') return ctx.quality != null;
        if (v === 'scope:newly_created_artifact') return true;
        this.note(this.unknownTriggers, `exists = ${v}`);
        return false;
      }
      case 'scope:weapon_type': return cmp(`flag:${ctx.weaponType}`, n.op, v);
      case 'scope:bow_type': return cmp(`flag:${ctx.bowType}`, n.op, v);
      case 'scope:armor_type': return cmp(`flag:${ctx.armorType}`, n.op, v);
      case 'scope:wealth': return ctx.wealth != null && cmp(ctx.wealth, n.op, Number(v));
      case 'scope:quality': return ctx.quality != null && cmp(ctx.quality, n.op, Number(v));
      case 'scope:owner.culture': return !!ctx.knightlyCulture;
      default: break;
    }
    if (this.triggers.has(k) && (v === 'yes' || v === 'no')) {
      if (this.depth > 40) return false;
      this.depth++;
      const r = this.evalTriggerList(this.triggers.get(k), ctx, st);
      this.depth--;
      return v === 'yes' ? r : !r;
    }
    this.note(this.unknownTriggers, k);
    return false;
  }

  // ---- effects --------------------------------------------------------------------------------------------
  /** Runs `nodes` on every state; returns the merged list of resulting states. */
  run(nodes, states, ctx) {
    let cur = states;
    let chain = null; // pending if/else_if/else chain: [{limit, body}] collected until the chain ends
    const flushChain = () => {
      if (!chain) return;
      const branches = chain;
      chain = null;
      const out = [];
      for (const st of cur) {
        const hit = branches.find((b) => b.limit === null || this.evalTriggerList(b.limit, ctx, st));
        if (!hit) { out.push(st); continue; }
        out.push(...this.run(hit.body, [st], ctx));
      }
      cur = merge(out);
    };
    for (const n of nodes) {
      const k = n.key;
      if (k === 'if' || k === 'else_if' || k === 'else') {
        const blk = n.block || [];
        const limitNode = blk.find((c) => c.key === 'limit');
        const body = blk.filter((c) => c.key !== 'limit');
        const branch = { limit: k === 'else' ? null : (limitNode ? limitNode.block || [] : []), body };
        if (k === 'if') { flushChain(); chain = [branch]; }
        else if (chain) { chain.push(branch); if (k === 'else') flushChain(); }
        // a dangling else_if/else without an if is ignored
        continue;
      }
      flushChain();
      cur = this.runOne(n, cur, ctx);
    }
    flushChain();
    return cur;
  }

  runOne(n, states, ctx) {
    const k = n.key;
    switch (k) {
      case 'add_artifact_modifier': {
        for (const st of states) st.mods.set(n.value, (st.mods.get(n.value) || 0) + 1);
        return merge(states);
      }
      case 'set_artifact_feature_group': {
        for (const st of states) st.groups.add(n.value);
        return merge(states);
      }
      case 'create_artifact':
      case 'reforge_artifact': {
        const mods = (n.block || []).filter((c) => c.key === 'modifier' && c.value).map((c) => c.value);
        for (const st of states) for (const m of mods) st.mods.set(m, (st.mods.get(m) || 0) + 1);
        return merge(states);
      }
      case 'hidden_effect':
      case 'hidden_effect_new_object':
      case 'scope:newly_created_artifact':
      case 'custom_tooltip':
      case 'show_as_tooltip':
        return n.block ? this.run(n.block, states, ctx) : states;
      case 'random_list': return this.runRandomList(n.block || [], states, ctx);
      case 'random': return this.runRandom(n.block || [], states, ctx);
      default: break;
    }
    if (k && this.effects.has(k) && (n.value === 'yes' || n.block)) {
      if (this.depth > 40) return states;
      this.depth++;
      const r = this.run(this.effects.get(k), states, ctx);
      this.depth--;
      return r;
    }
    if (k) this.note(this.unknownEffects, k);
    return states;
  }

  optionWeight(opt, base, ctx, st) {
    const blk = opt.block || [];
    const trig = blk.find((c) => c.key === 'trigger');
    if (trig && !this.evalTriggerList(trig.block || [], ctx, st)) return 0;
    let w = base;
    for (const m of blk.filter((c) => c.key === 'modifier' && c.block)) {
      const cond = m.block.filter((c) => !['add', 'factor', 'desc'].includes(c.key));
      if (!this.evalTriggerList(cond, ctx, st)) continue;
      const add = m.block.find((c) => c.key === 'add');
      const factor = m.block.find((c) => c.key === 'factor');
      if (add && add.value !== undefined) w += Number(add.value);
      if (factor && factor.value !== undefined) w *= Number(factor.value);
    }
    return Math.max(0, w);
  }

  runRandomList(blk, states, ctx) {
    const pickNode = blk.find((c) => c.key === 'pick');
    const pick = pickNode ? Number(pickNode.value) : 1;
    const unique = (blk.find((c) => c.key === 'unique') || {}).value === 'yes';
    const options = blk.filter((c) => c.key !== null && /^[0-9.]+$/.test(c.key) && c.block);
    const out = [];
    for (const st of states) {
      const weights = options.map((o) => this.optionWeight(o, Number(o.key), ctx, st));
      const bodies = options.map((o) => (o.block || []).filter((c) => !NON_BODY_OPTION_KEYS.has(c.key)));
      const rec = (state, remaining, used) => {
        if (remaining === 0) { out.push(state); return; }
        const avail = weights.map((w, i) => (unique && used.has(i) ? 0 : w));
        const total = avail.reduce((a, b) => a + b, 0);
        if (total <= 0) { out.push(state); return; }
        avail.forEach((w, i) => {
          if (w <= 0) return;
          const branch = cloneState(state, state.p * (w / total));
          const after = this.run(bodies[i], [branch], ctx);
          const nextUsed = new Set(used); nextUsed.add(i);
          for (const a of after) rec(a, remaining - 1, nextUsed);
        });
      };
      rec(st, pick, new Set());
    }
    return merge(out);
  }

  runRandom(blk, states, ctx) {
    const chanceNode = blk.find((c) => c.key === 'chance');
    const chance = chanceNode && chanceNode.value !== undefined ? Number(chanceNode.value) / 100 : 0;
    const body = blk.filter((c) => !['chance', 'modifier'].includes(c.key));
    const out = [];
    for (const st of states) {
      out.push(cloneState(st, st.p * (1 - chance)));
      out.push(...this.run(body, [cloneState(st, st.p * chance)], ctx));
    }
    return merge(out);
  }

  /** Evaluates `nodes` from one empty artifact (plus `baseMods`) and returns the outcome distribution. */
  outcomes(nodes, ctx, baseMods = []) {
    const st = { p: 1, mods: new Map(), groups: new Set(ctx.groups || []) };
    for (const m of baseMods) st.mods.set(m, (st.mods.get(m) || 0) + 1);
    return this.run(nodes, [st], ctx);
  }
}

/**
 * Summarises outcome states into expected values of modifier fields.
 * @param {object[]} states  outcomes ({p, mods})
 * @param {Map<string, object>} modValues modifier key -> {field: number}
 * @param {string[]} fields  modifier fields to report
 */
export function summarise(states, modValues, fields) {
  const total = states.reduce((a, s) => a + s.p, 0) || 1;
  const res = {};
  for (const f of fields) res[f] = { mean: 0, min: Infinity, max: -Infinity, p_nonzero: 0 };
  const modMeans = new Map();
  for (const st of states) {
    const p = st.p / total;
    const sums = Object.fromEntries(fields.map((f) => [f, 0]));
    for (const [m, n] of st.mods) {
      modMeans.set(m, (modMeans.get(m) || 0) + p * n);
      const vals = modValues.get(m);
      if (!vals) continue;
      for (const f of fields) if (vals[f] !== undefined) sums[f] += vals[f] * n;
    }
    for (const f of fields) {
      const r = res[f];
      r.mean += p * sums[f];
      r.min = Math.min(r.min, sums[f]);
      r.max = Math.max(r.max, sums[f]);
      if (sums[f] !== 0) r.p_nonzero += p;
    }
  }
  for (const f of fields) {
    const r = res[f];
    if (r.min === Infinity) { r.min = 0; r.max = 0; }
    r.mean = round(r.mean, 4); r.min = round(r.min, 4); r.max = round(r.max, 4); r.p_nonzero = round(r.p_nonzero, 4);
  }
  const modifiers = Object.fromEntries([...modMeans.entries()].filter(([, v]) => v > 1e-9).sort(([a], [b]) => a.localeCompare(b)).map(([k, v]) => [k, round(v, 4)]));
  return { fields: res, expected_modifier_counts: modifiers };
}

export function round(x, d = 4) {
  const f = 10 ** d;
  return Math.round(x * f) / f;
}
