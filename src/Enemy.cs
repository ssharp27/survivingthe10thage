using Godot;

/// <summary>
/// M1 greybox baseline enemies. Two archetypes: goblin (slow swarm clump) and wolf
/// (fast straight chaser). No physics bodies: pooled Node2Ds with circle overlap math;
/// separation is a cheap n-bounded push.
/// </summary>
public partial class Enemy : Combatant
{
    public float speed = 75.0f;
    public float xpValue = 1.0f;
    float _contactCd = 0.0f;

    Godot.ColorRect _body;

    // Visuals are built after spawn parameters land (Director sets kind/hp/speed post-new()),
    // so this runs on demand rather than in _EnterTree.
    public void InitVisuals()
    {
        if (kind == "reaper")
        {
            // TEST death-state reaper: big red circle-ish (diamond reads rounder
            // without a draw path), instant kill on touch, attack-proof
            _body = new ColorRect();
            _body.Size = new Vector2(72.0f, 72.0f);
            _body.Position = -_body.Size * 0.5f;
            _body.Rotation = 0.785f;
            _body.Color = new Color(0.85f, 0.12f, 0.12f);
            AddChild(_body);
            return;
        }
        float w = kind == "wolf" ? 34.0f : 26.0f;
        _body = new ColorRect();
        _body.Size = new Vector2(w, w * 0.8f);
        _body.Position = -_body.Size * 0.5f;
        _body.Color = kind == "wolf" ? new Color(0.55f, 0.58f, 0.62f)
                                     : new Color(0.30f, 0.48f, 0.23f);
        AddChild(_body);
    }

    public override void _Process(double d)
    {
        float delta = (float)d;
        if (_body == null)
            InitVisuals();
        Game g = GetParent() as Game;
        if (g == null)
            return;
        Vector2 p = GlobalPosition;
        if (kind == "reaper")
        {
            // TEST reaper: chase the nearest ally, kill the first body it touches,
            // then despawn. Attack-proof via its huge hp; no separation / contact
            // damage logic needed. kind "dead" + offscreen = clean despawn without
            // freeing (kill scan drops the registry entry, no XP).
            PartyMember prey = g.NearestMember(p);
            if (prey != null)
                GlobalPosition = g.ClampToArena(GlobalPosition + (prey.GlobalPosition - GlobalPosition).Normalized() * speed * delta);
            PartyMember touch = g.NearestMemberAny(GlobalPosition);
            if (touch != null && touch.GlobalPosition.DistanceTo(GlobalPosition) < 36.0f + g.MEMBER_RADIUS)
            {
                g.OnReaperKill(touch);
                kind = "dead";
                GlobalPosition = new Vector2(0.0f, -10000.0f);
            }
            return;
        }
        PartyMember m = g.NearestMember(GlobalPosition);
        _contactCd -= delta;
        if (m != null)
        {
            Vector2 to = m.GlobalPosition - p;
            float dist = to.Length();
            float wobble = kind == "goblin" ? (float)System.Math.Sin(p.X * 0.05 + p.Y * 0.03) * 26.0f : 0.0f;
            Vector2 dir = (to + new Vector2(wobble * 0.4f, wobble)).Normalized();
            GlobalPosition += dir * speed * delta;
            GlobalPosition = g.ClampToArena(GlobalPosition);
            if (dist < 12.0f + g.MEMBER_RADIUS && _contactCd <= 0.0f)
            {
                _contactCd = 0.6f;
                g.TakeHit(m, contactDamage);
            }
        }
        // downed bodies are still run into even though nobody chases them
        PartyMember hitAny = g.NearestMemberAny(p);
        if (hitAny != null && hitAny.downed &&
            hitAny.GlobalPosition.DistanceTo(p) < 12.0f + g.MEMBER_RADIUS && _contactCd <= 0.0f)
        {
            _contactCd = 0.6f;
            g.TakeHit(hitAny, contactDamage);
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
