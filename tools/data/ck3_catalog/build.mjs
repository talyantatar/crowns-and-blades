// Build bridge/data/ck3_catalog.json from CK3 1.20 game files (read-only).
// Usage: node build.mjs [outJson]   (CK3 install: env CK3_GAME_DIR, else found in the Steam libraries)
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { parseFile, listTxt, get, getItem, getAll, values, isBlock, vars, num, compact, toObj, walkTrigger } from './pdx.mjs';

// Repository root: the nearest folder above this script that holds tools/gen/lib/game_dirs.cjs.
const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = (() => {
  for (let d = HERE; ; d = path.dirname(d)) {
    if (fs.existsSync(path.join(d, 'tools', 'gen', 'lib', 'game_dirs.cjs'))) return d;
    if (path.dirname(d) === d) throw new Error(`repository root not found above ${HERE}`);
  }
})();
const gameDirs = createRequire(import.meta.url)(path.join(REPO, 'tools', 'gen', 'lib', 'game_dirs.cjs'));
const SELF = gameDirs.repoRelative(fileURLToPath(import.meta.url));
const GAME = gameDirs.ck3GameDir().replace(/\\/g, '/');
const C = GAME + '/common';
const OUT = process.argv[2] || path.join(REPO, 'bridge', 'data', 'ck3_catalog.json');
const rel = (f) => path.relative(GAME, f).replace(/\\/g, '/');
const uniq = (a) => [...new Set(a.filter((x) => x !== undefined && x !== null && x !== ''))];
const topItems = (root) => root.items.filter((it) => it.k && !it.k.startsWith('@') && isBlock(it.v));

// ---------------------------------------------------------------- DLC map
const dlcTriggers = {}; // has_xxx_dlc_trigger -> [features]
const scriptedTriggers = {}; // name -> {file,line,block}
for (const f of listTxt(C + '/scripted_triggers')) {
  const r = parseFile(f);
  for (const it of topItems(r)) {
    scriptedTriggers[it.k] = { file: rel(f), line: it.line, block: it.v };
  }
}
for (const [name, st] of Object.entries(scriptedTriggers)) {
  const feats = [];
  walkTrigger(st.block, (k, v) => { if (k === 'has_dlc_feature' && !isBlock(v)) feats.push(v); });
  if (/dlc/.test(name) && feats.length) dlcTriggers[name] = uniq(feats);
}
// dlc folders
const dlcs = [];
for (const d of fs.readdirSync(GAME + '/dlc')) {
  const df = fs.readdirSync(path.join(GAME, 'dlc', d)).find((x) => x.endsWith('.dlc'));
  if (!df) continue;
  const r = parseFile(path.join(GAME, 'dlc', d, df));
  dlcs.push({ folder: d, name: get(r, 'name'), steam_id: get(r, 'steam_id'), has_common_folder: fs.existsSync(path.join(GAME, 'dlc', d, 'common')), subfolders: fs.readdirSync(path.join(GAME, 'dlc', d)).filter((x) => !x.includes('.')) });
}
// codename -> feature (file prefixes used in common/ file names)
const CODENAME_FEATURE = { fp1: 'the_northern_lords', ep1: 'royal_court', fp2: 'the_fate_of_iberia', ep2: 'tours_and_tournaments', fp3: 'legacy_of_persia', ce1: 'legends_of_the_dead', ep3: 'roads_to_power', mpo: 'khans_of_the_steppe', tgp: 'all_under_heaven', pam: 'by_god_alone' };
function dlcFeaturesIn(block) {
  const feats = [];
  walkTrigger(block, (k, v, ctx) => {
    if (ctx.negated) return;
    if (k === 'has_dlc_feature' && !isBlock(v)) feats.push(v);
    if (dlcTriggers[k] && v === 'yes') feats.push(...dlcTriggers[k]);
  });
  return uniq(feats);
}

// ---------------------------------------------------------------- culture objects with parameters
const paramGrants = {}; // param -> [{kind,key}]
const addGrant = (p, kind, key) => { (paramGrants[p] ??= []).push({ kind, key }); };

