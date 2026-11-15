using System.IO;

namespace Client.Windows.Services;

/// <summary>
/// Byte counter for a send or a download. Worker threads add bytes through <see cref="ProgressReadStream"/>;
/// the UI reads <see cref="Permille"/> and <see cref="Percent"/> from a timer, so large files show steady progress.
/// </summary>
internal sealed class TransferProgress
{
    private readonly long _total;
    private long _completed;

    public TransferProgress(long totalBytes) => _total = Math.Max(0, totalBytes);

    public long Total => _total;
    public long Completed => Math.Min(Interlocked.Read(ref _completed), _total);

    public int Permille => _total == 0 ? 1000 : (int)(Completed * 1000 / _total);
    public int Percent => _total == 0 ? 100 : (int)(Completed * 100 / _total);

    public void Add(long bytes)
    {
        if (bytes > 0) Interlocked.Add(ref _completed, bytes);
    }

    /// <summary>Exact size of a file encrypted by FileContentEncryptionService (format ZKTF v1).</summary>
    public static long CiphertextLength(long plaintextLength)
    {
        const long chunkSize = 1024 * 1024;
        const long chunkOverhead = 12 + 4 + 12 + 16; // chunk header, length, AES-GCM nonce and tag
        long chunks = (plaintextLength + chunkSize - 1) / chunkSize;
        return plaintextLength + chunks * chunkOverhead;
    }
}

/// <summary>Read-only pass-through stream that reports every byte read to one or more <see cref="TransferProgress"/>.</summary>
internal sealed class ProgressReadStream : Stream
{
    private readonly Stream _inner;
    private readonly TransferProgress[] _progress;

    public ProgressReadStream(Stream inner, params TransferProgress[] progress)
    {
        _inner = inner;
        _progress = progress;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Report(_inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Report(_inner.Read(buffer));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Report(await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Report(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    private int Report(int read)
    {
        foreach (TransferProgress progress in _progress) progress.Add(read);
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // The caller owns the inner stream and disposes it.
}
