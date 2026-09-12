using System.Text.Json;

namespace Sora.Core;

public sealed record NativePoseBone(int Slot, string Path, uint Hash, int SceneBoneIndex);
public sealed record NativePoseMap(string ManifestHash, string AvatarCab, string AvatarObject,
    string Source, NativePoseBone[] Bones);

/// <summary>Authoritative native25 identities only; constructed poses remain a frontend operation.</summary>
public static class NativePoseService
{
    public static NativePoseMap Map(GameResources resources, SceneDocument scene, string resourcePath)
    {
        Validation.Require(scene.Bones.Length > 0, "This asset has no native skeleton");
        ResolvedAsset avatar;
        if (scene.Npc is {} npc)
        {
            var declaration = NativeNpcImport.Parse(npc.Declaration);
            var template = resources.ResolveAddress(NativeNpcImport.DeclaredResource(resources, declaration.Template, "template"), 114);
            Validation.Require(template.Cab == npc.TemplateCab && template.Object.Id == npc.TemplateObject, "NPC template identity changed since import");
            avatar = resources.Resolve(template.Cab, Json(template.Object).GetProperty("sizeAvatar"))
                ?? throw new InvalidDataException("NPC common Avatar is missing");
            Validation.Require(avatar.Object.ClassId == 90 && avatar.Cab == npc.CommonAvatarCab
                && avatar.Object.Id == npc.CommonAvatarObject, "NPC common Avatar identity changed since import");
        }
        else if (scene.FaceDriver is {} face)
            avatar = NativeAnimationService.FaceAvatar(resources, face);
        else
        {
            avatar = NativeAnimationService.PrefabAvatar(resources, resourcePath);
            if (!NativeAnimationService.HasHumanSkeleton(Json(avatar.Object)))
            {
                var extractedFace = NativeFaceMorph.Extract(resources, resourcePath, avatar, scene)
                    ?? throw new InvalidDataException("No authoritative humanoid Avatar is available");
                avatar = NativeAnimationService.FaceAvatar(resources, extractedFace);
            }
        }
        var rig = NativeHumanoidRig.Parse(Json(avatar.Object));
        var mapped = new List<NativePoseBone>();
        // Slots 0..21 are the verified native25 body schema used by NativeHumanoidPose.
        for (int slot = 0; slot < 22; slot++)
        {
            var human = rig.Nodes[rig.HumanNodes[slot]];
            var matches = scene.Bones.Select((bone, index) => (bone, index))
                .Where(pair => pair.bone.SourcePath == human.Path && pair.bone.SourceHash == human.PathHash).ToArray();
            Validation.Require(matches.Length == 1, "Humanoid pose source path/hash is missing or ambiguous: " + human.Path);
            mapped.Add(new(slot, human.Path, human.PathHash, matches[0].index));
        }
        foreach (int[] chain in new[] { new[] {12,14,16,18}, new[] {13,15,17,19}, new[] {1,3,5,20}, new[] {2,4,6,21} })
            for (int i = 1; i < chain.Length; i++)
            {
                int parent = mapped[chain[i-1]].SceneBoneIndex;
                int cursor = scene.Bones[mapped[chain[i]].SceneBoneIndex].Parent;
                while (cursor >= 0 && cursor != parent) cursor = scene.Bones[cursor].Parent;
                Validation.Require(cursor == parent, "Humanoid chain is not preserved in the imported skeleton");
            }
        return new(resources.Manifest.Hash, avatar.Cab, avatar.Object.Id.ToString(),
            "Native Avatar m_Human.m_HumanBoneIndex; native25 body slots, exact scene path/hash join", mapped.ToArray());
    }
    private static JsonElement Json(SerializedObject value) => JsonSerializer.SerializeToElement(value.Data, WireJson.Options);
}
