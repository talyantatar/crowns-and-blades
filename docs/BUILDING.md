# Building Crowns & Blades from source

This page is for contributors and maintainers. Players should use the installer: see [INSTALL.md](INSTALL.md).

## Prerequisites

- **Windows 10 or 11, 64-bit.** The bridge is a Windows app and the module a Windows game module.
- **.NET 10 SDK.** It builds the bridge (`net10.0-windows`), the Bannerlord module (`net472`, through the
  `Microsoft.NETFramework.ReferenceAssemblies` package, so no separate targeting pack is needed) and the installer (`net48`).
- **Node.js 20 or newer.** The CK3 mod generator, the lint, the data builders and the release tools are plain Node scripts
  with no npm packages.
- **Crusader Kings III 1.20** and **Mount & Blade II: Bannerlord v1.4.8**, installed. The module compiles against Bannerlord's
  own assemblies, and the generator and several tests read the CK3 install (read-only). Nothing is ever written into either
  install.
- **Git.**

## Repository layout

| Folder | What |
|---|---|
| `bridge/` | The bridge: `CK3xBL` (tray app), `CK3xBL.Core` (logs, delivery, Bannerlord link, orchestration), `CK3xBL.Mapping`, `CK3xBL.Appearance`, `CK3xBL.Ux`, `CK3xBL.SelfTest`, and `data/` (the mapping tables, read at run time) |
| `bannerlord_module/CK3Bridge/` | The Bannerlord module: `SubModule.xml`, `GUI/`, `src/` (the DLL), `selfcheck/` |
| `ck3_mod/ck3xbl/` | The CK3 mod. Most of it is **generated**: do not edit `ck3xbl_gen_*` files by hand |
| `shared/` | `Protocol.cs`, the data classes compiled into both the bridge and the module |
| `tools/gen/` | The CK3 mod generator, its lint, tests, the keypad reference encoder, the knights generators |
| `tools/data/` | The builders of the tables in `bridge/data/` |
| `tools/release/` | The release build and the leak scan |
| `installer/` | The setup program and its tests ([installer/README.md](../installer/README.md)) |
| `docs/` | This documentation and the [component contract](PROTOCOL.md) |

## Telling the tools where the games are

