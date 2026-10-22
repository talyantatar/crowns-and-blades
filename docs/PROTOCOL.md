# Crowns & Blades: component contract (protocol v2)

> **About this document.** This is the technical reference for modders and contributors; players do not need it (start with
> the [README](../README.md), and see [HOW_IT_WORKS.md](HOW_IT_WORKS.md) for the big picture). It grew with the project:
> sections 0 to 4 are the base contract, and each numbered addendum after them refines it, so a later section wins over an
> earlier one.
>
> **0.9.0-beta, installed mode.** The installer puts the bridge in `%LOCALAPPDATA%\Programs\CK3xBL\`, with `data\` next to
> `CK3xBL.exe`; its settings are `%LOCALAPPDATA%\ck3xbl\config.json` (a source checkout keeps `bridge/config.json`). The
> bridge starts Bannerlord with the environment variable `CK3XBL_HOST` (the bridge's process id) and, just before, writes a
> short-lived launch lease `%LOCALAPPDATA%\ck3xbl\bannerlord_launch.json` (`{"pid": <bridge pid>, "t": <unix ms>}`). The
> module stays passive (no splash skip, no loading card, no heartbeat, no custom game) unless the variable is set or a fresh
> lease names a running bridge. A running Bannerlord is adopted only on a fresh heartbeat from that process, no longer because
> of its command line (8.3). The bridge does not launch Bannerlord while `Modules\CK3Bridge` is missing.

This is the **single source of truth** for how the three components talk to each other. If code and this file disagree,
fix the code. This file only says *what* must happen.

```
 CK3 (+ mod ck3xbl)  --(1) log records-->  bridge (CK3xBL.exe)  --(3) request.json/command.txt-->  Bannerlord (+ module CK3Bridge)
                     <--(2) result IN----                        <--(4) result.json/status.json----
```

Shared folder: `%LOCALAPPDATA%\ck3xbl\` (below: `$X`). All JSON is UTF-8 without BOM, camelCase keys, written atomically
(`<file>.tmp` then rename). All CK3 script files are UTF-8 **with BOM**.

---

> **Superseded by §39 (siege assaults) and §40 (relief battles); kept for the field-battle contract.** Original design decision: sieges stay in CK3. CK3 keeps its own siege/assault mechanics. Only CK3 *combats* (field battles)
> are fought in Bannerlord, and they are always Bannerlord **field** battles: `kind` is always `"field"`, `siege` is `null`, and the
> module never opens siege missions. A combat at a besieged holding (relief army vs besiegers, sortie) is still a field battle; the
> location facts (holding, fort level, siege flag) may only influence the scene choice (e.g. terrain near a settlement) and presentation.

## 0. Modes

| | **Normal** (default, achievement-capable) | **Dev** |
|---|---|---|
| CK3 launch | `ck3.exe -gdpr-compliant` (no debug flags) | `ck3.exe -debug_mode -suppress_error_log -gdpr-compliant` |
| OUT channel (1) | `info_log` / `info_log_scopes` → `logs/game.log` | same, plus debug.log |
| IN channel (2) | **Keypad**: hidden 1×1 GUI buttons on vanilla modder hotkeys (`_ctrl_shift_alt_<0-9>`, `_f1`, `_f2`, `_f3`, `_f5`) in event `ck3xbl_battle.0002` → scripted GUIs → registers → static apply effect | **Run file**: `Documents/.../run/ck3xbl_apply.txt` + console `run ck3xbl_apply.txt` |
| Console use | never | allowed |

The bridge config key `mode` = `normal` | `dev`. Both modes share one mod build. The mod never writes or reads files.

---

## 1. CK3 → bridge: log records (OUT)

### 1.1 Line grammar
Each record is one marker line followed (optionally) by a scope dump.
- Marker: `info_log = "CK3XBL|<TYPE>|<arg1>|<arg2>..."`. The engine writes
  `[HH:MM:SS][I][jomini_effect_impl.cpp:NNN]: file: <path> line: <n> (<context>): CK3XBL|...`
  (the `(<context>)` part is missing for run files). Parse with: `CK3XBL\|(?<rec>[^\r\n]*)$` on lines that match the timestamp prefix
  `^\[\d\d:\d\d:\d\d\]\[[DIWE]\]\[[^\]]+\]: `.
- Dump: when the record carries values, the very next statement is `info_log_scopes = yes`. That emits a timestamped line whose
  message is the **current scope print**, then continuation lines **without** a timestamp: `Root: ...`, a blank line,
  `Saved event targets:`, then `key: value` lines. The dump ends at the next timestamped line.
- Every saved key the mod emits starts with `ck3xbl_`. Saved scopes **persist** for the rest of the effect, so a dump also
  contains keys of earlier records. The parser takes, for each record type, **only the keys listed for that type below**.
- Strip control bytes `\x15` and `\x16` before matching (they wrap tooltip fragments).
- Values print as decimals with 2 places (`709.00`). Scope prints look like:
  - character: `Name of c_title (Internal ID: 10811 - Historical ID 189226) weak (Character - 10811)!` (historical part optional)
  - army: `<army name> weak (Army - 16777235)!`
  - regiment: `'TOOLTIP:MEN_AT_ARMS_TYPE,<maa_key> L; <Name>!!' (Internal ID: '16706' - Type: 'personal' ) weak (Regiment - 16706)!`
    Type may be `personal`, `title` (then `- Owner ID: '<n>' - Controller ID: '<n>'` follows), or `unknown`.
  - province: `Sanda (Internal ID: 8199) weak (Province - 8199)!`
  - title: `Sanda (Internal ID: 10110 - Internal Key: c_sanda) weak (Landed_title - 10110)!`
  - combat: `Battle of Sanda weak (Combat - 134217751)!`, combat side: `Attacker weak (Combat_side - <id>)!`
  - faith: `west_african_pagan (88) weak (Faith - 88)!`; culture prints only its display name (use the CX lines below).
- Regexes to extract ids: `\((?<type>Character|Army|Regiment|Province|Landed_title|Combat|Combat_side|Faith|Culture) - (?<id>\d+)\)`,
  `Internal Key: (?<key>[A-Za-z0-9_]+)`, `MEN_AT_ARMS_TYPE,(?<maa>[A-Za-z0-9_]+)`, `Historical ID (?<hid>[A-Za-z0-9_]+)`.
- debug.log and game.log are recreated at every CK3 launch: if the file shrinks, reset the offset to 0. Open with
  `FileShare.ReadWrite | FileShare.Delete`. Timestamps are wall-clock without a date.

### 1.2 Battle snapshot (emitted in `on_combat_start`, only for player-involved combats)
Player-involved = some side has a participant, commander or knight with `is_ai = no`, or whose `top_liege` is the player.
The whole snapshot is emitted in the same tick, in this order:

| Record | Args | Dump keys (values unless noted) | Meaning |
|---|---|---|---|
| `B|BEGIN` | `<bid>` | `ck3xbl_bid`, `ck3xbl_year`, `ck3xbl_month`, `ck3xbl_day`, scopes `ck3xbl_combat`, `ck3xbl_prov`, `ck3xbl_county`, `ck3xbl_barony`, `ck3xbl_war` (optional), `ck3xbl_player` | Battle `bid` = `global_var:ck3xbl_seq` (1, 2, 3, ...). Province = the battle site. |
| `L|<field>|<value>` | — (no dump) | — | Static location facts from generated chains. Fields (the bridge stores **every** `L` field in a dictionary, so new fields never break parsing): `terrain` (17 keys of `common/terrain_types`), `winter` (`none`/`mild`/`normal`/`harsh`), `holding` (`castle_holding`/`city_holding`/`church_holding`/`tribal_holding`/`nomad_holding`/`herder_holding`/`temple_citadel_holding`/... or `none`), `coastal` (`yes`/`no`), `riverside` (`yes`/`no`), `siege` (`yes`/`no`, any army in a siege here), `raid` (`yes`/`no`), `occupied` (`yes`/`no`), `fort` (integer fort level), `dev` (integer county development), `region` (first matching climate region key from `bridge/data/mapping.json` `climate_regions`, or `none`), `building_gfx` (the holding culture building gfx key, or `none`), `loc_heritage` / `loc_culture` (culture of the county), `origin` (province id the attacker came from, or 0) |
| `S|<att\|def>` | `<bid>` | `ck3xbl_s_size`, scope `ck3xbl_s_primary`, `ck3xbl_s_player_in` (1 if the player is a participant/commander/knight on this side) | One per side |
| `A|<att\|def>` | `<bid>` | current scope = army print; `ck3xbl_a_tag`, `ck3xbl_a_size`, `ck3xbl_a_levies`, `ck3xbl_a_has_cmd` (0/1), scopes `ck3xbl_a_owner`, `ck3xbl_a_cmd` (only valid when has_cmd = 1) | One per army in combat at the site. `a_tag` = army variable `ck3xbl_aid` (global counter, unique for the session) |
| `R` | `<bid>` | current scope = regiment print; `ck3xbl_r_cur`, `ck3xbl_r_max`, `ck3xbl_r_hired` (0/1), `ck3xbl_a_tag` (of the army being iterated) | One per MaA regiment of the preceding army |
| `C|<role>|<att\|def>` | `<bid>` | current scope = character print; `ck3xbl_c_tag`, `ck3xbl_c_age`, `ck3xbl_c_female` (0/1), `ck3xbl_c_prowess`, `ck3xbl_c_martial`, `ck3xbl_c_health` (CK3 character health, optional; when absent the stat balance of the planner stays off), `ck3xbl_c_weight`, `ck3xbl_c_tier`, `ck3xbl_c_is_player` (0/1), `ck3xbl_c_army` (army tag or 0), scopes `ck3xbl_c_title` (optional), `ck3xbl_c_house` (optional) | Roles: `CMD` (army commander), `KNIGHT`, `PRIMARY` (side primary participant), `PLAYER` (the player when present but not otherwise listed). `c_tag` = character variable `ck3xbl_tag` (global counter; reused if already set). Tagged characters are added to global list `ck3xbl_chars`. |
| `CX|<field>|<value>` | — | — | Static facts about the **preceding** `C` record: `culture` (culture key), `heritage`, `unit_gfx`, `clothing_gfx`, `ethnicity`, `era`, `trait` (one line per appearance-relevant trait: `giant`, `dwarf`, `scarred`, `one_eyed`, `one_legged`, `blind`, `disfigured`, `beauty_good_1..3`, `beauty_bad_1..3`, `physique_good_1..3`, `physique_bad_1..3`, `albino`, `wounded_1..3`, `maimed`, `strong`, `weak`, `hunchbacked`, `spindly`, `bleeder`, `clubfooted`, `lisping`, `stuttering`, `gallowsbait`, `whole_of_body`) |
| `AX|culture|<key>` + `AX|heritage|...` + `AX|unit_gfx|...` | — | — | Culture of the **preceding** army's owner (army look) |
| `RX|culture|<key>` | — | — | For hired regiments: culture of `regiment_owner` (mercenary look) |
| `B|END` | `<bid>` | `ck3xbl_bid` | Snapshot complete. The bridge acts only after END. |

Unknown culture keys (hybrid/divergent cultures created in play) print `CX|culture|unknown`; the heritage and unit_gfx lines are
always present for a valid culture.

### 1.3 Player decisions and lifecycle
| Record | Args | Dump | Meaning |
|---|---|---|---|
| `CHOICE|FIELD` | `<bid>` | `ck3xbl_bid` | Player chose to fight in Bannerlord (event 0001 option a/c) |
| `CHOICE|AUTO` | `<bid>` | `ck3xbl_bid` | Player chose CK3 autoresolve. The bridge cancels any preload. |
| `CHOICE|ABANDON` | `<bid>` | `ck3xbl_bid` | Player gave up waiting in event 0002. Bridge: `cancel`/`abort`. CK3 resolves natively. |
| `J|<att\|def>` | `<bid of the combat or 0>` | like `A` | `on_combat_unit_join_side`: an army joined after the start. Informational (the bridge may ignore it once a BL battle is running). |
| `IN|OK` / `IN|BAD` | `<bid>` | `ck3xbl_bid`, `ck3xbl_in_count`, `ck3xbl_in_sum` | Keypad payload committed and its checksum verified (OK) or not (BAD). |
| `RES|BEGIN` / `RES|LIVE` / `RES|GONE` / `RES|A` / `RES|END` | `<bid>` | per type | Result application trace. `LIVE` = combat still existed, `GONE` = CK3 already finished it. `A` dumps `ck3xbl_a_tag`, `ck3xbl_after`. `END` = applied. |
| `END|<winner\|loser>` | — | `ck3xbl_bid` (0 if not ours), `ck3xbl_end_wipe` (0/1), scope `ck3xbl_end_primary` | `on_combat_end_*` for tagged combats: what CK3 finally decided |

### 1.4 Pause
`on_combat_start` (player-involved) sends `send_interface_toast = { type = msg_ck3xbl_battle_begins ... }` (filter `ck3xbl_battle`,
`auto_pause = yes`) and `trigger_event = ck3xbl_battle.0001`, so CK3 pauses in the same tick. The bridge never presses SPACE.

---

## 2. Bridge → CK3: result delivery (IN)

### 2.1 What gets applied (both channels)
For battle `bid`, in this order (`force_win` must be last):
1. Per army tag: `deplete_army_by_percentage = levyLoss` (fraction of the army's **levies** lost), then for every MaA type in that army
   `every_army_maa_regiment = { limit = { is_maa_type = <key> } change_maa_troops_count = { value = maa_max_troops_count multiply = <survivorShareOfMax> subtract = maa_current_troops_count } }`.
2. Per character tag: fate `0` unharmed, `1` wounded (`increase_wounds_no_death_effect = { REASON = fight }`), `2` maimed (`maimed_in_battle_effect = yes`),
   `3` captured (winner's side primary participant imprisons, only if at war), `4` killed (`save_died_in_battle_variables_effect` + `death = { death_reason = death_battle killer = <killer> }`).
   Killer = the character with tag `killerTag` if > 0.
3. `battle_event` lines on the combat sides (own loc keys with `_friendly`, `_enemy`, `_friendly_player`, `_enemy_player`).
4. Summary variables on the player for event `ck3xbl_battle.0003`: `ck3xbl_res_my_lost`, `ck3xbl_res_enemy_lost`, `ck3xbl_res_my_kills`, `ck3xbl_res_won` (0/1).
   From §14 on they are first saved as values of the same names, which the `RES` dumps carry. A payload 4 apply with outcome 1..3
   that arrives while another steered battle's summary window is open (`ck3xbl_res_showing`) leaves the player variables alone;
   `ck3xbl_clock.0003` writes them when it opens that battle's window (§14.4).
5. Outcome: `1` attacker wins / `2` defender wins → loser side `set_allow_early_retreat = yes` + `set_skip_pursuit = yes`, winner side `force_win = yes`.
   `3` abort → casualties only, no winner (CK3 continues the combat). `4` cancel → nothing.
Armies are re-found by `province:<prov> = { every_army_in_location = { limit = { var:ck3xbl_aid = <tag> } } }`; if missing there,
via `every_in_global_list = { variable = ck3xbl_armies ... }`. Characters by `every_in_global_list = { variable = ck3xbl_chars limit = { var:ck3xbl_tag = <tag> } }`.

### 2.2 Keypad channel (normal mode)
Registers are character variables `ck3xbl_r<N>` on the player, N = 0..199. Keys (all with Ctrl+Shift+Alt held):
| Key | Shortcut | Effect |
|---|---|---|
| `0`–`9` | `_ctrl_shift_alt_0..9` | `acc = acc*10 + d` |
| F1 | `_ctrl_shift_alt_f1` | store: `r[idx] = acc`, `idx += 1`, `acc = 0` |
| F5 | `_ctrl_shift_alt_f5` | seek: `idx = acc`, `acc = 0` |
| F2 | `_ctrl_shift_alt_f2` | commit: verify, set `ck3xbl_ready = 1`, log `IN|OK`/`IN|BAD` |
| F3 | `_ctrl_shift_alt_f3` | reset: all registers 0, `idx = 0`, `acc = 0` |
Values are non-negative integers < 1,000,000. Never use letters, F4 or F10.
Register map (fixed slots; unused slots stay 0 and are skipped with F5 seeks):

| Regs | Meaning |
|---|---|
| r0 | payload version = 2 |
| r1 | bid |
| r2 | outcome (1 attacker wins, 2 defender wins, 3 abort, 4 cancel) |
| r3 | province id (for re-finding armies) |
| r4..r7 | summary: my_lost, enemy_lost, my_kills, prestige_bonus |
| r10 + 14·i .. r23 + 14·i, i = 0..7 | army slot i: `+0` army tag, `+1` levy loss ‰ (0..1000), then six MaA pairs `+2+2k` maa type index (1-based index into `bridge/data/ck3_maa_index.json`, 0 = unused), `+3+2k` survivor share of max ‰ |
| r130 + 3·j .. r132 + 3·j, j = 0..19 | character slot j: tag, fate (0..4), killer tag |
| r199 | checksum = (sum of r0..r198) mod 9973 |
Commit (`ck3xbl_commit_effect`) verifies `r199` and `r1 = global_var:ck3xbl_pending_bid`. The apply effect (`ck3xbl_apply_effect`, static,
generated) runs when the player clicks the 0002 option ("The dust settles...") and `ck3xbl_ready = 1`; it then opens event `ck3xbl_battle.0003`.
If the player is quick and clicks before `IN|OK`, the option is disabled (trigger `var:ck3xbl_ready = 1`). The bridge may also press the commit key twice (idempotent).

### 2.3 Run-file channel (dev mode)
The bridge writes `Documents/Paradox Interactive/Crusader Kings III/run/ck3xbl_apply.txt` (BOM, temp + rename). The body is
`hidden_effect = { ... }`: it sets the same registers directly (`set_variable = { name = ck3xbl_r<N> value = <v> }` for every non-zero
register), then `ck3xbl_commit_effect = yes` (checksum + `ck3xbl_ready`, logs `IN|OK`/`IN|BAD`), then `ck3xbl_apply_effect = yes`
(the same static effect the keypad path runs when the player clicks the event 0002 option). Then it types `run ck3xbl_apply.txt` + Enter into the console (it opens the console with `` ` `` and closes it afterwards).
Afterwards it overwrites the file with `debug_log = "CK3XBL|NOOP"`.

---

## 3. Bridge → Bannerlord

### 3.1 Launch
`<BL>/bin/Win64_Shipping_Client/Bannerlord.exe /singleplayer _MODULES_*Native*SandBoxCore*CustomBattle*CK3Bridge*_MODULES_`
(working dir = that bin folder). Fall back to adding `*SandBox*StoryMode*` if the module reports it cannot start. Closing: write
`quit` to `command.txt`; if not exited within 10 s, `CloseMainWindow`. Never kill unless it is hung for > 30 s.

### 3.2 `$X/request.json` (written by the bridge, consumed and deleted by the module)
```jsonc
{
  "protocol": 2,
  "id": "b17",                     // battle key: "b" + bid + "_" + short session id
  "kind": "field",                 // always "field" (sieges stay in CK3)
  "scene": "battle_terrain_029",
  "season": "summer",              // summer | fall | winter | spring
  "timeOfDay": 12,                 // hours, valid stock values
  "playerSide": "defender",        // attacker | defender
  "playerIsGeneral": true,
  "presentation": {
    "preload": true,               // load the mission and hold it (paused, loading card) until command "go"
    "title": "The Battle of Orangallu",
    "subtitle": "Gunda Kakatiya against the Raiders of Hepung · Summer 870",
    "detail": "Hills near Orangallu",
    "objective": "Rout the Raiders of Hepung",
    "returnCountdownSeconds": 10,  // after the result; -1 = wait for the player
    "deployment": "order_of_battle" // order_of_battle | auto
  },
  "scale": { "menPerAgent": 6.2 }, // informational
  "attacker": SideRequest,
  "defender": SideRequest
}
```
`SideRequest`:
```jsonc
{
  "name": "Host of Gunda Kakatiya",
  "culture": "aserai",             // Bannerlord culture id
  "banner": { "code": "11.110...", "source": "title:c_orangallu" },   // optional; null = culture banner
  "general": "h0",                 // a heroes[].key, or a stock BL character id
  "heroes": [ HeroSpec ],
  "troops": [ { "id": "aserai_mameluke_cavalry", "count": 30, "group": "maa:armored_footmen#16706" } ]
}
```
`HeroSpec`:
```jsonc
{
  "key": "h0", "ck3Id": 10811, "tag": 3, "role": "player",   // role: player | commander | knight | primary
  "isPlayer": true, "name": "Gunda of Orangallu", "female": false, "age": 71, "level": 30,
  "formation": "Cavalry", "captain": true,
  "appearance": {
    "source": "meta_portrait",     // meta_portrait | save_dna | parent_inherited | ethnicity_seeded | stock
    "weight": 0.43, "build": 0.32, "height": 0.51,
    "skin": 0.87, "hairColor": 0.96, "eyeColor": 0.05,
    "hair": { "index": 0, "tags": [], "bald": true },
    "beard": { "index": 0, "tags": [] },
    "morphs": { "face_width": -0.12 },   // BL deform key id -> signed -1..1
    "bodyProperties": null               // optional raw override "<BodyProperties version=... />"
  },
  "equipment": { "roster": "ase_king_template_bat_m", "overrides": { "Item4": "banner_of_the_knight_t3" } },
  "skills": { "OneHanded": 116, "TwoHanded": 90, "Polearm": 100, "Bow": 40, "Crossbow": 30, "Throwing": 30,
              "Riding": 140, "Athletics": 90, "Tactics": 20, "Leadership": 60 },
  "healthMultiplier": 1.0
}
```
Rules: hero ids in Bannerlord are `ck3xbl_<id>_<key>` (no dots); released after the mission. Names are unique across both sides and
contain no `{`, `}` or `.`.

