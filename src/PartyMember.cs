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
	[Export] public string weaponKit = "melee"; // "melee" | "shot" | "aura" | "summon"
	[Export] public float projSpeed = 340.0f;  // px/s a bug projectile travels
	[Export] public float projLife = 2.2f;     // seconds a bolt may fly before it expires
	// firebolt kit knobs (Stack, user 2026-10-30): a hit sets a burn window on the body (dps x
	// seconds, Combatant.Burn); burnSpread = how many NEAR bodies that flame may ignite. The
	// DoT dps scales with the damage card at the call site (stat[ATTACK_DAMAGE] is the card
	// product; crits and ults stay per-swing like every other weapon)
	[Export] public double burnDps = 0.0;   // damage/sec stamped on bodies this member hits (0 = no burn)
	[Export] public double burnDur = 0.0;   // seconds that burn runs; linger flame cards raise it
	[Export] public int burnSpread = 0;     // kindling sparks rank: spread hop budget on the flame
	// Yu's familiars (M2, user dictation 2026-10-22): the fuse is the summon's LIFETIME, and
	// the familiar detonates on contact with prey OR when that fuse burns out - the fuse is
	// what keeps a player from parking a pile of them under a boss and saving them for later.
	// There is no summon cap (user): they never live long enough to stack up.
	[Export] public float minionSpeed = 84.0f;  // px/s a familiar runs (a hair under a goblin's 90)
	[Export] public float minionRadius = 56.0f; // px blast radius when it detonates (starts small)
	[Export] public float minionFuse = 6.0f;    // seconds of fuse before it detonates by itself
	// multistrike now lives in the stat store (StatTags.MULTISTRIKE); Strikes() reads it

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
	// guardian damage +50% at the RollDamage choke, and Game.OnEnemyKilled tallies awaKills:
	// every horde kill while awake heals the standing squad +1 hp each. _awaUp guards the
	// one-shot expiry print, same discipline as _grown.
	public double awaT = 0.0;
	public int awaKills = 0;
	bool _awaUp = false;
	readonly double AWA_LASTS = 10.0; // assistant pick (user named no window)
	readonly double AWA_RANGE = 2.0; // ring reach x2, after the AREA tag (EffRange)
	readonly double AWA_DMG = 1.5; // +50% guardian damage while awake (RollDamage choke)
	Godot.Node2D _telePivot; // telegraph armature (pure nodes: C# _draw is dead in 4.7)
	Godot.ColorRect[] _teleParts;
	float _teleHold = 0.0f; // telegraph visibility timer, set on each swing
	// Spirit Guardians armature: motes orbiting under ONE pivot, so a single Scale change carries
	// the "wider ring" rank (pure nodes again: the C# _draw hook is dead in 4.7)
	Godot.Node2D _auraPivot = null;
	Godot.ColorRect[] _auraParts;
	float _auraPulse = 0.0f; // seconds of ring flare left from the last pulse
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
				" doubles size and reach (damage x1.5) for " + (int)ULT_LASTS +
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
				" doubles ring reach (after ranks) and burns it +50% for " + (int)AWA_LASTS +
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
				" looses 6 hunters in a circle (10 dmg/s each, 10 s) spread out to hunt, gate " +
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
				float blast = (float)(minionRadius * stat[StatTags.AREA]); // wider burst rank: same as the ritual
				g4.SpawnKakas(GlobalPosition, RollDamage(g4, weaponDamage), minionFuse, blast, minionSpeed);
			}
			GD.Print("[10thAge] (ult) The Kaka: " + displayName +
				" looses 6 empowered familiars in a circle (blast x2, damage x2 after ranks, same fuse) that spread out, gate " +
				(int)(ULT_CD_BASE / stat[StatTags.ULT]) + " s");
			return true;
		}
		GD.Print("[10thAge] (ult) " + displayName + " has no ultimate yet");
		return false;
	}

	/** Seed the stat store from the registry BASE (once, in _EnterTree). */
	public void SeedStatStore()
	{
		for (int i = 0; i < StatTags.N; i += 1)
			stat[i] = StatTags.BASE[i];
	}
	/** Authored weaponDamage scaled by the ATTACK_DAMAGE tag. */
	public double EffDamage() { return weaponDamage * stat[StatTags.ATTACK_DAMAGE]; }
	/** Authored cooldown divided by the ATTACK_SPEED tag (higher = faster attacks). */
	public double EffCooldown()
	{
		double spd = stat[StatTags.ATTACK_SPEED] * (insT > 0.0 ? INS_SPD : 1.0); // bardic +25%
		if (spd < 0.05)
			spd = 0.05; // clamp: stacked speed cards can't drive the timer to zero
		return cooldown / spd;
	}
	/** Authored reach scaled by the AREA tag. */
	public double EffRange() { return range * stat[StatTags.AREA] * (ultT > 0.0 ? ULT_RANGE : 1.0) * (awaT > 0.0 ? AWA_RANGE : 1.0); } // awakening widens too
	/** Authored maxHp plus the additive HEALTH tag (points). */
	public double EffMaxHp() { return maxHp + stat[StatTags.HEALTH]; }
	/** Authored baseSpeed scaled by the MOVEMENT_SPEED tag. */
	public double EffMove() { return baseSpeed * stat[StatTags.MOVEMENT_SPEED]; }
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
			amount *= AWA_DMG; // awakening: +50% on Alvora hits while awake
		if (host is Game g && g.Rng != null && g.Rng.Randf() < stat[StatTags.CRIT_CHANCE])
			amount *= stat[StatTags.CRIT_DAMAGE];
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
		if (!downed && joined && stat[StatTags.REGEN] > 0.0)
		{
			double regenCap = EffMaxHp();
			if (hp < regenCap)
			{
				hp += (float)(stat[StatTags.REGEN] * delta);
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
			return; // a stranded recruit cannot swing: its weapon unlocks ON the join
		// ---- Spirit Guardians ring: orbit + pulse flare ----
		// A slow constant orbit so the ring reads as HIS aura at a glance; the flare is what makes
		// a PULSE readable without a HUD. One pivot, so one Scale carries the "wider ring" rank.
		if (weaponKit == "aura" && _auraPivot != null)
		{
			_auraPivot.Scale = new Vector2((float)(stat[StatTags.AREA] * (awaT > 0.0 ? AWA_RANGE : 1.0)), (float)(stat[StatTags.AREA] * (awaT > 0.0 ? AWA_RANGE : 1.0))); // awakened ring: visible pivot matches the doubled hit test
			_auraPivot.Rotation += (float)(1.1 * delta);
			if (_auraPulse > 0.0f)
			{
				_auraPulse -= delta;
				// flare decays over the pulse window, so the ring brightens then settles
				_auraPulse = _auraPulse < 0.0f ? 0.0f : _auraPulse;
				float f = _auraPulse / 0.22f;
				foreach (Godot.ColorRect s in _auraParts)
					s.Modulate = new Color(1.0f + f * 0.7f, 1.0f, 1.0f + f * 0.4f);
			}
			else
			{
				foreach (Godot.ColorRect s in _auraParts)
					s.Modulate = new Color(1.0f, 1.0f, 1.0f);
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
				" horde slain while awake - the standing squad healed " + awaKills +
				" hp each (tally via Game.OnEnemyKilled)");
		}
		if (awaT < 0.0)
			awaT = 0.0;
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
			Godot.Node2D prey = null;
			float preyD = 1e9f;
			for (int i = 0; i < enemies.Count; i++)
			{
				Godot.Node2D n = enemies[i];
				Combatant nc = (Combatant)n;
				if (nc == null || nc.kind == "dead")
					continue;
				Vector2 dv = pg.WrapDelta(p, n.GlobalPosition);
				float dd = dv.Length();
				if (dd >= preyD)
					continue;
				preyD = dd;
				prey = n;
			}
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
				pg.SpawnSummon(p + dir * 26.0f, (float)RollDamage(pg, weaponDamage), minionFuse, blast, minionSpeed);
			}
			// the ritual IS the familiar: once the volley is loosed the beat must NOT
			// fall through into the shared lane/fan search below (user bugfix 2026-10-28:
			// the fall-through handed Yu a free rapier thrust at anything inside reach every
			// beat, reading as a damage aura around him; his minions ARE his damage)
			return;
		}
		Godot.Node2D best = null;
		float bestD = 1e9f;
		for (int i = 0; i < enemies.Count; i++)
		{
			Godot.Node2D n = enemies[i];
			Combatant nc = (Combatant)n;
			if (nc == null || nc.kind == "dead")
				continue;
			Vector2 v = n.GlobalPosition - p;
			float dd = v.Length();
			if (dd >= bestD)
				continue;
			// Targeting gate is LANES-only (rapier): a lane weapon may only attack
			// what its facing arc covers (touching enemies always count). The maul
			// Mosiah-aims: it attacks the ABSOLUTE nearest body whatever way the
			// body faces, and the ~150 deg arc rides the swing (below), not the
			// walk direction - so a Baldrun walking away from the horde still
			// turns its cleave to the nearest threat instead of whiffing air.
			// a shooter Mosiah-aims too: the bow picks the nearest body in sight whatever way
			// it faces, so the lane gate stays lane-only
			if (isLeader && !cleaver && !shotKit && dd >= 40.0f &&
				System.Math.Abs(angleDiff((float)System.Math.Atan2(v.Y, v.X), facing)) > arcEff)
				continue;
			bestD = dd;
			best = n;
		}
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
				pg.SpawnShot(p + dir * 18.0f, dir * projSpeed, dmg, projLife, pierce, burnDur, burnDps * (float)stat[StatTags.ATTACK_DAMAGE], burnSpread); // firebolt: DoT rides the damage card, not crits
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
			_teleHold = 0.18f;
		}
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
}
