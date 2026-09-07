using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sora.Core;

public static class DatabaseFile
{
    public const int MaxPayloadBytes = 256 * 1024 * 1024;
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
        Validation.Require(BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) == 1, "Unsupported database version");
        ulong length = BinaryPrimitives.ReadUInt64LittleEndian(header[12..]);
        Validation.Require(length <= MaxPayloadBytes, "Database exceeds payload limit");
        if (stream.CanSeek) Validation.Require((ulong)(stream.Length - stream.Position) == length, "Database length mismatch");
        var payload = new byte[(int)length]; stream.ReadExactly(payload);
        Validation.Require(stream.ReadByte() == -1, "Trailing database bytes");
        Validation.Require(CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), header[20..]), "Database checksum mismatch");
        return ParsePayload(payload);
    }

    public static DatabaseDocument ParsePayload(byte[] payload)
    {
        Validation.Require(payload.Length <= MaxPayloadBytes, "Document exceeds payload limit");
        using var json = WireJson.Parse(payload);
        var database = json.Deserialize<DatabaseDocument>(WireJson.Options) ?? throw new InvalidDataException("Null database");
        Validation.Database(database);
        return database;
    }

    public static void Write(Stream stream, DatabaseDocument database)
    {
        Validation.Database(database);
        var payload = JsonSerializer.SerializeToUtf8Bytes(database, WireJson.Options);
        Validation.Require(payload.Length <= MaxPayloadBytes, "Database exceeds payload limit");
        Span<byte> header = stackalloc byte[52]; Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], 1);
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
