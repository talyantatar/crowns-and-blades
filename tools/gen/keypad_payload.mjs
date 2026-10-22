#!/usr/bin/env node
// keypad_payload.mjs - reference encoder for the result payload (docs/PROTOCOL.md 2.2 / 2.3 / 5.8 / 10.3 / 10.4 / 14.2).
//
// Given register values, prints the checksum, the keypad key sequence (Ctrl+Shift+Alt held for every key) and the
// equivalent dev-mode run-file body. Used for in-game tests of the CK3 mod and as the reference the bridge's own
// encoder must match.
//
// Usage: node tools/gen/keypad_payload.mjs <n>=<value> [<n>=<value> ...] [--json]
//   r0 defaults to the payload version (3); r199 (checksum) is always computed for the version in r0
//   (0=2 builds a legacy version 2 payload with the plain-sum checksum; 0=4 a battle clock payload, PROTOCOL 14.2:
//   r8/r9 kill targets, r198 pace, r11+14i absolute levy survivors, the version 3 checksum).
//   Example (defender wins battle 3 at province 8199, army 12 loses half its levies and keeps 40% of its
//   armored_footmen (MaA index 48), character 5 is killed by character 2):
//     node tools/gen/keypad_payload.mjs 1=3 2=2 3=8199 4=300 5=900 6=7 10=12 11=500 12=48 13=400 130=5 131=4 132=2
//   The same battle steered by the battle clock (the attacker must count 900 dead, the defender 300; army 12 keeps 1054 levies):
//     node tools/gen/keypad_payload.mjs 0=4 1=3 2=2 3=8199 4=300 5=900 6=7 8=900 9=300 198=10 10=12 11=1054 12=48 13=400 130=5 131=4 132=2

import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { ACCEPTED_VERSIONS, REG, payloadChecksum } from './lib/registers.mjs';

export function encode(values) {
  const r = new Array(REG.count).fill(0);
  r[REG.version] = REG.payloadVersion;
  for (const [n, v] of Object.entries(values)) {
    const i = Number(n);
    if (!Number.isInteger(i) || i < 0 || i >= REG.checksum) throw new Error(`register ${n} outside 0..${REG.checksum - 1}`);
    if (!Number.isInteger(v) || v < 0 || v >= 1000000) throw new Error(`r${n} = ${v}: values are integers in 0..999999`);
    r[i] = v;
  }
  if (!ACCEPTED_VERSIONS.includes(r[REG.version])) {
    throw new Error(`r0 = ${r[REG.version]}: the mod accepts payload versions ${ACCEPTED_VERSIONS.join(', ')}`);
  }
  r[REG.checksum] = payloadChecksum(r, r[REG.version]);

  // F3 resets (idx = 0, acc = 0). Each non-zero register: seek if needed (digits + F5), then digits + F1 (store, idx += 1).
  const keys = ['F3'];
  let idx = 0;
  for (let i = 0; i < REG.count; i++) {
    if (r[i] === 0) continue;
    if (i !== idx) keys.push(...String(i), 'F5');
    keys.push(...String(r[i]), 'F1');
    idx = i + 1;
  }
  keys.push('F2');

  // The run file starts with the same reset as F3, so registers left over from an earlier attempt never count.
  const runFile = [
    'hidden_effect = {',
    '\tck3xbl_keypad_reset_effect = yes',
    ...r.map((v, i) => (v ? `\tset_variable = { name = ck3xbl_r${i} value = ${v} }` : null)).filter(Boolean),
    '\tck3xbl_commit_effect = yes',
    '\tck3xbl_apply_effect = yes',
    '}',
  ].join('\n');
  return { registers: r, checksum: r[REG.checksum], keys, runFile };
}

function main() {
  const args = process.argv.slice(2);
  const values = {};
  for (const a of args.filter((x) => !x.startsWith('--'))) {
    const m = /^(\d+)=(\d+)$/.exec(a);
    if (!m) throw new Error(`expected <n>=<value>, got '${a}'`);
    values[m[1]] = Number(m[2]);
  }
  const out = encode(values);
  if (args.includes('--json')) {
    console.log(JSON.stringify({ checksum: out.checksum, keys: out.keys, runFile: out.runFile }, null, 2));
    return;
  }
  console.log(`checksum r${REG.checksum} = ${out.checksum} (payload version ${out.registers[REG.version]})`);
  console.log(`keys (${out.keys.length}, each with Ctrl+Shift+Alt held): ${out.keys.join(' ')}`);
  console.log('dev-mode run file body (UTF-8 with BOM):');
  console.log(out.runFile);
}

// Run as a command; imported (by test_ck3_mod.mjs) it only exports encode().
if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) main();
