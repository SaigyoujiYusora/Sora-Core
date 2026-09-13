namespace Sora.Core;

public sealed record NativeWeaponCompatibility(string Status,string CharacterId,int? CharacterWeaponType,string? WeaponId,int? WeaponType,string[] Sources,string Message,string? DedicatedExclusion=null);
public sealed record NativeWeaponAdaptation(string Status,string Confidence,string Rule,string Source,string Message);
public sealed record NativeCompatibleWeaponRow(string Id,string Label,string InternalName,int WeaponType,NativeWeaponAdaptation Adaptation,ImportCapability Capability);
public sealed record NativeUnknownWeapon(string Id,string Label,string InternalName,string Reason);
public sealed record NativeCompatibleWeapons(int Total,int Offset,int Limit,NativeCompatibleWeaponRow[] Rows,NativeWeaponCompatibility Compatibility,NativeUnknownWeapon[] Unknown);
public sealed record NativeWeaponAssemblyData(SceneDocument? Scene,NativeEquipmentBinding[] GenericSlots,NativeWeaponCompatibility Compatibility,
    NativeEquipmentGap[] Gaps,Dictionary<string,bool> CanBindStates,string Status);

/// <summary>One WeaponBasicTable declaration. ModelPath is the asset that the declared row itself references.</summary>
public sealed record NativeWeaponTypeDeclaration(string WeaponId,string ResourcePath,int WeaponType);

/// <summary>
/// Adaptation evidence for one general-weapon resource. WeaponType is null exactly when the status is unknown.
/// BaseIdentityStatus is not-applicable, consistent or ambiguous; it never claims confirmed base ownership.
/// </summary>
public sealed record NativeWeaponRecognition(string Status,string Confidence,int? WeaponType,string? BaseWeaponId,string BaseIdentityStatus,string Rule,string Source,string Message);

public static class NativeWeaponAssembly
{
    private sealed record WeaponTypeRow(string Id,string Path,int Type,NativeWeaponRecognition Recognition);
    private sealed record UnknownWeapon(AssetRecord Asset,NativeWeaponRecognition Recognition);
    private sealed record WeaponTypes(string Id,int Type,WeaponTypeRow[] Weapons,UnknownWeapon[] Unknown);
    private static Dictionary<string,object?> Row(object? value)=>value as Dictionary<string,object?>??throw new InvalidDataException("Invalid native weapon type row");
    public const string ConfirmedSource="TableCfg/CharacterTable.bytes:weaponType;TableCfg/WeaponBasicTable.bytes:weaponId/weaponType/modelPath";
    public const string InferredSource="catalog resource name <declared-weapon-id>_refined.prefab and base resource path <declared-model-path>;TableCfg/WeaponBasicTable.bytes:weaponId/weaponType/modelPath";
    public const string DedicatedExclusion="Candidate set is limited to catalog kind=weapon; character-declared dedicated equipment (kind=equipment, resolved from native StaticWeaponData) is excluded structurally and cannot appear";
    private const int Type2=2;

