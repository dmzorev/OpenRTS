using Godot;

namespace OpenRTS.Godot.Rendering;

public sealed class TerrainView
{
    public Node3D Node { get; } = new() { Name = "TerrainTiles" };
    public TerrainView(int width, int height, int tileSize, IEnumerable<string> terrainIds, Func<int, int, string> terrainAt, Func<string, Texture2D> textureFor)
    {
        foreach (string id in terrainIds)
        {
            var texture = textureFor(id) ?? throw new ArgumentException("Missing terrain resource: " + id);
            var resolved = SpriteBatch.Resolve(texture); var batch = new SpriteBatch(resolved.Texture, resolved.Region, false);
            batch.Node.Name = "Terrain_" + id;
            for (int y = 0; y < height / tileSize; y++)
                for (int x = 0; x < width / tileSize; x++)
                    if (terrainAt(x, y) == id)
                        batch.Add(new((x + 0.5f) * tileSize, 0, (y + 0.5f) * tileSize), 0,
                            new(tileSize, tileSize), Colors.White, new Color(1, 1, 1, 0));
            batch.Publish(); Node.AddChild(batch.Node);
        }
    }
}
