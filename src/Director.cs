using Godot;

/// <summary>
/// Horde director for M1: budget-curve spawning of goblins/wolves around the leader,
/// ramping within each half; paused during campfire and boss phases.
/// </summary>
public partial class Director : Godot.Node2D
{
    // ---------- horde director for M1: budget-curve spawning of goblins/wolves around the
    // leader, ramping within each half; paused during campfire and boss phases ----------
    float _spawnTimer = 4.0f;
    // VS-authentic on-screen cap: the horde applies pressure but can't snowball past this
    const int MAX_LIVE = 40;

    public override void _Process(double d)
    {
        Game g = GetParent() as Game;
        if (g == null)
            return;
        int st = g.GetStageState();
        if (st != 0 && st != 3) // PLAY1 / PLAY2 only
            return;
        _spawnTimer -= (float)d;
        if (_spawnTimer > 0.0f)
            return;
        if (g.GetLiveEnemies() >= MAX_LIVE)
            return; // at cap: stop spawning, don't reset the timer either
        double t = g.GetStageT();
        double half = st == 0 ? g.GetFirstHalf() : g.GetSecondHalf();
        _spawnTimer = (float)System.Math.Max(0.5, 2.0 - 1.1 * (t / half));
        Vector2 near = g.GetLeader() != null ? g.GetLeader().GlobalPosition : new Vector2(0, 0);
        Spawn(g, near);
    }

    private void Spawn(Game g, Vector2 near)
    {
        float a = (float)GD.Randf() * (float)(System.Math.PI * 2.0);
        Vector2 pos = g.ClampToArena(near + new Vector2((float)System.Math.Cos(a), (float)System.Math.Sin(a)) * 640.0f);
        bool wolf = (float)GD.Randf() < 0.30f;
        Enemy e = new Enemy();
        e.kind = wolf ? "wolf" : "goblin";
        e.maxHp = wolf ? 26.0f : 18.0f; // maul (26 dmg) one-shots both archetypes
        e.hp = e.maxHp;
        e.speed = wolf ? 150.0f : 78.0f;
        e.contactDamage = wolf ? 7.0f : 4.0f;
        e.xpValue = wolf ? 2.0f : 1.0f;
        e.GlobalPosition = pos;
        g.RegisterEnemy(e);
        g.AddChild(e);
    }
}
