#!/usr/bin/env node
// gen_artifact_tables.mjs - builds the two artifact tables for the knights feature (docs/PROTOCOL.md §13.8):
//
//   bridge/data/ck3_artifact_types.json      CK3 facts: artifact types -> slot type / battle category, rarities, the
//                                            script names to read equipped artifacts, the modifiers that carry
//                                            knight effectiveness / prowess, and per-rarity bonus estimates
//                                            computed from the install's creation effects (modifier pools).
//   bridge/data/ck3_artifact_to_bl_items.json Bannerlord replacements: CK3 artifact category x rarity x Bannerlord
//                                            culture (x mounted/foot) -> item ids, validated against the install's
//                                            item XML and bridge/data/bl_catalog.json, plus slot placement rules.
//
// Inputs (read-only): the CK3 install (CK3_GAME_DIR), the Bannerlord install (BL_DIR), both found in the Steam libraries
// when unset; the CK3 script_docs dumps (CK3XBL_SCRIPT_DOCS, default the CK3 logs folder; tools/gen/lib/game_dirs.cjs); bridge/data/bl_catalog.json, bridge/data/bl_ruler_kits.json.
// Output is deterministic (no timestamps). Usage: node tools/gen/knights/gen_artifact_tables.mjs [--check]
//   --check  write nothing; exit 1 if an output would change. Exit 2 on a validation error (unknown item id etc.).

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { bannerlordDir, gameDir, listFiles, readText, scriptDocsDir } from '../lib/pdx.mjs';
import { COMBAT_FIELDS, SIGNATURE_WEAPON_FLAGS, estimateBonuses, readArtifactDatabase, readModifiers, scanArtifactSites } from './ck3_artifacts.mjs';
import {
  ARMOR_SCORE, ARMOR_STYLE_PREFERENCE, BL_CULTURES, BODY_TARGET, CATEGORY_CLASSES, CULTURE_FALLBACK, CURATED, HEAD_TARGET,
  HELMET_FEATURE_PATTERNS, RARITIES, TYPE_TO_CATEGORY, catalogIndex, loadItemXml, pickArmor, pickHelmet,
} from './bl_items.mjs';
process.exit(0);
const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(HERE, '..', '..', '..');
const DATA = path.join(REPO, 'bridge', 'data');
const DOCS = scriptDocsDir();
const GAME = gameDir();
const CK3_ROOT = path.dirname(GAME);
const BL_ROOT = bannerlordDir();
const CHECK = process.argv.includes('--check');
const OUT_TYPES = path.join(DATA, 'ck3_artifact_types.json');
const OUT_ITEMS = path.join(DATA, 'ck3_artifact_to_bl_items.json');
const GENERATOR = 'tools/gen/knights/gen_artifact_tables.mjs';

const errors = [];
const fail = (msg) => errors.push(msg);

// ------------------------------------------------------------------------------------------------ script names
/** Where a script key is documented (script_docs <log>:<line>), first definition line. */
function docLine(name) {
  for (const log of ['triggers.log', 'effects.log', 'event_targets.log', 'modifiers.log']) {
    const lines = readText(path.join(DOCS, log)).split(/\r?\n/);
    const idx = lines.findIndex((l) => l.startsWith(`${name} - `) || l === `Tag: ${name}`);
    if (idx >= 0) return { log, line: idx + 1, text: lines[idx].slice(0, 160) };
  }
  return null;
}

/** First vanilla uses of `name` (whole word) in game/common and game/events. */
function vanillaUses(name, max = 4) {
  const re = new RegExp(`(^|[^A-Za-z0-9_])${name}\\s*(=|<|>|!)`);
  const out = [];
  let count = 0;
  for (const d of ['common', 'events']) {
    for (const file of listFiles(path.join(GAME, d), ['.txt'], true)) {
      const text = readText(file);
      if (!text.includes(name)) continue;
      const lines = text.split(/\r?\n/);
      lines.forEach((l, i) => {
        const code = l.split('#')[0];
        if (!re.test(code)) return;
        count++;
        if (out.length < max) out.push(`${path.relative(GAME, file).split(path.sep).join('/')}:${i + 1}: ${code.trim().replace(/\s+/g, ' ').slice(0, 110)}`);
      });
    }
  }
  return { count, examples: out };
}

