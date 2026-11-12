using Godot;

/// <summary>
/// Data-driven placeholder boss for the M1 timeline engine: three phases (100/66/33% HP),
/// every event is telegraph -> attack -> recovery, composed of ring/fan/spokes primitives.
/// Swap the pattern blocks for real encounter data later; the engine stays.
/// kind is inherited from Combatant; the scene author sets the beat it belongs to:
/// "mini1" / "boss1" / "mini2" / "final" (M2 beat loop). User 2026-11-03: both the middle
/// and final bosses are normalized to the mini-boss for now, and EVERY boss death opens a
/// campfire (Game.OnBossKilled: all boss kinds -> camp). The kind still owns body size /
/// colour and the beat slot, so the loop stays data in the scene, not four copies of a class.
/// Legacy "mini" still reads as a mini so an older scene keeps working.
/// 2026-11-12 HAND-PLAY RETUNE (user): "Boss HP feels way too low... 3x their HP" - the
/// authored maxHp lands at 900/900/900/1140/900 in Main.tscn (the hp = maxHp wiring below
/// is untouched). Same-day ask: "Remove the ability to destroy boss projectiles" - the
/// volley sites below opt into Game.SpawnBullet / SpawnBulletArrow bossShot named arg,
/// and Game.cs flags those pooled slots so the blade smas scans skip them (the bolts
/// still hurt the party through UpdateBullets exactly as before).
/// </summary>
public partial class Boss : Combatant
{
    public bool active = false;
    // the last four are scene-authored on BossMini/BossFinal: @export ships them in the PCK
    [Export] public float radius = 46.0f;
    [Export] public float driftSpeed = 42.0f;
    [Export] public float bulletDamage = 10.0f;

    float _eventTimer = 3.0f;
    bool _telegraph = false;
    float _ringOffset = 0.0f;
    int _phase = 0;
    float _contactCd = 0.0f;
    Godot.ColorRect _body;
    // bosses ARE minis (Enter-the-Gungeon spectacle): same table-slide family as the
    // party, just bigger amplitude - art swap reparents _body's replacement here
    Godot.Node2D _rig;
    float _rigT = 0.0f;
    // ALWAYS-FACE law (user 2026-11-12 hand-play: "all enemies' art should flip/rotate to
    // face the character at all times'): bosses ride the SAME rider canvases (authored LEFT),
    // so the art AND its eight red rim copies mirror together. _EnterTree records the base
    // scale + the nine copies; _Process re-applies base x mirror every frame.
    Godot.Sprite2D _artSpr = null;
    Godot.Sprite2D[] _rimSpr = null;
    float _bSc = 1.0f;
    float _bFace = 1.0f;

