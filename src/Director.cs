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
        // TIER-3 spawn cadence (user 2026-11-03 creatures): assistant-picked knobs, tune after playtest.
        const float RIDER_PACK_CH = 0.05f; // per-spawn-roll chance a sword/bow pack enters on a tier-3+ leg (assistant pick: 0.16 TPKed Bruno, 0.08 still bled; retunable post-playtest)
        int _hobsSinceMage = 0; // USER RULE: one mage per two hobgoblin spawns
        // TIER-4 OGRE (user 2026-11-05: this map's only tier-4 creature): per-spawn-roll
        // chance an ogre lumbers in on a tier-4+ leg. Budgeted ORDINARY spawn like the
        // rider rolls, so the cap binds it. Assistant-picked cadence knob.
        const float OGRE_CH = 0.05f; // user 2026-11-06 playtest: ~1 ogre per 15-20 s of spawn rolls (they stack and the slam chains killed fast)
        // TIER VARIETY (user 2026-11-07 creatures): tier-1 GOBLIN 2 clump flavour + tier-2
        // FAST GOBLIN with the rush/pause/lunge lane. Entry odds are assistant-picked knobs.
        const float G2_CH = 0.5f;  // per plain-goblin slot: chance the body enters as Goblin 2
        const float FAST_CH = 0.2f; // per-spawn roll on a tier-2+ leg: a fast goblin enters
        // WOLF FAMILY (user 2026-11-09): the 30% wolf roll now splits three ways - a wolf2
        // PACK of 3 (long-sight charge), a lone tanky wolf3, or the standard wolf. The family
        // lane stays 0.30 so pacing is unchanged; the split inside it is an assistant-picked
        // cadence knob, retunable by eye.
        const float WOLF_PACK_CH = 0.35f; // of the wolf lane: the roll enters as a wolf2 pack of 3 (USER pack size)
        const float WOLF3_CH = 0.30f;     // of what remains: a lone tanky wolf3
    // EARLY OGRE LOOK (user 2026-11-06: "spawn an ogre in at the start of the run so I
    // can test how it works much earlier"): the FIRST ordinary spawn of a run is forced
    // to be the ogre, so the telegraph + SLAM are on screen inside the first minute
    // instead of waiting for a tier-4 leg AND an OGRE_CH hit. TEST KNOB, not balance:
    // set false and the tier-4 OGRE_CH roll below owns ogre entry again (the shipped
    // rule). Retunable with the rest of the tier cadence.
    const bool OGRE_AT_RUN_START = false; // user 2026-11-06 playtest: "remove the one at boot" - the forced opener is gone, the tier-4 roll owns ogre entry again
    bool _ogreAtStart = OGRE_AT_RUN_START; // consumed by the first ordinary spawn
    // SPAWN RAMP (user 2026-11-12: "increase the enemy spawn numbers, including the maximum
    // allowed, by 5% every minute the run is ongoing."): stepped per COMPLETED minute (1.05
    // at min 1, 1.30 at min 6). The multiplier drives the live cap, the cadence divisor, the
    // horde-event swarm size, and the pack units (the 2+2 rider and 3-wolf2 USER packs grow
    // with it). Assistant-picked mapping of "numbers", retunable after hand-play.
    private static double SpawnRamp(Game g)
    {
        return 1.0 + 0.05 * System.Math.Floor(System.Math.Max(0.0, g.GetRunTime()) / 60.0);
    }
    int _hordeEventsSeen = 0; // HORDE MIX LAW cursor: the FIRST bonus swarm stays pure tier-1 goblins (the opening rule), later swarms mix active tiers

    // ---------- ENEMY TEST ROOM (user 2026-11-07: "let's build the enemy test room.
    // Functionally ... spawn an enemy every 15-20 seconds ... rotate through every enemy
    // for stage 1 at the moment for testing") ----------
    // The room is NOW hand-picked from an ordinary launch: the stage select card seats it
    // (CloseStage flips _testRoom off the registry), so a human never has to prefix an env
    // valve. M1_TEST_ROOM=1 stays as the bench/dev door so headless gates skip the card.
    // In the room the budget
    // curve / tier gates / escort hordes / horde EVENTS all stay OUT: one body every
    // 15-20 s cycles the FULL stage-1 roster (TEST_KINDS) at base stats, so every AI
    // lane gets a turn on the shortest honest clock. Budgeted ordinary spawns (one
    // body vs cap 40: the cap never binds here).
    float _testTimer = 2.0f; // first body lands ~2 s in so the room reads alive immediately
    int _testIdx = 0;        // rotation cursor: one cycle = the whole stage-1 ladder
    static readonly string[] TEST_KINDS = { "goblin", "goblin2", "wolf", "fastgoblin", "hobgoblin", "rider", "riderbow", "mage", "ogre", "ogre2" }; // every stage-1 body: the two rider kinds and the mage ride as LONE test bodies (no packs) so each AI lane gets a clean turn
    const float TEST_EVERY_LO = 15.0f; // USER cadence: "every 15-20 seconds"
    const float TEST_EVERY_HI = 20.0f;

    public override void _Process(double d)
    {
        Game g = GetParent() as Game;
        if (g == null)
            return;
        if (g.GetWorldPause())
            return; // picker / join-banner soft pause: the horde holds its march while a card is up
        // ENEMY TEST ROOM: the rotation lane owns the whole march - the budget curve, tier gates,
        // escort hordes, packs and horde events never run, so each stage-1 kind gets one clean
        // turn on the shortest honest clock.
        if (g.GetTestRoom())
        {
            TestRotation(g, (float)d);
            return;
        }
        // M1 read the stage as raw ints and hand-picked which states spawned; the beat
        // table means there is one marching state, so ask the stage whether it marches.
        if (!g.GetHordeActive())
            return; // CAMPFIRE / DEAD / VICTORY hold the march back
        // HORDE EVENT (user 2026-11-02): the budget-free bonus swarm. Game arms the schedule at
        // the FIRST mini-boss kill; it fires ONLY while the horde actually marches (camp + picker
        // pauses hold it back), and placement lives HERE on the same edge-ray lane as ordinary
        // spawns - that is the infrastructure the user asked for to reuse hordes on other maps.
        if (g.GetHordeEventDue() > 0.0 && g.GetRunTime() >= g.GetHordeEventDue())
            SpawnHordeEvent(g, (float)g.GetHordeScale(), (int)g.GetHordeTier());
        _spawnTimer -= (float)d * (float)g.GetTimeWarp(); // TEST M1_TIMEWARP: cadence charges on the RUN clock so legs keep their NORMAL spawn budgets at warp speed
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
        float ramp = (float)SpawnRamp(g); // 2026-11-12 (user): COUNTS + the max allowed grow +5% per run minute
        if (g.GetLiveEnemies() >= (int)(MAX_LIVE * scale * partyScale * ramp))
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
        _spawnTimer /= (scale * partyScale * ramp); // 2026-11-12 ramp: spawn NUMBERS charge +5%/min as well as the cap
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
        if (_ogreAtStart)
        {
            // EARLY OGRE LOOK (see the OGRE_AT_RUN_START knob): the forced first body of
            // the run. Ordinary budgeted spawn on the SAME off-camera edge ray as every
            // other body, so the ogre simply lumbers in from the frame border.
            _ogreAtStart = false;
            SpawnOgre(g, pos, hordeScale);
            return;
        }
        // user tier gate: the horde runs whatever tier the beat table last advanced. Tier 1
        // goblins | tier 2 wolves + hobgoblins | TIER 3 NOW DESIGNED (user 2026-11-03: sword +
        // bow wolf riders and the goblin mage, entering as packs and hob counts). Tier 4 is
        // TIER 4 NOW DESIGNED (user 2026-11-05: the ogre is this map's only tier-4 creature,
        // entering on OGRE_CH rolls below; the pool bump is retired, see tierBump).
        int tier = (int)g.GetHordeTier();
        bool tier3 = tier >= 3;
        if (tier3 && (float)GD.Randf() < RIDER_PACK_CH)
        {
            // TIER-3 RIDES (user 2026-11-03): "have them spawn in a small pack with bow
            // wolf riders" - one roll enters TWO sword + TWO bow mounts fanned around the
            // roll point, never lone riders. The 0.16 CH is the assistant-picked cadence knob.
            SpawnRiderPack(g, pos, hordeScale, tier);
            return;
        }
        if (tier >= 4 && (float)GD.Randf() < OGRE_CH)
        {
            // TIER-4 OGRE roll: ordinary budgeted spawn, so the near-party guard already
            // burned pos upstream
            SpawnOgre(g, pos, hordeScale);
            return;
        }
        bool wolf = tier >= 2 && (float)GD.Randf() < 0.30f;
        // WOLF FAMILY (user 2026-11-09): the wolf roll splits - wolf2 pack / wolf3 / standard
        // wolf. wolf3 keeps the wolf flag set so the goblin-vs-hob counter still reads it as
        // not-a-goblin, and the pack owns this budgeted roll outright.
        bool wolf3 = false;
        if (wolf)
        {
            float wf = (float)GD.Randf();
            if (wf < WOLF_PACK_CH)
            {
                SpawnWolfPack(g, pos, hordeScale);
                return; // the pack owns this budgeted roll
            }
            wolf3 = wf < WOLF_PACK_CH + WOLF3_CH;
        }
        // TIER-2 FAST GOBLIN (user 2026-11-07): own roll between the wolf and goblin lanes;
        // ordinary budgeted spawn like the rider rolls (the near-party guard already burned
        // pos upstream). NOT counted by the hob counter: "one hob per ten goblins" counts the
        // goblin family (goblin/goblin2), not the fast variant.
        if (!wolf && tier >= 2 && (float)GD.Randf() < FAST_CH)
        {
            Enemy fe = new Enemy();
            fe.kind = "fastgoblin";
            fe.maxHp = 40.0f * hordeScale; // USER number
            fe.hp = fe.maxHp;
            fe.speed = 110.0f;       // USER number
            fe.contactDamage = 3.0f; // USER number
            fe.xpValue = 2.0f;       // USER number
            fe.GlobalPosition = pos;
            g.RegisterEnemy(fe);
            g.AddChild(fe);
            return;
        }
        bool hob = false;
        if (!wolf)
        {
            _goblinsSinceHob++;
            if (tier >= 2 && _goblinsSinceHob >= 10)
            {
                hob = true;
                _goblinsSinceHob = 0;
            }
        }
        Enemy e = new Enemy();
        // TIER-1 VARIETY (user 2026-11-07): half the plain-goblin slots enter as Goblin 2
        // (25 hp / 100 speed / contact 2 / 1 xp = USER numbers) - pure clump flavour.
        bool g2 = !wolf && !hob && (float)GD.Randf() < G2_CH;
        e.kind = wolf ? (wolf3 ? "wolf3" : "wolf") : (hob ? "hobgoblin" : (g2 ? "goblin2" : "goblin"));
        // real tier-3 bodies landed, so the tier-2 pool holds its base HP on tier-3 legs;
        // tier 4 keeps the flagged placeholder bump on the pool (assistant pick - retunable).
        float tierBump = 1.0f; // tier-4 placeholder bump retired: real tier-4 bodies entered (the ogre), so pool bodies keep base HP
        e.maxHp = (wolf3 ? 100.0f : (wolf ? 70.0f : (hob ? 60.0f : (g2 ? 25.0f : 30.0f)))) * hordeScale * tierBump; // USER numbers
        e.hp = e.maxHp;
        e.speed = wolf3 ? 100.0f : (wolf ? 120.0f : (hob ? 100.0f : (g2 ? 100.0f : 90.0f)));
        e.contactDamage = wolf3 ? 4.0f : (wolf ? 3.0f : (hob ? 3.0f : 2.0f)); // wolf3 bites harder (USER); Goblin 2 chips 2 like the clump (USER)
        e.xpValue = wolf3 ? 3.0f : (wolf ? 2.0f : (hob ? 3.0f : 1.0f)); // wolf3 carries the heavier 3 xp gem (USER); Goblin 2 carries the same 1 xp gem (USER)
        e.GlobalPosition = pos;
        g.RegisterEnemy(e);
        g.AddChild(e);
        // fx-feel10 (user 2026-11-03: mages spawn early and never attack): hobs run from tier 2,
        // but the goblin mage is a TIER-3 creature - the user's creature pass locks it to the tier-3
        // legs (210 s + tier 4), so the mage counter only counts hobs while tier3 is true.
        if (hob && tier3)
        {
            // USER RULE: one goblin mage per TWO hobgoblin spawns (fx-feel10 user 2026-11-03 override:
            // mages are tier 3, so the counter+spawn now run ONLY on tier-3+ legs - never early).
            _hobsSinceMage++;
            if (_hobsSinceMage >= 2)
            {
                _hobsSinceMage = 0;
                SpawnMage(g, pos, hordeScale, tier);
            }
        }
    }

    // TIER-3 LANES (user 2026-11-03): the USER numbers are HP/speed/contact/XP per the
    // dictation above; offsets inside the pack and the "90 px south" mage nudges are
    // assistant picks. Bodies ride the budget (riders/mages are ordinary spawns, not
    // bonus-swarm bodies), so the horde cap still binds them.
    private void SpawnRiderPack(Game g, Vector2 at, float hordeScale, int tier)
    {
        float bump = 1.0f; // tier-4 placeholder bump retired (real tier-4 bodies entered)
        // 2026-11-12 SPAWN RAMP: the USER 2+2 pack unit thickens +5% per run minute;
        // the sword/bow split stays even (assistant pick).
        int pk = (int)(4.0 * SpawnRamp(g));
        if (pk % 2 == 1)
            pk += 1; // keep the sword/bow split even
        for (int i2 = 0; i2 < pk; i2++)
        {
            bool sword = i2 < pk / 2;
            Vector2 p2 = at + new Vector2((i2 % 2) * 56.0f - 28.0f, (i2 / 2) * 56.0f - 28.0f);
            if (g.GetLeader() != null && p2.DistanceTo(g.GetLeader().GlobalPosition) < 260.0f)
                continue; // near-party guard per body, same discipline as Spawn()
            Enemy e2 = new Enemy();
            e2.kind = sword ? "rider" : "riderbow";
            e2.maxHp = (sword ? 140.0f : 80.0f) * hordeScale * bump; // USER numbers (2026-11-09 retune: sword mount 100 -> 140, bow rider 60 -> 80)
            e2.hp = e2.maxHp;
            e2.speed = 110.0f;   // USER number
            e2.contactDamage = 4.0f; // USER number
            e2.rangedDamage = sword ? 0.0f : 3.0f; // USER number (bow rider)
            e2.xpValue = 5.0f;   // USER number
            e2.GlobalPosition = p2;
            g.RegisterEnemy(e2);
            g.AddChild(e2);
        }
        GD.Print("[10thAge] (rider) tier-3 pack enters: " + (pk / 2) + " sword + " + (pk / 2) + " bow wolf riders on one bearing");
    }

    private void SpawnMage(Game g, Vector2 at, float hordeScale, int tier)
    {
        Vector2 p2 = at + new Vector2(0.0f, 90.0f); // ride just south of the hob that funded it (assistant pick)
        if (g.GetLeader() != null && p2.DistanceTo(g.GetLeader().GlobalPosition) < 260.0f)
            return; // near-party guard
        Enemy e2 = new Enemy();
        e2.kind = "mage";
        float bump = 1.0f; // tier-4 placeholder bump retired (real tier-4 bodies entered)
        e2.maxHp = 40.0f * hordeScale * bump; // USER number
        e2.hp = e2.maxHp;
        e2.speed = 70.0f;      // USER number
        e2.contactDamage = 4.0f; // USER number (if walked into)
        e2.rangedDamage = 6.0f;  // USER number
        e2.xpValue = 6.0f;     // USER number
        e2.GlobalPosition = p2;
        g.RegisterEnemy(e2);
        g.AddChild(e2);
        GD.Print("[10thAge] (mage) goblin mage slips in after the second hobgoblin");
    }

    // WOLF-2 PACK (user 2026-11-09): "have these spawn in packs of 3 when they do spawn"
    // (USER cadence unit). USER numbers: 70 hp / 120 speed / contact 4 / 2 xp. Fan offsets and
    // the per-body near-party guard ride the same discipline as SpawnRiderPack.
    private void SpawnWolfPack(Game g, Vector2 at, float hordeScale)
    {
        // 2026-11-12 SPAWN RAMP: the USER pack-of-3 unit thickens +5% per run minute (assistant mapping)
        int pk = (int)(3.0 * SpawnRamp(g));
        for (int i2 = 0; i2 < pk; i2++)
        {
            Vector2 p2 = at + new Vector2((i2 - (pk - 1) * 0.5f) * 56.0f, (i2 % 2) * 44.0f - 22.0f); // small fan (assistant pick)
            if (g.GetLeader() != null && p2.DistanceTo(g.GetLeader().GlobalPosition) < 260.0f)
                continue; // near-party guard per body, same discipline as Spawn()
            Enemy e2 = new Enemy();
            e2.kind = "wolf2";
            e2.maxHp = 70.0f * hordeScale; // USER number
            e2.hp = e2.maxHp;
            e2.speed = 120.0f;       // USER number
            e2.contactDamage = 4.0f; // USER number
            e2.xpValue = 2.0f;       // USER number
            e2.GlobalPosition = p2;
            g.RegisterEnemy(e2);
            g.AddChild(e2);
        }
        GD.Print("[10thAge] (wolf2) long-sight charge pack enters: " + pk + " wolf2s on one bearing");
    }

    // TIER-4 OGRE (user 2026-11-05): "big, slow, and a lot of HP". HP/speed/contact/XP are
    // assistant picks (the user dictated the SLAM numbers, not the body): HP 220 (x horde
    // scale like every other body), speed 55 lumber, contact 4 if walked into, XP 10 bounty.
    private void SpawnOgre(Game g, Vector2 at, float hordeScale)
    {
        Enemy e = new Enemy();
        e.kind = "ogre";
        e.maxHp = 220.0f * hordeScale; // assistant pick: the map's fattest body
        e.hp = e.maxHp;
        e.speed = 55.0f;     // assistant pick: slowest body on the map
        e.contactDamage = 4.0f; // assistant pick: brute body, but the slam is its threat
        e.xpValue = 10.0f;   // assistant pick: tier-4 bounty
        e.GlobalPosition = at;
        g.RegisterEnemy(e);
        g.AddChild(e);
        GD.Print("[10thAge] (ogre) tier-4 ogre lumbers in");
        // OGRE2 ESCORT (user 2026-11-11: the second ogre "escorts the tier-4 ogre"): the pair
        // enters on the SAME beat, a short stride apart on the SAME fold (the near-party guard
        // already burned pos upstream, so a tight offset stays honest). SAME stat card as the
        // ogre (USER: same stats); the escort owns the goblin-hurl lane in Enemy.cs.
        Enemy o2 = new Enemy();
        o2.kind = "ogre2";
        o2.maxHp = 220.0f * hordeScale; // SAME card as the ogre (USER)
        o2.hp = o2.maxHp;
        o2.speed = 55.0f;
        o2.contactDamage = 4.0f;
        o2.xpValue = 10.0f;
        o2.GlobalPosition = at + new Vector2(80.0f, 60.0f); // assistant pick: stride apart, same fold
        g.RegisterEnemy(o2);
        g.AddChild(o2);
        GD.Print("[10thAge] (ogre) the ogre escort lumbers in beside it");
    }

