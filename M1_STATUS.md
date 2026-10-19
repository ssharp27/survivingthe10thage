# M1 STATUS — 2026-10-18 pass, refreshed 2026-10-19 (full stage flow verified through VICTORY)

M1 greybox of "Surviving the 10th Age" (Tanurial Forest) builds green, is **human-playable**,
and the full campaign-battle loop is verified in-engine headless:
roster → horde I → mini-boss slain → campfire → horde II (live-cap 40) → BOSS engaged →
`VICTORY — Tanurial Forest cleared`. The 2026-10-19 pass (bug fixes P1, ui wiring P5,
instrumentation P6) is **uncommitted** and verified by the smoke runs below.

## What was fixed on 2026-10-18

- **Input map was dead**: the old `[input]` entries were Godot-3 style (`events: []` + top-level
  `physical_key`, mapped to physical codes 544-547 = A,B,C,D). Godot 4.7 parsed them with no error
  but bound nothing. Rewrote to `Object(InputEventKey,...)` serialization:
  move = WASD (W566/A544/S563/D547) + arrows (4194305-4194308); level-up picker = A/S/D
  (shares the WASD letters — VS-style overlap, fine on a confirm-any-time picker).
- **Pilot boss engagement**: standing still pinned the whole horde into permanent contact damage
  (AFK TPK 15 s into the mini-boss). Now the pilot strafes at 90-110 px around the active boss
  with a `faceTo` face-lock so the maul arc stays pointed at the boss (movement would otherwise
  set facing to the strafe tangent, showing the boss its back).
- **Pilot bullet dodge**: boss bullets pass through the party when `M1_PILOT` is set
  (verification-only; simulates a human dodging telegraphed volleys).
- **Pilot picker rotates the three named picks** (wider arc / quicker thrusts / deeper thrust) up to the
  rank cap. There is **no party-heal pick** — `OPT_LABEL` has three entries and level-ups never restore HP
  (healing lives only in campfires and revives). The old "heal-aware pilot / party healed 25%" text described
  a pick removed in `f700526`; that was doc drift F1, corrected here on 2026-10-19.
- **Picker key-steal fixed (2026-10-19, supersedes the WASD-overlap note above)**: `pick_a/pick_s/pick_d`
  still share the A/S/D letters, but presses are now edge-read (`IsActionJustPressed`) and `_Process`
  returns early while the picker card is up, so a held A/S no longer walks AND confirms in the same frame,
  and arena/horde/party are frozen while you read the card.

## Verified in-engine (smokeR1 + smokeR2 + smokeR3, 2026-10-19, M1_PILOT=1, `--quit-after 60000`)

Three fresh headless runs of the current tree: **smokeR2 reached VICTORY** (level 11, xp 315, 53 log lines);
**smokeR1 (level 1, xp 4) and smokeR3 (level 3, xp 19) ended in clean TPKs**. All three stderr files are
**0 bytes**. The earlier smoke1/smoke2 pair (both VICTORY, level 12) is superseded by these.

Why the pilot now loses 2 of 3, and why that is not a bug: in R1 and R3 the spawn-once-at-10s **TEST reaper**
downed the **Leader** (Baldrun). Per DESIGN "Leader down (deferred)" only the Leader can channel a rescue, so
once the Leader is down there is **no channel at all** — the only rescue is the d20 saves, and the reaper chases
the nearest member, which is usually the Leader at the front of the ring. In R2 the reaper happened to down
Mosiah, so Baldrun walked onto the body and the channel completed. The reaper is a test bench (user-locked:
no balance meaning), but this is the reason the pilot win-rate looks worse than before. If the deferred
"a party member can revive the Leader" mechanic ever lands, this pattern disappears.

Full smokeR1 log (this is the run that exercised the dice, so it is the P6 evidence):

