#!/usr/bin/env node
// Generates bridge/data/ck3_combat_traits.json: every CK3 trait that matters in a field battle, with its exact CK3 effects
// (read from the user's CK3 install, read-only) and the concrete Bannerlord effect each one maps to.
//
//   node tools/gen/knights/gen_combat_traits.mjs           write the table (only when it changed)
//   node tools/gen/knights/gen_combat_traits.mjs --check   write nothing; exit 1 if the table on disk is stale
//   node tools/gen/knights/gen_combat_traits.mjs --stdout  print the table instead of writing it
//
// Install path: env CK3_GAME_DIR (default: found in the Steam libraries, tools/gen/lib/game_dirs.cjs).
// CK3 numbers are VERIFIED (copied from the install). Every Bannerlord number is a
// design choice (INFERRED) and lives in CONVERSION / PERSONAL below, so calibration means editing one place and re-running.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseText, readText, gameDir, listFiles, scriptDocsDir } from '../lib/pdx.mjs';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const OUT = path.join(ROOT, 'bridge/data/ck3_combat_traits.json');
const GAME = gameDir();
const COMMON = path.join(GAME, 'common');
const rel = (f) => path.relative(GAME, f).split(path.sep).join('/');

// ------------------------------------------------------------------------------------------------------------------
// Bannerlord conversion constants (INFERRED design; one place to calibrate)
// ------------------------------------------------------------------------------------------------------------------

const CONVERSION = {
  status: 'INFERRED (design constants; calibrate in play tests). The CK3 inputs are VERIFIED.',
  advantage: {
    rule: 'adv(side) = commander martial + sum of side.advantage effects of the side commander whose `when` holds + terrain defender_advantage (defender only, scaled by the attacker commander\'s side.enemyDefenderTerrainAdvantageAdd: max(0, 1 + sum)) + 5 for the defender in harsh winter; delta = adv(att) - adv(def). The side ahead: every agent deals damage x (1 + perPoint * |delta|), capped at maxDamageBonus, and starts with initial morale + min(maxMoraleBonus, |delta|). Same rule as mapping.json commander_rules.advantage; this table only adds the trait terms.',
    perPoint: 0.025,
    maxDamageBonus: 0.30,
    moralePerPoint: 1,
    maxMoraleBonus: 15,
    ck3Reference: 'CK3 game rule advantage_damage_effect default 5% damage per advantage point (common/game_rules/00_game_rules.txt:1570-1579); halved for Bannerlord because its melee already snowballs.',
  },
  combatRoll: {
    rule: 'CK3 rolls a commander roll in [COMMANDER_MIN_ROLL + min_combat_roll, COMMANDER_MAX_ROLL + max_combat_roll] every COMBAT_ROLL_DAYS (0..10 every 3 days, common/defines/00_defines.txt:584,602-603) and adds it to the advantage. A min/max change d shifts the expected roll by d/2 (-> side.advantage d/2) and the spread by +d (max) / -d (min) (-> side.rollSpread). The spread becomes volatility: every agent of the side deals AND takes damage x clamp(1 + perSpreadPoint * sum(rollSpread), 1 - maxVolatility, 1 + maxVolatility).',
    perSpreadPoint: 0.015,
    maxVolatility: 0.15,
  },
  enemyTerrainAdvantage: 'side.enemyDefenderTerrainAdvantageAdd v (CK3 enemy_terrain_advantage, loc "Counters enemy defensive terrain advantage"): the ENEMY side\'s terrain defender_advantage is multiplied by max(0, 1 + sum(v)) before the delta is formed. Only matters when the holder\'s side attacks.',
  casualties: 'outcome.* effects change the bridge\'s CK3 write-back (bridge/CK3xBL.Mapping/Resolution/CasualtyModel): own fatal share x max(0, 1 + sum(outcome.ownFatalShareAdd)); the enemy side\'s fatal share x max(0, 1 + sum(outcome.enemyFatalShareAdd)); own pursuit (retreat) losses x max(0, 1 + sum(outcome.ownRetreatLossAdd)), on top of the terrain retreat_losses already applied. Presentation in Bannerlord: the enemy side\'s troopDeathChance (killed vs knocked out) is multiplied by the same enemy factor.',
  faithHostility: { rule: 'side.advantage effects with when.faithHostility are scaled by the enemy side commander\'s faith hostility level towards the holder\'s faith (CK3 faith_hostility_level, triggers.log:10110): 0 righteous, 1 astray, 2 hostile, 3 evil.', scale: { 0: 0, 1: 0.5, 2: 1, 3: 1 } },
  tactics: {
    rule: 'side.tacticsAdd is added to the Tactics skill of the side\'s general hero in request.json (heroes[].skills.Tactics, max 300) on top of clamp(10 * martial, 0, 300). In a custom battle Tactics only matters at the gates of MissionCombatantsLogic.EarlyStart (<20 Charge only; >=20 FullScaleAttack + DefensiveEngagement/DefensiveLine or RangedHarrassmentOffensive; >=50 FrontalCavalryCharge + DefensiveRing/HoldChokePoint or CoordinatedRetreat), taken as the max Tactics over the side\'s characters.',
    commanderTraitBase: 5,
    commanderTraitPerLevel: 5,
    extra: { cautious_leader: 10, flexible_leader: 10, organizer: 5, strategist: 15, reckless: -15 },
  },
  ownerTroops: {
    rule: 'group.* effects come from the army OWNER\'s modifiers (CK3 computes a regiment\'s stats with its owner\'s character modifiers) and apply to the agents of that owner\'s groups (levy#<army>, maa:<key>#<army>#<regiment>) whose MaA base type matches when.maaBaseType (or MaA key when.maaType; `all` = every MaA group; levy effects only the levy group). damage_mult m -> damage dealt x (1 + damageShare * m); toughness_mult m -> damage taken x 1 / (1 + toughnessShare * m); *_add values are made relative to the regiment\'s CK3 base stat (ck3_catalog.json maa_types[].damage/toughness, levy 10) first. pursuit/screen go to the bridge\'s pursuit model (outcome) unchanged.',
    damageShare: 0.5,
    toughnessShare: 0.5,
  },
  stacking: 'Within one character: effects of the same name and same `when` add (side.*, outcome.*, *Add) or multiply (*Mult); booleans OR. Across characters: side.* and outcome.* come from the side commander only; hero.* from that hero only; retinue.* from the knight only; group.* from the army owner; liege.* from the knights\' liege.',
};

