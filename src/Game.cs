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
    readonly double REVIVE_RANGE = 30.0;
    readonly double REVIVE_CHANNEL = 3.0;

    // ---------- death saves ----------
    readonly double DS_INTERVAL = 4.0;
    readonly double DS_STABILIZE_AT = 15.0;
    readonly int DS_FAIL_LIMIT = 3;
    readonly int DS_STABILIZE_NEED = 2;

    // ---------- stage pacing (tanurial forest M1) ----------
    readonly double FIRST_HALF = 90.0;
    readonly double SECOND_HALF = 100.0;
    readonly double CAMPFIRE_LEN = 8.0;

    // ---------- xp / level ----------
    double _xp = 0.0;
    int _level = 1;
    double _xpNext = 6.0;
    readonly double PICKER_AUTO_TIMEOUT = 6.0;

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

    public void TakeHit(PartyMember m, double amount)
    {
        if (m == null || m.downed || m.eliminated)
            return;
        m.hp -= (float)amount;
        if (m.hp <= 0.0f)
        {
            m.downed = true;
            m.dsTimer = 0.0f;
            m.dsFails = 0;
            m.dsSucceeds = 0;
            m.reviveProgress = 0.0f;
            m.SetDsText("DOWN");
            CheckTpk();
        }
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
            m.SetDsText("back at camp");
        }
    }

    public void AddXp(double value)
    {
        _xp += value;
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
        if (_state == StageState.MINIBOSS && b.kind == "mini")
        {
            _state = StageState.CAMPFIRE;
            _stageT = 0.0;
            ClearEnemies();
            ReviveAllAtCampfire();
            GD.Print("[10thAge] mini-boss slain — campfire: party back at half HP");
        }
        else if (_state == StageState.BOSS && b.kind == "final")
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
        foreach (Godot.Node2D n in _enemies)
        {
            Combatant c = (Combatant)n;
            if (c.kind != "mini" && c.kind != "final" && c.kind != "dead")
            {
                c.kind = "dead";
                _liveEnemies--;
                n.QueueFree();
            }
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
        Godot.ColorRect floor = new ColorRect();
        floor.Size = new Vector2((float)(ARENA_HW * 2.0), (float)(ARENA_HH * 2.0));
        floor.Position = -floor.Size * 0.5f;
        floor.Color = new Color(0.10f, 0.16f, 0.10f, 1.0f);
        AddChild(floor);
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
        if (leaderLive)
            dir = new Vector2(Input.GetActionStrength("move_right") - Input.GetActionStrength("move_left"),
                              Input.GetActionStrength("move_down") - Input.GetActionStrength("move_up"));
        bool moved = dir.LengthSquared() > 0.0025f;
        if (moved)
        {
            dir = dir.Normalized();
            leader.GlobalPosition = ClampToArena(leader.GlobalPosition + dir * leader.baseSpeed * leader.speedMul * delta);
            leader.facing = (float)System.Math.Atan2(dir.Y, dir.X);
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

        // death saves + stand-still revive channels
        foreach (PartyMember m in _party)
        {
            if (m.eliminated)
                continue;
            if (!m.downed)
                continue;
            m.dsTimer += (float)d;
            if (m.dsTimer >= DS_INTERVAL)
            {
                m.dsTimer = 0.0f;
                int roll = (int)(GD.Randi() % 20) + 1;
                if (roll >= (int)DS_STABILIZE_AT)
                {
                    m.dsSucceeds++;
                    if (m.dsSucceeds >= DS_STABILIZE_NEED)
                    {
                        m.downed = false;
                        m.hp = m.maxHp * 0.25f;
                        m.SetDsText("stable!");
                    }
                    else
                    {
                        m.SetDsText("d20:" + roll + " stable " + m.dsSucceeds + "/" + DS_STABILIZE_NEED);
                    }
                }
                else
                {
                    m.dsFails++;
                    if (m.dsFails >= DS_FAIL_LIMIT)
                    {
                        m.eliminated = true;
                        m.SetDsText("OUT - next campfire");
                        CheckTpk();
                    }
                    else
                    {
                        m.SetDsText("d20:" + roll + " fail " + m.dsFails + "/" + DS_FAIL_LIMIT);
                    }
                }
            }
            bool inRange = leaderLive && leader.GlobalPosition.DistanceTo(m.GlobalPosition) < REVIVE_RANGE;
            if (inRange && !moved)
            {
                m.reviveProgress += (float)d;
                if (m.reviveProgress >= REVIVE_CHANNEL)
                {
                    m.downed = false;
                    m.reviveProgress = 0.0f;
                    m.hp = m.maxHp * 0.5f;
                    m.SetDsText("revived!");
                }
            }
            else if (m.reviveProgress > 0.0f)
            {
                m.reviveProgress = (float)System.Math.Max(0.0, m.reviveProgress - delta * 1.5);
            }
        }

        // kill scan + hurt-flash decay over everything weapons can hit
        foreach (Godot.Node2D n in _enemies)
        {
            Combatant c = (Combatant)n;
            if (c.kind == "dead")
                continue;
            if (c.hurtFlash > 0.0f)
                c.hurtFlash -= delta;
            if (c.hp <= 0.0f)
                OnEnemyKilled(c);
        }

        UpdateGems(delta);
        UpdateBullets(delta);
        PickerTick(d);
        _hud.Text = "Surviving the 10th Age - Tanurial Forest | Lv " + _level +
                    " | xp " + (int)_xp + "/" + (int)_xpNext +
                    " | " + StateName(_state) + " t=" + (int)_stageT + " | enemies " + _liveEnemies;
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
            PartyMember m = NearestMember(p);
            if (m != null && p.DistanceTo(m.GlobalPosition) < 12.0f + MEMBER_RADIUS)
            {
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
            _pickerLabel.Text = "LEVEL UP: [A] " + a + " dmg +15%   [S] " + b +
                                " dmg +15%   [D] heal party 25% (auto after 6s)";
        }
        if (!_pickerActive)
            return;
        _pickerWait += d;
        int choice = -1;
        if (Input.GetActionStrength("pick_a") > 0.5f)
            choice = 0;
        else if (_party.Count > 1 && Input.GetActionStrength("pick_s") > 0.5f)
            choice = 1;
        else if (Input.GetActionStrength("pick_d") > 0.5f)
            choice = 2;
        if (_pickerWait >= PICKER_AUTO_TIMEOUT)
            choice = 2;
        if (choice < 0)
            return;
        ApplyUpgrade(choice);
        _pickerActive = false;
        _pickerLabel.Text = "";
    }

    private void ApplyUpgrade(int choice)
    {
        if (choice < 2 && choice < _party.Count)
        {
            _party[choice].dmgMul *= 1.15f;
            GD.Print("[10thAge] upgrade: " + _party[choice].displayName + " dmg +15%");
        }
        else
        {
            foreach (PartyMember m in _party)
            {
                if (!m.eliminated)
                    m.hp = (float)System.Math.Min(m.maxHp, m.hp + m.maxHp * 0.25);
            }
            GD.Print("[10thAge] upgrade: party healed 25%");
        }
    }

    private void CheckTpk()
    {
        foreach (PartyMember m in _party)
        {
            if (!m.eliminated && m.hp > 0.0f)
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
        GD.Print("[10thAge] " + s + " | level " + _level + " | xp total " + (int)_xp);
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
