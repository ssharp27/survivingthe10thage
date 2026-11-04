extends CanvasLayer

# PauseMenu.gd - the PAUSE card (user 2026-11-05: "a pause menu listing every recruited
# character with their current upgrades" + "per-character targeting-logic toggle" +
# "maybe we make the game fade/ blur in the background so it shows that it's paused?").
#
# House style from ui/Campfire.gd + ui/RunSummary.gd: grimoire palette, ALL-UNTYPED
# @export arrays (the Array[Dictionary] trap: Game.cs' Set() lands ONLY on untyped
# exports), scene root NAMED PauseMenu (the signal Connect target), PROCESS_MODE_ALWAYS
# so buttons + ESC stay live while the tree is paused, hidden card owns NO input.
#
# Built IN CODE so ui/PauseMenu.tscn stays a trivial CanvasLayer + script. Layout
# follows the user's mockup: dark columns, mini -> name -> TARGET LOGIC -> UPGRADES,
# over the dimmed, softened arena (pause_blur.gdshader).

class_name PauseMenuScreen

signal target_changed(member_index: int, mode: int)
signal dismissed

# ---------- grimoire palette (same values as Campfire.gd / RunSummary.gd) ----------
var PAPER = Color(0.957, 0.929, 0.847, 1)
var INK = Color(0.21, 0.17, 0.12, 1)
var MUTED = Color(0.52, 0.44, 0.33, 1)
var GOLD = Color(0.83, 0.64, 0.24, 1)
var SAGE = Color(0.44, 0.58, 0.40, 1)
var BLOOD = Color(0.70, 0.26, 0.26, 1)
var AMBER = Color(0.82, 0.58, 0.20, 1)
var GREY = Color(0.62, 0.62, 0.62, 1)

# ---------- data bound by Game.cs (see ui/INTEGRATION.md) ----------
# UNTYPED ON PAIN: a typed Array[Dictionary] makes Game.cs' Set("members", ...) a NO-OP
# (the 2026-10-20 campfire oracle note). Rows: CampfireCards shape + idx / target_mode / art.
@export var members: Array = []
@export var stage_name: String = "Tanurial Forest"
@export var use_blur: bool = true
# pilot valve: >0 = seconds the headless pilot holds the card up before the card
# itself closes it. The valve CANNOT live in Game._process: the pause card is a TRUE
# tree pause, so that rail never ticks again while the card is up (seat 545 froze at
# beat 1 for the full 25-min kill rail). The card is PROCESS_MODE_ALWAYS, so its own
# _process does tick during the freeze: on timeout it runs _close, which emits the
# SAME dismissed signal the ESC rail uses - so C# still owns the unpause.
@export var pilot_timeout: float = 0.0

var MODE_LABEL = ["closest", "furthest", "highest hp", "lowest hp", "boss"]

var blur: ColorRect = null
var wash: ColorRect = null
var header: Label = null
var grid: GridContainer = null
var hint: Label = null
var cols: Array = []
var col_idx: Array = []
var col_mode: Array = []
var mode_buttons: Array = []
var _sig: String = ""
var _open_t: float = 0.0

func _ready() -> void:
	set_process_input(true)
	# the tree is frozen by Game.cs the moment this card opens; the card opts OUT of
	# the pause so buttons + ESC keep firing through the freeze (Campfire.gd human-test
	# bug: clicks died once the tree paused; process_mode is the ONLY 4.7 knob for it)
	set_process_mode(Node.PROCESS_MODE_ALWAYS)
	_build()

func _input(ev: InputEvent) -> void:
	# a hidden card owns NO input (the Campfire.gd trap: hidden Controls still receive
	# _input, and a stray click used to leave the tree paused with nothing on screen).
	# OPENING is Game.cs' "pause" action rail in _process; this rail only CLOSES a card
	# that is already up: dismissed fires the C# handler SYNCHRONOUSLY, so visible is
	# already false when Game._process sees the same physical press - the C# open rail
	# adds a one-frame just-closed guard for that handoff (see ui/INTEGRATION.md).
	if not visible:
		return
	if ev is InputEventKey and ev.pressed and not ev.echo and ev.keycode == 4194301:
		_close("esc")
	if ev is InputEventMouseButton and ev.pressed and ev.button_index == 1:
		# the camp card's human-test rounds 1-4 showed nested Buttons NOT firing pressed,
		# so raw left clicks are routed here too. _apply_mode is idempotent, so a click
		# the Button DID answer is ignored the second time round
		_route_click(ev)
		return

