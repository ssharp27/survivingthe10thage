using Godot;

/// <summary>
/// One party member: HP, auto-attack driven by signature-weapon parameters, and the
/// down/death-save/revive state fields (the state machine itself lives in Game).
/// Baldrun = wide ~150 deg arc cleave (damage split among everything in the fan);
/// Mosiah = narrow fast rapier (single target + pierce, hits one body at a time).
/// </summary>
public partial class PartyMember : Godot.Node2D
{
    public string displayName = "Member";
    public string weaponName = "Maul";
    public int memberIndex = 0;
    public bool isLeader = false;

    public float maxHp = 60.0f;
    public float baseSpeed = 185.0f;

    public float weaponDamage = 10.0f;
    public float cooldown = 1.15f;
    public float range = 95.0f;
    public float arcHalf = 1.05f; // half-arc radians: wide = maul, narrow = rapier

    public float hp = 0.0f;
    public bool downed = false;
    public bool eliminated = false;
    float cdTimer = 0.0f;
    Godot.Node2D _telePivot; // telegraph armature (pure nodes: C# _draw is dead in 4.7)
    Godot.ColorRect[] _teleParts;
    float _teleHold = 0.0f; // telegraph visibility timer, set on each swing
    public float facing = 0.0f;
    public float dmgMul = 1.0f;
    public float cdMul = 1.0f;
    public float rangeMul = 1.0f;
    public float speedMul = 1.0f;
    // "wider arc" rank track REMOVED per human test: Baldrun's upgrade pool went to
    // reach / damage / cooldown instead, so the maul fan stays a fixed ~150 deg arc.
    public int pierce = 0; // "deeper thrust": the rapier lane can skewer extra bodies
    public string class_ = ""; // display class for the ui/ cards (empty = weapon fallback)
    public string pickHistory = ""; // tracks taken so far, rendered on the run-summary card

    // down/death-save state (driven by the Game root)
    public float dsTimer = 0.0f;
    public int dsFails = 0;
    public int dsSucceeds = 0; // rolls of 11-19; 3 of them = stable
    public bool stable = false; // 3 successes: rolls stop, hits now slay
    public float reviveProgress = 0.0f; // leader stand-still channel
    public bool reviveShield = false; // body is mid-rescue: damage is ignored
    public float invuln = 0.0f; // post-revive immunity timer (2 s)
    public float dsDamageBuffer = 0.0f; // 2 s gap between damage-caused death-save fails
    public float dsHold = 0.0f; // countdown for SetDsTemp texts; 0 = persistent
    public bool campReturned = false; // lifted by the camp beat (pass 5: the LONG rest now tops these to full)
    // field-recruit rule (user 2026-10-20): the Leader starts in the squad; every other
    // roster PC waits for its join beat. Unjoined = no follow, no attacks, un-hittable,
    // outside the HP pool and the cards - Game flips this when the escort cage clears.
    public bool joined = false;

    // on-body death-save readout (DESIGN:12 - the die lives ON the downed body, the
    // save is not log-only): Game writes dsColor/dsLastRoll, the body shows them
    public Color dsColor = new Color(1.0f, 1.0f, 1.0f); // chip mood: blood/amber/sage/grey
    public int dsLastRoll = 0;                         // the die face the chip shows
    public float reviveChannelTotal = 3.0f;            // mirrors Game.REVIVE_CHANNEL (bar width)
    Godot.ColorRect _chipRing = null;                  // state ring behind the die face
    Godot.ColorRect _chipFace = null;                  // grimoire-paper die face
    Godot.Label _chipNum = null;                       // the roll itself
    Godot.ColorRect _chanBg = null;                    // STAND STILL channel bar (player revive)
    Godot.ColorRect _chanFill = null;

    Godot.ColorRect _body;
    Godot.Label _ds;
    float _flash = 0.0f;
    // mini motion rig (user 2026-10-21): the body/art hangs on this pivot so the mini
    // can shuffle without touching the node's logical position, hitbox, chips, labels
    // or telegraph - and future art sprites reparent here unchanged
    Godot.Node2D _rig;
    float _rigT = 0.0f;
    float _toppleDir = 1.0f;
    // user 2026-10-21: a mini only shuffles while a finger is PUSHING it, so the rig
    // measures the node's actual travel per frame - stopped party settles its last
    // step instead of freezing mid-sway
    Vector2 _lastMovePos = new Vector2();
    float _moveSpeed = 0.0f;

