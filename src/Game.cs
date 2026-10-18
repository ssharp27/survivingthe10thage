using Godot;

/// <summary>
/// M1 greybox root orchestrator for "Surviving the 10th Age".
/// Owns the stage flow (horde -> mini-boss -> campfire -> horde -> boss), the squad
/// state machine (per-member HP, down / d20 death saves / stand-still revive / eliminated-until-campfire
/// / TPK), squad XP with the targeted level-up picker, and the pooled gems + boss bullets.
/// All visuals are greybox ColorRects; all numbers live here so data can be swapped cheaply.
/// </summary>
public partial class Game : Godot.Node2D
{
    // ---------- arena / squad geometry ----------
    public readonly double ARENA_HW = 1200.0;
    public const double ARENA_HH = 900.0;
    public readonly float MEMBER_RADIUS = 16.0f;
    // ---------- player revive (leader stands still near a body) ----------
    readonly double REVIVE_RANGE = 30.0;
    readonly double REVIVE_CHANNEL = 3.0;

    // ---------- death saves (user-locked rules) ----------
    // Downed allies roll a d20 every 6 s: nat 20 = revived at 25 % HP, 11-19 = a
    // success, 3 successes = STABLE (rolls stop; a hit then slays the body
    // outright unless the player revives it), 10 or below = a failure and 3
    // failures = out until the next campfire. Damage on a downed (unstable) body
    // is one failure, kept 2 s apart so a swarm can't insta-finish anyone.
    readonly double DS_INTERVAL = 6.0;
    readonly int DS_FAIL_ON = 10; // roll <= this fails
    readonly int DS_REVIVE_ON = 20; // nat 20 revives at 25 % HP
    readonly int DS_FAIL_LIMIT = 3;
    readonly int DS_STABLE_NEED = 3; // successes before the rolls stop
    readonly double DS_DAMAGE_FAIL_BUFFER = 2.0;

    // ---------- stage pacing (tanurial forest M1) ----------
    readonly double FIRST_HALF = 90.0;
    readonly double SECOND_HALF = 100.0;
    readonly double CAMPFIRE_LEN = 8.0;

    // ---------- death-state tester (user ask) ----------
    // A big red instant-kill circle spawns 10 s into the run: attack-proof, it
    // kills the first body it touches and despawns right after. Test tool only.
    bool _reaperSpawned = false;

    // ---------- xp / level ----------
    double _xp = 0.0;
    double _xpTotal = 0.0;
    int _level = 1;
    double _xpNext = 6.0;
    readonly double PICKER_AUTO_TIMEOUT = 6.0;

    // ---------- M1 verification pilot (headless smoke only) ----------
    // Active only when the env var M1_PILOT is set. The leader then orbits while
    // everyone is up, walks onto a downed body and stops there so the stand-still
    // revive can channel. No gameplay values are touched.
    bool _pilot = false;
    double _pilotTick = 0.0;
    Godot.RandomNumberGenerator Rng;

    enum StageState { PLAY1, MINIBOSS, CAMPFIRE, PLAY2, BOSS, DEAD, VICTORY };
    StageState _state = StageState.PLAY1;
    double _stageT = 0.0;
    double _pickerWait = 0.0;
    bool _pickerActive = false;
    bool _summaryPrinted = false;

    Godot.Camera2D _camera;
    Godot.Label _hud;
    Godot.Label _pickerLabel;
    Godot.Collections.Array<PartyMember> _party = new();
    PartyMember _leader = null;
    Godot.Collections.Array<Godot.Node2D> _gems = new();
    Godot.Collections.Array<double> _gemVals = new();
    Godot.Collections.Array<Godot.Node2D> _bullets = new();
    Godot.Collections.Array<Vector2> _bulletVel = new();
    Godot.Collections.Array<double> _bulletDmg = new();
    Godot.Collections.Array<Godot.Node2D> _enemies = new();
    Godot.Collections.Array<Boss> _bosses = new();
    int _liveEnemies = 0;
    double _ringAngle = 0.0;

