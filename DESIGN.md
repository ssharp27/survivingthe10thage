# Surviving the 10th Age — Design

A Vampire-Survivors-like reframed as **squad D&D**: the player characters from our short campaign
are the weapon system. Recruiting party members *is* unlocking firepower. Stages are the actual
battles/chapters we fought at the table.

## Core loop (squad VS)

- One controlled **Leader** (first-picked PC); party of up to **8** follows in an orbit/formation.
- Every member auto-attacks with its own **signature attack** (class-flavored). You only control movement/positioning.
- **Per-member HP**, summed as the squad's effective pool. Damage goes to orbiting members **before** the Leader — the ring is real geometry. Exceptions exist as class passives (aggro/tank, e.g. Paladin *Protection*).
- **Downed state**: member stops attacking, HP exits the pool, an animated **d20 rolls over the body** (death-save flair). Accumulated failures → **eliminated** (out until the next campfire). Full squad eliminated = TPK = run over.
- **Death-save rules (locked)**: downed members roll a **d20 every 4 s**. **Nat 20 = revived at 25% HP**; **11–19 = a success**, and **3 successes = STABLE** — the rolls stop, the body stays down, and enemies/projectiles now **slay it outright** unless a player revives it; **≤ 10 = a failure**, and **3 failures = eliminated** — they disappear and only return at the next campfire (after a mini-boss/boss). Damage on a downed (unstable) body — enemies never *chase* downed members but can run into them, projectiles hit them too — counts as **one extra failure**, buffered **2 s** apart so a swarm can't insta-finish anyone.
- **Leader down (deferred)**: originally intended as instant TPK; shelved — for now the squad just keeps fighting while the Leader is downed. Future mechanic: a party member can revive the Leader.
- **Revive**: **nat 20 self-roll (25% HP)**, **player channel** — walk the Leader onto a body and **stand still ~3 s** → revived at **50% HP** (the only rescue for a STABLE body), and **campfires** heal everyone **+25% max HP** while **eliminated members return at 25% HP**. Mosiah self-reviving off the dice during play is the feel we want. Mosiah self-reviving off the dice during play is the feel we want. **Anti-frustration (locked)**: a downed body is **immune to damage while the channel is progressing** — a swarm can't screw the rescue out (and blocked reaper touches don't despawn it); every revive grants **2 s of post-revive invulnerability**.
- **XP is squad-shared**; each level-up pick **targets one named member** ("Wizard's ray +1", "Paladin: Shieldwall evolution"). **User-approved growth**: every level-up also auto-grants each member +0.5 flat weapon damage — before the card's pick.
- Upgrade paths: 3 ranks, then an **evolution = subclass choice** (Fighter → Champion / Echo Knight). Data-driven.
- **Recruitment = narrative join points**: a guarded spot in the stage where "the recruit arrives", tied to the campaign's own story for that battle.
- **DoT crossfire (locked, 2026-11-07 QoL)**: every **UNIQUE** damage-over-time effect running on an enemy marks it **+5% vulnerable** to ALL incoming damage — burn, bleed and poison today (all three = **+15%**), and **half that on bosses** (mini/mid/final, so they never get too easy). Counted **at the hit** from the live DoT windows, so re-striking the same venom never double-dips and an expired DoT stops paying; every future DoT pair slots into the same rail and pays for free.

## Stages (replaces the earlier "Ages" concept)

