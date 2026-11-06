using Godot;
using OpenRTS.Core;

namespace OpenRTS.Godot.Rendering;

/// <summary>Reuses a packed MultiMesh buffer; no Godot calls occur on the simulation worker.</summary>
public sealed class WorldView(UnitDefinition definition, int count)
{
    public MultiMeshInstance3D Node { get; } = PreviewRenderer.Create(definition, count);
    private float[] _transforms = [];

    public void Apply(ReadOnlySpan<EntityPose> poses, float alpha)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        if (_transforms.Length < poses.Length * 12)
        {
            int capacity = Math.Max(poses.Length, Math.Max(16, _transforms.Length / 6));
            _transforms = new float[checked(capacity * 12)];
            Node.Multimesh.InstanceCount = capacity;
            for (int i = 0; i < capacity; i++)
            {
                _transforms[i * 12] = 1;
                _transforms[i * 12 + 5] = 1;
                _transforms[i * 12 + 10] = 1;
            }
        }
        int visible = 0;
        for (int i = 0; i < poses.Length; i++)
        {
            var pose = poses[i];
            if (pose.Carrier.Value != 0) continue;
            int offset = visible++ * 12;
            _transforms[offset + 3] = Lerp(pose.Previous.X, pose.Current.X, alpha);
            _transforms[offset + 7] = Lerp(pose.Previous.Elevation, pose.Current.Elevation, alpha) + 0.25f;
            _transforms[offset + 11] = Lerp(pose.Previous.Y, pose.Current.Y, alpha);
        }
        Node.Multimesh.VisibleInstanceCount = visible;
        if (visible > 0) Node.Multimesh.Buffer = _transforms;
    }

    private static float Lerp(int previous, int current, float alpha) =>
        (previous + ((long)current - previous) * alpha) / WorldPosition.UnitsPerCell;
}
