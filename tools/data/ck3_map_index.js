#!/usr/bin/env node
// ck3_map_index.js - prototype of the bridge's CK3 map reader (reference implementation for the C# bridge).
// Reads the USER'S CK3 install at runtime; writes only derived numbers (no game assets are copied).
//
//   node ck3_map_index.js [--game "<CK3 install>/game"]   (default: env CK3_GAME_DIR, else found in the Steam libraries)
//                         [--out <full index json>] [--slim-out <repo>/bridge/data/ck3_province_terrain_index.json]
//                         [--site <provinceId>[,<id>...]] [--site-out <dir>] [--patch <halfSizeInHeightmapSamples>]
//
// Coordinate conventions (verified):
//   image px  : x right, y DOWN, origin top-left of provinces.png (9216x4608). All "c/b/s/loc" values below are image px.
//   world     : locators/adjacencies use (x, z) with z UP from the bottom edge  ->  imageY = 4608 - z.
//   heightmap : heightmap.png is 18432x9216 (2 samples per image px), 16-bit gray, top-down, same orientation as provinces.png.
//   height    : world units = raw/65535*50 (NJominiMap.WORLD_EXTENTS_Y = 50); water level = 3 units = raw 3932.
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');
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
const G = arg('--game', null) || gameDirs.ck3GameDir();
const OUT = arg('--out', null);
const SITES = (arg('--site', '') || '').split(',').filter(Boolean).map(Number);
const SITE_OUT = arg('--site-out', null);
const PATCH = +arg('--patch', 16); // half size in heightmap samples (16 -> 33x33 samples ~ 16x16 map px)
const t0 = Date.now(); const lg = (...a) => console.error(((Date.now() - t0) / 1000).toFixed(1) + 's', ...a);

