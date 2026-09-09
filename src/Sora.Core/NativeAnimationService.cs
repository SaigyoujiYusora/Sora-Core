using System.Text.Json;

namespace Sora.Core;

public sealed record NativeAnimationItem(string Id, string Path, string Label);
public sealed record NativeAnimationLoad(ClipRecord Clip, BoneRecord[] Bones);

public static class NativeAnimationService
{
    public static NativeAnimationItem[] Search(GameResources resources, string query, string? characterPath = null, int limit = 200)
    {
        var addressable = Search(resources.Manifest, query, characterPath, limit);
        if (CharacterToken(characterPath) is null) return addressable;
        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var controller = NativeAnimationClip.DiscoverControllerClips(resources, characterPath!)
            .Where(clip => terms.All(term => clip.ResourcePath.Contains(term, StringComparison.OrdinalIgnoreCase)
                || clip.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(clip => new NativeAnimationItem(clip.ResourcePath, clip.ResourcePath, Path.GetFileNameWithoutExtension(clip.ResourcePath)));
        return addressable.Concat(controller).DistinctBy(item => item.Path, StringComparer.Ordinal)
            .OrderBy(item => item.Path, StringComparer.Ordinal).Take(limit).ToArray();
    }

    public static NativeAnimationSubclip[] Discover(GameResources resources, string resourcePath, string? characterPath = null)
    {
        if (resources.Manifest.Assets.Any(asset => asset.Path == resourcePath))
            return NativeAnimationClip.DiscoverSubclips(resources, resourcePath);
        Validation.Require(CharacterToken(characterPath) is not null, "Select the imported character for its controller animations");
        var clips = NativeAnimationClip.DiscoverControllerClips(resources, characterPath!)
            .Where(clip => clip.ResourcePath == resourcePath).ToArray();
        Validation.Require(clips.Length > 0, "Animation resource is not referenced by this character controller");
        return clips;
    }

    public static NativeAnimationItem[] Search(NativeManifest manifest, string query, string? characterPath = null, int limit = 200)
    {
        Validation.Require(query is not null && query.Length <= 512 && !query.Any(char.IsControl), "Invalid animation query");
        Validation.Require(limit is > 0 and <= 1000, "Invalid animation result limit");
        string? character = CharacterToken(characterPath);
        string[] terms = query!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return manifest.Assets.Where(asset => (asset.Path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)
                || asset.Path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) && asset.Path.Contains("/animations/", StringComparison.OrdinalIgnoreCase))
                && (character is null || asset.Path.Contains("_" + character + "_", StringComparison.OrdinalIgnoreCase)
                    || asset.Path.Contains("/" + character + "/", StringComparison.OrdinalIgnoreCase))
                && terms.All(term => asset.Path.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(asset => asset.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Take(limit).Select(path => new NativeAnimationItem(path, path, Path.GetFileNameWithoutExtension(path))).ToArray();
    }

    public static NativeAnimationLoad Import(GameResources resources, DatabaseDocument database, string assetId,
        string resourcePath, string? avatarResource = null, string? workerExecutable = null, NativeAnimationSelection? selection = null)
    {
        var asset = new Catalog(database).Get(assetId);
        var scene = asset.Scene ?? throw new InvalidDataException("Asset has no decoded scene");
        Validation.Require(scene.Bones.Length > 0 && scene.Bones.All(bone => bone.SourcePath is not null && bone.SourceHash.HasValue),
            "Native animation requires the imported rig's original bone paths and hashes");
        if (database.ResourceIndex is { } index)
            Validation.Require(index.ManifestHash == resources.Manifest.Hash, "Game resources changed since this rig was imported; reimport the character");
        ResolvedAsset avatar;
        if (!string.IsNullOrWhiteSpace(avatarResource))
            avatar = ResolveResource(resources, avatarResource, 90);
        else if(scene.Npc is {} npc)
        {
            var declaration=NativeNpcImport.Parse(npc.Declaration);
            var template=resources.ResolveAddress(NativeNpcImport.DeclaredResource(resources,declaration.Template,"template"),114);
            Validation.Require(template.Cab==npc.TemplateCab&&template.Object.Id==npc.TemplateObject,"NPC template identity changed since import");
            avatar=resources.Resolve(template.Cab,Json(template.Object).GetProperty("sizeAvatar"))??throw new InvalidDataException("NPC common Avatar is missing");
            Validation.Require(avatar.Object.ClassId==90&&avatar.Cab==npc.CommonAvatarCab&&avatar.Object.Id==npc.CommonAvatarObject,"NPC common Avatar identity changed since import");
        }
        else
        {
            var face = scene.FaceDriver;
            if (face is null)
            {
                var animatorAvatar = PrefabAvatar(resources, asset.Id);
                var data = Json(animatorAvatar.Object);
                if (HasHumanSkeleton(data))
                    avatar = animatorAvatar;
                else
                {
                    face = NativeFaceMorph.Extract(resources, asset.Id, animatorAvatar, scene)
                        ?? throw new InvalidDataException("No authoritative humanoid Avatar found; provide an Avatar resource");
                    avatar = FaceAvatar(resources, face);
                }
            }
            else avatar = FaceAvatar(resources, face);
        }
        var rig = NativeHumanoidRig.Parse(Json(avatar.Object));
        if (!resources.Manifest.Assets.Any(item => item.Path == resourcePath))
        {
            var clips = Discover(resources, resourcePath, asset.Id);
            Validation.Require(selection is not null && clips.Any(clip => clip.Cab == selection.Cab && clip.PathId == selection.PathId),
                "Select an exact clip referenced by this character controller");
        }
        // NPC scale is authored outside the Avatar. Validate/solve in its original
        // unscaled native rest frame, then scale only exported location keys.
        double npcScale=scene.Npc is {} source?NativeNpcImport.Parse(source.Declaration).Scale:1;
        var bindingScene=scene;
        if(npcScale!=1)
        {
            double[] Unscale(double[] v)=>v.Select(x=>x/npcScale).ToArray();
            double[] Rest(double[] v){var r=(double[])v.Clone();r[3]/=npcScale;r[7]/=npcScale;r[11]/=npcScale;return r;}
            bindingScene=scene with {Bones=scene.Bones.Select(b=>b with {Head=Unscale(b.Head),Tail=Unscale(b.Tail),RestMatrix=Rest(b.RestMatrix!)}).ToArray(),HeadReference=scene.HeadReference is {} head?head with{RestMatrix=Rest(head.RestMatrix)}:null};
        }
        var conversion = NativeAnimationClip.Import(resources, resourcePath, rig, bindingScene, workerExecutable, selection);
        if(npcScale!=1)conversion=conversion with {Clip=conversion.Clip with {Tracks=conversion.Clip.Tracks.Select(t=>t.Channel=="location"?t with {Keys=t.Keys.Select(k=>k with {Value=k.Value.Select(v=>v*npcScale).ToArray()}).ToArray()}:t).ToArray()}};
        Validation.Require(conversion.Clip.Native is not null && conversion.Clip.Native.Source is not null,
            "Native animation conversion did not retain its source metadata");
        return new(conversion.Clip, scene.Bones);
    }

    public static string? CharacterToken(string? resourcePath)
    {
        if (resourcePath is null) return null;
        string name = Path.GetFileNameWithoutExtension(resourcePath);
        const string suffix = "_uimodel";
        if (!name.StartsWith("chr_", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        int separator = name.IndexOf('_', 4);
        if (separator <= 4 || !name.AsSpan(4, separator - 4).ToString().All(char.IsAsciiDigit)) return null;
        string token = name[(separator + 1)..^suffix.Length];
        return token.Length > 0 ? token : null;
    }

    private static ResolvedAsset FaceAvatar(GameResources resources, FaceDriverRecord face)
    {
        var host = ResolveResource(resources, face.SourcePath, 114);
        Validation.Require(host.Cab == face.SourceCab && host.Object.Id == face.SourceObject,
            "Authored Avatar source identity changed since import");
        var avatar = resources.Resolve(host.Cab, Json(host.Object).GetProperty("avatar"));
        Validation.Require(avatar is not null && avatar.Object.ClassId == 90
            && avatar.Cab == face.AvatarCab && avatar.Object.Id == face.AvatarObject, "Authored humanoid Avatar identity changed");
        return avatar!;
    }

    private static ResolvedAsset PrefabAvatar(GameResources resources, string resourcePath)
    {
        var address = Address(resources, resourcePath);
        var animators = resources.LoadClosure(address.Bundle).SelectMany(cab =>
        {
            var document = resources.GetDocument(cab);
            return document.Objects.Where(obj => obj.ClassId == 95).Select(obj => new ResolvedAsset(cab, document, obj));
        }).ToArray();
        Validation.Require(animators.Length == 1, "Character prefab must identify one Animator");
        var avatar = resources.Resolve(animators[0].Cab, Json(animators[0].Object).GetProperty("m_Avatar"));
        Validation.Require(avatar is not null && avatar.Object.ClassId == 90, "Character Animator has no Avatar");
        return avatar!;
    }

    private static ResolvedAsset ResolveResource(GameResources resources, string path, int classId)
    {
        var address = Address(resources, path);
        var found = new Dictionary<(string, long), ResolvedAsset>();
        foreach (string cab in resources.LoadClosure(address.Bundle))
            foreach (var container in resources.GetDocument(cab).Objects.Where(obj => obj.ClassId == 142))
                foreach (var row in NativeHumanoidRig.ArrayField(Json(container), "m_Container", 1000000))
                    if (row.GetProperty("first").GetString() == path)
                    {
                        var target = resources.Resolve(cab, row.GetProperty("second").GetProperty("asset"));
                        Validation.Require(target is not null && target.Object.ClassId == classId, "Native resource has an unexpected type");
                        found.TryAdd((target!.Cab, target.Object.Id), target);
                    }
        Validation.Require(found.Count == 1, "Native resource target is absent or ambiguous");
        return found.Values.Single();
    }

    private static AddressResource Address(GameResources resources, string path)
    {
        Validation.Require(!string.IsNullOrWhiteSpace(path) && path.Length <= 4096, "Select an exact native resource path");
        var matches = resources.Manifest.Assets.Where(asset => asset.Path == path).DistinctBy(asset => (asset.Path, asset.Bundle)).ToArray();
        Validation.Require(matches.Length == 1, "Native resource path is absent or ambiguous");
        return matches[0];
    }

    private static bool HasHumanSkeleton(JsonElement avatar) => avatar.GetProperty("m_Avatar").GetProperty("m_Human").GetProperty("data")
        .GetProperty("m_Skeleton").GetProperty("data").GetProperty("m_Node").GetProperty("Array").GetArrayLength() > 0;
    private static JsonElement Json(SerializedObject value) => JsonSerializer.SerializeToElement(value.Data, WireJson.Options);
}
