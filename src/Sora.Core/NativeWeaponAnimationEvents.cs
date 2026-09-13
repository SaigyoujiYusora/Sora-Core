using System.Text.Json;
namespace Sora.Core;

public sealed record NativeWeaponAnimationEvent(int SourceIndex, double Time, string FunctionName, int PackedParameter,
    int WeaponIndex, string? SlotId, string TargetRole, string TargetStatus, bool? Visible, bool? ModelVisible, bool? HideWithEffect,
    bool? ShowAtFight, string? RequestedMountState, int? NativeInnerState, string? NativeInnerStateName,
    string? NativeWeaponStateName, string? TriggerName, int? ParamType, double FloatParameter,
    string SourceClipId, string CharacterDeclarationId, JsonElement Raw,
    string Decoder = "endfield-weapon-events-native-20260910-v1");

/// <summary>Native event payload semantics, not a controller/visual-effects evaluator.</summary>
public static class NativeWeaponAnimationEvents
{
    // GameAssembly SHA256 c24495e51b406f03b03890c4788ee618ae022c991405be5d5b8b787cb775ae89.
    // WeaponVisible From/To tokens 0600d74d/0600d74e and handler 0600d754:
    // signed /1000 target; flags &1, &2, &4; native states visible?(fight?1:4):(effect?6:7).
    // global-metadata.dat SHA256 0076743397acadf03d3b0064343a963c7c88863b8160526d397e4b3efb96f02e (v29);
    // both hashes re-checked against the installed game before reuse (audit/current-20260910/weapon-event-native-semantics.md).
    // Visual dispatcher 1831d4628: state 4 Appear resolves get_showWhenIdle then _TryAppearInIdle; state 6 Disappear
    // calls SetActive(false) before effect logic. Both visible states therefore stay gated by the authored
    // declaration (ShowInFight by showWhenFight, Appear by showWhenIdle); hidden states never show the model.
    // WeaponAnim From/To 0600d73b/0600d73c; handler LateTick calls SetAnimatorTrigger.
    // These base-binary methods have ILFix branches; no runtime hotfix evaluation is claimed.
    public static NativeWeaponAnimationEvent[] Decode(NativeEquipmentClipInfo clip, NativeCharacterEquipmentData declaration)
    {
        var result = new List<NativeWeaponAnimationEvent>();
        for (int i = 0; i < clip.Events.Length; i++)
        {
            var e = clip.Events[i];
            string? function = e.GetProperty("functionName").GetString();
            if (function is not ("WeaponVisible" or "WeaponAnim")) continue;
            int packed = e.GetProperty("intParameter").GetInt32();
            int index = packed / 1000;
            var dedicated = declaration.DedicatedEquipment.Where(s => s.WeaponIndex == index).ToArray();
            var dynamic = declaration.DynamicWeapons.Where(s => s.GetProperty("weaponIndex").GetInt32() == index).ToArray();
            string? slot = null;
            string role = "unresolved", status = "absent-or-ambiguous-native-weapon-index";
            if (dedicated.Length == 1 && dynamic.Length == 0)
            {
                slot = dedicated[0].SlotId; role = "dedicated"; status = "native-declaration-slot";
            }
            else if (dynamic.Length == 1 && dedicated.Length == 0)
            {
                slot = declaration.CharacterId + ":weapon:" + index; role = "generic"; status = "native-declaration-slot";
            }
            bool visibility = function == "WeaponVisible";
            bool visible = (packed & 1) != 0, effect = (packed & 2) != 0, fight = (packed & 4) != 0;
            int state = visible ? (fight ? 1 : 4) : (effect ? 6 : 7);
            bool? modelVisible=null;
            if(visibility&&slot is not null) {
                bool declared=dedicated.Length==1?(fight?dedicated[0].ShowWhenFight:dedicated[0].ShowWhenIdle)
                    :dynamic[0].GetProperty(fight?"showWhenFight":"showWhenIdle").GetInt32()!=0;
                modelVisible=visible&&declared;
            }
            result.Add(new(i, e.GetProperty("time").GetDouble(), function!, packed, index, slot, role, status,
                visibility ? visible : null, modelVisible, visibility ? effect : null, visibility ? fight : null,
                modelVisible==true ? (fight?"fight":"idle") : null,
                visibility ? state : null, visibility ? state switch { 1 => "ShowInFight", 4 => "Appear", 6 => "Disappear", _ => "Hidden" } : null,
                visibility ? state switch { 1 => "InFight", 4 => "FightToIdle", 6 => "Disappear", _ => "Hide" } : null,
                visibility ? null : e.GetProperty("data").GetString(), visibility ? null : packed % 1000 / 4,
                e.GetProperty("floatParameter").GetDouble(), clip.SourceId, declaration.SourceId, e.Clone()));
        }
        return result.ToArray();
    }
}
