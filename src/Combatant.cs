using Godot;

/// <summary>
/// Shared combat surface for anything a weapon can hit (Enemy, Boss). Members deal damage
/// through TakeDamage without caring who they struck; kills are detected by the Game root.
/// </summary>
public partial class Combatant : Godot.Node2D
{
	public float hp = 10.0f;
	// @export so PackedScene overrides (Main.tscn's kind/maxHp on BossMini/BossFinal) ship
	// in the PCK; plain-public overrides only apply inside the editor (see PartyMember.cs)
	[Export] public float maxHp = 10.0f;
	[Export] public string kind = "goblin";
	public float contactDamage = 5.0f;
	public float hurtFlash = 0.0f;
	// HITBOX = THE ARTED SILHOUETTE (user 2026-11-12 hand-play: "the hitboxes need to be
	// the actual enemy art - attacks are only aiming at the middle where the temp square
	// used to be"): every hittable body carries the radius its DRAWN art covers, and the
	// hit tests in the codebase read THIS instead of a fixed core. Enemy.cs stamps it from
	// the canvas it loads (half the greybox while art is missing); Boss.cs stamps it from
	// its own art. The DEFAULT keeps an unstamped body hittable at the old 20 px bolt core,
	// so a body whose art failed to load can never become unhittable.
	public float hitR = 20.0f;

	// ROUND 16 (user hand-play 2026-11-27: "wasn't killing anything at all" party-wide from ~850 s on):
	// crescent-volley and spin-cast BITE MEMORY rides the BODY. PartyMember's old bitmaps were
	// indexed by LIST SLOT while Game's kill scans RemoveAt-compact _enemies in the SAME frame a
	// sweep runs - the moment a body died, every later index named a DIFFERENT body, so survivors
	// that shifted into a remembered bit were silently skipped and never bitten again; worse, both
	// maps were fixed at 64 entries, so past body 64 NOTHING was remembered OR bitten at horde
	// density. gen = the last crescent tag / spin-cast tag this body paid; mask = per-blade bits of
	// that generation. One honest bite per crescent per volley per body is kept exactly as before.

	public int biteGen = 0;   // crescent volley tag this body paid last (PartyMember _cresGenNext)
	public int biteMask = 0;  // per-crescent bits (1 << cc) of that volley
	public int spinGen = 0;   // spin-cast tag this body paid last (PartyMember _spinGenNext)
	public int spinMask = 0;  // per-blade circle bits (1 << blade) of that cast


	// guiding light (Alvora ring vulnerability, user 2026-10-23): bodies caught by her
	// ring take +% damage from ALL sources for a window. Lives on the SHARED surface so
	// melee, bolts, bullet pools and future verbs mark for free; the stronger mark
	// overwrites and the window only ever refreshes, so ranks never multiply.
	public double vulnMul = 0.0;
	public double vulnT = 0.0;

	public void Vulnerable(double mul, double duration)
	{
		if (mul > vulnMul)
			vulnMul = mul;
		if (duration > vulnT)
			vulnT = duration; // refresh only: two overlapping marks never stack
	}

	public void TickVuln(double delta)
	{
		if (vulnT > 0.0)
		{
			vulnT -= delta;
			if (vulnT <= 0.0)
				vulnMul = 0.0; // the mark decays with the window
		}
	}

	// DoT crossfire (user 2026-11-07 QoL: "make DoTs more impactful ... enemies take 5% more
	// damage based on the number of UNIQUE DoTs on them ... half this effect on bosses so
	// they don't get too easy"). Every UNIQUE DoT RUNNING on a body marks it +5% vulnerable
	// to ALL incoming damage - burn, bleed and poison today, and every future DoT pair slots
	// into UniqueDoTs() below. COUNTED AT THE HIT rather than stacked into vulnMul at apply
	// time: the Vulnerable() lane is max-mark/refresh-never-stack (guiding light invariant),
	// so three DoTs could never stack through it - while counting from the LIVE window pairs
	// gives the free invariants: re-striking the same venom never double-dips and an expired
	// DoT stops paying for free. Bosses ride the HALF bonus (user rule) via the kind list
	// that mirrors Game.IsBossKind (private there, and Combatant cannot reach Game).
	// Multiplies the same TakeDamage rail guiding light rides, so bites, bolts, bullets,
	// blasts and the DoT TICKS themselves all scale together.
	// +5% per unique DoT (assistant-carried USER number); bosses x0.5
	private static readonly double DOT_VULN = 0.05;

	public int UniqueDoTs()
	{
		// THE DoT SLOT RAIL: one count per PAIR RUNNING, never per stack. Every NEW DoT
		// pair on this surface adds its own line below and pays crossfire for free.
		int n = 0;
		if (burnT > 0.0)
			n++;
		if (bleedT > 0.0)
			n++;
		// Future DoT pairs add their own count line here (poison is already live below)
		// poison lives below in its own pair; count it via the window like burn/bleed
		if (poisonT > 0.0)
			n++;
		// Hapi's necrosis lives below in its own pair; count it via the window like burn/bleed/poison
		if (necrosisT > 0.0)
			n++;
		return n;
	}

