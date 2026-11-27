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
# follows the user's mockup: dark columns, mini -> name -> TARGET LOGIC -> in-run STATS
# (user 2026-11-25: the card cut 4.5 columns and "varied a lot based on text length" -
# the UPGRADE block drove both: GridContainer sized columns to the unwrapped Label. Upgrades
# now leave the card and ride a HOVER TOOLTIP - mouse over a character and its pick list
# pops above its column; the row itself centres on the viewport and always fits 5).

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
var pause_field: CanvasLayer = null
var header: Label = null
var grid: GridContainer = null
var hint: Label = null
var cols: Array = []
var col_idx: Array = []
var col_mode: Array = []
var mode_buttons: Array = []
var _sig: String = ""
var _open_t: float = 0.0
# ---------- hover tooltip (user 2026-11-25: upgrades pop over a card on mouse hover) ----------
var tooltip: PanelContainer = null
var tooltip_lbl: Label = null
var upgrade_texts: Array = [] # parallel to cols: each column's upgrade block for the pop-up
var _cell_w: float = 300.0 # last painted cell width: the tooltip wraps to the card
var _grid_top: float = 168.0 # last painted strip top: the tooltip sits above the hovered card

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
	# OPENING is Game.cs' "pause_menu" action rail in _process; this rail only CLOSES a card
	# that is already up: dismissed fires the C# handler SYNCHRONOUSLY, so visible is
	# already false when Game._process sees the same physical press - the C# open rail
	# adds a one-frame just-closed guard for that handoff (see ui/INTEGRATION.md).
	if not visible:
		return
	# KEYCODE TAX: Escape is 4194305 here (ui_cancel's own keycode, printed by the probe
	# seat), not 4194301; and P (80) closes alongside it.
	# the card's OWN closers: ESC and P. dismissed runs the C# handler synchronously.
	if ev is InputEventKey and ev.pressed and not ev.echo and (ev.keycode == 4194305 or ev.keycode == 80):
		_close("esc/p")
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
	_hover_update()
	var sig: String = _members_sig()
	if sig != _sig:
		# user 2026-11-06 playtest (targeting highlight only refreshed after closing and
		# re-opening the card): _sig was NEVER reassigned, so this gate was true EVERY
		# _process frame - _paint_columns() queue_free()d and rebuilt the whole column
		# strip on every frame, which shredded the live _apply_mode highlight the click
		# rail had just painted and re-read the stale target_mode Game.cs pushed at open.
		# Taking the signature fixes the loop: repaint only on a real roster change.
		_paint_columns()
		_sig = sig

func _members_sig() -> String:
	# the repaint trigger: name + upgrade line + current mode, per row Game.cs
	# pushed. A recruit arriving mid-run or a fresh pick CHANGES this, so the card
	# re-derives its own copy without the C# side having to ask it.
	var s: String = ""
	for row in members:
		# the condensed upgrade block is part of the sig: a fresh rank repaints the card
		s += str(row.get("name", "?")) + "|" + str(row.get("upgrade_block", "")) + "|" + str(row.get("picks", "")) + "|" + str(row.get("target_mode", 0)) + ";"
		# user 2026-11-25: the card paints the IN-RUN stats now, so the numbers
		# must count in the sig too - a level-up or fresh gear changes them with
		# NO roster change, and this signature is the only repaint trigger
		# str() prints floats as they come: if the box would show a difference,
		# the sig sees it too, which is exactly honest here
		# ROUND 16: area/crit/critd/multi/eva/xp/collect joined the box, so they must count
		# in the sig too or the card would never repaint on a crit/evasion/XP change alone
		s += str(row.get("hp", 0)) + "/" + str(row.get("max_hp", 0)) + "/" + str(row.get("dmg", 0)) + "/" + str(row.get("spd", 0.0)) + "/" + str(row.get("move", 0.0)) + "/" + str(row.get("rng", 0.0)) + "/" + str(row.get("arm", 0)) + "/" + str(row.get("area", 0.0)) + "/" + str(row.get("crit", 0.0)) + "/" + str(row.get("critd", 0.0)) + "/" + str(row.get("multi", 0)) + "/" + str(row.get("eva", 0.0)) + "/" + str(row.get("xp", 1.0)) + "/" + str(row.get("collect", 0.0)) + ";"
	return s