// Personal (hero agent) effects. CK3 has no agent-level combat, so these are INFERRED translations of the CK3 meaning;
// prowess/martial values of a trait are NOT repeated here (the bridge already reads CK3's final prowess and martial).
// Entry: effects at XP 0 (`base`) and per track step (`steps[xp]`, cumulative like CK3 tracks). Effect = [name, value, when?].
const BLADEMASTER_SKILLS = ['OneHanded', 'TwoHanded', 'Polearm', 'Bow', 'Crossbow', 'Throwing'].map((skill) => ['hero.skillAdd', 20, { skill }]);
const PERSONAL = {
  brave: { why: 'CK3: knights and commanders seek danger (x2 wound/maim/death odds, combat_phase_events); stands his ground.',
    base: [['hero.initialMoraleAdd', 25], ['hero.noPanic', 1], ['retinue.initialMoraleAdd', 10]] },
  craven: { why: 'CK3: x0.5 wound/maim/death odds, never turns berserker; first to run.',
    base: [['hero.initialMoraleAdd', -25], ['retinue.initialMoraleAdd', -10]] },
  wrathful: { why: 'CK3: x2 berserker attack, x5 becoming a berserker; fights in a rage.',
    base: [['hero.damageDealtMult', 1.10, { weapon: 'melee' }], ['hero.damageTakenMult', 1.05]] },
  berserker: { why: 'CK3 knight_berserker_attack: kills an enemy knight of lower prowess (weight 30 vs 2000 for nothing, every COMBAT_EVENT_DAYS).',
    base: [['hero.damageDealtMult', 1.20, { weapon: 'melee' }], ['hero.damageDealtMult', 1.25, { weapon: 'melee', vs: 'hero' }],
      ['hero.damageTakenMult', 0.90], ['hero.noPanic', 1], ['hero.initialMoraleAdd', 50]] },
  shieldmaiden: { why: 'CK3 knight_shieldmaiden_attack: kills an enemy knight of lower prowess, extra glory.',
    base: [['hero.damageDealtMult', 1.10, { weapon: 'melee' }], ['hero.damageDealtMult', 1.20, { weapon: 'melee', vs: 'hero' }],
      ['hero.noPanic', 1], ['hero.initialMoraleAdd', 25]] },
  viking: { why: 'Raiding veteran.', base: [['hero.initialMoraleAdd', 10], ['retinue.initialMoraleAdd', 5]] },
  varangian: { why: 'Guard veteran.', base: [['hero.initialMoraleAdd', 10], ['retinue.initialMoraleAdd', 5]] },
  order_member: { why: 'Holy order brother.', base: [['hero.initialMoraleAdd', 10], ['retinue.initialMoraleAdd', 5]] },
  faith_warrior: { why: 'Holy warrior.', base: [['hero.initialMoraleAdd', 10], ['retinue.initialMoraleAdd', 5]] },
  campeador: { why: 'Champion of battles.', base: [['hero.initialMoraleAdd', 15], ['retinue.initialMoraleAdd', 10]] },
  giant: { why: 'CK3: x10 berserker attack; huge reach and mass (body height is already sent as appearance).',
    base: [['hero.knockdownResistanceAdd', 0.25], ['hero.damageDealtMult', 1.05, { weapon: 'melee' }], ['hero.speedMult', 0.95]] },
  dwarf: { why: 'Small stature.', base: [['hero.knockdownResistanceAdd', -0.10], ['hero.speedMult', 0.90]] },
  strong: { why: 'Raw strength (prowess +4 already counted).', base: [['hero.damageDealtMult', 1.05, { weapon: 'melee' }], ['hero.knockdownResistanceAdd', 0.10]] },
  weak: { why: 'Feeble (prowess -2 already counted).', base: [['hero.damageDealtMult', 0.95, { weapon: 'melee' }], ['hero.knockdownResistanceAdd', -0.10]] },
  physique_good_1: { why: 'Hale.', base: [['hero.knockdownResistanceAdd', 0.05]] },
  physique_good_2: { why: 'Robust.', base: [['hero.knockdownResistanceAdd', 0.10], ['hero.speedMult', 1.02]] },
  physique_good_3: { why: 'Amazonian/Herculean.', base: [['hero.knockdownResistanceAdd', 0.15], ['hero.speedMult', 1.04]] },
  physique_bad_1: { why: 'Delicate.', base: [['hero.knockdownResistanceAdd', -0.05]] },
  physique_bad_2: { why: 'Frail.', base: [['hero.knockdownResistanceAdd', -0.10], ['hero.speedMult', 0.98]] },
  physique_bad_3: { why: 'Feeble.', base: [['hero.knockdownResistanceAdd', -0.15], ['hero.speedMult', 0.96]] },
  athletic: { why: 'Athlete.', base: [['hero.speedMult', 1.05]] },
  spindly: { why: 'Spindly frame.', base: [['hero.knockdownResistanceAdd', -0.05]] },
  hunchbacked: { why: 'Hunched gait.', base: [['hero.speedMult', 0.97]] },
  clubfooted: { why: 'Club foot.', base: [['hero.speedMult', 0.90]] },
  one_legged: { why: 'CK3: x0.25 berserker attack. Peg leg: slow on foot (mounted speed is the horse\'s and stays).',
    base: [['hero.speedMult', 0.75], ['hero.knockdownResistanceAdd', -0.15]] },
  one_eyed: { why: 'CK3: x0.25 berserker attack. No depth perception.', base: [['hero.rangedInaccuracyMult', 1.5]] },
  blind: { why: 'Blind (prowess -10 already counted).', base: [['hero.rangedInaccuracyMult', 4.0], ['hero.speedMult', 0.90]] },
  maimed: { why: 'CK3: maimed knights hang back (knight_none x1.3). Lost use of a limb.',
    base: [['hero.speedMult', 0.90], ['hero.swingSpeedMult', 0.95], ['hero.initialMoraleAdd', -10]] },
  wounded_1: { why: 'CK3: wounded knights hang back (knight_none x1.1). Fights with fresh wounds.', base: [['hero.startHealthFraction', 0.75], ['hero.initialMoraleAdd', -5]] },
  wounded_2: { why: 'CK3: knight_none x1.2.', base: [['hero.startHealthFraction', 0.75], ['hero.initialMoraleAdd', -10]] },
  wounded_3: { why: 'CK3: knight_none x1.3, knight_killed x4.', base: [['hero.startHealthFraction', 0.50], ['hero.initialMoraleAdd', -15]] },
  incapable: { why: 'CK3: cannot command (disables_combat_leadership), prowess x0. Should not be fielded; if he is, barely stands.',
    base: [['hero.startHealthFraction', 0.25], ['hero.speedMult', 0.70]] },
  infirm: { why: 'Old age (tracks degrade).', base: [['hero.speedMult', 0.92], ['hero.swingSpeedMult', 0.95]] },
  fragile_bones: { why: 'Brittle bones.', base: [['hero.knockdownResistanceAdd', -0.20]] },
  clouded_eyes: { why: 'Cataracts.', base: [['hero.rangedInaccuracyMult', 1.3]] },
  faltering_heart: { why: 'Weak heart.', base: [['hero.speedMult', 0.95]] },
  gout_ridden: { why: 'Gout.', base: [['hero.speedMult', 0.90]] },
  pregnant: { why: 'Pregnant.', base: [['hero.speedMult', 0.90]] },
  ill: { why: 'Sick.', base: [['hero.startHealthFraction', 0.90], ['hero.speedMult', 0.95]] },
  pneumonic: { why: 'Pneumonia.', base: [['hero.startHealthFraction', 0.75], ['hero.speedMult', 0.90]] },
  great_pox: { why: 'Great pox.', base: [['hero.startHealthFraction', 0.90]] },
  leper: { why: 'Leprosy.', base: [['hero.startHealthFraction', 0.80], ['hero.speedMult', 0.95]] },
  consumption: { why: 'Consumption.', base: [['hero.startHealthFraction', 0.80], ['hero.speedMult', 0.95]] },
  typhus: { why: 'Typhus.', base: [['hero.startHealthFraction', 0.75], ['hero.speedMult', 0.90]] },
  bubonic_plague: { why: 'Plague.', base: [['hero.startHealthFraction', 0.70], ['hero.speedMult', 0.90]] },
  smallpox: { why: 'Smallpox.', base: [['hero.startHealthFraction', 0.75], ['hero.speedMult', 0.90]] },
  measles: { why: 'Measles.', base: [['hero.startHealthFraction', 0.85], ['hero.speedMult', 0.95]] },
  dysentery: { why: 'Dysentery.', base: [['hero.startHealthFraction', 0.75], ['hero.speedMult', 0.90]] },
  // Each level also trains every weapon skill by 20 (on top of the prowess skill band), so a duelist's craft shows with any weapon.
  lifestyle_blademaster: { why: 'Blademaster levels (prowess +3/+6/+12 already counted): quicker blade work, every weapon skill +20 per level.',
    base: [['hero.swingSpeedMult', 1.02], ...BLADEMASTER_SKILLS],
    steps: { 50: [['hero.swingSpeedMult', 1.02], ...BLADEMASTER_SKILLS], 100: [['hero.swingSpeedMult', 1.02], ...BLADEMASTER_SKILLS] } },
  lifestyle_hunter: { why: 'Hunter path: archery practice.', base: [['hero.skillAdd', 10, { skill: 'Bow' }]],
    tracks: { hunter: { 50: [['hero.skillAdd', 15, { skill: 'Bow' }], ['hero.rangedInaccuracyMult', 0.92]], 100: [['hero.skillAdd', 15, { skill: 'Bow' }], ['hero.rangedInaccuracyMult', 0.92]] } } },
  tourney_participant: { why: 'Tournament paths: foot/horse/bow experience.', base: [],
    tracks: {
      foot: { 30: [['hero.skillAdd', 10, { skill: 'OneHanded' }], ['hero.skillAdd', 10, { skill: 'TwoHanded' }], ['hero.skillAdd', 10, { skill: 'Polearm' }]],
        65: [['hero.skillAdd', 10, { skill: 'OneHanded' }], ['hero.skillAdd', 10, { skill: 'TwoHanded' }], ['hero.skillAdd', 10, { skill: 'Polearm' }]],
        100: [['hero.skillAdd', 10, { skill: 'OneHanded' }], ['hero.skillAdd', 10, { skill: 'TwoHanded' }], ['hero.skillAdd', 10, { skill: 'Polearm' }]] },
      horse: { 30: [['hero.skillAdd', 10, { skill: 'Riding' }], ['hero.chargeDamageMult', 1.05]],
        65: [['hero.skillAdd', 10, { skill: 'Riding' }], ['hero.chargeDamageMult', 1.05]],
        100: [['hero.skillAdd', 10, { skill: 'Riding' }], ['hero.chargeDamageMult', 1.05]] },
      bow: { 30: [['hero.skillAdd', 10, { skill: 'Bow' }], ['hero.skillAdd', 10, { skill: 'Crossbow' }]],
        65: [['hero.skillAdd', 10, { skill: 'Bow' }], ['hero.skillAdd', 10, { skill: 'Crossbow' }]],
        100: [['hero.skillAdd', 10, { skill: 'Bow' }], ['hero.skillAdd', 10, { skill: 'Crossbow' }]] },
    } },
};

