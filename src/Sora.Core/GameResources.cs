using System.Text.Json;

namespace Sora.Core;

public sealed record ResolvedAsset(string Cab, SerializedDocument Document, SerializedObject Object);

public sealed class GameResources
{
    private sealed record Source(string Index, LogicalResource Resource);
    private sealed record CabSource(Source Source, VfsEntry Entry);
    private readonly Dictionary<string, Source> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CabSource> cabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SerializedDocument> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> loaded = [];
    private readonly Dictionary<int, string[]> bundleCabs = [];
    private long decodedBytes;
    public NativeManifest Manifest { get; }

    public GameResources(string root)
    {
        root = Path.GetFullPath(root);
        string data = Directory.Exists(Path.Combine(root, "Endfield_Data")) ? Path.Combine(root, "Endfield_Data") : root;
        var unavailable = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);
        var physicalSources = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string location in new[] { "Persistent", "StreamingAssets" })
            foreach (string block in new[] { "1CDDBF1F", "7064D8E2", "0CE8FA57" })
            {
                string indexPath = Path.Combine(data, location, "VFS", block, block + ".blc");
                if (!File.Exists(indexPath)) continue;
                foreach (var entry in BlockIndex.Read(indexPath).Resources)
                {
                    string name = Normalize(entry.Name);
                    if (files.ContainsKey(name)) continue;
                    string chunkKey = location + "/" + block + "/" + entry.Chunk;
                    if (!physicalSources.TryGetValue(chunkKey, out string? physicalIndex))
                    {
                        physicalIndex = new[] { location, location == "Persistent" ? "StreamingAssets" : "Persistent" }
                            .Select(x => Path.Combine(data, x, "VFS", block, block + ".blc"))
                            .FirstOrDefault(x => File.Exists(Path.Combine(Path.GetDirectoryName(x)!, entry.Chunk)));
                        physicalSources.Add(chunkKey, physicalIndex);
                    }
                    if (physicalIndex is null) { unavailable.TryAdd(name, new(indexPath, entry)); continue; }
                    if (unavailable.TryGetValue(name, out var newer))
                        Validation.Require(newer.Resource.PayloadDigest.Length > 0 && newer.Resource.PayloadDigest == entry.PayloadDigest && newer.Resource.Length == entry.Length,
                            "Available resource differs from missing newer cache content: " + name);
                    files.Add(name, new(physicalIndex, entry));
                }
            }
        byte[] manifest = GetBytes("Bundles/Windows/manifest.hgmmap");
        Manifest = NativeManifest.Read(new MemoryStream(manifest, false));
    }

    public IReadOnlyList<string> LoadClosure(int root)
    {
        var queue = new Queue<int>(); var visited = new HashSet<int>(); queue.Enqueue(root);
        while (queue.TryDequeue(out int current))
        {
            if (!visited.Add(current)) continue;
            Validation.Require(current >= 0 && current < Manifest.Bundles.Length && visited.Count <= 4096, "Invalid or excessive bundle closure");
            var bundle = Manifest.Bundles[current];
            foreach (int dependency in bundle.Dependencies) queue.Enqueue(dependency);
            if (loaded.Contains(current)) continue;
            var source = GetSource("Bundles/Windows/" + bundle.Name);
            using var archive = new VfsArchive(BlockIndex.Extract(source.Index, source.Resource));
            var identities = new List<string>();
            foreach (var entry in archive.Entries.Where(x => x.Name.StartsWith("CAB-", StringComparison.OrdinalIgnoreCase) && x.Name.Length == 36))
            {
                var locator = new CabSource(source, entry);
                if (cabs.TryGetValue(entry.Name, out var previous))
                    Validation.Require(previous == locator, "CAB identity refers to multiple bundle locations");
                else cabs.Add(entry.Name, locator);
                identities.Add(entry.Name);
            }
            bundleCabs[current] = identities.ToArray(); loaded.Add(current);
        }
        return bundleCabs[root];
    }

    public SerializedDocument GetDocument(string cab)
    {
        cab = Leaf(cab);
        if (documents.TryGetValue(cab, out var known)) return known;
        var locator = GetCab(cab);
        Validation.Require(documents.Count < 256 && decodedBytes + locator.Entry.Size <= 512L * 1024 * 1024, "Decoded CAB cache limit exceeded");
        using var archive = new VfsArchive(BlockIndex.Extract(locator.Source.Index, locator.Source.Resource));
        var document = SerializedAssets.Decode(archive.Extract(locator.Entry.Name));
        documents.Add(cab, document); decodedBytes += locator.Entry.Size; return document;
    }

    public ResolvedAsset? Resolve(string ownerCab, JsonElement pointer)
    {
        long id = pointer.GetProperty("m_PathID").GetInt64();
        if (id == 0) return null;
        var owner = GetDocument(ownerCab);
        int file = pointer.GetProperty("m_FileID").GetInt32();
        Validation.Require(file >= 0 && file <= owner.ExternalFiles.Length, "Serialized reference file index is out of bounds");
        string cab = file == 0 ? Leaf(ownerCab) : Leaf(owner.ExternalFiles[file - 1]);
        var document = GetDocument(cab);
        var asset = document.Objects.SingleOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException($"Object {id} is absent from {cab}");
        return new(cab, document, asset);
    }

    public byte[] ReadResource(string ownerCab, string resourcePath, long offset, int length)
    {
        var locator = GetCab(Leaf(ownerCab));
        using var archive = new VfsArchive(BlockIndex.Extract(locator.Source.Index, locator.Source.Resource));
        string name = Leaf(resourcePath);
        var entry = archive.Entries.SingleOrDefault(x => x.Name == name) ?? throw new KeyNotFoundException("Streamed resource is absent: " + name);
        Validation.Require(offset >= 0 && length >= 0 && offset <= entry.Size - length, "Streamed resource range is invalid");
        var bytes = archive.Extract(name); return bytes.AsSpan((int)offset, length).ToArray();
    }

    private CabSource GetCab(string cab) => cabs.TryGetValue(cab, out var source) ? source : throw new KeyNotFoundException("CAB is outside the loaded dependency closure: " + cab);
    private Source GetSource(string name) => files.TryGetValue(Normalize(name), out var source) ? source : throw new KeyNotFoundException("Logical game resource is absent: " + name);
    private byte[] GetBytes(string name) { var source = GetSource(name); return BlockIndex.Extract(source.Index, source.Resource); }
    private static string Leaf(string value) => value.Replace('\\', '/').Split('/')[^1];
    private static string Normalize(string name)
    {
        name = name.Replace('\\', '/');
        foreach (string prefix in new[] { "Assets/StreamingAssets/", "Data/" })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return name[prefix.Length..];
        return name;
    }
}