    public override void _EnterTree()
    {
        AddToGroup("party");
        ZIndex = 5; // members (and their _draw telegraphs) paint above enemy squares
        hp = maxHp;
        _body = new ColorRect();
        _body.Size = new Vector2(30.0f, 30.0f);
        _body.Position = -_body.Size * 0.5f;
        _body.Color = memberIndex == 0 ? new Color(0.92f, 0.92f, 0.95f)
                                       : new Color(0.78f, 0.63f, 0.44f);
        _toppleDir = memberIndex % 2 == 0 ? 1.0f : -1.0f; // toppled minis fall opposite ways
        _rig = new Godot.Node2D();
        _rig.Name = "Rig";
        AddChild(_rig);
        _rig.AddChild(_body); // under the rig, NOT the root: see motion block below
        _ds = new Label();
        _ds.Position = new Vector2(-24.0f, -58.0f); // clears the 64x8 dash bar at y-42
        AddChild(_ds);
        // d20 death-save chip (DESIGN:12): paper face + ink number + state ring, parked
        // to the body's right so it never fights the ds label (y-58), the dash bar
        // (y-42) or the channel bar (y-30). Pure ColorRects/Labels - the C# _draw hook
        // is dead in 4.7.
        _chipRing = new Godot.ColorRect();
        _chipRing.Size = new Vector2(40.0f, 40.0f);
        _chipRing.Position = new Vector2(12.0f, -20.0f);
        _chipRing.Color = new Color(0.70f, 0.70f, 0.70f, 1.0f);
        _chipRing.ZIndex = 6;
        AddChild(_chipRing);
        _chipFace = new Godot.ColorRect();
        _chipFace.Size = new Vector2(34.0f, 34.0f);
        _chipFace.Position = new Vector2(15.0f, -17.0f);
        _chipFace.Color = new Color(0.957f, 0.929f, 0.847f, 1.0f); // grimoire paper
        _chipFace.ZIndex = 7;
        AddChild(_chipFace);
        _chipNum = new Label();
        _chipNum.Position = new Vector2(23.0f, -14.0f); // ink sits INSIDE the face
        // the invisible-chip bug: a Label's default theme color is near-white on a
        // paper-white face - the roll never showed. Dark grimoire ink instead.
        _chipNum.Modulate = new Color(0.10f, 0.09f, 0.08f, 1.0f);
        _chipNum.ZIndex = 8;
        AddChild(_chipNum);
        _chanBg = new Godot.ColorRect();
        _chanBg.Size = new Vector2(48.0f, 6.0f);
        _chanBg.Position = new Vector2(-24.0f, -30.0f);
        _chanBg.Color = new Color(0.12f, 0.11f, 0.10f, 0.9f);
        _chanBg.ZIndex = 7;
        AddChild(_chanBg);
        _chanFill = new Godot.ColorRect();
        _chanFill.Size = new Vector2(0.0f, 6.0f);
        _chanFill.Position = new Vector2(-24.0f, -30.0f);
        _chanFill.Color = new Color(0.83f, 0.64f, 0.24f, 1.0f); // gold = rescue channel running
        _chanFill.ZIndex = 8;
        AddChild(_chanFill);
        // telegraph: a pivot + thin ColorRect "spokes" radiating along facing. The
        // maul is a ~150 deg ARC CLEAVE (human-test locked): spokes show the fan
        // edges + middle; the rapier stays one solid stab line. Pure nodes because
        // both the C# _draw hook and drawing outside _draw are dead ends in 4.7.
        _telePivot = new Godot.Node2D();
        _telePivot.Name = "Telegraph";
        AddChild(_telePivot);
        bool maul = arcHalf > 0.5f; // maul kit: arc cleave, split damage
        bool circle = arcHalf > 3.0f; // only a deliberate 360 deg kit gets a dial
        int spokes = circle ? 12 : (maul ? 3 : 1);
        float w = circle ? 4.0f : (maul ? 5.0f : 7.0f);
        Color col = circle || maul ? new Color(1.0f, 0.55f, 0.2f, 0.45f)
                                   : new Color(0.5f, 1.0f, 1.0f, 0.55f);
        _teleParts = new Godot.ColorRect[spokes];
        for (int i = 0; i < spokes; i++)
        {
            Godot.ColorRect s = new Godot.ColorRect();
            s.Size = new Vector2(range, w);
            s.Position = new Vector2(0.0f, -w * 0.5f);
            s.Rotation = circle ? (float)(System.Math.PI * 2.0 * i / 12.0)
                                : (spokes == 1 ? 0.0f : (float)(-arcHalf + 2.0 * arcHalf * i / (spokes - 1)));
            s.Color = col;
            _telePivot.AddChild(s);
            _teleParts[i] = s;
        }
    }

