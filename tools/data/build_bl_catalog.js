// build_bl_catalog.js — derive a Bannerlord v1.4.8 troop/culture/item catalog from the installed game XML.
// Usage: node build_bl_catalog.js [bannerlordRoot] [outJson]   (root default: env BL_DIR, else found in the Steam libraries)
// Reads only. Emits derived ids/numbers (no game assets are copied).
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
const OUT_JSON = process.argv[3] || path.join(DATA_DIR, 'bl_catalog.json');
const MODS = path.join(BL, 'Modules').replace(/\\/g, '/');
const MODULE_FOLDERS = ['Native', 'SandBoxCore', 'SandBox', 'CustomBattle', 'StoryMode', 'Multiplayer', 'BirthAndDeath', 'FastMode'];

// ---------------------------------------------------------------- tiny XML parser (elements + attrs + line numbers)
function decodeEnt(s) {
  return s.replace(/&(amp|lt|gt|quot|apos|#x[0-9a-fA-F]+|#\d+);/g, (m, e) => {
    if (e === 'amp') return '&'; if (e === 'lt') return '<'; if (e === 'gt') return '>';
    if (e === 'quot') return '"'; if (e === 'apos') return "'";
    if (e[1] === 'x') return String.fromCodePoint(parseInt(e.slice(2), 16));
    return String.fromCodePoint(parseInt(e.slice(1), 10));
  });
}
function parseXml(text, file) {
  if (text.charCodeAt(0) === 0xFEFF) text = text.slice(1);
  const root = { name: '#doc', attrs: {}, children: [], line: 0, file };
  const stack = [root];
  const re = /<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>|<\?[\s\S]*?\?>|<!DOCTYPE[^>]*>|<(\/?)([A-Za-z_][\w:.\-]*)((?:\s+[\w:.\-]+\s*=\s*(?:"[^"]*"|'[^']*'))*)\s*(\/?)>/g;
  let m, line = 1, last = 0;
  while ((m = re.exec(text))) {
    for (let i = last; i < m.index; i++) if (text.charCodeAt(i) === 10) line++;
    last = m.index;
    if (!m[2]) continue; // comment / pi / cdata
    if (m[1] === '/') {
      const top = stack.pop();
      if (!top || top.name !== m[2]) throw new Error(`${file}:${line} mismatched </${m[2]}> (open ${top && top.name})`);
      continue;
    }
    const attrs = {};
    const ar = /([\w:.\-]+)\s*=\s*(?:"([^"]*)"|'([^']*)')/g; let a;
    while ((a = ar.exec(m[3]))) attrs[a[1]] = decodeEnt(a[2] !== undefined ? a[2] : a[3]);
    const el = { name: m[2], attrs, children: [], line, file };
    stack[stack.length - 1].children.push(el);
    if (!m[4]) stack.push(el);
  }
  return root;
}
const kids = (el, ...names) => el.children.filter(c => names.length === 0 || names.includes(c.name));
const kid = (el, ...names) => el.children.find(c => names.includes(c.name));
function* walk(el) { yield el; for (const c of el.children) yield* walk(c); }
const ref = v => (v == null ? null : (v.includes('.') ? v.split('.').slice(1).join('.') : v));
const cleanName = s => (s || '').replace(/^\{=[^}]*\}/, '').replace(/\{@Plural\}[\s\S]*?\{\\@\}/g, '').trim();
const rel = f => f.replace(/\\/g, '/').replace(MODS + '/', 'Modules/');
const r2 = x => Math.round(x * 100) / 100;
const bankersRound = x => { const f = Math.floor(x), d = x - f; if (Math.abs(d - 0.5) < 1e-9) return f % 2 === 0 ? f : f + 1; return Math.round(x); };

// ---------------------------------------------------------------- SubModule.xml → which XML files load in which game types
const xmlNodes = []; // {module, id, path, gameTypes[], files[]}
for (const mod of MODULE_FOLDERS) {
  const sm = path.join(MODS, mod, 'SubModule.xml');
  if (!fs.existsSync(sm)) continue;
  const doc = parseXml(fs.readFileSync(sm, 'utf8'), sm);
  for (const n of walk(doc)) {
    if (n.name !== 'XmlNode') continue;
    const xn = kid(n, 'XmlName'); if (!xn) continue;
    const igt = kid(n, 'IncludedGameTypes');
    const gameTypes = igt ? kids(igt, 'GameType').map(g => g.attrs.value) : [];
    const base = path.join(MODS, mod, 'ModuleData', xn.attrs.path).replace(/\\/g, '/');
    let files = [];
    if (fs.existsSync(base + '.xml')) files = [base + '.xml'];
    else if (fs.existsSync(base) && fs.statSync(base).isDirectory()) files = fs.readdirSync(base).filter(f => f.endsWith('.xml')).sort().map(f => base + '/' + f);
    xmlNodes.push({ module: mod, id: xn.attrs.id, path: xn.attrs.path, gameTypes, files, line: xn.line, submodule: rel(sm) });
  }
}
const loadedIn = gts => (gts.length ? gts : ['ALL']);
const inCustomGame = gts => gts.length === 0 || gts.includes('CustomGame');
function nodesFor(id) { return xmlNodes.filter(x => x.id === id); }

// ---------------------------------------------------------------- crafting data (templates, weapon descriptions, pieces)
const nodeFiles = id => nodesFor(id).filter(n => inCustomGame(n.gameTypes) || n.gameTypes.includes('Campaign')).flatMap(n => n.files);
const weaponDescs = {};
for (const f of nodeFiles('WeaponDescriptions')) {
  for (const wd of walk(parseXml(fs.readFileSync(f, 'utf8'), f))) {
    if (wd.name !== 'WeaponDescription') continue;
    const flags = [];
    const wf = kid(wd, 'WeaponFlags'); if (wf) for (const x of kids(wf, 'WeaponFlag')) flags.push(x.attrs.value);
    const ap = kid(wd, 'AvailablePieces');
    weaponDescs[wd.attrs.id] = { id: wd.attrs.id, cls: wd.attrs.weapon_class, features: wd.attrs.item_usage_features || '', flags, pieces: new Set(ap ? kids(ap, 'AvailablePiece').map(p => p.attrs.id) : []) };
  }
}
const craftTemplates = {};
for (const f of nodeFiles('CraftingTemplates')) {
  for (const ct of walk(parseXml(fs.readFileSync(f, 'utf8'), f))) {
    if (ct.name !== 'CraftingTemplate') continue;
    const wds = kid(ct, 'WeaponDescriptions');
    craftTemplates[ct.attrs.id] = { itemType: ct.attrs.item_type, descs: wds ? kids(wds, 'WeaponDescription').map(w => w.attrs.id) : [] };
  }
}
const craftPieces = {};
for (const n of nodesFor('CraftingPieces').filter(n => inCustomGame(n.gameTypes))) {
  for (const f of n.files) for (const p of walk(parseXml(fs.readFileSync(f, 'utf8'), f))) {
    if (p.name === 'CraftingPiece') craftPieces[p.attrs.id] = { tier: +p.attrs.tier || 0, type: p.attrs.piece_type, culture: ref(p.attrs.culture) };
  }
}