	// (user rule) mirror of Game.IsBossKind: bosses pay HALF crossfire so they never
	// get too easy; mini/mid/final all ride this branch.
	// Combatant cannot reach Game's private helper, so the kind list is mirrored here.
	private bool IsBossBody()
	{
		return kind == "boss1" || kind == "boss2" || kind == "final" ||
			kind == "mini" || kind == "mini1" || kind == "mini2";
	}

	// Audit rail ONLY: (crossfire) prints fire on bonus CHANGES (a DoT landing or expiring),
	// never per hit - application/expiry events are rare even at horde density.
	// starts at 0.0: a clean body prints nothing, the first live mark on a DoT'd body does.
	private double _dotVBonus = 0.0;

	// draging maul (Baldrun 4th card, user 2026-10-24): the maul cleave taxes caught bodies -
	// body speed -% for a window. A BODY status, not a tag: it lives on the shared surface
	// so every verb could apply it, and only the maul applier enforces boss immunity. Stronger
	// mark wins, window refresh only - mirrors guiding light exactly, per user pick (slow
	// beats stun). Enemy.cs reads the pair at its three movement sites.
	public double slowMul = 0.0;
	public double slowT = 0.0;

	// firebolt burn (Stack's firebolt DoT, user 2026-10-30): bodies hit by a burning bolt
	// run burnDps damage/sec for burnT seconds. Kills still die through the Game's shared
	// kill scan. burnCinder is the CINDER stamp (cinder bloom rides the stronger flame): a body
	// that dies while still burning bursts in a small radius and scorches the bodies standing there
	// (Game.CinderBurst, hooked in the kill scan).
	public double burnDps = 0.0;
	public double burnT = 0.0;
	public double burnCinder = 0.0;

	// Bruno's bleeding edge (user 2026-10-31): its OWN status pair mirroring burn - bodies set
	// by a bleeding axe run bleedDps damage/sec for bleedT seconds. Deliberately SEPARATE from
	// burn so neither cinder bloom (which rides the burn stamp) nor the Drink "burning body"
	// bonus can key on it: the fire DoT lanes stay Stack-only. Kills ride the shared kill scan.
	public double bleedDps = 0.0;
	public double bleedT = 0.0;

	public void Burn(double dps, double dur, double cinder = 0.0)
	{
		if (dps <= 0.0 || dur <= 0.0)
			return; // a plain bolt never sets a window
		if (dps >= burnDps)
		{
			burnDps = dps;
			burnCinder = cinder; // the burst stamp rides the stronger flame, like the vuln mark does
		}
		if (dur > burnT)
			burnT = dur; // the window refreshes, never stacks
	}

	public void TickBurn(double delta)
	{
		if (burnT <= 0.0)
			return;
		burnT -= delta;
		if (burnT < 0.0)
			burnT = 0.0;
		TakeDamage(burnDps * delta); // burn kills ride the shared kill scan like any other
		if (burnT <= 0.0)
			burnDps = 0.0; // expiry clears the dps so a later WEAKER mark owns the tick (FIXED 2026-11-01: the stale rate kept ticking)
	}

	public void Bleed(double dps, double dur)
	{
		if (dps <= 0.0 || dur <= 0.0)
			return; // an axe without the card never sets a window
		if (dps >= bleedDps)
			bleedDps = dps; // the stronger edge owns the tick
		if (dur > bleedT)
			bleedT = dur; // the window refreshes, never stacks
	}

	public void TickBleed(double delta)
	{
		if (bleedT <= 0.0)
			return;
		bleedT -= delta;
		if (bleedT < 0.0)
			bleedT = 0.0;
		TakeDamage(bleedDps * delta); // bleed kills ride the shared kill scan like any other
		if (bleedT <= 0.0)
			bleedDps = 0.0; // expiry clears the dps (same fix as the burn pair)
	}

	// Brook's twin-dagger venom (user 2026-10-31): its OWN status pair mirroring burn/bleed -
	// bodies stabbed by a poisoned dagger run poisonDps damage/sec for poisonT seconds (user
	// kit: 1 dmg/s for 5 s, functionally the same DoT lane the fire/bleed pairs carry).
	// Deliberately SEPARATE so neither cinder bloom (which rides the burn stamp) nor the
	// Drink "burning body" bonus can key on it: the fire DoT lanes stay Stack-only.
	public double poisonDps = 0.0;
	public double poisonT = 0.0;

