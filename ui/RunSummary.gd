extends CanvasLayer

class_name RunSummaryScreen

signal dismissed

# ---------- grimoire palette ----------
var INK = Color(0.21, 0.17, 0.12, 1)
var MUTED = Color(0.52, 0.44, 0.33, 1)
var GOLD = Color(0.83, 0.64, 0.24, 1)
var SAGE = Color(0.44, 0.58, 0.40, 1)
var BLOOD = Color(0.70, 0.26, 0.26, 1)
var AMBER = Color(0.82, 0.58, 0.20, 1)
var GREY = Color(0.62, 0.62, 0.62, 1)

# ---------- data bound by Game.cs at integration time (see ui/INTEGRATION.md) ----------
@export var result: String = "victory"      # "victory" | "tpk"
@export var stage_name: String = "Tanurial Forest"
@export var level: int = 7
@export var xp_total: int = 130
@export var members: Array[Dictionary] = []  # {name, state, picks}

@onready var banner: Label = $Frame/Contents/Banner
@onready var stat_stage_value: Label = $Frame/Contents/StatsRow/StageStat/Value
@onready var stat_level_value: Label = $Frame/Contents/StatsRow/LevelStat/Value
@onready var stat_xp_value: Label = $Frame/Contents/StatsRow/XPStat/Value
@onready var roster: VBoxContainer = $Frame/Contents/Roster
@onready var btn_journal: Button = $Frame/Contents/Footer/Journal
@onready var btn_menu: Button = $Frame/Contents/Footer/Menu

var _rows: Array[Control] = []

func _ready() -> void:
	set_process_input(true)
	if members.size() == 0:
		_seed_demo()
	_apply_demo()
	_build()
	get_tree().paused = true

	btn_journal.pressed.connect(_on_journal)
	btn_menu.pressed.connect(_on_menu)

func _seed_demo() -> void:
	members = [
		{"name": "Baldrun", "state": "survived", "picks": "Pick history: Maul dmg +25% x2"},
		{"name": "Mosiah", "state": "fallen", "picks": "Pick history: Rapier dmg +25% x1"},
	]

func _apply_demo() -> void:
	var victory: bool = result == "victory"
	banner.text = "VICTORY - Tanurial Forest cleared" if victory else "TPK - the whole party is down"
	banner.add_theme_color_override("font_color", GOLD if victory else BLOOD)
	banner.scale = Vector2(1.15, 1.15)
	stat_stage_value.text = stage_name
	stat_level_value.text = "%d" % [level]
	stat_xp_value.text = "%d" % [xp_total]

func _build() -> void:
	for c in _rows:
		c.queue_free()
	_rows.clear()
	if members.size() == 0:
		var row: Control = Control.new()
		row.custom_minimum_size = Vector2(0.0, 30.0)
		var lb: Label = Label.new()
		lb.text = "No pick history tracked yet - greybox placeholder."
		lb.add_theme_color_override("font_color", MUTED)
		lb.scale = Vector2(0.9, 0.9)
		row.add_child(lb)
		roster.add_child(row)
		_rows.append(row)
		return
	for m in members:
		var row: HBoxContainer = HBoxContainer.new()
		row.add_theme_constant_override("separation", 14)
		var name_l: Label = Label.new()
		name_l.text = str(m.get("name", "Member"))
		name_l.add_theme_color_override("font_color", INK)
		var state_l: Label = Label.new()
		var state: String = str(m.get("state", "survived"))
		if state == "survived":
			state_l.text = "Survived"
			state_l.add_theme_color_override("font_color", SAGE)
		elif state == "downed":
			state_l.text = "Downed"
			state_l.add_theme_color_override("font_color", BLOOD)
		elif state == "stable":
			state_l.text = "Stable"
			state_l.add_theme_color_override("font_color", AMBER)
		else:
			state_l.text = "Fallen - returns at campfire"
			state_l.add_theme_color_override("font_color", GREY)
		var picks_l: Label = Label.new()
		picks_l.text = str(m.get("picks", "-"))
		picks_l.add_theme_color_override("font_color", MUTED)
		picks_l.scale = Vector2(0.85, 0.85)
		picks_l.size_flags_horizontal = 3
		row.add_child(name_l)
		row.add_child(state_l)
		row.add_child(picks_l)
		roster.add_child(row)
		_rows.append(row)

func _on_journal() -> void:
	_dismiss()

func _on_menu() -> void:
	_dismiss()

func _dismiss() -> void:
	get_tree().paused = false
	dismissed.emit()
	hide()

func _input(event: InputEvent) -> void:
	if event is InputEventKey and event.is_pressed() and event.keycode == KEY_ESCAPE:
		_dismiss()
