using Godot;

namespace OpenRTS.Godot.Rendering;

/// <summary>Draws visual tile IDs independently of the simulation's terrain types.</summary>
public sealed class TerrainView
{
    public Node3D Node { get; } = new() { Name = "TerrainTiles" };

    public TerrainView(int width, int height, int tileSize, IEnumerable<string> terrainIds, Func<int, int, string> terrainAt, Func<string, Texture2D> textureFor)
    {
        if (tileSize <= 0 || width <= 0 || height <= 0 || width % tileSize != 0 || height % tileSize != 0)
            throw new ArgumentException("Terrain dimensions must be positive multiples of tile size.");
        var batches = new Dictionary<string, SpriteBatch>(StringComparer.Ordinal);
        foreach (string id in terrainIds)
        {
            var texture = textureFor(id) ?? throw new ArgumentException("Missing terrain resource: " + id);
            var resolved = SpriteBatch.Resolve(texture);
            var batch = new SpriteBatch(resolved.Texture, resolved.Region, false);
            batch.Node.Name = "Terrain_" + id;
            batches.Add(id, batch);
        }
        for (int y = 0; y < height / tileSize; y++)
            for (int x = 0; x < width / tileSize; x++)
            {
                string id = terrainAt(x, y);
                if (!batches.TryGetValue(id, out var batch)) throw new ArgumentException("Unknown visual tile: " + id);
                batch.Add(new((x + 0.5f) * tileSize, 0, (y + 0.5f) * tileSize), 0,
                    new(tileSize, tileSize), Colors.White, new Color(1, 1, 1, 0));
            }
        foreach (var batch in batches.Values)
        {
            batch.Publish();
            Node.AddChild(batch.Node);
        }
    }
}
