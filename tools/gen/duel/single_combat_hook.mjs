// single_combat_hook.mjs - the mod's hook into CK3's single combat (docs/PROTOCOL.md 18).
//
// Every single combat in CK3 starts with configure_start_single_combat_effect (vanilla
// common/scripted_effects/00_single_combat_effects.txt, called from about 75 places). The mod redefines it, loaded after vanilla:
//   1. the head of the vanilla effect, which turns its parameters into saved scopes (sc_initiator, sc_attacker, sc_defender,
//      follow_up_event, invalidation_event), plus fatality, fixed and locale, saved here before the branch;
//   2. a duel a human player fights (ck3xbl_duel_wanted_trigger) goes to the mod (ck3xbl_duel_begin_effect), everything else
//      to ck3xbl_sc_setup_effect: the rest of the vanilla effect from its invalidation check on, unchanged except that the three
//      parameter values are read from the saved scopes. The mod calls the same effect after the Bannerlord duel, with scope:fixed
//      (and scope:fatality) set by the result.
// The vanilla text is read from the CK3 install at generation time, so a CK3 patch is picked up by regenerating.

const MARK = '# Invalidation check';

/** The block `name = { ... }` starting at the line where `name = {` begins, as lines. */
function block(lines, name) {
  const start = lines.findIndex((l) => new RegExp(`^${name}\\s*=\\s*\\{`).test(l));
  if (start < 0) throw new Error(`single combat hook: ${name} not found in the vanilla file`);
  let depth = 0;
  for (let i = start; i < lines.length; i++) {
    for (const ch of lines[i].replace(/#.*$/, '')) {
      if (ch === '{') depth++;
      else if (ch === '}') depth--;
    }
    if (depth === 0) return lines.slice(start, i + 1);
  }
  throw new Error(`single combat hook: ${name} is unbalanced`);
}

/** Removes `save_scope_value_as = { name = <n> value = flag:$<P>$ }` (one line or several) for the three rule parameters. */
function dropRuleSaves(text) {
  let out = text;
  for (const [name, param] of [['fatality', 'FATALITY'], ['fixed', 'FIXED'], ['locale', 'LOCALE']]) {
    const rx = new RegExp(`[ \\t]*save_scope_value_as\\s*=\\s*\\{\\s*name\\s*=\\s*${name}\\s*value\\s*=\\s*flag:\\$${param}\\$\\s*\\}[ \\t]*\\r?\\n`);
    if (!rx.test(out)) throw new Error(`single combat hook: the vanilla save of scope:${name} changed shape`);
    out = out.replace(rx, '');
  }
  return out;
}

/** Duels in Bannerlord are switched on (false: vanilla single combat untouched, the mod's duel effects unreachable). */
export const DUELS_ENABLED = true;


/** Lines of the hook file (without the generated header). */
export function singleCombatHookLines(vanillaText) {
  if (!DUELS_ENABLED) {
    return ['# Duels in Bannerlord are switched off (tools/gen/duel/single_combat_hook.mjs DUELS_ENABLED): vanilla single combat',
      '# stays untouched, and the mod\'s duel effects are never reached.', 'ck3xbl_sc_setup_effect = {', '}'];
  }
  const lines = vanillaText.replace(/^﻿/, '').split(/\r?\n/);
  const body = block(lines, 'configure_start_single_combat_effect');
  const at = body.findIndex((l) => l.includes(MARK));
  if (at < 0) throw new Error('single combat hook: no invalidation check in the vanilla effect');
  // The rest of the vanilla effect, from the invalidation check to its closing brace (exclusive), as vanilla indents it.
  const tail = dropRuleSaves(body.slice(at, -1).join('\n')).split('\n');
  const params = tail.join('\n').match(/\$[A-Z_]+\$/g);
  if (params) throw new Error(`single combat hook: parameters left in the vanilla tail: ${[...new Set(params)].join(', ')}`);
  // After a Bannerlord duel CK3 must not fight its rounds again (the player would pick moves until sudden death): the same tail,
  // but instead of the first round it lowers the success threshold out of reach of nobody and ends round one at once, so the
  // fixed winner wins it (single_combat.0021 checks the threshold, skipping the side the fix rules out) and vanilla's victory and
  // defeat events, wounds, death and output event follow.
  const roundStart = tail.findIndex((l) => l.trim() === FIRST_ROUND);
  if (roundStart < 0) throw new Error(`single combat hook: the vanilla tail no longer starts round one with "${FIRST_ROUND}"`);
  const ind = tail[roundStart].match(/^\s*/)[0];
  const decided = [...tail];
  decided.splice(roundStart, 1,
    `${ind}# CK3XBL: decided in Bannerlord (docs/PROTOCOL.md 30): round one ends the fight for the fixed winner.`,
    `${ind}scope:sc_defender = { set_variable = { name = success_threshold value = ${DECIDED_THRESHOLD} } }`,
    `${ind}scope:sc_attacker = { trigger_event = single_combat.0021 }`);
  return [
    '# CK3 single combat, hooked (docs/PROTOCOL.md 18; tools/gen/duel/single_combat_hook.mjs). Loaded after vanilla\'s',
    '# 00_single_combat_effects.txt, so this definition is the one used; check error.log after a CK3 patch.',
    'configure_start_single_combat_effect = {',
    '\t# Sort parties and arrange the aftermath (as vanilla), and the rules, saved before the branch.',
    '\t$SC_INITIATOR$ = { save_scope_as = sc_initiator }',
    '\t$SC_ATTACKER$ = { save_scope_as = sc_attacker }',
    '\t$SC_DEFENDER$ = { save_scope_as = sc_defender }',
    '\tif = {',
    '\t\tlimit = { exists = scope:sc_finished }',
    '\t\tclear_saved_scope = sc_finished',
    '\t}',
    '\tsave_scope_value_as = { name = follow_up_event value = event_id:$OUTPUT_EVENT$ }',
    '\tsave_scope_value_as = { name = invalidation_event value = event_id:$INVALIDATION_EVENT$ }',
    '\tsave_scope_value_as = { name = fatality value = flag:$FATALITY$ }',
    '\tsave_scope_value_as = { name = fixed value = flag:$FIXED$ }',
    '\tsave_scope_value_as = { name = locale value = flag:$LOCALE$ }',
    '\tif = {',
    '\t\tlimit = { ck3xbl_duel_wanted_trigger = yes }',
    '\t\tck3xbl_duel_begin_effect = yes',
    '\t}',
    '\telse = { ck3xbl_sc_setup_effect = yes }',
    '}',
    '',
    '# The rest of vanilla configure_start_single_combat_effect, from its invalidation check on (the rules come from the saved',
    '# scopes). Needs the scopes saved above; the mod runs it again after a Bannerlord duel with scope:fixed set.',
    'ck3xbl_sc_setup_effect = {',
    ...tail,
    '}',
    '',
    '# The same, for a duel already fought in Bannerlord (scope:fixed set): no rounds, CK3 goes straight to its aftermath.',
    'ck3xbl_sc_decided_effect = {',
    ...decided,
    '}',
  ];
}

/** The line of the vanilla tail that starts round one. */
export const FIRST_ROUND = 'scope:sc_defender = { trigger_event = single_combat.0001 }';
/** Success threshold every success check passes, so the first round decides for whoever the fix lets win. */
export const DECIDED_THRESHOLD = -100000;
