using System.Text.Json;
using Sora.Core;
internal static class NprTests
{
    public static void Run(Action<string,Action> test,Action<Action> reject)
    {
        test("NPR shader identity exact classification and fur override",()=> {
            if(NativeMaterials.Classify("HGRP/CharacterNPR",new Dictionary<string,double>{{"_UseCharacterFur",1}})!="Fur" || NativeMaterials.Classify("M_actor_hair",new Dictionary<string,double>())!="Unknown") throw new Exception();
            using var shader=JsonDocument.Parse("{\"m_Name\":\"container\",\"m_ParsedForm\":{\"m_Name\":\"HGRP/CharacterNPR_Hair\"}}");
            if(NativeMaterials.ShaderName(shader.RootElement)!="HGRP/CharacterNPR_Hair") throw new Exception();
            if(NativeMaterials.Usage("_DiffRampMap")!="raw" || NativeMaterials.Usage("_ShadowLutTex")!="color" || NativeMaterials.Usage("_BumpMap")!="normal") throw new Exception();
        });
        test("native NPR extraction resolves shader and preserves unsupported cubemap",()=> {
            var data=JsonSerializer.Deserialize<JsonElement>("""
            {"m_Shader":{"m_FileID":0,"m_PathID":9},"m_SavedProperties":{"m_Floats":{"Array":[{"first":"_UseCharacterFur","second":1}]},"m_Ints":{"Array":[{"first":"_Mode","second":2}]},"m_Colors":{"Array":[{"first":"_HDR","second":{"r":8,"g":-1,"b":2,"a":3}}]},"m_TexEnvs":{"Array":[{"first":"IBL_CharMaxCubemap","second":{"m_Texture":{"m_FileID":0,"m_PathID":10},"m_Scale":{"x":2,"y":3},"m_Offset":{"x":-1,"y":0.5}}},{"first":"_Null","second":{"m_Texture":{"m_FileID":0,"m_PathID":0}}},{"first":"_Missing","second":{"m_Texture":{"m_FileID":0,"m_PathID":11}}}]}},"m_ValidKeywords":{"Array":["FUR"]}}
            """);
            var doc=new SerializedDocument("test",[],[]);
            var source=new ResolvedAsset("cab",doc,new(-9007199254740993,21,data));
            var payloads=new Dictionary<string,TextureRecord>(); var descriptors=new Dictionary<string,TextureDescriptor>();
            var result=NativeMaterials.Extract(source,(_,pointer)=>pointer.GetProperty("m_PathID").GetInt64() switch {
                9=>new("cab",doc,new(9,48,new{m_ParsedForm=new{m_Name="HGRP/CharacterNPR"}})),
                10=>new("cab",doc,new(10,89,new{m_Width=16,m_Height=16,m_TextureFormat=10})),
                _=>throw new KeyNotFoundException("missing")
            },(_,_,_)=>throw new Exception("Cubemap must not decode as 2D"),payloads,descriptors);
            if(result.Part!="Fur" || result.Source.MaterialId.PathId!="-9007199254740993" || result.Textures["_Null"].Status!="null" || result.Textures["_Missing"].Status!="missing" || result.Textures["IBL_CharMaxCubemap"].Status!="unsupported" || result.Colors["_HDR"][0]!=8 || result.Keywords.Valid[0]!="FUR" || descriptors.Values.Single().Dimension!="Cube") throw new Exception();
            Validation.Scene(new("test",[],[],[new("m",[1,1,1,1],0,0.5,Npr:result)],[],[],descriptors.Values.ToArray()));
        });
        test("native shader unresolved pointer and texture field presence survive transport",()=> {
            var doc=new SerializedDocument("test",[],[]);
            MaterialNprDescriptor Extract(object data,Func<string,JsonElement,ResolvedAsset?> resolve) => NativeMaterials.Extract(new("cab",doc,new(1,21,data)),resolve,(_,_,_)=>throw new Exception(),new(),new());
            var missing=Extract(new{m_Shader=new{m_FileID=4,m_PathID=-77L},m_SavedProperties=new{}},(_,_)=>null);
            if(missing.Source.ResolutionStatus!="missing" || missing.Source.ShaderSourceRef!=new NativeReference(4,"-77")) throw new Exception("Lost unresolved shader pointer");
            var absent=Extract(new{m_SavedProperties=new{}},(_,_)=>throw new Exception());
            var nil=Extract(new{m_Shader=new{m_FileID=0,m_PathID=0},m_SavedProperties=new{}},(_,_)=>throw new Exception("Null pointer should not resolve"));
            if(absent.Source.ShaderSourceRef is not null || absent.Source.ResolutionStatus!="missing" || nil.Source.ResolutionStatus!="null") throw new Exception();
            using var stream=new MemoryStream();
            DatabaseFile.Write(stream,new("v",[new("a","a","","character",[],new("scene",[],[],[new("m",[1,1,1,1],0,0.5,Npr:missing)],[]))]));
            stream.Position=0;
            if(DatabaseFile.Read(stream).Assets[0].Scene!.Materials[0].Npr!.Source.ShaderSourceRef!=missing.Source.ShaderSourceRef) throw new Exception();
            var scene=new SceneDocument("s",[],[],[new("m",[1,1,1,1],0,0.5,Npr:missing)],[]);
            reject(()=>Validation.Scene(scene with{Materials=[scene.Materials[0] with{Npr=missing with{Source=missing.Source with{ResolutionStatus="null"}}}]}));
            reject(()=>Validation.Scene(scene with{Materials=[scene.Materials[0] with{Npr=missing with{Source=missing.Source with{ShaderSourceRef=new(-1,"77")}}}]}));
        });
        test("native BC4 BC5 channel semantics and BC6 HDR rejection",()=> {
            foreach(var format in new[]{26,27,24}) {
                var doc=new SerializedDocument("test",[],[]);
                var data=JsonSerializer.Deserialize<JsonElement>("""
                {"m_SavedProperties":{"m_TexEnvs":{"Array":[{"first":"_BumpMap","second":{"m_Texture":{"m_FileID":0,"m_PathID":2}}},{"first":"_Explicit","second":{"m_Texture":{"m_FileID":0,"m_PathID":0},"m_Scale":{"x":1,"y":1},"m_Offset":{"x":0,"y":0},"m_UVSet":0}}]}}}
                """);
                var descriptors=new Dictionary<string,TextureDescriptor>(); var payloads=new Dictionary<string,TextureRecord>(); var decoded=false;
                var result=NativeMaterials.Extract(new("cab",doc,new(1,21,data)),(_,_)=>new("cab",doc,new(2,28,new{m_Width=1,m_Height=1,m_TextureFormat=format})),(_,id,_)=>{decoded=true;return new(id,1,1,true,TexturePixels.Png(1,1,[1,2,3,255]));},payloads,descriptors);
                var descriptor=descriptors.Values.Single(); var binding=result.Textures["_BumpMap"]; var explicitBinding=result.Textures["_Explicit"];
                if(descriptor.Channels!=(format==26?"R":format==27?"RG":"RGB") || descriptor.AlphaMode!="none" || decoded!=(format!=24)) throw new Exception();
                if(binding.ScalePresent!=false || binding.OffsetPresent!=false || binding.UvSetPresent!=false || explicitBinding.ScalePresent!=true || explicitBinding.OffsetPresent!=true || explicitBinding.UvSetPresent!=true) throw new Exception("Native field presence lost");
                if(format==24 && (binding.Status!="unsupported" || descriptor.PayloadRef is not null || !result.Diagnostics.Any(x=>x.Message.Contains("HDR")))) throw new Exception();
                var scene=new SceneDocument("s",[],[],[new("m",[1,1,1,1],0,0.5,Npr:result)],[],payloads.Values.ToArray(),descriptors.Values.ToArray()); Validation.Scene(scene);
                reject(()=>Validation.Scene(scene with{TextureDescriptors=[descriptor with{Channels="RGBA"}]}));
                reject(()=>Validation.Scene(scene with{Materials=[scene.Materials[0] with{Npr=result with{Textures=new(){{"bad",binding with{Scale=[2,1]}}}}}]}));
            }
        });
        test("native property collections reject oversized raw arrays before resolving",()=> {
            var doc=new SerializedDocument("test",[],[]);
            foreach(var key in new[]{"m_Floats","m_Ints","m_Colors","m_TexEnvs"}) {
                var saved=new Dictionary<string,object>{{key,new{Array=Enumerable.Repeat(new{first="duplicate",second=0},4097).ToArray()}}};
                reject(()=>NativeMaterials.Extract(new("cab",doc,new(1,21,new{m_SavedProperties=saved})),(_,_)=>throw new Exception("Resolved before prevalidation"),(_,_,_)=>throw new Exception(),new(),new()));
            }
        });
        test("NPR metadata preserves HDR signed IDs null missing ST UV keywords and integers",()=> {
            var npr=Fixture(); var scene=new SceneDocument("npr",[],[],[new("m",[1,1,1,1],0,0.5,Npr:npr)],[]);
            var db=new DatabaseDocument("test",[new("id","label","","character",[],scene)]);
            using var stream=new MemoryStream(); DatabaseFile.Write(stream,db);stream.Position=0; var result=DatabaseFile.Read(stream).Assets[0].Scene!.Materials[0].Npr!;
            if(JsonSerializer.Serialize(npr,WireJson.Options)!=JsonSerializer.Serialize(result,WireJson.Options)) throw new Exception("Lossy descriptor");
            reject(()=>Validation.Scene(scene with{Materials=[scene.Materials[0] with{Npr=npr with{Floats=new(){{"bad",double.NaN}}}}]}));
            reject(()=>Validation.Scene(scene with{Materials=[scene.Materials[0] with{Npr=npr with{Textures=new(){{"slot",new(new(0,"7"),"cab:7","resolved",[1,1],[0,0],0,"absent")}}}}]}));
            reject(()=>Validation.Scene(scene with{Materials=[scene.Materials[0] with{Npr=npr with{Source=npr.Source with{MaterialId=new("cab","9223372036854775808")}}}]}));
        });
    }
    static MaterialNprDescriptor Fixture()=>new(1,new(new("cab","-9223372036854775808"),new("cab","9223372036854775807"),"HGRP/CharacterNPR_Hair","resolved"),"Hair",new("HGRP/CharacterNPR_Hair"),
        new(){{"_ZTest",3},{"_MissingVsZero",0}},new(){{"integer",-17}},new(){{"_Hdr",[8,-2,0.5,3]}},
        new(){{"_Null",new(new(0,"0"),null,"null",[2,3],[-1,0.5],2,null)},{"_Missing",new(new(4,"-77"),null,"missing",[1,1],[0,0],0,null)}},
        new(["VALID"],["INVALID"],["LEGACY"]),new(2450,new(){{"RenderType","Transparent"}},["ShadowCaster"],true),[]);
}
