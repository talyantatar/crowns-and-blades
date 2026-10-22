// validate_mapping.js — checks bridge/data/mapping.json against ck3_catalog.json, bl_catalog.json and the Bannerlord
// scene XML / SceneObj folders, then runs the reference resolver over every culture x era x MaA and a sample battle.
// Usage: node validate_mapping.js   (exit code 1 on any ERROR)
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
const ROOT = REPO;
const M = JSON.parse(fs.readFileSync(ROOT + '/bridge/data/mapping.json', 'utf8'));
const CK3 = JSON.parse(fs.readFileSync(ROOT + '/bridge/data/ck3_catalog.json', 'utf8'));
const BL = JSON.parse(fs.readFileSync(ROOT + '/bridge/data/bl_catalog.json', 'utf8'));
const RES = require('./mapping_resolver.js');
const BLMOD = path.join(gameDirs.bannerlordDir(), 'Modules').replace(/\\/g, '/');
const CK3_GAME = gameDirs.ck3GameDir().replace(/\\/g, '/');

const errors = [], warns = [], info = [];
const E = (s) => errors.push(s), W = (s) => warns.push(s), I = (s) => info.push(s);

// ---------- 1. every troop id anywhere in mapping.json -------------------------------------------------------------
const troopRefs = new Map(); // id -> [paths]
(function walk(o, p) {
  if (Array.isArray(o)) return o.forEach((x, i) => walk(x, p + '[' + i + ']'));
  if (o && typeof o === 'object') {
    for (const [k, v] of Object.entries(o)) {
      if (k === 'troop' && typeof v === 'string') { if (!troopRefs.has(v)) troopRefs.set(v, []); troopRefs.get(v).push(p); }
      else walk(v, p + '.' + k);
    }
  }
})(M, '$');
let badTroops = 0;
for (const [id, paths] of troopRefs) {
  const t = BL.troops[id];
  if (!t) { E(`troop ${id} not in bl_catalog.troops (${paths[0]})`); badTroops++; continue; }
  if (!t.loaded_in_customgame) { E(`troop ${id} is not loaded in CustomGame (${(t.game_types || []).join(',')})`); badTroops++; }
  if (t.is_female && !paths.some((p) => p.includes('commanders'))) W(`troop ${id} is female (used at ${paths[0]})`);
}
I(`troop ids referenced: ${troopRefs.size}, all in bl_catalog and CustomGame-loaded: ${badTroops === 0}`);
// commander bodies (CustomBattle/ModuleData/custombattlecharacters.xml, loaded for CustomGame + EditorGame)
const cmdIds = new Set(BL.commanders.map((c) => c.id));
let cmdRefs = 0;
for (const bc of M.bl_cultures) for (const [slot, arr] of Object.entries(M.commanders[bc] || {})) for (const c of arr) {
  cmdRefs++;
  const bc2 = BL.commanders.find((x) => x.id === c.character);
  if (!bc2) E(`commander ${c.character} not in bl_catalog.commanders`);
  else if (bc2.culture !== bc || (slot === 'female') !== !!bc2.is_female) E(`commander ${c.character} slot mismatch ${bc}/${slot}`);
}
const cbXml = fs.readFileSync(BLMOD + '/CustomBattle/ModuleData/custombattlecharacters.xml', 'utf8');
for (const id of cmdIds) if (!new RegExp('id="' + id + '"').test(cbXml)) E(`commander ${id} not found in custombattlecharacters.xml`);
I(`commander bodies referenced: ${cmdRefs} (all in custombattlecharacters.xml, CustomGame-loaded)`);

