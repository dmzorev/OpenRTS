using Godot;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Godot.Rendering;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;

/// <summary>Offline icon rendering through the same model composition as the game.</summary>
public partial class SandboxIconBuilder : Node
{
    public override async void _Ready()
    {
        try
        {
            var args = OS.GetCmdlineUserArgs();
            string Option(string key, string fallback) => args.FirstOrDefault(a => a.StartsWith(key + "="))?[(key.Length + 1)..] ?? fallback;
            var manifest = GameManifest.Load(Option("--game", ""));
            var units = manifest.LoadUnits();
            var models = manifest.LoadModels();
            if (args.Contains("--verify-icons"))
            {
                int verified = 0;
                foreach (var model in models.Models.Values.OrderBy(model => model.Id, StringComparer.Ordinal))
                {
                    if (model.Icon is null) continue;
                    var texture = GD.Load<Texture2D>(model.Icon) ?? throw new InvalidOperationException("Missing icon texture: " + model.Icon);
                    if (texture.GetWidth() <= 0 || texture.GetHeight() <= 0) throw new InvalidOperationException("Empty icon texture: " + model.Icon);
                    verified++;
                }
                GD.Print("Verified " + verified + " composed icons.");
                GetTree().Quit();
                return;
            }
            var requested = Option("--ids", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            bool vehicles = args.Contains("--vehicles");
            int size = int.Parse(Option("--size", "256"));
            if (size < 32 || size > 2048) throw new ArgumentException("Icon size must be 32..2048.");
            string output = Path.GetFullPath(Option("--output", ""));
            string resourceDirectory = Option("--resource-dir", "");
            Directory.CreateDirectory(output);
            var viewport = new SubViewport
            {
                Size = new(size * 2, size * 2), TransparentBg = true, OwnWorld3D = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always
            };
            AddChild(viewport);
            var camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = 64, Far = 200, Current = true };
            viewport.AddChild(camera);
            camera.RotationDegrees = manifest.Settings.Presentation.Camera == GameCamera.Isometric
                ? new(-35.26439f, 45, 0) : new(-90, 0, 0);
            camera.Position = camera.Basis.Z * 50;
            var sprites = new SpriteWorldView(units, models, _ => new Color("#e948cf"));
            viewport.AddChild(sprites.Node);
            sprites.AlignDepthWithCamera(camera);
            var completed = new Dictionary<string, string>(StringComparer.Ordinal);
            var matched = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < units.Units.Count; i++)
            {
                var unit = units.Units[i];
                var binding = units.Presentations[i];
                if (binding.Model is null) continue;
                string modelId = manifest.Settings.Presentation.ModelOverrides.GetValueOrDefault(binding.Model, binding.Model);
                if (requested.Count > 0 && !requested.Contains(unit.Id) && !requested.Contains(modelId) && !requested.Contains(binding.Model)) continue;
                if (vehicles && (!unit.Movable || unit.Armor is not (ArmorKind.Light or ArmorKind.Mechanical))) continue;
                matched.Add(unit.Id); matched.Add(modelId); matched.Add(binding.Model);
                if (completed.ContainsKey(modelId)) continue;
                var facing = FacingDirection.North;
                var pose = new EntityPose(new(1), i, default, default,
                    new(1, facing, facing, facing, facing, new(unit.IdleAnimation, 0, 0), AnimationPlayback.None, 0));
                camera.Size = 64;
                camera.Position = camera.Basis.Z * 50;
                sprites.Apply([pose], 1, 0, 30);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                using var rough = viewport.GetTexture().GetImage();
                var rect = rough.GetUsedRect();
                if (!rect.HasArea()) throw new InvalidOperationException("Empty icon for " + unit.Id);
                // Center on the visible pixels, then render again at the final pixel density.
                float pixelScale = camera.Size / viewport.Size.Y;
                var center = (Vector2)rect.Position + (Vector2)rect.Size / 2 - (Vector2)viewport.Size / 2;
                camera.Position += camera.Basis.X * center.X * pixelScale - camera.Basis.Y * center.Y * pixelScale;
                camera.Size *= Math.Max(rect.Size.X, rect.Size.Y) / (float)viewport.Size.Y / 0.86f;
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                using var image = viewport.GetTexture().GetImage();
                image.Resize(size, size, Image.Interpolation.Lanczos);
                string filename = modelId + ".png";
                if (image.SavePng(Path.Combine(output, filename)) != Error.Ok) throw new IOException("Could not save " + filename);
                completed.Add(modelId, resourceDirectory + "/" + filename);
                GD.Print("ICON " + unit.Id + " -> " + filename);
            }
            var missing = requested.Except(matched).ToArray();
            if (missing.Length > 0) throw new ArgumentException("Unknown or filtered IDs: " + string.Join(", ", missing));
            // Merge partial rebuilds so earlier icons remain connected.
            string declarations = Path.Combine(output, "icons.yaml");
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            string index = Path.Combine(output, "index.json");
            if (requested.Count > 0 && File.Exists(index)) entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(index))!;
            foreach (var item in completed) entries[item.Key] = item.Value;
            File.WriteAllText(declarations, "# Generated by tools/build_unit_icons.py\n" + string.Join("", entries.OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => JsonSerializer.Serialize(e.Key) + ": " + JsonSerializer.Serialize(e.Value) + "\n")));
            File.WriteAllText(index, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            GD.Print("Built " + completed.Count + " composed icons.");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
