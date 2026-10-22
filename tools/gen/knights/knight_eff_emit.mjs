// knight_eff_emit.mjs - renders the CK3 scripted effect that saves a liege's knight effectiveness in permille.
//
// One renderer for both users of bridge/data/ck3_knight_effectiveness_sources.json:
//   gen_knight_effectiveness.mjs --emit-ck3   the DRAFT file
//   tools/gen/gen_ck3_mod.mjs                 the mod's ck3xbl_gen_knight_effects.txt, which also clears the helper scopes at the
//                                             end of the effect: saved scopes, temporary ones included, are printed by every later
//                                             info_log_scopes dump of the effect run (in game, 2026-10-04), and clear_saved_scope
//                                             removes them
// this = the liege (army owner of the knights). The effect saves value ck3xbl_a_ke = 1000 + the sum of every testable source
// and the scopes the source triggers read (ck3xbl_kl, holder, councillor_liege = the liege).

const round6 = (v) => Math.round(v * 1e6) / 1e6;
const permille = (v) => round6(v * 1000);

/** Helper scopes the source triggers read: ck3xbl_kl (artifacts, holy sites), holder (building is_enabled), councillor_liege. */
const KNIGHT_EFF_HELPER_SCOPES = ['ck3xbl_kl', 'holder', 'councillor_liege'];

/**
 * Lines of `ck3xbl_save_knight_eff_effect = { ... }` (tab-indented from depth 0) for the source records. `clearHelpers` adds a
 * clear_saved_scope for every helper scope after the value is saved.
 */
export function knightEffEffectLines(records, { clearHelpers = false } = {}) {
  const L = [];
  L.push('ck3xbl_save_knight_eff_effect = {');
  for (const name of KNIGHT_EFF_HELPER_SCOPES) L.push(`\tsave_scope_as = ${name}`);
  L.push('\tsave_scope_value_as = {');
  L.push('\t\tname = ck3xbl_a_ke');
  L.push('\t\tvalue = {');
  L.push('\t\t\tvalue = 1000');
  const T = '\t\t\t';
  for (const r of records) {
    if (!r.affects_knight_effectiveness || r.scope_owner === 'knight' || r.testable === 'no' || !r.trigger) continue;
    if (r.subkind === 'holy_site_faith_scaled' && !r.scale?.liege_scope_expr) continue;
    L.push(`${T}# ${r.id} (${r.source_file})`);
    const terms = [];
    if (r.mult !== null && r.mult !== 0) terms.push({ m: permille(r.mult), skill: null });
    for (const p of r.per_skill) terms.push({ m: permille(p.value), skill: p.skill });
    for (const t of terms) {
      const factor = t.skill ? `{ value = scope:ck3xbl_kl.${t.skill} multiply = ${t.m} }` : `${t.m}`;
      const times = t.skill ? `multiply = scope:ck3xbl_kl.${t.skill} multiply = ${t.m}` : `multiply = ${t.m}`;
      if (r.subkind === 'council_task') {
        L.push(`${T}every_councillor = { limit = { is_performing_council_task = ${r.key} } add = { ${r.scale.script_value} ${times} } }`);
      } else if (r.scale && r.scale.liege_scope_expr) {
        const e = r.scale.liege_scope_expr;
        const body = e.startsWith('{') ? e.slice(1, -1).trim() : `value = ${e}`;
        L.push(`${T}if = { limit = { ${r.trigger} } add = { ${body} ${times} } }`);
      } else if (r.count) {
        const inner = r.count.replace(/add = 1 \}$/, `add = ${factor} }`);
        L.push(r.count_condition ? `${T}if = { limit = { ${r.count_condition} } ${inner} }` : `${T}${inner}`);
      } else {
        L.push(`${T}if = { limit = { ${r.trigger} } add = ${factor} }`);
      }
    }
  }
  L.push('\t\t}');
  L.push('\t}');
  if (clearHelpers) for (const name of KNIGHT_EFF_HELPER_SCOPES) L.push(`\tclear_saved_scope = ${name}`);
  L.push('}');
  return L;
}
