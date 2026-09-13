using System.Buffers.Binary;
using System.Text.Json;
using System.Text;
namespace Sora.Core;

/// <summary>One clip identity carried by a montage union body: the 36-byte AnimationClipAsyncInfo wire record
/// (hash at 0, length at 8, sample rate at 12, average speed at 16, average angular speed at 28, humanoid at
/// 32, looping at 33, two padding bytes) exactly as this build's formatter writes it.</summary>
public sealed record NativeAnimationMontageClip(string Key, string Kind, string Role, ulong Hash, float Length,
    float SampleRate, bool IsHumanoid, bool IsLooping);

/// <summary>One authored montage entry: the dictionary key plus every clip identity its union body carries.</summary>
public sealed record NativeAnimationMontage(string Key, int UnionTag, string Kind, NativeAnimationMontageClip[] Clips,
    long Offset, long EndOffset);

/// <summary>One verified AnimationConfigJson bone-weight-mask record.</summary>
public sealed record NativeAnimationBoneWeightMask(string Name, ulong Hash);

/// <summary>Verified montage map of one AnimationConfigJson resource. Only the montage dictionary is decoded;
/// the remaining root members stay unparsed and are reported with their exact offset, so a caller can never
/// mistake this for a complete config deserialization.</summary>
public sealed record NativeAnimationMontageMap(string Source, NativeAnimationMontage[] Montages, long ParsedBytes,
    bool MontagesComplete, long? UnparsedOffset, string? UnparsedReason,
    NativeAnimationBoneWeightMask[]? BoneWeightMasks = null, ulong? ControllerHash = null);

/// <summary>One montage clip resolved to the native resource the manifest hash names.</summary>
public sealed record NativeAnimationMontageResolution(string Key, string Kind, string Role, ulong Hash,
    string? ResourcePath, string? Cab, string? PathId, string? Name, string Status,
    double? Length = null, double? SampleRate = null);

/// <summary>Reads the character AnimationConfigJson montage dictionary and resolves its authored clip hashes
/// through the game manifest. Every member layout here is copied from the verified consumer delivery; an
/// unknown member stops the walk at its exact byte offset instead of being skipped or guessed.</summary>
public static class NativeAnimationMontageConfig
{
    private const int RootMembers = 15;
    private const int ExtraMembers = 30;
    private const int ClipMontageMembers = 26;
    private const int SequenceMontageMembers = 20;
    private const int AlphaBlendMembers = 3;
    private const int ClipInfoBytes = 36;
    private const int CurveMembers = 3;
    private const int CurveKeyBytes = 32;
    private const int MaxCount = 65536;
    private const int AnimationClipClassId = 74;

    private enum FieldKind
    {
        Boolean, Int32, Single, ShaderProperties, PlayEffects, RendererVisibility, SpecialDash, SpecialIdle,
        StatePerform, HurtAnimations, MoveAdditiveAnimations, OverridePerform
    }

    /// <summary>The CharacterAnimExtraData member order and reader kind, in the order the verified formatter
    /// writes them. Primitive members are plain scalars; the rest dispatch on the confirmed union body.</summary>
    private static readonly (string Name, FieldKind Kind)[] Fields =
    [
        ("_animatedShaderPropertyCfg", FieldKind.ShaderProperties),
        ("_animationEventPlayEffectCfg", FieldKind.PlayEffects),
        ("_animationEventRendererVisibilityCfg", FieldKind.RendererVisibility),
        ("_enableAnimatedShaderProperty", FieldKind.Boolean),
        ("_enableAnimationEventPlayEffect", FieldKind.Boolean),
        ("_enableAnimationEventRendererVisibility", FieldKind.Boolean),
        ("__strafeAngleHorizontalThresholdDeg", FieldKind.Single),
        ("__strafeAngleSmoothSpeed", FieldKind.Single),
        ("__strafeAngleThresholdDeg", FieldKind.Single),
        ("__strafeMagnitudeSmoothSpeed", FieldKind.Single),
        ("_characterBlackboardType", FieldKind.Int32),
        ("_clothIKDirectionNormalizeAngleDeg", FieldKind.Single),
        ("_clothIKLegAdaptAngleDeg", FieldKind.Single),
        ("_dashDuration", FieldKind.Single),
        ("_duringTransitionClothFrontScale", FieldKind.Single),
        ("_hasSpDash", FieldKind.Boolean),
        ("_hurtAnimConfigs", FieldKind.HurtAnimations),
        ("_ikReverseAffectSkirtPhysicsFactor", FieldKind.Single),
        ("_isHoldBombWithBothHands", FieldKind.Boolean),
        ("_jumpStartBlendInTime", FieldKind.Single),
        ("_magicaClothWeightDecreaseSpeed", FieldKind.Single),
        ("_magicaClothWeightIncreaseSpeed", FieldKind.Single),
        ("_moveAdditiveAnims", FieldKind.MoveAdditiveAnimations),
        ("_overrideDashDuration", FieldKind.Boolean),
        ("_overridePerformDict", FieldKind.OverridePerform),
        ("_runSpLoopCount", FieldKind.Int32),
        ("_spDashConfig", FieldKind.SpecialDash),
        ("_spIdleConfig", FieldKind.SpecialIdle),
        ("_statePerformEntries", FieldKind.StatePerform),
        ("_walkSpLoopCount", FieldKind.Int32),
    ];