// ---------------- PNG decoder (non-interlaced gray8/16, rgb8, rgba8, palette8) ----------------
function readPng(file) {
  const buf = fs.readFileSync(file);
  if (buf.readUInt32BE(0) !== 0x89504e47) throw new Error('not a PNG: ' + file);
  let off = 8, ihdr = null, plte = null; const idats = [];
  while (off < buf.length) {
    const len = buf.readUInt32BE(off), type = buf.toString('ascii', off + 4, off + 8), data = buf.subarray(off + 8, off + 8 + len);
    if (type === 'IHDR') ihdr = { w: data.readUInt32BE(0), h: data.readUInt32BE(4), bd: data[8], ct: data[9], il: data[12] };
    else if (type === 'PLTE') plte = Buffer.from(data);
    else if (type === 'IDAT') idats.push(data);
    else if (type === 'IEND') break;
    off += 12 + len;
  }
  if (ihdr.il) throw new Error('interlaced PNG not supported');
  const chans = { 0: 1, 2: 3, 3: 1, 4: 2, 6: 4 }[ihdr.ct], bpp = Math.max(1, (chans * ihdr.bd) >> 3), stride = (ihdr.w * chans * ihdr.bd + 7) >> 3;
  const raw = zlib.inflateSync(Buffer.concat(idats), { chunkSize: 1 << 24 });
  const out = Buffer.allocUnsafe(stride * ihdr.h); let prev = Buffer.alloc(stride);
  for (let y = 0; y < ihdr.h; y++) {
    const ft = raw[y * (stride + 1)], src = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1)), dst = out.subarray(y * stride, (y + 1) * stride);
    if (ft === 0) src.copy(dst);
    else if (ft === 1) for (let i = 0; i < stride; i++) dst[i] = (src[i] + (i >= bpp ? dst[i - bpp] : 0)) & 255;
    else if (ft === 2) for (let i = 0; i < stride; i++) dst[i] = (src[i] + prev[i]) & 255;
    else if (ft === 3) for (let i = 0; i < stride; i++) dst[i] = (src[i] + (((i >= bpp ? dst[i - bpp] : 0) + prev[i]) >> 1)) & 255;
    else if (ft === 4) for (let i = 0; i < stride; i++) { const a = i >= bpp ? dst[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0, p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c); dst[i] = (src[i] + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c)) & 255; }
    else throw new Error('bad PNG filter ' + ft);
    prev = dst;
  }
  return { ihdr, plte, stride, data: out };
}
const stripComments = s => s.split(/\r?\n/).map(l => l.replace(/#.*/, '')).join('\n');

// ---------------- definition.csv + provinces.png -> id map ----------------
const defs = new Map(), names = {};
for (const line of fs.readFileSync(G + '/map_data/definition.csv', 'latin1').split(/\r?\n/)) {
  const m = line.match(/^(\d+);(\d+);(\d+);(\d+);([^;]*);/); if (!m || m[1] === '0') continue;
  defs.set((+m[2] << 16) | (+m[3] << 8) | +m[4], +m[1]); names[+m[1]] = m[5];
}
const prov = readPng(G + '/map_data/provinces.png');
const W = prov.ihdr.w, H = prov.ihdr.h, N = 65536;
const idmap = new Uint16Array(W * H);
const cnt = new Uint32Array(N), sx = new Float64Array(N), sy = new Float64Array(N);
const bb = new Int32Array(N * 4); for (let i = 0; i < N; i++) { bb[i * 4] = bb[i * 4 + 1] = 1e9; bb[i * 4 + 2] = bb[i * 4 + 3] = -1; }
for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
  const o = y * prov.stride + x * 3, id = defs.get((prov.data[o] << 16) | (prov.data[o + 1] << 8) | prov.data[o + 2]) || 0;
  idmap[y * W + x] = id; if (!id) continue; cnt[id]++; sx[id] += x; sy[id] += y;
  const b = id * 4; if (x < bb[b]) bb[b] = x; if (y < bb[b + 1]) bb[b + 1] = y; if (x > bb[b + 2]) bb[b + 2] = x; if (y > bb[b + 3]) bb[b + 3] = y;
}
lg('provinces.png', W, H, 'definitions', defs.size);

// ---------------- default.map classes, terrain, winter bias ----------------
const cls = {};
for (const l of stripComments(fs.readFileSync(G + '/map_data/default.map', 'latin1')).split('\n')) {
  const m = l.match(/^\s*(sea_zones|river_provinces|lakes|impassable_mountains|impassable_seas)\s*=\s*(RANGE|LIST)\s*\{([^}]*)\}/); if (!m) continue;
  const nums = m[3].trim().split(/\s+/).map(Number), set = cls[m[1]] || (cls[m[1]] = new Set());
  if (m[2] === 'RANGE') for (let i = nums[0]; i <= nums[1]; i++) set.add(i); else nums.forEach(n => set.add(n));
}
const kindOf = id => cls.sea_zones.has(id) ? 'sea' : cls.lakes.has(id) ? 'lake' : cls.river_provinces.has(id) ? 'river' : (cls.impassable_seas && cls.impassable_seas.has(id)) ? 'impassable_sea' : cls.impassable_mountains.has(id) ? 'impassable' : 'land';
const isWaterKind = k => k === 'sea' || k === 'lake' || k === 'river' || k === 'impassable_sea';
const terr = {}; let defLand = 'plains', defSea = 'sea';
for (const l of stripComments(fs.readFileSync(G + '/common/province_terrain/00_province_terrain.txt', 'latin1')).split('\n')) {
  let m = l.match(/^\s*(\d+)\s*=\s*([a-z_]+)/); if (m) terr[+m[1]] = m[2];
  m = l.match(/^\s*default_land\s*=\s*([a-z_]+)/); if (m) defLand = m[1];
  m = l.match(/^\s*default_sea\s*=\s*([a-z_]+)/); if (m) defSea = m[1];
}
const wbias = {}; { const t = stripComments(fs.readFileSync(G + '/common/province_terrain/01_province_properties.txt', 'latin1')); const re = /(\d+)\s*=\s*\{([^}]*)\}/g; let m; while ((m = re.exec(t))) { const b = m[2].match(/winter_severity_bias\s*=\s*([-\d.]+)/); if (b) wbias[+m[1]] = +b[1]; } }

