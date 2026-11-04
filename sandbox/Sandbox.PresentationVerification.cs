using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using OpenRTS.Core;
using OpenRTS.Content;

public partial class Sandbox
{
    private async void VerifyPresentation(FrameExchange.FrameLease frame)
    {
        try
        {
            _verificationDone = true;
            var sprites = _sprites ?? throw new InvalidOperationException("Missing sprite renderer.");
            var report = _session!.Completion.GetAwaiter().GetResult();
            long tick = frame.Tick; int tickRate = frame.TickRate;
            var poses = frame.Poses.ToArray(); var apc = poses.Single(p => p.Id.Value == 3);
            var type = _catalog!.Units[apc.UnitType];
            uint expected = type.StateMask("damaged") | type.StateMask("swimming");
            if (tick != 12 || apc.Presentation.States != expected || !apc.Presentation.Weapon.IsActive(tick) ||
                apc.Presentation.Body.Animation != type.MoveAnimation || apc.Presentation.BodyFacing == apc.Presentation.WeaponFacing)
                throw new InvalidOperationException("Concurrent movement, aim, fire and states were not published.");
            if (sprites.BatchCount != 7 || sprites.SpriteCount != 16)
                throw new InvalidOperationException($"Expected seven textures and sixteen sprite layers; got {sprites.BatchCount}/{sprites.SpriteCount}.");
            int capacity = sprites.Capacity;
            sprites.Apply(ReadOnlySpan<EntityPose>.Empty, 1, tick, tickRate);
            if (sprites.SpriteCount != 0 || sprites.Node.GetChildren().OfType<MultiMeshInstance3D>().Any(n => n.Multimesh.VisibleInstanceCount != 0))
                throw new InvalidOperationException("Empty presentation did not hide the batches.");
            sprites.Apply(poses, 1, tick, tickRate);
            if (sprites.Capacity != capacity) throw new InvalidOperationException("Stable frame failed to reuse buffers.");
            VerifyAnimationSequences();
            var stress = new EntityPose[10000];
            for (int i = 0; i < stress.Length; i++)
            {
                var template = poses[i % poses.Length];
                var position = new WorldPosition((i % 100 + 1) * 1024, (i / 100 + 1) * 1024);
                stress[i] = template with { Id = new((uint)i + 1), Previous = position, Current = position };
            }
            sprites.Apply(stress, 1, tick, tickRate);
            if (sprites.BatchCount != 7 || sprites.SpriteCount != 16000 || sprites.Node.GetChildCount() != 7)
                throw new InvalidOperationException("Sprite scene size grew with entity count.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            sprites.Apply(poses, 1, tick, tickRate);
            foreach (var path in Directory.EnumerateFiles(ProjectSettings.GlobalizePath("res://assets/units"), "*.png"))
            {
                using var image = Image.LoadFromFile(path);
                if (image is null || image.DetectAlpha() == Image.AlphaMode.None || image.GetPixel(0, 0).A > 0.02f)
                    throw new InvalidOperationException("Unit sprite lacks actual transparent alpha: " + path + $" mode={image?.DetectAlpha()}, corner={image?.GetPixel(0, 0).A}");
            }
            _selected = new(3); UpdateSelection(1);
            if (_label is not null) _label.Text = ShowcaseLabel(tick);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_capturePath is not null && DisplayServer.GetName() != "headless")
            {
                using var screenshot = GetViewport().GetTexture().GetImage();
                int blue = TeamPixels(screenshot, poses.Single(p => p.Id.Value == 5), true);
                int red = TeamPixels(screenshot, poses.Single(p => p.Id.Value == 10), false);
                if (blue < 8 || red < 8) throw new InvalidOperationException($"Team recoloring is missing: blue={blue}, red={red}.");
                var path = Path.GetFullPath(_capturePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (screenshot.SavePng(path) != Error.Ok) throw new IOException("Failed to save presentation screenshot.");
            }
            GD.Print("OPENRTS_PRESENTATION:PASS seven alpha sprites, layered turret, concurrent states, weighted/pingpong timing, 10000-unit buffers and terrain resources");
            GD.Print("OPENRTS_SIMULATION:" + JsonSerializer.Serialize(report));
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
    private void VerifyAnimationSequences()
    {
        string resource = $"res://.godot/presentation-tests/frames-{System.Environment.ProcessId}.tres";
        Directory.CreateDirectory(Path.GetDirectoryName(ProjectSettings.GlobalizePath(resource))!);
        var frames = new SpriteFrames();
        Texture2D[] textures = [TestTexture("frame0", Colors.Red), TestTexture("frame1", Colors.Green), TestTexture("frame2", Colors.Blue)];
        foreach (string sequence in new[] { "stand", "travel", "shoot" })
        {
            frames.AddAnimation(sequence); frames.SetAnimationSpeed(sequence, 4);
            frames.SetAnimationLoopMode(sequence, sequence == "stand" ? SpriteFrames.LoopMode.None : SpriteFrames.LoopMode.Pingpong);
            for (int i = 0; i < textures.Length; i++) frames.AddFrame(sequence, textures[i], sequence == "stand" ? i == 0 ? 18 : 1 : i == 1 ? 2 : 1);
        }
        if (ResourceSaver.Save(frames, resource) != Error.Ok) throw new IOException("Failed to create sequence verification fixture.");
        var units = UnitCatalog.Load([new("sequence-test", "u: {Health: {HP: 1}, Presentation: {Model: m, Animations: [idle, move, fire]}}")]);
        var models = ModelCatalog.Load([new("sequence-model", $"m: {{Model: {{Parts: {{body: {{Resource: '{resource}'}}}}, Animations: {{idle: {{Sequence: stand, Parts: [body]}}, move: {{Sequence: travel, Parts: [body]}}, fire: {{Sequence: shoot, Parts: [body]}}}}}}}}")]);
        var probe = new OpenRTS.Godot.SpriteWorldView(units, models); AddChild(probe.Node);
        var type = units.Units[0];
        foreach (var sample in new[] { (0, "frame0"), (3, "frame1"), (8, "frame2"), (11, "frame1"), (15, "frame0") })
            Check(type.MoveAnimation, 0, sample.Item1, sample.Item2);
        foreach (var sample in new[] { (0, "frame0"), (3, "frame1"), (6, "frame2"), (9, "frame1") })
            Check(type.MoveAnimation, 10, sample.Item1, sample.Item2);
        Check(type.IdleAnimation, 0, 60, "frame2");
        Check(type.IdleAnimation, 10, 9, "frame1");
        Check(type.IdleAnimation, 10, 9, "frame2", 1);
        probe.Node.Visible = false; probe.Node.QueueFree();
        void Check(int animation, int duration, int tick, string expected, float alpha = 0)
        {
            var view = new EntityPresentation(0, FacingDirection.North, FacingDirection.North, FacingDirection.North, FacingDirection.North,
                new(animation, 0, duration), AnimationPlayback.None, 0);
            probe.Apply([new(new(1), 0, new(1024, 1024), new(1024, 1024), view)], alpha, tick, 10);
            var batch = probe.Node.GetChildren().OfType<MultiMeshInstance3D>().Single(n => n.Multimesh.VisibleInstanceCount == 1);
            var material = (ShaderMaterial)batch.Multimesh.Mesh.SurfaceGetMaterial(0);
            var texture = (Texture2D)material.GetShaderParameter("sprite_texture").AsGodotObject();
            if (texture.ResourceName != expected) throw new InvalidOperationException($"Sequence timing: tick={tick}, duration={duration}, expected={expected}, actual={texture.ResourceName}.");
        }
        static Texture2D TestTexture(string name, Color color) => new GradientTexture2D
        { Width = 4, Height = 4, ResourceName = name, Gradient = new Gradient { Colors = [color, color] } };
    }
    private int TeamPixels(Image image, EntityPose pose, bool blue)
    {
        var pixel = _camera!.UnprojectPosition(new(pose.Current.X / 1024f, 0, pose.Current.Y / 1024f));
        int count = 0;
        for (int y = Math.Max(0, (int)pixel.Y - 35); y < Math.Min(image.GetHeight(), (int)pixel.Y + 35); y++)
            for (int x = Math.Max(0, (int)pixel.X - 35); x < Math.Min(image.GetWidth(), (int)pixel.X + 35); x++)
            {
                var color = image.GetPixel(x, y);
                if (blue ? color.B > 0.3f && color.B > color.R * 1.8f : color.R > 0.3f && color.R > color.B * 1.8f) count++;
            }
        return count;
    }
}
