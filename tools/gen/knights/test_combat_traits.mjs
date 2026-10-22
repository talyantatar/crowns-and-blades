#!/usr/bin/env node
// Self-tests for bridge/data/ck3_combat_traits.json and its generator/evaluator.
//   node tools/gen/knights/test_combat_traits.mjs        (exit 1 on any failure)
// Checks: the table on disk equals a fresh generation from the install; known CK3 values; structural invariants; and
// end-to-end battle evaluations (combat_traits_eval.mjs) against hand-computed expectations.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from './gen_combat_traits.mjs';
import { evaluateBattle, evaluateCharacter } from './combat_traits_eval.mjs';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const FILE = path.join(ROOT, 'bridge/data/ck3_combat_traits.json');
let failed = 0;
let passed = 0;
const ok = (cond, msg) => { if (cond) passed++; else { failed++; console.error(`FAIL ${msg}`); } };
const eq = (a, b, msg) => ok(JSON.stringify(a) === JSON.stringify(b), `${msg}: expected ${JSON.stringify(b)}, got ${JSON.stringify(a)}`);
const near = (a, b, msg) => ok(typeof a === 'number' && Math.abs(a - b) < 1e-4, `${msg}: expected ${b}, got ${a}`); // outputs are rounded to 4 places

const fresh = build();
const disk = JSON.parse(fs.readFileSync(FILE, 'utf8'));
eq(JSON.stringify(disk), JSON.stringify(fresh), 'table on disk is up to date (run gen_combat_traits.mjs)');
const T = disk;

// --- coverage -------------------------------------------------------------------------------------------------------
const commanderKeys = ['logistician', 'military_engineer', 'aggressive_attacker', 'unyielding_defender', 'forder', 'flexible_leader',
  'desert_warrior', 'jungle_stalker', 'reaver', 'reckless', 'holy_warrior', 'open_terrain_expert', 'rough_terrain_expert', 'forest_fighter',
  'cautious_leader', 'organizer'];
for (const k of commanderKeys) ok(T.traits[k]?.category === 'commander', `commander trait ${k} present`);
eq(Object.values(T.traits).filter((r) => r.category === 'commander').length, 16, 'exactly the 16 commander traits of 1.20');
ok(T.traits.winter_soldier?.category === 'winter_commander', 'winter_soldier present');
for (const k of ['brave', 'craven', 'wrathful', 'berserker', 'shieldmaiden', 'strong', 'weak', 'giant', 'dwarf', 'wounded_1', 'wounded_2',
  'wounded_3', 'maimed', 'one_legged', 'blind', 'physique_good_1', 'physique_good_3', 'physique_bad_3', 'lifestyle_blademaster']) {
  ok(T.traits[k], `combat trait ${k} present`);
}
eq(T.emitPlan.traitsNotEmittedToday.includes('brave'), true, 'brave needs a new CX line');
eq(T.emitPlan.traitsNotEmittedToday.includes('giant'), false, 'giant is already emitted (CX|trait)');
eq(T.emitPlan.traitsNotEmittedToday.includes('forest_fighter'), false, 'commander traits are already emitted (CX|ctrait)');

// --- known CK3 values (cumulative tracks) ---------------------------------------------------------------------------
const lv = (k, level, stat, when) => T.traits[k].levels[level].ck3.filter((e) => e.stat === stat && JSON.stringify(e.when || {}) === JSON.stringify(when || {})).reduce((s, e) => s + e.value, 0);
eq([0, 1, 2, 3].map((l) => lv('logistician', l, 'advantage')), [0, 0, 1, 3], 'logistician advantage by level');
eq([0, 1, 2, 3].map((l) => lv('forest_fighter', l, 'advantage', { terrain: ['forest'] })), [2, 4, 6, 8], 'forest_fighter forest advantage by level');
eq([0, 1, 2].map((l) => lv('lifestyle_blademaster', l, 'prowess')), [3, 6, 12], 'blademaster prowess by level');
eq([0, 1, 2, 3].map((l) => lv('winter_soldier', l, 'advantage', { winter: true })), [3, 6, 9, 12], 'winter_soldier winter advantage by level');
eq([0, 1, 2, 3].map((l) => lv('aggressive_attacker', l, 'enemyHardCasualty')), [0.1, 0.2, 0.3, 0.4], 'aggressive_attacker enemy fatal casualties by level');
eq(lv('unyielding_defender', 0, 'hardCasualty'), -0.1, 'unyielding_defender own fatal casualties');
eq([lv('reckless', 0, 'combatRollMin'), lv('reckless', 0, 'combatRollMax')], [-2, 3], 'reckless rolls');
eq([lv('cautious_leader', 0, 'combatRollMin'), lv('cautious_leader', 0, 'combatRollMax')], [2, -1], 'cautious_leader rolls');
eq(lv('flexible_leader', 0, 'enemyTerrainAdvantage'), -0.2, 'flexible_leader counters terrain');
eq(lv('organizer', 0, 'retreatLosses'), -0.1, 'organizer retreat losses');
eq(T.traits.crusader_king.ck3.base.find((e) => e.key === 'retreat_losses')?.value, -0.2, 'crusader_king retreat losses');
eq(T.traits.wounded_3.disablesCombatLeadership, true, 'wounded characters cannot command');

