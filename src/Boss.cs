using Godot;

/// <summary>
/// Data-driven placeholder boss for the M1 timeline engine: three phases (100/66/33% HP),
/// every event is telegraph -> attack -> recovery, composed of ring/fan/spokes primitives.
/// Swap the pattern blocks for real encounter data later; the engine stays.
/// kind is inherited from Combatant; the scene author sets the beat it belongs to:
/// "mini1" / "boss1" / "mini2" / "final" (M2 beat loop). The kind is also what decides
/// what a death MEANS (Game.OnBossKilled: mini -> camp | mid -> keep marching | final -> win),
/// and the body's size / colour, so the loop is data in the scene, not four copies of a class.
/// Legacy "mini" still reads as a mini so an older scene keeps working.
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

    // MINI DANCE (user 2026-11-02): the mini-boss fight is choreography, not drift-and-ring.
    // Cycle: run toward the party and stop at MINI_STOP_DIST -> circle around it firing
    // fans AIMED at it (no rings anymore) -> crawl the ring slowly for MINI_STAND_LEN
    // (2026-11-02 user: "keep them moving slowly after they do their 3 rounds of arrow volleys
    // instead of standing still") -> repeat the whole cycle. The circle leg looses exactly 3
    // fans (0.5/1.7/2.9 s into the 4 s leg), so the slow beat lands right after the 3rd volley. Getting into position fires NOTHING,
    // until hp drops below half: then it adds one aimed arrow per MINI_ENRAGE_EVERY while
    // running in. Its bullets ride the arrow fx art (Game.SpawnBulletArrow).
    // ZERO Rng draws: the orbit side swaps deterministically every cycle, so run streams
    // never diverge on boss behaviour. Every number below but MINI_STAND_LEN is an
    // assistant pick, flagged for the gated retune.
    int _miniPhase = 0;          // 0 run-in | 1 circle | 2 stand
    float _miniClock = 0.0f;     // timer inside the current phase
    float _miniFanClock = 0.0f;  // fan cadence while circling
    float _miniShotClock = 0.0f; // enrage shot cadence while running in
    int _miniSide = 1;           // orbit direction, flips every cycle
    readonly float MINI_STOP_DIST = 220.0f;   // assistant pick: rapiers (320) and longbows reach it; Baldrun's 130 swing does NOT - honest, retune via gate
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

    // the beat a boss node belongs to is carried in its kind, so the body reads the
    // schedule: two minis (camp after either), one mid milestone, one stage end
    private bool IsMiniKind()
    {
        return kind == "mini1" || kind == "mini2" || kind == "mini";
    }

    public override void _EnterTree()
    {
        hp = maxHp;
        _body = new ColorRect();
        bool isMini = IsMiniKind();
        // mid boss sits between the two sizes: bigger than a mini, not the stage's finale
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
        float ratio = hp / maxHp;
        _phase = ratio > 0.66f ? 0 : (ratio > 0.33f ? 1 : 2);
        // mini slide (user 2026-10-21): the boss is a big painted miniature being
        // shoved across the table - lateral sway + lean, like the party, scaled up
        _rigT += delta;
        float ph = _rigT * 4.2f;
        float slid = (float)System.Math.Sin(ph) * (IsMiniKind() ? 3.0f : 4.5f);
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
            // close to melee reach: party signature weapons top out ~130px, so the
            // MID/FINAL bosses must settle inside that or they can never be struck.
            // Minis ignore this drift lane: the MINI DANCE below owns their movement,
            // and their 220 px settle keeps them OUT of Baldrun's 130 px swing (honest:
            // melee-leader seats lean on the ranged joins to strip a dancing mini)
            if (!IsMiniKind() && dist > 100.0f)
                GlobalPosition = g.WrapNearCamera(GlobalPosition + to.Normalized() * driftSpeed * delta * (burnT > 0.0 && g.LeaderDrink() ? 0.90f : 1.0f)); // Have a Drink!: a burning boss drifts slower
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

        // MINIS RUN THE DANCE, NOT the telegraph/ring engine (user 2026-11-02): the
        // mid milestone and the stage finale keep the old telegraph -> ring/fan/spokes
        // block below untouched
        if (IsMiniKind())
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
            g.SpawnBullet(p, new Vector2((float)System.Math.Cos(a), (float)System.Math.Sin(a)) * speed, bulletDamage);
        }
        // aimed fan primitive (phases 1+)
        if (_phase >= 1)
        {
            for (int i = -1; i <= 1; i++)
            {
                float a2 = aim + i * 0.18f;
                g.SpawnBullet(p, new Vector2((float)System.Math.Cos(a2), (float)System.Math.Sin(a2)) * (speed + 40.0f), bulletDamage);
            }
        }
        // spokes primitive (phase 2)
        if (_phase == 2)
        {
            for (int i = 0; i < 4; i++)
            {
                float a3 = aim + (float)(i * System.Math.PI / 4.0) + 0.4f;
                g.SpawnBullet(p, new Vector2((float)System.Math.Cos(a3), (float)System.Math.Sin(a3)) * (speed * 1.3f), bulletDamage);
            }
        }
    }

    // the choreographed mini fight (see MINI DANCE fields up top): run-in -> aimed fans
    // on the move -> 3 s stand -> repeat. Toroidal-safe: every reposition wraps through
    // the same WrapNearCamera lane the drift used, and distances measure the wrap-short
    // way, so an orbit across a screen seam still reads as one circle
    private void MiniDance(Game g, float delta, PartyMember target)
    {
        if (target == null)
            return; // nobody standing to dance around: the mini holds still
        float slow = (burnT > 0.0 && g.LeaderDrink()) ? 0.90f : 1.0f; // Have a Drink!: a burning mini dances a beat slower
        Vector2 to = g.WrapDelta(GlobalPosition, target.GlobalPosition); // boss -> party
        float dist = to.Length();
        _miniClock += delta;
        if (_miniPhase == 0) // RUN-IN: close in, stop at the set distance
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
                        g.SpawnBulletArrow(GlobalPosition, to.Normalized() * MINI_ARROW_SPEED, bulletDamage);
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
            Vector2 rel = (dist < 1.0f) ? new Vector2(MINI_STOP_DIST, 0.0f)
                                         : to.Normalized() * MINI_STOP_DIST; // the ring point the swept orbit wants this frame
            float omega = MINI_ORBIT_SPEED / MINI_STOP_DIST; // rad/s that keeps ring travel at MINI_ORBIT_SPEED px/s
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
                _miniFanClock = MINI_FAN_EVERY;
                float aim = (float)System.Math.Atan2(to.Y, to.X); // straight at the party, never a ring
                for (int i = -(MINI_FAN_N / 2); i <= MINI_FAN_N / 2; i++)
                {
                    float a = aim + (float)i * MINI_FAN_STEP;
                    g.SpawnBulletArrow(GlobalPosition, new Vector2((float)System.Math.Cos(a), (float)System.Math.Sin(a)) * MINI_ARROW_SPEED, bulletDamage);
                }
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
        Vector2 rel2 = (dist < 1.0f) ? new Vector2(MINI_STOP_DIST, 0.0f)
                                     : to.Normalized() * MINI_STOP_DIST;
        float omega2 = MINI_STAND_SPEED / MINI_STOP_DIST; // rad/s that keeps ring travel at the crawl pace
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
