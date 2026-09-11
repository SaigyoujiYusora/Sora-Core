using System.Numerics;
using System.Text.Json;

namespace Sora.Core;

public sealed record NativeAnimationScalarTrack(uint Path, int TypeId, int CustomType, uint Attribute, float SampleRate, float[] Values);
public sealed record NativeAnimationSource(string ResourcePath, string Cab, string PathId, string ManifestHash);
public sealed record NativeUnboundTransformTrack(int SourceTrack, uint Path, bool Position, bool Rotation, bool Scale, double[] Times, double[][] Translations, double[][] Rotations, double[][] Scales);
public sealed record NativeAnimationMetadata(NativeAnimationSource? Source, NativeAnimationScalarTrack[] CustomScalars, string[] Diagnostics, NativeUnboundTransformTrack[]? UnboundTransformTracks = null);
public sealed record NativeAnimationConversion(ClipRecord Clip, NativeAnimationScalarTrack[] CustomScalars, string[] Diagnostics, NativeAnimationSource? Source = null);
public sealed record NativeAnimationSelection(string Cab, string PathId);
public sealed record NativeAnimationSubclip(string ResourcePath, string Cab, string PathId, string Name);

/// <summary>Joins native generic/human channels to a converted rig using exact source identity.</summary>
public static class NativeAnimationClip
{
    public static NativeAnimationSubclip[] DiscoverSubclips(GameResources resources,string resourcePath,string? knownCab=null)
        => ResolveClips(resources,resourcePath,knownCab).Select(source=>new NativeAnimationSubclip(resourcePath,source.Cab,source.Object.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),JsonSerializer.SerializeToElement(source.Object.Data,WireJson.Options).GetProperty("m_Name").GetString()!)).OrderBy(x=>x.Name,StringComparer.Ordinal).ThenBy(x=>x.PathId,StringComparer.Ordinal).ToArray();

    public static NativeAnimationSubclip[] DiscoverControllerClips(GameResources resources,string characterPrefabPath)
    {
        Validation.Require(!string.IsNullOrWhiteSpace(characterPrefabPath)&&characterPrefabPath.EndsWith("_uimodel.prefab",StringComparison.OrdinalIgnoreCase),"Select an exact character UI prefab");
        var prefabs=resources.Manifest.Assets.Where(x=>x.Path==characterPrefabPath).DistinctBy(x=>(x.Path,x.Bundle)).ToArray();
        Validation.Require(prefabs.Length==1,"Character prefab identity is absent or ambiguous");
        var animators=resources.LoadClosure(prefabs[0].Bundle).SelectMany(cab=>resources.GetDocument(cab).Objects.Where(x=>x.ClassId==95).Select(x=>(cab,obj:x))).ToArray();
        Validation.Require(animators.Length==1,"Character prefab must have one Animator");
        var controller=resources.Resolve(animators[0].cab,JsonSerializer.SerializeToElement(animators[0].obj.Data,WireJson.Options).GetProperty("m_Controller"));
        if(controller is null)return [];
        Validation.Require(controller.Object.ClassId==91,"Unsupported native UI Animator controller type");
        var result=new Dictionary<(string,string,long),NativeAnimationSubclip>();var visited=new HashSet<(string,long)>();
        foreach(var pointer in NativeHumanoidRig.ArrayField(JsonSerializer.SerializeToElement(controller.Object.Data,WireJson.Options),"m_AnimationClips",4096))
        {
            var clip=resources.Resolve(controller.Cab,pointer);if(clip is null||!visited.Add((clip.Cab,clip.Object.Id)))continue;
            Validation.Require(clip.Object.ClassId==74,"Controller clip pointer has an unexpected native type");
            string name=JsonSerializer.SerializeToElement(clip.Object.Data,WireJson.Options).GetProperty("m_Name").GetString()!;
            foreach(var container in clip.Document.Objects.Where(x=>x.ClassId==142))
                foreach(var row in NativeHumanoidRig.ArrayField(JsonSerializer.SerializeToElement(container.Data,WireJson.Options),"m_Container",1000000))
                {
                    var reference=row.GetProperty("second").GetProperty("asset");if(reference.GetProperty("m_PathID").GetInt64()!=clip.Object.Id)continue;
                    var target=resources.Resolve(clip.Cab,reference);if(target is null||target.Cab!=clip.Cab||target.Object.Id!=clip.Object.Id)continue;
                    string path=row.GetProperty("first").GetString()!;Validation.Require(!string.IsNullOrWhiteSpace(path)&&path.Length<=4096,"Invalid native controller clip path");
                    result.TryAdd((path,clip.Cab,clip.Object.Id),new(path,clip.Cab,clip.Object.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),name));
                }
        }
        return result.Values.OrderBy(x=>x.ResourcePath,StringComparer.Ordinal).ThenBy(x=>x.Name,StringComparer.Ordinal).ThenBy(x=>x.PathId,StringComparer.Ordinal).ToArray();
    }

    private static ResolvedAsset[] ResolveClips(GameResources resources,string resourcePath,string? knownCab=null)
    {
        Validation.Require(!string.IsNullOrWhiteSpace(resourcePath)&&resourcePath.Length<=4096,"Select an exact animation resource path");
        var addresses=resources.Manifest.Assets.Where(x=>x.Path==resourcePath).DistinctBy(x=>(x.Path,x.Bundle)).ToArray();
        Validation.Require(addresses.Length<=1&&(addresses.Length==1||!string.IsNullOrWhiteSpace(knownCab)),"Select one exact native animation resource path");
        var clips=new Dictionary<(string,long),ResolvedAsset>();
        // Dependency-only UI clips need an explicit CAB already in a verified loaded closure.
        // The exact container path below remains mandatory; knownCab is not a substitute path.
        foreach(string cab in addresses.Length==1?resources.LoadClosure(addresses[0].Bundle):new[]{knownCab!})
            foreach(var container in resources.GetDocument(cab).Objects.Where(x=>x.ClassId==142))
                foreach(var row in NativeHumanoidRig.ArrayField(JsonSerializer.SerializeToElement(container.Data,WireJson.Options),"m_Container",1000000))
                    if(row.GetProperty("first").GetString()==resourcePath)
                    {
                        var target=resources.Resolve(cab,row.GetProperty("second").GetProperty("asset"));
                        if(target is not null&&target.Object.ClassId==74)clips.TryAdd((target.Cab,target.Object.Id),target);
                    }
        Validation.Require(clips.Count>0,"Exact resource has no AnimationClip container target");return clips.Values.ToArray();
    }

    public static NativeAnimationConversion Import(GameResources resources, string resourcePath, NativeHumanoidRig rig, SceneDocument scene, string? workerExecutable = null, NativeAnimationSelection? selection = null)
    {
        var candidates=ResolveClips(resources,resourcePath,selection?.Cab);
        if(selection is not null)
        {
            Validation.Require(long.TryParse(selection.PathId,System.Globalization.NumberStyles.AllowLeadingSign,System.Globalization.CultureInfo.InvariantCulture,out long pathId),"Invalid selected animation path ID");
            candidates=candidates.Where(x=>x.Cab==selection.Cab&&x.Object.Id==pathId).ToArray();
        }
        Validation.Require(candidates.Length==1,"Select one exact AnimationClip subasset by CAB and path ID");var source=candidates[0];
        var clip=JsonSerializer.SerializeToElement(source.Object.Data,WireJson.Options);var buffer=clip.GetProperty("m_AclCompressedBuffer");
        byte[] transforms=ConvertBytes(buffer,"TransformBufferData");var samples=AclCodec.Decode(transforms,ConvertBytes(buffer,"FloatBufferData"),workerExecutable);
        var rootSamples=AclCodec.Decode(transforms,ConvertBytes(buffer,"RootMotionBufferData"),workerExecutable);
        var result=Convert(clip.GetProperty("m_Name").GetString()!,new NativeAnimationBinding(clip,samples,rootSamples),rig,scene);
        var identity=new NativeAnimationSource(resourcePath,source.Cab,source.Object.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),resources.Manifest.Hash);
        var persisted=result.Clip with{Native=result.Clip.Native! with{Source=identity}};
        Validation.Scene(scene with{Clips=[persisted]});return result with{Source=identity,Clip=persisted};
    }

    public static NativeAnimationConversion Convert(string name, NativeAnimationBinding binding, NativeHumanoidRig rig, SceneDocument scene)
    {
        Validation.Scene(scene);
        Validation.Require(binding.RootScalarsValidated,"Native clip conversion requires verified root-buffer scalar agreement");
        var indexed = scene.Bones.Select((bone,index)=>(bone,index)).Where(x=>x.bone.SourceHash.HasValue).ToArray();
        Validation.Require(scene.Npc is not null || indexed.Select(x=>x.bone.SourceHash).Distinct().Count()==indexed.Length, "Imported rig has duplicate source hashes");
        var byHash = scene.Npc is not null?NativeNpcImport.AnimationBindings(scene,rig.SourcePaths):indexed.ToDictionary(x=>x.bone.SourceHash!.Value,x=>x.index);
        int Join(uint hash)
        {
            Validation.Require(rig.SourcePaths.TryGetValue(hash,out string? path) && byHash.TryGetValue(hash,out _), "Animation identity is absent from source or imported rig");
            int index=byHash[hash];Validation.Require(scene.Bones[index].SourcePath==path,"Animation hash and exact bone path disagree");return index;
        }
        var generic=binding.TransformPaths.Select(Join).ToArray();var genericSet=generic.ToHashSet();
        var checkedParents = new HashSet<int>();
        foreach (int animatedBone in generic)
            for (int index = animatedBone; index >= 0 && checkedParents.Add(index); index = scene.Bones[index].Parent)
            {
                var bone = scene.Bones[index];
                Validation.Require(bone.SourceHash.HasValue && rig.SourceParents.TryGetValue(bone.SourceHash.Value, out _)
                    && rig.SourcePaths.GetValueOrDefault(bone.SourceHash.Value) == bone.SourcePath,
                    "Generic animation ancestor has no authoritative native identity");
                uint? parentHash = rig.SourceParents[bone.SourceHash!.Value];
                Validation.Require(parentHash is null ? bone.Parent < 0 : bone.Parent >= 0
                    && scene.Bones[bone.Parent].SourceHash == parentHash
                    && scene.Bones[bone.Parent].SourcePath == rig.SourcePaths[parentHash.Value],
                    "Generic animation and imported native parents differ");
            }
        foreach(int human in rig.HumanNodes.Where(x=>x>=0))
        {
            var node=rig.Nodes[human];int index=Join(node.PathHash);
            if(node.Parent>=0)Validation.Require(scene.Bones[index].Parent>=0
                &&scene.Bones[scene.Bones[index].Parent].SourcePath==rig.Nodes[node.Parent].Path
                &&scene.Bones[scene.Bones[index].Parent].SourceHash==rig.Nodes[node.Parent].PathHash,
                "Human and imported immediate parent identities differ");
        }
        var f=Matrix4x4.CreateScale(-1,1,1);var c=f*Matrix4x4.CreateRotationX(MathF.PI/2);var inverseC=Inverse(c);
        var rest=scene.Bones.Select(x=>Matrix(x.RestMatrix??throw new InvalidDataException("Animation requires imported rest matrices"))).ToArray();
        var nativeRest=rest.Select(x=>f*x*inverseC).ToArray();
        var restLocal=rest.Select((x,i)=>x*(scene.Bones[i].Parent<0?Matrix4x4.Identity:Inverse(rest[scene.Bones[i].Parent]))).ToArray();
        var inverseRestLocal=restLocal.Select(Inverse).ToArray();
        var nativeLocal=nativeRest.Select((x,i)=>x*(scene.Bones[i].Parent<0?Matrix4x4.Identity:Inverse(nativeRest[scene.Bones[i].Parent]))).ToArray();
        var humanRoot=rig.Nodes[0];var expectedRoot=Matrix4x4.CreateFromQuaternion(humanRoot.Rotation)*Matrix4x4.CreateTranslation(humanRoot.Translation);
        Validation.Require(Difference(nativeRest[Join(humanRoot.PathHash)],expectedRoot)<0.001f,"Human root and imported native root frames differ");
        var zero=binding.HasHumanoid?NativeHumanoidPose.Evaluate(rig,binding.HumanFrame(0)):null;var animated=generic.ToHashSet();
        foreach(var body in zero?.Body??[])
        {
            int index=Join(body.PathHash);Validation.Require(!genericSet.Contains(index),"Generic and humanoid channels target the same bone");animated.Add(index);
            Validation.Require(Vector3.Distance(nativeLocal[index].Translation,rig.Nodes[rig.HumanNodes[body.HumanSlot]].Translation)<0.001f,"Imported skin and human rest translations differ");
        }
        int hips=zero is null?-1:Join(zero.HipsHash);if(hips>=0){Validation.Require(!genericSet.Contains(hips),"Generic and humanoid root channels overlap");animated.Add(hips);}else Validation.Require(binding.TransformPaths[0]==humanRoot.PathHash,"Generic root track does not match source root identity");
        Validation.Require((long)animated.Count*3*binding.Samples<=10_000_000,"Combined animation key limit exceeded");
        var keys=animated.SelectMany(bone=>new[]{"location","rotation","scale"}.Select(channel=>(bone,channel))).ToDictionary(x=>x,x=>new List<KeyRecord>(binding.Samples));
        var previous=new Dictionary<int,Quaternion>();
        for(int sample=0;sample<binding.Samples;sample++)
        {
            var human=binding.HasHumanoid?NativeHumanoidPose.Evaluate(rig,binding.HumanFrame(sample)):null;var local=(Matrix4x4[])nativeLocal.Clone();
            for(int i=0;i<generic.Length;i++)local[generic[i]]=binding.TransformLocal(sample,i,nativeLocal[generic[i]]);
            foreach(var body in human?.Body??[]){int bone=Join(body.PathHash);local[bone]=Matrix4x4.CreateFromQuaternion(body.Rotation)*Matrix4x4.CreateTranslation(local[bone].Translation);}
            var world=new Matrix4x4[scene.Bones.Length];var converted=new Matrix4x4[scene.Bones.Length];double time=sample/(double)binding.SampleRate;
            for(int bone=0;bone<world.Length;bone++)
            {
                int parent=scene.Bones[bone].Parent;
                world[bone]=bone==hips?Matrix4x4.CreateFromQuaternion(human!.HipsRotation)*Matrix4x4.CreateTranslation(human.HipsTranslation):local[bone]*(parent<0?Matrix4x4.Identity:world[parent]);
                converted[bone]=f*world[bone]*c;if(!animated.Contains(bone))continue;
                var targetLocal=converted[bone]*(parent<0?Matrix4x4.Identity:Inverse(converted[parent]));var basis=targetLocal*inverseRestLocal[bone];
                Validation.Require(Matrix4x4.Decompose(basis,out var scale,out var rotation,out var location),"Animation pose cannot be represented by TRS");rotation=Quaternion.Normalize(rotation);
                var rebuilt=Matrix4x4.CreateScale(scale)*Matrix4x4.CreateFromQuaternion(rotation)*Matrix4x4.CreateTranslation(location)*restLocal[bone];
                Validation.Require(Difference(targetLocal,rebuilt)<0.0001f,"Animation pose contains unsupported shear");
                if(previous.TryGetValue(bone,out var q)&&Quaternion.Dot(q,rotation)<0)rotation=new(-rotation.X,-rotation.Y,-rotation.Z,-rotation.W);previous[bone]=rotation;
                keys[(bone,"location")].Add(new(time,[location.X,location.Y,location.Z]));keys[(bone,"rotation")].Add(new(time,[rotation.X,rotation.Y,rotation.Z,rotation.W]));keys[(bone,"scale")].Add(new(time,[scale.X,scale.Y,scale.Z]));
            }
        }
        var clip=new ClipRecord(name,(binding.Samples-1)/(double)binding.SampleRate,binding.SampleRate,keys.OrderBy(x=>x.Key.bone).ThenBy(x=>x.Key.channel,StringComparer.Ordinal).Select(x=>new TrackRecord(x.Key.bone,x.Key.channel,x.Value.ToArray())).ToArray());
        var custom=binding.CustomScalarAttributes.Select(attribute=>new NativeAnimationScalarTrack(0,95,0,attribute,binding.SampleRate,Enumerable.Range(0,binding.Samples).Select(sample=>binding.ScalarValue(sample,attribute)).ToArray())).ToArray();
        var diagnostics=new List<string>();if(custom.Length>0)diagnostics.Add("Custom Animator scalar properties are preserved without mapping them to bones.");
        if(binding.RootComparisonMaximumError>0)diagnostics.Add("Independent root-stream quantization differs by at most "+binding.RootComparisonMaximumError.ToString("G9",System.Globalization.CultureInfo.InvariantCulture)+"; primary scalar values are retained.");
        var messages=diagnostics.ToArray();clip=clip with{Native=new(null,custom,messages)};Validation.Scene(scene with{Clips=[clip]});
        return new(clip,custom,messages);
    }
    private static Matrix4x4 Inverse(Matrix4x4 x){Validation.Require(Matrix4x4.Invert(x,out var inverse),"Singular animation rest matrix");return inverse;}
    private static byte[] ConvertBytes(JsonElement parent,string field)
    {
        string value=parent.GetProperty(field).GetProperty("Array").GetString()??throw new InvalidDataException("Missing animation buffer");
        Validation.Require(value.Length<=48*1024*1024,"Animation buffer exceeds limit");return System.Convert.FromBase64String(value);
    }
    private static Matrix4x4 Matrix(double[] a){Validation.Require(a.Length==16,"Invalid animation rest matrix");return new((float)a[0],(float)a[4],(float)a[8],(float)a[12],(float)a[1],(float)a[5],(float)a[9],(float)a[13],(float)a[2],(float)a[6],(float)a[10],(float)a[14],(float)a[3],(float)a[7],(float)a[11],(float)a[15]);}
    private static float Difference(Matrix4x4 a,Matrix4x4 b)=>new[]{Math.Abs(a.M11-b.M11),Math.Abs(a.M12-b.M12),Math.Abs(a.M13-b.M13),Math.Abs(a.M21-b.M21),Math.Abs(a.M22-b.M22),Math.Abs(a.M23-b.M23),Math.Abs(a.M31-b.M31),Math.Abs(a.M32-b.M32),Math.Abs(a.M33-b.M33),Math.Abs(a.M41-b.M41),Math.Abs(a.M42-b.M42),Math.Abs(a.M43-b.M43)}.Max();
}
