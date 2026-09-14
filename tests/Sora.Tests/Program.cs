using System.Text;
using System.Text.Json;
using Sora.Core;

try
{
if(args.Length==1 && args[0]=="acl-worker") {
    string? mode=Environment.GetEnvironmentVariable("SORA_ACL_TEST_MODE");
    if(mode=="timeout") {
        Console.In.ReadToEnd();
        if(Environment.GetEnvironmentVariable("SORA_ACL_TEST_PID_PATH") is {} pidPath) File.WriteAllText(pidPath,Environment.ProcessId.ToString());
        Thread.Sleep(30000);
    }
    else if(mode=="oversized-error") { Console.Error.Write(new string('x',70000)); }
    else Console.Write("{}");
    return;
}
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
Test("validated storage budget uses original payload bytes and format for v1 v2 v3", () => {
    byte[] payload = Encoding.UTF8.GetBytes("{\"gameVersion\":\"fixture\", \"assets\":[]}");
    foreach(uint version in new uint[] {1,2,3}) {
        byte[] header = new byte[52]; Encoding.ASCII.GetBytes("SREDB\r\n\x1a").CopyTo(header,0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8),version);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(12),(ulong)payload.Length);
        System.Security.Cryptography.SHA256.HashData(payload).CopyTo(header,20);
        byte[] bytes = [..header,..payload]; var stored = DatabaseFile.ReadValidated(new MemoryStream(bytes));
        if(stored.Storage != new DatabaseStorageInfo(version,payload.Length,268435456) || stored.Database.GameVersion!="fixture")throw new Exception("Incorrect validated budget");
        bytes[^1]^=1; Reject(()=>DatabaseFile.ReadValidated(new MemoryStream(bytes)));
    }
});
Test("atomic budget returned only after verified successful replacement", () => {
    string directory=Path.Combine(Path.GetTempPath(),"sora-budget-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
    string path=Path.Combine(directory,"test.sredb");
    try {
        var info=DatabaseFile.WriteAtomicValidated(path,sample);
        if(info.PayloadBytes!=new FileInfo(path).Length-52 || info!=DatabaseFile.ReadValidated(path).Storage)throw new Exception("Atomic size mismatch");
        byte[] before=File.ReadAllBytes(path);bool failed=false;
        using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            try { DatabaseFile.WriteAtomicValidated(path,new("replacement",[])); }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException) { failed=true; }
        }
        if(OperatingSystem.IsWindows() && (!failed || !before.SequenceEqual(File.ReadAllBytes(path))))throw new Exception("Locked original was not preserved");
        if(Directory.GetFiles(directory).Length!=1)throw new Exception("Temporary output leaked");
    } finally { File.Delete(path);Directory.Delete(directory); }
});
Test("map placeholder rejects invalid identity and never fabricates a document", () => {
    IMapDataReader reader = new PlaceholderMapDataReader();
    foreach (var id in new[] { "", " ", "bad\nmap", new string('x', 1025) })
        Reject(() => reader.Read(new(id)));
    Reject(() => reader.Read(null!));
    var result = reader.Read(new("unresolved-map"));
    if (reader.Status.Supported || reader.Status.State != "placeholder" || result.Document is not null
        || result.Status != reader.Status) throw new Exception("Placeholder claimed map data");
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, WireJson.Options));
    if (json.RootElement.GetProperty("document").ValueKind != JsonValueKind.Null)
        throw new Exception("Missing explicit null map document");
});
Test("empty database", () => {
    using var memory = new MemoryStream(); DatabaseFile.Write(memory, new("empty", [])); memory.Position = 0;
    if (DatabaseFile.Read(memory).Assets.Length != 0) throw new Exception();
});
Test("catalog capabilities distinguish cached indexed and unsupported assets", () => {
    var indexed = new AssetRecord("index", "Index", "", "character", [], Locator: new("character", "assets/test_uimodel.prefab"));
    var database = new DatabaseDocument("fixture", [indexed]);
    if (GameCatalog.Capability(indexed, database).State != "requires-game" || GameCatalog.Capability(indexed, database).CanImport) throw new Exception();
    if (GameCatalog.Capability(sample.Assets[0], sample).State != "cached") throw new Exception();
    if (GameCatalog.Capability(indexed with { Locator = null }, database).State != "unsupported") throw new Exception();
});
Test("streaming atomic payload matches public bytes", () => {
    string directory=Path.Combine(Path.GetTempPath(),"sora-stream-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
    string path=Path.Combine(directory,"test.sredb");
    try {
        using var expected=new MemoryStream();DatabaseFile.Write(expected,sample);
        using var nonseekBytes=new MemoryStream();
        using(var nonseek=new NonSeekWriteStream(nonseekBytes)) DatabaseFile.Write(nonseek,sample);
        if(!expected.ToArray().SequenceEqual(nonseekBytes.ToArray()))throw new Exception("Public nonseek Write changed");
        DatabaseFile.WriteAtomic(path,sample);
        if(!expected.ToArray().SequenceEqual(File.ReadAllBytes(path)))throw new Exception("Streamed SRED bytes differ");
        var larger=new DatabaseDocument("stream",Enumerable.Range(0,1024).Select(i=>new AssetRecord("a"+i,"A",new string('x',1024),"resource",[])).ToArray());
        byte[] original=File.ReadAllBytes(path);
        foreach(string stage in new[]{"validate-database-assets","write-database-payload","read-database-payload"}) {
            bool cancelled=false;
            OperationProgress.Sink=update=>{if(update.Stage==stage&&update.Completed>0)throw new OperationCanceledException();};
            try{DatabaseFile.WriteAtomic(path,larger);}catch(OperationCanceledException){cancelled=true;}
            if(!cancelled||!original.SequenceEqual(File.ReadAllBytes(path))||Directory.GetFiles(directory).Length!=1)throw new Exception("Checkpoint failed to preserve database: "+stage);
        }
    } finally {OperationProgress.Sink=null;File.Delete(path);Directory.Delete(directory);}
});
Test("streamed atomic payload limit preserves the previous database", () => {
    string directory=Path.Combine(Path.GetTempPath(),"sora-stream-limit-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
    string path=Path.Combine(directory,"test.sredb");
    try {
        DatabaseFile.WriteAtomic(path,sample);byte[] before=File.ReadAllBytes(path);
        string detail=new('x',8192);
        var oversized=new DatabaseDocument("limit",Enumerable.Range(0,32768).Select(i=>new AssetRecord("a"+i,"A",detail,"resource",[])).ToArray());
        Reject(()=>DatabaseFile.WriteAtomic(path,oversized));
        if(!before.SequenceEqual(File.ReadAllBytes(path))||Directory.GetFiles(directory).Length!=1)throw new Exception("Limit failure changed original or leaked temp");
    } finally {File.Delete(path);Directory.Delete(directory);}
});
Test("atomic commit callback separates final cancellation from committed replacement", () => {
    string directory=Path.Combine(Path.GetTempPath(),"sora-commit-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
    string path=Path.Combine(directory,"test.sredb");
    try {
        DatabaseFile.WriteAtomic(path,sample);byte[] original=File.ReadAllBytes(path);
        OperationProgress.Commit=_=>throw new OperationCanceledException();
        bool cancelled=false;try{DatabaseFile.WriteAtomic(path,new("replacement",[]));}catch(OperationCanceledException){cancelled=true;}
        if(!cancelled||!original.SequenceEqual(File.ReadAllBytes(path))||Directory.GetFiles(directory).Length!=1)throw new Exception("Precommit cancellation changed original or leaked output");
        bool committed=false;OperationProgress.Commit=replace=>{replace();committed=true;};
        DatabaseFile.WriteAtomic(path,new("replacement",[]));
        if(!committed||DatabaseFile.Read(path).GameVersion!="replacement"||Directory.GetFiles(directory).Length!=1)throw new Exception("Commit was not final");
    } finally {OperationProgress.Sink=null;OperationProgress.Commit=null;File.Delete(path);Directory.Delete(directory);}
});
Test("cancel before atomic commit preserves database and removes temporary output", () => {
    string directory = Path.Combine(Path.GetTempPath(), "sora-cancel-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, "test.sredb");
    try {
        DatabaseFile.WriteAtomic(path, sample); byte[] original = File.ReadAllBytes(path);
        OperationProgress.Sink = update => { if (update.Stage == "commit-database") throw new OperationCanceledException(); };
        bool cancelled = false;
        try { DatabaseFile.WriteAtomic(path, new("replacement", [])); } catch (OperationCanceledException) { cancelled = true; }
        if (!cancelled || !File.ReadAllBytes(path).SequenceEqual(original) || Directory.GetFiles(directory).Length != 1) throw new Exception();
    } finally { OperationProgress.Sink = null; File.Delete(path); Directory.Delete(directory); }
});
Test("scene node transform contract rejects invalid binding and parent cycles", () => {
    var scene = sample.Assets[0].Scene!;
    var node = new SceneNodeRecord("cab:1", "Root", -1, "Root", [1,0,0,2,0,1,0,3,0,0,1,4,0,0,0,1]);
    Validation.Scene(scene with { Nodes = [node], Meshes = [scene.Meshes[0] with { Node = 0, CoordinateSpace = "scene" }] });
    Reject(() => Validation.Scene(scene with { Nodes = [node with { Parent = 0 }] }));
    Reject(() => Validation.Scene(scene with { Nodes = [node], Meshes = [scene.Meshes[0] with { Node = 1, CoordinateSpace = "scene" }] }));
    Reject(() => Validation.Scene(scene with { Nodes = [node], Meshes = [scene.Meshes[0] with { Node = 0, CoordinateSpace = "node" }] }));
});
byte[] encoded;
using (var memory = new MemoryStream()) { DatabaseFile.Write(memory, sample); encoded = memory.ToArray(); }
Test("all truncated header boundaries", () => { for (int i = 0; i < 52; i++) Reject(() => DatabaseFile.Read(new MemoryStream(encoded[..i]))); });
Test("truncated payload", () => Reject(() => DatabaseFile.Read(new MemoryStream(encoded[..^1]))));
Test("trailing payload", () => Reject(() => DatabaseFile.Read(new MemoryStream([.. encoded, 0]))));
Test("hash corruption", () => { var data = (byte[])encoded.Clone(); data[^1] ^= 1; Reject(() => DatabaseFile.Read(new MemoryStream(data))); });
Test("unknown version", () => { var data = (byte[])encoded.Clone(); data[8] = 99; Reject(() => DatabaseFile.Read(new MemoryStream(data))); });
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
NativeTableTests.Run(Test, Reject);
NativeWeaponAdaptationTests.Run(Test, Reject);
EquipmentDefaultPoseTests.Run(Test, Reject);
EquipmentDefaultPoseReviewTests.Run(Test, Reject);
NativeGenericScalarSamplerTests.Run(Test, Reject);
NativeEquipmentAnimationTests.Run(Test, Reject);
var poseController=Environment.GetEnvironmentVariable("SORA_TEST_POSE_CONTROLLER");
var poseClip=Environment.GetEnvironmentVariable("SORA_TEST_POSE_CLIP");
if(poseController is not null || poseClip is not null) {
    if(poseController is null || poseClip is null)throw new Exception("Both optional pose fixture paths must be supplied");
    EquipmentDefaultPoseReviewTests.Run((name,action)=>Test("source fixture: "+name,action),Reject,poseController,poseClip);
    NativeGenericScalarSamplerTests.Run((name,action)=>Test("source fixture: "+name,action),Reject,poseClip);
    Console.WriteLine("OPTIONAL native pose source fixtures: PASSED (host only; no assembly extraction or Blender playback)");
} else Console.WriteLine("SKIP optional native pose source fixtures: set SORA_TEST_POSE_CONTROLLER and SORA_TEST_POSE_CLIP; portable synthetic gate does not prove game integration");
CatalogReviewTests.Run(Test, Reject);
FaceMorphTests.Run(Test, Reject);
NativeNpcTests.Run(Test, Reject);
AclTests.Run(Test, Reject);
HumanoidTests.Run(Test, Reject);
NativeAnimationServiceTests.Run(Test, Reject);
NativeSkillTests.Run(Test, Reject);
NativeAnimationMontageTests.Run(Test, Reject);
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

