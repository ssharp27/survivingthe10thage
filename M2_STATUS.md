# M2 STATUS - **IN PROGRESS** (opened 2026-10-22; join lottery + stage label + picker re-wild landed this pass)

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

## Stage beat loop — user RE-LOCKED 2026-10-23 (second lock: more scaling runway)

Still **data on the run clock**, `Game.cs` `BEAT_T` / `BEAT_KIND` / `BEAT_TIER` / `BEAT_BOSS`,
**10 rows**. The stretch again because the human playtest wiped too often — the party gets more
levels between fights before every gate:

| run s | row | what fires |
| --- | --- | --- |
| 30  | ally I        | **lottery draw 1** joins (escort cage) |
| 60  | tier-2 gate   | horde tier 2 |
| 120 | mini-boss I   | `mini1` — its kill opens the camp |
| 150 | ally II       | **lottery draw 2** joins + **6 tier-2 escorts** |
| 180 | boss I        | `boss1` (mid boss) — its kill keeps the horde marching |
| 210 | ally III      | **lottery draw 3** joins + tier-3 escorts ("we can define them now") |
| 240 | mini-boss II  | `mini2` — its kill opens the camp |
| 270 | ally IV       | **lottery draw 4** joins + tier-4 spawns — **spelling to confirm** (user wrote "tier-5") |
| 360 | final boss    | `final` — its kill ALSO keeps the horde marching (no instant VICTORY) |
| 420 | WRAP-UP       | kind 5: run clock lands on it with a clear arena → **VICTORY** |

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

- **Baseline 5% evasion for every PC (user follow-up 2026-10-24)**: one seed-table move in `StatTags.cs`,
  `BASE[EVASION]` 0 -> 0.05; the TakeHit dodge roll was already gated on `stat > 0`, so all five roster
  members dodge from run start with no other code touched. Docs registry table re-aligned; per-PC evasion
  grants would write the SAME tag slot later.
- **Stat-tag registry + tag-backed stat store (user 2026-10-24 stat plan)**: `src/StatTags.cs`
  (NEW, data-only) owns the **one canonical vocabulary**: 16 tags, indices 0..15 (the user plan plus
  the accepted 16th, `vulnerability`), each with `BASE` seed, `CATEGORY` (offense x7 / defense x4 /
  utility x4 / modifier x1) and `KIND` (0 = multiplier, 1 = points, 2 = count). `PartyMember.cs`
  now keeps the **single store** `stat = new double[StatTags.N]` seeded via `SeedStatStore()`
  before `hp = (float)EffMaxHp()`; the old per-stat fields `dmgMul/cdMul/rangeMul/speedMul` and
  `int shots` are GONE - every reader goes through `EffDamage/EffCooldown/EffRange/EffMaxHp/EffMove/
  Strikes()` and `RollDamage` (crit rolls on the run Rng: ALL party crits at 5% base for x1.5,
  horde never does; lifesteal feeds the body back capped at `EffMaxHp()`; REGEN ticks in `_Process`
  while standing and joined). `Game.TakeHit` grew the defence tags: EVASION full-dodge roll BEFORE
  the armour flat-minus, never-below-1 floor (user 2026-10-24). OPT commits now write TAG SLOTS
  (`ATTACK_DAMAGE` x, `ATTACK_SPEED` /cd - the cooldown lives INSIDE the speed tag, `AREA` x,
  `MULTISTRIKE` +); magnet x `COLLECTION_RANGE`, gems x `XP_PCT`; `Game.Rng` went public so
  `RollDamage` rolls on the run stream. HORDE side (Enemy/Boss) keeps its own numbers: tags are
  party-side only, plus the body-side vulnerability pair (`Combatant.vulnMul`). Docs: `docs/stat-tags.md`;
  the boot dump (16 `stat tag ...` rows after `Rng.SetSeed(9001)`) is the contract the trail greps.
- **Alvora verb swap (user 2026-10-23)**: warding light (flat −1/−2/−3 incoming armour) is **gone**,
  replaced by **guiding light** — her ring pulse also MARKS every body it catches: +10%/rank damage
  taken **from every source** for a 4 s window. Mark lives on the shared `Combatant` surface
  (`vulnMul`/`vulnT`, `Vulnerable()`, `TickVuln()` decay called from `Enemy._Process` and
  `Boss._Process`), so melee bolts rings and future verbs get the setup for free; stronger mark
  overwrites, window only refreshes (ranks never multiply). `Game.TakeHit` lost its armour block
  — the squad now has **zero flat mitigation**, the deliberate cost of the swap. Picker label +
  `OPT_VULN` table renamed; property `armour` -> `guidingLight` on the member.
