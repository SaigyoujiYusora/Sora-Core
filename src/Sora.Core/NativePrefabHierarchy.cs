using System.Numerics;
using System.Text.Json;

namespace Sora.Core;

public sealed record NativeHierarchyNode(ResolvedAsset Transform, ResolvedAsset GameObject, string Name, int Parent,
    string SourcePath, Matrix4x4 Local, Matrix4x4 World);

/// <summary>Exact prefab Transform references and parentage; no name-based parenting.</summary>
public static class NativePrefabHierarchy
{
    public static string Identity(ResolvedAsset asset)=>asset.Cab+":"+asset.Object.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static JsonElement Json(SerializedObject value)=>JsonSerializer.SerializeToElement(value.Data,WireJson.Options);
    public static NativeHierarchyNode[] Read(GameResources game,string path)
    {
        var addresses=game.Manifest.Assets.Where(a=>a.Path==path).DistinctBy(a=>(a.Hash,a.Path)).ToArray();
        Validation.Require(addresses.Length==1,"Prefab hierarchy address is missing or ambiguous");
        return Read(game,game.ResolveAddress(game.SelectAddress(path),1));
    }
    public static NativeHierarchyNode[] Read(GameResources game,ResolvedAsset root)
    {
        var nodes=new List<NativeHierarchyNode>();var seen=new HashSet<string>();
        ResolvedAsset Resolve(ResolvedAsset owner,JsonElement pointer)=>game.Resolve(owner.Cab,pointer)??throw new InvalidDataException("Missing prefab hierarchy reference");
        JsonElement[] Array(JsonElement value,string field)=>value.GetProperty(field).GetProperty("Array").EnumerateArray().ToArray();
        var rootTransform=Array(Json(root.Object),"m_Component").Select(component=>Resolve(root,component.GetProperty("component"))).Single(component=>component.Object.ClassId==4);
        float N(JsonElement value,string axis)=>value.GetProperty(axis).GetSingle();
        Vector3 V(JsonElement value)=>new(N(value,"x"),N(value,"y"),N(value,"z"));
        void Visit(ResolvedAsset transform,int parent) {
            OperationProgress.Report("read-prefab-hierarchy",nodes.Count);
            Validation.Require(transform.Object.ClassId==4&&nodes.Count<4096&&seen.Add(Identity(transform)),"Invalid or cyclic prefab Transform hierarchy");
            var data=Json(transform.Object);var go=Resolve(transform,data.GetProperty("m_GameObject"));
            Validation.Require(go.Object.ClassId==1,"Transform owner is not a GameObject");
            string name=Json(go.Object).GetProperty("m_Name").GetString()!;var q=data.GetProperty("m_LocalRotation");
            var rotation=new Quaternion(N(q,"x"),N(q,"y"),N(q,"z"),N(q,"w"));
            Validation.Require(Math.Abs(rotation.LengthSquared()-1)<0.001,"Invalid prefab rotation");
            var local=Matrix4x4.CreateScale(V(data.GetProperty("m_LocalScale")))*Matrix4x4.CreateFromQuaternion(rotation)*Matrix4x4.CreateTranslation(V(data.GetProperty("m_LocalPosition")));
            int index=nodes.Count;nodes.Add(new(transform,go,name,parent,parent<0?name:nodes[parent].SourcePath+"/"+name,local,local*(parent<0?Matrix4x4.Identity:nodes[parent].World)));
            foreach(var child in Array(data,"m_Children")) {
                var resolved=Resolve(transform,child);var father=Resolve(resolved,Json(resolved.Object).GetProperty("m_Father"));
                Validation.Require(Identity(father)==Identity(transform),"Prefab child and parent references disagree");Visit(resolved,index);
            }
        }
        Visit(rootTransform,-1);return nodes.ToArray();
    }
}