// Side-level extras that CK3 does not express as modifiers but that give the trait its Bannerlord feel (INFERRED).
const SIDE_EXTRA = {
  aggressive_attacker: { why: 'Deadlier assaults (CK3 enemy_hard_casualty_modifier); unlocks the Lancer accolade attribute.', base: [['side.chargeDamageMult', 1.10]] },
  open_terrain_expert: { why: 'Cavalry country.', base: [['side.chargeDamageMult', 1.10, { terrain: ['plains', 'farmlands', 'steppe'] }]] },
  reckless: { why: 'Throws the cavalry in.', base: [['side.chargeDamageMult', 1.05]] },
};

// Traits always listed (their combat relevance may be phase events only, or the task named them).
const ALWAYS = [
  'brave', 'craven', 'wrathful', 'calm', 'impatient', 'patient', 'sadistic', 'ambitious', 'content', 'compassionate', 'temperate', 'lazy',
  'berserker', 'shieldmaiden', 'strong', 'weak', 'giant', 'dwarf', 'wounded_1', 'wounded_2', 'wounded_3', 'maimed', 'one_legged',
  'one_eyed', 'disfigured', 'blind', 'physique_good_1', 'physique_good_2', 'physique_good_3', 'physique_bad_1', 'physique_bad_2',
  'physique_bad_3', 'lifestyle_blademaster', 'lifestyle_hunter', 'tourney_participant', 'athletic', 'incapable',
];

// ------------------------------------------------------------------------------------------------------------------
// Install readers
// ------------------------------------------------------------------------------------------------------------------

function parseFile(file) {
  const errors = [];
  const nodes = parseText(readText(file), errors);
  if (errors.length) throw new Error(`${rel(file)}: ${errors.slice(0, 3).map((e) => `line ${e.line}: ${e.message}`).join('; ')}`);
  return nodes;
}

function scriptValueNumbers() {
  const out = new Map();
  for (const f of listFiles(path.join(COMMON, 'script_values'), ['.txt'])) {
    for (const n of parseFile(f)) if (n.key && n.value !== undefined && /^-?\d+(\.\d+)?$/.test(n.value)) out.set(n.key, Number(n.value));
  }
  return out;
}

function terrainKeys() {
  const keys = new Set();
  for (const f of listFiles(path.join(COMMON, 'terrain_types'), ['.txt'])) for (const n of parseFile(f)) if (n.key && n.block) keys.add(n.key);
  return [...keys].sort((a, b) => b.length - a.length || a.localeCompare(b)); // longest first: desert_mountains before desert
}

function maaKeys() {
  const types = new Set();
  const bases = new Set();
  for (const f of listFiles(path.join(COMMON, 'men_at_arms_types'), ['.txt'])) {
    for (const n of parseFile(f)) {
      if (!n.key || !n.block || n.key.startsWith('@')) continue;
      types.add(n.key);
      const t = n.block.find((c) => c.key === 'type');
      if (t && t.value) bases.add(t.value);
    }
  }
  return { types, bases };
}

/** parameter -> [{ source, file:line }] for culture traditions and faith tenets/doctrines that grant it. */
function parameterSources() {
  const culture = new Map();
  const faith = new Map();
  const add = (map, p, src) => { if (!map.has(p)) map.set(p, []); map.get(p).push(src); };
  for (const f of listFiles(path.join(COMMON, 'culture', 'traditions'), ['.txt'])) {
    for (const n of parseFile(f)) {
      const params = n.block?.find((c) => c.key === 'parameters');
      for (const p of params?.block || []) if (p.key && p.value === 'yes') add(culture, p.key, `${n.key} (${rel(f)}:${p.line})`);
    }
  }
  for (const f of listFiles(path.join(COMMON, 'religion'), ['.txt'], true)) {
    // `parameters = { ... }` belongs to the block that contains it (a tenet or a doctrine); entries are `p = yes` or bare `p`.
    const visit = (nodes, owner) => {
      for (const n of nodes) {
        if (!n.block) continue;
        if (n.key === 'parameters') {
          for (const p of n.block) {
            if (p.key && p.value === 'yes') add(faith, p.key, `${owner} (${rel(f)}:${p.line})`);
            else if (!p.key && p.value) add(faith, p.value, `${owner} (${rel(f)}:${p.line})`);
          }
        } else visit(n.block, n.key ?? owner);
      }
    };
    visit(parseFile(f), null);
  }
  return { culture, faith };
}

