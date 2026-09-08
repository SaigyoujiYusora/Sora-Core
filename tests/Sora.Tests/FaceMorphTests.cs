using System.Numerics;
using System.Text.Json;
using Sora.Core;

internal static class FaceMorphTests
{
    public static void Run(Action<string,Action> test, Action<Action> reject)
    {
        var bone=new FaceBoneRecord(4,19,"Root/Face",0,[1,2,3],[17,29,-41],[1,1,1]);
        var face=new FaceDriverRecord(1,"test.asset","CAB-test",1,"CAB-avatar",2,"zyx",[1,-1,-1],0,[bone],
            [new("A",0,1,2,3,[new(0,[.1,0,0],[10,20,30],[.2,0,0])]),
             new("B",1,2,2,3,[new(0,[0,.2,0],[-5,6,8],[0,.1,0])])],[]);
        var scene=new SceneDocument("Face",[new("Face",-1,[0,0,0],[0,1,0],SourcePath:"Root/Face")],[],[],[],FaceDriver:face);
        test("face DTO validation and JSON roundtrip",()=>{
            NativeFaceMorph.Validate(face,scene);
            var copy=JsonSerializer.Deserialize<FaceDriverRecord>(JsonSerializer.Serialize(face,WireJson.Options),WireJson.Options)!;
            NativeFaceMorph.Validate(copy,scene);
        });
        test("authored face adds TRS once and resets without history",()=>{
            var result=NativeFaceMorph.Evaluate(face,new Dictionary<string,double>{{"A",.5},{"B",.25}})[0];
            if(!Matrix4x4.Decompose(result,out var s,out var q,out var p)||Vector3.Distance(p,new(1.05f,2.05f,3))>1e-6||Vector3.Distance(s,new(1.1f,1.025f,1))>1e-6) throw new Exception("TRS weights differ");
            var expected=NativeFaceMorph.Rotation([20.75,40.5,-24],"zyx",[1,-1,-1]);
            if(Math.Abs(Quaternion.Dot(q,expected))<.999999)throw new Exception("Rotation differs");
            var reverse=NativeFaceMorph.Evaluate(face,new Dictionary<string,double>{{"B",.25},{"A",.5}})[0];
            if(result!=reverse)throw new Exception("Input order changed result");
            var neutral=NativeFaceMorph.Evaluate(face,new Dictionary<string,double>())[0];
            if(neutral.Translation!=new Vector3(1,2,3))throw new Exception("Neutral retained history");
        });
        test("face fit distinguishes multi-axis convention",()=>{
            var fit=NativeFaceMorph.Fit([bone,bone with{Rotation=[63,-12,24]}],new[]{NativeFaceMorph.Rotation(bone.Rotation,"zyx",[1,-1,-1]),NativeFaceMorph.Rotation([63,-12,24],"zyx",[1,-1,-1])});
            if(fit.Order!="zyx"||!fit.Signs.SequenceEqual(new[]{1,-1,-1}))throw new Exception("Convention mismatch");
        });
        test("face rejects broken mappings and nonfinite values",()=>{
            reject(()=>NativeFaceMorph.Validate(face with{Bones=[bone with{SceneBone=3}]},scene));
            reject(()=>NativeFaceMorph.Validate(face with{Bones=[bone,bone with{NativeId=5}]},scene));
            reject(()=>NativeFaceMorph.Validate(face with{Controls=[face.Controls[0],face.Controls[0]]},scene));
            reject(()=>NativeFaceMorph.Evaluate(face,new Dictionary<string,double>{{"A",double.NaN}}));
        });
    }
}