// --- fate factors (combat_phase_events) -----------------------------------------------------------------------------
eq([T.traits.brave.fate.knight_wounded, T.traits.brave.fate.knight_maimed, T.traits.brave.fate.knight_killed], [2, 2, 2], 'brave knight risk x2');
eq([T.traits.craven.fate.commander_wounded, T.traits.craven.fate.commander_killed], [0.5, 0.5], 'craven commander risk x0.5');
eq(T.traits.wounded_3.fate.knight_killed, 4, 'wounded_3 knight death x4');
eq(T.traits.giant.fate.knight_berserker_attack, 10, 'giant berserker attack x10');
eq(T.traits.berserker.fate.requiredBy, ['knight_berserker_attack'], 'berserker attack requires berserker');
eq(T.phaseEvents.knight_killed.base, 30, 'knight_killed base weight');

// --- structure ------------------------------------------------------------------------------------------------------
const terrains = new Set(T.terrains);
for (const [k, r] of Object.entries(T.traits)) {
  const all = [...r.bl.base, ...(r.bl.tracks || []).flatMap((t) => t.steps.flatMap((s) => s.effects))];
  for (const e of all) {
    ok(T.blEffects[e.effect], `${k}: effect ${e.effect} is described in blEffects`);
    ok(Number.isFinite(e.value), `${k}: ${e.effect} has a numeric value`);
    for (const t of e.when?.terrain || []) ok(terrains.has(t), `${k}: terrain ${t} exists`);
    if (e.when?.cultureParameter) ok(T.parameters.culture[e.when.cultureParameter]?.length > 0, `${k}: culture parameter ${e.when.cultureParameter} has a source`);
    if (e.when?.faithParameter) ok(T.parameters.faith[e.when.faithParameter]?.length > 0, `${k}: faith parameter ${e.when.faithParameter} has a source`);
  }
  for (const tr of r.ck3.tracks || []) {
    const xs = tr.steps.map((s) => s.xp);
    ok(xs.every((x, i) => i === 0 || x > xs[i - 1]) && xs.every((x) => x >= 0 && x <= 100), `${k}: track ${tr.name} thresholds ascending in 0..100`);
  }
  ok(/^common\/traits\/[^:]+:\d+$/.test(r.source), `${k}: source is file:line`);
}

// --- end-to-end evaluations -----------------------------------------------------------------------------------------
// 1. Forest, attacker commander forest_fighter L3 (martial 8) vs a plain defender (martial 6): 8 + 8 + 3 = 19 vs 6 + forest 3 = 9.
let r = evaluateBattle(T, {
  terrain: 'forest', winter: 'none', terrainDefenderAdvantage: 3,
  attacker: { commander: { traits: ['forest_fighter'], levels: { forest_fighter: 3 }, martial: 8 } },
  defender: { commander: { traits: [], martial: 6 } },
});
eq(r.advantage, { attacker: 19, defender: 9, delta: 10 }, 'forest: advantage');
near(r.attacker.buffs.damageDealtMult, 1.25, 'forest: attacker deals +25%');
near(r.attacker.buffs.initialMoraleAdd, 10, 'forest: attacker morale +10');
near(r.defender.buffs.damageDealtMult, 1, 'forest: defender unchanged');
near(r.attacker.tacticsAdd, 20, 'forest: forest_fighter L3 Tactics +20');

// 2. Mountains, attacker flexible_leader L0 vs defender (terrain 12): enemy terrain x0.8 -> 9.6; cap at +30%.
r = evaluateBattle(T, {
  terrain: 'mountains', winter: 'none', terrainDefenderAdvantage: 12,
  attacker: { commander: { traits: ['flexible_leader'], martial: 5 } },
  defender: { commander: { traits: [], martial: 5 } },
});
near(r.advantage.defender, 14.6, 'mountains: defender terrain advantage cut by 20%');
near(r.defender.buffs.damageDealtMult, 1 + 0.025 * 9.6, 'mountains: defender ahead by 9.6');
near(r.attacker.tacticsAdd, 15, 'mountains: flexible_leader L0 Tactics +15');

