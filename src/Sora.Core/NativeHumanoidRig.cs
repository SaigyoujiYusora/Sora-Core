using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.Json;

namespace Sora.Core;

public sealed record NativeHumanAxes(Quaternion Pre, Quaternion Post, Vector3 Sign, Vector3 Minimum, Vector3 Maximum);
public sealed record NativeHumanNode(int Parent, uint PathHash, string Path, Vector3 Translation, Quaternion Rotation, int Axes);

/// <summary>Native human skeleton; identities are full Avatar paths and hashes, never UI bone indices.</summary>
public sealed class NativeHumanoidRig
{
    public ReadOnlyCollection<NativeHumanNode> Nodes { get; }
    public ReadOnlyCollection<NativeHumanAxes> Axes { get; }
    public ReadOnlyCollection<int> HumanNodes { get; }
    public ReadOnlyCollection<float> Masses { get; }
    public ReadOnlyDictionary<uint, string> SourcePaths { get; }
    public ReadOnlyDictionary<uint, uint?> SourceParents { get; }
    public float HumanScale { get; }
    public Vector4 TwistPolicy { get; }

    public NativeHumanoidRig(NativeHumanNode[] nodes, NativeHumanAxes[] axes, int[] humanNodes, float[] masses,
        float humanScale, Vector4 twistPolicy, IReadOnlyDictionary<uint, string>? sourcePaths = null,
        IReadOnlyDictionary<uint, uint?>? sourceParents = null)
    {
        Validation.Require(nodes is not null && nodes.Length is > 0 and <= 4096 && axes is not null && axes.Length <= 4096, "Unsupported or empty native human skeleton");
        Validation.Require(humanNodes is not null && humanNodes.Length == 25 && masses is not null && masses.Length == 25, "Expected native 25-slot human schema");
        Validation.Require(twistPolicy == new Vector4(1, 0, 1, 0) || twistPolicy == Vector4.Zero, "Unsupported humanoid twist policy; expected arm/forearm/upper-leg/leg 1/0/1/0 or 0/0/0/0");
        Validation.Require(float.IsFinite(humanScale) && humanScale > 0, "Invalid native human scale");
        var identities = new HashSet<uint>(); var paths = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < nodes!.Length; i++)
        {
            var node = nodes[i]; Validation.Require(node is not null, "Null human node");
            Validation.Require(node!.Parent >= -1 && node.Parent < i && (i == 0 || node.Parent >= 0), "Human skeleton must have one parent-before-child root");
            Validation.Require(node.Axes >= -1 && node.Axes < axes!.Length, "Invalid human axes reference");
            Validation.Require(node.Path is not null && node.Path.Length <= 4096 && !node.Path.Any(char.IsControl), "Invalid human bone path");
            Validation.Require(identities.Add(node.PathHash) && paths.Add(node.Path!), "Duplicate human path or hash");
            Finite(node.Translation); Rotation(node.Rotation);
        }
        foreach (var axis in axes!)
        {
            Validation.Require(axis is not null, "Null human axes"); Rotation(axis!.Pre); Rotation(axis.Post); Finite(axis.Sign); Finite(axis.Minimum); Finite(axis.Maximum);
            Validation.Require(axis.Minimum.X <= 0 && axis.Minimum.Y <= 0 && axis.Minimum.Z <= 0 && axis.Maximum.X >= 0 && axis.Maximum.Y >= 0 && axis.Maximum.Z >= 0, "Invalid human angular limits");
        }
        var assigned = new HashSet<int>();
        for (int slot = 0; slot < 25; slot++)
        {
            int node = humanNodes![slot]; Validation.Require(node >= -1 && node < nodes.Length && (slot >= 22 || node >= 0), "Missing core humanoid body node");
            Validation.Require(node < 0 || assigned.Add(node), "Duplicate human bone assignment");
            Validation.Require(float.IsFinite(masses![slot]) && masses[slot] >= 0, "Invalid human bone mass");
            Validation.Require(slot < 22 || masses[slot] == 0, "Unsupported eye or jaw body mass");
        }
        Validation.Require(masses!.Sum() > 0 && float.IsFinite(masses!.Sum()), "Invalid total human mass");
        Nodes = Array.AsReadOnly(nodes.Select(x => x with { Rotation = Quaternion.Normalize(x.Rotation) }).ToArray());
        Axes = Array.AsReadOnly(axes.Select(x => x with { Pre = Quaternion.Normalize(x.Pre), Post = Quaternion.Normalize(x.Post) }).ToArray());
        HumanNodes = Array.AsReadOnly((int[])humanNodes!.Clone()); Masses = Array.AsReadOnly((float[])masses!.Clone()); HumanScale = humanScale; TwistPolicy = twistPolicy;
        var allPaths = sourcePaths is null ? nodes.ToDictionary(x => x.PathHash, x => x.Path) : sourcePaths.ToDictionary(x => x.Key, x => x.Value);
        foreach (var node in nodes) { if (node.PathHash == 0 && node.Path == "" && !allPaths.ContainsKey(0)) allPaths.Add(0, ""); Validation.Require(allPaths.GetValueOrDefault(node.PathHash) == node.Path, "Human node and source Avatar paths disagree"); }
        Validation.Require(allPaths.Count <= 16384 && allPaths.Values.All(x => x is not null && x.Length <= 4096 && !x.Any(char.IsControl)), "Invalid source Avatar paths");
        SourcePaths = new(allPaths);
        var parents = sourceParents is null
            ? nodes.ToDictionary(x => x.PathHash, x => x.Parent < 0 ? (uint?)null : nodes[x.Parent].PathHash)
            : sourceParents.ToDictionary(x => x.Key, x => x.Value);
        Validation.Require(parents.Count <= 16384 && parents.All(x => allPaths.ContainsKey(x.Key)
            && (!x.Value.HasValue || x.Value != x.Key && allPaths.ContainsKey(x.Value.Value))), "Invalid native source parent identities");
        SourceParents = new(parents);
    }

    public static NativeHumanoidRig Parse(JsonElement avatar)
    {
        var human = avatar.GetProperty("m_Avatar").GetProperty("m_Human").GetProperty("data");
        var skeleton = human.GetProperty("m_Skeleton").GetProperty("data");
        var nodes = ArrayField(skeleton, "m_Node", 4096); var hashes = ArrayField(skeleton, "m_ID", 4096);
        var poses = ArrayField(human.GetProperty("m_SkeletonPose").GetProperty("data"), "m_X", 4096);
        Validation.Require(nodes.Length == hashes.Length && nodes.Length == poses.Length, "Native human skeleton arrays disagree");
        var paths = new Dictionary<uint, string>();
        foreach (var pair in ArrayField(avatar, "m_TOS", 16384)) Validation.Require(paths.TryAdd(pair.GetProperty("first").GetUInt32(), pair.GetProperty("second").GetString()!), "Duplicate Avatar path hash");
        var fullSkeleton = avatar.GetProperty("m_Avatar").GetProperty("m_AvatarSkeleton").GetProperty("data");
        var fullNodes = ArrayField(fullSkeleton, "m_Node", 16384);
        var fullHashes = ArrayField(fullSkeleton, "m_ID", 16384);
        Validation.Require(fullNodes.Length == fullHashes.Length, "Native source hierarchy arrays disagree");
        var sourceParents = new Dictionary<uint, uint?>();
        for (int i = 0; i < fullNodes.Length; i++)
        {
            int parent = fullNodes[i].GetProperty("m_ParentId").GetInt32();
            Validation.Require(parent >= -1 && parent < i && (i == 0 || parent >= 0), "Invalid native source hierarchy");
            Validation.Require(sourceParents.TryAdd(fullHashes[i].GetUInt32(), parent < 0 ? null : fullHashes[parent].GetUInt32()),
                "Duplicate native source hierarchy identity");
        }
        var records = new NativeHumanNode[nodes.Length];
        for (int i = 0; i < records.Length; i++)
        {
            uint hash = hashes[i].GetUInt32();
            Validation.Require(paths.TryGetValue(hash, out string? path) || hash == 0, "Human path hash is absent from Avatar TOS");
            Validation.Require(Vector3.Distance(Vector(poses[i].GetProperty("s")), Vector3.One) <= 0.001f, "Scaled native human pose is unsupported");
            records[i] = new(nodes[i].GetProperty("m_ParentId").GetInt32(), hash, path ?? "", Vector(poses[i].GetProperty("t")), QuaternionValue(poses[i].GetProperty("q")), nodes[i].GetProperty("m_AxesId").GetInt32());
        }
        var axes = ArrayField(skeleton, "m_AxesArray", 4096).Select(x => new NativeHumanAxes(QuaternionValue(x.GetProperty("m_PreQ")), QuaternionValue(x.GetProperty("m_PostQ")), Vector(x.GetProperty("m_Sgn")), Vector(x.GetProperty("m_Limit").GetProperty("m_Min")), Vector(x.GetProperty("m_Limit").GetProperty("m_Max")))).ToArray();
        return new(records, axes, ArrayField(human, "m_HumanBoneIndex", 25).Select(x => x.GetInt32()).ToArray(), ArrayField(human, "m_HumanBoneMass", 25).Select(x => x.GetSingle()).ToArray(), human.GetProperty("m_Scale").GetSingle(), new(human.GetProperty("m_ArmTwist").GetSingle(), human.GetProperty("m_ForeArmTwist").GetSingle(), human.GetProperty("m_UpperLegTwist").GetSingle(), human.GetProperty("m_LegTwist").GetSingle()), paths, sourceParents);
    }

    internal static JsonElement[] ArrayField(JsonElement parent, string field, int limit)
    {
        var value = parent.GetProperty(field).GetProperty("Array");
        Validation.Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= limit, "Invalid or oversized native human array: " + field);
        return value.EnumerateArray().ToArray();
    }
    internal static Vector3 Vector(JsonElement x) => new(x.GetProperty("x").GetSingle(), x.GetProperty("y").GetSingle(), x.GetProperty("z").GetSingle());
    internal static Quaternion QuaternionValue(JsonElement x) => new(x.GetProperty("x").GetSingle(), x.GetProperty("y").GetSingle(), x.GetProperty("z").GetSingle(), x.GetProperty("w").GetSingle());
    internal static void Finite(Vector3 x) => Validation.Require(float.IsFinite(x.X) && float.IsFinite(x.Y) && float.IsFinite(x.Z), "Nonfinite native human vector");
    internal static void Rotation(Quaternion x) => Validation.Require(float.IsFinite(x.LengthSquared()) && x.LengthSquared() > 1e-12f, "Invalid native human quaternion");
}
