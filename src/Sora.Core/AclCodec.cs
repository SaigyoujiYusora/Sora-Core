using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sora.Core;

public sealed record AclSamples(int Tracks, int Scalars, int Samples, float SampleRate, float[] Values);

/// <summary>ACL 2.1 transform/scalar decoding in a disposable native worker process.</summary>
public static class AclCodec
{
    private const int MaxBuffer = 32 * 1024 * 1024, MaxValues = 8 * 1024 * 1024;
    private const int MaxRequest = 90 * 1024 * 1024, MaxResponse = 192 * 1024 * 1024;
    private const string DllHash = "C84A6A84A1C9B9D35C75659507D3CA905E6144AB4CC527540F808D019142B5F5";
    private sealed record Request(byte[] Transforms, byte[] Scalars);
    private sealed record Header(int Size, int Tracks, int Samples, float Rate, bool Wrap) {
        public int InclusiveSamples => Samples + (Wrap ? 1 : 0);
    }

    public static AclSamples Decode(byte[] transforms, byte[] scalars, string? workerExecutable = null, int timeoutMilliseconds = 60000)
    {
        var (t, s, samples, rate, count) = Validate(transforms, scalars);
        if(timeoutMilliseconds is < 1 or > 60000) throw new InvalidDataException("ACL worker timeout must be between 1 and 60000 milliseconds.");
        try
        {
            string executable = Path.GetFullPath(workerExecutable ?? Path.Combine(AppContext.BaseDirectory, "Sora-Core.dll"));
            bool assembly = executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            string host = Environment.ProcessPath is {} processPath && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? processPath : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var start = new ProcessStartInfo(assembly ? host : executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if(assembly) start.ArgumentList.Add(executable);
            start.ArgumentList.Add("acl-worker");
            using var process = Process.Start(start) ?? throw new InvalidDataException("Cannot start ACL worker.");
            using var timeout = new CancellationTokenSource(timeoutMilliseconds);
            try
            {
                Task<string> stdout = ReadLimited(process.StandardOutput, MaxResponse, timeout.Token);
                Task<string> stderr = ReadLimited(process.StandardError, 65536, timeout.Token);
                Task send = Send(process, new Request(transforms, scalars), timeout.Token);
                var pending = new List<Task> { stdout, stderr, send, process.WaitForExitAsync(timeout.Token) };
                // Observe each failure immediately, including a reader hitting its output limit.
                while (pending.Count != 0)
                {
                    Task finished = Task.WhenAny(pending).WaitAsync(timeout.Token).GetAwaiter().GetResult();
                    finished.GetAwaiter().GetResult();
                    pending.Remove(finished);
                }
                if (process.ExitCode != 0) throw new InvalidDataException($"ACL worker failed ({process.ExitCode}): {stderr.Result}");
                var result = JsonSerializer.Deserialize<AclSamples>(stdout.Result) ?? throw new InvalidDataException("Empty ACL worker result.");
                if (result.Tracks != (t?.Tracks ?? 0) || result.Scalars != (s?.Tracks ?? 0) || result.Samples != samples || result.SampleRate != rate || result.Values is null || result.Values.Length != count || result.Values.Any(v => !float.IsFinite(v)))
                    throw new InvalidDataException("Invalid ACL worker result layout or values.");
                return result;
            }
            finally {
                timeout.Cancel();
                if (!process.HasExited) {
                    process.Kill(entireProcessTree: true);
                    if(!process.WaitForExit(5000)) throw new InvalidDataException("ACL worker did not exit after termination.");
                }
            }
        }
        catch (Exception e) when (e is not InvalidDataException) { throw new InvalidDataException("ACL worker decode failed.", e); }
    }

    private static async Task Send(Process process, Request request, CancellationToken token)
    {
        await JsonSerializer.SerializeAsync(process.StandardInput.BaseStream, request, cancellationToken: token);
        process.StandardInput.Close();
    }

    private static async Task<string> ReadLimited(TextReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[8192];
        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (result.Length > limit - n) throw new InvalidDataException("ACL worker message exceeds limit.");
            result.Append(buffer, 0, n);
        }
        return result.ToString();
    }

    private static (Header? Transform, Header? Scalar, int Samples, float Rate, int Count) Validate(byte[] transforms, byte[] scalars)
    {
        if (transforms is null || scalars is null) throw new InvalidDataException("Missing ACL buffer.");
        var t = transforms.Length == 0 ? null : Parse(transforms, 12);
        var s = scalars.Length == 0 ? null : Parse(scalars, 0);
        var timeline=t ?? s ?? throw new InvalidDataException("Both ACL streams are empty.");
        if (t is not null && s is not null && (s.InclusiveSamples != t.InclusiveSamples || s.Rate != t.Rate)) throw new InvalidDataException("ACL effective time layouts differ.");
        long count = ((long)(t?.Tracks ?? 0) * 10 + (s?.Tracks ?? 0)) * timeline.InclusiveSamples;
        if (count <= 0 || count > MaxValues) throw new InvalidDataException("ACL output exceeds limit.");
        return (t, s, timeline.InclusiveSamples, timeline.Rate, (int)count);
    }

