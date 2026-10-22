#!/usr/bin/env node
// build_mapping.js — generates bridge/data/mapping.json (CK3 1.20 -> Bannerlord 1.4.8 unit mapping).
// Dependency-free Node (>=18). Inputs: bridge/data/ck3_catalog.json, bridge/data/bl_catalog.json, and the Bannerlord
// CustomBattle scene XML (read-only). Re-run after either game patches:  node build_mapping.js [--out <file>]
// Hand-curated design data lives in this file (profiles, ladders, overrides, scene lists); everything derivable
// (culture_map, tier targets, terrain tier shifts, maa_base, levy lists) is computed from the catalogs.
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
const outAt = process.argv.indexOf('--out');   // --out <file>: write elsewhere (e.g. to compare with the committed file)
const OUT = outAt > 0 && process.argv[outAt + 1] ? process.argv[outAt + 1] : ROOT + '/bridge/data/mapping.json';
const CK3 = JSON.parse(fs.readFileSync(ROOT + '/bridge/data/ck3_catalog.json', 'utf8'));
const BL = JSON.parse(fs.readFileSync(ROOT + '/bridge/data/bl_catalog.json', 'utf8'));
const BL_MODULES = path.join(gameDirs.bannerlordDir(), 'Modules').replace(/\\/g, '/');

const BL_CULTURES = ['vlandia', 'empire', 'sturgia', 'battania', 'aserai', 'khuzait'];
const ERAS = ['culture_era_tribal', 'culture_era_early_medieval', 'culture_era_high_medieval', 'culture_era_late_medieval'];

// ---------------------------------------------------------------------------------------------------------------
// helpers
const L = (...pairs) => pairs.map(([troop, weight]) => ({ troop, weight }));          // [[id,w],...] -> [{troop,weight}]
const R = (tier, ...pairs) => ({ tier, troops: L(...(pairs.length ? pairs : [])) });   // ladder rung
const one = (id) => [[id, 1]];
const r2 = (x) => Math.round(x * 100) / 100;
// Berber javelin light horse (Beni Zilal minor faction): the berber profile's light cavalry and the Almoravid mulaththamun.
const BERBER_LIGHT_HORSE = [R(2, ['beni_zilal_tier_1', 1]), R(3, ['beni_zilal_tier_2', 0.6], ['aserai_tribal_horseman', 0.4]), R(4, ['beni_zilal_tier_3', 0.5], ['aserai_faris', 0.5]), R(5, ['aserai_veteran_faris', 1])];

// ---------------------------------------------------------------------------------------------------------------
// 1. PROFILES (appearance / army-style key). Every CK3 culture resolves to exactly one profile; a profile names the
//    Bannerlord culture whose trees are used and optionally overrides single roles, the levy variant and knights.
//    role_overrides[role] = [{min_era?, max_era?, use_culture? | ladder?}]  (first matching entry wins)
const PROFILES = {
  default:          { bl_culture: 'empire',   levy_variant: 'empire',   label: 'Fallback (no rule matched)', why: 'Empire has all four formation classes incl. crossbows and horse archers, so it is the safest generic medieval army.' },
  western_feudal:   { bl_culture: 'vlandia',  levy_variant: 'vlandia',  label: 'Frankish / German / English / Italian / Gothic feudal', why: 'Vlandia is the Norman/Western feudal faction: couched-lance knights, crossbows, spear/billmen. CK3 draws all of these with western_unit_gfx.' },
  iberian_christian:{ bl_culture: 'vlandia',  levy_variant: 'vlandia',  label: 'Christian Iberia', why: 'Feudal knights like the rest of the West (vlandia), but Iberian light horse were jinetes with javelins and the almogavars were javelin infantry, so light cavalry and skirmishers come from the Aserai javelin lines.',
    role_overrides: { light_cavalry: [{ use_culture: 'aserai' }], skirmishers: [{ use_culture: 'aserai' }] } },
  andalusian:       { bl_culture: 'aserai',   levy_variant: 'aserai',   label: 'al-Andalus', why: 'iberian_muslim_unit_gfx; Andalusi armies were Arab-Berber in kit (Aserai), with Western-style crossbows.',
    role_overrides: { crossbowmen: [{ use_culture: 'vlandia' }] } },
  norse:            { bl_culture: 'sturgia',  levy_variant: 'sturgia',  label: 'Norse / Scandinavian', why: 'Sturgia is Bannerlord\'s Rus/Viking faction (Varyag, Line Breaker, Heroic Line Breaker = berserker/ulfhednar). Viking-age nobles fought on foot, so knights are foot huscarls until the High Medieval era. The Viking-age extras (Skolderbrotva raiders, berserkers) end with the Early Medieval era (restricted_troops).',
    role_overrides: {
      skirmishers: [{ max_era: 'culture_era_early_medieval', ladder: [R(2, ['skolderbrotva_tier_1', 1]), R(3, ['skolderbrotva_tier_2', 1]), R(4, ['skolderbrotva_tier_3', 0.6], ['lakepike_tier_3', 0.4]), R(5, ['sturgian_veteran_warrior', 1])] }],
      heavy_infantry_2h: [{ max_era: 'culture_era_early_medieval', ladder: [R(4, ['sturgian_berzerker', 1]), R(5, ['sturgian_ulfhednar', 1])] }],
    },
    knights: [{ max_era: 'culture_era_early_medieval', troops: L(['huskarl_swordsman', 0.4], ['sturgian_ulfhednar', 0.3], ['sturgian_veteran_warrior', 0.3]) }] },
  east_slavic:      { bl_culture: 'sturgia',  levy_variant: 'sturgia',  label: 'Rus / East Slavic', why: 'Sturgia is modelled on Kievan Rus (druzhina, lamellar, round shields).' },
  west_slavic:      { bl_culture: 'sturgia',  levy_variant: 'sturgia',  label: 'Polish / Czech / Wendish', why: 'Early West Slavs fought like the Rus (sturgia). From the High Medieval era Polish and Bohemian nobles were Western-style knights, so heavy cavalry and knights switch to Vlandia.',
    role_overrides: { heavy_cavalry: [{ min_era: 'culture_era_high_medieval', use_culture: 'vlandia' }] },
    knights: [{ min_era: 'culture_era_high_medieval', troops: L(['vlandian_banner_knight', 0.4], ['vlandian_champion', 0.3], ['druzhinnik_champion', 0.3]) }] },
  balkan:           { bl_culture: 'empire',   levy_variant: 'sturgia',  label: 'South Slavic / Vlach / Albanian', why: 'Bulgarian and Serbian states copied the Byzantine military (empire MaA, cataphract-style nobles) while the peasant levy was Slavic (sturgia levy). CK3 draws them with eastern_unit_gfx (Byzantine models).' },
  byzantine:        { bl_culture: 'empire',   levy_variant: 'empire',   label: 'Greek / Roman', why: 'The Calradic Empire is Bannerlord\'s Byzantium: legionaries, menavliatons, cataphracts, bucellarii.' },
  caucasian:        { bl_culture: 'empire',   levy_variant: 'empire',   label: 'Armenian / Georgian', why: 'Armenian azatani and Georgian monaspa were Byzantine-style heavy cavalry (empire); their mounted archers looked Near-Eastern, so archer cavalry comes from the Aserai mamluk line.',
    role_overrides: { archer_cavalry: [{ use_culture: 'aserai' }] } },
  arab:             { bl_culture: 'aserai',   levy_variant: 'aserai',   label: 'Arab / Levantine / Egyptian / Syriac / Jewish (mena)', why: 'Aserai are Bannerlord\'s Arab faction (faris, mamluks, desert kit).' },
  berber:           { bl_culture: 'aserai',   levy_variant: 'berber',   label: 'Berber / Guanche / Zaghawa', why: 'Aserai kit; Berber armies leaned on javelin light horse and javelin infantry (Jawwal, Beni Zilal minor factions).',
    role_overrides: { light_cavalry: [{ ladder: BERBER_LIGHT_HORSE }] } },
  east_african:     { bl_culture: 'aserai',   levy_variant: 'tropical_foot', label: 'Ethiopian / Nubian / Somali / Swahili', why: 'mena_unit_gfx in CK3; Aserai bodies and light kit are the closest Bannerlord look. Levies are foot (spears, javelins, bows).' },
  sahel:            { bl_culture: 'aserai',   levy_variant: 'sahel',    label: 'Sahel / Mande / Senegambia / Kanem-Hausa', why: 'Sahelian empires (Ghana, Mali, Kanem, Jolof, Mossi) fielded Muslim-style mailed cavalry and archers; Aserai (faris, mamluks) is the closest look and has darker body templates than any European faction.' },
  african_forest:   { bl_culture: 'aserai',   levy_variant: 'tropical_foot', label: 'Akan / Yoruba / Edo / Igbo', why: 'Forest-belt states fought almost entirely on foot (archers, javelins, swords). Aserai bodies, foot-only levy, foot elite knights.',
    knights: [{ troops: L(['aserai_veteran_infantry', 0.4], ['mamluke_palace_guard', 0.4], ['aserai_master_archer', 0.2]) }] },
  persian:          { bl_culture: 'aserai',   levy_variant: 'aserai',   label: 'Iranian / Tocharian / Brahui', why: 'No Iranian faction exists. Persianate armies (Samanid, Buyid, Ghaznavid, Seljuk ghulams) were Near-Eastern heavy horse archers, faris-like aswaran and Daylamite infantry; the Aserai mamluk line matches that look better than the Roman-looking Empire.' },
  steppe:           { bl_culture: 'khuzait',  levy_variant: 'khuzait',  label: 'Turkic / Mongolic / Hunnic / Tungusic', why: 'Khuzait are Bannerlord\'s Turko-Mongol steppe faction (horse archers, lancers, kheshig). Nomadic governments switch to the mounted levy variant. Their horse archers also field the Karakhergit nomads, a steppe minor faction kept out of the other Khuzait-based profiles.',
    role_overrides: { archer_cavalry: [{ ladder: [R(2, ['khuzait_tribal_warrior', 1]), R(3, ['khuzait_raider', 0.6], ['khuzait_qanqli', 0.4]), R(4, ['khuzait_horse_archer', 0.7], ['karakhuzaits_tier_3', 0.3]), R(5, ['khuzait_heavy_horse_archer', 0.6], ['khuzait_kheshig', 0.4]), R(6, ['khuzait_khans_guard', 1])] }] } },
  hungarian:        { bl_culture: 'khuzait',  levy_variant: 'steppe_settled', label: 'Hungarian (settled Magyar)', why: 'Magyar horse archery survives as hussars/horse archers (khuzait), but the Christian kingdom had foot levies and, from the High Medieval era, Western-style knights.',
    role_overrides: { heavy_cavalry: [{ min_era: 'culture_era_high_medieval', use_culture: 'vlandia' }] },
    knights: [{ min_era: 'culture_era_high_medieval', troops: L(['vlandian_champion', 0.4], ['khuzait_heavy_lancer', 0.4], ['khuzait_khans_guard', 0.2]) }, { troops: L(['khuzait_heavy_lancer', 0.5], ['khuzait_khans_guard', 0.5]) }] },
  tibetan:          { bl_culture: 'khuzait',  levy_variant: 'steppe_settled', label: 'Tibetan / Qiangic (Tangut)', why: 'Tibetan Empire and Tangut armies were lamellar-armoured lancers and archers from a pastoral plateau: Khuzait kit, but levies mostly on foot.',
    knights: [{ troops: L(['khuzait_heavy_lancer', 0.6], ['khuzait_khans_guard', 0.4]) }] },
  east_asian:       { bl_culture: 'khuzait',  levy_variant: 'east_asian', label: 'Chinese / Korean / Viet / Hmong (chinese_unit_gfx)', why: 'No Chinese faction exists. Khuzait lamellar, spears and horse archers are the closest East-Asian look; Song/Tang/Goryeo crossbow corps use the Empire crossbow line (also lamellar). Levies are foot.',
    role_overrides: { crossbowmen: [{ use_culture: 'empire' }], pikemen: [{ ladder: [R(3, ['khuzait_spearman', 1]), R(4, ['khuzait_spear_infantry', 0.6], ['imperial_menavliaton', 0.4]), R(5, ['khuzait_darkhan', 0.6], ['imperial_elite_menavliaton', 0.4])] }] },
    knights: [{ troops: L(['khuzait_heavy_lancer', 0.5], ['khuzait_khans_guard', 0.5]) }] },
  japanese:         { bl_culture: 'khuzait',  levy_variant: 'east_asian', label: 'Japanese / Ryukyuan', why: 'Early samurai were lamellar-armoured mounted archers (khuzait noble line: kheshig, khan\'s guard). Naginata/yari foot use the Empire menavliaton (polearm) line.',
    role_overrides: { heavy_infantry: [{ ladder: [R(3, ['khuzait_spearman', 1]), R(4, ['khuzait_spear_infantry', 0.5], ['imperial_menavliaton', 0.5]), R(5, ['khuzait_darkhan', 0.5], ['imperial_elite_menavliaton', 0.5])] }] },
    knights: [{ troops: L(['khuzait_khans_guard', 0.6], ['khuzait_kheshig', 0.4]) }] },
  emishi:           { bl_culture: 'khuzait',  levy_variant: 'khuzait',  label: 'Emishi / Ainu', why: 'Emishi were famed mounted archers (CK3 emishi_horse_archers); Khuzait mixed levy with horse archers.' },
  siberian:         { bl_culture: 'khuzait',  levy_variant: 'siberian', label: 'Nivkh', why: 'Amur/Sakhalin hunter-fishers: foot archers and spearmen with East-Asian bodies (khuzait foot + Vakken forest archers).' },
  indian:           { bl_culture: 'aserai',   levy_variant: 'indian',   label: 'Indo-Aryan / Dravidian (indian_unit_gfx)', why: 'No Indian faction exists. Aserai turbans, light mail, bow-armed foot and faris-style cavalry are the closest look and body templates. Elephants are handled per MaA (heavy-cavalry substitute).' },
  southeast_asian:  { bl_culture: 'aserai',   levy_variant: 'tropical_foot', label: 'Burman / Mon-Khmer / Tai / Austronesian', why: 'Infantry-heavy tropical armies; Aserai light kit and darker bodies are the least-bad fit. Nobles fought on elephants or on foot, so knights mix faris and foot guards.',
    knights: [{ troops: L(['aserai_veteran_faris', 0.4], ['mamluke_palace_guard', 0.3], ['aserai_veteran_infantry', 0.3]) }] },
  baltic_finnic:    { bl_culture: 'sturgia',  levy_variant: 'baltic_forest', label: 'Baltic / Finnic / Volga-Finnic / Ugro-Permian (northern_unit_gfx)', why: 'Northern forest peoples next to the Rus: Sturgian kit, plus the Vakken "forest people" minor faction (Bannerlord\'s Finno-Ugric-inspired archers) for bows.',
    role_overrides: { archers: [{ ladder: [R(2, ['forest_people_tier_1', 1]), R(3, ['forest_people_tier_2', 0.6], ['sturgian_hunter', 0.4]), R(4, ['forest_people_tier_3', 0.6], ['sturgian_archer', 0.4]), R(5, ['sturgian_veteran_bowman', 0.6], ['forest_people_tier_3', 0.4])] }] } },
  celtic:           { bl_culture: 'battania', levy_variant: 'battania', label: 'Brythonic / Goidelic', why: 'Battania is Bannerlord\'s Celtic faction (falxmen, oathsworn, fian longbowmen). Galloglass mercenaries join the heavy infantry from the High Medieval era (they appear in Ireland in the 13th century).',
    role_overrides: { heavy_infantry: [{ min_era: 'culture_era_high_medieval', ladder:[R(3, ['battanian_trained_warrior', 0.6], ['galloglass_tier_2', 0.4]), R(4, ['battanian_picked_warrior', 0.5], ['galloglass_tier_3', 0.5]), R(5, ['battanian_oathsworn', 1])] }] } },
};

