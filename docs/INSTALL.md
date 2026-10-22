# Installing Crowns & Blades

There are two downloads on the [releases page](https://melty.gg/m/crowns-blades):

| File | What it is |
|---|---|
| `CK3xBL-Setup-0.9.0-beta.exe` | The installer. One file; it installs everything and can uninstall it again. Recommended. |
| `CK3xBL-0.9.0-beta-manual.zip` | The same files in folders, for copying by hand. |
| `SHA256SUMS.txt` | Checksums of both, to check your download. |

Both contain the same three parts: the bridge app (`CK3xBL.exe`, self-contained, so you need no .NET install), the CK3 mod
"Crowns & Blades Battles" and the Bannerlord module "CK3 Bridge".

**Before you start:** close CK3 and Bannerlord. You need Crusader Kings III 1.20.x and Mount & Blade II: Bannerlord v1.4.8,
ideally the Steam versions (see [Requirements](../README.md#requirements)).

## The installer

The exe is not code-signed yet. Windows SmartScreen may show "Windows protected your PC": click **More info**, then
**Run anyway**. To check the file first, see
[Troubleshooting](TROUBLESHOOTING.md#windows-or-the-antivirus-warns-about-the-exe). On Windows 11 with **Smart App Control**
on, unsigned programs are blocked with no **Run anyway**; see
[Smart App Control blocks the exe](TROUBLESHOOTING.md#smart-app-control-blocks-the-exe).

The installer works for your Windows user only and needs **no administrator rights**. It asks for them only if Bannerlord's
`Modules` folder cannot be written otherwise (some installs under Program Files), and only for that one step.

### The pages

1. **Welcome.**
2. **License.** The MIT license and the not-affiliated notice. Accept to continue.
3. **Game folders.** The installer looks for both games in your Steam libraries, then in Steam's uninstall entries, GOG and
   Epic. Each folder has a **Browse...** button. A folder counts only if it holds `binaries\ck3.exe` (CK3) or
   `bin\Win64_Shipping_Client\Bannerlord.exe` (Bannerlord). The page shows the game versions and warns about versions this
   release was not built for, and about GOG and Epic, which are untested. It never refuses.
4. **Options.**

   | Option | Default | What it does |
   |---|---|---|
   | Install the CK3 mod "Crowns & Blades Battles" | on | Copies the mod into your CK3 `mod` folder in Documents |
   | Add the mod to CK3's current mod list | on | Adds one entry to CK3's `dlc_load.json`; a backup of the original is kept |
   | Install the Bannerlord module "CK3 Bridge" | on | Copies the module into Bannerlord's `Modules` folder |
   | Start menu shortcut | on | "Crowns & Blades" |
   | Desktop shortcut | off | |

5. **Installing.** Every file is checked against the setup's own checksums before anything is written.
6. **Finished.** It lists what was done and what to do next, and offers to start Crowns & Blades now.

### Where things go

| What | Where |
|---|---|
| The bridge app | `%LOCALAPPDATA%\Programs\CK3xBL\` (`CK3xBL.exe`, `data\`, licence files, `CK3xBL-Uninstall.exe`) |
| The CK3 mod | `Documents\Paradox Interactive\Crusader Kings III\mod\ck3xbl\` and `mod\ck3xbl.mod` (your real Documents folder, also when OneDrive moved it) |
| CK3's mod list | one entry, `mod/ck3xbl.mod`, in `Documents\Paradox Interactive\Crusader Kings III\dlc_load.json`; the original is kept once as `dlc_load.json.ck3xbl_backup` |
| The Bannerlord module | `<Bannerlord>\Modules\CK3Bridge\` |
| Settings, logs, battle archive | `%LOCALAPPDATA%\ck3xbl\` (settings in `config.json`, written with the game folders you chose) |
| Uninstall entry | Windows Settings, Apps ("Crowns & Blades") |
| Setup log | `%LOCALAPPDATA%\ck3xbl\setup.log` |

Nothing is written into the CK3 install, Bannerlord's own files, the Paradox Launcher's database or your Bannerlord settings.

### Safety rules the installer follows

- It never kills a running program. A running Crowns & Blades is asked to quit; a running Bannerlord only holds up the module
  step ("close Bannerlord, then Retry"); a running CK3 only postpones the change to the mod list.
- If a `ck3xbl` mod or a `CK3Bridge` module is already there and this installer did not put it there, it asks before replacing
  it. The old copy is moved to `%LOCALAPPDATA%\ck3xbl\setup-backups\` and put back when you uninstall.
- It never writes or deletes through a folder link (junction). Developers who link the module folder to a source checkout are
  told where the link points and offered to remove the link only.

## Enable the mod in the Paradox Launcher

The Paradox Launcher rewrites CK3's mod list (`dlc_load.json`) from the selected playset **every time you press Play**. So
the installer's change to the list lasts only until you next play from the launcher. To keep the mod enabled:

1. Open the Paradox Launcher and go to **Playsets**.
2. Choose the playset you play with (or create one) and click **Add mods**.
3. Tick **Crowns & Blades Battles** and add it.
4. Make sure that playset is the active one, then press **Play**.

If you start CK3 from the Crowns & Blades tray menu instead, the bridge checks the mod list before every launch. When the mod
is missing it offers **Enable and launch**, **Launch without it** or **Cancel**. When CK3 is started some other way without
the mod, the tray shows a warning.

Other mods in the playset are fine, but mods that change armies, cultures or combat are untested with Crowns & Blades.

## Bannerlord notes

- **You do not need to enable "CK3 Bridge" in the Bannerlord launcher.** The bridge starts Bannerlord itself, directly, with
  the module. If you do tick it for your own campaigns, it stays inactive there: the module only wakes up in a Bannerlord the
  bridge started.
- **Start Bannerlord once by hand** after installing, from its own launcher, and reach the main menu. On a fresh install or
  after an update, Bannerlord shows one-time notices before its first game. The bridge starts Bannerlord hidden, so it cannot
  click those away for you yet.
- **Safe-mode question:** after Bannerlord crashes or is killed, it asks at the next start whether to start in safe mode. In a
  Bannerlord the bridge started, the bridge answers "No" for you. When you start Bannerlord yourself, answer it yourself.
- **Your own Bannerlord campaign:** while you play Bannerlord yourself, the bridge leaves that game alone and waits for you to
  close it. Battles from CK3 cannot be offered in the meantime.

## Manual install from the zip

1. **Unblock the zip first.** Right-click the zip, **Properties**, tick **Unblock**, **OK**. Windows marks downloaded files, and
   Bannerlord refuses to load a marked `CK3Bridge.dll`.
2. Extract it. Inside you find `INSTALL-MANUAL.txt` and three folders.
3. **The bridge:** copy the folder `CK3xBL` anywhere you like, for example `%LOCALAPPDATA%\Programs\CK3xBL`, and start
   `CK3xBL.exe`. It finds both games itself; if it does not, use **Set game folders...** in its tray menu.
4. **The CK3 mod:** copy the contents of `Crusader Kings III\mod` (the folder `ck3xbl` and the file `ck3xbl.mod`) into
   `%USERPROFILE%\Documents\Paradox Interactive\Crusader Kings III\mod` (or your moved Documents folder). Then add
   "Crowns & Blades Battles" to your playset as above. If the launcher does not list it, open `ck3xbl.mod` in a text editor
   and change `path="mod/ck3xbl"` to the full path of the copied `ck3xbl` folder, with forward slashes.
5. **The Bannerlord module:** copy `Mount & Blade II Bannerlord\Modules\CK3Bridge` into the `Modules` folder of your
   Bannerlord install, for example `C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules`.
6. Start Bannerlord once by hand, as above.

A manual install has no uninstaller: to remove it, delete the three things you copied. Settings and logs are in
`%LOCALAPPDATA%\ck3xbl`.

## Upgrade

Run the newer installer over the old one. It keeps the game folders you chose, replaces the files, and removes files the new
version no longer has, but only if they are unchanged since they were installed (a file you edited is kept). Your settings in
`%LOCALAPPDATA%\ck3xbl\config.json` are never overwritten.

Running the **same** version again repairs the install. Running an **older** version asks first.

If you installed manually, quit Crowns & Blades (tray menu, **Quit**), then copy the new files over the old ones.

## Uninstall

Use any of these:

- Windows **Settings, Apps**, "Crowns & Blades", **Uninstall**;
- `CK3xBL-Uninstall.exe` in the install folder;
- `CK3xBL-Setup-<version>.exe /uninstall`.

The uninstaller removes exactly what the installer wrote:

- Files are deleted only if they are unchanged since install. A file you changed is yours, so it is kept and logged.
- CK3's mod list: if nothing changed it since the install, the original is restored from the backup. If the launcher or you
  changed it since, only the Crowns & Blades entry is removed.
- Copies the installer moved aside are put back.
- Your settings, logs and battle archive in `%LOCALAPPDATA%\ck3xbl` are kept unless you tick **Also delete my CK3 x
  Bannerlord settings, logs and battle archive**. Your saves are never touched.
- If something cannot be removed yet (for example Bannerlord is running), the rest is removed and the uninstaller stays, so you
  can run it again.

Remember to remove "Crowns & Blades Battles" from your Paradox Launcher playset too.

## Command line

The installer can run silently, write a plan without changing anything, or install into a test folder. For example:

```powershell
# write the plan to ck3xbl-plan.log in this folder, change nothing
Start-Process .\CK3xBL-Setup-0.9.0-beta.exe -ArgumentList '/silent','/dry-run','/log=ck3xbl-plan.log' -Wait
# install with the defaults
Start-Process .\CK3xBL-Setup-0.9.0-beta.exe -ArgumentList '/silent' -Wait
```

`/dry-run` without `/silent` opens the wizard in plan-only mode instead.

Every switch and exit code is listed in [installer/README.md](../installer/README.md#command-line).
