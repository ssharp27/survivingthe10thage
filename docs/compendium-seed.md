# Compendium seed — parked 2026-11-04, work on it LATER

The user asked for a player-facing **compendium** that explains the game's mechanics.
This file is the seed: the locked decisions and the verified number tables below.
Nothing here is final copy — it exists so the later compendium pass never re-derives math.

## Locked decision (user 2026-11-04)

The user **likes compounding**. Keep the multiplicative stacking exactly as coded;
do NOT rework damage/reach/speed tags into additive points for copy's sake.
Card copy must instead be honest that bonuses COMPOUND per rank.

## Meta vs in-run (user 2026-11-08)

**All meta progression and character upgrades are ADDITIVE. Only IN-RUN upgrades
use the compounding option.** The stacking rule below (and the verified card tables)
describes the IN-RUN surface: level-up cards, short-rest gifts. Any future meta-progression
or permanent character-upgrade screens must promise flat, additive numbers, and the
compendium copy must keep the two surfaces visibly separate so players never read a
meta "+10%" as a compounding rank.

## Stage flow — verified player-facing breakdown (2026-11-08, source: Game.cs beat tables + Director.cs)

Stage copy truth: the plate one-liners are `STAGE_BLURB` (`src/Game.cs` ~148), rendered at
`_stageBlurb[k].Text` and prepended by `ShowStageSheet()`; the full card readout (schedule /
density / recruits / xp) is composed OFF LIVE beat-table data, so it self-updates with data edits.
The table below is that live data, verified this pass — the compendium must mirror it, not re-derive it.

### Beat table (run seconds — picker/join/camp pauses never burn a beat)

| Beat | Event |
| --- | --- |
| 30 | Ally I (escort join) |
| 60 | Tier-2 gate |
| 180 | Mini-boss I (300 hp) |
| 240 | Ally II |
| 360 | Boss I |
| 420 | Ally III |
| 540 | Tier-3 gate |
| 660 | Mini-boss II |
| 780 | Ally IV |
| 840 | Tier-4 gate |
| 960 | Boss II (second boss1 body) |
| 1200 | FINAL boss |
| 1260 | Wrap / victory |

A campfire sits after EVERY boss kill (mini, mid, and final alike).

### Spawn cap and cadence

- Live cap = `40` (`Director.MAX_LIVE`) × horde scale × (party in play / 2).
- Cadence = `max(0.35, 1.1 − 0.6 × legFrac)` within a tier leg, × 2 (slower) while a boss is
  live, then divided by (horde scale × party scale) — more party/horde pressure = faster spawns.
- Horde EVENT swarms are BUDGET-FREE: never counted against the cap.

### Tier composition

| Tier | Enters | Bodies (hp, xp) |
| --- | --- | --- |
| 1 | goblins only; 50% of plain slots enter as Goblin 2 (G2_CH) | goblin (30 hp, 1 xp) / goblin2 (25 hp, 1 xp, speed 100) |
| 2 | 30% wolf-family rolls split 35% wolf2 PACK of 3 / 30% lone wolf3 / rest standard; fast goblins on 20% per-spawn rolls (FAST_CH); 1 hob per 10 goblins | wolf (70 hp, 2 xp); wolf2 (70 hp, contact 4, 2 xp: long-sight charge → pause → straight-line dash with overshoot, no hop); wolf3 (100 hp, contact 4, 3 xp: tanky); fastgoblin (40 hp, 2 xp: rush → pause → straight-line lunge); hobgoblin (60 hp, 3 xp) |
| 3 | rider packs on 5% per-spawn rolls: 2 sword + 2 bow on one bearing | rider (140 hp) / riderbow (80 hp), 5 xp (user retune 2026-11-09: 100→140 / 60→80); mage 1 per 2 hobs (40 hp, 6 xp) |
| 4 | ogre on 5% per-spawn rolls | ogre (220 hp, 10 xp) |

### Horde events (bonus swarms)

Boot-armed: first swarm at 90 s run time, then every 60 s. Each swarm is 20 × horde scale ×
party scale tier-1 goblins, budget-free, 1 xp per body.

### Rests, escorts, joins

- LONG rest: whole squad revived to FULL hp, no free rank; horde scaling × 1.20.
- SHORT rest: +25% max-HP heal PLUS one free upgrade rank, offered as a 3-of-6 gift picker;
  also × 1.20 horde scaling.
