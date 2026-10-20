using Godot;

/// <summary>
/// Data-driven placeholder boss for the M1 timeline engine: three phases (100/66/33% HP),
/// every event is telegraph -> attack -> recovery, composed of ring/fan/spokes primitives.
/// Swap the pattern blocks for real encounter data later; the engine stays.
/// kind is inherited from Combatant; the scene author sets "mini"/"final".
/// </summary>
public partial class Boss : Combatant
{
    public bool active = false;
    public float radius = 46.0f;
    public float driftSpeed = 42.0f;
    public float bulletDamage = 10.0f;

    float _eventTimer = 3.0f;
    bool _telegraph = false;
    float _ringOffset = 0.0f;
    int _phase = 0;
    float _contactCd = 0.0f;
    Godot.ColorRect _body;

    public override void _EnterTree()
    {
        hp = maxHp;
        _body = new ColorRect();
        float w = kind == "mini" ? 60.0f : 96.0f;
        _body.Size = new Vector2(w, w);
        _body.Position = -_body.Size * 0.5f;
        _body.Color = kind == "mini" ? new Color(0.55f, 0.15f, 0.45f)
                                     : new Color(0.65f, 0.10f, 0.10f);
        AddChild(_body);
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
        Game g = GetParent() as Game;
        if (g == null)
            return;
        if (g.GetWorldPause())
            return; // picker / join-banner soft pause: the boss holds its volley mid-phase
        float ratio = hp / maxHp;
        _phase = ratio > 0.66f ? 0 : (ratio > 0.33f ? 1 : 2);

        // body-check the NEAREST live member, not the Leader specifically: the boss
        // must punish whoever stands in its way (downed bodies handled below)
        PartyMember target = g.NearestMember(GlobalPosition);
        _contactCd -= delta;
        if (target != null && !target.downed && !target.eliminated)
        {
            Vector2 to = g.WrapDelta(GlobalPosition, target.GlobalPosition);
            float dist = to.Length();
            // close to melee reach: party signature weapons top out ~130px, so the
            // boss must settle inside that or it can never be struck
            if (dist > 100.0f)
                GlobalPosition = g.WrapNearCamera(GlobalPosition + to.Normalized() * driftSpeed * delta);
            if (dist < radius + g.MEMBER_RADIUS && _contactCd <= 0.0f)
            {
                _contactCd = 0.8f;
                g.TakeHit(target, 12.0);
            }
        }
        // downed bodies still get body-checked by the boss (toroidal: a body across a
        // seam IS within reach, so measure the wrap-short way)
        PartyMember hitAny = g.NearestMemberAny(GlobalPosition);
        if (hitAny != null && hitAny.downed &&
            g.WrapDelta(GlobalPosition, hitAny.GlobalPosition).Length() < radius + g.MEMBER_RADIUS && _contactCd <= 0.0f)
        {
            _contactCd = 0.8f;
            g.TakeHit(hitAny, 12.0);
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
}
