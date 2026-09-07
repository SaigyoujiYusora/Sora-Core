using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;

namespace Sora.Core;

public sealed record LogicalResource(string Name, string Chunk, long Offset, long Length, bool Encrypted, long NonceSeed, string ChunkDigest = "", string PayloadDigest = "");
public sealed record BlockIndex(int Version, string Name, uint Trailer, byte[] Extension, LogicalResource[] Resources)
{
    private static readonly byte[] Key = Convert.FromHexString("E95B317AC4F828569D23A86BF271DCB53E846FA75C924D671DBA8E38F4CA52E1");

    public static BlockIndex Read(string path)
    {
        using var stream = File.OpenRead(path);
        Validation.Require(stream.Length >= 16 && stream.Length <= 64 * 1024 * 1024, "Invalid BLC length");
        byte[] encrypted = new byte[(int)stream.Length]; stream.ReadExactly(encrypted);
        byte[] plain = ChaChaStream.Transform(encrypted.AsSpan(12), Key, encrypted.AsSpan(0, 12));
        uint trailer = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(plain.Length - 4));
        using var input = new MemoryStream(plain, 0, plain.Length - 4, false);
        using var reader = new BinaryReader(input, new UTF8Encoding(false, true));
        int version = reader.ReadInt32();
        if (version < 11) reader.ReadInt32(); else version = 3;
        Validation.Require(version is 3 or 4, "Unsupported BLC version");
        string name = Text(reader); reader.ReadUInt64();
        int total = reader.ReadInt32(); reader.ReadUInt64(); reader.ReadByte();
        int chunks = Count(reader, 45);
        Validation.Require(total >= 0 && total <= 1_000_000, "BLC entry limit exceeded");
        var resources = new List<LogicalResource>();
        for (int chunk = 0; chunk < chunks; chunk++)
        {
            string chunkName = Convert.ToHexString(Exact(reader, 16)) + ".chk";
            Exact(reader, 16); long chunkLength = reader.ReadInt64(); reader.ReadByte();
            Validation.Require(chunkLength >= 0, "Invalid BLC chunk size");
            if (version > 3) reader.ReadUInt32();
            int files = Count(reader, 60);
            for (int file = 0; file < files; file++)
            {
                string logical = Text(reader).Replace('\\', '/');
                reader.ReadUInt64(); string chunkDigest = Convert.ToHexString(Exact(reader, 16)), payloadDigest = Convert.ToHexString(Exact(reader, 16));
                long offset = reader.ReadInt64(), length = reader.ReadInt64(); reader.ReadByte(); byte cipher = reader.ReadByte();
                Validation.Require(cipher <= 1 && offset >= 0 && length >= 0 && offset <= chunkLength - length, "Invalid BLC resource range");
                long seed = cipher == 1 ? reader.ReadInt64() : 0;
                if (version > 3) reader.ReadUInt32();
                resources.Add(new(logical, chunkName, offset, length, cipher == 1, seed, chunkDigest, payloadDigest));
                Validation.Require(resources.Count <= total, "BLC file count mismatch");
            }
        }
        Validation.Require(resources.Count == total && input.Length - input.Position <= 4096, "BLC structure length mismatch");
        var extension = Exact(reader, (int)(input.Length - input.Position));
        return new(version, name, trailer, extension, resources.ToArray());
    }

    public static byte[] Extract(string indexPath, LogicalResource resource)
    {
        Validation.Require(resource.Chunk.Length == 36 && resource.Chunk.EndsWith(".chk", StringComparison.Ordinal) && resource.Chunk[..32].All(Uri.IsHexDigit), "Invalid chunk identity");
        using var stream = File.OpenRead(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(indexPath))!, resource.Chunk));
        Validation.Require(resource.Offset >= 0 && resource.Length >= 0 && resource.Length <= DatabaseFile.MaxPayloadBytes && resource.Offset <= stream.Length - resource.Length, "Resource exceeds physical chunk bounds");
        stream.Position = resource.Offset;
        byte[] bytes = new byte[(int)resource.Length]; stream.ReadExactly(bytes);
        if (resource.Encrypted)
        {
            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(nonce, 3); BinaryPrimitives.WriteInt64LittleEndian(nonce[4..], resource.NonceSeed);
            bytes = ChaChaStream.Transform(bytes, Key, nonce);
        }
        if (resource.PayloadDigest.Length > 0)
            Validation.Require(Convert.ToHexString(MD5.HashData(bytes)).Equals(resource.PayloadDigest, StringComparison.OrdinalIgnoreCase), "BLC payload digest mismatch: " + resource.Name);
        return bytes;
    }

    private static byte[] Exact(BinaryReader reader, int length)
    {
        Validation.Require(length >= 0 && length <= reader.BaseStream.Length - reader.BaseStream.Position, "BLC read exceeds bounds");
        var bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException(); return bytes;
    }
    private static string Text(BinaryReader reader) => new UTF8Encoding(false, true).GetString(Exact(reader, reader.ReadUInt16()));
    private static int Count(BinaryReader reader, int width)
    {
        int count = reader.ReadInt32(); Validation.Require(count >= 0 && count <= (reader.BaseStream.Length - reader.BaseStream.Position) / width, "Invalid BLC count"); return count;
    }
}
