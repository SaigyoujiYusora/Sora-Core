using System.Buffers.Binary;
using System.Text;

namespace Sora.Core;

/// <summary>One native Animator parameter write authored by a gameplay action.</summary>
public sealed record NativeSkillParamAction(uint ParamBits, bool BoolValue, float FloatValue, int IntValue, int ParamType);

/// <summary>A single gameplay ability action decoded from the SkillData timeline.
/// Only union bodies whose byte layout is verified against this build's formatters are decoded; every
/// other tag stops the walk with its exact offset instead of being skipped or guessed.</summary>
public sealed record NativeSkillAction(int UnionTag, long Offset, long EndOffset, bool IsEnable, int PriorityLevel,
    int PriorityOffset, int ServerActionIndex, int? WeaponId = null, bool? ActionOnEnd = null,
    bool? ActionOnInterrupt = null, NativeSkillParamAction? ParamAction = null, NativeSkillParamAction? EndAction = null,
    NativeSkillParamAction? InterruptionAction = null, float? InterruptionBefore = null, bool? OverrideInterruptionTime = null,
    string? AnimationName = null, float? AnimationDuration = null, float? AnimationStartTime = null,
    float? AnimationPlaybackSpeed = null, NativeSkillAction[]? OnEndActions = null,
    bool? OnlyExecuteWhenSourceIsMainChar = null, bool? OnlyExecuteWhenSourceIsGuard = null,
    NativeSkillWeaponVisibility? WeaponVisibility = null);

/// <summary>Authored weapon visibility written by a CharWeaponVisibleAction (union tag 55): the wire order is
/// includeAllWeapons, overrideVFX, showVFX, vfxOverrideConfig, visible, weaponIndex. An empty VFX override does
/// not cancel the visible flag, and the runtime restore semantics on end are not asserted by this record.</summary>
public sealed record NativeSkillWeaponVisibility(int WeaponIndex, bool IncludeAllWeapons, bool OverrideVfx, bool ShowVfx,
    bool Visible);

/// <summary>One authored weapon visibility action with the containing element's native frames and seconds.</summary>
public sealed record NativeSkillVisibilityAction(int ElementIndex, int StartFrame, int EndFrame, double StartTime,
    double EndTime, NativeSkillWeaponVisibility Visibility);

/// <summary>One authored timeline element. Start/end are native animation frames, never seconds.</summary>
public sealed record NativeSkillTimelineElement(int Index, long Offset, long EndOffset, int StartFrame, int EndFrame,
    bool ForceSync, string? ForceSyncMontage, NativeSkillAction[] Actions, bool OnlyExecuteWhenSourceIsMainChar,
    bool OnlyExecuteWhenSourceIsGuard)
{
    /// <summary>Montage named by a play-animation action inside this element, if any.</summary>
    public string? MontageName => Actions.FirstOrDefault(action => action.AnimationName is not null)?.AnimationName;

    /// <summary>Runtime seconds produced by the game's own timeline construction, which divides the authored
    /// frames by a 30.0f constant (TimelineActionData.ToAction, 0x183733a10, constant 0x18a8c2e38). The raw
    /// frames remain the primary authored data: this conversion is evidence only for building the runtime
    /// action, not for how a sampler or scheduler consumes those seconds.</summary>
    public double StartTime => StartFrame / NativeSkillAnimation.RuntimeFramesPerSecond;
    public double EndTime => EndFrame / NativeSkillAnimation.RuntimeFramesPerSecond;
}

/// <summary>Parsed SkillData prefix. <see cref="UnparsedOffset"/> marks the first unverified union body;
/// everything before it was consumed continuously from byte zero.</summary>
public sealed record NativeSkillTimeline(string Name, int PassiveEventActionCount, int TimelineActionCount,
    NativeSkillTimelineElement[] Elements, long ParsedBytes, long? UnparsedOffset = null, string? UnparsedReason = null)
{
    /// <summary>True only when the whole authored timeline was consumed consecutively from byte zero.</summary>
    public bool Complete => UnparsedOffset is null;

    /// <summary>Callers must surface this: an incomplete root means equipment windows were not evaluated at all,
    /// which is not the same as "the skill has no equipment animation".</summary>
    public string WindowsScope => Complete ? "authored-windows" : "not-evaluated-incomplete-root";
}

/// <summary>Proven scheduler boundary semantics for one authored action window: start uses
/// currentTime + 1e-5 &gt;= startTime and the end test happens after the tick with the same epsilon.
/// The force-sync early branch (TimelineActionProcessor 0x183103fcd..0x1831040e2) only pre-syncs the
/// montage through 0x1844df0e0 and then jumps to the element loop tail 0x1831042fb; it never enters the
/// sequence/action execution branch 0x1831040ec, so it must not make the action active early. No pre-sync
/// abstraction is exposed because no current caller needs one. Evidence: _TickInternal 0x183103db0 /
/// OnTick 0x183108560 / _CastEnd 0x1830f56b0 plus the weapon ExecuteInternal 0x1836d62a0 and OnEnd
/// 0x1836d4c00 normal-end and interrupt branches. This mirrors the authored boundary rule only.</summary>
public static class NativeSkillScheduler
{
    public const double BoundaryEpsilon = 1e-5;

    public static bool Active(double currentTime, double startTime, double endTime)
    {
        return currentTime + BoundaryEpsilon >= startTime && !(currentTime + BoundaryEpsilon >= endTime);
    }
}

