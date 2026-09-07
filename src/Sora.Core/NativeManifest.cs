using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Sora.Core;

public sealed record BundleResource(int Index, string Name, int Category, int[] Dependencies);
public sealed record AddressResource(long Hash, string Path, int Bundle, int Size);
public sealed record NativeManifest(string Version, string Hash, string Revision, uint Trailer, BundleResource[] Bundles, AddressResource[] Assets)
{
    public DatabaseDocument ToDatabase()
    {
        var records = new List<AssetRecord>();
        foreach (var bundle in Bundles)
            records.Add(new("bundle:" + bundle.Index, bundle.Name, "Category " + bundle.Category, "bundle", bundle.Dependencies.Select(x => "bundle:" + x).ToArray()));
        for (int ordinal = 0; ordinal < Assets.Length; ordinal++)
        {
            var asset = Assets[ordinal];
            records.Add(new("asset:" + asset.Hash.ToString("x16") + ":" + ordinal, asset.Path, asset.Size + " bytes", "resource", ["bundle:" + asset.Bundle]));
        }
        var database = new DatabaseDocument(Version, records.ToArray());
        Validation.Database(database);
        return database;
    }

    public static NativeManifest Read(Stream compressed)
    {
        var input = new Region(Inflate(compressed, DatabaseFile.MaxPayloadBytes));
        int cursor = 0;
        Validation.Require(input.U32(ref cursor) == 0xff11ff11, "Unknown manifest signature");
        var version = input.Utf16(ref cursor, true);
        Validation.Require(input.U32(ref cursor) == 0xf1f2f3f4, "Unknown manifest section marker");
        var hash = input.Utf16(ref cursor, true);
        var revision = input.Utf16(ref cursor, true);
        var addresses = input.Section(ref cursor);
        _ = input.Section(ref cursor);
        var bundleTable = input.Section(ref cursor);
        var values = input.Section(ref cursor);
        Validation.Require(input.Length - cursor == 4, "Manifest trailer must contain four bytes");
        uint trailer = (uint)input.I32(cursor);
        int bundleCursor = 0;
        int bundleCount = bundleTable.Count(ref bundleCursor, 48);
        Validation.Require(bundleTable.Length == 4L + 48L * bundleCount, "Invalid manifest bundle record size");
        var bundles = new BundleResource?[bundleCount];
        for (int row = 0; row < bundleCount; row++)
        {
            int start = 4 + row * 48;
            int index = bundleTable.I32(start);
            Validation.Require(index >= 0 && index < bundleCount && bundles[index] is null, "Duplicate or invalid bundle index");
            int nameAt = bundleTable.I32(start + 4);
            int depsAt = bundleTable.I32(start + 8);
            var name = values.Utf16(ref nameAt, false).Replace('\\', '/');
            int count = values.Count(ref depsAt, 4);
            var dependencies = new int[count];
            for (int i = 0; i < count; i++)
            {
                dependencies[i] = values.I32(depsAt + i * 4);
                Validation.Require(dependencies[i] >= 0 && dependencies[i] < bundleCount, "Invalid bundle dependency");
            }
            bundles[index] = new(index, name, bundleTable.I32(start + 40), dependencies.Distinct().ToArray());
        }
        int addressCursor = 0;
        int capacity = addresses.Count(ref addressCursor, 8);
        addressCursor += capacity * 8;
        Validation.Require((addresses.Length - addressCursor) % 24 == 0, "Invalid address record width");
        var assets = new List<AddressResource>();
        while (addressCursor < addresses.Length)
        {
            long identity = addresses.I64(addressCursor);
            int pathAt = addresses.I32(addressCursor + 8);
            int bundle = addresses.I32(addressCursor + 12);
            int size = addresses.I32(addressCursor + 16);
            Validation.Require(bundle >= 0 && bundle < bundleCount && size >= 0, "Invalid address resource");
            var pathBytes = values.Section(ref pathAt);
            using var pathStream = new MemoryStream(pathBytes.Bytes, false);
            var path = DecodeText(Inflate(pathStream, 32768)).Replace('\\', '/');
            assets.Add(new(identity, path, bundle, size));
            addressCursor += 24;
        }
        return new(version, hash, revision, trailer, bundles.Select(x => x!).ToArray(), assets.ToArray());
    }

    private static string DecodeText(byte[] bytes)
    {
        Validation.Require(bytes.Length % 2 == 0, "Odd UTF-16 string length");
        return new UnicodeEncoding(false, false, true).GetString(bytes);
    }


    private static byte[] Inflate(Stream compressed, int maximum)
    {
        using var brotli = new BrotliStream(compressed, CompressionMode.Decompress, true);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int length;
        while ((length = brotli.Read(buffer)) > 0)
        {
            Validation.Require(output.Length + length <= maximum, "Manifest decompression exceeds limit");
            output.Write(buffer, 0, length);
        }
        return output.ToArray();
    }

    private sealed class Region(byte[] data)
    {
        public byte[] Bytes => data;
        public int Length => data.Length;
        private ReadOnlySpan<byte> Slice(int at, int length)
        {
            Validation.Require(at >= 0 && length >= 0 && at <= data.Length - length, "Manifest offset exceeds section");
            return data.AsSpan(at, length);
        }
        public int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(Slice(at, 4));
        public long I64(int at) => BinaryPrimitives.ReadInt64LittleEndian(Slice(at, 8));
        public uint U32(ref int at) { var result = (uint)I32(at); at += 4; return result; }
        public int Count(ref int at, int width)
        {
            int count = I32(at); at += 4;
            Validation.Require(count >= 0 && count <= (data.Length - at) / width, "Invalid manifest count");
            return count;
        }
        public Region Section(ref int at)
        {
            int size = I32(at); at += 4;
            var result = new Region(Slice(at, size).ToArray()); at += size; return result;
        }
        public string Utf16(ref int at, bool characters)
        {
            int size = I32(at); at += 4;
            Validation.Require(size >= 0 && size <= 32768, "Manifest text exceeds limit");
            if (characters) size *= 2;
            var result = DecodeText(Slice(at, size).ToArray()); at += size; return result;
        }
    }
}
