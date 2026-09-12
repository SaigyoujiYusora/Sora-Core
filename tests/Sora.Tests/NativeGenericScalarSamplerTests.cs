using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sora.Core;

public static class NativeGenericScalarSamplerTests
{
    public static void Run(Action<string, Action> test, Action<Action> reject, string? source=null)
    {
        byte[] raw = source is null ? [] : File.ReadAllBytes(source);
        if (source is not null && Convert.ToHexStringLower(SHA256.HashData(raw)) != "54f0f5c0ebbbf44211648a6b6bd72f3a8c9f84543dd9dd3d67a7cdc5519df73d")
            throw new Exception("Native bow source snapshot changed; renew evidence explicitly");
        JsonNode Fixture() => source is null ? PortablePoseFixtures.Clip() : JsonNode.Parse(raw)!;
        NativeGenericScalarSampler Make(JsonNode n) => new(JsonSerializer.SerializeToElement(n));
        JsonNode Stream(JsonNode n) => n["m_MuscleClip"]!["m_Clip"]!["data"]!["m_StreamedClip"]!;
        JsonNode Dense(JsonNode n) => n["m_MuscleClip"]!["m_Clip"]!["data"]!["m_DenseClip"]!;
        void Near(double actual, double expected) { if (Math.Abs(actual - expected) > 1e-12) throw new Exception($"Expected {expected}, got {actual}"); }
        test("scalar fixture continuous cubic samples match expected values", () =>
        {
            var sampler = Make(Fixture());
            double[] times = [0, .5, 1, 2, 3, 4];
            double[] expected = source is null ? times.Select(t=>.25+t*t*t/1024).ToArray() : [.25283610820770264, .24968230655940715, .24659572541713715, .262381886430069, .26021499507941537, .25283610820770264];
            for (int i = 0; i < times.Length; i++) Near(sampler.Sample(times[i])[0], expected[i]);
            if (sampler.Duration != 4 || sampler.DenseSampleRate != 60 || sampler.ScalarCount != 91) throw new Exception("Native dimensions changed");
            // Check every supported source grid time including both interval endpoints.
            for (int frame = 0; frame <= 240; frame++)
                if (!sampler.Sample(frame / 60.0).All(double.IsFinite)) throw new Exception("Nonfinite frame");
        });
        test("native dense samples use frame-major packed16 offsets and preserve constants", () =>
        {
            var n = Fixture(); var packed = Convert.FromBase64String(Dense(n)["m_ACLArray"]!["Array"]!.GetValue<string>());
            packed[60 * 24 * 2] = 255; packed[60 * 24 * 2 + 1] = 255;
            Dense(n)["m_ACLArray"]!["Array"] = Convert.ToBase64String(packed);
            var sampler = Make(n); Near(sampler.Sample(1)[3], 1); Near(sampler.Sample(0)[3], source is null ? (30000-32767.5)/32767.5 : -.139482719157702);
            var constants = n["m_MuscleClip"]!["m_Clip"]!["data"]!["m_ConstantClip"]!["data"]!["Array"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
            if (!sampler.Sample(2)[27..].SequenceEqual(constants)) throw new Exception("Constants changed");
        });
        test("native sampling rejects extrapolation and nonfinite time and reconstructs between native dense frames", () =>
        {
            var sampler = Make(Fixture());
            foreach (double t in new[] { -.001, 4.001, double.NaN, double.PositiveInfinity, double.NegativeInfinity }) reject(() => sampler.Sample(t));
            // The two adjacent authored frames bound the reconstruction, so an off-grid time is exactly
            // interpolated between them instead of being snapped to one frame.
            var low = sampler.Sample(3 / 60.0); var high = sampler.Sample(4 / 60.0); var mid = sampler.Sample(3.5 / 60.0);
            if (mid.Length != low.Length) throw new Exception("Dense reconstruction changed the scalar layout");
            // Scalars 0..2 are the streamed cubic curves; their fractional expectation stays the reviewed
            // cubic polynomial above. The between-frame reconstruction is proved separately below with two
            // authored packed16 frames that actually differ, so snapping to either frame cannot pass.
        });
        test("native dense sampling reconstructs between two changing authored frames", () =>
        {
            var n = Fixture(); var dense = Dense(n);
            double factor = double.Parse(dense["m_PositionFactor"]!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
            var packed = Convert.FromBase64String(dense["m_ACLArray"]!["Array"]!.GetValue<string>());
            packed[(59 * 24 + 0) * 2] = 0; packed[(59 * 24 + 0) * 2 + 1] = 0;
            packed[(60 * 24 + 0) * 2] = 255; packed[(60 * 24 + 0) * 2 + 1] = 255;
            dense["m_ACLArray"]!["Array"] = Convert.ToBase64String(packed);
            var sampler = Make(n);
            double low = (0 - 32767.5) / 32767.5 * factor, high = (65535 - 32767.5) / 32767.5 * factor;
            Near(sampler.Sample(59 / 60.0)[3], low); Near(sampler.Sample(60 / 60.0)[3], high);
            Near(sampler.Sample(59.5 / 60.0)[3], (low + high) / 2);
            Near(sampler.Sample(59.25 / 60.0)[3], low + (high - low) / 4);
        });
        test("stream-only cubic supports fractional time and agrees on both sides of interior keys", () =>
        {
            var n = Fixture();
            Dense(n)["m_CurveCount"] = 0; Dense(n)["m_FrameCount"] = 0; Dense(n)["m_ACLArray"]!["Array"] = "";
            var constants = n["m_MuscleClip"]!["m_Clip"]!["data"]!["m_ConstantClip"]!["data"]!["Array"]!.AsArray();
            for (int i = 0; i < 24; i++) constants.Insert(0, 0.0);
            var sampler = Make(n);
            if (sampler.DenseSampleRate is not null) throw new Exception("Unexpected dense clock");
            double t = .7333333492279053;
            if (Math.Abs(sampler.Sample(t - 1e-8)[0] - sampler.Sample(t + 1e-8)[0]) > 1e-7) throw new Exception("Key boundary is discontinuous");
            if (sampler.Sample(.123)[0] == sampler.Sample(0)[0]) throw new Exception("Fractional sampling held initial value");
        });
        test("native streamed corruption and unsupported special layouts fail closed", () =>
        {
            // Frame zero starts at word17 after the three-key negative max sentinel.
            var n = Fixture(); Stream(n)["data"]!["Array"]![20] = 0u; reject(() => Make(n));
            n = Fixture(); Stream(n)["data"]!["Array"]![24] = 0; reject(() => Make(n)); // duplicate zero-frame channel
            n = Fixture(); Stream(n)["data"]!["Array"]![17] = 1u; reject(() => Make(n)); // no exact zero
            n = Fixture(); Stream(n)["data"]!["Array"]![20] = 0x7f800000u; reject(() => Make(n));
            n = Fixture(); Stream(n)["data"]!["Array"]!.AsArray().RemoveAt(Stream(n)["data"]!["Array"]!.AsArray().Count - 1); reject(() => Make(n));
            n = Fixture(); Stream(n)["data"]!["Array"]!.AsArray().Add(0); reject(() => Make(n));
        });
        test("native dense unsupported shapes counts and binding mismatches fail closed", () =>
        {
            var n = Fixture(); Dense(n)["m_ACLArray"]!["Array"]=null; reject(() => Make(n));
            n = Fixture(); Dense(n)["m_ACLArray"]!["Array"]=123; reject(() => Make(n));
            n = Fixture(); Dense(n)["m_FrameCount"]=8_000_001; reject(() => Make(n));
            n = Fixture(); Dense(n)["m_CurveCount"]=16385; reject(() => Make(n));
            n = Fixture(); Dense(n)["m_ACLArray"]!["Array"]=new string('A',24_000_001); reject(() => Make(n));
            n = Fixture(); Dense(n)["m_ACLType"] = 8; reject(() => Make(n));
            n = Fixture(); Dense(n)["m_nRotationCurves"] = 1; reject(() => Make(n));
            n = Fixture(); Dense(n)["m_FrameCount"] = 240; reject(() => Make(n));
            n = Fixture(); Dense(n)["m_PositionFactor"] = 0; reject(() => Make(n));
            n = Fixture(); Dense(n)["m_SampleRate"] = 0; reject(() => Make(n));
            n = Fixture(); n["m_ClipBindingConstant"]!["genericBindings"]!["Array"]![1]!["attribute"] = 3; reject(() => Make(n));
            n = Fixture(); n["m_AclCompressedBuffer"]!["OutputTrackCount"] = 1; reject(() => Make(n));
        });
    }
}