// [name, what, a verified vanilla use (file:line under game/, checked to contain the name)]
const SCRIPT_NAMES = [
  ['any_equipped_character_artifact', 'trigger (character scope) - iterate equipped artifacts (inventory and court slots)', 'common/activities/pulse_actions/tournament_actions.txt:526'],
  ['every_equipped_character_artifact', 'effect (character scope) - iterate equipped artifacts; use this to emit records', 'common/on_action/death.txt:776'],
  ['ordered_equipped_character_artifact', 'effect (character scope) - e.g. the highest-rarity weapon only', null],
  ['random_equipped_character_artifact', 'effect (character scope)', 'common/scripted_effects/00_personal_details_effects.txt:368'],
  ['artifact_slot_type', 'trigger (artifact) - inventory slot type: primary_armament | armor | helmet | regalia | ...', 'common/character_interactions/00_artifact_interactions.txt:498'],
  ['artifact_type', 'trigger (artifact) - type key of common/artifacts/types (sword, armor_mail, ...)', 'common/scripted_effects/00_nickname_effects.txt:3806'],
  ['rarity', 'trigger (artifact) - common | masterwork | famed | illustrious', 'common/character_interactions/00_artifact_interactions.txt:509'],
  ['rarity_level', 'trigger (artifact) - compare with <, >= etc. (1 = common .. 4 = illustrious)', 'common/artifacts/templates/00_event_templates.txt:248'],
  ['category', 'trigger (artifact) - inventory | court (the key exists in other scopes too; vanilla_uses is not artifact-only)', 'common/artifacts/templates/00_type_templates.txt:35'],
  ['has_artifact_modifier', 'trigger (artifact) - exact modifier key (reads the rolled knight effectiveness exactly)', 'common/decisions/90_minor_decisions.txt:2574'],
  ['has_artifact_feature', 'trigger (artifact) - feature key, e.g. armor_helmet_nasal', 'common/scripted_effects/00_ep1_artifact_creation_effects.txt:10737'],
  ['has_artifact_feature_group', 'trigger (artifact) - feature group, e.g. blade_decoration', 'common/scripted_effects/00_ep1_artifact_creation_effects.txt:10116'],
  ['is_equipped', 'trigger (artifact)', 'common/activities/pulse_actions/wedding_pulse_actions.txt:647'],
  ['is_unique', 'trigger (artifact) - unique (historical) template', 'common/character_interactions/00_artifact_interactions.txt:247'],
  ['can_benefit_from_artifact', 'trigger (character, target artifact) - template can_benefit; false -> only the fallback modifier applies', 'common/character_interactions/00_artifact_interactions.txt:409'],
  ['artifact_owner', 'link artifact -> character', 'common/casus_belli_types/01_ep1_wars.txt:22'],
  ['num_artifact_kills', 'trigger (artifact) - kills made with it', null],
];

function checkReference(name, ref) {
  if (!ref) return null;
  const [file, line] = ref.split(':');
  const lines = readText(path.join(GAME, file)).split(/\r?\n/);
  const text = (lines[Number(line) - 1] || '').trim();
  if (!text.includes(name)) { fail(`vanilla reference ${ref} does not contain ${name}`); return null; }
  return `${ref}: ${text.replace(/\s+/g, ' ').slice(0, 110)}`;
}