    // BOSS DANCE (user 2026-11-02, extended 2026-11-03): every boss runs this choreography,
    // not the old drift-and-ring engine. Cycle: run toward the party and settle at
    // MINI_STOP_DIST -> circle around it at the LIVE distance (no pinned ring, 2026-11-02
    // follow-up: melee can get in and it backs off when they do) firing fans AIMED at it
    // (no rings anywhere) -> crawl slowly for MINI_STAND_LEN (2026-11-02 user: "keep them
    // moving slowly after they do their 3 rounds of arrow volleys instead of standing still")
    // -> repeat the whole cycle. The circle leg looses exactly 3 fans (0.5/1.7/2.9 s into
    // the 4 s leg), so the slow beat lands right after the 3rd volley. Getting into position
    // fires NOTHING, until hp drops below half: then it adds one aimed arrow per
    // MINI_ENRAGE_EVERY while running in. Its bullets ride the arrow fx art
    // (Game.SpawnBulletArrow).
    // ZERO Rng draws: the orbit side swaps deterministically every cycle, so run streams
    // never diverge on boss behaviour. Every number below but MINI_STAND_LEN is an
    // assistant pick, flagged for the gated retune.
    int _miniPhase = 0;          // 0 run-in | 1 circle | 2 stand
    float _miniClock = 0.0f;     // timer inside the current phase
    float _miniFanClock = 0.0f;  // fan cadence while circling
    float _miniShotClock = 0.0f; // enrage shot cadence while running in
    int _miniSide = 1;           // orbit direction, flips every cycle
    readonly float MINI_STOP_DIST = 220.0f;   // MAXIMUM leash only (2026-11-02 follow-up: the pinned ring is gone - see MINI_CLOSE_DIST)
    readonly float MINI_CLOSE_DIST = 110.0f;  // closest the dance holds: melee swings (Baldrun 130) can actually bite it (assistant pick)
    readonly float MINI_FAN_DIST = 150.0f;    // fans only loose with ROOM to dodge them (assistant pick): closer than this the mini circles SILENTLY while melee presses it
    readonly float MINI_RUN_SPEED = 130.0f;   // run-in charge vs the old 42 drift: user asked FASTER (assistant pick)
    readonly float MINI_ORBIT_SPEED = 120.0f; // px/s along the ring (constant-px/s discipline, assistant pick)
    readonly float MINI_ORBIT_LEN = 4.0f;     // circle-leg length (assistant pick: ~1/3 of a full lap)
    readonly float MINI_STAND_LEN = 3.0f;     // USER number: the 3 s slow beat after the volleys
    readonly float MINI_STAND_SPEED = 45.0f;  // assistant pick: ring travel px/s during that beat ("move slowly")
    readonly float MINI_FAN_EVERY = 1.2f;     // fan volley cadence while circling (assistant pick)
    readonly int MINI_FAN_N = 5;              // fan width (assistant pick)
    readonly float MINI_FAN_STEP = 0.16f;     // radians between fan spokes (assistant pick)
    readonly float MINI_ARROW_SPEED = 200.0f; // mini arrows are fast (assistant pick)
    readonly float MINI_ENRAGE_EVERY = 1.0f;  // aimed run-in shot cadence under half hp (assistant pick)
    bool _miniSaidDance = false;  // one-time audit prints so the headless bench can see the lanes
    bool _miniSaidEnrage = false;

    // the beat a boss node belongs to is carried in its kind. User 2026-11-03 normalized
    // BOTH non-mini bosses (boss1 / final) to the mini-boss profile, so every boss now
    // reads the same dance and camp routing; only the scene's beat slot differs.
    private bool IsDanceKind()
    {
        return kind == "mini1" || kind == "mini2" || kind == "mini"
            || kind == "boss1" || kind == "boss2" || kind == "final";
    }