	public void Poison(double dps, double dur)
	{
		if (dps <= 0.0 || dur <= 0.0)
			return; // a clean dagger never sets a window
		if (dps >= poisonDps)
			poisonDps = dps; // the stronger venom owns the tick
		if (dur > poisonT)
			poisonT = dur; // the window refreshes, never stacks
	}

	public void TickPoison(double delta)
	{
		if (poisonT <= 0.0)
			return;
		poisonT -= delta;
		if (poisonT < 0.0)
			poisonT = 0.0;
		TakeDamage(poisonDps * delta); // poison kills ride the shared kill scan like any other
		if (poisonT <= 0.0)
			poisonDps = 0.0; // expiry clears the dps (same fix as the burn/bleed pairs)
	}

	// Hapi's necrotic flame (user 2026-11-17): its OWN status pair mirroring burn/bleed/poison -
	// bodies caught in a necrotic flame pillar run necrosisDps damage/sec for necrosisT seconds
	// (user kit: 1 dmg/s for 5 s). Deliberately SEPARATE so the cinder bloom / Drink "burning body"
	// / venom lanes can never key on it; the Plague pass-on rail keys on THIS pair only.
	// Kills ride the shared kill scan like any other.
	public double necrosisDps = 0.0;
	public double necrosisT = 0.0;

	public void Necrotic(double dps, double dur)
	{
		if (dps <= 0.0 || dur <= 0.0)
			return; // a plain pillar strike never sets a window
		if (dps >= necrosisDps)
			necrosisDps = dps; // the stronger flame owns the tick
		if (dur > necrosisT)
			necrosisT = dur; // the window refreshes, never stacks
	}

	public void TickNecrotic(double delta)
	{
		if (necrosisT <= 0.0)
			return;
		necrosisT -= delta;
		if (necrosisT < 0.0)
			necrosisT = 0.0;
		TakeDamage(necrosisDps * delta); // necrotic kills ride the shared kill scan like any other
		if (necrosisT <= 0.0)
			necrosisDps = 0.0; // expiry clears the dps (same fix as the burn/bleed/poison pairs)
	}

	public void Slowed(double mul, double duration)
	{
		if (mul > slowMul)
			slowMul = mul;
		if (duration > slowT)
			slowT = duration; // refresh only: two overlapping marks never stack
	}

	public void TickSlow(double delta)
	{
		if (slowT > 0.0)
		{
			slowT -= delta;
			if (slowT <= 0.0)
				slowMul = 0.0; // the mark decays with the window
		}
	}
	public void Hurt()
	{
		hurtFlash = 0.12f;
	}

	// ROUND 14 split: the public RAIL everyone rides keeps its exact signature; its body moved
	// below to TakeDamageRail (protected, NON-virtual) so Enemy.cs' armour override can pay the
	// plate and then reach this implementation without recursing through the dispatch. NOTE FOR
	// FUTURE LAPS: this compiler front-end has no base-call keyword - a `super.X` (or a class-
	// qualified Combatant.X) is rejected CS0103/CS0120 even in a clean SDK scratch, so a delegate
	// helper is the ONLY honest way down. The DoT tick lines above keep calling the WRAPPER, so
	// on plated bodies each tick rides the threshold gate before it reaches the rail.
	public virtual void TakeDamage(double amount)
	{
		TakeDamageRail(amount);
	}
	protected void TakeDamageRail(double amount) // the shared rail: vuln multiplier, DoT crossfire audit, hp loss, hurt flash
	{
		// guiding light: a marked body bites harder against whatever strikes it
		if (vulnT > 0.0)
			amount *= (1.0 + vulnMul);
		// DoT crossfire (user 2026-11-07 QoL): +5% incoming damage per UNIQUE DoT running on this
		// body, HALF that on bosses so they never get too easy. Counted AT THE HIT from the live
		// window pairs, so an expired DoT stops paying and re-striking the same venom never
		// double-dips. The DoT ticks themselves ride this rail too, so a body juggling burn +
		// bleed + poison takes +15% from everything (bosses +7.5%).
		int dots = UniqueDoTs();
		double cb = DOT_VULN * (double)dots;
		if (IsBossBody())
			cb *= 0.5; // (user rule) bosses pay half crossfire so they never get too easy
		amount *= (1.0 + cb);
		// audit rail: (crossfire) prints on bonus CHANGES (a DoT landing or expiring), never
		// per hit, so horde density stays quiet.
		if (cb != _dotVBonus)
		{
			bool was = _dotVBonus > 0.0;
			_dotVBonus = cb;
			if (cb > 0.0)
				GD.Print("[10thAge] (crossfire) " + kind + " runs " + dots + " unique DoT(s): +" +
					(int)(cb * 100.0 + 0.5) + "% incoming");
			else if (was)
				GD.Print("[10thAge] (crossfire) " + kind + " DoT crossfire expired");
		}
		hp -= (float)amount;
		Hurt();
	}
}
