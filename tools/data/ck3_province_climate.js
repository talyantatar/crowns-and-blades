#!/usr/bin/env node
// ck3_province_climate.js - per-province climate facts for battlefield scene selection (derived numbers only).
//
//   node ck3_province_climate.js --full-index <ck3_full_index.json from ck3_map_index.js --out>
//        [--game "<CK3 install>/game"]   (default: env CK3_GAME_DIR, else found in the Steam libraries)
//        [--out <repo>/bridge/data/ck3_province_climate.json]
//
// Reads the user's CK3 install (landed_titles + geographical_regions) plus the full province index
// (materials + tree kinds per province) and writes, per land province:
//   graphical region (CK3's own art region: graphical_western/mena/india/mediterranean/steppe/siberia/east_asia),
//   material biome   (dominant Gaea biome family of the province's terrain materials: gen_<biome>_*),
//   tree kind        (dominant generated map-tree kind: pine/leaf/palm/jungle/cypress/bush/reeds/sakura),
//   climate bucket   (the bridge's 8-way scene family, rules in climateBucket() below; INFERRED mapping).
// No game assets are copied.
'use strict';
const fs = require('fs');
const path = require('path');
// Repository root: the nearest folder above this script that holds tools/gen/lib/game_dirs.cjs, so the script works
// wherever it sits in the repository. game_dirs locates the game installs (env CK3_GAME_DIR / BL_DIR, else Steam).
const REPO = (() => {
  for (let d = __dirname; ; d = path.dirname(d)) {
    if (fs.existsSync(path.join(d, 'tools', 'gen', 'lib', 'game_dirs.cjs'))) return d;
    if (path.dirname(d) === d) throw new Error(`repository root not found above ${__dirname}`);
  }
})();
const gameDirs = require(path.join(REPO, 'tools', 'gen', 'lib', 'game_dirs.cjs'));
const SELF = gameDirs.repoRelative(__filename);
const DATA_DIR = path.join(REPO, 'bridge', 'data');
const args = process.argv.slice(2);
const arg = (k, d) => { const i = args.indexOf(k); return i >= 0 ? args[i + 1] : d; };
const G = arg('--game', null) || gameDirs.ck3GameDir();
const FULL = arg('--full-index', null);
const OUT = arg('--out', path.join(DATA_DIR, 'ck3_province_climate.json'));
if (!FULL) { console.error('need --full-index <path>'); process.exit(2); }

// ---------- minimal Paradox script parser: returns [ [key, value], ... ] where value is string | array ----------
function tokenize(src) {
  const t = []; const re = /#[^\n]*|"(?:[^"\\]|\\.)*"|[{}=]|[<>]=?|[^\s{}=#"<>]+/g; let m;
  while ((m = re.exec(src))) { if (m[0][0] !== '#') t.push(m[0]); }
  return t;
}
function parse(tokens) {
  let i = 0;
  function block() {
    const out = [];
    while (i < tokens.length) {
      const tk = tokens[i++];
      if (tk === '}') return out;
      if (tk === '{') { out.push([null, block()]); continue; }
      if (tokens[i] === '=' || tokens[i] === '<' || tokens[i] === '>' || tokens[i] === '<=' || tokens[i] === '>=') {
        i++;
        let v = tokens[i++];
        if (v === '{') v = block();
        else if ((v === 'hsv' || v === 'rgb' || v === 'hsv360') && tokens[i] === '{') { i++; v = block(); }
        out.push([tk, v]);
      } else out.push([tk, null]); // bare word inside a list
    }
    return out;
  }
  return block();
}
const readScript = f => parse(tokenize(fs.readFileSync(f, 'utf8').replace(/^\uFEFF/, '')));

// ---------- landed titles: province -> barony/county/duchy/kingdom/empire ----------
const provTitles = new Map(); // id -> {b,c,d,k,e}
const titleProvs = new Map(); // title key -> [province ids]
function walkTitles(entries, chain) {
  for (const [k, v] of entries) {
    if (!k || !Array.isArray(v)) continue;
    const m = /^([bcdkeh])_/.exec(k);
    if (!m) continue;
    const c2 = Object.assign({}, chain, { [m[1]]: k });
    if (m[1] === 'b') {
      const p = v.find(([kk]) => kk === 'province');
      if (p) { const id = +p[1]; provTitles.set(id, c2); for (const t of Object.values(c2)) { if (!titleProvs.has(t)) titleProvs.set(t, []); titleProvs.get(t).push(id); } }
    }
    walkTitles(v, c2);
  }
}
const ltDir = G + '/common/landed_titles';
for (const f of fs.readdirSync(ltDir).filter(f => f.endsWith('.txt')).sort()) walkTitles(readScript(path.join(ltDir, f)), {});

