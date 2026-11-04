using Godot;
using OpenRTS.Core;

namespace OpenRTS.Godot;

/// <summary>Static diagnostic instancing; no entity nodes or gameplay state are created here.</summary>
public static class PreviewRenderer
{
    public static MultiMeshInstance3D Create(UnitDefinition definition, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var material = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.28f, 0.85f, 0.68f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        var mesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = new CylinderMesh { TopRadius = 0.25f, BottomRadius = 0.25f, Height = 0.5f, RadialSegments = 12, Material = material },
            InstanceCount = count
        };
        int columns = (int)Math.Ceiling(Math.Sqrt(count));
        for (int i = 0; i < count; i++)
            mesh.SetInstanceTransform(i, new Transform3D(Basis.Identity,
                new Vector3(i % columns - (columns - 1) / 2f, 0.25f, i / columns - (columns - 1) / 2f)));
        return new MultiMeshInstance3D { Name = definition.Id + "Preview", Multimesh = mesh };
    }
}