const traditions = {};
for (const f of listTxt(C + '/culture/traditions')) {
  const r = parseFile(f);
  for (const it of topItems(r)) {
    const params = Object.keys(toObj(get(it.v, 'parameters') ?? { items: [] }) || {});
    const isShown = get(it.v, 'is_shown'); const canPick = get(it.v, 'can_pick');
    const heritageReq = [];
    walkTrigger(isShown, (k, v, ctx) => { if (k === 'has_cultural_pillar' && !ctx.negated && !isBlock(v)) heritageReq.push(v); });
    traditions[it.k] = {
      category: get(it.v, 'category'),
      parameters: params,
      dlc_features: uniq([...dlcFeaturesIn(isShown), ...dlcFeaturesIn(canPick)]),
      shown_for_pillars: uniq(heritageReq),
      source_file: rel(f), line: it.line,
    };
    for (const p of params) addGrant(p, 'tradition', it.k);
  }
}
const innovations = {};
const innovationUnlocksMaa = {}; // maa -> [innovation]
for (const f of listTxt(C + '/culture/innovations')) {
  const r = parseFile(f);
  for (const it of topItems(r)) {
    const pot = get(it.v, 'potential');
    const params = Object.keys(toObj(get(it.v, 'parameters') ?? { items: [] }) || {});
    const unl = getAll(it.v, 'unlock_maa').filter((x) => !isBlock(x));
    const potPillars = [], potParams = [];
    walkTrigger(pot, (k, v, ctx) => {
      if (ctx.negated || isBlock(v)) return;
      if (k === 'has_cultural_pillar') potPillars.push(v);
      if (k === 'has_cultural_parameter') potParams.push(v);
    });
    innovations[it.k] = {
      culture_era: get(it.v, 'culture_era'), group: get(it.v, 'group'), region: get(it.v, 'region'),
      potential: pot ? compact(pot) : undefined, potential_pillars: uniq(potPillars), potential_parameters: uniq(potParams),
      unlock_maa: unl, custom: getAll(it.v, 'custom').filter((x) => !isBlock(x)), parameters: params,
      flags: getAll(it.v, 'flag').filter((x) => !isBlock(x)),
      dlc_features: dlcFeaturesIn(pot),
      source_file: rel(f), line: it.line,
    };
    for (const m of unl) (innovationUnlocksMaa[m] ??= []).push(it.k);
    for (const p of params) addGrant(p, 'innovation', it.k);
  }
}
const pillars = {};
for (const f of listTxt(C + '/culture/pillars')) {
  const r = parseFile(f);
  for (const it of topItems(r)) {
    const params = Object.keys(toObj(get(it.v, 'parameters') ?? { items: [] }) || {});
    pillars[it.k] = { type: get(it.v, 'type'), parameters: params, source_file: rel(f), line: it.line };
    for (const p of params) addGrant(p, 'pillar', it.k);
  }
}
const eraUnlocks = {};
for (const f of listTxt(C + '/culture/eras')) {
  const r = parseFile(f);
  for (const it of topItems(r)) for (const m of getAll(it.v, 'unlock_maa')) if (!isBlock(m)) (eraUnlocks[m] ??= []).push(it.k);
}

// ---------------------------------------------------------------- religions: holy order MaA
const holyOrderMaa = {}; // maa -> [religion or faith]
const religionHolyOrderMaa = {};
for (const f of listTxt(C + '/religion/religion_types')) {
  const r = parseFile(f);
  for (const it of topItems(r)) {
    const h = get(it.v, 'holy_order_maa');
    if (h) { const l = values(h); religionHolyOrderMaa[it.k] = l; for (const m of l) (holyOrderMaa[m] ??= []).push(it.k); }
    const faiths = get(it.v, 'faiths');
    if (isBlock(faiths)) for (const fa of faiths.items) {
      const hf = isBlock(fa.v) ? get(fa.v, 'holy_order_maa') : undefined;
      if (hf) { const l = values(hf); religionHolyOrderMaa[`${it.k}/${fa.k}`] = l; for (const m of l) (holyOrderMaa[m] ??= []).push(`${it.k}/${fa.k}`); }
    }
  }
}