// ---------------------------------------------------------------- items
const items = {}; const itemDupes = [];
const TIER_EVIDENCE = 'TaleWorlds.Core.DefaultItemValueModel.CalculateTier (TaleWorlds.Core.dll v1.4.8)';
function armorTierf(type, a) {
  let n = 1.2 * a.head + a.body + a.leg + a.arm;
  if (type === 'LegArmor') n *= 1.6; else if (type === 'HandArmor') n *= 1.7; else if (type === 'HeadArmor') n *= 1.2; else if (type === 'Cape') n *= 1.8;
  return n * 0.1 - 0.4;
}
function parseItem(el, file, gameTypes) {
  const at = el.attrs; const id = at.id;
  const it = { id, name: cleanName(at.name), culture: ref(at.culture) || null, file: rel(file), line: el.line, game_types: loadedIn(gameTypes) };
  const flagsEl = kid(el, 'Flags'); it.flags = flagsEl ? Object.keys(flagsEl.attrs).filter(k => flagsEl.attrs[k] === 'true') : [];
  if (el.name === 'CraftedItem') {
    const tmpl = at.crafting_template; const t = craftTemplates[tmpl] || { itemType: '?', descs: [] };
    const pcs = []; const pe = kid(el, 'Pieces'); if (pe) for (const p of kids(pe, 'Piece')) pcs.push(p.attrs.id);
    const usages = t.descs.filter(d => weaponDescs[d] && pcs.every(p => weaponDescs[d].pieces.has(p)));
    it.crafted = true; it.template = tmpl; it.type = t.itemType; it.pieces = pcs;
    it.usages = usages; // in template order; first = primary
    it.classes = [...new Set(usages.map(u => weaponDescs[u].cls))];
    it.weapon_flags = [...new Set(usages.flatMap(u => weaponDescs[u].flags))];
    it.features = [...new Set(usages.flatMap(u => weaponDescs[u].features.split(':')).filter(Boolean))];
    const sfx = id.match(/_t(\d)(?:_|$)/);
    const ptiers = pcs.map(p => craftPieces[p] && craftPieces[p].tier).filter(x => x);
    if (at.tier_override) { it.tierf = +at.tier_override; it.tier_src = 'tier_override'; }
    else if (sfx) { it.tierf = +sfx[1]; it.tier_src = 'id_suffix'; }
    else if (ptiers.length) { it.tierf = r2(ptiers.reduce((a, b) => a + b, 0) / ptiers.length); it.tier_src = 'piece_tier_avg'; }
    else { it.tierf = null; it.tier_src = 'unknown'; }
    return it;
  }
  it.type = at.Type; it.subtype = at.subtype || null; it.weight = at.weight != null ? +at.weight : null;
  const comp = kid(el, 'ItemComponent');
  const armor = comp && kid(comp, 'Armor'); const horse = comp && kid(comp, 'Horse');
  const weapons = comp ? kids(comp, 'Weapon') : [];
  let tierf = null, src = 'formula';
  if (armor) {
    const a = { head: +armor.attrs.head_armor || 0, body: +armor.attrs.body_armor || 0, leg: +armor.attrs.leg_armor || 0, arm: +armor.attrs.arm_armor || 0 };
    it.armor = a; it.material = armor.attrs.material_type || null;
    if (armor.attrs.family_type) it.family_type = +armor.attrs.family_type;
    for (const k of ['maneuver_bonus', 'speed_bonus', 'charge_bonus']) if (armor.attrs[k]) it[k] = +armor.attrs[k];
    tierf = armorTierf(it.type, a);
  } else if (horse) {
    const h = horse.attrs;
    it.horse = { monster: ref(h.monster), speed: +h.speed || 0, maneuver: +h.maneuver || 0, charge: +h.charge_damage || 0, extra_health: +h.extra_health || 0, mountable: h.is_mountable === 'true', pack: h.is_pack_animal === 'true' };
    tierf = it.horse.speed * 0.12 + it.horse.maneuver * 0.07 + it.horse.extra_health * 0.01 + it.horse.charge * 0.15 - 11.5;
  } else if (weapons.length) {
    it.weapons = weapons.map(w => {
      const wf = kid(w, 'WeaponFlags');
      return { cls: w.attrs.weapon_class, usage: w.attrs.item_usage || '', thrust: +w.attrs.thrust_damage || 0, swing: +w.attrs.swing_damage || 0, speed: +w.attrs.missile_speed || 0, acc: w.attrs.accuracy != null ? +w.attrs.accuracy : 100, len: +w.attrs.weapon_length || 0, body_armor: +w.attrs.body_armor || 0, thrust_speed: +w.attrs.thrust_speed || 0, max: +(w.attrs.ammo_limit || w.attrs.stack_amount || w.attrs.hit_points || 0), flags: wf ? Object.keys(wf.attrs).filter(k => wf.attrs[k] === 'true') : [] };
    });
    it.classes = [...new Set(it.weapons.map(w => w.cls))];
    it.weapon_flags = [...new Set(it.weapons.flatMap(w => w.flags))];
    const w0 = it.weapons[0];
    if (['Bow', 'Crossbow', 'Sling'].includes(it.type)) {
      let b = 0; if (it.type === 'Crossbow') b -= 1.5; if (w0.usage.includes('light')) b += 1.25;
      if (!w0.usage.includes('long_bow') && !w0.flags.includes('CantReloadOnHorseback')) b += 0.5;
      tierf = w0.thrust * 0.1 + w0.speed * 0.02 + w0.acc * 0.05 - 9.25 + b;
    } else if (['Arrows', 'Bolts', 'SlingStones'].includes(it.type)) {
      tierf = w0.thrust + Math.max(0, w0.max - 20) * 0.1;
    } else if (it.type === 'Shield') {
      tierf = (Math.pow(w0.max, 1.22) + 3 * w0.body_armor + w0.thrust_speed) / (6 + Math.pow(it.weight || 0, 1.11)) * 0.04 - 2;
    } else { tierf = null; src = 'not_computed(non-crafted melee/thrown: model returns 0)'; }
  } else if (it.type === 'Banner') { src = 'banner'; }
  else { src = 'no_component'; }
  if (at.tier_override) { tierf = +at.tier_override; src = 'tier_override'; }
  it.tierf = tierf == null ? null : r2(tierf); it.tier_src = src;
  return it;
}
for (const n of nodesFor('Items')) {
  for (const f of n.files) {
    const doc = parseXml(fs.readFileSync(f, 'utf8'), f);
    for (const el of walk(doc)) {
      if (el.name !== 'Item' && el.name !== 'CraftedItem') continue;
      if (!el.attrs.id) continue;
      const it = parseItem(el, f, n.gameTypes);
      if (items[it.id]) { itemDupes.push({ id: it.id, first: items[it.id].file, second: it.file }); if (!inCustomGame(n.gameTypes)) continue; if (inCustomGame(items[it.id].game_types[0] === 'ALL' ? [] : items[it.id].game_types)) continue; }
      items[it.id] = it;
    }
  }
}
for (const it of Object.values(items)) {
  it.tier = it.tierf == null ? null : Math.min(6, Math.max(0, bankersRound(it.tierf)));
}
const itemInCG = it => it && (it.game_types.includes('ALL') || it.game_types.includes('CustomGame'));

