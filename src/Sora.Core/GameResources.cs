using System.Text.Json;

namespace Sora.Core;

public sealed record ResolvedAsset(string Cab, SerializedDocument Document, SerializedObject Object);

public sealed class GameResources
{
    private sealed record Source(string Index, LogicalResource Resource);
    private sealed record CabSource(Source Source, VfsEntry Entry);
    private readonly Dictionary<string, Source> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Source> declarations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CabSource> cabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SerializedDocument> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> loaded = [];
    private readonly Dictionary<int, string[]> bundleCabs = [];
    private long decodedBytes;
    private readonly string sourceRoot;
    private int equivalentCopies, shadowedCopies, physicalFallbacks;
    public NativeManifest Manifest { get; }
    public object SourceSelectionPolicy => new { priority = "Persistent before StreamingAssets", equivalentCopies, shadowedCopies, physicalFallbacks, contentValidation = "selected native payload digest checked during extraction; search only checks indexed file ranges" };
    public string[] PhysicalChunkPaths => files.Values.Select(source=>Path.Combine(Path.GetDirectoryName(source.Index)!,source.Resource.Chunk)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public GameResources(string root)
    {
        OperationProgress.Report("read-game-index");
        root = Path.GetFullPath(root);
        string data = Directory.Exists(Path.Combine(root, "Endfield_Data")) ? Path.Combine(root, "Endfield_Data") : root;
        sourceRoot = data;
        var unavailable = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);
        var physicalSources = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string location in new[] { "Persistent", "StreamingAssets" })
            foreach (string block in new[] { "1CDDBF1F", "7064D8E2", "0CE8FA57", "775A31D1", "42A8FCA6" })
            {
                string indexPath = Path.Combine(data, location, "VFS", block, block + ".blc");
                OperationProgress.Report("read-game-index", detail: indexPath);
                if (!File.Exists(indexPath)) continue;
                var entries=BlockIndex.Read(indexPath).Resources;int indexed=0;
                foreach (var entry in entries)
                {
                    if(indexed++%4096==0)OperationProgress.Report("index-logical-resources",indexed-1,entries.Length,indexPath);
                    string name = Normalize(entry.Name);
                    declarations.TryAdd(name,new(indexPath,entry));
                    if (files.TryGetValue(name,out var chosen)) {
                        if(chosen.Resource.PayloadDigest==entry.PayloadDigest&&chosen.Resource.Length==entry.Length)equivalentCopies++;else shadowedCopies++;
                        continue;
                    }
                    string chunkKey = location + "/" + block + "/" + entry.Chunk;
                    if (!physicalSources.TryGetValue(chunkKey, out string? physicalIndex))
                    {
                        physicalIndex = new[] { location, location == "Persistent" ? "StreamingAssets" : "Persistent" }
                            .Select(x => Path.Combine(data, x, "VFS", block, block + ".blc"))
                            .FirstOrDefault(x => File.Exists(Path.Combine(Path.GetDirectoryName(x)!, entry.Chunk)));
                        physicalSources.Add(chunkKey, physicalIndex);
                    }
                    if (physicalIndex is null) { unavailable.TryAdd(name, new(indexPath, entry)); continue; }
                    if(physicalIndex!=indexPath)physicalFallbacks++;
                    if (unavailable.TryGetValue(name, out var newer))
                        if(!(newer.Resource.PayloadDigest.Length > 0 && newer.Resource.PayloadDigest == entry.PayloadDigest && newer.Resource.Length == entry.Length))
                        { shadowedCopies++; continue; } // retain the unavailable higher-priority declaration; never silently use stale bytes
                    files.Add(name, new(physicalIndex, entry));
                }
            }
        byte[] manifest = GetBytes("Bundles/Windows/manifest.hgmmap");
        Manifest = NativeManifest.Read(new MemoryStream(manifest, false));
    }

    public string[] MissingBundles(int root)
    {
        var queue = new Queue<int>(); var seen = new HashSet<int>(); var missing = new List<string>(); queue.Enqueue(root);
        while (queue.TryDequeue(out int index)) {
            if (!seen.Add(index)) continue;
            Validation.Require(index >= 0 && index < Manifest.Bundles.Length && seen.Count <= 4096, "Invalid or excessive bundle closure");
            var bundle = Manifest.Bundles[index];
            if (!HasLogicalResource("Bundles/Windows/" + bundle.Name)) missing.Add(bundle.Name);
            foreach (int dependency in bundle.Dependencies) queue.Enqueue(dependency);
        }
        return missing.ToArray();
    }

    public IReadOnlyList<string> LoadClosure(int root)
    {
        var queue = new Queue<int>(); var visited = new HashSet<int>(); queue.Enqueue(root);
        while (queue.TryDequeue(out int current))
        {
            OperationProgress.Report("resolve-dependencies", visited.Count, detail: "bundle:" + current);
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
        OperationProgress.Report("decode-serialized-data", documents.Count, detail: cab);
        var locator = GetCab(cab);
        Validation.Require(documents.Count < 256 && decodedBytes + locator.Entry.Size <= 512L * 1024 * 1024, "Decoded CAB cache limit exceeded");
        using var archive = new VfsArchive(BlockIndex.Extract(locator.Source.Index, locator.Source.Resource));
        var document = SerializedAssets.Decode(archive.Extract(locator.Entry.Name));
        documents.Add(cab, document); decodedBytes += locator.Entry.Size; return document;
    }

    public EndfieldResourceIndex SnapshotResourceIndex()
    {
        var sources = new List<ResourceFileRecord>();
        var sourceIds = new Dictionary<Source, int>();
        var rows = new List<CabResourceRecord>();
        foreach (var pair in cabs.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var location = pair.Value;
            if (!sourceIds.TryGetValue(location.Source, out int file))
            {
                file = sources.Count; sourceIds.Add(location.Source, file);
                sources.Add(new(Path.GetRelativePath(sourceRoot, location.Source.Index).Replace('\\', '/'),
                    location.Source.Resource with { Name = location.Source.Resource.Name.Replace('\\', '/'), Chunk = location.Source.Resource.Chunk.Replace('\\', '/') }));
            }
            rows.Add(documents.TryGetValue(pair.Key, out var document)
                ? ResourceIndexMetadata.Decoded(pair.Key, file, location.Entry, document)
                : new(pair.Key, "indexed", file, location.Entry, null, null, null));
        }
        var identities = rows.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string missing in rows.SelectMany(x => x.Dependencies ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Where(x => !identities.Contains(x)).Order(StringComparer.Ordinal).ToArray())
            rows.Add(new(missing, "unresolved", null, null, null, null, null));
        var result = new EndfieldResourceIndex(sourceRoot, Manifest.Hash, Manifest.Revision, sources.ToArray(), rows.ToArray());
        Validation.ResourceIndex(result);
        return result;
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

    public bool HasLogicalResource(string name)
    {
        if(!files.TryGetValue(Normalize(name),out var source))return false;
        var file=new FileInfo(Path.Combine(Path.GetDirectoryName(source.Index)!,source.Resource.Chunk));
        return file.Exists&&source.Resource.Offset>=0&&source.Resource.Length>=0&&source.Resource.Offset<=file.Length-source.Resource.Length;
    }
    public string[] LogicalNames => declarations.Keys.Order(StringComparer.Ordinal).ToArray();
    public ResourceFileRecord? DeclaredSource(string name) => declarations.TryGetValue(Normalize(name),out var source)?new(Path.GetRelativePath(sourceRoot,source.Index).Replace('\\','/'),source.Resource):null;
    public ResourceFileRecord LogicalSource(string name) { var source = GetSource(name); return new(Path.GetRelativePath(sourceRoot, source.Index).Replace('\\','/'), source.Resource); }
    public AddressResource SelectAddress(string path,string? hash=null)
        => GameCatalog.ChooseAlias(Manifest.Assets.Where(address=>address.Path==path&&(hash is null||address.Hash.ToString("x16").Equals(hash,StringComparison.OrdinalIgnoreCase))),bundle=>MissingBundles(bundle).Length==0);
    public ResolvedAsset ResolveHash(long hash, int classId)
    {
        var matches=Manifest.Assets.Where(a=>a.Hash==hash).DistinctBy(a=>(a.Hash,a.Path)).ToArray();
        Validation.Require(matches.Length==1,"Native resource hash absent or ambiguous: "+hash);return ResolveAddress(SelectAddress(matches[0].Path,matches[0].Hash.ToString("x16")),classId);
    }
    public ResolvedAsset ResolveAddress(AddressResource address, int classId)
    {
        var targets = new Dictionary<(string,long),ResolvedAsset>();
        foreach (var cab in LoadClosure(address.Bundle))
            foreach (var container in GetDocument(cab).Objects.Where(o => o.ClassId == 142))
            {
                var data = JsonSerializer.SerializeToElement(container.Data, WireJson.Options);
                bool hashTable = data.TryGetProperty("m_HashContainer", out var hashes) && hashes.GetProperty("Array").EnumerateArray().Any(r => r.GetProperty("first").GetInt64() == address.Hash);
                var rows = (hashTable ? hashes : data.GetProperty("m_Container")).GetProperty("Array");
                foreach (var row in rows.EnumerateArray())
                {
                    bool match = hashTable ? row.GetProperty("first").GetInt64() == address.Hash : row.GetProperty("first").GetString()!.Equals(address.Path,StringComparison.OrdinalIgnoreCase);
                    if (!match) continue;
                    var target = Resolve(cab,row.GetProperty("second").GetProperty("asset"));
                    if(target is not null && target.Object.ClassId == classId) targets.TryAdd((target.Cab,target.Object.Id),target);
                }
            }
        Validation.Require(targets.Count == 1,"Native resource target absent or ambiguous: " + address.Path + " hash " + address.Hash);
        return targets.Values.Single();
    }

    private CabSource GetCab(string cab) => cabs.TryGetValue(cab, out var source) ? source : throw new KeyNotFoundException("CAB is outside the loaded dependency closure: " + cab);
    private Source GetSource(string name) => files.TryGetValue(Normalize(name), out var source) ? source : throw new KeyNotFoundException("Logical game resource is absent: " + name);
    public byte[] GetBytes(string name) { var source = GetSource(name); return BlockIndex.Extract(source.Index, source.Resource); }
    private static string Leaf(string value) => value.Replace('\\', '/').Split('/')[^1];
    private static string Normalize(string name)
    {
        name = name.Replace('\\', '/');
        foreach (string prefix in new[] { "Assets/StreamingAssets/", "Data/" })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return name[prefix.Length..];
        return name;
    }
}
