using System.Text.Json;
namespace Sora.Core;

public sealed record NativeControllerTriggerTransition(string TriggerName, uint TriggerHash, int ParameterType,
    double Duration, double Offset, bool FixedDuration, string Status);
public sealed record NativeControllerStateClip(string ControllerId, int StateMachineIndex, uint StateNameHash,
    string StateName, string OriginalSourceId, string ClipId, double Speed, double CycleOffset, bool Loop,
    string Status = "native-single-leaf-state", NativeControllerTriggerTransition[]? TriggerTransitions = null);

/// <summary>One authored controller transition: ordinary state transitions and any-state transitions, with
/// the single trigger condition the transition is gated by (when it has exactly one).</summary>
public sealed record NativeControllerStateTransition(int StateMachineIndex, int FromStateIndex, int ToStateIndex,
    bool FromAnyState, uint TriggerHash, string? TriggerName, int ConditionMode, bool HasExitTime, double Duration,
    double Offset);

/// <summary>Observed controller state constants and native clip pointers; does not evaluate transitions.</summary>
public static class NativeAnimationControllerStates
{
    public static NativeControllerStateTransition[] ReadTransitions(GameResources game, ResolvedAsset controller)
    {
        var seen = new HashSet<string>();
        while (controller.Object.ClassId == 221)
        {
            Validation.Require(seen.Count < 16 && seen.Add(NativePrefabHierarchy.Identity(controller)), "Cyclic controller transition chain");
            var pointer = NativeObjectProjection.Json(controller.Object, "m_Controller").GetProperty("m_Controller");
            controller = game.Resolve(controller.Cab, pointer) ?? throw new InvalidDataException("Controller transition base missing");
        }
        Validation.Require(controller.Object.ClassId == 91, "Unsupported controller transition source");
        var data = NativeObjectProjection.Json(controller.Object, "m_Controller", "m_TOSData", "m_AnimationClips");
        var names = A(data, "m_TOSData").Select(x => x.GetString()!).Distinct(StringComparer.Ordinal)
            .GroupBy(NativeEquipmentDefaultPoseReader.PathHash).ToDictionary(g => g.Key, g => g.Count() == 1 ? g.First() : null);
        string? Name(uint hash) => names.TryGetValue(hash, out var found) ? found : null;
        var result = new List<NativeControllerStateTransition>();
        var machines = A(data.GetProperty("m_Controller"), "m_StateMachineArray");
        for (int machine = 0; machine < machines.Length; machine++)
        {
            var machineData = machines[machine].GetProperty("data");
            var states = A(machineData, "m_StateConstantArray");
            for (int index = 0; index < states.Length; index++)
            {
                foreach (var row in A(states[index].GetProperty("data"), "m_TransitionConstantArray"))
                    result.Add(Transition(machine, index, false, row.GetProperty("data"), Name));
                foreach (var row in A(machineData, "m_AnyStateTransitionConstantArray"))
                {
                    var transition = row.GetProperty("data");
                    if (transition.GetProperty("m_DestinationState").GetInt32() != index) continue;
                    result.Add(Transition(machine, index, true, transition, Name));
                }
            }
        }
        return result.ToArray();
    }

