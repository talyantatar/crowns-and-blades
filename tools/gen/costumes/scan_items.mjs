// Scans the wearable items of a Bannerlord install (and of Open Source Armory / Saddlery when given) for the costume
// tables (docs/PROTOCOL.md 21): id, slot, culture, armour, material, weight, appearance and English name.
import fs from 'node:fs';
import path from 'node:path';

export const SLOT_BY_TYPE = {
  HeadArmor: 'Head', BodyArmor: 'Body', LegArmor: 'Leg', HandArmor: 'Gloves', Cape: 'Cape', HorseHarness: 'HorseHarness',
};

const ARMOR_ATTRS = ['head_armor', 'body_armor', 'leg_armor', 'arm_armor'];

function attrs(tag) {
  const out = {};
  for (const m of tag.matchAll(/([\w:]+)\s*=\s*"([^"]*)"/g)) out[m[1]] = m[2];
  return out;
}

function stripName(name) {
  return (name || '').replace(/^\{=[^}]*\}/, '').trim();
}

/** Every armour-like Item of one XML text. `source` names the pack (vanilla, osa:<file>). */
export function scanItemXml(text, source) {
  const items = [];
  for (const m of text.matchAll(/<Item\s([^>]*?)(\/>|>([\s\S]*?)<\/Item>)/g)) {
    const a = attrs(m[1]);
    const slot = SLOT_BY_TYPE[a.Type];
    if (!slot || !a.id) continue;
    const body = m[3] || '';
    const armorTag = body.match(/<Armor\s([^>]*?)\/?>/);
    const ar = armorTag ? attrs(armorTag[1]) : {};
    const armor = {};
    for (const k of ARMOR_ATTRS) if (ar[k] !== undefined) armor[k] = Number(ar[k]) || 0;
    const flags = body.match(/<Flags\s([^>]*?)\/?>/);
    const fl = flags ? attrs(flags[1]) : {};
    items.push({
      id: a.id,
      slot,
      name: stripName(a.name),
      culture: (a.culture || '').replace(/^Culture\./, '') || 'neutral',
      weight: Number(a.weight) || 0,
      appearance: Number(a.appearance) || 0,
      material: ar.material_type || null,
      family: ar.family_type !== undefined ? Number(ar.family_type) : null,
      armor,
      total: Object.values(armor).reduce((s, v) => s + v, 0),
      civilian: fl.Civilian === 'true',
      notMerchandise: fl.NotMerchandise === 'true' || a.is_merchandise === 'false',
      source,
    });
  }
  return items;
}

/** XML files a module loads for CustomGame (SubModule.xml XmlNodes with id Items, or every items/*.xml for stock modules). */
export function moduleItemFiles(moduleDir) {
  const sub = path.join(moduleDir, 'SubModule.xml');
  const data = path.join(moduleDir, 'ModuleData');
  const files = new Set();
  if (fs.existsSync(sub)) {
    const xml = fs.readFileSync(sub, 'utf8');
    for (const m of xml.matchAll(/<XmlNode>([\s\S]*?)<\/XmlNode>/g)) {
      const name = m[1].match(/<XmlName\s+id="(\w+)"\s+path="([^"]+)"/);
      if (!name || name[1] !== 'Items') continue;
      const types = [...m[1].matchAll(/<GameType\s+value="(\w+)"/g)].map((t) => t[1]);
      if (types.length && !types.includes('CustomGame')) continue;
      for (const cand of [path.join(data, name[2] + '.xml'), path.join(data, name[2])]) {
        if (fs.existsSync(cand) && fs.statSync(cand).isFile()) files.add(cand);
        else if (fs.existsSync(cand) && fs.statSync(cand).isDirectory())
          for (const f of fs.readdirSync(cand)) if (f.endsWith('.xml')) files.add(path.join(cand, f));
      }
    }
  }
  return [...files].sort();
}

/** Vanilla items (SandBoxCore items folder, which CustomGame loads) plus every extra module directory given. */
export function scanInstall(blDir, extraModules = []) {
  const out = [];
  const vanilla = path.join(blDir, 'Modules', 'SandBoxCore', 'ModuleData', 'items');
  for (const f of fs.readdirSync(vanilla).filter((f) => f.endsWith('.xml')).sort())
    out.push(...scanItemXml(fs.readFileSync(path.join(vanilla, f), 'utf8'), 'vanilla'));
  for (const dir of extraModules) {
    const mod = path.basename(dir);
    for (const f of moduleItemFiles(dir))
      out.push(...scanItemXml(fs.readFileSync(f, 'utf8'), `${mod}:${path.basename(f, '.xml')}`));
  }
  const seen = new Map();
  for (const it of out) seen.set(it.id, it);   // a later module overrides an id, as MBObjectManager merges
  return [...seen.values()];
}
