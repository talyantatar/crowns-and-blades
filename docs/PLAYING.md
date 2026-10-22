# Playing Crowns & Blades

This page follows one battle from start to finish, then explains the tray menu, the two modes and every setting.

## Before the first battle

1. Start **Crowns & Blades**. Its icon, two crossed swords, sits in the notification area. A balloon says "Crowns & Blades
   ready", or tells you what is missing (click it for details).
2. The bridge starts Bannerlord **hidden and muted** in the background, so the first battle loads in a second or two instead
   of a minute.
3. Start CK3 from the tray menu (**Launch Crusader Kings III**, **Normal (achievements)**) or from the Paradox Launcher with
   "Crowns & Blades Battles" in your playset.
4. Set CK3's **Window Mode to Fullscreen** in its video options. In a window, the switch shows your desktop around the cards.

## A battle, step by step

### 1. Which battles are offered

A battle is offered when you take part in it (as a participant, an army commander or a knight), or when it belongs to a war
you are fighting. Battles of your vassals in their own wars, and every battle you have nothing to do with, stay with CK3.

Field battles, duels, tournament bouts, siege assaults (the decision "Storm the Walls") and sea battles go to Bannerlord. A relief
army that reaches a besieged holding can defend the walls, strike the besiegers' camp, or fight before the walls.

### 2. The call to arms

CK3 pauses by itself and shows a **"To Arms!"** notice and an event, **"The Battle of Apulia"**, with the troop numbers of both
sides. Your choices:

| Option | When | What happens |
|---|---|---|
| **I will take the field myself!** | you are with one of the armies | You fight in Bannerlord as your own character. |
| **I will lead this host in person.** | you are not with the armies | You fight as the commander of your largest army. If no army of your side has a commander, you fight as yourself, but as you are not really there, nothing that happens to you in Bannerlord harms you in CK3. |
| **Let my commanders decide the day.** | always | CK3 resolves the battle as usual. Nothing else happens. |

Only one battle can be fought in Bannerlord at a time. While one is under way, the field options of another battle are
greyed out ("Another battle is already being fought in the field").

While CK3 shows the event, the bridge already builds the battle and loads it in the hidden Bannerlord, so it is ready when you
click.

### 3. The title card

When you take the field, CK3 freezes on its last frame, which darkens into a title card in CK3's style: the battle's name, who
fights whom, the season and year, the ground ("Fields before Apulia"), and both coats of arms. The coats of arms are drawn from
your CK3 install, exactly as CK3 draws them.

Meanwhile Bannerlord comes forward, and the card dissolves straight into the battlefield. Bannerlord's sound comes up with it.

### 4. Deployment

You see Bannerlord's **Order of Battle** screen with your troops already in formations. Arrange them as you like and click
**Ready**. (With `battle.deployment` set to `auto`, or fewer than 20 troops, Bannerlord deploys for you.)

What is on the field:

- **Your armies, as they are in CK3.** Every army in the battle, with its levies and each men-at-arms regiment, becomes
  Bannerlord troops of the closest culture and kind (archers stay archers, heavy cavalry stays heavy cavalry). Large battles
  are scaled down: by default up to 1,000 soldiers across both sides, at most 600 on the field at once (Bannerlord brings the
  rest in as reinforcements; `battle.battleSize` goes up to 1,000, bigger froze Bannerlord in testing). Each side keeps its
  proportions, and the losses are scaled back up. Professionals take the field first, levies last. When free memory runs low,
  the bridge puts fewer men on the field at once (`battle.memoryGuard`).
- **You**, with your face (rebuilt from your CK3 genes), your age and build, and your CK3 battle gear (or equipment fitting
  your culture and rank).
- **Your commanders and knights** as named heroes, on both sides, each under their own coat of arms. Their names show in the
  kill feed and the scoreboard.
- **Your coat of arms** on your side's banners. The enemy's colours are adjusted, when needed, so the two sides are easy to
  tell apart.
- **War elephants** for elephant men-at-arms, when Burning Empires is installed (one elephant per four men).
- **The ground:** a Bannerlord battle map chosen for the CK3 terrain, climate and holding, in the right season and a matching
  time of day.
- **The objective**, as Bannerlord shows it: "Rout the Host of ...", "Break the siege of ..." or "Hold the siege lines
  before ...".

### 5. The fight

It is an ordinary Bannerlord battle: you command, you fight, you can fall. Two things to know:

