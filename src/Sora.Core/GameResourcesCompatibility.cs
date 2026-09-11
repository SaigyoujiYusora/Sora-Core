namespace Sora.Core;

// Temporary extension used by the reconstructed native-prefab stage. The
// catalog stage replaces GameResources with the alias-aware instance method.
internal static class GameResourcesCompatibility
{
    public static AddressResource SelectAddress(this GameResources game, string path, string? hash = null)
    {
        var matches = game.Manifest.Assets.Where(asset => asset.Path == path &&
            (hash is null || asset.Hash.ToString("x16").Equals(hash, StringComparison.OrdinalIgnoreCase))).ToArray();
        Validation.Require(matches.Length == 1, "Native resource address is absent or ambiguous: " + path);
        return matches[0];
    }
}
