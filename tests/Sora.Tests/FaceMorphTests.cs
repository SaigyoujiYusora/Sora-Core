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
        var earName="ear_L_rotattehorizontally_ctrl";
        var ears=new FaceDriverRecord(1,"ear.asset","CAB-ear",3,"CAB-avatar",2,"zyx",[1,-1,-1],0,
            [new(9,29,"Root/Face/EarL",1,[.1,.2,.3],[11,-22,33],[1,1,1]),new(10,30,"Root/Face/EarR",2,[-.1,.2,.3],[-11,22,-33],[1,1,1])],
            [new(earName,0,31,64,64,[new(0,[0,.1,0],[13,0,0],[0,0,0])]),new("ear_R_bend_ctrl",1,32,64,64,[new(1,[0,0,.2],[0,-17,0],[0,0,0])])],[]);
        var earScene=scene with{Bones=[scene.Bones[0],new("EarL",0,[0,1,0],[0,2,0],SourcePath:"Root/Face/EarL"),new("EarR",0,[0,1,0],[0,2,0],SourcePath:"Root/Face/EarR")]};
        test("supplementary ear merge remaps bone deltas without changing primary source or native IDs",()=>{
            string before=JsonSerializer.Serialize(face,WireJson.Options),earBefore=JsonSerializer.Serialize(ears,WireJson.Options);
            var merged=NativeFaceMorph.MergeSupplementary(face,ears,earScene);var source=merged.AdditionalSources!.Single();
            if(merged.PrimaryBoneCount!=1||merged.PrimaryControlCount!=2||merged.SourcePath!=face.SourcePath||merged.SourceCab!=face.SourceCab||merged.SourceObject!=face.SourceObject||merged.AvatarCab!=face.AvatarCab||merged.AvatarObject!=face.AvatarObject||merged.Controls[2].Id!=0||merged.Controls[2].Name!=earName||merged.Controls[2].Bones[0].Bone!=1||merged.Controls[3].Bones[0].Bone!=2||source.BoneStart!=1||source.BoneCount!=2||source.ControlStart!=2||source.ControlCount!=2||source.SourceCab!="CAB-ear"||source.SourceObject!=3||source.AvatarObject!=2||source.DataType!=1)throw new Exception("Ear identity or provenance was lost");
            var weights=new Dictionary<string,double>{{"A",.25},{earName,.7},{"ear_R_bend_ctrl",.3}};var combined=NativeFaceMorph.Evaluate(merged,weights);var separate=NativeFaceMorph.Evaluate(ears,weights);
            if(combined[0]!=NativeFaceMorph.Evaluate(face,weights)[0]||combined[1]!=separate[0]||combined[2]!=separate[1])throw new Exception("Supplementary deltas affected the wrong source bone");
            if(before!=JsonSerializer.Serialize(face,WireJson.Options)||earBefore!=JsonSerializer.Serialize(ears,WireJson.Options))throw new Exception("Merge mutated its source DTOs");
        });
        test("supplementary ear merge rejects Avatar convention source and bone conflicts",()=>{
            foreach(var invalid in new[]{ears with{AvatarCab="CAB-other"},ears with{AvatarObject=4},ears with{RotationOrder="xyz"},ears with{RotationSigns=[1,1,1]},ears with{SourcePath=face.SourcePath},ears with{SourceCab=face.SourceCab,SourceObject=face.SourceObject},ears with{Bones=[ears.Bones[0] with{NativeId=bone.NativeId},ears.Bones[1]]},ears with{Bones=[ears.Bones[0] with{SceneBone=0,NativePath=bone.NativePath},ears.Bones[1]]},ears with{Controls=[ears.Controls[0] with{Name="A"},ears.Controls[1]]}})
                reject(()=>NativeFaceMorph.MergeSupplementary(face,invalid,earScene));
        });
        test("supplementary provenance ranges prevent cross-source targets and repeated source-local IDs",()=>{
            var merged=NativeFaceMorph.MergeSupplementary(face,ears,earScene);var source=merged.AdditionalSources![0];
            foreach(var invalid in new[]{source with{BoneStart=0},source with{ControlCount=1},source with{AvatarObject=0},source with{DataType=0},source with{RestFitDegrees=double.NaN}})
                reject(()=>NativeFaceMorph.Validate(merged with{AdditionalSources=[invalid]},earScene));
            reject(()=>NativeFaceMorph.Validate(merged with{AdditionalSources=[source,source]},earScene));
            var controls=(FaceControlRecord[])merged.Controls.Clone();controls[2]=controls[2] with{Bones=[controls[2].Bones[0] with{Bone=0}]};reject(()=>NativeFaceMorph.Validate(merged with{Controls=controls},earScene));
            controls=(FaceControlRecord[])merged.Controls.Clone();controls[3]=controls[3] with{Id=controls[2].Id};reject(()=>NativeFaceMorph.Validate(merged with{Controls=controls},earScene));
        });
        test("supplementary controls update preset compatibility and survive SRED roundtrip",()=>{
            var primary=face with{Presets=[new("ear preset","preset.asset",false,new(){{earName,.5}},[earName])]};var merged=NativeFaceMorph.MergeSupplementary(primary,ears,earScene);
            if(merged.Presets[0].MissingControls.Length!=0||primary.Presets[0].MissingControls.Length!=1)throw new Exception("Preset compatibility was not recalculated");
            var database=new DatabaseDocument("v",[new("a","A","","character",[],earScene with{FaceDriver=merged})]);using var stream=new MemoryStream();DatabaseFile.Write(stream,database);stream.Position=0;
            var copy=DatabaseFile.Read(stream).Assets[0].Scene!.FaceDriver!;if(JsonSerializer.Serialize(copy,WireJson.Options)!=JsonSerializer.Serialize(merged,WireJson.Options))throw new Exception("SRED dropped supplementary source metadata");
        });
        test("legacy Face DTO without supplementary field still validates",()=>{
            var json=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(face,WireJson.Options))!.AsObject();json.Remove("additionalSources");json.Remove("primaryBoneCount");json.Remove("primaryControlCount");
            var copy=JsonSerializer.Deserialize<FaceDriverRecord>(json.ToJsonString(),WireJson.Options)!;NativeFaceMorph.Validate(copy,scene);if(copy.AdditionalSources is not null)throw new Exception("Invented legacy ear provenance");
        });
        test("declared primary ranges protect unused bones and shader-only tail controls",()=>{
            var withTail=face with{Bones=[bone,new(11,40,"Root/Unused",3,[0,0,0],[0,0,0],[1,1,1])],Controls=[..face.Controls,new("ShaderTail",2,40,2,3,[],new("parameter",1,0,0,0))]};
            var target=earScene with{Bones=[..earScene.Bones,new("Unused",0,[0,0,0],[0,1,0],SourcePath:"Root/Unused")]};
            var merged=NativeFaceMorph.MergeSupplementary(withTail,ears,target);var source=merged.AdditionalSources![0];
            if(merged.PrimaryBoneCount!=2||merged.PrimaryControlCount!=3)throw new Exception("Primary boundary was not independently retained");
            foreach(var invalid in new[]{source with{BoneStart=1,BoneCount=3},source with{ControlStart=2,ControlCount=3},source with{BoneStart=1,BoneCount=3,ControlStart=2,ControlCount=3},source with{BoneStart=3,BoneCount=1}})
                reject(()=>NativeFaceMorph.Validate(merged with{AdditionalSources=[invalid]},target));
            reject(()=>NativeFaceMorph.Validate(merged with{PrimaryBoneCount=null,PrimaryControlCount=null},target));
            reject(()=>NativeFaceMorph.Validate(merged with{PrimaryBoneCount=int.MaxValue},target));
            reject(()=>NativeFaceMorph.Validate(face with{PrimaryBoneCount=1},scene));
        });
        test("authored face part classification follows native partType bits and preset groups",()=>{
            foreach(var (bit,category,rule) in new[]{(1,"EYE","part-type-bit-0x01"),(2,"EYE","part-type-bit-0x02"),(4,"BROW","part-type-bit-0x04"),(8,"BROW","part-type-bit-0x08"),(16,"MOUTH","part-type-bit-0x10"),(32,"SHADER","part-type-bit-0x20"),(64,"EAR","part-type-bit-0x40"),(128,"EAR","part-type-bit-0x80")}) {
                var part=NativeFaceMorph.ClassifyPart(bit);
                if(part.Category!=category||part.Rule!=rule||part.Source!="native-part-type"||part.Confidence!="inferred")throw new Exception("Native part bit classification differs: "+bit);
            }
            var unrecognized=NativeFaceMorph.ClassifyPart(3);
            if(unrecognized.Category!="UNKNOWN"||unrecognized.Confidence!="unknown")throw new Exception("Unrecognized part bit was not left unclassified");
            if(NativeFaceMorph.ClassifyPreset("a/skeletalmorphanim/emotion/x.asset").Category!="EMOTION")throw new Exception("Emotion preset group was not classified");
            if(NativeFaceMorph.ClassifyPreset("a/skeletalmorphanim/pose/x.asset").Category!="POSE")throw new Exception("Pose preset group was not classified");
            if(NativeFaceMorph.ClassifyPreset("a/other/x.asset").Category!="UNKNOWN")throw new Exception("Unknown preset group was not left unclassified");
        });
    }
}
