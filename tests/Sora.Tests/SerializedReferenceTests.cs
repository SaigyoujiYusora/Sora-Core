using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Sora.Core;

internal static class SerializedReferenceTests
{
    public static void Run(Action<string, Action> test, Action<Action> reject)
    {
        test("v22 named managed reference decodes payload and host-only registry", () => {
            var doc = SerializedAssets.Decode(Fixture());
            var root = JsonSerializer.SerializeToElement(doc.Objects.Single().Data);
            var item = root.GetProperty("references").GetProperty("RefIds").GetProperty("Array")[0];
            if (item.GetProperty("rid").GetInt64() != 7 || item.GetProperty("data").GetProperty("value").GetInt32() != 42)
                throw new Exception("Managed reference payload mismatch");
        });
        test("v22 rejects unresolved managed types and unsupported registries", () => {
            reject(() => SerializedAssets.Decode(Fixture(unknown: true)));
            reject(() => SerializedAssets.Decode(Fixture(version: 1)));
            var bytes = Fixture();
            reject(() => SerializedAssets.Decode(bytes[..^1]));
        });
        test("v22 shares an aggregate byte budget across opaque arrays", () => {
            SerializedAssets.Decode(Fixture(rawSize: 16));
            try { SerializedAssets.Decode(Fixture(rawSize: 64 * 1024 * 1024 + 1)); }
            catch (InvalidDataException error) when (error.Message.Contains("raw data byte budget")) { return; }
            throw new Exception("Cumulative opaque-byte limit was not enforced");
        });
    }

    private record Node(byte Level, string Type, string Name, int Size = -1, int Flags = 0);
    private static byte[] Fixture(bool unknown = false, int version = 2, int rawSize = 0)
    {
        Node[] registry = [new(1,"ManagedReferencesRegistry","references"), new(2,"int","version",4),
            new(2,"vector","RefIds"), new(3,"Array","Array"), new(4,"int","size",4),
            new(4,"ReferencedObject","data"), new(5,"SInt64","rid",8), new(5,"ReferencedManagedType","type"),
            new(6,"string","class"), new(6,"string","ns"), new(6,"string","asm"),
            new(5,"ReferencedObjectData","data",0)];
        using var payload = new MemoryStream();
        using (var p = new BinaryWriter(payload, Encoding.UTF8, true)) {
            void Text(string s) { byte[] bytes = Encoding.UTF8.GetBytes(s); p.Write(bytes.Length); p.Write(bytes); while(payload.Position % 4 != 0) p.Write((byte)0); }
            p.Write(version); p.Write(1); p.Write(7L); Text(unknown ? "Unknown" : "Mapping"); Text("Fixture"); Text("FixtureAssembly"); p.Write(42);
            if (rawSize > 0) { p.Write(rawSize); payload.Position += rawSize; p.Write(rawSize); payload.Position += rawSize; payload.SetLength(payload.Position); }
        }
        using var metadata = new MemoryStream(); using var w = new BinaryWriter(metadata, Encoding.UTF8, true);
        void CString(string s) { w.Write(Encoding.UTF8.GetBytes(s)); w.Write((byte)0); }
        void Tree(Node[] nodes) {
            using var strings = new MemoryStream();
            var offsets = new Dictionary<string,uint>();
            foreach(var s in nodes.SelectMany(n=>new[]{n.Type,n.Name}).Distinct()) { offsets[s]=(uint)strings.Length; strings.Write(Encoding.UTF8.GetBytes(s)); strings.WriteByte(0); }
            w.Write(nodes.Length); w.Write((int)strings.Length);
            foreach(var n in nodes) { w.Write((ushort)1); w.Write(n.Level); w.Write((byte)0); w.Write(offsets[n.Type]); w.Write(offsets[n.Name]); w.Write(n.Size); w.Write(0); w.Write(n.Flags); w.Write(0UL); }
            w.Write(strings.ToArray());
        }
        CString("2021.3.34f5"); w.Write(19); w.Write((byte)1); w.Write(1);
        w.Write(114); w.Write((byte)0); w.Write((short)-1); w.Write(new byte[32]);
        Node[] raw = rawSize > 0 ? [new(1,"Array","firstBytes"),new(2,"int","size",4),new(2,"UInt8","data",1),new(1,"Array","secondBytes"),new(2,"int","size",4),new(2,"char","data",1)] : [];
        Tree([new(0,"MonoBehaviour","Base"), ..registry, ..raw]); w.Write(0);
        w.Write(1); while(metadata.Position % 4 != 0) w.Write((byte)0);
        w.Write(123L); w.Write(0UL); w.Write((uint)payload.Length); w.Write(0);
        w.Write(0); w.Write(0); w.Write(1);
        // Reference types use the script index, not classId, to signal their script hash.
        w.Write(-1); w.Write((byte)0); w.Write((short)0); w.Write(new byte[32]);
        Tree([new(0,"Mapping","Base"), new(1,"int","value",4), ..registry]);
        CString("Mapping"); CString("Fixture"); CString("FixtureAssembly"); CString("");
        int offset = ((int)metadata.Length + 48 + 15) & ~15;
        byte[] result = new byte[offset + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8),22);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20),(uint)metadata.Length);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(24),(ulong)result.Length);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(32),(ulong)offset);
        metadata.ToArray().CopyTo(result,48); payload.ToArray().CopyTo(result,offset);
        return result;
    }
}
