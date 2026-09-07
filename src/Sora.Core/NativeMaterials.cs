using System.Globalization;
using System.Text.Json;
namespace Sora.Core;

// Independently authored native metadata transport. No shader expressions or graph assets.
public static class NativeMaterials
{
    public static string Classify(string? shader, IReadOnlyDictionary<string,double> floats) => shader switch {
        "HGRP/CharacterNPR" => floats.GetValueOrDefault("_UseCharacterFur") != 0 ? "Fur" : "Standard",
        "HGRP/CharacterNPR_Skin" => "Face", "HGRP/CharacterNPR_Eye" => "Eyes",
        "HGRP/CharacterNPR_Hair" => "Hair", "HGRP/CharacterNPR_VFX" => "VFX",
        "HGRP/CharacterNPR_OverlayShadow" => "OverlayShadow", "HGRP/CharacterNPR_LiquidAg" => "LiquidAg", _ => "Unknown" };
    public static string? ShaderName(JsonElement data) => data.TryGetProperty("m_ParsedForm", out var parsed) && parsed.TryGetProperty("m_Name", out var nested) && !string.IsNullOrWhiteSpace(nested.GetString()) ? nested.GetString() : data.TryGetProperty("m_Name", out var name) ? name.GetString() : null;
    public static string Usage(string slot) => slot is "_BumpMap" or "_NormalMap" or "_NormalTex" or "_SplitNormalMap" or "_NTexture" or "_NormalTexture" ? "normal" : slot is "_BaseMap" or "_MainTex" or "_BaseColorMap" or "_DiffuseTex" or "_DTexture" or "_DiffuseTexture" or "_EmissionMap" or "_ShadowLutTex" or "_MatcapTex" ? "color" : "raw";
    static JsonElement Json(SerializedObject value) => JsonSerializer.SerializeToElement(value.Data, WireJson.Options);
    static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);
    static IEnumerable<JsonElement> Array(JsonElement data, string key) {
        if (!data.TryGetProperty(key, out var value)) return [];
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("Array", out var inner)) value = inner;
        Validation.Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 4096, "Native array type or count invalid: " + key);
        return value.EnumerateArray();
    }
    static string[] Strings(JsonElement data, string key) => Array(data,key).Select(x => x.GetString()!).ToArray();
    // Legacy keyword strings are bounded before Split allocates its result.
    static string[] Legacy(JsonElement data) {
        if(!data.TryGetProperty("m_ShaderKeywords",out var value)) return [];
        var text=value.GetString()??"";
        Validation.Require(text.Length<=65536,"Native legacy keyword text limit exceeded");
        var count=0; var inside=false;
        foreach(var c in text) { if(char.IsWhiteSpace(c)) inside=false; else if(!inside) { Validation.Require(++count<=4096,"Native legacy keyword count exceeded"); inside=true; } }
        return text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
    }
    static double[] Vec(JsonElement data,string key,double[] fallback) => data.TryGetProperty(key,out var value) ? new[]{value.GetProperty("x").GetDouble(),value.GetProperty("y").GetDouble()} : fallback;
    public static MaterialNprDescriptor Extract(ResolvedAsset source, Func<string,JsonElement,ResolvedAsset?> resolve,
        Func<ResolvedAsset,string,string,TextureRecord> decode, Dictionary<string,TextureRecord> payloads, Dictionary<string,TextureDescriptor> descriptors)
    {
        var data=Json(source.Object); var saved=data.GetProperty("m_SavedProperties");
        var floats=Array(saved,"m_Floats").ToDictionary(x=>x.GetProperty("first").GetString()!,x=>x.GetProperty("second").GetDouble(),StringComparer.Ordinal);
        var ints=Array(saved,"m_Ints").ToDictionary(x=>x.GetProperty("first").GetString()!,x=>x.GetProperty("second").GetInt32(),StringComparer.Ordinal);
        var colors=Array(saved,"m_Colors").ToDictionary(x=>x.GetProperty("first").GetString()!,x=>new[]{"r","g","b","a"}.Select(c=>x.GetProperty("second").GetProperty(c).GetDouble()).ToArray(),StringComparer.Ordinal);
        Validation.Require(floats.Count<=4096 && ints.Count<=4096 && colors.Count<=4096 && floats.Values.All(double.IsFinite) && colors.Values.All(x=>x.All(double.IsFinite)), "Native property limits or nonfinite values");
        var diagnostics=new List<NprDiagnostic>(); ResolvedAsset? shader=null; string resolution="missing"; NativeReference? shaderSourceRef=null;
        if(data.TryGetProperty("m_Shader",out var pointer)) {
            shaderSourceRef=new(pointer.GetProperty("m_FileID").GetInt32(),Id(pointer.GetProperty("m_PathID").GetInt64()));
            Validation.Require(shaderSourceRef.FileId>=0,"Invalid shader file ID");
            resolution=shaderSourceRef.PathId=="0"?"null":"missing";
            try { if(resolution!="null") { shader=resolve(source.Cab,pointer); resolution=shader is null ? "missing" : shader.Object.ClassId==48 ? "resolved" : "unsupported"; } }
            catch(KeyNotFoundException e) { diagnostics.Add(new("shader_missing",null,e.Message)); }
        }
        string? shaderName=resolution=="resolved" ? ShaderName(Json(shader!.Object)) : null;
        var bindings=new Dictionary<string,NprTextureBinding>(StringComparer.Ordinal);
        foreach(var entry in Array(saved,"m_TexEnvs")) {
            Validation.Require(bindings.Count<4096,"Native texture slot limit exceeded");
            string slot=entry.GetProperty("first").GetString()!; var env=entry.GetProperty("second");var reference=env.GetProperty("m_Texture");
            long path=reference.GetProperty("m_PathID").GetInt64(); int file=reference.GetProperty("m_FileID").GetInt32();
            string status=path==0?"null":"missing"; string? resolvedId=null,textureId=null;
            if(path!=0) {
                try {
                    var asset=resolve(source.Cab,reference);
                    if(asset is not null) {
                        resolvedId=asset.Cab+":"+Id(asset.Object.Id); string usage=Usage(slot); string identity=resolvedId+":"+usage;
                        if(!descriptors.TryGetValue(identity,out var descriptor)) {
                            Validation.Require(descriptors.Count<512,"Native texture descriptor limit exceeded");
                            var td=Json(asset.Object); int Num(string key,int fallback=0)=>td.TryGetProperty(key,out var v)?v.GetInt32():fallback;
                            string? Sample(string key) => td.TryGetProperty("m_TextureSettings",out var settings)&&settings.TryGetProperty(key,out var v)?v.GetInt32().ToString(CultureInfo.InvariantCulture):null;
                            int format=Num("m_TextureFormat"); bool supported=asset.Object.ClassId==28 && format is 4 or 10 or 12 or 25 or 26 or 27;
                            descriptor=new(identity,new(asset.Cab,Id(asset.Object.Id)),asset.Object.ClassId==28?"2D":asset.Object.ClassId==89?"Cube":"Unknown",Num("m_Width"),Num("m_Height"),format,Num("m_ColorSpace",-1)==0?"linear":Num("m_ColorSpace",-1)==1?"sRGB":"unknown",Sample("m_WrapU"),Sample("m_WrapV"),Sample("m_FilterMode"),format is 26 ? "R" : format is 27 ? "RG" : format is 24 ? "RGB" : format is 4 or 10 or 12 or 25 ? "RGBA" : "unknown", format is 24 or 26 or 27 ? "none" : format is 4 or 10 or 12 or 25 ? "straight" : "unknown",null,"unsupported");
                            if(supported) {Validation.Require(payloads.Count<256,"Native texture payload count exceeded"); var texture=decode(asset,identity,usage); Validation.Require(payloads.Values.Sum(x=>(long)x.Png.Length)+texture.Png.Length<=192L*1024*1024,"Native texture payload budget exceeded"); payloads.Add(identity,texture);descriptor=descriptor with{PayloadRef=identity,DecodeStatus="decoded"};}
                            descriptors.Add(identity,descriptor);
                        }
                        textureId=identity; status=descriptor.DecodeStatus=="decoded"?"resolved":"unsupported";
                        if(status=="unsupported") diagnostics.Add(new("texture_unsupported",slot,descriptor.NativeFormat==24?"BC6H HDR transport is unsupported; an 8-bit PNG would discard native HDR values":"Native texture dimension or format has no supported PNG decoder"));
                    }
                } catch(KeyNotFoundException e) { diagnostics.Add(new("texture_missing",slot,e.Message)); }
            }
            bindings.Add(slot,new(new(file,Id(path)),resolvedId,status,Vec(env,"m_Scale",[1,1]),Vec(env,"m_Offset",[0,0]),env.TryGetProperty("m_UVSet",out var uv)?uv.GetInt32():0,textureId,env.TryGetProperty("m_Scale",out _),env.TryGetProperty("m_Offset",out _),env.TryGetProperty("m_UVSet",out _)));
        }
        var tags=Array(data,"stringTagMap").ToDictionary(x=>x.GetProperty("first").GetString()!,x=>x.GetProperty("second").GetString()!,StringComparer.Ordinal);
        string part=Classify(shaderName,floats);
        if(part=="Unknown") diagnostics.Add(new("shader_unknown",null,"No verified NPR part mapping for the resolved shader identity"));
        return new(1,new(new(source.Cab,Id(source.Object.Id)),shader is null?null:new(shader.Cab,Id(shader.Object.Id)),shaderName,resolution,shaderSourceRef),part,
            new(shaderName,shaderName=="HGRP/CharacterNPR"&&floats.ContainsKey("_UseCharacterFur")?"_UseCharacterFur":null,shaderName=="HGRP/CharacterNPR"&&floats.TryGetValue("_UseCharacterFur",out var fur)?fur:null),floats,ints,colors,bindings,
            new(Strings(data,"m_ValidKeywords"),Strings(data,"m_InvalidKeywords"),Legacy(data)),
            new(data.TryGetProperty("m_CustomRenderQueue",out var queue)?queue.GetInt32():-1,tags,Strings(data,"disabledShaderPasses"),data.TryGetProperty("m_DoubleSidedGI",out var gi)&&gi.GetBoolean()),diagnostics.ToArray());
    }
}
