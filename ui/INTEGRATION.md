# UI integration spec — Campfire.tscn + RunSummary.tscn

Both scenes are standalone (nothing in Main.tscn, Game.cs, Director.cs or any existing
file is touched by this change). They are driven by lightweight GDScript controllers
(`CampfireScreen`, `RunSummaryScreen`) so they can be opened and previewed in isolation;
Game.cs integration below is the developer's next step.

## Files added
- `res://ui/grimoire_theme.tres` — shared Theme (parchment/ink/gold, panel/button styleboxes, default font)
- `res://ui/Campfire.tscn` + `res://ui/Campfire.gd` — party-recovery rest screen
- `res://ui/RunSummary.tscn` + `res://ui/RunSummary.gd` — post-run summary card
- `res://ui/INTEGRATION.md` — this file

## Campfire.tscn — wiring into Game.cs

Campfire root node: `CanvasLayer` named `Campfire`, script `Campfire.gd`, layer 2.
Signals (declared in Campfire.gd):
- `rest_chosen(rest_type: String)` — emits `"short"` or `"long"` after the confirm prompt
- `cancelled` — player backed out without resting

### 1. Instance + connect (Game._EnterTree, after the HUD labels at Main.tscn:467-474)

Add fields to `Game`:

```csharp
Godot.CanvasLayer _campfire;
Godot.CanvasLayer _summary;
bool _campfireWaiting = false;
```

In `_EnterTree()` (line 421), after `_pickerLabel` is added (line 474):

```csharp
var campfireScn = (Godot.PackedScene)GD.Load("res://ui/Campfire.tscn");
_campfire = (Godot.CanvasLayer)campfireScn.Instantiate();
AddChild(_campfire);
_campfire.Hide();
var campfireRoot = _campfire.GetNode("Campfire");
campfireRoot.Connect("rest_chosen", new Callable(this, nameof(_OnCampfireChosen)));
campfireRoot.Connect("cancelled", new Callable(this, nameof(_OnCampfireCancelled)));

var summaryScn = (Godot.PackedScene)GD.Load("res://ui/RunSummary.tscn");
_summary = (Godot.CanvasLayer)summaryScn.Instantiate();
AddChild(_summary);
_summary.Hide();
_summary.GetNode("RunSummary").Connect("dismissed", new Callable(this, nameof(_OnSummaryDismissed)));
```

