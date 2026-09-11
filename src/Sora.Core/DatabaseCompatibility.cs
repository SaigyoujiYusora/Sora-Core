namespace Sora.Core;

public sealed record DatabaseCompatibility(int ContractVersion, string Mode, string DatabaseVersion,
    bool VersionMatches, int AssetCount, int CachedSceneCount, bool AllAssetsHaveCachedScenes,
    bool CanImportCachedScenes, bool CanResolveIndexedAssets);

public static class DatabaseCompatibilityPolicy
{
    public static DatabaseCompatibility Inspect(DatabaseDocument database, string gameVersion, bool catalogMatches)
    {
        int count = database.Assets.Count(asset => asset.Scene is not null);
        bool indexed = database.CatalogSource is not null;
        return new(1, indexed ? "indexed-game" : count > 0 ? "standalone-scene" : "legacy-snapshot",
            database.GameVersion, database.GameVersion == gameVersion, database.Assets.Length, count,
            database.Assets.Length > 0 && count == database.Assets.Length,
            count > 0 && (!indexed || catalogMatches), indexed && catalogMatches);
    }
}