func _close(reason: String) -> void:
	# the C# side owns Hide() + GetTree().Paused = false; the card only speaks up
	# ESC and the pilot timeout are the card-side closers now; both emit dismissed, so
	# the C# handler still owns the unpause (see the _process valve above)
	# (dismissed.emit() runs the Game.cs handler SYNCHRONOUSLY - the Hide + unpause
	# lands BEFORE this line returns, which is what makes the ESC handoff safe)
	# ESC is the only card-side closer; the pilot valve closes from C# directly.
	print("[10thAge] pause card: ESC/P closed it (", reason, ") - Game.cs hides + resumes")
	dismissed.emit()

func _build() -> void:
	# ---------- the field behind the card: dim + soft (the user's fade/ blur ask) ----------
	# the dark wash ALWAYS paints first: it is also the fallback, because a headless
	# bench has no GPU and a shader that cannot load leaves the wash as the softener.
	# PAUSE BLUR LAW (user 2026-11-12 hand-play: "The blur is still too much on the eyes.
	# It's not the background, it's the text that's the problem"): the wash + blur sat on this
	# layer-4 ROOT, so the blur shader sampled screen_texture THROUGH the HUD (layer 0) and
	# smeared its HP strips + labels. They now ride their OWN nested CanvasLayer at layer 1:
	# canvas_layer.cpp feeds viewport_set_canvas_stacking the node's OWN layer (nesting is
	# ABSOLUTE against the viewport, not additive with the parent), so the stack is arena(0)
	# -> blur field(1) -> HUD (lifted to 2 by Game.cs) -> this card furniture(4). The blur
	# only ever samples what sits BELOW layer 1 now: the arena stays soft, the text stays crisp.
	pause_field = CanvasLayer.new()
	pause_field.name = "PauseField"
	pause_field.layer = 1 # explicit even though the default is 1: the layer IS the law
	add_child(pause_field)
	wash = ColorRect.new()
	wash.name = "Wash"
	wash.color = Color(0.06, 0.05, 0.045, 0.55)
	wash.anchor_left = 0.0
	wash.anchor_top = 0.0
	wash.anchor_right = 1.0
	wash.anchor_bottom = 1.0
	pause_field.add_child(wash)
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
	# the hover pop-up lives AFTER the grid so it paints OVER the columns (draw order
	# is child order inside one CanvasLayer); it lives on the ROOT layer, not a nested
	# one, so the blur field (layer 1) and HUD (layer 2) can never bury it.
	# mouse_pass_through: the pop-up draping a column must never eat the mode-button
	# clicks the click router still needs under it.
	tooltip = PanelContainer.new()
	tooltip.name = "UpgradeTip"
	tooltip.visible = false
	tooltip.mouse_filter = 1 # MOUSE_FILTER_PASS: registers the hit but clicks fall through
	tooltip.add_theme_stylebox_override("panel", _sb_column())
	add_child(tooltip)
	tooltip_lbl = Label.new()
	tooltip_lbl.name = "TipText"
	# 4.x keeps these as bare constants (TextWrappedString was the 3.x enum)
	tooltip_lbl.autowrap_mode = 2 # enum slot: AUTOWRAP_WORD_SMART
	tooltip_lbl.add_theme_color_override("font_color", PAPER)
	# scale 0.8 matches the card's own Labels so the pop-up reads at the same size
	tooltip_lbl.scale = Vector2(0.8, 0.8)
	tooltip_lbl.position = Vector2(10.0, 8.0)
	tooltip_lbl.mouse_filter = 1 # the child Label must not block either
	tooltip.add_child(tooltip_lbl)
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
	pause_field.add_child(blur)

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
	# user 2026-11-06 playtest: the card's right edge was clipped - 5 columns x 300 px
	# outran a 1280 window. Size the column FROM the viewport instead, clamped [170, 300].
	var vp: Vector2 = grid.get_viewport_rect().size
	if vp.x > 1.0:
		var avail: float = vp.x - 96.0 - float(wide - 1) * gap
		# two strip rows split the band vertically, so the width budget is unchanged
		cell_w = avail / float(wide)
		if cell_w > 300.0:
			# a huge window never gets the old oversized cells: 300 is the roomy ceiling
			cell_w = 300.0
		# the floor keeps mode buttons clickable; labels wrap inside the column
		if cell_w < 170.0:
			cell_w = 170.0
	var gw: float = wide * cell_w + (wide - 1) * gap
	var top: float = 168.0
	var gh: float = 0.0
	if big:
		gh = 620.0
	else:
		gh = 392.0
	# user 2026-11-25: the IN-RUN STATS box replaces the fluid upgrade block, so the
	# cell box governs the column width and all 5 recruited cards fit, centred.
	# The tooltip measures itself off the cell box + strip top so a pop-up wraps to
	# the card it hovers.
	upgrade_texts.clear()
	_cell_w = cell_w
	_grid_top = top
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
		# the user reads the card with keys in hand: name BOTH closers
		hint.text = "ESC or P closes   -   click a mode to set it"

