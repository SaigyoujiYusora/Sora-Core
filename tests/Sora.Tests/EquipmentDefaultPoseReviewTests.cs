using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sora.Core;

public static class EquipmentDefaultPoseReviewTests
{
    // Local, undistributed bow-controller-fresh.json; provenance and digest are recorded
    // in audit/v020/core-pose-final-candidate-manifest.json. Renew evidence explicitly.
    private static void VerifyControllerSnapshot(byte[] raw)
    {
        if (Convert.ToHexStringLower(SHA256.HashData(raw)) != "38581364ab6a9cac07004641a9445aff836dce7a84090158ba1187a46c77cb60")
            throw new InvalidDataException("Native bow controller source snapshot changed; renew evidence explicitly");
    }

    public static void Run(Action<string,Action> test,Action<Action> reject,string? controllerPath=null,string? clipPath=null)
    {
        byte[] raw = controllerPath is null ? [] : File.ReadAllBytes(controllerPath);
        if (controllerPath is not null) VerifyControllerSnapshot(raw);
        // Parse only the bytes that passed the pin, never reopen a mutable fixture path.
        JsonNode Controller()=>controllerPath is null ? PortablePoseFixtures.Controller() : JsonNode.Parse(raw)![0]!["controller"]!.DeepClone();
        JsonNode Machine(JsonNode n)=>n["m_Controller"]!["m_StateMachineArray"]!["Array"]![0]!["data"]!;
        JsonNode State(JsonNode n)=>Machine(n)["m_StateConstantArray"]!["Array"]![0]!["data"]!;
        JsonNode Leaf(JsonNode n)=>State(n)["m_BlendTreeConstantArray"]!["Array"]![0]!["data"]!["m_NodeArray"]!["Array"]![0]!["data"]!;
        void Validate(JsonNode n)=>NativeEquipmentInitialController.Validate(JsonSerializer.SerializeToElement(n));
        test("controller fixture selects safe initial subset",()=>
        {
            Validate(Controller());
            reject(()=>VerifyControllerSnapshot("[]"u8.ToArray()));
            if (controllerPath is not null)
                reject(()=>VerifyControllerSnapshot(raw.Concat(new byte[]{(byte)' '}).ToArray()));
        });
        test("mesh-space default pose accepts only a resolved nonhuman Avatar and a t=0-stable state",()=>
        {
            JsonNode MeshLayer(JsonNode n)=>n["m_Controller"]!["m_LayerArray"]!["Array"]![0]!["data"]!;
            // A MeshSpace layer is only inert for a nonhuman Avatar; the strict path must not accept it.
            var mesh=Controller();MeshLayer(mesh)["m_MeshSpace"]=true;
            reject(()=>Validate(mesh));
            // A missing Avatar is unresolved, never accepted as nonhuman; an unknown member shape stays rejected.
            var missing=NativeEquipmentInitialController.AvatarHumanSkeleton(null);
            if(missing.Resolved||missing.Nonhuman||missing.HumanPointerNull)throw new Exception("A null Avatar must stay unresolved Unknown");
            // The default-state transition proof: a conditionless branch that can fire at t=0 is unsupported.
            var early=Controller();var state=early["m_Controller"]!["m_StateMachineArray"]!["Array"]![0]!["data"]!["m_StateConstantArray"]!["Array"]![0]!["data"]!;
            state["m_TransitionConstantArray"]!["Array"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse("{\"data\":{\"m_ConditionConstantArray\":{\"Array\":[]},\"m_HasExitTime\":false,\"m_ExitTime\":0,\"m_DestinationState\":0,\"m_TransitionBlockParamConstantArray\":{\"Array\":[]},\"m_BlendStyle\":0,\"m_TransitionDuration\":0,\"m_HasFixedDuration\":true,\"m_TransitionOffset\":0}}"));
            reject(()=>Validate(early));
            // A future exit time cannot change the t=0 pose and stays accepted.
            var later=Controller();var laterState=later["m_Controller"]!["m_StateMachineArray"]!["Array"]![0]!["data"]!["m_StateConstantArray"]!["Array"]![0]!["data"]!;
            laterState["m_TransitionConstantArray"]!["Array"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse("{\"data\":{\"m_ConditionConstantArray\":{\"Array\":[]},\"m_HasExitTime\":true,\"m_ExitTime\":1.0,\"m_DestinationState\":0,\"m_TransitionBlockParamConstantArray\":{\"Array\":[]},\"m_BlendStyle\":0,\"m_TransitionDuration\":0,\"m_HasFixedDuration\":true,\"m_TransitionOffset\":0}}"));
            Validate(later);
        });
        test("unsupported controller policies remain explicit nonblocking initial-pose gaps",()=>
        {
            var edits=new Action<JsonNode>[] {
                n=>n["m_Controller"]!["m_LayerArray"]!["Array"]![0]!["data"]!["m_StateMachineIndex"]=1,
                n=>Machine(n)["m_ImmediateTransition"]=true,
                n=>Machine(n)["m_SelectorStateConstantArray"]!["Array"]![0]!["data"]!["m_TransitionConstantArray"]!["Array"]![0]!["data"]!["m_Destination"]=1,
                n=>State(n)["m_TimeParamID"]=123,
                n=>State(n)["m_SpeedParamID"]=123,
                n=>State(n)["m_MirrorParamID"]=123,
                n=>State(n)["m_CycleOffsetParamID"]=123,
                n=>State(n)["m_Mirror"]=true,
                n=>State(n)["m_BlendTreeConstantIndexArray"]!["Array"]![0]=1,
                n=>State(n)["m_StateParameterConstantArray"]!["Array"]!.AsArray().Add(0),
                n=>Leaf(n)["m_BlendType"]=1,
                n=>Leaf(n)["m_BlendEventID"]=1,
                n=>Leaf(n)["m_Duration"]=2,
                n=>Leaf(n)["m_Mirror"]=true,
                n=>n["m_Controller"]!["m_LayerArray"]!["Array"]!.AsArray().Add(n["m_Controller"]!["m_LayerArray"]!["Array"]![0]!.DeepClone())
            };
            var metadataEdits=new List<Action<JsonNode>>();
            JsonNode Layer(JsonNode n)=>n["m_Controller"]!["m_LayerArray"]!["Array"]![0]!["data"]!;
            JsonNode Sequence(JsonNode n)=>Leaf(n)["m_BlendSequenceData"]!["data"]!;
            foreach(string word in new[]{"word0","word1","word2"}) {
                metadataEdits.Add(n=>Layer(n)["m_BodyMask"]![word]=0);
                metadataEdits.Add(n=>Sequence(n)["m_BodyMask"]![word]=1);
            }
            foreach(string field in new[]{"m_DefaultWeight","(int&)m_layerOptMode","m_LODThreshold","m_AbilityThreshold"})metadataEdits.Add(n=>Layer(n)[field]=1);
            metadataEdits.Add(n=>Layer(n)["m_SkeletonMask"]!["data"]!["m_Data"]!["Array"]!.AsArray().Add(0));
            metadataEdits.Add(n=>Sequence(n)["m_SkeletonMask"]!["data"]!["m_Data"]!["Array"]!.AsArray().Add(0));
            foreach(string field in new[]{"m_ChildBodyMask","m_ChildSkeletonMask","m_ChildSpeed","m_ChildLodThreshold","m_ChildAbilityThreshold"})metadataEdits.Add(n=>Sequence(n)[field]!["Array"]!.AsArray().Add(0));
            foreach(string field in new[]{"m_BlendingMode","m_ChildCullingMode"}) {
                metadataEdits.Add(n=>Sequence(n)[field]!["Array"]="AA==");
                metadataEdits.Add(n=>Sequence(n)[field]!["Array"]=null);
            }
            metadataEdits.Add(n=>Sequence(n)["m_UseBlendDuration"]=false);
            edits=edits.Concat(metadataEdits).ToArray();
            foreach(var edit in edits)
            {
                var document=Controller();edit(document);var gaps=new List<NativeEquipmentGap>();
                var pose=NativeEquipmentAssembly.ReadOptionalInitialPose("equipment.prefab",()=>{Validate(document);throw new Exception("Unsupported controller accepted");},gaps);
                if(pose is not null||gaps.Count!=1||gaps[0].BlocksBinding||gaps[0].Code!="default-equipment-pose-unavailable")throw new Exception("Optional gap contract failed");
                var scene=new SceneDocument("static",[],[],[],[]);
                var resource=new NativeEquipmentResource("resource","equipment.prefab",scene,"rigid",[],[],"decoded",pose);
                if(resource.Scene!=scene||resource.Status!="decoded")throw new Exception("Static resource lost");
            }
            bool cancelled=false;try{NativeEquipmentAssembly.ReadOptionalInitialPose("x",()=>throw new OperationCanceledException(),[]);}catch(OperationCanceledException){cancelled=true;}
            if(!cancelled)throw new Exception("Cancellation swallowed");
            bool oom=false;try{NativeEquipmentAssembly.ReadOptionalInitialPose("x",()=>throw new OutOfMemoryException("synthetic"),[]);}catch(OutOfMemoryException){oom=true;}
            if(!oom)throw new Exception("OOM swallowed");
            bool unexpected=false;try{NativeEquipmentAssembly.ReadOptionalInitialPose("x",()=>throw new NotSupportedException("unrelated"),[]);}catch(NotSupportedException){unexpected=true;}
            if(!unexpected)throw new Exception("Unrelated failure swallowed");
        });
        test("partial channels preserve anchored imported parent and child rest",()=>
        {
            double[] V(Matrix4x4 m)=>[m.M11,m.M21,m.M31,m.M41,m.M12,m.M22,m.M32,m.M42,m.M13,m.M23,m.M33,m.M43,m.M14,m.M24,m.M34,m.M44];
            var f=Matrix4x4.CreateScale(-1,1,1);var space=f*Matrix4x4.CreateRotationX(MathF.PI/2);
            var root=Matrix4x4.CreateRotationZ(.3f)*Matrix4x4.CreateTranslation(3,4,5);
            var child=Matrix4x4.CreateRotationY(.4f)*Matrix4x4.CreateTranslation(1,2,3)*root;
            var nodes=new[]{new NativeHierarchyNode(null!,null!,"Root",-1,"Root",Matrix4x4.Identity,Matrix4x4.Identity),new NativeHierarchyNode(null!,null!,"Child",0,"Root/Child",Matrix4x4.Identity,Matrix4x4.Identity)};
            var scene=new SceneDocument("anchored",[new("Root",-1,[0,0,0],[0,0,1],0,V(f*root*space),"Root"),new("Child",0,[0,0,0],[0,0,1],0,V(f*child*space),"Root/Child")],[],[],[]);
            string before=JsonSerializer.Serialize(scene);
            var pose=NativeEquipmentDefaultPoseReader.ConvertPose(nodes,scene,"Root",[new(NativeEquipmentDefaultPoseReader.PathHash("Child"),3,[1,1,1])]);
            if(pose.Any(p=>p.BasisMatrix.Zip(V(Matrix4x4.Identity),(a,b)=>Math.Abs(a-b)).Max()>1e-5)||JsonSerializer.Serialize(scene)!=before)throw new Exception("Imported rest baseline drifted");
            reject(()=>NativeEquipmentDefaultPoseReader.ConvertPose(nodes,scene with{Bones=[scene.Bones[0],scene.Bones[1] with{Parent=-1}]},"Root",[]));
            reject(()=>NativeEquipmentDefaultPoseReader.ConvertPose(nodes,scene,"Root",[new(0,1,[double.MaxValue,0,0])]));
            reject(()=>NativeEquipmentDefaultPoseReader.ConvertPose(nodes,scene,"Root",[new(0,3,[float.MaxValue,float.MaxValue,float.MaxValue])]));
        });
        test("malformed streamed sentinels and zero-width dense metadata are nonblocking",()=>
        {
            JsonNode Fixture()=>clipPath is null ? PortablePoseFixtures.Clip() : JsonNode.Parse(File.ReadAllText(clipPath))!;
            foreach(var edit in new Action<JsonNode>[] {
                n=>n["m_MuscleClip"]!["m_Clip"]!["data"]!["m_StreamedClip"]!["data"]!["Array"]![17]=0x7f800000u,
                n=>n["m_MuscleClip"]!["m_Clip"]!["data"]!["m_StreamedClip"]!["data"]!["Array"]![0]=0xff800000u,
                n=>n["m_MuscleClip"]!["m_Clip"]!["data"]!["m_DenseClip"]!["m_CurveCount"]=0,
                n=>n["m_MuscleClip"]!["m_Clip"]!["data"]!["m_DenseClip"]!["m_ACLArray"]!["Array"]="not base64",
                n=>n["m_MuscleClip"]!["m_Clip"]!["data"]!["m_DenseClip"]!["m_ACLArray"]!["Array"]=null })
            {
                var n=Fixture();edit(n);var gaps=new List<NativeEquipmentGap>();
                var result=NativeEquipmentAssembly.ReadOptionalInitialPose("item",()=>{NativeEquipmentDefaultPoseReader.ReadZeroChannels(JsonSerializer.SerializeToElement(n));throw new Exception("Malformed data accepted");},gaps);
                if(result is not null||gaps.Count!=1||gaps[0].BlocksBinding)throw new Exception("Malformed optional pose blocked binding");
            }
        });
    }
}
