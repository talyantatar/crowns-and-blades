// ck3_artifacts.mjs - reads the artifact system of the CK3 install (read-only) for gen_artifact_tables.mjs.
//
// Sources (all under <CK3>/game):
//   common/artifacts/types/*.txt      type key -> inventory slot type, required/optional feature groups
//   common/artifacts/slots/*.txt      slot key -> slot type + category (inventory | court)
//   common/artifacts/templates/*.txt  scripted templates (can_benefit / fallback / unique)
//   common/artifacts/features/*.txt   feature keys per feature group (armor_helmet_* etc.)
//   common/defines/00_defines.txt     rarity thresholds and durability per rarity
//   common/modifiers/*.txt            static modifiers (prowess / knight effectiveness values)
//   common/scripted_effects, common/scripted_triggers   artifact creation logic (modifier pools)
//   common/**, events/**, history/**  every create_artifact / reforge_artifact / add_artifact_modifier site

import fs from 'node:fs';
import path from 'node:path';
import { listFiles, parseText, readText, walk } from '../lib/pdx.mjs';
import { ArtifactEvaluator, RARITIES, round, summarise } from './ck3_eval.mjs';

export const COMBAT_FIELDS = ['prowess', 'prowess_no_portrait', 'prowess_mult', 'knight_effectiveness_mult', 'knight_limit',
  'negate_prowess_penalty_add'];

/** Category of an artifact type for the battle mapping (null = no battle meaning). */
export const TYPE_CATEGORY = {
  sword: { category: 'sword', weapon_class: 'one_handed_sword', battle: true },
  axe: { category: 'axe', weapon_class: 'axe', battle: true },
  hammer: { category: 'hammer', weapon_class: 'war_hammer', battle: true },
  mace: { category: 'mace', weapon_class: 'mace', battle: true },
  spear: { category: 'spear', weapon_class: 'spear_or_lance', battle: true },
  dagger: { category: 'dagger', weapon_class: 'dagger', battle: true },
  bow: { category: 'bow', weapon_class: 'short_bow', battle: true },
  longbow: { category: 'longbow', weapon_class: 'long_bow', battle: true },
  composite: { category: 'composite_bow', weapon_class: 'composite_bow', battle: true },
  crossbow: { category: 'crossbow', weapon_class: 'crossbow', battle: true },
  armor_mail: { category: 'armor_mail', weapon_class: 'body_armor', battle: true },
  armor_plate: { category: 'armor_plate', weapon_class: 'body_armor', battle: true },
  armor_scale: { category: 'armor_scale', weapon_class: 'body_armor', battle: true },
  armor_lamellar: { category: 'armor_lamellar', weapon_class: 'body_armor', battle: true },
  armor_laminar: { category: 'armor_laminar', weapon_class: 'body_armor', battle: true },
  armor_brigandine: { category: 'armor_brigandine', weapon_class: 'body_armor', battle: true },
  helmet: { category: 'crown', weapon_class: 'crown', battle: true },
  helmet_simple: { category: 'crown', weapon_class: 'crown', battle: true },
};

/** Signature weapon flags (character variable `signature_weapon`, common/scripted_effects/00_personal_details_effects.txt). */
export const SIGNATURE_WEAPON_FLAGS = { axe: 'axe', hammer: 'hammer', mace: 'mace', spear: 'spear', dagger: 'dagger', sword: 'sword', wooden_sword: null };

function rel(game, file) {
  return path.relative(game, file).split(path.sep).join('/');
}

function parseFile(file) {
  return parseText(readText(file));
}

function topBlocks(game, dir, recursive = false) {
  const out = [];
  for (const file of listFiles(path.join(game, dir), ['.txt'], recursive)) {
    for (const n of parseFile(file)) if (n.key && n.block && !n.key.startsWith('@')) out.push({ key: n.key, node: n, file: rel(game, file) });
  }
  return out;
}

function bareValues(block) {
  return (block || []).filter((c) => c.key === null && c.value !== undefined).map((c) => c.value);
}

function child(block, key) {
  return (block || []).find((c) => c.key === key);
}

