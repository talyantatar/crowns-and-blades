// mapping_resolver.js — reference implementation of how bridge.exe should consume bridge/data/mapping.json.
// Pure functions, no I/O, no dependencies. Port 1:1 to C# (bridge, .NET 10). Used by build_mapping.js (maa_base),
// the validator and the doc generator.
'use strict';

const ERA_INDEX = { culture_era_tribal: 0, culture_era_early_medieval: 1, culture_era_high_medieval: 2, culture_era_late_medieval: 3 };
const r2 = (x) => Math.round(x * 100) / 100;

function eraOk(entry, era) {
  const e = ERA_INDEX[era] ?? 1;
  if (entry.min_era && e < ERA_INDEX[entry.min_era]) return false;
  if (entry.max_era && e > ERA_INDEX[entry.max_era]) return false;
  return true;
}
function pickEra(entries, era) { return (entries || []).find((x) => eraOk(x, era)) || null; }

// ---- culture --------------------------------------------------------------------------------------------------
// input: { culture?: key, heritage?: 'heritage_x', unit_gfx?: 'x_unit_gfx' (primary, i.e. first match of the
//          generated has_unit_gfx chain in mapping.unit_gfx_test_order) }
function resolveProfile(m, { culture, heritage, unit_gfx } = {}) {
  let profile = null, via = null;
  if (culture && m.culture_profile_map[culture]) { profile = m.culture_profile_map[culture]; via = 'culture'; }
  else if (heritage && unit_gfx && m.heritage_gfx_overrides[heritage + '|' + unit_gfx]) { profile = m.heritage_gfx_overrides[heritage + '|' + unit_gfx].profile; via = 'pair'; }
  else if (heritage && m.heritage_profile_map[heritage]) { profile = m.heritage_profile_map[heritage]; via = 'heritage'; }
  else if (unit_gfx && m.unit_gfx_profile_map[unit_gfx]) { profile = m.unit_gfx_profile_map[unit_gfx]; via = 'unit_gfx'; }
  else { profile = m.default_profile; via = 'default'; }
  return { profile, bl_culture: m.profiles[profile].bl_culture, via };
}

// ---- ladders ----------------------------------------------------------------------------------------------------
function interpolate(ladder, t) {
  const rungs = [...ladder].sort((a, b) => a.tier - b.tier);
  const lo = rungs[0], hi = rungs[rungs.length - 1];
  let mix;
  if (t <= lo.tier) mix = [[lo, 1]];
  else if (t >= hi.tier) mix = [[hi, 1]];
  else {
    let a = lo, b = hi;
    for (const r of rungs) { if (r.tier <= t) a = r; if (r.tier >= t) { b = r; break; } }
    mix = a === b ? [[a, 1]] : [[a, (b.tier - t) / (b.tier - a.tier)], [b, (t - a.tier) / (b.tier - a.tier)]];
  }
  const acc = {};
  for (const [r, w] of mix) for (const x of r.troops) acc[x.troop] = (acc[x.troop] || 0) + w * x.weight;
  let out = Object.entries(acc).map(([troop, weight]) => ({ troop, weight })).filter((x) => x.weight >= 0.05);
  const s = out.reduce((a, x) => a + x.weight, 0);
  return out.map((x) => ({ troop: x.troop, weight: r2(x.weight / s) })).sort((a, b) => b.weight - a.weight);
}
function agentMultiplier(ladder, t) {
  const tiers = ladder.map((r) => r.tier);
  const lo = Math.min(...tiers), hi = Math.max(...tiers);
  if (t > hi) return r2(Math.min(2, Math.pow(2, (t - hi) / 2)));
  if (t < lo) return r2(Math.max(0.5, Math.pow(2, (t - lo) / 2)));
  return 1;
}

