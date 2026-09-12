using System.Text.Json;
namespace Sora.Core;

/// <summary>Fail-closed acceptance of inert metadata for the initial-only reader.</summary>
public static class NativeEquipmentInitialController
{
    static JsonElement[] A(JsonElement p,string field,int maximum)
    {
        var a=p.GetProperty(field).GetProperty("Array");
        Validation.Require(a.ValueKind==JsonValueKind.Array&&a.GetArrayLength()<=maximum,"Unsupported initial controller array: "+field);
        return a.EnumerateArray().ToArray();
    }
    static void False(JsonElement p,params string[] fields){foreach(string field in fields)Validation.Require(!p.GetProperty(field).GetBoolean(),"Unsupported initial controller flag: "+field);}
    static void Zero(JsonElement p,params string[] fields){foreach(string field in fields)Validation.Require(p.GetProperty(field).GetUInt32()==0,"Unsupported initial controller parameter: "+field);}
    /// <summary>Outcome of the native Human predicate read from the resolved Avatar. Only an explicitly null
    /// Human pointer or an explicitly empty Human skeleton node array is positively nonhuman; any missing or
    /// unexpected member is unresolved and must fail closed.</summary>
    public sealed record AvatarHumanEvidence(bool HumanPointerPresent, bool HumanPointerNull, int? SkeletonNodeCount,
        string Status)
    {
        public bool Nonhuman => HumanPointerNull || SkeletonNodeCount == 0;
        public bool Resolved => HumanPointerNull || SkeletonNodeCount is not null;
    }

