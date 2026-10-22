// equipment.mjs - the battle gear of the CK3 mod: CK3 artifacts that are Bannerlord items (PROTOCOL.md §16).
//
// Every artifact is one concrete Bannerlord item, picked from bridge/data/bl_catalog.json: one item per culture, item
// class and tier (the highest tierf wins). Its CK3 prowess, price and secondary stats come from the item's quality q, the
// tier within the range of its kind (0 = the lowest tier of the kind, 1 = the highest):
//   prowess = max(min, round(cap x q^1.6))           caps per kind (KINDS): main weapon 20, body 8, head 7, shield 6 ...
//   gold    = 200 x (maxGold / 200)^q, to 10          maxGold per kind: main weapon 5000, horse 4000, body 3500 ...
//   prestige = gold / 10 when q >= 0.75 (the top items)
// The era multiplies the price (ERA_PRICE). Quality and wealth (CK3 rarity, durability) are 10 + 90 q.
//
// Ten character inventory slots (SLOTS) take them: one main weapon, three gear slots (shield, polearm, thrown, dagger),
// head, body, hands, legs, cape and horse. The mod logs CX|gear|<slot flag>|<item id> for every equipped one; the bridge
// reads the same table (bridge/data/ck3xbl_equipment.json) and dresses the hero.
//
// The renderers below are called by tools/gen/gen_ck3_mod.mjs (OUTPUTS); buildEquipment is pure.

import fs from 'node:fs';
import path from 'node:path';

export const BL_CULTURES = ['vlandia', 'empire', 'sturgia', 'battania', 'aserai', 'khuzait'];
const CULTURE_ADJ = { vlandia: 'Vlandian', empire: 'Imperial', sturgia: 'Sturgian', battania: 'Battanian', aserai: 'Aserai', khuzait: 'Khuzait', neutral_culture: '' };
const QUALITY_WORD = { 1: 'Plain', 2: 'Sturdy', 3: 'Fine', 4: 'Superior', 5: 'Noble', 6: 'Legendary' };

/** The one slot type of every weapon: the four weapon slots take any weapon, a main weapon or a sidearm. */
export const WEAPON_SLOT = 'ck3xbl_weapon';

/** Inventory slot keys, their slot type and the vanilla slot icon (gfx/interface/icons/inventory/<icon>.dds). */
export const SLOTS = [
  { key: 'ck3xbl_weapon_main', flag: WEAPON_SLOT, icon: 'weapon', name: 'Weapon I' },
  { key: 'ck3xbl_gear_1', flag: WEAPON_SLOT, icon: 'weapon', name: 'Weapon II' },
  { key: 'ck3xbl_gear_2', flag: WEAPON_SLOT, icon: 'weapon', name: 'Weapon III' },
  { key: 'ck3xbl_gear_3', flag: WEAPON_SLOT, icon: 'weapon', name: 'Weapon IV' },
  { key: 'ck3xbl_head', flag: 'ck3xbl_head', icon: 'crown', name: 'Helmet' },
  { key: 'ck3xbl_body', flag: 'ck3xbl_body', icon: 'armor', name: 'Body Armour' },
  { key: 'ck3xbl_hands', flag: 'ck3xbl_hands', icon: 'armor', name: 'Gloves' },
  { key: 'ck3xbl_legs', flag: 'ck3xbl_legs', icon: 'armor', name: 'Boots' },
  { key: 'ck3xbl_cape', flag: 'ck3xbl_cape', icon: 'brooch', name: 'Cloak' },
  { key: 'ck3xbl_horse', flag: 'ck3xbl_horse', icon: 'ck3xbl_horse', name: 'Horse' },
];
export const SLOT_FLAGS = [...new Set(SLOTS.map((s) => s.flag))];
const FLAG_NAME = {
  ck3xbl_weapon: 'Weapon', ck3xbl_main: 'Main Weapon', ck3xbl_gear: 'Sidearm', ck3xbl_head: 'Helmet', ck3xbl_body: 'Body Armour', ck3xbl_hands: 'Gloves',
  ck3xbl_legs: 'Boots', ck3xbl_cape: 'Cloak', ck3xbl_horse: 'Horse',
};

/**
 * Artifact kinds: CK3 artifact type ck3xbl_<kind> (icon gfx/interface/icons/artifact/ck3xbl_<kind>.dds), its slot flag,
 * prowess cap and floor, top price, secondary stat. `pool` groups the kinds for the vanilla event hooks.
 */
export const KINDS = {
  sword: { flag: 'ck3xbl_main', cap: 20, gold: 5000, extra: 'weapon', pool: 'weapon', noun: 'Sword' },
  greatsword: { flag: 'ck3xbl_main', cap: 20, gold: 5000, extra: 'weapon', pool: 'weapon', noun: 'Greatsword' },
  axe: { flag: 'ck3xbl_main', cap: 20, gold: 5000, extra: 'weapon', pool: 'weapon', noun: 'Axe' },
  greataxe: { flag: 'ck3xbl_main', cap: 20, gold: 5000, extra: 'weapon', pool: 'weapon', noun: 'Great Axe' },
  mace: { flag: 'ck3xbl_main', cap: 20, gold: 5000, extra: 'weapon', pool: 'weapon', noun: 'Mace' },
  greatmace: { flag: 'ck3xbl_main', cap: 20, gold: 5000, extra: 'weapon', pool: 'weapon', noun: 'War Maul' },
  bow: { flag: 'ck3xbl_main', cap: 20, gold: 5000, extra: 'weapon', pool: 'bow', noun: 'Bow' },
  crossbow: { flag: 'ck3xbl_main', cap: 20, gold: 5000, extra: 'weapon', pool: 'bow', noun: 'Crossbow' },
  shield: { flag: 'ck3xbl_gear', cap: 6, gold: 1500, extra: 'shield', pool: 'weapon', noun: 'Shield' },
  spear: { flag: 'ck3xbl_gear', cap: 4, gold: 1500, extra: null, pool: 'weapon', noun: 'Spear' },
  lance: { flag: 'ck3xbl_gear', cap: 4, gold: 1500, extra: null, pool: 'weapon', noun: 'Lance' },
  polearm: { flag: 'ck3xbl_gear', cap: 4, gold: 1500, extra: null, pool: 'weapon', noun: 'Polearm' },
  thrown: { flag: 'ck3xbl_gear', cap: 3, gold: 600, extra: null, pool: 'weapon', noun: 'Javelins' },
  dagger: { flag: 'ck3xbl_gear', cap: 2, gold: 400, extra: null, pool: 'weapon', noun: 'Dagger' },
  helmet: { flag: 'ck3xbl_head', cap: 7, gold: 2500, extra: 'armour', pool: 'armor', noun: 'Helmet' },
  armour: { flag: 'ck3xbl_body', cap: 8, gold: 3500, extra: 'armour', pool: 'armor', noun: 'Armour' },
  gloves: { flag: 'ck3xbl_hands', cap: 3, gold: 800, extra: null, pool: 'armor', noun: 'Gauntlets' },
  boots: { flag: 'ck3xbl_legs', cap: 3, gold: 800, extra: null, pool: 'armor', noun: 'Boots' },
  cloak: { flag: 'ck3xbl_cape', cap: 2, gold: 1000, extra: 'cloak', pool: 'armor', noun: 'Cloak' },
  horse: { flag: 'ck3xbl_horse', cap: 2, floor: 0, gold: 4000, extra: 'horse', pool: 'horse', noun: 'Horse' },
  camel: { flag: 'ck3xbl_horse', cap: 2, floor: 0, gold: 3000, extra: 'horse', pool: 'horse', noun: 'Camel' },
};