    private static Header Parse(byte[] data, byte type)
    {
        if (data is null || data.Length < 32 || data.Length > MaxBuffer) throw new InvalidDataException("Invalid ACL buffer size.");
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (size < 32 || size > data.Length || data.Length - size > 15) throw new InvalidDataException("Invalid ACL declared size.");
        uint hash = 2166136261;
        foreach (byte b in data.AsSpan(8, (int)size - 8)) hash = unchecked((hash ^ b) * 16777619);
        if (hash != BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4))) throw new InvalidDataException("ACL hash mismatch.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)) != 0xAC11AC11 || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12)) != 10 || data[14] != 0 || data[15] != type)
            throw new InvalidDataException("Unsupported ACL format, algorithm, version or track type.");
        // ACL 2.1 tracks_header.misc_packed bit 8 marks database-dependent transforms.
        if (type == 12 && (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28)) & (1u << 8)) != 0)
            throw new InvalidDataException("Database-bound ACL tracks are unsupported.");
        if(type==12 && (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28)) & (1u<<9))==0)
            throw new InvalidDataException("ACL external default poses are unsupported.");
        if(size<(type==12?123:75))throw new InvalidDataException("ACL payload is shorter than its minimum structural layout.");
        uint tracks = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)), samples = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20));
        float rate = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(24));
        if (tracks == 0 || tracks > 65535 || samples == 0 || samples > 1000000 || !float.IsNormal(rate) || rate <= 0 || rate > 1000)
            throw new InvalidDataException("Invalid ACL tracks, samples or sample rate.");
        // ACL v2.1 wrap optimization omits the repeated first endpoint sample.
        bool wrap=(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28)) & (1u<<30))!=0;
        float step=1.0f/rate;
        if(!float.IsFinite(step)||!float.IsFinite((samples-1+(wrap?1u:0u))*step))throw new InvalidDataException("ACL derived sample step/duration is not finite.");
        return new((int)size, (int)tracks, (int)samples, rate, wrap);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DecodeStreams(nint transforms, uint transformSize, nint scalars, uint scalarSize,
        uint samples, float rate, nint values, uint capacity);
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);

    /// <summary>Only call from the dedicated acl-worker CLI command; native crashes terminate this process.</summary>
    public static void RunWorker(Stream input, TextWriter output)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new InvalidDataException("ACL worker requires Windows x64.");
            SetErrorMode(0x0001 | 0x0002 | 0x8000);
            using var reader = new StreamReader(input, Encoding.UTF8, false, 8192, leaveOpen: true);
            string json = ReadLimited(reader, MaxRequest, CancellationToken.None).GetAwaiter().GetResult();
            var request = JsonSerializer.Deserialize<Request>(json) ?? throw new InvalidDataException("Missing ACL request.");
            var (t, s, samples, rate, count) = Validate(request.Transforms, request.Scalars);
            string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Sora.Acl.dll"));
            using (var dll = File.OpenRead(path))
                if (!Convert.ToHexString(SHA256.HashData(dll)).Equals(DllHash, StringComparison.Ordinal)) throw new InvalidDataException("ACL native DLL hash mismatch.");
            int scalarOffset = ((t?.Size ?? 0) + 15) & ~15;
            var combined = new byte[scalarOffset + (s?.Size ?? 0)];
            if (t is not null) request.Transforms.AsSpan(0, t.Size).CopyTo(combined);
            if (s is not null) request.Scalars.AsSpan(0, s.Size).CopyTo(combined.AsSpan(scalarOffset));
            nint allocation = Marshal.AllocHGlobal(combined.Length + 15), library = 0, outputAllocation = 0;
            try
            {
                nint aligned = (allocation + 15) & ~(nint)15;
                Marshal.Copy(combined, 0, aligned, combined.Length);
                library = NativeLibrary.Load(path);
                var decode = Marshal.GetDelegateForFunctionPointer<DecodeStreams>(NativeLibrary.GetExport(library, "SoraDecodeStreams"));
                outputAllocation=Marshal.AllocHGlobal(checked(count*sizeof(float)));
                int status=decode(t is null ? 0 : aligned,(uint)(t?.Size ?? 0),s is null ? 0 : aligned+scalarOffset,(uint)(s?.Size ?? 0),(uint)samples,rate,outputAllocation,(uint)count);
                if(status!=0) throw new InvalidDataException("ACL native decode rejected the request ("+status+").");
                var values = new float[count];
                Marshal.Copy(outputAllocation, values, 0, count);
                if (values.Any(v => !float.IsFinite(v))) throw new InvalidDataException("ACL native output is invalid.");
                output.Write(JsonSerializer.Serialize(new AclSamples(t?.Tracks ?? 0, s?.Tracks ?? 0, samples, rate, values)));
            }
            finally { if (outputAllocation!=0) Marshal.FreeHGlobal(outputAllocation); if (library != 0) NativeLibrary.Free(library); Marshal.FreeHGlobal(allocation); }
        }
        catch (Exception e) when (e is not InvalidDataException) { throw new InvalidDataException("ACL native worker failed.", e); }
    }
}