// ------------------------------------------------------------------------------------------------ CK3 table
function buildTypesTable() {
  const db = readArtifactDatabase(GAME);
  const modifiers = readModifiers(GAME);
  const { byType, modifierSites } = scanArtifactSites(GAME);
  const est = estimateBonuses(GAME, modifiers);
  const version = (() => {
    try { return JSON.parse(readText(path.join(CK3_ROOT, 'launcher', 'launcher-settings.json'))).rawVersion; } catch { return null; }
  })();

  const types = db.types.map((t) => {
    const s = byType.get(t.key);
    return {
      ...t,
      bl_category: TYPE_TO_CATEGORY[t.key] || null,
      created_by_script: s ? s.count : 0,
      visuals_seen: s ? [...s.visuals].sort() : [],
      templates_seen: s ? [...s.templates].sort() : [],
    };
  });
  for (const k of Object.keys(TYPE_TO_CATEGORY)) if (!types.some((t) => t.key === k)) fail(`artifact type ${k} (TYPE_TO_CATEGORY) is not in common/artifacts/types`);

  const slotTypes = [];
  for (const s of db.slots) {
    const e = slotTypes.find((x) => x.slot_type === s.slot_type);
    if (e) e.slots.push(s.key); else slotTypes.push({ slot_type: s.slot_type, category: s.category, slots: [s.key] });
  }

  const pickFields = (vals) => Object.fromEntries(COMBAT_FIELDS.filter((f) => vals[f] !== undefined).map((f) => [f, vals[f]]));
  const siteList = (key) => {
    const sites = modifierSites.get(key) || [];
    const effects = [...new Set(sites.map((s) => s.effect))].sort();
    return { sites: sites.length, added_by: effects.slice(0, 12), first_site: sites[0] ? `${sites[0].file}:${sites[0].line}` : null };
  };
  const keModifiers = [];
  const klModifiers = [];
  const prowessModifiers = [];
  for (const [key, m] of [...modifiers.entries()].sort(([a], [b]) => a.localeCompare(b))) {
    const v = m.values;
    const used = modifierSites.has(key);
    if (!used) continue; // only modifiers that script actually puts on artifacts
    if (v.knight_effectiveness_mult) keModifiers.push({ key, ...pickFields(v), source: m.source, ...siteList(key) });
    else if (v.knight_limit) klModifiers.push({ key, ...pickFields(v), source: m.source, ...siteList(key) });
    if ((v.prowess || v.prowess_no_portrait || v.prowess_mult) && key.startsWith('artifact_')) prowessModifiers.push({ key, ...pickFields(v), source: m.source, sites: (modifierSites.get(key) || []).length });
  }

  const scriptNames = SCRIPT_NAMES.map(([name, what, ref]) => {
    const doc = docLine(name);
    if (!doc) fail(`script name ${name} not found in the script_docs dumps (${DOCS})`);
    const uses = vanillaUses(name, 0);
    return { name, what, doc: doc ? `script_docs/${doc.log}:${doc.line}` : null, vanilla_uses: uses.count, vanilla_example: checkReference(name, ref) };
  });

  // Per-rarity summary for the knight-effectiveness reconstruction (the liege's equipped artifacts) and for prowess.
  const meanOf = (block, field) => Object.fromEntries(RARITIES.map((r) => [r, block[r].fields[field].mean]));
  const summary = {
    note: 'Means for ONE freshly created artifact of the type (baseline: no inspiration owner, no special traditions). Prowess is already part of the logged CK3 prowess (ck3xbl_c_prowess) - never add it again. Knight effectiveness belongs to the knights\' liege; read it exactly with has_artifact_modifier (knight_effectiveness_modifiers) and use these means only for artifacts whose modifiers are unknown (modded).',
    knight_effectiveness_mult: {
      melee_weapon: meanOf(est.weapons.sword, 'knight_effectiveness_mult'),
      mace: meanOf(est.weapons.mace, 'knight_effectiveness_mult'),
      dagger: meanOf(est.weapons.dagger, 'knight_effectiveness_mult'),
      armor: meanOf(est.armor.armor_mail, 'knight_effectiveness_mult'),
      bow_or_crossbow: meanOf(est.bows.bow, 'knight_effectiveness_mult'),
      longbow_or_composite: meanOf(est.bows.longbow, 'knight_effectiveness_mult'),
    },
    prowess: {
      melee_weapon: meanOf(est.weapons.sword, 'prowess_no_portrait'),
      mace: meanOf(est.weapons.mace, 'prowess_no_portrait'),
      dagger: meanOf(est.weapons.dagger, 'prowess_no_portrait'),
      armor_mail: meanOf(est.armor.armor_mail, 'prowess_no_portrait'),
      armor_other: meanOf(est.armor.armor_scale, 'prowess_no_portrait'),
      bow: meanOf(est.bows.bow, 'prowess_no_portrait'),
    },
    knight_limit: {
      melee_weapon: meanOf(est.weapons.sword, 'knight_limit'),
      armor: meanOf(est.armor.armor_mail, 'knight_limit'),
    },
    knightly_culture_knight_effectiveness_mult: {
      melee_weapon: Object.fromEntries(RARITIES.map((r) => [r, est.weapons.sword[r].knightly_culture_knight_effectiveness_mean])),
      armor: Object.fromEntries(RARITIES.map((r) => [r, est.armor.armor_mail[r].knightly_culture_knight_effectiveness_mean])),
    },
  };

  const featureGroups = Object.fromEntries(Object.entries(db.features)
    .filter(([g]) => ['armor_helmet', 'armor_material', 'blade_metal', 'head_metal', 'blade_decoration', 'crown_decoration'].includes(g))
    .map(([g, ks]) => [g, ks]));

  return {
    meta: {
      generated_by: GENERATOR,
      ck3_version: version,
      sources: ['common/artifacts/{types,slots,templates,features}', 'common/defines/00_defines.txt (ARTIFACT_THRESHOLD_*, ARTIFACT_DEFAULT_MAX_DURABILITY*)',
        'common/modifiers/*.txt', 'common/scripted_effects/00_ep1_artifact_creation_effects.txt (creation + modifier pools)',
        'every create_artifact / add_artifact_modifier site in common/, events/, history/', 'script_docs/*.log'],
      notes: [
        'types[].slot_type is what artifact_slot_type tests; types[].key is what artifact_type tests; bl_category is the key of ck3_artifact_to_bl_items.json items.',
        'Court-slot artifacts (category court: throne, wall_big, pedestal, ...) are equipped too: vanilla tests any_equipped_character_artifact = { artifact_slot_type = throne } (common/character_interactions/00_artifact_interactions.txt:997). Their modifiers (e.g. a wall banner\'s knight effectiveness) apply to the owner.',
        'Artifact visuals (sword vs longsword, sassanian_sword, ...) cannot be tested by script; only the type key, rarity, features and modifiers can.',
      ],
    },
    script_names: scriptNames,
    rarities: db.rarities,
    slot_types: slotTypes,
    types,
    feature_groups: featureGroups,
    templates_with_can_benefit: db.templates.filter((t) => t.has_can_benefit_condition || Object.keys(t.fallback_modifier).length).map((t) => ({ key: t.key, has_can_benefit_condition: t.has_can_benefit_condition, fallback_modifier: t.fallback_modifier, unique: t.unique, source: t.source })),
    signature_weapon: {
      note: 'Character variable signature_weapon (flag) set by set_signature_weapon_effect (common/scripted_effects/00_personal_details_effects.txt:350): the equipped primary_armament artifact type if any, else a random pick. Portrait triggers portrait_should_wield_<x>_trigger (common/scripted_triggers/00_artifact_triggers.txt:555-636). Optional hint for knights without a weapon artifact.',
      flags: Object.fromEntries(Object.entries(SIGNATURE_WEAPON_FLAGS).map(([flag, t]) => [`flag:${flag}`, t ? TYPE_TO_CATEGORY[t] : null])),
    },
    knight_effectiveness_modifiers: keModifiers,
    knight_limit_modifiers: klModifiers,
    prowess_modifiers: prowessModifiers,
    bonus_estimates: {
      method: 'Exact enumeration of the creation effects (random_list weights incl. pick/unique, if/else on rarity, slot, type, decoration feature groups and wealth) by tools/gen/knights/ck3_eval.mjs. Weapons: decoration first (wealth bands below), then the post-creation block. Stacking: a modifier added twice counts twice (vanilla relies on it: "Base is 1 prowess" + artifact_prowess_1_modifier).',
      wealth_mix_assumed: est.wealth_mix,
      sources: est.sources,
      summary_by_rarity: summary,
      weapons: est.weapons,
      armor: est.armor,
      bows: est.bows,
      pools: est.pools,
      unknown_triggers_treated_false: est.unknown_triggers,
      tournament_prizes: 'Tournament prize weapons/armour (common/scripted_effects/04_dlc_ep2_tournament_effects.txt:4076-4094) roll add_scaled_artifact_modifier_combat_effect once more: famed 50 %, illustrious always.',
    },
  };
}

