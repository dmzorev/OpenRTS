using Godot;
using OpenRTS.Core;

namespace OpenRTS.Godot;

public sealed class ObstacleView
{
    public MultiMeshInstance3D Node { get; } = new();
    private NavigationGrid? _grid;

    public void Apply(NavigationGrid? grid)
    {
        if (ReferenceEquals(grid, _grid)) return;
        _grid = grid;
        var cells = new List<int>();
        if (grid is not null)
            for (int i = 0; i < grid.CellCount; i++) if (grid.IsBlocked(i)) cells.Add(i);
        var material = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.43f, 0.22f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        var mesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = new BoxMesh { Size = new Vector3(1, 1.2f, 1), Material = material }, InstanceCount = cells.Count };
        for (int i = 0; i < cells.Count; i++)
        {
            var center = grid!.Center(cells[i]);
            mesh.SetInstanceTransform(i, new Transform3D(Basis.Identity, new(center.X / 1024f, 0.6f, center.Y / 1024f)));
        }
        var previous = Node.Multimesh;
        Node.Multimesh = mesh;
        previous?.Dispose();
    }
}
