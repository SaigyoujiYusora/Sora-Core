using System.Globalization;
using System.Numerics;
using System.Text.Json;
namespace Sora.Core;

public sealed record NativeEquipmentAnimationSelection(string SlotId, string ResourceId, string AnimatorId, string ControllerId);
public sealed record NativeEquipmentAnimationIdentity(string OwnerAssetId, string CharacterId, string DeclarationId,
    string SlotId, string ResourceId, string ResourcePath, string AnimatorId, string AnimatorSourcePath,
    string ControllerId, string? AvatarId, string ManifestHash, string RigKind = "native-equipment-source-path");
public sealed record NativeEquipmentAnimationSchemaBinding(uint PathHash, int Attribute, int TypeId,
    int CustomType, int IsPPtrCurve, string? SourcePath, string Resolution);
public sealed record NativeEquipmentAnimationItem(string ResourcePath, string Cab, string PathId, string Name,
    NativeEquipmentAnimationIdentity Equipment, string OriginalSourceId, string[] ControllerChain,
    NativeEquipmentAnimationSchemaBinding[] BindingSchema, NativeAnimationSource BindingSchemaSource,
    string Status = "referenced-clip; sampling-and-binding-not-yet-validated",
    string ProofContract = NativeEquipmentAnimationService.ProofContract,
    NativeControllerStateClip[]? ControllerStates = null);
public sealed record NativeEquipmentAnimationBinding(uint PathHash, int Attribute, int Bone, string SourcePath);
public sealed record NativeEquipmentAnimationProof(NativeEquipmentAnimationIdentity Identity, string ClipId,
    string OriginalSourceId, string[] ControllerChain, int Samples, double SampleRate, double[] Times,
    NativeEquipmentAnimationBinding[] Bindings, string[] Diagnostics,
    string Scope = "single-native-generic-clip; no controller transitions, events, visibility or damping",
    string ProofContract = NativeEquipmentAnimationService.ProofContract);
public sealed record NativeEquipmentAnimationLoad(ClipRecord Clip, BoneRecord[] Bones, NativeEquipmentAnimationProof Equipment);

