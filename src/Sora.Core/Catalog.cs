namespace Sora.Core;

public sealed class Catalog
{
    private readonly Dictionary<string, AssetRecord> assets;

    public Catalog(DatabaseDocument database)
    {
        Validation.Database(database);
        assets = database.Assets.ToDictionary(x => x.Id, StringComparer.Ordinal);
    }

    public AssetRecord Get(string id) => assets.TryGetValue(id, out var asset) ? asset : throw new KeyNotFoundException("Asset not found: " + id);

    public object Search(string query, int offset, int limit)
    {
        Validation.Require(offset >= 0 && limit >= 1 && limit <= 1000, "Invalid result window");
        var found = assets.Values.Where(x => x.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        return new { total = found.Length, rows = found.Skip(offset).Take(limit).Select(x => new { x.Id, x.Label, x.Detail, x.Kind, hasScene = x.Scene is not null }).ToArray() };
    }

    public object Closure(string id)
    {
        Get(id);
        var visited = new SortedSet<string>(StringComparer.Ordinal);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(); queue.Enqueue(id);
        while (queue.TryDequeue(out var current))
        {
            if (!visited.Add(current)) continue;
            Validation.Require(visited.Count <= 1_000_000, "Dependency traversal limit exceeded");
            if (!assets.TryGetValue(current, out var asset)) { missing.Add(current); continue; }
            foreach (var dependency in asset.Dependencies) queue.Enqueue(dependency);
        }
        return new { assets = visited.Except(missing).ToArray(), missing = missing.ToArray() };
    }
}