// ---------- geographical regions (all files in the folder; regions can reference regions) ----------
const regionDefs = new Map();
const grDir = G + '/map_data/geographical_regions';
for (const f of fs.readdirSync(grDir).filter(f => f.endsWith('.txt')).sort())
  for (const [k, v] of readScript(path.join(grDir, f))) if (k && Array.isArray(v)) regionDefs.set(k, v);
const regionCache = new Map();
function regionProvinces(name, stack = new Set()) {
  if (regionCache.has(name)) return regionCache.get(name);
  const def = regionDefs.get(name); const out = new Set();
  if (!def || stack.has(name)) return out;
  stack.add(name);
  for (const [k, v] of def) {
    if (!Array.isArray(v)) continue;
    const words = v.map(([kk]) => kk).filter(Boolean);
    if (k === 'provinces') words.forEach(w => out.add(+w));
    else if (k === 'regions') words.forEach(w => regionProvinces(w, stack).forEach(p => out.add(p)));
    else if (['duchies', 'counties', 'kingdoms', 'empires', 'baronies'].includes(k)) words.forEach(w => (titleProvs.get(w) || []).forEach(p => out.add(p)));
  }
  stack.delete(name);
  regionCache.set(name, out);
  return out;
}
const GRAPHICAL = [...regionDefs.entries()].filter(([, v]) => v.some(([k, x]) => k === 'graphical' && x === 'yes')).map(([k]) => k);
const provRegion = new Map(); const overlaps = [];
for (const g of GRAPHICAL) for (const p of regionProvinces(g)) { if (provRegion.has(p) && provRegion.get(p) !== g) overlaps.push([p, provRegion.get(p), g]); else provRegion.set(p, g); }

// ---------- material biome + tree kind from the full index ----------
const full = JSON.parse(fs.readFileSync(FULL, 'utf8'));
const BIOME_OF_MAT = [ // first match wins
  [/^gen_tropical_|^forest_jungle|^farm_paddy|^india_farmlands/, 'tropical'],
  [/^gen_northern_|^northern_plains|^forest_pine/, 'northern'],
  [/^gen_mediterranean_|^medi_|^hills_01_rocks_medi|^beach_02_mediterranean/, 'mediterranean'],
  [/^gen_steppe_|^steppe_/, 'steppe'],
  [/^gen_desert_|^desert_|^coastline_cliff_desert|^oasis/, 'desert'],
  [/^gen_drylands_|^drylands_/, 'drylands'],
  [/^gen_central_|^central_|^forest_leaf|^farmland_01|^plains_01|^wetlands_02/, 'central'],
];
const TREE_KIND = n => /jungle/.test(n) ? 'jungle' : /palm/.test(n) ? 'palm' : /cypress/.test(n) ? 'cypress' : /pine/.test(n) ? 'pine' : /leaf/.test(n) ? 'leaf' : /sakura/.test(n) ? 'sakura' : /bush/.test(n) ? 'bush' : /reed/.test(n) ? 'reeds' : 'other';
function biomeOf(mats) {
  const acc = {};
  for (const [m, w] of mats || []) { const hit = BIOME_OF_MAT.find(([re]) => re.test(m)); if (hit) acc[hit[1]] = (acc[hit[1]] || 0) + w; }
  const best = Object.entries(acc).sort((a, b) => b[1] - a[1])[0];
  return best ? best[0] : null;
}
function treeKindOf(trees) {
  const acc = {}; for (const [n, c] of Object.entries(trees || {})) { const k = TREE_KIND(n); acc[k] = (acc[k] || 0) + c; }
  const best = Object.entries(acc).sort((a, b) => b[1] - a[1])[0];
  return best ? best[0] : null;
}