    /// <summary>
    /// Classifies one general-weapon resource against the native WeaponBasicTable declarations.
    /// A declared row is confirmed native type evidence. A resource with no declared row is admitted only when it
    /// is named <declared-weapon-id>_refined.prefab; its type is inferred and only when the two available native
    /// associations (the name-derived base id and the base resource path) agree on the type. Conflicting or missing
    /// associations stay unknown, and the base ownership is reported as consistent or ambiguous, never confirmed.
    /// </summary>
    public static NativeWeaponRecognition Recognize(string resourcePath,IReadOnlyDictionary<string,NativeWeaponTypeDeclaration> declaredByPath,
        IReadOnlyDictionary<string,NativeWeaponTypeDeclaration> declaredById)
    {
        if(declaredByPath.TryGetValue(resourcePath,out var declared))
            return new("confirmed-native-type","confirmed",declared.WeaponType,null,"not-applicable",
                "character-weapon-type-equals-declared-weapon-row-type",ConfirmedSource,
                "WeaponBasicTable row '"+declared.WeaponId+"' declares weaponType "+declared.WeaponType+" for this exact resource path");
        if(resourcePath.EndsWith("_refined.prefab",StringComparison.Ordinal)) {
            string baseId=Path.GetFileNameWithoutExtension(resourcePath)[..^"_refined".Length];
            string derivedPath=resourcePath[..^"_refined.prefab".Length]+".prefab";
            declaredById.TryGetValue(baseId,out var byId);
            declaredByPath.TryGetValue(derivedPath,out var byPath);
            // The published rule requires both native associations. One association alone is incomplete evidence.
            if(byId is not null&&byPath is not null) {
                if(byId.WeaponType!=byPath.WeaponType)
                    return new("unknown-native-weapon-type","unknown",null,null,"conflicting",
                        "refined-name-and-base-path-associations-disagree","TableCfg/WeaponBasicTable.bytes:weaponId/modelPath against resource name",
                        "The name-derived base id '"+baseId+"' maps to weaponType "+byId.WeaponType+" while the base resource path maps to row '"+byPath.WeaponId+"' with weaponType "+byPath.WeaponType+"; the two native associations conflict, so no type is chosen");
                int type=byId.WeaponType;bool ambiguous=byId.WeaponId!=byPath.WeaponId;
                string ownership=ambiguous
                    ?"The name-derived base id '"+baseId+"' and the base-path row '"+byPath.WeaponId+"' differ, so base ownership is ambiguous and not confirmed."
                    :"The name-derived base id '"+baseId+"' and the base resource path agree on the declared row.";
                return new("inferred-native-base-type","inferred",type,byId.WeaponId,ambiguous?"ambiguous":"consistent",
                    "refined-resource-name-and-base-path-agree-on-native-type",InferredSource,
                    "No WeaponBasicTable row references this refined resource; weaponType "+type+" is inferred because both native associations are present and agree on the type. "+ownership+" Mount target and parsing are not established.");
            }
            string missing=byId is null&&byPath is null
                ?"Neither the name-derived base id '"+baseId+"' nor the base resource path '"+derivedPath+"' resolves to a declared row"
                :byId is null
                    ?"Only the base resource path '"+derivedPath+"' resolves to a declared row; the name-derived base id '"+baseId+"' does not"
                    :"Only the name-derived base id '"+baseId+"' resolves to a declared row; the base resource path '"+derivedPath+"' does not";
            return new("unknown-native-weapon-type","unknown",null,null,"incomplete",
                "refined-requires-both-name-and-base-path-native-associations","catalog resource name against TableCfg/WeaponBasicTable.bytes:weaponId/modelPath",
                missing+"; the refined variant's native type is inferred only when both associations exist and agree, so no type is chosen for this resource");
        }
        return new("unknown-native-weapon-type","unknown",null,null,"unresolved",
            "no-declared-row-and-no-refined-base-derivation","catalog kind=weapon without WeaponBasicTable identity",
            "No WeaponBasicTable row declares this resource and no <declared-weapon-id>_refined.prefab base derivation applies; native weapon type is unknown");
    }
    private static WeaponTypes Types(GameResources game,DatabaseDocument database,AssetRecord owner)
    {
        Validation.Require(owner.Kind=="character","Generic weapon compatibility requires a character asset");
        string id=Path.GetFileNameWithoutExtension(owner.Locator?.Path??owner.Id).Replace("_uimodel","",StringComparison.Ordinal);
        var characters=new NativeTable(game.GetBytes("TableCfg/CharacterTable.bytes"));var weapons=new NativeTable(game.GetBytes("TableCfg/WeaponBasicTable.bytes"));
        Validation.Require(characters.RowFieldTypes.GetValueOrDefault("weaponType")==6&&weapons.RowFieldTypes.GetValueOrDefault("weaponType")==6,"Native weapon-type schema is unavailable");
        var selected=characters.Rows("charId","weaponType").Where(entry=>entry.Key is string key&&key==id).ToArray();Validation.Require(selected.Length==1,"Character weapon type is absent or ambiguous");
        int type=(int)Row(selected[0].Value)["weaponType"]!;
        var declaredByPath=new Dictionary<string,NativeWeaponTypeDeclaration>(StringComparer.Ordinal);
        var declaredById=new Dictionary<string,NativeWeaponTypeDeclaration>(StringComparer.Ordinal);
        foreach(var entry in weapons.Rows("weaponId","weaponType","modelPath")) {
            var row=Row(entry.Value);string weaponId=(string)row["weaponId"]!;Validation.Require(entry.Key is string key&&key==weaponId,"Weapon table key disagrees with identity");
            var declaration=new NativeWeaponTypeDeclaration(weaponId,"assets/beyond/dynamicassets/"+((string)row["modelPath"]!).ToLowerInvariant(),(int)row["weaponType"]!);
            Validation.Require(declaredById.TryAdd(weaponId,declaration)&&declaredByPath.TryAdd(declaration.ResourcePath,declaration),"Duplicate native weapon id or resource path");
        }
        var known=new List<WeaponTypeRow>();
        var unknown=new List<UnknownWeapon>();
        foreach(var asset in database.Assets) {
            if(asset.Kind!="weapon") continue;
            string path=asset.Locator?.Path??asset.Id;var recognition=Recognize(path,declaredByPath,declaredById);
            if(recognition.WeaponType is null) {unknown.Add(new(asset,recognition));continue;}
            known.Add(new WeaponTypeRow(recognition.BaseWeaponId??declaredByPath[path].WeaponId,path,recognition.WeaponType.Value,recognition));
        }
        return new(id,type,known.OrderBy(row=>row.Path,StringComparer.Ordinal).ToArray(),unknown.ToArray());
    }
    private static NativeWeaponAdaptation Adaptation(WeaponTypeRow row)=>new(row.Recognition.Status,row.Recognition.Confidence,row.Recognition.Rule,row.Recognition.Source,row.Recognition.Message);
    public static NativeCompatibleWeapons Compatible(GameResources game,DatabaseDocument database,string ownerId,string query,int offset,int limit)
    {
        Validation.Require(offset>=0&&limit is >=1 and <=1000&&query.Length<=512&&!query.Any(char.IsControl),"Invalid compatible-weapon query");
        var owner=Catalog.For(database).Get(ownerId);var types=Types(game,database,owner);var byPath=types.Weapons.ToDictionary(weapon=>weapon.Path,StringComparer.Ordinal);
        bool Matches(AssetRecord asset)=>asset.Label.Contains(query,StringComparison.OrdinalIgnoreCase)||(asset.Metadata?.InternalName.Contains(query,StringComparison.OrdinalIgnoreCase)??false)||(asset.Locator?.Path??asset.Id).Contains(query,StringComparison.OrdinalIgnoreCase);
        bool MatchesUnknown(UnknownWeapon entry)=>Matches(entry.Asset);
        var rows=database.Assets.Where(asset=>asset.Kind=="weapon"&&byPath.TryGetValue(asset.Locator?.Path??asset.Id,out var weapon)&&weapon.Type==types.Type&&Matches(asset))
            .OrderBy(asset=>asset.Id,StringComparer.Ordinal).ToArray();
        var unknown=types.Unknown.Where(MatchesUnknown).OrderBy(entry=>entry.Asset.Id,StringComparer.Ordinal)
            .Select(entry=>new NativeUnknownWeapon(entry.Asset.Id,entry.Asset.Label,entry.Asset.Metadata?.InternalName??Path.GetFileNameWithoutExtension(entry.Asset.Locator?.Path??entry.Asset.Id),entry.Recognition.Message)).ToArray();
        return new(rows.Length,offset,limit,rows.Skip(offset).Take(limit).Select(asset=>{
                var weapon=byPath[asset.Locator?.Path??asset.Id];
                return new NativeCompatibleWeaponRow(asset.Id,asset.Label,asset.Metadata?.InternalName??Path.GetFileNameWithoutExtension(asset.Locator?.Path??asset.Id),weapon.Type,
                    Adaptation(weapon),GameCatalog.Capability(asset,database,game));
            }).ToArray(),
            new("native-type-filter",types.Id,types.Type,null,null,["TableCfg/CharacterTable.bytes:weaponType","TableCfg/WeaponBasicTable.bytes:weaponType/modelPath"],
                "Candidates are graded per row by their own native type evidence: confirmed rows have an exact WeaponBasicTable resource match, inferred rows have agreeing refined base associations; this is type adaptation only, not a verified mount",DedicatedExclusion),unknown);
    }
    private static readonly string[] TotalSources=["TableCfg/CharacterTable.bytes:weaponType","TableCfg/WeaponBasicTable.bytes:weaponId/weaponType/modelPath"];
    private static NativeWeaponAssemblyData Blocked(string status,WeaponTypes types,string weaponId,int? weaponType,string message,string code,string[]? sources=null) =>
        new(null,[],new(status,types.Id,types.Type,weaponId,weaponType,sources??TotalSources,message,DedicatedExclusion),
            [new(weaponId,code,message,true)],new(){{"idle",false},{"fight",false}},"blocked");
    public static NativeWeaponAssemblyData Resolve(GameResources game,DatabaseDocument database,string ownerId,string weaponId,string root)
    {
        var owner=Catalog.For(database).Get(ownerId);var weapon=Catalog.For(database).Get(weaponId);var types=Types(game,database,owner);
        string weaponPath=weapon.Locator?.Path??weapon.Id;
        var choice=types.Weapons.SingleOrDefault(row=>row.Path==weaponPath);
        if(weapon.Kind=="equipment") return Blocked("dedicated-equipment-not-general",types,weaponId,choice?.Type,
            "Character-declared dedicated equipment belongs to the owner equipment relation, not the general-weapon candidate set","dedicated-equipment-not-general");
        if(weapon.Kind!="weapon") return Blocked("not-a-general-weapon",types,weaponId,choice?.Type,
            "Selected asset is not a general weapon resource","not-a-general-weapon");
        if(choice is null) {
            var known=types.Unknown.SingleOrDefault(entry=>entry.Asset.Id==weaponId);
            return Blocked("unknown-weapon-type",types,weaponId,null,
                known?.Recognition.Message??"Selected weapon has no WeaponBasicTable row and no derivable <declared-weapon-id>_refined.prefab base; native type compatibility is unknown","unknown-native-weapon-type");
        }
        var recognition=choice.Recognition;bool inferred=recognition.Status=="inferred-native-base-type";
        var sources=inferred?[recognition.Source,..TotalSources]:TotalSources;
        if(choice.Type!=types.Type) return Blocked(inferred?"inferred-type-mismatch":"incompatible",types,weaponId,choice.Type,
            (inferred?"Inferred type mismatch: "+recognition.Message+" Client character weaponType is "+types.Type+", the inferred weaponType is "+choice.Type+".":"Native weaponType "+choice.Type+" does not match character weaponType "+types.Type),
            inferred?"inferred-type-mismatch":"incompatible-weapon",sources);
        var ownerScene=owner.Scene??GameCatalog.Scene(database,ownerId,root);
        var assembly=NativeEquipmentAssembly.Resolve(game,ownerScene,owner.Locator?.Path??owner.Id,includeDynamic:true);
        // The mount target is the owner's native dynamic-weapon declaration, which is the same evidence for a
        // WeaponBasicTable-confirmed row and for an inferred refined row; the weapon only supplies the parsed scene.
        var scene=GameCatalog.Scene(database,weaponId,root);var slots=(assembly.DynamicBindings??[]).Select(slot=>slot with {ResourceId=weaponId}).ToArray();
        var ready=new Dictionary<string,bool>{{"idle",slots.Length>0&&slots.All(slot=>!slot.Idle.Visible||slot.Idle.CanBind)},{"fight",slots.Length>0&&slots.All(slot=>!slot.Fight.Visible||slot.Fight.CanBind)}};
        var gaps=assembly.Gaps.Where(gap=>gap.Scope.Contains(":weapon:",StringComparison.Ordinal)||gap.Code.Contains("animation",StringComparison.Ordinal)||gap.Code.Contains("damping",StringComparison.Ordinal)).ToList();
        if(slots.Length==0)gaps.Add(new(ownerId,"no-general-weapon-slots","Native character declaration has no dynamic weapon slots",true));
        var mountReady=ready.Values.All(value=>value);
        if(inferred&&!mountReady)gaps.Add(new(weaponId,"inferred-base-type-match-mount-unverified",recognition.Message,true));
        // An inferred row stays inferred: the coarse type adaptation is never re-labelled as WeaponBasicTable-confirmed,
        // and the original recognition message (including ambiguous base ownership) is preserved. A verified mount is
        // claimed only when the owner's native dynamic slots actually resolve for both states and the resource parsed.
        NativeWeaponCompatibility compatibility=inferred
            ?mountReady
                ?new("inferred-base-type-mount-verified",types.Id,types.Type,choice.Id,choice.Type,sources,
                    recognition.Message+" The owner's native dynamic weapon slots and the parsed refined resource establish the mount target; the type adaptation remains inferred, not confirmed.",DedicatedExclusion)
                :new("inferred-type-candidate-not-mount-verified",types.Id,types.Type,choice.Id,choice.Type,sources,
                    recognition.Message,DedicatedExclusion)
            :new("native-type-match",types.Id,types.Type,choice.Id,choice.Type,sources,
                "Native type match; attach each dynamic slot as an independent instance",DedicatedExclusion);
        return new(scene,slots,compatibility,gaps.ToArray(),ready,mountReady?"static-states-ready-animation-unverified":"blocked-general-weapon-mount");
    }
}
