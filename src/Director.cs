using Godot;

/// <summary>
/// Horde director for M1: budget-curve spawning of goblins/wolves/hobgoblins around the
/// leader, ramping within each half; paused during campfire and boss phases.
/// </summary>
public partial class Director : Godot.Node2D
{
    // ---------- horde director for M1: budget-curve spawning of goblins/wolves/hobgoblins
    // around the leader, ramping within each half; paused during campfire and boss phases ----------
    float _spawnTimer = 4.0f;
    // VS-authentic on-screen cap: horde scale (long rest) multiplies this too, so a
    // long rest spawns MORE live bodies, not just faster ones (user rule)
    const int MAX_LIVE = 40;
    // one hobgoblin per ten goblins spawned (user rule); counts real spawns only,
    // rolls rejected by the near-party guard burn the timer but not this counter
    int _goblinsSinceHob = 0;

    public override void _Process(double d)
    {
        Game g = GetParent() as Game;
        if (g == null)
            return;
        if (g.GetWorldPause())
            return; // picker / join-banner soft pause: the horde holds its march while a card is up
        int st = g.GetStageState();
        if (st != 0 && st != 3) // PLAY1 / PLAY2 only
            return;
        _spawnTimer -= (float)d;
        if (_spawnTimer > 0.0f)
            return;
        // horde scale multiplies the cap too: a long-rest horde runs bigger AND denser
        float scale = (float)g.GetHordeScale();
        if (g.GetLiveEnemies() >= (int)(MAX_LIVE * scale))
            return; // at cap: stop spawning, don't reset the timer either
        double t = g.GetStageT();
        double half = st == 0 ? g.GetFirstHalf() : g.GetSecondHalf();
        _spawnTimer = (float)System.Math.Max(0.35, 1.1 - 0.6 * (t / half));
        if (st == 3)
            _spawnTimer *= 0.67f; // post-mini-boss: the horde runs 50 % denser
        else if (st == 0)
            _spawnTimer *= 2.0f;  // user pacing (2026-10-20): half rate through the long
                                  // pre-mini-boss window - pressure stays, the count does not
        // the long rest's price lands here: horde scale densifies pacing AND beefs up
        // whatever the next stage spawns (Game._hordeScale -> GetHordeScale)
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
        // user tier gate (2026-10-20): ONLY tier 1 (goblins) until TIER2_AT run-seconds,
        // then tier 2 (wolves + hobgoblins) mixes in. Tier 3/4 hooks land with later stages
        // - variety without handing a solo Leader a wolf pack at t=10s.
        bool tier2 = g.GetRunT() >= g.GetTier2At();
        bool wolf = tier2 && (float)GD.Randf() < 0.30f;
        bool hob = false;
        if (!wolf)
        {
            // user rule: one hobgoblin per ten goblins spawned
            _goblinsSinceHob++;
            if (tier2 && _goblinsSinceHob >= 10)
            {
                hob = true;
                _goblinsSinceHob = 0;
            }
        }
        Enemy e = new Enemy();
        e.kind = wolf ? "wolf" : (hob ? "hobgoblin" : "goblin");
        // user-approved balance round: goblin 30 / hobgoblin 60 / wolf 70 base HP,
        // still over a single hit on a packed cleave but a lone target dies in one
        // clean maul swing (30 dmg) - horde scale only bumps this on a long rest
        e.maxHp = (wolf ? 70.0f : (hob ? 60.0f : 30.0f)) * hordeScale;
        e.hp = e.maxHp;
        e.speed = wolf ? 120.0f : (hob ? 100.0f : 90.0f);
        e.contactDamage = wolf ? 3.0f : (hob ? 3.0f : 2.0f);
        e.xpValue = wolf ? 2.0f : (hob ? 3.0f : 1.0f);
        e.GlobalPosition = pos;
        g.RegisterEnemy(e);
        g.AddChild(e);
    }
}
