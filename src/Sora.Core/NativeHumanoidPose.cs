using System.Numerics;

namespace Sora.Core;

public sealed record NativeHumanoidFrame(Vector3 MotionTranslation, Quaternion MotionRotation, Vector3 RootTranslation,
    Quaternion RootRotation, float[] BodyMuscles, float[] FingerMuscles);
public sealed record NativeHumanRotation(int HumanSlot, uint PathHash, string Path, Quaternion Rotation);
public sealed record NativeHumanoidResult(NativeHumanRotation[] Body, uint HipsHash, string HipsPath, Vector3 HipsTranslation, Quaternion HipsRotation);

/// <summary>Independent native61 body/root evaluation. No IK override or finger-muscle solver.</summary>
public static class NativeHumanoidPose
{
    private static readonly (int Bone, int Axis)[] Channels = MakeChannels();
    private static (int, int)[] MakeChannels()
    {
        var result = new List<(int, int)>(); void Add(int bone, params int[] axes) { foreach (int axis in axes) result.Add((bone, axis)); }
        foreach (int bone in new[] { 7, 8, 9, 10, 11 }) Add(bone, 2, 1, 0);
        foreach (int bone in new[] { 22, 23, 24 }) Add(bone, 2, 1);
        foreach (var side in new[] { new[] { 1, 3, 5, 20 }, new[] { 2, 4, 6, 21 } }) { Add(side[0], 2, 1, 0); Add(side[1], 2, 0); Add(side[2], 2, 1, 0); Add(side[3], 1, 2, 0); }
        foreach (var side in new[] { new[] { 12, 14, 16, 18 }, new[] { 13, 15, 17, 19 } }) { Add(side[0], 2, 1); Add(side[1], 2, 1, 0); Add(side[2], 2, 0); Add(side[3], 2, 1); }
        return result.ToArray();
    }

    public static NativeHumanoidResult Evaluate(NativeHumanoidRig rig, NativeHumanoidFrame frame)
    {
        Validation.Require(frame.BodyMuscles is not null && frame.BodyMuscles.Length == 61 && frame.BodyMuscles.All(float.IsFinite), "Expected 61 finite Endfield body muscles");
        Validation.Require(frame.FingerMuscles is not null && frame.FingerMuscles.Length == 40 && frame.FingerMuscles.All(x => x == 0), "Nonzero finger muscles require an independent finger solver");
        NativeHumanoidRig.Finite(frame.MotionTranslation); NativeHumanoidRig.Finite(frame.RootTranslation); NativeHumanoidRig.Rotation(frame.MotionRotation); NativeHumanoidRig.Rotation(frame.RootRotation);
        var local = Local(rig, frame.BodyMuscles!); var world = World(rig, local); var neutral = World(rig, Local(rig, new float[61]));
        Quaternion body = Quaternion.Normalize(BodyFrame(rig, world.Position) * Quaternion.Conjugate(BodyFrame(rig, neutral.Position)));
        int hips = rig.HumanNodes[0]; Quaternion inverseMotion = Quaternion.Conjugate(Quaternion.Normalize(frame.MotionRotation));
        Quaternion relativeRoot = inverseMotion * Quaternion.Normalize(frame.RootRotation);
        Quaternion rotation = Quaternion.Normalize(relativeRoot * Quaternion.Conjugate(body) * world.Rotation[hips]);
        Quaternion net = Quaternion.Normalize(rotation * Quaternion.Conjugate(world.Rotation[hips]));
        Vector3 position = Vector3.Transform(frame.RootTranslation - frame.MotionTranslation, inverseMotion) - Vector3.Transform(Center(rig, world.Position) - world.Position[hips], net);
        NativeHumanoidRig.Finite(position); NativeHumanoidRig.Rotation(rotation);
        var output = local.Select(x => new NativeHumanRotation(x.Key, rig.Nodes[rig.HumanNodes[x.Key]].PathHash, rig.Nodes[rig.HumanNodes[x.Key]].Path, x.Value)).OrderBy(x => x.HumanSlot).ToArray();
        return new(output, rig.Nodes[hips].PathHash, rig.Nodes[hips].Path, position, rotation);
    }