    public void SetDsText(string s)
    {
        if (_ds != null)
            _ds.Text = s;
    }

    public void SetDsTemp(string s, double hold)
    {
        SetDsText(s);
        dsHold = (float)hold;
    }

    public override void _Process(double d)
    {
        Game pg0 = GetParent() as Game;
        if (pg0 != null && pg0.GetWorldPause())
            return; // picker / join-banner soft pause: nobody swings while a card is up
        float delta = (float)d;
        // looping world: fold every body but the Leader into the wrap box around the
        // camera (the Leader IS the anchor; folding it would pop the camera across a
        // seam). This is what keeps downed/eliminated bodies and the stranded recruit
        // reachable forever - walk any direction and the world loops back to them.
        // Live ring members are welded near the Leader, so the fold is a no-op for them.
        if (pg0 != null && !isLeader)
            GlobalPosition = pg0.WrapNearCamera(GlobalPosition);
        if (dsDamageBuffer > 0.0f)
            dsDamageBuffer -= delta; // ticks even while downed: the 2 s fail-buffer
        if (invuln > 0.0f)
            invuln -= delta; // post-revive immunity window, ticks always
        // eliminated = out until the next campfire: the body, telegraph and ds label
        // go hidden; writing visibility every frame is what un-hides a member when
        // the campfire lifts them back up
        bool hidden = eliminated;
        // user bug 2026-10-20: Mosiah popped into the world long before his beat. A
        // not-yet-joined PC stays hidden until the recruit event makes it the pending
        // one (the HELP! arrow target); camp/escort joins lift the gate because joined
        // becomes true
        PartyMember pend = pg0 != null ? pg0.GetRecruitPending() : null;
        if (!joined && !isLeader && (pend == null || pend.GetInstanceId() != GetInstanceId()))
            hidden = true;
        if (_body != null)
            _body.Visible = !hidden;
        if (_telePivot != null)
            _telePivot.Visible = !hidden;
        if (_ds != null)
            _ds.Visible = !hidden;
        // ---- downed-body readout: the d20 chip + the STAND STILL channel bar ----
        bool downChip = downed && !eliminated;
        float chanTotal = reviveChannelTotal > 0.0f ? reviveChannelTotal : 3.0f;
        bool channeling = downChip && reviveProgress > 0.0f;
        if (_chipRing != null)
        {
            _chipRing.Visible = downChip;
            if (downChip)
                _chipRing.Color = stable ? new Color(0.95f, 0.75f, 0.35f, 1.0f)
                                         : (dsFails > 0 ? new Color(0.88f, 0.42f, 0.42f, 1.0f)
                                                        : new Color(0.70f, 0.70f, 0.70f, 1.0f));
        }
        if (_chipFace != null)
            _chipFace.Visible = downChip;
        if (_chipNum != null)
        {
            _chipNum.Visible = downChip;
            _chipNum.Text = dsLastRoll > 0 ? "" + dsLastRoll : "d20";
        }
        if (_chanBg != null)
            _chanBg.Visible = channeling;
        if (_chanFill != null)
        {
            _chanFill.Visible = channeling;
            float frac = channeling ? reviveProgress / chanTotal : 0.0f;
            if (frac > 1.0f)
                frac = 1.0f;
            _chanFill.Size = new Vector2(48.0f * frac, 6.0f);
        }
        if (channeling && _ds != null)
        {
            // what the player is DOING owns the chip, so the countdown beats a stale
            // death-save line (the ring + gold bar show the same channel visually)
            float rem = chanTotal - reviveProgress;
            int left = (int)rem; // ceil without Math.ceil (4.7 Math lacks it)
            if ((float)left < rem)
                left++;
            _ds.Text = "STAND STILL " + left + "s";
            _ds.Modulate = new Color(0.95f, 0.75f, 0.35f, 1.0f);
        }
        else if (_ds != null)
            _ds.Modulate = dsColor;
        // ---- tabletop mini motion (user 2026-10-21 "the party ARE minis"): a live,
        // joined mini shuffles side-to-side with a lean and a set-down tap, exactly
        // like fingers nudging a miniature; members run on OFFSET phases so the squad
        // reads as individually nudged, not a marching band. DOWNED or eliminated =
        // toppled mini (rim down, still). Not-yet-joined = stands upright waiting to
        // be picked up. Front-facing art makes this whole language flip-free.
        if (_rig != null)
        {
            bool liveMini = joined && !downed && !eliminated;
            if (liveMini)
            {
                // how far did this mini actually travel this frame? (post-fold coords,
                // so wrap seams read as 0 not a teleport)
                Vector2 gp = GlobalPosition;
                float moved = gp.DistanceTo(_lastMovePos);
                _lastMovePos = gp;
                if (moved > 400.0f)
                    moved = 0.0f; // wrap-fold frame, not motion
                float inst = delta > 0.001f ? moved / delta : _moveSpeed;
                _moveSpeed += (inst - _moveSpeed) * (1.0f - (float)System.Math.Exp(-8.0 * delta));
                if (_moveSpeed > 30.0f) // being pushed across the table
                {
                    float ratio = _moveSpeed / 130.0f; // ~full party push speed
                    ratio = ratio < 0.35f ? 0.35f : (ratio > 1.4f ? 1.4f : ratio);
                    _rigT += delta * 7.2f * ratio; // user 2026-10-21: a touch faster than pass-14, scaled by push
                    float ph = _rigT + memberIndex * 0.9f;
                    float sway = (float)System.Math.Sin(ph) * 3.0f;
                    _rig.Position = new Vector2(sway, -(float)System.Math.Abs((float)System.Math.Sin(ph * 0.5)) * 1.2f);
                    _rig.Rotation = sway * 0.017f; // the lean trails the push
                }
                else
                {
                    // player stopped: glide the last sway back level - a set-down, not a jolt
                    _rig.Position += -_rig.Position * (1.0f - (float)System.Math.Exp(-9.0 * delta));
                    _rig.Rotation += -_rig.Rotation * (1.0f - (float)System.Math.Exp(-9.0 * delta));
                }
            }
            else
            {
                float top = downed || eliminated ? _toppleDir * (float)(System.Math.PI * 0.5) : 0.0f;
                _rig.Position += -_rig.Position * (1.0f - (float)System.Math.Exp(-9.0 * delta));
                _rig.Rotation += (top - _rig.Rotation) * (1.0f - (float)System.Math.Exp(-10.0 * delta));
            }
        }
        if (!joined || downed || eliminated)
            return; // a stranded recruit cannot swing: its weapon unlocks ON the join
        if (_flash > 0.0f)
        {
            _flash -= delta;
            // red tint, not a white multiply: Baldrun's near-white square barely
            // changed under a 3x white modulate
            _body.Modulate = new Color(1.0f, 0.35f, 0.35f);
        }
        else
            _body.Modulate = new Color(1.0f, 1.0f, 1.0f);
        cdTimer -= delta;
        if (dsHold > 0.0f)
        {
            dsHold -= delta;
            if (dsHold <= 0.0f && !downed && !eliminated)
                _ds.Text = "";
        }
        if (_teleHold > 0.0f)
        {
            _teleHold -= delta;
            float a = _teleHold <= 0.0f ? 0.0f : _teleHold / 0.18f;
            foreach (Godot.ColorRect s in _teleParts)
                s.Modulate = new Color(a, a, a, a);
        }
        if (invuln > 0.0f)
        {
            _body.Modulate = new Color(0.8f, 1.0f, 0.85f); // pale green = post-revive shield
            if (_ds != null)
            {
                // locked rule made visible: the player can see WHY a hit just bounced
                _ds.Text = "immune " + ((int)(invuln * 10.0f)) / 10.0f + "s";
                _ds.Modulate = new Color(0.60f, 0.84f, 0.60f, 1.0f); // sage = immunity
            }
        }
        if (cdTimer > 0.0f)
            return;
        Vector2 p = GlobalPosition;
        Game pg = GetParent() as Game;
        if (pg == null)
            return;
        Godot.Collections.Array<Godot.Node2D> enemies = pg.GetEnemies();
        // the live arc: ranks open the maul FAN, the rapier keeps its narrow lane
        float arcEff = (float)System.Math.Min((double)arcHalf, System.Math.PI);
        bool cleaver = arcHalf > 0.5f; // maul kit: arc cleave with split damage
        float reach = range * rangeMul;
        Godot.Node2D best = null;
        float bestD = 1e9f;
        for (int i = 0; i < enemies.Count; i++)
        {
            Godot.Node2D n = enemies[i];
            Combatant nc = (Combatant)n;
            if (nc == null || nc.kind == "dead")
                continue;
            Vector2 v = n.GlobalPosition - p;
            float dd = v.Length();
            if (dd >= bestD)
                continue;
            // Targeting gate is LANES-only (rapier): a lane weapon may only attack
            // what its facing arc covers (touching enemies always count). The maul
            // Mosiah-aims: it attacks the ABSOLUTE nearest body whatever way the
            // body faces, and the ~150 deg arc rides the swing (below), not the
            // walk direction - so a Baldrun walking away from the horde still
            // turns its cleave to the nearest threat instead of whiffing air.
            if (isLeader && !cleaver && dd >= 40.0f &&
                System.Math.Abs(angleDiff((float)System.Math.Atan2(v.Y, v.X), facing)) > arcEff)
                continue;
            bestD = dd;
            best = n;
        }
        if (best == null || bestD > reach)
            return;
        cdTimer = cooldown * cdMul;
        Vector2 to = best.GlobalPosition - p;
        float ang = (float)System.Math.Atan2(to.Y, to.X);
        if (!isLeader || cleaver)
            facing = ang; // auto-aim: the cleave swings at its prey (Mosiah-style),
                          // so the ~150 deg fan is centered on the swing toward the
                          // closest body; only lane weapons respect walk facing
        float dmg = weaponDamage * dmgMul;
        if (cleaver)
        {
            // Maul: ARC CLEAVE with SPLIT damage (human-test locked tradeoff): the
            // fan is centered on the swing toward the closest body (auto-aim above);
            // everything inside it is hit, but the swing's force divides among them -
            // a lone target eats the full blow, a packed horde only gets chipped
            int hits = 0;
            for (int i = 0; i < enemies.Count; i++)
            {
                Godot.Node2D n = enemies[i];
                Combatant nc = (Combatant)n;
                if (nc == null || nc.kind == "dead")
                    continue;
                Vector2 e = n.GlobalPosition - p;
                if (e.Length() <= reach &&
                    System.Math.Abs(angleDiff((float)System.Math.Atan2(e.Y, e.X), facing)) <= arcEff)
                    hits++;
            }
            float each = dmg / (float)(hits > 0 ? hits : 1);
            for (int i = 0; i < enemies.Count; i++)
            {
                Godot.Node2D n = enemies[i];
                Combatant nc = (Combatant)n;
                if (nc == null || nc.kind == "dead")
                    continue;
                Vector2 e = n.GlobalPosition - p;
                if (e.Length() <= reach &&
                    System.Math.Abs(angleDiff((float)System.Math.Atan2(e.Y, e.X), facing)) <= arcEff)
                    nc.TakeDamage(each);
            }
        }
        else
        {
            // Rapier: thrust + pierce. The point lands on the pick; ranks let up to
            // 1+pierce bodies share the lane and get skewered by the same lunge.
            Godot.Node2D[] lane = new Godot.Node2D[1 + (pierce > 3 ? 3 : pierce)];
            lane[0] = best;
            float laneHalf = (float)System.Math.Max((double)arcEff, 0.16);
            for (int s = 1; s < lane.Length; s++)
            {
                Godot.Node2D n = null;
                float nd = 1e9f;
                for (int i = 0; i < enemies.Count; i++)
                {
                    Godot.Node2D c = enemies[i];
                    Combatant nc = (Combatant)c;
                    if (nc == null || nc.kind == "dead")
                        continue;
                    bool already = false;
                    for (int q = 0; q < lane.Length; q++)
                    {
                        if (lane[q] != null && lane[q].GetInstanceId() == c.GetInstanceId())
                        {
                            already = true;
                            break;
                        }
                    }
                    if (already)
                        continue;
                    Vector2 e = c.GlobalPosition - p;
                    float dd = e.Length();
                    if (dd <= range * rangeMul && dd < nd &&
                        System.Math.Abs(angleDiff((float)System.Math.Atan2(e.Y, e.X), facing)) <= laneHalf)
                    {
                        nd = dd;
                        n = c;
                    }
                }
                if (n == null)
                    break;
                lane[s] = n;
            }
            foreach (Godot.Node2D hit in lane)
            {
                // pierce widens the lane but the rays past the last body stay empty
                if (hit != null)
                    ((Combatant)hit).TakeDamage(dmg);
            }
        }
        _flash = 0.12f;
        _telePivot.Rotation = facing;
        _telePivot.Scale = new Vector2(rangeMul, rangeMul);
        // rebuild the spokes for the LIVE arc: ranks open the maul fan, and the
        // rotations baked at load time only knew the base arcHalf
        if (_teleParts != null && _teleParts.Length > 1 && arcHalf <= 3.0f)
        {
            int nsp = _teleParts.Length;
            for (int i = 0; i < nsp; i++)
                _teleParts[i].Rotation = (float)(-arcEff + 2.0 * arcEff * i / (nsp - 1));
        }
        _teleHold = 0.18f;
    }

    private static double angleDiff(double a, double b)
    {
        double t = a - b;
        while (t > System.Math.PI)
            t -= System.Math.PI * 2.0;
        while (t < -System.Math.PI)
            t += System.Math.PI * 2.0;
        return t;
    }
}