    private static NativeControllerStateTransition Transition(int machine, int state, bool anyState, JsonElement transition, Func<uint, string?> name)
    {
        var conditions = A(transition, "m_ConditionConstantArray");
        uint hash = 0; int mode = -1;
        if (conditions.Length == 1)
        {
            var condition = conditions[0].GetProperty("data");
            hash = condition.GetProperty("m_EventID").GetUInt32();
            mode = condition.GetProperty("m_ConditionMode").GetInt32();
        }
        return new(machine, state, transition.GetProperty("m_DestinationState").GetInt32(), anyState, hash,
            hash == 0 ? null : name(hash), mode, transition.GetProperty("m_HasExitTime").GetBoolean(),
            transition.GetProperty("m_TransitionDuration").GetDouble(), transition.GetProperty("m_TransitionOffset").GetDouble());
    }
    private static JsonElement[] A(JsonElement data,string key) {
        var array=data.GetProperty(key).GetProperty("Array");
        Validation.Require(array.GetArrayLength()<=16384,"Controller state array exceeds bound");
        return array.EnumerateArray().ToArray();
    }
    public static NativeControllerStateClip[] Read(GameResources game,ResolvedAsset controller)
    {
        string effectiveId=NativePrefabHierarchy.Identity(controller);
        var effective=NativeAnimationController.Read(game,controller).ToDictionary(c=>c.OriginalSourceId);
        var seen=new HashSet<string>();
        while(controller.Object.ClassId==221) {
            Validation.Require(seen.Count<16&&seen.Add(NativePrefabHierarchy.Identity(controller)),"Cyclic controller state chain");
            var pointer=NativeObjectProjection.Json(controller.Object,"m_Controller").GetProperty("m_Controller");
            controller=game.Resolve(controller.Cab,pointer)??throw new InvalidDataException("Controller state base missing");
        }
        Validation.Require(controller.Object.ClassId==91,"Unsupported controller state source");
        var data=NativeObjectProjection.Json(controller.Object,"m_Controller","m_TOSData","m_AnimationClips");
        var names=A(data,"m_TOSData").Select(x=>x.GetString()!).Distinct(StringComparer.Ordinal)
            .GroupBy(NativeEquipmentDefaultPoseReader.PathHash).ToDictionary(g=>g.Key,g=>g.ToArray());
        var pointers=A(data,"m_AnimationClips");
        var result=new List<NativeControllerStateClip>();
        var machines=A(data.GetProperty("m_Controller"),"m_StateMachineArray");
        var parameters=A(data.GetProperty("m_Controller").GetProperty("m_Values").GetProperty("data"),"m_ValueArray");
        for(int machine=0;machine<machines.Length;machine++)
        {
        var machineData=machines[machine].GetProperty("data");
        var stateRows=A(machineData,"m_StateConstantArray");
        for(int stateIndex=0;stateIndex<stateRows.Length;stateIndex++) {
            var state=stateRows[stateIndex].GetProperty("data");
            uint hash=state.GetProperty("m_NameID").GetUInt32();
            if(!names.TryGetValue(hash,out var matches)||matches.Length!=1)continue;
            var trees=A(state,"m_BlendTreeConstantArray");
            if(trees.Length!=1)continue;
            var nodes=A(trees[0].GetProperty("data"),"m_NodeArray");
            if(nodes.Length!=1)continue;
            var node=nodes[0].GetProperty("data");
            if(A(node,"m_ChildIndices").Length!=0)continue;
            int index=node.GetProperty("m_ClipID").GetInt32();
            if(index<0||index>=pointers.Length)continue;
            var original=game.Resolve(controller.Cab,pointers[index]);
            if(original is null||!effective.TryGetValue(NativePrefabHierarchy.Identity(original),out var clip))continue;
            double speed=state.GetProperty("m_Speed").GetDouble();
            double offset=state.GetProperty("m_CycleOffset").GetDouble()+node.GetProperty("m_CycleOffset").GetDouble();
            bool dynamic=state.GetProperty("m_SpeedParamID").GetUInt32()!=0||state.GetProperty("m_TimeParamID").GetUInt32()!=0
                ||state.GetProperty("m_CycleOffsetParamID").GetUInt32()!=0||state.GetProperty("m_MirrorParamID").GetUInt32()!=0;
            bool mirror=state.GetProperty("m_Mirror").GetBoolean()||node.GetProperty("m_Mirror").GetBoolean();
            var triggers=new List<NativeControllerTriggerTransition>();
            foreach(var transitionRow in A(machineData,"m_AnyStateTransitionConstantArray")) {
                var transition=transitionRow.GetProperty("data");
                if(transition.GetProperty("m_DestinationState").GetInt32()!=stateIndex)continue;
                var conditions=A(transition,"m_ConditionConstantArray");
                if(conditions.Length!=1)continue;
                var condition=conditions[0].GetProperty("data");
                uint trigger=condition.GetProperty("m_EventID").GetUInt32();
                if(condition.GetProperty("m_ConditionMode").GetInt32()!=1||!names.TryGetValue(trigger,out var triggerNames)||triggerNames.Length!=1)continue;
                var parameter=parameters.Where(p=>p.GetProperty("m_ID").GetUInt32()==trigger).ToArray();
                if(parameter.Length!=1||parameter[0].GetProperty("m_Type").GetInt32()!=9)continue;
                bool direct=!transition.GetProperty("m_HasExitTime").GetBoolean()&&A(transition,"m_TransitionBlockParamConstantArray").Length==0;
                triggers.Add(new(triggerNames[0],trigger,9,transition.GetProperty("m_TransitionDuration").GetDouble(),
                    transition.GetProperty("m_TransitionOffset").GetDouble(),transition.GetProperty("m_HasFixedDuration").GetBoolean(),
                    direct?"native-trigger-any-state":"native-trigger-requires-state-evaluator"));
            }
            result.Add(new(effectiveId,machine,hash,matches[0],clip.OriginalSourceId,NativePrefabHierarchy.Identity(clip.Clip),speed,offset,state.GetProperty("m_Loop").GetBoolean(),
                dynamic||mirror||speed!=1||offset!=0?"native-state-timing-requires-evaluator":"native-single-leaf-state",triggers.ToArray()));
        }
        }
        return result.ToArray();
    }
}
