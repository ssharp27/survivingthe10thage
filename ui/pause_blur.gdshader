shader_type canvas_item;

// Pause-card backdrop (user 2026-11-05: "Maybe we make the game fade/blur in the
// background so it shows that it's paused?"). A ColorRect carrying this shader
// samples the SCREEN behind it, so the arena reads soft + dim while the card is
// up and snaps back the moment the card closes.
//
// strength: 0 = pure dim, 1 = full softening. radius: blur radius in fractions of
// the screen (Godot bokeh-lite: a small poisson sample ring, cheap enough to hold
// a full-screen blur at the pause card without any post pipeline).

uniform float strength: hint_range(0.0, 1.0) = 0.28; // 2026-11-12 wash softened; 2026-11-13 nudge softer again (user: "like the background is unfocused") - assistant pick, retunable
uniform float radius: hint_range(0.0, 0.05) = 0.006; // 2026-11-13: a wider gaussian defocus replaces the tight equal-weight ring that read HARRSH (the 0.0035 ring doubled edges harsh)
// 4.x removed the 3.x screen_texture builtin; the hint belongs on a GLOBAL uniform
// (seat 547 SHADER ERROR "Invalid variable type (samplers are not allowed)" - samplers
// are not legal as local variables)
uniform sampler2D screen_texture: hint_screen_texture;

void fragment() {
	// the raw screen behind this rect, then a ring of samples around it
	// 4.7 rejects the float->vec2 splat the older drafts relied on: build the vec2
	// explicitly (the seat 545 SHADER ERROR "Invalid assignment of 'float' to 'vec2'")
	vec2 r1 = vec2(radius, radius);
	vec2 r2 = r1 * 2.0;
	// the raw screen behind this rect, then a CENTRE-WEIGHTED TWO-ring gaussian:
	// the old single 9-tap EQUAL-weight ring doubled edges harsh (user 2026-11-13: "a
	// soft blur ... like the background is unfocused"), so the centre carries 4, an inner
	// ring 8x2, an outer ring 8x1 - a gentle out-of-focus defocus, not a hard ghost.
	vec2 uv = SCREEN_UV;
	vec4 soft = texture(screen_texture, uv) * 4.0;
	// inner ring, weight 2 each (8 taps)
	soft += texture(screen_texture, uv + vec2(r1.x, 0.0)) * 2.0;
	soft += texture(screen_texture, uv + vec2(-r1.x, 0.0)) * 2.0;
	soft += texture(screen_texture, uv + vec2(0.0, r1.y)) * 2.0;
	soft += texture(screen_texture, uv + vec2(0.0, -r1.y)) * 2.0;
	soft += texture(screen_texture, uv + r1) * 2.0;
	soft += texture(screen_texture, uv - r1) * 2.0;
	soft += texture(screen_texture, uv + vec2(r1.x, -r1.y)) * 2.0;
	soft += texture(screen_texture, uv + vec2(-r1.x, r1.y)) * 2.0;
	// outer ring, weight 1 each (8 taps): widens the falloff so the blur reads smooth, not boxed
	soft += texture(screen_texture, uv + vec2(r2.x, 0.0));
	soft += texture(screen_texture, uv + vec2(-r2.x, 0.0));
	soft += texture(screen_texture, uv + vec2(0.0, r2.y));
	soft += texture(screen_texture, uv + vec2(0.0, -r2.y));
	soft += texture(screen_texture, uv + r2);
	soft += texture(screen_texture, uv - r2);
	soft += texture(screen_texture, uv + vec2(r2.x, -r2.y));
	soft += texture(screen_texture, uv + vec2(-r2.x, r2.y));
	soft /= 4.0 + 16.0 + 8.0; // weight sum = 28
	// the dim wash: the field stays readable, it just reads as paused
	COLOR = mix(soft, soft * vec4(0.62, 0.58, 0.52, 1.0), strength);
	COLOR.a = 1.0;
}
