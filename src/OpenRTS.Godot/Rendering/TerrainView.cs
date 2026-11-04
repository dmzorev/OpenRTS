using Godot;
using OpenRTS.Content;

namespace OpenRTS.Godot.Rendering;

public sealed class TerrainView
{
    public Node3D Node { get; } = new() { Name = "TerrainTiles" };
    public TerrainView()
    {
        foreach (string id in ShowcaseScenario.TerrainIds)
        {
            var texture = GD.Load<Texture2D>("res://resources/terrain/" + id + ".tres") ?? throw new ArgumentException("Missing terrain resource: " + id);
            var resolved = SpriteBatch.Resolve(texture); var batch = new SpriteBatch(resolved.Texture, resolved.Region, false);
            batch.Node.Name = "Terrain_" + id;
            for (int y = 0; y < ShowcaseScenario.Height / ShowcaseScenario.TileSize; y++)
                for (int x = 0; x < ShowcaseScenario.Width / ShowcaseScenario.TileSize; x++)
                    if (ShowcaseScenario.TerrainAt(x, y) == id)
                        batch.Add(new((x + 0.5f) * ShowcaseScenario.TileSize, 0, (y + 0.5f) * ShowcaseScenario.TileSize), 0,
                            new(ShowcaseScenario.TileSize, ShowcaseScenario.TileSize), Colors.White, new Color(1, 1, 1, 0));
            batch.Publish(); Node.AddChild(batch.Node);
        }
    }
}
