using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sora.Core;

public sealed record DatabaseDocument(string GameVersion, AssetRecord[] Assets);
public sealed record AssetRecord(string Id, string Label, string Detail, string Kind,
    string[] Dependencies, SceneDocument? Scene = null);
public sealed record SceneDocument(string Name, BoneRecord[] Bones, MeshRecord[] Meshes,
    MaterialRecord[] Materials, ClipRecord[] Clips, TextureRecord[]? Textures = null);
public sealed record BoneRecord(string Name, int Parent, double[] Head, double[] Tail, double Roll = 0, double[]? RestMatrix = null);
public sealed record MaterialRecord(string Name, double[] BaseColor, double Metallic, double Roughness,
    string? BaseTexture = null, string? NormalTexture = null, bool AlphaClip = false, double AlphaCutoff = 0.5,
    bool Transparent = false, bool GrayAlpha = false);
public sealed record TextureRecord(string Name, int Width, int Height, bool Linear, byte[] Png);
public sealed record MeshRecord(string Name, double[][] Positions, int[][] Triangles,
    double[][] Normals, double[][] Uv, int Material, WeightRecord[] Weights, ShapeRecord[] Shapes,
    int[]? MaterialSlots = null, int[]? TriangleSlots = null, int SubmeshCount = 1);
public sealed record WeightRecord(int Vertex, int Bone, double Weight);
public sealed record ShapeRecord(string Name, double[][] Offsets);
public sealed record ClipRecord(string Name, double Duration, double Fps, TrackRecord[] Tracks);
public sealed record TrackRecord(int Bone, string Channel, KeyRecord[] Keys);
public sealed record KeyRecord(double Time, double[] Value);

public static class WireJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 64
    };

    public static JsonDocument Parse(ReadOnlyMemory<byte> bytes)
    {
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        try { CheckKeys(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void CheckKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON key: " + property.Name);
                CheckKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckKeys(item);
    }
}
