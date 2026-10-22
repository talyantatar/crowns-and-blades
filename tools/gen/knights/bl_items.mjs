// bl_items.mjs - Bannerlord side of the artifact mapping (read-only use of the install and bridge/data tables).
//
// Item choices are hand-curated per culture for weapons and crowns (CURATED below) and computed for body armour
// and armour helmets (pickArmor / pickHelmet), then every id is validated against
//   - the item XML of the install (Modules/*/ModuleData/items/*.xml, <Item>/<CraftedItem>), and
//   - bridge/data/bl_catalog.json (class, tier, armour values; only CustomGame-loaded SandBoxCore items are listed).

import fs from 'node:fs';
import path from 'node:path';

export const BL_CULTURES = ['vlandia', 'empire', 'sturgia', 'battania', 'aserai', 'khuzait'];
export const RARITIES = ['common', 'masterwork', 'famed', 'illustrious'];

/** Unknown / minor Bannerlord cultures -> the main culture whose items are used. */
export const CULTURE_FALLBACK = { nord: 'sturgia', vakken: 'sturgia', darshi: 'aserai', neutral_culture: 'empire', '*': 'vlandia' };
/** Neighbour order for armour/helmet searches when a culture has no item of the wanted style. */
const NEIGHBOURS = {
  vlandia: ['empire', 'sturgia', 'battania'],
  empire: ['vlandia', 'aserai', 'sturgia'],
  sturgia: ['vlandia', 'khuzait', 'battania'],
  battania: ['vlandia', 'sturgia', 'empire'],
  aserai: ['empire', 'khuzait', 'vlandia'],
  khuzait: ['aserai', 'sturgia', 'empire'],
};

/** Bannerlord weapon classes (ItemObject WeaponClass) that count as "the same weapon" for each category. */
export const CATEGORY_CLASSES = {
  sword: ['OneHandedSword', 'TwoHandedSword'],
  axe: ['OneHandedAxe', 'TwoHandedAxe'],
  mace: ['Mace', 'TwoHandedMace'],
  spear: ['OneHandedPolearm', 'TwoHandedPolearm', 'LowGripPolearm'],
  dagger: ['Dagger'],
  bow: ['Bow'],
  crossbow: ['Crossbow'],
};

// ---------------------------------------------------------------------------------------------------------------
// Curated weapon and crown ladders. [common, masterwork, famed, illustrious]; a `foot` row overrides `mounted`
// for heroes who fight on foot (formation Infantry/Ranged, or Horse cleared). Melee crafted weapons top out at
// tier 5 in Bannerlord (the noble "_t5" swords); famed/illustrious therefore pick the decorated noble pieces.
// ---------------------------------------------------------------------------------------------------------------
const L = (c, m, f, i) => ({ common: c, masterwork: m, famed: f, illustrious: i });

