# Compendium seed — parked 2026-11-04, work on it LATER

The user asked for a player-facing **compendium** that explains the game's mechanics.
This file is the seed: the locked decisions and the verified number tables below.
Nothing here is final copy — it exists so the later compendium pass never re-derives math.

## Locked decision (user 2026-11-04)

The user **likes compounding**. Keep the multiplicative stacking exactly as coded;
do NOT rework damage/reach/speed tags into additive points for copy's sake.
Card copy must instead be honest that bonuses COMPOUND per rank.

## The stacking rule (source: src/StatTags.cs KIND + src/Game.cs ApplyUpgrade ~line 4672)

- KIND 0 (multiplier): each rank multiplies the live stat. 3 ranks = (per-rank)^3.
  Example reach x1.15: rank 1 +15%, rank 2 +32%, rank 3 +52% (1.15^3 = 1.5209).
  The +52% is NOT 3 x 15% (+45%): the gap is the compounding, and the copy must say so.
- KIND 1 (points): additive on the BASE (crit chance 5% seed, crit damage 1.5 seed, hp on authored maxHp).
- KIND 2 (count): additive strikes/familiars (multishot +1 bolt per rank).

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