// ------------------------------------------------------------------------------------------------ Bannerlord table
function buildItemsTable(typesTable) {
  const catalog = JSON.parse(readText(path.join(DATA, 'bl_catalog.json')));
  const kits = JSON.parse(readText(path.join(DATA, 'bl_ruler_kits.json')));
  const idx = catalogIndex(catalog);
  const xml = loadItemXml(BL_ROOT);
  const used = new Map(); // id -> roles it is used in

  const info = (id) => {
    const rec = idx.get(id);
    const x = xml.get(id);
    if (!x) fail(`item ${id} is not in any Modules/*/ModuleData/items/*.xml`);
    if (!rec) fail(`item ${id} is not in bridge/data/bl_catalog.json`);
    if (x && x.module !== 'SandBoxCore' && x.module !== 'Native') fail(`item ${id} is from module ${x.module}, which the bridge launch does not load`);
    const out = { name: rec?.name ?? null, culture: rec?.culture ?? null, group: rec?.group ?? null, bl_type: rec?.type ?? x?.type ?? null, tier: rec?.tier ?? null };
    if (rec?.classes) out.classes = rec.classes;
    if (rec?.usages) out.usages = rec.usages;
    if (rec?.couch) out.couch = true;
    if (rec?.armor) out.armor = rec.armor;
    if (x) out.xml = x.file;
    return out;
  };
  const use = (id, role) => {
    const e = used.get(id) || new Set();
    e.add(role);
    used.set(id, e);
    return id;
  };

  // Validation of weapon class / slot fit per category.
  const expect = {
    sword: (r) => r.classes?.some((c) => CATEGORY_CLASSES.sword.includes(c)),
    axe: (r) => r.classes?.some((c) => CATEGORY_CLASSES.axe.includes(c)),
    mace: (r) => r.classes?.some((c) => CATEGORY_CLASSES.mace.includes(c)),
    spear: (r) => r.type === 'Polearm',
    dagger: (r) => r.classes?.includes('Dagger'),
    bow: (r) => r.type === 'Bow',
    longbow: (r) => r.type === 'Bow',
    composite_bow: (r) => r.type === 'Bow',
    crossbow: (r) => r.type === 'Crossbow',
    crown: (r) => r.type === 'HeadArmor',
  };
  const ammoFor = { bow: 'Arrows', longbow: 'Arrows', composite_bow: 'Arrows', crossbow: 'Bolts' };

  const items = {};
  const why = {};
  for (const [category, perCulture] of Object.entries(CURATED)) {
    items[category] = {};
    for (const culture of BL_CULTURES) {
      const row = perCulture[culture] || perCulture['*'];
      if (!row) { fail(`no ${category} row for ${culture}`); continue; }
      items[category][culture] = {};
      for (const rarity of RARITIES) {
        const entry = {};
        for (const mode of ['mounted', 'foot']) {
          const v = (row[mode] || row.mounted)[rarity];
          const [weapon, ammo] = Array.isArray(v) ? v : [v, null];
          const role = category === 'crown' ? 'Head' : 'weapon';
          const e = { [role]: use(weapon, `${category}.${role}`) };
          const rec = idx.get(weapon);
          if (rec && !expect[category](rec)) fail(`${category}/${culture}/${rarity}/${mode}: ${weapon} (${rec.type} ${rec.classes || ''}) is not a ${category}`);
          if (ammo) {
            e.ammo = use(ammo, `${category}.ammo`);
            const a = idx.get(ammo);
            if (a && a.type !== ammoFor[category]) fail(`${category}/${culture}/${rarity}: ammo ${ammo} is ${a.type}, expected ${ammoFor[category]}`);
          }
          if (mode === 'mounted' && rec && category === 'longbow' && rec.usages?.includes('long_bow')) fail(`${category}/${culture}/${rarity}: ${weapon} is a long_bow (not usable mounted)`);
          if (mode === 'mounted' && rec && category === 'crossbow' && rec.usages?.includes('crossbow') && !rec.usages?.includes('crossbow_light')) fail(`${category}/${culture}/${rarity}: ${weapon} is a heavy crossbow (no mounted reload)`);
          entry[mode] = e;
        }
        items[category][culture][rarity] = entry;
      }
    }
  }

  // Body armour (+ the accompanying helmet: every CK3 armour has the required feature group armor_helmet).
  for (const armorType of Object.keys(ARMOR_STYLE_PREFERENCE)) {
    items[armorType] = {};
    for (const culture of BL_CULTURES) {
      items[armorType][culture] = {};
      for (const rarity of RARITIES) {
        const body = pickArmor(catalog, culture, armorType, rarity);
        const head = pickHelmet(catalog, culture, rarity);
        why[`${armorType}/${culture}/${rarity}`] = { Body: body.why, Head: head.why };
        const e = { Body: use(body.id, `${armorType}.Body`), Head: use(head.id, 'armor.Head') };
        items[armorType][culture][rarity] = { mounted: e, foot: e };
      }
    }
  }

  // Helmet by CK3 armour helmet feature (optional refinement of the armour's Head).
  const helmetByFeature = {};
  const helmetFeatures = typesTable.feature_groups.armor_helmet || [];
  for (const f of helmetFeatures) if (!HELMET_FEATURE_PATTERNS[f]) fail(`armor_helmet feature ${f} has no Bannerlord pattern`);
  for (const f of Object.keys(HELMET_FEATURE_PATTERNS)) {
    if (!helmetFeatures.includes(f)) fail(`HELMET_FEATURE_PATTERNS key ${f} is not an armor_helmet feature of the install`);
    helmetByFeature[f] = {};
    for (const culture of BL_CULTURES) {
      helmetByFeature[f][culture] = {};
      for (const rarity of RARITIES) {
        const h = pickHelmet(catalog, culture, rarity, f);
        helmetByFeature[f][culture][rarity] = use(h.id, 'armor_helmet_feature.Head');
        why[`helmet:${f}/${culture}/${rarity}`] = h.why;
      }
    }
  }

  // Every id: details for the bridge's kit comparison (tier, armour), validated.
  const itemInfo = {};
  for (const id of [...used.keys()].sort()) {
    itemInfo[id] = { ...info(id), used_as: [...used.get(id)].sort() };
    const rec = idx.get(id);
    const roles = [...used.get(id)];
    if (rec && roles.some((r) => r.endsWith('.Body')) && rec.type !== 'BodyArmor') fail(`${id} used as Body but is ${rec.type}`);
    if (rec && roles.some((r) => r.endsWith('.Head')) && rec.type !== 'HeadArmor') fail(`${id} used as Head but is ${rec.type}`);
  }

  // Kit coverage: which kit items the bridge will compare against (class groups of every kit weapon).
  const kitWeaponClasses = new Set();
  for (const r of Object.values(kits.ruler_and_noble_rosters)) for (const s of r.sets) for (const it of s.items) {
    const rec = idx.get(it.id);
    if (rec?.classes) rec.classes.forEach((c) => kitWeaponClasses.add(c));
  }

  const blVersion = catalog.meta?.bannerlord_version || null;
  return {
    meta: {
      generated_by: GENERATOR,
      bannerlord_version: blVersion,
      ck3_types_table: 'bridge/data/ck3_artifact_types.json',
      sources: ['bridge/data/bl_catalog.json (classes, tiers, armour values)', 'bridge/data/bl_ruler_kits.json (hero kits the overrides apply to)',
        'Modules/*/ModuleData/items/*.xml of the install (every id validated; all are SandBoxCore items, loaded by CustomGame)'],
      how_to_use: [
        '1. For every hero (HeroSpec of a CK3 commander/knight) take the artifacts logged for him (CX|artifact records, PROTOCOL.md §13.2): type key + rarity (+ armor_helmet feature).',
        '2. category = type_to_category[type]; culture = the hero\'s Bannerlord culture, via culture_fallback when it is not one of cultures; mode = "mounted" when the hero rides (formation Cavalry/HorseArcher and Horse not cleared), else "foot".',
        '3. entry = items[category][culture][rarity][mode]: roles weapon (+ ammo), Head, Body. Resolve roles to equipment slots with placement (weapons) or directly (Head, Body) and add them to equipment.overrides.',
        '4. Never downgrade: keep the kit item when it is of the same class group and has a higher tier (weapons) or more armour (Body/Head). item_info has tier and armour of every id; bl_ruler_kits.json / bl_catalog.json of the kit items.',
        '5. Several artifacts: one primary_armament, one armor and one helmet (crown) slot exist per CK3 character, so at most one entry per role. A crown (helmet slot) wins Head over the armour\'s helmet.',
      ],
      notes: [
        'Bannerlord crafted melee weapons stop at tier 5 (the noble "_t5" pieces); famed and illustrious therefore map to the decorated noble weapons of the culture. Bows and crossbows reach tier 6 (noble_bow, noble_long_bow, crossbow_f).',
        'CK3 hammer has no Bannerlord counterpart (no war hammer item): it uses the mace rows.',
        'Two-handed picks: sturgia/vlandia/empire foot famed+ axes and aserai famed+ axes are two-handed (sturgia_2haxe_2_t5, aserai_2haxe_3_t5), as the culture lord kits already carry them.',
        'Daggers are only added to an empty weapon slot (placement.dagger); they never replace a weapon.',
        `Body armour: styles from the Bannerlord display name (bodyStyles: "Bronze Lamellar over Mail" = lamellar, then mail), ARMOR_STYLE_PREFERENCE per CK3 type; the item of the hero's culture or a neighbour culture with the lowest score |body - goal| + ${ARMOR_SCORE.NEIGHBOUR_PENALTY} x neighbour + ${ARMOR_SCORE.STYLE_RANK_PENALTY} x style rank + ${ARMOR_SCORE.SECONDARY_PENALTY} x secondary keyword wins; goal = body ${JSON.stringify(BODY_TARGET).replace('null', '"best of the culture"')}. Helmets: head armour closest to ${JSON.stringify(HEAD_TARGET).replace('null', '"max"')}, crowns excluded; armor_helmet_by_feature narrows to the CK3 helmet shape.`,
      ],
    },
    rarities: RARITIES,
    cultures: BL_CULTURES,
    culture_fallback: CULTURE_FALLBACK,
    type_to_category: TYPE_TO_CATEGORY,
    categories: {
      sword: { kind: 'melee', class_group: CATEGORY_CLASSES.sword, placement: 'melee' },
      axe: { kind: 'melee', class_group: CATEGORY_CLASSES.axe, placement: 'melee' },
      mace: { kind: 'melee', class_group: CATEGORY_CLASSES.mace, placement: 'melee' },
      spear: { kind: 'polearm', class_group: CATEGORY_CLASSES.spear, placement: 'polearm' },
      dagger: { kind: 'sidearm', class_group: CATEGORY_CLASSES.dagger, placement: 'dagger' },
      bow: { kind: 'ranged', class_group: CATEGORY_CLASSES.bow, ammo_type: 'Arrows', placement: 'ranged' },
      longbow: { kind: 'ranged', class_group: CATEGORY_CLASSES.bow, ammo_type: 'Arrows', placement: 'ranged' },
      composite_bow: { kind: 'ranged', class_group: CATEGORY_CLASSES.bow, ammo_type: 'Arrows', placement: 'ranged' },
      crossbow: { kind: 'ranged', class_group: CATEGORY_CLASSES.crossbow, ammo_type: 'Bolts', placement: 'ranged' },
      crown: { kind: 'head', slot: 'Head' },
      ...Object.fromEntries(Object.keys(ARMOR_STYLE_PREFERENCE).map((t) => [t, { kind: 'armor', slots: ['Body', 'Head'], style_preference: ARMOR_STYLE_PREFERENCE[t] }])),
    },
    placement: {
      slots: ['Item0', 'Item1', 'Item2', 'Item3'],
      same_group_first: 'If a kit weapon slot holds an item whose classes intersect the category class_group, that slot is the target (keep the kit item when its tier is higher).',
      melee: ['same_group', 'first slot with a one- or two-handed sword/axe/mace (the kit\'s main melee weapon)', 'empty', 'thrown (javelins / throwing axes)', 'polearm'],
      polearm: ['same_group', 'empty', 'thrown', 'second shield', 'main melee weapon'],
      dagger: ['empty'],
      ranged: ['same_group (launcher)', 'the other launcher kind (kit bow for a crossbow artifact and vice versa)', 'empty', 'thrown', 'polearm (a second polearm first; the bow is the CK3 primary armament, so it may displace the kit lance)', 'skip the artifact'],
      ammo: 'Ranged entries carry an ammo id of category ammo_type. Kit ammo of that type is replaced when ours has a higher tier; else the ammo takes the slot of the other ammo type (all of the other ammo is converted), else a free slot (same order as ranged). Without the needed slots the ranged artifact is skipped.',
      shields: 'Shields are never replaced by melee or ranged artifacts.',
    },
    items,
    armor_helmet_by_feature: helmetByFeature,
    item_info: itemInfo,
    choice_reasons: why,
    validation: {
      item_ids: Object.keys(itemInfo).length,
      all_in_item_xml: Object.keys(itemInfo).every((id) => xml.has(id)),
      all_in_catalog: Object.keys(itemInfo).every((id) => idx.has(id)),
      modules: [...new Set(Object.keys(itemInfo).map((id) => xml.get(id)?.module))].sort(),
      kit_weapon_classes_seen: [...kitWeaponClasses].sort(),
    },
  };
}

