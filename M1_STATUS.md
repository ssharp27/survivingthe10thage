# M1 STATUS — 2026-10-18 (full stage flow verified through VICTORY)

M1 greybox of "Surviving the 10th Age" (Tanurial Forest) builds green, is **human-playable**,
and the full campaign-battle loop is verified in-engine headless:
roster → horde I → mini-boss slain → campfire → horde II (live-cap 40) → BOSS engaged →
`VICTORY — Tanurial Forest cleared`.

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
- **Pilot picker is heal-aware**: picks the party-heal option when any live member is below 45 %
  HP, otherwise rotates through named member dmg picks.

## Verified in-engine (smokeAD, M1_PILOT=1, ~316 s sim)

```
[10thAge] squad: Baldrun (Maul) + Mosiah (Rapier) | leader: Baldrun
[10thAge] upgrade: Baldrun dmg +15%
[10thAge] mini-boss engaged
[10thAge] upgrade: Mosiah dmg +15%          (x2 — per-member dmgMul path)
[10thAge] mini-boss slain — campfire: party back at half HP
[10thAge] (pilot) horde II ... | live enemies 40   (director cap active)
[10thAge] BOSS engaged
[10thAge] upgrade: party healed 25%         (heal-aware pilot pick)
[10thAge] VICTORY — Tanurial Forest cleared | level 7 | xp total 130
```
stderr clean, including no input-parse warnings. Last logs:
`C:\Users\Steve\AppData\Local\Temp\smokeAD.out` / `smokeAD.err`.

## How to play (human, GUI)

Desktop `.bat` launches the editor (prepends `C:\Users\Steve\dev-tools\dotnet` to PATH — required;
system dotnet is runtime-only). Run the main scene; **WASD or arrows** move the Leader,
party follows on the bodyguard ring, everyone auto-attacks. At a level-up the picker label
shows options — press **A / S / D** (confirm-any-time; the A/S/D letters also walk — VS-style overlap).

## Godot 4.7 C# pitfalls (found empirically, keep for future work)

- **C# `_draw()` is never invoked** (binding dropped the hook; a plain `public void _draw()`
  silently runs 0 times — probe proved it). Drawing *outside* `_draw` is engine-enforced
  ("Drawing is only allowed inside this node's `_draw()`…"). **Workaround**: GDScript child
  `src/AttackTelegraph.gd` (GDScript `_draw` works), attached from C# via
  `Set("script", GD.Load<Godot.Script>(...))` and triggered with `Call("show_attack", …)`.
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

`M1_PILOT=1` env ⇒ leader autopilots (orbit / walk-to-body + stand-still revive channel /
boss strafe + face-lock), bullet volleys pass through, incoming party damage ×0.15, and the
picker auto-confirms at 2 s with a heal-aware choice. Implemented in Game.cs
(`_pilot`, `faceTo`, bullet-dodge gate, `PickerTick` pilot branch).

## Build + smoke recipe

```powershell
$env:Path = 'C:\Users\Steve\dev-tools\dotnet;' + $env:Path
cd C:\GitHub\survivingthe10thage
dotnet build survivingthe10thage.csproj --no-incremental -nologo   # NO -o: SDK props put the DLL
# at .godot\mono\temp\bin\Debug — exactly where the engine loads from
$env:M1_PILOT = '1'
$g = 'C:\AI Coding\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
& $g --path 'C:\GitHub\survivingthe10thage' --headless --quit-after 42000 > "$env:TEMP\smoke.out" 2> "$env:TEMP\smoke.err"
```
PS 5.1 gotchas: no `&&`; `>` redirect is UTF-16; kill godot processes before relaunch;
`--quit-after N` at ~133 fps ⇒ N/133 ≈ seconds of sim.
