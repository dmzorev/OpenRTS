shader_type spatial;
render_mode unshaded, cull_disabled;
uniform sampler2D sprite_texture : source_color, filter_linear_mipmap, repeat_disable;
uniform vec4 uv_rect = vec4(0.0, 0.0, 1.0, 1.0);
uniform bool team_paint = true;
varying vec4 team;
void vertex() { team = INSTANCE_CUSTOM; }
void fragment() {
    vec4 sample_color = texture(sprite_texture, uv_rect.xy + UV * uv_rect.zw);
    float mask = team_paint ? smoothstep(0.12, 0.35, (min(sample_color.r, sample_color.b) - sample_color.g) / max(max(sample_color.r, sample_color.b), 0.001)) : 0.0;
    float paint_light = mix(0.52, 1.08, max(sample_color.r, sample_color.b));
    vec3 paint = team.rgb * paint_light;
    vec3 color = mix(sample_color.rgb, paint, mask) * COLOR.rgb;
    ALBEDO = mix(color, vec3(1.0, 0.82, 0.40), team.a);
    ALPHA = sample_color.a * COLOR.a;
    ALPHA_SCISSOR_THRESHOLD = 0.08;
}
