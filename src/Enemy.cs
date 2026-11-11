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
    // user 2026-11-06 playtest ("only ONE mage attack in an entire run"): firsts PROVED
    // fire (308 mage first-looses across 459 corpus spawns), so the open question is the
    // REPEAT cadence. Per-body volley counter + a throttled print every 3rd mage shot
    // counts shots-per-body on the bench without spamming the horde.
    int _shotsLoosed = 0;
    // 2026-11-03 user: bow riders must zig-zag IN and OUT instead of holding a perfect
    // band, so short-range weapons can reach them. The zig bounds are assistant picks.
    bool _riderIn = true;
    float _riderSide = 1.0f;
    private readonly float RIDER_ZIG_MIN = 95.0f;   // closest the bow rider comes (assistant pick, inside Baldrun's 130 px swing)
    private readonly float RIDER_ZIG_MAX = 300.0f;  // farthest it backs off (assistant pick)
    private readonly float RIDER_ZIG_SIDE = 0.45f;  // tangential lean per zig: reads as a zig-zag, not a straight shuttle (assistant pick)
    private readonly float RIDER_ARROW_SPEED = 190.0f; // px/s (assistant pick)
    private readonly float RIDER_SHOT_EVERY = 4.0f; // USER number: 3 s + 1 s = 4 s per arrow
    private readonly float RIDER_SHOT_RANGE = 420.0f; // looses only with reach on a squad body (assistant pick)
    private readonly float MAGE_CAST = 300.0f;    // CLOSE to this before loosing (user 2026-11-04: "move close enough to cast"; pick)
    private readonly float MAGE_LOITER = 0.5f;    // the run-away leg rides this fraction of speed (assistant pick)
    private readonly float MAGE_FIRE_SPEED = 130.0f; // "slow moving fireball" - px/s is assistant pick
    private readonly float MAGE_SHOT_RANGE = 520.0f; // user 2026-11-06 playtest: ONLY one mage shot in a whole run. The lane holds the STAND band, but a mage walks 70 px/s and a kiting party walks 128 px/s, so the ring slips out from under it and the old 420 px gate went cold - 520 px keeps the band honest to the fireball it still throws
    private readonly float MAGE_SHOT_EVERY = 4.0f; // user 2026-11-06 playtest: 6 s read as "one mage shot a run"; 4 s is the cadence the user asked for, and the retune note above still applies (watch the Bruno seat)
    // TIER-4 OGRE (user 2026-11-05: this map's only tier-4 creature): big, slow, fat HP
    // (those ride the Director). The SLAM is the user lane: inside the cast ring it MARKS
    // a big circle, waits 1 s (USER number), then hits that area for 25 (USER number).
    // Cast ring / blast radius / cadence are assistant picks. The circle LOCKS where it
    // marks (assistant read: the beat is a dodge window, not a homing blast).
    private readonly float OGRE_CAST = 150.0f;   // user 2026-11-06 playtest: 240 was too far - ogres stacked and chained slams bled the party dead fast
    private readonly float OGRE_SLAM_R = 130.0f; // blast radius (assistant pick: the user's "big circle")
    private readonly float OGRE_SLAM_TELE = 1.0f; // windup seconds (USER number)
    private readonly float OGRE_SLAM_DMG = 25.0f; // blast damage (USER number)
    private readonly float OGRE_SLAM_CD = 6.0f;  // slam cadence (assistant pick: one volley per heal cycle)
    // slam state: windup countdown, cadence clock, LOCKED slam point, telegraph disc
    float _slamT = 0.0f;
    float _slamCd = 0.0f;
    Vector2 _slamPos = new Vector2();
    Godot.Polygon2D _slamDisc = null;
    // TIER VARIETY CREATURES (user 2026-11-07): GOBLIN 2 is a pure clump flavour (its stats
    // ride the Director; it walks the goblin lane, no new AI). FAST GOBLIN owns a rush/pause/
    // LUNGE lane: "rapidly approach the player, pause, then lunge in a straight line over a
    // set distance" (USER behaviour) with a JUMP animation on the visual rig (the user's
    // "maybe you can work out a jump animation for these?"). HP/speed/contact/XP are USER
    // numbers on the Director; trigger ring / pause length / lunge reach+speed / breath and
    // the hop geometry are assistant picks, retunable with the rest of the creature knobs.
    private int _fgState = 0;     // 0 rush | 1 pause (coil) | 2 lunge (spring)
    private float _fgT = 0.0f;    // pause countdown / lunge timer
    private float _fgCd = 0.0f;   // breath between lunges
    private float _fgDone = 0.0f; // px travelled along the locked line this spring
    private Vector2 _fgDir = new Vector2(); // the LOCKED straight-line bearing (USER)
    private bool _fgLeaped = false; // one print per body: first spring (bench audit)
    private readonly float FG_TRIGGER = 260.0f; // the pause/coil begins inside this ring (assistant pick)
    private readonly float FG_PAUSE = 0.6f;     // the pause beat (assistant length)
    private readonly float FG_LUNGE_DIST = 220.0f; // the set straight-line distance (assistant pick)
    private readonly float FG_LUNGE_SPD = 330.0f;  // spring px/s over the line (assistant pick)
    private readonly float FG_LUNGE_CD = 1.2f;     // breath after landing before the next rush (assistant pick)
    private readonly float FG_HOP = 16.0f;         // hop arc height px (visual rig only, never the hitbox)
    // WOLF-2 LONG-SIGHT CHARGE (user 2026-11-09): "similar to that goblin jumper ... but it
    // will charge from a long distance ... they will see players from a fairly long distance
    // away, pause, then dash in a straight line exactly at where the player is when they dash
    // ... they can go a fair distance passed the player too, like a long lunge" (USER). Same
    // coil-and-spring machine as the fastgoblin (shares the _fg* state), but a much wider
    // trigger ring, a longer coil, a much longer OVERSHOOTING spring, and NO hop - the mount
    // stays on the floor. Ring / beat / distance / speed / breath are assistant picks.
    private readonly float W2_TRIGGER = 520.0f;   // long-sight ring (assistant pick)
    private readonly float W2_PAUSE = 0.8f;       // the coil beat (assistant pick)
    private readonly float W2_DASH_DIST = 430.0f; // long lunge: full overshoot past the party (assistant pick)
    private readonly float W2_DASH_SPD = 360.0f;  // dash px/s (assistant pick)
    private readonly float W2_DASH_CD = 1.5f;     // breath between dashes (assistant pick)
    // MONSTER SWAY (user 2026-11-08 hand-play: the two-beat gait pass below made the head
    // vibrate and was SCRAPPED the same day - see the superseded note in RESUME_NOTES). Monsters
    // now ride the PARTY slide language (src/PartyMember.cs 1910-1936): bounce SIDE TO SIDE,
    // just FASTER because they are creatures, and the same set-down when they stop. AMP is the
    // lateral px per beat (the party sways 3.0), RATE the cadence in rad/s at full stride (the
    // party is 7.2), REF the px/s that reads as a full stride (the party 130), MIN the px/s below
    // which a body is standing. Assistant picks, retunable by feel - the USER owns the feel.
    private readonly float SWAY_AMP = 3.4f;
    private readonly float SWAY_RATE = 12.0f;
    private readonly float SWAY_REF = 130.0f;
    private readonly float SWAY_MIN = 30.0f;

    Godot.ColorRect _body;
    // painted creature art (user 2026-11-07 stage-1 enemy assignments): when the GD.Load
    // lands, the Sprite2D rides the bob rig and the greybox ColorRect hides (a MISSING
    // load keeps the placeholder - the art/enemies/ tree is the user's WIP, untracked)
    Godot.Sprite2D _spr = null;
    // mini-style motion rig (user 2026-10-21): enemies do NOT get the party's table
    // slide (they are creatures, not minis) - they get a walk bob on this pivot so
    // the theme contrasts; art/placeholder swap reparents here
    Godot.Node2D _rig;
    float _bobT = 0.0f;
    float _bobPhase = 0.0f;
    int _dustPlant = 0;               // which half-swing last planted grit (the gait clock counts up)
    bool _dustPrimed = false;         // first striding frame seeds the counter: never puffs the past
    Vector2 _lastMovePos = new Vector2(); // odometer: where the body stood last frame
    bool _movePrimed = false;             // the first frame has no previous checkpoint
    float _moveSpd = 0.0f;                // eased px/s: drives cadence + amplitude

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
            _lastMovePos = GlobalPosition; // the odometer starts at the spawn, not at the origin
            _movePrimed = true;
        }
        _shotCd = kind == "riderbow" ? RIDER_SHOT_EVERY : (kind == "mage" ? 2.0f : 0.0f); // user 2026-11-06: fresh mage looses its first ball after 2 s, not a full 4 s cycle - the volley lands while the party is still untangling the new pack
        _firedShot = false; // fx-feel10: audit flag lands when the shot clock does
        _shotsLoosed = 0; // 2026-11-06: volley counter lands with the clock
        if (kind == "ogre")
        {
            // telegraph disc built ONCE per body (the Alvora aura-disc precedent): a 24-gon
            // at blast radius, hidden until a mark, then repositioned per windup. Child of the
            // ENEMY, not the bobbing rig: the slam area must not walk.
            _slamDisc = new Godot.Polygon2D();
            _slamDisc.Name = "SlamDisc";
            _slamDisc.ZIndex = -1; // under the horde bodies: the fill marks the floor
            _slamDisc.Set("modulate", new Color(0.85f, 0.15f, 0.12f, 0.12f)); // blood-red telegraph fill
            Vector2[] disc = new Vector2[24]; // 24-gon: smooth enough at blast scale, zero Rng
            for (int i5 = 0; i5 < 24; i5++)
                disc[i5] = new Vector2((float)(System.Math.Cos(System.Math.PI * 2.0 * i5 / 24.0) * OGRE_SLAM_R), (float)(System.Math.Sin(System.Math.PI * 2.0 * i5 / 24.0) * OGRE_SLAM_R));
            _slamDisc.Polygon = disc;
            _slamDisc.Visible = false;
            AddChild(_slamDisc);
        }
        if (kind == "riderbow")
        {
            _riderIn = (GetInstanceId() % 2 == 0);
            _riderSide = (GetInstanceId() % 3 == 0) ? 1.0f : -1.0f;
        }
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
        float w = kind == "ogre" ? 64.0f : (kind == "wolf3" ? 40.0f : (kind == "wolf" || kind == "wolf2" ? 34.0f : (kind == "hobgoblin" ? 32.0f : (kind == "rider" || kind == "riderbow" ? 40.0f : (kind == "fastgoblin" ? 28.0f : 26.0f))))); // wolf3: the tankier wolf (assistant pick); ogre: the map's biggest body (assistant pick)
        _body = new ColorRect();
        _body.Size = new Vector2(w, w * 0.8f);
        _body.Position = -_body.Size * 0.5f;
        // hobgoblin: a darker, meaner green than the goblin clump (user art note)
        _body.Color = (kind == "wolf" || kind == "wolf2" || kind == "wolf3") ? new Color(0.55f, 0.58f, 0.62f)
                              : kind == "hobgoblin" ? new Color(0.13f, 0.26f, 0.12f)
                                                    : kind == "rider" ? new Color(0.45f, 0.34f, 0.28f) // dusty mount (assistant pick)
                                                    : kind == "riderbow" ? new Color(0.38f, 0.36f, 0.47f) // steel-tinted saddle (assistant pick)
                                                    : kind == "mage" ? new Color(0.42f, 0.22f, 0.55f) // robe violet (assistant pick)
                                                    : kind == "ogre" ? new Color(0.35f, 0.34f, 0.30f) // bog-brown brute (assistant pick)
                                                    : kind == "goblin2" ? new Color(0.18f, 0.44f, 0.40f) // teal dune clump variant (assistant pick)
                                                    : kind == "fastgoblin" ? new Color(0.62f, 0.72f, 0.20f) // acid-green sprinter (assistant pick)
                                                    : new Color(0.30f, 0.48f, 0.23f);
        _rig.AddChild(_body);
        // PAINTED CREATURE ART (user 2026-11-07 stage-1 assignments): goblin1 -> the clump
        // goblin, goblin2 -> Goblin 2, goblin3 -> Fast Goblin, hobgoblin -> Hobgoblin, all in
        // res://art/enemies/stage1/. GD.Load by path string only (same rail as the portraits);
        // a MISSING load keeps the greybox - the art/enemies/ tree is the user's WIP, untracked.
        string artPath = kind == "goblin" ? "res://art/enemies/stage1/goblin1.png"
                       : kind == "goblin2" ? "res://art/enemies/stage1/goblin2.png"
                         : kind == "fastgoblin" ? "res://art/enemies/stage1/goblin3.png"
                           : kind == "hobgoblin" ? "res://art/enemies/stage1/hobgoblin.png"
                              : kind == "wolf" ? "res://art/enemies/stage1/wolf1.png"
                                : kind == "wolf2" ? "res://art/enemies/stage1/wolf2.png"
                                  : kind == "wolf3" ? "res://art/enemies/stage1/wolf3.png"
                                    : kind == "rider" ? "res://art/enemies/stage1/goblinrider.png"
                                      : kind == "riderbow" ? "res://art/enemies/stage1/goblinriderarcher.png"
                                        : kind == "ogre" ? "res://art/enemies/stage1/ogre1.png" : "";
        if (artPath != "" && _spr == null)
        {
            Godot.Texture2D tex = GD.Load(artPath) as Godot.Texture2D;
            if (tex != null)
            {
                _spr = new Godot.Sprite2D();
                _spr.Texture = tex;
                _spr.Name = "Art";
                float th = kind == "ogre" ? 72.0f : kind == "hobgoblin" ? 52.0f : ((kind == "wolf" || kind == "wolf2" || kind == "wolf3") ? 38.0f : 46.0f); // wolves ride their OWN height: the ~120x80 landscape canvases would draw ~69 px wide at the 46 px humanoid target against the 34 px body (assistant pick - retunable); riders KEEP 46: their ~120x96 mount canvases draw ~57 px wide = the same mount span the wolf art gets at 38, and the goblin shares the canvas height (pick); ogre rides BIGGER still: 72 px target on its ~94x108 canvas draws ~63 px wide = its 64 px body, the tallest creature on the map (assistant pick - retunable)
                float texH = (float)tex.GetHeight();
                if (texH > 0.0f)
                {
                    float sc = th / texH;
                    _spr.Scale = new Vector2(sc, sc);
                }
                _rig.AddChild(_spr);
                _body.Visible = false; // greybox stands only while the art is missing
            }
        }
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
        // monster sway (user 2026-11-08 hand-play: the gait pass that made heads vibrate was
        // SCRAPPED the same day; the user asked for the PLAYER-character language instead).
        // This is the party rig law copied across from src/PartyMember.cs 1910-1936 - an
        // odometer of what the body ACTUALLY moved, an eased px/s, cadence scaled by that speed,
        // lateral sway + a half-rate dip + lean that trails the sway - with a FASTER cadence,
        // because creatures are not minis being pushed across a table. A body that stops swings
        // its last sway back LEVEL (a set-down, never a jolt). Visuals only: the hitbox is the
        // ENEMY node and the ogre telegraph disc is its child, so the sway cannot nudge anything.
        Vector2 gp = GlobalPosition;
        float moved = _movePrimed ? g.WrapDelta(_lastMovePos, gp).Length() : 0.0f;
        _lastMovePos = gp;
        _movePrimed = true;
        if (moved > 400.0f)
            moved = 0.0f; // wrap-fold frame, not motion (the party guard)
        float inst = delta > 0.001f ? moved / delta : _moveSpd;
        _moveSpd += (inst - _moveSpd) * (1.0f - (float)System.Math.Exp(-8.0 * delta));
        if (_rig != null)
        {
            if (_moveSpd > SWAY_MIN) // striding
            {
                float ratio = _moveSpd / SWAY_REF;
                ratio = ratio < 0.35f ? 0.35f : (ratio > 1.4f ? 1.4f : ratio);
                _bobT += delta * SWAY_RATE * ratio; // faster than the party 7.2 (assistant pick)
                float ph = _bobT + _bobPhase;       // the horde never sways in lockstep
                float sw = (float)System.Math.Sin(ph) * SWAY_AMP * (kind == "hobgoblin" ? 1.25f : kind == "wolf" || kind == "wolf2" ? 0.9f : kind == "wolf3" ? 0.75f : 1.0f);
                _rig.Position = new Vector2(sw, -(float)System.Math.Abs((float)System.Math.Sin(ph * 0.5)) * 1.2f); // side to side + half-rate dip
                _rig.Rotation = sw * 0.017f;        // the lean trails the sway (party constant)
                // grit on the plant (user 2026-11-09 hand-play: "give monsters a very small dirt-cloud
                // puff on their steps, so it reads better on the map as moving"): the foot that STOPS
                // the side-to-side swing plants on the ground, exactly where sin(ph) reaches an
                // extreme - every half-period of the gait clock. sw already IS that extreme, so the
                // puff lands on the planting side and a walk alternates left/right like the gait.
                // The pool jitters position + tilt so a clump never prints a row. PURE VFX: nothing
                // here touches motion, hitboxes or damage, and the set-down lane below never emits,
                // so a body that stops kicks nothing up.
                int plants = (int)System.Math.Floor((ph + 1.5707963267948966) / 3.141592653589793);
                if (!_dustPrimed)
                {
                    _dustPrimed = true;
                    _dustPlant = plants; // first striding frame: anchor, do not puff the past
                }
                else if (plants > _dustPlant)
                {
                    int steps = plants - _dustPlant;
                    if (steps > 2)
                        steps = 2; // a hitch frame must not spam the pool
                    _dustPlant = plants;
                    float feet = kind == "hobgoblin" ? 20.0f : 17.0f; // 2026-11-09 hand-play (user: "shift them a little higher/closer to the enemies' feet"): the art CENTRES on the node so half a sprite below IS the ground line - at 26/23 px the cloud sat with its middle ON the ground and read as sunk; riding a few px above that line reads as contact (pick - retunable)
                    for (int q = 0; q < steps; q++)
                        g.SpawnDustPuff(gp + new Vector2(sw, feet));
                }
            }
            else
            {
                float ease = 1.0f - (float)System.Math.Exp(-9.0 * delta);
                _rig.Position += -_rig.Position * ease;   // set-down, not a jolt (party law)
                _rig.Rotation += -_rig.Rotation * ease;
            }
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
                float moveSpd = spd; // FAST GOBLIN rewrites this on its spring leg; everyone else runs at spd
                if (kind == "riderbow")
                {
                    // BOW RIDER (user 2026-11-03 follow-up): no more perfect band. It
                    // zig-zags IN toward the squad, then OUT away from it, so short-range
                    // weapons can actually reach it. A small tangential lean keeps the
                    // path zig-zagging rather than shuttling along the same line.
                    // _riderIn is instance-stable: reaches the far bound -> commits in;
                    // reaches the near bound -> commits out; never jitter-reverses.
                    if (dist > RIDER_ZIG_MAX) _riderIn = true;
                    else if (dist < RIDER_ZIG_MIN) _riderIn = false;
                    Vector2 radial = _riderIn ? to.Normalized() : -to.Normalized();
                    Vector2 side = new Vector2(-to.Y, to.X).Normalized() * _riderSide * RIDER_ZIG_SIDE;
                    dir = (radial + side).Normalized();
                }
                else if (kind == "ogre")
                {
                    // OGRE walks on like the rest until the slam lane commits it: mid
                    // windup it STANDS STILL so the marked circle reads as a readable
                    // telegraph the player can step out of (assistant read of the user's 1 s beat)
                    if (_slamT > 0.0f)
                        dir = new Vector2();
                    else
                        dir = to.Normalized();
                }
                else if (kind == "mage")
                {
                    // GOBLIN MAGE (user 2026-11-04: "move close enough to cast their attack, then
                    // try to slowly run away"; user 2026-11-06 playtest: "they move close, stand
                    // there and move away" - the old boundary flip shuttled at the ring: full-speed
                    // CLOSE outside it, half-speed RETREAT inside, and the separation nudge kept
                    // re-crossing it, so the body jittered and never settled on a throw stance).
                    // New lane holds a STAND band at the cast ring: close until inside MAGE_CAST,
                    // slow back-off only when crowded well inside it, otherwise plant and hurl.
                    if (dist > MAGE_CAST)
                        dir = to.Normalized();  // close first
                    else if (dist < MAGE_CAST - 70.0f)
                    {
                        dir = -to.Normalized(); // back off ONLY when well inside the ring
                        dir *= MAGE_LOITER;     // the retreat leg stays a slow shamble
                    }
                    else
                    {
                        // user 2026-11-06 playtest (mage pattern STILL wrong): the frozen
                        // stance read as a still target loosing one lucky ball - the mage
                        // now CIRCLES at the cast ring at a shamble (mini-dance discipline,
                        // MAGE_LOITER fraction), hurling on cadence while it walks, so the
                        // pattern keeps constant pressure instead of one stand-still volley.
                        dir = new Vector2(-to.Y, to.X).Normalized() * MAGE_LOITER;
                    }
                }
                else if (kind == "fastgoblin" || kind == "wolf2")
                {
                    // SHARED COIL-AND-SPRING lane (user 2026-11-09: wolf2 is "similar to that
                    // goblin jumper ... but it will charge from a long distance"): same machine,
                    // per-kind knobs below, and NO hop for the mount.
                    bool springer = kind == "fastgoblin"; // only the goblin springs with a visible hop
                    float coilRing = springer ? FG_TRIGGER : W2_TRIGGER;
                    float coilBeat = springer ? FG_PAUSE : W2_PAUSE;
                    float springDist = springer ? FG_LUNGE_DIST : W2_DASH_DIST;
                    float springSpd = springer ? FG_LUNGE_SPD : W2_DASH_SPD;
                    float springCd = springer ? FG_LUNGE_CD : W2_DASH_CD;
                    // FAST GOBLIN lane (USER behaviour: "rapidly approach the player, pause,
                    // then lunge in a straight line over a set distance"): rush with a sin
                    // prowl wobble inside FG_TRIGGER it COILS (the pause beat, assistant 0.6 s),
                    // locking the straight-line bearing at that moment (USER "straight line");
                    // then it SPRINGS FG_LUNGE_DIST px along the locked line at FG_LUNGE_SPD
                    // (assistant picks), with a JUMP arc on the visual rig only ("maybe you can
                    // work out a jump animation"), hitbox stays on the floor. Landing re-arms
                    // the rush with a FG_LUNGE_CD breath before the next coil.
                    if (_fgState == 0)
                    {
                        float fwob = (float)System.Math.Sin(p.X * 0.04 + p.Y * 0.06) * 18.0f; // prowl wobble (assistant pick)
                        dir = (to + new Vector2(fwob * 0.5f, fwob)).Normalized();
                        _fgCd -= delta; // the breath clock only drains while running
                        if (dist <= coilRing && _fgCd <= 0.0f)
                        {
                            _fgState = 1;
                            _fgT = coilBeat;
                            _fgDir = to.Normalized(); // LOCKED at the coil: the lunge never re-aims
                        }
                    }
                    else if (_fgState == 1)
                    {
                        dir = new Vector2(); // the pause beat: feet planted, spring loading
                        _fgT -= delta;
                        if (_fgT <= 0.0f)
                        {
                            _fgState = 2;
                            _fgDone = 0.0f;
                            if (!_fgLeaped)
                            {
                                _fgLeaped = true;
                                GD.Print("[10thAge] (" + kind + ") springs its first straight-line lunge"); // bench audit
                            }
                        }
                    }
                    else
                    {
                        dir = _fgDir; // the locked bearing, straight over the set distance
                        moveSpd = springSpd;
                        _fgDone += springSpd * delta;
                        if (_rig != null && springer) // JUMP animation: the goblin HOPS; the wolf2 mount stays on the floor
                            _rig.Position = new Vector2(0.0f, -(float)System.Math.Sin(System.Math.PI * (_fgDone / springDist)) * FG_HOP);
                        if (_fgDone >= springDist)
                        {
                            _fgState = 0;
                            _fgCd = springCd; // breath between springs
                        }
                    }
                }
                else
                {
                    float wobble = (kind == "goblin" || kind == "goblin2") ? (float)System.Math.Sin(p.X * 0.05 + p.Y * 0.03) * 26.0f : 0.0f; // Goblin 2 prowls the clump too
                    dir = (to + new Vector2(wobble * 0.4f, wobble)).Normalized();
                }
                if (dir.LengthSquared() > 0.0001f)
                    GlobalPosition += dir * moveSpd * delta; // draging maul: a dragged body shambles
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
                        _shotsLoosed++; // 2026-11-06 repeat-fire audit: count this body's volleys
                        if (_shotsLoosed % 3 == 1 && (kind == "mage" || kind == "riderbow"))
                            // prints volley 1/4/7/... per body: a body that only ever fires once
                            // prints ONCE; a body running its cadence prints every 3rd ball
                            GD.Print("[10thAge] (shot+) " + kind + " loosed volley " + _shotsLoosed);
                        Vector2 aim = g.WrapDelta(GlobalPosition, m.GlobalPosition).Normalized();
                        if (kind == "riderbow")
                            g.SpawnBulletArrow(GlobalPosition, aim * RIDER_ARROW_SPEED, rangedDamage, true); // user 2026-11-06: rider arrows ride the RED-OUTLINE variant so the volley reads as an enemy projectile at a glance
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
            // TIER-4 OGRE slam lane (user 2026-11-05): inside the cast ring it MARKS the
            // area on itself - the circle LOCKS where it marks (assistant read: the beat
            // is a dodge window), stands through the 1 s windup (USER), then lands the
            // 25 damage (USER) through Game.SlamArea, which rides the TakeHit defence lane.
            if (kind == "ogre")
            {
                _slamCd -= delta;
                if (_slamT > 0.0f)
                {
                    _slamT -= delta;
                    if (_slamDisc != null)
                    {
                        // the disc tracks the LOCKED point fold-relative to THIS body, so
                        // even a separation nudge across a seam keeps the telegraph in frame
                        _slamDisc.Position = g.WrapDelta(p, _slamPos);
                        float frac = 1.0f - _slamT / OGRE_SLAM_TELE; // fill deepens across the windup
                        _slamDisc.Set("modulate", new Color(0.85f, 0.15f, 0.12f, 0.12f + 0.30f * frac));
                    }
                    if (_slamT <= 0.0f)
                    {
                        int blasted = g.SlamArea(_slamPos, OGRE_SLAM_R, OGRE_SLAM_DMG);
                        GD.Print("[10thAge] (slam) ogre slam lands: " + blasted + " bodies in the blast");
                        _slamCd = OGRE_SLAM_CD;
                        if (_slamDisc != null)
                            _slamDisc.Visible = false;
                    }
                }
                else if (_slamCd <= 0.0f && m != null && g.WrapDelta(p, m.GlobalPosition).Length() <= OGRE_CAST)
                {
                    // lock on THIS body's wrap fold: the slam point rides the same fold as
                    // the ogre, so a seam can never park the telegraph off-frame
                    _slamT = OGRE_SLAM_TELE;
                    _slamPos = p + g.WrapDelta(p, m.GlobalPosition);
                    if (_slamDisc != null)
                    {
                        _slamDisc.Visible = true;
                        _slamDisc.Position = g.WrapDelta(p, _slamPos);
                    }
                    GD.Print("[10thAge] (slam) ogre winds up: circle marks the slam area");
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
            if (_spr != null)
                _spr.Modulate = new Color(2.2f, 1.4f, 1.4f); // painted art flashes too
            else
                _body.Modulate = new Color(2.2f, 1.4f, 1.4f);
        }
        else if (_spr != null)
            _spr.Modulate = new Color(1.0f, 1.0f, 1.0f);
        else
            _body.Modulate = new Color(1.0f, 1.0f, 1.0f);
    }
}