    /// <summary>Reads the verified AnimationConfigJson root chain up to and including the montage dictionary.</summary>
    public static NativeAnimationMontageMap Read(ReadOnlySpan<byte> bytes, string name)
    {
        Validation.Require(bytes.Length is > 0 and <= 16 * 1024 * 1024, "Unsupported native animation config size");
        var reader = new Reader(bytes);
        var montages = new List<NativeAnimationMontage>();
        var boneWeightMasks = new List<NativeAnimationBoneWeightMask>();
        ulong? controllerHash = null;
        bool complete = false;
        long? stop = null;
        string? reason = null;
        try
        {
            Validation.Require(reader.Header(RootMembers), "AnimationConfigJson formatter version differs from the verified layout");
            Validation.Require(reader.Byte() == 255, "AnimationConfigJson fallback montages are not null; layout not verified");
            reader.Skip(8);
            reader.Skip(8);
            int boneWeightMaskCount = reader.Int32();
            Validation.Require(boneWeightMaskCount is >= 0 and <= MaxCount,
                "Unsupported AnimationConfigJson bone weight mask count");
            if (boneWeightMaskCount != 0)
                for (int index = 0; index < boneWeightMaskCount; index++)
                    boneWeightMasks.Add(BoneWeightMask(ref reader));
            controllerHash = reader.UInt64();
            Validation.Require(reader.Byte() == 0, "AnimationConfigJson extra data union tag is not the verified tag");
            Validation.Require(reader.Header(ExtraMembers), "CharacterAnimExtraData formatter version differs from the verified layout");
            foreach ((string field, FieldKind kind) in Fields) Consume(ref reader, field, kind);
            if (reader.Byte() != 255)
            {
                int count = reader.Int32();
                Validation.Require(count is >= 0 and <= 4096, "Unsupported montage dictionary count");
                for (int index = 0; index < count; index++) montages.Add(Montage(ref reader));
            }
            complete = true;
        }
        catch (UnverifiedConfig error)
        {
            stop = error.Offset;
            reason = error.Message;
        }
        if (complete)
        {
            stop = reader.At;
            reason = "remaining AnimationConfigJson members are not decoded; only the montage dictionary is consumed";
        }
        return new(name, montages.ToArray(), reader.At, complete, stop, reason, boneWeightMasks.ToArray(), controllerHash);
    }

    /// <summary>Reads the non-empty mask record observed in the current character config. Its object header is
    /// 2 followed by a UTF-8 layer name and a UInt64 mask hash.</summary>
    private static NativeAnimationBoneWeightMask BoneWeightMask(ref Reader reader)
    {
        long offset = reader.At;
        Validation.Require(reader.Header(2),
            "AnimationConfigJson bone weight mask formatter header differs at byte " + offset);
        string name = reader.String() ?? throw new UnverifiedConfig("Bone weight mask name is null", offset);
        ulong hash = reader.UInt64();
        return new(name, hash);
    }

