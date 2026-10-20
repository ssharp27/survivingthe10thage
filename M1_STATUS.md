# M1 STATUS — 2026-10-18 pass, refreshed 2026-10-19 (full stage flow verified through VICTORY)

M1 greybox of "Surviving the 10th Age" (Tanurial Forest) builds green, is **human-playable**,
and the full campaign-battle loop is verified in-engine headless:
roster → horde I → mini-boss slain → campfire → horde II (live-cap 40 × hordeScale) → BOSS engaged →
`VICTORY — Tanurial Forest cleared`. The 2026-10-19 pass (bug fixes P1, ui wiring P5,
instrumentation P6) is **uncommitted** and verified by the smoke runs below.

## What was fixed on 2026-10-18

- **Input map was dead**: the old `[input]` entries were Godot-3 style (`events: []` + top-level
  `physical_key`, mapped to physical codes 544-547 = A,B,C,D). Godot 4.7 parsed them with no error
  but bound nothing. Rewrote to `Object(InputEventKey,...)` serialization:
  move = WASD (W566/A544/S563/D547) + arrows (4194305-4194308); level-up picker = A/S/D
  (shares the WASD letters — VS-style overlap, fine on a confirm-any-time picker;
  later superseded, picker moved to number keys — see the picker key-steal bullet).
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
- **Picker moved to NUMBER keys (2026-10-19, human-test round)**: even edge-read A/S/D meant a human
  *naturally tapping* A/S while walking picked a card unseen. `pick_1/pick_2/pick_3` (keycodes 49-51,
  `physical_keycode:0` like the working `retry` entry) with the label split across three lines —
  the old single long line ran past the 1280 px window edge and read as cut-off text.
- **Campfire rest cards were misclick-dead (2026-10-19 human-test round)**: SHORT/LONG Buttons
  only covered their 64 px title strip, so clicking the card body (where the text is) did nothing
  and the human fell back to LEFT/RIGHT + SPACE. Root cause was layout, not signal wiring: the
  modal's COMMIT/REST NOW clicks always worked because ConfirmModal is the LAST subtree (topmost
  pick), while the rest row's buttons had a dead zone around them. Fixed in Campfire.tscn by
  making each Button the WHOLE card (`size_flags_vertical = 3` stretch + `custom_minimum_size`),
  with the body/chip Labels moved as CHILDREN of the Button (Labels are mouse-passable, so the
  click reaches the Button) - any click on a card now selects it.
- **Rest-card boxes un-dimmed (2026-10-19 human-test round)**: the only transparency on the screen
  was the full-rect Backdrop `Color(0, 0, 0, 0.52)` letting grass/projectiles show behind the
  text (every StyleBoxFlat in Campfire.tscn + grimoire_theme.tres was already alpha 1). Backdrop
  is now opaque dark `Color(0.09, 0.08, 0.07, 1)`.
- **Campfire confirm moved SPACE → ENTER/RETURN (2026-10-19 human-test round)**: SPACE is the
  Leader's dash (see the dash bullet), so the COMMIT trigger and the modal confirm both read
  KEY_ENTER only. WARNING for future edits: Campfire.gd is TAB-indented, and the edit tool must
  receive literal tabs - the first pass on this block went in malformed (broken elif + mixed
  tab/space indent = GDScript syntax error) and was repaired by a scripted rewrite of the whole
  `_input` func, then verified byte-exact with a repr() tab dump.
- **Level-up picker is now SMART-RANDOM 3-of-6 (2026-10-19 human-test round)**: the fixed
  3-option card read same-y every level. Pool grew to 6 named upgrades (3 per member); every
  level-up rolls slot 1 = one still-open Baldrun upgrade, slot 2 = one still-open Mosiah upgrade,
  slot 3 = fully wild from everything still open. Capped options never show, no option shows
  twice on one card, a fully-capped member borrows the wild pool, and if NOTHING is open the
  picker stays closed (the level-up just banks xp). Code: `RollOptions()` + `int[] _optRoll`
  (slot -> option) + `_pickRank[6]`; `OptLabel(slot)` resolves through the roll.
- **Baldrun's arc-widening upgrade REPLACED (2026-10-19 human-test round, user-locked)**: the
  maul fan stays a fixed ~150° arc (`arcMul` deleted outright from PartyMember.cs); Baldrun's
  pool is longer reach (range ×1.15) / heavier blows (damage ×1.10) / swifter swings
  (cooldown ×0.86). Mosiah keeps quicker thrusts + deeper thrust and gains sharper points
  (damage ×1.10).
