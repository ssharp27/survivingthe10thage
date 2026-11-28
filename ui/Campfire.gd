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
# 2026-10-20 oracle: Array[Dictionary] made Game.cs' Set("party_data", ...) SILENTLY
# no-op (Godot.Collections.Array is an untyped variant; typed setter rejects it, no
# error line), so every run's campfire painted the _seed_demo() placeholders instead
# - the "Mosiah always DOWN / 0-120 hp" bug was the demo data. Untyped lands.
@export var party_data: Array = []
@export var stage_name: String = "Tanurial Forest"
# ROUND 19 (user hand-play: "it doesn't pause anymore"): Game.cs' ShowCampfire now
# arms GetTree().Paused for the whole camp beat, so Game._process freezes and its
# timeout branch can never tick while the camp is up. The pilot valve therefore
# rides the CARD, which keeps processing through the pause (PROCESS_MODE_ALWAYS).
@export var pilot_timeout: float = 0.0
@onready var grid: GridContainer = $Frame/Contents/RosterRow/PartyGrid
@onready var btn_short: Button = $Frame/Contents/RestRow/ShortCard/ShortRest
@onready var btn_long: Button = $Frame/Contents/RestRow/LongCard/LongRest
# ROUND 19 (user hand-play, seven complaints at once): the header block, the
# "choose your rest" label, the two rule chips, the hint line and the COMMIT TO
# THE EMBERS button are all GONE - a click on a rest card is the choice, and the
# popup just above the two cards is the gate. Only the popup refs stay.
@onready var popup: PanelContainer = $Frame/Contents/AskRow/Popup
@onready var popup_body: Label = $Frame/Contents/AskRow/Popup/PopupBody
var _selection: int = 0
var _cards: Array[Panel] = []
var _sb_normal: StyleBoxFlat
var _sb_selected: StyleBoxFlat
# the pilot clock: seconds the card has been up, counted even while the tree is
# paused, because this node is PROCESS_MODE_ALWAYS
var _open_t: float = -1.0
# ENTER echo guard: a held key fires repeated pressed events mid-hold, and a second
# answer would commit twice
var _enter_down: bool = false
# the roster column width, sized from the viewport on every paint pass
var _cell_w: float = 0.0
# The ask's own up/down state. A Panel in a flow row cannot hide() without lifting
# the rest cards, so the ask is gated by modulate.a instead - and alpha 0 does NOT
# stop a Control from taking clicks, so the ask is pinned non-pickable for good (see
# _popup_fade). This flag is what the click router asks, so keep it exactly in step
# with _popup_fade.
var _popup_on: bool = false
# the grid width the last paint measured, so a re-fit runs only when layout really
# moved (rebuilding five plates every frame is heavy)
var _grid_w: float = -1.0

func _ready() -> void:
	set_process_input(true)
	# ROUND 19: the CAMP itself pauses the tree now (Game.cs ShowCampfire arms
	# GetTree().Paused for the whole beat), so the card must stay live through it:
	# ALWAYS keeps this node (and the whole UI subtree below it, children inherit)
	# processing + taking mouse clicks during pause, and keeps this node's _input
	# firing (human-test bug: SPACE/click died the moment the rest paused the tree).
	# NOTE: 4.7 has NO Control.pause_picking property - assigning it throws and
	# aborts _ready, which half-builds the whole card. process_mode is the knob.
	set_process_mode(Node.PROCESS_MODE_ALWAYS)
	if party_data.size() == 0:
		_seed_demo()
	_sb_normal = _stylebox_button(Color(0.20, 0.18, 0.15, 0.94), Color(0.42, 0.34, 0.22, 1))
	_sb_selected = _stylebox_button(Color(0.28, 0.24, 0.19, 0.96), Color(0.83, 0.64, 0.24, 1))
	# ROUND 19 (user hand-play: the header block, the rule chips, the hint line and
	# the COMMIT TO THE EMBERS button are gone): nothing else repaints the ask, so it
	# owns its plate and body here. The ask is a flow slot just above the two rest
	# cards, never a modal, so its alpha is the whole gate: hide() would lift the rest
	# cards up 118 px the moment the question appeared, so the slot stays reserved and
	# only modulate.a changes (the ask is pinned non-pickable in _popup_fade, so an
	# invisible ask can never swallow or answer a click).
	popup.add_theme_stylebox_override("panel", _sb_popup())
	popup_body.autowrap_mode = 2 # enum slot: AUTOWRAP_WORD_SMART (4.7 rejects the bare name)
	popup_body.text = ""
	_popup_reset()
	_build()
	_select(0)
