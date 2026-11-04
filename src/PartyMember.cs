using Godot;



/// <summary>

/// One party member: HP, auto-attack driven by signature-weapon parameters, and the

/// down/death-save/revive state fields (the state machine itself lives in Game).

/// Baldrun = wide ~150 deg arc cleave (FULL damage to every body in the fan - the

/// split was dropped user 2026-10-23, the maul was weaker than every other verb once

/// a few bodies crowded the group);

/// Mosiah = narrow fast rapier (single target + pierce, hits one body at a time);

/// Asper = longbow (party-side shot pool, multishot fan); Alvora = Spirit Guardians

/// (a ring that pulses around the caster: FULL damage to every body inside, not split).

/// </summary>

public partial class PartyMember : Godot.Node2D

{

	// [Export] IS REQUIRED, not cosmetic: the export packer only serializes [Export]-annotated

	// C# properties into the PCK, so a plain-public scene override (Main.tscn's Baldrun/Mosiah

	// blocks) silently vanishes in a standalone build (2026-10-21 demo bug: exported game ran

	// with displayName="Member", isLeader=false, art lookup miss, boss HP defaults).

	[Export] public string displayName = "Member";

	[Export] public string weaponName = "Maul";

	[Export] public int memberIndex = 0;

	[Export] public bool isLeader = false;



	[Export] public float maxHp = 60.0f;

	[Export] public float baseSpeed = 185.0f;



	[Export] public float weaponDamage = 10.0f;

	[Export] public float cooldown = 1.15f;

	[Export] public float range = 95.0f;

	[Export] public float arcHalf = 1.05f; // half-arc radians: wide = maul, narrow = rapier

	// ---------- kit verbs ----------

	// The kit is DATA on the member, not a new system: M1 shipped two melee verbs keyed on

	// the fan width (arcHalf > 0.5 rad = cleaver fan, and since the user 2026-10-23

	// rebalance its fan takes FULL damage per body, no split; narrower = rapier

	// lane with pierce). M2 adds a projectile verb (Asper's longbow): it fires the Game's

	// PARTY-SIDE shot pool (SpawnShot), aims at the CLOSEST enemy in sight, and multishot

	// fans extra bolts out of the same commit. The arc test alone cannot carry a third verb,

	// so the kit name is the switch and arcHalf stays purely a melee property. M2 adds a THIRD

	// verb for the cleric (Alvora's Spirit Guardians): a persistent ring of spirits around the

	// caster that pulses on the weapon cooldown. Radius rides the reach stat and the pulse rides

	// the cooldown, so both of its ranks reuse the AREA and ATTACK_SPEED tags like every other

	// reach and speed track; the damage is NOT split by count (user rule 2026-10-22) - every body

	// inside takes the full hit. The maul's fan dropped its split too (user 2026-10-23), so both

	// heavy AoE verbs now bite crowds at full strength. Warding light is GONE (user 2026-10-23

	// swapped it for offense): the cleric's fourth track is guiding light, a VULNERABILITY

	// mark - ring-caught bodies take +10%/rank damage from every source for a window.

	[Export] public string weaponKit = "melee"; // "melee" | "shot" | "aura" | "summon" | "shield" | "axe" | "dagger"

	[Export] public float projSpeed = 340.0f;  // px/s a bug projectile travels

	[Export] public float projLife = 2.2f;     // seconds a bolt may fly before it expires

	// firebolt kit knobs (Stack, user 2026-10-30): a hit sets a burn window on the body (dps x

	// seconds, Combatant.Burn); cinderRank = cinder bloom rank: bodies set on fire burst on death. The

	// DoT dps scales with the damage card at the call site (stat[ATTACK_DAMAGE] is the card

	// product; crits and ults stay per-swing like every other weapon)

	[Export] public double burnDps = 0.0;   // damage/sec stamped on bodies this member hits (0 = no burn)

	[Export] public double burnDur = 0.0;   // seconds that burn runs; linger flame cards raise it

	[Export] public int cinderRank = 0;      // cinder bloom rank: burning bodies burst on death

	// Wash's shield throw (user 2026-10-31): hits nearest prey in its SHORT reach, then bounces body

	// to body: base ONE bounce (hits 1, bounces to 1 more - user), extra rebounds card adds +1/rank.

	// Hop range GROWS with the rebound rank now (160 base + 96 px per extra rank, flight lane below):

	public int shieldBounce = 1;      // bounces after the first body (base + extra rebounds ranks)
	// user 2026-11-02 (Wash leader seat "Inspiring Leader"): ANY max-HP raise must also raise CURRENT
	// hp to match. The tracked ceiling below rides EffMaxHp() live, so the lane covers the seat
	// bonus (+15 for ALL allies while Wash leads) AND every grant this class can ever see.
	float _hpMaxSeen = -1.0f; // highest EffMaxHp seen since the last withdrawal; -1 = not yet seen

	static readonly double SHIELD_HOP = 160.0; // BASE px a bounce may hop. fx-feel9 (user: "it should just not even try to bounce if they are too far away"): the flight lane now NEVER widens this with rank - the rebound upgrade buys bounce COUNT only
	// Wash hurl speed (user 2026-11-02: "far too quickly so it looks terrible... smooth bounce between enemies"): px/s of VISIBLE travel; PURE MOTION, damage model untouched
	static readonly double SHIELD_HURL_SPEED = 340.0; // assistant pick: a 160 px hop ~0.47 s, a 60 px hop ~0.18 s - readable, never the old 0.09 s blur

	// authored armour base (Wash 1 armour, user 2026-10-31): the ARMOUR tag seeds at BASE then adds

	// this per-PC number; the flat-subtract lane with never-below-1 floor lives in Game.TakeHit.

	[Export] public double baseArmour = 0.0;    // per-PC armour seeded into the ARMOUR tag

	// Bruno's throwing axes (user 2026-10-31): base ONE crescent per beat (a half-arc the axe

	// traces OUT and boomerangs back over, raking everything within the blade band once per

	// crescent: out leg + back leg share the path, so one pass of one blade); the extra-axes

	// card adds one more crescent per rank, arcs ALTERNATING top/bottom of the throw line.

	// Kit fields like pierce/guidingLight, NOT tag slots: crescents/area/bleed are BODY

	// lanes; bleed is its OWN status pair mirroring burn (the fire DoT lanes stay Stack-only).

	[Export] public int axeN = 0;        // extra crescents per volley (0 = base one axe)

	[Export] public int axeSize = 0;    // size ranks: blade disc + band read 1.0 + 0.25 * rank

	[Export] public int bleedR = 0;     // bleed rank: hit bodies bleed like the fire DoT

	public double orbitT = 0.0;         // "Axes Away!" spin clock: seconds of orbit left (Game sets)

	double orbitAng = 0.0;              // spin angle: blades ride orbitAng + i * 2pi/blades

	// fx art (2026-11-01 art pass): per-PC effect sprites on the Main.tscn exports; "" = greybox

	// exactly as shipped. Data-driven like weaponKit: only the listed PCs build the armature.

	[Export] public string meleeFxArt = ""; // shared melee commit flash (Baldrun sweep arc / Mosiah rapier sliver)

	[Export] public string axeFxArt = "";   // Bruno: crescent volley flash + "Axes Away!" spin blades

	[Export] public string shieldFxArt = ""; // Wash: hurl disc caught mid-hurl on the commit

	// Alvora Spirit Guardians art (user 2026-11-02: "alvora_spirit - literally a sprite of Alvora...
	// tint it a spectral blue and shrink it down to about 1/3 of the size"): set on the ALVORA node in
	// Main.tscn; the six ring motes then ride that art instead of the twilight-violet ColorRect greybox
	// (tint + 1/3 scale live at the build below; damage, pulse and ring ranks untouched).
	[Export] public string spiritFxArt = ""; // Alvora: Spirit Guardians mote art (spectral-blue tinted)

	Godot.Sprite2D _meleeFx = null;

	// fx-feel10: the single disc sprite is gone - art lives in _shldArt, one per slot.

	Godot.Sprite2D[] _axeFx = null; // 5 SPIN blade slots (fx-feel8: the crescents got their OWN set so the default attack can run WHILE the ult spins - user: "allow him to continue his default attack while his ultimate is up")

	Godot.Sprite2D[] _cresFx = null; // 5 crescent blade slots: their own art set, so the spin and a volley never fight over one frame's placement

	float _fxT = 0.0f; // commit-fx visibility timer: decays with the world clock, hides sprites off-beat

	// user 2026-11-01 art/feel pass (user's bug batch): crescent blades are TRAVELLING hitboxes

	// - the flight state rides the member, the _Process block below advances it every frame, and

	// damage lands at the blade's own position: what is ON SCREEN is what damages.

	// (assistant pick: travel adds NO Rng draws; the volley still rolls once at commit, so the

	// Rng stream is untouched and the smoke stays comparable to the pre-fix runs)

	// (assistant pick: 0.85 s per volley since the loop path is twice the old half-arc - the

	// user asked for the WHOLE boomerang out and back on screen, not a longer beat; still pure motion, NO extra Rng draws)

	// NOTE: the tick section has no commit locals, so the flight re-reads GetEnemies() and its

	// own position itself; the spin clock owns the same _axeFx slots, so the flight freezes

	// while orbitT runs and resumes when that clock expires - neither lane steals frames

	// NOTE: one bite per body per crescent (memo bit 1<<c); bit 256 tallies uniques for the

	// print. crescents max at 5 slots so the bits never collide, and the memo is indexed by the

	// stable enemy-list index (dead bodies keep their slot and skip on the kind check)

	// NOTE: same as the rake lane, an empty clearing never stalls the beat: the blades still fly

	// (assistant pick: the catch bite at flight end re-samples GetEnemies() at the member's own

	// position - the old release sample, once per blade, so a body that only straddled the

	// release ring still gets exactly one honest bite)

	// Wash's shield HURLS visibly from himself, LIVE (user 2026-11-02 fx-feel6: the throw "goes
	// through enemies and seems to bounce at their set distance instead of hitting the enemy and
	// bouncing. It also applies the damage immediately instead of on hit (like it should do similar
	// to Bruno's)"): NO baked polyline, NO instant-at-commit damage. The commit only speaks for the
	// prey and looses the disc; the _Process chase homes on LIVE positions, bites ON ARRIVAL (Bruno-
	// style: the damage lands where the disc actually is) and picks the next hop at the hit itself.
	// 2026-11-02: hurl legs run at CONSTANT SHIELD_HURL_SPEED px/s (user smooth-bounce fix)

	// NOTE: the bounce ramp is FLAT 75% per hop (user: first body full, second and every body

	// after it 75%) - it does not compound to 0.75^n

	// Brook's dagger art spans the jab corridor on BOTH jabs; the second repeats the first line

	float _cresDur = 0.0f; // active-volley flag: total flight seconds (0 = no volley flying)

	float _cresT = 0.0f; // flight seconds left on the current volley

	double _cresA0 = 0.0; // prey-line angle: the loop's WIDEST point rides this at mid-flight

	float _cresR = 0.0f; // loop's max radius (mid-flight bulge): rides the prey line, clamped like the rake

	float _cresBand = 0.0f; // blade disc + band depth: grown by the size card

	double _cresDmg = 0.0; // the volley-wide roll the flying blades bite with

	int _cresNC = 0; // crescents flying this volley (base one + extra-axes ranks)

	bool _cresBled = false; // bleed stamp carries onto the flight bites

	int _cresBit = 0; // unique-body tally: printed the moment the flight lands

	int[] _cresMemo = null; // per-body crescent-bite bits (256 = tallied)

	// ult fix (user 2026-11-01 follow-up): the spin blades were PERMANENTLY visible

	// (the art block in _Process was MISSING BRACES - the clock check guarded only the

	// angle line, so the blades sat on the 70 px ring EVERY frame of the run, which is

	// what the user saw as axes parked on either side of Bruno) and raked NOTHING (the

	// beat commit sampled bodies at the commit instant only, inside a ring the hugging

	// horde stands INSIDE of: the gate smokes printed zero (spin) rakes across two casts).

	// The fix arms a TRAVELLING hitbox sweep in the tick below: the blades bite at their

	// OWN positions every frame while the clock runs, ONE shared roll per cast (the spin

	// is one action like every single-commit weapon), ONE honest bite per body per blade

	// per CIRCLE (the bite bits clear at every full turn), and the unique-body tally

	// prints the moment the clock expires. The DEFAULT ATTACK is OFF while the ult runs

	// (user): the cast voids an in-flight volley and the spin beat owns the commit, so

	// the crescents never fight the spin slots or the five blade sprites.

	bool _spinAct = false;      // sweep armed flag: cast arms the roll/memos, expiry prints + hides

	double _spinDmg = 0.0;      // the cast-wide roll the flying blades bite with

	bool _spinBled = false;     // bleed stamp carries onto the sweep bites

	double _spinA0 = 0.0;       // cast start angle: revolutions measured off this

	int _spinRev = 0;           // full circles the clock has already burned (bite bits clear each)

	int _spinBit = 0;           // unique-body tally: printed the moment the spin expires

	int[] _spinMemo = null;     // per-body bite bits for the CURRENT circle (1 << blade)

	int[] _spinCounted = null;  // per-body print bits across the whole cast (256 = tallied)

	static readonly double CRESCENT_FLIGHT = 0.85; // seconds of blade travel per volley: OUT to reach and BOOMERANG back (assistant pick: the loop path is twice the old half-arc, so travel stays pure motion)

	// 2026-11-02 fx-feel6 (user: the shields "go through enemies and seem to bounce at their set
	// distance instead of hitting the enemy and bouncing. It also applies the damage immediately
	// instead of on hit (like it should do similar to Bruno's)"): the BAKED-chain + instant-commit
	// hurl is GONE - the throw is now a LIVE homing state machine (see _Process): the disc chases
	// the current victim's LIVE position at SHIELD_HURL_SPEED px/s and bites ON ARRIVAL (Bruno-style
	// on-hit damage), then hops to the next fresh live body within SHIELD_HOP. Damage only ever lands
	// on BITTEN bodies - a disc flying past anything still bites nothing.
	// fx-feel10: ALL throw state is parallel per-slot arrays so one hurl can never overwrite
	// another - several discs may fly at once (user: the shield should be its own instance,
	// multiple ones bouncing around is fine). Growth is guarded +4 at a write site, like every
	// other memo in this file: no index is ever touched past the current length.
	Godot.Node2D[] _shldT = new Godot.Node2D[4]; // body each slot homes on (null = idle slot)
	Godot.Vector2[] _shldP = new Godot.Vector2[4]; // disc world position per slot: drives art AND chase
	Godot.Node2D[][] _shldHitM = new Godot.Node2D[4][]; // per-slot chain-lightning memo (per-throw sized)
	int[] _shldHitNA = new int[4]; // per-slot memo length
	int[] _shldLeftA = new int[4]; // per-slot bounce budget left after the current victim
	int[] _shldBitesA = new int[4]; // per-slot bodies actually bitten (audit print)
	double[] _shldRollA = new double[4]; // per-slot ONE crit/lifesteal roll shared by that whole throw
	float[] _shldFlownA = new float[4]; // per-slot px flown: tumbles that disc art by distance
	Godot.Texture2D _shldTex = null; // the kit disc texture; art is pooled per slot on first use
	Godot.Sprite2D[] _shldArt = new Godot.Sprite2D[4]; // pooled disc sprite per slot (ShieldArt)
	int _shldStackPrinted = 0; // fx-feel10 audit ONLY: highest disc stack already printed
	int _shldThrows = 0; // fx-feel10 audit ONLY: committed throws this run

	float _twinLen = 0.0f; // first jab's stab length: the second dagger repeats it

	// Brook's twin daggers (user 2026-10-31; user 2026-11-02 DAMAGE BUMP 7 -> 12 EACH to compensate for no

	// piercing): TWO daggers, thrust at the nearest

	// prey ALONG ONE LINE one right after the other (user: "attacks twice in the same direction,

	// one after another with a very small delay"): the beat lands the FIRST jab and parks cdTimer

	// at TWIN_STAGGER, so the second lands ~0.15 s later down the stored aim - the stagger is the

	// assistant pick (user named only "a very small delay"). Each jab stabs every body inside a

	// narrow corridor to the prey point; hits stamp POISON, its OWN status pair mirroring burn/

	// bleed (fire DoT lanes stay Stack-only). Kit fields like pierce/axeN, NOT tag slots.

	[Export] public double poisonDps = 0.0;   // damage/sec stamped on stabbed bodies (0 = no venom)

	[Export] public double poisonDur = 0.0;   // seconds the poison runs; lingering venom raises it

	[Export] public double baseEvasion = 0.0; // per-PC authored evasion seeded into the EVASION tag (Brook 10%)

	public double multiChance = 0.0;  // lucky strikes card: +0.05/rank chance a hit deals damage TWICE

	public bool twinArm = false;      // first dagger landed: the second repeats the SAME line

	public double twinDmg = 0.0;      // beat-wide crit/steal/multistrike roll the two daggers share

	public Vector2 twinAim = new Vector2(1.0f, 0.0f); // second dagger repeats the first's direction

	static readonly double TWIN_STAGGER = 0.4; // USER number (2026-11-02: "modify the delay to 0.4"; was 0.1): the counted gap between Brook's twin jabs

	// Yu's familiars (M2, user dictation 2026-10-22): the fuse is the summon's LIFETIME, and

