using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Combat;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Presentation;

public partial class Sandbox3D
{
    private sealed record ActorModel(PackedScene Scene, bool Infantry, bool ModularVehicle, bool Crane, float Radius,
        WeaponDefinition? Launcher)
    {
        public string Idle => Launcher is null ? "MixIdle" : "RpgIdle";
        public string Run => Launcher is null ? "MixRunLowReady" : "RpgRun";
    }

    private static ActorModel LoadModel(UnitDefinition type)
    {
        string? infantryScene = type.Id switch
        {
            "scout" => "Infantry.tscn", "sniper" => "Sniper.tscn", "rocketeer" => "Rocketeer.tscn", _ => null
        };
        string? modularScene = type.Id switch
        {
            "tank" => "MediumTank", "apc" => "TrackedAPC", "engineer" => "EngineeringTruck", _ => null
        };
        string file = infantryScene ?? type.Id + ".glb";
        string path = modularScene is null
            ? "res://assets/quaternius/" + file : $"res://assets/vehicles/{modularScene}.tscn";
        var scene = GD.Load<PackedScene>(path)
            ?? throw new InvalidOperationException($"Cannot load model {path}.");
        return new ActorModel(scene, infantryScene is not null, modularScene is not null, type.Id == "engineer",
            Math.Max(type.Body.HalfWidth, type.Body.HalfHeight) / (float)CellSize,
            type.Id == "rocketeer" ? type.Weapons[0].Weapon : null);
    }

    private static TorusMesh RingMesh(Color color) => new()
    {
        InnerRadius = 0.88f,
        OuterRadius = 1,
        Rings = 32,
        RingSegments = 8,
        Material = new StandardMaterial3D { AlbedoColor = color, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded }
    };

    private static Vector3 Display(WorldPosition point) => new Vector3(point.X, point.Elevation, point.Y) / CellSize;

    private sealed class ActorView
    {
        private readonly MeshInstance3D _ring;
        private readonly AnimationPlayer _animation;
        private readonly Node3D? _turret;
        private readonly ActorModel _model;
        private bool? _moving;

        public Node3D Node { get; } = new();
        public int Type { get; }
        public uint Owner { get; private set; }
        public Vector3 PickCenter => Node.Position + Vector3.Up * (_model.Infantry ? 0.65f : 0.28f);

        public ActorView(ActorModel model, Mesh marker, int type)
        {
            _model = model;
            Type = type;
            var visual = model.Scene.Instantiate<Node3D>();
            if (model.ModularVehicle)
            {
                visual.Rotation = new(0, Mathf.Pi, 0);
                _turret = visual.FindChild("TurretSocket", recursive: true, owned: false) as Node3D
                    ?? throw new InvalidOperationException("The modular vehicle is missing its turret socket.");
            }
            else
            {
                visual.Scale = Vector3.One * (model.Infantry ? 0.72f : 0.12f);
                if (!model.Infantry) visual.RotationDegrees = new(0, 90, 0);
            }
            Node.AddChild(visual);
            _animation = (AnimationPlayer)visual.FindChild("AnimationPlayer", recursive: true, owned: false);
            if (model.ModularVehicle)
                _animation.GetAnimation("Drive").LoopMode = Animation.LoopModeEnum.Linear;
            if (model.Infantry)
            {
                _animation.GetAnimation(model.Idle).LoopMode = Animation.LoopModeEnum.Linear;
                _animation.GetAnimation(model.Run).LoopMode = Animation.LoopModeEnum.Linear;
            }

            _ring = new MeshInstance3D
            {
                Mesh = marker,
                Position = new(0, 0.04f, 0),
                Scale = new(model.Radius + 0.14f, 0.45f, model.Radius + 0.14f),
                Visible = false
            };
            Node.AddChild(_ring);
        }

        public void SetSelected(bool selected) => _ring.Visible = selected || Owner != 1;

        public float PickRadius(Camera3D camera, Vector2 screenCenter)
        {
            float width = screenCenter.DistanceTo(camera.UnprojectPosition(PickCenter + Vector3.Right * (_model.Radius + 0.12f)));
            float halfHeight = screenCenter.DistanceTo(camera.UnprojectPosition(Node.Position));
            return Math.Max(12, Math.Max(width, halfHeight));
        }

        public void Apply(EntityPose pose, float alpha, Vector3 normal, long tick = 0, WeaponPose weapon = default)
        {
            Owner = pose.Presentation.Owner;
            if (Owner != 1) _ring.Visible = true;
            Node.Position = Display(pose.Previous).Lerp(Display(pose.Current), alpha);
            var before = pose.Presentation.PreviousBodyFacing;
            var after = pose.Presentation.BodyFacing;
            bool moving = pose.Previous != pose.Current;
            bool rocketShot = _model.Launcher is not null && !moving &&
                pose.Order is not { Kind: UnitActionKind.Move or UnitActionKind.AttackMove,
                    Phase: OrderPhase.Moving or OrderPhase.Approaching or OrderPhase.Waiting or OrderPhase.Withdrawing } &&
                (weapon.WindupEndTick > tick || weapon.Animation is { DurationTicks: > 0 } && weapon.Animation.IsActive(tick));
            if (rocketShot && weapon.Facing != default)
                before = after = weapon.Facing;
            Node.Rotation = new(0, Mathf.LerpAngle(Mathf.Atan2(before.X, before.Y), Mathf.Atan2(after.X, after.Y), alpha), 0);
            _ring.Quaternion = new Quaternion(Vector3.Up, Node.Basis.Inverse() * normal);
            if (_turret is not null)
            {
                var previousAim = pose.Presentation.PreviousWeaponFacing;
                var aim = pose.Presentation.WeaponFacing;
                if (previousAim == default) previousAim = before;
                if (aim == default) aim = after;
                float heading = Mathf.LerpAngle(Mathf.Atan2(previousAim.X, previousAim.Y), Mathf.Atan2(aim.X, aim.Y), alpha);
                if (_model.Crane && pose.WeaponCount == 0) heading = Node.Rotation.Y + Mathf.Pi;
                _turret.Rotation = new(0, heading - Node.Rotation.Y, 0);
            }

            if (_model.Launcher is { } launcher)
            {
                string clip = rocketShot ? "RpgFire" : moving ? _model.Run : _model.Idle;
                if (_animation.AssignedAnimation != clip || !_animation.IsPlaying())
                    _animation.Play(clip, rocketShot ? 0 : 0.10, rocketShot ? 0 : 1);
                if (rocketShot)
                {
                    const double LaunchSeconds = 5.0 / 30;
                    double time = tick + alpha;
                    double poseTime = weapon.WindupEndTick > tick
                        ? LaunchSeconds * (1 - Math.Clamp((weapon.WindupEndTick - time) / launcher.WindupTicks, 0, 1))
                        : LaunchSeconds + (_animation.CurrentAnimationLength - LaunchSeconds) *
                            Math.Clamp((time - weapon.Animation.StartTick) / weapon.Animation.DurationTicks, 0, 1);
                    _animation.Seek(poseTime, update: true);
                }
                return;
            }
            if (_moving == moving) return;
            _moving = moving;
            if (_model.Infantry)
                _animation.Play(moving ? _model.Run : _model.Idle, 0.12, moving ? 0.95f : 1f);
            else if (moving) _animation.Play(_model.ModularVehicle ? "Drive" : "TankArmature|Tank_Forward");
            else _animation.Stop(keepState: true);
        }
    }
}