// ---------------------------------------------------------------- MaA
const CONTROL = new Set(['AND', 'OR', 'NOT', 'NOR', 'NAND', 'trigger_if', 'trigger_else_if', 'trigger_else', 'limit', 'custom_tooltip', 'text', 'custom_description', 'calc_true_if', 'amount', 'any_accolade', 'accolade_attribute_level', 'culture', 'scope:character', 'rite', 'faith', 'religion', 'dynasty', 'house', 'capital_province', 'capital_county', 'location', 'domicile', 'primary_title', 'liege', 'top_liege']);
const maa = {};
const maaFiles = listTxt(C + '/men_at_arms_types');
for (const f of maaFiles) {
  const r = parseFile(f);
  const vm = vars(r);
  const codename = (path.basename(f).match(/_(fp\d|ep\d|mpo|tgp|ce\d|pam)_/) || [])[1];
  for (const it of topItems(r)) {
    const b = it.v;
    const N = (k) => num(get(b, k), vm);
    const can = get(b, 'can_recruit');
    const conds = [];
    const params = [], innos = [], pillarsReq = [], gov = [], faith = [], accolade = [], other = [], dlc = [];
    let titleOnly = null;
    walkTrigger(can, (k, v, ctx) => {
      if (ctx.path.includes("valid_for_maa_trigger") || ctx.path.includes("accolade_attribute_level")) return;
      if (k === 'valid_for_maa_trigger' && isBlock(v)) { const p = get(v, 'PARAMETER'); if (p) { params.push(p); conds.push({ key: 'has_cultural_parameter', value: p, negated: ctx.negated, via: 'valid_for_maa_trigger' }); } return; }
      if (k === 'accolade_attribute_level' && isBlock(v)) { accolade.push(`${get(v, 'type')} level>=${(getItem(v, 'level') || {}).v}`); return; }
      if (isBlock(v)) return;
      if (CONTROL.has(k)) return;
      conds.push({ key: k, value: v, negated: ctx.negated, ...(ctx.op !== '=' ? { op: ctx.op } : {}) });
      if (ctx.negated) { if (k === 'exists' && v === 'scope:title') titleOnly = 'not_title_maa'; return; }
      if (k === 'exists' && v === 'scope:title') titleOnly = 'title_maa_only';
      else if (k === 'has_cultural_parameter') params.push(v);
      else if (k === 'has_innovation') innos.push(v);
      else if (k === 'has_cultural_pillar') pillarsReq.push(v);
      else if (k === 'government_has_flag' || k === 'has_government') gov.push(`${k}=${v}`);
      else if (/doctrine|faith|religion/.test(k)) faith.push(`${k}=${v}`);
      else if (k === 'has_dlc_feature') dlc.push(v);
      else if (dlcTriggers[k]) dlc.push(...dlcTriggers[k]);
      else other.push(`${k}${ctx.op && ctx.op !== '=' ? ctx.op : '='}${v}`);
    });
    // DLC features reachable through unlock chain
    const unlockInnos = innovationUnlocksMaa[it.k] || [];
    const grants = uniq(params).flatMap((p) => (paramGrants[p] || []).map((g) => ({ parameter: p, ...g })));
    const chainDlc = uniq([
      ...dlc,
      ...dlcFeaturesIn(can),
      ...unlockInnos.flatMap((i) => innovations[i]?.dlc_features || []),
      ...grants.flatMap((g) => (g.kind === 'tradition' ? traditions[g.key]?.dlc_features || [] : g.kind === 'innovation' ? innovations[g.key]?.dlc_features || [] : [])),
    ]);
    // Is DLC required on every unlock path, or only on some alternatives?
    const pathFeats = [
      ...unlockInnos.map((i) => innovations[i]?.dlc_features || []),
      ...grants.map((g) => (g.kind === 'tradition' ? traditions[g.key]?.dlc_features || [] : g.kind === 'innovation' ? innovations[g.key]?.dlc_features || [] : [])),
    ];
    const directDlc = uniq([...dlc, ...dlcFeaturesIn(can)]);
    const allPathsGated = pathFeats.length > 0 && pathFeats.every((f) => f.length > 0);
    const dlcRequired = uniq([...directDlc, ...(allPathsGated ? pathFeats.flat() : [])]);
    const dlcSomePaths = allPathsGated ? [] : uniq(pathFeats.flat()).filter((f) => !dlcRequired.includes(f));
    // unlock text
    const parts = [];
    if (get(b, 'special_recruit_only') === 'yes') parts.push('special_recruit_only (never recruitable in GUI; created by code/effects/holy orders/events)');
    if (holyOrderMaa[it.k]) parts.push(`holy_order_maa of: ${holyOrderMaa[it.k].join(', ')}`);
    for (const i of unlockInnos) {
      const inn = innovations[i];
      const pot = [...inn.potential_pillars, ...inn.potential_parameters.map((p) => `param ${p} <- ${(paramGrants[p] || []).map((g) => g.key).join('|') || '?'}`)];
      parts.push(`innovation ${i} (${inn.culture_era}${inn.region ? `, region ${inn.region}` : ''}${pot.length ? `, potential: ${pot.join(' & ')}` : ''})`);
    }
    for (const p of uniq(params)) {
      const g = (paramGrants[p] || []).map((x) => `${x.kind} ${x.key}`);
      parts.push(`cultural parameter ${p} <- ${g.length ? g.join(' | ') : '(no granting tradition/innovation found)'}`);
    }
    for (const i of uniq(innos)) parts.push(`has_innovation ${i}${innovations[i] ? ` (${innovations[i].culture_era})` : ''}`);
    for (const p of uniq(pillarsReq)) parts.push(`culture pillar ${p}`);
    for (const a of uniq(accolade)) parts.push(`accolade attribute ${a}`);
    for (const g of uniq(gov)) parts.push(g);
    for (const x of uniq(faith)) parts.push(x);
    if (titleOnly === 'title_maa_only') parts.push('title MaA only');
    if (titleOnly === 'not_title_maa') parts.push('not for title MaA');
    for (const o of uniq(other)) parts.push(o);
    if (!can && !unlockInnos.length && get(b, 'special_recruit_only') !== 'yes') parts.push('base: no can_recruit, no innovation gate');
    const negs = conds.filter((c) => c.negated && !(c.key === 'exists')).map((c) => `${c.key}${c.op || '='}${c.value}`);
    if (negs.length) parts.push(`blocked if (inside NOT): ${uniq(negs).join(', ')}`);
    if (dlcRequired.length) parts.push(`DLC required: ${dlcRequired.join(', ')}`);
    if (dlcSomePaths.length) parts.push(`DLC on some unlock paths only: ${dlcSomePaths.join(', ')}`);

    const illus = getAll(b, 'illustration').filter(isBlock).map((ib) => ({ reference: get(ib, 'reference'), trigger: get(ib, 'trigger') ? compact(get(ib, 'trigger')) : undefined }));
    let category = 'regular';
    const fileBase = path.basename(f);
    if (/accolade/.test(fileBase)) category = 'accolade';
    else if (/holy_order/.test(fileBase)) category = 'holy_order';
    else if (/cultural/.test(fileBase)) category = 'cultural';
    else if (/regional/.test(fileBase)) category = 'regional';
    if (/^maa_(bandits|poachers|marauders|thieves)$/.test(it.k)) category = 'event_troops';
    if (it.k === 'nomadic_riders') category = 'nomad_herd';
    maa[it.k] = {
      base_type: get(b, 'type'),
      damage: N('damage'), toughness: N('toughness'), pursuit: N('pursuit') ?? 0, screen: N('screen') ?? 0,
      stack: N('stack'), hired_stack_size: N('hired_stack_size'),
      siege: N('siege_value') ?? 0, siege_tier: N('siege_tier'), fights_in_main_phase: get(b, 'fights_in_main_phase'),
      max_regiments: N('max_regiments'), max_sub_regiments: N('max_sub_regiments'), max_size_override: N('max'),
      special_recruit_only: get(b, 'special_recruit_only') === 'yes' || undefined,
      allowed_in_hired_troops: get(b, 'allowed_in_hired_troops'),
      holy_order_fallback: get(b, 'holy_order_fallback'), mercenary_fallback: get(b, 'mercenary_fallback'),
      access_through_subject: get(b, 'access_through_subject'),
      terrain_bonus: get(b, 'terrain_bonus') ? toObj(get(b, 'terrain_bonus'), vm) : undefined,
      winter_bonus: get(b, 'winter_bonus') ? toObj(get(b, 'winter_bonus'), vm) : undefined,
      holding_bonus: get(b, 'holding_bonus') ? toObj(get(b, 'holding_bonus'), vm) : undefined,
      counters: get(b, 'counters') ? toObj(get(b, 'counters'), vm) : undefined,
      provision_cost: N('provision_cost'),
      buy_cost: get(b, 'buy_cost') ? compact(get(b, 'buy_cost')) : undefined,
      icon: get(b, 'icon'),
      illustrations: illus.length ? illus : undefined,
      category,
      unlock: parts.join('; '),
      unlock_detail: {
        innovations_unlocking: unlockInnos.length ? unlockInnos : undefined,
        innovation_potential: unlockInnos.length ? unlockInnos.map((i) => ({ innovation: i, culture_era: innovations[i].culture_era, region: innovations[i].region, pillars: innovations[i].potential_pillars, parameters: innovations[i].potential_parameters.map((p) => ({ parameter: p, granted_by: paramGrants[p] || [] })), potential: innovations[i].potential })) : undefined,
        era_tooltips: eraUnlocks[it.k],
        required_parameters: uniq(params).length ? uniq(params) : undefined,
        parameter_granted_by: grants.length ? grants : undefined,
        required_innovations: uniq(innos).length ? uniq(innos) : undefined,
        required_pillars: uniq(pillarsReq).length ? uniq(pillarsReq) : undefined,
        accolade: uniq(accolade).length ? uniq(accolade) : undefined,
        government: uniq(gov).length ? uniq(gov) : undefined,
        faith: uniq(faith).length ? uniq(faith) : undefined,
        holy_order_of: holyOrderMaa[it.k],
        dlc_features: chainDlc.length ? chainDlc : undefined,
        dlc_required: dlcRequired.length ? dlcRequired : undefined,
        dlc_on_some_paths_only: dlcSomePaths.length ? dlcSomePaths : undefined,
        file_codename: codename, file_codename_dlc_feature_inferred: codename ? CODENAME_FEATURE[codename] : undefined,
        conditions: conds.length ? conds : undefined,
      },
      can_recruit: can ? compact(can) : undefined,
      source_file: rel(f), line: it.line,
    };
  }
}
// strip undefined
const clean = (o) => JSON.parse(JSON.stringify(o));

