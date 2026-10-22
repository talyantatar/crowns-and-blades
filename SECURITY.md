# Security and what Crowns & Blades does on your PC

Crowns & Blades has to do some unusual things to join two games: it presses keys in CK3, briefly holds back your keyboard and
mouse, and moves windows and volumes around. This page lists all of it, so you can judge it, and so you know what is normal if
an antivirus program asks.

The source code is all here. Every claim below can be checked in it.

## In short

- **No network access.** The bridge, the installer and the Bannerlord module contain no network code. Nothing is uploaded, there
  is no telemetry and no update check. The only links (GitHub, in the About window) open in your browser when you click them.
- **No administrator rights** in normal use. The installer asks for them only if Bannerlord's `Modules` folder cannot be
  written otherwise, and only for that one step.
- **No game files are changed.** The CK3 mod goes into your CK3 `mod` folder and the module into Bannerlord's `Modules` folder;
  both are ordinary mods.
- **No game memory is read or written, and no code is injected.** The bridge talks to CK3 through its log file and its modder
  hotkeys, and to Bannerlord through files read by our own module.

## The bridge (CK3xBL.exe)

### What it reads

| What | Why |
|---|---|
| CK3's `logs\game.log` and `logs\dedicated_server.log` (read-only, while CK3 writes them) | The mod's battle records, and whether CK3 is paused |
| Your newest CK3 save (read-only) | The exact face genes of you and your heir, and coats of arms |
| Files of your CK3 install (read-only) | Coat-of-arms textures, CK3's font for the cards, the gene database, the game version |
| Files of your Bannerlord install (read-only) | The game version, and whether the module is installed |
| CK3's mod list `dlc_load.json` | Whether the mod is enabled |
| The Windows registry (read-only): Steam, GOG and Epic entries | To find the games |
| The list of running programs, and the command line of running Bannerlord processes | To find CK3 and to tell its own Bannerlord from one you started |
| The state of your keys and mouse buttons | To wait until you are not typing before it types |

### What it writes

