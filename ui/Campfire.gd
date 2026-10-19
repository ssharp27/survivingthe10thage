extends CanvasLayer
class_name CampfireScreen
# Signals wired into Game.cs by the developer (see ui/INTEGRATION.md).
# rest_chosen carries "short" or "long".
signal rest_chosen(rest_type: String)
signal cancelled
# ---------- grimoire palette (parchment / ink / gold / blood / sage) ----------
var PAPER = Color(0.957, 0.929, 0.847, 1)
var INK = Color(0.21, 0.17, 0.12, 1)
var MUTED = Color(0.52, 0.44, 0.33, 1)
var GOLD = Color(0.83, 0.64, 0.24, 1)
var SAGE = Color(0.44, 0.58, 0.40, 1)
var BLOOD = Color(0.70, 0.26, 0.26, 1)
var AMBER = Color(0.82, 0.58, 0.20, 1)
var GREY = Color(0.62, 0.62, 0.62, 1)
var BAR_BG = Color(0.78, 0.70, 0.58, 1)
# ---------- data bound by Game.cs at integration time ----------
@export var party_data: Array[Dictionary] = []
@export var stage_name: String = "Tanurial Forest"
@onready var grid: GridContainer = $Frame/Contents/PartyGrid
@onready var btn_short: Button = $Frame/Contents/RestRow/ShortCard/ShortRest
@onready var btn_long: Button = $Frame/Contents/RestRow/LongCard/LongRest
@onready var commit: Button = $Frame/Contents/ConfirmBar/Commit
@onready var modal: Panel = $ConfirmModal
@onready var modal_body: Label = $ConfirmModal/ModalContents/ModalBody
@onready var btn_rest_now: Button = $ConfirmModal/ModalContents/ModalButtons/RestNow
@onready var btn_change_mind: Button = $ConfirmModal/ModalContents/ModalButtons/ChangeMind
@onready var subtitle: Label = $Frame/Contents/Header/TitleBlock/Subtitle
var _selection: int = 0
var _cards: Array[Panel] = []
var _sb_normal: StyleBoxFlat
var _sb_selected: StyleBoxFlat
func _ready() -> void:
	set_process_input(true)
	# the confirm modal freezes the tree; the rest card must stay live through it:
	# ALWAYS keeps this node (and the whole UI subtree below it, children inherit)
	# processing + taking mouse clicks during pause, and keeps this node's _input
	# firing (human-test bug: SPACE/click died the moment COMMIT paused the tree).
	# NOTE: 4.7 has NO Control.pause_picking property - assigning it throws and
	# aborts _ready, which half-builds the whole card. process_mode is the knob.
	set_process_mode(Node.PROCESS_MODE_ALWAYS)
	if party_data.size() == 0:
		_seed_demo()
	_sb_normal = _stylebox_button(Color(0.957, 0.929, 0.847, 1), Color(0.78, 0.66, 0.42, 1))
	_sb_selected = _stylebox_button(Color(0.90, 0.74, 0.42, 1), Color(0.70, 0.52, 0.26, 1))
	# Frame had no stylebox bound, so its dark INK labels floated on the dimmed
	# arena and read as smeared light-on-dark noise (human-test: "impossible to
	# read"); grimoire_theme.tres now binds PanelContainer/panel to paper
	modal.add_theme_stylebox_override("panel", _stylebox_modal())
	_build()
	_select(0)
	subtitle.text = "%s - camp between battles" % [stage_name]
	btn_short.pressed.connect(_on_short_pressed)
	btn_long.pressed.connect(_on_long_pressed)
	commit.pressed.connect(_on_commit_pressed)
	btn_rest_now.pressed.connect(_on_rest_now)
	btn_change_mind.pressed.connect(_on_change_mind)
	modal.hide()
	# Bar widths depend on settled layout; refresh once the tree has laid out.
	call_deferred("_refresh_bars")
func _refresh_bars() -> void:
	for i in range(_cards.size()):
		if i >= party_data.size():
			continue
		var card: Panel = _cards[i]
		var vbox: VBoxContainer = card.get_node("VBox") as VBoxContainer
		var hp_row: HBoxContainer = vbox.get_node("HPRow") as HBoxContainer
		var bar_wrap: Control = hp_row.get_node("BarWrap") as Control
		var bar_bg: ColorRect = bar_wrap.get_node("BarBg") as ColorRect
		var fill: ColorRect = bar_bg.get_node("Fill") as ColorRect
		var data: Dictionary = party_data[i]
		var hp: float = float(data.get("hp", 0))
		var max_hp: float = float(data.get("max_hp", 0))
		var pct: float = 0.0
		if max_hp > 0:
			pct = clamp(hp / max_hp, 0.0, 1.0)
		var inner: float = bar_wrap.size.x - 4.0
		if inner > 0:
			fill.size = Vector2(inner * pct, 12.0)
			fill.position = Vector2(2.0, 2.0)