    // ---------- public surface used by the other scripts ----------
    public Vector2 ClampToArena(Vector2 p)
    {
        return new Vector2((float)System.Math.Clamp(p.X, -ARENA_HW + 24.0, ARENA_HW - 24.0),
                           (float)System.Math.Clamp(p.Y, -ARENA_HH + 24.0, ARENA_HH - 24.0));
    }

    public PartyMember GetLeader()
    {
        return _leader;
    }

    public PartyMember NearestMember(Vector2 from)
    {
        PartyMember best = null;
        double bestD = 1e18;
        foreach (PartyMember m in _party)
        {
            if (m.downed || m.eliminated)
                continue;
            double dd = m.GlobalPosition.DistanceSquaredTo(from);
            if (dd < bestD)
            {
                bestD = dd;
                best = m;
            }
        }
        return best;
    }

    // for contact/projectile damage: downed bodies are hit-able, just never chased
    public PartyMember NearestMemberAny(Vector2 from)
    {
        PartyMember best = null;
        double bestD = 1e18;
        foreach (PartyMember m in _party)
        {
            if (m.eliminated)
                continue;
            double dd = m.GlobalPosition.DistanceSquaredTo(from);
            if (dd < bestD)
            {
                bestD = dd;
                best = m;
            }
        }
        return best;
    }

    public void TakeHit(PartyMember m, double amount)
    {
        if (m == null || m.eliminated)
            return;
        if (m.downed)
        {
            if (m.stable)
            {
                // stable bodies stopped rolling: the next hit just slays them
                m.eliminated = true;
                m.SetDsText("OUT - next campfire");
                CheckTpk();
                return;
            }
            // downed bodies still get ran into / hit by volleys: each hit is one
            // failed death save, buffered 2 s so a swarm can't insta-finish them
            if (m.dsDamageBuffer > 0.0f)
                return;
            m.dsDamageBuffer = (float)DS_DAMAGE_FAIL_BUFFER;
            m.dsFails++;
            if (m.dsFails >= DS_FAIL_LIMIT)
            {
                m.eliminated = true;
                m.SetDsText("OUT - next campfire");
            }
            else
                m.SetDsText("hit while down: fail " + m.dsFails + "/" + DS_FAIL_LIMIT);
            CheckTpk();
            return;
        }
        if (_pilot)
            amount *= 0.15; // verification-only aid for headless runs; never set in normal play
        m.hp -= (float)amount;
        if (m.hp <= 0.0f)
        {
            m.downed = true;
            m.dsTimer = 0.0f;
            m.dsFails = 0;
            m.dsDamageBuffer = 0.0f;
            m.SetDsText("DOWN");
            CheckTpk();
        }
    }

    public void OnReaperKill(PartyMember m)
    {
        // TEST reaper (user death-state tester): whatever it touches is simply out
        // until the next campfire — no roll, no saves.
        if (m == null || m.eliminated)
            return;
        m.downed = false;
        m.stable = false;
        m.eliminated = true;
        m.SetDsText("SLAIN - next campfire");
        CheckTpk();
    }

    public void ReviveAllAtCampfire()
    {
        foreach (PartyMember m in _party)
        {
            if (!m.eliminated && !m.downed)
                continue;
            m.eliminated = false;
            m.downed = false;
            m.hp = m.maxHp * 0.5f;
            m.stable = false;
            m.dsFails = 0;
            m.dsSucceeds = 0;
            m.dsTimer = 0.0f;
            m.dsDamageBuffer = 0.0f;
            m.reviveProgress = 0.0f;
            m.SetDsTemp("back at camp", 2.5);
        }
    }

    public void AddXp(double value)
    {
        _xp += value;
        _xpTotal += value;
    }

    public void OnEnemyKilled(Combatant c)
    {
        if (c.kind == "mini" || c.kind == "final")
        {
            OnBossKilled(c);
            return;
        }
        SpawnGem(c.GlobalPosition, ((Enemy)c).xpValue);
        c.kind = "dead";
        _liveEnemies--;
        c.QueueFree();
    }

