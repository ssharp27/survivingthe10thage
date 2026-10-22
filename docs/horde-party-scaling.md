# Horde scaling to party size — session record 2026-10-22

Source of the rule: human playtest 2026-10-22 ("solo Baldrun was swamped by a full-sized
horde"), user rule: the horde SCALES TO THE PARTY (VS-style: the survivors fund the horde).

## Where the code lives

- `src/Director.cs` — `_Process`, ~lines 33-61 (as of 2026-10-22): the party factor itself
- `src/Game.cs` — `GetPartyInPlay()` at ~line 772: the party count feeding the factor
- `src/Game.cs` — `_hordeScale` (rest/tier factor) at ~line 184, `GetHordeScale()` ~line 1072

## The formula

```
partyScale = GetPartyInPlay() / 2.0     // normalized: M1's 2-PC pacing = the 1.0 mark
```

Applied to BOTH spawn-volume dials:

1. **Live cap**: `if (GetLiveEnemies() >= (int)(MAX_LIVE * scale * partyScale)) return;`
2. **Cadence**: `_spawnTimer /= (scale * partyScale);`
   — solo = timer x2 (thinner), a full 5-PC squad = x0.4 (denser)

Effective multipliers: solo 0.5x, 2 PCs 1.0x (baseline), 3 PCs 1.5x, 5 PCs 2.5x.

## What it deliberately does NOT scale

Body HP keeps its rest-only x1.20 price (`Game.cs` `_hordeScale *= 1.20` after long/short
rests, consumed at `e.maxHp = hpBase * tierBump * GetHordeScale()` and in `Director.Spawn`).
A small squad faces FEWER bodies, not FATTER ones. The party factor only densifies volume.

## The party count definition

```csharp
// src/Game.cs, ~line 772
public int GetPartyInPlay()
{
    int n = 0;
    foreach (PartyMember pm in _party)
        if (pm.joined && !pm.eliminated)
            n++;
    return n < 1 ? 1 : n; // never below 1: something must still be on the menu
}
```

- Counts JOINED, not-yet-eliminated members (downed ones still count — they are squad
  and they return at campfire).
- Floor of 1 keeps a solo run spawnable.
- Field-recruit joins thicken the horde live: `joined` flips on the join beat, so the
  density rises mid-run as allies join (M1 starts solo-Leader => 0.5x until the recruit lands).

## Interaction with the other horde dials (for context)

- `Game._hordeScale` (rest/tier, x1.20 steps) multiplies cap and cadence ALONGSIDE partyScale
  (both divide `_spawnTimer`, both feed the cap) — the two factors are independent and
  composable.
- Boss phases multiply `_spawnTimer *= 2.0` on top (50 % rate pacing rule).
- Per-stage density ramp (`1.1 - 0.6 * (t / half)`) and leg-start easing are unchanged;
  partyScale is applied after those.

## Verbatim block (src/Director.cs, _Process, 2026-10-22)

```csharp
// user rule (human playtest 2026-10-22): the horde SCALES TO THE PARTY. A solo
// Baldrun opening runs a thin swarm and every ally who joins thickens it (VS-style:
// the survivors fund the horde). Normalized so M1's two-PC pacing is the 1.0 mark -
// solo runs half-density, a full 5-PC squad runs 2.5x. This scales SPAWN VOLUME
// (live cap AND cadence); body HP keeps its rest-only x1.20 price, so a small
// squad faces fewer bodies, not fatter ones.
float partyScale = (float)g.GetPartyInPlay() / 2.0f;
// horde scale multiplies the cap too: a long-rest horde runs bigger AND denser
float scale = (float)g.GetHordeScale();
if (g.GetLiveEnemies() >= (int)(MAX_LIVE * scale * partyScale))
    return; // at cap: stop spawning, don't reset the timer either
...
_spawnTimer /= (scale * partyScale);
```
