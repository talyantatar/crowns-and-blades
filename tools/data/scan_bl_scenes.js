#!/usr/bin/env node
// Scans the user's Bannerlord install (read-only) and writes a machine-readable scene catalog:
//   <repo>/bridge/data/bl_scenes.json
// Sources (all read at runtime, nothing copied):
//   Modules/*/SceneObj/<scene>/scene.xscene + atmosphere.xml + *.bin sizes
//   Modules/SandBox/ModuleData/sp_battle_scenes.xml       (campaign field-battle scenes, map_indices)
//   Modules/CustomBattle/ModuleData/custom_battle_scenes.xml (display names, siege/village flags)
//   Modules/{SandBox,StoryMode}/ModuleData/*settlements.xml   (which settlement uses which scene; culture; map pos)
//   Modules/SandBox/ModuleData/{conversation,meeting}_scenes.xml, Native/ModuleData/Multiplayer/MultiplayerScenes.xml
// Usage: node scan_bl_scenes.js ["<Bannerlord root>"] ["<out.json>"]   (root default: env BL_DIR, else found in the Steam libraries)
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

const BL = process.argv[2] || gameDirs.bannerlordDir();
const OUT = process.argv[3] || path.join(DATA_DIR, 'bl_scenes.json');
const MOD = path.join(BL, 'Modules');

const readText = (p) => { try { return fs.readFileSync(p, 'utf8').replace(/^\uFEFF/, ''); } catch { return null; } };
const fileSize = (p) => { try { return fs.statSync(p).size; } catch { return 0; } };
const attrs = (s) => { const o = {}; const re = /([A-Za-z_][\w.]*)\s*=\s*"([^"]*)"/g; let m; while ((m = re.exec(s))) o[m[1]] = m[2]; return o; };
const stripLoc = (s) => (s || '').replace(/^\{=[^}]*\}/, '').trim();
const uniq = (a) => [...new Set(a.filter(Boolean))];
const num = (s) => { const v = parseFloat(s); return Number.isFinite(v) ? v : null; };
const r1 = (v) => (v == null ? null : Math.round(v * 10) / 10);

const CULTURES = ['empire', 'vlandia', 'sturgia', 'aserai', 'khuzait', 'battania'];
const PREFIX_CULTURE = { arabian: 'aserai', roman: 'empire', pict: 'battania', nord: 'sturgia' };

// ---------- 1. metadata XMLs ----------
function parseSceneList(xml, tagName) {
  // returns [{attrs, terrainTypes:[...]}] for every <Scene ...>...</Scene> or <Scene .../> not inside comments
  if (!xml) return [];
  const clean = xml.replace(/<!--[\s\S]*?-->/g, '');
  const out = [];
  const re = new RegExp(`<${tagName}\\b([^>]*?)(/>|>([\\s\\S]*?)</${tagName}>)`, 'g');
  let m;
  while ((m = re.exec(clean))) {
    const a = attrs(m[1]);
    const body = m[3] || '';
    const tts = [];
    const tre = /<TerrainType\s+name="([^"]+)"/g; let t;
    while ((t = tre.exec(body))) tts.push(t[1]);
    const gts = [];
    const gre = /<GameType\s+name="([^"]+)"/g; let g;
    while ((g = gre.exec(body))) gts.push(g[1]);
    out.push({ a, terrainTypes: tts, gameTypes: gts });
  }
  return out;
}

