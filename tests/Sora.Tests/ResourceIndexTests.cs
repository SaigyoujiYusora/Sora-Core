using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Sora.Core;

internal static class ResourceIndexTests
{
    public static void Run(Action<string, Action> test, Action<Action> reject)
    {
        var file = new ResourceFileRecord("StreamingAssets/VFS/block/index.blc", new("Bundles/Windows/actor.ab", "chunk.bin", 123, 456, true, 7, "chunk-hash", "payload-hash"));
        var decoded = new CabResourceRecord("CAB-a", "decoded", 0, new("CAB-a", 12, 34), ["assets/actor.prefab"], [1, 142], ["CAB-b", "CAB-missing"]);
        var index = new EndfieldResourceIndex("F:/game/Endfield_Data", "manifest-hash", "revision", [file],
            [decoded, new("CAB-b", "decoded", 0, new("CAB-b", 46, 12), [], [28], ["CAB-a"]), new("CAB-missing", "unresolved", null, null, null, null, null)]);
        var database = new DatabaseDocument("test", [], index);
        test("SRED v2 resource index roundtrip retains source and phantom relationships", () => {
            using var stream = new MemoryStream(); DatabaseFile.Write(stream, database);
            if (BinaryPrimitives.ReadUInt32LittleEndian(stream.ToArray().AsSpan(8)) != 2) throw new Exception();
            stream.Position = 0; var restored = DatabaseFile.Read(stream);
            if (JsonSerializer.Serialize(restored, WireJson.Options) != JsonSerializer.Serialize(database, WireJson.Options)) throw new Exception();
        });
        test("SRED historical v1 envelope reads without invented resource index", () => {
            byte[] payload = "{\"gameVersion\":\"legacy\",\"assets\":[{\"id\":\"a\",\"label\":\"A\",\"detail\":\"\",\"kind\":\"bundle\",\"dependencies\":[]}]}"u8.ToArray();
            var restored = DatabaseFile.Read(new MemoryStream(Envelope(payload, 1)));
            if (restored.GameVersion != "legacy" || restored.ResourceIndex is not null || restored.Assets[0].Id != "a") throw new Exception();
            using var stream = new MemoryStream(); DatabaseFile.Write(stream, restored); stream.Position = 0;
            if (DatabaseFile.Read(stream).ResourceIndex is not null) throw new Exception();
        });
        test("SRED v1 cannot carry v2 index or smuggle null index", () => {
            reject(() => DatabaseFile.Read(new MemoryStream(Envelope(JsonSerializer.SerializeToUtf8Bytes(database, WireJson.Options), 1))));
            reject(() => DatabaseFile.ParsePayload("{\"gameVersion\":\"v\",\"assets\":[],\"resourceIndex\":null}"u8.ToArray(), 1));
        });
        test("SRED resource metadata distinguishes uninspected from empty", () => {
            Validation.ResourceIndex(index with { ManifestRevision = "" }); // Present native manifests have an empty revision.
            Validation.ResourceIndex(index with { Cabs = [new("CAB-a", "indexed", 0, decoded.Entry, null, null, null)] });
            reject(() => Validation.ResourceIndex(index with { Cabs = [decoded with { Status = "indexed" }] }));
            reject(() => Validation.ResourceIndex(index with { Cabs = [decoded with { ClassIds = null }] }));
            reject(() => Validation.ResourceIndex(index with { Cabs = [decoded with { Status = "unresolved" }] }));
        });
        test("SRED source file relationships and ranges are bounded", () => {
            foreach (int bad in new[] { -1, 1, int.MaxValue })
                reject(() => Validation.ResourceIndex(index with { Cabs = [decoded with { File = bad }] }));
            reject(() => Validation.ResourceIndex(index with { Files = [file with { Resource = file.Resource with { Offset = long.MaxValue, Length = 1 } }] }));
            reject(() => Validation.ResourceIndex(index with { Cabs = [decoded with { Entry = new("CAB-a", -1, 2) }] }));
            reject(() => Validation.ResourceIndex(index with { Cabs = [decoded with { Entry = new("CAB-other", 0, 2) }] }));
            foreach (string bad in new[] { "../index.blc", "/index.blc", "C:/index.blc", "a\\index.blc", "a//index.blc" })
                reject(() => Validation.ResourceIndex(index with { Files = [file with { BlockIndexPath = bad }] }));
        });
        test("SRED duplicate and dangling CAB identities reject while cycles remain valid", () => {
            Validation.ResourceIndex(index);
            reject(() => Validation.ResourceIndex(index with { Cabs = [decoded] }));
            reject(() => Validation.ResourceIndex(index with { Cabs = [.. index.Cabs, index.Cabs[2] with { Id = "cab-A" }] }));
            reject(() => Validation.ResourceIndex(index with { Files = [file, file] }));
            reject(() => Validation.ResourceIndex(index with { Cabs = [decoded with { Dependencies = ["CAB-a", "cab-a"] }] }));
            reject(() => Validation.ResourceIndex(index with { Cabs = [decoded with { ClassIds = [1, 1] }] }));
        });
        test("SRED independent native metadata projection keeps container class and external CAB facts", () => {
            using var json = JsonDocument.Parse("{\"m_Container\":{\"Array\":[{\"first\":\"assets/native.prefab\",\"second\":{}},{\"first\":\"assets/other.prefab\",\"second\":{}}]}}");
            var document = new SerializedDocument("native", ["archive:/CAB-a/CAB-a", "CAB-missing"], [new(1, 142, json.RootElement), new(7, 28, null)]);
            var row = ResourceIndexMetadata.Decoded("CAB-b", 0, new("CAB-b", 0, 10), document);
            if (!row.ClassIds!.SequenceEqual(new[] { 28, 142 }) || row.ContainerPaths!.Length != 2 || !row.Dependencies!.SequenceEqual(new[] { "CAB-a", "CAB-missing" })) throw new Exception();
            reject(() => ResourceIndexMetadata.Decoded("CAB-b", 0, new("CAB-b", 0, 10), document with { Objects = [new(1, 142, null)] }));
        });
        test("SRED v2 malformed and unknown resource fields reject", () => {
            var json = JsonSerializer.Serialize(database, WireJson.Options);
            reject(() => DatabaseFile.ParsePayload(System.Text.Encoding.UTF8.GetBytes(json.Replace("\"status\":\"decoded\"", "\"status\":\"future\""))));
            reject(() => DatabaseFile.ParsePayload(System.Text.Encoding.UTF8.GetBytes(json.Replace("\"manifestHash\":", "\"unknown\":0,\"manifestHash\":"))));
            reject(() => DatabaseFile.ParsePayload(System.Text.Encoding.UTF8.GetBytes(json.Replace("\"files\":[{", "\"files\":[null,{"))));
        });
    }

    private static byte[] Envelope(byte[] payload, uint version)
    {
        byte[] result = new byte[52 + payload.Length]; "SREDB\r\n\x1a"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), version);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(12), (ulong)payload.Length);
        SHA256.HashData(payload).CopyTo(result, 20); payload.CopyTo(result, 52); return result;
    }
}
