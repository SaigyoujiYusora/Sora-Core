using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sora.Core;

public sealed record NativeNpcDeclaration(string Id,string Template,string MeshTable,string? Face,string? Ear,string[] Parts,double Scale,JsonElement Source);
public sealed record NativeNpcPartRecord(string Name,long AvatarHash,string AvatarCab,long AvatarObject,string Attachment,string[] Meshes,NativeNpcBoneBinding[] Bindings,double MaxNeutralError, JsonElement SourceSlot, NativeNpcMeshSource[] MeshSources);
public sealed record NativeNpcMeshSource(long Hash,string Cab,long Object,JsonElement BindPoses,uint[] BonePathHashes);
public sealed record NativeNpcBoneBinding(int AvatarNodeIndex,string NativePath,uint NativePathHash,int SceneBone,string AssembledPath);
public sealed record NativeNpcProvenance(ResourceFileRecord PrefabInfo,JsonElement Declaration,string TemplateCab,long TemplateObject,string CommonAvatarCab,long CommonAvatarObject,string MeshTableCab,long MeshTableObject,NativeNpcPartRecord[] Parts,string[] Diagnostics, double[][]? PreAuthoredRestMatrices = null, NativeNpcNeutralMeshChange[]? NeutralMeshChanges = null);
public sealed record NativeNpcNeutralMeshChange(string Mesh,double MaxDisplacement);

