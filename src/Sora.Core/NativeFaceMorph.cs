using System.Numerics;
using System.Text.Json;

namespace Sora.Core;

/// <summary>Native authored skeletal morph DTO extraction; no Blender dependency.</summary>
public static class NativeFaceMorph
{
    private static JsonElement Json(SerializedObject o) => JsonSerializer.SerializeToElement(o.Data, WireJson.Options);
    private static JsonElement[] Array(JsonElement o, string key) {
        var a=o.GetProperty(key).GetProperty("Array");
        Validation.Require(a.GetArrayLength()<=16384,"Face array exceeds limit"); return a.EnumerateArray().ToArray();
    }
    private static double[] Vec(JsonElement o,string key) => new[]{"x","y","z"}.Select(x=>o.GetProperty(key).GetProperty(x).GetDouble()).ToArray();

    public static FaceDriverRecord? Extract(GameResources resources, string prefabPath, ResolvedAsset avatar, SceneDocument scene)
    {
        string token=Path.GetFileName(prefabPath).Replace("_uimodel.prefab","",StringComparison.OrdinalIgnoreCase);
        // Manifest file naming narrows candidates; referenced Avatar identity is the authority.
        var assets=resources.Manifest.Assets.Where(a=> {
            string file=Path.GetFileNameWithoutExtension(a.Path); const string prefix="data_facemorph_avatar_";
            return file.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)&&(token.Equals(file[prefix.Length..],StringComparison.OrdinalIgnoreCase)||token.EndsWith("_"+file[prefix.Length..],StringComparison.OrdinalIgnoreCase));
        }).DistinctBy(a=>(a.Path,a.Bundle)).ToArray();
        if(assets.Length==0) return null;
        Validation.Require(assets.Length==1,"Ambiguous authored face avatar");
        var asset=assets[0]; var hosts=new List<(string Cab,SerializedObject Object,JsonElement Data)>();
        foreach(string cab in resources.LoadClosure(asset.Bundle))
            foreach(var obj in resources.GetDocument(cab).Objects.Where(o=>o.ClassId==114)) {
                var d=Json(obj);
                if(d.TryGetProperty("data",out var data)&&data.TryGetProperty("morphMappingCfgs",out _)) hosts.Add((cab,obj,d));
            }
        Validation.Require(hosts.Count==1,"Expected one authored face mapping host");
        var host=hosts[0]; var linked=resources.Resolve(host.Cab,host.Data.GetProperty("avatar"));
        Validation.Require(linked is not null&&linked.Object.ClassId==90,"Face reference is not an Avatar");
        var presets=new List<FacePresetRecord>();
        // UI and gameplay use distinct Avatar assets. Bone identity is joined by
        // exact native path/hash below, never by sharing an array index.
        var face=Decode(host.Data,Json(linked!.Object),scene,asset.Path,host.Cab,host.Object.Id,linked.Cab,linked.Object.Id);
        string? character=CharacterToken(token);
        if(character is not null)
        {
            string fileName="data_earmorph_avatar_"+character+".asset";
            var ears=resources.Manifest.Assets.Where(a=>a.Path.Contains("/skeletalmorphcfg/",StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(a.Path).Equals(fileName,StringComparison.OrdinalIgnoreCase)).DistinctBy(a=>(a.Path,a.Bundle)).ToArray();
            Validation.Require(ears.Length<=1,"Ambiguous character-specific ear mapping");
            if(ears.Length==1)
            {
                var earHost=MappingResource(resources,ears[0]);
                Validation.Require(earHost.Data.GetProperty("dataType").GetInt32()==1,"Character ear mapping has an unexpected native data type");
                var earAvatar=resources.Resolve(earHost.Cab,earHost.Data.GetProperty("avatar"));
                Validation.Require(earAvatar is not null&&earAvatar.Object.ClassId==90,"Character ear mapping requires an explicit Avatar reference");
                Validation.Require(earAvatar!.Cab.Equals(linked.Cab,StringComparison.OrdinalIgnoreCase)&&earAvatar.Object.Id==linked.Object.Id,"Character ear mapping belongs to a different Avatar");
                var ear=Decode(earHost.Data,Json(earAvatar!.Object),scene,ears[0].Path,earHost.Cab,earHost.Object.Id,earAvatar.Cab,earAvatar.Object.Id);
                face=MergeSupplementary(face,ear,scene);
            }
        }
        var vocabulary=face.Controls.Select(c=>c.Name).ToHashSet(StringComparer.Ordinal);
        foreach(var preset in resources.Manifest.Assets.Where(a=>a.Path.Contains("/skeletalmorphanim/pose/",StringComparison.Ordinal)||a.Path.Contains("/skeletalmorphanim/emotion/",StringComparison.Ordinal)).DistinctBy(a=>(a.Path,a.Bundle)))
            foreach(string cab in resources.LoadClosure(preset.Bundle))
                foreach(var obj in resources.GetDocument(cab).Objects.Where(o=>o.ClassId==114)) {
                    var d=Json(obj); if(!d.TryGetProperty("_pose",out var pose)) continue;
                    var weights=new Dictionary<string,double>(StringComparer.Ordinal);
                    foreach(string group in new[]{"_browValueL","_browValueR","_eyeValueL","_eyeValueR","_mouthValue","_otherValue"})
                        foreach(var w in Array(pose,group)) {
                            string name=w.GetProperty("_ctrlName").GetString()!;
                            Validation.Require(weights.TryAdd(name,w.GetProperty("_value").GetDouble()),"Duplicate preset control");
                        }
                    var additive=pose.GetProperty("_bIsAdditivePose");
                    presets.Add(new(d.GetProperty("m_Name").GetString()!,preset.Path,additive.ValueKind==JsonValueKind.True||(additive.ValueKind==JsonValueKind.Number&&additive.GetInt32()!=0),weights,weights.Keys.Where(k=>!vocabulary.Contains(k)).ToArray()));
                }
        face=face with {Presets=presets.ToArray()}; Validate(face,scene); return face;
    }