	// the familiar detonates on contact with prey OR when that fuse burns out - the fuse is

	// what keeps a player from parking a pile of them under a boss and saving them for later.

	// There is no summon cap (user): they never live long enough to stack up.

	[Export] public float minionSpeed = 84.0f;  // px/s a familiar runs (a hair under a goblin's 90)

	[Export] public float minionRadius = 56.0f; // px blast radius when it detonates (starts small)

	[Export] public float minionFuse = 6.0f;    // seconds of fuse before it detonates by itself

	// multistrike now lives in the stat store (StatTags.MULTISTRIKE); Strikes() reads it



	// Temporary HP (user 2026-11-06, Field Medic over-heal): a buffer ABOVE the ceiling that
	// damage spends FIRST. Deliberately NOT in EffMaxHp - a raised ceiling is Wash's Inspiring
	// Leader seat, this one only ever holds what the medic pushed past the cap (Game.TEMP_HP_CAP),
	// and no other heal lane tops it (every other heal still clamps at EffMaxHp).
	public float tempHp = 0.0f;

	public float hp = 0.0f;

	public bool downed = false;

	public bool eliminated = false;

	float cdTimer = 0.0f;

	// ultimate (R, user 2026-10-25): ultT = seconds of Giant's Might left; ultCdT

	// = seconds until the ult is ready again (gate = ULT_CD_BASE / StatTags.ULT so

	// meta progression can shorten it). _grown guards the root scale restore so

	// expiry shrinks exactly once. The 10 s duration is the assistant pick (user

	// named no window) - documented on the trail.

	public double ultT = 0.0;

	public double ultCdT = 0.0;

	bool _grown = false;

	readonly double ULT_CD_BASE = 120.0; // user: 120 s gate, tag-driven

	readonly double ULT_LASTS = 10.0; // pick, documented on the trail

	readonly double ULT_DMG = 1.5; // +50% damage while grown (RollDamage choke)

	readonly double ULT_SIZE = 2.0; // root scale x2: art and tele/aura pivots inherit

	readonly double ULT_RANGE = 2.0; // reach / hit radius x2 (EffRange)

	readonly double ULT_SPD = 1.5; // user 2026-11-06: Giant's Might ALSO grants +50% attack speed (EffCooldown divisor, all kits)

	// Mosiah "Bardic Inspiration" (user 2026-10-26): insT = bardic seconds left ON THIS

	// body - the ult is PARTY-WIDE so every ally carries its own clock. INS_SPD divides

	// EffCooldown, which IS the cadence for all four kits (the aura pulse rides the

	// weapon cooldown too), and INS_DMG rides the RollDamage choke - no new RNG rolls.

	public double insT = 0.0;

	readonly double INS_LASTS = 10.0; // assistant pick (user named no window), like ULT_LASTS

	readonly double INS_DMG = 1.25; // +25% damage while inspired (RollDamage choke)

	readonly double INS_SPD = 1.25; // +25% attack speed: EffCooldown divisor, all kits

	// Alvora "Guardian Awakening" (user 2026-10-26): ring reach x2 MULTIPLIED after the

	// AREA tag (ranks keep widening the awakened ring - user asked for exactly that),

	// guardian damage +25% at the RollDamage choke, and Game.OnEnemyKilled tallies awaKills:

	// every horde kill while awake heals the standing squad +1 hp each. _awaUp guards the

	// one-shot expiry print, same discipline as _grown.

	public double awaT = 0.0;

	public int awaKills = 0;

	bool _awaUp = false;

	readonly double AWA_LASTS = 10.0; // assistant pick (user named no window)

	readonly double AWA_RANGE = 2.0; // ring reach x2, after the AREA tag (EffRange)

	readonly double AWA_DMG = 1.25; // user 2026-11-06: +25% guardian damage while awake (was +50%) (RollDamage choke)

	// Experimental Elixir (user 2026-10-31, Stack's ult): SIX one-shot party buffs, each riding

	// its OWN 10 s clock on every member (bench too - harmless, same discipline as insT). The

	// draw lives on Game.ElixirParty (two DISTINCT of six, 10% ALL six). Readers:

	// elixHealT=regen block below, elixSwiftT=EffMove, elixHardT=Game.TakeHit,

	// elixBoldT=RollDamage, elixPrecT=crit roll, elixMagT=Game.UpdateGems bubble.

	public double elixHealT = 0.0;  // Healing: regen 1% max hp per second

	public double elixSwiftT = 0.0; // Swiftness: move 10% faster

	public double elixHardT = 0.0;  // Resilience: take 10% less damage

	public double elixBoldT = 0.0;  // Boldness: deal 10% more damage

	public double elixPrecT = 0.0;  // Precision: +10% critical chance

	public double elixMagT = 0.0;   // Magnetism: collection bubble +10%

	// Oakhollow Assemble! (user 2026-10-31, Wash's ult): ONE clock on EVERY member (bench too -

	// harmless, same discipline as insT/elixir clocks) carries +25% move (EffMove), +5 armour (TakeHit)

	// and regen 1% max hp/s (REGEN block). Set by Game.OakHollowParty; duration lives on the Game side.

	public double oakT = 0.0;   // seconds of Oakhollow Assemble! left on this body

	// Assassination window seconds left on this body (user 2026-10-31 Brook ult): while up,

	// RollDamage gives +15% crit CHANCE points, crit damage x1.25 and 25% multistrike CHANCE.

	// NO stat-store writes: the window is temporary, the 17-row audit stays a permanent-grant ledger.

	public double assT = 0.0;

	Godot.Node2D _telePivot; // telegraph armature (pure nodes: C# _draw is dead in 4.7)

	Godot.ColorRect[] _teleParts;

	float _teleHold = 0.0f; // telegraph visibility timer, set on each swing

	// Spirit Guardians armature: motes orbiting under ONE pivot, so a single Scale change carries

	// the "wider ring" rank (pure nodes again: the C# _draw hook is dead in 4.7)

	Godot.Node2D _auraPivot = null;

	Godot.ColorRect[] _auraParts;

	// Art motes when spiritFxArt is set (user 2026-11-02 alvora_spirit pass): same pivot, same ring
	// spots, so orbit + the "wider ring" rank ride the pivot as ever; faces stay upright via the
	// counter-rotation in the orbit block
	Godot.Sprite2D[] _auraSprites = null;

	float _auraPulse = 0.0f; // seconds of ring flare left from the last pulse
	Godot.Polygon2D _auraDisc = null; // area disc handle: counter-rotates every frame so the fill stays put (user: the spin made it dizzy)

	public float facing = 0.0f;

	// canonical stat store: one slot per StatTags tag, seeded from StatTags.BASE in _EnterTree

	/**

	 * Per-tag stat values (index = StatTags.* constants; see StatTags.cs). Upgrades write

	 * these slots; everything combat-side reads them via the Eff* helpers, Strikes() and

	 * RollDamage below. This is the single canonical home for every permanent stat.

	 */

	public double[] stat = new double[StatTags.N];



	// "wider arc" rank track REMOVED per human test: Baldrun's upgrade pool went to

	// reach / damage / cooldown instead, so the maul fan stays a fixed ~150 deg arc.

	public int pierce = 0; // "deeper thrust": the rapier lane can skewer extra bodies

	// guiding light ranks: VULNERABILITY per rank (+0.10/rank). Stored on the member, spent

	// by the aura commit via Combatant.Vulnerable - the ring is the only applier, every

	// damage source reads the mark. (The old flat-armour property is gone per user swap.)

	public double guidingLight = 0.0;

	// draging maul ranks (Baldrun 4th card, user 2026-10-24): +0.25/rank drag strength, cap

	// 0.75 by PICK_RANK_CAP (no clamp needed). The maul cleave spends it via Combatant.Slowed

	// - only applier, bosses immune at the hook. Kit field like pierce/guidingLight, not a

	// tag slot: the slow is a BODY status (slowMul/slowT on the body), not a party stat.

	// deadly aim needs no field: its card writes the CRIT_CHANCE slot of the stat store.

	public double maulSlow = 0.0; // drag strength per rank: rank 3 = a quarter speed, no clamp

	public double fireDps = 0.0;  // fire rune (Baldrun row 40): the maul sets caught bodies burning, rank = Burn dps

	public double ringSlow = 0.0; // weighty ring (Alvora row 43): the pulse drags what it caught, rank = slow strength

	public double luckR = 0.0;    // lucky explosions (Yu row 44): familiars may detonate with a second blast pass

	[Export] public string class_ = ""; // display class for the ui/ cards (empty = weapon fallback)

	public string pickHistory = ""; // tracks taken so far, rendered on the run-summary card



	// down/death-save state (driven by the Game root)

	public float dsTimer = 0.0f;

	public int dsFails = 0;

	public int dsSucceeds = 0; // rolls of 11-19; 3 of them = stable

	public bool stable = false; // 3 successes: rolls stop, hits now slay

	public float reviveProgress = 0.0f; // leader stand-still channel

	public bool reviveShield = false; // body is mid-rescue: damage is ignored

	public float invuln = 0.0f; // post-revive immunity timer (2 s)

	public float dsDamageBuffer = 0.0f; // 2 s gap between damage-caused death-save fails

	public float dsHold = 0.0f; // countdown for SetDsTemp texts; 0 = persistent

	public bool campReturned = false; // lifted by the camp beat (pass 5: the LONG rest now tops these to full)

	// field-recruit rule (user 2026-10-20): the Leader starts in the squad; every other

	// roster PC waits for its join beat. Unjoined = no follow, no attacks, un-hittable,

	// outside the HP pool and the cards - Game flips this when the escort cage clears.

	public bool joined = false;



	// on-body death-save readout (DESIGN:12 - the die lives ON the downed body, the

	// save is not log-only): Game writes dsColor/dsLastRoll, the body shows them

	public Color dsColor = new Color(1.0f, 1.0f, 1.0f); // chip mood: blood/amber/sage/grey

	public int dsLastRoll = 0;                         // the die face the chip shows

	public float reviveChannelTotal = 3.0f;            // mirrors Game.REVIVE_CHANNEL (bar width)

	Godot.ColorRect _chipRing = null;                  // state ring behind the die face

	Godot.ColorRect _chipFace = null;                  // grimoire-paper die face

	Godot.Label _chipNum = null;                       // the roll itself

	Godot.ColorRect _chanBg = null;                    // STAND STILL channel bar (player revive)

	Godot.ColorRect _chanFill = null;



	Godot.ColorRect _body;

	Godot.Label _ds;

	float _flash = 0.0f; // RED lane: set ONLY when damage actually lands (NoteHitFlash)

	float _atkFlash = 0.0f; // ghost-white swing pulse, kept off the harm lane

	// Game.TakeHit calls this the frame damage lands, so the mini reads its harm

	// flash from the one place that knows invuln/buffer rules already passed

	public void NoteHitFlash()

	{

		_flash = 0.12f;

	}

	// mini motion rig (user 2026-10-21): the body/art hangs on this pivot so the mini

	// can shuffle without touching the node's logical position, hitbox, chips, labels

	// or telegraph - and future art sprites reparent here unchanged

	Godot.Node2D _rig;

	float _rigT = 0.0f;

	float _toppleDir = 1.0f;

	// user 2026-10-21: a mini only shuffles while a finger is PUSHING it, so the rig

	// measures the node's actual travel per frame - stopped party settles its last

	// step instead of freezing mid-sway

	Vector2 _lastMovePos = new Vector2();

	float _moveSpeed = 0.0f;

	// real painted mini (user 2026-10-21): hangs on the rig and replaces the placeholder

	// square for PCs that have art; null until its texture load succeeds

	Godot.Sprite2D _mini;



	public override void _EnterTree()