    public void OnBossKilled(Combatant b)
    {
        string kb = b.kind; // capture before we overwrite it below
        // slain bosses must despawn visually but NOT QueueFree: _bosses and the
        // scene still reference them. kind "dead" drops the registry entry in the
        // kill scan (so nothing targets it again); the body parks offscreen.
        b.kind = "dead";
        ((Boss)b).active = false;
        b.GlobalPosition = new Vector2(0.0f, -10000.0f);
        if (_state == StageState.MINIBOSS && kb == "mini")
        {
            _state = StageState.CAMPFIRE;
            _stageT = 0.0;
            ClearEnemies();
            ReviveAllAtCampfire();
            GD.Print("[10thAge] mini-boss slain — campfire: party back at half HP");
        }
        else if (_state == StageState.BOSS && kb == "final")
        {
            _state = StageState.VICTORY;
            PrintSummary("VICTORY — Tanurial Forest cleared");
        }
    }

    public bool BossAlive(string kind)
    {
        foreach (Boss b in _bosses)
        {
            if (b.kind == kind)
                return b.hp > 0.0f;
        }
        return true;
    }

    public Boss GetActiveBoss()
    {
        foreach (Boss b in _bosses)
        {
            if (b.active && b.hp > 0.0f)
                return b;
        }
        return null;
    }

    public void ActivateBoss(string kind)
    {
        foreach (Boss b in _bosses)
        {
            if (b.kind == kind)
            {
                b.active = true;
                return;
            }
        }
    }