- Escorts: each ally beat drops 6 tier-1 goblins as the escort pack (they scale with horde);
  the PC lands ~300 px past the camera (+90 margin) and joins at its ≥25% HP floor.

## The stacking rule (source: src/StatTags.cs KIND + src/Game.cs ApplyUpgrade ~line 4672)

- KIND 0 (multiplier): each rank multiplies the live stat. 3 ranks = (per-rank)^3.
  Example reach x1.15: rank 1 +15%, rank 2 +32%, rank 3 +52% (1.15^3 = 1.5209).
  The +52% is NOT 3 x 15% (+45%): the gap is the compounding, and the copy must say so.
- KIND 1 (points): additive on the BASE (crit chance 5% seed, crit damage 1.5 seed, hp on authored maxHp).
- KIND 2 (count): additive strikes/familiars (multishot +1 bolt per rank).

## DoT crossfire — verified math (locked 2026-11-07, source: src/Combatant.cs DOT_VULN / UniqueDoTs / TakeDamage)

The user's DoT QoL rule: enemies take **+5% more incoming damage per UNIQUE DoT running on them**,
HALF that on bosses so they don't get too easy. Copy must say **per unique DoT**, never "per hit".

- Counted **at the hit** from the LIVE window pairs — `burnT > 0` / `bleedT > 0` / `poisonT > 0`
  — one count per PAIR, never per stack. Re-striking the same venom never double-dips, and an
  expired DoT stops paying. Three DoTs live at once = **+15%** (bosses **+7.5%**).
- Boss branch = kinds `boss1 / boss2 / final / mini / mini1 / mini2` (`Combatant.IsBossBody()`,
  a mirror of `Game.IsBossKind`).
- It multiplies the SAME rail guiding light rides (`amount *= (1 + vulnMul)` then `*= (1 + cb)`),
  so the two VULNERABILITY sources **COMPOUND** exactly as the locked compounding decision wants:
  guiding light +30% + two DoTs live = `1.30 x 1.10` = **+43% incoming**.
- The DoT TICKS themselves call `TakeDamage`, so they ride the bonus too — a body already
  poisoned by Brook and burning from Stack has its poison tick boosted by the crossfire as well.
- **NOT a tag slot**: tag 15 `vulnerability` stays the guiding-light surface (`Combatant.vulnMul`)
  only, because `Vulnerable()` is max-mark / refresh-never-stack and three DoTs could never
  stack through it. Every FUTURE DoT pair adds one count line to `UniqueDoTs()` and inherits
  the bonus for free — the compendium must re-read that rail, not hardcode "three DoTs".

## Field pickups — rare kill drops (assistant-picked knobs, 2026-11-08, source: src/Game.cs PICKUP_CH + src/Pickup.cs)

The user's CONCEPT (2026-11-08): "small random pickups, such as a healing orb, an XP magnet, or short
damage buffs to help runs". Every number here is an assistant pick and stays retunable; the magnet
behaviour was the user's choice (pull dropped gems, never inject XP). **IN-RUN ONLY** — the meta law
above keeps field drops out of the additive meta pool.

- Drop roll `PICKUP_CH = 0.02` per ORDINARY enemy kill (stage 1 only); escort kills NEVER drop — those
  bodies are the rescue meter, not a loot source. Flavour weights 40 heal / 30 magnet / 30 rage.
- HEAL ORB `HEAL_AMT = 15` **FLAT** hp on the LOWEST-HP standing member WHATEVER who grabs it (user 2026-11-09 retarget), clamped at own `EffMaxHp` — flat, not a max-HP
  fraction, so an early run cannot chain a bar. It is the only HP recovery that does NOT pay the ×1.20
  rest/horde tax, which is why the roll stays scarce.