	{

		AddToGroup("party");

		ZIndex = 5; // members (and their _draw telegraphs) paint above enemy squares

		SeedStatStore(); hp = (float)EffMaxHp(); // HEALTH tag is additive points on the authored base

		_body = new ColorRect();

		_body.Size = new Vector2(30.0f, 30.0f);

		_body.Position = -_body.Size * 0.5f;

		_body.Color = memberIndex == 0 ? new Color(0.92f, 0.92f, 0.95f)

									   : new Color(0.78f, 0.63f, 0.44f);

		_toppleDir = memberIndex % 2 == 0 ? 1.0f : -1.0f; // toppled minis fall opposite ways

		_rig = new Godot.Node2D();

		_rig.Name = "Rig";

		AddChild(_rig);

		_rig.AddChild(_body); // under the rig, NOT the root: see motion block below

		// painted minis (user 2026-10-21): cleaned art passed the audit - Baldrun 98x118

		// (hard alpha, 50 opaque colours, dead-centred) and Mosiah 79x119 (0 soft-alpha px,

		// 58 colours, cx_delta 0) and Asper 80x119 (0 soft-alpha px, 61 colours, cx_delta -0.5,

		// 1px margins, rim read 7px like Mosiah) and Alvora 94x119 (0 soft-alpha px, 61 colours,

		// cx_delta -0.5, 1px margins, rim read 7px). Sprite lives on the RIG so the shuffle AND the topple

		// pivot at the base-rim centre, not the canvas centre - a knocked-over miniature

		// falls around its own base. Hitbox, chips, labels, telegraphs untouched.

		string miniPath = "";

		float miniScale = 0.75f; // all four canvases ~118-119 tall -> ~88 px stage, under the 96 px boss

		float miniRim = 7.0f;    // px from canvas bottom to the base-rim band's centre (audit read)

		if (displayName == "Baldrun")

			miniPath = "res://art/baldrun.png";

		else if (displayName == "Mosiah")

			miniPath = "res://art/mosiah.png";

		else if (displayName == "Asper")

			// 152-163 region: name -> art lookup (Asper art audit-passed, rim/scale carry over)

			miniPath = "res://art/asper.png";

		else if (displayName == "Alvora")

			// Alvora art audit-passed the same class 2026-10-23 - rim/scale carry over

			miniPath = "res://art/alvora.png";

		else if (displayName == "Yu")

		{

			// 2026-10-26: Yu art audit-passed (85x114 canvas, hard alpha 0 soft px, 61 opaque

			// colours, 1 px margins, cx_delta -0.5). BUT the plinth is deeper than the four:

			// row profile shows the base bottom ellipse at its widest (centre) on row 96,

			// 17.5 px up from the canvas edge - so the rim pin is MEASURED, not carried over

			miniPath = "res://art/yu.png";

			miniRim = 17.5f;   // rig origin pinned to the base ellipse centre (topple + dash plate)

		}

		else if (displayName == "Stack")

			// 2026-10-30: Stack art audit-passed (81x117 canvas, hard alpha 0 soft px, 51 opaque

			// colours, 1-2 px margins, cx_delta 0.0) - margins match the class, so scale/rim carry

			miniPath = "res://art/stack.png";

		else if (displayName == "Wash")

			// 2026-10-31: Wash art audit-passed (80x121 canvas, hard alpha 0 soft px, 60 opaque

			// colours, 1 px margins all sides, cx_delta -0.7). Row profile shows the base plate shallow

			// at the canvas edge like the class, so scale/rim carry over

			miniPath = "res://art/wash.png";

		else if (displayName == "Bruno")

			// 2026-10-31: Bruno art audit-passed (85x118 canvas, hard alpha 0 soft px, 61 opaque

			// colours, 1 px margins all sides, cx_delta -3.2). Row profile shows the base plate

			// shallow at the canvas edge like the class (identical bottom profile to Wash), so

			// 			// scale/rim carry over; the wider cx_delta is his two-handed axe stance, honest

			miniPath = "res://art/bruno.png";

		else if (displayName == "Brook")

			// 2026-10-31: Brook art audit-passed (80x118 canvas, hard alpha 4017 px, 0 soft

			// px, 61 opaque colours, 1 px margins all sides, cx_delta -1.65). Row profile is

			// IDENTICAL bottom geometry to Bruno (22 px baseline, same widening ramp), so

			// scale/rim carry over; his cx_delta sits centred like the class

			miniPath = "res://art/brook.png";

		if (miniPath != "")

		{

			Godot.Texture2D tex = GD.Load(miniPath) as Godot.Texture2D;

			if (tex != null)

			{

				_mini = new Godot.Sprite2D();

				_mini.Texture = tex;

				// 4.7 has no C# TextureFilterMode enum: 0 = TEXTURE_FILTER_NEAREST straight

				// into the native CanvasItem setter, so the painted edges stay hard

				_mini.Set("texture_filter", 0);

				_mini.Scale = new Vector2(miniScale, miniScale);

				// rim centre pinned to the rig origin: the topple AND the dash plate

				// (y+12 in Game.cs) anchor off this same contact point

				_mini.Position = new Vector2(0.0f, -(tex.GetHeight() * 0.5f - miniRim) * miniScale);

				_rig.AddChild(_mini);

				_body.Size = new Vector2(0.0f, 0.0f); // art replaces the square outright

			}

		}

		// fx art armature (2026-11-01 art pass): sprites exist ONLY when the PC carries art on its

		// export; a missing load = null and the kit stays greybox exactly as shipped (the smoke

		// contract never depends on art). Children of the member node, parked invisible; the commit

		// hooks pose them with GlobalPosition and _fxT decay hides them off-beat.

		if (meleeFxArt != "")

		{

			Godot.Texture2D fxT = GD.Load(meleeFxArt) as Godot.Texture2D;

			if (fxT != null)

			{

				_meleeFx = new Godot.Sprite2D();

				_meleeFx.Texture = fxT;

				_meleeFx.Set("texture_filter", 0); // NEAREST like the painted minis: hard edges

				_meleeFx.Visible = false;

				AddChild(_meleeFx);

			}

		}

		if (shieldFxArt != "")

		{

			Godot.Texture2D fxT = GD.Load(shieldFxArt) as Godot.Texture2D;

			if (fxT != null)

			{

				_shldTex = fxT; // fx-feel10: keep only the texture; ShieldArt(slot) builds a disc per slot on use

			}

		}

		if (axeFxArt != "")

		{

			Godot.Texture2D fxT = GD.Load(axeFxArt) as Godot.Texture2D;

			if (fxT != null)

			{

				_axeFx = new Godot.Sprite2D[5];

				for (int f2 = 0; f2 < 5; f2++)

				{

					_axeFx[f2] = new Godot.Sprite2D();

					_axeFx[f2].Texture = fxT;

					_axeFx[f2].Set("texture_filter", 0);

					_axeFx[f2].Visible = false;

					AddChild(_axeFx[f2]);

				}

				_cresFx = new Godot.Sprite2D[5]; // fx-feel8: the crescents ride their OWN slots so BOTH arts can be on screen at once

				for (int f2 = 0; f2 < 5; f2++)

				{

					_cresFx[f2] = new Godot.Sprite2D();

					_cresFx[f2].Texture = fxT;

					_cresFx[f2].Set("texture_filter", 0);

					_cresFx[f2].Visible = false;

					AddChild(_cresFx[f2]);

				}

			}

		}

		_ds = new Label();

		_ds.Position = new Vector2(-24.0f, -58.0f); // above-head label lane (the dash bar

													// moved under the mini 2026-10-21, so this

													// lane no longer fights it)

		AddChild(_ds);

		// d20 death-save chip (DESIGN:12): paper face + ink number + state ring, parked

		// to the body's right so it never fights the ds label (y-58), the dash bar

		// (y-42) or the channel bar (y-30). Pure ColorRects/Labels - the C# _draw hook

		// is dead in 4.7.

		_chipRing = new Godot.ColorRect();

		_chipRing.Size = new Vector2(40.0f, 40.0f);

		_chipRing.Position = new Vector2(12.0f, -20.0f);

		_chipRing.Color = new Color(0.70f, 0.70f, 0.70f, 1.0f);

		_chipRing.ZIndex = 6;

		AddChild(_chipRing);

		_chipFace = new Godot.ColorRect();

		_chipFace.Size = new Vector2(34.0f, 34.0f);

		_chipFace.Position = new Vector2(15.0f, -17.0f);

		_chipFace.Color = new Color(0.957f, 0.929f, 0.847f, 1.0f); // grimoire paper

		_chipFace.ZIndex = 7;

		AddChild(_chipFace);

		_chipNum = new Label();

		_chipNum.Position = new Vector2(23.0f, -14.0f); // ink sits INSIDE the face

		// the invisible-chip bug: a Label's default theme color is near-white on a

		// paper-white face - the roll never showed. Dark grimoire ink instead.

		_chipNum.Modulate = new Color(0.10f, 0.09f, 0.08f, 1.0f);

		_chipNum.ZIndex = 8;

		AddChild(_chipNum);

		_chanBg = new Godot.ColorRect();

		_chanBg.Size = new Vector2(48.0f, 6.0f);

		_chanBg.Position = new Vector2(-24.0f, -30.0f);

		_chanBg.Color = new Color(0.12f, 0.11f, 0.10f, 0.9f);

		_chanBg.ZIndex = 7;

		AddChild(_chanBg);

		_chanFill = new Godot.ColorRect();

		_chanFill.Size = new Vector2(0.0f, 6.0f);

		_chanFill.Position = new Vector2(-24.0f, -30.0f);

		_chanFill.Color = new Color(0.83f, 0.64f, 0.24f, 1.0f); // gold = rescue channel running

		_chanFill.ZIndex = 8;

		AddChild(_chanFill);

		// telegraph: a pivot + thin ColorRect "spokes" radiating along facing. The

		// maul is a ~150 deg ARC CLEAVE (human-test locked): spokes show the fan

		// edges + middle; the rapier stays one solid stab line. Pure nodes because

		// both the C# _draw hook and drawing outside _draw are dead ends in 4.7.

		_telePivot = new Godot.Node2D();

		_telePivot.Name = "Telegraph";

		AddChild(_telePivot);

		bool maul = arcHalf > 0.5f; // maul kit: arc cleave, full damage per body (user 2026-10-23)

		bool circle = arcHalf > 3.0f; // only a deliberate 360 deg kit gets a dial

		int spokes = circle ? 12 : (maul ? 3 : 1);

		float w = circle ? 4.0f : (maul ? 5.0f : 7.0f);

		Color col = circle || maul ? new Color(1.0f, 0.55f, 0.2f, 0.45f)

								   : new Color(0.5f, 1.0f, 1.0f, 0.55f);

		_teleParts = new Godot.ColorRect[spokes];

		for (int i = 0; i < spokes; i++)

		{

			Godot.ColorRect s = new Godot.ColorRect();

			s.Size = new Vector2(range, w);

			s.Position = new Vector2(0.0f, -w * 0.5f);

			s.Rotation = circle ? (float)(System.Math.PI * 2.0 * i / 12.0)

								: (spokes == 1 ? 0.0f : (float)(-arcHalf + 2.0 * arcHalf * i / (spokes - 1)));

			s.Color = col;

			_telePivot.AddChild(s);

			_teleParts[i] = s;

		}

		// Spirit Guardians ring (Alvora): six motes orbiting at the ring radius under one pivot.

		// Built for EVERY member and hidden by the visibility gate below unless this member IS

		// the aura kit, so the art pass never has to special-case which member owns which armature.

		// The pivot's live Scale carries the "wider ring" rank, so the motes are authored at the

		// base radius only.

		_auraPivot = new Godot.Node2D();

		_auraPivot.Name = "Guardians";

		AddChild(_auraPivot);

		_auraPivot.Visible = false;

		_auraParts = new Godot.ColorRect[6];

		for (int i = 0; i < 6; i++)

		{

			Godot.ColorRect s = new Godot.ColorRect();

			s.Size = new Vector2(9.0f, 9.0f);

			float a = (float)(System.Math.PI * 2.0 * i / 6.0);

			s.Position = new Vector2((float)System.Math.Cos(a) * range, (float)System.Math.Sin(a) * range);

			// twilight violet: the guardians read as spirits, not another weapon flash

			s.Color = new Color(0.72f, 0.55f, 0.95f, 0.5f);

			s.ZIndex = 4; // under the body (5) so a mote never hides the mini

			_auraPivot.AddChild(s);

			_auraParts[i] = s;

		}

		// user 2026-11-02 art pass: the six motes ride the Alvora sprite (art/fx/alvora_spirit.png,

		// 94x104) tinted spectral blue at ~1/3 native size (the user's "shrink it down to about 1/3"),

		// parked on the SAME ring spots as the greybox and parented to the same pivot. PURE ART:

		// hit test, pulse flare timing and ring ranks untouched.

		if (spiritFxArt != "")

		{

			Godot.Texture2D fxT = GD.Load(spiritFxArt) as Godot.Texture2D;

			if (fxT != null)

			{

				_auraSprites = new Godot.Sprite2D[6];

				for (int i2 = 0; i2 < 6; i2++)

				{

					_auraParts[i2].Visible = false; // art owns this mote slot

					_auraSprites[i2] = new Godot.Sprite2D();

					_auraSprites[i2].Texture = fxT;

					_auraSprites[i2].Set("texture_filter", 0);

					_auraSprites[i2].Position = _auraParts[i2].Position; // same authored ring spot as the greybox

					_auraSprites[i2].Scale = new Vector2(0.25f, 0.25f); // user 2026-11-02: 1/4 of the 94x104 art (-> ~24x26 on screen)

						_auraSprites[i2].Modulate = new Color(0.62f, 0.88f, 0.85f, 0.6f); // fx-feel9 (user 2026-11-02: "update Alvora's spirit transparency to 40%"): alpha 0.6 = 40% transparent (was 0.2); the flare + settle loops rewrite this Modulate every frame, so their alpha matches (all three 0.6)

					_auraSprites[i2].ZIndex = 4; // same layer the greybox motes hold, under the body (5)

					_auraPivot.AddChild(_auraSprites[i2]);

				}

				// user 2026-11-02: a translucent teal disc out to the spirit ring so the aura READS as an area,

				// not six lone dots - it rides the SAME pivot, so wide-ring/AREA scaling stretches it exactly

				// like the hit test does (hit test, ring ranks and pulse timing untouched: PURE ART)

				Godot.Polygon2D auraDisc = new Godot.Polygon2D();

				auraDisc.Name = "AuraArea";

				auraDisc.ZIndex = 3; // under the motes (4) and the body (5)

				auraDisc.Set("modulate", new Color(0.35f, 0.75f, 0.72f, 0.18f)); // light teal fill, ~18% alpha (assistant pick of the user's "semi-transparent teal")

				Vector2[] disc = new Vector2[24]; // 24-gon: smooth enough at ring scale, zero Rng

				for (int i3 = 0; i3 < 24; i3++)

					disc[i3] = new Vector2((float)(System.Math.Cos(System.Math.PI * 2.0 * i3 / 24.0) * range), (float)(System.Math.Sin(System.Math.PI * 2.0 * i3 / 24.0) * range)); // reaches the spirit ring spots (same authored radius)

				auraDisc.Polygon = disc;

				_auraPivot.AddChild(auraDisc);
				_auraDisc = auraDisc; // keep a handle so the disc can hold still while the ring orbits

			}

		}

	}



	public void SetDsText(string s)

	{

		if (_ds != null)

			_ds.Text = s;

	}



	public void SetDsTemp(string s, double hold)

	{

		SetDsText(s);

		dsHold = (float)hold;

	}



	// ---------- canonical stat accessors (StatTags registry) ----------

	// ---------- ultimate button (R, user 2026-10-25) ----------

	public int ultKey()

	{

		if (displayName == "Baldrun")

			return 1; // Giant's Might

		if (displayName == "Mosiah")

			return 2; // Bardic Inspiration

		if (displayName == "Alvora")

			return 3; // Guardian Awakening

		if (displayName == "Asper")

			return 4; // Swarm of Bees

		if (displayName == "Yu")

			return 5; // The Kaka

		if (displayName == "Stack")

			return 6; // Experimental Elixir (the draw lives on Game.ElixirParty)

		if (displayName == "Wash")

			return 7; // Oakhollow Assemble! (the walk lives on Game.OakHollowParty)

		// Bruno "Axes Away!" (user 2026-10-31): his OWN orbit, no roster walk - the blades

		// are PartyMember lanes, Game only sets the spin clock

		if (displayName == "Bruno")

			return 8; // Axes Away! (the spin clock lives on Game.BrunoAxesAway)

		// Brook "Assassination" (user 2026-10-31): party-wide window, the walk lives on Game

		// like bardic/oak/elixir (every ally carries its OWN assT clock set by the walk)

		if (displayName == "Brook")

			return 9; // Assassination (the window lives on Game.AssassinationParty)

		return 0; // other PCs: no ultimate authored yet (future lanes land here)

	}

	public bool TryUltimate()

	{

		if (ultCdT > 0.0)

		{

			GD.Print("[10thAge] (ult) " + displayName + " still cooling: " +

				(int)System.Math.Ceiling(ultCdT) + " s");

			return false; // press on cooldown is a no-op, gate not wasted

		}

		if (ultKey() == 1)

		{

			ultCdT = ULT_CD_BASE / stat[StatTags.ULT];

			ultT = ULT_LASTS;

			_grown = true;

			Scale = new Vector2((float)ULT_SIZE, (float)ULT_SIZE);

			GD.Print("[10thAge] (ult) Giant's Might: " + displayName +

				" doubles size and reach (damage x1.5, attack speed x1.5) for " + (int)ULT_LASTS +

				" s, gate " + (int)(ULT_CD_BASE / stat[StatTags.ULT]) + " s");

			return true;

		}

		else if (ultKey() == 2)

		{

			ultCdT = ULT_CD_BASE / stat[StatTags.ULT];

			// roster walk lives on Game (private _party; the group API would wrestle

			// Godot.Array bindings); it sets insT on EVERY ally incl. the caster

			Game g2 = GetParent() as Game;

			if (g2 != null)

				g2.InspireParty(INS_LASTS);

			GD.Print("[10thAge] (ult) Bardic Inspiration: " + displayName +

				" rallies ALL allies (+25% damage, +25% attack speed) for " + (int)INS_LASTS +

				" s, gate " + (int)(ULT_CD_BASE / stat[StatTags.ULT]) + " s");

			return true;

		}

		else if (ultKey() == 3)

		{

			ultCdT = ULT_CD_BASE / stat[StatTags.ULT];

			awaT = AWA_LASTS;

			awaKills = 0;

			_awaUp = true;

			GD.Print("[10thAge] (ult) Guardian Awakening: " + displayName +

				" doubles ring reach (after ranks) and burns it +25% for " + (int)AWA_LASTS +

				" s; every horde kill while awake heals ALL allies 1 hp, gate " +

				(int)(ULT_CD_BASE / stat[StatTags.ULT]) + " s");

			return true;

		}

		else if (ultKey() == 4)

		{

			ultCdT = ULT_CD_BASE / stat[StatTags.ULT];

			// the swarm lives on Game: it owns the enemy registry and the bee pool;

				// the member just stamps the fan center (user: circle around Asper)

			Game g3 = GetParent() as Game;

			if (g3 != null)

				g3.SpawnBees(GlobalPosition);

			GD.Print("[10thAge] (ult) Swarm of Bees: " + displayName +

				" looses 6 hunters in a circle (5 dmg/s each, 10 s) spread out to hunt, gate " +

				(int)(ULT_CD_BASE / stat[StatTags.ULT]) + " s");

			return true;

		}

		else if (ultKey() == 5)

		{

			ultCdT = ULT_CD_BASE / stat[StatTags.ULT];

			// the empowered familiar lives on the Game-side summon pool: it already steers, fuses and

			// blasts no-split, so the member just stamps the ring center and the ALREADY-SCALED

			// kit numbers - the x2 damage and x2 blast multiply on top of that scaling inside

			// SpawnKakas (same discipline as AWA_RANGE after the AREA tag).

			Game g4 = GetParent() as Game;

			if (g4 != null)

			{

				float blast = (float)(minionRadius * stat[StatTags.AREA] * LeaderAreaMul()); // wider burst rank: same as the ritual | Bruno lead +15%

				g4.SpawnKakas(GlobalPosition, RollDamage(g4, weaponDamage), minionFuse, blast, minionSpeed, luckR);

			}

			GD.Print("[10thAge] (ult) The Kaka: " + displayName +

				" looses 6 empowered familiars in a circle (blast x2, damage x2 after ranks, same fuse) that spread out, gate " +

				(int)(ULT_CD_BASE / stat[StatTags.ULT]) + " s");

			return true;

		}

		else if (ultKey() == 6)

		{

			ultCdT = ULT_CD_BASE / stat[StatTags.ULT];

			// the draw + the six clocks live on the Game side: it owns the roster walk and the RNG

			Game g5 = GetParent() as Game;

			if (g5 != null)

				g5.ElixirParty();

			GD.Print("[10thAge] (ult) Experimental Elixir: " + displayName +

				" brews TWO random elixirs of six (10% all six) for ALL allies 10 s, gate " +

				(int)(ULT_CD_BASE / stat[StatTags.ULT]) + " s");

			return true;

		}

		else if (ultKey() == 7)

		{

			ultCdT = ULT_CD_BASE / stat[StatTags.ULT];

			// the party-wide clock lives on the Game side: it owns the roster walk (same discipline as

			// bardic/bees/kaka/elixir)

			Game g6 = GetParent() as Game;

			if (g6 != null)

				g6.OakhollowParty();

			GD.Print("[10thAge] (ult) Oakhollow Assemble!: " + displayName +

				" rallies ALL allies (+25% move, +5 armour, regen 1% max hp/s) for 10 s, gate " +

				(int)(ULT_CD_BASE / stat[StatTags.ULT]) + " s");

			return true;

		}

		// Bruno "Axes Away!" (user 2026-10-31): the spin clock lives on the Game side like

		// the oak walk (Game sets m.orbitT + prints the cast line); the blades + pulses are

		// PartyMember lanes (his own orbit, no roster walk needed). Blades + disc + bleed are

		// read LIVE by the spin lane, so the spin "gains the effects of all upgrades".

		else if (ultKey() == 8)

		{

			ultCdT = ULT_CD_BASE / stat[StatTags.ULT];

			Game g7 = GetParent() as Game;

			if (g7 != null)

				g7.BrunoAxesAway(this);

			GD.Print("[10thAge] (ult) Axes Away!: " + displayName +

				" hurls " + (2 + axeN) + " massive axes that spin around him for 10 s (size + spin speed + bleed ride his ranks), gate " +

				(int)(ULT_CD_BASE / stat[StatTags.ULT]) + " s");

			return true;

		}

		// Brook "Assassination" (user 2026-10-31): the identification IS the window - the

		// party reads weak points through its own crit/multistrike readers (RollDamage asks

		// assT live), so Game only walks the roster to set each ally's own assT clock.

		else if (ultKey() == 9)

		{

			ultCdT = ULT_CD_BASE / stat[StatTags.ULT];

			Game g8 = GetParent() as Game;

			if (g8 != null)

				g8.AssassinationParty();

			GD.Print("[10thAge] (ult) Assassination: " + displayName +

				" reads the prey's weak points - ALL allies gain +25% multistrike, +15% crit chance and +25% crit damage for 10 s, gate " +

				(int)(ULT_CD_BASE / stat[StatTags.ULT]) + " s");

			return true;

		}

		// GD.Print("[10thAge] (ult) " + displayName + " has no ultimate yet");

		return false;

	}



