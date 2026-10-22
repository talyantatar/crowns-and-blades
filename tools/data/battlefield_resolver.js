#!/usr/bin/env node
// battlefield_resolver.js - reference implementation of Tier 1 "battlefield reflects the CK3 location".
// bridge.exe ports resolveBattlefield() to C#.
//
// Library:  const R = require('./battlefield_resolver.js'); const T = R.loadTables(); R.resolveBattlefield(T, input)
// CLI:      node battlefield_resolver.js --province 5072 --date 1066.10.14 [--winter none|mild|normal|harsh]
//             [--from <attacker origin province>] [--holding castle_holding --fort 4 --gfx western_building_gfx --heritage heritage_frankish]
//             [--mode field|assault|relief] [--agents 800] [--opener legacy|own] [--full-index <ck3_map_index.js --out file>]
//
// Pure function of (static tables, logged CK3 facts): the same battle always resolves to the same scene and sky.
'use strict';
const fs = require('fs');
const path = require('path');
// bridge/data of this repository (the script works from any folder inside it).
const DATA = (() => {
  for (let d = __dirname; ; d = path.dirname(d)) {
    if (fs.existsSync(path.join(d, 'bridge', 'data', 'terrain_scenes.json'))) return path.join(d, 'bridge', 'data');
    if (path.dirname(d) === d) throw new Error(`bridge/data not found above ${__dirname}`);
  }
})();

function loadTables({ data = DATA, fullIndex = null } = {}) {
  const ts = JSON.parse(fs.readFileSync(path.join(data, 'terrain_scenes.json'), 'utf8'));
  const idx = JSON.parse(fs.readFileSync(path.join(data, 'ck3_province_terrain_index.json'), 'utf8'));
  const cl = JSON.parse(fs.readFileSync(path.join(data, 'ck3_province_climate.json'), 'utf8'));
  const prov = new Map(), climate = new Map();
  const cols = idx.meta.cols.map(c => c.replace(/\(.*$/, ''));
  for (const r of idx.rows) { const o = {}; cols.forEach((c, i) => { o[c] = r[i]; }); o.terrain = idx.meta.terrains[o.terrain]; o.waterKind = o.waterKind >= 0 ? idx.meta.waterKinds[o.waterKind] : null; prov.set(o.id, o); }
  for (const r of cl.rows) climate.set(r[0], { region: cl.meta.regions[r[1]] || null, biome: cl.meta.biomes[r[2]] || null, tree: cl.meta.trees[r[3]] || null, bucket: cl.meta.buckets[r[4]], county: r[5] });
  const full = fullIndex ? JSON.parse(fs.readFileSync(fullIndex, 'utf8')) : null;
  return { ts, prov, climate, full };
}

// ---------- deterministic randomness ----------
function hash01(s) { let h = 0x811c9dc5; for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 0x01000193); } h ^= h >>> 13; h = Math.imul(h, 0x5bd1e995); h ^= h >>> 15; return (h >>> 0) / 4294967296; }
function pickWeighted(weights, u) { const tot = Object.values(weights).reduce((a, b) => a + b, 0); let acc = 0; for (const [k, w] of Object.entries(weights)) { acc += w / tot; if (u < acc) return k; } return Object.keys(weights).pop(); }
const parseDate = s => { const m = /^(\d+)\.(\d+)\.(\d+)$/.exec(String(s || '').trim()); return m ? { y: +m[1], m: +m[2], d: +m[3] } : null; };
const mdOf = (m, d) => m * 100 + d;
const mdParse = s => { const [m, d] = s.split('-').map(Number); return mdOf(m, d); };
const inWindow = (md, from, to) => (from <= to ? md >= from && md <= to : md >= from || md <= to);