- **Join lottery (user 2026-10-23: dynamic runs)**: which PCs show up as recruits and in which
  order is now random per run. `Game.cs` gained a stage-weighted table `JOIN_W` (row =
  `STAGE_NUMBER - 1`, columns memberIndex order; stage 1 = Baldrun 3 / Mosiah 3 / Asper 1 /
  Alvora 1 / Yu 1 — **sanity numbers, user will add rows per future stage**). `BuildJoinLottery()`
  draws once at boot (weighted, **without replacement**, leader excluded via `isLeader` — the
  table itself is leader-agnostic so any PC can show up on runs where another one leads).
  `JOIN_W` is a NEW-dimension initializer — old-style nested braces trip CS0623 in Godot's C#
  build. `_joinRng` is deliberately **unseeded**: every smoke / human run re-rolls (the trail
  prints `join lottery (stage 1): X > Y > Z > W` at boot so gates stay honest). A dry pool
  leaves the slot `-1` = logged no-op. Ally beats now look up `_joinOrder[_allyBeatIdx]`.
- **Stage label (user 2026-10-23)**: `STAGE_NUMBER = 1` beside `STAGE_NAME`; HUD and the
  VICTORY summary print `Tanurial Forest (stage 1)` — future levels just bump the constant.
- **Picker re-wilded (user 2026-10-23: was showing one upgrade per character only)**: card is
  back to M1's shape — **one still-open track per member in play PLUS one fully-wild slot**
  (random open track from ANY member, never a repeat on the card). `PICK_SLOTS = 6` (keys
  `pick_1..pick_6`; the `pick_6` keycode 54 was added to `project.godot`'s input map).
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
- **HUD clock is the RUN clock, always (user 2026-10-22)**: all three display sites (plain HUD, picker
  card, pilot strip) print `t=` from `GetRunTime()` (`_runT`) instead of `GetStageT()` - the leg clock
  RESETS every beat row, and that reset made the first mini-boss read as "way too early" (human
  complaint). `GetStageT()` survives only as the horde's density-ramp input (pacing DATA per leg,
  never displayed). Beat times themselves were always honest run-seconds.
- **Horde scales to party size (user rule, human playtest 2026-10-22)**: solo Baldrun was swamped by
  a full-sized horde. `Director._Process` now multiplies BOTH volume dials by `partyScale =
  GetPartyInPlay() / 2.0` (live cap AND cadence; normalized so M1's 2-PC pacing is 1.0 - solo 0.5x,
  5 PCs 2.5x, applied after the leg ramp and easing, stacked with the rest scale). `GetPartyInPlay()`
  (`Game.cs`) counts JOINED, not-eliminated members, floored at 1. Body HP deliberately keeps its
  rest-only ×1.20 price - small squads face FEWER bodies, not fatter ones. Full session record in
  **`docs/horde-party-scaling.md`** (shipped alongside - the verbatim block there matches the code
  byte-for-byte, line numbers included).
- **Beat loop re-locked + sandbox window (user 2026-10-23, twice - final lock below)**: every gate stretched (30/60/90/120/150/180/210/240/300) because
  the human playtest wiped at the old 120 s mid boss with only two PCs. Later the SAME day the user
  stretched again to **30/60/120/150/180/210/240/270/360 + 420 wrap** for more scaling runway.
  The **WRAP-UP row (kind 5)** is what raises VICTORY: `final`'s kill no longer ends the run (`OnBossKilled` final
  branch keeps the horde marching), and the wrap-up row is held back by `BossPending()` until the
  arena is clear, so "cleared" only prints once `final` is truly slain and the party gets a full
  sandbox at tier-4 / 5-PC strength before the stage closes.
- **Baldrun maul no longer splits (user 2026-10-23)**: damage stat 30 → **20** (`Main.tscn`) and the
  cleaver branch in `PartyMember._Commit` applies **FULL damage to every body in the fan** - the old
  `dmg / hits` made the maul weaker than every other verb once a few bodies crowded the group. The
  fan (auto-aim centered on the closest body) is unchanged.
- **Asper sprite (user 2026-10-23, `art/asper.png`)**: run through the same art pipeline as Baldrun/
  Mosiah - headless `--import` pass made the `.import` sidecar, then `tools/artcheck.gd` audit **passed**:
  80x119, opaque bbox 78x117 at 1,1 (1px margins), `cx_delta` -0.5, 0 soft-alpha px, 61 opaque colours.
  Wired into the `displayName -> miniPath` lookup in `PartyMember._EnterTree`; `miniScale 0.75` and
  `miniRim 7.0` carry over unchanged (same ~118-119 canvas class as the other two).
- **Alvora sprite (user 2026-10-23, `art/alvora.png`)**: same art pipeline - headless `--import` made
  the sidecar, `tools/artcheck.gd` audit **passed**: 94x119, opaque bbox 92x117 at 1,1 (1px margins),
  `cx_delta` -0.5, 0 soft-alpha px, 61 opaque colours. Wired as the 4th `displayName` branch; scale/rim
  carry over (all four canvases are the ~118-119 class).

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
- `smokeM2za` (plain), `smokeM2zb` (camp), `smokeM2zc` (join) — **horde-party-scaling + run-clock build**:
  **TPK, VICTORY, VICTORY**, all `.err` 0 bytes. `za` carries the rule evidence twice over: every `t=` column
  now climbs monotonically (mini1 honestly at beat 70 s, no per-leg reset), and the solo opening runs THIN
  (`t=15`: 4 live vs 7 pre-change; solo mini phase 12 live at half-density) then thickens the moment Mosiah
  joins (19→27→39→40 at the 2-PC cap) - density follows party size live. `zc` join-skip WON this roll
  (unseeded-RNG trial: earlier `w` stalled, `w2` TPKed, `z2` TPKed, `zc` cleared - terminal reached every
  time, which is what the stall rule guarantees).