// heritage -> profile (59). Heritage is a culture pillar, so hybrid/divergent runtime cultures inherit it.
const HERITAGE_PROFILE = {
  heritage_ainuic: 'emishi', heritage_akan: 'african_forest', heritage_albanian: 'balkan', heritage_ancient_greek: 'byzantine',
  heritage_arabic: 'arab', heritage_austronesian: 'southeast_asian', heritage_baltic: 'baltic_finnic', heritage_balto_finnic: 'baltic_finnic',
  heritage_bantu: 'east_african', heritage_berber: 'berber', heritage_brythonic: 'celtic', heritage_burman: 'southeast_asian',
  heritage_buyeo: 'east_asian', heritage_byzantine: 'byzantine', heritage_caucasian: 'caucasian', heritage_central_african: 'sahel',
  heritage_central_germanic: 'western_feudal', heritage_chinese: 'east_asian', heritage_dravidian: 'indian', heritage_east_african: 'east_african',
  heritage_east_slavic: 'east_slavic', heritage_egyptian: 'arab', heritage_frankish: 'western_feudal', heritage_goidelic: 'celtic',
  heritage_gothic: 'western_feudal', heritage_hmongic: 'east_asian', heritage_hunnic: 'steppe', heritage_iberian: 'iberian_christian',
  heritage_indo_aryan: 'indian', heritage_iranian: 'persian', heritage_israelite: 'arab', heritage_japonic: 'japanese',
  heritage_korean: 'east_asian', heritage_latin: 'western_feudal', heritage_magyar: 'hungarian', heritage_mon_khmer: 'southeast_asian',
  heritage_mongolic: 'steppe', heritage_nivkh: 'siberian', heritage_north_germanic: 'norse', heritage_phoenician: 'arab',
  heritage_qiangic: 'tibetan', heritage_sahelian: 'sahel', heritage_senegambian: 'sahel', heritage_somalian: 'east_african',
  heritage_south_slavic: 'balkan', heritage_syriac: 'arab', heritage_tai: 'southeast_asian', heritage_tibetan: 'tibetan',
  heritage_tocharian: 'persian', heritage_tungusic: 'steppe', heritage_turkic: 'steppe', heritage_ugro_permian: 'baltic_finnic',
  heritage_viet: 'east_asian', heritage_vlach: 'balkan', heritage_volga_finnic: 'baltic_finnic', heritage_west_african: 'sahel',
  heritage_west_germanic: 'western_feudal', heritage_west_slavic: 'west_slavic', heritage_yoruba: 'african_forest',
};
// unit_gfx -> profile (15). Only used when the heritage is unknown (e.g. a modded heritage). Majority profile of the gfx group.
const GFX_PROFILE = {
  chinese_unit_gfx: 'east_asian', eastern_unit_gfx: 'east_slavic', emishi_unit_gfx: 'emishi', iberian_christian_unit_gfx: 'iberian_christian',
  iberian_muslim_unit_gfx: 'andalusian', indian_unit_gfx: 'indian', iranian_unit_gfx: 'persian', japanese_unit_gfx: 'japanese',
  mena_unit_gfx: 'arab', mongol_unit_gfx: 'steppe', norse_unit_gfx: 'norse', northern_unit_gfx: 'baltic_finnic',
  southeast_asian_unit_gfx: 'southeast_asian', sub_sahran_unit_gfx: 'sahel', western_unit_gfx: 'western_feudal',
};
// (heritage, primary unit_gfx) pairs whose look differs from the heritage default. Key = "<heritage>|<unit_gfx>".
const PAIR_PROFILE = {
  'heritage_israelite|western_unit_gfx': ['western_feudal', 'Ashkenazi live in the Holy Roman Empire and CK3 draws them with western gfx.'],
  'heritage_israelite|indian_unit_gfx': ['indian', 'Kochinim (Cochin Jews) use indian gfx.'],
  'heritage_indo_aryan|iranian_unit_gfx': ['persian', 'Brahui (Baluchistan) use iranian gfx.'],
  'heritage_latin|eastern_unit_gfx': ['byzantine', 'Roman (restored Roman culture) uses eastern/Byzantine gfx.'],
  'heritage_magyar|mongol_unit_gfx': ['steppe', 'Mogyer (pre-conquest Magyars) are steppe nomads.'],
  'heritage_tai|chinese_unit_gfx': ['east_asian', 'Hlai (Hainan) use chinese gfx.'],
  'heritage_tai|mongol_unit_gfx': ['east_asian', 'Tujia (Hunan/Hubei hills) use mongol gfx in CK3 but live inside the Chinese sphere.'],
  'heritage_iberian|iberian_muslim_unit_gfx': ['andalusian', 'Andalusian uses iberian_muslim gfx.'],
};

