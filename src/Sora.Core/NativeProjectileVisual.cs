using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sora.Core;

/// <summary>Extract stationary single-burst mesh particles as an explicitly limited trajectory visual.</summary>
public static class NativeProjectileVisual
{
    private static JsonElement Json(SerializedObject value) => JsonSerializer.SerializeToElement(value.Data, WireJson.Options);
    private static double Constant(JsonElement curve)
    {
        int mode = curve.GetProperty("minMaxState").GetInt32();
        Validation.Require(mode == 0 || mode == 3 && curve.GetProperty("minScalar").GetDouble() == curve.GetProperty("scalar").GetDouble(),
            "Animated particle initial values are not supported");
        double value = curve.GetProperty("scalar").GetDouble();
        Validation.Require(double.IsFinite(value), "Nonfinite particle value"); return value;
    }
    public static MeshRecord[] Read(GameResources game, string effect)
    {
        Validation.Require(effect.Length is > 0 and < 256 && effect.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'), "Invalid effect name");
        string path = "assets/beyond/dynamicassets/gameplay/effects/prefabs/" + effect.ToLowerInvariant() + ".prefab";
        var meshes = new List<MeshRecord>();
        var space = Matrix4x4.CreateScale(-1, 1, 1) * Matrix4x4.CreateRotationX(MathF.PI / 2);
        foreach (var node in NativePrefabHierarchy.Read(game, path))
        {
            var components = Json(node.GameObject.Object).GetProperty("m_Component").GetProperty("Array").EnumerateArray()
                .Select(p => game.Resolve(node.GameObject.Cab, p.GetProperty("component"))).Where(c => c is not null).ToArray();
            var renderer = components.SingleOrDefault(c => c!.Object.ClassId == 199);
            var particle = components.SingleOrDefault(c => c!.Object.ClassId == 198);
            if (renderer is null || particle is null) continue;
            var render = Json(renderer.Object);
            if (render.GetProperty("m_RenderMode").GetInt32() != 4) continue;
            var system = Json(particle.Object); var initial = system.GetProperty("InitialModule");
            Validation.Require(Constant(initial.GetProperty("startSpeed")) == 0
                && !system.GetProperty("ShapeModule").GetProperty("enabled").GetBoolean()
                && !initial.GetProperty("rotation3D").GetBoolean()
                && Constant(initial.GetProperty("startRotation")) == 0, "Particle requires animated simulation");
            float scale = (float)Constant(initial.GetProperty("startSize"));
            var size = initial.GetProperty("size3D").GetBoolean()
                ? new Vector3(scale, (float)Constant(initial.GetProperty("startSizeY")), (float)Constant(initial.GetProperty("startSizeZ")))
                : new Vector3(scale);
            var source = game.Resolve(renderer.Cab, render.GetProperty("m_Mesh")) ?? throw new InvalidDataException("Missing mesh particle geometry");
            var meshData = JsonSerializer.SerializeToNode(source.Object.Data, WireJson.Options)!;
            if (meshData["m_StreamData"] is {} stream && stream["size"]!.GetValue<long>() > 0)
                meshData["m_VertexData"]!["m_DataSize"] = Convert.ToBase64String(game.ReadResource(source.Cab,
                    stream["path"]!.GetValue<string>(), stream["offset"]!.GetValue<long>(), checked((int)stream["size"]!.GetValue<long>())));
            var mesh = CharacterGeometry.DecodeMesh(JsonSerializer.SerializeToElement(meshData, WireJson.Options), source.Cab,
                source.Object.Id, Matrix4x4.CreateScale(size) * node.World * space, [], false);
            meshes.Add(mesh);
        }
        Validation.Require(meshes.Count > 0, "Effect has no supported stationary mesh particles");
        return meshes.ToArray();
    }
}
