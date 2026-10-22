# M2 STATUS — **IN PROGRESS** (opened 2026-10-22; Asper + the beat loop landed this pass)

M2 = the campaign roster. Scope, locked decisions, and gate trail live here; `M1_STATUS.md` stays the
authoritative M1 record (passes 1–22, signed off at `f0ca883` + `demo_final.zip` shipped, human test
PASSED). Tree was clean at M1 sign-off when M2 opened.

## Scope (`DESIGN.md`:52 — "M2 — the campaign roster")

- All **5 PCs** with classes, signature attacks, and **rank-4 subclass evolutions** (Stormcaller /
  Viperkiss / …; `DESIGN.md`:17 "3 ranks, then an evolution = subclass choice", data-driven).
- **Recruitment wiring** for the whole roster (M1 shipped one join beat: Mosiah).
- **Meta-progression**: unlock characters, who can be recruited. The starting-party-size knob
  (1→2→3) is **deferred** per the user.

Progress this pass: **1 of 5→3 roster PCs playable is not the claim** — Baldrun + Mosiah (M1) + **Asper**
= 3 in play; **Alvora** and **Yu** are dictated but not implemented. The stage **beat loop** and the
**5-slot picker** are in place, so the two missing PCs drop straight into live beats once they land.

## Stage beat loop — user-locked 2026-10-22