// ---------------------------------------------------------------------------------------------------------------
// 2. LADDERS: role -> bl_culture -> rungs [{tier, troops:[{troop,weight}]}]. A MaA regiment picks a fractional tier
//    target and is split between the two rungs around it (see tier_rule).
const LADDERS = {
  heavy_infantry: {
    vlandia:  [R(3, ['vlandian_spearman', 1]), R(4, ['vlandian_swordsman', 0.7], ['vlandian_billman', 0.3]), R(5, ['vlandian_sergeant', 1])],
    empire:   [R(3, ['imperial_trained_infantryman', 1]), R(4, ['imperial_veteran_infantryman', 1]), R(5, ['imperial_legionary', 1])],
    sturgia:  [R(3, ['sturgian_soldier', 1]), R(4, ['sturgian_spearman', 1]), R(5, ['sturgian_veteran_warrior', 0.5], ['sturgian_shock_troop', 0.5])],
    battania: [R(3, ['battanian_trained_warrior', 1]), R(4, ['battanian_picked_warrior', 1]), R(5, ['battanian_oathsworn', 1])],
    aserai:   [R(3, ['aserai_footman', 1]), R(4, ['aserai_infantry', 1]), R(5, ['aserai_veteran_infantry', 1])],
    khuzait:  [R(3, ['khuzait_spearman', 1]), R(4, ['khuzait_spear_infantry', 1]), R(5, ['khuzait_darkhan', 1])],
  },
  heavy_infantry_2h: {
    vlandia:  [R(4, ['vlandian_billman', 1]), R(5, ['vlandian_voulgier', 0.5], ['vlandian_sergeant', 0.5])],
    empire:   [R(4, ['imperial_menavliaton', 1]), R(5, ['imperial_elite_menavliaton', 1])],
    sturgia:  [R(4, ['sturgian_veteran_warrior', 1]), R(5, ['sturgian_shock_troop', 0.5], ['sturgian_veteran_warrior', 0.5])],
    battania: [R(4, ['battanian_falxman', 1]), R(5, ['battanian_veteran_falxman', 1])],
    aserai:   [R(3, ['aserai_mameluke_axeman', 1]), R(4, ['aserai_mameluke_guard', 1]), R(5, ['mamluke_palace_guard', 1])],
    khuzait:  [R(4, ['khuzait_spear_infantry', 0.6], ['eastern_mercenary_t4', 0.4]), R(5, ['khuzait_darkhan', 0.6], ['eastern_mercenary_t5', 0.4])],
  },
  pikemen: {
    vlandia:  [R(3, ['vlandian_spearman', 1]), R(4, ['western_mercenary_t4', 0.6], ['vlandian_spearman', 0.4]), R(5, ['vlandian_pikeman', 0.7], ['western_mercenary_t5', 0.3])],
    empire:   [R(3, ['imperial_militia_veteran_spearman', 1]), R(4, ['imperial_menavliaton', 0.4], ['imperial_veteran_infantryman', 0.3], ['western_mercenary_t4', 0.3]), R(5, ['imperial_elite_menavliaton', 0.5], ['imperial_legionary', 0.2], ['western_mercenary_t5', 0.3])],
    sturgia:  [R(3, ['sturgian_soldier', 1]), R(4, ['sturgian_spearman', 1]), R(5, ['sturgian_shock_troop', 1])],
    battania: [R(3, ['battanian_trained_warrior', 1]), R(4, ['battanian_picked_warrior', 1]), R(5, ['battanian_oathsworn', 1])],
    aserai:   [R(3, ['aserai_footman', 1]), R(4, ['aserai_infantry', 1]), R(5, ['aserai_veteran_infantry', 1])],
    khuzait:  [R(3, ['khuzait_spearman', 1]), R(4, ['khuzait_spear_infantry', 1]), R(5, ['khuzait_darkhan', 1])],
  },
  archers: {
    vlandia:  [R(2, ['battanian_militia_archer', 0.5], ['imperial_archer', 0.5]), R(3, ['battanian_militia_veteran_archer', 0.5], ['imperial_trained_archer', 0.5]), R(4, ['sturgian_archer', 0.5], ['imperial_veteran_archer', 0.5]), R(5, ['sturgian_veteran_bowman', 0.6], ['imperial_palatine_guard', 0.4])],
    empire:   [R(2, ['imperial_archer', 1]), R(3, ['imperial_trained_archer', 1]), R(4, ['imperial_veteran_archer', 1]), R(5, ['imperial_palatine_guard', 1])],
    sturgia:  [R(2, ['sturgian_militia_archer', 1]), R(3, ['sturgian_hunter', 1]), R(4, ['sturgian_archer', 1]), R(5, ['sturgian_veteran_bowman', 1])],
    battania: [R(2, ['battanian_militia_archer', 1]), R(3, ['battanian_militia_veteran_archer', 0.6], ['battanian_highborn_warrior', 0.4]), R(4, ['battanian_hero', 1]), R(5, ['battanian_fian', 1])],
    aserai:   [R(2, ['aserai_militia_archer', 1]), R(3, ['aserai_skirmisher', 1]), R(4, ['aserai_archer', 1]), R(5, ['aserai_master_archer', 1])],
    khuzait:  [R(2, ['khuzait_militia_archer', 1]), R(3, ['khuzait_hunter', 1]), R(4, ['khuzait_archer', 1]), R(5, ['khuzait_marksman', 1])],
  },
  crossbowmen: {
    vlandia:  [R(2, ['vlandian_levy_crossbowman', 1]), R(3, ['vlandian_crossbowman', 1]), R(4, ['vlandian_hardened_crossbowman', 1]), R(5, ['vlandian_sharpshooter', 1])],
    empire:   [R(4, ['imperial_crossbowman', 1]), R(5, ['imperial_sergeant_crossbowman', 1])],
    sturgia:  [R(3, ['vlandian_crossbowman', 1]), R(4, ['western_crossbow_t4', 0.5], ['mercenary_5', 0.5]), R(5, ['western_crossbow_t5', 0.5], ['mercenary_8', 0.5])],
    battania: [R(3, ['vlandian_crossbowman', 1]), R(4, ['western_crossbow_t4', 0.5], ['mercenary_5', 0.5]), R(5, ['western_crossbow_t5', 0.5], ['mercenary_8', 0.5])],
    aserai:   [R(3, ['vlandian_crossbowman', 1]), R(4, ['western_crossbow_t4', 0.5], ['mercenary_5', 0.5]), R(5, ['western_crossbow_t5', 0.5], ['mercenary_8', 0.5])],
    khuzait:  [R(4, ['imperial_crossbowman', 0.5], ['mercenary_5', 0.5]), R(5, ['imperial_sergeant_crossbowman', 0.5], ['mercenary_8', 0.5])],
  },
  skirmishers: {
    vlandia:  [R(2, ['vlandian_footman', 1]), R(3, ['vlandian_militia_veteran_spearman', 0.5], ['western_mercenary', 0.5]), R(4, ['vlandian_spearman', 0.5], ['western_mercenary', 0.5]), R(5, ['vlandian_voulgier', 1])],
    empire:   [R(2, ['imperial_infantryman', 1]), R(3, ['imperial_trained_infantryman', 1]), R(4, ['imperial_veteran_infantryman', 1])],
    sturgia:  [R(2, ['sturgian_woodsman', 1]), R(3, ['sturgian_brigand', 1]), R(4, ['sturgian_spearman', 0.6], ['sturgian_brigand', 0.4]), R(5, ['sturgian_veteran_warrior', 1])],
    battania: [R(2, ['battanian_woodrunner', 1]), R(3, ['battanian_skirmisher', 1]), R(4, ['battanian_veteran_skirmisher', 1]), R(5, ['battanian_wildling', 1])],
    aserai:   [R(2, ['aserai_tribesman', 1]), R(3, ['aserai_footman', 1]), R(4, ['aserai_infantry', 1]), R(5, ['aserai_veteran_infantry', 1])],
    khuzait:  [R(2, ['khuzait_militia_archer', 0.5], ['khuzait_footman', 0.5]), R(3, ['khuzait_hunter', 0.5], ['khuzait_spearman', 0.5]), R(4, ['khuzait_archer', 0.5], ['khuzait_spear_infantry', 0.5])],
  },
  light_cavalry: {
    vlandia:  [R(3, ['vlandian_light_cavalry', 1]), R(4, ['vlandian_cavalry', 0.6], ['mercenary_6', 0.4]), R(5, ['mercenary_9', 1])],
    empire:   [R(2, ['mercenary_3', 1]), R(3, ['imperial_equite', 0.6], ['mercenary_3', 0.4]), R(4, ['imperial_heavy_horseman', 0.6], ['mercenary_6', 0.4]), R(5, ['mercenary_9', 1])],
    sturgia:  [R(3, ['mercenary_3', 1]), R(4, ['sturgian_hardened_brigand', 1]), R(5, ['sturgian_horse_raider', 1])],
    battania: [R(3, ['mercenary_3', 1]), R(4, ['battanian_scout', 1]), R(5, ['battanian_mounted_skirmisher', 0.5], ['battanian_horseman', 0.5])],
    aserai:   [R(2, ['aserai_youth', 1]), R(3, ['aserai_tribal_horseman', 0.6], ['aserai_mameluke_regular', 0.4]), R(4, ['aserai_faris', 1]), R(5, ['aserai_veteran_faris', 1])],
    khuzait:  [R(2, ['khuzait_tribal_warrior', 1]), R(3, ['khuzait_horseman', 1]), R(4, ['khuzait_lancer', 0.5], ['khuzait_horse_archer', 0.5]), R(5, ['khuzait_heavy_lancer', 1])],
  },
  heavy_cavalry: {
    vlandia:  [R(3, ['vlandian_light_cavalry', 1]), R(4, ['vlandian_cavalry', 1]), R(5, ['vlandian_vanguard', 0.7], ['vlandian_champion', 0.3]), R(6, ['vlandian_banner_knight', 1])],
    empire:   [R(3, ['imperial_equite', 1]), R(4, ['imperial_heavy_horseman', 1]), R(5, ['imperial_cataphract', 1]), R(6, ['imperial_elite_cataphract', 1])],
    sturgia:  [R(4, ['varyag_veteran', 1]), R(5, ['druzhinnik', 0.6], ['sturgian_horse_raider', 0.4]), R(6, ['druzhinnik_champion', 1])],
    battania: [R(4, ['battanian_scout', 1]), R(5, ['battanian_horseman', 1])],
    aserai:   [R(3, ['aserai_mameluke_regular', 1]), R(4, ['aserai_faris', 1]), R(5, ['aserai_veteran_faris', 1]), R(6, ['aserai_vanguard_faris', 1])],
    khuzait:  [R(3, ['khuzait_horseman', 1]), R(4, ['khuzait_lancer', 1]), R(5, ['khuzait_heavy_lancer', 1])],
  },
  archer_cavalry: {
    vlandia:  [R(4, ['eastern_mounted_mercenary_t4', 1]), R(5, ['eastern_mounted_mercenary_t5', 0.5], ['bucellarii', 0.5])],
    empire:   [R(4, ['eastern_mounted_mercenary_t4', 1]), R(5, ['bucellarii', 1])],
    sturgia:  [R(3, ['khuzait_raider', 1]), R(4, ['eastern_mounted_mercenary_t4', 0.5], ['khuzait_horse_archer', 0.5]), R(5, ['khuzait_heavy_horse_archer', 1])],
    battania: [R(4, ['eastern_mounted_mercenary_t4', 1]), R(5, ['eastern_mounted_mercenary_t5', 0.5], ['bucellarii', 0.5])],
    aserai:   [R(4, ['aserai_mameluke_cavalry', 1]), R(5, ['aserai_mameluke_heavy_cavalry', 0.7], ['veteran_caravan_guard_aserai', 0.3])],
    khuzait:  [R(2, ['khuzait_tribal_warrior', 1]), R(3, ['khuzait_raider', 0.6], ['khuzait_qanqli', 0.4]), R(4, ['khuzait_horse_archer', 0.7], ['khuzait_torguud', 0.3]), R(5, ['khuzait_heavy_horse_archer', 0.6], ['khuzait_kheshig', 0.4]), R(6, ['khuzait_khans_guard', 1])],
  },
  militia: {
    vlandia:  [R(2, ['vlandian_militia_spearman', 1]), R(3, ['vlandian_militia_veteran_spearman', 1])],
    empire:   [R(2, ['imperial_militia_spearman', 1]), R(3, ['imperial_militia_veteran_spearman', 1])],
    sturgia:  [R(2, ['sturgian_militia_spearman', 1]), R(3, ['sturgian_militia_veteran_spearman', 1])],
    battania: [R(2, ['battanian_militia_spearman', 1]), R(3, ['battanian_militia_veteran_spearman', 1])],
    aserai:   [R(2, ['aserai_militia_spearman', 1]), R(3, ['aserai_militia_veteran_spearman', 1])],
    khuzait:  [R(2, ['khuzait_militia_spearman', 1]), R(3, ['khuzait_militia_veteran_spearman', 1])],
  },
};
// Culture-independent ladders (referenced by overrides via `ladder_ref`).
const SHARED_LADDERS = {
  camel: [R(3, ['jawwal_tier_2', 1]), R(4, ['jawwal_tier_3', 0.5], ['ghilman_tier_3', 0.5])],
  longbow: [R(2, ['brotherhood_of_woods_tier_1', 0.5], ['battanian_highborn_youth', 0.5]), R(3, ['brotherhood_of_woods_tier_2', 0.5], ['battanian_highborn_warrior', 0.5]), R(4, ['brotherhood_of_woods_tier_3', 0.4], ['battanian_hero', 0.6]), R(5, ['battanian_fian', 1]), R(6, ['battanian_fian_champion', 1])],
  huscarl: [R(4, ['sturgian_spearman', 0.6], ['huskarl_swordsman', 0.4]), R(5, ['huskarl_swordsman', 0.5], ['sturgian_veteran_warrior', 0.3], ['sturgian_shock_troop', 0.2])],
  varangian: [R(4, ['varyag', 0.4], ['sturgian_spearman', 0.3], ['huskarl_swordsman', 0.3]), R(5, ['sturgian_veteran_warrior', 0.5], ['huskarl_swordsman', 0.3], ['sturgian_shock_troop', 0.2])],
  jomsviking: [R(4, ['skolderbrotva_tier_3', 0.5], ['lakepike_tier_3', 0.5]), R(5, ['skolderbrotva_tier_3', 0.4], ['sturgian_veteran_warrior', 0.3], ['huskarl_swordsman', 0.3])],
  berber_light_horse: BERBER_LIGHT_HORSE,
  schiltron: [R(3, ['battanian_trained_warrior', 0.5], ['vlandian_spearman', 0.5]), R(4, ['western_mercenary_t4', 0.5], ['battanian_picked_warrior', 0.5]), R(5, ['vlandian_pikeman', 0.4], ['western_mercenary_t5', 0.3], ['battanian_oathsworn', 0.3])],
  naginata: [R(4, ['imperial_menavliaton', 0.6], ['khuzait_spear_infantry', 0.4]), R(5, ['imperial_elite_menavliaton', 0.6], ['khuzait_darkhan', 0.4])],
  zhanmadao: [R(4, ['khuzait_spear_infantry', 0.4], ['imperial_menavliaton', 0.3], ['eastern_mercenary_t4', 0.3]), R(5, ['khuzait_darkhan', 0.4], ['imperial_elite_menavliaton', 0.3], ['eastern_mercenary_t5', 0.3])],
  metsanvartija: [R(3, ['forest_people_tier_2', 1]), R(4, ['forest_people_tier_3', 1]), R(5, ['forest_people_tier_3', 0.5], ['sturgian_veteran_bowman', 0.5])],
  order_knights: [R(4, ['vlandian_knight', 0.5], ['vlandian_cavalry', 0.5]), R(5, ['vlandian_champion', 0.6], ['vlandian_vanguard', 0.4]), R(6, ['vlandian_banner_knight', 1])],
};