// ---------------------------------------------------------------- base unit types
const baseFromModifiers = [];
{
  const f = C + '/modifier_definition_formats/00_unit_definitions.txt';
  const r = parseFile(f);
  for (const it of r.items) { const m = it.k && it.k.match(/^([a-z_]+)_max_size_add$/); if (m && !m[1].startsWith('stationed_')) baseFromModifiers.push({ key: m[1], line: it.line }); }
}
const conceptFile = C + '/game_concepts/00_game_concepts.txt';
const concepts = {};
for (const it of topItems(parseFile(conceptFile))) concepts[it.k] = { texture: get(it.v, 'texture'), line: it.line, parent: get(it.v, 'parent') };
const usedBase = uniq(Object.values(maa).map((m) => m.base_type));
const counterTargets = uniq(Object.values(maa).flatMap((m) => Object.keys(m.counters || {})));
const allBase = uniq([...baseFromModifiers.map((x) => x.key), ...usedBase, ...counterTargets]);
const base_unit_types = {};
// INFERRED design hints for the Bannerlord side (TaleWorlds.Core.FormationClass names); not game data.
const BL_HINT = {
  heavy_infantry: { formation: 'Infantry', mounted: false, ranged: false, armor: 'heavy', weapons: 'one-hander/two-hander + shield' },
  pikemen: { formation: 'Infantry', mounted: false, ranged: false, armor: 'medium', weapons: 'long spear/pike (brace vs cavalry)' },
  peasant_militia: { formation: 'Infantry', mounted: false, ranged: false, armor: 'none/light', weapons: 'spear/pitchfork militia' },
  archers: { formation: 'Ranged', mounted: false, ranged: true, armor: 'light', weapons: 'bow or crossbow' },
  gunpowder: { formation: 'Ranged', mounted: false, ranged: true, armor: 'light', weapons: 'handgun -> substitute crossbow (no firearms in vanilla Bannerlord)' },
  skirmishers: { formation: 'Skirmisher', mounted: false, ranged: true, armor: 'light', weapons: 'javelins/throwing + shield' },
  light_cavalry: { formation: 'Cavalry', mounted: true, ranged: false, armor: 'light', weapons: 'spear/sword, light horse' },
  heavy_cavalry: { formation: 'Cavalry', mounted: true, ranged: false, armor: 'heavy', weapons: 'couchable lance + barded horse' },
  archer_cavalry: { formation: 'HorseArcher', mounted: true, ranged: true, armor: 'light/medium', weapons: 'bow, steppe horse' },
  nomadic_horde: { formation: 'HorseArcher', mounted: true, ranged: true, armor: 'light', weapons: 'bow, steppe horse (herd riders)' },
  camel_cavalry: { formation: 'Cavalry', mounted: true, ranged: false, armor: 'light', weapons: 'camel mount (Bannerlord has camels)' },
  elephant_cavalry: { formation: 'Cavalry', mounted: true, ranged: false, armor: 'heavy', weapons: 'no elephants in Bannerlord -> substitute heavy cavalry, scale count by damage' },
  siege_weapon: { formation: null, mounted: false, ranged: false, armor: null, weapons: 'not a field-battle unit (fights_in_main_phase = no for most) -> skip or spawn as crew' },
};
for (const bt of allBase) {
  const members = Object.entries(maa).filter(([, m]) => m.base_type === bt);
  const avg = (k) => members.length ? Math.round(members.reduce((s, [, m]) => s + (typeof m[k] === 'number' ? m[k] : 0), 0) / members.length * 10) / 10 : undefined;
  const countered_by = Object.entries(maa).filter(([, m]) => m.counters && m.counters[bt] !== undefined).map(([k]) => k);
  const counters_of_members = uniq(members.flatMap(([, m]) => Object.keys(m.counters || {})));
  const mod = baseFromModifiers.find((x) => x.key === bt);
  base_unit_types[bt] = {
    maa_types: members.map(([k]) => k),
    count: members.length,
    avg_damage: avg('damage'), avg_toughness: avg('toughness'), avg_pursuit: avg('pursuit'), avg_screen: avg('screen'),
    counters_targets_of_members: counters_of_members,
    countered_by_maa: countered_by,
    has_modifier_definitions: !!mod, modifier_definitions_line: mod ? `common/modifier_definition_formats/00_unit_definitions.txt:${mod.line}` : undefined,
    game_concept: concepts[bt] ? `common/game_concepts/00_game_concepts.txt:${concepts[bt].line}` : (bt === 'pikemen' && concepts.spearmen ? `spearmen concept common/game_concepts/00_game_concepts.txt:${concepts.spearmen.line}` : undefined),
    is_cavalry: /cavalry|horde/.test(bt) || undefined,
    fights_in_main_phase_no: members.filter(([, m]) => m.fights_in_main_phase === 'no').map(([k]) => k),
    bannerlord_hint_INFERRED: BL_HINT[bt],
    note: members.length === 0 ? 'Base type referenced only (counters/modifiers); no MaA regiment type uses it as `type`.' : undefined,
  };
}

