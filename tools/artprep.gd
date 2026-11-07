# ART PREP - promoted converter: generated JPG/PNG -> trimmed + matte-backed RGBA PNG.
# Edit the JOBS list in _init() ([src, out, box_w, box_h] rows) after each art batch lands, then run:
#   "<Godot 4.7 ..._console.exe>" --path C:\GitHub\survivingthe10thage --headless --script res://tools/artprep.gd
# (always the _console exe; poll stdout for BEGIN/DONE/MISS lines - GDScript CANNOT see a running game prints).
# 4.7 GDScript API renames, empirically settled on this engine - do NOT re-derive:
#   - entrypoint is func _init() (a bare main() is NEVER called - that dead entrypoint caused every earlier "hung" run)
#   - Image.load_from_file (Image.load() does not exist)
#   - JPEG decodes at bpp 3: compute bpp = data.size() / (w * h), never assume 4
#   - no get_image_format; no get_sub_image (crop by sampling pixels instead)
#   - Image.create(w, h, false, 5) is 4-arg in 4.7 - the 5-arg test form fails; format int 5 = RGBA
#   - FileAccess write_text(path, 3) takes the NUMERIC mode in this binding
#   - no ++ / -- operators
# SOURCE LAW (read this before filing a bug): generated sources MUST be flat white to EVERY canvas
# edge. The trim and the matte are both seeded from the four CORNER COLOURS, so a letterboxed /
# black-barred or vignetted source poisons the whole run - black corners mean the white field is
# never eaten and the saved PNG comes back opaque with bars in it. If a DONE row shows the source
# pixel size instead of a trimmed one, the source background is the bug, not this tool.
# Law: edge-trim background matching the four corners (tolerance 31/255), nearest-neighbour
# scale into the box, flood-fill matte near-background blobs connected to ALL four corners
# (tolerance 0.12) to transparent, save PNG. Sources of truth: generated JPGs stay UNTIL conversion lands;
# delete them after (committed outputs are the PNGs + .import sidecars).

extends SceneTree

func prep(sp: String, op: String, bw: int, bh: int) -> void:
	var img: Image = Image.load_from_file(sp)
	if img == null:
		prints("MISS", sp)
		return
	img.decompress()
	var w: int = img.get_width()
	var h: int = img.get_height()
	prints("BEGIN", sp, w, h)
	var data: PackedByteArray = img.get_data()
	var bpp: int = data.size() / (w * h)
	var cw: int = (w - 1) * bpp
	var bot: int = (h - 1) * w * bpp
	var corners: Array = [[data[0], data[1], data[2]], [data[cw], data[cw + 1], data[cw + 2]], [data[bot], data[bot + 1], data[bot + 2]], [data[bot + cw], data[bot + cw + 1], data[bot + cw + 2]]]
	var minx: int = 0
	var miny: int = 0
	var maxx: int = w - 1
	var maxy: int = h - 1
	while minx < maxx and _col_back(data, w, h, minx, corners, bpp):
		minx += 1
	while maxx > minx and _col_back(data, w, h, maxx, corners, bpp):
		maxx -= 1
	while miny < maxy and _row_back(data, w, h, miny, corners, bpp):
		miny += 1
	while maxy > miny and _row_back(data, w, h, maxy, corners, bpp):
		maxy -= 1
	var aw: int = maxx - minx + 1
	var ah: int = maxy - miny + 1
	var sc: float = min(float(bw) / float(aw), float(bh) / float(ah))
	var aw2: int = maxi(1, roundi(aw * sc))
	var ah2: int = maxi(1, roundi(ah * sc))
	var conv: Image = Image.create(aw2, ah2, false, 5)
	for yy in range(ah2):
		var ty: int = miny + int(ah * (yy + 0.5) / ah2)
		for xx in range(aw2):
			var tx: int = minx + int(aw * (xx + 0.5) / aw2)
			conv.set_pixel(xx, yy, img.get_pixel(tx, ty))
	_matte_small(conv)
	if conv.save_png(op) != OK:
		prints("SAVE", op)
		return
	prints("DONE", sp, op, aw, ah)

func _matte_small(img: Image) -> void:
	var w: int = img.get_width()
	var h: int = img.get_height()
	var seen: PackedByteArray = []
	seen.resize(w * h)
	_fill(img, seen, w, h, 0, 0)
	_fill(img, seen, w, h, w - 1, 0)
	_fill(img, seen, w, h, 0, h - 1)
	_fill(img, seen, w, h, w - 1, h - 1)

func _fill(img: Image, seen: PackedByteArray, w: int, h: int, cx: int, cy: int) -> void:
	var start: int = cx + cy * w
	if seen[start] == 1:
		return
	var ref: Color = img.get_pixel(cx, cy)
	if ref.a < 0.5:
		return
	var stack: PackedInt32Array = [start]
	seen[start] = 1
	while stack.size() > 0:
		var pos: int = stack[stack.size() - 1]
		stack.resize(stack.size() - 1)
		var px: int = pos % w
		var py: int = pos / w
		img.set_pixel(px, py, Color(0, 0, 0, 0))
		if px > 0 and seen[pos - 1] == 0 and _like(img, px - 1, py, ref):
			seen[pos - 1] = 1
			stack.append(pos - 1)
		if px + 1 < w and seen[pos + 1] == 0 and _like(img, px + 1, py, ref):
			seen[pos + 1] = 1
			stack.append(pos + 1)
		if py > 0 and seen[pos - w] == 0 and _like(img, px, py - 1, ref):
			seen[pos - w] = 1
			stack.append(pos - w)
		if py + 1 < h and seen[pos + w] == 0 and _like(img, px, py + 1, ref):
			seen[pos + w] = 1
			stack.append(pos + w)