- Pressing Esc pauses the battle as usual. **Exit to Main Menu** ends the battle early: the losses so far count, there is no
  winner, and CK3 continues the fight on its own.
- **Ctrl+Alt+Shift+End** does the same from anywhere (see [The abort hotkey](#the-abort-hotkey)).

### 6. The result

When one side breaks, a line appears at the bottom:

> The Battle of Apulia is won · Returning to your realm in 10 · [Enter] now · [Space] stay on the field

The countdown stops while the game is paused. Press **Enter** (or the scoreboard's **Done**) to go back at once, or **Space**
to stay and look around; then press Enter when you are ready.

The screen fades, and a result card in CK3's style says how it went, for example "Victory at Apulia: 1,457 of the enemy fell
for 515 of your men. The battle continues in Crusader Kings III. Click 'The dust settles...', then resume the game."

### 7. Back in CK3

CK3 comes back to the front, still paused, with an event: **"The Armies Clash"**. While you read it, the bridge types the
result into CK3 through the mod's hidden buttons. This takes a few seconds. **Leave the keyboard and mouse alone during it**:
the bridge waits for a quiet moment, then briefly holds back your key presses and clicks while it types. Before typing it
checks that the battle window answers; if another window or menu covers it, nothing is typed and a notice asks you to close
it (the bridge tries again a little later, or when you come back to CK3). If you switch away, typing stops and starts again
when CK3 is back in front; a won battle is never lost that way. The event's status line changes to **"The dispatch has
arrived."** when CK3 has the result.

| Option | What happens |
|---|---|
| **The dust settles...** | The result is applied (see below). A notice, "The Battle Rages On", tells you to resume the game. |
| **Let the battle run its course without me.** | The Bannerlord result is dropped and CK3 decides the battle itself. |

### 8. The battle clock: CK3 fights it out

After "The dust settles...", resume the game. The two armies are still locked in battle in CK3, and CK3's own combat now
inflicts the Bannerlord losses on them over the next days:

- As in any CK3 battle, nobody falls in the first days. Then the dead climb day by day towards the numbers from Bannerlord.
- By combat day 12 (the default; see `battleClock.paceDays`) the side that won in Bannerlord wins in CK3 too.
- Because CK3 really fought it, **war score, the battle summary and the aftermath are CK3's own**. A decisive Bannerlord victory
  can earn the full war score CK3 gives for one battle.
- One day after the battle ends, every army is set to exactly what survived in Bannerlord, and the final event opens:
  **"Victory at Apulia"** (or "Defeat at ..."), with your losses, the enemy's and how many foes you cut down yourself.

Things that happen on the way:

- **Characters.** Fates are applied right away when you click "The dust settles...": who was wounded, maimed, captured or
  slain. They also appear as lines in CK3's battle summary.
- **Your own fall.** If you fell in Bannerlord, CK3 turns it into a wound or a maiming (never death or capture), unless you set
  `battle.playerDeathIsReal`.
- **CK3's own aftermath.** After the battle, CK3 may still capture or kill some of the beaten side's commanders and knights, as
  it does after every battle. That comes on top of the Bannerlord result.
- **Reinforcements for the winner** simply join the fight and take CK3's normal losses.
- If the battle in CK3 is already over when the result arrives (for example, you unpaused for a long time during the
  Bannerlord fight), the survivors are set at once, but the battle gives no war score.

The tray icon's tooltip follows the battle: "Battle of Apulia, day 6: 1,212 of 2,116 enemy fallen".

### 9. Reinforcements: a second clash

If fresh troops reach the side that **lost** in Bannerlord while CK3 fights it out (at least 100 men and at least a tenth of
that side's fighters), the battle hangs in the balance again. CK3 pauses with **"Reinforcements!"** and an event,
**"Reinforcements Arrive!"**:

- **Fight again** in Bannerlord: the same battlefield, a later hour, the armies at what was left of them, plus the newcomers.
  The result of both clashes together goes back to CK3, and CK3 fights out the rest as before.
- **Let my commanders decide the day**: the first result stands, and CK3 decides the rest of the battle on its own.
- If you leave the event open for three game days, the battle goes on without you.

Set `battleClock.reinforcementClash` to `false` if you never want the second clash.

## Knights, commanders and rewards

Your knights and commanders, and the enemy's, are not just faces:

- **Knight effectiveness** (from CK3's Knights window) makes knights tougher.
- **Commander traits** such as Aggressive Attacker or Cautious Leader shape the whole side, and the enemy's commanders and
  knights lead its formations as captains.
- **Accolades** (Tours & Tournaments) strengthen the troops their knight leads; **artifacts** such as weapons and armour are
  carried into battle as matching Bannerlord gear.
- **Traits** such as strong, giant or wounded change how a hero fights.
- **Prowess** raises a hero's damage, health and weapon skill on soft caps: damage x2 at 40 prowess and x2.75 at 100, health
  up to x5 at 100, weapon skill 330 at 34 and up to 400. Heroes get a little speed from prowess and lose some with age over 50.
- **Martial** of a commander gives the whole army up to +30% damage and health and some morale.
- **Acclaimed knights** give their formation an aura (2.5% per rank) while they live.

All effects are capped so that no hero becomes invincible.

### Single combat

Whenever you are one of the two fighters of a CK3 single combat (a challenge, a trial by combat, a court duel, a duel inside a
battle), CK3 pauses and asks: **I will fight with my own hand!** opens the duel in Bannerlord; **Let fate decide** leaves it to
CK3 as before. Nobles fight in a city arena before an audience; lowborn duels are fought in a forest clearing. In a duel the
fighter with more prowess gets 1% more damage per point of difference (at most +50%). The winner in Bannerlord is the winner in
CK3, and CK3 then plays out its usual rounds and consequences. Death follows CK3's rules: in a duel that may kill, whoever
falls dead in Bannerlord dies in CK3; a practice bout kills nobody.

What a duel fought in Bannerlord earns:

- **Blademaster** experience (more for a win); the trait comes at 50 experience, and every level gives +20 to all weapon skills
  in Bannerlord, besides its prowess.
- **Brave** for beating someone with more prowess than you; a craven loses that trait instead.
- **Berserker** for the tenth duel won.
- Half the time the winner takes one of the loser's **artifacts** (never a court artifact).
- Duel **feats** (see [Feats](#feats)).

### Tournaments

When you compete in a CK3 tournament, the melee, joust and archery contests get an option to fight your bout in Bannerlord's
arena. A versus bout is decided by who wins in Bannerlord; the melee by how you fare in it; archery by your score at the targets.
Tournament feats are earned there.

### Sieges

- **Storm the walls.** While your army besieges a holding, the decision **Storm the Walls in Bannerlord** leads the assault in
  person. Each of your CK3 siege regiments brings one siege engine; the longer the siege has run, the more siege works stand
  ready (ladders, towers, a ram, a breach). The defenders are a professional garrison sized by the fort level, with bonuses
  from the walls, and you attack in waves of about 2.5 attackers per defender. Win, and the holding falls within days; you then
  choose its fate: sack it, ransom it or spare it. Lose, and the siege goes on with your losses counted. An early assault is
  brutal, a late one is winnable.
- **Relieve a siege.** When your relief army reaches a besieged holding, the call to arms offers to **defend the walls** with the
  garrison, to **strike the besiegers' camp** (a field battle with the besiegers shaken and a sally from the garrison), or to
  fight before the walls.

### Sea battles and landings

With the War Sails DLC:

- **At sea.** When an enemy fleet is near, the decision **Attack the Enemy Fleet in Bannerlord** brings your ships alongside
  (events "Sails on the Horizon" and "Enemy Sails Bear Down" offer the same when fleets meet). The enemy may slip away first: the
  better its commander's martial against yours, the likelier. Each side sails ships of its own culture (Norse drakkars for the
  North Germanic heritage), up to 8 a side; half the men who flee the losing ships drown.
- **Landings.** A battle your army fights right off its ships is a beach landing from at most 3 shallow-water ships. A landing
  the enemy makes against you is fought as a battle on the shore.

### Feats

What you do yourself in Bannerlord earns lifelong feats: CK3 character modifiers in three ranks, kept for life. There are 22,
for example Reaper (fifty foes cut down in one battle), Carnage, Living Legend (a thousand foes in a lifetime), Slayer of
Commanders, Miracle on the Field (won against twice your numbers), Invulnerable (ten battles without a scratch), and feats for
duels, sea battles, sieges and tournaments. Earning a feat again raises its rank. Turn them off with `battle.feats`.

### Costumes

Your soldiers dress like CK3's: the armour of each regiment comes from its CK3 culture and era (no plate before the late
medieval era), levies wear cloth, riders mail, archers hoods. Each piece has about the protection of the troop's own, so the
look changes and the fight does not. Heroes are dressed in the nearest armour of their look. For far more variety install
**Open Source Armory** and **Open Source Saddlery**, or **Terra Armarium** (Nexus Mods), into `<Bannerlord>\Modules`; the bridge
finds and loads them by itself. Their `Shaders` folders, built for another Bannerlord version, crash v1.4.8 at start, so the
bridge renames them `Shaders.ck3xbl_off` before the first battle; it also lets Terra Armarium's items load in custom battles
(see [SECURITY.md](../SECURITY.md#what-it-writes)). If Bannerlord still crashes while loading with them, the bridge starts
Bannerlord without them and remembers it in `%LOCALAPPDATA%\ck3xbl\armory_failed.txt`; delete that file to try them again. Turn costumes off with `"costumes": false` in the
`battle` section of the bridge config.

### Skirmishes on the road

When a travel danger brings you face to face with armed men (bandits, deserters, an assassin, soldiers who want to seize you,
zealots), the event has one more option: **Draw steel and fight it out in Bannerlord!** You, the knights travelling with you and
your guards meet the band on the spot, 50 fighters at most. Win, and the road is clear (gold, prestige, and sometimes the foe's
artifact); lose, and your purse pays for it. Wounds and deaths of the fighters follow CK3's battle odds. Some bands are met in
their hideout instead of on the road. All the vanilla options of the event are still there.

### Battle gear and the armory

Open your inventory and click **Battle Gear** (top right of the equipped side). Ten slots appear: main weapon, three weapon
slots that take any weapon or shield, helmet, body armour, gloves, boots, cloak and horse. Every artifact that fits them is one Bannerlord item, and in Bannerlord you
carry exactly that item. Empty slots keep the equipment of your culture and rank; a horse in its slot makes you ride, even if
your culture's nobles fight on foot; a bow or crossbow brings matching arrows or bolts.

- **The armory.** The decision **Visit the Armory** shows six offers: mostly your culture's wares, sometimes a foreign piece. The
  stock changes every two years. Prices run from 200 gold to 5000 gold, and the rarest pieces cost prestige as well; everything
  is dearer in earlier eras.
- **Stats.** The best main weapons give +20 prowess, body armour up to +8, a helmet +7, a shield +6, gloves, boots and cloaks a
  little; the finest pieces add prestige, knight effectiveness or army speed (horses). Like vanilla artifacts they wear out and
  can be repaired.
- **Events.** Vanilla events that hand out a weapon, armour or a bow give battle gear instead. AI rulers buy gear for their knights
  and themselves, so the enemy's champions come well armed too.

What heroes win in Bannerlord comes back to CK3:

- **You**, when you fought in person: prestige for your own kills, plus a bonus for a victory.
- **Knights and commanders** who struck down enemy heroes or many soldiers: prestige, glory for acclaimed knights, and chances
  to gain prowess.

## The abort hotkey

**Ctrl+Alt+Shift+End** works from anywhere, while the bridge runs:

| When | What it does |
|---|---|
| Before you entered Bannerlord | The battle is handed back to CK3. Choose "Let my commanders decide the day" there. |
| During the switch to Bannerlord | The battle is cancelled. Choose "Let the battle run its course" in CK3. |
| During the fight | The battle ends now: the losses so far count, there is no winner, and CK3 continues the fight. |
| On the result screen | You return to CK3 at once. |
| While the result is being sent to CK3 | Sending stops. Choose "Let the battle run its course" in CK3. |

If another program already uses this key combination, the bridge says so in its log and the hotkey is not available.

## The tray menu

Right-click the crossed swords:

| Item | What it does |
|---|---|
| **Launch Crusader Kings III** | **Normal (achievements)** or **Dev mode (debug, console)**; see [Modes](#normal-and-dev-mode). Before the launch it checks that the mod is in CK3's mod list. |
| **Bannerlord: ...** | The state of the hidden Bannerlord, with **Start Bannerlord** and **Close Bannerlord**. |
| Battle line | The battle in progress, if any. |
| **Open battle log folder** | `%LOCALAPPDATA%\ck3xbl` (also on double-click). |
| **Settings** | Opens `config.json` in your editor. Quit and restart Crowns & Blades to apply changes. |
| **Set game folders...** | Pick the CK3 and Bannerlord install folders by hand. |
| **Start with Windows** | Starts Crowns & Blades when you sign in. Off by default. |
| **About Crowns & Blades...** | Version, game versions, the install checks, paths, links, and **Copy diagnostics** for bug reports. |
| **Quit** | Closes Crowns & Blades and its hidden Bannerlord, gracefully. |

## Normal and Dev mode

| | **Normal** (default) | **Dev** |
|---|---|---|
| CK3 is started with | `-gdpr-compliant` only (release mode) | `-debug_mode -suppress_error_log -gdpr-compliant` |
| Results go into CK3 through | the mod's hidden buttons on CK3's modder hotkeys | a script file and CK3's console |
| Achievements | possible | off for that game |
| For | playing | modding and testing |

The mode you choose in the tray menu applies until the bridge restarts; otherwise the `mode` setting applies. Never mix the two
modes on one save you want achievements for.

## Settings (config.json)

The settings live in `%LOCALAPPDATA%\ck3xbl\config.json` (in a source checkout, in `bridge\config.json`). On a fresh install
the file holds only the game folders setup found. Add any key from the tables below in its section, for example
`"battle": { "playerDeathIsReal": true }`; a missing key takes its default. (When the bridge creates the file itself, as in a
manual install or a source checkout, and after **Set game folders...**, it is written with every key.) Open it with
**Settings** in the tray menu, then quit and restart Crowns & Blades. Unknown keys are ignored; a file that is not valid JSON
is never overwritten, and the defaults are used until you fix it.

### Top level

| Key | Default | Meaning |
|---|---|---|
| `mode` | `"normal"` | `"normal"` or `"dev"`; see [Modes](#normal-and-dev-mode). The tray launch overrides it until restart. |

### `ck3`

| Key | Default | Meaning |
|---|---|---|
| `gameDir` | `""` | The CK3 install folder (the one with `binaries` and `game`). Empty = find it automatically. |
| `userDir` | `""` | CK3's user folder (with `logs`, `mod`, `save games`). Empty = `Documents\Paradox Interactive\Crusader Kings III`. |
| `continueLastSave` | `false` | Add `-continuelastsave` when the tray starts CK3. |
| `logFile` | `"game.log"` | The CK3 log the bridge reads, in `logs\`: `game.log` or `debug.log`. |
| `logPollMilliseconds` | `75` | How often the log is read (20 to 500). |

### `bannerlord`

| Key | Default | Meaning |
|---|---|---|
| `gameDir` | `""` | The Bannerlord install folder (the one with `bin` and `Modules`). Empty = find it automatically. |
| `modules` | `["Native", "SandBoxCore", "CustomBattle", "CK3Bridge"]` | The modules Bannerlord is started with. |
| `fallbackModules` | the above plus `"SandBox"`, `"StoryMode"` | Used once if Bannerlord does not start with the first list. |
| `armoryModules` | `true` | Load Open Source Armory and Saddlery (with the SandBox and StoryMode they need) when they are in `Modules`. |
| `navalModules` | `true` | Load War Sails and the bridge's naval module for sea battles and landings when War Sails is installed. |
| `arenaModule` | `true` | Load SandBox, home of the arenas where noble duels and tournament bouts are fought. |
| `trimWhenIdle` | `true` | Give the RAM of the Bannerlord waiting hidden for the next battle back to CK3. |
| `recycleAboveMb` | `8000` | Restart the idle Bannerlord when the memory it keeps from earlier battles grows above this many MB; `0` = never. |
| `preloadOnStart` | `true` | Start Bannerlord hidden when the bridge starts, so the first battle loads quickly. |
| `keepRunning` | `true` | Start Bannerlord again in the background after it exits or crashes. |
| `heartbeatTimeoutSeconds` | `5` | Bannerlord counts as unresponsive when its module has not reported for this long. |
| `loadingHeartbeatTimeoutSeconds` | `60` | The same while Bannerlord boots or loads a battle. |
| `bootTimeoutSeconds` | `180` | Longest wait for Bannerlord to start. |
| `hungKillSeconds` | `30` | Bannerlord is ended by force only when its window has not responded for this long. Never otherwise. |

### `battle`

| Key | Default | Meaning |
|---|---|---|
| `maxAgentsTotal` | `1000` | Most Bannerlord soldiers across both sides (heroes not counted). Bigger CK3 armies are scaled down. |
| `minAgentsPerSide` | `40` | Fewest soldiers a side gets, so a small force is not reduced to a handful. |
| `battleSize` | `600` | Most soldiers on the field at once; the rest arrive as reinforcements (200 to 1000; bigger froze Bannerlord in testing). |
| `playerDeathIsReal` | `false` | `true`: your character can really die or be captured when you fall in Bannerlord. |
| `killedToCk3DeadShareWinner` | `0.45` | Share of the winning side's Bannerlord casualties that are dead in CK3. |
| `killedToCk3DeadShareLoser` | `0.65` | The same for the losing side. |
| `routedToCk3DeadShareLoser` | `0.25` | Share of the losing side's routed soldiers that die in the pursuit. |
| `returnCountdownSeconds` | `10` | Seconds on the result screen before going back to CK3. `0` = at once, `-1` = wait for you. |
| `deployment` | `"order_of_battle"` | `"order_of_battle"` (you arrange your troops) or `"auto"`. |
| `offerTimeoutMinutes` | `10` | The preloaded battle is dropped when you have not answered the CK3 event for this long (it is loaded again when you answer). |
| `enterTimeoutSeconds` | `120` | Give up switching to Bannerlord when the battle has not appeared this long after your click. |
| `strengthBalance` | `true` | Even the sides out so that a straight clash in Bannerlord ends as CK3 would decide it: the side Bannerlord's numbers would favour too little gets damage and health. Your command still decides the day. |
| `strengthTiersPerDoubling` | `3` | How much less Bannerlord values elite troops than CK3 (higher = elite men-at-arms get more help). Tuned from the `[balance]` lines of the bridge log. |
| `strengthMaxFactor` | `4` | Most help a side gets (4 = damage and health x2 each). |
| `costumes` | `true` | Troops wear the armour of their CK3 culture, era and role, at the stock protection. |
| `heroArms` | `true` | Knights and commanders carry their own CK3 coats of arms. |
| `feats` | `true` | Your own deeds in Bannerlord earn lifelong feats in CK3. |
| `trimCk3Memory` | `true` | Free CK3's RAM for Bannerlord once a battle has started (only when free RAM is low). |
| `memoryGuard` | `true` | Put fewer men on the field at once when free memory (RAM plus page file) would not hold `battleSize`. |
| `memoryReserveMb` | `2000` | Memory (MB) the memory guard leaves free for Windows and other programs. |
| `language` | `"auto"` | Language of the battle cards and Bannerlord texts: `"auto"` (CK3's language), `"en"` or `"ru"`. |

### `delivery`

How the result is typed into CK3. The defaults are tested; change them only when delivery fails on your PC.

| Key | Default | Meaning |
|---|---|---|
| `channel` | `"auto"` | `"auto"` (Normal mode: hidden buttons; Dev mode: script file), `"keypad"` or `"runfile"`. Normal mode always uses the hidden buttons. |
| `keyDelayMs` | `12` | Pause between key events (1 to 200). |
| `chunkSize` | `12` | Keys per burst; CK3 must be in front before every burst and every key (1 to 64). |
| `holdModifiersPerChunk` | `true` | Hold Ctrl+Shift+Alt for a whole burst (fast). `false` presses them for every key (slower). |
| `ackTimeoutSeconds` | `4` | How long to wait for CK3 to confirm; then the whole result is sent once more. |
| `blockPhysicalInput` | `true` | Hold back your own key presses and clicks while keys are typed (at most 4 seconds at a time). |
| `quietInputMs` | `300` | Your keyboard and mouse must be idle this long before typing starts. |
| `keypadAwardKeyBudget` | `90` | Most extra keys for the rewards of heroes (about 3 seconds). `0` = no rewards over the hidden buttons. |

### `ux`

| Key | Default | Meaning |
|---|---|---|
| `fontFamily` | `"Fondamento"` | The font of the title and result cards. It is read from your CK3 install; a serif font is used if it is missing. |
| `notifications` | `true` | Show tray balloons for battle events. Warnings are always shown. |
| `ck3Windowed` | `"notice"` | What to do when CK3 runs in a window: `"notice"` (tell you once per CK3 start), `"borderless"` (stretch CK3 borderless over its monitor during play, restored when the bridge quits) or `"ignore"`. |

### `battleClock`

| Key | Default | Meaning |
|---|---|---|
| `enabled` | `true` | `false`: the result is applied at once when you click "The dust settles...", and CK3 ends the battle immediately, without native war score. |
| `paceDays` | `12` | The combat day by which CK3 has fought out the Bannerlord result (6 to 13). |
| `reinforcementClash` | `true` | Offer a second clash when the Bannerlord loser is reinforced. |
