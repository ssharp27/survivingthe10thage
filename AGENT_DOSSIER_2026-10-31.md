# AGENT DOSSIER — whole-game review sweep, 2026-10-31

Repo state at review time: HEAD `f606921` (notes refresh atop Bruno roster pass `bed8359`), pushed. Scope: WHOLE GAME, all 8 PCs (Baldrun / Mosiah / Asper / Alvora / Yu / Stack / Wash / Bruno). No agent changed gameplay code or git.

Agents run: **game-tester** (fresh headless trio `smzq_a/b/c`), **critic** (the `game-critic` subagent failed twice on a provider token error; the critique was re-run through a general agent with the critic brief), **ui-designer** (static), **ux-designer** (static; wrote `UX_AUDIT.md` in the repo — contents folded into this dossier, file removed), **artist** (static PNG audit), **explore** (doc/code consistency sweep), **general** (whole-game code review).

---

## 1. GAME TESTER — runtime pass (fresh trio, build f606921)

Gate verdicts: smzq_a (default pilot) **VICTORY lv23, 17 rows, 0-byte .err — GREEN**; smzq_b (`M1_SELECT=Bruno` + camp skip) **TPK lv8 (legal terminal), 17, 0 — GREEN mechanics**; smzq_c (`M1_SELECT=Alvora` + camp skip) **VICTORY lv16, 17, 0 — GREEN**. No ERROR/Exception lines anywhere.

- **F1 HARMLESS (post-terminal rules inversion)**: after VICTORY, `Game._Process` early-returns (`src/Game.cs:1930-1931`), so d20 saves/channel stop, but Enemy/Boss nodes keep ticking into `TakeHit` — post-win bodies go DOWN → 3 buffered fails → ELIMINATED with **zero d20 rolls ever firing**. Post-terminal only; matches the accepted "post-wrap horde cleans near-dead bodies" note; pinned mechanism now.
- **F2 PASS**: locked d20 cadence prints; two elimination texts differentiate correctly — `ELIMINATED (3 failed saves)` (periodic path, `Game.cs:2305`) vs `ELIMINATED (3 fails)` (buffered-damage path, `Game.cs:607`).
- **F3 PASS**: lottery↔beat 1:1 with leader excluded; all 9 scheduled beat rows fire in table order (`Game.cs:79-91` byte-match); escort giveup valve + cleared joins; channel revive + invuln lines; camps + rest-price lane twice/run; `BossPending` hold held under a live boss then fired late-but-ordered.
- **F4 PASS**: Bruno seat ladder, 86× `(axe)` rakes, 2× `(ult) Axes Away!`, 7× `(spin)` rakes (1-5 bodies) — spin era confirmed. Wash shield 65 bounce prints; Smite crit lane silent (UNVERIFIABLE-FROM-HEADLESS). Cinder/burn and **bleed never exercised in this trio** (`bleeding edge` offered but never ranked — bleed has **0% verification** across the trail). Alvora awakening settle lines verified end-to-end.
- **F5 FOR USER EYES**: smzq_b Bruno-seat camp-skip run **TPK'd at the final boss** (800 hp, 115 live, party all OUT at t=370, lv8/xp147) where the prior zx67-69 trio VICTORY'd on all seats. Single unseeded trial; xp pace was unusually low (`camp vacuum: 0 xp gem(s)`). Mechanically legal — flagged, not a verdict.
- **F6 UNVERIFIABLE-FROM-HEADLESS** (unchanged human-owed list): d20 chip pixels, ULT READY label, all VFX, select click lane, camp menu, hand R presses.
- **F7 HARMLESS/cosmetic**: `→` ornament chars garble under cmd `>` capture (`VICTORY ǟ...`) — encoding artifact only.
- **Overall: GREEN trio**, one post-terminal inversion pinned (F1), one single-trial TPK draw flagged for the user (F5).

## 2. CRITIC — fun / balance / pacing (whole game)

