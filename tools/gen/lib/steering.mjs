// Constants of the battle clock (docs/PROTOCOL.md §14): steering modifiers, controller, timeline, end call,
// second clash and the phase-event shield. One source for the mod generator (gen_ck3_mod.mjs) and its self-test
// (test_ck3_mod.mjs). In-game measurements decided the alternatives; a later
// in-game result changes only these constants, and the script that uses them is regenerated.

// Every lever modifier moves its owner modifier by one percent point per unit of its scale value; the scale value reads
// the receiver's variable of the same name (ck3xbl_st_<lever>_<up|down>, percent points, 0 = off).
export const LEVER_STEP = 0.01;

// Owner levers (the army owner carries all three). `up`/`down` = modifier names (loc),
// `upMax`/`downMax` = the largest percent points the lever may add or take away. Measured in game: +1900 % gave x17.2-18.8; a total
// of -120 % turned damage negative and toughness erratic, so the down caps stay as they are (never 95/90).
// KNIGHT_UP_MAX is provisional (no knight fought in the measured battles; still to be confirmed in game).
export const DMG_UP_MAX = 1900;
export const DMG_DOWN_MAX = 50;
export const TOUGH_UP_MAX = 900;
export const TOUGH_DOWN_MAX = 40;
export const KNIGHT_UP_MAX = 1900;
export const KNIGHT_DOWN_MAX = 50;
export const LEVERS = [
  { key: 'dmg', modifier: 'army_damage_mult', up: 'Fierce Assault', down: 'Faltering Assault', upMax: DMG_UP_MAX, downMax: DMG_DOWN_MAX },
  { key: 'tough', modifier: 'army_toughness_mult', up: 'Stubborn Ranks', down: 'Wavering Ranks', upMax: TOUGH_UP_MAX, downMax: TOUGH_DOWN_MAX },
  { key: 'knight', modifier: 'knight_effectiveness_mult', up: 'Knights Pressing Hard', down: 'Knights Hanging Back', upMax: KNIGHT_UP_MAX, downMax: KNIGHT_DOWN_MAX },
];

// The lowest total (percent points) a down lever may bring an owner to, counting the vanilla maluses the generated headroom
// values know: a down lever stops at MALUS_FLOOR - 100 x the owner's known maluses.
export const MALUS_FLOOR = 90;

// How a lever's strength reaches the modifier. 'scale': one modifier per lever and direction whose
// `scale` reads the percent points (measured in game: damage x2.000 at +100). 'ladder' (the fallback, should `scale` stop working):
// fixed-step modifiers ck3xbl_st_<lever>_s<k>, the nearest step at or above the headroom limit.
export const MODIFIER_FORM = 'scale';
export const LADDER_STEPS = {
  dmg: [-50, -40, -30, -20, -10, 10, 25, 50, 75, 100, 150, 200, 300, 400, 600, 900, 1300, 1900],
  tough: [-40, -30, -20, -10, 10, 25, 50, 75, 100, 150, 200, 300, 400, 600, 900],
  knight: [-50, -40, -30, -20, -10, 10, 25, 50, 75, 100, 150, 200, 300, 400, 600, 900, 1300, 1900],
};

// Knight lever (still to be confirmed in game): 'follow' = the knight lever takes the damage lever's value; 'off' = never used
// (when army_damage_mult already scales knight damage, both levers would square the gain on the knights' share).
export const KNIGHT_LEVER = 'follow';

// Commanders: hard_casualty_modifier + enemy_hard_casualty_modifier, so every casualty of either side is a dead soldier
// (share 0.3 x (1 + m + 2 x HARD)). Measured in game: the share is not capped; with 1.16 it is about 1.05.
export const HARD = 1.16;
// Who carries the hard modifier: 'all' = every commander of both sides; 'leader' = only each side's side_commander.
// Measured in game: the commanders' values do not add up, one value per side, so 'all'.
export const HARD_CARRIER = 'all';
// The dead share of a side is 0.3 x (1 + its own commanders' hard_casualty_modifier + the enemy commanders'
// enemy_hard_casualty_modifier), so a side with no commander at all carries neither of its terms: its own share lacks the
// friendly term and the enemy's share lacks the enemy term. Live test 2026-10-04, clash 2 (the loser had no commander): the
// dead share was 0.65 on both sides every day, and the loser's 1,085 fighters could give only about 700 of its 885 dead.
// HARD_COVER = true: while one side has no commander, every commander of the other side also carries ck3xbl_st_hard_cover,
// HARD on both tags once more, so both shares are 0.3 x (1 + m + 2 x HARD) again. Both sides without a commander: no carrier.
export const HARD_COVER = true;

