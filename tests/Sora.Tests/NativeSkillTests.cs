using System.Buffers.Binary;
using System.Text;
using Sora.Core;

static class NativeSkillTests
{
    public static void Run(Action<string, Action> test, Action<Action> reject)
    {
        test("native SkillData root chain decodes timeline frames and weapon actions", () => {
            var timeline = NativeSkillAnimation.Read(AttackFixture(), "fixture");
            if(timeline.PassiveEventActionCount!=0||timeline.TimelineActionCount!=2||timeline.Elements.Length!=2||timeline.UnparsedOffset is not null)throw new Exception("SkillData prefix was not consumed continuously");
            var play=timeline.Elements[0];
            if(play.StartFrame!=0||play.EndFrame!=224||play.MontageName!="Attack01"||play.Actions.Length!=1||play.Actions[0].UnionTag!=277||play.Actions[0].AnimationDuration is null||Math.Abs(play.Actions[0].AnimationDuration!.Value-7.4666667f)>1e-6)throw new Exception("PlayAnimationAction was not decoded");
            var weapon=timeline.Elements[1];
            if(weapon.StartFrame!=3||weapon.EndFrame!=6||weapon.Actions.Length!=1)throw new Exception("Weapon timeline element was not decoded");
            var action=weapon.Actions[0];
            if(action.UnionTag!=54||action.ServerActionIndex!=70||action.WeaponId!=10||action.ParamAction!.ParamBits!=1844968592u||action.EndAction!.ParamBits!=2535617573u||action.InterruptionAction!.ParamBits!=0u||action.ActionOnEnd!=true||action.ActionOnInterrupt!=false||action.OverrideInterruptionTime!=false)throw new Exception("CharWeaponAnimationActionData fields changed");
        });
        test("native SkillData never skips an unverified union body", () => {
            var bytes=AttackFixture(99);
            var timeline=NativeSkillAnimation.Read(bytes,"fixture");
            if(timeline.Elements.Length!=1||timeline.UnparsedOffset!=117||!timeline.UnparsedReason!.Contains("99",StringComparison.Ordinal))throw new Exception("Unknown union tag was not reported at its exact offset");
        });
        test("floating skill control records preserve the following weapon action boundary", () => {
            var extra = new List<byte>();
            void BaseRecord(int tag, byte header) {
                if(tag < 250) extra.Add((byte)tag); else { extra.Add(250); extra.AddRange(BitConverter.GetBytes((ushort)tag)); }
                extra.Add(header); extra.Add(1); I32(extra,0); I32(extra,0); I32(extra,7);
            }
            BaseRecord(217,6); extra.Add(3); I32(extra,0); extra.Add(0); extra.Add(0); I32(extra,45);
            BaseRecord(69,4);
            BaseRecord(317,5); Str(extra,"floating");
            BaseRecord(189,6); extra.Add(3); I32(extra,0); extra.Add(0); extra.Add(0); extra.Add(255);
            var bytes=AttackFixture(extraActions: extra.ToArray(), extraCount:4);
            var timeline=NativeSkillAnimation.Read(bytes,"floating fixture");
            if(!timeline.Complete||timeline.Elements[1].Actions.Length!=5||timeline.ParsedBytes!=bytes.Length)
                throw new Exception("Floating action records drifted the timeline boundary");
            var weapon=timeline.Elements[1].Actions[^1];
            if(weapon.UnionTag!=54||weapon.WeaponId!=10||weapon.ParamAction!.ParamBits!=1844968592u)
                throw new Exception("Following weapon identity was not preserved");
            reject(()=>NativeSkillAnimation.Read(bytes[..^1],"truncated floating fixture"));
        });
        test("native skill frames convert to runtime seconds and project onto equipment slots", () => {
            var timeline=NativeSkillAnimation.Read(AttackFixture(),"fixture");
            if(timeline.Elements.Any(element=>Math.Abs(element.StartTime-element.StartFrame/30.0)>1e-12||Math.Abs(element.EndTime-element.EndFrame/30.0)>1e-12))throw new Exception("Native frame to runtime second conversion changed");
            var triggers=NativeSkillAnimation.EquipmentTriggers(timeline);
            if(triggers.Length!=1)throw new Exception("Equipment trigger projection changed");
            var trigger=triggers[0];
            if(trigger.SlotId!=10||trigger.ElementIndex!=1||trigger.StartFrame!=3||trigger.EndFrame!=6||Math.Abs(trigger.StartTime-0.1)>1e-9||Math.Abs(trigger.EndTime-0.2)>1e-9
                ||trigger.ParamBits!=1844968592u||trigger.ParamType!=9||trigger.ActionOnEnd!=true||trigger.EndParamBits!=2535617573u||trigger.InterruptionParamBits!=0u||trigger.ActionOnInterrupt!=false)
                throw new Exception("Equipment slot trigger fields changed");
        });
        test("native skill trigger windows resolve against authored controller parameter names", () => {
            var timeline=NativeSkillAnimation.Read(AttackFixture(),"fixture");
            var identities=NativeSkillAnimation.ParameterIdentities(["tAtk1","tAtk4","tPower","tUlt","tIdle"]);
            if(identities[NativeEquipmentDefaultPoseReader.PathHash("tAtk1")]!="tAtk1")throw new Exception("Parameter identity mapping changed");
            var windows=NativeSkillAnimation.ResolveTriggerWindows(NativeSkillAnimation.EquipmentTriggers(timeline),identities);
            if(windows.Length!=1)throw new Exception("Trigger window projection changed");
            var window=windows[0];
            if(window.SlotId!=10||window.StartFrame!=3||window.EndFrame!=6||window.TriggerName!="tAtk1"||window.EndTriggerName!="tIdle"||window.EndParamBits!=2535617573u||window.InterruptionTriggerName is not null||window.InterruptionParamBits!=0u||window.ActionOnInterrupt!=false)throw new Exception("Trigger window resolution changed");
        });
        test("native skill scheduler keeps the proven start, end and force-sync boundaries", () => {
            if(NativeSkillScheduler.Active(0.9,1.0,2.0)||NativeSkillScheduler.Active(1.0-2e-5,1.0,2.0)||!NativeSkillScheduler.Active(1.0-1e-5,1.0,2.0))throw new Exception("Start boundary changed");
            // Ended when currentTime+1e-5 >= endTime (the end test runs after the tick), so 2.0-2e-5 is still
            // inside the window and 2.0-1e-5 already ended.
            if(!NativeSkillScheduler.Active(1.5,1.0,2.0)||!NativeSkillScheduler.Active(2.0-2e-5,1.0,2.0)||NativeSkillScheduler.Active(2.0-1e-5,1.0,2.0)||NativeSkillScheduler.Active(2.0,1.0,2.0))throw new Exception("End boundary changed");
            // The force-sync early branch only pre-syncs the montage and jumps to the element loop tail, so no
            // caller can make an action active early through it.
        });
        test("incomplete skill roots are reported as not evaluated rather than empty", () => {
            var complete=NativeSkillAnimation.Read(AttackFixture(),"fixture");
            if(!complete.Complete||complete.WindowsScope!="authored-windows")throw new Exception("Complete timeline scope changed");
            var partial=NativeSkillAnimation.Read(AttackFixture(99),"fixture");
            if(partial.Complete||partial.WindowsScope!="not-evaluated-incomplete-root")throw new Exception("Incomplete timeline scope changed");
        });
    }

