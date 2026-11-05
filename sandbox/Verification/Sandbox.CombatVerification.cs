using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using OpenRTS.Core;

public partial class Sandbox
{
    private async Task VerifyCombatPresentation()
    {
        var catalog = _catalog!; var world = new SimulationWorld(catalog.Units, 48 * 1024, 32 * 1024);
        int Type(string id) => catalog.Units.ToList().FindIndex(u => u.Id == id);
        void Spawn(string id, int x, int y, uint owner) => world.AddInitialUnit(Type(id), new(x * 1024, y * 1024), owner);
        Spawn("tank-mixed", 12, 10, 1); Spawn("bunker-cannon", 17, 10, 2);
        Spawn("engineer", 12, 18, 1); Spawn("apc-mg", 14, 18, 1); Spawn("rocketeer", 19, 18, 2);
        Spawn("rifleman", 25, 10, 1); Spawn("bunker-mixed", 29, 10, 2); Spawn("rifleman", 29, 11, 2);
        bool firedCannon = false, firedRocket = false, repaired = false, exploded = false;
        var saved = _displayPoses; int savedCount = _displayCount; var selected = _selection.Snapshot();
        var cameraPosition = _camera!.Position; float cameraSize = _camera.Size;
        _camera.Position = new(20, 40, 14); _camera.Size = 21;
        _selectionRing!.Visible = true;
        _combatEffects!.Clear();
        try
        {
            for (int tick = 1; tick <= 40; tick++)
            {
                WorldCommand[] commands = tick switch {
                    1 => [new(1, 0, 1, CommandKind.AttackTarget, new(1), TargetEntity: new(2)), new(1, 0, 2, CommandKind.AttackTarget, new(5), TargetEntity: new(4)), new(1, 0, 3, CommandKind.UseAbility, new(6), TargetEntity: new(7), Action: "grenade")],
                    2 => [new(2, 0, 1, CommandKind.Move, new(1), new(12 * 1024, 13 * 1024))],
                    35 => [new(35, 0, 1, CommandKind.RepairTarget, new(3), TargetEntity: new(4))], _ => [] };
                world.Step(commands);
                foreach (var value in world.Events)
                {
                    _combatEffects.Accept(value);
                    if (value.Kind == WorldEventKind.WeaponFired && value.Entity.Value == 1)
                    { firedCannon |= value.Mount == 0; firedRocket |= value.Mount == 1; }
                    repaired |= value.Kind == WorldEventKind.Repaired;
                    exploded |= value.Kind == WorldEventKind.ProjectileImpact && value.Entity.Value == 6;
                }
                var poses = new EntityPose[world.Count]; var weapons = new WeaponPose[world.WeaponCount]; var shots = new ProjectilePose[world.ProjectileCount];
                world.CopyPoses(poses); world.CopyWeaponPoses(weapons); world.CopyProjectilePoses(shots);
                _sprites!.Apply(poses, 0.5f, tick, world.TickRate, weapons); _combatEffects.Apply(shots, 0.5f, tick);
                if (tick == 18)
                {
                    var mount = weapons.First(w => w.Entity.Value == 1 && w.Mount == 0);
                    var turret = _sprites.Node.GetChildren().OfType<MultiMeshInstance3D>().Single(n => n.Name == "Sprite_mixed-turret").Multimesh.Buffer;
                    float angle = SpriteWorldView.Angle(mount.PreviousFacing, mount.Facing, 0.5f);
                    if (!Mathf.IsEqualApprox(turret[0], MathF.Cos(angle) * 2) || !Mathf.IsEqualApprox(turret[8], MathF.Sin(angle) * 2))
                        throw new InvalidOperationException("Moving turret did not use its mount's target facing.");
                }
                if (tick == 18 && (_combatEffects.ProjectileCount < 2 || _combatEffects.Node.GetChildCount() != 3)) throw new InvalidOperationException("Projectile batches missing or per-shot nodes created.");
                if (tick == 36)
                {
                    var tracers = _combatEffects.Node.GetNode<MultiMeshInstance3D>("Tracers").Multimesh;
                    var buffer = tracers.Buffer; bool horizontalRepair = false;
                    var repairShot = world.Events.ToArray().Single(e => e.Kind == WorldEventKind.WeaponFired && e.Entity.Value == 3);
                    float expectedLength = (repairShot.Position.X - repairShot.SourcePosition.X) / 1024f;
                    for (int i = 0; i < tracers.VisibleInstanceCount; i++)
                    {
                        int n = i * 16;
                        if (MathF.Abs(buffer[n + 12] - 0.2f) < 0.001f && MathF.Abs(buffer[n + 13] - 1) < 0.001f)
                            horizontalRepair |= MathF.Abs(MathF.Abs(buffer[n + 2]) - expectedLength) < 0.001f && MathF.Abs(buffer[n + 10]) < 0.001f;
                    }
                    if (!horizontalRepair) throw new InvalidOperationException("Repair beam does not follow the source-to-target segment.");
                    _displayPoses = poses; _displayCount = poses.Length; RefreshSelection(); _selection.Change([new(3)], OpenRTS.Godot.Controls.SelectionChange.Replace);
                    if (!ActivateAction(UnitActionKind.Repair) || _armedAction != UnitActionKind.Repair) throw new InvalidOperationException("Repair action mode unavailable.");
                    _pointer = Screen(poses.Single(p => p.Id.Value == 4)); UpdateRtsCursor();
                    if (_rtsOverlay!.Cursor != OpenRTS.Godot.Controls.RtsCursor.Repair) throw new InvalidOperationException("Repair cursor missing on a damaged ally.");
                    _armedAction = null;
                }
                if (tick is 18 or 24 or 36 && _capturePath is not null && DisplayServer.GetName() != "headless")
                {
                    _selection.Change([], OpenRTS.Godot.Controls.SelectionChange.Replace); UpdateSelection(1);
                    if (_label is not null) _label.Text = $"OpenRTS · оружие · тик {tick}\nПушка + ракета на ходу · граната · ремонт союзника";
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    using var screenshot = GetViewport().GetTexture().GetImage();
                    string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_capturePath))!, $"combat-{tick}.png");
                    if (screenshot.SavePng(path) != Error.Ok) throw new IOException("Combat capture failed.");
                }
            }
            if (!firedCannon || !firedRocket || !repaired || !exploded) throw new InvalidOperationException($"Combat presentation scenario incomplete: {firedCannon}/{firedRocket}/{repaired}/{exploded}.");
            GD.Print("OPENRTS_COMBAT_PRESENTATION:PASS independent weapons, projectiles, impacts, repair action/cursor and three batched effect nodes");
        }
        finally
        {
            _combatEffects.Clear(); _combatEffects.Apply([], 1, 40);
            _camera.Position = cameraPosition; _camera.Size = cameraSize;
            _displayPoses = saved; _displayCount = savedCount; RefreshSelection(); _selection.Change(selected, OpenRTS.Godot.Controls.SelectionChange.Replace); _armedAction = null;
        }
    }
}
