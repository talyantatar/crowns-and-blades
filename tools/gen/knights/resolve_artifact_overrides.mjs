// resolve_artifact_overrides.mjs - reference implementation of the bridge-side rule "CK3 equipped artifacts ->
// HeroSpec.equipment.overrides" (PROTOCOL.md §13.4), for porting to CK3xBL.Mapping.
//
//   resolveArtifactOverrides({ kitItems, culture, mounted, artifacts }, tables) -> { overrides, log }
//     kitItems   { Item0: id, ..., Head: id, Body: id }  the hero's kit set as the module will spawn it
//     culture    Bannerlord culture id of the hero
//     mounted    true when the hero rides (formation Cavalry/HorseArcher and Horse not cleared)
//     artifacts  [{ slot_type, type, rarity, helmet_feature? }]  from CX|artifact / CX|artifact_helmet
//
// Usage: node tools/gen/knights/resolve_artifact_overrides.mjs   runs the self-test over every ruler/noble kit of
// bridge/data/bl_ruler_kits.json x every artifact category x rarity x mounted/foot and prints a few examples.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(HERE, '..', '..', '..');
const ITEM_SLOTS = ['Item0', 'Item1', 'Item2', 'Item3'];

export function loadTables(dataDir = path.join(REPO, 'bridge', 'data')) {
  const read = (f) => JSON.parse(fs.readFileSync(path.join(dataDir, f), 'utf8'));
  const map = read('ck3_artifact_to_bl_items.json');
  const catalog = read('bl_catalog.json');
  const idx = new Map();
  for (const [group, list] of Object.entries(catalog.items)) if (Array.isArray(list)) for (const it of list) idx.set(it.id, { ...it, group });
  return { map, idx };
}

/** Weapon-slot group of a Bannerlord item: sword/axe/mace/polearm/dagger/bow/crossbow/thrown/shield/arrows/bolts/banner/other. */
export function groupOf(rec) {
  if (!rec) return 'other';
  switch (rec.type) {
    case 'Shield': return 'shield';
    case 'Arrows': return 'arrows';
    case 'Bolts': return 'bolts';
    case 'Bow': return 'bow';
    case 'Crossbow': return 'crossbow';
    case 'Thrown': return 'thrown';
    case 'Polearm': return 'polearm';
    case 'Banner': return 'banner';
    default: break;
  }
  const c = rec.classes || [];
  if (c.includes('Dagger')) return 'dagger';
  if (c.some((x) => x === 'OneHandedSword' || x === 'TwoHandedSword')) return 'sword';
  if (c.some((x) => x === 'OneHandedAxe' || x === 'TwoHandedAxe')) return 'axe';
  if (c.some((x) => x === 'Mace' || x === 'TwoHandedMace')) return 'mace';
  return 'other';
}

const CATEGORY_GROUP = { sword: 'sword', axe: 'axe', mace: 'mace', spear: 'polearm', dagger: 'dagger', bow: 'bow', longbow: 'bow', composite_bow: 'bow', crossbow: 'crossbow' };
const MELEE = new Set(['sword', 'axe', 'mace']);

