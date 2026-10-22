// Reference evaluator for bridge/data/ck3_combat_traits.json: what the bridge should compute from a battle snapshot before it
// writes request.json. The bridge (C#) is expected to port these two functions; test_combat_traits.mjs pins their results.
//
// Character input (what the CK3 mod logs or is asked to log, see emitPlan in the table):
//   { traits: ['forest_fighter', 'brave'], levels: { forest_fighter: 3, 'tourney_participant:foot': 2 },
//     cultureParameters: ['forest_trait_bonuses'], faithParameters: [], martial: 8, prowess: 12 }
//   levels: number of track steps reached (CX|txp); a trait without an entry is at level 0 (base modifiers only).
// Battle context: { terrain: 'forest', winter: 'none'|'mild'|'normal'|'harsh', coastal: false, role: 'attacker'|'defender',
//   enemyFaithHostility: 0..3, sameFaith, hostileTerritory, ownTerritory, ownArmy, sameHeritageCounty, independentPrimary }

/** Effects of one character that hold in `ctx`, combined per effect name (and per qualifier for hero/group effects). */
export function evaluateCharacter(table, ch, ctx) {
  const out = { side: {}, outcome: {}, hero: {}, retinue: {}, qualified: [] };
  const params = { culture: new Set(ch.cultureParameters || []), faith: new Set(ch.faithParameters || []) };
  for (const key of ch.traits || []) {
    const rec = table.traits[key];
    if (!rec) continue;
    const effects = [...rec.bl.base];
    for (const tr of rec.bl.tracks || []) {
      const lv = ch.levels?.[tr.steps.length && rec.bl.tracks.length > 1 ? `${key}:${tr.name}` : key] ?? 0;
      for (let i = 0; i < Math.min(lv, tr.steps.length); i++) effects.push(...tr.steps[i].effects);
    }
    for (const e of effects) {
      const scale = holds(table, e.when, ctx, params);
      if (scale === 0) continue;
      add(table, out, e, scale);
    }
  }
  return out;
}

function holds(table, when, ctx, params) {
  if (!when) return 1;
  if (when.terrain && !when.terrain.includes(ctx.terrain)) return 0;
  if (when.winter && !['mild', 'normal', 'harsh'].includes(ctx.winter)) return 0;
  if (when.coastal && !ctx.coastal) return 0;
  if (when.role && when.role !== ctx.role) return 0;
  if (when.cultureParameter && !params.culture.has(when.cultureParameter)) return 0;
  if (when.faithParameter && !params.faith.has(when.faithParameter)) return 0;
  for (const flag of ['sameFaith', 'hostileTerritory', 'ownTerritory', 'ownArmy', 'sameHeritageCounty', 'independentPrimary']) {
    if (when[flag] && !ctx[flag]) return 0;
  }
  if (when.faithHostility) return table.conversion.faithHostility.scale[ctx.enemyFaithHostility ?? 0] ?? 0;
  return 1;
}

const QUALIFIERS = ['weapon', 'vs', 'skill', 'troops', 'maaBaseType', 'maaType', 'per'];

function add(table, out, e, scale) {
  const [scope, name] = e.effect.split('.');
  const combine = table.blEffects[e.effect]?.combine ?? 'add';
  const qual = e.when ? Object.fromEntries(QUALIFIERS.filter((q) => e.when[q] !== undefined).map((q) => [q, e.when[q]])) : {};
  if (Object.keys(qual).length || scope === 'group' || scope === 'liege') {
    const k = `${e.effect}|${JSON.stringify(qual)}`;
    let entry = out.qualified.find((x) => x.k === k);
    if (!entry) { entry = { k, effect: e.effect, when: qual, value: combine === 'mult' ? 1 : 0 }; out.qualified.push(entry); }
    entry.value = merge(combine, entry.value, e.value, scale);
    return;
  }
  const bucket = out[scope] || (out[scope] = {});
  bucket[name] = merge(combine, bucket[name], e.value, scale);
}

function merge(combine, cur, v, scale) {
  if (combine === 'or') return Boolean(cur) || Boolean(v);
  if (combine === 'mult') return (cur ?? 1) * (1 + (v - 1) * scale);
  return (cur ?? 0) + v * scale;
}

const r4 = (v) => Math.round(v * 10000) / 10000;
const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

