namespace Sora.Core;

public sealed record CatalogAssetMetadata(string InternalName, string? DisplayNameZh, string? DisplayNameEn, string LocalizationStatus,
    string SourceTable, string RowId, string NameTextId, string? DefaultWeaponId = null, int? WeaponType = null, string? LocalizationDetail = null);
public sealed record CatalogMetadataSource(string Path, bool Available, ResourceFileRecord? Source);

public sealed class NativeCatalogMetadata
{
    public static readonly string[] SourcePaths = ["TableCfg/CharacterTable.bytes", "TableCfg/WeaponBasicTable.bytes", "TableCfg/I18nTextTable_CN.bytes", "TableCfg/ItemTable.bytes"];
    private readonly Dictionary<string,CatalogAssetMetadata> paths = new(StringComparer.OrdinalIgnoreCase);
    public ResourceFileRecord[] Files { get; private init; } = [];
    public CatalogMetadataSource[] SourceStates { get; private init; } = [];
    public string Status { get; private init; } = "unavailable";
    public CatalogAssetMetadata? ForPath(string path, string? kind = null)
    {
        if(paths.TryGetValue(path,out var metadata)) return metadata;
        if(kind is not ("character" or "weapon" or "npc")) return null;
        string name=Path.GetFileNameWithoutExtension(path);
        string row=kind=="character"?name.Replace("_uimodel","",StringComparison.Ordinal):name;
        string source=kind=="character"?SourcePaths[0]:kind=="weapon"?SourcePaths[3]:"native-npc-declaration";
        bool missing=kind!="npc"&&(Status=="unavailable"||SourceStates.Any(state=>!state.Available&&(state.Path==source||state.Path==SourcePaths[2]||kind=="weapon"&&state.Path==SourcePaths[1])));
        return new(name,null,null,kind=="npc"?"not-mapped":missing?"missing-source":"missing-row",source,row,"0",LocalizationDetail:kind=="npc"?"NPC display-name mapping has not been decoded":missing?"Required native name table is unavailable":"No native display-name row maps to this exact resource path");
    }
    private static Dictionary<string,object?> Row(object? value) => value as Dictionary<string,object?> ?? throw new InvalidDataException("Native metadata row is not an object");
    private static string Text(Dictionary<string,object?> row,string key) => row.GetValueOrDefault(key) as string ?? throw new InvalidDataException("Missing native metadata text: "+key);
    public static CatalogMetadataSource Capture(GameResources game,string path)=>new(path,game.HasLogicalResource(path),game.DeclaredSource(path));
    public static NativeCatalogMetadata Read(GameResources game)
    {
        var states=SourcePaths.Select(path=>Capture(game,path)).ToArray();
        var output=new NativeCatalogMetadata{Status=states.All(state=>state.Available)?"native-tables-cn":"missing-table-source",SourceStates=states,Files=states.Where(state=>state.Source is not null).Select(state=>state.Source!).ToArray()};
        var names=new Dictionary<ulong,string>();
        if(states[2].Available) {
            var language=new NativeTable(game.GetBytes(SourcePaths[2]));
            Validation.Require(language.Name=="I18nTextTable_CN","Unexpected localization table");
            names=language.Rows().ToDictionary(row=>row.Key is ulong id?id:throw new InvalidDataException("Invalid localization ID"),row=>row.Value as string ?? throw new InvalidDataException("Missing localized text"));
        }
        var itemNames=new Dictionary<string,object?>();
        if(states[3].Available) {
            var itemTable=new NativeTable(game.GetBytes(SourcePaths[3]));
            Validation.Require(itemTable.RowFieldTypes.GetValueOrDefault("id")==7&&itemTable.RowFieldTypes.GetValueOrDefault("name")==8,"Unsupported item-name schema");
            itemNames=itemTable.Rows("id","name").ToDictionary(entry=>entry.Key is string id?id:throw new InvalidDataException("Invalid item ID"),entry=>Row(entry.Value)["name"]);
        }
        void Load(string source,string idField,string nameField,string[] fields,bool character) {
            if(!game.HasLogicalResource(source)) return;
            var table=new NativeTable(game.GetBytes(source));
            Validation.Require(table.RowFieldTypes.GetValueOrDefault(idField)==7&&table.RowFieldTypes.GetValueOrDefault(nameField)==8&&table.RowFieldTypes.GetValueOrDefault("weaponType")==6,"Unsupported native metadata schema");
            foreach(var entry in table.Rows(fields)) {
                var row=Row(entry.Value);string id=Text(row,idField);Validation.Require(entry.Key is string key&&key==id,"Metadata row key disagrees with identity");
                var name=Row(row[nameField]);Validation.Require(name.GetValueOrDefault("id") is ulong,"Missing localization hash");ulong textId=(ulong)name["id"]!;
                string? english=character?Text(row,"engName"):names.GetValueOrDefault(textId);string nameSource=source;
                if(!character) {nameSource=SourcePaths[3];textId=itemNames.TryGetValue(id,out var itemName)&&itemName is Dictionary<string,object?> itemText&&itemText.GetValueOrDefault("id") is ulong itemTextId?itemTextId:0;}
                names.TryGetValue(textId,out string? zh);
                string path=character?"assets/beyond/dynamicassets/gameplay/prefabs/uimodels/"+id+"_uimodel.prefab":"assets/beyond/dynamicassets/"+Text(row,"modelPath").ToLowerInvariant();
                bool missing=!states[2].Available||!character&&!states[3].Available;
                var metadata=new CatalogAssetMetadata(character?id+"_uimodel":Path.GetFileNameWithoutExtension(path),zh,english,
                    zh is not null?"native-cn":missing?"missing-source":"missing-translation",nameSource,id,textId.ToString(System.Globalization.CultureInfo.InvariantCulture),character?Text(row,"defaultWeaponId"):null,(int)row["weaponType"]!,missing?"Required native name table is unavailable":zh is null?"Native text ID has no CN translation":null);
                Validation.Require(output.paths.TryAdd(path,metadata),"Multiple metadata rows map to one model path");
            }
        }
        Load(SourcePaths[0],"charId","name",["charId","name","engName","defaultWeaponId","weaponType"],true);
        Load(SourcePaths[1],"weaponId","engName",["weaponId","engName","modelPath","weaponType"],false);
        return output;
    }
}