Headline: "Best mechanics (death saves, escort, targeted picks) read beautifully in the audit — and the run has almost no pressure where they matter." All three gate runs finished VICTORY at 45-67% pooled HP at final engage; zero eliminations during play; the DOWN→ELIMINATED theater fires AFTER the victory line. First ~250 s of the 420 s loop grant **zero** level-up decisions.

Kit power from logs (raw dps/body): Mosiah 25.0 (single-target, joint-lowest HP 60) / Baldrun 14.3 / Stack ~17.5 sustained w/ burn / Bruno 10×rake — **rake-18 peak = ~180 body-damage one beat**, the outlier crowd weapon. **Alvora + Yu contribute ZERO kit prints in any log — their dps is literally unmeasurable.** Wash bounces: every `(shield)` print is "bounds 1 more" — bounce ladder never verified above base.

Difficulty curve: TPK arc never threatened (0/15 eliminations during play; post-victory only); real pressure is boss1 (t=180-270, zx67 −15 pool HP in 15 s), final boss is the weakest second-half fight; final-boss HP swings 455→648 (+42%) between runs (die-roll variance). Escort abandonment has no cost (all 3 runs "abandoned → joins at FULL HP").

Economy: **XP dead zone — `lv 1 xp 0` until t≈250 in all three logs; 13 of 15 levels in the last 150 s.** Bruno/Wash/Stack took 0 ranks across the trio (pilot option-1 bias) → bleed, extra-axes, broader-bite, bounces = 0% verification. Camp AFK-defaults to SHORT + free rank ×3. Leader bonus lanes half idle (`lb chain 0` on 2 of 3 leaders).

PRIORITY PUNCH-LIST (numbers = suggestions):
1. Freeze/stage the world at VICTORY (14 post-victory DOWN→ELIMINATED lines are the best death theater in a no-stakes context).
2. XP dead zone fix — cut lv 2-5 thresholds ~70% or gem XP ×2.5-3 (target lv2≈t90-120, lv4≈t180, lv8≈t300, end lv14-16). Highest-value number change.
3. Escort abandonment cost — join at the locked 25% floor instead of full HP (or escort-cleared gets the free camp rank); drop the 55 s timeout toward ~40 s.
4. Sharpen final act / dull mid: +25% final-boss contact at t≥360 (target pool 25-35%) or SHORT rest heal +25%→+15%; make final-boss HP a function of party DPS, not a die-roll.
5. Bound Bruno: `extra axes` cap 2 ranks (3 crescents) or falloff 100/70/50%; bleed applies to nearest 3-4 bodies per crescent; give bleed a `(bleed)` print verb.
6. "Axes Away!" sits UNDER the baseline (7 spin rakes of 1-4 bodies while crescents raked 3-7 same seconds; 8.3% uptime): spin arm 70→110-120 px, blades 2+2×`axeN`, add 15-20 px body-drag pull.
7. Audit-blind kits: add `(fam) Yu familiars fuse N` and `(aura) Alvora ring pulses N` prints (symmetric to `(axe)`/`(shield)`).
8. VFX debt on the two loudest lanes (crescents/spin/bleed/shield) — the fun exists in data only.
9. Wash ult armour: +5 flat vs ~10-dmg hits ≈ 90% mitigation for 10 s — offer +3 or scale by tier.
10. Lottery softness: weight-3 Mosiah benched anyway in zx68; consider `{4,4,1...}` or guarantee stage-1 tickets.
Watch-out: pilot option-1 bias leaves the newest kit lanes at 0% verification — force-rail ranks for the next trio or ghosts inherit.

## 3. UI DESIGNER — static whole-UI review