// ---- MaA ----------------------------------------------------------------------------------------------------------
// ctx: { profile, era, terrain?, winter?: 'normal_winter'|'harsh_winter', base_type? (needed for unknown keys) }
function resolveMaa(m, key, ctx) {
  const info = m.maa_tier[key];
  const baseType = info ? info.base_type : ctx.base_type;
  const base = m.base_roles[baseType];
  if (!base) return { excluded: true, reason: 'unknown base type ' + baseType, troops: [] };
  if (base.exclude || (info && info.excluded)) return { excluded: true, reason: base.note, troops: [] };
  const ov = m.maa_overrides[key] || {};
  const prof = m.profiles[ctx.profile] || m.profiles[m.default_profile];
  let tier = ov.tier_target ?? (info ? info.tier_target : base.ref_tier);
  const [dlo, dhi] = (m.tier_rule && m.tier_rule.terrain_delta_clamp) || [-1.5, 1];
  let delta = 0;
  if (info && ctx.terrain && info.terrain_tier_delta && info.terrain_tier_delta[ctx.terrain] != null) delta += info.terrain_tier_delta[ctx.terrain];
  const wk = ctx.winter && ((m.winter_state && m.winter_state.maa_winter_key[ctx.winter]) || ctx.winter); // accepts winter_harsh_modifier or harsh_winter
  if (info && wk && info.winter_tier_delta && info.winter_tier_delta[wk] != null) delta += info.winter_tier_delta[wk];
  tier = r2(Math.max(0.5, tier + Math.min(dhi, Math.max(dlo, delta))));
  const role = ov.ladder_role || base.role;
  let ladder = null, source = null;
  const ref = ov.ladder_ref || (!ov.ladder_role ? base.ladder_ref : null);
  if (ref) { ladder = m.shared_ladders[ref]; source = 'shared:' + ref; }
  else if (ov.force_culture || base.force_culture) { const c = ov.force_culture || base.force_culture; ladder = m.maa_ladders[role][c]; source = role + ':' + c + ' (forced)'; }
  else {
    const po = prof.role_overrides && pickEra(prof.role_overrides[role], ctx.era);
    if (po && po.ladder) { ladder = po.ladder; source = 'profile:' + ctx.profile + ':' + role; }
    else if (po && po.use_culture) { ladder = m.maa_ladders[role][po.use_culture]; source = role + ':' + po.use_culture + ' (profile)'; }
    else { ladder = m.maa_ladders[role][prof.bl_culture]; source = role + ':' + prof.bl_culture; }
  }
  return { excluded: false, tier, role, ladder_source: source, troops: interpolate(ladder, tier), agent_multiplier: agentMultiplier(ladder, tier) };
}

// ---- levy / knights / commanders --------------------------------------------------------------------------------
function resolveLevy(m, { profile, era, nomadic }) {
  const prof = m.profiles[profile] || m.profiles[m.default_profile];
  let variant = prof.levy_variant || prof.bl_culture;
  if (nomadic && prof.bl_culture === 'khuzait') variant = 'steppe_nomadic';
  const e = m.levy_by_era[era] ? era : 'culture_era_early_medieval';
  return { variant, troops: m.levy_by_era[e][variant] };
}
function resolveKnights(m, { profile, era }) {
  const prof = m.profiles[profile] || m.profiles[m.default_profile];
  const pk = pickEra(prof.knights, era);
  return pk ? pk.troops : m.knights[prof.bl_culture];
}
function knightRetinue(prowess, scale, effMult = 0) {
  return Math.min(10, Math.round(Math.max(0, (2.236 * prowess * scale * (1 + effMult) - 4) / 4)));
}
// index lets the bridge rotate bodies when several commanders/army leaders share a culture
function pickCommander(m, { bl_culture, female, mounted = true, index = 0 }) {
  const c = m.commanders[bl_culture] || m.commanders[m.default_culture];
  const take = (arr) => arr[index % arr.length].character;
  if (female) {
    if (c.female && c.female.length) return take(c.female);
    const fb = m.commanders[(m.commander_rules.female_fallback || {})[bl_culture] || m.default_culture];
    if (fb && fb.female && fb.female.length) return take(fb.female);
  }
  const slots = mounted ? ['male_mounted', 'male_foot'] : ['male_foot', 'male_mounted'];
  for (const s of slots) if (c[s] && c[s].length) return take(c[s]);
  return take(m.commanders[m.default_culture].male_mounted);
}

