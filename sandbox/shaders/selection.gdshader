shader_type spatial;
render_mode unshaded, cull_disabled;
void fragment() {
    float radius = length(UV - vec2(0.5));
    float ring = smoothstep(0.43, 0.44, radius) * (1.0 - smoothstep(0.47, 0.48, radius));
    ALBEDO = COLOR.rgb;
    ALPHA = ring * 0.85;
}
