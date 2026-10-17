using Godot;

/// <summary>
/// One party member: HP, auto-attack driven by signature-weapon parameters, and the
/// down/death-save/revive state fields (the state machine itself lives in Game).
/// Baldrun = wide slow maul arc; Mosiah = narrow fast rapier line.
/// </summary>
public partial class PartyMember : Godot.Node2D
{
    public string displayName = "Member";
    public string weaponName = "Maul";
    public int memberIndex = 0;
    public bool isLeader = false;

    public float maxHp = 60.0f;
    public float baseSpeed = 185.0f;

    public float weaponDamage = 10.0f;
    public float cooldown = 1.15f;
    public float range = 95.0f;
    public float arcHalf = 1.05f; // half-arc radians: wide = maul, narrow = rapier

    public float hp = 0.0f;
    public bool downed = false;
    public bool eliminated = false;
    float cdTimer = 0.0f;
    public float facing = 0.0f;
    public float dmgMul = 1.0f;
    public float cdMul = 1.0f;
    public float rangeMul = 1.0f;
    public float speedMul = 1.0f;

    // down/death-save state (driven by the Game root)
    public float dsTimer = 0.0f;
    public int dsFails = 0;
    public int dsSucceeds = 0;
    public float reviveProgress = 0.0f;

    Godot.ColorRect _body;
    Godot.Label _ds;
    float _flash = 0.0f;

    public override void _EnterTree()
    {
        AddToGroup("party");
        hp = maxHp;
        _body = new ColorRect();
        _body.Size = new Vector2(30.0f, 30.0f);
        _body.Position = -_body.Size * 0.5f;
        _body.Color = memberIndex == 0 ? new Color(0.92f, 0.92f, 0.95f)
                                       : new Color(0.78f, 0.63f, 0.44f);
        AddChild(_body);
        _ds = new Label();
        _ds.Position = new Vector2(-24.0f, -40.0f);
        AddChild(_ds);
    }

    public void SetDsText(string s)
    {
        if (_ds != null)
            _ds.Text = s;
    }

    public override void _Process(double d)
    {
        float delta = (float)d;
        if (downed || eliminated)
            return;
        if (_flash > 0.0f)
        {
            _flash -= delta;
            _body.Modulate = new Color(3.0f, 3.0f, 3.0f);
        }
        else
            _body.Modulate = new Color(1.0f, 1.0f, 1.0f);
        cdTimer -= delta;
        if (cdTimer > 0.0f)
            return;
        Vector2 p = GlobalPosition;
        Game pg = GetParent() as Game;
        if (pg == null)
            return;
        Godot.Collections.Array<Godot.Node2D> enemies = pg.GetEnemies();
        Godot.Node2D best = null;
        float bestD = 1e9f;
        for (int i = 0; i < enemies.Count; i++)
        {
            Godot.Node2D n = enemies[i];
            if (((Combatant)n).kind == "dead")
                continue;
            float dd = n.GlobalPosition.DistanceTo(p);
            if (dd < bestD)
            {
                bestD = dd;
                best = n;
            }
        }
        if (best == null || bestD > range * rangeMul)
            return;
        cdTimer = cooldown * cdMul;
        Vector2 to = best.GlobalPosition - p;
        float ang = (float)System.Math.Atan2(to.Y, to.X);
        if (System.Math.Abs(angleDiff(ang, facing)) <= arcHalf)
        {
            float dmg = weaponDamage * dmgMul;
            if (arcHalf > 0.5f)
            {
                // Maul: every enemy within reach AND inside the arc (area damage)
                for (int i = 0; i < enemies.Count; i++)
                {
                    Godot.Node2D n = enemies[i];
                    if (((Combatant)n).kind == "dead")
                        continue;
                    Vector2 e = n.GlobalPosition - p;
                    float dd = e.Length();
                    if (dd <= range * rangeMul && System.Math.Abs(angleDiff((float)System.Math.Atan2(e.Y, e.X), facing)) <= arcHalf)
                        ((Combatant)n).TakeDamage(dmg);
                }
            }
            else
            {
                // Rapier: single precise target (narrow arc enforced above)
                ((Combatant)best).TakeDamage(dmg);
            }
            _flash = 0.12f;
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