Punch-list by impact:
1. **HIGH — Campfire roster grid hard-caps at 5 of 8 members** (`ui/Campfire.gd _build()` slots=5, `PartyGrid columns=1`; `CampfireCards()` returns up to 8): Wash/Bruno never appear on the rest screen. Fix: slots=8, grid columns=2 (or one column of 8 — fits).
2. **HIGH — no graphical XP bar; picker is a stacked text label**, not cards (XP is buried text in the top-left HUD label; `_pickerLabel` monospace list). Fix: 200×8 gold bar under HUD label + card slots for `_optRoll` (max 9, keys 1-9).
3. **MEDIUM — select party box: 17 stat rows in a ~342 px box** (~19 px/line, tail rows clip risk). Fix: show live tracks + a few placeholder rows, or shrink font/extend box.
4. **MEDIUM — HUD strips capped at 6 slots** (`UpdateHpStrips()` `i < 6`; 8-PC roster), name label uncapped width; Stack's six-elixir tag sprawls. Fix: 8 strips, ellipsis name column ~110 px, pin bar/dot columns.
5. **MEDIUM — death-save overlay grammar text-only**; no 3-fail/3-success track visual; d20 chip static vs DESIGN's animated die. Fix: 3-segment wedge track (blood=fails, amber=successes).
6. **MEDIUM — `_ds` label runs off the left screen edge** (x=−24, unbounded). Fix: center on body, autowrap/clamp, shorter copy ("STABLE" / "DOWN" / "fail 1/3").
7. **LOW — Campfire ConfirmHint crowds COMMIT** (long no-autowrap line in HBox). Fix: autowrap or stack above.
8. **LOW — stale select comments claim 6 seats / 3+2** vs actual 8-seat 3+3+2 `SEL_GRID`. Update comments so a future editor doesn't "fix" the grid and silently cut Bruno.
9. **LOW — SEL_BONUS copy out of sync**: Bruno "none yet" + ult listed (fine), but Wash's live Smite passive described as "none yet". Align or mark "M3 — bonus TBD" consistently.
Verified reads correctly: 8-seat board fits `SEL_PAGE`; strip geometry no collision; d20 layering zones; camp chips; RunSummary frame.
Open questions: XP bar placement/colors; card count/layout at 9 slots; campfire grid columns; death-save track visual; run dismissal routing (M4).

## 4. UX DESIGNER — player-experience audit (from UX_AUDIT.md, folded here)

P0 death/rescue legibility: downed read is console-only (no confirmed prone+grey+ring); **channel immunity silent** (hits not landing unexplained — wants gold tether + 3 s radial arc); post-revive 2 s invuln invisible (wants shimmer + countdown); buffered damage on downed bodies invisible (wants shatter spark + 1/3,2/3,3/3 pips); **STABLE is the most dangerous and least readable** state (wants skull icon + "SLAYABLE" + one-time toast).
P1 onboarding: select human click-path untested (2-step tooltip); join/escort/stranded is print-only (wants STRANDED HUD strip + off-screen arrow + escort cage visual + "RECRUIT JOINED" banner); elixir/Drink/Smite invisible; "How to survive" first-run card + campfire option copy.
P2 combat feedback: NO VFX on any signature attack (axe crescent arc sprite, shield hop sprite, burn ember, bleed drip, cinder ring; pooled, ≤3/body); boss telegraphs print-only; no per-body HP bars / damage numbers.
P3 difficulty: **near-TPK after VICTORY feels like a bug** (freeze spawns / grace 3 s, or run summary without wrap-up march); **"rescue abandoned → joins at floored HP" vs "returns at 60/60hp" copy contradicts itself** (reconcile); 25 s AFK camp default is a frustration magnet (countdown or player-driven).
P4 flow: picker mid-march pause (auto-pick countdown), run summary needs on-screen RETRY button.
P5 anti-clutter: lock color vocabulary (red=damage, gold=heal, amber=telegraph, green=success, grey=downed).
Recommended first 3 to ship: P0 downed/channel/invuln read; escort/stranded HUD; axe/shield/burn VFX.
Open questions: VICTORY freeze vs wrap march; camp default 8 s vs player-driven; leader-bonus HUD scope; d20 animation owner.

## 5. ARTIST — art audit + VFX direction (whole roster)

