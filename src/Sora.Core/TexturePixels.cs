using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Sora.Core;

public static class TexturePixels
{
    private const string NativeHash = "C5C8CC269D4605E82CBAF69506914A9031F62A8B3844316A2121E609A9683DF9";
    private static readonly object NativeLock = new();
    private static readonly Dictionary<string, Decoder> Decoders = new();
    private static IntPtr nativeHandle;

    // Audited dllmain.cpp: bool32_t __stdcall (const void*, int32_t, int32_t, void*).
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Decoder([In] byte[] data, int width, int height, [Out] byte[] image);

    public static byte[] Decode(int unityFormat, int width, int height, byte[] topMip)
    {
        int pixels = PixelCount(width, height);
        if (unityFormat == 4)
        {
            if (topMip is null || topMip.Length != checked(pixels * 4))
                throw new InvalidDataException("RGBA32 top mip length does not match its dimensions.");
            return (byte[])topMip.Clone();
        }
        var (export, blockBytes) = unityFormat switch
        {
            10 => ("DecodeDXT1", 8), 12 => ("DecodeDXT5", 16),
            26 => ("DecodeBC4", 8), 27 => ("DecodeBC5", 16),
            24 => ("DecodeBC6", 16), 25 => ("DecodeBC7", 16),
            _ => throw new InvalidDataException($"Unsupported Unity texture format {unityFormat}.")
        };
        int expected = checked(((width + 3) / 4) * ((height + 3) / 4) * blockBytes);
        if (topMip is null || topMip.Length != expected)
            throw new InvalidDataException($"Texture top mip must contain exactly {expected} bytes.");
        var decoder = GetDecoder(export);
        byte[] rgba = new byte[pixels * 4];
        if (decoder(topMip, width, height, rgba) == 0)
            throw new InvalidDataException("Native texture decoding failed.");
        // color.h packs little-endian BGRA; the public contract is RGBA.
        for (int i = 0; i < rgba.Length; i += 4)
            (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
        return rgba;
    }

    private static Decoder GetDecoder(string export)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new InvalidDataException("Texture decoding requires Windows x64.");
        lock (NativeLock)
        {
            if (Decoders.TryGetValue(export, out var existing)) return existing;
            try
            {
                if (nativeHandle == IntPtr.Zero)
                {
                    string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Texture2DDecoderNative.dll"));
                    // Keep replacement/deletion excluded between hashing and loading.
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (!Convert.ToHexString(SHA256.HashData(file)).Equals(NativeHash, StringComparison.Ordinal))
                        throw new InvalidDataException("Texture decoder SHA256 mismatch.");
                    nativeHandle = NativeLibrary.Load(path);
                }
                var decoder = Marshal.GetDelegateForFunctionPointer<Decoder>(NativeLibrary.GetExport(nativeHandle, export));
                Decoders.Add(export, decoder);
                return decoder;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                throw new InvalidDataException("Unable to load the audited local texture decoder.", error);
            }
        }
    }

    public static byte[] Png(int width, int height, byte[] rgba, bool flipY = false)
    {
        int pixels = PixelCount(width, height);
        if (rgba is null || rgba.Length != pixels * 4)
            throw new InvalidDataException("RGBA data length does not match texture dimensions.");
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8; header[9] = 6;
        Chunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            int stride = width * 4;
            for (int y = 0; y < height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(rgba, (flipY ? height - 1 - y : y) * stride, stride);
            }
        }
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static int PixelCount(int width, int height)
    {
        if (width is < 1 or > 16384 || height is < 1 or > 16384 || (long)width * height > 16_777_216)
            throw new InvalidDataException("Texture dimensions exceed supported bounds.");
        return width * height;
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        output.Write(word);
        byte[] name = Encoding.ASCII.GetBytes(type);
        output.Write(name); output.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte value in name) crc = CrcByte(crc, value);
        foreach (byte value in data) crc = CrcByte(crc, value);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc);
        output.Write(word);
    }

    private static uint CrcByte(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
        return crc;
    }
}
