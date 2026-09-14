internal sealed class NonSeekWriteStream(Stream destination) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) => destination.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => destination.Write(buffer);
    public override void Flush() => destination.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
