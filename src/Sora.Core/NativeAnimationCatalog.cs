namespace Sora.Core;

public sealed record NativeAnimationClassification(string Category,string Rule,string Source,string Confidence);
public sealed record NativeAnimationCatalogRow(string Id,string Path,string Label,NativeAnimationClassification Classification,string DiscoverySource);
public sealed record NativeAnimationPage(int Total,int Offset,int Limit,NativeAnimationCatalogRow[] Rows);

/// <summary>Pagination and explicit path-rule classification over existing animation discovery, without new decoding formats.</summary>
public static class NativeAnimationCatalog
{
    public static NativeAnimationClassification Classify(string path)
    {
        string value=path.ToLowerInvariant();string category="unclassified",rule="no-recognized-path-rule";
        if(value.Contains("/dialog/")||value.Contains("/interact/")||value.Contains("_interact_")){category="interaction";rule="dialog-or-interact-path";}
        else if(value.Contains("skill")||value.Contains("_ult")){category="skill";rule="skill-or-ult-token";}
        else if(value.Contains("_attack_")||value.Contains("_atk_")){category="attack";rule="attack-or-atk-token";}
        else if(value.Contains("_idle")||value.Contains("_relax")){category="idle";rule="idle-or-relax-token";}
        else if(new[]{"_walk","_run","_move","_dash"}.Any(token=>ContainsToken(value,token))){category="move";rule="walk-run-move-dash-token";}
        return new(category,rule,"native-resource-path",category=="unclassified"?"unknown":"inferred");
    }
    private static bool ContainsToken(string value,string token)
    {
        for(int at=value.IndexOf(token,StringComparison.Ordinal);at>=0;at=value.IndexOf(token,at+1,StringComparison.Ordinal))
        {
            int end=at+token.Length;
            if(end>=value.Length||!char.IsAsciiLetter(value[end]))return true;
        }
        return false;
    }
    public static NativeAnimationCatalogRow[] Discover(NativeManifest manifest,string? characterPath=null)
    {
        string? token=NativeAnimationService.CharacterToken(characterPath);int processed=0;var rows=new List<NativeAnimationCatalogRow>();var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var asset in manifest.Assets) {
            if(processed++%4096==0)OperationProgress.Report("index-animation-resources",processed-1,manifest.Assets.Length);
            string path=asset.Path;
            if(!(path.EndsWith(".anim",StringComparison.OrdinalIgnoreCase)||path.EndsWith(".fbx",StringComparison.OrdinalIgnoreCase)&&path.Contains("/animations/",StringComparison.OrdinalIgnoreCase)))continue;
            if(token is not null&&!path.Contains("_"+token+"_",StringComparison.OrdinalIgnoreCase)&&!path.Contains("/"+token+"/",StringComparison.OrdinalIgnoreCase))continue;
            if(seen.Add(path))rows.Add(new(path,path,System.IO.Path.GetFileNameWithoutExtension(path),Classify(path),"manifest-address"));
        }
        return rows.ToArray();
    }
    public static NativeAnimationCatalogRow[] Discover(GameResources game,string? characterPath=null)
    {
        var rows=Discover(game.Manifest,characterPath).ToList();
        if(NativeAnimationService.CharacterToken(characterPath) is not null)
            rows.AddRange(NativeAnimationClip.DiscoverControllerClips(game,characterPath!).Select(clip=>new NativeAnimationCatalogRow(clip.ResourcePath,clip.ResourcePath,Path.GetFileNameWithoutExtension(clip.ResourcePath),Classify(clip.ResourcePath),"native-controller-reference")));
        return rows.DistinctBy(row=>row.Path,StringComparer.Ordinal).OrderBy(row=>row.Path,StringComparer.Ordinal).ToArray();
    }
    public static NativeAnimationPage Page(IEnumerable<NativeAnimationCatalogRow> source,string query,int offset,int limit,string? category=null)
    {
        Validation.Require(query is not null&&query.Length<=512&&!query.Any(char.IsControl),"Invalid animation query");
        Validation.Require(offset>=0&&limit is >=1 and <=1000,"Invalid animation page window");
        Validation.Require(category is null or "" or "all" or "unclassified" or "idle" or "move" or "attack" or "skill" or "interaction","Unknown animation category");
        string[] terms=query!.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        var rows=source.Where(row=>(string.IsNullOrEmpty(category)||category=="all"||row.Classification.Category==category)&&terms.All(term=>row.Path.Contains(term,StringComparison.OrdinalIgnoreCase)||row.Label.Contains(term,StringComparison.OrdinalIgnoreCase))).OrderBy(row=>row.Path,StringComparer.Ordinal).ToArray();
        return new(rows.Length,offset,limit,rows.Skip(offset).Take(limit).ToArray());
    }
}
