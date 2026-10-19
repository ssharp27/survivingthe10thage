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
        if (g.GetPickerActive())
            return; // picker soft pause: the horde holds its march while the card is up
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
        _spawnTimer = (float)System.Math.Max(0.35, 1.1 - 0.6 * (t / half));
        if (st == 3)
            _spawnTimer *= 0.67f; // post-mini-boss: the horde runs 50 % denser
        // the long rest's price lands here: horde scale densifies pacing AND beefs up
        // whatever the next stage spawns (Game._hordeScale -> GetHordeScale)
        float scale = (float)g.GetHordeScale();
        _spawnTimer /= scale;
        Vector2 near = g.GetLeader() != null ? g.GetLeader().GlobalPosition : new Vector2(0, 0);
        Spawn(g, near, scale);
    }

    private void Spawn(Game g, Vector2 near, float hordeScale)
    {
        // spawn just outside the CAMERA view (half-width 640), not 640 px from the
        // leader: the old ring landed inside the visible frame so bodies materialized
        // on top of the squad
        Vector2 cen = g.GetCameraCenter();
        float a = (float)GD.Randf() * (float)(System.Math.PI * 2.0);
        Vector2 pos = g.ClampToArena(cen + new Vector2((float)System.Math.Cos(a), (float)System.Math.Sin(a)) * 800.0f);
        if (g.GetLeader() != null && pos.DistanceTo(g.GetLeader().GlobalPosition) < 260.0f)
            return; // an arena clamp may have dragged the spawn into the party: skip, the
                    // timer already burned this roll
        bool wolf = (float)GD.Randf() < 0.30f;
        Enemy e = new Enemy();
        e.kind = wolf ? "wolf" : "goblin";
        // HP no longer sits under the maul's base swing: a greybox trash hit should take
        // more than one clean strike, so the live cap can actually bind (horde scale only
        // bumps this on a long rest)
        e.maxHp = (wolf ? 96.0f : 48.0f) * hordeScale;
        e.hp = e.maxHp;
        e.speed = wolf ? 120.0f : 90.0f;
        e.contactDamage = wolf ? 3.0f : 2.0f;
        e.xpValue = wolf ? 2.0f : 1.0f;
        e.GlobalPosition = pos;
        g.RegisterEnemy(e);
        g.AddChild(e);
    }
}