// ---------------------------------------------------------------- equipment rosters (global sets)
const rosters = {};
for (const n of nodesFor('EquipmentRosters')) {
  for (const f of n.files) {
    const doc = parseXml(fs.readFileSync(f, 'utf8'), f);
    for (const r of walk(doc)) {
      if (r.name !== 'EquipmentRoster' || !r.attrs.id) continue;
      const sets = kids(r, 'EquipmentSet').map(s => ({ type: s.attrs.equipmentType || (s.attrs.civilian === 'true' ? 'Civilian' : 'Battle'), slots: Object.fromEntries(kids(s, 'Equipment', 'equipment').map(e => [e.attrs.slot, ref(e.attrs.id)])) }));
      const fl = kid(r, 'Flags');
      rosters[r.attrs.id] = { id: r.attrs.id, culture: ref(r.attrs.culture), sets, flags: fl ? Object.keys(fl.attrs).filter(k => fl.attrs[k] === 'true') : [], file: rel(f), line: r.line, game_types: loadedIn(n.gameTypes) };
    }
  }
}

// ---------------------------------------------------------------- characters
const SLOT_ALIASES = { Item0: 'Weapon0', Item1: 'Weapon1', Item2: 'Weapon2', Item3: 'Weapon3' };
const chars = {}; const charDupes = [];
for (const n of nodesFor('NPCCharacters')) {
  for (const f of n.files) {
    const doc = parseXml(fs.readFileSync(f, 'utf8'), f);
    for (const c of walk(doc)) {
      if (c.name !== 'NPCCharacter' || !c.attrs.id) continue;
      const at = c.attrs;
      const ch = {
        id: at.id, name: cleanName(at.name), culture: ref(at.culture), occupation: at.occupation || null,
        level: at.level != null ? +at.level : 1, level_explicit: at.level != null, default_group: at.default_group || null,
        is_hero: at.is_hero === 'true', is_female: at.is_female === 'true', is_obsolete: at.is_obsolete === 'true', is_basic_troop: at.is_basic_troop === 'true',
        upgrade_requires: ref(at.upgrade_requires) || null, skill_template: ref(at.skill_template) || null,
        file: rel(f), line: c.line, module: n.module, game_types: loadedIn(n.gameTypes), age: at.age ? +at.age : null,
      };
      const ut = kid(c, 'upgrade_targets'); ch.upgrade_targets = ut ? kids(ut, 'upgrade_target').map(u => ref(u.attrs.id)) : [];
      const sk = kid(c, 'skills', 'Skills'); ch.skills = sk ? Object.fromEntries(kids(sk, 'skill').map(s => [s.attrs.id, +s.attrs.value])) : {};
      // equipment resolution mirrors BasicCharacterObject.Deserialize + MBEquipmentRoster.Init/AddEquipmentRoster/AddOverriddenEquipments
      const eqs = kid(c, 'Equipments', 'equipments'); const sets = []; const setRefs = []; const overrides = [];
      if (eqs) {
        for (const e of eqs.children) {
          if (e.name === 'EquipmentRoster' || e.name === 'equipmentRoster') {
            sets.push({ type: e.attrs.equipmentType || (e.attrs.civilian === 'true' ? 'Civilian' : 'Battle'), src: 'inline', slots: Object.fromEntries(kids(e, 'equipment', 'Equipment').map(x => [x.attrs.slot, ref(x.attrs.id)])) });
          } else if (e.name === 'EquipmentSet' || e.name === 'equipmentSet') {
            const type = e.attrs.equipmentType || (e.attrs.civilian === 'true' ? 'Civilian' : 'Battle');
            setRefs.push({ id: e.attrs.id, type });
            const r = rosters[e.attrs.id];
            if (r) for (const s of r.sets) if (s.type === type) sets.push({ type, src: 'roster:' + r.id, slots: { ...s.slots } });
          } else if (e.name === 'equipment') overrides.push([e.attrs.slot, ref(e.attrs.id)]);
        }
      }
      for (const s of sets) for (const [slot, id] of overrides) s.slots[slot] = id;
      ch.equipment_set_refs = setRefs; ch.battle_sets = sets.filter(s => s.type === 'Battle');
      ch.civilian_set_count = sets.length - ch.battle_sets.length;
      if (chars[ch.id]) {
        charDupes.push({ id: ch.id, first: chars[ch.id].file + ':' + chars[ch.id].line, second: ch.file + ':' + ch.line });
        // the object manager keeps one object per id; later XML of the same id overwrites fields. Prefer the CustomGame-loaded copy.
        if (!inCustomGame(n.gameTypes)) continue;
      }
      chars[ch.id] = ch;
    }
  }
}

// ---------------------------------------------------------------- equipment analysis
const RANGED_CLS = { Bow: 'bow', Crossbow: 'crossbow', Javelin: 'throwing', ThrowingAxe: 'throwing', ThrowingKnife: 'throwing', Stone: 'throwing', Sling: 'sling' };
const POLE_CLS = new Set(['OneHandedPolearm', 'TwoHandedPolearm', 'LowGripPolearm', 'Pike']);
const TWOH_CLS = new Set(['TwoHandedSword', 'TwoHandedAxe', 'TwoHandedMace']);
function analyzeSet(slots) {
  const r = { horse: null, mount_kind: null, ranged: [], shield: false, polearm: false, couch: false, brace: false, pike: false, twohanded: false, melee: [], armor: { head: 0, body: 0, arm: 0, leg: 0 }, harness_armor: 0, missing: [] };
  for (const [slot0, id] of Object.entries(slots)) {
    const slot = SLOT_ALIASES[slot0] || slot0; const it = items[id];
    if (!it) { r.missing.push(id); continue; }
    if (/^Weapon\d$/.test(slot)) {
      if (it.type === 'Shield') { r.shield = true; continue; }
      const primary = it.crafted ? (it.usages[0] && weaponDescs[it.usages[0]].cls) : (it.weapons && it.weapons[0] && it.weapons[0].cls);
      if (primary && RANGED_CLS[primary]) r.ranged.push(RANGED_CLS[primary]);
      const allCls = it.classes || [];
      if (allCls.some(c => POLE_CLS.has(c)) && !(primary && RANGED_CLS[primary])) r.polearm = true;
      const feats = it.crafted ? it.features : (it.weapons || []).map(w => w.usage);
      if (it.crafted ? it.usages.includes('TwoHandedPolearm_Couchable') : (it.weapons || []).some(w => /couch/.test(w.usage))) r.couch = true;
      if (it.crafted ? it.usages.includes('TwoHandedPolearm_Bracing') : false) r.brace = true;
      if (it.crafted ? it.usages.includes('TwoHandedPolearm_Pike') : allCls.includes('Pike')) r.pike = true;
      if (allCls.some(c => TWOH_CLS.has(c))) r.twohanded = true;
      if (primary && !RANGED_CLS[primary] && !['Arrow', 'Bolt', 'SlingStone', 'Cartridge'].includes(primary)) r.melee.push(primary);
      void feats;
    } else if (slot === 'Horse') {
      if (it.horse && it.horse.mountable) { r.horse = id; const m = it.horse.monster || ''; r.mount_kind = /camel/.test(m) ? 'camel' : /mule/.test(m) ? 'mule' : 'horse'; }
    } else if (slot === 'HorseHarness') { if (it.armor) r.harness_armor = it.armor.body; }
    else if (it.armor) { for (const k of ['head', 'body', 'arm', 'leg']) r.armor[k] += it.armor[k]; }
  }
  r.armor.total = r.armor.head + r.armor.body + r.armor.arm + r.armor.leg;
  return r;
}
const majority = (arr) => { const c = {}; for (const x of arr) c[x] = (c[x] || 0) + 1; return Object.entries(c).sort((a, b) => b[1] - a[1])[0]; };
function armorClass(total) { return total < 30 ? 'unarmored' : total < 60 ? 'light' : total < 100 ? 'medium' : total < 150 ? 'heavy' : 'very_heavy'; }
function summarizeEquipment(ch) {
  const an = ch.battle_sets.map(s => analyzeSet(s.slots)); const N = an.length || 1;
  const frac = f => r2(an.filter(f).length / N);
  const rangedAny = an.map(a => a.ranged.length ? majority(a.ranged)[0] : 'none');
  const rangedOnly = rangedAny.filter(x => x !== 'none');
  const rangedMaj = (rangedOnly.length && rangedOnly.length / (an.length || 1) >= 0.5) ? majority(rangedOnly)[0] : 'none';
  const avg = k => an.length ? Math.round(an.reduce((s, a) => s + a.armor[k], 0) / an.length) : 0;
  const armor = { head: avg('head'), body: avg('body'), arm: avg('arm'), leg: avg('leg') }; armor.total = armor.head + armor.body + armor.arm + armor.leg;
  const horses = [...new Set(an.map(a => a.horse).filter(Boolean))];
  const kinds = [...new Set(an.map(a => a.mount_kind).filter(Boolean))];
  const mountedRatio = frac(a => !!a.horse);
  const mounted = mountedRatio >= 0.5;
  const ranged = rangedMaj;
  const formation_from_gear = mounted ? (ranged === 'bow' || ranged === 'crossbow' ? 'HorseArcher' : 'Cavalry') : (ranged === 'bow' || ranged === 'crossbow' || ranged === 'sling' ? 'Ranged' : 'Infantry');
  const missing = [...new Set(an.flatMap(a => a.missing))];
  return {
    battle_set_count: an.length, mounted, mounted_ratio: mountedRatio, mount_kind: kinds.join('|') || null, horses,
    ranged, ranged_all: [...new Set(an.flatMap(a => a.ranged))], ranged_ratio: frac(a => a.ranged.length > 0),
    shield: frac(a => a.shield) >= 0.5, shield_ratio: frac(a => a.shield),
    polearm: frac(a => a.polearm) >= 0.5, polearm_ratio: frac(a => a.polearm),
    couch_lance: frac(a => a.couch && !!a.horse) >= 0.5, brace_spear: frac(a => a.brace) >= 0.5, pike: frac(a => a.pike) >= 0.5, two_handed: frac(a => a.twohanded) >= 0.5,
    melee_classes: [...new Set(an.flatMap(a => a.melee))],
    armor, armor_class: armorClass(armor.total), horse_harness_armor: an.length ? Math.round(an.reduce((s, a) => s + a.harness_armor, 0) / an.length) : 0,
    formation_from_gear, missing_items: missing,
    sample_set: ch.battle_sets[0] ? ch.battle_sets[0].slots : null,
  };
}