// ---------- crossing (needs the full index: neighbours with river-border px + adjacencies) ----------
function crossingOf(T, battleId, fromId) {
  if (!fromId || fromId === battleId || !T.full) return { crossing: fromId && !T.full ? 'unknown' : 'none' };
  const P = T.full.provinces, p = P[battleId], f = P[fromId];
  if (!p || !f) return { crossing: 'unknown' };
  const bearing = Math.round((Math.atan2(-(f.c[1] - p.cb[1]), f.c[0] - p.cb[0]) * 180 / Math.PI + 360) % 360); // deg CCW from east, image y down
  const adj = T.full.meta.adjacencies.find(a => (a.from === battleId && a.to === fromId) || (a.to === battleId && a.from === fromId));
  if (adj && adj.type === 'river_large') return { crossing: 'major_river', bearing, via: adj.comment };
  if (adj && adj.type === 'sea') return { crossing: 'strait', bearing, via: adj.comment };
  const nb = (p.nb || []).find(n => n[0] === fromId);
  if (nb) return { crossing: nb[2] / nb[1] >= T.ts.rules.river_border_ratio ? 'river' : 'none', bearing, riverBorder: [nb[2], nb[1]] };
  const riverProvs = (p.nb || []).map(n => n[0]).filter(id => P[id] && P[id].kind === 'river');
  if (riverProvs.some(r => (f.nb || []).some(n => n[0] === r))) return { crossing: 'major_river', bearing, via: 'river province' };
  return { crossing: 'none', bearing, note: 'origin is not a neighbour' };
}

// ---------- season ----------
function resolveSeason(T, ctx) {
  const S = T.ts.season, { date, bucket, wb, winter, snowPct, seed } = ctx;
  const md = mdOf(date.m, date.d);
  let winterActive = false, why;
  if (snowPct >= S.permanent_snow_pct) { winterActive = true; why = `permanent snow ${snowPct}% at locator`; }
  else if (winter) {
    if (winter === 'harsh' || winter === 'normal') { winterActive = true; why = `CK3 winter_${winter}_modifier`; }
    else if (winter === 'mild') { winterActive = S.mild_winter_buckets.includes(bucket) || wb >= 0.6; why = `CK3 winter_mild_modifier, bucket ${bucket}, bias ${wb}`; }
    else why = 'CK3 winter_none';
  } else {
    const row = S.winter_window_by_bias.find(r => wb >= r.min);
    if (row && row.from && inWindow(md, mdParse(row.from), mdParse(row.to))) winterActive = row.chance === 1 || hash01(seed + '|winter') < wb;
    why = `no winter logged: bias ${wb} window ${row && row.from ? row.from + '..' + row.to : 'none'}`;
  }
  let season;
  if (winterActive) season = 'winter';
  else {
    const table = S.base[bucket] || S.base.default;
    season = table[0][1];
    for (const [from, s] of table) if (md >= mdParse(from)) season = s;
  }
  return { season, seasonId: S.bl_ids[season], winterActive, why };
}

// ---------- time of day ----------
function resolveHour(T, ctx, season, opener) {
  const TD = T.ts.time_of_day;
  if (opener === 'legacy') return +pickWeighted(TD.legacy_hours, hash01(ctx.seed + '|hour'));
  const [a, b] = TD.daylight[season];
  return Math.round((a + (b - a) * hash01(ctx.seed + '|hour')) * 4) / 4; // quarter hours
}

// ---------- weather ----------
function resolveWeather(T, ctx, season, hour) {
  const W = T.ts.weather, group = W.group_of_bucket[ctx.bucket] || 'temperate';
  let row = Object.assign({}, W.table[group][season]);
  if (season === 'winter' && W.ck3_winter_override[ctx.winter]) row = Object.assign({}, W.ck3_winter_override[ctx.winter]);
  if (season !== 'winter' && W.monsoon.buckets.includes(ctx.bucket) && W.monsoon.months.includes(ctx.date.m)) row = Object.assign({}, W.monsoon.override);
  if (W.desert_terrains.includes(ctx.terrain)) for (const k of ['light_rain', 'heavy_rain']) if (row[k]) { row.dust = (row.dust || 0) + row[k]; delete row[k]; }
  let kind = pickWeighted(row, hash01(ctx.seed + '|weather'));
  if (kind === 'fog' && !(hour >= W.fog_hours[0] && hour <= W.fog_hours[1])) kind = 'overcast';
  if ((kind === 'snow' || kind === 'blizzard') && season !== 'winter') kind = 'overcast';
  return kind;
}