const spBattle = {};
for (const s of parseSceneList(readText(path.join(MOD, 'SandBox/ModuleData/sp_battle_scenes.xml')), 'Scene')) {
  spBattle[s.a.id] = {
    terrain: s.a.terrain || 'Plain',
    forest_density: (s.a.forest_density || 'None').toLowerCase(),
    terrain_types: s.terrainTypes,
    map_indices: (s.a.map_indices || '').split(',').map((x) => parseInt(x, 10)).filter(Number.isFinite),
    is_naval: s.a.is_naval === 'true',
  };
}
const customBattle = {};
for (const s of parseSceneList(readText(path.join(MOD, 'CustomBattle/ModuleData/custom_battle_scenes.xml')), 'Scene')) {
  customBattle[s.a.id] = {
    name: stripLoc(s.a.name),
    terrain: s.a.terrain || null,
    forest_density: s.a.forest_density ? s.a.forest_density.toLowerCase() : null,
    terrain_types: s.terrainTypes,
    is_siege_map: s.a.is_siege_map === 'true',
    is_village_map: s.a.is_village_map === 'true',
    is_lords_hall_map: s.a.is_lords_hall_map === 'true',
    forced_scene_level: s.a.forced_scene_level || null,
  };
}
const conversation = {};
for (const s of parseSceneList(readText(path.join(MOD, 'SandBox/ModuleData/conversation_scenes.xml')), 'Scene')) conversation[s.a.id] = s.a;
const meeting = {};
for (const s of parseSceneList(readText(path.join(MOD, 'SandBox/ModuleData/meeting_scenes.xml')), 'Scene')) meeting[s.a.id] = s.a;
const mpScenes = {};
for (const s of parseSceneList(readText(path.join(MOD, 'Native/ModuleData/Multiplayer/MultiplayerScenes.xml')), 'Scene')) mpScenes[s.a.name] = s.gameTypes;

// settlements: scene -> [{settlement, location}]
const sceneUse = {};
const settlementList = [];
for (const f of ['SandBox/ModuleData/settlements.xml', 'StoryMode/ModuleData/story_mode_settlements.xml']) {
  const xml = readText(path.join(MOD, f));
  if (!xml) continue;
  const clean = xml.replace(/<!--[\s\S]*?-->/g, '');
  const blocks = clean.split(/<Settlement\b/).slice(1);
  for (const b0 of blocks) {
    const end = b0.indexOf('</Settlement>');
    const b = end >= 0 ? b0.slice(0, end) : b0;
    const head = attrs(b.slice(0, b.indexOf('>')));
    let type = 'other';
    const town = /<Town\b([^>]*)>/.exec(b);
    if (town) type = attrs(town[1]).is_castle === 'true' ? 'castle' : 'town';
    else if (/<Village\b/.test(b)) type = 'village';
    else if (/<Hideout\b/.test(b)) type = 'hideout';
    else if (/<RetirementSettlementComponent\b|<Retirement/.test(b)) type = 'retirement';
    const st = {
      id: head.id, name: stripLoc(head.name), type,
      culture: (head.culture || '').replace(/^Culture\./, '') || null,
      x: num(head.posX), y: num(head.posY),
    };
    if (st.id) settlementList.push(st);
    const lre = /<Location\b([^>]*?)\/?>/g; let lm;
    while ((lm = lre.exec(b))) {
      const la = attrs(lm[1]);
      for (const k of Object.keys(la)) {
        if (!/^scene_name(_\d)?$/.test(k) || !la[k]) continue;
        const lvl = k === 'scene_name' ? 0 : parseInt(k.slice(-1), 10);
        (sceneUse[la[k]] = sceneUse[la[k]] || []).push({ settlement: st.id, name: st.name, type, culture: st.culture, x: st.x, y: st.y, location: la.id, wall_level: lvl });
      }
    }
  }
}

// name-token -> settlement (for locating custom battle field scenes on Calradia; heuristic)
const settlementByName = {};
for (const s of settlementList) if (s.name && s.x != null) settlementByName[s.name.toLowerCase()] = s;
const KINGDOM_WORD = { vlandia: 'vlandia', vlandian: 'vlandia', sturgia: 'sturgia', battania: 'battania', aserai: 'aserai', khuzait: 'khuzait', empire: 'empire' };

