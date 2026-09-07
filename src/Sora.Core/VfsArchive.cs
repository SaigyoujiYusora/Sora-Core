namespace Sora.Core;

public sealed record VfsEntry(string Name, long Offset, long Size);

public sealed class VfsArchive : IDisposable
{
    private readonly Stream stream;
    private readonly List<(long Disk, long Decoded, VfsBlock Block)> blocks = [];
    public IReadOnlyList<VfsEntry> Entries { get; }

    public VfsArchive(string path) : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
    public VfsArchive(byte[] bytes) : this(new MemoryStream(bytes, false)) { }

    private VfsArchive(Stream input)
    {
        stream = input;
        try
        {
            byte[] head = Read(0, 48);
            Validation.Require(VfsFields.IsValidHeader(head), "Not a supported Endfield VFS archive");
            var header = VfsFields.ReadHeader(head);
            Validation.Require(header.CompressedBlocksInfoSize <= 16 * 1024 * 1024 && header.UncompressedBlocksInfoSize <= 16 * 1024 * 1024, "Archive metadata exceeds limit");
            bool atEnd = (header.Flags & 0x80) != 0;
            long infoPosition = atEnd ? stream.Length - header.CompressedBlocksInfoSize : (long)header.DataOffset;
            byte[] stored = Read(infoPosition, (int)header.CompressedBlocksInfoSize);
            byte[] info = DecodeMetadata(stored, (int)header.UncompressedBlocksInfoSize, (header.Flags & 0x3f) != 0);
            Validation.Require(info.Length >= 8, "Truncated VFS metadata");
            int count = VfsFields.Count(info, 0, false);
            Validation.Require(count >= 0 && count <= (info.Length - 8) / 10, "Invalid archive block count");
            int cursor = 4;
            long disk = (long)header.DataOffset;
            if (!atEnd) disk += (header.Flags & 0x200) != 0 ? ((long)header.CompressedBlocksInfoSize + 15) & ~15L : header.CompressedBlocksInfoSize;
            long decoded = 0;
            for (int i = 0; i < count; i++, cursor += 10)
            {
                var block = VfsFields.Block(info, cursor);
                Validation.Require(block.CompressedSize <= 64 * 1024 * 1024 && block.UncompressedSize <= 64 * 1024 * 1024, "Archive block exceeds limit");
                Validation.Require(block.CompressionType is 0 or 5, "Unsupported VFS compression mode");
                Validation.Require(block.CompressionType != 0 || block.CompressedSize == block.UncompressedSize, "Raw block sizes disagree");
                Validation.Require(disk >= 0 && disk <= (atEnd ? infoPosition : stream.Length) - block.CompressedSize, "Archive block exceeds data section");
                blocks.Add((disk, decoded, block));
                disk += block.CompressedSize; decoded = checked(decoded + block.UncompressedSize);
            }
            int entries = VfsFields.Count(info, cursor, true); cursor += 4;
            Validation.Require(entries >= 0 && entries <= (info.Length - cursor) / 21, "Invalid VFS entry count");
            var names = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<VfsEntry>(entries);
            for (int i = 0; i < entries; i++)
            {
                Validation.Require(info.Length - cursor >= 21, "Truncated VFS entry");
                int terminator = Array.IndexOf(info, (byte)0, cursor + 16, Math.Min(65, info.Length - cursor - 16));
                Validation.Require(terminator >= cursor + 16 && terminator - cursor - 16 < 64 && terminator <= info.Length - 5, "Invalid VFS entry name");
                var node = VfsFields.Node(info, cursor, out cursor);
                Validation.Require(node.Offset >= 0 && node.Size >= 0 && node.Offset <= decoded - node.Size, "VFS entry exceeds decoded stream");
                Validation.Require(!string.IsNullOrWhiteSpace(node.Name) && names.Add(node.Name), "Duplicate or empty VFS entry name");
                result.Add(new(node.Name, node.Offset, node.Size));
            }
            Validation.Require(cursor == info.Length, "Trailing VFS metadata");
            Entries = result.AsReadOnly();
        }
        catch { stream.Dispose(); throw; }
    }

    public byte[] Extract(string name)
    {
        var entry = Entries.SingleOrDefault(x => x.Name == name) ?? throw new KeyNotFoundException("Archive entry not found");
        Validation.Require(entry.Size <= DatabaseFile.MaxPayloadBytes, "Archive entry exceeds extraction limit");
        byte[] result = new byte[(int)entry.Size];
        foreach (var item in blocks)
        {
            long begin = Math.Max(entry.Offset, item.Decoded);
            long end = Math.Min(entry.Offset + entry.Size, item.Decoded + item.Block.UncompressedSize);
            if (begin >= end) continue;
            int within = checked((int)(begin - item.Decoded));
            int length = checked((int)(end - begin));
            if (item.Block.CompressionType == 0)
                Read(item.Disk + within, length).CopyTo(result, checked((int)(begin - entry.Offset)));
            else
            {
                var compressed = Read(item.Disk, (int)item.Block.CompressedSize);
                compressed = VfsFields.Decrypt(compressed, true);
                var decoded = Lz4Block.Decode(compressed, (int)item.Block.UncompressedSize, true);
                decoded.AsSpan(within, length).CopyTo(result.AsSpan(checked((int)(begin - entry.Offset))));
            }
        }
        return result;
    }

    private byte[] Read(long at, int length)
    {
        Validation.Require(at >= 0 && length >= 0 && at <= stream.Length - length, "Archive read exceeds input");
        stream.Position = at;
        byte[] bytes = new byte[length]; stream.ReadExactly(bytes); return bytes;
    }

    private static byte[] DecodeMetadata(byte[] stored, int length, bool compressed)
    {
        if (!compressed) { Validation.Require(stored.Length == length, "Raw metadata length mismatch"); return stored; }
        for (int mode = 0; mode < 3; mode++)
        {
            byte[] candidate = (byte[])stored.Clone();
            if (mode == 0) candidate = VfsFields.Decrypt(candidate, true);
            if (mode == 2) candidate = VfsFields.Decrypt(candidate, false);
            try { return Lz4Block.Decode(candidate, length); }
            catch (InvalidDataException) { }
        }
        throw new InvalidDataException("No supported VFS metadata decoding matched");
    }

    public void Dispose() => stream.Dispose();
}