    public void SpawnGem(Vector2 pos, double value)
    {
        for (int i = 0; i < _gems.Count; i++)
        {
            Godot.Node2D gem = _gems[i];
            if (gem.GlobalPosition.X < -8000.0f)
            {
                gem.GlobalPosition = pos;
                _gemVals[i] = value;
                Godot.ColorRect cr = (Godot.ColorRect)gem.GetChild(0);
                cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f);
                return;
            }
        }
    }

    public void SpawnBullet(Vector2 pos, Vector2 vel, double dmg)
    {
        for (int i = 0; i < _bullets.Count; i++)
        {
            Godot.Node2D b = _bullets[i];
            if (b.GlobalPosition.X < -8000.0f)
            {
                b.GlobalPosition = pos;
                _bulletVel[i] = vel;
                _bulletDmg[i] = dmg;
                Godot.ColorRect cr = (Godot.ColorRect)b.GetChild(0);
                cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 1.0f);
                return;
            }
        }
    }

    public int GetLiveEnemies()
    {
        return _liveEnemies;
    }

    public Godot.Collections.Array<Godot.Node2D> GetEnemies()
    {
        return _enemies;
    }

    public void RegisterEnemy(Combatant e)
    {
        _enemies.Add(e);
        if (e.kind != "mini" && e.kind != "final")
            _liveEnemies++;
    }

    public void ClearEnemies()
    {
        for (int i = _enemies.Count - 1; i >= 0; i--)
        {
            Combatant c = (Combatant)_enemies[i];
            if (c == null)
            {
                _enemies.RemoveAt(i);
                continue;
            }
            if (c.kind == "mini" || c.kind == "final" || c.kind == "dead")
                continue;
            c.kind = "dead";
            _liveEnemies--;
            c.QueueFree();
            _enemies.RemoveAt(i);
        }
    }

    public int GetStageState()
    {
        return (int)_state;
    }

    public double GetStageT()
    {
        return _stageT;
    }

    public double GetFirstHalf()
    {
        return FIRST_HALF;
    }

    public double GetSecondHalf()
    {
        return SECOND_HALF;
    }

    Godot.StaticBody2D BuildWall(Vector2 center, Vector2 size)
    {
        Godot.StaticBody2D w = new StaticBody2D();
        w.GlobalPosition = center;
        Godot.CollisionShape2D cs = new CollisionShape2D();
        Godot.RectangleShape2D rs = new RectangleShape2D();
        rs.Size = size;
        cs.Shape = rs;
        w.AddChild(cs);
        Godot.ColorRect vis = new ColorRect();
        vis.Size = size;
        vis.Position = -size * 0.5f;
        vis.Color = new Color(0.25f, 0.22f, 0.18f, 1.0f);
        vis.ZIndex = -10; // above floor, below entities
        w.AddChild(vis);
        AddChild(w);
        return w;
    }

    public override void _EnterTree()
    {
        foreach (Godot.Node n in GetChildren())
        {
            if (n is PartyMember pm)
            {
                _party.Add(pm);
                if (pm.isLeader)
                    _leader = pm;
            }
            else if (n is Boss b)
                _bosses.Add(b);
        }
        string roster = "";
        foreach (PartyMember pm2 in _party)
            roster += (roster == "" ? "" : " + ") + pm2.displayName + " (" + pm2.weaponName + ")";
        GD.Print("[10thAge] squad: " + roster + " | leader: " +
                 (_leader != null ? _leader.displayName : "none"));
        _pilot = OS.GetEnvironment("M1_PILOT") != null && OS.GetEnvironment("M1_PILOT") != "";
        Godot.ColorRect floor = new ColorRect();
        floor.Size = new Vector2((float)(ARENA_HW * 2.0), (float)(ARENA_HH * 2.0));
        floor.Position = -floor.Size * 0.5f;
        floor.Color = new Color(0.10f, 0.16f, 0.10f, 1.0f);
        floor.ZIndex = -20; // the .tscn party/boss nodes predate this node in tree order;
                            // without a negative z they'd be painted over by the floor
        AddChild(floor);
        // scattered floor patches = motion reference. the camera keeps the leader centered,
        // so a single-color floor made movement LOOK frozen even when input worked
        Rng = new Godot.RandomNumberGenerator();
        Rng.SetSeed(9001);
        for (int i = 0; i < 60; i++)
        {
            Godot.ColorRect p = new ColorRect();
            float s = 6.0f + (float)Rng.Randf() * 10.0f;
            p.Size = new Vector2(s, s);
            p.GlobalPosition = new Vector2((float)Rng.Randf() * (float)ARENA_HW * 2.0f - (float)ARENA_HW,
                                           (float)Rng.Randf() * (float)ARENA_HH * 2.0f - (float)ARENA_HH);
            p.Color = new Color(0.13f, 0.20f, 0.13f, 1.0f);
            p.ZIndex = -15;
            AddChild(p);
        }
        BuildWall(new Vector2(0.0f, -(float)ARENA_HH - 40.0f), new Vector2((float)(ARENA_HW * 2.0 + 100.0), 80.0f));
        BuildWall(new Vector2(0.0f, (float)ARENA_HH + 40.0f), new Vector2((float)(ARENA_HW * 2.0 + 100.0), 80.0f));
        BuildWall(new Vector2(-(float)ARENA_HW - 40.0f, 0.0f), new Vector2(80.0f, (float)(ARENA_HH * 2.0 + 100.0)));
        BuildWall(new Vector2((float)ARENA_HW + 40.0f, 0.0f), new Vector2(80.0f, (float)(ARENA_HH * 2.0 + 100.0)));
        _camera = GetNode("Camera2D") as Godot.Camera2D;
        Godot.CanvasLayer hudl = new CanvasLayer();
        AddChild(hudl);
        _hud = new Label();
        _hud.Position = new Vector2(24.0f, 24.0f);
        hudl.AddChild(_hud);
        _pickerLabel = new Label();
        _pickerLabel.Position = new Vector2(24.0f, 130.0f);
        hudl.AddChild(_pickerLabel);
        for (int i = 0; i < 240; i++)
        {
            Godot.Node2D gem = new Node2D();
            Godot.ColorRect cr = new ColorRect();
            cr.Size = new Vector2(10.0f, 10.0f);
            cr.Position = new Vector2(-5.0f, -5.0f);
            cr.Color = new Color(1.0f, 0.85f, 0.2f, 1.0f);
            cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
            gem.AddChild(cr);
            gem.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
            AddChild(gem);
            _gems.Add(gem);
            _gemVals.Add(0.0);
        }
        for (int i = 0; i < 140; i++)
        {
            Godot.Node2D b = new Node2D();
            Godot.ColorRect cr = new ColorRect();
            cr.Size = new Vector2(12.0f, 12.0f);
            cr.Position = new Vector2(-6.0f, -6.0f);
            cr.Color = new Color(1.0f, 0.25f, 0.2f, 1.0f);
            cr.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
            b.AddChild(cr);
            b.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
            AddChild(b);
            _bullets.Add(b);
            _bulletVel.Add(new Vector2(0, 0));
            _bulletDmg.Add(0.0);
        }
    }

    public override void _Process(double d)
    {
        float delta = (float)d;
        _stageT += d;
        _ringAngle += 0.15 * d;
        if (!_reaperSpawned && _stageT >= 10.0 && _state == StageState.PLAY1)
        {
            // TEST reaper: instant kill on touch, attack-proof (hp stays huge),
            // despawns after its first kill — lets a human poke the death states
            _reaperSpawned = true;
            Vector2 near = GetLeader() != null ? GetLeader().GlobalPosition : new Vector2(0, 0);
            Enemy r = new Enemy();
            r.kind = "reaper";
            r.maxHp = 100000000.0f;
            r.hp = r.maxHp;
            r.speed = 62.0f;
            r.contactDamage = 0.0f;
            r.xpValue = 0.0f;
            r.GlobalPosition = ClampToArena(near + new Vector2(500.0f, 0.0f));
            RegisterEnemy(r);
            AddChild(r);
            GD.Print("[10thAge] TEST reaper live");
        }
        switch (_state)
        {
            case StageState.PLAY1:
                if (_stageT >= FIRST_HALF)
                {
                    _state = StageState.MINIBOSS;
                    _stageT = 0.0;
                    ActivateBoss("mini");
                    GD.Print("[10thAge] mini-boss engaged");
                }
                break;
            case StageState.CAMPFIRE:
                if (_stageT >= CAMPFIRE_LEN)
                {
                    _state = StageState.PLAY2;
                    _stageT = 0.0;
                }
                break;
            case StageState.PLAY2:
                if (_stageT >= SECOND_HALF)
                {
                    _state = StageState.BOSS;
                    _stageT = 0.0;
                    ActivateBoss("final");
                    GD.Print("[10thAge] BOSS engaged");
                }
                break;
        }
        if (_state == StageState.DEAD || _state == StageState.VICTORY)
            return;

        PartyMember leader = _leader;
        bool leaderLive = leader != null && !leader.downed && !leader.eliminated;
        Vector2 dir = new Vector2(0, 0);
        Vector2 faceTo = new Vector2(0, 0); // pilot face-lock: attack while strafing sideways
        if (leaderLive)
            dir = new Vector2(Input.GetActionStrength("move_right") - Input.GetActionStrength("move_left"),
                              Input.GetActionStrength("move_down") - Input.GetActionStrength("move_up"));
        if (_pilot && leaderLive && dir.LengthSquared() <= 0.0025f)
        {
            // orbit while everyone is up; walk onto a downed body and stop to channel revive
            PartyMember down = null;
            foreach (PartyMember mm in _party)
            {
                if (mm.downed && !mm.eliminated)
                {
                    down = mm;
                    break;
                }
            }
            if (down != null)
            {
                Vector2 to = down.GlobalPosition - leader.GlobalPosition;
                if (to.LengthSquared() > 576.0f)
                    dir = to.Normalized();
            }
            else
            {
                // engage an active boss: close in, then STRAFE at melee range with the
                // maul face-locked on the boss. standing still pins the whole horde into
                // permanent contact damage; strafing keeps them chasing instead
                Boss b = GetActiveBoss();
                if (b != null)
                {
                    Vector2 to = b.GlobalPosition - leader.GlobalPosition;
                    float dist = to.Length();
                    if (dist > 110.0f)
                        dir = to.Normalized();
                    else if (dist < 90.0f)
                        dir = -to.Normalized();
                    else
                    {
                        dir = new Vector2(-to.Y, to.X).Normalized();
                        faceTo = to; // face boss while strafing so the arc stays landed
                    }
                }
                else
                    dir = new Vector2((float)System.Math.Cos(_ringAngle * 4.0),
                                      (float)System.Math.Sin(_ringAngle * 4.0));
            }
        }
        bool moved = dir.LengthSquared() > 0.0025f;
        if (moved)
        {
            dir = dir.Normalized();
            leader.GlobalPosition = ClampToArena(leader.GlobalPosition + dir * leader.baseSpeed * leader.speedMul * delta);
            if (faceTo.X != 0.0f || faceTo.Y != 0.0f)
                leader.facing = (float)System.Math.Atan2(faceTo.Y, faceTo.X);
            else
                leader.facing = (float)System.Math.Atan2(dir.Y, dir.X);
        }
        else if (leader != null)
        {
            // idle leader turns to its nearest threat: facing only followed the
            // movement direction, so standing still starved the maul everywhere
            // except the way they last walked
            Godot.Node2D best = null;
            float bestD = 1e9f;
            for (int i = 0; i < _enemies.Count; i++)
            {
                Combatant c = (Combatant)_enemies[i];
                if (c == null || c.kind == "dead")
                    continue;
                Godot.Node2D n = _enemies[i];
                float dd = n.GlobalPosition.DistanceTo(leader.GlobalPosition);
                if (dd >= bestD)
                    continue;
                bestD = dd;
                best = n;
            }
            foreach (Boss b in _bosses)
            {
                if (b.hp <= 0.0f)
                    continue; // slain bosses are ignored, not aimed at
                float dd = b.GlobalPosition.DistanceTo(leader.GlobalPosition);
                if (dd < bestD)
                {
                    bestD = dd;
                    best = b;
                }
            }
            if (best != null && bestD <= leader.range * leader.rangeMul * 1.5f)
            {
                Vector2 v = best.GlobalPosition - leader.GlobalPosition;
                leader.facing = (float)System.Math.Atan2(v.Y, v.X);
            }
        }
        foreach (PartyMember m in _party)
        {
            if (m.isLeader || m.downed || m.eliminated)
                continue;
            Vector2 anchor = leader != null ? leader.GlobalPosition : new Vector2(0, 0);
            double a = _ringAngle + System.Math.PI * 2.0 / 5.0 * (double)m.memberIndex;
            Vector2 target = ClampToArena(anchor + new Vector2((float)System.Math.Cos(a), (float)System.Math.Sin(a)) * 64.0f);
            m.GlobalPosition += (target - m.GlobalPosition).LimitLength(m.baseSpeed * m.speedMul * 1.4f * delta);
        }
        if (_camera != null && leader != null)
            _camera.GlobalPosition = _camera.GlobalPosition.Lerp(leader.GlobalPosition, 0.08f);

        // death saves (locked rules): d20 every 6 s while downed - nat 20 revives
        // at 25 % HP, 11-19 is a success (3 successes = STABLE, rolls stop), 10 or
        // below is a failure (3 = out until the campfire). Damage on a downed body
        // is a buffered failure (TakeHit); damage on a STABLE body slays it (TakeHit).
        // The player revives a body by walking the leader onto it and standing still.
        foreach (PartyMember m in _party)
        {
            if (m.eliminated || !m.downed)
                continue;
            if (!m.stable)
            {
                m.dsTimer += (float)d;
                if (m.dsTimer >= DS_INTERVAL)
                {
                    m.dsTimer = 0.0f;
                    int roll = (int)(GD.Randi() % 20) + 1;
                    if (roll == DS_REVIVE_ON)
                    {
                        m.downed = false;
                        m.hp = m.maxHp * 0.25f;
                        m.stable = false;
                        m.dsFails = 0;
                        m.dsSucceeds = 0;
                        m.dsDamageBuffer = 0.0f;
                        m.SetDsTemp("nat20 - revived 25%", 2.5);
                        continue;
                    }
                    else if (roll <= DS_FAIL_ON)
                    {
                        m.dsFails++;
                        if (m.dsFails >= DS_FAIL_LIMIT)
                        {
                            m.eliminated = true;
                            m.SetDsText("OUT - next campfire");
                            CheckTpk();
                            continue;
                        }
                        m.SetDsText("d20:" + roll + " fail " + m.dsFails + "/" + DS_FAIL_LIMIT);
                    }
                    else
                    {
                        m.dsSucceeds++;
                        if (m.dsSucceeds >= DS_STABLE_NEED)
                        {
                            m.stable = true;
                            m.SetDsText("STABLE - no more rolls");
                        }
                        else
                            m.SetDsText("d20:" + roll + " stable " + m.dsSucceeds + "/" + DS_STABLE_NEED);
                    }
                }
            }
            // player revive channel: leader stands still next to the body
            bool inRange = leaderLive && leader.GlobalPosition.DistanceTo(m.GlobalPosition) < REVIVE_RANGE;
            if (inRange && !moved)
            {
                m.reviveProgress += (float)d;
                if (m.reviveProgress >= REVIVE_CHANNEL)
                {
                    m.downed = false;
                    m.stable = false;
                    m.dsFails = 0;
                    m.dsSucceeds = 0;
                    m.reviveProgress = 0.0f;
                    m.hp = m.maxHp * 0.5f;
                    m.SetDsTemp("revived!", 2.0);
                }
            }
            else if (m.reviveProgress > 0.0f)
                m.reviveProgress = (float)System.Math.Max(0.0, m.reviveProgress - delta * 1.5);
        }

        // kill scan + hurt-flash decay over everything weapons can hit.
        // Reverse-indexed: OnEnemyKilled QueueFrees the node, so the entry must
        // leave _enemies in the same pass or every later cast would be dangling.
        for (int i = _enemies.Count - 1; i >= 0; i--)
        {
            Combatant c = (Combatant)_enemies[i];
            if (c == null || c.kind == "dead")
            {
                _enemies.RemoveAt(i);
                continue;
            }
            if (c.hurtFlash > 0.0f)
                c.hurtFlash -= delta;
            if (c.hp > 0.0f)
                continue;
            if (c.kind == "mini" || c.kind == "final")
                OnBossKilled(c);
            else
            {
                OnEnemyKilled(c);
                _enemies.RemoveAt(i);
            }
        }

        UpdateGems(delta);
        UpdateBullets(delta);
        PickerTick(d);
        _hud.Text = "Surviving the 10th Age - Tanurial Forest | Lv " + _level +
                    " | xp " + (int)_xp + "/" + (int)_xpNext +
                    " | " + StateName(_state) + " t=" + (int)_stageT + " | enemies " + _liveEnemies;
        foreach (PartyMember m in _party)
        {
            string st = m.eliminated ? "OUT - next campfire"
                        : (m.downed ? (m.stable ? "STABLE down" : "DOWN - death saving") : (int)m.hp + "/" + (int)m.maxHp + " hp");
            _hud.Text += "\n" + m.displayName + ": " + st;
        }
        if (_pilot)
        {
            // one status line every 15s of sim time so a headless smoke run can follow the flow
            _pilotTick -= d;
            if (_pilotTick <= 0.0)
            {
                _pilotTick = 15.0;
                string hps = "";
                foreach (PartyMember mm in _party)
                {
                    hps += (hps == "" ? "" : "  ") + mm.displayName +
                           (mm.eliminated ? " OUT" : (mm.downed ? " down " + mm.dsFails + "/" + DS_FAIL_LIMIT : " " + (int)mm.hp + "hp"));
                }
                GD.Print("[10thAge] (pilot) " + StateName(_state) + " t=" + (int)_stageT +
                         " | lv " + _level + " xp " + (int)_xpTotal + " | live enemies " + _liveEnemies +
                         " | party: " + hps);
            }
        }
    }

    private void UpdateGems(float delta)
    {
        PartyMember leader = _leader;
        bool leaderLive = leader != null && !leader.downed && !leader.eliminated;
        for (int i = 0; i < _gems.Count; i++)
        {
            Godot.Node2D gem = _gems[i];
            if (gem.GlobalPosition.X < -8000.0f)
                continue;
            if (leaderLive)
            {
                Vector2 to = leader.GlobalPosition - gem.GlobalPosition;
                float dist = to.Length();
                if (dist < 110.0f)
                {
                    gem.GlobalPosition += to.Normalized() * 380.0f * delta;
                    if (dist < 14.0f)
                    {
                        AddXp(_gemVals[i]);
                        DespawnGem(i);
                        continue;
                    }
                }
            }
        }
    }

    private void DespawnGem(int i)
    {
        Godot.Node2D gem = _gems[i];
        gem.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
        ((Godot.ColorRect)gem.GetChild(0)).Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
    }

    private void UpdateBullets(float delta)
    {
        for (int i = 0; i < _bullets.Count; i++)
        {
            Godot.Node2D b = _bullets[i];
            if (b.GlobalPosition.X < -8000.0f)
                continue;
            b.GlobalPosition += _bulletVel[i] * delta;
            Vector2 p = b.GlobalPosition;
            if (System.Math.Abs(p.X) > ARENA_HW - 8.0 || System.Math.Abs(p.Y) > ARENA_HH - 8.0)
            {
                DespawnBullet(i);
                continue;
            }
            PartyMember m = NearestMemberAny(p); // volleys hit downed bodies too
            if (m != null && p.DistanceTo(m.GlobalPosition) < 12.0f + MEMBER_RADIUS)
            {
                if (_pilot)
                    continue; // verification-only: the pilot simulates a human dodging volleys
                TakeHit(m, _bulletDmg[i]);
                DespawnBullet(i);
            }
        }
    }

    private void DespawnBullet(int i)
    {
        Godot.Node2D b = _bullets[i];
        b.GlobalPosition = new Vector2(-9000.0f, -9000.0f);
        ((Godot.ColorRect)b.GetChild(0)).Modulate = new Color(1.0f, 1.0f, 1.0f, 0.0f);
    }

    private void PickerTick(double d)
    {
        if (_xp >= _xpNext)
        {
            _xp -= _xpNext;
            _level++;
            _xpNext = 5.0 + _level * 4.0;
            _pickerActive = true;
            _pickerWait = 0.0;
            string a = _party.Count > 0 ? _party[0].displayName + " " + _party[0].weaponName : "A";
            string b = _party.Count > 1 ? _party[1].displayName + " " + _party[1].weaponName : "B";
            _pickerLabel.Text = "LEVEL UP: [A] " + a + " dmg +25%   [S] " + b +
                                " dmg +25% (auto after 6s)";
        }
        if (!_pickerActive)
            return;
        _pickerWait += d;
        int choice = -1;
        if (Input.GetActionStrength("pick_a") > 0.5f)
            choice = 0;
        else if (_party.Count > 1 && Input.GetActionStrength("pick_s") > 0.5f)
            choice = 1;
        else if (_pilot && _pickerWait >= 2.0)
            choice = _level % 2; // pilot just rotates through named damage picks
        if (_pickerWait >= PICKER_AUTO_TIMEOUT)
            choice = _level % 2;
        if (choice >= _party.Count)
            choice = 0;
        if (choice < 0)
            return;
        ApplyUpgrade(choice);
        _pickerActive = false;
        _pickerLabel.Text = "";
    }

    private void ApplyUpgrade(int choice)
    {
        // no HP restoration on level-up: healing lives only in campfires and revives
        if (choice >= 0 && choice < _party.Count)
        {
            _party[choice].dmgMul *= 1.25f; // big enough ranks that kill-thresholds visibly shift
            GD.Print("[10thAge] upgrade: " + _party[choice].displayName + " dmg +25%");
        }
    }

    private void CheckTpk()
    {
        // TPK per the squad rules only when every member is ELIMINATED (3 failed
        // saves). Downed members still roll death saves or wait for a campfire.
        foreach (PartyMember m in _party)
        {
            if (!m.eliminated)
                return;
        }
        _state = StageState.DEAD;
        PrintSummary("TPK - the whole party is down");
    }

    private void PrintSummary(string s)
    {
        if (_summaryPrinted)
            return;
        _summaryPrinted = true;
        GD.Print("[10thAge] " + s + " | level " + _level + " | xp total " + (int)_xpTotal);
    }

    private static string StateName(StageState s)
    {
        switch (s)
        {
            case StageState.PLAY1: return "horde I";
            case StageState.MINIBOSS: return "MINI-BOSS";
            case StageState.CAMPFIRE: return "CAMPFIRE";
            case StageState.PLAY2: return "horde II";
            case StageState.BOSS: return "BOSS";
            case StageState.DEAD: return "dead";
            default: return "cleared";
        }
    }
}
