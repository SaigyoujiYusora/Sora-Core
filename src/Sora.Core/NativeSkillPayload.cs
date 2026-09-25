namespace Sora.Core;

/// <summary>Nested sequences retain their branch membership. They are declarations, not executed events.</summary>
public sealed record NativeSkillSequence(NativeSkillAction[] Actions, bool OnlyMainCharacter, bool OnlyGuard);
public sealed record NativeSkillOpaque(string Kind, long Offset, long EndOffset);
public sealed record NativeSkillTarget(int Target, int Source, string? ContextKey, string? GroupKey,
    int CenterType, bool AdvancedDirection, int? Finder, long Offset, long EndOffset,
    float[]? FixedPoint = null);

public static partial class NativeSkillAnimation
{
    private static object? ReadMember(ref Reader reader, string kind)
    {
        if (kind.StartsWith("rv:", StringComparison.Ordinal)) kind = kind[3..];
        if (kind.StartsWith("list:", StringComparison.Ordinal))
        {
            int count = reader.Int32();
            Validation.Require(count is >= -1 and <= 65536, "Skill member list exceeds limit");
            if (count == -1) return null;
            var values = new object?[count];
            for (int i = 0; i < count; i++) values[i] = ReadMember(ref reader, kind[5..]);
            return values;
        }
        kind = kind.Split('@')[0];
        switch (kind.ToLowerInvariant())
        {
            case "bool": return reader.Boolean();
            case "int": case "int32": return reader.Int32();
            case "float": return reader.Single();
            case "string": return reader.String();
            case "vec3": return new[] { reader.Single(), reader.Single(), reader.Single() };
            case "sequence":
                var sequence = Sequence(ref reader);
                return new NativeSkillSequence(sequence.Actions, sequence.MainChar, sequence.Guard);
            case "target":
                long start = reader.At;
                if (!reader.Header(13)) return null;
                reader.Direction(); reader.String(); reader.Boolean(); int center = reader.Int32();
                bool advanced = reader.Boolean(); reader.String();
                var selector = reader;
                selector.Header(3); int finder = selector.Byte();
                if (finder == 250) finder = selector.UInt16();
                float[]? point = null;
                if (finder == 3)
                {
                    selector.Header(4);
                    point = [selector.Single(), selector.Single(), selector.Single()];
                }
                reader.Selector(); reader.Int32(); reader.Int32(); int target = reader.Int32();
                string? context = reader.String(), group = reader.String(); int source = reader.Int32();
                return new NativeSkillTarget(target, source, context, group, center, advanced,
                    finder == 255 ? null : finder, start, reader.At, point);
            default:
                if (VerifiedStructs.TryGetValue(kind, out string[]? fields))
                {
                    if (!reader.Header(fields.Length)) return null;
                    var values = new object?[fields.Length];
                    for (int i = 0; i < fields.Length; i++) values[i] = ReadMember(ref reader, fields[i]);
                    return values;
                }
                long offset = reader.At;
                Consume(ref reader, kind);
                return new NativeSkillOpaque(kind, offset, reader.At);
        }
    }

    public static IEnumerable<(NativeSkillAction Action, string Branch)> Descendants(NativeSkillAction action,
        string branch = "root")
    {
        yield return (action, branch);
        if (!action.IsEnable || action.Members is null) yield break;
        for (int i = 0; i < action.Members.Length; i++)
            if (action.Members[i] is NativeSkillSequence sequence)
                foreach (var child in sequence.Actions)
                    foreach (var entry in Descendants(child, branch + "/" + action.UnionTag + ":" + i))
                        yield return entry;
    }
}
