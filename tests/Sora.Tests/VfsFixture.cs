using System.Buffers.Binary;
using System.Numerics;
using System.Text;

internal static class VfsFixture
{
    public static byte[] Create()
    {
        string name = "fixture";
        int infoSize = 4 + 10 + 4 + 21 + name.Length;
        byte[] data = new byte[48 + infoSize + 6];
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at), value);
        void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at), value);
        static (ushort High, ushort Low) Size(uint value)
        {
            uint rotated = BitOperations.RotateLeft(value ^ 0xf74324ee, 18);
            ushort low = (ushort)rotated;
            return ((ushort)((rotated >> 16) ^ low ^ 0xa121), low);
        }
        static uint Count(uint value, bool nodes)
        {
            uint mixed = BitOperations.RotateLeft(value ^ (nodes ? 0xe4c1d9f2u : 0x91ce0a4fu), 18);
            uint word = (((mixed >> 16) ^ mixed) << 16) | (mixed & 65535);
            return word ^ (nodes ? 0x6b0ae55du : 0x23f77b8au);
        }
        static (uint High, uint Low) Extent(ulong value)
        {
            ulong mixed = BitOperations.RotateRight(value ^ 0xa4f1a11747816520, 14);
            return ((uint)(mixed >> 32) ^ (uint)mixed ^ 0xdad76848, (uint)mixed);
        }
        U32(0, 0x4a92f0cd); U32(4, 0xd8b1e637);
        var info = Size((uint)infoSize); U16(8, info.Low); U16(38, info.High); U16(26, info.High); U16(32, info.Low);
        U32(14, 7); U32(22, 0xa7f49310);
        U32(48, Count(1, false));
        var block = Size(6); U16(52, block.High); U16(54, block.High); U16(56, block.Low); U16(60, block.Low);
        uint target = (uint)(block.Low ^ 0x523f);
        uint joined = ((target << 2) | (target >> 14)) & 65535;
        uint lowByte = joined & 255, highByte = (joined >> 8) ^ lowByte;
        U16(58, (ushort)(((highByte << 8) | lowByte) ^ 0x9cd6));
        U32(62, Count(1, true));
        var offset = Extent(1); var length = Extent(3);
        U32(70, length.High); U32(74, offset.Low); U32(78, offset.High);
        for (int i = 0; i < name.Length; i++) data[82 + i] = (byte)(name[i] ^ (i ^ 0x97));
        U32(83 + name.Length, length.Low);
        Encoding.ASCII.GetBytes("abcdef").CopyTo(data, 48 + infoSize);
        return data;
    }
}
