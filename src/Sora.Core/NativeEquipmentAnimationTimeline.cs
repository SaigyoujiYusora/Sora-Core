using System.Numerics;
using System.Text.Json;
namespace Sora.Core;

public sealed record NativeEquipmentTimelineSegment(double Time, int State, string StateName, string ClipId,
    double ClipOffset, double Speed, bool Loop, int? FromState, double FromEnterTime, double FromClipOffset,
    double BlendDuration, string Cause);
public sealed record NativeEquipmentTimelineProof(NativeEquipmentAnimationIdentity Identity, string BodyClipId,
    double Duration, double SampleRate, double[] Times, NativeEquipmentTimelineSegment[] Segments,
    NativeWeaponAnimationEvent[] Events, string Scope = "single-layer leaf states; trigger and unconditional exit transitions; local TRS crossfade; no visibility or game montage gating",
    string ProofContract = "native-equipment-timeline-v1", NativeEquipmentUnboundSamples[]? UnboundChannels = null);
public sealed record NativeEquipmentUnboundSamples(string ClipId, uint PathHash, int Attribute, double[] Times, double[][] Values);
public sealed record NativeEquipmentTimelineLoad(ClipRecord Clip, BoneRecord[] Bones, NativeEquipmentTimelineProof Equipment);

/// <summary>One equipment state window authored by a skill trigger. Scope is component level: the source
/// actions come from a parsed SkillData timeline, and their root membership is whatever that parse proved.</summary>
public sealed record NativeEquipmentSkillWindow(int SlotId, string TriggerName, uint TriggerHash, string StateName,
    string StateClipId, bool Loop, int EnterFrame, int ExitFrame, double EnterTime, double ExitTime,
    string? EndTriggerName, string? ExpectedEndStateName,
    string Scope = "component-level skill trigger window; root membership is not asserted by this record");

public static partial class NativeEquipmentAnimationService
{
    private sealed record TimelineState(int Index, string Name, NativeControllerClipReference? Clip,
        JsonElement Data, JsonElement ClipData, double Duration, double Speed, double Offset, bool Loop)
    {
        /// <summary>An authored state without a BlendTree plays no motion; native keeps the imported rest pose.</summary>
        public bool Motionless => Clip is null;
    }
    private sealed record TimelineContext(Context Equipment, Dictionary<int,TimelineState> States, NativeEquipmentTimelineProof Plan);

    private sealed record TimelineTrigger(double Time, string Name, int SourceIndex);

    /// <summary>The authored interval, body identity and trigger set one controller schedule is evaluated over.
    /// The body path derives them from the body clip's decoded WeaponAnim events; a SkillData window supplies
    /// its own enter/end triggers, so the same scheduler evaluates both without a second state machine.</summary>
    private sealed record TimelineSchedule(double Duration, double FallbackRate, string BodyId, TimelineTrigger[] Triggers,
        NativeWeaponAnimationEvent[] Events, bool TolerateUnmatchedTriggers = false);

