namespace Sora.Core;

// Separate from the SRED scene contract: no map parser or scene conversion exists yet.
public sealed record MapDataRequest(string MapId);
public sealed record MapDataDocument(string MapId, MapChunkRecord[] Chunks);
public sealed record MapChunkRecord(string Id, string[] ResourceIds);
public sealed record MapDataStatus(bool Supported, string State, string Message);
public sealed record MapDataResult(MapDataStatus Status, MapDataDocument? Document);

public interface IMapDataReader
{
    MapDataStatus Status { get; }
    MapDataResult Read(MapDataRequest request);
}

/// <summary>Extension point only; never reads files or returns an invented empty map.</summary>
public sealed class PlaceholderMapDataReader : IMapDataReader
{
    public MapDataStatus Status { get; } = new(false, "placeholder",
        "Map data parsing and scene import are not implemented.");

    public MapDataResult Read(MapDataRequest request)
    {
        Validation.Require(request is not null && !string.IsNullOrWhiteSpace(request.MapId)
            && request.MapId.Length <= 1024 && !request.MapId.Any(char.IsControl), "Invalid map identity");
        return new(Status, null);
    }
}
