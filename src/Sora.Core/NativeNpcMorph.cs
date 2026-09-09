using System.Text.Json;
using System.Text.Json.Nodes;
namespace Sora.Core;
public sealed record NativeNpcMorphBone(int CfgId,int NameHash,string Name,string Status,string? AvatarCab,long? AvatarObject,int? AvatarNodeIndex,string? NativePath,uint? PathHash,int? SceneBone,string? AssembledPath);
public sealed record NativeNpcMorphSource(string Declaration,string ResourcePath,string SourceCab,long SourceObject,bool SourceAvatarNull,JsonElement NativeHost,NativeNpcMorphBone[] Bones);
public static class NativeNpcMorph
{
    public static void ValidateSources(FaceDriverRecord face,SceneDocument scene)
    {
        var sources=face.NativeSources!;Validation.Require(sources.Length is >0 and <=32,"Invalid NPC morph source count");
        var identities=new HashSet<(string,long)>();
        foreach(var source in sources)
        {
            Validation.Require(source is not null&&!string.IsNullOrEmpty(source.SourceCab)&&identities.Add((source.SourceCab.ToUpperInvariant(),source.SourceObject))&&source.NativeHost.ValueKind==JsonValueKind.Object&&source.NativeHost.GetRawText().Length<=16*1024*1024,"Invalid NPC morph source");
            Validation.Require(source!.SourceAvatarNull==(source.NativeHost.GetProperty("avatar").GetProperty("m_PathID").GetInt64()==0),"NPC morph null Avatar provenance mismatch");
            var bases=NativeNpcImport.A(source.NativeHost.GetProperty("data"),"basePoseConfig");var names=NativeNpcImport.A(source.NativeHost.GetProperty("data"),"allBoneNames");
            Validation.Require(source.Bones is not null&&source.Bones.Length==bases.Length&&names.Length==bases.Length&&bases.Length<=4096,"NPC source records must cover every authored base");
            var projected=new List<NativeNpcMorphBone>();
            for(int i=0;i<bases.Length;i++)
            {
                var b=source.Bones![i];Validation.Require(b is not null&&b.CfgId==bases[i].GetProperty("boneID").GetInt32()&&b.NameHash==bases[i].GetProperty("boneNameHash").GetInt32()&&b.Name==names[i].GetString(),"NPC CFG source record mismatch");
                if(b!.Status=="selected")
                {
                    Validation.Require(b.AvatarNodeIndex>=0&&b.AvatarObject is not null&&b.AvatarObject!=0&&!string.IsNullOrEmpty(b.AvatarCab)&&b.NativePath is not null&&b.NativePath.Split('/')[^1]==b.Name&&b.PathHash.HasValue&&b.SceneBone>=0&&b.SceneBone<scene.Bones.Length&&scene.Bones[b.SceneBone!.Value].SourcePath==b.AssembledPath,"Invalid selected NPC morph identity");var matches=(scene.Npc?.Parts??[]).Where(p=>string.Equals(p.AvatarCab,b.AvatarCab,StringComparison.OrdinalIgnoreCase)&&p.AvatarObject==b.AvatarObject).SelectMany(p=>p.Bindings).Where(binding=>binding.AvatarNodeIndex==b.AvatarNodeIndex).ToArray();
                    Validation.Require(matches.Length==1&&matches[0].NativePath==b.NativePath&&matches[0].NativePathHash==b.PathHash&&matches[0].SceneBone==b.SceneBone&&matches[0].AssembledPath==b.AssembledPath,"NPC morph identity disagrees with selected part binding");projected.Add(b);
                }
                else Validation.Require(b.Status=="not-applicable-to-selected-parts"&&b.AvatarNodeIndex is null&&b.SceneBone is null&&b.AvatarCab is null&&b.AvatarObject is null&&b.NativePath is null&&b.PathHash is null&&b.AssembledPath is null,"Invalid nonapplicable NPC morph record");
            }
            var additional=(face.AdditionalSources??[]).SingleOrDefault(s=>string.Equals(s.SourceCab,source.SourceCab,StringComparison.OrdinalIgnoreCase)&&s.SourceObject==source.SourceObject);
            bool primary=string.Equals(face.SourceCab,source.SourceCab,StringComparison.OrdinalIgnoreCase)&&face.SourceObject==source.SourceObject;
            Validation.Require(primary||additional is not null,"NPC source has no projected owner");int start=primary?0:additional!.BoneStart;int count=primary?(face.PrimaryBoneCount??face.Bones.Length):additional!.BoneCount;
            Validation.Require(projected.Count==count,"NPC projected bone count disagrees with retained source");
            for(int i=0;i<count;i++){var b=face.Bones[start+i];var original=projected[i];Validation.Require(b.NativeId==original.CfgId&&b.NameHash==original.NameHash&&b.AvatarNodeIndex==original.AvatarNodeIndex&&b.SceneBone==original.SceneBone&&b.NativePath==original.AssembledPath,"NPC projection lost its CFG/Avatar namespace");}
            if(additional is not null)Validation.Require(additional.SourceAvatarNull==source.SourceAvatarNull,"NPC supplementary null Avatar flag mismatch");
        }
        Validation.Require(sources.Length==1+(face.AdditionalSources?.Length??0),"NPC source provenance coverage is incomplete");
    }
    public static FaceDriverRecord? Extract(GameResources g,NativeNpcDeclaration declaration,NativeNpcPartRecord[] parts,SceneDocument scene)
    {
        FaceDriverRecord? result=null;var sources=new List<NativeNpcMorphSource>();
        foreach(var selected in new[]{(declaration.Face,"face"),(declaration.Ear,"ear")})
        {
            if(selected.Item1 is null)continue;
            var address=NativeNpcImport.DeclaredResource(g,selected.Item1,selected.Item2);var resource=g.ResolveAddress(address,114);var host=NativeNpcImport.Json(resource.Object);
            var linked=g.Resolve(resource.Cab,host.GetProperty("avatar"));
            var candidates=parts.Select(p=>(Part:p,Avatar:new ResolvedAsset(p.AvatarCab,g.GetDocument(p.AvatarCab),g.GetDocument(p.AvatarCab).Objects.Single(o=>o.Id==p.AvatarObject)))).ToArray();
            if(linked is null)candidates=candidates.Where(p=>p.Part.AvatarHash!=0).ToArray();
            if(linked is not null)candidates=candidates.Where(p=>p.Avatar.Cab==linked.Cab&&p.Avatar.Object.Id==linked.Object.Id).ToArray();
            Validation.Require(candidates.Length>0,"Declared morph Avatar is absent from selected NPC parts");
            var data=host.GetProperty("data");var bases=NativeNpcImport.A(data,"basePoseConfig");var names=NativeNpcImport.A(data,"allBoneNames");Validation.Require(bases.Length==names.Length,"NPC morph base/name arrays disagree");
            var records=new List<NativeNpcMorphBone>();
            for(int i=0;i<bases.Length;i++)
            {
                var b=bases[i];int cfg=b.GetProperty("boneID").GetInt32(),hash=b.GetProperty("boneNameHash").GetInt32();string name=names[i].GetString()!;
                var matches=candidates.SelectMany(p=> {
                    var av=NativeNpcImport.Json(p.Avatar.Object);var nh=NativeNpcImport.A(av.GetProperty("m_Avatar"),"m_SkeletonNameIDArray");
                    return p.Part.Bindings.Where(bind=>bind.AvatarNodeIndex<nh.Length&&unchecked((int)nh[bind.AvatarNodeIndex].GetUInt32())==hash&&bind.NativePath.Split('/')[^1]==name).Select(bind=>(p.Avatar,Binding:bind));
                }).ToArray();
                Validation.Require(matches.Length<=1,"NPC morph identity is ambiguous: "+name);
                records.Add(matches.Length==0?new(cfg,hash,name,"not-applicable-to-selected-parts",null,null,null,null,null,null,null):new(cfg,hash,name,"selected",matches[0].Avatar.Cab,matches[0].Avatar.Object.Id,matches[0].Binding.AvatarNodeIndex,matches[0].Binding.NativePath,matches[0].Binding.NativePathHash,matches[0].Binding.SceneBone,matches[0].Binding.AssembledPath));
            }
            sources.Add(new(selected.Item1,address.Path,resource.Cab,resource.Object.Id,linked is null,host.Clone(),records.ToArray()));
            var applicable=records.Where(r=>r.Status=="selected").ToArray();Validation.Require(applicable.Length>0,"Declared NPC morph has no verified selected bones");
            Validation.Require(applicable.Select(r=>(r.AvatarCab,r.AvatarObject)).Distinct().Count()==1,"Morph projection across multiple part Avatars requires separate source ranges");
            var chosen=candidates.Single(p=>p.Avatar.Cab==applicable[0].AvatarCab&&p.Avatar.Object.Id==applicable[0].AvatarObject).Avatar;
            // Decode a verified projection while retaining the unmodified CFG namespace above.
            var projected=JsonNode.Parse(host.GetRawText())!;var selectedByCfg=applicable.ToDictionary(r=>r.CfgId);
            var baseRows=new JsonArray();var baseNames=new JsonArray();
            for(int i=0;i<bases.Length;i++)if(selectedByCfg.TryGetValue(bases[i].GetProperty("boneID").GetInt32(),out var binding)){var row=JsonNode.Parse(bases[i].GetRawText())!;row["boneID"]=binding.AvatarNodeIndex;baseRows.Add(row);baseNames.Add(names[i].GetString());}
            projected["data"]!["basePoseConfig"]!["Array"]=baseRows;projected["data"]!["allBoneNames"]!["Array"]=baseNames;
            var refs=projected["references"]!["RefIds"]!["Array"]!.AsArray().ToDictionary(n=>n!["rid"]!.GetValue<long>(),n=>n!);
            var keptMappings=new JsonArray();var keptNames=new JsonArray();var mappings=NativeNpcImport.A(data,"morphMappingCfgs");var controlNames=NativeNpcImport.A(data,"morphMappingNames");
            for(int i=0;i<mappings.Length;i++)
            {
                var reference=refs[mappings[i].GetProperty("rid").GetInt64()];var ds=reference["data"]!["bones"]!["Array"]!.AsArray();var kept=new JsonArray();
                foreach(var d in ds){Validation.Require(records.Any(r=>r.CfgId==d!["boneID"]!.GetValue<int>()&&r.NameHash==d["boneNameHash"]!.GetValue<int>()),"Morph delta has no matching authored base");}
                foreach(var d in ds)if(selectedByCfg.TryGetValue(d!["boneID"]!.GetValue<int>(),out var binding)){Validation.Require(d["boneNameHash"]!.GetValue<int>()==binding.NameHash,"NPC morph delta hash mismatch");var row=d.DeepClone();row["boneID"]=binding.AvatarNodeIndex;kept.Add(row);}
                bool shader=reference["type"]!["class"]!.GetValue<string>()=="SkeletalMorphShaderPropMappingData";
                if(kept.Count==0&&!shader)continue;
                reference["data"]!["bones"]!["Array"]=kept;keptMappings.Add(JsonNode.Parse(mappings[i].GetRawText()));keptNames.Add(controlNames[i].GetString());
            }
            projected["data"]!["morphMappingCfgs"]!["Array"]=keptMappings;projected["data"]!["morphMappingNames"]!["Array"]=keptNames;
            var localBones=scene.Bones.ToArray();foreach(var r in applicable)localBones[r.SceneBone!.Value]=localBones[r.SceneBone.Value] with {SourcePath=r.NativePath,SourceHash=r.PathHash};
            var face=NativeFaceMorph.Decode(JsonSerializer.SerializeToElement(projected),NativeNpcImport.Json(chosen.Object),scene with {Bones=localBones},address.Path,resource.Cab,resource.Object.Id,chosen.Cab,chosen.Object.Id);
            var byNode=applicable.ToDictionary(r=>r.AvatarNodeIndex!.Value);
            face=face with {Bones=face.Bones.Select(b=>b with {NativeId=byNode[b.NativeId].CfgId,AvatarNodeIndex=b.NativeId,NativePath=byNode[b.NativeId].AssembledPath!}).ToArray()};
            if(result is null)result=face;
            else
            {
                Validation.Require(result.RotationOrder==face.RotationOrder&&result.RotationSigns.SequenceEqual(face.RotationSigns),"NPC morph rotation conventions disagree");
                int bs=result.Bones.Length,cs=result.Controls.Length;
                var source=new FaceMappingSourceRecord(face.SourcePath,face.SourceCab,face.SourceObject,face.AvatarCab,face.AvatarObject,1,face.RestFitDegrees,bs,face.Bones.Length,cs,face.Controls.Length,linked is null);
                result=result with {PrimaryBoneCount=result.PrimaryBoneCount??bs,PrimaryControlCount=result.PrimaryControlCount??cs,Bones=[..result.Bones,..face.Bones],Controls=[..result.Controls,..face.Controls.Select(c=>c with {Bones=c.Bones.Select(b=>b with {Bone=b.Bone+bs}).ToArray()})],AdditionalSources=[..result.AdditionalSources??[],source]};
            }
        }
        if(result is not null){result=result with {NativeSources=sources.ToArray()};NativeFaceMorph.Validate(result,scene);}return result;
    }
}
