using Godot;

/// <summary>
/// Canonical stat registry (user 2026-10-24: "set tags for all stats in the game
/// ... base for permanent meta progression"). These indices are the ONE vocabulary
/// the whole codebase uses - nobody gets to invent another name for armour again.
/// Party store: PartyMember.stat[] seeded from BASE below; upgrade cards and any
/// future meta/unlock system mutate ONLY that store, never the [Export] bases in
/// Main.tscn. Body-side tags: VULNERABILITY lives on Combatant (vulnMul/vulnT,
/// decay via TickVuln). ARMOUR/EVASION roll inside Game.TakeHit.
/// Kind codes: 0 = MULTIPLIER (grows on top of the member's authored base, seed
/// 1.0), 1 = POINTS (raw amount added on top of base), 2 = COUNT (whole strikes).
/// Pure constants on the Godot script class: display strings live in NAMES, and
/// every reader walks the arrays with its own loop (no helper methods on this
/// registry class - Godot script classes and statics keep the data honest).
/// </summary>
public class StatTags
{
	public static readonly int ATTACK_DAMAGE = 0;
	public static readonly int ATTACK_SPEED = 1;
	public static readonly int AREA = 2;
	public static readonly int CRIT_CHANCE = 3;
	public static readonly int CRIT_DAMAGE = 4;
	public static readonly int MULTISTRIKE = 5;
	public static readonly int LIFESTEAL = 6;
	public static readonly int HEALTH = 7;
	public static readonly int ARMOUR = 8;
	public static readonly int EVASION = 9;
	public static readonly int REGEN = 10;
	public static readonly int XP_PCT = 11;
	public static readonly int GOLD_PCT = 12;
	public static readonly int COLLECTION_RANGE = 13;
	public static readonly int MOVEMENT_SPEED = 14;
	public static readonly int VULNERABILITY = 15;
	public static readonly int N = 16;

	// seed table: multipliers at 1.0, counts at 1 strike, dormant stats at zero;
	// everyone crits at 5% for x1.5 (user 2026-10-24)
	public static readonly double[] BASE = new double[] { 1, 1, 1, 0.05, 1.5, 1, 0, 0,
														  0, 0, 0, 0, 0, 1, 1, 0 };

	// CATEGORY for meta screens: offense x7, defense x4, utility x4, modifier x1
	// (vulnerability is the body-side debuff Combatant already carries)
	public static readonly string[] CATEGORY = new string[] { "offense", "offense", "offense",
															  "offense", "offense", "offense", "offense",
															  "defense", "defense", "defense", "defense",
															  "utility", "utility", "utility", "utility",
															  "modifier" };

	// KIND: 0 = multiplier, 1 = points, 2 = count (how grants combine on the seed)
	public static readonly int[] KIND = new int[] { 0, 0, 0, 1, 0, 2, 1, 1, 1, 1,
													1, 1, 1, 0, 0, 1 };

	// NAMES are display strings for meta screens / future card tables
	// (the registry owns the wording, not any card array)
	public static readonly string[] NAMES = new string[] { "attack damage", "attack speed",
														  "area", "crit chance", "crit damage",
														  "multistrike", "lifesteal", "health",
														  "armour", "evasion", "hp regen",
														  "xp %", "gold %", "collection range",
														  "movement speed", "vulnerability" };
		// xp % and gold % rows print even while dormant so future meta work can find them
		// dormant rows print too: meta progression must see the whole board, not
		// only the stats that currently have values
		// NAMES is what a meta screen prints; CATEGORY + KIND tell a system HOW the
		// tag combines (kind 0 multiplies, kind 1 adds points, kind 2 adds strikes)
		// the registry is data-only on purpose: any future grant system reads
		// NAMES/KIND/BASE instead of re-declaring stat names anywhere
		// party-side tags all live in PartyMember.stat[] at these indices; the
		// body-side vulnerability tag pairs with Combatant.vulnMul
		// GOLD_PCT is the deliberate dormant row: no gold economy exists yet, the
		// slot is so meta progression already has something to multiply later
		// registry tail: one close for the class - the class body is fields-only now
		// (the earlier method stubs kept coming out malformed from write, so the
		// boot dump loops below live in Game.cs where the smoke can see them)
	}
