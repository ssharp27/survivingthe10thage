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

### 2. CAMPFIRE state becomes player-driven (Game._Process, lines 541-547)

Current:

```csharp
case StageState.CAMPFIRE:
    if (_stageT >= CAMPFIRE_LEN)
    {
        _state = StageState.PLAY2;
        _stageT = 0.0;
    }
    break;
```

Replace with a wait-for-input branch (the 8s auto-advance is removed; CAMPFIRE_LEN can be kept
as an optional fallback "auto short rest" timeout — an open question below):

```csharp
case StageState.CAMPFIRE:
    if (!_campfireWaiting)
    {
        _campfireWaiting = true;
        _stageT = 0.0;
        ShowCampfire();   // see #3
    }
    // remains in CAMPFIRE until _OnCampfireChosen advances to PLAY2
    break;
```

Director.cs already stops spawning during CAMPFIRE (Director.cs:21: `st != 0 && st != 3` returns), so
the horde is paused while the screen is up. `get_tree().paused` is toggled by the GDScript only
around the confirm modal; the campfire screen itself does not pause the tree.

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
`partySnapshot()` maps each `PartyMember` in `_party` (leader first) to one dictionary; out-of-roster
slots render as empty automatically (grid draws 6 slots).

### 4. Apply the chosen rest (new Game handler)

```csharp
private void _OnCampfireChosen(string restType)
{
    _campfire.Hide();
    _campfireWaiting = false;

    if (restType == "short")
    {
        // SHORT REST: heal everyone +25% max HP (clamped), horde pacing unchanged.
        foreach (PartyMember m in _party)
        {
            m.hp += m.maxHp * 0.25f;
            if (m.hp > m.maxHp) m.hp = m.maxHp;
        }
    }
    else
    {
        // LONG REST: full heal, fallen return at 25% HP, next-stage power, horde scaling grows.
        ReviveAllAtCampfire(); // Game.cs:198 — revived members come back at 25% HP
        foreach (PartyMember m in _party)
        {
            m.hp = m.maxHp;    // full heal for the living (ReviveAllAtCampfire only adds +25%)
        }
        // TODO M2: grant one member a damage rank (ApplyUpgrade-like hook) + set a horde-scaling
        // flag consumed by Director.cs spawn pacing (Director.cs:29-32 already ramps post-mini-boss).
    }

    _state = StageState.PLAY2;
    _stageT = 0.0;
}

private void _OnCampfireCancelled()
{
    // stay in CAMPFIRE; screen stays visible so the player can re-choose
    _campfireWaiting = false;
    ShowCampfire(); // or leave showing; keep simple: leave visible
}
```

Notes:
- `ReviveAllAtCampfire()` already exists (Game.cs:198-236): eliminates→return at 25%, downed→stand up,
  everyone +25% clamped. Long rest layers full heal on top of it.
- `TakeHit`/`ReviveAllAtCampfire` already reset death-save fields, so revived members re-enter cleanly.
- The level-up picker (`PickerTick`, Game.cs:871-904) is orthogonal: it fires during PLAY1/PLAY2 on XP
  thresholds; the campfire does not open the picker. If the dev wants long rest to include a pick,
  expose `ApplyUpgrade` (currently private, Game.cs:906) or add a public `GrantRank(int memberIndex)`.

### 5. Cancel path
If `cancelled` fires, the screen stays visible in CAMPFIRE and the player may re-choose; Game does not
advance the stage.

## RunSummary.tscn — wiring into Game.cs

RunSummary root: `CanvasLayer` named `RunSummary`, script `RunSummary.gd`, layer 3.
Signal: `dismissed` (journal / main-menu buttons, or ESC).

### 1. Instance + connect (same `_EnterTree` block as above)
```csharp
_summary.GetNode("RunSummary").Connect("dismissed", new Callable(this, nameof(_OnSummaryDismissed)));
```

### 2. Show on run end (Game.PrintSummary, lines 929-935)

Replace the print-only body:

```csharp
private void PrintSummary(string s)
{
    if (_summaryPrinted) return;
    _summaryPrinted = true;
    GD.Print("[10thAge] " + s + " | level " + _level + " | xp total " + (int)_xpTotal);

    var screen = (RunSummaryScreen)_summary.GetNode("RunSummary");
    screen.Result = s.Contains("VICTORY") ? "victory" : "tpk";
    screen.StageName = "Tanurial Forest";   // read from stage data when meta-map exists (M3)
    screen.Level = _level;
    screen.XpTotal = (int)_xpTotal;
    screen.Members = partySnapshot();       // {name, state, picks} — picks placeholder until tracked (M2)
    _summary.Show();
}
```

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
1. Keep `CAMPFIRE_LEN = 8.0` (Game.cs:38) as an auto-short-rest timeout, or make campfire purely player-driven?
2. Long-rest "next-stage power": which member gets the rank, and how does Director horde scaling change (Director.cs:29-32)?
3. Where does RunSummary dismiss to — title screen, campaign map (M3/M4), or re-run?
4. Per-member pick history (`picks`) is not tracked yet; placeholder text renders until M2 tracking exists.
5. Confirm modal pauses the tree while open; confirm that pause blends with the dev's eventual flow (e.g. no input conflicts behind the modal).

## Read order (screenshot-style description)

Campfire screen (1280x720, 40px safe margins):
1. Backdrop dims the arena to 52%; the grimoire panel sits centered (960x640).
2. Header: placeholder d20 token (dice flavor), then "THE CAMPFIRE" + "Tanurial Forest - camp between battles".
3. "THE COMPANY" roster bar, then a 2-column x 3-row grid of member cards (leader marked "LEADER" in gold). Each card: name + class/weapon, HP bar with fill + "N / M hp", and a colored state chip — READY (sage), DOWN - death saving (blood), STABLE - no more rolls (amber), OUT - back at campfire (grey, card dimmed). Empty slots show "empty slot".
4. "CHOOSE YOUR REST" then two cards: SHORT REST (safe, heals +25% max HP) vs LONG REST with a red "HORDE SCALING GROWS" risk chip — the required risk/reward copy. LEFT/RIGHT or clicking picks; the chosen card gets a gold border.
5. Bottom bar: hint "LEFT / RIGHT choose rest SPACE confirm ESC cancel" + "COMMIT TO THE EMBERS".
6. Confirm prompt (modal, gold-border panel): "COMMIT TO THE EMBERS?" + rest-specific consequence summary; "REST NOW" emits rest_chosen and closes the screen, "CHANGE MY MIND" / ESC returns to the choice.

RunSummary screen:
1. Dimmed backdrop; centered card (740x470).
2. Banner: "VICTORY - Tanurial Forest cleared" (gold) or "TPK - the whole party is down" (blood).
3. Stats row: STAGE / LEVEL / XP TOTAL (big values: "Tanurial Forest", "7", "130").
4. "THE COMPANY - PICK HISTORY" section: per-member rows — name, state chip color, pick-history placeholder ("Pick history: Maul dmg +25% x2").
5. Footer: "Return to campaign" + "Main Menu"; ESC dismisses too. Dismiss emits `dismissed` for the dev to route to the title/journal.
