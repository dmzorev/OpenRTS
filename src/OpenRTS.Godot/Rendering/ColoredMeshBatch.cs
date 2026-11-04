using Godot;

namespace OpenRTS.Godot.Rendering;

internal sealed class ColoredMeshBatch
{
    internal readonly MultiMeshInstance3D Node;
    private float[] _buffer = [];
    private int _count;
    internal ColoredMeshBatch(Mesh mesh, string name, Material? material = null)
    {
        mesh.SurfaceSetMaterial(0, material ?? new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha });
        Node = new() { Name = name, Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = mesh } };
    }
    internal void Clear() => _count = 0;
    internal void Add(Vector3 position, Vector3 size, float angle, Color color)
    {
        if ((_count + 1) * 16 > _buffer.Length) Array.Resize(ref _buffer, Math.Max(256, _buffer.Length * 2));
        var basis = new Basis(Vector3.Up, angle) * Basis.FromScale(size); int n = _count++ * 16;
        _buffer[n] = basis.X.X; _buffer[n + 1] = basis.Y.X; _buffer[n + 2] = basis.Z.X; _buffer[n + 3] = position.X;
        _buffer[n + 4] = basis.X.Y; _buffer[n + 5] = basis.Y.Y; _buffer[n + 6] = basis.Z.Y; _buffer[n + 7] = position.Y;
        _buffer[n + 8] = basis.X.Z; _buffer[n + 9] = basis.Y.Z; _buffer[n + 10] = basis.Z.Z; _buffer[n + 11] = position.Z;
        _buffer[n + 12] = color.R; _buffer[n + 13] = color.G; _buffer[n + 14] = color.B; _buffer[n + 15] = color.A;
    }
    internal void Publish()
    {
        var multi = Node.Multimesh; if (multi.InstanceCount != _buffer.Length / 16) multi.InstanceCount = _buffer.Length / 16;
        multi.Buffer = _buffer; multi.VisibleInstanceCount = _count;
    }
}