// ---------------------------------------------------------------- cultures
const cultures = {};
for (const n of nodesFor('SPCultures')) for (const f of n.files) {
  const doc = parseXml(fs.readFileSync(f, 'utf8'), f);
  for (const c of walk(doc)) {
    if (c.name !== 'Culture' || !c.attrs.id) continue;
    const a = c.attrs; const bm = kid(c, 'basic_mercenary_troops');
    const cu = {
      name: cleanName(a.name), is_main_culture: a.is_main_culture === 'true', is_bandit: a.is_bandit === 'true', can_have_settlement: a.can_have_settlement === 'true',
      basic_troop: ref(a.basic_troop) || null, elite_basic_troop: ref(a.elite_basic_troop) || null,
      melee_militia_troop: ref(a.melee_militia_troop) || null, ranged_militia_troop: ref(a.ranged_militia_troop) || null,
      melee_elite_militia_troop: ref(a.melee_elite_militia_troop) || null, ranged_elite_militia_troop: ref(a.ranged_elite_militia_troop) || null,
      basic_mercenary_troops: bm ? kids(bm, 'template').map(t => ref(t.attrs.name)) : [],
      caravan_guard: ref(a.caravan_guard) || null, veteran_caravan_guard: ref(a.veteran_caravan_guard) || null, caravan_master: ref(a.caravan_master) || null,
      guard: ref(a.guard) || null, prison_guard: ref(a.prison_guard) || null, armorer: ref(a.armorer) || null,
      bandit_bandit: ref(a.bandit_bandit) || null, bandit_raider: ref(a.bandit_raider) || null, bandit_chief: ref(a.bandit_chief) || null, bandit_boss: ref(a.bandit_boss) || null,
      color: a.color || null, color2: a.color2 || null, faction_banner_key: a.faction_banner_key || null,
      in_custom_battle_faction_list: null, file: rel(f), line: c.line, game_types: loadedIn(n.gameTypes),
    };
    for (const k of Object.keys(cu)) if (cu[k] === null && !['in_custom_battle_faction_list'].includes(k)) delete cu[k];
    cultures[a.id] = cu;
  }
}
// CustomBattleData.Factions (TaleWorlds.MountAndBlade.CustomBattle.dll v1.4.8): empire, sturgia, aserai, vlandia, battania, khuzait (+ nord only if NavalDLC active)
const CB_FACTIONS = ['empire', 'sturgia', 'aserai', 'vlandia', 'battania', 'khuzait'];
for (const [id, c] of Object.entries(cultures)) c.in_custom_battle_faction_list = CB_FACTIONS.includes(id) ? true : (id === 'nord' ? 'only_with_NavalDLC' : false);
// CustomBattleHelper.GetDefaultTroopOfFormationForFaction (TaleWorlds.MountAndBlade.CustomBattle.dll v1.4.8)
const CB_DEFAULTS = {
  empire: { Infantry: 'imperial_veteran_infantryman', Ranged: 'imperial_archer', Cavalry: 'imperial_heavy_horseman', HorseArcher: 'bucellarii' },
  sturgia: { Infantry: 'sturgian_spearman', Ranged: 'sturgian_archer', Cavalry: 'sturgian_hardened_brigand' },
  aserai: { Infantry: 'aserai_infantry', Ranged: 'aserai_archer', Cavalry: 'aserai_mameluke_cavalry', HorseArcher: 'aserai_faris' },
  vlandia: { Infantry: 'vlandian_swordsman', Ranged: 'vlandian_hardened_crossbowman', Cavalry: 'vlandian_knight' },
  battania: { Infantry: 'battanian_picked_warrior', Ranged: 'battanian_hero', Cavalry: 'battanian_scout' },
  khuzait: { Infantry: 'khuzait_spear_infantry', Ranged: 'khuzait_archer', Cavalry: 'khuzait_lancer', HorseArcher: 'khuzait_horse_archer' },
};
for (const [cid, d] of Object.entries(CB_DEFAULTS)) if (cultures[cid]) cultures[cid].custom_battle_defaults = d;