/**
 * Side and hero buffs of one battle. battle = { terrain, winter, coastal, terrainDefenderAdvantage,
 *   attacker: { commander: Character|null, heroes: { key: Character } }, defender: { ... }, context: { attacker: {...}, defender: {...} } }
 * Returns the proposed request additions: <side>.buffs, <side>.tacticsAdd, <side>.outcome, heroes[key].buffs.
 */
export function evaluateBattle(table, battle) {
  const conv = table.conversion;
  const sides = {};
  for (const role of ['attacker', 'defender']) {
    const ctx = { terrain: battle.terrain, winter: battle.winter, coastal: battle.coastal, role, ...(battle.context?.[role] || {}) };
    const cmd = battle[role].commander;
    sides[role] = { ctx, cmd: cmd ? evaluateCharacter(table, cmd, ctx) : null, martial: cmd?.martial ?? 0 };
  }
  const sideVal = (role, name) => sides[role].cmd?.side?.[name];
  const terrainAdv = battle.terrainDefenderAdvantage ?? 0;
  const advAtt = sides.attacker.martial + (sideVal('attacker', 'advantage') ?? 0);
  const advDef = sides.defender.martial + (sideVal('defender', 'advantage') ?? 0)
    + terrainAdv * Math.max(0, 1 + (sideVal('attacker', 'enemyDefenderTerrainAdvantageAdd') ?? 0))
    + (battle.winter === 'harsh' ? 5 : 0);
  const delta = advAtt - advDef;
  const result = { advantage: { attacker: r4(advAtt), defender: r4(advDef), delta: r4(delta) }, heroes: {} };
  for (const role of ['attacker', 'defender']) {
    const enemy = role === 'attacker' ? 'defender' : 'attacker';
    const ahead = role === 'attacker' ? delta > 0 : delta < 0;
    const spread = sideVal(role, 'rollSpread') ?? 0;
    const vol = clamp(1 + conv.combatRoll.perSpreadPoint * spread, 1 - conv.combatRoll.maxVolatility, 1 + conv.combatRoll.maxVolatility);
    const advMult = ahead ? Math.min(1 + conv.advantage.perPoint * Math.abs(delta), 1 + conv.advantage.maxDamageBonus) : 1;
    const morale = ahead ? Math.min(conv.advantage.maxMoraleBonus, conv.advantage.moralePerPoint * Math.abs(delta)) : 0;
    const out = sides[role].cmd?.outcome || {};
    const enemyOut = sides[enemy].cmd?.outcome || {};
    result[role] = {
      buffs: {
        damageDealtMult: r4(advMult * vol),
        damageTakenMult: r4(vol),
        initialMoraleAdd: r4(morale),
        chargeDamageMult: r4(sideVal(role, 'chargeDamageMult') ?? 1),
        troopDeathChanceMult: r4(Math.max(0, 1 + (enemyOut.enemyFatalShareAdd ?? 0))),
      },
      tacticsAdd: r4(sideVal(role, 'tacticsAdd') ?? 0),
      outcome: {
        fatalShareMult: r4(Math.max(0, 1 + (out.ownFatalShareAdd ?? 0)) * Math.max(0, 1 + (enemyOut.enemyFatalShareAdd ?? 0))),
        retreatLossMult: r4(Math.max(0, 1 + (out.ownRetreatLossAdd ?? 0))),
      },
      groupEffects: (sides[role].cmd?.qualified || []).filter((q) => q.effect.startsWith('group.')).map(({ k, ...q }) => ({ ...q, value: r4(q.value) })),
    };
    for (const [key, hero] of Object.entries(battle[role].heroes || {})) {
      const h = evaluateCharacter(table, hero, sides[role].ctx);
      const buffs = {};
      for (const [n, v] of Object.entries(h.hero || {})) buffs[n] = typeof v === 'number' ? r4(v) : v;
      for (const [n, v] of Object.entries(h.retinue || {})) buffs[`retinue${n[0].toUpperCase()}${n.slice(1)}`] = r4(v);
      const q = h.qualified.filter((x) => x.effect.startsWith('hero.')).map(({ k, ...x }) => ({ ...x, value: r4(x.value) }));
      if (q.length) buffs.qualified = q;
      if (buffs.deathChanceMult !== undefined) buffs.deathChance = r4(Math.min(0.9, 0.3 * buffs.deathChanceMult));
      result.heroes[key] = buffs;
    }
  }
  return result;
}