function presetFor(T, hour, weather) {
  const row = T.ts.time_of_day.presets_by_hour.find(r => hour >= r.from && hour < r.to) || T.ts.time_of_day.presets_by_hour[0];
  const pick = weather === 'heavy_rain' ? row.rain || 'TOD_12_00_HeavyRain'
    : weather === 'fog' ? row.fog || row.overcast || row.clear
      : ['overcast', 'light_rain', 'snow', 'blizzard', 'dust'].includes(weather) ? row.overcast || row.clear : row.clear;
  return { name: pick, proven: !!row.custom_battle && pick === row.clear };
}

// ---------- field scene ----------
function listFor(T, obj, bucket, terrain) {
  if (!obj) return [];
  if (obj.by_terrain && obj.by_terrain[terrain] && (!obj.by_terrain_buckets || obj.by_terrain_buckets.includes(bucket))) return obj.by_terrain[terrain];
  if (obj[bucket]) return obj[bucket];
  for (const b of T.ts.climate.fallback[bucket] || []) if (obj[b]) return obj[b];
  return obj.any || [];
}
function resolveField(T, ctx, season) {
  const { ts } = T, R = ts.rules, sit = ts.situational, p = ctx.p, why = [];
  const terrainList = listFor(T, ts.terrain[ctx.terrain] && ts.terrain[ctx.terrain].scenes, ctx.bucket, ctx.terrain);
  let lead = [], src = 'terrain';
  const near = p.waterDistPx >= 0 && p.waterDistPx <= R.coastal_near_px;
  if (ctx.crossing === 'major_river') { lead = listFor(T, sit.major_river_crossing, ctx.bucket, ctx.terrain); src = 'major_river_crossing'; }
  else if (ctx.crossing === 'river') { lead = listFor(T, sit.river_crossing, ctx.bucket, ctx.terrain); src = 'river_crossing'; }
  else if (ctx.crossing === 'strait') { lead = listFor(T, sit.coastal, ctx.bucket, ctx.terrain); src = 'strait->coastal'; }
  else if (near && p.waterKind === 'sea') { lead = listFor(T, sit.coastal, ctx.bucket, ctx.terrain); src = 'coastal_near'; }
  else if (near && p.waterKind === 'lake') { lead = listFor(T, sit.lakeside, ctx.bucket, ctx.terrain); src = 'lakeside_near'; }
  else if ((p.flags & 1) && hash01(ctx.seed + '|coast') < R.coastal_far_chance) { lead = listFor(T, sit.coastal, ctx.bucket, ctx.terrain); src = 'coastal_far'; }
  else if (((p.riverDistPx >= 0 && p.riverDistPx <= 2) || (p.flags & 4)) && hash01(ctx.seed + '|river') < R.riverside_chance) { lead = listFor(T, sit.river_crossing, ctx.bucket, ctx.terrain); src = 'riverside'; }
  why.push(`list: ${src} (${ctx.terrain}/${ctx.bucket})`);
  const exclude = new Set(R.exclude_unlisted);
  let c = [...new Set([...lead, ...terrainList])].filter(id => !exclude.has(id) && ts.scene_info[id] && !ts.scene_info[id].missing);
  const info = id => ts.scene_info[id];
  // keep the situational lead in front: reorder inside the lead block and inside the terrain block separately
  const reorderBlocks = pred => { const L = c.filter(x => lead.includes(x)), Rr = c.filter(x => !lead.includes(x)); const f = arr => [...arr.filter(pred), ...arr.filter(x => !pred(x))]; c = [...f(L), ...f(Rr)]; };
  const terc = R.relief7_terciles[ctx.terrain];
  if (terc) {
    const cls = p.relief7Raw >= terc[1] ? 'relief_high' : p.relief7Raw <= terc[0] ? 'relief_low' : null;
    if (cls) { reorderBlocks(id => info(id).relief === cls); why.push(`relief ${cls.replace('relief_', '')} (relief7 ${p.relief7Raw} vs terciles ${terc})`); }
  }
  if (season === 'winter') { reorderBlocks(id => info(id).snow_layer); why.push('winter: snow-layer scenes first'); }
  if (ctx.agents >= R.large_battle_agents) {
    const big = c.filter(id => Math.min(...(info(id).size_m || [0])) >= R.large_battle_min_scene_m);
    if (big.length) { c = big; why.push(`${ctx.agents} agents: scenes >= ${R.large_battle_min_scene_m} m only`); }
  }
  if (!c.length) c = [R.fallback_scene];
  const top = c.slice(0, R.pick_top_n), w = {}; top.forEach((id, i) => { w[id] = R.pick_weights[i]; });
  const scene = pickWeighted(w, hash01(ctx.seed + '|scene'));
  return { scene, candidates: c.slice(0, 6), source: src, why };
}