    /// <summary>Resolves the requested montage keys to their native clip resource through the game manifest: an
    /// authored hash that is absent or ambiguous never resolves to a guessed resource.</summary>
    public static NativeAnimationMontageResolution[] Resolve(GameResources game, NativeAnimationMontageMap map,
        IReadOnlyCollection<string> keys)
    {
        Validation.Require(game is not null && map is not null && keys is not null, "Missing montage resolution input");
        var wanted = keys!.ToHashSet(StringComparer.Ordinal);
        var rows = new List<NativeAnimationMontageResolution>();
        foreach (NativeAnimationMontage montage in map!.Montages.Where(m => wanted.Contains(m.Key)))
        foreach (NativeAnimationMontageClip clip in montage.Clips)
        {
            if (clip.Hash == 0)
            {
                rows.Add(new(clip.Key, clip.Kind, clip.Role, clip.Hash, null, null, null, null, "hash-zero"));
                continue;
            }
            var matches = game!.Manifest.Assets.Where(asset => unchecked((ulong)asset.Hash) == clip.Hash)
                .DistinctBy(asset => (asset.Path, asset.Bundle)).ToArray();
            if (matches.Length != 1)
            {
                rows.Add(new(clip.Key, clip.Kind, clip.Role, clip.Hash, null, null, null, null,
                    matches.Length == 0 ? "asset-hash-absent" : "asset-hash-ambiguous"));
                continue;
            }
            // ResolveHash demands exactly one manifest target of the requested class; a hash that names
            // something other than an AnimationClip is reported instead of being coerced to a clip.
            ResolvedAsset? resolved = null;
            try { resolved = game.ResolveHash((long)clip.Hash, AnimationClipClassId); }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException) { resolved = null; }
            if (resolved is null)
            {
                rows.Add(new(clip.Key, clip.Kind, clip.Role, clip.Hash, matches[0].Path, null, null, null,
                    "resource-is-not-an-animation-clip"));
                continue;
            }
            string id = NativePrefabHierarchy.Identity(resolved);
            // m_Name is a real serialized string on the clip object; the display projection wraps scalars, so
            // the raw wire data is read here exactly as the equipment discovery path reads it.
            string clipName = JsonSerializer.SerializeToElement(resolved.Object.Data, WireJson.Options).GetProperty("m_Name").GetString() ?? "";
            string[] parts = id.Split(':', 2);
            rows.Add(new(clip.Key, clip.Kind, clip.Role, clip.Hash, matches[0].Path, parts[0], parts.Length > 1 ? parts[1] : null,
                clipName, "resolved", clip.Length, clip.SampleRate));
        }
        return rows.OrderBy(row => row.Key, StringComparer.Ordinal).ThenBy(row => row.Role, StringComparer.Ordinal).ToArray();
    }

    private static NativeAnimationMontage Montage(ref Reader reader)
    {
        string key = reader.String() ?? throw new UnverifiedConfig("Montage dictionary key is null", reader.At);
        long offset = reader.At;
        int tag = reader.UnionTag();
        string kind;
        var clips = new List<NativeAnimationMontageClip>();
        if (tag == 0)
        {
            Validation.Require(reader.Header(ClipMontageMembers), "ClipMontageData formatter version differs from the verified layout");
            AnimMontagePrefix(ref reader);
            clips.Add(ClipInfo(ref reader, key, "ClipMontageData", "clipInfo"));
            reader.Boolean();
            for (int index = 0; index < 7; index++) Curve(ref reader);
            kind = "ClipMontageData";
        }
        else if (tag == 1)
        {
            Validation.Require(reader.Header(SequenceMontageMembers), "SequenceMontageData formatter version differs from the verified layout");
            AnimMontagePrefix(ref reader);
            clips.Add(ClipInfo(ref reader, key, "SequenceMontageData", "endClipInfo"));
            clips.Add(ClipInfo(ref reader, key, "SequenceMontageData", "loopClipInfo"));
            clips.Add(ClipInfo(ref reader, key, "SequenceMontageData", "startClipInfo"));
            kind = "SequenceMontageData";
        }
        else
        {
            throw new UnverifiedConfig("Unimplemented montage union tag " + tag, offset);
        }
        return new(key, tag, kind, clips.ToArray(), offset, reader.At);
    }

    /// <summary>The 17-member AnimMontageData prefix shared by both montage bodies.</summary>
    private static void AnimMontagePrefix(ref Reader reader)
    {
        reader.Boolean();
        reader.Int32();
        reader.Boolean();
        if (reader.Header(AlphaBlendMembers)) { reader.Int32(); reader.Single(); Curve(ref reader); }
        reader.Boolean();
        reader.Boolean();
        reader.Byte();
        reader.Byte();
        reader.Skip(8);
        reader.Int32();
        reader.Int32();
        reader.Single();
        reader.Boolean();
        reader.Single();
        reader.Single();
        reader.Boolean();
        reader.Boolean();
    }

    private static NativeAnimationMontageClip ClipInfo(ref Reader reader, string key, string kind, string role)
    {
        long at = reader.At;
        ulong hash = reader.UInt64();
        float length = reader.Single();
        float rate = reader.Single();
        reader.Single();
        reader.Skip(8);
        reader.Single();
        bool humanoid = reader.Boolean();
        bool looping = reader.Boolean();
        reader.Skip(2);
        Validation.Require(reader.At - at == ClipInfoBytes, "AnimationClipAsyncInfo wire size differs from the verified layout");
        return new(key, kind, role, hash, length, rate, humanoid, looping);
    }

    /// <summary>Unity FAnimationCurve: member header, an optional key count, 32-byte keys, then post/pre wrap.</summary>
    private static void Curve(ref Reader reader)
    {
        if (!reader.Header(CurveMembers)) return;
        int keys = reader.Int32();
        if (keys != -1)
        {
            Validation.Require(keys is >= 0 and <= MaxCount, "Unsupported animation curve key count");
            reader.Skip((long)keys * CurveKeyBytes);
        }
        reader.Int32();
        reader.Int32();
    }

    private static void Consume(ref Reader reader, string field, FieldKind kind)
    {
        switch (kind)
        {
            case FieldKind.Boolean: reader.Boolean(); return;
            case FieldKind.Int32: reader.Int32(); return;
            case FieldKind.Single: reader.Single(); return;
            case FieldKind.ShaderProperties:
            {
                if (!reader.Header(1)) return;
                int count = Count(ref reader, field);
                for (int index = 0; index < count; index++)
                {
                    if (!reader.Header(4)) continue;
                    reader.Int32(); reader.Int32(); reader.String(); reader.Int32();
                }
                return;
            }
            case FieldKind.PlayEffects:
            {
                if (!reader.Header(1)) return;
                int count = Count(ref reader, field);
                for (int index = 0; index < count; index++)
                {
                    if (!reader.Header(6)) continue;
                    reader.String(); reader.String(); reader.Boolean(); reader.Boolean(); reader.Boolean(); reader.String();
                }
                return;
            }
            case FieldKind.RendererVisibility:
            {
                if (!reader.Header(1)) return;
                int count = Count(ref reader, field);
                Validation.Require(count == 0, "AnimationEventRendererVisibilityConfig elements are not decoded yet");
                return;
            }
            case FieldKind.SpecialDash:
            {
                if (!reader.Header(3)) return;
                int count = Count(ref reader, field);
                for (int index = 0; index < count; index++)
                {
                    if (!reader.Header(2)) continue;
                    reader.String();
                    reader.Skip(4L * Count(ref reader, field));
                }
                reader.Boolean();
                int strings = Count(ref reader, field);
                for (int index = 0; index < strings; index++) reader.String();
                return;
            }
            case FieldKind.SpecialIdle:
            {
                if (!reader.Header(2)) return;
                int count = Count(ref reader, field);
                for (int index = 0; index < count; index++)
                {
                    if (!reader.Header(4)) continue;
                    int conditions = Count(ref reader, field);
                    for (int condition = 0; condition < conditions; condition++) Condition(ref reader);
                    reader.String();
                    reader.Skip(4L * Count(ref reader, field));
                    reader.Boolean();
                }
                int strings = Count(ref reader, field);
                for (int index = 0; index < strings; index++) reader.String();
                return;
            }
            case FieldKind.StatePerform:
            {
                int count = Count(ref reader, field);
                for (int index = 0; index < count; index++)
                {
                    if (!reader.Header(3)) continue;
                    reader.Int32(); reader.String(); reader.Int32();
                }
                return;
            }
            case FieldKind.HurtAnimations:
            {
                if (reader.Byte() == 255) return;
                int count = Count(ref reader, field);
                for (int index = 0; index < count; index++)
                {
                    reader.Int32();
                    if (!reader.Header(3)) continue;
                    Curve(ref reader);
                    reader.Skip(8);
                    if (!reader.Header(5)) continue;
                    reader.Skip(8); reader.Int32(); reader.Int32(); reader.Skip(8); reader.Boolean();
                }
                return;
            }
            case FieldKind.MoveAdditiveAnimations:
            {
                if (reader.Byte() == 255) return;
                int count = Count(ref reader, field);
                for (int index = 0; index < count; index++)
                {
                    reader.String();
                    if (!reader.Header(5)) continue;
                    reader.Skip(ClipInfoBytes); reader.Skip(ClipInfoBytes);
                    if (reader.Header(5)) { reader.Skip(8); reader.Int32(); reader.Int32(); reader.Skip(8); reader.Boolean(); }
                    reader.Skip(ClipInfoBytes); reader.Skip(ClipInfoBytes);
                }
                return;
            }
            case FieldKind.OverridePerform:
            {
                if (reader.Byte() == 255) return;
                int count = Count(ref reader, field);
                for (int index = 0; index < count; index++) { reader.String(); reader.String(); }
                return;
            }
            default:
                throw new UnverifiedConfig("Unimplemented CharacterAnimExtraData field kind " + kind + " at " + reader.At, reader.At);
        }
    }

    /// <summary>SpecialIdleCondition union bodies verified from this build's formatter read sequence.</summary>
    private static void Condition(ref Reader reader)
    {
        long at = reader.At;
        int tag = reader.UnionTag();
        switch (tag)
        {
            case 2: if (reader.Header(1)) reader.Single(); return;
            case 0:
                if (!reader.Header(6)) return;
                reader.Boolean(); reader.Boolean(); reader.Skip(8); reader.Boolean(); reader.Boolean(); reader.Skip(8);
                return;
            case 4: if (reader.Header(1)) reader.Skip(8); return;
            case 3: if (reader.Header(2)) { reader.Single(); reader.Single(); } return;
            case 5:
                // TransformInSightCondition (verified by an independent byte0 consumer of the real
                // anim_cfg_chr_0034_typhoea binary, which walked all 99 montages with this body):
                // header 2 -> list<string> targetPaths -> float threshold.
                if (!reader.Header(2)) return;
                int paths = reader.Int32();
                if (paths < 0) return;
                for (int index = 0; index < paths; index++) reader.String();
                reader.Single();
                return;
            default: throw new UnverifiedConfig("Unimplemented SpecialIdleCondition union tag " + tag, at);
        }
    }

    private static int Count(ref Reader reader, string field)
    {
        int count = reader.Int32();
        Validation.Require(count is >= 0 and <= MaxCount, "Unsupported " + field + " element count");
        return count;
    }

    private sealed class UnverifiedConfig(string message, long offset) : Exception(message)
    {
        public long Offset { get; } = offset;
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> data;
        public Reader(ReadOnlySpan<byte> source) { data = source; At = 0; }
        public long At { get; private set; }
        public bool Header(int members)
        {
            long at = At;
            byte actual = Byte();
            if (actual == 255) return false;
            Validation.Require(actual == members, "Formatter member count differs from the verified layout at byte " + at
                + ": expected " + members + ", read " + actual);
            return true;
        }
        public int UnionTag()
        {
            long at = At;
            byte tag = Byte();
            if (tag == 250) return UInt16();
            Validation.Require(tag < 251, "Unsupported union header " + tag + " at " + at);
            return tag;
        }
        public byte Byte()
        {
            Validation.Require(At < data.Length, "Native animation config ended inside a member");
            return data[(int)At++];
        }
        public bool Boolean() => Byte() != 0;
        public ushort UInt16() { ushort value = BinaryPrimitives.ReadUInt16LittleEndian(Slice(2)); At += 2; return value; }
        public int Int32() => (int)UInt32();
        public uint UInt32() { uint value = BinaryPrimitives.ReadUInt32LittleEndian(Slice(4)); At += 4; return value; }
        public ulong UInt64() { ulong value = BinaryPrimitives.ReadUInt64LittleEndian(Slice(8)); At += 8; return value; }
        public float Single() => BitConverter.UInt32BitsToSingle(UInt32());
        public string? String()
        {
            int length = Int32();
            if (length == -1) return null;
            Validation.Require(length >= 0 && At + length <= data.Length, "Invalid native string length");
            string text = Encoding.UTF8.GetString(Slice(length));
            At += length;
            return text;
        }
        public void Skip(long count)
        {
            Validation.Require(count >= 0 && At + count <= data.Length, "Native animation config ended inside a member");
            At += count;
        }
        private ReadOnlySpan<byte> Slice(int length)
        {
            Validation.Require(length >= 0 && At + length <= data.Length, "Native animation config ended inside a member");
            return data.Slice((int)At, length);
        }
    }
}
