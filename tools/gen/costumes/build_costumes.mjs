#!/usr/bin/env node
// Builds bridge/data/ck3xbl_costumes.json (docs/PROTOCOL.md 21): every wearable item of a Bannerlord install, Open Source Armory
// and Open Source Saddlery included when they sit in Modules, with its slot, armour, earliest CK3 era and role tags, and for every
// CK3 culture profile of mapping.json the items that dress it, with a weight.
//
//   node tools/gen/costumes/build_costumes.mjs [--bannerlord <dir>] [--check]
//
// The Bannerlord folder comes from --bannerlord, else BANNERLORD_DIR. The output lists items of optional modules too: the
// Bannerlord module skips ids the running game does not have, so the file works with and without the armory mods.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { scanInstall } from './scan_items.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..', '..', '..');
const OUT = path.join(ROOT, 'bridge', 'data', 'ck3xbl_costumes.json');
const MAPPING = path.join(ROOT, 'bridge', 'data', 'mapping.json');

/** Optional item modules looked for in <Bannerlord>/Modules. */
export const OPTIONAL_MODULES = [
  'OpenSourceArmory', 'OpenSourceSaddlery',
  // Terra Armarium (Byzantine, Rus, late medieval; the core first), as the bridge's launch list loads them.
  'BL.Terra_Armarium.UniversalCore', 'BL.Terra_Armarium.Byzantine', 'BL.Terra_Armarium.Rus&Mongols', 'BL.Terra_Armarium.LateMedieval',
];

/** Culture ids item modules misspell, read as the culture they mean (Terra Armarium's Rus pack: 'strugia'). */
const CULTURE_TYPOS = { strugia: 'sturgia' };

// ---- item tags ---------------------------------------------------------------------------------------------------------

const words = (s) => s.toLowerCase().split(/[^a-z]+/).filter(Boolean);
const any = (ws, set) => ws.some((w) => set.has(w));
const set = (s) => new Set(s.split(/\s+/).filter(Boolean));

// Never on a CK3 field: classical Rome, the arena, fantasy, regalia, women's dress, pack animals.
const BANNED = set(`legionary segmented gladiator secutor provocator pteruges toga laurel aeneas fortunas satan solar crown crowned
  circlet tiara dummy female feminine ladies dress veiled hijab huntress shaman merchant scholar pilgrim monk mule load baskets
  bags pack wicker ceremonial`);
const BANNED_ID = /roman|gladiator|legion|tournament|_female|crown|tiara|monk|merchant|satan|solar/i;
// Earliest CK3 era by look: plate and visors are late medieval, great helms, kettle hats and surcoats high medieval.
const LATE = set('plate plates brigandine visor visored faceplate gauntlets breastplate');
const HIGH = set('closed kettle roundkettle cervelliere cevelliere tabard great');
// Role tags: what a levy, a rider or an archer would rather wear among items of the same armour.
const LEVY = set('commoner peasant rural villager simple rough ragged crude patched hemp burlap torn woolen roughspun sackcloth rags tattered plain cheap');
const CAV = set('cavalry cavalryman lancer cataphract cavalier riding knight knightly mailed');
const RNG = set('archer ranger scout sagittarius hood hooded');

export const ERA = { tribal: 0, early: 1, high: 2, late: 3 };

export function itemTags(item) {
  const ws = words(item.name + ' ' + item.id.replace(/_/g, ' '));
  const tags = [];
  if (any(ws, LEVY)) tags.push('levy');
  if (any(ws, CAV)) tags.push('cav');
  if (any(ws, RNG)) tags.push('rng');
  return tags;
}

export function itemEra(item) {
  const ws = words(item.name);
  if (any(ws, LATE) || /full helm/i.test(item.name)) return ERA.late;
  if (any(ws, HIGH)) return ERA.high;
  return ERA.tribal;
}

export function isBanned(item) {
  if (item.culture === 'looters') return true;
  if (BANNED_ID.test(item.id)) return true;
  return any(words(item.name), BANNED);
}

// ---- looks -------------------------------------------------------------------------------------------------------------

const COLD = set('fur furred pelt bearskin wolfskin wolf bear snow cold boarskin');
const HOT = set('desert turban turbaned keffiyeh headwrap headscarf tuareg bedouin thawb bisht');
const NORTH = { avoid: HOT };
const SOUTH = { avoid: COLD };

/**
 * The look of each CK3 culture profile: Bannerlord cultures and their share (neutral items always join at 0.3), words that
 * make an item more likely (x2.5) and words that rule it out.
 */