	/** Seed the stat store from the registry BASE (once, in _EnterTree). */

	public void SeedStatStore()

	{

		for (int i = 0; i < StatTags.N; i += 1)

			stat[i] = StatTags.BASE[i];

		stat[StatTags.ARMOUR] += baseArmour; // per-PC authored armour lands on the store

		// Brook authored evasion (user 2026-10-31: 10% dodge): like armour, the EVASION tag

		// seeds at BASE then adds this per-PC number (reader lives on Game.TakeHit)

	 stat[StatTags.EVASION] += baseEvasion;    // authored dodge lands on the store

	}

	/** Authored weaponDamage scaled by the ATTACK_DAMAGE tag. */

	public double EffDamage() { return weaponDamage * stat[StatTags.ATTACK_DAMAGE]; }

	/** Authored cooldown divided by the ATTACK_SPEED tag (higher = faster attacks). */

	public double EffCooldown()

	{

		double spd = stat[StatTags.ATTACK_SPEED] * (insT > 0.0 ? INS_SPD : 1.0) * (ultT > 0.0 ? ULT_SPD : 1.0); // bardic +25% | Giant's Might +50% attack speed (user 2026-11-06)

		if (spd < 0.05)

			spd = 0.05; // clamp: stacked speed cards can't drive the timer to zero

		return cooldown / spd;

	}

	/** Bruno seat 7 perk readers (user 2026-10-31: "All allies gain 15% increased attack size

	(area) and 10% increased movement speed" while Bruno leads): the Game handle multiplies at the

	READER sites like Mosiah's pierce - NO stat-store writes, so the 17-row audit stays a

	permanent-grant ledger and a seat swap retargets by itself. */

	private double LeaderAreaMul() { Game g = GetParent() as Game; return g == null ? 1.0 : g.LeaderAreaMul(); }

	private double LeaderInspireHp() { Game g = GetParent() as Game; return g == null ? 0.0 : g.LeaderInspireHp(); } // Inspiring Leader seat rides live (same GetParent discipline as the Bruno pair)

	private double LeaderMoveMul() { Game g = GetParent() as Game; return g == null ? 1.0 : g.LeaderMoveMul(); }

	/** Authored reach scaled by the AREA tag (Bruno lead: +15%). */

	public double EffRange() { return range * stat[StatTags.AREA] * (ultT > 0.0 ? ULT_RANGE : 1.0) * (awaT > 0.0 ? AWA_RANGE : 1.0) * LeaderAreaMul(); } // awakening widens too | Bruno lead widens too

	/** Authored maxHp plus the additive HEALTH tag (points). */

	public double EffMaxHp() { return maxHp + stat[StatTags.HEALTH] + LeaderInspireHp(); } // Inspiring Leader seat: +15 hp ceiling for ALL allies while Wash leads

	/** Authored baseSpeed scaled by the MOVEMENT_SPEED tag. */

	public double EffMove() { return baseSpeed * stat[StatTags.MOVEMENT_SPEED] * (elixSwiftT > 0.0 ? 1.10 : 1.0) * (oakT > 0.0 ? 1.25 : 1.0) * LeaderMoveMul(); } // Swiftness elixir +10% | Oakhollow +25% | Bruno lead +10%: move while up

	/** Whole repeats of the current attack (MULTISTRIKE tag, kind COUNT). */

	public int Strikes()

	{

		int n = (int)stat[StatTags.MULTISTRIKE];

		if (n < 1)

			n = 1;

		if (n > 5)

			n = 5; // same 1..5 ceiling the shot fan always used

		return n;

	}

	/**

	 * Party-side damage surface: rolls CRIT_CHANCE on the run RNG (all allies crit,

	 * enemies never do), pays CRIT_DAMAGE on a hit, drips LIFESTEAL back into the

	 * body, and returns the number to hand TakeDamage. This deliberately lives on

	 * the party side, NOT inside Combatant.TakeDamage which the horde shares.

	 */

	public double RollDamage(Godot.Node2D host, double amount)

	{

		// Giant's Might: +50% on every party hit while the buff is up (single choke

		// point: all kits, the shot pool and insect seats route through here)

		if (ultT > 0.0)

			amount *= ULT_DMG;

		if (insT > 0.0)

			amount *= INS_DMG; // bardic: +25% on every ally hit while inspired

		if (awaT > 0.0)

			amount *= AWA_DMG; // awakening: +25% on Alvora hits while awake (user 2026-11-06: was +50%)

		if (elixBoldT > 0.0)

			amount *= 1.10; // Boldness elixir: +10% on every hit while brewed

		// multistrike CHANCE (Brook's lucky-strikes card + Assassination window): on a proc THIS

		// hit deals damage TWICE (user 2026-10-31: 5%/rank on attacks, the ult gives ALL allies

		// 25%). One extra run-stream draw only while a chance is live (zero cost otherwise);

		// lifesteal below rides the doubled hit, so a proc steals proportionally.

	 double msChance = multiChance + (assT > 0.0 ? 0.25 : 0.0);

		if (msChance > 0.0 && host is Game gms && gms.Rng != null && gms.Rng.Randf() < msChance)

			amount *= 2.0; // damage twice: the commit repeats its bite on the same strike

		if (host is Game g && g.Rng != null && g.Rng.Randf() < stat[StatTags.CRIT_CHANCE] + (elixPrecT > 0.0 ? 0.10 : 0.0) + (g.WashUp() ? 0.05 : 0.0) + (assT > 0.0 ? 0.15 : 0.0)) // Precision +10% while brewed | Smite +5% while Wash stands | Assassination +15% while the window is up

			amount *= stat[StatTags.CRIT_DAMAGE] * (host is Game gsw && gsw.WashUp() ? 1.25 : 1.0) * (assT > 0.0 ? 1.25 : 1.0); // Smite +25% crit bite | Assassination weak points bite +25%

		double steal = amount * stat[StatTags.LIFESTEAL];

		if (steal > 0.0)

		{

			hp += (float)steal;

			double cap = EffMaxHp();

			if (hp > cap)

				hp = (float)cap;

		}

		return amount > 0.5 ? amount : 0.5; // same 0.5 floor the old commits kept

	}

	public override void _Process(double d)

