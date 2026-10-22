#!/usr/bin/env node
// build_terrain_scenes.js - Tier-1 "battlefield reflects the CK3 location" lookup tables -> bridge/data/terrain_scenes.json
//
//   node build_terrain_scenes.js [--bl-scenes <repo>/bridge/data/bl_scenes.json]
//                                [--ck3-index <repo>/bridge/data/ck3_province_terrain_index.json]
//                                [--out <repo>/bridge/data/terrain_scenes.json]
//
// All scene lists below are HAND-CURATED (INFERRED) from bl_scenes.json facts (display names, terrain tags, culture hints,
// size, relief, water, snow layer). Ids are VALIDATED by validate_terrain_scenes.js against bl_scenes.json and the
// install's SceneObj folders. The script only reads derived data files and writes derived ids/numbers.
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
const BL_SCENES = arg('--bl-scenes', path.join(DATA_DIR, 'bl_scenes.json'));
const CK3_INDEX = arg('--ck3-index', path.join(DATA_DIR, 'ck3_province_terrain_index.json'));
const OUT = arg('--out', path.join(DATA_DIR, 'terrain_scenes.json'));
const sibling = (name) => path.posix.join(path.posix.dirname(SELF), name);
const inRepo = (p) => { const r = gameDirs.repoRelative(path.resolve(p)); return r.startsWith('..') ? path.basename(p) : r; };

const F = s => s.split(/\s+/).filter(Boolean).map(x => 'battle_terrain_' + x); // field-scene shorthand -> full ids

// ---------------- climate buckets (computed per province by ck3_province_climate.js) ----------------
const BUCKETS = ['temperate', 'boreal', 'mediterranean', 'arid', 'steppe', 'tropical', 'highland', 'east_asia'];
// when terrain[t].scenes has no list for a bucket, try these buckets in order, then "any"
const BUCKET_FALLBACK = {
  temperate: [], boreal: ['temperate'], mediterranean: ['temperate'], arid: ['steppe'], steppe: ['arid'],
  tropical: ['mediterranean', 'temperate'], highland: ['steppe', 'arid'], east_asia: ['temperate'],
};