func _seed_demo() -> void:
	party_data = [
		{"name": "Baldrun", "klass": "Rune Knight", "weapon": "Maul", "hp": 58, "max_hp": 150, "state": "up", "leader": true},
		{"name": "Mosiah", "klass": "Bard", "weapon": "Rapier", "hp": 0, "max_hp": 120, "state": "down", "leader": false},
	]
func _build() -> void:
	for c in _cards:
		c.queue_free()
	_cards.clear()
	var slots: int = 4
	for i in range(slots):
		var card: Panel = _make_card()
		grid.add_child(card)
		_cards.append(card)
		if i < party_data.size():
			_populate_card(card, party_data[i])
		else:
			_populate_empty(card)
func _make_card() -> Panel:
	var card: Panel = Panel.new()
	card.name = "MemberCard"
	# 138 (was 168): the full stack (header + roster + grid + rest row +
	# confirm bar) must fit the 592 px inner frame or lower controls spill
	# past the 720 px screen and become unclickable (human-test rounds 1-2)
	card.custom_minimum_size = Vector2(0.0, 138.0)
	card.add_theme_stylebox_override("panel", _stylebox_card())
	var vbox: VBoxContainer = VBoxContainer.new()
	vbox.name = "VBox"
	vbox.anchor_left = 0.0
	vbox.anchor_top = 0.0
	vbox.anchor_right = 1.0
	vbox.anchor_bottom = 1.0
	vbox.size_flags_horizontal = 3
	vbox.add_theme_constant_override("separation", 6)
	card.add_child(vbox)
	# name row
	var name_row: HBoxContainer = HBoxContainer.new()
	name_row.name = "NameRow"
	name_row.anchor_left = 0.0
	name_row.anchor_top = 0.0
	name_row.anchor_right = 1.0
	name_row.size_flags_horizontal = 3
	name_row.add_theme_constant_override("separation", 10)
	var name_l: Label = Label.new()
	name_l.name = "Name"
	name_l.add_theme_color_override("font_color", INK)
	name_l.size_flags_horizontal = 3
	var leader_l: Label = Label.new()
	leader_l.name = "LeaderTag"
	leader_l.text = "LEADER"
	leader_l.add_theme_color_override("font_color", GOLD)
	leader_l.scale = Vector2(0.75, 0.75)
	leader_l.visible = false
	name_row.add_child(name_l)
	name_row.add_child(leader_l)
	vbox.add_child(name_row)
	var weapon_l: Label = Label.new()
	weapon_l.name = "Weapon"
	weapon_l.add_theme_color_override("font_color", MUTED)
	weapon_l.scale = Vector2(0.85, 0.85)
	weapon_l.size_flags_horizontal = 3
	vbox.add_child(weapon_l)
	# hp row
	var hp_row: HBoxContainer = HBoxContainer.new()
	hp_row.name = "HPRow"
	hp_row.anchor_left = 0.0
	hp_row.anchor_top = 0.0
	hp_row.anchor_right = 1.0
	hp_row.size_flags_horizontal = 3
	hp_row.add_theme_constant_override("separation", 10)
	var bar_wrap: Control = Control.new()
	bar_wrap.name = "BarWrap"
	bar_wrap.anchor_left = 0.0
	bar_wrap.anchor_top = 0.0
	bar_wrap.anchor_right = 1.0
	bar_wrap.anchor_bottom = 1.0
	bar_wrap.size_flags_horizontal = 3
	bar_wrap.custom_minimum_size = Vector2(0.0, 16.0)
	var bar_bg: ColorRect = ColorRect.new()
	bar_bg.name = "BarBg"
	bar_bg.anchor_left = 0.0
	bar_bg.anchor_top = 0.0
	bar_bg.anchor_right = 1.0
	bar_bg.anchor_bottom = 1.0
	bar_bg.color = BAR_BG
	var fill: ColorRect = ColorRect.new()
	fill.name = "Fill"
	fill.anchor_left = 0.0
	fill.anchor_top = 0.0
	fill.anchor_right = 0.0
	fill.anchor_bottom = 0.0
	fill.position = Vector2(2.0, 2.0)
	fill.size = Vector2(0.0, 12.0)
	fill.color = GOLD
	bar_bg.add_child(fill)
	bar_wrap.add_child(bar_bg)
	hp_row.add_child(bar_wrap)
	var hp_l: Label = Label.new()
	hp_l.name = "HPText"
	hp_l.add_theme_color_override("font_color", MUTED)
	hp_l.scale = Vector2(0.9, 0.9)
	hp_row.add_child(hp_l)
	vbox.add_child(hp_row)
	# state row
	var state_row: HBoxContainer = HBoxContainer.new()
	state_row.name = "StateRow"
	state_row.anchor_left = 0.0
	state_row.anchor_top = 0.0
	state_row.anchor_right = 1.0
	state_row.size_flags_horizontal = 3
	state_row.add_theme_constant_override("separation", 10)
	var accent: ColorRect = ColorRect.new()
	accent.name = "Accent"
	accent.custom_minimum_size = Vector2(4.0, 26.0)
	accent.size_flags_vertical = 3
	var state_l: Label = Label.new()
	state_l.name = "State"
	state_l.add_theme_color_override("font_color", INK)
	state_l.scale = Vector2(0.9, 0.9)
	state_row.add_child(accent)
	state_row.add_child(state_l)
	vbox.add_child(state_row)
	return card
