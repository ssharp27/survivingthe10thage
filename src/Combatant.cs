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

    // draging maul (Baldrun 4th card, user 2026-10-24): the maul cleave taxes caught bodies -
    // body speed -% for a window. A BODY status, not a tag: it lives on the shared surface
    // so every verb could apply it, and only the maul applier enforces boss immunity. Stronger
    // mark wins, window refresh only - mirrors guiding light exactly, per user pick (slow
    // beats stun). Enemy.cs reads the pair at its three movement sites.
    public double slowMul = 0.0;
    public double slowT = 0.0;

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

    public virtual void TakeDamage(double amount)
    {
        // guiding light: a marked body bites harder against whatever strikes it
        if (vulnT > 0.0)
            amount *= (1.0 + vulnMul);
        hp -= (float)amount;
        Hurt();
    }
}
