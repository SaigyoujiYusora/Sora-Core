namespace Sora.Core;

public static class Lz4Block
{
    public static byte[] Decode(ReadOnlySpan<byte> source, int outputLength, bool endfield = false)
    {
        Validation.Require(outputLength >= 0 && outputLength <= 64 * 1024 * 1024, "LZ4 output exceeds limit");
        byte[] target = new byte[outputLength];
        int read = 0, write = 0;
        while (read < source.Length)
        {
            int token = source[read++];
            int literal = endfield ? (token & 3) + ((token >> 2) & 12) : token >> 4;
            int match = endfield ? ((token >> 2) & 3) + ((token >> 4) & 12) : token & 15;
            literal = Length(source, ref read, literal);
            Validation.Require(literal <= source.Length - read && literal <= target.Length - write, "Invalid LZ4 literal range");
            source.Slice(read, literal).CopyTo(target.AsSpan(write)); read += literal; write += literal;
            if (read == source.Length) break;
            Validation.Require(source.Length - read >= 2, "Missing LZ4 match offset");
            int distance = endfield ? source[read] * 256 + source[read + 1] : source[read] + source[read + 1] * 256;
            read += 2;
            match = checked(Length(source, ref read, match) + 4);
            Validation.Require(distance > 0 && distance <= write && match <= target.Length - write, "Invalid LZ4 match range");
            for (int i = 0; i < match; i++) { target[write] = target[write - distance]; write++; }
        }
        Validation.Require(write == outputLength, "LZ4 decoded length mismatch");
        return target;
    }

    private static int Length(ReadOnlySpan<byte> source, ref int cursor, int initial)
    {
        if (initial != 15) return initial;
        byte next;
        do
        {
            Validation.Require(cursor < source.Length, "Truncated LZ4 length");
            next = source[cursor++]; initial = checked(initial + next);
        } while (next == 255);
        return initial;
    }
}
