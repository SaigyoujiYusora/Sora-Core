using System.Text.Json;

namespace Sora.Core;

public sealed record NativeProjectileMotion(string Id, string ResourcePath, double Speed,
    double Duration, double Distance, bool FinishOnReach, string[] Effects, string Status,
    double[][]? SpeedCurve = null);
public sealed record NativeProjectileLaunch(int ElementIndex, double Time, long Offset, string Branch,
    string ProjectileId, int WeaponIndex, int WeaponMountPoint, string? MountSourcePath,
    float[]? FixedPoint, NativeProjectileMotion? Motion, string Status);
public sealed record NativeSkillFlightPlan(string Scope, NativeProjectileLaunch[] Launches, string[] Diagnostics,
    Dictionary<string, MeshRecord[]> Visuals, NativeHeldArrowEvent[] HeldArrowEvents);

/// <summary>Source-authored launch alternatives. No gameplay branch is silently executed by this contract.</summary>
public static class NativeSkillFlight
{
    private static JsonElement Json(SerializedObject obj) => JsonSerializer.SerializeToElement(obj.Data, WireJson.Options);
    private static JsonElement[] Array(JsonElement obj, string field) => obj.GetProperty(field).GetProperty("Array").EnumerateArray().ToArray();
    private static double Literal(JsonElement obj)
    {
        Validation.Require(obj.GetProperty("useBlackboardKey").GetInt32() == 0, "Projectile requires runtime blackboard");
        double value = obj.GetProperty("value").GetDouble();
        Validation.Require(double.IsFinite(value), "Nonfinite projectile value");
        return value;
    }
    public static NativeProjectileMotion ReadMotion(GameResources game, string id)
    {
        Validation.Require(id.Length is > 0 and < 256 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'), "Invalid projectile id");
        string path = "assets/beyond/dynamicassets/gamedata/projectile/data_" + id + ".asset";
        var address = game.Manifest.Assets.Where(a => a.Path == path).DistinctBy(a => (a.Path, a.Bundle)).Single();
        var data = Json(game.ResolveAddress(address, 114).Object);
        var refs = Array(data.GetProperty("references"), "RefIds").ToDictionary(r => r.GetProperty("rid").GetInt64());
        var root = refs[data.GetProperty("data").GetProperty("rid").GetInt64()];
        Validation.Require(root.GetProperty("type").GetProperty("class").GetString() == "ProjectileTemplateData", "Unexpected projectile root");
        var template = root.GetProperty("data");
        Validation.Require(template.GetProperty("id").GetString() == id, "Projectile id mismatch");
        var component = Array(template, "componentList").Select(p => refs[p.GetProperty("rid").GetInt64()])
            .Single(r => r.GetProperty("type").GetProperty("class").GetString() == "ProjectileComponentData").GetProperty("data");
        var modes = Array(component.GetProperty("moveModeDict"), "_valueData");
        Validation.Require(component.GetProperty("useSegmentMove").GetInt32() == 0 && modes.Length == 1, "Segmented projectile is not supported by the preview");
        var mode = modes[0];
        Validation.Require(mode.GetProperty("moveType").GetInt32() == 0 && mode.GetProperty("groundedMove").GetInt32() == 0
            && mode.GetProperty("lockVelocityToXZ").GetInt32() == 0 && mode.GetProperty("useSpeedScaleWithDistance").GetInt32() == 0,
            "Projectile requires a non-linear movement solver");
        var speedKeys = Array(mode.GetProperty("speedCurve"), "m_Curve");
        Validation.Require(speedKeys.Length > 0 && speedKeys.All(k => k.GetProperty("weightedMode").GetInt32() == 0),
            "Weighted projectile speed curves are not supported by the preview");
        var curve = speedKeys.Select(k => new[] { k.GetProperty("time").GetDouble(), k.GetProperty("value").GetDouble(),
            k.GetProperty("inSlope").GetDouble(), k.GetProperty("outSlope").GetDouble() }).ToArray();
        Validation.Require(curve.All(k => k.All(double.IsFinite) && k[0] >= 0 && k[1] >= 0)
            && curve.Zip(curve.Skip(1)).All(p => p.First[0] < p.Second[0]), "Invalid projectile speed curve");
        double speed = Literal(mode.GetProperty("speed")), duration = Literal(component.GetProperty("finishDuration")), distance = Literal(component.GetProperty("finishDistance"));
        Validation.Require(speed > 0 && duration > 0 && distance > 0, "Projectile lacks finite positive movement limits");
        return new(id, path, speed, duration, distance, component.GetProperty("finishOnReach").GetInt32() != 0,
            Array(component, "mainEffects").Select(e => e.GetProperty("effectName").GetString()!).ToArray(), "native-speed-curve", curve);
    }

    private static string Mount(GameResources game, NativeCharacterEquipmentData owner, int weapon, int mount)
    {
        var slot = owner.DedicatedEquipment.Single(s => s.WeaponIndex == weapon);
        var hierarchy = NativePrefabHierarchy.Read(game, slot.ResourcePath);
        var identities = new HashSet<string>();
        foreach (var node in hierarchy)
            foreach (var pointer in Array(Json(node.GameObject.Object), "m_Component"))
            {
                var component = game.Resolve(node.GameObject.Cab, pointer.GetProperty("component"));
                if (component?.Object.ClassId != 114) continue;
                var data = NativeObjectProjection.Json(component.Object, "weaponMountPoints");
                if (!data.TryGetProperty("weaponMountPoints", out var map)) continue;
                var keys = Array(map, "_keyData"); var values = Array(map, "_valueData");
                Validation.Require(keys.Length == values.Length, "Weapon mount arrays differ");
                for (int i = 0; i < keys.Length; i++)
                    if (keys[i].GetInt32() == mount && game.Resolve(component.Cab, values[i]) is {} point)
                        identities.Add(NativePrefabHierarchy.Identity(point));
            }
        Validation.Require(identities.Count == 1, "Projectile muzzle is absent or ambiguous");
        return hierarchy.Single(n => identities.Contains(NativePrefabHierarchy.Identity(n.Transform))).SourcePath;
    }

    public static NativeSkillFlightPlan Resolve(GameResources game, string character, NativeSkillTimeline timeline)
    {
        var launches = new List<NativeProjectileLaunch>(); var diagnostics = new HashSet<string>();
        var motions = new Dictionary<string, NativeProjectileMotion>(); var mounts = new Dictionary<(int,int), string>();
        NativeCharacterEquipmentData? owner = null;
        foreach (var element in timeline.Elements)
            foreach (var root in element.Actions)
                foreach (var (action, branch) in NativeSkillAnimation.Descendants(root))
                {
                    if (action.UnionTag != 222 || !action.IsEnable || action.Members is not { Length: 33 } m) continue;
                    string id = (string)m[20]!; int weapon = (int)m[31]!, mount = (int)m[32]!;
                    string? path = null; NativeProjectileMotion? motion = null; string status = "ready";
                    var target = m[29] as NativeSkillTarget;
                    try
                    {
                        Validation.Require(m[17] is true && m[30] is true, "Preview needs an explicit weapon muzzle");
                        Validation.Require(m[10] is float[] offset && offset.All(v => v == 0)
                            && m[12] is float[] random && random.All(v => v == 0), "Preview needs an unshifted deterministic muzzle");
                        owner ??= NativeCharacterEquipment.Read(game, character);
                        if (!mounts.TryGetValue((weapon, mount), out path)) mounts[(weapon, mount)] = path = Mount(game, owner, weapon, mount);
                        if (!motions.TryGetValue(id, out motion)) motions[id] = motion = ReadMotion(game, id);
                        if (target?.FixedPoint is null) status = "ready-forward-preview";
                    }
                    catch (Exception e) when (e is InvalidDataException or InvalidOperationException or KeyNotFoundException)
                    { status = e.Message; diagnostics.Add(id + ": " + status); }
                    launches.Add(new(element.Index, element.StartTime, action.Offset, branch, id, weapon, mount, path,
                        target?.FixedPoint, motion, status));
                }
        var visuals = new Dictionary<string, MeshRecord[]>();
        foreach (string effect in motions.Values.SelectMany(m => m.Effects).Distinct())
            try { visuals[effect] = NativeProjectileVisual.Read(game, effect); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or KeyNotFoundException)
            { diagnostics.Add(effect + ": " + e.Message); }
        NativeHeldArrowEvent[] held = [];
        try { held = NativeSkillAnimation.HeldArrowEvents(game, timeline); }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException)
        { diagnostics.Add("held-arrow lifecycle: " + e.Message); }
        return new("authored launch times; fixed-point or forward no-target preview; speed-curve time in seconds; native mesh particles with simplified material; no collisions, combat branching or trails",
            launches.ToArray(), diagnostics.ToArray(), visuals, held);
    }
}
