using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sora.Core;

public sealed record DatabaseStorageInfo(uint FormatVersion, long PayloadBytes, long MaxPayloadBytes);
public sealed record ValidatedDatabase(DatabaseDocument Database, DatabaseStorageInfo Storage);

public static class DatabaseFile
{
    public const int MaxPayloadBytes = 256 * 1024 * 1024;
    public const uint CurrentVersion = 3;
    private static ReadOnlySpan<byte> Magic => "SREDB\r\n\x1a"u8;

    public static DatabaseDocument Read(string path) => ReadValidated(path).Database;

    public static ValidatedDatabase ReadValidated(string path)
    {
        OperationProgress.Report("read-database");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadValidated(stream);
    }

    public static DatabaseDocument Read(Stream stream) => ReadValidated(stream).Database;

    public static ValidatedDatabase ReadValidated(Stream stream)
    {
        Span<byte> header = stackalloc byte[52];
        stream.ReadExactly(header);
        Validation.Require(header[..8].SequenceEqual(Magic), "Not a Sora Endfield Database; RCM6 is unsupported");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        Validation.Require(version is 1 or 2 or CurrentVersion, "Unsupported database version");
        ulong length = BinaryPrimitives.ReadUInt64LittleEndian(header[12..]);
        Validation.Require(length <= MaxPayloadBytes, "Database exceeds payload limit");
        if (stream.CanSeek) Validation.Require((ulong)(stream.Length - stream.Position) == length, "Database length mismatch");
        var payload = new byte[(int)length];
        using var checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int offset = 0; offset < payload.Length;)
        {
            OperationProgress.Report("read-database-payload", offset, payload.Length);
            var chunk = payload.AsSpan(offset, Math.Min(65536, payload.Length - offset));
            stream.ReadExactly(chunk); checksum.AppendData(chunk); offset += chunk.Length;
        }
        OperationProgress.Report("read-database-payload", payload.Length, payload.Length);
        Validation.Require(stream.ReadByte() == -1, "Trailing database bytes");
        Validation.Require(CryptographicOperations.FixedTimeEquals(checksum.GetHashAndReset(), header[20..]), "Database checksum mismatch");
        var database = ParsePayload(payload, version);
        return new(database, new(version, (long)length, MaxPayloadBytes));
    }

    public static DatabaseDocument ParsePayload(byte[] payload, uint version = CurrentVersion)
    {
        Validation.Require(version is 1 or 2 or CurrentVersion, "Unsupported database version");
        Validation.Require(payload.Length <= MaxPayloadBytes, "Document exceeds payload limit");
        OperationProgress.Report("parse-database-payload", detail: $"Parsing {payload.Length}-byte JSON payload");
        using var json = WireJson.Parse(payload);
        Validation.Require(version != 1 || json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("resourceIndex", out _), "Resource index requires database version 2");
        Validation.Require(version != 1 || !HasNativeClipMetadata(json.RootElement), "Native clip metadata requires database version 2");
        Validation.Require(version >= 3 || !json.RootElement.TryGetProperty("catalogSource", out var source) || source.ValueKind == JsonValueKind.Null, "Unified catalog requires database version 3");
        OperationProgress.Report("deserialize-database", detail: "Materializing database document");
        var database = json.Deserialize<DatabaseDocument>(WireJson.Options) ?? throw new InvalidDataException("Null database");
        Validation.Require(version >= 3 || database.Assets.All(asset => asset is null || asset.Locator is null && asset.Metadata is null), "Asset locators require database version 3");
        Validation.Require(version >= 3 || database.Assets.All(asset => asset?.Scene is null || asset.Scene.Nodes is null && asset.Scene.ImportDiagnostics is null && asset.Scene.Meshes.All(mesh => mesh is null || mesh.Node is null && mesh.CoordinateSpace is null)), "Scene node contract requires database version 3");
        Validation.Database(database);
        return database;
    }

    private static bool HasNativeClipMetadata(JsonElement root)
    {
        if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("assets",out var assets)||assets.ValueKind!=JsonValueKind.Array)return false;
        foreach(var asset in assets.EnumerateArray())
            if(asset.ValueKind==JsonValueKind.Object&&asset.TryGetProperty("scene",out var scene)&&scene.ValueKind==JsonValueKind.Object&&scene.TryGetProperty("clips",out var clips)&&clips.ValueKind==JsonValueKind.Array)
                foreach(var clip in clips.EnumerateArray())if(clip.ValueKind==JsonValueKind.Object&&clip.TryGetProperty("native",out _))return true;
        return false;
    }

    public static void Write(Stream stream, DatabaseDocument database)
    {
        Validation.Database(database);
        var payload = JsonSerializer.SerializeToUtf8Bytes(database, WireJson.Options);
        Validation.Require(payload.Length <= MaxPayloadBytes, "Database exceeds payload limit");
        Span<byte> header = stackalloc byte[52]; Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], CurrentVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(header[12..], (ulong)payload.Length);
        SHA256.HashData(payload).CopyTo(header[20..]);
        stream.Write(header); stream.Write(payload);
    }

    public static void WriteAtomic(string path, DatabaseDocument database) => WriteAtomicValidated(path, database);

    public static DatabaseStorageInfo WriteAtomicValidated(string path, DatabaseDocument database)
    {
        Validation.Database(database);
        path = Path.GetFullPath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                Span<byte> header = stackalloc byte[52]; header.Clear();
                stream.Write(header);
                using var payload = new DatabasePayloadStream(stream);
                OperationProgress.Report("write-database-payload", 0, detail: "Payload bytes written; total unknown");
                JsonSerializer.Serialize(payload, database, WireJson.Options);
                OperationProgress.Report("write-database-payload", payload.Written, detail: "Payload bytes written; serialization complete");
                Magic.CopyTo(header);
                BinaryPrimitives.WriteUInt32LittleEndian(header[8..], CurrentVersion);
                BinaryPrimitives.WriteUInt64LittleEndian(header[12..], (ulong)payload.Written);
                payload.FinishHash().CopyTo(header[20..]);
                stream.Position = 0; stream.Write(header); stream.Flush(true);
            }
            OperationProgress.Report("verify-database");
            var verified = ReadValidated(temporary);
            OperationProgress.Report("commit-database");
            OperationProgress.CommitAtomic(() => File.Move(temporary, path, true));
            return verified.Storage;
        }
        finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
    }
}