PNG audit (pure python): all 9 `art/*.png` hard-edged (0 soft-alpha px), 50-62 opaque colours, 1 px margins (Stack 2), class baseplates present. **brook.png satisfies house style but is wired nowhere** — 9th PC? enemy/NPC? leftover? bruno.png (85×118, 61 colours) matches spec; cxΔ −3.67 is the most off-center of all PCs (vs −3.2 cited in code) — confirm measurement method before finalizing rim pin.
Roster scope drift: DESIGN.md says M2 = "all 5 PCs"; code + art ship 8.
Mini scale: Bruno 85×118 ×0.75 ≈ 64×89 px, under the 96 px ceiling; fine axe detail lost at that size — **the thrown-axe crescent VFX must carry the entire attack read**.
Gaps: zero frame animation anywhere (correct for house style — fix via rig offsets + pooled decals, NOT frames); hit-reaction is modulate-only (wants micro-jitter); zero shaders/particles/trails/decals exist.
Lane-by-lane direction: axe crescent arc-decal + blade trail + apex spark (moderate); bleed = deep crimson hue-separated from burn's amber (cheap tint + drip); shield disc + chain line (cheap/mod); spin ring decal reusing axe blades (moderate); bees/kaka spawn burst + detonation flash (moderate); elixir aura chips 6 colours (cheap/mod).
PUNCH-LIST: P0-1 axe crescent VFX (moderate) · P0-2 bleed tint+drip (cheap) · P0-3 shield bounce (cheap/mod) · P0-4 hit micro-jitter (cheap, no art) · P1-1 spin ring · P1-2 familiar bursts · P1-3 elixir chips · P2-1 cinder visual · P2-2 rig-based commit poses · P2-3 Spirit motes → sprites · P3-1 camp/summary beats.
Confirmations owed: brook.png identity; M2 roster count lock; Bruno cxΔ method; select-slot native-size vs mini-scale.

## 6. EXPLORE — whole-repo consistency sweep

HIGH: **DESIGN.md + M2_STATUS.md still describe a 5-PC roster; code ships 8** (zero grep hits for Stack|Wash|Bruno in M2_STATUS.md — last three PC passes recorded nowhere in it); DESIGN.md:15 duplicated sentence. **Insect lane guard asymmetry** (closest real analog to the "SpawnFlies" claim — that symbol does not exist): claim leg guarded (`Game.cs:2724`) but the host-rebind lookup (`Game.cs:2676-2683`) has NO joined/downed/eliminated check, and the `host == null` despawn can never fire (`_party` never shrinks) — **insects keep riding/attacking from a fallen host with no retirement mechanism**. **M2_STATUS.md:211 documents a superseded ring rule** (wrong table order + table-vs-ordinal mechanic — re-implementing from it reintroduces the welded-ring bug).
MED: `ui/INTEGRATION.md` wires against a deleted `CAMPFIRE_LEN` (would not compile) + stale TODOs; RunSummary.gd "greybox placeholder" fallback contradicts shipped pick history; intra-Game.cs comments under-describe shipped widths (20/25/30-wide comments vs 35-wide reality, "five seats" vs 8).
LOW: `RING_SLOT` dead constant (intentional); `campReturned` write-only dead flag; Main.tscn authored boss positions dead data (overwritten by `_EnterTree`); PICKER_AUTO_TIMEOUT self-documented pilot knob.
Intentional placeholders listed: boss TBD, Wash/Bruno "none yet" leader bonuses, select "(+0)" M3 rows, untracked brook.png, vendored addon strings.
VERIFIED CLEAN: all OPT_* arrays 35-wide aligned to rows, stat registry 17, JOIN_W 8 cols, PICK_SLOTS 9 ↔ `pick_9` keycode 57, SEL_* boards 8-wide, Main.tscn exports all exist, death-save constants match DESIGN locks.

## 7. GENERAL — whole-game code review (static)