export function resolveArtifactOverrides({ kitItems, culture, mounted, artifacts }, { map, idx }) {
  const log = [];
  const eq = { ...kitItems }; // working copy: slot -> id ('' = empty)
  const overrides = {};
  const set = (slot, id, why) => { eq[slot] = id; overrides[slot] = id; log.push(`${slot} = ${id} (${why})`); };
  const tier = (id) => idx.get(id)?.tier ?? -1;
  const grp = (slot) => (eq[slot] ? groupOf(idx.get(eq[slot])) : 'empty');
  const cult = map.cultures.includes(culture) ? culture : (map.culture_fallback[culture] || map.culture_fallback['*']);
  const mode = mounted ? 'mounted' : 'foot';

  // Order: armour, then crown (so the crown wins Head), then weapons.
  const order = (a) => (a.slot_type === 'armor' ? 0 : a.slot_type === 'helmet' ? 1 : 2);
  for (const art of [...artifacts].sort((a, b) => order(a) - order(b))) {
    const category = map.type_to_category[art.type];
    if (!category) { log.push(`skip ${art.type}: no battle category`); continue; }
    const entry = map.items[category]?.[cult]?.[art.rarity]?.[mode];
    if (!entry) { log.push(`skip ${art.type}/${art.rarity}: no entry`); continue; }

    if (entry.Body || entry.Head) {
      const head = art.helmet_feature && map.armor_helmet_by_feature[art.helmet_feature]?.[cult]?.[art.rarity] || entry.Head;
      for (const [slot, id, key] of [['Body', entry.Body, 'body'], ['Head', head, 'head']]) {
        if (!id) continue;
        // Never downgrade the KIT item; a crown (helmet slot, applied after the armour) replaces an armour helmet anyway.
        const ours = idx.get(id)?.armor?.[key] ?? 0;
        const kit = kitItems[slot] ? (idx.get(kitItems[slot])?.armor?.[key] ?? 0) : -1;
        if (ours >= kit) set(slot, id, `${category} ${art.rarity}: ${key} ${ours} >= kit ${kit}`);
        else log.push(`keep ${slot} ${eq[slot]} (kit ${key} ${kit} > ${ours})`);
      }
      continue;
    }

    const group = CATEGORY_GROUP[category];
    const weapon = entry.weapon;
    const sameGroup = ITEM_SLOTS.filter((s) => grp(s) === group);
    const empty = ITEM_SLOTS.filter((s) => grp(s) === 'empty');
    const thrown = ITEM_SLOTS.filter((s) => grp(s) === 'thrown');
    const polearms = ITEM_SLOTS.filter((s) => grp(s) === 'polearm');
    const shields = ITEM_SLOTS.filter((s) => grp(s) === 'shield');
    const melee = ITEM_SLOTS.filter((s) => MELEE.has(grp(s)));

    if (map.categories[category].kind === 'ranged') {
      const ammoType = map.categories[category].ammo_type === 'Bolts' ? 'bolts' : 'arrows';
      const kitAmmo = ITEM_SLOTS.filter((s) => grp(s) === ammoType);
      // Free for the launcher and its ammo: empty slots, thrown weapons, then polearms (a second polearm first). The
      // bow/crossbow IS the character's primary armament in CK3, so it may displace the kit's lance; never a shield
      // or the main melee weapon.
      // The other launcher kind (a kit bow for a crossbow artifact and vice versa) and its ammo are free as well.
      const otherGroup = group === 'bow' ? 'crossbow' : 'bow';
      const otherAmmoType = ammoType === 'arrows' ? 'bolts' : 'arrows';
      const otherLauncher = ITEM_SLOTS.filter((s) => grp(s) === otherGroup);
      const otherAmmo = ITEM_SLOTS.filter((s) => grp(s) === otherAmmoType);
      const free = [...empty, ...thrown, ...polearms.slice(1), ...polearms.slice(0, 1)];
      let launcherSlot = sameGroup[0] || otherLauncher[0] || null;
      const ammoSlot = kitAmmo.length ? null : (otherAmmo[0] || null);
      const needSlots = (launcherSlot ? 0 : 1) + (kitAmmo.length || ammoSlot ? 0 : 1);
      if (free.length < needSlots) { log.push(`skip ${category}: needs ${needSlots} free weapon slot(s)`); continue; }
      if (!launcherSlot) launcherSlot = free.shift();
      if (eq[launcherSlot] && groupOf(idx.get(eq[launcherSlot])) === group && tier(eq[launcherSlot]) > tier(weapon)) log.push(`keep ${launcherSlot} ${eq[launcherSlot]} (tier ${tier(eq[launcherSlot])} > ${tier(weapon)})`);
      else if (eq[launcherSlot] === weapon) log.push(`keep ${launcherSlot} ${weapon} (already in the kit)`);
      else set(launcherSlot, weapon, `${category} ${art.rarity}`);
      if (kitAmmo.length) { for (const s of kitAmmo) if (tier(entry.ammo) > tier(eq[s])) set(s, entry.ammo, 'better ammo'); }
      else set(ammoSlot || free.shift(), entry.ammo, `${ammoType} for the ${category}`);
      for (const s of otherAmmo) if (s !== ammoSlot && eq[s] && groupOf(idx.get(eq[s])) === otherAmmoType) set(s, entry.ammo, `${ammoType} instead of unusable ${otherAmmoType}`);
      continue;
    }

    let target = null;
    let why = '';
    if (sameGroup.length) { target = sameGroup[0]; why = 'same class group'; }
    else if (category === 'dagger') { target = empty[0] || null; why = 'empty slot'; }
    else if (MELEE.has(group)) { target = melee[0] || empty[0] || thrown[0] || polearms[0] || null; why = 'main melee weapon / free slot'; }
    else if (group === 'polearm') { target = empty[0] || thrown[0] || (shields.length > 1 ? shields[1] : null) || melee[0] || null; why = 'free slot / main melee weapon'; }
    if (!target) { log.push(`skip ${category}: no slot`); continue; }
    if (sameGroup.length && tier(eq[target]) > tier(weapon)) { log.push(`keep ${target} ${eq[target]} (tier ${tier(eq[target])} > ${tier(weapon)})`); continue; }
    if (eq[target] === weapon) { log.push(`keep ${target} ${weapon} (already in the kit)`); continue; }
    set(target, weapon, `${category} ${art.rarity}, ${why}`);
  }
  return { overrides, log };
}