    private static TimelineContext Timeline(GameResources game, DatabaseDocument database, string ownerAsset,
        string resourcePath, NativeEquipmentAnimationSelection equipment, NativeAnimationSelection bodySelection)
    {
        var context=Resolve(game,database,ownerAsset,resourcePath,equipment);
        var declaration=NativeCharacterEquipment.Read(game,context.Identity.CharacterId);
        var bodyController=NativeAnimationConfig.ReadController(game,declaration.AnimationConfigPath!);
        var bodies=NativeAnimationController.Read(game,bodyController.Controller).Where(c=>c.Clip.Cab==bodySelection.Cab&&c.Clip.Object.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)==bodySelection.PathId).ToArray();
        Validation.Require(bodies.Length==1,"Body clip is absent or ambiguous in the native owner controller");
        var body=NativeObjectProjection.Json(bodies[0].Clip.Object,"m_Name","m_Events","m_MuscleClip","m_SampleRate");
        var interval=body.GetProperty("m_MuscleClip");
        double duration=(float)interval.GetProperty("m_StopTime").GetDouble();
        Validation.Require(interval.GetProperty("m_StartTime").GetDouble()==0&&duration>0,"Unsupported body clip interval");
        string bodyId=NativePrefabHierarchy.Identity(bodies[0].Clip);
        var bodyInfo=new NativeEquipmentClipInfo(bodyId,body.GetProperty("m_Name").GetString()!,A(body,"m_Events"),[]);
        var events=NativeWeaponAnimationEvents.Decode(bodyInfo,declaration).Where(e=>e.SlotId==equipment.SlotId).ToArray();
        Validation.Require(events.Where(e=>e.FunctionName=="WeaponAnim").All(e=>e.ParamType==0),"Equipment timeline contains a non-trigger WeaponAnim parameter");
        var triggers=events.Where(e=>e.FunctionName=="WeaponAnim").OrderBy(e=>e.Time).ThenBy(e=>e.SourceIndex)
            .Select(e=>new TimelineTrigger(e.Time,e.TriggerName!,e.SourceIndex)).ToArray();
        return Timeline(game,database,ownerAsset,resourcePath,equipment,context,
            new(duration,(float)body.GetProperty("m_SampleRate").GetDouble(),bodyId,triggers,events));
    }

    /// <summary>Evaluates one equipment controller over an explicit schedule. The layer, state constants, clip
    /// identities, parameters and transition rules are read exactly as the body path reads them; only the
    /// trigger source and the covered interval come from the caller.</summary>
    private static TimelineContext Timeline(GameResources game, DatabaseDocument database, string ownerAsset,
        string resourcePath, NativeEquipmentAnimationSelection equipment, Context context, TimelineSchedule schedule)
    {
        double duration=schedule.Duration;
        Validation.Require(duration>0&&schedule.FallbackRate>0,"Unsupported equipment timeline interval");
        string bodyId=schedule.BodyId;
        var events=schedule.Events;
        var controller=context.Controller;
        while(controller.Object.ClassId==221)
            controller=game.Resolve(controller.Cab,NativeObjectProjection.Json(controller.Object,"m_Controller").GetProperty("m_Controller"))!;
        var data=NativeObjectProjection.Json(controller.Object,"m_Controller","m_TOSData","m_AnimationClips");
        var constant=data.GetProperty("m_Controller");
        var layers=A(constant,"m_LayerArray");var machines=A(constant,"m_StateMachineArray");
        Validation.Require(layers.Length==1&&machines.Length==1,"Equipment timeline currently supports one native controller layer");
        var machine=machines[0].GetProperty("data");
        var names=A(data,"m_TOSData").Select(v=>v.GetString()!).Distinct().GroupBy(NativeEquipmentDefaultPoseReader.PathHash).ToDictionary(g=>g.Key,g=>g.ToArray());
        string Name(uint hash)=>names.TryGetValue(hash,out var found)&&found.Length==1?found[0]:throw new InvalidDataException("Ambiguous controller state/trigger name");
        var effective=context.Clips.ToDictionary(c=>c.OriginalSourceId);
        var clipPointers=A(data,"m_AnimationClips");
        var stateRows=A(machine,"m_StateConstantArray");
        var states=new Dictionary<int,TimelineState>();
        TimelineState State(int index) {
            if(states.TryGetValue(index,out var cached))return cached;
            var state=stateRows[index].GetProperty("data");var trees=A(state,"m_BlendTreeConstantArray");
            double speed=state.GetProperty("m_Speed").GetDouble();
            var stateName=Name(state.GetProperty("m_NameID").GetUInt32());
            Validation.Require(speed>0&&!state.GetProperty("m_Mirror").GetBoolean()
                &&new[]{"m_SpeedParamID","m_TimeParamID","m_CycleOffsetParamID","m_MirrorParamID"}.All(k=>state.GetProperty(k).GetUInt32()==0),"Dynamic speed/time or mirrored equipment states are unsupported");
            // An authored state with no BlendTree holds the imported rest pose for its whole duration.
            if(trees.Length==0)return states[index]=new TimelineState(index,stateName,null,state,default,0,speed,0,false);
            Validation.Require(trees.Length==1,"Equipment timeline has a multi-tree state");
            var nodes=A(trees[0].GetProperty("data"),"m_NodeArray");
            Validation.Require(nodes.Length==1&&A(nodes[0].GetProperty("data"),"m_ChildIndices").Length==0,"Equipment timeline BlendTree is unsupported");
            var node=nodes[0].GetProperty("data");
            var original=game.Resolve(controller.Cab,clipPointers[node.GetProperty("m_ClipID").GetInt32()])!;
            var clipId=NativePrefabHierarchy.Identity(original);
            Validation.Require(effective.TryGetValue(clipId,out var clip),"Equipment state clip is absent from the resolved controller chain: "+clipId);
            var clipData=J(clip!.Clip);
            double length=(float)clipData.GetProperty("m_MuscleClip").GetProperty("m_StopTime").GetDouble();
            Validation.Require(!node.GetProperty("m_Mirror").GetBoolean(),"Dynamic speed/time or mirrored equipment states are unsupported");
            return states[index]=new TimelineState(index,stateName,clip,state,clipData,length,speed,
                (state.GetProperty("m_CycleOffset").GetDouble()+node.GetProperty("m_CycleOffset").GetDouble())*length,state.GetProperty("m_Loop").GetBoolean());
        }
        int current=machine.GetProperty("m_DefaultState").GetInt32();
        double entered=0,offset=State(current).Offset,blendUntil=0;
        string ClipIdOf(int index)=>states[index].Motionless?"":NativePrefabHierarchy.Identity(states[index].Clip!.Clip);
        var segments=new List<NativeEquipmentTimelineSegment>{new(0,current,states[current].Name,ClipIdOf(current),offset,states[current].Speed,states[current].Loop,null,0,0,0,"native-default-state")};
        var triggers=schedule.Triggers;int eventIndex=0;
        var parameters=A(constant.GetProperty("m_Values").GetProperty("data"),"m_ValueArray");
        bool Trigger(JsonElement transition,string trigger) {
            var conditions=A(transition,"m_ConditionConstantArray");
            if(conditions.Length!=1)return false;
            var c=conditions[0].GetProperty("data");uint hash=c.GetProperty("m_EventID").GetUInt32();
            return c.GetProperty("m_ConditionMode").GetInt32()==1&&Name(hash)==trigger&&parameters.Any(p=>p.GetProperty("m_ID").GetUInt32()==hash&&p.GetProperty("m_Type").GetInt32()==9);
        }
        while(segments.Count<4096) {
            var state=states[current];
            var exits=A(state.Data,"m_TransitionConstantArray").Select(r=>r.GetProperty("data"))
                .Where(t=>A(t,"m_ConditionConstantArray").Length==0&&t.GetProperty("m_HasExitTime").GetBoolean()).ToArray();
            Validation.Require(exits.Length<=1,"Multiple unconditional equipment exit transitions are unsupported");
            double exit=exits.Length==0?double.PositiveInfinity:entered+(exits[0].GetProperty("m_ExitTime").GetDouble()*state.Duration-offset)/state.Speed;
            double triggerTime=eventIndex<triggers.Length?triggers[eventIndex].Time:double.PositiveInfinity;
            double time=Math.Min(exit,triggerTime);
            if(time>duration||double.IsPositiveInfinity(time))break;
            Validation.Require(time>=entered&&time>=blendUntil,"Equipment transition interruption requires a state evaluator");
            JsonElement transition;string cause;
            if(triggerTime<=exit) {
                var e=triggers[eventIndex++];
                var matches=A(machine,"m_AnyStateTransitionConstantArray").Concat(A(state.Data,"m_TransitionConstantArray"))
                    .Select(r=>r.GetProperty("data")).Where(t=>Trigger(t,e.Name)).ToArray();
                // A fired trigger that names no transition from the current state is inert in the native
                // machine. The body path stays strict; a SkillData window tolerates the authored end trigger
                // that was already consumed by the entered state's own exit transition.
                if(matches.Length!=1) {
                    Validation.Require(schedule.TolerateUnmatchedTriggers,"WeaponAnim trigger has no unique native transition");
                    continue;
                }
                transition=matches[0];cause="native-trigger:"+e.SourceIndex;
                Validation.Require(!transition.GetProperty("m_HasExitTime").GetBoolean(),"Trigger exit-time gating is unsupported");
            } else {transition=exits[0];cause="native-exit-time";}
            Validation.Require(A(transition,"m_TransitionBlockParamConstantArray").Length==0&&transition.GetProperty("m_BlendStyle").GetInt32()==0,"Equipment transition style or block conditions are unsupported");
            int next=transition.GetProperty("m_DestinationState").GetInt32();
            Validation.Require(next>=0&&next<stateRows.Length,"Equipment transition destination is not a direct state");
            State(next);
            // A motionless state is only established as the authored cold-start entry state. A transition
            // into one would need the state-entry default-value policy, which is not evaluated here.
            Validation.Require(!states[next].Motionless,"Equipment transition into a motionless state is unsupported");
            double blend=transition.GetProperty("m_TransitionDuration").GetDouble();
            if(!transition.GetProperty("m_HasFixedDuration").GetBoolean())blend*=state.Duration/state.Speed;
            double nextOffset=transition.GetProperty("m_TransitionOffset").GetDouble()*states[next].Duration+states[next].Offset;
            segments.Add(new(time,next,states[next].Name,ClipIdOf(next),nextOffset,states[next].Speed,states[next].Loop,
                current,entered,offset,blend,cause));
            current=next;entered=time;offset=nextOffset;blendUntil=time+blend;
        }
        Validation.Require(segments.Count<4096,"Equipment transition sequence does not terminate within the covered interval");
        // The emitted grid is the fastest authored motion clip; a controller whose states all hold rest keeps
        // the caller's fallback rate so the plan still describes the whole covered interval.
        double rate=states.Values.Any(s=>!s.Motionless)?states.Values.Where(s=>!s.Motionless).Max(s=>s.ClipData.GetProperty("m_SampleRate").GetDouble())
            :schedule.FallbackRate;
        Validation.Require(rate is >=1 and <=240&&duration*rate<=100000,"Equipment timeline sample budget exceeded");
        var times=Enumerable.Range(0,(int)Math.Ceiling(duration*rate)+1).Select(i=>Math.Min(i/rate,duration))
            .Concat(segments.SelectMany(s=>new[]{s.Time,Math.Min(duration,s.Time+s.BlendDuration)})).Distinct().Order().ToArray();
        return new(context,states,new(context.Identity,bodyId,duration,rate,times,segments.ToArray(),events));
    }

    public static NativeEquipmentTimelineProof Plan(GameResources game,DatabaseDocument database,string ownerAsset,
        string resourcePath,NativeEquipmentAnimationSelection equipment,NativeAnimationSelection bodySelection)
        =>Timeline(game,database,ownerAsset,resourcePath,equipment,bodySelection).Plan;

    public static NativeEquipmentTimelineLoad Bake(GameResources game,DatabaseDocument database,string ownerAsset,
        string resourcePath,NativeEquipmentAnimationSelection equipment,NativeAnimationSelection bodySelection)
        =>BakeWork(game,Timeline(game,database,ownerAsset,resourcePath,equipment,bodySelection),equipment,resourcePath);

    /// <summary>Bakes one SkillData-authored equipment window over the whole covered body interval. The authored
    /// enter trigger enters the window state at its own enter time; the native exit rules (the state's
    /// unconditional exit-time transition or the authored end trigger, whichever fires first) then return the
    /// controller to its default state, whose own motion keeps playing to the end of the interval. This reuses
    /// the controller readers, state cache and samplers of the body equipment timeline.</summary>
    public static NativeEquipmentTimelineLoad BakeWindow(GameResources game,DatabaseDocument database,string ownerAsset,
        string resourcePath,NativeEquipmentAnimationSelection equipment,NativeEquipmentSkillWindow window,
        double duration,double fallbackRate,string bodyClipId)
    {
        Validation.Require(window is not null,"Skill window is missing");
        Validation.Require(!string.IsNullOrWhiteSpace(window!.TriggerName),"Skill window has no authored enter trigger");
        Validation.Require(window!.EndTriggerName is not null,"Skill window has no authored end trigger");
        var context=Resolve(game,database,ownerAsset,resourcePath,equipment);
        var triggers=new List<TimelineTrigger>{new(window.EnterTime,window.TriggerName,0)};
        if(window.EndTriggerName is not null)triggers.Add(new(window.ExitTime,window.EndTriggerName,1));
        var work=Timeline(game,database,ownerAsset,resourcePath,equipment,context,
            new(duration,fallbackRate,bodyClipId,triggers.OrderBy(t=>t.Time).ThenBy(t=>t.SourceIndex).ToArray(),[],
                TolerateUnmatchedTriggers:true));
        return BakeWork(game,work,equipment,resourcePath);
    }

    private static NativeEquipmentTimelineLoad BakeWork(GameResources game,TimelineContext work,NativeEquipmentAnimationSelection equipment,string resourcePath)
    {
        var context=work.Equipment;var plan=work.Plan;
        var scene=NativeItemImport.Import(game,resourcePath).Assets.Single().Scene!;
        var required=plan.Segments.SelectMany(s=>s.FromState.HasValue?new[]{s.State,s.FromState.Value}:new[]{s.State}).Distinct().ToArray();
        // Only states with authored motion own a decoded sample source; a rest state contributes the
        // imported rest pose itself and is never given a fabricated clip.
        var motion=required.Where(i=>!work.States[i].Motionless).ToArray();
        var bindings=motion.ToDictionary(i=>i,i=>Bindings(work.States[i].ClipData,context.Nodes,scene,context.Identity.AnimatorSourcePath,preserveUnbound:true));
        var samplers=motion.ToDictionary(i=>i,i=>NativeEquipmentClipSampler.Create(work.States[i].ClipData,bindings[i]));
        var unbound = new List<NativeEquipmentUnboundSamples>();
        foreach (int index in motion.Where(i => bindings[i].Any(b => b.Bone < 0)))
        {
            double rate = work.States[index].ClipData.GetProperty("m_SampleRate").GetDouble();
            double duration = samplers[index].Duration;
            Validation.Require(duration*rate <= 100000, "Unbound equipment sample budget exceeded");
            var times = Enumerable.Range(0,(int)Math.Ceiling(duration*rate)+1).Select(i=>Math.Min(i/rate,duration)).ToArray();
            Validation.Require((long)times.Length * bindings[index].Sum(b => b.Attribute == 2 ? 4 : 3) <= 10_000_000,
                "Unbound equipment scalar sample budget exceeded");
            var samples = times.Select(samplers[index].Sample).ToArray();
            int offset = 0;
            foreach (var binding in bindings[index])
            {
                int size = binding.Attribute == 2 ? 4 : 3;
                if (binding.Bone < 0)
                    unbound.Add(new(NativePrefabHierarchy.Identity(work.States[index].Clip!.Clip),binding.PathHash,
                        binding.Attribute,times,samples.Select(v=>v[offset..(offset+size)]).ToArray()));
                offset += size;
            }
        }
        if (unbound.Count > 0) plan = plan with { UnboundChannels = unbound.ToArray() };
        (Vector3 Position,Quaternion Rotation,Vector3 Scale)[] Pose(int index,double entered,double offset,double time) {
            if(work.States[index].Motionless)return Enumerable.Repeat((Vector3.Zero,Quaternion.Identity,Vector3.One),scene.Bones.Length).ToArray();
            var state=work.States[index];double local=offset+(time-entered)*state.Speed;
            local=state.Loop?(local%state.Duration+state.Duration)%state.Duration:Math.Clamp(local,0,state.Duration);
            // The decoded sample source owns the exact interval it can serve; a float32 authored state end
            // may sit a few float32 ULPs beyond it. Never ask a source past its own decoded end.
            local=Math.Min(local,samplers[index].Duration);
            var values=samplers[index].Sample(local);int at=0;
            var channels=bindings[index].Select(b=>{int size=b.Attribute==2?4:3;var c=new NativeEquipmentDefaultPoseReader.Channel(b.PathHash,b.Attribute,values[at..(at+size)]);at+=size;return (b,c);})
                .Where(pair=>pair.b.Bone>=0).Select(pair=>pair.c).ToArray();
            return NativeEquipmentDefaultPoseReader.ConvertPose(context.Nodes,scene,context.Identity.AnimatorSourcePath,channels).Select(b=>{
                var a=b.BasisMatrix;var matrix=new Matrix4x4((float)a[0],(float)a[4],(float)a[8],(float)a[12],(float)a[1],(float)a[5],(float)a[9],(float)a[13],(float)a[2],(float)a[6],(float)a[10],(float)a[14],(float)a[3],(float)a[7],(float)a[11],(float)a[15]);
                Validation.Require(Matrix4x4.Decompose(matrix,out var scale,out var rotation,out var position),"Equipment blend basis is not TRS");
                return(position,Quaternion.Normalize(rotation),scale);
            }).ToArray();
        }
        var tracks=Enumerable.Range(0,scene.Bones.Length).SelectMany(i=>new[]{"location","rotation","scale"}.Select(c=>(i,c))).ToDictionary(k=>k,_=>new List<KeyRecord>());
        int segmentIndex=0;var previousRotations=new Dictionary<int,Quaternion>();
        for(int frame=0;frame<plan.Times.Length;frame++) {
            double time=plan.Times[frame];
            while(segmentIndex+1<plan.Segments.Length&&plan.Segments[segmentIndex+1].Time<=time)segmentIndex++;
            var segment=plan.Segments[segmentIndex];var target=Pose(segment.State,segment.Time,segment.ClipOffset,time);
            double alpha=segment.BlendDuration>0?Math.Clamp((time-segment.Time)/segment.BlendDuration,0,1):1;
            var from=alpha<1&&segment.FromState.HasValue?Pose(segment.FromState.Value,segment.FromEnterTime,segment.FromClipOffset,time):target;
            for(int i=0;i<target.Length;i++) {
                var p=Vector3.Lerp(from[i].Position,target[i].Position,(float)alpha);var q=Quaternion.Slerp(from[i].Rotation,target[i].Rotation,(float)alpha);var s=Vector3.Lerp(from[i].Scale,target[i].Scale,(float)alpha);
                if(previousRotations.TryGetValue(i,out var previous)&&Quaternion.Dot(previous,q)<0)q=new(-q.X,-q.Y,-q.Z,-q.W);
                previousRotations[i]=q;
                tracks[(i,"location")].Add(new(time,[p.X,p.Y,p.Z]));tracks[(i,"rotation")].Add(new(time,[q.X,q.Y,q.Z,q.W]));tracks[(i,"scale")].Add(new(time,[s.X,s.Y,s.Z]));
            }
            OperationProgress.Report("bake-equipment-timeline",frame+1,plan.Times.Length,equipment.SlotId);
        }
        var clip=new ClipRecord("Equipment timeline "+equipment.SlotId,plan.Duration,plan.SampleRate,tracks.Select(p=>new TrackRecord(p.Key.i,p.Key.c,p.Value.ToArray())).ToArray(),
            new(null,[],[plan.Scope]));
        Validation.Scene(scene with {Clips=[clip]});
        return new(clip,scene.Bones,plan);
    }

    /// <summary>Resolves parsed skill trigger windows against the real equipment controller: which native state
    /// each trigger enters, that state's clip identity, and the state the authored end trigger returns to. This
    /// is the product entry a SkillData caller uses; it shares Resolve and the controller readers, and it never
    /// scans bytes or guesses names.</summary>
    public static NativeEquipmentSkillWindow[] SkillWindows(GameResources game, DatabaseDocument database, string ownerAsset,
        string resourcePath, NativeEquipmentAnimationSelection equipment, NativeSkillSlotTriggerWindow[] triggers)
    {
        Validation.Require(triggers is not null, "Missing skill trigger windows");
        var context = Resolve(game, database, ownerAsset, resourcePath, equipment);
        var controller = context.Controller;
        while (controller.Object.ClassId == 221)
            controller = game.Resolve(controller.Cab, NativeObjectProjection.Json(controller.Object, "m_Controller").GetProperty("m_Controller"))
                ?? throw new InvalidDataException("Equipment controller base missing");
        var raw = NativeObjectProjection.Json(controller.Object, "m_TOSData", "m_Controller");
        var names = A(raw, "m_TOSData").Select(value => value.GetString()!).Distinct(StringComparer.Ordinal)
            .GroupBy(NativeEquipmentDefaultPoseReader.PathHash).Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.First());
        var stateRows = A(A(raw.GetProperty("m_Controller"), "m_StateMachineArray")[0].GetProperty("data"), "m_StateConstantArray");
        string StateName(int index)
        {
            Validation.Require(index >= 0 && index < stateRows.Length, "Equipment transition destination is out of range");
            uint hash = stateRows[index].GetProperty("data").GetProperty("m_NameID").GetUInt32();
            Validation.Require(names.TryGetValue(hash, out string? name), "Unresolved equipment state name");
            return name!;
        }
        var transitions = NativeAnimationControllerStates.ReadTransitions(game, controller);
        var clips = NativeAnimationControllerStates.Read(game, controller);
        var result = new List<NativeEquipmentSkillWindow>();
        foreach (var trigger in triggers!)
        {
            var entered = transitions.Where(t => trigger.TriggerName is not null && t.TriggerName == trigger.TriggerName).ToArray();
            Validation.Require(entered.Length > 0, "No native equipment transition fires trigger " + trigger.TriggerName);
            string state = StateName(entered[0].ToStateIndex);
            var clip = clips.FirstOrDefault(c => c.StateName == state) ?? throw new InvalidDataException("Equipment state has no leaf clip: " + state);
            string? endState = null;
            if (trigger.EndTriggerName is not null)
            {
                var back = transitions.FirstOrDefault(t => t.TriggerName == trigger.EndTriggerName);
                if (back is not null) endState = StateName(back.ToStateIndex);
            }
            result.Add(new(trigger.SlotId, trigger.TriggerName!, trigger.ParamBits, state, clip.ClipId, clip.Loop,
                trigger.StartFrame, trigger.EndFrame, trigger.StartTime, trigger.EndTime, trigger.EndTriggerName, endState));
        }
        return result.ToArray();
    }

    /// <summary>Authored Animator parameter identities of one equipment controller, read from the resolved
    /// controller chain's own name table. A skill trigger is named from the set its own slot authors, never from
    /// another slot's parameter table.</summary>
    public static string[] ParameterNames(GameResources game, DatabaseDocument database, string ownerAsset,
        string resourcePath, NativeEquipmentAnimationSelection equipment)
    {
        var context = Resolve(game, database, ownerAsset, resourcePath, equipment);
        var controller = context.Controller;
        while (controller.Object.ClassId == 221)
            controller = game.Resolve(controller.Cab, NativeObjectProjection.Json(controller.Object, "m_Controller").GetProperty("m_Controller"))
                ?? throw new InvalidDataException("Equipment controller base missing");
        var raw = NativeObjectProjection.Json(controller.Object, "m_TOSData", "m_Controller");
        return A(raw, "m_TOSData").Select(value => value.GetString()!).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToArray();
    }
}
