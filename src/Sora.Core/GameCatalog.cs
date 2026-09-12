using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Sora.Core;

public sealed record GameCatalogSource(string ManifestHash, string ManifestRevision, string Coverage, int AddressCount, int BundleCount,
    string MetadataStatus = "not-loaded", ResourceFileRecord[]? MetadataFiles = null, CatalogMetadataSource[]? MetadataSources = null,
    string AliasPolicy = "lowest-available-bundle-index");
public sealed record AssetLocator(string Parser, string Path, string? Hash = null, int? Bundle = null, string MetadataStatus = "unparsed",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? BundleCandidates = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ResourceFileRecord? Source = null);
public sealed record ImportCapability(string State, bool CanImport, string Reason, bool CanResolve = false, bool CanAttemptImport = false,
    string DependencyStatus = "unknown", string StructureStatus = "unparsed", int? SelectedBundle = null);

public static class GameCatalog
{
    public static string AddressId(AddressResource address) => "address:" + address.Hash.ToString("x16") + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address.Path))).ToLowerInvariant();
    public static AddressResource ChooseAlias(IEnumerable<AddressResource> addresses, Func<int,bool> available)
    {
        var candidates=addresses.DistinctBy(address=>(address.Hash,address.Path,address.Bundle)).OrderBy(address=>address.Bundle).ToArray();
        Validation.Require(candidates.Length>0&&candidates.Select(address=>(address.Hash,address.Path)).Distinct().Count()==1,"Resource path/hash is absent or ambiguous");
        return candidates.FirstOrDefault(address=>available(address.Bundle))??candidates[0];
    }
    public static DatabaseDocument Build(GameResources game)
    {
        var manifest=game.Manifest;var metadata=NativeCatalogMetadata.Read(game);var dedicated=NativeCharacterEquipment.DedicatedPaths(game);
        var records=manifest.Bundles.Select(bundle=>new AssetRecord("bundle:"+bundle.Index,bundle.Name,"Category "+bundle.Category,"bundle",bundle.Dependencies.Select(index=>"bundle:"+index).ToArray())).ToList();int completed=0,nextProgress=0;
        foreach(var group in manifest.Assets.GroupBy(address=>(address.Hash,address.Path)))
        {
            if(completed>=nextProgress){OperationProgress.Report("index-addresses",completed,manifest.Assets.Length);nextProgress=completed+512;}
            completed+=group.Count();var candidates=group.Select(address=>address.Bundle).Distinct().Order().ToArray();var address=group.OrderBy(value=>value.Bundle).First();
            bool item=address.Path.StartsWith("assets/beyond/dynamicassets/gameplay/prefabs/weapons/wpn_",StringComparison.Ordinal)
                && new[]{"funnel_","claym_","pistol_","sword_","lance_"}.Any(type=>Path.GetFileName(address.Path).StartsWith("wpn_"+type,StringComparison.Ordinal))&&address.Path.EndsWith(".prefab",StringComparison.Ordinal);
            bool character=Path.GetFileName(address.Path).StartsWith("chr_",StringComparison.Ordinal)&&address.Path.EndsWith("_uimodel.prefab",StringComparison.OrdinalIgnoreCase);
            string kind=character?"character":dedicated.Contains(address.Path)?"equipment":item?"weapon":"resource";var localized=metadata.ForPath(address.Path,kind);
            records.Add(new(AddressId(address),localized?.DisplayNameZh??Path.GetFileNameWithoutExtension(address.Path),address.Path,kind,
                candidates.Select(bundle=>"bundle:"+bundle).ToArray(),Locator:new(character?"character":item?"item":"unsupported",address.Path,address.Hash.ToString("x16"),address.Bundle,BundleCandidates:candidates.Length>1?candidates:null),Metadata:localized));
        }
        foreach(string npc in NativeNpcImport.Search(game,"")) {
            OperationProgress.Report("index-npc-declarations",detail:npc);
            records.Add(new("npc:"+npc,Path.GetFileNameWithoutExtension(npc),npc,"npc",[],Locator:new("npc",npc,Source:game.DeclaredSource(npc)),Metadata:metadata.ForPath(npc,"npc")));
        }
        return new(manifest.Version,records.ToArray(),CatalogSource:new(manifest.Hash,manifest.Revision,"complete-manifest-addresses-and-npc-declarations",manifest.Assets.Length,manifest.Bundles.Length,metadata.Status,metadata.Files,metadata.SourceStates));
    }
    private static bool SameSource(ResourceFileRecord stored, ResourceFileRecord current)=>stored.Resource.PayloadDigest.Length>0
        ?stored.Resource.PayloadDigest.Equals(current.Resource.PayloadDigest,StringComparison.OrdinalIgnoreCase)&&stored.Resource.Length==current.Resource.Length
        :stored.Resource==current.Resource;
    public static bool MetadataMatches(GameCatalogSource source, Func<string,CatalogMetadataSource> current)
    {
        if(source.MetadataSources is {} states)return states.All(state=>{
            var now=current(state.Path);return state.Available==now.Available&&(state.Source is null)==(now.Source is null)&&(state.Source is null||SameSource(state.Source,now.Source!));
        });
        if(source.MetadataStatus=="missing-table-source")return false; // old candidate did not retain the missing set
        return source.MetadataFiles is null||source.MetadataFiles.All(file=>current(file.Resource.Name) is {Available:true,Source:{} present}&&SameSource(file,present));
    }
    public static ImportCapability Capability(AssetRecord asset,DatabaseDocument database,GameResources? game=null)
    {
        if(asset.Scene is not null&&database.CatalogSource is null)return new("cached",true,"Complete decoded scene; standalone compatibility mode",true,true,"not-required","cached");
        if(asset.Locator?.Parser is not ("character" or "npc" or "item"))return new("unsupported",false,"Searchable resource; no standalone scene parser");
        if(game is null)return new("requires-game",false,"Select and validate the matching game directory");
        if(!Matches(database,game))return new("version-mismatch",false,"Game manifest or metadata sources differ; update or rebuild the catalog");
        if(asset.Locator.Parser=="npc") {
            if(!game.HasLogicalResource(asset.Locator.Path))return new("missing-dependencies",false,"Native NPC declaration is missing",DependencyStatus:"missing-declaration");
            if(asset.Locator.Source is {} source&&!SameSource(source,game.LogicalSource(asset.Locator.Path)))return new("source-changed",false,"NPC declaration changed; update the catalog",DependencyStatus:"declaration-changed");
            if(asset.Scene is not null)return new("cached",true,"Decoded scene with matching game and declaration",true,true,"cached","cached");
            return new("requires-parse",true,"Parse and import: native NPC parser available; template/mesh dependencies and declaration structure are not yet inspected",true,true,"declaration-present-dependencies-unknown","declaration-unparsed");
        }
        if(asset.Scene is not null)return new("cached",true,"Decoded scene with matching game metadata",true,true,"cached","cached");
        var candidates=asset.Locator.BundleCandidates??(asset.Locator.Bundle is int bundle?[bundle]:[]);
        if(candidates.Length==0)return new("requires-parse",true,"Native locator needs parsing; dependency availability is unknown",true,true);
        var checkedBundles=candidates.Order().Select(bundle=>(Bundle:bundle,Missing:game.MissingBundles(bundle))).ToArray();var selected=checkedBundles.FirstOrDefault(candidate=>candidate.Missing.Length==0);
        if(selected.Missing is null)return new("missing-dependencies",false,"No available alias closure. Missing bundles: "+string.Join(", ",checkedBundles.SelectMany(candidate=>candidate.Missing).Distinct().Take(3)),DependencyStatus:"missing-bundle-files");
        return new("requires-parse",true,asset.Locator.Parser=="item"?"Parse and import: LOD0 item parser available; renderer, skin and topology support is checked during parsing":"Parse and import: native character parser available; structure is checked during parsing",true,true,"bundle-files-present-content-unverified","unparsed",selected.Bundle);
    }
    public static bool Matches(DatabaseDocument database,GameResources game)=>database.CatalogSource is {} source
        && database.GameVersion==game.Manifest.Version&&source.ManifestHash==game.Manifest.Hash&&source.ManifestRevision==game.Manifest.Revision
        && MetadataMatches(source,path=>NativeCatalogMetadata.Capture(game,path));
    public static SceneDocument Scene(DatabaseDocument database,string id,string? root,GameResources? suppliedResources=null)
    {
        var asset=Catalog.For(database).Get(id);
        if(asset.Scene is not null&&database.CatalogSource is null)return asset.Scene;
        Validation.Require(!string.IsNullOrWhiteSpace(root),"Indexed import requires a matching game directory");var game=suppliedResources??new GameResources(root!);
        var capability=Capability(asset,database,game);Validation.Require(capability.CanAttemptImport,capability.Reason);
        if(asset.Scene is not null)return asset.Scene;
        OperationProgress.Report("decode-scene",detail:asset.Locator!.Path);
        var parsed=asset.Locator.Parser switch {
            "character"=>NativeCharacterImport.Import(game,asset.Locator.Path,game.SelectAddress(asset.Locator.Path,asset.Locator.Hash)),
            "npc"=>NativeNpcImport.Import(game,asset.Locator.Path),
            "item"=>NativeItemImport.Import(game,asset.Locator.Path,game.SelectAddress(asset.Locator.Path,asset.Locator.Hash)),
            _=>throw new InvalidDataException("Unsupported scene parser")
        };
        OperationProgress.Report("validate-scene");Validation.Database(parsed);return parsed.Assets.Single().Scene!;
    }
}
