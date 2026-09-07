using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Sora.Core.Vendor;

namespace Sora.Core;

internal sealed record VfsBlock(uint UncompressedSize, uint CompressedSize, int CompressionType);
internal sealed record VfsHeader(uint CompressedBlocksInfoSize, uint UncompressedBlocksInfoSize, uint Flags, long DataOffset);
internal sealed record VfsNode(string Name, long Offset, long Size);

internal static class VfsFields
{
    public static bool IsValidHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8) return false;
        uint first = BinaryPrimitives.ReadUInt32BigEndian(bytes) ^ 0x4a92f0cd;
        return BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]) == (((first << 2) & 0xffff0000) ^ BitOperations.RotateRight(first, 14) ^ 0xd8b1e637);
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32BigEndian(bytes[at..]);
    private static ushort U16(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt16BigEndian(bytes[at..]);
    private static uint Size32(ushort high, ushort low) => BitOperations.RotateRight(((uint)(high ^ low ^ 0xa121) << 16) | low, 18) ^ 0xf74324ee;
    private static long Size64(uint high, uint low) => unchecked((long)(BitOperations.RotateLeft(((ulong)(high ^ low ^ 0xdad76848) << 32) | low, 14) ^ 0xa4f1a11747816520));

    public static VfsHeader ReadHeader(ReadOnlySpan<byte> bytes)
    {
        Validation.Require(bytes.Length >= 48, "Truncated VFS header");
        return new(Size32(U16(bytes, 38), U16(bytes, 8)), Size32(U16(bytes, 26), U16(bytes, 32)),
            U32(bytes, 22) ^ U32(bytes, 10) ^ 0xa7f49310, (U32(bytes, 14) ^ U32(bytes, 10)) >= 7 ? 48 : 40);
    }

    public static int Count(ReadOnlySpan<byte> bytes, int at, bool nodes)
    {
        uint encoded = U32(bytes, at) ^ (nodes ? 0x6b0ae55du : 0x23f77b8au);
        uint combined = ((encoded ^ (encoded << 16)) & 0xffff0000) | (encoded & 0xffff);
        return unchecked((int)(BitOperations.RotateRight(combined, 18) ^ (nodes ? 0xe4c1d9f2u : 0x91ce0a4fu)));
    }

    public static VfsBlock Block(ReadOnlySpan<byte> bytes, int at)
    {
        uint encoded = (uint)(U16(bytes, at + 6) ^ 0x9cd6);
        uint joined = (((encoded >> 8) ^ encoded) & 255) << 8 | (encoded & 255);
        uint flags = U16(bytes, at + 4) ^ (((joined << 14) | (joined >> 2)) & 65535) ^ 0x523f;
        return new(Size32(U16(bytes, at), U16(bytes, at + 4)), Size32(U16(bytes, at + 2), U16(bytes, at + 8)), (int)(flags & 63));
    }

    public static VfsNode Node(ReadOnlySpan<byte> bytes, int at, out int next)
    {
        int length = bytes[(at + 16)..].IndexOf((byte)0);
        Validation.Require(length >= 0 && length < 64, "Invalid VFS name length");
        byte[] text = bytes.Slice(at + 16, length).ToArray();
        for (int i = 0; i < text.Length; i++) text[i] ^= (byte)(i ^ 0x97);
        next = checked(at + 21 + length);
        Validation.Require(next <= bytes.Length, "Truncated VFS node");
        return new(Encoding.ASCII.GetString(text), Size64(U32(bytes, at + 12), U32(bytes, at + 8)), Size64(U32(bytes, at + 4), U32(bytes, next - 4)));
    }

    public static byte[] Decrypt(byte[] input, bool sampled)
    {
        lock (typeof(VfsFields))
        {
            VFSAES.InitKeys(VfsConstants.VFSAESSBox, VfsConstants.VFSAESKey, VfsConstants.VFSAESIV, 0xf19ab7752cdd0196);
            if (!sampled || input.Length <= 256) return VFSAES.Decrypt(input);
            int pieces = Math.Min(input.Length / 16, 256), width = Math.Max(1, 256 / (input.Length / 16));
            var sample = new byte[256];
            for (int i = 0; i < pieces; i++) input.AsSpan(i * 16, width).CopyTo(sample.AsSpan(i * width));
            byte[] decoded = VFSAES.Decrypt(sample), result = (byte[])input.Clone();
            for (int i = 0; i < pieces; i++) decoded.AsSpan(i * width, width).CopyTo(result.AsSpan(i * 16));
            return result;
        }
    }
}
