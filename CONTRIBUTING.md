# Contributing to Crowns & Blades

Thank you for helping. Bug reports, play reports from setups we could not test, ideas and code are all welcome. Please follow
the [Code of Conduct](CODE_OF_CONDUCT.md).

## Reporting bugs

Use the [bug report form](https://melty.gg/m/crowns-blades). The most useful reports include:

- the versions: Crowns & Blades, CK3, Bannerlord, and where your games come from (Steam, GOG, Epic);
- **Copy diagnostics** from the tray menu's About window;
- `%LOCALAPPDATA%\ck3xbl\bridge.log` and `bannerlord.log`, and for a battle problem that battle's folder from
  `%LOCALAPPDATA%\ck3xbl\battles\`.

[TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) explains where everything is. Security problems go through private reporting, not
issues: see [SECURITY.md](SECURITY.md).

**Play reports are valuable too.** Several in-game checks are still open (see the [README](README.md#status)). If you played
battles on another store, another CK3 language, with other mods or on unusual hardware, an issue saying what worked and what
did not helps a lot.

## Suggesting features

Use the [feature request form](https://melty.gg/m/crowns-blades). Describe what you would like
to happen in the game, not only how to build it. Some limits are deliberate: nothing may cost players
their achievements in Normal mode.

## Working on the code

Start with [docs/HOW_IT_WORKS.md](docs/HOW_IT_WORKS.md) for the architecture and [docs/BUILDING.md](docs/BUILDING.md) for
the setup. The contract between the CK3 mod, the bridge and the Bannerlord module is [docs/PROTOCOL.md](docs/PROTOCOL.md).

Ground rules:

- **No game files and no game code.** Never commit files from CK3 or Bannerlord, CK3's `script_docs` dumps, or code copied or
  reconstructed from either game's binaries. Read what you need from the player's install at run time, as the existing code
  does. Short ids and names in the data tables are fine.
- **No personal data.** No absolute paths of your PC, no saves, no logs from your games, no screenshots that show your desktop
  or other programs.
- **Generated files are generated.** Do not edit the `ck3xbl_gen_*` files of the CK3 mod by hand: change
  `tools/gen/gen_ck3_mod.mjs` or the tables in `bridge/data/`, then regenerate. CK3 script files must be UTF-8 **with BOM**.
- **Contract changes go into PROTOCOL.md.** If you change a record, a register, a request field or a command, update
  `docs/PROTOCOL.md` and keep old partners working where you can (readers ignore what they do not know).
- **Normal mode must stay achievement-friendly:** no console, no debug mode, no runtime-written mod files.
- **Match the surrounding code.** Comments explain why; the bridge builds with no warnings.

### Before you open a pull request

```powershell
dotnet build bridge/CK3xBL.slnx -c Release                   # no warnings
dotnet run --project bridge/CK3xBL.SelfTest -c Release        # all pass (the appearance tests need CK3 and one CK3 save)
node tools/gen/gen_ck3_mod.mjs --check                        # the mod is up to date
node tools/gen/lint_ck3_mod.mjs
node tools/gen/test_ck3_mod.mjs
```

Then commit, and run the leak scan over a clean clone of your branch (a working folder also holds build output, which the
scan rejects):

```powershell
git clone --quiet . "$env:TEMP\ck3xbl-check"
node tools/release/leak_scan.mjs "$env:TEMP\ck3xbl-check"     # no failures
Remove-Item -Recurse -Force "$env:TEMP\ck3xbl-check"
```

If you changed the Bannerlord module, build it and run its self-check too. If you changed something players see, say in the
pull request how you tested it in the games.

## AI-assisted contributions

This project itself was built with an AI coding agent (see the [README](README.md#how-this-was-made-ai-disclosure)), so
AI-assisted contributions are welcome. You are responsible for what you submit: read and understand every line, run the
checks above, and say in the pull request that an AI tool helped.

## License

By contributing, you agree that your contribution is licensed under the [MIT License](LICENSE) of this project.