- **Dash added on SPACE (2026-10-19 human-test round)**: instant 240 px impulse toward the walk
  direction (or Baldrun's facing when standing still), 1.5 s cooldown, arena-clamped, Leader only,
  gated on the Leader being live; the picker early-returns before it so a frozen arena can't dash.
  Numbers are SANITY CHECK ONLY - balance approval is the user's. Input action `dash`
  (keycode 32 / physical_keycode 0, mirroring the working `pick_*` serialization).
- **USER-APPROVED BALANCE ROUND (2026-10-19, second human-test round)**: Baldrun 30 dmg/swing
  with 130 px reach; Mosiah 15 dmg/thrust with 115 px reach; goblin 30 HP, wolf 70 HP (numbers in
  Director.cs). **New enemy: hobgoblin** - 60 HP / 100 speed / 3 contact / 3 XP, one per ten goblins
  spawned (`_goblinsSinceHob` counter, counts real spawns only), drawn darker green + a hair bigger
  than the goblin clump. **Horde scale reworked**: long rest is now multiplicative ×1.20 on HP AND
  the Director multiplies the live cap (40 × scale) on top of the existing spawn-interval ÷ scale,
  so a long rest spawns MORE concurrent bodies, not just faster ones. **Squad dash**: live ring
  members lunge with the Leader at half the distance (120 px) on their own 6 s cooldown; the ring-
  follow reels them back, so it reads as lunge-and-recover. **Dash cooldown readout**: a 44×5 gold
  fill bar over Baldrun's head (world-space pivot, ZIndex 7/8, parked offscreen when the leader is
  gone) grows back as the Leader's dash recharges.
- **Rest-card clicks STILL dead round 2 (2026-10-19 human-test round 2)**: even as whole-card
  Buttons, SHORT/LONG clicks wouldn't fire (modal clicks did). Two-pronged fix: (a) the Contents
  minimums (dice 56 + 2×168 grid + 150 rest cards + 46 confirm + separations) overflowed the 592 px
  inner frame - the rest row landed at screen y≈568+ and COMMIT ran fully off the 720 px screen;
  shrunk to fit (cards 168→138, dice 56→48, v-seps 16→10 / 14→8, rest cards 150→130); (b)
  belt-and-braces in Campfire.gd `_input`: raw left-clicks are routed through the Buttons' rects
  via `rect_has_point(get_local_mouse_position())` - Button-local space, exactly the coordinate
  space Buttons pick in - so clicks land whether or not the Control picker routes them. NOTE:
  Campfire.gd edits went in via python byte-exact replaces (tab-indent trips the edit matcher).
- **Rest-card click CRASH round 3 (2026-10-19 human-test)**: the manual routing used
  `event.button_name == "left"` - 4.7's InputEventMouseButton has NO button_name in this build,
  so the long-rest click threw "Invalid access to property 'button_name'" inside _input. Fixed to
  the stable `event.button_index == 1` (MOUSE_BUTTON_LEFT).
- **Level-up growth (user-approved, 2026-10-19)**: every level-up now ALSO auto-grants each party
  member +0.5 FLAT weapon damage (reduced from the +1 the first cut shipped - players were trending
  too strong), added to base before upgrade multipliers, stacking under the card's pick and landing
  even when every rank is capped. The free signature ranks stay REVERTED per user; damage-only stands.
  Applied in PickerTick right at the level step.
- **HP nerf round (user-approved, 2026-10-19, third balance pass)**: players too strong post-balance -
  Baldrun maxHp 200 -> 80, Mosiah maxHp 160 -> 50 (Main.tscn). Party pool is now 130 total vs the
  previous 360; down/death-save economy unchanged, so squishy bodies + buffered fails are the pressure
  curve from here. NOTE: both 2026-10-19 pilot smoke runs under the new numbers ended TPK (lv 6 / lv
  10) - the autopilot never dashes and stands still during revivals, so it now dies where it used to
  clear; smoke gating is VICTORY **or** TPK + 0-byte .err from here, and human skill (dash, dodge
  timing, campfire economy) is what decides a run.
