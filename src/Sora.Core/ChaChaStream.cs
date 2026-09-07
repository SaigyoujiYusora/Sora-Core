using System.Buffers.Binary;
using System.Numerics;

namespace Sora.Core;

public static class ChaChaStream
{
    public static byte[] Transform(ReadOnlySpan<byte> input, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter = 1)
    {
        Validation.Require(key.Length == 32 && nonce.Length == 12, "ChaCha20 requires a 32-byte key and 12-byte nonce");
        Validation.Require((ulong)counter + ((ulong)input.Length + 63) / 64 <= (ulong)uint.MaxValue + 1, "ChaCha20 counter would wrap");
        uint[] initial = [0x61707865, 0x3320646e, 0x79622d32, 0x6b206574, 0, 0, 0, 0, 0, 0, 0, 0, counter, 0, 0, 0];
        for (int i = 0; i < 8; i++) initial[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i * 4, 4));
        for (int i = 0; i < 3; i++) initial[13 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(i * 4, 4));
        byte[] output = new byte[input.Length];
        Span<byte> block = stackalloc byte[64];
        for (int start = 0; start < input.Length; start += 64)
        {
            var state = (uint[])initial.Clone();
            for (int round = 0; round < 20; round++)
            {
                for (int lane = 0; lane < 4; lane++)
                {
                    int a = lane, b = 4 + (lane + round % 2) % 4, c = 8 + (lane + 2 * (round % 2)) % 4, d = 12 + (lane + 3 * (round % 2)) % 4;
                    state[a] += state[b]; state[d] = BitOperations.RotateLeft(state[d] ^ state[a], 16);
                    state[c] += state[d]; state[b] = BitOperations.RotateLeft(state[b] ^ state[c], 12);
                    state[a] += state[b]; state[d] = BitOperations.RotateLeft(state[d] ^ state[a], 8);
                    state[c] += state[d]; state[b] = BitOperations.RotateLeft(state[b] ^ state[c], 7);
                }
            }
            for (int i = 0; i < 16; i++) BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(4 * i, 4), state[i] + initial[i]);
            int count = Math.Min(64, input.Length - start);
            for (int i = 0; i < count; i++) output[start + i] = (byte)(input[start + i] ^ block[i]);
            initial[12]++;
        }
        return output;
    }
}