// 3. reckless L3 in the open: expected roll +4 (=(-4+12)/2) and spread +16 -> volatility capped at 1.15.
r = evaluateBattle(T, {
  terrain: 'plains', winter: 'none', terrainDefenderAdvantage: 0,
  attacker: { commander: { traits: ['reckless'], levels: { reckless: 3 }, martial: 5 } },
  defender: { commander: { traits: [], martial: 5 } },
});
near(r.advantage.attacker, 5 + 4 + 3, 'reckless L3: advantage = martial + roll shift + level advantage');
near(r.attacker.buffs.damageTakenMult, 1.15, 'reckless L3: takes +15% (volatility cap)');
near(r.attacker.buffs.damageDealtMult, (1 + 0.025 * 7) * 1.15, 'reckless L3: deals advantage x volatility');
near(r.attacker.buffs.chargeDamageMult, 1.05, 'reckless: charge bonus');
near(r.attacker.tacticsAdd, -10 + 15, 'reckless L3: Tactics -10 base + 3 levels');

// 4. Casualties: aggressive_attacker L1 vs unyielding_defender L0 + organizer.
r = evaluateBattle(T, {
  terrain: 'plains', winter: 'none', terrainDefenderAdvantage: 0,
  attacker: { commander: { traits: ['aggressive_attacker'], levels: { aggressive_attacker: 1 }, martial: 5 } },
  defender: { commander: { traits: ['unyielding_defender', 'organizer'], martial: 5 } },
});
near(r.defender.outcome.fatalShareMult, 0.9 * 1.2, 'casualties: defender fatal share x0.9 (own) x1.2 (enemy aggressive L1)');
near(r.defender.outcome.retreatLossMult, 0.9, 'casualties: organizer retreat losses -10%');
near(r.defender.buffs.troopDeathChanceMult, 1.2, 'casualties: presentation death chance of the defender x1.2');

// 5. Culture parameters gate personality terrain bonuses: brave commander in mountains with/without mountain_trait_bonuses.
const braveIn = (params) => evaluateCharacter(T, { traits: ['brave'], cultureParameters: params }, { terrain: 'mountains', role: 'defender' }).side.advantage ?? 0;
near(braveIn([]), 0, 'brave without Mountaineers tradition: no mountain roll');
near(braveIn(['mountain_trait_bonuses']), 1.5, 'brave with mountain_trait_bonuses: max roll +3 -> +1.5 advantage');

// 6. Winter: winter_soldier L2 in a normal winter, harsh winter adds 5 for the defender.
r = evaluateBattle(T, {
  terrain: 'plains', winter: 'harsh', terrainDefenderAdvantage: 0,
  attacker: { commander: { traits: ['winter_soldier'], levels: { winter_soldier: 2 }, martial: 4 } },
  defender: { commander: { traits: [], martial: 4 } },
});
eq(r.advantage, { attacker: 4 + 9 + 1, defender: 4 + 5, delta: 5 }, 'winter: winter_soldier L2 beats the harsh-winter defender bonus');

// 7. Heroes: brave + wounded_3 knight; berserker; blademaster L2.
r = evaluateBattle(T, {
  terrain: 'plains', winter: 'none', terrainDefenderAdvantage: 0,
  attacker: { commander: null, heroes: { h1: { traits: ['brave', 'wounded_3'] }, h2: { traits: ['berserker', 'giant'] }, h3: { traits: ['lifestyle_blademaster'], levels: { lifestyle_blademaster: 2 } } } },
  defender: { commander: null },
});
near(r.heroes.h1.initialMoraleAdd, 25 - 15, 'hero: brave + wounded_3 morale');
eq(r.heroes.h1.noPanic, true, 'hero: brave never panics');
near(r.heroes.h1.startHealthFraction, 0.5, 'hero: wounded_3 starts at half health');
near(r.heroes.h1.deathChanceMult, 8, 'hero: brave x2 and wounded_3 x4');
near(r.heroes.h1.deathChance, 0.9, 'hero: presentation death chance capped');
near(r.heroes.h1.retinueInitialMoraleAdd, 10, 'hero: brave retinue morale');
const melee = r.heroes.h2.qualified.find((q) => q.effect === 'hero.damageDealtMult' && JSON.stringify(q.when) === '{"weapon":"melee"}');
near(melee?.value, 1.2 * 1.05, 'hero: berserker x giant melee damage');
near(r.heroes.h2.knockdownResistanceAdd, 0.25, 'hero: giant knockdown resistance');
near(r.heroes.h3.swingSpeedMult, 1.02 ** 3, 'hero: blademaster L2 swing speed');

// 8. Owner troops: greatest_of_khans heavy cavalry.
const khan = evaluateCharacter(T, { traits: ['greatest_of_khans'] }, { terrain: 'steppe', role: 'attacker' });
near(khan.qualified.find((q) => q.effect === 'group.damageDealtMult' && q.when.maaBaseType === 'heavy_cavalry')?.value, 1.5, 'owner: khan heavy cavalry damage x1.5');

console.log(`${passed} checks passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