    public override void _EnterTree()
    {
        hp = maxHp;
        _body = new ColorRect();
        bool isMini = IsDanceKind();
        // user 2026-11-03: boss1 and final are normalized to the mini-boss, so all four
        // authored bosses use the 60 px mini body. The size/colour branches remain as a
        // fallback for any future non-dance kind.
        float w = isMini ? 60.0f : (kind == "boss1" ? 80.0f : 96.0f);
        _body.Size = new Vector2(w, w);
        _body.Position = -_body.Size * 0.5f;
        _body.Color = isMini ? new Color(0.55f, 0.15f, 0.45f)
                             : kind == "boss1" ? new Color(0.62f, 0.34f, 0.10f)
                                               : new Color(0.65f, 0.10f, 0.10f);
        _rig = new Godot.Node2D();
        _rig.Name = "Rig";
        AddChild(_rig);
        _rig.AddChild(_body);
        // BOSS ART (user 2026-11-11 rig-only: MINIS ride the goblin-rider ARCHER canvas - they
        // loose arrow fans - the BOSSES ride the plain goblin-rider; both scaled up and ringed with a
        // RED edge highlight; bosses get reworked later, this wiring is visual-only). The rider canvases
        // are the user's untracked WIP; a MISSING load keeps the coloured greybox like every art rail.
        string artPath = (kind == "mini1" || kind == "mini2" || kind == "mini")
            ? "res://art/enemies/stage1/goblinriderarcher.png"
            : "res://art/enemies/stage1/goblinrider.png"; // boss1 / boss2 / final
    // HITBOX = ART on the shared surface (Combatant.hitR): the boss keeps its AUTHORED
    // contact radius below (the scene authors radius on BossMini/BossFinal), but the shot
    // lane rides the drawn silhouette, so an arrow that visibly touches the painted boss
    // bites it. Greybox first, art overwrites it a few lines down.
    hitR = radius;
        Godot.Texture2D tex = GD.Load(artPath) as Godot.Texture2D;
        if (tex != null)
        {
            float th = (kind == "mini1" || kind == "mini2" || kind == "mini") ? 88.0f : 108.0f; // target heights: bosses ride BIGGER than the 46 px riders (assistant picks - retunable)
            float texH = (float)tex.GetHeight();
            if (texH > 0.0f)
            {
                float sc = th / texH;
                _bSc = sc; _rimSpr = new Godot.Sprite2D[8]; // the always-face mirror owns these per frame
    float drawnW = (float)tex.GetWidth() * sc;
    hitR = (drawnW > th ? drawnW : th) * 0.5f; // the DRAWN canvas half-extent: ~54 px on the 88 px mini, so the old 8 px + radius bolt core stays honest
                for (int i = 0; i < 8; i++) // the RED edge highlight: 8 copies ringing behind the art at a 3 px radius (assistant pick)
                {
                    float a = (float)(i * System.Math.PI / 4.0);
                    Godot.Sprite2D rim = new Godot.Sprite2D();
                    rim.Texture = tex;
                    rim.Name = "Rim"; // eight same-named copies, Godot renames freely
                    rim.Position = new Vector2((float)System.Math.Cos(a) * 3.0f, (float)System.Math.Sin(a) * 3.0f);
                    rim.Scale = new Vector2(sc, sc);
                    rim.Modulate = new Color(1.0f, 0.18f, 0.18f); // red highlight (assistant pick)
                    _rig.AddChild(rim);
                    _rimSpr[i] = rim;
                }
                Godot.Sprite2D art = new Godot.Sprite2D();
                art.Texture = tex;
                art.Name = "Art";
                art.Scale = new Vector2(sc, sc);
                _artSpr = art;
                _rig.AddChild(art); // on top of the rim ring: the RED only shows as the edge halo
                _body.Visible = false; // greybox stands only while the art is missing
            }
        }
        // P1: NO RegisterEnemy here. An inactive boss must not be in the hittable
        // registry or weapons can pre-kill it (the old pre-killable mini softlocked
        // the run at 90 s). Game.ActivateBoss brings it in the moment its stage
        // engages; until then it is parked far offscreen AND hidden, because a
        // wrapping world has no offscreen: absolute coords loop back into frame.
        active = false;
        Visible = false;
        GlobalPosition = new Vector2(0.0f, -10000.0f);
    }