/** Reads types, slots, templates, features, defines. */
export function readArtifactDatabase(game) {
  const slots = topBlocks(game, 'common/artifacts/slots').map(({ key, node, file }) => {
    const holySite = child(node.block, 'holy_site')?.value === 'yes';
    return {
      key,
      // holy-site relic slots take any type (allow_any_type_or_category); they belong to holy sites, not characters
      slot_type: child(node.block, 'type')?.value ?? (holySite ? '*' : null),
      category: child(node.block, 'category')?.value ?? (holySite ? 'holy_site' : null),
      requires_royal_court: !!child(node.block, 'available'),
      source: `${file}:${node.line}`,
    };
  });
  const slotTypeInfo = new Map();
  for (const s of slots) {
    const e = slotTypeInfo.get(s.slot_type) || { slot_type: s.slot_type, category: s.category, slots: [] };
    e.slots.push(s.key);
    slotTypeInfo.set(s.slot_type, e);
  }
  const types = topBlocks(game, 'common/artifacts/types').map(({ key, node, file }) => {
    const slotType = child(node.block, 'slot')?.value;
    const cat = TYPE_CATEGORY[key] || null;
    return {
      key,
      slot_type: slotType,
      slot_category: slotTypeInfo.get(slotType)?.category || null,
      inventory_slots: slotTypeInfo.get(slotType)?.slots || [],
      required_features: bareValues(child(node.block, 'required_features')?.block),
      optional_features: bareValues(child(node.block, 'optional_features')?.block),
      can_reforge: child(node.block, 'can_reforge')?.value !== 'no',
      battle_category: cat ? cat.category : null,
      weapon_class: cat ? cat.weapon_class : null,
      source: `${file}:${node.line}`,
    };
  });
  const templates = topBlocks(game, 'common/artifacts/templates').map(({ key, node, file }) => {
    const canBenefit = child(node.block, 'can_benefit')?.block || [];
    const fallback = child(node.block, 'fallback')?.block || [];
    return {
      key,
      has_can_benefit_condition: canBenefit.length > 0,
      fallback_modifier: Object.fromEntries(fallback.filter((c) => c.key && c.value !== undefined).map((c) => [c.key, Number(c.value)])),
      unique: child(node.block, 'unique')?.value === 'yes',
      source: `${file}:${node.line}`,
    };
  });
  const features = {};
  for (const { key, node } of topBlocks(game, 'common/artifacts/features')) {
    const g = child(node.block, 'group')?.value;
    if (!g) continue;
    (features[g] ||= []).push(key);
  }
  const defines = readText(path.join(game, 'common/defines/00_defines.txt'));
  const num = (name) => {
    const m = defines.match(new RegExp(`\\b${name}\\s*=\\s*([0-9.]+)`));
    return m ? Number(m[1]) : null;
  };
  const rarities = [
    { key: 'common', level: 1, min_quality_plus_wealth: 0, max_durability: num('ARTIFACT_DEFAULT_MAX_DURABILITY') },
    { key: 'masterwork', level: 2, min_quality_plus_wealth: num('ARTIFACT_THRESHOLD_MASTERWORK'), max_durability: num('ARTIFACT_DEFAULT_MAX_DURABILITY_MASTERWORK') },
    { key: 'famed', level: 3, min_quality_plus_wealth: num('ARTIFACT_THRESHOLD_FAMED'), max_durability: num('ARTIFACT_DEFAULT_MAX_DURABILITY_FAMED') },
    { key: 'illustrious', level: 4, min_quality_plus_wealth: num('ARTIFACT_THRESHOLD_ILLUSTRIOUS'), max_durability: num('ARTIFACT_DEFAULT_MAX_DURABILITY_ILLUSTRIOUS') },
  ];
  return { slots, types, templates, features, rarities };
}