### 3.3 `$X/command.txt` (one command per write; the module deletes it after reading)
`preload` (implicit with request) · `go` (show the held battle) · `cancel` (drop the preload/battle, back to idle, result status
`cancelled`) · `abort` (end the running mission now, result status `aborted` with casualties so far) · `return_now` (skip the
countdown) · `quit` (exit the game) · `fg` / `bg` (uncloak/cloak the window; frame limiter 30 while hidden) ·
`handoff_to <hwnd>` (module calls `SetForegroundWindow(hwnd)` itself) · `title_in_engine` (§13.6: the held battle shows the
module's own title card; honoured while the battle is loading or held; if it arrives before its request has started, the request
waiting in request.json gets it when its battle starts; module v0.4.0+, older modules log "unknown command ignored").

### 3.4 `$X/bannerlord_status.json` (module heartbeat, every ≤ 1 s and on every change)
`{ "status": "booting|idle|loading|held|visible|fighting|result|fading_out|black|ended|cancelled|aborted|error", "battleId": "b17", "t": <unix ms>, "pid": <pid>, "hwnd": <hwnd>, "error": null }`

### 3.5 `$X/result.json` (module → bridge)
```jsonc
{
  "protocol": 2, "id": "b17",
  "status": "result",              // started | result | finished | aborted | cancelled | error
  "winner": "defender",            // attacker | defender | none
  "playerRetreated": false,
  "durationSeconds": 412.5,
  "attacker": { "requested": 300, "spawned": 300, "killed": 120, "wounded": 0, "routed": 40,
                "groups": { "<troop group>": { "requested": 30, "spawned": 30, "killed": 12, "wounded": 0, "routed": 3 } } },
  "defender": { ... },
  "heroes": [ { "key": "h0", "ck3Id": 10811, "tag": 3, "state": "alive|killed|routed|unspawned", "kills": 7, "killedBy": "e1" } ],
  "error": null
}
```
Groups are keyed by `TroopRequest.group` (the module tags agents by the group they were spawned from). Unspawned men are survivors.

---

## 4. Ownership of files
| Path | Owner |
|---|---|
| `ck3_mod/ck3xbl/**` | CK3 mod (generated parts produced by `tools/gen/gen_ck3_mod.mjs` from `bridge/data/*.json`) |
| `bridge/**` | bridge (C# net10.0-windows, `CK3xBL.exe`) |
| `bridge/data/*.json` | data tables (mapping, scenes, appearance, banners); read by the bridge at runtime, by the generator at build time |
| `bannerlord_module/CK3Bridge/**` | Bannerlord module (net472) |
| `shared/Protocol.cs` | the C# DTOs of §3 — compiled into **both** the bridge and the module. Plain public fields, PascalCase; JSON is camelCase (module: Newtonsoft + CamelCasePropertyNamesContractResolver; bridge: System.Text.Json + CamelCase policy + IncludeFields) |

---

## 5. ADDENDUM — CK3 mod as implemented (2026-10-03, `ck3_mod/ck3xbl`, generator `tools/gen/gen_ck3_mod.mjs`)

These points refine §1–§2. They are additions; nothing above is removed. Parsers must stay tolerant (ignore unknown
record types, unknown `L`/`CX`/`AX`/`RX` fields and unknown dump keys).

### 5.1 Markers carry no numbers
Static script cannot print a number into `info_log` text, so the `<bid>` argument column of §1.2/§1.3 is **never** part of the
marker. Every record that has a dump carries **`ck3xbl_bid`** in it (saved first, before any record); take the bid from there.
Exact marker strings (everything after `CK3XBL|`):
`B|BEGIN`, `L|<field>|<value>`, `LV`, `S|att`/`S|def`, `A|att`/`A|def`, `R`, `C|<CMD|KNIGHT|PRIMARY|PLAYER>|<att|def>`,
`CX|<field>|<value>`, `AX|<field>|<value>`, `RX|<field>|<value>`, `B|END`, `CHOICE|FIELD|AUTO|ABANDON` (one of),
`J|att`/`J|def`, `IN|OK`/`IN|BAD`, `RES|BEGIN|LIVE|GONE|A|END` (one of), `END|winner`/`END|loser`.
Records without a dump (`L`, `CX`, `AX`, `RX`) belong to the open snapshot (between `B|BEGIN` and `B|END`) or to the `J` record
just before them; a snapshot's lines are written in one tick, contiguously.

### 5.2 Snapshot order (one effect run, `ck3xbl_snapshot_effect`)
`B|BEGIN` → all `L` lines → `LV` → side att: `S|att`, then per army `A|att` + `AX…` + per regiment `R` (+ `RX…` if hired), then
`C|CMD|att` per army commander, `C|KNIGHT|att` per side knight, `C|PRIMARY|att`, `C|PLAYER|att` (each `C` followed by its `CX…`)
→ the same for def → `B|END`. A character appears at most once per battle (first role wins: CMD, KNIGHT, PRIMARY, PLAYER).
`C|PLAYER` is emitted on the player's side whenever the player is not already listed (also when only the player's realm fights).

### 5.3 `LV` record (numbers of the location; replaces `L|fort`, `L|dev`, `L|origin`)
Dump keys: `ck3xbl_l_fort` (province `fort_level`), `ck3xbl_l_dev` (county `development_level`, 0 without county),
`ck3xbl_l_has_origin` (0/1) and scope `ck3xbl_l_origin` (province print; read it only when has_origin = 1, else origin = 0).
Origin = where the largest attacking army came from (army variables `ck3xbl_cur_loc`/`ck3xbl_prev_loc`, kept up to date by
`on_army_enter_province`). Store them as `L` fields `fort`, `dev`, `origin`. `L|holding` may also print `other`; `L|terrain`
may print `unknown`.

### 5.4 Sentinels (saved scopes persist across records)
Read an optional scope only when its sentinel is 1: `ck3xbl_s_has_primary` → `ck3xbl_s_primary`; `ck3xbl_a_has_cmd` → `ck3xbl_a_cmd`;
`ck3xbl_c_has_title` → `ck3xbl_c_title`; `ck3xbl_c_has_house` → `ck3xbl_c_house`; `ck3xbl_end_has_primary` → `ck3xbl_end_primary`.
`ck3xbl_war` (B|BEGIN) is absent outside wars (B|BEGIN is the first record, so nothing leaks into it).

### 5.5 Extra static fields
- `CX|ethnicity|<key>` uses `has_ethnicity` over the 46 ethnicity database entries of the install (`unknown` if none matched).
- `CX|ctrait|<key>`: one line per commander trait held (mapping.json `ck3_emit.commander_traits`). `CX|trait` stays appearance-only.
- `CX|culture|none` is not printed: a character without a culture simply has no culture lines.
- `AX|era|<culture_era_*>` and `AX|nomadic|yes|no` (owner government has `government_is_nomadic`).
- `RX|heritage|…` and `RX|unit_gfx|…` follow `RX|culture|…`.
- Every culture chain ends in `unknown` (hybrid/divergent cultures created in play).

### 5.6 Joins (`J`)
Emitted only for combats that already have a snapshot (the dump's `ck3xbl_bid` is never 0). The joining army is tagged like an
`A` army; `J` is followed by `AX…`, its `R`/`RX` lines and `C|CMD|<side>` + `CX…` for its commander. If the joining army makes an
untagged combat player-involved (the player's host joins an AI battle), the mod emits a **full snapshot** for that combat and
opens event 0001, exactly as for `on_combat_start`.

### 5.7 Pending battle
`global_var:ck3xbl_pending_bid` is set by `CHOICE|FIELD` (not at combat start) and reset to 0 by `RES|END` or `CHOICE|ABANDON`.
The field options of event 0001 are only available while no other battle is pending. At `CHOICE|FIELD` the mod also stores the
battle province, both war leaders and the player's side in global variables, so the apply works even after CK3 ended the combat.
Tags of battles not fought in Bannerlord are released at `END|*`; a pending battle keeps them until it is applied or abandoned.

### 5.8 Keypad and apply details (both channels)
- `ck3xbl_ready`: 0 waiting, 1 committed (`IN|OK`), **2 applied**. The 0002 option "The dust settles..." is enabled for `ready >= 1`;
  it runs `ck3xbl_apply_effect`, which only acts when `ready = 1`. So in dev mode the run file applies first (ready 2) and the
  player's click just opens 0003. F3 resets `ready` 1 → 0 (a new payload follows); it never touches `ready = 2`.
- Registers exist (all 0) from `CHOICE|FIELD` on; `RES|END` and `CHOICE|ABANDON` remove them.
- Commit (F2) checks `r0 = 2`, `r199 = (r0 + … + r198) mod 9973` (reduced after every addition — same result), and
  `r1 = global_var:ck3xbl_pending_bid > 0`. Dump: `ck3xbl_bid` (= r1), `ck3xbl_in_sum` (the mod's checksum), `ck3xbl_in_count`
  (number of non-zero registers among r0..r198).
- Store (F1) ignores indexes ≥ 200. Values must stay < 1,000,000.
- **r3 is not read.** Script cannot address `province:<n>` from a variable, so armies are always found by tag in
  `global_list ck3xbl_armies`, which holds every tagged army wherever it is now. Send r3 anyway (diagnostics).
- Per army slot: one MaA pair per MaA **type** (all regiments of that type in the army get the same share). MaA types without a pair
  keep whatever `deplete_army_by_percentage` left them (≈ levy loss fraction).
- Fates: capture (3) only for outcomes 1/2, when the winner's war leader is at war with the loser's war leader and the character
  is not on the winning side; otherwise the character is left unharmed. Abort (3) applies casualties and fates without a winner;
  cancel (4) applies nothing but still logs `RES|BEGIN`, `RES|LIVE/GONE`, `RES|END` and cleans up. The 0003 summary is set for
  every outcome (`ck3xbl_res_outcome` = r2 as well). `battle_event` lines are written on the victim's side (keys `ck3xbl_wounded`,
  `ck3xbl_wounded_by`, `ck3xbl_maimed`, `ck3xbl_maimed_by`, `ck3xbl_slain`, `ck3xbl_slain_by`, `ck3xbl_captured`) while the combat
  is live. If the player character dies, event 0003 is not shown.
- `RES|LIVE` dumps scope `ck3xbl_res_combat`; `RES|A` dumps `ck3xbl_a_tag`, `ck3xbl_after`; all RES dumps carry `ck3xbl_bid`.

---

## 6. ADDENDUM — bridge mapper as implemented (2026-10-03, revised after review; `bridge/CK3xBL.Mapping`)

Additions to §2.1 and §3; nothing above is removed. Readers must stay tolerant (unknown group keys, unknown override slots).

### 6.1 Troop group keys (`TroopRequest.group`, echoed as `result.<side>.groups` keys)
| Key | Meaning | Written back to CK3 |
|---|---|---|
| `levy#<armyTag>` | the levies of one army | yes (levy loss ‰) |
| `maa:<maaKey>#<armyTag>#<regimentId>` | one men-at-arms regiment (`regimentId` = the `(Regiment - N)` id) | yes (per MaA type of the army) |
| `knight-retinue#<charTag>` | retinue agents of one knight; not CK3 soldiers | no |

A group is usually split over several `TroopRequest`s (one per Bannerlord troop id); the module sums them per group. Siege-weapon
regiments (`fights_in_main_phase = no`) are never spawned in a field battle.

### 6.2 Request details
- `heroes[].isPlayer` marks the hero the user controls (at most one per request): the CK3 player when commanding an army or riding
  as a knight in this battle (`role: "player"`), otherwise the commander of the player's largest army as a stand-in
  (`role: "commander"`). `C|PRIMARY`/`C|PLAYER` alone never put the player on the field. `playerIsGeneral` = the avatar commands an
  army, or is the side's primary participant and present; otherwise the player is a sergeant under the side's `general` hero.
- Each side's `general` hero has the highest `level` of its side (Bannerlord picks the AI general by `GetPower()`). A side without
  any CK3 commander or knight gets a stock custom-battle commander id (`commander_N`) as `general` and no heroes.
- `equipment.overrides`: an **empty string** value means "leave this slot empty". The mapper sends `"Horse": ""` and
  `"HorseHarness": ""` for nobles of cultures whose knights fight on foot (e.g. early Norse). A module that cannot clear a slot must
  skip the entry, not fail.
- `kind` is always `"field"` and `siege` is always `null` (§0). A CK3 **relief battle** (`L|siege = yes` at a castle, city,
  church or temple-citadel holding; the CK3 attacker is the army that marched in, normally the relief force) gets the place's ordinary
  field scene, the same one it would get without the siege. At a besieged **tribal** holding it gets a custom-battle **village**
  scene of the place's architecture (a village map opens like any field battle; the module applies its `forced_scene_level`).
  The siege shows only in the presentation: `detail` "Fields before <province>" (village: "The village of <county>"), `objective`
  "Break the siege of <province>" (attacker) / "Hold the siege lines before <province>" (defender). The note under module
  addendum 3 about `kind: "siege"` no longer applies: the mapper never sends it.
- `scene` is always a scene of a module in every launch of §3.1 (`Native`, `SandBoxCore`, `CustomBattle`; `module` field of
  `bridge/data/bl_scenes.json`). Most village scenes live in `SandBox`, which the normal launch omits, so they are never sent.
- `timeOfDay` is always one of 6/12/15/18/22 and `season` one of spring/summer/fall/winter.
- Agent budget: `MapperOptions.MaxAgentsTotal` counts all troop agents of both sides including knight retinues; named heroes are
  extra. Each side gets at least `min(MinAgentsPerSide, its CK3 men)` troop agents; when that floor applies, that side has its
  own men-per-agent scale (casualties are converted per group, so CK3 losses stay exact).

### 6.3 Result handling
- Groups are matched by group key (§6.1). Accepted fallbacks: groups keyed by **troop id** (losses split over the groups that requested
  that troop, by requested count) or only **side totals** (split by requested count). Unspawned agents are survivors.
- Troops: `killed` and `wounded` are both casualties. The module's random killed-vs-unconscious split (`IAgentStateDecider`,
  `casualties.troopDeathChance`) only changes what the player sees; CK3's fatal losses always come from the configured conversion
  (`MapperOptions.KilledToCk3DeadShareWinner` / `…Loser` for the main phase, `RoutedToCk3DeadShareLoser` as the pursuit base).
  The mapper does not send `casualties.*`, so the module's defaults apply.
- Heroes: `state` `killed` and `wounded` both mean "fell in battle"; CK3's combat-phase odds decide wounded / maimed / killed /
  captured. Only the CK3 player in person (`role: "player"`) is protected from death (unless
  `MapperOptions.PlayerDeathIsReal`) and capture; a stand-in avatar (`role: "commander"`, `isPlayer: true`) gets CK3's odds.
- `killedBy` is used only when it names a hero of the **other** side; otherwise (no killer, or friendly fire) the killer is the enemy
  side's general hero (CK3 credits battle deaths to the commander), or none. `MyKills` = the avatar's `kills` minus heroes of his
  own side whose `killedBy` is the avatar. Team kills of ordinary troops cannot be told apart in `kills`.
- `Ck3Outcome` lists only what changes CK3 (each entry costs keypad chords): `Armies` leaves out armies whose losses round to nothing (levy loss 0‰ and no MaA type with dead), and
  when an army's levy loss is 0‰ it carries pairs only for MaA types that lost men (a pair for an untouched type would only move men
  between its regiments); with a levy loss > 0 every slotted type keeps its pair, because `deplete_army_by_percentage` also cuts
  MaA. `Characters` holds only fates 1..4. Order: `Armies` by fatal losses (largest first); `Characters` the CK3
  player first, then by fate (killed, captured, maimed, wounded), so fixed slots (§2.2: 8 armies, 20 characters) drop the least.
- Not sent yet: the resolver's weather (`location.atmosphere.*`, module addendum 2). `BattleRequest` in `shared/Protocol.cs` has no
  `location` member and the bridge serialises the DTO only; the values exist in `BattlePlan.Battlefield` (`AtmospherePreset`,
  `Rain`, `Snow`, `FogDensity`) for when the DTO gains the member.

---

## Additions: Bannerlord module stage 1 (CK3Bridge v0.2.0, 2026-10-03)
Clearly-marked extensions of §3 by the module. All are optional for the bridge; requests without them keep working.

1. **Dictionary keys are verbatim.** Member names are camelCase, but keys of dictionaries (`groups`, `skills`, `morphs`,
   `overrides`) are written exactly as received. The module uses Newtonsoft `DefaultContractResolver` +
   `CamelCaseNamingStrategy(processDictionaryKeys: false)`; `CamelCasePropertyNamesContractResolver` (named in §4) would
   lower-case the first letter of every group key on write ("Levy#3" -> "levy#3"). The bridge must not set a
   System.Text.Json `DictionaryKeyPolicy` either.
2. **Optional request fields**, read from the raw JSON (not in `shared/Protocol.cs` yet):
   - `location.atmosphere.atmospherePreset` (file name under `Modules/Native/Atmospheres` without `.xml`, e.g.
     `TOD_12_00_HeavyRain`; unknown names fall back to the stock preset of the hour), `location.atmosphere.rain`,
     `.snow` (0..1), `.fogDensity` (0..10).
   - `location.battlefield.sceneLevels` (village scenes; when empty the module uses the scene's `forced_scene_level` from
     `custom_battle_scenes.xml`).
   - `casualties.troopDeathChance` (default 0.3), `casualties.heroDeathChance` (default 0.3) and
     `attacker|defender.heroes[].deathChance`: probability that a downed agent is killed instead of wounded.
3. **Normalisation** (never fails a battle): `kind` other than `field` is fought as a field battle (sieges stay in CK3);
   season `autumn` -> `fall`, unknown/null -> `summer`; `timeOfDay` snaps to the nearest stock preset hour 6/12/15/18/22;
   an unknown scene, or a siege / lords-hall scene of the custom battle list, -> `battle_terrain_029`; `scale.battleSize`
   (agents) -> the nearest `BannerlordConfig.BattleSize` option (200/300/400/500/600/800/1000), applied only while the
   mission is created (absent -> the user's own option); unknown culture -> `vlandia`; unknown troop ids, items, skills,
   rosters and slots, and items that do not fit their slot, are skipped with a log line; an `equipment.overrides` value
   `""` empties that slot (§6.2); an invalid `banner.code` -> the culture banner. `protocol` other than 2 -> `error` (a
   missing `protocol` = 2). Note: §6.2's `kind: "siege"` relief battles are fought as field battles under the §0 decision;
   a siege scene is replaced by `battle_terrain_029`, so the bridge should send a field scene for them.
4. **result.json**: written in order `started` (request accepted, mission opening), `result` (provisional, at the battle
   result), then exactly one of `finished` | `aborted` | `cancelled` | `error`. `aborted` is also used when the mission ends
   without a result for any other reason (escape menu "Exit to Main Menu", game closed during a battle). `winner` is `none`
   for `aborted`/`cancelled`. `heroes[].state` may be `wounded` (knocked unconscious). Side totals count every human agent
   of the side, heroes and a stock general included; `groups` count troops only; `requested` includes troops whose id was
   unknown (never spawned = survivors). `durationSeconds` = mission time from deployment end to the result (pauses
   excluded). `playerRetreated` = the player confirmed Tab -> Leave -> retreat before a result.
5. **bannerlord_status.json**: heartbeat every 0.75 s from the main thread (a long synchronous load step can stall it for
   a few seconds: allow >= 10 s while `loading`). `booting` until the idle state is reached (again after an exit to the
   main menu). After a battle the status shows `ended` | `cancelled` | `aborted` | `error` with its `battleId` for 2 s,
   then `idle`. Requests are accepted whenever the module sits in its idle state, also during those 2 s.
6. **command.txt**: several commands may be sent on separate lines; they run in order. `cancel` / `abort` with no
   running battle consume a pending `request.json` and answer it with a `cancelled` / `aborted` result. `handoff_to`
   takes a decimal or `0x` hex handle. `return_now` only acts in `result`. `go` before loading finishes skips the hold.

---

## 7. ADDENDUM — appearance and banners as implemented (2026-10-03, revised after review; `bridge/CK3xBL.Appearance`)

Clarifies how `IAppearanceProvider.Enrich` fills the §3.2 fields (it runs after the mapper). No field is added or removed.

### 7.0 Which save facts a battle may use (revised after review)
CK3 reuses internal character ids between campaigns, so the newest save (and the cache built from it) is used for a battle only
on **positive evidence** that it describes the live player (`B|BEGIN` `ck3xbl_player` and its `C` record):
- the live player is the save's meta player or heir and, when the battle has a `C` record for them, agrees with the save (sex;
  portrait age vs. live age; birth date ±1.5 years when known), or
- the live player is another character of a text save whose sex **and** birth date were both checked against the live record.
Without a save read yet, the same rule applies to the cache's recorded player/heir. Anything else (player absent from the save,
no save and no matching cache) -> the battle uses stand-in faces and `game:` coats of arms and writes nothing to the cache.
Binary saves do not record sex for ordinary characters: it is unknown there, never "male". The same agreement check runs per hero.

### 7.1 `heroes[]`
- `appearance` is set for every hero with `ck3Id > 0`. `source`: `meta_portrait` (exact CK3 genes from the save header: player and
  heir), `save_dna` (the character's stored `dna=`), `parent_inherited` / `ethnicity_seeded` (a stand-in seeded by `ck3Id`, the same
  face every battle), `stock` (no CK3 game data: only `weight`/`build` carry information; keep the stock face).