- **Balance pass 4 (user-approved, 2026-10-19 human-test)**: dash halved to 120 px and cooldown raised
  to 6 s (DASH_IMPULSE/DASH_CD, Game.cs) - the run felt too safe, dash was eating too much ground.
  Side effect: the leader lunge (120) now EQUALS the member lunge (MEMBER_DASH_IMPULSE 120), so the
  squad lunge is symmetric and the ring formation survives the dash intact (this is the fix for
  "Mosiah doesn't dash with Baldrun, he has to catch up"). **Attachment re-tightened**: ring follow
  homes at max(own x1.5, leader +80) with a hard 48 px leash from the slot (was x1.15/+40 and 120 px)
  - members are effectively welded to their orbit slot; drift is physically impossible, shoves/dashes
  snap back within one frame. Orbit clock bumped 0.15 -> 0.22 rad/s ("rotate a little faster"). Dash
  readout bar follows automatically (it divides by DASH_CD).
- **Balance pass 5 - rest economy (user-approved, 2026-10-19)**: risk-vs-reward rework. **LONG** = full
  revive + full heal for the WHOLE squad (the campReturned guard gone - lifted fallen now top to full)
  and NO free upgrade rank. **SHORT** = +25% max-HP heal + ONE free upgrade rank (OpenOption(3) fresh
  seed) + the SAME x1.20 horde scaling as long. So: long = safety, short = power; both feed the horde
  scale. Applied in EndCampfire. **No-timeout lock**: human level-up picker and campfire NEVER default
  on a timer - they wait for a real selection forever; the 25 s campfire + pilot picker fallbacks now
  run only under M1_PILOT (or the missing-campfire-scene soft-lock guard). PICKER_AUTO_TIMEOUT constant
  intentionally unused for human play.
- **Rest-card click CRASH round 4 (2026-10-19 human-test)**: after the button_index fix, clicking
  threw "Nonexistent function 'rect_has_point' in base 'Button'" - 4.7's Control dropped that method
  too. Routing now uses Rect2 math: `get_global_rect().has_point(get_global_mouse_position())`
  (both exist on Control in 4.x; the rect and the mouse query share the global-transform space).
  LESSON: 4.7's Control API surface keeps missing legacy methods - prefer Rect2/2D primitives.
- **Rest-card clicks STILL dead round 5 (2026-10-19 human-test)**: clicks reached _input (the crash
  traces proved that path) and passed the pressed/index guards, but the global-rect has_point test
  kept returning false with no other symptom - guessing at spaces was done with. New belt-and-braces:
  a `_was_click_on(b, ev)` helper accepts the hit if ANY coordinate space agrees (global rect vs
  Control get_global_mouse_position, global rect vs raw ev.position - identical when window == base
  viewport, the default 1280x720 run - and local rect Rect2(0,0,b.size) vs get_local_mouse_position),
  plus a `[campfire] left-click at <pos> short= <rect> long= <rect> commit= <rect>` print dumping
  everything to the editor Output panel on every choice-phase click. UX change too: clicking a rest
  card now SELECTS and OPENS the popup in one click (the popup is the last gate, confirm with
  ENTER/click) - earlier rounds felt "dead" because a click only set a subtle gold border and the
  player still needed ENTER. If clicks STILL do nothing, the Output line is the ground truth: read
  the event position vs the three rects and stop theorizing.
- **Party always attached (user-locked, 2026-10-19)**: the ring follow had a baseSpeed x0.9 cap
  (Mosiah 140 x0.9 = 126 px/s) BELOW Baldrun's 128 px/s stride, so a member could never close the
  gap and permanently trailed. Reworked: home at max(own speed x1.15, leader speed +40) so catch-up
  is guaranteed, plus a hard 120 px leash that pulls any strayed body back onto the ring. Downed/
  eliminated bodies stay where they fell (unchanged); the orbit feel stays on the world-space clock.