/** All static modifiers with their numeric fields (common/modifiers). */
export function readModifiers(game) {
  const mods = new Map();
  for (const file of listFiles(path.join(game, 'common/modifiers'), ['.txt'])) {
    for (const n of parseFile(file)) {
      if (!n.key || !n.block || n.key.startsWith('@')) continue;
      const vals = {};
      for (const c of n.block) if (c.key && c.value !== undefined && /^-?[0-9.]+$/.test(c.value)) vals[c.key] = Number(c.value);
      mods.set(n.key, { values: vals, source: `${rel(game, file)}:${n.line}` });
    }
  }
  return mods;
}

/** Scripted effects and triggers by name (body nodes). */
export function readScripted(game) {
  const effects = new Map();
  for (const file of listFiles(path.join(game, 'common/scripted_effects'), ['.txt'])) {
    for (const n of parseFile(file)) if (n.key && n.block) effects.set(n.key, Object.assign(n.block, { __file: rel(game, file), __line: n.line }));
  }
  const triggers = new Map();
  for (const file of listFiles(path.join(game, 'common/scripted_triggers'), ['.txt'])) {
    for (const n of parseFile(file)) if (n.key && n.block) triggers.set(n.key, n.block);
  }
  return { effects, triggers };
}

/**
 * Every create_artifact / reforge_artifact block and every add_artifact_modifier in common/, events/, history/.
 * Returns per type: count, visuals, templates, modifiers; and per modifier: the sites that add it to an artifact.
 */
export function scanArtifactSites(game) {
  const byType = new Map();
  const modifierSites = new Map();
  const addSite = (mod, site) => {
    const list = modifierSites.get(mod) || [];
    list.push(site);
    modifierSites.set(mod, list);
  };
  // Scripted effects that add a modifier given as a parameter (add_artifact_modifier = $MODIFIER$, e.g.
  // pam_miles_christi_holy_weapon_blessing_effect): effect name -> parameter names. Their call sites are sites too.
  const paramEffects = new Map();
  for (const file of listFiles(path.join(game, 'common/scripted_effects'), ['.txt'])) {
    const text = readText(file);
    if (!/add_artifact_modifier\s*=\s*\$/.test(text)) continue;
    for (const top of parseText(text)) {
      if (!top.key || !top.block) continue;
      walk(top.block, (n) => {
        const m = n.key === 'add_artifact_modifier' && /^\$([A-Za-z0-9_]+)\$$/.exec(n.value || '');
        if (m) paramEffects.set(top.key, new Set([...(paramEffects.get(top.key) || []), m[1]]));
      });
    }
  }
  const paramRe = paramEffects.size ? new RegExp([...paramEffects.keys()].join('|')) : null;
  const dirs = ['common', 'events', 'history'];
  for (const d of dirs) {
    for (const file of listFiles(path.join(game, d), ['.txt'], true)) {
      if (file.includes(`${path.sep}modifiers${path.sep}`)) continue;
      const text = readText(file);
      if (!/create_artifact|reforge_artifact|add_artifact_modifier/.test(text) && !(paramRe && paramRe.test(text))) continue;
      const nodes = parseText(text);
      const relFile = rel(game, file);
      for (const top of nodes) {
        const owner = top.key || '?';
        walk([top], (n) => {
          if (n.key && n.block && paramEffects.has(n.key)) {
            for (const c of n.block) {
              if (c.key && paramEffects.get(n.key).has(c.key) && c.value && !c.value.includes('$')) {
                addSite(c.value, { file: relFile, line: c.line, effect: owner, how: `${n.key}(${c.key})` });
              }
            }
          }
          if ((n.key === 'create_artifact' || n.key === 'reforge_artifact') && n.block) {
            const type = child(n.block, 'type')?.value || '(unset)';
            const e = byType.get(type) || { count: 0, visuals: new Set(), templates: new Set(), modifiers: new Set(), sites: [] };
            e.count++;
            const vis = child(n.block, 'visuals')?.value; if (vis) e.visuals.add(vis);
            const tpl = child(n.block, 'template')?.value; if (tpl) e.templates.add(tpl);
            for (const c of n.block) if (c.key === 'modifier' && c.value) { e.modifiers.add(c.value); addSite(c.value, { file: relFile, line: c.line, effect: owner, how: n.key }); }
            if (e.sites.length < 3) e.sites.push(`${relFile}:${n.line}`);
            byType.set(type, e);
          } else if (n.key === 'add_artifact_modifier' && n.value) {
            addSite(n.value, { file: relFile, line: n.line, effect: owner, how: 'add_artifact_modifier' });
          }
        });
      }
    }
  }
  return { byType, modifierSites };
}

