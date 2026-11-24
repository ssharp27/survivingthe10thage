using Godot;

/// <summary>
/// M1 greybox root orchestrator for "Surviving the 10th Age".
/// Owns the stage flow (horde -> mini-boss -> campfire -> horde -> boss), the squad
/// state machine (per-member HP, down / d20 death saves / stand-still revive / eliminated-until-campfire
/// / TPK), squad XP with the targeted level-up picker, and the pooled gems + boss bullets.
/// All visuals are greybox ColorRects; all numbers live here so data can be swapped cheaply.
/// </summary>
public partial class Game : Godot.Node2D
{
	// ---------- looping world / squad geometry ----------
	// user bugfix 2026-10-20: goblin escort staging collapsed when a PC spawned near an
	// arena edge. The stage has NO walls now: the world LOOPS (torus). The wrap period
	// is NOT hardcoded: the fullscreen window (canvas_items stretch, aspect expand) can
	// see far more world than the 1920x1080 base viewport at DPI-scaled sizes, and the
	// frame is MEASURED per run (human bug 2026-10-20 part 2: the pinned floor covered
	// only ~1/4 of the frame). period = 2x the measured half-extent + 480 px slack, so
	// every entity's fold box strictly contains the frame and seam folds can never pop
	// in view, on any window.
	Vector2 _visHalf = new Vector2(960.0f, 540.0f); // overwritten by MeasureWindow()
	double _wrapW = 2400.0;
	double _wrapH = 1800.0;
	public readonly float MEMBER_RADIUS = 16.0f;
	public const float UNDEAD_RADIUS = 12.0f; // risen bodies (2026-11-17): the horde's shot-core lane uses this like MEMBER_RADIUS
	public const double RING_RADIUS = 64.0; // bodyguard ring; sized so REVIVE_RANGE
	// wraps a ring slot: a downed bodyguard is in arm's reach of the rescue
	// ---------- ring seats (one distinct angle per roster seat) ----------
	// Seats are keyed by memberIndex so a recruit always returns to the same place at the
	// table. M1's hand-placement is kept verbatim (Asper seat 2, Mosiah seat 3), so the
	// existing squad reads IDENTICALLY; what changed is that seats are now a table instead
	// of math that folded index 1 onto the same seat as index 3 - a fourth PC used to
	// weld straight onto Mosiah. Distinct seats hold up to the 5-PC roster.
	// RETIRED 2026-10-30: the follow loop now divides 2pi by the count of LIVE movers, so the
	// leader's own seat is no longer a pocket of air in the orbit (the missing-corner box the
	// user saw). Table kept for the audit trail:
	static readonly int[] RING_SLOT = { 0, 3, 2, 4, 1, 5 };
	// ---------- player revive (leader stands still near a body) ----------
	// 56 (was 30): widened so a bodyguard slot and the rescue share a spot
	readonly double REVIVE_RANGE = 56.0;
	readonly double REVIVE_CHANNEL = 3.0;
	readonly double REVIVE_INVULN = 2.0; // post-revive immunity; the body is also
	// shielded while the channel is actively progressing (reviveShield)

	// ---------- death saves (user-locked rules) ----------
	// Downed allies roll a d20 every 4 s: nat 20 = revived at 25 % HP, 11-19 = a
	// success, 3 successes = STABLE (rolls stop; a hit then slays the body
	// outright unless the player revives it), 10 or below = a failure and 3
	// failures = out until the next campfire. Damage on a downed (unstable) body
	// is one failure, kept 2 s apart so a swarm can't insta-finish anyone.
	readonly double DS_INTERVAL = 4.0; // d20 roll cadence while downed
	readonly int DS_FAIL_ON = 10; // roll <= this fails
	readonly int DS_REVIVE_ON = 20; // nat 20 revives at 25 % HP
	readonly int DS_FAIL_LIMIT = 3;
	readonly int DS_STABLE_NEED = 3; // successes before the rolls stop
	readonly double DS_DAMAGE_FAIL_BUFFER = 2.0;
	// down grace (user 2026-10-24): the 4 s right after a body falls, enemy hits can
	// never consume a death-save fail - a PC downed inside a swarm gets breathing
	// room. Rides the dsDamageBuffer lane (set to THIS at the down sites), fully
	// silent: no chip text, no flash, no HUD (user asked for no display at all)
	readonly double DS_DOWN_GRACE = 4.0;
	// grimoire chip palette for the ON-BODY death-save readout (the die lives on the
	// body, not in the log): failed saves run blood, successes/stable run amber,
	// every revive/at-camp beat runs sage, eliminated bodies run grey.
	readonly Color CHIP_BLOOD = new Color(0.88f, 0.42f, 0.42f, 1.0f);
	readonly Color CHIP_AMBER = new Color(0.95f, 0.75f, 0.35f, 1.0f);
	readonly Color CHIP_SAGE = new Color(0.60f, 0.84f, 0.60f, 1.0f);
	readonly Color CHIP_GREY = new Color(0.70f, 0.70f, 0.70f, 1.0f);

	// ---------- stage beat loop (M2 schedule; user re-locked 2026-11-03 to ~20 min) ----------
	// The stage is a BEAT TABLE on the run clock, not a fixed state chain. Times are
	// RUN seconds, so a picker / join / camp pause never burns a beat (pass-22 rule).
	//   30s ally I | 60s tier-2 gate | 180s mini-boss I | 240s ally II | 360s mid-boss I
	//   420s ally III | 540s tier-3 gate | 660s mini-boss II | 780s ally IV | 840s tier-4
	//   gate | 960s mid-boss II (the second boss1 body) | 1200s FINAL boss | 1260s WRAP-UP
	// The 2026-11-03 lock stretches the whole stage from 420 s to 1260 s (user: "runs are
	// very difficult now without the right upgrades ... make it around 20 minutes"): the party
	// gets ~20 minutes of build-up runway, then the Enter-the-Gungeon-style final sits on its
	// own beat with a few minutes to play out. The earlier 420 s lock (and the 2026-10-23 re-locks
	// before it) survive only as history here - the table below is the truth.
	// This is the gameplay loop "until times and scaling are changed around" (user rule),
	// so every number here is data the user can move without touching the sequencer.
	readonly double[] BEAT_T = { 30.0, 60.0, 180.0, 240.0, 360.0, 420.0, 540.0, 660.0, 780.0, 840.0, 960.0, 1200.0, 1260.0 };
	// 0 = ally join beat, 1 = horde tier gate, 2 = mini-boss (its kill opens the camp),
	// 3 = mid boss (its kill ALSO opens the camp now - user 2026-11-03 wants a campfire after
	// EVERY boss), 4 = final boss (its kill ALSO opens the camp; the run rolls on),
	// 5 = WRAP-UP (the run clock lands on it with a clear arena -> VICTORY; a live final
	// holds the row back until it is resolved)
	readonly int[] BEAT_KIND = { 0, 1, 2, 0, 3, 0, 1, 2, 0, 1, 3, 4, 5 };
	// the horde tier the stage runs at once that beat lands (0 = leave the tier alone).
	// This lock puts the tier gates on their OWN rows, so a tier step never hides inside an
	// ally beat again: tier 2 at 60s, tier 3 at 540s, tier 4 at 840s.
	readonly int[] BEAT_TIER = { 0, 2, 0, 0, 0, 0, 3, 0, 0, 4, 0, 0, 0 };
	// boss beats name the Boss node they engage, so a beat is data and the scene owns the
	// body (five of them now: mini1 / boss1 / mini2 / boss2 / final, parked hidden until
	// their beat fires)
	readonly string[] BEAT_BOSS = { "", "", "mini1", "", "boss1", "", "", "mini2", "", "", "boss2", "final", "" };
	// ally beats recruit whatever the per-run JOIN LOTTERY drew for that slot (user 2026-10-23:
	// runs are dynamic). The lottery is built at boot from the stage-weighted table below,
	// drawn without replacement and excluding the run's own leader. A -1 slot (pool
	// exhausted) is a logged no-op, so the loop stays honest while the roster grows.
	int _allyBeatIdx = 0;                 // which ally beat has fired (0 = none)
	int _nextBeat = 0;                    // next row of the beat table to fire
	// the density curve ramps inside the CURRENT beat leg: len = next beat - last beat
	// (replaces M1's FIRST_HALF / SECOND_HALF halves, which the beat table subsumes)
	// ---------- field recruit (user-approved 2026-10-20) ----------
	// RECRUIT_AT / TIER2_AT / BOSS_AT are gone: the beat table above owns every stage gate.
	// A not-yet-party PC drops into the world with HELP! and an arrow from the Leader; ESCORT_COUNT
	// escorts of the CURRENT horde tier slowly close in. Clearing them all joins the PC at
	// whatever HP he survived (floored - see JoinRecruit). The join banner carries the
	// "X JOINS THE BATTLE!" art slot and waits for a click (human gate, no timeout).
	const int ESCORT_COUNT = 6;
	// human-playtest fix (2026-10-20): the beat must read as a rescue the party rides
	// toward, not a pop-in next to the Leader - the stranded PC lands PAST the camera
	// edge, the escort goblins open wide of the PC, and they shamble in.
	readonly double RECRUIT_DIST = 300.0; // floor distance from the camera (see SpawnFieldRecruit)
	const double RECRUIT_MARGIN = 90.0;   // px past the visible rect the stranded PC lands at
	// human pass 11 (2026-10-20): even wider and even slower - the beat should read as a
	// hunt - and escort goblins now CLOSE ON the PC and chip him on contact, so dawdling
	// on the rescue trades away the HP he joins with (floored, see JoinRecruit)
	readonly double ESCORT_RING = 520.0;  // escort bodies start this wide of the stranded PC
	readonly double ESCORT_CRAWL = 0.12;  // creep factor on escort speed: pressure, not a chase
	const float RECRUIT_JOIN_FLOOR = 0.25f; // floored fraction of max HP the PC joins at
	// user 2026-11-03: recruitment escorts are tier-1 goblins on EVERY ally beat,
	// regardless of how far the run has advanced. They still scale like the horde does.
	double _hordeTier = 1.0;              // 1 goblins | 2 wolves+hobgoblins | 3/4 M2 gates (Director only)
	// stage name/number shown on the ui/ cards AND the HUD (user 2026-10-23: Tanurial Forest
	// is "stage 1" / "level 1" - many more stages are planned, so every display site reads
	// the pair instead of a baked string)
	// ---------- STAGE REGISTRY (one entry per stage the launch can open) ----------
	// user 2026-11-08: the stage is now SELECTABLE (Character Select > Stage Select), so the
	// pair every display site reads (HUD, camp card, summary, pause card) became runtime data.
	// A new stage = a row here + its JOIN_W row (row = registry index) +, later, its own beat
	// rows. Entry 0 is stage 1: a seat that asks for nothing keeps the shipped run untouched.
	// STAGE_NUMBER 0 marks a PSEUDO-stage: the Enemy Test Room is a practice yard, so it must
	// never read as "stage 2"; every display site reads the label, and only a real stage prints
	// "(stage N)".
	// STAGE_TEST flips the ENEMY TEST ROOM door the M1_TEST_ROOM=1 bench valve already boots, so
	// hand-picking the room from an ordinary launch lands on the very same lane.
	// STAGE_LOCKED keeps the card's "(locked)" tag honest when a future stage parks a slot.
	private static readonly string[] STAGE_ID = { "stage1", "testroom" };
	private static readonly string[] STAGE_LABEL = { "Tanurial Forest", "Enemy Test Room" };
	private static readonly int[] STAGE_NUMBER = { 1, 0 };      // 0 = pseudo-stage (see above)
	private static readonly bool[] STAGE_TEST = { false, true };
	private static readonly bool[] STAGE_LOCKED = { false, false };
	// one-line identity per plate: what standing in the stage means. The full readout (schedule,
	// density, recruits, xp) is composed off the LIVE data in ShowStageSheet so the card cannot
	// lie about the run; these are the short tags the plates carry.
	private static readonly string[] STAGE_BLURB = {
		"The shipped stage: the full beat schedule - ally rescues, horde tier gates, the mini / mid / final bosses and a campfire between the battles.",
		"The practice yard: one body every 15-20 s cycling the whole stage-1 roster, and the training ring at the squad start (+1 xp/sec while you stand in it). No ally joins, no bosses, no camp, no victory - down / revive / TPK and the gems stay exactly as shipped." };
	private int _stage = 0;                    // which registry entry this run plays (0 = stage 1: the bench default)
	// display helpers: the label, the number and the HUD badge for the stage this run plays
	private string StageName()
	{
		return STAGE_LABEL[_stage];
	}
	private int StageNumber()
	{
		return STAGE_NUMBER[_stage];
	}
	private string StageBadge() // "Tanurial Forest (stage 1)" | "Enemy Test Room (test room)"
	{
		int n = StageNumber();
		if (n <= 0)
			{ return STAGE_LABEL[_stage] + " (test room)"; }
		return STAGE_LABEL[_stage] + " (stage " + n + ")";
	}
	// ---------- roster join lottery (user 2026-10-23: dynamic runs) ----------
	// WHICH roster PCs can be recruited this run and in WHICH order: one weighted,
	// without-replacement draw per ally beat, taken at boot. The run's own leader never
	// rolls onto its own pool (it CAN show up on runs where it is not the leader).
	// Row = registry index (BuildJoinLottery reads _stage): columns are memberIndex order
	// (0 Baldrun | 1 Mosiah | 2 Asper | 3 Alvora | 4 Yu | 5 Stack | 6 Wash | 7 Bruno | 8 Brook | 9 Keeko | 10 Hapi | 11 Athios | 12 Hew).
	// Weight 0 = cannot appear on that stage. Stage 1 per the user: Baldrun + Mosiah more likely
	// than the rest. The Enemy Test Room row is ALL ZERO on purpose: the room runs no ally beats,
	// so the exhausted pool is a logged no-op and nothing can recruit there.
	// Sanity numbers - the user will add rows/weights per future stage.
	// the new-style initializer: Godot's C# build trips CS0623 on nested old-style braces
	private static readonly int[][] JOIN_W = { new int[] { 3, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, new int[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 } };   // row 0 = stage 1: Baldrun + Mosiah heavy, the rest (Keeko 2026-11-14, Hapi 2026-11-17, Athios 2026-11-19, Hew 2026-11-22) at parity | row 1 = the test room: no recruits
	private int[] _joinOrder = new int[0]; // ally slot k -> memberIndex drawn for it
	private Godot.Collections.Array<int> _hpOrder = new(); // HUD strip slot -> memberIndex: the join order (see BuildJoinLottery)
	private Godot.RandomNumberGenerator _joinRng; // UNSEEDED on purpose: a fresh process re-rolls
	// CAMPFIRE_LEN is gone: the campfire beat is player-driven (the ui/ Campfire
	// screen emits rest_chosen). This is only the safety valve so an idle/AFK
	// player (or a headless smoke run) still advances after this much waiting.
	readonly double CAMPFIRE_WAIT_TIMEOUT = 25.0;

	// ---------- death-state tester (user ask) ----------
	// A big red instant-kill circle spawns 10 s into the run: attack-proof, it
	// kills the first body it touches and despawns right after. Test tool only.
	bool _reaperSpawned = false;
	bool _reaperEnabled = false; // TEST BENCH default OFF; M1_REAPER=1 re-enables it

	// ---------- ENEMY TEST ROOM (user 2026-11-07: "let's build the enemy test room ... spawn an
	// enemy every 15-20 seconds ... rotate through every enemy for stage 1 at the moment ... also
	// add a circle section that gives constant XP at 1 per second that we can sit in to level up
	// so I'm not testing only with level 1 characters") ----------
	// M1_TEST_ROOM=1 boots the room INSIDE this scene: Director.cs owns the rotation lane, the
	// training ring below is the XP faucet. LIVE since this pass: the stage-select card offers the
	// room as a plate, so a human picks it from an ordinary launch and lands on the SAME lane a
	// bench seat reaches by prefixing M1_TEST_ROOM=1 (either switch sets _testRoom; the card also
	// arms the ring's floor read after the pick, see CloseStage). Only the STAGE SCHEDULE is held
	// back (no ally joins, bosses, camp, victory, horde events or rest scaling): death saves, revive,
	// TPK and gem drops stay exactly as the shipped game routes them.
	bool _testRoom = false;     // the room switch: M1_TEST_ROOM at the env rail, OR the stage card's pick
	// The ring sits on the squad's start point (Main.tscn parks the roster at (0,0)), so a pilot
	// bench stands inside it with zero steering and the faucet prints on their own.
	Vector2 _testRingAt = new Vector2(0.0f, 0.0f);
	float _testRingR = 240.0f; // assistant pick: outside the party's leash, walkable out of
	double _testXpPerSec = 1.0; // USER rate: "constant XP at 1 per second"
	double _testXpAccru = 0.0;  // sub-second drip; whole points feed the REAL AddXp -> picker lane
	Godot.Polygon2D _testRingDisc = null; // sage fill: the ring READS as an area, not a lone outline
	Godot.Line2D _testRingRim = null;     // rim so the edge sits exactly where the hit test does
	Godot.Label _testRingTag = null;      // tag over the center: what standing in it does

	// ---------- xp / level ----------
	double _xp = 0.0;
	double _xpTotal = 0.0;
	int _level = 1;
	double _xpNext = 6.0;
	// (human pickers never time out now - user-locked; this constant is intentionally
	// unused in human play, kept as the tuning knob in case the pilot gate ever needs it)
	readonly double PICKER_AUTO_TIMEOUT = 6.0;

	// ---------- level-up picker: one open track per member IN PLAY + 1 WILD, 3 ranks then cap ----------
	// Human-test: a fixed 3-option card felt the same every level - roll variety instead.
	// user RE-LOCK (2026-10-23): the card shows one still-open track per member in play
	// PLUS one fully-wild slot (a random open track from ANY member, never a repeat) -
	// M1's wildcard is back by user request. With six PCs that's up to seven keys.
	// A member whose every track is capped simply drops its slot, so the card never
	// shows a pick that cannot be taken.
	// Offense only - never HP, speed or healing (those live in campfires and revives); the
	// kit-native ranks (pierce, multishot) ride the same cap. Ranks cap at 3 per DESIGN; the
	// 4th rank would be the evolution (Stormcaller / Viperkiss), still stubbed for M2.
	const int PICK_RANK_CAP = 3;
	const int PICK_SLOTS = 13;   // keys pick_1..pick_13: twelve members + one wild slot (Keeko pick_11 and Hapi pick_12 entered the input map 2026-11-17, Athios pick_13 2026-11-19)
	static readonly string[] OPT_LABEL = { "Longer Reach (+15% Range)", "Heavier Blows (+10% Damage)",
										   "Swifter Swings (+16% Attack Speed)", "Quicker Thrusts (+16% Attack Speed)",
										   "Deeper Thrusts (+16% Range, Pierce +1)", "Sharper Tip (+10% Damage)",
										   "Pointy Arrows (+12% Damage)", "Faster Hands (+18% Attack Speed)",
										   "Piercing Shot (+1 Pierce)", "Multishot (+1 Projectile)",
										   "Spirit Brand (+12% Damage)", "Wider March (+16% Range)",
										   "Pulsing Guardians (+18% Attack Speed)", "Searing Light (10% Vulnerability)",
										   "Explosive Spirits (+12% Damage)", "Bigger Explosions (+16% Area)",
										   "Faster Ritual (+18% Attack Speed)", "More Friends (+1 Familiar)", "Staggering Blow (+25% Slow for 2.5s)",
										   "Deadly Aim (+5% Crit Chance)", "Hotter Vials (+10% Damage)", "Faster Throws (+18% Attack Speed)", "Piercing Flame (+1 Pierce)", "Explosive Finale (Burning enemies explode for 25% of Firebolt's damage)", "Searing Flames (+2s Burning Duration)", "Heavier Shield (+10% Damage)", "Quicker Hurl (+18% Attack Speed)", "Rebounding Shield (+1 Shield Bounce)", "Sharper Rims (+5% Crit Chance)", "Smite (+25% Crit Damage)", "Sharper Axes (+10% Damage)", "Quicker Throw (+18% Attack Speed)", "More Axes (+1 Projectile)", "Bigger Axes (+25% Size)", "Bleeding Edge (+1 Damage/sec for 5s)" ,
		"Pointy Edges (+10% Damage)", "Quicker Stabs (+18% Attack Speed)", "Lingering Venom (+2s Poison Duration)",
		"Lucky Stabs (+5% Crit Chance)", "Sneak Attack (+5% Multistrike Chance)",
											   "Fire Rune (+1 Burn Damage/sec for 5s)", "Harder Thrusts (+15% Crit Damage)",
											   "Eagle Eye (+5% Crit Chance)", "Difficult Terrain (10% Slow for 2.5s)",
											   "Echoing Explosions (+5% Multistrike Chance)", "Arcane Power (+12% Damage)", "Faster Channeling (+16% Attack Speed)", "Split Beams (+1 Beam Target)", "Arcane Reach (+10% Beam Range)", "Arcane Echo (+5% Multistrike Chance)", "Overflowing Necrosis (+12% Damage)", "Swift Curse (+16% Attack Speed)", "Multi-Flame (+1 Pillar Target)", "Necrotic Plague (Flame DoT passes on touch)", "Greater Pestilence (+15% Pillar Radius)" , "Sharper Claws (+12% Damage)", "Savage Attacks (+16% Attack Speed)", "Primal Might (+1 Claw Attack)", "Haemorrhaging Bleed (Bleed slows 10%)", "Longer Claws (+15% Range)" , "Remove Inhibitors (+12% Damage)", "Wider Barrel (+16% Beam Width)", "Efficient Gears (+16% Attack Speed)", "Vampiric Core (Martha Heals 5% of All Damage Dealt)", "Explosive Finale (Kill Blast 25/50/75% Damage)" }; // 2026-11-23 restore: claw rows 55-59 ride the SAME x/3 rank track as every other character (user law: "any characters we build follow the same formula of 3 skill ranks for all upgrades - this may be reworked down the line, but for now this is law"); an OFF/ON carve-out and a "+20% / Longest Claws" spec were assistant-authored text misfiled as user ground truth (opencode.db session_message has zero user-role hits for "+20%" or "Longest Claws") | rows 60-64 = Hew's Thunder Cannon kit (user 2026-11-22, spec verbatim: "Remove Inhibitors: +12% damage. Wider Barrel: +16% area. Efficient Gears: +16% attack speed. Vampiric Core: Martha heals 1% of all damage dealt. Explosive Finale: Hew and Martha kills explode for 25/50/75% damage")
	static readonly int[] OPT_MEMBER = { 0, 0, 0, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 0, 1, 5, 5, 5, 5, 5, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 8, 8, 8, 8, 8, 0, 1, 2, 3, 4, 9, 9, 9, 9, 9 , 10, 10, 10, 10, 10 , 11, 11, 11, 11, 11 , 12, 12, 12, 12, 12 };
	static readonly float[] OPT_CD = { 1.0f, 1.0f, 0.86f, 0.86f, 1.0f, 1.0f, 1.0f, 0.85f, 1.0f, 1.0f, 1.0f, 1.0f, 0.85f, 1.0f, 1.0f, 1.0f, 0.85f, 1.0f, 1.0f, 1.0f, 1.0f, 0.85f, 1.0f, 1.0f, 1.0f, 1.0f, 0.85f, 1.0f, 1.0f, 1.0f, 1.0f, 0.85f, 1.0f, 1.0f, 1.0f, 1.0f, 0.85f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 0.86f, 1.0f, 1.0f, 1.0f , 1.0f, 0.86f, 1.0f, 1.0f, 1.0f , 1.0f, 0.86f, 1.0f, 1.0f, 1.0f , 1.0f, 1.0f, 0.86f, 1.0f, 1.0f }; // row 56 Savage Attacks: cd x0.86 = +16% attack speed (hand-play brief) | row 62 Efficient Gears: cd x0.86 = +16% (shared lane; HEW clamps the authored 2.0 s and reroutes speed to damage at HALF rate)
	static readonly float[] OPT_RANGE = { 1.15f, 1.0f, 1.0f, 1.0f, 1.16f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.16f, 1.0f, 1.0f, 1.0f, 1.16f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f , 1.0f, 1.0f, 1.0f, 1.0f, 1.0f , 1.0f, 1.0f, 1.0f, 1.0f, 1.15f , 1.0f, 1.0f, 1.0f, 1.0f, 1.0f }; // row 59 Longer Claws: +15% range PER RANK (user brief seq 129914) | Hew keeps reach PINNED at 520: Wider Barrel rides OPT_BARREL and widens only the beam band
	// user 2026-11-01 audit fix: Stack rows 20/21 were swapped - keener flame carried no bonus at all and quicker fuse carried the damage. damage 1.10 rides row 20, cooldown 0.85 rides row 21.
	static readonly float[] OPT_DMG = { 1.0f, 1.10f, 1.0f, 1.0f, 1.0f, 1.10f, 1.12f, 1.0f, 1.0f, 1.0f, 1.12f, 1.0f, 1.0f, 1.0f, 1.12f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.10f, 1.0f, 1.0f, 1.0f, 1.0f, 1.10f, 1.0f, 1.0f, 1.0f, 1.0f, 1.10f, 1.0f, 1.0f, 1.0f, 1.0f, 1.10f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.12f, 1.0f, 1.0f, 1.0f, 1.0f , 1.12f, 1.0f, 1.0f, 1.0f, 1.0f , 1.12f, 1.0f, 1.0f, 1.0f, 1.0f , 1.12f, 1.0f, 1.0f, 1.0f, 1.0f }; // row 55 Sharper Claws: +12% damage (hand-play brief: damage ONLY, no crit) | row 60 Remove Inhibitors: +12% damage (user number)
	static readonly int[] OPT_PIERCE = { 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 }; // rows 4 + 8: Mosiah deeper thrust / Asper piercing shot (user 2026-10-30) | Hew's cannon pierces natively (the lane damages EVERY body it covers), no card needed
	static readonly int[] OPT_SHOTS = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0  , 0, 0, 0, 0,0 }; // multishot rank: for Yu it is the MULTIATTACK rank - one more familiar per ritual
	// guiding light (Alvora): the ring MARKS caught bodies for +10%/rank incoming damage
	// from EVERY source, a window on the body that decays on its own (Combatant.TickVuln).
	// The user swapped the old flat-armour defence for this offense (2026-10-23): the squad
	// keeps no mitigation, but the cleric's ring becomes the party's damage setup.
	static readonly int[] OPT_VULN = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 };
	// draging maul (Baldrun 4th card, user 2026-10-24): connected maul hits drag caught bodies to
	// (1 - 0.25/rank) speed for a 2.5 s window (Combatant.slowMul/slowT, refresh-not-stack); only
	// horde movement reads it and the cleave hook never marks Bosses. deadly aim (Mosiah 4th card):
	// +8%/rank CRIT_CHANCE points on the every-PC 5% seed - RollDamage already rolls that slot,
	// so the crit card is pure table data. Rows 18/19 of the 20-wide tables; caps bound both:
	// drag <= 0.75 at rank 3, crit <= 0.05 + 3 x 0.08 = 0.29.
	// OPT_SLOW = drag strength/rank (Baldrun row 18); OPT_CRIT = crit points/rank (Mosiah row 19).
	static readonly double[] OPT_SLOW = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.25, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 };
	static readonly double[] OPT_CRIT = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.08, 0, 0, 0, 0, 0, 0, 0, 0, 0.08, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.08, 0, 0, 0, 0.08, 0, 0, 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 }; // row 55 Sharper Claws: NO crit - the hand-play brief carries damage ONLY (reverted 2026-11-23)
	// firebolt kit cards (Stack, user 2026-10-30): OPT_BURN = +2 s of linger per rank onto
	// PartyMember.burnDur; OPT_CINDER = cinder bloom rank: a body that dies STILL burning bursts
	// in CINDER_R px, scorching the live horde bodies in the blast for 25/50/75% of the firebolt's
	// roll at rank 1/2/3 (Game.CinderBurst, hooked in the kill scan). Rows
	// 25-29 of the now 25-wide tables.
	static readonly double[] OPT_BURN = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2.0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 };
	static readonly int[] OPT_CINDER = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 };
	static readonly double CINDER_R = 60.0; // cinder bloom blast radius (assistant pick: small)
	// shield kit cards (Wash, user 2026-10-31): OPT_BOUNCE = +1 shield bounce per rank (the thrown
	// shield hits its prey then hops to one MORE body per bounce; the hop lane lives on PartyMember);
	// OPT_CRITD = +0.25 CRIT_DAMAGE points per rank on the tag store. Rows 25-29 of the now 30-wide
	// tables: heavier shield | quicker hurl | more rebounds | sharper rim | crushing blow.
	static readonly int[] OPT_BOUNCE = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 };
	static readonly double[] OPT_CRITD = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.25, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.15, 0, 0, 0, 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 };
	// axe kit cards (Bruno, user 2026-10-31): heavier axe rides the shared OPT_DMG 1.10 row,
	// quicker throw rides the shared OPT_CD 0.85 row; these three columns are NEW:
	// OPT_AXE = +1 extra crescent per rank (base ONE throw, arcs alternate top/bottom);
	// OPT_SIZE = +1 size rank per rank (the blade disc + band depth read 1.0 + 0.25 * rank);
	// OPT_BLEED = bleed rank (hit bodies bleed like the fire DoT, own status pair; dps scales
	// with the damage card at the call site). Rows 30-34 of the then 35-wide tables.
	static readonly int[] OPT_AXE = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 };
	static readonly int[] OPT_SIZE = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 };
	static readonly int[] OPT_BLEED = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 };
// Brook dagger cards (user 2026-10-31): sharper edges rides the shared OPT_DMG row,
// quicker jabs the shared OPT_CD row, deadly angle the shared OPT_CRIT row; these two
// columns are NEW: OPT_VENOM = +2 s of poison window per rank (lingering venom),
// OPT_MS = +0.05/rank multistrike CHANCE (lucky strikes: a hit deals damage twice).
// Rows 35-39 of the now 40-wide tables.
	static readonly double[] OPT_VENOM = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2.0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 };
	static readonly double[] OPT_MS = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.05, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.05 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 };	// fifth cards (user 2026-11-01): the tables are now 45-wide and rows 40-44 give the FIRST five
	// PCs a fifth track each. Two of the five ride columns that already exist - deadlier aim (row
	// 41) rides OPT_CRITD as +0.15 CRIT_DAMAGE points/rank (10% of the 1.5 BASE per rank, the same
	// points lane the Wash crushing blow card uses), keener aim (row 42) rides OPT_CRIT +0.08/rank exactly
	// like Mosiah's deadly aim. The other three are NEW columns: OPT_FIRE = fire rune dps/rank (5 s
	// Burn window at the maul commit; the third Burn arg stays 0 so cinder bloom + the Drink
	// "burning body" bonus keep riding the firebolt stamp ONLY), OPT_RING = weighty-ring slow
	// strength/rank (reuses the draging-maul lane Combatant.Slowed, bosses immune at the applier like
	// the drag, 2.5 s refresh-not-stack window), OPT_LUCK = lucky-explosion chance/rank baked on the
	// summon slot at spawn (_summonLuck, pool column grows WITH the pool) and rolled in DetonateSummon
	// as a SECOND sweep pass - zero Rng draw when the rank is 0.
	static readonly double[] OPT_FIRE = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1.0, 0, 0, 0, 0, 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 };
	static readonly double[] OPT_RING = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.10, 0, 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0  , 0, 0, 0, 0,0 };
	static readonly double[] OPT_LUCK = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.05, 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0  , 0, 0, 0, 0,0 };
	// Keeko "Nexus" kit cards (user 2026-11-14): rows 45-49, the tables are now 50-wide. Arcane Power
	// rides the shared OPT_DMG 1.12 row, Faster Channeling the shared OPT_CD 0.86 row (the same +16%
	// cadence the other speed cards use), Arcane Echo rides OPT_MS (+0.05/rank multistrike CHANCE -
	// the Strikes() roll, NOT the +1-projectile OPT_SHOTS lane). These two columns are NEW:
	// OPT_BEAM = Split Beams extra picks/rank (each extra prey gets its OWN hitscan lane + slot, cap 4);
	// OPT_REACH = Arcane Reach: +0.10 reach FACTOR per rank - the 2026-11-14 hand-play pass
	// RETIRED the knockback the user did not like ("I actually don't like the knockback. Let's replace it with a simple +10% range increase"): EffRange() x (1 + beamReach) now widens the Nexus lane instead, and PartyMember draws the band tome->prey so it crosses the bodies it wounds.
	static readonly int[] OPT_BEAM = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0  , 0, 0, 0, 0,0 };
	static readonly double[] OPT_REACH = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.10, 0 , 0, 0, 0, 0, 0 , 0, 0, 0, 0, 0  , 0, 0, 0, 0,0 };
	// Hapi "Necrotic Flame" kit cards (user 2026-11-17): rows 50-54, the tables are now 55-wide. Overflowing
	// Necrosis rides the shared OPT_DMG 1.12 row and Swift Curse the shared OPT_CD 0.86 row EXACTLY like
	// every other kit's damage/speed pair; the necrosis DoT itself scales with the damage card at the commit
	// (1 dps x damage stat, the user's 1 dmg/s x 5 s base). These THREE columns are NEW: OPT_PILLAR = Multi-Flame
	// extra pillar drops/rank (each extra pick drops its OWN column - the radius test means a column bites
	// EVERY body in PILLAR_R (1 + 0.15 x OPT_PEST rank), so Pestilence widens a REAL bite); OPT_PLAGUE =
	// Necrotic Plague rank (REBALANCED 2026-11-18 hand-play: carriers no longer pass the flame on TOUCH,
	// which read as free spread the horde could never answer - the curse now coughs on the flame itself,
	// every second of the DoT window a flamed body rolls PLAGUE_CHANCE x rank to stamp one nearby un-flamed
	// body inside PLAGUE_SPREAD with the window it still has); plus the applier's OWN stamp runs +2 s/rank
	// longer at the commit); OPT_PEST
	// = Greater Pestilence rank (column radius x1.15/rank at the PartyMember commit site).
	static readonly int[] OPT_PILLAR = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0 , 0, 0, 0, 0, 0  , 0, 0, 0, 0,0 };
	static readonly int[] OPT_PLAGUE = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0 , 0, 0, 0, 0, 0  , 0, 0, 0, 0,0 };
	static readonly int[] OPT_PEST = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 , 0, 0, 0, 0, 0  , 0, 0, 0, 0,0 };
	// Athios claw kit cards (rows 55-59 of the 65-wide tables, OPT_MEMBER 11; user 2026-11-19 brief, verbatim
	// at session seq 129914 and re-quoted at the table at seq 145106: "Sharper Claws - +12% damage / Savage
	// Attacks - +16% attack speed / Primal Might - +1 claw attack / Haemorrhaging Bleed - Bleed effect now also
	// slows the enemy by 10% / Longer Claws - +15% increased range. These are per level, as they have always
	// been for all characters"). Rows 55-59 ride SHARED lanes with exactly those numbers: Sharper Claws OPT_DMG
	// 1.12 with NO crit column, Savage Attacks OPT_CD 0.86 (cd x0.86 = +16% speed), Longer Claws OPT_RANGE 1.15.
	// Two columns are NEW: OPT_CLAW = Primal Might +1 claw attack PER RANK (hasPrimal is a rank count, read at
	// the march); OPT_BLEEDSLOW = one +10%/rank slow stamped beside the Bleed via Combatant.Slowed.
	// RESTORE 2026-11-23: the claw cards go back to the x/3 rank track like every other character (user law: "3
	// skill ranks for all upgrades ... may be reworked down the line, but for now this is law"). The OFF/ON carve-
	// out plus a "+20% damage / +20% crit / +25% range / Longest Claws / Primal Might adds 2 swings while his
	// ultimate is up" spec were ASSISTANT-authored text misfiled as user ground truth (opencode.db session_message
	// has zero user-role hits for "+20%", "Longest Claws" or "off/on"; receipts seq 129914 / 137200 / 145106 /
	// 148412). Nothing here ships on that attribution. OPT_MEMBER 11 gates the rows to Athios; no claw-specific
	// picker or ApplyUpgrade guard remains.
	static readonly int[] OPT_CLAW = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0  , 0, 0, 0, 0,0 };
	static readonly double[] OPT_BLEEDSLOW = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.10, 0  , 0, 0, 0, 0,0 }; // 10%/rank, matching the card text (user 2026-11-22 hand-play: the old 1.0 was a decimal-point typo - rank 1 parked bodies (speed * (1-1)) outright and rank 2 flipped them to NEGATIVE speed, fleeing at full run; the Slowed lane itself is fine, Keeko's freeze was a red herring)
	// Hew Thunder Cannon kit cards (user 2026-11-22; rows 60-64 of the 65-wide tables, OPT_MEMBER 12 gates
	// them to Hew). Remove Inhibitors rides the SHARED OPT_DMG lane (1.12), Efficient Gears rides the SHARED
	// OPT_CD lane (0.86 - but HEW clamps the authored 2.0 s cooldown and reroutes attack-speed bonuses to
	// damage at HALF rate per the spec, while MARTHA's bite really speeds up), and Wider Barrel deliberately
	// does NOT ride OPT_RANGE - Hew's reach is PINNED at the authored 520 and only the beam BAND widens.
	// THREE new columns carry the bespoke kits: OPT_BARREL = Wider Barrel presence switch (band x1.16/rank
	// at the cannon march; radius upgrades widen the beam too, spec "the beam gets wider with radius upgrades"),
	// OPT_VAMP = Vampiric Core presence (Martha heals 5% of ALL damage dealt by the party, capped at her 40 hp
	// pool), OPT_EXPLO = Explosive Finale presence (Hew and Martha kills explode for 25/50/75% of the kill's
	// damage by rank, ~60 px blast at the corpse).
	// The Leader Bonus lane is separate: every defeated enemy while Hew leads has a 5% chance to drop an
	// UPGRADE part (5% damage / 5% attack speed / 5% radius / 1 armour / 5 hp) boosting that stat for ALL
	// characters and Martha, run-permanent, diminishing per kind (n-th part = base x 0.9^n); Game.cs counts
	// the kinds and calls PartyMember.applyUpgradeBonus + the Martha fields.
	// NOTE: no claw-specific guards remain (2026-11-23 restore) - every track, claw and Hew alike, keeps rank.
	static readonly int[] OPT_BARREL = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 , 0, 1, 0, 0, 0 };
	static readonly int[] OPT_VAMP = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 , 0, 0, 0, 1, 0 };
	static readonly int[] OPT_EXPLO = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 , 0, 0, 0, 0, 1 };


	int[] _pickRank = new int[OPT_LABEL.Length];
	int[] _optRoll = new int[PICK_SLOTS]; // this card's offered options (slot -> option, -1 = empty slot)

	// ---------- dash (SPACE; balance pass 4, user-approved) ----------
	// Leader lunges 120 px (halved from 240 per user: "covers less ground") on a
	// 6 s cooldown (up from 1.5 s per user); every live ring member lunges the
	// SAME 120 px on its own 6 s cooldown - with the leader halved the impulses
	// are symmetric, so the ring formation is preserved through the squad lunge.
	// The cooldown gets a readout: a gold fill bar over Baldrun's head that
	// grows back as the dash recharges.
	readonly double DASH_IMPULSE = 120.0; // px burst per dash (user halved it)
	readonly double DASH_CD = 6.0;        // seconds between dashes (user raised it)
	double _dashCdT = 0.0;                // cooldown countdown (ready at <= 0)
	readonly double MEMBER_DASH_IMPULSE = 120.0; // px burst per ring member (now == leader)
	readonly double MEMBER_DASH_CD = 6.0;        // seconds between squad lunges
	double _memberDashCdT = 0.0;
	Godot.Node2D _dashBarPivot = null;    // ride-along pivot over the leader's head
	Godot.ColorRect _dashBarFill = null;  // the gold fill, re-sized every frame
	Godot.Label _ultLabel = null;         // ULT READY / countdown, rides the leader
	double _dashPulse = 0.0;              // free-running clock behind the dash-ready flash
	double _engageInvulnT = 0.0;          // 2 s party immunity window right after a boss engages

	// ---------- rest -> horde scaling (long rest costs the party its safety) ----------
	// Director.cs reads this and densifies spawns; the long-rest risk chip on the
	// Campfire screen is wired to exactly this.
	double _hordeScale = 1.0;
	// ---------- horde EVENT: budget-free bonus swarms (user 2026-11-02 rule: "random horde spawns
	// ... won't count against the maximum number of enemies that can spawn ... 20 enemies (these scale
	// with the horde scale increases) ... all spawn together and move towards the player"). Pacing then
	// rode two playtest rounds: user 2026-11-04 halved the cadence, and user 2026-11-06 playtest: "the
	// first horde spawned at 290s, we should have them start way sooner" - the schedule now ARMS AT BOOT
	// at an absolute run-time (HORDE_EVENT_FIRST_AT below), and the swarm COUNT rides the ordinary party
	// scaling inside SpawnHordeEvent (Director.cs) ----------
	// an event body never takes a budget slot: RegisterHordeBody is the same no-count lane the boss nodes use. TIER 1 goblins ONLY to begin with; mixing tier-2/3 in at later run points rides g.GetHordeTier() at the event spawner (infrastructure lives in Director.cs so other maps can reuse it). Times are the user's INITIAL TEST pacing - retuned with the full M3 pacing pass.
	readonly double HORDE_EVENT_FIRST_DELAY = 30.0; // legacy after-mini-boss arm (fallback lanes only - boot arms the schedule now)
	readonly double HORDE_EVENT_EVERY = 60.0;       // run-seconds between bonus swarms (user 2026-11-04: halved - "hardly saw any"; pick)
	readonly double HORDE_EVENT_FIRST_AT = 90.0;    // user 2026-11-06 playtest: FIRST swarm at this absolute run-time, armed at boot ("start way sooner" than the 290 s the mini-boss clock produced)
	double _hordeEventDue = 0.0;                    // 0 = schedule unarmed; else run-time deadline


	// ---------- ui/ screens (Campfire.tscn + RunSummary.tscn, see ui/INTEGRATION.md) ----------
	Godot.CanvasLayer _campfire;
	Godot.CanvasLayer _summary;
	bool _campfireWaiting = false;
	string _campfireChoice = "";
	// run summary retry: one ChangeSceneToFile per run, so a retry can never loop
	bool _summaryRetried = false;
	// pause card: persistent card + pilot open-once valve + the ESC-close handoff flag
	Godot.CanvasLayer _pauseCard = null;
	bool _pausePilotUsed = false; // headless proof opens the pause card once per run
	double _pauseWaitT = 0.0; // card clock: drives the pilot's close valve
	bool _pauseJustClosed = false; // card ate the same ESC press: eat one frame, no reopen
	readonly double PAUSE_PILOT_TIMEOUT = 5.0; // seconds the pilot holds the card up
	double _pauseRunT = 0.0; // run clock at card open: the seat readout proves the freeze held


	// ---------- M1 verification pilot (headless smoke only) ----------
	// Active only when the env var M1_PILOT is set. The leader then orbits while
	// everyone is up, walks onto a downed body and stops there so the stand-still
	// revive can channel. No gameplay values are touched.
	bool _pilot = false;
	// smoke helper mirroring the dev_camp key: M1_DEV_SKIP=camp makes a PILOT run
	// skip to the campfire immediately, so CI runs actually load Campfire.tscn/.gd
	// (layout edits would otherwise go unvalidated in TPK-heavy runs)
	string _devSkip = "";
	double _pilotTick = 0.0;
	public Godot.RandomNumberGenerator Rng; // public: PartyMember.RollDamage rolls crits on the run stream

	enum StageState { PLAY, CAMPFIRE, DEAD, VICTORY }; // beat table owns the stage flow now
	StageState _state = StageState.PLAY;
		int _bossBeat = -1; // beat row whose boss is currently in play (-1 = none): kills route on it
		int _beatIdx = -1;  // last beat row that fired; drives the density ramp + HUD label
		double _beatT0 = 0.0; // run-seconds when the current beat leg started (ramp origin)
	// the camp card's own small clock (the stage clock this used to be now rides the beat table)
	double _campWaitT = 0.0;
	double _pickerWait = 0.0;
	bool _pickerActive = false;
		// ---------- pre-run character select (user 2026-10-28: mockup layout pass) ----------
		// One board, rows of three (3+2, no paging): painted minis + name tokens. MOUSE
		// drives it: click a portrait to SELECT it (the gold ring box rides that slot);
		// click the bottom CONFIRM plate to seat that PC as the Leader (same shared swap
		// as the bench). Right side = the SELECTED PC's sheet (name, abilities, Base
		// Stats with "(+0)") over an ALWAYS-up party stats box: the two "(+0)" tracks
		// (per-character on the sheet, party-wide in the box) are display placeholders
		// M3 grows into real progression - keep the row names for its slots. Headless
		// gates have no pointer: the keyboard pick rail is GONE, so ONLY the pilot
		// clock rails below walk the smoke select -> confirm -> lottery wiring.
		// browse -> confirm -> lottery wiring. The swap rides ONE shared TrySeatLeader
		// copy (bench and select doors), and the join lottery waits for the swap (the
		// demoted Leader waits for its join beat like any other recruit; _hpOrder just
		// builds later on such a run, which the camp-card fallback already survives).
		bool _selHold = false;
		bool _selDemo = false;            // bench door up: M1_SELECT=<name> under pilot
		string _selWant = "";             // bench's wanted seat name (resolved at confirm)
		double _selWait = 0.0;            // card clock: feeds ONLY the pilot auto door
		int _selBrowse = -1;              // slot the card currently reads (-1 = nothing)
		double _selWheelCd = 0.0;         // wheel beat clock: one browse notch per 0.25 s (assistant pick, retunable)
		int _selWheel = 0;                // one-shot wheel scroll stamped by _Input (+1 up / -1 down); PollSelect reads and zeroes it
		Godot.CanvasLayer _selLayer = null;
		Control _selRoot = null;          // full-rect anchor parent for every piece
		ColorRect _selBack = null;        // full-rect cover: the card hides the world frame
		Control _selPage = null;          // the four-slot board window (2x2)
		ColorRect _selFrame = null;       // highlight frame: anchors jump to the browsed slot
		Label _selPanel = null;           // right-hand breakdown, rebuilt on every browse
		Label _selHint = null;            // key-grammar reminder on the card
		Control[] _selTile = new Control[0];
		Label[] _selName = new Label[0];
		// per-seat ART box in page fractions (sizing v2): the tile rects ride this - an aspect-
		// correct box computed at build from each canvas's real dims, default = the slot rect for
		// placeholder seats; the click map keeps riding the SLOT (the box lives inside it)
		float[] _selBox = new float[0];
		// sizing v3 (hand-play lap 4, THE sizing law): TextureRect draw sizing is DEAD in this
		// 4.7.2 C# build - no stretch_mode value ever resized a seat (lap 4 saw athios/hew STILL
		// native huge after two stretch attempts). Art rides the PROVEN Sprite2D + explicit .Scale
		// lane under each slot WRAPPER; _selArt keeps the frame handle (null = placeholder seat)
		// and _selArtH the native canvas height (the scale denominator). The click map and the
		// park-hide still ride the WRAPPER (a Control), unchanged.
		Godot.Sprite2D[] _selArt = new Godot.Sprite2D[0]; // per-seat Sprite2D art child
		float[] _selArtH = new float[0]; // per-seat native canvas height
		// PAGE SCROLL (user 2026-11-23 hand-play: "the mouse wheel just scrolls between characters,
		// but it should be scrolling the page/screen up or down to select characters as eventually
		// we're going to have a lot more"): the wheel no longer steps the browse ring - it slides
		// the WHOLE board inside its window. The shift is kept as a FRACTION of the page height so
		// one number drives the rows, the gold ring and the click map alike; rows parked past the
		// window edge hide, and the clamp range is wide enough to bring any parked row back.
		// (the 2026-11-22 _selScaled/_selBoxHalf lane is gone: sizing is the SLOT RECT's job now)
		// Positive rides the board DOWN (the wheel-up flick); SEL_SCROLL_HOME centres the board on
		// its own content so the current roster all reads inside the window at rest
		float _selScroll = 0.0f;
		// SMOOTH SCROLL companion (user hand-play lap 2: "can you make it a smooth scrolling?"):
		// the wheel nudges the TARGET; _selScroll chases it exponentially in the card's beat
		float _selTarget = 0.0f;
		// content clamp band (user hand-play lap 2: "it's possible to scroll up beyond the
		// characters ... It shouldn't be able to do that"): computed once per card build from the
		// ACTUAL art boxes (ComputeSelClamp): exactly enough wheel room to bring the first/last
		// row flush to the window edge and NO further, so no blank band can appear
		float _selClampLo = 0.0f;
		float _selClampHi = 0.0f;
		Control _selParty = null;         // party box copy: always up (party-wide meta track)
		Control _selSheet = null;         // dark plate behind the sheet (box border)
		Control _selPartyBack = null;     // dark plate behind the party box
		Control _selConfirm = null;       // CONFIRM button plate (bottom of the card)
		Control _selConfirmLabel = null;  // button face copy (set locally at build: Control field)
		// ---------- STAGE SELECT card fields (the leader card above is the template) ----------
		// same shape: hold rail + pilot clock valves + browse/CONFIRM, one registry entry per
		// plate. A seat that never sets M1_STAGE keeps _stageHold false and plays stage 1.
		bool _stageHold = false;           // card up: the _Process rail waits, _Input owns the mouse
		bool _stageDemo = false;           // bench door up: M1_STAGE=<id> under pilot
		string _stageWant = "";            // bench's wanted stage id (resolved on the pilot clock)
		double _stageWait = 0.0;           // card clock: feeds ONLY the pilot auto door
		int _stageBrowse = -1;             // registry entry the card reads (-1 = nothing yet)
		Godot.CanvasLayer _stageLayer = null;
		Control _stageRoot = null;         // full-rect anchor parent for every piece
		ColorRect _stageBack = null;       // full-rect cover: the card hides the world frame
		Control _stagePage = null;          // the board window the build draws AND the click maps into
		ColorRect _stageFrame = null;       // gold ring: anchors jump to the browsed plate
		Label _stagePanel = null;           // right-hand readout, rebuilt on every browse
		Control _stageSheet = null;         // dark plate behind the readout
		Label _stageHint = null;            // click-grammar reminder on the card
		Control[] _stageTile = new Control[0];  // one plate per registry entry
		Label[] _stageName = new Label[0];   // plate name label (stage label + locked tag)
		Label[] _stageBlurb = new Label[0];  // plate identity line: what the stage means
		Control _stageConfirm = null;      // CONFIRM button plate
		Control _stageConfirmLabel = null; // button face copy
	bool _summaryPrinted = false;

	Godot.Camera2D _camera;
	Godot.Label _hud;
	Godot.Label _pickerLabel;
	// ---------- per-member HP strips (8-bit HUD grammar, replaces stacked text) ----------
	// Human-test: the stacked "name: hp" lines ran past the window edge and were not
	// scannable mid-horde. Each slot is a name label + a 110x7 bar + a state dot
	// (sage standing / blood down / amber stable / grey OUT), gold marking the Leader.
	Godot.CanvasLayer _hudLayer = null;
	Godot.Collections.Array<Godot.Label> _hpName = new();
	Godot.Collections.Array<Godot.ColorRect> _hpBg = new();
	Godot.Collections.Array<Godot.ColorRect> _hpFill = new();
	Godot.Collections.Array<Godot.ColorRect> _hpDot = new();
	bool _hpStripsBuilt = false;
	// Martha mini HUD strip (user 2026-11-23): she is NOT a PartyMember, so a 40x5 bar + number
	// rides Hew's strip row instead of a slot of its own.
	Godot.Label _marthaHpN = null;
	Godot.ColorRect _marthaHpBg = null;
	Godot.ColorRect _marthaHpF = null;
	Godot.ColorRect _marthaRepairBg = null; // repair gauge BG (user 2026-11-24 round 7): a thin bar UNDER Martha's mini strip
	Godot.ColorRect _marthaRepairF = null;  // ditto: fills as the 20 s yard-time knits while she is broken
	bool _marthaRally = false;              // ditto: after a revive she RUSHES to Hew's flank before the hunt lanes reopen
	// Martha gait rig (user 2026-11-23: "the same movement animation as the enemies") - the Enemy.cs
	// sway law odometer + eased px/s live here; the rig Node2D swings her art, never the hit core.
	Godot.Node2D _marthaRig = null;
	double _marthaBobT = 0.0;
	double _marthaMoveSpd = 0.0;
	Vector2 _marthaLastMove = new Vector2(0.0f, 0.0f);
	bool _marthaMovePrimed = false;
	string _pickerCard = "LEVEL UP";      // what the picker is offering (level-up / camp gift)
	double _pickerNoticeT = 0.0;          // "every rank capped - xp banked" notice countdown
	Godot.Collections.Array<PartyMember> _party = new();
	PartyMember _leader = null;
	Godot.Collections.Array<Godot.Node2D> _gems = new();
	Godot.Collections.Array<double> _gemVals = new();
	// ---------- XP-gem FX tiers + merge rail (user 2026-11-17) ----------
	// The pool is a FIXED 240 nodes, so near capacity a kill can no longer find a free slot.
	// Easier fix the user asked for: fold CLUSTERS instead of growing the pool or evicting a
	// live orb (Vampire-Survivors style - one gem carries the XP of its neighbours). Gated so
	// an ordinary run never merges: the shipped drop-per-kill look stays untouched.
	const int GEM_MERGE_TRIGGER_PCT = 75; // fold clusters once the pool is this % full (assistant pick)
	const double GEM_MERGE_R = 34.0;      // fold radius at that moment, px (assistant pick)
	// FX tiers: the 1-4 / 5-9 / 10-49 / 50+ split is the USER law (2026-11-17: "50+ we can set as
	// red. This is our mega gem if it becomes too much"); the hues below are assistant picks.
	const double GEM_TIER_BLUE = 5.0;     // XP at which an orb turns blue
	const double GEM_TIER_GREEN = 10.0;   // XP at which an orb turns green
	const double GEM_TIER_RED = 50.0;     // XP at which an orb becomes the RED MEGA orb (USER)
	// GEM ART (2026-11-17, the user picked the d20 XP chip: "gem_d20 is amazing. Let's use that
	// one" + chose mega differentiation = bigger + inner star). Two HAND-AUTHORED tone-only
	// canvases in art/pickups: xp_gem.png 14x14 and xp_gem_mega.png 20x20. They carry SHAPE and DEPTH
	// only (grey luminance), never their own colour, because PaintGem multiplies the tier hue
	// through the sprite's Modulate - a baked hue would stop re-reading when a merge fattens an
	// orb. Loaded ONCE at pool build into these statics so PaintGem never touches GD.Load.
	// Missing art leaves both null and the shipped greybox square is the fallback, unchanged.
	// statics, not const: Vector2/Texture2D cannot be const in this dialect.
	// NOTE: these are set in the pool-build pass below; a project that has never built the pool
	// (never happens: the pool builds in InitVisuals before any kill can drop) reads null safely.
	private static Godot.Texture2D _gemArtTex = null;   // 14x14 normal chip
	private static Godot.Texture2D _gemMegaArtTex = null; // 20x20 chip + inner star (50+ tier)
	// ---------- field pickups (user 2026-11-08: small random drops to help runs) ----------
	// One rare roll per ORDINARY enemy kill (the kill rail calls MaybeDropPickup while the body
	// is still live); flavour numbers live in Pickup.cs. Assistant-picked odds, hand-test retunes.
	// IN-RUN only: the compendium meta law keeps the additive meta pool clean of field drops.
	const double PICKUP_CH = 0.02;  // per-ordinary-kill drop roll (assistant pick)
	public double _magnetT = 0.0;   // XP magnet clock: >0 widens UpdateGems' bubble to whole-field
	// looping-world decor: the flat floor rect rides the camera (solid color, so pinning
	// it is seam-invisible) while the scattered patches fold like every other entity,
	// which keeps them scrolling AND looping as the motion readout
	Godot.ColorRect _floorRect = null;
	// hidden full-rect Control: after layout its Size IS the visible canvas rect
	// (anchors 0,0-1,1 resolve against the root viewport) - the window measurement
	Godot.Control _winProbe = null;
	Godot.Collections.Array<Godot.ColorRect> _floorPatches = new();
	Godot.Collections.Array<Godot.Node2D> _bullets = new();
	Godot.Collections.Array<Vector2> _bulletVel = new();
	Godot.Collections.Array<double> _bulletDmg = new();
	// HURL TUMBLE (user 2026-11-12 hand-play: "when ogre2 hurls a goblin, make the goblin
	// spin so it's more obvious it's a projectile"): rad/s per slot, ZERO for every ordinary
	// volley so arrows / boss bolts / fireballs never twitch. Parallel to _bullets.
	Godot.Collections.Array<double> _bulletSpin = new();
	// BOSS SHOT LAW (user 2026-11-12: "Remove the ability to destroy boss projectiles.
	// Keep destruction of regular enemy projectiles."): one bool per pooled slot, TRUE
	// only for volleys Boss.cs fires. The two blade-vs-missile scans below skip those
	// slots, so a boss bolt can never be shattered by a melee verb - it still hurts
	// (UpdateBullets reads the same slots, untouched). Parallel to _bullets.
	Godot.Collections.Array<bool> _bulletBoss = new();
		// ---------- party-side projectile pool (M2: Asper's longbow) ----------
		// The boss pool above is FACTION-OWNED: UpdateBullets runs volleys into the PARTY
		// (NearestMemberAny -> TakeHit). A PC weapon that fires needs the mirror pool that
		// runs into ENEMIES, so the two never share slots or collision. Same discipline as
		// the boss pool: pooled nodes, no per-body allocation, despawn past the measured
		// frame (a wrapping world has no walls to die against), damage on contact.
		// Life is capped too, so a shot that misses every body cannot loop the torus forever.
		Godot.Collections.Array<Godot.Node2D> _shots = new();
		Godot.Collections.Array<Vector2> _shotVel = new();
		Godot.Collections.Array<double> _shotDmg = new();
		Godot.Collections.Array<double> _shotLife = new();
		// fx-feel8 cinder-bloom POP pool: purely visual. CinderBurst already dealt the damage;
		// this only draws the pop it made, like Yu's DetonateSummon re-sizes ITS shell. Pooled
		// because a chain of deaths can pop several corpses on one frame.
		Godot.Collections.Array<Godot.Node2D> _bursts = new();
		Godot.Collections.Array<double> _burstLife = new(); // seconds left in the 0.16 s pop window
		// fx-feel9 chain-bolt pool (see LooseChainBolt + UpdateChainFx): one thin blue streak
		// segment per hop of Baldrun's leader-bonus chain. life<=0 = free; delay = reveal beat.
		Godot.Collections.Array<Godot.Node2D> _boltN = new();
		Godot.Collections.Array<Godot.ColorRect> _boltC = new();
		Godot.Collections.Array<double> _boltLife = new();
		Godot.Collections.Array<double> _boltDelay = new();
		// ---------- party-side summon pool (M2: Yu's occult familiars) ----------
		// A familiar is NOT a projectile and NOT a Combatant: it runs at the closest prey and
		// DETONATES - either on contact, or when its fuse burns out (user rule 2026-10-22: "the
		// minions will last 6 seconds before detonating to keep the player from stacking them
		// up for a boss"), so there is nothing to cap and nothing to keep forever. Same pooled
		// discipline as the shot pool: fixed slots, no allocation per familiar. The 64 slots are
		// an implementation bound, not a gameplay cap - a 1.6s ritual with a 6s fuse and the
		// rank-3 multiattack still running keeps roughly 15 in flight, nowhere near the bound.
		// Blast radius and speed are per slot because both ride the member's ranks, which move
		// between one commit and the next.
		Godot.Collections.Array<Godot.Node2D> _summons = new();
		Godot.Collections.Array<Vector2> _summonVel = new();   // last heading, re-steered every frame
		Godot.Collections.Array<double> _summonDmg = new();
		Godot.Collections.Array<double> _summonLife = new();   // the fuse (seconds left)
		Godot.Collections.Array<double> _summonR = new();      // blast radius, px
		Godot.Collections.Array<float> _summonSpd = new();     // px/s this familiar runs
		Godot.Collections.Array<bool> _summonBurst = new();    // slot is mid detonation flare
		Godot.Collections.Array<bool> _summonKaka = new();   // slot is an empowered Kaka familiar (ult): doubled numbers baked at spawn
		Godot.Collections.Array<double> _summonLuck = new(); // lucky explosions: double-blast chance baked on the slot at spawn

		// Yu minion art (2026-11-01 art pass part 2): true when the flame-stump familiar loaded;
		// the shell reclaims its Color alpha ONLY while it IS the blast (see DetonateSummon)
		bool _famArt = false;
		// bolt-pool pierce memo (leader bonus, user 2026-10-25): while Mosiah owns the
		// leader seat a bolt punches through one body and keeps flying. _shotPen is the
		// per-bolt pierce budget stamped at spawn; _shotLast memoizes the body the bolt
		// just raked so a pierced bolt cannot re-hit the SAME body the next frame.
		// (GetInstanceId is 64-bit, so the memo array is long-typed)
		Godot.Collections.Array<double> _shotPen = new();
		Godot.Collections.Array<double> _shotBurnD = new();   // firebolt burn: dps stamped at spawn (0 = inert)
		Godot.Collections.Array<double> _shotBurnT = new();   // burn window start, seconds
		Godot.Collections.Array<double> _shotCinderD = new();  // cinder bloom burst damage stamped at spawn
		Godot.Collections.Array<long> _shotLast = new();

		// shot-lane fx art (2026-11-01 art pass): null-safe one-time loads at spawn; if the
		// art never imports, the greybox shell keeps drawing (the smoke contract never depends on art)
		Godot.Texture2D _fxArrowTex = null;
		Godot.Texture2D _fxFlameTex = null;
		// 2026-11-08 fx-art pass (user-approved sprites): Asper's stinger on the party-bolt lane,
		// the goblin mage's OWN fireball (firebolt stays Stack's burn read), and the bee swarm art
		Godot.Texture2D _fxStingerTex = null;
		Godot.Texture2D _fxMageTex = null;
		Godot.Texture2D _fxIceTex = null; // 2026-11-13 ice FX pass: art/fx/ice_spike.png (88x53, authored LEFT); the USER picked this candidate from two hand-checked spikes
		// OGRE2 hurl art: the thrown goblin rides the missile pool below (SpawnBulletThrown).
		// OWN try-once flag: the arrow / fireball lanes set _fxTried on the FIRST loose and load
		// ONLY their textures, so a shared flag would starve this lane forever.
		Godot.Texture2D _fxThrownTex = null;
		bool _fxThrownTried = false;
		Godot.Texture2D _fxBeeTex = null;
		bool _fxTried = false;
		// ---------- leader-bonus helper pools (user 2026-10-25): Asper's insects, Yu's spirits ----------
		// Pooled exactly like the summons: fixed slots, parallel arrays, parked past -9000.
		// _insectLife counts down the CURRENT phase (waiting for a host / bound for 10 s);
		// _insectCd is the fire cooldown while bound; _insectOwner is the bound member's
		// memberIndex (-1 = still waiting). The counters are cumulative since boot and
		// exist so a headless pilot run can SEE each leader lane doing its job.
		Godot.Collections.Array<Godot.Node2D> _insects = new();
		Godot.Collections.Array<double> _insectLife = new();
		Godot.Collections.Array<double> _insectCd = new();
		Godot.Collections.Array<int> _insectOwner = new();
		Godot.Collections.Array<Godot.Node2D> _spirits = new();
		Godot.Collections.Array<double> _spiritLife = new();
		// spawn / regen clocks and the cumulative lane counters (pilot evidence).
		// Clocks accumulate ONLY while that PC owns the leader seat (see UpdateHelpers),
		// so a seat swap starts fresh instead of inheriting a charged clock.
		double _insectClock = 0.0;
		double _spiritClock = 0.0;
		double _regenT = 0.0;
		int _lbChainFired = 0;
		int _lbInsectBound = 0;
		int _lbDrinkHits = 0; // Have a Drink!: hits softened on burning bodies while Stack leads
		int _lbSpiritTaken = 0;
		// ultimate (R, user 2026-10-25): _ultFired counts fires for the pilot readout;
		// the cooldown itself lives on the PartyMember (ultCdT), gated by StatTags.ULT
		// (gate = 120 s base / stat) so meta progression can shorten it later.
		int _ultFired = 0;
		// leader-bonus numbers: user dictates stay verbatim; the open picks are labelled
		const double CHAIN_CHANCE = 0.05; // Baldrun: 5% proc per landed party hit (user)
		const int CHAIN_BOUNCES = 4;      // up to 4 bounces (user)
		const double CHAIN_DMG = 10.0;    // each bounce bites for 10 (user)
		const double CHAIN_RANGE = 150.0; // hop range: left open by the user, this pick
		// fx-feel9 bolt ART for the chain (user 2026-11-02: "add a visual to Baldrun's leader bonus
		// that deals chain lightning ... just a simple blue streak of lightning that chains quickly
		// between enemies"): each hop draws one streak segment, lit GAP s after the last so the
		// shape reads as a fast zigzag, alive LIFE s then fading. Pool + timings are the picks.
		const double CHAIN_BOLT_LIFE = 0.14; // seconds a bolt segment stays drawn (assistant pick)
		const double CHAIN_BOLT_GAP = 0.05;  // hops light up this far apart: the fast-chain read (assistant pick)
		const int CHAIN_BOLT_POOL = 24;      // 4 segments/chain, procs can overlap (assistant pick)
		
		// Dirt puffs (user 2026-11-09 hand-play: "give monsters a very small dirt-cloud
		// puff on their steps, so it reads better on the map as moving"): the MONSTER SWAY
		// rig in src/Enemy.cs kicks one up every time a foot plants - the side-to-side
		// swing reaching an extreme, a half-period of the gait clock. PURE VFX lane: parked
		// slots like every other pool, and nothing here touches motion, hitboxes or damage.
		// Bosses and summons keep their own rigs and stay un-puffed (monsters only).
		Godot.Collections.Array<Godot.Sprite2D> _puffs = new();
		Godot.Collections.Array<double> _puffLife = new();
		Godot.Texture2D _fxPuffTex = null;
		const int PUFF_POOL = 32;      // ~3 plants/s per striding body x 0.3 s life: a big horde fits (pick)
		const double PUFF_LIFE = 0.30;   // seconds a puff stays drawn (pick)
		const float PUFF_FROM = 0.8f;    // scale at the plant: dustpuff.png is a 17x9 chunky cloud (pick)
		const float PUFF_TO = 1.5f;      // spread it drifts to before it clears (pick)
		const float PUFF_RISE = 26.0f;   // px/s the puff lifts off the plant (pick)
		const float PUFF_ALPHA = 0.6f;   // opacity at the plant: grit, not a smoke bomb (pick)

		const double REGEN_TICK = 10.0;   // Alvora: allies regen every 10 s (user)
		const double REGEN_HP = 1.0;      // +1 HP per tick (user)
		const double TEMP_HP_CAP = 10.0; // user 2026-11-06: Field Medic may OVER-heal - a buffer
					 // of up to 10 Temporary HP above the ceiling. NOT in EffMaxHp (a raised
					 // ceiling is Wash's Inspiring Leader seat); damage spends it FIRST (TakeHit)
					 // and NO other heal lane tops it (spirits, awakening and campfire still clamp at EffMaxHp)
		const double INSECT_EVERY = 30.0; // Asper: an insect waits every 30 s (user)
		const double INSECT_LASTS = 10.0; // a claimed insect helps for 10 s (user)
		const double INSECT_WAIT = 30.0;  // unclaimed wilts after 30 s: pick, keeps slots free
		const double INSECT_HEAD_LIFT = 86.0; // 2026-11-09 hand-play (user: "place the friendly bees from his leader passive higher, near his head"): 26 px measured out to Asper's KNEE, because a PC node is FEET-anchored (miniScale 0.75 over ~118 px canvases with a 7 px rim pin -> crown ~83 px above the node), unlike an enemy node, which its art CENTRES on (so the ult swarm's 20 px lift is already head-high). 86 px puts the ~15 px bee just above the crown. Assistant knob - retunable
		const double SPIRIT_EVERY = 30.0; // Yu: a spirit kindles every 30 s (user)
		const double SPIRIT_LASTS = 30.0; // unclaimed wilts after 30 s: same pick
		const float SPIRIT_HOP_EVERY = 0.85f; // one vigil hop cycle: push + rest (assistant pick)
		const float SPIRIT_HOP_MOVE = 0.28f;  // seconds of that cycle the vigil actually pushes (assistant pick)
		const float SPIRIT_HOP_SPEED = 165.0f; // px/s during the push: ~46 px a hop, ~54 px/s crawl (assistant pick)

		// user 2026-11-04: kindness spirits kindle FARTHER from the seat so the slow ride-in
		// reads on screen (Asper's insects keep the old HELPER_RING)
		const double SPIRIT_RING_MIN = 170.0;  // spirit spawn ring inner edge (pick)
		const double SPIRIT_RING_MAX = 280.0;  // outer edge: well inside the camera frame (pick)
		const double SPIRIT_HEAL = 5.0;  // user 2026-11-06: a touched spirit heals ALL allies 5 HP (was 10)
		const double HELPER_PICK = 24.0;  // "getting close": walk-contact reach, pick
		const double HELPER_RING_MIN = 90.0;  // spawn ring around the leader: picks
		const double HELPER_RING_MAX = 170.0; // (never on top of the seat itself)
		// Swarm of Bees (Asper ult, user 2026-10-26): six seekers on a 10 s leash (user),
		// each latched to ONE body and biting 5 dmg/s (user 2026-11-06: was 10); unlaunched they drift along
		// their spawn ray - the cast fans them on a circle so the swarm spreads out to
		// hunt (user). _beeTarget keeps the prey instance id (-1 = hunting): enemies QueueFree
		// on death so a node ref would dangle while an id simply stops resolving and the
		// bee drops its latch and re-hunts.
		Godot.Collections.Array<Godot.Node2D> _bees = new();
		Godot.Collections.Array<double> _beeLife = new();
		Godot.Collections.Array<double> _beeBite = new(); // seconds to the next chomp
		Godot.Collections.Array<double> _beeRay = new(); // spawn bearing: the search direction
		Godot.Collections.Array<long> _beeTarget = new();
		int _beesUp = 0;      // live bees this cast: the drop to 0 fires the dispersal print
		int _beeDrained = 0;  // tally: latched prey that stopped resolving while bitten
		bool _beeSwarmUp = false; // one dispersal print per cast
		const int BEE_COUNT = 6;      // user: exactly six bees
		const double BEE_LASTS = 10.0; // user: the swarm lives 10 s
		const double BEE_BITE = 5.0;   // user 2026-11-06: 5 damage per second to the latched body (was 10)
		const double BEE_SPEED = 300.0; // pick: fast hunter, outruns the horde
		const double BEE_R = 48.0;     // pick: spawn-ring radius around Asper
		const double BEE_FIND = 240.0; // pick: hunting reach: nearest body in range latches
		const double BEE_LATCH = 16.0; // pick: bite range: the bee rides the body
		const float BEE_ART_S = 0.32f; // pick: 39x48 bee art -> ~13 px beside the 10 px shell (assistant knob, retunable)
		const double BEE_HEAD_LIFT = 20.0; // riding anchor floats this many px ABOVE the victim center: near that character's head (user 2026-11-08: "floating near that character's head"); assistant knob
		const double BEE_BOB_PX = 1.6;    // px of gentle vertical bob on riding bees: "floating", not glued; assistant knob
		// Hapi "Necrotic Flame" pillar + "Bring Out Yer Dead!" risen pool (user 2026-11-17): the
		// risen are PLAIN pooled Node2D shells, NOT Combatants: they never enter _enemies and never
		// bite the party. The horde chews them through the Enemy chase branch and its shots
		// intercept them (real HP: enemies can kill the risen). Art: the corpse's OWN Sprite2D
		// texture + scale copied per spawn - no new art lane, no green tint over painted bodies.
		const int UNDEAD_POOL = 24;        // pool cap: conversions + leader rises (assistant pick)
		const double UNDEAD_LIFE = 10.0;   // user: each risen body lives 10 s after THEIR OWN rise
		const double UNDEAD_FIND = 620.0;  // pick: hunting reach: risen bodies SEEK the horde this far (2026-11-18 hand-play: 240 left rises standing among the pack, the user asked for real hunting)
		const float UNDEAD_TOUCH = 10.0f;  // pick: bite range: px PAST the prey's arted hitR
		const double TAUNT_PULL = 140.0;   // pick: EVERY risen body yanks enemies within this ring (Call of the Grave + ult conversions, 2026-11-18 hand-play)
		// NECROTIC PLAGUE (rebalanced 2026-11-18 hand-play): the OLD law had risen bodies pass the flame
		// on to whatever they TOUCHED, which read as unanswerable free spread over the horde. The curse
		// now coughs on the FLAME ITSELF - every second of the DoT window a flamed body rolls PLAGUE_CHANCE
		// x rank to stamp one nearby un-flamed body inside PLAGUE_SPREAD with the window it STILL has.
		// Ranks ALSO lengthen the applier's own stamp (+2 s/rank, at the PartyMember commit).
		const double PLAGUE_SPREAD = 110.0; // pick: how far a flamed body can cough the curse (assistant pick - retune by eye)
		const double PLAGUE_CHANCE = 0.10;  // user: 10/20/30% per rank, once per flame tick
		const double RISE_CHANCE = 0.05;   // user: 5% of slain enemies rise while Hapi leads
		const int PILLAR_POOL = 16;        // pick: pillar FX slots (Multi-Flame caps at 4 columns per volley)
		const double PILLAR_LIFE = 0.5;    // pick: a column reads for half a second then fades
		public const double PILLAR_R = 28.0; // BASE column radius: PartyMember commits PILLAR_R x (1 + 0.15 x Pest rank)
		public Godot.Texture2D pillarFxTex = null; // Hapi column art: wired ONCE by the member's _EnterTree gate; null = silent skip
		// FX-lap (user 2026-11-23, approved as designed): Hew's Upgrades! parts drop a VISIBLE tumbling
		// cog at the kill site; the numbers are assistant picks, retune by eye.
		const int COG_POOL = 12;        // part drops are rare (5% a kill): 12 live cogs is generous headroom
		const double COG_LIFE = 0.8;    // a cog reads ~0.8 s: tumble, drop a little, then fade
		const double COG_DROP = 10.0;   // px a cog falls over its life (the approved '~10 px' drop)
		const int MARTHA_SPARK_POOL = 6; // repair crackle slots: never more than six live streaks on the iron
		const double MARTHA_SPARK_LIFE = 0.25; // a spark reads this long then snaps out (assistant pick)
		const double MARTHA_SPARK_EVERY = 0.3; // crackle cadence across the whole 20 s broken window (assistant pick)
		public Godot.Texture2D cogFxTex = null; // Hew gear art: wired ONCE by the member's _EnterTree gate (pillarFxTex idiom); null = ember square falls back
		Godot.Collections.Array<Godot.Node2D> _undead = new();   // shells: child0 = fallback rect, child1 = art slot
		Godot.Collections.Array<double> _undHp = new();     // REAL hp: the horde can fell them
		Godot.Collections.Array<double> _undLife = new();   // seconds left after THEIR OWN rise
		Godot.Collections.Array<double> _undCd = new();     // bite cadence (1 s chomps)
		Godot.Collections.Array<double> _undDmg = new();    // bite = the corpse's contact damage
		Godot.Collections.Array<double> _undSpd = new();    // chase speed = the corpse's own speed
		Godot.Collections.Array<int> _undTaunt = new();     // 1 = risen body taunts the horde (2026-11-18: conversions taunt too; 0 only on parked slots)
		Godot.Collections.Array<Godot.Sprite2D> _pillarFx = new();
		Godot.Collections.Array<double> _pillarT = new();
	 double _plagueTick = 0.0;   // NECROTIC PLAGUE flame clock: one cough roll per second of the DoT
		// HEW "Upgrades!" leader bonus (user 2026-11-22): every defeated enemy has a 5% chance to
		// drop a part that boosts a RANDOM stat for ALL characters (and Martha): 5% Damage / 5%
		// Attack Speed / 5% Radius / +1 Armour / +5 HP, run-permanent. Parts land INSTANTLY on the
		// kill (no pickup node to chase) and DIMINISH: the n-th part of a kind applies at base x 0.9^n
		// (assistant pick - the user left the stacking open and straight 5% compounding read as a
		// snowball), so a lucky run still plateaus. Counters are RUN-WIDE: every character reads the
		// SAME tally, which is what "for ALL characters" means.
		int _upDmgN = 0;   // parts of each kind dropped this run (drives the 0.9^n dampener)
		int _upSpdN = 0;
		int _upRadN = 0;
		int _upArmN = 0;
		int _upHpN = 0;
		int _upGearN = 0;   // ROUND 8: gear parts that WELDED this run (pilot read only)
		int _upGearLoose = 0; // ROUND 8: of the granted parts, how many became a REAL field cog instead
		// ROUND 8 (user 2026-11-24: "make hews gear parts drop like xp would and make it stay on the
		// ground until picked up"): the user's 2.5% of the parts Hew's bonus roll grants loosen as a
		// Pickup node (pk 3) instead of welding into the squad on the spot. One drop, ONE route: the
		// roll either looses a field cog (this const) or welds instantly (the round-6 path, unchanged
		// for the other 97.5%). The cog never expires - unlike the three other flavours, whose 18 s
		// lifetime is the "grabbing it is a choice" law - the user asked for it to sit until grabbed.
		const double GEAR_PART_CH = 0.025; // user: 2.5% of Hew's part drops become a field cog
		// MARTHA (user 2026-11-22, Hew's companion): a dedicated PERSISTENT Node2D singleton, NOT a
		// PartyMember and NOT a risen shell - own bite lane, own damage route, never despawns. She IS
		// the horde's other player character: the Enemy chase already walks a Node2D quarry (GetTauntBody)
		// and chews it with REAL hp (UndeadHitBy), so she rides those lanes as a puller and answers the
		// shot intercept (UndeadAt) - no Enemy.cs edit needed. At 0 hp she is BROKEN for 20 s and
		// knits herself back (the countdown runs 2x while Hew stands close - proximity is an assistant
		// pick, retunable). Art: res://art/characters/martha.png (104x97 sheet, on the user's WIP rail).
		const double MARTHA_HP = 40.0;        // user
		const double MARTHA_ARMOUR = 3.0;  // user
		const double MARTHA_EVA = 0.05;     // user: 5% evasion
		const double MARTHA_CRIT = 0.05;   // user: 5% crit chance (her bite rolls it)
		const double MARTHA_BITE = 10.0;   // user
		const double MARTHA_BITE_EVERY = 1.5; // assistant pick (retune by eye): her bite cadence
		const double MARTHA_SPEED = 130.0;   // assistant pick: scuttles a touch faster than Hew (115)
		const double MARTHA_BREAK = 20.0;    // user: 20 s broken at 0 hp, repairs herself
		const double MARTHA_NEAR = 100.0;  // assistant pick (retunable): px within which Hew's presence DOUBLES the repair tick
		const double MARTHA_LEASH = 120.0; // assistant pick (retunable): px past which she runs back to Hew by the shortest (wrap) route
		const double MARTHA_PULL = 140.0;  // assistant pick (mirrors TAUNT_PULL): how far the horde notices her at all
		const double MARTHA_FIND = 620.0;  // assistant pick (mirrors UNDEAD_FIND): she chews live prey this far
		const double MARTHA_HUNT = 200.0;  // assistant pick (retunable): px around HEW inside which prey counts as HERS (flank guard: she guards the squad, not the map - hand-play 2026-11-23)
		const float MARTHA_RADIUS = 12.0f; // assistant pick: her hit core, same read as MEMBER_RADIUS / UNDEAD_RADIUS
		const double MARTHA_VITALS = 20.0;  // assistant pick (retunable): durability bonus ON the HP ceiling (hand-play recheck: 40 was chewed too fast behind the 3-armour soak)
		Godot.Node2D _marthaBody = null;   // her singleton node (null until BuildMartha seats her)
		double _marthaHp = 0.0;            // REAL hp: the horde can fell her (mirrors _undHp)
		double _marthaT = 0.0;            // BROKEN countdown: > 0 = she is repairing, never biting
		double _marthaCd = 0.0;           // bite cadence clock (mirrors _undCd)
		double _marthaDmg = 1.0;          // Upgrades! parts that landed on her: damage multiplier
		double _marthaSpd = 1.0;          // ditto: bite-speed multiplier
		double _marthaRad = 1.0;          // ditto: reach multiplier (her bite's touch band)
		double _marthaArm = 0.0;          // ditto: flat armour points on top of her 3
		Godot.Sprite2D _biteFx = null;       // FX-lap: her chomp stamp, ONE slot, child of the body (built art-null-safe at the first commit)
		double _marthaFxT = 0.0;            // FX-lap: bite-stamp life clock (0 = stamp off)
		Godot.Collections.Array<Godot.Node2D> _sparkN = new();    // FX-lap: repair crackle slots (children of her body; lazy-built on the first upkeep tick)
		Godot.Collections.Array<Godot.Sprite2D> _sparkS = new();  // the static art slot per crackle
		Godot.Collections.Array<Godot.ColorRect> _sparkR = new(); // the electric-blue streak fallback per crackle
		Godot.Collections.Array<double> _sparkLife = new();       // seconds left per crackle
		double _marthaSparkDelay = 0.0;     // FX-lap: crackle cadence while broken (0 = loose the next one)
		Godot.Collections.Array<Godot.Node2D> _cogN = new();      // FX-lap: Hew's part-drop cogs (pooled world slots, like cinders and bolts)
		Godot.Collections.Array<Godot.Sprite2D> _cogS = new();    // the gear art slot per cog
		Godot.Collections.Array<Godot.ColorRect> _cogR = new();   // the ember-square fallback per cog (paints only while the rail is absent)
		Godot.Collections.Array<double> _cogLife = new();         // seconds left per cog
		Godot.Collections.Array<double> _cogSpin = new();         // tumble rad/s per slot (alternates: two drops never read the same way)
		double _marthaHpBonus = 0.0;      // ditto: flat hp points on top of her 40
		double _marthaVamp = 0.0;         // Vampiric Core (Hew card): hp she has drunk this run, clamped to her own ceiling
		// ROUND 8 (user 2026-11-24, round 8: "she collects xp and such for the player, and will also
		// benefit from ultimates and passives that buff allies / party members (treat it like she is
		// another party member)"): she is NOT a PartyMember and NOT in _party, so none of the party
		// buff clocks - Athios Beastial Rage, Alvora Awaken, Brook Insight, the six Experimental Elixir
		// buffs, Oakhollow Assemble, an ally's own ultimate - can land on her the way they land on the
		// squad. Rather than duplicate every reader, she SAMPLES them: MirrorPartyBuffs() below takes
		// the STRONGEST live clock of each kind across the standing squad every UpdateMartha frame, so
		// a member standing in the same room as a buff and her get it alike. She never decays a mirror
		// (the PM _Process owns every countdown), so nothing can leak past its owner's expiry, and Hew's
		// OWN ult is excluded: her bite + soak lanes already read hew.ultT directly, and stacking the
		// same window twice would double-count Maximum Firepower!.
		double _muUltT = 0.0;           // mirrored ALLY ult window (never Hew's: see the exclusion above)
		double _muInsT = 0.0;           // Inquisitive Mind (Brook): +25% dealt, +25% attack speed
		// ROUND 8: she never decays a mirror (the PM _Process owns every countdown), so nothing can
		// leak past its owner's expiry. Hew's OWN ult is excluded from _muUltT (her lanes already
		// read hew.ultT directly; double-counting Maximum Firepower! would be wrong). The mirror SET
		// is the damage/move/defence/collect family - the four healing ults (Mending Tide, Tidebreak
		// Guard's revive shield, Vantage HP bump, Rootstalker Echo revive) ride HP/shield lanes with
		// no her-side analogue except HP regen, so they fold through _muOakT's regen tick when their
		// caster also runs Assemble-style sustain; assistant pick, retunable by hand.
		// (mirrors: max of the LIVE clocks across STANDING members, recomputed each UpdateMartha frame)
		double _muRageT = 0.0;          // Beastial Rage (Athios): +50% attack speed, half incoming
		double _muAwaT = 0.0;           // Awaken (Alvora): +25% dealt, double reach
		double _muOakT = 0.0;           // Assemble (Oakh): +5 armour, +25% move, regen tick
		double _muElixHealT = 0.0;      // Menders Brew (any member): regen while brewed
		// (clones the PM regen lane: max hp x 1% per second while brewed)
		double _muElixSwiftT = 0.0;     // Alacrity elixir (any member): +10% move
		// (clones SwiftfootElixirMoveMul = 1.10 on move)
		double _muElixHardT = 0.0;      // Resilience elixir (any member): 10% less incoming
		// (clones ResilienceElixirDamageMul = 0.90 in TakeHit)
		double _muElixBoldT = 0.0;      // Boldness elixir (any member): +10% dealt
		// (clones BoldnessElixirMul = 1.10 on dealt)
		double _muElixPrecT = 0.0;      // Precision elixir (any member): +10% crit
		// (clones PrecisionElixirCrit = 0.10 added to the crit chance)
		double _muElixMagT = 0.0;       // Wildcraft elixir (any member): her bubble +10%
		// (clones the +10% the brew puts on the leader's collection bubble - her own 110 px)
		const double EXPLO_BLAST = 60.0; // assistant pick (retunable): px ring an Explosive Finale kill detonates into
		// The Kaka (Yu ult, user 2026-10-26): six EMPOWERED copies of the ritual familiars -
		// same pool, steering, fuse and no-split blast, only doubled reach and bite. The x2
		// multiplies AFTER the rank scaling: the member passes already-scaled kit numbers and
		// these ride on top (same discipline as AWA_RANGE after the AREA tag). They enter on a
		// circle fanned around Yu so the flight spreads out, like the bees (user).
		const int KAKA_COUNT = 6;       // user: exactly six empowered friends
		const double KAKA_RING = 48.0;  // pick: spawn-circle radius around Yu, same as BEE_R
		const double KAKA_DMG = 2.0;    // user: damage x2, after the upgrade scaling
		const double KAKA_R = 2.0;      // user: blast radius x2, after the AREA ranks
	Godot.Collections.Array<Godot.Node2D> _enemies = new();
	Godot.Collections.Array<Boss> _bosses = new();
	int _liveEnemies = 0;
	// ---------- separation spatial hash (perf fix 2026-11-16) ----------
	// The old rail had every enemy walk the WHOLE registry every frame: O(n^2). That is
	// what turned 100+ body waves into a slideshow. Game rebuilds a fixed power-of-two hash
	// of 40 px cells at the top of _Process (above the soft-pause rails, so the snapshot is
	// never stale), and each enemy only reads the 3x3 cell ring around itself. 40 px cells
	// vs the 20 px shove radius (d2 < 400) means an in-range neighbour can never sit outside
	// the ring. Buckets chain through int arrays: no Dictionary, no per-frame allocation.
	// Bosses ride the registry too and keep shoving, exactly like before.
	const int SEP_CELL = 40;      // cell size in px (2x the shove radius)
	const int SEP_SLOTS = 4096;   // power of two: the mask is SLOT - 1 (the ~10,000-cell sheet
	// would fold 10 cells per chain at 1024, and a clumped horde walks those chains back to
	// O(n^2); 4096 keeps a 400x400 px clump collision-free and empty sheet costs nothing)
	const int SEP_MASK = 4095;
	Godot.Collections.Array<int> _sepHead = new(); // slot -> first body index, -1 = empty
	Godot.Collections.Array<int> _sepNext = new(); // body index -> next body index, -1 = end
	// _sepBody MUST be its own array, not the registry: kills during the frame REMOVE bodies
	// from _enemies after the rebuild ran, so a chain index into the registry can point past its
	// end (PD187 gate: 1,822 ArgumentOutOfRangeException from SepBody). The snapshot is the same
	// length as the census the chains were built for, so a chain can never read past it.
	Godot.Collections.Array<Godot.Node2D> _sepBody = new(); // body index -> registry node
	int _sepCount = 0;
	double _ringAngle = 0.0;
	// ---------- field-recruit state ----------
	// run clock drives the beat table; camp is excluded because it is a read-the-card
	// pause, and the picker / join soft-pauses keep stage beats honest while a human
	// reads the card. M2: one PLAY state covers horde AND boss fights, so the
	// PLAY1/MINIBOSS/PLAY2 set the run clock used to walk is gone.
	double _runT = 0.0;
	// Keeko "Rewrite Reality" (user 2026-11-14): world freeze clock in REAL seconds. While > 0 every
	// horde + boss MOVEMENT commit parks (EnemiesFrozen() read per commit) - movement only: bodies
	// still get hit and flash, but the SHOT lanes (riderbow/mage/mage2) hold their aim while the clock
	// runs (user 2026-11-18 hand-play: "freeze doesn't stop enemies attacking... needs to be disabled");
	// contact lanes stay live. Spawns read the clock live, so newborn bodies are born frozen.
	double _freezeT = 0.0;
	// TEST BENCH ONLY (same discipline as M1_PILOT / M1_DEV_SKIP / M1_REAPER): M1_TIMEWARP=<factor>
	// advances the run CLOCK x factor so the beat table walks at warp speed; physics (movement,
	// cooldowns, contact) stay real-time. The horde density curve reads a NORMALIZED leg fraction
	// (g.GetStageT()/g.GetStageLen()), so per-second pressure is UNCHANGED: legs just last 1/factor
	// of the real seconds - leaner totals, same peak live cap. Human play never sets it; factor clamps [1,12].
	double _timeWarp = 1.0;
	PartyMember _recruitPending = null; // stranded PC awaiting escort clear
	// a rescue the party never finishes must not freeze the stage forever: past
	// ESCORT_GIVEUP seconds of run clock the PC joins at the floored HP instead
	const double ESCORT_GIVEUP = 55.0;
 double _escortTimeout = 0.0;        // run-seconds the current rescue has been pending
	int _escortLeft = 0;                // escort bodies still alive
	Godot.Node2D _recruitArrow = null;  // arrow above the Leader, aimed at the PC
	Godot.Label _helpLabel = null;      // HELP! floating over the stranded PC
	bool _joinWaiting = false;          // join banner up, waiting for the click
	Godot.Label _joinBanner = null;
	Godot.ColorRect _joinBtn = null;
	Godot.Label _joinHint = null;       // CLICK TO CONTINUE under the gold card: it is a
										// field, not a local, so ClearJoinBanner can actually
										// take it away (human fix: the stale hint kept
										// telling the player to click a dead card)

	// ---------- public surface used by the other scripts ----------
	// the world LOOPS: instead of walls, every position is folded into the half-period
	// box around the camera (Asteroids-style torus). The Leader is the anchor and must
	// NOT be folded - folding it would pop the camera across a seam.
	public Vector2 WrapNearCamera(Vector2 p)
	{
		Vector2 c = GetCameraCenter();
		double pw = _wrapW;
		double ph = _wrapH;
		double dx = p.X - c.X;
		double dy = p.Y - c.Y;
		dx -= System.Math.Round(dx / pw) * pw;
		dy -= System.Math.Round(dy / ph) * ph;
		return new Vector2((float)(c.X + dx), (float)(c.Y + dy));
	}

	// shortest step on the looping world: chase/move math must use this or an entity
	// two steps from a seam tries the 2400 px way around instead of the 100 px hop
	public Vector2 WrapDelta(Vector2 from, Vector2 to)
	{
		double dx = to.X - from.X;
		double dy = to.Y - from.Y;
		dx -= System.Math.Round(dx / _wrapW) * _wrapW;
		dy -= System.Math.Round(dy / _wrapH) * _wrapH;
		return new Vector2((float)dx, (float)dy);
	}

	public PartyMember GetLeader()
	{
		return _leader;
	}

	public PartyMember NearestMember(Vector2 from)
	{
		PartyMember best = null;
		double bestD = 1e18;
		foreach (PartyMember m in _party)
		{
			if (!m.joined || m.downed || m.eliminated)
				continue; // a stranded recruit is not squad yet: escorts do not chase it
			double dd = m.GlobalPosition.DistanceSquaredTo(from);
			if (dd < bestD)
			{
				bestD = dd;
				best = m;
			}
		}
		return best;
	}

	// for contact/projectile damage: downed bodies are hit-able, just never chased
	public PartyMember NearestMemberAny(Vector2 from)
	{
		PartyMember best = null;
		double bestD = 1e18;
		foreach (PartyMember m in _party)
		{
			if (!m.joined || m.eliminated)
				continue;
			double dd = m.GlobalPosition.DistanceSquaredTo(from);
			if (dd < bestD)
			{
				bestD = dd;
				best = m;
			}
		}
		return best;
	}

	public void TakeHit(PartyMember m, double amount)
	{
		// the horde can never touch a stranded recruit: only escort goblins chip him
		// on contact (EscortHitRecruit), and that damage never runs the rescue down
		// (user design 2026-10-20 pass 11: contact damage shapes the join HP)
		if (m == null || !m.joined || m.eliminated)
			return;
		if (_engageInvulnT > 0.0)
			return; // user-approved camp-safety gate: a boss engage opens on a 2 s
					// immunity window, so the arrival volley can never free-hit a party
					// that is still reacting (or parked at the camp beat)
		if (m.invuln > 0.0f || m.reviveShield)
			return; // post-revive shield, or the body is mid-rescue: immune
		if (m.elixHardT > 0.0)
			amount *= 0.90; // Resilience elixir: every ally takes 10% less while brewed
		if (m.rageT > 0.0)
			amount *= 0.5; // Beastial Rage: the WHOLE squad (Athios + allies) takes HALF damage for the window
		amount *= LeaderPrideDamageMul(); // Spiritmane Pride: all allies take 10% less while Athios leads (1.0 otherwise)
		// defence tags (ARMOUR/EVASION): evasion rolls first for a full dodge,
		// then armour subtracts flat - never below 1 damage (user floor 2026-10-24)
		if (Rng != null && m.stat[StatTags.EVASION] * LeaderEvaMul() > 0.0 && Rng.Randf() < m.stat[StatTags.EVASION] * LeaderEvaMul()) // Inquisitive Mind: +5% while Brook leads
		{
			m.NoteHitFlash(); // a dodge reads on the ghost-white lane, never the harm lane
			GD.Print("[10thAge] " + m.displayName + " EVASION dodge - the hit glances off");
			return;
		}
		if (m.stat[StatTags.ARMOUR] + (m.oakT > 0.0 ? 5.0 : 0.0) + LeaderArmourFlat() > 0.0) // Oakhollow Assemble!: +5 while assembled | Inspiring Leader: +1 while Wash leads
		{
			amount -= m.stat[StatTags.ARMOUR] + (m.oakT > 0.0 ? 5.0 : 0.0) + LeaderArmourFlat(); // oak armour + Inspiring Leader ride the same flat-subtract lane
			if (amount < 1.0)
				amount = 1.0; // flat reduction with a never-below-1 floor
		}
		if (m.downed)
		{
			if (m.stable)
			{
				// stable bodies stopped rolling: the next hit just slays them
				m.eliminated = true;
				m.dsColor = CHIP_GREY;
				m.SetDsText("OUT - next campfire");
				GD.Print("[10thAge] " + m.displayName + " stable body SLAIN by damage - out until campfire");
				CheckTpk();
				return;
			}
			// downed bodies still get ran into / hit by volleys: each hit is one
			// failed death save, buffered 2 s so a swarm can't insta-finish them
			if (m.dsDamageBuffer > 0.0f)
				return;
			m.dsDamageBuffer = (float)DS_DAMAGE_FAIL_BUFFER;
			m.dsFails++;
			m.NoteHitFlash(); // downed-body ran-into still reads as harm: red lane
			// locked rule made visible: damage on a downed body IS an extra failure
			GD.Print("[10thAge] damage on downed " + m.displayName + " = extra death-save fail "
					 + m.dsFails + "/" + DS_FAIL_LIMIT + " (buffered 2s)");
			if (m.dsFails >= DS_FAIL_LIMIT)
			{
				m.eliminated = true;
				m.dsColor = CHIP_GREY;
				m.SetDsText("OUT - next campfire");
				GD.Print("[10thAge] " + m.displayName + " ELIMINATED (3 fails) - returns at the next campfire");
			}
			else
			{
				m.dsColor = CHIP_BLOOD;
				m.SetDsText("hit while down: fail " + m.dsFails + "/" + DS_FAIL_LIMIT);
			}
			CheckTpk();
			return;
		}
		if (_pilot)
			amount *= 0.15; // verification-only aid for headless runs; never set in normal play
		if (m.tempHp > 0.0f) // Field Medic buffer (user 2026-11-06): the medic's over-heal soaks damage FIRST, bare
		{
			// the buffer line eats the hit up to its own depth before the body's own hp is
			// touched; armour already paid above, so this soak is flat (Game 2026-11-06)
			float soak = (amount < (double)m.tempHp ? (float)amount : m.tempHp);
			m.tempHp -= soak;
			amount -= (double)soak;
			if (_pilot)
				GD.Print("[10thAge] " + m.displayName + " Temporary HP soaks " + (int)soak + " - buffer left " + (int)m.tempHp);
		}
		// no flat mitigation left: the warding-light armour the user traded away (2026-10-23)
		// was the last armour path, so every hit bites for its full amount now
		m.hp -= (float)amount;
		m.NoteHitFlash(); // live HP loss = the red lane fires
		if (m.hp <= 0.0f)
		{
			// D fix: HP floors at 0 the moment a body falls, so overkill can neither
			// show a negative bar on the HUD strip nor poison the campfire's +25% heal
			m.hp = 0.0f;
			m.downed = true;
			m.dsTimer = 0.0f;
			m.dsFails = 0;
			// down grace (user 2026-10-24): the fall ARMS the 4 s window on the same
			// lane the fail-buffer uses, so every enemy hit inside it just returns -
			// no fail, no chip text, no extra flash (silent, as the user asked)
			m.dsDamageBuffer = (float)DS_DOWN_GRACE;
			m.SetDsText("DOWN");
			m.dsColor = CHIP_BLOOD;
			GD.Print("[10thAge] " + m.displayName + " DOWN at 0hp - d20 death saves start");
			CheckTpk();
		}
	}

	// TIER-4 OGRE blast lane (user 2026-11-05): the ogre LOCKED the slam point when it
	// marked, a second earlier. Damage rides TakeHit so the defence tags, the down grace
	// and the TPK check all behave exactly like a contact hit. Returns how many in-play
	// bodies were inside the circle, for the audit print.
	public int SlamArea(Vector2 at, float radius, double dmg)
	{
		int hits = 0;
		foreach (PartyMember m in _party)
		{
			if (m == null || !m.joined || m.eliminated)
				continue; // bench or out since camp: the blast can't touch them
			if (WrapDelta(m.GlobalPosition, at).LengthSquared() <= radius * radius)
			{
				TakeHit(m, dmg); // defence tags / down grace / TPK all inside, like contact hits
				hits++;
			}
		}
		return hits;
	}

	public bool OnReaperDown(PartyMember m)
	{
		// TEST reaper (user death-state tester): whatever it touches goes DOWN and
		// starts the d20 death-save cycle — this is the revival test bench. Returns
		// true only when the down actually landed, so immunity lets it keep lurking.
		if (m == null || !m.joined || m.eliminated || m.downed)
			return false;
		if (m.invuln > 0.0f || m.reviveShield)
			return false;
		m.hp = 0.0f;
		m.downed = true;
		m.stable = false;
		m.dsTimer = 0.0f; // the first d20 rolls right away
		m.dsFails = 0;
		m.dsSucceeds = 0;
		// the TEST reaper gets the same 4 s grace, so the revival bench behaves live
		m.dsDamageBuffer = (float)DS_DOWN_GRACE;
		m.SetDsText("DOWN");
		m.dsColor = CHIP_BLOOD;
		GD.Print("[10thAge] TEST reaper downed " + m.displayName + " - death saves start");
		CheckTpk();
		return true;
	}

	// healBeat=false is the camp BEAT (mini-boss death): fallen lifted, bodies stand up,
	// but the +25% heal waits on the rest choice so the campfire never double-heals.
	public void ReviveAllAtCampfire(bool healBeat)
	{
		foreach (PartyMember m in _party)
		{
			if (!m.joined)
				continue; // a PC not in the squad yet has nothing to lift at camp
			if (m.eliminated)
			{
				// out since the last campfire: return at 25% HP
				m.eliminated = false;
				m.campReturned = true; // long rest must not full-heal these bodies
				m.downed = false;
				m.hp = (float)(m.EffMaxHp() * 0.25);
				m.stable = false;
				m.dsFails = 0;
				m.dsSucceeds = 0;
				m.dsTimer = 0.0f;
				m.dsDamageBuffer = 0.0f;
				m.reviveProgress = 0.0f;
				m.reviveShield = false;
				// locked rule: EVERY revive path grants 2 s of post-revive invulnerability
				m.invuln = (float)REVIVE_INVULN;
				GD.Print("[10thAge] campfire revive: " + m.displayName + " returns at 25% hp + 2s invulnerable");
				m.dsColor = CHIP_SAGE;
				m.SetDsTemp("back at camp", 2.5);
				continue;
			}
			if (m.downed)
			{
				// a body at camp: stands back up, rolls reset (and any legacy negative
				// HP is floored before the +25% heal lands, so the gift is never spent
				// on an overkill debt)
				if (m.hp < 0.0f)
					m.hp = 0.0f;
				m.downed = false;
				m.stable = false;
				m.dsFails = 0;
				m.dsSucceeds = 0;
				m.dsTimer = 0.0f;
				m.dsDamageBuffer = 0.0f;
				m.reviveProgress = 0.0f;
				m.reviveShield = false;
				m.invuln = (float)REVIVE_INVULN;
				GD.Print("[10thAge] campfire stand-up: " + m.displayName + " + 2s invulnerable");
				m.dsColor = CHIP_SAGE;
				m.SetDsTemp("back at camp", 2.5);
			}
			if (healBeat)
			{
				// the campfire's locked +25% heal - applied once, by the rest choice
				m.hp = m.hp + (float)(m.EffMaxHp() * 0.25);
				if (m.hp > m.EffMaxHp())
					m.hp = (float)m.EffMaxHp();
			}
		}
	}

	public void AddXp(double value)
	{
		_xp += value;
		_xpTotal += value;
	}

	public void OnEnemyKilled(Combatant c)
	{
		if (c is Boss) // five boss nodes now (mini1 / boss1 / mini2 / boss2 / final): the class,
					   // not a kind string, decides a death is a BOSS death
		{
			OnBossKilled(c);
			return;
		}
		// field recruit: escort goblins dying IS the rescue meter (user design 2026-10-20)
		if (((Enemy)c).escort && _recruitPending != null && _escortLeft > 0)
		{
			_escortLeft--;
			GD.Print("[10thAge] escort down: " + _escortLeft + "/" + ESCORT_COUNT +
					 " left to rescue " + _recruitPending.displayName);
			if (_escortLeft <= 0)
				JoinRecruit();
		}
		// Hapi "Bring Out Yer Dead!" kill lane (user 2026-11-17): a NON-boss kill while ANY joined
		// member's 10 s conversion window is up rises THIS corpse with its own stats. Escort
		// deaths already fed the rescue meter above, so they read BOTH lanes. The leader bonus
		// (Call of the Grave) is the separate 5% half-rise lane below.
		bool risenUp = false; // is any conversion window open across the squad?
		foreach (PartyMember m in _party)
		{
			if (m.joined && m.convT > 0.0)
			{
				risenUp = true;
				break;
				}
			}
		if (risenUp)
		{
			SpawnUndead(c, false); // conversion rise: fights beside the squad AND taunts it (2026-11-18 hand-play)
			}
		else if (LeaderBonusKey() == 11 && Rng.Randf() < RISE_CHANCE)
		{
			SpawnUndead(c, true); // risen HALF: half stats, and it taunts the nearby horde
			}
		// Guardian Awakening (Alvora ult, user 2026-10-26): every HORDE kill while an ally's
		// awakening window is up heals ONE RANDOM standing ally +1 hp (user 2026-11-06: it
		// used to heal the whole standing squad) - same eligibility the spirit heals use
		// (joined / not downed / not eliminated, clamp to EffMaxHp) - and
		// tallies on the awakening owner so its expiry print can report the haul.
		// Boss kills routed to OnBossKilled above and never count.
		foreach (PartyMember m in _party)
		{
			if (m.awaT > 0.0)
			{
				m.awaKills += 1;
				// one random standing ally per kill (user 2026-11-06): gather the eligible squad,
				// draw ONE patient - the awakening no longer heals everybody
				PartyMember[] cands = new PartyMember[13]; // roster-wide bound (13 PCs once seat 12 Hew joins)
				int cn = 0;
				foreach (PartyMember mm in _party)
				{
					if (!mm.joined || mm.downed || mm.eliminated)
						continue; // fallen bodies keep their own recovery lanes
					cands[cn++] = mm; // standing: eligible to be the one random patient
				}
				if (cn > 0)
				{
					int q = (int)(Rng.Randf() * (float)cn); // pick the one patient
					if (q >= cn)
						q = cn - 1; // belt-and-braces: Randf is < 1 so this never fires
					PartyMember pat = cands[q];
					pat.hp += 1.0f;
					if (pat.hp > pat.EffMaxHp())
						pat.hp = (float)pat.EffMaxHp(); // clamp at own cap: temp HP is the medic lane only
					if (_pilot)
						GD.Print("[10thAge] (ult) awakening kill-heal: " + m.displayName + " healed " + pat.displayName + " +1 hp (one random ally per kill)");
				}
				break; // exactly one awakening owner per kill
			}
		}
		UpgradePartDrop(c.GlobalPosition); // Hew's Upgrades! lane: a 5% part ON this kill — the site rides in so the approved FX can drop a cog where it landed
		SpawnGem(c.GlobalPosition, ((Enemy)c).xpValue);
		MaybeDropPickup(c); // rare kill drop, before the body leaves the registry
		c.kind = "dead"; // drop the registry entry in the kill scan
		ReleaseLiveCount((Enemy)c); // ownership-flagged: never double-decrements
		c.QueueFree();
	}

	public void OnBossKilled(Combatant b)
	{
		string kb = b.kind; // capture before we overwrite it below
		// slain bosses must despawn visually but NOT QueueFree: _bosses and the
		// scene still reference them. kind "dead" drops the registry entry in the
		// kill scan (so nothing targets it again); the body parks offscreen AND goes
		// hidden - in a wrapping world "offscreen" absolute coords no longer exist,
		// a long-enough walk loops right back to them
		b.kind = "dead";
		((Boss)b).active = false;
		b.Visible = false;
		b.GlobalPosition = new Vector2(0.0f, -10000.0f);
		// the boss a beat engaged decides what its death MEANS. With one PLAY state covering
		// every fight, the route cannot come from the stage state any more: it comes from the
		// kind the scene authored. User 2026-11-03: every boss kind -> camp (mini1,
		// mini2, boss1, final); the final-boss camp still leads on to WRAP-UP -> VICTORY.
		// EVERY boss death opens the camp now. boss1 and final were
		// normalized to the mini-boss, and the user explicitly asked for campfire after a
		// main boss too. The final-boss camp still leaves the 420 s WRAP-UP row to raise
		// VICTORY once the camp clears.
		if (kb == "mini1" || kb == "mini2" || kb == "boss1" || kb == "boss2" || kb == "final")
		{
			_state = StageState.CAMPFIRE;
			_campWaitT = 0.0;
			// fx-feel9 (user 2026-11-02: "make a change to the campfire. It can despawn projectiles,
			// but don't despawn any enemies"): the camp sweep NO LONGER clears the horde - the beat
			// sweeps IN-FLIGHT BULLETS only (the camp-safety gate stays: a player reading the card
			// cannot be shot mid-decision). Whatever marched into camp keeps marching out of it,
			// and the escort-recruit beat that used to hide inside the sweep keeps running normally
			// now that its bodies are never swept here, so there is nothing to rescue.
			// ClearEnemies(); REMOVED per user (the camp no longer despawns enemies)
			// user-approved camp-safety gate: the camp beat is a read-the-card moment,
			// so every in-flight boss bullet is swept before the grimoire goes up - a
			// player absorbing the rest choice cannot be shot mid-decision.
			ClearBullets();

			// fx-feel10: shield discs are per-member flight STATE, not bullets, so the camp
			// sweep ends live throws here too - nothing keeps flying across the rest beat.
			foreach (PartyMember pm in _party)
				pm.EndAllThrows();
			GD.Print("[10thAge] (camp) the campfire sweeps BULLETS only - horde kept: ", _enemies.Count, " bodies on the field");
			ReviveAllAtCampfire(false);
			GD.Print("[10thAge] boss slain -> camp: the fallen return at 25% hp; the rest choice decides healing");
			if (kb == "mini1")
			{
				// HORDE EVENT: boot now arms the schedule at HORDE_EVENT_FIRST_AT absolute, so this legacy
				// after-mini-boss lane stays only as a fallback for a future map that does NOT arm at boot -
				// it must never push an already-armed deadline later
				if (_hordeEventDue <= 0.0)
				{
					_hordeEventDue = GetRunTime() + HORDE_EVENT_FIRST_DELAY;
					GD.Print("[10thAge] (horde) first mini-boss down - bonus swarms armed: first 30 s out, every 120 s after");
				}
			}
		}
	}

	public Boss GetActiveBoss()
	{
		foreach (Boss b in _bosses)
		{
			if (b.active && b.hp > 0.0f)
				return b;
		}
		return null;
	}

	public void ActivateBoss(string kind)
	{
		// P1: a boss only joins the hittable registry when its stage engages. Before
		// that it is parked far offscreen, so weapons can never pre-kill it (pre-killing
		// the mini used to softlock the run) and nothing can target it.
		Vector2 near = _leader != null ? _leader.GlobalPosition : new Vector2(0, 0);
		foreach (Boss b in _bosses)
		{
			if (b.kind != kind || b.active || b.hp <= 0.0f)
				continue;
			b.active = true;
			b.Visible = true; // parked bosses start HIDDEN now (see Boss._EnterTree):
							  // in a looping world absolute offscreen parking is not a
							  // hiding spot, so visibility is the real gate
			// user-approved camp-safety gate: the engage itself is a 2 s free window
			// (TakeHit drops everything while this runs), then normal damage resumes.
			_engageInvulnT = REVIVE_INVULN;
			GD.Print("[10thAge] boss " + kind + " engaged - 2s party immunity window so the "
					 + "arrival cannot open on a free hit");
			bool registered = false;
			ulong id = b.GetInstanceId();
			foreach (Godot.Node2D n in _enemies)
			{
				if (n.GetInstanceId() == id)
				{
					registered = true;
					break;
				}
			}
			if (!registered)
			{
				// drop in just outside the camera view so the player sees it arrive
				Vector2 entry = WrapNearCamera(near + new Vector2(0.0f, -430.0f));
				b.GlobalPosition = entry;
				_enemies.Add(b);
				GD.Print("[10thAge] boss " + kind + " deployed at " + (int)b.GlobalPosition.X + ","
						 + (int)b.GlobalPosition.Y);
			}
		}
	}

	// XP orb. 2026-11-17 user rail part 1: a starved kill no longer drops nothing - the pool is
	// fixed, so the XP folds into the NEAREST orb already on the field instead of being lost. XP
	// is awarded at PICKUP, so a carrier paying the sum is the honest read; below saturation a
	// free slot is always found and this is exactly the shipped path. part 2 (same day): MergeGemClusters
	// folds whole clusters once the pool nears capacity, so the field never runs dry.
	public void SpawnGem(Vector2 pos, double value)
	{
		// the shipped free-slot scan, unchanged
		for (int i = 0; i < _gems.Count; i++)
		{
			Godot.Node2D gem = _gems[i];
			if (gem.GlobalPosition.X < -8000.0f)
			{
				gem.GlobalPosition = pos;
				_gemVals[i] = value;
				PaintGem(i); // tier hue for what it carries (user FX rail)
				Godot.ColorRect cr = (Godot.ColorRect)gem.GetChild(0);
				cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f); // alpha rides Modulate, as shipped
				return;
			}
		}
		// pool is full: hand the XP to the NEAREST orb instead of losing it outright (user 2026-11-17)
		int best = -1;
		double bestD = 1e30;
		for (int q = 0; q < _gems.Count; q++)
		{
			if (_gems[q].GlobalPosition.X <= -8000.0f)
				continue; // free slot: nothing to fold into
			double d = _gems[q].GlobalPosition.DistanceTo(pos);
			if (d < bestD)
			{
				bestD = d;
				best = q;
			}
		}
		if (best >= 0)
		{
			_gemVals[best] += value; // the carrier now pays the whole sum at pickup
			PaintGem(best); // and shows it: a folded orb re-reads its tier
		}
	}

	// ---------- field pickups: the drop rail (user 2026-11-08) ----------
	// RARE KILL DROPS on the shipped stage only: PICKUP_CH rolls once per ordinary enemy kill,
	// then a weighted flavour pick. Escort kills NEVER drop: those bodies ARE the rescue meter,
	// and a loot cage at the rescue site would clutter the beat the escort beat already pressures.
	public void MaybeDropPickup(Combatant c)
	{
		if (_stage != 0)
			return; // stage 1 only: the test room and any future plate stay clean
		if (((Enemy)c).escort)
			return; // escort bodies run the rescue meter, never a loot drop
		if (Rng.Randf() >= (float)PICKUP_CH)
			return; // the rare roll: the same rail the creature lanes use (assistant pick)
		double roll = Rng.Randf() * 100.0; // weighted flavour pick (assistant pick)
		int pk = roll < 48.0 ? 0 : (roll < 63.0 ? 1 : 2); // 48 heal orb | 15 xp magnet (HALVED user 2026-11-12 hand-play: "reduce the number of magnet spawns by 50%") | 37 rage vial
		SpawnPickup(c.GlobalPosition, pk);
	}
	
	public void SpawnPickup(Vector2 pos, int pk, int kind = 0)
	{
		Pickup p = new Pickup(); // allocated per drop, like a horde body: pickups are rare
		p.pk = pk;
		p.gearKind = kind; // ROUND 8: which Hew gear the pk 3 cog is (0 dmg | 1 spd | 2 rad | 3 arm | 4 hp); 0 for the other three flavours
		AddChild(p);
		p.GlobalPosition = pos; // drops where the body died; Pickup.cs folds it like the gems
		p.InitVisuals();
		string label = pk == 0 ? "heal orb" : pk == 1 ? "xp magnet" : pk == 3 ? "gear part" : "rage vial"; // ROUND 8: the gear cog flavour joins the read
		GD.Print("[10thAge] (pickup) " + label + " drops on the field");
	}
	
	public void SpawnBullet(Vector2 pos, Vector2 vel, double dmg, bool bossShot = false)
	{
		for (int i = 0; i < _bullets.Count; i++)
		{
			Godot.Node2D b = _bullets[i];
			if (b.GlobalPosition.X < -8000.0f)
			{
				b.GlobalPosition = pos;
				_bulletVel[i] = vel;
				_bulletDmg[i] = dmg;
				_bulletSpin[i] = 0.0; // ordinary volley: no tumble
				_bulletBoss[i] = bossShot; // BOSS SHOT LAW: Boss.cs passes true, every other lane stays false
				b.Rotation = 0.0f; // a recycled slot may still hold a thrown goblin tumble
				Godot.ColorRect cr = (Godot.ColorRect)b.GetChild(0);
				cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f);
				cr.Color = new Color(1.0f, 0.25f, 0.2f, 1.0f); // shell back to drawing: an arrow spawn zeroes its alpha
				((Godot.Sprite2D)cr.GetChild(0)).Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f); // sink any arrow an earlier volley left riding
				((Godot.Sprite2D)cr.GetChild(0)).Scale = new Vector2(1.0f, 1.0f); b.Scale = new Vector2(1.0f, 1.0f); // tier-3 fireballs ride this pool scaled up, so every reuse normalises BOTH scales

				return;
			}
		}
	}

	public void SpawnBulletArrow(Vector2 pos, Vector2 vel, double dmg)
	{
		SpawnBulletArrow(pos, vel, dmg, false); // the party's mini fans keep the plain lane
	}

	public void SpawnBulletArrow(Vector2 pos, Vector2 vel, double dmg, bool redOutline, bool bossShot = false)
	{
		// Same bullet pool; this variant rides the arrow fx sprite rotated into flight
		// instead of the red square (2026-11-02 mini dance: user asked for arrow fx on the
		// mini's fans). BOTH spawn lanes normalise the square/arrow pair on every reuse,
		// so a recycled slot can never inherit the other lane's look. No art on disk =
		// the square keeps drawing, exactly like the party-shot lane.
		for (int i = 0; i < _bullets.Count; i++)
		{
			Godot.Node2D b = _bullets[i];
			if (b.GlobalPosition.X < -8000.0f)
			{
				b.GlobalPosition = pos;
				_bulletVel[i] = vel;
				_bulletDmg[i] = dmg;
				_bulletSpin[i] = 0.0; // ordinary volley: no tumble
				_bulletBoss[i] = bossShot; // boss run-in fans flag true; riders take the false default
				b.Rotation = 0.0f; // a recycled slot may still hold a thrown goblin tumble
				Godot.ColorRect cr = (Godot.ColorRect)b.GetChild(0);
				if (!_fxTried)
				{
					_fxTried = true;
					_fxArrowTex = GD.Load("res://art/fx/arrow.png") as Godot.Texture2D;
					_fxFlameTex = GD.Load("res://art/fx/firebolt.png") as Godot.Texture2D;
					_fxIceTex = GD.Load("res://art/fx/ice_spike.png") as Godot.Texture2D; // 2026-11-13: an arrow loose BEFORE any mage volley still loads ice / mage tex - one try-once flag for all
				}
				Godot.Sprite2D fxs = (Godot.Sprite2D)cr.GetChild(0);
				b.Scale = new Vector2(1.0f, 1.0f); fxs.Scale = new Vector2(1.0f, 1.0f); // shed any fireball scale from an earlier mage volley
				// OUTLINE COLLAR: a second shell child at RELATIVE z -1 (grown at most once per
				// pooled slot and normalised on every reuse below). The rider's volley then reads
				// as an ENEMY projectile at a glance; the party's mini fans keep the plain lane.
				Godot.Sprite2D outl = null;
				if (cr.GetChildCount() > 1)
					outl = (Godot.Sprite2D)cr.GetChild(1);
				else if (redOutline && _fxArrowTex != null)
				{
					outl = new Godot.Sprite2D();
					outl.Set("texture_filter", 0); // NEAREST like the shaft and the minis
					outl.Position = new Vector2(6.0f, 6.0f); // Control origin is the rect corner -> centred
					outl.ZIndex = -1; // relative: behind the shaft, still above the bodies
				// the collar rides UNDER the shaft: a fatter copy of the SAME arrow art, so
				// programming (not new art) draws the red edge the playtest asked for
				// (user 2026-11-06: "the arrows should have a red outline")
					cr.AddChild(outl);
				}
				if (_fxArrowTex != null)
				{
					float ang = (float)System.Math.Atan2((double)vel.Y, (double)vel.X); // arrow points where it flies
					fxs.Texture = _fxArrowTex;
					fxs.Rotation = ang;
					fxs.Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f);
					// the collar copies the shaft's transform and rides deeper red; the plain
					// lane hides whatever a rider left in this recycled slot
					if (outl != null)
					{
						if (redOutline)
						{
							outl.Texture = _fxArrowTex;
							outl.Rotation = ang;
							outl.Scale = new Vector2(1.5f, 2.1f); // fatter than the 20x8 shaft: the edge peeks out
							outl.Modulate = new Color(0.85f, 0.12f, 0.10f, 1.0f); // deep red: the ENEMY-projectile read
						}
						else
							outl.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f); // plain lane: shed the rider's collar
					}
					cr.Color = new Color(0.0f, 0.0f, 0.0f, 0.0f); // shell stops drawing while art rides
					cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f);
				}
				else
				{
					// no art on disk (headless bench): the shell square carries the read,
					// and a RED shell when the rider lane asked for the outline
					cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f);
					cr.Color = redOutline ? new Color(0.72f, 0.12f, 0.10f, 1.0f) : new Color(1.0f, 0.25f, 0.2f, 1.0f);
					fxs.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
					if (outl != null)
						outl.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f); // no art: the shell carries the read
				}
				return;
			}
		}
	}

	public void SpawnBulletFire(Vector2 pos, Vector2 vel, double dmg)
		{
		SpawnBulletFire(pos, vel, dmg, 1.04f, false); // 2026-11-13 legacy 3-arg rail: the 1.04 fireball scale + no-ice default stay the DEFAULT
		}

	public void SpawnBulletFire(Vector2 pos, Vector2 vel, double dmg, float msIn, bool iceArt) // 2026-11-13 ice FX overload: iceArt picks art/fx/ice_spike.png (authored LEFT, so the lane adds PI rotation); mage2's THREE ICE SPIKES ride the SAME lane at a SMALLER scale (ice FX lands in a later pass); ordinary missiles stay shatterable
		{
		// same bullet pool; the mage fireball rides Stack's firebolt FX scaled UP
		// (user 2026-11-03 tier-3 ask: "you can use a large version of the existing fire
		// bolt FX"). No art on disk = fat orange square, same honest-fallback discipline
		// as the arrow lane. Motion/hit lanes read the node POSITION, so the scale is
		// visual-only - the hitbox stays the standard bullet hitbox (assistant pick).
		for (int i = 0; i < _bullets.Count; i++)
		{
		Godot.Node2D b = _bullets[i];
		if (b.GlobalPosition.X < -8000.0f)
		{
		b.GlobalPosition = pos;
		_bulletVel[i] = vel;
		_bulletDmg[i] = dmg;
		_bulletSpin[i] = 0.0; // ordinary volley: no tumble
		_bulletBoss[i] = false; // mage fireballs are ordinary missiles: shatterable
		b.Rotation = 0.0f; // a recycled slot may still hold a thrown goblin tumble
			if (!_fxTried) // first fireball may precede any arrow: same try-once art discipline
			{
				_fxTried = true;
				_fxArrowTex = GD.Load("res://art/fx/arrow.png") as Godot.Texture2D;
				_fxFlameTex = GD.Load("res://art/fx/firebolt.png") as Godot.Texture2D;
				_fxMageTex = GD.Load("res://art/fx/mage_fireball.png") as Godot.Texture2D;
				_fxIceTex = GD.Load("res://art/fx/ice_spike.png") as Godot.Texture2D; // 2026-11-13: same discipline from the fireball side
			}
		Godot.ColorRect cr = (Godot.ColorRect)b.GetChild(0);
		Godot.Sprite2D fxs = (Godot.Sprite2D)cr.GetChild(0);
		Godot.Texture2D mfx = iceArt && _fxIceTex != null ? _fxIceTex : _fxMageTex != null ? _fxMageTex : _fxFlameTex; // 2026-11-08 art pass: mage rides its OWN fireball; 2026-11-13: iceArt rides the picked ice_spike.png first, firebolt stays the last fallback; user 2026-11-18 hand-play sighted FIREBALLS on the frost mage: the rail is provably wired (art tracked since 9f365f6, both try-once loaders + this pick verified) - prime suspect is a stale seat dll / un-imported .ctex; if it persists with a fresh import, chase _fxIceTex == null at runtime (the fallback below paints orange when ice is null)
		if (mfx != null)
		{
			fxs.Texture = mfx;
			float ms = (iceArt ? _fxIceTex != null : _fxMageTex != null) ? msIn : 2.4f; // 2026-11-13: own art per lane (ice for spikes / mage fireball otherwise) owns msIn; fallback stays the fat firebolt read // 2026-11-13: msIn owns the scale (mage2 spikes ride MAGE2_FIRE_MS 0.62); fallback stays the fat firebolt read // 2026-11-09 hand-play (user: "when goblin mages fire their fireball, it needs to be just a little bit smaller"): 1.5 drew the 40x15 art at 60x22 px, now 52x20 px; 2026-11-12 hand-play: still a touch big, 20% off = 1.04 (~42x16). Assistant knob - retunable
			fxs.Scale = new Vector2(ms, ms);
			fxs.Modulate = (iceArt ? _fxIceTex != null : _fxMageTex != null) ? new Color(1.0f, 1.0f, 1.0f, 1.0f) : new Color(0.70f, 0.15f, 0.11f, 1.0f); // new art carries its own hues; fallback keeps the dark-red ENEMY read
			fxs.Rotation = (float)System.Math.Atan2((double)vel.Y, (double)vel.X) + (iceArt && _fxIceTex != null ? 3.14159265f : 0.0f); // 2026-11-13: +PI rides the LEFT-pointing ice spike so the tip leads travel; fireball keeps head-forward at 0 // 2026-11-09 hand-play (user: "the FX needs to rotate so it fires pointed at the players. Currently it's just static facing right"): measured, art/fx/mage_fireball.png IS authored head-forward - the 40x15 canvas tapers to a 1 px tail at x=0 and its 15 px head mass sits at x=26-34 - so riding the travel vector aims the head at the party, the same rail the arrow lane already uses
			cr.Color = new Color(0.0f, 0.0f, 0.0f, 0.0f); // shell stops drawing while art rides
			// user 2026-11-07 (fireball HIT but drew NOTHING): the pool parks its shell Modulate at
			// alpha 0 (build + DespawnBullet) and modulate CASCADES to the art child - the arrow
			// lane wakes it on every reuse, the fallback already did below, only this art lane
			// forgot, so every mage fireball rode an alpha-0 shell. ROOT CAUSE, one line:
			cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f); // wake the shell so the firebolt art draws
			b.Scale = new Vector2(1.0f, 1.0f);
			// both spawn lanes normalise the pooled outline child, so a recycled slot never inherits
			// the other lane's look: a fireball hides whatever collar a rider left in this slot
			if (cr.GetChildCount() > 1)
			{
				Godot.Sprite2D ghost = (Godot.Sprite2D)cr.GetChild(1);
				ghost.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
			}
			}
			else
			{
			b.Scale = new Vector2(2.4f, 2.4f); // fat fallback square: motion is manual, scale reads only in the draw
			cr.Color = new Color(0.62f, 0.13f, 0.10f, 1.0f); // dark red fallback square: the ENEMY-projectile read (user 2026-11-06)
			cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f);
			fxs.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
			// a rider's outline collar may ride this recycled slot: hide it when the fire lane reuses it
			if (cr.GetChildCount() > 1)
			{
				((Godot.Sprite2D)cr.GetChild(1)).Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
			}
		}
		return;
		}
		}
	}
	
	// OGRE2 HURL LANE (user 2026-11-11: ogre2 "will pick them up and throw them at the
	// player"): the hurled goblin rides the SAME enemy missile pool as every volley, goblin1
	// canvas art on it (art/enemies/ is untracked user WIP: no art on disk = the goblin-green
	// fallback square, the honest discipline every art lane carries). The melee trio reads
	// ALL live _bullets slots, so a flying goblin is SMASHABLE for free - the free ride the
	// QoL pass already won the melee party. Motion / hit lanes read the node POSITION like
	// every volley (bite 12 + MEMBER_RADIUS), the art scale is visual-only.
	public void SpawnBulletThrown(Vector2 pos, Vector2 vel, double dmg, double spin)
	{
		for (int i = 0; i < _bullets.Count; i++)
		{
			Godot.Node2D b = _bullets[i];
			if (b.GlobalPosition.X < -8000.0f) // pooled slots park at X < -8000: the free-slot read
			{
				b.GlobalPosition = pos;
				_bulletVel[i] = vel;
				_bulletDmg[i] = dmg;
				_bulletSpin[i] = spin; // the tumbles pinned to this slot
				_bulletBoss[i] = false; // thrown goblins are ordinary missiles: shatterable
				b.Rotation = 0.0f; // the tumble starts upright, like every launch
				if (!_fxThrownTried) // try-once art discipline, OWN flag (see note above)
				{
					_fxThrownTried = true;
					_fxThrownTex = GD.Load("res://art/enemies/stage1/goblin1.png") as Godot.Texture2D;
				}
				Godot.ColorRect cr = (Godot.ColorRect)b.GetChild(0);
				Godot.Sprite2D fxs = (Godot.Sprite2D)cr.GetChild(0);
				if (_fxThrownTex != null)
				{
					fxs.Texture = _fxThrownTex;
					float ts = 46.0f / (float)_fxThrownTex.GetHeight(); // SAME 46 px target a standing goblin rides (user 2026-11-12 hand-play: "I noticed the goblin shrinks when the ogre grabs it, keep it at its normal size")
					fxs.Scale = new Vector2(ts, ts); // recycled slot: normalise, no inherited fireball scale
					fxs.Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f); // wake the art child
					fxs.Rotation = 0.0f; // a recycled slot may still be aimed like an arrow: the goblin tumbles upright
					cr.Color = new Color(0.0f, 0.0f, 0.0f, 0.0f); // shell stops drawing while art rides
					cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f); // wake the shell: modulate CASCADES to the art child (the fireball lesson)
					b.Scale = new Vector2(1.0f, 1.0f);
					if (cr.GetChildCount() > 1)
					{
						Godot.Sprite2D ghost = (Godot.Sprite2D)cr.GetChild(1);
						ghost.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f); // hide whatever collar a rider left in this slot
					}
				}
				else
				{
					b.Scale = new Vector2(1.8f, 1.8f); // fat goblin-green fallback: a thrown body, not a pellet
					cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f); // wake the shell (the cascade lesson again)
					cr.Color = new Color(0.30f, 0.48f, 0.23f, 1.0f); // goblin green: the ENEMY-projectile read this lane owns
					fxs.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f); // sink whatever art an earlier volley left riding
					if (cr.GetChildCount() > 1)
					{
						((Godot.Sprite2D)cr.GetChild(1)).Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
					}
				}
			return;
			}
		}
	}
	
	public int GetLiveEnemies()
	{
		return _liveEnemies;
	}

	public Godot.Collections.Array<Godot.Node2D> GetEnemies()
	{
		return _enemies;
	}

	// the live-enemy counter is owned by the enemy itself (Enemy.counted): every exit
	// path (weapon kill, self-despawn like the TEST reaper, ClearEnemies) calls
	// ReleaseLiveCount once, so the cap can never leak upward and stop binding.
	public void ReleaseLiveCount(Enemy e)
	{
		if (e.counted)
		{
			e.counted = false;
			_liveEnemies--;
			if (_liveEnemies < 0)
				_liveEnemies = 0;
		}
	}

		public double GetHordeEventDue()
	{
		return _hordeEventDue;
	}

	// one event's worth forward after each swarm lands (the Director owns placement and calls this)
	public void AdvanceHordeSchedule()
	{
		_hordeEventDue += HORDE_EVENT_EVERY;
	}

	// ---------- separation spatial hash: rebuild + query ----------
	// Rebuild runs at the TOP of _Process, before any rail can early-return: parent-first
	// processing means the children (enemies) read the grid only after this ran. The world
	// torus-wraps at ~2400 px, so shoves stay in raw global coords - cells are keyed straight
	// off those, no wrap math, which keeps the push identical to the old rail's.
	public void RebuildSeparationGrid()
	{
		if (_sepHead.Count < SEP_SLOTS)
		{
			// one-time grow to the fixed slot count: the table never reallocates after this
			while (_sepHead.Count < SEP_SLOTS)
				_sepHead.Add(-1);
		}
		for (int i = 0; i < SEP_SLOTS; i++)
			_sepHead[i] = -1;
		_sepCount = 0;
		for (int i = 0; i < _enemies.Count; i++)
		{
			if (_sepNext.Count <= _sepCount)
			{
				// the arrays only ever grow to the high-mark census, then refill in place
				_sepBody.Add(_enemies[i]);
				_sepNext.Add(-1);
			}
			else
				_sepBody[_sepCount] = _enemies[i];
			Godot.Node2D n = _enemies[i];
			int cx = (int)System.Math.Floor(n.GlobalPosition.X / SEP_CELL);
			int cy = (int)System.Math.Floor(n.GlobalPosition.Y / SEP_CELL);
			int h = SepCellKey(cx, cy);
			_sepNext[_sepCount] = _sepHead[h];
			_sepHead[h] = _sepCount;
			_sepCount++;
		}
	}

	private int SepCellKey(int cx, int cy)
	{
		// big-prime mix then mask: coords go negative west/north, & keeps the slot in range
		return (int)((cx * 73856093L + cy * 19349663L) & (long)SEP_MASK);
	}

	// the query side Enemy uses: head of a cell's chain (-1 = empty), then chain-walk
	// through SepNext. Guarded because a body can process before the first rebuild lands.
	public int SepCellHead(int cx, int cy)
	{
		return _sepHead.Count > 0 ? _sepHead[SepCellKey(cx, cy)] : -1;
	}

	public Godot.Node2D SepBody(int i)
	{
		// the per-frame snapshot, NOT the registry: bodies killed later in the frame are gone
		// from _enemies and the chain index would read past its end
		return _sepBody[i];
	}

	public int SepNext(int i)
	{
		// -1 ends the chain
		return _sepNext[i];
	}

	// HORDE EVENT bodies (user: "won't count against the maximum number of enemies that can spawn"): the
	// same no-budget lane the boss nodes use - never counted, so ReleaseLiveCount is inert for them on
	// every exit path and the director's cap keeps seeing only ordinary horde bodies
	public void RegisterHordeBody(Enemy e)
	{
		_enemies.Add(e);
		e.counted = false;
	}

public void RegisterEnemy(Combatant e)
	{
		_enemies.Add(e);
		if (!(e is Boss)) // bosses (any of the four) never count toward the horde cap
		{
			((Enemy)e).counted = true;
			_liveEnemies++;
		}
	}

	public void ClearEnemies()
	{
		for (int i = _enemies.Count - 1; i >= 0; i--)
		{
			Combatant c = (Combatant)_enemies[i];
			if (c == null)
			{
				_enemies.RemoveAt(i);
				continue;
			}
			if (c is Boss || c.kind == "dead")
				continue; // the camp sweep clears the horde, never a boss node
			c.kind = "dead";
			ReleaseLiveCount((Enemy)c);
			c.QueueFree();
			_enemies.RemoveAt(i);
		}
		// the camp sweep must not orphan a field recruit: whatever escort cage survived
		// the mini-boss dies with the horde, so the party regroups and the PC joins at
		// camp instead of soft-locking an unreachable rescue on the rest of the stage
		if (_recruitPending != null && _escortLeft > 0)
			JoinRecruit();
	}

	public int GetStageState()
	{
		return (int)_state;
	}

	// seconds into the CURRENT beat leg: the horde density curve ramps inside a leg, and a
	// picker / join pause must not advance it (the run clock it rides on already holds).
	public double GetStageT()
	{
		double t = _runT - _beatT0;
		return t < 0.0 ? 0.0 : t;
	}

	// TOTAL run-seconds (the clock the beat table keys on): the HUD shows THIS, never
	// GetStageT - the leg clock resets every row, and that reset read made the first
	// mini-boss look "way too early" (human complaint 2026-10-22).
	public double GetTimeWarp()
	{
	return _timeWarp;
	}
	
	public double GetRunTime()
	{
		return _runT;
	}

	// how many bodies the squad has in play (JOINED, not eliminated; downed ones still
	// count - they are squad and they return): the horde scales with this (user rule
	// 2026-10-22, human playtest: solo Baldrun was swamped by a full-sized horde).
	// Never below 1: something must still be on the menu.
	public int GetPartyInPlay()
	{
		int n = 0;
		foreach (PartyMember pm in _party)
			if (pm.joined && !pm.eliminated)
				n++;
		return n < 1 ? 1 : n;
	}

	// length of the current beat leg (next beat time - leg start). Replaces M1's FIRST_HALF /
	// SECOND_HALF: the beat table says how long each leg is, so the ramp follows the schedule.
	public double GetStageLen()
	{
		if (_beatIdx + 1 < BEAT_T.Length)
			return BEAT_T[_beatIdx + 1] - _beatT0;
		return BEAT_T[_beatIdx] - _beatT0 + 60.0;
	}

	// the horde tier the stage is running at right now (user rule: the beat table gates
	// variety, and an ally beat's escorts come from whatever tier is live that frame)
	public double GetHordeTier()
	{
		return _hordeTier;
	}

	// the horde marches in PLAY (M1's boss states are folded in), and stops for camp /
	// death / victory. The Director consumes this instead of M1's numeric stage codes.
	public bool GetHordeActive()
	{
		return _state == StageState.PLAY;
	}

	// a boss beat's fight holds the NEXT beat back until it is resolved, so the stage
	// cannot stack a mini-boss on top of a live mid boss
	public bool GetBossLive()
	{
		return GetActiveBoss() != null;
	}

	// the scheduler's hold is STRICTER than the hittable check above: a boss whose hp has
	// crossed zero but whose kill routing has not run yet (OnBossKilled is what clears
	// active, and it runs in the kill scan, after the beat scheduler in the same frame)
	// still owns the arena. Without this the row after a boss beat can slip in during that
	// one-frame window - which is exactly what used to print "final boss engaged" BEFORE
	// "mini-boss slain -> camp".
	private bool BossPending()
	{
		foreach (Boss b in _bosses)
		{
			if (b.active)
				return true; // engaged, not yet routed
		}
		return false;
	}

	// ---------- beat scheduler (M2 stage loop) ----------
	// Rows fire in table order on the RUN clock. A row fires at most once (_nextBeat only
	// ever moves forward). Two honest stalls keep the loop from stacking on itself:
	//   * a LIVE boss holds the next row back - the fight must be resolved before the
	//     stage advances (this is what M1's MINIBOSS/BOSS states did with a state change);
	//   * an unresolved escort rescue holds the NEXT ally row back, so two stranded PCs
	//     never share the one rescue site. ESCORT_GIVEUP is the valve: if the party abandons
	//     the cage, the PC joins at the floored HP and the stage keeps moving.
	private void AdvanceBeats()
	{
		while (_nextBeat < BEAT_T.Length && _runT >= BEAT_T[_nextBeat])
		{
			int kind = BEAT_KIND[_nextBeat];
			if (BossPending())
				break; // a live (or slain-but-unrouted) boss owns the arena: no new beat over it
			if (kind == 0 && _recruitPending != null)
				break; // an unresolved rescue owns the stage
			_beatIdx = _nextBeat;
			_beatT0 = _runT;
			if (BEAT_TIER[_nextBeat] > 0)
				_hordeTier = BEAT_TIER[_nextBeat];
			if (kind == 0)
			{
				// the per-run join lottery drew this slot's PC at boot (user 2026-10-23:
				// dynamic runs). wantIdx -1 means the weighted pool ran dry: logged no-op.
				// _allyBeatIdx counts from 1 (0 = none fired), so the lottery slot is idx-1
				_allyBeatIdx++;
				int wantIdx = (_allyBeatIdx - 1) < _joinOrder.Length ? _joinOrder[_allyBeatIdx - 1] : -1;
				PartyMember pc = null;
				foreach (PartyMember pm in _party)
				{
					if (pm.memberIndex == wantIdx && !pm.joined)
					{
						pc = pm;
						break;
					}
				}
				if (pc != null)
				{
					SpawnFieldRecruit(pc);
					GD.Print("[10thAge] beat " + (int)BEAT_T[_beatIdx] + "s: ally " + _allyBeatIdx
							 + " (" + pc.displayName + ") is stranded off camera, escort cage live");
				}
				else
					GD.Print("[10thAge] beat " + (int)BEAT_T[_beatIdx] + "s: ally " + _allyBeatIdx
							 + " skipped - no such PC waiting in the roster yet");
			}
			else if (kind == 1)
				GD.Print("[10thAge] beat " + (int)BEAT_T[_beatIdx] + "s: horde tier "
						 + (int)_hordeTier + " gate");
			else if (kind == 5)
			{
				// user rule (2026-10-23): after the final boss the run keeps rolling to a
				// WRAP-UP beat (420 s on the current lock - data, keep it in the table) so the full roster and the fully scaled horde can be
				// tested at strength. This row is what raises VICTORY - and because BossPending
				// holds the row back, it only lands once `final` is truly slain, so "cleared"
				// stays honest even if the boss was still alive at the wrap time.
				_state = StageState.VICTORY;
				PrintSummary("VICTORY — " + StageBadge() + " cleared");
			}
			else
			{
				// 2 = mini (kill -> camp) | 3 = mid boss (kill -> keep marching) | 4 = final
				// (kill -> keep marching too; the wrap-up row ends the run)
				_bossBeat = _nextBeat;
				ActivateBoss(BEAT_BOSS[_nextBeat]);
				GD.Print("[10thAge] beat " + (int)BEAT_T[_beatIdx] + "s: " + BEAT_BOSS[_nextBeat]
						 + " engaged");
				_nextBeat++;
				break; // the fight owns the stage from here: the row AFTER it waits for
						// GetBossLive() at the loop top, so a slain boss can never re-fire
						// this row (that would reset the leg clock and spam the engage print)
			}
			_nextBeat++;
		}
	}

	// dev/pilot skip helper: park the run clock ON the next unfired beat of a kind so the
	// real path engages it next frame. Returns the run clock to park at (unchanged if that
	// beat already fired or no such beat exists).
	private double ParkNextBeat(int kind)
	{
		for (int i = _nextBeat; i < BEAT_T.Length; i++)
		{
			if (BEAT_KIND[i] != kind || _runT >= BEAT_T[i])
				continue;
			return BEAT_T[i];
		}
		return _runT;
	}

	// HUD tag for the leg the player is standing in, so "horde I / MINI-BOSS" reads the
	// schedule instead of M1's fixed five-state chain
	private string BeatLabel()
	{
		if (_testRoom)
			return "TEST ROOM"; // the room runs no beat table: say what is live instead of a leg
		if (_beatIdx < 0)
			return "horde I";
		switch (BEAT_KIND[_beatIdx])
		{
			case 0: return "rescue " + _allyBeatIdx;
			case 1: return "horde II";
			case 2: return "MINI-BOSS";
			case 3: return "MID-BOSS";
			case 4: return "FINAL BOSS";
			case 5: return "WRAP-UP";
			default: return "horde";
		}
	}

	// pilot-only readout: which boss (if any) owns the arena right now, so a verification
	// smoke can FOLLOW the beat loop's one-fight-at-a-time rule (a live boss holds the next
	// row back) instead of guessing it from the engage prints. "none" = the horde has the floor.
	private string BossReadout()
	{
		Boss b = GetActiveBoss();
		if (b == null)
			return "none";
		return b.kind + " " + (int)b.hp + "hp";
	}

	// the stage clock: every beat time in the table is read off this, so a picker / join /
	// camp pause never burns a beat. The HUD and the camp card ride it.
	public double GetRunT()
	{
		return _runT;
	}

	// ---------- gates + readouts the other scripts consume ----------
	// The picker is a soft pause: the world is drawn as usual but nobody acts, so
	// no damage is taken while a human reads the pick card. Enemies/bullets/revive
	// channels check this; so does the horde director.
	public bool GetPickerActive()
	{
		return _pickerActive;
	}

	// world freeze = the picker card OR the join-banner card OR a pre-run card: all are read-the-
	// card soft pauses (the arena is drawn, nothing acts, nothing takes damage). The join banner
	// got this treatment instead of a tree pause so its CLICK gate keeps receiving input, and the
	// stage card rides it so the Director's march never spawns a body while a human reads the
	// plates (the run clock waits on the _Process rail below anyway).
	public bool GetWorldPause()
	{
		return _pickerActive || _joinWaiting || _selHold || _stageHold;
	}

	// Experimental Elixir (user 2026-10-31, Stack's ult): the ONLY randomness of the cast lives
	// in this draw - 10% ALL SIX, else TWO DISTINCT of six (three seeded Rng draws: like every
	// other proc, the run stream shifts downstream). Each effect rides its own 10 s clock on
	// EVERY member (bench too - harmless, same discipline as insT). Readers: Healing=PartyMember
	// regen block, Swiftness=EffMove, Resilience=TakeHit, Boldness=RollDamage, Precision=crit
	// roll, Magnetism=UpdateGems bubble. Names ride the HUD strip via StackElixirTag.
	public void ElixirParty()
	{
		bool allSix = Rng.Randf() < 0.10; // user: 10% chance for ALL of the elixirs instead
		int a = (int)(Rng.Randf() * 6.0);
		int b = (int)(Rng.Randf() * 5.0);
		if (b >= a)
			b++; // second pick from the remaining five, kept DISTINCT
		bool heal = allSix || a == 0 || b == 0;
		bool swift = allSix || a == 1 || b == 1;
		bool hard = allSix || a == 2 || b == 2;
		bool bold = allSix || a == 3 || b == 3;
		bool prec = allSix || a == 4 || b == 4;
		bool mag = allSix || a == 5 || b == 5;
		foreach (PartyMember m in _party)
		{
			if (heal)
				m.elixHealT = ELIXIR_LASTS;
			if (swift)
				m.elixSwiftT = ELIXIR_LASTS;
			if (hard)
				m.elixHardT = ELIXIR_LASTS;
			if (bold)
				m.elixBoldT = ELIXIR_LASTS;
			if (prec)
				m.elixPrecT = ELIXIR_LASTS;
			if (mag)
				m.elixMagT = ELIXIR_LASTS;
		}
		string drew = "";
		if (heal)
			drew += (drew == "" ? "" : " + ") + ELIXIR_NAMES[0];
		if (swift)
			drew += (drew == "" ? "" : " + ") + ELIXIR_NAMES[1];
		if (hard)
			drew += (drew == "" ? "" : " + ") + ELIXIR_NAMES[2];
		if (bold)
			drew += (drew == "" ? "" : " + ") + ELIXIR_NAMES[3];
		if (prec)
			drew += (drew == "" ? "" : " + ") + ELIXIR_NAMES[4];
		if (mag)
			drew += (drew == "" ? "" : " + ") + ELIXIR_NAMES[5];
		GD.Print("[10thAge] (elixir) Experimental Elixir: ALL allies brew " + drew +
			(allSix ? " (ALL SIX!)" : "") + " for " + (int)ELIXIR_LASTS + " s");
	}
	static readonly double ELIXIR_LASTS = 10.0; // user: the effects last 10 seconds
	static readonly string[] ELIXIR_NAMES = { "Healing", "Swiftness", "Resilience", "Boldness", "Precision", "Magnetism" };

	// Have a Drink! (user 2026-10-31, Stack's leader bonus): while Stack owns the leader seat,
	// EVERY burning body hits 10% softer - horde contact AND boss melee share Game.TakeHit, so
	// the multiplier rides the CALL SITES (the body knows its own burnT); boss BULLET lanes are
	// NOT softened (a bullet is not a burning body). user 2026-11-06: HALF the effect on
	// bosses - a burning boss hits 5% softer (DrinkMul's boss branch below) and drifts /
	// dances a 5% beat slower (Boss.cs), while the horde keeps the full 10% everywhere.
	// No roll cost: pure state check, tallied.
	public double DrinkMul(Combatant src)
	{
		if (LeaderBonusKey() != 6 || src.burnT <= 0.0)
			return 1.0;
		_lbDrinkHits++; // bench tally: every softened hit is visible on the audit line
		return IsBossKind(src) ? 0.95 : 0.90; // bosses ride the HALF-effect branch (user 2026-11-06)
	}
	// the boss half of Have a Drink!: same kind set the dance rig uses (Boss.IsDanceKind)
	// plus the plain boss kinds. kind lives on the SHARED Combatant surface so Enemy and
	// Boss both answer here, and a boss bullet is its own pool and never reaches this
	// helper (the bullet lanes were never softened at all).
	private bool IsBossKind(Combatant src)
	{
		return src.kind == "boss1" || src.kind == "boss2" || src.kind == "final" ||
			src.kind == "mini" || src.kind == "mini1" || src.kind == "mini2";
	}
	public bool LeaderDrink()
	{
		return LeaderBonusKey() == 6; // movement readers: Enemy spd lane, Boss drift lane
	}

	// HUD strip suffix (user: name each active elixir next to Stack's name in the party list)
	private string StackElixirTag(PartyMember m)
	{
		if (m == null || m.displayName != "Stack")
			return ""; // the names only ride Stack's own strip, from Stack's own clocks
		string s = "";
		if (m.elixHealT > 0.0)
			s += (s == "" ? " (" : " + ") + ELIXIR_NAMES[0];
		if (m.elixSwiftT > 0.0)
			s += (s == "" ? " (" : " + ") + ELIXIR_NAMES[1];
		if (m.elixHardT > 0.0)
			s += (s == "" ? " (" : " + ") + ELIXIR_NAMES[2];
		if (m.elixBoldT > 0.0)
			s += (s == "" ? " (" : " + ") + ELIXIR_NAMES[3];
		if (m.elixPrecT > 0.0)
			s += (s == "" ? " (" : " + ") + ELIXIR_NAMES[4];
		if (m.elixMagT > 0.0)
			s += (s == "" ? " (" : " + ") + ELIXIR_NAMES[5];
		return s == "" ? "" : s + ")";
	}
	// Oakhollow Assemble! (user 2026-10-31, Wash's ult): ONE clock on EVERY member (bench too - harmless,
	// same discipline as insT/elixir clocks) carries all three party-wide effects: EffMove x1.25,
	// +5 armour in Game.TakeHit (rides the flat-subtract lane above), regen 1% max hp/s in the PM
	// REGEN block. Deterministic cast: zero RNG draws, the run stream stays put.
	public void OakhollowParty()
	{
		// clocks on EVERY member (bench too - harmless, same discipline as insT/elixir clocks)
		foreach (PartyMember m in _party)
		{
			m.oakT = OAK_LASTS; // +25% move / +5 armour / regen all ride this one clock
		}
		GD.Print("[10thAge] (oak) Oakhollow Assemble!: ALL allies +25% move speed, +5 armour, regen 1% max hp/s for " + (int)OAK_LASTS + " s");
	}
		static readonly double OAK_LASTS = 10.0; // user: the effects last 10 seconds

	// Bruno "Axes Away!" (user 2026-10-31): ONE spin clock on Bruno only (his own orbit -
	// no party-wide lane). Blade count/size/spin speed/bleed are read LIVE by PartyMember
	// while the clock runs, so the spin "gains the effects of all upgrades" as taken.
	// blades = 2 + extra-axes rank, disc + band ride 1.0 + 0.25 * size rank, spin pulses
	// every 0.35 s on ONE shared crit/steal roll per pulse across the blades.
	public void BrunoAxesAway(PartyMember m)
	{
		m.orbitT = AXES_LASTS; // the spin clock; PartyMember drives the blades + raking pulses
	}
	static readonly double AXES_LASTS = 10.0; // user: the massive axes spin around him for 10 seconds

	// Brook "Assassination" (user 2026-10-31): the identification IS the window - every ally
	// (bench too, like the oak/elixir clocks) carries its OWN assT that PartyMember.RollDamage
	// reads LIVE for +25% multistrike CHANCE, +15% crit CHANCE points and crit damage x1.25.
	// No stat-store writes: a temporary window, the 17-row audit stays a permanent-grant ledger.
	public void AssassinationParty()
	{
		foreach (PartyMember m in _party)
		{
			m.assT = ASS_LASTS;
		}
	}
	static readonly double ASS_LASTS = 10.0; // user: ALL allies ride the weak-point window for 10 seconds

	// Keeko "Rewrite Reality" (user 2026-11-14): ALL enemies - including any that spawn, since the
	// movement commits read EnemiesFrozen() per frame instead of a snapshot - frozen IN PLACE for
	// 10 s. The final-boss parenthetical is LIVE: if even one live kind "final" boss stands, the
	// cast flips to +25% damage dealt to them (Combatant.Vulnerable marker lane: max-mark,
	// refresh-never-stack, the guiding-light rail) and NO freeze happens at all.
	public void RewriteReality()
	{
		bool finalSeen = false;
		foreach (Godot.Node2D n in GetEnemies()) // the registry carries bosses too
		{
			Combatant c = (Combatant)n;
			if (c != null && c.kind == "final")
			{
				finalSeen = true;
				c.Vulnerable(0.25, 10.0); // +25% incoming damage for the same 10 s the freeze would have run
			}
		}
		if (!finalSeen)
			_freezeT = 10.0; // user: 10 seconds, spawns born frozen (live clock, not a snapshot)
	}
	public bool EnemiesFrozen()
	{
		return _freezeT > 0.0;
	}
	public bool FreezeHold() // user 2026-11-18 hand-play: "Keeko's ultimate time freeze doesn't stop enemies attacking, if you get close they will still shoot projectiles. This needs to be disabled during the time freeze" - the RANGED shot lanes (riderbow/mage/mage2) read this and hold the cadence clock instead of firing while the freeze runs; contact lanes stay live (a pinned body still grazes), and because _shotCd keeps draining a thaw does NOT gift a free volley
	{
		return _freezeT > 0.0;
	}


	// Smite passive probe (user 2026-10-31, Wash): the crit readers (PartyMember.RollDamage) ask
	// whether Wash stands anywhere in the squad - joined, not downed, not eliminated. No roll, no
	// clock: live presence check only, so a downed Wash stops smiting and a revive brings it back.
	public bool WashUp()
	{
		foreach (PartyMember m in _party)
		{
			if (m.displayName == "Wash" && m.joined && !m.downed && !m.eliminated)
				return true;
		}
		return false;
	}
	// party-wide ult lane (Mosiah "Bardic Inspiration", 2026-10-26): the roster lives here
	// (private _party) and every ally is a child of Game, so a party-wide effect walk
	// starts at Game; the authoring + the cooldown gate stay on PartyMember.TryUltimate
	public void InspireParty(double secs)
	{
		foreach (PartyMember m in _party)
			m.insT = secs; // every ally carries its OWN bardic clock (bench too - harmless)
	}

	// Athios "Beastial Rage" (user 2026-11-19, seat 12): party-wide 10 s window on each member's OWN
	// rageT clock. Readers: Game.TakeHit halves incoming damage, PartyMember.EffCooldown pays RAGE_SPD
	// (+50% attack speed) and Strikes() adds +1 attack for every kit that consumes multistrike (shot
	// volleys, beam, cleave re-thrusts, summon repeat). The CLAW kit adds its own swing at its commit
	// instead. Key 12 NEVER sets ultT for the caster: that would double-multiply EffCooldown speed.
	public void RallyParty(double secs)
	{
		foreach (PartyMember m in _party)
			m.rageT = secs; // every ally carries its OWN rage clock (bench too - harmless)
	}

	// how wide the camera can actually see: NOT assumed anymore. A DPI-scaled
	// fullscreen window (canvas_items stretch, aspect expand) sees far more world than
	// the 1920x1080 base viewport - the old hardcoded 960x540 left the pinned floor
	// covering ~1/4 of the frame (human bug 2026-10-20). MeasureWindow() refreshes
	// _visHalf (and the wrap period) from the camera itself, every frame.
	public Vector2 GetVisibleHalfExtents()
	{
		return _visHalf;
	}

	// measures the REAL visible rect, so the wrap period (and staging distances)
	// always strictly contain the frame on any window size / DPI scale. The source
	// is ground truth from the renderer's own layout pass: a hidden Control with
	// full-rect anchors (0,0)-(1,1) resolves to exactly the visible canvas rect,
	// and world<->canvas is 1:1 at zoom 1. Runs at run start AND every _Process
	// frame: layout has not happened on the first frames, so the sanity guard
	// rejects garbage and later frames correct it.
	private void MeasureWindow()
	{
		if (_winProbe == null)
		{
			_winProbe = new Godot.Control();
			// full-rect anchors: layout stretches this node to the visible canvas rect
			_winProbe.SetAnchor(Godot.Side.Left, 0.0f);
			_winProbe.SetAnchor(Godot.Side.Top, 0.0f);
			_winProbe.SetAnchor(Godot.Side.Right, 1.0f);
			_winProbe.SetAnchor(Godot.Side.Bottom, 1.0f);
			_winProbe.Visible = false; // measurement only: never drawn
			AddChild(_winProbe);
		}
		Godot.Vector2 vs = _winProbe.Size;
		double hx = (double)vs.X * 0.5;
		double hy = (double)vs.Y * 0.5;
		if (hx < 64.0 || hy < 64.0)
			return; // stale / garbage: keep the previous value
		double w = hx * 2.0 + 480.0;
		double h = hy * 2.0 + 480.0;
		if (System.Math.Abs(w - _wrapW) < 2.0 && System.Math.Abs(h - _wrapH) < 2.0)
			return; // same period, nothing to do
		_visHalf = new Vector2((float)hx, (float)hy);
		_wrapW = w;
		_wrapH = h;
		if (_floorRect != null)
		{
			_floorRect.Size = new Vector2((float)_wrapW, (float)_wrapH);
			// no pivot trick: the headless oracle (pivot_test) proved pivot_offset is
			// a NO-OP for translation - global_position setter just writes position.
			// The pin below therefore pins TOP-LEFT = camera - size/2.
		}
		RescatterFloorPatches();
		GD.Print("[10thAge] window measured: frame " + (int)(_visHalf.X * 2.0f) + "x" +
				 (int)(_visHalf.Y * 2.0f) + " world px | wrap period " + (int)_wrapW + "x" + (int)_wrapH);
	}

	// re-scatter the motion-cue patches over the (possibly grown) wrap period
	private void RescatterFloorPatches()
	{
		if (_floorRect == null || _floorPatches.Count == 0)
			return; // Rng only exists once the patches do; safe guard for frame 1
		Vector2 cen = GetCameraCenter();
		foreach (Godot.ColorRect p in _floorPatches)
			p.GlobalPosition = new Vector2(cen.X + (float)(Rng.Randf() * _wrapW - _wrapW * 0.5),
										   cen.Y + (float)(Rng.Randf() * _wrapH - _wrapH * 0.5));
	}

	// the escort must not shamble before the party can see the rescue (human fix: the
	// beat read as an off-camera animation that started mid-walk)
	public bool RecruitInView()
	{
		if (_recruitPending == null)
			return false;
		Vector2 cen = GetCameraCenter();
		Vector2 half = GetVisibleHalfExtents();
		Vector2 p = _recruitPending.GlobalPosition;
		return System.Math.Abs(p.X - cen.X) <= half.X && System.Math.Abs(p.Y - cen.Y) <= half.Y;
	}

	public float GetEscortCrawl()
	{
		return (float)ESCORT_CRAWL;
	}

	public PartyMember GetRecruitPending()
	{
		return _recruitPending;
	}

	// escort contact damage on the stranded PC (user pass 11): every goblin that walks
	// onto him chips his HP, and JoinRecruit brings in whatever survived. floored -
	// the escort pressures the rescue clock, it can never run the PC down outright
	public void EscortHitRecruit(PartyMember m, double amount)
	{
		if (m == null || m.joined)
			return;
		// terminal runs (TPK/VICTORY screens) still tick the world; escort contact used
		// to spam "chipped" forever behind the summary (CT50/51 log flood)
		if (_state == StageState.DEAD || _state == StageState.VICTORY)
			return;
		float floor = (float)(m.EffMaxHp() * RECRUIT_JOIN_FLOOR);
		float nh = m.hp - (float)amount;
		if (nh < floor)
			nh = floor;
		m.hp = nh;
		GD.Print("[10thAge] escort touch: " + m.displayName + " chipped to " +
				 (int)m.hp + "/" + (int)m.EffMaxHp() + "hp");
	}

	public Vector2 GetCameraCenter()
	{
		return _camera != null ? _camera.GlobalPosition : (_leader != null ? _leader.GlobalPosition
																			: new Vector2(0, 0));
	}

	// long rest feeds this back to the director's pacing (rest = power vs horde scaling)
	public double GetHordeScale()
	{
		return _hordeScale;
	}

	public bool GetRestChosen()
	{
		return _campfireChoice != "";
	}

	public PartyMember GetMemberByIndex(int idx)
	{
		foreach (PartyMember m in _party)
		{
			if (m.memberIndex == idx)
				return m;
		}
		return _party.Count > 0 ? _party[0] : null;
	}

	Godot.StaticBody2D BuildWall(Vector2 center, Vector2 size)
	{
		Godot.StaticBody2D w = new StaticBody2D();
		w.GlobalPosition = center;
		Godot.CollisionShape2D cs = new CollisionShape2D();
		Godot.RectangleShape2D rs = new RectangleShape2D();
		rs.Size = size;
		cs.Shape = rs;
		w.AddChild(cs);
		Godot.ColorRect vis = new ColorRect();
		vis.Size = size;
		vis.Position = -size * 0.5f;
		vis.Color = new Color(0.25f, 0.22f, 0.18f, 1.0f);
		vis.ZIndex = -10; // above floor, below entities
		w.AddChild(vis);
		AddChild(w);
		return w;
	}

	public override void _EnterTree()
	{
		foreach (Godot.Node n in GetChildren())
		{
			if (n is PartyMember pm)
			{
				_party.Add(pm);
				if (pm.isLeader)
					_leader = pm;
				// field-recruit rule (user 2026-10-20): only the Leader starts in the
				// squad; every other roster PC waits for its join beat and is parked
				// off-arena (hidden in the roster print, not on the map) until then
				pm.joined = pm.isLeader;
				if (!pm.joined)
					pm.GlobalPosition = new Vector2(-9500.0f, -9500.0f);
			}
			else if (n is Boss b)
				_bosses.Add(b);
		}
		_pilot = OS.GetEnvironment("M1_PILOT") != null && OS.GetEnvironment("M1_PILOT") != "";
		_devSkip = OS.GetEnvironment("M1_DEV_SKIP") == null ? "" : OS.GetEnvironment("M1_DEV_SKIP");
		// ENEMY TEST ROOM (user 2026-11-07): M1_TEST_ROOM=1 boots the dev room INSIDE this scene,
		// like every other bench door (M1_PILOT / M1_DEV_SKIP / M1_REAPER). Director.cs then owns
		// the spawn lane - one body every 15-20 s cycling the FULL stage-1 roster - and the stage
		// SCHEDULE is held back (no ally joins, no bosses, no camp, no victory, no horde events,
		// no rest scaling), while death saves, revive, TPK and gem drops stay exactly as shipped.
		// The stage-select card that lets a human pick the room from an ordinary launch is the
		// NEXT pass, so today the launch line prefixes M1_TEST_ROOM=1.
		_testRoom = OS.GetEnvironment("M1_TEST_ROOM") != null && OS.GetEnvironment("M1_TEST_ROOM") != "";
		if (_testRoom)
			GD.Print("[10thAge] (test) ENEMY TEST ROOM live: one body every 15-20 s cycling the stage-1 roster, beats held back, training ring +1 xp/sec at (0,0)");
		// TEST BENCH only (same discipline as M1_PILOT / M1_DEV_SKIP / M1_REAPER): M1_LEADER=<name>
		// moves the leader seat to another roster PC at boot so a smoke run can stand under a
		// given leader bonus; human play never sets it and keeps the Main.tscn export (Baldrun).
		// This MUST precede the join lottery: the demoted seat waits for its join beat like
		// any other recruit, and only a lottery that sees the swap can honour that.
		if (_pilot)
		{
			string want = OS.GetEnvironment("M1_LEADER") == null ? "" : OS.GetEnvironment("M1_LEADER");
				// the swap rule lives in TrySeatLeader so the bench and the pre-run select
				// share ONE copy; the seat swap MUST precede BuildJoinLottery either way
				if (TrySeatLeader(want))
					GD.Print("[10thAge] (bench) leader seat moved to: " + want);
		}
		// ---------- pre-run character select: the door opens before the roster print ----------
		// human / bench-demo runs hold the boot loop (see the _Process rail) until a leader
		// is seated; the panel opens on the roster head but the FIRST press only browses,
		// so an accidental key at boot can never seat a Leader. The lottery waits too (E).
		_selDemo = (OS.GetEnvironment("M1_SELECT") == null ? "" : OS.GetEnvironment("M1_SELECT")) != "";
		_selWant = OS.GetEnvironment("M1_SELECT") == null ? "" : OS.GetEnvironment("M1_SELECT");
		if (!_pilot || _selDemo)
		{
			_selHold = true;
			BuildSelectScreen();
			GD.Print("[10thAge] (select) open: click a portrait to select, click CONFIRM at the bottom to seat the Leader");
		}
		// ---------- STAGE SELECT: the card opens AFTER the character card ----------
		// the flow the user locked is Main Menu > Character Select > Stage Select; there is no
		// main menu yet, so the game opens at character select and the stage card follows it.
		// Same grammar as the leader card: click a stage plate to browse it (the gold ring rides
		// it), click CONFIRM to start. Bench valve M1_STAGE=<id> mirrors M1_SELECT: a seat that
		// asks for nothing keeps the card DOWN and plays stage 1, so every headless gate already
		// running stays byte-identical. The second plate is the ENEMY TEST ROOM the M1_TEST_ROOM=1
		// dev door boots - picking it from the card is the same switch, so the room is now enterable
		// from an ordinary launch.
		_stageDemo = (OS.GetEnvironment("M1_STAGE") == null ? "" : OS.GetEnvironment("M1_STAGE")) != "";
		_stageWant = OS.GetEnvironment("M1_STAGE") == null ? "" : OS.GetEnvironment("M1_STAGE");
		if (!_pilot || _stageDemo)
		{
			_stageHold = true;
			BuildStageScreen();
			GD.Print("[10thAge] (stage) open: click a stage plate to select, click CONFIRM to start the run");
		}
		string roster = "";
		foreach (PartyMember pm2 in _party)
			roster += (roster == "" ? "" : " + ") + pm2.displayName + " (" + pm2.weaponName + ")";
		GD.Print("[10thAge] squad: " + roster + " | leader: " +
				 (_leader != null ? _leader.displayName : "none"));
		// the join lottery rolls at boot so the pilot / escort paths never have to re-decide
		// which PC a beat owns (the print keeps the smoke trails honest about who lands when).
		// It waits while EITHER pre-run card is up: the row it reads rides the STAGE (JOIN_W row =
		// registry index) and the seat swap must precede it, so a card-up launch rolls it in Close
		// Stage (the demoted Leader waits for its join beat like any other recruit; _hpOrder just
		// builds later on such a run, which the camp-card fallback already survives).
		// HORDE EVENT clock arms AT BOOT (user 2026-11-06 playtest: "the first horde spawned at 290s,
		// we should have them start way sooner" - the mini-boss-relative arm landed that late, so the
		// schedule now opens at HORDE_EVENT_FIRST_AT absolute run-seconds inside the opening legs).
		// Arming is unconditional now: it used to sit inside the no-card branch, which left a human
		// run on the legacy mini-boss arm. CloseStage re-arms it after a pick (the room disarms it).
		ArmHordeClock();
		BuildTrainingRing(); // guarded: the card re-arms it once the pick lands on the room
		if (!_selHold && !_stageHold)
			BuildJoinLottery(); // a card-up run rolls it AFTER the stage pick instead
		// the reaper is a pure death/revive TEST BENCH (no balance meaning): human play
		// left it OFF now that the revive rules work, and M1_REAPER=1 puts it back on
		// for testing (a pilot smoke with it ON still TPKs often - the Leader-down rule
		// we deferred leaves no rescue channel, which is variance, not a bug)
		_reaperEnabled = OS.GetEnvironment("M1_REAPER") != null && OS.GetEnvironment("M1_REAPER") != "";
		// TEST time warp (bench smokes only): M1_TIMEWARP=<factor>, clamped [1,12]
		{
		string tw = OS.GetEnvironment("M1_TIMEWARP") == null ? "" : OS.GetEnvironment("M1_TIMEWARP");
		if (tw != "")
		{
		int acc = 0;
		foreach (char ch in tw)
		{
		if (ch >= '0' && ch <= '9')
		acc = acc * 10 + (ch - '0');
		}
		if (acc > 1)
		{
		_timeWarp = (double)(acc > 12 ? 12 : acc); // clamp [1,12]
		GD.Print("[10thAge] (test) run clock time warp x" + (int)_timeWarp + " live");
		}
		}
		}
		// camera first, so the frame (and wrap period) are MEASURED from the real window
		// before any stage geometry is built
		_camera = GetNode("Camera2D") as Godot.Camera2D;
		MeasureWindow();
		Godot.ColorRect floor = new ColorRect();
		floor.Size = new Vector2((float)_wrapW, (float)_wrapH);
		floor.Position = -floor.Size * 0.5f; // top-left offset = centered on origin; the
		// pin below writes TOP-LEFT = camera - size/2 every frame, so this only seeds
		// frame 0. (2026-10-20 human bug: pinning ORIGIN=camera left the rect covering
		// only the bottom-right quadrant; the headless pivot_test proved pivot_offset
		// is a no-op for translation, so centering must happen in the pin itself)
		floor.Color = new Color(0.10f, 0.16f, 0.10f, 1.0f);
		floor.ZIndex = -20; // the .tscn party/boss nodes predate this node in tree order;
							// without a negative z they'd be painted over by the floor
		AddChild(floor);
		_floorRect = floor; // rides the camera every frame: the looping world has no
							// edge, so the plain ground just keeps covering the frame
		// scattered floor patches = motion reference. the camera keeps the leader centered,
		// so a single-color floor made movement LOOK frozen even when input worked
		Rng = new Godot.RandomNumberGenerator();
		Rng.SetSeed(9001);
		// stat registry boot dump: every tag row prints (dormant rows included) so smoke
		// trails and future meta screens share one canonical vocabulary from StatTags.cs
		// kind 0 = multiplier, 1 = points, 2 = count - see StatTags.cs KIND
		for (int tag = 0; tag < StatTags.N; tag += 1)
			GD.Print("[10thAge] stat tag " + StatTags.NAMES[tag] + " [" +
			 StatTags.CATEGORY[tag] + " kind " + StatTags.KIND[tag] +
			 " base " + StatTags.BASE[tag] + "]");

		// party-side tags all live in PartyMember.stat[] at these indices; the
		// body-side vulnerability tag pairs with Combatant.vulnMul (the ring's mark)

		// patch DENSITY is kept constant to the measured period, so big windows keep the
		// same motion cues per screen area
		int patchCount = (int)(60.0 * _wrapW * _wrapH / (2400.0 * 1800.0));
		if (patchCount > 400)
			patchCount = 400;
		for (int i = 0; i < patchCount; i++)
		{
			Godot.ColorRect p = new ColorRect();
			float s = 6.0f + (float)Rng.Randf() * 10.0f;
			p.Size = new Vector2(s, s);
			p.GlobalPosition = new Vector2((float)Rng.Randf() * (float)_wrapW - (float)(_wrapW * 0.5),
										   (float)Rng.Randf() * (float)_wrapH - (float)(_wrapH * 0.5));
			p.Color = new Color(0.13f, 0.20f, 0.13f, 1.0f);
			p.ZIndex = -15;
			AddChild(p);
			_floorPatches.Add(p); // folded per frame like every other entity: the ground
								  // cover scrolls AND loops, so the wrap reads seamless
		}
		// ENEMY TEST ROOM: the training ring's floor READ lives in BuildTrainingRing() below, so
		// the stage-select card can arm it AFTER a pick lands on the room. Arming here still covers
		// the M1_TEST_ROOM=1 bench door (and any launch that never opens the card).
		if (_testRoom)
			BuildTrainingRing();
		// walls are retired with the wrap (user fix 2026-10-20): there is no edge to
		// bump into anymore, and a painted border would fight the invisible fold box
		_camera = GetNode("Camera2D") as Godot.Camera2D;
		Godot.CanvasLayer hudl = new CanvasLayer();
		// PAUSE BLUR LAW (user 2026-11-12 hand-play: "The blur is still too much on the
		// eyes. It's not the background, it's the text that's the problem"): the wash + blur
		// now ride their OWN nested CanvasLayer (layer 1 in PauseMenu.gd), so screen_texture
		// samples ONLY the arena - CanvasLayer layer indexes stack ABSOLUTELY against the
		// viewport (canvas_layer.cpp sets viewport_set_canvas_stacking with the node's own
		// layer, not the parent's). This HUD (HP strips, picker label, join banner) lifts to
		// layer 2: above the blur, crisp instead of smeared. The campfire card is ALSO 2 but
		// instantiated later in tree order, so it still owns the frame like it did before;
		// summary (3), pause furniture (4), leader card (9) and stage card (10) stay on top.
		hudl.Layer = 2;
		AddChild(hudl);
		_hudLayer = hudl; // the HP strips parent to this once the party is known
		_hud = new Label();
		_hud.Position = new Vector2(24.0f, 24.0f);
		hudl.AddChild(_hud);
		_pickerLabel = new Label();
		_pickerLabel.Position = new Vector2(24.0f, 274.0f); // clears the 6-row HP strip block
		hudl.AddChild(_pickerLabel);
		// dash-cooldown bar over the Leader's head: dark track + gold fill (resized
		// every frame in _Process). Human-test UX pass: the old 44x5 sliver was
		// illegible and fought the death-save chip, so it is now a 64x8 track riding
		// at y-42 (chip label sits at y-58, the revive channel bar at y-30). ZIndex
		// 7/8 stay above member bodies (members sit at 5).
		_dashBarPivot = new Godot.Node2D();
		AddChild(_dashBarPivot);
		Godot.ColorRect barBg = new Godot.ColorRect();
		barBg.Size = new Vector2(64.0f, 8.0f);
		barBg.Position = new Vector2(-32.0f, 0.0f);
		barBg.Color = new Color(0.12f, 0.11f, 0.10f, 0.92f);
		barBg.ZIndex = 7;
		_dashBarPivot.AddChild(barBg);
		_dashBarFill = new Godot.ColorRect();
		_dashBarFill.Size = new Vector2(64.0f, 8.0f);
		_dashBarFill.Position = new Vector2(0.0f, 0.0f);
		_dashBarFill.Color = new Color(0.83f, 0.64f, 0.24f, 1.0f); // grimoire gold
		_dashBarFill.ZIndex = 8;
		barBg.AddChild(_dashBarFill);
		_dashBarPivot.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
		_ultLabel = new Godot.Label();
		_ultLabel.Text = "ULT READY";
		_ultLabel.ZIndex = 9;
		AddChild(_ultLabel);
		_ultLabel.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
		// GEM ART loads ONCE here (the bee / ult pool idiom): GD.Load per spawn would re-enter the
		// resource loader on every kill. Null art = the shipped greybox square, unchanged.
		_gemArtTex = GD.Load("res://art/pickups/xp_gem.png") as Godot.Texture2D;
		_gemMegaArtTex = GD.Load("res://art/pickups/xp_gem_mega.png") as Godot.Texture2D;
		for (int i = 0; i < 240; i++)
		{
			Godot.Node2D gem = new Node2D();
			Godot.ColorRect cr = new ColorRect();
			cr.Size = new Vector2(10.0f, 10.0f);
			cr.Position = new Vector2(-5.0f, -5.0f);
			cr.Color = new Color(1.0f, 0.85f, 0.2f, 1.0f);
			cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
			gem.AddChild(cr);
			// the art rail (2026-11-17): the chip rides UNDER the shell like every other pooled fx
			// child, so the shell's Modulate alpha still owns the spawn/despawn fade (it cascades)
			// and PaintGem owns the hue. Built ONLY when the canvas is on disk, so a project without
			// the art keeps the shipped square with zero extra nodes.
			if (_gemArtTex != null)
			{
				Godot.Sprite2D gsp = new Godot.Sprite2D();
				gsp.Set("texture_filter", 0); // NEAREST: hard pixel edges like the shaft and minis
				gsp.Position = new Vector2(5.0f, 5.0f); // Control origin is the corner -> centred
				gsp.Texture = _gemArtTex;
				cr.AddChild(gsp);
			}
			gem.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
			AddChild(gem);
			_gems.Add(gem);
			_gemVals.Add(0.0);
		}
		for (int i = 0; i < 140; i++)
		{
			Godot.Node2D b = new Node2D();
			Godot.ColorRect cr = new ColorRect();
			cr.Size = new Vector2(12.0f, 12.0f);
			cr.Position = new Vector2(-6.0f, -6.0f);
			cr.Color = new Color(1.0f, 0.25f, 0.2f, 1.0f);
			cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
			// arrow fx child (2026-11-02 mini dance): mirrors the party-shot pool idiom - the
			// sprite rides INSIDE the shell so both spawn lanes below can swap square <-> arrow
			// per reuse with a Modulate/Color toggle; 20x8 px art reads fine over the 12 px shell
			Godot.Sprite2D fxs = new Godot.Sprite2D();
			fxs.Set("texture_filter", 0); // NEAREST like the painted minis: hard edges
			fxs.Position = new Vector2(6.0f, 6.0f); // Control origin is the rect corner -> centred
			fxs.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
			cr.AddChild(fxs);
			b.AddChild(cr);
			b.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
			AddChild(b);
			_bullets.Add(b);
			_bulletVel.Add(new Vector2(0, 0));
			_bulletDmg.Add(0.0);
			_bulletSpin.Add(0.0);
			_bulletBoss.Add(false);
		}
		// party-side shot pool (M2 longbow): pooled like the gems and the boss volleys, so
		// a rank-3 multishot archer never allocates a node per bolt mid-horde.
		for (int i = 0; i < 160; i++)
		{
			Godot.Node2D s = new Node2D();
			Godot.ColorRect cr = new ColorRect();
			cr.Size = new Vector2(9.0f, 7.0f);
			cr.Position = new Vector2(-4.5f, -3.5f);
			cr.Color = new Color(0.42f, 0.34f, 0.12f, 1.0f); // insect-chitin amber
			cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
			// fx art child (2026-11-01 art pass): the sprite rides INSIDE the shell, so the
			// flash/despawn Modulate the shot lane applies to the shell carries it for free;
			// the shell's own draw is alpha-toggled at spawn (no art on disk = greybox stays)
			Godot.Sprite2D fxs = new Godot.Sprite2D();
			fxs.Set("texture_filter", 0); // NEAREST like the painted minis: hard edges
			fxs.Position = new Vector2(4.5f, 3.5f); // Control origin is the rect corner -> centred
			fxs.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
			cr.AddChild(fxs);
			s.AddChild(cr);
			s.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
			AddChild(s);
			_shots.Add(s);
			_shotVel.Add(new Vector2(0, 0));
			_shotDmg.Add(0.0);
			_shotLife.Add(0.0);
			_shotPen.Add(0.0);
			_shotBurnD.Add(0.0); // burn memo grows WITH the pool: every slot needs every column
			_shotBurnT.Add(0.0);
			_shotCinderD.Add(0.0);
			_shotLast.Add(-1);
		}
		// fx-feel8: cinder-bloom pop pool (see CinderBurst + UpdateCinderFx): shell-only, no art on
		// disk - the SAME grow-to-blast + fade pattern Yu's detonation uses, ember-orange tinted.
		// 12 slots: deaths are bounded by the horde itself and a pop lives only 0.16 s.
		for (int i = 0; i < 12; i++)
		{
			Godot.Node2D s = new Godot.Node2D();
			Godot.ColorRect cr = new Godot.ColorRect();
			cr.Size = new Vector2(10.0f, 10.0f); // small ember core: UpdateCinderFx grows it to the blast it made
			cr.Position = new Vector2(-5.0f, -5.0f); // Control origin is the rect corner -> centred under the node
			cr.Color = new Color(0.95f, 0.55f, 0.18f, 1.0f); // ember orange: Stack flame read, not Yu violet
			cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f); // parked invisible until CinderBurst looses a pop
			cr.ZIndex = 9; // pops draw over the corpse so the burst reads on top
			s.AddChild(cr);
			s.GlobalPosition = new Vector2(-9000.0f, -9000.0f); // -8000 sentinel: free slot when parked
			AddChild(s);
			_bursts.Add(s);
			_burstLife.Add(0.0);
		}
		// fx-feel9 chain-bolt pool: thin streak segments authored electric blue; UpdateChainFx
		// fades them via Modulate (same pooled pattern as the cinder pops right above).
		for (int i = 0; i < CHAIN_BOLT_POOL; i++)
		{
			Godot.Node2D bn = new Godot.Node2D();
			Godot.ColorRect bcr = new Godot.ColorRect();
			bcr.Size = new Vector2(10.0f, 5.0f); // LooseChainBolt stretches it between the two bodies
			bcr.Position = new Vector2(-5.0f, -2.5f); // centred under the node (Control origin = corner)
			bcr.Color = new Color(0.45f, 0.7f, 1.0f, 1.0f); // electric blue: the user asked for a "simple blue"
			bcr.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f); // parked invisible until a hop looses it
			bcr.ZIndex = 10; // bolts draw over the cinder pops (9) and the bodies (5)
			bn.AddChild(bcr);
			bn.GlobalPosition = new Vector2(-9000.0f, -9000.0f); // free-slot sentinel (same as the shots)
			AddChild(bn);
			_boltN.Add(bn);
			_boltC.Add(bcr);
			_boltLife.Add(0.0);
			_boltDelay.Add(0.0);
		}
		// Hew gear/cog pool (FX-lap law): part drops tumble a cog AT the kill site, spin + fall + fade
		// in UpdateCogFx. Pooled exactly like the bolts and cinders above; art-null-safe via cogFxTex.
		for (int i = 0; i < COG_POOL; i++)
		{
			Godot.Node2D gn = new Godot.Node2D();
			Godot.Sprite2D gs = new Godot.Sprite2D();
			gs.Set("texture_filter", 0); // NEAREST like every other pool sprite
			gs.Scale = new Vector2(0.5f, 0.5f); // assistant pick (retune by eye): a dropped cog reads small
			gn.AddChild(gs);
			Godot.ColorRect gr = new Godot.ColorRect();
			gr.Size = new Vector2(7.0f, 7.0f); // cog-sized ember square (the art-absent fallback)
			gr.Position = new Vector2(-3.5f, -3.5f); // centred under the node (Control origin = corner)
			gr.Color = new Color(0.85f, 0.6f, 0.25f, 1.0f); // brass: a cog is hardware, not ember-orange
			gn.AddChild(gr);
			gn.GlobalPosition = new Vector2(-9000.0f, -9000.0f); // free-slot sentinel (same as every pool)
			AddChild(gn);
			_cogN.Add(gn);
			_cogS.Add(gs);
			_cogR.Add(gr);
			_cogLife.Add(0.0);
			_cogSpin.Add(i % 2 == 0 ? 5.0 : -5.0); // rad/s tumble, alternating direction (assistant pick)
		}
		// Yu's familiars: pooled like the bolts and the gems, so a rank-3 multiattack ritual

		// never allocates a node mid-horde. Shell-first so the creature reads as a body; the
		// detonation re-sizes this SAME rect to the blast it actually made (see DetonateSummon),
		// which is why SpawnSummon has to put the shell back before every reuse.
		Godot.Texture2D fxStumpTex = GD.Load("res://art/fx/stump.png") as Godot.Texture2D;
		_famArt = fxStumpTex != null;
		for (int i = 0; i < 64; i++)
		{
			Godot.Node2D s = new Node2D();
			Godot.ColorRect cr = new ColorRect();
			cr.Size = new Vector2(14.0f, 14.0f);
			cr.Position = new Vector2(-7.0f, -7.0f);
			cr.Color = new Color(0.26f, 0.2f, 0.34f, 1.0f); // occult violet shell
			cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
			Godot.Sprite2D fxs = new Godot.Sprite2D();
			fxs.Texture = fxStumpTex;
			fxs.Set("texture_filter", 0); // NEAREST like the painted minis: hard edges
			fxs.Position = new Vector2(7.0f, 7.0f); // Control origin is the rect corner -> centred
			fxs.Scale = new Vector2(0.5f, 0.5f); // user 2026-11-01: familiars halve (39x59 art read huge over a 14 px shell)
			cr.AddChild(fxs);
			if (_famArt)
				cr.Color = new Color(0.26f, 0.2f, 0.34f, 0.0f); // art rides: shell stops drawing
			s.AddChild(cr);
			s.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
			AddChild(s);
			_summons.Add(s);
			_summonVel.Add(new Vector2(0, 0));
			_summonDmg.Add(0.0);
			_summonLife.Add(0.0);
			_summonR.Add(0.0);
			_summonSpd.Add(0.0f);
			_summonBurst.Add(false);
			_summonKaka.Add(false);
			_summonLuck.Add(0.0);
		}
		// leader-bonus helper pools (user 2026-10-25): Asper's insects and Yu's spirits.
		// Slots stay small because one spawns every 30 s and neither phase lives past
		// 30 s - far more capacity than a run can ever have waiting at once.
		Godot.Texture2D fxBugTex = GD.Load("res://art/fx/bee.png") as Godot.Texture2D; // 2026-11-09: the passive bugs ARE the friendly bees (same art as the ult swarm)
		for (int i = 0; i < 8; i++)
		{
			Godot.Node2D s = new Node2D();
			Godot.ColorRect cr = new ColorRect();
			cr.Size = new Vector2(12.0f, 12.0f);
			cr.Position = new Vector2(-6.0f, -6.0f);
			// chitin green: deliberately off every horde hue so a friendly bug never reads as prey
			cr.Color = new Color(0.44f, 0.66f, 0.26f, 1.0f);
			cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
			// 2026-11-09 (user: the leader-passive bugs ARE the friendly bees too): bee art rides
			// the shell like the ult pool idiom - sprite child UNDER the ColorRect so the Modulate
			// cascade hides/shows both, shell stops drawing when art is on disk
			Godot.Sprite2D bsp = new Godot.Sprite2D();
			bsp.Set("texture_filter", 0); // NEAREST like the shaft and the minis
			bsp.Texture = fxBugTex;
			if (fxBugTex != null)
			{
				bsp.Scale = new Vector2(BEE_ART_S, BEE_ART_S);
				bsp.Position = new Vector2(6.0f - (float)fxBugTex.GetWidth() * BEE_ART_S * 0.5f, 6.0f - (float)fxBugTex.GetHeight() * BEE_ART_S * 0.5f);
				cr.Color = new Color(0.44f, 0.66f, 0.26f, 0.0f); // art rides: the chitin square stops drawing
			}
			cr.AddChild(bsp);
			s.AddChild(cr);
			s.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
			s.Set("z_index", 20); // passive bees draw ON TOP of every character too: same law as the ult pool
			AddChild(s);
			_insects.Add(s);
			_insectLife.Add(0.0);
			_insectCd.Add(0.0);
			_insectOwner.Add(-1);
		}
		Godot.Texture2D fxTreeTex = GD.Load("res://art/fx/tree.png") as Godot.Texture2D;
		for (int i = 0; i < 6; i++)
		{
			Godot.Node2D s = new Node2D();
			Godot.ColorRect cr = new ColorRect();
			cr.Size = new Vector2(12.0f, 12.0f);
			cr.Position = new Vector2(-6.0f, -6.0f);
			cr.Color = new Color(0.78f, 0.92f, 0.88f, 1.0f); // pale ghost teal: a kindness spirit
			cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
			Godot.Sprite2D fxs = new Godot.Sprite2D();
			fxs.Texture = fxTreeTex;
			fxs.Set("texture_filter", 0); // NEAREST like the painted minis: hard edges
			fxs.Position = new Vector2(6.0f, 6.0f); // Control origin is the rect corner -> centred
			fxs.Scale = new Vector2(0.5f, 0.5f); // user 2026-11-01: spirits halve to match the smaller familiars
			cr.AddChild(fxs);
			if (fxTreeTex != null)
				cr.Color = new Color(0.78f, 0.92f, 0.88f, 0.0f); // art rides: shell stops drawing
			s.AddChild(cr);
			s.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
			AddChild(s);
			_spirits.Add(s);
			_spiritLife.Add(0.0);
		}
		// Swarm of Bees pool (Asper ult, user 2026-10-26): six slots like the cast, parked
		// at -9000; casts reuse them, UpdateBees owns the leash.
		_fxBeeTex = GD.Load("res://art/fx/bee.png") as Godot.Texture2D; // 2026-11-08 art pass: one load at pool build, null-safe like the other lanes
		for (int i = 0; i < 6; i++)
		{
			Godot.Node2D s = new Node2D();
			Godot.ColorRect cr = new ColorRect();
			cr.Size = new Vector2(10.0f, 10.0f);
			cr.Position = new Vector2(-5.0f, -5.0f);
			cr.Color = new Color(0.86f, 0.72f, 0.18f, 1.0f); // honey amber: off every horde hue AND the insect lane's chitin so a friendly bee never reads as prey
			cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
			// the sprite child rides UNDER the shell: the pool Modulate cascade hides/shows both together
			Godot.Sprite2D bsp = new Godot.Sprite2D();
			bsp.Set("texture_filter", 0); // NEAREST like the shaft and the minis
			bsp.Texture = _fxBeeTex;
			if (_fxBeeTex != null)
			{
				bsp.Scale = new Vector2(BEE_ART_S, BEE_ART_S);
				bsp.Position = new Vector2(5.0f - (float)_fxBeeTex.GetWidth() * BEE_ART_S * 0.5f, 5.0f - (float)_fxBeeTex.GetHeight() * BEE_ART_S * 0.5f);
				cr.Color = new Color(0.86f, 0.72f, 0.18f, 0.0f); // art rides: the amber square stops drawing
			}
			cr.AddChild(bsp);
			s.AddChild(cr);
			s.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
			s.Set("z_index", 20); // bees draw ON TOP of every character (user 2026-11-08): the scene sets no z anywhere, so 20 outranks every sibling; set ONCE at pool build
			AddChild(s);
			_bees.Add(s);
			_beeLife.Add(0.0);
			_beeBite.Add(0.0);
			_beeRay.Add(0.0);
			_beeTarget.Add(-1L);
		}
		// Hapi risen pool + pillar FX pool (2026-11-17): shells get a fallback sickly-green rect (child
		// 0, parked invisible) + an art slot (child1) that SpawnUndead fills with the corpse's OWN
		// texture. z 2 like the bodies; pillar columns ride z 3 like the beam lance.
		for (int i = 0; i < UNDEAD_POOL; i++)
		{
			Godot.Node2D s = new Godot.Node2D();
			Godot.ColorRect cr = new Godot.ColorRect();
			cr.Size = new Vector2(14.0f, 18.0f);
			cr.Position = new Vector2(-7.0f, -18.0f);
			cr.Color = new Color(0.55f, 0.8f, 0.35f, 0.0f); // sickly green: parked slots invisible
			s.AddChild(cr);
			Godot.Sprite2D art = new Godot.Sprite2D();
			art.Set("texture_filter", 0); // NEAREST like every other pool sprite
			s.AddChild(art);
			s.Set("z_index", 2); // bodies lane: the pillar FX (z 3) draws over them
			s.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
			AddChild(s);
			_undead.Add(s);
			_undHp.Add(0.0);
			_undLife.Add(0.0);
			_undCd.Add(0.0);
			_undDmg.Add(0.0);
			_undSpd.Add(0.0);
			_undTaunt.Add(0);
		}
		BuildMartha(); // her singleton, once per run (parked until Hew joins)
		for (int i = 0; i < PILLAR_POOL; i++)
		{
			Godot.Sprite2D s = new Godot.Sprite2D();
			s.Set("texture_filter", 0); // NEAREST like every other pool sprite
			s.Set("z_index", 3); // above the bodies, below the chip UI, same lane as the beam
			s.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f); // parked slots invisible
			s.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
			AddChild(s);
			_pillarFx.Add(s);
			_pillarT.Add(0.0);
		}
		// Dirt puff pool (user 2026-11-09): the grit the horde kicks up on its foot plants.
		// Plain z (the scene sets none anywhere else, and the bees lane needed 20 to sit ABOVE
		// the characters): these Sprite2D slots join the tree after the floor rect and before
		// any runtime actor, so grit rides ABOVE the ground and BELOW every model - a mark on
		// the map, never paint over a creature.
		_fxPuffTex = GD.Load("res://art/fx/dustpuff.png") as Godot.Texture2D; // null-safe like the other art lanes: missing art draws nothing
		for (int i = 0; i < PUFF_POOL; i++)
		{
			Godot.Sprite2D s = new Godot.Sprite2D();
			s.Texture = _fxPuffTex;
			s.Set("texture_filter", 0); // NEAREST like every other pool sprite: hard painted edges
			s.GlobalPosition = new Vector2(-9000.0f, -9000.0f); // parked like every other pool
			s.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);     // parked slots are invisible
			AddChild(s);
			_puffs.Add(s);
			_puffLife.Add(0.0);
		}
	}

	// The face-to-threat snap BOTH leader legs share (2026-11-09): the angle to the nearest
	// live threat inside EffRange()*mult, or -1e30f when nothing is in sight (the caller keeps
	// the walk direction). Bodies keep their slot past death, so the kind check is the skip.
	private float NearestThreatAngle(PartyMember l, double mult)
	{
		Godot.Node2D best = null;
		float bestD = 1e9f;
		for (int i = 0; i < _enemies.Count; i++)
		{
			Combatant c = (Combatant)_enemies[i];
			if (c == null || c.kind == "dead")
				continue;
			Godot.Node2D n = _enemies[i];
			float dd = n.GlobalPosition.DistanceTo(l.GlobalPosition);
			if (dd >= bestD)
				continue;
			bestD = dd;
			best = n;
		}
		foreach (Boss b in _bosses)
		{
			if (!b.active || b.hp <= 0.0f)
				continue; // parked bosses are not threats yet
			float dd = b.GlobalPosition.DistanceTo(l.GlobalPosition);
			if (dd < bestD)
			{
				bestD = dd;
				best = b;
			}
		}
		if (best == null || bestD > (float)l.EffRange() * (float)mult)
			return -1e30f;
		Vector2 v = best.GlobalPosition - l.GlobalPosition;
		if (v.X == 0.0f && v.Y == 0.0f)
			return -1e30f; // standing on the threat: keep the walk facing
		return (float)System.Math.Atan2(v.Y, v.X);
	}
	
	public override void _Process(double d)
	{
		float delta = (float)d;
		MeasureWindow(); // keep frame/period honest for any window size (DPI scaling)
		RebuildSeparationGrid(); // parent-first: the hash snapshots the registry before any
		// rail can bail out, so the enemies that process after this always read a fresh grid
		if (_selHold)
		{
			PollSelect(delta); // boot card: the whole run loop waits so no beat burns while a
									   // human reads; even the pilot demo door rides this rail
			return;
		}
		if (_stageHold)
		{
			PollStage(delta); // stage card: the whole run loop waits so no beat burns while a human reads the plates
			return;
		}
		// the camp card's own clock: it holds during read-the-card soft pauses, which is
		// how M1 kept a human parked on the card from burning beats. M2's beats ride
		// _runT alone (below) off the beat table, so this clock only feeds the pilot's
		// "nobody chose a rest" timeout valve.
		if (!GetWorldPause())
			_campWaitT += d;
		if (!GetWorldPause())
			_ringAngle += 0.22 * d; // orbit clock (user: "rotate a little faster" - was 0.15)
			// user 2026-10-28: frozen while any soft pause runs. The clock advancing during a
			// level-up card (bodies parked) made the 48 px leash snap the whole ring when the
			// card closed - the jarring "standing reset" per level-up; only a revive now re-homes
		_dashPulse += d;         // free-running clock for the dash-ready flash
		if (_engageInvulnT > 0.0)
			_engageInvulnT -= delta; // boss-engage immunity window (set in ActivateBoss)
		// pause-card rail: only the one-frame ESC/P handoff is left here. The pilot CLOSE
		// valve had to move ONTO THE CARD: this pause is a TRUE tree pause, so this _process
		// never ticks again while the card is up (seat 545 froze at beat 1 for the whole
		// 25-min kill rail with a C#-side valve). The card is PROCESS_MODE_ALWAYS and times
		// PAUSE_PILOT_TIMEOUT itself (pilot_timeout export), closing via the dismissed
		// signal. ESC-close arrives through that signal and eats one frame via _pauseJustClosed.
		if (_pauseJustClosed)
			_pauseJustClosed = false; // the card closed on that very press: do not reopen it
		else if (Input.IsActionJustPressed("pause_menu") && !GetWorldPause() && !_campfireWaiting)
		{
			ShowPauseCard(); // human lane: ESC/P opens, the card owns the close
			// THE ARMING FRAME HOLDS STILL TOO (PD156 readout: "run clock held +1.2" = one warped
			// frame burned before the pause took effect - this _process was already mid-flight when
			// ShowPauseCard armed the tree, so the run clock / escort timeout / TPK rail below it all
			// still ticked that frame). RETURN like the _selHold + _stageHold rails above: the beat
			// table never moves on the frame a human opened the card, and the seat proof reads +0.0.
			return;
		}
		else if (_pilot && !_pausePilotUsed && _state == StageState.PLAY && !GetWorldPause() && !_campfireWaiting)
		{
			// headless proof: open the roster card once per run; the CARD's own
			// PROCESS_MODE_ALWAYS valve closes it (this _process is frozen by the pause)
			_pausePilotUsed = true;
			ShowPauseCard();
			return; // same arming-frame hold as the human rail above
		}
		// run clock drives the beat table; camp is excluded because it is a read-the-card
		// pause, and the picker / join soft-pauses keep stage beats honest while a human
		// reads the card. M2: one PLAY state covers horde AND boss fights, so the
		// PLAY1/MINIBOSS/PLAY2 set collapses to PLAY (the camp / DEAD / VICTORY states do
		// not burn beats).
		if (!_pickerActive && !_joinWaiting && _state == StageState.PLAY)
		{
			_runT += d * _timeWarp; // TEST warp valve (1.0 = real-time: no M1_TIMEWARP = zero change)
			if (_freezeT > 0.0)
				_freezeT -= d; // Rewrite Reality decays on REAL seconds: raw d, not the warp-scaled run clock
			// ENEMY TEST ROOM XP faucet (user: "a circle section that gives constant XP at 1 per
			// second that we can sit in to level up so I'm not testing only with level 1
			// characters"). The rate is the flat squad pool like kills feed it, so the level-up
			// picker card opens exactly as it does mid-run. Riding the run-clock gate above means
			// a picker / join card or a non-PLAY state never burns faucet xp.
			if (_testRoom && TestRingStood() > 0)
			{
				_testXpAccru += d * _timeWarp * _testXpPerSec;
				if (_testXpAccru >= 1.0)
				{
					int drip = (int)_testXpAccru;
					_testXpAccru -= drip;
					AddXp(drip); // the REAL xp lane: whole points only, so the picker rail sees normal steps
					GD.Print("[10thAge] (xp) training ring: +" + drip + " xp (" + TestRingStood() + " members stood in the ring)");
				}
			}
			if (_recruitPending != null)
				_escortTimeout += d; // an abandoned cage still ends: the stage must keep moving
		}
		// the stall rule needs a per-frame test: a stable-down party takes no more damage events,
		// so the damage-site CheckTpk calls could never fire (that was the smokeM2w stall)
		if (_state == StageState.PLAY)
			CheckTpk();
		// the join banner's click gate is a HUMAN gate (no-timeout rule): pilot smokes
		// clear it instantly so verification keeps advancing
		if (_joinWaiting && _pilot)
			ClearJoinBanner("pilot autopilot");
		// P4: the reaper stays a spawn-once-at-10s TEST BENCH (locked behavior; no
		// balance meaning), now OPT-IN via M1_REAPER=1. Its only changes here are where
		// it drops in (outside the camera view, never on the party) and a slower lurk.
		if (!_reaperSpawned && _runT >= 10.0 && _state == StageState.PLAY && _reaperEnabled)
		{
			// TEST reaper: spawns ONCE 10 s into horde I, downs whoever it touches
			// (death-save tester), attack-proof (hp stays huge), despawns after its
			// first down
			_reaperSpawned = true;
			Enemy r = new Enemy();
			r.kind = "reaper";
			r.maxHp = 100000000.0f;
			r.hp = r.maxHp;
			r.speed = 48.0f;
			r.contactDamage = 0.0f;
			r.xpValue = 0.0f;
			// spawn just OUTSIDE the camera view, never dropped on the party: the old
			// ClampToArena(leader + 500,0) could land right on top of the squad
			Vector2 cen = GetCameraCenter();
			r.GlobalPosition = cen + new Vector2(820.0f, 140.0f);
			RegisterEnemy(r);
			AddChild(r);
			GD.Print("[10thAge] TEST reaper live");
		}
		// DEV SKIP KEYS (human-test alignment probes, added 2026-10-20 on request; C#-side
		// convenience like `retry`, no balance effect):
		//   C: straight to the campfire beat - kill the mini that the BEAT TABLE already put on
		//      the arena, through the REAL kill scan (OnBossKilled: bullets swept, camp opens),
		//      or park ON the next mini beat if none owns the arena yet. The boss's own kind
		//      decides the route (mini -> camp), so there is nothing to stage up first. The old
		//      version reached every boss node at boot: it slayed minis the party never fought
		//      and restarted the camp timer every frame, an artifact a smoke should never lean on.
		//   V: park runT on the final-boss beat, so AdvanceBeats then walks the whole loop
		//      one fight at a time (each live boss holds the next beat back).
		//   J: park runT on the next ally beat so the escort drop fires next frame.
		if (Input.IsActionJustPressed("dev_camp") || (_pilot && _devSkip == "camp"))
		{
			bool slayed = false;
			foreach (Boss b in _bosses)
			{
				// only an ENGAGED mini: an inactive node is parked offscreen, its beat has not
				// fired, and slaying it would skip a fight the party never saw
				if (!b.active || b.hp <= 0.0f || (b.kind != "mini1" && b.kind != "mini2"))
					continue;
				b.hp = -1.0f; // the kill scan routes it through the real OnBossKilled
				GD.Print("[10thAge] (dev) mini-boss instant-slain -> camp");
				slayed = true;
				break; // one mini per press: the pilot should not fight two at once
			}
			if (!slayed)
			{
				// no mini owns the arena: park ON the next mini beat so AdvanceBeats engages it
				// on the real path (same park + guard as the join skip, so it cannot reprint
				// and re-park every frame once that beat has already fired)
				double parkMini = ParkNextBeat(2);
				if (_state == StageState.PLAY && parkMini > _runT)
				{
					_runT = parkMini;
					GD.Print("[10thAge] (dev) runT parked on the next mini-boss beat");
				}
			}
		}
	// the pilot mirrors park the SAME way, so a verification smoke can walk every ally
	// beat; the park > runT guard is what keeps a pilot press from reprinting (and
	// re-parking) every frame once that beat has already fired
	double parkAlly = ParkNextBeat(0);
	if (_state == StageState.PLAY && parkAlly > _runT &&
		(Input.IsActionJustPressed("dev_join") || (_pilot && _devSkip == "join")))
	{
		_runT = parkAlly; // the next ally drop fires on the real path next frame
		GD.Print("[10thAge] (dev) runT parked on the next ally beat");
	}
	double parkFinal = ParkNextBeat(4);
	if (_state == StageState.PLAY && parkFinal > _runT &&
		(Input.IsActionJustPressed("dev_boss") || (_pilot && _devSkip == "boss")))
	{
		_runT = parkFinal; // the final-boss beat engages on the real path next frame
	GD.Print("[10thAge] (dev) runT parked on the final-boss beat");
	}
		switch (_state)
		{
			case StageState.PLAY:
				// the beat table owns every stage gate now (user-locked schedule 2026-10-22):
				// ally drops, horde tier gates, mini-bosses (kill -> camp), the mid boss
				// (kill -> the horde keeps marching) and the final boss (kill -> VICTORY).
				// Rows fire in table order on the RUN clock, so a picker / join / camp pause
				// never burns a beat, and a live boss or an unresolved rescue holds the NEXT
				// row back until it is resolved (AdvanceBeats owns that stall, plus the
				// timeout valve that keeps an abandoned rescue from freezing the stage).
				// the timeout valve first: a rescue the party abandoned still resolves, so
				// the stalled-ally rule above can never freeze the stage
			if (_recruitPending != null && _escortTimeout >= ESCORT_GIVEUP)
			{
				GD.Print("[10thAge] rescue abandoned after " + (int)ESCORT_GIVEUP
					+ "s - " + _recruitPending.displayName + " joins at the floored HP");
				JoinRecruit();
			}
			if (!_testRoom)
				AdvanceBeats(); // ENEMY TEST ROOM: the whole beat table is held back - no ally
				// joins, no mini/mid/final bosses, no campfire, no VICTORY. The room tests BODIES,
				// not the stage, so nothing can end the run except a real TPK.
			break;
			case StageState.CAMPFIRE:
				// player-driven rest beat: the ui/Campfire screen emits rest_chosen and
				// Game._OnCampfireChosen advances back to PLAY. user-locked (2026-10-19): a
				// human campfire NEVER times out - it waits for a real choice forever.
				// The timeout valve now only runs for the pilot autopilot (verification
				// smokes) or when the campfire scene itself is missing (soft-lock guard).
				if (!_campfireWaiting)
				{
					_campfireWaiting = true;
					_campWaitT = 0.0;
					ShowCampfire();
				}
				else if ((_pilot || _campfire == null) && _campWaitT >= CAMPFIRE_WAIT_TIMEOUT)
				{
					GD.Print("[10thAge] campfire: nobody chose a rest after " + (int)CAMPFIRE_WAIT_TIMEOUT
							 + "s - defaulting to a short rest");
					EndCampfire("short");
				}
				break;
			case StageState.DEAD:
			case StageState.VICTORY:
				// the run is over: the RunSummary card is up. R retries the stage (M1 has no
				// journal/menu). ShowRunSummary un-pauses the tree, so this key gets input
				// frames even though the card paused it; _RetryRun fires at most once per run.
				if (Input.IsActionJustPressed("retry"))
					_RetryRun();
				break;
			default:
				break;
		}
		if (_state == StageState.DEAD || _state == StageState.VICTORY)
			return;

		// the per-member readout is strips + state dots now (built once the party is
		// known), so it refreshes on every path including the picker soft-pause
		UpdateHpStrips(delta);

		// ---------- join ceremony = soft pause (same read-the-card rule) ----------
		// human fix 2026-10-20: the banner must pause the game like the level-up and camp
		// screens so the player can actually look at the art. A tree pause would eat the
		// CLICK TO CONTINUE gate, so this is the picker's soft pause: the world is drawn,
		// nobody acts, nothing takes damage, and the run clock that gates the recruit drop
		// and the bosses already holds still (same _campWaitT bookkeeping as the picker).
		if (_joinWaiting)
		{
			_hud.Text = "RECRUIT JOINS THE BATTLE - click the gold card to continue"
						+ " | combat holds while you read | Lv " + _level + " | enemies " + _liveEnemies;
			return;
		}

		// ---------- level-up picker = soft pause ----------
		// P1: the picker used to share the WASD letters, so a held A/S walked AND
		// confirmed the pick the very frame the card appeared. Now presses are edge-read
		// (IsActionJustPressed) and combat is frozen while the card is up: the arena,
		// party, horde and bodies stay drawn, nobody acts, nothing takes damage.
		if (_pickerActive)
		{
			PickerTick(d);
			_hud.Text = _pickerCard + " - pick ONE (press the card's number once, releases"
						+ " do not count)"
						+ " | combat holds while you read | Lv " + _level + " | xp " + (int)_xp + "/"
						+ (int)_xpNext + " | " + StateName(_state) + " t=" + (int)GetRunTime() +
						" | enemies " + _liveEnemies;
			// member HP lives on the HUD strips, never in stacked label lines
			// (human-test: the lines ran past the window edge mid-card)
			return;
		}

		PartyMember leader = _leader;
		bool leaderLive = leader != null && !leader.downed && !leader.eliminated;
		Vector2 dir = new Vector2(0, 0);
		Vector2 faceTo = new Vector2(0, 0); // pilot face-lock: attack while strafing sideways
		if (leaderLive)
			dir = new Vector2(Input.GetActionStrength("move_right") - Input.GetActionStrength("move_left"),
							  Input.GetActionStrength("move_down") - Input.GetActionStrength("move_up"));
		if (_pilot && leaderLive && dir.LengthSquared() <= 0.0025f)
		{
			// orbit while everyone is up; walk onto a downed body and stop to channel revive
			PartyMember down = null;
			foreach (PartyMember mm in _party)
			{
				if (mm.downed && !mm.eliminated)
				{
					down = mm;
					break;
				}
			}
			if (down != null)
			{
				Vector2 to = down.GlobalPosition - leader.GlobalPosition;
				if (to.LengthSquared() > 576.0f)
					dir = to.Normalized();
			}
			else
			{
				// engage an active boss: close in, then STRAFE at melee range with the
				// maul face-locked on the boss. standing still pins the whole horde into
				// permanent contact damage; strafing keeps them chasing instead
				Boss b = GetActiveBoss();
				if (b != null)
				{
					Vector2 to = b.GlobalPosition - leader.GlobalPosition;
					float dist = to.Length();
					if (dist > 110.0f)
						dir = to.Normalized();
					else if (dist < 90.0f)
						dir = -to.Normalized();
					else
					{
						dir = new Vector2(-to.Y, to.X).Normalized();
						faceTo = to; // face boss while strafing so the arc stays landed
					}
				}
				else
				{
					dir = new Vector2((float)System.Math.Cos(_ringAngle * 4.0),
									  (float)System.Math.Sin(_ringAngle * 4.0));
					// competent-pilot face-lock: keep the maul arc on the nearest pursuer
					// while strafing, so the arc stops only grazing what we run past
					Combatant nearest = null;
					float nd = 1e9f;
					for (int i = _enemies.Count - 1; i >= 0; i--)
					{
						if (i >= _enemies.Count)
							continue;
						Combatant c = (Combatant)_enemies[i];
						if (c == null || c.kind == "dead" || c.hp <= 0.0f)
							continue;
						float dd = c.GlobalPosition.DistanceSquaredTo(leader.GlobalPosition);
						if (dd < nd)
						{
							nd = dd;
							nearest = c;
						}
					}
					if (nearest != null)
						faceTo = nearest.GlobalPosition - leader.GlobalPosition;
				}
			}
		}
		// ---------- dash (SPACE, user-approved balance round) ----------
		// Instant impulse toward the walk direction (or facing when idle). The whole
		// live squad lunges: the Leader 240 px on 1.5 s, ring members 120 px on their
		// own 6 s. The ring-follow below reels members back to their slot, so the
		// squad dash reads as a lunge-and-recover, not a permanent formation break.
		_dashCdT -= delta;
		_memberDashCdT -= delta;
		if (leaderLive && Input.IsActionJustPressed("dash"))
		{
			Vector2 burst = dir;
			if (burst.LengthSquared() <= 0.0025f)
				burst = new Vector2((float)System.Math.Cos(leader.facing),
									(float)System.Math.Sin(leader.facing));
			burst = burst.Normalized();
			if (_dashCdT <= 0.0)
			{
				leader.GlobalPosition = leader.GlobalPosition + burst * (float)DASH_IMPULSE;
				_dashCdT = DASH_CD;
			}
			if (_memberDashCdT <= 0.0)
			{
				foreach (PartyMember m in _party)
				{
					if (!m.joined || m.isLeader || m.downed || m.eliminated)
						continue; // fallen bodies stay where they fell; a stranded recruit
								  // does not lunge with the squad - it is not in it yet
					m.GlobalPosition = WrapNearCamera(m.GlobalPosition + burst * (float)MEMBER_DASH_IMPULSE);
				}
				_memberDashCdT = MEMBER_DASH_CD;
			}
		}
		// ---------- ultimate (R, user 2026-10-25) ----------
		// R fires the LEADER's ultimate when that PC owns one and its cooldown is
		// ready. TryUltimate prints the reason on every press (still cooling / no
		// ultimate authored yet). The pilot mirror auto-fires ONLY when the leader
		// owns a ready ult, so no-ult bench lanes never spam the print.
		if (leaderLive && (Input.IsActionJustPressed("ult") ||
			(_pilot && leader.ultKey() > 0 && leader.ultCdT <= 0.0)))
		{
			if (leader.TryUltimate())
				_ultFired += 1;
		}
		bool moved = dir.LengthSquared() > 0.0025f;
		// 2026-11-09 MOSIAH LANE LAW (user: "Mosiah no longer attacks unless I am standing still,
		// he won't attack while moving anymore for some reason"): the rapier is the only LANE
		// weapon (PartyMember's laneOnly = isLeader && !cleaver && !shotKit, a ~40 deg arc on
		// leader.facing), and in HUMAN play facing only followed the WALK direction while moving.
		// The face-to-threat snap lived on the idle leg alone, and the other aim assist (faceTo)
		// is the _pilot lane, not the human lane. So a walking leader pointed wherever it walked
		// and its narrow lane bit only what it happened to walk at, while standing still snapped
		// to the nearest threat and bit normally. Fix: the SAME snap now runs on the moving leg
		// for lane weapons, so the thrust turns to its prey exactly as standing still had it.
		// Movement, the dash direction, the maul auto-aim and the bow lane stay as shipped.
		bool laneLeader = leaderLive && leader.weaponKit == "melee" && leader.arcHalf <= 0.5f; // mirrors PartyMember's cleaver test: melee kit + narrow arc = lane weapon
		if (moved)
		{
			dir = dir.Normalized();
			leader.GlobalPosition = leader.GlobalPosition + dir * (float)leader.EffMove() * delta;
			if (faceTo.X != 0.0f || faceTo.Y != 0.0f)
				leader.facing = (float)System.Math.Atan2(faceTo.Y, faceTo.X);
			else
			{
				float laneAng = laneLeader ? NearestThreatAngle(leader, 1.5) : -1e30f;
				if (laneAng > -1e29f)
					leader.facing = laneAng; // lane weapon walking: aim at the nearest threat (user 2026-11-09)
				else
					leader.facing = (float)System.Math.Atan2(dir.Y, dir.X);
			}
		}
		else if (leader != null)
		{
			// idle leader turns to its nearest threat: facing only followed the
			// movement direction, so standing still starved the maul everywhere
			// except the way they last walked (the snap now lives in ONE helper both legs call)
			float idleAng = NearestThreatAngle(leader, 1.5);
			if (idleAng > -1e29f)
				leader.facing = idleAng;
		}

		// ---------- the bodyguard ring (human-test locked + "always attached" locked) ----------
		// Party members are ALWAYS attached to and orbiting the Leader unless they are
		// downed or dead. Homing speed exceeds the Leader's walk speed (the old
		// baseSpeed*0.9 cap maxed at 126 px/s - under Baldrun's 128 px/s stride, so
		// Mosiah permanently trailed), and a hard 120 px leash guarantees a stray push
		// can never leave a body lagging. The orbit feel stays: it rides the world-space
		// clock (_ringAngle), and auto-aim lives in PartyMember (the ring member bites
		// its prey), so the rapier still lands while circling.
		{
			Vector2 anchor = leader != null ? leader.GlobalPosition : new Vector2(0, 0);
			float leadSpeed = leader != null ? (float)leader.EffMove() : 128.0f;
			// even ring (user 2026-10-30): the fixed seat table left the leader's own seat as an
			// empty pocket in the orbit (the "box with a missing corner" in their screenshot). Spacing
			// now divides 2pi by the count of LIVE movers: 4 members circle a true box, 3 a triangle,
			// 2 face off; a revive re-spreads the ring as the follow below re-homes the bodies.
			int ringN = 0; int ringOrd = -1;
			foreach (PartyMember m2 in _party)
				if (m2.joined && !m2.isLeader && !m2.downed && !m2.eliminated)
					ringN++;
			foreach (PartyMember m in _party)
			{
				if (!m.joined || m.isLeader || m.downed || m.eliminated)
					continue; // fallen bodies stay where they fell (locked); a stranded
							  // recruit stands where it fell IN, welded to its own spot
				ringOrd++; // ordinal among live movers: same skip test + same iteration order as the
							   // count pass above, so each body keeps its arc position frame to frame
				double slot = (double)ringOrd * System.Math.PI * 2.0 / (double)(ringN > 0 ? ringN : 1);
				Vector2 aim = new Vector2((float)System.Math.Cos(_ringAngle + slot),
										  (float)System.Math.Sin(_ringAngle + slot));
				Vector2 target = anchor + aim * (float)RING_RADIUS;
				Vector2 to = target - m.GlobalPosition;
				double gap = (double)to.Length();
				if (gap <= 0.0009)
					continue;
				// attachment-first follow (user-locked): homing far above walk
				// speed so a gap never persists - the member behaves as welded
				// to the slot, never as a chaser
				float home = (float)System.Math.Max(m.EffMove() * 1.5,
													(double)leadSpeed + 80.0);
				double step = (double)home * (double)delta;
				Vector2 move = step >= gap ? to : to * (float)(step / gap);
				m.GlobalPosition = m.GlobalPosition + move;
				// hard leash: never further than 48 px from the ring slot (was
				// 120 - the user still saw Mosiah "catching up"; a tight clamp
				// makes drift physically impossible, dashes/shoves snap back)
				Vector2 slack = m.GlobalPosition - target;
				double leash = (double)slack.Length();
				if (leash > 48.0)
					m.GlobalPosition = target - slack * (float)(48.0 / leash);
			}
		}
		if (_camera != null && leader != null)
			_camera.GlobalPosition = _camera.GlobalPosition.Lerp(leader.GlobalPosition, 0.08f);
		// looping world: the flat floor rect rides the camera (solid color, so pinning
		// it cannot pop a seam), while the patches fold like every other entity - that
		// is what keeps the ground scrolling AND looping as the motion readout
		if (_floorRect != null)
			_floorRect.GlobalPosition = GetCameraCenter() - _floorRect.Size * 0.5f;
		for (int fp = 0; fp < _floorPatches.Count; fp++)
			_floorPatches[fp].GlobalPosition = WrapNearCamera(_floorPatches[fp].GlobalPosition);
		// recruit arrow: rides above the Leader and points at the stranded PC. Pure
		// guidance - the user asked for "an arrow pointing to their location (HELP!)".
		// both the arrow and the HELP! label now track the RECRUIT'S FOLDED position,
		// so when the beat wraps across a seam the guidance follows instead of hanging
		// in the void at a stale absolute spot
		if (_recruitPending != null && _recruitArrow != null)
		{
			if (_leader != null)
				_recruitArrow.GlobalPosition = _leader.GlobalPosition;
			_recruitArrow.LookAt(_recruitPending.GlobalPosition);
			if (_helpLabel != null)
				_helpLabel.GlobalPosition = _recruitPending.GlobalPosition + new Vector2(-26.0f, -62.0f);
		}
		// dash-cooldown readout over Baldrun: the gold fill grows back as the Leader's
		// dash recharges. Human-test UX pass: 64x8 track at y-42, and once the dash IS
		// ready the fill flashes bright gold twice a second, so the 6 s recharge has an
		// actual "you may press SPACE now" cue instead of a static sliver.
		// 2026-10-21 round 2: with the painted mini on, y-42 cut across the bear's torso
		// - the bar moved BELOW the base (rim bottom sits ~y+5) so it reads as a charge
		// plate the miniature stands on, never through the art.
		if (_dashBarPivot != null && _dashBarFill != null)
		{
			if (leader != null)
			{
				_dashBarPivot.GlobalPosition = leader.GlobalPosition + new Vector2(0.0f, 12.0f);
				if (_ultLabel != null)
				{
					_ultLabel.GlobalPosition = leader.GlobalPosition + new Vector2(20.0f, -66.0f);
					_ultLabel.Visible = leader.ultKey() > 0;
					_ultLabel.Text = leader.ultCdT <= 0.0 ? "ULT READY" : "ULT " + (int)System.Math.Ceiling(leader.ultCdT);
				}
				double ready = _dashCdT <= 0.0 ? 1.0 : 1.0 - _dashCdT / DASH_CD;
				if (ready < 0.0)
					ready = 0.0;
				if (ready > 1.0)
					ready = 1.0;
				_dashBarFill.Size = new Vector2((float)(64.0 * ready), 8.0f);
				bool charged = _dashCdT <= 0.0;
				// 4.7 C# Math has no sin(): a sawtooth triangle 0->1->0 at 2 Hz does
				// the ready-flash without any trig
				float tw = (float)(_dashPulse * 4.0);
				float cyc = tw - (int)tw;
				float tri = cyc < 0.5f ? cyc * 2.0f : 2.0f - cyc * 2.0f;
				float pulse = charged ? tri : 0.0f;
				_dashBarFill.Color = new Color(0.83f + 0.12f * pulse, 0.64f + 0.14f * pulse,
											   0.24f + 0.11f * pulse, 1.0f);
			}
			else
			{
				_dashBarPivot.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
				if (_ultLabel != null)
					_ultLabel.Visible = false;
			}
		}
		// party Y-sort (user 2026-10-21): painted minis overlap on the table, so a
		// bodyguard standing ABOVE the leader must paint UNDER him and vice versa.
		// Rank members by ground Y and bake the rank into ZIndex (base 4..8 keeps the
		// whole squad over the horde; chips/labels/telegraphs ride their parent's rank
		// since they are children). Uses FOLDED positions - the same coords the frame
		// draws - so wrap seams can't mis-sort.
		foreach (PartyMember m in _party)
		{
			int rank = 0;
			foreach (PartyMember o in _party)
				if (o != m && o.GlobalPosition.Y < m.GlobalPosition.Y)
					rank += 1;
			m.ZIndex = 4 + rank;
		}

		// death saves (locked rules): d20 every 4 s while downed - nat 20 revives
		// at 25 % HP, 11-19 is a success (3 successes = STABLE, rolls stop), 10 or
		// below is a failure (3 = out until the campfire). Damage on a downed body
		// is a buffered failure (TakeHit); damage on a STABLE body slays it (TakeHit).
		// The player revives a body by walking the leader onto it and standing still.
		foreach (PartyMember m in _party)
		{
			if (!m.joined || m.eliminated || !m.downed)
				continue;
			if (!m.stable)
			{
				m.dsTimer += (float)d;
				if (m.dsTimer >= DS_INTERVAL)
				{
					m.dsTimer = 0.0f;
					int roll = (int)(GD.Randi() % 20) + 1;
					m.dsLastRoll = roll; // the on-body d20 chip shows the die itself
					// P6 instrumentation: one line per d20 roll + outcome, so a headless
					// run can be verified by READING the dice, not the code.
					GD.Print("[10thAge] d20 " + m.displayName + " rolled " + roll +
							 " (target " + DS_REVIVE_ON + " revive / " + DS_FAIL_ON + " or under fails)");
					if (roll == DS_REVIVE_ON)
					{
						GD.Print("[10thAge]   outcome: NAT 20 - " + m.displayName +
								 " self-revives at 25% hp, 2s invulnerable");
						m.downed = false;
						m.hp = (float)(m.EffMaxHp() * 0.25);
						m.stable = false;
						m.dsFails = 0;
						m.dsSucceeds = 0;
						m.dsDamageBuffer = 0.0f;
						m.reviveShield = false;
						// locked rule: every revive grants 2 s of invulnerability
						m.invuln = (float)REVIVE_INVULN;
						GD.Print("[10thAge] revive(dice) " + m.displayName + " at 25% hp | invuln 2s granted");
						m.dsColor = CHIP_SAGE;
						m.SetDsTemp("NAT 20 - revived at 25%", 2.5);
						continue;
					}
					else if (roll <= DS_FAIL_ON)
					{
						m.dsFails++;
						if (m.dsFails >= DS_FAIL_LIMIT)
						{
							m.eliminated = true;
							m.dsColor = CHIP_GREY;
							m.SetDsText("OUT - next campfire");
							GD.Print("[10thAge] " + m.displayName + " ELIMINATED (3 failed saves) - returns at campfire");
							CheckTpk();
							continue;
						}
						m.dsColor = CHIP_BLOOD;
						m.SetDsText("d20 " + roll + " -> fail " + m.dsFails + "/" + DS_FAIL_LIMIT);
					}
					else
					{
						m.dsSucceeds++;
						if (m.dsSucceeds >= DS_STABLE_NEED)
						{
							m.stable = true;
							m.dsColor = CHIP_AMBER;
							m.SetDsText("STABLE - hits slay unless channeled");
							GD.Print("[10thAge] " + m.displayName + " STABLE - rolls stop; a hit now slays unless the party channels a rescue");
						}
						else
						{
							m.dsColor = CHIP_AMBER;
							m.SetDsText("d20 " + roll + " -> stable " + m.dsSucceeds + "/" + DS_STABLE_NEED);
						}
					}
				}
			}
			// player revive channel: leader stands still next to the body. The body
			// is immune while the channel is progressing (reviveShield) so a swarm
			// can't screw the rescue out, plus 2 s of post-revive invulnerability.
			bool channeling = leaderLive && !moved
							  && leader.GlobalPosition.DistanceTo(m.GlobalPosition) < REVIVE_RANGE;
			if (channeling && m.reviveProgress <= 0.0f)
				GD.Print("[10thAge] channel START on " + m.displayName + "'s body - stand still "
						 + (int)REVIVE_CHANNEL + "s; the body is immune while it runs");
			m.reviveShield = channeling;
			if (channeling)
			{
				m.reviveProgress += (float)d;
				if (m.reviveProgress >= REVIVE_CHANNEL)
				{
					m.downed = false;
					m.stable = false;
					m.dsFails = 0;
					m.dsSucceeds = 0;
					m.reviveProgress = 0.0f;
					m.reviveShield = false;
					// locked rule: every revive grants 2 s of invulnerability
					m.invuln = (float)REVIVE_INVULN;
					m.hp = (float)(m.EffMaxHp() * 0.5);
					GD.Print("[10thAge] channel COMPLETE: " + m.displayName +
							 " revived at 50% hp | invuln 2s granted");
					m.dsColor = CHIP_SAGE;
					m.SetDsTemp("REVIVED - 50% hp", 2.0);
				}
			}
			else if (m.reviveProgress > 0.0f)
				m.reviveProgress = (float)System.Math.Max(0.0, m.reviveProgress - delta * 1.5);
		}

		// kill scan + hurt-flash decay over everything weapons can hit.
		// Reverse-indexed: OnEnemyKilled QueueFrees the node, so the entry must
		// leave _enemies in the same pass or every later cast would be dangling.
		for (int i = _enemies.Count - 1; i >= 0; i--)
		{
			// OnBossKilled (mini slain) runs ClearEnemies() mid-scan, so the array
			// can shrink under us; re-clamp instead of indexing a stale position.
			if (i >= _enemies.Count)
				continue;
			Combatant c = (Combatant)_enemies[i];
			if (c == null || c.kind == "dead")
			{
				// self-despawn (TEST reaper) leaves kind "dead" without a decrement;
				// the counted flag makes this cleanup safe for every exit path
				if (c is Enemy)
					ReleaseLiveCount((Enemy)c);
				_enemies.RemoveAt(i);
				continue;
			}
			if (c.hurtFlash > 0.0f)
				c.hurtFlash -= delta;
			if (c.hp > 0.0f)
				continue;
			if (c is Boss)
				OnBossKilled(c);
			else
			{
				CinderBurst(c); // cinder bloom: burst BEFORE the corpse is freed (reads its live pos + stamp)
				OnEnemyKilled(c);
				_enemies.RemoveAt(i);
			}
		}

		// 2026-11-17 user rail: cluster-fold BEFORE the magnet pass, so a fold never moves a gem
		// this frame already rode, and a folded carrier is collected next frame like any other.
		MergeGemClusters();
		UpdateGems(delta);
		UpdateBullets(delta);
		UpdateShots(delta); // the party's own projectiles: mirror pool, opposite faction
		UpdateSummons(delta); // Yu's familiars: run at prey, detonate on contact OR on the fuse
		UpdateCinderFx(delta); // cinder blooms: the pops the last CinderBurst loosed, grow + fade
		UpdateChainFx(delta); // fx-feel9: chain bolts reveal + fade (Baldrun leader-bonus streaks)
		UpdateCogFx(delta); // FX-lap: Hew's part cogs tumble, fall and fade at the drop site
		UpdateMarthaFx(delta); // FX-lap: Martha's bite stamp decays and her broken iron crackles on its own cadence
		UpdatePuffs(delta); // grit on foot plants: the map finally reads the horde as walking (user 2026-11-09)

		UpdateHelpers(delta); // leader-bonus lane (user 2026-10-25): Alvora regen, Asper insects, Yu spirits
		PickerTick(d);
		_hud.Text = "Surviving the 10th Age - " + StageBadge() +
					") | Lv " + _level +
					" | xp " + (int)_xp + "/" + (int)_xpNext +
					" | " + StateName(_state) + " t=" + (int)GetRunTime() + " | enemies " + _liveEnemies +
					(_recruitPending != null ? " | escort " + _escortLeft : "");
		// member HP/state is the strip block (UpdateHpStrips), not stacked label lines
		if (_pilot)
		{
			// one status line every 15s of sim time so a headless smoke run can follow the flow
			_pilotTick -= d;
			if (_pilotTick <= 0.0)
			{
				_pilotTick = 15.0;
				string hps = "";
				foreach (PartyMember mm in _party)
				{
					if (!mm.joined)
						continue; // not squad yet: no phantom hp readout
					hps += (hps == "" ? "" : "  ") + mm.displayName + StackElixirTag(mm) +
						   (mm.eliminated ? " OUT" : (mm.downed ? " down " + mm.dsFails + "/" + DS_FAIL_LIMIT : " " + (int)mm.hp + "hp"));
				}
				GD.Print("[10thAge] (pilot) " + StateName(_state) + " t=" + (int)GetRunTime() +
						 " | beat " + BeatLabel() + " tier " + (int)GetHordeTier() + " boss " + BossReadout() +
						 " | lv " + _level + " xp " + (int)_xpTotal + " | live enemies " + _liveEnemies +
						 " | party: " + hps +
						 " | lb chain " + _lbChainFired + " bound " + _lbInsectBound + " healed " + _lbSpiritTaken +
						 " drink " + _lbDrinkHits +
						 " | ult " + _ultFired + " cd " + (_leader != null && _leader.ultCdT > 0.0 ? (int)System.Math.Ceiling(_leader.ultCdT)  : 0));
			}
		}
	}

	// QOL (user 2026-10-24): XP suction became PER-MEMBER - every living PC owns
	// its own COLLECT_RANGE bubble, so stragglers vacuum their side of the arena
	// and the leader no longer has to run over each pile (he is IN _party, so
	// his own bubble survives the scan below)
	// the campfire vacuum (camp beat) still reels gems to the leader UNCHANGED
	// (party suction on the field vs camp vacuum on the beat are separate lanes)
	// ---------- leader bonuses (user 2026-10-25): signature perks keyed to leader identity ----------
	// Exactly one perk runs at a time: the perk of whichever roster PC owns the leader seat.
	// Every reader asks _leader live, so a future leadership change moves which lane runs by
	// itself. Key names: 1 Baldrun chain lightning | 2 Mosiah pierce | 3 Alvora regen |
	// 4 Asper friendly insects | 5 Yu kindling spirits | 6 Stack drink seat | 7 Bruno area+move seat | 8 Brook inquisitive seat | 9 Wash inspiring seat. Display names are the roster key,
	// same convention the card pool already uses ("Baldrun's draging maul"). Procs roll on
	// the seeded run stream: a chain proc / spirit heal shifts horde draws downstream (as
	// crit rolls already do), and every such draw is documented at its site.
	private int LeaderBonusKey()
	{
		if (_leader == null)
			return 0;
		if (_leader.displayName == "Baldrun")
			return 1;
		if (_leader.displayName == "Mosiah")
			return 2;
		if (_leader.displayName == "Alvora")
			return 3;
		if (_leader.displayName == "Asper")
			return 4;
		if (_leader.displayName == "Stack")
			return 6; // Experimental Elixir + Have a Drink!
		if (_leader.displayName == "Bruno")
			return 7; // all allies +15% attack area + +10% move: readers multiply via LeaderAreaMul/LeaderMoveMul
		if (_leader.displayName == "Brook")
			return 8; // Inquisitive Mind: +5% evade, +15% collection bubble, +15% XP: readers multiply via LeaderEvaMul/LeaderCollectMul/LeaderXpMul
		if (_leader.displayName == "Yu")
			return 5;
		if (_leader.displayName == "Wash")
			return 9; // Inspiring Leader (user 2026-11-02): all allies +15 max hp / +1 armour while Wash leads: readers LeaderInspireHp() (EffMaxHp) + LeaderArmourFlat() (TakeHit)
		if (_leader.displayName == "Keeko")
			return 10; // Gekkota Training (user 2026-11-14): all allies +10% evasion / +10% move / +10% attack speed while Keeko leads: readers LeaderEvaMul/LeaderMoveMul/LeaderAtkSpdMul
		if (_leader.displayName == "Hapi")
			return 11; // Call of the Grave (user 2026-11-17): slain enemies 5% rise as undead halves (half stats) that taunt nearby enemies: the rise lives on Game.OnEnemyKilled, the pull on Game.GetTauntBody
		if (_leader.displayName == "Athios")
			return 12; // Spiritmane Pride (user 2026-11-19): the pack is battle-born - +10% move, +10% dodge,
		// 10% less damage taken party-wide. Readers: LeaderMoveMul / LeaderEvaMul / LeaderPrideDamageMul (below)
		if (_leader.displayName == "Hew")
		 return 13; // Upgrades! (user 2026-11-22): every defeated enemy has a 5% chance to drop a part boosting a random stat for ALL characters (and Martha): 5% Damage / 5% Attack Speed / 5% Radius / +1 Armour / +5 HP. Run-permanent; the lane lives on Game.OnEnemyKilled (below), Martha's half on her own upkeep
		return 0;
	}
	
	// Baldrun's seat: every party hit that LANDS rolls 5% to loose a lightning chain hopping
	// body to body - up to 4 bounces, 10 damage each (user 2026-10-25). Hooked at EVERY party
	// hit event (rapier lane, maul fan, guardian ring, bolt pool, familiar blast), so the
	// proc is per landed hit and a cleave raking three bodies rolls three times. Bounces pick
	// the nearest UNTOUCHED living body within CHAIN_RANGE (the user left the hop range open;
	// 150 px is the pick) and a body killed but not yet swept by the kill scan (hp <= 0) is
	// not a valid hop. Visual: each hopped body reads via the shared hurt flash this frame;
	// fx-feel9 (user 2026-11-02: "add a visual to Baldrun's leader bonus that deals chain
	// lightning ... just a simple blue streak of lightning that chains quickly between enemies"):
	// LooseChainBolt now DRAWS it - electric blue streak segments, lit GAP s apart per hop.

	public void TryChainLightning(Godot.Node2D struck)
	{
		if (LeaderBonusKey() != 1)
			return; // not Baldrun's seat: zero roll cost, zero effect
		Combatant src = (Combatant)struck;
		if (src == null || src.kind == "dead" || src.hp <= 0.0f)
			return; // a body dying from the hit itself cannot anchor a chain
		if (Rng.Randf() >= CHAIN_CHANCE)
			return; // the 5% proc: one run-stream draw per landed hit
		_lbChainFired++;
		// visit memo: no body hops twice in one chain (small alloc only on a proc)
		Godot.Node2D[] vis = new Godot.Node2D[1 + CHAIN_BOUNCES];
		vis[0] = struck;
		int used = 1;
		Godot.Node2D cur = struck;
		for (int b = 0; b < CHAIN_BOUNCES; b++)
		{
			Godot.Node2D next = null;
			float nd = (float)CHAIN_RANGE;
			for (int e = 0; e < _enemies.Count; e++)
			{
				Godot.Node2D n = _enemies[e];
				Combatant nc = (Combatant)n;
				if (nc == null || nc.kind == "dead" || nc.hp <= 0.0f)
					continue;
				bool seen = false;
				for (int q = 0; q < used; q++)
					if (vis[q].GetInstanceId() == n.GetInstanceId())
						seen = true;
				if (seen)
					continue; // no body hops twice
				float dd = WrapDelta(cur.GlobalPosition, n.GlobalPosition).Length();
				if (dd < nd)
				{
					nd = dd;
					next = n;
				}
			}
			if (next == null)
				break; // the chain dies when no other body stands in hop range
			// fx-feel9 visual: one streak from the body the bolt left off to the one it lands on,
			// revealed b * GAP s later so the hops read as a fast zigzag (pure art: damage is live)
			LooseChainBolt(cur.GlobalPosition, next.GlobalPosition, b * CHAIN_BOLT_GAP);
			((Combatant)next).TakeDamage(CHAIN_DMG);
			vis[used++] = next;
			cur = next;
		}
		// fx-feel9 bench evidence: the streak ART is visual-only, so a "(chain)" row is a proc worth
		// counting (the audit cannot see the bolts themselves)
		if (used > 1)
			GD.Print("[10thAge] (chain) Baldrun leader bonus: bolt hopped ", used - 1, " bodies for ", (int)((used - 1) * CHAIN_DMG), " total");

	}
	
	// Mosiah's seat: one extra pierced body for every party attack while he leads. Bends only
	// the two single-target verbs: the rapier lane reads this at its rank cap (PartyMember.cs
	// via its GetParent() as Game handle) and the bolt pool stamps it as the per-bolt budget
	// at spawn. Maul fan / guardian ring / familiar blast already sweep everything in shape,
	// so there is nothing to bend there.
	public int LeaderPierceBonus()
	{
		return LeaderBonusKey() == 2 ? 1 : 0;
	}
	
	// Bruno's seat (user 2026-10-31: "All allies gain 15% increased attack size (area) and
	// 10% increased movement speed"): multipliers at the READER sites - EffRange/EffMove,
	// the Kaka blast, the guardian-ring pivot and Bruno's own blade discs ride these. NO
	// stat-store writes: a seat perk is temporary, so the 17-row audit stays a permanent-grant
	// ledger and - because every reader asks _leader live - a seat swap retargets by itself.
	public double LeaderAreaMul()
	{
		return LeaderBonusKey() == 7 ? 1.15 : 1.0;
	}
	public double LeaderMoveMul()
	{
		return (LeaderBonusKey() == 7 || LeaderBonusKey() == 10 || LeaderBonusKey() == 12) ? 1.10 : 1.0; // Bruno seat | Keeko Gekkota Training | Athios Spiritmane Pride (user 2026-11-19)
	}
	// Brook "Inquisitive Mind" seat 8 (user 2026-10-31: allies +5% evasion, +15% collection
// range, +15% XP while Brook leads): the SAME multiplier discipline as the Bruno pair -
// readers multiply LIVE (Game.TakeHit dodge roll, Game.UpdateGems bubble + pickup XP) and
// write NO stat-store grants, so the 17-row audit stays a permanent-grant ledger.
	public double LeaderEvaMul()
	{
		 return (LeaderBonusKey() == 10 || LeaderBonusKey() == 12) ? 1.10 : ((LeaderBonusKey() == 8 || LeaderBonusKey() == 2) ? 1.05 : 1.0); // Keeko Gekkota Training / Athios Spiritmane Pride +10% (user 2026-11-19) | Inquisitive Mind | Mosiah +5%
	}

	// Keeko "Gekkota Training" seat 10 (user 2026-11-14: while she leads, ALL allies get +10% attack
	// speed too): PartyMember.EffCooldown multiplies the SPD lane by this LIVE - same multiplier
	// discipline as the Bruno/Eva pairs, no stat-store writes, a seat swap retargets by itself.
	public double LeaderAtkSpdMul()
	{
		return LeaderBonusKey() == 10 ? 1.10 : 1.0;
	}
	public double LeaderCollectMul()
	{
		return LeaderBonusKey() == 8 ? 1.15 : 1.0;
	}
	public double LeaderXpMul()
	{
		return LeaderBonusKey() == 8 ? 1.15 : 1.0;
		}
		
		// Wash "Inspiring Leader" seat 9 (user 2026-11-02: all allies +15 max hp / +1 armour while
		// Wash leads): SAME reader discipline as every seat perk - EffMaxHp (PartyMember.cs) and the
		// TakeHit armour lane read these LIVE, NO stat-store writes, so the 17-row audit stays a
		// permanent-grant ledger and a seat swap retargets by itself.
		public double LeaderInspireHp()
		{
		return LeaderBonusKey() == 9 ? 15.0 : 0.0;
		}
		public double LeaderArmourFlat()
		{
		return LeaderBonusKey() == 9 ? 1.0 : 0.0; // Inspiring Leader: +1 armour flat while Wash leads (TakeHit lane)
		}
		
		// Athios "Spiritmane Pride" seat 12 (user 2026-11-19: party +10% move / +10% evasion / 10%
		// less damage taken while he leads): the move + evasion halves ride the LeaderMoveMul and
		// LeaderEvaMul lines above; the mitigation half is this live multiplier, consumed in the
		// TakeHit incoming-damage gate beside the Resilience elixir. SAME multiplier discipline as
		// every seat perk - no stat-store writes, a seat swap retargets by itself.
		public double LeaderPrideDamageMul()
		{
			return LeaderBonusKey() == 12 ? 0.9 : 1.0;
		}
	
	// per-frame half of the leader lane: regen ticks (Alvora), insect / spirit upkeep,
	// and the seat-locked spawn clocks (Asper / Yu). Upkeep runs under ANY seat so a
	// helper already out there keeps living; only clocks and regen tick are seat-gated.
	private void UpdateHelpers(float delta)
	{
		int lb = LeaderBonusKey();
		if (lb == 3)
		{
			// Alvora seat: every 10 s every standing ally regenerates 1 hp (user 2026-10-25).
			// Own clock, not the dead hp-regen tag lane; since user 2026-11-06 the medic
			// OVER-heals at the ceiling into the Temporary HP buffer (see the branch below);
			// a stranded recruit is not yet an ally, a fallen body recovers via
			// a death save, not a free heal.
			_regenT += delta;
			while (_regenT >= REGEN_TICK)
			{
				_regenT -= REGEN_TICK;
				foreach (PartyMember m in _party)
				{
					if (!m.joined || m.downed || m.eliminated)
					continue; // not arrived / fallen: no free heal
					if (m.hp < m.EffMaxHp())
					{
						m.hp += (float)REGEN_HP;
						if (m.hp > m.EffMaxHp())
						m.hp = (float)m.EffMaxHp();
					}
					else if (m.tempHp < (float)TEMP_HP_CAP)
					{
						// user 2026-11-06: the medic MAY OVER-heal. At the ceiling it keeps
						// packing a Temporary HP buffer (up to Game.TEMP_HP_CAP) that damage
						// spends FIRST (Game.TakeHit). NOT folded into EffMaxHp - a raised
						// ceiling is Wash's Inspiring Leader seat; this is a spendable line,
						// and no other heal lane tops it (spirits / awakening / campfire still
						// clamp at EffMaxHp)
						float room = (float)TEMP_HP_CAP - m.tempHp;
						m.tempHp += room < (float)REGEN_HP ? room : (float)REGEN_HP;
						if (_pilot)
						GD.Print("[10thAge] (bonus) field medic over-heal: " + m.displayName + " at " + (int)m.hp + "/" + (int)m.EffMaxHp() + " carries " + (int)m.tempHp + " temp hp");
					}
				}
			}
		}
		// spawn clocks charge ONLY under the owning PC: leadership is fixed at boot today,
		// but the rule keeps the lane honest if the seat ever moves
		if (lb == 4)
		{
			_insectClock += delta;
			if (_insectClock >= INSECT_EVERY)
			{
				_insectClock = 0.0;
				SpawnInsect();
			}
		}
		else if (lb == 5)
		{
			// user 2026-11-01 (fix pass): a downed Yu must stop kindling kindness spirits. His KIT
			// already returns at PartyMember.cs:805 while down/eliminated, so this seat lane was the
			// only ungated spawner left. WashUp presence discipline read off the SEAT itself (lb == 5
			// means _leader is Yu): while he is downed or eliminated the kindle clock DRAINS to 0
			// instead of accumulating, so nothing spawns AND a Yu revived at a campfire resumes the
			// 30 s cadence instead of burst-spawning the banked time.
			if (_leader.downed || _leader.eliminated)
				_spiritClock = 0.0;
			else
				_spiritClock += delta;
			if (_spiritClock >= SPIRIT_EVERY)
			{
				_spiritClock = 0.0;
				SpawnSpirit();
			}
		}
		UpdateInsects(delta);
		UpdateSpirits(delta);
		UpdateBees(delta); // swarm upkeep: lives under ANY seat once a cast ran
		UpdateUndead(delta); // risen upkeep: columns fade, bodies shamble and chomp, plague walks
		// MARTHA upkeep (Hew's companion): parked while he is off-field; hunts, bites, leashes home,
		// knits when broken (see the Martha block). Unconditional like every other helper lane: a
		// body already on the field keeps living even if the seat moved (nothing can move it today),
		// and _party never shrinks (join/eliminate only flip flags), so the foreach stays safe.
		UpdateMartha(delta);
	}

	// Asper's vigil spawner: next free slot (parked at -9000 like the summon pool), a
	// random spot on a 90-170 px ring around the seat so an insect never kindles on
	// top of the squad. Two Rng draws per spawn shift the run stream downstream (documented).
	private void SpawnInsect()
	{
		if (_leader == null)
		return;
		for (int i = 0; i < _insects.Count; i++)
		{
			Godot.Node2D s = _insects[i];
			if (!(s.GlobalPosition.X < -8000.0f))
			continue; // slot busy: a run can never hold 8 vigils, guard only
			double aw = Rng.Randf() * 2.0 * System.Math.PI;
			double rr = HELPER_RING_MIN + Rng.Randf() * (HELPER_RING_MAX - HELPER_RING_MIN);
			Vector2 lp = _leader.GlobalPosition;
			s.GlobalPosition = lp + new Vector2((float)System.Math.Cos(aw), (float)System.Math.Sin(aw)) * (float)rr;
			((Godot.ColorRect)s.GetChild(0)).Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f);
			_insectLife[i] = INSECT_WAIT; // waiting phase: a host claims it on walk contact
			_insectCd[i] = 0.0;
			_insectOwner[i] = -1;
			GD.Print("[10thAge] (bonus) insect kindles: one waits in the clearing");
			return;
		}
	}

	// Yu's spawner: next free slot, same ring pick. One Rng draw per vigil.
	private void SpawnSpirit()
	{
		if (_leader == null)
		return;
		for (int i = 0; i < _spirits.Count; i++)
		{
			Godot.Node2D s = _spirits[i];
			if (!(s.GlobalPosition.X < -8000.0f))
			continue;
			double aw = Rng.Randf() * 2.0 * System.Math.PI;
			double rr = SPIRIT_RING_MIN + Rng.Randf() * (SPIRIT_RING_MAX - SPIRIT_RING_MIN);
			Vector2 lp = _leader.GlobalPosition;
			s.GlobalPosition = lp + new Vector2((float)System.Math.Cos(aw), (float)System.Math.Sin(aw)) * (float)rr;
			((Godot.ColorRect)s.GetChild(0)).Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f);
			_spiritLife[i] = SPIRIT_LASTS;
			GD.Print("[10thAge] (bonus) kindness spirit kindles");
			return;
		}
	}

	// helpers free their slots the summon pool way: parked off-world, alpha back to 0
	private void DespawnInsect(int i)
	{
		_insectLife[i] = 0.0;
		_insectCd[i] = 0.0;
		_insectOwner[i] = -1;
		_insects[i].GlobalPosition = new Vector2(-9000.0f, -9000.0f);
		((Godot.ColorRect)_insects[i].GetChild(0)).Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
	}

	private void DespawnSpirit(int i)
	{
		_spiritLife[i] = 0.0;
		_spirits[i].GlobalPosition = new Vector2(-9000.0f, -9000.0f);
		((Godot.ColorRect)_spirits[i].GetChild(0)).Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
	}

	// One grit puff at a planted foot: next free slot, jittered so a clump walking in
	// step never prints a perfect row of grit. The pool is pure VFX: a FULL pool simply
	// drops the puff, so visuals can never block or delay a step.
	public void SpawnDustPuff(Vector2 at)
	{
		for (int i = 0; i < _puffs.Count; i++)
		{
			if (_puffLife[i] > 0.0)
				continue;
			_puffLife[i] = PUFF_LIFE;
			Godot.Sprite2D s = _puffs[i];
			s.GlobalPosition = at + new Vector2((float)Rng.RandfRange(-3.0f, 3.0f), (float)Rng.RandfRange(-2.0f, 1.0f));
			s.Rotation = (float)Rng.RandfRange(-0.5f, 0.5f); // a clump never puffs identically
			s.Scale = new Vector2(PUFF_FROM, PUFF_FROM);
			s.Modulate = new Color(1.0f, 1.0f, 1.0f, PUFF_ALPHA);
			return;
		}
	}
	
	// grit puffs: spread + lift + clear over PUFF_LIFE, then park off-world and go fully
	// transparent so the next foot plant can reuse the slot
	private void UpdatePuffs(float delta)
	{
		for (int i = 0; i < _puffs.Count; i++)
		{
			if (_puffLife[i] <= 0.0)
				continue;
			_puffLife[i] -= delta;
			Godot.Sprite2D s = _puffs[i];
			if (_puffLife[i] <= 0.0)
			{
				s.GlobalPosition = new Vector2(-9000.0f, -9000.0f); // parked like every other pool
				s.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
				continue;
			}
			float k = 1.0f - (float)(_puffLife[i] / PUFF_LIFE); // 0 at the plant -> 1 as it clears
			float sc = PUFF_FROM + (PUFF_TO - PUFF_FROM) * k;
			s.Scale = new Vector2(sc, sc);
			s.Position = s.Position + new Vector2(0.0f, -PUFF_RISE * delta); // lifts off the plant
			s.Modulate = new Color(1.0f, 1.0f, 1.0f, PUFF_ALPHA * (1.0f - k));
		}
	}
	
	// Asper seat upkeep: waiting insects wilt after INSECT_WAIT (pick); a standing ally
	// walking within HELPER_PICK claims one (user's "getting close"): it rides 26 px above
	// the host and fires the seat's own longbow numbers - same range, same attack speed,
	// half the damage rolled (user 2026-10-25). No prey leaves the cooldown open so the
	// first shot looses the instant prey arrives. host lookup keys on memberIndex.
	private void UpdateInsects(float delta)
	{
		if (_leader == null)
		return;
		PartyMember seat = _leader;
		for (int i = 0; i < _insects.Count; i++)
		{
			Godot.Node2D s = _insects[i];
			if (s.GlobalPosition.X < -8000.0f)
			continue; // free slot: same idiom as the summon pool
			if (_insectOwner[i] >= 0)
			{
				// bound leg: ride the host, fire on the seat's own cooldown
				_insectLife[i] -= delta;
				if (_insectLife[i] <= 0.0)
				{
					DespawnInsect(i); // the 10 s ride is up (user)
					continue;
				}
				PartyMember host = null;
				foreach (PartyMember mm in _party)
					if (mm.memberIndex == _insectOwner[i])
						host = mm;
				if (host == null)
				{
					DespawnInsect(i); // host left the squad: the ride drops, the slot frees
					continue;
				}
				// 2026-11-09 (user: passive bees need the SAME head-float as the ult swarm): the fixed
				// 26 px ride becomes the bee anchor law - head lift + sway + bob, per-slot phase from
				// the ride clock + slot index (world time only, no real-time leak)
				float iph = (float)(INSECT_LASTS - _insectLife[i]); // seconds riding: bob phase
				s.GlobalPosition = host.GlobalPosition + new Vector2((float)System.Math.Sin(iph * 2.1 + (float)i * 1.7f) * 0.8f, -(float)INSECT_HEAD_LIFT + (float)System.Math.Sin(iph * 6.0 + (float)i * 1.7f) * (float)BEE_BOB_PX);
				_insectCd[i] -= delta;
				if (_insectCd[i] > 0.0)
				continue; // shot window still open
				Godot.Node2D prey = null;
				float nd = (float)seat.EffRange();
				for (int e = 0; e < _enemies.Count; e++)
				{
					Godot.Node2D en = _enemies[e];
					Combatant nc = (Combatant)en;
					if (nc == null || nc.kind == "dead" || nc.hp <= 0.0f)
					continue; // free / dead bodies are not prey
					float dd = WrapDelta(en.GlobalPosition, s.GlobalPosition).Length();
					if (dd < nd)
					{
						nd = dd;
						prey = en;
					}
				}
				if (prey == null)
				continue; // cooldown stays open (see comment above)
				Vector2 dir = WrapDelta(s.GlobalPosition, prey.GlobalPosition).Normalized();
				double dmg = seat.RollDamage(this, seat.weaponDamage) * 0.5; // half damage (user)
				SpawnShot(s.GlobalPosition + dir * 18.0f, dir * seat.projSpeed, dmg, seat.projLife, 0.0, 0.0, 0.0, 0.0, true); // 2026-11-09 (user): friendly helper bees shoot stingers; defaults stamped because stingerArt follows them
				_insectCd[i] = seat.EffCooldown(); // same attack speed as Asper (user)
			}
			else
			{
				// waiting leg: nearest standing ally within HELPER_PICK claims this vigil
				_insectLife[i] -= delta;
				if (_insectLife[i] <= 0.0)
				{
					DespawnInsect(i); // nobody came close in time: the vigil wilts (pick)
					continue;
				}
				Vector2 ip = s.GlobalPosition;
				PartyMember host = null;
				float hd = (float)HELPER_PICK;
				foreach (PartyMember mm in _party)
				{
					if (!mm.joined || mm.downed || mm.eliminated)
					continue; // stranded / fallen bodies cannot claim a helper
					float dd = WrapDelta(mm.GlobalPosition, ip).Length();
					if (dd < hd)
					{
						hd = dd;
						host = mm;
					}
				}
				if (host == null)
				continue;
				_insectOwner[i] = host.memberIndex;
				_insectLife[i] = INSECT_LASTS; // the 10 s ride starts AT pickup (user)
				_insectCd[i] = 0.0; // the first volley looses the moment the ride starts
				_lbInsectBound++;
				GD.Print("[10thAge] (bonus) insect joins " + host.displayName + " for 10 s");
			}
		}
	}

	// Swarm of Bees cast (Asper ult, user 2026-10-26): six bees on a compass ring
	// around the seat (user: circle + spread out to hunt): slot i keeps bearing i*60
	// deg while searching, and the nearest body inside BEE_FIND latches it. No Rng
	// draws - a fixed fan so the run stream stays put.
	public void SpawnBees(Vector2 center)
	{
		_beeDrained = 0;
		_beesUp = 0;
		_beeSwarmUp = true;
		for (int i = 0; i < _bees.Count; i++)
		{
			double a = System.Math.PI * 2.0 * i / BEE_COUNT;
			_beeLife[i] = BEE_LASTS;
			_beeBite[i] = 0.0;
			_beeRay[i] = a;
			_beeTarget[i] = -1L;
			Godot.Node2D s = _bees[i];
			s.GlobalPosition = WrapNearCamera(center + new Vector2((float)System.Math.Cos(a), (float)System.Math.Sin(a)) * (float)BEE_R);
			((Godot.ColorRect)s.GetChild(0)).Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f);
			_beesUp++;
		}
	}
	
	// swarm upkeep: a latched bee rides its prey and chews 5 dmg/s (user 2026-11-06: was 10); a hunting bee
	// drifts along its spawn ray (user). Prey resolves by instance id because dead bodies
	// QueueFree: an id that stops resolving means the quarry dropped WHILE bitten -
	// attribution is heuristic (another hand may have landed the last blow), printed
	// once per cast as the dispersal tally.
	private void UpdateBees(float delta)
	{
		Godot.Collections.Array<Godot.Node2D> enemies = GetEnemies();
		for (int i = 0; i < _bees.Count; i++)
		{
			Godot.Node2D s = _bees[i];
			if (s.GlobalPosition.X < -8000.0f)
				continue; // free slot: same idiom as every pool
			_beeLife[i] -= delta;
			s.GlobalPosition = WrapNearCamera(s.GlobalPosition);
			Godot.Node2D prey = null;
			if (_beeTarget[i] >= 0)
			{
				foreach (Godot.Node2D n in enemies)
				{
					if ((long)n.GetInstanceId() == _beeTarget[i])
					{
						prey = n;
						break;
					}
				}
				if (prey == null)
				{
					_beeTarget[i] = -1L;
					_beeBite[i] = 0.0;
					_beeDrained += 1; // the quarry dropped while this bee was biting it
				}
			}
			else
			{
				// 2026-11-09 (user: ult bees "seek out different enemies unless there are no other
				// enemies nearby"): TWO bests in one scan - the nearest UNCLAIMED body wins outright so
				// the swarm spreads one quarry per bee; a bee only falls back to the nearest CLAIMED body
				// when every body in BEE_FIND reach is already bitten into - it never starves or orbits.
				// Dead bodies are skipped so a corpse can never be a latch (same skip as the insect prey scan).
				double bestFree = BEE_FIND * BEE_FIND;
				Godot.Node2D pickFree = null;
				double bestTaken = BEE_FIND * BEE_FIND;
				Godot.Node2D pickTaken = null;
				foreach (Godot.Node2D n in enemies)
				{
					Combatant nc = (Combatant)n;
					if (nc == null || nc.kind == "dead" || nc.hp <= 0.0f)
						continue; // free / dead bodies are not prey
					Vector2 off = WrapDelta(s.GlobalPosition, n.GlobalPosition);
					double d2 = off.X * off.X + off.Y * off.Y;
					if (d2 >= BEE_FIND * BEE_FIND)
						continue; // out of reach for either pick
					long iid = (long)n.GetInstanceId();
					bool taken = false;
					for (int q = 0; q < _beeTarget.Count; q++)
					{
						if (q == i)
							continue; // this bee is hunting (_beeTarget[i] = -1 here)
						if (_beeTarget[q] == iid)
						{
							taken = true;
							break;
						}
					}
					if (taken)
					{
						if (d2 < bestTaken)
						{
							bestTaken = d2;
							pickTaken = n;
						}
					}
					else
					{
						if (d2 < bestFree)
						{
							bestFree = d2;
							pickFree = n;
						}
					}
				}
				Godot.Node2D pick = (pickFree != null) ? pickFree : pickTaken; // unclaimed quarry first (user law)
				if (pick != null)
				{
					prey = pick;
					_beeTarget[i] = (long)pick.GetInstanceId();
					_beeBite[i] = 0.0; // the first chomp lands the moment the bee rides on
				}
			}
			if (prey != null)
			{
				// latched anchor (user 2026-11-08: the bee floats near the HOST's HEAD, not the side): re-anchored EVERY
				// frame to head lift + per-bee bob phase (flight clock + spawn bearing: world time, no real-time leak),
				// so a moving host carries its passenger AND the approach walks to THE HEAD - never a body-side park
				// plus a jump: the walk and the ride speak the SAME anchor
				float bt = (float)(BEE_LASTS - _beeLife[i]); // seconds flown: bob phase per bee
				Vector2 anchor = prey.GlobalPosition + new Vector2((float)System.Math.Sin(bt * 2.1 + _beeRay[i]) * 0.8f, -(float)BEE_HEAD_LIFT + (float)System.Math.Sin(bt * 6.0 + _beeRay[i]) * (float)BEE_BOB_PX);
				Vector2 off = WrapDelta(s.GlobalPosition, anchor);
				if (off.X * off.X + off.Y * off.Y > BEE_LATCH * BEE_LATCH)
				{
					s.GlobalPosition = WrapNearCamera(s.GlobalPosition + off.Normalized() * (float)(BEE_SPEED * delta));
					_beeBite[i] = 0.0; // walking in: the chew only starts once it is riding
				}
				else
				{
					s.GlobalPosition = WrapNearCamera(anchor); // riding: snap EXACTLY onto the head anchor this frame (the bob moves the anchor, so this is the float)
					_beeBite[i] -= delta;
					if (_beeBite[i] <= 0.0)
					{
						_beeBite[i] = 1.0; // 10 dmg per SECOND (user): whole-second chomps
						((Combatant)prey).TakeDamage(BEE_BITE); // flat bite: no crit, no tag lane
					}
				}
			}
			else
			{
				// hunting: drift along the spawn ray (user: spread out in all directions)
				Vector2 dir = new Vector2((float)System.Math.Cos(_beeRay[i]), (float)System.Math.Sin(_beeRay[i]));
				s.GlobalPosition = WrapNearCamera(s.GlobalPosition + dir * (float)(BEE_SPEED * delta));
			}
			if (_beeLife[i] <= 0.0)
			{
				DespawnBee(i);
				_beesUp -= 1;
				if (_beeSwarmUp && _beesUp <= 0)
				{
					_beeSwarmUp = false;
					GD.Print("[10thAge] (ult) Swarm of Bees disperses: " + _beeDrained + " prey drained by 6 bites of 5 dmg/s over 10 s");
				}
			}
		}
	}
	
	// Hapi column paint: PartyMember commits the column radius (PILLAR_R x Pest rank) alongside the
	// hit, so the paint only reads the numbers. Silent skip while art is missing: the DoT already
	// landed, only the column drops. Columns ride z 3 over the bodies, same lane as the beam lance.
	public void SpawnPillarFx(Vector2 p, double radius)
	{
		for (int i = 0; i < _pillarFx.Count; i++)
		{
			if (_pillarT[i] > 0.0)
				continue; // busy slot: a fading column still reads; this volley takes the next free one
			Godot.Sprite2D s = _pillarFx[i];
			_pillarT[i] = PILLAR_LIFE; // a column reads for half a second then fades
			if (pillarFxTex == null)
			{
				s.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f); // no art lane: keep the slot invisible while spent
				return; // silent skip: the DoT already committed at the caller
			}
			s.Texture = pillarFxTex;
			s.Scale = new Vector2((float)(radius * 2.0 / 95.0), (float)(radius * 2.0 / 95.0)); // uniform: the 95 px sprite covers the column width, height rides with it (NEVER squash the art)
			s.Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f); // fresh paint: UpdateUndead fades it down each frame
			// sprite origin is center: stand the column on the body foot line (half the SCALED 176 px height)
			s.GlobalPosition = WrapNearCamera(p + new Vector2(0.0f, (float)(-88.0 * radius * 2.0 / 95.0)));
			return;
		}
		GD.Print("[10thAge] (hapi) every column slot is busy: this volley paints nothing"); // overflow: the hits still landed
	}
	// Hapi per-frame upkeep: fade spent columns, shamble + chomp risen bodies, and walk the
	// Necrotic Plague (opt-in while any joined member ranks the plague pick). Column paint is
	// PartyMember-committed: this lane only reads the numbers.
	private void UpdateUndead(double delta)
	{
		for (int i = 0; i < _pillarFx.Count; i++) // pillar fade FIRST: half a second then dust
		{
			if (_pillarT[i] <= 0.0)
				continue; // spent slot
			_pillarT[i] -= delta;
			double f = _pillarT[i] / PILLAR_LIFE;
			if (f < 0.0)
				f = 0.0;
				_pillarFx[i].Modulate = new Color(1.0f, 1.0f, 1.0f, (float)f); // linear fade to dust
			}
		int plagueRank = 0; // Necrotic Plague: the best rank among joined members drives the tick cough
		foreach (PartyMember m in _party)
		{
			if (m.joined && (int)m.plagueR > plagueRank)
				plagueRank = (int)m.plagueR; // the highest-ranked joined member coughs hardest
		}
		Godot.Collections.Array<Godot.Node2D> enemies = GetEnemies(); // horde snapshot once
		for (int i = 0; i < _undead.Count; i++)
		{
			Godot.Node2D s = _undead[i];
			if (s.GlobalPosition.X <= -8000.0f)
				continue; // parked slot
			_undLife[i] -= delta;
			if (_undLife[i] <= 0.0)
			{
				DespawnUndead(i);
				GD.Print("[10thAge] (hapi) a risen body turns to dust"); // its own 10 s done
				continue;
			}
			s.GlobalPosition = WrapNearCamera(s.GlobalPosition); // fold-follow like every pool
			Godot.Node2D pn = null; // nearest live prey within UNDEAD_FIND, wrap-squared
			double best = UNDEAD_FIND * UNDEAD_FIND;
			foreach (Godot.Node2D n in enemies)
			{
				Combatant nc = (Combatant)n;
				if (nc == null || nc.kind == "dead" || nc.hp <= 0.0f)
					continue; // dead bodies are not prey (same skip as the insect scan)
				Vector2 off = WrapDelta(s.GlobalPosition, n.GlobalPosition);
				double d2 = off.X * off.X + off.Y * off.Y;
				if (d2 < best)
				{
					best = d2;
					pn = n;
					}
				}
			if (pn != null)
			{
				Combatant pc = (Combatant)pn;
				double touch = (double)pc.hitR + UNDEAD_TOUCH;
				Vector2 off = WrapDelta(s.GlobalPosition, pn.GlobalPosition);
				double d2 = off.X * off.X + off.Y * off.Y;
				if (d2 > touch * touch)
				{
					s.GlobalPosition = WrapNearCamera(s.GlobalPosition + off.Normalized() * (float)(_undSpd[i] * delta)); // shamble toward prey
					}
					else
					{
						_undCd[i] -= delta;
						if (_undCd[i] <= 0.0)
						{
							_undCd[i] = 1.0; // 1 s chomps
							pc.TakeDamage(_undDmg[i]); // bite = the corpse's own contact damage
							}
						}
				}
		}
		// NECROTIC PLAGUE (rebalanced 2026-11-18 hand-play): the curse stopped riding risen bodies on
		// contact, which read as free spread across the horde. It coughs on the FLAME ITSELF. One flame
		// tick per second of the DoT window: every living, flamed body rolls PLAGUE_CHANCE x rank (user:
		// 10/20/30%), and a hit stamps one nearby un-flamed body inside PLAGUE_SPREAD with the window
		// THAT carrier still has - so a cough can never outlive the flame that bred it. Stamps are banked
		// first: a fresh cough never rolls its own tick inside the SAME wave.
		_plagueTick += delta;
		if (plagueRank > 0 && _plagueTick >= 1.0)
		{
			_plagueTick -= 1.0; // the DoT's own 1 dmg/s cadence: one roll per second per carrier
			double pch = PLAGUE_CHANCE * (double)plagueRank; // user: 10/20/30% per rank
			int en = enemies.Count;
			Godot.Node2D[] cough = new Godot.Node2D[en];
			double[] coughDps = new double[en];
			double[] coughDur = new double[en];
			int coughN = 0;
			foreach (Godot.Node2D n in enemies)
			{
				Combatant nc = (Combatant)n;
				if (nc == null || nc.kind == "dead" || nc.hp <= 0.0f || nc.necrosisT <= 0.0)
					continue; // only a living, flamed carrier ever coughs
				if (Rng.Randf() >= pch)
					continue; // the tick missed: the flame just keeps burning
				Godot.Node2D t = null; // nearest FRESH body in cough range, wrap-squared like every pool
				double best2 = 1e9;
				foreach (Godot.Node2D m2 in enemies)
				{
					Combatant mc = (Combatant)m2;
					if (mc == null || mc.kind == "dead" || mc.hp <= 0.0f || mc.necrosisT > 0.0)
						continue; // already-flamed bodies never re-carry
					Vector2 off = WrapDelta(n.GlobalPosition, m2.GlobalPosition);
					double dd = (double)System.Math.Sqrt(off.X * off.X + off.Y * off.Y) - (double)mc.hitR;
					if (dd > PLAGUE_SPREAD || dd >= best2)
						continue; // the cough ring gates the painted silhouette, nearest body answers
					best2 = dd;
					t = m2;
				}
				if (t != null)
				{
					cough[coughN] = t;
					coughDps[coughN] = nc.necrosisDps; // the carrier's dps rides the cough unchanged
					coughDur[coughN] = nc.necrosisT;    // the CURRENT window: a short flame coughs a short flame
					coughN++;
				}
			}
			for (int q = 0; q < coughN; q++)
				((Combatant)cough[q]).Necrotic(coughDps[q], coughDur[q]); // one wave per second, banked first
		}
	}
	private void DespawnBee(int i)
	{
		Godot.Node2D s = _bees[i];
		s.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
		((Godot.ColorRect)s.GetChild(0)).Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
	}
	// Hapi (2026-11-17 + 2026-11-18 hand-play fixes): raise a corpse as a risen ally that SEEKS
	// the horde (UNDEAD_FIND reach) and TAUNTS it - conversions pull too. half = leader
	// bonus halves ride HALF stats (hp / bite / speed) but FULL corpse art (2026-11-18 hand-play:
	// half paint read far too small); conversions ride the corpse's own stats. The corpse's OWN Sprite2D is found by a DEPTH WALK - the Enemy rigs its
	// art under the sway node, so the flat child scan never saw it and the risen read as a green
	// square. The paint rides the corpse's own texture + scale + offset, tinted sickly green (0.55,
	// 0.8, 0.35) so a risen body reads as a zombie of the body it used to be.
	private void SpawnUndead(Combatant c, bool half)
	{
		float k = half ? 0.5f : 1.0f;
		int slot = -1; // free-slot scan like every pool: parked means free
		for (int si = 0; si < _undead.Count; si++)
		{
			if (_undead[si].GlobalPosition.X <= -8000.0f)
			{
				slot = si;
				break;
			}
		}
		if (slot == -1)
		{
			GD.Print("[10thAge] (hapi) the graveyard is full: no free risen slot"); // pool overflow: log, never stall
			return;
		}
		Godot.Node2D s = _undead[slot];
		s.GlobalPosition = WrapNearCamera(c.GlobalPosition);
		((Godot.ColorRect)s.GetChild(0)).Color = new Color(0.55f, 0.8f, 0.35f, 0.9f);
		_undHp[slot] = (double)c.maxHp * k; // REAL hp lane: the horde can fell a risen body
		_undDmg[slot] = (double)c.contactDamage * k; // bite = the corpse's own contact damage
		_undSpd[slot] = ((Enemy)c).speed * k; // chase speed = the corpse's own speed
		_undLife[slot] = UNDEAD_LIFE; // 10 s after THEIR OWN rise: conversions outlive the 10 s window edge
		_undCd[slot] = 0.5; // first chomp half a second after rising
		_undTaunt[slot] = 1; // ALL risen bodies taunt: ult conversions yank the horde too (2026-11-18 hand-play: "which I think we missed out for his ultimate")
		Godot.Sprite2D art = (Godot.Sprite2D)s.GetChild(1);
		art.Texture = null; // stale paint never rides a fresh corpse
		Godot.Sprite2D src = CorpseArt(c, 3); // the corpse's OWN art, walked THROUGH the sway rig
		if (src != null && src.Texture != null)
		{
			art.Texture = src.Texture;
			art.Position = src.GlobalPosition - c.GlobalPosition; // the paint keeps its offset off the body
			art.Scale = src.Scale; // FULL corpse paint for both lanes: halves ride half STATS only (2026-11-18 hand-play: the half-paint rises read much smaller than the corpse the user called)
			art.FlipH = src.FlipH;
			art.Modulate = new Color(0.55f, 0.8f, 0.35f, 1.0f); // zombie read: the corpse's OWN art, tinted
			((Godot.ColorRect)s.GetChild(0)).Color = new Color(0.55f, 0.8f, 0.35f, 0.0f); // painted body: the rect stops drawing
		}
	}

	// Risen art pickup: the first TEXTURED Sprite2D under the body, walked by DEPTH. The Enemy hangs
	// its art under the sway rig (_rig.AddChild(_spr)), so the old flat GetChildren scan never saw it
	// and every risen body fell back to the green square the user called (2026-11-18 hand-play fix).
	private Godot.Sprite2D CorpseArt(Godot.Node body, int depth)
	{
		if (depth <= 0)
			return null;
		foreach (Godot.Node ch in body.GetChildren())
		{
			if (ch is Godot.Sprite2D && ((Godot.Sprite2D)ch).Texture != null)
				return (Godot.Sprite2D)ch;
			Godot.Sprite2D deeper = CorpseArt(ch, depth - 1);
			if (deeper != null)
				return deeper;
		}
		return null;
	}
	// Hapi shot intercept: nearest risen body inside the SAME 12 px bolt core members use.
	private Godot.Node2D UndeadAt(Vector2 p)
	{
		Godot.Node2D w = null;
		float nd = 1e9f;
		foreach (Godot.Node2D s in _undead)
		{
			if (s.GlobalPosition.X < -8000.0f)
				continue;
			float d = p.DistanceTo(s.GlobalPosition);
			if (d < 12.0f + UNDEAD_RADIUS && d < nd)
			{
				nd = d;
				w = s;
			}
		}

		// Martha answers the intercept too: she IS the horde's other player character, and the same
		// 12 px bolt core that reaches a risen body reaches her. hp > 0 = standing: broken iron is
		// not a shot target, so a knitting Martha never eats a bullet that was meant to fly through.
		if (_marthaBody != null && _marthaHp > 0.0 && _marthaBody.GlobalPosition.X > -8000.0f)
		{
			float md = p.DistanceTo(_marthaBody.GlobalPosition);
			if (md < 12.0f + MARTHA_RADIUS && md < nd)
			{
				nd = md;
				w = _marthaBody;
			}
		}
		return w;
	}
	public void UndeadHitBy(Godot.Node2D u, double dmg)
	{

		if (u == _marthaBody)
		{
			MarthaHitBy(dmg); // her own damage route: real hp, her armour, her evasion
			return; // she is not in the risen table, so the loop below could never spend her
		}
		for (int i = 0; i < _undead.Count; i++)
		{
			if (_undead[i] == u)
			{
				_undHp[i] -= dmg;
				if (_undHp[i] <= 0.0)
					DespawnUndead(i);
				return;
			}
		}
	}
	private void DespawnUndead(int i)
	{
		Godot.Node2D s = _undead[i];
		s.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
		((Godot.ColorRect)s.GetChild(0)).Color = new Color(0.55f, 0.8f, 0.35f, 0.0f);
	}
	// TAUNT QUERY (Call of the Grave + 2026-11-18 hand-play fix): nearest risen body of ANY flavor
	// (conversion or leader half) within TAUNT_PULL, wrap-corrected; null = zero chase change.
	// Parked slots (flag 0) never answer this; every live risen body does.
	public Godot.Node2D GetTauntBody(Vector2 pos)
	{
		Godot.Node2D w = null;
		double nd = 1e9;
		for (int i = 0; i < _undead.Count; i++)
		{
			Godot.Node2D s = _undead[i];
			if (s.GlobalPosition.X < -8000.0f || _undTaunt[i] == 0)
				continue;
			double d = WrapNearCamera(pos).DistanceTo(WrapNearCamera(s.GlobalPosition)); // wrap-corrected like every chase lane
			if (d < TAUNT_PULL && d < nd)
			{
				nd = d;
				w = s;
			}
		}

		// Martha is the horde's OTHER player character: within MARTHA_PULL she pulls chase exactly like
		// a risen body, which is why the Enemy needs no edit at all. hp > 0 = standing: a broken body is
		// iron nobody chews, and a parked one is off-field.
		if (_marthaBody != null && _marthaHp > 0.0 && _marthaBody.GlobalPosition.X > -8000.0f)
		{
			double md = WrapNearCamera(pos).DistanceTo(WrapNearCamera(_marthaBody.GlobalPosition));
			if (md < MARTHA_PULL && md < nd)
			{
				nd = md;
			w = _marthaBody;
			}
		}
		return w;
	}
	

//T// MARTHA (Hew's companion, user 2026-11-22): seat her singleton ONCE, like every other helper
//T// pool, and PARK her at -9000 until Hew joins (UpdateMartha does the seating). A missing sheet
//T// leaves the body invisible but the hit core still works: the greybox contract never depends on
//T// art, so her bite lane and the horde's pull both survive an art-less run.
	private void BuildMartha()
	{
		_marthaBody = new Godot.Node2D();
		_marthaBody.Name = "Martha";
		_marthaBody.GlobalPosition = new Vector2(-9000.0f, -9000.0f); // parked until Hew joins
		_marthaHp = MARTHA_HP; // hp is armed here, so a hand-play never reads a 0 pool
		_marthaT = 0.0;
		_marthaCd = 0.0;
		AddChild(_marthaBody); // persistent: never pooled, never QueueFreed
		// Gait rig: her art + FX stamps ride this Node2D, UpdateMarthaFx swings it with the ENEMY sway
		// law. The BODY node stays the hit core, so the sway never nudges anything the horde reads.
		_marthaRig = new Godot.Node2D();
		_marthaRig.Name = "Gait";
		_marthaBody.AddChild(_marthaRig);
		Godot.Texture2D tex = GD.Load("res://art/characters/martha.png") as Godot.Texture2D; // 104x97 sheet, on the user's WIP rail
		if (tex == null)
			return; // greybox: she still bites, still gets chewed, she just has no sheet
		Godot.Sprite2D art = new Godot.Sprite2D();
		art.Texture = tex;
		art.Set("texture_filter", 0); // NEAREST like every other pool sprite
		art.Set("z_index", 2); // body band: a rank under the ~90 px PCs (Hew reads 256 x 0.35)
		double ms = 0.65; // assistant pick (retune by eye): the 97 px sheet lands ~63 px tall
		art.Scale = new Vector2((float)ms, (float)ms);
		// rim 0: her base band sits at the canvas bottom, so pin the sheet bottom to the node origin
		art.Position = new Vector2(0.0f, (float)(-(tex.GetHeight() * 0.5) * ms));
		_marthaRig.AddChild(art); // art rides the gait rig: the swing animates the sheet, not the hit core
	}
//T// The Hew member, or null while he is off-field (never joined / eliminated). He is the ONLY seat
//T// Martha listens to: no Hew = no body on the field, and every lane below keeps that read.
	private PartyMember FindHew()
	{
		foreach (PartyMember m in _party)
		{
			if (m.displayName == "Hew")
				return m.joined && !m.eliminated ? m : null; // downed is NOT off-field: he is still a seat
		}
		return null;
	}
//T// MARTHA upkeep (the lane UpdateHelpers calls once a frame): parked while he is off-field; hunts,
//T// bites, leashes home, knits when broken. She is NOT a PartyMember, so none of the party lanes
//T// see her; the horde instead reaches her through GetTauntBody / UndeadAt / UndeadHitBy, which is
//T// what "enemies attack her as another player character" needs - and no Enemy.cs edit at all.
	// ROUND 8 party mirror (user: "treat her like she is another party member"): MirrorPartyBuffs
	// re-reads the standing squad's live buff clocks into the _mu* windows every frame (it NEVER
	// decrements - the PMs own their decay). Every lane under this call folds those windows onto
	// her at the PMs' OWN numbers; where a kit law has no her-side read the fold is an assistant
	// pick, retune by hand. (round-8 draft)
	private void UpdateMartha(float delta)
	{
		MirrorPartyBuffs();
		// ROUND 8 move fold (assistant pick, retune): the party's move lanes - Alacrity elixir
		// (+10%) and Assemble (+25%) - ride her scuttle, rally rush and leash alike. Awaken's
		// double lane is REACH (her bite band), not a walk lane.
		double mvf = (_muElixSwiftT > 0.0 ? 1.1 : 1.0) * (_muOakT > 0.0 ? 1.25 : 1.0);
		// ROUND 8 gear rail GROUND half (user: "make it stay on the ground until picked up"): a
		// walker welds a cog on her own at the same 18 px contact band the Pickup rail uses. She
		// is no member, so the Pickup's own member census can never reach her: she welds here,
		// frees the cog herself, and never rides it.
		if (_marthaBody != null && _marthaHp > 0.0 && _marthaT <= 0.0)
		{
			// the standing child-walk idiom: Godot.Node, never Godot.Node2D - the scene carries
			// Control children and an implicit Node2D cast THROWS InvalidCast per frame (PD229)
			foreach (Godot.Node nd in GetChildren())
			{
				Pickup pc3 = nd as Pickup; // null for every non-cog child, so no cast throws
				if (pc3 != null && pc3.pk == 3 && WrapDelta(pc3.GlobalPosition, _marthaBody.GlobalPosition).Length() < 18.0f)
				{
					CollectGearPart(pc3.gearKind, pc3.GlobalPosition); // shared weld math (damp/tally inside)
					pc3.QueueFree(); // the cog is spent: nobody welds it twice
				}
			}
		}
		PartyMember hew = FindHew();
		// off-field: park her and drop every timer, so a later join cannot burst banked time (the same
		// drain-to-0 discipline the Yu spirit clock uses for a downed seat)
		if (hew == null || _marthaBody == null)
		{
			if (_marthaBody != null)
				_marthaBody.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
			return;
		}
		if (_marthaBody.GlobalPosition.X <= -8000.0f)
		{
			// FIRST tick after he joins: seat her at his flank. The lazy seat is why she needs no join
			// hook at all - the stage-1 lottery can fire on any frame.
			_marthaHp = MARTHA_HP + _marthaHpBonus + MARTHA_VITALS;
			_marthaT = 0.0;
			_marthaCd = 0.5; // a half-second wind-up, then the bite clock
			_marthaBody.GlobalPosition = WrapNearCamera(hew.GlobalPosition + new Vector2(-40.0f, 0.0f));
			if (_pilot)
				GD.Print("[10thAge] (martha) Martha crawls out beside Hew (" + (MARTHA_HP + MARTHA_VITALS) + " hp / 3 armour, 10 px bite)");
		}
		_marthaBody.GlobalPosition = WrapNearCamera(_marthaBody.GlobalPosition); // fold-follow like every pool
		// BROKEN lane (user: at 0 hp she is broken 20 s and repairs herself). The countdown runs 2x while
		// Hew stands within MARTHA_NEAR (assistant pick: the user asked for a Hew-close repair and left
		// the rate open). Broken iron neither hunts, bites nor drinks.
		if (_marthaT > 0.0)
		{
			_marthaT -= delta * (WrapDelta(hew.GlobalPosition, _marthaBody.GlobalPosition).Length() <= MARTHA_NEAR ? 2.0 : 1.0);
			if (_marthaBody.GetChildCount() > 0)
			{
				// dim tint: a hand-play can SEE why she is out, and the tint fades back as she knits.
				// PD231 FIX: since the FX-2 gait lap the art Sprite rides INSIDE the "Gait" Node2D rig,
				// so body's child 0 is the rig and a hard Sprite2D cast THREW InvalidCast per broken frame
				// (never seen before: the bare-pilot seats never left Martha BROKEN; a Hew-led seat does).
				// ride the art through a safe `as` (null in the greybox with no sheet -> skip the tint)
				Godot.Sprite2D tint = _marthaRig != null ? _marthaRig.GetChild(0) as Godot.Sprite2D : _marthaBody.GetChild(0) as Godot.Sprite2D;
				if (tint != null)
					tint.Modulate = new Color(0.5f, 0.5f, 0.6f, (0.45f + 0.55f * (float)(1.0 - _marthaT / MARTHA_BREAK)) * (0.92f + 0.08f * (float)System.Math.Abs(System.Math.Sin(_marthaT * 9.0)))); // FX-lap: the tint BREATHES while she knits - a dim static pulse on the flicker beat
			}
			if (_marthaT <= 0.0)
			{
				_marthaT = 0.0;
				_marthaHp = MARTHA_HP + _marthaHpBonus + MARTHA_VITALS; // knitted back to her OWN ceiling, parts included
				_marthaRally = true; // revived: the RALLY below makes her run back to Hew before the hunt resumes (user 2026-11-24 round 7)
				_marthaCd = 0.5; // the fresh line bites after a wind-up, not straight out of the yard
				if (_pilot)
					GD.Print("[10thAge] (martha) Martha knits herself back to full"); // the 20 s done
			}
			return; // broken: nothing else this frame
		}
		// ROUND 8 regen fold (PM lane read, numbers THEIRS): Menders Brew and Assemble each knit
		// 1% of her OWN ceiling per second while any member runs them - the same EffMaxHp*0.01/s
		// law the PMs pay, never while broken (the lane above already returned).
		{
			double regenCap = MARTHA_HP + _marthaHpBonus + MARTHA_VITALS;
			double rate = (_muElixHealT > 0.0 ? 1.0 : 0.0) + (_muOakT > 0.0 ? 1.0 : 0.0);
			if (rate > 0.0 && _marthaHp < regenCap)
			{
				_marthaHp += regenCap * 0.01 * rate * delta;
				if (_marthaHp > regenCap)
					_marthaHp = regenCap; // clamp at her own ceiling like every other heal lane
			}
		}
		// RALLY (user 2026-11-24 round 7): after a revive she is FULL hp at the fell (G6 knits her back to her OWN
		// ceiling, parts included) and RUNS back to Hew BEFORE the seek>kill lanes below resume - while the flag
		// rides, closing only stops the rush once she is inside a 42 px flank seat (assistant fill of their "rush":
		// 2x her scuttle + the seat, retune by eye). The broken lane above returned early every knitting frame, so
		// the rush starts the frame the yard-time finishes; knit-speed still doubles as she closes inside MARTHA_NEAR.
		if (_marthaRally)
		{
			Vector2 rush = WrapDelta(hew.GlobalPosition, _marthaBody.GlobalPosition); // her offset FROM Hew
			if (rush.Length() > 42.0f)
			{
				_marthaBody.GlobalPosition = WrapNearCamera(_marthaBody.GlobalPosition - rush.Normalized() * (float)(MARTHA_SPEED * 2.0 * mvf * delta)); // the rush: homing idiom at 2x her scuttle
				return; // hunt lanes stay closed until she is back beside Hew (user law: rush BEFORE resuming seek>kill)
			}
			_marthaRally = false; // on his flank: the seek>kill resumes on THIS frame
		}
		// hunt: nearest live prey within MARTHA_FIND, wrap-squared, with the SAME body skip the risen
		// scan uses (dead / spent entries are not prey). Bosses ARE prey: she is a chomper, not a bolt.
		Godot.Collections.Array<Godot.Node2D> enemies = GetEnemies();
		Godot.Node2D pn = null;
		double best = MARTHA_FIND * MARTHA_FIND;
		foreach (Godot.Node2D n in enemies)
		{
			Combatant nc = (Combatant)n;
			if (nc == null || nc.kind == "dead" || nc.hp <= 0.0f)
				continue; // dead bodies are not prey
			// HUNT GATE (user 2026-11-23 hand-play: "Martha should be trying to stay close while still
			// running in to attack enemies"): prey only counts when it is within MARTHA_HUNT px of HEW,
			// so she never sprints off the flank after a straggler - attackers on Hew are her prey as always.
			Vector2 hoff = WrapDelta(hew.GlobalPosition, n.GlobalPosition); // prey distance FROM Hew
			if (hoff.X * hoff.X + hoff.Y * hoff.Y > MARTHA_HUNT * MARTHA_HUNT)
				continue; // flank guard: out of Hew's guard ring - hold the line, do not chase
			Vector2 off = WrapDelta(_marthaBody.GlobalPosition, n.GlobalPosition);
			double d2 = off.X * off.X + off.Y * off.Y;
			if (d2 < best)
			{
				best = d2;
				pn = n;
			}
		}
		if (pn != null)
		{
			Combatant pc = (Combatant)pn;
			// Radius parts widen her touch band (the user's radius card boosts a random stat for the whole
			// squad, so reach belongs to her bite too, exactly like the cannon band widens)
			double touch = (double)pc.hitR + (double)MARTHA_RADIUS * _marthaRad;
			Vector2 off = WrapDelta(_marthaBody.GlobalPosition, pn.GlobalPosition);
			double d2 = off.X * off.X + off.Y * off.Y;
			bool ult = hew.ultT > 0.0; // Maximum Firepower!: +25% damage AND +25% attack speed on HER
			if (d2 > touch * touch)
			{
				// chomp band not reached yet: scuttle along the SHORTEST (wrap) route toward the prey
				_marthaBody.GlobalPosition = WrapNearCamera(_marthaBody.GlobalPosition + off.Normalized() * (float)(MARTHA_SPEED * mvf * delta));
				_marthaCd = 0.0; // re-set, not banked: she only bites from a standstill inside the band
			}
			else
			{
				_marthaCd -= delta;
				if (_marthaCd <= 0.0)
				{
					// ROUND 8 bite folds (PM kit laws read; the compound is an assistant pick, retune):
					// cadence rides Beastial Rage (+50% attack speed) and Inquisitive Mind (+25%) like the
					// PMs' own readers - no multistrike lane exists for her. DEALT adds a running ALLY ult
					// (Maximum Firepower x1.5: MirrorPartyBuffs EXCLUDES Hew, so her own ult is ONLY the
					// 1.25 above, never double-counted), Inquisitive Mind (+25%), Awaken (+25%; its double
					// REACH has no clean her-side band, skipped) and Boldness (+10%); Precision widens the
					// crit odds +10% (the PM crit reader). Rage is NOT a damage term (their TakeHit lane
					// halves INCOMING instead - folded there).
					double spd = _marthaSpd * (ult ? 1.25 : 1.0) * (_muRageT > 0.0 ? 1.5 : 1.0) * (_muInsT > 0.0 ? 1.25 : 1.0);
					_marthaCd = MARTHA_BITE_EVERY / spd; // 1.5 s cadence, attack-speed parts and the ult on top
					double dealt = MARTHA_BITE * _marthaDmg * (ult ? 1.25 : 1.0) * (_muUltT > 0.0 ? 1.5 : 1.0) * (_muInsT > 0.0 ? 1.25 : 1.0) * (_muAwaT > 0.0 ? 1.25 : 1.0) * (_muElixBoldT > 0.0 ? 1.10 : 1.0);
					if (Rng != null && Rng.Randf() < MARTHA_CRIT + (_muElixPrecT > 0.0 ? 0.10 : 0.0))
						dealt *= 1.5; // her crit mirrors the engine's CRIT_DAMAGE base (1.5x)
					pc.TakeDamage(dealt);
					MarthaVampSip(dealt); // Vampiric Core (Hew card): she drinks what she bites
					if (pc.hp <= 0.0f || pc.kind == "dead")
						ExplosiveFinale(pn.GlobalPosition, dealt); // Explosive Finale: HER kill detonates the ring
					// FX-lap chomp stamp: ONE Sprite2D child of her body, posed toward the prey she just
					// bit (the bite's OWN wrap delta off is already in hand - body-anchored like the claw v5,
					// so the fold never drags the stamp). Null art = blank sprite draws nothing: the damage
					// and the pilot read survive the greybox exactly like every other FX lane.
					if (_biteFx == null)
					{
						_biteFx = new Godot.Sprite2D();
						_biteFx.Texture = artOrNull("res://art/fx/martha_bite.png"); // rail art lands this lap; absent = invisible stamp
						_biteFx.Set("texture_filter", 0); // NEAREST like every other pool sprite
						_biteFx.Set("z_index", 4); // over her body (2): the chomp reads ON the prey, not behind the iron
						(_marthaRig != null ? _marthaRig : _marthaBody).AddChild(_biteFx); // rides her fold-follow AND her gait: the chomp swings with the body
					}
					_biteFx.Position = off; // body->prey local seat: the same delta the touch test used
					_biteFx.Set("rotation", (float)System.Math.Atan2(off.Y, off.X)); // chomp aimed down the bite line
					_biteFx.Scale = new Vector2(0.6f, 0.6f); // assistant pick (retune by eye): fangs read at prey scale
					_biteFx.Visible = true;
					_marthaFxT = 0.18; // the stamp's own ~0.18 s life: UpdateMarthaFx drops it off-beat
				}
			}
		}
		else
		{
			// nothing in find range: run home by the shortest route once she strays past the leash
			Vector2 home = WrapDelta(hew.GlobalPosition, _marthaBody.GlobalPosition); // her offset FROM Hew
			if (home.Length() > MARTHA_LEASH)
				_marthaBody.GlobalPosition = WrapNearCamera(_marthaBody.GlobalPosition - home.Normalized() * (float)(MARTHA_SPEED * mvf * delta));
			_marthaCd = 0.0;
		}
	}
//T// the horde's OTHER player character: an enemy that reached her chews HERE instead of the risen
//T// hp table (UndeadHitBy routes her body). Armour is a flat soak with a never-below-1 floor, the
//T// same read Game.TakeHit gives the ARMOUR tag, so her 3 plus parts can soften but never fence her.
	public void MarthaHitBy(double dmg)
	{
		if (_marthaBody == null || _marthaT > 0.0)
			return; // broken iron is already spent: no soak, no second break while she knits
		PartyMember hew = FindHew();
		if (hew != null && hew.ultT > 0.0)
		{
			if (_pilot)
				GD.Print("[10thAge] (martha) Maximum Firepower!: Martha is invulnerable while the ult runs");
			return; // user: the ult makes her INVULNERABLE for its 10 s
		}
		// ROUND 8 evade fold: the leader's evasion aura (Inquisitive Mind / Gekkota / Pride - the
		// user's "all allies" law) scales her pool. Kit-tag dodge lanes (Brook's EVASION stat) are
		// NOT mirrored per-kit - assistant pick, retune.
		if (Rng != null && Rng.Randf() < MARTHA_EVA * LeaderEvaMul())
		{
			if (_pilot)
				GD.Print("[10thAge] (martha) Martha twists out of the bite"); // her 5% evasion
			return;
		}
		// ROUND 8 soak fold (PM kit laws read): Resilience elixir takes x0.90 incoming (the TakeHit
		// law), Beastial Rage HALVES incoming (their kit law, same read), and Assemble's oak iron
		// adds its flat +5 armour on top of her parts. The 1-floor below still owns the never-fence
		// law (user round 7).
		double soak = dmg * (_muElixHardT > 0.0 ? 0.9 : 1.0) * (_muRageT > 0.0 ? 0.5 : 1.0) - (MARTHA_ARMOUR + _marthaArm + (_muOakT > 0.0 ? 5.0 : 0.0));
		if (soak < 1.0)
			soak = 1.0;
		_marthaHp -= soak;
		if (_marthaHp <= 0.0)
		{
			_marthaHp = MARTHA_HP + _marthaHpBonus + MARTHA_VITALS; // REVIVE AT FULL HP (user 2026-11-24 round 7): the feller who
				// takes her down does not drain her further - the 20 s in the yard IS the repair, and the strip + the
				// repair gauge below read a full pool under the knitting iron
			_marthaT = MARTHA_BREAK; // BROKEN for 20 s: the upkeep lane knits her back (2x while Hew is close)
			if (_pilot)
				GD.Print("[10thAge] (martha) Martha is DOWN - she spends 20 s in the yard knitting");
		}
	}
//T// Vampiric Core (Hew's card): she heals 5% of ALL damage she deals per rank (user round 7: 5/10/15%), clamped to her own
//T// ceiling (assistant pick: the cap keeps a lucky sip lane from trading whole pools into free
//T// healing). No sip while broken - she is not biting, so there is nothing to drink.
	public void MarthaVampSip(double dealt)
	{
		PartyMember hew = FindHew();
		if (hew == null || hew.vampRank <= 0.0 || _marthaT > 0.0)
			return; // no card, no lane
		double cap = MARTHA_HP + _marthaHpBonus + MARTHA_VITALS;
		if (_marthaHp >= cap)
			return; // already at the ceiling: a sip is never wasted on a full pool
		_marthaHp += dealt * 0.05 * hew.vampRank; // round 7 (user 2026-11-24): ranks drink 5/10/15% of the damage she deals
		if (_marthaHp > cap)
			_marthaHp = cap;
	}
//T// Explosive Finale (Hew's card): a Hew or Martha kill detonates a 25/50/75% blast into the horde.
//T// Ranks live on the Hew member (Game.cs has no upgrade registry of its own), so the lane reads
//T// them through FindHew. The ring never chains: the scan skips bodies that are already spent, so
//T// the one kill that fired it cannot fan out across the squad.
	public void ExplosiveFinale(Vector2 at, double dealt)
	{
		PartyMember hew = FindHew();
		if (hew == null || hew.exploRank <= 0)
			return; // the card is not in the pile: nothing detonates
		double blast = dealt * 0.25 * (double)hew.exploRank; // 25 / 50 / 75% of the kill that made it
		if (blast <= 0.0)
			return;
		double r2 = EXPLO_BLAST * EXPLO_BLAST; // assistant pick: the ring's px radius
		foreach (Godot.Node2D n in GetEnemies())
		{
			Combatant nc = (Combatant)n;
			if (nc == null || nc.kind == "dead" || nc.hp <= 0.0f)
				continue; // spent bodies never pop a second time
			Vector2 off = WrapDelta(at, n.GlobalPosition);
			if (off.X * off.X + off.Y * off.Y > r2)
				continue;
			nc.TakeDamage(blast);
			MarthaVampSip(blast); // the shockwave is HER damage too, so it drinks
		}
	}
//T// HEW "Upgrades!" leader bonus (user 2026-11-22): 5% per defeated enemy for a part boosting a
//T// RANDOM stat for ALL characters (and Martha), run-permanent, applied INSTANTLY on the kill (no
//T// pickup node to chase) and DIMINISHING: the n-th part of a kind lands at base x 0.9^n (assistant
//T// pick - the user left stacking open and a flat 5% read as a snowball). Bosses never feed the lane:
//T// OnEnemyKilled returns before this call for them. Downed bodies DO take parts (they are still on
//T// the field); eliminated ones do not, and every character reads the SAME run-wide tally.
	private void UpgradePartDrop(Vector2 at) // FX-lap: the kill site rides in so the part can DROP visibly
	{
		if (LeaderBonusKey() != 13)
			return; // the seat is not Hew's: the bonus is inert under every other leader
		if (Rng == null || Rng.Randf() >= 0.05)
			return; // the 5% roll
		int kind = (int)(Rng.Randf() * 5.0); // one of five stats, uniform (the user asked for RANDOM)
		if (kind > 4)
			kind = 4; // belt-and-braces: Randf is < 1
		// ROUND 8 GEAR RAIL (user: "make hews gear parts drop like xp would and make it stay on the
		// ground until picked up"): 2.5% of the GRANTED parts do not weld on the spot - they become
		// a real cog Pickup (pk=3) parked at the kill site, which never times out and welds only
		// when someone walks over it. The weld half is shared (ApplyGearPart below), so a ground
		// part is IDENTICAL to an instant one except for the wait. Pilot census kept for the smoke.
		if (Rng.Randf() < GEAR_PART_CH)
		{
			_upGearLoose++; // pilot census: loose cogs vs welds
			SpawnPickup(at, 3, kind); // pk=3: the gear cog; kind travels on the pickup
			DropCogFx(at); // the same tumble the instant lane plays: the cog lands, it does not fuse
			return; // NO instant weld: the part waits on the ground until someone walks over it
		}
		ApplyGearPart(kind, at);
	}
	// ROUND 8: the weld half split out so the gear rail GROUND lane can re-enter it: identical
	// damp/tally math, squad loop and Martha mirror. `at` only feeds the visible cog puff.
	// A ground cog re-enters through CollectGearPart below: the landed cog carries the landing
	// read and the walk-over re-puffs the fuse - like XP and every other Pickup, a walker hears
	// BOTH the drop and the fuse. (round-8 draft, retune by hand)
	private void ApplyGearPart(int kind, Vector2 at)
	{
		double damp; // the n-th part of THIS kind applies at base x 0.9^n
		int tally; // how many of this kind have landed (the pilot read only)
		if (kind == 0)
		{ damp = System.Math.Pow(0.9, (double)_upDmgN); _upDmgN++; tally = _upDmgN; }
		else if (kind == 1)
		{ damp = System.Math.Pow(0.9, (double)_upSpdN); _upSpdN++; tally = _upSpdN; }
		else if (kind == 2)
		{ damp = System.Math.Pow(0.9, (double)_upRadN); _upRadN++; tally = _upRadN; }
		else if (kind == 3)
		{ damp = System.Math.Pow(0.9, (double)_upArmN); _upArmN++; tally = _upArmN; }
		else
		{ damp = System.Math.Pow(0.9, (double)_upHpN); _upHpN++; tally = _upHpN; }
		string[] tag = { "damage", "attack speed", "radius", "armour", "hp" }; // pilot read only
		if (_pilot)
			GD.Print("[10thAge] (upgrades) part: " + tag[kind] + " x" + tally + " (dim " + (float)damp + ")");
		DropCogFx(at); // FX-lap: the part the roll granted, DROPS visibly - a cog tumbles where the kill fell
		foreach (PartyMember m in _party)
		{
			if (m.eliminated)
				continue; // a body off the run takes nothing
			// the multiplier tags are multipliers (base 1.0); HEALTH and ARMOUR are flat points
			if (kind == 0)
				m.stat[StatTags.ATTACK_DAMAGE] += 0.05 * damp;
			else if (kind == 1)
				m.stat[StatTags.ATTACK_SPEED] += 0.05 * damp;
			else if (kind == 2)
				m.stat[StatTags.AREA] += 0.05 * damp;
			else if (kind == 3)
				m.stat[StatTags.ARMOUR] += 1.0 * damp;
			else
			{
				// +5 hp raises the CEILING and the line: the part is a weld, not a promise
				m.stat[StatTags.HEALTH] += 5.0 * damp;
					// the CURRENT line follows through the PM _hpMaxSeen tracker (PartyMember.cs lifts hp 1:1 when
					// EffMaxHp rises). The old bump HERE double-lifted and let hp ride past the cap (user 2026-11-24
					// round 7: "the party HP upgrade should raise MAX hp too" - it now raises the ceiling and hp
					// follows exactly once).
			}
		}
		// Martha is not in _party, so she reads the SAME tally through her own fields
		if (kind == 0)
			_marthaDmg *= 1.0 + 0.05 * damp;
		else if (kind == 1)
			_marthaSpd *= 1.0 + 0.05 * damp;
		else if (kind == 2)
			_marthaRad *= 1.0 + 0.05 * damp;
		else if (kind == 3)
			_marthaArm += 1.0 * damp;
		else
		{
			_marthaHpBonus += 5.0 * damp;
			if (_marthaHp > 0.0)
				_marthaHp += 5.0 * damp; // a standing Martha gets the weld right now
		}
	}
	// ROUND 8: the gear rail's GROUND re-entry: a picked-up cog welds EXACTLY like a rolled one
	// (shared damp/tally math through ApplyGearPart). Called by the Pickup rail (pk=3), which
	// passes the cog's spot so the fuse puffs where the walker found it.
	public void CollectGearPart(int kind, Vector2 at)
	{
		ApplyGearPart(kind, at);
	}
	// ROUND 8 (user: "treat Martha like another party member" for ally/party buffs): she is not in
	// _party, so she SAMPLES the standing squad's buff clocks at the top of every Martha tick. She
	// takes the MAX over live bodies: one shared window while any source holds it up (a healed ally
	// refreshes the party's clocks - the mirror just reads whatever the squad is holding now). Clocks
	// decay in each PartyMember._Process, NEVER here. Hew's OWN ult is EXCLUDED (his Thunder Cannon
	// rework is not an ally buff; Martha already folds his ult on her bite). Every other kit window
	// counts FOR Hew: Gekkota Training, Beastial Fury, Battery of Small Magic are his party lanes.
	// Reader lanes (round-8 draft, retune by hand): Might -> bite x1.5 + cadence x2/3; Inspire ->
	// bite x1.25 + proc + dodge; Rage -> cadence x2/3 + half soak; Awaken -> bite x1.25 + bubble;
	// Oak -> armour +0.25 soak + dodge +0.25 + move x1.25 + regen tick; elixirs -> Bold x1.10 dealt,
	// Prec +0.10 crit, Swift x1.10 move, Hard -> soak x1/1.10, Mag -> bubble x1.10, Heal -> regen tick.
	private void MirrorPartyBuffs()
	{
		_muUltT = 0.0; _muInsT = 0.0; _muRageT = 0.0; _muAwaT = 0.0; _muOakT = 0.0;
		_muElixHealT = 0.0; _muElixSwiftT = 0.0; _muElixHardT = 0.0; _muElixBoldT = 0.0; _muElixPrecT = 0.0; _muElixMagT = 0.0;
		foreach (PartyMember m in _party)
		{
			if (!m.joined)
				continue; // a body not on the run yet holds no party
			if (m.displayName != "Hew")
				_muUltT = System.Math.Max(_muUltT, m.ultT);
			_muInsT = System.Math.Max(_muInsT, m.insT);
			_muRageT = System.Math.Max(_muRageT, m.rageT);
			_muAwaT = System.Math.Max(_muAwaT, m.awaT);
			_muOakT = System.Math.Max(_muOakT, m.oakT);
			_muElixHealT = System.Math.Max(_muElixHealT, m.elixHealT);
			_muElixSwiftT = System.Math.Max(_muElixSwiftT, m.elixSwiftT);
			_muElixHardT = System.Math.Max(_muElixHardT, m.elixHardT);
			_muElixBoldT = System.Math.Max(_muElixBoldT, m.elixBoldT);
			_muElixPrecT = System.Math.Max(_muElixPrecT, m.elixPrecT);
			_muElixMagT = System.Math.Max(_muElixMagT, m.elixMagT);
		}
	}

	// FX-lap (user 2026-11-23, approved as designed): Hew's part drops tumble visible cogs; Martha's
	// broken iron crackles. Both ride the cinder/bolt pool pattern: fixed slots, life clocks in the
	// Update pass, world-parked when idle. Numbers are assistant picks, retune by eye.
	private Godot.Texture2D artOrNull(string path)
	{
		// GD.Load THROWS GodotException on missing / unimported art (proven live 2026-11-24: an
		// unimported rail png threw EVERY frame from UpdateMarthaFx, killing _Process past it and
		// freezing input - the human-lane freeze). The null-safe the "absent = invisible" comments
		// always promised: no throw, no spam, the fallback paints instead.
		try { return GD.Load(path) as Godot.Texture2D; } catch { return null; }
	}
	private void DropCogFx(Vector2 at)
	{
		// first free slot looses a cog AT the drop site; art when the rail wired one, ember square when
		// not - the greybox never depends on the art (same contract as the bolt paint above).
		for (int i = 0; i < _cogLife.Count; i++)
		{
			if (_cogLife[i] > 0.0)
				continue; // live cogs keep their own frames
			_cogLife[i] = COG_LIFE;
			Godot.Node2D gn = _cogN[i];
			gn.GlobalPosition = at;
			gn.Position = new Vector2(0.0f, 0.0f); // the fall below drives Position: clear any banked offset
			_cogS[i].Texture = cogFxTex; // null art: the sprite draws nothing, the square below takes the paint
			_cogS[i].Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f);
			_cogR[i].Modulate = new Color(1.0f, 1.0f, 1.0f, cogFxTex == null ? 1.0f : 0.0f); // fallback paints only while the rail is absent
			_cogS[i].Visible = true;
			_cogR[i].Visible = true;
			break; // ONE cog per part: the part count and the cog count stay equal
		}
	}
	private void UpdateCogFx(float delta)
	{
		// cogs tumble (rotation on the slot node), fall COG_DROP px over their life, and fade in the
		// last half: the world clock like every pool above (soft-pause discipline, no real-time leak).
		for (int i = 0; i < _cogLife.Count; i++)
		{
			if (_cogLife[i] <= 0.0)
				continue; // idle slot: hidden and parked already
			_cogLife[i] -= delta;
			Godot.Node2D gn = _cogN[i];
			if (_cogLife[i] <= 0.0)
			{
				_cogLife[i] = 0.0;
				_cogS[i].Visible = false;
				_cogR[i].Visible = false;
				gn.GlobalPosition = new Vector2(-9000.0f, -9000.0f); // free-slot sentinel like every pool
				continue;
			}
			gn.Set("rotation", (float)(gn.GetRotation() + _cogSpin[i] * delta)); // tumble on the slot node so art AND fallback read it
			gn.Position = new Vector2(gn.Position.X, gn.Position.Y + (float)(COG_DROP * delta / COG_LIFE)); // the approved fall: ~10 px over the life
			float a = _cogLife[i] > COG_LIFE * 0.5 ? 1.0f : (float)(_cogLife[i] / (COG_LIFE * 0.5)); // fade only in the back half: the drop stays crisp
			_cogS[i].Modulate = new Color(1.0f, 1.0f, 1.0f, a);
			if (cogFxTex == null)
				_cogR[i].Modulate = new Color(1.0f, 1.0f, 1.0f, a); // the square follows the same fade beat
		}
	}
	private void UpdateMarthaFx(float delta)
	{
		// Martha's stamp clocks: the bite chomp decays on its own ~0.18 s, and the broken iron looses
		// an electric crackle every MARTHA_SPARK_EVERY across the WHOLE 20 s window (the approved
		// "looseled ~0.25 s across the window" read). Stamps ride HER BODY as children, so the fold-follow
		// is free and the pool is lazy: BuildMartha can art-less return early, so the first upkeep tick
		// past the seat builds the crackle slots.
		if (_marthaBody == null)
			return; // off-field: nothing to tick, same read as every lane above
		if (_sparkN.Count == 0)
		{
			for (int i = 0; i < MARTHA_SPARK_POOL; i++)
			{
				Godot.Node2D sn = new Godot.Node2D();
				Godot.Sprite2D ss = new Godot.Sprite2D();
				ss.Texture = artOrNull("res://art/fx/static_martha.png"); // rail art lands this lap; absent = invisible, the streak paints instead
				ss.Set("texture_filter", 0); // NEAREST like every other pool sprite
				ss.Set("z_index", 5); // crackle over her body (2) and her chomp (4)
				sn.AddChild(ss);
				Godot.ColorRect sr = new Godot.ColorRect();
				sr.Size = new Vector2(9.0f, 4.0f); // short electric streak: the bolt lane's shape, Martha-sized
				sr.Position = new Vector2(-4.5f, -2.0f); // centred under the node (Control origin = corner)
				sr.Color = new Color(0.45f, 0.7f, 1.0f, 1.0f); // electric blue: same read as the chain bolts
				sn.AddChild(sr);
				sn.Visible = false;
				(_marthaRig != null ? _marthaRig : _marthaBody).AddChild(sn); // rides her fold-follow AND her gait
				_sparkN.Add(sn);
				_sparkS.Add(ss);
				_sparkR.Add(sr);
				_sparkLife.Add(0.0);
			}
			_marthaSparkDelay = 0.0;
		}
		// Gait (user 2026-11-23: "the same movement animation as the enemies"): the Enemy.cs sway law -
		// an odometer of what she ACTUALLY moved, an eased px/s, lateral sway + half-rate dip + a lean
		// that trails the sway, cadence scaled by that speed, set-down when she stops. Her 130 px/s
		// scuttle IS the enemy SWAY_REF, so a full gallop swings at the creature cadence (12/s), not
		// the party 7.2. Visuals only: the BODY node is the hit core the horde reads.
		{
			Vector2 gp = _marthaBody.GlobalPosition;
			float moved = _marthaMovePrimed ? WrapDelta(_marthaLastMove, gp).Length() : 0.0f;
			_marthaLastMove = gp;
			_marthaMovePrimed = true;
			if (moved > 400.0f)
				moved = 0.0f; // wrap-fold frame, not motion (the party guard)
			float inst = delta > 0.001f ? moved / delta : (float)_marthaMoveSpd;
			_marthaMoveSpd += (inst - _marthaMoveSpd) * (1.0 - (float)System.Math.Exp(-8.0 * delta));
			if (_marthaRig != null)
			{
				if (_marthaMoveSpd > 30.0) // striding (enemy SWAY_MIN)
				{
					double ratio = _marthaMoveSpd / 130.0; // enemy SWAY_REF
					ratio = ratio < 0.35 ? 0.35 : (ratio > 1.4 ? 1.4 : ratio);
					_marthaBobT += delta * 12.0 * ratio; // enemy SWAY_RATE: the creature cadence, not the party 7.2
					float ph = (float)_marthaBobT; // solo: no per-body phase, she is one of one
					float sw = (float)System.Math.Sin(ph) * 3.4f; // enemy SWAY_AMP
					_marthaRig.Position = new Vector2(sw, -(float)System.Math.Abs((float)System.Math.Sin(ph * 0.5)) * 1.2f); // side to side + half-rate dip
					_marthaRig.Rotation = sw * 0.017f; // the lean trails the sway (party constant)
				}
				else
				{
					float ease = 1.0f - (float)System.Math.Exp(-9.0 * delta); // set-down, not a jolt (party law)
					_marthaRig.Position += -_marthaRig.Position * ease;
					_marthaRig.Rotation += -_marthaRig.Rotation * ease;
				}
			}
		}
		if (_marthaFxT > 0.0)
		{
			_marthaFxT -= delta;
			if (_marthaFxT <= 0.0)
			{
				_marthaFxT = 0.0;
				if (_biteFx != null)
					_biteFx.Visible = false; // the chomp drops off-beat, ~0.18 s after the commit
			}
		}
		_marthaSparkDelay -= delta;
		if (_marthaT > 0.0 && _marthaSparkDelay <= 0.0)
		{
			_marthaSparkDelay = MARTHA_SPARK_EVERY; // the crackle cadence, kept out of the BROKEN lane itself
			for (int i = 0; i < _sparkLife.Count; i++)
			{
				if (_sparkLife[i] > 0.0)
					continue; // live sparks keep their own frames
				_sparkLife[i] = MARTHA_SPARK_LIFE;
				Godot.Node2D sn = _sparkN[i];
				float jr = Rng != null ? (float)Rng.Randf() : 0.5f; // loose jitter around her chassis: assistant pick (retune by eye)
				sn.Position = new Vector2((float)(jr * 44.0 - 22.0), (float)(((i * 7 + (int)(jr * 9)) % 11) * 4.0f - 55.0f)); // local seat (user 2026-11-23: "higher up to be on her body"): the crackle walks her torso + head, never under her base
				sn.Set("rotation", (float)(jr * 3.14159 * 2.0 - 3.14159)); // streak aimed loose, like a bolt that found no second body
				sn.Visible = true;
				_sparkS[i].Visible = _sparkS[i].Texture != null; // static art paints only once the rail has loaded
				_sparkR[i].Visible = true;
				break; // one spark per crackle beat
			}
		}
		for (int i = 0; i < _sparkLife.Count; i++)
		{
			if (_sparkLife[i] <= 0.0)
				continue;
			_sparkLife[i] -= delta;
			float a = _sparkLife[i] > MARTHA_SPARK_LIFE * 0.6 ? 1.0f : (float)(_sparkLife[i] / (MARTHA_SPARK_LIFE * 0.6)); // sparks SNAP: most of the read is full-bright, the fade is a blink
			_sparkS[i].Modulate = new Color(1.0f, 1.0f, 1.0f, a);
			_sparkR[i].Modulate = new Color(1.0f, 1.0f, 1.0f, a);
			if (_sparkLife[i] <= 0.0)
			{
				_sparkLife[i] = 0.0;
				_sparkN[i].Visible = false;
				_sparkS[i].Visible = false;
				_sparkR[i].Visible = false;
			}
		}
	}
// Yu seat upkeep (user 2026-11-02 rework): kindness spirits now RIDE like the gem pickups -
// the SAME 110 px collection bubble + 380 px/s stream that UpdateGems uses pull a vigil to
// its closest standing member (magnet elixir + Inquisitive Mind widen the bubble like every
// other pickup), and a DELIVERED vigil heals ALL allies who have ACTUALLY LOST hp, each
// +SPIRIT_HEAL clamped to their own EffMaxHp ceiling (user: "spirits heal ALL allies + ride
// orb-style pickup"). A healthy squad never drinks: the vigil waits on its clock instead of
// being wasted. No random patient any more, so the heal-all is DETERMINED: the run stream
// no longer burns one draw per heal (kindle rings still draw 2 each, documented at SpawnSpirit).
// Downed / eliminated bodies are excluded by design: down is a death-save situation, not an
// open hp wound. _party is a Godot.Collections.Array and never shrinks (join/eliminate flip
// flags), so the fixed candidate array + foreach is safe.
private void UpdateSpirits(float delta)
{
	for (int i = 0; i < _spirits.Count; i++)
	{
		Godot.Node2D s = _spirits[i];
		if (s.GlobalPosition.X < -8000.0f)
			continue; // free slot: same idiom as the summon pool
		// ride the fold like the gems do, so a vigil parked past the fold loops back
		s.GlobalPosition = WrapNearCamera(s.GlobalPosition);
		// gem-style magnet: closest standing member whose bubble holds this vigil owns the stream
		PartyMember puller = null;
		Vector2 to = Vector2.Zero;
		float dist = 1e30f;
		foreach (PartyMember mm in _party)
		{
			if (!mm.joined || mm.downed || mm.eliminated)
				continue; // stranded / fallen: no bubble
			Vector2 mt = mm.GlobalPosition - s.GlobalPosition;
			float md = mt.Length();
			if (md < 110.0f * (float)mm.stat[StatTags.COLLECTION_RANGE] * (mm.elixMagT > 0.0 ? 1.10f : 1.0f) * (float)LeaderCollectMul() && md < dist)
			{
				puller = mm;
				dist = md;
				to = mt;
			}
		}
		if (puller != null)
			s.GlobalPosition += to.Normalized() * 380.0f * delta; // the same orb ride the gems use
			// user 2026-11-03: outside anyone's collection bubble the vigil now SLOWLY HOPS toward
			// the leader seat (user: "his leader passive friendly familiars ... slowly hop towards him").
			// A frog-hop, not a glide: it pushes for SPIRIT_HOP_MOVE s then rests the rest of the cycle,
			// and the phase rides the EXISTING life clock (zero new state), same discipline as the bob.
			// The 380 px/s magnet ride above still owns the fast final approach, so the vigil reads as
			// kindled -> crawling to Yu -> riding in once he is close enough.
			if (puller == null && _leader != null && !(_leader.downed || _leader.eliminated))
			{
				float hopT = (float)(SPIRIT_LASTS - _spiritLife[i]);
				float cyc = hopT - (float)(int)(hopT / SPIRIT_HOP_EVERY) * SPIRIT_HOP_EVERY;
				if (cyc < SPIRIT_HOP_MOVE)
				{
					Vector2 hv = WrapDelta(s.GlobalPosition, _leader.GlobalPosition);
					if (hv.Length() > 2.0f)
						s.GlobalPosition = WrapNearCamera(s.GlobalPosition + hv.Normalized() * SPIRIT_HOP_SPEED * delta);
				}
			}
		if (puller != null && dist < 14.0f)
		{
			PartyMember[] cands = new PartyMember[13]; // roster-wide bound (13 PCs once seat 12 Hew joins; was exact-fit at 8)
			int cn = 0;
			foreach (PartyMember mm in _party)
			{
				if (!mm.joined || mm.downed || mm.eliminated)
					continue;
				if (mm.hp < mm.EffMaxHp())
					cands[cn++] = mm; // only real hp losses are patients
			}
			if (cn > 0)
			{
				// user 2026-11-02: the vigil heals ALL wounded allies in one DETERMINED pass
				// (no random patient, so no Rng draw is burned per heal)
				for (int q = 0; q < cn; q++)
				{
					cands[q].hp += (float)SPIRIT_HEAL;
					if (cands[q].hp > cands[q].EffMaxHp())
						cands[q].hp = (float)cands[q].EffMaxHp(); // clamp at each own ceiling
				}
				_lbSpiritTaken++;
				GD.Print("[10thAge] (bonus) kindness spirit heals ALL wounded allies: " + cn + " for 5 hp");
				DespawnSpirit(i);
				continue;
			}
			// healthy squad: the vigil is NOT consumed, it keeps waiting on its clock
		}
		// user 2026-11-02: kindness spirits bob IN PLACE (user: "the friendly one can just bounce
		// up and down in place") - visual-only art-child bob, same discipline as the familiar hop;
		// magnet pull, heal delivery and the life clock are untouched
		Godot.Sprite2D start = (Godot.Sprite2D)((Godot.ColorRect)s.GetChild(0)).GetChild(0);
		float sph = (float)_spiritLife[i] * 7.0f + i * 2.3f; // gentler cycle, slot-staggered (assistant pick)
		start.Position = new Vector2(6.0f, 6.0f - 5.0f * (float)System.Math.Abs(System.Math.Sin((double)sph))); // 5 px bob (assistant pick)
		_spiritLife[i] -= delta;
		if (_spiritLife[i] <= 0.0)
		{
			DespawnSpirit(i); // nobody hurt in time: it dims (pick)
		}
	}
}

	private void UpdateGems(float delta)
	{
		// XP magnet (rare kill drop): the clock drains once per frame here, and the bubble test
		// below widens to whole-field while it runs. The vacuum itself is UNCHANGED: gems still
		// ride the 380 px/s stream and pay their own XP_PCT math at pickup.
		// ROUND 8 mirror clocks live here for her bubble: MirrorPartyBuffs recomputes them at the top of
		// UpdateMartha, which runs AFTER this gem lane, so her Magnetism half lags ONE frame - noted,
		// harmless.
		// ROUND 8: Martha's OWN gem lane (user: "she collects xp... treat it like she is another party
		// member"). She is not in _party, so the puller census below cannot see her: her bubble is the
		// flat class band 110 px x the leader's collection bubble (x 1.10 while a magnetised party member
		// runs Magnetism - she pays the leader's own elixir math, not a member's), gems ride her at the
		// SAME 380 px/s, and she takes contact at the same 14 px the puller lane uses. She has NO XP_PCT
		// tag, so the value lane pays LeaderXpMul only. The magnet clock widens her lane too.
		// UpdateGems runs BEFORE UpdateMartha this frame, so the mirror lanes above lag ONE frame: noted,
		// harmless.
		if (_marthaBody != null && _marthaHp > 0.0 && _marthaT <= 0.0)
		{
			for (int i = 0; i < _gems.Count; i++)
			{
				Godot.Node2D gem = _gems[i];
				if (gem.GlobalPosition.X < -8000.0f)
					continue;
				Vector2 mt = _marthaBody.GlobalPosition - gem.GlobalPosition;
				float md = mt.Length();
				if (md < 110.0f * (float)LeaderCollectMul() * (_muElixMagT > 0.0 ? 1.10f : 1.0f) || _magnetT > 0.0)
				{
					gem.GlobalPosition += mt.Normalized() * 380.0f * delta;
					if (md < 14.0f)
						CollectGem(i, _marthaBody.GlobalPosition, null); // null member: NO XP_PCT math, and CollectGem's base law already pays LeaderXpMul - exactly the value the puller lane would grant a member without the tag
				}
			}
		}
		if (_magnetT > 0.0)
			_magnetT -= delta;
		for (int i = 0; i < _gems.Count; i++)
		{
			Godot.Node2D gem = _gems[i];
			if (gem.GlobalPosition.X < -8000.0f)
				continue;
			// gems sit where bodies died: fold them like everything else, so a player
			// who wanders off loops back to them instead of losing them off-world
			gem.GlobalPosition = WrapNearCamera(gem.GlobalPosition);
			// nearest living member whose bubble holds this gem: on overlap the
			// CLOSEST PC owns the magnet so a gem rides one stream, and pickup
			// pays that member's own XP_PCT tag (party max 5, gems few: cheap)
			PartyMember puller = null;
			Vector2 to = Vector2.Zero;
			float dist = 1e30f;
			foreach (PartyMember mm in _party)
			{
				if (!mm.joined || mm.downed || mm.eliminated)
					continue; // downed/eliminated bodies + stranded recruits: no bubble
				Vector2 mt = mm.GlobalPosition - gem.GlobalPosition;
				float md = mt.Length();
				if ((md < 110.0f * (float)mm.stat[StatTags.COLLECTION_RANGE] * (mm.elixMagT > 0.0 ? 1.10f : 1.0f) * (float)LeaderCollectMul() || _magnetT > 0.0) && md < dist) // Magnetism elixir: bubble +10% | Inquisitive Mind: bubble +15% while Brook leads | XP magnet (rare kill drop): whole-field while the clock runs
				{
					puller = mm;
					dist = md;
					to = mt;
				}
			}
			if (puller != null)
			{
				gem.GlobalPosition += to.Normalized() * 380.0f * delta;
				if (dist < 14.0f)
				{
					AddXp(_gemVals[i] * (1.0 + puller.stat[StatTags.XP_PCT]) * LeaderXpMul()); // XP_PCT tag: gems worth more at pickup | Inquisitive Mind: +15% while Brook leads
					DespawnGem(i);
					continue;
				}
			}
		}
	}

	// ROUND 8: the gem hand-in Martha's own lane above shares with the puller lane below: null
	// puller means NO XP_PCT tag math (she has none), LeaderXpMul still pays - exactly the value
	// the puller lane grants a member without the tag. pos is the hand-in seat, kept for FX readers.
	private void CollectGem(int i, Vector2 pos, PartyMember puller)
	{
		if (puller != null)
			AddXp(_gemVals[i] * (1.0 + puller.stat[StatTags.XP_PCT]) * LeaderXpMul());
		else
			AddXp(_gemVals[i] * LeaderXpMul());
		DespawnGem(i);
	}

	private void DespawnGem(int i)
	{
		Godot.Node2D gem = _gems[i];
		gem.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
		((Godot.ColorRect)gem.GetChild(0)).Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
	}

	// ---------- XP-gem FX tiers + merge rail (user 2026-11-17) ----------
	// Repaints an orb from the XP it carries. Only the base ColorRect hue moves: the spawn/despawn
	// alpha stays on Modulate exactly as the shipped rail owns it, so the fade reads unchanged.
	// The 1-4 / 5-9 / 10-49 / 50+ split is the USER law (2026-11-17: "50+ we can set as red.
	// This is our mega gem if it becomes too much"); the hues below are assistant picks.
	private void PaintGem(int i)
	{
		// what it carries
		double v = _gemVals[i];
		Godot.Node2D gem = _gems[i];
		Godot.ColorRect cr = (Godot.ColorRect)gem.GetChild(0);
		// the tier hue for what it carries (the 1-4 / 5-9 / 10-49 / 50+ split is the USER law:
		// "50+ we can set as red. This is our mega gem if it becomes too much"; hues are picks)
		Color hue;
		if (v >= GEM_TIER_RED)
			hue = new Color(1.0f, 0.18f, 0.18f, 1.0f); // 50+ XP: the RED MEGA orb (USER tier)
		else if (v >= GEM_TIER_GREEN)
			hue = new Color(0.3f, 1.0f, 0.45f, 1.0f); // 10+ XP: green (assistant pick: hue)
		else if (v >= GEM_TIER_BLUE)
			hue = new Color(0.3f, 0.62f, 1.0f, 1.0f); // 5-9 XP: blue (assistant pick: hue)
		else
			hue = new Color(1.0f, 0.85f, 0.2f, 1.0f); // 1-4 XP: the shipped yellow, untouched
		// ART lane (2026-11-17, the d20 XP chip the user picked): the chip rides UNDER the shell,
		// so the shell stops drawing (its Color alpha goes 0) and the hue multiplies through the
		// sprite's own Modulate. The canvases are TONE-ONLY, so a fold or a cluster merge that
		// fattens an orb RE-READS its tier - a baked-in art colour could not. The spawn/despawn
		// fade still lives on the SHELL's Modulate and cascades to this child, so SpawnGem,
		// DespawnGem and UpdateGems stay exactly as shipped.
		// 50+ swaps to the bigger star-bearing canvas: the user's "bigger + inner star", authored
		// as its own 20x20 file, so no runtime scale math. The sprite is centred (Sprite2D
		// centered=true) on the shell, so the 14 px and the 20 px canvases share one position.
		if (cr.GetChildCount() > 0)
		{
			Godot.Sprite2D sp = (Godot.Sprite2D)cr.GetChild(0);
			if (_gemMegaArtTex != null && v >= GEM_TIER_RED)
				sp.Texture = _gemMegaArtTex; // the MEGA chip: bigger + the inner star
			else
				sp.Texture = _gemArtTex; // the normal chip (the child exists only once art loaded)
			sp.Modulate = hue; // the tier hue rides the art
			cr.Color = new Color(hue.R, hue.G, hue.B, 0.0f); // the shell stops drawing over it
		}
		else
			cr.Color = hue; // shipped greybox square fallback: the hue IS the face
	}

	// Vampire-Survivors-style cluster fold, once the pool nears capacity: orbs sitting within
	// GEM_MERGE_R of each other collapse onto the lower-indexed carrier, which takes the SUM.
	// Nothing moves, no node is created, no live orb is evicted - a carrier is picked up like any
	// other orb and pays the whole sum, so no kill's XP is ever lost and the pool never grows.
	// Gated at GEM_MERGE_TRIGGER_PCT so an ordinary run (hundreds of free slots) returns at once
	// and keeps the shipped drop-per-kill look. Cost: O(n^2) over the FIXED 240 pool = 28,680
	// pairs worst case, and only while saturated; the kill scan already clears 20,480 pairs.
	private void MergeGemClusters()
	{
		int live = 0;
		for (int i = 0; i < _gems.Count; i++)
		{
			if (_gems[i].GlobalPosition.X > -8000.0f)
				live++;
		}
		// saturation rail, integer math so no float compare drifts: fold only when nearly full
		if (live * 100 < GEM_MERGE_TRIGGER_PCT * _gems.Count)
			return;
		int merged = 0;
		for (int i = 0; i < _gems.Count; i++)
		{
			Godot.Node2D gi = _gems[i];
			if (gi.GlobalPosition.X <= -8000.0f)
				continue;
			// the carrier may already own a sum folded at a lower i: keep the sum, one carrier per
			// cluster. Its post never moves here, so every distance rides the same point.
			for (int j = i + 1; j < _gems.Count; j++)
			{
				Godot.Node2D gj = _gems[j];
				if (gj.GlobalPosition.X <= -8000.0f)
					continue;
				if (gi.GlobalPosition.DistanceTo(gj.GlobalPosition) > (float)GEM_MERGE_R)
					continue;
				_gemVals[i] += _gemVals[j]; // the older orb keeps the sum, the newer folds in
				DespawnGem(j); // parked free like any collected orb, so the rail stays honest
				merged++;
			}
			if (merged > 0)
				PaintGem(i); // and shows it: a merged carrier re-reads its tier
		}
	}

	// OGRE2 HURL VICTIM SCAN (user 2026-11-11: "when ogre2 gets close to an enemy from
	// tier 1, he will pick them up and throw them at the player"): nearest LIVE tier-1
	// body within radius. TIER-1 KINDS ONLY = goblin / goblin2 (fastgoblin + hobgoblin are
	// tier-2 lanes in the Director, so the user's "tier 1" law reads them OUT: assistant
	// read - retunable). Downed / self-despawned bodies are off the menu; bosses ride
	// _enemies too but are never Enemy so they skip. The reach knob lives on the caller.
	public Enemy NearestTier1Victim(Vector2 from, float radius)
	{
		Enemy best = null;
		float bestD = radius;
		for (int i = 0; i < _enemies.Count; i++)
		{
			if (!(_enemies[i] is Enemy)) // bosses ride _enemies too: never menu items
			{
				continue;
			}
			Enemy e = (Enemy)_enemies[i];
			// kind "dead" bodies linger in _enemies until the kill scan drops them; already-downed
			// bodies are off the menu too
			if ((e.kind != "goblin" && e.kind != "goblin2") || e.hp <= 0.0f)
			{
				continue;
			}
			// wrapping world: the fold-relative delta owns the distance read
			float dist = WrapDelta(from, e.GlobalPosition).Length() - e.hitR; // HITBOX = ART: the menu reads the drawn body, so the yank can grab a goblin whose art is in reach
			if (dist < bestD)
			{
				bestD = dist;
				best = e;
			}
		}
		return best;
	}
	private void UpdateBullets(float delta)
	{
		for (int i = 0; i < _bullets.Count; i++)
		{
			Godot.Node2D b = _bullets[i];
			if (b.GlobalPosition.X < -8000.0f)
				continue;
			b.GlobalPosition += _bulletVel[i] * delta;
			// HURL tumble (user 2026-11-12 hand-play): only thrown-goblin slots hold a non-zero
			// spin, so arrows / bolts / fireballs never twitch; the tumble rides the NODE so the
			// art child AND the fallback square turn. Motion + hit lanes read POSITION: visual only.
			if (_bulletSpin[i] != 0.0)
				b.Rotation += (float)_bulletSpin[i] * delta;
			Vector2 p = b.GlobalPosition;
			// no arena to die against anymore: a volley bolt expires once it is a touch
			// past the measured frame (just inside the fold box, so it never gets a
			// chance to wrap and loop back around the player)
			Vector2 bc = GetCameraCenter();
			if (System.Math.Abs(p.X - bc.X) > _visHalf.X + 64.0 || System.Math.Abs(p.Y - bc.Y) > _visHalf.Y + 64.0)
			{
				DespawnBullet(i);
				continue;
			}
			Godot.Node2D uw = UndeadAt(p); // Hapi shot intercept: the horde chews risen bodies too
			if (uw != null)
			{
				UndeadHitBy(uw, _bulletDmg[i]); // real hp lane: a volley bolt can fell a risen body
				DespawnBullet(i);
				continue; // bolt spent on a risen body: members behind it stay untouched
				}
			PartyMember m = NearestMemberAny(p); // volleys hit downed bodies too
			if (m != null && p.DistanceTo(m.GlobalPosition) < 12.0f + MEMBER_RADIUS)
			{
				if (_pilot)
					continue; // verification-only: the pilot simulates a human dodging volleys
				TakeHit(m, _bulletDmg[i]);
				DespawnBullet(i);
			}
		}
	}

	// HEAL RETARGET (user 2026-11-09 QoL): "fix health pickups to heal the lowest HP party
	// member regardless of who picks it up". The heal orb ASKS this instead of healing its
	// picker; same live census as NearestMember (joined, not downed, not eliminated). Ties
	// keep the lower seat: stable, no Rng burn, run streams never diverge.
	public PartyMember LowestHpMember()
	{
		PartyMember w = null;
		foreach (PartyMember pm in _party)
		{
			if (!pm.joined || pm.downed || pm.eliminated)
				continue; // fallen / stranded bodies are out, same census as the gem rail
			if (w == null || pm.hp < w.hp)
				w = pm;
		}
		return w;
	}

	// MELEE MISSILE SMASH (user 2026-11-09 QoL): "Baldrun, Brook and Mosiah ... their attacks
	// to be able to destroy enemy missiles. Later in the run there's a ton of missiles ...
	// quite difficult if you happen to have mostly melee characters". NO side tag needed:
	// EVERY live slot in _bullets is an ENEMY-owned projectile (UpdateBullets only ever bites
	// the party, and party ranged weapons run in the mirror _shots pool below), so these scans
	// can never eat a friendly bolt. Geometry mirrors each melee verb's OWN bite (cleaver/rapier
	// fan, dagger thrust corridor), torus-safe via WrapDelta, dead slots sit at X < -8000.
	public int SmashBulletsArc(Vector2 from, float facing, float arcHalf, float reach)
	{
		int smashed = 0;
		for (int i = 0; i < _bullets.Count; i++)
		{
			Godot.Node2D b = _bullets[i];
			if (b.GlobalPosition.X < -8000.0f)
				continue; // pooled slot already expired
			if (_bulletBoss[i])
				continue; // BOSS SHOT LAW: a boss volley is never shatterable
			Vector2 v = WrapDelta(from, b.GlobalPosition);
			if (v.Length() > reach)
				continue;
			float a = (float)System.Math.Atan2(v.Y, v.X);
			float d = a - facing;
			d = (d + (float)System.Math.PI * 3.0f) % ((float)System.Math.PI * 2.0f) - (float)System.Math.PI;
			if (System.Math.Abs((double)d) > (double)arcHalf)
				continue; // clear of the swing fan
			DespawnBullet(i); // the blade clips the missile: it shatters
			smashed++;
		}
		return smashed;
	}

	// DAGGER shape: the same thrust corridor the jab loop uses (segment from -> from + dir *
	// reach, band px either side).
	public int SmashBulletsCorridor(Vector2 from, Vector2 dir, float reach, float band)
	{
		int smashed = 0;
		for (int i = 0; i < _bullets.Count; i++)
		{
			Godot.Node2D b = _bullets[i];
			if (b.GlobalPosition.X < -8000.0f)
				continue; // pooled slot already expired
			if (_bulletBoss[i])
				continue; // BOSS SHOT LAW: a boss volley is never shatterable
			Vector2 v = WrapDelta(from, b.GlobalPosition);
			double along = v.Dot(dir);
			if (along < 0.0 || along > (double)reach)
				continue; // outside the jab segment
			if ((v - dir * (float)along).Length() > band)
				continue; // clear of the blade lane
			DespawnBullet(i); // the dagger clips the missile: it shatters
			smashed++;
		}
		return smashed;
	}

	// ---------- party-side shot pool (M2: Asper's longbow) ----------
	// UpdateBullets is BOSS-owned: its bolts run into the party. A PC weapon that fires
	// needs the mirror pool that runs into ENEMIES, so the two never share slots, owners
	// or collision. Same discipline throughout: pooled nodes, no per-body allocation, and
	// a bolt expires past the MEASURED frame (the world loops now, so "offscreen" is not a
	// thing a bolt can die against) plus a hard lifetime, so a miss can never orbit the
	// torus back into its own shooter.
	private void UpdateShots(float delta)
	{
		for (int i = 0; i < _shots.Count; i++)
		{
			Godot.Node2D s = _shots[i];
			if (s.GlobalPosition.X < -8000.0f)
				continue;
			_shotLife[i] -= delta;
			s.GlobalPosition += _shotVel[i] * delta;
			Vector2 p = s.GlobalPosition;
			Vector2 bc = GetCameraCenter();
			if (_shotLife[i] <= 0.0 ||
				System.Math.Abs(p.X - bc.X) > _visHalf.X + 64.0 || System.Math.Abs(p.Y - bc.Y) > _visHalf.Y + 64.0)
			{
				DespawnShot(i);
				continue;
			}
			for (int e = 0; e < _enemies.Count; e++)
			{
				Godot.Node2D n = _enemies[e];
				Combatant nc = (Combatant)n;
				if (nc == null || nc.kind == "dead")
					continue; // a bolt only dies against a living body
				// bosses DO take bolts: the beat loop cannot advance while a live boss owns
				// the arena, so the party's shooter has to be able to bite it - a bow that
				// shrugged past a boss would be dead weight in every fight. The hit test
				// rides the authored silhouette, not a fixed 20 px core.
				float hitR = nc.hitR; // HITBOX = ART: Enemy.cs / Boss.cs stamp the drawn half-extent, so a bolt visibly brushing an ogre's arm BITES (an un-arted body keeps the old 20 px core, and a boss keeps its authored radius)
				if (p.DistanceTo(n.GlobalPosition) < hitR)
				{
					// one bolt, one body: the FAN is the multishot, and pierce belongs to
					// Mosiah's rapier - UNTIL Mosiah owns the leader seat, when every bolt
					// punches through one body and keeps flying (user 2026-10-25). _shotPen is
					// that per-bolt budget stamped at spawn; _shotLast memoizes the body just
					// raked so a pierced bolt cannot re-hit the same body the next frame. Damage
					// still goes through the shared kill scan, so the gem drop / live-count
					// release / escort meter stay enemy-owned.
					if (_shotLast[i] == (long)n.GetInstanceId())
						continue; // this bolt already raked THIS body: it rides on down the lane
					nc.TakeDamage(_shotDmg[i]);
					TryChainLightning(n); // Baldrun seat: the hit may loose a chain (no-op for other seats)
					// firebolt window opens on this body (0 dps = plain bolt: nothing burns)
					if (_shotBurnD[i] > 0.0)
						nc.Burn(_shotBurnD[i], _shotBurnT[i], _shotCinderD[i]); // kills still land on the shared scan
					if (_shotPen[i] > 0.0)
					{
						_shotPen[i] -= 1.0;
						_shotLast[i] = (long)n.GetInstanceId();
						// no DespawnShot: the pierced bolt keeps flying; the break below just bounds
						// the hit to one body per frame, which is all a 5.7 px step needs
					}
					else
						DespawnShot(i);
					break;
				}
			}
		}
	}

	private void DespawnShot(int i)
	{
		Godot.Node2D s = _shots[i];
		s.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
		((Godot.ColorRect)s.GetChild(0)).Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
	}

	// cinder bloom (user 2026-10-30 card swap: kindling sparks OUT, burning corpses burst IN):
	// a horde body that dies WHILE still burning bursts in CINDER_R px, scorching every live horde
	// body caught in the blast for the cinder damage STAMPED on the corpse at bolt spawn - the shot
	// call site computes it as 25/50/75% of the firebolt's live roll at rank 1/2/3, so damage cards
	// scale the burst WITH the DoT. The kill scan calls this before freeing the corpse; burst kills
	// land on the same scan (the reverse pass still visits lower slots this frame, higher slots the
	// next) and each corpse bursts at most once since it is freed on award - so a burning chain of
	// corpses is bounded by the horde itself and needs no budget. Bosses hold their own lane.
	private void CinderBurst(Combatant corpse)
	{
		if (corpse.burnT <= 0.0 || corpse.burnCinder <= 0.0 || corpse is Boss)
			return; // only a burning horde body carrying a cinder stamp bursts
		double dmg = corpse.burnCinder;
		Vector2 at = corpse.GlobalPosition;
		int hit = 0;
		for (int i = 0; i < _enemies.Count; i++)
		{
			Combatant nc = (Combatant)_enemies[i];
			if (nc == null || nc.kind == "dead" || nc.hp <= 0.0 || nc is Boss)
				continue; // already-dying bodies leave on their own; bosses hold their own lane
			if (at.DistanceTo(nc.GlobalPosition) > CINDER_R + nc.hitR) // HITBOX = ART: the blast sweeps to the DRAWN edge (CINDER_R is small enough that the squared shortcut no longer pays)
				continue; // small radius: the blast sweeps what stood beside the pyre
			nc.TakeDamage(dmg);
			hit++;
		}
		// fx-feel8 (user 2026-11-02: "need a small animation for burning bodies burst, something
		// like what you did for Yu's minions explosions"): the corpse POPS whether or not anyone
		// stood in the blast - visual only, the damage above is the whole story.
		for (int i = 0; i < _bursts.Count; i++)
		{
			Godot.Node2D b = _bursts[i];
			if (b.GlobalPosition.X >= -8000.0f)
				continue; // occupied slot (same sentinel as the shot pool)
			b.GlobalPosition = at;
			_burstLife[i] = 0.16; // one Yu-detonation beat: grow to the blast, fade to nothing
			Godot.ColorRect bc = (Godot.ColorRect)b.GetChild(0);
			bc.Size = new Vector2(10.0f, 10.0f);
			bc.Position = new Vector2(-5.0f, -5.0f);
			bc.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.9f); // first frame already readable
			break;
		}
		if (hit > 0)
			GD.Print("[10thAge] (cinder) burst hit=", hit, " dmg=", (int)dmg);
	}
	// cinder blooms: drive the pops CinderBurst loosed - SAME pattern as Yu's DetonateSummon (the
	// shell reaches the blast it made, then fades to nothing). No physics, no damage, no budget.
	private void UpdateCinderFx(float delta)
	{
		for (int i = 0; i < _bursts.Count; i++)
		{
			if (_burstLife[i] <= 0.0)
				continue;
			_burstLife[i] -= delta;
			Godot.Node2D b = _bursts[i];
			Godot.ColorRect bc = (Godot.ColorRect)b.GetChild(0);
			if (_burstLife[i] <= 0.0)
			{
				b.GlobalPosition = new Vector2(-9000.0f, -9000.0f); // back to the free-slot sentinel
				bc.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f); // nothing left to draw
				continue;
			}
			float t = 1.0f - (float)(_burstLife[i] / 0.16); // 0 at the pop -> 1 as the window closes
			float g = 10.0f + (float)(CINDER_R * 2.0) * t; // grows to the blast it actually made
			bc.Size = new Vector2(g, g);
			bc.Position = new Vector2(-g * 0.5f, -g * 0.5f); // keep it centred under the node
			bc.Modulate = new Color(1.0f, 0.85f, 0.6f, 1.0f - t); // ember-warm, fades to nothing
		}
	}
	// fx-feel9: chain bolts - drive the streaks LooseChainBolt loosed: reveal clock first (hops
	// light up CHAIN_BOLT_GAP apart = the fast-chain read the user asked for), then the fade.
	// Pure art: no physics, no damage, no budget. Positions are the instant the chain ran - a
	// bolt is past tense by the time bodies move on, so no tracking / wrap handling is needed
	// (hops are <= CHAIN_RANGE, too short to read a seam).
	private void UpdateChainFx(float delta)
	{
		for (int i = 0; i < _boltLife.Count; i++)
		{
			if (_boltLife[i] <= 0.0)
				continue; // free slot (parked at the sentinel, alpha 0)
			if (_boltDelay[i] > 0.0)
			{
				_boltDelay[i] -= delta; // reveal beat: the streak stays invisible until its turn
				continue;
			}
			_boltLife[i] -= delta;
			if (_boltLife[i] <= 0.0)
			{
				_boltN[i].GlobalPosition = new Vector2(-9000.0f, -9000.0f); // back to the free-slot sentinel
				_boltC[i].Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f); // nothing left to draw
				continue;
			}
			float k = (float)(_boltLife[i] / CHAIN_BOLT_LIFE); // 1 at the reveal -> 0 as the window closes
			_boltC[i].Modulate = new Color(1.0f, 1.0f, 1.0f, k); // white x the authored electric blue
		}
	}
	// fx-feel9 (user: "a simple blue streak of lightning that chains quickly between enemies"):
	// one bolt segment per chain hop, from the body the bolt left off to the one it lands on,
	// revealed `gap` seconds later (call site: b * CHAIN_BOLT_GAP) so the shape reads as a fast
	// zigzag instead of one flash. The damage already landed, so a full pool skips the streak.
	private void LooseChainBolt(Vector2 from, Vector2 to, double gap)
	{
		for (int i = 0; i < _boltLife.Count; i++)
		{
			if (_boltLife[i] > 0.0)
				continue;
			Godot.Node2D bn = _boltN[i];
			Godot.ColorRect bcr = _boltC[i];
			Vector2 dd = to - from;
			float len = dd.Length();
			bn.GlobalPosition = (from + to) * 0.5f;
			bn.Rotation = (float)System.Math.Atan2((double)dd.Y, (double)dd.X);
			bcr.Size = new Vector2(len + 8.0f, 5.0f); // +8 px: the nubs bite both bodies (assistant pick)
			bcr.Position = new Vector2(-(len + 8.0f) * 0.5f, -2.5f); // re-centre the stretched rect
			_boltLife[i] = CHAIN_BOLT_LIFE;
			_boltDelay[i] = gap; // hidden until its reveal beat
			bcr.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f); // invisible on the launch frame
			return;
		}
	}
	public void SpawnShot(Vector2 pos, Vector2 vel, double dmg, double life, double pen = 0.0, double burnDur = 0.0, double burnDps = 0.0, double cinder = 0.0, bool stingerArt = false) // 2026-11-09: stingerArt ONLY the leader-passive insect bolts pass; pen/burn/cinder stamped at spawn: Asper + Mosiah pierce, Stack firebolt burn + cinder bloom; everyone else keeps the 0 defaults
	{
		for (int i = 0; i < _shots.Count; i++)
		{
			Godot.Node2D s = _shots[i];
			if (s.GlobalPosition.X >= -8000.0f)
				continue;
			s.GlobalPosition = pos;
			_shotVel[i] = vel;
			_shotDmg[i] = dmg;
			_shotLife[i] = life;
			// pierce budget stamped at spawn: Mosiah leader seat + the shooter's own pen (Asper card)
			_shotPen[i] = LeaderPierceBonus() + pen;
			// firebolt burn / cinder-bloom damage stamped at spawn too (all 0 = inert for other lanes)
			_shotBurnT[i] = burnDur;
			_shotBurnD[i] = burnDps;
			_shotCinderD[i] = cinder;
			_shotLast[i] = -1;
			((Godot.ColorRect)s.GetChild(0)).Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f);
			// fx art: the burn stamped at spawn is Stack's own fire DoT (the only fire lane
			// riding this pool) -> flame sprite; every other party bolt rides Asper's arrow,
			// rotated to its own velocity. No art on disk = the amber shell keeps drawing
			Godot.ColorRect shell = (Godot.ColorRect)s.GetChild(0);
			if (!_fxTried)
			{
				_fxTried = true;
				_fxArrowTex = GD.Load("res://art/fx/arrow.png") as Godot.Texture2D;
				_fxFlameTex = GD.Load("res://art/fx/firebolt.png") as Godot.Texture2D;
				_fxStingerTex = GD.Load("res://art/fx/stinger.png") as Godot.Texture2D;
			}
			Godot.Texture2D tx = stingerArt ? _fxStingerTex : (burnDps > 0.0 ? _fxFlameTex : _fxArrowTex); // 2026-11-09 fix (user: "Asper should still be using the arrows, it's the friendly bees that spawn from his leader passive ... that should be shooting stingers"): whole-pool default rides the ARROW again; only insect bolts pass stingerArt
			if (tx != null)
			{
				Godot.Sprite2D fxs = (Godot.Sprite2D)shell.GetChild(0);
				fxs.Texture = tx;
				float tsx = stingerArt ? 0.45f : 1.0f; // arrow + flame ride 1:1; 2026-11-09b hand-play (user: "shrink the stinger on the passive bees and we're good"): 0.72 drew the 46x24 stinger at 33x17 px against a 15x13 px bee,
											  // i.e. the sting was longer than the bug carrying it. 0.45 draws it ~21x11 px - just past the bee. Assistant knob - retunable
				fxs.Scale = new Vector2(tsx, tsx);
				fxs.Rotation = (float)System.Math.Atan2((double)vel.Y, (double)vel.X);
				fxs.Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f);
				shell.Color = new Color(0.0f, 0.0f, 0.0f, 0.0f); // shell stops drawing while art rides
			}
			return;
		}
	}

	// ---------- Yu's familiars: run -> detonate, damage through the shared kill scan ----------
	public void SpawnSummon(Vector2 pos, double dmg, double fuse, double radius, float speed, double luck = 0.0)
	{
		for (int i = 0; i < _summons.Count; i++)
		{
			Godot.Node2D s = _summons[i];
			if (s.GlobalPosition.X >= -8000.0f)
				continue; // occupied slot
			s.GlobalPosition = pos;
			_summonVel[i] = new Vector2(0, 0);
			_summonDmg[i] = dmg;
			_summonLife[i] = fuse;
			_summonR[i] = radius;
			_summonSpd[i] = speed;
			_summonKaka[i] = false; // the ritual's own familiars never carry the Kaka's doubling
			_summonLuck[i] = luck; // baked per slot: DetonateSummon has no member handle to read the card
			_summonBurst[i] = false;
			// put the shell back: the slot may have been left mid-blast by DetonateSummon
			Godot.ColorRect cr = (Godot.ColorRect)s.GetChild(0);
			cr.Size = new Vector2(14.0f, 14.0f);
			cr.Position = new Vector2(-7.0f, -7.0f);
			Godot.Sprite2D fxs = (Godot.Sprite2D)cr.GetChild(0);
			fxs.Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f); // wake the sprite: a detonated slot left it sunk
			if (_famArt)
				cr.Color = new Color(0.26f, 0.2f, 0.34f, 0.0f); // art rides: shell stops drawing
			cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f);
			return;
		}
	}

	public void SpawnKakas(Vector2 center, double dmg, double fuse, double radius, float speed, double luck = 0.0)
	{
		// The Kaka entry (user 2026-10-26): empowered copies through the SAME plumbing SpawnSummon
		// uses - pool idiom, shell restore, fuse - but the doubled numbers bake here so the x2
		// multiplies AFTER the caller's rank scaling, and the compass fan seeds each familiar's
		// velocity along its own bearing: with no prey around they drift outward like the bees,
		// and the moment prey shows up the familiar's nearest-body steering takes over. KAKA_COUNT
		// > free slots is tolerated like SpawnSummon tolerates it: a crowded pool drops the tail.
		int born = 0;
		for (int i = 0; i < _summons.Count; i++)
		{
			Godot.Node2D s = _summons[i];
			if (s.GlobalPosition.X >= -8000.0f)
				continue; // occupied slot
			double a = System.Math.PI * 2.0 * born / KAKA_COUNT;
			s.GlobalPosition = WrapNearCamera(center + new Vector2((float)System.Math.Cos(a), (float)System.Math.Sin(a)) * (float)KAKA_RING);
			_summonVel[i] = new Vector2((float)System.Math.Cos(a), (float)System.Math.Sin(a)) * speed;
			_summonDmg[i] = dmg * KAKA_DMG; // x2 AFTER RollDamage's rank scaling (user)
			_summonLife[i] = fuse;          // same fuse: empowered friends live just as long
			_summonR[i] = radius * KAKA_R;  // x2 AFTER the AREA tag (user: doubles the upgraded burst)
			_summonSpd[i] = speed;
			_summonBurst[i] = false;
			_summonKaka[i] = true; // the doubling and the burst print ride this flag
			_summonLuck[i] = luck; // the ult blast rides the same lucky-explosion lane
			Godot.ColorRect cr = (Godot.ColorRect)s.GetChild(0);
			cr.Size = new Vector2(14.0f, 14.0f);
			cr.Position = new Vector2(-7.0f, -7.0f);
			Godot.Sprite2D fxs = (Godot.Sprite2D)cr.GetChild(0);
			fxs.Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f);
			if (_famArt)
				cr.Color = new Color(0.26f, 0.2f, 0.34f, 0.0f); // art rides: shell stops drawing
			cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f);
			born++;
			if (born >= KAKA_COUNT)
				return;
		}
	}


	private void UpdateSummons(float delta)
	{
		for (int i = 0; i < _summons.Count; i++)
		{
			Godot.Node2D s = _summons[i];
			if (s.GlobalPosition.X < -8000.0f)
				continue; // free slot
			if (_summonBurst[i])
			{
				// mid flare: the bite already went off, so this slot only fades and frees itself
				_summonLife[i] -= delta;
				if (_summonLife[i] <= 0.0)
					DespawnSummon(i);
				continue;
			}
			_summonLife[i] -= delta;
			Vector2 p = s.GlobalPosition;
			// the last second of the fuse runs hot so a dying familiar reads without a HUD
			if (_summonLife[i] < 1.0)
			{
				float f = (float)(1.0 - _summonLife[i]);
				if (f < 0.0f)
					f = 0.0f;
				((Godot.ColorRect)s.GetChild(0)).Modulate = new Color(1.0f + f * 0.9f, 1.0f, 1.0f, 1.0f);
			}
			if (_summonLife[i] <= 0.0)
			{
				// fuse out: it detonates wherever it stands, so a familiar can never be parked
				// under a boss and saved for later (user rule 2026-10-22)
				DetonateSummon(i);
				continue;
			}
			// steer at the closest living body on the SHORT torus path: the world loops, so
			// absolute deltas would have the familiar take 2400 px around a seam
			Godot.Node2D best = null;
			Combatant bestC = null;
			float bestD = 1e9f;
			for (int e = 0; e < _enemies.Count; e++)
			{
				Godot.Node2D n = _enemies[e];
				Combatant nc = (Combatant)n;
				if (nc == null || nc.kind == "dead")
					continue;
				Vector2 dv = WrapDelta(p, n.GlobalPosition);
				float dd = dv.Length();
				if (dd >= bestD)
					continue;
				bestD = dd;
				best = n;
				bestC = nc;
			}
			if (best != null)
			{
				// a familiar bites a boss exactly like a bolt does: the beat loop cannot advance
				// while a live boss owns the arena. The hit test rides the authored silhouette.
				float hitR = bestC.hitR; // HITBOX = ART: the familiar rams the DRAWN body (bosses ride their art stamp too, so the silhouette is the same law the bolts read)
				if (bestD <= hitR)
				{
					DetonateSummon(i); // it ran into a body: the fuse goes off ON contact
					continue;
				}
				_summonVel[i] = WrapDelta(p, best.GlobalPosition).Normalized() * _summonSpd[i];
			}
			// familiars wrap like every other entity instead of dying offscreen; the fuse is
			// what bounds their lifetime, so nothing here can orbit the torus forever
			s.GlobalPosition = WrapNearCamera(p + _summonVel[i] * delta);
			// user 2026-11-02: familiars BOUNCE UP AND DOWN while they run - VISUAL ONLY: the art
			// child rides a hop, the hit model, fuse and steering stay exactly as authored. Phase
			// rides the fuse clock (it advances every live frame) + slot stagger so a pack never
			// hops in lockstep; zero new state (assistant picks: cycle ~0.79 s, 7 px hop)
			Godot.Sprite2D fart = (Godot.Sprite2D)((Godot.ColorRect)s.GetChild(0)).GetChild(0);
			float ph = (float)_summonLife[i] * 8.0f + i * 1.9f;
			fart.Position = new Vector2(7.0f, 7.0f - 7.0f * (float)System.Math.Abs(System.Math.Sin((double)ph)));
		}
	}

	private void DetonateSummon(int i)
	{
		Godot.Node2D s = _summons[i];
		Vector2 p = s.GlobalPosition;
		double dmg = _summonDmg[i];
		double r = _summonR[i];
		// lucky explosions (Yu row 44, user 2026-11-01): the blast may fire a SECOND pass at
		// the same bodies. Rolled ONCE per detonation, and only when a rank is in play, so a
		// plain familiar pays zero Rng draw.
		int caught = 0; // blast tally: only an empowered Kaka prints it
		int passes = 1;
		if (_summonLuck[i] > 0.0 && Rng.Randf() < (float)_summonLuck[i])
			passes = 2; // double blast: everything a caught body eats repeats
		for (int pass = 0; pass < passes; pass++)
		{
			for (int e = 0; e < _enemies.Count; e++)
			{
				Godot.Node2D n = _enemies[e];
				Combatant nc = (Combatant)n;
				if (nc == null || nc.kind == "dead")
					continue;
				// the blast is a full-body sweep like the cleric's ring: damage is NOT split among
				// whatever stands in it (user rule 2026-10-22), which is what makes a familiar
				// worth spending on a packed horde instead of leaking damage on a lone body.
				if (WrapDelta(p, n.GlobalPosition).Length() - nc.hitR > r) // HITBOX = ART: the ring bites whatever art crosses it
					continue;
				nc.TakeDamage(dmg);
				TryChainLightning(n); // Baldrun seat: the blast can loose a chain per caught body
				if (pass == 0)
					caught++; // the Kaka tally counts bodies, not blast passes
			}
		}
		if (_summonKaka[i])
			GD.Print("[10thAge] (ult) The Kaka burst: " + caught + " body(s) caught in the doubled blast");
		// the slot BECOMES the blast for a beat: the shell grows to the radius it actually bit
		// and fades, so the AoE reads without a HUD. Pure nodes - the C# _draw hook is dead in 4.7.
		_summonBurst[i] = true;
		_summonLife[i] = 0.16;
		Godot.ColorRect cr = (Godot.ColorRect)s.GetChild(0);
		cr.Size = new Vector2((float)r * 2.0f, (float)r * 2.0f);
		cr.Position = new Vector2((float)-r, (float)-r);
		cr.Modulate = new Color(1.5f, 0.9f, 1.3f, 0.55f);
		cr.Color = new Color(0.26f, 0.2f, 0.34f, 1.0f); // the shell IS the blast again: reclaim the draw
		((Godot.Sprite2D)cr.GetChild(0)).Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f); // the familiar sinks into its own blast
	}

	private void DespawnSummon(int i)
	{
		Godot.Node2D s = _summons[i];
		s.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
		((Godot.ColorRect)s.GetChild(0)).Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
		_summonBurst[i] = false;
	}

	private void DespawnBullet(int i)
	{
		Godot.Node2D b = _bullets[i];
		b.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
		((Godot.ColorRect)b.GetChild(0)).Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
	}

	private void ClearBullets()
	{
		int swept = 0;
		for (int i = 0; i < _bullets.Count; i++)
		{
			if (_bullets[i].GlobalPosition.X >= -8000.0f)
			{
				DespawnBullet(i);
				swept++;
			}
		}
		if (swept > 0)
			GD.Print("[10thAge] camp safety: " + swept + " in-flight boss bullets swept before the card went up");
	}

	private void PickerTick(double d)
	{
		if (_xp >= _xpNext)
		{
			_xp -= _xpNext;
			_level++;
			_xpNext = 5.0 + _level * 4.0;
			// user-approved growth (2026-10-19): every level-up also grants each party
			// member a flat +0.5 weapon damage automatically (reduced from the +1 the
			// first cut shipped - players were trending too strong), stacking under the
			// card's pick (and landing even when every rank is capped). The free
			// reach/speed ranks stay reverted per user; damage-only stands.
			foreach (PartyMember m in _party)
				m.weaponDamage += 0.5f;
			GD.Print("[10thAge] level " + _level + " growth: all members +0.5 weapon dmg flat");
			// human-test: fixed cards felt same-y every level - roll variety instead (see
			// RollOptions): M2 grows the card with the squad, one still-open track per member
			// in play, read with the number keys
			if (RollOptions())
			{
				_pickerActive = true;
				_pickerWait = 0.0;
				_pickerCard = "LEVEL UP";
				// human test: one long line ran past the window edge - stack the picks so they
				// always fit, and read them with NUMBER keys (A/S/D were the movement keys, so a
				// walk tap could end the card unseen). M2: the stack grows with the squad, one
				// line per member in play.
				string card = _pickerCard + " - pick ONE (combat is frozen):\n";
				for (int s = 0; s < PICK_SLOTS; s++)
				{
					if (_optRoll[s] < 0)
						continue;
					card += "[" + (s + 1) + "] " + OptLabel(s) + "\n";
				}
				_pickerLabel.Text = card;
				GD.Print("[10thAge] level " + _level + " picker up: " + PickerLine());
			}
			else
			{
				// user-locked UX: a capped party must still say WHY no card appeared, or
				// a level-up reads as a silent stat steal (the picker never opens here, so
				// the no-timeout rule cannot soft-lock either)
				GD.Print("[10thAge] level " + _level + ": every upgrade capped - no pick offered");
				_pickerNoticeT = 4.0;
				_pickerLabel.Text = "EVERY RANK CAPPED - xp banked (evolutions are M2 content)";
			}
		}
		// the capped notice fades on its own timer; a live card owns the label instead
		if (_pickerNoticeT > 0.0)
		{
			_pickerNoticeT -= d;
			if (_pickerNoticeT <= 0.0 && !_pickerActive)
				_pickerLabel.Text = "";
		}
		if (!_pickerActive)
			return;
		_pickerWait += d;
		// P1: the picker used to ride on the WASD letters, so the old GetActionStrength
		// read confirmed the frame the card appeared for anyone still holding A/S.
		// NUMBER keys avoid the movement clash entirely - still edge-read only, so a
		// fresh tap is a deliberate pick. Slots map through _optRoll (this card's roll).
		int choice = -1;
		// card-shaped key scan: the number of keys the card can show is the squad size
		// (+1 wild), so scan the slots rather than three hand-written branches (pick_1..pick_7
		// exist in the input map - pick_7 joined the map with the sixth face; an empty slot is
		// not rendered, so a press there is a no-op
		for (int s = 0; s < PICK_SLOTS; s++)
		{
			if (!Input.IsActionJustPressed("pick_" + (s + 1)))
				continue;
			choice = _optRoll[s];
			break;
		}
		// user-locked (2026-10-19): the picker NEVER times out for human play - it waits
			// for a real numbered press forever. Only the pilot autopilot auto-confirms (2 s) so
		// verification smokes keep moving; it takes an uncapped slot from this card's roll.
		if (choice < 0 && _pilot && _pickerWait >= 2.0)
		{
			for (int s = 0; s < PICK_SLOTS; s++)
			{
				if (_optRoll[s] >= 0)
				{
					choice = _optRoll[s];
					break;
				}
			}
		}
		if (choice < 0)
			return;
		ApplyUpgrade(choice);
		_pickerActive = false;
		_pickerLabel.Text = "";
	}

	private string OptLabel(int slot)
	{
		if (slot < 0 || slot >= _optRoll.Length)
			return "-";
		int i = _optRoll[slot];
		if (i < 0 || i >= OPT_LABEL.Length)
			return "-";
		PartyMember m = GetMemberByIndex(OPT_MEMBER[i]);
		if (m == null)
			return "-";
		// human-test UX: show WHERE a pick lands in the 3-rank track, so a player
		// reading the card knows one more rank is all the track has before M2's
		// evolution
		return m.displayName + " " + m.weaponName + ": " + OPT_LABEL[i] +
			   " (" + (_pickRank[i] + 1) + "/" + PICK_RANK_CAP + ")";
	}


	// the same card as ONE log line, so a headless smoke reads exactly what a human saw
	// (and a grown card never gets a second, drifting print statement to keep them in step)
	private string PickerLine()
	{
		string s = "";
		for (int i = 0; i < PICK_SLOTS; i++)
		{
			if (_optRoll[i] < 0)
				continue;
			s += (s == "" ? "" : " | ") + "[" + (i + 1) + "] " + OptLabel(i);
		}
		return s;
	}
	// ---------- per-member HP strips: name + 110x7 bar + state dot, 6 slots ----------
	// Built lazily (the party is only known once the scene children are scanned) and
	// headroomed to 6 slots for M2's bigger roster. Human-test: stacked label lines
	// ran off the window edge and were unreadable mid-horde; bars scan at a glance.
	private void UpdateHpStrips(float delta)
	{
		if (_hudLayer == null || _party.Count == 0)
			return;
		if (!_hpStripsBuilt)
		{
			_hpStripsBuilt = true;
			for (int i = 0; i < 6; i++)
			{
				float y = 60.0f + i * 32.0f; // human pass-8: a few more px between rows
											 // used to cut through the name text (16 px
											 // font in a 22 px row)
				Godot.Label n = new Godot.Label();
				n.Position = new Vector2(24.0f, y);
				n.ZIndex = 12;
				_hudLayer.AddChild(n);
				_hpName.Add(n);
				Godot.ColorRect bg = new Godot.ColorRect();
				bg.Position = new Vector2(24.0f, y + 21.0f);
				bg.Size = new Vector2(110.0f, 7.0f);
				bg.Color = new Color(0.12f, 0.11f, 0.10f, 0.9f);
				bg.ZIndex = 12;
				_hudLayer.AddChild(bg);
				_hpBg.Add(bg);
				Godot.ColorRect f = new Godot.ColorRect();
				f.Position = new Vector2(24.0f, y + 21.0f);
				f.Size = new Vector2(0.0f, 7.0f);
				f.Color = new Color(0.83f, 0.64f, 0.24f, 1.0f);
				f.ZIndex = 13;
				_hudLayer.AddChild(f);
				_hpFill.Add(f);
				Godot.ColorRect dot = new Godot.ColorRect();
				dot.Position = new Vector2(140.0f, y + 21.0f);
				dot.Size = new Vector2(7.0f, 7.0f);
				dot.ZIndex = 13;
				_hudLayer.AddChild(dot);
				_hpDot.Add(dot);
			}
			// Martha's mini strip: a 40x5 bar + number built once, hidden until it anchors to Hew's row
			_marthaHpN = new Godot.Label();
			_marthaHpN.ZIndex = 12;
			_hudLayer.AddChild(_marthaHpN);
			_marthaHpBg = new Godot.ColorRect();
			_marthaHpBg.Size = new Vector2(40.0f, 5.0f);
			_marthaHpBg.Color = new Color(0.12f, 0.11f, 0.10f, 0.0f);
			_marthaHpBg.ZIndex = 12;
			_hudLayer.AddChild(_marthaHpBg);
			_marthaHpF = new Godot.ColorRect();
			_marthaHpF.Size = new Vector2(0.0f, 5.0f);
			_marthaHpF.Color = new Color(0.55f, 0.60f, 0.72f, 1.0f); // steel sheen: her iron, not the party brass
			_marthaHpF.ZIndex = 13;
			_hudLayer.AddChild(_marthaHpF);
			// Repair gauge (user 2026-11-24 round 7): built with the mini strip; hidden until broken
			_marthaRepairBg = new Godot.ColorRect();
			_marthaRepairBg.Size = new Vector2(40.0f, 4.0f);
			_marthaRepairBg.Color = new Color(0.12f, 0.11f, 0.10f, 0.0f);
			_marthaRepairBg.ZIndex = 12;
			_hudLayer.AddChild(_marthaRepairBg);
			_marthaRepairF = new Godot.ColorRect();
			_marthaRepairF.Size = new Vector2(0.0f, 4.0f);
			_marthaRepairF.Color = new Color(0.85f, 0.62f, 0.25f, 1.0f); // brass: the weld seam she is laying
			_marthaRepairF.ZIndex = 13;
			_hudLayer.AddChild(_marthaRepairF);
		}
		float hewRowY = -1000.0f; // the row Martha's mini strip anchors to
		for (int i = 0; i < _hpName.Count; i++)
		{
			Godot.Label n = _hpName[i];
			Godot.ColorRect bg = _hpBg[i];
			Godot.ColorRect f = _hpFill[i];
			Godot.ColorRect dot = _hpDot[i];
			// strip slot i shows the i-th PC to ARRIVE, not the i-th roster entry
			// (join-order map built in BuildJoinLottery; the STRANDED preview rides the
			// next strip, which is exactly the slot the current recruit drew)
			PartyMember m = (i < _hpOrder.Count) ? GetMemberByIndex(_hpOrder[i]) : null;
			if (m == null || (!m.joined && _recruitPending != m))
			{
				// unused slot / not-yet-arrived PC: fully invisible, never a phantom
				// bodyguard or a spoiler strip telling the player who is coming next.
				// once the field beat lands the strip goes live (pass 11): the escort
				// chips HP and the join brings in whatever survives, so it must read
				n.Text = "";
				f.Size = new Vector2(0.0f, 7.0f);
				bg.Color = new Color(0.12f, 0.11f, 0.10f, 0.0f);
				dot.Color = new Color(0.0f, 0.0f, 0.0f, 0.0f);
				continue;
			}
			bool isOut = m.eliminated; // "out" is a reserved word in C# (the parameter modifier)
			if (m.displayName == "Hew")
				hewRowY = 60.0f + i * 32.0f; // Martha's mini strip rides Hew's row (user 2026-11-23)
			bool down = m.downed && !isOut;
			float frac = m.EffMaxHp() > 0.0 ? (float)(m.hp / m.EffMaxHp()) : 0.0f;
			if (frac < 0.0f)
				frac = 0.0f; // HP floors at 0 (overkill never poisons the bar)
			if (frac > 1.0f)
				frac = 1.0f;
			string tag = (m.isLeader ? "* " : "") + m.displayName + StackElixirTag(m); // elixir names ride next to Stack while brewed
			// the over-glass buffer reads next to the number too (user 2026-11-06)
			string textra = (m.tempHp > 0.0f ? " +" + (int)m.tempHp + "t" : "");
			bool stranded = !m.joined; // field beat only: live strip tracks the join HP
			n.Text = stranded ? tag + " STRANDED " + (int)m.hp + "/" + (int)m.EffMaxHp()
												: (isOut ? tag + " OUT (campfire)"
													: (down ? tag + (m.stable ? " STABLE" : " DOWN") + textra
														: tag + " " + (int)m.hp + "/" + (int)m.EffMaxHp() + textra));
			f.Size = new Vector2(110.0f * frac, 7.0f);
			// Temporary HP (user 2026-11-06 Field Medic over-heal): the fill still tracks the
			// body's OWN fraction - an over-glass buffer must never fake a full bar - but it
			// tints the fill pale teal so the player can see the medic is holding damage
			f.Color = m.tempHp > 0.0f ? new Color(0.45f, 0.78f, 0.72f, 1.0f)
				: (m.isLeader ? new Color(0.83f, 0.64f, 0.24f, 1.0f)
					: new Color(0.62f, 0.52f, 0.38f, 1.0f));
			bg.Color = new Color(0.12f, 0.11f, 0.10f, 0.9f);
			dot.Color = stranded ? CHIP_AMBER
								: (isOut ? CHIP_GREY
									   : (down ? (m.stable ? CHIP_AMBER : CHIP_BLOOD) : CHIP_SAGE));
		}
		// Martha mini strip draw: anchored beside Hew's bar, hidden while she is off-field (user 2026-11-23)
		if (hewRowY > -500.0f && _marthaBody != null && FindHew() != null)
		{
			double mcap = MARTHA_HP + _marthaHpBonus + MARTHA_VITALS;
			_marthaHpN.Position = new Vector2(152.0f, hewRowY + 2.0f); // round-7 nudge (user 2026-11-24): her name was clipping the hp bar - it rides two px higher
			_marthaHpN.Text = (_marthaHp <= 0.0 || _marthaT > 0.0) ? "Martha DOWN" : "Martha " + (int)_marthaHp + "/" + (int)mcap; // round-7 re-key (user 2026-11-24): the fell no longer drains her - DOWN rides the 20 s yard-time too
			_marthaHpBg.Position = new Vector2(152.0f, hewRowY + 21.0f);
			_marthaHpBg.Color = new Color(0.12f, 0.11f, 0.10f, 0.9f);
			_marthaHpF.Position = new Vector2(152.0f, hewRowY + 21.0f);
			_marthaHpF.Size = new Vector2(mcap > 0.0 ? (float)(_marthaHp / mcap) * 40.0f : 0.0f, 5.0f); // floors at 0: overkill never poisons the bar
			// Repair gauge draw (user 2026-11-24 round 7): under her strip, fills as the yard-time knits.
			// MARTHA_BREAK is 20 s; the doubled tick is proximity to Hew, not this gauge.
			_marthaRepairBg.Position = new Vector2(152.0f, hewRowY + 30.0f);
			_marthaRepairBg.Color = new Color(0.12f, 0.11f, 0.10f, 0.9f);
			_marthaRepairF.Position = new Vector2(152.0f, hewRowY + 30.0f);
			_marthaRepairF.Size = new Vector2(_marthaT > 0.0 ? (float)(1.0 - _marthaT / 20.0) * 40.0f : 0.0f, 4.0f);
		}
		else if (_marthaHpN != null)
		{
			_marthaHpN.Text = "";
			_marthaHpBg.Color = new Color(0.12f, 0.11f, 0.10f, 0.0f);
			_marthaHpF.Size = new Vector2(0.0f, 5.0f);
			_marthaRepairBg.Color = new Color(0.12f, 0.11f, 0.10f, 0.0f); // gauge hidden with the strip
			_marthaRepairF.Size = new Vector2(0.0f, 4.0f);
		}
	}

	// ---------- join lottery draw (user 2026-10-23: dynamic runs) ----------
	// Builds the run's recruit order ONCE at boot: one weighted pick per ally-beat row,
	// without replacement, from the roster minus the run's own leader. Higher weight =
	// shows up on more runs; weight 0 = cannot appear this stage; a dry pool leaves the
	// remaining slots -1 (logged no-op at the beat). _joinRng is unseeded on purpose so
	// every smoke / every human run re-rolls.
	// ---------- pre-run character select: card build + polling (mockup layout pass) ----------
	// One CanvasLayer under a full-rect anchor Control: every position is a fraction of
	// the VISIBLE rect (MeasureWindow's probe proved that rect), so the card rides any
	// window / DPI. Slots pre-allocate hidden like the boot pools; UI text rides base-
	// viewport pixel sizes like the HUD's. MOCKUP: ONE board page holds all six
	// seats in rows of three (3+3; the bottom row fills once the sixth face lands).
	// Click a portrait to SELECT it (the gold ring box rides that slot); click the
	// CONFIRM plate at the card bottom to seat that PC. The right side stacks the
	// SELECTED PC's sheet over the ALWAYS-up party stats box; the "(+0)" rows on
	// both are display placeholders for the TWO meta tracks (sheet = per-character,
	// box = party-wide) M3 programs into the 17-stat tag registry.
	// Card copy is data-driven: tracks read OPT_LABEL/OPT_MEMBER live; the bonus / ult
	// blurbs below stay honest to their live lanes (index = member index, the OPT_MEMBER
	// convention: 0 Baldrun, 1 Mosiah, 2 Asper, 3 Alvora, 4 Yu, 5 Stack). PLACEHOLDER frames get
	// the real UI pass the user gathers; the art rects already show the painted minis.
	// Board slot rects as anchor fractions of the page window: slot i -> L,T,R,B
	// (four rows of three: 0,1,2 top | 3,4,5 | 6,7,8 | 9,10,11 bottom - Keeko enters the board 2026-11-14, two spares ahead)
	static readonly float[] SEL_GRID = { 0.04f, 0.03f, 0.30f, 0.24f, 0.35f, 0.03f, 0.61f, 0.24f, 0.66f, 0.03f, 0.96f, 0.24f,
		0.04f, 0.26f, 0.30f, 0.47f, 0.35f, 0.26f, 0.61f, 0.47f, 0.66f, 0.26f, 0.96f, 0.47f,
		0.04f, 0.49f, 0.30f, 0.70f, 0.35f, 0.49f, 0.61f, 0.70f, 0.66f, 0.49f, 0.96f, 0.70f,
		0.04f, 0.72f, 0.30f, 0.93f, 0.35f, 0.72f, 0.61f, 0.93f, 0.66f, 0.72f, 0.96f, 0.93f,
		0.04f, 0.95f, 0.30f, 1.16f }; // board fractions (assistant re-grid pick: 12 rects on 4 rows - retune by eye). 13th rect (seat 12 Hew): the page-bottom LEFT strip under row 4, clear of the CONFIRM plate at root-local 0.32-0.58 x 0.88-0.96 (page fractions run past 1.0 down there; existing 12 stay byte-identical, retune by eye)
	static readonly float[] SEL_PAGE = { 0.05f, 0.14f, 0.60f, 0.86f }; // the board window the build draws AND the click maps into
	// wheel-scroll law (2026-11-23 hand-play): all fractions of the PAGE height. The rows ride this
	// shift inside the window, the gold ring rides the same shift, and the click map undoes it - so
	// a scrolled row is clicked where it is DRAWN, never where it was built.
	static readonly float SEL_SCROLL_HOME = -0.07f; // the board opens centred on its own content (rows run 0.03..1.16 of the page): the 13th seat's parked row reads just inside the window bottom instead of under CONFIRM (assistant pick, retune by eye)
	// one wheel flick steps SEL_SCROLL_STEP page heights; the clamp lets a parked row come all the
	// way into the window and back out again (room for the future bigger roster)
	// wheel-up flicks bring the TOP rows in (the page rides down)
		static readonly float SEL_SCROLL_STEP = 0.08f; // one flick nudges the TARGET this much (the
		// old 0.14 JUMP every quarter second read stiff to hand-play lap 2: smaller nudges + the
		// chase below blend consecutive flicks into one glide). SMOOTH SCROLL glide speed (user
		// lap 2: "The scrolling is also really stiff, can you make it a smooth scrolling?"): the
		// page catches up SEL_SCROLL_CHASE page heights per SECOND toward the target (about 0.13
		// s to arrive at 60 fps, capped at half the gap per frame). The OLD SEL_SCROLL_MAX
		// CONSTANT clamp is GONE: the band is content-derived (ComputeSelClamp + _selClampLo/
		// _selClampHi) so a fully scrolled page ends FLUSH with the roster and no blank band can
		// ever show (user lap 2: "it's possible to scroll up beyond the characters ... It
		// shouldn't be able to do that")
		static readonly float SEL_SCROLL_CHASE = 8.0f; // assistant pick, retune at hand-play
	static readonly float SEL_PARK_EDGE = 0.12f; // a row's centre may overshoot the window by this page fraction before it parks hidden (about half a slot: a row at the edge still reads)
	static readonly bool[] SEL_LOCKED = { false, false, false, false, false, false, false, false, false, false, false, false, false }; // 13 seats (seat 12 Hew joins at parity)
	static readonly float[] SEL_CONFIRM = { 0.32f, 0.88f, 0.58f, 0.96f };
	// painted mini art per roster seat (index = memberIndex: 0 Baldrun | 1 Mosiah | 2 Asper | 3 Alvora | 4 Yu | 5 Stack | 6 Wash | 7 Bruno | 8 Brook)
		// art reorganised 2026-11-08 (user: "move all the current characters into the character folder")
		// - portraits now live under res://art/characters/ alongside the art/enemies/ tree
		static readonly string[] SEL_ART = { "res://art/characters/baldrun.png", "res://art/characters/mosiah.png", "res://art/characters/asper.png", "res://art/characters/alvora.png", "res://art/characters/yu.png", "res://art/characters/stack.png", "res://art/characters/wash.png", "res://art/characters/bruno.png", "res://art/characters/brook.png", "res://art/characters/keeko.png", "res://art/characters/hapi.png", "res://art/characters/athios.png", "res://art/characters/hew.png" };
		// PER-SEAT select-card scale is RETIRED (2026-11-23 hand-play, user: "Athios and Hew are
		// selectable but their art is too big. Look at the other characters art as a reference for
		// how it should look"): that lane pinned a px box AROUND the art's own native size and
		// bypassed the slot rect, so the 175x235 / 256x256 sheets landed ~native on a card sized
		// for the 78-98 W x 114-121 H class. One draw for EVERY seat now: the slot rect with
		// STRETCH_KEEP_ASPECT_CENTERED, which aspect-FITS the art to the slot's own height - the
		// big sheets get the same class read as the rest of the cast for free, nothing distorts
		// and nothing clips. The only per-seat answer left is the FILTER, and only where the draw
		// SHRINKS (a 2x sheet losing source pixels at NEAREST): LINEAR there, NEAREST for the 1:1-
		// up cast so the user's 2026-10-28 "not stretched" hard-edge law keeps its teeth.
		// The stage mini keeps its OWN knob (PartyMember.miniScale: 235 px * 0.38 = 89 px against
		// the class's 118 * 0.75 = 88 px) - the two lanes' numbers stay separate on purpose.
		static readonly float SEL_RING_PAD = 6.0f; // gold-ring px around the browsed slot (kept: the ring chrome, not the art lane)
		// shared art-box PIXEL height for the whole cast (sizing v2): class sheets are ~114-121 px
		// tall natively and drew native on the card (the look the user called the reference), so
		// every seat draws THIS height; width follows each canvas aspect exactly
		static readonly float SEL_ART_BOX_PX = 118.0f; // assistant pick from the PNG IHDRs, retune by eye
		static readonly string[] SEL_BONUS = {
		"Leader Bonus: Chain Lightning - All attacks have a 5% chance to unleash lightning on 5 targets for 10 damage.",
		"Leader Bonus: Pierce - All attacks gain +1 Pierce and all allies gain +5% Evasion.",
		"Leader Bonus: Bees! - A friendly bee spawns every 30 seconds. Touching one adds them as a temporary ally for 10s. They deal damage equal to half of Asper's Damage.",
		"Leader Bonus: Field Medic - All allies regenerate 1 HP every 10 seconds. This can over heal and add an additional 10 Temporary HP.",
		"Leader Bonus: Spirit Ritual - Friendly spirits spawn every 30s. Touching one heals all allies for 5 HP.",
		"Leader Bonus: Have a Drink! - While Stack leads, burning enemies deal 10% less damage and move 10% slower. Less effective on bosses.",
		"Leader Bonus: Inspiring Leader - While Wash leads, all allies gain +15 Max HP and +1 Armour.",
		"Leader Bonus: You... But Better! - All allies gain +15% Area and 10% Movement Speed.",
		"Leader Bonus: Inquisitive Mind - All allies gain +5% Evasion, +15% Collection Range and +15% XP.",
		"Leader Bonus: Gekkota Training - All allies gain +10% evasion, +10% movement speed and +10% attack speed.",
		"Leader Bonus: Call of the Grave - Enemies slain while Hapi leads have a 5% chance to rise as undead halves (half stats) that taunt nearby enemies.",
		"Leader Bonus: Spiritmane Pride - All allies gain +10% Movement Speed and +10% Evasion and take 10% less damage while Athios leads.",
		"Leader Bonus: Upgrades! - Every defeated enemy has a 5% chance to drop a part that boosts a random stat for ALL characters (and Martha): 5% Damage, 5% Attack Speed, 5% Radius, +1 Armour or +5 HP." };
	static readonly string[] SEL_ULT = {
		"Ultimate: Giant's Might (R) - Doubles Baldrun's size and reach and grants +50% Damage and Attack Speed for 10s.",
		"Ultimate: Bardic Inspiration (R) - All allies gain +25% Damage and Attack Speed for 10s",
		"Ultimate: Swarm of Bees (R) - Spawns 6 bees which seek out and stick to enemies. Each bee deals 5 damage per second for 10 seconds.",
		"Ultimate: Guardian Awakening (R) - Doubles the area of Spirit Guardians, deals +25 Damage and every kill while awakened heals a random ally for 1 HP.",
		"Ultimate: The Kaka (R) - Spawns 6 empowered familiars. These familiars deal double damage and have their blast radius doubled.",
		"Ultimate: Experimental Elixir (R) - All allies gain 2 of 6 random buffs for 10s. Healing: 1% HP Regeneration. Swiftness: 10% Movement Speed. Resilience: -10% Damage Taken. Boldness: +10% Damage. Precision: +10% Crit Chance. Magnetism: +10% Pickup Range. There is a 10% chance to gain the effects of all Elixirs.",
		"Ultimate: Oakhollow Assemble! (R) - All allies gain +25% Movement Speed, +5 Armour and 1% HP Regeneration per second for 10s",
		"Ultimate: Axes Away! (R) - Bruno spins two axes around him for 10 seconds. These blades gain all of the effects of his chosen upgrades.",
		"Ultimate: Assassination (R) - All allies gain +25% Multistrike, +15% Crit Chance and +25% Crit Damage for 10s",
		"Ultimate: Rewrite Reality (R) - All enemies (including any that spawn) become frozen in place for 10 seconds. (against final bosses, this instead increases damage dealt to them by 25%)",
		"Ultimate: Bring Out Yer Dead! (R) - For 10 seconds, every enemy slain rises as an undead ally with the same stats and real HP (they can be killed by enemies) that fights beside the squad for 10 seconds of its own.",
		"Ultimate: Beastial Rage (R) - All allies take half damage, attack 50% faster and gain +1 attack for 10s.",
		"Ultimate: Maximum Firepower! (R) - For 10 seconds Martha is invulnerable and gains +25% Damage and +25% Attack Speed, and Hew gains +25% Damage and +50% beam radius and area." };
	public void BuildSelectScreen()
	{
		_selLayer = new CanvasLayer();
		_selLayer.Layer = 9; // above the HUD layer: the card owns the frame while up
		AddChild(_selLayer);
		_selRoot = new Control();
		SetRectSel(_selRoot, 0.0f, 0.0f, 1.0f, 1.0f);
		_selLayer.AddChild(_selRoot);
		_selBack = new ColorRect();
		SetRectSel(_selBack, 0.0f, 0.0f, 1.0f, 1.0f);
		_selBack.Color = new Color(0.07f, 0.06f, 0.09f, 0.98f); // grimoire-dark board cover
		_selRoot.AddChild(_selBack);
		_selPage = new Control();
		SetRectSel(_selPage, SEL_PAGE[0], SEL_PAGE[1], SEL_PAGE[2], SEL_PAGE[3]); // the ONE board window (rows of three)
		_selRoot.AddChild(_selPage);
		_selFrame = new ColorRect();
		SetRectSel(_selFrame, SEL_GRID[0] - 0.02f, SEL_GRID[1] - 0.02f, SEL_GRID[2] + 0.02f, SEL_GRID[3] + 0.02f); // ring under slot 0
		_selFrame.Color = new Color(0.83f, 0.64f, 0.24f, 0.95f); // grimoire-gold RING: solid under the grown slot
		_selPage.AddChild(_selFrame);
		int n = _party.Count;
		_selTile = new Control[n];
		_selName = new Label[n];
		_selBox = new float[n * 4]; // per-seat ART box in page fractions (sizing v2, law below)
		_selArt = new Godot.Sprite2D[n]; // sizing v3: art-child handles (ShowSelPage refits them)
		_selArtH = new float[n]; // native canvas heights: the scale denominators
		for (int bi = 0; bi < _selBox.Length; bi++)
			_selBox[bi] = bi < SEL_GRID.Length ? SEL_GRID[bi] : 0.0f; // default = the slot rect: placeholder seats ride it, art seats overwrite it
		// one slot per seat (rows of three): tile + name plate. The slot rect IS the card box for
		// every seat now (v3: the WRAPPER rides the box; the ART child scales on itself)
		int ti = 0;
		foreach (PartyMember pm2 in _party)
		{
			// one slot: painted mini (Sprite2D child of a transparent wrapper, aspect-kept, NEAREST like the stage
			// minis) inside a name plate; page-parked by hiding when off-page (ShowSelPage)
			if (pm2.memberIndex >= 0 && pm2.memberIndex < SEL_ART.Length)
			{
				Godot.Texture2D tex = GD.Load(SEL_ART[pm2.memberIndex]) as Godot.Texture2D; // PM-verified load idiom
				if (tex == null)
				{				Godot.ColorRect ph = new ColorRect();
					ph.Color = new Color(0.16f, 0.15f, 0.18f, 1.0f); // placeholder block (framework)
					SetRectSel(ph, 0.04f, 0.04f, 0.96f, 0.96f); // full-rect of the slot below
					ph.AddChild(new Label()); // placeholder plate: a plain Control draws nothing
					_selTile[ti] = ph;
				}
				else
				{
					// sizing v3 (user 2026-11-23 hand-play lap 4: "Nothing seems to have changed. Both
					// are still too big" - laps 2-3 proved the draw NEVER resized: stretch 5 never fit,
					// stretch 0 never FILLED. LAW: NO stretch_mode value resizes a TextureRect in this
					// 4.7.2 C# build; art always paints at its NATIVE canvas size and the rect only places
					// it. The class cast read right BY LUCK (native ~114-121 px = the box law); athios
					// 175x235 and hew 256x256 stayed native huge. The PROVEN resize lane in this codebase
					// is Sprite2D + explicit .Scale (PartyMember.cs _mini/_bookFx draw byte-identical).
					// So: the tile is a transparent ColorRect WRAPPER (the anchor rect, park-hide handle,
					// plate child all ride it exactly as before), and the art is its Sprite2D CHILD,
					// refitted to the box in live px every frame in ShowSelPage.
					Godot.ColorRect wr = new Godot.ColorRect(); // wrapper: draws NOTHING (alpha 0)
					wr.Color = new Color(0.0f, 0.0f, 0.0f, 0.0f);
					Godot.Sprite2D sp = new Godot.Sprite2D(); sp.Texture = tex;
					float aw = (float)tex.GetWidth();
					float ah = (float)tex.GetHeight();
					float pagW = (SEL_PAGE[2] - SEL_PAGE[0]) * _visHalf.X * 2.0f; // page px W
					float pagH = (SEL_PAGE[3] - SEL_PAGE[1]) * _visHalf.Y * 2.0f; // page px H
					float boxH = SEL_ART_BOX_PX / pagH; // shared px height as a page-H fraction
					float boxW = SEL_ART_BOX_PX * aw / ah / pagW; // canvas aspect: never distorts
					float bcx = (SEL_GRID[ti * 4] + SEL_GRID[ti * 4 + 2]) * 0.5f; // box centred on its slot
					float bcy = (SEL_GRID[ti * 4 + 1] + SEL_GRID[ti * 4 + 3]) * 0.5f;
					_selBox[ti * 4] = bcx - boxW * 0.5f;
					_selBox[ti * 4 + 1] = bcy - boxH * 0.5f;
					_selBox[ti * 4 + 2] = bcx + boxW * 0.5f;
					_selBox[ti * 4 + 3] = bcy + boxH * 0.5f;
					// NEAREST keeps the hard edges where the fit is ~1:1 (the class cast); LINEAR for the
					// hard 2x shrink (athios 235 -> 118 px) where NEAREST craters thin features (assistant
					// pick, retune by eye at hand-play)
					sp.Set("texture_filter", tex.GetHeight() > 140.0f ? 1 : 0);
					// honest first fit (ShowSelPage keeps it live against the window): box px H IS
					// SEL_ART_BOX_PX by the box law, box px W follows the canvas aspect exactly
					sp.Position = new Vector2(SEL_ART_BOX_PX * aw / ah * 0.5f, SEL_ART_BOX_PX * 0.5f); // centred on the box
					sp.Scale = new Vector2(SEL_ART_BOX_PX / ah, SEL_ART_BOX_PX / ah); // uniform: never distorts
					wr.AddChild(sp);
					_selTile[ti] = wr;
					_selArt[ti] = sp; // refit handle (null = placeholder seat)
					_selArtH[ti] = ah; // scale denominator
				}
				// sizing v2 owns the tile: art seats ride their aspect BOX (computed in the art
				// branch above); the PLACEHOLDER seats keep the default _selBox = the slot copy. The
				// click map stays on the SLOT: the box lives inside the slot rect (and the plate
				// strip hangs at its foot), so the hit area is unchanged and a scrolled row is still
				// clicked where it is DRAWN (the map undoes _selScroll off the same slot fractions)
				SetRectSel(_selTile[ti], _selBox[ti * 4], _selBox[ti * 4 + 1], _selBox[ti * 4 + 2], _selBox[ti * 4 + 3]);
				_selName[ti] = new Label();
				// name plate law v2 (user 2026-11-23 hand-play lap 2: "names are overlapping with
				// character models too"): the plate used to ride y 0.85..0.99 INSIDE the tile - safe
				// when the tile rect WAS the slot, but the tile is the ART BOX now, so an inside
				// anchor sits ON the canvas. The plate now hangs BELOW the box: anchors past 1.0 are
				// legal (SetRectSel pins offsets to 0 so the anchor IS the rect) and ShowSelPage
				// parks whole tiles together with their plate children
				// sizing v3 plate law (user 2026-11-23 hand-play lap 4: "names are still getting
				// cut by the selection box" - CONFIRMED by math: the ring grows the box +/- 0.02 PAGE
				// fractions = ~+/-10-12 px on a ~605 px page, but the v2 top anchor 1.01 sat only +1.2
				// px below the box: the label STRADDLED the gold's bottom edge). 1.14 of the box height
				// = ~+17 px: clears the pad by a ~5 px breathing gap; 1.30 keeps the strip's own height.
				// Placeholder seats ride the same anchors on their slot rect: the plate just hangs lower,
				// still inside the click map (which rides the SLOT, untouched).
				SetRectSel(_selName[ti], -0.10f, 1.14f, 1.10f, 1.30f);
				_selName[ti].Text = pm2.displayName + (SEL_LOCKED[ti] ? " (locked)" : "");
				_selName[ti].Set("horizontal_alignment", 1); // CENTER (string key: binding-safe)
				_selTile[ti].AddChild(_selName[ti]);
				_selPage.AddChild(_selTile[ti]); // parent the slot: an orphan Control leaks at exit
				ti++;
			}
		}
		{
			ColorRect shp = new ColorRect();
			shp.Color = new Color(0.13f, 0.11f, 0.17f, 1.0f); // dark slate sheet plate
			SetRectSel(shp, 0.60f, 0.10f, 0.99f, 0.47f);
			_selSheet = shp;
		}
		_selRoot.AddChild(_selSheet);
		_selPanel = new Label();
		SetRectSel(_selPanel, 0.62f, 0.12f, 0.98f, 0.45f); // the sheet text over its plate
		_selPanel.Set("autowrap_mode", 2); // WORD wrap to the sheet rect
		_selRoot.AddChild(_selPanel);
		{
			ColorRect box = new ColorRect();
			box.Color = new Color(0.13f, 0.11f, 0.17f, 1.0f); // dark slate party plate
			SetRectSel(box, 0.60f, 0.49f, 0.99f, 0.95f);
			_selPartyBack = box;
		}
		_selRoot.AddChild(_selPartyBack);
		{
			Label boxText = new Label();
			SetRectSel(boxText, 0.62f, 0.50f, 0.98f, 0.94f); // the party stats box
			boxText.Set("autowrap_mode", 2); // WORD wrap (rows stay one-liners anyway)
			string pbox = "Party stats (ALL allies benefit when M3 raises them):";
			for (int p = 0; p < StatTags.NAMES.Length; p++)
				pbox += "\n" + StatTags.NAMES[p] + " +0"; // the registry owns the rows
			boxText.Text = pbox;
			_selParty = boxText;
		}
		_selRoot.AddChild(_selParty);
		{
			ColorRect btn = new ColorRect();
			btn.Color = new Color(0.28f, 0.20f, 0.08f, 1.0f); // dark bronze CONFIRM plate
			SetRectSel(btn, SEL_CONFIRM[0], SEL_CONFIRM[1], SEL_CONFIRM[2], SEL_CONFIRM[3]);
			_selConfirm = btn;
		}
		_selRoot.AddChild(_selConfirm);
		{
			Label face = new Label();
			SetRectSel(face, SEL_CONFIRM[0] + 0.01f, SEL_CONFIRM[1] + 0.01f, SEL_CONFIRM[2] - 0.01f, SEL_CONFIRM[3] - 0.01f);
			face.Text = "CONFIRM";
			face.Set("horizontal_alignment", 1); // CENTER (string key: binding-safe)
			_selConfirmLabel = face;
		}
		_selRoot.AddChild(_selConfirmLabel);
		_selHint = new Label();
		SetRectSel(_selHint, 0.06f, 0.04f, 0.98f, 0.10f); // title strip: the click grammar
		_selHint.Text = "PRE-RUN: click a portrait to select, wheel scrolls the page up/down, click CONFIRM to seat the Leader"; // wheel grammar spelled out (user 2026-11-23 hand-play ask)
		_selRoot.AddChild(_selHint);
		_selBrowse = _selDemo ? -1 : 0; // humans open boxed on the roster head
		ComputeSelClamp(); // content band BEFORE home is applied: home rides inside it
		_selScroll = SEL_SCROLL_HOME; // the board opens centred on its own content: the whole current roster reads inside the window at rest, the wheel walks it after
		if (_selScroll < _selClampLo)
			_selScroll = _selClampLo;
		if (_selScroll > _selClampHi)
			_selScroll = _selClampHi;
		_selTarget = _selScroll; // SMOOTH law: the target starts exactly where the board does
		ShowBreakdown(0); // the sheet opens on the roster head
		ShowSelPage();
	}
	// anchor-fraction setter: Godot 4 C# writes SetAnchor(Side, float) per side, the
	// same idiom MeasureWindow's probe already uses
	private void SetRectSel(Control node, float l, float t, float r, float b)
	{
		node.SetAnchor(Godot.Side.Left, l);
		node.SetAnchor(Godot.Side.Top, t);
		node.SetAnchor(Godot.Side.Right, r);
		node.SetAnchor(Godot.Side.Bottom, b);
		node.Set("offset_left", 0.0f); // set_anchor ALONE compensates offsets to keep the
		node.Set("offset_top", 0.0f);   // old rect parked: a runtime re-anchor (the ring riding a
		node.Set("offset_right", 0.0f);  // newly selected slot) never moved. Pin offsets to 0 so
		node.Set("offset_bottom", 0.0f); // the rect IS the anchor rect (build nodes ride it too)
	}

	// content-derived wheel clamp (user 2026-11-23 hand-play lap 2: "it's possible to scroll up
	// beyond the characters as seen in the second screenshot. It shouldn't be able to do that"):
	// the OLD law clamped +/- a CONSTANT page fraction (0.34), wide enough that a flicked-past
	// page showed a whole blank band over the roster. This law reads the ACTUAL art boxes once
	// per card build: the wheel can only push the FIRST box's top down to the window top, or
	// lift the LAST box's bottom up to the window bottom, so a fully scrolled page ends with
	// content FLUSH to the edge - no blank band above or below can ever appear - and a future
	// bigger roster automatically gets proportionally more room. Fractions of the page height:
	// content top+s <= 0 needs s <= -top, content bottom+s >= 1 needs s >= 1 - bot. Today
	// top 0.03, bottom ~1.13 -> band ~[-0.13, -0.03]; HOME -0.07 sits inside.
	private void ComputeSelClamp()
	{
		if (_selTile.Length == 0)
		{
			_selClampLo = SEL_SCROLL_HOME;
			_selClampHi = SEL_SCROLL_HOME;
			return; // card never built: a point band at HOME keeps any stray read sane
		}
		float top = 10.0f;
		float bot = -10.0f;
		for (int k = 0; k < _selTile.Length; k++)
		{
			if (_selBox[k * 4 + 1] < top)
				top = _selBox[k * 4 + 1];
			if (_selBox[k * 4 + 3] > bot)
				bot = _selBox[k * 4 + 3];
		}
		_selClampHi = -top;
		_selClampLo = 1.0f - bot;
		if (_selClampLo > _selClampHi)
			_selClampLo = _selClampHi = -top; // roster shorter than the window: flush-top rest only
	}
	// one board, one page: every tile AND the gold ring ride the PAGE SCROLL - their rects
	// shift by _selScroll page fractions (re-anchoring: SetRectSel pins offsets to 0, so
	// the anchor IS the rect and fractions can run past 1.0 down there). Rows whose centre
	// overshoots the window by SEL_PARK_EDGE park hidden; the gold frames only DRAWN rows.
	// (the 2026-11-22 scaled-seat ring branch is gone: every seat's box IS its slot now,
	// so one ring draw serves the whole cast)
	// CONFIRM stays live on a PARKED row - the scroll moves the view, not the roster.
	private void ShowSelPage()
	{
		for (int k = 0; k < _selTile.Length; k++)
		{
			// the scroll rides the anchors: same fractions the click map undoes
			SetRectSel(_selTile[k], _selBox[k * 4], _selBox[k * 4 + 1] + _selScroll,
				_selBox[k * 4 + 2], _selBox[k * 4 + 3] + _selScroll); // rows ride their ART box (sizing v2)
			// sizing v3 refit (THE sizing law): TextureRect stretch never resized a single seat in
			// this build (lap 4 CONFIRMED: art paints at NATIVE size, the rect only PLACES it), so
			// the Sprite2D CHILD rides the box in REAL px, re-fit every frame against the live _visHalf
			// exactly as the box fractions the wrapper above just got. Position is wrapper-LOCAL px
			// (wrapper top-left = box top-left AFTER the scroll anchors) -> no scroll term here,
			// only the box's own px size from the same page formula the build used.
			if (_selArt[k] != null)
			{
				float pw = (SEL_PAGE[2] - SEL_PAGE[0]) * _visHalf.X * 2.0f; // page px W this frame
				float ph = (SEL_PAGE[3] - SEL_PAGE[1]) * _visHalf.Y * 2.0f; // page px H this frame
				float bw = (_selBox[k * 4 + 2] - _selBox[k * 4]) * pw; // this row's ART box in px
				float bh = (_selBox[k * 4 + 3] - _selBox[k * 4 + 1]) * ph;
				_selArt[k].Position = new Vector2(bw * 0.5f, bh * 0.5f); // a Sprite2D is CENTRED on its position
				float sc = bh / _selArtH[k]; // uniform scale: box px H / native canvas H: never distorts
				_selArt[k].Scale = new Vector2(sc, sc); // the PROVEN PartyMember.cs FX-minis resize lane
			}
			// park rows past the window edge (half a slot of grace so an edge row still reads)
			// a scrolled-out row draws NOTHING: future bigger rosters page in from off-window
			// the name plate rides the tile (its child), so parking hides both together
			float cy = (SEL_GRID[k * 4 + 1] + SEL_GRID[k * 4 + 3]) * 0.5f + _selScroll;
			_selTile[k].Visible = (cy >= -SEL_PARK_EDGE && cy <= 1.0f + SEL_PARK_EDGE);
		}
		if (_selBrowse < 0 || _selBrowse >= _selTile.Length)
		{
			_selFrame.Visible = false; // nothing selected yet: the ring stays away
			return;
		}
		_selFrame.Visible = _selTile[_selBrowse].Visible; // the gold only frames rows that are DRAWN
		// the ring rides the selected slot: grown +/- 0.02 so its edge frames the tile
		// (byte-for-byte the shipped draw PLUS the same _selScroll shift the rows ride)
		// a parked browsed row parks its gold too - CONFIRM still works on it
		SetRectSel(_selFrame, _selBox[_selBrowse * 4] - 0.02f, _selBox[_selBrowse * 4 + 1] + _selScroll - 0.02f,
						_selBox[_selBrowse * 4 + 2] + 0.02f, _selBox[_selBrowse * 4 + 3] + _selScroll + 0.02f); // ring frames the ART box, not the slot
	}
	// the sheet for the SELECTED slot: name, then the mockup's Base Stats rows (live
	// off the roster block so the numbers can never drift) each with its "(+0)"
	// per-character meta placeholder, then the two ability blurbs. Speed reads as the
	// shot interval ("1.15 s") the mockup shows. (the framework pass's per-member
	// OPT_LABEL opt-ins stay on the camp card: the mockup sheet shows stats + abilities
	// only)
	private void ShowBreakdown(int slot)
	{
		if (slot < 0 || slot >= _party.Count)
			return;
		PartyMember m = _party[slot];
		string txt = m.displayName;
		txt += "\nHP " + (int)m.maxHp + " (+0)"; // per-character meta track (M3 grows)
		txt += "\nAttack speed " + m.cooldown + " s (+0)"; // shot interval, as the mockup reads
		txt += "\nDamage " + (int)m.weaponDamage + " (+0)";
		txt += "\nRange " + (int)m.range + " (+0)"; // m.range is the seat's reach
		txt += "\n" + SEL_BONUS[slot]; // the SEL strings are SELF-DESCRIBING ("Leader Bonus: ..."): the old "Leader Ability:" label doubled up (user 2026-11-14 saw "Ultimate Ability: Ultimate> Rewrite Reality")
		txt += "\n" + SEL_ULT[slot]; // same self-describing law: the sheet now reads "Ultimate: Rewrite Reality (R) - ..." ONCE
		_selPanel.Text = txt;
	}
	// card polling: the KEYBOARD pick rail is gone (mouse drives humans now), so
	// PollSelect only carries the pilot clock rails: the bench auto-browse (M1_SELECT=<name>)
	// and the auto-confirm one beat later, both printing their gate lines verbatim.
	// Humans hold here forever until SelectClick browses or CONFIRM seats (the picker's
	// no-timeout rule).
	private void PollSelect(double d)
	{
		_selWait += d;
		// bench auto door: resolve M1_SELECT=<name> to its roster slot (pilot clock)
		if (_pilot && _selBrowse < 0 && _selWait >= 1.2)
		{
			int pressed = -1;
			for (int k = 0; k < _party.Count; k++)
				if (_party[k].displayName == _selWant)
					pressed = k;
			if (pressed < 0)
			{
				GD.Print("[10thAge] (select) bench asked an unknown leader: " + _selWant + " - default seat stands");
				CloseSelect(false); // drop the card: the default seat stands, lottery rolls
				return;
			}
			GD.Print("[10thAge] (select) pilot auto-browse slot for: " + _party[pressed].displayName);
			_selBrowse = pressed;
			ShowSelPage();
			ShowBreakdown(pressed);
			return; // auto-browse once; the rail below confirms on the NEXT tick
		}
		// pilot confirm rail: one beat after the auto-browse, seat the browsed slot
		if (_pilot && _selBrowse >= 0 && _selWait >= 2.4)
		{
			ShowSelPage();
			ShowBreakdown(_selBrowse);
			GD.Print("[10thAge] (select) pilot confirm on browsed: " + _party[_selBrowse].displayName);
			CloseSelect(true);
			return;
		}
		// wheel rail (user 2026-11-23 hand-play, the full ask: "the mouse wheel just scrolls between
		// characters, but it should be scrolling the page/screen up or down to select characters as
		// eventually we're going to have a lot more"): the wheel no longer steps the browse ring -
		// it slides the WHOLE board inside its window (the PAGE SCROLL law by the fields). The click
		// stays the browse/selection rail; the wheel only moves the VIEW, so every seat is always
		// reachable by clicking wherever it is DRAWN. The beat clock keeps one flick from fanning
		// the scroll. The pilot clock drives the bench itself, so it never reads the wheel.
		_selWheelCd -= d;
		int wheel = _selWheel; // WHEEL READ: 4.7 dropped Input.get_wheel_direction, so _Input captures WHEEL_UP/WHEEL_DOWN into this one-shot
		_selWheel = 0;         // consumed EVERY frame (pilot frames too): an out-of-beat flick is dropped, not banked
		if (!_pilot && wheel != 0 && _selWheelCd <= 0.0)
		{
			_selWheelCd = 0.25; // assistant pick (retunable): one notch per quarter second
			// wheel-UP flicks bring the TOP rows into view: the board rides DOWN (+scroll);
			// the clamp keeps every seat reachable for the future bigger roster
			// SMOOTH law: the flick moves the TARGET only; the board chases it in the easing block
			// below the if, so a flick reads as a glide, not a jump
			_selTarget += (wheel > 0 ? 1.0f : -1.0f) * SEL_SCROLL_STEP;
			// CONTENT-DERIVED clamp (user lap 2: "it's possible to scroll up beyond the characters
			// as seen in the second screenshot. It shouldn't be able to do that"): the OLD constant
			// +/- 0.34 band let a flicked-past page show a whole blank band over the roster. The
			// band now comes from the ACTUAL art boxes (ComputeSelClamp at card build): the wheel
			// may only push the first box top down to the window top or lift the last box bottom
			// up to the window bottom, so a fully scrolled page ends FLUSH with the roster and a
			// future bigger roster automatically gets proportionally more room
			if (_selTarget > _selClampHi)
				_selTarget = _selClampHi;
			if (_selTarget < _selClampLo)
				_selTarget = _selClampLo;
			// scroll gate line: percent of the page toward the TARGET (the board glides toward it;
			// headless benches have no pointer so this only prints when a flick is simulated)
			GD.Print("[10thAge] (select) wheel scroll: " + (int)(_selTarget * 100.0f + 0.5f) + "% of page");
		}
		// SMOOTH SCROLL (user 2026-11-23 hand-play lap 2: "The scrolling is also really stiff,
		// can you make it a smooth scrolling?"): the flick above only nudged _selTarget; the board
		// CHASES that target exponentially every frame here, so consecutive flicks blend into one
		// gliding motion instead of teleporting a whole step on a quarter-second beat. k =
		// SEL_SCROLL_CHASE * dt (about 0.13 s to arrive at 60 fps), capped at half the gap so a
		// long frame cannot skip past the target; both ends of the chase sit inside the content
		// clamp, so the band can never be escaped. Card-only: the pilot clock never reaches here.
		if (_selScroll != _selTarget)
		{
			float k = SEL_SCROLL_CHASE * (float)d;
			if (k > 0.5f)
				k = 0.5f;
			_selScroll += (_selTarget - _selScroll) * k;
			ShowSelPage(); // rows, ring and park flags ride the same beat (one anchor pass)
		}
		// human card: waits for a real click forever (no timeout)
		return;
	}
	// human rail: a click maps window px -> the card's anchor fractions (CONFIRM tested
	// FIRST so a near-miss on the button never re-browses). The card spans the measured
	// window, so fractions ride _visHalf (double the half-extents); slots convert into
	// the PAGE rect's own anchors (SEL_PAGE, the rect the build drew).
	// (headless gates have no pointer: only the pilot clock rails above walk the bench
	// select -> confirm -> lottery wiring)
	private void SelectClick(Vector2 pos)
	{
		float fw = _visHalf.X * 2.0f;
		float fh = _visHalf.Y * 2.0f;
		if (fw < 64.0f || fh < 64.0f)
			return; // window not measured yet: garbage fractions beat a bad seat
		float fx = pos.X / fw;
		float fy = pos.Y / fh;
		// CONFIRM plate first (root-local fractions)
		if (fx >= SEL_CONFIRM[0] && fx <= SEL_CONFIRM[2] && fy >= SEL_CONFIRM[1] && fy <= SEL_CONFIRM[3])
		{
			if (_selBrowse >= 0)
			{
				GD.Print("[10thAge] (select) confirm click: " + _party[_selBrowse].displayName);
				CloseSelect(true); // the shared door seats the browsed PC
			}
			else
				GD.Print("[10thAge] (select) confirm click with nothing selected: default seat stands");
			return;
		}
		// slots next (page-local fractions UNDOING the page scroll: ShowSelPage re-anchored
		// every tile at SEL_GRID + _selScroll, so a scrolled row is clicked where it is DRAWN,
		// never where it was built)
		float px = (fx - SEL_PAGE[0]) / (SEL_PAGE[2] - SEL_PAGE[0]);
		float py = (fy - SEL_PAGE[1]) / (SEL_PAGE[3] - SEL_PAGE[1]) - _selScroll;
		for (int k = 0; k < _selTile.Length; k++)
		{
			if (!_selTile[k].Visible)
				continue; // parked rows are not clickable anywhere: the window is the door
			if (px < SEL_GRID[k * 4] || px > SEL_GRID[k * 4 + 2] ||
				py < SEL_GRID[k * 4 + 1] || py > SEL_GRID[k * 4 + 3])
				continue;
			if (SEL_LOCKED[k])
			{
				GD.Print("[10thAge] (select) click on a locked seat: " + _party[k].displayName);
				return; // the "(locked)" tag stays honest
			}
			if (k == _selBrowse)
			{
				GD.Print("[10thAge] (select) re-click on the browsed seat: use CONFIRM to seat it");
				return; // browsing again is a no-op: CONFIRM is the seat door
			}
			_selBrowse = k;
			ShowSelPage();
			ShowBreakdown(_selBrowse);
			GD.Print("[10thAge] (select) browse: " + _party[_selBrowse].displayName);
			return;
		}
		GD.Print("[10thAge] (select) click landed on bare board: nothing changes");
	}
	// the card closes: seat swap (if confirmed) THEN the deferred lottery (the swap MUST
	// precede it - same rule as the bench valve comment above: the demoted Leader waits
	// for its join beat like any other recruit; _hpOrder just builds later on such a run)
	private void CloseSelect(bool seated)
	{
		if (seated && _selBrowse >= 0 && _selBrowse < _party.Count)
		{
			if (TrySeatLeader(_party[_selBrowse].displayName))
				GD.Print("[10thAge] (select) leader seated: " + _party[_selBrowse].displayName);
		}
		_selHold = false;
		if (_selLayer != null)
			_selLayer.QueueFree(); // the whole card is gone in one cut: no RID/ODB leftovers at exit
		// C# discipline: a field holding a Godot node keeps it alive past the native
		// free, so every card field must go null or the art textures stay "in use" at
		// exit and Godot prints leak warnings on the smoke gate
		_selLayer = null;
		_selRoot = null;
		_selBack = null;
		_selPage = null;
		_selFrame = null;
		_selPanel = null;
		_selHint = null;
		_selSheet = null; // C# discipline: card fields must go null (leak gate)
		_selPartyBack = null;
		_selParty = null;
		_selConfirm = null;
		_selConfirmLabel = null;
		for (int k = 0; k < _selTile.Length; k++)
		{
			_selTile[k] = null;
			_selName[k] = null;
		}
		// the flow the user locked is Main Menu > Character Select > Stage Select: with the
		// leader card down, the stage card (parked hidden at boot) comes up and its own rails
		// own the mouse and the pilot clock. The lottery then waits for CloseStage: the JOIN_W
		// row and the _testRoom switch both RIDE the stage, so neither can roll before the pick.
		// A launch that never opens the stage card (pilot benches that do not ask M1_STAGE)
		// rolls it here, so every existing headless gate stays byte-identical.
		OpenStageCard();
		if (!_stageHold)
			BuildJoinLottery();
	}
	// ---------- STAGE SELECT card (the leader card above is the template) ----------
	// Same grammar, same discipline: one board page, one plate per registry entry, the gold
	// ring rides the browsed plate, CONFIRM at the card bottom starts the run. The right-hand
	// sheet is composed OFF THE LIVE DATA (beat census, JOIN_W row, ring faucet) so the card
	// cannot lie about what the run will do; STAGE_BLURB is only the plate's short tag.
	// Board slot rects as anchor fractions of the page window: plate i -> L,T,R,B (two plates
	// side by side: the shipped stage left, the practice yard right). Geometry drafted for
	// this card off the leader card's page.
	// STG_CONFIRM rides root-local fractions (the click tests CONFIRM FIRST so a near-miss
	// on the button never re-browses, same rule as the leader card)
	static readonly float[] STG_PAGE = { 0.05f, 0.14f, 0.63f, 0.92f };
	static readonly float[] STG_GRID = { 0.01f, 0.06f, 0.48f, 0.92f, 0.52f, 0.06f, 0.99f, 0.92f };
	// STG_CONFIRM rides root-local fractions; STG_PAGE/STG_GRID ride the page (see above)
	static readonly float[] STG_CONFIRM = { 0.30f, 0.90f, 0.60f, 0.97f };
	// the stage plate colour: forest moss for the shipped stage, cold slate for the yard,
	// a paler grey for a parked plate (greybox like every other PLACEHOLDER frame here)
	private Color StagePlate(int k)
	{
		if (STAGE_LOCKED[k])
		{ return new Color(0.10f, 0.09f, 0.12f, 1.0f); } // parked plate
		if (STAGE_TEST[k])
		{ return new Color(0.13f, 0.15f, 0.19f, 1.0f); } // practice-yard slate
		// forest moss: the shipped stage reads like the floor patches it plays on
		{ return new Color(0.12f, 0.19f, 0.13f, 1.0f); }
	}
	// the card comes up when the leader card closes: it is built at boot but PARKED HIDDEN
	// while the leader card owns the frame (CanvasItem visibility is inherited, so hiding the
	// root hides every plate, sheet and button), and one OpenStageCard call hands the frame
	// over. A launch with no leader card shows it at boot instead: BuildStageScreen's own
	// gate at the bottom of the build.
	private void OpenStageCard()
	{
		// the card may not exist at all: a seat that never asks M1_STAGE keeps it down and
		// plays stage 1, so the handoff has nothing to raise
		// the card comes up: the root was parked hidden under the leader card
		if (_stageHold && _stageRoot != null)
			_stageRoot.Visible = true;
	}
	public void BuildStageScreen()
	{
		_stageLayer = new CanvasLayer();
		_stageLayer.Layer = 10; // one rung above the leader card: only one card owns a frame
		// anyway (this one is parked hidden until the leader card is gone), so this only
		// decides who paints on the single handoff frame
		// THE CARD MUST BE ATTACHED: an orphan CanvasLayer draws nothing, and _stageHold
		// then froze the world behind an invisible card (human playtest 2026-11-05) - the
		// leader card attaches its layer the same way (AddChild(_selLayer))
		AddChild(_stageLayer);
		_stageRoot = new Control();
		SetRectSel(_stageRoot, 0.0f, 0.0f, 1.0f, 1.0f);
		_stageLayer.AddChild(_stageRoot);
		_stageBack = new ColorRect();
		_stageBack.Color = new Color(0.07f, 0.06f, 0.09f, 0.98f); // grimoire-dark board cover
		SetRectSel(_stageBack, 0.0f, 0.0f, 1.0f, 1.0f);
		_stageRoot.AddChild(_stageBack);
		_stagePage = new Control();
		SetRectSel(_stagePage, STG_PAGE[0], STG_PAGE[1], STG_PAGE[2], STG_PAGE[3]); // the ONE board window
		_stageRoot.AddChild(_stagePage);
		_stageFrame = new ColorRect();
		_stageFrame.Color = new Color(0.83f, 0.64f, 0.24f, 0.95f); // grimoire-gold RING
		_stagePage.AddChild(_stageFrame);
		_stageTile = new Control[STAGE_ID.Length];
		_stageName = new Label[STAGE_ID.Length];
		_stageBlurb = new Label[STAGE_ID.Length];
		// one plate per registry entry: the stage's own colour + name plate + identity line.
		// No art yet: the plates are greybox like every other PLACEHOLDER frame here.
		// (the leader card's painted minis are the art pass the user gathers)
		for (int k = 0; k < STAGE_ID.Length; k++)
		{
			ColorRect plate = new ColorRect();
			plate.Color = StagePlate(k);
			SetRectSel(plate, STG_GRID[k * 4], STG_GRID[k * 4 + 1], STG_GRID[k * 4 + 2], STG_GRID[k * 4 + 3]);
			_stageTile[k] = plate;
			_stageName[k] = new Label();
			SetRectSel(_stageName[k], 0.03f, 0.04f, 0.97f, 0.18f);
			_stageName[k].Text = STAGE_LABEL[k] + (STAGE_LOCKED[k] ? " (locked)" : "");
			_stageName[k].Set("horizontal_alignment", 1); // CENTER (string key: binding-safe)
			_stageTile[k].AddChild(_stageName[k]);
			_stageBlurb[k] = new Label();
			SetRectSel(_stageBlurb[k], 0.04f, 0.24f, 0.96f, 0.94f);
			_stageBlurb[k].Set("autowrap_mode", 2); // WORD wrap to the plate rect
			_stageBlurb[k].Text = STAGE_BLURB[k];
			_stageTile[k].AddChild(_stageBlurb[k]);
			_stagePage.AddChild(_stageTile[k]); // parent the plate: an orphan Control leaks at exit
		}
		// the sheet plate: the readout rides it (the leader card's sheet block, one slot
		// instead of the two the roster needs)
		ColorRect shp = new ColorRect();
		shp.Color = new Color(0.13f, 0.11f, 0.17f, 1.0f); // dark slate sheet plate
		SetRectSel(shp, 0.66f, 0.10f, 0.99f, 0.72f);
		_stageSheet = shp;
		_stageRoot.AddChild(_stageSheet);
		// the sheet text over its plate
		_stagePanel = new Label();
		SetRectSel(_stagePanel, 0.68f, 0.12f, 0.98f, 0.70f);
		_stagePanel.Set("autowrap_mode", 2); // WORD wrap to the sheet rect
		_stageRoot.AddChild(_stagePanel);
		// the CONFIRM plate at the card bottom (root-local anchors)
		ColorRect btn = new ColorRect();
		btn.Color = new Color(0.28f, 0.20f, 0.08f, 1.0f); // dark bronze CONFIRM plate
		SetRectSel(btn, STG_CONFIRM[0], STG_CONFIRM[1], STG_CONFIRM[2], STG_CONFIRM[3]);
		_stageConfirm = btn;
		_stageRoot.AddChild(_stageConfirm);
		// the button face: "CONFIRM" centered on the plate
		Label face = new Label();
		SetRectSel(face, STG_CONFIRM[0] + 0.01f, STG_CONFIRM[1] + 0.01f, STG_CONFIRM[2] - 0.01f, STG_CONFIRM[3] - 0.01f);
		face.Text = "CONFIRM"; // set locally at build: the field is a Control type
		_stageConfirmLabel = face;
		_stageRoot.AddChild(_stageConfirmLabel);
		// title strip: the click grammar the user locked (click a plate, then CONFIRM)
		_stageHint = new Label();
		_stageHint.Text = "STAGE SELECT - click a plate to browse it, then click CONFIRM to start";
		_stageRoot.AddChild(_stageHint);
		// humans open boxed on the shipped stage (a bench seat parks the card on nothing: the
		// FIRST press only browses, so an accidental key can never start a run)
		_stageBrowse = _stageDemo ? -1 : 0;
		// the sheet + ring open on the browsed plate
		ShowStageSheet(_stageBrowse);
		ShowStagePage();
		// the leader card owns the frame while it is up: park THIS card hidden until Close
		// Select hands over (a pilot bench that never opened the leader card shows it now)
		_stageRoot.Visible = !_selHold;
	}
	// one board, one page: the gold ring rides the browsed plate (its rect grown +/- 0.02
	// so the edge reads as a frame AROUND the plate); nothing browsed parks it hidden (the
	// leader card's page-parking rule: a bench seat opens on nothing).
	// all plates stand: the registry's plates always fit the one page
	private void ShowStagePage()
	{
		// nothing selected yet: the ring stays away
		for (int k = 0; k < _stageTile.Length; k++)
			_stageTile[k].Visible = true;
		if (_stageBrowse < 0 || _stageBrowse >= _stageTile.Length)
		{
			_stageFrame.Visible = false;
			return;
		}
		// the ring rides the browsed plate
		SetRectSel(_stageFrame, STG_GRID[_stageBrowse * 4] - 0.02f, STG_GRID[_stageBrowse * 4 + 1] - 0.02f,
				STG_GRID[_stageBrowse * 4 + 2] + 0.02f, STG_GRID[_stageBrowse * 4 + 3] + 0.02f);
		_stageFrame.Visible = true;
	}
	// the sheet for the BROWSED plate: composed off the LIVE data so the card cannot lie
	// about the run - the schedule census counts the real beat table, the recruit row reads
	// the real JOIN_W weights, the ring rate reads the faucet's own constant. The rotation
	// cadence is the one line of display copy that mirrors Director.cs' TEST_EVERY_LO/HI
	// (the Director is a separate node this class cannot read; the same switch boots both
	// the bench lane and the card lane, so the numbers cannot drift apart here).
	// (beat kinds as BeatLabel reads them: 0 ally | 1 horde | 2 mini | 3 mid | 4 final |
	// 5 wrap-up)
	private void ShowStageSheet(int k)
	{
		// nothing browsed: the sheet stays empty rather than guessing a stage
		if (_stagePanel == null || k < 0 || k >= STAGE_ID.Length)
			return;
		// beat census straight off the live table
		int allies = 0;
		int bosses = 0;
		for (int i = 0; i < BEAT_KIND.Length; i++)
		{
			if (BEAT_KIND[i] == 0)
				allies++;
			else if (BEAT_KIND[i] >= 2 && BEAT_KIND[i] <= 4)
				bosses++;
		}
		// how many roster seats CAN join this stage (weight 0 = cannot appear): row = registry
		// index, the same row BuildJoinLottery reads after the pick
		int joinable = 0;
		for (int w = 0; w < JOIN_W[k].Length; w++)
			if (JOIN_W[k][w] > 0)
				joinable++;
		// STAGE_NUMBER 0 marks a PSEUDO-stage: the yard must never read "stage 0"
		string txt = STAGE_LABEL[k] + (STAGE_NUMBER[k] > 0 ? " (stage " + STAGE_NUMBER[k] + ")" : " (test room)");
		// the plate's identity line (the registry's short tag)
		txt += "\n" + STAGE_BLURB[k];
		if (STAGE_TEST[k])
		{
			// the room: the whole table is held back, so say what the rotation lane actually is
			txt += "\nRotation: one body every 15-20 s, cycling the full stage-1 roster";
			// (the xp rate is the faucet's own constant, so the card and the tap agree)
			txt += "\nTraining ring: +" + (int)_testXpPerSec + " xp/sec while a member stands in it";
			// the death-save lane is identical everywhere: the room only trades the schedule
			txt += "\nDown / revive / TPK / gem drops run exactly as shipped";
		}
		else
		{
			// the shipped stage: the beat table drives it, so count the LIVE rows
			txt += "\nBeats: " + allies + " recruit joins | " + bosses + " boss rows | " + joinable + " roster seats can join";
			// the death-save lane is identical everywhere: the room only trades the schedule
			txt += "\nDown / revive / TPK / gem drops run exactly as shipped";
		}
		// a parked plate says so on the sheet as well as on the plate
		if (STAGE_LOCKED[k])
			txt += " | locked: the plate stays parked";
		_stagePanel.Text = txt;
	}
	// card polling: only carries the pilot clock rails: the bench auto-browse (M1_STAGE=<id>) one beat
	// after the card comes up and the auto-confirm one beat later, both printing their gate
	// lines verbatim. Humans hold here forever until StageClick browses or CONFIRM starts the
	// run (the picker's no-timeout rule, same as the leader card's human lane).
	private void PollStage(double d)
	{
		_stageWait += d; // the card clock feeds ONLY the pilot auto door below
		// bench auto door: resolve M1_STAGE=<id> to its registry entry (pilot clock)
		if (_pilot && _stageBrowse < 0 && _stageWait >= 1.2)
		{
			// the id, not the label: the env valve carries the registry key
			int wanted = -1;
			for (int k = 0; k < STAGE_ID.Length; k++)
				if (STAGE_ID[k] == _stageWant)
					wanted = k;
			if (wanted < 0)
			{
				GD.Print("[10thAge] (stage) bench asked an unknown stage: " + _stageWant + " - shipped stage stands");
				CloseStage(false); // drop the card: the shipped stage stands, lottery rolls
				return;
			}
			GD.Print("[10thAge] (stage) pilot auto-browse plate for: " + STAGE_LABEL[wanted]);
			_stageBrowse = wanted;
			ShowStagePage();
			ShowStageSheet(wanted);
			return; // auto-browse once; the rail below confirms on the NEXT tick
		}
		// pilot confirm rail: one beat after the auto-browse, start on the browsed plate
		if (_pilot && _stageBrowse >= 0 && _stageWait >= 2.4)
		{
			ShowStagePage();
			ShowStageSheet(_stageBrowse);
			GD.Print("[10thAge] (stage) pilot confirm on browsed: " + STAGE_LABEL[_stageBrowse]);
			CloseStage(true);
			return;
		}
		// human card: waits for a real click forever (no timeout)
		return;
	}
	// human rail: a click maps window px -> the card's anchor fractions (CONFIRM tested FIRST so
	// a near-miss on the button never re-browses). The card spans the measured window, so
	// fractions ride _visHalf (double the half-extents, same guard as the leader card); plates
	// convert into the PAGE rect's own anchors (STG_PAGE, the rect the build drew).
	// (headless gates have no pointer: only the pilot clock rails above walk the bench
	// stage -> confirm -> lottery wiring)
	private void StageClick(Vector2 pos)
	{
		float fw = _visHalf.X * 2.0f;
		float fh = _visHalf.Y * 2.0f;
		if (fw < 64.0f || fh < 64.0f)
			return; // window not measured yet: garbage fractions beat a bad stage
		float fx = pos.X / fw;
		float fy = pos.Y / fh;
		// CONFIRM plate first (root-local fractions)
		if (fx >= STG_CONFIRM[0] && fx <= STG_CONFIRM[2] && fy >= STG_CONFIRM[1] && fy <= STG_CONFIRM[3])
		{
			if (_stageBrowse >= 0)
			{
				GD.Print("[10thAge] (stage) confirm click: " + STAGE_LABEL[_stageBrowse]);
				CloseStage(true); // the shared door seats the browsed plate
			}
			else
			{
				// with nothing browsed the human has to pick a plate first: the CONFIRM line is a no-op
				// reminder here (unlike the leader card there is no default stage to fall back to - a
				// stage is a real choice, so the card stays up and waits). Humans actually open boxed on
				// plate 0, so this line only happens on a bench seat that parks the card on nothing.
				GD.Print("[10thAge] (stage) confirm click with nothing browsed: pick a plate first");
				return;
			}
			return;
		}
		// plates next (page-local fractions)
		float px = (fx - STG_PAGE[0]) / (STG_PAGE[2] - STG_PAGE[0]);
		float py = (fy - STG_PAGE[1]) / (STG_PAGE[3] - STG_PAGE[1]);
		for (int k = 0; k < _stageTile.Length; k++)
		{
			if (px < STG_GRID[k * 4] || px > STG_GRID[k * 4 + 2] ||
				py < STG_GRID[k * 4 + 1] || py > STG_GRID[k * 4 + 3])
				continue;
			if (STAGE_LOCKED[k])
			{
				GD.Print("[10thAge] (stage) click on a locked plate: " + STAGE_LABEL[k]);
				return; // the (locked) tags stay honest (a future stage parks a plate)
			}
			if (k == _stageBrowse)
			{
				GD.Print("[10thAge] (stage) re-click on the browsed plate: use CONFIRM to start");
				return; // browsing again is a no-op: CONFIRM is the stage door
			}
			// the room plate and the shipped stage share ONE door: CloseStage reads the browsed entry
			// and flips _testRoom when the registry says so
			_stageBrowse = k;
			ShowStagePage();
			ShowStageSheet(_stageBrowse);
			GD.Print("[10thAge] (stage) browse: " + STAGE_LABEL[_stageBrowse]);
			return;
		}
		GD.Print("[10thAge] (stage) click landed on bare board: nothing changes");
	}
	// the card closes: the PICKED stage is the run's stage, and the pieces that RIDE it (the
	// ring's floor read, the horde clock, the join lottery) all land AFTER the pick - ORDER
	// IS THE RULE: _stage lands before all three. The demoted-Leader / _hpOrder note from the
	// leader card still applies: a card-up run builds _hpOrder late, which the camp-card
	// fallback already survives.
	private void CloseStage(bool confirmed)
	{
		bool fromBench = _testRoom; // the boot valve already booted the room: do not reprint the line
		// a pick lands on its registry entry; nothing picked (the unknown-stage valve) keeps the
		// shipped stage, same default as a seat that never opens the card
		_stage = (_stageBrowse >= 0 && _stageBrowse < STAGE_ID.Length) ? _stageBrowse : 0;
		// the room plate lands on the SAME lane the M1_TEST_ROOM=1 bench valve boots
		_testRoom = _testRoom || STAGE_TEST[_stage];
		_stageHold = false; // the card is gone: the world starts on the next frame (the run clock never burned while
		// the card was up, so the beat table and the horde clock still read t=0)
		if (_stageLayer != null)
			_stageLayer.QueueFree(); // the whole card is gone in one cut: no RID/ODB leftovers at exit
		// C# discipline: a field holding a Godot node keeps it alive past the native free, so
		// every card field must go null or the textures stay "in use" at exit and Godot prints
		// leak warnings on the smoke gate
		_stageLayer = null;
		_stageRoot = null;
		_stageBack = null;
		_stagePage = null;
		_stageFrame = null;
		_stagePanel = null;
		_stageSheet = null;
		_stageHint = null;
		_stageConfirm = null;
		_stageConfirmLabel = null;
		for (int k = 0; k < _stageTile.Length; k++)
		{
			_stageTile[k] = null;
			_stageName[k] = null;
			_stageBlurb[k] = null;
		}
		// the stage lands BEFORE the pieces that read it, so this print is the gate anchor
		GD.Print("[10thAge] (stage) stage seated: " + StageBadge());
		// the room's faucet: guarded, so a launch that armed it at boot is a no-op here
		BuildTrainingRing();
		// the horde clock re-arms off the seated stage (the room disarms bonus swarms)
		ArmHordeClock();
		BuildJoinLottery();
		if (_testRoom && !fromBench)
		{
			// the room is now hand-picked from an ordinary launch: say what is live (the same lane the
			// bench valve boots, so a card-picked room matches the seat-805 evidence)
			GD.Print("[10thAge] (test) ENEMY TEST ROOM live: one body every 15-20 s cycling the stage-1 roster, beats held back, training ring +1 xp/sec at (0,0)");
			}
	}
	// the HORDE EVENT schedule arms for every launch now: the mini-boss-relative fallback arm
	// (HORDE_EVENT_FIRST_DELAY, the legacy lane) only covers legs that never armed one. Arming
	// sat inside the no-card branch before this pass, which left a HUMAN run on the legacy arm
	// - the first swarm landed at ~290 s instead of the 90 s the user asked for. Because
	// _testRoom rides the stage pick, CloseStage re-arms AFTER the pick; the boot call covers
	// a launch that never opens the card.
	private void ArmHordeClock()
	{
		_hordeEventDue = _testRoom ? 0.0 : HORDE_EVENT_FIRST_AT; // TEST ROOM: bonus swarms disarmed - the rotation is the ONLY body lane, so one kind is testable at a time
	}
	// the ring's floor read lives here so BOTH doors arm it: the M1_TEST_ROOM=1 bench valve at
	// boot, and CloseStage once a card pick lands on the room. Guarded so the two switches can
	// never build a second copy of the disc.
	private void BuildTrainingRing()
	{
		// guarded: the env valve at boot and the stage card both call this, so the disc exists
		// after the first one
		if (!_testRoom || _testRingDisc != null)
			return;
		_testRingDisc = new Polygon2D();
		_testRingDisc.Name = "TrainingRing";
		_testRingDisc.ZIndex = -12; // above the floor patches (-15), under every body
		_testRingDisc.Modulate = new Color(0.42f, 0.66f, 0.45f, 0.14f); // sage fill, faint so bodies stay legible
		_testRingDisc.Position = _testRingAt;
		Vector2[] disc = new Vector2[48]; // 48-gon: smooth enough at ring scale, zero Rng
		for (int ir = 0; ir < 48; ir++)
			disc[ir] = new Vector2((float)System.Math.Cos(System.Math.PI * 2.0 * ir / 48.0) * _testRingR, (float)System.Math.Sin(System.Math.PI * 2.0 * ir / 48.0) * _testRingR);
		_testRingDisc.Polygon = disc;
		AddChild(_testRingDisc);
		_testRingRim = new Line2D();
		_testRingRim.Name = "TrainingRingRim";
		_testRingRim.ZIndex = -11; // the edge reads over its own fill
		_testRingRim.Width = 3.0f;
		_testRingRim.Modulate = new Color(0.55f, 0.85f, 0.6f, 0.6f);
		_testRingRim.Position = _testRingAt;
		Vector2[] rim = new Vector2[49]; // closed by hand: the 49th point = the 1st
		for (int ir2 = 0; ir2 < 49; ir2++)
			rim[ir2] = new Vector2((float)System.Math.Cos(System.Math.PI * 2.0 * (ir2 % 48) / 48.0) * _testRingR, (float)System.Math.Sin(System.Math.PI * 2.0 * (ir2 % 48) / 48.0) * _testRingR);
		_testRingRim.Points = rim;
		AddChild(_testRingRim);
		_testRingTag = new Label();
		_testRingTag.Name = "TrainingRingTag";
		_testRingTag.ZIndex = -10; // over the fill + rim, still under the bodies (5)
		_testRingTag.Modulate = new Color(0.65f, 0.9f, 0.68f, 0.8f);
		_testRingTag.Text = "TRAINING RING - +1 xp/sec while you stand here";
		_testRingTag.Position = _testRingAt + new Vector2(-150.0f, -10.0f); // beside the center, clear of the rim
		AddChild(_testRingTag);
	}
	// one shared swap rule (bench valve + select door): promote seat, demote the old
	// Leader - it waits for its join beat like any other recruit - and carry the arena
	// spawn the squad opened on. Returns whether the seat actually moved.
	public bool TrySeatLeader(string want)
	{
		if (want == "" || _leader == null || want == _leader.displayName)
			return false;
		PartyMember seat = null;
		foreach (PartyMember pm2 in _party)
			if (pm2.displayName == want)
				seat = pm2;
		if (seat == null)
			return false;
		// the seat swap carries the arena spawn: the new Leader opens where the squad
		// opened, the demoted Leader is parked off-world until its own join beat
		Vector2 open = _leader.GlobalPosition;
		foreach (PartyMember pm2 in _party)
		{
			bool lead = (pm2 == seat);
			pm2.isLeader = lead;
			pm2.joined = lead;
			pm2.GlobalPosition = lead ? open : new Vector2(-9500.0f, -9500.0f);
		}
		_leader = seat;
		return true;
	}
	private void BuildJoinLottery()
	{
		int row = _stage; // row = registry index: the stage pick lands BEFORE this (see CloseStage / the boot gate)
		if (row < 0)
			row = 0;
		if (row >= JOIN_W.Length)
			row = JOIN_W.Length - 1;
		int slots = 0;
		for (int i = 0; i < BEAT_KIND.Length; i++)
			if (BEAT_KIND[i] == 0.0)
				slots++;
		_joinOrder = new int[slots];
		for (int i = 0; i < slots; i++)
			_joinOrder[i] = -1;
		_joinRng = new Godot.RandomNumberGenerator(); // unseeded on purpose
		int[] pool = new int[_party.Count];
		int poolCount = 0;
		foreach (PartyMember pm in _party)
		{
			// the leader obviously can't recruit itself (user rule); the table itself stays
			// leader-agnostic so any PC can show up when another one leads the run
			if (!pm.isLeader && pm.memberIndex < JOIN_W[row].Length)
				{ pool[poolCount++] = pm.memberIndex; }
		}
		for (int s = 0; s < slots && poolCount > 0; s++)
		{
			int total = 0;
			for (int i = 0; i < poolCount; i++)
				{ total += JOIN_W[row][pool[i]]; }
			{ if (total <= 0) break; } // everyone left cannot appear this stage
			{ double roll = _joinRng.Randf() * (double)total;
			 double acc = 0.0;
			 int chosen = -1;
			 for (int i = 0; i < poolCount; i++)
			 {
				 acc += (double)JOIN_W[row][pool[i]];
				 if (roll < acc)
				 { chosen = i; break; }
			 }
			{ if (chosen < 0) chosen = poolCount - 1; } // FP edge: take the last candidate
			 _joinOrder[s] = pool[chosen];
			 // pull the winner so no PC recruits twice in one run
			 for (int i = chosen; i < poolCount - 1; i++)
				 pool[i] = pool[i + 1];
			 poolCount--;
			}
		}
		string names = "";
		for (int s = 0; s < slots; s++)
		{
			if (_joinOrder[s] < 0)
				continue;
			PartyMember pm2 = GetMemberByIndex(_joinOrder[s]);
			names += (names == "" ? "" : " > ") + (pm2 != null ? pm2.displayName : "?");
		}
		GD.Print("[10thAge] join lottery for " + StageBadge() + ": " +
				 (names == "" ? "no recruits - the leader owns the whole roster" : names));
		// HUD strip order = JOIN order (user 2026-10-24): the leader owns the top strip and
		// each recruit appends the next one, so a Yu-first draw no longer paints Yu on its
		// fixed roster row with an empty gap above. Storage keeps roster order (memberIndex
		// is the ring slot); only the HUD strips read this map, built once because the
		// lottery already fixes the whole order at boot.
		_hpOrder = new Godot.Collections.Array<int>();
		foreach (PartyMember pm in _party)
		{
			if (pm.isLeader)
				_hpOrder.Add(pm.memberIndex); // the leader arrives at boot: strip 0
		}
		for (int s = 0; s < _joinOrder.Length; s++)
		{
			if (_joinOrder[s] < 0)
				{ continue; } // dry pool: nobody fills that strip (trailing slots stay invisible)
			_hpOrder.Add(_joinOrder[s]);
		}
	}

	// ---------- smart-random level-up roll (human-test: variety, not same-y cards) ----------
	// The CARD grows with the squad: one slot per member actually in play, in roster order,
	// each slot drawing that member's still-open tracks - THEN one fully-wild slot from
	// everything still open (user re-lock 2026-10-23: per-member options + 1 wildcard,
	// M1's wild slot back on demand). No option appears twice on one card. A member whose
	// whole pool is capped just drops its slot, so the card never shows a pick that cannot
	// be taken. If NOTHING is open the picker stays closed and the level-up banks the xp.
	// memberIndex order: 0 Baldrun | 1 Mosiah | 2 Asper | 3 Alvora | 4 Yu
	// (row 0 of JOIN_W above carries their stage-1 recruit weights; this card only sees
	// members already IN play).
	private bool RollOptions()
	{
		for (int s = 0; s < PICK_SLOTS; s++)
			_optRoll[s] = -1; // the card shrinks with the roster: clear the stale tail first
		bool[] open = new bool[OPT_LABEL.Length];
		int openCount = 0;
		for (int o = 0; o < OPT_LABEL.Length; o++)
		{
			// not-yet-recruited members never leak onto a card (their weapon is not
			// unlocked yet - recruiting IS the weapon unlock, user rule); pass-11 human
			// QOL: bodies OUT until the next campfire stop leaking too, so level-ups
			// feed the members that can still save the run
			// every track keeps the same rule: PICK_RANK_CAP ranks (user law 2026-11-23, seq 148412 follow-up:
			// "any characters we build follow the same formula of 3 skill ranks for all upgrades - this may be
			// reworked down the line, but for now this is law"). The claw rows 55-59 ride the same x/3 track as
			// everyone else; an earlier member-11 "off/on switches" carve-out was assistant-authored, never user
			// (zero user-role hits for "+20%" or "off/on" in the session store) and is now removed.
			open[o] = MemberInPlay(OPT_MEMBER[o]) && _pickRank[o] < PICK_RANK_CAP;
			if (open[o])
				openCount++;
		}
		if (openCount == 0)
			return false;
		// the members that still have a track to offer, in roster order
		int[] live = new int[PICK_SLOTS];
		int liveCount = 0;
		for (int mi = 0; mi < _party.Count && liveCount < PICK_SLOTS; mi++)
		{
			if (!MemberInPlay(mi))
				continue;
			bool any = false;
			for (int o = 0; o < OPT_LABEL.Length; o++)
				if (open[o] && OPT_MEMBER[o] == mi)
					any = true;
			if (any)
				live[liveCount++] = mi;
		}
		for (int slot = 0; slot < liveCount; slot++)
		{
			int wantMember = live[slot];
			int[] cand = new int[OPT_LABEL.Length];
			int c = 0;
			for (int o = 0; o < OPT_LABEL.Length; o++)
				if (open[o] && OPT_MEMBER[o] == wantMember)
					cand[c++] = o;
			if (c == 0)
				continue; // that member's whole track is capped: the slot simply drops
			int pick = cand[(int)(Rng.Randf() * (float)c) % c];
			_optRoll[slot] = pick;
			open[pick] = false; // a track never appears twice on one card (slots are distinct
								 // members now, so this only guards a hand-edited table)
		}
		// user re-lock (2026-10-23): +1 WILD slot - a random still-open track from ANY
		// member. The per-member slots already flipped open[pick]=false, so the
		// leftovers are exactly the wild pool; if the roster filled every key, the
		// wild drops out rather than pushing a 7th key onto the input map.
		{
		 int[] wild = new int[OPT_LABEL.Length];
		 int wc = 0;
		 for (int o = 0; o < OPT_LABEL.Length; o++)
			{ if (open[o]) wild[wc++] = o; }
		 if (wc > 0 && liveCount < PICK_SLOTS)
			 _optRoll[liveCount] = wild[(int)(Rng.Randf() * (float)wc) % wc];
		}
		for (int s = 0; s < PICK_SLOTS; s++)
			if (_optRoll[s] >= 0)
				return true;
		return false;
	}

	// never hand back a capped track: 3 ranks then the evolution, which is M2 content
	private int OpenOption(int seed)
	{
		int n = OPT_LABEL.Length;
		for (int k = 0; k < n; k++)
		{
			int o = ((seed + k) % n + n) % n;
			// every track keeps the same rank rule (no member-specific carve-out)
			if (_pickRank[o] < PICK_RANK_CAP
				&& OPT_MEMBER[o] < _party.Count && MemberInPlay(OPT_MEMBER[o]))
				return o;
		}
		for (int i = 0; i < n; i++)
		{
			if (OPT_MEMBER[i] < _party.Count && MemberInPlay(OPT_MEMBER[i]))
				return i;
		}
		return 0;
	}

	// is the roster PC with this memberIndex IN the squad AND still on the run?
	// (pass 11: eliminated-until-campfire bodies are filtered out here too, which is
	// what keeps dead members off every level-up and rest-gift card)
	private bool MemberInPlay(int idx)
	{
		foreach (PartyMember pm in _party)
			if (pm.memberIndex == idx)
				return pm.joined && !pm.eliminated;
		return false;
	}

	private void ApplyUpgrade(int option)
	{
		// no HP restoration on level-up: healing lives only in campfires and revives
		if (option < 0 || option >= OPT_LABEL.Length || OPT_MEMBER[option] >= _party.Count)
			option = 0;
		PartyMember m = GetMemberByIndex(OPT_MEMBER[option]);
		if (m == null)
			return;
		if (_pickRank[option] >= PICK_RANK_CAP)
		{
			// DESIGN: 3 ranks then an evolution = subclass choice. Stormcaller / Viperkiss
			// are M2 content, so a 4th rank stays capped and is logged, never silently eaten.
			GD.Print("[10thAge] upgrade: " + m.displayName + " " + OPT_LABEL[option] +
					 " capped at rank " + PICK_RANK_CAP + "/" + PICK_RANK_CAP +
					 " (evolution is M2)");
			return;
		}
		_pickRank[option]++;
		m.stat[StatTags.ATTACK_DAMAGE] *= OPT_DMG[option];
		m.stat[StatTags.ATTACK_SPEED] /= OPT_CD[option]; // cd x0.86 == speed /0.86; no-op when 1.0
		m.stat[StatTags.AREA] *= OPT_RANGE[option];
		m.pierce += OPT_PIERCE[option];
		m.stat[StatTags.MULTISTRIKE] += OPT_SHOTS[option]; // multistrike: one more bolt / familiar per rank
		{ m.guidingLight += 0.10 * OPT_VULN[option]; } // guiding light: marked bodies +10%/rank incoming
		{ m.maulSlow += OPT_SLOW[option]; } // draging maul: drag strength per rank onto the kit field
		{ m.stat[StatTags.CRIT_CHANCE] += OPT_CRIT[option]; } // deadly aim: crit points on the tag store
		{ m.burnDur += OPT_BURN[option]; } // linger flame: +2 s of burn per rank on the kit field
		{ m.cinderRank += OPT_CINDER[option]; } // cinder bloom rank: burst pct rides the firebolt's live roll at the shot call site
		{ m.shieldBounce += OPT_BOUNCE[option]; } // extra rebounds: +1 bounce per rank on the kit field
		{ m.stat[StatTags.CRIT_DAMAGE] += OPT_CRITD[option]; } // crushing blow: crit damage points on the tag store
		{ m.axeN += OPT_AXE[option]; } // extra axes: one more crescent (top/bottom alternating) per rank on the kit field
		{ m.axeSize += OPT_SIZE[option]; } // broader blade: one size rank per rank (disc + band read 1.0 + 0.25 * rank)
		{ m.bleedR += OPT_BLEED[option]; } // bleeding edge: hit bodies bleed 1 dmg/s for 4 s like the fire DoT
		{ m.poisonDur += OPT_VENOM[option]; } // lingering venom: +2 s of poison window per rank on the kit field
		{ m.multiChance += OPT_MS[option]; } // lucky strikes: +0.05/rank chance this hit deals damage twice
		{ m.fireDps += OPT_FIRE[option]; } // fire rune (Baldrun row 40): maul Burn dps per rank, kit field
		{ m.ringSlow += OPT_RING[option]; } // weighty ring (Alvora row 43): slow strength per rank, kit field
		{ m.luckR += OPT_LUCK[option]; } // lucky explosions (Yu row 44): double-blast chance per rank, kit field
		{ m.beamTargets += OPT_BEAM[option]; } // Split Beams (Keeko row 47): one more hitscan pick + its own lane per rank
		{ m.beamReach += OPT_REACH[option]; } // Arcane Reach (Keeko row 48): +0.10 reach factor per rank on the kit field (EffRange maths ride PartyMember)
		{ m.pillarTargets += OPT_PILLAR[option]; } // Multi-Flame (Hapi row 52): one more pillar drop per rank (cap 4 = base + 3 ranks)
		{ m.plagueR += OPT_PLAGUE[option]; } // Necrotic Plague (Hapi row 53): each flame tick rolls 10/20/30% to cough the curse onto a nearby body, and the applier's own stamp runs +2 s/rank longer
		{ m.pillarR += OPT_PEST[option]; } // Greater Pestilence (Hapi row 54): column radius x1.15/rank, PILLAR_R x (1 + 0.15 x rank) at the commit
		{ m.hasPrimal += OPT_CLAW[option]; } // Primal Might (Athios row 57): +1 claw attack PER RANK, so hasPrimal is a rank count 0..3 and the march adds (int)hasPrimal swings (user brief seq 129914: "Primal Might - +1 claw attack ... per level, as they have always been for all characters")
		{ m.bleedSlow += OPT_BLEEDSLOW[option]; } // Haemorrhaging Bleed (Athios row 58): +10%/rank slow stamped beside the Bleed (user brief seq 129914: "Bleed effect now also slows the enemy by 10%", per rank)
		{ m.barrelRank += OPT_BARREL[option]; } // Wider Barrel (Hew row 61) keeps RANK like every other track: the cannon band widens +16%/rank, reach stays PINNED at 520
		{ m.vampRank += OPT_VAMP[option]; } // Vampiric Core (Hew row 63) is a presence switch: Martha heals 1% of all party damage (Game.cs reads presence via GetMemberByIndex(12))
		{ m.exploRank += OPT_EXPLO[option]; } // Explosive Finale (Hew row 64) keeps rank: Hew/Martha kills blast 25/50/75% of the kill damage (Game.cs kill attribution)
		m.pickHistory += (m.pickHistory == "" ? "" : ", ") + m.weaponName + " " +
						 OPT_LABEL[option] + " x" + _pickRank[option];
		GD.Print("[10thAge] upgrade: " + m.displayName + " " + OPT_LABEL[option] +
				 " rank " + _pickRank[option] + "/" + PICK_RANK_CAP);
	}

	// ---------- rest beat: the ui/Campfire screen owns the choice, Game owns the rules ----------
	private void ShowCampfire()
	{
		if (_campfire == null)
		{
			Godot.PackedScene scn = GD.Load("res://ui/Campfire.tscn") as Godot.PackedScene;
			if (scn == null)
			{
				GD.Print("[10thAge] WARNING: res://ui/Campfire.tscn missing - falling back to the "
						 + (int)CAMPFIRE_WAIT_TIMEOUT + "s timeout rest");
				return;
			}
			_campfire = scn.Instantiate() as Godot.CanvasLayer;
			// party_data must land BEFORE AddChild: the screen builds its cards in _ready()
			_campfire.Set("party_data", CampfireCards());
			_campfire.Set("stage_name", StageName());
			AddChild(_campfire);
			// the instantiated scene root IS the "Campfire" node the signals live on
			_campfire.Connect("rest_chosen", new Callable(this, "_OnCampfireChosen"));
			_campfire.Connect("cancelled", new Callable(this, "_OnCampfireCancelled"));
		}
		else
			_campfire.Set("party_data", CampfireCards()); // re-offer after a cancel
		_campfire.Show();
		GD.Print("[10thAge] campfire: the rest card is up - short/long is the party's call");
	}

	private void EndCampfire(string restType)
	{
		if (_campfire != null)
			_campfire.Hide();
		_campfireWaiting = false;
		_campfireChoice = "";
		if (restType == "long")
		{
			// user-approved rest rebalance (2026-10-19): LONG = safety. Full revive + full
			// heal for the WHOLE squad - bodies the camp beat lifted now top off to full
			// hp too (the campReturned guard is gone). NO free upgrade rank anymore. The
			// x1.20 horde-scaling price stays.
			foreach (PartyMember m in _party)
				m.hp = (float)m.EffMaxHp();
			_hordeScale *= 1.20;
			GD.Print("[10thAge] LONG rest: whole squad revived to full hp, no free rank, horde "
					 + "scaling x" + (float)((int)(_hordeScale * 100.0)) / 100.0);
		}
		else
		{
			// user-approved rest rebalance (2026-10-19): SHORT = power. +25% max-HP heal
			// (clamped, applied once - the camp beat itself still does not heal) PLUS one
			// free upgrade rank, at the SAME x1.20 horde scaling as long. Risk-vs-reward:
			// long = full heal / no rank; short = less heal / free rank.
			ReviveAllAtCampfire(true);
			// user-approved: the free rank is now a CHOICE, not a dealt gift - the short
			// rest opens the same smart-random 3-of-6 card the level-up picker uses, so
			// "safe" (long = full heal) and "power" (short = rank) both land as decisions
			// the player actually reads. If every rank is capped there is nothing to pick
			// and the OpenOption fallback logs the swallowed gift instead of looping the
			// card forever.
			if (RollOptions())
			{
				_pickerActive = true;
				_pickerWait = 0.0;
				_pickerCard = "SHORT REST GIFT";
				_pickerCard = "SHORT REST GIFT";
				// the gift card grows the same way the level-up card does: one line per member
				// in play, read with the same NUMBER keys
				string gift = _pickerCard + " - pick ONE (combat is frozen):\n";
				for (int s = 0; s < PICK_SLOTS; s++)
				{
					if (_optRoll[s] < 0)
						continue;
					gift += "[" + (s + 1) + "] " + OptLabel(s) + "\n";
				}
				_pickerLabel.Text = gift;
				GD.Print("[10thAge] SHORT rest gift card: " + PickerLine());
			}
			else
				ApplyUpgrade(OpenOption(3)); // every rank capped: keep the gift logged, never silent
			_hordeScale *= 1.20;
			GD.Print("[10thAge] SHORT rest: +25% max HP heal, one free upgrade rank, horde "
					 + "scaling x" + (float)((int)(_hordeScale * 100.0)) / 100.0);
		}
		// user-approved camp benefit (2026-10-20): the campfire vacuums the map, like
		// VS's magnet - every xp gem still lying around is reeled into the Leader's
		// pickup ring. The existing UpdateGems magnet (110 px / 380 px/s) flies and
		// collects them, so the xp math + despawn path are untouched and no gem is
		// silently skipped on rest.
		if (_leader != null)
		{
			int reeled = 0;
			for (int i = 0; i < _gems.Count; i++)
			{
				if (_gems[i].GlobalPosition.X < -8000.0f)
					continue;
				double ang = Rng.Randf() * 6.2831853;
				float ring = 40.0f + (float)Rng.Randf() * 30.0f;
				_gems[i].GlobalPosition = _leader.GlobalPosition +
										  new Vector2((float)System.Math.Cos(ang) * ring,
													  (float)System.Math.Sin(ang) * ring);
				reeled++;
			}
			GD.Print("[10thAge] camp vacuum: " + reeled + " xp gem(s) reeled to the "
					 + "Leader's pickup ring");
		}
		foreach (PartyMember m in _party)
			m.campReturned = false;
		_state = StageState.PLAY; // one marching state again: the beat table owns the schedule
		_campWaitT = 0.0;
	}

	// ---------- field recruit (user-approved 2026-10-20) ----------
	// The drop: a not-yet-party PC lands RECRUIT_DIST px out with HELP! + an arrow from
	// the Leader. ESCORT_COUNT goblins open ESCORT_RING wide, hold still until the party
	// can SEE the rescue, then shamble in and close ON the PC: contact chips his HP, and
	// the join brings in whatever survives (floored) - dawdling costs a maimed recruit.
	// Clearing all of them (or the campfire sweep) joins the PC with a rank-0 upgrade
	// track (no catch-up gifts). The banner holds the "X JOINS THE BATTLE!" art slot.
	private void SpawnFieldRecruit(PartyMember m)
	{
		if (m == null || m.joined)
			return; // roster already whole: the beat is a no-op
		// human fix (2026-10-20): the beat drops OFF CAMERA. Landing 300 px out put the
		// PC and its escort inside the frame, so the party walked into a pop-in. Walk
		// outward until the site clears the visible rect. And the world WRAPS now (same
		// day, user bug: near an arena edge the goblin staging collapsed completely) -
		// with no edges staging cannot fail: land past the frame on any bearing and no
		// clamp can drag it back. reach stays <= ~1050 px, inside the 1200x900 fold box,
		// so the drop is never even folded by its own spawn.
		Vector2 cen = GetCameraCenter();
		Vector2 half = GetVisibleHalfExtents();
		double drop = Rng.Randf() * 6.2831853;
		Vector2 dir = new Vector2((float)System.Math.Cos(drop), (float)System.Math.Sin(drop));
		// distance along dir to the visible rect's border, then land past it
		double tx = System.Math.Abs(dir.X) > 0.001 ? half.X / System.Math.Abs(dir.X) : 1e30;
		double ty = System.Math.Abs(dir.Y) > 0.001 ? half.Y / System.Math.Abs(dir.Y) : 1e30;
		double reach = System.Math.Max(RECRUIT_DIST, System.Math.Min(tx, ty) + RECRUIT_MARGIN);
		m.GlobalPosition = cen + dir * (float)reach;
		GD.Print("[10thAge] field recruit: " + m.displayName + " stranded "
				 + (int)m.GlobalPosition.DistanceTo(cen) + "px out, off camera | escort "
				 + ESCORT_COUNT + " tier-1 goblins hold still until the party is in sight of the rescue");
		m.Visible = true;
		// HELP! floats over the stranded PC
		_helpLabel = new Godot.Label();
		// user 2026-11-06 (Bruno never appeared as a recruit): he DID - the join lottery draws him
		// ~34% of runs (57 of 167 bench benches name him in the stage-1 order), and a
		// human run simply died before its later ally beats. Nothing said WHO the drop
		// was offering, so a Bruno offer read as anonymous: name it on the label.
		_helpLabel.Text = "HELP! " + m.displayName;
		_helpLabel.GlobalPosition = m.GlobalPosition + new Vector2(-26.0f, -62.0f);
		_helpLabel.ZIndex = 12;
		AddChild(_helpLabel);
		// arrow rides over the Leader, aimed at the PC (updated per frame while pending)
		_recruitArrow = new Godot.Node2D();
		_recruitArrow.ZIndex = 10;
		AddChild(_recruitArrow);
		Godot.ColorRect shaft = new Godot.ColorRect();
		shaft.Size = new Vector2(26.0f, 4.0f);
		shaft.Position = new Vector2(26.0f, -2.0f);
		shaft.Color = new Color(0.95f, 0.85f, 0.35f, 0.9f);
		shaft.ZIndex = 10;
		_recruitArrow.AddChild(shaft);
		// escort cage: spawns wide (ESCORT_RING), crawls in (ESCORT_CRAWL), and the
		// hold point is the PC HIMSELF (pass 11): they walk onto him and chip him
		// on contact, no more honest-distance ring keeping the scene decorative
		_recruitPending = m;
		_escortTimeout = 0.0; // the give-up valve counts from the drop, not from run start
		_escortLeft = 0;
		// user 2026-11-03: recruitment escorts are TIER-1 goblins on every ally beat,
		// no matter how far into the run the beat fires. The rescue cage reads the same
		// at 30 s and at 270 s; the horde tier (_hordeTier) still drives the marching
		// Director lanes, not the escorts. They still scale with the squad's horde scale.
		// USER tier-1 numbers: 30 HP, speed 90, contact 2, XP 1.

		for (int i = 0; i < ESCORT_COUNT; i++)
		{
			Enemy e = new Enemy();
			e.kind = "goblin";
			e.maxHp = 30.0f * (float)GetHordeScale();
			e.hp = e.maxHp;
			e.speed = 90.0f;
			e.contactDamage = 2.0f;
			e.rangedDamage = 0.0f;
			e.xpValue = 1.0f;

			e.escort = true;
			double ea = 6.2831853 * i / ESCORT_COUNT + 0.3;
			e.escortHold = m.GlobalPosition;
			// human fix: they open wide of the stranded PC, so the ring closes in over
			// several seconds instead of materialising already on top of the rescue.
			// fold them too: recruit + 520 px can sit past the fold box, and the folded
			// side IS the short way around, which is exactly how the cage should read
			e.GlobalPosition = WrapNearCamera(m.GlobalPosition +
											  new Vector2((float)System.Math.Cos(ea),
														  (float)System.Math.Sin(ea)) * (float)ESCORT_RING);
			RegisterEnemy(e);
			AddChild(e);
			_escortLeft++;
		}
	}

	private void JoinRecruit()
	{
		PartyMember m = _recruitPending;
		_recruitPending = null;
		if (m == null)
			return;
		m.joined = true;
		// user rule (pass 11): the PC joins at the HP the player PRESERVED - escort
		// contact ate into him on the beat - floored so no rescue ever lands a corpse
		if (m.hp < m.EffMaxHp() * RECRUIT_JOIN_FLOOR)
			m.hp = (float)(m.EffMaxHp() * RECRUIT_JOIN_FLOOR);
		if (m.hp > m.EffMaxHp())
			m.hp = (float)m.EffMaxHp();
		GD.Print("[10thAge] join: " + m.displayName + " returns at " + (int)m.hp + "/" +
				 (int)m.EffMaxHp() + "hp | rank 0 upgrades start at zero");
		m.dsFails = 0;
		m.dsSucceeds = 0;
		m.dsDamageBuffer = 0.0f;
		m.downed = false;
		m.stable = false;
		if (_recruitArrow != null)
			_recruitArrow.Visible = false;
		if (_helpLabel != null)
			_helpLabel.Visible = false;
		// the beat is over: any escorts that survived the rescue (camp-sweep joins) stop
		// shuffling toward a hold point that no longer means anything in a wrapping
		// world and just become ordinary horde goblins
		for (int i = 0; i < _enemies.Count; i++)
		{
			if (_enemies[i] is Enemy en && en.escort)
				en.escort = false;
		}
		// join ceremony: the art slot ("X JOINS THE BATTLE!" posing with their weapon)
		// is a greybox banner + click gate for now. The human gate never times out
		// (same no-timer rule as the picker/campfire); the pilot clears it instantly.
		_joinWaiting = true;
		_joinBanner = new Godot.Label();
		_joinBanner.Position = new Vector2(430.0f, 300.0f);
		_joinBanner.Text = m.displayName + " JOINS THE BATTLE! (art slot: weapon pose)";
		_joinBanner.ZIndex = 14;
		_hudLayer.AddChild(_joinBanner);
		_joinBtn = new Godot.ColorRect();
		_joinBtn.Position = new Vector2(430.0f, 356.0f);
		_joinBtn.Size = new Vector2(300.0f, 44.0f);
		_joinBtn.Color = new Color(0.83f, 0.64f, 0.24f, 0.9f);
		_joinBtn.ZIndex = 14;
		_hudLayer.AddChild(_joinBtn);
		// the hint is a FIELD, not a local: ClearJoinBanner had no handle on a local, so
		// "CLICK TO CONTINUE" stayed painted over the HUD for the rest of the run (human
		// pass-9 report: a stale prompt next to a live picker read as a frozen game)
		_joinHint = new Godot.Label();
		_joinHint.Position = new Vector2(448.0f, 368.0f);
		_joinHint.Text = "CLICK TO CONTINUE";
		_joinHint.ZIndex = 15;
		_hudLayer.AddChild(_joinHint);
		GD.Print("[10thAge] RECRUIT JOINED: " + m.displayName + " at full hp, rank 0 | ring slot "
				 + m.memberIndex + " opens - escort cleared");
	}

	private void ClearJoinBanner(string why)
	{
		_joinWaiting = false;
		if (_joinBanner != null)
			_joinBanner.Visible = false;
		if (_joinBtn != null)
			_joinBtn.Visible = false;
		if (_joinHint != null)
			_joinHint.Visible = false; // human fix: the prompt leaves with the card
		GD.Print("[10thAge] join banner cleared (" + why + ") - the squad marches on");
	}

	// click gates share ONE _Input: the join banner (its wait window owns the mouse) and
	// the select card (_selHold soft pause: the tree is NOT paused, so _Input fires while
	// the card is up). MOUSE_BUTTON_LEFT == 1; presses only (releases carry Pressed=false)
	// so a click lands on the card/banner, never on the walk.
	public override void _Input(InputEvent ev)
	{
		// WHEEL PICKUP (user 2026-11-23 hand-play: "this area also needs to be scrollable, either
		// by click and drag or mouse wheel"): 4.7 DROPPED the Input.get_wheel_direction read and has
		// no wheel event class at all; viewport.cpp lands the wheel here as an InputEventMouseButton
		// whose ButtonIndex is MouseButton.WheelUp/WheelDown (press side only; releases carry
		// Pressed=false). PollSelect consumes _selWheel on the next frame, and the beat clock turns
		// one flick into one ring notch.
		if (ev is Godot.InputEventMouseButton mw && mw.Pressed &&
			(mw.ButtonIndex == Godot.MouseButton.WheelUp || mw.ButtonIndex == Godot.MouseButton.WheelDown))
			_selWheel = (mw.ButtonIndex == Godot.MouseButton.WheelUp) ? 1 : -1;
		if (ev is Godot.InputEventMouseButton mb && mb.ButtonIndex == Godot.MouseButton.Left
			&& mb.Pressed)
		{
			if (_selHold)
			{
				SelectClick(mb.Position); // the card owns the mouse: fractions decide
				return;
			}
			if (_stageHold)
			{
				StageClick(mb.Position); // the stage card owns the mouse the same way (parked hidden under the leader card until CloseSelect hands over)
				return;
			}
			if (_joinWaiting)
				ClearJoinBanner("click");
		}
	}

	private void _OnCampfireChosen(string restType)
	{
		EndCampfire(restType);
	}

	private void _OnCampfireCancelled()
	{
		// the player backed out of the embers: keep the beat, put the card back up
		_campfireWaiting = false;
		GD.Print("[10thAge] campfire declined - the rest card comes back up");
	}

	// ---------- run end: the ui/RunSummary card is the retry loop (journal is M4) ----------
	private void PrintSummary(string s)
	{
		if (_summaryPrinted)
			return;
		_summaryPrinted = true;
		// GD.Print stays alongside the screen so a headless smoke run still logs the beat
		GD.Print("[10thAge] " + s + " | level " + _level + " | xp total " + (int)_xpTotal);
		ShowRunSummary(s);
	}

	private void ShowRunSummary(string s)
	{
		if (_summary == null)
		{
			Godot.PackedScene scn = GD.Load("res://ui/RunSummary.tscn") as Godot.PackedScene;
			if (scn == null)
			{
				GD.Print("[10thAge] WARNING: res://ui/RunSummary.tscn missing - summary stays print-only");
				return;
			}
			_summary = scn.Instantiate() as Godot.CanvasLayer;
			_summary.Set("result", s.Contains("VICTORY") ? "victory" : "tpk");
			_summary.Set("stage_name", StageName());
			_summary.Set("level", _level);
			_summary.Set("xp_total", (int)_xpTotal);
			_summary.Set("members", SummaryCards());
			AddChild(_summary); // its _ready() pauses the tree and builds the rows
			_summary.Connect("dismissed", new Callable(this, "_OnSummaryDismissed"));
		}
		_summary.Show();
		// the card pauses the tree, but M1 un-pauses right away: DEAD/VICTORY already
		// freezes the sim through the stage early-return, and the retry key needs input
		// frames that a paused tree would never give us.
		GetTree().Paused = false;
		GD.Print("[10thAge] run summary up - press R to retry the stage (journal/menu are M4)");
	}

	private void _RetryRun()
	{
		if (_summaryPrinted && !_summaryRetried)
		{
			_summaryRetried = true;
			if (_summary != null)
				_summary.Hide();
			GetTree().Paused = false;
			GD.Print("[10thAge] retry: reloading res://Main.tscn");
			// the engine's own restart path: the main loop relaunches with this scene
			GetTree().ChangeSceneToFile("res://Main.tscn");
		}
	}

	private void _OnSummaryDismissed()
	{
		// M1 decision (flag for the designer): dismissing without R just closes the card;
		// the run stays over. Title screen / campaign journal routing is M4 content.
		if (_summary != null)
			_summary.Hide();
		GD.Print("[10thAge] summary dismissed - the run stays over (press R to retry the stage)");
	}

	// user bug 2026-10-24: joined-only rows showed just the leader + first recruit, so the camp
	// listed 2 of 5 PCs. The camp menu now lists the WHOLE roster, top-to-bottom in HUD join
	// order (_hpOrder); an unrecruited PC reads "away", and "picks" is the upgrade line.
	// _hpOrder (leader first, then join order) is the same map the HUD strips use.
	private Godot.Collections.Array CampfireCards()
	{
		Godot.Collections.Array rows = new();
		if (_hpOrder.Count > 0)
		{
			// dry-pool safety: a roster entry the join lottery never placed is tail-appended.
			for (int i = 0; i < _hpOrder.Count; i++)
			{
				PartyMember m = GetMemberByIndex(_hpOrder[i]);
				if (m != null)
					rows.Add(CampCard(m));
			}
			foreach (PartyMember m in _party)
			{
				bool shown = false;
				for (int i = 0; i < _hpOrder.Count; i++)
				{
					if (_hpOrder[i] == m.memberIndex)
						shown = true;
				}
				if (!shown)
					rows.Add(CampCard(m));
			}
			return rows;
		}
		// boot fallback: _hpOrder stays empty until BuildJoinLottery runs.
		foreach (PartyMember m in _party)
		{
			rows.Add(CampCard(m));
		}
		return rows;
	}

	private Godot.Collections.Dictionary CampCard(PartyMember m)
	{
		Godot.Collections.Dictionary card = new();
		card["name"] = m.displayName;
		card["klass"] = m.class_ == "" ? m.weaponName : m.class_;
		card["weapon"] = m.weaponName;
		card["hp"] = (int)m.hp;
		card["max_hp"] = (int)m.EffMaxHp();
		card["state"] = !m.joined ? "away" : (m.eliminated ? "out" : (m.stable ? "stable" : (m.downed ? "down" : "up")));
		card["leader"] = m.isLeader;
		card["picks"] = m.pickHistory == "" ? "no picks yet" : m.pickHistory;
		return card;
	}

	// ---------- pause card: roster read + per-member targeting toggle ----------
	// The card owns ESC-close (dismissed signal); Game owns open, the pilot valve
	// and the Hide + unpause. Rows reuse the camp-card shape plus idx + target_mode
	// + art so the card can paint minis and the mode toggle.
	private Godot.Collections.Array PauseCards()
	{
		Godot.Collections.Array rows = new();
		if (_hpOrder.Count > 0)
		{
			// HUD join order, recruited only - the pause card shows who is IN the party
			for (int i = 0; i < _hpOrder.Count; i++)
			{
				PartyMember m = GetMemberByIndex(_hpOrder[i]);
				if (m != null && m.joined)
					rows.Add(PauseCard(m));
			}
			foreach (PartyMember m in _party)
			{
				bool shown = false;
				for (int i = 0; i < _hpOrder.Count; i++)
					if (_hpOrder[i] == m.memberIndex)
						shown = true;
				if (!shown && m.joined)
					rows.Add(PauseCard(m));
			}
			return rows;
		}
		// boot fallback: _hpOrder stays empty until BuildJoinLottery runs.
		foreach (PartyMember m in _party)
		{
			if (m.joined)
				rows.Add(PauseCard(m));
		}
		return rows;
	}

	private Godot.Collections.Dictionary PauseCard(PartyMember m)
	{
		Godot.Collections.Dictionary card = new();
		card["name"] = m.displayName;
		card["klass"] = m.class_ == "" ? m.weaponName : m.class_;
		card["weapon"] = m.weaponName;
		card["hp"] = (int)m.hp;
		card["max_hp"] = (int)m.EffMaxHp();
		card["state"] = !m.joined ? "away" : (m.eliminated ? "out" : (m.stable ? "stable" : (m.downed ? "down" : "up")));
		card["leader"] = m.isLeader;
		card["picks"] = m.pickHistory == "" ? "no picks yet" : m.pickHistory;
		card["idx"] = m.memberIndex;
		card["upgrade_block"] = UpgradeBlock(m); // user 2026-11-06 playtest: ONE condensed line per track, compounded
		card["target_mode"] = m.targetMode;
		// the card's mini: art is res://art/characters/<lowercased name>.png (2026-11-08 art
		// reorg); the card skips it if the load misses, so a kit without a mini still shows its column
		card["art"] = "res://art/characters/" + m.displayName.ToLower() + ".png";
		return card;
	}

	// user 2026-11-06 playtest: the pause card's upgrade row WRAPPED and its right edge got cut
	// off. The pause card now sends ONE condensed line per taken track with the COMPOUNDED total
	// at the member's current rank - e.g. "Pulsing Guardians (+57% Attack Speed) (3/3)" - instead
	// of the raw comma chain, and the weapon prefix is dropped (the row already lives under the
	// member's own column). Numbers recompute from the SAME stat tables the picks ride so the
	// card cannot lie about the maths: Attack Speed / Damage / Range / Area compound by rank
	// POWER (they are multipliers), Slow / crit chance / crit damage ride per-rank POINTS so they
	// scale linearly, and "+N" tracks (pierce, projectiles, familiars, bounces, seconds of
	// duration, size) scale N by rank. Blurbs without a leading +/-% stay exactly as written.
	private string UpgradeBlock(PartyMember m)
	{
			string joined = "";
			for (int o = 0; o < OPT_LABEL.Length; o++)
			{
				// one line per track this PC has taken at least one rank of, in table order
					if (OPT_MEMBER[o] == m.memberIndex && _pickRank[o] > 0)
					{
							joined = joined + (joined == "" ? "" : "\n") + CondensePick(OPT_LABEL[o], o, _pickRank[o]);
					}
			}
			// a PC with nothing taken reads the same as the camp card's line
			return joined == "" ? "no picks yet" : joined;
	}

	// the label shape is "Name (+16% Range, Pierce +1)": split at the FIRST "(", compound each
	// comma-separated segment, then stamp "(rank/cap)" so the cap is visible on the card.
	private string CondensePick(string label, int o, int rank)
	{
			int open = label.IndexOf("(");
			int close = label.LastIndexOf(")");
			if (open < 0 || close <= open)
				return label + " (" + rank + "/" + PICK_RANK_CAP + ")"; // blurb-only track: kept as written
			// the label joins segments with ", "; Split on the comma and Trim each piece
			string[] segs = label.Substring(open + 1, close - open - 1).Split(',');
			string kept = label.Substring(0, open).Trim(); // the track NAME: no weapon prefix on the card
			for (int s = 0; s < segs.Length; s++)
			{
					kept = kept + (s == 0 ? " (" : ", ") + CompoundSeg(segs[s].Trim(), o, rank);
			}
			return kept + " (" + rank + "/" + PICK_RANK_CAP + ")";
	}

	// compound one "+X% Stat" segment from the tables: Pct re-emits the label's own word suffix
	// ("% Range", "s Burning Duration"...) with the compounded total percent.
	private string CompoundSeg(string seg, int o, int rank)
	{
			// static blurbs ("Burning enemies explode for 25%...") carry no leading sign: kept honest
			if (!(seg.StartsWith("+") || seg.StartsWith("-")))
				return seg;
			// Attack Speed: the table stores the COOLDOWN multiplier, so the speed factor is 1/cd per rank
			if (seg.Contains("Attack Speed") && OPT_CD[o] != 1.0f)
				return Pct(System.Math.Pow(1.0 / (double)OPT_CD[o], rank), seg);
			// flat damage compounds; the Burn/Bleed "Damage/sec" DoT tracks are per-rank additions at
			// the call site, so the guard keeps them linear instead
			if (seg.Contains("Damage") && !seg.Contains("Damage/sec") && OPT_DMG[o] != 1.0f)
				return Pct(System.Math.Pow((double)OPT_DMG[o], rank), seg);
			if ((seg.Contains("Range") || seg.Contains("Area")) && OPT_RANGE[o] != 1.0f)
				return Pct(System.Math.Pow((double)OPT_RANGE[o], rank), seg);
			// slow strength, crit chance and crit damage are per-rank POINTS: linear rank-times
			if (seg.Contains("Slow") && OPT_SLOW[o] > 0.0)
				return Pct(1.0 + OPT_SLOW[o] * (double)rank, seg);
			// crit CHANCE before crit damage: the flat-damage lane above only fires when OPT_DMG is set
			if (seg.Contains("Crit Chance") && OPT_CRIT[o] > 0.0)
				return Pct(1.0 + OPT_CRIT[o] * (double)rank, seg);
			if (seg.Contains("Crit Damage") && OPT_CRITD[o] > 0.0)
				return Pct(1.0 + OPT_CRITD[o] * (double)rank, seg);
			// "+N" tracks (pierce, projectiles, familiars, bounces, duration seconds, size percent,
			// multistrike points): the tables are per-rank constants, so N scales by rank
			return ScaleLabel(seg, rank);
	}

	// re-emit the segment with the COMPOUNDED factor as a total percent, keeping the sign the
	// compound result actually has and the label's word suffix untouched.
	private string Pct(double factor, string seg)
	{
			// skip the sign, then walk the digit run: everything from there on is the suffix
			int p = 0;
			if (seg.StartsWith("+") || seg.StartsWith("-"))
				p = 1;
			int d0 = p; // where the number starts: proves a digit run exists
			while (p < seg.Length && (int)seg[p] >= 48 && (int)seg[p] <= 57)
			{
					p = p + 1;
			}
			// no digits after the sign: nothing to compound, keep the segment honest
			if (p <= d0 || p >= seg.Length)
				return seg;
			string suffix = seg.Substring(p); // "% Range", "s Burning Duration", "% Slow for 2.5s"...
			int whole = (int)System.Math.Round((factor - 1.0) * 100.0);
			// the sign rides the COMPOUNDED result, not the label
			string sign = whole >= 0 ? "+" : "-";
			int mag = whole >= 0 ? whole : -whole;
			// concat order keeps the LEFT side a string first
			return sign + "" + mag + suffix;
	}

	// "+N word" tracks: the tables are per-rank constants, so the label's number scales by rank
	// and the rest of the label (unit + words) rides verbatim.
	private string ScaleLabel(string seg, int rank)
	{
			int p = 0;
			if (seg.StartsWith("+") || seg.StartsWith("-"))
				p = 1;
			int q = p;
			while (q < seg.Length && (int)seg[q] >= 48 && (int)seg[q] <= 57)
			{
					q = q + 1;
				// the digit run only: ASCII 48..57 are '0'..'9'
			}
			// no number in the segment: keep it exactly as written
			if (q <= p)
				return seg;
			int num = 0;
			for (int i = p; i < q; i = i + 1)
			{
					num = num * 10 + ((int)seg[i] - 48);
			}
			string head = seg.Substring(0, p); // the sign
			string tail = seg.Substring(q); // unit + words
			return head + "" + (num * rank) + tail;
	}

	private void ShowPauseCard()
	{
		if (_pauseCard == null)
		{
			Godot.PackedScene scn = GD.Load("res://ui/PauseMenu.tscn") as Godot.PackedScene;
			if (scn == null)
			{
				GD.Print("[10thAge] WARNING: res://ui/PauseMenu.tscn missing - pause is a no-op");
				return;
			}
			_pauseCard = scn.Instantiate() as Godot.CanvasLayer;
			// Set lands BEFORE AddChild: the card builds its columns in _ready()
			_pauseCard.Set("members", PauseCards());
			_pauseCard.Set("stage_name", StageName());
			AddChild(_pauseCard);
			// the instantiated scene root IS the "PauseMenu" node the signals live on
			_pauseCard.Connect("target_changed", new Callable(this, "_OnPauseTarget"));
			_pauseCard.Connect("dismissed", new Callable(this, "_OnPauseDismissed"));
		}
		else
			_pauseCard.Set("members", PauseCards()); // re-pull roster data on a re-open
		_pauseCard.Show();
		// NESTED FIELD VISIBILITY LAW: the wash + blur ride a CHILD CanvasLayer (PauseField, layer 1)
		// and a CanvasLayer's Show()/Hide() does NOT cascade into a nested CanvasLayer (stacking is
		// ABSOLUTE against the viewport, not additive with the parent - same law as the pause-blur
		// layer split). The card furniture (header/hint/grid, direct children of the root) hides fine,
		// but the blurred field stays burnt into the background unless this node is toggled DIRECTLY.
		// index 0 = PauseField: _build() adds it FIRST, header/hint/grid follow. The matching Hide
		// lives in _OnPauseDismissed; this Show re-arms it on every re-open.
		if (_pauseCard.GetChildCount() > 0)
			((Godot.CanvasLayer)_pauseCard.GetChild(0)).Show(); // re-show the soft field on every open
		// THE PAUSE ARM (user 2026-11-14 hand-play: "The pause menu also doesn't pause. It blurs,
		// but the game stays running in the background"): the card used to only PAINT. Nothing ever
		// set the tree pause: Campfire.gd (get_tree().paused = true) and RunSummary.gd do it, PauseMenu.gd
		// never did, and this file only ever CLEARED it - every comment here promises a TRUE tree
		// pause and none was armed, so the sim's soft-pause guards (GetWorldPause(), which never
		// mentions the pause card) had nothing to see and every _process rail kept ticking behind
		// the blur. ARM IT HERE next to the Show: Godot owns the freeze, _OnPauseDismissed already
		// clears it, so the pair is the whole contract (set_pause on the same value is a no-op, so
		// a re-open or the engine's own ESC handling can never fight this). Game._process is frozen
		// while the card is up, which is why the pilot CLOSE valve lives on the card.
		GetTree().Paused = true;
		_pauseRunT = _runT; // the freeze proof: the dismissed readout prints how far the clock moved
		// the pilot CLOSE valve is on the CARD (pilot_timeout export): its PROCESS_MODE_ALWAYS
		// _process ticks during this freeze while Game._process does not (seat 545 froze at
		// beat 1 with the valve here); 0.0 on a human seat = the card only closes on ESC
		_pauseCard.Set("pilot_timeout", _pilot ? PAUSE_PILOT_TIMEOUT : 0.0);
		_pauseWaitT = 0.0; // retired C# clock: kept so the field stays compile-clean
		GD.Print("[10thAge] pause: roster card up - ESC/P closes it");
		GD.Print("[10thAge] pause readout: tree armed, run clock parked at " + ((int)(_pauseRunT * 10.0) / 10.0));
	}

	private void _OnPauseTarget(int idx, int mode)
	{
		// one column's toggle press: route the mode to that member's shared picker
		PartyMember m = GetMemberByIndex(idx);
		if (m == null)
		{
			GD.Print("[10thAge] WARNING: pause target for unknown member " + idx);
			return;
		}
		m.SetTargetMode(mode);
	}

	private void _OnPauseDismissed()
	{
		if (_pauseCard != null)
		{
			_pauseCard.Hide();
			// NESTED FIELD VISIBILITY LAW (see the matching Show in ShowPauseCard): the wash + blur
			// ride the CHILD CanvasLayer PauseField (index 0, layer 1); the root Hide() does NOT reach
			// a nested CanvasLayer, so the soft field stays burnt in until this node hides it directly.
			if (_pauseCard.GetChildCount() > 0)
				((Godot.CanvasLayer)_pauseCard.GetChild(0)).Hide(); // the arena snaps back crisp the moment the card dismisses
		}
		GetTree().Paused = false;
		_pauseJustClosed = true; // the card ate the SAME ESC press: eat one frame, no reopen
		GD.Print("[10thAge] pause: card dismissed - the beat resumes");
		// the seat proof: a held pause reads +0.0. A leaking one prints the card's hold time
		// instead, which is what the user saw as "the game stays running in the background".
		GD.Print("[10thAge] pause readout: run clock held +" + ((int)(System.Math.Abs(_runT - _pauseRunT) * 10.0) / 10.0) + " over the card");
	}

	private Godot.Collections.Array SummaryCards()
	{
		Godot.Collections.Array rows = new();
		foreach (PartyMember m in _party)
		{
			if (!m.joined)
				continue; // never joined: not a party member to report on
			Godot.Collections.Dictionary card = new();
			card["name"] = m.displayName;
			card["state"] = m.eliminated ? "fallen"
							: (m.downed ? (m.stable ? "stable" : "downed") : "survived");
			card["picks"] = m.pickHistory == "" ? "no picks yet" : m.pickHistory;
			rows.Add(card);
		}
		return rows;
	}

	private void CheckTpk()
	{
		// TPK, two ways (user rule 2026-10-22 closes the stall): every JOINED member ELIMINATED (3
		// failed saves), OR nobody can act at all - every joined member is OUT or DOWN-STABLE
		// (death-save rolls stopped, so the dice cannot save the run either). The old test missed
		// a stable-down body no enemy ever touches: with a live boss holding the next beat the
		// stage could reach neither VICTORY nor TPK (first seen stalling 500+ s in smokeM2w).
		// Reviving a downed leader from an ally is future work; until then an up body (or a body
		// still rolling saves) is the only thing that keeps a run alive.
		foreach (PartyMember m in _party)
		{
			if (!m.joined)
				continue; // a PC that never joined is not part of the party - it cannot
						  // hold off a TPK (or rescue a stranded one)
			if (m.eliminated)
				continue; // out until the next campfire: cannot act, cannot save
			if (!m.downed)
				return; // someone is up: they fight, and they can revive
			if (!m.stable)
				return; // still rolling death saves: the dice can keep the run going
		}
		_state = StageState.DEAD;
		PrintSummary("TPK - the whole party is down");
	}


	// ENEMY TEST ROOM door: Director.cs's rotation lane consumes this. Ordinary launches never set
	// M1_TEST_ROOM, so the budget curve / tier gates / escort hordes / packs run untouched.
	public bool GetTestRoom()
	{
		return _testRoom;
	}

	// how many JOINED members stand inside the training ring right now. Wrap-aware like every
	// other distance test in the looping world, so walking out past the fold pauses the faucet
	// instead of silently re-entering it on the far side.
	private int TestRingStood()
	{
		int n = 0;
		foreach (PartyMember pm in _party)
		{
			if (!pm.joined || pm.eliminated)
				continue; // a PC that never joined (or is out until camp) cannot stand in the ring
			if (WrapDelta(pm.GlobalPosition, _testRingAt).LengthSquared() <= _testRingR * _testRingR)
				n++;
		}
		return n;
	}

	private static string StateName(StageState s)
	{
		switch (s)
		{
			case StageState.PLAY: return "march";
			case StageState.CAMPFIRE: return "CAMPFIRE";
			// the beat a death leads to is the row's own kind, so the label only ever has
			// to name the four states the collapse left behind
			case StageState.DEAD: return "dead";
			case StageState.VICTORY: return "stage cleared";
			default: return "cleared";
		}
	}
}
