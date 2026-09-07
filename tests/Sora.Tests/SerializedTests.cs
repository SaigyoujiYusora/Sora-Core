using System.Buffers.Binary;
using System.Text;
using Sora.Core;

internal static class SerializedTests
{
    public static void Run(Action<string, Action> test, Action<Action> reject)
    {
        test("ChaCha20 RFC 8439 block vector", () => {
            byte[] key = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
            byte[] nonce = Convert.FromHexString("000000090000004a00000000");
            var value = ChaChaStream.Transform(new byte[64], key, nonce);
            var expected = Convert.FromHexString("10f1e7e4d13b5915500fdd1fa32071c4c7d1f4c733c068030422aa9ac3d46c4ed2826446079faa0914c2d705d98b02a2b5129cd1de164eb9cbd083e8a2503c4e");
            if (!value.SequenceEqual(expected)) throw new Exception("RFC vector mismatch");
            reject(() => ChaChaStream.Transform([], new byte[31], nonce));
            reject(() => ChaChaStream.Transform(new byte[65], key, nonce, uint.MaxValue));
        });
        test("ChaCha20 empty and partial-block roundtrip", () => {
            byte[] source = Enumerable.Range(0, 129).Select(x => (byte)x).ToArray(), key = new byte[32], nonce = new byte[12];
            if (!ChaChaStream.Transform(ChaChaStream.Transform(source, key, nonce), key, nonce).SequenceEqual(source)) throw new Exception();
            if (ChaChaStream.Transform([], key, nonce).Length != 0) throw new Exception();
        });
        test("embedded type tree decodes generated v22 object", () => {
            var result = SerializedAssets.Decode(Fixture());
            if (result.UnityVersion != "2021.3.34f5" || result.Objects.Length != 1 || result.Objects[0].Id != 123) throw new Exception();
            if ((int)((Dictionary<string, object?>)result.Objects[0].Data!)["value"]! != 42) throw new Exception();
        });
        test("serialized version, size and truncation rejected", () => {
            byte[] data = Fixture();
            reject(() => SerializedAssets.Decode([])); reject(() => SerializedAssets.Decode(data[..^1]));
            byte[] old = (byte[])data.Clone(); old[11] = 21; reject(() => SerializedAssets.Decode(old));
            byte[] bad = (byte[])data.Clone(); bad[^1] = 0; bad[16] = 3; reject(() => SerializedAssets.Decode(bad));
        });
        test("BLC empty, bad and unsupported input", () => {
            var path = Path.Combine(Path.GetTempPath(), "sora-blc-" + Guid.NewGuid().ToString("N"));
            try { File.WriteAllBytes(path, []); reject(() => BlockIndex.Read(path)); File.WriteAllBytes(path, new byte[64]); reject(() => BlockIndex.Read(path)); }
            finally { File.Delete(path); }
        });
        test("BLC extraction verifies indexed payload before accepting a physical chunk", () => {
            string directory = Path.Combine(Path.GetTempPath(), "sora-payload-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string name = new string('0', 32) + ".chk", path = Path.Combine(directory, name);
            try {
                File.WriteAllBytes(path, [1,2,3]);
                string digest = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(new byte[]{2,3}));
                var resource = new LogicalResource("fixture", name, 1, 2, false, 0, "", digest);
                if (!BlockIndex.Extract(Path.Combine(directory,"fixture.blc"), resource).SequenceEqual(new byte[]{2,3})) throw new Exception();
                File.WriteAllBytes(path, [1,2,4]);
                reject(() => BlockIndex.Extract(Path.Combine(directory,"fixture.blc"), resource));
            } finally { File.Delete(path); Directory.Delete(directory); }
        });
    }

    private static byte[] Fixture()
    {
        using var metadata = new MemoryStream(); using var writer = new BinaryWriter(metadata, Encoding.UTF8, true);
        writer.Write(Encoding.UTF8.GetBytes("2021.3.34f5\0")); writer.Write(19); writer.Write((byte)1);
        writer.Write(1); writer.Write(1); writer.Write((byte)0); writer.Write((short)-1); writer.Write(new byte[16]);
        byte[] strings = Encoding.UTF8.GetBytes("Example\0Base\0int\0value\0");
        writer.Write(2); writer.Write(strings.Length);
        void Node(byte level, uint type, uint name) { writer.Write((ushort)1); writer.Write(level); writer.Write((byte)0); writer.Write(type); writer.Write(name); writer.Write(4); writer.Write(0); writer.Write(0); writer.Write(0UL); }
        Node(0, 0, 8); Node(1, 13, 17); writer.Write(strings); writer.Write(0);
        writer.Write(1); while (metadata.Length % 4 != 0) writer.Write((byte)0);
        writer.Write(123L); writer.Write(0UL); writer.Write(4u); writer.Write(0);
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write((byte)0);
        int dataOffset = ((int)metadata.Length + 48 + 15) & ~15;
        byte[] bytes = new byte[dataOffset + 4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 22);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)metadata.Length);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(24), (ulong)bytes.Length);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(32), (ulong)dataOffset);
        metadata.ToArray().CopyTo(bytes, 48); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(dataOffset), 42);
        return bytes;
    }
}