// ---------------------------------------------------------------- troop selection + trees
const TROOP_OCC = new Set(['Soldier', 'Mercenary', 'Bandit', 'CaravanGuard', 'PrisonGuard', 'Gangster', 'Guard']);
const isTroop = c => !c.is_hero && TROOP_OCC.has(c.occupation) && c.battle_sets.length > 0;
const troopIds = Object.keys(chars).filter(id => isTroop(chars[id]));
const parents = {};
for (const id of troopIds) for (const t of chars[id].upgrade_targets) (parents[t] = parents[t] || []).push(id);
// tree tier: depth from a root (troop with no upgrade parent), BFS minimal depth
// Pass 1 walks from the main cultures' basic_troop/elite_basic_troop (so Campaign-only bandit→soldier upgrades
// such as steppe_bandits_bandit→khuzait_* do not hijack the culture trees); pass 2 covers every other root.
const depth = {}; const treeRoot = {};
const roots = troopIds.filter(id => !(parents[id] || []).length);
function bfs(starts) {
  const q = starts.map(r => [r, 1, r]);
  while (q.length) {
    const [id, d, root] = q.shift();
    if (depth[id]) continue;
    depth[id] = d; treeRoot[id] = root;
    for (const t of (chars[id] && chars[id].upgrade_targets) || []) if (!depth[t]) q.push([t, d + 1, root]);
  }
}
bfs(Object.values(cultures).filter(c => c.is_main_culture).flatMap(c => [c.basic_troop, c.elite_basic_troop]).filter(x => x && chars[x]));
bfs(roots.filter(r => !depth[r]));
const militiaIds = new Set(); const mercListed = new Set(); const banditRoles = {};
for (const [cid, c] of Object.entries(cultures)) {
  for (const k of ['melee_militia_troop', 'ranged_militia_troop', 'melee_elite_militia_troop', 'ranged_elite_militia_troop']) if (c[k]) militiaIds.add(c[k]);
  for (const m of c.basic_mercenary_troops || []) mercListed.add(m);
  for (const k of ['bandit_bandit', 'bandit_raider', 'bandit_chief', 'bandit_boss']) if (c[k]) banditRoles[c[k]] = k.replace('bandit_', '');
}
function descendants(root) { const out = new Set(); const st = [root]; while (st.length) { const x = st.pop(); if (out.has(x) || !chars[x]) continue; out.add(x); st.push(...chars[x].upgrade_targets); } return out; }
const regularTree = {}, nobleTree = {};
for (const [cid, c] of Object.entries(cultures)) {
  if (c.basic_troop && c.basic_troop !== c.elite_basic_troop) for (const d of descendants(c.basic_troop)) if (chars[d] && chars[d].culture === cid) (regularTree[d] = regularTree[d] || []).push(cid);
  if (c.elite_basic_troop && c.is_main_culture) for (const d of descendants(c.elite_basic_troop)) if (chars[d] && chars[d].culture === cid) (nobleTree[d] = nobleTree[d] || []).push(cid);
}
const troops = {};
for (const id of troopIds.sort()) {
  const c = chars[id]; const eq = summarizeEquipment(c);
  let line = 'other';
  const fromObsolete = /obsolete_characters/.test(c.file);
  const cgLoaded = c.game_types.includes('ALL') || c.game_types.includes('CustomGame');
  if (c.occupation === 'Bandit' || (cultures[c.culture] && cultures[c.culture].is_bandit)) line = 'bandit';
  else if (/_tier_[0-9]$/.test(id)) line = 'minor_faction';
  else if (c.occupation === 'Gangster' || /^gangster_/.test(id)) line = 'gangster';
  else if (!cgLoaded && /spspecialcharacters|story_mode_characters/.test(c.file) && !/^guard_|prison_guard/.test(id)) line = 'quest';
  else if ((parents[id] || []).length && (parents[id] || []).every(p => chars[p] && (chars[p].occupation === 'Bandit'))) line = 'bandit_upgrade';
  else if (militiaIds.has(id) || /militia/.test(id)) line = 'militia';
  else if (nobleTree[id]) line = 'noble';
  else if (regularTree[id]) line = 'regular';
  else if (c.occupation === 'Mercenary' || mercListed.has(id) || /mercenary/.test(id)) line = 'mercenary';
  else if (c.occupation === 'CaravanGuard' || /caravan/.test(id)) line = 'caravan';
  else if (c.occupation === 'PrisonGuard' || /guard/.test(id)) line = 'guard';
  else if (fromObsolete) line = 'legacy';
  const group = c.default_group ? (['infantry', 'ranged', 'cavalry', 'horsearcher', 'skirmisher', 'heavyinfantry', 'lightcavalry', 'heavycavalry'].includes(c.default_group.toLowerCase()) ? { infantry: 'Infantry', ranged: 'Ranged', cavalry: 'Cavalry', horsearcher: 'HorseArcher', skirmisher: 'Skirmisher', heavyinfantry: 'HeavyInfantry', lightcavalry: 'LightCavalry', heavycavalry: 'HeavyCavalry' }[c.default_group.toLowerCase()] : c.default_group) : 'Infantry';
  const battleTier = Math.min(Math.max(Math.ceil((c.level - 5) / 5), 0), 7);
  const cg = c.game_types.includes('ALL') || c.game_types.includes('CustomGame');
  troops[id] = {
    name: c.name, culture: c.culture, occupation: c.occupation, line, faction: line === 'minor_faction' ? id.replace(/_tier_[0-9]$/, '') : undefined, from_obsolete_file: fromObsolete || undefined, level: c.level, tier: Math.min(battleTier, 6), tree_tier: depth[id] || null, tree_root: treeRoot[id] || null,
    formation: group, formation_from_gear: eq.formation_from_gear,
    mounted: eq.mounted, mount_kind: eq.mount_kind, ranged: eq.ranged, ranged_all: eq.ranged_all, shield: eq.shield, polearm: eq.polearm,
    couch_lance: eq.couch_lance, brace_spear: eq.brace_spear, pike: eq.pike, two_handed: eq.two_handed, melee_classes: eq.melee_classes,
    armor: eq.armor, armor_class: eq.armor_class, horse_harness_armor: eq.horse_harness_armor, horses: eq.horses,
    ratios: { mounted: eq.mounted_ratio, ranged: eq.ranged_ratio, shield: eq.shield_ratio, polearm: eq.polearm_ratio },
    upgrade_targets: c.upgrade_targets, upgrade_from: parents[id] || [], upgrade_requires: c.upgrade_requires,
    is_female: c.is_female || undefined, is_obsolete: c.is_obsolete || undefined, is_basic_troop: c.is_basic_troop || undefined,
    skills: c.skills, battle_set_count: eq.battle_set_count, sample_set: eq.sample_set,
    custom_battle_default_for: Object.entries(CB_DEFAULTS[c.culture] || {}).filter(([, v]) => v === id).map(([k]) => k)[0] || undefined,
    loaded_in_customgame: cg, custom_battle_ui_selectable: cg && /soldier/i.test(c.occupation || '') && !c.is_obsolete && CB_FACTIONS.includes(c.culture),
    game_types: c.game_types, source_file: c.file, source_line: c.line,
    missing_items: eq.missing_items.length ? eq.missing_items : undefined,
  };
}

// ---------------------------------------------------------------- commanders (CustomBattle) and lords count
const commanders = Object.values(chars).filter(c => /^commander_\d+$/.test(c.id)).sort((a, b) => +a.id.split('_')[1] - +b.id.split('_')[1]).map(c => {
  const eq = summarizeEquipment(c);
  const n = +c.id.split('_')[1];
  return { id: c.id, culture: c.culture, name: c.name, is_female: c.is_female, age: c.age, default_group: c.default_group, mounted: eq.mounted, horse: eq.horses[0] || null, ranged: eq.ranged, shield: eq.shield, armor_total: eq.armor.total, skill_template: c.skill_template, visible_in_core_only_content: n === 1 || n === 11, source_file: c.file, source_line: c.line };
});

