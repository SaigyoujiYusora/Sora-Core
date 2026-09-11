using System.Buffers.Binary;
using System.Text.Json;

namespace Sora.Core;

/// <summary>Classic (non-ACL) clip curves decoded into the established native binding layout.</summary>
public sealed record NativeStreamedClipCurves(int Samples, float SampleRate, int Tracks, int Scalars,
    uint[] TransformPaths, bool[] PositionTracks, bool[] RotationTracks, bool[] ScaleTracks, uint[] ScalarAttributes, float[] Values, string[] Notes);

/// <summary>
/// Bounded reader for the observed classic AnimationClip storage: cubic streamed float keys for the leading curves and
/// a constant block for the trailing curves, both in authored binding order (3 position, 4 rotation, 1 Animator scalar
/// per binding, matching the 362/362/151 layout observed on the locked battle-loop clip). Packed dense curves, legacy
/// and compressed layouts are not established here and fail explicitly. No ACL header, mask or compressed byte is
/// synthesized and no rest pose is substituted: every emitted value comes from the authored curves.
/// </summary>
public static class NativeStreamedClipDecoder
{
    private sealed record Key(double Time, double A, double B, double C, double Value)
    {
        public double At(double time) { double dt = time - Time; return ((A * dt + B) * dt + C) * dt + Value; }
    }

    private static JsonElement[] Array(JsonElement parent, string field, int maximum)
    {
        var array = parent.GetProperty(field).GetProperty("Array");
        Validation.Require(array.ValueKind == JsonValueKind.Array && array.GetArrayLength() <= maximum, "Native streamed array exceeds bound");
        return array.EnumerateArray().ToArray();
    }
    private static float Float(JsonElement word) => BitConverter.Int32BitsToSingle(unchecked((int)word.GetUInt32()));

