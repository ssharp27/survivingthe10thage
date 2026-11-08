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
	// (0 Baldrun | 1 Mosiah | 2 Asper | 3 Alvora | 4 Yu | 5 Stack | 6 Wash | 7 Bruno | 8 Brook).
	// Weight 0 = cannot appear on that stage. Stage 1 per the user: Baldrun + Mosiah more likely
	// than the rest. The Enemy Test Room row is ALL ZERO on purpose: the room runs no ally beats,
	// so the exhausted pool is a logged no-op and nothing can recruit there.
	// Sanity numbers - the user will add rows/weights per future stage.
	// the new-style initializer: Godot's C# build trips CS0623 on nested old-style braces
	private static readonly int[][] JOIN_W = { new int[] { 3, 3, 1, 1, 1, 1, 1, 1, 1 }, new int[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 } };   // row 0 = stage 1: Baldrun + Mosiah heavy, Wash + Bruno + Brook parity with the rest | row 1 = the test room: no recruits
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
	const int PICK_SLOTS = 10;   // keys pick_1..pick_10: nine members + one wild slot (pick_10 entered the input map 2026-10-31 with Brook)
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
											   "Echoing Explosions (+5% Multistrike Chance)" };
	static readonly int[] OPT_MEMBER = { 0, 0, 0, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 0, 1, 5, 5, 5, 5, 5, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 8, 8, 8, 8, 8, 0, 1, 2, 3, 4 };
	static readonly float[] OPT_CD = { 1.0f, 1.0f, 0.86f, 0.86f, 1.0f, 1.0f, 1.0f, 0.85f, 1.0f, 1.0f, 1.0f, 1.0f, 0.85f, 1.0f, 1.0f, 1.0f, 0.85f, 1.0f, 1.0f, 1.0f, 1.0f, 0.85f, 1.0f, 1.0f, 1.0f, 1.0f, 0.85f, 1.0f, 1.0f, 1.0f, 1.0f, 0.85f, 1.0f, 1.0f, 1.0f, 1.0f, 0.85f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f };
	static readonly float[] OPT_RANGE = { 1.15f, 1.0f, 1.0f, 1.0f, 1.16f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.16f, 1.0f, 1.0f, 1.0f, 1.16f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f };
	// user 2026-11-01 audit fix: Stack rows 20/21 were swapped - keener flame carried no bonus at all and quicker fuse carried the damage. damage 1.10 rides row 20, cooldown 0.85 rides row 21.
	static readonly float[] OPT_DMG = { 1.0f, 1.10f, 1.0f, 1.0f, 1.0f, 1.10f, 1.12f, 1.0f, 1.0f, 1.0f, 1.12f, 1.0f, 1.0f, 1.0f, 1.12f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.10f, 1.0f, 1.0f, 1.0f, 1.0f, 1.10f, 1.0f, 1.0f, 1.0f, 1.0f, 1.10f, 1.0f, 1.0f, 1.0f, 1.0f, 1.10f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f };
	static readonly int[] OPT_PIERCE = { 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }; // rows 4 + 8: Mosiah deeper thrust / Asper piercing shot (user 2026-10-30)
	static readonly int[] OPT_SHOTS = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }; // multishot rank: for Yu it is the MULTIATTACK rank - one more familiar per ritual
	// guiding light (Alvora): the ring MARKS caught bodies for +10%/rank incoming damage
	// from EVERY source, a window on the body that decays on its own (Combatant.TickVuln).
	// The user swapped the old flat-armour defence for this offense (2026-10-23): the squad
	// keeps no mitigation, but the cleric's ring becomes the party's damage setup.
	static readonly int[] OPT_VULN = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
	// draging maul (Baldrun 4th card, user 2026-10-24): connected maul hits drag caught bodies to
	// (1 - 0.25/rank) speed for a 2.5 s window (Combatant.slowMul/slowT, refresh-not-stack); only
	// horde movement reads it and the cleave hook never marks Bosses. deadly aim (Mosiah 4th card):
	// +8%/rank CRIT_CHANCE points on the every-PC 5% seed - RollDamage already rolls that slot,
	// so the crit card is pure table data. Rows 18/19 of the 20-wide tables; caps bound both:
	// drag <= 0.75 at rank 3, crit <= 0.05 + 3 x 0.08 = 0.29.
	// OPT_SLOW = drag strength/rank (Baldrun row 18); OPT_CRIT = crit points/rank (Mosiah row 19).
	static readonly double[] OPT_SLOW = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.25, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
	static readonly double[] OPT_CRIT = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.08, 0, 0, 0, 0, 0, 0, 0, 0, 0.08, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.08, 0, 0, 0, 0.08, 0, 0 };
	// firebolt kit cards (Stack, user 2026-10-30): OPT_BURN = +2 s of linger per rank onto
	// PartyMember.burnDur; OPT_CINDER = cinder bloom rank: a body that dies STILL burning bursts
	// in CINDER_R px, scorching the live horde bodies in the blast for 25/50/75% of the firebolt's
	// roll at rank 1/2/3 (Game.CinderBurst, hooked in the kill scan). Rows
	// 25-29 of the now 25-wide tables.
	static readonly double[] OPT_BURN = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2.0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
	static readonly int[] OPT_CINDER = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
	static readonly double CINDER_R = 60.0; // cinder bloom blast radius (assistant pick: small)
	// shield kit cards (Wash, user 2026-10-31): OPT_BOUNCE = +1 shield bounce per rank (the thrown
	// shield hits its prey then hops to one MORE body per bounce; the hop lane lives on PartyMember);
	// OPT_CRITD = +0.25 CRIT_DAMAGE points per rank on the tag store. Rows 25-29 of the now 30-wide
	// tables: heavier shield | quicker hurl | more rebounds | sharper rim | crushing blow.
	static readonly int[] OPT_BOUNCE = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
	static readonly double[] OPT_CRITD = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.25, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.15, 0, 0, 0 };
	// axe kit cards (Bruno, user 2026-10-31): heavier axe rides the shared OPT_DMG 1.10 row,
	// quicker throw rides the shared OPT_CD 0.85 row; these three columns are NEW:
	// OPT_AXE = +1 extra crescent per rank (base ONE throw, arcs alternate top/bottom);
	// OPT_SIZE = +1 size rank per rank (the blade disc + band depth read 1.0 + 0.25 * rank);
	// OPT_BLEED = bleed rank (hit bodies bleed like the fire DoT, own status pair; dps scales
	// with the damage card at the call site). Rows 30-34 of the then 35-wide tables.
	static readonly int[] OPT_AXE = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
	static readonly int[] OPT_SIZE = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
	static readonly int[] OPT_BLEED = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