func _process(_d: float) -> void:
	# the tree pause freezes GAME nodes, but this card is PROCESS_MODE_ALWAYS so this
	# rail runs during the freeze: Game.cs re-Sets members on every open (a longer
	# roster later in the run), and Show() alone repaints nothing - so the card
	# re-derives its own copy whenever the data it sees CHANGED (one string compare).
	if not visible:
		return
	# pilot valve lives HERE, not in Game._process: the pause is a TRUE tree pause, so
	# Game._process is frozen while the card is up (seat 545 froze at beat 1 with a
	# C#-side valve). This card is PROCESS_MODE_ALWAYS so this rail DOES tick during
	# the freeze: on timeout it closes through _close, which emits the same dismissed
	# signal the ESC rail uses - C# still owns the unpause.
	_open_t += _d
	if pilot_timeout > 0.0 and _open_t >= pilot_timeout:
		_close("pilot")
		_open_t = 0.0
		return
	var sig: String = _members_sig()
	if sig != _sig:
		_paint_columns()

func _members_sig() -> String:
	# the repaint trigger: name + upgrade line + current mode, per row Game.cs
	# pushed. A recruit arriving mid-run or a fresh pick CHANGES this, so the card
	# re-derives its own copy without the C# side having to ask it.
	var s: String = ""
	for row in members:
		s += str(row.get("name", "?")) + "|" + str(row.get("picks", "")) + "|" + str(row.get("target_mode", 0)) + ";"
	return s

func _close(reason: String) -> void:
	# the C# side owns Hide() + GetTree().Paused = false; the card only speaks up
	# ESC and the pilot timeout are the card-side closers now; both emit dismissed, so
	# the C# handler still owns the unpause (see the _process valve above)
	# (dismissed.emit() runs the Game.cs handler SYNCHRONOUSLY - the Hide + unpause
	# lands BEFORE this line returns, which is what makes the ESC handoff safe)
	# ESC is the only card-side closer; the pilot valve closes from C# directly.
	print("[10thAge] pause card: ESC closed it (", reason, ") - Game.cs hides + resumes")
	dismissed.emit()

func _build() -> void:
	# ---------- the field behind the card: dim + soft (the user's fade/ blur ask) ----------
	# the dark wash ALWAYS paints first: it is also the fallback, because a headless
	# bench has no GPU and a shader that cannot load leaves the wash as the softener.
	wash = ColorRect.new()
	wash.name = "Wash"
	wash.color = Color(0.06, 0.05, 0.045, 0.55)
	wash.anchor_left = 0.0
	wash.anchor_top = 0.0
	wash.anchor_right = 1.0
	wash.anchor_bottom = 1.0
	add_child(wash)
	blur = null
	# use_blur is an @export and C# pushes it as a VARIANT: force a real bool or
	# `not` throws on e.g. Int(1) - same tax as the campfire float trap
	var want_blur: bool = bool(use_blur)
	if want_blur:
		_paint_blur()
	# ---------- the card furniture over the dimmed field ----------
	header = Label.new()
	header.name = "Header"
	header.anchor_left = 0.0
	header.anchor_top = 0.0
	header.anchor_right = 1.0
	header.anchor_bottom = 0.0
	header.offset_left = 0.0
	header.offset_right = 0.0
	header.offset_top = 62.0
	header.offset_bottom = 150.0
	# 4.7 moved HORIZONTAL_ALIGNMENT_* to @GlobalScope: the Label./Control. bases 4.3-4.5
	# cards used both fail in 4.7, so the enum is referenced bare
	header.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	header.add_theme_color_override("font_color", GOLD)
	add_child(header)
	hint = Label.new()
	hint.name = "Hint"
	hint.anchor_left = 0.0
	hint.anchor_top = 0.0
	hint.anchor_right = 1.0
	hint.anchor_bottom = 0.0
	hint.offset_left = 0.0
	hint.offset_right = 0.0
	hint.offset_top = 988.0
	hint.offset_bottom = 1050.0
	hint.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	hint.add_theme_color_override("font_color", MUTED)
	hint.scale = Vector2(0.82, 0.82)
	add_child(hint)
	# the strip is placed by _paint_columns: its width and row height depend on how
	# many members are RECRUITED, which is only known once members arrive
	grid = GridContainer.new()
	grid.name = "Roster"
	add_child(grid)
	_paint_columns()

