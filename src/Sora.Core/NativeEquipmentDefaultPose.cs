using System.Numerics;
using System.Text;
using System.Text.Json;
namespace Sora.Core;

public sealed record NativeEquipmentDefaultPose(string AnimatorId,string ControllerId,int StateIndex,uint StateNameHash,string ClipId,string ClipName,double Time,NativeEquipmentPoseBone[] Bones,string ResourceId,string ResourcePath,string AnimatorSourcePath,string Status="native-controller-default-at-zero",string Scope="initial-pose-only; controller transitions and event playback are not evaluated");
public sealed record NativeEquipmentPoseBone(int Bone,string SourcePath,double[] BasisMatrix);

/// <summary>Bounded initial generic pose reader. Reads the default state's exact clip pointer, never selects by clip name.</summary>
public static class NativeEquipmentDefaultPoseReader
{
    static JsonElement[] A(JsonElement p,string k){var a=p.GetProperty(k).GetProperty("Array");Validation.Require(a.ValueKind==JsonValueKind.Array&&a.GetArrayLength()<=16384,"Initial equipment array bound exceeded: "+k);return a.EnumerateArray().ToArray();}
    static JsonElement J(ResolvedAsset a)=>JsonSerializer.SerializeToElement(a.Object.Data,WireJson.Options);
    public static uint PathHash(string value) {uint crc=uint.MaxValue;foreach(byte b in Encoding.UTF8.GetBytes(value)){crc^=b;for(int i=0;i<8;i++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0);}return ~crc;}
    public static NativeEquipmentDefaultPose? Read(GameResources game,NativeHierarchyNode[] nodes,SceneDocument scene,string resourceId,string resourcePath)
    {
        Validation.Require(nodes.Length<=16384&&!string.IsNullOrWhiteSpace(resourceId)&&!string.IsNullOrWhiteSpace(resourcePath),"Missing initial resource identity or excessive hierarchy");
        var animators=new List<(NativeHierarchyNode Node,ResolvedAsset Animator,ResolvedAsset Controller)>();
        foreach(var n in nodes)foreach(var p in A(J(n.GameObject),"m_Component")) {
            var a=game.Resolve(n.GameObject.Cab,p.GetProperty("component"));if(a?.Object.ClassId!=95)continue;
            var c=game.Resolve(a.Cab,J(a).GetProperty("m_Controller"));if(c is not null)animators.Add((n,a,c));
        }
        if(animators.Count==0||scene.Bones.Length==0)return null;
        Validation.Require(animators.Count==1,"Initial equipment pose requires one Animator");var entry=animators[0];
        Validation.Require(entry.Controller.Object.ClassId==91,"Initial equipment pose does not yet evaluate override controllers");
        var document=J(entry.Controller);var graph=document.GetProperty("m_Controller");
        // A MeshSpace=true layer is only inert for this direct-clip initial pose when the resolved
        // Avatar is positively nonhuman (native Human skeleton absent/empty). Transform-only bindings
        // are verified below on the exact default clip before the flag is accepted.
        bool meshSpaceLayer=graph.GetProperty("m_LayerArray").GetProperty("Array")[0].GetProperty("data")
            .GetProperty("m_MeshSpace").GetBoolean();
        if(meshSpaceLayer)
        {
            var avatar=game.Resolve(entry.Animator.Cab,J(entry.Animator).GetProperty("m_Avatar"));
            var human=NativeEquipmentInitialController.AvatarHumanSkeleton(avatar);
            Validation.Require(human.Resolved&&human.Nonhuman,
                "Initial equipment pose MeshSpace layer requires a resolved nonhuman Avatar: "+human.Status);
        }
        NativeEquipmentInitialController.Validate(document,meshSpaceLayer);
        Validation.Require(A(graph,"m_LayerArray").Length==1,"Initial equipment pose requires one controller layer");
        var machines=A(graph,"m_StateMachineArray");Validation.Require(machines.Length==1,"Initial equipment pose requires one state machine");
        var machine=machines[0].GetProperty("data");int state=machine.GetProperty("m_DefaultState").GetInt32();var states=A(machine,"m_StateConstantArray");
        Validation.Require(state>=0&&state<states.Length,"Invalid default equipment state index");var s=states[state].GetProperty("data");
        // The default-state transition proof (t=0 cannot be left) is enforced by NativeEquipmentInitialController,
        // which reads the authored conditions and exit times. Here only the state time policy remains.
        Validation.Require(s.GetProperty("m_Speed").GetDouble()==1&&s.GetProperty("m_CycleOffset").GetDouble()==0,"Default equipment state has unsupported transitions or time policy");
        var defaults=graph.GetProperty("m_DefaultValues").GetProperty("data");
        Validation.Require(A(defaults,"m_BoolValues").All(v=>!v.GetBoolean())&&A(defaults,"m_FloatValues").Length==0&&A(defaults,"m_IntValues").Length==0,"Initial equipment controller has active or unsupported parameters");
        var parameters=A(graph.GetProperty("m_Values").GetProperty("data"),"m_ValueArray");
        Validation.Require(parameters.All(p=>p.GetProperty("m_Type").GetInt32()==9&&p.GetProperty("m_Index").GetInt32()>=0&&p.GetProperty("m_Index").GetInt32()<A(defaults,"m_BoolValues").Length),"Initial equipment controller needs declared false trigger parameters");
        var triggerIds=parameters.Select(p=>p.GetProperty("m_ID").GetUInt32()).ToHashSet();
        // Only false trigger conditions are known inactive at initialization.
        foreach(var t in A(machine,"m_AnyStateTransitionConstantArray")) {
            var conditions=A(t.GetProperty("data"),"m_ConditionConstantArray");
            Validation.Require(conditions.Length>0&&conditions.All(c=>c.GetProperty("data").GetProperty("m_ConditionMode").GetInt32()==1&&triggerIds.Contains(c.GetProperty("data").GetProperty("m_EventID").GetUInt32())),"Unsupported initial AnyState condition");
        }
        var trees=A(s,"m_BlendTreeConstantArray");Validation.Require(trees.Length==1,"Default equipment state is not a single clip");
        var treeNodes=A(trees[0].GetProperty("data"),"m_NodeArray");Validation.Require(treeNodes.Length==1,"Default equipment state blends multiple clips");
        var leaf=treeNodes[0].GetProperty("data");Validation.Require(A(leaf,"m_ChildIndices").Length==0&&!leaf.GetProperty("m_Mirror").GetBoolean()&&leaf.GetProperty("m_CycleOffset").GetDouble()==0,"Unsupported equipment blend node");
        int index=leaf.GetProperty("m_ClipID").GetInt32();var pointers=A(document,"m_AnimationClips");Validation.Require(index>=0&&index<pointers.Length,"Default equipment clip index is invalid");
        var clip=game.Resolve(entry.Controller.Cab,pointers[index])??throw new InvalidDataException("Default equipment clip is missing");Validation.Require(clip.Object.ClassId==74,"Default equipment pointer is not a clip");
        var data=J(clip);
        if(meshSpaceLayer){foreach(var binding in A(data.GetProperty("m_ClipBindingConstant"),"genericBindings"))
            Validation.Require(binding.GetProperty("attribute").GetInt32() is >=1 and <=4,
                "Initial equipment pose MeshSpace layer requires a transform-only direct clip");}
        var channels=ReadZeroChannels(data);var basis=ConvertPose(nodes,scene,entry.Node.SourcePath,channels);
        return new(NativePrefabHierarchy.Identity(entry.Animator),NativePrefabHierarchy.Identity(entry.Controller),state,s.GetProperty("m_NameID").GetUInt32(),NativePrefabHierarchy.Identity(clip),data.GetProperty("m_Name").GetString()!,0,basis,resourceId,resourcePath,entry.Node.SourcePath);
    }
    public sealed record Channel(uint Path,int Attribute,double[] Values);
    public static Channel[] ReadZeroChannels(JsonElement clip)
    {
        var values=new NativeGenericScalarSampler(clip).Sample(0);int offset=0;
        return A(clip.GetProperty("m_ClipBindingConstant"),"genericBindings").Select(binding=>
        {
            int attribute=binding.GetProperty("attribute").GetInt32(),size=attribute==2?4:3;
            var row=new Channel(binding.GetProperty("path").GetUInt32(),attribute,values[offset..(offset+size)]);offset+=size;return row;
        }).ToArray();
    }
    static Matrix4x4 M(double[] a)=>new((float)a[0],(float)a[4],(float)a[8],(float)a[12],(float)a[1],(float)a[5],(float)a[9],(float)a[13],(float)a[2],(float)a[6],(float)a[10],(float)a[14],(float)a[3],(float)a[7],(float)a[11],(float)a[15]);
    static Matrix4x4 Inv(Matrix4x4 m){Validation.Require(Matrix4x4.Invert(m,out var v)&&Values(v).All(double.IsFinite),"Singular or nonfinite equipment pose matrix");return v;}
    static double[] Values(Matrix4x4 m)=>[m.M11,m.M21,m.M31,m.M41,m.M12,m.M22,m.M32,m.M42,m.M13,m.M23,m.M33,m.M43,m.M14,m.M24,m.M34,m.M44];
    public static NativeEquipmentPoseBone[] ConvertPose(NativeHierarchyNode[] nodes,SceneDocument scene,string animatorPath,Channel[] channels)
    {
        Validation.Require(nodes.Length>0&&nodes.Length<=16384&&scene.Bones.Length>0&&scene.Bones.Length<=16384&&channels.Length<=16384&&!string.IsNullOrWhiteSpace(animatorPath),"Invalid initial hierarchy bounds or identity");
        Validation.Require(nodes.All(n=>!string.IsNullOrWhiteSpace(n.SourcePath))&&nodes.Select(n=>n.SourcePath).Distinct().Count()==nodes.Length,"Duplicate or empty native hierarchy paths");
        Validation.Require(scene.Bones.All(b=>!string.IsNullOrWhiteSpace(b.SourcePath)&&b.RestMatrix is {Length:16}&&b.RestMatrix.All(v=>double.IsFinite(v)&&Math.Abs(v)<=float.MaxValue))&&scene.Bones.Select(b=>b.SourcePath).Distinct().Count()==scene.Bones.Length,"Invalid imported rest identities or matrices");
        for(int i=0;i<nodes.Length;i++)Validation.Require(nodes[i].Parent>=-1&&nodes[i].Parent<i,"Invalid native parent order");
        var lookup=nodes.Where(n=>n.SourcePath==animatorPath||n.SourcePath.StartsWith(animatorPath+"/",StringComparison.Ordinal)).GroupBy(n=>PathHash(n.SourcePath==animatorPath?"":n.SourcePath[(animatorPath.Length+1)..])).ToDictionary(g=>g.Key,g=>g.ToArray());
        var local=nodes.Select(n=>n.Local).ToArray();var nodeIndex=nodes.Select((n,i)=>(n.SourcePath,i)).ToDictionary(x=>x.SourcePath,x=>x.i);var touched=new HashSet<string>();
        Validation.Require(nodeIndex.ContainsKey(animatorPath)&&scene.Bones.All(b=>nodeIndex.ContainsKey(b.SourcePath!)),"Imported bone or Animator absent from resource hierarchy");
        var f=Matrix4x4.CreateScale(-1,1,1);var space=f*Matrix4x4.CreateRotationX(MathF.PI/2);var inverseSpace=Inv(space);
        var rest=scene.Bones.Select(b=>M(b.RestMatrix!)).ToArray();var nativeRest=rest.Select(m=>f*m*inverseSpace).ToArray();
        for(int i=0;i<scene.Bones.Length;i++)
        {
            var bone=scene.Bones[i];int index=nodeIndex[bone.SourcePath!],parent=nodes[index].Parent;
            Validation.Require(bone.Parent>=-1&&bone.Parent<i&&(parent<0?bone.Parent<0:bone.Parent>=0&&scene.Bones[bone.Parent].SourcePath==nodes[parent].SourcePath),"Imported rest parent differs from exact native hierarchy");
            local[index]=nativeRest[i]*(bone.Parent<0?Matrix4x4.Identity:Inv(nativeRest[bone.Parent]));
            Validation.Require(Values(local[index]).All(double.IsFinite),"Nonfinite imported rest local");
        }
        var seen=new HashSet<(uint,int)>();
        foreach(var group in channels.GroupBy(c=>c.Path)) {
            Validation.Require(lookup.TryGetValue(group.Key,out var matches)&&matches.Length==1,"Initial equipment animation path is missing or hash-colliding");var node=matches![0];int i=nodeIndex[node.SourcePath];Validation.Require(Matrix4x4.Decompose(local[i],out var scale,out var rotation,out var position),"Invalid imported rest local");
            var baseline=Matrix4x4.CreateScale(scale)*Matrix4x4.CreateFromQuaternion(rotation)*Matrix4x4.CreateTranslation(position);
            Validation.Require(Values(baseline).All(double.IsFinite)&&Values(baseline).Zip(Values(local[i]),(a,b)=>Math.Abs(a-b)).Max()<0.0001,"Imported rest contains unsupported shear");
            foreach(var c in group){var v=c.Values;Validation.Require(c.Attribute is >=1 and <=3&&seen.Add((c.Path,c.Attribute))&&v.Length==(c.Attribute==2?4:3)&&v.All(x=>double.IsFinite(x)&&Math.Abs(x)<=float.MaxValue),"Invalid initial channel values");if(c.Attribute==1)position=new((float)v[0],(float)v[1],(float)v[2]);else if(c.Attribute==3)scale=new((float)v[0],(float)v[1],(float)v[2]);else {rotation=new((float)v[0],(float)v[1],(float)v[2],(float)v[3]);Validation.Require(float.IsFinite(rotation.LengthSquared())&&Math.Abs(rotation.LengthSquared()-1)<0.001,"Invalid initial pose quaternion");rotation=Quaternion.Normalize(rotation);}}
            local[i]=Matrix4x4.CreateScale(scale)*Matrix4x4.CreateFromQuaternion(rotation)*Matrix4x4.CreateTranslation(position);touched.Add(node.SourcePath);
        }
        Validation.Require(touched.All(path=>scene.Bones.Any(b=>b.SourcePath==path)),"Initial animation targets a transform absent from equipment rig");
        var world=new Matrix4x4[nodes.Length];for(int i=0;i<nodes.Length;i++){world[i]=local[i]*(nodes[i].Parent<0?Matrix4x4.Identity:world[nodes[i].Parent]);Validation.Require(Values(world[i]).All(double.IsFinite),"Initial pose world overflow");}
        var pose=scene.Bones.Select(b=>f*world[nodeIndex[b.SourcePath!]]*space).ToArray();
        return scene.Bones.Select((b,i)=>{var posedLocal=pose[i]*(b.Parent<0?Matrix4x4.Identity:Inv(pose[b.Parent]));var restLocal=rest[i]*(b.Parent<0?Matrix4x4.Identity:Inv(rest[b.Parent]));var values=Values(posedLocal*Inv(restLocal));Validation.Require(values.All(double.IsFinite),"Initial pose basis overflow");return new NativeEquipmentPoseBone(i,b.SourcePath!,values);}).ToArray();
    }
}