    public override void _Process(double d)
    {
        if (!active || hp <= 0.0f)
            return;
        float delta = (float)d;
        TickVuln((double)delta); // a marching boss decays its mark; dormant bosses hold still
        TickBurn((double)delta); // bosses burn too: the beat loop cannot stall past a DoT
        TickBleed((double)delta); // bosses bleed too: same own-pair discipline as burn
        TickPoison((double)delta); // bosses get poisoned too: same own-pair discipline
        Game g = GetParent() as Game;
        if (g == null)
            return;
        if (g.GetWorldPause())
            return; // picker / join-banner soft pause: the boss holds its volley mid-phase
        // ALWAYS-FACE law (the Enemy.cs twin above): bosses ride the rider canvases too, authored
        // LEFT. Art + red rim mirror together so the halo reads identical; visual only - the
        // hitbox is the boss node.
        if (_artSpr != null)
        {
            PartyMember face = g.NearestMember(GlobalPosition);
            if (face != null)
            {
                float fdx = g.WrapDelta(GlobalPosition, face.GlobalPosition).X;
                if (fdx > 4.0f || fdx < -4.0f)
                	_bFace = fdx > 0.0f ? -1.0f : 1.0f; // ally RIGHT: mirror the left-authored canvas
            }
            _artSpr.Scale = new Vector2(_bSc * _bFace, _bSc);
            if (_rimSpr != null)
            	for (int bi = 0; bi < 8; bi++)
            		_rimSpr[bi].Scale = new Vector2(_bSc * _bFace, _bSc); // the halo mirrors with the art
        }
        float ratio = hp / maxHp;
        _phase = ratio > 0.66f ? 0 : (ratio > 0.33f ? 1 : 2);
        // mini slide (user 2026-10-21): the boss is a big painted miniature being
        // shoved across the table - lateral sway + lean, like the party, scaled up
        _rigT += delta;
        float ph = _rigT * 4.2f;
        float slid = (float)System.Math.Sin(ph) * (IsDanceKind() ? 3.0f : 4.5f);
        _rig.Position = new Vector2(slid, 0.0f);
        _rig.Rotation = slid * 0.012f;

        // body-check the NEAREST live member, not the Leader specifically: the boss
        // must punish whoever stands in its way (downed bodies handled below)
        PartyMember target = g.NearestMember(GlobalPosition);
        _contactCd -= delta;
        if (target != null && !target.downed && !target.eliminated)
        {
            Vector2 to = g.WrapDelta(GlobalPosition, target.GlobalPosition);
            float dist = to.Length();
            // dance bosses ignore this drift lane: the BOSS DANCE below owns movement,
            // and its LIVE clamp lets melee close in and forces a back-off (user 2026-11-02).
            // The drift branch remains for a future non-dance kind.
            if (!IsDanceKind() && dist > 100.0f)
                GlobalPosition = g.WrapNearCamera(GlobalPosition + to.Normalized() * driftSpeed * delta * (burnT > 0.0 && g.LeaderDrink() ? 0.95f : 1.0f)); // Have a Drink!: a burning boss drifts slower - HALF the effect on bosses (user 2026-11-06)
            if (dist < radius + g.MEMBER_RADIUS && _contactCd <= 0.0f)
            {
                _contactCd = 0.8f;
                g.TakeHit(target, 12.0 * g.DrinkMul(this)); // Have a Drink!: a burning boss hits 10% softer
            }
        }
        // downed bodies still get body-checked by the boss (toroidal: a body across a
        // seam IS within reach, so measure the wrap-short way)
        PartyMember hitAny = g.NearestMemberAny(GlobalPosition);
        if (hitAny != null && hitAny.downed &&
            g.WrapDelta(GlobalPosition, hitAny.GlobalPosition).Length() < radius + g.MEMBER_RADIUS && _contactCd <= 0.0f)
        {
            _contactCd = 0.8f;
            g.TakeHit(hitAny, 12.0 * g.DrinkMul(this)); // Have a Drink!: a burning boss hits 10% softer
        }

        // EVERY authored boss runs the dance now (user 2026-11-03: boss1 and final are
        // exactly the mini-boss for now). The telegraph -> ring/fan/spokes block below is
        // a fallback for a future non-dance kind, not reached by mini1/boss1/mini2/final.
        if (IsDanceKind())
        {
            MiniDance(g, delta, target);
            return;
        }
        _eventTimer -= delta;
        if (_eventTimer > 0.0f)
            return;
        if (!_telegraph)
        {
            _telegraph = true;
            _body.Modulate = new Color(3.0f, 3.0f, 3.0f);
            _eventTimer = 0.7f; // telegraph window, then the attack fires
            return;
        }
        _telegraph = false;
        // the telegraph flash ends with the volley: without this reset the boss stays
        // blown-out white forever after its first wind-up
        _body.Modulate = new Color(1.0f, 1.0f, 1.0f);
        _eventTimer = 2.2f - 0.3f * (float)_phase;
        _ringOffset += 0.35f;

        Vector2 p = GlobalPosition;
        PartyMember tgt = g.NearestMember(p);
        Vector2 aimv = tgt != null ? g.WrapDelta(p, tgt.GlobalPosition) : new Vector2(1.0f, 0.0f);
        float aim = (float)System.Math.Atan2(aimv.Y, aimv.X);
        float speed = 110.0f + 25.0f * (float)_phase;

        // ring primitive
        int ringN = 8 + _phase * 2;
        for (int i = 0; i < ringN; i++)
        {
            float a = (float)(i * System.Math.PI * 2.0 / ringN) + _ringOffset;
            g.SpawnBullet(p, new Vector2((float)System.Math.Cos(a), (float)System.Math.Sin(a)) * speed, bulletDamage, bossShot: true);
        }
        // aimed fan primitive (phases 1+)
        if (_phase >= 1)
        {
            for (int i = -1; i <= 1; i++)
            {
                float a2 = aim + i * 0.18f;
                g.SpawnBullet(p, new Vector2((float)System.Math.Cos(a2), (float)System.Math.Sin(a2)) * (speed + 40.0f), bulletDamage, bossShot: true);
            }
        }
        // spokes primitive (phase 2)
        if (_phase == 2)
        {
            for (int i = 0; i < 4; i++)
            {
                float a3 = aim + (float)(i * System.Math.PI / 4.0) + 0.4f;
                g.SpawnBullet(p, new Vector2((float)System.Math.Cos(a3), (float)System.Math.Sin(a3)) * (speed * 1.3f), bulletDamage, bossShot: true);
            }
        }
    }

