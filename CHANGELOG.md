# Changelog

All notable changes to Crowns & Blades. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- **Russian text** for the CK3 mod (`localization/russian`): the call to arms, battle results, combat summaries and steering modifiers.
- **Stat balance** (runs when the CK3 mod logs character health): prowess raises damage by 2.5 % per point (up to x2), CK3 character health scales the health pool (x0.7 to x1.5 around the base of 5), a commander's martial gives his whole army +1 % damage and health per point (up to +30 %) and +0.5 initial morale per point, and heroes over 50 lose 0.5 % speed per year (down to 85 %).

- **Battle gear.** Ten new inventory slots on a Battle Gear page of the CK3 inventory: main weapon, three sidearms, helmet,
  body armour, gloves, boots, cloak and horse. Every such artifact is one Bannerlord item, and in Bannerlord you carry exactly it;
  empty slots keep the equipment of your culture and rank, a horse makes you ride. Prowess from +1 up to +20 (the best main weapons),
  less for armour pieces, plus prestige, knight effectiveness or army speed. Bows get matching arrows.
- **The armory** (decision "Visit the Armory"): six offers that change every two years, from 200 gold up to 5000 gold plus prestige
  for the rarest pieces, dearer in earlier eras.
- Vanilla events that give a weapon, armour or bow give battle gear instead; AI rulers buy gear for their knights and themselves.

- **Strength balance.** A straight clash in Bannerlord now ends as CK3 would decide it: the bridge compares the sides' CK3
  strength with what Bannerlord's numbers would make of it and gives the short side damage and health (settings
  `strengthBalance`, `strengthTiersPerDoubling`, `strengthMaxFactor`). The bridge log records both and the field result.

- **Single combat in Bannerlord.** Every CK3 duel you fight yourself (challenges, trials by combat, tournaments, court events)
  asks whether to fight it with your own hand in Bannerlord or let fate decide. The winner in Bannerlord wins in CK3; a loser
  killed in a duel that may kill dies in CK3 too. Duels train the Blademaster trait (+20 to every weapon skill per level in
  Bannerlord), beating a stronger foe makes you Brave (or cures Craven), ten won duels make a Berserker, and half the time the
  winner takes one of the loser's artifacts.

- **Levies by development.** The development of the army owner's capital county arms his levies: rich lands field levies like a
  later era (more veterans, better kit), poor lands like an earlier one.

- **Skirmishes on the road.** The travel dangers with a human foe (bandits in the mountains, on a bridge or on the road,
  deserters, an assassin, soldiers trying to seize you, zealots) get one more option: fight it out in Bannerlord, you, your
  travelling knights and your guards against the band, 50 fighters at most. Every vanilla option is still there. A win brings
  gold, prestige and sometimes the foe's artifact; a loss costs gold; wounds and deaths follow CK3's battle odds.

- **Costumes.** Troops wear the armour of their CK3 culture and era: Norse in Vendel helmets, Celts in plaid, Byzantines in
  cataphract scale, Iberians in a mix of western and Andalusian kit, no plate before the late medieval era. Levies wear cloth,
  riders mail, archers hoods. Every piece has about the armour of the troop's own, so battles are as hard as before. With
  Open Source Armory and Open Source Saddlery (Nexus Mods) installed in Bannerlord's Modules folder, the bridge loads them and
  the choice grows from about 600 pieces to 2200. Should they crash Bannerlord (their shader cache of another game version: remove
  their `Shaders` folders), the bridge starts Bannerlord without them.

- **Battle feats.** Your own deeds in Bannerlord field battles earn CK3 rewards: Reaper (50 kills in a battle), Carnage (100),
  Living Legend (1000 in a lifetime), Slayer of Commanders, Miracle on the Field (a win against twice your numbers), Invulnerable
  (10 battles without a wound), Veteran's Scars (5 wounds survived), and the commander traits Unyielding Defender, Aggressive
  Attacker, Winter Soldier, Rough Terrain Expert, Forest Fighter and Desert Warrior for repeated wins. `"feats"` in the `battle`
  section.
- **Your wounds come from your own fight:** falling in Bannerlord always wounds you (10 % severely, two wound levels), and ending
  a battle under 10 % health wounds you too. No more maiming from a fall unless `playerDeathIsReal` is on.
- **Bannerlord restarts itself between battles when it holds too much memory** (it keeps the memory of every battle it fought):
  above 8 GB (`"recycleAboveMb"` in the `bannerlord` section) the idle, hidden Bannerlord is quit and a fresh one starts.

