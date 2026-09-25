namespace Sora.Core;

public sealed record NativeBuffWeaponVisual(int Event, string Effect, int WeaponIndex,
    bool UseWeaponMountPoint, int WeaponVfxIndex, string? WeaponVfxKey, bool ShowHideWithWeapon);
public sealed record NativeHeldArrowEvent(int ElementIndex, long Offset, double Time, bool Visible,
    string BuffId, string Effect, int WeaponIndex);

public static partial class NativeSkillAnimation
{
    /// <summary>Read the verified BuffData prefix through buffEventAction, not the unneeded tail.
    /// Unsupported nonempty modifier/ability lists reject the projection rather than guessing offsets.</summary>
    public static NativeBuffWeaponVisual[] ReadBuffWeaponVisuals(ReadOnlySpan<byte> bytes)
    {
        var r = new Reader(bytes);
        Validation.Require(r.Header(30), "Unexpected BuffData header");
        Validation.Require(r.Int32() == 0, "Buff ability events are outside held-arrow preview scope");
        BlackboardDouble(ref r);
        int tags = r.Int32();
        Validation.Require(tags is >= -1 and <= 4096, "Invalid buff tag count");
        if (tags > 0) r.Skip(tags * 4L);
        Validation.Require(r.Header(2) && r.Int32() == 0, "Buff attribute modifiers are outside held-arrow preview scope");
        r.Boolean();
        int pairs = r.Int32();
        Validation.Require(pairs is >= -1 and <= 4096, "Invalid buff blackboard count");
        for (int i = 0; i < pairs; i++)
        {
            Validation.Require(r.Header(4), "Unexpected DataPair header");
            r.Boolean(); r.String(); r.Skip(8); r.String();
        }
        int maps = r.Int32();
        Validation.Require(maps is >= -1 and <= 4096, "Invalid buff event count");
        var result = new List<NativeBuffWeaponVisual>();
        for (int i = 0; i < maps; i++)
        {
            Validation.Require(r.Header(2), "Unexpected BuffActionMap header");
            int sequences = r.Int32();
            Validation.Require(sequences is >= -1 and <= 4096, "Invalid buff sequence count");
            var actions = new List<NativeSkillAction>();
            for (int j = 0; j < sequences; j++) actions.AddRange(Sequence(ref r).Actions);
            int evt = r.Int32();
            foreach (var action in actions)
                if (action.IsEnable && action.UnionTag == 162 && action.Members?[2] is object?[] cfg
                    && cfg.Length == 85 && cfg[80] is int weapon && cfg[12] is string effect)
                    result.Add(new(evt, effect, weapon, cfg[75] is true, (int)cfg[82]!, (string?)cfg[83], cfg[60] is true));
        }
        return result.ToArray();
    }

    /// <summary>Held-arrow preview visibility follows the named arrow-show buff's authored
    /// finish/create times. It represents a weapon-mounted VFX with the imported arrow model.</summary>
    public static NativeHeldArrowEvent[] HeldArrowEvents(GameResources game, NativeSkillTimeline timeline)
    {
        var rows = new List<NativeHeldArrowEvent>();
        var cache = new Dictionary<string, NativeBuffWeaponVisual[]>();
        foreach (var element in timeline.Elements)
            foreach (var action in element.Actions)
            {
                if (!action.IsEnable || action.Members is not {} m) continue;
                var ids = new List<string>();
                bool visible = action.UnionTag == 146;
                if (visible && m[3] is object?[] inputs)
                    ids.AddRange(inputs.OfType<object?[]>().Where(v => v.Length == 5).Select(v => v[2]).OfType<string>());
                else if (action.UnionTag == 180 && m[1] is object?[] find && find[0] is object?[] names)
                    ids.AddRange(names.OfType<string>());
                foreach (string id in ids.Where(id => id.EndsWith("_common_arrowshow", StringComparison.Ordinal)))
                {
                    Validation.Require(id.Length < 256 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'), "Invalid buff identity");
                    if (!cache.TryGetValue(id, out var visuals))
                        cache[id] = visuals = ReadBuffWeaponVisuals(game.GetBytes("Json/BuffData/" + id + ".json"));
                    foreach (var visual in visuals)
                        rows.Add(new(element.Index, action.Offset, element.StartTime, visible, id, visual.Effect, visual.WeaponIndex));
                }
            }
        return rows.OrderBy(r => r.Time).ThenBy(r => r.ElementIndex).ToArray();
    }
}
