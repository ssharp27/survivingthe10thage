# Surviving the 10th Age — Design

A Vampire-Survivors-like reframed as **squad D&D**: the player characters from our short campaign
are the weapon system. Recruiting party members *is* unlocking firepower. Stages are the actual
battles/chapters we fought at the table.

## Core loop (squad VS)

- One controlled **Leader** (first-picked PC); party of up to **5** follows in an orbit/formation.
- Every member auto-attacks with its own **signature attack** (class-flavored). You only control movement/positioning.
- **Per-member HP**, summed as the squad's effective pool. Damage goes to orbiting members **before** the Leader — the ring is real geometry. Exceptions exist as class passives (aggro/tank, e.g. Paladin *Protection*).
- **Downed state**: member stops attacking, HP exits the pool, an animated **d20 rolls over the body** (death-save flair). Accumulated failures → **eliminated** (out until the next campfire). Full squad eliminated = TPK = run over.
- **Revive**: stand still on a downed body (~3s channel) → revives at **50% HP**. Standing still in a horde is the risk/reward core.
- **XP is squad-shared**; each level-up pick **targets one named member** ("Wizard's ray +1", "Paladin: Shieldwall evolution").
- Upgrade paths: 3 ranks, then an **evolution = subclass choice** (Fighter → Champion / Echo Knight). Data-driven.
- **Recruitment = narrative join points**: a guarded spot in the stage where "the recruit arrives", tied to the campaign's own story for that battle.

## Stages (replaces the earlier "Ages" concept)

- Meta-map = the **campaign path**; each **stage = one battle/chapter** from the campaign, with its actual set pieces (special enemies, hazards, mini-boss from that encounter).
- **Campfire rests** between stages: short rest = heals + small buff; long rest = full heal, revive eliminated members, next-stage power — at the price of horde scaling.
- Bosses run the **data-driven attack-timeline engine**: phases at 100/66/33% HP, every move is telegraph → attack → recovery, composed from reusable pattern primitives (spokes, rings, sweeps, walls, chases).

## Engine decisions (unchanged from earlier plan)

- Closed arena; pinned hazards run off the projectile pool.
- Pooled projectiles + spatial-hash movement; **no per-enemy physics bodies**.
- Stats = flat dictionary, now keyed **per party member**.
- Horde director paces spawns; VS pacing math for XP curve/kill rates unchanged.

## Milestones

- **M1 — greybox, squad feel from day one**: one stage ("Generic Ruins" placeholder), Leader + **one recruitable ally**, full down/death-save(d20 anim)/revive/TPK loop, squad XP + targeted picks, one signature attack each, one prototype boss on the timeline engine, run summary → death loop.
- **M2 — the campaign roster**: all 5 PCs with classes, signature attacks, subclass evolutions; recruitment wiring; meta-progression (unlock characters, starting party size 1→2→3).
- **M3 — more stages**: each a campaign battle with hazards + unique enemies + set pieces; campfire/rest layer.
- **M4 — finale battle + gauntlet**, run summaries as a **campaign journal**, juice/polish.

## Open decisions (awaiting campaign data)

- Roster: 5 PCs, names/classes (+ race, signature spell, campaign quirks).
- Which battle is stage 1 (M1 can greybox with "Generic Ruins" until then).
- Stage set pieces + recruitment moments: per-battle campaign memories.
- Tank/aggro passives (M2), gauntlet shape (M4).
