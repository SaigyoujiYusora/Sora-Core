using Sora.Core;

internal static class CatalogReviewTests
{
    public static void Run(Action<string,Action> test,Action<Action> reject)
    {
        test("database compatibility distinguishes cached scenes from a verified index",()=>{
            var cached=new AssetRecord("a","A","","character",[],new SceneDocument("empty",[],[],[],[]));
            var uncached=new AssetRecord("b","B","","character",[]);
            foreach(string version in new[]{"v1","v2"}) {
                var result=DatabaseCompatibilityPolicy.Inspect(new("v1",[cached]),version,false);
                if(result.Mode!="standalone-scene"||!result.CanImportCachedScenes||result.CanResolveIndexedAssets
                    ||!result.AllAssetsHaveCachedScenes||result.VersionMatches!=(version=="v1"))throw new Exception();
            }
            var partial=DatabaseCompatibilityPolicy.Inspect(new("v1",[cached,uncached]),"v1",false);
            if(partial.AllAssetsHaveCachedScenes||partial.CachedSceneCount!=1||partial.AssetCount!=2)throw new Exception();
            foreach(AssetRecord[] assets in new[]{Array.Empty<AssetRecord>(),new[]{uncached}}) {
                var empty=DatabaseCompatibilityPolicy.Inspect(new("v1",assets),"v1",false);
                if(empty.Mode!="legacy-snapshot"||empty.CanImportCachedScenes||empty.AllAssetsHaveCachedScenes)throw new Exception();
            }
            foreach(bool matches in new[]{false,true}) {
                var indexed=DatabaseCompatibilityPolicy.Inspect(new("v1",[cached],CatalogSource:new("hash","revision","coverage",1,1)),"v1",matches);
                if(indexed.Mode!="indexed-game"||indexed.CanImportCachedScenes!=matches||indexed.CanResolveIndexedAssets!=matches)throw new Exception();
            }
        });
        test("localized catalog preserves internal suffix and full path search identity",()=>{
            const string path="assets/test/items/wpn_funnel_0001.prefab";
            var asset=new AssetRecord("address:stable-hash","��������","legacy/source.prefab","item",[],
                Locator:new("item",path),Metadata:new("wpn_funnel_0001","��������","English weapon","native-cn","test","row","123"));
            var catalog=new Catalog(new("v",[asset,new("other","Other","","item",[])]));
            foreach(string query in new[]{"��������","wpn_funnel_0001","wpn_funnel_0001.prefab",path,path.ToUpperInvariant(),"English weapon","legacy/source.prefab"}){
                var result=System.Text.Json.JsonSerializer.SerializeToElement(catalog.Search(query,0,20),WireJson.Options);
                if(result.GetProperty("total").GetInt32()!=1||result.GetProperty("rows")[0].GetProperty("id").GetString()!=asset.Id)throw new Exception("Search lost resource identity: "+query);
            }
        });
        test("manifest alias choice is stable and prefers an available closure",()=>{
            AddressResource[] aliases=[new(7,"assets/item.prefab",9,1),new(7,"assets/item.prefab",2,1),new(7,"assets/item.prefab",9,1)];
            if(GameCatalog.ChooseAlias(aliases,bundle=>bundle==9).Bundle!=9||GameCatalog.ChooseAlias(aliases.Reverse(),_=>true).Bundle!=2)throw new Exception();
            reject(()=>GameCatalog.ChooseAlias([aliases[0],new(8,"assets/item.prefab",1,1)],_=>true));
        });
        test("missing metadata inventory matches missing state and invalidates on appearance",()=>{
            var missing=NativeCatalogMetadata.SourcePaths.Select(path=>new CatalogMetadataSource(path,false,null)).ToArray();
            var source=new GameCatalogSource("hash","","coverage",0,0,"missing-table-source",[],missing);
            if(!GameCatalog.MetadataMatches(source,path=>new(path,false,null)))throw new Exception();
            var file=new ResourceFileRecord("Persistent/VFS/42A8FCA6/42A8FCA6.blc",new("Data/"+missing[0].Path,new string('0',32)+".chk",0,10,false,0,"chunk","digest"));
            if(GameCatalog.MetadataMatches(source,path=>new(path,path==missing[0].Path,path==missing[0].Path?file:null)))throw new Exception();
            var present=missing.Select((state,index)=>index==0?state with {Available=true,Source=file}:state).ToArray();source=source with {MetadataSources=present,MetadataFiles=[file]};
            if(!GameCatalog.MetadataMatches(source,path=>new(path,path==missing[0].Path,path==missing[0].Path?file:null)))throw new Exception();
            if(GameCatalog.MetadataMatches(source,path=>new(path,path==missing[0].Path,path==missing[0].Path?file with {Resource=file.Resource with {PayloadDigest="changed"}}:null)))throw new Exception();
            if(GameCatalog.MetadataMatches(source with {MetadataSources=null},path=>new(path,false,null)))throw new Exception();
        });
        test("missing name metadata keeps internal label with explicit status",()=>{
            var fallback=new NativeCatalogMetadata().ForPath("assets/test/chr_0013_aglina_uimodel.prefab","character")!;
            if(fallback.InternalName!="chr_0013_aglina_uimodel"||fallback.DisplayNameZh is not null||fallback.LocalizationStatus!="missing-source")throw new Exception();
        });
        test("cached standalone capability is an attemptable cached scene",()=>{
            var scene=new SceneDocument("empty",[],[],[],[]);var asset=new AssetRecord("a","A","","character",[],scene);
            var capability=GameCatalog.Capability(asset,new("v",[asset]));
            if(capability.State!="cached"||!capability.CanAttemptImport||!capability.CanImport)throw new Exception();
        });
    }
}