/** Price factor by the buyer's culture era: better gear is scarcer early. */
export const ERA_PRICE = [
  ['culture_era_late_medieval', 0.85], ['culture_era_high_medieval', 1], ['culture_era_early_medieval', 1.25],
];
export const ERA_PRICE_TRIBAL = 1.5;

/** Offer weights by tier: the armory (expensive items are rare) and the vanilla artifact events (mostly middle tiers). */
export const SHOP_WEIGHT = { 1: 30, 2: 28, 3: 20, 4: 12, 5: 7, 6: 3 };
export const EVENT_WEIGHT = { 1: 20, 2: 30, 3: 30, 4: 15, 5: 4, 6: 1 };

/** The six armory offers: what each one rolls from (own = the buyer's Bannerlord culture, any = every culture). */
export const OFFERS = [
  { n: 1, pool: 'main', from: 'own' },
  { n: 2, pool: 'gear', from: 'own' },
  { n: 3, pool: 'armor', from: 'own' },
  { n: 4, pool: 'horse', from: 'own' },
  { n: 5, pool: 'all', from: 'own' },
  { n: 6, pool: 'all', from: 'any' },
];

const BAD_ID = /(tournament|_blunt|training|practice|wooden|sparring|_civil|peasant|^dummy|_test)/i;

function classify(it) {
  const cls = it.classes || [];
  switch (it.type) {
    case 'OneHandedWeapon':
      if (cls.includes('Dagger')) return 'dagger';
      if (cls.includes('OneHandedSword')) return 'sword';
      if (cls.includes('OneHandedAxe')) return 'axe';
      if (cls.includes('Mace')) return 'mace';
      return null;
    case 'TwoHandedWeapon':
      if (cls.includes('TwoHandedSword')) return 'greatsword';
      if (cls.includes('TwoHandedAxe')) return 'greataxe';
      if (cls.includes('TwoHandedMace')) return 'greatmace';
      if (cls.includes('TwoHandedPolearm')) return 'polearm';
      return null;
    case 'Polearm':
      if (it.couch) return 'lance';
      if (cls.includes('TwoHandedPolearm') || it.pike) return 'polearm';
      return 'spear';
    case 'Bow': return 'bow';
    case 'Crossbow': return 'crossbow';
    case 'Thrown': return cls.some((c) => /Javelin|ThrowingAxe|ThrowingKnife/.test(c)) || cls.length === 0 ? 'thrown' : null;
    case 'Shield': return 'shield';
    case 'HeadArmor': return 'helmet';
    case 'BodyArmor': return 'armour';
    case 'HandArmor': return 'gloves';
    case 'LegArmor': return 'boots';
    case 'Cape': return 'cloak';
    case 'Horse':
      if (!it.mountable || it.pack) return null;
      return it.monster === 'camel' ? 'camel' : it.monster === 'horse' ? 'horse' : null;
    default: return null;
  }
}

function noun(kind, it) {
  if (kind === 'armour') {
    switch (it.material) {
      case 'Plate': return 'Plate Armour';
      case 'Chainmail': return 'Mail Hauberk';
      case 'Leather': return 'Leather Armour';
      default: return 'Padded Coat';
    }
  }
  if (kind === 'shield') return it.cls === 'SmallShield' ? 'Round Shield' : 'Great Shield';
  if (kind === 'horse') return it.tier >= 5 ? 'Destrier' : it.tier >= 3 ? 'Warhorse' : 'Courser';
  if (kind === 'thrown') {
    const c = (it.classes || []).join(' ');
    return /ThrowingAxe/.test(c) ? 'Throwing Axes' : /ThrowingKnife/.test(c) ? 'Throwing Knives' : 'Javelins';
  }
  return KINDS[kind].noun;
}

// Russian names: the adjectives agree with the noun's gender (m, f, n) or number (pl).
const RU_NOUN = {
  Sword: ['меч', 'm'], Greatsword: ['двуручный меч', 'm'], Axe: ['топор', 'm'], 'Great Axe': ['секира', 'f'], Mace: ['булава', 'f'],
  'War Maul': ['боевой молот', 'm'], Bow: ['лук', 'm'], Crossbow: ['арбалет', 'm'], 'Round Shield': ['круглый щит', 'm'],
  'Great Shield': ['большой щит', 'm'], Spear: ['копьё', 'n'], Lance: ['кавалерийское копьё', 'n'], Polearm: ['алебарда', 'f'],
  Javelins: ['дротики', 'pl'], 'Throwing Axes': ['метательные топоры', 'pl'], 'Throwing Knives': ['метательные ножи', 'pl'],
  Dagger: ['кинжал', 'm'], Helmet: ['шлем', 'm'], 'Plate Armour': ['латы', 'pl'], 'Mail Hauberk': ['кольчуга', 'f'],
  'Leather Armour': ['кожаный доспех', 'm'], 'Padded Coat': ['стёганка', 'f'], Gauntlets: ['латные перчатки', 'pl'],
  Boots: ['сапоги', 'pl'], Cloak: ['плащ', 'm'], Courser: ['скакун', 'm'], Warhorse: ['боевой конь', 'm'],
  Destrier: ['рыцарский конь', 'm'], Camel: ['верблюд', 'm'],
};
const RU_QUALITY = { 1: 'простой', 2: 'крепкий', 3: 'добротный', 4: 'превосходный', 5: 'благородный', 6: 'легендарный' };
const RU_CULTURE = { vlandia: 'вландийский', empire: 'имперский', sturgia: 'стургийский', battania: 'баттанийский', aserai: 'асерайский', khuzait: 'кузаитский', neutral_culture: '' };