// ---------------- CK3 terrain (game/common/terrain_types/00_terrains.txt, 17 keys) -> ordered field scenes ----------------
// bl_terrain = TaleWorlds.Core.TerrainType for MissionInitializerRecord.TerrainType (Plain 1, Desert 2, Snow 3, Forest 4,
// Steppe 5, Mountain 7, Swamp 15). Never leave it at -1: Mission.TerrainType then reports Water.
const TERRAIN = {
  plains: { bl: 'Plain', scenes: {
    any: F('032 biome_025 biome_131 biome_027 biome_012 biome_114'),
    boreal: F('biome_047 006 032 biome_131 biome_012'),
    mediterranean: F('biome_130 L m O biome_131'),   // not 026 (Erithrys Foothill): a glade in dense pine forest, seen in the live test 2026-10-04
    arid: F('biome_040 biome_094 biome_067 biome_076 019'),
    steppe: F('006 013 015 biome_040 019 014'),
    tropical: F('biome_096 biome_130 biome_131 biome_107 biome_025'),
    highland: F('biome_088 014 013 012 biome_148'),
    east_asia: F('biome_131 biome_130 biome_012 032 biome_096'),
  } },
  farmlands: { bl: 'Plain', scenes: {
    any: F('032 biome_131 biome_012 biome_096'),
    boreal: F('biome_047 032 biome_131 biome_012'),
    mediterranean: F('biome_130 L m biome_131 z'),
    arid: F('biome_075 biome_096 biome_130 biome_076'),
    steppe: F('006 biome_089 018 biome_047'),
    tropical: F('biome_096 biome_107 biome_130 biome_131'),
    east_asia: F('biome_096 biome_131 biome_130 032'),
  } },
  hills: { bl: 'Plain', scenes: {
    any: F('026 biome_098 biome_027 biome_012 z biome_050 biome_149'),
    boreal: F('007 biome_098 biome_050 J biome_047'),
    mediterranean: F('i L m O 026 biome_098'),
    arid: F('g 008 biome_088 b biome_076'),
    steppe: F('biome_148 007 014 013 biome_088'),
    tropical: F('k h biome_106 026 023'),
    highland: F('biome_148 012 014 biome_088 007'),
    east_asia: F('026 biome_098 O i biome_012'),
  } },
  terraced_hills: { bl: 'Plain', scenes: {
    any: F('biome_096 026 z m L O biome_012'),
    tropical: F('biome_096 026 O m k'),
    east_asia: F('biome_096 z 026 L m'),
  } },
  mountains: { bl: 'Mountain', scenes: {
    any: F('biome_034 biome_044 biome_128 004 001 031 biome_022'),
    boreal: F('004 biome_044 biome_128 001 007'),
    mediterranean: F('i O biome_034 031 h'),
    arid: F('g 008 biome_088 b'),
    steppe: F('007 biome_148 biome_050 012'),
    tropical: F('k h biome_034 biome_128'),
    highland: F('biome_148 012 007 biome_050 biome_022 031'),
    east_asia: F('biome_034 biome_044 031 biome_128 O'),
  } },
  desert: { bl: 'Desert', scenes: {
    any: F('010 009 d e b biome_067 008'),
  } },
  desert_mountains: { bl: 'Mountain', scenes: {
    any: F('g 008 biome_088 b biome_076 012'),
  } },
  oasis: { bl: 'Desert', scenes: {
    any: F('biome_075 biome_076 biome_065 biome_083 022 b'),
  } },
  drylands: { bl: 'Plain', scenes: {
    any: F('biome_040 biome_094 biome_067 biome_076 019 012 biome_148'),
    highland: F('012 biome_088 014 biome_148 013'),
    steppe: F('015 019 biome_040 014 012'),
    tropical: F('biome_040 biome_094 019 biome_130'),
  } },
  steppe: { bl: 'Steppe', scenes: {
    any: F('006 015 013 019 014 biome_040 012 biome_089'),
    boreal: F('006 biome_047 013'),
  } },
  jungle: { bl: 'Forest', scenes: {
    any: F('k h v biome_097 biome_106 023'),
  } },
  forest: { bl: 'Forest', scenes: {
    any: F('r 028 t u n'),
    boreal: F('a J u biome_046 003 t 024'),
    mediterranean: F('n h v 028'),
    arid: F('v biome_106 h n'),
    steppe: F('a biome_006 t biome_097'),
    tropical: F('k h v biome_097 biome_106'),
    highland: F('biome_106 J 023 025'),
    east_asia: F('028 003 biome_106 023 n t'),
  } },
  taiga: { bl: 'Forest', scenes: {
    any: F('J u a biome_046 003 t'),
  } },
  wetlands: { bl: 'Swamp', scenes: {
    any: F('034 005 f 033 biome_092 biome_046'),
    boreal: F('biome_046 034 f 005'),
    mediterranean: F('f 034 005 biome_092'),
    arid: F('005 034 biome_096 biome_075'),
    steppe: F('017 018 034 005'),
    tropical: F('biome_097 biome_096 005 biome_092 biome_087b biome_093b'),
    east_asia: F('biome_096 034 005 biome_092'),
  } },
  floodplains: { bl: 'Plain', scenes: {
    any: F('s 011 biome_048 biome_107 027 biome_092'),
    mediterranean: F('s 011 biome_053 biome_107'),
    arid: F('biome_075 017 biome_089 biome_096 018'),
    steppe: F('017 018 biome_089'),
    tropical: F('biome_096 biome_107 s 011'),
    east_asia: F('biome_107 biome_048 011 s biome_096'),
  } },
  // CK3 never fights a land combat in a sea province; kept so every terrain key resolves (e.g. a modded map or a bad locator)
  coastal_sea: { bl: 'Plain', scenes: { any: F('029 021 biome_028 020 p biome_005') } },
  sea: { bl: 'Plain', scenes: { any: F('029 021 biome_028 020 p biome_005') } },
};