Nothing in the repository contains a machine-specific path. The tools find the games themselves, in this order: an explicit
option or environment variable, then your Steam libraries (from Steam's registry entry and `libraryfolders.vdf`), then the
default Steam folder under Program Files.

| Variable | Used by | Meaning |
|---|---|---|
| `CK3_GAME_DIR` | Node tools | The CK3 `game` folder (`...\Crusader Kings III\game`); the install root is accepted too |
| `BANNERLORD_DIR` | Node tools, module build, `build_release` | The Bannerlord root (`...\Mount & Blade II Bannerlord`) |
| `BL_DIR` | Node tools and `build_release` only | The same; a direct `dotnet build` of the module ignores it |
| `-p:BannerlordDir="<root>"` | module build | The same as an MSBuild property; wins over `BANNERLORD_DIR` |
| `CK3XBL_SCRIPT_DOCS` | lint, knights generators | Folder with CK3's `script_docs` dumps (see below) |
| `CK3XBL_CK3_GAME` | bridge self test | The CK3 install root, for the appearance tests |
| `CK3XBL_BRIDGE_ROOT` | bridge | The `bridge/` folder, for a binary built outside the repository |
| `CK3XBL_DATA` | bridge (appearance tables only) | A different folder for the face and banner tables; the rest of the bridge uses `CK3XBL_BRIDGE_ROOT` |
| `CK3XBL_GAME_LOG` | bridge self test (opt-in) | A real CK3 `logs/game.log` to decode in the `integration.live` test |
| `CK3XBL_UX_LIVE=1` | bridge self test (opt-in) | Run the `ux.live` tests, which draw on screen and change volumes |
| `CK3XBL_UX_VERBOSE=1` | bridge self test | Print the UX diagnostics |

If a game is not found, the module build stops with an error that says how to set its folder.

### CK3's script_docs dumps

The lint and the knights generators check every effect, trigger and modifier they write against CK3's own script
documentation. That documentation is Paradox's text, so it is not in this repository. Make your own copy:

1. Start CK3 with the launch option `-debug_mode`, in a **throwaway game**: using the console disables achievements for that
   playthrough.
2. Open the console and run `script_docs`.
3. CK3 writes `effects.log`, `triggers.log`, `event_targets.log`, `event_scopes.log`, `on_actions.log` and `modifiers.log` to
   `Documents\Paradox Interactive\Crusader Kings III\logs\`.

The tools read them from there (a moved Documents folder, for example by OneDrive, is found), or from the folder in
`CK3XBL_SCRIPT_DOCS`. The mod generator itself does not need them.

## The bridge

```powershell
dotnet build bridge/CK3xBL.slnx -c Release
dotnet run --project bridge/CK3xBL.SelfTest -c Release                 # every suite
dotnet run --project bridge/CK3xBL.SelfTest -c Release -- integration   # only tests whose name contains "integration"
```

The self test returns the number of failures as its exit code. It needs no running game: the integration suite replays a CK3
log generated from the mod's own statements (`bridge/CK3xBL.SelfTest/fixtures/`) through the whole pipeline. The appearance
tests read your CK3 install and your saves, read-only: they need CK3 installed (found as above, or set `CK3XBL_CK3_GAME`) and
at least one CK3 save from any campaign, and fail without them.

Run the bridge from the checkout with `dotnet run --project bridge/CK3xBL -c Release`, or start the built
`CK3xBL.exe`. In a source checkout (recognised by `bridge/CK3xBL.slnx`), the settings live in `bridge/config.json`, which git
ignores. `CK3xBL.exe --print-paths` shows which folders, games and checks it would use, without starting anything (it is a
Windows program: in PowerShell, pipe it to `Out-String` so the prompt waits for the output).

## The Bannerlord module

```powershell
dotnet build bannerlord_module/CK3Bridge/src/CK3Bridge.csproj -c Release -p:BannerlordDir="<Bannerlord root>"
dotnet build bannerlord_module/CK3Bridge/selfcheck/CK3Bridge.SelfCheck.csproj -c Release -p:BannerlordDir="<Bannerlord root>"
# then run CK3Bridge.SelfCheck.exe from bannerlord_module/CK3Bridge/selfcheck/bin/Release/
```

The self-check tests the module's own logic (request rules, buffs, text folding, the host gate) and its GUI files without
starting the game. The DLL goes to `bannerlord_module/CK3Bridge/bin/Win64_Shipping_Client/`, so the folder `bannerlord_module/CK3Bridge` is a
complete module. For development, link it into the game instead of copying:

```powershell
cmd /c mklink /J "<Bannerlord root>\Modules\CK3Bridge" "<repository>\bannerlord_module\CK3Bridge"
```

Close Bannerlord before you build: a running game locks the DLL. The installer recognises such a link and never writes
through it.

## The CK3 mod

The mod's data-driven parts (culture and terrain chains, the 200 keypad registers, the apply and battle-clock code, the knights
facts) are generated from `bridge/data/*.json` and the CK3 install:

```powershell
node tools/gen/gen_ck3_mod.mjs            # regenerate ck3_mod/ck3xbl
node tools/gen/gen_ck3_mod.mjs --check    # fail if the committed mod is out of date
node tools/gen/lint_ck3_mod.mjs           # every key checked against the script_docs dumps
node tools/gen/test_ck3_mod.mjs           # the mod's script run in a small interpreter, battle clock included
node tools/gen/knights/test_combat_traits.mjs
```

The knights generators (`tools/gen/knights/gen_*.mjs`) rebuild their tables in `bridge/data/` from the CK3 install; each has
`--check`. Remember that CK3 script files must be **UTF-8 with BOM**; the generator writes them that way.

To play your working copy, point a mod descriptor at it: a file `Documents\Paradox Interactive\Crusader Kings III\mod\ck3xbl.mod`
with the content of `ck3_mod/ck3xbl/descriptor.mod` plus a line `path="<repository>/ck3_mod/ck3xbl"` (forward slashes).

## The data tables

`bridge/data/*.json` are derived tables: ids, numbers and short names from both games, plus our own mappings. Most can be
rebuilt with the scripts in `tools/data/` (they find the games as above) and the knights generators. Five tables are frozen
extractions from Bannerlord v1.4.8 and CK3 1.20 whose generators are not published yet: `bl_ruler_kits.json`,
`bl_skins_human.json`, `bl_banner_colors.json`, `bl_troops_customgame.json` and `ck3_named_colors_to_bl.json`. Three more
are maintained by hand: `ck3_maa_index.json` (append-only; the keypad's men-at-arms numbering depends on its order), and the
inferred `ck3_emblem_to_bl_icon.json` and `ck3_gene_to_bl_face.json`.

## A release

```powershell
node tools/release/build_release.mjs --list     # the steps
node tools/release/build_release.mjs            # all of them
```

The version comes only from `tools/release/version.json`. The steps:

| Step | What |
|---|---|
| `versions` | Every project carries that version: `bridge/Directory.Build.props`, `BridgeVersion.cs`, `SubModule.xml`, `descriptor.mod`, the installer |
| `gen`, `lint`, `test-node` | The generator check, the lint and the Node tests |
| `bridge` | Build and self test |
| `module` | Build and self-check |
| `publish` | `CK3xBL.exe`, self-contained for win-x64, as one file |
| `payload` | The installer payload: the app with `data/` and the licence files, the CK3 mod, the module (no `.pdb`) |
| `gate` | The leak scan over the payload and every built assembly |
| `installer` | The setup stub, its self test, then `dist/CK3xBL-Setup-<version>.exe` |
| `manual` | `dist/CK3xBL-<version>-manual.zip` |
| `sums` | `dist/SHA256SUMS.txt`, and a last leak scan of the release files |

`--skip`, `--only` and `--until` select steps; `--bl <dir>` names the Bannerlord root. Every command is printed before it runs.
Builds are deterministic and carry no build-machine paths: `Directory.Build.props` maps the source folder to `/_/`, release
builds ship no `.pdb` files, and the `gate` step checks the binaries.

## The leak scan

```powershell
node tools/release/leak_scan.mjs <folder | file.zip | file.exe> [--profile repo|release]
```

It fails on anything that must never be published: paths into a Windows user profile, e-mail addresses other than GitHub's
`noreply` ones, API keys and private keys, game code, files byte-identical to a file of either game install, fonts, media,
stray binaries and logs. It warns on large files, open work markers and every image (images need a look by eye: no desktop,
taskbar or personal data). `--profile release` also opens archives and reads the strings inside binaries.

**For maintainers:** the public repository is exported from the maintainer's working tree by an explicit allowlist. Only listed
files are copied, each through the leak scan; a new file that is not classified stops the export. The release build runs from
that exported copy, so a release contains exactly what is published.

**For contributors:** run the leak scan over a clean clone of your branch before you open a pull request (a working folder
also holds `bin/` and `obj/`, which the scan rejects; see [CONTRIBUTING.md](../CONTRIBUTING.md)), and never commit game files, script_docs dumps, saves, logs or screenshots that show
your desktop.