func _paint_blur() -> void:
	# the arena behind the card, softened. screen_texture samples whatever the game
	# already drew, so this ColorRect needs no art of its own
	var sh = load("res://ui/pause_blur.gdshader")
	if sh == null:
		# no GPU on the headless bench / a bad shader: the wash alone stays the fade
		print("[10thAge] pause card: blur shader did not load - plain dark wash")
		return
	blur = ColorRect.new()
	blur.name = "Blur"
	blur.anchor_left = 0.0
	blur.anchor_top = 0.0
	blur.anchor_right = 1.0
	blur.anchor_bottom = 1.0
	var mat: ShaderMaterial = ShaderMaterial.new()
	mat.shader = sh
	blur.material = mat
	add_child(blur)

func _paint_columns() -> void:
	# a repaint buries the old subtrees: the mode buttons live INSIDE their columns,
	# so queue_free covers them - but the registries must stop referencing them the
	# same moment, or the click router could fire at a freed button
	for c in cols:
		c.queue_free()
	cols.clear()
	col_idx.clear()
	col_mode.clear()
	mode_buttons.clear()
	# only RECRUITED rows paint: "away" means never joined, and the camp card hides
	# those too (user 2026-11-02: listing characters that exist to find spoiled runs)
	var shown: Array = []
	for row in members:
		if str(row.get("state", "")) != "away":
			shown.append(row)
	if shown.size() == 0:
		# nothing recruited: the card stays up for the header only, never an empty strip
		grid.columns = 1
		grid.custom_minimum_size = Vector2(1.0, 1.0)
		return
	var wide: int = shown.size()
	if wide > 5:
		wide = 5
	var rows: int = 1
	if shown.size() > 5:
		# the mockup is five columns wide; 6..9 recruits wrap to a second strip row.
		# a run is only ever 1 leader + 8 joins, so two rows is the ceiling
		rows = 2
	var big: bool = rows == 1
	# one row gets the whole band above the hint; two rows split it with a gap
	var cell_w: float = 300.0
	var gap: float = 16.0
	var gw: float = wide * cell_w + (wide - 1) * gap
	var top: float = 168.0
	var gh: float = 0.0
	if big:
		gh = 620.0
	else:
		gh = 392.0
	# centred strip: x anchors at 0.5 make the offsets a pixel box around mid-screen,
	# so a 1280-wide window keeps the dim field showing at the sides (the mockup look)
	grid.anchor_left = 0.5
	grid.anchor_right = 0.5
	grid.anchor_top = 0.0
	grid.anchor_bottom = 0.0
	grid.offset_left = -gw * 0.5
	grid.offset_right = gw * 0.5
	grid.offset_top = top
	grid.offset_bottom = top + gh
	grid.custom_minimum_size = Vector2(gw, gh)
	grid.columns = wide
	var ci: int = 0
	for row in shown:
		_paint_one(row, ci, cell_w, gh, big)
		ci += 1
	# header and hint read off what just painted (stage_name is Game.cs' stage label)
	if header != null:
		header.text = "PAUSED  -  " + str(stage_name)
	if hint != null:
		hint.text = "ESC closes   -   click a mode to set it"