export const LOOKS = {
  default: { cultures: { empire: 1 } },
  western_feudal: { cultures: { vlandia: 1 }, prefer: 'western kettle nasal knightly cavalier knight angevin', ...NORTH },
  iberian_christian: { cultures: { vlandia: 0.7, aserai: 0.3 }, prefer: 'western' },
  andalusian: { cultures: { aserai: 0.7, vlandia: 0.3 }, prefer: 'southern', ...SOUTH },
  norse: { cultures: { sturgia: 1, vlandia: 0.15 }, prefer: 'nordic vendel northern goggled nord ulfhednar berserkr huscarl', ...NORTH },
  east_slavic: { cultures: { sturgia: 0.8, khuzait: 0.2 }, prefer: 'varyag vaegir sloven spiked northern', ...NORTH },
  west_slavic: { cultures: { sturgia: 0.6, vlandia: 0.4 }, prefer: 'sloven', ...NORTH },
  balkan: { cultures: { empire: 0.5, sturgia: 0.5 } },
  byzantine: { cultures: { empire: 1 }, prefer: 'cataphract palatine imperial', avoid: COLD },
  caucasian: { cultures: { empire: 0.4, aserai: 0.3, khuzait: 0.3 } },
  arab: { cultures: { aserai: 1 }, prefer: 'mamluke emir bedouin keffiyeh southern', ...SOUTH },
  berber: { cultures: { aserai: 1 }, prefer: 'tuareg desert', ...SOUTH },
  east_african: { cultures: { aserai: 1 }, prefer: 'leopard zebra', ...SOUTH },
  sahel: { cultures: { aserai: 1 }, prefer: 'tuareg desert leopard', ...SOUTH },
  african_forest: { cultures: { aserai: 1, battania: 0.2 }, prefer: 'leopard zebra', ...SOUTH },
  persian: { cultures: { aserai: 0.6, khuzait: 0.4 }, prefer: 'immortal', ...SOUTH },
  steppe: { cultures: { khuzait: 1 }, prefer: 'steppe nomad khan' },
  hungarian: { cultures: { khuzait: 0.6, vlandia: 0.4 }, prefer: 'steppe' },
  tibetan: { cultures: { khuzait: 0.8, aserai: 0.2 } },
  east_asian: { cultures: { khuzait: 1 }, prefer: 'lamellar', avoid: COLD },
  japanese: { cultures: { khuzait: 1 }, prefer: 'lamellar', avoid: COLD },
  emishi: { cultures: { khuzait: 0.7, battania: 0.3 } },
  siberian: { cultures: { khuzait: 0.5, sturgia: 0.5 }, prefer: 'fur furred pelt', ...NORTH },
  indian: { cultures: { aserai: 1 }, ...SOUTH },
  southeast_asian: { cultures: { aserai: 1 }, ...SOUTH },
  baltic_finnic: { cultures: { sturgia: 0.6, battania: 0.4 }, prefer: 'northern', ...NORTH },
  celtic: { cultures: { battania: 1 }, prefer: 'highland plaid tartan pict kilt', ...NORTH },
};

/**
 * Looks of the CK3 men-at-arms with a culture of their own (docs/PROTOCOL.md 35), keyed "maa:<CK3 MaA key>": a regiment of them
 * wears these instead of its culture's look. Their words weigh much more (UNIQUE_PREFER_FACTOR), so the regiment stands out.
 */