// Brook dagger cards (user 2026-10-31): sharper edges rides the shared OPT_DMG row,
// quicker jabs the shared OPT_CD row, deadly angle the shared OPT_CRIT row; these two
// columns are NEW: OPT_VENOM = +2 s of poison window per rank (lingering venom),
// OPT_MS = +0.05/rank multistrike CHANCE (lucky strikes: a hit deals damage twice).
// Rows 35-39 of the now 40-wide tables.
	static readonly double[] OPT_VENOM = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2.0, 0, 0, 0, 0, 0, 0, 0 };
	static readonly double[] OPT_MS = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.05, 0, 0, 0, 0, 0 };	// fifth cards (user 2026-11-01): the tables are now 45-wide and rows 40-44 give the FIRST five
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
	static readonly double[] OPT_FIRE = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1.0, 0, 0, 0, 0 };
	static readonly double[] OPT_RING = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.10, 0 };
	static readonly double[] OPT_LUCK = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.05 };

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
		Godot.CanvasLayer _selLayer = null;
		Control _selRoot = null;          // full-rect anchor parent for every piece
		ColorRect _selBack = null;        // full-rect cover: the card hides the world frame
		Control _selPage = null;          // the four-slot board window (2x2)
		ColorRect _selFrame = null;       // highlight frame: anchors jump to the browsed slot
		Label _selPanel = null;           // right-hand breakdown, rebuilt on every browse
		Label _selHint = null;            // key-grammar reminder on the card
		Control[] _selTile = new Control[0];
		Label[] _selName = new Label[0];
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
	string _pickerCard = "LEVEL UP";      // what the picker is offering (level-up / camp gift)
	double _pickerNoticeT = 0.0;          // "every rank capped - xp banked" notice countdown
	Godot.Collections.Array<PartyMember> _party = new();
	PartyMember _leader = null;
	Godot.Collections.Array<Godot.Node2D> _gems = new();
	Godot.Collections.Array<double> _gemVals = new();
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
	double _ringAngle = 0.0;
	// ---------- field-recruit state ----------
	// run clock drives the beat table; camp is excluded because it is a read-the-card
	// pause, and the picker / join soft-pauses keep stage beats honest while a human
	// reads the card. M2: one PLAY state covers horde AND boss fights, so the
	// PLAY1/MINIBOSS/PLAY2 set the run clock used to walk is gone.
	double _runT = 0.0;
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
		// starts the d20 death-save cycle ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â this is the revival test bench. Returns
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
				PartyMember[] cands = new PartyMember[9]; // roster-wide bound (9 PCs at 2026-11-01)
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

	public void SpawnGem(Vector2 pos, double value)
	{
		for (int i = 0; i < _gems.Count; i++)
		{
			Godot.Node2D gem = _gems[i];
			if (gem.GlobalPosition.X < -8000.0f)
			{
				gem.GlobalPosition = pos;
				_gemVals[i] = value;
				Godot.ColorRect cr = (Godot.ColorRect)gem.GetChild(0);
				cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f);
				return;
			}
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
		int pk = roll < 40.0 ? 0 : (roll < 70.0 ? 1 : 2); // 0 heal orb | 1 xp magnet | 2 rage vial
		SpawnPickup(c.GlobalPosition, pk);
	}
	
	public void SpawnPickup(Vector2 pos, int pk)
	{
		Pickup p = new Pickup(); // allocated per drop, like a horde body: pickups are rare
		p.pk = pk;
		AddChild(p);
		p.GlobalPosition = pos; // drops where the body died; Pickup.cs folds it like the gems
		p.InitVisuals();
		string label = pk == 0 ? "heal orb" : (pk == 1 ? "xp magnet" : "rage vial");
		GD.Print("[10thAge] (pickup) " + label + " drops on the field");
	}
	
	public void SpawnBullet(Vector2 pos, Vector2 vel, double dmg)
	{
		for (int i = 0; i < _bullets.Count; i++)
		{
			Godot.Node2D b = _bullets[i];
			if (b.GlobalPosition.X < -8000.0f)
			{
				b.GlobalPosition = pos;
				_bulletVel[i] = vel;
				_bulletDmg[i] = dmg;
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

	public void SpawnBulletArrow(Vector2 pos, Vector2 vel, double dmg, bool redOutline)
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
				Godot.ColorRect cr = (Godot.ColorRect)b.GetChild(0);
				if (!_fxTried)
				{
					_fxTried = true;
					_fxArrowTex = GD.Load("res://art/fx/arrow.png") as Godot.Texture2D;
					_fxFlameTex = GD.Load("res://art/fx/firebolt.png") as Godot.Texture2D;
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
			if (!_fxTried) // first fireball may precede any arrow: same try-once art discipline
			{
				_fxTried = true;
				_fxArrowTex = GD.Load("res://art/fx/arrow.png") as Godot.Texture2D;
				_fxFlameTex = GD.Load("res://art/fx/firebolt.png") as Godot.Texture2D;
				_fxMageTex = GD.Load("res://art/fx/mage_fireball.png") as Godot.Texture2D;
			}
		Godot.ColorRect cr = (Godot.ColorRect)b.GetChild(0);
		Godot.Sprite2D fxs = (Godot.Sprite2D)cr.GetChild(0);
		Godot.Texture2D mfx = _fxMageTex != null ? _fxMageTex : _fxFlameTex; // 2026-11-08 art pass: the goblin mage rides its OWN fireball art; the big firebolt stays the fallback read
		if (mfx != null)
		{
			fxs.Texture = mfx;
			float ms = _fxMageTex != null ? 1.5f : 2.4f; // ~1.5x: the 40 px fireball art over the bullet body (assistant pick - retunable)
			fxs.Scale = new Vector2(ms, ms);
			fxs.Modulate = _fxMageTex != null ? new Color(1.0f, 1.0f, 1.0f, 1.0f) : new Color(0.70f, 0.15f, 0.11f, 1.0f); // new art carries its own hues; fallback keeps the dark-red ENEMY read
			fxs.Rotation = 0.0f; // a fireball tumbles; it does not point like an arrow
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
				PrintSummary("VICTORY ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â " + StageBadge() + " cleared");
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
		for (int i = 0; i < 240; i++)
		{
			Godot.Node2D gem = new Node2D();
			Godot.ColorRect cr = new ColorRect();
			cr.Size = new Vector2(10.0f, 10.0f);
			cr.Position = new Vector2(-5.0f, -5.0f);
			cr.Color = new Color(1.0f, 0.85f, 0.2f, 1.0f);
			cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
			gem.AddChild(cr);
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
			ShowPauseCard(); // human lane: ESC/P opens, the card owns the close
		else if (_pilot && !_pausePilotUsed && _state == StageState.PLAY && !GetWorldPause() && !_campfireWaiting)
		{
			// headless proof: open the roster card once per run; the CARD's own
			// PROCESS_MODE_ALWAYS valve closes it (this _process is frozen by the pause)
			_pausePilotUsed = true;
			ShowPauseCard();
		}
		// run clock drives the beat table; camp is excluded because it is a read-the-card
		// pause, and the picker / join soft-pauses keep stage beats honest while a human
		// reads the card. M2: one PLAY state covers horde AND boss fights, so the
		// PLAY1/MINIBOSS/PLAY2 set collapses to PLAY (the camp / DEAD / VICTORY states do
		// not burn beats).
		if (!_pickerActive && !_joinWaiting && _state == StageState.PLAY)
		{
			_runT += d * _timeWarp; // TEST warp valve (1.0 = real-time: no M1_TIMEWARP = zero change)
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

		UpdateGems(delta);
		UpdateBullets(delta);
		UpdateShots(delta); // the party's own projectiles: mirror pool, opposite faction
		UpdateSummons(delta); // Yu's familiars: run at prey, detonate on contact OR on the fuse
		UpdateCinderFx(delta); // cinder blooms: the pops the last CinderBurst loosed, grow + fade
		UpdateChainFx(delta); // fx-feel9: chain bolts reveal + fade (Baldrun leader-bonus streaks)
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
						 " | ult " + _ultFired + " cd " + (_leader != null && _leader.ultCdT > 0.0 ? (int)System.Math.Ceiling(_leader.ultCdT) : 0));
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
		return LeaderBonusKey() == 7 ? 1.10 : 1.0;
	}
	// Brook "Inquisitive Mind" seat 8 (user 2026-10-31: allies +5% evasion, +15% collection
// range, +15% XP while Brook leads): the SAME multiplier discipline as the Bruno pair -
// readers multiply LIVE (Game.TakeHit dodge roll, Game.UpdateGems bubble + pickup XP) and
// write NO stat-store grants, so the 17-row audit stays a permanent-grant ledger.
	public double LeaderEvaMul()
	{
		return (LeaderBonusKey() == 8 || LeaderBonusKey() == 2) ? 1.05 : 1.0; // Inquisitive Mind | Mosiah seat also dodges 5% better (user 2026-11-02)
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
		return LeaderBonusKey() == 9 ? 1.0 : 0.0;
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
		// upkeep is unconditional: a helper on the field keeps living even if the seat
		// moved (nothing can move it today), so nothing pops off mid-raid. foreach over
		// Godot.Collections.Array is safe: _party never shrinks (join/eliminate flip flags)
		UpdateInsects(delta);
		UpdateSpirits(delta);
		UpdateBees(delta); // swarm upkeep: lives under ANY seat once a cast ran
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
	
	private void DespawnBee(int i)
	{
		Godot.Node2D s = _bees[i];
		s.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
		((Godot.ColorRect)s.GetChild(0)).Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
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
			PartyMember[] cands = new PartyMember[9]; // roster-wide bound (9 PCs at 2026-11-01; was exact-fit at 8)
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

	private void DespawnGem(int i)
	{
		Godot.Node2D gem = _gems[i];
		gem.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
		((Godot.ColorRect)gem.GetChild(0)).Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
	}

	private void UpdateBullets(float delta)
	{
		for (int i = 0; i < _bullets.Count; i++)
		{
			Godot.Node2D b = _bullets[i];
			if (b.GlobalPosition.X < -8000.0f)
				continue;
			b.GlobalPosition += _bulletVel[i] * delta;
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
				float hitR = nc is Boss ? 8.0f + ((Boss)nc).radius : 20.0f;
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
			if (at.DistanceSquaredTo(nc.GlobalPosition) > CINDER_R * CINDER_R)
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
				float tsx = stingerArt ? 0.72f : 1.0f; // arrow + flame ride 1:1, stinger stays near the old shaft footprint (assistant pick - retunable)
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
				float hitR = bestC is Boss ? 16.0f + ((Boss)bestC).radius : 20.0f;
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
				if (WrapDelta(p, n.GlobalPosition).Length() > r)
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
		}
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
	// (three across, two rows: 0,1,2 top | 3,4,5 bottom)
	static readonly float[] SEL_GRID = { 0.04f, 0.04f, 0.30f, 0.32f, 0.35f, 0.04f, 0.61f, 0.32f,
		0.66f, 0.04f, 0.96f, 0.32f, 0.04f, 0.36f, 0.30f, 0.64f, 0.35f, 0.36f, 0.61f, 0.64f, 0.66f, 0.36f, 0.96f, 0.64f, 0.35f, 0.68f, 0.61f, 0.96f, 0.66f, 0.68f, 0.96f, 0.96f, 0.04f, 0.68f, 0.30f, 0.96f };
	static readonly float[] SEL_PAGE = { 0.05f, 0.14f, 0.60f, 0.86f }; // the board window the build draws AND the click maps into
	static readonly bool[] SEL_LOCKED = { false, false, false, false, false, false, false, false, false, };
	static readonly float[] SEL_CONFIRM = { 0.32f, 0.88f, 0.58f, 0.96f };
	// painted mini art per roster seat (index = memberIndex: 0 Baldrun | 1 Mosiah | 2 Asper | 3 Alvora | 4 Yu | 5 Stack | 6 Wash | 7 Bruno | 8 Brook)
		// art reorganised 2026-11-08 (user: "move all the current characters into the character folder")
		// - portraits now live under res://art/characters/ alongside the art/enemies/ tree
		static readonly string[] SEL_ART = { "res://art/characters/baldrun.png", "res://art/characters/mosiah.png", "res://art/characters/asper.png", "res://art/characters/alvora.png", "res://art/characters/yu.png", "res://art/characters/stack.png", "res://art/characters/wash.png", "res://art/characters/bruno.png", "res://art/characters/brook.png" };
		static readonly string[] SEL_BONUS = {
		"Leader Bonus: Chain Lightning - All attacks have a 5% chance to unleash lightning on 5 targets for 10 damage.",
		"Leader Bonus: Pierce - All attacks gain +1 Pierce and all allies gain +5% Evasion.",
		"Leader Bonus: Bees! - A friendly bee spawns every 30 seconds. Touching one adds them as a temporary ally for 10s. They deal damage equal to half of Asper's Damage.",
		"Leader Bonus: Field Medic - All allies regenerate 1 HP every 10 seconds. This can over heal and add an additional 10 Temporary HP.",
		"Leader Bonus: Spirit Ritual - Friendly spirits spawn every 30s. Touching one heals all allies for 5 HP.",
		"Leader Bonus: Have a Drink! - While Stack leads, burning enemies deal 10% less damage and move 10% slower. Less effective on bosses.",
		"Leader Bonus: Inspiring Leader - While Wash leads, all allies gain +15 Max HP and +1 Armour.",
		"Leader Bonus: You... But Better! - All allies gain +15% Area and 10% Movement Speed.",
		"Leader Bonus: Inquisitive Mind - All allies gain +5% Evasion, +15% Collection Range and +15% XP." };
	static readonly string[] SEL_ULT = {
		"Ultimate: Giant's Might (R) - Doubles Baldrun's size and reach and grants +50% Damage and Attack Speed for 10s.",
		"Ultimate: Bardic Inspiration (R) - All allies gain +25% Damage and Attack Speed for 10s",
		"Ultimate: Swarm of Bees (R) - Spawns 6 bees which seek out and stick to enemies. Each bee deals 5 damage per second for 10 seconds.",
		"Ultimate: Guardian Awakening (R) - Doubles the area of Spirit Guardians, deals +25 Damage and every kill while awakened heals a random ally for 1 HP.",
		"Ultimate: The Kaka (R) - Spawns 6 empowered familiars. These familiars deal double damage and have their blast radius doubled.",
		"Ultimate: Experimental Elixir (R) - All allies gain 2 of 6 random buffs for 10s. Healing: 1% HP Regeneration. Swiftness: 10% Movement Speed. Resilience: -10% Damage Taken. Boldness: +10% Damage. Precision: +10% Crit Chance. Magnetism: +10% Pickup Range. There is a 10% chance to gain the effects of all Elixirs.",
		"Ultimate: Oakhollow Assemble! (R) - All allies gain +25% Movement Speed, +5 Armour and 1% HP Regeneration per second for 10s",
		"Ultimate: Axes Away! (R) - Bruno spins two axes around him for 10 seconds. These blades gain all of the effects of his chosen upgrades.",
		"Ultimate: Assassination (R) - All allies gain +25% Multistrike, +15% Crit Chance and +25% Crit Damage for 10s" };
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
		int ti = 0;
		foreach (PartyMember pm2 in _party)
		{
			// one slot: painted mini (TextureRect, aspect-kept, NEAREST like the stage
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
					Godot.TextureRect tr = new Godot.TextureRect(); tr.Texture = tex; // Control-friendly: anchor rects ride it
						tr.Set("stretch_mode", 3); // STRETCH_KEEP_CENTERED (3 in 4.7's renumbered enum,
						// probed headless): art at NATIVE pixel size centered in the slot - user 2026-10-28
						// said the 2.3x-scaled art looked stretched; native matches the stage minis
					// (aspect is moot under KEEP_CENTERED: the art is never scaled at all)
					tr.Set("texture_filter", 0); // NEAREST like the stage minis (hard painted edges)
					_selTile[ti] = tr;
				}
				SetRectSel(_selTile[ti], SEL_GRID[ti * 4], SEL_GRID[ti * 4 + 1],
						SEL_GRID[ti * 4 + 2], SEL_GRID[ti * 4 + 3]);
				_selName[ti] = new Label();
				SetRectSel(_selName[ti], 0.02f, 0.78f, 0.98f, 0.96f);
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
		_selHint.Text = "PRE-RUN: click a portrait to select, click CONFIRM to seat the Leader";
		_selRoot.AddChild(_selHint);
		_selBrowse = _selDemo ? -1 : 0; // humans open boxed on the roster head
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
	// one board, one page: the gold ring rides the selected slot (its rect grown
	// +/- 0.02 so the edge reads as a frame AROUND the tile); nothing selected parks
	// it hidden. (the framework pass's blocks-of-four paging is gone: rows of three
	// hold all five seats, so no slot ever parks off-page)
	private void ShowSelPage()
	{
		for (int k = 0; k < _selTile.Length; k++)
			_selTile[k].Visible = true; // all five seats stand: no page-two tail
		if (_selBrowse < 0 || _selBrowse >= _selTile.Length)
		{
			_selFrame.Visible = false; // nothing selected yet: the ring stays away
			return;
		}
		// the ring rides the selected slot: grown +/- 0.02 so its edge frames the tile
		SetRectSel(_selFrame, SEL_GRID[_selBrowse * 4] - 0.02f, SEL_GRID[_selBrowse * 4 + 1] - 0.02f,
				SEL_GRID[_selBrowse * 4 + 2] + 0.02f, SEL_GRID[_selBrowse * 4 + 3] + 0.02f);
		_selFrame.Visible = true;
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
		txt += "\nLeader Ability: " + SEL_BONUS[slot];
		txt += "\nUltimate Ability: " + SEL_ULT[slot];
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
		// slots next (page-local fractions)
		float px = (fx - SEL_PAGE[0]) / (SEL_PAGE[2] - SEL_PAGE[0]);
		float py = (fy - SEL_PAGE[1]) / (SEL_PAGE[3] - SEL_PAGE[1]);
		for (int k = 0; k < _selTile.Length; k++)
		{
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
			open[o] = _pickRank[o] < PICK_RANK_CAP && MemberInPlay(OPT_MEMBER[o]);
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
			if (_pickRank[o] < PICK_RANK_CAP && OPT_MEMBER[o] < _party.Count
				&& MemberInPlay(OPT_MEMBER[o]))
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
		GetTree().Paused = true; // true tree pause like RunSummary/Campfire: the world freezes
		// the pilot CLOSE valve is on the CARD (pilot_timeout export): its PROCESS_MODE_ALWAYS
		// _process ticks during this freeze while Game._process does not (seat 545 froze at
		// beat 1 with the valve here); 0.0 on a human seat = the card only closes on ESC
		_pauseCard.Set("pilot_timeout", _pilot ? PAUSE_PILOT_TIMEOUT : 0.0);
		_pauseWaitT = 0.0; // retired C# clock: kept so the field stays compile-clean
		GD.Print("[10thAge] pause: roster card up - ESC/P closes it");
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
			_pauseCard.Hide();
		GetTree().Paused = false;
		_pauseJustClosed = true; // the card ate the SAME ESC press: eat one frame, no reopen
		GD.Print("[10thAge] pause: card dismissed - the beat resumes");
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