/// <summary>Independent native NPC declarations, resource identity and part assembly.</summary>
public static class NativeNpcImport
{
    internal static JsonElement Json(SerializedObject o)=>JsonSerializer.SerializeToElement(o.Data,WireJson.Options);
    internal static JsonElement[] A(JsonElement e,string k)=>e.GetProperty(k).GetProperty("Array").EnumerateArray().ToArray();
    public static void Validate(SceneDocument scene,NativeNpcProvenance npc)
    {
        Validation.Require(npc.Parts is not null&&npc.Parts.Length is >0 and <=128&&npc.Diagnostics is not null&&npc.Diagnostics.Length<=256,"Invalid NPC provenance collections");
        Validation.Require(npc.Parts!.Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()==npc.Parts!.Length,"Duplicate NPC part names");
        var declaration=Parse(npc.Declaration);Validation.Require(declaration.Parts.All(name=>npc.Parts!.Count(p=>p.Name==name)==1),"NPC provenance omits an authored selected part");
        Validation.Require(npc.PrefabInfo is not null&&npc.TemplateObject!=0&&npc.CommonAvatarObject!=0&&npc.MeshTableObject!=0,"NPC source identity is missing");
        Validation.Require(npc.PrefabInfo!.Resource is not null&&Path.GetFileNameWithoutExtension(npc.PrefabInfo.Resource.Name.Replace('\\','/'))==declaration.Id,"NPC declaration ID disagrees with PrefabInfo resource name");
        var meshNames=new HashSet<string>(StringComparer.Ordinal);
        foreach(var part in npc.Parts!)
        {
            Validation.Require(part is not null&&part.AvatarObject!=0&&part.Meshes is not null&&part.Bindings is not null&&part.MeshSources is not null&&part.Meshes.Length==part.MeshSources.Length&&part.SourceSlot.ValueKind==JsonValueKind.Object&&part.Bindings.Length<=4096&&double.IsFinite(part.MaxNeutralError)&&part.MaxNeutralError>=0,"Invalid NPC part provenance");
            Validation.Require(part!.SourceSlot.GetProperty("name").GetString()==part.Name&&part.SourceSlot.GetProperty("parentBoneTransformName").GetString()==part.Attachment&&part.SourceSlot.GetProperty("selfAvatarPathHash").GetInt64()==part.AvatarHash,"NPC source slot identity disagrees with part provenance");
            if(part.SourceSlot.GetProperty("bUseSelfAvatar").GetInt32()!=0)Validation.Require(part.AvatarHash!=0&&!string.IsNullOrEmpty(part.Attachment),"NPC self Avatar requires hash and attachment");
            foreach(string name in part!.Meshes!){Validation.Require(meshNames.Add(name)&&scene.Meshes.Count(m=>m.Name==name)==1,"NPC mesh ownership absent or ambiguous");}
            var nodes=new HashSet<int>();foreach(var binding in part.Bindings!){Validation.Require(binding is not null&&binding.AvatarNodeIndex>=0&&nodes.Add(binding.AvatarNodeIndex)&&binding.SceneBone>=0&&binding.SceneBone<scene.Bones.Length&&binding.AssembledPath==scene.Bones[binding.SceneBone].SourcePath,"Invalid NPC part-to-scene binding");}
            var slotMeshes=A(part.SourceSlot,"partSubMeshsLOD0");Validation.Require(slotMeshes.Length==part.MeshSources!.Length,"NPC source slot and mesh provenance count disagree");
            var nativeHashes=part.Bindings!.Select(b=>b.NativePathHash).ToHashSet();
            for(int i=0;i<part.MeshSources.Length;i++)
            {
                var mesh=part.MeshSources[i];Validation.Require(mesh is not null&&mesh.Hash!=0&&mesh.Object!=0&&mesh.BonePathHashes is not null&&mesh.BonePathHashes.Length<=4096&&mesh.BindPoses.GetProperty("Array").GetArrayLength()==mesh.BonePathHashes.Length,"Invalid native NPC mesh bind palette");
                Validation.Require(slotMeshes[i].GetProperty("meshPathHash").GetInt64()==mesh!.Hash&&slotMeshes[i].GetProperty("meshName").GetString()==part.Meshes![i],"NPC slot mesh hash/name disagrees with provenance");
                Validation.Require(scene.Meshes.Single(m=>m.Name==part.Meshes![i]).SourceId==mesh.Cab+":"+mesh.Object.ToString(System.Globalization.CultureInfo.InvariantCulture),"NPC scene mesh identity disagrees with source provenance");
                Validation.Require(mesh.BonePathHashes!.All(nativeHashes.Contains),"NPC mesh palette references a bone outside its source part namespace");
            }
        }
        Validation.Require(meshNames.Count==scene.Meshes.Length,"NPC provenance does not cover assembled meshes");
        if(npc.PreAuthoredRestMatrices is {} rests)Validation.Require(rests.Length==scene.Bones.Length&&rests.All(r=>r is not null&&r.Length==16&&r.All(double.IsFinite)),"Invalid pre-authored NPC rest snapshot");
        if(npc.NeutralMeshChanges is {} changes)Validation.Require(changes.Length==scene.Meshes.Length&&changes.Select(c=>c?.Mesh).Distinct(StringComparer.Ordinal).Count()==changes.Length&&changes.All(c=>c is not null&&meshNames.Contains(c.Mesh)&&double.IsFinite(c.MaxDisplacement)&&c.MaxDisplacement>=0),"Invalid NPC neutral change diagnostics");
    }
    public static string[] Search(GameResources resources,string query)
    {
        using var index=WireJson.Parse(resources.GetBytes("Json/NPC/PrefabInfo/manifest.json"));
        return index.RootElement.GetProperty("files").EnumerateArray()
            .Select(file=>"Json/NPC/PrefabInfo/"+file.GetString()!)
            .Where(path=>path.Contains(query,StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    public static NativeNpcDeclaration Parse(JsonElement e)
    {
        string? Optional(string k)=>e.TryGetProperty(k,out var p)&&p.GetString() is {Length:>0} v?v:null;
        var parts=e.GetProperty("partNameIdList").EnumerateArray().Select(x=>x.GetString()!).ToArray();
        Validation.Require(parts.Length is >0 and <=128&&parts.Distinct(StringComparer.Ordinal).Count()==parts.Length,"Invalid NPC part selection");
        double scale=e.GetProperty("scale").GetDouble();Validation.Require(double.IsFinite(scale)&&scale>0&&scale<=100,"Invalid NPC scale");
        return new(e.GetProperty("id").GetString()!,e.GetProperty("avatarTempletName").GetString()!,e.GetProperty("avatarMeshName").GetString()!,Optional("facialMorphAvatarName"),Optional("earMorphAvatarName"),parts,scale,e.Clone());
    }
    internal static AddressResource DeclaredResource(GameResources g,string declaration,string kind)
    {
        var segments=declaration.Split('/');Validation.Require(segments.Length>=3&&!segments.Any(x=>x.Length==0||x is "." or ".."),"Invalid native declaration");
        string file=kind switch {"template"=>"data_npc_avatartemplet_", "mesh"=>"data_npc_avatarmesh_", "face"=>"data_facemorph_avatar_", "ear"=>"data_earmorph_avatar_", _=>throw new InvalidDataException("Unknown NPC declaration kind")};
        string folder=kind switch {"template"=>"/gameplay/npc/avatartemplet/", "mesh"=>"/gameplay/npc/avatarmesh/", _=>"/skeletalmorphcfg/"+segments[^2].ToLowerInvariant()+"/"};
        var matches=g.Manifest.Assets.Where(a=>a.Path.Contains(folder,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(a.Path).Equals(file+segments[^1]+".asset",StringComparison.OrdinalIgnoreCase)).DistinctBy(a=>(a.Hash,a.Path)).ToArray();
        Validation.Require(matches.Length==1,"Declared NPC resource absent or ambiguous: "+declaration);return g.SelectAddress(matches[0].Path,matches[0].Hash.ToString("x16"));
    }
    public static DatabaseDocument Import(GameResources g,string query)
    {
        var candidates=Search(g,query);Validation.Require(candidates.Length==1,"NPC selection is absent or ambiguous");
        using var payload=WireJson.Parse(g.GetBytes(candidates[0]));var selection=Parse(payload.RootElement);
        var template=g.ResolveAddress(DeclaredResource(g,selection.Template,"template"),114);var table=g.ResolveAddress(DeclaredResource(g,selection.MeshTable,"mesh"),114);
        var common=g.Resolve(template.Cab,Json(template.Object).GetProperty("sizeAvatar"))??throw new InvalidDataException("NPC template has no size Avatar");
        var scene=CharacterGeometry.Convert(new(common.Document.UnityVersion,[],[common.Object]),selection.Id,allowEmptyMeshes:true).Assets[0].Scene!;
        var bones=scene.Bones.ToList();var meshes=new List<MeshRecord>();var parts=new List<NativeNpcPartRecord>();var diagnostics=new List<string>();
        var materials=new Dictionary<string,ResolvedAsset[]>(StringComparer.Ordinal);var identities=new Dictionary<string,string>(StringComparer.Ordinal);
        var slots=A(Json(table.Object),"avatarSlotMeshDatas").ToList();
        var selectedNames=selection.Parts.ToList();var accessoryNames=new HashSet<string>();
        long mainHash=Json(table.Object).GetProperty("mainPrefabPathHash").GetInt64();
        if(!g.Manifest.Assets.Any(a=>a.Hash==mainHash))diagnostics.Add("Mesh table mainPrefabPathHash "+mainHash+" absent from manifest; using explicit sizeAvatar and native part resources");
        if(selection.Source.TryGetProperty("accessories",out var accessories))foreach(var acc in accessories.EnumerateArray())
        {
            string name=acc.GetProperty("name").GetString()!;
            if(acc.GetProperty("bIsHidden").GetBoolean()){diagnostics.Add("Hidden authored accessory retained: "+name);continue;}
            var addresses=g.Manifest.Assets.Where(a=>a.Path.Contains("/npc/accessory/",StringComparison.OrdinalIgnoreCase)&&Path.GetFileNameWithoutExtension(a.Path)==name).DistinctBy(a=>(a.Hash,a.Path)).ToArray();Validation.Require(addresses.Length==1,"NPC accessory configuration absent or ambiguous");
            var asset=g.ResolveAddress(addresses[0],114);var cfg=Json(asset.Object);var mounts=A(Json(template.Object),"mountconfigs").Where(m=>m.GetProperty("mountType").GetInt32()==acc.GetProperty("mountPoint").GetInt32()).ToArray();Validation.Require(mounts.Length==1,"NPC accessory mount absent or ambiguous");
            Validation.Require(cfg.GetProperty("attach").GetProperty("bUseRootOffset").GetInt32()==0,"NPC accessory root offset requires explicit conversion");
            var mount=mounts[0];Validation.Require(new[]{"x","y","z"}.All(k=>mount.GetProperty("attachOffset").GetProperty(k).GetDouble()==0)&&new[]{"bLockLocalPosition","bLocalLockRotation","bLocalLockScale"}.All(k=>mount.GetProperty(k).GetInt32()==0),"Accessory mount offset/locks require explicit conversion");
            var av=g.ResolveHash(cfg.GetProperty("avatarPathHash").GetInt64(),90);
            var slot=JsonSerializer.SerializeToNode(cfg)!;slot["name"]=name;slot["bUseSelfAvatar"]=1;slot["selfAvatarPathHash"]=cfg.GetProperty("avatarPathHash").GetInt64();slot["selfAvatarName"]=Json(av.Object).GetProperty("m_Name").GetString();slot["parentBoneTransformName"]=mount.GetProperty("avatarBonePath").GetString();slot["partSubMeshsLOD0"]=JsonNode.Parse(cfg.GetProperty("meshAssets").GetProperty("partSubMeshsLOD0").GetRawText());
            slots.Add(JsonSerializer.SerializeToElement(slot));selectedNames.Add(name);accessoryNames.Add(name);
            long prefabHash=cfg.GetProperty("accPathHash").GetInt64();if(!g.Manifest.Assets.Any(a=>a.Hash==prefabHash))diagnostics.Add("Accessory prefab hash "+prefabHash+" absent from manifest; using explicit config mesh/Avatar and template mount");
        }
        foreach(string name in selectedNames)
        {
            var matches=slots.Where(x=>x.GetProperty("name").GetString()==name).ToArray();Validation.Require(matches.Length==1,"Selected NPC slot absent or ambiguous: "+name);var slot=matches[0];
            bool self=slot.GetProperty("bUseSelfAvatar").GetInt32()!=0;long avatarHash=slot.GetProperty("selfAvatarPathHash").GetInt64();string attachment=slot.GetProperty("parentBoneTransformName").GetString()!;
            var selected=A(slot,"partSubMeshsLOD0");Validation.Require(selected.Length>0,"Selected NPC slot has no LOD0 meshes");
            var nativeMeshes=selected.Select(x=>g.ResolveHash(x.GetProperty("meshPathHash").GetInt64(),43)).ToArray();
            ResolvedAsset avatar;
            if(self) {Validation.Require(avatarHash!=0&&attachment.Length>0,"Self Avatar requires exact attachment");avatar=g.ResolveHash(avatarHash,90);Validation.Require(Json(avatar.Object).GetProperty("m_Name").GetString()==slot.GetProperty("selfAvatarName").GetString(),"Self Avatar name mismatch");}
            else
            {
                var candidatesAvatar=nativeMeshes.SelectMany(m=>m.Document.Objects.Where(o=>o.ClassId==90).Select(o=>new ResolvedAsset(m.Cab,m.Document,o))).DistinctBy(x=>(x.Cab,x.Object.Id)).ToArray();
                Validation.Require(candidatesAvatar.Length==1,"Common part meshes require one native FBX Avatar");avatar=candidatesAvatar[0];
            }
            var decoded=new List<SerializedObject>();
            for(int i=0;i<nativeMeshes.Length;i++)
            {
                var mesh=nativeMeshes[i];var node=JsonSerializer.SerializeToNode(mesh.Object.Data,WireJson.Options)!;
                string meshName=node["m_Name"]!.GetValue<string>();Validation.Require(meshName==selected[i].GetProperty("meshName").GetString(),"NPC mesh name/hash mismatch");
                var stream=node["m_StreamData"];if(stream is not null&&stream["size"]!.GetValue<long>()>0)node["m_VertexData"]!["m_DataSize"]=Convert.ToBase64String(g.ReadResource(mesh.Cab,stream["path"]!.GetValue<string>(),stream["offset"]!.GetValue<long>(),checked((int)stream["size"]!.GetValue<long>())));
                decoded.Add(new(mesh.Object.Id,43,node));identities.Add(meshName,mesh.Cab+":"+mesh.Object.Id);
                var hashes=A(selected[i],"materialPathHashes").Select(x=>x.GetInt64()).ToArray();
                // Authored renderer material codes select an exact native backup mapping.
                string renderer=meshName[..^5];var renderNames=selection.Source.GetProperty("renders").EnumerateArray().Select(x=>x.GetString()).ToArray();int codeIndex=Array.IndexOf(renderNames,renderer);
                if(codeIndex>=0&&hashes.Length==1) {int code=selection.Source.GetProperty("materialCodes")[codeIndex].GetInt32();var backups=selected[i].GetProperty("backupMaterialNameToPathHashes");var keys=A(backups,"m_Keys");var values=A(backups,"m_Values");Validation.Require(keys.Length==values.Length,"NPC material code arrays disagree");var mi=Enumerable.Range(0,keys.Length).Where(j=>keys[j].GetInt32()==code).ToArray();Validation.Require(mi.Length==1,"Authored NPC material code absent or ambiguous");hashes[0]=values[mi[0]].GetInt64();}
                materials.Add(meshName,hashes.Select(h=>g.ResolveHash(h,21)).ToArray());
            }
            var partScene=CharacterGeometry.Convert(new(avatar.Document.UnityVersion,[],[avatar.Object,..decoded]),name,allowAuxiliaryScale:true).Assets[0].Scene!;
            var mapping=Assemble(bones,partScene,self?attachment:null,name,out var output,out double error, accessoryNames.Contains(name)?"":null);meshes.AddRange(output);
            parts.Add(new(name,avatarHash,avatar.Cab,avatar.Object.Id,attachment,output.Select(m=>m.Name).ToArray(),mapping,error,slot.Clone(),nativeMeshes.Select((m,i)=>new NativeNpcMeshSource(selected[i].GetProperty("meshPathHash").GetInt64(),m.Cab,m.Object.Id,Json(m.Object).GetProperty("m_BindPose").Clone(),A(Json(m.Object),"m_BoneNameHashes").Select(h=>h.GetUInt32()).ToArray())).ToArray()));
        }
        scene=scene with {Name=selection.Id,Bones=bones.ToArray(),Meshes=meshes.ToArray()};
        scene=NativeCharacterImport.ApplyMaterials(g,scene,materials,identities);
        // Raw declaration remains in SRED even for explicitly diagnosed gameplay-only fields.
        scene=scene with {Npc=new(g.LogicalSource(candidates[0]),selection.Source,template.Cab,template.Object.Id,common.Cab,common.Object.Id,table.Cab,table.Object.Id,parts.ToArray(),diagnostics.ToArray())};
        scene=scene with {FaceDriver=NativeNpcMorph.Extract(g,selection,parts.ToArray(),scene)};
        scene=ApplyAuthoredNeutral(scene);
        if(selection.Scale!=1)
        {
            double[] Scale(double[] v)=>v.Select(x=>x*selection.Scale).ToArray();
            double[] Rest(double[] r){var v=(double[])r.Clone();v[3]*=selection.Scale;v[7]*=selection.Scale;v[11]*=selection.Scale;return v;}
            scene=scene with {Bones=scene.Bones.Select(b=>b with {Head=Scale(b.Head),Tail=Scale(b.Tail),RestMatrix=Rest(b.RestMatrix!)}).ToArray(),Meshes=scene.Meshes.Select(m=>m with {Positions=m.Positions.Select(Scale).ToArray(),Shapes=m.Shapes.Select(sh=>sh with {Offsets=sh.Offsets.Select(Scale).ToArray()}).ToArray()}).ToArray(),HeadReference=scene.HeadReference is {} head?head with {RestMatrix=Rest(head.RestMatrix)}:null,
                FaceDriver=scene.FaceDriver is {} face?face with {Bones=face.Bones.Select(b=>b with {Position=Scale(b.Position)}).ToArray(),Controls=face.Controls.Select(c=>c with {Bones=c.Bones.Select(d=>d with {Position=Scale(d.Position)}).ToArray()}).ToArray()}:null};
        }
        var database=new DatabaseDocument(g.Manifest.Version,[new("npc:"+selection.Id,selection.Id,"Native selected NPC parts, explicit morph declarations; authored scale "+selection.Scale,"character",[],scene)]);
        Validation.Database(database);return database;
    }
    /// <summary>Rebases native skin and rest together to the authored zero-control pose; source bind palettes stay in provenance.</summary>
    public static SceneDocument ApplyAuthoredNeutral(SceneDocument scene)
    {
        if(scene.FaceDriver is not {} face)return scene;
        var reflection=Matrix4x4.CreateScale(-1,1,1);var poses=NativeFaceMorph.Evaluate(face,new Dictionary<string,double>());
        var authored=face.Bones.Select((b,i)=>(b.SceneBone,Local:reflection*poses[i]*reflection)).ToDictionary(x=>x.SceneBone,x=>x.Local);
        var old=scene.Bones.Select(b=>M(b.RestMatrix!)).ToArray();var world=new Matrix4x4[old.Length];var shift=new Matrix4x4[old.Length];
        for(int i=0;i<world.Length;i++){int p=scene.Bones[i].Parent;Matrix4x4.Invert(p<0?Matrix4x4.Identity:old[p],out var pi);world[i]=authored.GetValueOrDefault(i,old[i]*pi)*(p<0?Matrix4x4.Identity:world[p]);Matrix4x4.Invert(old[i],out var inv);shift[i]=inv*world[i];}
        var bones=scene.Bones.Select((b,i)=>b with {Head=Values(Vector3.Transform(V(b.Head),shift[i])),Tail=Values(Vector3.Transform(V(b.Tail),shift[i])),RestMatrix=Values(world[i]),Roll=Roll(world[i])}).ToArray();
        var meshes=scene.Meshes.Select(mesh=>{
            var weights=mesh.Weights.GroupBy(w=>w.Vertex).ToDictionary(x=>x.Key,x=>x.ToArray());
            Vector3 Skin(int i,Vector3 v,bool direction){if(!weights.TryGetValue(i,out var ws))return v;var output=Vector3.Zero;foreach(var w in ws)output+=(direction?Vector3.TransformNormal(v,shift[w.Bone]):Vector3.Transform(v,shift[w.Bone]))*(float)w.Weight;return output;}
            return mesh with {Positions=mesh.Positions.Select((v,i)=>Values(Skin(i,V(v),false))).ToArray(),Normals=mesh.Normals.Select((v,i)=>Values(Vector3.Normalize(Skin(i,V(v),true)))).ToArray(),Tangents=mesh.Tangents?.Select((v,i)=>{var t=Vector3.Normalize(Skin(i,V(v),true));return new double[]{t.X,t.Y,t.Z,v[3]};}).ToArray(),Shapes=mesh.Shapes.Select(sh=>sh with {Offsets=sh.Offsets.Select((v,i)=>Values(Skin(i,V(v),true))).ToArray()}).ToArray()};
        }).ToArray();
        var changes=meshes.Select((m,i)=>new NativeNpcNeutralMeshChange(m.Name,m.Positions.Select((v,j)=>(double)Vector3.Distance(V(v),V(scene.Meshes[i].Positions[j]))).DefaultIfEmpty().Max())).ToArray();
        return scene with {Bones=bones,Meshes=meshes,Npc=scene.Npc is {} npc?npc with {PreAuthoredRestMatrices=scene.Bones.Select(b=>(double[])b.RestMatrix!.Clone()).ToArray(),NeutralMeshChanges=changes}:null,HeadReference=scene.HeadReference is {} head?head with {RestMatrix=bones[head.Bone].RestMatrix!}:null};
    }
    /// <summary>Joins only exact paths in the authoritative animation Avatar namespace.
    /// Independent part hashes retain their native value and cannot shadow a common body joint.</summary>
    public static Dictionary<uint,int> AnimationBindings(SceneDocument scene,IReadOnlyDictionary<uint,string> sourcePaths)
    {
        var result=new Dictionary<uint,int>();
        foreach(var pair in sourcePaths)
        {
            var matches=scene.Bones.Select((b,i)=>(b,i)).Where(x=>x.b.SourceHash==pair.Key&&x.b.SourcePath==pair.Value).ToArray();
            Validation.Require(matches.Length<=1,"Duplicate imported identity in animation Avatar namespace: "+pair.Value);
            if(matches.Length==1)result.Add(pair.Key,matches[0].i);
        }
        return result;
    }
    internal static Matrix4x4 M(double[] a)=>new((float)a[0],(float)a[4],(float)a[8],(float)a[12],(float)a[1],(float)a[5],(float)a[9],(float)a[13],(float)a[2],(float)a[6],(float)a[10],(float)a[14],(float)a[3],(float)a[7],(float)a[11],(float)a[15]);
    internal static double[] Values(Matrix4x4 m)=>[m.M11,m.M21,m.M31,m.M41,m.M12,m.M22,m.M32,m.M42,m.M13,m.M23,m.M33,m.M43,m.M14,m.M24,m.M34,m.M44];
    private static double Roll(Matrix4x4 rest)
    {
        Validation.Require(Matrix4x4.Decompose(rest,out _,out var rotation,out _),"Invalid assembled bone rest");
        var axis=Vector3.Normalize(Vector3.Transform(Vector3.UnitY,rotation));
        var shortest=axis.Y < -0.999999f?Quaternion.CreateFromAxisAngle(Vector3.UnitZ,MathF.PI):Quaternion.Normalize(new Quaternion(axis.Z,0,-axis.X,1+axis.Y));
        var twist=Quaternion.Normalize(Quaternion.Conjugate(shortest)*rotation);return Math.IEEERemainder(2*Math.Atan2(twist.Y,twist.W),2*Math.PI);
    }
    private static Vector3 V(double[] v)=>new((float)v[0],(float)v[1],(float)v[2]);
    private static double[] Values(Vector3 v)=>[v.X,v.Y,v.Z];
    public static NativeNpcBoneBinding[] Assemble(List<BoneRecord> target,SceneDocument source,string? attachment,string partName,out MeshRecord[] meshes,out double neutralError,string? anchorPath=null)
    {
        var map=Enumerable.Repeat(-1,source.Bones.Length).ToArray();
        var needed=source.Meshes.SelectMany(m=>m.Weights).Select(w=>w.Bone).ToHashSet();
        foreach(int joint in needed.ToArray())for(int p=source.Bones[joint].Parent;p>=0;p=source.Bones[p].Parent)needed.Add(p);var transform=Matrix4x4.Identity;int attach=-1,anchor=-1;
        if(attachment is not null)
        {
            var matches=target.Select((b,i)=>(b,i)).Where(x=>x.b.SourcePath==attachment).ToArray();Validation.Require(matches.Length==1,"NPC attachment path absent or ambiguous");attach=matches[0].i;
            string leaf=anchorPath??attachment.Split('/')[^1];var anchors=source.Bones.Select((b,i)=>(b,i)).Where(x=>x.b.SourcePath==leaf&&(anchorPath==""?x.b.Parent==-1:x.b.Parent==0)).ToArray();Validation.Require(anchors.Length==1,"Self Avatar attachment root absent or ambiguous");anchor=anchors[0].i;
            Validation.Require(Matrix4x4.Invert(M(source.Bones[anchor].RestMatrix!),out var inv),"Singular NPC part anchor");transform=anchorPath is null
                ? Matrix4x4.CreateTranslation(M(target[attach].RestMatrix!).Translation-M(source.Bones[anchor].RestMatrix!).Translation)
                : inv*M(target[attach].RestMatrix!);
        }
        var result=new List<NativeNpcBoneBinding>();
        for(int i=0;i<source.Bones.Length;i++)
        {
            var b=source.Bones[i];string path=b.SourcePath!;
            if(attachment is null && !needed.Contains(i) && !target.Any(t=>t.SourcePath==path))continue;string assembled=attachment is null?path:anchorPath is null?attachment+"/"+partName+(path.Length==0?"":"/"+path):i==anchor?attachment:(anchorPath==""||path.StartsWith(source.Bones[anchor].SourcePath+"/",StringComparison.Ordinal))?attachment+(anchorPath==""?"/"+path:path[source.Bones[anchor].SourcePath!.Length..]):"@"+partName+"/"+path;
            var matches=target.Select((x,j)=>(x,j)).Where(x=>x.x.SourcePath==assembled).ToArray();Validation.Require(matches.Length<=1,"Ambiguous assembled NPC path");
            if(matches.Length==1)map[i]=matches[0].j;
            else
            {
                var rest=M(b.RestMatrix!)*transform;
                if(attachment is null&&b.Parent>=0){Validation.Require(map[b.Parent]>=0&&Matrix4x4.Invert(M(source.Bones[b.Parent].RestMatrix!),out _),"Missing common parent rest");Matrix4x4.Invert(M(source.Bones[b.Parent].RestMatrix!),out var parentInverse);rest=M(b.RestMatrix!)*parentInverse*M(target[map[b.Parent]].RestMatrix!);}
                Matrix4x4.Invert(M(b.RestMatrix!),out var oldInverse);var shift=oldInverse*rest;
                var head=Vector3.Transform(V(b.Head),shift);var tail=Vector3.Transform(V(b.Tail),shift);
                string unique=b.Name;if(target.Any(x=>x.Name==unique))unique=partName+":"+unique;
                map[i]=target.Count;target.Add(b with {Name=unique,Parent=b.Parent<0?(attachment is not null&&anchorPath is null?attach:-1):map[b.Parent],Head=Values(head),Tail=Values(tail),RestMatrix=Values(rest),Roll=Roll(rest),SourcePath=assembled});
            }
            result.Add(new(i,path,b.SourceHash!.Value,map[i],assembled));
        }
        var skin=source.Bones.Select((b,i)=>{if(map[i]<0)return Matrix4x4.Identity;Validation.Require(Matrix4x4.Invert(M(b.RestMatrix!),out var inv),"Singular native part rest");return inv*M(target[map[i]].RestMatrix!);}).ToArray();
        meshes=source.Meshes.Select(mesh=> {
            var weights=mesh.Weights.GroupBy(w=>w.Vertex).ToDictionary(x=>x.Key,x=>x.ToArray());
            Vector3 Skin(int i,Vector3 v,bool normal){if(!weights.TryGetValue(i,out var ws))return normal?Vector3.TransformNormal(v,transform):Vector3.Transform(v,transform);var output=Vector3.Zero;foreach(var w in ws)output+=(normal?Vector3.TransformNormal(v,skin[w.Bone]):Vector3.Transform(v,skin[w.Bone]))*(float)w.Weight;return output;}
            return mesh with {Positions=mesh.Positions.Select((p,i)=>Values(Skin(i,V(p),false))).ToArray(),Normals=mesh.Normals.Select((n,i)=>Values(Vector3.Normalize(Skin(i,V(n),true)))).ToArray(),Weights=mesh.Weights.Select(w=>w with{Bone=map[w.Bone]}).ToArray(),Tangents=mesh.Tangents?.Select((t,i)=>{var v=Vector3.Normalize(Skin(i,V(t),true));return new double[]{v.X,v.Y,v.Z,t[3]};}).ToArray(),Shapes=mesh.Shapes.Select(s=>s with{Offsets=s.Offsets.Select((o,i)=>Values(Skin(i,V(o),true))).ToArray()}).ToArray()};
        }).ToArray();
        neutralError=attachment is null?0:anchorPath is null
            ? Vector3.Distance((M(source.Bones[anchor].RestMatrix!)*transform).Translation,M(target[attach].RestMatrix!).Translation)
            : Values(M(source.Bones[anchor].RestMatrix!)*transform).Zip(target[attach].RestMatrix!,(a,b)=>Math.Abs(a-b)).Max();
        Validation.Require(neutralError<0.00001,"Self Avatar attachment did not preserve common anchor");return result.ToArray();
    }
}