    /// <summary>Reads the native Human predicate path Avatar.m_Avatar.data.m_Human(.data).m_Skeleton.data.m_Node.
    /// Array. The Avatar asset itself is the AvatarConstant wrapper, so the predicate lives one wrapper deeper
    /// than the asset root.</summary>
    public static AvatarHumanEvidence AvatarHumanSkeleton(ResolvedAsset? avatar)
    {
        // Only the native predicate itself decides: the Human offset pointer is explicitly null, or the Human
        // skeleton node array exists and is explicitly empty. Peripheral nulls (a null Avatar wrapper, a null
        // skeleton/data wrapper) are not the proven Human pointer and stay Unknown, so they fail closed.
        if (avatar is null) return new(false, false, null, "avatar-pointer-null");
        var data = JsonSerializer.SerializeToElement(avatar.Object.Data, WireJson.Options);
        if (data.ValueKind != JsonValueKind.Object) return new(false, false, null, "avatar-root-not-object");
        if (!data.TryGetProperty("m_Avatar", out var wrapper)) return new(false, false, null, "avatar-wrapper-absent");
        if (wrapper.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return new(false, false, null, "avatar-wrapper-null");
        if (wrapper.ValueKind != JsonValueKind.Object) return new(false, false, null, "avatar-wrapper-not-object");
        if (!wrapper.TryGetProperty("m_Human", out var human)) return new(false, false, null, "human-member-absent");
        if (human.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return new(true, true, null, "human-pointer-null");
        if (human.ValueKind != JsonValueKind.Object) return new(false, false, null, "human-pointer-not-object");
        if (!human.TryGetProperty("data", out var humanData)) return new(false, false, null, "human-data-absent");
        if (humanData.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return new(false, false, null, "human-data-null");
        if (humanData.ValueKind != JsonValueKind.Object) return new(false, false, null, "human-data-not-object");
        if (!humanData.TryGetProperty("m_Skeleton", out var skeleton)) return new(false, false, null, "human-skeleton-absent");
        if (skeleton.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return new(false, false, null, "human-skeleton-null");
        if (skeleton.ValueKind != JsonValueKind.Object) return new(false, false, null, "human-skeleton-not-object");
        if (!skeleton.TryGetProperty("data", out var skeletonData)) return new(false, false, null, "human-skeleton-data-absent");
        if (skeletonData.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return new(false, false, null, "human-skeleton-data-null");
        if (skeletonData.ValueKind != JsonValueKind.Object) return new(false, false, null, "human-skeleton-data-not-object");
        if (!skeletonData.TryGetProperty("m_Node", out var node)) return new(false, false, null, "human-skeleton-node-absent");
        if (node.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return new(false, false, null, "human-skeleton-node-null");
        if (node.ValueKind != JsonValueKind.Object) return new(false, false, null, "human-skeleton-node-not-object");
        if (!node.TryGetProperty("Array", out var array)) return new(false, false, null, "human-skeleton-node-array-absent");
        if (array.ValueKind != JsonValueKind.Array) return new(false, false, null, "human-skeleton-node-array-not-array");
        return new(true, false, array.GetArrayLength(), array.GetArrayLength() == 0 ? "human-skeleton-empty" : "human-skeleton-present");
    }

    public static void Validate(JsonElement document,bool meshSpaceAccepted=false)
    {
        var graph=document.GetProperty("m_Controller");var layers=A(graph,"m_LayerArray",1);var machines=A(graph,"m_StateMachineArray",1);
        Validation.Require(layers.Length==1&&machines.Length==1,"Initial pose needs one layer and machine");
        var layer=layers[0].GetProperty("data");Zero(layer,"m_StateMachineIndex","m_StateMachineSynchronizedLayerIndex","(int&)m_LayerBlendingMode");
        False(layer,"m_IKPass","m_SyncedLayerAffectsTiming","m_UseThreePoseBlender","m_PostProcessLayer",
            "m_UseIdentifyValues","m_ConvertSimpleBlend","m_DisableRootMotionPass");
        if(!meshSpaceAccepted)False(layer,"m_MeshSpace");
        A(layer.GetProperty("m_SkeletonMask").GetProperty("data"),"m_Data",0);
        var mask=layer.GetProperty("m_BodyMask");
        Validation.Require(mask.GetProperty("word0").GetUInt32()==uint.MaxValue&&mask.GetProperty("word1").GetUInt32()==uint.MaxValue&&mask.GetProperty("word2").GetUInt32()==33554431,"Unsupported initial layer body mask");
        // Exact observed single base-layer profile; other optimization/culling policies are unestablished.
        Validation.Require(layer.GetProperty("m_DefaultWeight").GetDouble()==0&&layer.GetProperty("(int&)m_layerOptMode").GetUInt32()==65470464&&layer.GetProperty("m_LODThreshold").GetInt32()==999&&layer.GetProperty("m_AbilityThreshold").GetInt32()==999,"Unsupported initial layer weight/optimization/culling profile");
        Validation.Require(layer.GetProperty("m_curveMaskIndex").GetUInt32()==uint.MaxValue,"Unsupported initial layer curve mask");
        var machine=machines[0].GetProperty("data");False(machine,"m_ImmediateTransition","m_InterruptDynamicTransitions");
        Validation.Require(machine.GetProperty("m_SynchronizedLayerCount").GetInt32()==1,"Unsupported synchronized machine");
        var states=A(machine,"m_StateConstantArray",4096);int index=machine.GetProperty("m_DefaultState").GetInt32();
        Validation.Require(index>=0&&index<states.Length,"Invalid default state");
        int entries=0;
        foreach(var row in A(machine,"m_SelectorStateConstantArray",64))
        {
            var selector=row.GetProperty("data");bool entry=selector.GetProperty("m_IsEntry").GetBoolean();if(entry)entries++;
            Validation.Require(selector.GetProperty("m_FullPathID").GetUInt32()==layer.GetProperty("m_Binding").GetUInt32(),"Selector belongs to another machine");
            var transitions=A(selector,"m_TransitionConstantArray",64);Validation.Require(transitions.Length>0,"Empty initial selector");
            foreach(var item in transitions)
            {
                var transition=item.GetProperty("data");A(transition,"m_ConditionConstantArray",0);A(transition,"m_TransitionBlockParamConstantArray",0);
                Validation.Require(transition.GetProperty("m_Destination").GetInt32()==(entry?index:30000),"Unsupported selector/entry destination");
            }
        }
        Validation.Require(entries==1,"Expected one unconditional default entry selector");
        var state=states[index].GetProperty("data");
        False(state,"m_Mirror","m_IKOnFeet");Zero(state,"m_SpeedParamID","m_MirrorParamID","m_CycleOffsetParamID","m_TimeParamID","m_SyncGroupID","m_SyncGroupRole");
        Validation.Require(state.GetProperty("m_WriteDefaultValues").GetBoolean(),"Unsupported initial default-value policy");
        A(state,"m_StateParameterConstantArray",0);
        Validation.Require(state.GetProperty("m_Speed").GetDouble()==1&&state.GetProperty("m_CycleOffset").GetDouble()==0,"Unsupported state time policy");
        var defaults=graph.GetProperty("m_DefaultValues").GetProperty("data");
        foreach(string field in new[]{"m_FloatValues","m_IntValues","m_PositionValues","m_QuaternionValues","m_ScaleValues"})A(defaults,field,0);
        var bools=A(defaults,"m_BoolValues",4096);Validation.Require(bools.All(v=>!v.GetBoolean()),"Active initial triggers");
        var parameters=A(graph.GetProperty("m_Values").GetProperty("data"),"m_ValueArray",4096);
        Validation.Require(parameters.All(p=>p.GetProperty("m_Type").GetInt32()==9&&p.GetProperty("m_Index").GetInt32()>=0&&p.GetProperty("m_Index").GetInt32()<bools.Length),"Unsupported initial parameter type/index");
        var ids=parameters.Select(p=>p.GetProperty("m_ID").GetUInt32()).ToHashSet();Validation.Require(ids.Count==parameters.Length,"Duplicate parameter identity");
        // The initial pose is the default state at t=0. An authored transition cannot change that pose when it
        // provably cannot fire at t=0: a trigger-conditioned branch needs an If-trigger that is currently false
        // (every declared trigger bool is validated false above), and an unconditional branch needs a future
        // exit time. Both facts are read from the controller JSON, never inferred from a transition count.
        foreach(var branch in A(state,"m_TransitionConstantArray",4096))
        {
            var transition=branch.GetProperty("data");
            var branchConditions=A(transition,"m_ConditionConstantArray",4096);
            if(branchConditions.Length>0)
            {
                Validation.Require(branchConditions.All(c=>c.GetProperty("data").GetProperty("m_ConditionMode").GetInt32()==1
                    &&ids.Contains(c.GetProperty("data").GetProperty("m_EventID").GetUInt32())),"Unsupported conditioned state transition on the default equipment state");
            }
            else
            {
                Validation.Require(transition.GetProperty("m_HasExitTime").GetBoolean()
                    &&transition.GetProperty("m_ExitTime").GetDouble()>0,"Unconditional default equipment transition can fire at t=0");
            }
        }
        foreach(var row in A(machine,"m_AnyStateTransitionConstantArray",4096))
        {
            var conditions=A(row.GetProperty("data"),"m_ConditionConstantArray",64);
            Validation.Require(conditions.Length>0&&conditions.All(c=>c.GetProperty("data").GetProperty("m_ConditionMode").GetInt32()==1&&ids.Contains(c.GetProperty("data").GetProperty("m_EventID").GetUInt32())),"Initial AnyState condition is not an inactive trigger");
        }
        var selected=A(state,"m_BlendTreeConstantIndexArray",1);var trees=A(state,"m_BlendTreeConstantArray",1);
        Validation.Require(selected.Length==1&&selected[0].GetInt32()==0&&trees.Length==1,"Unsupported selected blend tree");
        var nodes=A(trees[0].GetProperty("data"),"m_NodeArray",1);Validation.Require(nodes.Length==1,"Unsupported blended default motion");
        var leaf=nodes[0].GetProperty("data");A(leaf,"m_ChildIndices",0);False(leaf,"m_Mirror");Zero(leaf,"m_BlendType");
        A(leaf.GetProperty("m_Blend1dData").GetProperty("data"),"m_ChildThresholdArray",0);
        var two=leaf.GetProperty("m_Blend2dData").GetProperty("data");
        foreach(string field in new[]{"m_ChildPositionArray","m_ChildMagnitudeArray","m_ChildPairVectorArray","m_ChildPairAvgMagInvArray","m_ChildNeighborListArray"})A(two,field,0);
        foreach(string field in new[]{"m_BlendDirectData","m_BlendSequenceData"})
        {
            var data=leaf.GetProperty(field).GetProperty("data");A(data,"m_ChildBlendEventIDArray",0);A(data,"m_ChildPoseTimeEventIDArray",0);False(data,"m_NormalizedBlendValues","m_UsePoseTimeValues");
        }
        var sequence=leaf.GetProperty("m_BlendSequenceData").GetProperty("data");
        Zero(sequence.GetProperty("m_BodyMask"),"word0","word1","word2");
        A(sequence.GetProperty("m_SkeletonMask").GetProperty("data"),"m_Data",0);
        foreach(string field in new[]{"m_ChildBodyMask","m_ChildSkeletonMask","m_ChildSpeed","m_ChildLodThreshold","m_ChildAbilityThreshold"})A(sequence,field,0);
        foreach(string field in new[]{"m_BlendingMode","m_ChildCullingMode"})Validation.Require(sequence.GetProperty(field).GetProperty("Array").ValueKind==JsonValueKind.String&&sequence.GetProperty(field).GetProperty("Array").GetString()=="","Unsupported initial sequence packed array: "+field);
        Validation.Require(sequence.GetProperty("m_UseBlendDuration").GetBoolean(),"Unsupported initial sequence duration policy");
        Validation.Require(leaf.GetProperty("m_BlendEventID").GetUInt32()==uint.MaxValue&&leaf.GetProperty("m_BlendEventYID").GetUInt32()==uint.MaxValue&&leaf.GetProperty("m_CycleOffset").GetDouble()==0&&leaf.GetProperty("m_Duration").GetDouble()==1&&leaf.GetProperty("m_StateNameHash").GetUInt32()==state.GetProperty("m_NameID").GetUInt32(),"Unsupported initial blend leaf parameters");
        int clip=leaf.GetProperty("m_ClipID").GetInt32();Validation.Require(clip>=0&&clip<A(document,"m_AnimationClips",4096).Length,"Initial clip index outside controller");
    }
}
