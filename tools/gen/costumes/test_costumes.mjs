#!/usr/bin/env node
// Tests of the costume tables (docs/PROTOCOL.md 21) that need no Bannerlord install: item parsing, era, bans, role tags, the
// looks, and the shipped bridge/data/ck3xbl_costumes.json against mapping.json.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { scanItemXml } from './scan_items.mjs';
import { ERA, LOOKS, UNIQUE_LOOKS, isBanned, itemEra, itemTags, lookWeights, PREFER_FACTOR, NEUTRAL_SHARE } from './build_costumes.mjs';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');

const xml = `<Items>
  <Item id="x_helm" name="{=a}Nordic Vendel Helmet" mesh="m" culture="Culture.sturgia" weight="2" Type="HeadArmor">
    <ItemComponent><Armor head_armor="30" material_type="Plate"/></ItemComponent><Flags/></Item>
  <Item id="x_plate" name="{=b}Plate Cuirass With Visor" culture="Culture.vlandia" Type="BodyArmor">
    <ItemComponent><Armor body_armor="50" arm_armor="10" leg_armor="5"/></ItemComponent></Item>
  <Item id="x_kettle" name="{=c}Kettle Hat" culture="Culture.vlandia" Type="HeadArmor"><ItemComponent><Armor head_armor="20"/></ItemComponent></Item>
  <Item id="x_cloth" name="{=d}Peasant Tunic" culture="Culture.neutral_culture" Type="BodyArmor"><ItemComponent><Armor body_armor="4"/></ItemComponent></Item>
  <Item id="x_fur" name="{=e}Wolf Pelt Cape" culture="Culture.aserai" Type="Cape"><ItemComponent><Armor body_armor="2"/></ItemComponent></Item>
  <Item id="x_crown" name="{=f}Golden Crown" culture="Culture.empire" Type="HeadArmor"><ItemComponent><Armor head_armor="1"/></ItemComponent></Item>
  <Item id="x_sword" name="{=g}Sword" Type="OneHandedWeapon"/>
</Items>`;

const items = scanItemXml(xml, 'test');
const byId = Object.fromEntries(items.map((i) => [i.id, i]));
assert.equal(items.length, 6, 'weapons are not costume items');
assert.equal(byId.x_plate.total, 65, 'armour is the sum of the Armor attributes');
assert.equal(byId.x_plate.slot, 'Body');
assert.equal(byId.x_helm.culture, 'sturgia');
assert.equal(byId.x_helm.name, 'Nordic Vendel Helmet', 'the {=key} is stripped');

assert.equal(itemEra(byId.x_plate), ERA.late, 'plate and visors are late medieval');
assert.equal(itemEra(byId.x_kettle), ERA.high, 'kettle hats are high medieval');
assert.equal(itemEra(byId.x_helm), ERA.tribal, 'a vendel helmet fits every era');
assert.ok(isBanned(byId.x_crown), 'regalia are not troop wear');
assert.ok(!isBanned(byId.x_helm));
assert.deepEqual(itemTags(byId.x_cloth), ['levy'], 'peasant cloth is levy wear');

const norse = lookWeights(LOOKS.norse, items);
assert.equal(norse.x_helm, PREFER_FACTOR, 'a Nordic item is preferred by the Norse');
assert.equal(norse.x_cloth, NEUTRAL_SHARE, 'neutral items join every look');
assert.equal(norse.x_fur, undefined, 'an Aserai item is no Norse wear');
const arab = lookWeights(LOOKS.arab, items);
assert.equal(arab.x_fur, undefined, 'no fur in the south');

const mapping = JSON.parse(fs.readFileSync(path.join(ROOT, 'bridge', 'data', 'mapping.json'), 'utf8'));
for (const profile of Object.keys(mapping.profiles)) assert.ok(LOOKS[profile], `mapping.json profile ${profile} has a look`);
const shipped = JSON.parse(fs.readFileSync(path.join(ROOT, 'bridge', 'data', 'ck3xbl_costumes.json'), 'utf8'));
const ids = new Set(shipped.items.map((i) => i.id));
for (const [profile, look] of Object.entries(shipped.looks))
  for (const id of Object.keys(look)) assert.ok(ids.has(id), `${profile}: ${id} is a costume item`);
assert.deepEqual(Object.keys(shipped.looks).filter((k) => !k.startsWith('maa:')).sort(), Object.keys(mapping.profiles).sort(), 'one look per profile');
const maaIndex = new Set(JSON.parse(fs.readFileSync(path.join(ROOT, 'bridge', 'data', 'ck3_maa_index.json'), 'utf8')).keys);
for (const maa of Object.keys(UNIQUE_LOOKS)) {
  assert.ok(maaIndex.has(maa), `UNIQUE_LOOKS ${maa} is a CK3 MaA key`);
  assert.ok(Object.keys(shipped.looks[`maa:${maa}`] ?? {}).length > 50, `maa:${maa} has a look of its own`);
}

console.log('test_costumes: all tests passed');