    private static string? CharacterToken(string stem)
    {
        if(!stem.StartsWith("chr_",StringComparison.OrdinalIgnoreCase))return null;
        int separator=stem.IndexOf('_',4);
        return separator>4&&stem.AsSpan(4,separator-4).ToString().All(char.IsAsciiDigit)&&separator+1<stem.Length?stem[(separator+1)..]:null;
    }

    private static (string Cab,SerializedObject Object,JsonElement Data) MappingResource(GameResources resources,AddressResource address)
    {
        var targets=new Dictionary<(string,long),ResolvedAsset>();
        foreach(string cab in resources.LoadClosure(address.Bundle))
            foreach(var container in resources.GetDocument(cab).Objects.Where(x=>x.ClassId==142))
                foreach(var row in Array(Json(container),"m_Container"))
                    if(row.GetProperty("first").GetString()==address.Path)
                    {
                        var target=resources.Resolve(cab,row.GetProperty("second").GetProperty("asset"));
                        Validation.Require(target is not null&&target.Object.ClassId==114,"Ear resource container does not identify a mapping host");
                        targets.TryAdd((target!.Cab,target.Object.Id),target);
                    }
        Validation.Require(targets.Count==1,"Character ear mapping target is absent or ambiguous");
        var source=targets.Values.Single();var data=Json(source.Object);
        Validation.Require(data.TryGetProperty("data",out var body)&&body.TryGetProperty("morphMappingCfgs",out _),"Ear resource is not a skeletal morph mapping");
        return(source.Cab,source.Object,data);
    }