/** A masculine adjective (-ый / -ий) in the gender or number given. */
function ruAdj(adj, g) {
  if (!adj || g === 'm') return adj;
  const soft = /[кгхжшщч]ий$/.test(adj) || /ний$/.test(adj);
  const stem = adj.slice(0, -2);
  if (g === 'f') return stem + 'ая';
  if (g === 'n') return stem + (soft && !/[кгх]ий$/.test(adj) ? 'ее' : 'ое');
  return stem + (soft ? 'ие' : 'ые');
}

function ruName(tier, culture, nounEn) {
  const [nounRu, g] = RU_NOUN[nounEn] ?? [nounEn, 'm'];
  const words = [ruAdj(RU_QUALITY[tier], g), ruAdj(RU_CULTURE[culture], g), nounRu].filter(Boolean).join(' ');
  return words.charAt(0).toUpperCase() + words.slice(1);
}

const round10 = (v) => Math.round(v / 10) * 10;
const r2 = (v) => Math.round(v * 100) / 100;

/** Every item of the catalog groups that can be battle gear. */
function catalogItems(catalog) {
  const out = [];
  for (const group of ['weapons', 'armors', 'shields', 'horses', 'camels']) {
    const v = catalog.items[group];
    if (!v) continue;
    for (const it of Array.isArray(v) ? v : Object.values(v)) out.push(it);
  }
  return out;
}

/** The harness a horse artifact brings: same mount family, the horse's culture (else any), the best tier not above the horse. */
function pickHarness(catalog, horse) {
  const v = catalog.items.harness || [];
  const all = (Array.isArray(v) ? v : Object.values(v)).filter((h) => h.mount_family === horse.monster && Number.isInteger(h.tier) && h.tier >= 1);
  const fits = (h) => h.tier <= horse.tier;
  const byTier = (a, b) => b.tier - a.tier || (b.tierf ?? 0) - (a.tierf ?? 0) || a.id.localeCompare(b.id);
  return [...all.filter((h) => h.culture === horse.culture && fits(h)), ...all.filter(fits)].sort(byTier)[0]?.id
    ?? [...all].sort((a, b) => a.tier - b.tier || a.id.localeCompare(b.id))[0]?.id ?? null;
}

/** The gear table (pure): items with index, slot flag, kind, culture, tier, prowess, price and extras. */
export function buildEquipment(catalog) {
  const best = new Map();
  for (const it of catalogItems(catalog)) {
    if (!Number.isInteger(it.tier) || it.tier < 1 || it.tier > 6) continue;
    if (!BL_CULTURES.includes(it.culture) && it.culture !== 'neutral_culture') continue;
    if (BAD_ID.test(it.id) || it.tournament) continue;
    const kind = classify(it);
    if (!kind) continue;
    const variant = kind === 'armour' ? it.material ?? '' : kind === 'shield' ? it.cls ?? '' : '';
    const key = `${it.culture}|${kind}|${variant}|${it.tier}`;
    const had = best.get(key);
    if (!had || (it.tierf ?? 0) > (had.it.tierf ?? 0) || ((it.tierf ?? 0) === (had.it.tierf ?? 0) && it.id < had.it.id)) best.set(key, { it, kind });
  }
  const chosen = [...best.values()];
  const tierRange = {};
  for (const { it, kind } of chosen) {
    const r = (tierRange[kind] ??= { lo: 6, hi: 1 });
    r.lo = Math.min(r.lo, it.tier);
    r.hi = Math.max(r.hi, it.tier);
  }
  // The item order (and so every ck3xbl_item_<n> index in saved games) stays the one of the old main / sidearm slot split.
  const slotOrder = ['ck3xbl_main', 'ck3xbl_gear', ...SLOT_FLAGS.filter((f) => f !== WEAPON_SLOT)];
  const kindOrder = Object.keys(KINDS);
  chosen.sort((a, b) => slotOrder.indexOf(KINDS[a.kind].flag) - slotOrder.indexOf(KINDS[b.kind].flag)
    || kindOrder.indexOf(a.kind) - kindOrder.indexOf(b.kind)
    || a.it.culture.localeCompare(b.it.culture) || a.it.tier - b.it.tier || a.it.id.localeCompare(b.it.id));

  const names = new Map();
  const items = chosen.map(({ it, kind }, i) => {
    const k = KINDS[kind];
    const r = tierRange[kind];
    const q = r.hi > r.lo ? (it.tier - r.lo) / (r.hi - r.lo) : 1;
    const prowess = Math.max(k.floor ?? 1, Math.round(k.cap * q ** 1.6));
    const gold = round10(200 * (k.gold / 200) ** q);
    const prestige = q >= 0.75 ? round10(gold / 10) : 0;
    const extras = {};
    if (k.extra === 'weapon' && q >= 0.75) extras.monthly_prestige = r2(0.1 + 0.4 * q);
    if (k.extra === 'armour' && q >= 0.25) extras.knight_effectiveness_mult = r2(0.15 * q);
    if (k.extra === 'shield' && q >= 0.5) extras.knight_effectiveness_mult = r2(0.1 * q);
    if (k.extra === 'cloak') extras.monthly_prestige = r2(0.1 + 0.2 * q);
    if (k.extra === 'horse') extras.movement_speed = r2(0.03 + 0.07 * q);
    const nounEn = noun(kind, it);
    let name = [QUALITY_WORD[it.tier], CULTURE_ADJ[it.culture], nounEn].filter(Boolean).join(' ');
    let nameRu = ruName(it.tier, it.culture, nounEn);
    const n = (names.get(name) ?? 0) + 1;
    names.set(name, n);
    const numeral = ['', 'I', 'II', 'III', 'IV', 'V', 'VI', 'VII', 'VIII'][n] ?? String(n);
    if (n > 1) { name = `${name} ${numeral}`; nameRu = `${nameRu} ${numeral}`; }
    return {
      idx: i + 1, id: it.id, kind, slot: k.flag, culture: it.culture, tier: it.tier, quality: r2(q), prowess, gold, prestige,
      rarity_value: Math.round(10 + 90 * q), extras, name, name_ru: nameRu, harness: kind === 'horse' || kind === 'camel' ? pickHarness(catalog, it) : null,
    };
  });
  // Names that got a numeral: the first one too, so the pair reads I / II.
  for (const it of items) if (names.get(it.name) > 1) { it.name = `${it.name} I`; it.name_ru = `${it.name_ru} I`; }
  return { items };
}