/** Knight/commander combat phase events: per event its type, base weight, requirements and trait factors. */
function phaseEvents() {
  const out = {};
  for (const f of listFiles(path.join(COMMON, 'combat_phase_events'), ['.txt'])) {
    for (const ev of parseFile(f)) {
      if (!ev.key || !ev.block) continue;
      const type = ev.block.find((c) => c.key === 'type')?.value;
      const chance = ev.block.find((c) => c.key === 'chance');
      const rec = { type, source: `${rel(f)}:${ev.line}`, base: null, factors: {}, requires: [], excludes: [] };
      for (const c of chance?.block || []) {
        if (c.key === 'base') rec.base = Number(c.value);
        if (c.key !== 'modifier' || !c.block) continue;
        const factor = c.block.find((x) => x.key === 'factor');
        if (!factor || factor.value === undefined || !/^-?\d+(\.\d+)?$/.test(factor.value)) continue;
        const conds = c.block.filter((x) => x.key !== 'factor');
        const traits = traitsOfConditions(conds);
        if (traits && traits.length) for (const t of traits) rec.factors[t] = Number(factor.value);
      }
      const valid = ev.block.find((c) => c.key === 'is_valid');
      for (const c of valid?.block || []) {
        if (c.key === 'has_trait') rec.requires.push(c.value);
        if ((c.key === 'NOR' || c.key === 'NAND') && c.block) for (const x of c.block) if (x.key === 'has_trait') rec.excludes.push(`${c.key}:${x.value}`);
      }
      out[ev.key] = rec;
    }
  }
  return out;
}

/** Trait keys named by a modifier's conditions: a lone has_trait / has_trait_rank (wounded rank N -> wounded_N) or an OR of them. */
function traitsOfConditions(conds) {
  if (conds.length !== 1) return null;
  const c = conds[0];
  if (c.key === 'has_trait') return [c.value];
  if (c.key === 'has_trait_rank' && c.block) {
    const trait = c.block.find((x) => x.key === 'trait')?.value;
    const rank = c.block.find((x) => x.key === 'rank');
    if (trait === 'wounded' && rank && rank.op === '=') return [`wounded_${rank.value}`];
    return null;
  }
  if (c.key === 'OR' && c.block) {
    const all = [];
    for (const x of c.block) {
      const t = traitsOfConditions([x]);
      if (!t) return null;
      all.push(...t);
    }
    return all;
  }
  return null;
}

// ------------------------------------------------------------------------------------------------------------------
// Modifier classification (names VERIFIED in the script_docs modifiers.log)
// ------------------------------------------------------------------------------------------------------------------

function makeClassifier(terrains, maa) {
  const exact = {
    advantage: { stat: 'advantage' },
    attacker_advantage: { stat: 'advantage', when: { role: 'attacker' } },
    defender_advantage: { stat: 'advantage', when: { role: 'defender' } },
    winter_advantage: { stat: 'advantage', when: { winter: true } },
    defender_winter_advantage: { stat: 'advantage', when: { winter: true, role: 'defender' } },
    coastal_advantage: { stat: 'advantage', when: { coastal: true } },
    uncontrolled_province_advantage: { stat: 'advantage', when: { hostileTerritory: true, role: 'attacker' } },
    controlled_province_advantage: { stat: 'advantage', when: { ownTerritory: true, role: 'defender' } },
    led_by_owner_extra_advantage_add: { stat: 'advantage', when: { ownArmy: true } },
    same_heritage_county_advantage_add: { stat: 'advantage', when: { sameHeritageCounty: true } },
    independent_primary_attacker_advantage_add: { stat: 'advantage', when: { independentPrimary: true, role: 'attacker' } },
    independent_primary_defender_advantage_add: { stat: 'advantage', when: { independentPrimary: true, role: 'defender' } },
    advantage_against_coreligionists: { stat: 'advantage', when: { sameFaith: true } },
    tolerance_advantage_mod: { stat: 'advantage', when: { faithHostility: true } },
    enemy_terrain_advantage: { stat: 'enemyTerrainAdvantage' },
    max_combat_roll: { stat: 'combatRollMax' },
    min_combat_roll: { stat: 'combatRollMin' },
    hard_casualty_modifier: { stat: 'hardCasualty' },
    hard_casualty_winter: { stat: 'hardCasualty', when: { winter: true } },
    enemy_hard_casualty_modifier: { stat: 'enemyHardCasualty' },
    retreat_losses: { stat: 'retreatLosses' },
    prowess: { stat: 'prowess' },
    prowess_no_portrait: { stat: 'prowess' },
    prowess_mult: { stat: 'prowessMult' },
    martial: { stat: 'martial' },
    martial_mult: { stat: 'martialMult' },
    negate_prowess_penalty_add: { stat: 'negateProwessPenalty' },
    knight_effectiveness_mult: { stat: 'knightEffectivenessMult' },
    knight_limit: { stat: 'knightLimit' },
    army_damage_mult: { stat: 'troopDamageMult', when: { troops: 'army' } },
    army_toughness_mult: { stat: 'troopToughnessMult', when: { troops: 'army' } },
    army_pursuit_mult: { stat: 'troopPursuitMult', when: { troops: 'army' } },
    army_screen_mult: { stat: 'troopScreenMult', when: { troops: 'army' } },
    levy_attack: { stat: 'troopDamageAdd', when: { troops: 'levy' } },
    levy_toughness: { stat: 'troopToughnessAdd', when: { troops: 'levy' } },
    levy_pursuit: { stat: 'troopPursuitAdd', when: { troops: 'levy' } },
    levy_screen: { stat: 'troopScreenAdd', when: { troops: 'levy' } },
  };
  const statWord = { damage: 'Damage', toughness: 'Toughness', pursuit: 'Pursuit', screen: 'Screen' };
  return function classify(key) {
    if (exact[key]) return { ...exact[key] };
    let m = key.match(/^(prowess|martial)_per_([a-z_]+)_level$/);
    if (m) return { stat: `${m[1]}PerLevel`, when: { per: m[2] } };
    m = key.match(/^knight_effectiveness_per_([a-z]+)$/);
    if (m) return { stat: 'knightEffectivenessPer', when: { per: m[1] } };
    for (const t of terrains) {
      if (key === `${t}_advantage`) return { stat: 'advantage', when: { terrain: [t] } };
      if (key === `${t}_max_combat_roll`) return { stat: 'combatRollMax', when: { terrain: [t] } };
      if (key === `${t}_min_combat_roll`) return { stat: 'combatRollMin', when: { terrain: [t] } };
    }
    m = key.match(/^(stationed_)?([a-z_]+?)_(damage|toughness|pursuit|screen)_(add|mult)$/);
    if (m && !m[1]) {
      const who = m[2];
      const stat = `troop${statWord[m[3]]}${m[4] === 'add' ? 'Add' : 'Mult'}`;
      if (who === 'maa') return { stat, when: { troops: 'maa', maaBaseType: 'all' } };
      if (maa.bases.has(who)) return { stat, when: { troops: 'maa', maaBaseType: who } };
      if (maa.types.has(who)) return { stat, when: { troops: 'maa', maaType: who } };
    }
    return null;
  };
}

// ------------------------------------------------------------------------------------------------------------------
// Trait reading
// ------------------------------------------------------------------------------------------------------------------

const NON_MODIFIER_BLOCKS = new Set(['name', 'desc', 'icon', 'triggered_opinion', 'compatibility', 'opposites', 'potential',
  'genetic_constraint_all', 'genetic_constraint_men', 'genetic_constraint_women', 'valid_sex', 'monthly_track_xp_degradation',
  'culture_succession_prio', 'portrait_extremity_shift', 'trigger']);

function numberValue(v, consts) {
  if (v === undefined) return NaN;
  if (/^-?\d+(\.\d+)?$/.test(v)) return Number(v);
  if (consts.has(v)) return consts.get(v);
  return NaN;
}