// ---------- 2. per-scene xscene/atmosphere ----------
function scanScene(dir, id) {
  const xs = readText(path.join(dir, 'scene.xscene'));
  const r = { ok: !!xs };
  if (!xs) return r;
  const lv = /<levels>([\s\S]*?)<\/levels>/.exec(xs);
  r.levels = lv ? [...lv[1].matchAll(/<level name="([^"]+)"/g)].map((m) => m[1]) : [];
  const env = {};
  const envBlock = /<environment_properties>([\s\S]*?)<\/environment_properties>/.exec(xs);
  if (envBlock) for (const m of envBlock[1].matchAll(/<property name="([^"]+)" value="([^"]*)"/g)) if (!(m[1] in env)) env[m[1]] = m[2];
  r.env = env;
  const tm = /<terrain ([^>]*)>/.exec(xs);
  if (tm) {
    const ta = attrs(tm[1]);
    const tStart = tm.index;
    const tEnd = xs.indexOf('</terrain>', tStart);
    const tBody = xs.slice(tStart, tEnd > 0 ? tEnd : tStart + 2_000_000);
    const layers = [...tBody.matchAll(/<layer name="([^"]+)">/g)].map((m) => m[1]);
    const diffuse = uniq([...tBody.matchAll(/<texture type="diffusemap" name="([^"]+)"/g)].map((m) => m[1]));
    const meshes = uniq([...tBody.matchAll(/<mesh name="([^"]+)"/g)].map((m) => m[1]));
    const physMats = uniq([...tBody.matchAll(/physics_material="([^"]+)"/g)].map((m) => m[1]));
    const nx = parseInt(ta.node_dimension_x, 10), ny = parseInt(ta.node_dimension_y, 10), ns = num(ta.node_size);
    r.terrain = {
      enabled: ta.enabled === 'true',
      node_dim: [nx, ny], node_size: ns,
      size_m: [nx * ns, ny * ns],
      min_h: r1(num(ta.min_height)), max_h: r1(num(ta.max_height)),
      relief_m: r1(num(ta.max_height) - num(ta.min_height)),
      dynamic_weather_effects: ta.use_dynamic_weather_effects === 'true',
      layers, ground_textures: diffuse, layer_meshes: meshes, physics_materials: physMats,
    };
  } else r.terrain = null;
  r.paths = [];
  for (const m of xs.matchAll(/<path name="([^"]+)"[^>]*>([\s\S]*?)<\/path>/g)) r.paths.push({ name: m[1], points: (m[2].match(/<point\b/g) || []).length });
  const hist = (re) => { const h = {}; for (const m of xs.matchAll(re)) h[m[1]] = (h[m[1]] || 0) + 1; return h; };
  r.tags = hist(/<tag name="([^"]+)"/g);
  r.scripts = hist(/<script name="([^"]+)"/g);
  r.prefabs = hist(/prefab="([^"]+)"/g);
  r.entities = (xs.match(/<game_entity\b/g) || []).length;
  const atm = readText(path.join(dir, 'atmosphere.xml'));
  if (atm) {
    const v = {}; for (const m of atm.matchAll(/<value name="([^"]+)" value="([^"]*)"/g)) if (!(m[1] in v)) v[m[1]] = m[2];
    r.atmo = {
      name: v.name || null, season: v.season || null, time_of_day: num(v.time_of_day),
      temperature: num(v.temperature), humidity: num(v.humidity), color_grade: v.color_grade_name || null,
      snow_density: num(v.snow_density), rain_density: num(v.fall_density), skybox: v.skybox_background_texture_name || null,
    };
  } else r.atmo = null;
  r.bin = { terrain: fileSize(path.join(dir, 'terrain.bin')), navmesh: fileSize(path.join(dir, 'navmesh.bin')), flora: fileSize(path.join(dir, 'flora.bin')), prt: fileSize(path.join(dir, 'prt_data.bin')) };
  return r;
}

// ---------- 3. classification ----------
function cultureFromId(id) {
  const p = id.toLowerCase().split('_')[0];
  if (CULTURES.includes(p)) return p;
  if (PREFIX_CULTURE[p]) return PREFIX_CULTURE[p];
  for (const c of CULTURES) if (id.toLowerCase().includes(c)) return c;
  return null;
}

