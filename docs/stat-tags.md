# Stat tags — session record 2026-10-24

Source of the rule: the user's stat plan 2026-10-24 ("set tags for all stats in the
game ... base for permanent meta progression") — 15 named stats; the assistant's
16th tag (vulnerability) was accepted to complete the vocabulary.

The tag indices in `src/StatTags.cs` are THE canonical vocabulary. Nothing else
gets to invent another name for armour again.

## Where the code lives

- `src/StatTags.cs` — the registry (data-only: indices, BASE, CATEGORY, KIND, NAMES)
- `src/PartyMember.cs` — `stat = new double[StatTags.N]` (the store) + `SeedStatStore()`
  + the accessors `EffDamage / EffCooldown / EffRange / EffMaxHp / EffMove / Strikes()`
  + `RollDamage(...)` (crit/lifesteal surface)
- `src/Game.cs` — boot dump loop (~line 1213, after `Rng.SetSeed(9001)`), the TakeHit
  defence tags (ARMOUR/EVASION ~line 407), magnet `COLLECTION_RANGE`, gem `XP_PCT`,
  upgrade-card OPT commits writing tag slots
- `src/Combatant.cs` — body-side `vulnMul` pair for the VULNERABILITY tag, plus the
  `slowMul` pair the draging maul spends (see grant chains below)

## The registry (index: tag / category / kind / base)

```
0  attack damage      offense  multiplier  1.0
1  attack speed       offense  multiplier  1.0     (cooldown lives INSIDE: cd / stat)
2  area               offense  multiplier  1.0
3  crit chance        offense  points      0.05    (everyone crits at 5% base)
4  crit damage        offense  multiplier  1.5
5  multistrike        offense  count       1       (WHOLE attack repeats, all kits)
6  lifesteal          offense  points      0
7  health             defense  points      0       (additive on the authored maxHp)
8  armour             defense  points      0       (flat minus, never below 1)
9  evasion            defense  points      0.05    (full dodge roll, before armour)
10 hp regen           defense  points      0       (pts/sec, while standing and joined)
11 xp %               utility  points      0       (gem pickup: xp * (1 + stat))
12 gold %             utility  points      0       (DORMANT: no gold economy yet)
13 collection range   utility  multiplier  1.0     (magnet radius x stat)
14 movement speed     utility  multiplier  1.0     (leader/escort move x stat)
15 vulnerability      modifier points      0       (body-side pair: Combatant.vulnMul)
```

## How grants combine (kind codes)

- kind 0 MULTIPLIER grows on top of the member's authored base (`Main.tscn` [Export]
  values stay the authored base; the store seeds them at 1.0).
- kind 1 POINTS adds raw amount on top of the base (health, armour, crit chance...).
- kind 2 COUNT is whole strikes: `Strikes()` clamps 1..5, same ceiling the shot fan used.

Crit rolls on the RUN RNG stream (`Game.Rng`, now public for `RollDamage`); enemies
never crit — the roll lives on the party side, NOT in `Combatant.TakeDamage`.

## The boot-dump contract

After `Rng.SetSeed(9001)` Game.cs prints one line per tag:

```
[10thAge] stat tag <name> [<category> kind <0|1|2> base <seed>]
```

Sixteen rows, dormant included — a missing row means the registry broke, and the
gate should fail. The loop prints only; it consumes no RNG, so boot trails stay
cross-gate aligned.

## Upgrade-card grant chains (the 2026-10-24 cards)

- Baldrun **draging maul** — deliberately NOT a tag slot. Kit field
  `PartyMember.maulSlow` (+0.25/rank, cap 0.75 falls out of `PICK_RANK_CAP`, no
  clamp) spent by the maul cleave at `nc.Slowed(maulSlow, 2.5)`; the slow is a
  BODY status: `Combatant.slowMul/slowT` + `Slowed()`/`TickSlow()` mirror the
  guiding-light vulnerability pair exactly (stronger mark wins, window refresh
  only). `Enemy.cs` reads the pair at its three movement sites as
  `spd = speed * (1 - slowMul)` while `slowT > 0`; boss immunity is enforced
  at the applier hook (`!(nc is Boss)`), never inside `Slowed()`.
- Mosiah **deadly aim** — pure data: `OPT_CRIT` +0.08/rank straight into tag 3
  (`crit chance`, points kind). `RollDamage` already rolls the slot, so the
  card is 5% → 13/21/29% by rank with zero new code paths.

## Deliberately parked (this pass)

- Meta/unlocks/party size/evolutions — the store exists so those systems have stable
  references, but they stay parked.
- GOLD_PCT: slot reserved, no economy feeds it yet.
- HORDE side (Enemy.cs/Boss.cs) keeps its own numbers: tags are PARTY-SIDE ONLY,
  plus the body-side vulnerability pair.
- Balance numbers here are sanity checks, not tuning.