// base type -> default ladder role, reference (regular) MaA and its Bannerlord tier. The reference MaA defines what a
// "standard" regiment of that base type looks like; all other MaA of the type are placed relative to it.
const BASE_ROLE = {
  heavy_infantry:   { role: 'heavy_infantry', ref_maa: 'armored_footmen',  ref_tier: 4 },
  pikemen:          { role: 'pikemen',        ref_maa: 'pikemen_unit',     ref_tier: 4 },
  archers:          { role: 'archers',        ref_maa: 'bowmen',           ref_tier: 3 },
  skirmishers:      { role: 'skirmishers',    ref_maa: 'light_footmen',    ref_tier: 3 },
  light_cavalry:    { role: 'light_cavalry',  ref_maa: 'light_horsemen',   ref_tier: 3 },
  heavy_cavalry:    { role: 'heavy_cavalry',  ref_maa: 'armored_horsemen', ref_tier: 4 },
  archer_cavalry:   { role: 'archer_cavalry', ref_maa: 'horse_archers',    ref_tier: 4 },
  camel_cavalry:    { role: 'camel',          ref_maa: 'camel_rider',      ref_tier: 3, ladder_ref: 'camel' },
  elephant_cavalry: { role: 'heavy_cavalry',  ref_maa: 'armored_horsemen', ref_tier: 4, note: 'no elephants in Bannerlord: heavy cavalry substitute, tier measured against armored_horsemen' },
  gunpowder:        { role: 'crossbowmen',    ref_maa: 'crossbowmen',      ref_tier: 4.6, note: 'no single-player firearms: crossbow substitute; ref crossbowmen sits at tier 4.6 on the archer scale' },
  nomadic_horde:    { role: 'archer_cavalry', ref_maa: 'nomadic_riders',   ref_tier: 2, force_culture: 'khuzait' },
  peasant_militia:  { role: 'militia',        ref_maa: 'pikemen_militia',  ref_tier: 2 },
  siege_weapon:     { role: null, exclude: true, note: 'fights_in_main_phase = no for all 9 siege MaA: never spawned in a field battle' },
};

// ---------------------------------------------------------------------------------------------------------------
// 3. MaA overrides (specific keys). Fields: ladder_role, ladder_ref (SHARED_LADDERS), force_culture, tier_target,
//    agent_multiplier, exclude, why, confidence. Anything not set falls back to BASE_ROLE + the computed tier.
const MAA_OVERRIDES = {
  // heavy infantry
  huscarl:            { ladder_ref: 'huscarl', why: 'Hird housecarls (Norse/Anglo-Danish): Sturgian heavy axemen and the legacy "Huskarl Swordsman" troop, whatever the owner culture.' },
  varangian_veterans: { ladder_ref: 'varangian', why: 'Norse/Rus veterans of Miklagard with Dane axes: Sturgian Varyag / Line Breaker kit.' },
  varangian_guards:   { ladder_ref: 'varangian', why: 'Imperial Varangian Guard is Norse/Rus/English even when the owner is Greek, so the Sturgian axe line is used regardless of culture.' },
  praetorian:         { force_culture: 'empire', why: 'Revived Roman legionaries (legionnaires innovation / Hellenic holy order): Imperial Legionary.' },
  druzhina:           { force_culture: 'sturgia', why: 'Rus retinue infantry; Sturgia\'s heavy infantry line is literally modelled on the druzhina.' },
  order_serjeants:    { force_culture: 'vlandia', why: 'Serjeant-brothers of the Catholic military orders: Vlandian sergeants.' },
  samurai:            { ladder_ref: 'naginata', why: 'Foot samurai with naginata/yari: Imperial menavliaton (polearm) and Khuzait lamellar spearmen.' },
  zhanmadao_infantry: { ladder_ref: 'zhanmadao', why: 'Song "horse-cutting saber" two-handed infantry: polearm/two-handed heavy foot.' },
  // pikemen
  landsknecht:        { force_culture: 'vlandia', why: 'German pike and zweihander mercenaries: Vlandian pikemen + Hired Pike.' },
  picchieri:          { force_culture: 'vlandia', why: 'Italian communal pikemen: Vlandian pike line.' },
  schiltron:          { ladder_ref: 'schiltron', why: 'Scottish pike rings: real pikes (Hired Pike, Vlandian Pikeman) mixed with Battanian spearmen for the look.' },
  skoutatoi:          { force_culture: 'empire', why: 'Byzantine shield-and-spear line infantry.' },
  warrior_monks:      { ladder_ref: 'naginata', why: 'Sohei with naginata: polearm infantry.' },
  // archers
  longbowmen:         { ladder_ref: 'longbow', tier_target: 4.5, why: 'CK3 longbowmen have low per-man stats (20/8) but counter heavy cavalry and skirmishers (counters cut the target\'s damage by up to 90%, MEN_AT_ARMS_MAX_COUNTER 0.9). Bannerlord can only express that through stronger long bows, so the tier is raised to 4.5 on the long-bow ladder (Battanian highborn/fian + Brotherhood of the Woods).' },
  crossbowmen:        { ladder_role: 'crossbowmen', why: 'Crossbow MaA use the crossbow ladder.' },
  accolade_maa_crossbowers: { ladder_role: 'crossbowmen', why: 'Crossbow accolade regiment.' },
  ballistrai:         { ladder_role: 'crossbowmen', force_culture: 'empire', why: 'Byzantine tzangratores (hand-ballista/crossbow men): Imperial crossbowmen.' },
  shenbigong:         { ladder_role: 'crossbowmen', why: 'Song "divine arm bow" is a heavy crossbow.' },
  rocket_carts:       { ladder_role: 'crossbowmen', why: 'Fire-arrow carts: no artillery in a field mission, represented as a heavy missile volley (crossbows).', confidence: 'low' },
  metsanvartija:      { ladder_ref: 'metsanvartija', why: 'Finnic "forest guardians": Vakken forest-people archers (Bannerlord\'s Finno-Ugric minor faction).' },
  // skirmishers
  jomsviking_pirates: { ladder_ref: 'jomsviking', why: 'Elite Norse mercenaries (30/30): Skolderbrotva/Lakepike and Sturgian heavy axemen.' },
  akritai:            { force_culture: 'empire', why: 'Byzantine border light troops.' },
  // light cavalry
  caballero:          { force_culture: 'aserai', why: 'Iberian caballeros villanos / jinetes: javelin light horse (Aserai tribal horsemen, Beni Zilal).' },
  mulaththamun:       { ladder_ref: 'berber_light_horse', why: 'Almoravid veiled Berber cavalry: the Berber javelin light horse (Beni Zilal) whatever the owner culture.' },
  steppe_raiders:     { force_culture: 'khuzait', why: 'Nomad light horse.' },
  ayrudzi:            { ladder_role: 'heavy_cavalry', tier_target: 4.5, why: 'Armenian noble horsemen (50/30, stack 50) are armoured lancer-archers, closer to heavy cavalry than to scouts.' },
  // heavy cavalry
  gendarme:           { force_culture: 'vlandia', why: 'French men-at-arms in plate.' },
  conrois:            { force_culture: 'vlandia', why: 'Norman/Frankish conroi of knights.' },
  cataphract:         { force_culture: 'empire', tier_target: 5, why: 'Byzantine kataphraktoi. The formula gives 4.32 (125/35 is only 25% above armored_horsemen per soldier), but Bannerlord has a troop literally named Imperial Cataphract at T5, so the tier is pinned to 5 for recognisability; the power gap is small.' },
  order_knights:      { ladder_ref: 'order_knights', tier_target: 5, why: 'Templar/Hospitaller brother-knights were dedicated professional knights; CK3 rates them 112/36 at stack 50 (top per-man heavy cavalry), so they use the Vlandian noble knight line at tier 5.' },
  nomad_lancers:      { force_culture: 'khuzait', why: 'Steppe lancers.' },
  tiefutu:            { force_culture: 'khuzait', why: 'Jurchen "iron pagoda" lancers: Khuzait heavy lancers.' },
  cataphract_archers: { ladder_role: 'archer_cavalry', force_culture: 'khuzait', tier_target: 5, why: 'Armoured horse archers (Khans of the Steppe): Khuzait heavy horse archers / kheshig.' },
  // archer cavalry
  kheshig:            { force_culture: 'khuzait', why: 'Mongol imperial guard: Khuzait Kheshig / Khan\'s Guard.' },
  heavy_horse_archers:{ force_culture: 'khuzait', why: 'Nomad heavy horse archers.' },
  mangudai:           { force_culture: 'khuzait', why: 'Light steppe horse archers.' },
  japanese_horse_archers: { force_culture: 'khuzait', why: 'Mounted samurai (yumi + lamellar o-yoroi): Khuzait noble horse archers.' },
  // camels / elephants / gunpowder / horde
  camel_rider:        { ladder_ref: 'camel', why: 'Only native camel riders are the Jawwal (light) and Ghilman (heavy) minor factions; used for every culture.' },
  accolade_maa_cameliers: { ladder_ref: 'camel', why: 'Camel accolade regiment.' },
  war_elephant:       { ladder_role: 'heavy_cavalry', why: 'No elephant monster/item exists in Bannerlord 1.4.8. Substitute: the culture\'s heaviest cavalry at the computed tier (250/50 per man is about an accolade lancer); any tier above the ladder top is converted into extra agents (agent_multiplier).' },
  accolade_maa_elephantiers: { ladder_role: 'heavy_cavalry', why: 'Elephant accolade (400/100): heavy cavalry substitute at the ladder top plus extra agents for the tier overflow.' },
  ballista_elephant:  { ladder_role: 'archer_cavalry', why: 'Elephant-mounted ballista: heavy horse archers substitute (mobile missile platform).' },
  handgunners:        { ladder_role: 'crossbowmen', why: 'No firearms are used by any single-player item in 1.4.8: heavy crossbowmen substitute.' },
  accolade_maa_handgunners: { ladder_role: 'crossbowmen', why: 'Gunpowder accolade: crossbow substitute.' },
  fire_lancers:       { ladder_role: 'crossbowmen', why: 'Fire lances (short-range gunpowder): crossbow substitute.', confidence: 'low' },
  nomadic_riders:     { force_culture: 'khuzait', why: 'Nomad herd riders: low-tier Khuzait horse archers (tribal warriors).' },
};