- `weight`, `build`, `height` (0..1) map to `DynamicBodyProperties`/`CurrentWeight`, `CurrentBuild` and `HeightMultiplier`.
- `skin`, `hairColor`, `eyeColor` (0..1) are `idx/(n-1)` of the nearest point of the adult gradient of the hero's gender in
  `bridge/data/bl_skins_human.json` (Native `skins.xml` order) -> `CurrentSkinColorOffset`, `CurrentHairColorOffset`, `CurrentEyeColorOffset`.
- `hair` / `beard`: `index` (adult `skins.xml` index for the hero's gender) wins when not null; `tags` are Bannerlord style tags in
  preference order (for `GetHairIndicesByTag` / `GetFacialIndicesByTag` when the index does not apply). `hair.bald` = index 0.
  Beard index 0 = clean-shaven (always for women, under-18s and men with the trait `beardless_eunuch`, as CK3's portrait
  modifier `special_beardless_eunuch`; `eunuch_1` keeps the beard).
  **Requested of the CK3 mod (revised after review):** add `beardless_eunuch` to the §1.2 `CX|trait` list (generator
  `APPEARANCE_TRAITS`). The bridge already honours it; until then derived (non-portrait) beardless eunuchs may get a beard.
- `morphs`: Bannerlord deform-key **id** -> signed -1..1 with the CK3 direction already applied; map around the runtime neutral weight.
  A missing key means neutral. Keys are lower-case ids (`face_width`), sent verbatim.
- `age` is replaced by the CK3 **portrait** (apparent) age, an integer 16..100.
- `equipment`: the mapper's `roster` and its `overrides` (including `""` = empty slot) are kept. Enrich only adds overrides:
  armour slots (`Head`, `Body`, `Cape`, `Gloves`, `Leg`) from another set of the same roster for variety, lower-tier `Head`/`Body`
  for barons and landless knights, a `using_tableau` shield in a free `Item0..3` slot when the kit has none, and a culture banner
  item in `Item4` for captains. Every id comes from `bl_ruler_kits.json` / `bl_catalog.json` (SandBoxCore/Native items).
- `skills` are filled only when empty; `name` is filled from CK3 only when empty, and always made unique without `{`, `}`, `.`.

### 7.2 `banner`
- `code` is a Bannerlord banner code: integers only, layer 0 = background mesh 11 over the 1528 canvas, at most 33 layers. Field divisions use icon
  mesh **510** as a plain rectangle (inferred from stock clan banners; to be confirmed in game), charges use the icon ids of
  `bridge/data/ck3_emblem_to_bl_icon.json` (most ids are placeholders inside the right icon category until the icon sheet is
  labelled). All colours are exact palette ids. Table values cannot raise the layer count past 33 (divisions and charges share
  the 32 icon layers).
- `source`: `meta:primary_title` (player, from the save header), `title:<key>` (save CoA database, text or binary save, or the
  cache), `game:<key>` (predefined CoA of the install), `meta:house` (player's house, from the save header), `house:<id>`.
  No `banner` = no CoA found: use the culture banner. Order for a side's primary participant: the save-time primary-title CoA when
  the live record names the same title (or none); else the live title key in the save's CoA database (binary saves do not say which
  title the header CoA belongs to, and the title may be new since the save); else the header CoA when its title is unknown; else
  cache, `game:`; else the house CoA.
- **Team colours (revised after review):** whenever the **enemy** side carries a banner, the bridge has already made its primary
  colour distinct (redmean distance >= 60, the module's `RequestRules.ClashDistance`) from the player side's team colour: the player
  side's banner, or, when the player side has none, the culture banner the module will fly for `side.culture` (`bl_catalog.json`
  `cultures[].color`/`color2`, `new Banner(culture.Banner, culture.Color, culture.Color2)`; unknown culture ids -> `vlandia`, as in
  the module). Colours are swapped per layer, so the arms keep every colour. The module's own swap
  (`BannerFactory.ResolveClash`, which repaints **all** icon layers in one colour) therefore finds nothing to fix on a CK3 banner and
  is only needed for an enemy **culture** banner. Exceptions the module may still meet: a culture the catalog lists without colours
  (e.g. `nord`), or a player-side CK3 banner the module rejects and replaces with the culture banner. Recommended module change
  (owner: Bannerlord module): skip the swap for banners with `source` set, or swap pairwise per layer.

### 7.3 `presentation.attackerCoaPng` / `defenderCoaPng`
Absolute paths of 256x256 RGBA PNGs `<cacheDir>/coa/<content hash>_256s.png` (`<cacheDir>` = the folder given to `Start`; the bridge
host passes `$X/cache`), rendered from the user's CK3 install with CK3's own shield
mask (transparent outside the shield). Files are content-addressed and never rewritten. Absent when the side has no CoA or the
CK3 install was not found.

### 7.4 Bridge-private files
`<cacheDir>/appearance_cache.json` (facts per CK3 internal id: genes, ethnicity, culture gfx, title/house CoAs; written atomically;
cleared when a save of another playthrough is read: different `playthrough_id`s, or, when either id is unknown, a player who is
neither the previous player nor heir; format version 2, older files are discarded) and `<cacheDir>/appearance.log`.

---

## 8. ADDENDUM — bridge core and executable as implemented (2026-10-03, `bridge/CK3xBL.Core`, `bridge/CK3xBL`)

Additions and clarifications; nothing above is removed. Everything here is tolerant of both the §1 and the §5 record forms.

### 8.1 Reading the OUT channel (§1, §5)
- Source: `logs/game.log` (config `ck3.logFile`; `debug.log` also works). Only one file is followed: `info_log` lines appear in both
  files, so following both would duplicate every record.
- The bid of a record is `ck3xbl_bid` from its dump; a number in the marker (§1 form) is used only when the dump has none.
- A dump is attached to the pending marker even when up to 8 unrelated timestamped lines come between them (other engine threads
  write to the same log). A dump without any `ck3xbl_` key is someone else's and is skipped. A record whose dump never comes is
  emitted without one. The last dump of a burst is completed when the file has been idle for one poll (75 ms).
- Records with a dump: `B`, `LV`, `S`, `A`, `R`, `C`, `CHOICE`, `J`, `IN`, `RES`, `END`. Without: `L`, `CX`, `AX`, `RX`; unknown types
  are ignored. `LV` values are stored as `L` facts `fort`, `dev`, `origin` (§5.3). A `J` record collects the `AX`/`R`/`RX`/`C`/`CX`
  lines after it (outside a snapshot) and becomes one `ArmyJoined` event when the next unrelated record arrives or the log idles.
- Sentinels (§5.4) decide when present. Without them (older builds), a character's title is the key printed in its own name, and a
  saved house is trusted only when the printed name contains it.
- Snapshot model additions (CK3xBL.Core.Model): `LocationInfo.Facts` (every `L` field verbatim), `ArmySnapshot.OwnerNomadic`
  (`AX|nomadic`), `CharacterSnapshot.CommanderTraits` (`CX|ctrait`).
- Request ids are `b<bid>_<session>`, `session` = 4 hex digits chosen by the bridge, renewed when `game.log` is recreated (CK3
  restarted) and when a `B|BEGIN` bid is not larger than the previous one (an older save was loaded). Battles of the old timeline
  are dropped (and a preloaded one cancelled) when the first snapshot of the new session arrives.

### 8.2 Delivering results (§2)
- The keypad key sequence is identical to `tools/gen/keypad_payload.mjs` (F3, seek `digits F5` whenever the next non-zero register is
  not the next index, `digits F1`, F2). The run file body is identical to that tool's body (non-zero registers only).
- Keys go by scan code (digits `0x02-0x0B`, F1 `0x3B`, F2 `0x3C`, F3 `0x3D`, F5 `0x3F`, LCtrl `0x1D`, LShift `0x2A`, LAlt `0x38`).
  Default: Ctrl+Shift+Alt held for a chunk of 12 keys (`delivery.holdModifiersPerChunk = false` presses them for every key), 12 ms
  requested between events. CK3 must be the foreground window before every chunk and every key; otherwise the bridge stops and
  releases what it holds. CK3's keyboard layout is restored afterwards if it changed.
- No `IN|OK` within 4 s, or `IN|BAD`: the whole sequence is sent once more (it starts with F3). Dev mode waits for `RES|END` after
  `run ck3xbl_apply.txt` (and fails fast on `IN|BAD`), then closes the console and writes the no-op.
- Mode: `normal` always uses the keypad, even if `delivery.channel = runfile` is configured; `dev` uses the run file. There is no
  automatic fallback between the channels (a timed-out run file may still have been applied).
- `result.json` `aborted` → outcome code 3 (casualties without a winner) whatever the mapper computed.

### 8.3 Talking to Bannerlord (§3) (revised after review, 2026-10-03)
- Before writing a request the bridge deletes a stale `result.json`. Commands are written one per file write, and the bridge waits
  until the module has taken (deleted) `command.txt` before it writes the next command or request, so e.g. a `cancel` is always
  processed before the next battle's request. **A pending command is never overwritten or appended to** (the module reads the file
  whole, then deletes it, so either would lose a command or run one twice); there is no time limit while the process lives, because
  a main thread blocked for long shows as a hung window and is killed after 30 s (its exit ends the wait). Only `quit` replaces a
  pending command, after 10 s. Commands are dropped while Bannerlord is not running.
- `cancel <battleId>` (extension of §3.3): the bridge names the battle it cancels. The current module ignores the extra word
  (`BridgeCommand.Parse` splits on whitespace), so it behaves exactly like `cancel`. **Request to the module:** when an id is given,
  end the session only if it is that battle, and in `DropPendingRequest` consume `request.json` only if it carries that id; a
  `cancel` that arrives late must never take another battle's pending request.
- Adoption: a Bannerlord that is already running is used only when it proves it runs CK3Bridge: a fresh heartbeat whose `pid` is
  that process (since 0.9.0-beta the command line no longer counts). Any other `Bannerlord` process is the player's own game: the bridge never sends
  it commands, never quits, closes or kills it, does not start a second Bannerlord next to it, and tells the player (tray balloon,
  tray status "open without the bridge module", and the reason in the battle notice). It starts its own Bannerlord once that game
  has exited. The safe-mode question is answered only in bridge processes.
- Shared folder hygiene: at bridge start, `request.json`, `command.txt` and their `.tmp` files are deleted (an earlier run's leftovers
  would be taken as new work). When the Bannerlord process exits (crash, close, kill), whatever it had not taken (`request.json`,
  `command.txt`) is deleted. A battle dropped while Bannerlord is not running has its `request.json` withdrawn (deleted if it still
  carries that battle's id).
- Heartbeat: a status is used only when its `pid` is the Bannerlord process the bridge launched or adopted (or another live
  `Bannerlord*` process named by a fresh heartbeat, which is then followed). Lost has two kinds: **exited** (the process is gone;
  its preload is gone too and is written again when the next process reports its first `idle`) and **unresponsive** (no
  heartbeat 180 s after launch, or `t` older than 5 s, 60 s while `booting`/`loading`; the process may still hold its battle, so
  the preload is kept, and the status is re-read when the heartbeat recovers). Either kind ends a battle that is on screen. Kill
  only when the window has been hung (IsHungAppWindow) for more than 30 s. After an exit the bridge relaunches Bannerlord after 3 s
  (`bannerlord.keepRunning`), with the fallback module list if the process died before its first heartbeat or reported `error`
  without a battle id while booting.
- Reconciliation: a status `loading`/`held`/`visible`/`fighting`/`result` naming a battle id the bridge does not own (and has
  not cancelled already) is answered with one `cancel <battleId>`. A request is never written again while the current process
  reports that battle in one of those states.
- Entering: if the battle is still `held` after the bridge sent `fg` and `go` (e.g. a load step delayed `fg`), `fg` and `go` are
  sent again (at most twice; both are idempotent in the module).
- Status → bridge phase: `visible`/`fighting` reveal the battle (overlay away); `result` = scoreboard; `fading_out`, `black`,
  `ended`, `aborted` — and `idle` while a battle was running (missed transitions) — start the return: the final `result.json`
  (the final `finished`/`aborted` one, waited for up to 3 s, else the provisional `result`) is turned into the outcome.
  `error` before or during a battle drops it.
- `result.json` is watched too (write time): a final result (`finished`/`aborted`) of the running battle starts the return even
  when no status transition was seen; `cancelled`/`error` while entering or fighting ends it with a notice.
- First `idle` of each process (by `pid`: launch, relaunch, restart from the tray): the bridge sends `bg`, and if the booting game
  took the foreground from CK3 it hands it back. `bg` is also sent whenever the player is taken back to CK3 without a normal return
  (abort hotkey, failure, CK3 restarted or an older save loaded while a battle was on screen).
- The return sends `handoff_to <ck3 hwnd>` (decimal) and waits 1.5 s for CK3 to be in front (at most 1.5 s more for the module to
  take the command); only if the foreground is still one of the games, the overlay or nobody does the bridge use the activation
  ladder. If the player is in another app, delivery waits.
- Delivery (§8.2) while the player holds a key or mouse button, or keeps using keyboard and mouse: nothing is typed and it is tried
  again shortly (not counted as an attempt; the result card stays up). After 60 s it types as soon as no modifier or mouse button is
  held, and gives up (notice) only if one still is.

---

## 9. ADDENDUM — Bannerlord module stage 2: presentation (CK3Bridge v0.3.0, 2026-10-03)

Clarifies what the module's statuses mean on screen and how it uses `presentation`; nothing above is removed. Requests without the
optional fields keep working.

1. **Reveal rule.** A loaded battle is shown only when (a) `go` was received or `presentation.preload` is false, (b) the window is
   not cloaked (`fg`), and (c) a window of the Bannerlord process is the foreground window. Until then the loading card (black,
   with the title card) stays up, the mission is paused and the status is `held` (for `preload: false` too). The module never
   reveals on a timer and never releases a hold by itself: the bridge's enter timeout and `cancel` are the only ways out. While
   held, the engine's 720 s loading-stuck watchdog is stopped (loading is complete).
2. **`visible`** = the loading card has been swapped, in the same frame, for the identical in-engine title card; the battlefield
   emerges behind it over 1.2 s, the title fades after 2.5 s (1 s fade). **`fighting`** = the title card is gone **and**
   deployment is finished (Order of Battle "Ready", `presentation.deployment: "auto"`, or fewer than 20 troops).
3. **Result.** A footer shows "<title> is won|lost|over · Returning to your realm in N · [Enter] now · [Space] stay on the
   field". N counts `returnCountdownSeconds` in real time and stops while the game is paused (escape menu, options).
   `0` = return at once, `-1` = wait for the player. Enter, the scoreboard's Done button and `return_now` start the return:
   `fading_out` (native 0.6 s fade to black) → `black` 0.75 s later (EndMission) → `ended` (2 s) → `idle`. Space = stay on the
   field (status stays `result`; there is no `lingering` status). Bannerlord stays black until the next battle is `visible`.
4. **`abort` / `cancel` while the battle is on screen** (`visible`, `fighting`, `result`) also go `fading_out` → `black` → final
   `aborted` / `cancelled`; during the 0.75 s fade `result.json` still holds `started` (or the provisional `result`). While
   `loading` / `held` they end the mission at once, without a fade.
5. **Focus.** The focus-lost escape menu opens only in `fighting`. With the CK3 views active the user's "Stop Game On Focus Lost"
   option is never touched (only the stock-view fallback, mission "CustomBattle", still suppresses it in memory as in stage 1).
6. **`presentation` fields used:** `title`, `subtitle`, `detail` (title card, loading card), `objective` (native mission
   objective, with an enemy casualties x/y bar; default "Defeat <enemy side name>"), `returnCountdownSeconds`, `deployment`,
   `attackerCoaPng` / `defenderCoaPng` (§7.3; the player's coat left, the enemy's right; a file larger than 4 MB, not a PNG, larger
   than 2048 px or not loadable by the engine is skipped with a log line). Missing `title` → "The Battle"; missing `subtitle` →
   "<player side name> against <enemy side name>".
7. **Text rules** (never fail a battle): `{...}` markup and control characters are removed, whitespace is collapsed, the title is
   cut at 96 and other lines at 160 characters (with "…"). Card text and the side and hero names are folded to the glyphs of
   Bannerlord's Galahad font: letters the font lacks lose their diacritics (ǫ → o, ș → s), a few get an ASCII form (ŋ → n,
   ʿ → '), other unsupported characters are dropped.
8. **Scoreboard:** the side headers show `attacker.name` / `defender.name` (cleaned as above) instead of "Attackers" /
   "Defenders"; the party rows already did.

---

## 10. ADDENDUM — CK3 mod after review (2026-10-03, `ck3_mod/ck3xbl`)

Amends §1.2, §2.2, §2.3, §5.7, §5.8 and §8.2 as marked; nothing else above changes. Parsers stay tolerant as in §5.

### 10.1 Which battles are reported (amends §1.2 "player-involved")
A combat gets a snapshot (and the toast and event 0001) only when a human player is a participant, commander or knight of one
side, **or a participant of the war the combat belongs to** (`combat_war`). Battles of the player's vassals or realm in wars the
player is not part of are left to CK3 and produce no records (every snapshot is now followed by event 0001; the bridge may keep
preloading on `B|END`). Joins (§5.6) use the same rule.
The player's side in a snapshot (where `C|PLAYER` goes, `S…ck3xbl_s_player_in`, the events' texts), first match wins: the side of
the army the player commands or rides with as a knight; the side where the player is a participant, commander or knight; the side
**opposite** a side that has a participant at war with the player (the player's war, or rebels in a faction war); the side with a
loyal member of the player's realm (characters at war with their top liege are not counted); the defender.

### 10.2 Pending battle lifecycle (amends §5.7)
- `CHOICE|FIELD` also stores the player (`ck3xbl_pending_player`) and starts a 7-day grace timer (`ck3xbl_pending_fresh`).
- A pending battle is **stale** when its player is dead or no longer played, or when the grace timer has run out and none of its
  tagged armies is in combat any more. A stale pending battle no longer blocks the field options of another battle.
- When a new `CHOICE|FIELD` replaces a stale pending battle, the mod first logs **`CHOICE|ABANDON` with the stale battle's bid**
  (`ck3xbl_bid` in its dump; the same tick, immediately before the new `CHOICE|FIELD`) and releases that battle's tags. The bridge
  treats it exactly like a player's ABANDON (`cancel`/`abort` if that battle is still staged or running).
- Event 0001 options a/c re-check the field when clicked; if another battle took it meanwhile, 0001 opens again (no record).
- Event 0002: "The dust settles..." needs this battle's payload committed (`ck3xbl_ready = 1` and this battle pending) or applied
  (`ck3xbl_ready = 2` and player variable `ck3xbl_applied_bid` = this bid); "Await word from the field." only while this battle is
  pending and nothing is committed; "Let the battle run its course without me." unless applied. ABANDON from a window whose
  battle is no longer pending only logs the record (registers and `ck3xbl_ready` belong to the pending battle and are not touched).
- An army tagged for the pending battle keeps its `ck3xbl_aid` / battle if it joins another combat before the result is applied
  (only possible when CK3 runs during the Bannerlord battle); its `A`/`J` records in that other snapshot carry the same `a_tag`.

### 10.3 Payload version 3: position-weighted checksum (amends §2.2, §5.8)
- `r0 = 3` and `r199 = Σ_{i=0..198} ((r_i mod 9973) × ((i mod 16) + 1)) mod 9973`, reduced after every term (every intermediate
  stays below 170,000). A repeated store key (F1) inside a run of consecutive registers shifts the rest of the run by one slot;
  the plain sum of version 2 cannot see that, the weights do. The mod still accepts **version 2** (`r0 = 2`, plain sum, §5.8) so
  existing bridges keep working; **the bridge should switch to version 3** (`KeypadLayout.PayloadVersion = 3` and
  `ComputeChecksum`). Reference: `tools/gen/lib/registers.mjs` `payloadChecksum`, `tools/gen/keypad_payload.mjs`
  (`node tools/gen/keypad_payload.mjs 1=3 2=2 3=8199 4=300 5=900 6=7 10=12 11=500 12=48 13=400 130=5 131=4 132=2` → r199 = 2292).
- A digit key that would make the accumulator exceed 999,999 (a lost F1 merged two values) sets player variable
  `ck3xbl_overflow = 1` instead of leaving CK3's fixed-point range; the commit then logs `IN|BAD`. F3 clears it.
- The key sequence itself is unchanged (§8.2).

### 10.4 Run file starts with a reset (amends §2.3, §8.2)
The run-file body is `hidden_effect = {`, **`ck3xbl_keypad_reset_effect = yes`**, the `set_variable` lines of the non-zero
registers, `ck3xbl_commit_effect = yes`, `ck3xbl_apply_effect = yes`, `}`. Registers left by an earlier partial attempt never
count. The reset leaves `ck3xbl_ready = 2` alone, so re-running an applied file never applies twice (its commit fails: no battle
is pending). The bridge's `RunFileRenderer` must add the line.

### 10.5 CK3's own post-battle rolls stay (decision; see "open" below)
After `force_win`, vanilla `on_combat_end_winner` runs as for any battle. Its `combat_event.1001` may, in a war, capture the
losing side's leading commander (about 10 %) and capture (about 10 %) or kill (about 5 %) each losing knight, on top of the
Bannerlord fates; these show as vanilla lines in the combat summary. Event 0003 says "more of the vanquished may yet be taken or
cut down" for decided battles. The mod does not suppress the rolls: that needs a full override of vanilla
`events/war_events/combat_events.txt` or `common/on_action/combat_on_actions.txt`, i.e. shipping copies of game files (against
the project rules, and they would break with every patch). Consumers must not assume a character the result left unharmed is
still free or alive afterwards. **Open question:** whether these extra rolls stay; the alternative is the override above.

### 10.6 Characters in concurrent battles (amends §2.1, §5.7)
`ck3xbl_cbid` / `ck3xbl_cside` only mark the latest snapshot that listed a character (de-duplication). At `CHOICE|FIELD` the
battle's characters are **pinned**: character variable `ck3xbl_fside` = their side in the pending battle (everyone that snapshot
listed last, the combat's commanders and knights, both war leaders, the player). Fates use `ck3xbl_fside` for the side of their
`battle_event` line. A character leaves `ck3xbl_chars` only when it is not pinned and the battle that listed it last has no tagged
army left; pins are released at `RES|END`, `CHOICE|ABANDON` of the pending battle and when a stale battle is dropped. A commander
joining the pending battle (`J`) is pinned at once.

### 10.7 Event texts
Event 0001 shows the troop numbers from its own chain (`scope:ck3xbl_my_men` / `ck3xbl_enemy_men`); the player variables
`ck3xbl_my_men` / `ck3xbl_enemy_men` are no longer set.

---

## 11. ADDENDUM — Bannerlord module after review (CK3Bridge v0.3.1, 2026-10-03)

Amends §3.3, §3.5, §9 and the module's stage-1 additions. Requests and commands without the new parts behave as before.

1. **`cancel [<battleId>]` / `abort [<battleId>]`** (amends §3.3 and stage-1 addition 6; implements the request in §8.3). A
   command that names a battle acts only on that battle (ids compared exactly, case-sensitive):
   - a running battle is stopped only if it is that battle;
   - with no battle running, `request.json` is consumed only if its top-level `id` is that battle, and answered with a
     `cancelled` / `aborted` result as before;
   - while another battle runs, a pending `request.json` of the named battle is withdrawn **without** an answer
     (`result.json` and the status belong to the running battle); anything else is ignored with a log line.
   A bare `cancel` / `abort` keeps its old meaning (the running battle, else whatever request is pending).
2. **`black`** (amends §9 items 3-4): means the mission has ended **and** the final `result.json` (`finished` / `aborted` /
   `cancelled`) is written. On a normal return that is one frame after `EndMission`, i.e. about 0.75 s after `fading_out`;
   the status stays `fading_out` until then.
3. **Stops and returns while the game is paused** (amends §9 item 4). `cancel`, `abort`, `return_now`, the countdown, Enter
   and Done never wait for the player to close the escape menu: the module closes the escape menu and open inquiries itself
   (as the player would), then fades and ends the mission. While anything else holds the game state (not expected in a
   battle) the stop waits, with a log line, and the status keeps its current phase.
4. **`presentation.preload`** (amends §3.2): only an explicit `true` holds the loaded battle until `go`. A request without
   `presentation`, or whose `presentation` has no `preload`, is shown as soon as it is loaded (the other conditions of §9
   item 1 still apply), as protocol 1 requests were. The bridge always sends `preload: true`, so nothing changes for it.
5. **Groups that share one troop id** (amends §3.5 `groups`): each spawned agent of a shared (side, troop) goes to the group
   that is least filled relative to its requested count (ties: request order), so `spawned` and the casualties are spread over
   those groups in proportion instead of the first group taking every agent that fits the battle size.
6. **Taking inbound files**: the module renames `request.json` / `command.txt` to `<name>.taken` before reading it (atomic),
   so a file the bridge writes right after a take is never deleted unread. `.taken` files are module-private.
7. **Status file readers** should open `bannerlord_status.json` with `FileShare.ReadWrite | FileShare.Delete`: a reader without
   `Delete` blocks the module's replace. The module then retries every 0.25 s (no waiting on its main thread, logged once), and
   `t` stays stale for as long as the file is held.

---

## 12. ADDENDUM — integration (2026-10-03; end-to-end test `bridge/CK3xBL.SelfTest/IntegrationTests.cs`)

Closes the open cross-component items of §7.1, §10.2–§10.4; nothing above is removed.

### 12.1 The bridge sends payload version 3 (implements §10.3)
`KeypadLayout.PayloadVersion = 3`; `KeypadPayload.ComputeChecksum(registers, version)` computes r199 exactly like the mod's
`ck3xbl_commit_effect` (version 3 weighted by `(i mod 16) + 1`, reduced after every term; version 2 = plain sum, kept for tests). The
key sequence is unchanged (§8.2). On `IN|BAD` the bridge journal compares the mod's `ck3xbl_in_sum` with the payload's r199 (both are
the reduced checksum) and `ck3xbl_in_count` with the payload's non-zero registers among r0..r198.

### 12.2 The run file starts with the keypad reset (implements §10.4)
`RunFileRenderer.Render` writes `hidden_effect = {`, `ck3xbl_keypad_reset_effect = yes`, the non-zero `set_variable` lines,
`ck3xbl_commit_effect = yes`, `ck3xbl_apply_effect = yes`, `}` — byte-identical to `tools/gen/keypad_payload.mjs`.

### 12.3 `CX|trait|beardless_eunuch` (implements the request of §7.1)
The generator's `APPEARANCE_TRAITS` (and so the §1.2 `CX|trait` list) ends with `beardless_eunuch`; the bridge already honours it.

### 12.4 Stale-battle ABANDON in the bridge (§10.2)
No bridge change was needed: `CHOICE|ABANDON` of the active battle cancels it in Bannerlord (`cancel <id>`), of a queued battle removes
it, of a battle the bridge no longer knows is ignored; the `CHOICE|FIELD` that follows in the same tick stages the new battle (test
`core.orchestrator.stale_battle_abandoned_before_field`).

### 12.5 Observed engine formats (a real `logs/game.log`, release mode, 2026-10-03 18:05–18:07)
- Marker: `[hh:mm:ss][I][jomini_effect_impl.cpp:450]: file: <file of the info_log statement> line: <n> (<context>): CK3XBL|...`.
  Contexts: on_action name (`(ck3xbl_test_after_lobby)`), `(<event id>:immediate)`, `(<event id>:option)`,
  `(<scripted gui>:effect)`.
- `info_log_scopes`: `[..][I][effectimpl.cpp:642]: <current scope>`, `Root: ...`, blank, `Saved event targets:`, engine-saved
  foreign targets first (`governor:`, `steps: 0.10`, `throne: yes`, `Flag bone (6463)`), then our keys; `\x15` wraps `weak`. A dump
  may continue with a blank line, `Saved list targets:`, `<list>: ` headers and **key-less** member lines (one observed dump had
  about 3,400 of them). Readers take only `ck3xbl_*: value` lines (the bridge does). Lowborn prints `Name Lowborn of  (Internal ID 23538)`.
- Whether dumps written from `on_combat_start` carry such large foreign sections is not known yet (game.log size per battle is
  measured in game).

### 12.6 The end-to-end contract test (no game needed)
`dotnet run --project bridge/CK3xBL.SelfTest -- integration` runs: a game.log fixture generated from the mod's own `info_log`
statements (`bridge/CK3xBL.SelfTest/fixtures/make_ck3_battle_game_log.mjs`; regenerate after changing the mod's records) → parser
→ snapshot → `BattleMapper` → `AppearanceProvider.Enrich` (newest real save, read-only) → `request.json` as `BannerlordLink` writes it
→ read back with **Bannerlord's own Newtonsoft.Json** and the module's exact settings and read path (loaded from the install at run
time) plus a strict reader of the module's contract → `result.json` written with the module's settings → `BuildOutcome` →
keypad payload / run file → replayed through the mod's generated keypad, commit and apply scripts (parsed from
`ck3_mod/ck3xbl`). It also checks that every `ck3xbl_*` key the bridge's decoder reads is saved by the mod, that every keypad key
the bridge presses is a vanilla `_ctrl_shift_alt_*` shortcut on a 0002 button whose scripted GUI runs the effect of the same
meaning, and that the register map, MaA index chain, summary registers and checksum of the mod equal the bridge's.

---

## 13. ADDENDUM — knights & commanders + polish (request protocol stays 2, keypad payload stays 3: additive fields and award slots)

Everything here is **additive**: no field, record or
register above changes meaning. `ProtocolInfo.Version` stays **2** (the module answers any other `protocol` with `error`, stage-1
addition 3, so a bump would break every installed module for nothing) and the keypad payload stays **version 3** (`r0 = 3`): the new
award slots live in registers that were free and are already covered by the v3 checksum. Readers stay tolerant as in §5: unknown
records, `L`/`CX`/`AX`/`RX` fields, dump keys and JSON members are ignored; every new request member has a neutral default.
**Amended by §14 (battle clock):** keypad payload version 4 keeps every register, award slot, encoder rule and the checksum of
this addendum, and adds r8, r9, r198 and absolute levy survivors; payload 3 stays valid and unchanged. The final register map of
both versions is §14.2.

### 13.1 Compatibility matrix
| Writer → reader | Result |
|---|---|
| new mod → old bridge | New `CX`/`AX` fields fall into `BattleSnapshotBuilder.ApplyCulture`, which has no case for them; new dump keys are stored and never read; `RES|AW` becomes an ignored `ResultTrace`. Unchanged behaviour. |
| old mod → new bridge | No `S` dump has `ck3xbl_s_has_cmd`, so `BattleSnapshot.KnightFactsLogged = false` and the mapper skips the whole knights pass: today's request member for member (no buffs, no `captainOf`/`deathChance`, `damageMultiplier` 1, today's Tactics and fates), except `presentation.inEngineTitle = false` and the §13.7 wording ("Rout <enemy host>" without `AX|besieging`). Facts today's mod already logs (martial, `CX|ctrait`, `CX|trait`, terrain, winter) are **not** turned into buffs on their own. |
| new bridge → old mod (keypad) | Award registers are stored, counted in `ck3xbl_in_count` and in the checksum, and ignored by the old apply effect. Character slots with fate 0 are no longer written (13.5); every mod version ignores them anyway. With the battle clock (§14) the first payload is version 4; a mod from before §14 refuses it, and the bridge sends the same outcome once more as version 3 (§14.3). |
| new bridge → old module | Newtonsoft `MissingMemberHandling.Ignore` drops the new members: today's battle (double title card as before). |
| old bridge / hand-dropped request → new module | Defaults: `inEngineTitle = true`, no buffs, stock captains: today's behaviour (13.4 item 6). |

