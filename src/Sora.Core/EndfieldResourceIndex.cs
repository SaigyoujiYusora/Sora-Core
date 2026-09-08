using System.Text.Json;

namespace Sora.Core;

// Paths are metadata, relative to SourceRoot. Reading an index never opens them.
// The snapshot covers the loaded closure, not every resource in the installation.
public sealed record EndfieldResourceIndex(string SourceRoot, string ManifestHash, string ManifestRevision,
    ResourceFileRecord[] Files, CabResourceRecord[] Cabs);
public sealed record ResourceFileRecord(string BlockIndexPath, LogicalResource Resource);
// Null metadata means uninspected, not an empty set. Unresolved rows have no locator.
public sealed record CabResourceRecord(string Id, string Status, int? File, VfsEntry? Entry,
    string[]? ContainerPaths, int[]? ClassIds, string[]? Dependencies);

public static class ResourceIndexMetadata
{
    public static CabResourceRecord Decoded(string id, int file, VfsEntry entry, SerializedDocument document)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in document.Objects.Where(x => x.ClassId == 142))
        {
            var data = JsonSerializer.SerializeToElement(item.Data, WireJson.Options);
            Validation.Require(data.ValueKind == JsonValueKind.Object && data.TryGetProperty("m_Container", out _), "Missing native container metadata");
            var container = data.GetProperty("m_Container");
            Validation.Require(container.ValueKind == JsonValueKind.Object && container.TryGetProperty("Array", out _), "Invalid native container metadata");
            var rows = container.GetProperty("Array");
            Validation.Require(rows.ValueKind == JsonValueKind.Array, "Invalid native container rows");
            foreach (var row in rows.EnumerateArray())
            {
                Validation.Require(row.ValueKind == JsonValueKind.Object && row.TryGetProperty("first", out var path) && path.ValueKind == JsonValueKind.String, "Invalid native container path");
                paths.Add(row.GetProperty("first").GetString()!);
            }
        }
        return new(id, "decoded", file, entry, paths.ToArray(),
            document.Objects.Select(x => x.ClassId).Distinct().Order().ToArray(),
            document.ExternalFiles.Select(CabLeaf).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray());
    }

    public static string CabLeaf(string value) => value.Replace('\\', '/').Split('/')[^1];
}