// ---------------------------------------------------------------- defines
const defs = parseFile(C + '/defines/00_defines.txt');
const ncombat = toObj(get(defs, 'NCombat'));
const narmy = toObj(get(defs, 'NArmy'));
const lineOf = (blockKey, k) => { const b = get(defs, blockKey); const it = getItem(b, k); return it ? `common/defines/00_defines.txt:${it.line}` : undefined; };
const levy = {
  damage: ncombat.LEVY_ATTACK, toughness: ncombat.LEVY_TOUGHNESS, pursuit: ncombat.LEVY_PURSUIT, screen: ncombat.LEVY_SCREEN, siege: ncombat.LEVY_SIEGE,
  base_type_equivalent: 'none (levies are not a MaA base type; countered as peasant-ish infantry)',
  source: { LEVY_ATTACK: lineOf('NCombat', 'LEVY_ATTACK'), LEVY_TOUGHNESS: lineOf('NCombat', 'LEVY_TOUGHNESS'), LEVY_PURSUIT: lineOf('NCombat', 'LEVY_PURSUIT'), LEVY_SCREEN: lineOf('NCombat', 'LEVY_SCREEN'), LEVY_SIEGE: lineOf('NCombat', 'LEVY_SIEGE') },
};
// knight limits from basic modifiers
const basicMods = parseFile(C + '/modifiers/00_basic_modifiers.txt');
const knightLimitByTier = {};
for (const it of topItems(basicMods)) { const kl = get(it.v, 'knight_limit'); if (kl !== undefined && /_modifier$|prestige_level/.test(it.k)) knightLimitByTier[it.k] = { knight_limit: num(kl), source: `common/modifiers/00_basic_modifiers.txt:${(getItem(it.v, 'knight_limit') || {}).line}` }; }
const knights = {
  damage_per_prowess: ncombat.KNIGHT_DAMAGE_PER_PROWESS, toughness_per_prowess: ncombat.KNIGHT_TOUGHNESS_PER_PROWESS,
  average_prowess_for_ai: ncombat.KNIGHT_AVERAGE_PROWESS_FOR_AI_POWER_CALCULATION,
  max_prowess: num(toObj(get(defs, 'NSkills'))?.MAX_PROWESS), max_prowess_source: lineOf('NSkills', 'MAX_PROWESS'),
  combat_event_days: ncombat.COMBAT_EVENT_DAYS,
  knight_limit_modifiers: knightLimitByTier,
  modifiers_affecting: ['knight_limit', 'knight_effectiveness_mult', 'knight_effectiveness_per_prowess', 'knight_effectiveness_per_martial', 'knight_effectiveness_per_diplomacy', 'knight_effectiveness_per_intrigue', 'knight_effectiveness_per_learning', 'knight_effectiveness_per_stewardship', 'knight_effectiveness_per_dread', 'knight_effectiveness_per_tyranny'],
  formula_note: 'Knight combat damage = prowess * KNIGHT_DAMAGE_PER_PROWESS * (1 + knight_effectiveness_mult); toughness = prowess * KNIGHT_TOUGHNESS_PER_PROWESS (engine-side; formula INFERRED from define comments).',
  mercenary_num_knights_by_band_level: toObj(get(get(defs, 'NMercenary') ?? { items: [] }, 'NUM_KNIGHTS')) ,
  holy_order: { base_num_knights: toObj(get(defs, 'NHolyOrder'))?.BASE_NUM_KNIGHTS, extra_knights_per_holding: toObj(get(defs, 'NHolyOrder'))?.EXTRA_KNIGHTS_PER_HOLDING },
  source: { KNIGHT_DAMAGE_PER_PROWESS: lineOf('NCombat', 'KNIGHT_DAMAGE_PER_PROWESS'), KNIGHT_TOUGHNESS_PER_PROWESS: lineOf('NCombat', 'KNIGHT_TOUGHNESS_PER_PROWESS') },
};
const combatKeys = ['COMBAT_ROLL_DAYS', 'MANEUVER_PHASE_DAYS', 'PURSUIT_PHASE_DAYS', 'DAMAGE_SCALING_FACTOR', 'BASE_RATIO_CASUALTIES_CONVERSION', 'ADVANTAGE_DAMAGE_SCALING_FACTOR', 'BASE_WIDTH_RATIO', 'COMMANDER_MIN_ROLL', 'COMMANDER_MAX_ROLL', 'MEN_AT_ARMS_MAX_COUNTER', 'RATIO_FOR_MAX_COUNTER', 'MINIMUM_COMBAT_WIDTH', 'BASE_RATIO_CASUALTIES_CONVERSION_PURSUIT', 'PURSUIT_STAT_TO_PURSUIT_DAMAGE', 'BASE_TOUGHNESS_TO_PURSUIT', 'MINIMUM_PURSUIT_DAMAGE', 'WAR_ATTACKER_COMBAT_MAX_SCORE', 'WAR_DEFENDER_COMBAT_MAX_SCORE'];
const combat_defines = {};
for (const k of combatKeys) combat_defines[k] = { value: ncombat[k], source: lineOf('NCombat', k) };
for (const k of ['REGIMENT_DEFAULT_STACK_SIZE', 'REGIMENT_DEFAULT_MAX_SIZE', 'REGIMENT_MAA_CAP_BY_TIER', 'REGIMENT_MAA_EXTRA_BY_CAP', 'REGIMENT_MAX_SIZE', 'NOMADIC_RIDERS_MAX_SIZE']) combat_defines[k] = { value: narmy[k], source: lineOf('NArmy', k) };