// ---------------- vegetation of every field scene (bridge SceneVegetation.Derive over the scene files: tree types and densities
// inside the playable area). Plains and farmlands pools hold only open scenes.
const VEGETATION = Object.fromEntries(Object.entries({
  conifer_dense: '003 005 019 023 024 025 026 033 J L O biome_005 biome_006 biome_022 biome_044 biome_046 biome_047 biome_048 biome_087b biome_092 biome_093b biome_106 biome_128 biome_130 biome_149 i m z',
  mixed: '011 021 029 a h p r s t',
  deciduous_open: '004 020 biome_067 biome_097 n u v',
  open: '001 006 007 008 009 010 012 013 014 015 017 018 022 027 028 030 031 032 034 b biome_012 biome_025 biome_027 biome_028 biome_030 biome_034 biome_040 biome_050 biome_053 biome_065 biome_075 biome_076 biome_083 biome_088 biome_089 biome_094 biome_096 biome_098 biome_107 biome_114 biome_131 biome_148 d e f g k y',
}).flatMap(([tag, ids]) => F(ids).map(id => [id, tag])));

// ---------------- situational overrides (precedence in RULES.precedence) ----------------
const SITUATIONAL = {
  major_river_crossing: { // attacker came over a river_large adjacency / across a river province (Dnieper, Nile, Thames...)
    any: F('biome_053 biome_048 biome_092 018 011'),
    arid: F('biome_075 017 biome_089'),
    steppe: F('018 017 biome_089 biome_048'),
    tropical: F('biome_092 biome_107 biome_053'),
    boreal: F('biome_048 biome_053 011'),
  },
  river_crossing: { // attacker crossed a rivers.png river on the shared border (riverBorder/border >= 0.3)
    any: F('011 s 027 biome_053 biome_048 biome_107 f'),
    boreal: F('027 011 biome_048 f'),
    mediterranean: F('biome_053 s 011 030'),
    arid: F('biome_075 017 biome_089'),
    steppe: F('017 018 biome_089 biome_048'),
    tropical: F('biome_107 biome_096 s biome_092'),
    highland: F('biome_089 018 017'),
    east_asia: F('biome_107 011 s biome_048'),
    by_terrain: { mountains: F('biome_030 027 011'), hills: F('biome_030 027 011 s'), forest: F('027 011 f'), taiga: F('027 f 011') },
    by_terrain_buckets: ['temperate', 'boreal', 'mediterranean', 'east_asia'],
  },
  coastal: { // sea within RULES.coastal_near_px of the combat locator (or a 'sea' strait crossing)
    any: F('029 biome_005 biome_028 020 p'),
    boreal: F('biome_028 020 029 p'),
    mediterranean: F('021 029 020 biome_005'),
    arid: F('022 y biome_083 biome_065'),
    steppe: F('y biome_065 biome_083'),
    tropical: F('p 029 021 022'),
    highland: F('y biome_065'),
    east_asia: F('029 021 biome_028 p'),
    by_terrain: {
      hills: F('020 030 biome_005 biome_028'), mountains: F('020 030 biome_005'), terraced_hills: F('020 biome_005 029'),
      forest: F('p 020 biome_028'), taiga: F('p biome_028 020'),
    },
    by_terrain_buckets: ['temperate', 'boreal', 'mediterranean', 'east_asia'], // green coasts only; other buckets use their climate list
  },
  lakeside: { // lake within RULES.coastal_near_px
    any: F('biome_149 001 biome_025 biome_130 t'),
    arid: F('biome_076 biome_065'),
    steppe: F('biome_048 biome_025'),
  },
};
// riverside (river near the locator but no crossing) reuses river_crossing lists with RULES.riverside_chance

