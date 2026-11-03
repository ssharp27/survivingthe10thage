using Godot;

/// <summary>
/// M1 greybox baseline enemies. Three archetypes: goblin (slow swarm clump), hobgoblin
/// (tough dark-green bruiser, one per ten goblins), and wolf (fast straight chaser).
/// No physics bodies: pooled Node2Ds with circle overlap math; separation is a cheap
/// n-bounded push.
/// </summary>
public partial class Enemy : Combatant
{
    public float speed = 75.0f;
    public float xpValue = 1.0f;
    float _contactCd = 0.0f;
    // ownership flag for Game's live-enemy counter: exactly one ReleaseLiveCount per
    // registration (weapon kill, reaper self-despawn, ClearEnemies, kill-scan cleanup),
    // so the horde cap can never leak upward and stop binding
    public bool counted = false;
    // field-recruit escort (user design 2026-10-20): the goblins that cage a stranded
    // PC creep toward escortHold and stop - they pressure the scene instead of dogging
    // the Leader like the rest of the horde. Killing them all is what recruits the PC.
    public bool escort = false;
    public Vector2 escortHold = new Vector2();
    // TIER-3 CREATURES (user 2026-11-03: wolf riders + goblin mage). HP/speed/contact/XP
    // ride the Director like every other body; these AI knobs are assistant picks unless
    // marked USER. rider = sword rider (chases, contact 4); riderbow = bow rider (holds a
    // close band, circles the squad like the mini dance, aimed arrow every 3 s = USER);
    // mage = goblin mage (retreats, lobs slow fireballs; cadence assistant pick).
    public float rangedDamage = 0.0f; // bow 3 / mage 6 = USER numbers (set by Director)
    private float _shotCd = 0.0f;

    // fx-feel10 audit: did this body ever loose a shot? first loose prints once,
    // so the bench counts loosed lanes instead of spamming the horde.
    bool _firedShot = false;
    private readonly float RIDER_MIN = 170.0f;   // closest the bow rider holds (assistant pick)
    private readonly float RIDER_MAX = 300.0f;   // farther than this it closes back in (assistant pick)
    private readonly float RIDER_ARROW_SPEED = 190.0f; // px/s (assistant pick)
    private readonly float RIDER_SHOT_EVERY = 3.0f; // USER number: arrow every 3 s
    private readonly float RIDER_SHOT_RANGE = 420.0f; // looses only with reach on a squad body (assistant pick)
    private readonly float MAGE_RETREAT = 420.0f; // backs off inside this (assistant pick)
    private readonly float MAGE_RETURN = 620.0f;  // past this it rejoins the march (assistant pick)
    private readonly float MAGE_FIRE_SPEED = 130.0f; // "slow moving fireball" - px/s is assistant pick
    private readonly float MAGE_SHOT_RANGE = 560.0f; // fireball range (assistant pick)
    private readonly float MAGE_SHOT_EVERY = 6.0f; // mage cadence (assistant pick: 4 s fireballs + bow-rider arrows bled the Bruno seat to TPK, so opened up; retunable post-playtest)

    Godot.ColorRect _body;
    // mini-style motion rig (user 2026-10-21): enemies do NOT get the party's table
    // slide (they are creatures, not minis) - they get a walk bob on this pivot so
    // the theme contrasts; art/placeholder swap reparents here
    Godot.Node2D _rig;
    float _bobT = 0.0f;
    float _bobPhase = 0.0f;

    // Visuals are built after spawn parameters land (Director sets kind/hp/speed post-new()),
    // so this runs on demand rather than in _EnterTree.
    public void InitVisuals()
    {
        if (_rig == null)
        {
            _rig = new Godot.Node2D();
            _rig.Name = "Rig";
            AddChild(_rig);
            _bobPhase = (GetInstanceId() % 64) * 0.37f; // the horde never bobs in lockstep
        }
        _shotCd = kind == "riderbow" ? 3.0f : (kind == "mage" ? 4.0f : 0.0f); // first shot never snaps on spawn
        _firedShot = false; // fx-feel10: audit flag lands when the shot clock does
        if (kind == "reaper")
        {
            // TEST death-state reaper: big red diamond; touching it DOWNs a member
            // (death saves start), attack-proof
            _body = new ColorRect();
            _body.Size = new Vector2(72.0f, 72.0f);
            _body.Position = -_body.Size * 0.5f;
            _body.Rotation = 0.785f;
            _body.Color = new Color(0.85f, 0.12f, 0.12f);
            _rig.AddChild(_body);
            return;
        }
        float w = kind == "wolf" ? 34.0f : (kind == "hobgoblin" ? 32.0f : (kind == "rider" || kind == "riderbow" ? 40.0f : 26.0f));
        _body = new ColorRect();
        _body.Size = new Vector2(w, w * 0.8f);
        _body.Position = -_body.Size * 0.5f;
        // hobgoblin: a darker, meaner green than the goblin clump (user art note)
        _body.Color = kind == "wolf" ? new Color(0.55f, 0.58f, 0.62f)
                              : kind == "hobgoblin" ? new Color(0.13f, 0.26f, 0.12f)
                                                    : kind == "rider" ? new Color(0.45f, 0.34f, 0.28f) // dusty mount (assistant pick)
                                                    : kind == "riderbow" ? new Color(0.38f, 0.36f, 0.47f) // steel-tinted saddle (assistant pick)
                                                    : kind == "mage" ? new Color(0.42f, 0.22f, 0.55f) // robe violet (assistant pick)
                                                    : new Color(0.30f, 0.48f, 0.23f);
        _rig.AddChild(_body);
    }

