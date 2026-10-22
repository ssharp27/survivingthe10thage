# M2 STATUS — **IN PROGRESS** (opened 2026-10-22; Asper, the beat loop + Alvora landed this pass)

M2 = the campaign roster. Scope, locked decisions, and gate trail live here; `M1_STATUS.md` stays the
authoritative M1 record (passes 1–22, signed off at `f0ca883` + `demo_final.zip` shipped, human test
PASSED). Tree was clean at M1 sign-off when M2 opened.

## Scope (`DESIGN.md`:52 — "M2 — the campaign roster")

- All **5 PCs** with classes, signature attacks, and **rank-4 subclass evolutions** (Stormcaller /
  Viperkiss / …; `DESIGN.md`:17 "3 ranks, then an evolution = subclass choice", data-driven).
- **Recruitment wiring** for the whole roster (M1 shipped one join beat: Mosiah).
- **Meta-progression**: unlock characters, who can be recruited. The starting-party-size knob
  (1→2→3) is **deferred** per the user.

Progress this pass: **5 of 5 roster PCs in play** — Baldrun + Mosiah (M1) + **Asper** + **Alvora** + **Yu**.
The stage **beat loop**, the **5-slot picker** and the escort-cage join path carry all five, and the
roster is now complete (meta/unlocks and the party-size knob stay deferred).

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
  armour (flat −1/−2/−3, floor 1). Damage 10, HP 80. **LANDED this pass** (`Main.tscn` `memberIndex = 3`,
  `weaponKit = "aura"`, `class_ = "Twilight Cleric"`). Sanity defaults below are **assistant-designed**
  (the user dictated only damage and HP): ring 96 px, tick 1.15 s, baseSpeed 124 px/s — vetoable.
