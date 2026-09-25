namespace Sora.Core;

public sealed record NativeSkillClipEntry(string ResourcePath, string Cab, string PathId, string[] Skills);

/// <summary>Join SkillData montage references to exact native clip identities, never filenames.</summary>
public static class NativeSkillClipIndex
{
    public static NativeSkillClipEntry[] Read(GameResources game, string character)
    {
        var declaration = NativeCharacterEquipment.Read(game, character);
        if (declaration.AnimationConfigPath is null) return [];
        var map = NativeAnimationMontageConfig.Read(game.GetBytes(declaration.AnimationConfigPath), declaration.AnimationConfigPath);
        var resolved = NativeAnimationMontageConfig.Resolve(game, map, map.Montages.Select(m => m.Key).ToArray())
            .Where(r => r.Status == "resolved").GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.ToArray());
        var matches = new List<(NativeAnimationMontageResolution Clip, string Skill)>();
        foreach (string resource in game.LogicalNames.Where(n => n.StartsWith("Json/SkillData/" + character + "_", StringComparison.OrdinalIgnoreCase)
            && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            NativeSkillTimeline skill;
            try { skill = NativeSkillAnimation.Read(game.GetBytes(resource), resource); }
            catch (InvalidDataException) { continue; }
            if (!skill.Complete) continue;
            var keys = skill.Elements.SelectMany(e => e.Actions).Where(a => a.IsEnable && a.AnimationName is not null)
                .Select(a => a.AnimationName!).Distinct().ToArray();
            var clips = keys.Where(resolved.ContainsKey).SelectMany(k => resolved[k]).DistinctBy(r => (r.Cab, r.PathId)).ToArray();
            foreach (var clip in clips) matches.Add((clip, resource));
        }
        return matches.GroupBy(m => (m.Clip.ResourcePath, m.Clip.Cab, m.Clip.PathId))
            .Select(g => new NativeSkillClipEntry(g.Key.ResourcePath!, g.Key.Cab!, g.Key.PathId!,
                g.Select(m => m.Skill).Distinct().Order(StringComparer.Ordinal).ToArray())).ToArray();
    }
}