export const CURATED = {
  sword: {
    vlandia: { mounted: L('vlandia_sword_4_t4', 'vlandia_sword_5_t5', 'vlandia_noble_sword_1_t5', 'vlandia_noble_sword_3_t5') },
    empire: { mounted: L('empire_sword_4_t4', 'empire_sword_6_t5', 'empire_noble_sword_1_t5', 'empire_noble_sword_2_t5') },
    sturgia: { mounted: L('sturgia_sword_4_t4', 'sturgia_sword_5_t5', 'sturgia_noble_sword_2_t5', 'sturgia_noble_sword_4_t5') },
    battania: { mounted: L('battania_sword_4_t4', 'battania_sword_5_t5', 'battania_noble_sword_3_t5', 'battania_noble_sword_1_t5') },
    aserai: { mounted: L('aserai_sword_4_t4', 'aserai_sword_7_t5', 'aserai_noble_sword_1_t5', 'aserai_noble_sword_2_t5') },
    khuzait: { mounted: L('khuzait_sword_4_t4', 'khuzait_noble_sword_3_t5', 'khuzait_noble_sword_1_t5', 'khuzait_noble_sword_2_t5') },
  },
  axe: {
    vlandia: { mounted: L('vlandia_axe_2_t4', 'sturgia_axe_5_t5', 'sturgia_axe_5_t5', 'sturgia_axe_5_t5'),
      foot: L('vlandia_axe_2_t4', 'sturgia_axe_5_t5', 'sturgia_2haxe_2_t5', 'sturgia_2haxe_2_t5') },
    empire: { mounted: L('battle_axe_t4', 'sturgia_axe_5_t5', 'sturgia_axe_5_t5', 'sturgia_axe_5_t5'),
      foot: L('battle_axe_t4', 'sturgia_axe_5_t5', 'sturgia_2haxe_2_t5', 'sturgia_2haxe_2_t5') },
    sturgia: { mounted: L('sturgia_axe_4_t4', 'sturgia_axe_5_t5', 'sturgia_axe_5_t5', 'sturgia_axe_5_t5'),
      foot: L('sturgia_axe_4_t4', 'sturgia_axe_5_t5', 'sturgia_2haxe_2_t5', 'sturgia_2haxe_2_t5') },
    battania: { mounted: L('battania_axe_2_t4', 'battania_axe_3_t5', 'battania_axe_3_t5', 'battania_axe_3_t5') },
    aserai: { mounted: L('bamboo_axe_t4', 'sturgia_axe_5_t5', 'aserai_2haxe_3_t5', 'aserai_2haxe_3_t5') },
    khuzait: { mounted: L('battle_axe_t4', 'sturgia_axe_5_t5', 'sturgia_axe_5_t5', 'sturgia_axe_5_t5') },
  },
  mace: {
    vlandia: { mounted: L('vlandia_mace_2_t4', 'vlandia_mace_3_t5', 'vlandia_mace_3_t5', 'vlandia_mace_3_t5') },
    empire: { mounted: L('empire_mace_3_t4', 'empire_mace_5_t5', 'empire_mace_5_t5', 'empire_mace_4_t5') },
    sturgia: { mounted: L('eastern_steel_mace_t4', 'vlandia_mace_3_t5', 'vlandia_mace_3_t5', 'vlandia_mace_3_t5') },
    battania: { mounted: L('vlandia_mace_2_t4', 'vlandia_mace_3_t5', 'vlandia_mace_3_t5', 'vlandia_mace_3_t5') },
    aserai: { mounted: L('aserai_mace_4_t4', 'shishpar_mace_t4', 'khuzait_mace_4_t5', 'khuzait_mace_4_t5') },
    khuzait: { mounted: L('khuzait_mace_3_t4', 'khuzait_mace_4_t5', 'khuzait_mace_4_t5', 'khuzait_mace_4_t5') },
  },
  spear: {
    vlandia: { mounted: L('vlandia_lance_2_t4', 'vlandia_lance_3_t5', 'vlandia_lance_3_t5', 'vlandia_lance_3_t5'),
      foot: L('western_spear_4_t4', 'northern_spear_4_t5', 'northern_spear_4_t5', 'northern_spear_4_t5') },
    empire: { mounted: L('empire_lance_2_t4', 'empire_lance_3_t5', 'empire_lance_3_t5', 'empire_lance_3_t5'),
      foot: L('western_spear_4_t4', 'empire_polearm_2_t5', 'empire_polearm_2_t5', 'empire_polearm_2_t5') },
    sturgia: { mounted: L('sturgia_lance_1_t4', 'sturgia_lance_2_t5', 'sturgia_lance_2_t5', 'sturgia_lance_2_t5'),
      foot: L('northern_spear_3_t4', 'northern_spear_4_t5', 'northern_spear_4_t5', 'northern_spear_4_t5') },
    battania: { mounted: L('northern_spear_4_t4', 'northern_spear_4_t4', 'sturgia_lance_2_t5', 'sturgia_lance_2_t5'),
      foot: L('highland_spear_4_t4', 'northern_spear_4_t5', 'northern_spear_4_t5', 'northern_spear_4_t5') },
    aserai: { mounted: L('aserai_lance_1_t5', 'aserai_lance_1_t5', 'aserai_lance_1_t5', 'aserai_lance_1_t5'),
      foot: L('eastern_spear_4_t4', 'southern_spear_3_t4', 'eastern_spear_5_t5', 'eastern_spear_5_t5') },
    khuzait: { mounted: L('khuzait_lance_2_t4', 'khuzait_lance_3_t5', 'khuzait_lance_3_t5', 'khuzait_lance_3_t5'),
      foot: L('eastern_spear_4_t4', 'eastern_spear_5_t5', 'eastern_spear_5_t5', 'eastern_spear_5_t5') },
  },
  dagger: {
    vlandia: { mounted: L('seax', 'seax', 'Broad_Skaen', 'Broad_Skaen') },
    empire: { mounted: L('pugio', 'pugio', 'Broad_Skaen', 'Broad_Skaen') },
    sturgia: { mounted: L('seax', 'seax', 'Broad_Skaen', 'Broad_Skaen') },
    battania: { mounted: L('celtic_dagger', 'celtic_dagger', 'Broad_Skaen', 'Broad_Skaen') },
    aserai: { mounted: L('hooked_cleaver', 'hooked_cleaver', 'Broad_Skaen', 'Broad_Skaen') },
    khuzait: { mounted: L('hooked_cleaver', 'hooked_cleaver', 'Broad_Skaen', 'Broad_Skaen') },
  },
  // Ranged: weapon + ammo. long_bow usage cannot be shot from horseback in Bannerlord, so mounted rows use bows.
  bow: {
    '*': { mounted: L(['composite_bow', 'bodkin_arrows_b'], ['composite_bow', 'bodkin_arrows_b'], ['noble_bow', 'bodkin_arrows_a'], ['noble_bow', 'bodkin_arrows_a']) },
    sturgia: { mounted: L(['nordic_shortbow', 'bodkin_arrows_b'], ['composite_bow', 'bodkin_arrows_b'], ['noble_bow', 'bodkin_arrows_a'], ['noble_bow', 'bodkin_arrows_a']) },
    khuzait: { mounted: L(['steppe_war_bow', 'steppe_arrows'], ['steppe_heavy_bow', 'heavy_steppe_arrows'], ['noble_bow', 'heavy_steppe_arrows'], ['noble_bow', 'heavy_steppe_arrows']) },
  },
  longbow: {
    '*': { mounted: L(['composite_bow', 'bodkin_arrows_b'], ['composite_bow', 'bodkin_arrows_b'], ['noble_bow', 'bodkin_arrows_a'], ['noble_bow', 'bodkin_arrows_a']),
      foot: L(['lowland_yew_bow', 'bodkin_arrows_b'], ['longbow_recurve_desert_bow', 'bodkin_arrows_b'], ['noble_long_bow', 'bodkin_arrows_a'], ['noble_long_bow', 'piercing_arrows']) },
    battania: { mounted: L(['composite_bow', 'bodkin_arrows_b'], ['composite_bow', 'bodkin_arrows_b'], ['noble_bow', 'bodkin_arrows_a'], ['noble_bow', 'bodkin_arrows_a']),
      foot: L(['woodland_longbow', 'bodkin_arrows_b'], ['woodland_longbow', 'bodkin_arrows_a'], ['noble_long_bow', 'bodkin_arrows_a'], ['noble_long_bow', 'piercing_arrows']) },
    aserai: { mounted: L(['composite_bow', 'bodkin_arrows_b'], ['composite_bow', 'bodkin_arrows_b'], ['noble_bow', 'bodkin_arrows_a'], ['noble_bow', 'bodkin_arrows_a']),
      foot: L(['nomad_bow', 'bodkin_arrows_b'], ['longbow_recurve_desert_bow', 'bodkin_arrows_b'], ['noble_long_bow', 'bodkin_arrows_a'], ['noble_long_bow', 'piercing_arrows']) },
  },
  composite_bow: {
    '*': { mounted: L(['composite_bow', 'bodkin_arrows_b'], ['steppe_heavy_bow', 'bodkin_arrows_b'], ['noble_bow', 'bodkin_arrows_a'], ['noble_bow', 'piercing_arrows']) },
    khuzait: { mounted: L(['composite_steppe_bow', 'steppe_arrows'], ['steppe_war_bow', 'heavy_steppe_arrows'], ['noble_bow', 'heavy_steppe_arrows'], ['noble_bow', 'heavy_steppe_arrows']) },
  },
  // Heavy crossbows ("crossbow" usage) cannot be reloaded on horseback; mounted rows use the light ones.
  crossbow: {
    '*': { mounted: L(['crossbow_e', 'bolt_b'], ['crossbow_g', 'bolt_b'], ['crossbow_g', 'bolt_c'], ['crossbow_g', 'bolt_c']),
      foot: L(['crossbow_b', 'bolt_b'], ['crossbow_c', 'bolt_b'], ['crossbow_d', 'bolt_c'], ['crossbow_f', 'bolt_c']) },
    empire: { mounted: L(['crossbow_e', 'bolt_b'], ['crossbow_g', 'bolt_b'], ['crossbow_g', 'bolt_c'], ['crossbow_g', 'bolt_c']),
      foot: L(['crossbow_b', 'bolt_d'], ['crossbow_d', 'bolt_b'], ['crossbow_d', 'bolt_c'], ['crossbow_f', 'bolt_c']) },
  },
  // Crowns (CK3 helmet slot): battle crowns / crowned helmets of the culture.
  crown: {
    vlandia: { mounted: L('western_crowned_helmet', 'western_crowned_helmet', 'western_crowned_plated_helmet', 'western_crowned_plated_helmet') },
    empire: { mounted: L('empire_crown', 'empire_battle_crown_north', 'empire_jewelled_helmet', 'empire_guarded_lord_helmet') },
    sturgia: { mounted: L('sturgian_crown', 'sturgian_crown', 'sturgian_battle_crown', 'sturgian_battle_crown') },
    battania: { mounted: L('battania_battle_crown', 'battania_battle_crown', 'battanian_crowned_helmet', 'battanian_crowned_helmet') },
    aserai: { mounted: L('aserai_crown', 'aserai_battle_crown', 'aserai_lord_helmet_a', 'aserai_lord_helmet_a') },
    khuzait: { mounted: L('khuzait_battle_crown', 'khuzait_battle_crown', 'khuzait_noble_helmet_with_feathers', 'khuzait_noble_helmet_with_neckguard') },
  },
};