- **UX/UI + campfire-safety pass 6 (user-approved clusters A,B,C,D,E,F,I; 2026-10-19)**: from the
  six-agent review pass; G (balance) declined - x1.20 horde scaling stays exactly as the user set it,
  H (art) queued. Shipped: (A) campfire copy SYNCED to the pass-5 economy - ShortBody/ShortChip/LongBody/
  LongChip + `_rest_summary()` popup + "both feed the horde x1.20" header in Campfire.tscn/gd; (B) the
  SHORT rest's free rank is now a CHOICE - EndCampfire opens the same smart-random 3-of-6 picker
  (RollOptions) with a "SHORT REST GIFT" card instead of ApplyUpgrade(OpenOption(3)) dealing it silently;
  (C) camp safety gate - ClearBullets() sweeps in-flight boss bullets the moment the camp card goes up,
  ActivateBoss opens a 2 s party immunity window (_engageInvulnT, TakeHit early-return) so the engage can't
  free-hit, and downed bodies get a gold STAND STILL channel bar + countdown; (D) HP floors at 0 the frame
  a body falls (overkill never poisons the +25% heal nor reads as a negative bar); (E) the d20 lives ON
  the downed body - paper chip + ink number + state ring (blood pending-fail / amber STABLE / grey out)
  with color-coded chips (CHIP_BLOOD/AMBER/SAGE/GREY light-end grimoire tints, Game.cs -> m.dsColor/dsLastRoll);
  (F) HUD strips - 6 slots of name label + 110x7 bar (gold leader / bronze others) + state dot (sage/blood/
  amber/grey) replacing the stacked text lines, dash bar 64x8 at y-42 (clears the -58 ds chip and -30 channel
  bar) with a sawtooth ready-flash when charged, picker labels carry "(x/3)" rank progress, and an "EVERY
  RANK CAPPED - xp banked" notice with a 4 s timer; (I) campfire layout headroom - grid slots 4->6, member
  cards 138->100 px, rest buttons 130->100 px so M2's 6-member party fits the 592 px inner frame. NEW 4.7
  C# DRIFT: `System.Math.sin` / `System.Math.ceil` DO NOT exist on Godot's Math class - sawtooth triangle +
  integer ceil instead. Also `out` is a reserved word (isOut). Gate: smokeCT9 TPK lv 9 + .err 0 bytes, and
  the whole campfire chain reads live in the log: 2 s engage window -> "41 in-flight boss bullets swept"
  -> gift card with (2/3) labels -> pilot picked it -> heal math 8->28 of 80 (+25%) -> horde x1.2.
- **Camp + display pass 7 (user-approved, 2026-10-20)**: (1) campfire VACUUMS the map - EndCampfire
   reels every uncollected xp gem into the Leader's pickup ring (40-70 px ring, existing UpdateGems
   magnet does the flight + collection so no xp path is forked; logs "camp vacuum: N gems reeled");
   (2) HP-strip spacing bug FIXED - the 110x7 bars sat at y+11 and cut through the 16 px name text:
   rows 22->28 px, bar/dot y+11->y+18, picker label 210->250; (3) bigger auto-scaling window per user
   (VS-style): project.godot display = 1920x1080 viewport, stretch/mode="canvas_items", stretch/aspect=
   "expand" (no letterbox on any monitor aspect; swap the pair for a 3:2 base like 1620x1080 if wanted).
   NOTE: the campfire `_was_click_on` helper already accepts all three click coordinate spaces, so
   stretch scaling never breaks rest clicks. Gate: smokeCT15 TPK lv 9 + .err 0 bytes, and the log carries
   the new "camp vacuum: 2 xp gem(s) reeled to the Leader's pickup ring" line plus the full SHORT-gift
   picker chain.
- **Human-play pass 8 (user report + approval, 2026-10-20)**: (1) WINDOW - user: 1920x1080 base did not "stretch
   to the monitor"; added `window/size/mode=4` (MODE_EXCLUSIVE_FULLSCREEN: window resizes to the monitor, and
   canvas_items stretch + expand aspect then auto-scale the 1920x1080 design onto any monitor aspect, no border,
   no letterbox - the VS-style behavior). Docs: mode=3 keeps a 1px border (GUI apps), 4 is the game mode. (2) HP
   strips wanted a few more px: rows 28->32, bar/dot y+18->y+21, picker label 250->274. (3) d20 chip "not noticed"
   root-caused: the roll Label NEVER got an ink color - Label's theme default is near-WHITE on the paper-white face
   (invisible), and the state ring was a 2 px lip. Fixed: dark grimoire ink (0.10,0.09,0.08), chip 32->40 px ring
   / 28->34 px face, number moved inside the face. Gate: smokeCT16 TPK lv 7 + .err 0 bytes; death-save lines live.