/** Items a pool rolls from. pool: main | gear | armor | horse | all | weapon | bow (the event pools); culture null = any. */
export function poolItems(items, pool, culture) {
  const inPool = (it) => {
    const k = KINDS[it.kind];
    switch (pool) {
      case 'main': return k.flag === 'ck3xbl_main';
      case 'gear': return k.flag === 'ck3xbl_gear';
      case 'armor': return ['ck3xbl_head', 'ck3xbl_body', 'ck3xbl_hands', 'ck3xbl_legs', 'ck3xbl_cape'].includes(k.flag);
      case 'horse': return k.flag === 'ck3xbl_horse';
      case 'all': return true;
      default: return k.pool === pool;
    }
  };
  return items.filter((it) => inPool(it) && (culture === null || it.culture === culture || it.culture === 'neutral_culture'));
}

// ---------------------------------------------------------------------------------------------------------------
// Renderers (each returns the file text without its generated header; gen_ck3_mod.mjs adds the header)
// ---------------------------------------------------------------------------------------------------------------

const mod = (it) => `ck3xbl_item_${it.idx}_modifier`;
const loc = (it) => `ck3xbl_item_${it.idx}`;

export function slotsText() {
  const L = ['# The battle gear slots of the character inventory (gui/window_inventory.gui shows them on the Battle Gear page).'];
  for (const s of SLOTS) L.push(`${s.key} = {`, `\ttype = ${s.flag}`, '\tcategory = inventory', `\ticon = ${s.icon}`, '}');
  return L;
}

/** The slot type an artifact kind goes into: every weapon (main or sidearm) fits any of the four weapon slots. */
export const slotTypeOf = (k) => (k.flag === 'ck3xbl_main' || k.flag === 'ck3xbl_gear' ? WEAPON_SLOT : k.flag);

export function typesText() {
  const L = ['# One artifact type per gear kind; the item itself is told by its modifier (ck3xbl_item_<n>_modifier). Every weapon kind',
    '# goes into the same slot type, so any of the four weapon slots takes any weapon.'];
  for (const [kind, k] of Object.entries(KINDS)) L.push(`ck3xbl_${kind} = {`, `\tslot = ${slotTypeOf(k)}`, `\tdefault_visuals = ck3xbl_${kind}`, '\tcan_reforge = no', '}');
  return L;
}

// The 3D model CK3 shows in the artifact window: every visual needs one (without it create_artifact logs "Couldn't determine
// 'asset' visual type" and the visual type "has no defined assets"). Base-game models of vanilla artifact visuals, the nearest
// weapon for weapons; armour pieces, cloaks and mounts have no vanilla model of their own.
export const GEAR_ASSETS = {
  sword: 'ep1_western_sword_01_a_entity', greatsword: 'ep1_western_sword_01_a_entity',
  axe: 'ep1_western_axe_01_a_entity', greataxe: 'ep1_western_axe_01_a_entity',
  mace: 'ep1_western_mace_01_a_entity', greatmace: 'ep1_western_hammer_01_a_entity',
  bow: 'ep2_long_bow_01_a_portrait_entity', crossbow: 'ep2_crossbow_01_entity',
  shield: 'ep1_western_shield_01_a_entity',
  spear: 'ep1_western_spear_01_a_entity', lance: 'ep1_western_spear_01_a_entity', polearm: 'ep1_western_spear_01_a_entity',
  thrown: 'ep1_western_spear_01_a_entity', dagger: 'ep1_western_dagger_01_a_entity',
  helmet: 'ep1_western_shield_01_a_entity', armour: 'ep1_western_shield_01_a_entity', gloves: 'ep1_western_shield_01_a_entity',
  boots: 'ep1_western_shield_01_a_entity', cloak: 'ep1_generic_animal_hide_small_01_a_entity',
  horse: 'ep1_generic_trophy_horse_01_a_entity', camel: 'ep1_generic_trophy_horse_01_a_entity',
};

export function visualsText() {
  const L = ['# Icons of the gear kinds: gfx/interface/icons/artifact/ck3xbl_<kind>.dds, rendered by tools/gen/equipment/make_icons.mjs',
    '# from game-icons.net (CC BY 3.0, authors in tools/gen/equipment/icons/SOURCES.md and the mod\'s CREDITS.md).',
    '# asset: a base-game model for the artifact window (GEAR_ASSETS in tools/gen/equipment/equipment.mjs).'];
  for (const kind of Object.keys(KINDS)) {
    const asset = GEAR_ASSETS[kind];
    if (!asset) throw new Error(`equipment.mjs: no GEAR_ASSETS model for gear kind ${kind}`);
    L.push(`ck3xbl_${kind} = {`, `\ticon = "ck3xbl_${kind}.dds"`, `\tasset = ${asset}`, `\tdefault_type = ck3xbl_${kind}`, '}');
  }
  return L;
}

export function modifiersText(items) {
  const L = ['# One modifier per gear item: its prowess (no portrait change, as vanilla artifacts) and secondary stats.'];
  for (const it of items) {
    const parts = [`prowess_no_portrait = ${it.prowess}`].filter(() => it.prowess > 0);
    for (const [k, v] of Object.entries(it.extras)) parts.push(`${k} = ${v}`);
    L.push(`${mod(it)} = { ${parts.length ? parts.join(' ') : 'prowess_no_portrait = 0'} }`);
  }
  return L;
}

/** CK3 culture keys -> Bannerlord culture, from mapping.json culture_map. */
function culturesByBl(mapping) {
  const by = Object.fromEntries(BL_CULTURES.map((c) => [c, []]));
  for (const [ck3, bl] of Object.entries(mapping.culture_map)) (by[bl] ?? by.empire).push(ck3);
  for (const c of BL_CULTURES) by[c].sort();
  return by;
}

export function triggersText(mapping) {
  const L = ['# this = character. Whose Bannerlord culture the armory stocks (mapping.json culture_map).'];
  const by = culturesByBl(mapping);
  for (const c of BL_CULTURES) {
    L.push(`ck3xbl_gear_culture_${c}_trigger = {`, '\tOR = {');
    for (const ck3 of by[c]) L.push(`\t\thas_culture = culture:${ck3}`);
    if (by[c].length === 0) L.push('\t\talways = no');
    L.push('\t}', '}');
  }
  return L;
}

export function valuesText() {
  const L = ['# this = character. The armory price factor of the character\'s culture era.', 'ck3xbl_era_price_factor = {', `\tvalue = ${ERA_PRICE_TRIBAL}`];
  for (const [era, f] of [...ERA_PRICE].reverse()) L.push(`\tif = { limit = { culture = { has_cultural_era_or_later = ${era} } } value = ${f} }`);
  L.push('}');
  return L;
}

