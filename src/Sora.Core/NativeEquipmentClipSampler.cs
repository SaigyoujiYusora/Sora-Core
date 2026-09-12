using System.Text.Json;
namespace Sora.Core;

/// <summary>A bounded sample source for one dedicated-equipment clip: native scalar blocks or ACL transform tracks.</summary>
public interface INativeEquipmentClipSampler
{
    double Duration { get; }
    double? DenseSampleRate { get; }
    double[] Sample(double time);
}

/// <summary>Chooses the established bounded sample source from the clip's own native storage, never from a name.</summary>
public static class NativeEquipmentClipSampler
{
    public static bool IsAcl(JsonElement clip) =>
        clip.GetProperty("m_AclCompressedBuffer").GetProperty("TransformBufferData").GetProperty("Array").GetString() is { Length: > 0 };

    public static INativeEquipmentClipSampler Create(JsonElement clip, NativeEquipmentAnimationBinding[] bindings,
        string? workerExecutable = null) =>
        IsAcl(clip) ? new NativeEquipmentAclSampler(clip, bindings, workerExecutable) : new NativeGenericScalarSampler(clip);
}

/// <summary>
/// ACL transform-track sampling for dedicated equipment. It reuses the reviewed ACL worker decode and the
/// authored TransformSubTrackMasks so every ACL track is bound to one exact native path hash; it does not
/// require the character root-motion scalar layout, which equipment clips do not carry. Between two adjacent
/// native samples the per-track TRS components are reconstructed by bounded linear interpolation, so the
/// authored frame times stay exact and no sample is invented outside the decoded interval.
/// </summary>
public sealed class NativeEquipmentAclSampler : INativeEquipmentClipSampler
{
    private sealed record BoundTrack(int Track, int Size, int Offset);
    private readonly float[] values = [];
    private readonly int tracks, samples, scalars, stride;
    private readonly float rate;
    private readonly BoundTrack[] mapping = [];
    public double Duration { get; }
    public double? DenseSampleRate => rate;

    private static byte[] Base64(JsonElement buffer, string field)
    {
        string? text = buffer.GetProperty(field).GetProperty("Array").GetString();
        Validation.Require(text is not null, "Missing ACL equipment byte buffer: " + field);
        return Convert.FromBase64String(text!);
    }

    public NativeEquipmentAclSampler(JsonElement clip, NativeEquipmentAnimationBinding[] bindings, string? workerExecutable = null)
    {
        Validation.Require(bindings is { Length: > 0 and <= 16384 }, "ACL equipment clip requires at least one bound transform channel");
        var buffer = clip.GetProperty("m_AclCompressedBuffer");
        Validation.Require(clip.GetProperty("m_aclType").GetInt32() == 16 && buffer.GetProperty("Header").GetProperty("Version").GetInt32() == 10,
            "Unsupported Endfield equipment animation encoding");
        var primary = AclCodec.Decode(Base64(buffer, "TransformBufferData"), Base64(buffer, "FloatBufferData"), workerExecutable);
        tracks = primary.Tracks; samples = primary.Samples; scalars = primary.Scalars; rate = primary.SampleRate; values = primary.Values;
        stride = tracks * 10 + scalars;
        Validation.Require(tracks is > 0 and <= 4096 && samples is > 0 and <= 1000000 && float.IsFinite(rate) && rate > 0,
            "Unsupported ACL equipment sample layout");
        Validation.Require(values is not null && values.Length == (long)stride * samples && values.All(float.IsFinite),
            "Invalid ACL equipment sample values");
        Validation.Require(clip.GetProperty("m_SampleRate").GetSingle() == rate, "ACL equipment sample rate disagrees with the clip");
        Duration = (samples - 1) / (double)rate;
        Validation.Require(Duration > 0, "ACL equipment clip has no sample interval");
        Validation.Require(Math.Abs((float)clip.GetProperty("m_MuscleClip").GetProperty("m_StopTime").GetDouble() - Duration) <= 0.0001,
            "ACL equipment stop time disagrees with its decoded sample grid");
        mapping = Map(buffer, tracks, bindings);
    }