// ---------- settlement scenes (assault / relief / tribal) ----------
function architectureOf(T, gfx, heritage, kind) {
  const A = T.ts.siege.architecture;
  let a = A.building_gfx[gfx] || A.default;
  if (typeof a === 'object') a = a[kind] || a.castle;
  if (gfx === 'western_building_gfx' && A.heritage_override_for_western[heritage]) a = A.heritage_override_for_western[heritage];
  return a;
}
function resolveSettlement(T, ctx, season) {
  const S = T.ts.siege, kind = S.holding_kind[ctx.holding || 'none'] || 'field';
  if (kind === 'field') return null;
  const arch = architectureOf(T, ctx.gfx, ctx.heritage, kind);
  if (kind === 'village') {
    const list = T.ts.village.scenes[arch] || [];
    return { kind: 'village', architecture: arch, scene: list[Math.floor(hash01(ctx.seed + '|village') * list.length)], sceneLevels: '', candidates: list.slice(0, 6) };
  }
  const wall = (S.fort_level_to_wall_level.find(r => (ctx.fortLevel ?? 0) <= r.max) || { wall: 2 }).wall;
  const untested = new Set(S.untested_scenes);
  let list = (S.scenes[arch] && S.scenes[arch][kind] || []).filter(id => !untested.has(id));
  const tags = id => (T.ts.scene_info[id] || {});
  const wantWater = (ctx.p.flags & 1) || (ctx.p.flags & 4) || (ctx.p.riverDistPx >= 0 && ctx.p.riverDistPx <= 2);
  if (wantWater) list = [...list.filter(id => tags(id).water), ...list.filter(id => !tags(id).water)];
  if (season === 'winter') list = [...list.filter(id => tags(id).snow_layer), ...list.filter(id => !tags(id).snow_layer)];
  const top = list.slice(0, 3);
  const scene = top[Math.floor(hash01(ctx.seed + '|siege') * top.length)];
  return { kind, architecture: arch, scene, wallLevel: wall, sceneLevels: `level_${wall} siege`, sceneUpgradeLevel: wall, wallHitPoints: S.wall_hit_points.default.slice(), candidates: list.slice(0, 6) };
}

