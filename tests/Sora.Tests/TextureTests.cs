using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Sora.Core;

internal static class TextureTests
{
    public static void Run(Action<string, Action> test, Action<Action> reject)
    {
        test("native RGBA32 ramps retain byte order alpha and independent storage", () => {
            byte[] source = [1,2,3,4,250,128,0,255];
            byte[] decoded = TexturePixels.Decode(4,2,1,source);
            Equal(decoded,source); decoded[0]=99;
            if(source[0]!=1) throw new Exception("Decoder aliases source data");
            reject(()=>TexturePixels.Decode(4,2,1,source[..^1]));
            reject(()=>TexturePixels.Decode(4,2,1,[..source,0]));
            reject(()=>TexturePixels.Decode(4,2,1,null!));
        });
        test("BC1 solid red preserves RGBA channel order and clipped block dimensions", () =>
        {
            byte[] block = [0, 248, 0, 0, 0, 0, 0, 0];
            foreach (var (w, h) in new[] { (4, 4), (1, 1), (3, 2) })
            {
                byte[] pixels = TexturePixels.Decode(10, w, h, block);
                Equal(pixels, Enumerable.Range(0, w * h).SelectMany(_ => new byte[] { 255, 0, 0, 255 }).ToArray());
            }
        });
        test("texture dimensions and top mip lengths reject before native access", () =>
        {
            foreach (var (w, h) in new[] { (0, 4), (4, -1), (16385, 1), (4097, 4096), (int.MaxValue, int.MaxValue) })
            {
                reject(() => TexturePixels.Decode(10, w, h, []));
                reject(() => TexturePixels.Png(w, h, []));
            }
            foreach (int format in new[] { 10, 12, 26, 27, 24, 25 })
            {
                int size = format is 10 or 26 ? 8 : 16;
                reject(() => TexturePixels.Decode(format, 4, 4, new byte[size - 1]));
                reject(() => TexturePixels.Decode(format, 4, 4, new byte[size + 1]));
                reject(() => TexturePixels.Decode(format, 5, 4, new byte[size]));
                reject(() => TexturePixels.Decode(format, 4, 4, null!));
            }
            reject(() => TexturePixels.Decode(999, 4, 4, new byte[8]));
            reject(() => TexturePixels.Png(1, 1, new byte[3]));
            reject(() => TexturePixels.Png(1, 1, new byte[5]));
            reject(() => TexturePixels.Png(1, 1, null!));
        });
        test("PNG signature dimensions chunk CRC zlib scanlines and vertical flip", () =>
        {
            byte[] pixels = [255, 0, 0, 255, 0, 255, 0, 127, 0, 0, 255, 64, 1, 2, 3, 0];
            foreach (bool flip in new[] { false, true })
            {
                byte[] png = TexturePixels.Png(2, 2, pixels, flip);
                Equal(png[..8], [137, 80, 78, 71, 13, 10, 26, 10]);
                var types = new List<string>();
                using var compressed = new MemoryStream();
                int position = 8;
                while (position < png.Length)
                {
                    int count = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position, 4));
                    string type = Encoding.ASCII.GetString(png, position + 4, 4);
                    types.Add(type);
                    var data = png.AsSpan(position + 8, count);
                    // Independent table-based CRC validation, including PNG's known IEND CRC.
                    uint crc = uint.MaxValue;
                    foreach (byte value in png.AsSpan(position + 4, count + 4)) crc = Table[(crc ^ value) & 255] ^ (crc >> 8);
                    if (~crc != BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position + 8 + count, 4))) throw new Exception("PNG CRC mismatch");
                    if (type == "IHDR")
                    {
                        Equal(data.ToArray(), [0, 0, 0, 2, 0, 0, 0, 2, 8, 6, 0, 0, 0]);
                    }
                    if (type == "IDAT") compressed.Write(data);
                    if (type == "IEND" && (count != 0 || ~crc != 0xAE426082u)) throw new Exception("Invalid IEND");
                    position += count + 12;
                }
                if (position != png.Length || !types.SequenceEqual(new[] { "IHDR", "IDAT", "IEND" })) throw new Exception("PNG chunk structure");
                compressed.Position = 0;
                using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
                using var decoded = new MemoryStream(); zlib.CopyTo(decoded);
                byte[] expected = flip ? [0, .. pixels[8..], 0, .. pixels[..8]] : [0, .. pixels[..8], 0, .. pixels[8..]];
                Equal(decoded.ToArray(), expected);
            }
        });
    }

    private static void Equal(byte[] actual, byte[] expected)
    {
        if (!actual.SequenceEqual(expected)) throw new Exception("Texture bytes mismatch");
    }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        uint entry = (uint)i;
        for (int bit = 0; bit < 8; bit++) entry = (entry & 1) == 0 ? entry >> 1 : (entry >> 1) ^ 0xEDB88320u;
        return entry;
    }).ToArray();
}