// HORDE EVENT: one bonus swarm, budget-free (user: "these won't count against the maximum
// number of enemies that can spawn"). Bodies enter as TIER 1 goblins now - the user's opening
// rule - and mixing tier-2/3 at later run points is ONE read of g.GetHordeTier() here, so the
// infrastructure is already in place. The count rides the horde-scale chip ("20 enemies (these
// scale with the horde scale increases)"). All bodies enter as ONE group on ONE random bearing,
// fanned ~70 degrees with a staggered front row so the wave reads as a wave, not 20 bodies at
// one pixel; they home to the squad for free (the Enemy class chases the nearest ally itself).
// One Rng draw per event shifts the run stream downstream, like every other proc.
// HORDE MIX LAW (user 2026-11-12: "every horde spawn after the initial one can include any monsters
// from any current active tiers"): the FIRST bonus swarm stays "just the very first goblin" (the
// opening rule); every later swarm rolls ONE kind PER BODY across tiers 1..GetHordeTier(), pool
// mirroring the cadence gates, each body on its ordinary stat card. Escort semantics (rider packs,
// ogre2 escorts) do NOT chain inside a bonus wave - the swarm enters as one fanned group and
// chained escorts would break the fan (assistant call, retunable). One Rng draw PER BODY on
// mixed swarms shifts the run stream downstream, like every other proc.
    private void SpawnHordeEvent(Game g, float hordeScale, int hordeTier)
    {
        // user 2026-11-06 playtest: the swarm size rides the SAME party factor the ordinary
        // cadence uses (GetPartyInPlay()/2), so a solo opener gets ~10 bodies and a 5-PC squad
        // the full 20+ - the event thickens with the party like every other spawn does.
        float partyScale = (float)g.GetPartyInPlay() / 2.0f;
        int n = (int)(20.0f * hordeScale * partyScale * SpawnRamp(g)); // 2026-11-12 ramp: the bonus swarm thickens +5%/min (user rule)
        Vector2 cen = g.GetCameraCenter();
        Vector2 half = g.GetVisibleHalfExtents();
        float a0 = (float)GD.Randf() * (float)(System.Math.PI * 2.0); // random side of the frame
        for (int i = 0; i < n; i++)
        {
            // one group, one fan: ~70 degrees spread, front row staggered so the swarm is
            // organic rather than 20 bodies at one pixel
            float frac = n > 1 ? (float)i / (float)(n - 1) : 0.5f;
            float a = a0 + (frac - 0.5f) * 1.2f;
            Vector2 unit = new Vector2((float)System.Math.Cos(a), (float)System.Math.Sin(a));
            double tx = System.Math.Abs(unit.X) > 0.001 ? half.X / System.Math.Abs(unit.X) : 1e30;
            double ty = System.Math.Abs(unit.Y) > 0.001 ? half.Y / System.Math.Abs(unit.Y) : 1e30;
            double reach = System.Math.Min(tx, ty) + 60.0 + 40.0 * (i % 3);
            Vector2 pos = cen + unit * (float)reach;
            if (g.GetLeader() != null && pos.DistanceTo(g.GetLeader().GlobalPosition) < 260.0f)
                continue; // near-party guard, same discipline as Spawn()
            Enemy e = new Enemy();
            // HORDE MIX LAW: first swarm pure tier-1 goblins; later swarms roll a kind per body
            // across the ACTIVE tiers (pool mirrors the cadence gates: mage/rider/riderbow are
            // tier-3 creatures, wolf2 packs and the ogre family are tier-4, tier 2 adds fastgoblin/hob/wolves)
            string k = "goblin";
            if (_hordeEventsSeen > 0 && hordeTier > 1)
            {
                string[] pool;
                if (hordeTier == 2)
                    pool = new string[] { "goblin", "goblin2", "fastgoblin", "hobgoblin", "wolf", "wolf2", "wolf3" };
                else if (hordeTier == 3)
                    pool = new string[] { "goblin", "goblin2", "fastgoblin", "hobgoblin", "wolf", "wolf2", "wolf3", "rider", "riderbow", "mage" };
                else
                    pool = new string[] { "goblin", "goblin2", "fastgoblin", "hobgoblin", "wolf", "wolf2", "wolf3", "rider", "riderbow", "mage", "ogre", "ogre2" };
                k = pool[(int)GD.RandRange(pool.Length - 1, 0)];
            }
            float hp = 30.0f, spd = 90.0f, contact = 2.0f, ranged = 0.0f, xp = 1.0f; // tier-1 goblin card (USER numbers)
            if (k == "goblin2") { hp = 25.0f; spd = 100.0f; contact = 2.0f; }
            else if (k == "fastgoblin") { hp = 40.0f; spd = 110.0f; contact = 3.0f; xp = 2.0f; }
            else if (k == "hobgoblin") { hp = 60.0f; spd = 100.0f; contact = 3.0f; xp = 3.0f; }
            else if (k == "wolf") { hp = 70.0f; spd = 120.0f; contact = 3.0f; xp = 2.0f; }
            else if (k == "wolf2") { hp = 70.0f; spd = 120.0f; contact = 4.0f; xp = 2.0f; }
            else if (k == "wolf3") { hp = 100.0f; spd = 100.0f; contact = 4.0f; xp = 3.0f; }
            else if (k == "rider") { hp = 140.0f; spd = 110.0f; contact = 4.0f; xp = 5.0f; } // 2026-11-09 retuned mount card (USER)
            else if (k == "riderbow") { hp = 80.0f; spd = 110.0f; contact = 4.0f; ranged = 3.0f; xp = 5.0f; }
            else if (k == "mage") { hp = 40.0f; spd = 70.0f; contact = 4.0f; ranged = 6.0f; xp = 6.0f; }
            else if (k == "ogre" || k == "ogre2") { hp = 220.0f; spd = 55.0f; contact = 4.0f; xp = 10.0f; }
            e.kind = k; // HORDE MIX LAW body: pool-picked kind on its own card (first swarm stays pure tier-1)
            e.maxHp = hp * hordeScale; // each body on its ordinary stat card, rest chips fatten it like ordinary spawns
            e.hp = e.maxHp;
            e.speed = spd;
            e.contactDamage = contact;
            e.xpValue = xp; // the user's "chance to gain a lot more XP": clean bodies = a fat purse
            e.rangedDamage = ranged; // bow riders + mages keep their ranged card inside the swarm
            e.GlobalPosition = pos;
            g.RegisterHordeBody(e); // budget-free lane: never counted, exactly like the boss nodes
            g.AddChild(e);
        }
        _hordeEventsSeen++; // the swarm that just fired was the pure one if it was the first (mix law)
        g.AdvanceHordeSchedule();
        GD.Print("[10thAge] (horde) bonus swarm: " + n + " bodies fan in on one bearing (first swarm pure goblins, later swarms mix active tiers), budget-free (xp per body)");
    }

    // ENEMY TEST ROOM rotation (user 2026-11-07): one body every 15-20 s (USER cadence), cycling
    // TEST_KINDS so EVERY stage-1 creature gets a turn. Placement rides the same off-camera edge
    // ray as ordinary spawns. A near-party guard RE-ROLLS on the same kind (_testTimer = 1 s)
    // without eating the cursor, so the ladder still walks the whole roster.
    private void TestRotation(Game g, float d)
    {
        _testTimer -= d * (float)g.GetTimeWarp(); // warp speed charges the cadence like every other leg
        if (_testTimer > 0.0f)
            return;
        Vector2 cen = g.GetCameraCenter();
        Vector2 half = g.GetVisibleHalfExtents();
        float a = (float)GD.Randf() * (float)(System.Math.PI * 2.0);
        Vector2 unit = new Vector2((float)System.Math.Cos(a), (float)System.Math.Sin(a));
        double tx = System.Math.Abs(unit.X) > 0.001 ? half.X / System.Math.Abs(unit.X) : 1e30;
        double ty = System.Math.Abs(unit.Y) > 0.001 ? half.Y / System.Math.Abs(unit.Y) : 1e30;
        double reach = System.Math.Min(tx, ty) + 60.0;
        Vector2 pos = cen + unit * (float)reach;
        if (g.GetLeader() != null && pos.DistanceTo(g.GetLeader().GlobalPosition) < 260.0f)
        {
            _testTimer = 1.0f; // retry NEXT clock on the same kind: the rotation never skips a body
            return;
        }
        string kind = TEST_KINDS[_testIdx % TEST_KINDS.Length];
        _testIdx++;
        SpawnTestBody(g, pos, kind, (float)g.GetHordeScale());
        _testTimer = TEST_EVERY_LO + (float)GD.Randf() * (TEST_EVERY_HI - TEST_EVERY_LO);
        GD.Print("[10thAge] (test) rotation " + _testIdx + ": " + kind + " enters (15-20 s cadence, full stage-1 roster)");
    }

    // Test bodies ride the SAME numbers as the ordinary tier lane (USER HP/speed/contact/ranged/XP
    // per kind), so what the user watches in the room is what the stage does. hordeScale is 1.0 in
    // the room (no rest scaling) but the read keeps the math identical to Spawn()/SpawnOgre().
    private void SpawnTestBody(Game g, Vector2 at, string kind, float hordeScale)
    {
        Enemy e = new Enemy();
        e.kind = kind;
        float hp = 30.0f, spd = 90.0f, contact = 2.0f, ranged = 0.0f, xp = 1.0f;
        if (kind == "wolf") { hp = 70.0f; spd = 120.0f; contact = 3.0f; xp = 2.0f; }
        else if (kind == "goblin2") { hp = 25.0f; spd = 100.0f; contact = 2.0f; xp = 1.0f; }
        else if (kind == "fastgoblin") { hp = 40.0f; spd = 110.0f; contact = 3.0f; xp = 2.0f; }
        else if (kind == "hobgoblin") { hp = 60.0f; spd = 100.0f; contact = 3.0f; xp = 3.0f; }
        else if (kind == "rider") { hp = 100.0f; spd = 110.0f; contact = 4.0f; xp = 5.0f; }
        else if (kind == "riderbow") { hp = 60.0f; spd = 110.0f; contact = 4.0f; ranged = 3.0f; xp = 5.0f; }
        else if (kind == "mage") { hp = 40.0f; spd = 70.0f; contact = 4.0f; ranged = 6.0f; xp = 6.0f; }
        else if (kind == "ogre") { hp = 220.0f; spd = 55.0f; contact = 4.0f; xp = 10.0f; }
        else if (kind == "ogre2") { hp = 220.0f; spd = 55.0f; contact = 4.0f; xp = 10.0f; } // SAME card as the ogre (USER)
        e.maxHp = hp * hordeScale;
        e.hp = e.maxHp;
        e.speed = spd;
        e.contactDamage = contact;
        e.rangedDamage = ranged;
        e.xpValue = xp;
        e.GlobalPosition = at;
        g.RegisterEnemy(e);
        g.AddChild(e);
    }
}
