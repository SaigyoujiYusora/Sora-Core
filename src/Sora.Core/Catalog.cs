namespace Sora.Core;

public sealed class Catalog
{
    private readonly Dictionary<string, AssetRecord> assets;
    private readonly DatabaseDocument database;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DatabaseDocument, Catalog> Cache = new();
    public static Catalog For(DatabaseDocument database) => Cache.GetValue(database, value => new Catalog(value));

    public Catalog(DatabaseDocument database)
    {
        Validation.Database(database);
        this.database = database;
        assets = database.Assets.ToDictionary(x => x.Id, StringComparer.Ordinal);
    }

    public AssetRecord Get(string id) => assets.TryGetValue(id, out var asset) ? asset : throw new KeyNotFoundException("Asset not found: " + id);

    public object Search(string query, int offset, int limit, GameResources? game = null, string? kind = null)
    {
        Validation.Require(offset >= 0 && limit >= 1 && limit <= 1000, "Invalid result window");
        var found = assets.Values.Where(x => x.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Detail.Contains(query, StringComparison.OrdinalIgnoreCase) || (x.Locator?.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) || (x.Metadata?.InternalName.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) || (x.Metadata?.DisplayNameEn?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(x => string.IsNullOrEmpty(kind) || x.Kind == kind)
            .OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        return new { total = found.Length, rows = found.Skip(offset).Take(limit).Select(x => new { x.Id, x.Label, x.Detail, x.Kind, hasScene = x.Scene is not null, capability = GameCatalog.Capability(x, database, game), x.Locator, x.Metadata, databaseMode = database.CatalogSource is null ? "standalone-scene" : "indexed-game" }).ToArray() };
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