// ---------------------------------------------------------------------------------------------------------------
// 3b. Restricted troops: bandit, minor-faction, quest and civilian lines that look wrong
//     outside their own setting. A troop matching a pattern may only be resolved where one of its `allow` entries matches; an
//     empty list means never. Entry fields (all optional, all given ones must match): profiles, maa (CK3 MaA keys), ladders
//     (shared_ladders keys), uses (levy | maa | knights), min_era / max_era (inclusive). Checked by the bridge
//     (CK3xBL.Mapping.Data.TroopRestrictions) over every profile x era x MaA, levy and knight resolution.
const STURGIA_PROFILES = ['norse', 'east_slavic', 'west_slavic', 'baltic_finnic', 'balkan'];
const NEVER = (pattern, what) => ({ pattern, what, allow: [] });
const RESTRICTED_TROOPS = {
  note: 'Troops matching a pattern resolve only where an allow entry matches (profile/MaA/shared ladder/use/era); allow [] = never. Checked by CK3xBL.Mapping.Data.TroopRestrictions at load.',
  rules: [
    { pattern: 'brotherhood_of_woods_*', what: 'Brotherhood of the Woods: Vlandian forest outlaws with long bows', allow: [{ ladders: ['longbow'], why: 'English and Welsh longbowmen (longbowmen MaA): the yeoman look' }] },
    NEVER('wolfskins_*', 'Wolfskins: Battanian wolf-head bandit archers'),
    { pattern: 'forest_people_*', what: 'Vakken foresters: Finno-Ugric forest archers', allow: [{ profiles: ['baltic_finnic', 'siberian'], uses: ['levy', 'maa'], why: 'Baltic, Finnic and Siberian forest peoples' }, { ladders: ['metsanvartija'], why: 'Finnic forest guardians' }] },
    { pattern: 'skolderbrotva_*', what: 'Skolderbrotva: Nord raider brotherhood', allow: [{ profiles: ['norse'], uses: ['maa'], max_era: 'culture_era_early_medieval', why: 'Viking-age Norse skirmishers' }, { ladders: ['jomsviking'], why: 'Jomsviking mercenaries are Norse whoever hires them' }] },
    { pattern: 'lakepike_*', what: 'Lake Rats: Sturgian river pirates', allow: [{ profiles: ['norse'], uses: ['maa'], max_era: 'culture_era_early_medieval', why: 'Viking-age Norse skirmishers' }, { ladders: ['jomsviking'], why: 'Jomsviking mercenaries' }] },
    { pattern: 'beni_zilal_*', what: 'Beni Zilal: Berber javelin horsemen', allow: [{ profiles: ['berber'], uses: ['levy', 'maa'], why: 'Berber light horse' }, { ladders: ['berber_light_horse'], why: 'Almoravid veiled cavalry (mulaththamun)' }] },
    { pattern: 'jawwal_*', what: 'Jawwal: Bedouin camel riders', allow: [{ ladders: ['camel'], why: 'the only native camel riders' }] },
    { pattern: 'ghilman_*', what: 'Ghilman: Darshi slave soldiers, heavy camel rider at tier 3', allow: [{ ladders: ['camel'], why: 'the only heavy camel rider' }] },
    { pattern: 'galloglass_*', what: 'Galloglass: Hebridean mercenary axemen', allow: [{ profiles: ['celtic'], uses: ['maa'], min_era: 'culture_era_high_medieval', why: 'Gaelic heavy infantry from the 13th century' }] },
    NEVER('eleftheroi_*', 'Eleftheroi: unarmoured highland lancers of a Calradian minor faction'),
    NEVER('legion_of_the_betrayed_*', 'Legion of the Betrayed: renegade legionaries (hastati/principes/triarii)'),
    NEVER('hidden_hand_*', 'Hidden Hand: slingers and knife throwers of a secret society'),
    NEVER('embers_of_flame_*', 'Embers of the Flame: robed religious zealots'),
    NEVER('company_of_the_boar_*', 'Company of the Boar: a named Vlandian crossbow company'),
    { pattern: 'karakhuzaits_*', what: 'Karakhergits: steppe nomad horse archers', allow: [{ profiles: ['steppe'], uses: ['maa'], why: 'Turkic and Mongolic horse archers' }] },
    NEVER('guardians_*', 'Guardians: quest archers'),
    NEVER('sword_sister*', 'Sword Sisters: quest and arena women'),
    NEVER('gangster_*', 'Thugs: town gangsters'),
    { pattern: '*brigand*', what: 'Sturgian brigand line (regular Sturgian skirmishers and light horse, bandit-named)', allow: [{ profiles: STURGIA_PROFILES, why: 'profiles built on Sturgia (troop trees or levies)' }] },
    NEVER('armed_trader_*', 'Armed traders: civilian caravan crew'),
    NEVER('caravan_*', 'Caravan guards and masters: civilian caravan crew'),
    NEVER('prison_guard_*', 'Prison guards: town guards'),
    NEVER('*_fighter', 'Arena fighters'),
    { pattern: 'sturgian_berzerker', what: 'Sturgian Line Breaker: wolf-skin berserker', allow: [{ profiles: ['norse'], max_era: 'culture_era_early_medieval', why: 'Viking-age Norse only' }] },
    { pattern: 'sturgian_ulfhednar', what: 'Sturgian Heroic Line Breaker: wolf-skin berserker', allow: [{ profiles: ['norse'], max_era: 'culture_era_early_medieval', why: 'Viking-age Norse only' }] },
  ],
};

// ---------------------------------------------------------------------------------------------------------------
// 4. Levy variants. melee/ranged/mounted ladders + shares; era sets the tier target (same tier scale as MaA:
//    10/10 levy vs armored_footmen 32/22 gives 4 + 2*log2(sqrt(10/32*10/22)) = 1.2).
const LEVY_ERA_TIER = { culture_era_tribal: 1.0, culture_era_early_medieval: 1.4, culture_era_high_medieval: 1.8, culture_era_late_medieval: 2.2 };
const MEL = {
  vlandia:  [R(1, ['vlandian_recruit', 1]), R(2, ['vlandian_militia_spearman', 0.55], ['vlandian_footman', 0.45]), R(3, ['vlandian_militia_veteran_spearman', 0.6], ['vlandian_spearman', 0.4])],
  empire:   [R(1, ['imperial_recruit', 1]), R(2, ['imperial_militia_spearman', 0.5], ['imperial_infantryman', 0.5]), R(3, ['imperial_militia_veteran_spearman', 0.6], ['imperial_trained_infantryman', 0.4])],
  sturgia:  [R(1, ['sturgian_recruit', 1]), R(2, ['sturgian_militia_spearman', 0.45], ['sturgian_warrior', 0.4], ['sturgian_woodsman', 0.15]), R(3, ['sturgian_militia_veteran_spearman', 0.5], ['sturgian_soldier', 0.3], ['sturgian_brigand', 0.2])],
  battania: [R(1, ['battanian_volunteer', 1]), R(2, ['battanian_militia_spearman', 0.4], ['battanian_clanwarrior', 0.4], ['battanian_woodrunner', 0.2]), R(3, ['battanian_militia_veteran_spearman', 0.4], ['battanian_trained_warrior', 0.3], ['battanian_skirmisher', 0.3])],
  aserai:   [R(1, ['aserai_recruit', 1]), R(2, ['aserai_militia_spearman', 0.5], ['aserai_tribesman', 0.5]), R(3, ['aserai_militia_veteran_spearman', 0.6], ['aserai_footman', 0.4])],
  berber:   [R(1, ['aserai_recruit', 1]), R(2, ['aserai_tribesman', 0.6], ['aserai_militia_spearman', 0.4]), R(3, ['aserai_militia_veteran_spearman', 0.5], ['aserai_footman', 0.5])],
  tropical: [R(1, ['aserai_recruit', 1]), R(2, ['aserai_tribesman', 0.7], ['aserai_militia_spearman', 0.3]), R(3, ['aserai_militia_veteran_spearman', 0.5], ['aserai_footman', 0.5])],
  khuzait:  [R(1, ['khuzait_nomad', 1]), R(2, ['khuzait_militia_spearman', 0.5], ['khuzait_footman', 0.5]), R(3, ['khuzait_militia_veteran_spearman', 0.6], ['khuzait_spearman', 0.4])],
};
const RNG = {
  vlandia:  [R(2, ['vlandian_militia_archer', 0.6], ['vlandian_levy_crossbowman', 0.4]), R(3, ['vlandian_militia_veteran_archer', 0.6], ['vlandian_crossbowman', 0.4])],
  empire:   [R(2, ['imperial_militia_archer', 0.6], ['imperial_archer', 0.4]), R(3, ['imperial_militia_veteran_archer', 0.6], ['imperial_trained_archer', 0.4])],
  sturgia:  [R(2, ['sturgian_militia_archer', 1]), R(3, ['sturgian_militia_veteran_archer', 0.6], ['sturgian_hunter', 0.4])],
  battania: [R(2, ['battanian_militia_archer', 1]), R(3, ['battanian_militia_veteran_archer', 1])],
  aserai:   [R(2, ['aserai_militia_archer', 1]), R(3, ['aserai_militia_veteran_archer', 0.6], ['aserai_skirmisher', 0.4])],
  khuzait:  [R(2, ['khuzait_militia_archer', 1]), R(3, ['khuzait_militia_veteran_archer', 0.6], ['khuzait_hunter', 0.4])],
  forest_n: [R(2, ['forest_people_tier_1', 0.6], ['sturgian_militia_archer', 0.4]), R(3, ['forest_people_tier_2', 0.6], ['sturgian_hunter', 0.4])],
  forest_e: [R(2, ['forest_people_tier_1', 0.5], ['khuzait_militia_archer', 0.5]), R(3, ['forest_people_tier_2', 0.5], ['khuzait_hunter', 0.5])],
};
const MNT = {
  khuzait:  [R(2, ['khuzait_tribal_warrior', 1]), R(3, ['khuzait_raider', 0.6], ['khuzait_horseman', 0.4])],
  aserai:   [R(2, ['aserai_youth', 1]), R(3, ['aserai_tribal_horseman', 0.5], ['aserai_mameluke_regular', 0.5])],
  berber:   [R(2, ['beni_zilal_tier_1', 1]), R(3, ['beni_zilal_tier_2', 0.5], ['aserai_tribal_horseman', 0.5])],
};
const LEVY_VARIANTS = {
  vlandia:        { melee: MEL.vlandia,  ranged: RNG.vlandia,  shares: { melee: 0.75, ranged: 0.25 } },
  empire:         { melee: MEL.empire,   ranged: RNG.empire,   shares: { melee: 0.75, ranged: 0.25 } },
  sturgia:        { melee: MEL.sturgia,  ranged: RNG.sturgia,  shares: { melee: 0.75, ranged: 0.25 } },
  battania:       { melee: MEL.battania, ranged: RNG.battania, shares: { melee: 0.7, ranged: 0.3 } },
  aserai:         { melee: MEL.aserai,   ranged: RNG.aserai,   shares: { melee: 0.75, ranged: 0.25 } },
  khuzait:        { melee: MEL.khuzait,  ranged: RNG.khuzait,  mounted: MNT.khuzait, shares: { melee: 0.45, ranged: 0.2, mounted: 0.35 } },
  steppe_nomadic: { melee: MEL.khuzait,  ranged: RNG.khuzait,  mounted: MNT.khuzait, shares: { melee: 0.25, ranged: 0.15, mounted: 0.6 } },
  steppe_settled: { melee: MEL.khuzait,  ranged: RNG.khuzait,  mounted: MNT.khuzait, shares: { melee: 0.6, ranged: 0.3, mounted: 0.1 } },
  east_asian:     { melee: MEL.khuzait,  ranged: RNG.khuzait,  shares: { melee: 0.65, ranged: 0.35 } },
  siberian:       { melee: MEL.khuzait,  ranged: RNG.forest_e, shares: { melee: 0.55, ranged: 0.45 } },
  baltic_forest:  { melee: MEL.sturgia,  ranged: RNG.forest_n, shares: { melee: 0.65, ranged: 0.35 } },
  berber:         { melee: MEL.berber,   ranged: RNG.aserai,   mounted: MNT.berber, shares: { melee: 0.65, ranged: 0.25, mounted: 0.1 } },
  sahel:          { melee: MEL.aserai,   ranged: RNG.aserai,   mounted: MNT.aserai, shares: { melee: 0.6, ranged: 0.3, mounted: 0.1 } },
  tropical_foot:  { melee: MEL.tropical, ranged: RNG.aserai,   shares: { melee: 0.65, ranged: 0.35 } },
  indian:         { melee: MEL.aserai,   ranged: RNG.aserai,   shares: { melee: 0.65, ranged: 0.35 } },
};

// ---------------------------------------------------------------------------------------------------------------
// 5. Knights (default per Bannerlord culture) and commanders.
const KNIGHTS = {
  vlandia:  L(['vlandian_banner_knight', 0.5], ['vlandian_champion', 0.5]),
  empire:   L(['imperial_elite_cataphract', 0.6], ['imperial_cataphract', 0.4]),
  sturgia:  L(['druzhinnik_champion', 0.6], ['druzhinnik', 0.4]),
  battania: L(['battanian_fian_champion', 0.4], ['battanian_oathsworn', 0.3], ['battanian_horseman', 0.3]),
  aserai:   L(['aserai_vanguard_faris', 0.6], ['aserai_veteran_faris', 0.4]),
  khuzait:  L(['khuzait_khans_guard', 0.5], ['khuzait_heavy_lancer', 0.3], ['khuzait_kheshig', 0.2]),
};