# ROUND 19: the ask is NOT free-standing. Clicking a rest card is the choice and the
# card just above the two rest cards asks; a SECOND click on that SAME card (or
# ENTER) commits, ESC steps back. The decision happens on mouse-DOWN (_pick_rest in
# _input), never on the Button's pressed event: deciding on release lets one click
# arm the ask and instantly read as the click that confirms it, so the question would
# appear and answer in the same motion. The Button signal stays only to restate the
# selection, and _ready's _select(0) above must not raise the ask - only _popup_ask
# ever does.
	btn_short.pressed.connect(_on_rest_pressed)
	btn_long.pressed.connect(_on_rest_pressed)
	_open_t = Time.get_unix_time_from_system() # the pilot clock counts THIS node through the tree pause
	# Bar widths depend on settled layout; refresh once the tree has laid out.
	call_deferred("_refresh_bars")
func _refresh_bars() -> void:
	# ROUND 19 (user hand-play: "it doesn't show the party members like it does on
	# the pause screen. It's still the old layout"): the roster used to be ONE 880 px
	# strip per member and the GridContainer still had columns = 1 with every strip
	# stretched vertically, so the party stacked as whole-frame bars instead of the
	# pause card's grid of plates. The plate width now rides the VIEWPORT exactly as
	# PauseMenu.gd measures its columns (clamped, so a 1280 window never loses the
	# click targets and a huge window never gets bloated plates) - the camp's own
	# frame is what bounds it, since the pause card's 1160 px shell does not exist
	# here. 5 plates, the tscn's 18 px h_separation, one grid row.
	# 2026-10-20 guard kept: before layout the measured width reads 0, and a 0 min
	# width collapsed the GridContainer columns to a sliver so every label painted
	# over the next card; the tscn's own 880 px band is the fallback.
		var gw: float = grid.size.x
		if gw <= 0.0:
			gw = 880.0
		if abs(gw - _grid_w) > 0.5:
			_grid_w = gw
			var want: float = (gw - 4.0 * 18.0) / 5.0
			if want > 300.0:
				want = 300.0 # a huge window never gets the old oversized plates
			if want < 170.0:
				want = 170.0 # the floor keeps the rest cards and plates usable in a small window
			_cell_w = want
			# one rebuild per real layout change: rebuilding five plates every frame is heavy
			_build()
		for i in range(_cards.size()):
			# user 2026-10-20: empty slots must not paint at all; visibility is re-derived
			# from party_data on every refresh, so a member arriving later shows up (the
			# card stays in the tree so grid positions never shift)
			# user 2026-11-02 (user): hide "away" (never recruited) cards TOO - Game.cs sends
			# the FULL roster with state "away" for unrecruited PCs, and even the dim "not
			# recruited yet" strips spoiled which characters existed to find mid-run. HIDE
			# (never remove) keeps grid slots fixed; re-deriving per refresh means a member
			# who joins at THIS camp shows up on the next paint.
			var shown: bool = i < party_data.size()
			if not shown:
				_cards[i].visible = false
				continue
			# 2026-10-20: full re-render per refresh, not just the bar: after a cancel Game.cs
			# re-Sets party_data on the SAME node (no _ready re-run), so stale labels would
			# linger if the party changed during the camp beat.
			_populate_card(_cards[i], party_data[i])
			_cards[i].visible = str(party_data[i].get("state", "")) != "away"
func _seed_demo() -> void:
	party_data = [
		{"name": "Baldrun", "klass": "Rune Knight", "weapon": "Maul", "hp": 58, "max_hp": 150, "state": "up", "leader": true},
		{"name": "Mosiah", "klass": "Bard", "weapon": "Rapier", "hp": 0, "max_hp": 120, "state": "down", "leader": false},
	]
func _build() -> void:
	for c in _cards:
		c.queue_free()
	_cards.clear()
	var slots: int = 5 # 2026-11-02: a run is only ever 1 leader + 4 party = 5 bodies, so the card keeps 5 slots — the FULL 9-entry roster still arrives from Game.cs, and unrecruited members ("away") are HIDDEN, not given slots
	for i in range(slots):
		var card: Panel = _make_card()
		grid.add_child(card)
		_cards.append(card)
		if i < party_data.size():
			_populate_card(card, party_data[i])
		else:
			_populate_empty(card)
