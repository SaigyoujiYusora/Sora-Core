using System.Text.Json.Nodes;

// Authored synthetic values and structure-only format fixtures, not redistributed game dumps.
public static class PortablePoseFixtures
{
    static JsonObject A(params JsonNode?[] values)=>new(){["Array"]=new JsonArray(values)};
    static JsonObject D(JsonNode value)=>new(){["data"]=value};
    public static JsonNode Controller()
    {
        var sequence=new JsonObject();
        foreach(string field in new[]{"m_ChildBlendEventIDArray","m_ChildPoseTimeEventIDArray","m_ChildBodyMask","m_ChildSkeletonMask","m_ChildSpeed","m_ChildLodThreshold","m_ChildAbilityThreshold"})sequence[field]=A();
        sequence["m_NormalizedBlendValues"]=false;sequence["m_UsePoseTimeValues"]=false;
        sequence["m_BodyMask"]=new JsonObject{["word0"]=0,["word1"]=0,["word2"]=0};
        sequence["m_SkeletonMask"]=D(new JsonObject{["m_Data"]=A()});
        sequence["m_BlendingMode"]=new JsonObject{["Array"]=""};sequence["m_ChildCullingMode"]=new JsonObject{["Array"]=""};sequence["m_UseBlendDuration"]=true;
        var two=new JsonObject();foreach(string field in new[]{"m_ChildPositionArray","m_ChildMagnitudeArray","m_ChildPairVectorArray","m_ChildPairAvgMagInvArray","m_ChildNeighborListArray"})two[field]=A();
        var leaf=new JsonObject{["m_BlendType"]=0,["m_BlendEventID"]=uint.MaxValue,["m_BlendEventYID"]=uint.MaxValue,["m_ChildIndices"]=A(),["m_Mirror"]=false,["m_CycleOffset"]=0,["m_Duration"]=1,["m_StateNameHash"]=1,["m_ClipID"]=0,
            ["m_Blend1dData"]=D(new JsonObject{["m_ChildThresholdArray"]=A()}),["m_Blend2dData"]=D(two),["m_BlendSequenceData"]=D(sequence),
            ["m_BlendDirectData"]=D(new JsonObject{["m_ChildBlendEventIDArray"]=A(),["m_ChildPoseTimeEventIDArray"]=A(),["m_NormalizedBlendValues"]=false,["m_UsePoseTimeValues"]=false})};
        var state=new JsonObject{["m_NameID"]=1,["m_Mirror"]=false,["m_IKOnFeet"]=false,["m_WriteDefaultValues"]=true,["m_Speed"]=1,["m_CycleOffset"]=0,["m_StateParameterConstantArray"]=A(),["m_TransitionConstantArray"]=A(),["m_BlendTreeConstantIndexArray"]=A(JsonValue.Create(0)),["m_BlendTreeConstantArray"]=A(D(new JsonObject{["m_NodeArray"]=A(D(leaf))}))};
        foreach(string field in new[]{"m_SpeedParamID","m_MirrorParamID","m_CycleOffsetParamID","m_TimeParamID","m_SyncGroupID","m_SyncGroupRole"})state[field]=0;
        var layer=new JsonObject{["m_StateMachineIndex"]=0,["m_StateMachineSynchronizedLayerIndex"]=0,["(int&)m_LayerBlendingMode"]=0,["m_Binding"]=1,["m_curveMaskIndex"]=uint.MaxValue,["m_DefaultWeight"]=0,["(int&)m_layerOptMode"]=65470464,["m_LODThreshold"]=999,["m_AbilityThreshold"]=999,["m_BodyMask"]=new JsonObject{["word0"]=uint.MaxValue,["word1"]=uint.MaxValue,["word2"]=33554431},["m_SkeletonMask"]=D(new JsonObject{["m_Data"]=A()})};
        foreach(string field in new[]{"m_IKPass","m_SyncedLayerAffectsTiming","m_MeshSpace","m_UseThreePoseBlender","m_PostProcessLayer","m_UseIdentifyValues","m_ConvertSimpleBlend","m_DisableRootMotionPass"})layer[field]=false;
        var machine=new JsonObject{["m_DefaultState"]=0,["m_ImmediateTransition"]=false,["m_InterruptDynamicTransitions"]=false,["m_SynchronizedLayerCount"]=1,["m_StateConstantArray"]=A(D(state)),["m_AnyStateTransitionConstantArray"]=A(),["m_SelectorStateConstantArray"]=A(D(new JsonObject{["m_IsEntry"]=true,["m_FullPathID"]=1,["m_TransitionConstantArray"]=A(D(new JsonObject{["m_Destination"]=0,["m_ConditionConstantArray"]=A(),["m_TransitionBlockParamConstantArray"]=A()}))}))};
        var defaults=new JsonObject();foreach(string field in new[]{"m_FloatValues","m_IntValues","m_PositionValues","m_QuaternionValues","m_ScaleValues","m_BoolValues"})defaults[field]=A();
        return new JsonObject{["m_Controller"]=new JsonObject{["m_LayerArray"]=A(D(layer)),["m_StateMachineArray"]=A(D(machine)),["m_DefaultValues"]=D(defaults),["m_Values"]=D(new JsonObject{["m_ValueArray"]=A()})},["m_AnimationClips"]=A(new JsonObject{["m_FileID"]=0,["m_PathID"]=1})};
    }
    public static JsonNode Clip()
    {
        uint F(float value)=>BitConverter.SingleToUInt32Bits(value);
        var words=new List<uint>();
        void Frame(float time,bool terminal){words.Add(F(time));words.Add(3);for(uint i=0;i<3;i++){words.Add(i);words.Add(F(terminal?0:1f/1024));words.Add(0);words.Add(0);words.Add(F(.25f+i+(terminal? .0625f:0)));}}
        // Native sentinel has zero coefficients; its value equals the zero-time key.
        Frame(-float.MaxValue,false);for(int i=0;i<3;i++)words[3+i*5]=0;
        Frame(0,false);Frame(4,true);words.Add(F(float.PositiveInfinity));words.Add(0);
        var packed=new byte[241*24*2];for(int i=0;i<packed.Length;i+=2){packed[i]=0x30;packed[i+1]=0x75;}
        var acl=new JsonObject();foreach(string field in new[]{"OutputTrackCount","FloatCurveCount","RootTrackCount"})acl[field]=0;foreach(string field in new[]{"TransformBufferData","FloatBufferData","RootMotionBufferData"})acl[field]=new JsonObject{["Array"]=""};
        var dense=new JsonObject{["m_CurveCount"]=24,["m_FrameCount"]=241,["m_SampleArray"]=A(),["m_ACLArray"]=new JsonObject{["Array"]=Convert.ToBase64String(packed)},["m_SampleRate"]=60,["m_PositionFactor"]=1,["m_BeginTime"]=0,["m_ACLType"]=16,["m_nPositionCurves"]=24,["m_nRotationCurves"]=0,["m_nEulerCurves"]=0,["m_nScaleCurves"]=0};
        var bindings=new List<JsonNode?>();for(int i=0;i<25;i++)bindings.Add(new JsonObject{["path"]=i,["attribute"]=i<9?1:2,["typeID"]=4,["customType"]=0,["isPPtrCurve"]=0});
        return new JsonObject{["m_AclCompressedBuffer"]=acl,["m_Legacy"]=false,["m_Compressed"]=false,["m_MuscleClip"]=new JsonObject{["m_StartTime"]=0,["m_StopTime"]=4,["m_Clip"]=D(new JsonObject{["m_StreamedClip"]=new JsonObject{["curveCount"]=3,["data"]=A(words.Select(w=>(JsonNode?)JsonValue.Create(w)).ToArray())},["m_DenseClip"]=dense,["m_ConstantClip"]=new JsonObject{["data"]=A(Enumerable.Range(0,64).Select(i=>(JsonNode?)JsonValue.Create((double)i)).ToArray())}})},["m_ClipBindingConstant"]=new JsonObject{["genericBindings"]=A(bindings.ToArray())}};
    }
}
