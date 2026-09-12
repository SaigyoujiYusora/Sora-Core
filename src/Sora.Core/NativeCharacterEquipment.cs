using System.Text.Json;

namespace Sora.Core;

public sealed record NativeMountTarget(bool MountToWeapon, int CharacterMountPoint, string? CharacterNodeName,
    int TargetWeaponIndex, int WeaponMountPoint, int LegacyMountPoint, string? LegacyNodeName, string Status);
public sealed record NativeEquipmentSlot(string SlotId, int WeaponIndex, string DeclarationRid, string ResourcePath, string? ResourceId,
    double Scale, bool ShowWhenIdle, bool ShowWhenFight, NativeMountTarget IdleMount, NativeMountTarget FightMount, string Status, JsonElement NativeDeclaration);
public sealed record NativeCharacterEquipmentData(string CharacterId, string SourcePath, string SourceId, string ModelId,
    NativeEquipmentSlot[] DedicatedEquipment, JsonElement[] DynamicWeapons, Dictionary<int,string> CharacterMountPoints, string Status, string? AnimationConfigPath = null);

/// <summary>Follows authored managed-reference edges; never treats unreferenced declarations as ownership.</summary>
public static class NativeCharacterEquipment
{
    private static JsonElement Json(SerializedObject value)=>JsonSerializer.SerializeToElement(value.Data,WireJson.Options);
    private static JsonElement[] Array(JsonElement value,string name)=>value.GetProperty(name).GetProperty("Array").EnumerateArray().ToArray();
    /// <summary>Native resource paths any character declares as authored StaticWeaponData; the identity rule for dedicated equipment.</summary>
    public static HashSet<string> DedicatedPaths(GameResources game)
    {
        var characters=game.Manifest.Assets.Where(a=>a.Path.StartsWith("assets/beyond/dynamicassets/gamedata/characterdata/data_chr_",StringComparison.Ordinal)&&a.Path.EndsWith(".asset",StringComparison.Ordinal))
            .Select(a=>Path.GetFileNameWithoutExtension(a.Path)["data_".Length..]).Distinct().Order(StringComparer.Ordinal).ToArray();
        var paths=new HashSet<string>(StringComparer.Ordinal);
        foreach(string character in characters) {
            OperationProgress.Report("index-dedicated-equipment",detail:character);
            foreach(var slot in Read(game,character).DedicatedEquipment) if(slot.ResourceId is not null) paths.Add(slot.ResourcePath);
        }
        return paths;
    }
    public static NativeCharacterEquipmentData Read(GameResources game,string characterId)
    {
        Validation.Require(characterId.StartsWith("chr_",StringComparison.Ordinal)&&characterId.Length<=128&&!characterId.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='_'),"Invalid character identity");
        string path="assets/beyond/dynamicassets/gamedata/characterdata/data_"+characterId+".asset";
        var candidates=game.Manifest.Assets.Where(a=>a.Path==path).DistinctBy(a=>(a.Path,a.Bundle)).ToArray();
        Validation.Require(candidates.Length==1,"Character declaration is absent or ambiguous");
        OperationProgress.Report("read-character-equipment",detail:characterId);
        var source=game.ResolveAddress(candidates[0],114);var data=Json(source.Object);
        var references=Array(data.GetProperty("references"),"RefIds");Validation.Require(references.Length<=10000,"Equipment reference limit exceeded");
        var refs=references.ToDictionary(r=>r.GetProperty("rid").GetInt64());
        JsonElement Resolve(JsonElement reference)=>refs.TryGetValue(reference.GetProperty("rid").GetInt64(),out var result)?result:throw new InvalidDataException("Missing equipment managed reference");
        string Type(JsonElement reference) {
            var type=reference.GetProperty("type");
            Validation.Require(type.GetProperty("asm").GetString()=="Gameplay.Beyond" && type.GetProperty("ns").GetString()!.StartsWith("Beyond.Gameplay",StringComparison.Ordinal),"Unsupported character component origin");
            return type.GetProperty("class").GetString()!;
        }
        var root=Resolve(data.GetProperty("data"));Validation.Require(Type(root)=="CharacterTemplateData","Unexpected character declaration type");
        var rootData=root.GetProperty("data");Validation.Require(rootData.GetProperty("id").GetString()==characterId,"Character declaration ID mismatch");
        var components=Array(rootData,"componentList").Select(Resolve).ToArray();Validation.Require(components.Length<=1024,"Character component limit exceeded");
        var mountComponent=components.Single(c=>Type(c)=="CharacterRootComponentData").GetProperty("data").GetProperty("mountPointData");
        var keys=Array(mountComponent,"_keyData");var values=Array(mountComponent,"_valueData");Validation.Require(keys.Length==values.Length,"Mount mapping arrays disagree");
        var mounts=keys.Select((key,index)=>(key.GetInt32(),values[index].GetString()!)).ToDictionary(p=>p.Item1,p=>p.Item2);
        string model=components.Single(c=>Type(c)=="ModelComponentData").GetProperty("data").GetProperty("modelId").GetString()!;
        var animation=components.SingleOrDefault(c=>Type(c)=="CharacterAnimationComponentData");
        string? animationConfig=animation.ValueKind==JsonValueKind.Undefined?null:animation.GetProperty("data").GetProperty("_animCfgPath").GetString();
        var component=components.SingleOrDefault(c=>Type(c)=="WeaponComponentData");
        if(component.ValueKind==JsonValueKind.Undefined)return new(characterId,path,source.Cab+":"+source.Object.Id,model,[],[],mounts,"no-weapon-component");
        var slots=new List<NativeEquipmentSlot>();var dynamic=new List<JsonElement>();var visited=new HashSet<long>();
        NativeMountTarget Mount(JsonElement declaration,string mode) {
            var target=declaration.GetProperty(mode+"MountTarget");int mount=target.GetProperty("characterMountPoint").GetInt32();int legacy=declaration.GetProperty("legacy"+char.ToUpperInvariant(mode[0])+mode[1..]+"MountPoint").GetInt32();
            bool toWeapon=target.GetProperty("mountToWeapon").GetInt32()!=0;
            return new(toWeapon,mount,mounts.GetValueOrDefault(mount),target.GetProperty("targetWeaponIndex").GetInt32(),target.GetProperty("weaponMountPoint").GetInt32(),legacy,mounts.GetValueOrDefault(legacy),
                toWeapon?"weapon-target-declared":mount>0&&mounts.ContainsKey(mount)?"character-target-declared":legacy>0?"legacy-target-requires-compatibility":mount==0?"battle-root-declared":"unspecified");
        }
        void Visit(JsonElement reference,int depth) {
            Validation.Require(depth<=16&&visited.Count<1024,"Equipment declaration graph limit exceeded");
            var entry=Resolve(reference);long rid=entry.GetProperty("rid").GetInt64();Validation.Require(visited.Add(rid),"Repeated or cyclic equipment declaration");
            string type=Type(entry);var value=entry.GetProperty("data");
            if(type=="WeaponDataWrapper"){foreach(var child in Array(value,"dataList"))Visit(child,depth+1);return;}
            if(type=="WeaponData"){dynamic.Add(value.Clone());return;}
            Validation.Require(type=="StaticWeaponData","Unsupported authored weapon declaration type: "+type);
            int index=value.GetProperty("weaponIndex").GetInt32();string resourcePath="assets/beyond/dynamicassets/"+value.GetProperty("_weaponPath").GetString()!.ToLowerInvariant()+".prefab";
            var resources=game.Manifest.Assets.Where(a=>a.Path==resourcePath).DistinctBy(a=>(a.Path,a.Bundle)).ToArray();
            slots.Add(new(characterId+":equipment:"+index,index,rid.ToString(System.Globalization.CultureInfo.InvariantCulture),resourcePath,resources.Length==1?GameCatalog.AddressId(resources[0]):null,
                value.GetProperty("weaponScale").GetDouble(),value.GetProperty("showWhenIdle").GetInt32()!=0,value.GetProperty("showWhenFight").GetInt32()!=0,Mount(value,"idle"),Mount(value,"fight"),resources.Length==1?"declared-binding-unverified":"missing-or-ambiguous-resource",value.Clone()));
        }
        foreach(var reference in Array(component.GetProperty("data").GetProperty("weaponCfg"),"weaponDataList"))Visit(reference,0);
        Validation.Require(slots.Select(slot=>slot.WeaponIndex).Distinct().Count()==slots.Count,"Duplicate dedicated equipment slot");
        return new(characterId,path,source.Cab+":"+source.Object.Id,model,slots.ToArray(),dynamic.ToArray(),mounts,"native-declarations-binding-unverified",animationConfig);
    }
}