/** random_list entries that pick one item: scope values ck3xbl_pick, ck3xbl_pick_gold, ck3xbl_pick_prestige. */
function rollLines(list, weights, indent) {
  const t = '\t'.repeat(indent);
  const L = [`${t}random_list = {`];
  for (const it of list) {
    L.push(`${t}\t${weights[it.tier]} = {`,
      `${t}\t\tsave_scope_value_as = { name = ck3xbl_pick value = ${it.idx} }`,
      `${t}\t\tsave_scope_value_as = { name = ck3xbl_pick_gold value = ${it.gold} }`,
      `${t}\t\tsave_scope_value_as = { name = ck3xbl_pick_prestige value = ${it.prestige} }`,
      `${t}\t}`);
  }
  L.push(`${t}}`);
  return L;
}

export function effectsText(items) {
  const L = [];
  L.push('# this = character (the new owner), scope:ck3xbl_pick = the item index. Creates the artifact (scope:newly_created_artifact).');
  L.push('ck3xbl_create_item_effect = {');
  items.forEach((it, i) => {
    L.push(`\t${i === 0 ? 'if' : 'else_if'} = {`, `\t\tlimit = { scope:ck3xbl_pick = ${it.idx} }`, '\t\tcreate_artifact = {',
      `\t\t\tname = ${loc(it)}`, `\t\t\tdescription = ${loc(it)}_desc`, `\t\t\ttype = ck3xbl_${it.kind}`, `\t\t\tvisuals = ck3xbl_${it.kind}`,
      `\t\t\tmodifier = ${mod(it)}`, `\t\t\tquality = ${it.rarity_value}`, `\t\t\twealth = ${it.rarity_value}`,
      '\t\t\tsave_scope_as = newly_created_artifact', '\t\t}', '\t}');
  });
  L.push('}', '');

  L.push('# this = character. One CX|gear line per equipped battle gear artifact (PROTOCOL.md §16.2).');
  L.push('ck3xbl_emit_cx_gear_effect = {', '\tevery_equipped_character_artifact = {');
  L.push(`\t\tlimit = { OR = { ${SLOT_FLAGS.map((f) => `artifact_slot_type = ${f}`).join(' ')} } }`);
  items.forEach((it, i) => L.push(`\t\t${i === 0 ? 'if' : 'else_if'} = { limit = { has_artifact_modifier = ${mod(it)} } info_log = "CK3XBL|CX|gear|${it.slot}|${it.id}" }`));
  L.push('\t}', '}', '');

  // Pools: own culture (with neutral items) per Bannerlord culture, and any culture.
  const pools = ['main', 'gear', 'armor', 'horse', 'all', 'weapon', 'bow'];
  for (const pool of pools) {
    for (const [label, weights] of [['shop', SHOP_WEIGHT], ['event', EVENT_WEIGHT]]) {
      if (label === 'event' && !['weapon', 'bow', 'armor'].includes(pool)) continue;
      if (label === 'shop' && ['weapon', 'bow'].includes(pool)) continue;
      L.push(`# this = character. Picks a ${pool} item of the character's Bannerlord culture for the ${label === 'shop' ? 'armory' : 'vanilla artifact events'}.`);
      L.push(`ck3xbl_roll_${label}_${pool}_own_effect = {`);
      BL_CULTURES.forEach((c, i) => {
        const list = poolItems(items, pool, c);
        L.push(`\t${i === 0 ? 'if' : 'else_if'} = {`, `\t\tlimit = { ck3xbl_gear_culture_${c}_trigger = yes }`);
        L.push(...rollLines(list.length ? list : poolItems(items, pool, null), weights, 2));
        L.push('\t}');
      });
      L.push('\telse = {', ...rollLines(poolItems(items, pool, 'empire'), weights, 2), '\t}', '}');
      if (label === 'shop' && pool === 'all') {
        L.push(`ck3xbl_roll_shop_all_any_effect = {`, ...rollLines(poolItems(items, 'all', null), weights, 1), '}');
      }
      L.push('');
    }
  }

  L.push('# this = character. Fills the six armory offers (variables ck3xbl_offer_<n>, _gold, _prestige); prices with the era factor.');
  L.push('ck3xbl_armory_restock_effect = {');
  for (const o of OFFERS) {
    L.push(`\tck3xbl_roll_shop_${o.pool}_${o.from === 'any' ? 'any' : 'own'}_effect = yes`);
    L.push(`\tset_variable = { name = ck3xbl_offer_${o.n} value = scope:ck3xbl_pick }`);
    L.push(`\tset_variable = { name = ck3xbl_offer_${o.n}_gold value = { value = scope:ck3xbl_pick_gold multiply = ck3xbl_era_price_factor round = yes } }`);
    L.push(`\tset_variable = { name = ck3xbl_offer_${o.n}_prestige value = { value = scope:ck3xbl_pick_prestige multiply = ck3xbl_era_price_factor round = yes } }`);
  }
  L.push('\tset_variable = { name = ck3xbl_armory_stocked value = yes years = 2 }', '}', '');

  L.push('# this = character. Buys armory offer $N$ (the option checked the money): pays, creates the artifact, clears the offer.');
  L.push('ck3xbl_armory_buy_effect = {',
    '\tremove_short_term_gold = var:ck3xbl_offer_$N$_gold',
    '\tif = {', '\t\tlimit = { var:ck3xbl_offer_$N$_prestige > 0 }', '\t\tadd_prestige = { value = var:ck3xbl_offer_$N$_prestige multiply = -1 }', '\t}',
    '\tsave_scope_value_as = { name = ck3xbl_pick value = var:ck3xbl_offer_$N$ }',
    '\tck3xbl_create_item_effect = yes',
    '\tremove_variable = ck3xbl_offer_$N$', '\tremove_variable = ck3xbl_offer_$N$_gold', '\tremove_variable = ck3xbl_offer_$N$_prestige', '}', '');

  L.push('# this = AI ruler. Equips one of its knights (or itself) with an item it can comfortably pay for (PROTOCOL.md §16.5).');
  L.push('ck3xbl_ai_outfit_effect = {',
    '\trandom_list = {', '\t\t40 = { ck3xbl_roll_shop_main_own_effect = yes }', '\t\t20 = { ck3xbl_roll_shop_gear_own_effect = yes }',
    '\t\t30 = { ck3xbl_roll_shop_armor_own_effect = yes }', '\t\t10 = { ck3xbl_roll_shop_horse_own_effect = yes }', '\t}',
    '\tsave_scope_value_as = { name = ck3xbl_ai_gold value = { value = scope:ck3xbl_pick_gold multiply = ck3xbl_era_price_factor round = yes } }',
    // A reserve: the AI keeps two thirds of its treasury and prestige.
    '\tsave_scope_value_as = { name = ck3xbl_ai_need_gold value = { value = scope:ck3xbl_ai_gold multiply = 3 } }',
    '\tsave_scope_value_as = { name = ck3xbl_ai_need_prestige value = { value = scope:ck3xbl_pick_prestige multiply = 3 } }',
    '\tif = {', '\t\tlimit = {', '\t\t\tgold >= scope:ck3xbl_ai_need_gold', '\t\t\tprestige >= scope:ck3xbl_ai_need_prestige', '\t\t}',
    '\t\tremove_short_term_gold = scope:ck3xbl_ai_gold',
    '\t\tif = { limit = { scope:ck3xbl_pick_prestige > 0 } add_prestige = { value = scope:ck3xbl_pick_prestige multiply = -1 } }',
    '\t\trandom_list = {',
    '\t\t\t60 = {', '\t\t\t\ttrigger = { any_knight = { is_alive = yes } }', '\t\t\t\trandom_knight = { ck3xbl_create_item_effect = yes }', '\t\t\t}',
    '\t\t\t40 = { ck3xbl_create_item_effect = yes }', '\t\t}', '\t}', '}', '');

  for (const [effect, pool] of [['create_artifact_weapon_effect', 'weapon'], ['create_artifact_armor_effect', 'armor'], ['create_artifact_bow_effect', 'bow']]) {
    L.push(`# Hook for the vanilla ${effect} (common/scripted_effects/zz_ck3xbl_vanilla_artifact_hooks.txt): this = the owner.`);
    L.push(`ck3xbl_event_${pool}_item_effect = {`, `\tck3xbl_roll_event_${pool}_own_effect = yes`, '\tck3xbl_create_item_effect = yes', '}', '');
  }
  return L;
}