// ---------------------------------------------------------------------------------------------------------------
// 6. Scenes. Hand-classified from custom_battle_scenes.xml names + terrain attribute + SandBox sp_battle_scenes
//    TerrainTypes (INFERRED; audit visually). Every id is validated against the XML and the SceneObj folder.
const S = (...ids) => ids.map((x) => (x.startsWith('battle_terrain_') ? x : 'battle_terrain_' + x));
const TERRAIN_SCENES = {
  plains:          S('biome_025', 'biome_047', 'biome_130', 'biome_131', '032', 'biome_027', 'biome_149', 'biome_005', '029', 'biome_012'),
  farmlands:       S('biome_130', '032', 'biome_131', 'biome_053', 'biome_012', 'biome_096', 'biome_005'),
  hills:           S('biome_050', 'O', 'i', 'L', 'm', '026', 'biome_098', 'z', 'biome_148', '020'),
  terraced_hills:  S('biome_096', '026', 'z', 'biome_012', 'm', 'L'),
  mountains:       S('biome_034', 'biome_044', 'biome_128', '001', '004', '030', '031', '007'),
  desert:          S('010', 'b', 'd', 'e', '009', 'biome_067', 'biome_076'),
  desert_mountains:S('g', '008', 'biome_088'),
  oasis:           S('biome_075', 'biome_083', 'biome_065', '022'),
  jungle:          S('k', 'h', 'v', 'p'),
  forest:          S('a', 'biome_006', 'biome_106', 'v', 'h', 'J', 'n', 'r', 't', 'u', '003', '023', '024', '025', '028', 'p', 'biome_097'),
  taiga:           S('J', 'u', 'a', 'biome_046', 't', 'biome_107'),
  wetlands:        S('f', '005', '034', '033', 'biome_092', 'biome_046'),
  steppe:          S('006', '012', '013', '014', '015', '019', 'biome_089', '018', '017', 'y'),
  floodplains:     S('s', '011', 'biome_030', 'biome_048', 'biome_107', 'biome_053', 'biome_092', '027'),
  drylands:        S('biome_040', 'biome_094', 'biome_088', 'biome_076', '014', '012', 'biome_148'),
  coastal_sea:     S('029', '021', 'biome_028', '020', 'p', 'biome_005'),
  sea:             S('029', '021', 'biome_028', '020', 'p', 'biome_005'),
};
const COASTAL_SCENES = S('029', '021', 'biome_028', '020', 'p', 'biome_005', 'y', '022', 'biome_083', 'biome_065');
const RIVER_SCENES = S('s', '011', 'biome_030', 'biome_048', 'biome_107', 'biome_053', 'biome_092', '027', 'biome_089', '018', '017', 'biome_075', 'f');
// climate -> terrain -> scene pool replacing the default pool (location look beats terrain label)
const CLIMATE_SCENES = {
  arid:     { plains: TERRAIN_SCENES.drylands, farmlands: S('biome_075', '022', 'biome_076', 'biome_083'), hills: S('biome_088', 'biome_148', 'g', '008'), floodplains: S('biome_075', 'biome_089', '017'), forest: TERRAIN_SCENES.drylands, steppe: TERRAIN_SCENES.drylands },
  steppe:   { plains: TERRAIN_SCENES.steppe, farmlands: S('biome_089', '018', '017', '006'), hills: S('biome_148', '007', 'biome_088', '014') },
  tropical: { forest: TERRAIN_SCENES.jungle, hills: S('k', 'h', 'biome_098', 'biome_097') },
  cold:     { forest: TERRAIN_SCENES.taiga },
  highland: { plains: S('biome_098', '014', 'biome_088', '007'), hills: S('biome_098', '007', 'biome_148', 'biome_044') },
};
// ordered CK3 geographical_region tests (province scope trigger `geographical_region = X`), first match wins
const CLIMATE_REGIONS = [
  ['world_siberia', 'cold'], ['world_europe_north', 'cold'], ['world_tibet', 'highland'], ['world_himalaya', 'highland'],
  ['world_steppe', 'steppe'], ['world_asia_shiwei_steppe', 'steppe'], ['world_asia_khitan_steppe', 'steppe'],
  ['world_middle_east', 'arid'], ['world_africa_north', 'arid'], ['world_africa_sahara', 'arid'],
  ['world_india', 'tropical'], ['world_burma', 'tropical'], ['world_asia_southeast', 'tropical'], ['world_africa_west', 'tropical'], ['world_africa_east', 'tropical'],
  ['world_europe_east', 'continental'], ['world_asia_north_east', 'continental'],
  ['world_europe', 'temperate'], ['world_asia_minor', 'temperate'], ['world_asia_east', 'temperate'],
];
const SEASON_BY_MONTH = { 1: 'winter', 2: 'winter', 3: 'spring', 4: 'spring', 5: 'spring', 6: 'summer', 7: 'summer', 8: 'summer', 9: 'fall', 10: 'fall', 11: 'fall', 12: 'winter' };
const SEASON_BY_CLIMATE = {
  temperate:   SEASON_BY_MONTH,
  continental: { 1: 'winter', 2: 'winter', 3: 'winter', 4: 'spring', 5: 'spring', 6: 'summer', 7: 'summer', 8: 'summer', 9: 'fall', 10: 'fall', 11: 'winter', 12: 'winter' },
  cold:        { 1: 'winter', 2: 'winter', 3: 'winter', 4: 'winter', 5: 'spring', 6: 'summer', 7: 'summer', 8: 'summer', 9: 'fall', 10: 'fall', 11: 'winter', 12: 'winter' },
  highland:    { 1: 'winter', 2: 'winter', 3: 'winter', 4: 'spring', 5: 'spring', 6: 'summer', 7: 'summer', 8: 'summer', 9: 'fall', 10: 'fall', 11: 'winter', 12: 'winter' },
  steppe:      { 1: 'winter', 2: 'winter', 3: 'spring', 4: 'spring', 5: 'spring', 6: 'summer', 7: 'summer', 8: 'summer', 9: 'fall', 10: 'fall', 11: 'fall', 12: 'winter' },
  arid:        { 1: 'spring', 2: 'spring', 3: 'spring', 4: 'summer', 5: 'summer', 6: 'summer', 7: 'summer', 8: 'summer', 9: 'summer', 10: 'fall', 11: 'fall', 12: 'fall' },
  tropical:    { 1: 'summer', 2: 'summer', 3: 'summer', 4: 'summer', 5: 'summer', 6: 'summer', 7: 'summer', 8: 'summer', 9: 'summer', 10: 'summer', 11: 'summer', 12: 'summer' },
};

// ---------------------------------------------------------------------------------------------------------------
// Derived data
function maaPower(maa, base, dmgAdd = 0, toughAdd = 0) {
  const ref = CK3.maa_types[base.ref_maa];
  const d = Math.max(1, maa.damage + dmgAdd), t = Math.max(1, maa.toughness + toughAdd);
  return Math.sqrt((d / ref.damage) * (t / ref.toughness));
}
const TIER_K = 2; // +2 Bannerlord tiers per doubling of per-soldier power
function tierFromPower(base, p) { return r2(base.ref_tier + TIER_K * Math.log2(p)); }

const maaTier = {};
for (const [key, m] of Object.entries(CK3.maa_types)) {
  const base = BASE_ROLE[m.base_type];
  if (!base) throw new Error('unknown base type ' + m.base_type);
  if (base.exclude) { maaTier[key] = { base_type: m.base_type, excluded: true, damage: m.damage, toughness: m.toughness, stack: m.stack }; continue; }
  const p = maaPower(m, base);
  const ov = MAA_OVERRIDES[key] || {};
  const e = { base_type: m.base_type, damage: m.damage, toughness: m.toughness, stack: m.stack, power_vs_ref: r2(p), tier_raw: tierFromPower(base, p) };
  e.tier_target = ov.tier_target != null ? ov.tier_target : e.tier_raw;
  e.tier_source = ov.tier_target != null ? 'override' : 'formula';
  const terr = {};
  for (const [tk, b] of Object.entries(m.terrain_bonus || {})) {
    const tr = tierFromPower(base, maaPower(m, base, b.damage || 0, b.toughness || 0));
    const delta = r2(tr - e.tier_raw);
    if (Math.abs(delta) >= 0.1) terr[tk] = delta;
  }
  if (Object.keys(terr).length) e.terrain_tier_delta = terr;
  const win = {};
  for (const [wk, b] of Object.entries(m.winter_bonus || {})) {
    const delta = r2(tierFromPower(base, maaPower(m, base, b.damage || 0, b.toughness || 0)) - e.tier_raw);
    if (Math.abs(delta) >= 0.1) win[wk] = delta;
  }
  if (Object.keys(win).length) e.winter_tier_delta = win;
  maaTier[key] = e;
}

// interpolation (same algorithm as mapping_resolver.js next to this script)
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
  out = out.map((x) => ({ troop: x.troop, weight: r2(x.weight / s) })).sort((a, b) => b.weight - a.weight);
  return out;
}
function flattenLevy(variant, era) {
  const v = LEVY_VARIANTS[variant];
  const t = LEVY_ERA_TIER[era];
  const acc = {};
  for (const part of ['melee', 'ranged', 'mounted']) {
    if (!v[part] || !v.shares[part]) continue;
    for (const x of interpolate(v[part], t)) acc[x.troop] = (acc[x.troop] || 0) + x.weight * v.shares[part];
  }
  return Object.entries(acc).map(([troop, weight]) => ({ troop, weight: r2(weight) })).sort((a, b) => b.weight - a.weight);
}

// culture resolution
const unitGfxKeys = CK3.unit_gfx.slice();
// order for `has_unit_gfx` if-chains: a culture's primary gfx must be tested before any of its secondary gfx
function gfxTestOrder() {
  const before = {}; unitGfxKeys.forEach((g) => (before[g] = new Set()));
  for (const c of Object.values(CK3.cultures)) { const [p, ...rest] = c.unit_gfx; for (const s of rest) before[s].add(p); }
  const order = []; const seen = new Set();
  const visit = (g) => { if (seen.has(g)) return; seen.add(g); for (const p of before[g]) visit(p); order.push(g); };
  // visit secondaries last: we need primaries first, i.e. every p in before[s] precedes s
  unitGfxKeys.forEach(visit);
  return order;
}
function profileFor(heritage, gfx) {
  const pk = heritage + '|' + gfx;
  if (PAIR_PROFILE[pk]) return { profile: PAIR_PROFILE[pk][0], via: 'pair' };
  if (HERITAGE_PROFILE[heritage]) return { profile: HERITAGE_PROFILE[heritage], via: 'heritage' };
  if (GFX_PROFILE[gfx]) return { profile: GFX_PROFILE[gfx], via: 'unit_gfx' };
  return { profile: 'default', via: 'default' };
}
const cultureMap = {}, cultureProfile = {}, cultureVia = {};
for (const [k, c] of Object.entries(CK3.cultures)) {
  const r = profileFor(c.heritage, c.unit_gfx[0]);
  cultureProfile[k] = r.profile; cultureMap[k] = PROFILES[r.profile].bl_culture; cultureVia[k] = r.via;
}
const heritageMap = {}, gfxMap = {};
for (const h of CK3.heritages) heritageMap[h] = PROFILES[HERITAGE_PROFILE[h]].bl_culture;
for (const g of unitGfxKeys) gfxMap[g] = PROFILES[GFX_PROFILE[g]].bl_culture;

// maa_base (spec shape) is filled after `mapping` exists: base type -> bl culture -> [{troop,weight}], i.e. what the
// resolver returns for the base type's typical MaA in a plain (override-free) profile of that Bannerlord culture.
const TYPICAL_MAA = { heavy_infantry: 'armored_footmen', pikemen: 'pikemen_unit', archers: 'bowmen', skirmishers: 'light_footmen', light_cavalry: 'light_horsemen',
  heavy_cavalry: 'armored_horsemen', archer_cavalry: 'horse_archers', camel_cavalry: 'camel_rider', elephant_cavalry: 'war_elephant', gunpowder: 'handgunners',
  nomadic_horde: 'nomadic_riders', peasant_militia: 'pikemen_militia', siege_weapon: 'mangonel' };
const maaBase = {};

// levy outputs
const levy = {}; for (const bc of BL_CULTURES) levy[bc] = flattenLevy(bc, 'culture_era_early_medieval');
const levyByEra = {};
for (const era of ERAS) { levyByEra[era] = {}; for (const v of Object.keys(LEVY_VARIANTS)) levyByEra[era][v] = flattenLevy(v, era); }

// commanders from bl_catalog + Tactics from SandBoxCore skill sets (CustomGame-loaded)
function readTactics() {
  const x = fs.readFileSync(BL_MODULES + '/SandBoxCore/ModuleData/sandboxcore_skill_sets.xml', 'utf8').replace(/<!--[\s\S]*?-->/g, '');
  const out = {}; const re = /<SkillSet\s+id="([^"]+)">([\s\S]*?)<\/SkillSet>/g; let m;
  while ((m = re.exec(x))) { const s = {}; m[2].replace(/id="([A-Za-z]+)"\s+value="(\d+)"/g, (a, k, v) => { s[k] = +v; }); out[m[1]] = s; }
  return out;
}
const SKILLSETS = readTactics();
const commanders = {};
for (const bc of BL_CULTURES) commanders[bc] = { male_mounted: [], male_foot: [], female: [] };
for (const c of BL.commanders) {
  const slot = c.is_female ? 'female' : c.mounted ? 'male_mounted' : 'male_foot';
  const tactics = (SKILLSETS[c.skill_template] || {}).Tactics || 0;
  commanders[c.culture][slot].push({ character: c.id, tactics, mounted: !!c.mounted, armor: c.armor_total });
}