- **Field-recruit + pacing pass 9 (user-approved 2026-10-20, their own design)**: (1) at t=25 s RUN time a not-yet-joined
    PC drops 300 px from the Leader with a HELP! label + a gold arrow riding over the Leader aimed at it;
    6 escort goblins spawn wide and creep in slowly (40 % speed, hold a 52 px ring, never re-home) - the
    recruit is UN-HITTABLE and cannot attack until cleared; killing all 6 joins it at FULL HP / rank 0.
    Join ceremony = banner "X JOINS THE BATTLE!" (art slot) + CLICK-TO-CONTINUE gold button; the click gate
    is human-gated (no-timeout rule), pilot clears instantly. If the cage survives the mini-boss, the camp
    sweep joins the PC instead (no soft-lock). (2) joined flag on PartyMember: unjoined PCs are excluded from
    HP pool, ring follow, member dash, death saves, TPK count, contact targeting, enemy targeting, HUD strips,
    campfire/summary cards and every picker option (recruiting IS the weapon unlock). Picker seed order
    generalized to member-0..member-4 + wild (`slot < 5`). (3) pacing: tier gate - ONLY goblins until runT
    45 s, wolves+hobgoblins from then (tier 3/4 hooks planned later); PLAY1 (pre-mini-boss) spawn interval
    x2 (half rate) so the long window keeps pressure without count inflation; mini-boss stays 90 s; FINAL
    BOSS moved to a run-clock gate at 150 s (BOSS_AT, test value per user). Run clock `_runT` advances in
    PLAY1/MINIBOSS/PLAY2 only (camp beat and picker soft-pause excluded). Gate: smokeCT17 VICTORY lv 5 +
    .err 0 bytes; recruit drop + join lines both fired in the pilot log (join via the camp-sweep path since
    the pilot never walks to the rescue site).
 - **Human-play pass 10 (three pass-9 bug reports fixed, 2026-10-20)**: (1) STAGING - the beat must read as a
    rescue the party rides toward, so the stranded PC lands PAST the camera edge (distance = the visible rect's
    exit along the chosen bearing + RECRUIT_MARGIN 90 px, floored at RECRUIT_DIST 300; 8 bearing retries, because
    ClampToArena can pull a candidate back into frame) and the escort goblins open ESCORT_RING 340 px wide of the
    PC (was 160). (2) CRAWL GATE - an escort goblin does not move at all until Game.RecruitInView() says the PC is
    inside the ~960x540 camera rect, and the creep factor dropped 0.4 -> ESCORT_CRAWL 0.22, so the goblins shamble
    in over the screen instead of animating off-camera. (3) JOIN PAUSE - the banner is now a SOFT pause exactly like
    the picker/camp screens: Game.GetWorldPause() = _pickerActive || _joinWaiting, the 4 external gates (Enemy,
    PartyMember, Boss, Director) read it, a freeze block after UpdateHpStrips holds the sim, and `_joinWaiting` was
    added to the run-clock guard. It is NOT a tree pause on purpose: the CLICK TO CONTINUE gate needs input frames.
    (4) HINT LEAK - the CLICK TO CONTINUE Label was a LOCAL inside JoinRecruit, so ClearJoinBanner never had a
    handle on it and the prompt stayed painted over the HUD for the rest of the run (in the pass-9 human run that
    stale prompt next to the SHORT REST GIFT picker is exactly what read as a frozen game); it is a field `_joinHint`
    now, hidden with the card. (5) CAMPFIRE FREEZE/CRASH - after EndCampfire hides the card a hidden Control still
    receives _input and its rect still answers, so a stray click or ENTER re-ran _on_commit_pressed (modal.show() +
    get_tree().paused = true with nothing visible on screen, and nothing left to un-pause it): Campfire.gd now bails
    `if not visible` at the top of _input, and _on_rest_now bails `if not modal.visible` so a rest cannot resolve
    twice behind the camp. Gate: smokeCT18 TPK lv 4 + smokeCT19 VICTORY lv 6 + smokeCT20 TPK lv 4, all .err 0 bytes;
    CT19 logged the drop 1119 px off camera, the camp-sweep join, the pilot join-banner clear, the SHORT rest and
    the BOSS/VICTORY line.
 - **Balance pass 11 + recruit-beat rework (user-approved, 2026-10-20 human-test)**: (1) Mosiah maxHp 50 -> 60
    (Main.tscn, user ask). (2) PICKER QOL - eliminated-until-campfire bodies stop leaking onto level-up cards:
    MemberJoinedAt -> MemberInPlay (joined && !eliminated), applied at all 3 picker call sites, so picks feed the
    bodies that can still save the run; campfire revival re-opens their tracks automatically. (3) ESCORT HUNT -
    escort goblins open ESCORT_RING 340 -> 520 px wide and crawl 0.22 -> 0.12 (both agent sanity-check numbers on
    the user's "further/slower" ask), and they now CLOSE ON the PC instead of holding a decorative ring (escortHold
    = the PC, Enemy escort branch stops at 18 px, damages on contact within 30 px, contactDamage 2 on a 1 s cd).
    (4) JOIN HP - EscortHitRecruit chips the stranded PC's hp (floored at RECRUIT_JOIN_FLOOR 0.25 of max, proposal
    number), and JoinRecruit brings him in at whatever survived instead of full HP (supersedes the old "joins at
    FULL HP" user rule). The horde still can never touch a stranded recruit; the escort can never run him down.
    (5) HUD readout - while the beat is pending the HP strip goes live showing "Mosiah STRANDED 44/60" so the
    join-HP cost is readable (agent addition on top of the user rule; vetoable). Smoke gate CT21/CT22 VICTORY lv 6
    (join print shows the new 60/60hp) + CT23 TPK lv 6, all .err 0 bytes; escort contact damage and the STRANDED
    strip are human-only paths (the pilot never walks to the rescue site, so it joins at full HP via the camp sweep).
 - **Wrap-world conversion (user fix, 2026-10-20 human-found bug)**: goblin escort staging collapsed completely
    when a PC spawned near an arena edge (ClampToArena squashed the ring). The stage now has NO walls: ARENA_HW/HH
    became the WRAP PERIOD (2400x1800 torus). ClampToArena -> WrapNearCamera (every entity folded into the half-period
    box around the camera - 1200x900 vs the 960x540 screen, so seam folds sit outside frame and never pop) plus
    WrapDelta for toroidal chase/contact math (Enemy normal/reaper/escort branches, Boss drift/aim/contact, downed-body
    contacts). PartyMember._Process folds every body but the Leader (the Leader IS the camera anchor); gems fold in
    UpdateGems; the floor rect rides the camera while the 60 patches fold (motion readout stays); bullets now despawn
    camera-relative. Walls retired (BuildWall no longer called); HELP!/arrow track the recruit's FOLDED position;
    SpawnFieldRecruit staging simplified (retry loop + clamp removed - staging cannot fail when there are no edges).
    "Absolute offscreen parking" is no longer a hiding spot in a looping world: Bosses start and die HIDDEN
    (_EnterTree + OnBossKilled Visible=false, ActivateBoss Visible=true), the dead TEST reaper hides itself too.
    Escort goblins that survive a camp-sweep join become ordinary horde goblins (no shuffling to a dead hold point;
    escort branch now tracks the recruit's live position, not the spawn-time escortHold). Smoke gate CT31/CT32/CT33:
    all VICTORY (lv 6/7/7), .err 0 bytes - recruit drop prints 1119px out cleanly. Caveat: wrap-awareness covers
    chase/contact; enemy-to-enemy separation still uses raw coords (opposite-seam pairs under-count, greybox-tolerable).
 - **Pass 12b (2026-10-20, human-found wrap fallout)**: (1) the pinned floor covered ~1/4 of the frame on the user's
    fullscreen window - the 960x540 assumption was WRONG (DPI-scaled fullscreen with canvas_items stretch sees far more
    world). Frame + wrap period are now MEASURED, not hardcoded: a hidden full-rect anchor Control (_winProbe, anchors
    0,0-1,1) reports the REAL visible canvas rect after layout (ground truth from the renderer, no viewport singleton
    needed - Godot 4.7 C# has no GetRoot()/Window-singleton access and Camera2D.GetViewport() is NULL for the main
    camera - a null-ref there spammed 200MB of stderr and froze flow, caught by the CT34 gate). MeasureWindow runs at
    run start AND every _Process frame (layout is stale on frame 1; the >64 sanity guard rejects garbage); the floor
    resizes, the patch field re-scatters with density held to area (cap 400), Director spawns walk out PAST the
    measured frame (the old fixed 800 px popped goblins on-screen on big windows), and volley bolts now die just
    outside the measured frame. Headless smoke measures 1920x1080 = the old numbers, so smoke behavior is unchanged.
    (2) Mosiah popped into the world before his beat: PartyMember now hides any not-yet-joined PC that is not the
    pending recruit (GetRecruitPending instance-id gate; camp/escort joins flip joined and lift it). Smoke gate
    CT34/CT35/CT36: VICTORY lv 6/7/6, all .err 0 bytes.
  - **Pass 12b addendum (same day, human round 2)**: floor STILL pinned its top-LEFT corner at the camera. Root cause
    was not sizing - the per-frame pin `_floorRect.GlobalPosition = GetCameraCenter()` IS `Position` (root parent,
    identity) and overwrites the -size/2 seed every frame, so the rect only ever covered the bottom-right quadrant;
    the old 2400x1800 rect merely spilled past the assumed frame so it looked fine. A headless oracle (src/_pivot_test
    .gd run through the console binary on a 1-node tscn) proved pivot_offset is a NO-OP for pure translation
    (gp stayed == pos after setting it) - do not trust "set pivot, pin origin" as a centering idiom. Fix: the pin now
    writes TOP-LEFT = GetCameraCenter() - Size*0.5f, which is correct under both semantics. Gate CT37/CT38/CT39:
    VICTORY lv 6/6/6, all .err 0 bytes.


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
shows options on three lines — press **1 / 2 / 3** (number keys so a WASD walk tap can never
pick unseen; press-edge read, arena frozen while the card is up). After a run ends the
**R** key retries the stage from the run-summary card (journal/menu are M4). **SPACE dashes**
the whole live squad: Baldrun 240 px / 1.5 s, ring members 120 px / 6 s (user-approved numbers),
and a gold bar over Baldrun's head fills as its dash recharges.
The campfire card takes **mouse clicks anywhere on the card** (rest card → COMMIT → REST NOW;
raw left-clicks are also rect-routed in `_input` as a fallback for the Control picker),
with LEFT/RIGHT + **ENTER** as the keyboard fallback (SPACE is the dash now, so the campfire
reads ENTER/RETURN only).

**Death-state tester (TEST BENCH, default OFF)**: the reaper only spawns when `M1_REAPER=1` is in the
environment — human play left it off once the revive rules tested clean, and nothing about it carries
balance meaning. With it on: 10 s into a run a **big red diamond "reaper"** spawns **exactly once** —
touching it **downs** whoever it touches (death saves start; it's a revival/STABLE test bench) and then
it despawns. It doesn't outrun the party; walk into it yourself. Dead nodes are fully inert (`kind="dead"`
early-return).
**Baldrun's maul is a ~150° ARC CLEAVE with SPLIT damage (human-test locked)**: it **Mosiah-aims** — the
swing targets the absolute nearest body whatever way the body walks, and the fan is centered on that swing
(the arc never depends on walk facing); every enemy inside the fan is hit, but the swing's force divides
among them — full 30 on a lone target (user-approved), chip damage on a packed horde; telegraph is a
3-spoke fan. The
"wider arc" rank was **REMOVED** (user-locked): the fan stays a fixed arc and Baldrun's upgrade pool
went to reach / damage / cooldown instead. **Mosiah's rapier
is single-target** at 15 dmg / 115 px reach (user-approved) so the two kits feel distinct (wide/split
vs. narrow /heavy). **Followers slowly ORBIT the Leader** (world-space clock, radius 64; the threat-facing slot made
them feel glued to directional input) and auto-aim their strikes.
Level-up picks come from a **SMART-RANDOM 3-of-6 pool, each capped at rank 3** — Baldrun: longer reach
(range ×1.15) / heavier blows (damage ×1.10) / swifter swings (cooldown ×0.86); Mosiah: quicker thrusts
(cooldown ×0.86) / deeper thrust (range ×1.16 + 1 pierce) / sharper points (damage ×1.10). Each level-up
rolls one guaranteed upgrade per member plus one fully wild slot, so kits get variety instead of the
same three lines every time; the old fixed card and its maul-arc ×1.18 track are gone (arc stays fixed).
**Dash on SPACE** (Leader 240 px / 1.5 s, live ring members 120 px / 6 s, user-approved) is a movement
verb, never an upgrade — HP, speed and healing stay off the picker (they live in campfires and revives);
the gold bar over Baldrun's head is the Leader dash's cooldown readout. Death saves (locked): d20 every **4 s** — nat 20 revives 25 %;
3 successes = **STABLE**, rolls stop and hits slay unless the Leader **stands still ~3 s on the body** (revive 50 %);
3 fails = OUT until the next campfire. Campfires (locked, rest-choice split): the camp beat lifts the fallen
(**return at 25 % HP**) and stands bodies down→up without healing; **SHORT rest = the single +25 % max-HP heal**
(clamped, never stacked); **LONG rest** fully heals the already-standing company while the lifted fallen **stay at
25 %**, grants one free rank, and grows horde scaling to ×1.20 per long rest (HP ×1.20, spawn cadence ÷ scale
AND the live cap × scale — user-approved rework of the old +0.35 additive). **Revive protection (locked)**: a downed body being channeled is **immune** while
the channel progresses (a swarm can't screw the rescue out), and every revive grants **2 s invulnerability**
(pale-green tint); the reaper tester obeys immunity too — it only despawns after its down actually lands.

## Godot 4.7 C# pitfalls (found empirically, keep for future work)

- **Input-map special keycodes are a minefield** (human bug 2026-10-20: Escape moved the leader up): the four
  movement actions had bound 4194305..4194308 = Escape/Tab/Backtab/Backspace, NOT the arrows. Real arrow codes:
  Left=4194319, Up=4194320, Right=4194321, Down=4194322 (verified via OS.get_keycode_string headless oracle).
  Escape is now free for the future pause/menu key.

- **C# `_draw()` is never invoked** (binding dropped the hook; a plain `public void _draw()`
  silently runs 0 times — probe proved it). Drawing *outside* `_draw` is engine-enforced
  ("Drawing is only allowed inside this node's `_draw()`…"). A GDScript child worked in
  theory but users still saw nothing, so the shipped workaround is **pure nodes**: a
  pivot + thin ColorRect "spokes" (maul = 3-spoke arc fan, rapier = solid line) rotated to
  `facing` and alpha-faded each frame — node attributes, not draw commands.
- 4.7 bindings renames/removals hit so far: no `Godot.GDScriptResource` class (use `Godot.Script`);
  no `Node.Script` property (use `Set("script", …)`, which returns **void**); no `z_index` field
  (use `ZIndex`); input serialization is `keycode`/`physical_keycode`; no `Input.GetPressedKeys`;
  `DrawPolygon` wants per-vertex `Godot.Color[]`; `_draw` not virtual (never `override`).
- **Input-event handling (2026-10-20)**: the C# virtual is `public override void _Input(InputEvent ev)`
  (the `Input(Map<InputEvent>)` form does NOT exist — `Map<>` has no C# alias; `Input(InputEvent)`
  without the underscore fails). The base `InputEvent` class carries NO `Button`/`Pressed` fields —
  match the subclass: `ev is Godot.InputEventMouseButton mb` and read `mb.ButtonIndex` whose enum
  type is `Godot.MouseButton` with **PascalCase members** (`Godot.MouseButton.Left`, not `LEFT`,
  not `MOUSE_BUTTON_LEFT`; sibling members read as `WheelUp`/`WheelDown` style). `mb.Pressed` (press
  vs release) lives on the subclass too.

## Still open — vision work (pick priority with user)

1. **Balance/feel pass for human play**: contact pressure, XP curve, campfire heal economy;
   numbers live in Game.cs / Director.cs / Main.tscn (cheap tuning surface).
2. **Boss spectacle**: Enter-the-Gungeon feel — phase telegraphs exist in Boss.cs (3 phases,
   ring/fan volleys); needs wind-up visuals, boss body size/silhouette, named phases.
3. **Roster growth**: 3 more campaign PCs (recruit = join-point narrative beats in this stage),
   3-rank upgrade tracks + evolutions (Stormcaller / Viperkiss) still stubbed (M2).
4. **8-bit D&D aesthetic pass**: greybox ColorRects → pixel sprites + palette; battle-scene backdrops.
5. **Meta-layer stub**: campfire screen (return eliminated members), campaign-path stage select.
6. Nice-to-have: MCP plugin GUI boot + hub re-pin (untested since the DLL-placement fix).

## Pilot note (verification-only, env-gated — never active in human play)

`M1_PILOT=1` env ⇒ leader autopilots (orbits around threats, boss strafe + face-lock), bullet volleys
pass through, incoming party damage ×0.15, and the picker auto-confirms at 2 s by taking the **first
open slot of the current roll** (`_optRoll`, which `RollOptions()` seeded) — there is still no heal
pick, and the pilot never presses `dash`, so the dash path is only exercised by human play. Implemented in Game.cs (`_pilot`, `faceTo`, bullet-dodge gate,
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

The reaper is now **opt-in**: `M1_REAPER=1` (pair with M1_PILOT) re-enables the death/revive TEST BENCH
for a smoke run; by default no reaper spawns, so a pilot smoke reliably reaches the campfire branch
without any skip knob. With the reaper ON the pilot TPKs ~often because its down targets the Leader and
the deferred Leader-down rule leaves no rescue channel — that is expected variance, not a bug.
**Gate on the `VICTORY` / `TPK` line, not on the frame budget**: `--quit-after` is fragile (frame rate
varies run to run). A run that ends in TPK legitimately has no VICTORY line — read the last log line to
tell truncation apart from a real loss; rerun at 90000 frames only if the last line is a mid-stage status
line.
