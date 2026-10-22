using Godot;

/// <summary>
/// Horde director: budget-curve spawning of the horde around the camera, ramping inside the
/// CURRENT beat leg and gated by the horde tier the beat table set. One PLAY state covers
/// horde AND boss marches now, so the march only holds during camp / death / victory.
/// </summary>
public partial class Director : Godot.Node2D
{
    // ---------- horde director: budget-curve spawning around the camera ----------
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
        // M1 read the stage as raw ints and hand-picked which states spawned; the beat
        // table means there is one marching state, so ask the stage whether it marches.
        if (!g.GetHordeActive())
            return; // CAMPFIRE / DEAD / VICTORY hold the march back
        _spawnTimer -= (float)d;
        if (_spawnTimer > 0.0f)
            return;
        // user rule (human playtest 2026-10-22): the horde SCALES TO THE PARTY. A solo
        // Baldrun opening runs a thin swarm and every ally who joins thickens it (VS-style:
        // the survivors fund the horde). Normalized so M1's two-PC pacing is the 1.0 mark -
        // solo runs half-density, a full 5-PC squad runs 2.5x. This scales SPAWN VOLUME
        // (live cap AND cadence); body HP keeps its rest-only x1.20 price, so a small
        // squad faces fewer bodies, not fatter ones.
        float partyScale = (float)g.GetPartyInPlay() / 2.0f;
        // horde scale multiplies the cap too: a long-rest horde runs bigger AND denser
        float scale = (float)g.GetHordeScale();
        if (g.GetLiveEnemies() >= (int)(MAX_LIVE * scale * partyScale))
            return; // at cap: stop spawning, don't reset the timer either
        // the density curve ramps inside the CURRENT beat leg (len = next beat - this one),
        // so the schedule's pacing is data: move a beat's time and the ramp follows
        double t = g.GetStageT();
        double half = g.GetStageLen();
        _spawnTimer = (float)System.Math.Max(0.35, 1.1 - 0.6 * (t / half));
        float legFrac = (float)(half > 0.0 ? t / half : 0.0);
        // leg-start easing: a fresh beat leg opens thin and thickens toward the NEXT beat.
        // This is what M1's flat "half rate through the long pre-mini-boss window" did per
        // half; the beat table makes legs 20-40 s, so the easing rides the leg instead of a
        // hardcoded 90 s of stage.
        _spawnTimer *= legFrac < 0.4f ? 2.0f - (legFrac / 0.4f) : 1.0f;
        if (g.GetBossLive())
            _spawnTimer *= 2.0f; // boss phases (any of the four): horde at 50 % rate, pressure
                                  // without overwhelming (user pacing rule 2026-10-20)
        // the long rest's price lands here: horde scale densifies pacing AND beefs up
        // whatever the next leg spawns (Game._hordeScale -> GetHordeScale); the party
        // factor densifies pacing alongside it (solo = timer x2, 5 PCs = x0.4)
        _spawnTimer /= (scale * partyScale);
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
        // spawn just OUTSIDE the MEASURED frame: 800 px assumed a 960 half-width, and
        // on DPI-scaled windows that landed goblins right inside frame = pop-in
        Vector2 half = g.GetVisibleHalfExtents();
        Vector2 unit = new Vector2((float)System.Math.Cos(a), (float)System.Math.Sin(a));
        double tx = System.Math.Abs(unit.X) > 0.001 ? half.X / System.Math.Abs(unit.X) : 1e30;
        double ty = System.Math.Abs(unit.Y) > 0.001 ? half.Y / System.Math.Abs(unit.Y) : 1e30;
        double reach = System.Math.Min(tx, ty) + 60.0;
        Vector2 pos = cen + unit * (float)reach;
        if (g.GetLeader() != null && pos.DistanceTo(g.GetLeader().GlobalPosition) < 260.0f)
            return; // an arena clamp may have dragged the spawn into the party: skip, the
                    // timer already burned this roll
        // user tier gate: the horde runs whatever tier the beat table last advanced. Tier 1
        // goblins | tier 2 wolves + hobgoblins | tiers 3 and 4 are NOT DESIGNED YET - the
        // user will dictate those creatures, so until they land a tier-3/4 leg draws the
        // tier-2 bodies with an elite HP bump and says so. Flagged placeholder, not invented
        // content: nothing here pretends a creature nobody approved exists.
        int tier = (int)g.GetHordeTier();
        bool tier2 = tier >= 2;
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
        float tierBump = tier >= 4 ? 1.6f : (tier >= 3 ? 1.3f : 1.0f);
        e.maxHp = (wolf ? 70.0f : (hob ? 60.0f : 30.0f)) * hordeScale * tierBump;
        e.hp = e.maxHp;
        e.speed = wolf ? 120.0f : (hob ? 100.0f : 90.0f);
        e.contactDamage = wolf ? 3.0f : (hob ? 3.0f : 2.0f);
        e.xpValue = wolf ? 2.0f : (hob ? 3.0f : 1.0f);
        e.GlobalPosition = pos;
        g.RegisterEnemy(e);
        g.AddChild(e);
    }
}
