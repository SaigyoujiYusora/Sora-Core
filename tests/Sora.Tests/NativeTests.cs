using System.IO.Compression;
using System.Text;
using Sora.Core;

internal static class NativeTests
{
    public static void Run(Action<string, Action> test, Action<Action> reject)
    {
        test("LZ4 empty and literal blocks", () => {
            if (Lz4Block.Decode([], 0).Length != 0 || Encoding.ASCII.GetString(Lz4Block.Decode([0x30, 65, 66, 67], 3)) != "ABC") throw new Exception();
            if (Encoding.ASCII.GetString(Lz4Block.Decode([3, 65, 66, 67], 3, true)) != "ABC") throw new Exception();
        });
        test("LZ4 overlapping matches and Endfield offset order", () => {
            if (Encoding.ASCII.GetString(Lz4Block.Decode([0x10, 65, 1, 0], 5)) != "AAAAA") throw new Exception();
            if (Encoding.ASCII.GetString(Lz4Block.Decode([1, 65, 0, 1], 5, true)) != "AAAAA") throw new Exception();
        });
        test("LZ4 malformed lengths, offsets and boundaries", () => {
            foreach (var input in new byte[][] { [0xf0], [0, 0, 0], [0x10], [0x10, 65, 9, 0], [0x10, 65, 1] }) reject(() => Lz4Block.Decode(input, 5));
            reject(() => Lz4Block.Decode([0x30, 65, 66, 67], 2)); reject(() => Lz4Block.Decode([], -1)); reject(() => Lz4Block.Decode([], int.MaxValue));
        });
        test("native manifest normal and duplicate hash records", () => {
            var manifest = NativeManifest.Read(new MemoryStream(Fixture(2)));
            if (manifest.Bundles.Length != 1 || manifest.Assets.Length != 2 || manifest.Assets[0].Path != "Assets/test.prefab") throw new Exception();
            var database = manifest.ToDatabase();
            if (database.Assets.Select(x => x.Id).Distinct().Count() != 3) throw new Exception();
        });
        test("native manifest empty address table", () => {
            if (NativeManifest.Read(new MemoryStream(Fixture(0))).Assets.Length != 0) throw new Exception();
        });
        test("native manifest malformed and empty input", () => {
            reject(() => NativeManifest.Read(new MemoryStream())); reject(() => NativeManifest.Read(new MemoryStream([1, 2, 3, 4])));
        });
        test("native manifest unsupported signature and invalid offset", () => {
            reject(() => NativeManifest.Read(new MemoryStream(Fixture(1, badMagic: true))));
            reject(() => NativeManifest.Read(new MemoryStream(Fixture(1, badOffset: true))));
        });
        test("native VFS rejects empty and non-VFS files", () => {
            var path = Path.Combine(Path.GetTempPath(), "sora-vfs-" + Guid.NewGuid().ToString("N"));
            try {
                File.WriteAllBytes(path, []); reject(() => { using var archive = new VfsArchive(path); });
                File.WriteAllBytes(path, new byte[128]); reject(() => { using var archive = new VfsArchive(path); });
            } finally { File.Delete(path); }
        });
        test("synthetic VFS directory and bounded entry extraction", () => {
            var path = Path.Combine(Path.GetTempPath(), "sora-vfs-" + Guid.NewGuid().ToString("N"));
            try {
                byte[] bytes = VfsFixture.Create(); File.WriteAllBytes(path, bytes);
                using (var archive = new VfsArchive(path)) {
                    if (archive.Entries.Count != 1 || archive.Entries[0].Name != "fixture" || Encoding.ASCII.GetString(archive.Extract("fixture")) != "bcd") throw new Exception("Synthetic VFS mismatch");
                }
                File.WriteAllBytes(path, bytes[..^4]); reject(() => { using var archive = new VfsArchive(path); });
            } finally { File.Delete(path); }
        });
    }

    private static byte[] Pack(byte[] data)
    {
        using var output = new MemoryStream();
        using (var encoder = new BrotliStream(output, CompressionLevel.Fastest, true)) encoder.Write(data);
        return output.ToArray();
    }

    private static byte[] Fixture(int records, bool badMagic = false, bool badOffset = false)
    {
        using var values = new MemoryStream();
        using var v = new BinaryWriter(values, Encoding.UTF8, true);
        byte[] name = Encoding.Unicode.GetBytes("fixture.bundle"); v.Write(name.Length); v.Write(name);
        int dependencies = (int)values.Position; v.Write(1); v.Write(0);
        int path = (int)values.Position; byte[] packedPath = Pack(Encoding.Unicode.GetBytes("Assets/test.prefab")); v.Write(packedPath.Length); v.Write(packedPath);
        using var address = new MemoryStream(); using var a = new BinaryWriter(address, Encoding.UTF8, true);
        a.Write(0);
        for (int i = 0; i < records; i++) { a.Write(5L); a.Write(badOffset ? int.MaxValue : path); a.Write(0); a.Write(8); a.Write(0); }
        using var bundle = new MemoryStream(); using var b = new BinaryWriter(bundle, Encoding.UTF8, true);
        b.Write(1); b.Write(0); b.Write(0); b.Write(dependencies); b.Write(dependencies); b.Write(dependencies); b.Write(0); b.Write(0L); b.Write(0L); b.Write(1); b.Write(0);
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(badMagic ? 0u : 0xff11ff11u);
        void Text(string text) { writer.Write(text.Length); writer.Write(Encoding.Unicode.GetBytes(text)); }
        void Section(byte[] bytes) { writer.Write(bytes.Length); writer.Write(bytes); }
        Text("fixture-1"); writer.Write(0xf1f2f3f4u); Text("hash"); Text("revision");
        Section(address.ToArray()); Section([]); Section(bundle.ToArray()); Section(values.ToArray()); writer.Write(0u);
        return Pack(output.ToArray());
    }
}