const RULES = {
  precedence: ['siege_or_relief', 'major_river_crossing', 'river_crossing', 'strait_crossing->coastal', 'coastal_near', 'lakeside_near', 'coastal_far(chance)', 'riverside(chance)', 'terrain'],
  coastal_near_px: 3,            // waterDistPx of kind sea <= 3 map px (~4.5 km) -> coastal list
  coastal_far_chance: 0.35,      // coastal province but sea farther than that -> this share of battles use the coastal list
  riverside_chance: 0.35,        // river within 2 px of the locator or majorRiverside, no crossing -> this share use river lists
  river_border_ratio: 0.3,       // shared border px touching a rivers.png river / border px >= this -> river crossing
  pick_top_n: 3,
  pick_weights: [0.5, 0.3, 0.2], // deterministic: hash(provinceId, CK3 date, "scene") picks among the top N after re-ordering
  reorder: [
    'winter season -> stable-move scenes with snow_layer to the front (46 field scenes have a winter-only snow layer)',
    'relief class high (relief7Raw >= p67 of the terrain) -> stable-move relief_high scenes forward; low (<= p33) -> relief_low forward',
    'battle with >= large_battle_agents agents -> drop scenes whose smaller side is < large_battle_min_scene_m (keep at least one)',
  ],
  large_battle_agents: 600,
  large_battle_min_scene_m: 1000,
  exclude_unlisted: F('benchmark 016 035 c'), // battle-capable but in neither sp_battle_scenes.xml nor custom_battle_scenes.xml
  fallback_scene: 'battle_terrain_029',        // CustomBattleData.CoreContentDefaultSceneName
};

// ---------------- sieges / settlement scenes ----------------
// CK3 holding_type (common/holdings/00_holdings.txt) -> Bannerlord settlement kind
const HOLDING_KIND = {
  castle_holding: 'castle', city_holding: 'town', church_holding: 'town', temple_citadel_holding: 'castle',
  tribal_holding: 'village', nomad_holding: 'field', herder_holding: 'field', none: 'field',
};
// fort_level (CK3 value) -> Bannerlord wall level 1..3. OpenSiegeMissionWithDeployment maps 0 to level_3, so always pass 1..3.
const FORT_TO_WALL = [{ max: 2, wall: 1 }, { max: 5, wall: 2 }, { max: 999, wall: 3 }]; // INFERRED, tune with logged fort levels
// CK3 building_gfx (culture) -> Bannerlord architecture culture. kind-specific entries {castle, town, village}.
const ARCHITECTURE = {
  western_building_gfx: 'vlandia', norse_building_gfx: 'sturgia', east_slavic_building_gfx: 'sturgia',
  mediterranean_building_gfx: 'empire', byzantine_building_gfx: 'empire', caucasian_building_gfx: 'empire',
  iberian_building_gfx: { castle: 'vlandia', town: 'empire', village: 'empire' },
  mena_building_gfx: 'aserai', arabic_group_building_gfx: 'aserai', berber_group_building_gfx: 'aserai',
  iranian_building_gfx: 'aserai', african_building_gfx: 'aserai', indian_building_gfx: 'aserai', southeast_asian_building_gfx: 'aserai',
  steppe_building_gfx: 'khuzait', tibetan_building_gfx: 'khuzait', chinese_building_gfx: 'khuzait', japanese_building_gfx: 'khuzait',
  amuric_building_gfx: 'khuzait', emishi_building_gfx: 'khuzait',
};
// western_building_gfx cultures whose heritage reads better as another Calradian culture (heritage keys VERIFIED in mapping.json heritage_map)
const HERITAGE_OVERRIDE = {
  heritage_goidelic: 'battania', heritage_brythonic: 'battania',
  heritage_baltic: 'sturgia', heritage_balto_finnic: 'sturgia', heritage_west_slavic: 'sturgia', heritage_volga_finnic: 'sturgia',
  heritage_ugro_permian: 'sturgia', heritage_north_germanic: 'sturgia',
};
// order of the CK3 if-chain `has_building_gfx = X` (most specific first; a culture may carry several, e.g. akan: african + mena)
const BUILDING_GFX_CHAIN = ['norse_building_gfx', 'east_slavic_building_gfx', 'byzantine_building_gfx', 'iberian_building_gfx',
  'caucasian_building_gfx', 'berber_group_building_gfx', 'arabic_group_building_gfx', 'iranian_building_gfx', 'tibetan_building_gfx',
  'japanese_building_gfx', 'emishi_building_gfx', 'amuric_building_gfx', 'chinese_building_gfx', 'southeast_asian_building_gfx',
  'indian_building_gfx', 'african_building_gfx', 'steppe_building_gfx', 'mena_building_gfx', 'mediterranean_building_gfx', 'western_building_gfx'];

