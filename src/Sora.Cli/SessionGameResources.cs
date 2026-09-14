using System.Security.Cryptography;
using Sora.Core;

/// <summary>One metadata-only snapshot. Index bytes and physical range metadata invalidate the cache; payload bytes are verified on extraction.</summary>
internal static class SessionGameResources
{
    private static string? key;
    private static GameResources? cached;
    private static string[] chunks=[];
    private static readonly string[] Blocks=["1CDDBF1F","7064D8E2","0CE8FA57","775A31D1","42A8FCA6"];
    private static string IndexKey(string root)
    {
        string data=Directory.Exists(Path.Combine(root,"Endfield_Data"))?Path.Combine(root,"Endfield_Data"):root;var pieces=new List<string>{root};int completed=0;
        foreach(string location in new[]{"Persistent","StreamingAssets"})foreach(string block in Blocks) {
            string folder=Path.Combine(data,location,"VFS",block);var file=new FileInfo(Path.Combine(folder,block+".blc"));
            OperationProgress.Report("validate-game-index-cache",completed++,10,file.FullName);
            if(!file.Exists){pieces.Add(file.FullName+"|missing");continue;}
            long length=file.Length;var modified=file.LastWriteTimeUtc;
            using var input=new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.Read);
            string digest=Convert.ToHexString(SHA256.HashData(input));file.Refresh();
            if(!file.Exists||file.Length!=length||file.LastWriteTimeUtc!=modified)throw new IOException("Game index changed while validating; retry after the update finishes");
            pieces.Add($"{file.FullName}|{length}|{modified.Ticks}|{digest}|{Directory.GetLastWriteTimeUtc(folder).Ticks}");
        }
        return string.Join("\n",pieces);
    }
    private static string ChunkKey(IEnumerable<string> paths)
    {
        return string.Join("\n",paths.Select(path=>{var file=new FileInfo(path);return file.Exists?$"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}":path+"|missing";}));
    }
    public static GameResources Read(string root)
    {
        if(!SessionDatabase.Enabled)return new GameResources(root);
        root=Path.GetFullPath(root);string before=IndexKey(root);string current=before+"\n"+ChunkKey(chunks);
        if(current==key&&cached is not null){OperationProgress.Report("game-index-cache-hit");return cached;}
        cached=null;key=null;chunks=[];var resources=new GameResources(root);string after=IndexKey(root);
        if(before!=after)throw new IOException("Game index changed while loading; retry after the update finishes");
        chunks=resources.PhysicalChunkPaths;key=after+"\n"+ChunkKey(chunks);cached=resources;return resources;
    }
}
