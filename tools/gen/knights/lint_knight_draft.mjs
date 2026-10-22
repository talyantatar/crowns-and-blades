#!/usr/bin/env node
// lint_knight_draft.mjs - runs the mod's own lint (tools/gen/lint_ck3_mod.mjs) over the DRAFT knight-effectiveness
// effect, entirely in memory: nothing under ck3_mod/ is read-modified-written.
//
// The draft (tools/gen/knights/draft/ck3xbl_knight_eff_effects.txt, from gen_knight_effectiveness.mjs --emit-ck3) is
// added as common/scripted_effects/ck3xbl_knight_eff_effects.txt, and a copy of the generated side effects calls it
// inside `army_owner = { ... }` of every A record, so the lint's scope pass checks it with a character (army owner)
// scope. Reports only problems the overrides add (the baseline lint of the real mod is subtracted).
//
// Usage: node tools/gen/knights/lint_knight_draft.mjs [<draft file>]

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { lintMod, DEFAULT_MOD } from '../lint_ck3_mod.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const draftFile = process.argv[2] || path.join(HERE, 'draft', 'ck3xbl_knight_eff_effects.txt');
const DRAFT_REL = 'common/scripted_effects/ck3xbl_knight_eff_effects.txt';
const SIDE_REL = 'common/scripted_effects/ck3xbl_gen_side_effects.txt';

const draft = fs.readFileSync(draftFile, 'utf8');
const side = fs.readFileSync(path.join(DEFAULT_MOD, ...SIDE_REL.split('/')), 'utf8');
const hook = 'army_owner = { ck3xbl_emit_ax_effect = yes }';
if (!side.includes(hook)) {
  console.error(`hook '${hook}' not found in ${SIDE_REL}; update lint_knight_draft.mjs`);
  process.exit(2);
}
const sideWithCall = side.split(hook).join('army_owner = { ck3xbl_emit_ax_effect = yes ck3xbl_save_knight_eff_effect = yes }');

const key = (p) => `${p.level}|${p.file}|${p.message}`;
const base = lintMod();
const baseKeys = new Set(base.problems.map(key));
const withDraft = lintMod({ overrides: { [DRAFT_REL]: draft, [SIDE_REL]: sideWithCall } });
const added = withDraft.problems.filter((p) => !baseKeys.has(key(p)));

for (const p of added) console.log(`${p.level}: ${p.file}:${p.line}: ${p.message}`);
console.log(`baseline: ${base.summary}`);
console.log(`with draft: ${withDraft.summary}`);
console.log(`${added.length} problem(s) added by the draft`);
process.exitCode = added.some((p) => p.level === 'error') ? 1 : 0;