// ---------------- season / weather / time of day ----------------
const SEASON = {
  bl_ids: { spring: 0, summer: 1, fall: 2, winter: 3 },  // TimeInfo.Season (BannerlordMissions). Never send "autumn".
  // base season by day-of-year when CK3 winter is NOT active (CK3 map_data/seasons.txt: summer 06.01-09.10, autumn 10.10-10.31,
  // winter 12.01-02.31, spring 04.01-05.01; gaps split at the midpoint)
  base: {
    default: [['01-01', 'spring'], ['06-01', 'summer'], ['09-11', 'fall'], ['12-01', 'fall']], // Dec-Feb without winter -> fall/spring split below
    arid: [['01-01', 'spring'], ['04-01', 'summer'], ['10-01', 'fall']],
    tropical: [['01-01', 'summer']],
  },
  no_winter_split: 'Dec -> fall, Jan-Feb -> spring (the campaign does the same for clear winter days: DefaultMapWeatherModel)',
  winter_from_modifier: { harsh: 'winter', normal: 'winter', mild: 'winter if bucket in mild_winter_buckets or winterBias >= 0.6, else no winter', none: 'no winter' },
  mild_winter_buckets: ['boreal', 'steppe', 'highland'],
  // fallback when the CK3 mod did not log the winter modifier: winter window by winter_severity_bias
  winter_window_by_bias: [
    { min: 0.8, from: '11-15', to: '03-31', chance: 1 },
    { min: 0.5, from: '12-01', to: '02-28', chance: 1 },
    { min: 0.2, from: '01-01', to: '02-15', chance: 'winterBias' },
    { min: 0, from: null, to: null, chance: 0 },
  ],
  permanent_snow_pct: 30, // CK3 'snow' ground class at the locator >= 30% (painted high-mountain snow) -> winter look any month
};

