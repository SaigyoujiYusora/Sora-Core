using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sora.Core;

public static class DatabaseFile
{
    public const int MaxPayloadBytes = 256 * 1024 * 1024;
    public const uint CurrentVersion = 2;
    private static ReadOnlySpan<byte> Magic => "SREDB\r\n\x1a"u8;

    public static DatabaseDocument Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Read(stream);
    }

    public static DatabaseDocument Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[52];
        stream.ReadExactly(header);
        Validation.Require(header[..8].SequenceEqual(Magic), "Not a Sora Endfield Database; RCM6 is unsupported");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        Validation.Require(version is 1 or CurrentVersion, "Unsupported database version");
        ulong length = BinaryPrimitives.ReadUInt64LittleEndian(header[12..]);
        Validation.Require(length <= MaxPayloadBytes, "Database exceeds payload limit");
        if (stream.CanSeek) Validation.Require((ulong)(stream.Length - stream.Position) == length, "Database length mismatch");
        var payload = new byte[(int)length]; stream.ReadExactly(payload);
        Validation.Require(stream.ReadByte() == -1, "Trailing database bytes");
        Validation.Require(CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), header[20..]), "Database checksum mismatch");
        return ParsePayload(payload, version);
    }

    public static DatabaseDocument ParsePayload(byte[] payload, uint version = CurrentVersion)
    {
        Validation.Require(version is 1 or CurrentVersion, "Unsupported database version");
        Validation.Require(payload.Length <= MaxPayloadBytes, "Document exceeds payload limit");
        using var json = WireJson.Parse(payload);
        Validation.Require(version != 1 || json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("resourceIndex", out _), "Resource index requires database version 2");
        Validation.Require(version != 1 || !HasNativeClipMetadata(json.RootElement), "Native clip metadata requires database version 2");
        var database = json.Deserialize<DatabaseDocument>(WireJson.Options) ?? throw new InvalidDataException("Null database");
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

    public static void WriteAtomic(string path, DatabaseDocument database)
    {
        path = Path.GetFullPath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { Write(stream, database); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