/** Splits a modifier block into plain modifiers and parameter-gated ones. */
function readModifierBlock(block, consts, classify, ctx) {
  const effects = [];
  const nonBattle = new Set();
  for (const n of block) {
    if (!n.key) continue;
    if (n.block) {
      if (n.key === 'culture_modifier' || n.key === 'rite_modifier' || n.key === 'faith_modifier') {
        const param = n.block.find((c) => c.key === 'parameter')?.value;
        const gate = n.key === 'culture_modifier' ? { cultureParameter: param } : { faithParameter: param };
        const inner = readModifierBlock(n.block.filter((c) => c.key !== 'parameter'), consts, classify, ctx);
        for (const e of inner.effects) effects.push({ ...e, when: { ...e.when, ...gate } });
        for (const k of inner.nonBattle) nonBattle.add(k);
      }
      continue;
    }
    if (n.key === 'parameter') continue;
    const value = numberValue(n.value, consts);
    if (Number.isNaN(value)) continue; // flags, categories, names
    const c = classify(n.key);
    if (!c) { if (ctx.isModifier(n.key)) nonBattle.add(n.key); continue; }
    const e = { key: n.key, stat: c.stat, value };
    if (c.when) e.when = c.when;
    effects.push(e);
  }
  return { effects, nonBattle };
}

const TRAIT_FIELDS = new Set(['category', 'group', 'level', 'flag', 'genetic', 'physical', 'good', 'disables_combat_leadership',
  'shown_in_ruler_designer', 'ruler_designer_cost', 'inherit_chance', 'birth', 'random_creation', 'random_creation_weight',
  'minimum_age', 'maximum_age', 'health', 'fertility', 'same_opinion', 'opposite_opinion', 'can_have_children', 'enables_inbred',
  'immortal', 'incapacitating', 'can_inherit', 'inherit_from_real_father', 'parent_inheritance_sex', 'child_inheritance_sex',
  'both_parent_has_trait_inherit_chance', 'blocks_from_claim_inheritance', 'can_not_marry', 'can_be_taken_from_dynasty',
  'bastard', 'is_bastard', 'shown_in_encyclopedia', 'trait_exclusive_if_realm_contains', 'name', 'desc', 'icon']);

function readTrait(t, consts, classify, ctx) {
  const block = t.block;
  const field = (k) => block.find((c) => c.key === k)?.value;
  const flags = block.filter((c) => c.key === 'flag').map((c) => c.value);
  const plain = block.filter((c) => !c.block || c.key === 'culture_modifier' || c.key === 'rite_modifier' || c.key === 'faith_modifier');
  const base = readModifierBlock(plain.filter((c) => !TRAIT_FIELDS.has(c.key)), consts, classify, ctx);
  const nonBattle = new Set(base.nonBattle);
  const tracks = [];
  const readSteps = (trackBlock) => {
    const steps = [];
    for (const s of trackBlock) {
      if (!s.key || !s.block) continue;
      const xp = numberValue(s.key, consts);
      if (Number.isNaN(xp)) throw new Error(`${t.key}: unresolved track threshold '${s.key}'`);
      const r = readModifierBlock(s.block, consts, classify, ctx);
      for (const k of r.nonBattle) nonBattle.add(k);
      steps.push({ xp, effects: r.effects });
    }
    steps.sort((a, b) => a.xp - b.xp);
    return steps;
  };
  const single = block.find((c) => c.key === 'track' && c.block);
  if (single) tracks.push({ name: t.key, implicit: true, steps: readSteps(single.block) });
  const multi = block.find((c) => c.key === 'tracks' && c.block);
  for (const tr of multi?.block || []) if (tr.key && tr.block) tracks.push({ name: tr.key, implicit: false, steps: readSteps(tr.block) });
  // has_trait_xp takes a track name only when the trait has several tracks (triggers.log:4788), so a lone named track is implicit.
  if (tracks.length === 1) tracks[0].implicit = true;
  return {
    key: t.key, line: t.line,
    category: field('category') ?? null, group: field('group') ?? null, level: field('level') !== undefined ? Number(field('level')) : null,
    flags, disablesCombatLeadership: field('disables_combat_leadership') === 'yes',
    base: base.effects, tracks, nonBattle: [...nonBattle].sort(),
  };
}

// ------------------------------------------------------------------------------------------------------------------
// Bannerlord mapping
// ------------------------------------------------------------------------------------------------------------------

const ALREADY_IN_SNAPSHOT = new Set(['prowess', 'prowessMult', 'prowessPerLevel', 'martial', 'martialMult', 'martialPerLevel', 'negateProwessPenalty']);

/** CK3 effect -> Bannerlord effects (empty when the value is already in the snapshot or has no battle meaning). */
function blOfCk3(e) {
  const when = e.when ? { ...e.when } : undefined;
  const w = (extra) => { const o = { ...(when || {}), ...(extra || {}) }; return Object.keys(o).length ? o : undefined; };
  const fx = (effect, value, extraWhen) => { const o = { effect, value: round(value) }; const ww = w(extraWhen); if (ww) o.when = ww; o.from = e.key; return o; };
  switch (e.stat) {
    case 'advantage': return [fx('side.advantage', e.value)];
    case 'combatRollMax': return [fx('side.advantage', e.value / 2), fx('side.rollSpread', e.value)];
    case 'combatRollMin': return [fx('side.advantage', e.value / 2), fx('side.rollSpread', -e.value)];
    case 'enemyTerrainAdvantage': return [fx('side.enemyDefenderTerrainAdvantageAdd', e.value)];
    case 'hardCasualty': return [fx('outcome.ownFatalShareAdd', e.value)];
    case 'enemyHardCasualty': return [fx('outcome.enemyFatalShareAdd', e.value)];
    case 'retreatLosses': return [fx('outcome.ownRetreatLossAdd', e.value)];
    case 'knightEffectivenessMult': return [fx('liege.knightEffectivenessAdd', e.value)];
    case 'knightEffectivenessPer': return [fx('liege.knightEffectivenessPerSkillPoint', e.value)];
    case 'knightLimit': return [];
    case 'troopDamageMult': return [fx('group.damageDealtMult', 1 + CONVERSION.ownerTroops.damageShare * e.value)];
    case 'troopToughnessMult': return [fx('group.damageTakenMult', 1 / (1 + CONVERSION.ownerTroops.toughnessShare * e.value))];
    case 'troopDamageAdd': return [fx('group.ck3DamageAdd', e.value)];
    case 'troopToughnessAdd': return [fx('group.ck3ToughnessAdd', e.value)];
    case 'troopPursuitAdd': case 'troopPursuitMult': return [fx(`outcome.pursuit${e.stat.endsWith('Add') ? 'Add' : 'Mult'}`, e.value)];
    case 'troopScreenAdd': case 'troopScreenMult': return [fx(`outcome.screen${e.stat.endsWith('Add') ? 'Add' : 'Mult'}`, e.value)];
    default: return [];
  }
}

function round(v) { return Math.round(v * 10000) / 10000; }

function personalEffects(list) {
  return (list || []).map(([effect, value, when]) => { const o = { effect, value }; if (when) o.when = when; o.from = 'design'; return o; });
}

