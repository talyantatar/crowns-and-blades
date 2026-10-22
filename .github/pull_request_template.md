## What and why

<!-- What does this change, and why? Link the issue it fixes, if any (Fixes #123). -->

## Which parts

- [ ] CK3 mod (`ck3_mod/`, `tools/gen/`)
- [ ] Bridge (`bridge/`)
- [ ] Bannerlord module (`bannerlord_module/`)
- [ ] Installer or release tools (`installer/`, `tools/release/`)
- [ ] Data tables (`bridge/data/`, `tools/data/`)
- [ ] Documentation

## How it was tested

<!-- The checks you ran, and for anything players see: what you did in the games and what happened. -->

## Checklist

- [ ] `dotnet build bridge/CK3xBL.slnx -c Release` builds with no warnings, and the self test passes.
- [ ] `node tools/gen/gen_ck3_mod.mjs --check`, the lint and `node tools/gen/test_ck3_mod.mjs` pass (if the mod or its data changed).
- [ ] The Bannerlord module builds and its self-check passes (if it changed).
- [ ] `docs/PROTOCOL.md` is updated (if a record, register, request field or command changed).
- [ ] The leak scan of a clean clone has no failures: no game files, no game code, no personal paths, saves, logs or desktop screenshots.
- [ ] Generated files were regenerated, not edited by hand.
- [ ] If an AI tool helped, I say so here and I have reviewed every line.