The user locked this schedule ("Our gameplay beat/loop for now until times and scaling are changed
around"), so it is **data on the run clock**, not a state chain: `Game.cs`:61–71 `BEAT_T` /
`BEAT_KIND` / `BEAT_TIER` / `BEAT_BOSS`, 9 rows.

| run s | row | what fires |
| --- | --- | --- |
| 30  | ally I        | Mosiah joins (escort cage) |
| 50  | tier-2 gate   | horde tier 2 |
| 70  | mini-boss I   | `mini1` — its kill opens the camp |
| 90  | ally II       | Asper joins + **6 tier-2 escorts** |
| 120 | first boss    | `boss1` (mid boss) — its kill keeps the horde marching |
| 150 | ally III      | + tier-3 escorts ("we can define them now") |
| 180 | mini-boss II  | `mini2` — its kill opens the camp |
| 210 | ally IV       | + tier-4 spawns — **spelling to confirm** (user wrote "tier-5") |
| 230 | final boss    | `final` → VICTORY |

Rules the sequencer enforces (`Game.cs`:760 `AdvanceBeats` / `ParkNextBeat`):
- Times are **run seconds**, so a picker / join / camp pause never burns a beat (pass-22 rule).
- Rows fire in table order; `_nextBeat` only ever moves forward, so a row can fire at most once.
- **A live boss holds the next row back** — the fight must be resolved before the stage advances
  (this is what M1's `MINIBOSS`/`BOSS` states did with a state change). The hold is
  `BossPending()` (`active == true`, cleared only by `OnBossKilled`), which is **stricter** than
  `GetActiveBoss()` (`active && hp > 0`): a boss whose hp crossed zero but whose kill routing
  has not run yet still owns the arena. Without that strictness the row after a boss beat slips in
  during the one-frame gap and prints "final boss engaged" **before** "mini-boss slain → camp".
- **An unresolved escort rescue holds the next ALLY row back** so two stranded PCs never share the
  one rescue site. `ESCORT_GIVEUP = 55.0` s is the valve: abandon the cage and the PC joins at the
  floored HP and the stage keeps moving.
- A boss row **consumes itself** (`_nextBeat++` then `break`) before the camp break.
- `StageState` collapsed to {`PLAY`, `CAMPFIRE`, `DEAD`, `VICTORY`}; `PLAY1`/`MINIBOSS`/`PLAY2`,
  `RECRUIT_AT`, `TIER2_AT`, `BOSS_AT` are gone — the beat table subsumes them.
- Kill routing is decided by **boss identity by class** (`c is Boss`), and the **kind string is data**
  (`mini1`/`boss1`/`mini2`/`final`; legacy `"mini"` still accepted) — mini → camp, mid boss → keep
  marching, final → VICTORY (`Game.cs`:520 `OnBossKilled`).

## Locked decisions (user, 2026-10-22)

1. **All 4 recruits join inside Stage 1 (Tanurial Forest)**, one beat per non-starting PC, reusing the
   existing escort-cage language (`SpawnFieldRecruit`). **Later beats use more difficult enemies** —
   difficulty ramps with beat order.
2. **One PC becomes permanently unlocked when the 20-minute run has been won**, and **the player
   chooses which one** at the end. (Ties the standing 20-minute run-curve workstream to M2: the
   unlock gate cannot be evaluated before the extended-run curve exists.)
3. **Starting party size 1→2→3: DEFERRED to the meta slice.** Baldrun stays the set starter for now.
4. **Roster data is the user's to dictate** (name / race / class / signature weapon + behaviour /
   base stats / evolution).
5. **Unlock + meta progression governs who can be recruited/unlocked** — beats are the delivery
   mechanism, the meta layer is the permission layer.
6. Future-milestone note (main-menu / character-select / stage-select era): the player picks **which
   PC starts**. Not M2 work; capture only.

## Roster dictation (user) — implementation state

- **Asper** — half-elf arcane archer, insect themed. Longbow fires **bug projectiles at the closest
  enemy**. Upgrades: damage, attack speed, range, **multishot (fan of simultaneous shots)**. Damage
  10, HP 60; approved sanity defaults: range 260 px, cooldown 0.9 s, baseSpeed 132 px/s, proj speed
  340 px/s, projLife 2.2 s. **LANDED this pass** (`Main.tscn` `memberIndex = 2`, `weaponKit = "shot"`,
  `class_ = "Arcane Archer"`, no `arcHalf`).
- **Alvora** — Owlin twilight cleric. **Spirit Guardians** = persistent AoE around himself, **10
  damage to every enemy inside, NOT split by count**. Upgrades: damage, AoE size, tick rate,
  armour (flat −1/−2/−3, floor 1). Damage 10, HP 80. **DICTATED, NOT IMPLEMENTED.**
- **Yu** — Tortle Occultist. Summons small creatures spawned near himself that run to an enemy and
  **explode for AoE damage**. Upgrades: damage, explosion AoE, summon rate, multiattack. Minion
  damage 20 (not reduced by caught enemies), HP 70. **DICTATED, NOT IMPLEMENTED.**
- Projectiles, rays, summons and AoEs are all in scope eventually.

## Landed this pass (uncommitted → committed this commit)

- **Asper's kit**: `[Export] weaponKit` ("melee" | "shot") dispatch in `PartyMember.cs` replaces the
  `arcHalf > 0.5f` two-branch verb switch; `arcHalf` is now **purely a melee property**. Shot branch
  aims at the closest body in sight (bosses included), spawns into a **party-owned shot pool**
  (`_shots`, `Game.cs`:233/1181/1894, mirroring the boss-owned `_bullets`), multishot fans extra
  bolts off the same commit (`PartyMember.cs`:470, ±0.16 rad/bolt). Telegraph armature is gated
  `if (!shotKit)` — a bow shows its bolt in flight, not a fan of ground spokes.
- **Bolts hit bosses** (this was the dead-weight defect this session found): the old shot test skipped
  `nc is Boss`, so the longbow did nothing in every fight, and the beat loop cannot advance while a
  live boss owns the arena. Hit radius now rides the silhouette:
  `float hitR = nc is Boss ? 8.0f + ((Boss)nc).radius : 20.0f;` (a `8.0` double does **not** implicitly
  narrow to `float`).
- **Beat-table sequencer** (`AdvanceBeats` / `ParkNextBeat` / `BeatLabel` / `GetRunT`) + `Director.cs`
  rewritten to the beat API (`GetHordeActive` / `GetHordeTier` / `GetBossLive`). `GetTier2At()` and
  `BossAlive(kind)` deleted — dead, and `BossAlive`'s semantics broke once `OnBossKilled` rewrites
  `kind` to `"dead"`.
- **Boss visuals/engage are kind-aware** (`Boss.cs`); mid boss sized 80 px amber (mini 60 / final 96).
  Four boss nodes in `Main.tscn`: `mini1` 300hp/r46, `boss1` 520hp/r56, `mini2` 380hp/r46, final.
- **Escort beats**: escort bodies draw from the horde tier live when the beat fires; 90s ally II brings
  6 tier-2 bodies. Mid boss sized up from the mini.
- **Grown picker**: `PICK_SLOTS = 5`, one option per member in play (up to 5, keys 1–5). Asper tracks
  are options 6–9 (`heavier arrows` / `quicker draw` / `farther shot` / `multishot`) — all four verified
  present and the fan verified in `PartyMember.cs`. `project.godot` gains `pick_4`/`pick_5`.
- **Camp skip made honest**: the dev C key now only slays a mini that the **beat table already put on
  the arena** (`active`), and otherwise parks `_runT` on the next mini beat so `AdvanceBeats` engages it
  on the real path (same `park > runT` guard as the join skip). The old version reached every boss node
  unconditionally at boot — instant-slaying minis the party never fought and restarting the camp timer
  every frame — an artifact a smoke must never lean on.
- **Dead-code cleanup** (behavior-neutral): the duplicated `if (_joinWaiting)` join-ceremony block was
  **pre-existing in M1's signed-off HEAD** (byte-identical, unreachable after the first `return`); the
  stale "advances to `PLAY2`" comment corrected to `PLAY`.

## Needs the user's word (blocking, not guessed)

- **Tier-3 / tier-4 creatures are undefined in the repo.** The 150 s / 210 s escort beats currently run
  **tier-2 bodies with an elite HP bump** (×1.3 tier 3, ×1.6 tier 4) — flagged placeholders, no invented
  creatures. The user must dictate the real ones.
- **The 210 s beat's spelling**: the user wrote "tier-5 spawns" while 150 s is tier 3 — almost certainly a
  typo for **tier 4** (and the table is built that way), but confirm.
- **Rank-4 evolution shape** (one-way vs two-way subclass choice) unanswered; none of the 3 new PCs has
  a named evolution.
- **Bolt-on-boss** is a decision I made to unblock the beat loop (bosses are enemies and the loop cannot
  advance past a live boss) — veto if the longbow should not trade with bosses.

## M2 backlog / open items

- Implement **Alvora** and **Yu** on the specs above (blocking the last 2 roster slots).
- **M1 carry-over, not M2 scope unless the user pulls it**: deferred human-playtest checklist (dash
  feel, escort contact chips, live HUD strip, 25% join-HP floor, picker gating, DPI frame, escort-KILL
  joins), camp alignment round 3, 20-minute run-curve pass (see decision 2 — becomes load-bearing).
- **Doc staleness**: `ui/INTEGRATION.md` still shows `_state = StageState.PLAY2` (states collapsed to
  `PLAY` this pass); no `.gd` reads the state machine, so it is documentation-only. Fix when the doc is
  next touched.

## Gate trail (this pass)

Gate = the trail ends on a `VICTORY` or `TPK` line **and** the run's `.err` is 0 bytes. Smoke recipe
and the Godot 4.7 pitfall catalogue are unchanged — see `M1_STATUS.md` "Build + smoke recipe" /
"Godot 4.7 C# pitfalls" and `RESUME_NOTES.md` "Resume recipe".

- `smokeM2a`–`d` — plain pilot, intermediate build (pre-`BossPending`): **4× TPK terminal, 0-byte `.err`**;
  the dev skips were silently inert there (env-var pitfall: `set A=1 B=2` sets only one variable —
  correct form is `set A=1&& set B=2`).
- `smokeM2e` — camp skip, intermediate build: TPK + 0-byte `.err`, but **dev artifacts** (boot instant-killed
  both minis every frame, camp timer restarted) — superseded by the honest camp skip.
- `smokeM2f` — join skip, correct env, intermediate build: **VICTORY**, full loop walked, but it exposed the
  ordering anomaly (final-boss engage printed **before** the mini "slain → camp" line) and VICTORY while the
  state readout said `CAMPFIRE`. Root cause fixed at `BossPending()`.
- `smokeM2g`/`h` — killed mid-run: **VOID**, not evidence either way.
- `smokeM2i`–`k` — launched on the `BossPending` build then killed mid-run: **VOID** (the partial read of
  `i` confirmed the `boss <kind> <hp>hp` readout showing one boss at a time and the `ESCORT_GIVEUP` valve).
- `smokeM2l` — join skip on the `BossPending` build: **VICTORY**, ordering correct — `boss mini1 135hp…99hp`
  → camp → `boss boss1` "keeps marching" (no camp) → `boss mini2 380hp` → camp → `boss final` → VICTORY.
- `smokeM2m` — plain pilot on the `BossPending` build: **TPK** (party wiped during the `mini1` fight at
  level 6, `live enemies 36`, 0-byte `.err`) — expected, M1's pilot TPKs and the 90 s ally had not joined yet.
- `smokeM2n` — camp skip on the `BossPending` build: **superseded** by the camp-skip rewrite.
- `smokeM2o` — **camp skip, shipped build**: **VICTORY**, 0-byte `.err`. Confirms the honest skip: park →
  engage on the real beat path → one slay per mini (`(dev) runT parked on the next mini-boss beat` at boot,
  never at an inactive boss); the mid boss was fought with real damage (`boss boss1 370hp` → "keeps marching");
  loop walked 30 ally → 50 tier-2 → 70 `mini1` → camp → 90 Asper + 6 tier-2 escorts → 120 `boss1` → 150 ally-3
  skipped + tier 3 → 180 `mini2` → camp → 210 ally-4 skipped + tier 4 → 230 `final` → VICTORY.
- `smokeM2p` — join skip, **shipped build**: **TPK** terminal, 0-byte `.err`. Walked 30 ally → 50 tier-2 gate
  → 70 `mini1`, then the boss-live hold kept rows 90/120/150 back while `mini1` was live (readout never shows
  a second boss, `mini1` 115hp → 75hp); the party wiped inside that fight at level 6.
- `smokeM2q` — plain pilot, **shipped build**: **TPK** terminal, 0-byte `.err`, same shape (stopped at the
  `mini1` row, hold respected, `live enemies 40` at the wipe).
- **Gate for the shipped build is green**: `smokeM2o` (camp) VICTORY, `smokeM2p`/`smokeM2q` (join / plain)
  TPK, all three `.err` 0 bytes. A TPK is a pass here — the pilot autopilot is not a competent player, and
  M1's pilot TPKed too; the VICTORY proof is the camp-skip trail that walks the whole loop.

## Pitfalls added this pass (on top of the M1 catalogue)

- `8.0f` vs `8.0`: Godot C# will not implicitly narrow a double literal to `float` in a ternary arm —
  `float hitR = cond ? 8.0f + r : 20.0f;` must be `8.0f`.
- The pilot `t=` readout is `GetStageT()` = `_runT - _beatT0` (**leg-relative** clock), not the run clock;
  the beat table's own times are **absolute run seconds**. A trail that looks like "beats fired at t=10"
  is a leg clock, not a schedule bug.
- `foreach`-hold tests must match the **fight** meaning, not the **hittable** meaning: `GetActiveBoss()`
  needs `hp > 0` for targeting, but the scheduler needs `active` alone. Same array, two different questions.
- A dev skip that reaches every boss node unconditionally is a **smoke lie**: gate it on the arena's actual
  owner, or park on the beat and let the real path engage.
