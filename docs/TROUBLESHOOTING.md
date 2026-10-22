# Troubleshooting

Start here when something does not work. If your problem is not listed, please
[open an issue](https://melty.gg/m/crowns-blades) with the versions and logs described below.

## First checks

1. **Right-click the tray icon, About Crowns & Blades...** It lists every check: both games found and their versions, the CK3
   mod installed and in CK3's mod list, the Bannerlord module installed, and whether all versions match. Anything marked as a
   warning is the place to start.
2. **Copy diagnostics** in the same window copies the version, the checks, the paths and the end of the logs to the
   clipboard, ready to paste into an issue. Your Windows user folder is shown as `%USERPROFILE%` in it.
3. In PowerShell, `& "$env:LOCALAPPDATA\Programs\CK3xBL\CK3xBL.exe" --print-paths | Out-String` prints the same facts
   without starting anything. (`CK3xBL.exe` is a Windows program; the `| Out-String` makes PowerShell wait for its output.
   After a manual install, use the folder you copied it to.)

## Where the logs are

| Log | Where | What |
|---|---|---|
| Bridge | `%LOCALAPPDATA%\ck3xbl\bridge.log` | Everything the bridge did: CK3 records, battles, Bannerlord states, deliveries, warnings |
| Switch and cards | `%LOCALAPPDATA%\ck3xbl\ux.log` | Title and result cards, focus changes, volume ramps |
| Bannerlord module | `%LOCALAPPDATA%\ck3xbl\bannerlord.log` | What the module did inside Bannerlord |
| Faces and banners | `%LOCALAPPDATA%\ck3xbl\cache\appearance.log` | Save reading and coat-of-arms rendering |
| Battle archive | `%LOCALAPPDATA%\ck3xbl\battles\<battle>\` | Per battle: `snapshot.json`, `request.json`, `result.json`, `outcome.json` (the newest 50 battles are kept) |
| Installer | `%LOCALAPPDATA%\ck3xbl\setup.log` | What setup did; the uninstaller logs to `%TEMP%\CK3xBL-Uninstall.log` |
| CK3 | `Documents\Paradox Interactive\Crusader Kings III\logs\` | `game.log` (the mod's records), `error.log` |
| Bannerlord | `C:\ProgramData\Mount and Blade II Bannerlord\logs\` | Bannerlord's own `rgl_log_*.txt` |

**Open battle log folder** in the tray menu (or a double-click on the icon) opens `%LOCALAPPDATA%\ck3xbl`. Paste `%LOCALAPPDATA%`
into the Explorer address bar to get there by hand.

The log files contain the names of your CK3 characters and the folders on your PC, which can include your Windows user name.
**Copy diagnostics** masks the user folder; for the raw files, look them over before you post them publicly.

## Common problems

### Windows or the antivirus warns about the exe

Crowns & Blades is not code-signed yet, so **SmartScreen** says "Windows protected your PC". Click **More info**, then
**Run anyway**. Some antivirus programs are also wary of what the bridge legitimately does (it presses keys in CK3 and briefly
holds back keyboard input while it types; see [SECURITY.md](../SECURITY.md)).

Only download from the [releases page](https://melty.gg/m/crowns-blades), and check the file before you
run it. In PowerShell, in your download folder:

```powershell
Get-FileHash .\CK3xBL-Setup-0.9.0-beta.exe -Algorithm SHA256
```

Compare the result with the line for that file in `SHA256SUMS.txt` from the same release page. If they differ, do not run the
file.

### Smart App Control blocks the exe

On Windows 11, **Smart App Control** may block the installer, and then `CK3xBL.exe` and `CK3xBL-Uninstall.exe`, because they
are not code-signed. Unlike SmartScreen, it offers no **Run anyway**. You can see whether it is on in Windows **Settings,
Privacy & security, Windows Security, App & browser control, Smart App Control settings** ("On", "Evaluation" or "Off").

If it blocks Crowns & Blades, the honest options are:

- **Wait for a signed release.** This beta is not signed; the [changelog](../CHANGELOG.md) will say when a release is.
- **Turn Smart App Control off.** We do not recommend this just for this mod: it protects your whole PC, and on many Windows
  versions it cannot be turned back on without resetting or reinstalling Windows. Read what your Windows says about turning
  it back on before you decide.

Crowns & Blades has not been tested with Smart App Control on.

### CK3 does not offer battles in Bannerlord

- **The mod is not enabled.** The Paradox Launcher rewrites CK3's mod list every time you press Play. Add "Crowns & Blades
  Battles" to your playset (see [INSTALL.md](INSTALL.md#enable-the-mod-in-the-paradox-launcher)). The tray warns when CK3
  starts without the mod.
- **The battle does not involve you.** Only battles you take part in, or that one of your own armies fights, are offered; allies' battles in your war are left to CK3. A siege goes
  to Bannerlord only when you storm it yourself (the decision "Storm the Walls").
- **Another battle is being fought.** Only one battle at a time goes to Bannerlord.
- **The bridge is not running,** or started after a problem. Look at its balloon or the About window.

### "The Bannerlord module is missing" or "CK3Bridge module not installed"

The bridge does not start Bannerlord without the module. Run the installer again (it repairs), or copy `Modules\CK3Bridge`
from the manual zip into your Bannerlord install. If you used the zip, make sure you **unblocked** it before extracting:
Bannerlord refuses to load a `CK3Bridge.dll` that Windows marked as downloaded. To fix it afterwards, right-click
`Modules\CK3Bridge\bin\Win64_Shipping_Client\CK3Bridge.dll`, **Properties**, tick **Unblock**.

### "The CK3 mod does not match the bridge" or "The Bannerlord module does not match the bridge"

Parts from different versions are installed. Run the installer of the version you want; it replaces all three parts.

### Bannerlord does not start, or starts and stops

- **Start Bannerlord once by hand** from its own launcher and reach the main menu. On a fresh install or after a game update it
  shows one-time notices, which the hidden start cannot click away.
- **A safe-mode question** ("The game shut down unexpectedly... start in safe mode?") appears after a crash. The bridge answers
  "No" in a Bannerlord it started. If you start Bannerlord yourself, answer it yourself.
- **Your own Bannerlord is open.** The bridge never touches a Bannerlord it did not start; the tray shows "your own Bannerlord
  is open". Close it, and the bridge starts its own.
- If Bannerlord fails to boot with both module lists, the bridge stops trying and tells you. Check `bannerlord.log` and
  Bannerlord's own log, then use **Bannerlord, Start Bannerlord** in the tray menu to try again.

### CK3 runs in a window, and I see my desktop during the switch

Set CK3's **Window Mode** to **Fullscreen** in its video options. Or set `ux.ck3Windowed` to `"borderless"` in the settings: the
bridge then stretches CK3 borderless over its monitor while it runs. See [PLAYING.md](PLAYING.md#ux).

### Bannerlord pauses or shows its menu by itself

Bannerlord's option "Stop Game On Focus Lost" pauses the battle whenever another window takes the focus: a chat message, a
firewall prompt, an update dialog, a game launcher popping up. Close such programs, or turn on Windows **Do not disturb** while
you play. A dialog that covers the screen can also stop the switch between the games; the bridge waits rather than typing into
the wrong window.

### The result does not arrive in CK3

After a battle, the event "The Armies Clash" should say "The dispatch has arrived." within a few seconds.

- **Do not use the keyboard or mouse while the result is sent.** The bridge waits for a quiet moment and holds back your input
  briefly while it types. Holding a key or a mouse button delays the delivery.
- **CK3 must be in front.** If you switched to another program, the delivery waits until CK3 is back.
- **The abort hotkey is shared.** If another program uses Ctrl+Alt+Shift+End or the Ctrl+Shift+Alt combinations, it may
  intercept keys. The bridge log says when the hotkey is taken.
- If nothing works, choose "Let the battle run its course without me." in CK3: CK3 then decides the battle itself.

`bridge.log` shows each attempt: `IN|OK` means CK3 accepted the result, `IN|BAD` that it was rejected (the bridge then sends it
once more).

### The battle in CK3 ends differently than in Bannerlord

After "The dust settles...", resume the game: CK3 needs several days of game time to fight the battle out (about 12 by
default). The Bannerlord winner wins, and the armies end with the Bannerlord survivors one day after the battle. Two things can
differ on purpose:

- After the battle, CK3 may still capture or kill some of the beaten side's knights and commanders, as after any battle.
- If you let the game run during the Bannerlord fight and CK3 ended the battle meanwhile, the survivors are set but the battle
  gives no war score.

### I want to stop a battle

Press **Ctrl+Alt+Shift+End**. See [PLAYING.md](PLAYING.md#the-abort-hotkey) for what it does at each moment.

### Achievements show as unavailable

- Start CK3 in **Normal** mode (tray menu) or from the Paradox Launcher. Dev mode uses `-debug_mode`, which turns achievements
  off for that game.
- Using the console disables achievements for that playthrough. That is CK3's rule, not the mod's, and the mod never opens
  the console in Normal mode.
- CK3's other achievement rules still apply.

## Reporting a bug

Please include:

- the Crowns & Blades version, and the CK3 and Bannerlord versions and stores (all in the About window);
- what you did and what happened;
- **Copy diagnostics** from the About window, and `bridge.log` and `bannerlord.log` if you can;
- for a battle problem, that battle's folder from `%LOCALAPPDATA%\ck3xbl\battles\`.

The [bug report form](https://melty.gg/m/crowns-blades) asks for exactly this.
