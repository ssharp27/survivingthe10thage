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

	// ---------- stage beat loop (M2 schedule, user re-locked 2026-10-23 again) ----------
	// The stage is a BEAT TABLE on the run clock, not a fixed state chain. Times are
	// RUN seconds, so a picker / join / camp pause never burns a beat (pass-22 rule).
	//   30s ally I | 60s tier-2 gate | 120s mini-boss I | 150s ally II | 180s boss I
	//   210s ally III | 240s mini-boss II | 270s ally IV | 360s final boss | 420s WRAP-UP
	// Second re-lock same day: the human playtest still wiped too often, so the user stretched
	// the early legs again (mini I 90 -> 120, boss I 150 -> 180, mini II 210 -> 240, final
	// 300 -> 360, wrap 400 -> 420) to give the party more scaling runway before each fight.
	// This is the gameplay loop "until times and scaling are changed around" (user rule),
	// so every number here is data the user can move without touching the sequencer.
	readonly double[] BEAT_T = { 30.0, 60.0, 120.0, 150.0, 180.0, 210.0, 240.0, 270.0, 360.0, 420.0 };
	// 0 = ally join beat, 1 = horde tier gate, 2 = mini-boss (its kill opens the camp),
	// 3 = mid boss (its kill just lets the horde keep marching), 4 = final boss (its kill
	// ALSO keeps the horde marching - the run rolls on), 5 = WRAP-UP (run clock lands on it
	// with a clear arena -> VICTORY; a live `final` holds the row back until it is resolved)
	readonly int[] BEAT_KIND = { 0, 1, 2, 0, 3, 0, 2, 0, 4, 5 };
	// the horde tier the stage runs at once that beat lands (0 = leave the tier alone).
	// Ally III / IV bring the tier-3 / tier-4 escorts the user asked for, so their beats
	// also advance the horde.
	readonly int[] BEAT_TIER = { 0, 2, 0, 0, 0, 3, 0, 4, 0, 0 };
	// boss beats name the Boss node they engage, so a beat is data and the scene owns the
	// body (four of them now, parked hidden until their beat fires)
	readonly string[] BEAT_BOSS = { "", "", "mini1", "", "boss1", "", "mini2", "", "final", "" };
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
	// escort bodies run on the tier that is live when the beat fires (user rule: later
	// beats are guarded by harder enemies), and they scale like the horde does
	double _hordeTier = 1.0;              // 1 goblins | 2 wolves+hobgoblins | 3/4 M2 gates
	// stage name/number shown on the ui/ cards AND the HUD (user 2026-10-23: Tanurial Forest
	// is "stage 1" / "level 1" - many more stages are planned, so every display site reads
	// the pair instead of a baked string)
	private const string STAGE_NAME = "Tanurial Forest";
	private const int STAGE_NUMBER = 1;
	// ---------- roster join lottery (user 2026-10-23: dynamic runs) ----------
	// WHICH roster PCs can be recruited this run and in WHICH order: one weighted,
	// without-replacement draw per ally beat, taken at boot. The run's own leader never
	// rolls onto its own pool (it CAN show up on runs where it is not the leader).
	// Row = STAGE_NUMBER - 1; columns are memberIndex order
	// (0 Baldrun | 1 Mosiah | 2 Asper | 3 Alvora | 4 Yu | 5 Stack). Weight 0 = cannot appear
	// on that stage. Stage 1 per the user: Baldrun + Mosiah more likely than the rest.
	// Sanity numbers - the user will add rows/weights per future stage.
	// the new-style initializer: Godot's C# build trips CS0623 on nested old-style braces
	private static readonly int[][] JOIN_W = { new int[] { 3, 3, 1, 1, 1, 1 } };   // row 0 = stage 1: Baldrun + Mosiah heavy, the rest parity
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
	const int PICK_SLOTS = 7;   // keys pick_1..pick_7: six members + one wild slot (pick_7 entered the input map 2026-10-30)
	static readonly string[] OPT_LABEL = { "longer reach (maul)", "heavier blows (maul damage)",
										   "swifter swings (cooldown)", "quicker thrusts (cooldown)",
										   "deeper thrust (reach + pierce)", "sharper points (damage)",
										   "heavier arrows (longbow damage)", "quicker draw (longbow cooldown)",
										   "piercing shot (longbow pierce)", "multishot (fan of shots)",
										   "spirit brand (guardian damage)", "wider ring (guardian reach)",
										   "quicker pulse (guardian rate)", "guiding light (ring vulnerability)",
										   "occult flame (blast damage)", "wider burst (blast reach)",
										   "quicker ritual (summon rate)", "twin ritual (one more familiar)", "draging maul (slow on hit)",
										   "deadly aim (crit chance)", "keener flame (firebolt damage)", "quicker fuse (firebolt cooldown)", "split bolt (firebolt pierce)", "kindling sparks (burn spreads)", "linger flame (burn lasts longer)" };
	static readonly int[] OPT_MEMBER = { 0, 0, 0, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 0, 1, 5, 5, 5, 5, 5 };
	static readonly float[] OPT_CD = { 1.0f, 1.0f, 0.86f, 0.86f, 1.0f, 1.0f, 1.0f, 0.85f, 1.0f, 1.0f, 1.0f, 1.0f, 0.85f, 1.0f, 1.0f, 1.0f, 0.85f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f };
	static readonly float[] OPT_RANGE = { 1.15f, 1.0f, 1.0f, 1.0f, 1.16f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.16f, 1.0f, 1.0f, 1.0f, 1.16f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f };
	static readonly float[] OPT_DMG = { 1.0f, 1.10f, 1.0f, 1.0f, 1.0f, 1.10f, 1.12f, 1.0f, 1.0f, 1.0f, 1.12f, 1.0f, 1.0f, 1.0f, 1.12f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.10f, 1.0f, 1.0f, 1.0f };
	static readonly int[] OPT_PIERCE = { 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0 }; // rows 4 + 8: Mosiah deeper thrust / Asper piercing shot (user 2026-10-30)
	static readonly int[] OPT_SHOTS = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 }; // multishot rank: for Yu it is the MULTIATTACK rank - one more familiar per ritual
	// guiding light (Alvora): the ring MARKS caught bodies for +10%/rank incoming damage
	// from EVERY source, a window on the body that decays on its own (Combatant.TickVuln).
	// The user swapped the old flat-armour defence for this offense (2026-10-23): the squad
	// keeps no mitigation, but the cleric's ring becomes the party's damage setup.
	static readonly int[] OPT_VULN = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
	// draging maul (Baldrun 4th card, user 2026-10-24): connected maul hits drag caught bodies to
	// (1 - 0.25/rank) speed for a 2.5 s window (Combatant.slowMul/slowT, refresh-not-stack); only
	// horde movement reads it and the cleave hook never marks Bosses. deadly aim (Mosiah 4th card):
	// +8%/rank CRIT_CHANCE points on the every-PC 5% seed - RollDamage already rolls that slot,
	// so the crit card is pure table data. Rows 18/19 of the 20-wide tables; caps bound both:
	// drag <= 0.75 at rank 3, crit <= 0.05 + 3 x 0.08 = 0.29.
	// OPT_SLOW = drag strength/rank (Baldrun row 18); OPT_CRIT = crit points/rank (Mosiah row 19).
	static readonly double[] OPT_SLOW = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.25, 0, 0, 0, 0, 0, 0 };
	static readonly double[] OPT_CRIT = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.08, 0, 0, 0, 0, 0 };
	// firebolt kit cards (Stack, user 2026-10-30): OPT_BURN = +2 s of linger per rank onto
	// PartyMember.burnDur; OPT_SPREAD = kindling sparks rank = the hop budget on Combatant
	// (a burning body ignites that many nearby unburnt bodies, each one hop cheaper). Rows
	// 25-29 of the now 25-wide tables.
	static readonly double[] OPT_BURN = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2.0 };
	static readonly int[] OPT_SPREAD = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0 };
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

	// ---------- ui/ screens (Campfire.tscn + RunSummary.tscn, see ui/INTEGRATION.md) ----------
	Godot.CanvasLayer _campfire;
	Godot.CanvasLayer _summary;
	bool _campfireWaiting = false;
	string _campfireChoice = "";
	// run summary retry: one ChangeSceneToFile per run, so a retry can never loop
	bool _summaryRetried = false;

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
		// bolt-pool pierce memo (leader bonus, user 2026-10-25): while Mosiah owns the
		// leader seat a bolt punches through one body and keeps flying. _shotPen is the
		// per-bolt pierce budget stamped at spawn; _shotLast memoizes the body the bolt
		// just raked so a pierced bolt cannot re-hit the SAME body the next frame.
		// (GetInstanceId is 64-bit, so the memo array is long-typed)
		Godot.Collections.Array<double> _shotPen = new();
		Godot.Collections.Array<double> _shotBurnD = new();   // firebolt burn: dps stamped at spawn (0 = inert)
		Godot.Collections.Array<double> _shotBurnT = new();   // burn window start, seconds
		Godot.Collections.Array<int> _shotBurnHop = new();  // kindling sparks hop budget stamped at spawn
		Godot.Collections.Array<long> _shotLast = new();
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
		const double REGEN_TICK = 10.0;   // Alvora: allies regen every 10 s (user)
		const double REGEN_HP = 1.0;      // +1 HP per tick (user)
		const double INSECT_EVERY = 30.0; // Asper: an insect waits every 30 s (user)
		const double INSECT_LASTS = 10.0; // a claimed insect helps for 10 s (user)
		const double INSECT_WAIT = 30.0;  // unclaimed wilts after 30 s: pick, keeps slots free
		const double SPIRIT_EVERY = 30.0; // Yu: a spirit kindles every 30 s (user)
		const double SPIRIT_LASTS = 30.0; // unclaimed wilts after 30 s: same pick
		const double SPIRIT_HEAL = 10.0;  // a touched spirit heals its patient for 10 HP (user)
		const double HELPER_PICK = 24.0;  // "getting close": walk-contact reach, pick
		const double HELPER_RING_MIN = 90.0;  // spawn ring around the leader: picks
		const double HELPER_RING_MAX = 170.0; // (never on top of the seat itself)
		// Swarm of Bees (Asper ult, user 2026-10-26): six seekers on a 10 s leash (user),
		// each latched to ONE body and biting 10 dmg/s (user); unlaunched they drift along
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
		const double BEE_BITE = 10.0;  // user: 10 damage per second to the latched body
		const double BEE_SPEED = 300.0; // pick: fast hunter, outruns the horde
		const double BEE_R = 48.0;     // pick: spawn-ring radius around Asper
		const double BEE_FIND = 240.0; // pick: hunting reach: nearest body in range latches
		const double BEE_LATCH = 16.0; // pick: bite range: the bee rides the body
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
		// defence tags (ARMOUR/EVASION): evasion rolls first for a full dodge,
		// then armour subtracts flat - never below 1 damage (user floor 2026-10-24)
		if (Rng != null && m.stat[StatTags.EVASION] > 0.0 && Rng.Randf() < m.stat[StatTags.EVASION])
		{
			m.NoteHitFlash(); // a dodge reads on the ghost-white lane, never the harm lane
			GD.Print("[10thAge] " + m.displayName + " EVASION dodge - the hit glances off");
			return;
		}
		if (m.stat[StatTags.ARMOUR] > 0.0)
		{
			amount -= m.stat[StatTags.ARMOUR];
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
		if (c is Boss) // four boss nodes now (mini1 / boss1 / mini2 / final): the class,
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
		// awakening window is up heals the STANDING squad +1 hp each - same eligibility the
		// spirit heals use (joined / not downed / not eliminated, clamp to EffMaxHp) - and
		// tallies on the awakening owner so its expiry print can report the haul.
		// Boss kills routed to OnBossKilled above and never count.
		foreach (PartyMember m in _party)
		{
			if (m.awaT > 0.0)
			{
				m.awaKills += 1;
				foreach (PartyMember mm in _party)
				{
					if (!mm.joined || mm.downed || mm.eliminated)
						continue; // fallen bodies keep their own recovery lanes
					mm.hp += 1.0f;
					if (mm.hp > mm.EffMaxHp())
						mm.hp = (float)mm.EffMaxHp(); // spirit-heal clamp: ceiling at own cap
				}
				break; // exactly one awakening owner per kill
			}
		}
		SpawnGem(c.GlobalPosition, ((Enemy)c).xpValue);
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
		// kind the scene authored (mini -> camp | mid boss -> the horde keeps marching |
		// final -> VICTORY).
		if (kb == "mini1" || kb == "mini2")
		{
			_state = StageState.CAMPFIRE;
			_campWaitT = 0.0;
			ClearEnemies();
			// user-approved camp-safety gate: the camp beat is a read-the-card moment,
			// so every in-flight boss bullet is swept before the grimoire goes up - a
			// player absorbing the rest choice cannot be shot mid-decision.
			ClearBullets();
			ReviveAllAtCampfire(false);
			GD.Print("[10thAge] mini-boss slain ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â camp: the fallen return at 25% hp; the "
					 + "rest choice decides healing");
		}
		else if (kb == "boss1")
		{
			// user beat loop (2026-10-22): the mid boss is a milestone fight, not the end of
			// the stage. Its death leaves the horde marching on toward the next beat, and
			// deliberately does NOT sweep the arena or open a camp (campfires sit after
			// mini-bosses only).
			_campWaitT = 0.0;
			GD.Print("[10thAge] mid boss slain - the stage keeps marching");
		}
		else if (kb == "final")
		{
			// user rule (2026-10-23): killing the final boss no longer ends the run. The
			// stage runs on to the WRAP-UP beat (420 s on the current lock) - the user's sandbox window to test
			// the full roster and horde scaling at strength. The body is already despawned
			// above; the horde keeps marching like it does after the mid boss.
			GD.Print("[10thAge] final boss slain - the run keeps rolling to the wrap-up beat");
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
				PrintSummary("VICTORY ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â " + STAGE_NAME + " (stage " + STAGE_NUMBER + ") cleared");
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
		if (_beatIdx < 0)
			return "horde I";
		switch (BEAT_KIND[_beatIdx])
		{
			case 0: return "rescue " + _allyBeatIdx;
			case 1: return "horde II";
			case 2: return "MINI-BOSS";
			case 3: return "BOSS";
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

	// world freeze = the picker card OR the join-banner card: both are read-the-card soft
	// pauses (the arena is drawn, nothing acts, nothing takes damage). The join banner got
	// this treatment instead of a tree pause so its CLICK gate keeps receiving input.
	public bool GetWorldPause()
	{
		return _pickerActive || _joinWaiting || _selHold;
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
		string roster = "";
		foreach (PartyMember pm2 in _party)
			roster += (roster == "" ? "" : " + ") + pm2.displayName + " (" + pm2.weaponName + ")";
		GD.Print("[10thAge] squad: " + roster + " | leader: " +
				 (_leader != null ? _leader.displayName : "none"));
		// the join lottery rolls at boot so the pilot / escort paths never have to re-decide
		// which PC a beat owns (the print keeps the smoke trails honest about who lands when)
		// human / bench-demo runs roll it in CloseSelect, AFTER the seat swap
		if (!_selHold)
				BuildJoinLottery();
		// the reaper is a pure death/revive TEST BENCH (no balance meaning): human play
		// left it OFF now that the revive rules work, and M1_REAPER=1 puts it back on
		// for testing (a pilot smoke with it ON still TPKs often - the Leader-down rule
		// we deferred leaves no rescue channel, which is variance, not a bug)
		_reaperEnabled = OS.GetEnvironment("M1_REAPER") != null && OS.GetEnvironment("M1_REAPER") != "";
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
			_shotBurnHop.Add(0);
			_shotLast.Add(-1);
		}
		// Yu's familiars: pooled like the bolts and the gems, so a rank-3 multiattack ritual
		// never allocates a node mid-horde. Shell-first so the creature reads as a body; the
		// detonation re-sizes this SAME rect to the blast it actually made (see DetonateSummon),
		// which is why SpawnSummon has to put the shell back before every reuse.
		for (int i = 0; i < 64; i++)
		{
			Godot.Node2D s = new Node2D();
			Godot.ColorRect cr = new ColorRect();
			cr.Size = new Vector2(14.0f, 14.0f);
			cr.Position = new Vector2(-7.0f, -7.0f);
			cr.Color = new Color(0.26f, 0.2f, 0.34f, 1.0f); // occult violet shell
			cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
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
		}
		// leader-bonus helper pools (user 2026-10-25): Asper's insects and Yu's spirits.
		// Slots stay small because one spawns every 30 s and neither phase lives past
		// 30 s - far more capacity than a run can ever have waiting at once.
		for (int i = 0; i < 8; i++)
		{
			Godot.Node2D s = new Node2D();
			Godot.ColorRect cr = new ColorRect();
			cr.Size = new Vector2(12.0f, 12.0f);
			cr.Position = new Vector2(-6.0f, -6.0f);
			// chitin green: deliberately off every horde hue so a friendly bug never reads as prey
			cr.Color = new Color(0.44f, 0.66f, 0.26f, 1.0f);
			cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
			s.AddChild(cr);
			s.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
			AddChild(s);
			_insects.Add(s);
			_insectLife.Add(0.0);
			_insectCd.Add(0.0);
			_insectOwner.Add(-1);
		}
		for (int i = 0; i < 6; i++)
		{
			Godot.Node2D s = new Node2D();
			Godot.ColorRect cr = new ColorRect();
			cr.Size = new Vector2(12.0f, 12.0f);
			cr.Position = new Vector2(-6.0f, -6.0f);
			cr.Color = new Color(0.78f, 0.92f, 0.88f, 1.0f); // pale ghost teal: a kindness spirit
			cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
			s.AddChild(cr);
			s.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
			AddChild(s);
			_spirits.Add(s);
			_spiritLife.Add(0.0);
		}
		// Swarm of Bees pool (Asper ult, user 2026-10-26): six slots like the cast, parked
		// at -9000; casts reuse them, UpdateBees owns the leash.
		for (int i = 0; i < 6; i++)
		{
			Godot.Node2D s = new Node2D();
			Godot.ColorRect cr = new ColorRect();
			cr.Size = new Vector2(10.0f, 10.0f);
			cr.Position = new Vector2(-5.0f, -5.0f);
			cr.Color = new Color(0.86f, 0.72f, 0.18f, 1.0f); // honey amber: off every horde hue AND the insect lane's chitin so a friendly bee never reads as prey
			cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
			s.AddChild(cr);
			s.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
			AddChild(s);
			_bees.Add(s);
			_beeLife.Add(0.0);
			_beeBite.Add(0.0);
			_beeRay.Add(0.0);
			_beeTarget.Add(-1L);
		}
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
		// run clock drives the beat table; camp is excluded because it is a read-the-card
		// pause, and the picker / join soft-pauses keep stage beats honest while a human
		// reads the card. M2: one PLAY state covers horde AND boss fights, so the
		// PLAY1/MINIBOSS/PLAY2 set collapses to PLAY (the camp / DEAD / VICTORY states do
		// not burn beats).
		if (!_pickerActive && !_joinWaiting && _state == StageState.PLAY)
		{
			_runT += d;
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
			AdvanceBeats();
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
		if (moved)
		{
			dir = dir.Normalized();
			leader.GlobalPosition = leader.GlobalPosition + dir * (float)leader.EffMove() * delta;
			if (faceTo.X != 0.0f || faceTo.Y != 0.0f)
				leader.facing = (float)System.Math.Atan2(faceTo.Y, faceTo.X);
			else
				leader.facing = (float)System.Math.Atan2(dir.Y, dir.X);
		}
		else if (leader != null)
		{
			// idle leader turns to its nearest threat: facing only followed the
			// movement direction, so standing still starved the maul everywhere
			// except the way they last walked
			Godot.Node2D best = null;
			float bestD = 1e9f;
			for (int i = 0; i < _enemies.Count; i++)
			{
				Combatant c = (Combatant)_enemies[i];
				if (c == null || c.kind == "dead")
					continue;
				Godot.Node2D n = _enemies[i];
				float dd = n.GlobalPosition.DistanceTo(leader.GlobalPosition);
				if (dd >= bestD)
					continue;
				bestD = dd;
				best = n;
			}
			foreach (Boss b in _bosses)
			{
				if (!b.active || b.hp <= 0.0f)
					continue; // parked bosses are not threats yet
				float dd = b.GlobalPosition.DistanceTo(leader.GlobalPosition);
				if (dd < bestD)
				{
					bestD = dd;
					best = b;
				}
			}
			if (best != null && bestD <= (float)leader.EffRange() * 1.5f)
			{
				Vector2 v = best.GlobalPosition - leader.GlobalPosition;
				leader.facing = (float)System.Math.Atan2(v.Y, v.X);
			}
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
				OnEnemyKilled(c);
				_enemies.RemoveAt(i);
			}
		}

		UpdateGems(delta);
		UpdateBullets(delta);
		UpdateShots(delta); // the party's own projectiles: mirror pool, opposite faction
		SpreadScan(); // kindling sparks: burning bodies ignite near ones (hop budget = rank)
		UpdateSummons(delta); // Yu's familiars: run at prey, detonate on contact OR on the fuse
		UpdateHelpers(delta); // leader-bonus lane (user 2026-10-25): Alvora regen, Asper insects, Yu spirits
		PickerTick(d);
		_hud.Text = "Surviving the 10th Age - " + STAGE_NAME + " (stage " + STAGE_NUMBER +
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
					hps += (hps == "" ? "" : "  ") + mm.displayName +
						   (mm.eliminated ? " OUT" : (mm.downed ? " down " + mm.dsFails + "/" + DS_FAIL_LIMIT : " " + (int)mm.hp + "hp"));
				}
				GD.Print("[10thAge] (pilot) " + StateName(_state) + " t=" + (int)GetRunTime() +
						 " | beat " + BeatLabel() + " tier " + (int)GetHordeTier() + " boss " + BossReadout() +
						 " | lv " + _level + " xp " + (int)_xpTotal + " | live enemies " + _liveEnemies +
						 " | party: " + hps +
						 " | lb chain " + _lbChainFired + " bound " + _lbInsectBound + " healed " + _lbSpiritTaken +
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
	// 4 Asper friendly insects | 5 Yu kindling spirits. Display names are the roster key,
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
		if (_leader.displayName == "Yu")
			return 5;
		return 0;
	}
	
	// Baldrun's seat: every party hit that LANDS rolls 5% to loose a lightning chain hopping
	// body to body - up to 4 bounces, 10 damage each (user 2026-10-25). Hooked at EVERY party
	// hit event (rapier lane, maul fan, guardian ring, bolt pool, familiar blast), so the
	// proc is per landed hit and a cleave raking three bodies rolls three times. Bounces pick
	// the nearest UNTOUCHED living body within CHAIN_RANGE (the user left the hop range open;
	// 150 px is the pick) and a body killed but not yet swept by the kill scan (hp <= 0) is
	// not a valid hop. Visual: each hopped body reads via the shared hurt flash this frame;
	// a drawn bolt line is a display-pass follow-up.
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
			((Combatant)next).TakeDamage(CHAIN_DMG);
			vis[used++] = next;
			cur = next;
		}
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
	
	// per-frame half of the leader lane: regen ticks (Alvora), insect / spirit upkeep,
	// and the seat-locked spawn clocks (Asper / Yu). Upkeep runs under ANY seat so a
	// helper already out there keeps living; only clocks and regen tick are seat-gated.
	private void UpdateHelpers(float delta)
	{
		int lb = LeaderBonusKey();
		if (lb == 3)
		{
			// Alvora seat: every 10 s every standing ally regenerates 1 hp (user 2026-10-25).
			// Own clock, not the dead hp-regen tag lane; never over-heals past the EffMaxHp
			// ceiling; a stranded recruit is not yet an ally, a fallen body recovers via
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
			double rr = HELPER_RING_MIN + Rng.Randf() * (HELPER_RING_MAX - HELPER_RING_MIN);
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
				s.GlobalPosition = host.GlobalPosition + new Vector2(0.0f, -26.0f);
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
				SpawnShot(s.GlobalPosition + dir * 18.0f, dir * seat.projSpeed, dmg, seat.projLife);
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
	
	// swarm upkeep: a latched bee rides its prey and chews 10 dmg/s (user); a hunting bee
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
				double best = BEE_FIND * BEE_FIND;
				Godot.Node2D pick = null;
				foreach (Godot.Node2D n in enemies)
				{
					Vector2 off = WrapDelta(s.GlobalPosition, n.GlobalPosition);
					double d2 = off.X * off.X + off.Y * off.Y;
					if (d2 < best)
					{
						best = d2;
						pick = n;
					}
				}
				if (pick != null)
				{
					prey = pick;
					_beeTarget[i] = (long)pick.GetInstanceId();
					_beeBite[i] = 0.0; // the first chomp lands the moment the bee rides on
				}
			}
			if (prey != null)
			{
				Vector2 off = WrapDelta(s.GlobalPosition, prey.GlobalPosition);
				if (off.X * off.X + off.Y * off.Y > BEE_LATCH * BEE_LATCH)
				{
					s.GlobalPosition = WrapNearCamera(s.GlobalPosition + off.Normalized() * (float)(BEE_SPEED * delta));
					_beeBite[i] = 0.0; // walking in: the chew only starts once it is riding
				}
				else
				{
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
					GD.Print("[10thAge] (ult) Swarm of Bees disperses: " + _beeDrained + " prey drained by 6 bites of 10 dmg/s over 10 s");
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
	
	// Yu seat upkeep: a standing ally within HELPER_PICK touches a vigil; the patient is one
	// RANDOM ally who has ACTUALLY LOST HP, healed 10 and the spirit is consumed (user
	// 2026-10-25). A healthy squad never drinks: the vigil waits instead of being wasted.
	// _party is a Godot.Collections.Array and never shrinks (join/eliminate flip flags),
	// so plain foreach + a fixed candidate array is safe; one Rng draw per heal shifts
	// the run stream (documented). Downed / eliminated bodies are excluded by design:
	// down is a death-save situation, not an open hp wound.
	private void UpdateSpirits(float delta)
	{
		for (int i = 0; i < _spirits.Count; i++)
		{
			Godot.Node2D s = _spirits[i];
			if (s.GlobalPosition.X < -8000.0f)
			continue; // free slot: same idiom as the summon pool
			Vector2 ip = s.GlobalPosition;
			bool touched = false;
			foreach (PartyMember mm in _party)
			{
				if (!mm.joined || mm.downed || mm.eliminated)
				continue; // stranded / fallen: cannot touch the vigil
				if (WrapDelta(mm.GlobalPosition, ip).Length() < (float)HELPER_PICK)
				{
					touched = true;
					break;
				}
			}
			if (touched)
			{
				PartyMember[] cands = new PartyMember[8];
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
					PartyMember pat = cands[(int)(Rng.Randf() * cn)];
					pat.hp += (float)SPIRIT_HEAL;
					if (pat.hp > pat.EffMaxHp())
					pat.hp = (float)pat.EffMaxHp();
					_lbSpiritTaken++;
					GD.Print("[10thAge] (bonus) spirit heals " + pat.displayName + " for 10 hp");
					DespawnSpirit(i);
					continue;
				}
				// healthy squad: the vigil is NOT consumed, it keeps waiting below
			}
			_spiritLife[i] -= delta;
			if (_spiritLife[i] <= 0.0)
			{
				DespawnSpirit(i); // nobody hurt / nobody close in time: it dims (pick)
				continue;
			}
		}
	}

	private void UpdateGems(float delta)
	{
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
				if (md < 110.0f * (float)mm.stat[StatTags.COLLECTION_RANGE] && md < dist)
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
					AddXp(_gemVals[i] * (1.0 + puller.stat[StatTags.XP_PCT])); // XP_PCT tag: gems worth more at pickup
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
						nc.Burn(_shotBurnD[i], _shotBurnT[i], _shotBurnHop[i]); // kills still land on the shared scan
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

	// a PC fires one bolt: same free-slot scan the boss pool uses, no allocation
	private void SpreadScan()
	{
		// kindling sparks (Stack card, user 2026-10-30): a burning body with hop budget left
		// ignites NEARBY unburnt horde bodies, each ignition one hop cheaper, so rank 3 chains
		// at most three new bodies. Ignitions are collected first and applied after the scan,
		// so a body lit this pass cannot relay the flame the same frame. Bosses are skipped:
		// spread hops through the horde only, and the radius is the authored silhouette plus
		// a 30 px spark gap, like the bolt hit test's maths.
		Godot.Collections.Array<Godot.Node2D> ignite = new();
		Godot.Collections.Array<double> dpsA = new();
		Godot.Collections.Array<double> durA = new();
		Godot.Collections.Array<int> hopA = new();
		for (int a = 0; a < _enemies.Count; a++)
		{
			Combatant ca = (Combatant)_enemies[a];
			if (ca == null || ca.kind == "dead" || ca.burnT <= 0.0 || ca.burnHop <= 0)
				continue; // only a burning body with budget left can spread
			Vector2 pa = ca.GlobalPosition;
			for (int b = 0; b < _enemies.Count; b++)
			{
				if (b == a)
					continue;
				Godot.Node2D nb = _enemies[b];
				Combatant cb = (Combatant)nb;
				if (cb == null || cb.kind == "dead" || cb is Boss || cb.burnT > 0.0)
					continue; // already burning: it rides its own window; bosses hold their own lane
				double rad = 20.0 + 30.0; // horde silhouettes share one spark gap (bosses are skipped above)
				if (pa.DistanceSquaredTo(nb.GlobalPosition) > rad * rad)
					continue;
				ignite.Add(nb);
				dpsA.Add(ca.burnDps);
				durA.Add(ca.burnT); // pass the REMAINING window: the flame rides one clock face
				hopA.Add(ca.burnHop - 1);
			}
		}
		if (ignite.Count == 0)
			return;
		GD.Print("[10thAge] (spread) ignite=", ignite.Count);
		for (int i = 0; i < ignite.Count; i++)
		{
			Combatant cb = (Combatant)ignite[i];
			cb.Burn(dpsA[i], durA[i], hopA[i]);
		}
	}
	public void SpawnShot(Vector2 pos, Vector2 vel, double dmg, double life, double pen = 0.0, double burnDur = 0.0, double burnDps = 0.0, int spread = 0) // pen/burn/hop stamped at spawn: Asper + Mosiah pierce, Stack firebolt burn; everyone else keeps the 0 defaults
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
			// firebolt burn / hop budget stamped at spawn too (all 0 = inert for other lanes)
			_shotBurnT[i] = burnDur;
			_shotBurnD[i] = burnDps;
			_shotBurnHop[i] = spread;
			_shotLast[i] = -1;
			((Godot.ColorRect)s.GetChild(0)).Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f);
			return;
		}
	}

	// ---------- Yu's familiars: run -> detonate, damage through the shared kill scan ----------
	public void SpawnSummon(Vector2 pos, double dmg, double fuse, double radius, float speed)
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
			_summonBurst[i] = false;
			// put the shell back: the slot may have been left mid-blast by DetonateSummon
			Godot.ColorRect cr = (Godot.ColorRect)s.GetChild(0);
			cr.Size = new Vector2(14.0f, 14.0f);
			cr.Position = new Vector2(-7.0f, -7.0f);
			cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f);
			return;
		}
	}

	public void SpawnKakas(Vector2 center, double dmg, double fuse, double radius, float speed)
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
			Godot.ColorRect cr = (Godot.ColorRect)s.GetChild(0);
			cr.Size = new Vector2(14.0f, 14.0f);
			cr.Position = new Vector2(-7.0f, -7.0f);
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
		}
	}

	private void DetonateSummon(int i)
	{
		Godot.Node2D s = _summons[i];
		Vector2 p = s.GlobalPosition;
		double dmg = _summonDmg[i];
		double r = _summonR[i];
		int caught = 0; // blast tally: only an empowered Kaka prints it
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
			caught++;
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
			string tag = (m.isLeader ? "* " : "") + m.displayName;
			bool stranded = !m.joined; // field beat only: live strip tracks the join HP
			n.Text = stranded ? tag + " STRANDED " + (int)m.hp + "/" + (int)m.EffMaxHp()
							 : (isOut ? tag + " OUT (campfire)"
									: (down ? tag + (m.stable ? " STABLE" : " DOWN")
										  : tag + " " + (int)m.hp + "/" + (int)m.EffMaxHp()));
			f.Size = new Vector2(110.0f * frac, 7.0f);
			f.Color = m.isLeader ? new Color(0.83f, 0.64f, 0.24f, 1.0f)
								 : new Color(0.62f, 0.52f, 0.38f, 1.0f);
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
	static readonly float[] SEL_GRID = { 0.04f, 0.04f, 0.30f, 0.48f, 0.35f, 0.04f, 0.61f, 0.48f,
		0.66f, 0.04f, 0.96f, 0.48f, 0.04f, 0.54f, 0.30f, 0.98f, 0.35f, 0.54f, 0.61f, 0.98f, 0.66f, 0.54f, 0.96f, 0.98f };
	static readonly float[] SEL_PAGE = { 0.05f, 0.14f, 0.60f, 0.86f }; // the board window the build draws AND the click maps into
	static readonly bool[] SEL_LOCKED = { false, false, false, false, false, false };
	static readonly float[] SEL_CONFIRM = { 0.32f, 0.88f, 0.58f, 0.96f };
	// painted mini art per roster seat (index = memberIndex: 0 Baldrun | 1 Mosiah | 2 Asper | 3 Alvora | 4 Yu | 5 Stack)
		static readonly string[] SEL_ART = { "res://art/baldrun.png", "res://art/mosiah.png", "res://art/asper.png", "res://art/alvora.png", "res://art/yu.png", "res://art/stack.png" };
		static readonly string[] SEL_BONUS = {
		"Leader bonus: chain lightning - landed party hits can loose a lightning chain hopping between bodies",
		"Leader bonus: pierce - every bolt punches through one body and keeps flying",
		"Leader bonus: friendly insects kindle in the clearing; touching one joins the squad for 10 s",
		"Leader bonus: field medic - every 10 s every standing ally regenerates 1 hp",
		"Leader bonus: kindness spirits kindle in the clearing; touching one heals 10 hp",
		"Leader bonus: none yet - Stack's leader lane ships in a future pass" };
	static readonly string[] SEL_ULT = {
		"Ultimate: Giant's Might (R) - doubles size and reach, damage x1.5, for 10 s",
		"Ultimate: Bardic Inspiration (R) - rallies ALL allies +25% damage and attack speed for 10 s",
		"Ultimate: Swarm of Bees (R) - 6 hunters in a circle, one body latched each at 10 dmg/s for 10 s, spread out to hunt",
		"Ultimate: Guardian Awakening (R) - ring reach x2 and +50% damage; every horde kill while awake heals ALL allies 1 hp for 10 s",
		"Ultimate: The Kaka (R) - 6 empowered familiars in a circle, blast x2 and damage x2 after ranks, spread out",
		"Ultimate: none yet - Stack's ultimate ships in a future pass" };
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
		BuildJoinLottery();
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
		int row = STAGE_NUMBER - 1;
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
		GD.Print("[10thAge] join lottery (stage " + STAGE_NUMBER + "): " +
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
		{ m.burnSpread += OPT_SPREAD[option]; } // kindling sparks: spread hop budget on the kit field
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
			_campfire.Set("stage_name", STAGE_NAME);
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
				 + ESCORT_COUNT + " tier-" + (int)_hordeTier + " bodies hold still until the party is in sight of the rescue");
		m.Visible = true;
		// HELP! floats over the stranded PC
		_helpLabel = new Godot.Label();
		_helpLabel.Text = "HELP!";
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
		// escort bodies run on the tier that is live when the beat fires (user rule: later
		// beats are guarded by harder enemies), drawn from that tier's pool. The 90s beat is
		// six wolves / hobgoblins, not six goblins.
		// TIER 3 / TIER 4 CREATURES ARE NOT DESIGNED YET: the user will dictate them, so
		// until they land those beats escort with the tier-2 bodies plus an elite HP bump.
		// That is a flagged placeholder, not invented content - see HORDE_TIER3_BELOW.
		for (int i = 0; i < ESCORT_COUNT; i++)
		{
			Enemy e = new Enemy();
			int tier = (int)_hordeTier;
			bool eliteBeast = tier >= 2 && Rng.Randf() < 0.5;
			e.kind = tier >= 2 ? (eliteBeast ? "wolf" : "hobgoblin") : "goblin";
			float hpBase = tier >= 2 ? (eliteBeast ? 70.0f : 60.0f) : 30.0f;
			float tierBump = tier >= 4 ? 1.6f : (tier >= 3 ? 1.3f : 1.0f);
			e.maxHp = hpBase * tierBump * (float)GetHordeScale();
			e.hp = e.maxHp;
			e.speed = tier >= 2 ? 105.0f : 90.0f;
			e.contactDamage = tier >= 2 ? 3.0f : 2.0f;
			e.xpValue = tier >= 2 ? 2.0f : 1.0f;
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
			_summary.Set("stage_name", STAGE_NAME);
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