const WEATHER = {
  kinds: ['clear', 'overcast', 'light_rain', 'heavy_rain', 'fog', 'snow', 'blizzard', 'dust'],
  // probabilities (sum 1) by group and season; winter rows apply only when the winter season is active
  table: {
    temperate: { spring: { clear: 0.5, overcast: 0.15, light_rain: 0.2, heavy_rain: 0.08, fog: 0.07 }, summer: { clear: 0.7, overcast: 0.1, light_rain: 0.12, heavy_rain: 0.05, fog: 0.03 }, fall: { clear: 0.45, overcast: 0.2, light_rain: 0.2, heavy_rain: 0.07, fog: 0.08 }, winter: { clear: 0.3, overcast: 0.15, snow: 0.4, blizzard: 0.05, fog: 0.1 } },
    mediterranean: { spring: { clear: 0.65, overcast: 0.12, light_rain: 0.15, heavy_rain: 0.05, fog: 0.03 }, summer: { clear: 0.9, overcast: 0.05, light_rain: 0.03, heavy_rain: 0.02 }, fall: { clear: 0.6, overcast: 0.15, light_rain: 0.15, heavy_rain: 0.07, fog: 0.03 }, winter: { clear: 0.35, overcast: 0.2, snow: 0.3, blizzard: 0.05, fog: 0.1 } },
    arid: { spring: { clear: 0.85, overcast: 0.05, light_rain: 0.03, dust: 0.07 }, summer: { clear: 0.88, dust: 0.12 }, fall: { clear: 0.85, overcast: 0.05, light_rain: 0.02, dust: 0.08 }, winter: { clear: 0.5, overcast: 0.15, snow: 0.3, blizzard: 0.05 } },
    steppe: { spring: { clear: 0.65, overcast: 0.1, light_rain: 0.12, heavy_rain: 0.05, dust: 0.08 }, summer: { clear: 0.78, light_rain: 0.07, heavy_rain: 0.05, dust: 0.1 }, fall: { clear: 0.6, overcast: 0.15, light_rain: 0.12, dust: 0.08, fog: 0.05 }, winter: { clear: 0.3, overcast: 0.1, snow: 0.45, blizzard: 0.15 } },
    tropical: { spring: { clear: 0.65, overcast: 0.1, light_rain: 0.15, heavy_rain: 0.05, fog: 0.05 }, summer: { clear: 0.6, overcast: 0.1, light_rain: 0.15, heavy_rain: 0.1, fog: 0.05 }, fall: { clear: 0.6, overcast: 0.1, light_rain: 0.15, heavy_rain: 0.1, fog: 0.05 }, winter: { clear: 0.6, overcast: 0.15, light_rain: 0.15, fog: 0.1 } },
  },
  group_of_bucket: { temperate: 'temperate', boreal: 'temperate', east_asia: 'temperate', mediterranean: 'mediterranean', arid: 'arid', highland: 'steppe', steppe: 'steppe', tropical: 'tropical' },
  monsoon: { buckets: ['tropical'], months: [6, 7, 8, 9], override: { clear: 0.3, overcast: 0.2, light_rain: 0.25, heavy_rain: 0.25 } },
  ck3_winter_override: { harsh: { snow: 0.5, blizzard: 0.3, clear: 0.15, overcast: 0.05 }, normal: { snow: 0.5, blizzard: 0.1, clear: 0.25, overcast: 0.15 } }, // CK3 GetBadWeatherDesc: blizzard when any winter modifier
  desert_terrains: ['desert', 'oasis', 'desert_mountains'], // CK3 GetBadWeatherDesc: sandstorm here, never storm/downpour
  desert_terrain_override: { rain_to: 'dust' },
  fog_hours: [5, 10],                // fog only for start hours in this window, else -> overcast
  // AtmosphereInfo values (campaign model DefaultMapWeatherModel, including its interpolated path)
  atmosphere: {
    clear: { interpolated: 'semicloudy_field_battle', rain: 0, snow: 0, fog: 1 },
    overcast: { interpolated: 'semicloudy_field_battle', rain: 0, snow: 0, fog: 1.5, preset_family: 'Overcast' },
    light_rain: { interpolated: 'semi_rainy', rain: 0.7, snow: 0, fog: 1.5, preset_family: 'Overcast', runtime_rain: 0.5 },
    heavy_rain: { interpolated: 'dense_rainy', rain: 0.9, snow: 0, fog: 2, preset_family: 'HeavyRain' },
    fog: { interpolated: 'semicloudy_field_battle', rain: 0, snow: 0, fog: 4, preset_family: 'Foggy' },
    snow: { interpolated: 'semi_snowy', rain: 0.55, snow: 0.6, fog: 1.5, season: 'winter', runtime_snow: 0.6 },
    blizzard: { interpolated: 'dense_snowy', rain: 0.85, snow: 0.85, fog: 3, season: 'winter', runtime_snow: 0.9 },
    dust: { interpolated: 'semicloudy_field_battle', rain: 0, snow: 0, fog: 3, fog_color: [0.80, 0.68, 0.50], dryness: 1 },
  },
};