/** CK3 artifact type -> mapping category used in the tables below. */
export const TYPE_TO_CATEGORY = {
  sword: 'sword', axe: 'axe', hammer: 'mace', mace: 'mace', spear: 'spear', dagger: 'dagger',
  bow: 'bow', longbow: 'longbow', composite: 'composite_bow', crossbow: 'crossbow',
  armor_mail: 'armor_mail', armor_plate: 'armor_plate', armor_scale: 'armor_scale', armor_lamellar: 'armor_lamellar',
  armor_laminar: 'armor_laminar', armor_brigandine: 'armor_brigandine',
  helmet: 'crown', helmet_simple: 'crown',
};

// ---------------------------------------------------------------------------------------------------------------
// Armour styles: CK3 armour type -> ordered Bannerlord styles (first that the culture has wins).
// Style of a Bannerlord body armour comes from its name (first matching rule).
// ---------------------------------------------------------------------------------------------------------------
export const ARMOR_STYLE_PREFERENCE = {
  armor_mail: ['mail'],
  armor_scale: ['scale', 'lamellar'],
  armor_lamellar: ['lamellar', 'scale'],
  armor_laminar: ['laminar', 'lamellar', 'scale'],
  armor_plate: ['plate', 'brigandine', 'scale'],
  armor_brigandine: ['brigandine', 'plate'],
};
function styleOf(s) {
  if (/brigandine/.test(s)) return 'brigandine';
  if (/coat of plates|coat_of_plates|breastplate|plated|plate armor|plates/.test(s)) return 'plate';
  if (/banded|strip/.test(s)) return 'laminar';
  if (/lamellar/.test(s)) return 'lamellar';
  if (/scale/.test(s)) return 'scale';
  if (/mail|hauberk|chain/.test(s)) return 'mail';
  return 'other';
}
/** Style of a Bannerlord body armour: from its display name, else from its id. */
export function bodyStyle(item) {
  const byName = styleOf(item.name.toLowerCase());
  return byName !== 'other' ? byName : styleOf(item.id.toLowerCase().replace(/_/g, ' '));
}
/** Every style keyword an armour mentions ("Bronze Lamellar over Mail" -> lamellar + mail); primary first. */
export function bodyStyles(item) {
  const primary = bodyStyle(item);
  const words = item.name.toLowerCase().split(/\s+over\s+|\s+with\s+|\s+and\s+/);
  const all = [primary, ...words.map(styleOf)].filter((s) => s !== 'other');
  return [...new Set(all)];
}
/** Body armour value targets per rarity (illustrious = the culture's best). INFERRED from the tier-4..6 spread. */
export const BODY_TARGET = { common: 30, masterwork: 38, famed: 45, illustrious: Infinity };
export const HEAD_TARGET = { common: 36, masterwork: 42, famed: 47, illustrious: Infinity };