const nmerc = toObj(get(defs, 'NMercenary')); const nho = toObj(get(defs, 'NHolyOrder'));
const pick = (o, ks) => Object.fromEntries(ks.filter((k) => o[k] !== undefined).map((k) => [k, o[k]]));
const hired_troops = {
  mercenary: { ...pick(nmerc, ['LEVELS', 'LEVIES', 'MAA_REGIMENTS', 'NUM_KNIGHTS', 'MAA_REGIMENT_SIZES', 'SIZE_INNOVATIONS_RATIO']), source: `common/defines/00_defines.txt:${getItem(defs, 'NMercenary').line}` },
  holy_order: { ...pick(nho, ['NUM_MAA_BASE', 'NUM_MAA_PER_HOLDING', 'NUM_HOLDINGS_PER_MAA_REGIMENT', 'RELIGION_MAA_RATIO', 'BASE_NUM_KNIGHTS', 'EXTRA_KNIGHTS_PER_HOLDING', 'LEVIES', 'BASE_LEVIES', 'LEVIES_PER_HOLDING']), source: `common/defines/00_defines.txt:${getItem(defs, 'NHolyOrder').line}` },
  note: 'No script definitions for mercenary companies exist in common/ (spawned by code per culture, sized by NMercenary). Hired MaA picks are steered by allowed_in_hired_troops / hired_stack_size / mercenary_fallback / fallback_in_hired_troops_if_unlocked / holy_order_fallback on MaA types, and religion holy_order_maa lists. In a battle, hired regiments are visible via every_army_maa_regiment (include_hired = yes default) and is_hired_maa_regiment.',
};

// ---------------------------------------------------------------- terrains
const terrains = [];
for (const f of listTxt(C + '/terrain_types')) {
  const r = parseFile(f); const vm = vars(r);
  for (const it of topItems(r)) {
    const dce = get(it.v, 'defender_combat_effects'); const ace = get(it.v, 'attacker_combat_effects');
    terrains.push({
      key: it.k,
      combat_width: num(get(it.v, 'combat_width'), vm) ?? 1,
      movement_speed: num(get(it.v, 'movement_speed'), vm) ?? 1,
      defender_advantage: dce ? num(get(dce, 'advantage')) : 0,
      attacker_advantage: ace ? num(get(ace, 'advantage')) : 0,
      defender_combat_effect: dce ? get(dce, 'name') : undefined,
      attacker_modifier: get(it.v, 'attacker_modifier') ? toObj(get(it.v, 'attacker_modifier')) : undefined,
      defender_modifier: get(it.v, 'defender_modifier') ? toObj(get(it.v, 'defender_modifier')) : undefined,
      is_water: /sea/.test(it.k) || undefined,
      maa_with_bonus: Object.entries(maa).filter(([, m]) => m.terrain_bonus && m.terrain_bonus[it.k]).map(([k]) => k),
      source_file: rel(f), line: it.line,
    });
  }
}