	{

		Game pg0 = GetParent() as Game;

		if (pg0 != null && pg0.GetWorldPause())

			return; // picker / join-banner soft pause: nobody swings while a card is up

		float delta = (float)d;
		// user 2026-11-02: max-HP growth lifts CURRENT hp 1:1 (Wash's Inspiring Leader seat is the
		// grant today: +15 ceiling for ALL allies while he leads, and everyone sat stuck at the
		// old cap under it). Track the ceiling: growth pushes hp up by the EXACT amount it grew;
		// a withdrawn bonus (seat swap away) clamps hp into the new cap. Downed/eliminated pools
		// never get a free revive. First sight HEALS a whole body up to the RAISED cap: party init and
		// stranded-recruit spawn pay the BASE maxHp, so under Wash's +15 seat every ally sat at 70/85
		// from frame one (user 2026-11-02: "They should be full HP").
		{
			// scoped: this frame's effective ceiling vs the tracked one
			float mxEff = (float)EffMaxHp();
			if (_hpMaxSeen < 0.0f)
			{
				_hpMaxSeen = mxEff; // first sight of the ceiling: seed the tracker...
				if (hp > 0.0f && !downed && !eliminated && hp < mxEff)
					hp = mxEff; // ...and a body that spawned WHOLE rides AT the raised cap: run start and joins paid the BASE maxHp, not EffMaxHp, so Wash's Inspiring Leader seat left every ally one tier under glass from the first frame (user: "They should be full HP")
			}
			else if (mxEff > _hpMaxSeen)
			{
				if (hp > 0.0f && !downed && !eliminated)
					hp += mxEff - _hpMaxSeen; // the raise lifts current hp by the SAME amount (user)
				_hpMaxSeen = mxEff;
			}
			else if (mxEff < _hpMaxSeen)
			{
				_hpMaxSeen = mxEff; // bonus withdrawn: track the lower ceiling...
				if (hp > mxEff)
					hp = mxEff; // ...and clamp hp down to the new cap
			}
		}

		// looping world: fold every body but the Leader into the wrap box around the

		// camera (the Leader IS the anchor; folding it would pop the camera across a

		// seam). This is what keeps downed/eliminated bodies and the stranded recruit

		// reachable forever - walk any direction and the world loops back to them.

		// Live ring members are welded near the Leader, so the fold is a no-op for them.

		if (pg0 != null && !isLeader)

			GlobalPosition = pg0.WrapNearCamera(GlobalPosition);

		if (dsDamageBuffer > 0.0f)

			dsDamageBuffer -= delta; // ticks even while downed: the 2 s fail-buffer

		if (invuln > 0.0f)

			invuln -= delta; // post-revive immunity window, ticks always

		// hp regen (REGEN tag): points per second, never while downed or unjoined

		if (!downed && joined && (stat[StatTags.REGEN] > 0.0 || elixHealT > 0.0 || oakT > 0.0)) // Healing elixir + Oakhollow ride the same lane

		{

			double regenCap = EffMaxHp();

			if (hp < regenCap)

			{

				hp += (float)((stat[StatTags.REGEN] + (elixHealT > 0.0 ? EffMaxHp() * 0.01 : 0.0) + (oakT > 0.0 ? EffMaxHp() * 0.01 : 0.0)) * delta); // Healing elixir + Oakhollow: 1% max hp/s each

				if (hp > regenCap)

					hp = (float)regenCap;

			}

		}

		// eliminated = out until the next campfire: the body, telegraph and ds label

		// go hidden; writing visibility every frame is what un-hides a member when

		// the campfire lifts them back up

		bool hidden = eliminated;

		// user bug 2026-10-20: Mosiah popped into the world long before his beat. A

		// not-yet-joined PC stays hidden until the recruit event makes it the pending

		// one (the HELP! arrow target); camp/escort joins lift the gate because joined

		// becomes true

		PartyMember pend = pg0 != null ? pg0.GetRecruitPending() : null;

		if (!joined && !isLeader && (pend == null || pend.GetInstanceId() != GetInstanceId()))

			hidden = true;

		if (_body != null)

			_body.Visible = !hidden;

		if (_mini != null)

			_mini.Visible = !hidden; // same gate: campfire un-hides art and square alike

		// the telegraph armature is a MELEE property: a bow shows its bolt in flight and the

		// cleric shows orbiting guardians, so neither carries a fan of ground spokes. (The commit

		// site already gated the swing; this is the IDLE frame, where the spokes would otherwise

		// sit at their authored alpha forever on a member that never swings.)

		if (_telePivot != null)

			_telePivot.Visible = !hidden && weaponKit == "melee";

		if (_ds != null)

			_ds.Visible = !hidden;

		// the guardians ring belongs to a LIVE, joined cleric only: a stranded recruit stands

		// waiting without an aura, and a downed body has no spirits

		if (_auraPivot != null)

			_auraPivot.Visible = !hidden && joined && !downed && weaponKit == "aura";

		// ---- downed-body readout: the d20 chip + the STAND STILL channel bar ----

		bool downChip = downed && !eliminated;

		float chanTotal = reviveChannelTotal > 0.0f ? reviveChannelTotal : 3.0f;

		bool channeling = downChip && reviveProgress > 0.0f;

		if (_chipRing != null)

		{

			_chipRing.Visible = downChip;

			if (downChip)

				_chipRing.Color = stable ? new Color(0.95f, 0.75f, 0.35f, 1.0f)

										 : (dsFails > 0 ? new Color(0.88f, 0.42f, 0.42f, 1.0f)

														: new Color(0.70f, 0.70f, 0.70f, 1.0f));

		}

		if (_chipFace != null)

			_chipFace.Visible = downChip;

		if (_chipNum != null)

		{

			_chipNum.Visible = downChip;

			_chipNum.Text = dsLastRoll > 0 ? "" + dsLastRoll : "d20";

		}

		if (_chanBg != null)

			_chanBg.Visible = channeling;

		if (_chanFill != null)

		{

			_chanFill.Visible = channeling;

			float frac = channeling ? reviveProgress / chanTotal : 0.0f;

			if (frac > 1.0f)

				frac = 1.0f;

			_chanFill.Size = new Vector2(48.0f * frac, 6.0f);

		}

		if (channeling && _ds != null)

		{

			// what the player is DOING owns the chip, so the countdown beats a stale

			// death-save line (the ring + gold bar show the same channel visually)

			float rem = chanTotal - reviveProgress;

			int left = (int)rem; // ceil without Math.ceil (4.7 Math lacks it)

			if ((float)left < rem)

				left++;

			_ds.Text = "STAND STILL " + left + "s";

			_ds.Modulate = new Color(0.95f, 0.75f, 0.35f, 1.0f);

		}

		else if (_ds != null)

			_ds.Modulate = dsColor;

		// ---- tabletop mini motion (user 2026-10-21 "the party ARE minis"): a live,

		// joined mini shuffles side-to-side with a lean and a set-down tap, exactly

		// like fingers nudging a miniature; members run on OFFSET phases so the squad

		// reads as individually nudged, not a marching band. DOWNED or eliminated =

		// toppled mini (rim down, still). Not-yet-joined = stands upright waiting to

		// be picked up. Front-facing art makes this whole language flip-free.

		if (_rig != null)

		{

			bool liveMini = joined && !downed && !eliminated;

			if (liveMini)

			{

				// how far did this mini actually travel this frame? (post-fold coords,

				// so wrap seams read as 0 not a teleport)

				Vector2 gp = GlobalPosition;

				float moved = gp.DistanceTo(_lastMovePos);

				_lastMovePos = gp;

				if (moved > 400.0f)

					moved = 0.0f; // wrap-fold frame, not motion

				float inst = delta > 0.001f ? moved / delta : _moveSpeed;

				_moveSpeed += (inst - _moveSpeed) * (1.0f - (float)System.Math.Exp(-8.0 * delta));

				if (_moveSpeed > 30.0f) // being pushed across the table

				{

					float ratio = _moveSpeed / 130.0f; // ~full party push speed

					ratio = ratio < 0.35f ? 0.35f : (ratio > 1.4f ? 1.4f : ratio);

					_rigT += delta * 7.2f * ratio; // user 2026-10-21: a touch faster than pass-14, scaled by push

					float ph = _rigT + memberIndex * 0.9f;

					float sway = (float)System.Math.Sin(ph) * 3.0f;

					_rig.Position = new Vector2(sway, -(float)System.Math.Abs((float)System.Math.Sin(ph * 0.5)) * 1.2f);

					_rig.Rotation = sway * 0.017f; // the lean trails the push

				}

				else

				{

					// player stopped: glide the last sway back level - a set-down, not a jolt

					_rig.Position += -_rig.Position * (1.0f - (float)System.Math.Exp(-9.0 * delta));

					_rig.Rotation += -_rig.Rotation * (1.0f - (float)System.Math.Exp(-9.0 * delta));

				}

			}

			else

			{

				float top = downed || eliminated ? _toppleDir * (float)(System.Math.PI * 0.5) : 0.0f;

				_rig.Position += -_rig.Position * (1.0f - (float)System.Math.Exp(-9.0 * delta));

				_rig.Rotation += (top - _rig.Rotation) * (1.0f - (float)System.Math.Exp(-10.0 * delta));

			}

		}

		if (!joined || downed || eliminated)

			{
			_fxT = 0.0f; // DEATH-LANE FX CLEANUP (user 2026-11-04: Brook's stab sliver stayed on the
			// canvas when he died mid double-jab): this early return skipped the _fxT decay AND the
			// cdTimer tick, so attack art left visible at the moment of death lingered FOREVER -
			// generic for every kit: stab sliver / crescent / axe slots / shield discs (live flight
			// art drops too; the bullet itself keeps flying on its own node). twinArm=false forfeits
			// a stale second dagger so a body revived at camp starts a fresh beat instead.
			_cresDur = 0.0f;
			orbitT = 0.0;
			twinArm = false;
			if (_meleeFx != null)
				_meleeFx.Visible = false;
			if (_cresFx != null)
				 foreach (Godot.Sprite2D sx in _cresFx)
					 sx.Visible = false;
			if (_axeFx != null)
				 foreach (Godot.Sprite2D sx in _axeFx)
					 sx.Visible = false;
			for (int sk = 0; sk < _shldT.Length; sk++)
			{
				 Godot.Sprite2D da = ShieldArt(sk);
				 if (da != null)
					 da.Visible = false;
			}
			return; // a stranded recruit cannot swing: its weapon unlocks ON the join
		}

		// ---- Spirit Guardians ring: orbit + pulse flare ----

		// A slow constant orbit so the ring reads as HIS aura at a glance; the flare is what makes

		// a PULSE readable without a HUD. One pivot, so one Scale carries the "wider ring" rank.

		if (weaponKit == "aura" && _auraPivot != null)

		{

			_auraPivot.Scale = new Vector2((float)(stat[StatTags.AREA] * LeaderAreaMul() * (awaT > 0.0 ? AWA_RANGE : 1.0)), (float)(stat[StatTags.AREA] * LeaderAreaMul() * (awaT > 0.0 ? AWA_RANGE : 1.0))); // awakened ring: visible pivot matches the doubled hit test (hit test rides EffRange, same multiplier)

			_auraPivot.Rotation += (float)(1.1 * delta);

			if (_auraSprites != null)

			 foreach (Godot.Sprite2D s in _auraSprites)

				 s.Rotation = -_auraPivot.Rotation; // spirit faces stay upright while the ring orbits
			if (_auraDisc != null)
				_auraDisc.Rotation = -_auraPivot.Rotation; // the disc holds still: only the motes orbit (user 2026-11-02: the spinning fill read dizzy)

			if (_auraPulse > 0.0f)

			{

				_auraPulse -= delta;

				// flare decays over the pulse window, so the ring brightens then settles

				_auraPulse = _auraPulse < 0.0f ? 0.0f : _auraPulse;

				float f = _auraPulse / 0.22f;


				// art motes flare on top of the spectral base so the tick still reads

				if (_auraSprites != null)

					foreach (Godot.Sprite2D s in _auraSprites)

						s.Modulate = new Color(0.55f * (1.0f + f * 0.7f), 0.78f, 1.0f * (1.0f + f * 0.4f), 0.6f); // fx-feel9: flare rides the SAME 40% transparency (0.6), reads via brightness only
				foreach (Godot.ColorRect s in _auraParts)

					s.Modulate = new Color(1.0f + f * 0.7f, 1.0f, 1.0f + f * 0.4f);

			}

			else

			{

				foreach (Godot.ColorRect s in _auraParts)

					s.Modulate = new Color(1.0f, 1.0f, 1.0f);

				// art motes settle back to the spectral base (build tint would otherwise be
				// overwritten by the white loop above)

				if (_auraSprites != null)

					foreach (Godot.Sprite2D s in _auraSprites)

						s.Modulate = new Color(0.55f, 0.78f, 1.0f, 0.6f); // fx-feel9: settled motes ride the same 40% transparency (0.6)

			}

		}

		if (_flash > 0.0f)

		{

			_flash -= delta;

			// RED = the body actually took damage (user 2026-10-21: harm owns the red

			// lane; swings used to borrow it). Tint, not a white multiply: painted

			// minis barely budge under >1 modulate

			_body.Modulate = new Color(1.0f, 0.35f, 0.35f);

			if (_mini != null)

				_mini.Modulate = new Color(1.0f, 0.35f, 0.35f);

		}

		else if (_atkFlash > 0.0f)

		{

			_atkFlash -= delta;

			// SWING = ghost-white commit pulse. Modulate only multiplies, so a bright

			// white is impossible on painted art - semi-transparent IS the white read

			if (_mini != null)

				_mini.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.55f);

			else

				_body.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.6f); // placeholder squares ghost alike

		}

		else

		{

			_body.Modulate = new Color(1.0f, 1.0f, 1.0f);

			if (_mini != null)

				_mini.Modulate = new Color(1.0f, 1.0f, 1.0f);

		}

		// fx art decay: commit flashes live ~0.15 s then drop; while the spin clock runs, the

		// spin block re-places the blade slots every frame, so this decay only hunts crescent,

		// shield and melee flashes off-beat (world clock: soft-pause discipline, no real-time leak)

		if (_fxT > 0.0f)

		{

			_fxT -= delta;

			if (_fxT <= 0.0f)

			{

				if (_meleeFx != null)

					_meleeFx.Visible = false;

				for (int sk = 0; sk < _shldT.Length; sk++)
				{
					// fx-feel10: only IDLE slots drop their disc art - a live flight keeps its own
					if (_shldT[sk] == null && ShieldArt(sk) != null)
						ShieldArt(sk).Visible = false;
				}

				if (_axeFx != null && orbitT <= 0.0)

					foreach (Godot.Sprite2D sx in _axeFx)

						sx.Visible = false; // spin slots keep drawing while the spin clock runs (the spin re-arms _fxT every frame)

						if (_cresFx != null && _cresDur <= 0.0f)

							foreach (Godot.Sprite2D sx in _cresFx)

								sx.Visible = false; // fx-feel8: crescent flash done on its OWN slots; the flight-clock guard keeps a volley mid-spin from being clipped

			}

		}

		cdTimer -= delta;

		ultCdT -= delta; // recharges on its own clock: bench ults stay ready when the seat flips

		if (ultCdT < 0.0)

			ultCdT = 0.0;

		insT -= delta; // bardic clock pauses with the world's soft pause too

		if (insT < 0.0)

			insT = 0.0;

		awaT -= delta; // awakening window ticks with the same soft-pause discipline

		if (_awaUp && awaT <= 0.0)

		{

			_awaUp = false;

			awaT = 0.0;

			GD.Print("[10thAge] (ult) Guardian Awakening settles: " + awaKills +

				" horde slain while awake - each kill healed one random ally +1 hp" +

				" (tally via Game.OnEnemyKilled)");

		}

		if (awaT < 0.0)

			awaT = 0.0;

		// Experimental Elixir clocks tick with the same soft-pause discipline as bardic/awakening

		elixHealT = System.Math.Max(0.0, elixHealT - delta);

		elixSwiftT = System.Math.Max(0.0, elixSwiftT - delta);

		elixHardT = System.Math.Max(0.0, elixHardT - delta);

		elixBoldT = System.Math.Max(0.0, elixBoldT - delta);

		elixPrecT = System.Math.Max(0.0, elixPrecT - delta);

		elixMagT = System.Math.Max(0.0, elixMagT - delta);

		oakT = System.Math.Max(0.0, oakT - delta); // Oakhollow clock ticks with the same soft-pause discipline

		// Assassination window ticks on the same soft-pause discipline (party-wide: every ally

		// carries its OWN assT, like the oak/bardic/elixir clocks)

		assT = System.Math.Max(0.0, assT - delta);

		// Bruno's "Axes Away!" spin: the clock decays with the same soft-pause discipline, and

		// the ANGLE advances every frame one FULL circle per cooldown window, so the quick-throw

		// card spins the orbit faster (attack speed is an "effect of the upgrades" the spin gains)

		orbitT = System.Math.Max(0.0, orbitT - delta);

		if (orbitT > 0.0)

		{

			if (!_spinAct)

			{

				// cast just armed: ONE shared roll for the WHOLE cast (the spin is one action

				// like every single-commit weapon), bite memos clear per circle, print memos clear

				// per cast. fx-feel8 (user 2026-11-02): the DEFAULT ATTACK STAYS ON while the ult runs - no
				// early return in the attack decision any more, and a volley already in flight is NOT voided at
				// cast: the crescents ride their OWN _cresFx slots so both arts stay on screen together

				_spinAct = true;

				Game spg = GetParent() as Game;

				_spinDmg = spg != null ? RollDamage(spg, weaponDamage) : weaponDamage;

				_spinBled = bleedR > 0;

				_spinMemo = new int[64];

				_spinCounted = new int[64];

				_spinBit = 0;

				_spinRev = 0;

				_spinA0 = orbitAng;

				// fx-feel8 (user 2026-11-02: "allow him to continue his default attack while his ultimate
				// is up, it feels like a bad limitation"): a volley already in flight is NO LONGER voided at
				// cast - it keeps flying and biting under the spin, on its own _cresFx slots

			}

			while (orbitAng - _spinA0 >= (double)(_spinRev + 1) * System.Math.PI * 2.0)

			{

				_spinRev++;

				for (int q2 = 0; q2 < _spinMemo.Length; q2++)

					_spinMemo[q2] = 0; // fresh bite window: every blade rakes every body once per circle

			}

			orbitAng += (double)delta * System.Math.PI * 2.0 / (float)EffCooldown(); // spin cadence: one circle per EffCooldown

			// user 2026-11-01 (ult bugged: blades visible + biting nothing): THE SWEEP IS THE

			// HITBOX - the blades bite at their OWN positions every frame while the clock runs,

			// one honest bite per body per blade per CIRCLE. What is ON SCREEN is what hurts,

			// like the boomerang fix the user approved (the old commit-only sample raked nothing,

			// and the missing braces left the pair parked on the ring all run).

			// fx art: spin blades are real sprites riding the same orbit clock and radius the

			// rakes read (arm 70 px = the rake lane's own spin radius, tangent rotation)

			if (_axeFx != null)

			{

				_fxT = 0.3f; // keeps the crescent-hide decay honest while the clock runs

				int bl = 2 + axeN; // rake cadence mirror: base pair + extra-axes ranks

				float spDisc = (float)(40.0 * (1.0 + 0.25 * (double)axeSize) * LeaderAreaMul()); // MASSIVE disc, size rank grows it | Bruno lead +15%

				Game spg2 = GetParent() as Game;

				Godot.Collections.Array<Godot.Node2D> live = spg2 != null ? spg2.GetEnemies() : null;

				for (int b2 = 0; b2 < 5; b2++)

				{

					if (b2 < bl)

					{

						float ba2 = (float)(orbitAng + (double)b2 * System.Math.PI * 2.0 / (double)bl);

						_axeFx[b2].GlobalPosition = GlobalPosition + new Vector2((float)System.Math.Cos(ba2), (float)System.Math.Sin(ba2)) * 70.0f;

						_axeFx[b2].Rotation = ba2 + (float)System.Math.PI * 0.5f; _axeFx[b2].Scale = new Vector2(spDisc / 40.0f, spDisc / 40.0f); // user 2026-11-02 (a): spin blade ART rides the SAME size factor the rake disc uses (spDisc / 40 px base)

						_axeFx[b2].Visible = true;

						if (live != null)

						{

							for (int ii = 0; ii < live.Count && ii < _spinMemo.Length; ii++)

							{

								if ((_spinMemo[ii] & (1 << b2)) != 0)

									continue; // this blade already bit this body this circle: one honest bite

								Godot.Node2D en = live[ii];

								Combatant ec = (Combatant)en;

								if (ec == null || ec.kind == "dead" || ec.hp <= 0.0f)

									continue; // killed-but-unswept bodies are not valid bites (chain discipline)

								if ((en.GlobalPosition - _axeFx[b2].GlobalPosition).Length() > spDisc)

									continue; // disc at the BLADE position: the hitbox the user asked for

								ec.TakeDamage(_spinDmg);

								if (_spinBled)

									ec.Bleed(1.0 * stat[StatTags.ATTACK_DAMAGE], 4.0); // same card-scaled stamp as the rake

								_spinMemo[ii] |= (1 << b2);

								if ((_spinCounted[ii] & 256) == 0)

								{

									_spinCounted[ii] |= 256;

									_spinBit++; // unique-body tally for the expiry print

								}

							}

						}

					}

					else

						_axeFx[b2].Visible = false;

				}

			}

		}

		else if (_spinAct)

		{

			// the spin just expired: the tally lands here (one print per cast, house style like a

			// flight landing) and the blades hide CLEANLY - the permanent pair the user saw is gone

			_spinAct = false;

			if (_spinBit > 0)

				GD.Print("[10thAge] (spin) Axes Away!: " + displayName + " massive blades rake " + _spinBit + " bodies");

			if (_axeFx != null)

				for (int b2 = 0; b2 < 5; b2++)

					_axeFx[b2].Visible = false;

		}

		// user 2026-11-01 + follow-up: BOOMERANG crescent hitboxes - the blade leaves from his OWN

		// hands, swings widest on the prey line at mid-flight, and RETURNS like a boomerang, so
		// hands, swings widest on the prey line at mid-flight, and RETURNS like a boomerang (art).
		// HIT MODEL (2026-11-02 follow-up, full-disc): the swept arm bites EVERYTHING inside reach,
		// one honest bite per blade via the memo below. Same shared roll; print lands when it returns.
		// hurts. Same shared roll/band disc; the print lands when the blade returns to the throw.

		if (_cresDur > 0.0f) // fx-feel8 (user): the volley TICKS while the spin runs - the default attack keeps biting under the ult

		{

			_cresT -= delta;

			double progF = 1.0 - (double)_cresT / CRESCENT_FLIGHT; // 0..1 along the OUT-and-BACK loop

			if (progF > 1.0)

				progF = 1.0;

			Game wpg = GetParent() as Game;

			Godot.Collections.Array<Godot.Node2D> live = wpg != null ? wpg.GetEnemies() : null;

			for (int cc = 0; cc < _cresNC; cc++)

			{

				double sgn = (cc % 2) == 0 ? 1.0 : -1.0; // arcs alternate top/bottom like the volley

				double pf = progF - cc * 0.1 / CRESCENT_FLIGHT; if (pf < 0.0) pf = 0.0; if (pf > 1.0) pf = 1.0; double angB = _cresA0 + sgn * System.Math.PI * (2.0 * pf - 1.0); // user 2026-11-02 (b): blade cc runs the SAME full loop ~0.1 s later (launch offset INTO progF, NOT per-blade clocks: one shared volley roll + memo invariant kept); unlaunched blades clamp to 0 = ride at the hands

				Godot.Vector2 biteP = GlobalPosition + new Godot.Vector2((float)System.Math.Cos(angB), (float)System.Math.Sin(angB)) * _cresR * (float)System.Math.Sin(System.Math.PI * pf); // sin bulge: radius 0 AT the hands at both ends, arcR on the prey line at mid-flight (boomerang out AND back)

				if (_cresFx != null && cc < 5)

				{

					_cresFx[cc].GlobalPosition = biteP;

					_cresFx[cc].Rotation = (float)angB + (float)(sgn * 1.5708) + (float)pf * 18.84f; _cresFx[cc].Scale = new Vector2((float)(1.0 + 0.25 * (double)axeSize) * (float)LeaderAreaMul(), (float)(1.0 + 0.25 * (double)axeSize) * (float)LeaderAreaMul()); // user 2026-11-02 (a): blade ART rides the SAME size factor the disc + band use ((1+0.25xsize)xareaMul) | (b): tumble per staggered clock

					_cresFx[cc].Visible = true; // fx-feel8: volley blades light on their OWN _cresFx slots (art)

				}

				if (live == null)

					continue; // no world handle: visual-only frame, the volley still completes

				for (int ii = 0; ii < live.Count && ii < _cresMemo.Length; ii++)

				{

					Godot.Node2D en = live[ii];

					Combatant ec = (Combatant)en;

					if (ec == null || ec.kind == "dead" || ec.hp <= 0.0f)

						continue;

					if ((_cresMemo[ii] & (1 << cc)) != 0)

						continue; // this blade already bit this body: one honest bite per crescent

					// user 2026-11-02 follow-up: FULL-DISC bite - max range 130 is a real LEASH, not a set
					// ring: the SWEPT ARM (its live angle angB) bites anything INSIDE the crescent reach,
					// one honest bite per blade via the same memo. Window = band depth at the body's own
					// radius + half a frame of sweep (0.18 rad slack, assistant pick) so the fast arm
					// never skips a body between tick frames.
					Godot.Vector2 bv = wpg.WrapDelta(GlobalPosition, en.GlobalPosition); // thrower -> body, wrap-short
					float rb = bv.Length();
					if (rb < 1.0f || System.Math.Abs(rb - (double)((float)System.Math.Sin(System.Math.PI * pf) * _cresR)) > _cresBand + (float)(System.Math.PI * _cresR * System.Math.Abs(System.Math.Cos(System.Math.PI * pf)) / CRESCENT_FLIGHT * delta) + 4.0f)
											continue; // fx-feel9 (user: "hitbox for Bruno's axes are a little bit off... not sure if it's doing damage based on where enemies were when it's thrown"): damage ALWAYS rode LIVE positions, the GEOMETRY missed - the old bite accepted ANY radius along the swept arm (a body beside Bruno bit while the blade art was mid-arc). Now the bite is a RING at the blade's CURRENT arc radius r_pf = _cresR*sin(pi*pf), the exact spot _cresFx draws at, with ring depth = band + half a frame of radial sweep + 4 px of art bleed: art == hitbox, and the full 130-px disc still gets covered because the blade spirals through it (assistant pick: depth slack)
					double ab = System.Math.Atan2((double)bv.Y, (double)bv.X); // body bearing
					double dang = ab - angB; // gap vs the arm's swept angle
					dang = dang - System.Math.Floor((dang + System.Math.PI) / (System.Math.PI * 2.0)) * System.Math.PI * 2.0; // wrap to [-pi, pi]
					if (dang < 0.0)
						dang = -dang;
					if (dang > _cresBand / rb + 0.18)
						continue; // outside the swept arm at this radius

					ec.TakeDamage(_cresDmg);

					if (_cresBled)

						ec.Bleed(1.0 * stat[StatTags.ATTACK_DAMAGE], 4.0); // same card-scaled stamp as the rake

					_cresMemo[ii] |= (1 << cc) | 256;

					_cresBit++;

				}

			}

			if (_cresT <= 0.0f)

			{

				// the catch bite past the thrower's own hands: the old release sample, once per blade

				if (live != null)

				{

					for (int cc = 0; cc < _cresNC; cc++)

						for (int ii = 0; ii < live.Count && ii < _cresMemo.Length; ii++)

						{

							Godot.Node2D en = live[ii];

							Combatant ec = (Combatant)en;

							if (ec == null || ec.kind == "dead" || ec.hp <= 0.0f)

								continue;

							if ((_cresMemo[ii] & (1 << cc)) != 0)

								continue;

							if ((en.GlobalPosition - GlobalPosition).Length() > _cresBand)

								continue;

							ec.TakeDamage(_cresDmg);

							if (_cresBled)

								ec.Bleed(1.0 * stat[StatTags.ATTACK_DAMAGE], 4.0);

							_cresMemo[ii] |= (1 << cc) | 256;

							_cresBit++;

						}

				}

				if (_cresBit > 0)

					GD.Print("[10thAge] (axe) " + displayName + " crescents rake " + _cresBit + " bodies");

				_cresDur = 0.0f; // volley done: the _fxT decay hides the blades off the throw

			}

		}

		// 2026-11-02 fx-feel6 (user: "the shields go through enemies and seem to bounce at their set
		// distance instead of hitting the enemy and bouncing. It also applies the damage immediately
		// instead of on hit (like it should do similar to Bruno's)"): the BAKED-chain + instant-commit
		// hurl is GONE. LIVE homing with ON-ARRIVAL damage: the disc chases the CURRENT victim's LIVE
		// position at SHIELD_HURL_SPEED px/s and bites the moment it REACHES it (Bruno-style: the
		// damage lands where the disc actually is), then picks the nearest FRESH live body within
		// SHIELD_HOP of that hit and chases IT; no next body (or spent budget / dead victim) = the
		// shield disappears exactly where it landed. Chain-lightning memo kept: no body is ever struck
		// twice in one throw, and a disc flying PAST anything still bites nothing - only bitten bodies
		// take damage.
		// fx-feel10: the flight walk runs OVER EVERY live slot, so several discs chase in the same
		// frame and a fast beat can never truncate one. Each slot keeps its own memo, budget, roll
		// and disc art; the behaviour below is unchanged, per slot.
		for (int sk = 0; sk < _shldT.Length; sk++)
		{
			if (_shldT[sk] == null)
				continue; // idle slot: nothing in the air to walk

			Combatant tc = (Combatant)_shldT[sk];
			if (tc == null || tc.kind == "dead" || tc.hp <= 0.0f)
			{
				// fx-feel8 (user 2026-11-02: "the shield vanishes early... the actual object never
				// reaches anything. I'm suspecting it's just a timeout"): no timeout exists in the
				// live-homing lane - the DROPOUT was mid-chase victim death ending the throw in
				// mid-air, and the pre-fix probe proved it: one Wash-led run logged far more
				// "(shield-diag) lost prey en route" losses than completed tallies. Now the disc
				// CHASES THE NEAREST FRESH body from its CURRENT spot (wrap-short, inside the same
				// flat BASE hop window the bounce picker uses (measured from the DISC), memo discipline kept) WITHOUT
				// spending a bounce - nothing was bitten, so nothing is spent. Only when no fresh
				// body answers does the chain end where the disc stands (user's own rule).
				Game pgt = GetParent() as Game;
				Godot.Node2D nxT = null;
				if (pgt != null)
				{
					Godot.Collections.Array<Godot.Node2D> foesT = pgt.GetEnemies();
						float hopRT = (float)SHIELD_HOP; // fx-feel9 (user: "not even try to bounce if they are too far away"): flat BASE hop, rank never stretches a leg (the long leg was what vanished mid-air); the retarget measures from the DISC (_shldP[sk]), NEVER Wash
					float nxTb = 1e9f;
					for (int i = 0; i < foesT.Count; i++)
					{
						Godot.Node2D en = foesT[i];
						Combatant ec = (Combatant)en;
						if (ec == null || ec.kind == "dead" || ec.hp <= 0.0f)
							continue; // killed-but-unswept bodies are not chases
						bool done = false;
						for (int h = 0; h < _shldHitNA[sk]; h++)
						{
							if (_shldHitM[sk][h] == en)
								done = true;
						}
						if (done)
							continue; // one honest bite per body per throw
						float dd = pgt.WrapDelta(_shldP[sk], en.GlobalPosition).Length();
						if (dd >= nxTb || dd > hopRT)
							continue; // a mid-air retarget measures from the DISC itself (dd anchors on _shldP[sk], never Wash)
						nxTb = dd;
						nxT = en;
					}
				}
				if (nxT != null)
				{
					if (_shldHitNA[sk] >= _shldHitM[sk].Length) // memo grows on demand: retargets never spend the bounce budget
					{
						Godot.Node2D[] bigger = new Godot.Node2D[_shldHitM[sk].Length + 4];
						for (int h = 0; h < _shldHitM[sk].Length; h++)
							bigger[h] = _shldHitM[sk][h];
						_shldHitM[sk] = bigger;
					}
					_shldHitM[sk][_shldHitNA[sk]++] = nxT;
					_shldT[sk] = nxT; // the disc keeps flying: the nearest fresh body takes the leg
				}
				else
				{
					if (_shldBitesA[sk] == 0)
						GD.Print("[10thAge] (shield) " + displayName + " lost prey mid-flight, nothing fresh in reach");
					_shldT[sk] = null; // nothing fresh to chase: the disc drops where it stands
				}
			}
			else
			{
				Game pgs = GetParent() as Game; // toroidal-safe wrap lanes, same as every aim lane
				if (pgs == null)
					_shldT[sk] = null; // no world handle: end the throw instead of hanging it
				else
				{
					_shldFlownA[sk] += (float)SHIELD_HURL_SPEED * delta; // px the disc may fly this frame
					Vector2 tgt = _shldT[sk].GlobalPosition; // LIVE position: the prey moves, the disc chases
					Vector2 dv = pgs.WrapDelta(_shldP[sk], tgt); // disc -> victim, wrap-short way
					float step = (float)SHIELD_HURL_SPEED * delta; // constant hurl speed (px/s)
					if (dv.Length() <= step)
					{
						_shldP[sk] = tgt; // land ON the body: the disc strikes it, never flies past it
						_shldBitesA[sk]++;
						((Combatant)_shldT[sk]).TakeDamage(_shldBitesA[sk] == 1 ? _shldRollA[sk] : _shldRollA[sk] * 0.75); // ONE roll for the whole throw: first bite full, every bounce a flat 75% (user 2026-11-01)
						// pick the next hop AT THIS INSTANT from live positions: nearest fresh body within SHIELD_HOP
						// of the hit that this throw has not spoken for yet (chain-lightning memo discipline)
						Godot.Node2D nxN = null;
						if (_shldLeftA[sk] > 0)
						{
							Godot.Collections.Array<Godot.Node2D> foes = pgs.GetEnemies();
							// user 2026-11-02 fx-feel9 follow-up: the chain is VICTIM-ANCHORED ALWAYS - every hop searches
							// within the FLAT BASE SHIELD_HOP of the body this throw has JUST BIT (dd2 below anchors on tgt,
							// the LIVE victim - never Wash). The old +96 px/rank reach growth is REMOVED: its long legs were
							// exactly the vanished-mid-air symptom (a leg onto prey that died en route); the rebound upgrade now
							// buys bounce COUNT only (assistant pick: base stays 160 px).
									float hopR = (float)SHIELD_HOP; // fx-feel9 (user): too-far prey is NEVER picked - flat BASE 160 px hop; the rebound upgrade buys COUNT only (assistant pick: base stays 160 px); hops measure from the just-bitten victim (dd2/tgt), NEVER Wash
							float nxDb = 1e9f;
							for (int i = 0; i < foes.Count; i++)
							{
								Godot.Node2D en = foes[i];
								Combatant ec = (Combatant)en;
								if (ec == null || ec.kind == "dead" || ec.hp <= 0.0f)
									continue; // killed-but-unswept bodies are never hops
								bool done = false;
								for (int h = 0; h < _shldHitNA[sk]; h++)
									if (_shldHitM[sk][h] == en)
									{ done = true; break; }
								if (done)
									continue; // one honest bite per body per throw
								float dd2 = pgs.WrapDelta(tgt, en.GlobalPosition).Length();
								if (dd2 >= nxDb || dd2 > hopR)
									continue; // a bounce hops ONLY within the flat BASE window of the body IT just bit (dd2 anchors on the LIVE victim, never Wash: too-far prey is NEVER tried)
								nxDb = dd2;
								nxN = en;
							}
						}
						if (nxN != null)
						{
								if (_shldHitNA[sk] >= _shldHitM[sk].Length) // fx-feel8: retargets push the memo PAST the per-throw
								{					// capacity (they never spend the bounce budget), so EVERY write now grows it
									Godot.Node2D[] bigger2 = new Godot.Node2D[_shldHitM[sk].Length + 4];
									for (int h = 0; h < _shldHitM[sk].Length; h++)
										bigger2[h] = _shldHitM[sk][h];
									_shldHitM[sk] = bigger2;
								}
							_shldHitM[sk][_shldHitNA[sk]++] = nxN; // the hop is spoken for the moment it is picked
							_shldT[sk] = nxN; // the disc takes the next leg off the body IT just bit (user: hit, then bounce to the next)
							_shldLeftA[sk]--;
						}
						else
						{
							// no fresh body to bounce to: the shield disappears where it landed (user)
							_shldT[sk] = null;
						}
					}
					else
						_shldP[sk] = pgs.WrapNearCamera(_shldP[sk] + dv.Normalized() * step); // home on the LIVE body between frames
					if (ShieldArt(sk) != null)
					{
						ShieldArt(sk).GlobalPosition = _shldP[sk];
						ShieldArt(sk).Rotation = (float)System.Math.Atan2((double)dv.Y, (double)dv.X) + _shldFlownA[sk] * 0.045f; // tumbles by distance flown, not by time
						ShieldArt(sk).Visible = true;
					}
				}
			}
			if (_shldT[sk] == null)
			{
				// the chain ended THIS frame (no next body / dead victim / no world): the disc drops at its
				// landing spot (user: "if there isn't a next enemy to bounce to, it disappears")
				if (ShieldArt(sk) != null)
					ShieldArt(sk).Visible = false;
				if (_shldBitesA[sk] > 1)
					GD.Print("[10thAge] (shield) " + displayName + " shield bounds " + (_shldBitesA[sk] - 1) + " more bodies");
			}

		}
		ultT -= delta;

		if (_grown && ultT <= 0.0)

		{

			ultT = 0.0;

			_grown = false;

			Scale = new Vector2(1.0f, 1.0f); // grown visual shrinks back when the buff expires

		}

		if (dsHold > 0.0f)

		{

			dsHold -= delta;

			if (dsHold <= 0.0f && !downed && !eliminated)

				_ds.Text = "";

		}

		if (_teleHold > 0.0f)

		{

			_teleHold -= delta;

			float a = _teleHold <= 0.0f ? 0.0f : _teleHold / 0.18f;

			foreach (Godot.ColorRect s in _teleParts)

				s.Modulate = new Color(a, a, a, a);

		}

		if (invuln > 0.0f)

		{

			_body.Modulate = new Color(0.8f, 1.0f, 0.85f); // pale green = post-revive shield

			if (_mini != null)

				_mini.Modulate = new Color(0.8f, 1.0f, 0.85f);

			if (_ds != null)

			{

				// locked rule made visible: the player can see WHY a hit just bounced

				_ds.Text = "immune " + ((int)(invuln * 10.0f)) / 10.0f + "s";

				_ds.Modulate = new Color(0.60f, 0.84f, 0.60f, 1.0f); // sage = immunity

			}

		}

		if (cdTimer > 0.0f)

			return;

		Vector2 p = GlobalPosition;

		Game pg = GetParent() as Game;

		if (pg == null)

			return;

		Godot.Collections.Array<Godot.Node2D> enemies = pg.GetEnemies();

		// the live arc: ranks open the maul FAN, the rapier keeps its narrow lane

		float arcEff = (float)System.Math.Min((double)arcHalf, System.Math.PI);

		// longbow kit: fires the Game's PARTY-SIDE shot pool (boss bullets live in the other

		// pool, which only ever bites the party). Kit dispatch keys on weaponKit, NOT on

		// arcHalf - arcHalf stays purely a melee property.

		bool shotKit = weaponKit == "shot"; // longbow kit: fires the Game's party-side shot pool

		bool cleaver = !shotKit && arcHalf > 0.5f; // maul kit: arc cleave, full damage per body (user 2026-10-23)

		// Spirit Guardians kit: a ring that pulses around the caster. Damage is NOT split by

		// count (user rule 2026-10-22) - every body inside takes the FULL hit, the exact

		// opposite of the maul's cleave split, and what makes the cleric an anti-swarm verb.

		// Bosses live in the SAME array (RegisterEnemy takes them; they only skip the horde cap),

		// so the ring can trade with a live boss, which is what lets the beat loop advance past

		// one. An empty ring burns nothing: the cooldown stays open, so a cleric standing over

		// empty clearing never stalls its own beat. The radius rides reach and the pulse rides

		// the weapon cooldown, so "wider ring" / "quicker pulse" reuse the AREA and ATTACK_SPEED tags.

		// M2's FOURTH verb is Yu's occult ritual: neither a bolt nor a ring but a small creature

		// that runs at the closest prey and detonates for a blast NO body shares (user rule 2026-10-22:

		// "the damage isn't reduced by number of enemies in the radius"). It lives in the Game's

		// party-side summon pool and its fuse is its whole lifetime, so the ranks reuse the same

		// plumbing every other member uses - cooldown = summon rate, damage = blast damage,

		// reach = the ritual (it widens the sight AND the blast), the MULTISTRIKE tag = multiattack rank.

		bool auraKit = weaponKit == "aura"; // Spirit Guardians kit: ring pulsing around the caster

		float reach = (float)EffRange();

		// the fourth verb is keyed on the kit name like every other weapon

		bool sumKit = weaponKit == "summon"; // Occultist ritual: fuse-driven familiars run at prey

		bool shieldKit = weaponKit == "shield"; // Wash's shield: short throw, then body-to-body bounces

		// Bruno's throwing axes (user 2026-10-31): the FIFTH verb, a boomerang crescent

		// (half-arc out + back over the same path, rakes everything in the blade band)

		// plus a spin lane ("Axes Away!") that orbits massive blades while the clock runs

		bool axeKit = weaponKit == "axe";

		// Brook's twin daggers (user 2026-10-31): the SIXTH verb, a same-line double jab

		bool daggerKit = weaponKit == "dagger";

		if (auraKit)

		{

			// the ring is a full-body sweep, so the lane/fan target search below is skipped

			int struck = 0;

			for (int i = 0; i < enemies.Count; i++)

			{

				Godot.Node2D n = enemies[i];

				Combatant nc = (Combatant)n;

				if (nc == null || nc.kind == "dead")

					continue;

				if ((n.GlobalPosition - p).Length() > reach)

					continue;

				nc.TakeDamage(RollDamage(pg, weaponDamage) * Strikes()); // FULL hit, not dmg / struck

				pg.TryChainLightning(n); // Baldrun seat: each caught body may loose a chain

				// guiding light: the pulse also MARKS what it caught (rank 0 = no mark),

				// so the whole party bites marked bodies harder until the window decays

				if (guidingLight > 0.0)

					nc.Vulnerable(guidingLight, 4.0);

				if (ringSlow > 0.0 && !(nc is Boss))

					nc.Slowed(ringSlow, 2.5); // weighty ring: same drag lane as draging maul (10/20/30%, 2.5 s, bosses immune)

				struck++;

			}

			if (struck <= 0)

				return; // nothing in the ring: the next pulse stays ready

			cdTimer = (float)EffCooldown();

			_atkFlash = 0.08f; // same commit pulse the longbow uses, never the red harm lane

			_auraPulse = 0.22f; // the ring flares so the tick reads without a HUD

			return;

		}

		// ---------- Yu's occult ritual: run the familiar pool, never a node per familiar ----------

		// The ritual is neither a bolt nor a ring, so it does its OWN prey scan: it sends a

		// creature after something well outside arm's reach, which the shared lane/fan search

		// below (gated by reach) is not for. Prey is the NEAREST living body, whatever way Yu

		// faces - like the bow and the maul, only a lane weapon respects walk facing.

		if (sumKit)

		{

			// the pause card's mode toggle rides the shared picker, same reach gate as before

			Godot.Node2D prey = PickPrey(pg, enemies, p, reach, true, false, false, 0.0f, 0.0f);

			// preyD answers the chosen body's distance; the empty-clearing guard below stands

			float preyD = PickPreyD;

			if (prey == null || preyD > reach)

				return; // nothing in sight: the ritual stays ready, an empty clearing never stalls a beat

			cdTimer = (float)EffCooldown();

			_atkFlash = 0.08f; // same commit pulse the longbow uses, never the red harm lane

			// multiattack rank: one more familiar per rank, spread in a shallow fan so a

			// triple summon never spawns three bodies in one spot. They are spawned NEAR Yu

			// (user dictation 2026-10-22), not on the prey it chased.

			int familiars = Strikes(); // MULTISTRIKE tag repeats the whole summon volley

			// ONE reach rank widens BOTH the ritual's sight AND its blast, so "wider burst"

			// rides the AREA tag like every other reach track. Blast damage is NOT split by whatever

			// stands in it (user rule 2026-10-22) - that no-split lives in the pool, see DetonateSummon.

			float blast = (float)(minionRadius * stat[StatTags.AREA]);

			// chase on the SHORT torus path: an absolute delta would take the 2400 px way round a seam

			Vector2 aim = pg.WrapDelta(p, prey.GlobalPosition);

			aim = aim.Length() > 0.01f ? aim.Normalized() : new Vector2(1.0f, 0.0f);

			for (int s = 0; s < familiars; s++)

			{

				float off = (s - (familiars - 1) * 0.5f) * 0.42f; // rad of spread per extra familiar

				float ca = (float)System.Math.Cos(off); // rotate the aim: Godot C# Vector2 has no .rotated()

				float sa = (float)System.Math.Sin(off);

				Vector2 dir = new Vector2(aim.X * ca - aim.Y * sa, aim.X * sa + aim.Y * ca);

				pg.SpawnSummon(p + dir * 26.0f, (float)RollDamage(pg, weaponDamage), minionFuse, blast, minionSpeed, luckR);

			}

			// the ritual IS the familiar: once the volley is loosed the beat must NOT

			// fall through into the shared lane/fan search below (user bugfix 2026-10-28:

			// the fall-through handed Yu a free rapier thrust at anything inside reach every

			// beat, reading as a damage aura around him; his minions ARE his damage)

			return;

		}

		// ---------- Wash's shield throw (user 2026-10-31): a SHORT hurl that bounces ----------

		// the shield hits the nearest prey inside its short reach, then bounces to ONE MORE fresh

		// body per bounce (base 1 + extra rebounds ranks): nearest living body NOT yet struck this

		// throw within SHIELD_HOP of the last hit, structural like the Baldrun chain hop but ungated

		// (it IS the weapon). ONE roll shared across every body - the shield is one object, one bite.

		// fx-feel10 (user 2026-11-03: "the shield despawns and he attacks again before it can
		// hit an enemy... The shield should be its own instance, doesn't matter if multiple
		// ones are bouncing around"): each hurl looses its OWN disc. The old lane kept ONE flight
		// and the next commit OVERWROTE it - and the user's attack-speed instinct was right: the
		// speed cards push cooldown BELOW the flight time, so the very next hurl despawned a
		// live disc in mid-air before it could land a bite. Every commit claims a FREE slot
		// (its own flight state + its own pooled disc sprite): several discs may share the air,
		// and no fast beat can truncate a live throw. Slot arrays grow +4 at a guard, the same
		// memo-growth discipline that settled the bounce-rank crash.
		if (shieldKit)
		{
			// the pause card's mode toggle rides the shared picker, same reach gate as before
			Godot.Node2D prey = PickPrey(pg, enemies, p, reach, false, false, false, 0.0f, 0.0f);
			float preyD = PickPreyD;
			if (prey == null || preyD > reach)
				return; // nothing in the throw: the beat stays ready, an empty clearing never stalls it
			int slot = -1;
			for (int q = 0; q < _shldT.Length; q++)
			{
				if (_shldT[q] == null)
				{
					slot = q;
					break;
				}
			}
			if (slot < 0)
			{
				GrowShieldSlots(); // every slot busy: grow all arrays +4, then claim a fresh one
				slot = _shldT.Length - 4;
			}
			int live = 0;
			for (int q = 0; q < _shldT.Length; q++)
			{
				if (_shldT[q] != null)
					live++;
			}
			if (live > _shldStackPrinted && live >= 1)
			{
				_shldStackPrinted = live; // fx-feel10 audit: a NEW stack height prints ONCE
				GD.Print("[10thAge] (shield) " + displayName + " now flies " + (live + 1) + " discs at once");
			}
			_shldThrows++; // fx-feel10 audit: committed throws this run (bites vs completions)
			// 2026-11-02 fx-feel6 (user: the throw "goes through enemies... bounces at their set distance...
			// applies the damage immediately instead of on hit (like it should do similar to Bruno's)"):
			// the commit now ONLY speaks for the prey and looses the disc - the _Process chase lands
			// every bite ON ARRIVAL and picks each hop at the hit itself. No baked polyline, no instant
			// damage at commit.
			_shldP[slot] = p; // flight state lives even without art: a greybox Wash still bites on arrival
			_shldT[slot] = prey; // the disc HOMES on live positions: no bake, no instant commit
			_shldRollA[slot] = RollDamage(pg, weaponDamage); // ONE crit/lifesteal roll for the whole throw (one object, one bite)
			_shldLeftA[slot] = shieldBounce; // bounce budget: extra bites after the first body (ranks grow it)
			_shldBitesA[slot] = 0; // nothing bitten yet: the FIRST bite lands when the disc arrives
			_shldFlownA[slot] = 0.0f; // distance clock for that disc's tumble
			// memo must fit THIS throw's budget: the old lazy allocation froze capacity at the FIRST
			// throw's rank, so every later "more rebounds" rank bought wrote past the end (the bench
			// caught the IndexOutOfRange swarm in the Wash-led smoke, and it is why upgraded shields
			// die mid-bounce in real play - user 2026-11-02: "doesn't bounce more even with upgrades")
			_shldHitM[slot] = new Godot.Node2D[1 + shieldBounce]; // reallocated every throw: ranks bought mid-run resize it live
			_shldHitNA[slot] = 1;
			_shldHitM[slot][0] = prey; // the prey is spoken for: it can never be a later hop this throw
			if (ShieldArt(slot) != null)
			{
				ShieldArt(slot).GlobalPosition = p; // starts in his hand: the throw visibly LEAVES the model
				ShieldArt(slot).Visible = true;
			}
			cdTimer = (float)EffCooldown();
			_atkFlash = 0.08f; // same commit pulse every kit uses, never the red harm lane
			return; // the throw owns its whole beat: damage lands when the disc lands on a body
		}

		// ---------- Bruno's "Axes Away!" spin (user 2026-10-31): orbiting massive blades ----------

		// (ult fix user 2026-11-01: the instant rake is GONE - the old sample only bit bodies

		// at the commit instant inside the arm's-length ring, and the hugging horde stands INSIDE

		// that ring, so the whirl raked practically nothing (zero (spin) prints across two casts

		// in the gate smokes). Damage now lives in the _Process tick above: the blades bite at

		// their OWN positions every frame while the clock runs (one shared roll per cast, one

		// honest bite per body per blade per circle, unique-body tally printed when the clock

		// expires). fx-feel8 (user 2026-11-02: "allow him to continue his default attack while his
		// ultimate is up, it feels like a bad limitation"): the spin NO LONGER owns the beat - the old
		// early return here froze the default attack for the whole cast and the cast voided any volley
		// in flight, which made the ult read as a dead stop; now the crescent commit cycles AND bites
		// on its own cd while the spin ticks, each lane on its own art slots (spin _axeFx, volley _cresFx).

		// ---------- Brook's twin daggers (user 2026-10-31): same-line double jab ----------

		// TWO daggers, 12 damage EACH (user 2026-11-02 bump from 7 to compensate for no piercing), thrust at the nearest prey ALONG ONE LINE one right after

		// the other (user: "attacks twice in the same direction, one after another with a very

		// small delay between them"): the beat lands the FIRST jab and parks cdTimer at TWIN_STAGGER

		// (0.1 s, user 2026-11-02 number) so the SECOND jab

		// lands on the next arrival down the stored aim - the stagger is what reads them as two

		// distinct jabs, and the cadence rides the attack-speed card exactly like every other

		// cooldown. Each jab stabs EVERY body inside a narrow corridor to the prey point (band

		// half-width is an assistant pick, it rides the AREA tag like other attack sizes); hits

		// stamp POISON, its OWN status pair mirroring burn/bleed (user kit: 1 dmg/s for 5 s,

		// dps scales with the damage card at the call site like the burn/bleed stamps; the fire

		// DoT lanes stay Stack-only). ONE crit/steal/multistrike roll SHARED by BOTH daggers

		// like the axe volley shares one roll across its crescents. Instant like the other melee

		// verbs: the thrust corridor is deterministic geometry, no shot-pool column to mirror.

		if (daggerKit)

		{

			// second dagger: DYNAMIC re-aim (it re-picks the nearest live prey at its own instant,
			// see the block below), then the beat is DONE (real cooldown)

			if (twinArm)

			{

				twinArm = false;

				_atkFlash = 0.08f; // the second jab flashes short like a draw, never the harm lane
				// user 2026-11-02 follow-up: the SECOND stab is DYNAMIC - it re-picks the nearest prey
				// AT THIS INSTANT and aims fresh at IT (the stored line almost always missed: bodies
				// move during the stagger). Only the ROLL stays shared across both daggers; with no
				// live prey left it falls back to the stored line so the beat still lands.
				Vector2 aim2 = twinAim; // stored line as the fallback
				// the second stab re-picks with the SAME mode so both daggers agree
				Godot.Node2D prey2 = PickPrey(pg, enemies, p, reach, true, true, false, 0.0f, 0.0f);
				double preyD2 = PickPreyD;
				if (prey2 != null)
				{
					Vector2 v2 = pg.WrapDelta(p, prey2.GlobalPosition);
					if (v2.Length() >= 0.01f)
						aim2 = v2 / v2.Length(); // aim where the prey IS now
				}

				float band2 = (float)(14.0 * stat[StatTags.AREA] * LeaderAreaMul()); // corridor half-width, rides attack size

				int hitN2 = 0;

				for (int i = 0; i < enemies.Count; i++)

				{

					Godot.Node2D n = enemies[i];

					Combatant nc = (Combatant)n;

					if (nc == null || nc.kind == "dead" || nc.hp <= 0.0f)

						continue;

					Vector2 v = pg.WrapDelta(p, n.GlobalPosition);

					double along = v.Dot(aim2);

					if (along < 0.0 || along > (double)reach)

						continue; // outside the thrust corridor (segment p -> p + aim * reach)

					if ((v - aim2 * (float)along).Length() > band2)

						continue; // clear of the blade lane

					nc.TakeDamage(twinDmg);

					nc.Poison(poisonDps * stat[StatTags.ATTACK_DAMAGE], poisonDur); // venom stamp: dps rides the damage card

					hitN2++;

				}

				if (_twinLen > 0.0f)

				PoseStabFx(p, aim2, _twinLen); // the second dagger rides the FRESH aim (user 2026-11-02: dynamic)

				GD.Print("[10thAge] (dagger) " + displayName + " second dagger stabs " + hitN2 + " bodies");

				cdTimer = (float)EffCooldown(); // the beat completes: full cooldown from here

				return;

			}

			// first dagger: pick the prey, share ONE roll across both jabs

			// the pause card's mode toggle rides the shared picker; hpGate keeps the

			// "dying body" rejection this kit already had

			Godot.Node2D prey = PickPrey(pg, enemies, p, reach, true, true, false, 0.0f, 0.0f);

			// preyD answers the chosen body's distance (double: the guard below casts reach)

			double preyD = PickPreyD;

			if (prey == null || preyD > (double)reach)

				return; // an empty clearing never burns the beat

			Vector2 aimv = pg.WrapDelta(p, prey.GlobalPosition);

			if (aimv.Length() < 0.01f)

				twinAim = new Vector2(1.0f, 0.0f); // prey standing on the blade: stab the facing line

			else

				twinAim = aimv / aimv.Length();

			twinDmg = RollDamage(pg, weaponDamage); // ONE crit/steal/multistrike roll shared by BOTH daggers

			twinArm = true;

			cdTimer = (float)TWIN_STAGGER; // the very small delay between the jabs

			float band = (float)(14.0 * stat[StatTags.AREA] * LeaderAreaMul()); // corridor half-width, rides attack size

			int hitN = 0;

			for (int i = 0; i < enemies.Count; i++)

			{

				Godot.Node2D n = enemies[i];

				Combatant nc = (Combatant)n;

				if (nc == null || nc.kind == "dead" || nc.hp <= 0.0f)

					continue;

				Vector2 v = pg.WrapDelta(p, n.GlobalPosition);

				double along = v.Dot(twinAim);

				if (along < 0.0 || along > (double)reach)

					continue;

				if ((v - twinAim * (float)along).Length() > band)

					continue;

				nc.TakeDamage(twinDmg);

				nc.Poison(poisonDps * stat[StatTags.ATTACK_DAMAGE], poisonDur); // venom stamp: dps rides the damage card

				hitN++;

			}

			float jabLen = reach; // user 2026-11-02: jabs ALWAYS render at FULL reach ("attacks seem to shrink based on how close an enemy is... they should always be attacking at maximum distance")

			_twinLen = jabLen; // the second dagger repeats the SAME line length

			PoseStabFx(p, twinAim, jabLen); // user 2026-11-01: the stab art rides the jab lane like the rest

			GD.Print("[10thAge] (dagger) " + displayName + " first dagger stabs " + hitN + " bodies");

			return; // the beat is mid-swing: the second dagger lands when cdTimer expires

		}



		// ---------- Bruno's throwing axes (user 2026-10-31): boomerang crescents ----------

		// ONE axe traces a crescent moon: it flies out along a half-arc centred on the thrower

		// and boomerangs BACK OVER THE SAME PATH, so every body within the blade band is raked

		// once per crescent (out leg + back leg = one pass of one blade). Base ONE crescent per

		// beat; the extra-axes card adds one more per rank, arcs ALTERNATING top/bottom of the

		// throw line (user: "one coming from the top, then bottom, then top..."). ONE roll

		// shared across the volley like every single-commit weapon. Bleed rank stamps its OWN

		// status pair (NOT the fire DoT: cinder bloom and the Drink burning-body bonus stay

		// Stack-only). Instant like the shield verb: the swept path is deterministic geometry,

		// so there is no shot-pool column to mirror. Print tallies unique bodies raked.

		if (axeKit)

		{

			// prey pick mirrors the ritual: nearest living body whatever way he faces (torus

			// delta: the crescent radius rides reach, an absolute delta would pop the loop

			// over a seam); an empty clearing never stalls the beat, like every kit

			// the pause card's mode toggle rides the shared picker, same reach gate as before

			Godot.Node2D prey = PickPrey(pg, enemies, p, reach, true, false, false, 0.0f, 0.0f);

			// preyD answers the chosen body's distance; the empty-clearing guard below stands

			float preyD = PickPreyD;

			if (prey == null || preyD > reach)

				return; // nothing in sight: the beat stays ready, an empty clearing never stalls it

			cdTimer = (float)EffCooldown();

			_atkFlash = 0.08f; // same commit pulse every kit uses, never the red harm lane

			double axDmg = RollDamage(pg, weaponDamage); // ONE crit/steal roll for the whole volley

			// crescent geometry: radius = the PREY distance clamped 40..reach, so the boomerang's

			// loop always passes the crowd it spotted (floor: the loop starts a step out of his

			// hands, not under his feet; ceiling: the axe is a range weapon). Band = blade disc:

			// 26 px base grown +25%/rank by the size card, raked on BOTH sides of the swept arc.

			// 33 samples per crescent: 32 along the half-arc (~13 px apart at the 130 px halved reach,

			// never wider than the grown disc) plus the RELEASE/CATCH point itself at the thrower, so

			// the return bite exists. Per-crescent memo = one strike per crescent; a body under

			// TWO crescents takes two honest bites (different blades), the print tallies uniques.

			// bleed: own pair mirroring burn, dps scaled by the damage card at the call site

			// like the burn stamp (dur 4.0 s assistant pick, 1.0 dps baseline like the fire DoT)

			int crescents = 1 + axeN; // base ONE axe; extra-axes card adds crescents alternating arcs

			float arcR = reach; // user 2026-11-02 (c): FIXED constant radius under EffRange() (reach IS EffRange at the beat top) - art, hitbox and telegraph all key off this ONE constant so every throw reads the same

			float band = (float)(26.0 * (1.0 + 0.25 * (double)axeSize) * LeaderAreaMul()); // blade disc + band depth: Bruno lead widens the crescent band too

			Vector2 aimv = pg.WrapDelta(p, prey.GlobalPosition); // the SHORT way: the arc rides reach

			float a0 = (float)System.Math.Atan2(aimv.Y, aimv.X); // throw line angle

			bool bled = bleedR > 0;

			// fx + hitbox (user 2026-11-01 + follow-up): the crescent is an OUT-and-BACK boomerang -

			// the blade leaves from his OWN hands (first frame AT the hands, NOT the crowd), swings

			// out to the prey line at mid-flight, and boomerangs back: the sin-bulged loop is the ART;
			// the HIT model (2026-11-02 follow-up) is the swept arm - full-disc bite inside reach.
			// Same ONE shared roll, band depth per radius, prints on landing. The
			// hitbox the user asked for. Same ONE shared roll, same band disc, prints on landing. The

			// clock owns these slots, so the flight freezes while spinning and resumes when

			// that clock expires. Same grown band disc; one bite per body per crescent.

			_cresNC = crescents;

			_cresR = arcR;

			_cresBand = band;

			_cresA0 = (double)a0;

			_cresDmg = axDmg;

			_cresBled = bled;

			_cresBit = 0;

			_cresDur = (float)CRESCENT_FLIGHT + (crescents - 1) * 0.1f; // user 2026-11-02 (b): total flight adds ~0.1 s launch stagger per blade so ALL axes are SEEN (ONE shared volley roll + memo bit 1<<cc invariant kept)

			_cresT = _cresDur; // staggered clock: counts down the WHOLE staggered volley (last blade lands when it hits 0)

			_cresMemo = new int[64]; // one bite per body per crescent (bit 256 = tallied)

			_fxT = _cresDur + 0.06f; // decay hides the blades the moment the LAST staggered blade lands

			if (_cresFx != null) // fx-feel8: throw frames ride through while the spin runs (each lane owns its slots now)

			{

				for (int c2 = 0; c2 < 5; c2++)

				{

					_cresFx[c2].Visible = c2 < crescents; // blades light up at the throw start

					if (c2 < crescents)

					{

						float sa2 = (c2 % 2) == 0 ? 1.0f : -1.0f;

						float ca2 = a0 - sa2 * 3.1416f + sa2 * 0.3f; // first sliver at the DEPARTURE side of the loop

						_cresFx[c2].GlobalPosition = p + new Vector2((float)System.Math.Cos(ca2), (float)System.Math.Sin(ca2)) * (arcR * 0.12f); // first frame AT the hands (sin bulge starts at 0); the tick block takes over

						_cresFx[c2].Rotation = ca2 + sa2 * 1.5708f; _cresFx[c2].Scale = new Vector2((float)(1.0 + 0.25 * (double)axeSize) * (float)LeaderAreaMul(), (float)(1.0 + 0.25 * (double)axeSize) * (float)LeaderAreaMul()); // user 2026-11-02 (a): throw start frames ride the size factor too

					}

				}

			}

			return; // the volley owns its whole beat: the flight block prints when it lands

		}

		// ONE shared picker for every kit (see PickPrey at the foot of the file): the

		// rapier LANES-only gate rides laneOnly + facing + arcEff, so a targeting mode the

		// arc cannot see still falls back to the closest body the lane actually covers.

		// Targeting gate is LANES-only (rapier): a lane weapon may only attack what its

		// facing arc covers (touching enemies always count). The maul Mosiah-aims: it

		// attacks the ABSOLUTE nearest body whatever way the body faces, and the ~150 deg

		// arc rides the swing (below), not the walk direction - so a Baldrun walking away

		// from the horde still turns its cleave to the nearest threat instead of whiffing

		// air. a shooter Mosiah-aims too: the bow picks the nearest body in sight

		// whatever way it faces, so the lane gate stays lane-only.

		Godot.Node2D best = PickPrey(pg, enemies, p, reach, false, false, isLeader && !cleaver && !shotKit, facing, arcEff);

		// bestD answers the chosen body's distance; the empty-clearing guard below stands

		float bestD = PickPreyD;

		if (best == null || bestD > reach)

			return;

		cdTimer = (float)EffCooldown();

		Vector2 to = best.GlobalPosition - p;

		float ang = (float)System.Math.Atan2(to.Y, to.X);

		if (!isLeader || cleaver)

			facing = ang; // auto-aim: the cleave swings at its prey (Mosiah-style),

						  // so the ~150 deg fan is centered on the swing toward the

						  // closest body; only lane weapons respect walk facing

		float dmg = (float)RollDamage(pg, weaponDamage);

		if (shotKit)

		{

			// Longbow: the commit fires the Game's PARTY-SIDE shot pool at the closest body in

			// sight. Damage is not split - every bolt that connects takes the full hit, and a

			// miss simply expires; the "piercing shot" card (user 2026-10-30: replaced the old

			// "farther shot" reach rank) stamps a per-bolt pierce budget so raked bolts keep flying

			// Bolts still fan tight: rank 1..3 reads as a wedge of chitin, not a stack.

			int bolts = Strikes(); // MULTISTRIKE tag fans one more bolt per rank

			for (int s = 0; s < bolts; s++)

			{

				float off = (s - (bolts - 1) * 0.5f) * 0.16f; // rad of fan per extra bolt

				float a2 = ang + off;

				Vector2 dir = new Vector2((float)System.Math.Cos(a2), (float)System.Math.Sin(a2));

				pg.SpawnShot(p + dir * 18.0f, dir * projSpeed, dmg, projLife, pierce, burnDur, burnDps * (float)stat[StatTags.ATTACK_DAMAGE], cinderRank > 0 ? dmg * 0.25 * (double)(cinderRank > 3 ? 3 : cinderRank) : 0.0); // firebolt: DoT + cinder-burst dmg ride the damage roll (dmg), not crits

			}

		}

		else if (cleaver)

		{

			// Maul: ARC CLEAVE, FULL damage to every body in the fan (user 2026-10-23:

			// the split made the maul weaker than every other verb once a few bodies

			// crowded the group - the fan now reads as a reaper, and the damage stat

			// dropped 30 -> 20 to buy the honesty back). The fan is still centered on

			// the swing toward the closest body (auto-aim above).

			for (int i = 0; i < enemies.Count; i++)

			{

				Godot.Node2D n = enemies[i];

				Combatant nc = (Combatant)n;

				if (nc == null || nc.kind == "dead")

					continue;

				Vector2 e = n.GlobalPosition - p;

				if (e.Length() <= reach &&

					System.Math.Abs(angleDiff((float)System.Math.Atan2(e.Y, e.X), facing)) <= arcEff)

					{

						nc.TakeDamage(dmg * Strikes()); // MULTISTRIKE: the whole swing repeats

						pg.TryChainLightning(n); // Baldrun seat: the hit may loose a chain

						// draging maul: rank-gated drag on caught bodies (2.5 s window, refresh-not-stack);

						// bosses immune at the applier, so only the horde gets the tax. See Combatant.Slowed.

						if (maulSlow > 0.0 && !(nc is Boss))

							nc.Slowed(maulSlow, 2.5);

						if (fireDps > 0.0)

							nc.Burn(fireDps, 5.0); // fire rune: 1/2/3 dps over 5 s; cinder stamp stays 0 so the burst keeps riding the firebolt ONLY

					}

			}

		}

		else

		{

			// Rapier: thrust + pierce. The point lands on the pick; ranks let up to

			// 1+pierce bodies share the lane and get skewered by the same lunge.

			// Mosiah seat (user 2026-10-25): while he owns the leader seat the lane skewers

			// one extra body (rank cap 4 total; base cap stays 3). pg handle = GetParent()

			int laneRank = pierce + pg.LeaderPierceBonus();

			if (laneRank > 4)

				laneRank = 4;

			Godot.Node2D[] lane = new Godot.Node2D[1 + laneRank];

			lane[0] = best;

			float laneHalf = (float)System.Math.Max((double)arcEff, 0.16);

			for (int s = 1; s < lane.Length; s++)

			{

				Godot.Node2D n = null;

				float nd = 1e9f;

				for (int i = 0; i < enemies.Count; i++)

				{

					Godot.Node2D c = enemies[i];

					Combatant nc = (Combatant)c;

					if (nc == null || nc.kind == "dead")

						continue;

					bool already = false;

					for (int q = 0; q < lane.Length; q++)

					{

						if (lane[q] != null && lane[q].GetInstanceId() == c.GetInstanceId())

						{

							already = true;

							break;

						}

					}

					if (already)

						continue;

					Vector2 e = c.GlobalPosition - p;

					float dd = e.Length();

					if (dd <= (float)EffRange() && dd < nd &&

						System.Math.Abs(angleDiff((float)System.Math.Atan2(e.Y, e.X), facing)) <= laneHalf)

					{

						nd = dd;

						n = c;

					}

				}

				if (n == null)

					break;

				lane[s] = n;

			}

			foreach (Godot.Node2D hit in lane)

			{

				// pierce widens the lane but the rays past the last body stay empty

				if (hit != null)

				{

					((Combatant)hit).TakeDamage(dmg * Strikes()); // MULTISTRIKE: the rapier re-thrusts

					pg.TryChainLightning(hit); // Baldrun seat: each skewered body may loose a chain

				}

			}

		}

		_atkFlash = shotKit ? 0.08f : 0.12f; // a shooter flashes a shorter draw, never red

		// the telegraph armature is a MELEE property: a bow shows its bolt in flight, not a

		// fan of spokes on the ground (that would read as a swing that never happened)

		if (!shotKit)

		{

			_telePivot.Rotation = facing;

			_telePivot.Scale = new Vector2((float)stat[StatTags.AREA], (float)stat[StatTags.AREA]);

			// rebuild the spokes for the LIVE arc: ranks open the maul fan, and the

			// rotations baked at load time only knew the base arcHalf

			if (_teleParts != null && _teleParts.Length > 1 && arcHalf <= 3.0f)

			{

				int nsp = _teleParts.Length;

				for (int i = 0; i < nsp; i++)

					_teleParts[i].Rotation = (float)(-arcEff + 2.0 * arcEff * i / (nsp - 1));

			}

			if (_meleeFx != null && !cleaver)

			{

				// user 2026-11-01: on the rapier the stab ART replaces the telegraph line (the

				// user saw line + sliver drawn at once). Zero the spoke alpha outright: the

				// decay only fades while _teleHold is live, so a dead hold alone would linger

				_teleHold = 0.0f;

				if (_teleParts != null)

					foreach (Godot.ColorRect s in _teleParts)

						s.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);

			}

			else

				_teleHold = 0.18f; // the maul keeps its ~150 deg fan spokes as the swing's telegraph

			// fx art (user 2026-11-01 fix): the art FILLS the swing - the maul arc is authored

			// centred on the thrower so it radiates from the model at the swing's true radius,

			// and the rapier sliver spans the full stab line anchored at the model. Scale is

			// recomputed each beat: reach cards and the size ranks stretch the art too

			if (_meleeFx != null)

			{

				float texW = 128.0f, texH = 128.0f;

				if (_meleeFx.Texture != null)

				{

					texW = (float)_meleeFx.Texture.GetWidth();

					texH = (float)_meleeFx.Texture.GetHeight();

				}

				if (cleaver)

				{

					_meleeFx.GlobalPosition = p; // arc radiates from his feet, not a floating sliver

					_meleeFx.Scale = new Vector2(reach * 2.0f / texW, reach * 2.0f / texH);

				}

				else

				{

					_meleeFx.GlobalPosition = p + new Vector2((float)System.Math.Cos(facing), (float)System.Math.Sin(facing)) * (reach * 0.5f); // centred on the stab corridor

					_meleeFx.Scale = new Vector2(reach / texW, (float)stat[StatTags.AREA]); // X spans the thrust, Y rides attack size

				}

				_meleeFx.Rotation = facing;

				_meleeFx.Visible = true;

				_fxT = 0.16f;

			}

		}

	}



	// user 2026-11-01: Brook's dagger ART - a stab sliver from the model down the jab corridor,

	// the same corridor the jab math bites (both jabs fire it; "" art keeps the greybox)

	private void PoseStabFx(Vector2 from, Vector2 aim, float len)

	{

		if (_meleeFx == null || len <= 0.0f)

			return; // greybox stays greybox: nothing painted, nothing decayed

		float texW = 112.0f;

		if (_meleeFx.Texture != null)

			texW = (float)_meleeFx.Texture.GetWidth();

		_meleeFx.GlobalPosition = from + aim * (len * 0.5f); // centred on the stab corridor

		_meleeFx.Rotation = (float)System.Math.Atan2((double)aim.Y, (double)aim.X);

		_meleeFx.Scale = new Vector2(len / texW, 1.0f); // X spans the jab, Y keeps the authored height

		_meleeFx.Visible = true;

		_fxT = 0.16f; // same commit-flash decay clock every kit uses

	}

	private static double angleDiff(double a, double b)

	{

		double t = a - b;

		while (t > System.Math.PI)

			t -= System.Math.PI * 2.0;

		while (t < -System.Math.PI)

			t += System.Math.PI * 2.0;

		return t;

	}



	// fx-feel10: pooled disc art - one Sprite2D per throw slot, built on first use so a greybox
	// Wash (no kit art) simply gets null and the flight still lands its bites.
	private Godot.Sprite2D ShieldArt(int slot)
	{
		if (_shldArt == null || slot < 0 || slot >= _shldArt.Length)
			return null;
		if (_shldArt[slot] == null)
		{
			if (_shldTex == null)
				return null; // no kit texture: flight state only, nothing to draw
			Godot.Sprite2D sx = new Godot.Sprite2D();
			sx.Texture = _shldTex;
			sx.Set("texture_filter", 0);
			sx.Visible = false;
			AddChild(sx); // same parenting as the old single disc: global placement tracks the party
			_shldArt[slot] = sx;
		}
		return _shldArt[slot];
	}

	// fx-feel10: every slot busy (rare: cooldown far below flight time): grow all parallel arrays
	// +4 AT THIS GUARD, copying live state across, so no in-flight disc is ever dropped by growth.
	private void GrowShieldSlots()
	{
		int n = _shldT.Length + 4;
		Godot.Node2D[] t = new Godot.Node2D[n];
		Godot.Vector2[] pv = new Godot.Vector2[n];
		Godot.Node2D[][] hm = new Godot.Node2D[n][];
		int[] hn = new int[n];
		int[] la = new int[n];
		int[] bi = new int[n];
		double[] rl = new double[n];
		float[] fl = new float[n];
		Godot.Sprite2D[] ar = new Godot.Sprite2D[n];
		for (int q = 0; q < _shldT.Length; q++)
		{
			t[q] = _shldT[q];
			pv[q] = _shldP[q];
			hm[q] = _shldHitM[q];
			hn[q] = _shldHitNA[q];
			la[q] = _shldLeftA[q];
			bi[q] = _shldBitesA[q];
			rl[q] = _shldRollA[q];
			fl[q] = _shldFlownA[q];
			ar[q] = _shldArt[q];
		}
		_shldT = t;
		_shldP = pv;
		_shldHitM = hm;
		_shldHitNA = hn;
		_shldLeftA = la;
		_shldBitesA = bi;
		_shldRollA = rl;
		_shldFlownA = fl;
		_shldArt = ar;
	}

	// fx-feel10: the campfire beat sweeps in-flight bullets; shield discs are per-member STATE, so
	// the camp ends every live throw here too - nothing keeps flying over the rest beat.
	public void EndAllThrows()
	{
		for (int sk = 0; sk < _shldT.Length; sk++)
		{
			if (_shldT[sk] == null)
				continue;
			_shldT[sk] = null;
			_shldBitesA[sk] = 0; // a swept throw never reports bounds: it never finished
			if (ShieldArt(sk) != null)
				ShieldArt(sk).Visible = false;
		}
	}
	// pause-card targeting: every kit asks ONE picker instead of its own nearest loop.
	// Modes: 0 closest (the default), 1 furthest, 2 highest HP, 3 lowest HP, 4 boss.
	// A mode with no prey in reach falls back to the closest body in reach - the user's
	// 'boss selected but out of range' case; ties prefer the nearer body.
	public int targetMode = 0;
	public float PickPreyD = 1e9f;

	// The pause card pushes the same mode any number of times: only a CHANGE prints.
	public void SetTargetMode(int m)
	{
		if (m == targetMode)
			return;
		targetMode = m;
		GD.Print("[10thAge] " + displayName + " target logic: " + m);
	}

	// The shared prey pick. Candidates are reach-gated INSIDE so 'no prey in this mode'
	// can fall back; PickPreyD answers the chosen body's distance (1e9f when nothing was
	// found). Flags keep each kit's old rules: wrap = torus delta (the seam-safe pair the
	// melee kits rode), hpGate = also reject dying bodies, laneOnly = the facing-arc gate
	// for lane weapons (bodies under 40 always count, exactly as the loop had it).
	public Godot.Node2D PickPrey(Game pg, Godot.Collections.Array<Godot.Node2D> enemies,
		Vector2 p, float reach, bool wrap, bool hpGate, bool laneOnly, float facing, float arcEff)
	{
		Godot.Node2D got = _PickFor(targetMode, pg, enemies, p, reach, wrap, hpGate, laneOnly, facing, arcEff);
		if (got == null && targetMode != 0)
			got = _PickFor(0, pg, enemies, p, reach, wrap, hpGate, laneOnly, facing, arcEff);
		return got;
	}

	// One mode's pick; mode 4 only scores bosses, so a horde with no live boss in reach
	// leaves got null and PickPrey falls back to the closest.
	private Godot.Node2D _PickFor(int mode, Game pg, Godot.Collections.Array<Godot.Node2D> enemies,
		Vector2 p, float reach, bool wrap, bool hpGate, bool laneOnly, float facing, float arcEff)
	{
		Godot.Node2D got = null;
		float gotD = 1e9f;
		float gotHP = 0.0f;
		for (int i = 0; i < enemies.Count; i++)
		{
			Godot.Node2D n = enemies[i];
			Combatant nc = (Combatant)n;
			if (nc == null || nc.kind == "dead")
				continue;
			if (hpGate && nc.hp <= 0.0f)
				continue;
			if (mode == 4 && !(nc is Boss))
				continue;
			Vector2 v = wrap ? pg.WrapDelta(p, n.GlobalPosition) : n.GlobalPosition - p;
			float dd = v.Length();
			if (dd > reach)
				continue;
			if (laneOnly && dd >= 40.0f && System.Math.Abs(angleDiff((float)System.Math.Atan2(v.Y, v.X), facing)) > arcEff)
				continue;
			if (mode == 2)
			{
				if (got == null || nc.hp > gotHP || (nc.hp == gotHP && dd < gotD))
				{
					gotHP = nc.hp;
					gotD = dd;
					got = n;
				}
				continue;
			}
			if (mode == 3)
			{
				if (got == null || nc.hp < gotHP || (nc.hp == gotHP && dd < gotD))
				{
					gotHP = nc.hp;
					gotD = dd;
					got = n;
				}
				continue;
			}
			if (mode == 1)
			{
				if (got == null || dd > gotD)
				{
					gotD = dd;
					got = n;
				}
				continue;
			}
			if (got == null || dd < gotD)
			{
				gotD = dd;
				got = n;
			}
		}
		PickPreyD = (got == null) ? 1e9f : gotD;
		return got;
	}

}