const ELITE = { preferFactor: 6 };
export const UNIQUE_LOOKS = {
  huscarl: { cultures: { sturgia: 1, vlandia: 0.2 }, prefer: 'nordic northern huscarl vendel goggled', ...NORTH, ...ELITE },
  varangian_guards: { cultures: { sturgia: 0.6, empire: 0.4 }, prefer: 'varyag varangian scale lamellar', ...ELITE },
  varangian_veterans: { cultures: { sturgia: 0.7, empire: 0.3 }, prefer: 'varyag varangian northern', ...ELITE },
  vigmen: { cultures: { sturgia: 1 }, prefer: 'nordic northern raider', ...NORTH, ...ELITE },
  jomsviking_pirates: { cultures: { sturgia: 1 }, prefer: 'nordic northern raider goggled', ...NORTH, ...ELITE },
  bondi: { cultures: { sturgia: 1 }, prefer: 'northern nordic', ...NORTH, ...ELITE },
  druzhina: { cultures: { sturgia: 1, khuzait: 0.2 }, prefer: 'druzhinnik varyag spiked lamellar', ...NORTH, ...ELITE },
  cataphract: { cultures: { empire: 1 }, prefer: 'cataphract scale lamellar palatine', avoid: COLD, ...ELITE },
  cataphract_archers: { cultures: { empire: 0.6, khuzait: 0.4 }, prefer: 'cataphract scale lamellar', avoid: COLD, ...ELITE },
  skoutatoi: { cultures: { empire: 1 }, prefer: 'imperial scale', avoid: COLD, ...ELITE },
  akritai: { cultures: { empire: 0.6, khuzait: 0.4 }, prefer: 'lamellar imperial', avoid: COLD, ...ELITE },
  praetorian: { cultures: { empire: 1 }, prefer: 'palatine guard imperial', avoid: COLD, ...ELITE },
  monaspa: { cultures: { empire: 0.5, aserai: 0.3, khuzait: 0.2 }, prefer: 'scale lamellar', ...ELITE },
  ayrudzi: { cultures: { empire: 0.4, aserai: 0.3, khuzait: 0.3 }, prefer: 'lamellar scale', ...ELITE },
  mubarizun: { cultures: { aserai: 1 }, prefer: 'champion emir mamluke', ...SOUTH, ...ELITE },
  mulaththamun: { cultures: { aserai: 1 }, prefer: 'tuareg veil veiled desert', ...SOUTH, ...ELITE },
  ayyar: { cultures: { aserai: 1 }, prefer: 'desert bedouin', ...SOUTH, ...ELITE },
  asawira: { cultures: { aserai: 0.5, khuzait: 0.5 }, prefer: 'immortal scale lamellar', ...SOUTH, ...ELITE },
  camel_rider: { cultures: { aserai: 1 }, prefer: 'camel desert bedouin', ...SOUTH, ...ELITE },
  war_elephant: { cultures: { aserai: 1 }, prefer: 'scale', ...SOUTH, ...ELITE },
  caballero: { cultures: { vlandia: 0.6, aserai: 0.4 }, prefer: 'cavalier knightly', ...ELITE },
  gendarme: { cultures: { vlandia: 1 }, prefer: 'plate knight knightly visor', ...ELITE },
  conrois: { cultures: { vlandia: 1 }, prefer: 'knight knightly cavalier', ...NORTH, ...ELITE },
  landsknecht: { cultures: { vlandia: 1 }, prefer: 'padded slashed', ...NORTH, ...ELITE },
  order_knights: { cultures: { vlandia: 1 }, prefer: 'knight knightly tabard crusader templar', ...ELITE },
  order_serjeants: { cultures: { vlandia: 1 }, prefer: 'tabard crusader sergeant', ...ELITE },
  longbowmen: { cultures: { battania: 0.5, vlandia: 0.5 }, prefer: 'archer hood hooded', ...NORTH, ...ELITE },
  schiltron: { cultures: { battania: 1 }, prefer: 'highland', ...NORTH, ...ELITE },
  teulu: { cultures: { battania: 1 }, prefer: 'highland mail', ...NORTH, ...ELITE },
  hussar: { cultures: { khuzait: 0.5, vlandia: 0.5 }, prefer: 'winged lamellar', ...ELITE },
  kheshig: { cultures: { khuzait: 1 }, prefer: 'khan guard lamellar', ...ELITE },
  tarkhan: { cultures: { khuzait: 1 }, prefer: 'lamellar khan', ...ELITE },
  mangudai: { cultures: { khuzait: 1 }, prefer: 'steppe nomad', ...ELITE },
  heavy_horse_archers: { cultures: { khuzait: 1 }, prefer: 'lamellar steppe', ...ELITE },
  steppe_raiders: { cultures: { khuzait: 1 }, prefer: 'steppe nomad raider', ...ELITE },
  nomadic_riders: { cultures: { khuzait: 1 }, prefer: 'steppe nomad', ...ELITE },
  nomad_lancers: { cultures: { khuzait: 1 }, prefer: 'lancer steppe', ...ELITE },
  samurai: { cultures: { khuzait: 1 }, prefer: 'lamellar', avoid: COLD, ...ELITE },
  warrior_monks: { cultures: { khuzait: 1 }, prefer: 'robe', avoid: COLD, ...ELITE },
  palace_guards: { cultures: { khuzait: 0.5, empire: 0.5 }, prefer: 'guard lamellar', avoid: COLD, ...ELITE },
};

