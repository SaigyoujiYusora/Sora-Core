using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sora.Core;
public static class EquipmentDefaultPoseTests
{
    public static void Run(Action<string,Action> test,Action<Action> reject)
    {
        JsonObject Fixture(){
            uint W(float f)=>unchecked((uint)BitConverter.SingleToInt32Bits(f));
            return JsonSerializer.SerializeToNode(new {
                m_Legacy=false,m_Compressed=false,
                m_AclCompressedBuffer=new {OutputTrackCount=0,FloatCurveCount=0,RootTrackCount=0,TransformBufferData=new{Array=""},FloatBufferData=new{Array=""},RootMotionBufferData=new{Array=""}},
                m_MuscleClip=new {m_StartTime=0,m_StopTime=1,m_Clip=new {data=new {
                    m_StreamedClip=new {curveCount=3,data=new {Array=new uint[]{W(0),3,0,0,0,0,W(1),1,0,0,0,W(2),2,0,0,0,W(3),W(1),3,0,0,0,0,W(1),1,0,0,0,W(2),2,0,0,0,W(3),W(float.PositiveInfinity),0}}},
                    m_DenseClip=new {m_CurveCount=3,m_FrameCount=2,m_SampleRate=1,m_BeginTime=0,m_ACLType=16,m_SampleArray=new {Array=System.Array.Empty<float>()},m_ACLArray=new {Array=Convert.ToBase64String(new byte[]{0,0,0,128,255,255,0,0,0,128,255,255})},m_nPositionCurves=3,m_nRotationCurves=0,m_nEulerCurves=0,m_nScaleCurves=0,m_PositionFactor=2},
                    m_ConstantClip=new {data=new {Array=new float[]{0,0,0,1}}}
                }}},
                m_ClipBindingConstant=new {genericBindings=new {Array=new[]{new {path=0u,typeID=4,customType=0,isPPtrCurve=0,attribute=1},new {path=NativeEquipmentDefaultPoseReader.PathHash("Child"),typeID=4,customType=0,isPPtrCurve=0,attribute=1},new {path=0u,typeID=4,customType=0,isPPtrCurve=0,attribute=2}}}}
            })!.AsObject();
        }
        JsonElement E(JsonNode n)=>JsonSerializer.SerializeToElement(n);
        test("equipment initial curves join streamed zero packed16 and constants",()=>{
            var rows=NativeEquipmentDefaultPoseReader.ReadZeroChannels(E(Fixture()));
            if(rows.Length!=3||rows[0].Values[1]!=2||rows[1].Values[0]!=-2||rows[1].Values[2]!=2||Math.Abs(rows[1].Values[1]-2.0/65535)>1e-10||rows[2].Values[3]!=1)throw new Exception("Scalar block order mismatch");
            if(NativeEquipmentDefaultPoseReader.PathHash("Root")!=3066451557||NativeEquipmentDefaultPoseReader.PathHash("idle_to_battle")!=2868670551)throw new Exception("Native identity CRC mismatch");
        });
        test("equipment initial curves reject missing zero packed truncation and unknown bindings",()=>{
            var n=Fixture();n["m_MuscleClip"]!["m_Clip"]!["data"]!["m_StreamedClip"]!["data"]!["Array"]![0]=unchecked((uint)BitConverter.SingleToInt32Bits(-1));reject(()=>NativeEquipmentDefaultPoseReader.ReadZeroChannels(E(n)));
            n=Fixture();n["m_MuscleClip"]!["m_Clip"]!["data"]!["m_DenseClip"]!["m_ACLArray"]!["Array"]="AA==";reject(()=>NativeEquipmentDefaultPoseReader.ReadZeroChannels(E(n)));
            n=Fixture();n["m_ClipBindingConstant"]!["genericBindings"]!["Array"]![0]!["typeID"]=95;reject(()=>NativeEquipmentDefaultPoseReader.ReadZeroChannels(E(n)));
        });
        test("equipment initial pose preserves rest and converts world frame exactly",()=>{
            var f=Matrix4x4.CreateScale(-1,1,1);var space=f*Matrix4x4.CreateRotationX(MathF.PI/2);double[] V(Matrix4x4 m)=>[m.M11,m.M21,m.M31,m.M41,m.M12,m.M22,m.M32,m.M42,m.M13,m.M23,m.M33,m.M43,m.M14,m.M24,m.M34,m.M44];
            var rest=V(f*space);var nodes=new[]{new NativeHierarchyNode(null!,null!,"Item",-1,"Item",Matrix4x4.Identity,Matrix4x4.Identity)};
            var scene=new SceneDocument("Item",[new("Item",-1,[0,0,0],[0,0,1],0,rest,"Item")],[],[],[]);
            var pose=NativeEquipmentDefaultPoseReader.ConvertPose(nodes,scene,"Item",[new(0,1,[1,2,3])]);var m=pose[0].BasisMatrix;
            if(Math.Abs(m[3]+1)>1e-6||Math.Abs(m[7]-2)>1e-6||Math.Abs(m[11]-3)>1e-6||!scene.Bones[0].RestMatrix!.SequenceEqual(rest))throw new Exception("Rest or local basis frame changed");
            reject(()=>NativeEquipmentDefaultPoseReader.ConvertPose(nodes,scene,"Item",[new(123,1,[1,2,3])]));
        });
    }
}
