using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;

namespace Sora.Core;

public static class CharacterGeometry
{
    private static JsonElement Array(JsonElement parent, string name) => parent.GetProperty(name).GetProperty("Array");
    private static JsonElement BoundedArray(JsonElement parent, string name, int limit)
    {
        var value = Array(parent, name);
        Validation.Require(value.GetArrayLength() <= limit, name + " count limit exceeded");
        return value;
    }
    private static float Number(JsonElement value, string name) => value.GetProperty(name).GetSingle();
    private static Vector3 Vector(JsonElement value) => new(Number(value, "x"), Number(value, "y"), Number(value, "z"));
    private static double[] Values(Vector3 value) => [value.X, value.Y, value.Z];
    private static double[] MatrixValues(Matrix4x4 m) => [m.M11,m.M21,m.M31,m.M41,m.M12,m.M22,m.M32,m.M42,m.M13,m.M23,m.M33,m.M43,m.M14,m.M24,m.M34,m.M44];
    private static Matrix4x4 Bind(JsonElement m) => new(Number(m,"e00"),Number(m,"e10"),Number(m,"e20"),Number(m,"e30"),Number(m,"e01"),Number(m,"e11"),Number(m,"e21"),Number(m,"e31"),Number(m,"e02"),Number(m,"e12"),Number(m,"e22"),Number(m,"e32"),Number(m,"e03"),Number(m,"e13"),Number(m,"e23"),Number(m,"e33"));
    private static Matrix4x4 Local(JsonElement value)
    {
        var rotation = value.GetProperty("q");
        var quaternion = new Quaternion(Number(rotation, "x"), Number(rotation, "y"), Number(rotation, "z"), Number(rotation, "w"));
        return Matrix4x4.CreateScale(Vector(value.GetProperty("s"))) * Matrix4x4.CreateFromQuaternion(quaternion) * Matrix4x4.CreateTranslation(Vector(value.GetProperty("t")));
    }