export const NEUTRAL_SHARE = 0.3;
export const PREFER_FACTOR = 2.5;

export function lookWeights(look, items) {
  const prefer = look.prefer ? set(look.prefer) : new Set();
  const avoid = look.avoid ?? new Set();
  const preferFactor = look.preferFactor ?? PREFER_FACTOR;
  const out = {};
  for (const it of items) {
    const neutral = it.culture === 'neutral' || it.culture === 'neutral_culture';
    let w = neutral ? NEUTRAL_SHARE : look.cultures[it.culture] ?? 0;
    if (w <= 0) continue;
    const ws = words(it.name + ' ' + it.id.replace(/_/g, ' '));
    if (any(ws, avoid)) continue;
    if (any(ws, prefer)) w *= preferFactor;
    out[it.id] = Math.round(w * 100) / 100;
  }
  return out;
}

// ---- build -------------------------------------------------------------------------------------------------------------

export function buildCostumes(blDir) {
  const modulesDir = path.join(blDir, 'Modules');
  const extras = OPTIONAL_MODULES.map((m) => path.join(modulesDir, m)).filter((d) => fs.existsSync(path.join(d, 'SubModule.xml')));
  const scanned = scanInstall(blDir, extras).filter((it) => !isBanned(it))
    .map((it) => (CULTURE_TYPOS[it.culture] ? { ...it, culture: CULTURE_TYPOS[it.culture] } : it));
  const items = scanned
    .map((it) => ({
      id: it.id, slot: it.slot, culture: it.culture, armor: it.total, era_min: itemEra(it), family: it.family,
      tags: itemTags(it), source: it.source === 'vanilla' ? 'vanilla' : it.source.split(':')[0],
    }))
    .sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
  const mapping = JSON.parse(fs.readFileSync(MAPPING, 'utf8'));
  const looks = {};
  for (const profile of Object.keys(mapping.profiles).sort()) {
    const look = LOOKS[profile];
    if (!look) throw new Error(`no look for mapping.json profile ${profile} (tools/gen/costumes/build_costumes.mjs LOOKS)`);
    looks[profile] = lookWeights(look, scanned);
  }
  for (const maa of Object.keys(UNIQUE_LOOKS).sort()) looks[`maa:${maa}`] = lookWeights(UNIQUE_LOOKS[maa], scanned);
  return {
    meta: {
      generator: 'tools/gen/costumes/build_costumes.mjs',
      modules: ['SandBoxCore', ...extras.map((d) => path.basename(d))],
      notes: [
        'items: armour = sum of the Armor attributes; era_min 0 tribal .. 3 late medieval; tags levy/cav/rng prefer that role.',
        'looks: CK3 profile -> item id -> weight. The bridge filters by era and role, the module by armour and by what the game loaded.',
      ],
    },
    items,
    looks,
  };
}

/** One item and one look per line: diffable, a third of the indented size. */
function format(data) {
  const lines = ['{', ` "meta": ${JSON.stringify(data.meta)},`, ' "items": ['];
  data.items.forEach((it, i) => lines.push(`  ${JSON.stringify(it)}${i < data.items.length - 1 ? ',' : ''}`));
  lines.push(' ],', ' "looks": {');
  const keys = Object.keys(data.looks);
  keys.forEach((k, i) => lines.push(`  ${JSON.stringify(k)}: ${JSON.stringify(data.looks[k])}${i < keys.length - 1 ? ',' : ''}`));
  lines.push(' }', '}', '');
  return lines.join('\n');
}

function main() {
  const args = process.argv.slice(2);
  const at = args.indexOf('--bannerlord');
  const blDir = at >= 0 ? args[at + 1] : process.env.BANNERLORD_DIR;
  if (!blDir) {
    console.error('build_costumes: give --bannerlord <dir> or set BANNERLORD_DIR');
    process.exit(2);
  }
  const text = format(buildCostumes(blDir));
  if (args.includes('--check')) {
    const old = fs.existsSync(OUT) ? fs.readFileSync(OUT, 'utf8') : '';
    if (old !== text) {
      console.error(`build_costumes: ${path.relative(ROOT, OUT)} is stale; run without --check`);
      process.exit(1);
    }
    console.log('build_costumes: up to date');
    return;
  }
  fs.writeFileSync(OUT, text);
  const data = JSON.parse(text);
  console.log(`build_costumes: ${data.items.length} items, ${Object.keys(data.looks).length} looks -> ${path.relative(ROOT, OUT)}`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
