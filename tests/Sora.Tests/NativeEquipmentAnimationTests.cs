using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sora.Core;

public static class NativeEquipmentAnimationTests
{
    static JsonElement E(JsonNode n)=>JsonSerializer.SerializeToElement(n);
    static JsonNode Fixture()
    {
        byte[] packed=[0,0,0,128,0,128,0,128,0,128,0,128,255,255,0,128,0,128];
        return JsonSerializer.SerializeToNode(new {
            m_Name="Synthetic moving item",m_SampleRate=2,m_Legacy=false,m_Compressed=false,
            m_AclCompressedBuffer=new {OutputTrackCount=0,FloatCurveCount=0,RootTrackCount=0,TransformBufferData=new{Array=""},FloatBufferData=new{Array=""},RootMotionBufferData=new{Array=""}},
            m_MuscleClip=new {m_StartTime=0,m_StopTime=1,m_Clip=new {data=new {
                m_StreamedClip=new {curveCount=0,data=new {Array=System.Array.Empty<uint>()}},
                m_DenseClip=new {m_CurveCount=3,m_FrameCount=3,m_SampleRate=2,m_BeginTime=0,m_ACLType=16,m_SampleArray=new {Array=System.Array.Empty<float>()},m_ACLArray=new {Array=Convert.ToBase64String(packed)},m_nPositionCurves=3,m_nRotationCurves=0,m_nEulerCurves=0,m_nScaleCurves=0,m_PositionFactor=1},
                m_ConstantClip=new {data=new {Array=new float[]{0,0,0,1}}}
            }}},
            m_ClipBindingConstant=new {genericBindings=new {Array=new[]{new {path=0u,typeID=4,customType=0,isPPtrCurve=0,attribute=1},new {path=0u,typeID=4,customType=0,isPPtrCurve=0,attribute=2}}}}
        })!;
    }
    public static void Run(Action<string,Action> test,Action<Action> reject)
    {
        var f=Matrix4x4.CreateScale(-1,1,1);var space=f*Matrix4x4.CreateRotationX(MathF.PI/2);var m=f*space;
        double[] rest=[m.M11,m.M21,m.M31,m.M41,m.M12,m.M22,m.M32,m.M42,m.M13,m.M23,m.M33,m.M43,m.M14,m.M24,m.M34,m.M44];
        NativeHierarchyNode[] nodes=[new(null!,null!,"Item",-1,"Item",Matrix4x4.Identity,Matrix4x4.Identity)];
        var scene=new SceneDocument("Item",[new("Item",-1,[0,0,0],[0,0,1],0,rest,"Item")],[],[],[]);
        var source=new NativeAnimationSource("assets/item.prefab","CAB-fixture","42","fixture-manifest");
        NativeEquipmentAnimationService.Conversion Convert(JsonNode n)=>NativeEquipmentAnimationService.Convert(E(n),nodes,scene,"Item",source);
        test("equipment discovery schema retains complete raw attributes and exact native paths",()=>{
            var rows=NativeEquipmentAnimationService.ReadBindingSchema(E(Fixture()),nodes,"Item");
            if(rows.Length!=2||!rows.Select(r=>r.Attribute).SequenceEqual(new[]{1,2})||rows.Any(r=>r.PathHash!=0||r.SourcePath!="Item"||r.Resolution!="native-path"||r.TypeId!=4||r.CustomType!=0||r.IsPPtrCurve!=0))throw new Exception("Discovery lost binding schema");
            var bindings=NativeEquipmentAnimationService.Bindings(E(Fixture()),nodes,scene,"Item");
            if(!rows.Select(r=>(r.PathHash,r.Attribute,r.SourcePath)).SequenceEqual(bindings.Select(r=>(r.PathHash,r.Attribute,(string?)r.SourcePath))))throw new Exception("Discovery/import source bindings differ");
        });
        test("equipment discovery exposes unknown unsupported and duplicate source bindings without inventing mapping",()=>{
            var n=Fixture();var rows=n["m_ClipBindingConstant"]!["genericBindings"]!["Array"]!.AsArray();rows[0]!["path"]=123456u;rows[0]!["typeID"]=23;rows.Add(rows[0]!.DeepClone());
            var schema=NativeEquipmentAnimationService.ReadBindingSchema(E(n),nodes,"Item");
            if(schema.Length!=3||schema[0].SourcePath is not null||schema[0].Resolution!="unmapped-path-hash"||schema[0].TypeId!=23||schema[0]!=schema[2])throw new Exception("Unresolved schema was silently changed or discarded");
            reject(()=>Convert(n));
        });
        test("equipment full clip retains endpoints native grid TRS source and untouched Rest",()=>{
            var before=JsonSerializer.Serialize(scene,WireJson.Options);var result=Convert(Fixture());
            if(!result.Times.SequenceEqual(new[]{0,.5,1})||result.Clip.Tracks.Length!=3||result.Bindings.Length!=2||result.Clip.Native?.Source!=source)throw new Exception("Clip grid or source lost");
            var location=result.Clip.Tracks.Single(t=>t.Channel=="location");
            if(Math.Abs(location.Keys[0].Value[0]-1)>1e-6||Math.Abs(location.Keys[2].Value[0]+1)>1e-6||Math.Abs(location.Keys[1].Value[0]+1.0/65535)>1e-6)throw new Exception("Full native motion or basis orientation was lost");
            if(JsonSerializer.Serialize(scene,WireJson.Options)!=before)throw new Exception("Rest scene changed");
            foreach(var track in result.Clip.Tracks)if(!track.Keys.Select(k=>k.Time).SequenceEqual(result.Times))throw new Exception("Track key grid differs");
        });
        test("equipment clip rejects unknown duplicate and unsupported bindings with identities",()=>{
            var n=Fixture();n["m_ClipBindingConstant"]!["genericBindings"]!["Array"]![0]!["path"]=123456u;
            try{Convert(n);throw new Exception("Unknown path accepted");}catch(InvalidDataException e){if(!e.Message.Contains("pathHash=123456 attribute=1"))throw new Exception("Missing precise unknown binding diagnostic");}
            n=Fixture();n["m_ClipBindingConstant"]!["genericBindings"]!["Array"]![0]!["typeID"]=23;reject(()=>Convert(n));
            n=Fixture();var a=n["m_ClipBindingConstant"]!["genericBindings"]!["Array"]!.AsArray();a.Add(a[0]!.DeepClone());reject(()=>Convert(n));
            var absent=scene with {Bones=[scene.Bones[0] with {SourcePath="Different"}]};reject(()=>NativeEquipmentAnimationService.Bindings(E(Fixture()),nodes,absent,"Item"));
        });
        test("equipment clip rejects source rate mismatch and missing data and keeps the authored off-grid end",()=>{
            var n=Fixture();n["m_SampleRate"]=30;reject(()=>Convert(n));
            n=Fixture();n["m_MuscleClip"]!["m_Clip"]!["data"]!["m_DenseClip"]!["m_ACLArray"]!["Array"]="";reject(()=>Convert(n));
            n=Fixture();n["m_MuscleClip"]!["m_Clip"]!["data"]!["m_ConstantClip"]!["data"]!["Array"]![3]=0;reject(()=>Convert(n));
            // An authored end between native frames stays the final exact key instead of being snapped.
            n=Fixture();n["m_MuscleClip"]!["m_StopTime"]=.9;var result=Convert(n);
            if(result.Times.Length!=3||result.Times[0]!=0||result.Times[1]!=.5||Math.Abs(result.Times[2]-(float).9)>1e-12)throw new Exception("Authored off-grid end was not kept");
            foreach(var track in result.Clip.Tracks)if(!track.Keys.Select(k=>k.Time).SequenceEqual(result.Times))throw new Exception("Track grid differs from the authored end");
        });
        test("equipment clip cancellation stops inside frame loop without partial success",()=>{
            var saved=OperationProgress.Sink;int frames=0;bool cancelled=false;
            try{
                OperationProgress.Sink=u=>{if(u.Stage=="sample-equipment-animation"&&++frames==2)throw new OperationCanceledException();};
                try{Convert(Fixture());}catch(OperationCanceledException){cancelled=true;}
            }finally{OperationProgress.Sink=saved;}
            if(!cancelled||frames!=2)throw new Exception("Cancellation was swallowed or not checkpointed");
        });
        test("equipment source paths cannot alias two hierarchy nodes",()=>{
            NativeHierarchyNode[] ambiguous=[nodes[0],nodes[0]];
            reject(()=>NativeEquipmentAnimationService.Bindings(E(Fixture()),ambiguous,scene,"Item"));
        });
    }
}