/** Finds, inside `nodes`, the first `scope:newly_created_artifact = { ... }` block that calls `marker`. */
function findPostCreationBlock(nodes, marker) {
  let found = null;
  walk(nodes, (n) => {
    if (found || n.key !== 'scope:newly_created_artifact' || !n.block) return;
    if (n.block.some((c) => c.key === marker)) found = n;
  });
  return found;
}

/** Base `modifier` of the create_artifact blocks inside an effect (all must agree). */
function baseModifiers(nodes) {
  const set = new Set();
  walk(nodes, (n) => {
    if (n.key === 'create_artifact' && n.block) for (const c of n.block) if (c.key === 'modifier' && c.value) set.add(c.value);
  });
  return [...set];
}

/**
 * Expected prowess / knight effectiveness / knight limit of a freshly created personal artifact, per category
 * and rarity, from the install's creation effects (modifier pools).
 */
export function estimateBonuses(game, modifiers) {
  const { effects, triggers } = readScripted(game);
  const ev = new ArtifactEvaluator(effects, triggers);
  const modValues = new Map([...modifiers.entries()].map(([k, v]) => [k, v.values]));
  const need = (name) => {
    const body = effects.get(name);
    if (!body) throw new Error(`scripted effect ${name} not found in the install`);
    return body;
  };
  // Wealth bands per rarity (INFERRED): rarity needs quality + wealth >= 50/130/180 and
  // wealth = quality/4 + 10 x owner tier + capital development/4 (get_artifact_wealth_effect).
  const WEALTH_MIX = {
    common: [[25, 1]],
    masterwork: [[25, 0.5], [55, 0.5]],
    famed: [[55, 0.6], [80, 0.4]],
    illustrious: [[80, 0.7], [95, 0.3]],
  };
  const mixed = (rarity, fn) => {
    const all = [];
    for (const [wealth, share] of WEALTH_MIX[rarity]) for (const st of fn(wealth)) all.push({ ...st, p: st.p * share });
    return all;
  };
  const fields = COMBAT_FIELDS;
  const out = { weapons: {}, armor: {}, bows: {}, sources: {} };

  // Melee weapons: decoration (wealth driven) -> post-creation modifiers. Daggers get intrigue instead of combat.
  const weaponEffect = need('create_artifact_weapon_effect');
  const weaponPost = findPostCreationBlock(weaponEffect, 'add_scaled_artifact_modifier_prowess_effect');
  const weaponBase = baseModifiers(weaponEffect);
  out.sources.weapon = { effect: `${weaponEffect.__file}:${weaponEffect.__line}`, post_creation_line: weaponPost?.line, base_modifiers: weaponBase };
  for (const wt of ['sword', 'axe', 'hammer', 'mace', 'spear', 'dagger']) {
    const deco = need(`get_${wt}_decoration_effect`);
    out.weapons[wt] = {};
    for (const rarity of RARITIES) {
      const states = mixed(rarity, (wealth) => {
        const ctx = { rarity, slotType: 'primary_armament', type: wt, weaponType: `artifact_weapon_type_${wt}`, wealth };
        const afterDeco = ev.run(deco, [{ p: 1, mods: new Map(weaponBase.map((m) => [m, 1])), groups: new Set() }], ctx);
        return ev.run([weaponPost], afterDeco, ctx);
      });
      const decorated = states.filter((s) => s.groups.size > 0).reduce((a, s) => a + s.p, 0);
      const sum = summarise(states, modValues, fields);
      sum.p_decorated = round(decorated, 4);
      out.weapons[wt][rarity] = sum;
      // knightly-culture variant (tradition_chivalry & co. make the knight-effectiveness option x1.5 likelier)
      const kStates = mixed(rarity, (wealth) => {
        const ctx = { rarity, slotType: 'primary_armament', type: wt, weaponType: `artifact_weapon_type_${wt}`, wealth, knightlyCulture: true };
        const afterDeco = ev.run(deco, [{ p: 1, mods: new Map(weaponBase.map((m) => [m, 1])), groups: new Set() }], ctx);
        return ev.run([weaponPost], afterDeco, ctx);
      });
      sum.knightly_culture_knight_effectiveness_mean = summarise(kStates, modValues, ['knight_effectiveness_mult']).fields.knight_effectiveness_mult.mean;
    }
  }

  // Armour: post-creation modifiers per armour type.
  const armorEffect = need('create_artifact_armor_effect');
  const armorPost = findPostCreationBlock(armorEffect, 'add_scaled_artifact_modifier_prowess_effect');
  const armorBase = baseModifiers(armorEffect);
  out.sources.armor = { effect: `${armorEffect.__file}:${armorEffect.__line}`, post_creation_line: armorPost?.line, base_modifiers: armorBase };
  for (const at of ['armor_mail', 'armor_plate', 'armor_scale', 'armor_lamellar', 'armor_laminar', 'armor_brigandine']) {
    out.armor[at] = {};
    for (const rarity of RARITIES) {
      const ctx = { rarity, slotType: 'armor', type: at, wealth: 55 };
      const states = ev.outcomes([armorPost], ctx, armorBase);
      const sum = summarise(states, modValues, fields);
      const kStates = ev.outcomes([armorPost], { ...ctx, knightlyCulture: true }, armorBase);
      sum.knightly_culture_knight_effectiveness_mean = summarise(kStates, modValues, ['knight_effectiveness_mult']).fields.knight_effectiveness_mult.mean;
      out.armor[at][rarity] = sum;
    }
  }

  // Bows: post-creation modifiers per bow type (bow/crossbow roll the combat pool, longbow/composite do not).
  const bowEffect = need('create_artifact_bow_effect');
  const bowPost = findPostCreationBlock(bowEffect, 'add_scaled_artifact_modifier_prowess_effect');
  const bowBase = baseModifiers(bowEffect);
  out.sources.bow = { effect: `${bowEffect.__file}:${bowEffect.__line}`, post_creation_line: bowPost?.line, base_modifiers: bowBase };
  for (const bt of ['bow', 'longbow', 'composite', 'crossbow']) {
    out.bows[bt] = {};
    for (const rarity of RARITIES) {
      const ctx = { rarity, slotType: 'primary_armament', type: bt, bowType: `artifact_bow_type_${bt}`, wealth: 55 };
      out.bows[bt][rarity] = summarise(ev.outcomes([bowPost], ctx, bowBase), modValues, fields);
    }
  }

  // The two combat pools on their own (what one roll of each pool adds).
  out.pools = {};
  for (const pool of ['add_scaled_artifact_modifier_combat_effect', 'add_2_scaled_artifact_modifier_combat_effect', 'add_scaled_artifact_modifier_prowess_effect']) {
    const body = need(pool);
    out.pools[pool] = { source: `${body.__file}:${body.__line}` };
    for (const slot of ['primary_armament', 'armor']) {
      out.pools[pool][slot] = {};
      for (const rarity of RARITIES) {
        const ctx = { rarity, slotType: slot, type: slot === 'armor' ? 'armor_mail' : 'sword', weaponType: slot === 'armor' ? null : 'artifact_weapon_type_sword', wealth: 55 };
        out.pools[pool][slot][rarity] = summarise(ev.outcomes(body, ctx), modValues, fields);
      }
    }
  }
  out.unknown_triggers = Object.fromEntries([...ev.unknownTriggers.entries()].sort((a, b) => b[1] - a[1]));
  out.unknown_effects = Object.fromEntries([...ev.unknownEffects.entries()].sort((a, b) => b[1] - a[1]));
  out.wealth_mix = WEALTH_MIX;
  return out;
}

export { child, rel, RARITIES };
export function exists(p) { return fs.existsSync(p); }
