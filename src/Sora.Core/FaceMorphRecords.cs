namespace Sora.Core;

public sealed record FaceDriverRecord(int Version, string SourcePath, string SourceCab, long SourceObject,
    string AvatarCab, long AvatarObject, string RotationOrder, int[] RotationSigns, double RestFitDegrees,
    FaceBoneRecord[] Bones, FaceControlRecord[] Controls, FacePresetRecord[] Presets,
    FaceMappingSourceRecord[]? AdditionalSources = null,
    int? PrimaryBoneCount = null, int? PrimaryControlCount = null);
public sealed record FaceMappingSourceRecord(string SourcePath, string SourceCab, long SourceObject,
    string AvatarCab, long AvatarObject, int DataType, double RestFitDegrees,
    int BoneStart, int BoneCount, int ControlStart, int ControlCount);
public sealed record FaceBoneRecord(int NativeId, int NameHash, string NativePath, int SceneBone,
    double[] Position, double[] Rotation, double[] Scale);
public sealed record FaceDeltaRecord(int Bone, double[] Position, double[] Rotation, double[] Scale);
public sealed record FaceShaderRecord(string Parameter, int RendererMask, double DefaultValue, int BlendMode, int VectorIndex);
public sealed record FaceControlRecord(string Name, int Id, int NameHash, int TagHash, int PartType,
    FaceDeltaRecord[] Bones, FaceShaderRecord? Shader = null);
public sealed record FacePresetRecord(string Name, string SourcePath, bool Additive,
    Dictionary<string, double> Weights, string[] MissingControls);
