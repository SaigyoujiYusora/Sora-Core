using System.Security.Cryptography;

namespace Sora.Core;

internal sealed class DatabasePayloadStream(Stream destination) : Stream
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    public long Written { get; private set; }
    private long reported;
    public byte[] FinishHash() => hash.GetHashAndReset();
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var chunk = buffer[..Math.Min(buffer.Length, 65536)];
            Validation.Require(Written + chunk.Length <= DatabaseFile.MaxPayloadBytes, "Database exceeds payload limit");
            if (Written - reported >= 65536)
            {
                OperationProgress.Report("write-database-payload", Written, detail: "Payload bytes written; total unknown");
                reported = Written;
            }
            destination.Write(chunk);
            hash.AppendData(chunk);
            Written += chunk.Length;
            buffer = buffer[chunk.Length..];
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) hash.Dispose(); base.Dispose(disposing); }
    public override void Flush() => destination.Flush();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Written;
    public override long Position { get => Written; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
