#!/usr/bin/env node
// Lints the proposed accolade records (tools/gen/knights/ck3_accolade_emit_snippet.txt) with the mod's own linter,
// entirely in memory: the snippet is added as an extra scripted-effects file and wired into in-memory copies of the
// snapshot / side / apply effects. Nothing under ck3_mod/ is written.
//
//   node tools/gen/knights/lint_accolade_proposal.mjs            # proposal wired in; problems grouped by message
//   node tools/gen/knights/lint_accolade_proposal.mjs --all      # every problem line
//   node tools/gen/knights/lint_accolade_proposal.mjs --base     # the unmodified mod, for comparison
//
// Known gaps of the current lint (it predates accolades), each of which the mod owner must add when adopting:
// BLOCK_TRIGGERS has no table for accolade_attribute_level, BLOCK_EFFECTS none for add_glory, and the record grammar
// does not list the new CX/AX fields. Those messages are counted as "expected"; anything else is a real finding.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { lintMod } from '../lint_ck3_mod.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../../..');
const MOD = path.join(ROOT, 'ck3_mod/ck3xbl');
const BOM = String.fromCharCode(0xfeff);
const read = (rel) => fs.readFileSync(path.join(MOD, ...rel.split('/')), 'utf8');

function wire(text, find, replace, file) {
  if (!text.includes(find)) throw new Error(`${file}: anchor not found: ${find}`);
  return text.split(find).join(replace);
}

const overrides = {};
if (!process.argv.includes('--base')) {
  let snippet = fs.readFileSync(path.join(HERE, 'ck3_accolade_emit_snippet.txt'), 'utf8');
  if (snippet.startsWith(BOM)) snippet = snippet.slice(1);
  overrides['common/scripted_effects/ck3xbl_knights_accolade_proposal.txt'] = BOM + snippet;

  const snap = 'common/scripted_effects/ck3xbl_snapshot_effects.txt';
  let s = read(snap);
  s = wire(s, '\tck3xbl_emit_cx_traits_effect = yes\n', '\tck3xbl_emit_cx_traits_effect = yes\n\tck3xbl_emit_accolade_effect = yes\n', snap);
  s = wire(s, 'army_owner = { ck3xbl_emit_ax_effect = yes }', 'army_owner = { ck3xbl_emit_ax_effect = yes }\n\t\tck3xbl_emit_army_uncontrolled_effect = yes', snap);
  overrides[snap] = s;

  const side = 'common/scripted_effects/ck3xbl_gen_side_effects.txt';
  overrides[side] = wire(read(side), 'army_owner = { ck3xbl_emit_ax_effect = yes }', 'army_owner = { ck3xbl_emit_ax_effect = yes }\n\t\t\tck3xbl_emit_army_uncontrolled_effect = yes', side);

  const apply = 'common/scripted_effects/ck3xbl_gen_apply_effects.txt';
  const calls = [[122, 123], [124, 125], [126, 127], [128, 129], [190, 191], [192, 193], [194, 195], [196, 197]]
    .map(([t, g]) => `\t\t\tck3xbl_apply_glory_slot_effect = { TAG = ck3xbl_r${t} GLORY = ck3xbl_r${g} }\n`).join('');
  overrides[apply] = wire(read(apply), '\t\t\tck3xbl_apply_char_slot_effect = { TAG = ck3xbl_r130 ', `${calls}\t\t\tck3xbl_apply_char_slot_effect = { TAG = ck3xbl_r130 `, apply);
}

const { problems, summary } = lintMod({ modDir: MOD, overrides });
const EXPECTED = [
  /no parameter table for trigger 'accolade_attribute_level'/,
  /unknown effect\/trigger\/keyword 'level'/,
  /no parameter table for effect 'add_glory'/,
  /info_log 'CK3XBL\|(CX\|acc_(active|rank|primary|type)|AX\|uncontrolled)\|[^']*' does not match the record grammar/,
];
const expected = problems.filter((p) => EXPECTED.some((re) => re.test(p.message)));
const real = problems.filter((p) => !expected.includes(p));
if (process.argv.includes('--all')) for (const p of problems) console.log(`${p.level}: ${p.file}:${p.line}: ${p.message}`);
else {
  const groups = new Map();
  for (const p of expected) {
    const k = p.message.replace(/'CK3XBL\|[^']*'/, "'CK3XBL|...'");
    groups.set(k, (groups.get(k) || 0) + 1);
  }
  for (const [k, n] of groups) console.log(`expected (lint gap) x${n}: ${k}`);
  for (const p of real) console.log(`${p.level}: ${p.file}:${p.line}: ${p.message}`);
}
console.log(summary);
console.log(`${real.length} finding(s) beyond the known lint gaps`);
process.exit(real.length ? 1 : 0);