REAL BUGS:
- **F1 infinite gem farming at the campfire** (`Game.cs:556` gem collection early-returns on `!joined` but `SpawnGem` despawns only on `!MemberInPlay` — all PCs always joined → gems never despawn, unbounded hoover).
- **F2 campfire recruit index/slot confusion** (`joinRecruit` stores a `_party` SLOT, escort resolves `GetMemberByIndex(memberIndex)`; current scene keeps them equal so it works today, but the two-key identity is fragile; `_joinSlot` not re-clamped at one path).
- **F3 `GetMemberByIndex` falls back to `_party[0]` instead of null** — every "host not found" path silently claims the first PC.
- **F4 seat-lane guard asymmetry**: `UpdateInsects` has `_leader == null` guard, `UpdateSpirits`/`UpdateBees` do not — one refactor from a null deref.
- **F5 hollow-seat dodge has no cost + empty hook** (100% dodge for 10 s of the ult window every cycle).
- **F6 leader swap parks the party off-map ~0.55 s while kit lanes still fire** (off-map bursts).
- **F7 dash not world-pause gated** (dash during join walk/card hold).
LATENT: F8 `SEL_BONUS[slot]` bound only vs `_party.Count` (9th PC → swallowed exception, stale breakdown text); F9 `PartyMember[8] cands` exact-fit; F10 Boss.cs never ticks Slow despite Combatant decaying it + "immune" copy lies; F11 `TickBurn/TickBleed` never clear dps at expiry; F12 DoTs tick during card pauses while party clocks/cooldowns don't (cards strictly better mid-DoT); F13 pools silently drop content when full (shots 100/summons 40/gems 60); F14 two-key pool identity (insects key memberIndex, spirits key slot); F15 two sources of truth for camp haste.
BALANCE numbers to revisit: ult uptime 8.3% vs 75-weight camp roll; Bruno reach gets +200/rank while AWAY window doesn't; DASH_IMPULSE 120 is a teleport with no wall test; ESCORT_CRAWL linear vs non-linear odds; campfire revive 6+3·lvl vs full-HP respawn after 60 s (waiting > reviving); gem value uncapped with F1; PICK_RANK_CAP 3 × −15% CD = 57% reduction.
Clean: OPT arrays 35-wide verified, `pick_9` present, param orders verified, scene exports consistent (the inconsistency is in the lookup KEY — F2/F14).

---

## 8. Cross-agent convergence (one screen)

- **Docs are the biggest systemic drift**: DESIGN.md/M2_STATUS.md still say 5 PCs; three shipped passes missing from M2_STATUS.md; ui/INTEGRATION.md references deleted constants. (explore + artist + general all flagged.)
- **XP dead zone (250 s with zero decisions)** — critic + tester F5 (TPK draw tied to low xp pace) + ui-designer (no XP bar). Probably the highest-value single change.
- **Post-victory death theater** appears in every report: tester F1 (mechanism pinned), critic #1 (staging), ux P3.14 (freeze spawns).
- **VFX debt** (critic #8, artist P0s, ux P2): the whole fun layer is data-only; axe crescent, bleed tint, spin ring, shield tether are the consensus first ships.
- **Verification gap**: pilot option-1 bias leaves bleed / extra-axes / broader-bite / bounces at 0% taken ranks across ALL trails (critic + tester agree; next trio should force-rail ranks).
- **Audit-blind kits**: Yu and Alvora have zero kit prints — add `(fam)`/`(aura)` print verbs (critic #7).
- **Real code bugs to gate**: gem-farm at campfire (general F1), `GetMemberByIndex` fallback (F3), insect lane riding dead hosts (explore H2 ≈ general F4), hollow-seat free dodge (F5).

## 9. Open questions for the user

1. **Bruno leader bonus** — still undecided (lane honestly `return 0`); pitched: whetstone (damage aura) / extra crescent while leading / spin recovery (+HP or partial ult recharge).
2. **brook.png** — 9th PC, an NPC, or leftover? (Untracked, untouched; artist + explore both flagged it unwired.)
3. **VICTORY staging**: freeze spawns/grace vs keep the wrap-up march (affects run length + the F1 inversion).
4. **XP pacing retune** (critic #2 numbers) — approve?
5. **Escort abandonment cost**: join at 25% floor vs full HP; 55 s → ~40 s timeout.
6. **Campfire**: keep 25 s AFK SHORT default, or 8 s countdown / fully player-driven.
7. **Doc refresh**: DESIGN.md + M2_STATUS.md roster 5→8 sync (and ui/INTEGRATION.md dead `CAMPFIRE_LEN` wiring).
8. Tester F5: the Bruno-seat camp-skip TPK trial — re-run or retune?