| Where | What |
|---|---|
| `%LOCALAPPDATA%\ck3xbl\` | Settings (`config.json`), logs, the battle archive, rendered coat-of-arms images, remembered volume levels (`audio.json`), and the files it exchanges with Bannerlord |
| CK3's `dlc_load.json` | When you choose **Enable and launch** before a launch, or when Melty's **Play** starts it. It adds one entry and keeps a one-time backup. |
| CK3's `mod\ck3xbl.mod` | Only when the mod folder is there without it (an install through Melty): the descriptor, pointing at the mod folder |
| Bannerlord's `Modules\CK3Bridge.Naval` | Only when War Sails is installed: copied from the bridge's own files |
| Bannerlord's `Modules\CK3Bridge.Elephants` | Only when Burning Empires' `BurningEmpires_Elephants` is installed: built from its elephant files (copies or hard links) |
| Open Source Armory, Saddlery and Terra Armarium in `Modules` | Only when they are installed: a `Shaders` folder built for another game version is renamed `Shaders.ck3xbl_off` (it crashes Bannerlord v1.4.8), and Terra Armarium's `SubModule.xml` gets `CustomGame` so its items load in custom battles (the original kept as `SubModule.xml.ck3xbl_bak`). Each change is logged. |
| CK3's `run\ck3xbl_apply.txt` | Only in **Dev mode**: the result as a script file for the console |
| `HKEY_CURRENT_USER\...\Run` | Only if you tick **Start with Windows** |

### Keyboard and mouse

- **Key presses into CK3.** After a battle, the bridge types the result into CK3 with Windows' `SendInput`: Ctrl+Shift+Alt
  combined with digits, F1, F2, F3 and F5, the modder hotkeys the mod's hidden buttons listen to. It does this only after you
  chose to fight the battle, only while CK3 is the foreground window (it checks before every key), only when you are not
  holding a key, and only after two probe keys showed that the battle window answers (so nothing lands in CK3's own menus). In **Dev mode** it also opens CK3's console, types one `run` command and closes it.
- **Holding back your input.** While it types, the bridge uses low-level keyboard and mouse hooks to hold back your own key
  presses, mouse clicks and wheel turns, so they cannot mix with its keys. Each block lasts at most 4 seconds and ends by
  itself; mouse movement and key releases always pass. The hooks are installed only around the switch between the games and
  the delivery, and removed afterwards. Besides blocking, they only note *when* you last used the keyboard or mouse; **they
  never record which keys you press.** You can turn the blocking off with `delivery.blockPhysicalInput`.
- **Bringing a game to the front** may involve a short Alt key tap, a standard Windows technique, and only when no other
  program of yours is in front (see below).
- **One global hotkey**, Ctrl+Alt+Shift+End, to abort a battle.

### Windows and sound

- **One frame of CK3** is captured into memory for the transition (the freeze frame). It is never saved.
- **An overlay window** shows the title and result cards. It is click-through, never takes the focus, and hides itself when
  you switch to another program.
- **Bringing a game to the front:** the bridge brings CK3 or Bannerlord to the foreground when a battle starts or ends. It never
  takes the foreground from a program you switched to, and it never leaves a window "always on top".
- **Bannerlord's window** is hidden (cloaked) and shown by our module inside Bannerlord, at the bridge's request.
- **CK3's window** is only restyled if you set `ux.ck3Windowed` to `borderless`, and it is put back when the bridge quits.
- **Volume:** the bridge fades CK3 and Bannerlord in and out through Windows' per-app volume. It remembers your own levels and
  never sets a game louder than you had it. If the bridge crashes mid-battle, it puts the levels back at its next start.

### Programs

- The bridge starts `ck3.exe` (from the tray menu, or at Melty's **Play**) and `Bannerlord.exe` (hidden, with our module) itself.
- It closes its own Bannerlord gracefully. It ends it by force only when its window has not responded for 30 seconds, or when
  its module has sent no sign of life for a minute past its limit (a frozen engine); then it starts a fresh one.
- A Bannerlord you started yourself is never sent commands, closed or ended.
- After a Bannerlord crash, Bannerlord asks whether to start in safe mode; the bridge answers "No" in a Bannerlord it started,
  and confirms its "Mod change detected" notice.
- **Memory:** to make room for a big battle, the bridge may ask Windows to move CK3's unused memory pages to the page file
  (`EmptyWorkingSet`) when free RAM is low, and does the same for its own idle Bannerlord. Nothing in that memory is read or
  changed; CK3 pages it back in as it needs it.

## The installer (CK3xBL-Setup.exe)

It writes only the places listed in [docs/INSTALL.md](docs/INSTALL.md#where-things-go), records each file with its SHA-256 in
`install_manifest.json`, and the uninstaller removes exactly those files (unless you changed them). It checks the integrity of
every file it carries before writing anything, never writes through folder links, and never ends a running program.

## The CK3 mod

Plain CK3 script. It writes log lines with `info_log`, shows its events, and reads the hidden buttons. It reads and writes no
files. Its data-driven parts are generated text that never changes while you play, which keeps CK3's checksum stable.

## The Bannerlord module

It does nothing unless the bridge started Bannerlord (it checks the `CK3XBL_HOST` environment variable, or a launch note the
bridge left a moment before). When active, it reads and writes only its exchange files in `%LOCALAPPDATA%\ck3xbl\`, reads
Bannerlord's own font and data, and hides or shows its own window.

## Reporting a vulnerability

Please **do not open a public issue** for a security problem. Use GitHub's private reporting instead: on the repository page,
open the **Security** tab and click **Report a vulnerability**. Describe the problem, the version, and how to reproduce it.

If that button is not there, open an issue that only asks for a private channel, without any details, and the maintainer will
arrange one.

This is a hobby project maintained in spare time. We aim to answer within a week and to fix confirmed problems in the next
release. Only the latest release gets security fixes.