```
[10thAge] squad: Baldrun (Maul) + Mosiah (Rapier) | leader: Baldrun
[10thAge] (pilot) horde I t=0 | lv 1 xp 0 | live enemies 0 | party: Baldrun 200hp  Mosiah 160hp
[10thAge] TEST reaper live
[10thAge] (pilot) horde I t=15 | lv 1 xp 4 | live enemies 10 | party: Baldrun 199hp  Mosiah 159hp
[10thAge] TEST reaper downed Baldrun - death saves start
[10thAge] d20 Baldrun rolled 10 (target 20 revive / 10 or under fails)
[10thAge] damage on downed Baldrun = extra death-save fail 2/3 (buffered 2s)
[10thAge] (pilot) horde I t=30 | lv 1 xp 4 | live enemies 22 | party: Baldrun down 2/3  Mosiah 144hp
[10thAge] d20 Baldrun rolled 8 (target 20 revive / 10 or under fails)
[10thAge] Baldrun ELIMINATED (3 failed saves) - returns at campfire
[10thAge] (pilot) horde I t=45 | lv 1 xp 4 | live enemies 37 | party: Baldrun OUT  Mosiah 79hp
[10thAge] Mosiah DOWN at 0hp - d20 death saves start
[10thAge] damage on downed Mosiah = extra death-save fail 1/3 (buffered 2s)
[10thAge] damage on downed Mosiah = extra death-save fail 2/3 (buffered 2s)
[10thAge] (pilot) horde I t=60 | lv 1 xp 4 | live enemies 40 | party: Baldrun OUT  Mosiah down 2/3
[10thAge] d20 Mosiah rolled 4 (target 20 revive / 10 or under fails)
[10thAge] Mosiah ELIMINATED (3 failed saves) - returns at campfire
[10thAge] TPK - the whole party is down | level 1 | xp total 4
[10thAge] run summary up - press R to retry the stage (journal/menu are M4)
```

smokeR2 key lines (VICTORY walk):

```
[10thAge] TEST reaper downed Mosiah - death saves start
[10thAge] channel START on Mosiah's body - stand still 3s; the body is immune while it runs
[10thAge] channel COMPLETE: Mosiah revived at 50% hp | invuln 2s granted
[10thAge] mini-boss slain - campfire: party +25% HP, the fallen return at 25%
[10thAge] campfire: the rest card is up - short/long is the party's call
[10thAge] campfire: nobody chose a rest after 25s - defaulting to a short rest
[10thAge] SHORT rest: company healed +25% max HP (clamped), horde pace unchanged
[10thAge] upgrade: Baldrun wider arc (maul area) capped at rank 3/3 (evolution is M2)
[10thAge] VICTORY — Tanurial Forest cleared | level 11 | xp total 315
[10thAge] run summary up - press R to retry the stage (journal/menu are M4)
```

smokeR3 (the second Leader-down TPK) is the run where the live cap visibly saturates:

```
[10thAge] TEST reaper downed Baldrun - death saves start
[10thAge] d20 Baldrun rolled 12 (target 20 revive / 10 or under fails)
[10thAge] damage on downed Baldrun = extra death-save fail 3/3 (buffered 2s)
[10thAge] Baldrun ELIMINATED (3 fails) - returns at the next campfire
[10thAge] (pilot) horde I t=64 | lv 3 xp 19 | live enemies 40 | party: Baldrun OUT  Mosiah 43hp
[10thAge] Mosiah DOWN at 0hp - d20 death saves start
[10thAge] Mosiah ELIMINATED (3 fails) - returns at the next campfire
[10thAge] TPK - the whole party is down | level 3 | xp total 19
```

