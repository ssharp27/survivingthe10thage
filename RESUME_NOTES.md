# RESUME NOTES — Surviving the 10th Age — end of 2026-10-26 session (CURRENT)

Session stopped on request (user 2026-10-26). Start with **M2_STATUS.md** (the authoritative M2 trail + pitfalls) and this block. The 2026-10-21 block and everything below is archive history.

## Where we are (2026-10-26 shutdown — CURRENT)
- HEAD = **Asper "Swarm of Bees" `babc000`** on `main`, **pushed to origin** (github.com/ssharp27/survivingthe10thage), tree clean.
- M2 feature trail (all committed+pushed, one user-driven feature at a time, smoke-gated): stat-tag registry `bd4367a` > evasion `8aea93b` > cards `509d48b` > HUD-order `96f44af` > camp-roster `cc7d31e` > down-grace `09de67b` > XP-suction `754bfc8` > leader bonuses `45387d2` > **ultimates `33c1283`** (R key, 120 s tag-gated cooldown) > Yu roster art `ccdea2f` (+ trail note `f0f5d8d`) > Mosiah "Bardic Inspiration" `e1f65cc` > Alvora "Guardian Awakening" `25b04bc` (+ notes refresh `0bb2ed8`) > **Asper "Swarm of Bees" `babc000`**. **Four ult lanes live** (Baldrun Giant's Might / Mosiah party-wide +25% dmg & rate / Alvora ring x2 reach +50% dmg heal-per-kill / Asper 6 seeker bees, 10 s, 10 dps latch-on-one, compass-circle spawn fan, no Rng draws); Yu ult still deferred.
- M2 feature trail (all committed+pushed, one user-driven feature at a time, smoke-gated): stat-tag registry `bd4367a` > evasion `8aea93b` > cards `509d48b` > HUD-order `96f44af` > camp-roster `cc7d31e` > down-grace `09de67b` > XP-suction `754bfc8` > leader bonuses `45387d2` > **ultimates `33c1283`** (R key, 120 s tag-gated cooldown; Baldrun "Giant's Might" the only lane so far; 10 s duration is the assistant pick, documented on the trail; other four PCs' ults deferred).
- Beat loop lock (user 2026-10-23, do not re-litigate): 30 ally I | 60 tier-2 gate | 120 mini-boss I | 150 ally II | 180 boss I | 210 ally III | 240 mini-boss II | 270 ally IV | 360 final boss | 420 VICTORY; roster 5/5 one-PC-at-a-time; picker = one track/member + 1 wild `pick_1..pick_6`.
- Gate contract NOW: VICTORY + **17** `stat tag` audit rows (registry grew 16→17 with tag 16 `ultimate cooldown`, utility multiplier — gate = `ULT_CD_BASE 120 s / stat` inside PartyMember, reserved for future meta progression) + 0-byte `.err` (measure via `.Length`). Smokes strictly SEQUENTIAL, background; never two Godot processes on the project dir.
- Latest gates (smokeM8 trio): zx31 (Baldrun leads) VICTORY lv15/xp543 (`ult 2 cd 15`, Giant's Might regression, NO bee prints), zx32 (`M1_LEADER=Asper`) VICTORY lv15/xp507 — 2× `(ult) Swarm of Bees` + dispersal tallies (`0 prey drained` honest empty case at the mini-boss arena, `88 prey drained` in the tier-4 stampede, heuristic latch-break attribution), zx33 (`M1_LEADER=Yu`) VICTORY lv16/xp578 (`ult 0 cd 0` silence, spirit lane `healed 2`). All camp-skip, **17** rows, 0-byte `.err`. NOTE: the trio's first background attempt got harness-killed between gates after zx31 finished; remaining zx32/33 re-run via smokeM8b.ps1 (timeout 0) — tasklist proved no godot survived. Earlier trios zx1..zx30 (incl. awakening `25b04bc`'s zx28/29/30) documented on the M2 trail.
- Human-only paths still owed (pilot cannot reach): pressing R / dash / picker keys in a real run, campfire UI interaction, and the Giant's Might ×2 visual actually looking right.
- Parked/unanswered: assistant proposed evidence runs for the **Mosiah pierce + Alvora regen** leader lanes (`M1_LEADER=Mosiah`/`Alvora`) — the leader-bonus trio covered chain/insects/spirits only; user never answered, so it stays parked.
- Helper scripts live in `C:\Users\Steve\AppData\Local\Temp\opencode` (untracked): `smokeM8.ps1`/`smokeM8b.ps1` = latest trio (bees) + retry of the killed back half, evidence grep `ult |Bardic|Giant|Awakening|Swarm|disperses|bonus|lb chain|leader seat` (`smokeM7.ps1` = awakening trio), `verify10.py` = BOM-aware verifier (17-row filter), `commitmsg9.txt` = last commit-message style ref, `splicePM6.py` = bee PartyMember git-splice (`splicePM5.py`/`splicePM4.py` = awakening/bardic), `beeGame.py` = Game.cs 4-insertion marker splice, `fixGame.py` = Game.cs marker-splice repair pattern.

## Next steps (2026-10-25 shutdown, in order)
1. Await the user's next feature / bug-fix request — features are user-driven, ONE at a time, with the established smoke gate.
2. Only if the user re-approves: pierce/regen leader-lane evidence runs (clone `smokeM4.ps1` with `M1_LEADER=Mosiah`/`Alvora`, verify `(ult)`-style counters + 17 rows).
3. Natural follow-ons (user gates, do not start unasked): Yu ultimate (Baldrun/Mosiah/Alvora/Asper landed), meta progression spending tag 16, bardic/awakening/bee buff-or-swarm visuals, drawn lightning VFX, insect/spirit/bee art.

## Splice + tooling discipline (proven, unchanged — details in M2_STATUS.md)
- **PartyMember.cs raw writes corrupt** (re-serialize ~12 interleaved copies): ONLY the git-splice route — python builds the full new content in a TEMP file → `git hash-object -w <tmp>` → `git update-index --cacheinfo "100644,<hash>,src/PartyMember.cs"` (no bare `--force`, ambiguous on this git) → `git checkout-index -f -- src/PartyMember.cs`. Game.cs python splices (`sub()` asserting `count == 1`) are safe direct writes — but brace-heavy MULTI-LINE C# payloads via the edit tool mangled Game.cs on 2026-10-26 (nested empty braces, a swallowed `c.kind = "dead";` line): route those through a marker-matched python splice and re-read after every edit.
- Build: `cmd /c 'set PATH=C:\Users\Steve\dev-tools\dotnet;%PATH%&& dotnet build survivingthe10thage.csproj --no-incremental -nologo'` — expect ONLY 3 pre-existing GodotMcpPlugin CS0618 warnings.
- Smoke exe: `"C:\AI Coding\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe" --path C:\GitHub\survivingthe10thage --headless --quit-after 120000`; env `M1_PILOT=1`, `M1_DEV_SKIP=camp|join`, bench valve `M1_LEADER=<name>` (flips the seat BEFORE `BuildJoinLottery()`); run a ps1 via `powershell -NoProfile -Command "Get-Content '<ps1>' -Raw | Invoke-Expression"`, background. PS `>` redirect is UTF-16 — verifiers must BOM-detect (utf-16-le else utf-8).
- PS 5.1: no `&&` (use `;`), `$` eaten at write positions (patch via python `chr(36)`), `git commit -m` breaks on quotes → `-F` file.
- Godot C# bindings are PascalCase (`Vector2.Normalized()`, `Scale`); `GetInstanceId()` returns **ulong** — `(long)` casts on both sides vs `Array<long>`.
- RNG discipline: seeded `Rng` (9001) drives combat rolls — ult/leader features shift downstream streams WITHOUT the feature consuming a roll (magnitudes change kill timing); `_joinRng` and death saves are unseeded (`GD.Randi()`) so run-to-run lv/xp drift is legitimate; join-order drift explains cross-trail lv/xp differences (zx21 15/548 vs its zx18 twin 16/552 is that variance, not a bug).

---

# Older history below — end of 2026-10-21 session (archive)

Session stopped on request, **M1 declared SUCCESS by the user (2026-10-21)**. Start with M1_STATUS.md (authoritative, passes 1–20) and that block. Everything below the 2026-10-21 block is older history (archive only).

## Where we are (2026-10-21 shutdown — archive)
- HEAD = **pass-20 `ea1f10c`** on `main`, **pushed to origin** — all 68 commits now live at `https://github.com/ssharp27/survivingthe10thage`.
- **M1 = SUCCESS**: full stage loop verified through VICTORY/TPK gates, squad model + death saves + campfire + join beat all shipped, and the art pass landed for BOTH party PCs (ahead of schedule — the art gate the old doc called for is done).
- 2026-10-21 chain (old→new): pass-14/15 motion language (party table-slide + topple, push-gated shuffle; enemies walk-bob) > pass-16 art audit + Baldrun wiring `d8e348f` > pass-17 dash-plate drop (`y+12`) + party Y-sort (band `4+rank`) `1648490` > pass-18 flash-lane split `0bfc042` > pass-19 Mosiah wiring `14d14fa` > pass-20 camp alignment `ea1f10c`.
- Art pipeline as it now stands: user-cleaned minis `art/baldrun.png` (98x118) + `art/mosiah.png` (79x119), both audit-passed (hard alpha, quantized palette, dead-centred) via the one-off `tools/artcheck.gd` (UNTRACKED — swap the path line to audit a new PNG; run `--headless --import` before auditing a fresh file). Wiring lives in `src/PartyMember.cs`: `displayName -> path` lookup, Sprite2D on the RIG at `miniScale 0.75`, `miniRim 7.0` pins the base-rim centre to the rig origin.
- Feel locks (user, 2026-10-21): party minis slide across the table and the bounce STOPS with a settle when movement stops; enemies keep the different verb (walk-bob). RED flash = damage only (`NoteHitFlash` from `Game.TakeHit`, both harm paths); SWING = ghost-white semi-transparent (`_atkFlash`, modulate alpha 0.55 — modulate only multiplies). Downed/eliminated = 90-degree topple around the base-rim pivot; rescue-target bodies stay tipped until the channel completes.
- Camp screen: pass-20 stack-budget fix (grid 3 cols/2 rows, cards stretch ~123 px, body 0.55..0.87 @ 0.75 over chip 0.90..0.99). **User deferred further alignment polish — "not perfect, another time"** — round 3 stays open, not M1 debt.
- Repo hygiene at shutdown: clean tree except untracked `.opencode/` + `tools/` (keep or drop `tools/artcheck.gd` at next session's discretion).
- Never run-verified (pilot cannot reach; human run needed): dash press, escort contact chips on the STRANDED recruit, live "STRANDED x/y" HUD strip, 25% join-HP floor, picker gating for eliminated members, DPI-scaled frame, escort-KILL joins.
- Pass-13 recap (2026-10-20, superseded by passes 14-20; full detail lives in M1_STATUS.md): the campfire data-truth fix — C# `Set("party_data", ...)` silently no-ops on a typed `Array[Dictionary]` export (untyped `Array` lands); `_refresh_bars` full re-render per refresh + `inner >= 0` fill guard; CT57-60 gates green.
- Run-summary DOWN at run end IS real data (Mosiah genuinely got eliminated in PLAY2 under boss-phase hordes) — user accepted, "not a big deal for M1".
- Pass-10 recap (rescue staging, escort crawl gate, join-banner SOFT pause, campfire freeze fix) fully absorbed into M1_STATUS.md passes 10-12c; human playtest of pass-10 never completed — the human-only paths re-listed above are still the open verification list.

## Next steps (2026-10-21 shutdown, in order)
1. (User-deferred, NOT M1 debt) camp alignment round 3 polish — pass-20 got the camp screen structurally right; user said "not perfect, another time" and will drive the rest.
2. Human playtest round for the pilot-unverifiable paths (dash, escort contact chip on STRANDED, STRANDED HUD strip, 25% join-HP floor, picker gate for eliminated members, DPI-scaled frame, escort-KILL joins).
3. Standing across ALL milestones (not started): 20-minute run curve workstream.
4. M2 scope starts with roster growth per DESIGN.md; boss spectacle is a later milestone.
(2026-10-20 shutdown block above is archive; its next-steps list is fully overtaken by the 2026-10-21 one.)

## Resume recipe (unchanged — details in M1_STATUS.md)
- Build: `& 'C:\Users\Steve\dev-tools\dotnet\dotnet.exe' build survivingthe10thage.csproj --no-incremental -nologo` (no `-o`; OutputPath baked to `.godot\mono\temp\bin\Debug\`).
- Smoke: `"C:\AI Coding\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe" --path C:\GitHub\survivingthe10thage --headless --quit-after 90000`, run via `cmd /c 'set M1_PILOT=1&& ... > file 2> file2'`, timeout 0, SEQUENTIALLY. Gate = VICTORY-or-TPK line + 0-byte `.err`. Pilot = verification only, never human play. `M1_REAPER=1` re-enables test bench.
- Campfire.gd is TAB-indented and pure ASCII (LF, no BOM) — the reliable edit path is PowerShell line-slice splices: `[System.IO.File]::ReadAllText` → `-split "\n"` → splice by line index → rejoin with LF → `[byte[]][char[]]` + `WriteAllBytes`, then verify with `Select-String` line dumps. NEVER put multi-line GDScript into the edit tool's oldString (generation corrupts tab-heavy GDScript payloads — happened twice on 2026-10-20 AND twice on 2026-10-21; all aborts left the file untouched). For single-value tweaks, a count-guarded PowerShell `-replace` (`([regex]::Matches($t,pat)).Count -eq 1` then `WriteAllBytes`) also works — verified on pass-20; always read back after. PowerShell here is 5.1: `[System.IO.File]::` (not `[IO.Encoding]`), double quotes inside double quotes are `""` (not `\"`), no `&&`, `chr()` does not exist.
- All 4.7 C#/GDScript API drifts catalogued in M1_STATUS.md (incl. pass-6 `Math`/`out` drifts).

---

# Older history below — end of 2026-10-18 session (archive)

Goal: multi-agent M1 pass (6 agents dispatched via `.opencode/agents/*.md`). Session ended on request; resume tomorrow from this file.

## How to resume
1. The game-developer run was **cancelled** overnight — its uncommitted edits (7 files, +637/−71) may be partial. On resume: first `git diff` vs the P1–P7 list to see what actually landed, then continue its session (`ses_eb0519a48ffePA80GmeNCOuAPp`) or re-dispatch a fresh game-developer carrying this file's work order. Its last smoke run (`prun_*`) result was never checked — treat all its intended changes as UNVERIFIED.
2. Re-run the smoke gate yourself if its completion is uncertain (recipe in M1_STATUS.md, plus: gate on the `VICTORY` line — `--quit-after` is a fragile frame budget, F5).
3. Then execute the "Next steps" list.

## Agent roster, models, outcomes (all dispatched)
| Agent | Model (in its frontmatter) | Session ID | State |
| --- | --- | --- | --- |
| game-developer | `strata/qwen3.8-flash-next-coder-iq1_m#high` | `ses_eb0519a48ffePA80GmeNCOuAPp` | ❌ **CANCELLED at shutdown** (was mid-work on P1–P7, mid smoke-verify) — on resume: continue this sessionID if possible, else re-dispatch fresh with the full work order from this file |
| game-tester (retry) | `opencode/deepseek-v4.1-flash-free#high` | `ses_eb0640d64ffeTbo6Qd0Me9Gez9` | ✅ done |
| game-tester (1st attempt) | `opencode/glm-5.3-flash-free#high` | `ses_eb0659e9cfferhdjGDcOHpqLg5` | ❌ endpoint failure ("Endpoint is unavailable") — glm endpoint suspect; retry used deepseek |
| game-critic | `xai/grok-4.7#xhigh` | `ses_eb0659e9cffdTTZgVMSlSOfnLh` | ✅ done |
| ui-designer | `opencode/nemotron-3.5-super-vl-free#high` | `ses_eb064ffafffeQtlZNaH3Pv0hKK` | ✅ done — 6 files under `res://ui/` |
| ux-designer | `opencode/beam-free#high` | `ses_eb0659e91ffediWawoCS3cuQCX` | ✅ done |
| artist | `opencode/nemotron-3.5-super-vl-free#high` | `ses_eb0659e90ffeTBYWDfOm271AUP` | ✅ done (planning only) |

## Repo state at shutdown
- HEAD = `618d1a5` ("death saves every 4 s; campfires heal +25%..."), **no new commits**.
- Uncommitted modifications (game-developer's, likely mid-work): `Main.tscn`, `project.godot`, `src/Boss.cs`, `src/Director.cs`, `src/Enemy.cs`, `src/Game.cs` (+547 lines), `src/PartyMember.cs` — 7 files, +637/−71.
- New untracked: `.opencode/`, `ui/` (ui-designer deliverables), plus ~19 stray smoke-log `.txt` files in the project root (`build_log/pilot_*/probe*/prun_*/q*/quick_*/run_0*`) — **sweep or delete on resume**; they were the developer's run logs.
- Last smoke run in flight: `prun_out.txt`/`prun_err.txt` written 20:00–20:01 (result never inspected).

## User guidance — carry into the next session (authoritative)
- **APPROVAL GATE (hard rule):** all feedback implementations and balance changes require the user's explicit approval BEFORE any agent starts work. Agents may *suggest*, but the user has the final say — this exists to prevent suggest→apply→re-suggest loops. Never bundle an unapproved balance/feedback change into a dispatch.
- **The user does balance testing themselves** going forward — no agent-run balance probes; game-tester stays for mechanics/flow/bug verification only, and balance "verification" is off the table until the user asks.
- **Reaper = test bench only** (death/revive mechanics testing). Ignore it in any balance pass; its chase behavior is not a difficulty signal.
- **Balance need NOT be "super balanced" now**: M1 is a short run (one mini-boss + one boss); real target ≈ **20-minute total runs** when stages expand later. Design intent: player *gets strong* AND challenge rises — growth is desired. M1 = core concept; refinement is later milestones' job.
- Consequence: developer's P2 balance numbers = sanity check, not tuning; cap binding + readable pressure is enough.

## Deliverables already received (condensed; full text lives in the subagent sessions above)
### game-tester (verified baseline at HEAD 618d1a5)
- Build green (0 errors; 3 pre-existing addon CS0618 warnings). Pilot headless run: VICTORY, level 9, xp 211, ~225 s sim, stderr 0 bytes.
- Locked rules 1–13 + 16–18 code-PASS; NOT run-verified (no prints, F4). Live-cap 40 never bound (max live 16, F6). AFK-no-pilot probe → TPK (clean).
- Doc drift F1: M1_STATUS "party healed 25%"/heal-aware pilot beats describe a deleted feature (heal pick removed in `f700526`).
- Findings: F2 live-counter leak via reaper `kind="dead"` path; F3 boss contact always hits Leader (violates ring-priority rule); F5 `--quit-after` frame-budget fragility (≥187 fps truncates before VICTORY); F7 gem/bullet pool exhaustion silent; F8 pilot ≠ lethality.

### game-critic (verdict + prioritized fixes)
"Stage sequencer, not a readable squad game." Developer-owned: boss pre-kill/softlock (registered parked bosses hittable from t=0; telegraph `Modulate` never reset); picker key-steal (`GetActionStrength` on shared A/S + no pause); ring decorative (no body-block; `REVIVE_RANGE 30` < orbit 64); hide eliminated bodies; campfire revive missing locked 2 s invuln; balance (kiting free: 170+ speed vs 78/150 mobs; `arcHalf 3.15` = 360° one-shot aura; spawns clamp onto player).

### ux-designer — 15-spec audit
Simulation faithful, presentation blind: no downed read (needs prone+grey+ring within 150 ms), no channel arc/tether, no telegraph ghost preview, no boss HP bar, no floating HP bars/damage numbers, no TPK overlay/retry, campfire has no choice UI. Cross-cutting spec table + anti-clutter contract (≤3 float texts/body, pooled labels, locked color vocabulary).

### ui-designer — DELIVERED FILES (exist on disk, unwired)
`res://ui/grimoire_theme.tres`, `Campfire.tscn`+`Campfire.gd`, `RunSummary.tscn`+`RunSummary.gd`, `INTEGRATION.md` (exact Game.cs wiring: campfire branch → `rest_chosen` signal; DEAD/VICTORY → summary screen; standalone-previewable). Open Qs: 8 s timeout vs pure player-driven campfire; long-rest power/horde-scaling hook; dismiss target; pick-history is placeholder.

### artist — art-direction package (planning only, no files)
Locked 16-color grimoire palette (hex table); party crests (Baldrun STORM/MOON bear, Mosiah VIOLET/GOLD bard); telegraph language amber→crimson→green inside the 0.7 s window, phase-tinted bullets; silhouette size-tiering rules; exact hook points (`Body` ColorRect→Sprite2D keeping node names so `Modulate` survives; `TelegraphRing` child driven by `_phase`; sprites in `Enemy.InitVisuals()` keyed by `kind`); suggested `res://assets|atlases|scenes/VFX|shaders` layout; P0 (must-final: silhouettes, telegraph colors, hurt flash, downed+d20, floor contrast) vs P1/P2 (8-bit pass = P2). Open Qs for user: leader choice, mini-boss/boss identity, d20 anim owner, campfire art scope, floor contrast, boss size vs weapon reach.

## Developer's pending work order (what it was dispatched with)
P1 bugs: (1) boss pre-kill/softlock — no RegisterEnemy until ActivateBoss, park off-screen; (2) reset telegraph Modulate; (3) picker `IsActionJustPressed` + pause combat while picker active; (4) hide eliminated; (5) campfire revive gets 2 s invuln; (6) F2 counter-leak decrement; (7) F3 boss uses nearest-member like regular enemies. P2 balance (per user guidance: sanity-check only, not tuning): speeds ~120/130/100, `arcHalf ~1.3`, wolf HP ~40, spawn outside camera never clamped, distinct picker stat ranks (maul area vs rapier cooldown, damage-only, 3-rank cap). P3 ring interpose + contact priority + `REVIVE_RANGE ~56`. P4 reaper = test bench only (dormant/Mosiah — revisit per reaper guidance: keep it a tool, no balance meaning). P5 wire ui/ screens (short/long rest; summary + in-engine retry). P6 instrumentation prints (roll/revive/STABLE/eliminate/damage-fail, `[10thAge]` prefix). P7 regenerate M1_STATUS "Verified in-engine" from fresh run. Gate: build 0 errors, pilot → VICTORY, stderr clean, new prints present, pilot auto-confirm still works.

## 2026-10-19 resume pass (game-developer, uncommitted — nothing staged)

- **Audit vs the P1–P7 order**: P1 (all 7 bug fixes), P2 (balance sanity values), P3 (ring interpose +
  contact priority + `REVIVE_RANGE 56`), P5 (ui screens wired + in-engine retry), P6 (`[10thAge]`
  instrumentation prints) = **landed**. P4 (reaper) = **partial** → repaired: landed code had moved the
  reaper spawn to `_stageT >= 45.0`, contradicting the locked spawn-once-at-10s rule; reverted to
  `10.0` in `src/Game.cs`. P7 = **done** (`M1_STATUS.md` "Verified in-engine" regenerated from fresh runs).
- **Gates green**: `dotnet build ... --no-incremental` → 0 errors, 3 pre-existing addon CS0618 warnings,
  DLL at `.godot\mono\temp\bin\Debug`. Headless pilot: **smokeR2 = VICTORY (lv 11, xp 315)**, **smokeR1 =
  clean TPK (lv 1, xp 4)**, **smokeR3 = clean TPK (lv 3, xp 19)**, all three **stderr 0 bytes**. The two losses
  are design-legal, not bugs: the reaper downed the **Leader** and only the Leader can channel (DESIGN "Leader
  down (deferred)"), so no rescue existed. smokeR1/R3 also show the **live-cap 40 binding** for the first time.
- **ui/ is clean on disk** — `ui/RunSummary.gd` is 118 lines and uses the locked `BLOOD` Color constant;
  there is no `add_theme_color_override("font_color", "blood")` anywhere in the repo (the reported parse
  error was an unsaved/parallel ui-designer draft, not landed). Deleted `ui/_probe.tscn`: an orphan ui-designer
  preview scaffold whose `res://ui/_probe.gd` never existed — the only genuinely dangling reference in `res://`
  (user-approved). No godot MCP instance connected all pass, so GDScript load is verified by 0-byte stderr, not
  `script-validate`.
- **Awaiting explicit user approval — NOT applied** (the approval gate; these stay suggestions):
  campfire AFK timeout (landed 25 s defaulting to a SHORT rest vs the work order's 8 s), long-rest power
  (landed: full heal + fallen at 25 % + one damage rank + `_hordeScale += 0.35`), `ui/Campfire.gd` treating
  KEY_A/KEY_D as rest choices (overlaps WASD/movement), and the landed P2 numbers vs work-order suggestions
  (Baldrun 200 hp / 128 speed / `arcHalf 1.3`, Mosiah 160 / 140, wolf 96 / goblin 48 hp, horde speeds 120 / 90).
- **Not run-verified** (need a human run or more frames): `R` retry, the LONG-rest branch, picker key-steal
  under real input, and the `STABLE` / `campfire revive` prints.

## Next steps (in order)
1. ✅ Confirm developer completion (diff audited vs P1–P7; see the 2026-10-19 section above). ✅ Stray root
   `.txt` smoke logs swept (none remain).
2. ◑ Re-run smoke (done: smokeR1/R2/R3) + a **no-pilot human-representative probe** — still owed: picker
   steal under real input, bosses unhittable pre-engage, cap binds, prints readable.
3. Ask critic to re-score briefly (balance bar relaxed per user guidance — growth+challenge, ~20-min intent).
4. Optional queue: ui-designer Qs (campfire timeout, dismiss target), artist Qs (leader, mini/boss identity),
   then artist asset production at its `res://` paths.
5. Commit + refresh M1_STATUS when verified.

## Facts worth re-checking at resume (why they matter)
- `project.godot` input: `pick_a`/`pick_s` share WASD letters (source of the picker-steal bug).
- No `.gd` files existed before (C# only) — now ui-designer added `ui/*.gd`; `script-validate` applies to those.
- Godot exe: `C:\AI Coding\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe`; dotnet PATH prepend `C:\Users\Steve\dev-tools\dotnet` (system dotnet is runtime-only). PowerShell here is PS 5.1: no `&&`, `>` redirect is UTF-16; there is no `Get-Tail`/`Get-Dir` — use `Get-Content -Tail`, `Get-ChildItem`.
- No Godot editor MCP instance was connected all day (`godot.*` tools unavailable; that is why tester/screenshots were static-only).