const TIME_OF_DAY = {
  // Tier 1a (stock OpenCustomBattleMission): only these hours have a preset (BannerlordMissions)
  legacy_hours: { 6: 0.2, 12: 0.35, 15: 0.35, 18: 0.1, 22: 0 },
  // Tier 1b (own opener): continuous hour inside a daylight window per season; night share configurable (default 0)
  daylight: { winter: [8.5, 15.5], spring: [7, 17.5], summer: [6, 19.5], fall: [7.5, 17] },
  night_share: 0,
  // AtmosphereName preset by hour for the preset path (Native/Atmospheres/*.xml; ordered by the presets' sun_altitude).
  // custom_battle: true = the same hour->preset pair the stock custom battle uses (BannerlordMissions), i.e. proven.
  presets_by_hour: [
    { from: 0, to: 4, clear: 'TOD_01_00_SemiCloudy', rain: 'TOD_01_00_HeavyRain', custom_battle: true },
    { from: 4, to: 5.5, clear: 'TOD_03_00_SemiCloudy' },
    { from: 5.5, to: 6.5, clear: 'TOD_06_00_SemiCloudy', fog: 'TOD_06_00_Foggy', overcast: 'TOD_06_00_Overcast', custom_battle: true },
    { from: 6.5, to: 7.5, clear: 'TOD_07_00_SemiCloudy', fog: 'TOD_06_00_Foggy', overcast: 'TOD_06_00_Overcast' },
    { from: 7.5, to: 8.5, clear: 'TOD_08_00_SemiCloudy', fog: 'TOD_06_00_Foggy', overcast: 'TOD_08_00_Overcast' },
    { from: 8.5, to: 9.5, clear: 'TOD_09_00_SemiCloudy', overcast: 'TOD_08_00_Overcast' },
    { from: 9.5, to: 10.5, clear: 'TOD_10_00_SemiCloudy', overcast: 'TOD_08_00_Overcast' },
    { from: 10.5, to: 11.5, clear: 'TOD_11_00_SemiCloudy', overcast: 'TOD_12_00_Overcast' },
    { from: 11.5, to: 14, clear: 'TOD_12_00_SemiCloudy', overcast: 'TOD_12_00_Overcast', rain: 'TOD_12_00_HeavyRain', custom_battle: true },
    { from: 14, to: 16.5, clear: 'TOD_04_00_SemiCloudy', overcast: 'TOD_12_00_Overcast', rain: 'TOD_12_00_HeavyRain', custom_battle: true },
    { from: 16.5, to: 17.5, clear: 'TOD_05_00_SemiCloudy', overcast: 'TOD_06_30_Cloudy_sunset2' },
    { from: 17.5, to: 20, clear: 'TOD_03_00_SemiCloudy', overcast: 'TOD_06_30_Cloudy_sunset2', rain: 'TOD_01_00_HeavyRain', custom_battle: true },
    { from: 20, to: 24, clear: 'TOD_01_00_SemiCloudy', rain: 'TOD_01_00_HeavyRain', custom_battle: true },
  ],
};

// ---------------- build ----------------
const scenes = JSON.parse(fs.readFileSync(BL_SCENES, 'utf8'));
const byId = new Map(scenes.map(s => [s.id, s]));
const sceneInfo = id => {
  const s = byId.get(id); if (!s) return { missing: true };
  const t = s.terrain || {}; const tags = s.terrain_tags || [];
  return {
    name: s.display_name || null, kind: s.kind, culture: s.culture || null,
    size_m: t.size_m || null, relief: tags.find(x => /^relief_/.test(x)) || null,
    snow_layer: tags.includes('snow_layer'), water: !!(s.water && s.water.exists),
    listed: { custom_battle: !!s.custom_battle, campaign: !!s.sp_battle },
    spawn_paths: s.spawn_paths ?? null,
    vegetation: VEGETATION[id],
  };
};

// relief terciles per CK3 terrain from the slim province index (relief7Raw = max-min heightmap raw over 7x7 map px at the locator)
const idx = JSON.parse(fs.readFileSync(CK3_INDEX, 'utf8'));
const cols = idx.meta.cols, ci = k => cols.findIndex(c => c.startsWith(k));
const iT = ci('terrain'), iR = ci('relief7Raw'), iFl = ci('flags');
const rel = {};
for (const r of idx.rows) { if (r[iFl] & 8) continue; const t = idx.meta.terrains[r[iT]]; (rel[t] = rel[t] || []).push(r[iR]); }
const q = (a, p) => { const s = a.slice().sort((x, y) => x - y); return s[Math.floor(p * (s.length - 1))]; };
const reliefTerciles = {}; for (const [t, a] of Object.entries(rel)) reliefTerciles[t] = [q(a, 1 / 3), q(a, 2 / 3)];

