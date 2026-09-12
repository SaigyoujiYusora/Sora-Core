using System.Buffers.Binary;
using System.Text.Json;

namespace Sora.Core;

/// <summary>
/// Bounded non-ACL scalar sampling, independent of controller state and Blender.
/// Streamed keys must describe continuous finite cubics, with explicit clip endpoints.
/// Packed position data is sampled only at its native frame times; interpolation is
/// deliberately unsupported until its runtime contract is independently established.
/// No looping, clamping, event interpretation, quaternion conversion, or rig binding.
/// </summary>
public sealed class NativeGenericScalarSampler : INativeEquipmentClipSampler
{
    private sealed record Key(double Time, double A, double B, double C, double Value)
    {
        public double At(double time) { double dt = time - Time; return ((A * dt + B) * dt + C) * dt + Value; }
    }
    private readonly Key[][] streamed;
    private readonly byte[] packed;
    private readonly double[] constants;
    private readonly int width, frames, positionCurves, rotationCurves;
    private readonly int[] denseKinds = [];
    private readonly double rate, factor;
    public double Duration { get; }
    public int ScalarCount => streamed.Length + width + constants.Length;
    public double? DenseSampleRate => width == 0 ? null : rate;

    private static JsonElement[] Array(JsonElement parent, string field, int maximum)
    {
        var array = parent.GetProperty(field).GetProperty("Array");
        Validation.Require(array.ValueKind == JsonValueKind.Array && array.GetArrayLength() <= maximum, "Native scalar array exceeds bound");
        return array.EnumerateArray().ToArray();
    }
    private static double Float(JsonElement word) => BitConverter.Int32BitsToSingle(unchecked((int)word.GetUInt32()));