    /// <summary>Appends one explicit same-Avatar ear table without changing the primary face identity.</summary>
    public static FaceDriverRecord MergeSupplementary(FaceDriverRecord primary,FaceDriverRecord supplement,SceneDocument scene)
    {
        Validate(primary,scene);Validate(supplement,scene);
        Validation.Require(supplement.AdditionalSources is null||supplement.AdditionalSources.Length==0,"Nested supplementary face sources are unsupported");
        Validation.Require(supplement.Presets.Length==0,"Supplementary mapping must not replace the primary preset collection");
        Validation.Require(primary.AvatarCab.Equals(supplement.AvatarCab,StringComparison.OrdinalIgnoreCase)&&primary.AvatarObject==supplement.AvatarObject,"Supplementary mapping belongs to a different Avatar");
        Validation.Require(primary.RotationOrder==supplement.RotationOrder&&primary.RotationSigns.SequenceEqual(supplement.RotationSigns),"Supplementary mapping has an incompatible rotation convention");
        var ids=primary.Bones.Select(x=>x.NativeId).ToHashSet();var sceneIds=primary.Bones.Select(x=>x.SceneBone).ToHashSet();var paths=primary.Bones.Select(x=>x.NativePath).ToHashSet(StringComparer.Ordinal);
        Validation.Require(supplement.Bones.All(x=>!ids.Contains(x.NativeId)&&!sceneIds.Contains(x.SceneBone)&&!paths.Contains(x.NativePath)),"Supplementary mapping overlaps existing face bone identities");
        int offset=primary.Bones.Length,controlStart=primary.Controls.Length;
        var controls=primary.Controls.Concat(supplement.Controls.Select(c=>c with{Bones=c.Bones.Select(d=>d with{Bone=checked(d.Bone+offset)}).ToArray()})).ToArray();
        var vocabulary=controls.Select(c=>c.Name).ToHashSet(StringComparer.Ordinal);
        var source=new FaceMappingSourceRecord(supplement.SourcePath,supplement.SourceCab,supplement.SourceObject,supplement.AvatarCab,supplement.AvatarObject,1,supplement.RestFitDegrees,offset,supplement.Bones.Length,controlStart,supplement.Controls.Length);
        var merged=primary with{Bones=primary.Bones.Concat(supplement.Bones).ToArray(),Controls=controls,AdditionalSources=[..(primary.AdditionalSources??[]),source],PrimaryBoneCount=primary.PrimaryBoneCount??primary.Bones.Length,PrimaryControlCount=primary.PrimaryControlCount??primary.Controls.Length,Presets=primary.Presets.Select(p=>p with{MissingControls=p.Weights.Keys.Where(k=>!vocabulary.Contains(k)).ToArray()}).ToArray()};
        Validate(merged,scene);return merged;
    }