func _stylebox_card() -> StyleBoxFlat:
	var sb: StyleBoxFlat = StyleBoxFlat.new()
	sb.bg_color = Color(0.991, 0.980, 0.941, 1)
	sb.border_width_left = 2
	sb.border_width_top = 2
	sb.border_width_right = 2
	sb.border_width_bottom = 2
	sb.border_color = Color(0.78, 0.66, 0.42, 1)
	sb.corner_radius_top_left = 10
	sb.corner_radius_top_right = 10
	sb.corner_radius_bottom_right = 10
	sb.corner_radius_bottom_left = 10
	sb.shadow_size = 4.0
	sb.shadow_color = Color(0, 0, 0, 0.14)
	return sb
func _stylebox_button(bg: Color, border: Color) -> StyleBoxFlat:
	var sb: StyleBoxFlat = StyleBoxFlat.new()
	sb.bg_color = bg
	sb.border_width_left = 2
	sb.border_width_top = 2
	sb.border_width_right = 2
	sb.border_width_bottom = 2
	sb.border_color = border
	sb.corner_radius_top_left = 10
	sb.corner_radius_top_right = 10
	sb.corner_radius_bottom_right = 10
	sb.corner_radius_bottom_left = 10
	return sb
func _stylebox_modal() -> StyleBoxFlat:
	var sb: StyleBoxFlat = StyleBoxFlat.new()
	sb.bg_color = Color(0.966, 0.941, 0.871, 1)
	sb.border_width_left = 3
	sb.border_width_top = 3
	sb.border_width_right = 3
	sb.border_width_bottom = 3
	sb.border_color = Color(0.83, 0.64, 0.24, 1)
	sb.corner_radius_top_left = 12
	sb.corner_radius_top_right = 12
	sb.corner_radius_bottom_right = 12
	sb.corner_radius_bottom_left = 12
	sb.shadow_size = 12.0
	sb.shadow_color = Color(0, 0, 0, 0.35)
	return sb
func _populate_card(card: Panel, data: Dictionary) -> void:
	var vbox: VBoxContainer = card.get_node("VBox") as VBoxContainer
	var name_l: Label = vbox.get_node("NameRow/Name") as Label
	var leader_l: Label = vbox.get_node("NameRow/LeaderTag") as Label
	var weapon_l: Label = vbox.get_node("Weapon") as Label
	var hp_row: HBoxContainer = vbox.get_node("HPRow") as HBoxContainer
	var bar_wrap: Control = hp_row.get_node("BarWrap") as Control
	var bar_bg: ColorRect = bar_wrap.get_node("BarBg") as ColorRect
	var fill: ColorRect = bar_bg.get_node("Fill") as ColorRect
	var hp_l: Label = hp_row.get_node("HPText") as Label
	var state_row: HBoxContainer = vbox.get_node("StateRow") as HBoxContainer
	var accent: ColorRect = state_row.get_node("Accent") as ColorRect
	var state_l: Label = state_row.get_node("State") as Label
	var hp: float = float(data.get("hp", 0))
	var max_hp: float = float(data.get("max_hp", 0))
	var pct: float = 0.0
	if max_hp > 0:
		pct = clamp(hp / max_hp, 0.0, 1.0)
	var inner: float = bar_wrap.size.x - 4.0
	fill.size = Vector2(inner * pct, 12.0)
	fill.position = Vector2(2.0, 2.0)
	var state: String = str(data.get("state", "up"))
	if state != "up":
		fill.color = GREY
	else:
		fill.color = GOLD if data.get("leader", false) else Color(0.62, 0.52, 0.38, 1)
	if max_hp > 0:
		hp_l.text = "%d / %d hp" % [int(hp), int(max_hp)]
	else:
		hp_l.text = "- / - hp"
	name_l.text = str(data.get("name", "Member"))
	leader_l.visible = bool(data.get("leader", false))
	weapon_l.text = "%s - %s" % [str(data.get("klass", "")), str(data.get("weapon", ""))]
	match state:
		"up":
			state_l.text = "READY"
			accent.color = SAGE
		"down":
			state_l.text = "DOWN - death saving"
			accent.color = BLOOD
		"stable":
			state_l.text = "STABLE - no more rolls"
			accent.color = AMBER
		"out":
			state_l.text = "OUT - back at campfire"
			accent.color = GREY
		_:
			state_l.text = "-"
			accent.color = GREY
	card.modulate = Color(1, 1, 1, 0.55) if state == "out" else Color(1, 1, 1, 1)
