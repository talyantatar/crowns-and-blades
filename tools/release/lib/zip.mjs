// Minimal, dependency-free ZIP writer and reader for the release tools.
// Writer: deterministic output (entries in the given order, one fixed timestamp, no extra fields), deflate or store,
// forward-slash names, UTF-8 flag set. Reader: central-directory based, deflate and store, no ZIP64 or encryption
// (the release archives are far below 4 GB). Used to build the manual zip and the installer payload, and by the
// leak scan to look inside archives.

import zlib from 'node:zlib';

const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();

/** CRC-32 (zlib.crc32 where available, Node >= 22.2). */
export function crc32(buf) {
  if (typeof zlib.crc32 === 'function') return zlib.crc32(buf) >>> 0;
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

/** DOS date/time words for a Date, from its UTC fields (so the archive does not depend on the time zone of the build). */
function dosDateTime(date) {
  const y = Math.max(1980, date.getUTCFullYear());
  const time = (date.getUTCHours() << 11) | (date.getUTCMinutes() << 5) | Math.floor(date.getUTCSeconds() / 2);
  const day = ((y - 1980) << 9) | ((date.getUTCMonth() + 1) << 5) | date.getUTCDate();
  return { time, day };
}

/**
 * Builds a ZIP archive.
 * @param {{name: string, data: Buffer}[]} entries  file entries; names use '/', no leading slash, no '..'
 * @param {{date?: Date, level?: number}} [options]  date: the timestamp of every entry (default 2026-01-01 00:00 UTC)
 * @returns {Buffer}
 */
export function writeZip(entries, { date = new Date(Date.UTC(2026, 0, 1)), level = 9 } = {}) {
  const { time, day } = dosDateTime(date);
  const locals = [];
  const centrals = [];
  let offset = 0;
  const seen = new Set();
  for (const { name, data } of entries) {
    if (!name || name.startsWith('/') || name.includes('\\') || name.split('/').includes('..')) throw new Error(`bad zip entry name: ${name}`);
    if (seen.has(name.toLowerCase())) throw new Error(`duplicate zip entry: ${name}`);
    seen.add(name.toLowerCase());
    const nameBuf = Buffer.from(name, 'utf8');
    const crc = crc32(data);
    const deflated = zlib.deflateRawSync(data, { level });
    const useDeflate = deflated.length < data.length;
    const body = useDeflate ? deflated : data;
    const method = useDeflate ? 8 : 0;

    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);            // version needed
    local.writeUInt16LE(0x0800, 6);        // UTF-8 names
    local.writeUInt16LE(method, 8);
    local.writeUInt16LE(time, 10);
    local.writeUInt16LE(day, 12);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(body.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(nameBuf.length, 26);
    local.writeUInt16LE(0, 28);
    locals.push(local, nameBuf, body);

    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(20, 4);          // version made by (MS-DOS, 2.0)
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0x0800, 8);
    central.writeUInt16LE(method, 10);
    central.writeUInt16LE(time, 12);
    central.writeUInt16LE(day, 14);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(body.length, 20);
    central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(nameBuf.length, 28);
    central.writeUInt16LE(0, 30);          // extra
    central.writeUInt16LE(0, 32);          // comment
    central.writeUInt16LE(0, 34);          // disk
    central.writeUInt16LE(0, 36);          // internal attributes
    central.writeUInt32LE(0, 38);          // external attributes
    central.writeUInt32LE(offset, 42);
    centrals.push(central, nameBuf);

    offset += 30 + nameBuf.length + body.length;
  }
  const centralBuf = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(centralBuf.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, centralBuf, end]);
}

/**
 * Finds the ZIP archive in a buffer that is, or ends with, one (a self-extracting exe; a short trailer after the archive
 * is allowed). Offsets inside an appended archive are relative to its own start.
 * @param {Buffer} buf
 * @returns {{base: number, start: number, end: number, eocd: number, count: number, cdOffset: number}}
 *   base: where offset 0 of the archive is; start: its first byte (the lowest local header); end: one past its end of
 *   central directory record (with comment). The bytes outside [start, end) are not part of the archive.
 */
export function locateZip(buf) {
  let eocd = -1;
  for (let i = buf.length - 22; i >= Math.max(0, buf.length - 22 - 0xffff); i--) {
    if (buf.readUInt32LE(i) === 0x06054b50 && i + 22 + buf.readUInt16LE(i + 20) <= buf.length) { eocd = i; break; }
  }
  if (eocd < 0) throw new Error('not a zip archive (no end of central directory)');
  const count = buf.readUInt16LE(eocd + 10);
  const cdSize = buf.readUInt32LE(eocd + 12);
  const cdOffset = buf.readUInt32LE(eocd + 16);
  // Archives appended to another file: offsets are relative to the start of the archive.
  const base = eocd - cdSize - cdOffset;
  if (base < 0) throw new Error('corrupt zip archive (central directory out of range)');
  let start = base + cdOffset;
  let p = base + cdOffset;
  for (let n = 0; n < count && p + 46 <= eocd; n++) {
    if (buf.readUInt32LE(p) !== 0x02014b50) throw new Error('corrupt zip archive (central directory entry)');
    start = Math.min(start, base + buf.readUInt32LE(p + 42));
    p += 46 + buf.readUInt16LE(p + 28) + buf.readUInt16LE(p + 30) + buf.readUInt16LE(p + 32);
  }
  return { base, start, end: eocd + 22 + buf.readUInt16LE(eocd + 20), eocd, count, cdOffset };
}

/**
 * Reads a ZIP archive (or a file that ends with one, such as a self-extracting exe).
 * @param {Buffer} buf
 * @returns {{name: string, data: Buffer}[]}  file entries (directories skipped), in central-directory order
 */
export function readZip(buf) {
  const { base, count, cdOffset } = locateZip(buf);
  const out = [];
  let p = base + cdOffset;
  for (let n = 0; n < count; n++) {
    if (buf.readUInt32LE(p) !== 0x02014b50) throw new Error('corrupt zip archive (central directory entry)');
    const flags = buf.readUInt16LE(p + 8);
    const method = buf.readUInt16LE(p + 10);
    const crc = buf.readUInt32LE(p + 16);
    const csize = buf.readUInt32LE(p + 20);
    const usize = buf.readUInt32LE(p + 24);
    const nlen = buf.readUInt16LE(p + 28);
    const xlen = buf.readUInt16LE(p + 30);
    const clen = buf.readUInt16LE(p + 32);
    const lho = buf.readUInt32LE(p + 42);
    const name = buf.toString(flags & 0x0800 ? 'utf8' : 'latin1', p + 46, p + 46 + nlen);
    p += 46 + nlen + xlen + clen;
    if (name.endsWith('/')) continue;
    if (flags & 1) throw new Error(`encrypted zip entry: ${name}`);
    const lh = base + lho;
    if (buf.readUInt32LE(lh) !== 0x04034b50) throw new Error(`corrupt zip archive (local header of ${name})`);
    const start = lh + 30 + buf.readUInt16LE(lh + 26) + buf.readUInt16LE(lh + 28);
    const raw = buf.subarray(start, start + csize);
    let data;
    if (method === 0) data = Buffer.from(raw);
    else if (method === 8) data = zlib.inflateRawSync(raw);
    else throw new Error(`unsupported zip compression method ${method}: ${name}`);
    if (data.length !== usize || crc32(data) !== crc) throw new Error(`zip entry failed its CRC check: ${name}`);
    out.push({ name: name.replace(/\\/g, '/'), data });
  }
  return out;
}