All three runs: stderr **0 bytes** — no GDScript parse/load errors from `ui/` (the Campfire and RunSummary
screens instantiate, their `_ready()` runs, and `party_data` / `members` set cleanly from C#), no
C# exceptions. The `ui/` screens are therefore load-verified headless; the `R` retry path is code-wired
(`retry` input action + `ChangeSceneToFile`) but not exercised headless (no input frames press R).

Live cap 40 **now binds** (it never did before): smokeR1 reads `live enemies 40` at t=60 and smokeR3 at t=64,
and the counter holds at 40 rather than drifting upward (F2 fix: `Enemy.counted` + `ReleaseLiveCount`).
The earlier runs peaked ~23 live because the pilot cleared the horde faster under the pre-P2 numbers.

P6 prints confirmed firing: `d20 <name> rolled N`, `damage on downed = extra death-save fail k/3 (buffered 2s)`,
`<name> DOWN at 0hp`, `<name> ELIMINATED`, `channel START/COMPLETE`, `TEST reaper live/downed ...`, campfire
card + 25 s timeout + SHORT rest, rank-cap lines. Note the two elimination phrasings are path-specific and both
correct: the dice loop says `ELIMINATED (3 failed saves) - returns at campfire`, the damage path says
`ELIMINATED (3 fails) - returns at the next campfire`. Still not exercised by the pilot: `STABLE` (3 successes)
and `campfire revive` — the pilot either channels (3 s < the 4 s save cadence, so no dice roll happens) or
loses the body outright; needs a human run that leaves a STABLE body down through a campfire.

Logs: `C:\Users\Steve\AppData\Local\Temp\smokeR1.out` (1506 B) / `smokeR1.err` (0 B);
`smokeR2.out` (4960 B) / `smokeR2.err` (0 B). The console renders the em-dash as a replacement glyph —
a codepage artifact of the redirected pipe, not a runtime issue.

## How to play (human, GUI)

Desktop `.bat` launches the editor (prepends `C:\Users\Steve\dev-tools\dotnet` to PATH — required;
system dotnet is runtime-only). Run the main scene; **WASD or arrows** move the Leader,
party follows on the bodyguard ring, everyone auto-attacks. At a level-up the picker label
shows options — press **A / S / D once** (press-edge read; holding a movement key no longer
confirms a pick, and the arena is frozen while the card is up). After a run ends the
**R** key retries the stage from the run-summary card (journal/menu are M4).

**Death-state tester**: 10 s into a run a **big red diamond "reaper"** spawns **exactly once** — touching it
**downs** whoever it touches (death saves start; it's a revival/STABLE test bench) and then it despawns.
It doesn't outrun the party; walk into it yourself. Dead nodes are fully inert (`kind="dead"` early-return).
Level-up picks are **distinct per-option effects capped at rank 3** (wider arc ×1.18,
quicker thrusts cooldown ×0.86, deeper thrust range ×1.16 + 1 pierce; damage ×1.08-1.12 per rank) — the old
flat "+25% dmg" ranks from `f88de14` were replaced. Death saves (locked): d20 every **4 s** — nat 20 revives 25 %;
3 successes = **STABLE**, rolls stop and hits slay unless the Leader **stands still ~3 s on the body** (revive 50 %);
3 fails = OUT until the next campfire. Campfires (locked, rest-choice split): the camp beat lifts the fallen
(**return at 25 % HP**) and stands bodies down→up without healing; **SHORT rest = the single +25 % max-HP heal**
(clamped, never stacked); **LONG rest** fully heals the already-standing company while the lifted fallen **stay at
25 %**, grants one free rank, and grows horde scaling ×+0.35. **Revive protection (locked)**: a downed body being channeled is **immune** while
the channel progresses (a swarm can't screw the rescue out), and every revive grants **2 s invulnerability**
(pale-green tint); the reaper tester obeys immunity too — it only despawns after its down actually lands.

## Godot 4.7 C# pitfalls (found empirically, keep for future work)

- **C# `_draw()` is never invoked** (binding dropped the hook; a plain `public void _draw()`
  silently runs 0 times — probe proved it). Drawing *outside* `_draw` is engine-enforced
  ("Drawing is only allowed inside this node's `_draw()`…"). A GDScript child worked in
  theory but users still saw nothing, so the shipped workaround is **pure nodes**: a
  pivot + thin ColorRect "spokes" (maul = 3-ray fan, rapier = solid line) rotated to
  `facing` and alpha-faded each frame — node attributes, not draw commands.
- 4.7 bindings renames/removals hit so far: no `Godot.GDScriptResource` class (use `Godot.Script`);
  no `Node.Script` property (use `Set("script", …)`, which returns **void**); no `z_index` field
  (use `ZIndex`); input serialization is `keycode`/`physical_keycode`; no `Input.GetPressedKeys`;
  `DrawPolygon` wants per-vertex `Godot.Color[]`; `_draw` not virtual (never `override`).

## Still open — vision work (pick priority with user)

1. **Balance/feel pass for human play**: contact pressure, XP curve, campfire heal economy;
   numbers live in Game.cs / Director.cs / Main.tscn (cheap tuning surface).
2. **Boss spectacle**: Enter-the-Gungeon feel — phase telegraphs exist in Boss.cs (3 phases,
   ring/fan volleys); needs wind-up visuals, boss body size/silhouette, named phases.
3. **Roster growth**: 3 more campaign PCs (recruit = join-point narrative beats in this stage),
   3-rank upgrade tracks + evolutions (Stormcaller / Viperkiss) currently stubbed as +15 % ranks.
4. **8-bit D&D aesthetic pass**: greybox ColorRects → pixel sprites + palette; battle-scene backdrops.
5. **Meta-layer stub**: campfire screen (return eliminated members), campaign-path stage select.
6. Nice-to-have: MCP plugin GUI boot + hub re-pin (untested since the DLL-placement fix).

## Pilot note (verification-only, env-gated — never active in human play)

`M1_PILOT=1` env ⇒ leader autopilots (orbits around threats, boss strafe + face-lock), bullet volleys
pass through, incoming party damage ×0.15, and the picker auto-confirms at 2 s with a **rotating named
pick** (there is no heal pick). Implemented in Game.cs (`_pilot`, `faceTo`, bullet-dodge gate,
`PickerTick` pilot branch). The stand-still revive channel is back with the STABLE rules (dice nat-20
revive coexists with it), so pilot walk-to-body still exercises it — but only while the **Leader** is live;
with the Leader downed the pilot has no body to walk, which is the deferred rule in DESIGN.

## Build + smoke recipe

```powershell
$env:Path = 'C:\Users\Steve\dev-tools\dotnet;' + $env:Path
cd C:\GitHub\survivingthe10thage
dotnet build survivingthe10thage.csproj --no-incremental -nologo   # NO -o: SDK props put the DLL
# at .godot\mono\temp\bin\Debug — exactly where the engine loads from
$env:M1_PILOT = '1'
$g = 'C:\AI Coding\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
& $g --path 'C:\GitHub\survivingthe10thage' --headless --quit-after 60000 > "$env:TEMP\smoke.out" 2> "$env:TEMP\smoke.err"
```

Proven cmd form (avoids PS 5.1's UTF-16 `>` and lack of `&&` — use this for the logs you will grep):

```bat
cmd /c 'set M1_PILOT=1&& "C:\AI Coding\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe" --path C:\GitHub\survivingthe10thage --headless --quit-after 60000 > C:\Users\Steve\AppData\Local\Temp\smoke.out 2> C:\Users\Steve\AppData\Local\Temp\smoke.err'
```

`M1_SKIP_REAPER=1` (verification-only, pair with M1_PILOT) skips the reaper test bench so a smoke
pilot reliably reaches the campfire branch; with the reaper ON the pilot TPKs ~often because its
down targets the Leader and the deferred Leader-down rule leaves no rescue channel.
**Gate on the `VICTORY` / `TPK` line, not on the frame budget**: `--quit-after` is fragile (frame rate
varies run to run). A run that ends in TPK legitimately has no VICTORY line — read the last log line to
tell truncation apart from a real loss; rerun at 90000 frames only if the last line is a mid-stage status
line.