/// <summary>Catalog-authorized, source-bound dedicated equipment clip sampling; no client rig mapping.</summary>
public static partial class NativeEquipmentAnimationService
{
    public const string ProofContract = "native-equipment-clip-proof-v2";
    private sealed record Context(NativeEquipmentAnimationIdentity Identity, NativeHierarchyNode[] Nodes,
        NativeControllerClipReference[] Clips, ResolvedAsset Controller);
    private static JsonElement J(ResolvedAsset value) => JsonSerializer.SerializeToElement(value.Object.Data, WireJson.Options);
    private static JsonElement[] A(JsonElement value, string key)
    {
        var a = value.GetProperty(key).GetProperty("Array");
        Validation.Require(a.ValueKind == JsonValueKind.Array && a.GetArrayLength() <= 16384, "Equipment clip array exceeds bound: " + key);
        return a.EnumerateArray().ToArray();
    }
    private static Context Resolve(GameResources game, DatabaseDocument database, string ownerAsset,
        string resourcePath, NativeEquipmentAnimationSelection selection)
    {
        Validation.Require(selection is not null && database.CatalogSource is not null && GameCatalog.Matches(database, game),
            "Equipment animation requires a matching unified game catalog");
        var owner = Catalog.For(database).Get(ownerAsset);
        Validation.Require(owner.Kind == "character" && owner.Locator is { Parser: "character" }, "Equipment animation requires a catalog character owner");
        string ownerPath = owner.Locator!.Path;
        Validation.Require(GameCatalog.AddressId(game.SelectAddress(ownerPath, owner.Locator.Hash)) == owner.Id,
            "Catalog owner stable ID does not match its native prefab locator");
        Validation.Require(NativeAnimationService.CharacterToken(ownerPath) is not null, "Owner has no native character prefab identity");
        string character = Path.GetFileNameWithoutExtension(ownerPath)[..^"_uimodel".Length];
        Validation.Require(owner.Metadata?.RowId is null || owner.Metadata.RowId == character, "Catalog owner metadata disagrees with native prefab");
        var declaration = NativeCharacterEquipment.Read(game, character);
        var slots = declaration.DedicatedEquipment.Where(s => s.SlotId == selection!.SlotId).ToArray();
        Validation.Require(slots.Length == 1 && slots[0].ResourceId is not null && slots[0].ResourceId == selection!.ResourceId
            && slots[0].ResourcePath == resourcePath, "Equipment slot/resource identity does not belong to the selected owner");
        var nodes = NativePrefabHierarchy.Read(game, resourcePath);
        var entries = new List<(NativeHierarchyNode Node, ResolvedAsset Animator, ResolvedAsset Controller, string? Avatar)>();
        foreach (var n in nodes)
        foreach (var pointer in A(J(n.GameObject), "m_Component"))
        {
            var animator = game.Resolve(n.GameObject.Cab, pointer.GetProperty("component"));
            if (animator?.Object.ClassId != 95 || NativePrefabHierarchy.Identity(animator) != selection!.AnimatorId) continue;
            var data = J(animator); var controller = game.Resolve(animator.Cab, data.GetProperty("m_Controller"));
            Validation.Require(controller is not null && NativePrefabHierarchy.Identity(controller) == selection.ControllerId,
                "Equipment Animator/controller identity changed");
            var avatar = game.Resolve(animator.Cab, data.GetProperty("m_Avatar"));
            Validation.Require(avatar is null || avatar.Object.ClassId == 90, "Equipment Animator Avatar pointer has an unsupported type");
            entries.Add((n, animator, controller!, avatar is null ? null : NativePrefabHierarchy.Identity(avatar)));
        }
        Validation.Require(entries.Count == 1, "Selected Animator is absent or ambiguous in the exact equipment prefab");
        var e = entries[0];
        var identity = new NativeEquipmentAnimationIdentity(ownerAsset, character, declaration.SourceId, slots[0].SlotId,
            slots[0].ResourceId!, resourcePath, selection!.AnimatorId, e.Node.SourcePath, selection.ControllerId, e.Avatar, game.Manifest.Hash);
        OperationProgress.Report("resolve-equipment-animation", detail: identity.ResourcePath);
        return new(identity, nodes, NativeAnimationController.Read(game, e.Controller), e.Controller);
    }
    public static NativeEquipmentAnimationItem[] Discover(GameResources game, DatabaseDocument database, string ownerAsset,
        string resourcePath, NativeEquipmentAnimationSelection equipment)
    {
        var context = Resolve(game, database, ownerAsset, resourcePath, equipment);
        var states=NativeAnimationControllerStates.Read(game,context.Controller);
        return context.Clips.Select(c =>
        {
            var data = J(c.Clip); string pathId = c.Clip.Object.Id.ToString(CultureInfo.InvariantCulture);
            return new NativeEquipmentAnimationItem(resourcePath, c.Clip.Cab, pathId,
                data.GetProperty("m_Name").GetString()!, context.Identity, c.OriginalSourceId, c.ControllerChain,
                ReadBindingSchema(data, context.Nodes, context.Identity.AnimatorSourcePath),
                new(resourcePath, c.Clip.Cab, pathId, game.Manifest.Hash),
                ControllerStates:states.Where(s=>s.ClipId==NativePrefabHierarchy.Identity(c.Clip)).ToArray());
        }).ToArray();
    }
    /// <summary>Discovery proof from every raw native binding, including unsupported and unresolved rows.</summary>
    public static NativeEquipmentAnimationSchemaBinding[] ReadBindingSchema(JsonElement clip,
        NativeHierarchyNode[] nodes, string animatorPath)
    {
        Validation.Require(nodes.Length is > 0 and <= 16384 && !string.IsNullOrWhiteSpace(animatorPath),
            "Invalid equipment discovery hierarchy");
        var paths = nodes.Where(n => n.SourcePath == animatorPath || n.SourcePath.StartsWith(animatorPath + "/", StringComparison.Ordinal))
            .GroupBy(n => NativeEquipmentDefaultPoseReader.PathHash(n.SourcePath == animatorPath ? "" : n.SourcePath[(animatorPath.Length + 1)..]))
            .ToDictionary(g => g.Key, g => g.ToArray());
        return A(clip.GetProperty("m_ClipBindingConstant"), "genericBindings").Select(b =>
        {
            uint hash = b.GetProperty("path").GetUInt32(); paths.TryGetValue(hash, out var matches);
            string? sourcePath = matches is { Length: 1 } ? matches[0].SourcePath : null;
            return new NativeEquipmentAnimationSchemaBinding(hash, b.GetProperty("attribute").GetInt32(),
                b.GetProperty("typeID").GetInt32(), b.GetProperty("customType").GetInt32(), b.GetProperty("isPPtrCurve").GetInt32(),
                sourcePath, matches is null ? "unmapped-path-hash" : matches.Length == 1 ? "native-path" : "ambiguous-path-hash");
        }).ToArray();
    }
    public static NativeEquipmentAnimationLoad Import(GameResources game, DatabaseDocument database, string ownerAsset,
        string resourcePath, NativeEquipmentAnimationSelection equipment, NativeAnimationSelection selected)
    {
        Validation.Require(selected is not null && long.TryParse(selected.PathId, NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out _), "Select an exact equipment clip CAB and signed path ID");
        var context = Resolve(game, database, ownerAsset, resourcePath, equipment);
        var matches = context.Clips.Where(c => c.Clip.Cab == selected!.Cab
            && c.Clip.Object.Id.ToString(CultureInfo.InvariantCulture) == selected.PathId).ToArray();
        Validation.Require(matches.Length == 1, "Selected clip is absent or ambiguous in the equipment controller chain");
        var reference = matches[0]; var data = J(reference.Clip);
        // Parse and bind from native data, never the current Blender rig or a supplied bone array.
        var scene = NativeItemImport.Import(game, resourcePath).Assets.Single().Scene!;
        var converted = Convert(data, context.Nodes, scene, context.Identity.AnimatorSourcePath,
            new(resourcePath, selected!.Cab, selected.PathId, game.Manifest.Hash));
        return new(converted.Clip, scene.Bones, new(context.Identity, NativePrefabHierarchy.Identity(reference.Clip),
            reference.OriginalSourceId, reference.ControllerChain, converted.Times.Length, converted.Clip.Fps,
            converted.Times, converted.Bindings, []));
    }
    public sealed record Conversion(ClipRecord Clip, double[] Times, NativeEquipmentAnimationBinding[] Bindings);
    public static NativeEquipmentAnimationBinding[] Bindings(JsonElement clip, NativeHierarchyNode[] nodes,
        SceneDocument scene, string animatorPath, bool preserveUnbound = false)
    {
        Validation.Require(nodes.Length is > 0 and <= 16384 && scene.Bones.Length is > 0 and <= 4096,
            "Equipment animation hierarchy/bone bounds exceeded");
        var paths = nodes.Where(n => n.SourcePath == animatorPath || n.SourcePath.StartsWith(animatorPath + "/", StringComparison.Ordinal))
            .GroupBy(n => NativeEquipmentDefaultPoseReader.PathHash(n.SourcePath == animatorPath ? "" : n.SourcePath[(animatorPath.Length + 1)..]))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var result = new List<NativeEquipmentAnimationBinding>(); var errors = new List<string>(); var seen = new HashSet<(uint, int)>();
        // ACL clips carry their own Animator scalar bindings next to the Transform channels; only the
        // Transform channels describe the dedicated-equipment pose, and the ACL stream already carries them.
        bool acl = NativeEquipmentClipSampler.IsAcl(clip);
        foreach (var b in A(clip.GetProperty("m_ClipBindingConstant"), "genericBindings"))
        {
            uint hash = b.GetProperty("path").GetUInt32(); int attribute = b.GetProperty("attribute").GetInt32();
            string label = $"pathHash={hash} attribute={attribute}";
            int type = b.GetProperty("typeID").GetInt32();
            // Only the ACL Animator scalar bindings are known to sit next to the Transform channels; they
            // describe the root scalar stream, not the equipment pose, and nothing else is skipped silently.
            if (acl && type == 95) continue;
            if (type != 4 || b.GetProperty("customType").GetInt32() != 0 || b.GetProperty("isPPtrCurve").GetInt32() != 0 || attribute is < 1 or > 3)
            { errors.Add("unsupported binding " + label); continue; }
            if (!seen.Add((hash, attribute))) { errors.Add("duplicate binding " + label); continue; }
            if (!paths.TryGetValue(hash, out var matches) || matches.Length != 1)
            {
                if (matches is null && preserveUnbound) result.Add(new(hash, attribute, -1, ""));
                else errors.Add((matches is null ? "unmapped " : "ambiguous ") + label);
                continue;
            }
            var bones = scene.Bones.Select((bone, i) => (bone, i)).Where(x => x.bone.SourcePath == matches[0].SourcePath).ToArray();
            if (bones.Length != 1) { errors.Add("outside or ambiguous imported rig " + label + " sourcePath=" + matches[0].SourcePath); continue; }
            result.Add(new(hash, attribute, bones[0].i, matches[0].SourcePath));
        }
        Validation.Require(errors.Count == 0, "Equipment clip bindings rejected: " + string.Join("; ", errors.Take(64))
            + (errors.Count > 64 ? $"; {errors.Count - 64} additional diagnostics" : ""));
        Validation.Require(result.Count > 0, "Equipment clip has no supported Transform bindings");
        return result.ToArray();
    }
    public static Conversion Convert(JsonElement clip, NativeHierarchyNode[] nodes, SceneDocument scene,
        string animatorPath, NativeAnimationSource source)
    {
        var bindings = Bindings(clip, nodes, scene, animatorPath);
        OperationProgress.Report("validate-equipment-animation", detail: $"{source.Cab}:{source.PathId}");
        var sampler = NativeEquipmentClipSampler.Create(clip, bindings);
        double fps = clip.GetProperty("m_SampleRate").GetDouble();
        // Both rates are native float32 values; compare them in that value space so the shortest decimal
        // round-trip of the JSON projection cannot report a spurious mismatch.
        Validation.Require(double.IsFinite(fps) && fps is >= 1 and <= 240
            && (sampler.DenseSampleRate is null || (float)sampler.DenseSampleRate.Value == (float)fps),
            "Equipment clip sample rates differ or are unsupported");
        // The authored interval end is the clamp of the last native frame step, exactly as the equipment
        // timeline does it: the frame grid covers the interval and the exact end time is kept as the last key.
        int steps = (int)Math.Ceiling(sampler.Duration * fps);
        Validation.Require(steps >= 1 && steps <= 100000 && (long)(steps + 1) * scene.Bones.Length * 3 <= 1_000_000,
            "Equipment clip interval is unsupported or output exceeds bound");
        var times = Enumerable.Range(0, steps + 1).Select(i => Math.Min(i / fps, sampler.Duration)).Distinct().Order().ToArray();
        var tracks = Enumerable.Range(0, scene.Bones.Length).SelectMany(b => new[] { "location", "rotation", "scale" }.Select(c => (b, c)))
            .ToDictionary(k => k, _ => new List<KeyRecord>(times.Length));
        var previous = new Dictionary<int, Quaternion>();
        for (int frame = 0; frame < times.Length; frame++)
        {
            OperationProgress.Report("sample-equipment-animation", frame, times.Length, $"{source.Cab}:{source.PathId}");
            var values = sampler.Sample(times[frame]); int offset = 0;
            var channels = bindings.Select(b => { int size = b.Attribute == 2 ? 4 : 3; var c = new NativeEquipmentDefaultPoseReader.Channel(b.PathHash, b.Attribute, values[offset..(offset + size)]); offset += size; return c; }).ToArray();
            var pose = NativeEquipmentDefaultPoseReader.ConvertPose(nodes, scene, animatorPath, channels);
            foreach (var b in pose)
            {
                var a = b.BasisMatrix;
                var matrix = new Matrix4x4((float)a[0],(float)a[4],(float)a[8],(float)a[12],(float)a[1],(float)a[5],(float)a[9],(float)a[13],(float)a[2],(float)a[6],(float)a[10],(float)a[14],(float)a[3],(float)a[7],(float)a[11],(float)a[15]);
                Validation.Require(Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var location), "Equipment sampled basis cannot be decomposed");
                rotation = Quaternion.Normalize(rotation);
                var rebuilt = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(location);
                double[] r = [rebuilt.M11,rebuilt.M21,rebuilt.M31,rebuilt.M41,rebuilt.M12,rebuilt.M22,rebuilt.M32,rebuilt.M42,rebuilt.M13,rebuilt.M23,rebuilt.M33,rebuilt.M43,rebuilt.M14,rebuilt.M24,rebuilt.M34,rebuilt.M44];
                Validation.Require(r.All(double.IsFinite) && r.Zip(a,(x,y)=>Math.Abs(x-y)).Max() <= .0001, "Equipment sampled basis has unsupported shear or nonfinite TRS");
                if (previous.TryGetValue(b.Bone, out var q) && Quaternion.Dot(q, rotation) < 0) rotation = new(-rotation.X,-rotation.Y,-rotation.Z,-rotation.W);
                previous[b.Bone] = rotation;
                tracks[(b.Bone,"location")].Add(new(times[frame],[location.X,location.Y,location.Z]));
                tracks[(b.Bone,"rotation")].Add(new(times[frame],[rotation.X,rotation.Y,rotation.Z,rotation.W]));
                tracks[(b.Bone,"scale")].Add(new(times[frame],[scale.X,scale.Y,scale.Z]));
            }
        }
        var output = new ClipRecord(clip.GetProperty("m_Name").GetString()!, sampler.Duration, fps,
            tracks.Select(p => new TrackRecord(p.Key.b,p.Key.c,p.Value.ToArray())).ToArray(),
            new(source,[],["Native generic Transform clip on source frame grid; controller transitions, events, visibility and damping are not evaluated"]));
        Validation.Scene(scene with { Clips = [output] });
        OperationProgress.Report("sample-equipment-animation", times.Length, times.Length, "Clip and source bindings complete");
        return new(output,times,bindings);
    }
}
