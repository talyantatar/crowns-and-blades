// make_icons.mjs - the battle gear icons of the CK3 mod, from the game-icons.net SVGs in tools/gen/equipment/icons
// (CC BY 3.0, authors in icons/SOURCES.md). Each icon is rendered as an alpha mask (rasterize_svg.ps1, WPF), then tinted
// with a light-from-above metal gradient and a dark rim, and written as uncompressed 32-bit DDS in the vanilla layouts:
//   gfx/interface/icons/artifact/ck3xbl_<kind>.dds   960 x 240, four 240 x 240 frames (as vanilla artifact icons)
//   gfx/interface/icons/inventory/ck3xbl_horse.dds   60 x 60 (the horse slot icon)
// Usage (Windows): node tools/gen/equipment/make_icons.mjs   (writes into ck3_mod/ck3xbl; the files are committed)

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const MOD = path.resolve(HERE, '..', '..', '..', 'ck3_mod', 'ck3xbl');
const ICONS = path.join(HERE, 'icons');

/** Alpha mask (size x size bytes) of an SVG. */
function mask(svg, size) {
  const out = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'ck3xbl-icon-')), 'mask.bin');
  execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', path.join(HERE, 'rasterize_svg.ps1'),
    '-Svg', svg, '-Size', String(size), '-Out', out], { stdio: ['ignore', 'ignore', 'inherit'] });
  const a = fs.readFileSync(out);
  fs.rmSync(path.dirname(out), { recursive: true, force: true });
  if (a.length !== size * size) throw new Error(`${svg}: mask of ${a.length} bytes`);
  return a;
}

/** Smallest alpha in a disc of radius r around each pixel (erosion): what stays inside the rim. */
function erode(a, size, r) {
  const offs = [];
  for (let dy = -r; dy <= r; dy++) for (let dx = -r; dx <= r; dx++) if (dx * dx + dy * dy <= r * r) offs.push([dx, dy]);
  const out = new Uint8Array(size * size);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      let m = 255;
      for (const [dx, dy] of offs) {
        const xx = x + dx;
        const yy = y + dy;
        const v = xx < 0 || yy < 0 || xx >= size || yy >= size ? 0 : a[yy * size + xx];
        if (v < m) { m = v; if (!m) break; }
      }
      out[y * size + x] = m;
    }
  }
  return out;
}

/** RGBA of one frame: metal tint with light from above, a dark rim. */
function frame(a, size, tint) {
  const core = erode(a, size, Math.max(1, Math.round(size / 60)));
  const px = new Uint8Array(size * size * 4);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      const i = y * size + x;
      if (!a[i]) continue;
      const shade = 0.55 + 0.45 * (1 - y / size);
      const inner = core[i] / a[i];
      for (let c = 0; c < 3; c++) px[i * 4 + c] = Math.round(tint[c] * shade * inner + 28 * (1 - inner));
      px[i * 4 + 3] = a[i];
    }
  }
  return px;
}

/** An uncompressed A8R8G8B8 DDS (BGRA in memory), as the vanilla artifact icons. */
function dds(width, height, rgba) {
  const h = Buffer.alloc(128);
  h.write('DDS ', 0, 'ascii');
  h.writeUInt32LE(124, 4);
  h.writeUInt32LE(0x1 | 0x2 | 0x4 | 0x1000 | 0x8, 8);   // caps, height, width, pixelformat, pitch
  h.writeUInt32LE(height, 12);
  h.writeUInt32LE(width, 16);
  h.writeUInt32LE(width * 4, 20);
  h.writeUInt32LE(32, 76);                               // pixel format size
  h.writeUInt32LE(0x1 | 0x40, 80);                       // alpha pixels, RGB
  h.writeUInt32LE(32, 88);
  h.writeUInt32LE(0x00ff0000, 92);
  h.writeUInt32LE(0x0000ff00, 96);
  h.writeUInt32LE(0x000000ff, 100);
  h.writeUInt32LE(0xff000000, 104);
  h.writeUInt32LE(0x1000, 108);                          // texture
  const body = Buffer.alloc(width * height * 4);
  for (let i = 0; i < width * height; i++) {
    body[i * 4] = rgba[i * 4 + 2];
    body[i * 4 + 1] = rgba[i * 4 + 1];
    body[i * 4 + 2] = rgba[i * 4];
    body[i * 4 + 3] = rgba[i * 4 + 3];
  }
  return Buffer.concat([h, body]);
}

// Frame tints from plain to illustrious (the four frames of a vanilla artifact icon).
const TINTS = [[176, 168, 156], [196, 176, 140], [214, 190, 130], [236, 206, 120]];

function artifactIcon(svg) {
  const F = 240;
  const a = mask(svg, F);
  const out = new Uint8Array(F * 4 * F * 4);
  TINTS.forEach((tint, f) => {
    const px = frame(a, F, tint);
    for (let y = 0; y < F; y++) out.set(px.subarray(y * F * 4, (y + 1) * F * 4), (y * F * 4 + f * F) * 4);
  });
  return dds(F * 4, F, out);
}

function write(rel, buf) {
  const file = path.join(MOD, ...rel.split('/'));
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, buf);
  console.log(`wrote ${rel} (${buf.length} bytes)`);
}

const kinds = fs.readdirSync(ICONS).filter((f) => f.endsWith('.svg')).map((f) => f.slice(0, -4)).sort();
for (const kind of kinds) write(`gfx/interface/icons/artifact/ck3xbl_${kind}.dds`, artifactIcon(path.join(ICONS, `${kind}.svg`)));
write('gfx/interface/icons/inventory/ck3xbl_horse.dds', dds(60, 60, frame(mask(path.join(ICONS, 'horse.svg'), 60), 60, [220, 210, 190])));
// Icons of the earlier hand-drawn set that no kind uses any more.
for (const old of ['ck3xbl_helmet', 'ck3xbl_boots', 'ck3xbl_cloak', 'ck3xbl_horse']) {
  if (!kinds.includes(old.replace('ck3xbl_', ''))) fs.rmSync(path.join(MOD, 'gfx', 'interface', 'icons', 'artifact', `${old}.dds`), { force: true });
}
