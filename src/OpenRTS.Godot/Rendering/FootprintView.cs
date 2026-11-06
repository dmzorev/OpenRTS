using Godot;
using OpenRTS.Core;

namespace OpenRTS.Godot.Rendering;

/// <summary>Cell preview only. Placement authority and movement permissions remain in Core.</summary>
public sealed class FootprintView
{
    private readonly ColoredMeshBatch _cells;
    private readonly MeshInstance3D _range;
    private readonly StandardMaterial3D _rangeMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        NoDepthTest = true
    };
    public Node3D Node => _cells.Node;
    public FootprintView()
    {
        var shader = new Shader { Code = "shader_type spatial; render_mode unshaded, cull_disabled, depth_draw_never; void fragment() { float edge = step(0.94, max(abs(UV.x - 0.5), abs(UV.y - 0.5)) * 2.0); ALBEDO = COLOR.rgb; ALPHA = COLOR.a * mix(0.18, 1.0, edge); }" };
        _cells = new(new PlaneMesh { Size = Vector2.One }, "FootprintCells", new ShaderMaterial { Shader = shader });
        var circle = new ImmediateMesh();
        circle.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
        const int segments = 96;
        for (int i = 0; i <= segments; i++)
        {
            float angle = i * Mathf.Tau / segments;
            circle.SurfaceAddVertex(new(Mathf.Cos(angle), 0, Mathf.Sin(angle)));
        }
        circle.SurfaceEnd();
        _range = new MeshInstance3D { Name = "ConstructionRange", Mesh = circle, MaterialOverride = _rangeMaterial, Visible = false };
        Node.AddChild(_range);
    }
    public void SetRange(WorldPosition center, int radius, bool placing)
    {
        _range.Visible = radius > 0;
        if (radius <= 0) return;
        _range.Position = new(center.X / 1024f, center.Elevation / 1024f + 0.05f, center.Y / 1024f);
        _range.Scale = new(radius / 1024f, 1, radius / 1024f);
        _rangeMaterial.AlbedoColor = new(0.3f, 0.9f, 1, placing ? 1f : 0.55f);
    }
    public void Apply(FootprintDefinition? selected, WorldPosition selectedPosition, FootprintDefinition? preview = null, WorldPosition previewPosition = default, bool valid = true)
    {
        _cells.Clear();
        if (selected is not null) Draw(selected, selectedPosition, true);
        if (preview is not null) Draw(preview, previewPosition, valid);
        _cells.Publish();
    }
    private void Draw(FootprintDefinition footprint, WorldPosition position, bool valid)
    {
        foreach (var cell in footprint.Cells)
        {
            var box = footprint.Bounds(cell, position);
            Color color = !valid ? new(1, 0.15f, 0.1f, 0.9f) : cell.Navigation.Traverse == false ? new(0.25f, 0.6f, 1, 0.8f) :
                cell.Navigation.CanStop == false ? new(1, 0.65f, 0.15f, 0.8f) : new(0.25f, 1, 0.5f, 0.8f);
            _cells.Add(new((box.MinX + box.MaxX) / 2048f, position.Elevation / 1024f + 0.018f, (box.MinY + box.MaxY) / 2048f), new(footprint.CellSize / 1024f, 1, footprint.CellSize / 1024f), 0, color);
        }
    }
}