(Godot 4 C#: `Node.Connect(StringName, Callable)`; `Callable.From(this, nameof(...))` also works.)

### 2. CAMPFIRE state becomes player-driven (Game._Process)

Shipped (synced 2026-10-31): the 8 s auto-advance and `CAMPFIRE_LEN` are **gone** from `Game.cs` — the
camp beat waits for a real choice. A human camp **never times out** (user-locked 2026-10-19, re-locked
2026-10-31: no countdown, no AFK default). The only timeout is `CAMPFIRE_WAIT_TIMEOUT = 25.0`, which
runs just for the pilot autopilot (headless verification smokes) and as a missing-scene soft-lock guard.

```csharp
case StageState.CAMPFIRE:
    if (!_campfireWaiting)
    {
        _campfireWaiting = true;
        _campWaitT = 0.0;
        ShowCampfire();   // see #3
    }
    else if ((_pilot || _campfire == null) && _campWaitT >= CAMPFIRE_WAIT_TIMEOUT)
    {
        // BENCH ONLY: pilot smokes / missing scene. Never fires for a human player.
        EndCampfire("short");
    }
    break;
```

Director.cs already stops spawning during CAMPFIRE (Director.cs:21: `st != 0 && st != 3` returns), so
the horde is paused while the screen is up. `get_tree().paused` is toggled by the GDScript only
around the confirm modal; the campfire screen itself does not pause the tree. The screen opts out of
pause anyway (root `process_mode = ALWAYS` — 4.7 has no `pause_picking` property at all, assigning
one throws and aborts `_ready`) so buttons and keys stay live *during* the modal pause — without it,
the ENTER/click confirm dies the moment the modal freezes the tree (the human-test bug this fixed).

### 3. Populate + show (new Game helper)

```csharp
private void ShowCampfire()
{
    var screen = (CampfireScreen)_campfire.GetNode("Campfire");
    screen.PartyData = partySnapshot(); // Array[Dictionary]: {name, klass, weapon, hp, max_hp, state, leader}
    screen.StageName = "Tanurial Forest";
    _campfire.Show();
}
```

`state` strings the GDScript reads: `"up" | "down" | "stable" | "out"`; `leader: bool`.
Shipped (2026-10-31 sync): the helper is `Game.CampfireCards()`, re-sent every frame the camp is up
(`Game.cs`:4150 area) so a cancel re-offers fresh data. **Open item**: `ui/Campfire.gd` still renders
`slots = 5` strips — at the 8-PC roster, Stack/Wash/Bruno get no camp strip (cosmetic, queued in `M2_STATUS.md`).

### 4. Apply the chosen rest (new Game handler)

Shipped (synced 2026-10-31): the handler is thin; all of the rest math lives in `Game.EndCampfire(restType)`.
The user-approved risk-vs-reward flip is live — **SHORT = power** (+25% max-HP heal + one free upgrade rank
offered as a `SHORT REST GIFT` card the player picks), **LONG = safety** (whole squad revived to FULL HP, no
free rank), and **both** rests pay `×1.20` multiplicative horde scaling (`_hordeScale`, read by the beat
director through `GetHordeScale()`). The camp also vacuums loose xp gems into the Leader's ring (2026-10-20).

```csharp
private void _OnCampfireChosen(string restType)
{
    EndCampfire(restType);   // hides the card, applies the rest, advances back to PLAY
}
```

Notes:
- `ReviveAllAtCampfire()` still exists (`Game.cs`:198-236): eliminated → return at 25%, downed → stand up,
  everyone +25% clamped. `EndCampfire("short")` calls it; `EndCampfire("long")` full-heals the squad outright.
- `TakeHit`/`ReviveAllAtCampfire` already reset death-save fields, so revived members re-enter cleanly.
- The free rank is **player-chosen**, not dealt: `EndCampfire("short")` opens the same smart-random options
  the level-up picker rolls (`RollOptions()` + `PICK_SLOTS`), so no `ApplyUpgrade`/`GrantRank` exposure is
  needed. `ApplyUpgrade` stays private and is only the capped-rank fallback.
- The level-up picker remains orthogonal: it fires on XP thresholds during `PLAY`; the camp gift card is
  a separate opening of the same picker.

### 5. Cancel path
If `cancelled` fires, the screen stays visible in CAMPFIRE and the player may re-choose; Game does not
advance the stage.

## RunSummary.tscn — wiring into Game.cs

RunSummary root: `CanvasLayer` named `RunSummary`, script `RunSummary.gd`, layer 3.
Signal: `dismissed` (journal / main-menu buttons, or ESC).

### 1. Instance + connect (shipped: NOT in `_EnterTree` — `ShowRunSummary` lazy-loads on run end)

```csharp
_summary.Connect("dismissed", new Callable(this, "_OnSummaryDismissed"));
```

(The campfire layer still connects in `_EnterTree`; the summary layer is instantiated only when a run
ends, so the boot path stays clean and a missing `RunSummary.tscn` degrades to print-only.)

### 2. Show on run end (shipped 2026-10-31: `Game.PrintSummary` → `ShowRunSummary`)

`PrintSummary` keeps the GD.Print line (a headless smoke run still logs the terminal beat) and hands
the card to `ShowRunSummary`, which **lazy-loads** `res://ui/RunSummary.tscn` (missing scene = print-only
warning, never a crash), sets the scene's `result` / `stage_name` / `level` / `xp_total` / `members`
props, parents the layer, connects `dismissed`, and **un-pauses the tree** so the R retry key gets input
frames (the card itself pauses on `_ready`).

```csharp
private void PrintSummary(string s)
{
    if (_summaryPrinted) return;
    _summaryPrinted = true;
    GD.Print("[10thAge] " + s + " | level " + _level + " | xp total " + (int)_xpTotal);
    ShowRunSummary(s);
}
```

`members` rows come from `Game.SummaryCards()` — `{name, state, picks}` where **picks are now tracked**:
every upgrade appends to `PartyMember.pickHistory` (`Game.cs`:4122), so the card's pick-history rows are
real copy, not the old placeholder.

`state` strings: `"survived" | "downed" | "stable" | "fallen"`.

### 3. Dismissal
```csharp
private void _OnSummaryDismissed()
{
    _summary.Hide();
    // developer decision: change to title scene / reload Main.tscn / open campaign journal (M4)
}
```

`CheckTpk` (Game.cs:916-927) already calls `PrintSummary("TPK - the whole party is down")`, and
`OnBossKilled` (Game.cs:274-278) calls `PrintSummary("VICTORY — Tanurial Forest cleared")`, so both
end states flow through the same summary screen without touching those call sites.

## Open questions (for the UX / developer)
1. ~~Keep `CAMPFIRE_LEN = 8.0` as an auto-short-rest timeout?~~ — **CLOSED (user 2026-10-19, re-locked 2026-10-31)**: campfire is purely player-driven. `CAMPFIRE_LEN` is gone; the only valve is `CAMPFIRE_WAIT_TIMEOUT = 25.0` and it runs **only** under `_pilot` or when the campfire scene is missing (soft-lock guard). A human camp waits for a real rest choice forever.
2. ~~Long-rest "next-stage power": which member gets the rank?~~ — **CLOSED (user 2026-10-19 rebalance)**: long rest grants **no** rank (full revive + full heal); the **short** rest gifts one free rank as a player-chosen `SHORT REST GIFT` picker card. Both rests pay the `×1.20` `_hordeScale` price the beat director consumes.
3. Where does RunSummary dismiss to — title screen, campaign map (M3/M4), or re-run? — shipped M1 behaviour: **R retries the stage** (`_RetryRun`, at most once per run); the journal/menu stays M4.
4. ~~Per-member pick history not tracked yet~~ — **CLOSED**: `PartyMember.pickHistory` appends every upgrade (`Game.cs`:4122) and renders on the card.
5. Confirm modal pauses the tree while open; confirm that pause blends with the dev's eventual flow (e.g. no input conflicts behind the modal).

## Read order (screenshot-style description)

Campfire screen (1280x720, 40px safe margins):
1. Backdrop is an **opaque** dark wash `Color(0.09, 0.08, 0.07, 1)` — the 2026-10-19 human-test
   asked for the boxes un-dimmed, and the only transparency on the card was the 52% Backdrop letting
   grass/projectiles show through behind the text (every StyleBoxFlat was already alpha 1); the
   grimoire panel sits centered (960x640).
2. Header: placeholder d20 token (dice flavor), then "THE CAMPFIRE" + "Tanurial Forest - camp between battles".
3. "THE COMPANY" roster bar, then a 2-column x 3-row grid of member cards (leader marked "LEADER" in gold). Each card: name + class/weapon, HP bar with fill + "N / M hp", and a colored state chip — READY (sage), DOWN - death saving (blood), STABLE - no more rolls (amber), OUT - back at campfire (grey, card dimmed). Empty slots show "empty slot".
4. "CHOOSE YOUR REST" then two cards: SHORT REST (safe, heals +25% max HP) vs LONG REST with a red
   "HORDE SCALING GROWS" risk chip — the required risk/reward copy. **The whole card is the Button**
   (2026-10-19 human-test: the 64 px title strip was the only clickable strip, so clicks on the card
   body felt dead; the Button now stretches the full card with the body/chip Labels as its children)
   — LEFT/RIGHT or clicking anywhere on a card picks; the chosen card gets a gold border.
    **Click routing, round 2 (2026-10-19)**: whole-card Buttons alone STILL wouldn't fire `pressed`
    when clicked (the modal's Buttons did). Two-pronged fix - (a) the Contents minimums overflowed the
    592 px inner frame (rest row landed at screen y~568+, COMMIT fully off the 720 px screen): cards
    168->138, dice 56->48, v-seps 16->10 / 14->8, rest cards 150->130 - everything fits and COMMIT is
    visible; (b) belt-and-braces: `Campfire.gd._input` routes raw left-clicks through each Button's
    rect via `_was_click_on(b, ev)`: accepts if ANY space agrees (global rect x get_global_mouse_position,
    global rect x raw ev.position, local Rect2(0,0,size) x get_local_mouse_position) - round 4's single
    global-space test still left the rest cards inert, so the helper is the belt-and-braces route;
    every choice-phase left click ALSO prints `[campfire] left-click at <pos>` + all three global rects
    to the Output panel (ground truth if clicks ever look dead again). Click = SELECT + OPEN the popup
    in one go (popup is the last gate; ENTER/click confirms),
    guarded by `!modal.visible` so the modal's working Buttons keep their own path; `_select` and
    `_on_commit_pressed` are idempotent with the `pressed` handlers. Containers (Backdrop/Frame/Contents
    /rows/cards) are now `mouse_passable = false` so nothing swallows a pick. NOTE: Campfire.gd is
    TAB-indented - its edits went in via byte-exact python replaces, verify with a `repr()` dump.
5. Bottom bar: hint "CLICK anywhere on a rest card (or LEFT / RIGHT)   CLICK COMMIT (or ENTER)
   ESC declines" + "COMMIT TO THE EMBERS". ENTER/RETURN replaced SPACE because SPACE is now the
   Leader's in-world dash.
6. Confirm prompt (modal, gold-border panel): "COMMIT TO THE EMBERS?" + rest-specific consequence
   summary; "REST NOW" (click or ENTER) emits rest_chosen and closes the screen, "CHANGE MY MIND"
   / ESC returns to the choice.

RunSummary screen:
1. Dimmed backdrop; centered card (740x470).
2. Banner: "VICTORY - Tanurial Forest cleared" (gold) or "TPK - the whole party is down" (blood).
3. Stats row: STAGE / LEVEL / XP TOTAL (big values: "Tanurial Forest", "7", "130").
4. "THE COMPANY - PICK HISTORY" section: per-member rows — name, state chip color, pick-history placeholder ("Pick history: Maul dmg +25% x2").
5. Footer: "Return to campaign" + "Main Menu"; ESC dismisses too. Dismiss emits `dismissed` for the dev to route to the title/journal.
