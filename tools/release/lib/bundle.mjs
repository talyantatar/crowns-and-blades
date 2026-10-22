// Reader for .NET single-file bundles (an apphost exe with the app's files appended, as `dotnet publish
// -p:PublishSingleFile=true` writes it). With -p:EnableCompressionInSingleFile=true the bundled files are deflated, so
// their strings cannot be read from the exe's raw bytes; the leak scan opens the bundle with this reader instead.
//
// Layout (format versions 1-6): the apphost holds the 32-byte bundle signature, preceded by the int64 offset of the bundle
// header. Header: uint32 major, uint32 minor, int32 file count, string bundle id; from major 2 on also the offset and size
// of deps.json and runtimeconfig.json and an int64 flags field. Then one record per file: int64 offset, int64 size,
// int64 compressed size (major 6 and later; 0 = stored), byte type, string relative path. Strings are UTF-8 with a 7-bit
// encoded length prefix.

import zlib from 'node:zlib';

// SHA-256 of ".net core bundle", the marker the SDK's HostWriter looks for in the apphost.
const SIGNATURE = Buffer.from([
  0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
  0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
]);

export const BUNDLE_FILE_TYPES = ['unknown', 'assembly', 'native', 'deps.json', 'runtimeconfig.json', 'symbols'];

/**
 * Reads a .NET single-file bundle.
 * @param {Buffer} buf  the whole exe
 * @returns {null | {major: number, minor: number, id: string, hostEnd: number,
 *                   files: {name: string, type: string, offset: number, size: number, compressed: number, read: () => Buffer}[]}}
 *   null when the file is not a single-file bundle (no signature, or an apphost that was never bundled). hostEnd: the
 *   lowest offset of a bundled file, i.e. where the apphost part of the exe ends.
 * @throws when the signature is found but the bundle is damaged
 */
export function readBundle(buf) {
  const at = buf.indexOf(SIGNATURE);
  if (at < 8) return null;
  const header = Number(buf.readBigInt64LE(at - 8));
  if (header === 0) return null;                       // an apphost that was never bundled
  if (header < 0 || header + 12 > buf.length) throw new Error('damaged single-file bundle (header offset out of range)');
  let p = header;
  const need = (n) => { if (p + n > buf.length) throw new Error('damaged single-file bundle (truncated header)'); };
  const str = () => {
    let len = 0;
    for (let shift = 0; ; shift += 7) {
      need(1);
      const b = buf[p++];
      len |= (b & 0x7f) << shift;
      if (!(b & 0x80)) break;
      if (shift > 21) throw new Error('damaged single-file bundle (string length)');
    }
    need(len);
    const s = buf.toString('utf8', p, p + len);
    p += len;
    return s;
  };
  need(12);
  const major = buf.readUInt32LE(p);
  const minor = buf.readUInt32LE(p + 4);
  const count = buf.readInt32LE(p + 8);
  p += 12;
  if (major < 1 || major > 6 || count < 0 || count > 100000) throw new Error(`unsupported single-file bundle (version ${major}.${minor}, ${count} files)`);
  const id = str();
  if (major >= 2) { need(40); p += 40; }               // deps.json and runtimeconfig.json locations, flags
  const files = [];
  let hostEnd = header;
  for (let i = 0; i < count; i++) {
    need(major >= 6 ? 25 : 17);
    const offset = Number(buf.readBigInt64LE(p));
    const size = Number(buf.readBigInt64LE(p + 8));
    p += 16;
    let compressed = 0;
    if (major >= 6) { compressed = Number(buf.readBigInt64LE(p)); p += 8; }
    const type = BUNDLE_FILE_TYPES[buf[p++]] || 'unknown';
    const name = str().replace(/\\/g, '/');
    const stored = compressed || size;
    if (offset < 0 || size < 0 || compressed < 0 || offset + stored > header) throw new Error(`damaged single-file bundle (entry ${name} out of range)`);
    hostEnd = Math.min(hostEnd, offset);
    const read = () => {
      const raw = buf.subarray(offset, offset + stored);
      if (!compressed) return Buffer.from(raw);
      const data = zlib.inflateRawSync(raw);
      if (data.length !== size) throw new Error(`damaged single-file bundle (entry ${name} inflates to ${data.length} bytes, expected ${size})`);
      return data;
    };
    files.push({ name, type, offset, size, compressed, read });
  }
  return { major, minor, id, hostEnd, files };
}