func _paint_one(row: Dictionary, ci: int, w: float, h: float, big: bool) -> void:
	# one mockup column: painted mini -> name -> target logic -> IN-RUN STATS
	# (upgrades left the column on 2026-11-25: they ride the hover pop-up)
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
		# user 2026-11-06 playtest: the CHOSEN mode wears the highlight box in normal
		# AND hover states, not only while pressed: the pick reads with the cursor
		# anywhere on the card (md is the mode Game.cs says this column runs).
		bb.add_theme_stylebox_override("normal", _sb_mode(m == md))
		bb.add_theme_stylebox_override("hover", _sb_mode(m == md))
		bb.add_theme_stylebox_override("pressed", _sb_mode(true))
		# the nested Buttons stopped firing pressed in the camp card's rounds 1-4, so
		# _input routes raw clicks too; _apply_mode is idempotent either way
		bb.pressed.connect(_on_mode_pressed.bind(ci, m))
		body.add_child(bb)
		modes.append(bb)
		_tint_mode(bb, m == md)
	mode_buttons.append(modes)
	# ---------- upgrades LEAVE the card (user 2026-11-25: they ride the pop-up) ----
	# the pick line stays PARKED per column: _hover_update paints it above the card
	# the cursor rests on. Keeping it out of the column is what fixes the cut-off -
	# an unwrapped Label's min width drove the GridContainer wider than the strip,
	# so the card's length literally decided where the row got sliced.
	var ublock: String = str(row.get("upgrade_block", ""))
	# user 2026-11-06 playtest: the C# side sends ONE condensed line per taken track
	# (newlines already in it), so the card must NOT re-split that text.
	# the raw picks chain stays only as the fallback for a build that sends no block.
	# GD's own line-up split would mangle the block: its segments carry commas too.
	if ublock == "":
		ublock = _upgrade_block(str(row.get("picks", "")))
	upgrade_texts.append(ublock)
	# ---------- in-run stats: the box under target logic (user 2026-11-25) ----------
	# HP, damage, attack speed, move, range, armour: every number is Game.cs' live
	# read, so gear and level bonuses ride with it.
	# AUTOWRAP is the point: an unwrapped Label reports its whole text as its min
	# width, and the GridContainer widened every column to that, so the upgrade
	# length decided where the row was sliced off past the strip's edge. Word-wrap
	# keeps the Label's min width to the longest word, so the CELL box governs and
	# the 0.5 anchors centre honestly - five recruited members always fit.
	# the old clip law carries: a long text stays inside its column.
	var st_l: Label = Label.new()
	st_l.name = "Stats"
	st_l.text = _stat_line(row)
	# taller band when one strip row owns the box: the stats read on two easy lines
	var st_h: float = 46.0
	if not big:
		st_h = 34.0
	st_l.custom_minimum_size = Vector2(pad, st_h)
	st_l.size_flags_horizontal = 3
	st_l.autowrap_mode = 2 # enum slot: AUTOWRAP_WORD_SMART
	st_l.add_theme_color_override("font_color", MUTED)
	st_l.scale = Vector2(0.8, 0.8)
	# scale 0.8 is applied to the Label, so the clip box follows the drawn node
	body.add_child(st_l)

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
			# the live box too, not only the tint: the chosen mode reads whether or not
			# the cursor sits on it, so an idle card still names the logic in play
			btns[j].add_theme_stylebox_override("normal", _sb_mode(j == m))
			btns[j].add_theme_stylebox_override("hover", _sb_mode(j == m))
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

