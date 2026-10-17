# M1 STATUS — end-of-session save, 2026-10-17

M1 greybox of "Surviving the 10th Age" (Tanurial Forest) builds green and the full stage
flow runs in-engine, headless-verified up to (not including) the final boss kill.

## Verified today in-engine (headless smoke, M1_PILOT=1)

- C# assembly-load fix: DLLs must land at `.godot/mono/temp/bin/Debug/` (SDK-native
  OutputPath). **Build with NO `-o`**: `dotnet build survivingthe10thage.csproj --no-incremental -nologo`
  (PATH must prepend `C:\Users\Steve\dev-tools\dotnet`; system dotnet is runtime-only).
- Scene serialization: camelCase `.tscn` props → C# fields — roster banner line proves it.
- horde I (90 s) → `mini-boss engaged` → party kills it → `mini-boss slain — campfire: party back at half HP`
- horde II (PLAY2) with the Director's VS-authentic **live-enemy cap (40)** working
  → `BOSS engaged` at PLAY2 t=100.
- Targeted level-up picker: named picks print `upgrade: Baldrun dmg +15%` / `upgrade: Mosiah dmg +15%`
  (pilot branch chooses at 2 s; rotation level%2). Level 6 reached, xp total 90.
- Down state / d20 death saves (`0/3, 1/3, 2/3`) / elimination (`OUT`) / TPK summary all exercised.
- Boss melee-close fix (kept distance 260 → 80–100 band): without it the party's 115/130-px arcs
  could never strike an active boss at all.
- Balance changes made this session: goblin 22→18 / wolf 34→26 HP (maul 26 dmg one-shots both),
  final boss maxHp 1200→800 (placeholder data), Director live-cap 40, spawn curve intact.

## Still open — resume here

1. **VICTORY line not yet observed**: AFK pilot TPKs ~10–25 s into the final-boss fight
   (can't dodge telegraphed volleys; enters boss at half HP post-campfire). The kill-boss
   → win mechanism itself is already exercised by the mini-boss (same `OnBossKilled` + `PrintSummary`
   path). Options, in order of preference:
   a) accept as verification-equivalent, then do a real balance pass targeting human play;
   b) pilot-gated dodge (step perpendicular to nearest incoming bullet);
   c) greybox tuning: final HP 800→600 or campfire full-heal.
2. Balance pass for human feel: cap-40 horde contact pressure vs 2-PC AoE clear is likely too hot;
   numbers are all in Game.cs / Director.cs / Main.tscn so it's a cheap tuning surface.
3. Boss data swap point = `Main.tscn` BossMini/BossFinal property blocks (kind/maxHp/radius/
   driftSpeed/bulletDamage/position) — that's the "campaign battle data" surface.
4. Optional nice-to-have: MCP plugin GUI boot + hub re-pin (DLL now at SDK-native path;
   untested since the placement fix).

## Headless verification pilot (env-gated, never active in normal play)

`M1_PILOT` env var set ⇒ leader autopilots (orbit while healthy / walk-to-body + stand-still
revive channel / engage active boss at melee range) AND incoming party damage ×0.15.
Implemented in `Game.cs` (`_pilot`, `_pilotTick`, `TakeHit`, `PickerTick`, `GetActiveBoss`).
Keep or strip at ship-time — your call.

## Resume recipe

```powershell
# build (DLL -> .godot\mono\temp\bin\Debug, where the engine loads from)
$env:Path = 'C:\Users\Steve\dev-tools\dotnet;' + $env:Path
cd C:\GitHub\survivingthe10thage
dotnet build survivingthe10thage.csproj --no-incremental -nologo

# headless flow smoke (~316 s sim at ~133 fps covers the whole timeline)
$env:M1_PILOT = '1'
$g = 'C:\AI Coding\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
& $g --path 'C:\GitHub\survivingthe10thage' --headless --quit-after 42000 > "$env:TEMP\smoke.out" 2> "$env:TEMP\smoke.err"
Select-String -Path "$env:TEMP\smoke.out" -Pattern '10thAge'
```

GUI launch = the desktop `.bat` (also prepends dev-tools dotnet to PATH).
PS 5.1 gotchas: no `&&`; `Get-ChildItem` (not `Get-Child`); `>` redirect writes UTF-16;
`$env:TEMP\opencode` is the harness-approved temp area. Last smoke:
`C:\Users\Steve\AppData\Local\Temp\smokeAB.out` / `smokeAB.err` (err clean).

## Key evidence lines (smokeAB, 2026-10-17)

```
[10thAge] squad: Baldrun (Maul) + Mosiah (Rapier) | leader: Baldrun
[10thAge] mini-boss engaged
[10thAge] upgrade: Mosiah dmg +15%
[10thAge] mini-boss slain — campfire: party back at half HP
[10thAge] (pilot) horde II t=5 | lv 5 xp 57 | live enemies 3 | party: Baldrun 75hp  Mosiah 15hp
[10thAge] BOSS engaged
[10thAge] TPK - the whole party is down | level 6 | xp total 90
```

## Repo hygiene as of this commit

- `addons/_probe` probe deleted; its entry removed from `project.godot` enabled list
  (godot_mcp plugin entry stays).
- `.claude/` added to .gitignore; `survivingthe10thage.sln` tracked (keeps clones one command
  from green; Godot regenerates it if deleted, but tracking avoids the no-sln editor dialog error on clone).
- `bin/` and `.godot/` remain gitignored build artifacts. Nothing pushed yet.