    public static FaceDriverRecord Decode(JsonElement host,JsonElement avatar,SceneDocument scene,string path,string cab,long id,string avatarCab,long avatarId)
    {
        var data=host.GetProperty("data"); var definition=avatar.GetProperty("m_Avatar");
        var nameHashes=Array(definition,"m_SkeletonNameIDArray");
        var pathHashes=Array(definition.GetProperty("m_AvatarSkeleton").GetProperty("data"),"m_ID");
        var paths=Array(avatar,"m_TOS").ToDictionary(x=>x.GetProperty("first").GetUInt32(),x=>x.GetProperty("second").GetString()!);
        var bases=Array(data,"basePoseConfig"); var names=Array(data,"allBoneNames");
        Validation.Require(bases.Length>0&&bases.Length==names.Length,"Face base/name arrays disagree");
        var bones=new List<FaceBoneRecord>();
        foreach(var b in bases) {
            int native=b.GetProperty("boneID").GetInt32(), hash=b.GetProperty("boneNameHash").GetInt32();
            Validation.Require(native>=0&&native<nameHashes.Length&&native<pathHashes.Length,"Invalid native face bone index");
            Validation.Require(unchecked((int)nameHashes[native].GetUInt32())==hash,"Face bone hash mismatch");
            string bonePath=paths[pathHashes[native].GetUInt32()];
            Validation.Require(bonePath.Split('/')[^1]==names[bones.Count].GetString(),"Face bone name mismatch");
            var matches=scene.Bones.Select((bone,index)=>(bone,index)).Where(x=>x.bone.SourcePath==bonePath&&x.bone.SourceHash==pathHashes[native].GetUInt32()).ToArray();
            Validation.Require(matches.Length==1,"Face bone path is absent or ambiguous in imported rig");
            bones.Add(new(native,hash,bonePath,matches[0].index,Vec(b,"position"),Vec(b,"rotation"),Vec(b,"scale")));
        }
        var indexById=bones.Select((b,i)=>(b.NativeId,i)).ToDictionary(x=>x.NativeId,x=>x.i);
        var refs=Array(host.GetProperty("references"),"RefIds").ToDictionary(x=>x.GetProperty("rid").GetInt64());
        var controls=new List<FaceControlRecord>(); var controlNames=Array(data,"morphMappingNames"); var mappings=Array(data,"morphMappingCfgs");
        Validation.Require(controlNames.Length==mappings.Length,"Face control arrays disagree");
        foreach(var mapping in mappings) {
            var reference=refs[mapping.GetProperty("rid").GetInt64()]; var type=reference.GetProperty("type"); string cls=type.GetProperty("class").GetString()!;
            Validation.Require(type.GetProperty("ns").GetString()=="Beyond.Gameplay.Core"&&type.GetProperty("asm").GetString()=="Gameplay.Beyond"&&cls is "SkeletalMorphMappingData" or "SkeletalMorphShaderPropMappingData","Unexpected face mapping class");
            var d=reference.GetProperty("data"); var deltas=new List<FaceDeltaRecord>();
            foreach(var b in Array(d,"bones")) {
                int native=b.GetProperty("boneID").GetInt32(); Validation.Require(indexById.ContainsKey(native),"Mapping bone has no base"); int index=indexById[native];
                Validation.Require(b.GetProperty("boneNameHash").GetInt32()==bones[index].NameHash,"Mapping bone hash mismatch");
                deltas.Add(new(index,Vec(b,"position"),Vec(b,"rotation"),Vec(b,"scale")));
            }
            FaceShaderRecord? shader=null;
            if(cls=="SkeletalMorphShaderPropMappingData") {
                var r=refs[d.GetProperty("shaderParam").GetProperty("rid").GetInt64()];
                Validation.Require(r.GetProperty("type").GetProperty("class").GetString()=="SkMorphShaderParamFloat","Unsupported shader face parameter class");
                var s=r.GetProperty("data"); shader=new(s.GetProperty("_paramName").GetString()!,s.GetProperty("_rendererMask").GetInt32(),s.GetProperty("defaultValue").GetDouble(),s.GetProperty("blendMode").GetInt32(),d.GetProperty("vectorIndex").GetInt32());
            }
            controls.Add(new(controlNames[controls.Count].GetString()!,d.GetProperty("id").GetInt32(),d.GetProperty("nameHash").GetInt32(),d.GetProperty("tagHash").GetInt32(),d.GetProperty("partType").GetInt32(),deltas.ToArray(),shader));
        }
        var poses=Array(definition.GetProperty("m_DefaultPose").GetProperty("data"),"m_X");
        Validation.Require(bones.All(b=>b.NativeId<poses.Length),"Face bone has no native default pose");
        var fit=Fit(bones.ToArray(),bones.Select(b=>{var q=poses[b.NativeId].GetProperty("q"); return Quaternion.Normalize(new((float)q.GetProperty("x").GetDouble(),(float)q.GetProperty("y").GetDouble(),(float)q.GetProperty("z").GetDouble(),(float)q.GetProperty("w").GetDouble()));}).ToArray());
        return new(1,path,cab,id,avatarCab,avatarId,fit.Order,fit.Signs,fit.Error,bones.ToArray(),controls.ToArray(),[]);
    }