// scenes: validate against CustomBattle XML (comments stripped) and SceneObj folders
function readScenes() {
  const x = fs.readFileSync(BL_MODULES + '/CustomBattle/ModuleData/custom_battle_scenes.xml', 'utf8').replace(/<!--[\s\S]*?-->/g, '');
  const out = {}; const re = /<Scene\b([\s\S]*?)(?:\/>|>([\s\S]*?)<\/Scene>)/g; let m;
  while ((m = re.exec(x))) {
    const a = {}; m[1].replace(/([A-Za-z_]+)="([^"]*)"/g, (s, k, v) => { a[k] = v; });
    const tt = []; (m[2] || '').replace(/TerrainType\s+name="([^"]+)"/g, (s, v) => { tt.push(v); });
    const kind = a.is_siege_map === 'true' ? 'siege' : a.is_village_map === 'true' ? 'village' : a.is_lords_hall_map === 'true' ? 'lords_hall' : 'field';
    const dir = ['SandBoxCore', 'SandBox', 'CustomBattle', 'Native'].find((mod) => fs.existsSync(`${BL_MODULES}/${mod}/SceneObj/${a.id}`));
    out[a.id] = { name: (a.name || '').replace(/\{=[^}]*\}/, ''), terrain: a.terrain, forest_density: a.forest_density || null, terrain_types: tt, kind, scene_module: dir || null };
  }
  return out;
}
const ALL_SCENES = readScenes();
const usedScenes = new Set([...Object.values(TERRAIN_SCENES).flat(), ...COASTAL_SCENES, ...RIVER_SCENES, ...Object.values(CLIMATE_SCENES).flatMap((o) => Object.values(o).flat())]);
// optional enrichment from the scene-system research (bridge/data/bl_scenes.json, built by another task from the scene
// folders): terrain tags such as snow_layer / relief_high and a Calradia culture hint for 37 field scenes.
let BL_SCENES = {};
try {
  const raw = JSON.parse(fs.readFileSync(ROOT + '/bridge/data/bl_scenes.json', 'utf8'));
  for (const x of (Array.isArray(raw) ? raw : Object.values(raw))) if (x && x.id) BL_SCENES[x.id] = x;
} catch (e) { BL_SCENES = {}; }
const scenes = {};
for (const id of [...usedScenes].sort()) {
  const s = ALL_SCENES[id];
  if (!s) throw new Error('scene not in custom_battle_scenes.xml: ' + id);
  const ext = BL_SCENES[id] || {};
  scenes[id] = { name: s.name, bl_terrain: s.terrain, forest_density: s.forest_density, terrain_types: s.terrain_types, kind: s.kind, scene_module: s.scene_module,
    used_for: Object.entries(TERRAIN_SCENES).filter(([, v]) => v.includes(id)).map(([k]) => k),
    terrain_tags: (ext.terrain_tags || []).filter((t) => !/^authored_/.test(t)), culture_hint: ext.culture || null };
}

