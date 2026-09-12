using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sora.Core;

/// <summary>Native prefab traversal for standalone rigid and skinned items, without a humanoid Avatar requirement.</summary>
public static class NativeItemImport
{
    private sealed record Node(ResolvedAsset Source, string Name, int Parent, string Path, Matrix4x4 Local, Matrix4x4 World);
    private sealed record RenderMesh(ResolvedAsset Renderer, ResolvedAsset Mesh, JsonElement Data, int Node, int[] Palette, uint[] Hashes, ResolvedAsset[] Materials, bool Enabled);
    private static JsonElement Json(SerializedObject value) => JsonSerializer.SerializeToElement(value.Data, WireJson.Options);
    // Meshes carry a native per-bone AABB cache that neither item import nor mesh decoding
    // consumes; some authored meshes store the engine's empty-box sentinel (+Infinity/-Infinity
    // in m_Min/m_Max), which the JSON contract cannot represent. Project the mesh without that
    // one cache field and keep every consumed field (name, vertex/index/submesh, bone hashes,
    // bind pose, stream data) untouched. The decoded source object is never modified.
    private static JsonElement JsonMesh(SerializedObject value) => JsonSerializer.SerializeToElement(
        value.Data is Dictionary<string, object?> map && map.ContainsKey("m_BonesAABB")
            ? new Dictionary<string, object?>(map.Where(pair => pair.Key != "m_BonesAABB"), StringComparer.Ordinal)
            : value.Data, WireJson.Options);
    private static JsonElement[] Array(JsonElement value, string name) => value.GetProperty(name).GetProperty("Array").EnumerateArray().ToArray();
    private static float N(JsonElement value, string name) => value.GetProperty(name).GetSingle();
    private static Vector3 V(JsonElement value) => new(N(value,"x"),N(value,"y"),N(value,"z"));
    private static double[] Values(Vector3 value) => [value.X,value.Y,value.Z];
    private static double[] Values(Matrix4x4 m) => [m.M11,m.M21,m.M31,m.M41,m.M12,m.M22,m.M32,m.M42,m.M13,m.M23,m.M33,m.M43,m.M14,m.M24,m.M34,m.M44];
    private static Matrix4x4 Bind(JsonElement m) => new(N(m,"e00"),N(m,"e10"),N(m,"e20"),N(m,"e30"),N(m,"e01"),N(m,"e11"),N(m,"e21"),N(m,"e31"),N(m,"e02"),N(m,"e12"),N(m,"e22"),N(m,"e32"),N(m,"e03"),N(m,"e13"),N(m,"e23"),N(m,"e33"));
    private static string Identity(ResolvedAsset source) => source.Cab + ":" + source.Object.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static DatabaseDocument Import(GameResources game, string path, AddressResource? selectedPrefab = null)
    {
        var matches = game.Manifest.Assets.Where(a => a.Path == path).DistinctBy(a => (a.Hash,a.Path)).ToArray();
        Validation.Require(selectedPrefab is not null || matches.Length == 1, "Select an exact unambiguous item prefab");
        if(selectedPrefab is not null)Validation.Require(selectedPrefab.Path==path&&game.Manifest.Assets.Contains(selectedPrefab),"Selected item locator is not in the current manifest");
        var selectedAddress=selectedPrefab ?? game.SelectAddress(path,matches[0].Hash.ToString("x16"));
        var root = game.ResolveAddress(selectedAddress, 1);
        var nodes = new List<Node>(); var nodeIds = new Dictionary<string,int>(StringComparer.Ordinal);
        ResolvedAsset Resolve(ResolvedAsset source, JsonElement pointer) => game.Resolve(source.Cab, pointer) ?? throw new InvalidDataException("Missing item component reference");
        ResolvedAsset Transform(ResolvedAsset go) => Array(Json(go.Object), "m_Component").Select(p => Resolve(go, p.GetProperty("component"))).Single(p => p.Object.ClassId == 4);
        void Visit(ResolvedAsset transform, int parent) {
            OperationProgress.Report("parse-item-hierarchy", nodes.Count);
            Validation.Require(nodes.Count < 4096 && !nodeIds.ContainsKey(Identity(transform)), "Invalid or cyclic item hierarchy");
            var data = Json(transform.Object); var go = Resolve(transform, data.GetProperty("m_GameObject"));
            string name = Json(go.Object).GetProperty("m_Name").GetString()!;
            var q = data.GetProperty("m_LocalRotation");
            var local = Matrix4x4.CreateScale(V(data.GetProperty("m_LocalScale"))) * Matrix4x4.CreateFromQuaternion(new(N(q,"x"),N(q,"y"),N(q,"z"),N(q,"w"))) * Matrix4x4.CreateTranslation(V(data.GetProperty("m_LocalPosition")));
            int index = nodes.Count; nodeIds.Add(Identity(transform),index);
            nodes.Add(new(transform,name,parent,parent < 0 ? name : nodes[parent].Path + "/" + name,local,local * (parent < 0 ? Matrix4x4.Identity : nodes[parent].World)));
            foreach (var pointer in Array(data,"m_Children")) Visit(Resolve(transform,pointer),index);
        }
        Visit(Transform(root), -1);
        int disabledLod0Renderers=0,otherLodRenderers=0;
        var selected = new List<RenderMesh>(); var anchors = new Dictionary<int, Matrix4x4>(); var skinNodes = new HashSet<int>(); var boneNodes = new HashSet<int>();
        for (int i = 0; i < nodes.Count; i++) {
            var node = nodes[i]; var go = Resolve(node.Source,Json(node.Source.Object).GetProperty("m_GameObject"));
            var components = Array(Json(go.Object),"m_Component").Select(p => Resolve(go,p.GetProperty("component"))).ToArray();
            foreach (var renderer in components.Where(c => c.Object.ClassId is 23 or 137)) {
                var data = Json(renderer.Object);
                bool enabled = data.GetProperty("m_Enabled").GetBoolean();
                bool skinned = renderer.Object.ClassId == 137;
                var mesh = skinned ? Resolve(renderer,data.GetProperty("m_Mesh")) : Resolve(components.Single(c=>c.Object.ClassId==33),Json(components.Single(c=>c.Object.ClassId==33).Object).GetProperty("m_Mesh"));
                var meshData = JsonMesh(mesh.Object); string meshName = meshData.GetProperty("m_Name").GetString()!;
                // Current standalone scope uses native LOD0 names; other LODs are not silently combined.
                if (!meshName.EndsWith("_lod0",StringComparison.OrdinalIgnoreCase)) {otherLodRenderers++;continue;}
                // A default-disabled LOD0 renderer is a real authored part with its own mesh and material; keep its
                // geometry and native initial state instead of dropping it. The frontend consumes the state in a later module.
                if (!enabled) disabledLod0Renderers++;
                int[] palette = skinned ? Array(data,"m_Bones").Select(pointer => nodeIds[Identity(Resolve(renderer,pointer))]).ToArray() : [];
                uint[] hashes = skinned ? Array(meshData,"m_BoneNameHashes").Select(h=>h.GetUInt32()).ToArray() : [];
                var binds = Array(meshData,"m_BindPose");
                Validation.Require(!skinned || palette.Length == hashes.Length && binds.Length == palette.Length, "Item skin palette disagrees with native renderer bones");
                for (int j = 0; j < palette.Length; j++) {
                    Validation.Require(Matrix4x4.Invert(Bind(binds[j]),out var inverse),"Singular item mesh bind pose");
                    var anchor = inverse * node.World;
                    if (anchors.TryGetValue(palette[j],out var previous)) Validation.Require(Values(anchor).Zip(Values(previous),(a,b)=>Math.Abs(a-b)).Max()<0.001,"Item meshes disagree on bone bind frames");
                    else anchors.Add(palette[j],anchor);
                    for (int ancestor = palette[j]; ancestor >= 0; ancestor = nodes[ancestor].Parent) { boneNodes.Add(ancestor); skinNodes.Add(ancestor); }
                }
                var materials = Array(data,"m_Materials").Select(pointer=>Resolve(renderer,pointer)).ToArray();
                Validation.Require(materials.Length>0 && materials.All(m=>m.Object.ClassId==21),"Item material reference is missing");
                selected.Add(new(renderer,mesh,meshData,i,palette,hashes,materials,enabled));
            }
        }
        // A prefab may carry its own Animator. Every Transform target authored by that controller's clips is
        // a real native node and must stay bindable even when it is not a mesh skin target; otherwise the
        // authored clip is rejected instead of bound. Such nodes become non-deforming rig bones. An Animator
        // without a controller is a valid native state and is skipped, exactly as the initial-pose reader does.
        for (int i = 0; i < nodes.Count; i++) {
            var animatorNode = nodes[i];
            var animatorOwner = Resolve(animatorNode.Source, Json(animatorNode.Source.Object).GetProperty("m_GameObject"));
            foreach (var animator in Array(Json(animatorOwner.Object), "m_Component").Select(p => Resolve(animatorOwner, p.GetProperty("component"))).Where(c => c.Object.ClassId == 95)) {
                var controller = game.Resolve(animator.Cab, Json(animator.Object).GetProperty("m_Controller"));
                if (controller is null) continue;
                var animated = nodes.Select((n, index) => (n, index))
                    .Where(x => x.n.Path == animatorNode.Path || x.n.Path.StartsWith(animatorNode.Path + "/", StringComparison.Ordinal))
                    .GroupBy(x => NativeEquipmentDefaultPoseReader.PathHash(x.n.Path == animatorNode.Path ? "" : x.n.Path[(animatorNode.Path.Length + 1)..]))
                    .ToDictionary(g => g.Key, g => g.Select(x => x.index).ToArray());
                foreach (var reference in NativeAnimationController.Read(game, controller)) {
                    foreach (var binding in Array(Json(reference.Clip.Object).GetProperty("m_ClipBindingConstant"), "genericBindings")) {
                        if (binding.GetProperty("typeID").GetInt32() != 4) continue;
                        if (!animated.TryGetValue(binding.GetProperty("path").GetUInt32(), out var targets) || targets.Length != 1) continue;
                        for (int ancestor = targets[0]; ancestor >= 0; ancestor = nodes[ancestor].Parent) boneNodes.Add(ancestor);
                    }
                }
            }
        }
        Validation.Require(selected.Count>0,"No supported LOD0 renderer found in item prefab");
        Validation.Require(selected.Sum(item => (long)item.Data.GetProperty("m_VertexData").GetProperty("m_VertexCount").GetInt32()) <= 5_000_000, "Item vertex limit exceeded");
        Validation.Require(selected.Sum(item => Array(item.Data,"m_SubMeshes").Sum(sub => (long)sub.GetProperty("indexCount").GetInt32()/3)) <= 10_000_000, "Item triangle limit exceeded");
        var reflection=Matrix4x4.CreateScale(-1,1,1); var space=reflection*Matrix4x4.CreateRotationX(MathF.PI/2);
        Matrix4x4.Invert(space,out var inverseSpace);
        // Keep the skin/ancestor closure at its original indices and place the additional animated nodes
        // after it in native topology order, so palette indices, vertex groups and bind frames are unchanged.
        var orderedBones=skinNodes.Order().Concat(boneNodes.Except(skinNodes).Order()).ToArray();var boneMap=orderedBones.Select((node,index)=>(node,index)).ToDictionary(p=>p.node,p=>p.index);
        var restWorld=new Dictionary<int,Matrix4x4>();var bones=new List<BoneRecord>();
        foreach(int nodeIndex in orderedBones) {
            var node=nodes[nodeIndex];var world=anchors.GetValueOrDefault(nodeIndex,node.Local*(node.Parent<0?Matrix4x4.Identity:restWorld[node.Parent]));restWorld[nodeIndex]=world;
            var rest=reflection*world*space;
            Validation.Require(Matrix4x4.Decompose(rest,out var scale,out var rotation,out var head) && Vector3.Distance(scale,Vector3.One)<0.001,"Scaled item bones require explicit scale support");
            var axis=Vector3.Normalize(Vector3.Transform(Vector3.UnitY,rotation));
            var shortest=axis.Y < -0.999999f ? Quaternion.CreateFromAxisAngle(Vector3.UnitZ,MathF.PI):Quaternion.Normalize(new Quaternion(axis.Z,0,-axis.X,1+axis.Y));
            var twist=Quaternion.Normalize(Quaternion.Conjugate(shortest)*rotation);double roll=Math.IEEERemainder(2*Math.Atan2(twist.Y,twist.W),2*Math.PI);
            string name=node.Name; if(bones.Any(b=>b.Name==name))name += "_"+nodeIndex;
            bones.Add(new(name,node.Parent<0?-1:boneMap[node.Parent],Values(head),Values(head+axis*0.05f),roll,Values(rest),node.Path));
        }
        var meshes=new List<MeshRecord>();var materialsByMesh=new Dictionary<string,ResolvedAsset[]>();var identities=new Dictionary<string,string>();
        foreach(var item in selected) {
            OperationProgress.Report("decode-item-mesh",meshes.Count,selected.Count);
            var json=JsonSerializer.SerializeToNode(item.Data,WireJson.Options)!;
            if(json["m_StreamData"] is {} stream && stream["size"]!.GetValue<long>()>0)
                json["m_VertexData"]!["m_DataSize"]=Convert.ToBase64String(game.ReadResource(item.Mesh.Cab,stream["path"]!.GetValue<string>(),stream["offset"]!.GetValue<long>(),checked((int)stream["size"]!.GetValue<long>())));
            string name=json["m_Name"]!.GetValue<string>();if(materialsByMesh.ContainsKey(name)) {name += "_"+item.Node;json["m_Name"]=name;}
            bool skinned=item.Palette.Length>0;
            var mesh=CharacterGeometry.DecodeMesh(JsonSerializer.SerializeToElement(json,WireJson.Options),item.Mesh.Cab,item.Mesh.Object.Id,skinned?nodes[item.Node].World*space:space,item.Hashes,skinned);
            mesh=mesh with {Weights=mesh.Weights.Select(w=>w with {Bone=boneMap[item.Palette[w.Bone]]}).ToArray(),Node=item.Node,CoordinateSpace=skinned?"scene":"node",RendererEnabled=item.Enabled};
            meshes.Add(mesh);materialsByMesh.Add(name,item.Materials);identities.Add(name,Identity(item.Mesh));
        }
        var exportedNodes=nodes.Select(n=>new SceneNodeRecord(Identity(n.Source),n.Name,n.Parent,n.Path,Values(inverseSpace*n.Local*space))).ToArray();
        var scene=new SceneDocument(Json(root.Object).GetProperty("m_Name").GetString()!,bones.ToArray(),meshes.ToArray(),[],[],Nodes:exportedNodes,ImportDiagnostics:[$"Native LOD0 scope: {selected.Count} selected renderers ({disabledLod0Renderers} disabled-by-default LOD0 included with native renderer state); {otherLodRenderers} other-LOD renderers omitted",$"Selected native prefab bundle {selectedAddress.Bundle}; hash {selectedAddress.Hash:x16}"]);
        scene=NativeCharacterImport.ApplyMaterials(game,scene,materialsByMesh,identities);
        var database=new DatabaseDocument(game.Manifest.Version,[new(path,scene.Name,"Native standalone LOD0 item; animation and character equipment binding not included","weapon",[],scene)]);
        Validation.Database(database);return database;
    }
}