// ---------------------------------------------------------------- cultures
const cultures = {};
const gfxSets = (b, k) => getAll(b, k).filter(isBlock).map((x) => values(x));
for (const f of listTxt(C + '/culture/cultures')) {
  const r = parseFile(f);
  for (const it of topItems(r)) {
    const b = it.v;
    const unitSets = gfxSets(b, 'unit_gfx'), clothSets = gfxSets(b, 'clothing_gfx'), coaSets = gfxSets(b, 'coa_gfx'), bldSets = gfxSets(b, 'building_gfx');
    const dlcTrads = getAll(b, 'dlc_tradition').filter(isBlock).map((d) => ({ trait: get(d, 'trait'), requires_dlc_flag: get(d, 'requires_dlc_flag'), fallback: get(d, 'fallback') }));
    const dlcFallback = getAll(b, 'dlc_fallback_pillar').filter(isBlock).map((d) => ({ fallback: get(d, 'fallback'), requires_dlc_flag: get(d, 'requires_dlc_flag') }));
    cultures[it.k] = {
      heritage: get(b, 'heritage'), language: get(b, 'language'), ethos: get(b, 'ethos'), martial_custom: get(b, 'martial_custom'), head_determination: get(b, 'head_determination'),
      unit_gfx: uniq(unitSets.flat()), clothing_gfx: uniq(clothSets.flat()), coa_gfx: uniq(coaSets.flat()), building_gfx: uniq(bldSets.flat()),
      clothing_gfx_sets: clothSets.length > 1 ? clothSets : undefined, coa_gfx_sets: coaSets.length > 1 ? coaSets : undefined, building_gfx_sets: bldSets.length > 1 ? bldSets : undefined,
      traditions: get(b, 'traditions') ? values(get(b, 'traditions')) : [],
      dlc_traditions: dlcTrads.length ? dlcTrads : undefined,
      dlc_fallback_pillars: dlcFallback.length ? dlcFallback : undefined,
      name_list: getAll(b, 'name_list'),
      parents: get(b, 'parents') ? values(get(b, 'parents')) : undefined,
      created: get(b, 'created'),
      ethnicities: get(b, 'ethnicities') ? get(b, 'ethnicities').items.filter((x) => x.k !== undefined && !isBlock(x.v)).map((x) => ({ weight: num(x.k), ethnicity: x.v })) : undefined,
      source_file: rel(f), line: it.line,
    };
  }
}
// MaA reachable per culture: starting traditions' parameters, heritage-gated innovations
const maaByParam = {};
for (const [k, m] of Object.entries(maa)) for (const p of m.unlock_detail.required_parameters || []) (maaByParam[p] ??= []).push(k);
function culturalMaa(c, trads) {
  const params = uniq(trads.flatMap((t) => traditions[t]?.parameters || []).concat(...[c.heritage, c.ethos, c.language, c.martial_custom].map((p) => pillars[p]?.parameters || [])));
  const fromTrad = uniq(params.flatMap((p) => maaByParam[p] || []));
  const fromInno = [];
  for (const [ik, inn] of Object.entries(innovations)) {
    if (!inn.unlock_maa.length) continue;
    const pillarOk = inn.potential_pillars.length && inn.potential_pillars.some((p) => [c.heritage, c.language, c.ethos].includes(p));
    const paramOk = inn.potential_parameters.length && inn.potential_parameters.some((p) => params.includes(p));
    if (pillarOk || paramOk) fromInno.push(...inn.unlock_maa.map((m) => `${m} (via ${ik})`));
  }
  return { fromTrad, fromInno };
}
for (const [ck, c] of Object.entries(cultures)) {
  // with all DLC (the user's setup): starting traditions + dlc_tradition traits
  const withDlc = culturalMaa(c, uniq([...c.traditions, ...(c.dlc_traditions || []).map((d) => d.trait)]));
  // without the gating DLC: starting traditions + dlc_tradition fallbacks
  const noDlc = culturalMaa(c, uniq([...c.traditions, ...(c.dlc_traditions || []).map((d) => d.fallback)]));
  c.cultural_maa_from_starting_traditions = withDlc.fromTrad.length ? withDlc.fromTrad : undefined;
  c.cultural_maa_from_innovations = withDlc.fromInno.length ? withDlc.fromInno : undefined;
  const nd = uniq([...noDlc.fromTrad, ...noDlc.fromInno]);
  const wd = uniq([...withDlc.fromTrad, ...withDlc.fromInno]);
  if (nd.join() !== wd.join()) c.cultural_maa_without_dlc = nd;
}

// heritage / pillar lists
const heritages = Object.entries(pillars).filter(([, p]) => p.type === 'heritage').map(([k]) => k).sort();
const heritage_detail = {};
for (const h of heritages) {
  const cs = Object.entries(cultures).filter(([, c]) => c.heritage === h);
  const ug = {}; for (const [, c] of cs) for (const u of c.unit_gfx.slice(0, 1)) ug[u] = (ug[u] || 0) + 1;
  heritage_detail[h] = { cultures: cs.map(([k]) => k), primary_unit_gfx_counts: ug, source: `${pillars[h].source_file}:${pillars[h].line}` };
}
const pillarList = (t) => Object.entries(pillars).filter(([, p]) => p.type === t).map(([k]) => k).sort();

// ---------------------------------------------------------------- unit gfx
const gut = {}; // graphical unit types (illustrations for knights/levies)
{
  const f = C + '/graphical_unit_types/00_graphical_unit_types.txt';
  for (const it of topItems(parseFile(f))) for (const g of values(get(it.v, 'graphical_cultures'))) (gut[g] ??= []).push(`${it.k} (common/graphical_unit_types/00_graphical_unit_types.txt:${it.line})`);
}
const armyEntities = {};
for (const f of listTxt(GAME + '/gfx/models/units/entity_links')) {
  const r = parseFile(f); const vm = vars(r);
  for (const it of topItems(r)) {
    const gc = get(it.v, 'graphical_cultures');
    for (const g of gc ? values(gc) : ['(fallback)']) (armyEntities[g] ??= []).push({ link: it.k, type: get(it.v, 'type'), quality: num(get(it.v, 'quality'), vm), entity: get(it.v, 'entity'), file: rel(f), line: it.line });
  }
}
const asianTrigger = scriptedTriggers.should_use_asian_maa_graphics;
const asianGfx = []; walkTrigger(asianTrigger?.block, (k, v) => { if (k === 'has_unit_gfx') asianGfx.push(v); });
const unitGfxAll = uniq([...Object.values(cultures).flatMap((c) => c.unit_gfx), ...Object.keys(gut), ...Object.keys(armyEntities).filter((x) => x !== '(fallback)')]).sort();
const unit_gfx_detail = {};
for (const u of unitGfxAll) {
  const prim = Object.entries(cultures).filter(([, c]) => c.unit_gfx[0] === u).map(([k]) => k);
  const any = Object.entries(cultures).filter(([, c]) => c.unit_gfx.includes(u)).map(([k]) => k);
  const herit = {}; for (const k of any) herit[cultures[k].heritage] = (herit[cultures[k].heritage] || 0) + 1;
  unit_gfx_detail[u] = {
    cultures_primary: prim.length, cultures_any: any.length,
    example_cultures: prim.slice(0, 8),
    heritages: herit,
    knight_levy_illustration_group: gut[u],
    uses_asian_maa_illustrations: asianGfx.includes(u) || undefined,
    map_army_entities: (armyEntities[u] || []).filter((e) => e.type === 'army').map((e) => `${e.entity}${e.quality ? ` (quality ${e.quality})` : ''}`),
    map_other_entities: (armyEntities[u] || []).filter((e) => e.type !== 'army').map((e) => `${e.type}:${e.entity}`),
    defined_in_cultures: any.length > 0,
  };
}