// ---------------- rivers.png (palette idx: 0 source, 1 tributary-join, 2 split, 3..15 river width thin->wide, 254 water, 255 land) ----------------
const riv = readPng(G + '/map_data/rivers.png');
const rivAt = (x, y) => riv.data[y * riv.stride + x];
const isRiv = v => v >= 3 && v <= 15;
const riverPx = new Uint32Array(N);
for (let i = 0; i < W * H; i++) if (isRiv(riv.data[Math.floor(i / W) * riv.stride + (i % W)])) riverPx[idmap[i]]++;

// ---------------- neighbours + river-on-border ----------------
const nb = new Map();
const addPair = (a, b, r) => { if (a > b) { const t = a; a = b; b = t; } const k = a * 65536 + b; let e = nb.get(k); if (!e) nb.set(k, e = [0, 0]); e[0]++; if (r) e[1]++; };
for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
  const a = idmap[y * W + x];
  if (x + 1 < W) { const b = idmap[y * W + x + 1]; if (a !== b) addPair(a, b, isRiv(rivAt(x, y)) || isRiv(rivAt(x + 1, y))); }
  if (y + 1 < H) { const b = idmap[(y + 1) * W + x]; if (a !== b) addPair(a, b, isRiv(rivAt(x, y)) || isRiv(rivAt(x, y + 1))); }
}
const neigh = new Map();
for (const [k, e] of nb) { const a = Math.floor(k / 65536), b = k % 65536; if (!a || !b) continue; (neigh.get(a) || neigh.set(a, []).get(a)).push([b, e[0], e[1]]); (neigh.get(b) || neigh.set(b, []).get(b)).push([a, e[0], e[1]]); }
// river_large / sea adjacencies (crossings)
const adj = []; for (const l of fs.readFileSync(G + '/map_data/adjacencies.csv', 'latin1').split(/\r?\n/).slice(1)) { const p = l.split(';'); if (p.length < 4 || !/^\d+$/.test(p[0])) continue; adj.push({ from: +p[0], to: +p[1], type: p[2], through: +p[3], start: [+p[4], +p[5]], stop: [+p[6], +p[7]], comment: (p[8] || '').trim() }); }

// ---------------- heightmap.png (2x) ----------------
const hm = readPng(G + '/map_data/heightmap.png');
const HW = hm.ihdr.w, HH = hm.ihdr.h, HS = HW / W; // 2
const hraw = (hx, hy) => hm.data.readUInt16BE(Math.min(HH - 1, Math.max(0, hy)) * hm.stride + Math.min(HW - 1, Math.max(0, hx)) * 2);
const hAt = (x, y) => { let s = 0; for (let dy = 0; dy < HS; dy++) for (let dx = 0; dx < HS; dx++) s += hraw(HS * x + dx, HS * y + dy); return s / (HS * HS); }; // map-px average
const hmin = new Uint16Array(N).fill(65535), hmax = new Uint16Array(N), hsum = new Float64Array(N), hsq = new Float64Array(N);
for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) { const id = idmap[y * W + x]; if (!id) continue; const v = hAt(x, y); if (v < hmin[id]) hmin[id] = v; if (v > hmax[id]) hmax[id] = v; hsum[id] += v; hsq[id] += v * v; }
lg('heightmap', HW, HH);