// ---------- 2. CK3 keys ------------------------------------------------------------------------------------------------
const ck3Cultures = Object.keys(CK3.cultures);
const missingC = ck3Cultures.filter((c) => !M.culture_map[c]);
const extraC = Object.keys(M.culture_map).filter((c) => !CK3.cultures[c]);
if (missingC.length) E('cultures without mapping: ' + missingC.join(','));
if (extraC.length) E('culture_map keys not in ck3_catalog: ' + extraC.join(','));
for (const [c, p] of Object.entries(M.culture_profile_map)) {
  if (!M.profiles[p]) E(`culture ${c} -> unknown profile ${p}`);
  else if (M.profiles[p].bl_culture !== M.culture_map[c]) E(`culture ${c}: culture_map ${M.culture_map[c]} != profile ${p} bl_culture`);
}
I(`cultures: ${ck3Cultures.length} in catalog, ${Object.keys(M.culture_map).length} mapped`);
for (const h of CK3.heritages) { if (!M.heritage_map[h] || !M.heritage_profile_map[h]) E('heritage unmapped: ' + h); }
for (const h of Object.keys(M.heritage_profile_map)) if (!CK3.heritages.includes(h)) E('heritage_profile_map key not in catalog: ' + h);
for (const g of CK3.unit_gfx) { if (!M.unit_gfx_map[g] || !M.unit_gfx_profile_map[g]) E('unit_gfx unmapped: ' + g); }
for (const k of Object.keys(M.heritage_gfx_overrides)) { const [h, g] = k.split('|'); if (!CK3.heritages.includes(h) || !CK3.unit_gfx.includes(g)) E('bad pair key ' + k); }
if (M.unit_gfx_test_order.length !== CK3.unit_gfx.length) E('unit_gfx_test_order incomplete');
// primary-first property of the has_unit_gfx chain
for (const [c, v] of Object.entries(CK3.cultures)) {
  const first = M.unit_gfx_test_order.find((g) => v.unit_gfx.includes(g));
  if (first !== v.unit_gfx[0]) E(`has_unit_gfx chain would classify ${c} as ${first} instead of primary ${v.unit_gfx[0]}`);
}
// runtime path (heritage + gfx only, as for hybrid/divergent cultures) == culture-key path
let pathMismatch = 0;
for (const [c, v] of Object.entries(CK3.cultures)) {
  const a = RES.resolveProfile(M, { culture: c }).profile;
  const b = RES.resolveProfile(M, { heritage: v.heritage, unit_gfx: v.unit_gfx[0] }).profile;
  if (a !== b) { pathMismatch++; E(`culture ${c}: key path ${a} != heritage/gfx path ${b}`); }
}
I(`culture key path == heritage/unit_gfx path for all cultures: ${pathMismatch === 0}`);
// MaA keys
const maaKeys = Object.keys(CK3.maa_types);
for (const k of maaKeys) if (!M.maa_tier[k]) E('maa_tier missing ' + k);
for (const k of Object.keys(M.maa_tier)) if (!CK3.maa_types[k]) E('maa_tier key not in catalog ' + k);
for (const k of Object.keys(M.maa_overrides)) if (!CK3.maa_types[k]) E('maa_overrides key not in catalog ' + k);
for (const k of CK3.codegen_keys.maa_keys) if (!M.maa_tier[k]) E('codegen maa key missing ' + k);
for (const bt of CK3.codegen_keys.base_types) { if (!M.base_roles[bt]) E('base type missing in base_roles ' + bt); if (!M.maa_base[bt]) E('maa_base missing ' + bt); }
for (const bt of Object.keys(M.maa_base)) for (const bc of M.bl_cultures) {
  const l = M.maa_base[bt][bc];
  if (!l) { E(`maa_base ${bt}/${bc} missing`); continue; }
  if (bt === 'siege_weapon') { if (l.length) E('siege_weapon should be empty'); continue; }
  const s = l.reduce((a, x) => a + x.weight, 0);
  if (!l.length || Math.abs(s - 1) > 0.03) E(`maa_base ${bt}/${bc} weights sum ${s.toFixed(2)}`);
}
I(`MaA: ${maaKeys.length} in catalog, ${Object.keys(M.maa_tier).length} with tier targets, ${Object.keys(M.maa_overrides).length} explicit overrides, ${Object.values(M.maa_tier).filter((x) => x.excluded).length} excluded (siege)`);
// ladders: every role x culture present, rungs sane
for (const [role, byC] of Object.entries(M.maa_ladders)) for (const bc of M.bl_cultures) {
  const lad = byC[bc]; if (!lad || !lad.length) { E(`ladder ${role}/${bc} missing`); continue; }
  for (const r of lad) { const s = r.troops.reduce((a, x) => a + x.weight, 0); if (Math.abs(s - 1) > 0.011) E(`ladder ${role}/${bc} T${r.tier} weights ${s}`); }
}
// ---------- 3. role/formation sanity over every culture x era x MaA ------------------------------------------------
const tr = (id) => BL.troops[id];
const isMounted = (id) => !!tr(id).mounted;
const isRanged = (id) => tr(id).ranged && tr(id).ranged !== 'none';
const expect = {
  heavy_infantry: (id) => !isMounted(id), pikemen: (id) => !isMounted(id), peasant_militia: (id) => !isMounted(id),
  archers: (id) => !isMounted(id) && isRanged(id), skirmishers: (id) => !isMounted(id), gunpowder: (id) => !isMounted(id) && tr(id).ranged === 'crossbow',
  light_cavalry: isMounted, heavy_cavalry: isMounted, camel_cavalry: (id) => isMounted(id) && tr(id).mount_kind === 'camel', elephant_cavalry: isMounted,
  archer_cavalry: (id) => isMounted(id) && isRanged(id), nomadic_horde: (id) => isMounted(id) && isRanged(id),
};
const roleExpect = { crossbowmen: (id) => !isMounted(id) && tr(id).ranged === 'crossbow' };
let combos = 0, roleBad = new Map(), empty = 0, multStats = {};
const profiles = Object.keys(M.profiles);
for (const p of profiles) for (const era of M.eras) for (const k of maaKeys) {
  const r = RES.resolveMaa(M, k, { profile: p, era });
  combos++;
  if (r.excluded) continue;
  if (!r.troops.length) { empty++; E(`empty resolution ${k} in ${p}/${era}`); continue; }
  const bt = M.maa_tier[k].base_type;
  const chk = roleExpect[r.role] || expect[bt];
  for (const x of r.troops) if (chk && !chk(x.troop)) { const key = `${k} -> ${x.troop} (${bt}/${r.role})`; roleBad.set(key, (roleBad.get(key) || 0) + 1); }
  if (r.agent_multiplier !== 1) multStats[k] = r.agent_multiplier;
}
for (const [k, n] of roleBad) W(`role/formation mismatch: ${k} in ${n} profile/era combos`);
I(`resolver: ${combos} profile x era x MaA combinations, ${empty} empty, ${roleBad.size} distinct role mismatches`);
I('MaA with agent_multiplier != 1 in at least one profile (last seen value): ' + JSON.stringify(multStats));
// terrain resolution sanity
for (const t of CK3.terrains.map((x) => x.key)) for (const k of ['war_elephant', 'light_horsemen', 'armored_horsemen', 'black_armor_cavalry']) {
  const r = RES.resolveMaa(M, k, { profile: 'western_feudal', era: 'culture_era_high_medieval', terrain: t });
  if (!r.troops.length) E(`terrain ${t} ${k} empty`);
}
// unknown future MaA key falls back to base type
const unk = RES.resolveMaa(M, 'future_unknown_maa', { profile: 'norse', era: 'culture_era_late_medieval', base_type: 'heavy_infantry' });
if (!unk.troops.length) E('unknown MaA key fallback failed'); else I('unknown MaA key (base heavy_infantry, norse) -> ' + unk.troops.map((x) => x.troop + ':' + x.weight).join(', '));
// levy / knights / commanders for every profile x era
for (const p of profiles) for (const era of M.eras) {
  for (const nomadic of [false, true]) {
    const l = RES.resolveLevy(M, { profile: p, era, nomadic });
    if (!l.troops || !l.troops.length) E(`levy empty ${p}/${era}`);
    else { const s = l.troops.reduce((a, x) => a + x.weight, 0); if (Math.abs(s - 1) > 0.03) E(`levy ${p}/${era} sum ${s}`); }
  }
  const kn = RES.resolveKnights(M, { profile: p, era });
  if (!kn || !kn.length) E(`knights empty ${p}/${era}`);
  else for (const x of kn) if (tr(x.troop).tier < 5) W(`knight troop below tier 5: ${x.troop} (${p})`);
}
for (const bc of M.bl_cultures) for (const female of [false, true]) {
  const c = RES.pickCommander(M, { bl_culture: bc, female });
  const cc = BL.commanders.find((x) => x.id === c);
  if (!c || !cc) E(`commander missing for ${bc} female=${female}`);
  else if (!!cc.is_female !== female) W(`commander for ${bc} female=${female} resolves to ${c} (${cc.culture}, no ${female ? 'female' : 'male'} body in ${bc})`);
}
// ---------- 4. scenes ------------------------------------------------------------------------------------------------
const xml = fs.readFileSync(BLMOD + '/CustomBattle/ModuleData/custom_battle_scenes.xml', 'utf8').replace(/<!--[\s\S]*?-->/g, '');
const fieldIds = new Set(); const allIds = new Set();
xml.replace(/<Scene\b([\s\S]*?)(?:\/>|>)/g, (s, a) => { const id = (a.match(/id="([^"]+)"/) || [])[1]; if (!id) return; allIds.add(id); if (!/is_(siege|village|lords_hall)_map="true"/.test(a)) fieldIds.add(id); });
const terrains = CK3.terrains.map((t) => t.key);
for (const t of terrains) if (!M.terrain_scenes[t] || !M.terrain_scenes[t].length) E('terrain without scenes: ' + t);
for (const t of Object.keys(M.terrain_scenes)) if (!terrains.includes(t)) E('terrain_scenes key not a CK3 terrain: ' + t);
const sceneIds = new Set([...Object.values(M.terrain_scenes).flat(), ...M.coastal_scenes, ...M.river_scenes, ...Object.values(M.climate_scene_overrides).flatMap((o) => Object.values(o).flat())]);
for (const id of sceneIds) {
  if (!fieldIds.has(id)) E(`scene ${id} is not an uncommented field scene in custom_battle_scenes.xml`);
  const dir = ['SandBoxCore', 'SandBox', 'CustomBattle', 'Native'].some((m) => fs.existsSync(`${BLMOD}/${m}/SceneObj/${id}/scene.xscene`));
  if (!dir) E(`scene ${id} has no SceneObj/<id>/scene.xscene`);
}
const unusedField = [...fieldIds].filter((x) => !sceneIds.has(x));
I(`scenes: ${sceneIds.size} referenced, all field scenes with SceneObj folders; custom-battle field scenes not used: ${unusedField.length ? unusedField.join(',') : 'none'}`);
for (const m of Object.keys(M.season_by_month)) if (!['spring', 'summer', 'fall', 'winter'].includes(M.season_by_month[m])) E('bad season ' + m);
if (Object.keys(M.season_by_month).length !== 12) E('season_by_month must have 12 months');
for (const [cl, t] of Object.entries(M.season_by_climate)) if (Object.keys(t).length !== 12) E('season_by_climate ' + cl + ' incomplete');
// geographical regions exist in CK3 map data
const geo = fs.readFileSync(CK3_GAME + '/map_data/geographical_regions/geographical_region.txt', 'utf8');
for (const c of M.climate_regions) if (!new RegExp('^' + c.region + '\\s*=\\s*\\{', 'm').test(geo)) E('geographical region not found: ' + c.region);
const traits = fs.readFileSync(CK3_GAME + '/common/traits/00_traits.txt', 'utf8');
for (const t of [...M.ck3_emit.commander_traits, ...Object.keys(M.commander_rules.traits), 'wounded_1', 'wounded_2', 'wounded_3', 'maimed']) if (!new RegExp('^' + t + '\\s*=\\s*\\{', 'm').test(traits)) E('CK3 trait not found: ' + t);
for (const t of CK3.terrains.map((x) => x.key)) if (!M.ck3_emit.terrain_chain.includes('terrain = ' + t)) E('terrain chain missing ' + t);
I(`CK3 emit chains: ${M.ck3_emit.heritage_chain.length} heritages, ${M.ck3_emit.unit_gfx_chain.length} unit_gfx, ${M.ck3_emit.era_chain.length} eras, ${M.ck3_emit.terrain_chain.length} terrains, ${M.ck3_emit.region_chain.length} regions, ${M.ck3_emit.commander_traits.length} commander traits (all trait keys found in 00_traits.txt)`);
const pmods = fs.readFileSync(CK3_GAME + '/common/modifiers/00_province_modifiers.txt', 'utf8');
for (const w of M.winter_state.chain) if (!new RegExp('^' + w + '\\s*=\\s*\\{', 'm').test(pmods)) E('winter modifier not found: ' + w);
for (const [mk, wk] of Object.entries(M.winter_state.maa_winter_key)) if (!Object.values(CK3.maa_types).some((x) => x.winter_bonus && x.winter_bonus[wk])) E('maa winter key unused: ' + wk);
I('winter modifiers ' + M.winter_state.chain.join(', ') + ' found in 00_province_modifiers.txt; season(Jan, temperate, none/mild/normal) = ' + ['winter_none_modifier','winter_mild_modifier','winter_normal_modifier'].map((w) => RES.seasonFor(M, { month: 1, climate: 'temperate', winter: w })).join('/'));
// override previews must agree with the resolver
for (const [k, ov] of Object.entries(M.maa_overrides)) if (!ov.resolved) E('override without preview ' + k);

// ---------- 5. sample battle end to end --------------------------------------------------------------------------
const sample = (() => {
  const att = { culture: 'french', era: 'culture_era_high_medieval', levies: 6000, maa: [['armored_footmen', 300], ['crossbowmen', 200], ['armored_horsemen', 150], ['gendarme', 100]], knights: [8, 12, 15, 6, 20] };
  const def = { culture: 'norse', era: 'culture_era_early_medieval', levies: 4000, maa: [['huscarl', 300], ['vigmen', 200], ['bondi', 300], ['varangian_veterans', 100]], knights: [10, 14, 9, 18] };
  const terrain = 'forest', climate = RES.climateFor(M, { regions: ['world_europe_west_francia', 'world_europe_west', 'world_europe'], terrain }), month = 10;
  const sideGroups = [att, def].map((s) => {
    const prof = RES.resolveProfile(M, { culture: s.culture });
    const groups = [{ id: s.culture + ':levy', kind: 'levy', soldiers: s.levies, agent_multiplier: 1, troops: RES.resolveLevy(M, { profile: prof.profile, era: s.era }).troops }];
    for (const [k, n] of s.maa) { const r = RES.resolveMaa(M, k, { profile: prof.profile, era: s.era, terrain }); groups.push({ id: s.culture + ':' + k, kind: 'maa', soldiers: n, agent_multiplier: r.agent_multiplier, troops: r.troops, tier: r.tier }); }
    return { prof, groups, heroes: 1 + s.knights.length, knights: s.knights, knightTroops: RES.resolveKnights(M, { profile: prof.profile, era: s.era }), commander: RES.pickCommander(M, { bl_culture: prof.bl_culture }) };
  });
  const alloc = RES.allocateAgents(M, sideGroups.map((s) => ({ groups: s.groups, heroes: s.heroes })), 1500);
  const scene = RES.pickScene(M, { terrain, climate, seed: 12345 }), season = RES.seasonFor(M, { month, climate });
  return { sideGroups, alloc, scene, season, climate };
})();
const lines = [];
lines.push(`sample: French (high medieval) vs Norse (early medieval) in forest, month 10 -> climate ${sample.climate}, scene ${sample.scene}, season ${sample.season}, scale ${sample.alloc.scale.toFixed(4)}`);
sample.sideGroups.forEach((s, i) => {
  lines.push(`  side ${i}: profile ${s.prof.profile} (${s.prof.bl_culture}), general ${s.commander}, knights ${s.knights.length} -> ${s.knightTroops.map((x) => x.troop).join('/')}, retinue per knight ${s.knights.map((p) => RES.knightRetinue(p, sample.alloc.scale)).join(',')}`);
  sample.alloc.sides[i].forEach((g, j) => lines.push(`    ${g.id}: ${g.soldiers} soldiers -> ${g.agents} agents  [${sample.sideGroups[i].groups[j].troops.map((x) => x.troop + ':' + x.weight).join(', ')}]${sample.sideGroups[i].groups[j].tier ? ' tier ' + sample.sideGroups[i].groups[j].tier : ''}`));
});
const tot = sample.alloc.sides.map((s) => s.reduce((a, g) => a + g.agents, 0));
const soldiers = sample.sideGroups.map((s) => s.groups.reduce((a, g) => a + g.soldiers, 0));
lines.push(`  agents ${tot[0]} vs ${tot[1]} (ratio ${(tot[0] / tot[1]).toFixed(3)}), soldiers ${soldiers[0]} vs ${soldiers[1]} (ratio ${(soldiers[0] / soldiers[1]).toFixed(3)})`);
if (Math.abs(tot[0] / tot[1] - soldiers[0] / soldiers[1]) / (soldiers[0] / soldiers[1]) > 0.05) W('sample battle side ratio drift > 5%');
// fake result: attacker wins; attacker lost 25% downed, defender 45% downed + 20% fled
const mk = (sideIdx, fDown, fFled) => sample.alloc.sides[sideIdx].map((g) => {
  const maa = sample.sideGroups[sideIdx].groups.find((x) => x.id === g.id); const key = g.id.split(':')[1]; const st = CK3.maa_types[key];
  const down = Math.round(g.agents * fDown);
  return { id: g.id, soldiers: g.soldiers, agents: g.agents, killed: down, unconscious: 0, fled: Math.round(g.agents * fFled), toughness: st ? st.toughness : 10, pursuit: st ? st.pursuit : 0, screen: st ? st.screen : 0 };
});
const cas = RES.casualtiesToCK3(M, mk(0, 0.25, 0), mk(1, 0.45, 0.2), { terrain: 'forest', winter: null, loserIsDefender: true });
const casW = RES.casualtiesToCK3(M, mk(0, 0.25, 0), mk(1, 0.45, 0.2), { terrain: 'wetlands', winter: 'winter_harsh_modifier', loserIsDefender: true });
lines.push(`  same result in wetlands + harsh winter: loser levy fatal ${casW.loser[0].fatal} (forest, no winter: ${cas.loser[0].fatal}); pursuit fraction ${casW.pursuit_fraction}`);
lines.push(`  fake result (attacker wins; att 25% downed, def 45% downed + 20% fled): pursuit fraction ${cas.pursuit_fraction}`);
for (const g of cas.winner) lines.push(`    W ${g.id}: casualties ${g.casualties}, fatal ${g.fatal}, routed returning ${g.routed_returning}`);
for (const g of cas.loser) lines.push(`    L ${g.id}: casualties ${g.casualties}, fatal ${g.fatal}, routed pool ${g.routed_pool}`);

// ---------- report ---------------------------------------------------------------------------------------------------
const out = ['# mapping.json validation', `mapping.json generated ${M.meta.generated}`, '', '## errors (' + errors.length + ')', ...errors.map((x) => '- ' + x), '', '## warnings (' + warns.length + ')', ...warns.map((x) => '- ' + x), '', '## info', ...info.map((x) => '- ' + x), '', '## sample battle', ...lines];
fs.writeFileSync(__dirname + '/validation_report.txt', out.join('\n'));
console.log(out.join('\n'));
process.exit(errors.length ? 1 : 0);
