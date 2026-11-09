using Godot;

namespace OpenRTS.Godot.Rendering;

/// <summary>Decorative circle history, independent of authoritative cell expiration.</summary>
internal sealed class TemporalSight
{
    private readonly SubViewport[] _views;
    private readonly ShaderMaterial[] _materials;
    private readonly ColorRect[] _rects;
    private readonly Texture2D _current;
    private long _tick = -1;
    private uint _viewer;
    private int _hold, _front;
    public Texture2D Texture => _views[_front].GetTexture();

    internal TemporalSight(Node parent, Texture2D current)
    {
        _current = current;
        _views = [new(){Disable3D=true,TransparentBg=true,RenderTargetUpdateMode=SubViewport.UpdateMode.Disabled},new(){Disable3D=true,TransparentBg=true,RenderTargetUpdateMode=SubViewport.UpdateMode.Disabled}];
        var shader=new Shader{Code="""
            shader_type canvas_item;
            uniform sampler2D current_sight : filter_linear, repeat_disable;
            uniform sampler2D previous_sight : filter_linear, repeat_disable;
            uniform float decay;
            void fragment() {
                float a = max(texture(current_sight,UV).a,max(0.0,texture(previous_sight,UV).a-decay));
                COLOR = vec4(1.0,1.0,1.0,a);
            }
            """};
        _materials=[new(){Shader=shader},new(){Shader=shader}];
        _rects=[new(){Material=_materials[0]},new(){Material=_materials[1]}];
        for(int i=0;i<2;i++){_views[i].AddChild(_rects[i]);parent.AddChild(_views[i]);_materials[i].SetShaderParameter("current_sight",current);}
    }
    internal void Apply(long tick,uint viewer,int hold,Vector2I size)
    {
        bool reset=_tick<0 || tick<_tick || viewer!=_viewer || hold!=_hold || _views[0].Size!=size;
        if(!reset && tick==_tick)return;
        if(reset)for(int i=0;i<2;i++){_views[i].Size=size;_rects[i].Size=size;}
        int next=1-_front;
        _materials[next].SetShaderParameter("previous_sight",reset?_current:Texture);
        _materials[next].SetShaderParameter("decay",reset?1f:(tick-_tick)/(float)(hold+1));
        _views[next].RenderTargetUpdateMode=SubViewport.UpdateMode.Once;
        _front=next;_tick=tick;_viewer=viewer;_hold=hold;
    }
}
