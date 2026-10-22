# Release tools

Node 20 or newer, no packages. The .NET 10 SDK builds the bridge, the Bannerlord module and the installer.

| File | What it does |
|---|---|
| `version.json` | The release version. It is the only place the release script reads it from. |
| `build_release.mjs` | Builds, tests and packages a release into `dist/`. |
| `leak_scan.mjs` | Checks a folder or a release archive for personal data, machine paths, secrets, game code and copied game files. |
| `make_notices.mjs` | Writes `THIRD-PARTY-NOTICES.txt`: the licences and notices of the .NET runtime that `CK3xBL.exe` bundles, from the runtime packs in the NuGet cache. |
| `lib/zip.mjs` | Writes and reads zip archives (the manual-install zip, and the scan of archives). |
| `lib/bundle.mjs` | Reads a .NET single-file exe, so the scan can look at the files inside `CK3xBL.exe`. |

## Build a release

```
node tools/release/build_release.mjs
```

The steps run in this order. `--list` prints them, `--skip a,b`, `--only a,b` and `--until <step>` select them, and every
command is printed before it runs.

| Step | Checks or produces |
|---|---|
| `versions` | `bridge/Directory.Build.props`, `BridgeVersion.cs`, `SubModule.xml`, `descriptor.mod` and the installer project all carry `version.json`'s version. |
| `gen`, `lint`, `test-node` | The CK3 mod matches its generator (`gen_ck3_mod.mjs --check`), passes the lint, and the Node tests pass. |
| `bridge` | `dotnet build bridge/CK3xBL.slnx -c Release`, then the self test. |
| `module` | The Bannerlord module and its self-check. |
| `publish` | `CK3xBL.exe`, self-contained for win-x64, as a single file. Players need no .NET install. |
| `payload` | The installer payload: `app/` (CK3xBL.exe, `data/`, `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt`), `ck3mod/ck3xbl/`, `blmodule/CK3Bridge/` (8 files, no `.pdb`). `data/` holds only the files the bridge loads (`RUNTIME_DATA` in `build_release.mjs`); the generator inputs in `bridge/data` stay in the repository. The step fails when the code names a data file that is not on that list, when `LICENSE` or `THIRD-PARTY-NOTICES.txt` is missing, or when the notices are not those of the runtime the exe bundles (then run `node tools/release/make_notices.mjs --from-exe <the published CK3xBL.exe>`). |
| `gate` | The leak scan over the payload and over our own built assemblies. |
| `installer` | `dist/CK3xBL-Setup-<version>.exe`: the setup stub from `installer/src` with the payload packed in by the pack tool of `installer/tests`. |
| `manual` | `dist/CK3xBL-<version>-manual.zip`: the same files in the folders players copy them to, with `INSTALL-MANUAL.txt`. |
| `sums` | `dist/SHA256SUMS.txt`, then a last leak scan of the two release files. |

What the build needs:
- **CK3** (for `gen`, `lint` and the tests): set `CK3_GAME_DIR` to the `.../Crusader Kings III/game` folder, or let the tools find
  it in your Steam libraries.
- **CK3 script_docs dumps** (for `lint` and the knights generators): set `CK3XBL_SCRIPT_DOCS`, or keep them where CK3 writes them,
  in `Documents/Paradox Interactive/Crusader Kings III/logs`. To make them, start CK3 with `-debug_mode`, open the console and run
  `script_docs`. Use a throwaway game, because the console disables achievements for that playthrough.
- **Bannerlord** (for `module` and the knights generators): `--bl <root>`, or `BANNERLORD_DIR` / `BL_DIR`, or auto-detection from
  Steam.

Builds are deterministic, and no build path ends up in a binary: the solution's `Directory.Build.props` and the release script map
the source folder to `/_/`. The `gate` step checks this.

## Leak scan

```
node tools/release/leak_scan.mjs <folder | file.zip | file.exe> [--profile repo|release]
```

- **Fails on:**
  - user-profile and home-folder paths;
  - e-mail addresses other than GitHub `noreply` ones (third-party licence notices, which credit their authors, are
    exempt);
  - API keys, tokens and private keys;
  - disassembler output, or code declared in a game namespace;
  - files byte-identical to a file of the CK3 or Bannerlord install (found automatically, or `--ck3` / `--bl`);
  - binaries, fonts and media in a source tree;
  - images over 1 MB;
  - `.log` files other than the synthetic test fixtures;
  - files that never belong in the repository (settings, saves, symbols, keys).
- **Warns on:**
  - large files;
  - open work markers;
  - every image, which needs a look by eye.
- **Exit code:** 1 when anything fails.

With `--profile release` it also opens zip archives and reads the strings inside binaries. An archive appended to an exe
(the installer's payload) is scanned entry by entry and left out of the exe's own strings, and a .NET single-file exe
(`CK3xBL.exe`) is opened too: the files of ours in it are scanned, the .NET runtime's own files are skipped.

To regenerate the notices after a .NET update:

```
node tools/release/make_notices.mjs --from-exe <the published CK3xBL.exe>
```
