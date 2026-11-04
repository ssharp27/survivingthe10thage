shader_type canvas_item;

// Pause-card backdrop (user 2026-11-05: "Maybe we make the game fade/blur in the
// background so it shows that it's paused?"). A ColorRect carrying this shader
// samples the SCREEN behind it, so the arena reads soft + dim while the card is
// up and snaps back the moment the card closes.
//
// strength: 0 = pure dim, 1 = full softening. radius: blur radius in fractions of
// the screen (Godot bokeh-lite: a small poisson sample ring, cheap enough to hold
// a full-screen blur at the pause card without any post pipeline).

uniform float strength: hint_range(0.0, 1.0) = 0.55;
uniform float radius: hint_range(0.0, 0.05) = 0.008;
// 4.x removed the 3.x screen_texture builtin; the hint belongs on a GLOBAL uniform
// (seat 547 SHADER ERROR "Invalid variable type (samplers are not allowed)" - samplers
// are not legal as local variables)
uniform sampler2D screen_texture: hint_screen_texture;

void fragment() {
	// the raw screen behind this rect, then a ring of samples around it
	// 4.7 rejects the float->vec2 splat the older drafts relied on: build the vec2
	// explicitly (the seat 545 SHADER ERROR "Invalid assignment of 'float' to 'vec2'")
	vec2 px = vec2(radius, radius);
	vec2 uv = SCREEN_UV;
	vec4 soft = texture(screen_texture, uv);
	soft += texture(screen_texture, uv + vec2(px.x, 0.0));
	soft += texture(screen_texture, uv + vec2(-px.x, 0.0));
	soft += texture(screen_texture, uv + vec2(0.0, px.y));
	soft += texture(screen_texture, uv + vec2(0.0, -px.y));
	soft += texture(screen_texture, uv + vec2(px.x, px.y));
	soft += texture(screen_texture, uv + vec2(-px.x, -px.y));
	soft += texture(screen_texture, uv + vec2(-px.x, px.y));
	soft += texture(screen_texture, uv + vec2(px.x, -px.y));
	soft /= 9.0;
	// the dim wash: the field stays readable, it just reads as paused
	COLOR = mix(soft, soft * vec4(0.62, 0.58, 0.52, 1.0), strength);
	COLOR.a = 1.0;
}