    public static NativeStreamedClipCurves Decode(JsonElement clip)
    {
        var acl = clip.GetProperty("m_AclCompressedBuffer");
        foreach (string field in new[] { "TransformBufferData", "FloatBufferData", "RootMotionBufferData" })
            Validation.Require(acl.GetProperty(field).GetProperty("Array").GetString() == "", "Classic streamed decoding requires an empty ACL payload");
        Validation.Require(!clip.GetProperty("m_Legacy").GetBoolean() && !clip.GetProperty("m_Compressed").GetBoolean(), "Unsupported classic animation curve layout");
        float rate = clip.GetProperty("m_SampleRate").GetSingle();
        Validation.Require(float.IsFinite(rate) && rate > 0 && rate <= 1000, "Invalid classic animation sample rate");
        var muscle = clip.GetProperty("m_MuscleClip");
        float stop = (float)muscle.GetProperty("m_StopTime").GetDouble();
        Validation.Require(muscle.GetProperty("m_StartTime").GetDouble() == 0 && float.IsFinite(stop) && stop > 0 && stop <= 86400, "Unsupported classic animation interval");
        Validation.Require(NativeHumanoidRig.ArrayField(muscle.GetProperty("m_DeltaPose"), "m_DoFArray", 61).Length == 61, "Clip does not use the observed native61 muscle layout");
        double frames = stop * (double)rate + 1;
        int samples = (int)Math.Round(frames);
        Validation.Require(samples is >= 2 and <= 1_000_000 && Math.Abs(frames - samples) <= 0.001, "Classic animation interval is not on a whole sample grid");
        var block = muscle.GetProperty("m_Clip").GetProperty("data");
        Validation.Require(block.GetProperty("m_DenseClip").GetProperty("m_CurveCount").GetInt32() == 0, "Classic dense curves are not an established storage for this reader");
        int streamedCount = block.GetProperty("m_StreamedClip").GetProperty("curveCount").GetInt32();
        Validation.Require(streamedCount is >= 0 and <= 16384, "Streamed curve count exceeds bound");
        double[] constants = Array(block.GetProperty("m_ConstantClip"), "data", 16384).Select(value => value.GetDouble()).ToArray();
        Validation.Require(constants.All(double.IsFinite), "Nonfinite classic animation constant");
        var bindings = Array(clip.GetProperty("m_ClipBindingConstant"), "genericBindings", 16384);
        var tracks = new List<uint>();
        var trackLookup = new Dictionary<uint, int>();
        var scalarAttributes = new List<uint>();
        var scalarLookup = new Dictionary<uint, int>();
        var duplicateCurves = new List<(uint Attribute, int First, int Second)>();
        var firstCurve = new Dictionary<uint, int>();
        var output = new List<(int Track, int Kind, int Component, int Curve, int ScalarColumn)>();
        int total = 0;
        foreach (var binding in bindings)
        {
            int type = binding.GetProperty("typeID").GetInt32();
            uint attribute = binding.GetProperty("attribute").GetUInt32();
            int custom = binding.GetProperty("customType").GetInt32();
            Validation.Require(binding.GetProperty("isPPtrCurve").GetInt32() == 0, "Unsupported object animation binding");
            if (type == 4)
            {
                Validation.Require(custom == 0 && attribute is 1 or 2 or 3, "Unsupported native transform curve binding");
                uint path = binding.GetProperty("path").GetUInt32();
                if (!trackLookup.TryGetValue(path, out int track)) trackLookup[path] = track = tracks.Count;
                if (track == tracks.Count) tracks.Add(path);
                int size = attribute == 2 ? 4 : 3;
                for (int component = 0; component < size; component++) output.Add((track, (int)attribute, component, total++, -1));
            }
            else if (type == 95)
            {
                Validation.Require(binding.GetProperty("path").GetUInt32() == 0 && custom == (attribute < 143 ? 8 : 0), "Unsupported native Animator scalar binding");
                if (!scalarLookup.TryGetValue(attribute, out int column))
                {
                    scalarLookup[attribute] = column = scalarAttributes.Count;
                    scalarAttributes.Add(attribute);
                    firstCurve[attribute] = total;
                }
                else duplicateCurves.Add((attribute, firstCurve[attribute], total));
                output.Add((-1, 0, 0, total++, column));
            }
            else throw new InvalidDataException("Unsupported native animation binding type");
        }
        Validation.Require(total == streamedCount + constants.Length, "Classic curve blocks disagree with the authored binding cardinality");
        Validation.Require(scalarLookup.Count >= 14 && scalarLookup.Count <= 4096 && Enumerable.Range(0, 143).All(x => scalarLookup.ContainsKey((uint)x)), "Required native Animator scalar attributes are absent");
        Validation.Require(tracks.Count is > 0 and <= 4096 && tracks.Distinct().Count() == tracks.Count, "Unsupported native transform track count");
        // Streamed float keys: (time, keyCount) frames, each key (curveIndex, a, b, c, value).
        var words = Array(block.GetProperty("m_StreamedClip"), "data", 8_000_000);
        var keys = Enumerable.Range(0, streamedCount).Select(_ => new List<Key>()).ToArray();
        var sentinel = new Dictionary<int, double>();
        int cursor = 0; double previous = double.NegativeInfinity; bool terminated = false;
        while (cursor < words.Length)
        {
            Validation.Require(cursor + 2 <= words.Length && !terminated, "Truncated or trailing streamed frame");
            double time = Float(words[cursor++]); int count = words[cursor++].GetInt32();
            Validation.Require(!double.IsNaN(time) && time > previous && count >= 0 && count <= streamedCount && cursor + (long)count * 5 <= words.Length, "Invalid streamed frame header");
            if (double.IsPositiveInfinity(time)) { Validation.Require(count == 0 && cursor == words.Length, "Invalid streamed end sentinel"); terminated = true; break; }
            bool initial = time == -float.MaxValue;
            Validation.Require(double.IsFinite(time) && (initial && previous == double.NegativeInfinity || time >= 0 && time <= stop), "Unsupported streamed key time");
            var seen = new HashSet<int>();
            for (int index = 0; index < count; index++)
            {
                int curve = words[cursor++].GetInt32();
                Validation.Require(curve >= 0 && curve < streamedCount && seen.Add(curve), "Duplicate or invalid streamed curve index");
                double a = Float(words[cursor++]), b = Float(words[cursor++]), c = Float(words[cursor++]), value = Float(words[cursor++]);
                Validation.Require(new[] { a, b, c, value }.All(double.IsFinite), "Nonfinite streamed polynomial");
                if (initial) { Validation.Require(a == 0 && b == 0 && c == 0, "Unsupported initial streamed sentinel"); sentinel[curve] = value; }
                else keys[curve].Add(new(time, a, b, c, value));
            }
            previous = time;
        }
        Validation.Require(streamedCount == 0 && words.Length == 0 || terminated, "Missing streamed end sentinel");
        for (int curve = 0; curve < streamedCount; curve++)
        {
            var list = keys[curve];
            Validation.Require(list.Count >= 2 && list[0].Time == 0 && list[^1].Time <= stop, "Streamed curve requires exact interval endpoints");
            Validation.Require(!sentinel.TryGetValue(curve, out double first) || first == list[0].Value, "Initial sentinel disagrees with the zero key");
            for (int index = 0; index + 1 < list.Count; index++)
            {
                double endpoint = list[index].At(list[index + 1].Time), expected = list[index + 1].Value;
                Validation.Require(double.IsFinite(endpoint) && Math.Abs(endpoint - expected) <= 1e-6 * Math.Max(1, Math.Abs(expected)), "Streamed coefficients do not establish a continuous outgoing cubic");
            }
            Validation.Require(list[^1].A == 0 && list[^1].B == 0 && list[^1].C == 0, "Unsupported terminal streamed polynomial");
        }
        long outputLength = (tracks.Count * 10L + scalarLookup.Count) * samples;
        Validation.Require(outputLength <= 16 * 1024 * 1024 && (long)total * samples <= 16 * 1024 * 1024, "Classic animation sample count exceeds bound");
        var values = new float[(int)outputLength];
        var curveValues = new double[(long)total * samples];
        for (int sample = 0; sample < samples; sample++)
        {
            double time = sample / (double)rate;
            for (int curve = 0; curve < total; curve++)
                if (curve < streamedCount)
                {
                    var list = keys[curve]; int lower = 0, upper = list.Count;
                    while (lower + 1 < upper) { int mid = (lower + upper) / 2; if (list[mid].Time <= time) lower = mid; else upper = mid; }
                    curveValues[curve * samples + sample] = list[lower].At(time);
                }
                else curveValues[curve * samples + sample] = constants[curve - streamedCount];
        }
        // A repeated custom Animator property is only collapsed when its authored curves agree on every sample.
        foreach ((uint attribute, int first, int second) in duplicateCurves)
            for (int sample = 0; sample < samples; sample++)
                Validation.Require(curveValues[first * samples + sample] == curveValues[second * samples + sample],
                    "Duplicate Animator scalar binding " + attribute + " has differing authored values");
        for (int sample = 0; sample < samples; sample++)
        {
            int at = sample * (tracks.Count * 10 + scalarLookup.Count);
            foreach ((int track, int kind, int component, int curve, int scalarColumn) in output)
            {
                float value = (float)curveValues[curve * samples + sample];
                if (track < 0) values[at + tracks.Count * 10 + scalarColumn] = value;
                else values[at + track * 10 + (kind == 1 ? 4 : kind == 2 ? 0 : 7) + component] = value;
            }
        }
        // Rotation bindings must decode to quaternion components; an unrelated encoding would not stay normalized.
        foreach ((int track, int kind, int component, int curve, int scalarColumn) in output)
            if (track >= 0 && kind == 2 && component == 0)
            {
                for (int sample = 0; sample < samples; sample++)
                {
                    int at = sample * (tracks.Count * 10 + scalarLookup.Count) + track * 10;
                    double norm = Math.Sqrt((double)values[at] * values[at] + (double)values[at + 1] * values[at + 1] + (double)values[at + 2] * values[at + 2] + (double)values[at + 3] * values[at + 3]);
                    Validation.Require(Math.Abs(norm - 1) <= 0.01, "Streamed rotation curves are not quaternion components");
                }
            }
        var position = new bool[tracks.Count]; var rotation = new bool[tracks.Count]; var scale = new bool[tracks.Count];
        foreach ((int track, int kind, int component, int curve, int scalarColumn) in output)
            if (track >= 0)
            {
                if (kind == 1) position[track] = true; else if (kind == 2) rotation[track] = true; else scale[track] = true;
            }
        var notes = new List<string>();
        foreach ((uint attribute, int first, int second) in duplicateCurves)
            notes.Add("Two authored curves for custom Animator attribute " + attribute.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " are value-identical across all " + samples + " samples; a single track is emitted.");
        return new(samples, rate, tracks.Count, scalarLookup.Count, tracks.ToArray(), position, rotation, scale, scalarAttributes.ToArray(), values, notes.ToArray());
    }
}
