using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Visibility;

namespace OpenRTS.Godot.Rendering;

public enum FogDisplayStyle { Exact, Circles, Compare }

internal partial class FogCircleCanvas : Node2D
{
    internal VisionSource[] Sources = [];
    internal int SourceCount;
    internal Vector2 WorldScale;
    public override void _Draw()
    {
        DrawSetTransform(Vector2.Zero, 0, WorldScale);
        for (int i = 0; i < SourceCount; i++) { var s = Sources[i]; DrawCircle(new(s.X, s.Y), s.Radius, Colors.White, antialiased: true); }
    }
}

/// <summary>Presentation-only mask. Unknown cells stay square; current sight can use true circles.</summary>
public sealed class FogWorldView
{
    public Node3D Node { get; } = new() { Name = "FogOfWar" };
    public Texture2D Texture => _composite.GetTexture();
    private readonly SubViewport _circles = new() { Disable3D = true, TransparentBg = true };
    private readonly SubViewport _composite = new() { Disable3D = true, TransparentBg = true };
    private readonly FogCircleCanvas _canvas = new();
    private readonly ColorRect _rect = new() { Color = Colors.White };
    private readonly ShaderMaterial _shader;
    private readonly MeshInstance3D _plane = new() { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    private ImageTexture? _states;
    private TemporalSight? _trail;
    private long _tick = -1;
    private uint _viewer;
    private FogDisplayStyle _style;
    private int _width, _depth;

    public FogWorldView()
    {
        _shader = new() { Shader = new Shader { Code = """
            shader_type canvas_item;
            uniform sampler2D states : filter_nearest, repeat_disable;
            uniform sampler2D sight : filter_linear, repeat_disable;
            uniform int style = 1;
            uniform vec2 cells;
            uniform vec2 world_size;
            uniform float cell_size;
            uniform bool retention = false;
            void fragment() {
                vec2 logical = UV * world_size / cell_size;
                vec2 data = texture(states, (floor(logical)+0.5)/cells).rg;
                float state = data.r;
                float circle = texture(sight, UV).a;
                float visible = style == 0 ? (retention ? data.g : step(0.75, state)) : circle;
                float alpha = state < 0.25 ? 1.0 : 0.65 * (1.0-visible);
                if (style == 2 && state >= 0.25) {
                    vec2 edge = min(fract(logical), 1.0-fract(logical));
                    if (min(edge.x,edge.y) < 0.075) alpha = max(alpha, 0.24);
                }
                COLOR = vec4(0.0,0.0,0.0,alpha);
            }
            """ } };
        _rect.Material = _shader;
        _circles.AddChild(_canvas); _composite.AddChild(_rect);
        Node.AddChild(_circles); Node.AddChild(_composite); Node.AddChild(_plane);
        _shader.SetShaderParameter("sight", _circles.GetTexture());
        _plane.MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoTexture = Texture, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
    }
    public void Apply(FrameExchange.FrameLease frame, FogDisplayStyle style = FogDisplayStyle.Circles)
    {
        Node.Visible = frame.FogCellSize > 0;
        if (!Node.Visible || _tick == frame.Tick && _viewer == frame.Viewer && _style == style) return;
        _tick = frame.Tick; _viewer = frame.Viewer; _style = style;
        if (_width != frame.FogWidth || _depth != frame.FogDepth)
        {
            _width = frame.FogWidth; _depth = frame.FogDepth;
            float scale = Math.Min(8, 2048f / Math.Max(frame.FogColumns, frame.FogRows));
            var size = new Vector2I(Math.Max(1,(int)(frame.FogColumns*scale)),Math.Max(1,(int)(frame.FogRows*scale)));
            _circles.Size = _composite.Size = size; _rect.Size = size;
            _canvas.WorldScale = new(size.X/(float)_width,size.Y/(float)_depth);
            _plane.Mesh = new PlaneMesh { Size = new(_width/1024f,_depth/1024f) };
            _plane.Position = new(_width/2048f,0.08f,_depth/2048f);
        }
        using var image = Image.CreateEmpty(frame.FogColumns, frame.FogRows, false, Image.Format.Rg8);
        for (int y = 0; y < frame.FogRows; y++) for (int x = 0; x < frame.FogColumns; x++)
            image.SetPixel(x,y,new Color((int)frame.FogStates[y*frame.FogColumns+x]/2f,frame.FogStrength.Length == 0 ? 0 : frame.FogStrength[y*frame.FogColumns+x]/255f,0));
        if (_states is null) _states = ImageTexture.CreateFromImage(image); else _states.Update(image);
        _shader.SetShaderParameter("retention",frame.FogStrength.Length > 0);
        _shader.SetShaderParameter("states",_states); _shader.SetShaderParameter("style",(int)style);
        _shader.SetShaderParameter("cells",new Vector2(frame.FogColumns,frame.FogRows));
        _shader.SetShaderParameter("world_size",new Vector2(_width,_depth)); _shader.SetShaderParameter("cell_size",(float)frame.FogCellSize);
        _canvas.SourceCount = style == FogDisplayStyle.Exact && frame.FogHoldTicks == 0 ? 0 : frame.VisionSources.Length;
        if (_canvas.Sources.Length < _canvas.SourceCount) Array.Resize(ref _canvas.Sources, _canvas.SourceCount);
        frame.VisionSources[.._canvas.SourceCount].CopyTo(_canvas.Sources); _canvas.QueueRedraw();
        _circles.RenderTargetUpdateMode = _composite.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        if (frame.FogHoldTicks > 0)
        {
            _trail ??= new(Node,_circles.GetTexture());
            _trail.Apply(frame.Tick,frame.Viewer,frame.FogHoldTicks,_circles.Size);
            _shader.SetShaderParameter("sight",_trail.Texture);
        }
        else _shader.SetShaderParameter("sight",_circles.GetTexture());
    }
}