- **Gate for the party-scaling + run-clock build is green**: `smokeM2za` TPK, `smokeM2zb` VICTORY,
  `smokeM2zc` VICTORY, all `.err` 0 bytes.
- `smokeM2zd2` (plain, 90 k frames), `smokeM2ze` (camp skip), `smokeM2zf` (join skip) — **beat re-lock + wrap-up + Baldrun de-split build**:
  **VICTORY, VICTORY, VICTORY**, all `.err` 0 bytes. The schedule prints fire on the NEW rows in every
  trail (30/60/90/107-camp/…/210/240/300), the new `final`-kill line "the run keeps rolling to the
  400s wrap-up beat" appears in all three, and VICTORY lands at the 400 s WRAP-UP row with the
  sandbox proven live (final slain ~325-355 s, horde marching tier-4 at 2.5× party density, live
  36→55 bodies, through 385 s). Note the 60 k-frame cap is now SHORT of the stage (plain run
  expired ~330 s without a terminal in `zd`) — the sandbox window needs 90 k frames.
- **Gate for the beat re-lock + wrap-up + Baldrun de-split build is green**: `smokeM2zd2` VICTORY,
  `smokeM2ze` VICTORY, `smokeM2zf` VICTORY, all `.err` 0 bytes.
- `smokeM2zg` (plain, 120 k frames), `smokeM2zh` (camp skip), `smokeM2zi` (join skip) — **second beat
  re-lock (420 s wrap) build**: **VICTORY, VICTORY, VICTORY**, all `.err` 0 bytes. `zg` cleared at
  level 24 / xp 1244 with all five PCs down before the wrap — the arena was clear so the wrap row
  honestly raised VICTORY (the sandbox window did its job even for a dead pilot party). 120 k frames
  now cover the longer stage comfortably.
- `smokeM2zg2` (plain, 120 k frames) — **gate trail on the shipped binary** (after de-hardcoding the
  final-kill print and comments to stop naming the wrap time): **VICTORY** level 24 / xp 1222, 0-byte
  `.err`, and the kill line now reads "the run keeps rolling to the wrap-up beat" with no baked time.
- **Gate for the second beat re-lock build is green**: `smokeM2zg2` (shipped) VICTORY + `smokeM2zh`
  (camp) VICTORY + `smokeM2zi` (join) VICTORY, all `.err` 0 bytes.
- `smokeM2zj` (plain, 120 k frames) — **Asper art wiring build**: **VICTORY** level 24 / xp 1222, 0-byte
  `.err`. Asper's `GD.Load` of `res://art/asper.png` runs at join time (150 s row) and the run stays
  error-free end to end, so the sidecar import + audit wiring are live in the shipped binary.
- `smokeM2zk` (plain, 120 k frames) — **Alvora art wiring build**: **VICTORY** level 24 / xp 1244, 0-byte
  `.err`. Alvora joins at the 210 s row (its `GD.Load` runs at join time), ring-slot-3 opens, and the
  run stays error-free to the wrap, so the new sidecar + 4th lookup branch are live.
- `smokeM2zo` (plain, 120 k frames) — **join-lottery + stage label + picker re-wild build**: **VICTORY**
  level 24 / xp 1247, 0-byte `.err`. Boot prints `join lottery (stage 1): Yu > Mosiah > Alvora > Asper`
  and the four ally beats recruit EXACTLY that order (30s Yu / 150s Mosiah / 210s Alvora / 270s Asper)
  — the earlier off-by-one is gone, no ally row skips, and the VICTORY line reads "Tanurial Forest
  (stage 1) cleared". Picker cards now show per-member slots + the wild (six-key ceiling live).
- `smokeM2zp` (camp skip) — **VICTORY** level 16 / xp 581, 0-byte `.err`; its boot lottery
  re-rolled to `Yu > Mosiah > Asper > Alvora` (different from `zo` — the unseeded `_joinRng`
  re-rolls per run, as designed).
- **Gate for the join-lottery + stage-label + picker re-wild build is green**: `smokeM2zo` (plain)
  VICTORY + `smokeM2zp` (camp) VICTORY + `smokeM2zq` (join skip) VICTORY, all `.err` 0 bytes.
- `smokeM2zr` (plain) / `smokeM2zs` (camp) / `smokeM2zt` (join) — **guiding-light verb swap**: all
  **VICTORY**, 0-byte `.err`; picker cards render "guiding light (ring vulnerability)" (the pilot
  never ranks the Alvora slot, so those trails alone never exercise the mark).