// ---------- main ----------
function resolveBattlefield(T, input) {
  const p = T.prov.get(input.provinceId);
  if (!p) throw new Error('unknown province ' + input.provinceId);
  const cl = T.climate.get(input.provinceId) || { bucket: 'temperate' };
  const date = parseDate(input.date) || { y: 1066, m: 9, d: 15 };
  const opener = input.opener || 'own';
  const ctx = {
    p, terrain: p.terrain, bucket: cl.bucket, wb: p.winterBias, winter: input.winter || null, snowPct: p['snow%'] || 0,
    date, seed: `${input.provinceId}|${input.date}|${input.combatSeq || 0}`, agents: input.agents || 0,
    holding: input.holding, fortLevel: input.fortLevel, gfx: input.buildingGfx, heritage: input.heritage,
  };
  const cross = crossingOf(T, input.provinceId, input.attackerFrom);
  ctx.crossing = cross.crossing;
  const s = resolveSeason(T, ctx);
  const hour = resolveHour(T, ctx, s.season, opener);
  const weather = resolveWeather(T, ctx, s.season, hour);
  const A = T.ts.weather.atmosphere[weather];
  const mode = input.mode || 'field';
  let settlement = null;
  if (mode === 'assault' || mode === 'relief') settlement = resolveSettlement(T, ctx, s.season);
  const field = settlement && settlement.scene ? null : resolveField(T, ctx, s.season);
  const blTerrain = s.season === 'winter' ? 'Snow' : (T.ts.terrain[ctx.terrain] || { bl_terrain_type: 'Plain' }).bl_terrain_type;
  const legacyHour = opener === 'legacy' ? hour : +Object.keys(T.ts.time_of_day.legacy_hours).reduce((a, b) => (Math.abs(b - hour) < Math.abs(a - hour) ? b : a));
  const preset = presetFor(T, hour, weather);
  return {
    kind: settlement ? (mode === 'relief' ? 'relief' : settlement.kind === 'village' ? 'village' : 'siege') : 'field',
    scene: settlement ? settlement.scene : field.scene,
    sceneLevels: settlement ? settlement.sceneLevels : '',
    terrainType: T.ts.bl_terrain_type_ids[blTerrain], terrainTypeName: blTerrain,
    candidates: settlement ? settlement.candidates : field.candidates,
    siege: settlement && settlement.kind !== 'village' ? { architecture: settlement.architecture, wallLevel: settlement.wallLevel, sceneUpgradeLevel: settlement.sceneUpgradeLevel, wallHitPoints: settlement.wallHitPoints } : null,
    site: { terrain: ctx.terrain, climate: cl.bucket, region: cl.region, biome: cl.biome, treeKind: cl.tree, county: cl.county, crossing: cross, coastal: !!(p.flags & 1), waterKind: p.waterKind, waterDistPx: p.waterDistPx, riverDistPx: p.riverDistPx, relief7Raw: p.relief7Raw, downhillDirDeg: p.downhillDirDeg, forestPct: p['forest%'], treesPer1000px: p.treesPer1000px },
    atmosphere: {
      season: s.season, seasonId: s.seasonId, winterActive: s.winterActive, hour, weather,
      interpolatedAtmosphere: A.interpolated, atmospherePreset: preset.name, presetProven: preset.proven,
      rain: A.rain, snow: A.snow, fogDensity: A.fog, fogColor: A.fog_color || null,
      winterTimeFactor: s.winterActive ? 1 : 0, drynessFactor: A.dryness ?? (cl.bucket === 'arid' ? 0.8 : 0.3),
    },
    legacy: { scene: settlement ? settlement.scene : field.scene, season: s.season, timeOfDay: legacyHour },
    why: [s.why, ...(field ? field.why : [`settlement ${settlement.kind} ${settlement.architecture}`])],
  };
}

module.exports = { loadTables, resolveBattlefield, hash01, crossingOf };

if (require.main === module) {
  const a = process.argv.slice(2), g = (k, d) => { const i = a.indexOf(k); return i >= 0 ? a[i + 1] : d; };
  const T = loadTables({ fullIndex: g('--full-index', null) });
  const out = resolveBattlefield(T, {
    provinceId: +g('--province', 5072), date: g('--date', '1066.10.14'), winter: g('--winter', null), attackerFrom: +g('--from', 0) || null,
    holding: g('--holding', null), fortLevel: +g('--fort', 0), buildingGfx: g('--gfx', null), heritage: g('--heritage', null),
    mode: g('--mode', 'field'), agents: +g('--agents', 0), opener: g('--opener', 'own'),
  });
  console.log(JSON.stringify(out, null, 1));
}