    // the choreographed boss fight (see BOSS DANCE fields up top): run-in -> aimed fans
    // on the move -> 3 s stand -> repeat. Toroidal-safe: every reposition wraps through
    // the same WrapNearCamera lane the drift used, and distances measure the wrap-short
    // way, so an orbit across a screen seam still reads as one circle
    private void MiniDance(Game g, float delta, PartyMember target)
    {
        if (target == null)
            return; // nobody standing to dance around: the mini holds still
        float slow = (burnT > 0.0 && g.LeaderDrink()) ? 0.95f : 1.0f; // Have a Drink!: a burning mini dances a beat slower - HALF the effect on bosses (user 2026-11-06)
        Vector2 to = g.WrapDelta(GlobalPosition, target.GlobalPosition); // boss -> party
        float dist = to.Length();
        _miniClock += delta;
        if (_miniPhase == 0) // RUN-IN: close in from far away, settle inside the MAXIMUM leash
        {
            if (dist > MINI_STOP_DIST)
            {
                GlobalPosition = g.WrapNearCamera(GlobalPosition + to.Normalized() * MINI_RUN_SPEED * slow * delta);
                if (hp < maxHp * 0.5f) // ENRAGED: getting into position now adds aimed shots
                {
                    _miniShotClock += delta;
                    if (_miniShotClock >= MINI_ENRAGE_EVERY)
                    {
                        _miniShotClock = 0.0f;
                        if (!_miniSaidEnrage)
                        {
                            _miniSaidEnrage = true;
                            GD.Print("[10thAge] (dance) " + kind + " under half hp: run-in shots live");
                        }
                        g.SpawnBulletArrow(GlobalPosition, to.Normalized() * MINI_ARROW_SPEED, bulletDamage, false, bossShot: true);
                    }
                }
            }
            else
            {
                _miniPhase = 1;
                _miniClock = 0.0f;
                _miniSide = -_miniSide; // orbit side swaps every cycle: predictable, zero Rng
                _miniFanClock = 0.5f;   // first fan loose as the circle opens
                if (!_miniSaidDance)
                {
                    _miniSaidDance = true;
                    GD.Print("[10thAge] (dance) " + kind + " settled: circling with aimed fans, slow crawl between cycles");
                }
            }
            return;
        }
        if (_miniPhase == 1) // CIRCLE: hold the pinned ring, sweep it, fans aimed AT the party
        {
            // 2026-11-02 follow-up (user: "stuck at a set range... remove any static dependencies
            // so players can get near it before it runs away"): the ring is NOT pinned any more.
            // The orbit holds the distance it is ACTUALLY at, clamped to [MINI_CLOSE_DIST, MINI_STOP_DIST]:
            // close the gap and the mini backs AWAY to MINI_CLOSE_DIST (inside Baldrun's 130 swing, so
            // melee can bite); back off past the leash and it chases. Cycle shape is untouched.
            float hold = dist;
            if (hold > MINI_STOP_DIST)
                hold = MINI_STOP_DIST;
            if (hold < MINI_CLOSE_DIST)
                hold = MINI_CLOSE_DIST;
            Vector2 dir = (dist < 1.0f) ? new Vector2(1.0f, 0.0f) : to.Normalized();
            Vector2 rel = dir * hold;
            float omega = MINI_ORBIT_SPEED / hold; // rad/s so ring travel reads at MINI_ORBIT_SPEED px/s at whatever radius it holds
            Vector2 want = target.GlobalPosition - rel.Rotated(_miniSide * omega * delta);
            Vector2 drift = g.WrapDelta(GlobalPosition, want); // boss -> ring point, wrap-short way
            float step = MINI_RUN_SPEED * slow * delta; // user 2026-11-02: RUN to the ring point, never teleport ("if it's already too close, it teleports back to the set distance... have it run there instead"). Cap >= MINI_ORBIT_SPEED so an on-ring sweep still lands exactly on the swept point; only a too-close/seam miss forces a chase
            if (drift.Length() > step)
                GlobalPosition = g.WrapNearCamera(GlobalPosition + drift.Normalized() * step);
            else
                GlobalPosition = g.WrapNearCamera(want);
            _miniFanClock -= delta;
            if (_miniFanClock <= 0.0f)
            {
                _miniFanClock = MINI_FAN_EVERY; // the cadence ticks even while gated: shots resume the moment the party gives room
                if (dist >= MINI_FAN_DIST) // 2026-11-02 follow-up (assistant pick, retunable): a fan loosed at 110 px had no dodge time and wiped benches; fans wait for ROOM
                {
                    float aim = (float)System.Math.Atan2(to.Y, to.X); // straight at the party, never a ring
                    for (int i = -(MINI_FAN_N / 2); i <= MINI_FAN_N / 2; i++)
                    {
                        float a = aim + (float)i * MINI_FAN_STEP;
                        g.SpawnBulletArrow(GlobalPosition, new Vector2((float)System.Math.Cos(a), (float)System.Math.Sin(a)) * MINI_ARROW_SPEED, bulletDamage, false, bossShot: true);
                    }
                }
                // closer than MINI_FAN_DIST: the mini keeps circling SILENTLY while melee presses it (user:
                // "players can get near to it before it tries to run away and repeat its cycle of shooting")
            }
            if (_miniClock >= MINI_ORBIT_LEN)
            {
                _miniPhase = 2;
                _miniClock = 0.0f;
            }
            return;
        }
        // SLOW BEAT (2026-11-02 user: "keep them moving slowly after they do their 3 rounds of
        // arrow volleys instead of standing still"): same pinned-ring sweep as the circle leg,
        // just at MINI_STAND_SPEED px/s - a crawl, still NO fans - then the cycle repeats
        float hold2 = dist; // same LIVE clamp as the circle leg: the crawl keeps whatever distance it is at
        if (hold2 > MINI_STOP_DIST)
            hold2 = MINI_STOP_DIST;
        if (hold2 < MINI_CLOSE_DIST)
            hold2 = MINI_CLOSE_DIST;
        Vector2 dir2 = (dist < 1.0f) ? new Vector2(1.0f, 0.0f) : to.Normalized();
        Vector2 rel2 = dir2 * hold2;
        float omega2 = MINI_STAND_SPEED / hold2; // rad/s that keeps ring travel at the crawl pace
        Vector2 want2 = target.GlobalPosition - rel2.Rotated(_miniSide * omega2 * delta);
        Vector2 drift2 = g.WrapDelta(GlobalPosition, want2);
        float step2 = MINI_RUN_SPEED * slow * delta; // cap > crawl speed: an on-ring sweep lands exactly on the swept point
        if (drift2.Length() > step2)
            GlobalPosition = g.WrapNearCamera(GlobalPosition + drift2.Normalized() * step2);
        else
            GlobalPosition = g.WrapNearCamera(want2);
        if (_miniClock >= MINI_STAND_LEN)
        {
            _miniPhase = 0;
            _miniClock = 0.0f;
            _miniShotClock = 0.0f; // the enrage cadence starts fresh on every run-in
        }
    }
}