/** CK3 armour helmet feature (armor_helmet group) -> Bannerlord helmet name pattern. */
export const HELMET_FEATURE_PATTERNS = {
  armor_helmet_spangenhelm: /spangenhelm/,
  armor_helmet_crested: /plumed|crest|feather/,
  armor_helmet_nasal: /nasal/,
  armor_helmet_enclosed: /closed|goggled|faceguard|facemask|visored/,
  armor_helmet_great_helmet: /full helm|visored|closed knight|faceguard/,
  armor_helmet_great_helmet_bascinet: /visored|full helm|faceguard/,
  armor_helmet_great_helmet_cervelliere: /full helm|cervelliere|visored/,
  armor_helmet_turban_helmet: /turban|wrapped/,
  armor_helmet_lamellar: /lamellar|lancer/,
  armor_helmet_cervelliere: /cervelliere|skullcap/,
  armor_helmet_kettle: /kettle/,
  armor_helmet_kulah_khud: /pointed|skullcap|spiked|casque|steel cap/,
};

// ---------------------------------------------------------------------------------------------------------------

/** Every <Item>/<CraftedItem> id of every module's ModuleData/items/*.xml. */
export function loadItemXml(blRoot) {
  const ids = new Map();
  const modules = path.join(blRoot, 'Modules');
  for (const mod of fs.readdirSync(modules, { withFileTypes: true })) {
    if (!mod.isDirectory()) continue;
    const dir = path.join(modules, mod.name, 'ModuleData', 'items');
    if (!fs.existsSync(dir)) continue;
    for (const f of fs.readdirSync(dir).filter((x) => x.endsWith('.xml')).sort()) {
      const text = fs.readFileSync(path.join(dir, f), 'utf8');
      const re = /<(Item|CraftedItem)\b([^>]*)>/gs;
      let m;
      while ((m = re.exec(text))) {
        const attrs = m[2];
        const id = (attrs.match(/\bid="([^"]+)"/) || [])[1];
        if (!id) continue;
        const type = (attrs.match(/\bType="([^"]+)"/) || [])[1] || null;
        const template = (attrs.match(/\bcrafting_template="([^"]+)"/) || [])[1] || null;
        if (!ids.has(id)) ids.set(id, { module: mod.name, file: `Modules/${mod.name}/ModuleData/items/${f}`, tag: m[1], type, crafting_template: template });
      }
    }
  }
  return ids;
}