// ---------------------------------------------------------------- items catalog (CustomGame-loadable items only)
const cgItems = Object.values(items).filter(itemInCG);
const brief = it => { const o = { id: it.id, name: it.name, culture: it.culture, type: it.type, tier: it.tier, tierf: it.tierf, tier_src: it.tier_src }; return o; };
const usedBy = {}; for (const [tid, t] of Object.entries(troops)) for (const s of chars[tid].battle_sets) for (const id of Object.values(s.slots)) (usedBy[id] = usedBy[id] || new Set()).add(tid);
const used = id => (usedBy[id] ? [...usedBy[id]].sort() : []);
const mountOut = it => ({ ...brief(it), monster: it.horse.monster, speed: it.horse.speed, maneuver: it.horse.maneuver, charge: it.horse.charge, extra_health: it.horse.extra_health, mountable: it.horse.mountable, pack: it.horse.pack, merchandise_flags: it.flags, used_by_troops: used(it.id) });
const horses = cgItems.filter(it => it.horse && /^horse/.test(it.horse.monster || '')).map(mountOut);
const camels = cgItems.filter(it => it.horse && /camel/.test(it.horse.monster || '')).map(mountOut);
const otherAnimals = cgItems.filter(it => it.horse && !/^horse|camel/.test(it.horse.monster || '')).map(mountOut);
const harness = cgItems.filter(it => it.type === 'HorseHarness').map(it => ({ ...brief(it), family_type: it.family_type || null, mount_family: it.family_type === 2 ? 'camel' : it.family_type === 1 ? 'horse' : null, body_armor: it.armor.body, maneuver_bonus: it.maneuver_bonus || 0, speed_bonus: it.speed_bonus || 0, charge_bonus: it.charge_bonus || 0, used_by_troops: used(it.id).length }));
const weaponsOut = cgItems.filter(it => (it.crafted || it.weapons) && it.type !== 'Shield' && !['Arrows', 'Bolts', 'SlingStones', 'Banner'].includes(it.type)).map(it => ({ ...brief(it), classes: it.classes, usages: it.crafted ? it.usages : (it.weapons || []).map(w => w.usage).filter(Boolean), couch: it.crafted ? it.usages.includes('TwoHandedPolearm_Couchable') : (it.weapons || []).some(w => /couch/.test(w.usage)), brace: it.crafted ? it.usages.includes('TwoHandedPolearm_Bracing') : false, pike: it.crafted ? it.usages.includes('TwoHandedPolearm_Pike') : false, crafted: !!it.crafted, template: it.template, item_usage: it.weapons ? it.weapons[0].usage : undefined, used_by_troops: used(it.id).length, tournament: /tournament|_blunt|sparring|wooden/.test(it.id) || undefined }));
const armorsOut = cgItems.filter(it => it.armor && it.type !== 'HorseHarness').map(it => ({ ...brief(it), armor: it.armor, material: it.material, used_by_troops: used(it.id).length }));
const shieldsOut = cgItems.filter(it => it.type === 'Shield').map(it => ({ ...brief(it), cls: it.weapons[0].cls, hit_points: it.weapons[0].max, body_armor: it.weapons[0].body_armor, used_by_troops: used(it.id).length }));
const ammoOut = cgItems.filter(it => ['Arrows', 'Bolts', 'SlingStones'].includes(it.type)).map(it => ({ ...brief(it), damage: it.weapons[0].thrust, stack: it.weapons[0].max, used_by_troops: used(it.id).length }));
const bannersOut = cgItems.filter(it => it.type === 'Banner').map(it => ({ id: it.id, name: it.name, culture: it.culture }));
const real = w => !w.tournament && !/^(torch|peasant_|wooden_)/.test(w.id);
const notable = {
  couchable_lances: weaponsOut.filter(w => w.couch && real(w)).map(w => ({ id: w.id, name: w.name, culture: w.culture, tier: w.tier, used_by_troops: w.used_by_troops })),
  pikes: weaponsOut.filter(w => (w.pike || (w.classes || []).includes('Pike')) && real(w)).map(w => ({ id: w.id, name: w.name, culture: w.culture, tier: w.tier })),
  bracing_spears: weaponsOut.filter(w => w.brace && !w.pike && real(w)).length,
  longbows: weaponsOut.filter(w => w.type === 'Bow' && /long_bow/.test(w.item_usage || '')).map(w => ({ id: w.id, name: w.name, culture: w.culture, tier: w.tier })),
  mounted_bows: weaponsOut.filter(w => w.type === 'Bow' && !/long_bow/.test(w.item_usage || '')).map(w => ({ id: w.id, name: w.name, culture: w.culture, tier: w.tier, item_usage: w.item_usage })),
  crossbows: weaponsOut.filter(w => w.type === 'Crossbow').map(w => ({ id: w.id, name: w.name, culture: w.culture, tier: w.tier, item_usage: w.item_usage })),
  slings: weaponsOut.filter(w => (w.classes || []).includes('Sling')).map(w => ({ id: w.id, name: w.name, culture: w.culture })),
  javelins_and_throwing: weaponsOut.filter(w => ['Javelin', 'ThrowingAxe', 'ThrowingKnife'].includes((w.classes || [])[0]) && real(w)).map(w => ({ id: w.id, name: w.name, culture: w.culture, cls: w.classes[0], tier: w.tier })),
  heaviest_body_armor_per_culture: Object.fromEntries([...new Set(armorsOut.map(a => a.culture))].map(cu => [cu || 'none', armorsOut.filter(a => a.culture === cu && a.type === 'BodyArmor').sort((a, b) => b.armor.body - a.armor.body).slice(0, 3).map(a => ({ id: a.id, name: a.name, body: a.armor.body, tier: a.tier }))])),
  war_mounts: [...horses, ...camels].filter(h => h.mountable && (h.charge >= 20 || /war|noble|special/.test(h.id))).sort((a, b) => b.tierf - a.tierf).map(h => ({ id: h.id, name: h.name, culture: h.culture, monster: h.monster, tier: h.tier, speed: h.speed, charge: h.charge, extra_health: h.extra_health })),
  heaviest_horse_barding: harness.filter(h => h.mount_family === 'horse').sort((a, b) => b.body_armor - a.body_armor).slice(0, 6).map(h => ({ id: h.id, name: h.name, culture: h.culture, body_armor: h.body_armor })),
  camel_saddles: harness.filter(h => h.mount_family === 'camel').map(h => ({ id: h.id, name: h.name, body_armor: h.body_armor })),
  elephants: { exists: false, note: 'No elephant monster or item in any CustomGame-loaded XML (see monster_ids_checked). Substitute CK3 elephant_cavalry with the heaviest available mount (special_camel T4, or a horse with imperial_scale_barding/chain_barding) ridden by very_heavy riders; optionally raise per-agent health/charge in CK3Bridge.' },
  gunpowder: { exists: false, note: 'No firearms in v1.4.8 single-player items (ItemTypeEnum has Pistol/Musket/Bullets but no SP items use them). Substitute CK3 gunpowder MAA (handgunners, fire_lancers) with heavy crossbowmen.' },
};