    public override void _Process(double d)
    {
        float delta = (float)d;
        TickVuln((double)delta); // guiding-light window decays on every body, even off-screen
        TickSlow((double)delta); // draging-maul window decays on every body, like guiding light
        TickBurn((double)delta); // firebolt burn ticks like the vuln/slow windows do
        TickBleed((double)delta); // Bruno's bleed ticks on the same discipline (own pair)
        TickPoison((double)delta); // Brook's venom ticks on the same own-pair discipline
        Game g = GetParent() as Game; // hoisted: the drink seat gates the spd calc below
        if (g == null)
            return;
        float spd = speed * (slowT > 0.0 ? 1.0f - (float)slowMul : 1.0f) * (burnT > 0.0 && g.LeaderDrink() ? 0.90f : 1.0f); // Have a Drink!: burning bodies shamble
        if (_body == null)
            InitVisuals();
        if (kind == "dead")
            return; // despawned: parked offscreen and inert; the node is NOT freed, so
                    // without this guard a dead reaper would walk back into the arena
        if (g.GetWorldPause())
            return; // picker / join-banner soft pause: the horde holds still while the card is read
        // creature walk bob (user 2026-10-21): enemies are creatures, not minis - vertical
        // step bob instead of the party's table slide, phase per spawn so the horde
        // never reads as one marching block; visuals only, never the hitbox
        if (_rig != null)
        {
            _bobT += delta;
            float amp = kind == "hobgoblin" ? 2.2f : kind == "wolf" ? 1.8f : 1.4f;
            _rig.Position = new Vector2(0.0f, (float)System.Math.Sin(_bobT * 8.0f + _bobPhase) * amp);
        }
        Vector2 p = GlobalPosition;
        if (kind == "reaper")
        {
            // TEST reaper: chase the nearest ally, DOWN the first body it touches
            // (starts the death-save cycle), then despawn. Attack-proof via its huge hp;
            // damage logic needed. kind "dead" + offscreen = clean despawn without
            // freeing (kill scan drops the registry entry, no XP).
            PartyMember prey = g.NearestMember(p);
            if (prey != null)
                GlobalPosition = g.WrapNearCamera(GlobalPosition +
                                             g.WrapDelta(GlobalPosition, prey.GlobalPosition).Normalized() * spd * delta);
            PartyMember touch = g.NearestMemberAny(GlobalPosition);
            if (touch != null && g.WrapDelta(GlobalPosition, touch.GlobalPosition).Length() < 36.0f + g.MEMBER_RADIUS
                && g.OnReaperDown(touch))
            {
                // despawn only when a down actually landed: immune bodies let it lurk
                kind = "dead";
                GlobalPosition = new Vector2(0.0f, -10000.0f);
                Visible = false; // a wrapping world has no offscreen: absolute parking
                                 // loops back into frame, so hidden is the real despawn
                // the counter is owned by the enemy: self-despawn must release its slot
                // too, or every leak would make the live-cap readout drift upward
                g.ReleaseLiveCount(this);
            }
            return;
        }
        if (escort)
        {
            // escort cage (pass 11): hold still until the party can SEE the rescue,
            // then close ON the stranded PC himself. contact chips his HP - whatever
            // survives is what joins - so dawdling on the rescue costs a maimed
            // recruit. they still never swing at the living squad
            if (g.RecruitInView())
            {
                // track the RECRUIT'S LIVE (folded) position, not the spawn-time hold:
                // the PC wraps like every other entity, so a stale absolute point would
                // send the cage shuffling off-world once the beat folds across a seam
                PartyMember pc = g.GetRecruitPending();
                Vector2 goal = pc != null ? pc.GlobalPosition : escortHold;
                Vector2 hold = g.WrapDelta(p, goal);
                float hl = hold.Length();
                if (hl > 18.0f)
                    GlobalPosition = g.WrapNearCamera(GlobalPosition +
                                   hold.Normalized() * spd * g.GetEscortCrawl() * delta);
                _contactCd -= delta;
                if (hl < 30.0f && _contactCd <= 0.0f)
                {
                    _contactCd = 1.0f;
                    g.EscortHitRecruit(g.GetRecruitPending(), contactDamage * g.DrinkMul(this)); // Have a Drink!: burning escorts chip softer too
                }
            }
        }
        else
        {
            PartyMember m = g.NearestMember(GlobalPosition);
            _contactCd -= delta;
            if (m != null)
            {
                // chase measures the WRAP-short way: an ally just across a seam is
                // 100 px away, not the 2300 px the raw coords would read
                Vector2 to = g.WrapDelta(p, m.GlobalPosition);
                float dist = to.Length();
                Vector2 dir;
                if (kind == "riderbow")
                {
                    // BOW RIDER (user 2026-11-03: "tries to circle around the player similar to
                    // the mini-boss"): hold the band and slide along it at constant px/s, same
                    // constant-px/s discipline as the dance; side is instance-stable so a rider
                    // never jitter-reverses mid-orbit.
                    if (dist > RIDER_MAX) dir = to.Normalized();
                    else if (dist < RIDER_MIN) dir = -to.Normalized();
                    else
                    {
                        float side = (GetInstanceId() % 2 == 0) ? 1.0f : -1.0f;
                        dir = new Vector2(-to.Y, to.X).Normalized() * side;
                    }
                }
                else if (kind == "mage")
                {
                    // GOBLIN MAGE (user: "try to run away from the player"): back off inside the
                    // retreat radius, hold at its edge, rejoin the march only if it drifted too
                    // far - wrapping world gives "flee" no walls, so the return leg is the leash.
                    if (dist < MAGE_RETREAT) dir = -to.Normalized();
                    else if (dist > MAGE_RETURN) dir = to.Normalized();
                    else dir = new Vector2(0.0f, 0.0f);
                }
                else
                {
                    float wobble = kind == "goblin" ? (float)System.Math.Sin(p.X * 0.05 + p.Y * 0.03) * 26.0f : 0.0f;
                    dir = (to + new Vector2(wobble * 0.4f, wobble)).Normalized();
                }
                if (dir.LengthSquared() > 0.0001f)
                    GlobalPosition += dir * spd * delta; // draging maul: a dragged body shambles
                GlobalPosition = g.WrapNearCamera(GlobalPosition); // fold instead of walls
                if (dist < 8.0f + g.MEMBER_RADIUS && _contactCd <= 0.0f)
                {
                    _contactCd = 1.0f;
                    g.TakeHit(m, contactDamage * g.DrinkMul(this)); // Have a Drink!: burning bodies hit 10% softer
                }
                // TIER-3 ranged lanes fire on their own cadence (rider: USER 3 s; mage: assistant
                // pick). Aiming rides the LIVE squad position, same discipline as the mini fans.
                if ((kind == "riderbow" || kind == "mage") && m != null)
                {
                    _shotCd -= delta;
                    bool inReach = kind == "riderbow" ? dist <= RIDER_SHOT_RANGE : dist <= MAGE_SHOT_RANGE;
                    if (_shotCd <= 0.0f && inReach)
                    {
                        _shotCd = kind == "riderbow" ? RIDER_SHOT_EVERY : MAGE_SHOT_EVERY;
                        Vector2 aim = g.WrapDelta(GlobalPosition, m.GlobalPosition).Normalized();
                        if (kind == "riderbow")
                            g.SpawnBulletArrow(GlobalPosition, aim * RIDER_ARROW_SPEED, rangedDamage);
                        else
                            g.SpawnBulletFire(GlobalPosition, aim * MAGE_FIRE_SPEED, rangedDamage);

                        if (!_firedShot)
                        {
                            // fx-feel10 bench evidence: first loose prints once per ranged body
                            _firedShot = true;
                            GD.Print("[10thAge] (shot) " + kind + " loosed its first " + (kind == "mage" ? "fireball" : "arrow"));
                        }
                    }
                }
            }
            // downed bodies are still run into even though nobody chases them
            PartyMember hitAny = g.NearestMemberAny(p);
            if (hitAny != null && hitAny.downed &&
                g.WrapDelta(p, hitAny.GlobalPosition).Length() < 8.0f + g.MEMBER_RADIUS && _contactCd <= 0.0f)
            {
                _contactCd = 1.0f;
                g.TakeHit(hitAny, contactDamage * g.DrinkMul(this)); // Have a Drink!: burning bodies hit 10% softer
            }
        }
        // separation: n-bounded push apart (spatial hash replaces this in M2)
        foreach (Godot.Node2D n in g.GetEnemies())
        {
            if (n == this)
                continue;
            Vector2 e = n.GlobalPosition;
            float d2 = (e - p).LengthSquared();
            if (d2 < 400.0f && d2 > 0.0001f)
                GlobalPosition += (p - e).Normalized() * 14.0f * delta;
        }
        if (hurtFlash > 0.0f)
        {
            hurtFlash -= delta;
            _body.Modulate = new Color(2.2f, 1.4f, 1.4f);
        }
        else
            _body.Modulate = new Color(1.0f, 1.0f, 1.0f);
    }
}
