using System;
using System.Text.Json;
using DeclarativeContent;
using Godot;
using OpenRTS.Content;
using OpenRTS.Godot;

public partial class Sandbox : Node3D
{
    public override void _Ready()
    {
        bool verify = Array.Exists(OS.GetCmdlineUserArgs(), a => a == "--verify");
        try
        {
            int count = 10000;
            foreach (string arg in OS.GetCmdlineUserArgs())
            {
                if (arg == "--verify") continue;
                if (!arg.StartsWith("--count=", StringComparison.Ordinal) || !int.TryParse(arg[8..], out count) || count < 1 || count > 1000000)
                    throw new ArgumentException("Параметры: --verify, --count=1..1000000 (лимит диагностического стенда).");
            }
            using var file = Godot.FileAccess.Open("res://content/units.yaml", Godot.FileAccess.ModeFlags.Read)
                ?? throw new InvalidOperationException("Не удалось открыть content/units.yaml: " + Godot.FileAccess.GetOpenError());
            var catalog = UnitCatalog.Load([new ContentSource("content/units.yaml", file.GetAsText())]);
            var preview = PreviewRenderer.Create(catalog.Units[0], count);
            AddChild(preview);
            BuildScene(count, catalog);

            if (verify)
            {
                VerifyPreview(preview, count);
                GD.Print("OPENRTS_CONTENT:" + JsonSerializer.Serialize(catalog.Summary));
                GD.Print($"OPENRTS_PREVIEW:{count}");
                GetTree().Quit();
            }
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            if (verify) GetTree().Quit(1);
            else AddOverlay("Ошибка загрузки контента\n" + error.Message);
        }
    }

    private void BuildScene(int count, UnitCatalog catalog)
    {
        float side = MathF.Ceiling(MathF.Sqrt(count)) + 8;
        var ground = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(side, side) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.055f, 0.09f, 0.12f),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
            }
        };
        AddChild(ground);
        var camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            Size = side * 1.18f,
            Position = new Vector3(0, side, side * 0.7f),
            Far = side * 4,
            Current = true
        };
        AddChild(camera);
        camera.LookAt(Vector3.Zero);
        AddOverlay($"OpenRTS · подготовка движка\n\n{count:N0} статических маркеров · MultiMesh\n" +
            $"{catalog.Summary.DefinitionCount} определения из YAML · ядро без Godot\n" +
            $"Контент: {catalog.Summary.Fingerprint[..16]}\n\nДвижение и расчёт симуляции — следующий этап.");
    }

    private void AddOverlay(string text)
    {
        var layer = new CanvasLayer();
        AddChild(layer);
        var panel = new PanelContainer { Position = new Vector2(24, 24) };
        layer.AddChild(panel);
        var margin = new MarginContainer();
        foreach (string side in new[] { "left", "top", "right", "bottom" })
            margin.AddThemeConstantOverride("margin_" + side, 18);
        panel.AddChild(margin);
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", 18);
        margin.AddChild(label);
    }

    private static void VerifyPreview(MultiMeshInstance3D preview, int count)
    {
        if (preview.Multimesh.InstanceCount != count || preview.GetChildCount() != 0)
            throw new InvalidOperationException("Ожидался один MultiMesh без узлов на каждый объект.");
        // The headless dummy renderer does not retain per-instance GPU transforms.
        if (DisplayServer.GetName() == "headless") return;
        int columns = (int)Math.Ceiling(Math.Sqrt(count));
        var last = preview.Multimesh.GetInstanceTransform(count - 1).Origin;
        if (last.X != (count - 1) % columns - (columns - 1) / 2f ||
            last.Z != (count - 1) / columns - (columns - 1) / 2f)
            throw new InvalidOperationException($"Последний маркер расположен неверно: {last}.");
    }
}