- XP MAGNET `MAGNET_T = 6 s`: the gem collection bubble becomes WHOLE-FIELD, the vacuum itself is
  untouched (the 380 px/s ride + the picker's own XP_PCT math at pickup). It pulls DROPPED GEMS.
- RAGE VIAL `RAGE_MUL = 0.2` for `RAGE_T = 8 s` on LIVE enemies (the same live census the prey
  pickers run — the enemy registry keeps dead entries), riding the guiding-light `Vulnerable()`
  **max-mark / refresh-never-stack** rail: two vials, or a vial beside guiding light, never stack —
  they take the deepest mark, and it COMPOUNDS with crossfire per the section above.
- Field lifetime `LIFE_D = 18 s`, walk-over pick radius `PICK_R = 18 px`: reaching it is a choice, not free.

## Motion language — walking without sprite frames (assistant-picked knobs, 2026-11-08, source: src/Enemy.cs SWAY_* + src/PartyMember.cs rig)

NO sprite animation anywhere: every creature and PC is ONE static pose, and all of its life comes
from the transform rig. Compendium copy describes motion, it never promises frames. There are TWO rigs,
ONE language (the table slide) run at two speeds, so copy must not describe them alike:

- **THE PARTY = a table slide** — lateral sway + dip + lean trailing the push (`PartyMember.cs`
  ~1922). They are minis on a board, and that is the point of the contrast.
- **CREATURES = the SAME party slide, run FASTER** (`Enemy.cs` MONSTER SWAY, copied from `PartyMember.cs`
  1910–1936): an odometer of the body's REAL travel (`WrapDelta`, one frame of lag) feeds an eased px/s;
  cadence is that speed times `SWAY_RATE = 12` rad/s (the party's is 7.2 — monsters swing quicker),
  amplitude `SWAY_AMP = 3.4` px lateral (party 3.0), with the party's half-rate dip and a lean that trails
  the sway (`sw * 0.017`, the party constant). A body that stops EASES its last swing back level — a set-
  down, never a jolt — and below `SWAY_MIN = 30` px/s it simply stands. Per-kind amp: hobgoblin 1.25 / wolf + wolf2 0.9 / wolf3 0.75.
- THE TWO-BEAT HEEL GAIT (abs(sin) step plant + squash) was BUILT and then SCRAPPED the same day — user
  hand-play called it "the head is vibrating rapidly, really difficult to look at". Do not resurrect it;
  compendium copy must not describe a foot-step plant either.
- Both rigs read the body's OWN wrap-aware displacement, so no movement lane has to hand a velocity to the
  visuals, and all of it is visuals-only: the hitbox is the ENEMY node and the ogre SlamDisc is its CHILD,
  so sway cannot nudge anything.
  to hand a velocity to the visuals, and all of it is visuals-only: the hitbox is the ENEMY node and
  the ogre telegraph disc is its CHILD, so squash + lean cannot nudge anything.




## Level-up cards — verified percentage tables (2026-11-04, from OPT_* tables Game.cs 176-254)

Attack-speed cards read as SPEED GAIN (1/0.86 = +16%, 1/0.85 = +18%).
Counts stay counts. All ranks cap at 3/3 (rank 4 = evolution, M2 parked content).

### Baldrun (Maul)
| Card | Per rank | At 3/3 |
| --- | --- | --- |
| longer reach | +15% reach | +52% reach |
| heavier blows | +10% damage | +33% damage |
| swifter swings | +16% attack speed | +57% attack speed |
| draging maul | bodies 25% slower, 2.5 s window (bosses immune) | bodies 75% slower |
| fire rune | +1 burn dps /5 s | +3 burn dps /5 s |

### Mosiah (Rapier)
| Card | Per rank | At 3/3 |
| --- | --- | --- |
| quicker thrusts | +16% attack speed | +57% attack speed |
| deeper thrust | +16% reach AND pierce +1 body | +56% reach, pierce +3 bodies |
| sharper points | +10% damage | +33% damage |
| deadly aim | crit chance 5% -> 13% | crit 29% |
| deadlier aim | crit hits 150% -> 165% | crit hits 195% |

### Asper (Longbow)
| Card | Per rank | At 3/3 |
| --- | --- | --- |
| heavier arrows | +12% damage | +40% damage |
| quicker draw | +18% attack speed | +63% attack speed |
| piercing shot | pierce +1 body | pierce +3 bodies |
| multishot | +1 bolt per volley | +3 bolts per volley |
| keener aim | crit chance 5% -> 13% | crit 29% |

### Alvora (Guardian ring)
| Card | Per rank | At 3/3 |
| --- | --- | --- |
| spirit brand | +12% damage | +40% damage |
| wider ring | +16% ring reach | +56% ring reach |
| quicker pulse | +18% attack speed | +63% attack speed |
| guiding light | marked bodies take +10% more damage (all sources) | +30% more |
| weighty ring | ring bodies 10% slower, 2.5 s (bosses immune) | 30% slower |

### Yu (Familiars/blast)
| Card | Per rank | At 3/3 |
| --- | --- | --- |
| occult flame | +12% blast damage | +40% blast damage |
| wider burst | +16% blast area | +56% blast area |
| quicker ritual | +18% summon rate | +63% summon rate |
| twin ritual | +1 familiar per ritual | +3 familiars per ritual |
| lucky explosions | +5% double-blast chance | +15% double-blast chance |

### Stack (Firebolt)
| Card | Per rank | At 3/3 |
| --- | --- | --- |
| keener flame | +10% damage | +33% damage |
| quicker fuse | +18% attack speed | +63% attack speed |
| split bolt | pierce +1 body | pierce +3 bodies |
| cinder bloom | dying burning bodies burst 60 px for 25% of the firebolt | 75% of the firebolt |
| linger flame | burn lasts +2 s | burn lasts +6 s |

### Wash (Shield)
| Card | Per rank | At 3/3 |
| --- | --- | --- |
| heavier shield | +10% damage | +33% damage |
| quicker hurl | +18% attack speed | +63% attack speed |
| more rebounds | shield bounces +1 body | bounces +3 bodies |
| sharper rim | crit chance 5% -> 13% | crit 29% |
| crushing blow | crit hits 150% -> 175% | crit hits 225% |

### Bruno (Axes)
| Card | Per rank | At 3/3 |
| --- | --- | --- |
| heavier axe | +10% damage | +33% damage |
| quicker throw | +18% attack speed | +63% attack speed |
| extra axes | +1 axe per throw (1 -> 2) | +3 axes (1 -> 4) |
| broader blade | blades +25% size | blades +75% size |
| bleeding edge | hit bodies bleed 1 dmg/s for 4 s (dps scales with damage card) | rank-3 bleed |

### Brook (Daggers)
| Card | Per rank | At 3/3 |
| --- | --- | --- |
| sharper edges | +10% damage | +33% damage |
| quicker jabs | +18% attack speed | +63% attack speed |
| lingering venom | poison lasts +2 s | poison lasts +6 s |
| deadly angle | crit chance 5% -> 13% | crit 29% |
| lucky strikes | +5% chance a hit strikes twice | +15% chance |

## Copy traps to settle in the compendium pass

1. Percentage cards that COMPOUND must carry "compounds per rank" framing (or the
   compendium explains the multiplier math once and cards stay short).
2. Crit damage cards differ per character at 3/3: Wash 225% vs Mosiah 195%. Never
   promise a shared crit-damage number in copy.
3. Duration numbers (5 s burn / 4 s bleed / 2.5 s slow windows) are baked at call
   sites; compendium copy must match the code, not the OPT_LABEL drift.
4. Descriptive text surfaces live in six places only (see RESUME_NOTES 2026-11-04
   map): OPT_LABEL (Game.cs 176-190), SEL_BONUS/SEL_ULT (Game.cs 4140-4159),
   ShowBreakdown stats sheet (Game.cs 4307-4320), StatTags NAMES (StatTags.cs 62-68),
   camp pair (ui/Campfire.tscn static text + ui/Campfire.gd _rest_summary 309/310),
   summary + pause cards (ui/RunSummary.tscn/.gd, ui/PauseMenu.gd 45/260/263/319).
5. Crossfire copy must name the **UNIQUE DoT** rule and the **HALF on bosses** branch: it is a
   body-wide incoming-damage bonus computed at the hit, so it never reads as a card bonus, it
   disappears when the DoT window expires, and it COMPOUNDS with guiding light (see the section
   above). Keep it separate from the DoT duration trap — burn 5 s / bleed 4 s / poison 5 s windows
   are baked at the call sites, and only their LIVE state feeds the count.
6. Pickup copy must say **IN-RUN ONLY** (never a meta unlock), that the magnet WIDENS the collection
   bubble for a window (it is not an XP injection and it never pays XP by itself), and that the rage
   vial is an incoming-damage MARK on the body — max-mark / refresh-never-stack like guiding light, so
   copy must never read "stacks" or "+20% per vial".


7. Motion copy must NEVER say "animated" or "sprite frames" — the art is one pose and every step is
   procedural. There are now TWO names for ONE language (party = table slide, creatures = the same slide
   run FASTER); never describe creatures as heel-stepping, planting, squashing or bobbing — that gait was
   scrapped. Never quote a sway amplitude, cadence or lean angle in copy: they are assistant-picked knobs
   the user retunes by feel.