- **Storm the walls in Bannerlord.** While your army besieges a holding, the decision "Storm the Walls" is always open: the
  assault is fought on a Bannerlord castle or town of the defenders' culture (tribal holdings: palisades), its level by the fort
  level. What you bring depends on how far the siege has gone (the mod estimates it with CK3's own siege rule): ladders only at
  first, a siege tower from 20 %, a ram from 40 %, a second tower from 60 %, a breach in the wall from 80 %. Every siege regiment of
  your army (onagers, mangonels, trebuchets, bombards, ballistae, rocket carts) brings one Bannerlord engine; strong forts man
  ballistae and mangonels on their walls. Win, and the walls are yours: CK3 completes the siege within a day or two.
- **War elephants:** with Burning Empires installed (Nexus), CK3 war elephant regiments ride real elephants in Bannerlord (one
  elephant per four riders' worth of men, in harness). The bridge builds a small elephants-only module from your copy of Burning
  Empires; the conversion itself is never loaded. Elephants charge; their trunk attacks belong to Burning Empires' own code.
- **Prowess keeps counting past 40.** Damage, health and weapon skill grow (more slowly) up to prowess 100, and in duels and
  tournament bouts the stronger fighter gets up to 50 % more damage and 50 % less damage taken from the prowess gap.
- **Harder assaults.** The garrison fights as the holding's soldiers (heavy foot and archers, better behind stronger walls), the
  walls give the defenders damage, protection and morale, and the besiegers climb in waves: at most 2.5 of them per defender at once.
- **Battles of more than 1000 men** on the field at once can be tried (`battleSize` and `maxAgentsTotal` up to 1500), but Bannerlord's
  engine froze with 1500 in live play: 1000 stays the default. A frozen Bannerlord is now restarted on its own.
- **Fixed:** results lost after a battle, the long black screen on the way back and a stuck cursor: CK3's memory is no
  longer emptied for a battle unless RAM is short, the result waits until CK3 is ready, switching away no longer spends the
  retries, and the cursor no longer stalls while the result is typed.
- **Fixed:** a battle whose call to arms was open in a loaded save (after a crash or a restart) could not be fought: the bridge
  now takes its snapshot from the battle archive. A landing takes at most three ships (more crashed Bannerlord), and a landing you defend is fought on the coast (Bannerlord's
  raid only works with your own ships). Battles at sea now leave wounded, not only dead, as on land.
- **Fixed:** a won battle could be lost to CK3 when entering its result failed (CK3 still slow after the battle): the result is now
  typed again, slower, several times before giving up.- **Universal weapon slots.** The four weapon slots of the Battle Gear page take any weapon: two swords, an axe and a bow, or any
  mix. Prowess of all four adds up.
- **Feats for life, in three ranks.** Every feat buff now lasts a lifetime and grows to rank II and III when the feat is earned
  again. New feats: Last Stand, Knight Hunter, Bloody Harvest, Veteran of Many Fields, Sea Wolf, First to Board, Terror of the
  Coasts (battles and the sea), Unbeaten Blade, Champion Slayer, Merciful Victor (duels), Champion of the Lists, Unhorsed by None,
  Keen Eye (tournaments).- **Battles at sea (War Sails).** A fleet at sea can attack an enemy fleet nearby (decision "Attack the
  Enemy Fleet in Bannerlord", or the "Sails on the Horizon" event), and AI fleets may bear down on yours; the side attacked may slip
  away, by its commander's martial. With War Sails installed, the battle is fought ship against ship
  in Bannerlord (up to 8 ships a side, hulls of each side's naval tradition, Norse drakkars for North Germanic heritage), and half
  the men who flee the losing ships drown; an army that lands straight from the sea fights a landing at a coastal village. Without
  War Sails these battles are fought on the coast.
- **Fixed:** when the list of Bannerlord modules changes (new armour packs, or a restart without them), Bannerlord's "Mod change
  detected" notice is acknowledged by the bridge; a hidden game used to wait on it forever.
- **More armour:** Terra Armarium (Byzantine, Rus, late medieval) is loaded when installed and dresses the matching CK3 cultures;
  item packs' outdated shader caches are switched off automatically.
- **Heroes wear their culture:** commanders, knights and you are dressed in the armour of your CK3 culture and era wherever your
  CK3 gear and artifacts leave a slot to the kit, at about the kit's protection.
- **Fixed:** choosing to hold the walls (or strike the camp) entered Bannerlord before the new battle had loaded; the game sat in
  the foreground loading for seconds and Windows gave the focus to another window. The title card now stays until it is ready.
- **Fixed:** vanilla events that forge a weapon, armour or bow gave nothing at all (the mod's replacement did not take the
  arguments vanilla passes); they give battle gear again.
- **Fixed:** an error in CK3's log when a character listed for a battle had died before the cleanup.
- **Fixed:** AI travellers and AI tournament contestants could pick "fight in Bannerlord" in their own events, so far-away skirmishes
  were prepared and dropped again and again (and kept the field from you).
- **Fixed:** a memory restart of the idle Bannerlord could be mistaken for a crash of Open Source Armory, after which the
  costumes fell back to vanilla items for good.
- **Acclaimed knights lead their formation:** a knight whose accolade attribute leads archers, pikemen, cavalry and so on gives
  every such troop of your side in Bannerlord a captain's bonus while he stands (better aim and harder arrows for archers, harder
  hits and steadier lines for foot, stronger charges for horse; 2.5 % per accolade rank). When he falls, his formation loses heart.
- **Relieve a besieged holding.** When your army meets the besiegers of your own or a vassal's holding, the battle window offers
  two more ways to fight it in Bannerlord: break into the holding and hold the walls with its garrison (breaking in costs 10-20 %
  of your host, less with a good commander's martial), or fall on the besiegers' camp, where they are caught off guard and the
  garrison sallies out to join you.
- **The fate of a stormed holding:** once CK3 completes the siege you stormed, choose to sack it (gold and dread, a ruined and
  bitter county, a sin against your own faith), let the townsfolk ransom themselves, or spare the town (prestige and goodwill).
- **Siege feats:** *First on the Wall* for every holding you storm in person (the third makes you a Military Engineer), and
  *Unbroken Walls* for every defence of the walls you win (the third makes you an Unyielding Defender).
- **Battle gear as tournament prizes:** the winner of a martial contest has a 30 % chance of a piece of battle gear that fits
  it (a bow for archery, a horse or lance for the joust, a weapon for the duel, armour for wrestling and the melee), AI winners too.
- **Wrestling is quick:** wrestlers strip every piece of armour (undergarments, barefoot), their health is cut to 35 % and their
  blows count 1.5 times, so a bout is over in about a quarter of the time; the stronger wrestler keeps his edge.
- **Tournaments: joust and archery.** The joust is ridden in the arena on tournament horses with blunt lances; in the archery
  contest you have two minutes and two quivers to hit as many pots as the best other archer would (the mark is shown).
- **Bandit lairs:** bandits and deserters are fought at their camp (a hideout of the terrain); they sit by their fires until you
  come close.
- **Trophies:** a foe you strike down yourself may leave you one of his artifacts (60 % if he dies or is captured, 25 % if wounded).
- **Fugitives are pursued:** knights and commanders who flee the field of a lost battle may be caught (captured, or wounded).
- **Unique men-at-arms look their part:** huscarls, varangians, cataphracts, kheshig, mubarizun, longbowmen and 34 more wear a
  look of their own.
- **Cleave:** heroes with more than 30 prowess (you too) swing through the man they hit into the next one.

### Fixed

- 0.9.1, from players' crash reports on Melty:
  - When ck3.exe is set to run as administrator, the bridge could not start CK3 and said only "the requested operation requires
    elevation". It now offers to restart itself as administrator (Windows asks to confirm), so it can start CK3 and enter battle
    results into it; or the player unticks the setting. Nobody else ever sees an administrator prompt.
  - When Bannerlord dies before the bridge module answers, Bannerlord's own log of that run is copied into the bridge log, so a
    crash report shows the cause.
  - The boot failure notice no longer says "Bannerlord v1.4.8 may not be supported (this release was built for v1.4.8)"; on the
    supported version it points to Bannerlord's own log and launcher instead.
- Back in CK3 after a battle, the result could be typed while the battle window was not in front: the keys then reached CK3's
  own hotkeys, opening the character, realm, military and court windows, changing the speed and pressing menu items, over and
  over. The bridge now first checks that the battle window answers (two keys) and types the result only then; otherwise it asks
  you to close other windows and tries again only rarely, or when you come back to CK3.
- A family member without arms of his own (an unlanded son in a duel with his father) fought under a culture banner; he now
  bears his house's arms, the same as his kin on the other side.

- Big battles (800-1000 men at once) with costumes crashed Bannerlord at the order of battle: troops were re-dressed inside the
  engine's agent build. They are now dressed once deployment is over, 40 per frame.

- AI heroes no longer fight like champions at modest prowess: the best AI now comes at prowess 30 instead of 20 (a prowess 14
  knight took two minutes to beat in the lists).
- The arena crowd: townsfolk failed to take their seats (the stands stayed empty).
- The memory guard counts the room a Windows-managed page file can still grow, so it no longer shrinks battles needlessly.
- Coats of arms with an eagle come out as a plain field (not fixed yet: the eagle now uses an Empire eagle of the stock banners,
  and the Bannerlord log records the banner each team really carries, to find the cause).

- CK3 running in Russian (or any language that prints an em dash in scope dumps): characters and men-at-arms regiments were
  lost, so battles had only levies and no heroes.
- Russian localization no longer breaks the mod's lint and tests; the inventory's slot tabs fit the window again.
- A single combat fought in Bannerlord no longer makes you fight CK3's rounds again: CK3 goes straight to its aftermath with
  the Bannerlord winner (victory or defeat, wounds, and the consequences of the challenge: prestige, opinions, stakes).
- Title cards of a CK3 running in Russian no longer read "The Битва в провинции ...": the English article is added only to
  CK3's English "Battle of ..." names.
- Dev mode with a Russian keyboard layout: the console key also typed "ё", so CK3 got "ёrun ck3xbl_apply.txt" and the
  Bannerlord result never arrived. The bridge now clears the console line before typing.
- Allies' battles in your war are no longer offered when none of your armies fights there (even with your armies not raised,
  an ally's battle pulled you into Bannerlord). Battles you, your knights or your own armies fight in are offered as before.
- An adventurer (landless) who joined a war fought allied battles on the wrong side: the Bannerlord win counted as a CK3 loss.
  The player's side is now the side whose leader is on the player's side of the battle's war.
- Bannerlord no longer crashes when a big battle starts on a PC short of memory (RAM plus page file): when the free memory would
  not hold the battle size, the bridge puts fewer men on the field at once and the rest come as reinforcements (`"memoryGuard"`
  in the `battle` section; the bridge log says when it happens). A larger Windows page file keeps the full size.
- Battle gear no longer logs "Couldn't determine 'asset' visual type" in error.log: every gear kind has a base-game model
  for the artifact window.

### Changed

- **Professionals take the field first.** When an army is larger than Bannerlord's battle size, the men-at-arms and knights'
  retinues of every regiment come first, mixed in proportion, and the levies arrive last, once too few professionals are left to
  fill the field. Before, each army's levies came before its men-at-arms.
- **Knights and commanders wear costumes too:** the armour of their kit takes the look of their own culture and era; CK3 battle
  gear and artifacts stay as they are.
- **Tournaments in Bannerlord** (Tours & Tournaments): in your own bouts of the single combat and wrestling contests and in the
  grand melee, the pivotal moment offers "Step into the lists yourself, in Bannerlord!". The bout is fought in the arena (wrestling
  bare-handed, the melee team against team); whoever wins there wins in CK3, and the contest goes on with its bracket, prizes and
  glory as always. Nobody dies in the arena.
- **AI heroes fight as well as their prowess:** knights, commanders and duelists that Bannerlord's AI plays block, parry and attack
  much better the higher their CK3 prowess (prowess 20 and more: the best Bannerlord's AI can do).
- **Arena duels:** the foe now comes at you instead of waiting; the arena's crowd no longer stands in a T-pose.
- **Bands on the road:** a random number of men, armed by the year (rabble around 867, solid tier 3-4 foot by 1100), and a leader
  whose prowess grows with the year too.
- **Bannerlord gives its RAM back to CK3** while it waits hidden for the next battle (`"trimWhenIdle"` in the `bannerlord` section).
- **Where single combats are fought:** against a noble foe in the arena of their culture, against a lowborn one (a bandit, a
  wanderer) in the woods. Skirmishes on the road are fought in the woods too.
- **Knights and commanders bear their own arms:** the coat of arms of their title, or of their house, on their heraldic shields
  and clothing, so you can tell the lords apart on the field; the side keeps its colours.
- **Russian battle texts:** with CK3 in Russian, the title and result cards, objectives, single combat and skirmish titles and
  Bannerlord's return footer are in Russian (`"language"` in the `battle` section: auto, en or ru).
- **Battle size 600** by default (was 500), up to 1000 with `"battleSize"` in the `battle` section. 800 and 1000 crashed
  Bannerlord (with Open Source Armory) at the order of battle in every big battle tried so far.
- **CK3 gives its RAM to Bannerlord during a battle:** a moment after the battle starts the bridge moves CK3's memory out of RAM
  (it is paused meanwhile and takes it back after the battle). Turn it off with `"trimCk3Memory": false` in the `battle` section.
- The bridge log now says after each battle whether Bannerlord's winner is the side CK3 would have favoured, with a count for the
  session (`[balance] ... CK3 check`).
- **Prowess makes heroes quicker on foot:** +0.4 % speed per point, at most +10 %, together with the slowdown of age.
- Costumes no longer change horse harness: some riders sat on invisible horses.
- Wounded characters start with 75 % of their health (wounded 1 and 2); severely wounded characters keep 50 %.
- **Men-at-arms damage and toughness reach Bannerlord as damage and health.** The tier rule still picks the troop, but a regiment
  stronger or weaker than its troop ladder no longer gets more or fewer soldiers. Instead, that overflow and the regiment's
  damage-against-toughness profile (relative to the regular MaA of its type) give its soldiers a damage and a health factor: half
  of the CK3 excess, each 0.6 to 1.6 (for example kheshig x1.31 damage, x1.53 health). Other group buffs multiply on top, capped at 0.5 to 2.

## [0.9.0-beta] - 2026-10-08

The first public release. Built for Crusader Kings III 1.20 and Mount & Blade II: Bannerlord v1.4.8 (Steam), on Windows 10
and 11.

### Added

- **Battles in Bannerlord.** Field battles you take part in, or that belong to your war, can be fought in Bannerlord. CK3
  pauses itself and asks; "Let my commanders decide the day" keeps CK3's own resolution.
- **Your armies and your people.** Levies and men-at-arms by type and culture, scaled to Bannerlord's battle size; you as your
  own character (or as your army's commander when you are away); commanders and knights of both sides as named heroes; faces
  rebuilt from your save; coats of arms on the banners; a battle map chosen for the CK3 terrain, climate and season.
- **A seamless switch.** Bannerlord waits preloaded, hidden and muted. A freeze frame of CK3 turns into a title card that
  dissolves into the battle; a result card brings you back.
- **Results back in CK3.** Losses per army and men-at-arms type, wounds, maimings, deaths and captures by CK3's own odds,
  prestige for your own kills, and prestige, glory and prowess for heroes who did well.
- **The battle clock.** CK3's own combat fights the result out over about 12 days, so war score, the battle summary and the
  aftermath are native. A second Bannerlord clash is offered when the loser is reinforced.
- **Knights and commanders.** Knight effectiveness, commander traits, accolades, artifacts and personal traits shape the
  heroes, their retinues and their side, within caps.
- **Achievement-friendly Normal mode.** Results enter CK3 through the mod's hidden buttons on CK3's modder hotkeys; the console
  and debug mode are never used. A Dev mode with the console is available for modders.
- **Tray app.** Launch CK3 in either mode, see the battle and Bannerlord state, abort hotkey Ctrl+Alt+Shift+End, Set game
  folders, Start with Windows, About with install checks and "Copy diagnostics".
- **Installer.** One self-contained setup exe, per user, no administrator rights, no .NET install needed; detects Steam, GOG and
  Epic installs; upgrade, repair and a clean uninstall. A manual zip for those who prefer copying files.
- **Documentation** for players, modders and contributors.

### Known issues

- The installer, the installed layout, and GOG and Epic detection are new and tested with automated tests only.
- Fixes made after the last play test still need an in-game check: playing as yourself when no army of your side has a
  commander, the wording of a second clash, war score when a leaderless loser is steered, bridge start-up, and leaner battle
  clock logging.
- Most knight and commander effects are tested in code but not yet in game.
- Achievements were checked in game on CK3 1.20.0.3; a re-check on 1.20.0.4 is pending.
- The exe is not code-signed: SmartScreen and some antivirus programs warn, and Windows 11's Smart App Control, when it is
  on, blocks the installer and the programs it installs with no override.
- Bannerlord must be started once by hand after installing, so it can show its first-start notices.
- Banner charges are the nearest Bannerlord icon of the right kind, not exact copies.
- The mod's texts are English only.

[0.9.0-beta]: https://github.com/graff0n/ck3-bannerlord-bridge/releases/tag/v0.9.0-beta
