using System.IO;

namespace DropScan.Sharing;

// ZipArchive in .NET 8 writes headers/central directory synchronously. Adapt those small
// writes to async response I/O; never enable synchronous Kestrel I/O or buffer the archive.
internal sealed class ZipResponseStream(Stream output, CancellationToken cancellation) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => output.FlushAsync(cancellation).GetAwaiter().GetResult();
    public override Task FlushAsync(CancellationToken token) => output.FlushAsync(cancellation);
    public override void Write(byte[] buffer, int offset, int count) => output.WriteAsync(buffer.AsMemory(offset, count), cancellation).AsTask().GetAwaiter().GetResult();
    public override void Write(ReadOnlySpan<byte> buffer) { var bytes = buffer.ToArray(); Write(bytes, 0, bytes.Length); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => output.WriteAsync(buffer.AsMemory(offset, count), cancellation).AsTask();
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => output.WriteAsync(buffer, cancellation);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