func _like(img: Image, x: int, y: int, ref: Color) -> bool:
	var q: Color = img.get_pixel(x, y)
	return q.a > 0.5 and abs(q.r - ref.r) < 0.12 and abs(q.g - ref.g) < 0.12 and abs(q.b - ref.b) < 0.12


func _match(data: PackedByteArray, ai: int, corners: Array) -> bool:
	for c in corners:
		if abs(data[ai] - c[0]) < 31 and abs(data[ai + 1] - c[1]) < 31 and abs(data[ai + 2] - c[2]) < 31:
			return true
	return false

func _col_back(data: PackedByteArray, w: int, h: int, x: int, corners: Array, bpp: int) -> bool:
	var ai: int = x * bpp
	for y in range(h):
		if not _match(data, ai, corners):
			return false
		ai += w * bpp
	return true

func _row_back(data: PackedByteArray, w: int, h: int, y: int, corners: Array, bpp: int) -> bool:
	var ai: int = y * w * bpp
	for x in range(w):
		if not _match(data, ai, corners):
			return false
		ai += bpp
	return true

func _init() -> void:
	# JOBS = one row per generated sprite: [src, out, box_w, box_h]. EDIT THIS LIST each batch.
	# Shipped re-roll batch (2026-11-08, sources DELETED after conversion, outputs committed:
	#   bee_v2 48x48 box -> 48x41 | mage_fireball_v3 40x40 -> 40x15 | potion 40x40 -> 28x40):
	#   var jobs: Array = [
	#       ["res://art/fx/bee_v2.jpg", "res://art/fx/bee.png", 48, 48],
	#       ["res://art/fx/mage_fireball_v3.jpg", "res://art/fx/mage_fireball.png", 40, 40],
	#       ["res://art/pickups/potion.jpg", "res://art/pickups/potion.png", 40, 40],
	#   ]
	# Shipped shield batch (2026-11-08: v2/v3 letterboxed - black bars to the corners - rejected
	#   per SOURCE LAW, v4 painterly too muddy at game scale, v5 chunky 32-px-style roll used;
	#   shield_v5 48x48 box -> 27x48; user then hand-fixed the PNG at the same 27x48 canvas;
	#   Main.tscn shieldFxArt + PartyMember.cs ShieldArt flight untouched, zero code change):
	#   var jobs: Array = [
	#       ["res://art/fx/shield_v5.jpg", "res://art/fx/shield.png", 48, 48],
	#   ]
	# Shipped handaxe batch (2026-11-08: axe_v1 chunky handaxe 32x32 box -> 13x32; user hand-edited
	#   the PNG at the same 13x32 canvas (colours/bits only); axeFxArt untouched in both lanes):
	#   var jobs: Array = [
	#       ["res://art/fx/axe_v1.jpg", "res://art/fx/axe.png", 32, 32],
	#   ]
	# Shipped dagger batch (2026-11-08: dagger_v1 thrust MOTION wedge + tip star, NO blade;
	#   64x16 box rejected on the spot - 20x shrink of the 838x330 source mashed all detail (user:
	#   "condensed down it loses all detail. Any way we can keep it?"), re-converted SAME source
	#   at 128x32 box -> 81x32 (Y is the detail carrier: PoseStabFx keeps authored height,
	#   X-stretches len/texW); user then hand-fixed pixels at the same 81x32 canvas):
	#   var jobs: Array = [
	#       ["res://art/fx/dagger_v1.jpg", "res://art/fx/dagger.png", 128, 32],
	#   ]
	# Shipped sweep+lunge batch (2026-11-08: sweep_v1 slab + v2 jagged fan rejected by user;
	#   sweep_v3 crescent SLASH BAND (maul edge swept over an area, not a fan) 128x128 box ->
	#   102x128 (band bbox near-square so texture center = pivot, rim reaches full reach, NO
	#   vertex-pad needed that round); lunge_v1 letterboxed SOURCE LAW reject, v3 diagonal reject,
	#   v4 approved AFTER a component pass cleared the 12-px tail-dash chunk (on-axis spark kept)
	#   then 80x16 cropped to 80x12; user hand-fixed both PNGs at the same canvases):
	#   var jobs: Array = [
	#       ["res://art/fx/sweep_v3.jpg", "res://art/fx/sweep.png", 128, 128],
	#       ["res://art/fx/lunge_v4.jpg", "res://art/fx/lunge.png", 96, 16],
	#   ]
	# Current batch (2026-11-08 Stack chef split art: firebolt_v3 head-trailing + hollow-core
	# reject, firebolt_v4 SOLID-core comet head-forward 64x32 box; vial_v1 upright corked
	# hot-sauce bottle 24x36 box -> art/fx/vial.png rides the throw rig):
	#   var jobs: Array = [
	#       ["res://art/fx/firebolt_v4.jpg", "res://art/fx/firebolt.png", 64, 32],
	#       ["res://art/fx/vial_v1.jpg", "res://art/fx/vial.png", 24, 36],
	#   ]
	# No live batch - edit THIS list for the next one.
	var jobs: Array = [
		["res://art/fx/firebolt_v4.jpg", "res://art/fx/firebolt.png", 64, 32],
		["res://art/fx/vial_v1.jpg", "res://art/fx/vial.png", 24, 36],
	]
	for j in jobs:
		prep(String(j[0]), String(j[1]), int(j[2]), int(j[3]))
	quit()