    public NativeGenericScalarSampler(JsonElement clip)
    {
        var acl = clip.GetProperty("m_AclCompressedBuffer");
        foreach (string field in new[] { "OutputTrackCount", "FloatCurveCount", "RootTrackCount" })
            Validation.Require(acl.GetProperty(field).GetInt32() == 0, "Native scalar sampling requires non-ACL data");
        foreach (string field in new[] { "TransformBufferData", "FloatBufferData", "RootMotionBufferData" })
            Validation.Require(acl.GetProperty(field).GetProperty("Array").GetString() == "", "Unexpected ACL payload");
        Validation.Require(!clip.GetProperty("m_Legacy").GetBoolean() && !clip.GetProperty("m_Compressed").GetBoolean(), "Unsupported legacy or compressed curve layout");
        var muscle = clip.GetProperty("m_MuscleClip");
        // m_StopTime is a native float32. Reading it as a double exposes the shortest decimal
        // round-trip of that float, which is up to one float32 ULP below the value the streamed
        // key times are decoded from; comparing them as doubles rejects clips whose last streamed
        // key is exactly the authored clip end. Compare in the native float32 value space instead.
        Duration = (float)muscle.GetProperty("m_StopTime").GetDouble();
        Validation.Require(muscle.GetProperty("m_StartTime").GetDouble() == 0 && double.IsFinite(Duration) && Duration > 0 && Duration <= 86400, "Unsupported native scalar clip interval");
        var block = muscle.GetProperty("m_Clip").GetProperty("data");
        var stream = block.GetProperty("m_StreamedClip");
        int count = stream.GetProperty("curveCount").GetInt32();
        Validation.Require(count >= 0 && count <= 16384, "Streamed scalar count exceeds bound");
        var lists = Enumerable.Range(0, count).Select(_ => new List<Key>()).ToArray();
        var words = Array(stream, "data", 1_000_000);
        int cursor = 0; double previous = double.NegativeInfinity; bool terminated = false;
        var sentinelValues = new Dictionary<int, double>();
        while (cursor < words.Length)
        {
            Validation.Require(cursor + 2 <= words.Length && !terminated, "Truncated or trailing streamed frame");
            double time = Float(words[cursor++]); int keys = words[cursor++].GetInt32();
            Validation.Require(!double.IsNaN(time) && time > previous && keys >= 0 && keys <= count && cursor + (long)keys * 5 <= words.Length, "Invalid streamed frame header");
            bool sentinel = time == -float.MaxValue;
            if (double.IsPositiveInfinity(time))
            {
                Validation.Require(keys == 0 && cursor == words.Length, "Invalid streamed end sentinel");
                terminated = true; break;
            }
            Validation.Require(double.IsFinite(time) && (sentinel && previous == double.NegativeInfinity || time >= 0 && time <= Duration), "Unsupported streamed key time");
            var indices = new HashSet<int>();
            for (int k = 0; k < keys; k++)
            {
                int index = words[cursor++].GetInt32();
                Validation.Require(index >= 0 && index < count && indices.Add(index), "Duplicate or invalid streamed scalar index");
                double a = Float(words[cursor++]), b = Float(words[cursor++]), c = Float(words[cursor++]), value = Float(words[cursor++]);
                Validation.Require(new[] { a, b, c, value }.All(double.IsFinite), "Nonfinite or special streamed polynomial is unsupported");
                if (sentinel)
                {
                    Validation.Require(a == 0 && b == 0 && c == 0, "Unsupported initial streamed sentinel");
                    sentinelValues.Add(index, value);
                }
                else lists[index].Add(new(time, a, b, c, value));
            }
            previous = time;
        }
        Validation.Require(count == 0 && words.Length == 0 || terminated, "Missing streamed end sentinel");
        streamed = lists.Select(list => list.ToArray()).ToArray();
        for (int i = 0; i < count; i++)
        {
            var keys = streamed[i];
            // A curve may stop changing before the clip end; its terminal segment is required to be
            // constant below, so the authored last value is held exactly up to Duration. Curves may not
            // start after time zero, because no value would be defined before their first key.
            Validation.Require(keys.Length >= 2 && keys[0].Time == 0 && keys[^1].Time <= Duration, "Streamed scalar requires exact interval endpoints");
            Validation.Require(!sentinelValues.TryGetValue(i, out double initial) || initial == keys[0].Value, "Initial sentinel disagrees with zero key");
            for (int k = 0; k + 1 < keys.Length; k++)
            {
                double endpoint = keys[k].At(keys[k + 1].Time), expected = keys[k + 1].Value;
                Validation.Require(double.IsFinite(endpoint) && Math.Abs(endpoint - expected) <= 1e-6 * Math.Max(1, Math.Abs(expected)), "Streamed coefficients do not establish a continuous outgoing cubic");
            }
            Validation.Require(keys[^1].A == 0 && keys[^1].B == 0 && keys[^1].C == 0, "Unsupported terminal streamed polynomial");
        }
        var dense = block.GetProperty("m_DenseClip");
        width = dense.GetProperty("m_CurveCount").GetInt32(); frames = dense.GetProperty("m_FrameCount").GetInt32();
        Validation.Require(width >= 0 && width <= 16384 && frames >= 0 && frames <= 8_000_000 && (width > 0 || frames == 0) && (long)frames * width <= 8_000_000, "Dense scalar bounds exceeded");
        Validation.Require(Array(dense, "m_SampleArray", 8_000_000).Length == 0, "Unpacked dense layout is unsupported");
        var payload = dense.GetProperty("m_ACLArray").GetProperty("Array");
        Validation.Require(payload.ValueKind == JsonValueKind.String, "Packed dense payload must be a non-null string");
        string encoded = payload.GetString()!;
        Validation.Require(encoded.Length <= 24_000_000, "Packed dense input exceeds bound");
        packed = Convert.FromBase64String(encoded);
        Validation.Require(packed.Length == (long)frames * width * 2, "Packed dense byte count mismatch");
        if (width > 0)
        {
            rate = dense.GetProperty("m_SampleRate").GetDouble(); factor = dense.GetProperty("m_PositionFactor").GetDouble();
            Validation.Require(frames > 0 && dense.GetProperty("m_BeginTime").GetDouble() == 0 && dense.GetProperty("m_ACLType").GetInt32() == 16, "Unsupported packed dense layout");
            Validation.Require(double.IsFinite(rate) && rate > 0 && rate <= 1000 && double.IsFinite(factor) && factor > 0, "Invalid dense rate or factor");
            positionCurves = dense.GetProperty("m_nPositionCurves").GetInt32(); rotationCurves = dense.GetProperty("m_nRotationCurves").GetInt32();
            Validation.Require(positionCurves >= 0 && rotationCurves >= 0 && positionCurves + rotationCurves == width
                && dense.GetProperty("m_nEulerCurves").GetInt32() == 0 && dense.GetProperty("m_nScaleCurves").GetInt32() == 0,
                "Unsupported packed16 dense curve kinds; only position and rotation scalars are established");
            Validation.Require(Duration * rate <= frames - 1, "Dense data does not cover clip interval");
        }
        constants = Array(block.GetProperty("m_ConstantClip"), "data", 16384).Select(v => v.GetDouble()).ToArray();
        Validation.Require(constants.All(double.IsFinite), "Nonfinite constant scalar");
        // Confirm the observed raw block order against transform binding cardinality. The packed16 dense
        // block carries the transform channels whose scalars start at or after the streamed block, in the
        // authored binding order; each position binding owns 3 curves and each rotation binding 4.
        int total = 0; var seen = new HashSet<(uint, int)>(); var kinds = new List<int>();
        foreach (var binding in Array(clip.GetProperty("m_ClipBindingConstant"), "genericBindings", 16384))
        {
            int attribute = binding.GetProperty("attribute").GetInt32();
            Validation.Require(binding.GetProperty("typeID").GetInt32() == 4 && binding.GetProperty("customType").GetInt32() == 0 && binding.GetProperty("isPPtrCurve").GetInt32() == 0 && attribute is >= 1 and <= 3 && seen.Add((binding.GetProperty("path").GetUInt32(), attribute)), "Unsupported native transform scalar binding");
            int size = attribute == 2 ? 4 : 3;
            if (total < count + width && total + size > count)
            {
                Validation.Require(total >= count && total + size <= count + width && attribute is 1 or 2, "Packed dense scalars cross or disagree with their transform bindings");
                for (int i = 0; i < size; i++) kinds.Add(attribute);
            }
            total += size;
        }
        Validation.Require(total == ScalarCount, "Native scalar blocks and binding count disagree");
        Validation.Require(kinds.Count == width && kinds.Count(k => k == 1) == positionCurves && kinds.Count(k => k == 2) == rotationCurves,
            "Packed dense curve kinds disagree with the authored transform bindings");
        denseKinds = kinds.ToArray();
    }