// ---- scaling ------------------------------------------------------------------------------------------------------
// sides: [{ groups: [{ id, kind:'levy'|'maa', soldiers, agent_multiplier }], heroes: n }, {...}]
function allocateAgents(m, sides, maxTotal) {
  const total = maxTotal ?? m.scaling.max_total_agents_default;
  const heroes = sides.reduce((a, s) => a + (s.heroes || 0), 0);
  const soldiers = sides.map((s) => s.groups.reduce((a, g) => a + g.soldiers, 0));
  const s = Math.min(1, Math.max(0, total - heroes) / Math.max(1, soldiers[0] + soldiers[1]));
  const out = sides.map((side) => {
    const quotas = side.groups.map((g) => ({ g, exact: g.soldiers * s * (g.agent_multiplier || 1) }));
    const res = quotas.map((q) => ({ id: q.g.id, kind: q.g.kind, soldiers: q.g.soldiers, agents: q.g.soldiers > 0 ? Math.max(q.g.kind === 'levy' ? 0 : 1, Math.floor(q.exact)) : 0, rem: q.exact - Math.floor(q.exact) }));
    const target = Math.round(quotas.reduce((a, q) => a + q.exact, 0));
    let cur = res.reduce((a, r) => a + r.agents, 0);
    for (const r of [...res].sort((a, b) => b.rem - a.rem)) { if (cur >= target) break; if (r.soldiers > 0) { r.agents++; cur++; } }
    return res.map(({ rem, ...x }) => x);
  });
  // ratio guard: per-regiment minimums can over-represent a small side; trim its levy pool back to within 5%
  const want = soldiers[0] / Math.max(1, soldiers[1]);
  const sum = (i) => out[i].reduce((a, g) => a + g.agents, 0);
  for (let guard = 0; guard < 10000; guard++) {
    const have = sum(0) / Math.max(1, sum(1));
    if (Math.abs(have - want) / want <= 0.05) break;
    const big = have > want ? 0 : 1;
    const levy = out[big].find((g) => g.kind === 'levy' && g.agents > 0);
    if (!levy) break;
    levy.agents--;
  }
  return { scale: s, sides: out };
}

