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