func _paint_one(row: Dictionary, ci: int, w: float, h: float, big: bool) -> void:
	# one mockup column: painted mini -> name -> target logic -> upgrades
	var col: PanelContainer = PanelContainer.new()
	col.name = "Member" + str(ci)
	col.custom_minimum_size = Vector2(w, h)
	col.add_theme_stylebox_override("panel", _sb_column())
	grid.add_child(col)
	cols.append(col)
	# a PanelContainer lays its direct children in A ROW, so everything stacked here
	# goes into one VBox body: anchors mean nothing to a container's children
	var body: VBoxContainer = VBoxContainer.new()
	body.name = "Body"
	body.add_theme_constant_override("separation", 8)
	col.add_child(body)
	# the ROSTER idx is the only thing a button may speak: column slots shift when a
	# recruit joins mid-run, so target_changed always carries Game.cs' GetMemberByIndex key
	var mi: int = int(row.get("idx", ci))
	col_idx.append(mi)
	var md: int = int(row.get("target_mode", 0))
	if md < 0:
		md = 0
	if md > 4:
		md = 4
	col_mode.append(md)
	var pad: float = w - 28.0
	# ---------- painted mini (skipped when the art is missing) ----------
	var art: String = str(row.get("art", ""))
	var tex = null
	if art != "":
		tex = load(art)
	if tex != null:
		var tr: TextureRect = TextureRect.new()
		tr.name = "Art"
		var sz: float = 150.0
		if not big:
			sz = 96.0
		tr.custom_minimum_size = Vector2(sz, sz)
		tr.size_flags_horizontal = 4
		tr.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
		tr.stretch_mode = TextureRect.STRETCH_KEEP_ASPECT_CENTERED
		tr.texture = tex
		body.add_child(tr)
	# ---------- name ----------
	var name_l: Label = Label.new()
	name_l.name = "Name"
	name_l.text = str(row.get("name", "?"))
	name_l.custom_minimum_size = Vector2(pad, 26.0)
	name_l.size_flags_horizontal = 3
	name_l.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	name_l.add_theme_color_override("font_color", PAPER)
	body.add_child(name_l)
	# ---------- target logic ----------
	var tag_l: Label = Label.new()
	tag_l.name = "LogicTag"
	tag_l.text = "target logic"
	tag_l.custom_minimum_size = Vector2(pad, 18.0)
	tag_l.size_flags_horizontal = 3
	tag_l.add_theme_color_override("font_color", AMBER)
	tag_l.scale = Vector2(0.85, 0.85)
	body.add_child(tag_l)
	var modes: Array = []
	for m in range(5):
		var bb: Button = Button.new()
		bb.name = "Mode" + str(m)
		bb.text = MODE_LABEL[m]
		var bh: float = 30.0
		if not big:
			bh = 20.0
		bb.custom_minimum_size = Vector2(pad, bh)
		bb.add_theme_stylebox_override("normal", _sb_mode(false))
		bb.add_theme_stylebox_override("hover", _sb_mode(false))
		bb.add_theme_stylebox_override("pressed", _sb_mode(true))
		# the nested Buttons stopped firing pressed in the camp card's rounds 1-4, so
		# _input routes raw clicks too; _apply_mode is idempotent either way
		bb.pressed.connect(_on_mode_pressed.bind(ci, m))
		body.add_child(bb)
		modes.append(bb)
		_tint_mode(bb, m == md)
	mode_buttons.append(modes)
	# ---------- upgrades: the member's own pick line, one row per pick ----------
	var up_l: Label = Label.new()
	up_l.name = "Upgrades"
	up_l.text = _upgrade_block(str(row.get("picks", "")))
	up_l.custom_minimum_size = Vector2(pad, 120.0)
	if not big:
		up_l.custom_minimum_size = Vector2(pad, 96.0)
	up_l.size_flags_horizontal = 3
	up_l.add_theme_color_override("font_color", MUTED)
	up_l.scale = Vector2(0.8, 0.8)
	body.add_child(up_l)

func _upgrade_block(picks: String) -> String:
	# Game.cs sends pickHistory as "a, b, c"; the mockup reads one upgrade per row.
	# a long pick list (a level-20 veteran) would spill past the panel, so the block
	# prints the head and says how many more there are - the count never lies
	if picks == "" or picks == "no picks yet":
		return "no picks yet"
	var parts: Array = picks.split(", ")
	var keep: int = parts.size()
	if keep > 9:
		keep = 9
	var out: String = ""
	for i in range(keep):
		if i > 0:
			out += "\n"
		out += parts[i]
	if parts.size() > keep:
		if out != "":
			out += "\n"
		out += "+" + str(parts.size() - keep) + " more"
	return out