func _was_hover_on(c: Control, ev: Vector2) -> bool:
	# the hover twin of _was_click_on: 4.7 keeps dropping hit-test helpers, and the
	# camp card needed THREE spaces to agree before a cursor counted on a node - the
	# global rect vs the global mouse, the global rect vs the raw point, then the
	# node's own local rect vs its local mouse. Same geometry, no InputEvent.
	# the local check is what follows the GridContainer's own transform, so a
	# centred strip still reports the right column at any window width.
	var gr: Rect2 = c.get_global_rect()
	# the pop-up is mouse_pass_through, so the hovered column still gets the
	# cursor even when the panel drapes it - the hover never hides a click
	if gr.has_point(c.get_global_mouse_position()):
		return true
	if gr.has_point(ev):
		return true
	if c.size.x > 1.0 and c.size.y > 1.0:
		# the column is inside the GridContainer's transform, so its LOCAL mouse is
		# the only reading that follows the container's own position
		var lr: Rect2 = Rect2(Vector2(0.0, 0.0), c.size)
		# the tooltip lives on the ROOT layer over the strip, so the node under the
		# cursor may be the pop-up's own panel; the column's local mouse still names
		# it because mouse_pass_through leaves the hit-test with the card
		if lr.has_point(c.get_local_mouse_position()):
			return true
	return false

func _stat_line(row: Dictionary) -> String:
	# ROUND 16: the user handed back the FULL row list, so the labels are theirs
	# verbatim and in their order: HP / Attack Damage / Attack Speed / Range / Area /
	# Crit Chance / Crit Damage / Multistrike / Armour / Evasion / Movement Speed /
	# XP / Collection Range. Gold was named but stays OUT: the run has no gold
	# economy yet, and the card refuses to print a stat nothing in the run can
	# change. Multiplier rows read as PERCENT INCREASES, not x-factors: the user
	# found "speed x1.63" unclear, and +63% is the honest read (_pct below).
	# Crit Chance and Evasion are ABSOLUTE probabilities - plain N% is the truth,
	# a +N% would promise an increase nobody took (_pct_plain below).
	# Round-15 laws carried: armour prints ONLY when non-zero (a zero line is
	# noise on four of five cards), range 0 means melee and is STILL shown so a
	# melee row never looks blank, and newlines come FIRST with autowrap second
	# (Campfire.gd law: 4.7 autowrap alone did not constrain). No format idiom
	# exists in the .gd codebase (grep: no String.num / %.2f), so _fmt_num
	# below does the rounding by hand.
	var lines: Array = []
	lines.append("HP: " + str(int(float(row.get("hp", 0)))) + "/" + str(int(float(row.get("max_hp", 0)))))
	lines.append("Attack Damage: " + str(int(float(row.get("dmg", 0)))))
	lines.append("Attack Speed: " + _pct(float(row.get("spd", 1.0))))
	lines.append("Range: " + _fmt_num(float(row.get("rng", 0.0)), 1))
	lines.append("Area: " + _pct(float(row.get("area", 1.0))))
	# crit chance and evasion are absolute probabilities: plain N% is honest here
	lines.append("Crit Chance: " + _pct_plain(float(row.get("crit", 0.0))))
	lines.append("Crit Damage: " + _pct(float(row.get("critd", 1.0))))
	lines.append("Multistrike: " + str(int(float(row.get("multi", 0)))))
	var arm: int = int(float(row.get("arm", 0)))
	if arm != 0:
		lines.append("Armour: " + str(arm))
	lines.append("Evasion: " + _pct_plain(float(row.get("eva", 0.0))))
	lines.append("Movement Speed: " + _fmt_num(float(row.get("move", 0.0)), 1))
	lines.append("XP: " + _pct(float(row.get("xp", 1.0))))
	lines.append("Collection Range: " + _pct(float(row.get("collect", 1.0))))
	return "\n".join(lines)


