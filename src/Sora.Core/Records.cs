using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sora.Core;

public sealed record DatabaseDocument(string GameVersion, AssetRecord[] Assets, EndfieldResourceIndex? ResourceIndex = null);
public sealed record AssetRecord(string Id, string Label, string Detail, string Kind,
    string[] Dependencies, SceneDocument? Scene = null);
public sealed record SceneDocument(string Name, BoneRecord[] Bones, MeshRecord[] Meshes,
    MaterialRecord[] Materials, ClipRecord[] Clips, TextureRecord[]? Textures = null, TextureDescriptor[]? TextureDescriptors = null, HeadReferenceRecord? HeadReference = null, FaceDriverRecord? FaceDriver = null);
public sealed record HeadReferenceRecord(int Bone, string Name, string NativePath, double[] RestMatrix, string Status = "native-head-axes-unverified");
public sealed record BoneRecord(string Name, int Parent, double[] Head, double[] Tail, double Roll = 0, double[]? RestMatrix = null, string? SourcePath = null, uint? SourceHash = null);
public sealed record MaterialRecord(string Name, double[] BaseColor, double Metallic, double Roughness,
    string? BaseTexture = null, string? NormalTexture = null, bool AlphaClip = false, double AlphaCutoff = 0.5,
    bool Transparent = false, bool GrayAlpha = false, MaterialNprDescriptor? Npr = null);
public sealed record TextureRecord(string Name, int Width, int Height, bool Linear, byte[] Png);
public sealed record MeshRecord(string Name, double[][] Positions, int[][] Triangles,
    double[][] Normals, double[][] Uv, int Material, WeightRecord[] Weights, ShapeRecord[] Shapes,
    int[]? MaterialSlots = null, int[]? TriangleSlots = null, int SubmeshCount = 1, UvSetRecord[]? UvSets = null, double[][]? Tangents = null, double[][]? Colors = null, string? SourceId = null);
public sealed record UvSetRecord(int Set, double[][] Values, int? NativeDimension = null, int? NativeFormat = null);
public sealed record WeightRecord(int Vertex, int Bone, double Weight);
public sealed record ShapeRecord(string Name, double[][] Offsets);
public sealed record ClipRecord(string Name, double Duration, double Fps, TrackRecord[] Tracks, NativeAnimationMetadata? Native = null);
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

public sealed record NativeIdentity(string Cab, string PathId);
public sealed record MaterialSource(NativeIdentity MaterialId, NativeIdentity? ShaderId, string? ShaderName, string ResolutionStatus, NativeReference? ShaderSourceRef = null);
public sealed record PartEvidence(string? ShaderName, string? Discriminator = null, double? Value = null);
public sealed record NativeReference(int FileId, string PathId);
// Missing native ST/UV fields use scale [1,1], offset [0,0], UV0. Presence null means legacy metadata did not record presence.
public sealed record NprTextureBinding(NativeReference SourceRef, string? ResolvedId, string Status, double[] Scale, double[] Offset, int UvSet, string? TextureId, bool? ScalePresent = null, bool? OffsetPresent = null, bool? UvSetPresent = null);
public sealed record NprKeywords(string[] Valid, string[] Invalid, string[] Legacy);
public sealed record NprRenderState(int CustomRenderQueue, Dictionary<string,string> Tags, string[] DisabledPasses, bool DoubleSidedGI);
public sealed record NprDiagnostic(string Code, string? Property, string Message);
public sealed record MaterialNprDescriptor(int SchemaVersion, MaterialSource Source, string Part, PartEvidence PartEvidence,
    Dictionary<string,double> Floats, Dictionary<string,int> Ints, Dictionary<string,double[]> Colors,
    Dictionary<string,NprTextureBinding> Textures, NprKeywords Keywords, NprRenderState RenderState, NprDiagnostic[] Diagnostics);
public sealed record TextureDescriptor(string Id, NativeIdentity Source, string Dimension, int Width, int Height,
    int NativeFormat, string ColorSpace, string? WrapU, string? WrapV, string? Filter,
    string Channels, string AlphaMode, string? PayloadRef, string DecodeStatus);
