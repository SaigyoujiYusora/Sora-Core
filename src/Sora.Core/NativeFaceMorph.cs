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
        if(face.Bones is null||face.Controls is null||face.Presets is null||face.RotationSigns is null) throw new InvalidDataException("Missing face collections");
        Validation.Require(!string.IsNullOrWhiteSpace(face.SourcePath)&&!string.IsNullOrWhiteSpace(face.SourceCab)&&!string.IsNullOrWhiteSpace(face.AvatarCab),"Missing face provenance");
        Validation.Require(face.Version==1&&face.Bones.Length is >0 and <=4096&&face.Controls.Length<=4096&&face.Presets.Length<=4096,"Invalid face descriptor size/version");
        Validation.Require(new[]{"xyz","xzy","yxz","yzx","zxy","zyx"}.Contains(face.RotationOrder)&&face.RotationSigns.Length==3&&face.RotationSigns.All(x=>x is -1 or 1)&&double.IsFinite(face.RestFitDegrees),"Invalid face rotation convention");
        void Vector(double[] v) => Validation.Require(v is not null&&v.Length==3&&v.All(double.IsFinite),"Invalid face vector");
        var ids=new HashSet<int>();var sceneIds=new HashSet<int>();var vocabulary=new HashSet<string>(StringComparer.Ordinal);
        foreach(var b in face.Bones) {Validation.Require(ids.Add(b.NativeId)&&sceneIds.Add(b.SceneBone)&&b.SceneBone>=0&&b.SceneBone<scene.Bones.Length&&scene.Bones[b.SceneBone].SourcePath==b.NativePath,"Invalid face bone relationship");Vector(b.Position);Vector(b.Rotation);Vector(b.Scale);}
        foreach(var c in face.Controls) {Validation.Require(!string.IsNullOrEmpty(c.Name)&&vocabulary.Add(c.Name)&&c.Bones.Length<=4096,"Invalid face control");foreach(var d in c.Bones) {Validation.Require(d.Bone>=0&&d.Bone<face.Bones.Length,"Invalid face delta bone");Vector(d.Position);Vector(d.Rotation);Vector(d.Scale);}if(c.Shader is {} shader) Validation.Require(double.IsFinite(shader.DefaultValue)&&!string.IsNullOrEmpty(shader.Parameter),"Invalid face shader parameter");}
        foreach(var p in face.Presets) Validation.Require(p.Weights.Count<=4096&&p.Weights.Values.All(double.IsFinite)&&p.MissingControls.Order().SequenceEqual(p.Weights.Keys.Where(k=>!vocabulary.Contains(k)).Order()),"Invalid face preset");
    }
}
