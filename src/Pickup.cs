using Godot;

/// <summary>
/// FIELD PICKUPS (user 2026-11-08 idea: "small random pickups, such as a healing orb,
/// an XP magnet, or short damage buffs to help runs"). Rare kill drops - the roll and
/// the drop rail live in Game.cs (PICKUP_CH), the numbers are assistant-picked knobs
/// below (the user dictated the CONCEPT, not the numbers, and picked "pull dropped
/// gems" for the magnet). Three flavours: HEAL ORB (flat fixed heal on the member
/// who walks over it - fixed not max-HP-fraction so early runs cannot chain a bar),
/// XP MAGNET (the UpdateGems collection bubble becomes WHOLE-FIELD for MAGNET_T
/// seconds: dropped gems ride the proven 380 px/s vacuum and pay their own XP_PCT
/// math at pickup - the magnet is Stack's puller lane made temporary), RAGE VIAL
/// (battle frenzy riding the Guiding-Light VULNERABLE-mark rail: live enemies take
/// +RAGE_MUL incoming for RAGE_T seconds, max-mark refresh-never-stack, counted at
/// the hit like the crossfire law - no new damage math anywhere).
/// IN-RUN only: the compendium meta law (docs/compendium-seed.md) keeps pickups out
/// of the additive meta pool. Nodes are children of Game and run their own _Process,
/// same discipline as Enemy.cs.
/// </summary>
public partial class Pickup : Godot.Node2D
{
    public int pk = 0; // 0 heal | 1 magnet | 2 rage
    private double _life = 0.0;
    private float _bobT = 0.0f;
    Godot.ColorRect _orb;
    // 2026-11-08 art pass (user-approved sprites): healing potion / arcane orb / rage flame per flavour;
    // the ColorRect shell stays as the null-safe fallback, same discipline as Enemy.cs
    static Godot.Texture2D _artHeal = null;
    static Godot.Texture2D _artOrb = null;
    static Godot.Texture2D _artRage = null;
    static bool _artTried = false;
    static readonly float PICKUP_ART_S = 0.42f; // 40 px art -> ~16 px: a touch over the 12 px shell (assistant pick)
    // assistant-picked knobs (hand-test retunes)
    static readonly float HEAL_AMT = 15.0f; // flat hp on the picker (assistant pick)
    static readonly float MAGNET_T = 6.0f; // whole-field gem vacuum window (assistant pick)
    static readonly double RAGE_MUL = 0.2; // +20% incoming on LIVE enemies (assistant pick)
    static readonly double RAGE_T = 8.0; // frenzy window (assistant pick)
    static readonly double LIFE_D = 18.0; // field lifetime: grabbing it is a choice, not free (assistant pick)
    static readonly float PICK_R = 18.0f; // walk-over collection radius (assistant pick)

    public void InitVisuals()
    {
        _orb = new Godot.ColorRect();
        _orb.Size = new Vector2(12.0f, 12.0f);
        _orb.Position = -_orb.Size * 0.5f;
        _orb.Color = pk == 0 ? new Color(0.25f, 0.85f, 0.35f) // heal orb green
                       : pk == 1 ? new Color(0.95f, 0.75f, 0.25f) // magnet gold
                       : new Color(0.90f, 0.20f, 0.20f); // rage vial red
        if (!_artTried)
        {
            _artTried = true;
            _artHeal = GD.Load("res://art/pickups/potion.png") as Godot.Texture2D; // red flask (user swapped the heart concept)
            _artOrb = GD.Load("res://art/pickups/orb.png") as Godot.Texture2D;
            _artRage = GD.Load("res://art/pickups/rage_flame.png") as Godot.Texture2D;
        }
        Godot.Texture2D atx = pk == 0 ? _artHeal : pk == 1 ? _artOrb : _artRage;
        if (atx != null)
        {
            Godot.Sprite2D asp = new Godot.Sprite2D();
            asp.Set("texture_filter", 0); // NEAREST like the other art lanes
            asp.Texture = atx;
            asp.Scale = new Vector2(PICKUP_ART_S, PICKUP_ART_S);
            asp.Position = new Vector2(6.0f - (float)atx.GetWidth() * PICKUP_ART_S * 0.5f, 6.0f - (float)atx.GetHeight() * PICKUP_ART_S * 0.5f);
            _orb.AddChild(asp);
            _orb.Color = new Color(0.0f, 0.0f, 0.0f, 0.0f); // art rides: the flavour shell stops drawing
        }
        AddChild(_orb);
        _life = LIFE_D;
    }

    public override void _Process(double d)
    {
        float delta = (float)d;
        Game g = GetParent() as Game;
        if (g == null)
            return;
        if (_orb == null)
            InitVisuals(); // lazy like Enemy.cs: the node self-inits on its first frame
        GlobalPosition = g.WrapNearCamera(GlobalPosition); // fold like the gems do
        if (g.GetWorldPause())
            return; // picker / join-banner pause: the field clock holds still, same as the horde
        _bobT += delta;
        if (_orb != null)
            _orb.Position = new Vector2(-6.0f, -6.0f - 4.0f * (float)System.Math.Sin((double)_bobT * 5.0f)); // 4 px hover (assistant pick)
        _life -= delta;
        if (_life <= 0.0)
        {
            QueueFree(); // nobody grabbed it in time: it dims out
            return;
        }
        PartyMember m = g.NearestMember(GlobalPosition);
        if (m == null || !m.joined || m.downed || m.eliminated)
            return; // no bubble from fallen / stranded bodies, same census as the gem rail
        if (g.WrapDelta(GlobalPosition, m.GlobalPosition).Length() > PICK_R)
            return;
        // walk-over collect: the closest standing member grabs it
        if (pk == 0)
        {
            m.hp += HEAL_AMT;
            if (m.hp > m.EffMaxHp())
                m.hp = (float)m.EffMaxHp(); // clamp at own cap like every other heal lane
            GD.Print("[10thAge] (pickup) heal orb: +15 hp");
        }
        else if (pk == 1)
        {
            g._magnetT = MAGNET_T; // UpdateGems reads this clock: bubble becomes whole-field
            GD.Print("[10thAge] (pickup) xp magnet: whole-field gem vacuum for 6 s");
        }
        else
        {
            int marked = 0;
            foreach (Godot.Node2D n in g.GetEnemies())
            {
                Combatant nc = (Combatant)n;
                if (nc == null || nc.kind == "dead" || nc.hp <= 0.0)
                    continue; // the SAME live census CinderBurst uses: dead registry entries stay inert
                nc.Vulnerable(RAGE_MUL, RAGE_T); // the guiding-light mark rail: max-mark, refresh-never-stack
                marked++;
            }
            GD.Print("[10thAge] (pickup) rage vial: " + marked + " live enemies +20% incoming for 8 s");
        }
        QueueFree();
    }
}
