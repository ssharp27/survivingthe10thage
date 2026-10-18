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
    Godot.Node2D _telePivot; // telegraph armature (pure nodes: C# _draw is dead in 4.7)
    Godot.ColorRect[] _teleParts;
    float _teleHold = 0.0f; // telegraph visibility timer, set on each swing
    public float facing = 0.0f;
    public float dmgMul = 1.0f;
    public float cdMul = 1.0f;
    public float rangeMul = 1.0f;
    public float speedMul = 1.0f;

    // down/death-save state (driven by the Game root)
    public float dsTimer = 0.0f;
    public int dsFails = 0;
    public int dsSucceeds = 0; // rolls of 11-19; 3 of them = stable
    public bool stable = false; // 3 successes: rolls stop, hits now slay
    public float reviveProgress = 0.0f; // leader stand-still channel
    public float dsDamageBuffer = 0.0f; // 2 s gap between damage-caused death-save fails
    public float dsHold = 0.0f; // countdown for SetDsTemp texts; 0 = persistent

    Godot.ColorRect _body;
    Godot.Label _ds;
    float _flash = 0.0f;

    public override void _EnterTree()
    {
        AddToGroup("party");
        ZIndex = 5; // members (and their _draw telegraphs) paint above enemy squares
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
        // telegraph: a pivot + thin ColorRect "spokes" radiating along facing. The
        // maul shows the fan edges + middle; the rapier one solid stab line. Pure
        // nodes because both the C# _draw hook and drawing outside _draw are dead
        // ends in 4.7.
        _telePivot = new Godot.Node2D();
        _telePivot.Name = "Telegraph";
        AddChild(_telePivot);
        bool circle = arcHalf > 3.0f; // Baldrun's maul is a true 360 deg sweep now
        bool maul = arcHalf > 0.5f;
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

    public override void _Process(double d)
    {
        float delta = (float)d;
        if (dsDamageBuffer > 0.0f)
            dsDamageBuffer -= delta; // ticks even while downed: the 2 s fail-buffer
        if (downed || eliminated)
            return;
        if (_flash > 0.0f)
        {
            _flash -= delta;
            // red tint, not a white multiply: Baldrun's near-white square barely
            // changed under a 3x white modulate
            _body.Modulate = new Color(1.0f, 0.35f, 0.35f);
        }
        else
            _body.Modulate = new Color(1.0f, 1.0f, 1.0f);
        cdTimer -= delta;
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
            Combatant nc = (Combatant)n;
            if (nc == null || nc.kind == "dead")
                continue;
            Vector2 v = n.GlobalPosition - p;
            float dd = v.Length();
            if (dd >= bestD)
                continue;
            // The leader swings where it walks, so it may only target what its facing
            // arc covers (touching enemies always count). Targeting the absolute
            // nearest and then bailing when it fell outside the arc starved both
            // members dead the moment a player stood still and the horde wrapped
            // around behind them.
            if (isLeader && dd >= 40.0f &&
                System.Math.Abs(angleDiff((float)System.Math.Atan2(v.Y, v.X), facing)) > arcHalf)
                continue;
            bestD = dd;
            best = n;
        }
        if (best == null || bestD > range * rangeMul)
            return;
        cdTimer = cooldown * cdMul;
        Vector2 to = best.GlobalPosition - p;
        float ang = (float)System.Math.Atan2(to.Y, to.X);
        if (!isLeader)
            facing = ang; // ring members auto-aim: the bodyguard bites its prey
        float dmg = weaponDamage * dmgMul;
        if (arcHalf > 0.5f)
        {
            // Maul: every enemy within reach AND inside the arc (area damage)
            for (int i = 0; i < enemies.Count; i++)
            {
                Godot.Node2D n = enemies[i];
                Combatant nc = (Combatant)n;
                if (nc == null || nc.kind == "dead")
                    continue;
                Vector2 e = n.GlobalPosition - p;
                float dd = e.Length();
                if (dd <= range * rangeMul && System.Math.Abs(angleDiff((float)System.Math.Atan2(e.Y, e.X), facing)) <= arcHalf)
                    nc.TakeDamage(dmg);
            }
        }
        else
            ((Combatant)best).TakeDamage(dmg); // Rapier: single precise target
        _flash = 0.12f;
        _telePivot.Rotation = facing;
        _telePivot.Scale = new Vector2(rangeMul, rangeMul);
        _teleHold = 0.18f;
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