    private static void I32(List<byte> bytes,int value)=>bytes.AddRange(BitConverter.GetBytes(value));
    private static void F32(List<byte> bytes,float value)=>bytes.AddRange(BitConverter.GetBytes(value));
    private static void Str(List<byte> bytes,string value){I32(bytes,value.Length);bytes.AddRange(Encoding.UTF8.GetBytes(value));}
    private static void Param(List<byte> bytes,uint bits){bytes.Add(5);bytes.AddRange(BitConverter.GetBytes(bits));bytes.Add(1);F32(bytes,0);I32(bytes,0);I32(bytes,(int)bits==0?4:9);}
    private static void Force(List<byte> bytes){bytes.Add(4);bytes.Add(0);Str(bytes,"");F32(bytes,0);I32(bytes,0);}

    private static byte[] AttackFixture(int weaponTag = 54, byte[]? extraActions = null, int extraCount = 0)
    {
        var bytes=new List<byte>{48,2};
        I32(bytes,0);
        I32(bytes,2);
        bytes.Add(4);I32(bytes,224);
        bytes.Add(3);I32(bytes,1);
        bytes.Add(250);bytes.AddRange(BitConverter.GetBytes((ushort)277));
        bytes.Add(16);bytes.Add(1);I32(bytes,0);I32(bytes,0);I32(bytes,1);
        Str(bytes,"Attack01");F32(bytes,.1f);F32(bytes,.1f);I32(bytes,0);F32(bytes,7.4666667f);bytes.Add(0);bytes.Add(0);
        bytes.Add(3);I32(bytes,0);bytes.Add(0);bytes.Add(0);
        F32(bytes,1f);F32(bytes,0);Str(bytes,"");bytes.Add(0);
        bytes.Add(0);bytes.Add(0);
        I32(bytes,0);
        Force(bytes);
        bytes.Add(4);I32(bytes,6);
        bytes.Add(3);I32(bytes,1+extraCount);
        if(extraActions is not null)bytes.AddRange(extraActions);
        bytes.Add((byte)weaponTag);
        bytes.Add(12);bytes.Add(1);I32(bytes,0);I32(bytes,0);I32(bytes,70);
        bytes.Add(1);bytes.Add(0);
        Param(bytes,2535617573u);
        Param(bytes,0u);
        F32(bytes,0);
        bytes.Add(0);
        Param(bytes,1844968592u);
        I32(bytes,10);
        bytes.Add(0);bytes.Add(0);
        I32(bytes,3);
        Force(bytes);
        return bytes.ToArray();
    }
}
