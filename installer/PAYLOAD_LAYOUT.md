# Installer payload layout (format 1)

This is the contract between the release script (`tools/release`) and the installer (`installer/src`).
The release script fills a **staging folder**. The pack tool turns it into the player download,
`CK3xBL-Setup-<version>.exe`. Do not write `manifest.json` or the zip by hand.

## 1. Staging folder

```
<staging>/
  app/                                 -> %LOCALAPPDATA%\Programs\CK3xBL\
    CK3xBL.exe                           required: the self-contained bridge (dotnet publish -r win-x64 --self-contained)
    *.dll, ...                           anything else the publish folder holds (none for a single-file publish)
    data/
      ck3_maa_index.json                 required (the bridge finds its data through this file)
      *.json                             the other bridge tables, already scrubbed
    LICENSE.txt                          the repository's LICENSE; the wizard's license page shows it
    THIRD-PARTY-NOTICES.txt              the .NET runtime notice (it is redistributed)
    naval/CK3Bridge.Naval/               optional (built where War Sails is installed): the bridge copies it into
                                         Modules only when NavalDLC is there (PROTOCOL §44)
  ck3mod/
    ck3xbl/                            -> Documents\Paradox Interactive\Crusader Kings III\mod\ck3xbl\
      descriptor.mod                     required
      common/**, events/**, gui/**, localization/**
  blmodule/
    CK3Bridge/                         -> <Bannerlord>\Modules\CK3Bridge\
      SubModule.xml                      required
      bin/Win64_Shipping_Client/CK3Bridge.dll   required
      GUI/Brushes/*.xml, GUI/Prefabs/*.xml
```

Rules:

- **Only these three top-level folders.** In `ck3mod/`, only `ck3xbl/`; in `blmodule/`, only `CK3Bridge/`.
  The pack tool rejects anything else, and so does the installer.
- **Files are copied byte for byte.** Keep the UTF-8 BOM on the CK3 mod's `.txt` and `.yml` files (CK3 needs it).
- **Do not stage `mod/ck3xbl.mod`.** Setup writes it from `descriptor.mod`, adding an absolute `path=` line with forward
  slashes, in UTF-8 without a BOM. Any `path=` line in `descriptor.mod` is replaced.
- **Never stage** `*.pdb`, `app/config.json` (a developer's bridge settings), junctions or symbolic links, test files or
  internal notes. The pack tool refuses the first three.
- Build every binary without absolute build paths: `-p:DebugType=none -p:Deterministic=true -p:PathMap=<repo>=/_/`.
  The pack tool refuses any file, text or binary (scanned both as single-byte text and as UTF-16), that contains a
  Windows user folder path (`X:\Users\<name>\`). It also refuses every pattern passed with `--forbid` (see section 4).

## 2. manifest.json (written by the pack tool)

It is the first entry of the zip:

```json
{
  "format": 1,
  "product": "CK3xBL",
  "version": "0.9.0-beta",
  "supports": { "ck3": "1.20.*", "bannerlord": "v1.4.*" },
  "files": [
    { "path": "app/CK3xBL.exe", "size": 49551027, "sha256": "<64 hex>" }
  ]
}
```

- `path`: relative, `/` separators, no `.` / `..` / empty segments, no drive or colon, no trailing dot or space.
- `version`: `major.minor.patch[-pre]`. Setup compares it with the installed version: a newer one upgrades, the same one
  repairs, and an older one needs a confirmation (or `/allow-downgrade`).
- `supports`: version patterns. A trailing `*` means "starts with". The wizard shows a warning for other game versions
  and never blocks them.
- Setup checks every listed file (present, size, SHA-256) and refuses unlisted zip entries **before** it writes anything.
  It checks the hash again as it writes each file.

## 3. CK3xBL-Setup-<version>.exe

```
[ bare CK3xBL-Setup.exe (the stub, ~190 KB) ][ payload.zip ][ trailer, 32 bytes ]

trailer (little-endian):
  int64  zip offset  (= stub length)
  int64  zip length
  int32  format      (= 1)
  int32  reserved    (= 0)
  byte[8] "CK3XBLPL"
```

- The zip's own offsets are relative to its first byte. Setup reads it through a window that starts at `zip offset`.
- **Code signing.** Sign the final exe *after* packing. Authenticode appends its certificate table (8-byte aligned)
  behind the trailer. Setup finds the trailer through the PE security directory, so a signed setup still works.
- The uninstaller that setup copies into the install folder (`CK3xBL-Uninstall.exe`) is the stub alone, with the
  certificate entry cleared. It is about 190 KB, not the full download.
- The stub without a payload is a valid program: run on its own, it offers to uninstall an existing installation.

## 4. Building it

```powershell
dotnet build installer/tests/CK3xBL.Setup.Tests.csproj -c Release      # builds the setup stub too
$tool = "installer/tests/bin/Release/net48/CK3xBL.Setup.Tests.exe"
$stub = "installer/tests/bin/Release/net48/CK3xBL-Setup.exe"
& $tool pack --stub $stub --payload <staging> --version 0.9.0-beta `
       --out dist/CK3xBL-Setup-0.9.0-beta.exe `
       --ck3 "1.20.*" --bannerlord "v1.4.*" `
       --forbid "<pattern 1>" --forbid "<pattern 2>"                   # e.g. every forbidden_patterns entry of the leak list
```

- Exit code 0 means the setup was written and verified: it was re-opened, and every hash was checked.
- Exit code 2 means the payload is not ready. The output lists every problem: missing required files, `.pdb` files,
  `app/config.json`, user folder paths and `--forbid` matches. No output file is left behind.
- `--forbid` patterns are .NET regular expressions and are matched case-insensitively. Scanning a 50 MB single-file
  bridge with a dozen patterns takes about 25 s.
- The zip is deterministic: entries are sorted, timestamps are fixed and deflate is used. The same staging folder gives
  the same payload bytes.

## 5. Where each area goes

| Area | Destination | Recorded as |
|---|---|---|
| `app/**` | `%LOCALAPPDATA%\Programs\CK3xBL\` (or the folder chosen in the wizard) | `app` |
| (stub) | `<install>\CK3xBL-Uninstall.exe` | `uninstaller` |
| `ck3mod/ck3xbl/**` | `<Documents>\Paradox Interactive\Crusader Kings III\mod\ck3xbl\` | `ck3mod` |
| (generated) | `<Documents>\...\Crusader Kings III\mod\ck3xbl.mod` | `ck3descriptor` |
| `blmodule/CK3Bridge/**` | `<Bannerlord>\Modules\CK3Bridge\` | `blmodule` |

`<Documents>` is `Environment.SpecialFolder.MyDocuments`, the real Documents folder, including a OneDrive
redirection. Everything written is listed with its SHA-256 in `<install>\install_manifest.json`. Upgrades and uninstall
work only from that list.