func _make_card() -> Panel:
	# ROUND 19 (user hand-play: "it doesn't show the party members like it does on
	# the pause screen. It's still the old layout"): the old strip laid 8 nodes across
	# ONE HBox inside an 880 px panel - a name, a bar, a class line - which read as a
	# spreadsheet row, nothing like the pause card. Clone the pause card's anatomy
	# (PauseMenu.gd _paint_one): a PLATE per member with ONE VBox "Body" that STACKS
	# the rows (a PanelContainer lays its direct children in a ROW, which is why the
	# stack needs a body). The rows carry size_flags_horizontal = 3 and a HEIGHT-only
	# min size, so the only width in play is _cell_w on the plate and a hidden row
	# cannot collapse the column. The camp's own frame decides that width.
	# No painted mini and no IN-RUN STATS box: Game.cs CampCard() sends only name,
	# klass, weapon, hp, max_hp, state, leader and picks, so the pause card's TextureRect
	# nameplate has nothing to draw here and its stat line has no numbers to read.
		var card: Panel = Panel.new()
		card.name = "MemberCard"
		# plate height: 7 stacked rows (26+20+20+18+20+20+20) + 6 x 8 separation + the
		# plate's 12 px content margins top and bottom
		card.custom_minimum_size = Vector2(_cell_w, 216.0)
		card.add_theme_stylebox_override("panel", _stylebox_card())
		var body: VBoxContainer = VBoxContainer.new()
		body.name = "Body"
		body.add_theme_constant_override("separation", 8)
		card.add_child(body)
		var pad: float = _cell_w - 28.0
		# ---------- name ----------
		var name_l: Label = Label.new()
		name_l.name = "Name"
		name_l.custom_minimum_size = Vector2(pad, 26.0)
		name_l.size_flags_horizontal = 3
		name_l.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
		name_l.autowrap_mode = 2 # enum slot: AUTOWRAP_WORD_SMART (4.7 rejects the bare name)
		name_l.add_theme_color_override("font_color", PAPER)
		body.add_child(name_l)
		# ---------- leader flag (parked until Game.cs says this body leads) ----------
		var leader_l: Label = Label.new()
		leader_l.name = "LeaderTag"
		leader_l.text = "LEADER"
		leader_l.custom_minimum_size = Vector2(pad, 20.0)
		leader_l.size_flags_horizontal = 3
		leader_l.add_theme_color_override("font_color", AMBER)
		leader_l.scale = Vector2(0.85, 0.85)
		leader_l.visible = false
		body.add_child(leader_l)
		# ---------- class + weapon, the pause card's "logic" line ----------
		var logic_l: Label = Label.new()
		logic_l.name = "LogicTag"
		logic_l.custom_minimum_size = Vector2(pad, 20.0)
		logic_l.size_flags_horizontal = 3
		logic_l.autowrap_mode = 2
		logic_l.add_theme_color_override("font_color", AMBER)
		logic_l.scale = Vector2(0.85, 0.85)
		body.add_child(logic_l)
		# ---------- hp: the track is a plate, the fill rides it by anchors ----------
		# the old bar measured its own Control before layout, which drew an inverted
		# fill on the first human frame; anchors mean a 0-1 fraction of the track, so
		# nothing here needs a measured pixel width.
		var bar_wrap: PanelContainer = PanelContainer.new()
		bar_wrap.name = "BarWrap"
		bar_wrap.custom_minimum_size = Vector2(pad, 18.0)
		bar_wrap.size_flags_horizontal = 3
		bar_wrap.add_theme_stylebox_override("panel", _sb_hp_track())
		body.add_child(bar_wrap)
		var fill: ColorRect = ColorRect.new()
		fill.name = "Fill"
		fill.anchor_left = 0.0
		fill.anchor_top = 0.0
		fill.anchor_right = 0.0
		fill.anchor_bottom = 1.0
		fill.color = GOLD
		bar_wrap.add_child(fill)
		var hp_l: Label = Label.new()
		hp_l.name = "HPText"
		hp_l.custom_minimum_size = Vector2(pad, 20.0)
		hp_l.size_flags_horizontal = 3
		hp_l.add_theme_color_override("font_color", PAPER)
		body.add_child(hp_l)
		# ---------- condition ----------
		var state_l: Label = Label.new()
		state_l.name = "State"
		state_l.custom_minimum_size = Vector2(pad, 20.0)
		state_l.size_flags_horizontal = 3
		state_l.autowrap_mode = 2
		state_l.add_theme_color_override("font_color", BLOOD)
		state_l.scale = Vector2(0.85, 0.85)
		body.add_child(state_l)
		# ---------- picks ----------
		var picks_l: Label = Label.new()
		picks_l.name = "Picks"
		picks_l.custom_minimum_size = Vector2(pad, 20.0)
		picks_l.size_flags_horizontal = 3
		picks_l.autowrap_mode = 2
		picks_l.add_theme_color_override("font_color", AMBER)
		picks_l.scale = Vector2(0.85, 0.85)
		body.add_child(picks_l)
		return card