// ---------------- locators ----------------
function readLocators(file) { const t = fs.readFileSync(G + '/gfx/map/map_object_data/' + file, 'latin1'); const re = /id\s*=\s*(\d+)\s*position\s*=\s*\{\s*([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s*\}/g; const m = new Map(); let r; while ((r = re.exec(t))) m.set(+r[1], [+(+r[2]).toFixed(2), +(H - +r[4]).toFixed(2)]); return m; }
const LB = readLocators('building_locators.txt'), LC = readLocators('combat_locators.txt'), LS = readLocators('siege_locators.txt');
function readTransforms(file) { const t = fs.readFileSync(file, 'latin1'); const out = []; const re = /name="([^"]*)"[\s\S]*?transform="([^"]*)"/g; let m; while ((m = re.exec(t))) for (const ln of m[2].split('\n')) { const p = ln.trim().split(/\s+/); if (p.length >= 3) out.push({ name: m[1], x: +p[0], y: H - +p[2] }); } return out; }
const bridges = readTransforms(G + '/gfx/map/map_object_data/bridges.txt');
const lakeObjs = readTransforms(G + '/gfx/map/map_object_data/lakes.txt'); // small visual lakes (meshes) that are not lake provinces

// ---------------- terrain materials: detail_index.tga / detail_intensity.tga (32bpp BGRA, uncompressed, bottom-up) ----------------
const ms = stripComments(fs.readFileSync(G + '/gfx/map/terrain/materials.settings', 'latin1'));
const mats = []; { let d = 0, b = ''; for (const ch of ms) { if (ch === '{') { d++; if (d === 2) b = ''; continue; } if (ch === '}') { if (d === 2) mats.push((b.match(/\bid\s*=\s*"([^"]+)"/) || b.match(/\bname\s*=\s*"([^"]+)"/) || [])[1] || '?'); d--; continue; } if (d === 2) b += ch; } }
const fdI = fs.openSync(G + '/gfx/map/terrain/detail_index.tga', 'r'), fdN = fs.openSync(G + '/gfx/map/terrain/detail_intensity.tga', 'r');
const tgaHdr = Buffer.alloc(18); fs.readSync(fdI, tgaHdr, 0, 18, 0);
if (tgaHdr[2] !== 2 || tgaHdr.readUInt16LE(12) !== W || tgaHdr.readUInt16LE(14) !== H || tgaHdr[16] !== 32) throw new Error('unexpected detail_index.tga header');
const tgaTopDown = (tgaHdr[17] & 0x20) !== 0;
const tgaRow = (fd, y, x0, n) => { const row = tgaTopDown ? y : H - 1 - y; const b = Buffer.alloc(n * 4); fs.readSync(fd, b, 0, n * 4, 18 + (row * W + x0) * 4); return b; };
function materialMix(cx, cy, r) { const acc = {}; let tot = 0; for (let y = Math.max(0, cy - r); y <= Math.min(H - 1, cy + r); y++) { const x0 = Math.max(0, cx - r), n = Math.min(W - 1, cx + r) - x0 + 1, bi = tgaRow(fdI, y, x0, n), bn = tgaRow(fdN, y, x0, n); for (let i = 0; i < n * 4; i++) { const w = bn[i]; if (!w) continue; const k = mats[bi[i]] || '#' + bi[i]; acc[k] = (acc[k] || 0) + w; tot += w; } } return Object.entries(acc).sort((a, b) => b[1] - a[1]).map(([k, v]) => [k, +(v / tot).toFixed(3)]); }
const matAcc = new Map();
for (let y = 0; y < H; y += 2) { const bi = tgaRow(fdI, y, 0, W), bn = tgaRow(fdN, y, 0, W); for (let x = 0; x < W; x += 2) { const id = idmap[y * W + x]; if (!id) continue; let a = matAcc.get(id); if (!a) matAcc.set(id, a = new Float64Array(256)); for (let c = 0; c < 4; c++) { const w = bn[x * 4 + c]; if (w) a[bi[x * 4 + c]] += w; } } }
lg('materials', mats.length);

// ---------------- trees (generated foliage objects) ----------------
const trees = new Map(); const tdir = G + '/gfx/map/map_object_data/generated/';
for (const f of fs.readdirSync(tdir)) { const kind = f.replace(/_generator.*$/, '').replace(/_\d+$/, ''); for (const t of readTransforms(tdir + f)) { const px = Math.floor(t.x), py = Math.floor(t.y); if (px < 0 || py < 0 || px >= W || py >= H) continue; const id = idmap[py * W + px]; let o = trees.get(id); if (!o) trees.set(id, o = {}); o[kind] = (o[kind] || 0) + 1; } }

// ---------------- colormap.dds (DXT5, top-down, mip0 at offset 128) ground tint ----------------
const fdC = fs.openSync(G + '/gfx/map/terrain/colormap.dds', 'r');
function colormapAt(x, y) { const bx = x >> 2, by = y >> 2, blk = Buffer.alloc(16); fs.readSync(fdC, blk, 0, 16, 128 + (by * (W >> 2) + bx) * 16); const c0 = blk.readUInt16LE(8), c1 = blk.readUInt16LE(10), idx = (blk.readUInt32LE(12) >>> (2 * ((y & 3) * 4 + (x & 3)))) & 3; const rgb = c => [((c >> 11) & 31) * 255 / 31, ((c >> 5) & 63) * 255 / 63, (c & 31) * 255 / 31]; const a = rgb(c0), b = rgb(c1); const col = idx === 0 ? a : idx === 1 ? b : idx === 2 ? a.map((v, i) => (2 * v + b[i]) / 3) : a.map((v, i) => (v + 2 * b[i]) / 3); return col.map(Math.round); }

// ---------------- material -> semantic ground class (first match wins) ----------------
const CLASS_RULES = [['snow', /snow|winter/], ['forest', /forest|jungle/], ['farmland', /farm|paddy/], ['wet', /wetland|mud_wet|flood|oasis/],
  ['rock', /rock|cliff|mountain/], ['sand', /desert|beach/], ['steppe', /steppe/], ['dry', /dryland|dry|cracked|drought/], ['grass', /./]];
const classOf = name => (CLASS_RULES.find(([, re]) => re.test(name)) || ['grass'])[0];

// ---------------- per-province records ----------------
function nearest(cx, cy, maxR, pred) { for (let r = 0; r <= maxR; r++) for (let dy = -r; dy <= r; dy++) for (let dx = -r; dx <= r; dx++) { if (Math.max(Math.abs(dx), Math.abs(dy)) !== r) continue; const x = cx + dx, y = cy + dy; if (x < 0 || y < 0 || x >= W || y >= H) continue; const v = pred(x, y); if (v !== false && v !== undefined) return { d: +Math.hypot(dx, dy).toFixed(1), v, dir: Math.round((Math.atan2(-dy, dx) * 180 / Math.PI + 360) % 360) }; } return null; }
function localStats(cx, cy, r) { let mn = 65535, mx = 0, gs = 0, n = 0, gxs = 0, gys = 0; for (let y = cy - r; y <= cy + r; y++) for (let x = cx - r; x <= cx + r; x++) { if (x < 1 || y < 1 || x >= W - 1 || y >= H - 1) continue; const v = hAt(x, y); mn = Math.min(mn, v); mx = Math.max(mx, v); const gx = (hAt(x + 1, y) - hAt(x - 1, y)) / 2, gy = (hAt(x, y + 1) - hAt(x, y - 1)) / 2; gs += Math.hypot(gx, gy); gxs += gx; gys += gy; n++; } return { relief: Math.round(mx - mn), slope: Math.round(gs / n), downhillDir: Math.round((Math.atan2(gys, -gxs) * 180 / Math.PI + 360) % 360) }; }
const recs = {};
for (let id = 1; id < N; id++) {
  if (!cnt[id]) continue; const kind = kindOf(id);
  const r = { name: names[id], kind, terrain: terr[id] || (isWaterKind(kind) ? defSea : defLand), px: cnt[id], c: [+(sx[id] / cnt[id]).toFixed(1), +(sy[id] / cnt[id]).toFixed(1)], bbox: [bb[id * 4], bb[id * 4 + 1], bb[id * 4 + 2], bb[id * 4 + 3]] };
  const m = hsum[id] / cnt[id]; r.h = { min: Math.round(hmin[id]), mean: Math.round(m), max: Math.round(hmax[id]), sd: Math.round(Math.sqrt(Math.max(0, hsq[id] / cnt[id] - m * m))) };
  if (kind === 'land' || kind === 'impassable') {
    if (LB.has(id)) r.b = LB.get(id); if (LC.has(id)) r.cb = LC.get(id); if (LS.has(id)) r.sg = LS.get(id);
    r.winterBias = wbias[id] !== undefined ? wbias[id] : null;
    const ns = neigh.get(id) || []; r.nb = ns;
    r.coastal = ns.some(n => kindOf(n[0]) === 'sea'); r.lakeside = ns.some(n => kindOf(n[0]) === 'lake'); r.majorRiverside = ns.some(n => kindOf(n[0]) === 'river');
    r.riverPx = riverPx[id];
    const at = (r.cb || r.b || r.c).map(Math.round), [ax, ay] = at;
    const ls = localStats(ax, ay, 3); r.local = { hAt: Math.round(hAt(ax, ay)), relief7: ls.relief, slope7: ls.slope, downhillDir: ls.downhillDir };
    const rv = nearest(ax, ay, 6, (x, y) => { const v = rivAt(x, y); return isRiv(v) ? v - 2 : false; }); if (rv) r.local.river = { dist: rv.d, width: rv.v, dir: rv.dir }; // width 1..13
    const wv = nearest(ax, ay, 8, (x, y) => { const k = kindOf(idmap[y * W + x]); return isWaterKind(k) ? k : false; }); if (wv) r.local.water = { dist: wv.d, kind: wv.v, dir: wv.dir };
    const br = bridges.filter(b => Math.hypot(b.x - ax, b.y - ay) <= 6); if (br.length) r.local.bridges = br.length;
    const lk = lakeObjs.filter(b => Math.hypot(b.x - ax, b.y - ay) <= 6); if (lk.length) r.local.lakeObjects = lk.length;
    const mix = materialMix(ax, ay, 2); r.local.mats = mix.slice(0, 5); r.local.groundRgb = colormapAt(ax, ay);
    const cl = {}; for (const [k, w] of mix) { const c = classOf(k); cl[c] = +((cl[c] || 0) + w).toFixed(3); } r.local.classes = cl;
    const a = matAcc.get(id); if (a) { let tot = 0; for (const v of a) tot += v; r.mats = [...a].map((v, i) => [mats[i], v]).filter(e => e[1] > 0).sort((p, q) => q[1] - p[1]).slice(0, 5).map(([k, v]) => [k, +(v / tot).toFixed(2)]); }
    const t = trees.get(id); if (t) { r.trees = t; r.treeDensity = +(Object.values(t).reduce((p, q) => p + q, 0) / cnt[id]).toFixed(3); } else r.treeDensity = 0;
  }
  recs[id] = r;
}
lg('records', Object.keys(recs).length);

if (OUT) {
  const meta = { generatedFrom: '<CK3 install>/game', generated: new Date().toISOString(), mapW: W, mapH: H, heightmapW: HW, heightmapH: HH, heightRawMax: 65535, worldHeightUnits: 50, waterLevelRaw: 3932,
    coords: 'image px, origin top-left, y down. Locators converted from world (x,z): y = mapH - z. squared_distance in CK3 script is in these px.',
    fields: { c: 'pixel centroid', b: 'building (holding) locator', cb: 'combat locator', sg: 'siege locator', nb: '[neighbourId, borderPx, borderPxTouchingRiver]', h: 'heightmap raw (0..65535, map-px averaged) over province', local: 'around combat locator: hAt raw, relief7/slope7 over 7x7 map px, river {dist px, width 1..13, dir deg CCW from east}, water {dist,kind,dir}, bridges within 6px, mats r=2, groundRgb from colormap.dds', mats: 'top terrain materials over whole province', trees: 'generated tree objects per kind', treeDensity: 'tree objects per province px' },
    materials: mats, adjacencies: adj };
  fs.writeFileSync(OUT, JSON.stringify({ meta, provinces: recs }));
  lg('wrote', OUT, (fs.statSync(OUT).size / 1e6).toFixed(1) + ' MB');
}

// ---------------- slim table (land + impassable only; numbers only) ----------------
const SLIM = arg('--slim-out', null);
if (SLIM) {
  const terrains = [...new Set(Object.values(recs).map(r => r.terrain))].sort();
  const classes = CLASS_RULES.map(c => c[0]); const waterKinds = ['sea', 'lake', 'river', 'impassable_sea'];
  const cols = ['id', 'terrain', 'combatX', 'combatY', 'siegeX', 'siegeY', 'winterBias', 'flags(1 coastal|2 lakeside|4 majorRiverside|8 impassable)',
    'relief7Raw', 'slope7Raw', 'hAtMinusWaterRaw', 'downhillDirDeg', 'treesPer1000px', 'riverDistPx(-1 none<=6)', 'riverWidth1to13', 'waterDistPx(-1 none<=8)', 'waterKind', 'bridgesWithin6px', 'lakeObjectsWithin6px', ...classes.map(c => c + '%')];
  const rowsOut = [];
  for (const [id, r] of Object.entries(recs)) {
    if (r.kind !== 'land' && r.kind !== 'impassable') continue; const L = r.local || {};
    const flags = (r.coastal ? 1 : 0) | (r.lakeside ? 2 : 0) | (r.majorRiverside ? 4 : 0) | (r.kind === 'impassable' ? 8 : 0);
    const at = r.cb || r.b || r.c, sg = r.sg || r.b || at;
    rowsOut.push([+id, terrains.indexOf(r.terrain), Math.round(at[0]), Math.round(at[1]), Math.round(sg[0]), Math.round(sg[1]), r.winterBias, flags,
      L.relief7, L.slope7, L.hAt - 3932, L.downhillDir, Math.round(r.treeDensity * 1000), L.river ? Math.round(L.river.dist) : -1, L.river ? L.river.width : 0,
      L.water ? Math.round(L.water.dist) : -1, L.water ? waterKinds.indexOf(L.water.kind) : -1, L.bridges || 0, L.lakeObjects || 0, ...classes.map(c => Math.round(100 * ((L.classes || {})[c] || 0)))]);
  }
  const SRC = ['map_data/provinces.png', 'map_data/definition.csv', 'map_data/heightmap.png', 'map_data/rivers.png', 'map_data/default.map', 'map_data/adjacencies.csv', 'common/province_terrain/00_province_terrain.txt', 'common/province_terrain/01_province_properties.txt', 'gfx/map/terrain/detail_index.tga', 'gfx/map/terrain/detail_intensity.tga', 'gfx/map/terrain/materials.settings', 'gfx/map/map_object_data/combat_locators.txt'];
  const sources = Object.fromEntries(SRC.map(p => { const st = fs.statSync(G + '/' + p); return [p, [st.size, Math.round(st.mtimeMs)]]; }));
  fs.writeFileSync(SLIM, JSON.stringify({ meta: { generatedFrom: '<CK3 install>/game', generated: new Date().toISOString(), sources, note: `Derived numbers only, regenerate with ${SELF}.` + ' Coords are provinces.png px (y down). Heights are raw heightmap units (0..65535; water level 3932).', terrains, classes, waterKinds, cols }, rows: rowsOut }).replace(/\],\[/g, '],\n['));
  lg('wrote slim', SLIM, (fs.statSync(SLIM).size / 1e6).toFixed(2) + ' MB', rowsOut.length, 'rows');
}

// ---------------- battle-site descriptor(s) ----------------
for (const id of SITES) {
  const r = recs[id]; if (!r) { console.error('no province', id); continue; }
  const [ax, ay] = (r.cb || r.b || r.c).map(Math.round); const hx = ax * HS, hy = ay * HS;
  const patch = []; for (let y = -PATCH; y <= PATCH; y++) { const row = []; for (let x = -PATCH; x <= PATCH; x++) row.push(hraw(hx + x, hy + y)); patch.push(row); }
  const core = []; for (let y = -2; y <= 2; y++) for (let x = -2; x <= 2; x++) core.push(hraw(hx + x, hy + y));
  const site = { provinceId: id, province: r, center: { mapPx: [ax, ay], heightmapPx: [hx, hy] },
    heightPatch: { sizeSamples: 2 * PATCH + 1, sampleSpacingMapPx: 1 / HS, approxKmPerSample: '0.65-1.0 (see report)', rawMinusWater: patch.map(row => row.map(v => v - 3932)) },
    core5x5: { min: Math.min(...core), max: Math.max(...core), relief: Math.max(...core) - Math.min(...core) },
    neighbours: (r.nb || []).map(([n, border, rb]) => ({ id: n, name: recs[n] && recs[n].name, kind: recs[n] && recs[n].kind, terrain: recs[n] && recs[n].terrain, border, riverBorder: rb })),
    crossings: adj.filter(a => a.from === id || a.to === id) };
  const s = JSON.stringify(site);
  if (SITE_OUT) { fs.writeFileSync(path.join(SITE_OUT, 'ck3_site_' + id + '.json'), s); lg('wrote site', id); } else console.log(s);
}