/** customizable_localization: the name and the cost line of each armory offer. */
export function customLocText(items) {
  const L = ['# The armory offers: ck3xbl_offer_<n>_name (the item, with its prowess) and ck3xbl_offer_<n>_cost.'];
  for (const o of OFFERS) {
    L.push(`ck3xbl_offer_${o.n}_name = {`, '\ttype = character', '\trandom_valid = no');
    for (const it of items) L.push(`\ttext = { trigger = { var:ck3xbl_offer_${o.n} = ${it.idx} } localization_key = ${loc(it)}_offer }`);
    L.push('}');
    L.push(`ck3xbl_offer_${o.n}_cost = {`, '\ttype = character', '\trandom_valid = no',
      `\ttext = { trigger = { var:ck3xbl_offer_${o.n}_prestige > 0 } localization_key = ck3xbl_offer_${o.n}_cost_prestige }`,
      `\ttext = { trigger = { always = yes } localization_key = ck3xbl_offer_${o.n}_cost_gold }`, '}');
  }
  return L;
}

/** Fixed texts of the gear localization, per language. */
const TEXT = {
  english: {
    slots: Object.fromEntries(SLOTS.map((s) => [s.key, s.name])),
    flags: FLAG_NAME,
    type: (kind) => KINDS[kind].noun.toLowerCase(),
    desc: {
      weapon: 'A weapon fit for the field. In Bannerlord you carry exactly this weapon.',
      armour: 'Battle harness. In Bannerlord you wear exactly this piece.',
      horse: 'A trained mount. In Bannerlord you ride it into battle, barded to match.',
    },
    modDesc: 'Carried into Bannerlord as this very item.',
    and: 'and',
    buy: 'Buy',
    for: 'for',
  },
  russian: {
    slots: {
      ck3xbl_weapon_main: 'Оружие I', ck3xbl_gear_1: 'Оружие II', ck3xbl_gear_2: 'Оружие III', ck3xbl_gear_3: 'Оружие IV',
      ck3xbl_head: 'Шлем', ck3xbl_body: 'Доспех', ck3xbl_hands: 'Перчатки', ck3xbl_legs: 'Сапоги', ck3xbl_cape: 'Плащ', ck3xbl_horse: 'Конь',
    },
    flags: {
      ck3xbl_weapon: 'Оружие', ck3xbl_main: 'Основное оружие', ck3xbl_gear: 'Снаряжение', ck3xbl_head: 'Шлем', ck3xbl_body: 'Доспех', ck3xbl_hands: 'Перчатки',
      ck3xbl_legs: 'Сапоги', ck3xbl_cape: 'Плащ', ck3xbl_horse: 'Конь',
    },
    type: (kind) => (RU_NOUN[KINDS[kind].noun]?.[0] ?? { Shield: 'щит', Armour: 'доспех', Horse: 'конь' }[KINDS[kind].noun] ?? KINDS[kind].noun),
    desc: {
      weapon: 'Оружие, достойное поля боя. В Bannerlord у вас будет именно оно.',
      armour: 'Боевое снаряжение. В Bannerlord на вас будет именно эта вещь.',
      horse: 'Обученный конь. В Bannerlord вы поедете на нём в бой, в подходящей сбруе.',
    },
    modDesc: 'Переходит в Bannerlord именно этим предметом.',
    and: 'и',
    buy: 'Купить',
    for: 'за',
  },
};

