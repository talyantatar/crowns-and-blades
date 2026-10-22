// contest_hooks.mjs - the player's tournament bouts fought in Bannerlord (docs/PROTOCOL.md 29).
//
// events/activities/tournaments/contest_events.txt is vanilla's, copied whole from the CK3 install at generation time (as the travel
// events, tools/gen/skirmish/travel_hooks.mjs). The player's pivotal moment of three contests gets one option first:
//   option = { name = ck3xbl_tourney.fight  trigger = { the player competes  ck3xbl_skirmish_available_trigger = yes }
//              hidden_effect = { flag  ck3xbl_d_tourney value  trigger_event = ck3xbl_tourney.0001 } }
// A versus bout (single combat, wrestling) is decided by contest_events.0802 one day after the pivotal moment; with the flag
// ck3xbl_tourney_bl that call waits for the Bannerlord result (ck3xbl_tourney_apply_effect sends it then). The grand melee is
// decided at the contest's end from the teams' progress to victory, which the result sets. Every vanilla option stays as it was.
import { VANILLA_COPY_MARK } from '../skirmish/travel_hooks.mjs';

export const CONTEST_FILE = 'events/activities/tournaments/contest_events.txt';

/** The hooked pivotal moments: tourney code 1 single combat, 2 wrestling, 3 grand melee, 4 joust, 5 archery (PROTOCOL 36). A contest
 * that is no bout (melee, archery) is decided at its end from progress to victory; its option sets the flag of one vanilla option. */
export const CONTEST_HOOKS = [
  { id: 'contest_events.0862', code: 1, versus: true },
  { id: 'contest_events.0861', code: 2, versus: true },
  { id: 'contest_events.0610', code: 3, versus: false, flag: 'martial_pivotal_duel' },
  { id: 'contest_events.0860', code: 4, versus: true },
  { id: 'contest_events.0710', code: 5, versus: false, flag: 'prowess_pivotal_duel' },
];

export const RESOLVE_CALL = 'trigger_event = { id = contest_events.0802 days = 1 }';

const stripComment = (l) => l.replace(/#.*$/, '');

/** Index range [start, end] of an event block. */
function eventRange(lines, id) {
  const start = lines.findIndex((l) => new RegExp(`^${id.replace('.', '\\.')}\\s*=\\s*\\{`).test(l));
  if (start < 0) throw new Error(`${CONTEST_FILE}: event ${id} not found`);
  let depth = 0;
  for (let i = start; i < lines.length; i++) {
    for (const ch of stripComment(lines[i])) {
      if (ch === '{') depth++;
      else if (ch === '}') depth--;
    }
    if (depth === 0) return [start, i];
  }
  throw new Error(`${CONTEST_FILE}: event ${id} is not closed`);
}

/** The vanilla file with the options added (lines), or an error naming what changed in the install. */
export function hookedContestFile(vanillaText) {
  const lines = vanillaText.replace(/^﻿/, '').split(/\r?\n/);
  for (const ev of CONTEST_HOOKS) {
    let [start, end] = eventRange(lines, ev.id);
    if (ev.versus) {
      // The bout is resolved one day later; our flag holds that call until Bannerlord has spoken.
      const call = lines.findIndex((l, i) => i > start && i < end && stripComment(l).trim() === RESOLVE_CALL);
      if (call < 0) throw new Error(`${CONTEST_FILE}: ${ev.id} no longer resolves its bout with "${RESOLVE_CALL}"`);
      const ind = lines[call].match(/^\s*/)[0];
      lines[call] = `${ind}if = { limit = { NOT = { has_character_flag = ck3xbl_tourney_bl } } ${RESOLVE_CALL} } # CK3XBL: held while fought in Bannerlord`;
    }
    [start, end] = eventRange(lines, ev.id);
    let depth = 0;
    let at = -1;
    for (let i = start; i <= end; i++) {
      const code = stripComment(lines[i]);
      if (depth === 1 && /^\s*option\s*=\s*\{/.test(code)) { at = i; break; }
      for (const ch of code) {
        if (ch === '{') depth++;
        else if (ch === '}') depth--;
      }
    }
    if (at < 0) throw new Error(`${CONTEST_FILE}: event ${ev.id} has no option to put ours before`);
    const ind = lines[at].match(/^\s*/)[0];
    const step = ind.includes('\t') ? '\t' : '    ';
    const opt = [
      `option = { # CK3XBL: compete in Bannerlord yourself (docs/PROTOCOL.md 29)`,
      `${step}name = ck3xbl_tourney.fight`,
      `${step}trigger = {`,
      `${step}${step}scope:pivotal_contestant ?= this`,
      ev.versus ? `${step}${step}exists = scope:versus_contestant` : null,
      `${step}${step}ck3xbl_skirmish_available_trigger = yes`,
      `${step}}`,
      `${step}hidden_effect = {`,
      ev.versus ? `${step}${step}add_character_flag = ck3xbl_tourney_bl` : `${step}${step}add_character_flag = ${ev.flag}`,
      `${step}${step}save_scope_value_as = { name = ck3xbl_d_tourney value = ${ev.code} }`,
      `${step}${step}trigger_event = ck3xbl_tourney.0001`,
      `${step}}`,
      `}`,
    ].filter((l) => l !== null).map((l) => ind + l);
    lines.splice(at, 0, ...opt);
  }
  return [VANILLA_COPY_MARK, ...lines];
}
