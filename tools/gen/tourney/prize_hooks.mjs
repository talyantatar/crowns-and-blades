// prize_hooks.mjs - battle gear among the prizes of CK3 tournaments (docs/PROTOCOL.md 38).
//
// The vanilla effects that reward a contest winner with prestige (one winner, and each member of the winning melee team) are
// copied from the CK3 install at generation time into a zz_ file that loads after vanilla, each with one more line at its end:
//   ck3xbl_tourney_prize_effect = yes
// which gives the winner a chance of a piece of battle gear fitting the contest (ck3xbl_tourney_effects.txt). The rest of each
// effect is vanilla's, word for word, so a CK3 update that changes them is picked up at the next generation.
import { VANILLA_COPY_MARK } from '../skirmish/travel_hooks.mjs';

export const PRIZE_SOURCE = 'common/scripted_effects/04_dlc_ep2_tournament_effects.txt';
export const PRIZE_FILE = 'common/scripted_effects/zz_ck3xbl_gen_tourney_prize_hooks.txt';
export const PRIZE_EFFECTS = ['tournament_winner_prestige_rewards_effect', 'tournament_team_winner_prestige_rewards_effect'];
export const PRIZE_CALL = 'ck3xbl_tourney_prize_effect = yes';

const stripComment = (l) => l.replace(/#.*$/, '');

/** The lines of the hooked effects, or an error naming what changed in the install. */
export function prizeHookLines(vanillaText) {
  const lines = vanillaText.replace(/^﻿/, '').split(/\r?\n/);
  const out = [
    VANILLA_COPY_MARK,
    '# Vanilla tournament reward effects, copied whole from the CK3 install by tools/gen/tourney/prize_hooks.mjs, each ending in',
    `# "${PRIZE_CALL}" (docs/PROTOCOL.md 38). Do not edit by hand.`,
    '',
  ];
  for (const name of PRIZE_EFFECTS) {
    const start = lines.findIndex((l) => new RegExp(`^${name}\\s*=\\s*\\{`).test(l));
    if (start < 0) throw new Error(`${PRIZE_SOURCE}: ${name} not found`);
    let depth = 0;
    let end = -1;
    for (let i = start; i < lines.length && end < 0; i++) {
      for (const ch of stripComment(lines[i])) {
        if (ch === '{') depth++;
        else if (ch === '}') depth--;
      }
      if (depth === 0) end = i;
    }
    if (end < 0) throw new Error(`${PRIZE_SOURCE}: ${name} is not closed`);
    const block = lines.slice(start, end + 1);
    const last = block.pop();
    out.push(...block, `\t${PRIZE_CALL} # CK3XBL: a chance of battle gear (docs/PROTOCOL.md 38)`, last, '');
  }
  return out;
}