# --- round 15 helpers (append-only tail) ---
func _fmt_num(v: float, dp: int) -> String:
	# by-hand fixed-decimal formatting
	# str() has no format control; % is modulo only
	var neg := v < 0.0
	var t := v if dp == 0 else (v * 10.0 if dp == 1 else v * 100.0)
	var n := absi(int(round(t)))
	# divisor mirrors dp: 0 ones, 1 tenths, 2 hundredths
	var d := 10 if dp == 1 else (100 if dp == 2 else 1)
	# leading-zero pad keeps x2.50 reading as x2.50
	# by-hand zero pad: str(n % d) left-padded to dp chars
	var fs := ""
	if dp > 0:
		var ds := str(n % d)
		while ds.length() < dp:
			ds = "0" + ds
		fs = ds
	var out := str(int(n / d))
	out += ("." + fs) if dp > 0 else ""
	# no sign for a rounded -0 (n already zero then)
	if neg and n != 0:
		# leading minus
		out = "-" + out
	# sign applied
	return out

func _wrap_words(txt: String, per_line: int) -> String:
	# pre-wrap at word boundaries so the tooltip height is countable:
	# 4.x autowrap does not reliably constrain height (Campfire modal law);
	# per_line is conservative, a real wrapped line never exceeds it; long
	# words stay whole and autowrap is the belt for those
	if per_line < 6:
		return txt
	var out := ""
	var cur := ""
	for para: String in txt.split("\n"):
	# flush the previous paragraph's last line
		if cur != "":
			out += cur + "\n"
			cur = ""
		# word budget: cur plus space plus word must fit the line
		for wd: String in para.split(" "):
			if wd == "":
				# collapse doubled spaces
			# skip empty word
				continue
			# long word on an empty line stays whole
			# join with a space when it fits; flush and start fresh otherwise
			if cur == "" or cur.length() + 1 + wd.length() <= per_line:
				cur = wd if cur == "" else cur + " " + wd
			else:
				out += cur + "\n"
				cur = wd
	if cur != "":
		out += cur
	return out

func _hover_update() -> void:
	# hover tooltip: raw mouse point, no event; columns may have moved, rescan every frame
	tooltip.visible = false
	var idx := -1
	for i: int in range(cols.size()):
		# null guard: cols entries are only Controls once _build has run
		if cols[i] == null:
			continue
		# triple-check hover: grid transform may have changed, use each column's own frame
		if _was_hover_on(cols[i], grid.get_global_mouse_position()):
			idx = i
			break
	if idx == -1 or idx >= upgrade_texts.size():
		return
	# pre-wrap by conservative chars/line so the panel height can be computed here
	tooltip_lbl.text = _wrap_words(upgrade_texts[idx], _est_chars(_cell_w))
	tooltip.visible = true
	# explicit sizing: PanelContainer min-size caching is unreliable in 4.7
	var lines := tooltip_lbl.text.count("\n") + 1
	tooltip.size = Vector2(_cell_w, lines * 16.0 + 40.0)
	var cpos: Vector2 = cols[idx].get_global_position() + cols[idx].size * 0.5
	tooltip.position = Vector2(cpos.x - tooltip.size.x * 0.5, max(0.0, _grid_top - tooltip.size.y))
	tooltip.position.x = clampf(tooltip.position.x, 8.0, grid.get_viewport_rect().size.x - tooltip.size.x - 8.0)


func _est_chars(w: float) -> int:
	# conservative chars per line: assume wide glyphs so lines wrap earlier than they really do
	# label renders at 0.8 scale; 8.0 px per char at native size stays under real capacity
	return max(8, int(w * 0.8 / 8.0))


# --- round 16 helpers (append-only tail): factor -> percent-increase strings ---
# the user reads multipliers as INCREASES: spd factor 1.63 must print +63%, and
# _fmt_num alone cannot do the (factor - 1) * 100 step, so it lives here.
func _pct(f: float) -> String:
	# multiplier factor -> percent INCREASE: 1.63 -> +63%, 1.0 -> +0% (sign always
	# printed so a debuff would read -x% and a neutral row is visibly neutral)
	var p: int = int(round((f - 1.0) * 100.0))
	if p < 0:
		return "-" + _fmt_num(float(-p), 0) + "%"
	return "+" + _fmt_num(float(p), 0) + "%"

func _pct_plain(p: float) -> String:
	# ABSOLUTE probabilities (crit chance, evasion) print as plain N%: a +N% would
	# read as "an increase nobody took". 0.05 -> 5%
	return _fmt_num(round(p * 100.0), 0) + "%"