/** Effect names with their engine hook and how values combine. */
const BL_EFFECTS = {
  'side.advantage': { combine: 'add', scope: 'side commander', hook: 'bridge: advantage delta (CONVERSION.advantage) -> side.damageDealtMult + side.initialMoraleAdd in request.<side>.buffs' },
  'side.rollSpread': { combine: 'add', scope: 'side commander', hook: 'bridge: CONVERSION.combatRoll -> side.damageDealtMult and side.damageTakenMult' },
  'side.enemyDefenderTerrainAdvantageAdd': { combine: 'add', scope: 'side commander', hook: 'bridge: scales the enemy\'s terrain defender_advantage in the delta' },
  'side.tacticsAdd': { combine: 'add', scope: 'side commander', hook: 'bridge: request heroes[general].skills.Tactics (MissionCombatantsLogic.EarlyStart gates 20/50)' },
  'side.chargeDamageMult': { combine: 'mult', scope: 'side commander', hook: 'module: CK3AgentApplyDamageModel.ApplyGeneralDamageModifiers when collisionData.IsHorseCharge or the attacker rides and strikes in melee' },
  'outcome.ownFatalShareAdd': { combine: 'add', scope: 'side commander', hook: 'bridge: CasualtyModel fatal share of the holder\'s side' },
  'outcome.enemyFatalShareAdd': { combine: 'add', scope: 'side commander', hook: 'bridge: CasualtyModel fatal share of the enemy side; module: enemy side troopDeathChance (presentation)' },
  'outcome.ownRetreatLossAdd': { combine: 'add', scope: 'side commander', hook: 'bridge: CasualtyModel pursuit losses of the holder\'s side' },
  'outcome.pursuitAdd': { combine: 'add', scope: 'army owner', hook: 'bridge: pursuit of the owner\'s matching regiments' },
  'outcome.pursuitMult': { combine: 'add', scope: 'army owner', hook: 'bridge: pursuit x (1 + sum)' },
  'outcome.screenAdd': { combine: 'add', scope: 'army owner', hook: 'bridge: screen of the owner\'s matching regiments' },
  'outcome.screenMult': { combine: 'add', scope: 'army owner', hook: 'bridge: screen x (1 + sum)' },
  'group.damageDealtMult': { combine: 'mult', scope: 'army owner', hook: 'module: ApplyGeneralDamageModifiers, attacker agent (rider for a mount) in a matching group' },
  'group.damageTakenMult': { combine: 'mult', scope: 'army owner', hook: 'module: ApplyGeneralDamageModifiers, victim agent in a matching group' },
  'group.ck3DamageAdd': { combine: 'add', scope: 'army owner', hook: 'bridge: -> group.damageDealtMult via the regiment\'s CK3 base damage (CONVERSION.ownerTroops)' },
  'group.ck3ToughnessAdd': { combine: 'add', scope: 'army owner', hook: 'bridge: -> group.damageTakenMult via the regiment\'s CK3 base toughness' },
  'liege.knightEffectivenessAdd': { combine: 'add', scope: 'liege of the knights', hook: 'bridge: knight effectiveness (ck3_knight_effectiveness_sources.json) -> knight health x damage multiplier' },
  'liege.knightEffectivenessPerSkillPoint': { combine: 'add', scope: 'liege of the knights', hook: 'bridge: x the liege\'s skill named in when.per' },
  'hero.initialMoraleAdd': { combine: 'add', scope: 'hero', hook: 'module: CK3BattleMoraleModel.GetEffectiveInitialMorale (caller clamps 15..100)' },
  'hero.noPanic': { combine: 'or', scope: 'hero', hook: 'module: CK3BattleMoraleModel.CanPanicDueToMorale -> false' },
  'hero.damageDealtMult': { combine: 'mult', scope: 'hero', hook: 'module: ApplyGeneralDamageModifiers, attacker = hero (or his mount); when.weapon melee|ranged, when.vs hero' },
  'hero.damageTakenMult': { combine: 'mult', scope: 'hero', hook: 'module: ApplyGeneralDamageModifiers, victim = hero' },
  'hero.chargeDamageMult': { combine: 'mult', scope: 'hero', hook: 'module: ApplyGeneralDamageModifiers, horse charge or mounted melee by the hero' },
  'hero.startHealthFraction': { combine: 'mult', scope: 'hero', hook: 'module: MissionLogic.OnAgentBuild, Health = max(1, HealthLimit x fraction) after the healthMultiplier' },
  'hero.speedMult': { combine: 'mult', scope: 'hero', hook: 'module: CK3AgentStatCalculateModel.UpdateAgentStats, MaxSpeedMultiplier and CombatMaxSpeedMultiplier (on foot)' },
  'hero.swingSpeedMult': { combine: 'mult', scope: 'hero', hook: 'module: UpdateAgentStats, SwingSpeedMultiplier and ThrustOrRangedReadySpeedMultiplier' },
  'hero.rangedInaccuracyMult': { combine: 'mult', scope: 'hero', hook: 'module: CK3AgentStatCalculateModel.GetWeaponInaccuracy for ranged weapons' },
  'hero.knockdownResistanceAdd': { combine: 'add', scope: 'hero', hook: 'module: GetKnockDownResistance, GetKnockBackResistance (and half to GetDismountResistance)' },
  'hero.skillAdd': { combine: 'add', scope: 'hero', hook: 'module: CK3AgentStatCalculateModel.GetEffectiveSkill (when.skill); flows into weapon damage, speed and AI level' },
  'hero.deathChanceMult': { combine: 'mult', scope: 'hero', hook: 'bridge: heroes[].deathChance = 0.3 x mult (cap 0.9; presentation of killed vs knocked out). CK3 fates use `fate` below' },
  'retinue.initialMoraleAdd': { combine: 'add', scope: 'knight', hook: 'module: OnAgentBuild, SetMorale(GetMorale() + v) for agents of group knight-retinue#<tag>' },
};

// ------------------------------------------------------------------------------------------------------------------
// Build
// ------------------------------------------------------------------------------------------------------------------

