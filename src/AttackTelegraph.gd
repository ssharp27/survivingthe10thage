extends Node2D

# Greybox attack telegraph: kind 0 = maul swing sector, kind 1 = rapier stab line.
# Driven by PartyMember.cs via show_attack(); clears itself when vis runs out.

var radius = 0.0
var arc_half = 1.0
var facing = 0.0
var kind = 0
var vis = 0.0

func _process(d):
	if vis > 0.0:
		vis -= d

func show_attack(r, ah, f, k):
	radius = r
	arc_half = ah
	facing = f
	kind = k
	vis = 0.18

func _draw():
	if vis <= 0.0:
		return
	if kind == 0:
		var pts = PackedVector2Array([Vector2.ZERO])
		var cols = PackedColorArray()
		for i in range(13):
			var a = facing - arc_half + 2.0 * arc_half * i / 12.0
			pts.append(Vector2(cos(a), sin(a)) * radius)
		for _p in pts:
			cols.append(Color(1.0, 0.55, 0.2, 0.45))
		draw_polygon(pts, cols)
	else:
		draw_line(Vector2.ZERO, Vector2(cos(facing), sin(facing)) * radius, Color(0.5, 1.0, 1.0, 0.55), 7.0)
