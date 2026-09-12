using System.Text.Json;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sora.Core;

public static class NativeCharacterImport
{
    public static DatabaseDocument Import(GameResources resources, string query, AddressResource? selectedPrefab = null)
    {
        Validation.Require(!string.IsNullOrWhiteSpace(query), "Enter a character name or prefab path");
        var candidates = resources.Manifest.Assets.Where(x => x.Path.EndsWith("_uimodel.prefab", StringComparison.OrdinalIgnoreCase) && x.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(x => (x.Hash, x.Path)).ToArray();
        Validation.Require(selectedPrefab is not null || candidates.Length == 1, "Character selection is absent or ambiguous");
        if(selectedPrefab is not null)Validation.Require(selectedPrefab.Path.Contains(query,StringComparison.OrdinalIgnoreCase)&&resources.Manifest.Assets.Contains(selectedPrefab),"Selected character locator is not in the current manifest");
        var prefab = selectedPrefab ?? resources.SelectAddress(candidates[0].Path,candidates[0].Hash.ToString("x16"));
        var rootCabs = resources.LoadClosure(prefab.Bundle);
        var actorDocs = rootCabs.Select(cab => (Cab: cab, Document: resources.GetDocument(cab))).Where(x => x.Document.Objects.Any(y => y.ClassId == 95)).ToArray();
        Validation.Require(actorDocs.Length == 1, "Character prefab must contain one animator document");
        var actor = actorDocs[0];
        var animators = actor.Document.Objects.Where(x => x.ClassId == 95).ToArray();
        Validation.Require(animators.Length == 1, "Character prefab has multiple animators");
        var avatar = resources.Resolve(actor.Cab, Json(animators[0]).GetProperty("m_Avatar")) ?? throw new InvalidDataException("Character Avatar is missing");
        Validation.Require(avatar.Object.ClassId == 90, "Animator reference is not an Avatar");
        var hierarchy=NativePrefabHierarchy.Read(resources,resources.ResolveAddress(prefab,1));
        var hierarchyByObject=hierarchy.ToDictionary(node=>NativePrefabHierarchy.Identity(node.GameObject),StringComparer.Ordinal);
        var meshTransforms=new Dictionary<string,Matrix4x4>(StringComparer.Ordinal);
        var meshes = new List<SerializedObject>();
        var meshIdentities = new Dictionary<string, string>(StringComparer.Ordinal);
        var meshMaterials = new Dictionary<string, ResolvedAsset[]>(StringComparer.Ordinal);
        foreach (var renderer in actor.Document.Objects.Where(x => x.ClassId == 137))
        {
            OperationProgress.Report("decode-mesh", meshes.Count);
            var data = Json(renderer);
            if (!data.GetProperty("m_Enabled").GetBoolean()) continue;
            var mesh = resources.Resolve(actor.Cab, data.GetProperty("m_Mesh")) ?? throw new InvalidDataException("Renderer mesh is missing");
            Validation.Require(mesh.Object.ClassId == 43, "Renderer reference is not a Mesh");
            var node = JsonSerializer.SerializeToNode(mesh.Object.Data, WireJson.Options)!;
            var stream = node["m_StreamData"];
            if (stream is not null && stream["size"]!.GetValue<long>() > 0)
            {
                var bytes = resources.ReadResource(mesh.Cab, stream["path"]!.GetValue<string>(), stream["offset"]!.GetValue<long>(), checked((int)stream["size"]!.GetValue<long>()));
                node["m_VertexData"]!["m_DataSize"] = System.Convert.ToBase64String(bytes);
            }
            meshes.Add(new(mesh.Object.Id, 43, node));
            var rawMaterialPointers = data.GetProperty("m_Materials").GetProperty("Array");
            Validation.Require(rawMaterialPointers.ValueKind == JsonValueKind.Array && rawMaterialPointers.GetArrayLength() <= 4096, "Renderer material slot limit exceeded");
            var materialPointers = rawMaterialPointers.EnumerateArray().ToArray();
            var rendererMaterials = materialPointers.Select(p => resources.Resolve(actor.Cab, p) ?? throw new InvalidDataException("Renderer material is missing")).ToArray();
            Validation.Require(rendererMaterials.Length > 0 && rendererMaterials.All(x => x.Object.ClassId == 21), "Renderer reference is not a Material");
            string name = node["m_Name"]!.GetValue<string>();
            var rendererObject=resources.Resolve(actor.Cab,data.GetProperty("m_GameObject"))??throw new InvalidDataException("Renderer GameObject is missing");
            Validation.Require(hierarchyByObject.TryGetValue(NativePrefabHierarchy.Identity(rendererObject),out var rendererNode),"Renderer is outside the selected prefab hierarchy");
            meshTransforms.Add(name,rendererNode!.World);
            meshIdentities.Add(name, mesh.Cab + ":" + mesh.Object.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Validation.Require(meshMaterials.TryAdd(name, rendererMaterials), "Multiple renderers use the same mesh name");
        }
        var decoded = new SerializedDocument(actor.Document.UnityVersion, [], [avatar.Object, .. meshes]);
        var database = CharacterGeometry.Convert(decoded, prefab.Path, nativeMeshTransforms: meshTransforms);
        var scene = database.Assets[0].Scene!;
        scene = ApplyMaterials(resources, scene, meshMaterials, meshIdentities);
        scene = scene with { FaceDriver = NativeFaceMorph.Extract(resources, prefab.Path, avatar, scene), ImportDiagnostics = [$"Selected native prefab bundle {prefab.Bundle}; hash {prefab.Hash:x16}; renderer transforms aligned to Avatar mesh nodes"] };
        database = new(resources.Manifest.Version, [database.Assets[0] with { Detail = "Native geometry, materials and authored facial controls; body animation not included", Scene = scene }]);
        Validation.Database(database); return database;
    }

    internal static SceneDocument ApplyMaterials(GameResources resources, SceneDocument scene, Dictionary<string, ResolvedAsset[]> meshMaterials, Dictionary<string,string> meshIdentities)
    {
        var materials = new List<MaterialRecord>();
        var textures = new Dictionary<string, TextureRecord>(StringComparer.Ordinal);
        var descriptors = new Dictionary<string, TextureDescriptor>(StringComparer.Ordinal);
        var materialIndices = new Dictionary<(string, long), int>();
        var outputMeshes = new List<MeshRecord>();
        foreach (var mesh in scene.Meshes)
        {
            OperationProgress.Report("decode-materials", outputMeshes.Count, scene.Meshes.Length);
            var slots = new List<int>();
            foreach (var source in meshMaterials[mesh.Name])
            {
            var key = (source.Cab, source.Object.Id);
            if (!materialIndices.TryGetValue(key, out int index))
            {
                index = materials.Count;
                var data = Json(source.Object);
                var npr = NativeMaterials.Extract(source, resources.Resolve, (asset, identity, usage) => DecodeTexture(resources, asset, identity, usage), textures, descriptors);
                var floats = npr.Floats; var colors = npr.Colors;
                string? Texture(bool normal) {
                    string[] accepted = normal ? ["_NormalTex", "_NormalMap", "_BumpMap", "_NTexture", "_NormalTexture"] : ["_MainTex", "_BaseMap", "_BaseColorMap", "_DiffuseTex", "_DTexture", "_DiffuseTexture"];
                    return accepted.Select(slot => npr.Textures.GetValueOrDefault(slot)).FirstOrDefault(x => x?.Status == "resolved")?.TextureId;
                }
                double[] color = [1, 1, 1, 1];
                foreach (string colorName in new[] { "_BaseColor", "_Color", "_MainColor" })
                    if (colors.TryGetValue(colorName, out var value)) { color = value.Select(x => Math.Clamp(x, 0, 1)).ToArray(); break; }
                string keywords = string.Join(" ", npr.Keywords.Valid.Concat(npr.Keywords.Legacy));
                bool alpha = floats.GetValueOrDefault("_AlphaClip") > 0 || keywords.Contains("ALPHATEST", StringComparison.OrdinalIgnoreCase);
                materials.Add(new(data.GetProperty("m_Name").GetString()! + "_" + index, color,
                    Math.Clamp(floats.GetValueOrDefault("_Metallic"), 0, 1), Math.Clamp(floats.GetValueOrDefault("_Roughness", 0.5), 0, 1), Texture(false), Texture(true), alpha,
                    Math.Clamp(floats.GetValueOrDefault("_AlphaClipThreshold", floats.GetValueOrDefault("_Cutoff", 0.5)), 0, 1),
                    floats.GetValueOrDefault("_SurfaceType") > 0 || floats.GetValueOrDefault("_UseGrayAsAlpha") > 0, floats.GetValueOrDefault("_UseGrayAsAlpha") > 0, npr));
                materialIndices[key] = index;
            }
            slots.Add(index);
            }
            Validation.Require(slots.Count >= mesh.SubmeshCount, "Native renderer omits submesh materials");
            outputMeshes.Add(mesh with { Material = slots[0], MaterialSlots = slots.ToArray(), SourceId = meshIdentities[mesh.Name] });
        }
        scene = scene with { Meshes = outputMeshes.ToArray(), Materials = materials.ToArray(), Textures = textures.Values.ToArray(), TextureDescriptors = descriptors.Values.ToArray() };
        return scene;
    }

    private static TextureRecord DecodeTexture(GameResources resources, ResolvedAsset asset, string identity, string usage)
    {
        OperationProgress.Report("decode-texture", detail: identity);
        Validation.Require(asset.Object.ClassId == 28, "Material texture reference is not a Texture2D");
        var data = Json(asset.Object); int width = data.GetProperty("m_Width").GetInt32(), height = data.GetProperty("m_Height").GetInt32(), format = data.GetProperty("m_TextureFormat").GetInt32();
        Validation.Require(width > 0 && height > 0 && width <= 16384 && height <= 16384 && (long)width * height <= 16777216, "Native texture dimensions exceed limit");
        int blockSize = format is 10 or 26 ? 8 : 16;
        int topSize = format == 4 ? checked(width * height * 4) : checked(((width + 3) / 4) * ((height + 3) / 4) * blockSize);
        byte[] pixels = data.GetProperty("image data").GetBytesFromBase64();
        if (pixels.Length == 0)
        {
            var stream = data.GetProperty("m_StreamData");
            Validation.Require(stream.GetProperty("size").GetInt64() >= topSize, "Texture stream does not contain top mip");
            pixels = resources.ReadResource(asset.Cab, stream.GetProperty("path").GetString()!, stream.GetProperty("offset").GetInt64(), topSize);
        }
        Validation.Require(pixels.Length >= topSize, "Texture top mip is truncated");
        var rgba = TexturePixels.Decode(format, width, height, pixels.AsSpan(0, topSize).ToArray());
        if (usage == "normal" && format == 27)
            for (int i = 0; i < rgba.Length; i += 4)
            {
                double x = rgba[i] / 127.5 - 1, y = rgba[i + 1] / 127.5 - 1;
                rgba[i + 2] = (byte)Math.Round((Math.Sqrt(Math.Max(0, 1 - x * x - y * y)) * 0.5 + 0.5) * 255);
            }
        return new(identity, width, height, usage != "color" || data.GetProperty("m_ColorSpace").GetInt32() == 0, TexturePixels.Png(width, height, rgba, true));
    }

    private static JsonElement Json(SerializedObject source) => JsonSerializer.SerializeToElement(source.Data, WireJson.Options);
}