// ---- casualties back to CK3 -------------------------------------------------------------------------------------
// group result: { soldiers, agents, killed, unconscious, fled, toughness (per soldier), pursuit, screen }
// opts.terrain / opts.winter (province winter modifier) / opts.loserIsDefender apply CK3 terrain and harsh-winter casualty modifiers
function casualtiesToCK3(m, winnerGroups, loserGroups, { useDeciderKilled = false, annihilated = false, terrain = null, winter = null, loserIsDefender = false } = {}) {
  const tm = (m.casualties.fatal_ratio_modifiers.terrain || {})[terrain] || { attacker: {}, defender: {} };
  const harsh = winter === 'winter_harsh_modifier' || winter === 'harsh_winter';
  const ratio = (isDef) => 0.3 * (1 + ((isDef ? tm.defender : tm.attacker).hard_casualty_modifier || 0)) * (harsh ? 1.2 : 1);
  let conv = ratio(!loserIsDefender); // winner side ratio, reassigned for the loser below
  const main = (g) => {
    const cas = g.agents ? Math.round(((g.killed + g.unconscious) / g.agents) * g.soldiers) : 0;
    const fatal = useDeciderKilled ? Math.round((g.killed / Math.max(1, g.agents)) * g.soldiers) : Math.round(cas * conv);
    return { cas, fatal };
  };
  const win = winnerGroups.map((g) => { const r = main(g); return { id: g.id, casualties: r.cas, fatal: r.fatal, routed_returning: r.cas - r.fatal }; });
  const alive = (g) => Math.max(0, g.soldiers - (g.agents ? Math.round(((g.killed + g.unconscious + (g.fled || 0)) / g.agents) * g.soldiers) : 0));
  const P = winnerGroups.reduce((a, g) => a + alive(g) * (g.pursuit || 0), 0);
  const Sc = loserGroups.reduce((a, g) => a + alive(g) * (g.screen || 0), 0);
  conv = ratio(loserIsDefender);
  const pre = loserGroups.map((g) => { const r = main(g); const routed = r.cas - r.fatal + (g.agents ? Math.round(((g.fled || 0) / g.agents) * g.soldiers) : 0); return { g, r, routed }; });
  const T = pre.reduce((a, x) => a + x.routed * (x.g.toughness || 10), 0);
  const retreat = ((loserIsDefender ? tm.defender : tm.attacker).retreat_losses || 0);
  const q = annihilated ? 1 : Math.min(1, Math.max(0.01, (0.05 + 0.5 * Math.max(0, P - Sc) / Math.max(1, T)) * (1 + retreat)));
  const lose = pre.map((x) => ({ id: x.g.id, casualties: x.r.cas, fatal: Math.min(x.g.soldiers, x.r.fatal + Math.round(x.routed * q)), routed_pool: x.routed }));
  return { pursuit_fraction: r2(q), winner: win, loser: lose };
}

// ---- scene / season -----------------------------------------------------------------------------------------------
function climateFor(m, { regions = [], terrain } = {}) {
  for (const c of m.climate_regions) if (regions.includes(c.region)) return c.climate;
  return m.terrain_default_climate[terrain] || m.climate_default;
}
function seededRandom(seed) { let x = (seed >>> 0) || 1; return () => { x ^= x << 13; x >>>= 0; x ^= x >> 17; x ^= x << 5; x >>>= 0; return x / 4294967296; }; }
function pickScene(m, { terrain, climate, coastal = false, riverside = false, seed = 1, season = null, prefer_culture = null }) {
  const rnd = seededRandom(seed);
  let pool = (m.climate_scene_overrides[climate] || {})[terrain] || m.terrain_scenes[terrain] || [];
  if (coastal && ['plains', 'farmlands', 'drylands', 'hills'].includes(terrain) && rnd() < 0.3) pool = m.coastal_scenes;
  else if (riverside && rnd() < 0.3) pool = m.river_scenes;
  const tags = (id) => (m.scenes[id] && m.scenes[id].terrain_tags) || [];
  if (season === 'winter') { const snowy = pool.filter((id) => tags(id).includes('snow_layer')); if (snowy.length) pool = snowy; }
  if (prefer_culture && rnd() < 0.5) { const own = pool.filter((id) => m.scenes[id] && m.scenes[id].culture_hint === prefer_culture); if (own.length) pool = own; }
  if (!pool.length) return m.terrain_scene_rules.fallback_scene.split(' ')[0];
  return pool[Math.floor(rnd() * pool.length)];
}
// winter = province winter modifier key if the CK3 mod emitted it (authoritative), else month + climate
function seasonFor(m, { month, climate, winter = null }) {
  const table = (m.season_by_climate[climate] || m.season_by_month)[month];
  if (winter === 'winter_harsh_modifier' || winter === 'winter_normal_modifier') return 'winter';
  if (winter === 'winter_mild_modifier') return ['cold', 'continental', 'highland', 'steppe'].includes(climate) ? 'winter' : 'fall';
  if (winter === 'winter_none_modifier' && table === 'winter') return 'fall';
  return table;
}

module.exports = { ERA_INDEX, eraOk, pickEra, resolveProfile, interpolate, agentMultiplier, resolveMaa, resolveLevy, resolveKnights, knightRetinue, pickCommander, allocateAgents, casualtiesToCK3, climateFor, pickScene, seasonFor };