// Steering modifiers are added for this many days, so a broken chain cannot leave one on.
export const MODIFIER_DAYS = 30;

// Controller: gain range and the damping and step clamp of the closed loop. The levers add to the
// owner's existing multipliers, so a gain is never exact; the loop absorbs it. The first damage day after a (re)seed runs at
// gain 1 with the levers off, and its measured kills set the gain absolutely (no feed-forward: total_army_damage is a sum of
// per-man stats, seen in game).
export const G_MIN = 0.05;
export const G_MAX = 33;
export const DAMPING = 0.7;
export const STEP_MIN = 0.25;
export const STEP_MAX = 4;
// Measured in game: no damage on combat days 1-4; the first kills come on day 5.
export const FIRST_DAMAGE_DAY = 5;

// End check: a side has reached its target when less than REACHED_OBS_SHARE of a day's kills remain;
// on the end day the steering gets one more day (never after HARD_END_DAY) while the winner has killed less than
// EXTEND_KILL_SHARE of its target and the loser still has fighting men (live test 2026-10-04: the old rule also wanted the
// winner's gain at the cap, so a winner short of its target below the cap was ended on the end day). A gain at or above
// EXTEND_GAIN_SHARE x G_MAX on the last pulse marks ST|WIN as capped.
export const REACHED_OBS_SHARE = 0.5;
export const EXTEND_GAIN_SHARE = 0.97;
export const EXTEND_KILL_SHARE = 0.9;
// The enemy dead left to the pursuit: PURSUIT_FLOOR x the enemy's soldiers. Measured in game: no pursuit casualties with set_skip_pursuit.
export const PURSUIT_FLOOR = 0;

// Timeline: the window ends on combat day pace (default PACE_DEFAULT, the bridge may send 6..13 in
// r198), never before the steering start + MIN_WINDOW_DAYS and never after HARD_END_DAY (MIN_DAYS_BEFORE_MANUAL_RETREAT 14).
// The default 12 gives damage on days 5-11 and the end call on day 12 at the latest. A resumed steering gets at least
// RESUME_WINDOW_DAYS.
export const PACE_DEFAULT = 12;
export const PACE_MIN = 6;
export const PACE_MAX = 13;
export const HARD_END_DAY = 13;
export const MIN_WINDOW_DAYS = 3;
export const RESUME_WINDOW_DAYS = 2;
// r198 = pace + 100 x flags: the pace is r198 modulo PACE_HINT_FLAGS.
export const PACE_HINT_FLAGS = 100;

// The end call: set_winner, which ends the combat on the engine's next tick (measured in game: one END pair). force_win ended the
// battle twice even with the game running, so it is never used. A combat still going END_BACKSTOP_DAYS pulses after the
// call is handed back to CK3 (ST|DONE reason 6); in game, END came in the first day tick after the call.
export const END_EFFECT = 'set_winner';
export const END_BACKSTOP_DAYS = 3;

// Second clash (docs/PROTOCOL.md §14.6): a join on the Bannerlord loser's side opens one when it brings at least
// REINFORCE_MIN_MEN men and REINFORCE_SHARE of that side's fighters. A suspension without an answer hands the battle back
// after FAILSAFE_DAYS game days.
export const REINFORCE_MIN_MEN = 100;
export const REINFORCE_SHARE = 0.1;
export const FAILSAFE_DAYS = 3;

// Combat day markers: marker k is set with days = k, so the number of expired markers is the
// combat day counted from the snapshot (measured in game: one expires per day). 13 = the last day before manual retreat is allowed.
export const DAY_MARKERS = 13;

// Phase-event shield: entries per type (knight, commander) and the weight of each (100000 is the
// largest vanilla weight, scheme_critical_moments_events.txt). Against vanilla's knight entries this leaves a wound or
// death roll a chance of about 0.03 % per pulse.
export const SHIELD_ENTRIES = 10;
export const SHIELD_WEIGHT = 100000;