- `smokeM2zu` (join skip) — **temp-verification run**: boot handed Alvora two guiding-light ranks
  and Combatant printed marks + amplified hits: **527 `guiding mark` applications** and **726
  amplified hits at x1.2 including on the `final` boss** (11.5→13.8, 21.5→25.8, 16.5→19.8) — the
  mark bites OTHER sources, not just the ring. VICTORY lv8, 0-byte `.err`.
- **Gate for the guiding-light build is green** (temp lines removed, rebuild): `smokeM2zv` (plain)
  VICTORY lv24 / xp 1243 + `smokeM2zw` (camp) VICTORY + `smokeM2zx` (join) VICTORY, all `.err` 0,
  **0 VERIFY lines in the shipped binary**, and the plain trail's lottery `Asper > Yu > Mosiah >
  Alvora` matches its four ally beats one-for-one.
- **Gate for the stat-tag store build is green**: `smokeM2zy` (plain) VICTORY lv24 / xp 1272 +
  `smokeM2zz` (camp) VICTORY lv16 / xp 593 + `smokeM2zy2` (join) VICTORY lv9 / xp 191, all `.err` 0
  bytes, **16 `stat tag` rows each** (the new registry audit), 0 VERIFY. Shape note: every party hit
  now consumes one `Rng` draw (5% base crit), so the horde stream shifted - all three trails show
  post-wrap elimination lines (the horde cleans near-dead bodies after the win), win line itself
  unchanged and present before the frame cap.
- `smokeM2zw1` (plain), `smokeM2zw2` (camp skip), `smokeM2zw3` (join skip) - **baseline-evasion build**:
  all 16 `stat tag` rows print with `evasion [defense kind 1 base 0.05]`; plain VICTORY lv24 / xp 1245 (50
  dodge lines) + camp VICTORY lv16 / xp 576 (45 dodges) + join-skip VICTORY lv9 / xp 190 (37 dodges),
  all `.err` 0 bytes, 0 VERIFY. The dodge rolls now consume the run Rng too, so the horde stream shifted
  again - wins unchanged, levels drift by one as expected.
- `smokeM2zx1` (camp skip), `smokeM2zx2` (join skip), `smokeM2zx3` (plain) — **draging-maul +
  deadly-aim upgrade-card build**: **VICTORY, VICTORY, VICTORY**, 0-byte `.err`, 16 `stat tag`
  rows each. The picker now offers Baldrun's `draging maul` (kit field + body slow via
  `Combatant.Slowed`, bosses immune at the applier) and Mosiah's `deadly aim` (+8%/rank
  CRIT_CHANCE, pure data). Crit rolls already consumed the run RNG from the stat-tag pass, so
  the horde stream shifted; the slow itself adds no draws.
- `smokeM2zx4` (camp skip), `smokeM2zx5` (join skip), `smokeM2zx6` (plain) — **HUD join-order strip
  build**: **VICTORY, VICTORY, VICTORY**, 0-byte `.err`, 16 `stat tag` rows each. Bug fix (user 2026-10-24):
  strips drew fixed roster rows, so a Yu-first recruit painted the bottom row with a three-row gap
  above. Now strip slot i paints the i-th PC to ARRIVE via `_hpOrder` (leader + the boot-drawn
  lottery sequence, built once in `BuildJoinLottery`); the STRANDED preview rides the next strip
  because `_recruitPending` is always that strip's drawn member; dry-pool trailing slots stay
  invisible. Storage stays roster order (`memberIndex` = ring slot, picker cards unchanged) -
  display-only change, no RNG consumed, so beat trails keep the cards-build shape.
- `smokeM2zx7` (camp skip), `smokeM2zx8` (join skip), `smokeM2zx9` (plain) — **camp-menu roster
  build**: **VICTORY, VICTORY, VICTORY**, 0-byte `.err`, 16 `stat tag` rows each. Bug fix #2 (user
  2026-10-24): the camp menu only showed the first 2 characters — `CampfireCards()` sent joined
  members ONLY, so early camps painted leader + first recruit and the 3-col x 2-row mini-card grid
  had nowhere for the rest. The camp now lists the WHOLE roster top-to-bottom in join order on the
  same `_hpOrder` map the HUD strips use: an unrecruited PC reads "not recruited yet" (dimmed, grey
  accent), and every row carries its upgrade picks (`pickHistory`, same source the run summary
  reports) plus the current weapon — the user's ask. `ui/Campfire.tscn` `PartyGrid` went 3 cols ->
  1 col; `ui/Campfire.gd` mini-cards (284x100 VBox stack) became 880x32 single-row strips
  (Accent/Name/LeaderTag/BarWrap/HPText/State/Weapon/Picks) so 5 rows x 32 px + 4 x 14 sep = 216 px
  keeps the old vertical budget inside the 592 px inner frame (rest row + commit button keep
  clickable rects — human-test rounds 1-2 rule). Data side gained a `CampCard()` helper ("away"
  state + "picks" key); `SummaryCards()` keeps its joined-only filter ON PURPOSE (run report = who
  fought, camp = whole roster). No RNG consumed — display-only, trails unchanged.
- `smokeM2zx10` (camp skip), `smokeM2zx11` (join skip), `smokeM2zx12` (plain) — **down-grace build**:
  **VICTORY lv15/xp536, VICTORY lv8/xp149, VICTORY lv24/xp1233**, 0-byte `.err`, 16 `stat tag` rows
  each. QOL add (user 2026-10-24): a fresh DOWN now opens a 4 s window where enemy hits can never
  consume a death-save fail - swarm mercy for a body downed mid-rush. Rides the EXISTING silent lane:
  both down sites (TakeHit hp<=0, OnReaperDown) arm `dsDamageBuffer` to the new `DS_DOWN_GRACE` = 4.0
  instead of zeroing it, so hits inside the window just return - no fail, no chip text, no extra flash,
  no HUD (user asked for no display at all; downed bodies never lose HP, so window hits are inert).
  Grace == `DS_INTERVAL` so the window covers exactly down -> first d20 roll; time-based saves roll as
  normal, only damage-caused fails are shielded, and the 2 s between-fail buffer (`DS_DAMAGE_FAIL_BUFFER`)
  keeps re-arming each post-window fail; join/campfire/revive sites stay zeroed (grace belongs to the
  fall, not the rise). Evidence: `smokeM2zxv` ran once with a TEMP buffer print showing countdowns up
  to "4s left" (the fail-buffer alone caps at 2.0), then reverted before the trio. No RNG consumed by
  the lane itself.
- `smokeM2zx13` (camp skip), `smokeM2zx14` (join skip), `smokeM2zx15` (plain) — **per-member XP
  suction build**: **VICTORY lv16/xp580, VICTORY lv9/xp191, VICTORY lv24/xp1250**, 0-byte `.err`,
  16 `stat tag` rows each. QOL add (user 2026-10-24): XP gems are no longer leader-only — every
  living PC owns its own `COLLECTION_RANGE` magnet bubble (nearest member wins on overlap so a gem
  rides ONE stream, no splitting), and pickup pays THAT member's own `XP_PCT` bonus. Downed/eliminated
  bodies and the stranded unjoined recruit own no bubble. The campfire beat's vacuum still reels
  gems to the leader UNCHANGED (a beat effect, separate lane). Evidence is directional rather than
  print-shown: XP totals rise against the grace trio (580/191/1250 vs 536/149/1233; the join-skip
  trail jumps from lv8 to lv9) because stragglers now vacuum their side of the arena, and all
  three trails keep VICTORY with clean `.err`. No RNG consumed by the suction itself.
- `smokeM2zx16` (camp skip), `smokeM2zx17` (camp skip, `M1_LEADER=Asper`), `smokeM2zx18` (camp skip,
  `M1_LEADER=Yu`) — **leader-bonus build**: **VICTORY lv15/xp535, VICTORY lv15/xp533, VICTORY lv16/xp552**,
  0-byte `.err`, 16 `stat tag` rows each. Each roster PC owns a signature perk while it holds the **leader
  seat** (user 2026-10-25; ultimates deferred): **Baldrun** — every party hit rolls 5% for chain lightning,
  up to **4 bounces of 10 damage** to the nearest UNTOUCHED living body (150 px hops, vis memo; visual is
  the shared hurt flash, drawn lightning deferred); **Mosiah** — party bolts and the rapier lane punch
  through **1 extra body** (`min(4, pierce + seat bonus)`, per-bolt budget + `_shotLast` memo of the last
  raked body so a bolt never re-rakes); **Alvora** — every standing ally regenerates **1 HP every 10 s**
  (own clock, clamped to `EffMaxHp()`; the dead `hp-regen` tag slot stays unused); **Asper** — friendly
  **insects spawn every 30 s**, walk contact (24 px) makes one temporarily ride above the claimant and
  fire the seat's bow — same range, same attack speed, **half damage** — for **10 s**; **Yu** — friendly
  **spirits spawn every 30 s**, and touching one heals **10 HP on one RANDOM ally who has actually lost
  HP** (a healthy squad never drinks the vigil). Lane clocks (`_insectClock`/`_spiritClock`/`_regenT`)
  charge ONLY while the owner leads; upkeep is unconditional so a helper already out keeps living.
  Evidence is printed: zx16 tail `lb chain 76` (other counters correctly 0), zx17 boot `(bench) leader
  seat moved to: Asper` + 7 `insect kindles` / 6 `insect joins` (`bound 5` at t=415), zx18 8 `kindness
  spirit kindles` / 4 `spirit heals ...` (`healed 4`). Bench valve `M1_LEADER` (pilot-gated) flips the
  seat BEFORE `BuildJoinLottery()`, so the flipped leader carries the arena spawn and marches first.
  Picks (not user-locked): 150 px hop range, 30 s waiting lifetimes, 24 px pick radius, 90-170 px spawn
  ring; the chain proc rolls once PER HIT LANDED (a cleave raking 3 bodies rolls 3x). Rng stream shifts
  downstream (proc/crit/heal draws) — lv/xp totals differ from the suction trio (535/533/552 vs 580/191/1250)
  while the beat schedule is untouched.
- `smokeM2zx19` (camp skip), `smokeM2zx20` (camp skip, `M1_LEADER=Asper`), `smokeM2zx21` (camp skip,
  `M1_LEADER=Yu`) — **ultimate-button build**: **VICTORY lv15/xp527, VICTORY lv15/xp533, VICTORY
  lv15/xp548**, 0-byte `.err`, **17** `stat tag` rows each — the registry grew 16→17 with tag
  16 `ultimate cooldown` (utility multiplier, seed 1.0) so meta progression can shorten the gate
  later (user 2026-10-25). **R** (`ult` action, keycode 82, appended to project.godot's `[input]`
  like the sibling `dev_*`/`dash` entries) fires the ultimate of the PC holding the **leader seat**
  if it owns one and its cooldown is ready. **Baldrun "Giant's Might"** is the only lane authored:
  root `Scale` ×2 (art and tele/aura pivots inherit; restore shrinks back exactly once via `_grown`),
  `EffRange` ×2 (maul reach AND struck radius — every hit-test reads `EffRange`), and `RollDamage`
  ×1.5 at the single party-side damage choke (all kits, shot pool, insect seats) — for **10 s**, the
  assistant pick (user named no window, documented here); the cleave arc *angle* stays untouched
  (already π-capped, "radius" read as reach + footprint). Gate `ULT_CD_BASE 120 s / stat[StatTags.ULT]`
  (user: 120 s, tag reserved for meta progression, cd-inside convention like attack speed); the
  per-PC `ultCdT` recharges on its own clock — bench ults stay ready when the seat flips — while
  `ultT`/scale countdowns pause with the world's soft pause (buff never decays with a card up).
  `TryUltimate` prints the reason on every press (`still cooling: N s` / `no ultimate yet`); the
  pilot mirror auto-fires ONLY when the leader owns a ready ult, so no-ult benches never spam.
  Fire itself consumes no RNG. Evidence is printed: zx19 shows 2 `(ult) Giant's Might` fires
  (~t=120 and ~t=300, readout `ult 1/2`, `cd 120` re-armed then ticking `105→15` to the tail),
  zx20/zx21 readouts hold `ult 0 cd 0` while their insect/spirit lanes run untouched and VICTORY
  holds. Readout appends `" | ult fired cd"` reading `_leader.ultCdT`.