### 13.2 OUT additions (CK3 → bridge)
All lines are static `info_log` text (§5.1); numbers only in dumps. Emitted in the same effect run as the snapshot (§5.2), and for
`J` records too (§5.6).

**`S` record dump (side scope), saved before `info_log = "CK3XBL|S|<side>"`:**
| Key | Meaning |
|---|---|
| `ck3xbl_s_has_cmd` | sentinel 0/1 (§5.4) for `ck3xbl_s_cmd` |
| `ck3xbl_s_cmd` | scope: `side_commander` of the side (the character whose commander traits are the side's; he is also a `C|CMD` record) |
| `ck3xbl_s_hostility_out` | 0..3: `faith_hostility_level` of **this** side commander's faith towards the enemy side commander's faith (0 when either is missing). Faith hostility is asymmetric; the other direction is the enemy side's `ck3xbl_s_hostility_out`. Which direction CK3's combat advantage uses is still open (to be confirmed in game). Its presence (with `ck3xbl_s_has_cmd`) marks a mod with this addendum. |

**`A` (and `J`) record dump, saved before the marker:**
| Key | Meaning |
|---|---|
| `ck3xbl_a_ke` | knight effectiveness of the army **owner** in **permille** (1000 = CK3's base 100 %), computed in CK3 by the generated effect `ck3xbl_save_knight_eff_effect` (every exact/inferred source of `bridge/data/ck3_knight_effectiveness_sources.json`, per-skill terms and equipped-artifact modifiers included). Logged raw (it may be ≤ 0); absent = not logged = 1000; the bridge clamps (0.5..3.0). The mod clears it (`clear_saved_scope`) right after the record's dump and keeps the effect's helper scopes temporary, so neither appears in later dumps. |

**`AX` lines after an `A`/`J` record (army scope unless noted):**
| Record | Meaning |
|---|---|
| `AX|besieging|yes|no` | `is_army_in_siege` of this army (a siege interrupted by this combat counts). Decides each side's siege role (13.7). |
| `AX|territory|own|hostile|other` | the battle barony seen from the army owner: `own` = its `barony_controller` is the owner or below him (`target_is_same_character_or_above`); `hostile` = the controller is at war with the owner or the owner's `top_liege`; `other` = anything else (allied, neutral, no controller). The bridge applies "Hostile Territory Attacker" effects (accolade Marauder, trait the_wake) only for `hostile` and the combat attacker. |
| `AX|otrait|<trait>` | (owner scope) one line per trait of the army **owner** among the `ck3_combat_traits.json` traits with role `armyOwner` (25 keys) |
| `AX|ocparam|<p>` / `AX|ofparam|<p>` | (owner scope) the owner's culture parameters (`emitPlan.cultureParameters`, 11) / faith parameters (`emitPlan.faithParameters`, 4) that are true |

**`CX` lines after every `C` record (character scope), after today's `CX` lines, in this order:**
| Record | Meaning |
|---|---|
| `CX|ktrait|<trait>` | one per held trait of `ck3_combat_traits.json` `emitPlan.traitsNotEmittedToday` (68 keys). `CX|trait` stays appearance-only and `CX|ctrait` stays the commander traits; the bridge's combat evaluator reads the union of the three lists. |
| `CX|txp|<trait>[:<track>]|<1..4>` | level of a held trait of `emitPlan.traitsWithLevels` (27 keys; chain highest first). The maximum is the length of the trait's chain in `ck3_combat_traits.json` (4 for clouded_eyes, faltering_heart, fragile_bones, infirm, the_wake; 2 or 3 for the others). No line = level 0; 0 is never emitted. |
| `CX|cparam|<p>` / `CX|fparam|<p>` | the character's culture / faith parameters that are true (same 11 / 4 keys as above) |
| `CX|acc_active|yes|no` | only for acclaimed knights (`is_acclaimed = yes`): the accolade is one of its owner's active accolades |
| `CX|acc_rank|<0..6>` | accolade rank (glory itself is not readable in script; the rank bounds it) |
| `CX|acc_primary|<accolade_type>` | the accolade's initial (primary) attribute |
| `CX|acc_type|<accolade_type>|<0..3>` | one per attribute held, with its level |
| `CX|artifact|<slot_type>|<type>|<rarity>` | one per equipped artifact of slot type `primary_armament`, `armor` or `helmet`; `<type>` is `unknown` for a type the table does not know; rarity `common|masterwork|famed|illustrious` |
| `CX|artifact_helmet|<feature>` | follows an `armor` artifact line: its `armor_helmet_*` feature |

**Lifecycle:** `RES|AW` (dump: `ck3xbl_bid`, `ck3xbl_aw_tag`, `ck3xbl_aw_value`): one per award slot applied (13.5), after the army
slots (`RES|A`) and before the fates. Payload 4 (§14) applies no army slot at the click, so there `RES|AW` follows
`RES|LIVE`/`RES|GONE` (§14.4). Informational (bridge journal).

Not emitted (decisions): `AX|ke_art` (artifact knight effectiveness is inside `ck3xbl_a_ke`), per-source
knight-effectiveness lines, glory, `CX|sigweapon`.

### 13.3 Snapshot model (bridge, `CK3xBL.Core.Model`)
`BattleSnapshot.KnightFactsLogged` (`bool`: an `S` dump had `ck3xbl_s_has_cmd`); `SideSnapshot.Commander` (`CharacterRef?`),
`.FaithHostilityOut` (`int?`); `ArmySnapshot.KnightEffectivenessPermille` (`int?`, null = not logged), `.Besieging` (`bool?`),
`.Territory` (`string?`: own | hostile | other; null = not logged), `.OwnerTraits`, `.OwnerCultureParams`, `.OwnerFaithParams`;
`CharacterSnapshot.CombatTraits`, `.TraitLevels` (`"<trait>[:<track>]"` → level), `.CultureParams`, `.FaithParams`, `.Accolade`
(`Active`, `Rank`, `Primary`, `Levels`), `.Artifacts` (`SlotType`, `Type`, `Rarity`, `HelmetFeature`).

### 13.4 Request additions (`shared/Protocol.cs`, request protocol 2)
```jsonc
"presentation": { ..., "inEngineTitle": false },            // default true
"attacker": { ...,
  "buffs": { "damageDealtMult": 1.2, "damageTakenMult": 1.0, "initialMoraleAdd": 8, "chargeDamageMult": 1.1, "troopDeathChanceMult": 1.0 },  // default null
  "groupBuffs": {                                            // default {}; keys = TroopRequest.group, verbatim
    "knight-retinue#5": { "damageDealtMult": 1.25, "damageTakenMult": 1.0, "healthMult": 1.25, "initialMoraleAdd": 0 },
    "maa:bowmen#12#16706": { "damageDealtMult": 1.2, "damageTakenMult": 1.0, "healthMult": 1.1, "initialMoraleAdd": 6 } },
  "heroes": [ { ..., "healthMultiplier": 2.4, "damageMultiplier": 1.25, "captainOf": "Ranged", "deathChance": 0.6,
                "buffs": { "damageDealtMeleeMult": 1.1, "damageDealtRangedMult": 1.0, "damageDealtVsHeroMult": 1.0, "damageTakenMult": 1.05,
                           "chargeDamageMult": 1.0, "initialMoraleAdd": 25, "noPanic": true, "startHealthFraction": 1.0,
                           "speedMult": 1.0, "swingSpeedMult": 1.0, "rangedInaccuracyMult": 1.0, "knockdownResistanceAdd": 0.0 } } ] }
```
1. **`presentation.inEngineTitle`**: 13.6.
2. **`heroes[].healthMultiplier`** keeps its meaning (whole pool at spawn); the bridge now folds knight effectiveness into it.
   **`damageMultiplier`**: every hit the hero deals (melee, missile, his mount's charge). **`captainOf`**: FormationClass name
   (`Infantry|Ranged|Cavalry|HorseArcher`, case-insensitive; anything else is ignored with a log line); the module makes the hero
   captain of that formation of his team when it has units, otherwise stock assignment. Stock Bannerlord assigns captains only for
   teams that do not deploy with Order of Battle, and the player's side deploys with OoB whenever it has ≥ 20 troops
   (`CustomBattleInitializationModel`), so in practice `captainOf` acts on the AI side; the bridge sends it only for heroes of the
   side opposite `playerSide` (the player's OoB choices are never overridden). **`deathChance`**: the
   hero's killed-vs-unconscious chance (stage-1 addition 2 read it raw under the same key; `null` = module default).
   **`buffs`** (`HeroBuffs`): looked up by the hero's character id (`ck3xbl_<id>_<key>`).
3. **`<side>.buffs`** (`SideBuffs`): every agent of the side. **`<side>.groupBuffs`** (`GroupBuffs`): every agent the module assigns
   to that group (the ledger's assignment, §3.5). A key the request's troops do not contain is ignored with a log line; a `null`
   dictionary is treated as empty. `GroupBuffs.damageTakenMult` is honoured by the module but reserved: the bridge leaves it 1.
   For every mod (knights facts or not), each MaA group's `damageDealtMult` and `healthMult` also carry its CK3 damage and
   toughness (`ForcePlanner.MaaStatFactors`): the tier overflow, which no longer adds agents, and the damage/toughness shape
   against the base type's `ref_maa`, half of each excess, 0.6..1.6, multiplied into the other group buffs and capped 0.5..2.
   The bridge never writes NaN/∞ (every float passes a finite-or-neutral clamp) and caps a hero's combined durability
   (`healthMultiplier / (hero damageTakenMult × side damageTakenMult) ≤ 4`) and dealt product (≤ 2 including the side factors).
4. **Per-hit damage** (module, `AgentApplyDamageModel.ApplyGeneralDamageModifiers`, every hit except falls and friendly fire; a
   charge is credited to the rider; damage dealt to mounts is not changed):
   `mult = attackerSide.damageDealtMult × (charge ? attackerSide.chargeDamageMult : 1) × attackerHero.damageMultiplier ×
   (missile ? attackerHeroBuffs.damageDealtRangedMult : attackerHeroBuffs.damageDealtMeleeMult) × (melee vs hero ?
   attackerHeroBuffs.damageDealtVsHeroMult : 1) × (charge ? attackerHeroBuffs.chargeDamageMult : 1) × attackerGroup.damageDealtMult ×
   victimSide.damageTakenMult × victimHeroBuffs.damageTakenMult × victimGroup.damageTakenMult`, clamped to **0.25..3.0**.
   `charge` = `collisionData.IsHorseCharge`, or a melee hit by an agent that has a mount.
5. **Health and morale:** `groupBuffs.healthMult` multiplies `BaseHealthLimit` and `HealthLimit` and sets `Health = HealthLimit` when
   the agent is built; `buffs.startHealthFraction` then sets the hero's `Health = max(1, HealthLimit × fraction)` (after his
   `healthMultiplier`). Initial morale: `+ side.initialMoraleAdd + hero buffs.initialMoraleAdd` inside the morale model;
   `+ group initialMoraleAdd` with `ChangeMorale` when the agent is built (Bannerlord clamps 15..100 at initialisation only).
   `noPanic` heroes never panic. Troop killed-vs-unconscious chance × `side.troopDeathChanceMult`, clamped 0..1.
6. **Absent fields = today:** the module's three models subclass the CustomBattle models and return the base value unchanged when no
   buff applies, and its captain logic subclass defers to the stock logic when no hero has `captainOf`.
7. **Module safety clamps** (the bridge already clamps tighter): every `*Mult` 0.25..4,
   `initialMoraleAdd` −50..50, `startHealthFraction` 0.05..1, `knockdownResistanceAdd` −0.5..0.5, `damageMultiplier` 0.25..3;
   NaN/∞ → neutral. `healthMultiplier` keeps its 0.25..4 clamp.

### 13.5 IN additions: award slots (keypad payload versions 3 and 4; amends §2.1, §2.2, §5.8)
**Register map** (additions in bold; everything else as §2.2; both payload versions in one table: §14.2):

| Regs | Meaning |
|---|---|
| r8, r9 | free (stay 0) in payload 3; kill targets in payload 4 (§14.2) |
| **r122 + 2k, r123 + 2k, k = 0..3** | **award slot k: character tag, award value** |
| **r190 + 2(k−4), r191 + 2(k−4), k = 4..7** | **award slot k: character tag, award value** |
| r198 | free (stays 0) in payload 3; steering hint in payload 4 (§14.2) |

**Award value** `A = R + 10·G + 1000·P`: `R` = prowess rolls 0..3, `G` = glory / 5 (0..20 = 0..100 glory), `P` = prestige / 25
(0..40 = 0..1000 prestige). `A ≤ 40,203 < 10^6`. A slot is used only with tag > 0 and A > 0; slots fill in order k = 0, 1, ...

**Apply** (`ck3xbl_apply_effect`, outcomes 1, 2 and 3 only; after the army slots and before the character fates; payload 4:
after `RES|LIVE`/`RES|GONE` and before the fates, §14.4): per used slot
decode `P = floor(A / 1000)`, `G = floor((A mod 1000) / 10)`, `R = A mod 10`; for the alive character with that tag in
`ck3xbl_chars`: `add_prestige = 25·P` (if P > 0); `accolade = { add_glory = 5·G }` if `is_acclaimed = yes` (if G > 0); the vanilla
scripted effect `knight_increase_prowess_chance_effect` R times; then `RES|AW`. Battle-outcome glory (`combat_event.2101/2111`)
and the player's `r7` prestige stay as they are; awards never repeat them.

**Encoder** (bridge `KeypadPayload`; reference `tools/gen/keypad_payload.mjs`):
- Character slots are written only for characters with a fate > 0 (amends §2.2: a fate-0 slot was always a no-op in the apply
  effect, which requires `fate > 0`; the killer is found by tag in `ck3xbl_chars`, not through a slot).
- Award slots fill k = 0, 1, ... in the order of `Ck3Outcome.Awards` (priority), without gaps.
- **No total key budget**: the payload is as long as the battle needs (measured 150..650 keys for 2..8 armies and 6..20 characters,
  ~34 ms per key). Only the award block is capped, and only in the **keypad** channel: award slots are dropped from the last one
  while the keys they add exceed `delivery.keypadAwardKeyBudget` (bridge config, default 90 ≈ 3 s, enough for 8 awards with 3-digit
  tags; 0 = no awards over the keypad). Armies and characters are never dropped for awards. The run file (Dev mode) always carries
  every award.
- Key cost: ≈ 10 keys per award (3-digit tag, 5-digit value, two stores) plus a 4-key seek before each block unless the register
  before it is used.
- Reference vectors: **encoder** (two awards → slots 0 and 1) `1=3 2=2 3=8199 4=300 5=900 6=7 10=12 11=500 12=48 13=400 122=7
  123=13062 124=9 125=40203 130=5 131=4 132=2` → `r199 = 4016`, 79 keys; **registers** (both blocks, fed directly to the mod's
  interpreter) `... 122=7 123=13062 130=5 131=4 132=2 190=9 191=40203` (same header) → `r199 = 4656`, 83 keys.
The key sequence and run-file rules (§8.2, §10.4) are unchanged (non-zero registers only).

### 13.6 Single dissolving title card (amends §9 items 1-2)
- `inEngineTitle = false`: the module's loading card and its in-engine card show **only the black backdrop** (no title, subtitle,
  detail or coats of arms). At the reveal (`visible`, unchanged: the frame the loading card is swapped) the backdrop fades 1 → 0 over
  **1.2 s**; there is no title hold or title fade, and `fighting` follows once the backdrop is gone and deployment is finished. The
  result footer (§9 item 3) is unchanged.
- Bridge: on `visible` it keeps its own title card fully up for **400 ms**, then dissolves it over **900 ms** (instead of the 500 ms
  hide), so one card dissolves into the live battle.
- Bridge: it also starts bringing Bannerlord's sound back at `visible`, together with the dissolve (not after it).
- **Fallback** `title_in_engine` (new module command, §3.3; sent by the bridge before `fg` when `inEngineTitle = false` but its own
  title card could not be shown, e.g. the player was in another application): the module treats the held battle as
  `inEngineTitle = true`. Modules that do not know the command log "unknown command ignored" and keep the black backdrop.
- `inEngineTitle = true` (hand-dropped requests and the fallback): as §9, but the coats of arms sit closer to the title without
  covering it or the Order-of-Battle formation lists (which span 15..315 units from each screen edge on the 1920×1080 reference):
  text column 820 units, coat margins 30, centres at ±535 units (≈ ±713 px at 1440p), outer edges at ±630; long titles wrap.

### 13.7 Presentation wording (amends §6.2 "relief battle" wording)
- **Siege role** of a side at a besieged holding (`L|siege = yes`): **besieger** if any of its armies logged `AX|besieging|yes`;
  **relief** if the other side is the besieger. `objective`: player relief → "Break the siege of <province>"; player besieger →
  "Hold the siege lines before <province>"; otherwise, and whenever no `AX|besieging` was logged → "Rout <enemy host>".
- `detail` of a relief battle names the ground actually used: "Fields before <province>" only when the scene comes from an open
  (farmlands/plains/steppe/drylands) pool, else "<terrain name> before <province>" ("Woods before South Seaxe").
- A relief battle at a castle or town-kind holding in forest, taiga or jungle is fought on the farmlands pool of its climate
  (cleared land around the holding) and takes no chance-based coast or river lead; water at the holding itself (coast or lake
  within coastal_near_px) and crossings still lead, and such a scene names its lead ("Coast before X", "Riverbank before X",
  "Lakeshore before X", "River crossing before X").
- The bridge's result card and its "dispatch has arrived" notice name the CK3 option exactly as the mod's localization
  (`ck3xbl_battle.0002.a` = `The dust settles...`): "Click 'The dust settles...' in Crusader Kings III".

### 13.8 Data files
`bridge/data/ck3_knight_effectiveness_sources.json`, `ck3_accolade_types.json`, `ck3_artifact_types.json`,
`ck3_artifact_to_bl_items.json` and `ck3_combat_traits.json` are inputs of both the CK3 generator and the bridge; they and their
generators under `tools/gen/knights/` change only together (`--check` must stay "up to date"), and any change of a key list in them
(traits, accolade types, artifact types, parameters) requires regenerating the mod.

---

## 14. ADDENDUM — battle clock (steered CK3 combat after the Bannerlord fight, 2026-10-04)

Additive. Request protocol stays **2**: the module is unchanged, and a second clash is an ordinary request (14.6). Keypad payload
**version 4** is new; a version 3 payload behaves exactly as §2, §10 and §13 (immediate apply, `force_win`). Readers stay tolerant
(§5). Amends §2.1, §2.2, §5.7, §5.8, §10.3 and §13.5 as marked.

### 14.1 Meaning
With payload version 4 the result is no longer applied at day 0. The apply (0002 "The dust settles...") applies character fates,
`battle_event` lines, awards and the summary values, stores each army's end state on the army, and starts **steering**: CK3's own
combat inflicts the Bannerlord dead over the next days (by default until combat day 12, never after day 13), so casualties, war
score and the battle summary are native. Then the Bannerlord winner wins (`set_winner` while the game runs, as measured in
game; `force_win` is never issued; loser `set_skip_pursuit`; both sides `set_allow_early_retreat` from the steering start). One day after
the combat ends, the mod sets every army's men-at-arms exactly and cuts levies down to the end state, then opens 0003. An army or
MaA type no slot names ends at its state at the steering start. Outcome 3 steers to the targets and then hands the combat back to
CK3 without a winner. Outcome 4 applies nothing, as before, except that a cancelled second clash hands its parent back
(`ST|RESUME` reason 4). If the combat is already gone at the click, or at combat day 13 or later, the end state is applied at once
(no war score from the battle; `ST|DONE` reason 4).

### 14.2 Register map, payload versions 3 and 4 (final; amends §2.2, §10.3, §13.5)
Every register r0..r199 has exactly one meaning per version. Unused registers stay 0 and are skipped with F5 seeks (§8.2).

| Regs | Payload 3 | Payload 4 |
|---|---|---|
| r0 | 3 (2 = legacy, plain checksum) | 4 |
| r1 | bid | bid (a second clash has its own bid, 14.6) |
| r2 | outcome: 1 attacker wins, 2 defender wins, 3 abort, 4 cancel | 1 / 2 = steer, then this side wins; 3 = steer, then hand back; 4 = cancel |
| r3 | province id (diagnostics only, §5.8) | same |
| r4..r6 | summary: my_lost, enemy_lost, my_kills | same, **cumulative** over all clashes of the CK3 combat |
| r7 | prestige bonus | same, **per clash** (each apply adds it once) |
| r8 | 0 | attacker kill target: enemy dead CK3 must count for the attacker side, cumulative over clashes |
| r9 | 0 | defender kill target (same) |
| r10 + 14i, i = 0..7 | army slot i: army tag | same |
| r11 + 14i | levy loss ‰ (0..1000) | levy **survivors** (absolute men), less what the fix restores to indexed MaA types without a pair (their dead, and in a reinforcement clash their earlier clashes' losses); unindexed types keep CK3's losses. An army of an earlier clash names every indexed type in its pairs and is listed only when it lost men in this clash |
| r12 + 14i + 2k, r13 + 14i + 2k, k = 0..5 | MaA pair k: type index (1-based, `ck3_maa_index.json`), survivor share of max ‰ | same (set exactly, up or down, one day after the combat) |
| r122 + 2k, r123 + 2k, k = 0..3 | award slot k: character tag, award value (§13.5) | same |
| r130 + 3j .. r132 + 3j, j = 0..19 | character slot j: tag, fate 1..4, killer tag (written only for fate > 0, §13.5) | same |
| r190 + 2(k−4), r191 + 2(k−4), k = 4..7 | award slot k (§13.5) | same |
| r198 | 0 | steering hint: pace day 6..13 (0 = the mod's default 12; the bridge sends `battleClock.paceDays`, default 12) + 100 × flags (flags reserved, 0) |
| r199 | checksum (14.3) | checksum (14.3) |

- Army slots occupy r10..r121, award block A r122..r129, characters r130..r189, award block B r190..r197; nothing overlaps.
- Every value is a non-negative integer below 1,000,000 (§2.2); the bridge clamps r8 and r9 to 999,999.
- Awards (outcomes 1..3) and character fates are **per clash** in both versions: a second clash sends only its own.
- An army not named by a clash-k payload keeps the end state of the earlier clash (or its steering-start state).
- A version 4 outcome sent as version 3 (the old-mod fallback of 14.3) leaves out army slots with a 0 ‰ levy loss and no MaA pair:
  they exist only for their levy survivors and change nothing in a version 3 apply.

### 14.3 Commit, checksum and the IN dump (amends §5.8, §10.3)
- The commit accepts `r0` = 2, 3 or 4. **Checksum:** version 2 = plain sum (§5.8); versions 3 and 4 = the position-weighted sum
  of §10.3, `r199 = Σ_{i=0..198} ((r_i mod 9973) × ((i mod 16) + 1)) mod 9973`, with `r0` itself in the sum. Payload 4 changes no
  checksum rule.
- Every IN dump (`IN|OK` and `IN|BAD`) carries `ck3xbl_bid`, `ck3xbl_in_sum`, `ck3xbl_in_count` (§5.8) and, from this addendum on,
  **`ck3xbl_in_maxver`** = 4 (the highest payload version the mod accepts). A dump without it comes from a mod before §14.
- **Old mod, new bridge.** A mod before §14 computes the weighted sum for every `r0` other than 2 (its commit's `else` branch,
  `ck3xbl_gen_keypad_effects.txt`) but accepts only `r0` = 2 or 3, so it answers a version 4 payload with `IN|BAD`, no
  `ck3xbl_in_maxver`, and `ck3xbl_in_sum` = r199. Only in exactly that case does the bridge send the same outcome once more as
  version 3 (`r11 + 14i` = levy loss ‰; r8, r9, r198 = 0; the battle is then applied at once as before) and use version 3 for the
  rest of its session. An `IN|BAD` that carries `ck3xbl_in_maxver` (stale bid, overflow, no pending battle) is an ordinary
  failure and never lowers the version. The run-file channel follows the same rule.

### 14.4 Apply of payload 4 (amends §2.1, §13.5)
Order inside `ck3xbl_apply_effect` for `r0 = 4`: `RES|BEGIN`, `RES|LIVE` or `RES|GONE`, awards (`RES|AW`, outcomes 1..3), fates and
`battle_event` lines, summary values, the steering start (`ST|BEGIN`), storage of the army slots on the armies, `RES|END`, then the
finish (pending battle, registers and pins released as in §5.7/§10.6; armies being steered keep their tags). No army slot is
applied at the click (no `RES|A`): the end state is set by the fix one day after the combat (`ST|FIX`). Event 0002's option sends
the toast `msg_ck3xbl_battle_rages` instead of opening 0003; 0003 opens after the fix, one window at a time, with the cumulative
r4..r6. `RES|END` keeps its meaning (apply finished; the run-file channel waits for it).

### 14.5 New and extended records (OUT)
All `ST` records have a dump that starts with `ck3xbl_bid` = the **steering bid** (the bid of the clash whose payload started the
current steering). Each pulse starts with only `scope:ck3xbl_chain` (`ck3xbl_clock.0001` begins with
`ck3xbl_st_clear_carried_scopes_effect`); records of one pulse (or of one apply chain) share its saved values, so a record can still
carry a key an earlier record of the same pulse set. Readers take `ck3xbl_st_reason` only from SUSPEND, RESUME, HANDBACK and DONE, `ck3xbl_st_capped` only from
WIN and HANDBACK, `ck3xbl_st_mode` only from BEGIN and RESUME, `ck3xbl_st_parent` only from BEGIN and `ck3xbl_st_child` only from
SUSPEND; the mod sets each of them fresh on exactly those stages.

| Record | When | Dump keys (beyond ck3xbl_bid) |
|---|---|---|
| `ST|BEGIN` | in the apply, before `RES|END` (run file: before its `RES|END`, i.e. possibly before the bridge has detached the battle) | `ck3xbl_st_mode` (1 decide, 2 hand back), `ck3xbl_st_winner` (0/1/2), `ck3xbl_st_att_target`, `ck3xbl_st_def_target`, `ck3xbl_st_end_day`, `ck3xbl_st_cday`, `ck3xbl_st_parent` (0 or the bid this clash continues) |
| `ST|P` | daily while steered | `ck3xbl_st_cday`, `ck3xbl_st_cday_marks`, `ck3xbl_st_days`, `ck3xbl_st_att_killed`, `ck3xbl_st_def_killed`, `ck3xbl_st_att_target`, `ck3xbl_st_def_target`, `ck3xbl_st_att_men`, `ck3xbl_st_def_men`, `ck3xbl_st_att_gain`, `ck3xbl_st_def_gain` (gain × 1000), `ck3xbl_st_susp` (0/1). No war score key: `warscore_value` reads 0 on a live combat (measured in game) |
| `ST|SUSPEND` | a loser-side join opened a second clash | `ck3xbl_st_child` (new bid), `ck3xbl_st_reason` (1) |
| `ST|RESUME` | steering continues after a suspension, always in hand-back mode | `ck3xbl_st_mode`, `ck3xbl_st_reason` (2 AUTO/ABANDON of the child, 3 failsafe/stale, 4 child cancelled in Bannerlord); the sides keep their Bannerlord roles |
| `ST|WIN` | the end call (`set_winner`) was issued, game running; logged once | `ck3xbl_st_winner`, `ck3xbl_st_cday`, `ck3xbl_st_att_killed`, `ck3xbl_st_def_killed`, `ck3xbl_st_att_short`, `ck3xbl_st_def_short`, `ck3xbl_st_capped` (0/1), `ck3xbl_st_retry` (always 0: when the combat does not end within 3 pulses the backstop hands it back, `ST|HANDBACK` and `ST|DONE` reason 6, and never retries) |
| `ST|HANDBACK` | hand-back mode reached its targets, or a forced hand-back | as `ST|WIN` without `ck3xbl_st_winner`, plus `ck3xbl_st_reason` (2 targets/day E, 3 day 13 while suspended, 6 end call failed) |
| `ST|FIX` | per army, one day after the end | `ck3xbl_a_tag`, `ck3xbl_after`, `ck3xbl_st_levy_excess`, `ck3xbl_st_default` (1 = no slot named it), `ck3xbl_st_unindexed` (men of MaA types outside the index left as CK3 had them) |
| `ST|DONE` | steering finished | `ck3xbl_st_reason` (1 decided, 2 handed back, 3 natural end before the target, 4 combat gone or too late to steer, 5 the Bannerlord loser won (after an `ST|RESUME` this is CK3's own verdict in hand-back mode, not a failure), 6 the end call did not end the combat) |

Extended dumps:
- `B|BEGIN` + `ck3xbl_parent_bid` (0 = none) and `ck3xbl_clash` (1, 2, ...);
- `J` + `ck3xbl_j_steer` (0 not steered, 1 joined the Bannerlord winner's side, 2 loser side and a second clash offered, 3 loser
  side without an offer, 4 hand-back mode from the start, i.e. outcome 3; after an `ST|RESUME` the sides keep their Bannerlord roles,
  so a join is 1 or 3 there). 1, 3 and 4 make the army a steered joiner: native losses, no end state, released by the fix;
- `END` + `ck3xbl_end_st_bid` (0 = not steered). A combat's `END` may name the latest snapshot bid (a child) while
  `ck3xbl_end_st_bid` names the steering bid;
- `IN` + `ck3xbl_in_maxver` (14.3);
- `RES` + the summary values `ck3xbl_res_my_lost`, `ck3xbl_res_enemy_lost`, `ck3xbl_res_my_kills`, `ck3xbl_res_won` (§2.1 item 4).

### 14.6 Second clash
A qualifying loser-side join (≥ 100 men and ≥ 10 % of the loser's fighters, field free) suspends steering and emits a full snapshot
(§1.2) of the live combat with a **new bid** and `ck3xbl_parent_bid`, then the auto-pausing toast `msg_ck3xbl_reinforcements` and
event 0001 titled "Reinforcements Arrive!". The child snapshot keeps each steered army's `ck3xbl_aid` and side code and gives it the
child's `ck3xbl_bid`; an army being fought in Bannerlord keeps everything; the joiner gets a new tag as usual.
- **FIELD**: an ordinary battle (request protocol 2, no new member): its request copies the parent's scene, season, coats of arms,
  `inEngineTitle` and deployment, takes the next stock hour, keeps the parent's `playerSide`, says "Reinforcements arrive!" in
  `presentation.title`, and caps every parent army at its end state of the earlier clashes (characters captured or killed there
  are left out). Its version 4 payload carries the child bid in r1, cumulative targets (r8, r9) and cumulative r4..r6.
- **AUTO/ABANDON**, a Bannerlord cancel of the child (r2 = 4), or 3 game days without an answer (the mod then logs `CHOICE|AUTO` for
  the child): the parent continues in hand-back mode, and its 0003 says the battle continues. A child 0001 answered after that
  offers no field.
- A third army that joins during the suspension is logged (`J`) and becomes a steered joiner (`ck3xbl_st_bid`, `ck3xbl_st_joiner`:
  native losses, no end state, released by the fix). At `ST|RESUME` the reinforcement that opened the offer becomes a steered joiner
  the same way. A second loser-side join after clash 2 nests the same way (parent = clash 2's bid).
- **Stale child.** A child that was taken to the field but went stale (the battle was dropped) does not hold the parent: at the
  failsafe the parent resumes (`ST|RESUME` reason 3), on day 13 it is handed back (`ST|HANDBACK` reason 3), and then the mod logs
  `CHOICE|ABANDON` with the child's bid, which the bridge treats like AUTO.
- **Module wording.** `presentation.title` "Reinforcements arrive!" is not a battle name. From module v0.4.0 a title that ends in
  '!' or '?' makes the result footer read "The battle is won/lost/over" (`CardText.Outcome`); the native objective header shows the
  title as sent.

### 14.7 Bridge
A version 4 battle is detached after `IN|OK` (keypad) or `RES|END` (run file) into a steering table, so the Bannerlord slot is free
at once. It closes at `ST|DONE`, at `CHOICE|ABANDON`/`AUTO` or `END` before `ST|BEGIN`, or at a CK3 restart or session change. The
bridge never acts on a steered battle; it only follows it (journal, tray progress). The battle archive keeps, next to a reinforcement
clash's `snapshot.json`, the first clash's planned snapshot as `snapshot_root.json`: the mod keeps each army's steering-start state
from its first clash, so a restarted bridge needs it to plan clash 3 and later. A battle delivered as version 3 (the
fallback of 14.3, or `battleClock.enabled = false`) is not detached and ends as before.

## 15. ADDENDUM — live-test fixes 2026-10-04
(Request protocol stays 2, keypad payload stays 4: one
additive dump key, no new member.)

### 15.1 The player when no army of his side has a commander (item 1)
- **Mod**: in every player-involved snapshot the player has a `C` record on the side his realm fights on:
  his record as participant, commander or knight when he already has one (`C|PRIMARY` with `ck3xbl_c_is_player = 1` in the live
  test), else `C|PLAYER` (§1.2, §5.2: extended, not duplicated). The player's record carries the new dump key
  **`ck3xbl_c_absent`** = 1 when the player is not physically in the combat, 0 when he is, plus the usual `C` keys and `CX` lines.
- **Bridge parser:** `CharacterSnapshot.Absent` (`bool?`) = `ck3xbl_c_absent`, read only on the player's record (`IsPlayer`): saved
  values travel along the dump chain, so a later record's dump can still carry the key. Not logged = null (older mod).
- **Mapper (avatar):** unchanged when the player commands or rides as a knight, and when an army of his side has a commander (that
  commander stands in, `role: "commander"`). Otherwise the player's own record on his side (any role) becomes the avatar:
  `isPlayer: true`, `role: "player"`, key `h0`, the side's `general`, `playerIsGeneral: true`; formation, kit, skills and level from
  his CK3 facts as for any hero; a knight of that side rides under him. Without any record of the player, today's behaviour (a
  knight, else no hero and a stock general). The knights pass never takes this stand-in as the side's CK3 commander (his commander
  traits do not shape troop buffs, casualty factors or Tactics bonuses): CK3's side has no commander.
- **Outcome:** an absent player (`ck3xbl_c_absent` = 1; not logged: neither commander nor knight) is never killed, wounded, maimed
  or captured by the Bannerlord result: his hero fate maps to 0 (unharmed) and no character slot is sent for him, whatever
  `playerDeathIsReal` says. No personal prestige (r7) and no award slot either (he was not there); his Bannerlord kills still count
  in r6 and in the summary ("You struck down N foes yourself"). An absent player is never named as killer (`killer` of a fate):
  neither for the blows his avatar struck nor as his side's `general`, so an enemy he or a nameless soldier struck down dies in
  battle with no killer, as when CK3's side has no commander. A player logged with `ck3xbl_c_absent` = 0 keeps the rules of the
  player in person (protected from capture, death only with `playerDeathIsReal`).

### 15.2 Reinforcement-clash wording (item 2)
- `presentation.subtitle` of a reinforcement clash (§14.6): "Fresh troops join the Host of X · <season> <year>" when the newcomer's
  owner or commander is the host's leader (the character the host is named after) or commands an army of the earlier clash on that
  side; "<commander> brings fresh troops to the Host of X" when the new army has a commander; else "<owner> joins the Host of X"
  (no names: "The <army> joins the Host of X"). The same rule serves either side.
- Outcome `summary` of clash 2 and later (the first line of the bridge result card): this clash's losses and kills first, then the
  whole battle's: "Defeat at Apulia: 777 of your men fell, and 156 of the enemy. Over both clashes: 1,024 of your men and 184 of the
  enemy." (clash 3+: "Over all N clashes"). r4..r6 stay cumulative; a first clash keeps its single sentence.

### 15.3 Bridge start-up (items 4 and the stale result)
- `logs/dedicated_server.log` is read silently at attach: its toggles are never replayed as `[ck3] paused/running` lines. The last
  toggle counts as CK3's state only while CK3 runs (its file is that session's); a file a closed CK3 left behind leaves the state
  unknown. One journal line reports the state at attach; after it every toggle is live. A recreated file (CK3 relaunched) resets the
  state to unknown with one line, and the new session's toggles are live.
- `result.json` whose `id` is not a battle the bridge requested in this run (a file an earlier run or a test left behind) is
  skipped quietly: no journal line, no event.

## 16. ADDENDUM — battle gear (CK3 artifacts that are Bannerlord items, 2026-10-17)

Generated by `tools/gen/equipment/equipment.mjs` through `tools/gen/gen_ck3_mod.mjs` from `bridge/data/bl_catalog.json` and
`mapping.json` `culture_map`; the bridge reads the same table, `bridge/data/ck3xbl_equipment.json`. Request protocol and keypad
payload are unchanged: the gear only adds overrides to `HeroSpec.equipment` (§13.4).

### 16.1 Items, slots, types
- **One artifact = one Bannerlord item.** Per Bannerlord culture (and `neutral_culture`), item class and tier 1..6 the item with the
  highest `tierf` is chosen (tournament, practice and civilian items excluded): about 350 items. Each has an index `n` (1-based, in
  slot, kind, culture, tier order) and a modifier `ck3xbl_item_<n>_modifier`, which identifies it (types are per kind; reforging is off).
- **Ten inventory slots** (`common/artifacts/slots/ck3xbl_gen_gear_slots.txt`): four weapon slots `ck3xbl_weapon_main`,
  `ck3xbl_gear_1..3` of the one slot type `ck3xbl_weapon`, each taking any weapon (2026-10-19; before, one main weapon slot and three
  sidearm slots). The items keep their category flag, which `CX|gear` reports: `ck3xbl_main` (one- and two-handed swords, axes,
  maces, bows, crossbows) and `ck3xbl_gear` (shields, spears, lances, polearms, thrown, daggers); prowess adds up over all four. Then `ck3xbl_head`, `ck3xbl_body`, `ck3xbl_hands`, `ck3xbl_legs`, `ck3xbl_cape`, `ck3xbl_horse` (horse or camel, with the harness
  of the table). The vanilla crown, weapon and armour slots stay; their artifacts are mapped as before (§13), and the gear wins.
- `gui/window_inventory.gui` is vanilla's file of the CK3 install with a **Battle Gear** page added to its left side (toggle button
  top right, a 3 x 4 grid of the vanilla slot widget). It must be regenerated after a CK3 patch changes the vanilla file.
- Icons: vanilla artifact icons where CK3 has one; our own drawings (`tools/gen/equipment/make_icons.mjs`) for horse, helmet, boots, cloak.

### 16.2 OUT record
| Record | Meaning |
|---|---|
| `CX|gear|<slot flag>|<Bannerlord item id>` | one per equipped battle gear artifact of the character, after its CX|artifact lines (no dump) |

### 16.3 Stats and the bridge
- q = (tier - lowest tier of the kind) / (highest - lowest). prowess = max(1, round(cap x q^1.6)) with caps: main weapon 20, body 8,
  head 7, shield 6, spear/lance/polearm 4, thrown 3, gloves and boots 3, dagger and cloak 2, horse 2 (floor 0). Extras: main weapons
  q >= 0.75 `monthly_prestige` 0.1 + 0.4 q; head and body q >= 0.25 `knight_effectiveness_mult` 0.15 q; shields q >= 0.5 0.1 q;
  cloaks `monthly_prestige` 0.1 + 0.2 q; horses `movement_speed` 0.03 + 0.07 q. Quality and wealth (rarity, durability) 10 + 90 q.
- Bridge (`GearOutfitter`, after the vanilla artifact overrides): weapons = the main weapon, the sidearms, then the kit's weapons of a
  class group none of them has, four at most; a bow or crossbow without its ammunition gets the kit's, else the catalog's best of a tier
  not above the launcher (the last weapon that is neither a launcher nor a shield makes room). Head/Body/Gloves/Leg/Cape replace the
  kit's. A horse sets Horse and HorseHarness and makes the hero ride (formation Cavalry, HorseArcher with a bow or crossbow).
  An id the catalog does not know is ignored.

### 16.4 The armory
- Decision `ck3xbl_visit_armory_decision` (rulers; cooldown 3 months) opens event `ck3xbl_armory.0001` with six offers: own culture
  main weapon, sidearm, armour piece, horse, any item, and one item of any culture. The stock lasts two years
  (`ck3xbl_armory_stocked`). Buying re-opens the event.
- Price: gold = 200 x (top / 200)^q to the nearest 10 (top: main weapon 5000, horse 4000, body 3500, head 2500, horse camel 3000,
  shield/polearm 1500, cloak 1000, gloves/boots 800, thrown 600, dagger 400); prestige = gold / 10 when q >= 0.75. Both x the era
  factor of the buyer's culture: tribal 1.5, early 1.25, high 1, late 0.85. No discount for small incomes.
- Offer weights by tier: 30, 28, 20, 12, 7, 3 (tier 1..6).

### 16.5 AI
- The AI uses the decision (ai_check_interval 36, gold >= 600) and buys an offer with weight 5 against leaving 20.
- `yearly_playable_pulse`: an AI ruler with 600+ gold, 25 % a year, rolls one item of its culture and buys it only when it keeps two
  thirds of its gold and prestige; 60 % for a random knight, 40 % for itself.

### 16.6 Vanilla artifact events
`common/scripted_effects/zz_ck3xbl_gen_vanilla_artifact_hooks.txt` redefines `create_artifact_weapon_effect`,
`create_artifact_armor_effect` and `create_artifact_bow_effect` (loaded after vanilla's file, the later definition is used; verify in
error.log after a CK3 patch). They create a gear item of the owner's culture (weapons and sidearms, armour pieces, bows and crossbows)
with tier weights 20, 30, 30, 15, 4, 1, and set `scope:newly_created_artifact` as vanilla does. `CREATOR` and `SET_*_TYPE` are accepted
and unused.

## 17. ADDENDUM — strength balance (2026-10-17, `bridge/CK3xBL.Mapping/Planning/StrengthBalance.cs`)

A straight clash in Bannerlord should end as CK3 would decide it; the player's command is what tips it. The agent counts stay
proportional to the CK3 men (§6); instead the side that Bannerlord would make weaker than CK3 does gets damage and health.

- CK3 strength of a side: `S = (Σ men × damage) × (Σ men × toughness)` over its levies and men-at-arms (catalog base stats).
  CK3 casualties per day follow the enemy's damage over the own toughness, so the fight obeys Lanchester's square law and S is
  its invariant.
- Bannerlord estimate: `S = (Σ agents × worth)²`, worth of an agent = `10 × 2^((troop tier − 1) / k) × √(statDamage × statHealth)`
  (mean catalog tier of the group's troop mix, §16 stat factors). A tier 1 troop is worth a CK3 levy (damage 10, toughness 10);
  `k` = troop tiers per doubling of worth (`battle.strengthTiersPerDoubling`, default 3: Bannerlord values elite troops less than
  CK3; with 2 the estimate uses the tier rule's own scale). Heroes and knight retinues count on neither side.
- `f = (S_ck3 att / S_ck3 def) / (S_bl att / S_bl def)`. f > 1 strengthens the attacker by f, else the defender by 1/f, at most
  `battle.strengthMaxFactor` (default 4): `SideBuffs.damageDealtMult × √f`, `damageTakenMult / √f`, on top of the commander-trait
  buffs (§13). Below 2 % nothing changes. `battle.strengthBalance = false` switches it off.
- `request.balance` (`BalanceInfo`) records the numbers; the module ignores it. The journal writes one `[balance]` line at
  planning (both ratios, the factor) and one with the field result (winner, duration, agents down and routed per side), for
  calibrating `k` in play.

## 18. ADDENDUM — single combat in Bannerlord (2026-10-17)

### 18.1 Hook
CK3 starts every single combat with `configure_start_single_combat_effect` (vanilla `00_single_combat_effects.txt`, about 75
callers). `common/scripted_effects/zz_ck3xbl_gen_single_combat_hook.txt` (generated from the install by
`tools/gen/duel/single_combat_hook.mjs`, loaded after vanilla: verify in error.log after a CK3 patch) redefines it: the head saves
`sc_initiator`, `sc_attacker`, `sc_defender`, `follow_up_event`, `invalidation_event` and the rules `fatality`, `fixed`, `locale` as
saved scopes; a duel a human player fights with no fixed winner while the field is free (`ck3xbl_duel_wanted_trigger`) goes to
`ck3xbl_duel_begin_effect`, every other one to `ck3xbl_sc_setup_effect` = the rest of the vanilla effect, unchanged.
`DUELS_ENABLED = false` in the tool generates only an empty `ck3xbl_sc_setup_effect` and leaves vanilla untouched.

### 18.2 Records
A duel is a snapshot without armies: `B|BEGIN` with `ck3xbl_kind = 2` and `ck3xbl_d_fatality` (0 no, 1 possible, 2 always,
3 default, 4 practice), the L lines of the player's location, then per duelist `S|att` / `S|def` (size 1, the duelist as primary)
and `C|KNIGHT|att` / `C|KNIGHT|def` (+CX, `ck3xbl_c_absent = 0`), `B|END`. CHOICE, keypad, IN and RES records are the battle's.

### 18.3 Bannerlord and the payload
The mapper puts the two duelists alone on the field of the location (no troops, no retinues, each the general of its side,
deployment auto, no strength balance), with Bannerlord death chance 0 for no/practice, 1 for always, 0.5 for possible/default.
The outcome is payload 3: r2 = winner (1 attacker, 2 defender; 3/4 = none), **r8 = 1 when the loser was killed in Bannerlord**;
no army slots, fates or awards.

### 18.4 CK3 side
Events `ck3xbl_duel.0001` (fight it yourself / let fate decide), `0002` (keypad, waiting), `0004` (hidden apply).
`ck3xbl_duel_apply_effect` finishes the pending duel like a battle, sets `scope:fixed = flag:sc_attacker|sc_defender` and
the rule: a loser killed in a "possible" duel → `always`; a loser only downed in a "possible" or "default" duel → `no`; "always",
"no", "practice" stay. Then `ck3xbl_sc_setup_effect` runs CK3's own single combat with that winner: rounds, wounds, deaths and the
caller's output event as always. Rewards (`ck3xbl_duel_rewards_effect`): blademaster XP 20 for the winner and 8 for the loser
(the trait is earned at 50 XP), brave for beating someone with more prowess (a craven loses craven instead), berserker at the tenth
duel won (`ck3xbl_duel_wins`), and a 50 % chance that the winner takes one of the loser's inventory artifacts (never a court one).
The blademaster trait gives every weapon skill +20 per level in Bannerlord (`ck3_combat_traits.json`, hero.skillAdd).

## 19. ADDENDUM — levies by development (2026-10-17)

The A (and J) dump carries `ck3xbl_a_dev` = `development_level` of the army owner's capital county (-1 = no capital). The levy mix
of `levy_by_era` moves along the eras by `shift = clamp((development - 20) / 20, -1, +1.5)`: development 20 keeps the culture era's
own mix, 40 arms the levies like the next era, 0 like the previous one; between whole eras the two mixes are blended, and past the
first or last era the end mix stays. CK3 itself gives levies fixed stats (damage 10, toughness 10), so only the look and the
Bannerlord troops change; the strength balance (§17) still holds the sides to CK3's own ratio.

## 20. ADDENDUM — skirmishes on the road (2026-10-17)

### 20.1 Vanilla events with a Bannerlord option
`tools/gen/skirmish/travel_hooks.mjs` copies these vanilla files whole from the install into the mod (same paths, so they replace
vanilla's; regenerate after a CK3 patch) and puts one option first in each listed event; every vanilla option stays:

| File (events/travel_events/) | Events | Foe scope | Kind |
|---|---|---|---|
| travel_danger_events.txt | 1002 (mountain bandits) | bandit | 1 bandits |
| travel_danger_events_joe.txt | 6010, 6011, 6012, 6013 (deserters) | soldier | 2 deserters |
| travel_events.txt | travel_events.1003 (bandit attack) | attacking_bandit | 1 |
| travel_events_veronica.txt | 3070 (bandits on a bridge), 3130 (assassin), 3140 (an army tries to seize you) | bandit_scope, sexy_assassin, hostile_commander | 1, 3, 4 |
| travel_events_james.txt | 4020 (zealots) | strong_believer_scope | 5 zealots |

The option (`ck3xbl_skirmish.fight`, shown while the field is free) saves `scope:ck3xbl_foe` and `scope:ck3xbl_sk_kind` and opens
`ck3xbl_skirmish.0001`. The copies carry `# CK3XBL vanilla copy:`; the lint and the pulse-scope list skip them.

### 20.2 Records and Bannerlord
A small battle: `B|BEGIN` with `ck3xbl_kind = 3` and `ck3xbl_sk_kind`; the foe's side `S|att` + `A|att` (the band as levies of his
culture, `ck3xbl_sk_foes_value` = 8 + danger / 5, +6 for soldiers, 3 for an assassin; 3..28) + `C|KNIGHT|att` (the foe); the
travellers `S|def` + `A|def` (10 guards, 6 against an assassin) + `C|KNIGHT|def` for the player and up to four knights on the same
journey. A tags are 900000 + 2 × bid + side (no CK3 army). CHOICE|FIELD follows at once (the choice is the option). The mapper caps
the field at 50 fighters, heroes included (`ForcePlanner.SkirmishMaxFighters`), without the per-side floor or retinues, deploys
automatically and titles it by kind. The outcome is payload 3 without army slots: fates and awards of the fighters as in a battle.

### 20.3 CK3 side
Events `ck3xbl_skirmish.0002` (keypad, waiting; "slip away" costs minor gold) and `0004` (apply): `ck3xbl_apply_effect` applies
the fates, then `ck3xbl_skirmish_apply_effect` the spoils: a win gives minor gold and prestige and, half the time, one of the foe's
inventory artifacts; a loss costs medium gold.

## 21. ADDENDUM — costumes (2026-10-17)

Troops look like their CK3 culture, era and role; they fight as the stock troop does.

### 21.1 Table
`tools/gen/costumes/build_costumes.mjs` (`--bannerlord <dir>` or BANNERLORD_DIR; `--check`) writes `bridge/data/ck3xbl_costumes.json`
from the wearable items of SandBoxCore and, when they are in Modules, Open Source Armory and Open Source Saddlery:
- `items`: id, slot (Head, Body, Leg, Gloves, Cape, HorseHarness), culture, armour (sum of the Armor attributes), `era_min`
  (0 tribal; 2 for great helms, kettle hats, surcoats; 3 for plate, brigandines, visors), `tags` (levy, cav, rng from the
  name). Classical Rome, the arena, regalia, women's dress and pack harness are left out.
- `looks`: for every mapping.json profile, item id -> weight: the profile's Bannerlord cultures by share (for example
  iberian_christian vlandia 0.7 + aserai 0.3), neutral items 0.3, x2.5 for the profile's own words (Norse: nordic, vendel;
  Celtic: plaid, tartan; Byzantine: cataphract), none with the words ruled out (fur in the south, turbans in the north).

### 21.2 Request
Each group's look key is `profile|era ordinal|role` (`CostumePlanner.Key`; levies `levy`, MaA by base type `cav`, `rng` or `inf`,
knight retinues `inf`). `TroopRequest.look` names it, and `BattleRequest.looks[key].slots[slot]` lists `{id, weight}`: the
profile's items not later than the era, x2 when tagged for the role. Setting `battle.costumes` (default true) off sends none.

### 21.3 Module
`CK3Costumes` dresses each grouped agent in OnAgentBuild (before its group buffs). For every armour slot the stock troop fills,
it takes the look's items the game loaded that fit the slot and lie within max(2, 15 %) of the stock item's armour (else
the nearest three within max(5, 35 %), else the stock item stays), draws one by weight, and re-equips the agent through
`Agent.UpdateSpawnEquipmentAndRefreshVisuals`. A rider's horse gets a harness of the same family the same way. Weapons,
shields, horses and empty slots are untouched; heroes keep their own gear (§16).

Heroes (`heroes[].look`, their culture, era and the role of their formation) are dressed in the build, every armour slot their
`equipment.overrides` leaves to the kit (CK3 gear and artifacts stay). A lord's kit is heavier than most of a look's items, so a
hero draws among the look's three items nearest to the kit item's armour at any distance: always in the dress of their culture
and era (2026-10-18; before, the troops' tolerance bands left most heroes in their lord's kit). The module logs each dressed hero.
Since troops are dressed after deployment (§31), heroes, a handful, still are in the build.

### 21.4 Launch
With `bannerlord.armoryModules` (default true) the bridge adds the installed `OpenSourceArmory` / `OpenSourceSaddlery` before
CK3Bridge, with SandBox and StoryMode, which they depend on. Without them the costumes use the vanilla items alone.
`armory_failed.txt` (a crash before the first heartbeat with the armory loaded) leaves them out; a Bannerlord the bridge has just
seen exit is never adopted, so a memory restart (§31) no longer reads the dying game's exit as an armory crash (2026-10-18).

## 22. ADDENDUM — spawn order, prowess speed, no harness swap (2026-10-17)

### 22.1 Spawn order (referred to as §3.6 in the module)
Bannerlord fields only the battle size at once and sends the rest as reinforcements, in the order of each side's troop supplier.
Right after building the stock `CustomBattleTroopSupplier` the module replaces its private queue `_characters` (`CK3SpawnOrder`;
the stock order stays when the field is missing): heroes and the stock general first (priority 100), then every professional
agent (MaA regiments, knight retinues) at 2..3, then the levies (`levy#` groups) at 1..2. Within a tier the j-th agent of a group of
n has priority base + 1 - (j + 0.5) / n, so all groups of the tier spread evenly over its spawns. Levies take the field only once
too few professionals are left to fill it. `GroupLedger` reservations carry the same rank, so a Bannerlord troop shared by a
regiment and a levy is booked to the regiment first.

### 22.2 Prowess speed
With the stat balance (§17) a hero's `buffs.speedMult` also gets x(1 + 0.004 x prowess), at most x1.10 (`HeroPlanner.ProwessSpeed`),
before the age factor; the product stays within 0.6..1.2. It acts on foot (MaxSpeedMultiplier, CombatMaxSpeedMultiplier).

### 22.3 Costumes leave horses alone
Re-equipping a mount after it was built (§21.3) made horses invisible in play. The bridge no longer sends HorseHarness items, and the
module ignores that slot.

## 23. ADDENDUM — hero costumes, CK3 check, battle size 1000 (2026-10-17)

- **Hero costumes.** `HeroSpec.look` (a key of `looks`, §21) dresses a hero's kit armour in the look of their own culture and era
  (role by formation: Cavalry/HorseArcher `cav`, Ranged `rng`, else `inf`). Slots named in `equipment.overrides` (CK3 battle gear,
  artifacts, an emptied slot) are never touched. The module dresses heroes in BattleTracker, before CK3FaceGuard.
- **CK3 check.** After each battle the bridge logs `[balance] <id>: CK3 check: CK3 favours the <side> (<odds>:1), Bannerlord winner <side>
  -> same|DIFFERENT` and a running "agreed in n of m battles" for the session. The favourite is the side with 1.1 times the CK3
  strength of the balance record (§17; commander advantage and terrain are not in it); a closer battle is a toss-up and is not
  counted. Nothing is changed by it.
- **Battle size.** `battle.battleSize` defaults to 1000 (Bannerlord's largest); `maxAgentsTotal` stays 1000.

## 24. ADDENDUM — CK3's RAM during a battle (2026-10-17)

`battle.trimCk3Memory` (default true): 3 s after 'go' the bridge calls `EmptyWorkingSet` on CK3 (`ProcessMemory.Trim`), and
journals `[memory] <id>: CK3 working set <before> MB -> <after> MB`. The pages go to the standby list or the page file. Paused
behind Bannerlord, CK3 hardly touches them, so Bannerlord gets that RAM; CK3 takes its pages back over its first seconds after
the battle. No limit is set and nothing of CK3 is lost. A failure is one warning line.

## 25. ADDENDUM — heroes' own arms (2026-10-18)

`HeroSpec.banner` (`{code, source}`, the same Bannerlord banner code as a side's): a hero's own CK3 arms, those of their primary
title, else of their house (AppearanceProvider, the side banner's rules). It is left out when it is the side's banner itself (the
side's primary). The module wraps each side's troop supplier (`CK3HeraldrySupplier`): a hero with arms gets an origin that is the
stock one in every respect but `Banner`, so heraldic shields and garments show the hero's arms while team colours stay the side's.
An unusable code is logged and the side banner kept.

## 26. ADDENDUM — Russian texts (2026-10-18)

`battle.language` (`auto` | `en` | `ru`, default auto): auto reads `"language"={ value="l_russian" }` from CK3's
`pdx_settings.txt`. Every sentence the bridge writes (title card, subtitles, objectives, the reinforcement clash, single combat and
skirmish titles, result lines, renown, result card, the hint) comes from `Core.Texts.Words` in that language, and
`presentation.language` tells the module, whose own words (outcome, return footer, defaults) follow it. CK3 names are used as CK3
gives them; Russian phrasing avoids declining them ("Разгромите врага: <army>"). Bannerlord's Galahad font has the Cyrillic letters.

## 27. ADDENDUM — venues of single combats and skirmishes (2026-10-18)

- **CK3**: `B|BEGIN` of a single combat carries `ck3xbl_d_noble` (1 when the foe is not lowborn, `is_lowborn = no`).
- **Arena**: a noble single combat gets `kind: "arena"`, `scene: arena_<culture>_a` (the challenged side's Bannerlord culture, else the
  challenger's, else vlandia) and `fallbackScene` = the field scene it would have had. The bridge loads SandBox, where the arenas
  live, when it is installed (`bannerlord.arenaModule`, default true). The module opens mission `CK3Arena`: the behaviours of the
  custom battle lords hall with `CK3ArenaDuelController` as spawner (both duelists on foot at the two `sp_arena` points furthest
  apart, facing each other; no team AI) and the lords hall views plus the CK3 presenter. When the arena scene or its views are
  missing, the duel is fought on `fallbackScene`.
- **Woods**: a single combat with a lowborn foe and every skirmish on the road take a field chosen as for a wooded province (CK3
  terrain forest, taiga and jungle kept) and never a holding's village; season and climate apply as usual.
- Bandit hideouts are not used yet (their scenes have no spawn points for a fight).

## 28. ADDENDUM — AI heroes, arena crowd, bands by year, idle Bannerlord RAM (2026-10-18)

- **AI heroes**: `HeroBuffs.aiLevel` = clamp(0.3 + 0.035 x prowess, 0.3, 1) for every hero but the player's (prowess 20 = 1). The
  module moves the hero's AI abilities (block, parry, realizing feints and wrong-side blocks, attack decisions, chambers, stun
  estimates) towards 1 and its errors and delays down by up to 80 % (`CK3AgentStatCalculateModel.SharpenAi`); never for the
  player's agent.
- **Arena duelists charge**: without team AI a formation holds, so the arena controller orders every formation to charge and every
  AI fighter to pick targets itself.
- **Arena crowd**: `CK3ArenaAudience` hides the scene's `audience` placeholders (unanimated, the T-pose) and seats 45 % of them with
  `townsman_/townswoman_<culture>` playing `act_arena_spectator`; when that animation does not play, the stands stay empty.
  SandBox's MissionAudienceHandler needs a campaign settlement and cannot run in a custom game.
- **Bands**: a skirmish band (`LadderSource` "band") has a random size seeded by bid and date (bandits 6..18, deserters 8..20, an
  assassin's cutthroats 2..5, soldiers 12..26, zealots 8..18) plus half of what the road's danger added in CK3, and is armed from
  the profile's heavy infantry ladder (70 %) and bowmen one tier lower (30 %) at tier 1.5 + (year - 867) / 116.5 (1.5..4.5;
  deserters +0.3, soldiers +0.7, cutthroats +1). The band's leader has prowess 6 + (year - 867) / 30 at least (6..18).
- **Idle Bannerlord**: with `bannerlord.trimWhenIdle` (default true) a Bannerlord idle for 20 s gets `EmptyWorkingSet`, again every
  5 minutes while it stays idle (`[memory] idle Bannerlord working set ...`); never while it holds a battle.

## 29. ADDENDUM — tournament bouts in Bannerlord (2026-10-18)

### 29.1 Hook
`tools/gen/tourney/contest_hooks.mjs` copies `events/activities/tournaments/contest_events.txt` whole from the install (as §20) and
puts the option `ck3xbl_tourney.fight` first in the player's pivotal moments: `contest_events.0862` (single combat, tourney code 1),
`0861` (wrestling, 2) and `0610` (grand melee, 3), shown when the player competes and the field is free. It saves
`scope:ck3xbl_d_tourney` and opens `ck3xbl_tourney.0001`. The versus events resolve the bout one day later with
`contest_events.0802`; that call is wrapped in `NOT = { has_character_flag = ck3xbl_tourney_bl }`, the flag the option sets.

### 29.2 Records
`ck3xbl_tourney_begin_effect` logs the bout as a single combat (`ck3xbl_kind = 2`, fatality practice, `ck3xbl_d_tourney`): the
player against `scope:versus_contestant`, or in a grand melee the player against the other team's best fighter plus up to 8 more of
each team (`ck3xbl_duel_emit_knight_effect`, from the guest subsets team_alpha / team_beta), takes the field (CHOICE|FIELD) and
opens the waiting event `ck3xbl_tourney.0002`. The bridge sends every tournament bout to the arena (§27) with `request.tourney`
and the title "Tournament: ..."; wrestlers' weapon slots, helmet, gloves, cape and horse are emptied, and the module takes any weapon
left from them as they spawn. Nobody dies in Bannerlord (practice).

### 29.3 Result
`ck3xbl_tourney.0004` → `ck3xbl_tourney_apply_effect`: r2 = 1 the player's side won, 2 the other. A versus bout sets the loser's
`progress_to_victory_score` to -100000 and the winner's to 100000, so the weighted draw of `contest_events.0802` (sent now, one day
later) can only pick the winner; `ck3xbl_tourney.0005` restores both scores two days later. A grand melee sets the player's
`progress_to_victory` to 100000 (won) or -100000 (lost), so the team draw at the contest's end follows. No winner, or the waiting
event's second option, leaves the bout to CK3 (`contest_events.0802` as vanilla would have sent it). Prizes, glory, injuries and
the bracket stay CK3's.

## 30. ADDENDUM — a Bannerlord duel goes straight to CK3's aftermath (2026-10-18)

Before, `ck3xbl_duel_apply_effect` ran the vanilla setup again with `scope:fixed` set, so CK3 fought its rounds once more (the
player picking moves until sudden death) before its aftermath. `tools/gen/duel/single_combat_hook.mjs` now also writes
`ck3xbl_sc_decided_effect`: the same vanilla tail, but where it starts round one (`scope:sc_defender = { trigger_event =
single_combat.0001 }`) it sets `scope:sc_defender.var:success_threshold` to -100000 and sends `single_combat.0021` (the round end) to
`scope:sc_attacker`. The round end's success check then passes at once for the side `scope:fixed` lets win, and vanilla's
finalisation follows: the victory and defeat events, wounds and death by `scope:fatality`, and the output event of whatever started
the duel (prestige, opinions, the stakes of a challenge). A duel without a Bannerlord winner still runs CK3's rounds.


## 31. Memory guard (battle size by free memory)

Loading a field battle commits memory for the scene and for every agent on the field. When Windows has no commit left (RAM plus
page file, with CK3, Bannerlord and every other program counted), the allocation fails and Bannerlord crashes with an access
violation as the battle starts (seen: 1000 agents with Open Source Armory, 3.8 GB of a 48 GB commit limit left). Trimming CK3's
working set (§24) frees RAM but not commit.

Before each request the bridge reads the free commit (`GlobalMemoryStatusEx`, `ullAvailPageFile`) and, when it would not hold
`battleSize`, uses the largest Bannerlord field battle size (200, 300, 400, 500, 600, 800, 1000) that does: about 2000 MB for the
scene and 6 MB per agent, with `memoryReserveMb` (2000) left free. The armies are unchanged; fewer men stand on the field at once
and the rest come as reinforcements. The bridge log says what it did (`[memory] ... free commit`). `"memoryGuard": false` in the
`battle` section turns it off; a larger page file keeps the full battle size.
**Above 1000 (2026-10-19).** `battleSize` may be up to 1500 for field battles. Bannerlord's spawn logic caps its battle size at
half the engine's agent limit (2040 / 2: every troop might bring a horse), so after opening a field mission the module sets the
spawn logic's `_battleSize` itself (`RuntimeSettings.RaiseFieldBattleSize`): `RequestRules.FieldTroopsFor` = the requested size, at
most 1500, and troops plus mounts within 1550 agents (by the share of riders in the request). Bench: 1493 troops fought without a
fault; 1750 and 1971 froze the engine's native side as the battle began, and in live play (b305, 2026-10-20) 1500 froze 40 s into
the fighting. The default stays 1000; more is an experiment. A booted Bannerlord silent for its heartbeat limit plus `SilentKill`
(60 s) is killed and relaunched even when its window still answers (b305's frozen game had kept the next hour's battles from
Bannerlord). Sieges and battles at sea keep Bannerlord's own sizes.
`maxAgentsTotal` (1000 by default) must be raised with it, or the whole battle already fits on a field of 1000; 1500 agents need
about 11 GB of free commit.
Bannerlord keeps much of the memory of every battle it has fought (seen: 15.5 GB committed by the idle game after three battles,
next to CK3's 16.8 GB). So when the idle Bannerlord is trimmed (§28) and still commits more than `recycleAboveMb` (8000, in the
`bannerlord` section; 0 = never), the bridge quits it with the `quit` command and starts a fresh one at once, hidden and idle. A
Bannerlord that does not quit is left running; it is never killed for this.

## 32. Battle feats and the player's own wounds

**Wounds.** The CK3 player who fights in person is wounded by his own fight, not by CK3's odds: a fall in Bannerlord (killed or
knocked out) is always a wound, and with 10 % (seeded by bid and tag) a severe one, fate 5 = two wound levels at once
(`increase_wounds_no_death_effect` twice); ending the battle on his feet under 10 % health is a wound too. The module reports
`heroes[].health` (0..1 of the full pool) for heroes still on the field. With `playerDeathIsReal` a fall keeps CK3's odds. Fate 5
ranks between a wound and a maiming in the keypad's character slots and counts as neither capture nor death for later clashes.

**Feats.** For a decided field clash he fought in person (not a duel or a skirmish), the outcome carries the player's deeds:
kills, won, defending, enemy:own CK3 strength, terrain class (rough = hills, mountains, wetlands; forest = forest, taiga, jungle;
desert = desert, drylands, oasis, desert mountains), winter, whether he downed an enemy commander himself, and whether the battle
wounded him. The bridge counts them per player character in `feats.json` in the shared folder (each battle id once) and sends the
earned feats in r7 = prestige bonus (0..225) + 226 × mask:

| bit | feat | rule | CK3 |
|---|---|---|---|
| 1 | Reaper | 50+ kills in the battle | modifier `ck3xbl_feat_reaper` 10 years (+2 prowess, +10 dread) |
| 2 | Carnage | 100+ kills in the battle | `ck3xbl_feat_carnage` 15 years (+3 prowess, +20 dread, +1 prestige/month), +200 prestige; replaces Reaper |
| 4 | Living Legend | 1000 kills in his life | `ck3xbl_feat_legend` for life (+3 prowess, +1 prestige/month) |
| 8 | Slayer of Commanders | downed an enemy commander himself | +150 prestige, `ck3xbl_feat_slayer` 5 years |
| 16 | Unyielding Defender | 3 defensive wins against a larger enemy | trait `unyielding_defender` |
| 32 | Aggressive Attacker | 5 wins in attack | trait `aggressive_attacker` |
| 64 | Miracle | a win against twice his numbers or more | +300 prestige, `ck3xbl_feat_miracle` 10 years (+1 prestige/month, +5 vassal opinion) |
| 128 | Invulnerable | 10 battles in a row without a wound | `ck3xbl_feat_invulnerable` 10 years (+0.5 health) |
| 256 | Veteran's Scars | 5 battles that wounded him | `ck3xbl_feat_scars` for life (+2 prowess, -5 attraction opinion) |
| 512 | Winter Soldier | 3 winter wins | trait `winter_soldier` |
| 1024-2048 | terrain | 3 wins on that terrain: 1 rough, 2 forest, 3 desert | `rough_terrain_expert`, `forest_fighter`, `desert_warrior` |

A count that stays over its threshold sends its bit again: a trait the player has then gains 20 experience on its track, a
modifier is renewed. Each feat shows a toast. The mod decodes r7 in `ck3xbl_apply_summary_effect` and applies the feats in
`ck3xbl_apply_feats_effect` (log record `RES|FEATS`). `"feats": false` in the `battle` section turns the feats off.
## 33. Cleave and the AI level of heroes

A hero whose CK3 prowess is over 30 (the player's own hero too) carries `heroes[].buffs.cleave`: his melee swings cut through the
man they hit into the next one, as the "cut through" mods do. `CK3AgentApplyDamageModel.DecideWeaponCollisionReaction` turns
the reaction of an unblocked, unparried swing that strikes a man into `SlicedThrough`, and `CalculateRemainingMomentum` keeps
at least 75 % of the momentum for the next man. Blocks, parries, thrusts, missiles and horse charges keep the stock reaction.

The AI level of heroes (§28) is now 0.25 + 0.025 × prowess, the best AI from prowess 30 (it was 0.3 + 0.035 × prowess, the best
from 20): a prowess 14 knight parried like a champion and a prowess 38 player needed two minutes to beat him in the lists.

The free commit of the memory guard (§31) counts the room a page file may still grow: up to max(3 × RAM, 4 GB) for a page file
Windows manages, up to its maximum for a sized one, within the drive's free space.
## 34. Fugitive heroes and trophies

The winner's pursuit of fleeing men was already CK3's (the casualty model: the winner's pursuit against the loser's screen).
Heroes of the losing side who fled the field (result state "routed") no longer escape untouched: each is caught with 20 % + 50 %
of the pursuit share (at most 90 %); a caught hero is taken prisoner in a war (else wounded), and one who escapes capture is still
wounded with half that chance. Nobody is named as the killer. The CK3 player is never taken prisoner by a rout, only wounded.

Trophies: when the player struck a character down himself (the fate slot's killer is the player), the mod may hand him one of
the victim's personal artifacts (inventory, never court artifacts): 60 % from a man killed or taken prisoner, 25 % from one
wounded or maimed (`ck3xbl_res_trophy_effect`, before the fate, so a dead man's artifact is not inherited first).

## 35. Men-at-arms with a look of their own

`ck3xbl_costumes.json` carries looks keyed `maa:<CK3 MaA key>` for the men-at-arms with a culture of their own (huscarls,
varangians, cataphracts, kheshig, mubarizun, longbowmen, samurai and 33 more, `UNIQUE_LOOKS` in build_costumes.mjs). A
regiment of them wears its own look instead of its culture's (`CostumePlanner.LookOf`); its words weigh 6 times, not 2.5.

## 36. Tournaments: joust and archery

Two more pivotal moments offer the Bannerlord option: the joust (contest_events.0860, tourney code 4, a bout resolved by
contest_events.0802 like the single combat) and the archery contest (contest_events.0710, code 5, decided at the contest's end
like the grand melee). The joust is fought in the arena on tournament horses with a blunt lance (`empire_lance_1_t3_blunt`) and
the jousting shield. The archery contest is fought at the archery set of the aserai, battania or empire arena (SandBox prefab
`tournament_archery_set`, 13 pots): the player gets a hunting bow and two quivers of tournament arrows and has 120 s to hit
`tourneyPar` different pots, 4 + the best other archer's prowess / 3 (4..12). The rival stands by. Hitting the mark wins at once;
time up or no arrow left loses; the loser fades out of the lists, so the mission ends as a duel does. In CK3 a win sets the
player's progress to victory to 100000, a loss to 0. `ck3xbl_tourney_bout_trigger` names the bout codes (1, 2, 4).

## 37. Bandit hideouts

A skirmish with bandits (kind 1) or deserters (2) is fought at the band's camp, one of SandBox's hideout scenes picked by CK3
terrain (forest, mountain, desert, steppe; sea bandits on a coast) and bid; the woods stay as `fallbackScene`. Kind "hideout":
`CK3HideoutController` spawns the party at the attackers' spot (tags attacker_infantry / attacker_archer), the band over the camp's
posts (npc_wait, sp_guard_with_spear) and its leader at the boss spot (hideout_boss_fight). The band idles until the party comes
within 35 m or 25 s have passed, then both sides charge. An assassin, soldiers and zealots still meet you on the road.
## 38. Battle gear among tournament prizes

The winner of a martial contest (archery, horse race, joust, single combat, wrestling, grand melee; each member of the winning
melee team) gets, with 30 %, a piece of battle gear that fits the contest besides vanilla's rewards: a bow for archery, a horse for
the race, a horse or a main weapon for the joust, a weapon for the single combat, armour for wrestling and the melee (the
armory's own-culture rolls, `ck3xbl_tourney_prize_effect`). AI winners too. The hook: vanilla's
`tournament_winner_prestige_rewards_effect` and `tournament_team_winner_prestige_rewards_effect`, copied from the install into
`zz_ck3xbl_gen_tourney_prize_hooks.txt` with the call at their end (tools/gen/tourney/prize_hooks.mjs); recital and board game
winners get nothing more.

## 39. Storming the walls

**CK3.** The decision `ck3xbl_storm_walls_decision` is shown while an army the player commands or owns is in a siege
(`is_army_in_siege`). It runs `ck3xbl_siege_begin_effect`: B|BEGIN with `ck3xbl_kind = 4` and the siege facts
`ck3xbl_sg_fort` (the province's fort level, `ck3xbl_fort_level_value`), `ck3xbl_sg_garrison` (`ck3xbl_garrison_value`),
`ck3xbl_sg_days` (today minus `ck3xbl_enter_day`, set on the army when it enters a province) and `ck3xbl_sg_men`; the besiegers
are the attacker (army A records, regiments, commander, knights, the player C record), the defenders a synthetic garrison army
(tag 900002 + 2 × bid) with the holder when he is in the province. Event 0002 waits for the keypad like a field battle; 0004
applies the result (`ck3xbl_siege_apply_effect`, the province saved as `scope:ck3xbl_sg_prov`). CK3 script cannot read a siege's
progress, end a siege or occupy a province, so a won assault breaks the walls instead: province modifier `ck3xbl_walls_stormed`
for 30 days (fort level −100, garrison −99 %) and on the army owner `ck3xbl_storming_party` for 10 days (siege morale loss +1000,
siege phase time −90 %). CK3 then completes the siege within a day or two. A lost assault leaves the siege as it was; the
casualties of both sides apply as after a battle. Kind 4 is never steered by the battle clock.

**Bridge.** `SiegePlanner` estimates the progress with CK3's own rule (NSiege): morale 100 + 75 × fort level, a loss of 1 a day
(at least 0.5, +0.01 per 200 besiegers beyond the garrison), × 0.7 per siege tier the besiegers' engines lack against the fort
(thresholds 4, 6, 11, 16, 31), × 1.25 for siege events. The request gets `kind = "siege"`, a castle scene of the defenders'
Bannerlord culture (a town for a city or temple, Sturgia's for tribal, nomad and herder holdings; picked by bid; the field stays as
`fallbackScene`), and `siege`:

| progress | attackers' works |
| --- | --- |
| < 0.2 | ladders only |
| ≥ 0.2 | a siege tower |
| ≥ 0.4 | + a ram |
| ≥ 0.6 | + a second tower |
| ≥ 0.8 | + the first wall breached (`wallHitpointPercentages` [0, 1]) |

One ranged engine per siege regiment, the strongest first, at most 4: onager → Onager, mangonel → Catapult, trebuchet →
Trebuchet, bombard and cannon → FireTrebuchet, ballista (and elephant) → Ballista, rocket carts → FireOnager; siege_tower and
cloud_ladder regiments bring a tower each (2 at most). `sceneUpgradeLevel`: fort 1–2 → 1, 3–5 → 2, 6+ → 3; the defenders man
nothing, a Ballista, or a Ballista and a Catapult by level. The strength balance does not run.

**Module.** Mission "CK3Siege": the behaviours of `BannerlordMissions.OpenSiegeMissionWithDeployment` (v1.4.8) with the CK3
suppliers and captains, scene levels `level_N siege`, engines by `DefaultSiegeEngineTypes` property name
(`MissionSiegeWeapon.CreateDefaultWeapon`); views are the stock custom siege views plus the CK3 presenter. A scene that is not a
siege map, or missing views, falls back to the field battle on `fallbackScene`.

## 40. Relief: at the walls or at the camp

**CK3.** `ck3xbl_save_relief_effect` (in the snapshot, before B|BEGIN) sets `ck3xbl_relief = 1` on the first clash of a battle
fought in a province whose holding belongs to the player or a vassal (`province_owner` is the player or `target_is_liege_or_above`
him) while an army of an outsider besieges it there, with the siege facts `ck3xbl_sg_fort`, `_garrison`, `_days` (of the largest
besieging army) and `_men`. Event 0001 then offers two more options: d "We break into the holding and hold the walls!"
(CHOICE|FIELD_DEFEND) and e "We fall on the besiegers' camp!" (CHOICE|FIELD_CAMP); both take the field like a and c
(`ck3xbl_take_field_effect`). The battle stays an ordinary CK3 combat, steered by the battle clock as any other.

**Bridge.** The orchestrator keeps the mode (`Battle.Mode`, `ReliefModeOf`) and plans the battle again when the choice is not the
field (the preloaded field request is cancelled; the new one is `b<bid>_<session>_<mode>`). `MapperOptions.ReliefMode`:

- *defend*: kind `siege` on a castle or town of the player's side's culture, the besiegers' works as in §39 from their siege's
  estimated progress. Breaking in costs `SiegePlanner.BreakInLoss`: 10 % against a besieger half the host's size or less, up to
  20 % against twice its size, times (1 − martial / 60, martial capped at 30), martial of the side's CK3 commander (else the
  player). Each troop group sends that much fewer agents; `siege.breakIn` records them, and the outcome counts them as killed
  agents of their group. The garrison (`ck3xbl_sg_garrison` men, group `garrison#<900002 + 2 × bid>`, troops of the side's
  levies) fights with the player; no CK3 army answers for it, so the outcome leaves it out. `siege.playerDefends = true`: after
  the looks are set (`AppearanceProvider.Enrich` pairs the snapshot's sides with the request's), `SiegeSides.Settle` swaps the
  request's sides when CK3 had the player attack, so he is the defender in Bannerlord (`siege.sidesSwapped`);
  `SiegeSides.AsCk3` swaps request and result back before the outcome is built. The summary adds how many fell breaking in.
- *camp*: a field battle (the custom battle siege scenes carry no `sp_relief_force_attack_set` and no `sally_out_set`, so
  Bannerlord's relief force attack cannot be used): the besiegers start with initial morale −20 (caught in their camp) and the
  garrison sallies out on the player's side.

The strength balance does not run for either.

## 41. The fall of a stormed holding, siege feats

**Sack.** A won assault leaves `ck3xbl_storming_party` on the player. When CK3 then completes the siege (`on_siege_completion`,
root = the occupant, `scope:county`), `ck3xbl_on_siege_completion` removes the modifier and opens ck3xbl_siege.0010 "The Holding
Has Fallen":

| option | effects |
| --- | --- |
| a "Let the men take what they want." | gold 30 + 6 × development, dread +15, county development −3, county modifier `ck3xbl_sacked_modifier` 10 years (opinion −20, development growth −50 %, tax −25 %), piety −75 against your own faith; stress for compassionate, just, forgiving |
| b "Let the townsfolk buy their safety." | gold 15 + 3 × development, prestige +25, `ck3xbl_ransomed_modifier` 5 years (opinion −10); stress for compassionate |
| c "Spare the town." | prestige +75, `ck3xbl_spared_modifier` 10 years (opinion +15), piety +50 for your own faith; stress for greedy, sadistic, wrathful |

CK3's own siege loot and occupation come on top.

**Feats** (counted in CK3; the feat mask of r7 has no bits left, §32). *First on the Wall*: a won assault the player fought in
person (`scope:ck3xbl_player_present`): +100 prestige, `ck3xbl_feat_first_on_wall` 10 years (prowess +2, siege phase time −10 %,
prestige +0.5 a month); the third (`var:ck3xbl_storms_won`) gives `military_engineer` (else 20 trait experience). *Unbroken
Walls*: a won defence of the walls (`var:ck3xbl_wall_defence_bid`, set by option d, checked by `ck3xbl_unbroken_walls_effect`
after the apply in ck3xbl_battle.0004): +100 prestige, `ck3xbl_feat_unbroken_walls` 10 years (defender advantage +3, prestige
+0.5 a month); the third (`var:ck3xbl_walls_held`) gives `unyielding_defender` (else 20 trait experience).
## 42. Acclaimed knights lead a formation

An acclaimed knight (active accolade) whose primary attribute, else his strongest, is linked to a men-at-arms type leads every
troop of his side (not heroes) in that type's Bannerlord formation, like a Bannerlord captain: `heroes[].aura`
(`KnightBuffPlanner.AuraFor`), both sides alike. Strength 2.5 % per accolade rank (15 % at rank 6):

| linked type | formation | effect (s = 0.025 × rank) |
| --- | --- | --- |
| archers (archer, crossbowmen), gunpowder | Ranged | missiles × (1 + s), inaccuracy × (1 − s) |
| archer cavalry (horse archer) | HorseArcher | missiles × (1 + s), inaccuracy × (1 − s) |
| skirmishers | Ranged | missiles × (1 + s), melee × (1 + s/2) |
| pikemen, heavy infantry (vanguard) | Infantry | melee × (1 + s), damage taken × (1 − s/2) |
| heavy cavalry (lancer) | Cavalry | melee × (1 + s), charge × (1 + 1.5 s) |
| light, camel, elephant cavalry | Cavalry | melee × (1 + s), charge × (1 + s) |

Troops that take the field while he stands get initial morale +2 × rank. The module (`CK3BuffRegistry`) matches a troop by its
formation's base class (`FormationClass.FallbackClass`, else its kind's); several knights over one formation give the strongest
of each effect, not the product (`BuffMath.Strongest`). When the knight leaves the field (killed, knocked out, fled) his aura ends
and the formation's troops lose `moraleLoss` (2 × rank, at least 5) morale, their aim back to their own.

## 43. War elephants and more armour packs

**Elephants.** Burning Empires (Nexus 7750) ships war elephants in its module `BurningEmpires_Elephants`, which depends on the
whole conversion; that conversion cannot be loaded for the bridge (its XSLTs remove the stock custom battle scenes the sieges and
fields are fought on, and change cultures and items). So before every launch `ElephantModule.Ensure` builds `CK3Bridge.Elephants`
on the player's machine from the player's own copy: the asset package (hard link, else copy), sounds, prefab, `action_sets`,
`action_types`, `monster_usage_sets` (+xslt), `module_sounds`, the monster, and the horse and harness items with their Burning
Empires cultures removed (no troops, no cultures). It depends on the stock modules only, is rebuilt when its source or
`ElephantModule.Generation` changes, and is never shipped. `BannerlordLink.LoadsElephants` (the running game loads it) sets
`MapperOptions.Elephants`.

With it, every regiment whose CK3 base type is `elephant_cavalry` (war elephants, the elephantiers accolade, ballista elephants)
rides: `troops[].mount = "elephant"`, `troops[].mountHarness` (elephantiers `be_elephant_armor_tier_3`, war elephants
`be_elephant_armor_kushan_tier_2`, others `be_elephant_armor_sassanid_tier_1`), one elephant per 4 agents the group would have
fielded (`ElephantPlanner.MenPerElephant`; 500 hit points, 400 charge). The module (`MountedTroopFactory`) creates one battle
character per troop, mount and harness through the XML path of heroes (culture, level, skills, face range, every battle
equipment set with the horse slots replaced) and releases it with the heroes; a mount the game did not load leaves the troop on
its horse. The elephants' trunk attacks and sounds belong to the Burning Empires DLL and are not there: elephants charge.

**Armour.** `bannerlord.armoryModules` also loads, in this order, `BL.Terra_Armarium.UniversalCore`, `.Byzantine`, `.Rus&Mongols`,
`.LateMedieval` (Nexus 7165, free to use with credit) and `CK3Bridge.Elephants`. `ArmoryCompat.Prepare` renames an item module's
`Shaders` folder `Shaders.ck3xbl_off` (caches built for another game version crash v1.4.8) and gives Terra Armarium's
campaign-only item nodes `CustomGame` (original kept as `SubModule.xml.ck3xbl_bak`). The costume catalog includes Terra's items
(`OPTIONAL_MODULES`; its `Culture.strugia` read as sturgia).

**Status (2026-10-19): on.** The first build crashed Bannerlord at the first elephant's spawn: the module lacked
ModuleData/project.mbproj, which tells the engine to load its action sets, action types, monster usages and sounds, so the elephant
had no action set. Generation 2 copies it; the bench (a request with elephant riders, every armour pack loaded) fights two minutes
without a fault. Bannerlord's OK-only `Mod change detected` notice, shown whenever the module list changes, is acknowledged by
the bridge (SafeModeDialog.TryAcknowledgeModChange).

## 44. Battles at sea and landings (War Sails)

**When.** CK3 armies never fight at sea; the mod lets two hosts at war meet there. The decision `ck3xbl_sea_battle_decision` is
shown while an army the player commands or owns is at sea (`is_sea_province`) with an enemy army (owner at war with the player or
his top liege, not in combat, no `ck3xbl_sea_tried`) in the same or a neighbouring sea province. On `on_army_enter_province` into a
sea province (once per army per 15 days, `ck3xbl_sea_sighted`): the player's fleet near an enemy one gets the sighting event
`ck3xbl_sea.0010` (attack or sail on); an AI fleet at war with the player that comes near his fleet, and is at least four fifths
its size, bears down half the time (`ck3xbl_sea.0011`: give battle or run). Whoever is attacked may slip away first
(`ck3xbl_sea_evade_effect`): 35 % + 4 % per point of martial its commander (else owner) has over the chaser's, 5..75 %. Both fleets
of a fought battle carry `ck3xbl_sea_tried` for 30 days.

**Snapshot.** `ck3xbl_sea_begin_effect`: `B|BEGIN` with `ck3xbl_kind = 5`, the site is the defenders' sea province, both sides as
real armies (S, A with regiments, AX, commander, knights; the player on his own side), then `CHOICE|FIELD` and the waiting event
`ck3xbl_sea.0002`; `ck3xbl_sea.0004` applies the result with `ck3xbl_apply_effect` (no CK3 combat: payload 3, no steering, as an
assault). A landing is a field battle whose attackers came straight off the sea: `LV` carries `ck3xbl_l_landing = 1` when the
attacker origin (`ck3xbl_l_origin`) is a sea province.

**Bridge.** `BattleSnapshot.IsSeaBattle` (kind 5) and `.Landing`. With `MapperOptions.Naval` (the running Bannerlord loads
`CK3Bridge.Naval`) and neither a duel, skirmish nor siege, `BattleMapper.ShapeNaval` makes the request `kind = "naval"` with
`naval` (`NavalInfo`): `raid` (a landing), `attackerShips` / `defenderShips` (War Sails hull ids), `windStrength` 0.3..0.7 by bid,
`windDirection` `Random`, `terrain` (`CoastalSea`, `OpenSea` off a non-coastal sea, `River` for a landing). `NavalPlanner.Fleet`
fills a side's agents into its naval tradition's hulls in turn (heavy, medium, light; Norse heritage sails `nord` drakkars, else
the side's Bannerlord culture, else `vlandia`) up to `MaxShips` = 8; a landing takes shallow-water hulls only, the largest first,
and at most `RaidMaxShips` = 3 (live b298 crashed with eight ships in `OnAgentBuild`; on the bench 2026-10-20 four crashed in
`OnDeploymentFinished`, three landed and fought), and the defenders
stand ashore. Scenes: one of 12 coastal seas (or `battle_terrain_opensea_northern`) by bid, a raided village of the defenders'
culture for a landing; `fallbackScene` keeps the land field. In the outcome of a battle at sea (not a landing),
`NavalPlanner.DrownedShareOfFled` = half of the loser's fled agents count as killed. Without War Sails a battle at sea is fought on
the coast.

**Module.** `CK3Bridge.Naval` (bannerlord_module/CK3Bridge.Naval, depends on NavalDLC and CK3Bridge) sets `CK3Missions.NavalOpener`.
For CustomGame it adds the War Sails ship, deployment and combat models (ours wrap them and call `CK3CombatHooks`, so the CK3
buffs hold at sea; `CK3BridgeSubModule.NavalModelsRegistered` makes CK3Bridge skip its own stat and damage models), registers the
ship types and loads `ShipUpgradePieces`, `ShipSlots`, `ShipHulls`, `ShipPhysicsReferences`, `MissionShips`. Missions `CK3Naval` /
`CK3NavalRaid` replicate the DLC's naval custom battle and raid behaviours with our spawn order, heraldry and hand-off view. The
release ships it under `app/naval/CK3Bridge.Naval` (built only where War Sails is installed); `NavalModule.Ensure` copies it into
Modules when NavalDLC is present, and `NavalModule.WithNaval` adds SandBox, StoryMode, NavalDLC before CK3Bridge and
CK3Bridge.Naval after it.

**Status (2026-10-19): on.** The first bench run froze at the ships' deployment plan: the custom battle's initialization model reads
the land battle's spawn logic, which a naval mission lacks (`CK3NavalBattleInitializationModel` answers "no order of battle" at
sea and keeps the stock answer on land). Then the bench fought a battle at sea (3 minutes, both fleets boarding), a landing
(2.5 minutes), a land battle with War Sails loaded, and a battle at sea with every armour pack and the elephants loaded, all without a
fault. `bannerlord.navalModules` defaults to true. Bannerlord's `Mod change detected` notice has a single OK button with the id
IDCANCEL; `SafeModeDialog.TryAcknowledgeModChange` now presses the dialog's only button whatever its id.

## 45. Feats for life, in three ranks

**Ranks.** Every feat modifier (`common/modifiers/ck3xbl_feat_modifiers.txt`) is for life and has three ranks: `ck3xbl_feat_<feat>`
(rank I, the name older saves already hold), `_2`, `_3`. `ck3xbl_feat_rank_effect = { FEAT = <feat> }` raises the rank kept in the
character variable `ck3xbl_fr_<feat>` (a modifier from an older save without it counts as rank I), swaps the modifier and shows
the feat's toast; at rank III nothing more happens. Trait feats (unyielding defender, aggressive attacker, winter soldier, terrain
experts, military engineer) keep their trait and trait experience.

**Feats and when they rise.**

| Feat | Earned |
|---|---|
| reaper, carnage, bloody_harvest | 50 / 100 / 250 kills of his own in one battle (carnage +200, bloody harvest +300 prestige) |
| legend | crossing 1000, 2500, 5000 kills in a lifetime |
| slayer | an enemy commander downed by his hand (+150 prestige) |
| knight_hunter | 5 enemy heroes downed by his hand in one battle |
| miracle | a win against twice his numbers (+300 prestige) |
| last_stand | a defensive win against three times his numbers (+250 prestige) |
| invulnerable | 10 battles in a row without a wound |
| scars | crossing 5, 10, 20 battles that wounded him |
| veteran | crossing 25, 50, 100 battles fought in person |
| sea_wolf | crossing 3, 6, 10 battles at sea won |
| first_to_board | a battle at sea won with 10+ kills of his own |
| coast_terror | crossing 5, 10, 20 landings won as the attacker |
| first_on_wall, unbroken_walls | every stormed holding / held wall defence (§41) |
| unbeaten_blade | every fifth duel won in a row in Bannerlord (a lost duel breaks the streak) |
| champion_slayer | a duel won against ten or more prowess over his own |
| merciful | a beaten foe spared in a duel that could kill (fatality `possible`, the loser did not fall dead) |
| tourney_champion | the 3rd, 6th and 10th tournament bout won in Bannerlord |
| joust_master | every fifth joust won in a row |
| marksman | an archery contest won |

**Wire.** The battle feats come from the bridge's feat ledger (§32). The first twelve bits ride in r7 as before; bits 12..18
(last stand, knight hunter, bloody harvest, veteran, sea wolf, first to board, terror of the coasts) ride in r6 above the player's
kills: r6 = min(kills, 999) + 1000 × (mask >> 12). The mod reads `ck3xbl_res_my_kills` as r6 modulo 1000 and runs
`ck3xbl_apply_feats2_effect` on the rest. Duel and tournament feats are counted by the mod (`ck3xbl_duel_streak`,
`ck3xbl_tourney_wins`, `ck3xbl_joust_streak`). The lint expands a name built from a scripted effect's parameter
(`ck3xbl_feat_$FEAT$_2`) with every value the effect is called with.

## 46. A harder assault, and a delivery that does not give up

**Garrison.** In an assault (§39) the garrison (the defenders' levy army, tag 900002 + 2 × bid) are the holding's soldiers, not
village levies: `TroopResolver.ResolveGarrison` gives heavy foot (the `armored_footmen` ladder) at `SiegePlanner.GarrisonTier`
= 2 + 0.5 × fort level (2.5 .. 5) and 45 % archers (the `bowmen` ladder) half a tier below. Their number stays the CK3 garrison.

**Walls.** The defenders' side buffs gain `SiegePlanner.WallBonus(fort)`: damage × (1 + 0.04 × fort, at most 1.4), damage taken
× (1 − 0.03 × fort, at least 0.7), initial morale + 4 × fort (at most 30).

**Waves.** `siege.attackerWaveRatio` = 2.5: the module's `CK3SiegeSpawnHandler` replaces Bannerlord's `CustomSiegeMissionSpawnHandler`,
cuts the spawn logic's battle size to the garrison × (1 + ratio) (at least 200), weights the defenders' share with
`DefenderAdvantageFactor` = attackers / (defenders × ratio) so the whole garrison stands on the walls, and keeps Bannerlord's wave
reinforcements (50 %, every 3 s). A host five times the garrison now climbs the walls 2.5 to 1, wave after wave. Sally-outs and
relief battles keep Bannerlord's own split.

**Delivery.** A refused keypad delivery (IN|BAD, or no IN within the timeout) is typed again up to three times, each slower (key
delay × 1, × 2, × 4), and a failed delivery is tried again after 3 s while the orchestrator's attempts last (`MaxDeliveryAttempts`).
CK3 losing the foreground spends no attempt: the result waits for CK3 to be in front again, however often. Live b288 and b299 lost
keys (1 to 22 of 23 and 86 registers) because CK3's working set had been trimmed for the battle (10 GB to 0.6 GB, §24) and CK3 paged
it back in for a long while after the return (also the long black screen): CK3 is now trimmed only when less than
`TrimCk3BelowFreeRamMb` (6000 MB) of RAM is free, and a trimmed CK3 is first given time to page back in (its working set stops
growing, at most 20 s) before the result is typed. While the input hooks are installed, the bridge's garbage collector runs in
`SustainedLowLatency` mode, so a blocking collection never holds the managed hook callbacks and with them the cursor.

## 47. Prowess past 40: soft caps and the duel gap

Every prowess factor of a hero (§28, §33) used to stop early: weapon skills at 330 (prowess 34), damage at x2 (prowess 40), the
health pool at x3 (prowess 50), on top of a hero cap of x2 dealt and x4 durability. With universal weapon slots (§16) prowess goes
far past that, and live b322 (prowess 88 against 29) was fought as between near equals. Now:

- damage `HeroPlanner.ProwessDamage`: 1 + 0.025 P to x2 at 40, then 0.0125 per point to x2.75 at 100;
- health `ProwessHealth`: 1 + P / 25 to x5 at 100 (times the CK3 health factor);
- skills `WeaponSkill`: 60 + 8 P to 330 at 34, then 1 per point to 400 (the module's `MaxSkill`);
- hero caps `KnightBuffPlanner.HeroDurabilityCap` 6 and `HeroDealtCap` 3; the module takes a health multiplier up to 6.

In a single combat or tournament bout (not archery) `BattleMapper.ApplyProwessGap` gives the duelist with more prowess +1 % melee
and ranged damage and 1 % less damage taken per point of the gap, at most 50 %, after the hero caps.

## 48. Bench findings, 2026-10-20

- **Landings.** War Sails' raid lands the player's own ships: with the player ashore and the AI landing, `NavalRaidMissionController`
  indexes its landing paths by ship and crashes. A landing the player defends is fought on the coast (`ShapeNaval` leaves it a field
  battle; the module falls back to the field scene as well). A landing the player makes takes at most three ships (§44).
- **Wounded at sea.** War Sails reports every downed man of a battle at sea or a raid as killed. The outcome counts
  `NavalPlanner.KilledShareOfDowned` (0.35, a land battle's share) of them dead and the rest unconscious before the CK3 conversion;
  the loser's drowned still come from his fled men.
- **Skills.** A trait's skill bonus clamped weapon skills to 330 and so undid the prowess soft cap (§47); it now never lowers a skill
  and caps at 400.
- **Exceptions.** The module logs the first-chance `IndexOutOfRange`, `NullReference`, `ArgumentOutOfRange`, `InvalidCast` and
  `KeyNotFound` exceptions with their stack to bannerlord.log (at most 20 a game): a crash leaves its cause in our log even when the
  watchdog waits for someone to answer its report dialog.
- **Battle size.** 1500 fought eight minutes on the bench without a fault (1506 agents); the live freeze of b305 was not reproduced.
  The default stays 1000.
- **Sieges (§46).** A first-day assault (ladders only) lost 191 men to the garrison's 12 in eight minutes; after a long siege (two
  towers, a ram) the besiegers took the walls in seven minutes for 197 men against 178.