/** id -> catalog record (weapons, armors, shields, ammo, horses). */
export function catalogIndex(catalog) {
  const idx = new Map();
  for (const [group, list] of Object.entries(catalog.items)) {
    if (!Array.isArray(list)) continue;
    for (const it of list) idx.set(it.id, { ...it, group });
  }
  return idx;
}

function armorCandidates(catalog, type) {
  return catalog.items.armors.filter((a) => a.type === type && a.culture && BL_CULTURES.includes(a.culture) && !/dummy/.test(a.id));
}

function closest(cands, value, target) {
  const sorted = [...cands].sort((a, b) => {
    if (target === Infinity) return value(b) - value(a) || (b.tierf ?? 0) - (a.tierf ?? 0) || a.id.localeCompare(b.id);
    return Math.abs(value(a) - target) - Math.abs(value(b) - target) || value(b) - value(a) || a.id.localeCompare(b.id);
  });
  return sorted[0] || null;
}

/** Body armour for (culture, CK3 armour type, rarity) with the reason it was chosen. */
/**
 * Scoring of body-armour candidates (lower is better):
 *   |body - goal| + NEIGHBOUR_PENALTY (item of a neighbour culture) + STYLE_RANK_PENALTY x rank of the style in
 *   ARMOR_STYLE_PREFERENCE + SECONDARY_PENALTY (the style is only a secondary keyword, e.g. "... over Mail").
 * goal = BODY_TARGET[rarity]; for illustrious the best body armour of the hero's own culture.
 */