- Meta-map = the **campaign path**; each **stage = one battle/chapter** from the campaign, with its actual set pieces (special enemies, hazards, mini-boss from that encounter).
- **Campfire rests** between stages: the camp beat itself only lifts the fallen (back at 25%) and stands bodies up. **User-approved risk-vs-reward rebalance (2026-10-19): SHORT rest = +25 % max-HP heal for everyone + one free upgrade rank; LONG rest = full revive + full heal for the WHOLE squad (fallen go to full, not 25 %) but NO free rank** — and **both** rests carry the horde-scaling price (**×1.20 multiplicative per rest — HP, spawn cadence, AND the Director's live cap 40 × scale, so the next horde runs bigger and denser**). Long = safety, short = power. The camp itself is **fully player-driven** (user-locked 2026-10-19, re-locked 2026-10-31): **no countdown, no AFK default** — a human camp waits for a real rest choice forever, and choosing to stand idle is a valid choice.
- **Campfires also sit after mini-bosses**, mid-run: an un-revived downed member becomes eliminated and **revives at the next campfire**, so 2–3 campfires per run are the party-recovery beats. The camp card lists the roster in join order but paints **only recruited members** — never-recruited PCs stay hidden (user rule 2026-11-02: showing dim "not recruited yet" strips spoiled which characters existed to find, so the camp only reveals bodies you have actually recruited), and a member who joins at a camp shows up on the next refresh.
- Bosses run the **data-driven attack-timeline engine**: phases at 100/66/33% HP, every move is telegraph → attack → recovery, composed from reusable pattern primitives (spokes, rings, sweeps, walls, chases).

## Engine decisions (unchanged from earlier plan)

- Closed arena; pinned hazards run off the projectile pool.
- Pooled projectiles + spatial-hash movement; **no per-enemy physics bodies**.
- Stats = flat dictionary, now keyed **per party member**.
- Horde director paces spawns; VS pacing math for XP curve/kill rates unchanged.

## Campaign data

### Party roster (all **9** landed — roster complete 2026-10-31: Baldrun, Mosiah, Asper, Alvora, Yu, Stack, Wash, Bruno, Brook)

- **Baldrun** — Ursine (white humanoid polar bear, Volibear silhouette), Rune Knight Fighter. Massive two-handed maul.
  - Base weapon: generic maul — heavy, slow, **area damage** (full damage to every body in the fan).
  - Upgrades: damage / attack speed / area / **draging maul** (hits slow bodies) / **fire rune** (maul sets caught bodies burning, 1/2/3 dps × 5 s).
  - Evolution: **Stormcaller** — maul that summons lightning.
- **Mosiah** — Human Bard, stylish combat with a rapier.
  - Base weapon: generic rapier — fast, precise.
  - Upgrades: damage / attack speed / range / **deadly aim** (crit chance +8%/rank) / **deadlier aim** (crit damage +10/20/30%).
  - Leader seat: all allies gain **+5% evasion** while Mosiah leads.
  - Evolution: **Viperkiss** — **critical + poison damage** focus.
- **Asper** — Half-elf Arcane Archer, insect-themed.
  - Base weapon: **longbow** — bug-shot arrows at the nearest body; fast, low damage.
  - Upgrades: damage / attack speed / **piercing shot** / **multishot** (fan of shots) / **keener aim** (crit chance +8/16/24%).
  - Leader seat: friendly insects kindle in the clearing; touching one joins the squad for 10 s.
  - Ultimate: **Swarm of Bees** — 6 hunters in a circle, one body latched each at 10 dmg/s for 10 s.
  - Evolution: **TBD** (rank-4 subclass parked).
- **Alvora** — Owlin Twilight Cleric.
  - Base weapon: **Spirit Guardians** — a persistent ring, full damage to every body inside (anti-swarm).
  - Signature: **guiding light** — ring pulses mark caught bodies (+10%/rank damage taken, 4 s).
  - Upgrades: guardian damage / ring reach / pulse rate / guiding light / **weighty ring** (bodies caught in the ring are slowed 10/20/30%).
  - Leader seat: every standing ally regenerates 1 HP every 10 s.
  - Ultimate: **Guardian Awakening** — ring ×2 reach, guardians +50% damage, +1 hp healed per horde kill.
  - Evolution: **TBD** (parked).
- **Yu** — Tortle Occultist.
  - Base weapon: **familiar summons** — small creatures spawn near him, run to prey, **explode for AoE** (full damage, no split).
  - Upgrades: damage / blast AoE / summon rate / multiattack (one more familiar per ritual) / **lucky explosions** (blast may fire twice, 5/10/15%).
  - Leader seat: kindness spirits kindle every 30 s and **ride the gem magnet lane** to the nearest standing ally; a DELIVERED vigil heals **ALL wounded allies +10** each — a healthy squad never drinks, and a downed Yu stops kindling.
  - Ultimate: **The Kaka** — 6 empowered familiars, damage and blast ×2 after upgrades.
  - Evolution: **TBD** (parked).
- **Stack** — Artificer.
  - Base weapon: **firebolt** — hits set **burn** (1 dmg/s; damage cards scale the DoT too).
  - Upgrades: damage / attack speed / pierce / **cinder bloom** (burning bodies burst on death, 25/50/75% of the bolt's live roll) / **linger flame** (burn lasts longer).
  - Leader seat: **Have a Drink!** — burning bodies take −10% damage and move 10% slower while Stack leads.
  - Ultimate: **Experimental Elixir** — two of six boons on every ally for 10 s (10% all six).
  - Evolution: **TBD** (parked).
- **Wash** — Paladin.
  - Base weapon: **shield throw** — hits one body, then **bounces to one more** at short range (the disc visibly hurls from Wash and hops body to body).
  - Upgrades: damage / attack speed / range / +1 bounce per rank / **crushing blow** (crit damage).
  - Passive: **Smite** — allies standing get +5% crit chance / +25% crit damage while Wash stands.
  - Leader seat: **Inspiring Leader** — all allies gain **+15 max HP** and **+1 armour** while Wash leads (reader-side; the stat-tag audit stays a permanent-grant ledger).
  - Ultimate: **Oakhollow Assemble!** — all allies +25% move, +5 armour, regen 1%/s for 10 s.
  - Evolution: **TBD** (parked).
- **Bruno** — Axethrower.
  - Base weapon: **crescent boomerang axes** — one axe thrown in a returning half-moon arc; it hits **every body it passes through** (no target cap). The crescents are TRAVELLING hitboxes (what is on screen is what hurts), launch-staggered ~0.1 s per axe so extra axes are all visible, and every throw keys off ONE fixed arc radius = the member's reach so art, hitbox and telegraph agree.
  - Upgrades: damage / attack speed / number of axes (arcs alternate top/bottom) / axe size (area — blade art and hit discs ride the SAME size factor) / axes apply **bleed** (1 dmg/s × 4 s).
  - Ultimate: **Axes Away!** — two massive axes PLUS one per extra-axe rank (carrying ALL upgrade effects) spin around him on a 70 px ring for 10 s; each blade bites along its own spin path, one honest bite per body per turn, and his normal throws stand down for the window.
  - Leader seat: all allies gain **+15% attack size (area)** and **+10% movement speed** while he leads (reader multipliers — reach, discs, rings and blasts all widen).
  - Evolution: **TBD** (parked).
- **Brook** — Rogue, twin daggers.
  - Base weapon: **twin dagger stabs** — one beat = TWO same-line stabs at short reach (very small delay between them); the corridor band hits every body on the throw line and applies **poison** (1 dmg/s × 5 s, scales with the damage card like every weapon). Both daggers share ONE crit/multistrike roll per beat (the twin strike is one attack event).
  - Upgrades: damage / attack speed / **venom** (poison lasts longer) / **crit chance** / **multistrike** (5%/rank deal damage twice).
  - Leader seat: **Inquisitive Mind** — allies gain +5% evasion, +15% collection range and +15% XP while Brook leads.
  - Ultimate: **Assassination** — 10 s: ALL allies +25% multistrike, +15% crit chance, +25% crit damage.
  - Evolution: **TBD** (parked).

### Stage 1 — Tanurial Forest

Standard fantasy forest outside a major city, currently under attack. Baseline enemies: goblins, wolves. Mini-bosses and boss for this stage: **TBD** (greybox uses placeholders).

## Milestones

- **M1 — greybox, squad feel from day one**: one stage (**Tanurial Forest**, placeholder art), Baldrun + Mosiah (one Leader, the other at a narrative join point), goblins/wolves hordes, **mini-boss → campfire party-recovery beat**, full down/death-save(d20 anim)/revive/eliminated-at-campfire/TPK loop, squad XP + targeted picks, maul + rapier signature attacks, one **placeholder** boss on the timeline engine, run summary → death loop.
- **M2 — the campaign roster**: all 9 PCs with classes, signature attacks, leader seats and ultimates (subclass evolutions PARKED); recruitment wiring; meta-progression (unlock characters, starting party size 1→2→3) — still deferred.
- **M3 — more stages**: each a campaign battle with hazards + unique enemies + set pieces; campfire/rest layer.
- **M4 — finale battle + gauntlet**, run summaries as a **campaign journal**, juice/polish.

## Open decisions

- Who leads in M1: Baldrun or Mosiah starts the run; the other is the narrative-recruit moment.
- Tanurial Forest mini-bosses + boss: user is designing; greybox uses placeholder timeline boss.
- ~~Remaining 3 PCs (M2)~~ — **CLOSED 2026-10-31**: the roster grew to 9 (Asper/Alvora/Yu in the M2 pass; Stack, Wash and Bruno from later user dictations; Brook added as the 9th the same day). Rank-4 evolutions stay PARKED.
- Tank/aggro passives (M2), rest/gauntlet shape (M4).
