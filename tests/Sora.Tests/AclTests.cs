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
        test("ACL rejects truncated headers and scalar-only input", () => {
            for (int n = 0; n < 32; n++) Invalid(new byte[n]);
            Invalid([], Header(0));
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
        var b = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 0xAC11AC11);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), 10);
        b[15] = type; b[16] = 1; b[20] = 1;
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(24), 30);
        Rehash(b); return b;
    }

    private static void Rehash(byte[] b)
    {
        uint hash = 2166136261;
        foreach (byte value in b.AsSpan(8)) hash = unchecked((hash ^ value) * 16777619);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), hash);
    }
}
