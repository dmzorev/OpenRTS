using Godot;

namespace OpenRTS.Godot.Controls;

public enum RtsCursor { Default, Select, Inspect, Move, AdjustedMove, Attack, Unavailable, Repair }

/// <summary>Animated vector cursors and marquee; UI time intentionally continues while the match is paused.</summary>
public partial class RtsOverlay : Control
{
    public Vector2 Pointer { get; set; }
    public RtsCursor Cursor { get; set; }
    public Rect2? Marquee { get; set; }
    public string? CustomCursor { get; set; }
    public Vector2? Rally { get; set; }
    private sealed record CursorStyle(Color Color, float Radius, SpriteFrames? Frames, string Sequence, Vector2 Hotspot);
    private readonly System.Collections.Generic.Dictionary<string, CursorStyle> _custom = new(StringComparer.Ordinal);
    public void RegisterCursor(string id, Color color, float radius = 12, SpriteFrames? frames = null, string sequence = "stand", Vector2 hotspot = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (radius < 1 || radius > 128 || frames is not null && (!frames.HasAnimation(sequence) || frames.GetFrameCount(sequence) == 0)) throw new ArgumentException("Invalid custom cursor.");
        _custom[id] = new(color, radius, frames, sequence, hotspot);
    }
    private float _time;
    public override void _Ready() { MouseFilter = MouseFilterEnum.Ignore; SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); }
    public override void _Process(double delta) { _time += (float)delta; QueueRedraw(); }
    public override void _Draw()
    {
        if (Marquee is { } box)
        { DrawRect(box, new Color(0.2f, 0.8f, 1, 0.12f)); DrawRect(box, new Color(0.4f, 0.9f, 1), false, 1.5f); }
        if (Rally is { } rally) { DrawLine(rally, rally + new Vector2(0, -24), Colors.LimeGreen, 2); DrawColoredPolygon([rally + new Vector2(0, -24), rally + new Vector2(15, -18), rally + new Vector2(0, -12)], Colors.LimeGreen); }
        if (CustomCursor is { } id && _custom.TryGetValue(id, out var custom))
        {
            if (custom.Frames is { } frames)
            {
                float total = 0; for (int f = 0; f < frames.GetFrameCount(custom.Sequence); f++) total += frames.GetFrameDuration(custom.Sequence, f);
                float phase = (_time * (float)frames.GetAnimationSpeed(custom.Sequence)) % total; int frame = 0;
                while (frame < frames.GetFrameCount(custom.Sequence) - 1 && phase >= frames.GetFrameDuration(custom.Sequence, frame)) { phase -= frames.GetFrameDuration(custom.Sequence, frame); frame++; }
                DrawTexture(frames.GetFrameTexture(custom.Sequence, frame), Pointer - custom.Hotspot);
            }
            else
            { DrawArc(Pointer, custom.Radius + MathF.Sin(_time * 6), _time, _time + MathF.Tau * 0.85f, 40, custom.Color, 2, true);
                DrawLine(Pointer - new Vector2(5, 0), Pointer + new Vector2(5, 0), custom.Color, 2); DrawLine(Pointer - new Vector2(0, 5), Pointer + new Vector2(0, 5), custom.Color, 2); }
            return;
        }
        Vector2 p = Pointer; float pulse = 0.5f + MathF.Sin(_time * 6) * 0.5f;
        var color = Cursor switch
        { RtsCursor.Repair => new Color(0.2f, 1, 0.6f), RtsCursor.Attack => new Color(1, 0.25f, 0.18f), RtsCursor.Unavailable => new Color(1, 0.35f, 0.3f),
            RtsCursor.AdjustedMove => new Color(1, 0.77f, 0.25f), RtsCursor.Move => new Color(0.35f, 1, 0.55f),
            RtsCursor.Select => new Color(0.3f, 0.85f, 1), _ => Colors.White };
        if (Cursor is RtsCursor.Default or RtsCursor.Select or RtsCursor.Inspect)
        {
            Vector2[] arrow = [p, p + new Vector2(0, 23), p + new Vector2(6, 17), p + new Vector2(11, 28), p + new Vector2(15, 26), p + new Vector2(10, 15), p + new Vector2(20, 15)];
            DrawColoredPolygon(arrow, new Color(0.02f, 0.04f, 0.06f, 0.95f)); DrawPolyline([.. arrow, arrow[0]], color, 1.5f, true);
            if (Cursor != RtsCursor.Default) DrawArc(p + new Vector2(21, 6), 5 + pulse, _time, _time + MathF.PI * 1.5f, 16, color, 1.5f, true);
        }
        else if (Cursor == RtsCursor.Unavailable)
        { DrawArc(p, 10 + pulse, 0, MathF.Tau, 32, color, 2, true); DrawLine(p + new Vector2(-7, 7), p + new Vector2(7, -7), color, 2, true); }
        else
        {
            DrawArc(p, 8 + pulse * 2, _time, _time + MathF.PI * 1.5f, 24, color, 1.5f, true);
            for (int d = 0; d < 4; d++)
            {
                var direction = Vector2.FromAngle(d * MathF.PI / 2);
                DrawLine(p + direction * 12, p + direction * (18 + pulse * 2), color, 2, true);
            }
            if (Cursor == RtsCursor.Repair) { DrawLine(p + new Vector2(-5, 0), p + new Vector2(5, 0), color, 3); DrawLine(p + new Vector2(0, -5), p + new Vector2(0, 5), color, 3); }
            else if (Cursor == RtsCursor.Attack) { DrawLine(p - Vector2.One * 4, p + Vector2.One * 4, color, 1.5f); DrawLine(p + new Vector2(-4, 4), p + new Vector2(4, -4), color, 1.5f); }
            else { DrawLine(p + new Vector2(-4, -2), p + new Vector2(0, 3), color, 2); DrawLine(p + new Vector2(0, 3), p + new Vector2(6, -6), color, 2); }
            if (Cursor == RtsCursor.AdjustedMove) DrawString(ThemeDB.FallbackFont, p + new Vector2(15, 28), "↪", HorizontalAlignment.Left, -1, 20, color);
        }
    }
}
