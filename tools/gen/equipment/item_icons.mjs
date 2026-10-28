// item_icons.mjs - one CK3 artifact icon per battle gear item, from Bannerlord's own thumbnail of that item (2026-10-28: the player
// should see in CK3 what he will carry in Bannerlord). Two steps:
//   node tools/gen/equipment/item_icons.mjs list
//       writes %LOCALAPPDATA%\ck3xbl\thumbs\items.txt (every item of bridge/data/ck3xbl_equipment.json); then a Bannerlord started
//       by the bridge renders them on the module's "thumbnails" command (command.txt), into thumbs\<id>.png, and writes done.txt.
//   node tools/gen/equipment/item_icons.mjs build [<thumbs folder>]
//       fits every PNG into a 128 x 128 square (fit_thumbs.ps1) and writes it as uncompressed DDS to
//       ck3_mod/ck3xbl/gfx/interface/icons/artifact/ck3xbl_item_<idx>.dds (the files are committed). gen_ck3_mod.mjs then gives each
//       item with an icon its own artifact visuals (equipment.mjs visualsText).
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..', '..', '..');
const EQUIPMENT = path.join(ROOT, 'bridge', 'data', 'ck3xbl_equipment.json');
export const ICON_DIR = path.join(ROOT, 'ck3_mod', 'ck3xbl', 'gfx', 'interface', 'icons', 'artifact');
export const SIZE = 128;

/** The icon file of a gear item (by its table index). */
export const itemIconName = (idx) => `ck3xbl_item_${idx}.dds`;

/** True when the item has an icon of its own in the mod. */
export const hasItemIcon = (idx) => fs.existsSync(path.join(ICON_DIR, itemIconName(idx)));

/** An uncompressed A8R8G8B8 DDS from BGRA bytes, as the vanilla artifact icons (make_icons.mjs writes the same layout). */
function dds(width, height, bgra) {
  const h = Buffer.alloc(128);
  h.write('DDS ', 0, 'ascii');
  h.writeUInt32LE(124, 4);
  h.writeUInt32LE(0x1 | 0x2 | 0x4 | 0x1000 | 0x8, 8);
  h.writeUInt32LE(height, 12);
  h.writeUInt32LE(width, 16);
  h.writeUInt32LE(width * 4, 20);
  h.writeUInt32LE(32, 76);
  h.writeUInt32LE(0x1 | 0x40, 80);
  h.writeUInt32LE(32, 88);
  h.writeUInt32LE(0x00ff0000, 92);
  h.writeUInt32LE(0x0000ff00, 96);
  h.writeUInt32LE(0x000000ff, 100);
  h.writeUInt32LE(0xff000000, 104);
  h.writeUInt32LE(0x1000, 108);
  return Buffer.concat([h, bgra]);
}

function items() {
  return JSON.parse(fs.readFileSync(EQUIPMENT, 'utf8')).items;
}

function thumbsDir() {
  return path.join(process.env.LOCALAPPDATA || os.homedir(), 'ck3xbl', 'thumbs');
}

function list() {
  const dir = thumbsDir();
  fs.mkdirSync(dir, { recursive: true });
  const ids = [...new Set(items().map((it) => it.id))];
  fs.writeFileSync(path.join(dir, 'items.txt'), ids.join('\n') + '\n');
  fs.rmSync(path.join(dir, 'done.txt'), { force: true });
  console.log(`item_icons: ${ids.length} item(s) listed in ${dir}\\items.txt; send "thumbnails" to a bridge-started Bannerlord`);
}

function build(from) {
  const dir = from ?? thumbsDir();
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'ck3xbl-thumbs-'));
  try {
    execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', path.join(HERE, 'fit_thumbs.ps1'),
      '-In', dir, '-Out', tmp, '-Size', String(SIZE)], { stdio: ['ignore', 'inherit', 'inherit'] });
    fs.mkdirSync(ICON_DIR, { recursive: true });
    let written = 0;
    const missing = [];
    for (const it of items()) {
      const bin = path.join(tmp, `${it.id}.bin`);
      if (!fs.existsSync(bin)) { missing.push(it.id); continue; }
      const bgra = fs.readFileSync(bin);
      if (bgra.length !== SIZE * SIZE * 4) throw new Error(`${it.id}: ${bgra.length} bytes, expected ${SIZE * SIZE * 4}`);
      fs.writeFileSync(path.join(ICON_DIR, itemIconName(it.idx)), dds(SIZE, SIZE, bgra));
      written++;
    }
    console.log(`item_icons: ${written} icon(s) written to ${ICON_DIR}${missing.length ? `; no thumbnail for ${missing.join(', ')}` : ''}`);
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [cmd, arg] = process.argv.slice(2);
  if (cmd === 'list') list();
  else if (cmd === 'build') build(arg);
  else {
    console.error('usage: node tools/gen/equipment/item_icons.mjs list | build [<thumbs folder>]');
    process.exit(2);
  }
}