function classify(id, s, cb, sp, uses) {
  const lid = id.toLowerCase();
  const usedAs = (uses || []).map((u) => `${u.type}:${u.location}`);
  if (sp && sp.is_naval) return ['naval', 'naval_battle'];
  if (/navalraid|_raid$/.test(lid)) return ['village', 'river_raid'];
  if (/^sea_bandit/.test(lid)) return ['other', 'sea_hideout'];
  if (/^mp_/.test(lid) || mpScenes[id]) return ['other', /siege/.test(lid) ? 'multiplayer_siege' : 'multiplayer_field'];
  if (/(interior|keep|tavern|dungeon|_house|house_|lords_hall|lordshall)/.test(lid) || (cb && cb.is_lords_hall_map)) return ['other', 'interior'];
  if (/^arena_/.test(lid)) return ['other', 'arena'];
  if (/hideout/.test(lid) || usedAs.some((u) => u.startsWith('hideout'))) return ['other', 'hideout'];
  if (conversation[id] || /^conversation_scene/.test(lid)) return ['other', 'conversation'];
  if (meeting[id]) return ['other', 'meeting'];
  if ((cb && cb.is_siege_map) || usedAs.some((u) => /^(town|castle):center$/.test(u)) || (/(castle|town|city|siege)/.test(lid) && s.levels && s.levels.includes('siege')))
    return ['siege', usedAs.some((u) => u.startsWith('castle')) || /castle/.test(lid) ? 'castle' : 'town'];
  if ((cb && cb.is_village_map) || usedAs.some((u) => u.startsWith('village')) || /village/.test(lid)) return ['village', 'village'];
  if (/^battle_terrain/.test(lid) || sp || (cb && !cb.is_siege_map && !cb.is_village_map)) return ['field', /benchmark/.test(lid) ? 'benchmark' : 'battle_terrain'];
  if (/^(scn_|main_menu|character_|inventory|banner_editor|crafting|ibl_|flora_editor|rain$|benchmark)/.test(lid) || /notification|cutscene/.test(lid)) return ['other', 'menu_or_cutscene'];
  if (/training_field|villa_|retirement/.test(lid)) return ['other', 'story_or_misc'];
  if (lid === 'main_map') return ['other', 'campaign_map'];
  return ['other', 'misc'];
}

const NAME_TAGS = [
  [/forest|woods|jungle/i, 'forest'], [/jungle/i, 'jungle'], [/river|creek|riverbed|bridge|ford/i, 'river'], [/bridge/i, 'bridge'],
  [/coast|bay|shore|sea\b/i, 'coast'], [/mountain|cliff|pass\b|foothill|hill|plateau|edge/i, 'hills'], [/mountain|cliffs/i, 'mountain'],
  [/valley|basin|lowland/i, 'valley'], [/plains|prairie|meadow/i, 'open_plain'], [/swamp|bog|marsh/i, 'wetlands'],
  [/desert|dune/i, 'desert'], [/wastes/i, 'wastes'], [/canyon/i, 'canyon'], [/lake|llyn/i, 'lake'],
];

function terrainTags(id, s, cb, sp, kind) {
  const t = [];
  const add = (x) => x && t.push(String(x).toLowerCase());
  if (sp) { add(sp.terrain); sp.terrain_types.forEach(add); if (sp.forest_density && sp.forest_density !== 'none') add('forest_' + sp.forest_density); }
  if (cb) { add(cb.terrain); cb.terrain_types.forEach(add); if (cb.forest_density && cb.forest_density !== 'none') add('forest_' + cb.forest_density); for (const [re, tag] of NAME_TAGS) if (re.test(cb.name || '')) add(tag); }
  if (s.terrain) {
    const L = (s.terrain.layers || []).join(' ').toLowerCase();
    const T = (s.terrain.ground_textures || []).join(' ').toLowerCase();
    const M = (s.terrain.layer_meshes || []).join(' ').toLowerCase();
    if (/snow/.test(L)) add('snow_layer');
    if (/sand|desert|dune/.test(L + ' ' + T)) add('sand_ground');
    if (/mud|swamp|marsh|bog/.test(L + ' ' + T)) add('mud_ground');
    if (/tree|pine|oak|beech|birch|poplar|palm|aspen|cypress|forest/.test(M)) add('flora_trees');
    if (/reed|cattail|swamp/.test(M)) add('flora_reeds');
    const rel = s.terrain.relief_m;
    if (rel != null && kind === 'field') add(rel >= 250 ? 'relief_high' : rel >= 120 ? 'relief_medium' : 'relief_low');
  }
  if (s.env && s.env.water_exists === 'true') add('water_plane');
  const pf = Object.keys(s.prefabs || {}).join(' ');
  if (/river_plane|river_crossing/.test(pf)) add('river_prefab');
  if (/water_plane/.test(pf)) add('water_prefab');
  if (s.atmo && s.atmo.season) add('authored_' + s.atmo.season);
  return uniq(t);
}

