using System.Text;
using System.Text.Json;
using Sora.Core;

try
{
int passed = 0;
void Test(string name, Action action) { action(); Console.WriteLine("PASS " + name); passed++; }
void Reject(Action action)
{
    try { action(); }
    catch (Exception e) when (e is InvalidDataException or EndOfStreamException or JsonException) { return; }
    throw new Exception("Expected rejection");
}
DatabaseDocument sample = new("fixture-1", [
    new("character:sample", "Protocol fixture", "One rig, mesh, face channel and animation", "character", ["texture:missing"],
        new("Protocol fixture", [new("Root", -1, [0, 0, 0], [0, 0, 1])],
            [new("Face", [[-1, 0, 0], [1, 0, 0], [0, 0, 2]], [[0, 1, 2]], [[0, -1, 0], [0, -1, 0], [0, -1, 0]], [[0, 0], [1, 0], [0.5, 1]], 0,
                [new(0, 0, 1), new(1, 0, 1), new(2, 0, 1)], [new("Smile", [[0, 0, 0.2], [0, 0, 0.2], [0, 0, 0]])])],
            [new("Blue", [0.08, 0.4, 0.8, 1], 0, 0.5)],
            [new("Wave", 1, 30, [new(0, "location", [new(0, [0, 0, 0]), new(1, [0, 0, 1])])])]))]);

Test("scene roundtrip retains geometry, rig, face and animation", () => {
    using var memory = new MemoryStream(); DatabaseFile.Write(memory, sample); memory.Position = 0;
    var result = DatabaseFile.Read(memory);
    if (JsonSerializer.Serialize(sample, WireJson.Options) != JsonSerializer.Serialize(result, WireJson.Options)) throw new Exception("Roundtrip mismatch");
});
Test("empty database", () => {
    using var memory = new MemoryStream(); DatabaseFile.Write(memory, new("empty", [])); memory.Position = 0;
    if (DatabaseFile.Read(memory).Assets.Length != 0) throw new Exception();
});
byte[] encoded;
using (var memory = new MemoryStream()) { DatabaseFile.Write(memory, sample); encoded = memory.ToArray(); }
Test("all truncated header boundaries", () => { for (int i = 0; i < 52; i++) Reject(() => DatabaseFile.Read(new MemoryStream(encoded[..i]))); });
Test("truncated payload", () => Reject(() => DatabaseFile.Read(new MemoryStream(encoded[..^1]))));
Test("trailing payload", () => Reject(() => DatabaseFile.Read(new MemoryStream([.. encoded, 0]))));
Test("hash corruption", () => { var data = (byte[])encoded.Clone(); data[^1] ^= 1; Reject(() => DatabaseFile.Read(new MemoryStream(data))); });
Test("unknown version", () => { var data = (byte[])encoded.Clone(); data[8] = 3; Reject(() => DatabaseFile.Read(new MemoryStream(data))); });
Test("old RCM6 input", () => { var data = (byte[])encoded.Clone(); Encoding.ASCII.GetBytes("6MCR").CopyTo(data, 0); Reject(() => DatabaseFile.Read(new MemoryStream(data))); });
Test("oversized length", () => { var data = (byte[])encoded.Clone(); Array.Fill(data, (byte)255, 12, 8); Reject(() => DatabaseFile.Read(new MemoryStream(data))); });
Test("empty JSON", () => Reject(() => DatabaseFile.ParsePayload([])));
Test("null JSON", () => Reject(() => DatabaseFile.ParsePayload("null"u8.ToArray())));
Test("duplicate JSON keys", () => Reject(() => DatabaseFile.ParsePayload("{\"gameVersion\":\"a\",\"gameVersion\":\"b\",\"assets\":[]}"u8.ToArray())));
Test("missing and unknown fields", () => {
    Reject(() => DatabaseFile.ParsePayload("{\"assets\":[]}"u8.ToArray()));
    Reject(() => DatabaseFile.ParsePayload("{\"gameVersion\":\"a\",\"assets\":[],\"alien\":1}"u8.ToArray()));
});
Test("null entries", () => Reject(() => DatabaseFile.ParsePayload("{\"gameVersion\":\"a\",\"assets\":[null]}"u8.ToArray())));
Test("duplicate identities", () => Reject(() => Validation.Database(sample with { Assets = [sample.Assets[0], sample.Assets[0]] })));
Test("cycle and unresolved dependency", () => {
    var catalog = new Catalog(new("test", [new("a", "A", "", "asset", ["b"]), new("b", "B", "", "asset", ["a", "missing"])]));
    var output = JsonSerializer.Serialize(catalog.Closure("a"));
    if (output != "{\"assets\":[\"a\",\"b\"],\"missing\":[\"missing\"]}") throw new Exception(output);
});
Test("stable search and empty search", () => {
    var catalog = new Catalog(sample);
    if (!JsonSerializer.Serialize(catalog.Search("PROTOCOL", 0, 1)).Contains("character:sample")) throw new Exception();
    if (!JsonSerializer.Serialize(catalog.Search("does-not-exist", 0, 1)).Contains("\"total\":0")) throw new Exception();
    Reject(() => catalog.Search("", -1, 1)); Reject(() => catalog.Search("", 0, 1001));
});
var scene = sample.Assets[0].Scene!;
Test("invalid bone hierarchy", () => Reject(() => Validation.Scene(scene with { Bones = [scene.Bones[0] with { Parent = 0 }] })));
Test("invalid mesh index", () => Reject(() => Validation.Scene(scene with { Meshes = [scene.Meshes[0] with { Triangles = [[0, 1, 3]] }] })));
Test("invalid normal and numeric value", () => {
    Reject(() => Validation.Scene(scene with { Meshes = [scene.Meshes[0] with { Normals = [[0, 0, 0], [0, 0, 0], [0, 0, 0]] }] }));
    Reject(() => Validation.Scene(scene with { Bones = [scene.Bones[0] with { Head = [double.NaN, 0, 0] }] }));
});
Test("invalid weight and shape cardinality", () => {
    Reject(() => Validation.Scene(scene with { Meshes = [scene.Meshes[0] with { Weights = [new(0, 0, 0.5)] }] }));
    Reject(() => Validation.Scene(scene with { Meshes = [scene.Meshes[0] with { Shapes = [new("Smile", [])] }] }));
});
Test("non-increasing animation and invalid quaternion", () => {
    Reject(() => Validation.Scene(scene with { Clips = [new("bad", 1, 30, [new(0, "location", [new(1, [0, 0, 0]), new(0, [0, 0, 0])])])] }));
    Reject(() => Validation.Scene(scene with { Clips = [new("bad", 1, 30, [new(0, "rotation", [new(0, [0, 0, 0, 0])])])] }));
});
Test("invalid replacement preserves existing database", () => {
    var folder = Path.Combine(Path.GetTempPath(), "sora-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, "asset.sredb");
    try {
        DatabaseFile.WriteAtomic(path, sample);
        Reject(() => DatabaseFile.WriteAtomic(path, sample with { GameVersion = "" }));
        if (!File.ReadAllBytes(path).SequenceEqual(encoded)) throw new Exception("Existing file changed");
        if (Directory.GetFiles(folder).Length != 1) throw new Exception("Temporary file leaked");
    } finally { File.Delete(path); Directory.Delete(folder); }
});
NativeTests.Run(Test, Reject);
SerializedTests.Run(Test, Reject);
SerializedReferenceTests.Run(Test, Reject);
CharacterTests.Run(Test, Reject);
TextureTests.Run(Test, Reject);
NprTests.Run(Test, Reject);
ResourceIndexTests.Run(Test, Reject);
if (args.Length == 1)
{
    Directory.CreateDirectory(args[0]);
    DatabaseFile.WriteAtomic(Path.Combine(args[0], "fixture.sredb"), sample);
    File.WriteAllText(Path.Combine(args[0], "fixture.json"), JsonSerializer.Serialize(sample, WireJson.Options));
}
Console.WriteLine($"PASSED {passed} groups");
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
