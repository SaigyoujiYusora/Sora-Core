namespace Sora.Core;

/// <summary>Preserves an imported resource snapshot while adding newly inspected native resources.</summary>
public static class EndfieldResourceIndexMerge
{
    public static EndfieldResourceIndex Merge(EndfieldResourceIndex? original, EndfieldResourceIndex fresh)
    {
        Validation.ResourceIndex(fresh);
        if(original is null)return fresh;
        Validation.ResourceIndex(original);
        Validation.Require(Root(original.SourceRoot)==Root(fresh.SourceRoot)&&original.ManifestHash==fresh.ManifestHash&&original.ManifestRevision==fresh.ManifestRevision,"Cannot merge different native resource snapshots");
        var files=original.Files.ToList();var fileKeys=new Dictionary<(string,string),int>();
        for(int i=0;i<files.Count;i++)Validation.Require(fileKeys.TryAdd(Key(files[i]),i),"Ambiguous existing native source file");
        var remap=new int[fresh.Files.Length];
        for(int i=0;i<fresh.Files.Length;i++)
        {
            var file=fresh.Files[i];var key=Key(file);
            if(fileKeys.TryGetValue(key,out int old)){Validation.Require(files[old]==file,"Native source file changed within the same snapshot");remap[i]=old;}
            else{remap[i]=files.Count;fileKeys.Add(key,files.Count);files.Add(file);}
        }
        var cabs=original.Cabs.ToList();var indices=cabs.Select((cab,index)=>(cab,index)).ToDictionary(x=>x.cab.Id,x=>x.index,StringComparer.OrdinalIgnoreCase);
        foreach(var entry in fresh.Cabs)
        {
            var next=entry with{File=entry.File is {} file?remap[file]:null};
            if(!indices.TryGetValue(next.Id,out int index)){indices.Add(next.Id,cabs.Count);cabs.Add(next);continue;}
            var previous=cabs[index];
            if(previous.Status!="unresolved"&&next.Status!="unresolved")Validation.Require(previous.File==next.File&&previous.Entry==next.Entry,"CAB location changed within the same snapshot");
            if(previous.Status=="decoded"&&next.Status=="decoded")
                Validation.Require(Set(previous.ContainerPaths!,next.ContainerPaths!,StringComparer.Ordinal)&&previous.ClassIds!.ToHashSet().SetEquals(next.ClassIds!)&&Set(previous.Dependencies!,next.Dependencies!,StringComparer.OrdinalIgnoreCase),"Decoded CAB metadata changed within the same snapshot");
            if(Rank(next.Status)>Rank(previous.Status))cabs[index]=next with{Id=previous.Id};
        }
        var merged=original with{Files=files.ToArray(),Cabs=cabs.ToArray()};Validation.ResourceIndex(merged);return merged;
    }
    private static int Rank(string status)=>status=="decoded"?2:status=="indexed"?1:0;
    private static bool Set(string[] a,string[] b,StringComparer comparer)=>a.ToHashSet(comparer).SetEquals(b);
    private static string Root(string value)=>value.Replace('\\','/').TrimEnd('/').ToUpperInvariant();
    private static (string,string) Key(ResourceFileRecord file)=>(file.BlockIndexPath.ToUpperInvariant(),file.Resource.Name.ToUpperInvariant());
}
