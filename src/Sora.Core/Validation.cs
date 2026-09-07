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
            Name(bone!.Name); Require(names.Add(bone.Name), "Duplicate bone name");
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
        foreach (var material in scene.Materials!)
        {
            Require(material is not null, "Null material"); Name(material!.Name);
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
}