// ---------- climate bucket (the scene family the bridge selects from) [INFERRED rules] ----------
// buckets: temperate | boreal | mediterranean | arid | steppe | tropical | highland | east_asia
// First matching rule wins. Inputs are all static CK3 data (terrain key, winter_severity_bias, graphical region,
// dominant Gaea material biome, dominant generated map-tree kind).
const DESERTISH = new Set(['desert', 'desert_mountains', 'oasis']);
const COLD_ASIA = new Set(['graphical_india', 'graphical_east_asia', 'graphical_steppe']);
function climateBucket(p, region, biome, tree) {
  const t = p.terrain, wb = p.winterBias ?? 0.1;
  if (region === 'graphical_mena' && t !== 'jungle' && (t === 'floodplains' || tree === 'palm')) return 'arid'; // Nile, Mesopotamia, palm oases
  if (t === 'jungle' || biome === 'tropical' || tree === 'jungle') return 'tropical';            // W. Africa, India, SE Asia, S. China
  if (DESERTISH.has(t)) return 'arid';
  if (t === 'taiga' || biome === 'northern' || region === 'graphical_siberia') return 'boreal';
  if (tree === 'pine' && wb >= 0.6 && region !== 'graphical_mediterranean') return 'boreal';   // Baltic, Muscovy, S. Scandinavia
  if (region === 'graphical_steppe' || t === 'steppe' || biome === 'steppe') return wb >= 0.8 && t !== 'steppe' ? 'boreal' : 'steppe';
  if (biome === 'desert') return 'arid';
  if (biome === 'drylands' || t === 'drylands') return wb >= 0.5 && COLD_ASIA.has(region) ? 'highland' : 'arid'; // Tibet/Pamir vs Levant/Iran
  if (region === 'graphical_mena') return 'mediterranean';                                       // green MENA: Caspian coast, Levant hills, Canaries
  if (region === 'graphical_india') return wb >= 0.5 ? 'highland' : 'tropical';
  if (region === 'graphical_east_asia') return wb >= 0.8 ? 'boreal' : 'east_asia';
  if (region === 'graphical_mediterranean' || biome === 'mediterranean' || tree === 'cypress')
    return (biome === 'central' && wb >= 0.4) || wb >= 0.5 ? 'temperate' : 'mediterranean';   // Alps/Balkan interior -> temperate
  if (wb >= 0.8) return 'boreal';
  return 'temperate';
}

const REGIONS = GRAPHICAL.slice().sort();
const BIOMES = ['central', 'northern', 'mediterranean', 'steppe', 'desert', 'drylands', 'tropical'];
const TREES = ['leaf', 'pine', 'palm', 'jungle', 'cypress', 'bush', 'reeds', 'sakura', 'other'];
const BUCKETS = ['temperate', 'boreal', 'mediterranean', 'arid', 'steppe', 'tropical', 'highland', 'east_asia'];
const rows = []; const stats = { land: 0, noRegion: 0, noBiome: 0, byBucket: {}, byRegion: {}, terrainByBucket: {} };
for (const [idStr, p] of Object.entries(full.provinces)) {
  if (p.kind !== 'land') continue; // impassable/sea/lake/river provinces never host a land battle
  const id = +idStr; stats.land++;
  const region = provRegion.get(id) || null, biome = biomeOf(p.mats), tree = treeKindOf(p.trees);
  if (!region) stats.noRegion++; if (!biome) stats.noBiome++;
  const bucket = climateBucket(p, region, biome, tree);
  stats.byBucket[bucket] = (stats.byBucket[bucket] || 0) + 1;
  stats.byRegion[region] = (stats.byRegion[region] || 0) + 1;
  const tb = stats.terrainByBucket[p.terrain] || (stats.terrainByBucket[p.terrain] = {}); tb[bucket] = (tb[bucket] || 0) + 1;
  const t = provTitles.get(id) || {};
  rows.push([id, region ? REGIONS.indexOf(region) : -1, biome ? BIOMES.indexOf(biome) : -1, tree ? TREES.indexOf(tree) : -1, BUCKETS.indexOf(bucket), t.c || null]);
}
rows.sort((a, b) => a[0] - b[0]);
const out = {
  meta: {
    generated: new Date().toISOString(), generatedFrom: '<CK3 install>/game', fullIndex: path.basename(FULL),
    note: `Derived ids only. Regenerate with ${SELF}. climateBucket rules are INFERRED.`,
    regions: REGIONS, biomes: BIOMES, trees: TREES, buckets: BUCKETS,
    cols: ['id', 'graphicalRegion(idx|-1)', 'materialBiome(idx|-1)', 'treeKind(idx|-1)', 'climateBucket(idx)', 'county'],
    overlaps: overlaps.length,
  },
  rows,
};
fs.writeFileSync(OUT, JSON.stringify(out));
console.error('land provinces', stats.land, 'without graphical region', stats.noRegion, 'without biome', stats.noBiome, 'region overlaps', overlaps.length);
console.error('by region', JSON.stringify(stats.byRegion));
console.error('by bucket', JSON.stringify(stats.byBucket));
for (const [t, b] of Object.entries(stats.terrainByBucket).sort()) console.error(t.padEnd(17), JSON.stringify(b));
console.error('wrote', OUT, fs.statSync(OUT).size, 'bytes');
