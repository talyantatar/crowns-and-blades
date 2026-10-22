#!/usr/bin/env node
// validate_terrain_scenes.js - checks bridge/data/terrain_scenes.json against bl_scenes.json, the Bannerlord install
// (scene.xscene + atmosphere files exist) and the CK3 install (every terrain / holding / building_gfx key is covered),
// then runs the Tier-1 resolver over every CK3 land province x 12 months and reports scene usage.
//
//   node validate_terrain_scenes.js [--full-index <ck3_map_index.js --out file>]   (full index enables crossing examples)
// Read-only on both game installs. Exit code 1 on any ERROR.
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
const R = require('./battlefield_resolver.js');
const args = process.argv.slice(2);
const arg = (k, d) => { const i = args.indexOf(k); return i >= 0 ? args[i + 1] : d; };
const DATA = DATA_DIR;
const BL = gameDirs.bannerlordDir();
const CK3 = gameDirs.ck3GameDir();
const FULL = arg('--full-index', null);

const errors = [], warns = [], infos = [];
const E = m => errors.push(m), W = m => warns.push(m), I = m => infos.push(m);
const ts = JSON.parse(fs.readFileSync(path.join(DATA, 'terrain_scenes.json'), 'utf8'));
const scenes = JSON.parse(fs.readFileSync(path.join(DATA, 'bl_scenes.json'), 'utf8'));
const byId = new Map(scenes.map(s => [s.id, s]));
const catalog = JSON.parse(fs.readFileSync(path.join(DATA, 'ck3_catalog.json'), 'utf8'));
const xsceneOk = s => fs.existsSync(`${BL}/Modules/${s.module}/SceneObj/${s.id}/scene.xscene`);
const strip = t => t.replace(/^﻿/, '').replace(/#[^\n]*/g, '');

// ---------- 1. field lists ----------
const exclude = new Set(ts.rules.exclude_unlisted);
const checkField = (where, list) => {
  if (!Array.isArray(list) || !list.length) { E(`${where}: empty list`); return; }
  const seen = new Set();
  for (const id of list) {
    const s = byId.get(id);
    if (seen.has(id)) W(`${where}: duplicate ${id}`); seen.add(id);
    if (!s) { E(`${where}: ${id} not in bl_scenes.json`); continue; }
    if (s.kind !== 'field') E(`${where}: ${id} kind=${s.kind}, expected field`);
    if (!s.battle_capable || !s.battle_capable.field) E(`${where}: ${id} not field-battle capable`);
    if (!(s.spawn_paths >= 1)) E(`${where}: ${id} has no spawn_path_NN`);
    if (!xsceneOk(s)) E(`${where}: ${id} scene.xscene missing on disk`);
    if (exclude.has(id)) W(`${where}: ${id} is in rules.exclude_unlisted`);
  }
};
const terrainKeys = [...strip(fs.readFileSync(`${CK3}/common/terrain_types/00_terrains.txt`, 'utf8')).matchAll(/^([a-z_]+)\s*=\s*\{/gm)].map(m => m[1]);
for (const k of terrainKeys) if (!ts.terrain[k]) E(`CK3 terrain '${k}' (00_terrains.txt) has no entry`);
for (const [k, v] of Object.entries(ts.terrain)) {
  if (!terrainKeys.includes(k)) E(`terrain '${k}' is not a CK3 1.20 terrain key`);
  if (!v.scenes.any) E(`terrain ${k}: no 'any' list`);
  if (!ts.bl_terrain_type_ids[v.bl_terrain_type]) E(`terrain ${k}: unknown bl_terrain_type ${v.bl_terrain_type}`);
  for (const [b, l] of Object.entries(v.scenes)) { if (b !== 'any' && !ts.climate.buckets.includes(b)) E(`terrain ${k}: unknown bucket ${b}`); checkField(`terrain.${k}.${b}`, l); }
}
for (const [k, v] of Object.entries(ts.situational))
  for (const [b, l] of Object.entries(v)) {
    if (b === 'by_terrain') { for (const [t, l2] of Object.entries(l)) { if (!terrainKeys.includes(t)) E(`situational.${k}.by_terrain: bad terrain ${t}`); checkField(`situational.${k}.by_terrain.${t}`, l2); } }
    else if (b === 'by_terrain_buckets') { for (const x of l) if (!ts.climate.buckets.includes(x)) E(`situational.${k}.by_terrain_buckets: bad bucket ${x}`); }
    else { if (b !== 'any' && !ts.climate.buckets.includes(b)) E(`situational.${k}: unknown bucket ${b}`); checkField(`situational.${k}.${b}`, l); }
  }
checkField('rules.fallback_scene', [ts.rules.fallback_scene]);

// ---------- 2. siege + village lists ----------
for (const [c, kinds] of Object.entries(ts.siege.scenes))
  for (const [k, list] of Object.entries(kinds)) {
    const tested = list.filter(id => !ts.siege.untested_scenes.includes(id));
    if (!tested.length) E(`siege.${c}.${k}: no custom-battle-tested scene`);
    for (const id of list) {
      const s = byId.get(id);
      if (!s) { E(`siege.${c}.${k}: ${id} missing`); continue; }
      if (s.kind !== 'siege' || s.culture !== c || (k === 'castle') !== (s.subkind === 'castle')) E(`siege.${c}.${k}: ${id} is ${s.kind}/${s.subkind}/${s.culture}`);
      for (const lv of ['level_1', 'level_2', 'level_3', 'siege']) if (!(s.levels || []).includes(lv)) E(`siege ${id}: no scene level ${lv}`);
      if (!s.siege || !(s.siege.wall_segments > 0) || !(s.siege.castle_gates > 0)) E(`siege ${id}: no WallSegment/CastleGate`);
      if (!xsceneOk(s)) E(`siege ${id}: scene.xscene missing on disk`);
    }
  }
for (const [c, list] of Object.entries(ts.village.scenes)) {
  if (!list.length) E(`village.${c}: empty`);
  for (const id of list) { const s = byId.get(id); if (!s || s.kind !== 'village' || s.culture !== c) E(`village.${c}: bad ${id}`); else if (!xsceneOk(s)) E(`village ${id}: scene.xscene missing on disk (empty SceneObj folder)`); else if (!s.custom_battle) W(`village ${id}: not in custom_battle_scenes.xml`); }
}
// holdings + architecture coverage against the CK3 install / catalog
const holdings = [...strip(fs.readFileSync(`${CK3}/common/holdings/00_holdings.txt`, 'utf8')).matchAll(/^([a-z_]+)\s*=\s*\{/gm)].map(m => m[1]);
for (const h of holdings) if (!ts.siege.holding_kind[h]) E(`holding ${h} (00_holdings.txt) not mapped`);
const gfxUsed = new Set(); for (const c of Object.values(catalog.cultures)) for (const g of c.building_gfx || []) gfxUsed.add(g);
for (const g of gfxUsed) { if (!ts.siege.architecture.building_gfx[g]) E(`building_gfx ${g} not mapped`); if (!ts.siege.architecture.ck3_chain_order.includes(g)) E(`building_gfx ${g} missing from ck3_chain_order`); }
const heritages = new Set(Object.values(catalog.cultures).map(c => c.heritage));
for (const h of Object.keys(ts.siege.architecture.heritage_override_for_western)) if (!heritages.has(h)) E(`heritage override ${h} is not a CK3 heritage`);
const archCultures = new Set(Object.values(ts.siege.architecture.building_gfx).flatMap(v => (typeof v === 'object' ? Object.values(v) : [v])));
for (const c of archCultures) if (!ts.siege.scenes[c] || !ts.village.scenes[c]) E(`architecture culture ${c} has no siege/village scenes`);

// ---------- 3. atmosphere names exist ----------
const ATM = `${BL}/Modules/Native/Atmospheres`;
for (const r of ts.time_of_day.presets_by_hour) for (const k of ['clear', 'fog', 'overcast', 'rain']) if (r[k] && !fs.existsSync(`${ATM}/${r[k]}.xml`)) E(`preset ${r[k]} missing`);
for (const [w, a] of Object.entries(ts.weather.atmosphere)) if (!fs.existsSync(`${ATM}/Interpolated/${a.interpolated}.xml`)) E(`weather ${w}: interpolated ${a.interpolated} missing`);
for (const g of Object.values(ts.weather.group_of_bucket)) for (const s of ['spring', 'summer', 'fall', 'winter']) { const row = ts.weather.table[g][s]; const sum = Object.values(row).reduce((a, b) => a + b, 0); if (Math.abs(sum - 1) > 1e-6) E(`weather.table.${g}.${s} sums to ${sum}`); for (const k of Object.keys(row)) if (!ts.weather.kinds.includes(k)) E(`weather kind ${k}`); }

// ---------- 4. whole-map simulation ----------
const T = R.loadTables({ fullIndex: FULL });
const use = new Map(), srcCount = {}, cellCands = new Map(), seasonCount = {}, weatherCount = {};
let runs = 0, fails = 0;
for (const [id, p] of T.prov) {
  if (p.flags & 8) continue; // impassable
  if (!T.climate.has(id)) continue; // not a land province with a climate row
  for (let m = 1; m <= 12; m++) {
    let out;
    try { out = R.resolveBattlefield(T, { provinceId: id, date: `1100.${m}.15`, agents: m % 2 ? 400 : 900 }); } catch (e) { fails++; if (fails < 5) E(`resolve ${id}: ${e.message}`); continue; }
    runs++;
    if (!out.scene) { fails++; E(`no scene for ${id} month ${m}`); continue; }
    use.set(out.scene, (use.get(out.scene) || 0) + 1);
    const src = out.why[1].replace(/^list: (\S+).*/, '$1'); srcCount[src] = (srcCount[src] || 0) + 1;
    seasonCount[out.atmosphere.season] = (seasonCount[out.atmosphere.season] || 0) + 1;
    weatherCount[out.atmosphere.weather] = (weatherCount[out.atmosphere.weather] || 0) + 1;
    const cell = `${p.terrain}/${out.site.climate}`; if (!cellCands.has(cell)) cellCands.set(cell, out.candidates.length);
  }
}
for (const [cell, n] of cellCands) if (n < 3) W(`cell ${cell}: only ${n} candidates`);
const fieldIds = scenes.filter(s => s.kind === 'field' && !exclude.has(s.id)).map(s => s.id);
const unused = fieldIds.filter(id => !use.has(id));
const sorted = [...use.entries()].sort((a, b) => b[1] - a[1]);
I(`simulation: ${runs} battles (land provinces x 12 months), ${fails} failures, ${use.size} distinct field scenes used of ${fieldIds.length} listed`);
I(`top scenes: ${sorted.slice(0, 8).map(([k, v]) => `${k.replace('battle_terrain_', '')} ${(100 * v / runs).toFixed(1)}%`).join(', ')}`);
I(`list source: ${Object.entries(srcCount).map(([k, v]) => `${k} ${(100 * v / runs).toFixed(1)}%`).join(', ')}`);
I(`season: ${Object.entries(seasonCount).map(([k, v]) => `${k} ${(100 * v / runs).toFixed(1)}%`).join(', ')}`);
I(`weather: ${Object.entries(weatherCount).map(([k, v]) => `${k} ${(100 * v / runs).toFixed(1)}%`).join(', ')}`);
I(`field scenes never chosen: ${unused.map(x => x.replace('battle_terrain_', '')).join(' ') || 'none'}`);

// ---------- 5. worked examples ----------
const EX = [
  { label: 'Ostroh (5072) Oct, attacker from Buchach 5066', provinceId: 5072, date: '1066.10.14', attackerFrom: 5066 },
  { label: 'TOMBO (8199) Jul', provinceId: 8199, date: '1100.7.3' },
  { label: 'Paris (2333) Jan, winter_normal', provinceId: 2333, date: '1100.1.20', winter: 'normal' },
  { label: 'Baghdad (4828) Aug', provinceId: 4828, date: '1100.8.10' },
  { label: 'Novgorod (5137) Feb, winter_harsh, 900 agents', provinceId: 5137, date: '1100.2.10', winter: 'harsh', agents: 900 },
  { label: 'Lhasa (9237) Apr', provinceId: 9237, date: '1100.4.10' },
  { label: 'London (1527) Oct, attacker from Southwark 1526 (river_large adjacency)', provinceId: 1527, date: '1066.10.14', attackerFrom: 1526 },
  { label: 'ASSAULT Paris castle fort 4, frankish', provinceId: 2333, date: '1100.6.1', mode: 'assault', holding: 'castle_holding', fortLevel: 4, buildingGfx: 'western_building_gfx', heritage: 'heritage_frankish' },
  { label: 'ASSAULT Byzantion city fort 8', provinceId: 496, date: '1100.6.1', mode: 'assault', holding: 'city_holding', fortLevel: 8, buildingGfx: 'byzantine_building_gfx' },
  { label: 'ASSAULT Dublin (28) castle fort 2, goidelic, Dec winter_mild', provinceId: 28, date: '1100.12.5', winter: 'mild', mode: 'assault', holding: 'castle_holding', fortLevel: 2, buildingGfx: 'western_building_gfx', heritage: 'heritage_goidelic' },
  { label: 'ASSAULT Lhasa temple citadel fort 5, tibetan', provinceId: 9237, date: '1100.6.1', mode: 'assault', holding: 'temple_citadel_holding', fortLevel: 5, buildingGfx: 'tibetan_building_gfx' },
];
const exOut = [];
for (const ex of EX) {
  const o = R.resolveBattlefield(T, ex);
  exOut.push(`${ex.label}\n    -> ${o.kind} ${o.scene}${o.sceneLevels ? ' [' + o.sceneLevels + ']' : ''} | ${o.site.terrain}/${o.site.climate} | crossing ${o.site.crossing.crossing} | ${o.atmosphere.season} ${o.atmosphere.hour}h ${o.atmosphere.weather} (${o.atmosphere.interpolatedAtmosphere} / ${o.atmosphere.atmospherePreset}) | TerrainType ${o.terrainTypeName}\n    candidates ${o.candidates.map(x => x.replace('battle_terrain_', '')).join(' ')}\n    why: ${o.why.join('; ')}`);
}

console.log('== INFO'); infos.forEach(m => console.log('  ' + m));
console.log('== EXAMPLES'); exOut.forEach(m => console.log('  ' + m));
console.log(`== WARN (${warns.length})`); warns.slice(0, 40).forEach(m => console.log('  ' + m));
console.log(`== ERROR (${errors.length})`); errors.slice(0, 60).forEach(m => console.log('  ' + m));
console.log(`checked: ${terrainKeys.length} CK3 terrains, ${holdings.length} holdings, ${gfxUsed.size} building_gfx, ${Object.keys(ts.scene_info).length} scene ids`);
process.exit(errors.length ? 1 : 0);
