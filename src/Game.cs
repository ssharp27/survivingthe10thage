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
    public const double RING_RADIUS = 64.0; // bodyguard ring; sized so REVIVE_RANGE
    // wraps a ring slot: a downed bodyguard is in arm's reach of the rescue
    // ---------- player revive (leader stands still near a body) ----------
    // 56 (was 30): widened so a bodyguard slot and the rescue share a spot
    readonly double REVIVE_RANGE = 56.0;
    readonly double REVIVE_CHANNEL = 3.0;
    readonly double REVIVE_INVULN = 2.0; // post-revive immunity; the body is also
    // shielded while the channel is actively progressing (reviveShield)

    // ---------- death saves (user-locked rules) ----------
    // Downed allies roll a d20 every 4 s: nat 20 = revived at 25 % HP, 11-19 = a
    // success, 3 successes = STABLE (rolls stop; a hit then slays the body
    // outright unless the player revives it), 10 or below = a failure and 3
    // failures = out until the next campfire. Damage on a downed (unstable) body
    // is one failure, kept 2 s apart so a swarm can't insta-finish anyone.
    readonly double DS_INTERVAL = 4.0; // d20 roll cadence while downed
    readonly int DS_FAIL_ON = 10; // roll <= this fails
    readonly int DS_REVIVE_ON = 20; // nat 20 revives at 25 % HP
    readonly int DS_FAIL_LIMIT = 3;
    readonly int DS_STABLE_NEED = 3; // successes before the rolls stop
    readonly double DS_DAMAGE_FAIL_BUFFER = 2.0;

    // ---------- stage pacing (tanurial forest M1) ----------
    readonly double FIRST_HALF = 90.0;
    readonly double SECOND_HALF = 100.0;
    // stage name shown on the ui/ cards; hardcoded until the meta-map exists (M3)
    private const string STAGE_NAME = "Tanurial Forest";
    // CAMPFIRE_LEN is gone: the campfire beat is player-driven (the ui/ Campfire
    // screen emits rest_chosen). This is only the safety valve so an idle/AFK
    // player (or a headless smoke run) still advances after this much waiting.
    readonly double CAMPFIRE_WAIT_TIMEOUT = 25.0;

    // ---------- death-state tester (user ask) ----------
    // A big red instant-kill circle spawns 10 s into the run: attack-proof, it
    // kills the first body it touches and despawns right after. Test tool only.
    bool _reaperSpawned = false;
    bool _reaperEnabled = false; // TEST BENCH default OFF; M1_REAPER=1 re-enables it

    // ---------- xp / level ----------
    double _xp = 0.0;
    double _xpTotal = 0.0;
    int _level = 1;
    double _xpNext = 6.0;
    readonly double PICKER_AUTO_TIMEOUT = 6.0;

    // ---------- level-up picker: SMART-RANDOM 3 OF 6 named upgrades, 3 ranks then cap ----------
    // Human-test: the fixed 3-option card felt the same every level - roll variety instead.
    // Two slots guarantee one still-open upgrade per member (Baldrun, Mosiah), the last
    // slot is fully random from the whole pool, and capped options never show. Offense
    // only - never HP, speed or healing (those live in campfires and revives). Ranks cap
    // at 3 per DESIGN; the 4th rank would be the evolution (Stormcaller / Viperkiss),
    // stubbed for M2. This table is the data surface: a .tres pass copies these out.
    const int PICK_RANK_CAP = 3;
    static readonly string[] OPT_LABEL = { "longer reach (maul)", "heavier blows (maul damage)",
                                           "swifter swings (cooldown)", "quicker thrusts (cooldown)",
                                           "deeper thrust (reach + pierce)", "sharper points (damage)" };
    static readonly int[] OPT_MEMBER = { 0, 0, 0, 1, 1, 1 }; // targets one named member (DESIGN)
    static readonly float[] OPT_CD = { 1.0f, 1.0f, 0.86f, 0.86f, 1.0f, 1.0f };
    static readonly float[] OPT_RANGE = { 1.15f, 1.0f, 1.0f, 1.0f, 1.16f, 1.0f };
    static readonly float[] OPT_DMG = { 1.0f, 1.10f, 1.0f, 1.0f, 1.0f, 1.10f };
    static readonly int[] OPT_PIERCE = { 0, 0, 0, 0, 1, 0 };
    int[] _pickRank = new int[6];
    int[] _optRoll = { -1, -1, -1 }; // this level-up's three offered options (slot -> option)

    // ---------- dash (SPACE; sanity-check numbers, balance approval is the user's) ----------
    readonly double DASH_IMPULSE = 240.0; // px burst per dash
    readonly double DASH_CD = 1.5;        // seconds between dashes
    double _dashCdT = 0.0;                // cooldown countdown (ready at <= 0)

    // ---------- rest -> horde scaling (long rest costs the party its safety) ----------
    // Director.cs reads this and densifies spawns; the long-rest risk chip on the
    // Campfire screen is wired to exactly this.
    double _hordeScale = 1.0;

    // ---------- ui/ screens (Campfire.tscn + RunSummary.tscn, see ui/INTEGRATION.md) ----------
    Godot.CanvasLayer _campfire;
    Godot.CanvasLayer _summary;
    bool _campfireWaiting = false;
    string _campfireChoice = "";
    // run summary retry: one ChangeSceneToFile per run, so a retry can never loop
    bool _summaryRetried = false;

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
        if (m.invuln > 0.0f || m.reviveShield)
            return; // post-revive shield, or the body is mid-rescue: immune
        if (m.downed)
        {
            if (m.stable)
            {
                // stable bodies stopped rolling: the next hit just slays them
                m.eliminated = true;
                m.SetDsText("OUT - next campfire");
                GD.Print("[10thAge] " + m.displayName + " stable body SLAIN by damage - out until campfire");
                CheckTpk();
                return;
            }
            // downed bodies still get ran into / hit by volleys: each hit is one
            // failed death save, buffered 2 s so a swarm can't insta-finish them
            if (m.dsDamageBuffer > 0.0f)
                return;
            m.dsDamageBuffer = (float)DS_DAMAGE_FAIL_BUFFER;
            m.dsFails++;
            // locked rule made visible: damage on a downed body IS an extra failure
            GD.Print("[10thAge] damage on downed " + m.displayName + " = extra death-save fail "
                     + m.dsFails + "/" + DS_FAIL_LIMIT + " (buffered 2s)");
            if (m.dsFails >= DS_FAIL_LIMIT)
            {
                m.eliminated = true;
                m.SetDsText("OUT - next campfire");
                GD.Print("[10thAge] " + m.displayName + " ELIMINATED (3 fails) - returns at the next campfire");
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
            GD.Print("[10thAge] " + m.displayName + " DOWN at 0hp - d20 death saves start");
            CheckTpk();
        }
    }

    public bool OnReaperDown(PartyMember m)
    {
        // TEST reaper (user death-state tester): whatever it touches goes DOWN and
        // starts the d20 death-save cycle — this is the revival test bench. Returns
        // true only when the down actually landed, so immunity lets it keep lurking.
        if (m == null || m.eliminated || m.downed)
            return false;
        if (m.invuln > 0.0f || m.reviveShield)
            return false;
        m.hp = 0.0f;
        m.downed = true;
        m.stable = false;
        m.dsTimer = 0.0f; // the first d20 rolls right away
        m.dsFails = 0;
        m.dsSucceeds = 0;
        m.dsDamageBuffer = 0.0f;
        m.SetDsText("DOWN");
        GD.Print("[10thAge] TEST reaper downed " + m.displayName + " - death saves start");
        CheckTpk();
        return true;
    }

    // healBeat=false is the camp BEAT (mini-boss death): fallen lifted, bodies stand up,
    // but the +25% heal waits on the rest choice so the campfire never double-heals.
    public void ReviveAllAtCampfire(bool healBeat)
    {
        foreach (PartyMember m in _party)
        {
            if (m.eliminated)
            {
                // out since the last campfire: return at 25% HP
                m.eliminated = false;
                m.campReturned = true; // long rest must not full-heal these bodies
                m.downed = false;
                m.hp = m.maxHp * 0.25f;
                m.stable = false;
                m.dsFails = 0;
                m.dsSucceeds = 0;
                m.dsTimer = 0.0f;
                m.dsDamageBuffer = 0.0f;
                m.reviveProgress = 0.0f;
                m.reviveShield = false;
                // locked rule: EVERY revive path grants 2 s of post-revive invulnerability
                m.invuln = (float)REVIVE_INVULN;
                GD.Print("[10thAge] campfire revive: " + m.displayName + " returns at 25% hp + 2s invulnerable");
                m.SetDsTemp("back at camp", 2.5);
                continue;
            }
            if (m.downed)
            {
                // a body at camp: stands back up, rolls reset
                m.downed = false;
                m.stable = false;
                m.dsFails = 0;
                m.dsSucceeds = 0;
                m.dsTimer = 0.0f;
                m.dsDamageBuffer = 0.0f;
                m.reviveProgress = 0.0f;
                m.reviveShield = false;
                m.invuln = (float)REVIVE_INVULN;
                GD.Print("[10thAge] campfire stand-up: " + m.displayName + " + 2s invulnerable");
                m.SetDsTemp("back at camp", 2.5);
            }
            if (healBeat)
            {
                // the campfire's locked +25% heal - applied once, by the rest choice
                m.hp = m.hp + m.maxHp * 0.25f;
                if (m.hp > m.maxHp)
                    m.hp = m.maxHp;
            }
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
        ReleaseLiveCount((Enemy)c); // ownership-flagged: never double-decrements
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
            ReviveAllAtCampfire(false);
            GD.Print("[10thAge] mini-boss slain — camp: the fallen return at 25% hp; the "
                     + "rest choice decides healing");
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
        // P1: a boss only joins the hittable registry when its stage engages. Before
        // that it is parked far offscreen, so weapons can never pre-kill it (pre-killing
        // the mini used to softlock the run) and nothing can target it.
        Vector2 near = _leader != null ? _leader.GlobalPosition : new Vector2(0, 0);
        foreach (Boss b in _bosses)
        {
            if (b.kind != kind || b.active || b.hp <= 0.0f)
                continue;
            b.active = true;
            bool registered = false;
            ulong id = b.GetInstanceId();
            foreach (Godot.Node2D n in _enemies)
            {
                if (n.GetInstanceId() == id)
                {
                    registered = true;
                    break;
                }
            }
            if (!registered)
            {
                // drop in just outside the camera view so the player sees it arrive
                Vector2 entry = ClampToArena(near + new Vector2(0.0f, -430.0f));
                b.GlobalPosition = entry;
                _enemies.Add(b);
                GD.Print("[10thAge] boss " + kind + " deployed at " + (int)b.GlobalPosition.X + ","
                         + (int)b.GlobalPosition.Y);
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

    // the live-enemy counter is owned by the enemy itself (Enemy.counted): every exit
    // path (weapon kill, self-despawn like the TEST reaper, ClearEnemies) calls
    // ReleaseLiveCount once, so the cap can never leak upward and stop binding.
    public void ReleaseLiveCount(Enemy e)
    {
        if (e.counted)
        {
            e.counted = false;
            _liveEnemies--;
            if (_liveEnemies < 0)
                _liveEnemies = 0;
        }
    }

    public void RegisterEnemy(Combatant e)
    {
        _enemies.Add(e);
        if (e.kind != "mini" && e.kind != "final")
        {
            ((Enemy)e).counted = true;
            _liveEnemies++;
        }
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
            ReleaseLiveCount((Enemy)c);
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

    // ---------- gates + readouts the other scripts consume ----------
    // The picker is a soft pause: the world is drawn as usual but nobody acts, so
    // no damage is taken while a human reads the pick card. Enemies/bullets/revive
    // channels check this; so does the horde director.
    public bool GetPickerActive()
    {
        return _pickerActive;
    }

    public Vector2 GetCameraCenter()
    {
        return _camera != null ? _camera.GlobalPosition : (_leader != null ? _leader.GlobalPosition
                                                                            : new Vector2(0, 0));
    }

    // long rest feeds this back to the director's pacing (rest = power vs horde scaling)
    public double GetHordeScale()
    {
        return _hordeScale;
    }

    public bool GetRestChosen()
    {
        return _campfireChoice != "";
    }

    public PartyMember GetMemberByIndex(int idx)
    {
        foreach (PartyMember m in _party)
        {
            if (m.memberIndex == idx)
                return m;
        }
        return _party.Count > 0 ? _party[0] : null;
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
        // the reaper is a pure death/revive TEST BENCH (no balance meaning): human play
        // left it OFF now that the revive rules work, and M1_REAPER=1 puts it back on
        // for testing (a pilot smoke with it ON still TPKs often - the Leader-down rule
        // we deferred leaves no rescue channel, which is variance, not a bug)
        _reaperEnabled = OS.GetEnvironment("M1_REAPER") != null && OS.GetEnvironment("M1_REAPER") != "";
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
        // P4: the reaper stays a spawn-once-at-10s TEST BENCH (locked behavior; no
        // balance meaning), now OPT-IN via M1_REAPER=1. Its only changes here are where
        // it drops in (outside the camera view, never on the party) and a slower lurk.
        if (!_reaperSpawned && _stageT >= 10.0 && _state == StageState.PLAY1 && _reaperEnabled)
        {
            // TEST reaper: spawns ONCE 10 s into horde I, downs whoever it touches
            // (death-save tester), attack-proof (hp stays huge), despawns after its
            // first down
            _reaperSpawned = true;
            Enemy r = new Enemy();
            r.kind = "reaper";
            r.maxHp = 100000000.0f;
            r.hp = r.maxHp;
            r.speed = 48.0f;
            r.contactDamage = 0.0f;
            r.xpValue = 0.0f;
            // spawn just OUTSIDE the camera view, never dropped on the party: the old
            // ClampToArena(leader + 500,0) could land right on top of the squad
            Vector2 cen = GetCameraCenter();
            r.GlobalPosition = ClampToArena(cen + new Vector2(820.0f, 140.0f));
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
            case StageState.PLAY2:
                if (_stageT >= SECOND_HALF)
                {
                    _state = StageState.BOSS;
                    _stageT = 0.0;
                    ActivateBoss("final");
                    GD.Print("[10thAge] BOSS engaged");
                }
                break;
            case StageState.CAMPFIRE:
                // player-driven rest beat: the ui/Campfire screen emits rest_chosen and
                // Game._OnCampfireChosen advances to PLAY2. The timeout only exists so an
                // AFK player / headless smoke run can never hang the stage.
                if (!_campfireWaiting)
                {
                    _campfireWaiting = true;
                    _stageT = 0.0;
                    ShowCampfire();
                }
                else if (_stageT >= CAMPFIRE_WAIT_TIMEOUT)
                {
                    GD.Print("[10thAge] campfire: nobody chose a rest after " + (int)CAMPFIRE_WAIT_TIMEOUT
                             + "s - defaulting to a short rest");
                    EndCampfire("short");
                }
                break;
            case StageState.DEAD:
            case StageState.VICTORY:
                // the run is over: the RunSummary card is up. R retries the stage (M1 has no
                // journal/menu). ShowRunSummary un-pauses the tree, so this key gets input
                // frames even though the card paused it; _RetryRun fires at most once per run.
                if (Input.IsActionJustPressed("retry"))
                    _RetryRun();
                break;
            default:
                break;
        }
        if (_state == StageState.DEAD || _state == StageState.VICTORY)
            return;

        // ---------- level-up picker = soft pause ----------
        // P1: the picker used to share the WASD letters, so a held A/S walked AND
        // confirmed the pick the very frame the card appeared. Now presses are edge-read
        // (IsActionJustPressed) and combat is frozen while the card is up: the arena,
        // party, horde and bodies stay drawn, nobody acts, nothing takes damage.
        if (_pickerActive)
        {
            PickerTick(d);
            _hud.Text = "LEVEL UP - pick ONE (press 1 / 2 / 3 once, releases do not count)"
                        + " | combat holds while you read | Lv " + _level + " | xp " + (int)_xp + "/"
                        + (int)_xpNext + " | " + StateName(_state) + " t=" + (int)_stageT +
                        " | enemies " + _liveEnemies;
            foreach (PartyMember m in _party)
            {
                string st = m.eliminated ? "OUT - next campfire"
                            : (m.downed ? (m.stable ? "STABLE down" : "DOWN - death saving")
                                 : (int)m.hp + "/" + (int)m.maxHp + " hp");
                _hud.Text += "\n" + m.displayName + ": " + st;
            }
            return;
        }

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
                {
                    dir = new Vector2((float)System.Math.Cos(_ringAngle * 4.0),
                                      (float)System.Math.Sin(_ringAngle * 4.0));
                    // competent-pilot face-lock: keep the maul arc on the nearest pursuer
                    // while strafing, so the arc stops only grazing what we run past
                    Combatant nearest = null;
                    float nd = 1e9f;
                    for (int i = _enemies.Count - 1; i >= 0; i--)
                    {
                        if (i >= _enemies.Count)
                            continue;
                        Combatant c = (Combatant)_enemies[i];
                        if (c == null || c.kind == "dead" || c.hp <= 0.0f)
                            continue;
                        float dd = c.GlobalPosition.DistanceSquaredTo(leader.GlobalPosition);
                        if (dd < nd)
                        {
                            nd = dd;
                            nearest = c;
                        }
                    }
                    if (nearest != null)
                        faceTo = nearest.GlobalPosition - leader.GlobalPosition;
                }
            }
        }
        // ---------- dash (SPACE, human-test addition) ----------
        // Short forward burst toward the walk direction (or the way Baldrun is
        // facing when standing still) on a cooldown. Sanity numbers pending the
        // user's balance approval; instant impulse so it reads as a dodge, not a
        // speed buff that makes holding a direction feel slippery.
        _dashCdT -= delta;
        if (leaderLive && _dashCdT <= 0.0 && Input.IsActionJustPressed("dash"))
        {
            Vector2 burst = dir;
            if (burst.LengthSquared() <= 0.0025f)
                burst = new Vector2((float)System.Math.Cos(leader.facing),
                                    (float)System.Math.Sin(leader.facing));
            leader.GlobalPosition = ClampToArena(leader.GlobalPosition + burst.Normalized() * (float)DASH_IMPULSE);
            _dashCdT = DASH_CD;
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
                if (!b.active || b.hp <= 0.0f)
                    continue; // parked bosses are not threats yet
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

        // ---------- the bodyguard ring (human-test locked) ----------
        // followers slowly ORBIT the leader on a world-space clock - the threat-facing
        // slot system made them feel glued to the Leader's directional input. Auto-aim
        // lives in PartyMember (the ring member bites its prey), so the rapier still
        // lands while circling.
        {
            Vector2 anchor = leader != null ? leader.GlobalPosition : new Vector2(0, 0);
            foreach (PartyMember m in _party)
            {
                if (m.isLeader || m.downed || m.eliminated)
                    continue;
                int k = m.memberIndex;
                if (k < 2)
                    k = 2 + (k % 4);
                double slot = k * System.Math.PI * 0.4;
                Vector2 aim = new Vector2((float)System.Math.Cos(_ringAngle + slot),
                                          (float)System.Math.Sin(_ringAngle + slot));
                Vector2 target = ClampToArena(anchor + aim * (float)RING_RADIUS);
                m.GlobalPosition += (target - m.GlobalPosition).LimitLength(m.baseSpeed * m.speedMul * 0.9f * delta);
            }
        }
        if (_camera != null && leader != null)
            _camera.GlobalPosition = _camera.GlobalPosition.Lerp(leader.GlobalPosition, 0.08f);

        // death saves (locked rules): d20 every 4 s while downed - nat 20 revives
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
                    // P6 instrumentation: one line per d20 roll + outcome, so a headless
                    // run can be verified by READING the dice, not the code.
                    GD.Print("[10thAge] d20 " + m.displayName + " rolled " + roll +
                             " (target " + DS_REVIVE_ON + " revive / " + DS_FAIL_ON + " or under fails)");
                    if (roll == DS_REVIVE_ON)
                    {
                        GD.Print("[10thAge]   outcome: NAT 20 - " + m.displayName +
                                 " self-revives at 25% hp, 2s invulnerable");
                        m.downed = false;
                        m.hp = m.maxHp * 0.25f;
                        m.stable = false;
                        m.dsFails = 0;
                        m.dsSucceeds = 0;
                        m.dsDamageBuffer = 0.0f;
                        m.reviveShield = false;
                        // locked rule: every revive grants 2 s of invulnerability
                        m.invuln = (float)REVIVE_INVULN;
                        GD.Print("[10thAge] revive(dice) " + m.displayName + " at 25% hp | invuln 2s granted");
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
                            GD.Print("[10thAge] " + m.displayName + " ELIMINATED (3 failed saves) - returns at campfire");
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
                            GD.Print("[10thAge] " + m.displayName + " STABLE - rolls stop; a hit now slays unless the party channels a rescue");
                        }
                        else
                            m.SetDsText("d20:" + roll + " stable " + m.dsSucceeds + "/" + DS_STABLE_NEED);
                    }
                }
            }
            // player revive channel: leader stands still next to the body. The body
            // is immune while the channel is progressing (reviveShield) so a swarm
            // can't screw the rescue out, plus 2 s of post-revive invulnerability.
            bool channeling = leaderLive && !moved
                              && leader.GlobalPosition.DistanceTo(m.GlobalPosition) < REVIVE_RANGE;
            if (channeling && m.reviveProgress <= 0.0f)
                GD.Print("[10thAge] channel START on " + m.displayName + "'s body - stand still "
                         + (int)REVIVE_CHANNEL + "s; the body is immune while it runs");
            m.reviveShield = channeling;
            if (channeling)
            {
                m.reviveProgress += (float)d;
                if (m.reviveProgress >= REVIVE_CHANNEL)
                {
                    m.downed = false;
                    m.stable = false;
                    m.dsFails = 0;
                    m.dsSucceeds = 0;
                    m.reviveProgress = 0.0f;
                    m.reviveShield = false;
                    // locked rule: every revive grants 2 s of invulnerability
                    m.invuln = (float)REVIVE_INVULN;
                    m.hp = m.maxHp * 0.5f;
                    GD.Print("[10thAge] channel COMPLETE: " + m.displayName +
                             " revived at 50% hp | invuln 2s granted");
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
            // OnBossKilled (mini slain) runs ClearEnemies() mid-scan, so the array
            // can shrink under us; re-clamp instead of indexing a stale position.
            if (i >= _enemies.Count)
                continue;
            Combatant c = (Combatant)_enemies[i];
            if (c == null || c.kind == "dead")
            {
                // self-despawn (TEST reaper) leaves kind "dead" without a decrement;
                // the counted flag makes this cleanup safe for every exit path
                if (c is Enemy)
                    ReleaseLiveCount((Enemy)c);
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
            // human-test: fixed cards felt same-y every level - roll a smart-random
            // three instead (see RollOptions): one upgrade per member is guaranteed,
            // the third slot is fully wild
            if (RollOptions())
            {
                _pickerActive = true;
                _pickerWait = 0.0;
                // human test: one long line ran past the window edge - stack the three
                // picks so they always fit, and read them with NUMBER keys (A/S/D were
                // the movement keys, so a walk tap could end the card unseen)
                _pickerLabel.Text = "LEVEL UP - pick ONE (combat is frozen):\n" +
                                    "[1] " + OptLabel(0) + "\n[2] " + OptLabel(1) + "\n[3] " + OptLabel(2);
                GD.Print("[10thAge] level " + _level + " picker up: [1] " + OptLabel(0) + " | [2] " +
                         OptLabel(1) + " | [3] " + OptLabel(2));
            }
            else
                GD.Print("[10thAge] level " + _level + ": every upgrade capped - no pick offered");
        }
        if (!_pickerActive)
            return;
        _pickerWait += d;
        // P1: the picker used to ride on the WASD letters, so the old GetActionStrength
        // read confirmed the frame the card appeared for anyone still holding A/S.
        // NUMBER keys avoid the movement clash entirely - still edge-read only, so a
        // fresh tap is a deliberate pick. Slots map through _optRoll (this card's roll).
        int choice = -1;
        if (Input.IsActionJustPressed("pick_1"))
            choice = _optRoll[0];
        else if (Input.IsActionJustPressed("pick_2"))
            choice = _optRoll[1];
        else if (Input.IsActionJustPressed("pick_3"))
            choice = _optRoll[2];
        // pilot auto-confirm (verification only) + the AFK fallback: both take an
        // uncapped option, rotating so the pilot exercises all three stat tracks
        if (choice < 0 && (_pickerWait >= PICKER_AUTO_TIMEOUT || (_pilot && _pickerWait >= 2.0)))
        {
            for (int s = 0; s < 3; s++)
            {
                if (_optRoll[s] >= 0)
                {
                    choice = _optRoll[s];
                    break;
                }
            }
        }
        if (choice < 0)
            return;
        ApplyUpgrade(choice);
        _pickerActive = false;
        _pickerLabel.Text = "";
    }

    private string OptLabel(int slot)
    {
        if (slot < 0 || slot >= _optRoll.Length)
            return "-";
        int i = _optRoll[slot];
        if (i < 0 || i >= OPT_LABEL.Length)
            return "-";
        PartyMember m = GetMemberByIndex(OPT_MEMBER[i]);
        if (m == null)
            return "-";
        return m.displayName + " " + m.weaponName + ": " + OPT_LABEL[i];
    }

    // ---------- smart-random level-up roll (human-test: variety, not same-y cards) ----------
    // Slots 0/1 each draw one still-open upgrade for member 0 and member 1 (so every
    // character is always represented), slot 2 is fully wild from everything still
    // open. No option is ever offered twice on one card. A member whose whole pool is
    // capped borrows from the wild pool instead; if NOTHING is open the picker stays
    // closed and the level-up just banks the xp.
    private bool RollOptions()
    {
        bool[] open = new bool[OPT_LABEL.Length];
        int[] cand = new int[OPT_LABEL.Length];
        int openCount = 0;
        for (int o = 0; o < OPT_LABEL.Length; o++)
        {
            open[o] = OPT_MEMBER[o] < _party.Count && _pickRank[o] < PICK_RANK_CAP;
            if (open[o])
                openCount++;
        }
        if (openCount == 0)
            return false;
        for (int slot = 0; slot < 3; slot++)
        {
            int c = 0;
            int wantMember = (slot < 2 && slot < _party.Count) ? slot : -1;
            for (int o = 0; o < OPT_LABEL.Length; o++)
                if (open[o] && (wantMember < 0 || OPT_MEMBER[o] == wantMember))
                    cand[c++] = o;
            if (c == 0) // this member is fully capped: fall back to the wild pool
                for (int o = 0; o < OPT_LABEL.Length; o++)
                    if (open[o])
                        cand[c++] = o;
            if (c == 0)
            {
                _optRoll[slot] = -1;
                continue;
            }
            int pick = cand[(int)(Rng.Randf() * (float)c) % c];
            _optRoll[slot] = pick;
            open[pick] = false; // never offer the same upgrade twice on one card
        }
        return _optRoll[0] >= 0 || _optRoll[1] >= 0 || _optRoll[2] >= 0;
    }

    // never hand back a capped track: 3 ranks then the evolution, which is M2 content
    private int OpenOption(int seed)
    {
        int n = OPT_LABEL.Length;
        for (int k = 0; k < n; k++)
        {
            int o = ((seed + k) % n + n) % n;
            if (_pickRank[o] < PICK_RANK_CAP && OPT_MEMBER[o] < _party.Count)
                return o;
        }
        for (int i = 0; i < n; i++)
        {
            if (OPT_MEMBER[i] < _party.Count)
                return i;
        }
        return 0;
    }

    private void ApplyUpgrade(int option)
    {
        // no HP restoration on level-up: healing lives only in campfires and revives
        if (option < 0 || option >= OPT_LABEL.Length || OPT_MEMBER[option] >= _party.Count)
            option = 0;
        PartyMember m = GetMemberByIndex(OPT_MEMBER[option]);
        if (m == null)
            return;
        if (_pickRank[option] >= PICK_RANK_CAP)
        {
            // DESIGN: 3 ranks then an evolution = subclass choice. Stormcaller / Viperkiss
            // are M2 content, so a 4th rank stays capped and is logged, never silently eaten.
            GD.Print("[10thAge] upgrade: " + m.displayName + " " + OPT_LABEL[option] +
                     " capped at rank " + PICK_RANK_CAP + "/" + PICK_RANK_CAP +
                     " (evolution is M2)");
            return;
        }
        _pickRank[option]++;
        m.dmgMul *= OPT_DMG[option];
        m.cdMul *= OPT_CD[option];
        m.rangeMul *= OPT_RANGE[option];
        m.pierce += OPT_PIERCE[option];
        m.pickHistory += (m.pickHistory == "" ? "" : ", ") + m.weaponName + " " +
                         OPT_LABEL[option] + " x" + _pickRank[option];
        GD.Print("[10thAge] upgrade: " + m.displayName + " " + OPT_LABEL[option] +
                 " rank " + _pickRank[option] + "/" + PICK_RANK_CAP);
    }

    // ---------- rest beat: the ui/Campfire screen owns the choice, Game owns the rules ----------
    private void ShowCampfire()
    {
        if (_campfire == null)
        {
            Godot.PackedScene scn = GD.Load("res://ui/Campfire.tscn") as Godot.PackedScene;
            if (scn == null)
            {
                GD.Print("[10thAge] WARNING: res://ui/Campfire.tscn missing - falling back to the "
                         + (int)CAMPFIRE_WAIT_TIMEOUT + "s timeout rest");
                return;
            }
            _campfire = scn.Instantiate() as Godot.CanvasLayer;
            // party_data must land BEFORE AddChild: the screen builds its cards in _ready()
            _campfire.Set("party_data", CampfireCards());
            _campfire.Set("stage_name", STAGE_NAME);
            AddChild(_campfire);
            // the instantiated scene root IS the "Campfire" node the signals live on
            _campfire.Connect("rest_chosen", new Callable(this, "_OnCampfireChosen"));
            _campfire.Connect("cancelled", new Callable(this, "_OnCampfireCancelled"));
        }
        else
            _campfire.Set("party_data", CampfireCards()); // re-offer after a cancel
        _campfire.Show();
        GD.Print("[10thAge] campfire: the rest card is up - short/long is the party's call");
    }

    private void EndCampfire(string restType)
    {
        if (_campfire != null)
            _campfire.Hide();
        _campfireWaiting = false;
        _campfireChoice = "";
        if (restType == "long")
        {
            // LONG REST (DESIGN): full heal for the already-standing company - bodies the
            // camp beat just lifted stay at the locked 25 % (no full-heal cheat for the
            // fallen) - one member trains overnight, at the price of horde scaling.
            foreach (PartyMember m in _party)
            {
                if (!m.campReturned)
                    m.hp = m.maxHp;
            }
            ApplyUpgrade(OpenOption(1));
            _hordeScale += 0.35;
            GD.Print("[10thAge] LONG rest: living party fully healed, the fallen stay at 25% hp, horde "
                     + "scaling x" + (float)((int)(_hordeScale * 100.0)) / 100.0);
        }
        else
        {
            // SHORT REST: this IS the locked campfire heal - +25 % max HP, clamped, applied
            // exactly once (the camp beat itself did not heal). Nobody gets lifted back up
            // from OUT here - that is the long rest's price.
            ReviveAllAtCampfire(true);
            GD.Print("[10thAge] SHORT rest: company healed +25% max HP (clamped), horde pace unchanged");
        }
        foreach (PartyMember m in _party)
            m.campReturned = false;
        _state = StageState.PLAY2;
        _stageT = 0.0;
    }

    private void _OnCampfireChosen(string restType)
    {
        EndCampfire(restType);
    }

    private void _OnCampfireCancelled()
    {
        // the player backed out of the embers: keep the beat, put the card back up
        _campfireWaiting = false;
        GD.Print("[10thAge] campfire declined - the rest card comes back up");
    }

    // ---------- run end: the ui/RunSummary card is the retry loop (journal is M4) ----------
    private void PrintSummary(string s)
    {
        if (_summaryPrinted)
            return;
        _summaryPrinted = true;
        // GD.Print stays alongside the screen so a headless smoke run still logs the beat
        GD.Print("[10thAge] " + s + " | level " + _level + " | xp total " + (int)_xpTotal);
        ShowRunSummary(s);
    }

    private void ShowRunSummary(string s)
    {
        if (_summary == null)
        {
            Godot.PackedScene scn = GD.Load("res://ui/RunSummary.tscn") as Godot.PackedScene;
            if (scn == null)
            {
                GD.Print("[10thAge] WARNING: res://ui/RunSummary.tscn missing - summary stays print-only");
                return;
            }
            _summary = scn.Instantiate() as Godot.CanvasLayer;
            _summary.Set("result", s.Contains("VICTORY") ? "victory" : "tpk");
            _summary.Set("stage_name", STAGE_NAME);
            _summary.Set("level", _level);
            _summary.Set("xp_total", (int)_xpTotal);
            _summary.Set("members", SummaryCards());
            AddChild(_summary); // its _ready() pauses the tree and builds the rows
            _summary.Connect("dismissed", new Callable(this, "_OnSummaryDismissed"));
        }
        _summary.Show();
        // the card pauses the tree, but M1 un-pauses right away: DEAD/VICTORY already
        // freezes the sim through the stage early-return, and the retry key needs input
        // frames that a paused tree would never give us.
        GetTree().Paused = false;
        GD.Print("[10thAge] run summary up - press R to retry the stage (journal/menu are M4)");
    }

    private void _RetryRun()
    {
        if (_summaryPrinted && !_summaryRetried)
        {
            _summaryRetried = true;
            if (_summary != null)
                _summary.Hide();
            GetTree().Paused = false;
            GD.Print("[10thAge] retry: reloading res://Main.tscn");
            // the engine's own restart path: the main loop relaunches with this scene
            GetTree().ChangeSceneToFile("res://Main.tscn");
        }
    }

    private void _OnSummaryDismissed()
    {
        // M1 decision (flag for the designer): dismissing without R just closes the card;
        // the run stays over. Title screen / campaign journal routing is M4 content.
        if (_summary != null)
            _summary.Hide();
        GD.Print("[10thAge] summary dismissed - the run stays over (press R to retry the stage)");
    }

    private Godot.Collections.Array CampfireCards()
    {
        Godot.Collections.Array rows = new();
        foreach (PartyMember m in _party)
        {
            Godot.Collections.Dictionary card = new();
            card["name"] = m.displayName;
            card["klass"] = m.class_ == "" ? m.weaponName : m.class_;
            card["weapon"] = m.weaponName;
            card["hp"] = (int)m.hp;
            card["max_hp"] = (int)m.maxHp;
            card["state"] = m.eliminated ? "out" : (m.stable ? "stable" : (m.downed ? "down" : "up"));
            card["leader"] = m.isLeader;
            rows.Add(card);
        }
        return rows;
    }

    private Godot.Collections.Array SummaryCards()
    {
        Godot.Collections.Array rows = new();
        foreach (PartyMember m in _party)
        {
            Godot.Collections.Dictionary card = new();
            card["name"] = m.displayName;
            card["state"] = m.eliminated ? "fallen"
                            : (m.downed ? (m.stable ? "stable" : "downed") : "survived");
            card["picks"] = m.pickHistory == "" ? "no picks yet" : m.pickHistory;
            rows.Add(card);
        }
        return rows;
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