- **Yu** — Tortle Occultist. Summons small creatures spawned near himself that run to an enemy and
  **explode for AoE damage**. Upgrades: damage, explosion AoE, summon rate, multiattack. Minion
  damage 20 (not reduced by caught enemies), HP 70. **LANDED this pass** (`Main.tscn` `memberIndex = 4`,
  `weaponKit = "summon"`, `class_ = "Occultist"`). Sanity defaults below are **assistant-designed**
  (the user dictated only damage and HP): ritual sight 320 px, cooldown 1.6 s, familiar speed 84 px/s
  (a hair under a goblin's 90), blast 56 px, fuse 6 s — vetoable.
- Projectiles, rays, summons and AoEs are all in scope eventually.

## Landed this pass (uncommitted → committed this commit)

- **Asper's kit**: `[Export] weaponKit` ("melee" | "shot") dispatch in `PartyMember.cs` replaces the
  `arcHalf > 0.5f` two-branch verb switch; `arcHalf` is now **purely a melee property**. Shot branch
  aims at the closest body in sight (bosses included), spawns into a **party-owned shot pool**
  (`_shots`, `Game.cs`:233/1181/1894, mirroring the boss-owned `_bullets`), multishot fans extra
  bolts off the same commit (`PartyMember.cs`:470, ±0.16 rad/bolt). Telegraph armature is gated
  `if (!shotKit)` — a bow shows its bolt in flight, not a fan of ground spokes.
- **Alvora's kit = `weaponKit = "aura"`** (the THIRD verb, so kit dispatch now has three arms): the
  commit path in `PartyMember.cs` runs a ring sweep **before** the melee/fan target search, and every
  body inside `range * rangeMul` takes the **FULL** `weaponDamage * dmgMul` — **no split by count**, the
  exact opposite of the maul's cleave rule, which is what makes the cleric the anti-swarm verb. An
  **empty ring burns nothing**: it returns without setting `cdTimer`, so a cleric standing over empty
  clearing never stalls its own beat; a live commit sets `cdTimer = cooldown * cdMul`. That is what makes
  "wider ring" the same `rangeMul` plumbing as every other reach track and "quicker pulse" the same
  `cdMul` plumbing as every other speed track. Bosses live in the same `GetEnemies()` array (`RegisterEnemy`
  takes them, they only skip the horde cap) and `Boss.cs:13` is `class Boss : Combatant`, so the ring
  **can** trade with a live boss — without which the beat loop could never advance past one.
  Visuals are node-only as everywhere else in 4.7: 6 motes orbiting under one `Guardians` pivot (Scale
  carries the ring rank) with a flare on each pulse. The **telegraph armature is now hidden for any
  non-melee kit** — the idle visibility site only tested `!hidden`, so Asper's 260 px spoke fan and
  Alvora's ring both inherited a melee fan that no swing ever explained.
- **Warding light = `public int armour`** ranks, a **FLAT** incoming-damage reduction applied in
  `Game.TakeHit` (the one place party damage lands: bullets, contact, boss volleys) with a **floor of 1
  per hit**, so armour can never turn a swarm into a free pass. Tracks are picker options **10–13**
  (`spirit brand` / `wider ring` / `quicker pulse` / `warding light`) on a new `OPT_ARMOUR` array.
- **Yu's kit = `weaponKit = "summon"`** (the FOURTH verb): a commit spawns familiars **near Yu** in a
  shallow fan into a **party-side summon pool** (`_summons` + parallel arrays in `Game.cs`, 64 pooled
  slots — an **implementation bound, not a gameplay cap**: no cap per the user, "they won't last long").
  Each familiar steers at the closest live body on the **short torus path** (`WrapDelta`) and **detonates
  on contact OR when its 6 s fuse burns out** — the fuse IS the lifetime (user rule 2026-10-22), so no
  familiar can be parked under a boss and saved for later. The blast reuses the cleric's no-split rule:
  **FULL damage to every body in radius**. Familiars bite bosses exactly like bolts do
  (`hitR = 16.0f + ((Boss)bestC).radius`) — the beat loop needs that bite to advance. Ranks reuse the
  ordinary plumbing: picker options **14–17** (`occult flame` = `dmgMul`, `wider burst` = ONE `rangeMul`
  rank widening BOTH ritual sight and blast radius, `quicker ritual` = `cdMul`, and `twin ritual` = the
  multishot rank doubling as **multiattack**, one more familiar per commit).
- **Ring seats are now a table** (`RING_SLOT = { 0, 3, 2, 4, 5, 1 }` keyed by `memberIndex`): the old
  math folded `memberIndex` 1 and 3 onto the same angle, so a fourth PC welded straight onto Mosiah.
  M1's hand-placement is kept verbatim (Mosiah seat 3, Asper seat 2), Alvora takes seat 4.
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
- **Stall closed - CheckTpk two-way rule (user 2026-10-22)**: TPK fires when every joined member is
  ELIMINATED (the old rule), **OR** when nobody can act at all — every joined member is OUT or **down-STABLE**
  (rolls stopped, so the dice cannot save the run either; "the run is basically over without the leader").
  Needs a **per-frame call** in `_Process` gated on `PLAY` (a motionless stable party takes no damage events,
  so damage-site `CheckTpk` calls could never fire — that was the 500+ s `smokeM2w` stall; the `PLAY` gate
  keeps it from overwriting a same-frame VICTORY). Guard order matters: **eliminated first** — eliminated
  members keep `stable == false`, and a stability test ahead of the elimination test re-broke the classic
  TPK (caught by the reaper smoke that ran to the frame cap with everyone OUT).

## Needs the user's word (blocking, not guessed)

- **Tier-3 / tier-4 creatures are undefined in the repo.** The 150 s / 210 s escort beats currently run
  **tier-2 bodies with an elite HP bump** (×1.3 tier 3, ×1.6 tier 4) — flagged placeholders, no invented
  creatures. The user must dictate the real ones.
- **The 210 s beat's spelling**: the user wrote "tier-5 spawns" while 150 s is tier 3 — almost certainly a
  typo for **tier 4** (and the table is built that way), but confirm.
- **Rank-4 evolution shape**: **parked** (user 2026-10-22: needs more thinking, not deciding yet) — none of
  the new PCs has named evolutions, and nothing here should force the shape until it is dictated.
- **Bolt-on-boss** is a decision I made to unblock the beat loop (bosses are enemies and the loop cannot
  advance past a live boss) — veto if the longbow should not trade with bosses.
- **Party members reviving a downed leader** is **future work** (user 2026-10-22: "at some point" — it
  helps when the leader is down but allies are up, and rescues nobody already out). Until it lands, an
  up body (or one still rolling saves) is the only thing keeping a run alive. The TPK rule closing the
  stall **landed this pass** — see "Landed this pass".

## M2 backlog / open items

- **Roster complete at 5 PCs**: Yu landed this pass. What remains roster-wise is the **rank-4 evolution
  shape** (below in "Needs the user's word") — none of the new PCs has a named evolution yet.
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

- `smokeM2r` - **camp skip, Alvora build**: **VICTORY**, 0-byte `.err`. The roster line prints all four
  PCs (`Baldrun (Maul) + Mosiah (Rapier) + Asper (Longbow) + Alvora (Guardians)`); Alvora is delivered on
  the 150 s ally-III row (`stranded 1051 px out` + **6 tier-3 escorts** → `join: Alvora returns at
  80/80hp`), and the 210 s ally-IV row logs the honest **`ally 4 skipped - no such PC waiting in the
  roster yet`** (Yu is still only dictated). The camp card offered `Alvora Guardians: warding light
  (armour) (1/3)`, which is the new `OPT_ARMOUR` track being live in the picker, not just present in
  source.
- `smokeM2s` - join skip, Alvora build: **TPK** terminal, 0-byte `.err` — same shape as `smokeM2p`
  (stopped inside the `mini1` fight with the boss-live hold respected; `mini1` 66hp → 58hp while only
  Baldrun + Mosiah were live).
- `smokeM2t` - plain pilot, Alvora build: **TPK** terminal, 0-byte `.err` (party wiped during `mini1` at
  level 6, `live enemies 37`).
- `smokeM2u` - camp skip run **with a temporary boss-strike print added to the aura branch** (added → run
  → removed → rebuilt): 7 pulse lines, all inside the `final` march, each `N bodies, 1 of them boss`
  (the last `2 bodies`), and the final boss closed at **70 hp** on VICTORY. This is the direct proof that
  the guardian ring bites a live **boss**, which is the verb the beat loop needs to advance at all —
  **the print is not in the shipped code**, and `smokeM2r`/`s`/`t` are the trails for the shipped build.
- **Gate for the Alvora build is green**: `smokeM2r` (camp) VICTORY, `smokeM2s`/`t` (join / plain) TPK,
  all three `.err` 0 bytes.
- `smokeM2ver` — camp skip **with temp verification deltas** (ally IV beat 210→160 s, `ESCORT_GIVEUP`
  55→6 s, temp blast print in `DetonateSummon`): **VICTORY**, 0-byte `.err`. The familiar verb **committed**:
  27 blast lines, Yu joined (`RECRUIT JOINED: Yu ... ring slot 4 opens`), and the boss bite is direct —
  `12 bodies, 1 of them boss` and `1 bodies, 1 of them boss` ×2 during the **FINAL BOSS** march. The picker
  offered Yu tracks (`occult flame` / `wider burst` / `quicker ritual`, rank (1/3)). The deltas were
  **added → run → reverted**; the print is not in the shipped code.
- `smokeM2v` — camp skip, **Yu build, shipped values**: **VICTORY**, 0-byte `.err`. Roster line prints all
  five; the 210 s ally IV row fires with 6 tier-4 escorts, Yu still stranded at VICTORY (the 55 s valve
  loses the race there) — roster/loop evidence; the verb itself is gated by `smokeM2ver`.
- `smokeM2w` — join skip, Yu build: **VOID — SOFT-LOCK**: mini1 march stalled at t=525 with Baldrun **STABLE**
  down (rolls stopped per the locked rule), Mosiah OUT, mini1 frozen at 58 hp, beats held by the live boss,
  TPK cannot fire (downed ≠ eliminated). Nothing Yu-related is involved — the 18-option OPT table shifted
  pilot RNG into this branch. Recorded above in "Needs the user's word".
- `smokeM2w2` — join-skip retry (fresh RNG): **TPK** terminal, 0-byte `.err` — clean wipe, all joined
  members eliminated, no stall.
- `smokeM2x` — plain pilot, Yu build: **TPK** terminal, 0-byte `.err`; beats fire in table order (30 ally →
  50 tier-2 gate → 70 mini1), party wiped during the mini1 march.
- **Gate for the Yu build is green**: `smokeM2v` (camp) VICTORY, `smokeM2w2` (join) TPK, `smokeM2x` (plain)
  TPK, all `.err` 0 bytes; verb-bite evidence carried by the temp-delta `smokeM2ver`.
- `smokeM2y` — reaper ON (`M1_REAPER=1`), the TPK-rule edit **pre-guard-fix**: everyone OUT yet **no terminal**
  ran to the frame cap — direct evidence of the guard-order defect (stability test returned for eliminated
  members). Superseded by `smokeM2y2`.
- `smokeM2y2` — reaper ON with **temp rule print**: **TPK**, 0-byte `.err`, short trail. Baldrun stabilizes
  (rolls 14, 15 → STABLE) and the line `(verif) TPK fired: stall rule (stable-down present)` lands **at the
  stabilization moment** — the stall state itself ends the run, not a damage race. Print removed → not in
  shipped code.
- `smokeM2z` / `smokeM2z2` / `smokeM2z3` — camp / join / plain, **shipped build with the new rule**:
  **VICTORY, TPK, TPK**, all `.err` 0 bytes; the plain trail walks 30→50→70 in table order and TPKs during
  the mini1 march.
- **Gate for the TPK-rule build is green**: `smokeM2z` VICTORY, `smokeM2z2`/`z3` TPK, all `.err` 0 bytes.

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
- **A new roster PC is not just a scene node.** The follow-ring math (`memberIndex` → angle) folded seats
  1 and 3 onto the same angle, so a 4th PC welded onto Mosiah and every ring/AoE read from that body became
  ambiguous. Seat **tables** beat arithmetic when the seat count is small and hand-placed.
- Kit-visibility gates must be written at the **idle** site, not only at the commit site: the telegraph
  pivot was hidden only by `!hidden`, so a non-melee kit inherited a permanent fan of ground spokes at its
  authored alpha (Asper's 260 px fan, Alvora's ring). A commit-site `if (!shotKit)` does not cover frames
  where nothing committed.
- A verb whose damage route never touches a boss cannot advance the beat loop, and a smoke that VICTORYes
  does **not** by itself prove the new verb bit the boss. Print the interaction once (temporarily), gate on
  it, then remove the print - a VICTORY shared with the other trails is otherwise ambiguous.
- PowerShell line-splices: `$all += , @(...)` **nests** an array, and `-join` then renders literal `System.Object[]`
  **into the source file** — the intended lines are LOST and the file is corrupt. Append scalars individually,
  never a nested array. And a `$ls = @()` slip before `-join` writes an **empty file**: recover via
  `git checkout HEAD -- <file>` only if the file's uncommitted pass can be re-applied — check `git status`
  and file size **before** any reflex restore, since a restore discards the whole uncommitted pass.
- `(Get-Item f).Size` prints empty on a `FileInfo` in this shell; use `.Length` (and/or raw content length)
  for the 0-byte `.err` gate. `-like '*System.Object[]*'` throws (`[]` is an invalid wildcard class) —
  use `-match 'System\.Object'`.
- Dev-skip smokes are **unseeded-RNG trials**: a soft-lock branch (see the gate-trail note) can void one
  trail while the retry passes. Treat a no-terminal trail as VOID and retry, but record the stall if it is
  a rules hole the shipped game can reach.
- A new terminal rule that is meant to **subsume** an old rule must still pass the old rule's own case
  end-to-end: eliminated members keep `stable == false`, so testing "still rolling" before "eliminated"
  silently re-opened the classic TPK. Symptom to trust: everyone `OUT` in every march strip yet no terminal
  line until the frame cap.
