using Godot;
using OpenRTS.Core;

namespace OpenRTS.Godot.Rendering;

/// <summary>Cell preview only. Placement authority and movement permissions remain in Core.</summary>
public sealed class FootprintView
{
    private readonly ColoredMeshBatch _cells;
    public Node3D Node => _cells.Node;
    public FootprintView()
    {
        var shader = new Shader { Code = "shader_type spatial; render_mode unshaded, cull_disabled, depth_draw_never; void fragment() { float edge = step(0.94, max(abs(UV.x - 0.5), abs(UV.y - 0.5)) * 2.0); ALBEDO = COLOR.rgb; ALPHA = COLOR.a * mix(0.18, 1.0, edge); }" };
        _cells = new(new PlaneMesh { Size = Vector2.One }, "FootprintCells", new ShaderMaterial { Shader = shader });
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