// ---------------------------------------------------------------- output
const counts = {
  maa_types: Object.keys(maa).length,
  maa_by_category: Object.values(maa).reduce((a, m) => ((a[m.category] = (a[m.category] || 0) + 1), a), {}),
  maa_by_base_type: Object.fromEntries(Object.entries(base_unit_types).map(([k, v]) => [k, v.count])),
  cultures: Object.keys(cultures).length, heritages: heritages.length, languages: pillarList('language').length, ethos: pillarList('ethos').length, martial_customs: pillarList('martial_custom').length,
  unit_gfx: unitGfxAll.length, terrains: terrains.length, traditions: Object.keys(traditions).length, innovations: Object.keys(innovations).length,
};
const catalog = {
  meta: {
    game: 'Crusader Kings III', version_hint: '1.20.0.3 (Crozier)', game_dir: '<CK3 install>/game', generated: new Date().toISOString(),
    generator: `${SELF} (Node.js Paradox-script parser pdx.mjs)`,
    sources: { maa: 'game/common/men_at_arms_types/*.txt', cultures: 'game/common/culture/cultures/*.txt', pillars: 'game/common/culture/pillars/*.txt', traditions: 'game/common/culture/traditions/*.txt', innovations: 'game/common/culture/innovations/*.txt', terrains: 'game/common/terrain_types/00_terrains.txt', defines: 'game/common/defines/00_defines.txt', base_types: 'game/common/modifier_definition_formats/00_unit_definitions.txt + MaA `type` values', unit_gfx_entities: 'game/gfx/models/units/entity_links/*.txt', graphical_unit_types: 'game/common/graphical_unit_types/00_graphical_unit_types.txt', holy_orders: 'game/common/religion/religion_types/*.txt holy_order_maa' },
    dlc_note: 'game/dlc/* folders contain only .dlc descriptors + gfx/music/sound; ALL DLC script content lives in game/common and is gated by has_dlc_feature / has_*_dlc_trigger / requires_dlc_flag.',
    dlc_folders: dlcs, dlc_trigger_map: dlcTriggers, codename_to_dlc_feature: CODENAME_FEATURE,
    counts,
  },
  maa_types: clean(maa),
  base_unit_types: clean(base_unit_types),
  levy: clean(levy),
  knights: clean(knights),
  combat_defines: clean(combat_defines),
  hired_troops: clean(hired_troops),
  cultures: clean(cultures),
  heritages,
  heritage_detail,
  languages: pillarList('language'), ethos: pillarList('ethos'), martial_customs: pillarList('martial_custom'),
  unit_gfx: unitGfxAll,
  unit_gfx_detail: clean(unit_gfx_detail),
  terrains: clean(terrains),
  holy_order_maa_by_religion: religionHolyOrderMaa,
  codegen_keys: {
    note: 'Ordered key lists for generating CK3 if/else_if emission chains (CK3 script cannot print a MaA/culture key directly as data; test each key). Trigger syntax VERIFIED in logs/triggers.log (script_docs, CK3 1.20).',
    triggers: {
      'regiment: is_maa_type = <maa key>': 'triggers.log:9351', 'regiment: is_unit_type = <base type>': 'triggers.log:9382',
      'regiment: maa_current_troops_count / maa_max_troops_count / maa_size (value triggers)': 'triggers.log:9388-9402',
      'culture: has_unit_gfx = <x_unit_gfx>': 'triggers.log:12465', 'culture: has_cultural_pillar = <heritage_/language_/ethos_/martial_custom_ key>': 'triggers.log:12392',
      'army: every_army_maa_regiment = { include_hired = yes ... }': 'effects.log:15141',
    },
    maa_keys: Object.keys(maa), base_types: allBase, unit_gfx: unitGfxAll, heritages, terrains: terrains.map((t) => t.key),
  },
  maa_traditions: clean(Object.fromEntries(Object.entries(traditions).filter(([, t]) => t.parameters.some((p) => maaByParam[p])).map(([k, t]) => [k, { ...t, unlocks_maa: uniq(t.parameters.flatMap((p) => maaByParam[p] || [])) }]))),
  maa_innovations: clean(Object.fromEntries(Object.entries(innovations).filter(([, i]) => i.unlock_maa.length || i.custom.some((c) => /unlock_maa/.test(c))))),
};
fs.mkdirSync(path.dirname(OUT), { recursive: true });
fs.writeFileSync(OUT, JSON.stringify(catalog, null, 1));
console.log('wrote', OUT, JSON.stringify(counts));
