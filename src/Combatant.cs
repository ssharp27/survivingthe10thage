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

    public void Hurt()
    {
        hurtFlash = 0.12f;
    }

    public virtual void TakeDamage(double amount)
    {
        hp -= (float)amount;
        Hurt();
    }
}