func _stylebox_card() -> StyleBoxFlat:
	# ROUND 19: the pause card's column plate (PauseMenu.gd _sb_column) is what the
	# player means by "like it does on the pause screen". The old camp plate had 10 px
	# radii and a 4 px shadow, which made every strip read as its own floating card;
	# these are the same 2 px rim and 8 px radius the pause columns actually wear, and
	# the 14/12 px content margins that give the rows their air.
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

func _sb_hp_track() -> StyleBoxFlat:
	# the hp bar's track: the plate the anchor-scaled fill rides. Same box language as
	# the pause card's mode chips (_sb_mode), so the plate and its chips read as one
	# material.
		var sb: StyleBoxFlat = StyleBoxFlat.new()
		sb.bg_color = Color(0.20, 0.18, 0.15, 0.9)
		sb.border_width_left = 1
		sb.border_width_top = 1
		sb.border_width_right = 1
		sb.border_width_bottom = 1
		sb.border_color = Color(0.52, 0.44, 0.33, 1)
		sb.corner_radius_top_left = 4
		sb.corner_radius_top_right = 4
		sb.corner_radius_bottom_right = 4
		sb.corner_radius_bottom_left = 4
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
func _sb_popup() -> StyleBoxFlat:
	var sb: StyleBoxFlat = StyleBoxFlat.new()
	sb.bg_color = Color(0.16, 0.14, 0.12, 0.97)
	sb.border_width_left = 2
	sb.border_width_top = 2
	sb.border_width_right = 2
	sb.border_width_bottom = 2
	sb.border_color = Color(0.83, 0.64, 0.24, 1)
	sb.corner_radius_top_left = 6
	sb.corner_radius_top_right = 6
	sb.corner_radius_bottom_right = 6
	sb.corner_radius_bottom_left = 6
	sb.shadow_size = 8.0
	sb.shadow_color = Color(0, 0, 0, 0.4)
	return sb
func _populate_card(card: Panel, data: Dictionary) -> void:
	# ROUND 19: the rows live in the plate's VBox Body, so every lookup path follows
	# the new anatomy (the old Row/HBox one is gone).
		var body: VBoxContainer = card.get_node("Body") as VBoxContainer
		var name_l: Label = body.get_node("Name") as Label
		var leader_l: Label = body.get_node("LeaderTag") as Label
		var logic_l: Label = body.get_node("LogicTag") as Label
		var bar_wrap: PanelContainer = body.get_node("BarWrap") as PanelContainer
		var fill: ColorRect = bar_wrap.get_node("Fill") as ColorRect
		var hp_l: Label = body.get_node("HPText") as Label
		var state_l: Label = body.get_node("State") as Label
		var picks_l: Label = body.get_node("Picks") as Label
		var hp: float = float(data.get("hp", 0))
		var max_hp: float = float(data.get("max_hp", 0))
		var pct: float = 0.0
		if max_hp > 0:
			pct = clamp(hp / max_hp, 0.0, 1.0)
		# the fill rides the track by anchors: anchor_right is a 0-1 fraction of it, and
		# the 0.03 floor is what keeps a DOWN body from reading as a hairline
		fill.anchor_right = clamp(0.03 + 0.97 * pct, 0.0, 1.0)
		var state: String = str(data.get("state", "up"))
		if state != "up":
			fill.color = GREY
		elif bool(data.get("leader", false)):
			fill.color = GOLD
		else:
			fill.color = Color(0.62, 0.52, 0.38, 1)
		if max_hp > 0:
			hp_l.text = "%d / %d hp" % [int(hp), int(max_hp)]
		else:
			hp_l.text = "- / - hp"
		name_l.text = str(data.get("name", "Member"))
		leader_l.visible = bool(data.get("leader", false))
		logic_l.text = "%s - %s" % [str(data.get("klass", "")), str(data.get("weapon", ""))]
		picks_l.text = str(data.get("picks", "no picks yet"))
		match state:
			"up":
				state_l.text = "READY"
				state_l.add_theme_color_override("font_color", SAGE)
			"down":
				state_l.text = "DOWN - death saving"
				state_l.add_theme_color_override("font_color", BLOOD)
			"stable":
				state_l.text = "STABLE - no more rolls"
				state_l.add_theme_color_override("font_color", AMBER)
			"out":
				state_l.text = "OUT - back at campfire"
				state_l.add_theme_color_override("font_color", GREY)
			"away":
				state_l.text = "not recruited yet"
				state_l.add_theme_color_override("font_color", GREY)
			_:
				state_l.text = "-"
				state_l.add_theme_color_override("font_color", GREY)
		# 2026-10-24: away (never recruited) reads dimmer than an out-at-camp body
		if state == "out":
			card.modulate = Color(1, 1, 1, 0.55)
		elif state == "away":
			card.modulate = Color(1, 1, 1, 0.45)
		else:
			card.modulate = Color(1, 1, 1, 1)
