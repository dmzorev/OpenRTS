using Godot;

namespace OpenRTS.Godot.Rendering;

/// <summary>One draw batch per frame texture/UV region, not one scene node per unit.</summary>
internal sealed class SpriteBatch
{
    internal MultiMeshInstance3D Node { get; }
    private float[] _buffer = [];
    internal int Count { get; private set; }
    internal int Capacity => _buffer.Length / 20;
    internal SpriteBatch(Texture2D texture, Rect2 region, bool teamPaint)
    {
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/team_sprite.gdshader") };
        material.SetShaderParameter("sprite_texture", texture);
        material.SetShaderParameter("uv_rect", new Vector4(region.Position.X, region.Position.Y, region.Size.X, region.Size.Y));
        material.SetShaderParameter("team_paint", teamPaint);
        Node = new() { Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true, UseCustomData = true, Mesh = new PlaneMesh { Size = Vector2.One, Material = material } } };
    }
    internal void Clear() => Count = 0;
    internal void Add(Vector3 position, float angle, Vector2 size, Color tint, Color team)
    {
        if (Count >= Capacity)
        {
            Array.Resize(ref _buffer, Math.Max(16, Capacity * 2) * 20);
            Node.Multimesh.InstanceCount = Capacity;
        }
        int i = Count++ * 20; float c = MathF.Cos(angle), s = MathF.Sin(angle);
        // Godot's MultiMesh buffer stores three row-major transform rows, then color and custom data.
        _buffer[i] = c * size.X; _buffer[i + 1] = 0; _buffer[i + 2] = -s * size.Y; _buffer[i + 3] = position.X;
        _buffer[i + 4] = 0; _buffer[i + 5] = 1; _buffer[i + 6] = 0; _buffer[i + 7] = position.Y;
        _buffer[i + 8] = s * size.X; _buffer[i + 9] = 0; _buffer[i + 10] = c * size.Y; _buffer[i + 11] = position.Z;
        _buffer[i + 12] = tint.R; _buffer[i + 13] = tint.G; _buffer[i + 14] = tint.B; _buffer[i + 15] = tint.A;
        _buffer[i + 16] = team.R; _buffer[i + 17] = team.G; _buffer[i + 18] = team.B; _buffer[i + 19] = team.A;
    }
    internal void Publish()
    {
        Node.Multimesh.VisibleInstanceCount = Count;
        if (Count > 0) Node.Multimesh.Buffer = _buffer;
    }
    internal static (Texture2D Texture, Rect2 Region) Resolve(Texture2D texture)
    {
        if (texture is AtlasTexture atlas)
        {
            var source = atlas.Atlas ?? throw new ArgumentException("Atlas texture has no source.");
            var rect = atlas.Region;
            if (rect.Size == Vector2.Zero) rect = new(Vector2.Zero, source.GetSize());
            return (source, new(rect.Position / source.GetSize(), rect.Size / source.GetSize()));
        }
        return (texture, new(0, 0, 1, 1));
    }
}
