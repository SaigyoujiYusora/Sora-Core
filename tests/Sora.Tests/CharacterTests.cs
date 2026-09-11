using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using Sora.Core;

internal static class CharacterTests
{
    public static void Run(Action<string, Action> test, Action<Action> reject)
    {
        test("reflected asymmetric skin and unweighted child retain proper bone frames", () => {
            var childLocal = Matrix4x4.CreateRotationZ(0.37f) * Matrix4x4.CreateTranslation(0.2f, 0.4f, -0.7f);
            var source = Fixture((a, m) => {
                var skeleton = a["m_Avatar"]!["m_AvatarSkeleton"]!["data"]!;
                skeleton["m_Node"]!["Array"]!.AsArray().Add(new JsonObject { ["m_ParentId"] = 1 });
                skeleton["m_ID"]!["Array"]!.AsArray().Add(300);
                a["m_TOS"]!["Array"]!.AsArray().Add(new JsonObject { ["first"] = 300, ["second"] = "Root/Body_lod0/Unweighted" });
                a["m_Avatar"]!["m_DefaultPose"]!["data"]!["m_X"]!["Array"]!.AsArray().Add(new JsonObject {
                    ["t"] = new JsonObject { ["x"] = 0.2, ["y"] = 0.4, ["z"] = -0.7 },
                    ["q"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["z"] = Math.Sin(0.37 / 2), ["w"] = Math.Cos(0.37 / 2) },
                    ["s"] = new JsonObject { ["x"] = 1, ["y"] = 1, ["z"] = 1 } });
            });
            var scene = CharacterGeometry.Convert(source, "reflection").Assets[0].Scene!;
            var reflection = Matrix4x4.CreateScale(-1, 1, 1);
            var space = reflection * Matrix4x4.CreateRotationX(MathF.PI / 2);
            Matrix4x4[] nativeRest = [Matrix4x4.Identity, Matrix4x4.CreateTranslation(1, 2, 3), childLocal * Matrix4x4.CreateTranslation(1, 2, 3)];
            var mesh = scene.Meshes.Single();
            for (int bone = 0; bone < scene.Bones.Length; bone++) {
                var rest = ReadMatrix(scene.Bones[bone].RestMatrix!);
                Check(Math.Abs(rest.GetDeterminant() - 1) < 1e-5, "Proper bone frame");
                var point = new Vector3(0.31f, -0.24f, 0.52f);
                var expected = Vector3.Transform(Vector3.Transform(point, nativeRest[bone]), space);
                Check(Vector3.Distance(Vector3.Transform(Vector3.Transform(point, reflection), rest), expected) < 1e-5, "Bone local frame reflection including unweighted child");
                Matrix4x4.Invert(rest, out var inverseRest);
                Matrix4x4.Invert(nativeRest[bone], out var inverseNative);
                var posedNative = Matrix4x4.CreateRotationY(0.43f) * nativeRest[bone] * Matrix4x4.CreateTranslation(0.12f, -0.23f, 0.34f);
                var posed = reflection * posedNative * space;
                var vertexNative = new Vector3(2, 2, 3);
                var vertexConverted = new Vector3((float)mesh.Positions[1][0], (float)mesh.Positions[1][1], (float)mesh.Positions[1][2]);
                Check(Vector3.Distance(Vector3.Transform(vertexConverted, inverseRest * rest), vertexConverted) < 1e-5, "Bind vertex identity");
                var expectedPosed = Vector3.Transform(vertexNative, inverseNative * posedNative * space);
                Check(Vector3.Distance(Vector3.Transform(vertexConverted, inverseRest * posed), expectedPosed) < 1e-5, "Posed vertex parity");
            }
            var corners = mesh.Triangles.Single();
            Vector3 V(int index) => new((float)mesh.Positions[index][0], (float)mesh.Positions[index][1], (float)mesh.Positions[index][2]);
            var geometricNormal = Vector3.Normalize(Vector3.Cross(V(corners[1]) - V(corners[0]), V(corners[2]) - V(corners[0])));
            Check(Vector3.Distance(geometricNormal, new(0, -1, 0)) < 1e-5, "Reflected winding agrees with normals");
        });
        test("character geometry transforms triangle, normals, bones and skin", () => {
            var result = CharacterGeometry.Convert(Fixture(), "character:synthetic");
            Check(result.GameVersion == "synthetic-1" && result.Assets.Single().Id == "character:synthetic", "Identity");
            var scene = result.Assets[0].Scene!; var mesh = scene.Meshes.Single();
            Check(scene.Name == "Synthetic" && scene.Bones.Length == 2, "Scene and skeleton");
            Check(scene.Bones[0].Name == "Root" && scene.Bones[1].Name == "Body_lod0" && scene.Bones[1].Parent == 0, "Bone identities");
            Near(scene.Bones[1].Head, -1, -3, 2); Near(scene.Bones[1].Tail, -1, -3, 2.05);
            Near(mesh.Positions[0], -1, -3, 2); Near(mesh.Positions[1], -2, -3, 2); Near(mesh.Positions[2], -1, -3, 3);
            Check(mesh.Triangles.Single().SequenceEqual(new[] { 0, 2, 1 }), "Triangle winding");
            foreach (var normal in mesh.Normals) Near(normal, 0, -1, 0);
            Check(mesh.Uv[1].SequenceEqual(new double[] { 1, 0 }), "UV preservation");
            Check(mesh.Weights.Length == 6 && mesh.Material == -1 && scene.Materials.Length == 0, "Output cardinality");
            foreach (int vertex in Enumerable.Range(0, 3)) {
                var weights = mesh.Weights.Where(x => x.Vertex == vertex).OrderBy(x => x.Bone).ToArray();
                Check(weights.Length == 2 && weights[0].Bone == 0 && weights[1].Bone == 1, "Bind palette resolves skeleton");
                Check(Math.Abs(weights[0].Weight - 2.0 / 3) < 1e-6 && Math.Abs(weights[1].Weight - 1.0 / 3) < 1e-6, "Skin normalization");
            }
        });
        test("character packed octahedral normal channel converts to scene space", () => {
            var result = CharacterGeometry.Convert(Fixture((a, m) => {
                var channel = m["m_VertexData"]!["m_Channels"]!["Array"]![1]!;
                channel["format"] = 10; channel["dimension"] = 0x31;
                var bytes = Convert.FromBase64String(m["m_VertexData"]!["m_DataSize"]!.GetValue<string>());
                uint[] packedNormals = [Pack(0, 0), Pack(511, 0), Pack(511, 511)];
                for (int i = 0; i < 3; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 42 + 12), packedNormals[i]);
                m["m_VertexData"]!["m_DataSize"] = Convert.ToBase64String(bytes);
            }), "test");
            var normals = result.Assets[0].Scene!.Meshes[0].Normals;
            Near(normals[0], 0, -1, 0); Near(normals[1], -1, 0, 0); Near(normals[2], 0, 1, 0);
        });
        test("character LOD boundary and absent selection", () => {
            foreach (int lod in new[] { -1, 4 }) reject(() => CharacterGeometry.Convert(Fixture(), "test", lod));
            reject(() => CharacterGeometry.Convert(Fixture(), "test", 1));
            var source = Fixture((a, m) => {
                m["m_Name"] = "Body_lod3";
                a["m_TOS"]!["Array"]![1]!["second"] = "Root/Body_lod3";
            });
            Check(CharacterGeometry.Convert(source, "test", 3).Assets[0].Scene!.Meshes[0].Name == "Body_lod3", "Upper supported LOD");
        });
        test("additional UV channels color and tangent frames survive conversion", () => {
            var result = CharacterGeometry.Convert(Fixture((a, m) => {
                var vertex = m["m_VertexData"]!;
                var channels = vertex["m_Channels"]!["Array"]!;
                void Extra(int index, int offset, int format, int dimension) {
                    channels[index]!["stream"] = 1; channels[index]!["offset"] = offset;
                    channels[index]!["format"] = format; channels[index]!["dimension"] = dimension;
                }
                Extra(2, 0, 0, 0x24); Extra(3, 16, 2, 0x34); Extra(5, 20, 0, 0x23); Extra(11, 32, 0, 1);
                var data = new byte[128 + 3 * 36];
                Convert.FromBase64String(vertex["m_DataSize"]!.GetValue<string>()).CopyTo(data, 0);
                for (int i = 0; i < 3; i++) {
                    int start = 128 + i * 36;
                    BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(start), 1);
                    BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(start + 12), i == 1 ? -1 : 1);
                    data[start + 16] = 255; data[start + 17] = 128; data[start + 18] = 0; data[start + 19] = 64;
                    foreach (int offset in new[] {20, 24, 28, 32}) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(start + offset), i + offset / 4f);
                }
                vertex["m_DataSize"] = Convert.ToBase64String(data);
            }), "extra-attributes");
            var mesh = result.Assets[0].Scene!.Meshes.Single();
            Check(mesh.UvSets!.Select(x => x.Set).SequenceEqual(new[] {0, 1, 7}), "Sparse UV identity");
            Near(mesh.UvSets![1].Values[2], 7, 8, 9); Near(mesh.UvSets[2].Values[1], 9);
            Near(mesh.Tangents![1], -1, 0, 0, 1);
            Near(mesh.Colors![0], 1, 128.0 / 255, 0, 64.0 / 255);
            reject(() => CharacterGeometry.Convert(Fixture((a,m) => m["m_VertexData"]!["m_Channels"]!["Array"]![4]!["dimension"] = 0x25), "invalid-uv"));
        });
        test("character empty objects and duplicate avatars rejected", () => {
            reject(() => CharacterGeometry.Convert(new("synthetic-1", [], []), "test"));
            var source = Fixture();
            reject(() => CharacterGeometry.Convert(source with { Objects = [source.Objects[0]] }, "test"));
            reject(() => CharacterGeometry.Convert(source with { Objects = [.. source.Objects, source.Objects[0]] }, "test"));
        });
        test("native source identities retain signed path IDs and bone provenance", () => {
            var source = Fixture();
            source = source with { Objects = [source.Objects[0], source.Objects[1] with { Id = long.MinValue }] };
            var scene = CharacterGeometry.Convert(source, "cab:fixture").Assets[0].Scene!;
            Check(scene.Meshes[0].SourceId == "cab:fixture:-9223372036854775808", "Signed source identity");
            Check(scene.Bones[1].SourcePath == "Root/Body_lod0" && scene.Bones[1].SourceHash == 200, "Bone path/hash provenance");
            Check(scene.HeadReference is null, "Absent native head mapping stays absent");
        });
        test("native head mapping and duplicate leaf provenance remain explicit", () => {
            SerializedDocument WithChild(string path) => Fixture((a, m) => {
                var definition = a["m_Avatar"]!;
                definition["m_AvatarSkeleton"]!["data"]!["m_Node"]!["Array"]!.AsArray().Add(new JsonObject { ["m_ParentId"] = 1 });
                definition["m_AvatarSkeleton"]!["data"]!["m_ID"]!["Array"]!.AsArray().Add(300);
                definition["m_DefaultPose"]!["data"]!["m_X"]!["Array"]!.AsArray().Add(definition["m_DefaultPose"]!["data"]!["m_X"]!["Array"]![0]!.DeepClone());
                a["m_TOS"]!["Array"]!.AsArray().Add(new JsonObject { ["first"] = 300, ["second"] = path });
            });
            var headScene = CharacterGeometry.Convert(WithChild("Root/Body_lod0/Bip001_Head"), "head").Assets[0].Scene!;
            var head = headScene.HeadReference!;
            Check(head.Bone == 2 && head.Name == "Bip001_Head" && head.NativePath == "Root/Body_lod0/Bip001_Head", "Exact native head provenance");
            Check(head.Status == "native-head-axes-unverified" && head.RestMatrix.SequenceEqual(headScene.Bones[2].RestMatrix!), "Native frame retained without inferred axes");
            var duplicate = CharacterGeometry.Convert(WithChild("Root/Body_lod0/Root"), "duplicate-leaf").Assets[0].Scene!;
            Check(duplicate.Bones[2].Name == "Root_2" && duplicate.Bones[2].SourcePath == "Root/Body_lod0/Root" && duplicate.Bones[2].SourceHash == 300, "Renamed leaf retains original identity");
        });
        test("index format enum alignment and declared geometry counts reject early", () => {
            foreach (int format in new[] { -1, 2, int.MaxValue })
                reject(() => CharacterGeometry.Convert(Fixture((a,m) => m["m_IndexFormat"] = format), "index-format"));
            foreach (int format in new[] { 0, 1 })
                reject(() => CharacterGeometry.Convert(Fixture((a,m) => {
                    m["m_IndexFormat"] = format;
                    m["m_IndexBuffer"]!["Array"] = Convert.ToBase64String(new byte[16]);
                    m["m_SubMeshes"]!["Array"]![0]!["firstByte"] = 1;
                }), "unaligned-index"));
            reject(() => CharacterGeometry.Convert(Fixture((a,m) => m["m_VertexData"]!["m_VertexCount"] = 5_000_001), "vertex-limit"));
            reject(() => CharacterGeometry.Convert(Fixture((a,m) => {
                var nodes = a["m_Avatar"]!["m_AvatarSkeleton"]!["data"]!["m_Node"]!["Array"]!.AsArray();
                for (int i = nodes.Count; i < 4097; i++) nodes.Add(new JsonObject { ["m_ParentId"] = 0 });
            }), "skeleton-limit"));
            reject(() => CharacterGeometry.Convert(Fixture((a,m) => {
                var submeshes = m["m_SubMeshes"]!["Array"]!.AsArray();
                for (int i = submeshes.Count; i < 4097; i++) submeshes.Add(submeshes[0]!.DeepClone());
            }), "submesh-limit"));
            reject(() => CharacterGeometry.Convert(Fixture((a,m) => {
                var palette = m["m_BoneNameHashes"]!["Array"]!.AsArray();
                for (int i = palette.Count; i < 4097; i++) palette.Add(100);
            }), "palette-limit"));
        });
        test("character empty mesh accepted with empty output", () => {
            var result = CharacterGeometry.Convert(Fixture((a, m) => {
                m["m_VertexData"]!["m_VertexCount"] = 0;
                m["m_VertexData"]!["m_DataSize"] = "";
                m["m_SubMeshes"]!["Array"]![0]!["indexCount"] = 0;
            }), "test");
            var mesh = result.Assets[0].Scene!.Meshes[0];
            Check(mesh.Positions.Length == 0 && mesh.Triangles.Length == 0 && mesh.Weights.Length == 0, "Empty mesh");
        });
        test("mesh bind palette overrides a conflicting avatar default pose", () => {
            var document = Fixture((a, m) => {
                a["m_Avatar"]!["m_AvatarSkeleton"]!["data"]!["m_Node"]!["Array"]!.AsArray().Add(JsonNode.Parse("{\"m_ParentId\":0}"));
                a["m_Avatar"]!["m_AvatarSkeleton"]!["data"]!["m_ID"]!["Array"]!.AsArray().Add(300);
                a["m_TOS"]!["Array"]!.AsArray().Add(JsonNode.Parse("{\"first\":300,\"second\":\"Root/Joint\"}"));
                a["m_Avatar"]!["m_DefaultPose"]!["data"]!["m_X"]!["Array"]!.AsArray().Add(JsonNode.Parse("{\"t\":{\"x\":0,\"y\":100,\"z\":0},\"q\":{\"x\":0,\"y\":0,\"z\":0,\"w\":1},\"s\":{\"x\":1,\"y\":1,\"z\":1}}"));
                m["m_BoneNameHashes"]!["Array"]![1] = 300;
                var bind = m["m_BindPose"]!["Array"]![1]!;
                bind["e03"] = 1; bind["e13"] = 1; bind["e23"] = 3;
            });
            var result = CharacterGeometry.Convert(document, "bind-test");
            Near(result.Assets[0].Scene!.Bones[2].Head, 0, 0, 1);
            Check(result.Assets[0].Scene!.Bones[2].RestMatrix is { Length: 16 }, "Full bind matrix retained");
            reject(() => CharacterGeometry.Convert(Fixture((a,m) => m["m_BindPose"]!["Array"]![0]!["e00"] = 0), "singular-bind"));
        });
        test("character malformed skeleton and unsupported layout rejected", () => {
            reject(() => CharacterGeometry.Convert(Fixture((a, m) => a["m_Avatar"]!["m_AvatarSkeleton"]!["data"]!["m_Node"]!["Array"]![1]!["m_ParentId"] = 1), "test"));
            reject(() => CharacterGeometry.Convert(Fixture((a, m) => a["m_Avatar"]!["m_DefaultPose"]!["data"]!["m_X"]!["Array"]!.AsArray().RemoveAt(1)), "test"));
            reject(() => CharacterGeometry.Convert(Fixture((a, m) => m["m_VertexData"]!["m_DataSize"] = "AA=="), "test"));
            reject(() => CharacterGeometry.Convert(Fixture((a, m) => m["m_VertexData"]!["m_Channels"]!["Array"]![0]!["format"] = 12), "test"));
            reject(() => CharacterGeometry.Convert(Fixture((a, m) => m["m_BonesPerVertex"] = 3), "test"));
            reject(() => CharacterGeometry.Convert(Fixture((a, m) => m["m_SubMeshes"]!["Array"]![0]!["topology"] = 1), "test"));
            reject(() => CharacterGeometry.Convert(Fixture((a, m) => m["m_SubMeshes"]!["Array"]![0]!["indexCount"] = 6), "test"));
            reject(() => CharacterGeometry.Convert(Fixture((a, m) => m["m_SubMeshes"]!["Array"]![0]!["baseVertex"] = 1), "test"));
        });
        test("character invalid skin indices and absent palette hashes rejected", () => {
            reject(() => CharacterGeometry.Convert(Fixture((a, m) => {
                var bytes = Convert.FromBase64String(m["m_VertexData"]!["m_DataSize"]!.GetValue<string>());
                bytes[41] = 2; m["m_VertexData"]!["m_DataSize"] = Convert.ToBase64String(bytes);
            }), "test"));
            reject(() => CharacterGeometry.Convert(Fixture((a, m) => m["m_BoneNameHashes"]!["Array"]![1] = 999), "test"));
        });
        test("native renderer matrix admits a mesh node absent from Avatar without inventing bones", () => {
            var original=Fixture();
            var extra=((JsonObject)original.Objects.Single(o=>o.ClassId==43).Data!).DeepClone().AsObject();
            extra["m_Name"]="Extra_lod0";
            extra["m_BindPose"]!["Array"]![0]!["e03"]=2;
            extra["m_BindPose"]!["Array"]![1]!["e03"]=1;
            var source=original with {Objects=[..original.Objects,new(3,43,extra)]};
            reject(()=>CharacterGeometry.Convert(source,"missing-avatar-mesh-node"));
            var transforms=new Dictionary<string,Matrix4x4>{["Body_lod0"]=Matrix4x4.CreateTranslation(1,2,3),["Extra_lod0"]=Matrix4x4.CreateTranslation(2,2,3)};
            var scene=CharacterGeometry.Convert(source,"native-renderer-mesh-node",nativeMeshTransforms:transforms).Assets[0].Scene!;
            Check(scene.Bones.Length==2&&scene.Meshes.Length==2,"Native renderer does not synthesize Avatar bones");
            Near(scene.Meshes.Single(m=>m.Name=="Extra_lod0").Positions[0],-2,-3,2);
            Check(scene.Meshes.All(m=>m.Weights.Length==6),"Authored skin influences remain present");
            transforms.Remove("Extra_lod0");reject(()=>CharacterGeometry.Convert(source,"missing-native-renderer",nativeMeshTransforms:transforms));
        });
        test("large vertex decode observes a managed cancellation checkpoint",()=>{
            var source=Fixture((avatar,mesh)=>{
                var vertex=mesh["m_VertexData"]!;var original=Convert.FromBase64String(vertex["m_DataSize"]!.GetValue<string>());var bytes=new byte[5000*42];
                for(int i=0;i<5000;i++)original.AsSpan(0,42).CopyTo(bytes.AsSpan(i*42,42));
                vertex["m_VertexCount"]=5000;vertex["m_DataSize"]=Convert.ToBase64String(bytes);
            });
            bool cancelled=false;OperationProgress.Sink=update=>{if(update.Stage=="decode-mesh-vertices"&&update.Completed>=4096)throw new OperationCanceledException();};
            try{CharacterGeometry.Convert(source,"cancel-vertices");}catch(OperationCanceledException){cancelled=true;}finally{OperationProgress.Sink=null;}
            Check(cancelled,"Cancellation must occur before the whole mesh finishes");
        });
        test("octahedral signed ten-bit canonical directions", () => {
            (int x, int y, Vector3 direction)[] cases = [(0, 0, Vector3.UnitZ), (511, 0, Vector3.UnitX), (-511, 0, -Vector3.UnitX), (0, 511, Vector3.UnitY), (0, -511, -Vector3.UnitY), (511, 511, -Vector3.UnitZ)];
            foreach (var c in cases) {
                var decoded = CharacterGeometry.Octahedral(Pack(c.x, c.y));
                Check(Vector3.Distance(decoded, c.direction) < 1e-6, "Canonical direction");
                Check(Vector3.Distance(CharacterGeometry.Octahedral(Pack(c.x, c.y) | 0xfff00000u), c.direction) < 1e-6, "Unused upper bits");
            }
        });
        test("octahedral signed endpoints and oblique normals stay normalized", () => {
            foreach (int x in new[] { -512, -511, -256, -1, 0, 1, 256, 511 })
                foreach (int y in new[] { -512, -511, -256, -1, 0, 1, 256, 511 }) {
                    var n = CharacterGeometry.Octahedral(Pack(x, y));
                    Check(float.IsFinite(n.X) && float.IsFinite(n.Y) && float.IsFinite(n.Z) && Math.Abs(n.Length() - 1) < 1e-6, "Finite unit vector");
                }
        });
    }

    private static uint Pack(int x, int y) => (uint)(x & 1023) | ((uint)(y & 1023) << 10);
    private static Matrix4x4 ReadMatrix(double[] m) => new((float)m[0], (float)m[4], (float)m[8], (float)m[12], (float)m[1], (float)m[5], (float)m[9], (float)m[13], (float)m[2], (float)m[6], (float)m[10], (float)m[14], (float)m[3], (float)m[7], (float)m[11], (float)m[15]);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Near(double[] actual, params double[] expected) => Check(actual.Length == expected.Length && actual.Zip(expected).All(p => Math.Abs(p.First - p.Second) < 1e-5), "Coordinate mismatch");

    // Explicit synthetic serialized object shape; no game files or decoder implementation needed.
    private static SerializedDocument Fixture(Action<JsonObject, JsonObject>? change = null)
    {
        var avatar = JsonNode.Parse("""
        {"m_Name":"Synthetic","m_TOS":{"Array":[{"first":100,"second":"Root"},{"first":200,"second":"Root/Body_lod0"}]},
         "m_Avatar":{"m_AvatarSkeleton":{"data":{"m_Node":{"Array":[{"m_ParentId":-1},{"m_ParentId":0}]},"m_ID":{"Array":[100,200]}}},
          "m_DefaultPose":{"data":{"m_X":{"Array":[
           {"t":{"x":0,"y":0,"z":0},"q":{"x":0,"y":0,"z":0,"w":1},"s":{"x":1,"y":1,"z":1}},
           {"t":{"x":1,"y":2,"z":3},"q":{"x":0,"y":0,"z":0,"w":1},"s":{"x":1,"y":1,"z":1}}]}}}}}
        """)!.AsObject();
        var mesh = JsonNode.Parse("""
        {"m_Name":"Body_lod0","m_VertexData":{"m_VertexCount":3,"m_Channels":{"Array":[]},"m_DataSize":""},
         "m_BoneNameHashes":{"Array":[100,200]},"m_BonesPerVertex":2,"m_IndexFormat":0,
         "m_BindPose":{"Array":[{"e00":1,"e10":0,"e20":0,"e30":0,"e01":0,"e11":1,"e21":0,"e31":0,"e02":0,"e12":0,"e22":1,"e32":0,"e03":1,"e13":2,"e23":3,"e33":1},{"e00":1,"e10":0,"e20":0,"e30":0,"e01":0,"e11":1,"e21":0,"e31":0,"e02":0,"e12":0,"e22":1,"e32":0,"e03":0,"e13":0,"e23":0,"e33":1}]},
         "m_IndexBuffer":{"Array":"AAABAAIA"},"m_SubMeshes":{"Array":[{"topology":0,"firstByte":0,"indexCount":3,"baseVertex":0}]}}
        """)!.AsObject();
        var channels = mesh["m_VertexData"]!["m_Channels"]!["Array"]!.AsArray();
        for (int i = 0; i < 14; i++) channels.Add(new JsonObject { ["stream"] = 0, ["offset"] = 0, ["format"] = 0, ["dimension"] = 0 });
        void Channel(int index, int offset, int format, int dimension) { channels[index]!["offset"] = offset; channels[index]!["format"] = format; channels[index]!["dimension"] = dimension; }
        Channel(0, 0, 0, 3); Channel(1, 12, 0, 3); Channel(4, 24, 0, 2); Channel(12, 32, 0, 2); Channel(13, 40, 6, 2);
        byte[] data = new byte[126];
        for (int i = 0; i < 3; i++) {
            float[] fields = [i == 1 ? 1 : 0, i == 2 ? 1 : 0, 0, 0, 0, 1, i == 1 ? 1 : 0, i == 2 ? 1 : 0, 2, 1];
            for (int j = 0; j < fields.Length; j++) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 42 + j * 4), fields[j]);
            data[i * 42 + 40] = 0; data[i * 42 + 41] = 1;
        }
        mesh["m_VertexData"]!["m_DataSize"] = Convert.ToBase64String(data);
        change?.Invoke(avatar, mesh);
        return new("synthetic-1", [], [new(1, 90, avatar), new(2, 43, mesh)]);
    }
}
