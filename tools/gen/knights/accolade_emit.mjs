// accolade_emit.mjs - renders the CK3 scripted effect that logs a battle character's accolade (CX|acc_* records,
// docs/PROTOCOL.md 13.2). Shared by gen_ck3_accolade_types.mjs (its proposal snippet) and tools/gen/gen_ck3_mod.mjs (the mod's
// ck3xbl_gen_knight_effects.txt), so both always print the same script.
// Script names: script_docs (is_acclaimed triggers.log:4970, accolade event_targets.log:372,
// accolade_owner/acclaimed_knight/initial_type event_targets.log:1482-1526, any_active_accolade triggers.log:1250,
// accolade_rank 11436, has_accolade_type 11455, accolade_attribute_level 11414).

/** Highest accolade rank (NAccolade.ACCOLADE_GLORY_LEVELS has 6 thresholds) and highest attribute level. */
export const ACCOLADE_MAX_RANK = 6;
export const ACCOLADE_MAX_LEVEL = 3;

/**
 * Lines of `ck3xbl_emit_accolade_effect = { ... }` (tab-indented from depth 0). this = the character of a C record, right
 * after its CX lines. `typeKeys` = the accolade type keys (bridge/data/ck3_accolade_types.json types). `clearKnight` (the mod)
 * saves the knight with save_scope_as and clears it after the accolade lines, so it is not printed by the later dumps of the
 * snapshot (saved scopes, temporary ones included, persist for the whole effect run; in game, 2026-10-04).
 */
export function accoladeEmitEffectLines(typeKeys, { clearKnight = false } = {}) {
  const L = [];
  L.push('ck3xbl_emit_accolade_effect = {');
  L.push('\tif = {');
  L.push('\t\tlimit = { is_acclaimed = yes }');
  L.push(`\t\t${clearKnight ? 'save_scope_as' : 'save_temporary_scope_as'} = ck3xbl_acc_knight`);
  L.push('\t\taccolade = {');
  L.push('\t\t\tif = {');
  L.push('\t\t\t\tlimit = { accolade_owner ?= { any_active_accolade = { acclaimed_knight ?= scope:ck3xbl_acc_knight } } }');
  L.push('\t\t\t\tinfo_log = "CK3XBL|CX|acc_active|yes"');
  L.push('\t\t\t}');
  L.push('\t\t\telse = { info_log = "CK3XBL|CX|acc_active|no" }');
  for (let r = ACCOLADE_MAX_RANK; r >= 1; r--) {
    L.push(`\t\t\t${r === ACCOLADE_MAX_RANK ? 'if' : 'else_if'} = { limit = { accolade_rank >= ${r} } info_log = "CK3XBL|CX|acc_rank|${r}" }`);
  }
  L.push('\t\t\telse = { info_log = "CK3XBL|CX|acc_rank|0" }');
  L.push('\t\t\tinitial_type ?= {');
  typeKeys.forEach((t, i) => L.push(`\t\t\t\t${i === 0 ? 'if' : 'else_if'} = { limit = { this = accolade_type:${t} } info_log = "CK3XBL|CX|acc_primary|${t}" }`));
  L.push('\t\t\t}');
  for (const t of typeKeys) {
    L.push('\t\t\tif = {');
    L.push(`\t\t\t\tlimit = { has_accolade_type = ${t} }`);
    for (let lv = ACCOLADE_MAX_LEVEL; lv >= 1; lv--) {
      L.push(`\t\t\t\t${lv === ACCOLADE_MAX_LEVEL ? 'if' : 'else_if'} = { limit = { accolade_attribute_level = { type = ${t} level >= ${lv} } } info_log = "CK3XBL|CX|acc_type|${t}|${lv}" }`);
    }
    L.push(`\t\t\t\telse = { info_log = "CK3XBL|CX|acc_type|${t}|0" }`);
    L.push('\t\t\t}');
  }
  L.push('\t\t}');
  if (clearKnight) L.push('\t\tclear_saved_scope = ck3xbl_acc_knight');
  L.push('\t}');
  L.push('}');
  return L;
}