func _sb_column() -> StyleBoxFlat:
	var sb: StyleBoxFlat = StyleBoxFlat.new()
	sb.bg_color = Color(0.13, 0.12, 0.11, 0.94)
	sb.border_width_left = 2
	sb.border_width_top = 2
	sb.border_width_right = 2
	sb.border_width_bottom = 2
	sb.border_color = Color(0.42, 0.34, 0.22, 1)
	sb.corner_radius_top_left = 8
	sb.corner_radius_top_right = 8
	sb.corner_radius_bottom_right = 8
	sb.corner_radius_bottom_left = 8
	sb.content_margin_left = 14.0
	sb.content_margin_top = 12.0
	sb.content_margin_right = 14.0
	sb.content_margin_bottom = 12.0
	return sb

func _sb_mode(chosen: bool) -> StyleBoxFlat:
	var sb: StyleBoxFlat = StyleBoxFlat.new()
	if chosen:
		sb.bg_color = Color(0.83, 0.64, 0.24, 0.22)
	else:
		sb.bg_color = Color(0.20, 0.18, 0.15, 0.9)
	sb.border_width_left = 1
	sb.border_width_top = 1
	sb.border_width_right = 1
	sb.border_width_bottom = 1
	sb.border_color = Color(0.52, 0.44, 0.33, 1)
	sb.corner_radius_top_left = 4
	sb.corner_radius_top_right = 4
	sb.corner_radius_bottom_left = 4
	sb.corner_radius_bottom_right = 4
	return sb

func _tint_mode(bb: Button, chosen: bool) -> void:
	# modulate tints the whole button, text included: one property, no theme-key
	# guessing, and it reads the same on the headless bench as in play
	if chosen:
		bb.modulate = GOLD
	else:
		bb.modulate = GREY

func _apply_mode(ci: int, m: int) -> void:
	# idempotent: the pressed signal AND the raw click router can both answer one
	# click, so a repeat must never spam target_changed or re-tint
	if ci < 0 or ci >= col_idx.size():
		return
	if m < 0 or m > 4:
		return
	if ci < col_mode.size() and col_mode[ci] == m:
		return
	if ci < col_mode.size():
		col_mode[ci] = m
	if ci < mode_buttons.size():
		var btns: Array = mode_buttons[ci]
		for j in range(btns.size()):
			_tint_mode(btns[j], j == m)
	print("[10thAge] pause card: column ", ci, " -> mode ", m, " (", MODE_LABEL[m], ")")
	# the card speaks the ROSTER idx, so a slot that shifted mid-run still lands right
	target_changed.emit(int(col_idx[ci]), m)

func _on_mode_pressed(ci: int, m: int) -> void:
	_apply_mode(ci, m)

func _route_click(ev: InputEvent) -> void:
	# first button whose rect agrees the cursor is on it wins; rects are checked in
	# paint order, so overlapping stale nodes can never shadow a live column
	for ci in range(mode_buttons.size()):
		if ci >= cols.size():
			return
		var btns: Array = mode_buttons[ci]
		for m in range(btns.size()):
			if _was_click_on(btns[m], ev):
				_apply_mode(ci, m)
				return

func _was_click_on(b: Button, ev: InputEvent) -> bool:
	# 4.7 keeps dropping hit-test helpers (rect_has_point and button_name both gone),
	# and the camp card needed THREE spaces to agree before a click counted: Control-
	# space mouse vs global rect, raw event position vs global rect, then the button's
	# own local rect vs its local mouse. Copy of that proven shape.
	var gr: Rect2 = b.get_global_rect()
	if gr.has_point(b.get_global_mouse_position()):
		return true
	if gr.has_point(ev.position):
		return true
	if b.size.x > 1.0 and b.size.y > 1.0:
		var lr: Rect2 = Rect2(Vector2(0.0, 0.0), b.size)
		if lr.has_point(b.get_local_mouse_position()):
			return true
	return false