/// <summary>One equipment slot trigger authored by a weapon animation action, expressed in the containing
/// element's native frames and in the game's runtime seconds.</summary>
public sealed record NativeSkillSlotTrigger(int SlotId, int ElementIndex, int StartFrame, int EndFrame, double StartTime,
    double EndTime, uint ParamBits, int ParamType, bool ActionOnEnd, uint EndParamBits, uint InterruptionParamBits,
    bool ActionOnInterrupt);

/// <summary>Equipment trigger window resolved against the controller's authored parameter names.</summary>
public sealed record NativeSkillSlotTriggerWindow(int SlotId, int StartFrame, int EndFrame, double StartTime, double EndTime,
    uint ParamBits, string? TriggerName, bool ActionOnEnd, uint EndParamBits, string? EndTriggerName, bool ActionOnInterrupt,
    uint InterruptionParamBits, string? InterruptionTriggerName);

public static class NativeSkillAnimation
{
    /// <summary>Struct wires whose member order is confirmed from the real 58/58 consumer delivery.</summary>
    private static readonly Dictionary<string, string[]> VerifiedStructs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AssignPair"] = ["int", "string", "float", "string", "string", "bool"],
        ["BattleCmdMappingModifierData"] = ["bool", "rv:BlackboardDouble", "bool", "int", "bool", "string"],
        ["FixDistanceData"] = ["blackboardDouble", "bool", "bool"],
        ["RangedData"] = ["blackboardDouble"],
        ["BuffFindSettings"] = ["list:string", "int", "rv:GameplayTagQuery"],
        ["CostData"] = ["float", "int", "float"],
        ["DamageUnit"] = ["bool", "bool", "rv:CalculationBase", "rv:BlackboardDouble", "bool", "list:CostData", "int", "int64", "list:DamageProcessorBase", "list:GameplayTag", "int", "string", "int", "rv:EffectActionCfg", "bool", "bool", "bool", "bool", "bool", "rv:HitSoundData", "int", "bool", "bool", "bool", "bool", "bool", "bool", "rv:CalculationBase", "bool", "float", "bool", "bool", "bool"],
        ["EffectActionCfg"] = ["float", "int", "float", "bool", "bool", "float", "float", "float", "int", "raw8", "int", "rv:BlackboardDouble", "string", "list:TerrainEffectData", "bool", "float", "bool", "float", "int", "bool", "bool", "float", "bool", "bool", "float", "bool", "bool", "bool", "bool", "bool", "bool", "rv:BlackboardDouble", "float", "bool", "int", "string", "float", "bool", "int", "int", "int", "int", "bool", "vec3", "rv:BlackboardVector3", "int", "float", "bool", "bool", "int", "int", "int", "bool", "int", "int", "vec3", "rv:BlackboardVector3", "bool", "vec3", "rv:BlackboardVector3", "bool", "int", "bool", "bool", "bool", "int", "int", "bool", "bool", "bool", "bool", "bool", "bool", "bool", "bool", "bool", "float", "bool", "bool", "int", "int", "int", "int", "string", "bool"],
        ["GameplayTagQuery"] = ["int", "list:int"],
        ["HitEnvData"] = ["int64", "bool", "bool", "rv:CalculationBase"],
        ["HitSoundData"] = ["string"],
        ["ShapeData"] = ["rv:BlackboardDouble", "int", "rv:BlackboardVector3", "int", "int", "bool", "rv:BlackboardVector3", "rv:BlackboardDouble", "int", "bool", "bool", "rv:BlackboardDouble", "int", "int", "rv:BlackboardDouble", "int", "rv:BlackboardVector3", "bool"],
        ["WeaponVFXOverrideConfig"] = ["bool", "bool", "bool", "bool", "bool", "bool", "bool", "bool", "bool", "string", "string", "string", "string", "string", "string", "string", "string", "string"],
        ["ImpulseDefinitionData"] = ["float", "bool", "unityAnimationCurve", "int32", "float", "int32", "float", "float", "float", "int32", "float", "int32", "int32", "float", "bool", "string", "int32", "envelopeDefinition"],
        ["EnvelopeDefinition"] = ["unityAnimationCurve", "float", "unityAnimationCurve", "float", "bool", "bool", "float"],
        ["BuffIconDurationSourceSetting"] = ["int32", "string"],
        ["CreateBuffActionInput"] = ["bool", "list:assignPair", "string", "string", "bool"],
        ["GameplayTag"] = ["int32"],
        ["GameplayTagList"] = ["list:gameplayTag"],
        ["MoveParamData"] = ["bool", "bool", "int32", "bool", "blackboardDouble", "blackboardDouble", "unityAnimationCurve", "blackboardDouble", "int32"],
        ["BuffFilterSettings"] = ["buffFindSettings", "int32"],
        ["BlackboardString"] = ["string", "bool", "string"],
        // Option@9902 (Beyond.Gameplay.Core.SwitchAction+Option): header 2 -> SequenceActionData -> BlackboardDouble.
        // Verified by the independent byte0 consumer (loop@1792 options count=5, first option carries SequenceActionData).
        ["Option"] = ["sequence", "blackboardDouble"],
    };

    /// <summary>Frame-to-second divisor proven from the runtime timeline construction (30.0f).</summary>
    public const double RuntimeFramesPerSecond = 30.0;

    private const int SkillDataMembers = 48;
    private const int ActionGroupMembers = 2;
    private const int TimelineMembers = 4;
    private const int SequenceMembers = 3;
    private const int ForceSyncMembers = 4;
    private const int ParamMembers = 5;
    private const int WeaponActionMembers = 12;
    private const int PlayAnimationMembers = 16;
    private const int SelfRotateMembers = 18;
    private const int RootMotionMembers = 24;
    private const int BlackboardMembers = 3;
    private const int AnimationCurveMembers = 3;
    private const int AnimationCurveKeyBytes = 28;
    private const int SubSpeedMembers = 5;
    private const int Blackboard4Members = 4;

    /// <summary>Union bodies whose field order is verified from this build's formatter read sequence.
    /// Every entry excludes the four AbilityActionData base members, which are always read first.</summary>
    private static readonly Dictionary<int, string[]> VerifiedBodies = new()
    {
        [66] = ["bool", "float", "bool", "bool", "target", "target"],
        [97] = ["target", "int32", "bool", "bool", "int32", "string"],
        [246] =
        [
            "bool", "blackboardDouble", "bool", "int32", "bool", "bool", "bool", "bool", "bool", "bool",
            "blackboardDouble", "bool", "raw12", "int32", "blackboardVector3", "blackboardDouble", "bool", "int32",
            "bool", "bool", "bool", "blackboardDouble", "float", "int32", "bool", "bool", "string", "blackboardDouble",
            "blackboardDouble", "blackboardDouble", "unityAnimationCurve", "int32", "int32", "float", "bool", "target",
            "blackboardDouble", "bool", "bool", "bool", "subSpeed", "subSpeed"
        ],
        [345] = ["blackboard4", "blackboard4", "target"]
        ,
        [178] = ["direction", "int32", "string", "int32", "bool", "string", "selector", "int32", "int32", "string",
                 "int32", "string", "bool", "bool"],
        [47] = ["sequence", "bool", "int32", "target", "float", "float"],
        [201] = ["bool", "sequence", "sequence", "sequence"],
        [128] = ["target", "target"]
        ,
        [14] = ["list:string"],
        [281] = ["int32", "int32", "int32", "string", "int32", "bool", "bool", "bool", "int32", "target", "int32",
                 "int32", "float", "float", "bool", "bool", "int32", "int32"],
        [408] = ["int32", "int32", "string", "int32", "int32", "string", "target"],
        [169] =
        [
            "bool", "target", "bool", "bool", "float", "target", "unityAnimationCurve", "bool", "bool", "direction",
            "bool", "int32", "bool", "blackboard4", "int32", "bool", "blackboardDouble", "bool", "raw1", "bool",
            "blackboardDouble", "bool", "blackboardDouble", "blackboardDouble"
        ]
        ,
        [60] = ["buffFindSettings", "int32", "target", "int32", "bool", "blackboardDouble"],
        [126] = ["target", "target"],
        [154] = ["bool", "int32", "list:damageUnit", "target", "hitEnvData", "bool", "target"]
        ,
        [162] = ["string", "target", "effectActionCfg", "target", "bool", "target", "bool", "bool", "bool", "bool",
                 "bool", "string", "target", "bool"]
        ,
        [55] = ["bool", "bool", "bool", "weaponVFXOverrideConfig", "bool", "int32"]
        ,
        [37] = ["int32", "float", "unityAnimationCurve", "blackboardDouble", "int32", "blackboardDouble"],
        [80] = ["int32", "blackboardDouble", "blackboardDouble"],
        [104] = ["target"],
        // Verified from the Deserialize setter order of the Beyond.MemoryPack wrappers reached by the real
        // typhoea SkillData bytes (native helper byte0 consumer, 2026-09-13): header = 4 base members + body.
        [320] = ["string", "target", "target"],
        [210] = ["target", "bool", "bool", "list:string", "string"],
        [387] = ["string", "blackboardDouble", "list:target", "bool", "list:target", "blackboardDouble",
                 "int32", "int32", "int32", "unityAnimationCurve", "bool", "bool"],
        // Verified through the same MethodSpec-driven resolver: tags reached after 320/210/387 in the real
        // typhoea binaries. 155 carries an inline 16-byte payload between two strings; 39 nests
        // BlackboardString; 16 is a long numeric action.
        [155] = ["string", "raw16", "string", "int32", "target"],
        [39] = ["target", "bool", "bool", "blackboardString", "bool", "target"],
        [16] = ["bool", "string", "bool", "float", "float", "bool", "float", "bool", "float", "bool", "bool",
                "bool", "bool", "bool", "int32", "float", "float", "float", "int32", "int32"],
        [355] = ["string", "int32", "blackboardDouble", "blackboardDouble"],
        [310] = ["buffFindSettings", "int32", "target", "string", "bool"],
        [374] = ["bool", "blackboardDouble", "list:option"],
        // header 6 == base4 + 2 strings (declaration order is reversed vs. read order; 6 is the real value).
        [114] = ["string", "string"],
        // header 7 == base4 + ignoreTargets list + timeDilationPriority int + timeScale float.
        [404] = ["list:target", "int32", "float"],
        // header 20 == base4 + the 16 ATB fields in native setter order.
        [254] = ["int32", "int32", "bool", "int32", "blackboardDouble", "int32", "blackboardDouble", "bool", "bool",
                 "bool", "bool", "target", "target", "bool", "bool", "int32"],
        // Verified from the Deserialize setter/branch order of the Beyond.MemoryPack wrappers reached after
        // 404 in the real typhoea ultimate (native helper byte0 consumer, 2026-09-13). 403 carries only the
        // 4 base members, so its body is empty; 180 compares the header byte differently but keeps base+body.
        [403] = [],
        [198] = ["bool"],
        [196] = ["string", "buffFindSettings", "string", "target"],
        [180] = ["target", "buffFindSettings", "target", "bool", "blackboardDouble", "target", "bool", "bool", "bool"],
        [152] = ["string", "unityAnimationCurve", "blackboardDouble", "string", "bool"],
        [234] = ["bool", "string", "list:target"],
        [253] = [],
        [321] = ["int32", "target", "target", "int32", "target", "target", "string"],
        [361] =
        [
            "string", "string", "int32", "string", "target", "direction", "bool", "bool", "bool", "bool",
            "list:assignPair", "bool", "target", "int32", "vec3", "int32", "string", "raw16", "bool", "bool",
            "string", "bool", "bool", "blackboardDouble", "list:string", "bool", "bool", "bool", "bool", "bool",
            "bool", "bool", "bool", "bool"
        ],
        [380] = ["sequence", "bool", "bool", "bool", "bool", "float", "blackboardDouble", "bool", "target", "bool"],
        [381] = ["string", "fixDistanceData", "rangedData", "target", "int32"],
        [78] = ["list:battleCmdMappingModifierData"]
        ,
        [222] =
        [
            "bool", "bool", "bool", "list:assignPair", "bool", "bool", "bool", "bool", "int32", "target",
            "vec3", "int32", "vec3", "vec3", "int32", "vec3", "int32", "bool", "bool", "list:presetPointDef",
            "string", "string", "target", "string", "string", "string", "bool", "int32", "target", "target",
            "bool", "int32", "int32"
        ],
        [236] = ["int32", "target", "bool", "string", "int32", "blackboardDouble"],
        [8] =
        [
            "blackboardVector3", "blackboardVector3", "blackboardDouble", "blackboardDouble", "blackboardDouble",
            "blackboardDouble", "unityAnimationCurve", "int32", "blackboardDouble", "unityAnimationCurve", "int32",
            "blackboardDouble", "float", "blackboardDouble", "bool", "string", "bool", "list:transitionConditionAnd",
            "unityAnimationCurve", "bool", "list:transitionConditionAnd", "blackboardVector3", "blackboardDouble",
            "blackboardDouble", "blackboardDouble", "blackboardDouble", "blackboardDouble", "blackboardDouble", "bool",
            "bool", "bool", "unityAnimationCurve", "blackboardVector3", "bool", "bool", "bool", "bool", "bool",
            "bool", "bool", "bool", "bool", "bool", "bool", "bool", "bool"
        ],
        [36] = ["string", "bool", "impulseDefinitionData", "int32", "vec3", "bool", "bool", "target"],
        [49] = ["bool", "bool", "bool", "blackboardDouble"],
        [101] = ["int32", "target", "bool", "blackboardDouble"],
        [140] = ["blackboardVector3", "target", "int32", "int32", "string", "int32", "float", "int32"],
        [146] =
        [
            "bool", "bool", "buffIconDurationSourceSetting", "list:createBuffActionInput", "int32", "string",
            "blackboardDouble", "bool", "list:string", "bool", "bool", "bool", "bool", "bool", "target"
        ],
        [197] =
        [
            "bool", "string", "effectActionCfg", "calculationBase", "int32", "gameplayTagList", "int32", "bool",
            "bool", "bool", "target", "bool"
        ],
        [212] = ["target", "target", "float", "int32"],
        [291] =
        [
            "bool", "bool", "blackboardDouble", "bool", "blackboardDouble", "int32", "bool", "bool",
            "blackboardDouble", "blackboardDouble", "blackboardDouble", "unityAnimationCurve", "blackboardDouble",
            "int32", "moveParamData", "bool"
        ],
        [360] =
        [
            "int32", "string", "bool", "target", "int32", "bool", "unityAnimationCurve", "blackboardDouble",
            "string", "float", "blackboardDouble", "unityAnimationCurve", "float", "bool"
        ],
        [365] = ["int32", "bool", "target", "target"]
    };
    private const int PlayAnimationTag = 277;
    private const int VisibleActionMembers = 10;

    /// <summary>Reads the verified SkillData root chain: SkillData -> ActionGroupData -> timeline actions.
    /// The walk always starts at byte zero and never skips an unknown object.</summary>
    public static NativeSkillTimeline Read(ReadOnlySpan<byte> bytes, string name)
    {
        Validation.Require(bytes.Length is > 0 and <= 8 * 1024 * 1024, "Unsupported SkillData size");
        var reader = new Reader(bytes);
        Validation.Require(reader.Header(SkillDataMembers), "SkillData formatter version differs from the verified layout");
        Validation.Require(reader.Header(ActionGroupMembers), "ActionGroupData formatter version differs from the verified layout");
        int passive = reader.Int32();
        Validation.Require(passive is >= 0 and <= 4096, "Unsupported passive action count");
        int count = reader.Int32();
        Validation.Require(count is >= 0 and <= 8192, "Unsupported timeline action count");
        var elements = new List<NativeSkillTimelineElement>();
        long? unparsedAt = passive == 0 ? null : 2;
        string? reason = passive == 0 ? null : "Passive action bodies are not decoded yet";
        if (passive == 0)
            try
            {
                for (int index = 0; index < count; index++) elements.Add(Timeline(ref reader, index));
            }
            catch (UnverifiedUnion error)
            {
                unparsedAt = error.Offset;
                reason = error.Message;
            }
        return new(name, passive, count, elements.ToArray(), reader.At, unparsedAt, reason);
    }

    /// <summary>Projects every decoded weapon animation action onto its equipment slot. Slot identity is the
    /// authored weapon id; the trigger window is the containing timeline element's native frame interval and
    /// the runtime seconds derived from it. No offset scanning and no synthetic timing are involved.</summary>
    public static NativeSkillSlotTrigger[] EquipmentTriggers(NativeSkillTimeline timeline)
    {
        Validation.Require(timeline is not null, "Missing parsed skill timeline");
        // Only an enabled action that actually writes a Trigger parameter is an active equipment trigger: the
        // authored interrupt field can carry hash 0 with param type 4 (no trigger), which must not become an
        // unnamed event. Names are never invented and enabled triggers are never dropped.
        return timeline!.Elements
            .SelectMany(element => element.Actions
                .Where(action => action.UnionTag == 54 && action.WeaponId is not null && action.IsEnable
                    && action.ParamAction is { ParamBits: not 0, ParamType: 9 })
                .Select(action => new NativeSkillSlotTrigger(action.WeaponId!.Value, element.Index, element.StartFrame,
                    element.EndFrame, element.StartTime, element.EndTime, action.ParamAction?.ParamBits ?? 0,
                    action.ParamAction?.ParamType ?? 0, action.ActionOnEnd ?? false, action.EndAction?.ParamBits ?? 0,
                    action.InterruptionAction?.ParamBits ?? 0, action.ActionOnInterrupt ?? false)))
            .OrderBy(trigger => trigger.StartFrame).ThenBy(trigger => trigger.SlotId).ToArray();
    }

    /// <summary>Maps authored Animator parameter names to their native identity, the same identity the
    /// equipment controller uses for its triggers.</summary>
    public static IReadOnlyDictionary<uint, string> ParameterIdentities(IEnumerable<string> authoredNames)
    {
        var identities = new Dictionary<uint, string>();
        foreach (string name in authoredNames)
        {
            Validation.Require(!string.IsNullOrWhiteSpace(name), "Invalid controller parameter name");
            identities.TryAdd(NativeEquipmentDefaultPoseReader.PathHash(name), name);
        }
        return identities;
    }

    /// <summary>Resolves each slot trigger window against the controller parameter identities so a caller can
    /// drive the authored equipment controller without byte scanning or name guessing.</summary>
    public static NativeSkillSlotTriggerWindow[] ResolveTriggerWindows(NativeSkillSlotTrigger[] triggers,
        IReadOnlyDictionary<uint, string> parameterIdentities)
    {
        Validation.Require(triggers is not null && parameterIdentities is not null, "Missing trigger or parameter identity input");
        var identities = parameterIdentities!;
        string? Name(uint bits) => identities.TryGetValue(bits, out string? found) ? found : null;
        return triggers!.Select(trigger => new NativeSkillSlotTriggerWindow(trigger.SlotId, trigger.StartFrame, trigger.EndFrame,
            trigger.StartTime, trigger.EndTime, trigger.ParamBits, Name(trigger.ParamBits), trigger.ActionOnEnd,
            trigger.EndParamBits, Name(trigger.EndParamBits), trigger.ActionOnInterrupt, trigger.InterruptionParamBits,
            Name(trigger.InterruptionParamBits))).ToArray();
    }

    /// <summary>Projects every authored weapon visibility action onto its containing element. The visible flag
    /// is authored data; the runtime restore policy after the action ends is not asserted here.</summary>
    public static NativeSkillVisibilityAction[] WeaponVisibility(NativeSkillTimeline timeline)
    {
        Validation.Require(timeline is not null, "Missing parsed skill timeline");
        return timeline!.Elements.SelectMany(element => element.Actions
            .Where(action => action.UnionTag == 55 && action.WeaponVisibility is not null)
            .Select(action => new NativeSkillVisibilityAction(element.Index, element.StartFrame, element.EndFrame,
                element.StartTime, element.EndTime, action.WeaponVisibility!))).ToArray();
    }

    private static NativeSkillTimelineElement Timeline(ref Reader reader, int index)
    {
        long start = reader.At;
        Validation.Require(reader.Header(TimelineMembers), "TimelineActionData formatter version differs from the verified layout");
        int endFrame = reader.Int32();
        var sequence = Sequence(ref reader);
        int startFrame = reader.Int32();
        Validation.Require(reader.Header(ForceSyncMembers), "ForceSyncAnimData formatter version differs from the verified layout");
        bool forceSync = reader.Boolean();
        string? montage = reader.String();
        reader.Single();
        reader.Int32();
        return new(index, start, reader.At, startFrame, endFrame, forceSync, montage, sequence.Actions,
            sequence.MainChar, sequence.Guard);
    }

    private static (NativeSkillAction[] Actions, bool MainChar, bool Guard) Sequence(ref Reader reader)
    {
        Validation.Require(reader.Header(SequenceMembers), "SequenceActionData formatter version differs from the verified layout");
        int count = reader.Int32();
        Validation.Require(count is >= 0 and <= 4096, "Unsupported sequence action count");
        var actions = new NativeSkillAction[count];
        for (int i = 0; i < count; i++) actions[i] = Action(ref reader);
        bool guard = reader.Boolean();
        bool mainChar = reader.Boolean();
        return (actions, mainChar, guard);
    }

    private static NativeSkillAction Action(ref Reader reader)
    {
        long offset = reader.At;
        int tag = reader.UnionTag();
        if (tag == 54)
        {
            Validation.Require(reader.Header(WeaponActionMembers), "CharWeaponAnimationActionData formatter version differs from the verified layout");
            var (enable, level, priorityOffset, serverIndex) = Base(ref reader);
            bool onEnd = reader.Boolean();
            bool onInterrupt = reader.Boolean();
            var end = Param(ref reader);
            var interrupt = Param(ref reader);
            float before = reader.Single();
            bool overrideTime = reader.Boolean();
            var param = Param(ref reader);
            int weapon = reader.Int32();
            return new(tag, offset, reader.At, enable, level, priorityOffset, serverIndex, weapon, onEnd, onInterrupt,
                param, end, interrupt, before, overrideTime);
        }
        if (tag == 55)
        {
            // CharWeaponVisibleActionData (wrapper 0x183e39680, named setters includeAllWeapons/overrideVFX/
            // showVFX/vfxOverrideConfig/visible/weaponIndex). The VFX override payload is consumed exactly as
            // its own formatter writes it; an empty payload never cancels the visible flag.
            Validation.Require(reader.Header(VisibleActionMembers), "CharWeaponVisibleActionData formatter version differs from the verified layout");
            var (enable, level, priorityOffset, serverIndex) = Base(ref reader);
            bool includeAllWeapons = reader.Boolean();
            bool overrideVfx = reader.Boolean();
            bool showVfx = reader.Boolean();
            Consume(ref reader, "weaponVFXOverrideConfig");
            bool visible = reader.Boolean();
            int weaponIndex = reader.Int32();
            return new(tag, offset, reader.At, enable, level, priorityOffset, serverIndex,
                WeaponVisibility: new(weaponIndex, includeAllWeapons, overrideVfx, showVfx, visible));
        }
        if (tag == PlayAnimationTag)
        {
            Validation.Require(reader.Header(PlayAnimationMembers), "PlayAnimationActionData formatter version differs from the verified layout");
            var (enable, level, priorityOffset, serverIndex) = Base(ref reader);
            string? animation = reader.String();
            float blendDuration = reader.Single();
            reader.Single();
            reader.Int32();
            float duration = reader.Single();
            reader.Boolean();
            reader.Boolean();
            var onEnd = Sequence(ref reader);
            float playbackSpeed = reader.Single();
            float startTime = reader.Single();
            reader.String();
            reader.Boolean();
            return new(tag, offset, reader.At, enable, level, priorityOffset, serverIndex, AnimationName: animation,
                AnimationDuration: duration, AnimationStartTime: startTime, AnimationPlaybackSpeed: playbackSpeed,
                OnEndActions: onEnd.Actions, OnlyExecuteWhenSourceIsMainChar: onEnd.MainChar,
                OnlyExecuteWhenSourceIsGuard: onEnd.Guard);
        }
        if (tag == 324)
        {
            Validation.Require(reader.Header(SelfRotateMembers), "SelfRotateAction formatter version differs from the verified layout");
            var (enable, level, priorityOffset, serverIndex) = Base(ref reader);
            reader.Direction();
            for (int i = 0; i < 5; i++) reader.Boolean();
            reader.String();
            reader.Int32();
            reader.Int32();
            reader.Int32();
            reader.Single();
            reader.Int32();
            reader.Target();
            reader.Boolean();
            return new(tag, offset, reader.At, enable, level, priorityOffset, serverIndex);
        }
        if (tag == 153)
        {
            Validation.Require(reader.Header(RootMotionMembers), "CustomRootMotionAction formatter version differs from the verified layout");
            var (enable, level, priorityOffset, serverIndex) = Base(ref reader);
            reader.String();
            BlackboardDouble(ref reader);
            AnimationCurve(ref reader);
            reader.Boolean();
            reader.Boolean();
            BlackboardDouble(ref reader);
            reader.Boolean();
            reader.Int32();
            reader.Boolean();
            BlackboardDouble(ref reader);
            reader.Target();
            BlackboardDouble(ref reader);
            reader.Int32();
            BlackboardDouble(ref reader);
            BlackboardDouble(ref reader);
            BlackboardDouble(ref reader);
            reader.Int32();
            reader.Boolean();
            reader.Boolean();
            reader.Boolean();
            return new(tag, offset, reader.At, enable, level, priorityOffset, serverIndex);
        }
        if (VerifiedBodies.TryGetValue(tag, out string[]? body))
        {
            Validation.Require(reader.Header(body.Length + 4), "Union body member count differs from the verified layout at byte " + reader.At);
            var (enable, level, priorityOffset, serverIndex) = Base(ref reader);
            foreach (string kind in body) Consume(ref reader, kind);
            return new(tag, offset, reader.At, enable, level, priorityOffset, serverIndex);
        }
        throw new UnverifiedUnion($"Unimplemented union tag {tag} at {offset}; this build's layout is not verified here", offset);
    }

    /// <summary>Consumes one verified member kind. Unknown kinds fail closed at the exact offset.</summary>
    private static void Consume(ref Reader reader, string kind)
    {
        // The confirmed delivery uses rv:/list: prefixes and short primitive names; normalise them here so the
        // tables stay verbatim copies of the evidence.
        if (kind.StartsWith("rv:", StringComparison.Ordinal)) { Consume(ref reader, kind[3..]); return; }
        if (kind.StartsWith("list:", StringComparison.Ordinal))
        {
            // MemoryPack list: int32 count, -1 means null, then exactly that many elements. A null or empty
            // list needs no element body; an unknown non-empty element kind still fails closed.
            string element = kind[5..];
            int count = reader.Int32();
            if (count < 0) return;
            for (int index = 0; index < count; index++) Consume(ref reader, element);
            return;
        }
        // Native plans carry the resolved definition id as a "@<n>" suffix; the wire is identical for the
        // verified type, so the suffix is stripped before dispatching on the member kind.
        int definition = kind.IndexOf('@');
        if (definition >= 0) kind = kind[..definition];
        switch (kind.ToLowerInvariant())
        {
            case "bool": reader.Boolean(); return;
            case "int": reader.Int32(); return;
            case "int64": reader.Skip(8); return;
            case "int32": reader.Int32(); return;
            case "float": reader.Single(); return;
            case "string": reader.String(); return;
            case "raw1": reader.Byte(); return;
            case "raw8": reader.Skip(8); return;
            case "vec3": reader.Skip(12); return;
            case "calculationbase":
                // CalculationBase is a union: this sample only hits tag 2 ->
                // DefiniteValueCalculation with header 3 (bool, BlackboardDouble, BlackboardDouble).
                {
                    // Nullable union value: a 0xFF byte means no authored value, so there is no union body.
                    byte first = reader.Byte();
                    if (first == 255) return;
                    int tag = first == 250 ? reader.UInt16() : first;
                    if (tag == 2)
                    {
                        if (!reader.Header(3)) return;
                        foreach (string part in (string[])["bool", "blackboardDouble", "blackboardDouble"]) Consume(ref reader, part);
                        return;
                    }
                    if (tag == 0)
                    {
                        if (!reader.Header(1)) return;
                        Consume(ref reader, "blackboardDouble");
                        return;
                    }
                    if (tag == 3)
                    {
                        if (!reader.Header(4)) return;
                        foreach (string part in (string[])["blackboardDouble", "int32", "blackboardDouble", "int32"]) Consume(ref reader, part);
                        return;
                    }
                    throw new UnverifiedUnion("Unimplemented CalculationBase union tag " + tag + " at " + reader.At, reader.At);
                }
            case "target": reader.Target(); return;
            case "direction": reader.Direction(); return;
            case "selector": reader.Selector(); return;
            case "sequence":
                Sequence(ref reader);
                return;
            case "raw12": reader.Skip(12); return;
            case "raw16": reader.Skip(16); return;
            case "blackboarddouble": BlackboardDouble(ref reader); return;
            case "blackboardstring":
                if (!reader.Header(3)) return;
                reader.String(); reader.Boolean(); reader.String();
                return;
            case "unityanimationcurve": AnimationCurve(ref reader); return;
            case "blackboardvector3":
                BlackboardVector3(ref reader);
                return;
            case "subspeed":
                if (!reader.Header(SubSpeedMembers)) return;
                foreach (string part in (string[])["blackboardDouble", "string", "blackboardDouble", "unityAnimationCurve", "int32"]) Consume(ref reader, part);
                return;
            case "blackboard4":
                if (!reader.Header(Blackboard4Members)) return;
                foreach (string part in (string[])["string", "bool", "int32", "bool"]) Consume(ref reader, part);
                return;
            case "bufffindsettings":
                if (!reader.Header(3)) return;
                foreach (string part in (string[])["list:string", "int32", "gameplayTagQuery"]) Consume(ref reader, part);
                return;
            case "gameplaytagquery":
                if (!reader.Header(2)) return;
                foreach (string part in (string[])["int32", "list:int32"]) Consume(ref reader, part);
                return;
            default:
                if (VerifiedStructs.TryGetValue(kind, out string[]? fields))
                {
                    if (!reader.Header(fields.Length)) return;
                    foreach (string field in fields) Consume(ref reader, field);
                    return;
                }
                throw new UnverifiedUnion($"Unimplemented field kind {kind} at {reader.At}; this build's layout is not verified here", reader.At);
        }
    }

    /// <summary>BlackboardDouble is a 3-member struct: optional key, flag, single value.</summary>
    private static void BlackboardDouble(ref Reader reader)
    {
        if (!reader.Header(BlackboardMembers)) return;
        reader.String();
        reader.Boolean();
        reader.Single();
    }

    /// <summary>BlackboardVector3 is a 3-member struct of BlackboardDouble components.</summary>
    private static void BlackboardVector3(ref Reader reader)
    {
        if (!reader.Header(3)) return;
        for (int i = 0; i < 3; i++) BlackboardDouble(ref reader);
    }

    /// <summary>UnityEngine.AnimationCurve keyframes are raw 28-byte entries without per-key headers.</summary>
    private static void AnimationCurve(ref Reader reader)
    {
        if (!reader.Header(AnimationCurveMembers)) return;
        reader.Int32();
        reader.Int32();
        int keys = reader.Int32();
        if (keys == -1) return;
        Validation.Require(keys is >= 0 and <= 65536, "Unsupported animation curve key count");
        reader.Skip((long)keys * AnimationCurveKeyBytes);
    }

    private static (bool Enable, int Level, int PriorityOffset, int ServerIndex) Base(ref Reader reader)
        => (reader.Boolean(), reader.Int32(), reader.Int32(), reader.Int32());

    private static NativeSkillParamAction? Param(ref Reader reader)
    {
        if (!reader.Header(ParamMembers)) return null;
        return new(reader.UInt32(), reader.Boolean(), reader.Single(), reader.Int32(), reader.Int32());
    }

    private sealed class UnverifiedUnion(string message, long offset) : Exception(message)
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
            Validation.Require(actual == members, "Formatter member count differs from the verified layout at byte " + at + ": expected " + members + ", read " + actual);
            return true;
        }
        public int UnionTag()
        {
            long unionAt = At;
            byte tag = Byte();
            if (tag == 250) return UInt16();
            Validation.Require(tag < 251, "Unsupported union header " + tag + " at " + unionAt);
            return tag;
        }
        public byte Byte() { Validation.Require(At < data.Length, "SkillData ended inside a member"); return data[(int)At++]; }
        public bool Boolean() => Byte() != 0;
        public ushort UInt16() { var value = BinaryPrimitives.ReadUInt16LittleEndian(Slice(2)); At += 2; return value; }
        public uint UInt32() { var value = BinaryPrimitives.ReadUInt32LittleEndian(Slice(4)); At += 4; return value; }
        public int Int32() => (int)UInt32();
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
        public void Direction()
        {
            if (!Header(8)) return;
            Boolean(); Boolean(); Int32(); Boolean(); Target(); Int32(); Target(); Int32();
        }
        public void Target()
        {
            if (!Header(13)) return;
            Direction(); String(); Boolean(); Int32(); Boolean(); String();
            Selector();
            Int32(); Int32(); Int32(); String(); String(); Int32();
        }
        public void Selector()
        {
            Validation.Require(Header(3), "SelectorData formatter version differs from the verified layout");
            byte finder = Byte();
            if (finder == 255) { /* no finder authored */ }
            else
            {
                int tag = finder;
                if (tag == 250) tag = UInt16();
                switch (tag)
                {
                    // 0-member finders proved from their own wrapper deserializers: the object header must be
                    // 0 (0xff means null) and no member is read.
                    case 2: Header(0); break;   // CharacterTeamFinder, wrapper 0x183f50850
                    case 10: Header(0); break;  // MainTargetFinder, wrapper 0x183ff0a70
                    case 3:                     // FixedPointFinder
                        Validation.Require(Header(4), "FixedPointFinder layout differs from the confirmed header 4");
                        Skip(12); Skip(16); BlackboardDouble(ref this); Boolean();
                        break;
                    case 7: HitBoxFinder(); break;
                    default: throw new InvalidDataException("Unimplemented selector finder union tag " + tag + " at " + At);
                }
            }
            // Native SelectorData order is Finder -> PostProcessor list -> Validator list (wrapper 0x1837f9560).
            // Post-processor elements (def10232) are not decoded; validator elements are unions whose tag 9 is
            // MainCharacterValidator with a legal header 0 and no members.
            int processors = Int32();
            for (int index = 0; index < processors; index++)
            {
                byte processorTag = Byte();
                int tag = processorTag == 250 ? UInt16() : processorTag;
                Validation.Require(tag == 7, "Unimplemented selector post-processor union tag " + tag + " at " + (At - 1));
                Validation.Require(Header(6), "PriorityFilter layout differs from the confirmed header 6");
                Consume(ref this, "buffFilterSettings");
                Int32(); Boolean(); Int32(); Boolean(); Int32();
            }
            int validators = Int32();
            for (int index = 0; index < validators; index++)
            {
                byte first = Byte();
                if (first == 250) { first = (byte)(UInt16() & 0xFF); }
                Validation.Require(first == 9, "Unimplemented selector validator union tag " + first + " at " + (At - 1));
                Validation.Require(Header(0), "MainCharacterValidator layout differs from the confirmed header 0");
            }
        }
        private void RequireEmptyList(string label)
        {
            int count = Int32();
            Validation.Require(count == 0, "Non-empty selector list is not decoded: " + label + " count=" + count);
        }
        /// <summary>HitBoxFinder (selector finder union tag 7): 8 members per the confirmed layout.</summary>
        private void HitBoxFinder()
        {
            Validation.Require(Header(8), "HitBoxFinder formatter version differs from the confirmed layout");
            Boolean(); Boolean(); Boolean(); Int32(); Int32(); Boolean();
            int shapes = Int32();
            Validation.Require(shapes >= 0, "HitBoxFinder shape list is null");
            for (int i = 0; i < shapes; i++) Shape();
            Int32();
        }
        /// <summary>Selector HitBoxFinder ShapeData (formatter 0x1832d07f0, member header 18). This is the
        /// exact type reached from the selector finder union; it is NOT the other same-named ShapeData in this
        /// build, so the wire below is bound to this MethodSpec's own layout.</summary>
        private void Shape()
        {
            Validation.Require(Header(18), "Selector HitBoxFinder ShapeData formatter version differs from the confirmed layout");
            BlackboardDouble(ref this);
            Int32();
            BlackboardVector3(ref this);
            Int32(); Int32();
            Boolean();
            BlackboardVector3(ref this);
            BlackboardDouble(ref this);
            Int32();
            Boolean(); Boolean();
            BlackboardDouble(ref this);
            Int32(); Int32();
            BlackboardDouble(ref this);
            Int32();
            BlackboardVector3(ref this);
            Boolean();
        }
        public void Skip(long count)
        {
            Validation.Require(count >= 0 && At + count <= data.Length, "SkillData ended inside a member");
            At += count;
        }
        private ReadOnlySpan<byte> Slice(int length) { Validation.Require(At + length <= data.Length, "SkillData ended inside a member"); return data.Slice((int)At, length); }
    }
}
