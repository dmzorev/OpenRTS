using Godot;
using OpenRTS.Core.Combat;

public partial class Sandbox3D
{
    // Launch marker exported from the firing pose, including the infantry scale of 0.72.
    private static readonly Vector3 RocketeerMuzzle = new(-0.20615f, 1.00997f, 0.22648f);
    private MultiMesh _rockets = null!;

    private void CreateRocketBatch()
    {
        _rockets = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = new BoxMesh
            {
                Size = new(0.055f, 0.055f, 0.23f),
                Material = new StandardMaterial3D
                    { AlbedoColor = new Color("#ffe095"), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded }
            }
        };
        AddChild(new MultiMeshInstance3D { Name = "Rockets", Multimesh = _rockets });
    }

    private void UpdateRockets(ReadOnlySpan<ProjectilePose> shots, float alpha)
    {
        if (shots.Length > _rockets.InstanceCount) _rockets.InstanceCount = Math.Max(8, shots.Length);
        int count = 0;
        foreach (var shot in shots)
        {
            if (_models[shot.UnitType].Launcher is null) continue;
            var from = Display(shot.Previous);
            var to = Display(shot.Current);
            if (shot.LaunchProjection is { Origin: { } origin } anchor)
            {
                var direction = shot.Departure.MountFacing;
                float yaw = Mathf.Atan2(direction.X, direction.Y);
                var offset = Display(shot.Departure.Origin) - Display(origin) + RocketeerMuzzle.Rotated(Vector3.Up, yaw);
                from += offset * anchor.PreviousWeight;
                to += offset * anchor.CurrentWeight;
            }
            var heading = from.DirectionTo(to);
            if (heading == Vector3.Zero) heading = new(shot.Facing.X, 0, shot.Facing.Y);
            var up = MathF.Abs(heading.Normalized().Y) > 0.99f ? Vector3.Right : Vector3.Up;
            var basis = heading == Vector3.Zero ? Basis.Identity : Basis.LookingAt(heading, up);
            _rockets.SetInstanceTransform(count++, new(basis, from.Lerp(to, alpha)));
        }
        _rockets.VisibleInstanceCount = count;
    }
}