// ------------------------------------------------------------------------------------------------ self-test
function selfTest() {
  const tables = loadTables();
  const { map, idx } = tables;
  const kits = JSON.parse(fs.readFileSync(path.join(REPO, 'bridge', 'data', 'bl_ruler_kits.json'), 'utf8')).ruler_and_noble_rosters;
  const typesByCategory = {};
  for (const [type, cat] of Object.entries(map.type_to_category)) (typesByCategory[cat] ||= type);
  const slotOf = (type) => (type.startsWith('armor_') ? 'armor' : type.startsWith('helmet') ? 'helmet' : 'primary_armament');
  let cases = 0; let skipped = 0; const problems = []; const skipReasons = {};
  for (const [rosterId, roster] of Object.entries(kits)) {
    if (/_civ_/.test(rosterId)) continue; // civilian kits are not battle kits
    const items = {};
    for (const it of roster.sets[0].items) if (!(it.slot in items)) items[it.slot] = it.id;
    for (const [cat, type] of Object.entries(typesByCategory)) for (const rarity of map.rarities) for (const mounted of [true, false]) {
      cases++;
      const { overrides, log } = resolveArtifactOverrides({ kitItems: items, culture: roster.culture, mounted, artifacts: [{ slot_type: slotOf(type), type, rarity }] }, tables);
      const skip = log.find((l) => l.startsWith('skip'));
      if (skip) { skipped++; const k = `${cat} ${mounted ? 'mounted' : 'foot'}: ${skip.replace(/^skip [a-z_]+: /, '')}`; skipReasons[k] = (skipReasons[k] || 0) + 1; }
      const after = { ...items, ...overrides };
      for (const [slot, id] of Object.entries(overrides)) {
        if (!idx.has(id)) problems.push(`${rosterId} ${type} ${rarity}: unknown ${id}`);
        const g = groupOf(idx.get(items[slot]));
        if (items[slot] && g === 'shield') problems.push(`${rosterId} ${type} ${rarity}: replaced shield in ${slot}`);
      }
      const groups = ITEM_SLOTS.map((s) => (after[s] ? groupOf(idx.get(after[s])) : 'empty'));
      if (groups.includes('bow') && !groups.includes('arrows')) problems.push(`${rosterId} ${type} ${rarity} ${mounted}: bow without arrows`);
      if (groups.includes('crossbow') && !groups.includes('bolts')) problems.push(`${rosterId} ${type} ${rarity} ${mounted}: crossbow without bolts`);
    }
  }
  console.log(`self-test: ${cases} cases (kits x categories x rarities x mounted/foot), ${skipped} skipped by placement, ${problems.length} problem(s)`);
  for (const [k, n] of Object.entries(skipReasons).sort()) console.log(`  skipped ${n} x ${k}`);
  for (const p of problems.slice(0, 20)) console.log(`  ${p}`);
  const demo = (rosterId, culture, mounted, artifacts) => {
    const items = {};
    for (const it of kits[rosterId].sets[0].items) if (!(it.slot in items)) items[it.slot] = it.id;
    const r = resolveArtifactOverrides({ kitItems: items, culture, mounted, artifacts }, tables);
    console.log(`\n${rosterId} (${culture}, ${mounted ? 'mounted' : 'foot'}) + ${artifacts.map((a) => `${a.rarity} ${a.type}`).join(', ')}`);
    for (const l of r.log) console.log(`  ${l}`);
  };
  demo('vla_king_template_bat_m', 'vlandia', true, [{ slot_type: 'primary_armament', type: 'sword', rarity: 'famed' }, { slot_type: 'armor', type: 'armor_mail', rarity: 'illustrious', helmet_feature: 'armor_helmet_great_helmet' }, { slot_type: 'helmet', type: 'helmet', rarity: 'illustrious' }]);
  demo('stu_bat_template_medium', 'sturgia', false, [{ slot_type: 'primary_armament', type: 'axe', rarity: 'illustrious' }]);
  demo('khu_bat_template_medium', 'khuzait', true, [{ slot_type: 'primary_armament', type: 'composite', rarity: 'famed' }, { slot_type: 'armor', type: 'armor_lamellar', rarity: 'common' }]);
  demo('emp_bat_template_medium', 'empire', true, [{ slot_type: 'primary_armament', type: 'spear', rarity: 'masterwork' }]);
  return problems.length ? 1 : 0;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) process.exit(selfTest());
