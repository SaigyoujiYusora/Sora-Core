using Sora.Core;

internal static class SessionAnimationCatalog
{
    private static GameResources? metadataIdentity;
    private static string? character;
    private static NativeAnimationCatalogRow[]? rows;
    public static NativeAnimationCatalogRow[] Read(string root,string? characterPath)
    {
        if(!SessionDatabase.Enabled)return NativeAnimationCatalog.Discover(new GameResources(root),characterPath);
        var metadata=SessionGameResources.Read(root);
        if(ReferenceEquals(metadataIdentity,metadata)&&character==characterPath&&rows is not null)return rows;
        metadataIdentity=null;rows=null;var discovered=NativeAnimationCatalog.Discover(new GameResources(root),characterPath);
        metadataIdentity=metadata;character=characterPath;rows=discovered;return rows;
    }
}