func _populate_empty(card: Panel) -> void:
	var body: VBoxContainer = card.get_node("Body") as VBoxContainer
	(body.get_node("Name") as Label).text = "-"
	(body.get_node("HPText") as Label).text = "- / - hp"
	(body.get_node("State") as Label).text = "-"
	(body.get_node("LogicTag") as Label).text = ""
	(body.get_node("Picks") as Label).text = ""
	(body.get_node("BarWrap/Fill") as ColorRect).anchor_right = 0.0
	(body.get_node("LeaderTag") as Label).visible = false
	# the slot is hidden anyway (visibility is re-derived from party_data), but keep
	# the paint honest so a slot that becomes real later never inherits a stale row
	card.modulate = Color(1, 1, 1, 0.45)
func _select(idx: int) -> void:
	_selection = idx
	btn_short.add_theme_stylebox_override("normal", _sb_selected if _selection == 0 else _sb_normal)
	btn_short.add_theme_stylebox_override("hover", _sb_selected if _selection == 0 else _sb_normal)
	btn_long.add_theme_stylebox_override("normal", _sb_selected if _selection == 1 else _sb_normal)
	btn_long.add_theme_stylebox_override("hover", _sb_selected if _selection == 1 else _sb_normal)
	# the ask sits in its own flow row directly above the two rest cards, so it
	# restates the standing choice; only _popup_ask ever makes it visible.
	if popup_body:
		popup_body.text = _ask_text()
# ROUND 18 copy law (verbatim): the popup reads "Are you sure you want to X?"
# and nothing else - the rule chips and the hint line the player never asked for
# were deleted from the card, so this single line is the whole ask.
func _ask_text() -> String:
	if _selection == 0:
		return "Are you sure you want to short rest?"
	return "Are you sure you want to long rest?"
# ROUND 19 popup law: the ask is a flow slot, NOT a modal. Containers skip hidden
# children, so hide()-ing the ask lifted the rest cards up 118 px the moment the
# question appeared (human-test: the cards jumped on every confirm). The slot stays
# reserved and only alpha moves; alpha 0 alone does not stop a Control taking clicks,
# so the ask is pinned non-pickable on every fade step. NOTE the two dialects: the
# scene writes mouse_passable = false (the .tscn spelling 4.7 accepts), while the GD
# property is mouse_filter, whose values are Stop, Pass (Propagate Up), Ignore - 2 is
# Ignore, so an invisible ask can never swallow or answer a click. _popup_on keeps the
# router's view exactly in step with the fade.
func _popup_fade(a: float) -> void:
	popup.modulate = Color(1, 1, 1, a)
	popup.mouse_filter = 2 # enum slot: MOUSE_FILTER_IGNORE (4.7 rejects the bare name)
	_popup_on = a > 0.0
func _popup_reset() -> void:
	_popup_fade(0.0)
func _popup_hide() -> void:
	_popup_fade(0.0)
func _popup_ask() -> void:
	_popup_fade(1.0)
func _commit() -> void:
	# the ask is the last gate: answering it IS the decision. Nothing here touches
	# the tree pause - Game.cs clears GetTree().Paused in both handlers, so the camp
	# cannot leave the world frozen if the answer lands twice or arrives mid-hold.
	_popup_hide()
	rest_chosen.emit("short" if _selection == 0 else "long")
	hide()