// siege + village lists from bl_scenes.json (custom-battle-listed scenes first; unlisted ones kept at the end, flagged)
const CULTURES = ['vlandia', 'empire', 'sturgia', 'battania', 'aserai', 'khuzait'];
const siegeScenes = {}, villageScenes = {}, untested = [];
for (const c of CULTURES) {
  siegeScenes[c] = { castle: [], town: [] };
  const sieges = scenes.filter(s => s.kind === 'siege' && s.culture === c).sort((a, b) => ((b.settlements || []).length - (a.settlements || []).length) || a.id.localeCompare(b.id));
  for (const s of sieges) { const k = s.subkind === 'castle' ? 'castle' : 'town'; if (s.custom_battle) siegeScenes[c][k].push(s.id); }
  for (const s of sieges) if (!s.custom_battle) { untested.push(s.id); siegeScenes[c][s.subkind === 'castle' ? 'castle' : 'town'].push(s.id); }
  villageScenes[c] = scenes.filter(s => s.kind === 'village' && s.culture === c && s.custom_battle && !/raid/.test(s.id)).map(s => s.id).sort();
}

const out = {
  meta: {
    version: 1, generated: new Date().toISOString(), bannerlord: 'v1.4.8', ck3: '1.20.0.3',
    sources: { bl_scenes: inRepo(BL_SCENES), ck3_index: inRepo(CK3_INDEX), builder: SELF, validator: sibling('validate_terrain_scenes.js'), resolver: sibling('battlefield_resolver.js') },
    note: 'Scene lists are hand-curated (INFERRED) and ordered best-first; ids are validated against bl_scenes.json and the install. Supersedes mapping.json terrain_scenes/climate_*/season_*/time_of_day.',
  },
  climate: { buckets: BUCKETS, fallback: BUCKET_FALLBACK, source: `bridge/data/ck3_province_climate.json (${sibling('ck3_province_climate.js')})`, mapping_json_alias: { cold: 'boreal', continental: 'temperate', highland: 'highland', temperate: 'temperate', steppe: 'steppe', arid: 'arid', tropical: 'tropical' } },
  terrain: Object.fromEntries(Object.entries(TERRAIN).map(([k, v]) => [k, { bl_terrain_type: v.bl, scenes: v.scenes }])),
  bl_terrain_type_ids: { Plain: 1, Desert: 2, Snow: 3, Forest: 4, Steppe: 5, Mountain: 7, Swamp: 15 },
  situational: SITUATIONAL,
  rules: Object.assign({}, RULES, { relief7_terciles: reliefTerciles }),
  siege: {
    holding_kind: HOLDING_KIND, fort_level_to_wall_level: FORT_TO_WALL,
    architecture: { building_gfx: ARCHITECTURE, heritage_override_for_western: HERITAGE_OVERRIDE, ck3_chain_order: BUILDING_GFX_CHAIN, default: 'vlandia' },
    scenes: siegeScenes, untested_scenes: untested,
    wall_hit_points: { default: [1, 1], max_entries: 2, note: 'Settlement.WallSectionCount = 2; FindRightMostWall handles 1-2 entries only (SiegeMissionPreparationHandler)' },
    scene_levels: 'level_<wall> siege (pass sceneUpgradeLevel 1..3; 0 means level_3 in the stock siege opener, BannerlordMissions)',
    prefer: ['water_plane when CK3 coastal/riverside', 'snow_layer in winter'],
  },
  village: { scenes: villageScenes, opener: 'OpenCustomBattleMission(sceneId, ..., sceneLevels = custom_battle.forced_scene_level or "")', use_when: 'tribal holding sieges; optional settlement-edge battles' },
  season: SEASON, weather: WEATHER, time_of_day: TIME_OF_DAY,
  scene_info: {},
};
const referenced = new Set();
const walk = v => { if (Array.isArray(v)) v.forEach(walk); else if (v && typeof v === 'object') Object.values(v).forEach(walk); else if (typeof v === 'string' && byId.has(v)) referenced.add(v); };
walk(out.terrain); walk(out.situational); walk(out.siege.scenes); walk(out.village.scenes); walk(RULES.fallback_scene);
for (const id of [...referenced].sort()) out.scene_info[id] = sceneInfo(id);
fs.writeFileSync(OUT, JSON.stringify(out, null, 1));
console.error('wrote', OUT, fs.statSync(OUT).size, 'bytes;', referenced.size, 'scenes referenced');