func _populate_empty(card: Panel) -> void:
	var vbox: VBoxContainer = card.get_node("VBox") as VBoxContainer
	(vbox.get_node("NameRow/Name") as Label).text = "-"
	(vbox.get_node("Weapon") as Label).text = "empty slot"
	(vbox.get_node("HPRow/HPText") as Label).text = "- / - hp"
	(vbox.get_node("StateRow/State") as Label).text = "-"
	(vbox.get_node("StateRow/Accent") as ColorRect).color = GREY
	card.modulate = Color(1, 1, 1, 0.45)
func _select(idx: int) -> void:
	_selection = idx
	btn_short.add_theme_stylebox_override("normal", _sb_selected if _selection == 0 else _sb_normal)
	btn_short.add_theme_stylebox_override("hover", _sb_selected if _selection == 0 else _sb_normal)
	btn_long.add_theme_stylebox_override("normal", _sb_selected if _selection == 1 else _sb_normal)
	btn_long.add_theme_stylebox_override("hover", _sb_selected if _selection == 1 else _sb_normal)
func _on_short_pressed() -> void:
	_select(0)
func _on_long_pressed() -> void:
	_select(1)
func _on_commit_pressed() -> void:
	modal_body.text = _rest_summary()
	modal.show()
	get_tree().paused = true
func _rest_summary() -> String:
	if _selection == 0:
		return "Short rest - bind wounds and keep watch.\nThe company heals +25% max HP (clamped). The horde holds its pace."
	return "Long rest - sleep through the night.\nFull heal for all; the fallen return at 25% HP.\nOne member trains overnight (one free upgrade rank).\nBut the horde scales - the next stage hits harder."
func _on_rest_now() -> void:
	get_tree().paused = false
	modal.hide()
	rest_chosen.emit("short" if _selection == 0 else "long")
	hide()
func _on_change_mind() -> void:
	get_tree().paused = false
	modal.hide()
func _input(event: InputEvent) -> void:
	# human-test rounds 1-2: even as whole-card Buttons the SHORT/LONG
	# still wouldn't fire Button.pressed when clicked (the modal's Buttons
	# do), so belt-and-braces: _input always sees the raw mouse events and
	# repeats the click test in each Button's OWN space (get_local_mouse_position
	# is stretch/canvas-safe, exactly what Button uses to accept a click).
	if event is InputEventMouseButton:
		if not modal.visible and event.pressed and event.button_name == "left":
			if btn_short.rect_has_point(btn_short.get_local_mouse_position()):
				_select(0)
			if btn_long.rect_has_point(btn_long.get_local_mouse_position()):
				_select(1)
			if commit.rect_has_point(commit.get_local_mouse_position()):
				_on_commit_pressed()
		# releases need no routing; modal-area clicks use the Buttons' pressed
		# path, already confirmed working there
		return
	if not event.is_pressed():
		return
	if event is InputEventKey:
		if modal.visible:
			# human-test change: SPACE became the leader's dash mid-world, so
			# the campfire confirm moved to ENTER/RETURN only (clicks still
			# confirm: the card root keeps process_mode ALWAYS through the pause)
			if event.keycode == KEY_ESCAPE:
				_on_change_mind()
			elif event.keycode == KEY_ENTER:
				_on_rest_now()
			return
		match event.keycode:
			KEY_LEFT, KEY_A:
				_select(0)
			KEY_RIGHT, KEY_D:
				_select(1)
			# SPACE is the leader's dash now: the campfire confirm is ENTER/RETURN
			KEY_ENTER:
				_on_commit_pressed()
			KEY_ESCAPE:
				cancelled.emit()
				hide()