// ---------------------------------------------------------------------------------------------------------------
const mapping = {
  meta: {
    generated: new Date().toISOString(),
    generator: SELF,
    reference_resolver: `${path.posix.join(path.posix.dirname(SELF), 'mapping_resolver.js')} (port this to C# in bridge.exe)`,
    ck3: CK3.meta.version_hint, bannerlord: BL.meta.bannerlord_version,
    inputs: ['bridge/data/ck3_catalog.json', 'bridge/data/bl_catalog.json', 'Modules/CustomBattle/ModuleData/custom_battle_scenes.xml', 'Modules/SandBoxCore/ModuleData/sandboxcore_skill_sets.xml'],
    confidence: 'Troop/scene/culture ids are VERIFIED to exist (validator). Culture->profile choices, ladders, tier rule, scene classification, scaling and casualty formulas are DESIGN (INFERRED).',
    culture_resolution_order: [
      '1. culture_profile_map[ck3 culture key] when the bridge knows the key (all 244 template cultures)',
      '2. heritage_gfx_overrides["<heritage>|<primary unit_gfx>"]',
      '3. heritage_profile_map[heritage]  (hybrid/divergent cultures keep a parent heritage)',
      '4. unit_gfx_profile_map[primary unit_gfx]',
      '5. default_profile',
    ],
    maa_resolution_order: [
      '1. maa_tier[key].excluded -> spawn nothing (siege weapons)',
      '2. ov = maa_overrides[key]; tier = ov.tier_target ?? maa_tier[key].tier_target (+ maa_tier[key].terrain_tier_delta[terrain]) ; unknown key -> base_roles[base_type].ref_tier',
      '3. ladder = shared_ladders[ov.ladder_ref] || maa_ladders[role][ov.force_culture || base_roles[bt].force_culture] || profile.role_overrides[role] (era-gated) || maa_ladders[role][profile.bl_culture]',
      '4. troops = interpolate(ladder, tier); agent_multiplier = clamp(2^((tier - ladder_max)/2), 1, 2) above the top rung, clamp(2^((tier - ladder_min)/2), 0.5, 1) below the bottom rung',
    ],
  },
  bl_cultures: BL_CULTURES,
  eras: ERAS,
  default_culture: PROFILES.default.bl_culture,
  default_profile: 'default',
  culture_map: cultureMap,
  culture_profile_map: cultureProfile,
  culture_resolved_via: cultureVia,
  heritage_map: heritageMap,
  heritage_profile_map: HERITAGE_PROFILE,
  unit_gfx_map: gfxMap,
  unit_gfx_profile_map: GFX_PROFILE,
  heritage_gfx_overrides: Object.fromEntries(Object.entries(PAIR_PROFILE).map(([k, [p, why]]) => [k, { profile: p, bl_culture: PROFILES[p].bl_culture, why }])),
  unit_gfx_test_order: gfxTestOrder(),
  profiles: PROFILES,
  levy_era_tier: LEVY_ERA_TIER,
  levy: levy,
  levy_by_era: levyByEra,
  levy_variants: LEVY_VARIANTS,
  levy_rules: {
    variant: 'profiles[p].levy_variant; if the army owner has government_has_flag = government_is_nomadic and the profile bl_culture is khuzait, use steppe_nomadic',
    tier: 'levy_era_tier[army owner culture era]; same tier scale as MaA (10/10 levy vs armored_footmen 32/22 = tier 1.2)',
    note: 'CK3 levies are generic 10/10 soldiers with no culture variant; the variant only changes the look and the melee/ranged/mounted shares.',
  },
  tier_rule: {
    formula: 'tier = base_roles[base].ref_tier + 2 * log2( sqrt( (dmg/ref_dmg) * (tough/ref_tough) ) )',
    k_tiers_per_power_doubling: TIER_K,
    note: 'per-soldier CK3 damage/toughness relative to the regular reference MaA of the same base type; terrain/winter bonuses give tier deltas (applied on top of overrides too)',
    terrain_delta_clamp: [-1.5, 1.0],
    terrain_delta_rule: 'tier += clamp(terrain_tier_delta[terrain] + winter_tier_delta[winter], -1.5, +1.0); a large malus (e.g. light cavalry in wetlands) then also lowers agent_multiplier below the bottom rung',
    agent_multiplier: 'tier above the ladder top -> extra agents 2^((tier-top)/2) capped 2.0; tier below the bottom rung -> fewer agents 2^((tier-bottom)/2) floored 0.5',
  },
  base_roles: BASE_ROLE,
  maa_ladders: LADDERS,
  shared_ladders: SHARED_LADDERS,
  maa_base: maaBase,
  maa_tier: maaTier,
  maa_overrides: MAA_OVERRIDES,
  restricted_troops: RESTRICTED_TROOPS,
  knights: KNIGHTS,
  knight_rules: {
    agents: 'each CK3 knight = 1 named hero agent (always, never scaled) + retinue agents',
    retinue: 'retinue = min(10, round(max(0, (2.236 * prowess * scale * (1 + knight_effectiveness_mult) - 4) / 4)))  # knight power in levy units = sqrt(50p/10 * 10p/10) = 2.236p; one tier-5/6 agent ~ 4 levy agents',
    troop_source: 'profile.knights (era-gated) else knights[bl_culture]; retinue uses the same list',
    prowess_phase1: 'hero agent health multiplier = clamp(1 + prowess/25, 1, 3) applied at spawn (Agent.Health/HealthLimit)',
    prowess_phase2: 'CK3Bridge NPCCharacters ck3b_knight_<bl_culture>_p<band> with weapon/riding/athletics skills = clamp(60 + 8*prowess, 60, 330), bands 0-4,5-9,10-14,15-19,20-29,30+',
    fate: {
      killed_probability_when_downed: 'clamp(0.30 - prowess/100, 0.05, 0.30) via CK3Bridge IAgentStateDecider',
      killed: 'death = { death_reason = death_battle killer = <enemy side_commander> }',
      downed_not_killed_side_lost: 'capture with p = 0.35: <enemy side_primary_participant> = { imprison = { target = <knight> type = dungeon } } (verify imprison type keys); otherwise wounded',
      downed_not_killed: 'add_trait = wounded_1 (60%) / wounded_2 (30%) / wounded_3 (10%); 5% maimed',
      not_downed: 'nothing',
    },
  },
  commanders: commanders,
  commander_rules: {
    body: 'commanders[bl_culture][male_mounted|male_foot|female]; a female CK3 commander keeps her gender first: no female body in the culture -> female body of female_fallback[bl_culture]; an empty male slot -> the other male slot of the same culture',
    female_fallback: { sturgia: 'empire', aserai: 'empire' },
    must_add_as_character: 'CustomBattleCombatant.GetTacticsSkillAmount() takes the max Tactics over _characters, so the general must be AddCharacter()ed as well as SetGeneral() (CustomBattleHelper does the same)',
    martial_to_tactics: 'Bannerlord field-battle AI unlocks tactics by side max Tactics: <20 Charge only; >=20 FullScaleAttack + DefensiveEngagement/DefensiveLine (def) or RangedHarrassmentOffensive (att); >=50 FrontalCavalryCharge + DefensiveRing/HoldChokePoint (def) or CoordinatedRetreat (att). Map CK3 martial m -> Tactics = clamp(10*m, 0, 300): m<=1 charge-only, m 2-4 basic, m>=5 full set. Needs phase-2 custom commander characters; stock bodies have Tactics 15 (commander_12/24) to 200.',
    advantage: 'adv = martial + terrain defender_advantage (defender only) + 5 for the defender in harsh winter (winter_harsh_modifier: defender_winter_advantage = 5) + commander-trait advantage for the terrain/winter; delta = adv_A - adv_B; the side ahead deals outgoing damage x (1 + 0.025*|delta|) capped at +30% (CK3 default game rule is +5%/point; halved because Bannerlord melee already snowballs). Implement in a CK3Bridge AgentApplyDamageModel registered in SubModule.OnGameStart (CustomGame calls GameManager.OnGameStart after its own models).',
    morale: 'initial morale + |delta| (max +15) for the side ahead',
    traits: { aggressive_attacker: 'outgoing damage +5% when attacker', unyielding_defender: 'incoming damage -5% when defender', reckless: 'outgoing +8%, incoming +8%', cautious_leader: 'incoming -5%, outgoing -3%', flexible_leader: 'ignore enemy terrain advantage 20%', forest_fighter: '+2 advantage in forest/taiga', rough_terrain_expert: '+2 advantage in hills/mountains', open_terrain_expert: '+2 advantage in plains/farmlands/steppe/drylands', desert_warrior: '+2 advantage in desert/drylands/oasis/desert_mountains', jungle_stalker: '+2 advantage in jungle' },
  },
  scaling: {
    max_total_agents_default: 1500,
    max_total_agents_rule: 'both sides incl. reinforcements; default 1.5 x BannerlordConfig.GetRealBattleSize() (battle sizes 200..1000 on the field), user-configurable',
    scale: 's = min(1, (max_total_agents - hero_agents) / (field_soldiers_attacker + field_soldiers_defender))  # same s for both sides keeps the side ratio',
    field_soldiers: 'levies + every MaA regiment maa_current_troops_count except siege_weapon (fights_in_main_phase = no)',
    regiment_agents: 'agents_i = max(1, round(n_i * s * agent_multiplier_i)) for n_i > 0; levy pool absorbs rounding (largest-remainder over the side)',
    min_agents_per_regiment: 1,
    ratio_guard: 'after minimums, if a side ratio differs from the CK3 soldier ratio by >5%, shrink that side\'s levy pool (never below 0) until within 5%',
    hero_agents: 'commanders (1 each, every army commander on the side) + knights (1 each) + knight retinues; not scaled',
    group_tracking: 'every agent must carry its CK3 group id (regiment / levy pool / knight): CK3Bridge supplies its own IMissionTroopSupplier/IAgentOriginBase; fallback = split losses of a shared troop id proportionally to spawned counts',
  },
  casualties: {
    fatal_ratio_modifiers: {
      terrain: Object.fromEntries(CK3.terrains.filter((t) => (t.attacker_modifier || t.defender_modifier)).map((t) => [t.key, { attacker: t.attacker_modifier || {}, defender: t.defender_modifier || {} }])),
      rule: 'fatal_ratio = 0.3 x (1 + side terrain hard_casualty_modifier) x (1.2 in harsh winter); pursuit q x (1 + loser retreat_losses) (wetlands +0.25 both sides, desert_mountains defender -0.3). Values VERIFIED from common/terrain_types/00_terrains.txt via ck3_catalog; applying them this way is INFERRED.',
    },
    ck3_model: 'Routed casualties return to the army after battle; fatal casualties are permanent (game_concepts_l_english.yml:972-975). Main phase converts BASE_RATIO_CASUALTIES_CONVERSION = 0.3 of casualties to fatal; the pursuit (aftermath) turns routed into fatal: 5% of routed toughness (BASE_TOUGHNESS_TO_PURSUIT) + 0.5 x pursuit (PURSUIT_STAT_TO_PURSUIT_DAMAGE) minus screen, at least 1% (MINIMUM_PURSUIT_DAMAGE).',
    bl_default: 'CustomGame registers DefaultAgentDecideKilledOrUnconsciousModel which returns death probability 1.0, so every downed agent is Killed unless a mission behaviour implements IAgentStateDecider',
    recommended_bl_model: 'CK3Bridge IAgentStateDecider: regular agents Killed with p = 0.3 (= CK3 main-phase conversion), heroes per knight_rules.fate',
    per_group: [
      'f_down = (killed + unconscious) / agents_spawned ; casualties = round(f_down * soldiers)',
      'fatal_main = round(casualties * 0.3)   (or round(killed/agents * soldiers) when the decider is installed)',
      'winner: fatal = fatal_main ; routed = casualties - fatal_main return to the army',
      'loser: routed_pool = casualties - fatal_main + round(fled_agents/agents * soldiers) ; q = clamp(0.05 + 0.5 * max(0, P_win - S_lose) / T_routed, 0.01, 1.0) ; fatal = fatal_main + round(routed_pool * q)',
      'loser annihilated (no agent fled or standing at mission end): fatal = casualties (early-phase defeat rule, ck3.paradoxwikis.com/Warfare)',
      'P_win = sum over winner surviving MaA soldiers of pursuit (levies 0) ; S_lose = sum over loser surviving MaA soldiers of screen ; T_routed = sum routed soldiers x toughness (levy 10)',
    ],
    writeback: {
      maa: 'regiment = { change_maa_troops_count = -<fatal> }  (effects.log: Supported Scopes: regiment)',
      levies: 'army = { deplete_army_by_percentage = <fatal_levy / army_size> } THEN reset every MaA regiment of that army to its exact target with change_maa_troops_count (handles both possible deplete semantics; verify with a probe first)',
      winner: 'combat_side = { force_win = yes } on the Bannerlord winner + loser side { set_skip_pursuit = yes } so CK3 does not add its own pursuit on top of ours; alternative set_winner = yes keeps CK3\'s pursuit (its internal routed pool is ~0 if CK3 was paused at on_combat_start)',
      summary: 'combat_side = { battle_event = { key = ck3bl_fought_in_bannerlord left_portrait = <commander> right_portrait = <enemy commander> } } to show a line in the CK3 battle summary',
    },
  },
  terrain_scenes: TERRAIN_SCENES,
  terrain_scene_rules: {
    pick: 'seeded random (seed = CK3 combat location province id + date) from the pool',
    pool: 'climate_scene_overrides[climate][terrain] if present else terrain_scenes[terrain]; is_coastal province and terrain in plains/farmlands/drylands/hills -> 30% chance coastal_scenes; is_riverside_province -> 30% chance river_scenes',
    fallback_scene: 'battle_terrain_029 (CustomBattleData.CoreContentDefaultSceneName)',
    winter_preference: 'season winter -> prefer pool scenes whose terrain_tags contain snow_layer (scene has a snow material layer) when the pool has any',
    culture_preference: '50% chance to restrict the pool to scenes whose culture_hint equals the Bannerlord culture of the location holder (or defender) when any exist; culture_hint/terrain_tags come from bridge/data/bl_scenes.json (name-matched Calradia settlement hints, INFERRED)',
    narrow_terrain_note: 'combat_width < 1 (mountains/desert_mountains 0.5, wetlands 0.6, jungle 0.7) is represented by scene choice only; defender_advantage feeds commander_rules.advantage',
  },
  coastal_scenes: COASTAL_SCENES,
  river_scenes: RIVER_SCENES,
  climate_regions: CLIMATE_REGIONS.map(([region, climate]) => ({ region, climate })),
  climate_default: 'temperate',
  terrain_default_climate: { taiga: 'cold', steppe: 'steppe', desert: 'arid', desert_mountains: 'arid', oasis: 'arid', drylands: 'arid', jungle: 'tropical' },
  climate_scene_overrides: CLIMATE_SCENES,
  scenes: scenes,
  season_by_month: SEASON_BY_MONTH,
  season_by_climate: SEASON_BY_CLIMATE,
  season_ids: { spring: 0, summer: 1, fall: 2, winter: 3, source: 'BannerlordMissions.CreateAtmosphereInfoForMission' },
  winter_state: {
    source: 'province `has_province_modifier = winter_<x>_modifier` (game/common/modifiers/00_province_modifiers.txt:138-155; logs/triggers.log:10810). The engine swaps these modifiers as winter severity changes, so they are the authoritative winter signal; month + climate is only the fallback.',
    chain: ['winter_harsh_modifier', 'winter_normal_modifier', 'winter_mild_modifier', 'winter_none_modifier'],
    season: { winter_harsh_modifier: 'winter', winter_normal_modifier: 'winter', winter_mild_modifier: 'winter_if_cold_climate_else_fall', winter_none_modifier: 'month_table_but_winter_becomes_fall' },
    maa_winter_key: { winter_normal_modifier: 'normal_winter', winter_harsh_modifier: 'harsh_winter' },
    harsh_effects: { defender_advantage: 5, fatal_ratio_mult: 1.2, note: 'winter_harsh_modifier carries defender_winter_advantage = 5 and hard_casualty_winter = 0.2' },
  },
  time_of_day: { values: { 6: 'Morning', 12: 'Noon', 15: 'Afternoon', 18: 'Evening', 22: 'Night' }, weights: { 6: 0.3, 12: 0.3, 15: 0.3, 18: 0.1, 22: 0 }, note: 'CK3 has no time of day; only 6/12/15/18/22 have atmospheres (CreateAtmosphereInfoForMission)' },
  ck3_emit: {
    note: 'ordered key lists for the generated CK3 if/else_if chains that fill request.json (CK3 script cannot print keys)',
    heritage_chain: CK3.heritages.map((h) => 'has_cultural_pillar = ' + h),
    unit_gfx_chain: gfxTestOrder().map((g) => 'has_unit_gfx = ' + g),
    era_chain: [...ERAS].reverse().map((e) => 'has_cultural_era_or_later = ' + e),
    terrain_chain: CK3.terrains.map((t) => 'terrain = ' + t.key),
    region_chain: CLIMATE_REGIONS.map(([r]) => 'geographical_region = ' + r),
    winter_chain: ['winter_harsh_modifier', 'winter_normal_modifier', 'winter_mild_modifier', 'winter_none_modifier'].map((w) => 'has_province_modifier = ' + w),
    commander_traits: ['logistician', 'military_engineer', 'aggressive_attacker', 'unyielding_defender', 'forder', 'flexible_leader', 'desert_warrior', 'jungle_stalker', 'reaver', 'reckless', 'holy_warrior', 'open_terrain_expert', 'rough_terrain_expert', 'forest_fighter', 'cautious_leader', 'organizer'],
    nomadic_flag: 'army_owner = { government_has_flag = government_is_nomadic }',
  },
  custom_troop_wishlist: [
    { id: 'ck3b_knight_<bl_culture>_p<band>', why: 'prowess-scaled knights (skills) instead of a health multiplier' },
    { id: 'ck3b_commander_<bl_culture>_<m|f>_m<band>', why: 'martial-scaled Tactics/Leadership so commander quality changes Bannerlord AI tactics' },
    { id: 'ck3b_camel_<bl_culture>', why: 'culture-looking camel riders (war_camel + camel_saddle with the culture kit) instead of Jawwal/Ghilman for everyone' },
    { id: 'ck3b_horse_archer_<vlandia|battania|sturgia>', why: 'these cultures lack horse archers; today neutral Outriders / Khuzait troops stand in' },
    { id: 'ck3b_crossbow_<aserai|sturgia|battania|khuzait>', why: 'these cultures lack crossbowmen; today neutral Hired Crossbows stand in' },
    { id: 'ck3b_<profile>_infantry', why: 'body templates for profiles without a matching faction (West/East African, Indian, SE Asian, Chinese, Japanese)' },
    { id: 'ck3b_female_<role>', why: 'martial_custom_equal/female_only cultures' },
    { id: 'ck3b_peasant_rebel / ck3b_bandit', why: 'bandits.xml is Campaign-only; peasant armies and event troops need CustomGame-loaded stand-ins' },
  ],
};

// fill maa_base with the reference resolver (plain profiles, early medieval, no terrain)
const resolver = require('./mapping_resolver.js');
{
  const tmp = { ...mapping, profiles: { ...mapping.profiles } };
  for (const bc of BL_CULTURES) tmp.profiles['__plain_' + bc] = { bl_culture: bc, levy_variant: bc };
  for (const bt of Object.keys(BASE_ROLE)) {
    maaBase[bt] = {};
    for (const bc of BL_CULTURES) {
      const r = resolver.resolveMaa(tmp, TYPICAL_MAA[bt], { profile: '__plain_' + bc, era: 'culture_era_early_medieval' });
      maaBase[bt][bc] = r.excluded ? [] : r.troops;
    }
  }
  // convenience preview inside each override (authoritative path is still the profile-aware resolver)
  for (const [k, ov] of Object.entries(MAA_OVERRIDES)) {
    const cultureFree = ov.ladder_ref || ov.force_culture;
    const res = (bc) => resolver.resolveMaa(tmp, k, { profile: '__plain_' + bc, era: 'culture_era_high_medieval' });
    if (cultureFree) { const r = res('vlandia'); ov.resolved = { troops: r.troops, tier: r.tier, agent_multiplier: r.agent_multiplier }; }
    else { ov.resolved = { by_culture: Object.fromEntries(BL_CULTURES.map((bc) => { const r = res(bc); return [bc, r.troops]; })), tier: res('vlandia').tier }; }
  }
  mapping.maa_base_note ='maa_base[base][bl_culture] = resolver output for the typical MaA of the base type (' + Object.entries(TYPICAL_MAA).map(([k, v]) => k + ':' + v).join(', ') + ') in a plain profile of that culture; siege_weapon is empty by design (excluded).';
}

fs.writeFileSync(OUT, JSON.stringify(mapping, null, 1));
console.log('wrote', OUT, (fs.statSync(OUT).size / 1024).toFixed(1) + ' KB');
console.log('cultures', Object.keys(cultureMap).length, 'via', JSON.stringify(Object.values(cultureVia).reduce((a, v) => ((a[v] = (a[v] || 0) + 1), a), {})));
console.log('maa', Object.keys(maaTier).length, 'scenes', Object.keys(scenes).length);
