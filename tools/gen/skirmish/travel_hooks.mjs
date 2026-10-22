// travel_hooks.mjs - vanilla travel danger events with an extra option: fight it out in Bannerlord (docs/PROTOCOL.md 20).
//
// The events are vanilla's, copied whole from the CK3 install at generation time (a file of the mod with the same path replaces
// the vanilla file, and file-local scripted triggers stay with their events). Each listed event gets one option first:
//   option = { name = ck3xbl_skirmish.fight  trigger = { ck3xbl_skirmish_available_trigger = yes }
//              hidden_effect = { <foe> = { save_scope_as = ck3xbl_foe }  ck3xbl kind value  trigger_event = ck3xbl_skirmish.0001 } }
// Every vanilla option stays as it was. Regenerate after a CK3 patch.

/** Files and events: the opponent's saved scope in the event, and the kind of foe (1 bandits, 2 deserters, 3 assassin, 4 soldiers,
 *  5 zealots), which sets the size and the wording. */
export const TRAVEL_HOOKS = [
  { file: 'events/travel_events/travel_danger_events.txt', events: [{ id: 'travel_danger_events.1002', foe: 'bandit', kind: 1 }] },
  {
    file: 'events/travel_events/travel_danger_events_joe.txt',
    events: ['6010', '6011', '6012', '6013'].map((n) => ({ id: `travel_danger_events.${n}`, foe: 'soldier', kind: 2 })),
  },
  { file: 'events/travel_events/travel_events.txt', events: [{ id: 'travel_events.1003', foe: 'attacking_bandit', kind: 1 }] },
  {
    file: 'events/travel_events/travel_events_veronica.txt',
    events: [
      { id: 'travel_events.3070', foe: 'bandit_scope', kind: 1 },
      { id: 'travel_events.3130', foe: 'sexy_assassin', kind: 3 },
      { id: 'travel_events.3140', foe: 'hostile_commander', kind: 4 },
    ],
  },
  { file: 'events/travel_events/travel_events_james.txt', events: [{ id: 'travel_events.4020', foe: 'strong_believer_scope', kind: 5 }] },
];

export const VANILLA_COPY_MARK = '# CK3XBL vanilla copy: a CK3 event file with Bannerlord options added (tools/gen/skirmish/travel_hooks.mjs).';

const stripComment = (l) => l.replace(/#.*$/, '');

/** The vanilla file with the options added (lines), or an error naming what changed in the install. */
export function hookedEventFile(vanillaText, hook) {
  const lines = vanillaText.replace(/^﻿/, '').split(/\r?\n/);
  for (const ev of hook.events) {
    const start = lines.findIndex((l) => new RegExp(`^${ev.id.replace('.', '\\.')}\\s*=\\s*\\{`).test(l));
    if (start < 0) throw new Error(`${hook.file}: event ${ev.id} not found`);
    // The first option at the event's own level (depth 1).
    let depth = 0;
    let at = -1;
    for (let i = start; i < lines.length; i++) {
      const code = stripComment(lines[i]);
      if (depth === 1 && /^\s*option\s*=\s*\{/.test(code)) { at = i; break; }
      for (const ch of code) {
        if (ch === '{') depth++;
        else if (ch === '}') depth--;
      }
      if (depth === 0 && i > start) break;
    }
    if (at < 0) throw new Error(`${hook.file}: event ${ev.id} has no option to put ours before`);
    const ind = lines[at].match(/^\s*/)[0];
    const step = ind.includes('\t') ? '\t' : '    ';
    const opt = [
      `option = { # CK3XBL: fight it out in Bannerlord (docs/PROTOCOL.md 20)`,
      `${step}name = ck3xbl_skirmish.fight`,
      `${step}trigger = { exists = scope:${ev.foe} ck3xbl_skirmish_available_trigger = yes }`,
      `${step}hidden_effect = {`,
      `${step}${step}scope:${ev.foe} = { save_scope_as = ck3xbl_foe }`,
      `${step}${step}save_scope_value_as = { name = ck3xbl_sk_kind value = ${ev.kind} }`,
      `${step}${step}trigger_event = ck3xbl_skirmish.0001`,
      `${step}}`,
      `}`,
    ].map((l) => ind + l);
    lines.splice(at, 0, ...opt);
  }
  return [VANILLA_COPY_MARK, ...lines];
}