function calradiaHint(cb) {
  if (!cb || !cb.name) return null;
  const words = cb.name.replace(/\(.*?\)/g, '').split(/[\s-]+/).map((w) => w.toLowerCase()).filter(Boolean);
  for (let n = 3; n >= 1; n--) for (let i = 0; i + n <= words.length; i++) {
    const key = words.slice(i, i + n).join(' ');
    if (settlementByName[key]) { const s = settlementByName[key]; return { via: 'name_match', settlement: s.id, name: s.name, culture: s.culture, x: s.x, y: s.y }; }
  }
  for (const w of words) if (KINGDOM_WORD[w]) return { via: 'kingdom_word', culture: KINGDOM_WORD[w] };
  return null;
}

// ---------- 4. walk all SceneObj folders ----------
const out = [];
for (const modName of fs.readdirSync(MOD)) {
  const so = path.join(MOD, modName, 'SceneObj');
  if (!fs.existsSync(so)) continue;
  for (const id of fs.readdirSync(so)) {
    const dir = path.join(so, id);
    if (!fs.statSync(dir).isDirectory()) continue;
    const s = scanScene(dir, id);
    const sp = spBattle[id] || null;
    const cb = customBattle[id] || null;
    const usesRaw = sceneUse[id] || null;
    // group per settlement+location; wall_levels = which Town wall levels (scene_name_1..3; 0 = plain scene_name) use this scene
    let uses = null;
    if (usesRaw) {
      const g = new Map();
      for (const u of usesRaw) {
        const k = u.settlement + '|' + u.location;
        if (!g.has(k)) g.set(k, { ...u, wall_levels: [] });
        if (!g.get(k).wall_levels.includes(u.wall_level)) g.get(k).wall_levels.push(u.wall_level);
      }
      uses = [...g.values()];
    }
    const [kind, subkind] = classify(id, s, cb, sp, uses);
    const spawnPaths = (s.paths || []).filter((p) => /^spawn_path_\d\d$/.test(p.name) && p.points > 1);
    const tags = s.tags || {}; const scripts = s.scripts || {};
    const sc = (k) => scripts[k] || 0;
    const siege = (sc('WallSegment') || sc('CastleGate') || sc('SiegeLadderSpawner') || sc('BatteringRamSpawner') || sc('SiegeTowerSpawner')) ? {
      wall_segments: sc('WallSegment'), castle_gates: sc('CastleGate'), deployment_points: sc('DeploymentPoint'),
      ladder_spawners: sc('SiegeLadderSpawner'), tower_spawners: sc('SiegeTowerSpawner'), ram_spawners: sc('BatteringRamSpawner'),
      ballista_spawners: sc('BallistaSpawner'), mangonel_spawners: sc('MangonelSpawner'), trebuchet_spawners: sc('TrebuchetSpawner'),
      ram_paths: (s.paths || []).filter((p) => /^ram_path/.test(p.name)).length, tower_paths: (s.paths || []).filter((p) => /^tower_path/.test(p.name)).length,
      destructibles: sc('DestructableComponent'),
    } : null;
    let culture = cultureFromId(id);
    if (!culture && uses) culture = uniq(uses.map((u) => u.culture))[0] || null;
    const hint = kind === 'field' ? calradiaHint(cb) : null;
    let cultureSource = culture ? 'scene_id_or_settlement' : null;
    if (!culture && hint && hint.culture) { culture = hint.culture; cultureSource = 'calradia_hint(' + hint.via + ')'; }
    if (!culture && meeting[id]) culture = (meeting[id].culture || '').replace('Culture.', '') || null;
    const notes = [];
    if (sp) notes.push(`campaign field scene (sp_battle_scenes.xml) map_indices=[${sp.map_indices.join(',')}]`);
    if (cb && cb.name) notes.push(`custom battle: "${cb.name}"`);
    if (cb && cb.forced_scene_level) notes.push(`forced_scene_level=${cb.forced_scene_level}`);
    if (uses) notes.push(`used by ${uses.length} settlement location(s): ` + uniq(uses.map((u) => `${u.name}(${u.type}/${u.location})`)).slice(0, 8).join(', ') + (uses.length > 8 ? ', ...' : ''));
    if (kind === 'field' && spawnPaths.length === 0) notes.push('NO spawn_path_NN paths -> BattleSpawnPathSelector cannot initialise; avoid for field battles');
    if (mpScenes[id]) notes.push('MP game types: ' + mpScenes[id].join('/'));
    if (!s.ok) notes.push('scene.xscene missing/unreadable');
    const battle_capable = {
      field: kind === 'field' && spawnPaths.length > 0 && s.bin && s.bin.navmesh > 0,
      siege: kind === 'siege' && !!siege && (s.levels || []).includes('siege'),
      village: kind === 'village' && s.bin && s.bin.navmesh > 0,
    };
    out.push({
      id, module: modName, kind, subkind,
      terrain_tags: terrainTags(id, s, cb, sp, kind),
      culture, culture_source: cultureSource,
      notes: notes.join('; '),
      display_name: cb ? cb.name : null,
      battle_capable,
      sp_battle: sp, custom_battle: cb ? { terrain: cb.terrain, terrain_types: cb.terrain_types, forest_density: cb.forest_density, is_siege_map: cb.is_siege_map, is_village_map: cb.is_village_map, forced_scene_level: cb.forced_scene_level } : null,
      settlements: uses ? uses.map((u) => ({ id: u.settlement, name: u.name, type: u.type, culture: u.culture, location: u.location, wall_levels: u.wall_levels.sort(), x: u.x, y: u.y })) : null,
      calradia_hint: hint,
      levels: s.levels || [],
      terrain: s.terrain ? { size_m: s.terrain.size_m, node_dim: s.terrain.node_dim, node_size: s.terrain.node_size, min_h: s.terrain.min_h, max_h: s.terrain.max_h, relief_m: s.terrain.relief_m, layers: s.terrain.layers, ground_textures: s.terrain.ground_textures.slice(0, 16), physics_materials: s.terrain.physics_materials, dynamic_weather_effects: s.terrain.dynamic_weather_effects } : null,
      water: s.env ? { exists: s.env.water_exists === 'true', level: num(s.env.water_level) } : null,
      north_angle: s.env ? num(s.env.north_angle) : null,
      navmesh: s.bin ? { bytes: s.bin.navmesh, auto_generated: s.env ? s.env.nav_mesh_auto_generated_ === 'true' : null } : null,
      spawn_paths: spawnPaths.length,
      walk_area_vertices: (tags.walk_area_vertex || 0) + ((s.prefabs || {}).border_soft || 0),
      hard_border_vertices: (tags.walk_area_vertex_hard || 0) + ((s.prefabs || {}).border_hard || 0),
      battle_set_tags: Object.keys(tags).filter((k) => /_set$|^(attacker|defender)_(infantry|archer|cavalry|horsearcher|ranged)$/.test(k)),
      tactical: { positions: sc('TacticalPosition'), regions: sc('TacticalRegion'), flee_lines: (s.prefabs || {}).flee_line || 0 },
      siege,
      atmosphere: s.atmo,
      entity_count: s.entities || 0,
      bin_bytes: s.bin || null,
    });
  }
}
out.sort((a, b) => (a.kind + a.id).localeCompare(b.kind + b.id));
fs.mkdirSync(path.dirname(OUT), { recursive: true });
fs.writeFileSync(OUT, JSON.stringify(out, null, 1));
const count = {}; for (const s of out) count[s.kind] = (count[s.kind] || 0) + 1;
const cap = { field: out.filter((s) => s.battle_capable.field).length, siege: out.filter((s) => s.battle_capable.siege).length, village: out.filter((s) => s.battle_capable.village).length };
console.log(`wrote ${out.length} scenes to ${OUT}`); console.log('by kind', count); console.log('battle-capable', cap);