function build() {
  const traitsDir = path.join(COMMON, 'traits');
  const files = listFiles(traitsDir, ['.txt']);
  const sv = scriptValueNumbers();
  const terrains = terrainKeys();
  const maa = maaKeys();
  const modifierTags = loadModifierTags();
  const classify = makeClassifier(terrains, maa);
  const ctx = { isModifier: (k) => modifierTags(k) };
  const params = parameterSources();
  const events = phaseEvents();
  const emitted = existingEmits();

  const raw = [];
  for (const f of files) {
    const nodes = parseFile(f);
    const consts = new Map(sv);
    for (const n of nodes) if (n.key && n.key.startsWith('@') && /^-?\d+(\.\d+)?$/.test(n.value ?? '')) consts.set(n.key, Number(n.value));
    for (const n of nodes) if (n.key && n.block && !n.key.startsWith('@')) raw.push({ ...readTrait(n, consts, classify, ctx), file: rel(f) });
  }
  const byKey = new Map(raw.map((t) => [t.key, t]));
  for (const k of [...ALWAYS, ...Object.keys(PERSONAL), ...Object.keys(SIDE_EXTRA), ...Object.keys(CONVERSION.tactics.extra)]) {
    if (!byKey.has(k)) throw new Error(`trait ${k} is not in the install`);
  }
  // Hand-authored steps must sit on real track thresholds of the install.
  for (const [k, p] of Object.entries(PERSONAL)) {
    const t = byKey.get(k);
    const check = (trackName, steps) => {
      const tr = t.tracks.find((x) => (trackName ? x.name === trackName : x.implicit));
      if (!tr) throw new Error(`PERSONAL.${k}: no track ${trackName ?? '(implicit)'} in the install`);
      for (const xp of Object.keys(steps)) if (!tr.steps.some((s) => s.xp === Number(xp))) throw new Error(`PERSONAL.${k}: track ${tr.name} has no step at XP ${xp}`);
    };
    if (p.steps) check(null, p.steps);
    for (const [name, steps] of Object.entries(p.tracks || {})) check(name, steps);
  }

  // fate factors per trait
  const fate = {};
  for (const [ev, rec] of Object.entries(events)) {
    for (const [trait, factor] of Object.entries(rec.factors)) {
      (fate[trait] ||= {})[ev] = factor;
    }
    for (const t of rec.requires) ((fate[t] ||= {}).requiredBy ||= []).push(ev);
  }

  const out = {};
  const usedCultureParams = new Set();
  const usedFaithParams = new Set();
  for (const t of raw) {
    const allEffects = [...t.base, ...t.tracks.flatMap((tr) => tr.steps.flatMap((s) => s.effects))];
    const isCommander = t.category === 'commander' || t.category === 'winter_commander';
    const relevant = isCommander || allEffects.length > 0 || ALWAYS.includes(t.key) || fate[t.key] || PERSONAL[t.key];
    if (!relevant) continue;

    const blBase = [];
    const tacticsExtra = CONVERSION.tactics.extra[t.key] ?? 0;
    if (isCommander) blBase.push({ effect: 'side.tacticsAdd', value: CONVERSION.tactics.commanderTraitBase + tacticsExtra, from: 'design' });
    else if (tacticsExtra) blBase.push({ effect: 'side.tacticsAdd', value: tacticsExtra, from: 'design' });
    for (const e of t.base) blBase.push(...blOfCk3(e));
    if (SIDE_EXTRA[t.key]) blBase.push(...personalEffects(SIDE_EXTRA[t.key].base));
    const p = PERSONAL[t.key];
    if (p) blBase.push(...personalEffects(p.base));
    const killed = fate[t.key]?.knight_killed;
    if (killed && killed !== 1) blBase.push({ effect: 'hero.deathChanceMult', value: killed, from: 'combat_phase_events knight_killed' });

    const tracks = t.tracks.map((tr) => ({
      name: tr.name,
      ...(tr.implicit ? { implicit: true } : {}),
      steps: tr.steps.map((s) => {
        const bl = [];
        if (isCommander && tr.implicit) bl.push({ effect: 'side.tacticsAdd', value: CONVERSION.tactics.commanderTraitPerLevel, from: 'design' });
        for (const e of s.effects) bl.push(...blOfCk3(e));
        const ps = p?.steps?.[s.xp] || p?.tracks?.[tr.name]?.[s.xp];
        if (ps) bl.push(...personalEffects(ps));
        return { xp: s.xp, ck3: s.effects, bl };
      }),
    }));
    const blAll = [...blBase, ...tracks.flatMap((tr) => tr.steps.flatMap((s) => s.bl))];
    // Parameters matter only where they gate an effect with a Bannerlord meaning (prowess/martial ones are already counted).
    for (const e of blAll) {
      if (e.when?.cultureParameter) usedCultureParams.add(e.when.cultureParameter);
      if (e.when?.faithParameter) usedFaithParams.add(e.when.faithParameter);
    }
    const roles = new Set();
    for (const b of blAll) {
      const scope = b.effect.split('.')[0];
      roles.add({ side: 'commander', outcome: 'commander', hero: 'hero', retinue: 'hero', group: 'armyOwner', liege: 'liege' }[scope]);
    }
    if (fate[t.key]) roles.add('fate');
    const alreadyCounted = [...new Set(allEffects.filter((e) => ALREADY_IN_SNAPSHOT.has(e.stat)).map((e) => e.stat.startsWith('prowess') || e.stat === 'negateProwessPenalty' ? 'prowess' : 'martial'))].sort();

    const rec = {
      category: t.category,
      source: `${t.file}:${t.line}`,
    };
    if (t.group) rec.group = t.group;
    if (t.level !== null) rec.level = t.level;
    if (t.flags.length) rec.flags = t.flags;
    if (t.disablesCombatLeadership) rec.disablesCombatLeadership = true;
    rec.roles = [...roles].filter(Boolean).sort();
    if (alreadyCounted.length) rec.alreadyCounted = alreadyCounted;
    rec.ck3 = { base: t.base };
    if (tracks.length) rec.ck3.tracks = tracks.map((tr) => ({ name: tr.name, ...(tr.implicit ? { implicit: true } : {}), steps: tr.steps.map((s) => ({ xp: s.xp, effects: s.ck3 })) }));
    if (t.nonBattle.length) rec.ck3.nonBattle = t.nonBattle;
    rec.bl = { base: blBase };
    if (tracks.length) rec.bl.tracks = tracks.map((tr) => ({ name: tr.name, steps: tr.steps.map((s) => ({ xp: s.xp, effects: s.bl })) }));
    if (p?.why || SIDE_EXTRA[t.key]?.why) rec.bl.why = [p?.why, SIDE_EXTRA[t.key]?.why].filter(Boolean).join(' ');
    if (fate[t.key]) rec.fate = fate[t.key];
    if (tracks.length === 1 && tracks[0].implicit) rec.levels = cumulativeLevels(t, tracks[0]);
    rec.emit = emitFor(t, tracks, emitted, blAll);
    out[t.key] = rec;
  }

  const sortedTraits = Object.fromEntries(Object.entries(out).sort(([a, ra], [b, rb]) => order(ra) - order(rb) || a.localeCompare(b)));
  const table = {
    meta: {
      generator: 'tools/gen/knights/gen_combat_traits.mjs',
      ck3Version: ck3Version(),
      sources: [...files.map(rel), 'common/combat_phase_events/*.txt', 'common/script_values/*.txt (track thresholds)',
        'common/terrain_types/*.txt', 'common/men_at_arms_types/*.txt', 'common/culture/traditions/*.txt', 'common/religion/**/*.txt'],
      status: 'ck3.*, fate, levels and parameters are VERIFIED copies of the install. bl.* effects are INFERRED design (CONVERSION, PERSONAL, SIDE_EXTRA in the generator).',
      trackSemantics: 'Track steps are cumulative: at XP x a character has the base modifiers plus every step with xp <= x (INFERRED from the data: lifestyle_blademaster negate_health_penalty_add 0.25+0.25+0.5 = 1.0 exactly; the_wake wetlands_advantage +2/+3/+1 would otherwise fall at higher levels; gallowsbait, charioteer_* and clouded_eyes repeat identical steps that would be pointless otherwise). To confirm in game: a level-3 forest_fighter shows +8 Forest Advantage and +3 Advantage in the trait tooltip.',
      levelNames: 'Commander traits: level 0 = XP 0..32 (trait_<key>), 1 = 33..65 (trait_<key>_1), 2 = 66..99, 3 = 100 (localization/english/traits_l_english.yml).',
      prowessAndMartial: 'prowess*/martial* values are part of CK3\'s final prowess and martial, which the snapshot already carries (C record ck3xbl_c_prowess / ck3xbl_c_martial). They are listed in ck3 for completeness and never re-applied (alreadyCounted).',
      commanderOnly: 'CK3 applies advantage, combat-roll, casualty and retreat modifiers only for the combat side\'s commander (loc icon command_modifier_i). Knights get nothing from them. Characters with disables_combat_leadership (incapable, infirm, wounded_1..3) never command.',
    },
    terrains: [...terrains].sort(),
    conversion: CONVERSION,
    blEffects: BL_EFFECTS,
    parameters: {
      culture: Object.fromEntries([...usedCultureParams].sort().map((p) => [p, params.culture.get(p) || []])),
      faith: Object.fromEntries([...usedFaithParams].sort().map((p) => [p, params.faith.get(p) || []])),
    },
    phaseEvents: events,
    emitPlan: emitPlan(out, usedCultureParams, usedFaithParams),
    traits: sortedTraits,
  };
  return table;
}

function order(r) {
  return ({ commander: 0, winter_commander: 1, lifestyle: 2, fame: 3, personality: 4, education: 5, health: 6, childhood: 7, court_type: 8 })[r.category] ?? 9;
}