// ---------------------------------------------------------------- monsters check (for elephants)
const monsterIds = [];
for (const n of nodesFor('Monsters')) for (const f of n.files) for (const el of walk(parseXml(fs.readFileSync(f, 'utf8'), f))) if (el.name === 'Monster' && el.attrs.id) monsterIds.push(el.attrs.id);
notable.elephants.monster_ids_checked = monsterIds;
notable.elephants.exists = monsterIds.some(m => /elephant/i.test(m)) || Object.keys(items).some(i => /elephant/i.test(i));

// ---------------------------------------------------------------- per-culture summary trees
for (const [cid, cu] of Object.entries(cultures)) {
  const mine = Object.entries(troops).filter(([, t]) => t.culture === cid);
  const tree = {};
  for (const [id, t] of mine) { const k = t.line; (tree[k] = tree[k] || []).push(id); }
  cu.troops_by_line = tree;
}

// ---------------------------------------------------------------- CK3 MAA base types (read-only, from the local CK3 install) + mapping hints (INFERRED)
const CK3 = (process.env.CK3_ROOT || gameDirs.ck3RootDir()).replace(/\\/g, '/');
const ck3Maa = {}; const ck3MaaSrc = [];
try {
  const dirs = [CK3 + '/game/common/men_at_arms_types'];
  const dlcRoot = CK3 + '/game/dlc';
  if (fs.existsSync(dlcRoot)) for (const d of fs.readdirSync(dlcRoot)) { const p = dlcRoot + '/' + d + '/common/men_at_arms_types'; if (fs.existsSync(p)) dirs.push(p); }
  for (const dir of dirs) {
    if (!fs.existsSync(dir)) continue;
    for (const fn of fs.readdirSync(dir).filter(x => x.endsWith('.txt'))) {
      const full = dir + '/' + fn; ck3MaaSrc.push(full.replace(CK3 + '/', ''));
      const t = fs.readFileSync(full, 'utf8').replace(/#[^\n]*/g, '');
      let depthB = 0, name = null, start = 0; const re = /([A-Za-z0-9_]+)\s*=\s*\{|\{|\}/g; let m;
      while ((m = re.exec(t))) {
        if (m[0] === '}') { depthB--; if (depthB === 0 && name) { const body = t.slice(start, m.index); const ty = (body.match(/^\s*type\s*=\s*([a-z_]+)/m) || [])[1] || 'unknown'; (ck3Maa[ty] = ck3Maa[ty] || []).push(name); name = null; } }
        else { if (depthB === 0 && m[1]) { name = m[1]; start = m.index; } depthB++; }
      }
    }
  }
  for (const k of Object.keys(ck3Maa)) ck3Maa[k] = [...new Set(ck3Maa[k])].sort();
} catch (e) { ck3Maa.__error = String(e); }

const CORE = ['regular', 'noble'];
const MAIN = Object.entries(cultures).filter(([, c]) => c.is_main_culture).map(([id]) => id);
function pool(cu, lines = CORE) { return Object.entries(troops).filter(([, t]) => t.culture === cu && lines.includes(t.line) && t.loaded_in_customgame).map(([id, t]) => ({ id, ...t })); }
function pick(list, tier, n = 2, score = () => 0) {
  return list.map(t => ({ t, k: Math.abs(t.tier - tier) * 10 - score(t) })).sort((a, b) => a.k - b.k || b.t.armor.total - a.t.armor.total).slice(0, n).map(x => x.t.id);
}
const isMeleeInf = t => !t.mounted && !['bow', 'crossbow'].includes(t.ranged);
const RULES = {
  levy_melee: { tier: 1, why: 'CK3 levies (melee share): culture recruit/T2 foot + militia spearman', f: cu => [...pick(pool(cu, ['regular']).filter(isMeleeInf), 1, 2), ...pool(cu, ['militia']).filter(t => t.ranged === 'none' && t.tier === 2).map(t => t.id)] },
  levy_ranged: { tier: 2, why: 'CK3 levies (ranged share): militia archer + T2 foot ranged', f: cu => [...pool(cu, ['militia']).filter(t => ['bow', 'crossbow'].includes(t.ranged) && t.tier === 2).map(t => t.id), ...pick(pool(cu, ['regular']).filter(t => !t.mounted && ['bow', 'crossbow'].includes(t.ranged)), 2, 1)] },
  heavy_infantry: { tier: 4, why: 'shielded heavy melee infantry', f: cu => pick(pool(cu).filter(t => isMeleeInf(t) && t.shield && ['heavy', 'very_heavy'].includes(t.armor_class)), 4, 3) },
  pikemen: { tier: 4, why: 'pike > bracing spear > any polearm infantry', f: cu => pick(pool(cu).filter(t => isMeleeInf(t) && (t.pike || t.brace_spear || t.polearm)), 4, 3, t => (t.pike ? 3 : 0) + (t.brace_spear ? 1 : 0)) },
  archers: { tier: 4, why: 'foot bowmen', f: cu => pick(pool(cu).filter(t => !t.mounted && t.ranged === 'bow'), 4, 3) },
  crossbowmen: { tier: 4, why: 'foot crossbowmen (CK3 crossbowmen/ballistrai are base type archers)', f: cu => pick(pool(cu).filter(t => !t.mounted && t.ranged === 'crossbow'), 4, 3) },
  skirmishers: { tier: 3, why: 'light javelin/throwing infantry', f: cu => pick(pool(cu).filter(t => !t.mounted && t.ranged === 'throwing' && t.armor_class !== 'very_heavy'), 3, 3) },
  light_cavalry: { tier: 3, why: 'light/medium cavalry, no couched lance preferred', f: cu => pick(pool(cu).filter(t => t.mounted && t.formation === 'Cavalry' && t.armor_class !== 'very_heavy'), 3, 3, t => (t.couch_lance ? -1 : 1)) },
  heavy_cavalry: { tier: 5, why: 'heavy shock cavalry (couched lance preferred)', f: cu => pick(pool(cu).filter(t => t.mounted && t.formation === 'Cavalry' && ['heavy', 'very_heavy'].includes(t.armor_class)), 5, 3, t => (t.couch_lance ? 2 : 0)) },
  archer_cavalry: { tier: 4, why: 'horse archers (bow, mounted)', f: cu => pick(pool(cu).filter(t => t.mounted && t.ranged === 'bow'), 4, 3) },
  knights: { tier: 6, why: 'CK3 knights: top of the noble (elite_basic_troop) line', f: cu => pick(pool(cu, ['noble']), 6, 2) },
};
const FALLBACK = { archer_cavalry: 'khuzait', crossbowmen: 'vlandia', archers: 'battania', heavy_cavalry: 'vlandia', light_cavalry: 'khuzait', pikemen: 'vlandia', skirmishers: 'battania' };
const byCulture = {};
for (const cu of MAIN) {
  byCulture[cu] = {};
  for (const [k, r] of Object.entries(RULES)) {
    let ids = [...new Set(r.f(cu))]; let fb = null;
    if (!ids.length && FALLBACK[k] && FALLBACK[k] !== cu) { ids = [...new Set(r.f(FALLBACK[k]))]; fb = FALLBACK[k]; }
    byCulture[cu][k] = fb ? { fallback_culture: fb, troops: ids } : ids;
  }
  byCulture[cu].commander_bodies = commanders.filter(c => c.culture === cu).map(c => c.id);
}
const camelTroops = Object.entries(troops).filter(([, t]) => t.mount_kind && t.mount_kind.includes('camel')).map(([id, t]) => ({ id, culture: t.culture, tier: t.tier, line: t.line, loaded_in_customgame: t.loaded_in_customgame }));
const mappingHints = {
  status: 'INFERRED design suggestion generated by the rules below from this catalog; tune in bridge/mapping.json',
  ck3_maa_base_types: ck3Maa, ck3_maa_sources: ck3MaaSrc,
  rules: Object.fromEntries(Object.entries(RULES).map(([k, r]) => [k, { target_tier: r.tier, why: r.why }])),
  fallback_cultures: FALLBACK,
  special: {
    camel_cavalry: { native_camel_troops: camelTroops, note: 'Native camel riders exist only in minor factions (jawwal, ghilman). For culture-accurate camel cavalry define CK3Bridge NPCCharacters reusing an Aserai/Khuzait rider kit with Horse=camel|war_camel|special_camel and HorseHarness=camel_saddle (harness family_type 2 = camel).' },
    elephant_cavalry: 'no elephants in v1.4.8: use the culture heavy_cavalry pick (or special_camel riders) and apply a per-agent health/charge multiplier in CK3Bridge; convert counts by CK3 damage/toughness ratio',
    gunpowder: 'no SP firearms: use the crossbowmen pick',
    siege_weapon: 'not representable in a field battle mission: skip (siege engines only exist in siege missions, OpenSiegeMissionWithDeployment)',
    nomadic_horde: 'khuzait archer_cavalry + light_cavalry mix',
    peasant_militia: 'levy_melee (militia spearman)',
  },
  by_culture: byCulture,
};

// ---------------------------------------------------------------- output
const out = {
  meta: {
    generated: new Date().toISOString(), generator: SELF, bannerlord_version: 'v1.4.8 (Modules/Native/SubModule.xml <Version>)',
    bannerlord_root: '<Bannerlord install>',
    notes: [
      'Only XML whose SubModule.xml XmlNode includes GameType CustomGame (or no IncludedGameTypes) is loaded by CustomGame (TaleWorlds.ObjectSystem.MBObjectManager.GetMergedXmlForManaged). troops[*].loaded_in_customgame reflects this. SandBox/ModuleData/bandits.xml (looters, sea raiders, ...) is Campaign/CampaignStoryMode ONLY.',
      'tier = BasicCharacterObject.GetBattleTier formula min(max(ceil((level-5)/5),0),7) clamped to 6 (same as DefaultCharacterStatsModel.GetTier); tree_tier = depth in upgrade tree from a root (1 = recruit).',
      'formation = XML default_group (FormationClass, case-insensitive); formation_from_gear = derived from resolved battle equipment (horse + bow/crossbow).',
      'Equipment resolution mirrors BasicCharacterObject.Deserialize: inline <EquipmentRoster> (civilian="true" → Civilian), <EquipmentSet id equipmentType> pulls matching-type sets from global EquipmentRosters, direct <equipment> children override every set.',
      'item tierf: exact DefaultItemValueModel.CalculateTier for armor/horse/harness/bow/crossbow/ammo/shield; crafted weapons use the _tN id suffix (TaleWorlds naming) or average crafting-piece tier (approximation; the real formula also needs computed melee stats). tier = clamp(round(tierf),0,6).',
      'Crafted weapon usages = template WeaponDescriptions whose AvailablePieces contain every used piece (TaleWorlds.Core.Crafting.GenerateCraftedItem). couch = TwoHandedPolearm_Couchable, brace = TwoHandedPolearm_Bracing, pike = TwoHandedPolearm_Pike.',
    ],
    troop_fields: {
      line: 'regular | noble (elite_basic_troop tree) | militia | minor_faction (*_tier_N) | mercenary (neutral_culture) | caravan | guard | bandit (Campaign-only) | bandit_upgrade | gangster | quest (Campaign/StoryMode-only special) | legacy (obsolete_characters.xml)',
      tier: 'level-based troop tier 0..6 (DefaultCharacterStatsModel.GetTier); tree_tier: depth from tree root (1 = root)',
      formation: 'XML default_group normalised to FormationClass name; formation_from_gear: Cavalry/HorseArcher/Ranged/Infantry from resolved battle equipment',
      ranged: "'bow' | 'crossbow' | 'throwing' | 'sling' | 'none' — majority across battle variants, only if >=50% of variants carry a ranged weapon; ranged_all lists every ranged kind seen",
      mounted: 'true if >=50% of battle variants have a mountable Horse; mount_kind horse|camel|mule',
      shield_polearm_etc: 'shield / polearm / couch_lance (mounted + couchable usage) / brace_spear / pike / two_handed: >=50% of battle variants; ratios.* gives the exact share',
      armor: 'average over battle variants of summed head/body/arm/leg armor of all worn pieces; armor_class u<30 l<60 m<100 h<150 vh>=150',
      sample_set: 'first resolved battle equipment variant (slot -> item id), handy as a template for CK3Bridge custom troops',
      loaded_in_customgame: 'XML file is loaded for game type CustomGame (spawnable in a custom battle mission)',
      custom_battle_ui_selectable: 'would show in the vanilla Custom Battle troop picker (IsSoldier && !IsObsolete && CB faction)',
    },
    xml_nodes: xmlNodes.filter(x => ['NPCCharacters', 'Items', 'EquipmentRosters', 'SPCultures', 'CraftingPieces', 'CraftingTemplates', 'WeaponDescriptions', 'Monsters'].includes(x.id)).map(x => ({ module: x.module, id: x.id, path: x.path, game_types: loadedIn(x.gameTypes), files: x.files.map(rel), submodule_line: x.submodule + ':' + x.line })),
    counts: { characters_parsed: Object.keys(chars).length, troops: Object.keys(troops).length, troops_loaded_in_customgame: Object.values(troops).filter(t => t.loaded_in_customgame).length, items_parsed: Object.keys(items).length, items_customgame: cgItems.length, rosters: Object.keys(rosters).length, character_duplicates: charDupes.length, item_duplicates: itemDupes.length },
    character_duplicates: charDupes, item_duplicates: itemDupes.slice(0, 50),
  },
  cultures,
  custom_battle: { factions: CB_FACTIONS, nord_requires: 'NavalDLC (not installed)', default_troops: CB_DEFAULTS, evidence: 'TaleWorlds.MountAndBlade.CustomBattle.dll v1.4.8: CustomBattleData.Factions/Characters, CustomBattleHelper.GetDefaultTroopOfFormationForFaction, ArmyCompositionGroupVM ctor (c.IsSoldier && !c.IsObsolete), ArmyCompositionItemVM.IsValidUnitItem (culture == selected && DefaultFormationClass match)' },
  troops,
  commanders,
  mapping_hints: mappingHints,
  items: { horses, camels, other_animals: otherAnimals, harness, notable, weapons: weaponsOut, armors: armorsOut, shields: shieldsOut, ammo: ammoOut, banners: bannersOut },
};
fs.mkdirSync(path.dirname(OUT_JSON), { recursive: true });
fs.writeFileSync(OUT_JSON, JSON.stringify(out, null, 1));
console.log('wrote', OUT_JSON, (fs.statSync(OUT_JSON).size / 1024).toFixed(0) + 'KB', JSON.stringify(out.meta.counts));
module.exports = out;
