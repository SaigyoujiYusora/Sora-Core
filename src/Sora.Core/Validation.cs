namespace Sora.Core;

public static class Validation
{
    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static void Name(string? value) => Require(!string.IsNullOrWhiteSpace(value) && value.Length <= 4096 && !value.Any(char.IsControl), "Invalid name or identity");
    private static void Vector(double[]? value, int size)
        => Require(value is not null && value.Length == size && value.All(double.IsFinite), "Invalid numeric vector");

    public static void Database(DatabaseDocument database)
    {
        if (database.ResourceIndex is not null) ResourceIndex(database.ResourceIndex);
        Name(database.GameVersion);
        Require(database.Assets is not null && database.Assets.Length <= 1_000_000, "Invalid asset collection");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in database.Assets!)
        {
            Require(asset is not null, "Null asset");
            Name(asset!.Id); Name(asset.Label); Name(asset.Kind);
            Require(asset.Detail is not null && asset.Detail.Length <= 16384, "Invalid asset detail");
            Require(identities.Add(asset.Id), "Duplicate asset identity");
            Require(asset.Dependencies is not null && asset.Dependencies.Length <= 1_000_000, "Invalid dependencies");
            var edges = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dependency in asset.Dependencies!) { Name(dependency); Require(edges.Add(dependency), "Duplicate dependency"); }
            if (asset.Scene is not null) Scene(asset.Scene);
        }
    }

    public static void ResourceIndex(EndfieldResourceIndex index)
    {
        Name(index.SourceRoot); Name(index.ManifestHash);
        Require(index.ManifestRevision is not null && index.ManifestRevision.Length <= 4096 && !index.ManifestRevision.Any(char.IsControl), "Invalid manifest revision");
        Require(index.Files is not null && index.Files.Length <= 1_000_000 && index.Cabs is not null && index.Cabs.Length <= 1_000_000, "Invalid resource index collections");
        var fileKeys = new HashSet<(string, string)>();
        foreach (var file in index.Files!)
        {
            Require(file is not null && file.Resource is not null, "Null source file");
            RelativeMetadataPath(file!.BlockIndexPath);
            var source = file.Resource!;
            RelativeMetadataPath(source.Name); RelativeMetadataPath(source.Chunk);
            Require(source.Offset >= 0 && source.Length >= 0 && source.Offset <= long.MaxValue - source.Length, "Invalid logical resource range");
            Require(source.ChunkDigest is not null && source.PayloadDigest is not null && source.ChunkDigest.Length <= 4096 && source.PayloadDigest.Length <= 4096, "Invalid source digest");
            Require(fileKeys.Add((file.BlockIndexPath, source.Name)), "Duplicate source file");
        }
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cab in index.Cabs!)
        {
            Require(cab is not null, "Null CAB record"); Name(cab!.Id);
            Require(ids.Add(cab.Id), "Duplicate CAB identity");
            Require(cab.Status is "decoded" or "indexed" or "unresolved", "Unsupported CAB status");
            if (cab.Status == "unresolved") Require(cab.File is null && cab.Entry is null, "Unresolved CAB has a locator");
            else
            {
                Require(cab.File is >= 0 && cab.File < index.Files.Length && cab.Entry is not null, "Invalid CAB file relationship");
                Name(cab.Entry!.Name);
                Require(string.Equals(ResourceIndexMetadata.CabLeaf(cab.Entry.Name), cab.Id, StringComparison.OrdinalIgnoreCase), "CAB entry identity mismatch");
                Require(cab.Entry.Offset >= 0 && cab.Entry.Size >= 0 && cab.Entry.Offset <= long.MaxValue - cab.Entry.Size, "Invalid CAB entry range");
            }
            if (cab.Status != "decoded")
                Require(cab.ContainerPaths is null && cab.ClassIds is null && cab.Dependencies is null, "Uninspected CAB has decoded metadata");
            else
            {
                Require(cab.ContainerPaths is not null && cab.ClassIds is not null && cab.Dependencies is not null, "Decoded CAB lacks metadata");
                UniqueNames(cab.ContainerPaths!); UniqueNames(cab.Dependencies!, StringComparer.OrdinalIgnoreCase);
                Require(cab.ClassIds!.Length <= 1_000_000 && cab.ClassIds.Distinct().Count() == cab.ClassIds.Length, "Invalid class ID set");
            }
        }
        foreach (var cab in index.Cabs)
            foreach (string dependency in cab.Dependencies ?? []) Require(ids.Contains(dependency), "CAB dependency lacks resolved or unresolved record");
    }

    private static void UniqueNames(string[] values, StringComparer? comparer = null)
    {
        Require(values.Length <= 1_000_000, "Resource metadata limit exceeded");
        var unique = new HashSet<string>(comparer ?? StringComparer.Ordinal);
        foreach (var value in values) { Name(value); Require(unique.Add(value), "Duplicate resource metadata"); }
    }

    private static void RelativeMetadataPath(string value)
    {
        Name(value);
        Require(!value.StartsWith('/') && !value.Contains('\\') && !value.Contains(':') && value.Split('/').All(x => x.Length > 0 && x is not "." and not ".."), "Source path must be a relative metadata path");
    }

    public static void Scene(SceneDocument scene)
    {
        Name(scene.Name);
        Require(scene.Bones is not null && scene.Meshes is not null && scene.Materials is not null && scene.Clips is not null, "Missing scene collections");
        Require(scene.Bones!.Length <= 4096 && scene.Meshes!.Length <= 4096 && scene.Materials!.Length <= 4096 && scene.Clips!.Length <= 16384, "Scene collection limit exceeded");
        long totalVertices = 0, totalTriangles = 0, totalKeys = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < scene.Bones!.Length; index++)
        {
            var bone = scene.Bones[index]; Require(bone is not null, "Null bone");
            if(bone!.SourcePath is not null && bone.SourcePath.Length!=0) Name(bone.SourcePath);
            Name(bone.Name); Require(names.Add(bone.Name), "Duplicate bone name");
            Require(System.Text.Encoding.UTF8.GetByteCount(bone.Name) <= 63, "Bone name exceeds Blender's UTF-8 limit");
            Require(bone.Parent >= -1 && bone.Parent < index, "Skeleton must be parent before child");
            Vector(bone.Head, 3); Vector(bone.Tail, 3);
            Require(double.IsFinite(bone.Roll) && Math.Abs(bone.Roll) <= Math.PI * 2, "Invalid bone roll");
            if (bone.RestMatrix is not null)
            {
                Vector(bone.RestMatrix, 16);
                Require(Math.Abs(bone.RestMatrix[12]) + Math.Abs(bone.RestMatrix[13]) + Math.Abs(bone.RestMatrix[14]) + Math.Abs(bone.RestMatrix[15] - 1) < 1e-5, "Rest matrix must be affine");
                Require(Enumerable.Range(0,3).All(axis => Math.Abs(bone.RestMatrix[axis * 4 + 3] - bone.Head[axis]) < 1e-4), "Rest matrix and bone head disagree");
                for (int axis = 0; axis < 3; axis++)
                    for (int other = 0; other < 3; other++)
                        Require(Math.Abs(Enumerable.Range(0,3).Sum(row => bone.RestMatrix[row * 4 + axis] * bone.RestMatrix[row * 4 + other]) - (axis == other ? 1 : 0)) < 1e-4, "Rest matrix basis must be orthonormal");
            }
            Require(bone.Head.Zip(bone.Tail, (a, b) => (a - b) * (a - b)).Sum() > 1e-16, "Zero length bone");
        }
        if(scene.HeadReference is { } head) {
            Require(head.Bone>=0 && head.Bone<scene.Bones.Length,"Invalid head bone index"); Name(head.Name); Name(head.NativePath); Vector(head.RestMatrix,16);
            var bone=scene.Bones[head.Bone];
            Require(head.Name==bone.Name && head.NativePath==bone.SourcePath && bone.RestMatrix is not null && head.RestMatrix.SequenceEqual(bone.RestMatrix),"Head reference provenance mismatch");
            Require(head.Status=="native-head-axes-unverified","Unsupported head axes status");
        }
        names.Clear();
        var textureNames = new HashSet<string>(StringComparer.Ordinal);
        long textureBytes = 0;
        Require(scene.Textures is null || scene.Textures.Length <= 256, "Texture count limit exceeded");
        foreach (var texture in scene.Textures ?? [])
        {
            Require(texture is not null, "Null texture"); Name(texture!.Name); Require(textureNames.Add(texture.Name), "Duplicate texture name");
            Require(texture.Width >= 1 && texture.Height >= 1 && texture.Width <= 16384 && texture.Height <= 16384 && (long)texture.Width * texture.Height <= 16777216, "Texture dimensions exceed limit");
            Require(texture.Png is not null && texture.Png.Length >= 33 && texture.Png.Length <= 64 * 1024 * 1024, "Invalid PNG payload size");
            textureBytes += texture.Png!.Length; Require(textureBytes <= 192L * 1024 * 1024, "Scene texture payload limit exceeded");
            Require(texture.Png.AsSpan(0, 8).SequenceEqual(new byte[] {137, 80, 78, 71, 13, 10, 26, 10}) && texture.Png.AsSpan(12, 4).SequenceEqual("IHDR"u8), "Invalid PNG header");
            Require(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(texture.Png.AsSpan(16)) == texture.Width && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(texture.Png.AsSpan(20)) == texture.Height, "PNG dimensions disagree with texture record");
        }
        var descriptorIds = new HashSet<string>(StringComparer.Ordinal);
        Require(scene.TextureDescriptors is null || scene.TextureDescriptors.Length <= 512, "Texture descriptor limit exceeded");
        foreach(var descriptor in scene.TextureDescriptors ?? []) {
            Require(descriptor is not null,"Null texture descriptor"); Name(descriptor!.Id); Require(descriptorIds.Add(descriptor.Id),"Duplicate texture descriptor"); Identity(descriptor.Source);
            Require(descriptor.Dimension is "2D" or "Cube" or "Unknown", "Invalid texture dimension");
            Require(descriptor.Width >= 0 && descriptor.Height >= 0 && descriptor.Width <= 16384 && descriptor.Height <= 16384, "Invalid descriptor dimensions");
            Name(descriptor.ColorSpace); Name(descriptor.Channels); Name(descriptor.AlphaMode);
            foreach(var sampler in new[]{descriptor.WrapU,descriptor.WrapV,descriptor.Filter}) if(sampler is not null) Name(sampler);
            Require(descriptor.DecodeStatus is "decoded" or "unsupported", "Invalid decode status");
            if(descriptor.NativeFormat is 24 or 26 or 27) Require(descriptor.Channels==(descriptor.NativeFormat==24?"RGB":descriptor.NativeFormat==26?"R":"RG") && descriptor.AlphaMode=="none","Native channel semantics mismatch");
            if(descriptor.NativeFormat==24) Require(descriptor.DecodeStatus=="unsupported" && descriptor.PayloadRef is null,"BC6H HDR cannot use 8-bit PNG transport");
            Require(descriptor.PayloadRef is null || textureNames.Contains(descriptor.PayloadRef), "Missing descriptor payload");
            Require((descriptor.DecodeStatus == "decoded") == (descriptor.PayloadRef is not null), "Decode payload status mismatch");
            if(descriptor.PayloadRef is not null) { var payload = scene.Textures!.Single(x=>x.Name==descriptor.PayloadRef); Require(payload.Width==descriptor.Width && payload.Height==descriptor.Height,"Descriptor dimensions disagree"); }
        }
        foreach (var material in scene.Materials!)
        {
            Require(material is not null, "Null material"); Name(material!.Name);
            if(material.Npr is not null) Npr(material.Npr, descriptorIds, textureNames);
            Require(names.Add(material.Name), "Duplicate material name"); Vector(material.BaseColor, 4);
            Require(material.BaseColor.All(x => x >= 0 && x <= 1), "Color outside unit range");
            Require(double.IsFinite(material.Metallic) && material.Metallic >= 0 && material.Metallic <= 1 &&
                double.IsFinite(material.Roughness) && material.Roughness >= 0 && material.Roughness <= 1, "Invalid material factors");
            Require(material.BaseTexture is null || textureNames.Contains(material.BaseTexture), "Missing base texture");
            Require(material.NormalTexture is null || textureNames.Contains(material.NormalTexture), "Missing normal texture");
            Require(double.IsFinite(material.AlphaCutoff) && material.AlphaCutoff >= 0 && material.AlphaCutoff <= 1, "Invalid alpha cutoff");
        }
        names.Clear();
        foreach (var mesh in scene.Meshes!)
        {
            Require(mesh is not null, "Null mesh"); Name(mesh!.Name); Require(names.Add(mesh.Name), "Duplicate mesh name");
            Require(mesh.Positions is not null && mesh.Triangles is not null && mesh.Normals is not null && mesh.Uv is not null && mesh.Weights is not null && mesh.Shapes is not null, "Missing mesh collections");
            totalVertices += mesh.Positions!.Length; totalTriangles += mesh.Triangles!.Length;
            Require(totalVertices <= 5_000_000 && totalTriangles <= 10_000_000 && mesh.Weights!.Length <= 20_000_000 && mesh.Shapes!.Length <= 512, "Mesh collection limit exceeded");
            foreach (var position in mesh.Positions!) Vector(position, 3);
            foreach (var triangle in mesh.Triangles!) Require(triangle is not null && triangle.Length == 3 && triangle.Distinct().Count() == 3 && triangle.All(x => x >= 0 && x < mesh.Positions.Length), "Invalid triangle");
            Require(mesh.Normals!.Length == 0 || mesh.Normals.Length == mesh.Positions.Length, "Normal count mismatch");
            foreach (var normal in mesh.Normals) { Vector(normal, 3); Require(Math.Abs(normal.Sum(x => x * x) - 1) < 1e-4, "Normal must be normalized"); }
            Require(mesh.Uv!.Length == 0 || mesh.Uv.Length == mesh.Positions.Length, "UV count mismatch");
            foreach (var uv in mesh.Uv) Vector(uv, 2);
            if(mesh.SourceId is not null) Name(mesh.SourceId);
            if(mesh.UvSets is not null) {
                Require(mesh.UvSets.Length<=8,"UV set limit exceeded"); var sets=new HashSet<int>();
                foreach(var set in mesh.UvSets) { Require(set is not null && set.Set>=0 && set.Set<8 && sets.Add(set.Set),"Invalid UV set"); Require(set!.Values is not null && set.Values.Length==mesh.Positions.Length,"UV set count mismatch"); Require(set.NativeDimension is null || (set.NativeDimension >= 0 && set.NativeDimension <= 255 && (set.NativeDimension & 15) is >= 1 and <= 4), "Invalid native UV dimension"); Require(set.NativeFormat is null || set.NativeFormat is >= 0 and <= 11,"Invalid native UV format"); foreach(var row in set.Values!) { Require(row is not null && row.Length>=1 && row.Length<=4,"Invalid UV row"); Vector(row,row!.Length); } }
            }
            if(mesh.Tangents is not null) { Require(mesh.Tangents.Length==mesh.Positions.Length,"Tangent count mismatch"); foreach(var row in mesh.Tangents) {Vector(row,4); Require(Math.Abs(row.Take(3).Sum(x=>x*x)-1)<1e-4 && Math.Abs(Math.Abs(row[3])-1)<1e-4,"Invalid tangent");} }
            if(mesh.Colors is not null) { Require(mesh.Colors.Length==mesh.Positions.Length,"Color count mismatch"); foreach(var row in mesh.Colors) Vector(row,4); }
            Require(mesh.Material >= -1 && mesh.Material < scene.Materials.Length, "Invalid material reference");
            Require((mesh.MaterialSlots is null) == (mesh.TriangleSlots is null), "Material slots and triangle slots must be supplied together");
            if (mesh.MaterialSlots is not null)
            {
                Require(mesh.SubmeshCount >= 1 && mesh.SubmeshCount <= mesh.MaterialSlots.Length, "Invalid submesh count");
                Require(mesh.MaterialSlots.Length >= 1 && mesh.MaterialSlots.Length <= 4096 && mesh.MaterialSlots.All(x => x >= -1 && x < scene.Materials.Length), "Invalid mesh material slots");
                Require(mesh.TriangleSlots!.Length == mesh.Triangles.Length && mesh.TriangleSlots.All(x => x >= 0 && x < mesh.SubmeshCount), "Invalid triangle material slots");
            }
            var pairs = new HashSet<(int, int)>();
            var sums = new Dictionary<int, double>();
            foreach (var weight in mesh.Weights!)
            {
                Require(weight is not null, "Null weight");
                Require(weight!.Vertex >= 0 && weight.Vertex < mesh.Positions.Length && weight.Bone >= 0 && weight.Bone < scene.Bones.Length && double.IsFinite(weight.Weight) && weight.Weight > 0 && weight.Weight <= 1, "Invalid skin weight");
                Require(pairs.Add((weight.Vertex, weight.Bone)), "Duplicate skin influence");
                sums[weight.Vertex] = sums.GetValueOrDefault(weight.Vertex) + weight.Weight;
            }
            Require(sums.Values.All(x => Math.Abs(x - 1) < 1e-5), "Skin weights must sum to one");
            var shapeNames = new HashSet<string>(StringComparer.Ordinal) { "Basis" };
            foreach (var shape in mesh.Shapes!)
            {
                Require(shape is not null, "Null shape"); Name(shape!.Name); Require(shapeNames.Add(shape.Name), "Duplicate or reserved shape name");
                Require(shape.Offsets is not null && shape.Offsets.Length == mesh.Positions.Length, "Shape vertex count mismatch");
                foreach (var offset in shape.Offsets!) Vector(offset, 3);
            }
        }
        names.Clear();
        foreach (var clip in scene.Clips!)
        {
            Require(clip is not null, "Null clip"); Name(clip!.Name); Require(names.Add(clip.Name), "Duplicate clip name");
            Require(double.IsFinite(clip.Duration) && clip.Duration >= 0 && clip.Duration <= 86400 && double.IsFinite(clip.Fps) && clip.Fps >= 1 && clip.Fps <= 240, "Invalid clip timing");
            Require(clip.Tracks is not null, "Missing tracks");
            Require(clip.Tracks!.Length <= 12288, "Track limit exceeded");
            var targets = new HashSet<(int, string)>();
            foreach (var track in clip.Tracks!)
            {
                Require(track is not null && track.Bone >= 0 && track.Bone < scene.Bones.Length, "Invalid track bone");
                Require(track!.Channel is "location" or "rotation" or "scale", "Unknown track channel");
                Require(targets.Add((track.Bone, track.Channel)), "Duplicate track");
                Require(track.Keys is not null, "Missing keys");
                totalKeys += track.Keys!.Length; Require(totalKeys <= 10_000_000, "Animation key limit exceeded");
                double previous = -1;
                foreach (var key in track.Keys!)
                {
                    Require(key is not null, "Null key");
                    Require(double.IsFinite(key!.Time) && key.Time >= 0 && key.Time <= clip.Duration && key.Time > previous, "Key times must increase within clip");
                    Vector(key.Value, track.Channel == "rotation" ? 4 : 3);
                    if (track.Channel == "rotation") Require(Math.Abs(key.Value.Sum(x => x * x) - 1) < 1e-4, "Quaternion must be normalized");
                    previous = key.Time;
                }
            }
        }
    }
    static void Identity(NativeIdentity? id) { Require(id is not null,"Missing native identity"); Name(id!.Cab); PathId(id.PathId); }
    static void PathId(string? id) => Require(id is not null && long.TryParse(id, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture,out _),"Invalid signed path ID");
    static void Map<T>(Dictionary<string,T>? map) { Require(map is not null && map.Count<=4096,"Invalid NPR map"); foreach(var key in map!.Keys) Name(key); }
    static void StringList(string[]? items) { Require(items is not null && items.Length<=4096,"Invalid NPR strings"); foreach(var item in items!) Name(item); }
    static void Npr(MaterialNprDescriptor npr,HashSet<string> descriptors,HashSet<string> payloads) {
        Require(npr.SchemaVersion==1,"Unsupported NPR schema"); Require(npr.Source is not null,"Missing NPR source"); Identity(npr.Source!.MaterialId); if(npr.Source.ShaderId is not null) Identity(npr.Source.ShaderId);
        if(npr.Source.ShaderName is not null) Name(npr.Source.ShaderName);
        Require(npr.Source.ResolutionStatus is "null" or "missing" or "resolved" or "unsupported","Invalid shader resolution");
        if(npr.Source.ShaderSourceRef is { } shaderRef) {
            PathId(shaderRef.PathId); Require(shaderRef.FileId>=0,"Invalid shader file ID");
            Require((npr.Source.ResolutionStatus=="null")== (long.Parse(shaderRef.PathId,System.Globalization.CultureInfo.InvariantCulture)==0),"Shader pointer status mismatch");
        }
        Require(npr.Source.ResolutionStatus is not ("null" or "missing") || (npr.Source.ShaderId is null && npr.Source.ShaderName is null),"Unresolved shader has resolved provenance");
        Require(npr.Source.ResolutionStatus is not ("resolved" or "unsupported") || npr.Source.ShaderId is not null,"Missing resolved shader identity");
        Require(npr.Part is "Standard" or "Face" or "Eyes" or "Hair" or "Fur" or "Eyebrow" or "VFX" or "OverlayShadow" or "LiquidAg" or "Unknown","Invalid NPR part");
        Require(npr.PartEvidence is not null,"Missing part evidence"); if(npr.PartEvidence!.ShaderName is not null) Name(npr.PartEvidence.ShaderName); if(npr.PartEvidence.Discriminator is not null) Name(npr.PartEvidence.Discriminator); Require(npr.PartEvidence.Value is null || double.IsFinite(npr.PartEvidence.Value.Value),"Invalid discriminator");
        Map(npr.Floats); Require(npr.Floats.Values.All(double.IsFinite),"Nonfinite NPR float"); Map(npr.Ints); Map(npr.Colors); foreach(var c in npr.Colors.Values) Vector(c,4);
        Map(npr.Textures); foreach(var binding in npr.Textures.Values) {
            Require(binding is not null && binding.SourceRef is not null,"Missing texture binding"); PathId(binding!.SourceRef!.PathId); Require(binding.SourceRef.FileId>=0,"Invalid texture file ID"); Vector(binding.Scale,2); Vector(binding.Offset,2); Require(binding.UvSet>=0 && binding.UvSet<8,"Invalid texture UV set");
            Require(binding.ScalePresent!=false || binding.Scale.SequenceEqual(new double[]{1,1}),"Absent scale must use documented identity fallback");
            Require(binding.OffsetPresent!=false || binding.Offset.SequenceEqual(new double[]{0,0}),"Absent offset must use documented zero fallback");
            Require(binding.UvSetPresent!=false || binding.UvSet==0,"Absent UV set must use documented UV0 fallback");
            Require(binding.Status is "null" or "missing" or "resolved" or "unsupported","Invalid binding status");
            Require((binding.Status=="null")== (binding.SourceRef.PathId=="0"),"Null binding mismatch");
            if(binding.ResolvedId is not null) Name(binding.ResolvedId);
            Require(binding.TextureId is null || descriptors.Contains(binding.TextureId),"Missing binding descriptor");
            Require(binding.Status!="resolved" || (binding.ResolvedId is not null && binding.TextureId is not null && payloads.Contains(binding.TextureId)),"Missing resolved payload");
            Require(binding.Status is not ("null" or "missing") || (binding.TextureId is null && binding.ResolvedId is null),"Unresolved binding has payload");
        }
        Require(npr.Keywords is not null && npr.RenderState is not null,"Missing NPR metadata"); StringList(npr.Keywords!.Valid); StringList(npr.Keywords.Invalid); StringList(npr.Keywords.Legacy); Map(npr.RenderState!.Tags); foreach(var value in npr.RenderState.Tags.Values) Require(value is not null && value.Length<=4096,"Invalid tag"); StringList(npr.RenderState.DisabledPasses);
        Require(npr.Diagnostics is not null && npr.Diagnostics.Length<=4096,"Invalid NPR diagnostics"); foreach(var d in npr.Diagnostics!) { Require(d is not null,"Null diagnostic"); Name(d!.Code); if(d.Property is not null) Name(d.Property); Require(d.Message is not null && d.Message.Length<=16384,"Invalid diagnostic message"); }
    }

}