    private static BoundTrack[] Map(JsonElement buffer, int tracks, NativeEquipmentAnimationBinding[] bindings)
    {
        int bytes = (tracks + 7) / 8, padded = (bytes * 3 + 7) / 8 * 8;
        string? text = buffer.GetProperty("TransformSubTrackMasks").GetProperty("Array").GetString();
        Validation.Require(text is not null, "Missing ACL equipment transform mask");
        byte[] mask = Convert.FromBase64String(text!);
        Validation.Require(mask.Length == padded, "Invalid ACL equipment transform mask size");
        Validation.Require(mask.AsSpan(bytes * 3).IndexOfAnyExcept((byte)0) < 0, "Nonzero ACL equipment mask alignment padding");
        if (tracks % 8 != 0) for (int channel = 0; channel < 3; channel++)
            Validation.Require((mask[(channel + 1) * bytes - 1] & ((1 << (8 - tracks % 8)) - 1)) == 0, "Nonzero ACL equipment mask padding");
        bool Has(int track, int channel) => (mask[channel * bytes + track / 8] & (128 >> (track % 8))) != 0;
        var paths = new uint?[tracks];
        void Assign(List<uint> channel, int slot)
        {
            int cursor = 0;
            for (int track = 0; track < tracks; track++) if (Has(track, slot))
            {
                Validation.Require(cursor < channel.Count, "ACL equipment mask has more tracks than authored channels");
                uint path = channel[cursor++];
                Validation.Require(paths[track] is null || paths[track] == path, "ACL equipment mask and channel order disagree");
                paths[track] = path;
            }
            Validation.Require(cursor == channel.Count, "ACL equipment channel exceeds transform mask coverage");
        }
        Assign(bindings.Where(b => b.Attribute == 1).Select(b => b.PathHash).ToList(), 0);
        Assign(bindings.Where(b => b.Attribute == 2).Select(b => b.PathHash).ToList(), 1);
        Assign(bindings.Where(b => b.Attribute == 3).Select(b => b.PathHash).ToList(), 2);
        var bound = new Dictionary<(uint, int), int>();
        for (int track = 0; track < tracks; track++)
        {
            Validation.Require(paths[track].HasValue, "ACL equipment transform track is not identified by an authored binding");
            for (int channel = 0; channel < 3; channel++) if (Has(track, channel))
                Validation.Require(bound.TryAdd((paths[track]!.Value, channel + 1), track), "Duplicate ACL equipment transform track");
        }
        var result = new List<BoundTrack>(bindings.Length);
        foreach (var binding in bindings)
        {
            Validation.Require(bound.TryGetValue((binding.PathHash, binding.Attribute), out int track),
                "ACL equipment binding has no transform track for pathHash=" + binding.PathHash + " attribute=" + binding.Attribute);
            result.Add(new(track, binding.Attribute == 2 ? 4 : 3, binding.Attribute == 1 ? 4 : binding.Attribute == 2 ? 0 : 7));
        }
        return result.ToArray();
    }

    public double[] Sample(double time)
    {
        Validation.Require(double.IsFinite(time) && time >= 0 && time <= Duration, "Sample time outside native clip interval");
        double index = time * rate;
        int lower = (int)Math.Floor(index); double blend = index - lower;
        if (lower >= samples - 1) { lower = samples - 1; blend = 0; }
        Validation.Require(lower >= 0 && lower < samples, "ACL equipment sample index is outside the native grid");
        var result = new double[mapping.Sum(m => m.Size)];
        int at = 0;
        foreach (var bound in mapping)
        {
            int a = lower * stride + bound.Track * 10 + bound.Offset, b = (lower + 1) * stride + bound.Track * 10 + bound.Offset;
            if (bound.Size == 4)
            {
                // Rotation: keep the decoded component order and renormalize. Between two authored samples the
                // quaternion hemisphere is resolved before the bounded reconstruction so the result stays on
                // the shorter arc; at an authored sample the decoded quaternion is used unchanged.
                double sign = 1;
                if (blend != 0)
                {
                    double dot = 0;
                    for (int component = 0; component < 4; component++) dot += values[a + component] * (double)values[b + component];
                    sign = dot < 0 ? -1 : 1;
                }
                double norm = 0;
                for (int component = 0; component < 4; component++)
                {
                    double value = blend == 0 ? values[a + component] : values[a + component] + (sign * values[b + component] - values[a + component]) * blend;
                    result[at + component] = value; norm += value * value;
                }
                Validation.Require(double.IsFinite(norm) && norm > 1e-12, "ACL equipment rotation is degenerate");
                double length = Math.Sqrt(norm);
                for (int component = 0; component < 4; component++) result[at + component] /= length;
                at += 4;
                continue;
            }
            for (int component = 0; component < bound.Size; component++)
            {
                double value = values[a + component];
                if (blend != 0) value += (values[b + component] - value) * blend;
                result[at++] = value;
            }
        }
        return result;
    }
}