    public static DatabaseDocument Convert(SerializedDocument source, string identity, int lod = 0, bool allowEmptyMeshes = false, bool allowAuxiliaryScale = false)
    {
        Validation.Require(lod >= 0 && lod <= 3, "LOD must be between zero and three");
        var avatars = source.Objects.Where(x => x.ClassId == 90).Take(2).ToArray();
        Validation.Require(avatars.Length == 1, "Character geometry requires one Avatar");
        var avatar = JsonSerializer.SerializeToElement(avatars[0].Data, WireJson.Options);
        var definition = avatar.GetProperty("m_Avatar");
        var skeleton = definition.GetProperty("m_AvatarSkeleton").GetProperty("data");
        var nodes = BoundedArray(skeleton, "m_Node", 4096).EnumerateArray().ToArray();
        var pathHashes = BoundedArray(skeleton, "m_ID", 4096).EnumerateArray().Select(x => x.GetUInt32()).ToArray();
        var poses = BoundedArray(definition.GetProperty("m_DefaultPose").GetProperty("data"), "m_X", 4096).EnumerateArray().ToArray();
        Validation.Require(nodes.Length == poses.Length && nodes.Length == pathHashes.Length && nodes.Length <= 4096, "Avatar skeleton arrays disagree");
        var paths = BoundedArray(avatar, "m_TOS", 16384).EnumerateArray().ToDictionary(x => x.GetProperty("first").GetUInt32(), x => x.GetProperty("second").GetString()!);
        var world = new Matrix4x4[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            int parent = nodes[i].GetProperty("m_ParentId").GetInt32();
            Validation.Require(parent >= -1 && parent < i, "Avatar parents must precede children");
            world[i] = Local(poses[i]) * (parent < 0 ? Matrix4x4.Identity : world[parent]);
        }
        var selected = source.Objects.Where(x => x.ClassId == 43).Select(x => (x.Id, Mesh: JsonSerializer.SerializeToElement(x.Data, WireJson.Options)))
            .Where(x => x.Mesh.GetProperty("m_Name").GetString()!.EndsWith("_lod" + lod, StringComparison.OrdinalIgnoreCase)).Take(4097).ToArray();
        Validation.Require((selected.Length > 0 || allowEmptyMeshes) && selected.Length <= 4096, "Selected mesh count is empty or exceeds limit");
        var anchors = new Dictionary<int, Matrix4x4>();
        foreach (var entry in selected)
        {
            var mesh = entry.Mesh;
            string name = mesh.GetProperty("m_Name").GetString()!;
            var owners = Enumerable.Range(0, nodes.Length).Where(i => paths.GetValueOrDefault(pathHashes[i], "").Split('/')[^1] == name).ToArray();
            Validation.Require(owners.Length == 1, "Mesh transform is absent or ambiguous");
            var palette = BoundedArray(mesh, "m_BoneNameHashes", 4096).EnumerateArray().ToArray();
            var binds = BoundedArray(mesh, "m_BindPose", 4096).EnumerateArray().ToArray();
            Validation.Require(palette.Length == binds.Length, "Bind palette and bone hashes disagree");
            for (int i = 0; i < palette.Length; i++)
            {
                var joints = Enumerable.Range(0, nodes.Length).Where(j => pathHashes[j] == palette[i].GetUInt32()).ToArray();
                Validation.Require(joints.Length == 1, "Mesh bone hash is absent or ambiguous");
                Validation.Require(Matrix4x4.Invert(Bind(binds[i]), out var inverseBind), "Singular mesh bind matrix");
                var candidate = inverseBind * world[owners[0]];
                if (anchors.TryGetValue(joints[0], out var previous))
                    Validation.Require(MatrixValues(candidate).Zip(MatrixValues(previous), (x,y) => Math.Abs(x-y)).Max() < 0.001, "Meshes disagree about a shared bone bind basis");
                else anchors.Add(joints[0], candidate);
            }
        }
        var restWorld = new Matrix4x4[nodes.Length];
        // Unity point coordinates become (-x, -z, y). A matching reflection
        // of each bone's local X axis keeps its exported frame right handed.
        var localReflection = Matrix4x4.CreateScale(-1, 1, 1);
        var space = localReflection * Matrix4x4.CreateRotationX(MathF.PI / 2);
        var bones = new BoneRecord[nodes.Length];
        var boneNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < nodes.Length; i++)
        {
            int parent = nodes[i].GetProperty("m_ParentId").GetInt32();
            Validation.Require(parent >= -1 && parent < i, "Avatar parents must precede children");
            restWorld[i] = anchors.TryGetValue(i, out var authoritative) ? authoritative : Local(poses[i]) * (parent < 0 ? Matrix4x4.Identity : restWorld[parent]);
            var rest = localReflection * restWorld[i] * space;
            Validation.Require(Matrix4x4.Decompose(rest, out var scale, out var rotation, out var head), "Invalid avatar rest matrix");
            Validation.Require(Vector3.Distance(scale, Vector3.One) < 0.001 || (allowAuxiliaryScale && !anchors.ContainsKey(i)), "Scaled avatar rest bones require a separate conversion: " + identity + " node " + i + " scale " + scale);
            var axis = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, rotation));
            var shortest = axis.Y < -0.999999f ? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI) : Quaternion.Normalize(new Quaternion(axis.Z, 0, -axis.X, 1 + axis.Y));
            var twist = Quaternion.Normalize(Quaternion.Conjugate(shortest) * rotation);
            double roll = Math.IEEERemainder(2 * Math.Atan2(twist.Y, twist.W), 2 * Math.PI);
            string path = paths.GetValueOrDefault(pathHashes[i], "");
            string name = path.Length == 0 ? "SceneRoot" : path.Split('/')[^1];
            if (!boneNames.Add(name)) { name = name + "_" + i; Validation.Require(boneNames.Add(name), "Duplicate avatar bone identity"); }
            var normalizedRest = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)) * Matrix4x4.CreateTranslation(head);
            bones[i] = new(name, parent, Values(head), Values(head + axis * 0.05f), roll, MatrixValues(normalizedRest), SourcePath: path, SourceHash: pathHashes[i]);
        }
        var meshes = new List<MeshRecord>();
        long totalVertices = 0, totalTriangles = 0;
        foreach (var entry in selected)
        {
            var mesh = entry.Mesh;
            string name = mesh.GetProperty("m_Name").GetString()!;
            var meshNode = Enumerable.Range(0, nodes.Length).Where(i => paths.GetValueOrDefault(pathHashes[i], "").Split('/')[^1] == name).ToArray();
            Validation.Require(meshNode.Length == 1, "Mesh transform is missing or ambiguous: " + name);
            var transform = world[meshNode[0]] * space;
            Validation.Require(Matrix4x4.Invert(transform, out var inverse), "Singular mesh transform");
            var normalTransform = Matrix4x4.Transpose(inverse);
            var vertex = mesh.GetProperty("m_VertexData");
            int count = vertex.GetProperty("m_VertexCount").GetInt32();
            totalVertices += count;
            Validation.Require(count >= 0 && totalVertices <= 5_000_000, "Mesh vertex limit exceeded");
            var channels = BoundedArray(vertex, "m_Channels", 32).EnumerateArray().ToArray();
            Validation.Require(channels.Length >= 14, "Mesh vertex channel layout is incomplete");
            byte[] bytes = vertex.GetProperty("m_DataSize").GetBytesFromBase64();
            int[] widths = [4, 2, 1, 1, 2, 2, 1, 1, 2, 2, 4, 4];
            var strides = new int[8]; var bases = new int[8];
            foreach (var channel in channels)
            {
                int dimension = channel.GetProperty("dimension").GetInt32() & 15;
                if (dimension == 0) continue;
                int stream = channel.GetProperty("stream").GetInt32(), offset = channel.GetProperty("offset").GetInt32(), format = channel.GetProperty("format").GetInt32();
                Validation.Require(stream >= 0 && stream < 8 && offset >= 0 && format >= 0 && format < widths.Length, "Unsupported vertex channel");
                strides[stream] = Math.Max(strides[stream], offset + widths[format] * dimension);
            }
            int end = 0;
            for (int stream = 0; stream < 8; stream++)
            {
                if (strides[stream] == 0) continue;
                end = checked((end + 15) & ~15); bases[stream] = end; end = checked(end + count * strides[stream]);
            }
            Validation.Require(end <= bytes.Length, "Vertex buffer does not cover channel streams");
            int Address(int channel, int index)
            {
                int stream = channels[channel].GetProperty("stream").GetInt32();
                return checked(bases[stream] + strides[stream] * index + channels[channel].GetProperty("offset").GetInt32());
            }
            double Scalar(int channel, int index, int component)
            {
                var info = channels[channel]; int format = info.GetProperty("format").GetInt32();
                int address = Address(channel, index) + widths[format] * component;
                Validation.Require(address >= 0 && address <= bytes.Length - widths[format], "Vertex attribute exceeds buffer");
                var span = bytes.AsSpan(address);
                return format switch {
                    0 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(span)),
                    1 => (double)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(span)),
                    2 => span[0] / 255.0, 3 => Math.Max(-1, unchecked((sbyte)span[0]) / 127.0),
                    4 => BinaryPrimitives.ReadUInt16LittleEndian(span) / 65535.0,
                    5 => Math.Max(-1, BinaryPrimitives.ReadInt16LittleEndian(span) / 32767.0),
                    6 => span[0], 7 => unchecked((sbyte)span[0]), 8 => BinaryPrimitives.ReadUInt16LittleEndian(span),
                    9 => BinaryPrimitives.ReadInt16LittleEndian(span), 10 => BinaryPrimitives.ReadUInt32LittleEndian(span),
                    11 => BinaryPrimitives.ReadInt32LittleEndian(span), _ => throw new InvalidDataException("Unsupported attribute format")
                };
            }
            Validation.Require((channels[0].GetProperty("dimension").GetInt32() & 15) == 3, "Position channel must have three components");
            var positions = new double[count][]; var normals = new double[count][];
            bool hasUv = (channels[4].GetProperty("dimension").GetInt32() & 15) >= 2;
            var uv = hasUv ? new double[count][] : [];
            var uvSets = Enumerable.Range(0, 8)
                .Where(set => (channels[4 + set].GetProperty("dimension").GetInt32() & 15) != 0)
                .Select(set => new UvSetRecord(set, new double[count][], channels[4 + set].GetProperty("dimension").GetInt32(), channels[4 + set].GetProperty("format").GetInt32())).ToArray();
            foreach (var set in uvSets)
                Validation.Require((channels[4 + set.Set].GetProperty("dimension").GetInt32() & 15) is >= 1 and <= 4, "Unsupported UV component count");
            int colorDimension = channels[3].GetProperty("dimension").GetInt32() & 15;
            int tangentDimension = channels[2].GetProperty("dimension").GetInt32() & 15;
            Validation.Require(colorDimension is 0 or 3 or 4, "Unsupported vertex color encoding");
            Validation.Require(tangentDimension is 0 or 4, "Unsupported tangent encoding");
            double[][]? colors = colorDimension == 0 ? null : new double[count][];
            double[][]? tangents = tangentDimension == 0 ? null : new double[count][];
            var hashes = Array(mesh, "m_BoneNameHashes").EnumerateArray().Select(x => x.GetUInt32()).ToArray();
            var joints = hashes.Select(hash => {
                var candidates = Enumerable.Range(0, pathHashes.Length).Where(i => pathHashes[i] == hash).ToArray();
                Validation.Require(candidates.Length == 1, "Mesh bone hash is absent or ambiguous"); return candidates[0];
            }).ToArray();
            int influences = mesh.GetProperty("m_BonesPerVertex").GetInt32();
            Validation.Require(influences is 1 or 2 or 4, "Variable bone influences are not yet supported");
            var weights = new List<WeightRecord>();
            for (int vertexIndex = 0; vertexIndex < count; vertexIndex++)
            {
                positions[vertexIndex] = Values(Vector3.Transform(new((float)Scalar(0, vertexIndex, 0), (float)Scalar(0, vertexIndex, 1), (float)Scalar(0, vertexIndex, 2)), transform));
                int normalDimension = channels[1].GetProperty("dimension").GetInt32();
                Vector3 normal;
                if (normalDimension == 0x31)
                    normal = Octahedral(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(Address(1, vertexIndex))));
                else {
                    Validation.Require((normalDimension & 15) == 3, "Unsupported normal encoding");
                    normal = new((float)Scalar(1, vertexIndex, 0), (float)Scalar(1, vertexIndex, 1), (float)Scalar(1, vertexIndex, 2));
                }
                normals[vertexIndex] = Values(Vector3.Normalize(Vector3.TransformNormal(normal, normalTransform)));
                if (hasUv) uv[vertexIndex] = [Scalar(4, vertexIndex, 0), Scalar(4, vertexIndex, 1)];
                foreach (var set in uvSets)
                    set.Values[vertexIndex] = Enumerable.Range(0, channels[4 + set.Set].GetProperty("dimension").GetInt32() & 15)
                        .Select(component => Scalar(4 + set.Set, vertexIndex, component)).ToArray();
                if (colors is not null)
                    colors[vertexIndex] = [Scalar(3, vertexIndex, 0), Scalar(3, vertexIndex, 1), Scalar(3, vertexIndex, 2), colorDimension == 4 ? Scalar(3, vertexIndex, 3) : 1];
                if (tangents is not null)
                {
                    var tangent = Vector3.TransformNormal(new((float)Scalar(2, vertexIndex, 0), (float)Scalar(2, vertexIndex, 1), (float)Scalar(2, vertexIndex, 2)), transform);
                    var convertedNormal = new Vector3((float)normals[vertexIndex][0], (float)normals[vertexIndex][1], (float)normals[vertexIndex][2]);
                    tangent = Vector3.Normalize(tangent - convertedNormal * Vector3.Dot(tangent, convertedNormal));
                    double sign = Scalar(2, vertexIndex, 3);
                    Validation.Require(Math.Abs(Math.Abs(sign) - 1) < 0.0001, "Invalid tangent handedness");
                    tangents[vertexIndex] = [tangent.X, tangent.Y, tangent.Z, Math.Sign(sign) * Math.Sign(transform.GetDeterminant())];
                }
                var vertexWeights = new Dictionary<int, double>();
                for (int component = 0; component < influences; component++)
                {
                    double weight = influences == 1 ? 1 : Scalar(12, vertexIndex, component);
                    if (weight <= 0) continue;
                    int localBone = checked((int)Scalar(13, vertexIndex, component));
                    Validation.Require(localBone >= 0 && localBone < joints.Length, "Skin index outside mesh bind palette");
                    int joint = joints[localBone]; vertexWeights[joint] = vertexWeights.GetValueOrDefault(joint) + weight;
                }
                double sum = vertexWeights.Values.Sum(); Validation.Require(double.IsFinite(sum) && sum > 0, "Vertex has no valid skin influence");
                foreach (var weight in vertexWeights) weights.Add(new(vertexIndex, weight.Key, weight.Value / sum));
            }
            byte[] indices = Array(mesh, "m_IndexBuffer").GetBytesFromBase64();
            int indexFormat = mesh.GetProperty("m_IndexFormat").GetInt32();
            Validation.Require(indexFormat is 0 or 1, "Unsupported mesh index format");
            int indexWidth = indexFormat == 0 ? 2 : 4;
            var triangles = new List<int[]>();
            var triangleSlots = new List<int>(); int slot = 0;
            foreach (var submesh in BoundedArray(mesh, "m_SubMeshes", 4096).EnumerateArray())
            {
                Validation.Require(submesh.GetProperty("topology").GetInt32() == 0, "Only triangle submeshes are supported");
                int start = submesh.GetProperty("firstByte").GetInt32(), indexCount = submesh.GetProperty("indexCount").GetInt32(), baseVertex = submesh.GetProperty("baseVertex").GetInt32();
                Validation.Require(start >= 0 && start % indexWidth == 0 && indexCount >= 0 && indexCount % 3 == 0 && (long)start + (long)indexCount * indexWidth <= indices.Length, "Invalid submesh index range");
                totalTriangles += indexCount / 3;
                Validation.Require(totalTriangles <= 10_000_000, "Scene triangle limit exceeded");
                for (int triangle = 0; triangle < indexCount; triangle += 3)
                {
                    int[] corners = new int[3];
                    for (int corner = 0; corner < 3; corner++) {
                        var span = indices.AsSpan(start + (triangle + corner) * indexWidth);
                        corners[corner] = checked(baseVertex + (indexWidth == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(span) : (int)BinaryPrimitives.ReadUInt32LittleEndian(span)));
                    }
                    if (transform.GetDeterminant() < 0) (corners[1], corners[2]) = (corners[2], corners[1]);
                    if (corners.Distinct().Count() == 3) { triangles.Add(corners); triangleSlots.Add(slot); }
                }
                slot++;
            }
            Validation.Require(slot > 0, "Mesh has no submeshes");
            meshes.Add(new(name, positions, triangles.ToArray(), normals, uv, -1, weights.ToArray(), [], Enumerable.Repeat(-1, slot).ToArray(), triangleSlots.ToArray(), slot,
                UvSets: uvSets, Tangents: tangents, Colors: colors, SourceId: identity + ":" + entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        HeadReferenceRecord? headReference = null;
        var heads = Enumerable.Range(0, bones.Length).Where(i => bones[i].SourcePath!.Split('/')[^1] == "Bip001_Head").Take(2).ToArray();
        if (heads.Length == 1)
        {
            var head = bones[heads[0]];
            headReference = new(heads[0], head.Name, head.SourcePath!, head.RestMatrix!, "native-head-axes-unverified");
        }
        var scene = new SceneDocument(avatar.GetProperty("m_Name").GetString()!, bones, meshes.ToArray(), [], [], HeadReference: headReference);
        var database = new DatabaseDocument(source.UnityVersion, [new(identity, scene.Name, "Native geometry; materials and animation are not included", "character", [], scene)]);
        Validation.Database(database); return database;
    }

    public static Vector3 Octahedral(uint packed)
    {
        double Component(int shift) { int value = (int)((packed >> shift) & 1023); return (value >= 512 ? value - 1024 : value) / 511.0; }
        double x = Component(0), y = Component(10), z = 1 - Math.Abs(x) - Math.Abs(y);
        double fold = Math.Max(0, -z);
        return Vector3.Normalize(new((float)(x - Math.CopySign(fold, x)), (float)(y - Math.CopySign(fold, y)), (float)z));
    }
}