export const ARMOR_SCORE = { NEIGHBOUR_PENALTY: 6, STYLE_RANK_PENALTY: 3, SECONDARY_PENALTY: 4 };
export const STYLE_GAP_OK = ARMOR_SCORE.NEIGHBOUR_PENALTY;

export function pickArmor(catalog, culture, armorType, rarity) {
  const all = armorCandidates(catalog, 'BodyArmor').filter((a) => a.armor.body >= 24);
  const prefs = ARMOR_STYLE_PREFERENCE[armorType];
  const own = all.filter((a) => a.culture === culture);
  const goal = BODY_TARGET[rarity] === Infinity ? Math.max(...own.map((a) => a.armor.body)) : BODY_TARGET[rarity];
  const scored = [];
  for (const a of all) {
    const neighbour = a.culture === culture ? 0 : NEIGHBOURS[culture].includes(a.culture) ? 1 : null;
    if (neighbour === null) continue;
    const styles = bodyStyles(a);
    let best = null;
    prefs.forEach((style, rank) => {
      const pos = styles.indexOf(style);
      if (pos < 0) return;
      const s = rank * ARMOR_SCORE.STYLE_RANK_PENALTY + (pos > 0 ? ARMOR_SCORE.SECONDARY_PENALTY : 0);
      if (!best || s < best.s) best = { s, style, secondary: pos > 0 };
    });
    if (!best) continue;
    const score = Math.abs(a.armor.body - goal) + neighbour * ARMOR_SCORE.NEIGHBOUR_PENALTY + best.s;
    scored.push({ a, score, ...best, neighbour });
  }
  scored.sort((x, y) => x.score - y.score || x.s - y.s || y.a.armor.body - x.a.armor.body || x.a.id.localeCompare(y.a.id));
  const top = scored[0];
  if (!top) {
    const pick = closest(own, (a) => a.armor.body, BODY_TARGET[rarity]);
    return { id: pick.id, why: 'culture body armour of any style (no style match)' };
  }
  const where = top.neighbour ? `neighbour ${top.a.culture}` : 'culture';
  return { id: top.a.id, why: `${where} ${top.style}${top.secondary ? ' (secondary keyword)' : ''} body armour, body ${top.a.armor.body}, goal ${goal}, score ${top.score}` };
}

const CROWN_LIKE = /crown|ceremonial|silk cap|jeweled band|laurel/;
/** Helmet for an armour artifact (culture, rarity, optional armor_helmet feature). */
export function pickHelmet(catalog, culture, rarity, feature = null) {
  const all = armorCandidates(catalog, 'HeadArmor').filter((a) => a.armor.head >= 28 && !CROWN_LIKE.test(`${a.name} ${a.id}`.toLowerCase()));
  const pattern = feature ? HELMET_FEATURE_PATTERNS[feature] : null;
  if (pattern) {
    for (const c of [culture, ...NEIGHBOURS[culture]]) {
      const cands = all.filter((a) => a.culture === c && pattern.test(a.name.toLowerCase()));
      if (!cands.length) continue;
      const pick = closest(cands, (a) => a.armor.head, HEAD_TARGET[rarity]);
      return { id: pick.id, why: `${c === culture ? 'culture' : `neighbour ${c}`} helmet matching ${feature}` };
    }
  }
  const pick = closest(all.filter((a) => a.culture === culture), (a) => a.armor.head, HEAD_TARGET[rarity]);
  return { id: pick.id, why: `culture helmet closest to head ${HEAD_TARGET[rarity] === Infinity ? 'max' : HEAD_TARGET[rarity]}` };
}

/** Bannerlord weapon class list of a catalog record (crafted usages included). */
export function classesOf(rec) {
  if (!rec) return [];
  if (rec.classes) return rec.classes;
  return [];
}