    /// <summary>Returns streamed, then dense, then constant scalars in native binding order.</summary>
    public double[] Sample(double time)
    {
        Validation.Require(double.IsFinite(time) && time >= 0 && time <= Duration, "Sample time outside native clip interval");
        int frame = 0; double blend = 0;
        if (width > 0)
        {
            double index = time * rate;
            Validation.Require(double.IsFinite(index) && index >= 0 && index <= frames - 1, "Packed dense sample index is outside the native clip");
            frame = (int)Math.Floor(index); blend = index - frame;
            if (frame == frames - 1) blend = 0;
        }
        var values = new double[ScalarCount];
        for (int i = 0; i < streamed.Length; i++)
        {
            var keys = streamed[i]; int lower = 0, upper = keys.Length;
            while (lower + 1 < upper) { int mid = (lower + upper) / 2; if (keys[mid].Time <= time) lower = mid; else upper = mid; }
            values[i] = keys[lower].At(time);
            Validation.Require(double.IsFinite(values[i]), "Streamed sample overflow");
        }
        for (int i = 0; i < width; i++)
        {
            if (denseKinds[i] == 2)
            {
                // A rotation binding owns four consecutive packed16 curves. Resolve the hemisphere from the
                // adjacent authored frame and renormalize so every emitted value is a unit quaternion.
                Validation.Require(i + 4 <= width && denseKinds[i + 1] == 2 && denseKinds[i + 2] == 2 && denseKinds[i + 3] == 2, "Packed dense rotation curves are not grouped by four");
                double sign = 1;
                if (blend != 0)
                {
                    double dot = 0;
                    for (int c = 0; c < 4; c++) dot += DenseScalar(i + c, frame) * DenseScalar(i + c, frame + 1);
                    sign = dot < 0 ? -1 : 1;
                }
                double norm = 0; var quaternion = new double[4];
                for (int c = 0; c < 4; c++)
                {
                    double rotationValue = DenseScalar(i + c, frame);
                    if (blend != 0) rotationValue += (sign * DenseScalar(i + c, frame + 1) - rotationValue) * blend;
                    quaternion[c] = rotationValue; norm += rotationValue * rotationValue;
                }
                Validation.Require(double.IsFinite(norm) && norm > 1e-12, "Packed dense rotation is degenerate");
                double length = Math.Sqrt(norm);
                for (int c = 0; c < 4; c++) values[streamed.Length + i + c] = quaternion[c] / length;
                i += 3;
                continue;
            }
            double value = DenseScalar(i, frame);
            if (blend != 0) value += (DenseScalar(i, frame + 1) - value) * blend;
            values[streamed.Length + i] = value;
        }
        constants.CopyTo(values, streamed.Length + width);
        return values;
    }

    // Position curves use the authored position factor; rotation curves use the same unsigned normalized
    // unpacking without that factor and are normalized as a quaternion by the caller.
    private double DenseScalar(int curve, int frame)
    {
        double raw = (BinaryPrimitives.ReadUInt16LittleEndian(packed.AsSpan((frame * width + curve) * 2, 2)) - 32767.5) / 32767.5;
        return denseKinds[curve] == 2 ? raw : raw * factor;
    }
}