// ------------------------------------------------------------------------------------------------ main
function writeJson(file, obj) {
  const text = `${JSON.stringify(obj, null, 1)}\n`;
  const old = fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : null;
  if (old === text) return false;
  if (!CHECK) fs.writeFileSync(file, text, 'utf8');
  return true;
}

const typesTable = buildTypesTable();
const itemsTable = buildItemsTable(typesTable);
if (errors.length) {
  console.error(`${errors.length} validation error(s):`);
  for (const e of errors) console.error(`  - ${e}`);
  process.exit(2);
}
const changed = [];
if (writeJson(OUT_TYPES, typesTable)) changed.push(path.relative(REPO, OUT_TYPES));
if (writeJson(OUT_ITEMS, itemsTable)) changed.push(path.relative(REPO, OUT_ITEMS));
const s = typesTable.bonus_estimates.summary_by_rarity;
console.log(`types: ${typesTable.types.length} (${typesTable.types.filter((t) => t.bl_category).length} with a battle category), knight effectiveness modifiers: ${typesTable.knight_effectiveness_modifiers.length}`);
console.log(`items: ${itemsTable.validation.item_ids} ids, all in item XML: ${itemsTable.validation.all_in_item_xml}, all in catalog: ${itemsTable.validation.all_in_catalog}`);
console.log(`prowess (melee weapon) by rarity: ${JSON.stringify(s.prowess.melee_weapon)}`);
console.log(`knight effectiveness (melee weapon) by rarity: ${JSON.stringify(s.knight_effectiveness_mult.melee_weapon)}`);
if (CHECK) {
  if (changed.length) { console.error(`out of date: ${changed.join(', ')}`); process.exit(1); }
  console.log('up to date');
} else {
  console.log(changed.length ? `wrote ${changed.join(', ')}` : 'no changes');
}