    public static Quaternion Rotation(double[] degrees,string order,int[] signs) {
        var q=Quaternion.Identity;
        foreach(char axis in order) {int i="xyz".IndexOf(axis); var v=i==0?Vector3.UnitX:i==1?Vector3.UnitY:Vector3.UnitZ; q=Quaternion.Multiply(q,Quaternion.CreateFromAxisAngle(v,(float)(degrees[i]*signs[i]*Math.PI/180)));}
        return Quaternion.Normalize(q);
    }
    public static (string Order,int[] Signs,double Error) Fit(FaceBoneRecord[] bones,Quaternion[] target) {
        (string Order,int[] Signs,double Error) best=("zyx",[1,-1,-1],double.PositiveInfinity);
        foreach(string order in new[]{"xyz","xzy","yxz","yzx","zxy","zyx"})
            for(int mask=0;mask<8;mask++) {int[] signs=Enumerable.Range(0,3).Select(i=>(mask&(1<<i))==0?1:-1).ToArray();
                double error=bones.Select((b,i)=>2*Math.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(Rotation(b.Rotation,order,signs),target[i])),0,1))*180/Math.PI).Average();
                if(error<best.Error) best=(order,signs,error);
            }
        return best;
    }
    public static Matrix4x4[] Evaluate(FaceDriverRecord face,IReadOnlyDictionary<string,double> weights) {
        Validation.Require(weights.Values.All(double.IsFinite),"Non-finite face weight");
        var p=face.Bones.Select(b=>(double[])b.Position.Clone()).ToArray(); var r=face.Bones.Select(b=>(double[])b.Rotation.Clone()).ToArray(); var s=face.Bones.Select(b=>(double[])b.Scale.Clone()).ToArray();
        foreach(var control in face.Controls) if(weights.TryGetValue(control.Name,out double w)) foreach(var d in control.Bones) for(int i=0;i<3;i++) {p[d.Bone][i]+=w*d.Position[i];r[d.Bone][i]+=w*d.Rotation[i];s[d.Bone][i]+=w*d.Scale[i];}
        return face.Bones.Select((b,i)=>Matrix4x4.CreateScale((float)s[i][0],(float)s[i][1],(float)s[i][2])*Matrix4x4.CreateFromQuaternion(Rotation(r[i],face.RotationOrder,face.RotationSigns))*Matrix4x4.CreateTranslation((float)p[i][0],(float)p[i][1],(float)p[i][2])).ToArray();
    }
    public static void Validate(FaceDriverRecord face,SceneDocument scene) {
        if(face is null||face.Bones is null||face.Controls is null||face.Presets is null||face.RotationSigns is null) throw new InvalidDataException("Missing face collections");
        bool Identity(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=4096&&!value.Any(char.IsControl);
        Validation.Require(Identity(face.SourcePath)&&Identity(face.SourceCab)&&Identity(face.AvatarCab),"Missing face provenance");
        Validation.Require(face.Version==1&&face.Bones.Length is >0 and <=4096&&face.Controls.Length<=4096&&face.Presets.Length<=4096,"Invalid face descriptor size/version");
        Validation.Require(new[]{"xyz","xzy","yxz","yzx","zxy","zyx"}.Contains(face.RotationOrder)&&face.RotationSigns.Length==3&&face.RotationSigns.All(x=>x is -1 or 1)&&double.IsFinite(face.RestFitDegrees)&&face.RestFitDegrees>=0,"Invalid face rotation convention");
        void Vector(double[] v) => Validation.Require(v is not null&&v.Length==3&&v.All(double.IsFinite),"Invalid face vector");
        var boneOwners=new int[face.Bones.Length];var controlOwners=new int[face.Controls.Length];
        var additional=face.AdditionalSources??[];Validation.Require(additional.Length<=32,"Too many supplementary face sources");
        var sourcePaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase){face.SourcePath};var sourceObjects=new HashSet<(string,long)>{(face.SourceCab.ToUpperInvariant(),face.SourceObject)};
        Validation.Require(additional.Length==0||(face.PrimaryBoneCount.HasValue&&face.PrimaryControlCount.HasValue),"Supplementary sources require declared primary ranges");
        Validation.Require(face.PrimaryBoneCount.HasValue==face.PrimaryControlCount.HasValue,"Primary face range declarations must be paired");
        int boneCursor=face.PrimaryBoneCount??face.Bones.Length,controlCursor=face.PrimaryControlCount??face.Controls.Length;
        Validation.Require(boneCursor>0&&boneCursor<=face.Bones.Length&&controlCursor>=0&&controlCursor<=face.Controls.Length,"Invalid supplementary source prefix");
        for(int i=0;i<additional.Length;i++)
        {
            var source=additional[i];Validation.Require(source is not null,"Null supplementary face source");
            Validation.Require(Identity(source!.SourcePath)&&Identity(source.SourceCab)&&Identity(source.AvatarCab)&&source.SourceObject!=0&&source.AvatarObject!=0,"Invalid supplementary source provenance");
            Validation.Require(sourcePaths.Add(source.SourcePath)&&sourceObjects.Add((source.SourceCab.ToUpperInvariant(),source.SourceObject)),"Duplicate supplementary mapping source");
            Validation.Require((source.SourceAvatarNull || (source.AvatarCab.Equals(face.AvatarCab,StringComparison.OrdinalIgnoreCase)&&source.AvatarObject==face.AvatarObject))&&source.DataType==1&&double.IsFinite(source.RestFitDegrees)&&source.RestFitDegrees>=0,"Incompatible supplementary Avatar or data type");
            Validation.Require(source.BoneStart==boneCursor&&source.BoneCount>0&&source.BoneCount<=face.Bones.Length-boneCursor&&source.ControlStart==controlCursor&&source.ControlCount>0&&source.ControlCount<=face.Controls.Length-controlCursor,"Invalid supplementary source ranges");
            for(int b=0;b<source.BoneCount;b++)boneOwners[boneCursor+b]=i+1;
            for(int c=0;c<source.ControlCount;c++)controlOwners[controlCursor+c]=i+1;
            boneCursor+=source.BoneCount;controlCursor+=source.ControlCount;
        }
        Validation.Require(boneCursor==face.Bones.Length&&controlCursor==face.Controls.Length,"Supplementary provenance does not cover appended records");
        var ids=new HashSet<int>();var sceneIds=new HashSet<int>();var paths=new HashSet<string>(StringComparer.Ordinal);var vocabulary=new HashSet<string>(StringComparer.Ordinal);var controlIds=new HashSet<(int,int)>();
        foreach(var b in face.Bones) {Validation.Require(b is not null,"Null face bone");Validation.Require(b!.NativeId>=0&&ids.Add(b.NativeId)&&sceneIds.Add(b.SceneBone)&&Identity(b.NativePath)&&paths.Add(b.NativePath)&&b.SceneBone>=0&&b.SceneBone<scene.Bones.Length&&scene.Bones[b.SceneBone].SourcePath==b.NativePath,"Invalid face bone relationship");Vector(b.Position);Vector(b.Rotation);Vector(b.Scale);}
        for(int index=0;index<face.Controls.Length;index++) {var c=face.Controls[index];Validation.Require(c is not null&&c.Bones is not null,"Null face control or deltas");Validation.Require(Identity(c!.Name)&&vocabulary.Add(c.Name)&&controlIds.Add((controlOwners[index],c.Id))&&c.Bones!.Length<=4096,"Invalid face control");var targets=new HashSet<int>();foreach(var d in c.Bones!) {Validation.Require(d is not null,"Null face delta");Validation.Require(d!.Bone>=0&&d.Bone<face.Bones.Length&&targets.Add(d.Bone)&&boneOwners[d.Bone]==controlOwners[index],"Invalid face delta bone or source ownership");Vector(d.Position);Vector(d.Rotation);Vector(d.Scale);}if(c.Shader is {} shader) Validation.Require(double.IsFinite(shader.DefaultValue)&&!string.IsNullOrEmpty(shader.Parameter),"Invalid face shader parameter");}
        Validation.Require(!(face.AdditionalSources??[]).Any(s=>s.SourceAvatarNull)||face.NativeSources is not null,"Shared NPC source requires retained native provenance");
        if(face.NativeSources is not null)NativeNpcMorph.ValidateSources(face,scene);
        foreach(var p in face.Presets) Validation.Require(p.Weights.Count<=4096&&p.Weights.Values.All(double.IsFinite)&&p.MissingControls.Order().SequenceEqual(p.Weights.Keys.Where(k=>!vocabulary.Contains(k)).Order()),"Invalid face preset");
    }
}
