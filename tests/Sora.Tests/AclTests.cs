using System.Buffers.Binary;
using Sora.Core;
using System.Diagnostics;
using System.Reflection;

internal static class AclTests
{
    public static void Run(Action<string, Action> test, Action<Action> reject)
    {
        // The nonexistent executable proves these malformed inputs fail before launching native code.
        void Invalid(byte[] transform, byte[]? scalar = null) => reject(() => {
            try { AclCodec.Decode(transform, scalar ?? [], "missing-acl-test-worker.exe"); }
            catch (InvalidDataException e) when (e.InnerException is not null) {
                throw new Exception("Malformed ACL input reached process startup.", e);
            }
        });
        test("ACL rejects truncated headers and empty streams", () => {
            for (int n = 0; n < 32; n++) Invalid(new byte[n]);
            Invalid([]);
        });
        test("ACL rejects declared sizes outside bounded buffers", () => {
            foreach (uint size in new uint[] { 0, 31, 33, uint.MaxValue }) {
                var b = Header(); BinaryPrimitives.WriteUInt32LittleEndian(b, size); Invalid(b);
            }
            Invalid(new byte[32 * 1024 * 1024 + 1]);
            Invalid([.. Header(), .. new byte[16]]);
        });
        test("ACL rejects hash corruption", () => { var b = Header(); b[31] ^= 1; Invalid(b); });
        test("ACL rejects unsupported tag version algorithm and track type", () => {
            foreach (int offset in new[] { 8, 12, 14, 15 }) {
                var b = Header(); b[offset] ^= 1; Rehash(b); Invalid(b);
            }
            Invalid(Header(0));
            Invalid(Header(), Header(12));
        });
        test("ACL rejects database-bound transforms", () => {
            var b = Header(); b[29] |= 1; Rehash(b); Invalid(b);
        });
        test("ACL rejects external default poses without default data", () => {
            var value=Header();value[29]&=unchecked((byte)~2);Rehash(value);Invalid(value);
        });
        test("ACL rejects subnormal rates and overflowing derived duration", () => {
            foreach(float rate in new[]{float.Epsilon,float.BitIncrement(0f),1.17549435e-38f}) {
                var value=LayoutHeader(12,121,false,rate);Invalid(value);
            }
        });
        test("ACL rejects impossible counts and output allocation", () => {
            foreach (uint count in new uint[] { 0, 65536, uint.MaxValue }) {
                var b = Header(); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), count); Rehash(b); Invalid(b);
            }
            foreach (uint count in new uint[] { 0, 1000001, uint.MaxValue, 1000000 }) {
                var b = Header(); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), count); Rehash(b); Invalid(b);
            }
        });
        test("ACL rejects nonfinite and unsupported sample rates", () => {
            foreach (float rate in new[] { float.NaN, float.PositiveInfinity, 0, -1, 1001 }) {
                var b = Header(); BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(24), rate); Rehash(b); Invalid(b);
            }
        });
        test("ACL requires matching transform scalar time layouts", () => {
            foreach (int offset in new[] { 20, 24 }) {
                var b = Header(0); b[offset] ^= 1; Rehash(b); Invalid(Header(), b);
            }
        });
        test("ACL matches a wrap-optimized scalar endpoint", () => {
            var value=Timeline(LayoutHeader(12,121,false),LayoutHeader(0,120,true));
            if(value.Samples!=121||value.Count!=1331)throw new Exception("Wrong inclusive scalar timeline");
        });
        test("ACL matches a wrap-optimized transform endpoint", () => {
            if(Timeline(LayoutHeader(12,226,true),LayoutHeader(0,227,false)).Samples!=227)throw new Exception("Wrong inclusive transform timeline");
        });
        test("ACL includes final seek when both streams wrap", () => {
            if(Timeline(LayoutHeader(12,120,true),LayoutHeader(0,120,true)).Samples!=121)throw new Exception("Missing loop endpoint");
        });
        test("ACL scalar-only layout has no transform contribution", () => {
            var value=Timeline([],LayoutHeader(0,120,true));
            if(value.Samples!=121||value.Count!=121)throw new Exception("Scalar-only layout differs");
        });
        test("ACL unequal effective durations remain rejected", () => {
            reject(()=>Timeline(LayoutHeader(12,121,false),LayoutHeader(0,120,false)));
            reject(()=>Timeline(LayoutHeader(12,121,false),LayoutHeader(0,121,true)));
            reject(()=>Timeline(LayoutHeader(12,121,false),LayoutHeader(0,120,true,30)));
        });
        test("ACL inclusive endpoint participates in output bound",()=>reject(()=>Timeline(LayoutHeader(12,1000000,true),[])));
        test("ACL bounds timeout values before starting worker", () => {
            reject(()=>AclCodec.Decode(Header(),[],timeoutMilliseconds:0));
            reject(()=>AclCodec.Decode(Header(),[],timeoutMilliseconds:60001));
        });
        test("ACL rejects malformed worker responses and oversized diagnostics", () => {
            string? previous=Environment.GetEnvironmentVariable("SORA_ACL_TEST_MODE");
            try {
                foreach(string mode in new[]{"invalid","oversized-error"}) {
                    Environment.SetEnvironmentVariable("SORA_ACL_TEST_MODE",mode);
                    reject(()=>AclCodec.Decode(Header(),[],Assembly.GetExecutingAssembly().Location,5000));
                }
            } finally { Environment.SetEnvironmentVariable("SORA_ACL_TEST_MODE",previous); }
        });
        test("ACL terminates stalled worker within deadline", () => {
            string? previous=Environment.GetEnvironmentVariable("SORA_ACL_TEST_MODE");
            string? previousPid=Environment.GetEnvironmentVariable("SORA_ACL_TEST_PID_PATH");
            string pidFile=Path.Combine(Path.GetTempPath(),"sora-acl-timeout-"+Guid.NewGuid().ToString("N")+".txt");
            try {
                Environment.SetEnvironmentVariable("SORA_ACL_TEST_MODE","timeout");
                Environment.SetEnvironmentVariable("SORA_ACL_TEST_PID_PATH",pidFile);
                var timer=Stopwatch.StartNew();
                try { AclCodec.Decode(Header(),[],Assembly.GetExecutingAssembly().Location,1000); throw new Exception("Stalled worker unexpectedly completed"); }
                catch(InvalidDataException error) when(error.InnerException is OperationCanceledException) { }
                if(timer.Elapsed<TimeSpan.FromMilliseconds(900)||timer.Elapsed>TimeSpan.FromSeconds(6)) throw new Exception("Worker timeout was not bounded");
                int pid=int.Parse(File.ReadAllText(pidFile));
                try {using var worker=Process.GetProcessById(pid);if(!worker.HasExited)throw new Exception("Timed-out worker remains alive");}
                catch(ArgumentException) { }
            } finally {
                Environment.SetEnvironmentVariable("SORA_ACL_TEST_MODE",previous);
                Environment.SetEnvironmentVariable("SORA_ACL_TEST_PID_PATH",previousPid);
                if(File.Exists(pidFile)) File.Delete(pidFile);
            }
        });
    }

    private static byte[] Header(byte type = 12)
    {
        // Complete canonical constant payloads, not header-only pseudo streams.
        var b = new byte[type==12?123:75];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)b.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 0xAC11AC11);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), 10);
        b[15] = type; b[16] = 1; b[20] = 1;
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(24), 30);
        if(type==12) {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28),0x23e);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32),1);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(64),uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(68),52);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(72),68);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(76),76);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(80),76);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(96),76);
        } else {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(36),20);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40),24);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(44),28);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(48),28);
        }
        Rehash(b); return b;
    }

    private static byte[] LayoutHeader(byte type,int samples,bool wrap,float rate=60) {
        var value=Header(type);
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(20),samples);
        BinaryPrimitives.WriteSingleLittleEndian(value.AsSpan(24),rate);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(28),(type==12?0x23eu:0u)|(wrap?1u<<30:0u));
        Rehash(value);return value;
    }

    private static (int Samples,int Count) Timeline(byte[] transforms,byte[] scalars) {
        try {
            var value=typeof(AclCodec).GetMethod("Validate",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[transforms,scalars])!;
            return ((int)value.GetType().GetField("Item3")!.GetValue(value)!,(int)value.GetType().GetField("Item5")!.GetValue(value)!);
        }catch(TargetInvocationException error) when(error.InnerException is InvalidDataException) {throw (InvalidDataException)error.InnerException;}
    }

    private static void Rehash(byte[] b)
    {
        uint hash = 2166136261;
        foreach (byte value in b.AsSpan(8)) hash = unchecked((hash ^ value) * 16777619);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), hash);
    }
}
