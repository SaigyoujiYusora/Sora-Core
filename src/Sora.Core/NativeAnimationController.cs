using System.Text.Json;

namespace Sora.Core;

public sealed record NativeControllerClipReference(string OriginalSourceId, ResolvedAsset Clip, string[] ControllerChain);

/// <summary>Resolves native AnimatorController / AnimatorOverrideController edges without inferring clip ownership from names.</summary>
public static class NativeAnimationController
{
    private static JsonElement Json(SerializedObject value)=>value.ClassId==91?NativeObjectProjection.Json(value,"m_AnimationClips"):NativeObjectProjection.Json(value,"m_Controller","m_Clips");
    private static JsonElement[] Array(JsonElement value,string name)=>value.GetProperty(name).GetProperty("Array").EnumerateArray().ToArray();
    public static NativeControllerClipReference[] Read(GameResources game,ResolvedAsset controller)
    {
        if(controller is null)throw new InvalidDataException("Animator controller is missing");
        var visited=new HashSet<string>();
        Dictionary<string,NativeControllerClipReference> Visit(ResolvedAsset current,int depth) {
            string id=NativePrefabHierarchy.Identity(current);Validation.Require(depth<16&&visited.Add(id),"Cyclic or excessive Animator controller graph");var data=Json(current.Object);
            if(current.Object.ClassId==91) {
                var output=new Dictionary<string,NativeControllerClipReference>(StringComparer.Ordinal);var pointers=Array(data,"m_AnimationClips");Validation.Require(pointers.Length<=4096,"Controller clip limit exceeded");
                foreach(var pointer in pointers) {
                    var clip=game.Resolve(current.Cab,pointer);if(clip is null)continue;Validation.Require(clip.Object.ClassId==74,"Controller reference is not an AnimationClip");
                    string clipId=NativePrefabHierarchy.Identity(clip);output.TryAdd(clipId,new(clipId,clip,[id]));
                }
                return output;
            }
            Validation.Require(current.Object.ClassId==221,"Unsupported Animator controller class: "+current.Object.ClassId);
            var parent=game.Resolve(current.Cab,data.GetProperty("m_Controller"))??throw new InvalidDataException("Override controller has no base controller");
            var result=Visit(parent,depth+1);var seenOverrides=new HashSet<string>();var overrides=Array(data,"m_Clips");Validation.Require(overrides.Length<=4096,"Controller override limit exceeded");
            foreach(var row in overrides) {
                var original=game.Resolve(current.Cab,row.GetProperty("m_OriginalClip"));var replacement=game.Resolve(current.Cab,row.GetProperty("m_OverrideClip"));if(original is null&&replacement is null)continue;
                Validation.Require(original is not null&&original.Object.ClassId==74,"Override controller original clip is invalid");string originalId=NativePrefabHierarchy.Identity(original!);
                Validation.Require(seenOverrides.Add(originalId)&&result.ContainsKey(originalId),"Duplicate or unknown original controller clip");
                if(replacement is null)continue;Validation.Require(replacement.Object.ClassId==74,"Override target is not an AnimationClip");
                result[originalId]=result[originalId] with{Clip=replacement};
            }
            return result.ToDictionary(pair=>pair.Key,pair=>pair.Value with{ControllerChain=[id,..pair.Value.ControllerChain]},StringComparer.Ordinal);
        }
        return Visit(controller,0).Values.ToArray();
    }
}
