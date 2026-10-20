# RESUME NOTES — Surviving the 10th Age — end of 2026-10-20 session (CURRENT)

Session stopped on request. Resume here first — everything below the 2026-10-20 block is older history (keep as archive; M1_STATUS.md remains the single source of truth for flow/pitfalls/balance passes).

## Where we are (2026-10-20 shutdown)
- HEAD = **pass-10 commit (the one you are looking at)** on `main` — three pass-9 human-playtest bug reports fixed. Chain: pass 9 `1524c6e` > pass 8 `058f762` > resume notes `c96b1d8` > pass 7 `3e4dfa0` > older. Local-only, nothing ever pushed.
- Pass 10 shipped (all three reports, plus the leak the reports exposed):
  1. **Rescue staging** — the stranded PC lands PAST the camera edge (`SpawnFieldRecruit` walks a random bearing out to the visible rect's exit + `RECRUIT_MARGIN` 90 px, floor `RECRUIT_DIST` 300, 8 retries because `ClampToArena` can pull a candidate back into frame); escort goblins open `ESCORT_RING` 340 px wide of the PC (was 160).
  2. **Crawl gate** — an escort goblin does not move until `Game.RecruitInView()` (PC inside the ~960x540 camera rect); creep factor 0.4 → `ESCORT_CRAWL` 0.22. So the beat no longer animates off-camera and reads as "they are coming for him".
  3. **Join banner = SOFT pause** — `Game.GetWorldPause()` = `_pickerActive || _joinWaiting`; the 4 external gates (`Enemy`, `PartyMember`, `Boss`, `Director`) switched to it, `_Process` gained a freeze block after `UpdateHpStrips`, and the run-clock guard now excludes `_joinWaiting` too. Deliberately NOT a tree pause: the CLICK TO CONTINUE gate needs input frames.
  4. **Hint leak** — the CLICK TO CONTINUE Label was a LOCAL inside `JoinRecruit`, so `ClearJoinBanner` never had a handle on it and it stayed painted over the HUD for the rest of the run (that stale prompt next to the SHORT REST GIFT picker is what read as a frozen game). Now a field `_joinHint`, hidden with the card.
  5. **Campfire freeze/crash** — after `EndCampfire` hides the card, a hidden Control still receives `_input` and its rect still answers, so a stray click or ENTER re-ran `_on_commit_pressed` (`modal.show()` + `get_tree().paused = true` with nothing visible and nothing left to un-pause it). `Campfire.gd` now bails `if not visible` at the top of `_input`, and `_on_rest_now` bails `if not modal.visible` so a rest cannot resolve twice behind the camp. NOTE: the exact crash line never reached the log (buffer lost on crash); the hidden-card routing is the confirmed freeze mechanism, so if a human run still crashes on the short rest, say so and we hunt it with the runtime-error capture instead.
- Gate evidence: `smokeCT18` TPK lv 4, `smokeCT19` VICTORY lv 6 (xp 88), `smokeCT20` TPK lv 4 — all `.err` 0 bytes. CT19 logged the drop 1119 px off camera, the camp-sweep join, the pilot join-banner clear, the SHORT-rest chain, BOSS and VICTORY.
- **NEXT: one more human playtest round on pass 10.** Ask the player: does the off-camera rescue read the way they wanted (they must walk to it), does the escort now creep in slowly enough, does the join banner actually pause so they can look at the art, and is the short rest clean? Pilot smokes never walk to the rescue site, so escort-KILL joins are still only human-tested (the camp-sweep join is smoke-covered).
- Balance hold: escort goblin HP (30 × horde) and base speed stay user-side; `ESCORT_RING` / `ESCORT_CRAWL` / `RECRUIT_MARGIN` are staging dials, re-take them on request.

## Queued vision slices (unchanged, from M1_STATUS.md)
- art/8-bit slice H (artist report: enemy silhouettes, attack FX, atlas/palette, boss spectacle), M2 roster slice (3 PCs + join beats + evolutions), campfire/meta-map stub.
- Optional cleanup: SDK artifacts (`sdk-bak-8.0.108`, zip); decide whether `RESUME_NOTES.md` / `.opencode/` get tracked (`RESUME_NOTES.md` is now tracked since this commit, `.opencode/` still untracked).

## Resume recipe (unchanged — details in M1_STATUS.md)
- Build: `& 'C:\Users\Steve\dev-tools\dotnet\dotnet.exe' build survivingthe10thage.csproj --no-incremental -nologo` (no `-o`; OutputPath baked to `.godot\mono\temp\bin\Debug\`).
- Smoke: `"C:\AI Coding\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe" --path C:\GitHub\survivingthe10thage --headless --quit-after 90000`, run via `cmd /c 'set M1_PILOT=1&& ... > file 2> file2'`, timeout 0, SEQUENTIALLY. Gate = VICTORY-or-TPK line + 0-byte `.err`. Pilot = verification only, never human play. `M1_REAPER=1` re-enables test bench.
- Campfire.gd is TAB-indented — python byte-exact replaces with `repr()` verification are the only reliable edit path.
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
4. Optional queue: ui-designer Qs (campfire timeout, dismiss target), artist Qs (leader, mini/boss identity), then artist asset production at its `res://` paths.
5. Commit + refresh M1_STATUS when verified.

## Facts worth re-checking at resume (why they matter)
- `project.godot` input: `pick_a`/`pick_s` share WASD letters (source of the picker-steal bug).
- No `.gd` files existed before (C# only) — now ui-designer added `ui/*.gd`; `script-validate` applies to those.
- Godot exe: `C:\AI Coding\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe`; dotnet PATH prepend `C:\Users\Steve\dev-tools\dotnet` (system dotnet is runtime-only). PowerShell here is PS 5.1: no `&&`, `>` redirect is UTF-16; there is no `Get-Tail`/`Get-Dir` — use `Get-Content -Tail`, `Get-ChildItem`.
- No Godot editor MCP instance was connected all day (`godot.*` tools unavailable; that is why tester/screenshots were static-only).