- `smokeM2zx22` (camp skip), `smokeM2zx23` (camp skip, `M1_LEADER=Asper`), `smokeM2zx24` (camp skip,
  `M1_LEADER=Yu`) — **Yu roster art** (user dropped `art/yu.png` overnight): **VICTORY lv15/xp545,
  VICTORY lv15/xp540, VICTORY lv16/xp563**, 0-byte `.err`, **17** `stat tag` rows each; zx22 still
  auto-fires Baldrun's ult (`ult 1/2`, `cd 120` re-arm) and zx23/24 keep their insect/spirit lanes
  byte-identical in kind. Audit passed the class via the one-off `tools/artcheck.gd` (UNTRACKED —
  swap the path line, run `--headless --import` before auditing a fresh PNG): 85x114 canvas, hard
  alpha (`soft_alpha_px=0`), 61 opaque colours, 1 px margins, cx_delta −0.5. **BUT the plinth is
  deeper than the four**: the bottom row profile shows the base's bottom ellipse widest (centre) on
  row 96 — **17.5 px up from the canvas edge**, so `miniRim = 17.5f` is MEASURED, not the carried
  7 px (the rig-origin pin is the topple pivot AND the dash-plate anchor — carrying 7 would have
  sunk Yu's feet 10 px into the clearing). `displayName == "Yu"` branch spliced into the art
  lookup via the git-splice route; headless smokes never see pixels, the audit print IS the
  visual evidence for this gate. (Note: `tools/artcheck.gd` is **tracked**, despite the older
  gate's UNTRACKED claim — swap its path line for each audit but `git checkout HEAD --` it back
  after, so the one-off probe state never ships.)
- `smokeM2zx25` (camp skip), `smokeM2zx26` (camp skip, `M1_LEADER=Mosiah`), `smokeM2zx27` (camp
  skip, `M1_LEADER=Asper`) — **Mosiah "Bardic Inspiration" ultimate lane** (user: all allies +25%
  damage, +25% attack speed): **VICTORY lv15/xp541, VICTORY lv15/xp550, VICTORY lv16/xp557**,
  0-byte `.err`, **17** `stat tag` rows each. Party-wide means **per-body clocks**: `insT` rides
  the `_Process` tick block next to `ultT`/`ultCdT` so it pauses with the world's soft pause, and
  `TryUltimate` stamps it on every ally *including the caster* through a new `Game.InspireParty`
  walk (private `_party` lives on Game; the group API would wrestle Godot.Array bindings). The
  two multipliers reuse the ult machinery's existing chokes — ×1.25 damage inside `RollDamage`
  (all kits + shot pool + insect seats) and ×1.25 speed as an `EffCooldown` divisor, which is the
  cadence for **all four kits** because the aura pulse rides the weapon cooldown, so Asper's bow
  and Alvora's ring genuinely tick faster. Duration 10 s = assistant pick (user named no window,
  same class as `ULT_LASTS`); gate stays shared `ULT_CD_BASE 120 s / StatTags.ULT`. Evidence:
  zx25 keeps Baldrun byte-identical (2× `Giant's Might`, `ult 2 cd 15` tail), zx26 prints 2×
  `(ult) Bardic Inspiration` with the final boss cut visibly faster (205 hp left at t=370 vs
  zx25's 522 — the DPS signature), zx27 holds `ult 0 cd 0` silence while its insect lane runs
  (`bound 5`). No new RNG consumed; cadence shift re-bakes downstream streams (lv/xp drift
  legitimate). The R/pilot firing block needed no change — it keys generically on `ultKey() > 0`.
- `smokeM2zx28` (camp skip), `smokeM2zx29` (camp skip, `M1_LEADER=Alvora`), `smokeM2zx30` (camp
  skip, `M1_LEADER=Asper`) — **Alvora "Guardian Awakening" ultimate lane** (user: Spirit Guardians
  range x2 *riding range upgrades*, guardians +50% damage, heal-all +1 hp per horde kill while
  active): **VICTORY lv15/xp534, VICTORY lv15/xp488, VICTORY lv15/xp530**, 0-byte `.err`, **17**
  `stat tag` rows each. Three effects, three homes: **×2 ring reach MULTIPLIED AFTER the AREA
  tag** in `EffRange` (user asked for multiplicative-with-ranks explicitly; and the `Guardians`
  pivot scale got the same factor so the visible ring matches the doubled hit test — the only
  other place reading reach is the damage arc itself), **×1.5 damage at the `RollDamage` choke**
  (only Alvora ever sets `awaT`, so it is his ring exactly), and the **per-kill heal hooked in
  `Game.OnEnemyKilled`** non-boss path (boss kills route above and never count): each horde kill
  while any `awaT` is up heals every standing ally +1 with the *spirit-heal eligibility* (joined
  / not downed / not eliminated, clamp at `EffMaxHp`) and tallies `awaKills` on the owner; expiry
  prints a one-shot `settles: N horde slain` line guarded by `_awaUp` (same discipline as `_grown`).
  zx29 evidence: 2× `(ult) Guardian Awakening` + settle lines `0 horde slain` (first window,
  honest empty case) and `4 horde slain ... healed 4 hp each`; boss1 eaten faster (500→400→270→147
  hp at t=244-289). zx28 keeps Baldrun's lane firing identically (`ult 2 cd 15` tail), zx30 holds
  `ult 0 cd 0` silence while its insect lane runs (`bound 5`). No new RNG consumed (heals are flat
  +1); reach/damage shift re-bakes downstream streams (Alvora's lower lv/xp is the ring's slower
  natural DPS). PITFALL: first two edit-tool attempts on Game.cs **garbled the hook** (nested
  empty braces/stray semicolons) **and ate `c.kind = "dead";`** — repaired via a marker-matched
  python splice (`fixGame.py`: from the Awakening comment's foreach to the SpawnGem line, whole-
  line replace, c.kind restored); lesson: multi-line C# edits into Game.cs go through python
  splices, not the edit tool, when the payload has brace-heavy indentation.
- `smokeM2zx31` (camp skip), `smokeM2zx32` (camp skip, `M1_LEADER=Asper`), `smokeM2zx33` (camp
  skip, `M1_LEADER=Yu`) — **Asper "Swarm of Bees" ultimate lane** (user: 6 bees, 10 s, 10 dmg/s,
  latch one enemy at a time, spawn in a circle, spread out to hunt): **VICTORY lv15/xp543, VICTORY
  lv15/xp507, VICTORY lv16/xp578**, 0-byte `.err`, **17** `stat tag` rows each. The swarm lives
  entirely on Game: six-slot pool (honey-amber `ColorRect`, off every horde hue AND the insect
  lane's chitin) + `SpawnBees` fans slot i onto bearing i×60° (user's compass circle; **no Rng
  draws** — a fixed fan so the run stream stays put) + `UpdateBees` unconditional upkeep next to
  `UpdateInsects/UpdateSpirits`. Latch discipline: `_beeTarget` holds the **prey instance id** —
  dead bodies QueueFree, so a node ref would dangle while an id simply stops resolving and the
  bee unlatches, tallies `_beeDrained`, and re-hunts; hunting bees drift along their spawn ray and
  the **nearest body inside BEE_FIND 240 px** latches (spread ring + own-nearest = the fan
  searches all directions); latched bees ride (`BEE_LATCH 16`) and chomp whole seconds via flat
  `TakeDamage(10)` — no crit, no tag lane. One-shot dispersal print when `_beesUp` drops to 0
  (`_beeSwarmUp` guard, same discipline as `_awaUp`). zx32 evidence: 2× `(ult) Swarm of Bees` +
  dispersal tallies `0 prey drained` (first window at the mini-boss arena, honest empty case) and
  `88 prey drained` (tier-4 stampede; the tally is **heuristic latch-break attribution** — another
  hand may have landed the last blow, documented). zx31 keeps Baldrun's `Giant's Might` identical
  (`ult 2 cd 15`, no bee prints), zx33 holds `ult 0 cd 0` Yu silence with spirit lane running
  (`healed 2`). No Rng consumed; bee kills ride the shared kill scan so XP gems / escort meter
  stay enemy-owned (downstream lv/xp drift legitimate). PITFALL: the harness **kills the whole
  process tree when a background smoke exceeds an explicit timeout** — zx31 had finished, the
  trio died between gates before zx32's redirect even created its file; lesson: verify via
  `tasklist` no godot survives, then re-run the REMAINING gates from a fresh ps1 with **timeout
  0** (background commands have no timeout by default).
- `smokeM9zx34` (camp skip), `smokeM9zx35` (camp skip, `M1_LEADER=Yu`), `smokeM9zx36` (camp
  skip, `M1_LEADER=Asper`) — **Yu "The Kaka" ultimate lane** (user: 6 empowered familiars same
  as the regular summons, blast radius x2 and damage x2 scaling AFTER upgrades, spawn in a circle
  and spread out like the bees): **VICTORY lv15/xp551, VICTORY lv15/xp531, VICTORY lv15/xp527**,
  0-byte `.err`, **17** `stat tag` rows each. Zero new plumbing: the Kaka rides the ritual's OWN
  summon pool (64 slots; steering, fuse, contact/fuse detonation and the no-split blast untouched)
  via a per-slot `_summonKaka` flag reset at every spawn point (no DespawnSummon hygiene edit
  needed: every reuse path stamps the flag). `SpawnKakas` bakes `dmg*KAKA_DMG` / `radius*KAKA_R`
  AFTER the caller's rank scaling (PM passes the ALREADY-scaled kit numbers - same after-ranks
  discipline as AWA_RANGE after the AREA tag), fans 6 on a compass circle like `SpawnBees`
  (fixed bearings, **no Rng draws**), and seeds each familiar's velocity along its spawn bearing
  so an empty clearing still fans them outward (bee-style drift; the familiar's own nearest-body
  steering takes over the moment prey shows). `DetonateSummon` tallies caught bodies and prints
  ONLY for empowered slots. zx35 evidence: 2x `(ult) The Kaka` casts + six burst tallies per cast
  (`0 body(s)` at the mini-boss arena = honest empty case, `1 body(s)` per burst in the tier-4
  stampede); zx34 silent with Baldrun's lane identical (`ult 2 cd 15`), zx36 keeps the bee lane
  intact (`53 prey drained`, no Kaka prints). Splice lesson: Game.cs marker splice with **single-
  line anchors only** - the file carries pre-existing `\r\r\n` artifacts (161 stray CRs already in
  the HEAD blob), so global CRLF normalization is OUT and anchors must never cross a line ending.
  No Rng consumed; kaka kills ride the shared kill scan so gems / escort meter stay enemy-owned.

## Pitfalls added this pass (on top of the M1 catalogue)

- **Join-lottery index base**: `_allyBeatIdx` counts from **1** (0 = none fired), but `_joinOrder`
  is a 0-based draw order — looking up `_joinOrder[_allyBeatIdx]` silently skipped the FIRST drawn
  PC and left the last ally row "no such PC waiting". The smoke trail caught it (boot print said
  `Mosiah > Asper > ...` but beat 30 s recruited Asper); always cross-check the boot lottery line
  against the per-beat `ally N (name)` lines.
- **Old-style nested array initializers trip Godot's C# build** (CS0623: "Array initializers can
  only be used in a variable or field initializer"): `int[][] X = { { 1, 2 }, };` fails even as a
  field; write `new int[][] { new int[] { 1, 2 } }`.

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
- Godot C# bindings are **PascalCase**: `Vector2.Normalized()` (not `normalized()`), and `GetInstanceID()`
  returns **ulong** — memoizing instance ids in an `Array<long>` needs `(long)` casts on BOTH the store and
  the compare; a bare `==` mixes long/ulong and throws CS0034 "ambiguous", not the narrowing error you'd expect.
- A bench `M1_LEADER` valve that swaps seats AFTER `BuildJoinLottery()` is half-true: the arena spawn order
  and the join draw both read the leader seat, so the swapped-in leader would march last and the lottery
  would key the OLD leader. Flip the seat BEFORE the draw; symptom of the bad order: boot prints the moved
  seat yet the first march strip's `party:` head is still the old leader.
- Splice scripts: `str.rfind(sub, start)` searches FORWARD from `start` (the second arg is a bounds
  floor, not a backseek) — to find the line START of a match, use `rfind(sub, 0, i)`. And readout
  continuation lines mix tabs with alignment spaces: DERIVE the indent from the matched line instead
  of guessing it, or the spliced line drifts out of the `GD.Print` chain.
- The Giant's Might restore `Scale = Vector2(1,1)` is only exact while the root scale stays authored
  (1,1): PartyMember writes scales ONLY on `_mini`/`_telePivot`/`_auraPivot` children. If a future
  system ever scales a PC root, `_grown` must remember the pre-fire scale and restore THAT.