/** Localization of the gear (the generated mark is added after the l_<language>: line by gen_ck3_mod.mjs). */
export function locText(items, language = 'english') {
  const T = TEXT[language];
  const q = (s) => s.replace(/"/g, "'");
  const nameOf = (it) => q(language === 'russian' ? it.name_ru : it.name);
  const L = [];
  for (const s of SLOTS) L.push(` ${s.key}:0 "${T.slots[s.key]}"`);
  for (const f of SLOT_FLAGS) L.push(` artifact_slot_${f}:0 "${T.flags[f]}"`);
  for (const kind of Object.keys(KINDS)) L.push(` artifact_ck3xbl_${kind}:0 "${T.type(kind)}"`);
  for (const it of items) {
    const k = KINDS[it.kind];
    const desc = T.desc[k.flag === 'ck3xbl_horse' ? 'horse' : ['ck3xbl_main', 'ck3xbl_gear'].includes(k.flag) ? 'weapon' : 'armour'];
    L.push(` ${loc(it)}:0 "${nameOf(it)}"`);
    L.push(` ${loc(it)}_desc:0 "${desc}"`);
    L.push(` ${loc(it)}_offer:0 "${nameOf(it)}${it.prowess > 0 ? ` (+${it.prowess} @prowess_icon!)` : ''}"`);
    L.push(` ${mod(it)}:0 "${nameOf(it)}"`);
    L.push(` ${mod(it)}_desc:0 "${T.modDesc}"`);
  }
  for (const o of OFFERS) {
    L.push(` ck3xbl_offer_${o.n}_cost_gold:0 "[ROOT.Char.MakeScope.Var('ck3xbl_offer_${o.n}_gold').GetValue|0] @gold_icon!"`);
    L.push(` ck3xbl_offer_${o.n}_cost_prestige:0 "[ROOT.Char.MakeScope.Var('ck3xbl_offer_${o.n}_gold').GetValue|0] @gold_icon! ${T.and} [ROOT.Char.MakeScope.Var('ck3xbl_offer_${o.n}_prestige').GetValue|0] @prestige_icon!"`);
    L.push(` ck3xbl_armory.0001.buy_${o.n}:0 "${T.buy}: [ROOT.Char.Custom('ck3xbl_offer_${o.n}_name')], ${T.for} [ROOT.Char.Custom('ck3xbl_offer_${o.n}_cost')]"`);
  }
  return L;
}

/** The armory: decision and event, with one option per offer. */
export function decisionsText() {
  return [
    '# The armory (PROTOCOL.md §16.4): opens the shop event; the stock lasts two years.',
    'ck3xbl_visit_armory_decision = {',
    '\tpicture = { reference = "gfx/interface/illustrations/decisions/decision_smith.dds" }',
    '\tdesc = ck3xbl_visit_armory_decision_desc',
    '\tselection_tooltip = ck3xbl_visit_armory_decision_tooltip',
    '\tis_shown = { is_ruler = yes }',
    '\tis_valid_showing_failures_only = { is_imprisoned = no }',
    '\tcooldown = { months = 3 }',
    '\teffect = {',
    '\t\tif = { limit = { NOT = { has_variable = ck3xbl_armory_stocked } } ck3xbl_armory_restock_effect = yes }',
    '\t\ttrigger_event = ck3xbl_armory.0001',
    '\t}',
    '\tai_check_interval = 36',
    '\tai_potential = { gold >= 600 }',
    '\tai_will_do = { base = 25 }',
    '}',
  ];
}

export function eventsText() {
  const L = ['namespace = ck3xbl_armory', '', '# The armory: buy any of the offers you can pay for, then the event opens again.', 'ck3xbl_armory.0001 = {',
    '\ttype = character_event', '\ttitle = ck3xbl_armory.0001.t', '\tdesc = ck3xbl_armory.0001.desc', '\ttheme = martial', '\tleft_portrait = root', ''];
  for (const o of OFFERS) {
    L.push('\toption = {', `\t\tname = ck3xbl_armory.0001.buy_${o.n}`,
      '\t\ttrigger = {', `\t\t\thas_variable = ck3xbl_offer_${o.n}`, `\t\t\tgold >= var:ck3xbl_offer_${o.n}_gold`, `\t\t\tprestige >= var:ck3xbl_offer_${o.n}_prestige`, '\t\t}',
      `\t\tshow_as_unavailable = { has_variable = ck3xbl_offer_${o.n} }`,
      `\t\tck3xbl_armory_buy_effect = { N = ${o.n} }`, '\t\ttrigger_event = ck3xbl_armory.0001',
      // The AI buys now and then and leaves more often (each purchase opens the event again).
      '\t\tai_chance = { base = 5 }', '\t}');
  }
  L.push('\toption = {', '\t\tname = ck3xbl_armory.0001.leave', '\t\tai_chance = { base = 20 }', '\t}', '}');
  return L;
}

export function eventLocText(language = 'english') {
  if (language === 'russian') return [
    ' ck3xbl_visit_armory_decision:0 "Посетить оружейную"',
    ' ck3xbl_visit_armory_decision_desc:0 "Кузнецы, бронники и барышники показывают лучшие товары. Что вы купите здесь, то и возьмёте с собой в бой."',
    ' ck3xbl_visit_armory_decision_tooltip:0 "Купить оружие, доспехи и коней для боевого снаряжения в инвентаре."',
    ' ck3xbl_visit_armory_decision_confirm:0 "В оружейную"',
    ' ck3xbl_armory.0001.t:0 "Оружейная"',
    ' ck3xbl_armory.0001.desc:0 "Мастера раскладывают свой товар. Ассортимент меняется раз в два года; лучшие вещи попадаются редко, а самые ценные стоят не только золота, но и престижа."',
    ' ck3xbl_armory.0001.leave:0 "На сегодня всё."',
    ' CK3XBL_GEAR_TOGGLE:0 "Снаряжение"',
    ' CK3XBL_GEAR_TOGGLE_TT:0 "Показать слоты боевого снаряжения: то, с чем вы выйдете на поле в Bannerlord. Пустые слоты берут снаряжение вашей культуры и ранга."',
    ' CK3XBL_GEAR_HEADER:0 "Боевое снаряжение"',
  ];
  return [
    ' ck3xbl_visit_armory_decision:0 "Visit the Armory"',
    ' ck3xbl_visit_armory_decision_desc:0 "Smiths, armourers and horse traders show their finest wares. What you buy here is what you carry into battle."',
    ' ck3xbl_visit_armory_decision_tooltip:0 "Buy weapons, armour and horses for the Battle Gear slots of your inventory."',
    ' ck3xbl_visit_armory_decision_confirm:0 "To the armory"',
    ' ck3xbl_armory.0001.t:0 "The Armory"',
    ' ck3xbl_armory.0001.desc:0 "The armourers lay out their wares. The stock changes every two years; the finest pieces are rare, and the best of them cost prestige as well as gold."',
    ' ck3xbl_armory.0001.leave:0 "That will be all."',
    ' CK3XBL_GEAR_TOGGLE:0 "Battle Gear"',
    ' CK3XBL_GEAR_TOGGLE_TT:0 "Show the battle gear slots: what you carry into Bannerlord. Empty slots keep the equipment of your culture and rank."',
    ' CK3XBL_GEAR_HEADER:0 "Battle Gear"',
  ];
}

/** The vanilla artifact effects, redefined to give battle gear (loaded after vanilla's 00_ep1_artifact_creation_effects.txt). */
export function vanillaHooksText() {
  const L = ['# The vanilla weapon, armour and bow artifact effects give battle gear instead (PROTOCOL.md §16.6): the 66 vanilla',
    '# events that call them stay untouched. The parameters are kept; scope:newly_created_artifact is set as vanilla does.'];
  L.push('# CREATOR and SET_*_TYPE are not needed (the item, not a smith, decides the piece), but every caller passes them, and an',
    '# argument the body does not use fails the whole call ("unknown arguments", error.log 2026-10-18): so they are read and dropped.');
  for (const [effect, pool, type] of [['create_artifact_weapon_effect', 'weapon', 'WEAPON'], ['create_artifact_armor_effect', 'armor', 'ARMOR'], ['create_artifact_bow_effect', 'bow', 'BOW']]) {
    L.push(`${effect} = {`, `\t$CREATOR$ ?= { save_temporary_scope_as = ck3xbl_unused_creator }`,
      `\tsave_temporary_scope_value_as = { name = ck3xbl_unused_type value = $SET_${type}_TYPE$ }`,
      `\t$OWNER$ = { ck3xbl_event_${pool}_item_effect = yes }`, '}');
  }
  return L;
}

/** The inventory window with the Battle Gear page: vanilla's gui/window_inventory.gui plus our block in its left side. */
export function inventoryGuiText(vanillaText) {
  const lines = vanillaText.replace(/^﻿/, '').split(/\r?\n/);
  const at = lines.findIndex((l) => /name\s*=\s*"left side"/.test(l));
  if (at < 0) throw new Error('window_inventory.gui: no "left side" widget');
  // The left side widget opened on the line before its name; find its closing brace.
  let open = at;
  while (open >= 0 && !/\{\s*$/.test(lines[open])) open--;
  let depth = 0;
  let close = -1;
  for (let i = open; i < lines.length; i++) {
    for (const ch of lines[i].replace(/#.*$/, '')) {
      if (ch === '{') depth++;
      else if (ch === '}') depth--;
    }
    if (depth === 0) { close = i; break; }
  }
  if (close < 0) throw new Error('window_inventory.gui: unbalanced left side widget');
  const ind = (lines[close].match(/^\t*/) || [''])[0] + '\t';
  const cells = [
    ['ck3xbl_head', 0, 0], ['ck3xbl_cape', 1, 0], ['ck3xbl_horse', 2, 0],
    ['ck3xbl_weapon_main', 0, 1], ['ck3xbl_body', 1, 1], ['ck3xbl_gear_1', 2, 1],
    ['ck3xbl_hands', 0, 2], ['ck3xbl_legs', 1, 2], ['ck3xbl_gear_2', 2, 2],
    ['ck3xbl_gear_3', 2, 3],
  ];
  const B = [
    '',
    '# CK3XBL battle gear (tools/gen/equipment/equipment.mjs): the page covers the paperdoll while it is shown.',
    'widget = {',
    '\tname = "ck3xbl_battle_gear"',
    '\tvisible = "[GetVariableSystem.Exists(\'ck3xbl_gear\')]"',
    '\tsize = { 100% 100% }',
    '\talwaystransparent = no',
    '\twidget = {', '\t\tusing = Window_Background_NoDecoration', '\t\tsize = { 100% 100% }', '\t}',
    '\ttext_label_center = {', '\t\tparentanchor = top|hcenter', '\t\tposition = { 0 2 }', '\t\ttext = "CK3XBL_GEAR_HEADER"', '\t}',
  ];
  for (const [slot, col, row] of cells) {
    B.push('\twidget_inventory_slot = {', `\t\tname = "${slot}_slot"`, `\t\tdatacontext = "[Inventory.GetSlot('${slot}')]"`,
      '\t\tparentanchor = top|left', `\t\tposition = { ${30 + col * 150} ${30 + row * 138} }`, '\t}');
  }
  B.push('}', '',
    // Below the vanilla weapon slot (vcenter, 140 high, 30 up: it ends 40 below the centre), above the brooch row; on the Battle
    // Gear page the grid's fourth row holds only the right column, so the button covers no slot there either.
    'button_standard = {', '\tname = "ck3xbl_gear_toggle"', '\tparentanchor = vcenter|left', '\tposition = { 15 75 }', '\tsize = { 150 32 }',
    '\ttext = "CK3XBL_GEAR_TOGGLE"', '\ttooltip = "CK3XBL_GEAR_TOGGLE_TT"', '\tonclick = "[GetVariableSystem.Toggle(\'ck3xbl_gear\')]"', '}');
  lines.splice(close, 0, ...B.map((l) => (l === '' ? '' : ind + l)));

  // The slot-type tabs of the right side: the vanilla page lists the vanilla types, the Battle Gear page ours (all of them in one
  // row do not fit the window, and the tabs past its edge cannot be clicked).
  const tabs = lines.findIndex((l) => /name\s*=\s*"artifact_slot_type"/.test(l));
  const vis = tabs < 0 ? -1 : lines.findIndex((l, i) => i > tabs && i < tabs + 4 && l.includes('visible = "[InventorySlotType.IsInventoryType]"'));
  if (vis < 0) throw new Error('window_inventory.gui: no slot-type tab visibility to patch');
  const isOurs = (f) => `EqualTo_string( InventorySlotType.GetSlotTypeString, '${f}' )`;
  const tests = SLOT_FLAGS.map(isOurs);
  if (tests.length < 2) throw new Error('the tab filter expects at least two slot types');
  const ours = tests.reduceRight((rest, t) => (rest === null ? t : `Or( ${t}, ${rest} )`), null);
  const page = "GetVariableSystem.Exists( 'ck3xbl_gear' )";
  lines[vis] = lines[vis].replace('visible = "[InventorySlotType.IsInventoryType]"',
    `visible = "[And( InventorySlotType.IsInventoryType, Or( And( ${ours}, ${page} ), And( Not( ${ours} ), Not( ${page} ) ) ) )]"`);
  return lines;
}

/** bridge/data/ck3xbl_equipment.json (read by CK3xBL.Mapping). */
export function equipmentJson(table) {
  return JSON.stringify({
    meta: {
      generated_by: 'tools/gen/equipment/equipment.mjs (via tools/gen/gen_ck3_mod.mjs)',
      source: 'bridge/data/bl_catalog.json',
      record: 'CX|gear|<slot flag>|<Bannerlord item id> (PROTOCOL.md §16.2)',
    },
    slots: SLOTS.map((s) => ({ key: s.key, flag: s.flag })),
    items: table.items.map((it) => ({
      idx: it.idx, id: it.id, kind: it.kind, slot: it.slot, culture: it.culture, tier: it.tier, quality: it.quality,
      prowess: it.prowess, gold: it.gold, prestige: it.prestige, harness: it.harness, name: it.name,
    })),
  }, null, 1) + '\n';
}

export const EQUIPMENT_JSON = path.join('bridge', 'data', 'ck3xbl_equipment.json');

/** Writes text to a file when it differs; returns true when it did (or would, with dryRun). */
export function writeIfChanged(file, text, dryRun) {
  const old = fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : null;
  if (old === text) return false;
  if (!dryRun) {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, text, 'utf8');
  }
  return true;
}