func _pick_rest(idx: int) -> void:
	# the choice happens on mouse-DOWN, never on Button.pressed (release): deciding
	# on release lets ONE click arm the ask and read as the click that answers it,
	# so the question appeared and answered in the same motion (human-test round 5,
	# "the rest cards felt dead"). A second click on the SAME card while the ask is
	# up is the answer; a click on the other card restates the choice and re-arms it.
	if _popup_on and idx == _selection:
		_commit()
		return
	_select(idx)
	_popup_ask()
func _on_rest_pressed() -> void:
	# btn_short/btn_long.pressed only restates the standing choice: it must NEVER
	# arm the ask, or the release half of one click would answer the ask the press
	# half just opened. A hidden card still emits pressed signals (hide() does not
	# silence its Buttons), so restyling invisible nodes is all this can do.
	_select(_selection)
# ROUND 19 pilot valve lives ON THE CARD: Game.cs arms GetTree().Paused for the
# whole camp beat, which freezes Game._process, so C#'s timeout branch can never
# tick while the camp is up. This node is PROCESS_MODE_ALWAYS (set in _ready), so
# its own clock is the only one that can resolve a camp nobody answers - and C#
# only arms pilot_timeout for the pilot run, so a hand-played camp never times out.
func _process(_delta: float) -> void:
	if not visible:
		# a closed camp owns no clock; re-arming on the next Show() keeps the pilot
		# timeout honest for a camp opened a second time after a cancel
		_open_t = -1.0
		return
	if pilot_timeout <= 0.0:
		return
	if _open_t < 0.0:
		_open_t = Time.get_unix_time_from_system()
		return
	if Time.get_unix_time_from_system() - _open_t < pilot_timeout:
		return
	_commit()
func _was_click_on(b: Button, ev: InputEvent) -> bool:
	# 4.7 keeps dropping legacy hit-test helpers (rect_has_point and the event
	# button_name both gone) and round 4's global-rect test still left the rest
	# cards inert, so accept a click if ANY space agrees the cursor is over the
	# button: Control-space mouse vs global rect, raw event position vs global
	# rect (window == base viewport on the default 1280x720 run), or the button's
	# own local rect vs its local mouse.
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
func _input(event: InputEvent) -> void:
	# ROUND 19: the card has no header, no rule chips, no hint line and no COMMIT
	# button left, so the router has exactly two jobs - a left click on a rest card
	# IS the choice, and the ask above the cards gates the commit. Kept from the
	# earlier passes: 4.7 lost button_name and rect_has_point, the card root routes
	# raw mouse events because whole-card Buttons stayed inert when clicked, and a
	# hidden Control still receives _input with a rect that still answers (human-test
	# pass-9: a stray click after the rest re-ran EndCampfire behind the camp and
	# left the tree paused with nothing on screen), so the visible guard is the last
	# line of defence.
	if not visible:
		return
	# the ENTER echo guard first: a held key repeats pressed events mid-hold, and the
	# pressed() filter below drops the release, so one press must answer at most once
	if event is InputEventKey and event.keycode == KEY_ENTER and not event.pressed:
		_enter_down = false
		return
	if event is InputEventMouseButton:
		if event.pressed and event.button_index == 1:  # MOUSE_BUTTON_LEFT (4.7 has no button_name)
			if _was_click_on(btn_short, event):
				_pick_rest(0)
			elif _was_click_on(btn_long, event):
				_pick_rest(1)
		# releases need no routing, and there is nothing else to click now that the
		# COMMIT button is gone
		return
	if not event.is_pressed():
		return
	if event is InputEventKey:
		if _popup_on:
			# the ask is up: ESC only steps back (the question closes, the standing
			# choice stays), ENTER answers it. SPACE is the leader's dash mid-world and
			# must never answer here.
			if event.keycode == KEY_ESCAPE:
				_popup_hide()
			elif event.keycode == KEY_ENTER:
				if not _enter_down:
					_enter_down = true
					_commit()
			return
	match event.keycode:
		KEY_LEFT, KEY_A:
			_select(0)
		KEY_RIGHT, KEY_D:
			_select(1)
		KEY_ENTER:
			# no ask yet: ENTER opens the question for the standing choice - it must
			# not answer its own question, the popup is the gate the player sees
			if not _enter_down:
				_enter_down = true
				_popup_ask()
		KEY_ESCAPE:
			cancelled.emit()
			hide()

