extends SceneTree

# audit: palette + soft alpha + bbox for a mini PNG

func _init():
	var tex = ResourceLoader.load("res://art/mosiah.png", "Texture2D")
	if tex == null:
		print("[10thArt] LOAD FAILED")
		quit(1)
		return
	var img: Image = tex.get_image()
	var w = img.get_width()
	var h = img.get_height()
	print("[10thArt] size=", w, "x", h)
	var ur = {}
	var uo = {}
	var soft = 0
	var cov = 0
	var x0 = w
	var y0 = h
	var x1 = -1
	var y1 = -1
	for yy in h:
		for xx in w:
			var c = img.get_pixel(xx, yy)
			var r = int(round(clamp(c.r, 0.0, 1.0) * 255.0))
			var g = int(round(clamp(c.g, 0.0, 1.0) * 255.0))
			var b = int(round(clamp(c.b, 0.0, 1.0) * 255.0))
			var a = int(round(clamp(c.a, 0.0, 1.0) * 255.0))
			ur[str(r,",",g,",",b,",",a)] = true
			if a > 0:
				cov += 1
				if a < 255:
					soft += 1
				uo[str(r,",",g,",",b)] = true
				if xx < x0: x0 = xx
				if xx > x1: x1 = xx
				if yy < y0: y0 = yy
				if yy > y1: y1 = yy
	if x1 >= 0:
		print("[10thArt] bbox=", x1-x0+1, "x", y1-y0+1, " at ", x0, ",", y0)
		print("[10thArt] gaps: top=", y0, " bottom=", h-1-y1, " left=", x0, " right=", w-1-x1, " cx_delta=", (x0+x1)/2.0 - w/2.0)
	print("[10thArt] colors_rgba=", ur.size(), " opaque_colors=", uo.size())
	print("[10thArt] covered_px=", cov, " soft_alpha_px=", soft)
	quit(0)