/** Totals per level for single-track traits: { level, minXp, ck3: [{stat, when, value}] }. */
function cumulativeLevels(t, track) {
  const levels = [];
  const acc = new Map();
  const addAll = (effects) => {
    for (const e of effects) {
      const k = `${e.stat}|${JSON.stringify(e.when || {})}`;
      const cur = acc.get(k) || { stat: e.stat, ...(e.when ? { when: e.when } : {}), value: 0 };
      cur.value = round(cur.value + e.value);
      acc.set(k, cur);
    }
  };
  addAll(t.base);
  levels.push({ level: 0, minXp: 0, ck3: [...acc.values()].map((x) => ({ ...x })) });
  track.steps.forEach((s, i) => {
    addAll(s.ck3);
    levels.push({ level: i + 1, minXp: s.xp, ck3: [...acc.values()].map((x) => ({ ...x })) });
  });
  return levels;
}

function emitFor(t, tracks, emitted, blEffects) {
  const e = { trigger: `has_trait = ${t.key}` };
  if (emitted.appearance.has(t.key)) e.record = `CX|trait|${t.key}`;
  else if (emitted.commander.has(t.key)) e.record = `CX|ctrait|${t.key}`;
  else e.record = `CX|ktrait|${t.key}`;
  e.emittedToday = emitted.appearance.has(t.key) || emitted.commander.has(t.key);
  if (tracks.length) {
    e.levels = tracks.map((tr) => ({
      track: tr.name,
      record: `CX|txp|${t.key}${tr.implicit ? '' : `:${tr.name}`}|<level>`,
      chain: tr.steps.map((s, i) => ({ level: i + 1, trigger: `has_trait_xp = { trait = ${t.key}${tr.implicit ? '' : ` track = ${tr.name}`} value >= ${s.xp} }` })).reverse(),
    }));
  }
  const cp = [...new Set(blEffects.map((x) => x.when?.cultureParameter).filter(Boolean))].sort();
  const fp = [...new Set(blEffects.map((x) => x.when?.faithParameter).filter(Boolean))].sort();
  if (cp.length) e.cultureParameters = cp;
  if (fp.length) e.faithParameters = fp;
  return e;
}

function emitPlan(traits, cps, fps) {
  // Only traits with a Bannerlord effect or a CK3 fate factor need a line; prowess/martial-only traits are already in the C dump.
  const notEmitted = Object.entries(traits).filter(([, r]) => !r.emit.emittedToday && r.roles.length > 0).map(([k]) => k);
  const withLevels = Object.entries(traits).filter(([, r]) => r.emit.levels && r.roles.some((x) => x !== 'fate')).map(([k]) => k);
  return {
    status: 'PROPOSED additions to the CK3 mod (PROTOCOL §5.5 style CX lines; unknown CX fields are already ignored by the bridge, BattleSnapshotBuilder.CharacterFact). Not implemented: the main build owns ck3_mod/ and tools/gen/gen_ck3_mod.mjs.',
    records: {
      'CX|ktrait|<key>': 'one line per held trait of `traitsNotEmittedToday` (same if-chain shape as CX|ctrait)',
      'CX|txp|<key>[:<track>]|<level>': 'trait level 1..n (first matching has_trait_xp of the chain, highest first); no line = level 0. Only for `traitsWithLevels` the character holds',
      'CX|cparam|<parameter>': 'culture = { has_cultural_parameter = <parameter> } for each of `cultureParameters`',
      'CX|fparam|<parameter>': 'faith = { has_doctrine_parameter = <parameter> } for each of `faithParameters`',
      'S dump ck3xbl_s_has_cmd / ck3xbl_s_cmd': 'side_commander of the combat side (event_targets.log:1596) so the bridge knows whose side.* effects apply; the side commander is also listed as C|CMD',
      'S dump ck3xbl_s_hostility': 'optional: faith_hostility_level of the ENEMY side commander\'s faith towards this side commander\'s faith (0..3), for when.faithHostility (holy_warrior, crusader_king)',
    },
    traitsNotEmittedToday: notEmitted.sort(),
    traitsWithLevels: withLevels.sort(),
    cultureParameters: [...cps].sort(),
    faithParameters: [...fps].sort(),
  };
}

/** Trait lists the CK3 mod already prints (read-only look at the generator and mapping.json). */
function existingEmits() {
  const appearance = new Set();
  const commander = new Set();
  try {
    const gen = fs.readFileSync(path.join(ROOT, 'tools/gen/gen_ck3_mod.mjs'), 'utf8');
    const m = gen.match(/const APPEARANCE_TRAITS = \[([\s\S]*?)\];/);
    if (m) for (const k of m[1].matchAll(/'([a-z_0-9]+)'/g)) appearance.add(k[1]);
  } catch { /* generator missing: nothing emitted */ }
  try {
    const mapping = JSON.parse(fs.readFileSync(path.join(ROOT, 'bridge/data/mapping.json'), 'utf8'));
    for (const k of mapping.ck3_emit?.commander_traits || []) commander.add(k);
  } catch { /* no mapping */ }
  return { appearance, commander };
}

/** Modifier tags of modifiers.log as a predicate (templated $X$ parts match any key word). */
function loadModifierTags() {
  const docs = scriptDocsDir({ required: false, need: ['modifiers.log'] });
  if (!docs) console.warn('warning: modifiers.log of the CK3 script_docs dumps not found (set CK3XBL_SCRIPT_DOCS); modifier tags are not verified');
  const file = path.join(docs ?? '', 'modifiers.log');
  const exact = new Set();
  const patterns = [];
  if (fs.existsSync(file)) {
    for (const line of fs.readFileSync(file, 'utf8').split(/\r?\n/)) {
      const m = line.match(/^Tag: (\S+)/);
      if (!m) continue;
      if (m[1].includes('$')) patterns.push(new RegExp(`^${m[1].replace(/\$[A-Z_]+\$/g, '[a-z0-9_]+')}$`));
      else exact.add(m[1]);
    }
  }
  return (k) => exact.has(k) || patterns.some((r) => r.test(k));
}

function ck3Version() {
  try {
    const launcher = JSON.parse(fs.readFileSync(path.join(GAME, '..', 'launcher', 'launcher-settings.json'), 'utf8'));
    return launcher.rawVersion || launcher.version || null;
  } catch { return null; }
}

// ------------------------------------------------------------------------------------------------------------------

function main() {
  const args = new Set(process.argv.slice(2));
  const table = build();
  const text = JSON.stringify(table, null, 1) + '\n';
  if (args.has('--stdout')) { process.stdout.write(text); return; }
  const old = fs.existsSync(OUT) ? fs.readFileSync(OUT, 'utf8') : null;
  if (args.has('--check')) {
    if (old !== text) { console.error(`${path.relative(ROOT, OUT)} is stale; run node tools/gen/knights/gen_combat_traits.mjs`); process.exit(1); }
    console.log(`${path.relative(ROOT, OUT)} is up to date (${Object.keys(table.traits).length} traits)`);
    return;
  }
  if (old === text) { console.log(`unchanged: ${path.relative(ROOT, OUT)} (${Object.keys(table.traits).length} traits)`); return; }
  const tmp = `${OUT}.tmp`;
  fs.writeFileSync(tmp, text, 'utf8');
  fs.renameSync(tmp, OUT);
  console.log(`wrote ${path.relative(ROOT, OUT)} (${Object.keys(table.traits).length} traits)`);
}

export { build, CONVERSION, PERSONAL, BL_EFFECTS };

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