    private static Dictionary<int, Quaternion> Local(NativeHumanoidRig rig, ReadOnlySpan<float> muscles)
    {
        var angles = new Vector3[25]; var active = new bool[25];
        for (int i = 0; i < Channels.Length; i++)
        {
            var (bone, axis) = Channels[i]; int node = rig.HumanNodes[bone];
            if (node < 0) { Validation.Require(muscles[i] == 0, "Muscle targets an absent native human bone"); continue; }
            int index = rig.Nodes[node].Axes;
            Validation.Require(index >= 0, "Human muscle node lacks angular axes"); var spec = rig.Axes[index]; float input = muscles[i];
            float value = input * (input < 0 ? -Component(spec.Minimum, axis) : Component(spec.Maximum, axis)) * Component(spec.Sign, axis);
            Validation.Require(float.IsFinite(value), "Human muscle angle overflow");
            if (axis == 0) angles[bone].X = value; else if (axis == 1) angles[bone].Y = value; else angles[bone].Z = value; active[bone] = true;
        }
        var raw = new Dictionary<int, Quaternion>();
        for (int bone = 0; bone < 25; bone++) if (active[bone])
        {
            var axis = rig.Axes[rig.Nodes[rig.HumanNodes[bone]].Axes]; var a = angles[bone];
            var swing = new Quaternion(0, MathF.Tan(a.Y / 2), MathF.Tan(a.Z / 2), 1); NativeHumanoidRig.Rotation(swing); swing = Quaternion.Normalize(swing);
            raw.Add(bone, Quaternion.Normalize(axis.Pre * swing * Quaternion.CreateFromAxisAngle(Vector3.UnitX, a.X) * Quaternion.Conjugate(axis.Post)));
        }
        var result = new Dictionary<int, Quaternion>(raw);
        // Distal pairs precede proximal pairs: the proximal residual acts on the
        // already redistributed child, preserving both authored twist channels.
        // The authored twist of the proximal bone is split by its native weight: the proximal bone keeps
        // weight*w and the distal bone receives the remainder in the proximal frame. weight 1 keeps raw and
        // weight 0 reproduces the previous fully redistributed endpoint exactly.
        Quaternion Split(int bone, float weight)
        {
            var axis = rig.Axes[rig.Nodes[rig.HumanNodes[bone]].Axes]; var a = angles[bone];
            var swing = Quaternion.Normalize(new Quaternion(0, MathF.Tan(a.Y / 2), MathF.Tan(a.Z / 2), 1));
            return Quaternion.Normalize(axis.Pre * swing * Quaternion.CreateFromAxisAngle(Vector3.UnitX, a.X * weight) * Quaternion.Conjugate(axis.Post));
        }
        foreach (var (parent, child, weight) in new[] { (3, 5, rig.TwistPolicy.W), (4, 6, rig.TwistPolicy.W),
            (16, 18, rig.TwistPolicy.Y), (17, 19, rig.TwistPolicy.Y),
            (1, 3, rig.TwistPolicy.Z), (2, 4, rig.TwistPolicy.Z), (14, 16, rig.TwistPolicy.X), (15, 17, rig.TwistPolicy.X) })
        {
            if (weight == 1) continue;
            var split = Split(parent, weight);
            result[parent] = split;
            result[child] = Quaternion.Normalize(Quaternion.Conjugate(split) * raw[parent] * result[child]);
        }
        return result;
    }

    private static (Vector3[] Position, Quaternion[] Rotation) World(NativeHumanoidRig rig, Dictionary<int, Quaternion> local)
    {
        var driven = local.ToDictionary(x => rig.HumanNodes[x.Key], x => x.Value); var positions = new Vector3[rig.Nodes.Count]; var rotations = new Quaternion[rig.Nodes.Count];
        for (int i = 0; i < rig.Nodes.Count; i++)
        {
            var node = rig.Nodes[i]; Quaternion q = driven.GetValueOrDefault(i, node.Rotation);
            rotations[i] = node.Parent < 0 ? q : Quaternion.Normalize(rotations[node.Parent] * q);
            positions[i] = node.Parent < 0 ? node.Translation : positions[node.Parent] + Vector3.Transform(node.Translation, rotations[node.Parent]);
        }
        return (positions, rotations);
    }
    private static Quaternion BodyFrame(NativeHumanoidRig rig, Vector3[] world)
    {
        Vector3 P(int slot) => world[rig.HumanNodes[slot]];
        Vector3 right = Unit((P(15) - P(14)) + (P(2) - P(1))), up = Unit((P(14) + P(15)) - (P(1) + P(2)));
        Vector3 forward = Unit(Vector3.Cross(right, up)); right = Unit(Vector3.Cross(up, forward));
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new(right.X, right.Y, right.Z, 0, up.X, up.Y, up.Z, 0, forward.X, forward.Y, forward.Z, 0, 0, 0, 0, 1)));
    }
    private static Vector3 Center(NativeHumanoidRig rig, Vector3[] world)
    {
        Vector3 P(int slot) => world[rig.HumanNodes[slot]]; var points = Enumerable.Range(0, 22).Select(P).ToArray();
        foreach (var (a, b) in new[] { (1, 3), (2, 4), (3, 5), (4, 6), (7, 8), (8, 9), (10, 11), (12, 14), (13, 15), (14, 16), (15, 17), (16, 18), (17, 19) }) points[a] = (P(a) + P(b)) / 2;
        points[0] = (P(1) + P(2) + P(7)) / 3; points[9] = (P(9) + P(10) + P(12) + P(13)) / 4;
        Vector3 center = Vector3.Zero; for (int i = 0; i < points.Length; i++) center += points[i] * rig.Masses[i]; return center / rig.Masses.Sum();
    }
    private static float Component(Vector3 x, int axis) => axis == 0 ? x.X : axis == 1 ? x.Y : x.Z;
    private static Vector3 Unit(Vector3 x) { NativeHumanoidRig.Finite(x); Validation.Require(x.LengthSquared() > 1e-12f, "Degenerate human body frame"); return Vector3.Normalize(x); }
}
